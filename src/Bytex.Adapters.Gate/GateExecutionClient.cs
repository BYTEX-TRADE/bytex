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

namespace Bytex.Adapters.Gate;

/// <summary>
/// Order routing and execution reporting for Gate's spot market.
/// <para>
/// One thing about this venue is different from every other in this repository and it touches every order: Gate has
/// no client-order-id field. The id travels in the order's <c>text</c> field, which the venue also uses for its own
/// purposes - it writes <c>web</c>, <c>api</c> or <c>liquidation</c> there on orders it raised itself - and it
/// demands a <c>t-</c> prefix and at most 28 characters after it from anything a user puts there. So an id is
/// prefixed on the way out and unprefixed on the way back, and the prefix is what tells this node's orders from
/// everybody else's on the same account.
/// </para>
/// </summary>
public sealed class GateExecutionClient : ExecutionClientBase
{
    private readonly GateExecutionClientConfig _config;
    private readonly GateHttp _http;
    private readonly GateInstrumentProvider _instruments;
    private readonly HashSet<string> _seenTrades = new(StringComparer.Ordinal);
    private readonly Dictionary<Currency, AccountBalance> _balances = new();
    private readonly object _gate = new();
    private WebSocketClient? _ws;

    public GateExecutionClient(ClientId clientId, GateExecutionClientConfig config, KernelServices services)
        : base(
            clientId,
            GateVenue.Venue,
            new AccountId($"{GateVenue.Venue}-SPOT"),
            AccountType.Cash,
            null,
            OmsType.Netting,
            services)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        if (_config.ProductType != GateProductType.Spot)
        {
            throw new ArgumentException(
                $"This client trades Gate's spot market and the configuration says {_config.ProductType}. The two "
                + "derivative markets are GateFuturesExecutionClient and GateDeliveryExecutionClient.",
                nameof(config));
        }

        _http = new GateHttp(config, Log, requireCredentials: true);
        _instruments = new GateInstrumentProvider(_http, config.InstrumentProvider, Log);
    }

    public GateInstrumentProvider Instruments => _instruments;

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

        await PublishAccountStateAsync(ct).ConfigureAwait(false);
        _ws = GateUserStream.Open(
            GateVenue.WsBase(_config),
            GateStream.SpotPrefix,
            GateVenue.Credentials(_config),
            Log,
            HandleMessageAsync,
            NotifyConnected,
            NotifyDisconnected);

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

    // ----- the private stream -----

    private Task HandleMessageAsync(string text)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            JsonElement root = doc.RootElement;
            if (root.Has("error"))
            {
                JsonElement error = root.GetProperty("error");
                Log.LogWarning(
                    "Gate spot refused the {Channel} private subscription with {Code}: {Message}",
                    root.Str("channel"),
                    error.Long("code"),
                    error.Str("message"));
                return Task.CompletedTask;
            }

            if (root.Str("event") != GateStream.UpdateEvent || !root.Has("result"))
            {
                return Task.CompletedTask;
            }

            JsonElement result = root.GetProperty("result");
            switch (root.Str("channel"))
            {
                case GateStream.SpotPrefix + GateUserStream.Orders:
                    Each(result, HandleOrderChange);
                    break;
                case GateStream.SpotPrefix + GateUserStream.UserTrades:
                    Each(result, HandleUserTrade);
                    break;
                case GateStream.SpotPrefix + GateUserStream.Balances:
                    Each(result, HandleBalance);
                    break;
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            Log.LogWarning(e, "Gate spot: unreadable private stream message");
        }

        return Task.CompletedTask;
    }

    private static void Each(JsonElement result, Action<JsonElement> handle)
    {
        if (result.ValueKind != JsonValueKind.Array)
        {
            handle(result);
            return;
        }

        foreach (JsonElement row in result.EnumerateArray())
        {
            handle(row);
        }
    }

    /// <summary>
    /// An order's life on this venue: <c>put</c> when it reaches the book, <c>update</c> when it is amended or
    /// partly filled, and <c>finish</c> when it leaves - filled, cancelled or expired, which the <c>finish_as</c>
    /// field distinguishes.
    /// </summary>
    private void HandleOrderChange(JsonElement o)
    {
        if (GateVenue.FromOrderText(o.Str("text")) is not { } clientOrderId)
        {
            // An order this node did not place. Somebody else's, or one the venue raised itself.
            return;
        }

        Order? order = Services.Cache.Order(clientOrderId);
        InstrumentId instrumentId = order?.InstrumentId ?? GateVenue.ToInstrumentId(o.Str("currency_pair"));
        if (Find(instrumentId) is not { } instrument)
        {
            return;
        }

        StrategyId strategyId = order?.StrategyId ?? new StrategyId("EXTERNAL");
        VenueOrderId venueOrderId = new(o.Str("id"));
        UnixNanos ts = o.FractionalMilliseconds("update_time_ms");

        switch (o.Str("event"))
        {
            case "put":
                GenerateOrderAccepted(strategyId, instrumentId, clientOrderId, venueOrderId, ts);
                break;

            case "update":
                GenerateOrderUpdated(
                    strategyId,
                    instrumentId,
                    clientOrderId,
                    venueOrderId,
                    instrument.MakeQuantity(o.Dec("amount")),
                    o.Dec("price") > 0m ? instrument.MakePrice(o.Dec("price")) : null,
                    null,
                    ts);
                break;

            case "finish":
                switch (o.Str("finish_as"))
                {
                    case "filled":
                        // The fills themselves arrive on the user-trades channel with the venue's own trade ids.
                        break;
                    case "ioc":
                    case "stp":
                    case "cancelled":
                        GenerateOrderCanceled(strategyId, instrumentId, clientOrderId, venueOrderId, ts);
                        break;
                    default:
                        GenerateOrderExpired(strategyId, instrumentId, clientOrderId, venueOrderId, ts);
                        break;
                }

                break;
        }
    }

    private void HandleUserTrade(JsonElement t)
    {
        if (GateVenue.FromOrderText(t.Str("text")) is not { } clientOrderId)
        {
            return;
        }

        Order? order = Services.Cache.Order(clientOrderId);
        InstrumentId instrumentId = order?.InstrumentId ?? GateVenue.ToInstrumentId(t.Str("currency_pair"));
        if (Find(instrumentId) is not { } instrument)
        {
            return;
        }

        string tradeId = t.Str("id");
        lock (_gate)
        {
            if (tradeId.Length > 0 && !_seenTrades.Add(tradeId))
            {
                return;
            }
        }

        Quantity filled = instrument.MakeQuantity(t.Dec("amount"));
        Price price = instrument.MakePrice(t.Dec("price"));
        LiquiditySide liquidity = t.Str("role") == "maker" ? LiquiditySide.Maker : LiquiditySide.Taker;
        Currency feeCurrency = t.Str("fee_currency") is { Length: > 0 } code ? Currency.FromCode(code) : instrument.QuoteCurrency;

        GenerateOrderFilled(
            order?.StrategyId ?? new StrategyId("EXTERNAL"),
            instrumentId,
            clientOrderId,
            new VenueOrderId(t.Str("order_id")),
            null,
            new TradeId(tradeId.Length > 0 ? tradeId : Guid.NewGuid().ToString("N")),
            t.Str("side") == "buy" ? OrderSide.Buy : OrderSide.Sell,
            order?.Type ?? OrderType.Market,
            filled,
            price,
            instrument.QuoteCurrency,
            new Money(t.Dec("fee"), feeCurrency),
            liquidity,
            t.FractionalMilliseconds("create_time_ms"));
    }

    private void HandleBalance(JsonElement b)
    {
        if (b.Str("currency") is not { Length: > 0 } code)
        {
            return;
        }

        Currency currency = Currency.FromCode(code);
        decimal total = b.Dec("total");
        decimal available = b.Dec("available");
        List<AccountBalance> all;
        lock (_gate)
        {
            _balances[currency] = AccountBalance.Of(new Money(total, currency), new Money(Math.Max(0m, total - available), currency));
            all = _balances.Values.ToList();
        }

        GenerateAccountState(all, [], reported: true, b.Has("timestamp_ms") ? b.Ms("timestamp_ms") : Clock.Timestamp);
    }

    private async Task PublishAccountStateAsync(CancellationToken ct)
    {
        try
        {
            JsonElement data = await _http.GetSignedAsync("/spot/accounts", null, ct).ConfigureAwait(false);
            if (data.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            List<AccountBalance> all;
            lock (_gate)
            {
                _balances.Clear();
                foreach (JsonElement row in data.EnumerateArray())
                {
                    if (row.Str("currency") is not { Length: > 0 } code)
                    {
                        continue;
                    }

                    Currency currency = Currency.FromCode(code);
                    decimal available = row.Dec("available");
                    decimal locked = row.Dec("locked");
                    _balances[currency] = AccountBalance.Of(
                        new Money(available + locked, currency),
                        new Money(locked, currency));
                }

                all = _balances.Values.ToList();
            }

            GenerateAccountState(all, [], reported: true, Clock.Timestamp);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.LogError(e, "Failed to load Gate spot account state");
        }
    }

    // ----- commands -----

    public override async Task SubmitOrderAsync(SubmitOrder command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        Order order = command.Order;
        if (Find(order.InstrumentId) is not { } instrument)
        {
            GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, $"instrument {order.InstrumentId} unknown to the Gate spot client", Clock.Timestamp);
            return;
        }

        if (GateVenue.ToOrderText(order.ClientOrderId) is not { } text)
        {
            GenerateOrderRejected(
                order.StrategyId,
                order.InstrumentId,
                order.ClientOrderId,
                $"Gate carries a client order id in the order's text field and accepts at most {GateVenue.MaxClientOrderIdLength} "
                + $"characters of letters, digits and \"{GateVenue.ClientOrderIdExtraCharacters}\" there",
                Clock.Timestamp);
            return;
        }

        if (order.Type is not (OrderType.Market or OrderType.Limit))
        {
            GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, $"order type {order.Type} is not supported by the Gate spot client", Clock.Timestamp);
            return;
        }

        if (order.IsQuoteQuantity)
        {
            GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, "Gate spot sizes an order in the base currency, so a quote quantity cannot be sent", Clock.Timestamp);
            return;
        }

        Dictionary<string, object> body = new(StringComparer.Ordinal)
        {
            ["text"] = text,
            ["currency_pair"] = instrument.RawSymbol!.Value,
            ["side"] = order.IsBuy ? "buy" : "sell",
            ["amount"] = Json.Fmt(order.Quantity.Value),
            ["type"] = order.Type == OrderType.Limit ? "limit" : "market",
        };

        if (order.Type == OrderType.Limit)
        {
            body["price"] = Json.Fmt(order.Price!.Value.Value);
            body["time_in_force"] = order.IsPostOnly && order.TimeInForce is not TimeInForce.Ioc
                ? "poc"
                : order.TimeInForce switch
                {
                    TimeInForce.Ioc => "ioc",
                    TimeInForce.Fok => "fok",
                    _ => "gtc",
                };
        }
        else
        {
            // A spot market order carries no price and the venue takes only immediate-or-cancel for one, which is
            // what a market order is: there is nothing left to rest on the book.
            body["time_in_force"] = "ioc";
        }

        GenerateOrderSubmitted(order.StrategyId, order.InstrumentId, order.ClientOrderId, Clock.Timestamp);
        try
        {
            await _http.PostSignedAsync("/spot/orders", body, null, ct).ConfigureAwait(false);
        }
        catch (GateApiException e)
        {
            GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, e.Msg.Length > 0 ? e.Msg : e.Message, Clock.Timestamp);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, e.Message, Clock.Timestamp);
        }
    }

    /// <summary>
    /// Amends a resting order. Gate amends spot orders in place, and the venue's own rules on what that costs are
    /// worth knowing: reducing the quantity alone keeps the order's place in the queue, while changing the price or
    /// growing the quantity moves it to the back of the new price level, and reducing it below what has already
    /// filled cancels it instead.
    /// </summary>
    public override async Task ModifyOrderAsync(ModifyOrder command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.TriggerPrice is not null)
        {
            GenerateOrderModifyRejected(command.StrategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, "the Gate spot client places no triggered orders, so there is no trigger price to change", Clock.Timestamp);
            return;
        }

        if (command.Price is null && command.Quantity is null)
        {
            GenerateOrderModifyRejected(command.StrategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, "Gate needs a price or a quantity to amend an order to", Clock.Timestamp);
            return;
        }

        Dictionary<string, object> body = new(StringComparer.Ordinal);
        if (command.Price is { } price)
        {
            body["price"] = Json.Fmt(price.Value);
        }

        if (command.Quantity is { } quantity)
        {
            body["amount"] = Json.Fmt(quantity.Value);
        }

        GenerateOrderPendingUpdate(command.StrategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, Clock.Timestamp);
        try
        {
            await _http.PatchSignedAsync("/spot/orders/" + Uri.EscapeDataString(OrderPath(command.ClientOrderId, command.VenueOrderId)), body, PairQuery(command.InstrumentId), ct).ConfigureAwait(false);
        }
        catch (GateApiException e)
        {
            GenerateOrderModifyRejected(command.StrategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, e.Msg.Length > 0 ? e.Msg : e.Message, Clock.Timestamp);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            GenerateOrderModifyRejected(command.StrategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, e.Message, Clock.Timestamp);
        }
    }

    public override async Task CancelOrderAsync(CancelOrder command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        try
        {
            await _http
                .DeleteSignedAsync("/spot/orders/" + Uri.EscapeDataString(OrderPath(command.ClientOrderId, command.VenueOrderId)), PairQuery(command.InstrumentId), ct)
                .ConfigureAwait(false);
        }
        catch (GateApiException e)
        {
            Log.LogWarning("Gate spot refused a cancel of {ClientOrderId}: {Reason}", command.ClientOrderId, e.Msg);
        }
    }

    public override async Task CancelAllOrdersAsync(CancelAllOrders command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        try
        {
            await _http.DeleteSignedAsync("/spot/orders", PairQuery(command.InstrumentId), ct).ConfigureAwait(false);
        }
        catch (GateApiException e)
        {
            Log.LogWarning("Gate spot refused a cancel-all for {Instrument}: {Reason}", command.InstrumentId, e.Msg);
        }
    }

    // ----- reports -----

    public override async Task<ExecutionMassStatus?> GenerateMassStatusAsync(UnixNanos? since, CancellationToken ct)
    {
        IReadOnlyList<OrderStatusReport> orders = await GenerateOrderStatusReportsAsync(null, since, null, openOnly: true, ct).ConfigureAwait(false);
        IReadOnlyList<FillReport> fills = await GenerateFillReportsAsync(null, null, since, null, ct).ConfigureAwait(false);
        return new ExecutionMassStatus(ClientId, AccountId, Venue, orders, fills, [], Clock.Timestamp, Guid.NewGuid());
    }

    public override async Task<OrderStatusReport?> GenerateOrderStatusReportAsync(InstrumentId instrumentId, ClientOrderId? clientOrderId, VenueOrderId? venueOrderId, CancellationToken ct)
    {
        try
        {
            JsonElement o = await _http
                .GetSignedAsync("/spot/orders/" + Uri.EscapeDataString(OrderPath(clientOrderId, venueOrderId)), PairQuery(instrumentId), ct)
                .ConfigureAwait(false);

            return o.ValueKind == JsonValueKind.Object ? ParseOrder(o) : null;
        }
        catch (GateApiException)
        {
            return null;
        }
    }

    public override async Task<IReadOnlyList<OrderStatusReport>> GenerateOrderStatusReportsAsync(InstrumentId? instrumentId, UnixNanos? start, UnixNanos? end, bool openOnly, CancellationToken ct)
    {
        // The venue needs a pair to list orders for, and lists every pair with resting orders when none is given -
        // at a different endpoint. Only the per-pair one is used here, so a mass status with no instrument asks
        // about the pairs this node holds orders on rather than about the whole account.
        List<OrderStatusReport> reports = new();
        foreach (InstrumentId id in instrumentId is { } one ? [one] : OpenPairs())
        {
            Dictionary<string, string> query = new(StringComparer.Ordinal)
            {
                ["currency_pair"] = GateVenue.ToRawSymbol(id),
                ["status"] = openOnly ? "open" : "finished",
            };

            JsonElement data = await _http.GetSignedAsync("/spot/orders", query, ct).ConfigureAwait(false);
            if (data.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement o in data.EnumerateArray())
            {
                if (ParseOrder(o) is { } report
                    && (start is not { } from || report.TsLast >= from)
                    && (end is not { } to || report.TsLast <= to))
                {
                    reports.Add(report);
                }
            }
        }

        return reports;
    }

    public override async Task<IReadOnlyList<FillReport>> GenerateFillReportsAsync(InstrumentId? instrumentId, VenueOrderId? venueOrderId, UnixNanos? start, UnixNanos? end, CancellationToken ct)
    {
        List<FillReport> fills = new();
        foreach (InstrumentId id in instrumentId is { } one ? [one] : OpenPairs())
        {
            Dictionary<string, string> query = new(StringComparer.Ordinal) { ["currency_pair"] = GateVenue.ToRawSymbol(id) };
            if (venueOrderId is { } v)
            {
                query["order_id"] = v.Value;
            }

            if (start is { } from)
            {
                query["from"] = (from.Value / UnixNanos.NanosPerSecond).ToString(CultureInfo.InvariantCulture);
            }

            if (end is { } to)
            {
                query["to"] = (to.Value / UnixNanos.NanosPerSecond).ToString(CultureInfo.InvariantCulture);
            }

            JsonElement data = await _http.GetSignedAsync("/spot/my_trades", query, ct).ConfigureAwait(false);
            if (data.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement t in data.EnumerateArray())
            {
                if (Find(GateVenue.ToInstrumentId(t.Str("currency_pair"))) is not { } instrument)
                {
                    continue;
                }

                Currency feeCurrency = t.Str("fee_currency") is { Length: > 0 } code ? Currency.FromCode(code) : instrument.QuoteCurrency;
                fills.Add(new FillReport(
                    AccountId,
                    instrument.Id,
                    new VenueOrderId(t.Str("order_id")),
                    new TradeId(t.Str("id")),
                    t.Str("side") == "buy" ? OrderSide.Buy : OrderSide.Sell,
                    instrument.MakeQuantity(t.Dec("amount")),
                    instrument.MakePrice(t.Dec("price")),
                    new Money(t.Dec("fee"), feeCurrency),
                    t.Str("role") == "maker" ? LiquiditySide.Maker : LiquiditySide.Taker,
                    t.FractionalMilliseconds("create_time_ms"),
                    Clock.Timestamp,
                    Guid.NewGuid(),
                    GateVenue.FromOrderText(t.Str("text"))));
            }
        }

        return fills;
    }

    /// <summary>
    /// A cash account holds balances and never a position, so there is nothing here to report. Declared rather than
    /// inherited so that the parity table records a deliberate empty answer instead of a venue nobody taught.
    /// </summary>
    public override Task<IReadOnlyList<PositionStatusReport>> GeneratePositionStatusReportsAsync(InstrumentId? instrumentId, UnixNanos? start, UnixNanos? end, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PositionStatusReport>>([]);

    // ----- helpers -----

    private Instrument? Find(InstrumentId id) => _instruments.Find(id) ?? Services.Cache.Instrument(id);

    private static Dictionary<string, string> PairQuery(InstrumentId id) =>
        new(StringComparer.Ordinal) { ["currency_pair"] = GateVenue.ToRawSymbol(id) };

    /// <summary>The pairs this node has orders on, which is what a report with no instrument can ask about.</summary>
    private IReadOnlyList<InstrumentId> OpenPairs() =>
        [.. Services.Cache.OrdersOpen(Venue).Select(o => o.InstrumentId).Distinct()];

    /// <summary>
    /// What goes in the order's path. The venue takes either its own id or the user's <c>text</c> id there, and the
    /// text id works only while the order is resting - so the venue id is preferred wherever one is known.
    /// </summary>
    private static string OrderPath(ClientOrderId? clientOrderId, VenueOrderId? venueOrderId) =>
        venueOrderId is { } v ? v.Value
        : clientOrderId is { } c && GateVenue.ToOrderText(c) is { } text ? text
        : throw new ArgumentException("Gate needs an order id or a client order id to act on an order.", nameof(venueOrderId));

    private OrderStatusReport? ParseOrder(JsonElement o)
    {
        if (Find(GateVenue.ToInstrumentId(o.Str("currency_pair"))) is not { } instrument)
        {
            return null;
        }

        Quantity size = instrument.MakeQuantity(o.Dec("amount"));
        Quantity left = instrument.MakeQuantity(o.Dec("left"));
        Quantity filled = instrument.MakeQuantity(Math.Max(0m, size.Value - left.Value));
        decimal price = o.Dec("price");
        decimal filledTotal = o.Dec("filled_total");
        bool limit = o.Str("type") == "limit";

        OrderStatus status = o.Str("status") switch
        {
            "open" => filled.Value > 0m ? OrderStatus.PartiallyFilled : OrderStatus.Accepted,
            "closed" => OrderStatus.Filled,
            "cancelled" => OrderStatus.Canceled,
            _ => filled.Value > 0m ? OrderStatus.PartiallyFilled : OrderStatus.Accepted,
        };

        return new OrderStatusReport(
            AccountId,
            instrument.Id,
            GateVenue.FromOrderText(o.Str("text")),
            new VenueOrderId(o.Str("id")),
            o.Str("side") == "buy" ? OrderSide.Buy : OrderSide.Sell,
            limit ? OrderType.Limit : OrderType.Market,
            o.Str("time_in_force") switch
            {
                "ioc" => TimeInForce.Ioc,
                "fok" => TimeInForce.Fok,
                _ => TimeInForce.Gtc,
            },
            status,
            size,
            filled,
            o.FractionalMilliseconds("create_time_ms"),
            o.FractionalMilliseconds("update_time_ms"),
            Clock.Timestamp,
            Guid.NewGuid(),
            limit && price > 0m ? instrument.MakePrice(price) : null,
            null,
            TriggerType.Default,
            null,
            TrailingOffsetType.Price,
            null,
            filled.Value > 0m && filledTotal > 0m ? filledTotal / filled.Value : null,

            // "poc" is the venue's pending-or-cancelled, which is post-only by another name.
            o.Str("time_in_force") == "poc",
            false,
            null,
            null,
            ContingencyType.None,
            null);
    }
}

/// <summary>
/// The private socket, which is the same on all three of Gate's markets: the same three channels under the market's
/// own prefix, opened with a signature over the channel, the event and the time rather than with the REST signature.
/// <para>
/// Written once here because the alternative is three copies of a signing scheme, and a wrong one of those is a
/// socket that connects, reports the subscription refused, and leaves a node trading blind to its own fills.
/// </para>
/// </summary>
internal static class GateUserStream
{
    public const string Orders = "orders";

    public const string UserTrades = "usertrades";

    public const string Balances = "balances";

    /// <summary>
    /// The balances channel on the two derivative markets, which the venue names for the settlement account rather
    /// than for a currency balance.
    /// </summary>
    public const string FuturesBalances = "balances";

    public const string Positions = "positions";

    /// <summary>
    /// What the venue takes to mean "every instrument" in a private channel's payload. Spot takes it on its own;
    /// the derivative markets want the account's numeric user id in front of it, which only a signed request can
    /// supply, so that payload is built where the user id is known.
    /// </summary>
    public const string Everything = "!all";

    public static WebSocketClient Open(
        string wsBase,
        string prefix,
        GateCredentials credentials,
        ILogger? log,
        Func<string, Task> onText,
        Action notifyConnected,
        Action<string> notifyDisconnected,
        IReadOnlyList<string>? channels = null,
        string payloadJson = "[\"" + Everything + "\"]")
    {
        IReadOnlyList<string> wanted = channels ?? [Orders, UserTrades, Balances];
        WebSocketClient? client = null;
        client = new WebSocketClient(
            new WebSocketClientConfig
            {
                Url = new Uri(wsBase),
                PingMessage = GateStream.Ping(prefix),
                PingInterval = GateVenue.DefaultPingInterval,
            },
            log)
        {
            OnText = onText,
            OnConnected = isReconnect =>
            {
                foreach (string channel in wanted)
                {
                    client?.SendText(GateStream.SubscribePrivate(prefix + channel, payloadJson, credentials));
                }

                if (isReconnect)
                {
                    // The drop marked the client disconnected and the socket reconnects by itself: say so, or the
                    // mark stays for good while orders and fills flow.
                    notifyConnected();
                }

                return Task.CompletedTask;
            },
            OnDisconnected = reason =>
            {
                notifyDisconnected(reason);
                return Task.CompletedTask;
            },
        };

        return client;
    }
}

public sealed class GateDataClientFactory : IDataClientFactory
{
    public string Name => "GATE";

    public Type ConfigType => typeof(GateDataClientConfig);

    public IDataClient Create(ClientId clientId, DataClientConfig config, KernelServices services)
    {
        GateDataClientConfig c = config as GateDataClientConfig
            ?? throw new ArgumentException($"Gate needs a {nameof(GateDataClientConfig)}.", nameof(config));

        return c.ProductType switch
        {
            GateProductType.Futures => new GateFuturesDataClient(clientId, c, services),
            GateProductType.Delivery => new GateDeliveryDataClient(clientId, c, services),
            _ => new GateDataClient(clientId, c, services),
        };
    }
}

public sealed class GateExecutionClientFactory : IExecutionClientFactory
{
    public string Name => "GATE";

    public Type ConfigType => typeof(GateExecutionClientConfig);

    public IExecutionClient Create(ClientId clientId, ExecutionClientConfig config, KernelServices services)
    {
        GateExecutionClientConfig c = config as GateExecutionClientConfig
            ?? throw new ArgumentException($"Gate needs a {nameof(GateExecutionClientConfig)}.", nameof(config));

        return c.ProductType switch
        {
            GateProductType.Futures => new GateFuturesExecutionClient(clientId, c, services),
            GateProductType.Delivery => new GateDeliveryExecutionClient(clientId, c, services),
            _ => new GateExecutionClient(clientId, c, services),
        };
    }
}

/// <summary>
/// What Gate is, for anything that has to onboard the venue without reading this source.
/// </summary>
public sealed class GatePlugin : Core.Plugins.IPlugin, IVenuePlugin
{
    public string Id => "bytex.gate";

    /// <summary>
    /// Gate as three families, and the three differ in the things a host cares about rather than in a path: where
    /// they answer, what they cost, whether a position is charged funding, what class of instrument comes back, and
    /// - uniquely on this venue - whether an order can be amended at all.
    /// <para>
    /// The venue has more settled markets than these three. <c>/futures/btc</c> holds one inverse contract,
    /// <c>/futures/usd1</c> nine USD1-settled linear ones, and <c>/delivery/btc</c> answers with an empty list; the
    /// reasons each is not offered are written on <see cref="GateFuturesVenue"/>, where the measurement is.
    /// </para>
    /// </summary>
    public VenueDescriptor Describe() => new()
    {
        Venue = GateVenue.Venue,
        DisplayName = "Gate",

        // Nothing carries an id on this venue, because there is nothing published to carry. Gate runs an API broker
        // programme and names the mechanism on the programme page - an additional channel id, applied for through a
        // business manager - and publishes neither the header's spelling nor its value format in the API reference.
        // An id invented from a third-party client's source would be a magic string nobody here can source.
        BrokerTag = BrokerTag.None,
        BrokerProgramme = BrokerProgramme.MechanismUndisclosed,
        Families =
        [
            new VenueFamily
            {
                Name = "spot",
                InstrumentClasses = [InstrumentClass.Spot],
                PaysFunding = false,
                HttpBase = GateVenue.DefaultHttpBase,
                WsBase = GateVenue.DefaultWsBase,
                Key = GateKey,
                Config = new Dictionary<string, string> { ["productType"] = nameof(GateProductType.Spot) },

                // The venue publishes one rate per pair for both sides of it, and it was "0.2" - a PERCENTAGE - on
                // 2225 of the 2229 pairs listed. Twenty basis points as a fraction.
                DefaultFees = new VenueFees(0.002m, 0.002m),
                Capabilities = new VenueCapabilities
                {
                    LoadOneInstrument = true,
                    ListInstruments = true,
                    BarHistory = true,

                    // Spot is not margined, so nothing is charged funding and there is none to fetch.
                    FundingHistory = false,
                    MarketData = true,
                    Execution = true,
                    AmendOrders = true,
                },
            },
            new VenueFamily
            {
                Name = "futures",
                InstrumentClasses = [InstrumentClass.Swap],
                PaysFunding = true,
                HttpBase = GateFuturesVenue.DefaultHttpBase,
                WsBase = GateFuturesVenue.DefaultWsBase,
                Key = GateKey,
                Config = new Dictionary<string, string> { ["productType"] = nameof(GateProductType.Futures) },

                // The venue's own rate on BTC_USDT was a maker REBATE of -0.0001 and a taker fee of 0.00075, and a
                // rebate is not a fee this declaration can carry: it states a fraction of a trade paid, and the
                // range it is held to starts at zero. So the maker side is declared as nothing paid, which
                // understates the benefit and never understates the cost, and an instrument's own rate - which does
                // carry the rebate - is the truth the moment there is an instrument.
                DefaultFees = new VenueFees(0m, 0.00075m),
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
                Name = "delivery",
                InstrumentClasses = [InstrumentClass.Future],

                // A dated contract settles at expiry. It is not held to spot by a funding payment and the venue
                // publishes no funding fields on it at all - the ticker channel that carries a rate on the
                // perpetual market sends an empty string here.
                PaysFunding = false,
                HttpBase = GateFuturesVenue.DefaultHttpBase,
                WsBase = GateFuturesVenue.DefaultDeliveryWsBase,
                Key = GateKey,
                Config = new Dictionary<string, string> { ["productType"] = nameof(GateProductType.Delivery) },

                // ETH_USDT_20261009 charged a maker rebate of -0.00015 and a taker fee of 0.00025. As above, the
                // rebate cannot be declared, so the maker side reads as nothing paid.
                DefaultFees = new VenueFees(0m, 0.00025m),
                Capabilities = new VenueCapabilities
                {
                    LoadOneInstrument = true,
                    ListInstruments = true,
                    BarHistory = true,
                    FundingHistory = false,
                    MarketData = true,
                    Execution = true,

                    // The one capability that differs between this venue's own markets. Spot amends with PATCH and
                    // perpetual futures with PUT; the delivery surface has no amend endpoint of any kind - no PUT,
                    // no PATCH, no batch - so an order here is cancelled and replaced or not changed at all.
                    AmendOrders = false,
                },
            },
        ],
    };

    /// <summary>
    /// Two parts, and that is the whole key. Gate signs with an API key and a secret: there is no passphrase and no
    /// key version, which is worth declaring rather than leaving a host to infer from the absence of a third part -
    /// the three venues either side of this one all have one.
    /// </summary>
    private static VenueKey GateKey => new()
    {
        Parts =
        [
            new VenueKeyPart("API key", GateVenue.EnvApiKey, Secret: false),
            new VenueKeyPart("API secret", GateVenue.EnvApiSecret, Secret: true),
        ],
    };

    public string Version => typeof(GatePlugin).Assembly.GetName().Version?.ToString() ?? "0";

    public void Register(Core.Plugins.IPluginRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.AddDataClientFactory(new GateDataClientFactory());
        registry.AddExecutionClientFactory(new GateExecutionClientFactory());
    }
}
