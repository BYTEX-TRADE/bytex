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
/// Order routing and execution reporting for Gate's USDT-settled perpetual contracts.
/// <para>
/// Four things this market requires that spot does not, and all four are hidden here.
/// </para>
/// <para>
/// Sizes cross in CONTRACTS, and the size is SIGNED - a positive size is a buy and a negative one a sell. There is
/// no side field on a futures order at all, so the sign is the side; sending an unsigned size would buy where a
/// strategy meant to sell, which the venue would accept.
/// </para>
/// <para>
/// A market order is a limit order at a price of zero with immediate-or-cancel. The venue has no market type.
/// </para>
/// <para>
/// Leverage is account state, per CONTRACT, and an order carrying one is ignored - so the configured figure is set at
/// the venue before anything trades, as it is on Binance and Bybit. What is this venue's own is that a contract can
/// hold two positions at once in dual mode and the leverage still belongs to the contract rather than to a side:
/// the venue's own announcement of two-side mode says the leverage and the risk limit apply to both positions. So
/// one call per contract sets both legs and there is nothing per-side to set.
/// </para>
/// <para>
/// And this account holds positions, where a spot account holds only balances.
/// </para>
/// </summary>
public class GateFuturesExecutionClient : ExecutionClientBase
{
    private readonly GateExecutionClientConfig _config;
    private readonly GateHttp _http;
    private readonly InstrumentProviderBase _instruments;
    private readonly HashSet<string> _seenTrades = new(StringComparer.Ordinal);
    private readonly Dictionary<Currency, AccountBalance> _balances = new();
    private readonly object _gate = new();
    private WebSocketClient? _ws;

    public GateFuturesExecutionClient(ClientId clientId, GateExecutionClientConfig config, KernelServices services)
        : this(clientId, config, services, GateProductType.Futures)
    {
    }

    /// <summary>
    /// Builds the client for one of the venue's two derivative markets. The product is a constructor argument so a
    /// delivery client cannot be handed a perpetual configuration and send orders to the wrong market.
    /// </summary>
    protected GateFuturesExecutionClient(ClientId clientId, GateExecutionClientConfig config, KernelServices services, GateProductType product)
        : base(
            clientId,
            GateVenue.Venue,
            new AccountId($"{GateVenue.Venue}-{product.ToString().ToUpperInvariant()}"),
            AccountType.Margin,
            Currency.FromCode(GateFuturesVenue.Settle.ToUpperInvariant()),
            OmsType.Netting,
            services)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        if (_config.ProductType != product)
        {
            throw new ArgumentException(
                $"This client trades Gate's {product} market and the configuration says {_config.ProductType}.",
                nameof(config));
        }

        if (_config.Leverage is { } leverage && leverage <= 0m)
        {
            // Zero is not "no leverage" on this venue: it is the value that switches the contract to CROSS margin,
            // and cross margin wants a separate ceiling in `cross_leverage_limit` that one decimal cannot carry.
            // Accepting it would put a strategy written for a leverage nobody chose onto cross margin at whatever
            // ceiling the account was last left on, which is the exact failure a configured leverage exists to stop.
            throw new ArgumentOutOfRangeException(
                nameof(config),
                leverage,
                $"Gate reads a leverage of {GateFuturesVenue.CrossMarginLeverage} as a switch to cross margin, whose "
                + $"ceiling travels in a separate {GateFuturesVenue.CrossLeverageLimitParameter} this field cannot "
                + "carry. Configure a positive leverage for isolated margin, or leave it unset to trade at whatever "
                + "the account already holds.");
        }

        _http = new GateHttp(config, Log, requireCredentials: true);
        _instruments = product == GateProductType.Delivery
            ? new GateDeliveryInstrumentProvider(_http, config.InstrumentProvider, Log)
            : new GateFuturesInstrumentProvider(_http, config.InstrumentProvider, Log);
    }

    public InstrumentProviderBase Instruments => _instruments;

    /// <summary>Which derivative market this client trades, which decides every path it asks for.</summary>
    protected GateProductType Product => _config.ProductType;

    /// <summary>The path prefix of this client's market.</summary>
    protected string Prefix => GateFuturesVenue.Prefix(Product);

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

        string? user = await PublishAccountStateAsync(ct).ConfigureAwait(false);
        await ApplyLeverageAsync(ct).ConfigureAwait(false);

        // The private channels here want the account's own numeric user id in front of the instrument, where spot
        // takes the instrument alone. It is not in the key and cannot be configured, so it comes off the account
        // the client has just read; without it the socket is not opened rather than opened and silent.
        if (user is { Length: > 0 })
        {
            _ws = GateUserStream.Open(
                GateVenue.WsBase(_config),
                GateStream.FuturesPrefix,
                GateVenue.Credentials(_config),
                Log,
                HandleMessageAsync,
                NotifyConnected,
                NotifyDisconnected,
                [GateUserStream.Orders, GateUserStream.UserTrades, GateUserStream.FuturesBalances, GateUserStream.Positions],
                $"[{JsonSerializer.Serialize(user)},\"{GateUserStream.Everything}\"]");

            await _ws.ConnectAsync(ct).ConfigureAwait(false);
        }
        else
        {
            Log.LogWarning(
                "Gate {Product}: the account did not name a user id, so its private channels cannot be opened. Orders "
                + "and fills will be seen only by reconciliation.",
                Product);
        }

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
    /// Sets the configured leverage at the venue, per contract, before anything is traded. On this venue leverage is
    /// account state and an order carrying one is ignored, so a strategy written for 3x would otherwise be traded at
    /// whatever the account was last left on - and would backtest and paper at 3x while going live at something
    /// else, with nothing saying so.
    /// <para>
    /// One call per contract, and it covers both legs of a dual position: the leverage belongs to the contract on
    /// this venue rather than to a side.
    /// </para>
    /// <para>
    /// A refusal is logged and does not stop the node. It is usually the venue saying the account is not entitled to
    /// the figure asked for, which a node cannot fix and a person needs to read.
    /// </para>
    /// </summary>
    private async Task ApplyLeverageAsync(CancellationToken ct)
    {
        if (_config.Leverage is not { } leverage)
        {
            return;
        }

        string value = leverage.ToString(CultureInfo.InvariantCulture);
        IReadOnlyList<Instrument> tradable =
            [.. Services.Cache.Instruments(Venue).Concat(_instruments.GetAll()).DistinctBy(i => i.Id)];

        foreach (Instrument instrument in tradable)
        {
            try
            {
                await _http.PostSignedAsync(
                    $"{Prefix}/positions/{Uri.EscapeDataString(instrument.RawSymbol!.Value)}/leverage",
                    null,
                    new Dictionary<string, string>(StringComparer.Ordinal) { [GateFuturesVenue.LeverageParameter] = value },
                    ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Log.LogError(
                    e,
                    "Gate refused {Leverage}x on {Instrument}; it will trade at whatever the account is set to",
                    leverage,
                    instrument.Id);
            }
        }
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
                    "Gate {Product} refused the {Channel} private subscription with {Code}: {Message}",
                    Product,
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
                case GateStream.FuturesPrefix + GateUserStream.Orders:
                    Each(result, HandleOrderChange);
                    break;
                case GateStream.FuturesPrefix + GateUserStream.UserTrades:
                    Each(result, HandleUserTrade);
                    break;
                case GateStream.FuturesPrefix + GateUserStream.FuturesBalances:
                    Each(result, HandleBalance);
                    break;
                case GateStream.FuturesPrefix + GateUserStream.Positions:
                    Each(result, HandlePosition);
                    break;
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            Log.LogWarning(e, "Gate {Product}: unreadable private stream message", Product);
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
    /// An order's life: the venue sends the whole order each time, with <c>status</c> <c>open</c> or
    /// <c>finished</c> and, when it is finished, a <c>finish_as</c> saying how. There is no separate accepted
    /// message, so the first <c>open</c> is the acceptance.
    /// </summary>
    private void HandleOrderChange(JsonElement o)
    {
        if (GateVenue.FromOrderText(o.Str("text")) is not { } clientOrderId)
        {
            return;
        }

        Order? order = Services.Cache.Order(clientOrderId);
        InstrumentId instrumentId = order?.InstrumentId ?? GateFuturesVenue.ToInstrumentId(o.Str("contract"));
        if (Find(instrumentId) is not { } instrument)
        {
            return;
        }

        StrategyId strategyId = order?.StrategyId ?? new StrategyId("EXTERNAL");
        VenueOrderId venueOrderId = new(o.Str("id"));
        UnixNanos ts = o.Has("finish_time") ? o.FractionalSeconds("finish_time") : o.FractionalSeconds("create_time");

        if (o.Str("status") == "open")
        {
            GenerateOrderAccepted(strategyId, instrumentId, clientOrderId, venueOrderId, ts);
            return;
        }

        switch (o.Str("finish_as"))
        {
            case "filled":
                // The fills arrive on the user-trades channel with the venue's own trade ids.
                break;
            case "cancelled":
            case "ioc":
            case "auto_deleveraged":
            case "reduce_only":
            case "position_closed":
                GenerateOrderCanceled(strategyId, instrumentId, clientOrderId, venueOrderId, ts);
                break;
            case "liquidated":
            case "reduce_out":
                GenerateOrderCanceled(strategyId, instrumentId, clientOrderId, venueOrderId, ts);
                break;
            default:
                GenerateOrderExpired(strategyId, instrumentId, clientOrderId, venueOrderId, ts);
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
        InstrumentId instrumentId = order?.InstrumentId ?? GateFuturesVenue.ToInstrumentId(t.Str("contract"));
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

        // Signed contracts, as on every size this market reports: the sign is the side.
        decimal contracts = t.Dec("size");
        Quantity filled = GateFuturesVenue.ToQuantity(instrument, Math.Abs(contracts));
        Price price = instrument.MakePrice(t.Dec("price"));
        LiquiditySide liquidity = t.Str("role") == "maker" ? LiquiditySide.Maker : LiquiditySide.Taker;

        GenerateOrderFilled(
            order?.StrategyId ?? new StrategyId("EXTERNAL"),
            instrumentId,
            clientOrderId,
            new VenueOrderId(t.Str("order_id")),
            null,
            new TradeId(tradeId.Length > 0 ? tradeId : Guid.NewGuid().ToString("N")),
            contracts >= 0m ? OrderSide.Buy : OrderSide.Sell,
            order?.Type ?? OrderType.Market,
            filled,
            price,
            instrument.SettlementCurrency,

            // The stream carries no fee on this market, so the commission comes from the instrument's own rate. A
            // fill read over REST carries the venue's figure and reconciliation replaces this with it.
            instrument.CalculateCommission(filled, price, liquidity),
            liquidity,
            t.FractionalSeconds("create_time"));
    }

    private void HandleBalance(JsonElement b)
    {
        if (!b.Has("balance"))
        {
            return;
        }

        Currency currency = Currency.FromCode(GateFuturesVenue.Settle.ToUpperInvariant());
        decimal total = b.Dec("balance");
        List<AccountBalance> all;
        lock (_gate)
        {
            _balances[currency] = AccountBalance.Of(new Money(total, currency), new Money(0m, currency));
            all = _balances.Values.ToList();
        }

        GenerateAccountState(all, [], reported: true, b.Has("time_ms") ? b.Ms("time_ms") : Clock.Timestamp);
    }

    private void HandlePosition(JsonElement p)
    {
        if (Find(GateFuturesVenue.ToInstrumentId(p.Str("contract"))) is not { } instrument)
        {
            return;
        }

        Log.LogDebug(
            "Gate {Product} position {Instrument}: {Quantity}",
            Product,
            instrument.Id,
            GateFuturesVenue.ToQuantity(instrument, Math.Abs(p.Dec("size"))));
    }

    /// <summary>
    /// Reads the settlement account and publishes it, answering with the account's numeric user id - which the
    /// private channels need and nothing else supplies.
    /// </summary>
    private async Task<string?> PublishAccountStateAsync(CancellationToken ct)
    {
        try
        {
            JsonElement data = await _http.GetSignedAsync(Prefix + "/accounts", null, ct).ConfigureAwait(false);
            if (data.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            Currency currency = Currency.FromCode(data.Str("currency") is { Length: > 0 } code ? code : GateFuturesVenue.Settle.ToUpperInvariant());
            decimal total = data.Dec("total");
            decimal available = data.Dec("available");
            List<AccountBalance> all;
            lock (_gate)
            {
                _balances[currency] = AccountBalance.Of(
                    new Money(total, currency),
                    new Money(Math.Max(0m, total - available), currency));
                all = _balances.Values.ToList();
            }

            GenerateAccountState(all, [], reported: true, Clock.Timestamp);
            return data.Str("user");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.LogError(e, "Failed to load Gate {Product} account state", Product);
            return null;
        }
    }

    // ----- commands -----

    public override async Task SubmitOrderAsync(SubmitOrder command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        Order order = command.Order;
        if (Find(order.InstrumentId) is not { } instrument)
        {
            GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, $"instrument {order.InstrumentId} unknown to the Gate {Product} client", Clock.Timestamp);
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
            GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, $"order type {order.Type} is not supported by the Gate {Product} client", Clock.Timestamp);
            return;
        }

        if (order.IsQuoteQuantity)
        {
            GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, $"Gate {Product} sizes an order in contracts, so a quote quantity cannot be sent", Clock.Timestamp);
            return;
        }

        long contracts;
        try
        {
            contracts = GateFuturesVenue.ToContracts(instrument, order.Quantity);
        }
        catch (ArgumentException e)
        {
            // A quantity that is not a whole number of contracts, which the engine should have rounded already.
            GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, e.Message, Clock.Timestamp);
            return;
        }

        Dictionary<string, object> body = new(StringComparer.Ordinal)
        {
            ["text"] = text,
            ["contract"] = instrument.RawSymbol!.Value,

            // The sign IS the side on this market: there is no side field on the order at all.
            ["size"] = order.IsBuy ? contracts : -contracts,
        };

        if (order.IsReduceOnly)
        {
            body["reduce_only"] = true;
        }

        if (order.Type == OrderType.Limit)
        {
            body["price"] = Json.Fmt(order.Price!.Value.Value);
            body["tif"] = order.IsPostOnly && order.TimeInForce is not TimeInForce.Ioc
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
            // The venue has no market order type. A price of zero with immediate-or-cancel is one: it crosses what
            // it can and leaves nothing resting, which is what a market order means.
            body["price"] = MarketPrice;
            body["tif"] = "ioc";
        }

        GenerateOrderSubmitted(order.StrategyId, order.InstrumentId, order.ClientOrderId, Clock.Timestamp);
        try
        {
            await _http.PostSignedAsync(Prefix + "/orders", body, null, ct).ConfigureAwait(false);
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
    /// The price that makes an order a market order on this venue. Zero, with immediate-or-cancel; it is a named
    /// constant because a literal zero in an order body reads like a bug rather than like the venue's own rule.
    /// </summary>
    private const string MarketPrice = "0";

    /// <summary>
    /// Amends a resting order. The venue takes a new size, a new price, or both, and its own rules are worth
    /// knowing: the side cannot change, a size at or below what has already filled cancels the order instead, and
    /// growing a reduce-only order may cancel other reduce-only orders on the same position.
    /// </summary>
    public override async Task ModifyOrderAsync(ModifyOrder command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.TriggerPrice is not null)
        {
            GenerateOrderModifyRejected(command.StrategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, $"the Gate {Product} client places no triggered orders, so there is no trigger price to change", Clock.Timestamp);
            return;
        }

        if (command.Price is null && command.Quantity is null)
        {
            GenerateOrderModifyRejected(command.StrategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, "Gate needs a price or a size to amend an order to", Clock.Timestamp);
            return;
        }

        Dictionary<string, object> body = new(StringComparer.Ordinal);
        if (command.Price is { } price)
        {
            body["price"] = Json.Fmt(price.Value);
        }

        if (command.Quantity is { } quantity)
        {
            if (Find(command.InstrumentId) is not { } instrument)
            {
                GenerateOrderModifyRejected(command.StrategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, $"instrument {command.InstrumentId} unknown to the Gate {Product} client", Clock.Timestamp);
                return;
            }

            // The amended size keeps the ORDER's side, which the venue refuses to change, so it is read off the
            // order this node holds rather than guessed from the amendment.
            bool buy = Services.Cache.Order(command.ClientOrderId)?.IsBuy ?? true;
            long contracts;
            try
            {
                contracts = GateFuturesVenue.ToContracts(instrument, quantity);
            }
            catch (ArgumentException e)
            {
                GenerateOrderModifyRejected(command.StrategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, e.Message, Clock.Timestamp);
                return;
            }

            body["size"] = buy ? contracts : -contracts;
        }

        GenerateOrderPendingUpdate(command.StrategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, Clock.Timestamp);
        try
        {
            await _http
                .PutSignedAsync($"{Prefix}/orders/{Uri.EscapeDataString(OrderPath(command.ClientOrderId, command.VenueOrderId))}", body, null, ct)
                .ConfigureAwait(false);
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
                .DeleteSignedAsync($"{Prefix}/orders/{Uri.EscapeDataString(OrderPath(command.ClientOrderId, command.VenueOrderId))}", null, ct)
                .ConfigureAwait(false);
        }
        catch (GateApiException e)
        {
            Log.LogWarning("Gate {Product} refused a cancel of {ClientOrderId}: {Reason}", Product, command.ClientOrderId, e.Msg);
        }
    }

    public override async Task CancelAllOrdersAsync(CancelAllOrders command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        try
        {
            await _http
                .DeleteSignedAsync(
                    Prefix + "/orders",
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["contract"] = Raw(command.InstrumentId) },
                    ct)
                .ConfigureAwait(false);
        }
        catch (GateApiException e)
        {
            Log.LogWarning("Gate {Product} refused a cancel-all for {Instrument}: {Reason}", Product, command.InstrumentId, e.Msg);
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
        try
        {
            JsonElement o = await _http
                .GetSignedAsync($"{Prefix}/orders/{Uri.EscapeDataString(OrderPath(clientOrderId, venueOrderId))}", null, ct)
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
        Dictionary<string, string> query = new(StringComparer.Ordinal)
        {
            ["status"] = openOnly ? "open" : "finished",
        };

        if (instrumentId is { } id)
        {
            query["contract"] = Raw(id);
        }

        List<OrderStatusReport> reports = new();
        JsonElement data = await _http.GetSignedAsync(Prefix + "/orders", query, ct).ConfigureAwait(false);
        if (data.ValueKind != JsonValueKind.Array)
        {
            return reports;
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

        return reports;
    }

    public override async Task<IReadOnlyList<FillReport>> GenerateFillReportsAsync(InstrumentId? instrumentId, VenueOrderId? venueOrderId, UnixNanos? start, UnixNanos? end, CancellationToken ct)
    {
        Dictionary<string, string> query = new(StringComparer.Ordinal);
        if (instrumentId is { } id)
        {
            query["contract"] = Raw(id);
        }

        if (venueOrderId is { } v)
        {
            query["order"] = v.Value;
        }

        List<FillReport> fills = new();
        JsonElement data = await _http.GetSignedAsync(Prefix + "/my_trades", query, ct).ConfigureAwait(false);
        if (data.ValueKind != JsonValueKind.Array)
        {
            return fills;
        }

        foreach (JsonElement t in data.EnumerateArray())
        {
            if (Find(GateFuturesVenue.ToInstrumentId(t.Str("contract"))) is not { } instrument)
            {
                continue;
            }

            UnixNanos at = t.FractionalSeconds("create_time");
            if ((start is { } from && at < from) || (end is { } to && at > to))
            {
                continue;
            }

            decimal contracts = t.Dec("size");
            fills.Add(new FillReport(
                AccountId,
                instrument.Id,
                new VenueOrderId(t.Str("order_id")),
                new TradeId(t.Str("id")),
                contracts >= 0m ? OrderSide.Buy : OrderSide.Sell,
                GateFuturesVenue.ToQuantity(instrument, Math.Abs(contracts)),
                instrument.MakePrice(t.Dec("price")),
                new Money(t.Dec("fee"), instrument.SettlementCurrency),
                t.Str("role") == "maker" ? LiquiditySide.Maker : LiquiditySide.Taker,
                at,
                Clock.Timestamp,
                Guid.NewGuid(),
                GateVenue.FromOrderText(t.Str("text"))));
        }

        return fills;
    }

    /// <summary>
    /// What the account holds, which a spot account never has. The venue counts a position in contracts and SIGNS
    /// it - negative is short - so the size becomes base currency here and the sign becomes the side.
    /// </summary>
    public override async Task<IReadOnlyList<PositionStatusReport>> GeneratePositionStatusReportsAsync(InstrumentId? instrumentId, UnixNanos? start, UnixNanos? end, CancellationToken ct)
    {
        List<PositionStatusReport> reports = new();
        JsonElement data = await _http.GetSignedAsync(Prefix + "/positions", null, ct).ConfigureAwait(false);
        if (data.ValueKind != JsonValueKind.Array)
        {
            return reports;
        }

        foreach (JsonElement p in data.EnumerateArray())
        {
            if (Find(GateFuturesVenue.ToInstrumentId(p.Str("contract"))) is not { } instrument
                || (instrumentId is { } wanted && instrument.Id != wanted))
            {
                continue;
            }

            decimal contracts = p.Dec("size");
            if (contracts == 0m)
            {
                // Flat is still a position the venue lists, and reporting it keeps reconciliation able to close one
                // this node thinks is open.
                reports.Add(new PositionStatusReport(
                    AccountId,
                    instrument.Id,
                    PositionSide.Flat,
                    instrument.MakeQuantity(0m),
                    Clock.Timestamp,
                    Clock.Timestamp,
                    Guid.NewGuid()));
                continue;
            }

            reports.Add(new PositionStatusReport(
                AccountId,
                instrument.Id,
                contracts > 0m ? PositionSide.Long : PositionSide.Short,
                GateFuturesVenue.ToQuantity(instrument, Math.Abs(contracts)),
                Clock.Timestamp,
                Clock.Timestamp,
                Guid.NewGuid(),
                null,
                p.Dec("entry_price") > 0m ? p.Dec("entry_price") : null));
        }

        return reports;
    }

    // ----- helpers -----

    private static string Raw(InstrumentId id) => GateFuturesVenue.ToRawSymbol(id);

    private Instrument? Find(InstrumentId id) => _instruments.Find(id) ?? Services.Cache.Instrument(id);

    /// <summary>
    /// What goes in the order's path. The venue takes either its own id or the user's <c>text</c> id there, and the
    /// text id works only while the order is resting and for a minute after it closes - so the venue id is preferred
    /// wherever one is known.
    /// </summary>
    private static string OrderPath(ClientOrderId? clientOrderId, VenueOrderId? venueOrderId) =>
        venueOrderId is { } v ? v.Value
        : clientOrderId is { } c && GateVenue.ToOrderText(c) is { } text ? text
        : throw new ArgumentException("Gate needs an order id or a client order id to act on an order.", nameof(venueOrderId));

    private OrderStatusReport? ParseOrder(JsonElement o)
    {
        if (Find(GateFuturesVenue.ToInstrumentId(o.Str("contract"))) is not { } instrument)
        {
            return null;
        }

        decimal contracts = o.Dec("size");
        decimal left = o.Dec("left");
        Quantity size = GateFuturesVenue.ToQuantity(instrument, Math.Abs(contracts));
        Quantity filled = GateFuturesVenue.ToQuantity(instrument, Math.Max(0m, Math.Abs(contracts) - Math.Abs(left)));
        decimal price = o.Dec("price");
        decimal fillPrice = o.Dec("fill_price");

        // A price of zero is the market order this client sent as one, not a limit at nothing.
        bool limit = price > 0m;
        OrderStatus status = o.Str("status") == "open"
            ? filled.Value > 0m ? OrderStatus.PartiallyFilled : OrderStatus.Accepted
            : o.Str("finish_as") switch
            {
                "filled" => OrderStatus.Filled,
                "cancelled" or "ioc" or "reduce_only" or "position_closed" => OrderStatus.Canceled,
                _ => OrderStatus.Expired,
            };

        return new OrderStatusReport(
            AccountId,
            instrument.Id,
            GateVenue.FromOrderText(o.Str("text")),
            new VenueOrderId(o.Str("id")),
            contracts >= 0m ? OrderSide.Buy : OrderSide.Sell,
            limit ? OrderType.Limit : OrderType.Market,
            o.Str("tif") switch
            {
                "ioc" => TimeInForce.Ioc,
                "fok" => TimeInForce.Fok,
                _ => TimeInForce.Gtc,
            },
            status,
            size,
            filled,
            o.FractionalSeconds("create_time"),
            o.Has("finish_time") ? o.FractionalSeconds("finish_time") : o.FractionalSeconds("create_time"),
            Clock.Timestamp,
            Guid.NewGuid(),
            limit ? instrument.MakePrice(price) : null,
            null,
            TriggerType.Default,
            null,
            TrailingOffsetType.Price,
            null,
            filled.Value > 0m && fillPrice > 0m ? fillPrice : null,
            o.Str("tif") == "poc",
            o.Bool("is_reduce_only"),
            null,
            null,
            ContingencyType.None,
            null);
    }
}
