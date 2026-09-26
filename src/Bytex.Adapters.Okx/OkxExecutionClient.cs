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

namespace Bytex.Adapters.Okx;

/// <summary>
/// Order routing and execution reporting for one of OKX's three markets.
/// <para>
/// One client for all three, because the endpoints are the same on all three: what an order carries changes, not
/// where it is sent. A spot order is sized in base currency and says its margin mode is "cash"; a derivative order
/// is sized in CONTRACTS and says whether the account posts cross or isolated margin. Both are hidden here, so a
/// strategy written against base-currency quantities is the same strategy on all three markets and on the other
/// venues this engine speaks to.
/// </para>
/// <para>
/// Three things this venue demands that none of the others do.
/// </para>
/// <para>
/// A client order id of letters and digits only, and at most 32 of them. The engine's own ids carry hyphens unless a
/// strategy says otherwise, so an order with one is refused here with the setting that fixes it named. Rewriting the
/// id instead would put the order at the venue under a name reconciliation can no longer match it by, which is the
/// failure the broker-id prefix on another venue had to be built carefully to avoid.
/// </para>
/// <para>
/// A trade mode on every order. The venue has no default to fall back on and the choice is not a per-order one, so it
/// is configured once per client and spot has no choice to make.
/// </para>
/// <para>
/// And a size unit that is stated. The venue's own default for a spot MARKET order is the quote currency, so a market
/// buy of 0.01 would spend 0.01 USDT rather than buy 0.01 bitcoin; every spot order therefore says which currency its
/// size is in rather than letting the default decide.
/// </para>
/// </summary>
public sealed class OkxExecutionClient : ExecutionClientBase
{
    private const string OrderPath = "/api/v5/trade/order";

    private const string CancelPath = "/api/v5/trade/cancel-order";

    private const string CancelBatchPath = "/api/v5/trade/cancel-batch-orders";

    private const string PendingOrdersPath = "/api/v5/trade/orders-pending";

    private const string OrderHistoryPath = "/api/v5/trade/orders-history";

    private const string FillsPath = "/api/v5/trade/fills";

    private const string BalancePath = "/api/v5/account/balance";

    private const string PositionsPath = "/api/v5/account/positions";

    /// <summary>The private channels a trading node needs: its orders, its balances and its positions.</summary>
    private const string ChannelOrders = "orders";

    private const string ChannelAccount = "account";

    private const string ChannelPositions = "positions";

    /// <summary>
    /// How many order cancellations one batch request carries. The venue's own documented maximum for this endpoint;
    /// it cannot be measured without a key, so a batch larger than this is sent in several requests rather than
    /// risking one refusal that cancels nothing.
    /// </summary>
    private const int CancelBatchPage = 20;

    /// <summary>What the venue calls a fill made by the resting side of the trade.</summary>
    private const string MakerExecType = "M";

    private readonly OkxExecutionClientConfig _config;
    private readonly OkxHttp _http;
    private readonly OkxInstrumentProvider _instruments;
    private readonly HashSet<string> _seenTrades = new(StringComparer.Ordinal);
    private readonly Dictionary<Currency, AccountBalance> _balances = [];
    private readonly object _gate = new();
    private WebSocketClient? _ws;

    public OkxExecutionClient(ClientId clientId, OkxExecutionClientConfig config, KernelServices services)
        : base(
            clientId,
            OkxVenue.Venue,
            new AccountId($"{OkxVenue.Venue}-{OkxVenue.InstType(config?.InstrumentType ?? OkxInstrumentType.Spot)}"),
            config?.InstrumentType == OkxInstrumentType.Spot ? AccountType.Cash : AccountType.Margin,
            null,

            // Netting, and the reason is stated rather than assumed: this client never sends a position side, which
            // is what the venue's net mode means. Its long/short mode needs a side on every order and on every
            // leverage change, and it cannot be told apart from net mode without reading an account - so a client
            // that guessed would place orders the venue refuses for a reason nobody could act on.
            OmsType.Netting,
            services)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _http = new OkxHttp(config, Log, requireCredentials: true);
        _instruments = new OkxInstrumentProvider(_http, config.InstrumentType, config.InstrumentProvider, Log);
    }

    public OkxInstrumentProvider Instruments => _instruments;

    private bool IsSpot => _config.InstrumentType == OkxInstrumentType.Spot;

    /// <summary>The trade mode every order carries: "cash" on spot, and the configured margin mode elsewhere.</summary>
    private string TradeMode => IsSpot ? OkxVenue.CashTradeMode : OkxVenue.MarginMode(_config.MarginMode);

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

        await ApplyLeverageAsync(ct).ConfigureAwait(false);
        await PublishAccountStateAsync(ct).ConfigureAwait(false);

        OkxCredentials credentials = OkxVenue.Credentials(_config);
        _ws = new WebSocketClient(new WebSocketClientConfig
        {
            Url = OkxVenue.WsPrivate(_config),
            PingMessage = OkxVenue.PingMessage,
            PingInterval = OkxVenue.PingInterval,
        }, Log)
        {
            OnText = HandleMessageAsync,
            OnConnected = isReconnect =>
            {
                // The private socket is subscribed to only after a login, and the login is answered asynchronously,
                // so the subscriptions go out when that answer arrives rather than here. Sending them now would have
                // them refused for not being authenticated - on the first connection and on every reconnection.
                _ws?.SendText(OkxStream.Login(credentials, DateTimeOffset.UtcNow));

                if (isReconnect)
                {
                    // The drop marked the client disconnected and the socket reconnects by itself: say so, or the
                    // mark stays for good while orders and fills flow.
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

    /// <summary>
    /// Sets the configured leverage at the venue, per instrument, before anything trades. This venue holds leverage
    /// as account state rather than taking it on an order - an order carrying one is ignored - so a strategy written
    /// for 3x is traded at 3x only if this happens, and a node that skipped it would trade at whatever the account
    /// was last left on with nothing to say so.
    /// <para>
    /// The scope is the instrument and the margin mode. The venue also takes a leverage per CURRENCY, for cross
    /// margin on its margin-trading product, and a position side on top of both when an account is in isolated
    /// long/short mode - neither applies here: these are derivative positions in net mode, so the instrument is the
    /// scope and no side is sent.
    /// </para>
    /// <para>
    /// A refusal does not stop the node. It is usually the venue saying the account is not entitled to the figure
    /// asked for, which a node cannot fix, and abandoning a start over it would be worse than trading at a leverage
    /// somebody can read about and change.
    /// </para>
    /// </summary>
    private async Task ApplyLeverageAsync(CancellationToken ct)
    {
        if (_config.Leverage is not { } leverage || IsSpot)
        {
            // Spot has no leverage to set. Refusing the configuration instead would reject something harmless, and a
            // host setting one field for every venue should not have to know which markets read it.
            return;
        }

        IReadOnlyList<Instrument> tradable =
            [.. Services.Cache.Instruments(Venue).Concat(_instruments.GetAll()).DistinctBy(i => i.Id)];

        // Before anything is sent. A venue asked for more leverage than it grants does not refuse - it grants
        // what it will and trades on, so the strategy would run at a size it was never tested at.
        LeverageGuard.EnsureGranted(leverage, tradable, OkxVenue.Venue.Value);

        foreach (Instrument instrument in tradable)
        {
            try
            {
                await _http.PostSignedAsync(
                    OkxVenue.SetLeveragePath,
                    new
                    {
                        instId = instrument.RawSymbol.Value,
                        mgnMode = OkxVenue.MarginMode(_config.MarginMode),
                        lever = Json.Fmt(leverage),
                    },
                    ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Log.LogError(
                    e,
                    "OKX refused {Leverage}x on {Instrument}; it will trade at whatever the account is set to",
                    leverage,
                    instrument.Id);
            }
        }
    }

    // ----- helpers -----

    private static string Raw(InstrumentId id) => OkxVenue.ToRawSymbol(id);

    private Instrument? Find(InstrumentId id) => _instruments.Find(id) ?? Services.Cache.Instrument(id);

    private Instrument? FindByRaw(string instId) => Find(OkxVenue.ToInstrumentId(instId));

    private static IEnumerable<JsonElement> Rows(JsonElement data) =>
        data.ValueKind == JsonValueKind.Array ? data.EnumerateArray() : [];

    // ----- the private stream -----

    private Task HandleMessageAsync(string text)
    {
        if (text.Length == 0 || string.Equals(text, OkxVenue.PongMessage, StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            JsonElement root = doc.RootElement;
            string e = root.Str("event");
            if (e.Length > 0)
            {
                HandleEvent(e, root);
                return Task.CompletedTask;
            }

            if (!root.Has("arg") || !root.Has("data"))
            {
                return Task.CompletedTask;
            }

            JsonElement data = root.GetProperty("data");
            switch (root.GetProperty("arg").Str("channel"))
            {
                case ChannelOrders:
                    foreach (JsonElement o in Rows(data))
                    {
                        HandleOrderChange(o);
                    }

                    break;

                case ChannelAccount:
                    foreach (JsonElement a in Rows(data))
                    {
                        HandleBalances(a);
                    }

                    break;

                case ChannelPositions:
                    foreach (JsonElement p in Rows(data))
                    {
                        HandlePosition(p);
                    }

                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            Log.LogWarning(ex, "OKX: unreadable private stream message {Text}", LogText.Truncate(text, LogText.MaxMessageLength));
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The login answer and the subscription answers. The subscriptions go out here because the venue refuses them
    /// until the socket is authenticated, and the login is a message with an answer rather than a handshake.
    /// </summary>
    private void HandleEvent(string e, JsonElement root)
    {
        if (string.Equals(e, OkxStream.EventError, StringComparison.Ordinal))
        {
            Log.LogWarning("OKX private stream error {Code}: {Message}", root.Str("code"), root.Str("msg"));
            return;
        }

        if (!string.Equals(e, OkxStream.OpLogin, StringComparison.Ordinal))
        {
            return;
        }

        // The orders and positions channels are per market and the balance channel is account-wide, which is why one
        // of the three subscriptions carries no instType at all.
        _ws?.SendText(OkxStream.SubscribePrivate(ChannelOrders, _http.InstType));
        _ws?.SendText(OkxStream.SubscribePrivate(ChannelPositions, _http.InstType));
        _ws?.SendText(OkxStream.SubscribePrivate(ChannelAccount, null));
    }

    /// <summary>
    /// An order's life on this venue. The state is a word - <c>live</c>, <c>partially_filled</c>, <c>filled</c>,
    /// <c>canceled</c> - and a message carries BOTH the state and, when the state changed because of a trade, that
    /// trade: so a single message can be an acceptance, or a fill, or both.
    /// </summary>
    private void HandleOrderChange(JsonElement o)
    {
        string clOrdId = o.Str("clOrdId");
        if (clOrdId.Length == 0)
        {
            // An order this node did not place - placed by hand, or by something else on the same account. There is
            // no id to attribute it to, so there is nothing to report.
            return;
        }

        ClientOrderId clientOrderId = new(clOrdId);
        Order? order = Services.Cache.Order(clientOrderId);
        InstrumentId instrumentId = order?.InstrumentId ?? OkxVenue.ToInstrumentId(o.Str("instId"));
        Instrument? instrument = Find(instrumentId);
        if (instrument is null)
        {
            return;
        }

        StrategyId strategyId = order?.StrategyId ?? new StrategyId("EXTERNAL");
        VenueOrderId venueOrderId = new(o.Str("ordId"));
        UnixNanos ts = o.Filled("uTime") ? o.Ms("uTime") : o.Filled("cTime") ? o.Ms("cTime") : Clock.Timestamp;
        string state = o.Str("state");

        // The trade first, where there is one, so that a fill is reported before the terminal state that follows it.
        if (o.Filled("tradeId") && o.Dec("fillSz") > 0m)
        {
            HandleFill(o, order, strategyId, instrumentId, clientOrderId, venueOrderId, instrument, ts);
        }

        switch (state)
        {
            case "live":
                GenerateOrderAccepted(strategyId, instrumentId, clientOrderId, venueOrderId, ts);
                break;

            case "canceled":
                GenerateOrderCanceled(strategyId, instrumentId, clientOrderId, venueOrderId, ts);
                break;

            case "mmp_canceled":
                // A market-maker protection cancel. A cancel from the engine's side, whatever the venue's reason.
                GenerateOrderCanceled(strategyId, instrumentId, clientOrderId, venueOrderId, ts);
                break;

            case "filled":
            case "partially_filled":
                // The fill above is the event; the state only repeats what it already said.
                break;
        }

        // An amendment the venue applied, which it reports on the order rather than as an event of its own.
        if (o.Str("amendResult") == "0")
        {
            GenerateOrderUpdated(
                strategyId,
                instrumentId,
                clientOrderId,
                venueOrderId,
                OkxVenue.FromVenueSize(instrument, o.Dec("sz")),
                o.Dec("px") > 0m ? instrument.MakePrice(o.Dec("px")) : null,
                null,
                ts);
        }
    }

    private void HandleFill(JsonElement o, Order? order, StrategyId strategyId, InstrumentId instrumentId, ClientOrderId clientOrderId, VenueOrderId venueOrderId, Instrument instrument, UnixNanos ts)
    {
        string tradeId = o.Str("tradeId");
        lock (_gate)
        {
            if (!_seenTrades.Add(tradeId))
            {
                return;
            }
        }

        Quantity filled = OkxVenue.FromVenueSize(instrument, o.Dec("fillSz"));
        Price price = instrument.MakePrice(o.Dec("fillPx"));
        LiquiditySide liquidity = o.Str("execType") == MakerExecType ? LiquiditySide.Maker : LiquiditySide.Taker;

        // The venue reports a fee as a NEGATIVE number when it charged one and a positive number when it paid a
        // rebate, which is the opposite sign from what a commission is. Its own figure is used where it sent one,
        // because that is what the account was charged; the instrument's rate stands in only where it sent none.
        Currency feeCurrency = o.Filled("fillFeeCcy") ? Currency.FromCode(o.Str("fillFeeCcy"))
            : o.Filled("feeCcy") ? Currency.FromCode(o.Str("feeCcy"))
            : instrument.QuoteCurrency;

        Money commission = o.Filled("fillFee")
            ? new Money(-o.Dec("fillFee"), feeCurrency)
            : o.Filled("fee")
                ? new Money(-o.Dec("fee"), feeCurrency)
                : instrument.CalculateCommission(filled, price, liquidity);

        GenerateOrderFilled(
            strategyId,
            instrumentId,
            clientOrderId,
            venueOrderId,
            null,
            new TradeId(tradeId),
            o.Str("side") == "buy" ? OrderSide.Buy : OrderSide.Sell,
            order?.Type ?? OrderType.Market,
            filled,
            price,
            instrument.QuoteCurrency,
            commission,
            liquidity,
            ts);
    }

    /// <summary>
    /// The account's balances. The venue nests them one level down - a single account row with a <c>details</c> array
    /// of currencies - and publishes them as equity and what of it is free.
    /// </summary>
    private void HandleBalances(JsonElement account)
    {
        if (!account.Has("details"))
        {
            return;
        }

        List<AccountBalance> all;
        lock (_gate)
        {
            foreach (JsonElement d in Rows(account.GetProperty("details")))
            {
                if (ToBalance(d) is { } balance)
                {
                    _balances[balance.Total.Currency] = balance;
                }
            }

            all = [.. _balances.Values];
        }

        if (all.Count > 0)
        {
            GenerateAccountState(all, [], reported: true, account.Filled("uTime") ? account.Ms("uTime") : Clock.Timestamp);
        }
    }

    private static AccountBalance? ToBalance(JsonElement d)
    {
        string code = d.Str("ccy");
        if (code.Length == 0)
        {
            return null;
        }

        Currency currency = Currency.FromCode(code);
        decimal equity = d.Filled("eq") ? d.Dec("eq") : d.Dec("cashBal");
        decimal available = d.Filled("availEq") ? d.Dec("availEq") : d.Filled("availBal") ? d.Dec("availBal") : equity;
        return AccountBalance.Of(new Money(equity, currency), new Money(Math.Max(0m, equity - available), currency));
    }

    private void HandlePosition(JsonElement p)
    {
        if (FindByRaw(p.Str("instId")) is { } instrument)
        {
            Log.LogDebug(
                "OKX position {Instrument}: {Quantity}",
                instrument.Id,
                OkxVenue.FromVenueSize(instrument, Math.Abs(p.Dec("pos"))));
        }
    }

    private async Task PublishAccountStateAsync(CancellationToken ct)
    {
        try
        {
            lock (_gate)
            {
                _balances.Clear();
            }

            // One request for the whole unified account, which is the shape of this venue: spot balances and
            // derivative margin live in one account rather than one per market, so there is nothing to ask twice.
            JsonElement data = await _http.GetSignedAsync(BalancePath, null, ct).ConfigureAwait(false);
            foreach (JsonElement account in Rows(data))
            {
                HandleBalances(account);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.LogError(e, "Failed to load OKX account state");
        }
    }

    // ----- commands -----

    public override async Task SubmitOrderAsync(SubmitOrder command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        Order order = command.Order;
        Instrument? instrument = Find(order.InstrumentId);
        if (instrument is null)
        {
            GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, $"instrument {order.InstrumentId} unknown to the OKX client", Clock.Timestamp);
            return;
        }

        if (!OkxVenue.IsAcceptableClientOrderId(order.ClientOrderId.Value))
        {
            GenerateOrderRejected(
                order.StrategyId,
                order.InstrumentId,
                order.ClientOrderId,
                $"OKX accepts a client order id of letters and digits only, at most {OkxVenue.MaxClientOrderIdLength} of "
                + $"them, and '{order.ClientOrderId.Value}' is not one. Set UseHyphensInClientOrderIds to false on the "
                + "strategy: rewriting the id here would place the order under a name reconciliation could not match.",
                Clock.Timestamp);
            return;
        }

        if (order.Type is not (OrderType.Market or OrderType.Limit))
        {
            // The venue keeps every triggered order in a separate algo-order system with its own endpoint, its own
            // ids and its own stream channel. Saying so is the honest answer; sending a trigger price on a plain
            // order would have it accepted as a working order with no trigger at all.
            GenerateOrderRejected(
                order.StrategyId,
                order.InstrumentId,
                order.ClientOrderId,
                $"order type {order.Type} is not supported by this OKX client; the venue holds triggered orders in a "
                + "separate algo-order system this adapter does not speak",
                Clock.Timestamp);
            return;
        }

        if (order.IsQuoteQuantity && !IsSpot)
        {
            GenerateOrderRejected(
                order.StrategyId,
                order.InstrumentId,
                order.ClientOrderId,
                "OKX sizes a derivative order in contracts, so a quote quantity cannot be sent",
                Clock.Timestamp);
            return;
        }

        GenerateOrderSubmitted(order.StrategyId, order.InstrumentId, order.ClientOrderId, Clock.Timestamp);
        try
        {
            JsonElement data = await _http.PostSignedAsync(OrderPath, Body(order, instrument), ct).ConfigureAwait(false);

            // The venue answers a batch-shaped array even for one order, and it reports a REFUSED order inside that
            // array with HTTP 200 and an envelope code of 0 - the per-order code is the one that says whether the
            // order was taken. Reading only the envelope would report every refused order as accepted.
            foreach (JsonElement row in Rows(data))
            {
                string code = row.Str("sCode");
                if (code.Length > 0 && code != OkxVenue.Ok)
                {
                    GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, row.Str("sMsg").Length > 0 ? row.Str("sMsg") : "OKX refused the order with code " + code, Clock.Timestamp);
                }

                return;
            }
        }
        catch (OkxApiException e)
        {
            GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, e.Msg.Length > 0 ? e.Msg : e.Message, Clock.Timestamp);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, e.Message, Clock.Timestamp);
        }
    }

    /// <summary>
    /// The request body of an order. Three things here are this venue's own: the trade mode, which it demands and has
    /// no default for; the size, which is contracts on a derivative and base currency on spot; and, on spot, the
    /// statement of which currency the size is in, because the venue's default for a market order is the quote one.
    /// </summary>
    private Dictionary<string, object> Body(Order order, Instrument instrument)
    {
        bool limit = order.Type == OrderType.Limit;
        Dictionary<string, object> body = new(StringComparer.Ordinal)
        {
            ["instId"] = instrument.RawSymbol.Value,
            ["tdMode"] = TradeMode,
            ["clOrdId"] = order.ClientOrderId.Value,
            ["side"] = order.IsBuy ? "buy" : "sell",
            ["sz"] = Json.Fmt(order.IsQuoteQuantity ? order.Quantity.Value : OkxVenue.ToVenueSize(instrument, order.Quantity)),
            ["ordType"] = limit ? OrderTypeOf(order) : "market",
        };

        if (limit)
        {
            body["px"] = Json.Fmt(order.Price!.Value.Value);
        }

        if (IsSpot)
        {
            // Stated on every spot order, market or limit, so there is one answer rather than a default that changes
            // with the order type.
            body["tgtCcy"] = order.IsQuoteQuantity ? OkxVenue.SizeInQuoteCurrency : OkxVenue.SizeInBaseCurrency;
        }

        if (order.IsReduceOnly)
        {
            body["reduceOnly"] = true;
        }

        return body;
    }

    /// <summary>
    /// What the venue calls a limit order of this kind. It carries the time in force IN the order type rather than in
    /// a field of its own, which is why post-only and fill-or-kill are spellings of "limit" here.
    /// </summary>
    private static string OrderTypeOf(Order order) => (order.TimeInForce, order.IsPostOnly) switch
    {
        (TimeInForce.Fok, _) => "fok",
        (TimeInForce.Ioc, _) => "ioc",
        (_, true) => "post_only",
        _ => "limit",
    };

    /// <summary>
    /// Changes an order already at the venue. This venue can, on all three markets and through one endpoint, which is
    /// what its family declarations say - and it is worth naming because the market next to it in this engine, KuCoin's
    /// perpetuals, cannot amend at all.
    /// <para>
    /// The venue refuses an amendment that changes nothing, so a command that names neither a new size nor a new
    /// price is refused here rather than sent.
    /// </para>
    /// </summary>
    public override async Task ModifyOrderAsync(ModifyOrder command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        Order? order = Services.Cache.Order(command.ClientOrderId);
        StrategyId strategyId = order?.StrategyId ?? new StrategyId("EXTERNAL");
        Instrument? instrument = Find(command.InstrumentId);
        if (instrument is null)
        {
            GenerateOrderModifyRejected(strategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, $"instrument {command.InstrumentId} unknown to the OKX client", Clock.Timestamp);
            return;
        }

        if (command.Quantity is null && command.Price is null)
        {
            GenerateOrderModifyRejected(strategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, "OKX refuses an amendment that changes neither the size nor the price", Clock.Timestamp);
            return;
        }

        Dictionary<string, object> body = new(StringComparer.Ordinal)
        {
            ["instId"] = instrument.RawSymbol.Value,
            ["clOrdId"] = command.ClientOrderId.Value,
        };

        if (command.Quantity is { } quantity)
        {
            body["newSz"] = Json.Fmt(OkxVenue.ToVenueSize(instrument, quantity));
        }

        if (command.Price is { } price)
        {
            body["newPx"] = Json.Fmt(price.Value);
        }

        GenerateOrderPendingUpdate(strategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, Clock.Timestamp);
        try
        {
            JsonElement data = await _http.PostSignedAsync(OkxVenue.AmendOrderPath, body, ct).ConfigureAwait(false);
            foreach (JsonElement row in Rows(data))
            {
                string code = row.Str("sCode");
                if (code.Length > 0 && code != OkxVenue.Ok)
                {
                    GenerateOrderModifyRejected(strategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, row.Str("sMsg").Length > 0 ? row.Str("sMsg") : "OKX refused the amendment with code " + code, Clock.Timestamp);
                }

                return;
            }
        }
        catch (OkxApiException e)
        {
            GenerateOrderModifyRejected(strategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, e.Msg.Length > 0 ? e.Msg : e.Message, Clock.Timestamp);
        }
    }

    public override async Task CancelOrderAsync(CancelOrder command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        try
        {
            await _http.PostSignedAsync(
                CancelPath,
                new
                {
                    instId = Raw(command.InstrumentId),
                    clOrdId = command.ClientOrderId.Value,
                },
                ct).ConfigureAwait(false);
        }
        catch (OkxApiException e)
        {
            Log.LogWarning("OKX refused a cancel of {ClientOrderId}: {Reason}", command.ClientOrderId, e.Msg);
        }
    }

    /// <summary>
    /// Cancels everything open for one instrument. The venue has no cancel-all of its own for this market, so the
    /// open orders are read and cancelled in batches - which is one request per twenty orders rather than one per
    /// order, and matters when a node is shutting down and the orders are the only thing left at the venue.
    /// </summary>
    public override async Task CancelAllOrdersAsync(CancelAllOrders command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        IReadOnlyList<OrderStatusReport> open = await GenerateOrderStatusReportsAsync(command.InstrumentId, null, null, openOnly: true, ct).ConfigureAwait(false);
        List<object> pending = [];
        foreach (OrderStatusReport report in open)
        {
            if (report.ClientOrderId is { } clientOrderId)
            {
                pending.Add(new { instId = Raw(report.InstrumentId), clOrdId = clientOrderId.Value });
            }
            else
            {
                pending.Add(new { instId = Raw(report.InstrumentId), ordId = report.VenueOrderId.Value });
            }
        }

        for (int i = 0; i < pending.Count; i += CancelBatchPage)
        {
            object[] page = [.. pending.Skip(i).Take(CancelBatchPage)];
            try
            {
                await _http.PostSignedAsync(CancelBatchPath, page, ct).ConfigureAwait(false);
            }
            catch (OkxApiException e)
            {
                Log.LogWarning("OKX refused a cancel-all for {Instrument}: {Reason}", command.InstrumentId, e.Msg);
            }
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

    public override async Task<OrderStatusReport?> GenerateOrderStatusReportAsync(InstrumentId instrumentId, ClientOrderId? clientOrderId, VenueOrderId? venueOrderId, CancellationToken ct)
    {
        Dictionary<string, string> query = new(StringComparer.Ordinal) { ["instId"] = Raw(instrumentId) };
        if (clientOrderId is { } c)
        {
            query["clOrdId"] = c.Value;
        }
        else if (venueOrderId is { } v)
        {
            query["ordId"] = v.Value;
        }
        else
        {
            return null;
        }

        try
        {
            JsonElement data = await _http.GetSignedAsync(OrderPath, query, ct).ConfigureAwait(false);
            foreach (JsonElement o in Rows(data))
            {
                return ParseOrder(o);
            }
        }
        catch (OkxApiException)
        {
            // The venue refuses a question about an order it has never heard of. That is an answer, and the caller's
            // question was "what became of this" - so it is answered with nothing rather than with a venue string.
            return null;
        }

        return null;
    }

    public override async Task<IReadOnlyList<OrderStatusReport>> GenerateOrderStatusReportsAsync(InstrumentId? instrumentId, UnixNanos? start, UnixNanos? end, bool openOnly, CancellationToken ct)
    {
        Dictionary<string, string> query = new(StringComparer.Ordinal) { ["instType"] = _http.InstType };
        if (instrumentId is { } id)
        {
            query["instId"] = Raw(id);
        }

        if (start is { } s)
        {
            // The venue's "begin" is a creation time in milliseconds, and it bounds the history endpoint only: an
            // open order is open whenever it was placed, so a window on the pending endpoint would hide orders a
            // reconciliation has to see.
            query["begin"] = s.ToMilliseconds().ToString(CultureInfo.InvariantCulture);
        }

        if (end is { } e)
        {
            query["end"] = e.ToMilliseconds().ToString(CultureInfo.InvariantCulture);
        }

        if (openOnly)
        {
            query.Remove("begin");
            query.Remove("end");
        }

        List<OrderStatusReport> reports = [];
        JsonElement data = await _http.GetSignedAsync(openOnly ? PendingOrdersPath : OrderHistoryPath, query, ct).ConfigureAwait(false);
        foreach (JsonElement o in Rows(data))
        {
            if (ParseOrder(o) is { } report)
            {
                reports.Add(report);
            }
        }

        return reports;
    }

    public override async Task<IReadOnlyList<FillReport>> GenerateFillReportsAsync(InstrumentId? instrumentId, VenueOrderId? venueOrderId, UnixNanos? start, UnixNanos? end, CancellationToken ct)
    {
        Dictionary<string, string> query = new(StringComparer.Ordinal) { ["instType"] = _http.InstType };
        if (instrumentId is { } id)
        {
            query["instId"] = Raw(id);
        }

        if (venueOrderId is { } v)
        {
            query["ordId"] = v.Value;
        }

        if (start is { } s)
        {
            query["begin"] = s.ToMilliseconds().ToString(CultureInfo.InvariantCulture);
        }

        if (end is { } e)
        {
            query["end"] = e.ToMilliseconds().ToString(CultureInfo.InvariantCulture);
        }

        List<FillReport> fills = [];
        JsonElement data = await _http.GetSignedAsync(FillsPath, query, ct).ConfigureAwait(false);
        foreach (JsonElement x in Rows(data))
        {
            Instrument? instrument = FindByRaw(x.Str("instId"));
            if (instrument is null)
            {
                continue;
            }

            Currency feeCurrency = x.Filled("feeCcy") ? Currency.FromCode(x.Str("feeCcy")) : instrument.QuoteCurrency;
            fills.Add(new FillReport(
                AccountId,
                instrument.Id,
                new VenueOrderId(x.Str("ordId")),
                new TradeId(x.Str("tradeId")),
                x.Str("side") == "buy" ? OrderSide.Buy : OrderSide.Sell,
                OkxVenue.FromVenueSize(instrument, x.Dec("fillSz")),
                instrument.MakePrice(x.Dec("fillPx")),

                // Negated, because this venue signs a fee it charged as negative and a rebate it paid as positive,
                // and a commission is the other way round.
                new Money(-x.Dec("fee"), feeCurrency),
                x.Str("execType") == MakerExecType ? LiquiditySide.Maker : LiquiditySide.Taker,
                x.Ms("ts"),
                Clock.Timestamp,
                Guid.NewGuid(),
                x.Filled("clOrdId") ? new ClientOrderId(x.Str("clOrdId")) : null));
        }

        return fills;
    }

    /// <summary>
    /// What the account holds. The venue signs a position - negative is short - and counts it in contracts on the
    /// derivative markets, so the sign becomes the side and the size becomes base currency here.
    /// </summary>
    public override async Task<IReadOnlyList<PositionStatusReport>> GeneratePositionStatusReportsAsync(InstrumentId? instrumentId, UnixNanos? start, UnixNanos? end, CancellationToken ct)
    {
        List<PositionStatusReport> reports = [];
        if (IsSpot)
        {
            // A cash spot account holds balances rather than positions, so there is nothing to report and nothing to
            // ask for. Asking anyway would return this account's DERIVATIVE positions, which are another client's.
            return reports;
        }

        JsonElement data = await _http
            .GetSignedAsync(PositionsPath, new Dictionary<string, string>(StringComparer.Ordinal) { ["instType"] = _http.InstType }, ct)
            .ConfigureAwait(false);

        foreach (JsonElement p in Rows(data))
        {
            Instrument? instrument = FindByRaw(p.Str("instId"));
            if (instrument is null || (instrumentId is { } wanted && instrument.Id != wanted))
            {
                continue;
            }

            decimal contracts = p.Dec("pos");
            UnixNanos ts = p.Filled("uTime") ? p.Ms("uTime") : Clock.Timestamp;
            if (contracts == 0m)
            {
                // Flat is still a position the venue lists, and reporting it keeps reconciliation able to close one
                // this node thinks is open.
                reports.Add(new PositionStatusReport(
                    AccountId,
                    instrument.Id,
                    PositionSide.Flat,
                    instrument.MakeQuantity(0m),
                    ts,
                    Clock.Timestamp,
                    Guid.NewGuid()));
                continue;
            }

            reports.Add(new PositionStatusReport(
                AccountId,
                instrument.Id,
                contracts > 0m ? PositionSide.Long : PositionSide.Short,
                OkxVenue.FromVenueSize(instrument, Math.Abs(contracts)),
                ts,
                Clock.Timestamp,
                Guid.NewGuid(),
                null,
                p.Filled("avgPx") ? p.Dec("avgPx") : null));
        }

        return reports;
    }

    /// <summary>
    /// One order as the venue describes it. The venue carries the time in force inside the order type rather than
    /// beside it, so both are read out of the one field.
    /// </summary>
    private OrderStatusReport? ParseOrder(JsonElement o)
    {
        Instrument? instrument = FindByRaw(o.Str("instId"));
        if (instrument is null)
        {
            return null;
        }

        string ordType = o.Str("ordType");
        decimal price = o.Dec("px");
        Quantity size = OkxVenue.FromVenueSize(instrument, o.Dec("sz"));
        Quantity filled = OkxVenue.FromVenueSize(instrument, o.Dec("accFillSz"));

        OrderStatus status = o.Str("state") switch
        {
            "live" => OrderStatus.Accepted,
            "partially_filled" => OrderStatus.PartiallyFilled,
            "filled" => OrderStatus.Filled,
            "canceled" or "mmp_canceled" => OrderStatus.Canceled,
            _ => filled.Value > 0m ? OrderStatus.PartiallyFilled : OrderStatus.Accepted,
        };

        UnixNanos created = o.Filled("cTime") ? o.Ms("cTime") : Clock.Timestamp;
        return new OrderStatusReport(
            AccountId,
            instrument.Id,
            o.Filled("clOrdId") ? new ClientOrderId(o.Str("clOrdId")) : null,
            new VenueOrderId(o.Str("ordId")),
            o.Str("side") == "buy" ? OrderSide.Buy : OrderSide.Sell,
            ordType == "market" ? OrderType.Market : OrderType.Limit,
            ordType switch
            {
                "fok" => TimeInForce.Fok,
                "ioc" => TimeInForce.Ioc,

                // A market order on this venue is filled or cancelled immediately, which is what immediate-or-cancel
                // means; every other type rests until it is cancelled.
                "market" => TimeInForce.Ioc,
                _ => TimeInForce.Gtc,
            },
            status,
            size,
            filled,
            created,
            o.Filled("uTime") ? o.Ms("uTime") : created,
            Clock.Timestamp,
            Guid.NewGuid(),
            ordType != "market" && price > 0m ? instrument.MakePrice(price) : null,
            null,
            TriggerType.Default,
            null,
            TrailingOffsetType.Price,
            null,
            o.Filled("avgPx") && o.Dec("avgPx") > 0m ? o.Dec("avgPx") : null,
            ordType == "post_only",
            o.Filled("reduceOnly") && o.Str("reduceOnly") == "true");
    }
}

public sealed class OkxDataClientFactory : IDataClientFactory
{
    public string Name => OkxVenue.Venue.Value;

    public Type ConfigType => typeof(OkxDataClientConfig);

    public IDataClient Create(ClientId clientId, DataClientConfig config, KernelServices services) =>
        new OkxDataClient(clientId, (OkxDataClientConfig)config, services);
}

public sealed class OkxExecutionClientFactory : IExecutionClientFactory
{
    public string Name => OkxVenue.Venue.Value;

    public Type ConfigType => typeof(OkxExecutionClientConfig);

    public IExecutionClient Create(ClientId clientId, ExecutionClientConfig config, KernelServices services) =>
        new OkxExecutionClient(clientId, (OkxExecutionClientConfig)config, services);
}

public sealed class OkxPlugin : Core.Plugins.IPlugin, IVenuePlugin
{
    public string Id => "bytex.okx";

    /// <summary>
    /// OKX as three markets on one API. It is the first venue here to bring more than two families and the first
    /// whose families share a host, so almost everything a host would otherwise infer from an address has to be read
    /// off these declarations instead: what selects a family is a parameter on the request, not where it is sent.
    /// <para>
    /// Its key has three parts, like KuCoin's and unlike Binance's and Bybit's, and the third cannot be recovered
    /// after the key is made.
    /// </para>
    /// </summary>
    public VenueDescriptor Describe() => new()
    {
        Venue = OkxVenue.Venue,
        DisplayName = "OKX",

        // Nothing carries a broker id here, and that is not a claim about the venue having no programme.
        BrokerTag = BrokerTag.None,

        // The venue runs a broker programme and publishes its mechanism only to approved applicants: what an order
        // has to carry, and whether it is a tag, a header or a credential, is not in the public documentation at all.
        // So nothing can be built before somebody applies, which is what this state means - and it is deliberately
        // not "no programme", because every trade routed here earns a rebate that is not being claimed.
        BrokerProgramme = BrokerProgramme.MechanismUndisclosed,
        Families =
        [
            new VenueFamily
            {
                Name = "spot",
                InstrumentClasses = [InstrumentClass.Spot],
                PaysFunding = false,
                HttpBase = OkxVenue.DefaultHttpBase,
                WsBase = OkxVenue.DefaultWsBase,
                Key = OkxKey,
                Config = new Dictionary<string, string> { ["instrumentType"] = nameof(OkxInstrumentType.Spot) },

                // Every other venue here names its markets with one of these two words and this one does not, so a
                // host that has a value for either has a value for a field this venue ignores.
                IgnoredConfig = ["accountType", "productType"],
                DefaultFees = new VenueFees(OkxFees.SpotMaker, OkxFees.SpotTaker),
                FreeDatasets = [new VenueDataset("daily trades, one zip per instrument per day", OkxDatasets.DailyTrades)],
                Capabilities = new VenueCapabilities
                {
                    LoadOneInstrument = true,
                    ListInstruments = true,
                    BarHistory = true,

                    // Spot pays no funding, so there is none to fetch.
                    FundingHistory = false,
                    MarketData = true,
                    Execution = true,

                    // One amend endpoint serves all three markets, which is why this is true on all three.
                    AmendOrders = true,
                },
            },
            new VenueFamily
            {
                Name = "swap",
                InstrumentClasses = [InstrumentClass.Swap],
                PaysFunding = true,
                HttpBase = OkxVenue.DefaultHttpBase,
                WsBase = OkxVenue.DefaultWsBase,
                Key = OkxKey,
                Config = new Dictionary<string, string> { ["instrumentType"] = nameof(OkxInstrumentType.Swap) },
                IgnoredConfig = ["accountType", "productType"],
                DefaultFees = new VenueFees(OkxFees.SwapMaker, OkxFees.SwapTaker),
                FreeDatasets = [new VenueDataset("daily trades, one zip per instrument per day", OkxDatasets.DailyTrades)],
                Capabilities = new VenueCapabilities
                {
                    LoadOneInstrument = true,
                    ListInstruments = true,
                    BarHistory = true,
                    FundingHistory = true,
                    MarketData = true,
                    Execution = true,
                    AmendOrders = true,
                },
            },
            new VenueFamily
            {
                Name = "futures",

                // Dated contracts that deliver. The spelling is NOT BTC-USDT-250926: the venue lists no
                // USDT-margined dated futures at all, and its linear ones are BTC-USD_UM-261030, settled in USD and
                // sized in the base coin. Checked against all 244 of them.
                InstrumentClasses = [InstrumentClass.Future],

                // A dated contract converges on spot by delivering rather than by being funded, so it is charged
                // none - which is why this cannot be a venue-wide fact even though two of the three markets share
                // an endpoint.
                PaysFunding = false,
                HttpBase = OkxVenue.DefaultHttpBase,
                WsBase = OkxVenue.DefaultWsBase,
                Key = OkxKey,
                Config = new Dictionary<string, string> { ["instrumentType"] = nameof(OkxInstrumentType.Futures) },
                IgnoredConfig = ["accountType", "productType"],
                DefaultFees = new VenueFees(OkxFees.FuturesMaker, OkxFees.FuturesTaker),

                // Empty, and that is a statement rather than an omission: the daily trade archive the other two
                // families publish has no file for a dated contract. BTC-USDT and BTC-USDT-SWAP both answered 200
                // for the same day and BTC-USD_UM-261030 answered 404.
                FreeDatasets = [],
                Capabilities = new VenueCapabilities
                {
                    LoadOneInstrument = true,
                    ListInstruments = true,
                    BarHistory = true,

                    // No funding is charged here, so there is none to fetch. The helper exists for the swap family
                    // beside it, which is exactly why this has to be declared per family.
                    FundingHistory = false,
                    MarketData = true,
                    Execution = true,
                    AmendOrders = true,
                },
            },
        ],
    };

    /// <summary>
    /// The same three parts on all three markets: one OKX key, one unified account. All three are required - there is
    /// no optional fourth part as there is on KuCoin, where a key version defaults.
    /// </summary>
    private static VenueKey OkxKey => new()
    {
        Parts =
        [
            new VenueKeyPart("API key", OkxVenue.EnvApiKey, Secret: false),
            new VenueKeyPart("API secret", OkxVenue.EnvApiSecret, Secret: true),

            // Chosen when the key is made and not recoverable afterwards, so a host that never offers the field
            // leaves a user holding two thirds of a key with no way to say which third is missing.
            new VenueKeyPart("passphrase", OkxVenue.EnvApiPassphrase, Secret: true),
        ],
    };

    public string Version => typeof(OkxPlugin).Assembly.GetName().Version?.ToString() ?? "0";

    public void Register(Core.Plugins.IPluginRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.AddDataClientFactory(new OkxDataClientFactory());
        registry.AddExecutionClientFactory(new OkxExecutionClientFactory());
    }
}

/// <summary>
/// What the venue publishes for anyone to download. Measured rather than listed: the daily trade archive answered 200
/// with 19 MB for BTC-USDT-SWAP and for BTC-USDT on the same day, and 404 for a dated contract - and the aggregated
/// trade and funding-rate archives other venues publish do not exist here at all.
/// </summary>
internal static class OkxDatasets
{
    /// <summary>
    /// The root of the daily trade archive. A file under it is
    /// <c>&lt;yyyyMMdd&gt;/&lt;instId&gt;-trades-&lt;yyyy-MM-dd&gt;.zip</c> - a root rather than one file, on the
    /// same terms as the REST and socket bases beside it, because which file a host wants is the host's business.
    /// </summary>
    public const string DailyTrades = "https://www.okx.com/cdn/okex/traderecords/trades/daily";
}
