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

namespace Bytex.Adapters.Binance;

/// <summary>
/// Order routing and execution reporting for a Binance spot or USDⓈ-M futures account.
/// </summary>
public sealed class BinanceExecutionClient : ExecutionClientBase
{
    private readonly BinanceExecutionClientConfig _config;
    private readonly BinanceHttp _http;
    private readonly BinanceInstrumentProvider _instruments;
    private readonly bool _futures;
    private WebSocketClient? _userStream;
    private string? _listenKey;
    private Task? _keepAlive;
    private CancellationTokenSource? _cts;

    public BinanceExecutionClient(ClientId clientId, BinanceExecutionClientConfig config, KernelServices services)
        : base(clientId, BinanceVenue.Venue, new AccountId($"{BinanceVenue.Venue}-{config.AccountType.ToString().ToUpperInvariant()}"),
            config.AccountType == BinanceAccountType.Spot ? AccountType.Cash : AccountType.Margin, null, OmsType.Netting, services)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _futures = config.AccountType == BinanceAccountType.UsdMFutures;
        _http = new BinanceHttp(config, Log, requireCredentials: true, config.RecvWindowMs);
        _instruments = new BinanceInstrumentProvider(_http, config.AccountType, config.InstrumentProvider, Log);
    }

    private string OrderPath => _http.Prefix + "/order";

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
        await StartUserStreamAsync(ct).ConfigureAwait(false);
        NotifyConnected();
    }

    public override async Task DisconnectAsync(CancellationToken ct)
    {
        _cts?.Cancel();
        if (_userStream is not null)
        {
            await _userStream.DisposeAsync().ConfigureAwait(false);
            _userStream = null;
        }

        if (_listenKey is not null)
        {
            try
            {
                string path = _futures ? "/fapi/v1/listenKey" : "/api/v3/userDataStream";
                await _http.SendKeyedAsync(HttpMethod.Delete, path, _futures ? null : new Dictionary<string, string> { ["listenKey"] = _listenKey }, ct).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log.LogDebug(e, "Failed to close Binance listen key");
            }

            _listenKey = null;
        }

        NotifyDisconnected("disconnect requested");
    }

    protected override void OnDispose()
    {
        _cts?.Cancel();
        _userStream?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _http.Dispose();
    }

    // ----- User data stream -----

    private async Task StartUserStreamAsync(CancellationToken ct)
    {
        string path = _futures ? "/fapi/v1/listenKey" : "/api/v3/userDataStream";
        using JsonDocument doc = await _http.SendKeyedAsync(HttpMethod.Post, path, null, ct).ConfigureAwait(false);
        _listenKey = doc.RootElement.Str("listenKey");
        _cts = new CancellationTokenSource();
        _keepAlive = Task.Run(() => KeepAliveLoopAsync(_cts.Token), CancellationToken.None);

        _userStream = new WebSocketClient(new WebSocketClientConfig { Url = new Uri($"{BinanceVenue.WsBase(_config)}/ws/{_listenKey}") }, Log)
        {
            OnText = HandleUserMessageAsync,
            OnDisconnected = reason =>
            {
                NotifyDisconnected(reason);
                return Task.CompletedTask;
            },
            OnConnected = isReconnect =>
            {
                if (isReconnect)
                {
                    NotifyConnected();
                }

                return Task.CompletedTask;
            },
        };
        await _userStream.ConnectAsync(ct).ConfigureAwait(false);
    }

    private async Task KeepAliveLoopAsync(CancellationToken ct)
    {
        string path = _futures ? "/fapi/v1/listenKey" : "/api/v3/userDataStream";
        try
        {
            using PeriodicTimer timer = new(TimeSpan.FromMinutes(30));
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await _http.SendKeyedAsync(HttpMethod.Put, path, _futures || _listenKey is null ? null : new Dictionary<string, string> { ["listenKey"] = _listenKey }, ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Log.LogWarning(e, "Binance listen key keep-alive failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private Task HandleUserMessageAsync(string text)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            JsonElement root = doc.RootElement;
            string eventType = root.Str("e");
            switch (eventType)
            {
                case "executionReport":
                    HandleExecutionReport(root);
                    break;
                case "ORDER_TRADE_UPDATE":
                    HandleExecutionReport(root.GetProperty("o"), root.Ms("E"));
                    break;
                case "outboundAccountPosition":
                    HandleSpotBalances(root);
                    break;
                case "ACCOUNT_UPDATE":
                    HandleFuturesAccount(root);
                    break;
                case "listenKeyExpired":
                    Log.LogWarning("Binance listen key expired; reconnecting user stream");
                    _ = Task.Run(async () =>
                    {
                        await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                        await StartUserStreamAsync(CancellationToken.None).ConfigureAwait(false);
                        NotifyConnected();
                    });
                    break;
            }
        }
        catch (Exception e)
        {
            Log.LogWarning(e, "Failed to parse Binance user message: {Text}", text.Length > 300 ? text.Substring(0, 300) : text);
        }

        return Task.CompletedTask;
    }

    private void HandleExecutionReport(JsonElement o, UnixNanos? eventTime = null)
    {
        string clientOrderIdText = o.Str("c");
        if (string.IsNullOrEmpty(clientOrderIdText))
        {
            return;
        }

        ClientOrderId clientOrderId = new(clientOrderIdText);
        Order? order = Services.Cache.Order(clientOrderId);
        InstrumentId instrumentId = order?.InstrumentId ?? BinanceVenue.ToInstrumentId(o.Str("s"), _config.AccountType);
        StrategyId strategyId = order?.StrategyId ?? StrategyId.External;
        VenueOrderId venueOrderId = new(o.Long("i").ToString(CultureInfo.InvariantCulture));
        UnixNanos ts = eventTime ?? (o.Has("E") ? o.Ms("E") : o.Has("T") ? o.Ms("T") : Clock.Timestamp);
        string execType = o.Str("x");
        string status = o.Str("X");

        switch (execType)
        {
            case "NEW":
                GenerateOrderAccepted(strategyId, instrumentId, clientOrderId, venueOrderId, ts);
                break;
            case "CANCELED":
                GenerateOrderCanceled(strategyId, instrumentId, clientOrderId, venueOrderId, ts);
                break;
            case "EXPIRED":
                if (status == "EXPIRED_IN_MATCH" || order is { TimeInForce: TimeInForce.Ioc or TimeInForce.Fok } || order is { IsPostOnly: true })
                {
                    GenerateOrderCanceled(strategyId, instrumentId, clientOrderId, venueOrderId, ts);
                }
                else
                {
                    GenerateOrderExpired(strategyId, instrumentId, clientOrderId, venueOrderId, ts);
                }

                break;
            case "REJECTED":
                GenerateOrderRejected(strategyId, instrumentId, clientOrderId, o.StrOpt("r") ?? "rejected by venue", ts);
                break;
            case "TRADE":
                {
                    Instrument? instrument = _instruments.Find(instrumentId) ?? Services.Cache.Instrument(instrumentId);
                    if (instrument is null)
                    {
                        Log.LogWarning("Fill for unknown instrument {InstrumentId}", instrumentId);
                        return;
                    }

                    decimal commissionAmount = o.Has("n") ? o.Dec("n") : 0m;
                    string commissionAsset = o.StrOpt("N") ?? instrument.QuoteCurrency.Code;
                    Money commission = new(commissionAmount, Currency.FromCode(commissionAsset));
                    bool maker = o.Bool("m");
                    OrderSide side = o.Str("S") == "BUY" ? OrderSide.Buy : OrderSide.Sell;
                    OrderType type = MapOrderType(o.Str("o"));
                    GenerateOrderFilled(strategyId, instrumentId, clientOrderId, venueOrderId, null, new TradeId(o.Long("t").ToString(CultureInfo.InvariantCulture)), side, type,
                        instrument.MakeQuantity(o.Dec("l")), instrument.MakePrice(o.Dec("L")), instrument.QuoteCurrency, commission, maker ? LiquiditySide.Maker : LiquiditySide.Taker, o.Has("T") ? o.Ms("T") : ts);
                    break;
                }

            case "AMENDMENT":
                {
                    Instrument? instrument = _instruments.Find(instrumentId) ?? Services.Cache.Instrument(instrumentId);
                    if (instrument is not null)
                    {
                        GenerateOrderUpdated(strategyId, instrumentId, clientOrderId, venueOrderId, instrument.MakeQuantity(o.Dec("q")), instrument.MakePrice(o.Dec("p")), o.Has("sp") ? instrument.MakePrice(o.Dec("sp")) : null, ts);
                    }

                    break;
                }
        }
    }

    private void HandleSpotBalances(JsonElement root)
    {
        List<AccountBalance> balances = new();
        foreach (JsonElement b in root.GetProperty("B").EnumerateArray())
        {
            Currency currency = Currency.FromCode(b.Str("a"));
            decimal free = b.Dec("f");
            decimal locked = b.Dec("l");
            balances.Add(AccountBalance.Of(new Money(free + locked, currency), new Money(locked, currency)));
        }

        GenerateAccountState(balances, [], reported: true, root.Ms("E"));
    }

    private void HandleFuturesAccount(JsonElement root)
    {
        JsonElement a = root.GetProperty("a");
        List<AccountBalance> balances = new();
        foreach (JsonElement b in a.GetProperty("B").EnumerateArray())
        {
            Currency currency = Currency.FromCode(b.Str("a"));
            decimal wallet = b.Dec("wb");
            decimal cross = b.Has("cw") ? b.Dec("cw") : wallet;
            balances.Add(AccountBalance.Of(new Money(wallet, currency), new Money(Math.Max(0m, wallet - cross), currency)));
        }

        GenerateAccountState(balances, [], reported: true, root.Ms("E"));
    }

    private async Task PublishAccountStateAsync(CancellationToken ct)
    {
        try
        {
            List<AccountBalance> balances = new();
            if (_futures)
            {
                using JsonDocument doc = await _http.GetSignedAsync("/fapi/v2/balance", null, 5, ct).ConfigureAwait(false);
                foreach (JsonElement b in doc.RootElement.EnumerateArray())
                {
                    Currency currency = Currency.FromCode(b.Str("asset"));
                    decimal balance = b.Dec("balance");
                    decimal available = b.Has("availableBalance") ? b.Dec("availableBalance") : balance;
                    balances.Add(AccountBalance.Of(new Money(balance, currency), new Money(Math.Max(0m, balance - available), currency)));
                }
            }
            else
            {
                using JsonDocument doc = await _http.GetSignedAsync("/api/v3/account", null, 20, ct).ConfigureAwait(false);
                foreach (JsonElement b in doc.RootElement.GetProperty("balances").EnumerateArray())
                {
                    decimal free = b.Dec("free");
                    decimal locked = b.Dec("locked");
                    if (free == 0m && locked == 0m)
                    {
                        continue;
                    }

                    Currency currency = Currency.FromCode(b.Str("asset"));
                    balances.Add(AccountBalance.Of(new Money(free + locked, currency), new Money(locked, currency)));
                }
            }

            GenerateAccountState(balances, [], reported: true, Clock.Timestamp);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.LogError(e, "Failed to load Binance account state");
        }
    }

    // ----- Commands -----

    public override async Task SubmitOrderAsync(SubmitOrder command, CancellationToken ct)
    {
        Order order = command.Order;
        Instrument? instrument = _instruments.Find(order.InstrumentId) ?? Services.Cache.Instrument(order.InstrumentId);
        if (instrument is null)
        {
            GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, $"instrument {order.InstrumentId} unknown to Binance client", Clock.Timestamp);
            return;
        }

        Dictionary<string, string>? query = BuildOrderQuery(order, instrument, out string? error);
        if (query is null)
        {
            GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, error ?? "unsupported order", Clock.Timestamp);
            return;
        }

        GenerateOrderSubmitted(order.StrategyId, order.InstrumentId, order.ClientOrderId, Clock.Timestamp);
        try
        {
            using JsonDocument doc = await _http.PostSignedAsync(OrderPath, query, 1, ct).ConfigureAwait(false);
            // Acceptance and fills arrive on the user stream; the REST response is used only to catch synchronous rejections.
            if (doc.RootElement.TryGetProperty("code", out JsonElement code) && code.ValueKind == JsonValueKind.Number && code.GetInt32() < 0)
            {
                GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, doc.RootElement.StrOpt("msg") ?? "rejected", Clock.Timestamp);
            }
        }
        catch (VenueHttpException e)
        {
            GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, ErrorMessage(e), Clock.Timestamp);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, e.Message, Clock.Timestamp);
        }
    }

    private Dictionary<string, string>? BuildOrderQuery(Order order, Instrument instrument, out string? error)
    {
        error = null;
        Dictionary<string, string> q = new()
        {
            ["symbol"] = instrument.RawSymbol.Value,
            ["side"] = order.IsBuy ? "BUY" : "SELL",
            ["newClientOrderId"] = order.ClientOrderId.Value,
            ["newOrderRespType"] = "ACK",
        };

        if (order.IsQuoteQuantity && !_futures && order.Type == OrderType.Market)
        {
            q["quoteOrderQty"] = Json.Fmt(order.Quantity.Value);
        }
        else
        {
            q["quantity"] = Json.Fmt(order.Quantity.Value);
        }

        if (_futures && order.IsReduceOnly)
        {
            q["reduceOnly"] = "true";
        }

        string tif = order.TimeInForce switch
        {
            TimeInForce.Gtc => "GTC",
            TimeInForce.Ioc => "IOC",
            TimeInForce.Fok => "FOK",
            TimeInForce.Gtd when _futures => "GTD",
            _ => "GTC",
        };
        if (order.IsPostOnly)
        {
            tif = _futures ? "GTX" : "GTC";
        }

        string workingType = _futures ? (order.TriggerType == TriggerType.MarkPrice || (order.TriggerType == TriggerType.Default && _config.DefaultTriggerType == TriggerType.MarkPrice) ? "MARK_PRICE" : "CONTRACT_PRICE") : string.Empty;

        switch (order.Type)
        {
            case OrderType.Market:
                q["type"] = "MARKET";
                break;
            case OrderType.Limit:
                q["type"] = order.IsPostOnly && !_futures ? "LIMIT_MAKER" : "LIMIT";
                q["price"] = Json.Fmt(order.Price!.Value.Value);
                if (q["type"] == "LIMIT")
                {
                    q["timeInForce"] = tif;
                }

                if (order.DisplayQuantity is { } display && !_futures)
                {
                    q["icebergQty"] = Json.Fmt(display.Value);
                }

                break;
            case OrderType.StopMarket:
                q["type"] = _futures ? (order.IsBuy ? "STOP_MARKET" : "STOP_MARKET") : "STOP_LOSS";
                q["stopPrice"] = Json.Fmt(order.TriggerPrice!.Value.Value);
                break;
            case OrderType.StopLimit:
                q["type"] = _futures ? "STOP" : "STOP_LOSS_LIMIT";
                q["price"] = Json.Fmt(order.Price!.Value.Value);
                q["stopPrice"] = Json.Fmt(order.TriggerPrice!.Value.Value);
                q["timeInForce"] = tif;
                break;
            case OrderType.MarketIfTouched:
                q["type"] = _futures ? "TAKE_PROFIT_MARKET" : "TAKE_PROFIT";
                q["stopPrice"] = Json.Fmt(order.TriggerPrice!.Value.Value);
                break;
            case OrderType.LimitIfTouched:
                q["type"] = _futures ? "TAKE_PROFIT" : "TAKE_PROFIT_LIMIT";
                q["price"] = Json.Fmt(order.Price!.Value.Value);
                q["stopPrice"] = Json.Fmt(order.TriggerPrice!.Value.Value);
                q["timeInForce"] = tif;
                break;
            case OrderType.TrailingStopMarket when _futures && order is TrailingStopMarketOrder trailing:
                q["type"] = "TRAILING_STOP_MARKET";
                decimal callbackRate = trailing.TrailingOffsetType == TrailingOffsetType.BasisPoints ? trailing.TrailingOffset / 100m : trailing.TrailingOffset;
                q["callbackRate"] = Json.Fmt(Math.Clamp(callbackRate, 0.1m, 10m));
                if (trailing.ActivationPrice is { } activation)
                {
                    q["activationPrice"] = Json.Fmt(activation.Value);
                }

                break;
            default:
                error = $"order type {order.Type} is not supported by Binance {_config.AccountType}";
                return null;
        }

        if (_futures && q.ContainsKey("stopPrice"))
        {
            q["workingType"] = workingType;
        }

        if (order.TimeInForce == TimeInForce.Gtd && _futures && order.ExpireTime is { } expire)
        {
            q["goodTillDate"] = expire.ToMilliseconds().ToString(CultureInfo.InvariantCulture);
        }

        return q;
    }

    public override async Task ModifyOrderAsync(ModifyOrder command, CancellationToken ct)
    {
        Order? order = Services.Cache.Order(command.ClientOrderId);
        if (order is null)
        {
            return;
        }

        Instrument? instrument = _instruments.Find(order.InstrumentId) ?? Services.Cache.Instrument(order.InstrumentId);
        if (instrument is null || order.Type != OrderType.Limit)
        {
            GenerateOrderModifyRejected(command.StrategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, "only limit orders can be modified", Clock.Timestamp);
            return;
        }

        GenerateOrderPendingUpdate(command.StrategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, Clock.Timestamp);
        try
        {
            Quantity quantity = command.Quantity ?? order.Quantity;
            Price price = command.Price ?? order.Price!.Value;
            if (_futures)
            {
                Dictionary<string, string> q = new()
                {
                    ["symbol"] = instrument.RawSymbol.Value,
                    ["origClientOrderId"] = order.ClientOrderId.Value,
                    ["side"] = order.IsBuy ? "BUY" : "SELL",
                    ["quantity"] = Json.Fmt(quantity.Value),
                    ["price"] = Json.Fmt(price.Value),
                };
                using JsonDocument doc = await _http.PutSignedAsync(OrderPath, q, 1, ct).ConfigureAwait(false);
                GenerateOrderUpdated(command.StrategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, quantity, price, null, Clock.Timestamp);
            }
            else
            {
                Dictionary<string, string> q = new()
                {
                    ["symbol"] = instrument.RawSymbol.Value,
                    ["side"] = order.IsBuy ? "BUY" : "SELL",
                    ["type"] = "LIMIT",
                    ["cancelReplaceMode"] = "STOP_ON_FAILURE",
                    ["cancelOrigClientOrderId"] = order.ClientOrderId.Value,
                    ["timeInForce"] = "GTC",
                    ["quantity"] = Json.Fmt(quantity.Value),
                    ["price"] = Json.Fmt(price.Value),
                    ["newClientOrderId"] = order.ClientOrderId.Value,
                };
                using JsonDocument doc = await _http.PostSignedAsync("/api/v3/order/cancelReplace", q, 1, ct).ConfigureAwait(false);
                GenerateOrderUpdated(command.StrategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, quantity, price, null, Clock.Timestamp);
            }
        }
        catch (VenueHttpException e)
        {
            GenerateOrderModifyRejected(command.StrategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, ErrorMessage(e), Clock.Timestamp);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            GenerateOrderModifyRejected(command.StrategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, e.Message, Clock.Timestamp);
        }
    }

    public override async Task CancelOrderAsync(CancelOrder command, CancellationToken ct)
    {
        GenerateOrderPendingCancel(command.StrategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, Clock.Timestamp);
        try
        {
            Dictionary<string, string> q = new() { ["symbol"] = BinanceVenue.ToRawSymbol(command.InstrumentId), ["origClientOrderId"] = command.ClientOrderId.Value };
            using JsonDocument doc = await _http.DeleteSignedAsync(OrderPath, q, 1, ct).ConfigureAwait(false);
            // The cancel confirmation arrives on the user stream.
        }
        catch (VenueHttpException e)
        {
            GenerateOrderCancelRejected(command.StrategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, ErrorMessage(e), Clock.Timestamp);
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

        try
        {
            Dictionary<string, string> q = new() { ["symbol"] = BinanceVenue.ToRawSymbol(command.InstrumentId) };
            string path = _futures ? "/fapi/v1/allOpenOrders" : "/api/v3/openOrders";
            using JsonDocument doc = await _http.DeleteSignedAsync(path, q, 1, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.LogError(e, "Binance cancel-all failed for {InstrumentId}", command.InstrumentId);
        }
    }

    public override async Task QueryOrderAsync(QueryOrder command, CancellationToken ct)
    {
        OrderStatusReport? report = await GenerateOrderStatusReportAsync(command.InstrumentId, command.ClientOrderId, command.VenueOrderId, ct).ConfigureAwait(false);
        if (report is not null)
        {
            Log.LogInformation("Order status {ClientOrderId}: {Status} filled {Filled}/{Quantity}", report.ClientOrderId, report.OrderStatus, report.FilledQuantity, report.Quantity);
        }
    }

    // ----- Reports -----

    public override async Task<ExecutionMassStatus?> GenerateMassStatusAsync(UnixNanos? since, CancellationToken ct)
    {
        IReadOnlyList<OrderStatusReport> orders = await GenerateOrderStatusReportsAsync(null, since, null, openOnly: true, ct).ConfigureAwait(false);
        List<FillReport> fills = new();
        foreach (InstrumentId instrumentId in orders.Select(o => o.InstrumentId).Distinct())
        {
            fills.AddRange(await GenerateFillReportsAsync(instrumentId, null, since, null, ct).ConfigureAwait(false));
        }

        IReadOnlyList<PositionStatusReport> positions = await GeneratePositionStatusReportsAsync(null, null, null, ct).ConfigureAwait(false);
        return new ExecutionMassStatus(ClientId, AccountId, Venue, orders, fills, positions, Clock.Timestamp, Guid.NewGuid());
    }

    public override async Task<OrderStatusReport?> GenerateOrderStatusReportAsync(InstrumentId instrumentId, ClientOrderId? clientOrderId, VenueOrderId? venueOrderId, CancellationToken ct)
    {
        Dictionary<string, string> q = new() { ["symbol"] = BinanceVenue.ToRawSymbol(instrumentId) };
        if (clientOrderId is { } c)
        {
            q["origClientOrderId"] = c.Value;
        }
        else if (venueOrderId is { } v)
        {
            q["orderId"] = v.Value;
        }
        else
        {
            return null;
        }

        try
        {
            using JsonDocument doc = await _http.GetSignedAsync(OrderPath, q, 2, ct).ConfigureAwait(false);
            return ParseOrderReport(doc.RootElement);
        }
        catch (VenueHttpException e)
        {
            Log.LogWarning("Order status query failed: {Message}", ErrorMessage(e));
            return null;
        }
    }

    public override async Task<IReadOnlyList<OrderStatusReport>> GenerateOrderStatusReportsAsync(InstrumentId? instrumentId, UnixNanos? start, UnixNanos? end, bool openOnly, CancellationToken ct)
    {
        List<OrderStatusReport> reports = new();
        Dictionary<string, string> q = new();
        if (instrumentId is { } id)
        {
            q["symbol"] = BinanceVenue.ToRawSymbol(id);
        }

        if (openOnly)
        {
            using JsonDocument doc = await _http.GetSignedAsync(_http.Prefix + "/openOrders", q, instrumentId is null ? 40 : 3, ct).ConfigureAwait(false);
            foreach (JsonElement o in doc.RootElement.EnumerateArray())
            {
                OrderStatusReport? report = ParseOrderReport(o);
                if (report is not null)
                {
                    reports.Add(report);
                }
            }

            return reports;
        }

        if (instrumentId is null)
        {
            return reports;
        }

        if (start is { } s)
        {
            q["startTime"] = s.ToMilliseconds().ToString(CultureInfo.InvariantCulture);
        }

        if (end is { } e)
        {
            q["endTime"] = e.ToMilliseconds().ToString(CultureInfo.InvariantCulture);
        }

        using JsonDocument all = await _http.GetSignedAsync(_http.Prefix + "/allOrders", q, 10, ct).ConfigureAwait(false);
        foreach (JsonElement o in all.RootElement.EnumerateArray())
        {
            OrderStatusReport? report = ParseOrderReport(o);
            if (report is not null)
            {
                reports.Add(report);
            }
        }

        return reports;
    }

    private OrderStatusReport? ParseOrderReport(JsonElement o)
    {
        string raw = o.Str("symbol");
        InstrumentId instrumentId = BinanceVenue.ToInstrumentId(raw, _config.AccountType);
        Instrument? instrument = _instruments.Find(instrumentId) ?? Services.Cache.Instrument(instrumentId);
        if (instrument is null)
        {
            return null;
        }

        string clientId = o.StrOpt("clientOrderId") ?? string.Empty;
        decimal price = o.Has("price") ? o.Dec("price") : 0m;
        decimal stopPrice = o.Has("stopPrice") ? o.Dec("stopPrice") : 0m;
        decimal avgPx = o.Has("avgPrice") ? o.Dec("avgPrice") : 0m;
        decimal executed = o.Dec("executedQty");
        decimal cumQuote = o.Has("cummulativeQuoteQty") ? o.Dec("cummulativeQuoteQty") : 0m;
        if (avgPx == 0m && executed > 0m && cumQuote > 0m)
        {
            avgPx = cumQuote / executed;
        }

        long time = o.Has("time") ? o.Long("time") : o.Has("transactTime") ? o.Long("transactTime") : 0;
        long update = o.Has("updateTime") ? o.Long("updateTime") : time;
        return new OrderStatusReport(
            AccountId, instrumentId, string.IsNullOrEmpty(clientId) ? null : new ClientOrderId(clientId), new VenueOrderId(o.Long("orderId").ToString(CultureInfo.InvariantCulture)),
            o.Str("side") == "BUY" ? OrderSide.Buy : OrderSide.Sell, MapOrderType(o.Str("type")), MapTif(o.StrOpt("timeInForce")), MapStatus(o.Str("status")),
            instrument.MakeQuantity(o.Dec("origQty")), instrument.MakeQuantity(executed), UnixNanos.FromMilliseconds(time), UnixNanos.FromMilliseconds(update), Clock.Timestamp, Guid.NewGuid(),
            price > 0m ? instrument.MakePrice(price) : null, stopPrice > 0m ? instrument.MakePrice(stopPrice) : null, TriggerType.Default, null, TrailingOffsetType.Price, null,
            avgPx > 0m ? avgPx : null, o.StrOpt("timeInForce") == "GTX", o.Bool("reduceOnly"));
    }

    public override async Task<IReadOnlyList<FillReport>> GenerateFillReportsAsync(InstrumentId? instrumentId, VenueOrderId? venueOrderId, UnixNanos? start, UnixNanos? end, CancellationToken ct)
    {
        if (instrumentId is null)
        {
            return [];
        }

        Instrument? instrument = _instruments.Find(instrumentId.Value) ?? Services.Cache.Instrument(instrumentId.Value);
        if (instrument is null)
        {
            return [];
        }

        Dictionary<string, string> q = new() { ["symbol"] = instrument.RawSymbol.Value };
        if (venueOrderId is { } v)
        {
            q["orderId"] = v.Value;
        }

        if (start is { } s)
        {
            q["startTime"] = s.ToMilliseconds().ToString(CultureInfo.InvariantCulture);
        }

        if (end is { } e)
        {
            q["endTime"] = e.ToMilliseconds().ToString(CultureInfo.InvariantCulture);
        }

        string path = _futures ? "/fapi/v1/userTrades" : "/api/v3/myTrades";
        List<FillReport> fills = new();
        using JsonDocument doc = await _http.GetSignedAsync(path, q, 10, ct).ConfigureAwait(false);
        foreach (JsonElement t in doc.RootElement.EnumerateArray())
        {
            bool isBuyer = t.Has("isBuyer") ? t.Bool("isBuyer") : t.StrOpt("side") == "BUY";
            bool isMaker = t.Has("isMaker") ? t.Bool("isMaker") : t.Bool("maker");
            Currency commissionCurrency = Currency.FromCode(t.StrOpt("commissionAsset") ?? instrument.QuoteCurrency.Code);
            fills.Add(new FillReport(AccountId, instrumentId.Value, new VenueOrderId(t.Long("orderId").ToString(CultureInfo.InvariantCulture)), new TradeId(t.Long("id").ToString(CultureInfo.InvariantCulture)),
                isBuyer ? OrderSide.Buy : OrderSide.Sell, instrument.MakeQuantity(t.Dec("qty")), instrument.MakePrice(t.Dec("price")), new Money(t.Dec("commission"), commissionCurrency),
                isMaker ? LiquiditySide.Maker : LiquiditySide.Taker, t.Ms("time"), Clock.Timestamp, Guid.NewGuid()));
        }

        return fills;
    }

    public override async Task<IReadOnlyList<PositionStatusReport>> GeneratePositionStatusReportsAsync(InstrumentId? instrumentId, UnixNanos? start, UnixNanos? end, CancellationToken ct)
    {
        if (!_futures)
        {
            return [];
        }

        Dictionary<string, string> q = new();
        if (instrumentId is { } id)
        {
            q["symbol"] = BinanceVenue.ToRawSymbol(id);
        }

        List<PositionStatusReport> reports = new();
        using JsonDocument doc = await _http.GetSignedAsync("/fapi/v2/positionRisk", q, 5, ct).ConfigureAwait(false);
        foreach (JsonElement p in doc.RootElement.EnumerateArray())
        {
            decimal amount = p.Dec("positionAmt");
            if (amount == 0m)
            {
                continue;
            }

            InstrumentId posId = BinanceVenue.ToInstrumentId(p.Str("symbol"), _config.AccountType);
            Instrument? instrument = _instruments.Find(posId) ?? Services.Cache.Instrument(posId);
            if (instrument is null)
            {
                continue;
            }

            reports.Add(new PositionStatusReport(AccountId, posId, amount > 0m ? PositionSide.Long : PositionSide.Short, instrument.MakeQuantity(Math.Abs(amount)),
                p.Has("updateTime") ? p.Ms("updateTime") : Clock.Timestamp, Clock.Timestamp, Guid.NewGuid(), null, p.Has("entryPrice") ? p.Dec("entryPrice") : null));
        }

        return reports;
    }

    // ----- Mapping -----

    private static OrderType MapOrderType(string type) => type switch
    {
        "MARKET" => OrderType.Market,
        "LIMIT" or "LIMIT_MAKER" => OrderType.Limit,
        "STOP_LOSS" or "STOP_MARKET" => OrderType.StopMarket,
        "STOP_LOSS_LIMIT" or "STOP" => OrderType.StopLimit,
        "TAKE_PROFIT" => OrderType.MarketIfTouched,
        "TAKE_PROFIT_LIMIT" => OrderType.LimitIfTouched,
        "TAKE_PROFIT_MARKET" => OrderType.MarketIfTouched,
        "TRAILING_STOP_MARKET" => OrderType.TrailingStopMarket,
        _ => OrderType.Market,
    };

    private static TimeInForce MapTif(string? tif) => tif switch
    {
        "IOC" => TimeInForce.Ioc,
        "FOK" => TimeInForce.Fok,
        "GTD" => TimeInForce.Gtd,
        _ => TimeInForce.Gtc,
    };

    private static OrderStatus MapStatus(string status) => status switch
    {
        "NEW" => OrderStatus.Accepted,
        "PARTIALLY_FILLED" => OrderStatus.PartiallyFilled,
        "FILLED" => OrderStatus.Filled,
        "CANCELED" or "PENDING_CANCEL" => OrderStatus.Canceled,
        "REJECTED" => OrderStatus.Rejected,
        "EXPIRED" or "EXPIRED_IN_MATCH" => OrderStatus.Expired,
        "NEW_INSURANCE" or "NEW_ADL" => OrderStatus.Accepted,
        _ => OrderStatus.Accepted,
    };

    private static string ErrorMessage(VenueHttpException e)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(e.Body);
            return doc.RootElement.StrOpt("msg") ?? e.Message;
        }
        catch (JsonException)
        {
            return e.Message;
        }
    }
}

public sealed class BinanceExecutionClientFactory : IExecutionClientFactory
{
    public string Name => "BINANCE";

    public Type ConfigType => typeof(BinanceExecutionClientConfig);

    public IExecutionClient Create(ClientId clientId, ExecutionClientConfig config, KernelServices services) =>
        new BinanceExecutionClient(clientId, (BinanceExecutionClientConfig)config, services);
}

/// <summary>
/// Registers the Binance factories with a plugin registry.
/// </summary>
public sealed class BinancePlugin : Core.Plugins.IPlugin
{
    public string Id => "bytex.binance";

    public string Version => typeof(BinancePlugin).Assembly.GetName().Version?.ToString() ?? "0";

    public void Register(Core.Plugins.IPluginRegistry registry)
    {
        registry.AddDataClientFactory(new BinanceDataClientFactory());
        registry.AddExecutionClientFactory(new BinanceExecutionClientFactory());
    }
}
