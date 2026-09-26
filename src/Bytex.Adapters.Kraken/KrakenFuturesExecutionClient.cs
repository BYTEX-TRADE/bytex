using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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

namespace Bytex.Adapters.Kraken;

/// <summary>
/// The words Kraken's futures platform uses for an order, and how its private socket is authenticated.
/// </summary>
internal static class KrakenFuturesOrders
{
    /// <summary>The order types this adapter sends, in the venue's spelling.</summary>
    public static string OrderType(Core.Model.OrderType type) => type switch
    {
        Core.Model.OrderType.Market => "mkt",
        Core.Model.OrderType.Limit => "lmt",
        Core.Model.OrderType.StopMarket => "stp",
        Core.Model.OrderType.StopLimit => "stp",
        Core.Model.OrderType.MarketIfTouched => "take_profit",
        Core.Model.OrderType.LimitIfTouched => "take_profit",
        _ => throw new NotSupportedException($"Kraken futures does not take a {type} order."),
    };

    /// <summary>
    /// The maker-only order type. On this platform post-only is a TYPE rather than a flag on a limit order, so a
    /// post-only limit is a different value of the same field and not an extra one.
    /// </summary>
    public const string PostOnly = "post";

    /// <summary>The immediate-or-cancel order type, which is likewise a type here rather than a time in force.</summary>
    public const string ImmediateOrCancel = "ioc";

    /// <summary>
    /// Which price a stop watches. The venue offers the last traded price, the index and the mark price; the mark
    /// price is what it liquidates against, so a stop guarding a position has to watch the same one or the position
    /// can be liquidated without the stop ever triggering.
    /// </summary>
    public const string TriggerSignal = "mark";

    /// <summary>The field a broker id travels in on an order, which is the mechanism this venue publishes.</summary>
    public const string BrokerField = "broker";

    /// <summary>The engine's order status for one of the venue's.</summary>
    public static OrderStatus Status(string status) => status switch
    {
        "untouched" => OrderStatus.Accepted,
        "partiallyFilled" => OrderStatus.PartiallyFilled,
        "filled" => OrderStatus.Filled,
        "cancelled" or "canceled" => OrderStatus.Canceled,
        "rejected" => OrderStatus.Rejected,
        _ => OrderStatus.Accepted,
    };

    /// <summary>
    /// The answer to the venue's socket challenge: the challenge hashed with SHA-256, then HMAC-SHA-512 with the
    /// base64-decoded secret, base64-encoded.
    /// <para>
    /// The challenge itself IS measured: the venue issues one for any api_key at all, without checking it, so the
    /// handshake is known to be <c>{"event":"challenge","api_key":...}</c> answered with
    /// <c>{"event":"challenge","message":"&lt;uuid&gt;"}</c>. Whether this signature satisfies it has never been
    /// tested, because a wrong one and a wrong key produce the same "Failed to subscribe to authenticated feed".
    /// </para>
    /// </summary>
    public static string SignChallenge(string secret, string challenge)
    {
        byte[] hashed = SHA256.HashData(Encoding.UTF8.GetBytes(challenge));
        return HmacSigner.Sha512Base64(Convert.FromBase64String(secret), hashed);
    }
}

/// <summary>
/// Order routing and execution reporting for Kraken's futures platform.
/// <para>
/// Separate from the spot client for the same reason the data clients are separate: a different host, a different
/// key, a different signing scheme, different endpoints and a different socket grammar. The two share a key SHAPE
/// and nothing else.
/// </para>
/// <para>
/// Two things this platform requires that spot does not.
/// </para>
/// <para>
/// LEVERAGE AND MARGIN MODE ARE ONE SETTING HERE, and that coupling cannot be avoided. A leverage preference for a
/// symbol sets a maximum leverage AND selects isolated margin for it; deleting the preference selects cross margin.
/// There is no call that sets one without the other. So a configured leverage puts the symbol into isolated margin,
/// which is stated in the log rather than done quietly - a strategy sized for cross margin behaves differently
/// under isolated, and nothing else would say the mode had changed. What leverage cross margin then runs at is NOT
/// published by the venue and is not guessed at here: with no leverage configured, nothing is set and the account
/// keeps whatever mode and limit it already had.
/// </para>
/// <para>
/// A POSITION EXISTS. A spot account holds balances; this one holds balances and positions, so positions are
/// reported and reconciled.
/// </para>
/// <para>
/// NOTHING PRIVATE HERE HAS BEEN EXERCISED AGAINST THE LIVE VENUE. The platform answers every unsigned request with
/// <c>authenticationError</c> and validates neither parameters nor paths before the signature, so the signing
/// scheme, the order field names and the socket challenge answer are all as the venue publishes them and none has
/// had a correct credential put through it. The one private-surface fact that IS measured is that
/// <c>/derivatives/api/v3/editorder</c> exists: it parsed and refused a malformed order id before it refused the
/// credentials, which no nonexistent endpoint does.
/// </para>
/// </summary>
public sealed class KrakenFuturesExecutionClient : ExecutionClientBase
{
    private readonly KrakenExecutionClientConfig _config;
    private readonly KrakenHttp _http;
    private readonly KrakenFuturesInstrumentProvider _instruments;
    private readonly Dictionary<Currency, AccountBalance> _balances = new();
    private readonly HashSet<string> _seenFills = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private WebSocketClient? _ws;
    private string? _challenge;
    private string? _signedChallenge;

    public KrakenFuturesExecutionClient(ClientId clientId, KrakenExecutionClientConfig config, KernelServices services)
        : base(
            clientId,
            KrakenVenue.Venue,
            new AccountId($"{KrakenVenue.Venue}-FUTURES"),
            AccountType.Margin,
            null,
            OmsType.Netting,
            services)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        if (_config.ProductType != KrakenProductType.Futures)
        {
            throw new ArgumentException(
                $"This client trades Kraken's futures platform and the configuration says {_config.ProductType}. "
                + "The spot platform is KrakenExecutionClient.",
                nameof(config));
        }

        _http = new KrakenHttp(config, Log, requireCredentials: true);
        _instruments = new KrakenFuturesInstrumentProvider(_http, config.InstrumentProvider, Log);
    }

    public KrakenFuturesInstrumentProvider Instruments => _instruments;

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
        await OpenPrivateSocketAsync(ct).ConfigureAwait(false);
        NotifyConnected();
    }

    /// <summary>
    /// Sets the configured leverage at the venue, per symbol, before anything trades - which is what a strategy
    /// written for 3x needs in order to be traded at 3x rather than at whatever the account was left on.
    /// <para>
    /// And it changes the margin mode, because on this venue those are one setting. Each symbol is named in the log
    /// so that a person can read what happened rather than discovering it from a liquidation price.
    /// </para>
    /// </summary>
    private async Task ApplyLeverageAsync(CancellationToken ct)
    {
        if (_config.Leverage is not { } leverage)
        {
            // Nothing configured means do not touch it, which is what every configuration written before the field
            // existed means - and here it also means leaving the account's margin mode alone.
            return;
        }

        // Before anything is sent. This adapter used to WARN here and send the preference anyway, which left the
        // venue to grant what it allows and the run to trade at a leverage nobody chose - the log line said so and
        // nothing stopped. Refusing is the same knowledge acted on.
        LeverageGuard.EnsureGranted(
            leverage,
            Services.Cache.Instruments(Venue).Concat(_instruments.GetAll()).DistinctBy(i => i.Id),
            KrakenVenue.Venue.Value);

        foreach (Instrument instrument in _instruments.GetAll())
        {
            try
            {
                await _http.PutSignedAsync(
                    KrakenFuturesVenue.LeveragePreferencesPath,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["symbol"] = instrument.RawSymbol.Value,
                        [KrakenFuturesVenue.MaxLeverageField] = Json.Fmt(leverage),
                    },
                    ct).ConfigureAwait(false);

                Log.LogInformation(
                    "Kraken futures {Instrument} set to at most {Leverage}x, which also puts it into ISOLATED margin: "
                    + "on this venue a leverage preference and the margin mode are one setting, and removing the "
                    + "preference is what selects cross margin again",
                    instrument.Id,
                    leverage);
            }
            catch (Exception e) when (e is KrakenApiException or VenueHttpException or HttpRequestException)
            {
                // Usually the account not being entitled to the figure asked for, which a node cannot fix. Logged
                // and the node starts: abandoning a start silently would be worse than trading at a leverage a
                // person can read about and change.
                Log.LogWarning(e, "Kraken futures refused the leverage preference for {Instrument}", instrument.Id);
            }
        }
    }

    private async Task OpenPrivateSocketAsync(CancellationToken ct)
    {
        _ws = new WebSocketClient(new WebSocketClientConfig
        {
            Url = new Uri(KrakenVenue.WsBase(_config) + KrakenFuturesVenue.WsPath),
        }, Log)
        {
            OnText = HandleMessageAsync,
            OnConnected = isReconnect =>
            {
                lock (_gate)
                {
                    // The challenge belongs to the connection, so a reconnection has to ask for a new one before it
                    // can subscribe to anything private.
                    _challenge = null;
                    _signedChallenge = null;
                }

                _ws?.SendText(JsonSerializer.Serialize(new { @event = "challenge", api_key = KrakenVenue.Credentials(_config).Key }));
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

        // No second challenge here: OnConnected fires on the first connection as well as on a reconnection, so
        // asking beside it would ask for two challenges and subscribe every private feed twice.
        await _ws.ConnectAsync(ct).ConfigureAwait(false);
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

    private static string Raw(InstrumentId id) => KrakenFuturesVenue.ToRawSymbol(id);

    private Instrument? Find(InstrumentId id) => _instruments.Find(id) ?? Services.Cache.Instrument(id);

    private Instrument? FindByRaw(string rawSymbol) => Find(KrakenFuturesVenue.ToInstrumentId(rawSymbol));

    // ----- the private stream -----

    private Task HandleMessageAsync(string text)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            JsonElement root = doc.RootElement;
            if (root.Has("event"))
            {
                switch (root.Str("event"))
                {
                    case "challenge":
                        SignAndSubscribe(root.Str("message"));
                        break;
                    case "alert":
                        Log.LogWarning("Kraken futures private stream alert: {Message}", root.Str("message"));
                        break;
                }

                return Task.CompletedTask;
            }

            switch (root.Str("feed"))
            {
                case "open_orders" or "open_orders_snapshot":
                    HandleOrders(root);
                    break;
                case "fills" or "fills_snapshot":
                    HandleFills(root);
                    break;
                case "balances" or "balances_snapshot":
                    HandleBalances(root);
                    break;
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            Log.LogWarning(e, "Kraken futures: unreadable private stream message");
        }

        return Task.CompletedTask;
    }

    private void SignAndSubscribe(string challenge)
    {
        if (challenge.Length == 0)
        {
            return;
        }

        string key = KrakenVenue.Credentials(_config).Key;
        string signed = KrakenFuturesOrders.SignChallenge(KrakenVenue.Credentials(_config).Secret, challenge);
        lock (_gate)
        {
            _challenge = challenge;
            _signedChallenge = signed;
        }

        foreach (string feed in KrakenFuturesVenue.PrivateFeeds)
        {
            _ws?.SendText(JsonSerializer.Serialize(new
            {
                @event = "subscribe",
                feed,
                api_key = key,
                original_challenge = challenge,
                signed_challenge = signed,
            }));
        }
    }

    /// <summary>
    /// The account's open orders. The venue publishes the whole list on subscribing and then one message per change,
    /// where the change carries the order under <c>order</c> and says what happened in <c>reason</c>.
    /// </summary>
    private void HandleOrders(JsonElement root)
    {
        if (root.TryGetProperty("orders", out JsonElement orders) && orders.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement order in orders.EnumerateArray())
            {
                ReportOrder(order, accepted: true);
            }

            return;
        }

        if (root.TryGetProperty("order", out JsonElement single))
        {
            ReportOrder(single, accepted: true);
            return;
        }

        // A removal carries the id and the reason and no order at all, which is what a cancel looks like here.
        if (root.Str("order_id") is { Length: > 0 } venueId)
        {
            Log.LogInformation("Kraken futures dropped order {VenueOrderId}: {Reason}", venueId, root.Str("reason"));
        }
    }

    private void ReportOrder(JsonElement o, bool accepted)
    {
        string clientOid = o.Str("cli_ord_id");
        if (clientOid.Length == 0 || Services.Cache.Order(new ClientOrderId(clientOid)) is not { } order)
        {
            // An order this node never placed. There is nothing to report it against, and inventing an id would
            // attach the venue's activity to an order that does not exist.
            return;
        }

        if (!accepted)
        {
            return;
        }

        GenerateOrderAccepted(
            order.StrategyId,
            order.InstrumentId,
            order.ClientOrderId,
            new VenueOrderId(o.Str("order_id")),
            o.Has("time") ? o.Ms("time") : Clock.Timestamp);
    }

    private void HandleFills(JsonElement root)
    {
        if (!root.TryGetProperty("fills", out JsonElement fills) || fills.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement f in fills.EnumerateArray())
        {
            string fillId = f.Str("fill_id");
            if (fillId.Length > 0)
            {
                lock (_gate)
                {
                    if (!_seenFills.Add(fillId))
                    {
                        continue;
                    }
                }
            }

            string clientOid = f.Str("cli_ord_id");
            Order? order = clientOid.Length > 0 ? Services.Cache.Order(new ClientOrderId(clientOid)) : null;
            if (order is null || Find(order.InstrumentId) is not { } instrument)
            {
                continue;
            }

            Quantity filled = instrument.MakeQuantity(f.Dec("qty"));
            Price price = instrument.MakePrice(f.Dec("price"));
            LiquiditySide liquidity = f.Str("fill_type").Contains("maker", StringComparison.OrdinalIgnoreCase)
                ? LiquiditySide.Maker
                : LiquiditySide.Taker;

            GenerateOrderFilled(
                order.StrategyId,
                order.InstrumentId,
                order.ClientOrderId,
                new VenueOrderId(f.Str("order_id")),
                null,
                new TradeId(fillId.Length > 0 ? fillId : Guid.NewGuid().ToString("N")),
                f.Str("buy") == "true" || f.Bool("buy") ? OrderSide.Buy : OrderSide.Sell,
                order.Type,
                filled,
                price,
                instrument.QuoteCurrency,

                // The fills feed carries no fee, so the instrument's own rate stands in. A fill report read over
                // REST carries the venue's figure and reconciliation replaces this with it.
                instrument.CalculateCommission(filled, price, liquidity),
                liquidity,
                f.Has("time") ? f.Ms("time") : Clock.Timestamp);
        }
    }

    private void HandleBalances(JsonElement root)
    {
        // The multi-collateral account is the one this family is margined out of; the others under the same key hold
        // collateral for the coin-margined contracts this adapter does not offer.
        if (!root.TryGetProperty("flex_futures", out JsonElement flex)
            || !flex.TryGetProperty("currencies", out JsonElement currencies)
            || currencies.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        lock (_gate)
        {
            foreach (JsonProperty holding in currencies.EnumerateObject())
            {
                Currency currency = Currency.FromCode(holding.Name);
                decimal quantity = holding.Value.Dec("quantity");
                decimal available = holding.Value.Dec("available");
                _balances[currency] = AccountBalance.Of(
                    new Money(quantity, currency),
                    new Money(Math.Max(0m, quantity - available), currency));
            }
        }

        GenerateAccountState(Snapshot(), [], reported: true, Clock.Timestamp);
    }

    private List<AccountBalance> Snapshot()
    {
        lock (_gate)
        {
            return _balances.Values.ToList();
        }
    }

    private async Task PublishAccountStateAsync(CancellationToken ct)
    {
        try
        {
            JsonElement data = await _http.GetSignedAsync(KrakenFuturesVenue.AccountsPath, null, ct).ConfigureAwait(false);
            if (data.TryGetProperty("accounts", out JsonElement accounts)
                && accounts.TryGetProperty(KrakenFuturesVenue.MultiCollateralAccount, out JsonElement multi)
                && multi.TryGetProperty("currencies", out JsonElement currencies)
                && currencies.ValueKind == JsonValueKind.Object)
            {
                lock (_gate)
                {
                    foreach (JsonProperty holding in currencies.EnumerateObject())
                    {
                        Currency currency = Currency.FromCode(holding.Name);
                        decimal quantity = holding.Value.Dec("quantity");
                        decimal available = holding.Value.Dec("available");
                        _balances[currency] = AccountBalance.Of(
                            new Money(quantity, currency),
                            new Money(Math.Max(0m, quantity - available), currency));
                    }
                }
            }

            GenerateAccountState(Snapshot(), [], reported: true, Clock.Timestamp);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.LogError(e, "Failed to load Kraken futures account state");
        }
    }

    // ----- commands -----

    public override async Task SubmitOrderAsync(SubmitOrder command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        Order order = command.Order;
        if (Find(order.InstrumentId) is not { } instrument)
        {
            GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, $"instrument {order.InstrumentId} unknown to the Kraken futures client", Clock.Timestamp);
            return;
        }

        if (order.IsQuoteQuantity)
        {
            GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, "Kraken futures sizes an order in base currency, so a quote quantity cannot be sent", Clock.Timestamp);
            return;
        }

        Dictionary<string, string> form;
        try
        {
            form = Body(order, instrument);
        }
        catch (NotSupportedException e)
        {
            GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, e.Message, Clock.Timestamp);
            return;
        }

        GenerateOrderSubmitted(order.StrategyId, order.InstrumentId, order.ClientOrderId, Clock.Timestamp);
        try
        {
            JsonElement data = await _http.PostSignedAsync(KrakenFuturesVenue.SendOrderPath, form, ct).ConfigureAwait(false);

            // The venue wraps the outcome of the order in a status object, and a REFUSAL arrives here rather than as
            // an error: the request succeeded and the order did not, so a client reading only the envelope would
            // report an order as placed that the venue never accepted.
            if (data.TryGetProperty("sendStatus", out JsonElement status))
            {
                string outcome = status.Str("status");
                if (outcome is "placed" or "partiallyFilled" or "filled")
                {
                    GenerateOrderAccepted(order.StrategyId, order.InstrumentId, order.ClientOrderId, new VenueOrderId(status.Str("order_id")), Clock.Timestamp);
                }
                else if (outcome.Length > 0)
                {
                    GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, outcome, Clock.Timestamp);
                }
            }
        }
        catch (KrakenApiException e)
        {
            GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, e.Code, Clock.Timestamp);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, e.Message, Clock.Timestamp);
        }
    }

    private Dictionary<string, string> Body(Order order, Instrument instrument)
    {
        bool limit = order.Type is Core.Model.OrderType.Limit or Core.Model.OrderType.StopLimit or Core.Model.OrderType.LimitIfTouched;
        string type = KrakenFuturesOrders.OrderType(order.Type);

        // Post-only and immediate-or-cancel are TYPES on this venue rather than qualifiers of a limit order, so a
        // limit order that is either of those is sent as a different type and not as a limit with a flag.
        if (limit && order.TriggerPrice is null)
        {
            if (order.IsPostOnly)
            {
                type = KrakenFuturesOrders.PostOnly;
            }
            else if (order.TimeInForce is Core.Model.TimeInForce.Ioc)
            {
                type = KrakenFuturesOrders.ImmediateOrCancel;
            }
        }

        Dictionary<string, string> form = new(StringComparer.Ordinal)
        {
            ["orderType"] = type,
            ["symbol"] = instrument.RawSymbol.Value,
            ["side"] = order.IsBuy ? "buy" : "sell",
            ["size"] = Json.Fmt(order.Quantity.Value),
            ["cliOrdId"] = order.ClientOrderId.Value,
        };

        if (limit && order.Price is { } price)
        {
            form["limitPrice"] = Json.Fmt(price.Value);
        }

        if (order.TriggerPrice is { } trigger)
        {
            form["stopPrice"] = Json.Fmt(trigger.Value);
            form["triggerSignal"] = KrakenFuturesOrders.TriggerSignal;
        }

        if (order.IsReduceOnly)
        {
            form["reduceOnly"] = "true";
        }

        if (_config.BrokerId is { Length: > 0 } broker)
        {
            form[KrakenFuturesOrders.BrokerField] = broker;
        }

        return form;
    }

    /// <summary>
    /// Changes an order the venue already holds. The endpoint is the one fact about this platform's private surface
    /// that is measured: asked to edit order "abc" it answered "Invalid UUID string: abc" BEFORE it refused the
    /// credentials, which a path that does not exist cannot do - that answers 404 NOT_FOUND instead.
    /// </summary>
    public override async Task ModifyOrderAsync(ModifyOrder command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        Order? order = Services.Cache.Order(command.ClientOrderId);
        StrategyId strategyId = order?.StrategyId ?? new StrategyId("EXTERNAL");
        Dictionary<string, string> form = new(StringComparer.Ordinal);
        if (command.VenueOrderId is { } venueOrderId)
        {
            form["orderId"] = venueOrderId.Value;
        }
        else
        {
            form["cliOrdId"] = command.ClientOrderId.Value;
        }

        if (command.Quantity is { } quantity)
        {
            form["size"] = Json.Fmt(quantity.Value);
        }

        if (command.Price is { } price)
        {
            form["limitPrice"] = Json.Fmt(price.Value);
        }

        if (command.TriggerPrice is { } trigger)
        {
            form["stopPrice"] = Json.Fmt(trigger.Value);
        }

        GenerateOrderPendingUpdate(strategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, Clock.Timestamp);
        try
        {
            JsonElement data = await _http.PostSignedAsync(KrakenFuturesVenue.EditOrderPath, form, ct).ConfigureAwait(false);
            if (data.TryGetProperty("editStatus", out JsonElement status)
                && status.Str("status") is { Length: > 0 } outcome
                && outcome != "edited")
            {
                // As with an order: the request succeeded and the amend did not, so the refusal is in the body.
                GenerateOrderModifyRejected(strategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, outcome, Clock.Timestamp);
            }
        }
        catch (Exception e) when (e is KrakenApiException or VenueHttpException or HttpRequestException)
        {
            GenerateOrderModifyRejected(
                strategyId,
                command.InstrumentId,
                command.ClientOrderId,
                command.VenueOrderId,
                e is KrakenApiException refusal ? refusal.Code : e.Message,
                Clock.Timestamp);
        }
    }

    public override async Task CancelOrderAsync(CancelOrder command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        Dictionary<string, string> form = command.VenueOrderId is { } venueOrderId
            ? new(StringComparer.Ordinal) { ["order_id"] = venueOrderId.Value }
            : new(StringComparer.Ordinal) { ["cliOrdId"] = command.ClientOrderId.Value };

        try
        {
            await _http.PostSignedAsync(KrakenFuturesVenue.CancelOrderPath, form, ct).ConfigureAwait(false);
        }
        catch (KrakenApiException e)
        {
            Log.LogWarning("Kraken futures refused a cancel of {ClientOrderId}: {Reason}", command.ClientOrderId, e.Code);
        }
    }

    /// <summary>
    /// Cancels every open order on one contract. Unlike the spot platform this one takes a symbol, so the request a
    /// caller made is the request the venue receives rather than a loop over what this node happens to hold.
    /// </summary>
    public override async Task CancelAllOrdersAsync(CancelAllOrders command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        try
        {
            await _http.PostSignedAsync(
                KrakenFuturesVenue.CancelAllOrdersPath,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["symbol"] = Raw(command.InstrumentId) },
                ct).ConfigureAwait(false);
        }
        catch (KrakenApiException e)
        {
            Log.LogWarning("Kraken futures refused a cancel-all for {Instrument}: {Reason}", command.InstrumentId, e.Code);
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
        // The platform has no by-id order endpoint, so the open orders are read and the one asked for is picked out.
        IReadOnlyList<OrderStatusReport> open = await GenerateOrderStatusReportsAsync(instrumentId, null, null, openOnly: true, ct).ConfigureAwait(false);
        return open.FirstOrDefault(r =>
            (clientOrderId is { } c && r.ClientOrderId == c) || (venueOrderId is { } v && r.VenueOrderId == v));
    }

    public override async Task<IReadOnlyList<OrderStatusReport>> GenerateOrderStatusReportsAsync(InstrumentId? instrumentId, UnixNanos? start, UnixNanos? end, bool openOnly, CancellationToken ct)
    {
        JsonElement data = await _http.GetSignedAsync(KrakenFuturesVenue.OpenOrdersPath, null, ct).ConfigureAwait(false);
        List<OrderStatusReport> reports = new();
        if (!data.TryGetProperty("openOrders", out JsonElement orders) || orders.ValueKind != JsonValueKind.Array)
        {
            return reports;
        }

        foreach (JsonElement o in orders.EnumerateArray())
        {
            if (FindByRaw(o.Str("symbol")) is not { } instrument)
            {
                continue;
            }

            if (instrumentId is { } only && instrument.Id != only)
            {
                continue;
            }

            UnixNanos at = o.Iso("receivedTime");
            if ((start is { } from && at < from) || (end is { } to && at > to))
            {
                continue;
            }

            decimal size = o.Dec("unfilledSize") + o.Dec("filledSize");
            reports.Add(new OrderStatusReport(
                AccountId,
                instrument.Id,
                o.Str("cliOrdId") is { Length: > 0 } clientOid ? new ClientOrderId(clientOid) : null,
                new VenueOrderId(o.Str("order_id")),
                o.Str("side") == "buy" ? OrderSide.Buy : OrderSide.Sell,
                o.Str("orderType") switch
                {
                    "mkt" or "market" => Core.Model.OrderType.Market,
                    "stp" or "stop" => Core.Model.OrderType.StopMarket,
                    "take_profit" => Core.Model.OrderType.MarketIfTouched,
                    _ => Core.Model.OrderType.Limit,
                },
                Core.Model.TimeInForce.Gtc,
                KrakenFuturesOrders.Status(o.Str("status")),
                instrument.MakeQuantity(size > 0m ? size : o.Dec("size")),
                instrument.MakeQuantity(o.Dec("filledSize")),
                at,
                at,
                Clock.Timestamp,
                Guid.NewGuid(),
                Price: o.Dec("limitPrice") > 0m ? instrument.MakePrice(o.Dec("limitPrice")) : null,
                TriggerPrice: o.Dec("stopPrice") > 0m ? instrument.MakePrice(o.Dec("stopPrice")) : null,
                ReduceOnly: o.Bool("reduceOnly")));
        }

        return reports;
    }

    public override async Task<IReadOnlyList<FillReport>> GenerateFillReportsAsync(InstrumentId? instrumentId, VenueOrderId? venueOrderId, UnixNanos? start, UnixNanos? end, CancellationToken ct)
    {
        JsonElement data = await _http.GetSignedAsync(KrakenFuturesVenue.FillsPath, null, ct).ConfigureAwait(false);
        List<FillReport> reports = new();
        if (!data.TryGetProperty("fills", out JsonElement fills) || fills.ValueKind != JsonValueKind.Array)
        {
            return reports;
        }

        foreach (JsonElement f in fills.EnumerateArray())
        {
            if (FindByRaw(f.Str("symbol")) is not { } instrument)
            {
                continue;
            }

            if (instrumentId is { } only && instrument.Id != only)
            {
                continue;
            }

            VenueOrderId orderId = new(f.Str("order_id"));
            if (venueOrderId is { } wanted && orderId != wanted)
            {
                continue;
            }

            UnixNanos at = f.Iso("fillTime");
            if ((start is { } from && at < from) || (end is { } to && at > to))
            {
                continue;
            }

            Quantity size = instrument.MakeQuantity(f.Dec("size"));
            Price price = instrument.MakePrice(f.Dec("price"));
            LiquiditySide liquidity = f.Str("fillType").Contains("maker", StringComparison.OrdinalIgnoreCase)
                ? LiquiditySide.Maker
                : LiquiditySide.Taker;

            reports.Add(new FillReport(
                AccountId,
                instrument.Id,
                orderId,
                new TradeId(f.Str("fill_id")),
                f.Bool("side") || f.Str("side") == "buy" ? OrderSide.Buy : OrderSide.Sell,
                size,
                price,
                instrument.CalculateCommission(size, price, liquidity),
                liquidity,
                at,
                Clock.Timestamp,
                Guid.NewGuid(),
                ClientOrderId: f.Str("cliOrdId") is { Length: > 0 } clientOid ? new ClientOrderId(clientOid) : null));
        }

        return reports;
    }

    public override async Task<IReadOnlyList<PositionStatusReport>> GeneratePositionStatusReportsAsync(InstrumentId? instrumentId, UnixNanos? start, UnixNanos? end, CancellationToken ct)
    {
        JsonElement data = await _http.GetSignedAsync(KrakenFuturesVenue.OpenPositionsPath, null, ct).ConfigureAwait(false);
        List<PositionStatusReport> reports = new();
        if (!data.TryGetProperty("openPositions", out JsonElement positions) || positions.ValueKind != JsonValueKind.Array)
        {
            return reports;
        }

        foreach (JsonElement p in positions.EnumerateArray())
        {
            if (FindByRaw(p.Str("symbol")) is not { } instrument)
            {
                continue;
            }

            if (instrumentId is { } only && instrument.Id != only)
            {
                continue;
            }

            decimal size = p.Dec("size");
            bool isLong = p.Str("side") == "long";
            reports.Add(new PositionStatusReport(
                AccountId,
                instrument.Id,
                size == 0m ? PositionSide.Flat : isLong ? PositionSide.Long : PositionSide.Short,
                instrument.MakeQuantity(Math.Abs(size)),
                p.Iso("fillTime"),
                Clock.Timestamp,
                Guid.NewGuid(),
                AvgPxOpen: p.Dec("price") > 0m ? p.Dec("price") : null));
        }

        return reports;
    }
}

public sealed class KrakenDataClientFactory : IDataClientFactory
{
    public string Name => "KRAKEN";

    public Type ConfigType => typeof(KrakenDataClientConfig);

    public IDataClient Create(ClientId clientId, DataClientConfig config, KernelServices services)
    {
        KrakenDataClientConfig kraken = (KrakenDataClientConfig)config;
        return kraken.ProductType == KrakenProductType.Futures
            ? new KrakenFuturesDataClient(clientId, kraken, services)
            : new KrakenDataClient(clientId, kraken, services);
    }
}

public sealed class KrakenExecutionClientFactory : IExecutionClientFactory
{
    public string Name => "KRAKEN";

    public Type ConfigType => typeof(KrakenExecutionClientConfig);

    public IExecutionClient Create(ClientId clientId, ExecutionClientConfig config, KernelServices services)
    {
        KrakenExecutionClientConfig kraken = (KrakenExecutionClientConfig)config;
        return kraken.ProductType == KrakenProductType.Futures
            ? new KrakenFuturesExecutionClient(clientId, kraken, services)
            : new KrakenExecutionClient(clientId, kraken, services);
    }
}

public sealed class KrakenPlugin : Core.Plugins.IPlugin, Core.Adapters.IVenuePlugin
{
    public string Id => "bytex.kraken";

    /// <summary>
    /// Kraken as two families, and they are further apart than any other venue's here. Spot and futures are not two
    /// products of one API: they are separate platforms, on separate hosts, with SEPARATE CREDENTIALS - a spot key
    /// is issued on one site and a futures key on the other, and neither signs for the other, because the signing
    /// schemes differ too. What is declared per family is therefore almost everything.
    /// <para>
    /// Neither family has a test environment. The futures platform had one at <c>demo-futures.kraken.com</c> and
    /// that host now answers a permanent redirect to a marketing page; every other spelling of a demo or sandbox
    /// host fails to resolve, and the spot platform has never had one. So this venue can be papered and it cannot be
    /// rehearsed, which is a fact worth knowing before a key is put into a live node.
    /// </para>
    /// </summary>
    public Core.Adapters.VenueDescriptor Describe() => new()
    {
        Venue = KrakenVenue.Venue,
        DisplayName = "Kraken",

        // A field on the order, on both platforms: AddOrder and sendorder each take a `broker` parameter carrying
        // the partner's own Kraken IIBAN. Nothing about the order's identity changes, so turning an id on needs no
        // reconciliation test the way a client-order-id prefix does.
        BrokerTag = Core.Adapters.BrokerTag.OrderField,

        // The venue runs the programme and publishes the mechanism, and this adapter carries an id for it. What
        // cannot be verified without an account on it is whether the field is ACCEPTED: the futures platform
        // documents its `broker` parameter as available in pre-production only, and it validates neither parameters
        // nor paths before the signature, so an unapproved id may simply be ignored there.
        BrokerProgramme = Core.Adapters.BrokerProgramme.Carried,
        Families =
        [
            new Core.Adapters.VenueFamily
            {
                Name = "spot",
                InstrumentClasses = [InstrumentClass.Spot],
                PaysFunding = false,
                HttpBase = KrakenVenue.DefaultHttpBase,

                // The PUBLIC socket. This platform serves public market data and private data on two different
                // hosts and refuses each on the other's, in as many words, so one base cannot describe both - and
                // the one a host can be offered is the one a data client uses. The private host is derived from it.
                WsBase = KrakenVenue.DefaultWsBase,
                Key = KrakenKey,
                Config = new Dictionary<string, string> { ["productType"] = nameof(KrakenProductType.Spot) },
                IgnoredConfig = ["accountType"],

                // From the venue's published schedule and NOT from its API: the per-pair fee arrays it documents
                // measured empty on all 1451 pairs it lists, and on the `info=fees` variant as well.
                DefaultFees = new Core.Adapters.VenueFees(KrakenVenue.DefaultMakerFee, KrakenVenue.DefaultTakerFee),
                Capabilities = new Core.Adapters.VenueCapabilities
                {
                    LoadOneInstrument = true,
                    ListInstruments = true,
                    BarHistory = true,

                    // Spot pays no funding, so there is none to fetch.
                    FundingHistory = false,
                    MarketData = true,
                    Execution = true,

                    // A real amend through /0/private/AmendOrder, which keeps the order's identity rather than
                    // cancelling and replacing it - so a protective order is never off the book while it is resized.
                    AmendOrders = true,
                },
            },
            new Core.Adapters.VenueFamily
            {
                Name = "futures",

                // Perpetuals and dated contracts both, which is a fact about the venue: it lists 278 perpetuals and
                // 10 dated contracts of the type this adapter offers, and its own type field does not distinguish
                // them - only the expiry field does. Coin-margined contracts are not offered, because a quantity of
                // one cannot be expressed in base currency without a price.
                InstrumentClasses = [InstrumentClass.Swap, InstrumentClass.Future],
                PaysFunding = true,
                HttpBase = KrakenFuturesVenue.DefaultHttpBase,

                // One host for both the public feeds and the private ones here, unlike spot: the private feeds are
                // opened on the same connection after a challenge is signed.
                WsBase = KrakenFuturesVenue.DefaultWsBase,
                Key = KrakenKey,
                Config = new Dictionary<string, string> { ["productType"] = nameof(KrakenProductType.Futures) },
                IgnoredConfig = ["accountType"],

                // Measured from the schedule PF_XBTUSD points at, remembering that the platform publishes its fees
                // as percentages: two and five basis points.
                DefaultFees = new Core.Adapters.VenueFees(KrakenFuturesVenue.DefaultMakerFee, KrakenFuturesVenue.DefaultTakerFee),
                Capabilities = new Core.Adapters.VenueCapabilities
                {
                    LoadOneInstrument = true,
                    ListInstruments = true,
                    BarHistory = true,
                    FundingHistory = true,
                    MarketData = true,
                    Execution = true,

                    // /derivatives/api/v3/editorder, which is the one private endpoint whose existence is measured:
                    // it refused a malformed order id before it refused the credentials.
                    AmendOrders = true,
                },
            },
        ],
    };

    /// <summary>
    /// The same two parts on both platforms - and NOT the same two values. A spot key and a futures key are issued
    /// separately and sign differently, so the variables hold whichever platform's key the configured family needs.
    /// A host that offers one pair of fields per venue can only hold one of them at a time, which is a limit of the
    /// declaration rather than of the venue.
    /// </summary>
    private static Core.Adapters.VenueKey KrakenKey => new()
    {
        Parts =
        [
            new Core.Adapters.VenueKeyPart("API key", KrakenVenue.EnvApiKey, Secret: false),
            new Core.Adapters.VenueKeyPart("private key", KrakenVenue.EnvApiSecret, Secret: true),
        ],
    };

    public string Version => typeof(KrakenPlugin).Assembly.GetName().Version?.ToString() ?? "0";

    public void Register(Core.Plugins.IPluginRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.AddDataClientFactory(new KrakenDataClientFactory());
        registry.AddExecutionClientFactory(new KrakenExecutionClientFactory());
    }
}
