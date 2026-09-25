using System.Globalization;
using System.Text.Json;
using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Model.Reports;
using Bytex.Live.Network;
using Microsoft.Extensions.Logging;

namespace Bytex.Adapters.Kucoin;

/// <summary>
/// Order routing and execution reporting for KuCoin's perpetual futures market.
/// <para>
/// Separate from the spot client for the same reason the data clients are separate: a different host, different
/// endpoints, different message shapes, and orders counted in contracts rather than base currency. What it shares
/// with spot is the key and the shape of the envelope.
/// </para>
/// <para>
/// Three things this market requires that spot does not, and all three are hidden here:
/// </para>
/// <para>
/// Every order carries a leverage. The venue uses it to work out the margin to freeze, and it has no default - so
/// the configured leverage supplies one, and it is 1 unless somebody says otherwise. A
/// silent default of anything higher would lever a position nobody asked to lever.
/// </para>
/// <para>
/// Sizes cross in contracts. Everything above the adapter counts in base currency, so a quantity is divided by the
/// contract size on the way out and multiplied by it on the way back - orders, fills and positions alike.
/// </para>
/// <para>
/// And this account holds positions, where a spot account holds only balances.
/// </para>
/// </summary>
public sealed class KucoinFuturesExecutionClient : ExecutionClientBase
{
    private readonly KucoinExecutionClientConfig _config;
    private readonly KucoinHttp _http;
    private readonly KucoinFuturesInstrumentProvider _instruments;
    private readonly HashSet<string> _seenTrades = new(StringComparer.Ordinal);
    private readonly Dictionary<Currency, AccountBalance> _balances = new();
    private readonly Dictionary<string, ClientOrderId> _stopVenueIds = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private WebSocketClient? _ws;

    public KucoinFuturesExecutionClient(ClientId clientId, KucoinExecutionClientConfig config, KernelServices services)
        : base(
            clientId,
            KucoinVenue.Venue,
            new AccountId($"{KucoinVenue.Venue}-FUTURES"),
            AccountType.Margin,
            null,
            OmsType.Netting,
            services)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        if (_config.ProductType != KucoinProductType.Futures)
        {
            throw new ArgumentException(
                $"This client trades KuCoin's futures market and the configuration says {_config.ProductType}. "
                + "The spot market is KucoinExecutionClient.",
                nameof(config));
        }

        _http = new KucoinHttp(config, Log, requireCredentials: true);
        _instruments = new KucoinFuturesInstrumentProvider(_http, config.InstrumentProvider, Log);
    }

    public KucoinFuturesInstrumentProvider Instruments => _instruments;

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

        (Uri first, TimeSpan ping) = await KucoinStream.AddressAsync(_http, privateStream: true, _config.BaseUrlWs, ct).ConfigureAwait(false);
        Uri? prepared = first;
        _ws = new WebSocketClient(new WebSocketClientConfig
        {
            Url = new Uri(first.GetLeftPart(UriPartial.Path)),
            UrlProvider = async token =>
            {
                Uri? ready = Interlocked.Exchange(ref prepared, null);
                return ready ?? (await KucoinStream.AddressAsync(_http, privateStream: true, _config.BaseUrlWs, token).ConfigureAwait(false)).Url;
            },
            PingMessage = KucoinStream.PingMessage,
            PingInterval = ping,
        }, Log)
        {
            OnText = HandleMessageAsync,
            OnConnected = isReconnect =>
            {
                foreach (string topic in KucoinFuturesVenue.PrivateTopics)
                {
                    _ws?.SendText(KucoinStream.Subscribe(topic, privateChannel: true));
                }

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

    // ----- helpers -----

    private static string Raw(InstrumentId id) => KucoinFuturesVenue.ToRawSymbol(id);

    private Instrument? Find(InstrumentId id) => _instruments.Find(id) ?? Services.Cache.Instrument(id);

    private Instrument? FindByRaw(string rawSymbol) => Find(KucoinFuturesVenue.ToInstrumentId(rawSymbol));

    // ----- the private stream -----

    private Task HandleMessageAsync(string text)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            JsonElement root = doc.RootElement;
            if (root.Str("type") != "message")
            {
                if (root.Str("type") == "error")
                {
                    Log.LogWarning("KuCoin futures private stream error {Code}: {Data}", root.Str("code"), root.Str("data"));
                }

                return Task.CompletedTask;
            }

            string topic = root.Str("topic");
            JsonElement data = root.GetProperty("data");
            if (topic.StartsWith("/contractMarket/tradeOrders", StringComparison.Ordinal))
            {
                HandleOrderChange(data);
            }
            else if (topic.StartsWith("/contractAccount/wallet", StringComparison.Ordinal))
            {
                HandleWallet(root.Str("subject"), data);
            }
            else if (topic.StartsWith("/contract/position", StringComparison.Ordinal))
            {
                HandlePosition(data);
            }
            else if (topic.StartsWith("/contractMarket/advancedOrders", StringComparison.Ordinal))
            {
                HandleStopOrder(data);
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            Log.LogWarning(e, "KuCoin futures: unreadable private stream message");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// An order's life on this venue: <c>open</c>, <c>match</c>, <c>update</c>, <c>filled</c>, <c>canceled</c>. The
    /// sizes are contracts and become base currency here, as everywhere else.
    /// </summary>
    private void HandleOrderChange(JsonElement o)
    {
        string clientOid = o.Str("clientOid");
        if (clientOid.Length == 0)
        {
            return;
        }

        ClientOrderId clientOrderId = new(clientOid);
        Order? order = Services.Cache.Order(clientOrderId);
        InstrumentId instrumentId = order?.InstrumentId ?? KucoinFuturesVenue.ToInstrumentId(o.Str("symbol"));
        Instrument? instrument = Find(instrumentId);
        if (instrument is null)
        {
            return;
        }

        StrategyId strategyId = order?.StrategyId ?? new StrategyId("EXTERNAL");
        VenueOrderId venueOrderId = new(o.Str("orderId"));
        UnixNanos ts = o.Has("ts") ? o.Ns("ts") : o.Has("orderTime") ? o.Ns("orderTime") : Clock.Timestamp;

        switch (o.Str("type"))
        {
            case "open":
                GenerateOrderAccepted(strategyId, instrumentId, clientOrderId, venueOrderId, ts);
                break;

            case "match":
                HandleMatch(o, order, strategyId, instrumentId, clientOrderId, venueOrderId, instrument, ts);
                break;

            case "update":
                GenerateOrderUpdated(
                    strategyId,
                    instrumentId,
                    clientOrderId,
                    venueOrderId,
                    KucoinFuturesVenue.ToQuantity(instrument, o.Dec("size")),
                    o.Dec("price") > 0m ? instrument.MakePrice(o.Dec("price")) : null,
                    null,
                    ts);
                break;

            case "filled":
                // The venue says an order is done; the fills themselves arrived as matches.
                break;

            case "canceled":
                GenerateOrderCanceled(strategyId, instrumentId, clientOrderId, venueOrderId, ts);
                break;
        }
    }

    private void HandleMatch(JsonElement o, Order? order, StrategyId strategyId, InstrumentId instrumentId, ClientOrderId clientOrderId, VenueOrderId venueOrderId, Instrument instrument, UnixNanos ts)
    {
        string tradeId = o.Str("tradeId");
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

        Quantity filled = KucoinFuturesVenue.ToQuantity(instrument, o.Dec("matchSize"));
        Price price = instrument.MakePrice(o.Dec("matchPrice"));
        OrderSide side = o.Str("side") == "buy" ? OrderSide.Buy : OrderSide.Sell;
        LiquiditySide liquidity = o.Str("liquidity") == "maker" ? LiquiditySide.Maker : LiquiditySide.Taker;

        // The order stream carries no fee on this market either, so the commission comes from the instrument's own
        // rate. A fill report read over REST carries the venue's figure and reconciliation replaces this with it.
        Money commission = instrument.CalculateCommission(filled, price, liquidity);

        GenerateOrderFilled(
            strategyId,
            instrumentId,
            clientOrderId,
            venueOrderId,
            null,
            new TradeId(tradeId.Length > 0 ? tradeId : Guid.NewGuid().ToString("N")),
            side,
            order?.Type ?? OrderType.Market,
            filled,
            price,
            instrument.QuoteCurrency,
            commission,
            liquidity,
            ts);
    }

    /// <summary>A stop order on this market reports under its venue id, so the engine's id is looked up.</summary>
    private void HandleStopOrder(JsonElement o)
    {
        string venueId = o.Str("orderId");
        ClientOrderId? clientOrderId;
        lock (_gate)
        {
            clientOrderId = _stopVenueIds.TryGetValue(venueId, out ClientOrderId held) ? held : null;
        }

        if (clientOrderId is not { } id)
        {
            return;
        }

        Order? order = Services.Cache.Order(id);
        if (order is null)
        {
            return;
        }

        UnixNanos ts = o.Has("ts") ? o.Ns("ts") : Clock.Timestamp;
        if (o.Str("type") == "cancel")
        {
            GenerateOrderCanceled(order.StrategyId, order.InstrumentId, id, new VenueOrderId(venueId), ts);
        }
    }

    private void HandleWallet(string subject, JsonElement w)
    {
        if (!w.Has("currency"))
        {
            return;
        }

        Currency currency = Currency.FromCode(w.Str("currency"));
        decimal total = w.Has("walletBalance") ? w.Dec("walletBalance") : w.Dec("availableBalance");
        decimal available = w.Has("availableBalance") ? w.Dec("availableBalance") : total;
        List<AccountBalance> all;
        lock (_gate)
        {
            _balances[currency] = AccountBalance.Of(new Money(total, currency), new Money(Math.Max(0m, total - available), currency));
            all = _balances.Values.ToList();
        }

        GenerateAccountState(all, [], reported: true, w.Has("timestamp") ? w.Ms("timestamp") : Clock.Timestamp);
        Log.LogDebug("KuCoin futures wallet {Subject}: {Currency} {Total}", subject, currency.Code, total);
    }

    private void HandlePosition(JsonElement p)
    {
        if (!p.Has("symbol"))
        {
            return;
        }

        Instrument? instrument = FindByRaw(p.Str("symbol"));
        if (instrument is null)
        {
            return;
        }

        decimal contracts = p.Has("currentQty") ? p.Dec("currentQty") : 0m;
        Log.LogDebug(
            "KuCoin futures position {Instrument}: {Quantity}",
            instrument.Id,
            KucoinFuturesVenue.ToQuantity(instrument, Math.Abs(contracts)));
    }

    private async Task PublishAccountStateAsync(CancellationToken ct)
    {
        try
        {
            List<AccountBalance> all = new();
            lock (_gate)
            {
                _balances.Clear();
            }

            foreach (string currency in KucoinFuturesVenue.SettlementCurrencies)
            {
                JsonElement data = await _http
                    .GetSignedAsync("/api/v1/account-overview", new Dictionary<string, string> { ["currency"] = currency }, ct)
                    .ConfigureAwait(false);

                if (data.ValueKind != JsonValueKind.Object || !data.Has("accountEquity"))
                {
                    continue;
                }

                Currency c = Currency.FromCode(currency);
                decimal equity = data.Dec("accountEquity");
                decimal available = data.Has("availableBalance") ? data.Dec("availableBalance") : equity;
                lock (_gate)
                {
                    _balances[c] = AccountBalance.Of(new Money(equity, c), new Money(Math.Max(0m, equity - available), c));
                    all = _balances.Values.ToList();
                }
            }

            GenerateAccountState(all, [], reported: true, Clock.Timestamp);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.LogError(e, "Failed to load KuCoin futures account state");
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
            GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, $"instrument {order.InstrumentId} unknown to the KuCoin futures client", Clock.Timestamp);
            return;
        }

        if (order.ClientOrderId.Value.Length > KucoinVenue.MaxClientOrderIdLength)
        {
            GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, $"KuCoin accepts client order ids of at most {KucoinVenue.MaxClientOrderIdLength} characters", Clock.Timestamp);
            return;
        }

        bool stop = order.Type is OrderType.StopMarket or OrderType.StopLimit or OrderType.MarketIfTouched or OrderType.LimitIfTouched;
        if (order.Type is not (OrderType.Market or OrderType.Limit) && !stop)
        {
            GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, $"order type {order.Type} is not supported by KuCoin futures", Clock.Timestamp);
            return;
        }

        if (order.IsQuoteQuantity)
        {
            GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, "KuCoin futures sizes an order in contracts, so a quote quantity cannot be sent", Clock.Timestamp);
            return;
        }

        Dictionary<string, object> body;
        try
        {
            body = Body(order, instrument, order.Quantity, order.Price, order.TriggerPrice);
        }
        catch (ArgumentException e)
        {
            // A quantity that is not a whole number of contracts, which the engine should have rounded already.
            GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, e.Message, Clock.Timestamp);
            return;
        }

        GenerateOrderSubmitted(order.StrategyId, order.InstrumentId, order.ClientOrderId, Clock.Timestamp);
        try
        {
            JsonElement data = await _http.PostSignedAsync("/api/v1/orders", body, ct).ConfigureAwait(false);
            if (stop && data.Str("orderId") is { Length: > 0 } stopId)
            {
                // A stop order's stream messages name the venue id only, so it is learnt here from the answer.
                lock (_gate)
                {
                    _stopVenueIds[stopId] = order.ClientOrderId;
                }

                GenerateOrderAccepted(order.StrategyId, order.InstrumentId, order.ClientOrderId, new VenueOrderId(stopId), Clock.Timestamp);
            }
        }
        catch (KucoinApiException e)
        {
            GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, e.Msg.Length > 0 ? e.Msg : e.Message, Clock.Timestamp);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, e.Message, Clock.Timestamp);
        }
    }

    /// <summary>
    /// The request body of an order. Two things here are this market's own: the size is a whole number of contracts,
    /// and a leverage has to be stated because the venue freezes margin against it and supplies no default.
    /// </summary>
    private Dictionary<string, object> Body(Order order, Instrument instrument, Quantity quantity, Price? price, Price? trigger)
    {
        Dictionary<string, object> body = new(StringComparer.Ordinal)
        {
            ["clientOid"] = order.ClientOrderId.Value,
            ["symbol"] = instrument.RawSymbol!.Value,
            ["side"] = order.IsBuy ? "buy" : "sell",
            ["leverage"] = _config.Leverage ?? KucoinFuturesVenue.DefaultLeverage,
            ["size"] = KucoinFuturesVenue.ToContracts(instrument, quantity),
        };

        if (order.IsReduceOnly)
        {
            body["reduceOnly"] = true;
        }

        bool limit = order.Type is OrderType.Limit or OrderType.StopLimit or OrderType.LimitIfTouched;
        body["type"] = limit ? "limit" : "market";
        if (limit)
        {
            body["price"] = Json.Fmt(price!.Value.Value);
            switch (order.TimeInForce)
            {
                case TimeInForce.Ioc:
                    body["timeInForce"] = "IOC";
                    break;
                default:
                    body["timeInForce"] = "GTC";
                    break;
            }

            if (order.IsPostOnly && order.TimeInForce is not TimeInForce.Ioc)
            {
                body["postOnly"] = true;
            }
        }

        if (trigger is { } stopPrice)
        {
            // "up" triggers when the price rises to the stop, "down" when it falls to it. A sell stop and a buy
            // if-touched wait for a fall; a buy stop and a sell if-touched wait for a rise. Spot calls the same two
            // directions "loss" and "entry", which is why neither name is shared between the clients.
            bool waitsForARise = order.Type switch
            {
                OrderType.StopMarket or OrderType.StopLimit => order.IsBuy,
                _ => !order.IsBuy,
            };

            body["stop"] = waitsForARise ? "up" : "down";
            body["stopPrice"] = Json.Fmt(stopPrice.Value);
            body["stopPriceType"] = KucoinFuturesVenue.StopPriceType;
        }

        return body;
    }

    public override Task ModifyOrderAsync(ModifyOrder command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        // The venue has no amend on this market at all - not for plain orders, which spot can amend, and not for
        // stops. Saying so is the honest answer; a cancel-and-replace here would leave a perpetual position
        // unprotected for the length of two requests without the caller having asked for that.
        GenerateOrderModifyRejected(
            Services.Cache.Order(command.ClientOrderId)?.StrategyId ?? new StrategyId("EXTERNAL"),
            command.InstrumentId,
            command.ClientOrderId,
            null,
            "KuCoin futures cannot change an order once it is placed; cancel it and submit a new one",
            Clock.Timestamp);

        return Task.CompletedTask;
    }

    public override async Task CancelOrderAsync(CancelOrder command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        try
        {
            await _http
                .DeleteSignedAsync("/api/v1/orders/client-order/" + Uri.EscapeDataString(command.ClientOrderId.Value), null, ct)
                .ConfigureAwait(false);
        }
        catch (KucoinApiException e)
        {
            Log.LogWarning("KuCoin futures refused a cancel of {ClientOrderId}: {Reason}", command.ClientOrderId, e.Msg);
        }
    }

    public override async Task CancelAllOrdersAsync(CancelAllOrders command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        Dictionary<string, string> query = new(StringComparer.Ordinal) { ["symbol"] = Raw(command.InstrumentId) };
        try
        {
            await _http.DeleteSignedAsync("/api/v1/orders", query, ct).ConfigureAwait(false);
        }
        catch (KucoinApiException e)
        {
            Log.LogWarning("KuCoin futures refused a cancel-all for {Instrument}: {Reason}", command.InstrumentId, e.Msg);
        }

        try
        {
            await _http.DeleteSignedAsync("/api/v1/stopOrders", query, ct).ConfigureAwait(false);
        }
        catch (KucoinApiException e)
        {
            Log.LogWarning("KuCoin futures refused a stop cancel-all for {Instrument}: {Reason}", command.InstrumentId, e.Msg);
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
            if (clientOrderId is { } c)
            {
                JsonElement byClient = await _http
                    .GetSignedAsync("/api/v1/orders/byClientOid", new Dictionary<string, string> { ["clientOid"] = c.Value }, ct)
                    .ConfigureAwait(false);

                return byClient.ValueKind == JsonValueKind.Object ? ParseOrder(byClient) : null;
            }

            if (venueOrderId is { } v)
            {
                JsonElement byId = await _http
                    .GetSignedAsync("/api/v1/orders/" + Uri.EscapeDataString(v.Value), null, ct)
                    .ConfigureAwait(false);

                return byId.ValueKind == JsonValueKind.Object ? ParseOrder(byId) : null;
            }
        }
        catch (KucoinApiException)
        {
            return null;
        }

        return null;
    }

    public override async Task<IReadOnlyList<OrderStatusReport>> GenerateOrderStatusReportsAsync(InstrumentId? instrumentId, UnixNanos? start, UnixNanos? end, bool openOnly, CancellationToken ct)
    {
        Dictionary<string, string> query = new(StringComparer.Ordinal) { ["pageSize"] = "200" };
        if (openOnly)
        {
            query["status"] = "active";
        }

        if (instrumentId is { } id)
        {
            query["symbol"] = Raw(id);
        }

        if (start is { } s)
        {
            query["startAt"] = s.ToMilliseconds().ToString(CultureInfo.InvariantCulture);
        }

        if (end is { } e)
        {
            query["endAt"] = e.ToMilliseconds().ToString(CultureInfo.InvariantCulture);
        }

        List<OrderStatusReport> reports = new();
        JsonElement data = await _http.GetSignedAsync("/api/v1/orders", query, ct).ConfigureAwait(false);
        foreach (JsonElement o in Items(data))
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
        Dictionary<string, string> query = new(StringComparer.Ordinal) { ["pageSize"] = "200" };
        if (instrumentId is { } id)
        {
            query["symbol"] = Raw(id);
        }

        if (venueOrderId is { } v)
        {
            query["orderId"] = v.Value;
        }

        if (start is { } s)
        {
            query["startAt"] = s.ToMilliseconds().ToString(CultureInfo.InvariantCulture);
        }

        if (end is { } e)
        {
            query["endAt"] = e.ToMilliseconds().ToString(CultureInfo.InvariantCulture);
        }

        List<FillReport> fills = new();
        JsonElement data = await _http.GetSignedAsync("/api/v1/fills", query, ct).ConfigureAwait(false);
        foreach (JsonElement x in Items(data))
        {
            Instrument? instrument = FindByRaw(x.Str("symbol"));
            if (instrument is null)
            {
                continue;
            }

            Currency feeCurrency = x.Str("feeCurrency").Length > 0 ? Currency.FromCode(x.Str("feeCurrency")) : instrument.QuoteCurrency;
            fills.Add(new FillReport(
                AccountId,
                instrument.Id,
                new VenueOrderId(x.Str("orderId")),
                new TradeId(x.Str("tradeId")),
                x.Str("side") == "buy" ? OrderSide.Buy : OrderSide.Sell,
                KucoinFuturesVenue.ToQuantity(instrument, x.Dec("size")),
                instrument.MakePrice(x.Dec("price")),
                new Money(x.Dec("fee"), feeCurrency),
                x.Str("liquidity") == "maker" ? LiquiditySide.Maker : LiquiditySide.Taker,
                x.Has("tradeTime") ? x.Ns("tradeTime") : x.Ms("createdAt"),
                Clock.Timestamp,
                Guid.NewGuid(),
                null));
        }

        return fills;
    }

    /// <summary>
    /// What the account holds, which a spot account never has. The venue counts a position in contracts and signs it
    /// - negative is short - so the size becomes base currency here and the sign becomes the side.
    /// </summary>
    public override async Task<IReadOnlyList<PositionStatusReport>> GeneratePositionStatusReportsAsync(InstrumentId? instrumentId, UnixNanos? start, UnixNanos? end, CancellationToken ct)
    {
        List<PositionStatusReport> reports = new();
        JsonElement data = await _http.GetSignedAsync("/api/v1/positions", null, ct).ConfigureAwait(false);
        if (data.ValueKind != JsonValueKind.Array)
        {
            return reports;
        }

        foreach (JsonElement p in data.EnumerateArray())
        {
            Instrument? instrument = FindByRaw(p.Str("symbol"));
            if (instrument is null || (instrumentId is { } wanted && instrument.Id != wanted))
            {
                continue;
            }

            decimal contracts = p.Has("currentQty") ? p.Dec("currentQty") : 0m;
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
                KucoinFuturesVenue.ToQuantity(instrument, Math.Abs(contracts)),
                Clock.Timestamp,
                Clock.Timestamp,
                Guid.NewGuid(),
                null,
                p.Has("avgEntryPrice") ? p.Dec("avgEntryPrice") : null));
        }

        return reports;
    }

    /// <summary>The venue pages some answers in an <c>items</c> array and returns others bare.</summary>
    private static IEnumerable<JsonElement> Items(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in data.EnumerateArray())
            {
                yield return item;
            }

            yield break;
        }

        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("items", out JsonElement items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in items.EnumerateArray())
            {
                yield return item;
            }
        }
    }

    private OrderStatusReport? ParseOrder(JsonElement o)
    {
        Instrument? instrument = FindByRaw(o.Str("symbol"));
        if (instrument is null)
        {
            return null;
        }

        string clientOid = o.Str("clientOid");
        bool limit = o.Str("type") == "limit";
        decimal price = o.Dec("price");
        decimal trigger = o.Dec("stopPrice");
        Quantity size = KucoinFuturesVenue.ToQuantity(instrument, o.Dec("size"));
        Quantity filled = KucoinFuturesVenue.ToQuantity(instrument, o.Dec("dealSize"));
        decimal dealValue = o.Dec("dealValue");

        OrderType type = (trigger > 0m, limit) switch
        {
            (true, true) => OrderType.StopLimit,
            (true, false) => OrderType.StopMarket,
            (false, true) => OrderType.Limit,
            _ => OrderType.Market,
        };

        bool active = !o.Has("isActive") || o.Bool("isActive");
        OrderStatus status = !active
            ? (filled.Value >= size.Value && size.Value > 0m ? OrderStatus.Filled : OrderStatus.Canceled)
            : filled.Value > 0m ? OrderStatus.PartiallyFilled
            : OrderStatus.Accepted;

        UnixNanos created = o.Has("createdAt") ? o.Ms("createdAt") : Clock.Timestamp;
        UnixNanos updated = o.Has("updatedAt") ? o.Ms("updatedAt") : created;

        return new OrderStatusReport(
            AccountId,
            instrument.Id,
            clientOid.Length == 0 ? null : new ClientOrderId(clientOid),
            new VenueOrderId(o.Str("id")),
            o.Str("side") == "buy" ? OrderSide.Buy : OrderSide.Sell,
            type,
            o.Str("timeInForce") == "IOC" ? TimeInForce.Ioc : TimeInForce.Gtc,
            status,
            size,
            filled,
            created,
            updated,
            Clock.Timestamp,
            Guid.NewGuid(),
            limit && price > 0m ? instrument.MakePrice(price) : null,
            trigger > 0m ? instrument.MakePrice(trigger) : null,
            TriggerType.Default,
            null,
            TrailingOffsetType.Price,
            null,
            filled.Value > 0m && dealValue > 0m ? dealValue / filled.Value : null,
            o.Has("postOnly") && o.Bool("postOnly"),
            o.Has("reduceOnly") && o.Bool("reduceOnly"),
            null,
            null,
            ContingencyType.None,
            null);
    }
}
