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

namespace Bytex.Adapters.Kucoin;

/// <summary>
/// Order routing and execution reporting for a KuCoin spot trading account. Plain orders go through the venue's
/// high-frequency order endpoints; orders with a trigger price are the venue's stop orders, which live in a list of their
/// own until they trigger.
/// </summary>
public sealed class KucoinExecutionClient : ExecutionClientBase
{
    private readonly KucoinExecutionClientConfig _config;
    private readonly KucoinHttp _http;
    private readonly KucoinInstrumentProvider _instruments;
    private readonly HashSet<string> _seenTrades = new(StringComparer.Ordinal);
    private readonly Dictionary<Currency, AccountBalance> _balances = new();

    // The venue cannot modify a stop order, so a modification cancels it and places a new one. The new one needs a client
    // id of its own; the engine goes on knowing the order by the id it gave it. These three maps hold that translation.
    private readonly Dictionary<string, ClientOrderId> _engineIds = new(StringComparer.Ordinal);
    private readonly Dictionary<ClientOrderId, string> _venueOids = new();
    private readonly Dictionary<string, ClientOrderId> _stopVenueIds = new(StringComparer.Ordinal);
    private readonly Dictionary<ClientOrderId, int> _replacements = new();
    private readonly object _gate = new();
    private WebSocketClient? _ws;

    public KucoinExecutionClient(ClientId clientId, KucoinExecutionClientConfig config, KernelServices services)
        : base(clientId, KucoinVenue.Venue, new AccountId($"{KucoinVenue.Venue}-SPOT"), AccountType.Cash, null, OmsType.Netting, services)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _http = new KucoinHttp(config, Log, requireCredentials: true);
        _instruments = new KucoinInstrumentProvider(_http, config.InstrumentProvider, Log);
    }

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
                foreach (string topic in new[] { "/spotMarket/tradeOrdersV2", "/account/balance", "/spotMarket/advancedOrders" })
                {
                    _ws?.SendText(KucoinStream.Subscribe(topic, privateChannel: true));
                }

                if (isReconnect)
                {
                    // The drop marked the client disconnected and the socket reconnects by itself: say so, or the mark stays for good while orders and fills flow.
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

    // ----- Private stream -----

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
                    Log.LogError("KuCoin private stream error {Code}: {Data}", root.Str("code"), root.Str("data"));
                }

                return Task.CompletedTask;
            }

            JsonElement data = root.GetProperty("data");
            switch (root.Str("topic"))
            {
                case "/spotMarket/tradeOrdersV2":
                    HandleOrderChange(data);
                    break;
                case "/spotMarket/advancedOrders":
                    HandleStopOrder(data);
                    break;
                case "/account/balance":
                    HandleBalance(data);
                    break;
            }
        }
        catch (Exception e)
        {
            Log.LogWarning(e, "Failed to parse KuCoin private message: {Text}", LogText.Truncate(text, LogText.MaxMessageLength));
        }

        return Task.CompletedTask;
    }

    private void HandleOrderChange(JsonElement o)
    {
        string clientOid = o.Str("clientOid");
        if (clientOid.Length == 0)
        {
            return;
        }

        ClientOrderId clientOrderId = EngineId(clientOid);
        Order? order = Services.Cache.Order(clientOrderId);
        InstrumentId instrumentId = order?.InstrumentId ?? KucoinVenue.ToInstrumentId(o.Str("symbol"));
        StrategyId strategyId = order?.StrategyId ?? StrategyId.External;
        VenueOrderId venueOrderId = new(o.Str("orderId"));
        UnixNanos ts = o.Has("ts") ? o.Ns("ts") : Clock.Timestamp;

        switch (o.Str("type"))
        {
            case "received":
            case "open":
                // A market order is never "open"; "received" is the only acceptance it gets before it matches.
                if (order is null || order.Status is OrderStatus.Submitted or OrderStatus.Initialized or OrderStatus.Triggered)
                {
                    GenerateOrderAccepted(strategyId, instrumentId, clientOrderId, venueOrderId, ts);
                }

                break;
            case "match":
                HandleMatch(o, order, strategyId, instrumentId, clientOrderId, venueOrderId, ts);
                break;
            case "update":
                if (_instruments.Find(instrumentId) is { } instrument)
                {
                    decimal price = o.Dec("price");
                    GenerateOrderUpdated(strategyId, instrumentId, clientOrderId, venueOrderId, instrument.MakeQuantity(o.Dec("size")), price > 0m ? instrument.MakePrice(price) : null, null, ts);
                }

                break;
            case "canceled":
                GenerateOrderCanceled(strategyId, instrumentId, clientOrderId, venueOrderId, ts);
                break;
        }
    }

    private void HandleMatch(JsonElement o, Order? order, StrategyId strategyId, InstrumentId instrumentId, ClientOrderId clientOrderId, VenueOrderId venueOrderId, UnixNanos ts)
    {
        string tradeId = o.Str("tradeId");
        lock (_gate)
        {
            if (!_seenTrades.Add(venueOrderId.Value + ":" + tradeId))
            {
                return;
            }
        }

        Instrument? instrument = _instruments.Find(instrumentId) ?? Services.Cache.Instrument(instrumentId);
        if (instrument is null)
        {
            return;
        }

        decimal price = o.Dec("matchPrice");
        decimal size = o.Dec("matchSize");
        bool maker = o.Str("liquidity") == "maker";
        // The stream does not carry the fee. It is worked out from the instrument's rate here; the fill reports, which
        // come from REST, carry the venue's own figure.
        Money commission = new(price * size * (maker ? instrument.MakerFee : instrument.TakerFee), instrument.QuoteCurrency);
        GenerateOrderFilled(strategyId, instrumentId, clientOrderId, venueOrderId, null, new TradeId(tradeId),
            o.Str("side") == "buy" ? OrderSide.Buy : OrderSide.Sell, order?.Type ?? (o.Str("orderType") == "market" ? OrderType.Market : OrderType.Limit),
            instrument.MakeQuantity(size), instrument.MakePrice(price), instrument.QuoteCurrency, commission, maker ? LiquiditySide.Maker : LiquiditySide.Taker, ts);
    }

    /// <summary>A stop order before it triggers. Once it triggers the venue turns it into an ordinary order, reported on the order channel.</summary>
    private void HandleStopOrder(JsonElement o)
    {
        VenueOrderId venueOrderId = new(o.Str("orderId"));
        Order? order;
        lock (_gate)
        {
            // Only a stop order that is current: the cancel of one that a modification replaced is this client's own doing
            // and must not close the engine's order.
            order = _stopVenueIds.TryGetValue(venueOrderId.Value, out ClientOrderId id) ? Services.Cache.Order(id) : null;
        }

        if (order is null)
        {
            return;
        }

        UnixNanos ts = o.Has("ts") ? o.Ns("ts") : Clock.Timestamp;
        switch (o.Str("type"))
        {
            case "open" when order.Status is OrderStatus.Submitted or OrderStatus.Initialized:
                GenerateOrderAccepted(order.StrategyId, order.InstrumentId, order.ClientOrderId, venueOrderId, ts);
                break;
            case "cancel":
            case "canceled":
                GenerateOrderCanceled(order.StrategyId, order.InstrumentId, order.ClientOrderId, venueOrderId, ts);
                break;
            case "triggered":
                GenerateOrderTriggered(order.StrategyId, order.InstrumentId, order.ClientOrderId, venueOrderId, ts);
                break;
        }
    }

    private void HandleBalance(JsonElement b)
    {
        Currency currency = Currency.FromCode(b.Str("currency"));
        decimal total = b.Dec("total");
        decimal hold = b.Dec("hold");
        List<AccountBalance> all;
        lock (_gate)
        {
            _balances[currency] = AccountBalance.Of(new Money(total, currency), new Money(Math.Min(total, hold), currency));
            all = _balances.Values.ToList();
        }

        GenerateAccountState(all, [], reported: true, b.Has("time") ? b.Ms("time") : Clock.Timestamp);
    }

    private async Task PublishAccountStateAsync(CancellationToken ct)
    {
        try
        {
            JsonElement data = await _http.GetSignedAsync("/api/v1/accounts", new Dictionary<string, string> { ["type"] = "trade" }, ct).ConfigureAwait(false);
            List<AccountBalance> all;
            lock (_gate)
            {
                _balances.Clear();
                foreach (JsonElement a in data.EnumerateArray())
                {
                    Currency currency = Currency.FromCode(a.Str("currency"));
                    decimal total = a.Dec("balance");
                    _balances[currency] = AccountBalance.Of(new Money(total, currency), new Money(Math.Min(total, a.Dec("holds")), currency));
                }

                all = _balances.Values.ToList();
            }

            GenerateAccountState(all, [], reported: true, Clock.Timestamp);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.LogError(e, "Failed to load KuCoin account state");
        }
    }

    // ----- Commands -----

    public override async Task SubmitOrderAsync(SubmitOrder command, CancellationToken ct)
    {
        Order order = command.Order;
        Instrument? instrument = _instruments.Find(order.InstrumentId) ?? Services.Cache.Instrument(order.InstrumentId);
        if (instrument is null)
        {
            GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, $"instrument {order.InstrumentId} unknown to KuCoin client", Clock.Timestamp);
            return;
        }

        if (order.ClientOrderId.Value.Length > KucoinVenue.MaxClientOrderIdLength)
        {
            GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, $"KuCoin accepts client order ids of at most {KucoinVenue.MaxClientOrderIdLength} characters", Clock.Timestamp);
            return;
        }

        bool stop = IsStopType(order.Type);
        if (order.Type is not (OrderType.Market or OrderType.Limit) && !stop)
        {
            GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, $"order type {order.Type} is not supported by KuCoin spot", Clock.Timestamp);
            return;
        }

        Dictionary<string, object> body = Body(order, instrument, order.ClientOrderId.Value, order.Quantity, order.Price, order.TriggerPrice);
        string path = stop ? "/api/v1/stop-order" : "/api/v1/hf/orders";
        GenerateOrderSubmitted(order.StrategyId, order.InstrumentId, order.ClientOrderId, Clock.Timestamp);
        try
        {
            JsonElement data = await _http.PostSignedAsync(path, body, ct).ConfigureAwait(false);
            if (stop && data.Str("orderId") is { Length: > 0 } stopId)
            {
                // A stop order's stream messages name the venue id only, so the id is learnt here, from the answer.
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

    private static bool IsStopType(OrderType type) => type is OrderType.StopMarket or OrderType.StopLimit or OrderType.MarketIfTouched or OrderType.LimitIfTouched;

    /// <summary>The request body of an order; quantity, price and trigger are passed in so that a replacement can differ from the order it replaces.</summary>
    private Dictionary<string, object> Body(Order order, Instrument instrument, string clientOid, Quantity quantity, Price? price, Price? trigger)
    {
        Dictionary<string, object> body = new()
        {
            ["clientOid"] = clientOid,
            ["symbol"] = instrument.RawSymbol.Value,
            ["side"] = order.IsBuy ? "buy" : "sell",
        };

        bool limit = order.Type is OrderType.Limit or OrderType.StopLimit or OrderType.LimitIfTouched;
        body["type"] = limit ? "limit" : "market";
        if (limit)
        {
            body["price"] = Json.Fmt(price!.Value.Value);
            body["size"] = Json.Fmt(quantity.Value);
            switch (order.TimeInForce)
            {
                case TimeInForce.Ioc:
                    body["timeInForce"] = "IOC";
                    break;
                case TimeInForce.Fok:
                    body["timeInForce"] = "FOK";
                    break;
                case TimeInForce.Gtd when order.ExpireTime is { } expire:
                    body["timeInForce"] = "GTT";
                    body["cancelAfter"] = Math.Max(1, (expire.Value - Clock.Timestamp.Value) / UnixNanos.NanosPerSecond);
                    break;
                default:
                    body["timeInForce"] = "GTC";
                    break;
            }

            if (order.IsPostOnly && order.TimeInForce is not (TimeInForce.Ioc or TimeInForce.Fok))
            {
                body["postOnly"] = true;
            }
        }
        else if (order.IsQuoteQuantity)
        {
            body["funds"] = Json.Fmt(quantity.Value);
        }
        else
        {
            body["size"] = Json.Fmt(quantity.Value);
        }

        if (IsStopType(order.Type))
        {
            // "loss" triggers when the price falls to the stop price, "entry" when it rises to it.
            bool protective = order.Type is OrderType.StopMarket or OrderType.StopLimit;
            bool rises = protective ? order.IsBuy : order.IsSell;
            body["stop"] = rises ? "entry" : "loss";
            body["stopPrice"] = Json.Fmt(trigger!.Value.Value);
        }

        return body;
    }

    /// <summary>The client id the venue knows the order by: the engine's own, or the one a replacement was placed under.</summary>
    private string VenueOid(ClientOrderId id)
    {
        lock (_gate)
        {
            return _venueOids.GetValueOrDefault(id, id.Value);
        }
    }

    private ClientOrderId EngineId(string clientOid)
    {
        lock (_gate)
        {
            return _engineIds.TryGetValue(clientOid, out ClientOrderId id) ? id : new ClientOrderId(clientOid);
        }
    }

    /// <summary>"-r1", "-r2", ... after the engine's id, shortened where needed to stay within the venue's 40 characters.</summary>
    private string NextOid(ClientOrderId id)
    {
        lock (_gate)
        {
            int n = _replacements[id] = _replacements.GetValueOrDefault(id) + 1;
            string suffix = "-r" + n.ToString(CultureInfo.InvariantCulture);
            string stem = id.Value.Length + suffix.Length > KucoinVenue.MaxClientOrderIdLength
                ? id.Value[..(KucoinVenue.MaxClientOrderIdLength - suffix.Length)]
                : id.Value;
            return stem + suffix;
        }
    }

    /// <summary>True while the order is a stop order the venue has not triggered yet: it is then in the stop order list, not among the plain orders.</summary>
    private bool IsUntriggeredStop(ClientOrderId id) =>
        Services.Cache.Order(id) is { Type: OrderType.StopMarket or OrderType.StopLimit or OrderType.MarketIfTouched or OrderType.LimitIfTouched, Status: not (OrderStatus.Triggered or OrderStatus.PartiallyFilled) };

    public override async Task ModifyOrderAsync(ModifyOrder command, CancellationToken ct)
    {
        if (IsUntriggeredStop(command.ClientOrderId))
        {
            await ReplaceStopAsync(command, ct).ConfigureAwait(false);
            return;
        }

        if (command.TriggerPrice is not null)
        {
            GenerateOrderModifyRejected(command.StrategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, "the order has no trigger price left to change: it has triggered or never had one", Clock.Timestamp);
            return;
        }

        Dictionary<string, object> body = new() { ["symbol"] = KucoinVenue.ToRawSymbol(command.InstrumentId), ["clientOid"] = VenueOid(command.ClientOrderId) };
        if (command.Price is { } price)
        {
            body["newPrice"] = Json.Fmt(price.Value);
        }

        if (command.Quantity is { } quantity)
        {
            body["newSize"] = Json.Fmt(quantity.Value);
        }

        GenerateOrderPendingUpdate(command.StrategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, Clock.Timestamp);
        try
        {
            await _http.PostSignedAsync("/api/v1/hf/orders/alter", body, ct).ConfigureAwait(false);
        }
        catch (KucoinApiException e)
        {
            GenerateOrderModifyRejected(command.StrategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, e.Msg.Length > 0 ? e.Msg : e.Message, Clock.Timestamp);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            GenerateOrderModifyRejected(command.StrategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, e.Message, Clock.Timestamp);
        }
    }

    /// <summary>
    /// The venue has no way to change a stop order, so a change is a cancel and a new stop order. Between the two the
    /// position has no stop at the venue, for the length of one request. If the venue refuses the new one, the old one is
    /// placed again; if that fails too the order is reported cancelled, because that is then the truth, and an error says
    /// that the position is unprotected.
    /// </summary>
    private async Task ReplaceStopAsync(ModifyOrder command, CancellationToken ct)
    {
        Order order = Services.Cache.Order(command.ClientOrderId)!;
        Instrument? instrument = _instruments.Find(order.InstrumentId) ?? Services.Cache.Instrument(order.InstrumentId);
        if (instrument is null)
        {
            GenerateOrderModifyRejected(command.StrategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, $"instrument {order.InstrumentId} unknown to KuCoin client", Clock.Timestamp);
            return;
        }

        GenerateOrderPendingUpdate(command.StrategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, Clock.Timestamp);
        List<string> oldVenueIds;
        lock (_gate)
        {
            // Forgotten before the cancel goes out: the stream's "cancel" for it is this client's own doing.
            oldVenueIds = _stopVenueIds.Where(kv => kv.Value == command.ClientOrderId).Select(kv => kv.Key).ToList();
            foreach (string id in oldVenueIds)
            {
                _stopVenueIds.Remove(id);
            }
        }

        try
        {

            await _http.DeleteSignedAsync("/api/v1/stop-order/cancelOrderByClientOid", new Dictionary<string, string> { ["symbol"] = instrument.RawSymbol.Value, ["clientOid"] = VenueOid(command.ClientOrderId) }, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            lock (_gate)
            {
                foreach (string id in oldVenueIds)
                {
                    _stopVenueIds[id] = command.ClientOrderId;
                }
            }

            GenerateOrderModifyRejected(command.StrategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, "the stop order could not be cancelled to be replaced: " + Reason(e), Clock.Timestamp);
            return;
        }

        Quantity quantity = command.Quantity ?? order.Quantity;
        Price? price = command.Price ?? order.Price;
        Price? trigger = command.TriggerPrice ?? order.TriggerPrice;
        try
        {
            VenueOrderId placed = await PlaceStopAsync(order, instrument, quantity, price, trigger, ct).ConfigureAwait(false);
            GenerateOrderUpdated(command.StrategyId, command.InstrumentId, command.ClientOrderId, placed, quantity, price, trigger, Clock.Timestamp);
            return;
        }
        catch (Exception refused) when (refused is not OperationCanceledException)
        {
            try
            {
                VenueOrderId restored = await PlaceStopAsync(order, instrument, order.Quantity, order.Price, order.TriggerPrice, ct).ConfigureAwait(false);
                GenerateOrderModifyRejected(command.StrategyId, command.InstrumentId, command.ClientOrderId, restored, $"the venue refused the new stop order ({Reason(refused)}); the previous one was placed again", Clock.Timestamp);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Log.LogCritical(e, "KuCoin: stop order {ClientOrderId} on {InstrumentId} was cancelled to be replaced, the venue refused the replacement ({Refusal}) and the previous stop could not be placed again. THE POSITION HAS NO STOP AT THE VENUE.", command.ClientOrderId, command.InstrumentId, Reason(refused));
                GenerateOrderCanceled(command.StrategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, Clock.Timestamp);
            }
        }
    }

    private async Task<VenueOrderId> PlaceStopAsync(Order order, Instrument instrument, Quantity quantity, Price? price, Price? trigger, CancellationToken ct)
    {
        string clientOid = NextOid(order.ClientOrderId);
        lock (_gate)
        {
            // Known before the request goes out: the stream may name the new order before the answer arrives.
            _engineIds[clientOid] = order.ClientOrderId;
        }

        JsonElement data = await _http.PostSignedAsync("/api/v1/stop-order", Body(order, instrument, clientOid, quantity, price, trigger), ct).ConfigureAwait(false);
        string stopId = data.Str("orderId");
        lock (_gate)
        {
            _venueOids[order.ClientOrderId] = clientOid;
            _stopVenueIds[stopId] = order.ClientOrderId;
        }

        return new VenueOrderId(stopId);
    }

    private static string Reason(Exception e) => e is KucoinApiException { Msg.Length: > 0 } api ? api.Msg : e.Message;

    public override async Task CancelOrderAsync(CancelOrder command, CancellationToken ct)
    {
        GenerateOrderPendingCancel(command.StrategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, Clock.Timestamp);
        try
        {
            Dictionary<string, string> symbol = new() { ["symbol"] = KucoinVenue.ToRawSymbol(command.InstrumentId) };
            if (IsUntriggeredStop(command.ClientOrderId))
            {
                symbol["clientOid"] = VenueOid(command.ClientOrderId);
                await _http.DeleteSignedAsync("/api/v1/stop-order/cancelOrderByClientOid", symbol, ct).ConfigureAwait(false);
            }
            else
            {
                // A stop order that was replaced and has triggered since is a plain order under its replacement's id.
                await _http.DeleteSignedAsync("/api/v1/hf/orders/client-order/" + Uri.EscapeDataString(VenueOid(command.ClientOrderId)), symbol, ct).ConfigureAwait(false);
            }
        }
        catch (KucoinApiException e)
        {
            GenerateOrderCancelRejected(command.StrategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, e.Msg.Length > 0 ? e.Msg : e.Message, Clock.Timestamp);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            GenerateOrderCancelRejected(command.StrategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, e.Message, Clock.Timestamp);
        }
    }

    public override async Task CancelAllOrdersAsync(CancelAllOrders command, CancellationToken ct)
    {
        if (command.OrderSide is not null)
        {
            await base.CancelAllOrdersAsync(command, ct).ConfigureAwait(false);
            return;
        }

        Dictionary<string, string> symbol = new() { ["symbol"] = KucoinVenue.ToRawSymbol(command.InstrumentId) };
        foreach (string path in new[] { "/api/v1/hf/orders", "/api/v1/stop-order/cancel" })
        {
            try
            {
                await _http.DeleteSignedAsync(path, symbol, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Log.LogError(e, "KuCoin cancel-all ({Path}) failed for {InstrumentId}", path, command.InstrumentId);
            }
        }
    }

    // ----- Reports -----

    public override async Task<ExecutionMassStatus?> GenerateMassStatusAsync(UnixNanos? since, CancellationToken ct)
    {
        IReadOnlyList<OrderStatusReport> orders = await GenerateOrderStatusReportsAsync(null, since, null, openOnly: true, ct).ConfigureAwait(false);
        IReadOnlyList<FillReport> fills = await GenerateFillReportsAsync(null, null, since, null, ct).ConfigureAwait(false);
        return new ExecutionMassStatus(ClientId, AccountId, Venue, orders, fills, [], Clock.Timestamp, Guid.NewGuid());
    }

    public override async Task<OrderStatusReport?> GenerateOrderStatusReportAsync(InstrumentId instrumentId, ClientOrderId? clientOrderId, VenueOrderId? venueOrderId, CancellationToken ct)
    {
        Dictionary<string, string> symbol = new() { ["symbol"] = KucoinVenue.ToRawSymbol(instrumentId) };
        string? path = clientOrderId is { } c ? "/api/v1/hf/orders/client-order/" + Uri.EscapeDataString(VenueOid(c))
            : venueOrderId is { } v ? "/api/v1/hf/orders/" + Uri.EscapeDataString(v.Value)
            : null;
        if (path is null)
        {
            return null;
        }

        try
        {
            JsonElement o = await _http.GetSignedAsync(path, symbol, ct).ConfigureAwait(false);
            return o.ValueKind == JsonValueKind.Object ? ParseOrder(o) : null;
        }
        catch (KucoinApiException)
        {
            return null;
        }
    }

    public override async Task<IReadOnlyList<OrderStatusReport>> GenerateOrderStatusReportsAsync(InstrumentId? instrumentId, UnixNanos? start, UnixNanos? end, bool openOnly, CancellationToken ct)
    {
        List<OrderStatusReport> reports = new();
        foreach (string symbol in await SymbolsAsync(instrumentId, ct).ConfigureAwait(false))
        {
            JsonElement data = await _http.GetSignedAsync("/api/v1/hf/orders/active", new Dictionary<string, string> { ["symbol"] = symbol }, ct).ConfigureAwait(false);
            if (data.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement o in data.EnumerateArray())
            {
                if (ParseOrder(o) is { } report)
                {
                    reports.Add(report);
                }
            }
        }

        Dictionary<string, string> stopQuery = new() { ["pageSize"] = "500" };
        if (instrumentId is { } id)
        {
            stopQuery["symbol"] = KucoinVenue.ToRawSymbol(id);
        }

        JsonElement stops = await _http.GetSignedAsync("/api/v1/stop-order", stopQuery, ct).ConfigureAwait(false);
        if (stops.ValueKind == JsonValueKind.Object && stops.TryGetProperty("items", out JsonElement items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement o in items.EnumerateArray())
            {
                if (ParseOrder(o, stopOrder: true) is { } report)
                {
                    reports.Add(report);
                }
            }
        }

        return reports;
    }

    /// <summary>The venue lists open orders and fills one symbol at a time; without a symbol it is asked which symbols have open orders.</summary>
    private async Task<IReadOnlyList<string>> SymbolsAsync(InstrumentId? instrumentId, CancellationToken ct)
    {
        if (instrumentId is { } id)
        {
            return [KucoinVenue.ToRawSymbol(id)];
        }

        JsonElement data = await _http.GetSignedAsync("/api/v1/hf/orders/active/symbols", null, ct).ConfigureAwait(false);
        return data.ValueKind == JsonValueKind.Object && data.TryGetProperty("symbols", out JsonElement symbols) && symbols.ValueKind == JsonValueKind.Array
            ? symbols.EnumerateArray().Select(s => s.GetString() ?? string.Empty).Where(s => s.Length > 0).ToList()
            : [];
    }

    private OrderStatusReport? ParseOrder(JsonElement o, bool stopOrder = false)
    {
        InstrumentId instrumentId = KucoinVenue.ToInstrumentId(o.Str("symbol"));
        Instrument? instrument = _instruments.Find(instrumentId) ?? Services.Cache.Instrument(instrumentId);
        if (instrument is null)
        {
            return null;
        }

        string clientOid = o.Str("clientOid");
        bool limit = o.Str("type") == "limit";
        decimal price = o.Dec("price");
        decimal trigger = o.Dec("stopPrice");
        decimal size = o.Dec("size");
        decimal filled = o.Dec("dealSize");
        decimal dealFunds = o.Dec("dealFunds");
        OrderType type = (stopOrder || trigger > 0m, limit) switch
        {
            (true, true) => OrderType.StopLimit,
            (true, false) => OrderType.StopMarket,
            (false, true) => OrderType.Limit,
            _ => OrderType.Market,
        };
        OrderStatus status = stopOrder ? OrderStatus.Accepted
            : o.Has("active") && !o.Bool("active") ? (filled >= size && size > 0m ? OrderStatus.Filled : OrderStatus.Canceled)
            : filled > 0m ? OrderStatus.PartiallyFilled
            : OrderStatus.Accepted;
        // A stop order's orderTime is in nanoseconds; plain orders give createdAt in milliseconds.
        UnixNanos created = o.Has("createdAt") ? o.Ms("createdAt") : o.Has("orderTime") ? o.Ns("orderTime") : Clock.Timestamp;
        UnixNanos updated = o.Has("lastUpdatedAt") ? o.Ms("lastUpdatedAt") : created;
        return new OrderStatusReport(AccountId, instrumentId, clientOid.Length == 0 ? null : EngineId(clientOid), new VenueOrderId(o.Str("id")),
            o.Str("side") == "buy" ? OrderSide.Buy : OrderSide.Sell, type, MapTif(o.Str("timeInForce")), status,
            instrument.MakeQuantity(size), instrument.MakeQuantity(filled), created, updated, Clock.Timestamp, Guid.NewGuid(),
            limit && price > 0m ? instrument.MakePrice(price) : null, trigger > 0m ? instrument.MakePrice(trigger) : null, TriggerType.Default, null, TrailingOffsetType.Price, null,
            filled > 0m && dealFunds > 0m ? dealFunds / filled : null, o.Bool("postOnly"), false, null, null, ContingencyType.None, null);
    }

    public override async Task<IReadOnlyList<FillReport>> GenerateFillReportsAsync(InstrumentId? instrumentId, VenueOrderId? venueOrderId, UnixNanos? start, UnixNanos? end, CancellationToken ct)
    {
        List<FillReport> fills = new();
        foreach (string symbol in await SymbolsAsync(instrumentId, ct).ConfigureAwait(false))
        {
            Dictionary<string, string> q = new() { ["symbol"] = symbol, ["limit"] = "100" };
            if (venueOrderId is { } v)
            {
                q["orderId"] = v.Value;
            }

            if (start is { } s)
            {
                q["startAt"] = s.ToMilliseconds().ToString(CultureInfo.InvariantCulture);
            }

            if (end is { } e)
            {
                q["endAt"] = e.ToMilliseconds().ToString(CultureInfo.InvariantCulture);
            }

            JsonElement data = await _http.GetSignedAsync("/api/v1/hf/fills", q, ct).ConfigureAwait(false);
            if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("items", out JsonElement items) || items.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement x in items.EnumerateArray())
            {
                InstrumentId fillInstrument = KucoinVenue.ToInstrumentId(x.Str("symbol"));
                Instrument? instrument = _instruments.Find(fillInstrument) ?? Services.Cache.Instrument(fillInstrument);
                if (instrument is null)
                {
                    continue;
                }

                Currency feeCurrency = x.Str("feeCurrency").Length > 0 ? Currency.FromCode(x.Str("feeCurrency")) : instrument.QuoteCurrency;
                fills.Add(new FillReport(AccountId, fillInstrument, new VenueOrderId(x.Str("orderId")), new TradeId(x.Str("tradeId")), x.Str("side") == "buy" ? OrderSide.Buy : OrderSide.Sell,
                    instrument.MakeQuantity(x.Dec("size")), instrument.MakePrice(x.Dec("price")), new Money(x.Dec("fee"), feeCurrency),
                    x.Str("liquidity") == "maker" ? LiquiditySide.Maker : LiquiditySide.Taker, x.Ms("createdAt"), Clock.Timestamp, Guid.NewGuid(), null));
            }
        }

        return fills;
    }

    /// <summary>A spot account holds balances, not positions.</summary>
    public override Task<IReadOnlyList<PositionStatusReport>> GeneratePositionStatusReportsAsync(InstrumentId? instrumentId, UnixNanos? start, UnixNanos? end, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PositionStatusReport>>([]);

    private static TimeInForce MapTif(string tif) => tif switch
    {
        "IOC" => TimeInForce.Ioc,
        "FOK" => TimeInForce.Fok,
        "GTT" => TimeInForce.Gtd,
        _ => TimeInForce.Gtc,
    };
}

public sealed class KucoinExecutionClientFactory : IExecutionClientFactory
{
    public string Name => "KUCOIN";

    public Type ConfigType => typeof(KucoinExecutionClientConfig);

    /// <summary>
    /// One factory name for the venue and two clients behind it, chosen by the configuration exactly as the data
    /// clients are - so nothing configuring KUCOIN has to know the venue has two markets.
    /// </summary>
    public IExecutionClient Create(ClientId clientId, ExecutionClientConfig config, KernelServices services)
    {
        KucoinExecutionClientConfig kucoin = (KucoinExecutionClientConfig)config;
        return kucoin.ProductType == KucoinProductType.Futures
            ? new KucoinFuturesExecutionClient(clientId, kucoin, services)
            : new KucoinExecutionClient(clientId, kucoin, services);
    }
}

public sealed class KucoinPlugin : Core.Plugins.IPlugin, Core.Adapters.IVenuePlugin
{
    public string Id => "bytex.kucoin";

    /// <summary>
    /// KuCoin as two markets that share a key and nothing else. They answer on different hosts, spell their symbols
    /// differently, order their candle rows differently and denominate an order differently, which is why none of it
    /// is stated for "KuCoin". Its key has three parts where the others have two - the passphrase is chosen when the
    /// key is made and cannot be recovered - and a host that assumed two would have nowhere to put it.
    /// </summary>
    public Core.Adapters.VenueDescriptor Describe() => new()
    {
        Venue = KucoinVenue.Venue,
        DisplayName = "KuCoin",

        // KuCoin's programme is not one this adapter carries an id for yet.
        // This venue was declared as having no programme, and that was wrong. It runs two broker tiers with
        // rebate management, and its mechanism is a signed partner credential on every REST request - an id, a
        // broker name, and a signature over the timestamp, the id and the API key made with a second secret the
        // programme issues, on every request and not only on the ones that place orders.
        //
        // So nothing here can carry it: the configured id is one string, this needs three values and a key the
        // programme has not issued. Declared as a rebate going unclaimed rather than as a venue with nothing to
        // claim, because those are the same to the adapter and opposite to whoever decides which programmes to
        // join.
        BrokerTag = Core.Adapters.BrokerTag.None,
        BrokerProgramme = Core.Adapters.BrokerProgramme.NotCarried,
        Families =
        [
            new Core.Adapters.VenueFamily
            {
                Name = "spot",
                InstrumentClasses = [InstrumentClass.Spot],
                PaysFunding = false,
                Collateral = VenueCollateral.None,
                HttpBase = KucoinVenue.DefaultHttpBase,

                // None: KuCoin answers a REST call with the address to connect to and a token that expires, so
                // the socket is somewhere different each time and there is no base for a host to hold or override.
                WsBase = null,
                Key = KucoinKey,
                Config = new Dictionary<string, string> { ["productType"] = nameof(KucoinProductType.Spot) },
                IgnoredConfig = ["accountType"],
                DefaultFees = new Core.Adapters.VenueFees(0.001m, 0.001m),
                Capabilities = new Core.Adapters.VenueCapabilities
                {
                    LoadOneInstrument = true,
                    ListInstruments = true,
                    BarHistory = true,

                    // Spot pays no funding, so there is none to fetch.
                    FundingHistory = false,
                    MarketData = true,
                    Execution = true,

                    // Plain orders through hf/orders/alter; a stop is cancelled and replaced, which this adapter
                    // does for the caller, so an amend is honoured either way.
                    AmendOrders = true,
                },
            },
            new Core.Adapters.VenueFamily
            {
                Name = "futures",

                // Perpetuals and nothing else, which is a fact about the venue rather than a limit of the adapter:
                // KuCoin's only dated contracts are inverse, coin-margined ones, and inverse contracts are not
                // offered here because a quantity of one cannot be expressed in base currency without a price.
                InstrumentClasses = [InstrumentClass.Swap],
                PaysFunding = true,
                Collateral = VenueCollateral.Quote,
                HttpBase = KucoinFuturesVenue.DefaultHttpBase,

                // As spot: the venue answers a REST call with the address and a token, per connection.
                WsBase = null,
                Key = KucoinKey,
                Config = new Dictionary<string, string> { ["productType"] = nameof(KucoinProductType.Futures) },
                IgnoredConfig = ["accountType"],
                DefaultFees = new Core.Adapters.VenueFees(0.0002m, 0.0006m),
                Capabilities = new Core.Adapters.VenueCapabilities
                {
                    LoadOneInstrument = true,
                    ListInstruments = true,
                    BarHistory = true,
                    FundingHistory = true,
                    MarketData = true,
                    Execution = true,

                    // FALSE, and the reason this is declared at all. This market cannot change an order once it is
                    // placed - not plain orders, which spot can amend, and not stops. A strategy that resizes a
                    // protective order has to be told before it tries, not after a position has grown past the stop
                    // guarding it.
                    AmendOrders = false,
                },
            },
        ],
    };

    /// <summary>The same three parts and the same version on both markets: one KuCoin key, two sets of permissions.</summary>
    private static Core.Adapters.VenueKey KucoinKey => new()
    {
        Parts =
        [
            new Core.Adapters.VenueKeyPart("API key", KucoinVenue.EnvApiKey, Secret: false),
            new Core.Adapters.VenueKeyPart("API secret", KucoinVenue.EnvApiSecret, Secret: true),
            new Core.Adapters.VenueKeyPart("passphrase", KucoinVenue.EnvApiPassphrase, Secret: true),

            // Not a secret and not required - it defaults to 3 - but part of the key all the same: a right
            // passphrase signed as the wrong version is rejected, and a host that never offers the field leaves a
            // user with a key that looks correct and does not work.
            new Core.Adapters.VenueKeyPart(
                "API key version", KucoinVenue.EnvApiKeyVersion, Secret: false, Required: false),
        ],
    };

    public string Version => typeof(KucoinPlugin).Assembly.GetName().Version?.ToString() ?? "0";

    public void Register(Core.Plugins.IPluginRegistry registry)
    {
        registry.AddDataClientFactory(new KucoinDataClientFactory());
        registry.AddExecutionClientFactory(new KucoinExecutionClientFactory());
    }
}
