using System.Globalization;
using System.Text.Json;
using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Model.Reports;
using Bytex.Live.Network;
using Microsoft.Extensions.Logging;

namespace Bytex.Adapters.Hyperliquid;

/// <summary>
/// Order routing and execution reporting for this venue's perpetuals.
/// <para>
/// Four things here are this venue's own, and none of them could be hidden by following the other adapters.
/// </para>
/// <para>
/// A write is a SIGNATURE, not an authenticated request. Every action is MessagePack-encoded, hashed with the nonce,
/// wrapped in EIP-712 typed data and signed with a secp256k1 key; the venue recovers the signer from the signature
/// and never sees a key. So a refusal can mean "this digest is not the one I built" rather than anything about
/// permissions, which is why <see cref="HyperliquidSigner"/> exists and why it is verified against published vectors.
/// </para>
/// <para>
/// The venue has NO MARKET ORDER. Measured: an order whose type is <c>{"market":{}}</c> is refused at HTTP 422
/// before the signature is looked at - the only two types it deserialises are a limit and a trigger. So a market
/// order is an immediate-or-cancel limit priced through the book, which means this client has to know a price before
/// it can send one and has to choose how far to reach. <see cref="HyperliquidVenue.MarketOrderSlippage"/> is that
/// choice, named because it is a choice.
/// </para>
/// <para>
/// A client order id is a 128-BIT NUMBER here and a string of the caller's choosing everywhere else, so the engine's
/// id cannot travel. What travels is the first sixteen bytes of its SHA-256, and an id coming back is resolved by
/// recomputing that for the orders this node holds - which is what makes it survive a restart, where a table would
/// not.
/// </para>
/// <para>
/// And reading costs nothing. Positions, resting orders and fills are PUBLIC reads keyed by an address - measured
/// against live accounts with no credential at all - and so are the order and fill streams on the websocket. Only
/// the writes need the key. That is the reverse of every other venue here, where a report is the thing a key is for.
/// </para>
/// </summary>
public sealed class HyperliquidExecutionClient : ExecutionClientBase
{
    private readonly HyperliquidExecutionClientConfig _config;
    private readonly HyperliquidHttp _http;
    private readonly HyperliquidInstrumentProvider _instruments;
    private readonly HyperliquidCredentials _credentials;
    private readonly Dictionary<string, ClientOrderId> _byCloid = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ClientOrderId, VenueOrderId> _venueIds = new();
    private readonly HashSet<string> _seenTrades = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private WebSocketClient? _ws;

    public HyperliquidExecutionClient(ClientId clientId, HyperliquidExecutionClientConfig config, KernelServices services)
        : base(
            clientId,
            HyperliquidVenue.Venue,
            new AccountId($"{HyperliquidVenue.Venue}-PERP"),
            AccountType.Margin,

            // The collateral is one token for the whole family, so the account has a base currency where a venue
            // with several settlement currencies cannot have one.
            Currency.FromCode(HyperliquidVenue.QuoteCurrency, HyperliquidVenue.QuoteCurrencyPrecision),

            // Netting, and not a choice: this venue has no hedge mode. One position per coin, reported as "oneWay"
            // on every live account read, so a long and a short in the same asset cannot both exist.
            OmsType.Netting,
            services)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));

        // INTEGER ONLY, measured rather than assumed: an updateLeverage carrying 5.5 is refused at HTTP 422 with
        // "Failed to deserialize the JSON body into the target type", before the signature is examined - the field
        // is a whole number in the venue's own type. The shared configuration carries a decimal because a strategy
        // document does, so the refusal belongs here, when the client is built, rather than on the first order.
        if (_config.Leverage is { } leverage && leverage != decimal.Truncate(leverage))
        {
            throw new ArgumentException(
                $"Hyperliquid takes a whole-number leverage and this client is configured for {leverage}. Sending a "
                + "fraction is refused by the venue before it reads the signature, so it would fail on connecting "
                + "rather than quietly trade at something else.",
                nameof(config));
        }

        _http = new HyperliquidHttp(config, Log, requireCredentials: true);
        _credentials = _http.Credentials!;
        _instruments = new HyperliquidInstrumentProvider(_http, config.InstrumentProvider, Log);
    }

    public HyperliquidInstrumentProvider Instruments => _instruments;

    // ----- connection -----

    public override async Task ConnectAsync(CancellationToken ct)
    {
        await _instruments.InitializeAsync(ct).ConfigureAwait(false);
        foreach (Instrument instrument in _instruments.GetAll())
        {
            if (Services.Cache.Instrument(instrument.Id) is null)
            {
                (Services.Cache as Core.Caching.Cache)?.AddInstrument(instrument);
            }
        }

        // Said once, on connecting, because it is otherwise invisible and fatal. An API wallet signs for an account
        // and has an address of its own, so with one configured these two differ - and every read below is keyed by
        // the ACCOUNT. Configure the key without the account and the account becomes the signer's own address,
        // which has no positions, no orders and no balance, and nothing fails.
        if (_credentials.Signer is { } signer && !signer.Equals(_credentials.Account, StringComparison.OrdinalIgnoreCase))
        {
            Log.LogInformation(
                "Hyperliquid: signing as {Signer} on behalf of the account {Account}. An API wallet can trade and "
                + "cannot withdraw.",
                signer,
                _credentials.Account);
        }

        await PublishAccountStateAsync(ct).ConfigureAwait(false);
        await ApplyLeverageAsync(ct).ConfigureAwait(false);

        _ws = new WebSocketClient(new WebSocketClientConfig
        {
            Url = new Uri(HyperliquidVenue.WsBase(_config) + HyperliquidVenue.WsPath),
            PingMessage = HyperliquidVenue.PingMessage,
            PingInterval = HyperliquidVenue.PingInterval,
        }, Log)
        {
            OnText = HandleMessageAsync,
            OnConnected = isReconnect =>
            {
                // No handshake and no signature: the order and fill streams are subscribed to by ADDRESS, exactly
                // as the public channels are. Measured - an arbitrary address's orderUpdates subscription was
                // acknowledged over a socket that had authenticated nothing.
                foreach (string subscription in HyperliquidChannels.PrivateSubscriptions(_credentials.Account))
                {
                    _ws?.SendText(subscription);
                }

                if (isReconnect)
                {
                    NotifyConnected();
                }

                return Task.CompletedTask;
            },
            OnDisconnected = reason =>
            {
                NotifyDisconnected(reason);
                return Task.CompletedTask;
            },
        };

        await _ws.ConnectAsync(ct).ConfigureAwait(false);
        NotifyConnected();
    }

    public override async Task DisconnectAsync(CancellationToken ct)
    {
        if (_ws is not null)
        {
            await _ws.DisposeAsync().ConfigureAwait(false);
            _ws = null;
        }

        NotifyDisconnected("disconnect requested");
    }

    protected override void OnDispose()
    {
        _ws?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _http.Dispose();
    }

    // ----- helpers -----

    private Instrument? Find(InstrumentId id) => _instruments.Find(id) ?? Services.Cache.Instrument(id);

    /// <summary>
    /// The instrument a coin names, or null when this adapter does not carry it. Null is a real answer and a
    /// frequent one: an account's fills and orders mix this family's coins with spot pairs, which arrive as
    /// <c>@142</c>, and with the assets of the builder-deployed exchanges, which arrive as <c>xyz:CL</c>. Measured
    /// on live accounts. This family is the main perpetuals and nothing else, so those are skipped rather than
    /// guessed at.
    /// </summary>
    private Instrument? FindByCoin(string coin) =>
        coin.Length == 0 || coin.Contains(':', StringComparison.Ordinal) || coin.StartsWith('@')
            ? null
            : Find(HyperliquidVenue.ToInstrumentId(coin));

    /// <summary>
    /// The engine's order id behind one of this venue's, which has to be worked backwards because the venue's is a
    /// hash of it. What this node placed in this process is remembered; anything else - an order placed before a
    /// restart, or by something else on the same account - is found by recomputing the hash for the orders the cache
    /// holds. Both, because the cache is the only thing that survives a restart and the map is the only thing that
    /// covers an order not yet in it.
    /// </summary>
    private ClientOrderId? Resolve(string cloid)
    {
        if (cloid.Length == 0)
        {
            return null;
        }

        lock (_gate)
        {
            if (_byCloid.TryGetValue(cloid, out ClientOrderId known))
            {
                return known;
            }
        }

        foreach (Order order in Services.Cache.OrdersOpen(Venue))
        {
            if (HyperliquidVenue.CloidFor(order.ClientOrderId).Equals(cloid, StringComparison.OrdinalIgnoreCase))
            {
                lock (_gate)
                {
                    _byCloid[cloid] = order.ClientOrderId;
                }

                return order.ClientOrderId;
            }
        }

        return null;
    }

    // ----- the account -----

    /// <summary>
    /// What the account holds, from a read that needs no credential. The venue reports one collateral balance and
    /// the margin held against it, so there is a margin balance to publish where a spot account would have none.
    /// </summary>
    private async Task PublishAccountStateAsync(CancellationToken ct)
    {
        try
        {
            JsonElement state = await _http.InfoAsync(
                HyperliquidReads.ClearinghouseState,
                new Dictionary<string, object>(StringComparer.Ordinal) { [HyperliquidReads.User] = _credentials.Account },
                ct).ConfigureAwait(false);

            if (!state.TryGetProperty(MarginSummary, out JsonElement summary))
            {
                return;
            }

            Currency currency = Currency.FromCode(HyperliquidVenue.QuoteCurrency, HyperliquidVenue.QuoteCurrencyPrecision);
            decimal equity = summary.Dec(AccountValue);
            decimal used = summary.Dec(TotalMarginUsed);
            decimal maintenance = state.Dec(CrossMaintenanceMarginUsed);

            // The margins go in the info rather than in the margin balances, and that is a shape mismatch rather
            // than a shortcut. A MarginBalance is per INSTRUMENT, and this venue's cross margin is one pool for the
            // whole account: the collateral behind a BTC position and an ETH position is the same collateral, and
            // splitting it across instruments would either double-count it or invent a division the venue does not
            // make. So the account carries one balance, and the two numbers the venue really publishes are recorded
            // where they can be read without being misread as per-instrument.
            GenerateAccountState(
                [AccountBalance.Of(new Money(equity, currency), new Money(Math.Max(0m, used), currency))],
                [],
                reported: true,
                state.Has(Time) ? state.Ms(Time) : Clock.Timestamp,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [TotalMarginUsed] = Json.Trim(used),
                    [CrossMaintenanceMarginUsed] = Json.Trim(maintenance),
                },
                defaultLeverage: _config.Leverage);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.LogError(e, "Failed to load the Hyperliquid account state for {Account}", _credentials.Account);
        }
    }

    /// <summary>
    /// The configured leverage, applied to every instrument this client knows about.
    /// <para>
    /// Per ASSET on this venue, not per account, and the same action chooses the margin mode - so one setting
    /// becomes one call per instrument and each of those calls also says whether the position shares the account's
    /// collateral. An asset the venue only allows on its own margin is skipped rather than refused for the whole
    /// connection: the venue publishes that per asset, and failing to connect over one of 234 would be worse than
    /// leaving it on the leverage it already had.
    /// </para>
    /// </summary>
    private async Task ApplyLeverageAsync(CancellationToken ct)
    {
        if (_config.Leverage is not { } configured)
        {
            return;
        }

        // Before anything is sent. A venue asked for more leverage than it grants does not refuse - it grants
        // what it will and trades on, so the strategy would run at a size it was never tested at.
        LeverageGuard.EnsureGranted(configured, _instruments.GetAll(), HyperliquidVenue.Venue.Value);

        int leverage = (int)configured;
        foreach (Instrument instrument in _instruments.GetAll())
        {
            ct.ThrowIfCancellationRequested();
            HyperliquidAsset asset = HyperliquidAsset.Of(instrument);
            if (leverage > asset.MaxLeverage)
            {
                Log.LogWarning(
                    "Hyperliquid allows at most {Max}x on {Instrument} and this client is configured for {Wanted}x, "
                    + "so its leverage is left alone",
                    asset.MaxLeverage,
                    instrument.Id,
                    leverage);
                continue;
            }

            try
            {
                await _http.ExchangeAsync(
                    HyperliquidActions.UpdateLeverage(asset.Index, _config.CrossMargin, leverage),
                    _http.NextNonce(),
                    ct).ConfigureAwait(false);
            }
            catch (HyperliquidApiException e)
            {
                Log.LogWarning("Hyperliquid refused {Leverage}x on {Instrument}: {Reason}", leverage, instrument.Id, e.Detail);
            }
        }
    }

    // ----- the streams -----

    private Task HandleMessageAsync(string text)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty(HyperliquidChannels.ChannelField, out JsonElement _))
            {
                return Task.CompletedTask;
            }

            string channel = root.Str(HyperliquidChannels.ChannelField);
            if (!root.TryGetProperty(HyperliquidChannels.DataField, out JsonElement data))
            {
                return Task.CompletedTask;
            }

            switch (channel)
            {
                case HyperliquidChannels.OrderUpdates:
                    if (data.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement update in data.EnumerateArray())
                        {
                            HandleOrderUpdate(update);
                        }
                    }

                    break;

                // The fill stream answers on "user" and NOT on "userEvents", which is what was subscribed to.
                // Measured: a userEvents subscription is acknowledged as userEvents and its messages arrive on
                // user. A client switching on the subscription's own name receives nothing and reports nothing.
                case HyperliquidChannels.UserEventsReply:
                    if (data.TryGetProperty(HyperliquidChannels.Fills, out JsonElement fills) && fills.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement fill in fills.EnumerateArray())
                        {
                            HandleFill(fill);
                        }
                    }

                    break;

                case HyperliquidChannels.Error:
                    Log.LogWarning("Hyperliquid private stream error: {Data}", data.GetRawText());
                    break;
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            Log.LogWarning(e, "Hyperliquid: unreadable private stream message");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// What became of an order. The message nests the order inside a status, so the status is beside the order
    /// rather than on it.
    /// <para>
    /// Only <c>open</c> was seen on the live stream while this was written, so the other statuses are mapped by what
    /// they say rather than from a list somebody measured: anything whose name carries "canceled" ends the order.
    /// The venue has several of those - a cancel for margin, for a reduce-only that no longer reduces, for a
    /// delisting - and treating an unrecognised one as nothing would leave the order open in this node for ever
    /// while the venue had closed it. The exact status is logged whenever it is not one of the four below, so the
    /// list can be corrected from a log rather than from a guess.
    /// </para>
    /// </summary>
    private void HandleOrderUpdate(JsonElement update)
    {
        if (!update.TryGetProperty(HyperliquidChannels.Order, out JsonElement o))
        {
            return;
        }

        Instrument? instrument = FindByCoin(o.Str(HyperliquidChannels.Coin));
        if (instrument is null)
        {
            return;
        }

        ClientOrderId? clientOrderId = Resolve(o.Str(HyperliquidChannels.Cloid));
        if (clientOrderId is not { } id)
        {
            // An order on this account that this node did not place. Nothing to report it against, and inventing a
            // client order id would create an order the cache has never seen.
            return;
        }

        Order? order = Services.Cache.Order(id);
        StrategyId strategyId = order?.StrategyId ?? new StrategyId("EXTERNAL");
        VenueOrderId venueOrderId = new(o.Str(HyperliquidChannels.Oid));
        lock (_gate)
        {
            _venueIds[id] = venueOrderId;
        }

        UnixNanos ts = update.Has(HyperliquidChannels.StatusTimestamp)
            ? update.Ms(HyperliquidChannels.StatusTimestamp)
            : Clock.Timestamp;

        string status = update.Str(HyperliquidChannels.Status);
        switch (status)
        {
            case HyperliquidChannels.StatusOpen:
                GenerateOrderAccepted(strategyId, instrument.Id, id, venueOrderId, ts);
                break;

            case HyperliquidChannels.StatusTriggered:
                GenerateOrderTriggered(strategyId, instrument.Id, id, venueOrderId, ts);
                break;

            case HyperliquidChannels.StatusRejected:
                GenerateOrderRejected(strategyId, instrument.Id, id, "Hyperliquid rejected the order", ts);
                break;

            // The fills carry this one; a filled status is the venue saying the order is done, which the fills
            // already said.
            case HyperliquidChannels.StatusFilled:
                break;

            default:
                if (status.Contains(HyperliquidChannels.CanceledMarker, StringComparison.OrdinalIgnoreCase))
                {
                    Log.LogInformation("Hyperliquid closed {ClientOrderId} with the status {Status}", id, status);
                    GenerateOrderCanceled(strategyId, instrument.Id, id, venueOrderId, ts);
                }
                else
                {
                    Log.LogWarning(
                        "Hyperliquid reported {ClientOrderId} as {Status}, which this adapter does not recognise, so "
                        + "the order is left as it was here",
                        id,
                        status);
                }

                break;
        }
    }

    /// <summary>
    /// One fill. Two of its fields are worth naming: <c>crossed</c> is whether this account was the taker, which is
    /// the liquidity side, and <c>fee</c> can be NEGATIVE - a maker rebate - which is a real number the venue pays
    /// and not a sign error.
    /// </summary>
    private void HandleFill(JsonElement f)
    {
        Instrument? instrument = FindByCoin(f.Str(HyperliquidChannels.Coin));
        if (instrument is null)
        {
            return;
        }

        string tradeId = f.Str(HyperliquidChannels.TradeId);
        if (tradeId.Length > 0)
        {
            lock (_gate)
            {
                if (!_seenTrades.Add(tradeId))
                {
                    return;
                }
            }
        }

        ClientOrderId? clientOrderId = Resolve(f.Str(HyperliquidChannels.Cloid));
        if (clientOrderId is not { } id)
        {
            return;
        }

        Order? order = Services.Cache.Order(id);
        Currency fee = f.Str(HyperliquidChannels.FeeToken) is { Length: > 0 } token
            ? Currency.FromCode(token, HyperliquidVenue.QuoteCurrencyPrecision)
            : instrument.QuoteCurrency;

        GenerateOrderFilled(
            order?.StrategyId ?? new StrategyId("EXTERNAL"),
            instrument.Id,
            id,
            new VenueOrderId(f.Str(HyperliquidChannels.Oid)),
            null,
            new TradeId(tradeId.Length > 0 ? tradeId : Guid.NewGuid().ToString("N")),

            // The side is the ACCOUNT's, spelled with the venue's one letter: B is a buy.
            f.Str(HyperliquidChannels.Side) == HyperliquidChannels.BuyAggressor ? OrderSide.Buy : OrderSide.Sell,
            order?.Type ?? OrderType.Limit,
            instrument.MakeQuantity(f.Dec(HyperliquidChannels.Size)),
            instrument.MakePrice(f.Dec(HyperliquidChannels.Price)),
            instrument.QuoteCurrency,
            new Money(f.Dec(HyperliquidChannels.Fee), fee),
            f.Bool(HyperliquidChannels.Crossed) ? LiquiditySide.Taker : LiquiditySide.Maker,
            f.Ms(HyperliquidChannels.Time));
    }

    // ----- commands -----

    public override async Task SubmitOrderAsync(SubmitOrder command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        Order order = command.Order;
        Instrument? instrument = Find(order.InstrumentId);
        if (instrument is null)
        {
            GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, $"instrument {order.InstrumentId} unknown to the Hyperliquid client", Clock.Timestamp);
            return;
        }

        if (order.IsQuoteQuantity)
        {
            GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, "Hyperliquid sizes an order in base units, so a quote quantity cannot be sent", Clock.Timestamp);
            return;
        }

        HyperliquidOrderWire? wire;
        try
        {
            wire = await WireAsync(order, instrument, order.Quantity, order.Price, order.TriggerPrice, ct).ConfigureAwait(false);
        }
        catch (NotSupportedException e)
        {
            GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, e.Message, Clock.Timestamp);
            return;
        }
        catch (HyperliquidApiException e)
        {
            // The book read a market order needs, which is a request of its own and can fail on its own.
            GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, e.Detail, Clock.Timestamp);
            return;
        }

        if (wire is null)
        {
            return;
        }

        lock (_gate)
        {
            _byCloid[wire.Cloid!] = order.ClientOrderId;
        }

        GenerateOrderSubmitted(order.StrategyId, order.InstrumentId, order.ClientOrderId, Clock.Timestamp);
        try
        {
            await _http.ExchangeAsync(HyperliquidActions.Order([wire]), _http.NextNonce(), ct).ConfigureAwait(false);
        }
        catch (HyperliquidApiException e)
        {
            GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, e.Detail, Clock.Timestamp);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, e.Message, Clock.Timestamp);
        }
    }

    /// <summary>
    /// One order as this venue takes it.
    /// <para>
    /// The price is where all the venue's peculiarities land. It has to be a STRING - a numeric price is refused at
    /// HTTP 422, measured - it has to satisfy both of the venue's rounding rules, and for a market order it has to
    /// be invented, because the venue has no market order at all.
    /// </para>
    /// </summary>
    private async Task<HyperliquidOrderWire?> WireAsync(Order order, Instrument instrument, Quantity quantity, Price? price, Price? trigger, CancellationToken ct)
    {
        HyperliquidAsset asset = HyperliquidAsset.Of(instrument);
        bool isTrigger = order.Type is OrderType.StopMarket or OrderType.StopLimit or OrderType.MarketIfTouched or OrderType.LimitIfTouched;
        if (order.Type is not (OrderType.Market or OrderType.Limit) && !isTrigger)
        {
            throw new NotSupportedException($"order type {order.Type} is not supported by Hyperliquid");
        }

        decimal limitPrice;
        string? timeInForce = null;
        if (order.Type == OrderType.Market)
        {
            // There is no market order here, so one is an immediate-or-cancel limit placed through the far side of
            // the book by a fixed margin. The book is read for it, which is one extra request on the path of a
            // market order and the price of the venue not having one.
            limitPrice = await MarketPriceAsync(asset, order.IsBuy, ct).ConfigureAwait(false);
            timeInForce = HyperliquidActions.Ioc;
        }
        else if (isTrigger && order.Type is OrderType.StopMarket or OrderType.MarketIfTouched)
        {
            // A trigger order that fires as a market order still carries a limit price, which the venue uses as the
            // worst fill it will take once triggered. The trigger price itself is the reference, reached through by
            // the same margin: there is no book to read for a price that will apply at some future moment.
            decimal reference = trigger?.Value ?? throw new NotSupportedException($"{order.Type} needs a trigger price");
            limitPrice = order.IsBuy
                ? reference * (1m + HyperliquidVenue.MarketOrderSlippage)
                : reference * (1m - HyperliquidVenue.MarketOrderSlippage);
        }
        else
        {
            limitPrice = price?.Value ?? throw new NotSupportedException($"{order.Type} needs a limit price");
            timeInForce = order.TimeInForce switch
            {
                TimeInForce.Ioc => HyperliquidActions.Ioc,

                // Post-only is its own time in force here rather than a flag beside one, and the venue refuses a
                // post-only order that would cross instead of repricing it.
                _ when order.IsPostOnly => HyperliquidActions.Alo,
                _ => HyperliquidActions.Gtc,
            };
        }

        decimal rounded = asset.RoundPrice(limitPrice);
        if (rounded != limitPrice)
        {
            // Said out loud, because it is the one place this venue's price rule can change an order without anybody
            // asking. The instrument publishes the decimal-places rule and the venue also caps a price at five
            // significant figures, which no instrument increment can express, so a price the engine considered valid
            // can still move here.
            Log.LogInformation(
                "Hyperliquid rounded {ClientOrderId} from {Wanted} to {Sent} on {Instrument}: this venue caps a "
                + "price at {Figures} significant figures as well as at {Decimals} decimal places",
                order.ClientOrderId,
                Json.Trim(limitPrice),
                Json.Trim(rounded),
                instrument.Id,
                HyperliquidVenue.MaxPriceSignificantFigures,
                asset.PriceDecimals);
        }

        return new HyperliquidOrderWire
        {
            Asset = asset.Index,
            IsBuy = order.IsBuy,
            Price = Json.Trim(rounded),
            Size = Json.Trim(quantity.Value),
            ReduceOnly = order.IsReduceOnly,
            TimeInForce = timeInForce,
            TriggerPrice = isTrigger && trigger is { } t ? Json.Trim(asset.RoundPrice(t.Value)) : null,
            TriggerIsMarket = order.Type is OrderType.StopMarket or OrderType.MarketIfTouched,

            // A stop-loss waits for the price to move AGAINST the position and a take-profit for it to move in
            // favour, and the venue has to be told which because that is what decides the side the trigger is
            // crossed from. A stop order is a loss stop; an if-touched order is an entry, which this venue's two
            // names make a take-profit.
            TriggerIsTakeProfit = order.Type is OrderType.MarketIfTouched or OrderType.LimitIfTouched,
            Cloid = HyperliquidVenue.CloidFor(order.ClientOrderId),
        };
    }

    /// <summary>
    /// A price that will cross the book now, for the immediate-or-cancel limit that stands in for a market order.
    /// The far touch, reached through by <see cref="HyperliquidVenue.MarketOrderSlippage"/> so that a size larger
    /// than the top level still fills.
    /// </summary>
    private async Task<decimal> MarketPriceAsync(HyperliquidAsset asset, bool isBuy, CancellationToken ct)
    {
        JsonElement book = await _http.InfoAsync(
            HyperliquidReads.L2Book,
            new Dictionary<string, object>(StringComparer.Ordinal) { [HyperliquidReads.Coin] = asset.Coin },
            ct).ConfigureAwait(false);

        if (!book.TryGetProperty(HyperliquidChannels.Levels, out JsonElement levels)
            || levels.ValueKind != JsonValueKind.Array || levels.GetArrayLength() < 2)
        {
            throw new NotSupportedException($"Hyperliquid published no book for {asset.Coin}, so a market order has no price to cross at");
        }

        JsonElement side = isBuy ? levels[1] : levels[0];
        if (side.ValueKind != JsonValueKind.Array || side.GetArrayLength() == 0)
        {
            throw new NotSupportedException($"Hyperliquid has nothing resting on the {(isBuy ? "ask" : "bid")} of {asset.Coin}, so a market order cannot cross");
        }

        decimal touch = side[0].Dec(HyperliquidChannels.Price);
        return isBuy
            ? touch * (1m + HyperliquidVenue.MarketOrderSlippage)
            : touch * (1m - HyperliquidVenue.MarketOrderSlippage);
    }

    /// <summary>
    /// An amendment, which this venue really has - and which replaces the whole order rather than patching a field,
    /// so everything about it travels again whether it changed or not.
    /// <para>
    /// What it can change is bounded by what an order wire carries: the price, the size, the time in force, the
    /// reduce-only flag and a trigger. The asset cannot change, and neither can the client order id - the venue
    /// matches the amendment by the order's own id, which is why an order with no venue id yet cannot be amended.
    /// Whether the venue permits a change of SIDE is not established here: it would need an order resting on a
    /// funded account to find out, and guessing would be a resize that silently reverses a position.
    /// </para>
    /// </summary>
    public override async Task ModifyOrderAsync(ModifyOrder command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        Order? order = Services.Cache.Order(command.ClientOrderId);
        StrategyId strategyId = order?.StrategyId ?? new StrategyId("EXTERNAL");
        Instrument? instrument = Find(command.InstrumentId);
        if (instrument is null || order is null)
        {
            GenerateOrderModifyRejected(
                strategyId,
                command.InstrumentId,
                command.ClientOrderId,
                command.VenueOrderId,
                instrument is null
                    ? $"instrument {command.InstrumentId} unknown to the Hyperliquid client"
                    : "this node does not hold the order, and Hyperliquid replaces a whole order on an amendment",
                Clock.Timestamp);
            return;
        }

        VenueOrderId? venueOrderId = command.VenueOrderId ?? order.VenueOrderId;
        if (venueOrderId is null)
        {
            lock (_gate)
            {
                venueOrderId = _venueIds.GetValueOrDefault(command.ClientOrderId);
            }
        }

        if (venueOrderId is not { } vid
            || !ulong.TryParse(vid.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong oid))
        {
            GenerateOrderModifyRejected(
                strategyId,
                command.InstrumentId,
                command.ClientOrderId,
                command.VenueOrderId,
                "Hyperliquid amends an order by its own numeric id and this order has not been given one yet",
                Clock.Timestamp);
            return;
        }

        GenerateOrderPendingUpdate(strategyId, command.InstrumentId, command.ClientOrderId, vid, Clock.Timestamp);
        try
        {
            HyperliquidOrderWire? wire = await WireAsync(
                order,
                instrument,
                command.Quantity ?? order.Quantity,
                command.Price ?? order.Price,
                command.TriggerPrice ?? order.TriggerPrice,
                ct).ConfigureAwait(false);

            if (wire is null)
            {
                return;
            }

            await _http.ExchangeAsync(HyperliquidActions.Modify(oid, wire), _http.NextNonce(), ct).ConfigureAwait(false);
            GenerateOrderUpdated(
                strategyId,
                command.InstrumentId,
                command.ClientOrderId,
                vid,
                command.Quantity ?? order.Quantity,
                command.Price ?? order.Price,
                command.TriggerPrice ?? order.TriggerPrice,
                Clock.Timestamp);
        }
        catch (Exception e) when (e is HyperliquidApiException or NotSupportedException)
        {
            GenerateOrderModifyRejected(strategyId, command.InstrumentId, command.ClientOrderId, vid, e is HyperliquidApiException api ? api.Detail : e.Message, Clock.Timestamp);
        }
    }

    /// <summary>
    /// A cancel by client order id, which this venue has as an action of its own - so an order can be cancelled
    /// before the venue's id for it has come back over the stream.
    /// </summary>
    public override async Task CancelOrderAsync(CancelOrder command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        Instrument? instrument = Find(command.InstrumentId);
        if (instrument is null)
        {
            Log.LogWarning("Hyperliquid cannot cancel {ClientOrderId}: {Instrument} is unknown to this client", command.ClientOrderId, command.InstrumentId);
            return;
        }

        HyperliquidAsset asset = HyperliquidAsset.Of(instrument);
        try
        {
            await _http.ExchangeAsync(
                HyperliquidActions.CancelByCloid(asset.Index, HyperliquidVenue.CloidFor(command.ClientOrderId)),
                _http.NextNonce(),
                ct).ConfigureAwait(false);
        }
        catch (HyperliquidApiException e)
        {
            Log.LogWarning("Hyperliquid refused a cancel of {ClientOrderId}: {Reason}", command.ClientOrderId, e.Detail);
        }
    }

    // ----- reports -----

    public override async Task<ExecutionMassStatus?> GenerateMassStatusAsync(UnixNanos? since, CancellationToken ct)
    {
        IReadOnlyList<OrderStatusReport> orders = await GenerateOrderStatusReportsAsync(null, since, null, openOnly: true, ct).ConfigureAwait(false);
        IReadOnlyList<FillReport> fills = await GenerateFillReportsAsync(null, null, since, null, ct).ConfigureAwait(false);
        IReadOnlyList<PositionStatusReport> positions = await GeneratePositionStatusReportsAsync(null, null, null, ct).ConfigureAwait(false);
        return new ExecutionMassStatus(ClientId, AccountId, Venue, orders, fills, positions, Clock.Timestamp, Guid.NewGuid());
    }

    /// <summary>
    /// What became of one order. The venue answers by its own numeric id only - there is no read that takes a client
    /// order id - so an order this node has no venue id for cannot be asked about, and <c>unknownOid</c> is the
    /// answer for one the venue has forgotten. Measured.
    /// </summary>
    public override async Task<OrderStatusReport?> GenerateOrderStatusReportAsync(InstrumentId instrumentId, ClientOrderId? clientOrderId, VenueOrderId? venueOrderId, CancellationToken ct)
    {
        VenueOrderId? id = venueOrderId;
        if (id is null && clientOrderId is { } c)
        {
            lock (_gate)
            {
                id = _venueIds.GetValueOrDefault(c);
            }
        }

        if (id is not { } vid || !long.TryParse(vid.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long oid))
        {
            return null;
        }

        JsonElement answer = await _http.InfoAsync(
            HyperliquidReads.OrderStatus,
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [HyperliquidReads.User] = _credentials.Account,
                [Oid] = oid,
            },
            ct).ConfigureAwait(false);

        return answer.TryGetProperty(OrderField, out JsonElement wrapped) && wrapped.TryGetProperty(OrderField, out JsonElement o)
            ? ParseOrder(o, wrapped.Str(HyperliquidChannels.Status))
            : null;
    }

    /// <summary>
    /// The resting orders. The <c>frontendOpenOrders</c> read rather than the plain one, because the plain one
    /// leaves out the trigger fields - so a stop guarding a position would come back looking like a limit order at
    /// its limit price, which is exactly the report a reconciliation must not be given.
    /// </summary>
    public override async Task<IReadOnlyList<OrderStatusReport>> GenerateOrderStatusReportsAsync(InstrumentId? instrumentId, UnixNanos? start, UnixNanos? end, bool openOnly, CancellationToken ct)
    {
        JsonElement data = await _http.InfoAsync(
            HyperliquidReads.FrontendOpenOrders,
            new Dictionary<string, object>(StringComparer.Ordinal) { [HyperliquidReads.User] = _credentials.Account },
            ct).ConfigureAwait(false);

        List<OrderStatusReport> reports = new();
        if (data.ValueKind != JsonValueKind.Array)
        {
            return reports;
        }

        foreach (JsonElement o in data.EnumerateArray())
        {
            if (ParseOrder(o, HyperliquidChannels.StatusOpen) is { } report
                && (instrumentId is not { } wanted || report.InstrumentId == wanted)
                && (start is not { } from || report.TsLast >= from)
                && (end is not { } to || report.TsLast <= to))
            {
                reports.Add(report);
            }
        }

        return reports;
    }

    /// <summary>
    /// The account's fills, newest first from the venue and oldest first from here.
    /// <para>
    /// This read mixes families: an account that trades both gets its spot fills in the same list, arriving as
    /// <c>@142</c> rather than a coin, and the assets of the builder-deployed exchanges arrive as <c>xyz:CL</c>.
    /// Both were measured on a live account. They are skipped, which is why a fill is looked up by coin and a
    /// miss is not a warning.
    /// </para>
    /// </summary>
    public override async Task<IReadOnlyList<FillReport>> GenerateFillReportsAsync(InstrumentId? instrumentId, VenueOrderId? venueOrderId, UnixNanos? start, UnixNanos? end, CancellationToken ct)
    {
        JsonElement data = await _http.InfoAsync(
            HyperliquidReads.UserFills,
            new Dictionary<string, object>(StringComparer.Ordinal) { [HyperliquidReads.User] = _credentials.Account },
            ct).ConfigureAwait(false);

        List<FillReport> fills = new();
        if (data.ValueKind != JsonValueKind.Array)
        {
            return fills;
        }

        foreach (JsonElement f in data.EnumerateArray())
        {
            Instrument? instrument = FindByCoin(f.Str(HyperliquidChannels.Coin));
            if (instrument is null || (instrumentId is { } wanted && instrument.Id != wanted))
            {
                continue;
            }

            UnixNanos ts = f.Ms(HyperliquidChannels.Time);
            if ((start is { } from && ts < from) || (end is { } to && ts > to))
            {
                continue;
            }

            VenueOrderId orderId = new(f.Str(HyperliquidChannels.Oid));
            if (venueOrderId is { } only && orderId != only)
            {
                continue;
            }

            Currency fee = f.Str(HyperliquidChannels.FeeToken) is { Length: > 0 } token
                ? Currency.FromCode(token, HyperliquidVenue.QuoteCurrencyPrecision)
                : instrument.QuoteCurrency;

            fills.Add(new FillReport(
                AccountId,
                instrument.Id,
                orderId,
                new TradeId(f.Str(HyperliquidChannels.TradeId)),
                f.Str(HyperliquidChannels.Side) == HyperliquidChannels.BuyAggressor ? OrderSide.Buy : OrderSide.Sell,
                instrument.MakeQuantity(f.Dec(HyperliquidChannels.Size)),
                instrument.MakePrice(f.Dec(HyperliquidChannels.Price)),
                new Money(f.Dec(HyperliquidChannels.Fee), fee),
                f.Bool(HyperliquidChannels.Crossed) ? LiquiditySide.Taker : LiquiditySide.Maker,
                ts,
                Clock.Timestamp,
                Guid.NewGuid(),
                null));
        }

        return [.. fills.OrderBy(f => f.TsEvent.Value)];
    }

    /// <summary>
    /// The account's positions. One per coin and signed - negative is short - because this venue has no hedge mode:
    /// every live account read reports its positions as <c>oneWay</c>, so a long and a short in one asset cannot
    /// both exist and there is no position id to carry.
    /// </summary>
    public override async Task<IReadOnlyList<PositionStatusReport>> GeneratePositionStatusReportsAsync(InstrumentId? instrumentId, UnixNanos? start, UnixNanos? end, CancellationToken ct)
    {
        JsonElement state = await _http.InfoAsync(
            HyperliquidReads.ClearinghouseState,
            new Dictionary<string, object>(StringComparer.Ordinal) { [HyperliquidReads.User] = _credentials.Account },
            ct).ConfigureAwait(false);

        List<PositionStatusReport> reports = new();
        if (!state.TryGetProperty(AssetPositions, out JsonElement positions) || positions.ValueKind != JsonValueKind.Array)
        {
            return reports;
        }

        foreach (JsonElement wrapper in positions.EnumerateArray())
        {
            if (!wrapper.TryGetProperty(PositionField, out JsonElement p))
            {
                continue;
            }

            Instrument? instrument = FindByCoin(p.Str(HyperliquidChannels.Coin));
            if (instrument is null || (instrumentId is { } wanted && instrument.Id != wanted))
            {
                continue;
            }

            decimal signed = p.Dec(SignedSize);
            reports.Add(new PositionStatusReport(
                AccountId,
                instrument.Id,
                signed > 0m ? PositionSide.Long : signed < 0m ? PositionSide.Short : PositionSide.Flat,
                instrument.MakeQuantity(Math.Abs(signed)),
                Clock.Timestamp,
                Clock.Timestamp,
                Guid.NewGuid(),
                null,
                p.Has(EntryPrice) ? p.Dec(EntryPrice) : null));
        }

        return reports;
    }

    /// <summary>
    /// One of the venue's resting orders as a report. The trigger fields are what make this read worth using: an
    /// order with a trigger price is a stop or a take-profit, and one without is a plain limit.
    /// </summary>
    private OrderStatusReport? ParseOrder(JsonElement o, string status)
    {
        Instrument? instrument = FindByCoin(o.Str(HyperliquidChannels.Coin));
        if (instrument is null)
        {
            return null;
        }

        decimal trigger = o.Dec(TriggerPx);
        bool isTrigger = o.Bool(IsTrigger) || trigger > 0m;
        bool isMarket = o.Bool(IsPositionTpsl) || o.Str(OrderTypeField).Contains("Market", StringComparison.OrdinalIgnoreCase);
        decimal size = o.Dec(HyperliquidChannels.Size);
        decimal original = o.Has(OriginalSize) ? o.Dec(OriginalSize) : size;

        OrderType type = (isTrigger, isMarket) switch
        {
            (true, true) => OrderType.StopMarket,
            (true, false) => OrderType.StopLimit,
            _ => OrderType.Limit,
        };

        UnixNanos created = o.Has(HyperliquidChannels.Timestamp) ? o.Ms(HyperliquidChannels.Timestamp) : Clock.Timestamp;
        return new OrderStatusReport(
            AccountId,
            instrument.Id,
            Resolve(o.Str(HyperliquidChannels.Cloid)),
            new VenueOrderId(o.Str(HyperliquidChannels.Oid)),
            o.Str(HyperliquidChannels.Side) == HyperliquidChannels.BuyAggressor ? OrderSide.Buy : OrderSide.Sell,
            type,

            // A resting order is good till cancelled by definition: an immediate-or-cancel order never rests, and a
            // post-only order that rested is indistinguishable from a plain one once it has.
            TimeInForce.Gtc,
            status == HyperliquidChannels.StatusOpen
                ? (original > size ? OrderStatus.PartiallyFilled : OrderStatus.Accepted)
                : OrderStatus.Canceled,
            instrument.MakeQuantity(original),
            instrument.MakeQuantity(Math.Max(0m, original - size)),
            created,
            created,
            Clock.Timestamp,
            Guid.NewGuid(),
            o.Dec(LimitPx) > 0m ? instrument.MakePrice(o.Dec(LimitPx)) : null,
            trigger > 0m ? instrument.MakePrice(trigger) : null,
            TriggerType.Default,
            null,
            TrailingOffsetType.Price,
            null,
            null,
            false,
            o.Bool(ReduceOnly),
            null,
            null,
            ContingencyType.None,
            null);
    }

    // The field names of the account and order reads, named once. The venue spells the same thing differently in
    // different reads - a size is "sz" on an order and "szi" on a position, and the position's is SIGNED - so these
    // are not interchangeable with the stream's.

    private const string MarginSummary = "marginSummary";
    private const string AccountValue = "accountValue";
    private const string TotalMarginUsed = "totalMarginUsed";
    private const string CrossMaintenanceMarginUsed = "crossMaintenanceMarginUsed";
    private const string Time = "time";
    private const string AssetPositions = "assetPositions";
    private const string PositionField = "position";
    private const string OrderField = "order";
    private const string Oid = "oid";

    /// <summary>A position's size, SIGNED: negative is short. The only signed size on this venue.</summary>
    private const string SignedSize = "szi";

    private const string EntryPrice = "entryPx";
    private const string LimitPx = "limitPx";
    private const string TriggerPx = "triggerPx";
    private const string IsTrigger = "isTrigger";
    private const string IsPositionTpsl = "isPositionTpsl";
    private const string OrderTypeField = "orderType";
    private const string OriginalSize = "origSz";
    private const string ReduceOnly = "reduceOnly";
}

/// <summary>
/// One factory for the venue's data client, named the way every adapter's is so a host can configure HYPERLIQUID
/// without knowing what is behind it.
/// </summary>
public sealed class HyperliquidDataClientFactory : IDataClientFactory
{
    public string Name => HyperliquidVenue.Venue.Value;

    public Type ConfigType => typeof(HyperliquidDataClientConfig);

    public IDataClient Create(ClientId clientId, DataClientConfig config, KernelServices services) =>
        new HyperliquidDataClient(clientId, (HyperliquidDataClientConfig)config, services);
}

public sealed class HyperliquidExecutionClientFactory : IExecutionClientFactory
{
    public string Name => HyperliquidVenue.Venue.Value;

    public Type ConfigType => typeof(HyperliquidExecutionClientConfig);

    public IExecutionClient Create(ClientId clientId, ExecutionClientConfig config, KernelServices services) =>
        new HyperliquidExecutionClient(clientId, (HyperliquidExecutionClientConfig)config, services);
}
