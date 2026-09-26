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

namespace Bytex.Adapters.Bybit;

/// <summary>
/// Order routing and execution reporting for a Bybit unified account (spot or linear).
/// </summary>
public sealed class BybitExecutionClient : ExecutionClientBase
{
    private readonly BybitExecutionClientConfig _config;
    private readonly BybitHttp _http;
    private readonly BybitInstrumentProvider _instruments;
    private readonly string _apiKey;
    private readonly string _apiSecret;
    private readonly HashSet<string> _seenExecutions = new(StringComparer.Ordinal);
    private WebSocketClient? _ws;

    public BybitExecutionClient(ClientId clientId, BybitExecutionClientConfig config, KernelServices services)
        : base(clientId, BybitVenue.Venue, new AccountId($"{BybitVenue.Venue}-UNIFIED"), config.ProductType == BybitProductType.Spot ? AccountType.Cash : AccountType.Margin, null, OmsType.Netting, services)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        (_apiKey, _apiSecret) = BybitVenue.Credentials(config);
        _http = new BybitHttp(config, Log, requireCredentials: true, config.RecvWindowMs, config.BrokerId);
        _instruments = new BybitInstrumentProvider(_http, config.ProductType, config.InstrumentProvider, Log);
    }

    private string Category => _http.Category;

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

        _ws = new WebSocketClient(new WebSocketClientConfig { Url = new Uri(BybitVenue.WsPrivate(_config)), PingMessage = "{\"op\":\"ping\"}", PingInterval = BybitVenue.PingInterval }, Log)
        {
            OnText = HandleMessageAsync,
            OnConnected = isReconnect =>
            {
                Authenticate();
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

    private void Authenticate()
    {
        long expires = DateTimeOffset.UtcNow.Add(BybitVenue.AuthExpiry).ToUnixTimeMilliseconds();
        string signature = HmacSigner.Sha256Hex(_apiSecret, "GET/realtime" + expires.ToString(CultureInfo.InvariantCulture));
        _ws?.SendText(JsonSerializer.Serialize(new { op = "auth", args = new object[] { _apiKey, expires, signature } }));
    }

    /// <summary>
    /// Sets the configured leverage at the venue, per symbol, before anything is traded.
    /// <para>
    /// On this venue leverage is account state and an order carrying one is ignored, so a strategy written for 3x
    /// would otherwise be traded at whatever the account was last left on - and would backtest and paper at 3x while
    /// going live at something else, with nothing saying so.
    /// </para>
    /// <para>
    /// Two of this venue's four families have no leverage to set, for different reasons: spot borrows nothing, and
    /// the venue margins an option by a portfolio calculation with no per-symbol leverage endpoint and no ceiling
    /// published to check one against. A leverage configured on either is said out loud rather than dropped -
    /// a configuration field nothing reads is the failure this whole guard exists for.
    /// </para>
    /// <para>
    /// A refusal is logged and does not stop the node: it is usually the venue saying the account is not entitled
    /// to the figure asked for, which a node cannot fix and a person needs to read.
    /// </para>
    /// </summary>
    private async Task ApplyLeverageAsync(CancellationToken ct)
    {
        if (_config.Leverage is not { } leverage)
        {
            return;
        }

        if (!BybitVenue.AppliesLeverage(_config.ProductType))
        {
            Log.LogWarning(
                "A leverage of {Leverage} is configured and Bybit applies none to its {Category} market, so nothing "
                + "was sent and positions will be margined the way this venue margins that market",
                leverage,
                Category);
            return;
        }

        string value = leverage.ToString(CultureInfo.InvariantCulture);

        // What this client will actually trade: what the node holds for this venue, plus anything its own provider
        // loaded. Either can be empty on its own.
        IReadOnlyList<Instrument> tradable =
            [.. Services.Cache.Instruments(Venue).Concat(_instruments.GetAll()).DistinctBy(i => i.Id)];

        // Before anything is sent. This venue publishes its ceiling per symbol in the same response the instruments
        // came from, so there is never a reason to find out by having a lower one silently granted instead.
        LeverageGuard.EnsureGranted(leverage, tradable, BybitVenue.Venue.Value);

        foreach (Instrument instrument in tradable)
        {
            try
            {
                await _http.PostSignedAsync(
                    BybitVenue.LeveragePath,
                    new
                    {
                        category = _http.Category,
                        symbol = instrument.RawSymbol!.Value,

                        // This venue takes the two sides separately and refuses a one-sided change on a netting
                        // account, so both are set to the same figure.
                        buyLeverage = value,
                        sellLeverage = value,
                    },
                    ct).ConfigureAwait(false);
            }
            catch (BybitApiException e) when (e.Code == BybitVenue.ErrorLeverageUnchanged)
            {
                // The venue refuses a change to the figure already in force. Nothing to do and nothing wrong.
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Log.LogError(
                    e,
                    "Bybit refused {Leverage}x on {Instrument}; it will trade at whatever the account is set to",
                    leverage,
                    instrument.Id);
            }
        }
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
            if (root.TryGetProperty("op", out JsonElement op))
            {
                string operation = op.GetString() ?? string.Empty;
                if (operation == "auth")
                {
                    if (root.Bool("success"))
                    {
                        _ws?.SendText(JsonSerializer.Serialize(new { op = "subscribe", args = new[] { "order", "execution", "wallet" } }));
                    }
                    else
                    {
                        Log.LogError("Bybit private stream authentication failed: {Message}", root.Str("ret_msg"));
                    }
                }

                return Task.CompletedTask;
            }

            string topic = root.Str("topic");
            JsonElement data = root.GetProperty("data");
            switch (topic)
            {
                case "order":
                    foreach (JsonElement o in data.EnumerateArray())
                    {
                        HandleOrderUpdate(o);
                    }

                    break;
                case "execution":
                    foreach (JsonElement x in data.EnumerateArray())
                    {
                        HandleExecution(x);
                    }

                    break;
                case "wallet":
                    foreach (JsonElement w in data.EnumerateArray())
                    {
                        HandleWallet(w, root.Has("creationTime") ? root.Ms("creationTime") : Clock.Timestamp);
                    }

                    break;
            }
        }
        catch (Exception e)
        {
            Log.LogWarning(e, "Failed to parse Bybit private message: {Text}", LogText.Truncate(text, LogText.MaxMessageLength));
        }

        return Task.CompletedTask;
    }

    private void HandleOrderUpdate(JsonElement o)
    {
        if (o.Str("category") != Category)
        {
            return;
        }

        string linkId = o.Str("orderLinkId");
        if (string.IsNullOrEmpty(linkId))
        {
            return;
        }

        ClientOrderId clientOrderId = new(linkId);
        Order? order = Services.Cache.Order(clientOrderId);
        InstrumentId instrumentId = order?.InstrumentId ?? BybitVenue.ToInstrumentId(o.Str("symbol"), _config.ProductType);
        StrategyId strategyId = order?.StrategyId ?? StrategyId.External;
        VenueOrderId venueOrderId = new(o.Str("orderId"));
        UnixNanos ts = o.Has("updatedTime") ? o.Ms("updatedTime") : Clock.Timestamp;

        switch (o.Str("orderStatus"))
        {
            case "New":
            case "Untriggered":
                if (order is null || order.Status is OrderStatus.Submitted or OrderStatus.Initialized or OrderStatus.PendingUpdate)
                {
                    if (order is { Status: OrderStatus.PendingUpdate } && _instruments.Find(instrumentId) is { } instrument)
                    {
                        decimal px = o.Dec("price");
                        decimal tp = o.Dec("triggerPrice");
                        GenerateOrderUpdated(strategyId, instrumentId, clientOrderId, venueOrderId, instrument.MakeQuantity(o.Dec("qty")), px > 0m ? instrument.MakePrice(px) : null, tp > 0m ? instrument.MakePrice(tp) : null, ts);
                    }
                    else
                    {
                        GenerateOrderAccepted(strategyId, instrumentId, clientOrderId, venueOrderId, ts);
                    }
                }

                break;
            case "Triggered":
                GenerateOrderTriggered(strategyId, instrumentId, clientOrderId, venueOrderId, ts);
                break;
            case "Cancelled":
            case "Deactivated":
                GenerateOrderCanceled(strategyId, instrumentId, clientOrderId, venueOrderId, ts);
                break;
            case "Rejected":
                GenerateOrderRejected(strategyId, instrumentId, clientOrderId, o.Str("rejectReason"), ts);
                break;
            case "PartiallyFilledCanceled":
                GenerateOrderCanceled(strategyId, instrumentId, clientOrderId, venueOrderId, ts);
                break;
        }
    }

    private void HandleExecution(JsonElement x)
    {
        if (x.Str("category") != Category)
        {
            return;
        }

        string execType = x.Str("execType");
        if (!BybitVenue.IsTradeExecution(execType))
        {
            // Not a trade: a funding payment, a delivery, a settlement or a position transfer. Its quantity is the
            // position's, not a fill's, so booking it would move the position twice.
            Log.LogDebug("Ignoring {ExecType} row {ExecId} on the execution topic: it is not a trade", execType, x.Str("execId"));
            return;
        }

        string execId = x.Str("execId");
        if (!_seenExecutions.Add(execId))
        {
            return;
        }

        string linkId = x.Str("orderLinkId");
        ClientOrderId clientOrderId = new(string.IsNullOrEmpty(linkId) ? "O-" + x.Str("orderId") : linkId);
        Order? order = Services.Cache.Order(clientOrderId);
        InstrumentId instrumentId = order?.InstrumentId ?? BybitVenue.ToInstrumentId(x.Str("symbol"), _config.ProductType);
        Instrument? instrument = _instruments.Find(instrumentId) ?? Services.Cache.Instrument(instrumentId);
        if (instrument is null)
        {
            return;
        }

        StrategyId strategyId = order?.StrategyId ?? StrategyId.External;
        Currency feeCurrency = x.Has("feeCurrency") && x.Str("feeCurrency").Length > 0 ? Currency.FromCode(x.Str("feeCurrency")) : instrument.SettlementCurrency;
        GenerateOrderFilled(strategyId, instrumentId, clientOrderId, new VenueOrderId(x.Str("orderId")), null, new TradeId(execId),
            x.Str("side") == "Buy" ? OrderSide.Buy : OrderSide.Sell, MapOrderType(x.Str("orderType"), x.Str("stopOrderType")),
            instrument.MakeQuantity(x.Dec("execQty")), instrument.MakePrice(x.Dec("execPrice")), instrument.QuoteCurrency, new Money(x.Dec("execFee"), feeCurrency),
            x.Bool("isMaker") ? LiquiditySide.Maker : LiquiditySide.Taker, x.Ms("execTime"));
    }

    private void HandleWallet(JsonElement w, UnixNanos ts)
    {
        List<AccountBalance> balances = new();
        foreach (JsonElement c in w.GetProperty("coin").EnumerateArray())
        {
            Currency currency = Currency.FromCode(c.Str("coin"));
            decimal total = c.Dec("walletBalance");
            decimal locked = c.Dec("locked") + c.Dec("totalOrderIM") + c.Dec("totalPositionIM");
            balances.Add(AccountBalance.Of(new Money(total, currency), new Money(Math.Min(total, locked), currency)));
        }

        GenerateAccountState(balances, [], reported: true, ts);
    }

    private async Task PublishAccountStateAsync(CancellationToken ct)
    {
        try
        {
            JsonElement result = await _http.GetSignedAsync("/v5/account/wallet-balance", new Dictionary<string, string> { ["accountType"] = "UNIFIED" }, ct).ConfigureAwait(false);
            foreach (JsonElement account in result.GetProperty("list").EnumerateArray())
            {
                HandleWallet(account, Clock.Timestamp);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.LogError(e, "Failed to load Bybit account state");
        }
    }

    // ----- Commands -----

    public override async Task SubmitOrderAsync(SubmitOrder command, CancellationToken ct)
    {
        Order order = command.Order;
        Instrument? instrument = _instruments.Find(order.InstrumentId) ?? Services.Cache.Instrument(order.InstrumentId);
        if (instrument is null)
        {
            GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, $"instrument {order.InstrumentId} unknown to Bybit client", Clock.Timestamp);
            return;
        }

        Dictionary<string, object> body = new()
        {
            ["category"] = Category,
            ["symbol"] = instrument.RawSymbol.Value,
            ["side"] = order.IsBuy ? "Buy" : "Sell",
            ["orderLinkId"] = order.ClientOrderId.Value,
            ["qty"] = Json.Fmt(order.Quantity.Value),
        };

        string tif = order.TimeInForce switch
        {
            TimeInForce.Ioc => "IOC",
            TimeInForce.Fok => "FOK",
            _ => order.IsPostOnly ? "PostOnly" : "GTC",
        };

        switch (order.Type)
        {
            case OrderType.Market:
                body["orderType"] = "Market";
                if (order.IsQuoteQuantity && _config.ProductType == BybitProductType.Spot)
                {
                    body["marketUnit"] = "quoteCoin";
                }

                break;
            case OrderType.Limit:
                body["orderType"] = "Limit";
                body["price"] = Json.Fmt(order.Price!.Value.Value);
                body["timeInForce"] = tif;
                break;
            case OrderType.StopMarket:
            case OrderType.MarketIfTouched:
                body["orderType"] = "Market";
                AddTrigger(body, order, instrument);
                break;
            case OrderType.StopLimit:
            case OrderType.LimitIfTouched:
                body["orderType"] = "Limit";
                body["price"] = Json.Fmt(order.Price!.Value.Value);
                body["timeInForce"] = tif;
                AddTrigger(body, order, instrument);
                break;
            default:
                GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, $"order type {order.Type} is not supported by Bybit", Clock.Timestamp);
                return;
        }

        // The two contract families take it; spot has no position to reduce. It is left off an option order
        // deliberately: this venue documents the field for its option market and that could not be confirmed
        // without a key, and a flag the venue might reject would fail the whole order rather than the flag.
        if (_config.ProductType is BybitProductType.Linear or BybitProductType.Inverse && order.IsReduceOnly)
        {
            body["reduceOnly"] = true;
        }

        GenerateOrderSubmitted(order.StrategyId, order.InstrumentId, order.ClientOrderId, Clock.Timestamp);
        try
        {
            await _http.PostSignedAsync("/v5/order/create", body, ct).ConfigureAwait(false);
        }
        catch (BybitApiException e)
        {
            GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, e.RetMsg, Clock.Timestamp);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, e.Message, Clock.Timestamp);
        }
    }

    private void AddTrigger(Dictionary<string, object> body, Order order, Instrument instrument)
    {
        decimal trigger = order.TriggerPrice!.Value.Value;
        body["triggerPrice"] = Json.Fmt(trigger);
        bool stop = order.Type is OrderType.StopMarket or OrderType.StopLimit;
        // Direction 1 = triggers when price rises to the trigger, 2 = when it falls to it.
        bool rises = stop ? order.IsBuy : order.IsSell;
        body["triggerDirection"] = rises ? 1 : 2;
        TriggerType triggerType = order.TriggerType == TriggerType.Default ? _config.DefaultTriggerType : order.TriggerType;
        body["triggerBy"] = triggerType switch
        {
            TriggerType.MarkPrice => "MarkPrice",
            TriggerType.IndexPrice => "IndexPrice",
            _ => "LastPrice",
        };
        if (_config.ProductType == BybitProductType.Spot)
        {
            body["orderFilter"] = "StopOrder";
        }
    }

    public override async Task ModifyOrderAsync(ModifyOrder command, CancellationToken ct)
    {
        Dictionary<string, object> body = new()
        {
            ["category"] = Category,
            ["symbol"] = BybitVenue.ToRawSymbol(command.InstrumentId),
            ["orderLinkId"] = command.ClientOrderId.Value,
        };
        if (command.Quantity is { } qty)
        {
            body["qty"] = Json.Fmt(qty.Value);
        }

        if (command.Price is { } price)
        {
            body["price"] = Json.Fmt(price.Value);
        }

        if (command.TriggerPrice is { } trigger)
        {
            body["triggerPrice"] = Json.Fmt(trigger.Value);
        }

        GenerateOrderPendingUpdate(command.StrategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, Clock.Timestamp);
        try
        {
            await _http.PostSignedAsync("/v5/order/amend", body, ct).ConfigureAwait(false);
        }
        catch (BybitApiException e)
        {
            GenerateOrderModifyRejected(command.StrategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, e.RetMsg, Clock.Timestamp);
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
            await _http.PostSignedAsync("/v5/order/cancel", new { category = Category, symbol = BybitVenue.ToRawSymbol(command.InstrumentId), orderLinkId = command.ClientOrderId.Value }, ct).ConfigureAwait(false);
        }
        catch (BybitApiException e)
        {
            GenerateOrderCancelRejected(command.StrategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, e.RetMsg, Clock.Timestamp);
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
            await _http.PostSignedAsync("/v5/order/cancel-all", new { category = Category, symbol = BybitVenue.ToRawSymbol(command.InstrumentId) }, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.LogError(e, "Bybit cancel-all failed for {InstrumentId}", command.InstrumentId);
        }
    }

    // ----- Reports -----

    public override async Task<ExecutionMassStatus?> GenerateMassStatusAsync(UnixNanos? since, CancellationToken ct)
    {
        IReadOnlyList<OrderStatusReport> orders = await GenerateOrderStatusReportsAsync(null, since, null, openOnly: true, ct).ConfigureAwait(false);
        IReadOnlyList<FillReport> fills = await GenerateFillReportsAsync(null, null, since, null, ct).ConfigureAwait(false);
        IReadOnlyList<PositionStatusReport> positions = await GeneratePositionStatusReportsAsync(null, null, null, ct).ConfigureAwait(false);
        return new ExecutionMassStatus(ClientId, AccountId, Venue, orders, fills, positions, Clock.Timestamp, Guid.NewGuid());
    }

    public override async Task<OrderStatusReport?> GenerateOrderStatusReportAsync(InstrumentId instrumentId, ClientOrderId? clientOrderId, VenueOrderId? venueOrderId, CancellationToken ct)
    {
        Dictionary<string, string> q = new() { ["category"] = Category, ["symbol"] = BybitVenue.ToRawSymbol(instrumentId) };
        if (clientOrderId is { } c)
        {
            q["orderLinkId"] = c.Value;
        }
        else if (venueOrderId is { } v)
        {
            q["orderId"] = v.Value;
        }

        JsonElement result = await _http.GetSignedAsync("/v5/order/realtime", q, ct).ConfigureAwait(false);
        foreach (JsonElement o in result.GetProperty("list").EnumerateArray())
        {
            OrderStatusReport? report = ParseOrder(o);
            if (report is not null)
            {
                return report;
            }
        }

        return null;
    }

    public override async Task<IReadOnlyList<OrderStatusReport>> GenerateOrderStatusReportsAsync(InstrumentId? instrumentId, UnixNanos? start, UnixNanos? end, bool openOnly, CancellationToken ct)
    {
        List<OrderStatusReport> reports = new();
        string path = openOnly ? "/v5/order/realtime" : "/v5/order/history";
        foreach (IReadOnlyDictionary<string, string> market in Markets(instrumentId))
        {
            Dictionary<string, string> q = new(market, StringComparer.Ordinal)
            {
                ["category"] = Category,
                ["limit"] = BybitVenue.OrderReportPage.ToString(CultureInfo.InvariantCulture),
            };

            if (openOnly)
            {
                q["openOnly"] = "0";
            }

            if (start is { } s)
            {
                q["startTime"] = s.ToMilliseconds().ToString(CultureInfo.InvariantCulture);
            }

            string? cursor = null;
            do
            {
                if (cursor is not null)
                {
                    q["cursor"] = cursor;
                }

                JsonElement result = await _http.GetSignedAsync(path, q, ct).ConfigureAwait(false);
                foreach (JsonElement o in result.GetProperty("list").EnumerateArray())
                {
                    OrderStatusReport? report = ParseOrder(o);
                    if (report is not null)
                    {
                        reports.Add(report);
                    }
                }

                cursor = result.Str("nextPageCursor");
            }
            while (!string.IsNullOrEmpty(cursor));
        }

        return reports;
    }

    /// <summary>
    /// How this venue is asked about a market rather than about one instrument, as one query per request to make.
    /// <para>
    /// Naming an instrument is one request whatever the family. Asking about everything is not: this venue takes a
    /// symbol, a settle coin or a base coin, and which of those exists differs per family. Spot answers with no
    /// filter at all. The linear family settles in one coin per request, which is the pre-existing shape - a
    /// client holding USDC contracts as well as USDT ones is asked once per coin now rather than for USDT alone.
    /// An INVERSE contract settles in its own base coin, so there is no single settle coin for that market and the
    /// coins come from the contracts this client holds. An option is asked for by underlying.
    /// </para>
    /// <para>
    /// An empty list means this client holds nothing that could tell it what to ask for, which is not the same as
    /// the account holding nothing - so it says so rather than sending a request the venue would refuse for a
    /// missing parameter and having that read as an empty account.
    /// </para>
    /// <para>
    /// Which filter each family really requires could not be confirmed without a key: every read below is signed.
    /// What is certain is that one of them is required, because the venue refuses an unfiltered whole-market read.
    /// </para>
    /// </summary>
    private IReadOnlyList<IReadOnlyDictionary<string, string>> Markets(InstrumentId? instrumentId)
    {
        if (instrumentId is { } id)
        {
            return [new Dictionary<string, string>(StringComparer.Ordinal) { ["symbol"] = BybitVenue.ToRawSymbol(id) }];
        }

        if (_config.ProductType == BybitProductType.Spot)
        {
            return [new Dictionary<string, string>(StringComparer.Ordinal)];
        }

        bool byUnderlying = _config.ProductType == BybitProductType.Option;
        string parameter = byUnderlying ? "baseCoin" : "settleCoin";
        IReadOnlyList<Instrument> held = [.. Services.Cache.Instruments(Venue).Concat(_instruments.GetAll()).DistinctBy(i => i.Id)];
        string[] coins =
        [
            .. held
                .Select(i => byUnderlying ? i.BaseCurrency?.Code : i.SettlementCurrency.Code)
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
        ];

        if (coins.Length > 0)
        {
            return [.. coins.Select(coin => new Dictionary<string, string>(StringComparer.Ordinal) { [parameter] = coin })];
        }

        if (_config.ProductType == BybitProductType.Linear)
        {
            return [new Dictionary<string, string>(StringComparer.Ordinal) { [parameter] = BybitVenue.LinearSettleCoin }];
        }

        Log.LogWarning(
            "This client holds no Bybit {Category} instruments, so there is no {Parameter} to ask the venue for and "
            + "a whole-market read is skipped rather than sent without one",
            Category,
            parameter);

        return [];
    }

    private OrderStatusReport? ParseOrder(JsonElement o)
    {
        InstrumentId instrumentId = BybitVenue.ToInstrumentId(o.Str("symbol"), _config.ProductType);
        Instrument? instrument = _instruments.Find(instrumentId) ?? Services.Cache.Instrument(instrumentId);
        if (instrument is null)
        {
            return null;
        }

        string linkId = o.Str("orderLinkId");
        decimal price = o.Dec("price");
        decimal trigger = o.Dec("triggerPrice");
        decimal avg = o.Dec("avgPrice");
        return new OrderStatusReport(AccountId, instrumentId, string.IsNullOrEmpty(linkId) ? null : new ClientOrderId(linkId), new VenueOrderId(o.Str("orderId")),
            o.Str("side") == "Buy" ? OrderSide.Buy : OrderSide.Sell, MapOrderType(o.Str("orderType"), o.Str("stopOrderType")), MapTif(o.Str("timeInForce")), MapStatus(o.Str("orderStatus")),
            instrument.MakeQuantity(o.Dec("qty")), instrument.MakeQuantity(o.Dec("cumExecQty")), o.Ms("createdTime"), o.Ms("updatedTime"), Clock.Timestamp, Guid.NewGuid(),
            price > 0m ? instrument.MakePrice(price) : null, trigger > 0m ? instrument.MakePrice(trigger) : null, TriggerType.Default, null, TrailingOffsetType.Price, null,
            avg > 0m ? avg : null, o.Str("timeInForce") == "PostOnly", o.Bool("reduceOnly"), null, null, ContingencyType.None, o.Str("rejectReason"));
    }

    public override async Task<IReadOnlyList<FillReport>> GenerateFillReportsAsync(InstrumentId? instrumentId, VenueOrderId? venueOrderId, UnixNanos? start, UnixNanos? end, CancellationToken ct)
    {
        Dictionary<string, string> q = new() { ["category"] = Category, ["limit"] = BybitVenue.FillReportPage.ToString(CultureInfo.InvariantCulture) };
        if (instrumentId is { } id)
        {
            q["symbol"] = BybitVenue.ToRawSymbol(id);
        }

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

        List<FillReport> fills = new();
        JsonElement result = await _http.GetSignedAsync("/v5/execution/list", q, ct).ConfigureAwait(false);
        foreach (JsonElement x in result.GetProperty("list").EnumerateArray())
        {
            if (!BybitVenue.IsTradeExecution(x.Str("execType")))
            {
                // The same history endpoint returns funding, delivery and settlement rows; a fill report made from one
                // of those tells reconciliation about a trade that never happened.
                continue;
            }

            InstrumentId fillInstrument = BybitVenue.ToInstrumentId(x.Str("symbol"), _config.ProductType);
            Instrument? instrument = _instruments.Find(fillInstrument) ?? Services.Cache.Instrument(fillInstrument);
            if (instrument is null)
            {
                continue;
            }

            string linkId = x.Str("orderLinkId");
            Currency feeCurrency = x.Str("feeCurrency").Length > 0 ? Currency.FromCode(x.Str("feeCurrency")) : instrument.SettlementCurrency;
            fills.Add(new FillReport(AccountId, fillInstrument, new VenueOrderId(x.Str("orderId")), new TradeId(x.Str("execId")), x.Str("side") == "Buy" ? OrderSide.Buy : OrderSide.Sell,
                instrument.MakeQuantity(x.Dec("execQty")), instrument.MakePrice(x.Dec("execPrice")), new Money(x.Dec("execFee"), feeCurrency),
                x.Bool("isMaker") ? LiquiditySide.Maker : LiquiditySide.Taker, x.Ms("execTime"), Clock.Timestamp, Guid.NewGuid(), string.IsNullOrEmpty(linkId) ? null : new ClientOrderId(linkId)));
        }

        return fills;
    }

    public override async Task<IReadOnlyList<PositionStatusReport>> GeneratePositionStatusReportsAsync(InstrumentId? instrumentId, UnixNanos? start, UnixNanos? end, CancellationToken ct)
    {
        if (_config.ProductType == BybitProductType.Spot)
        {
            // A cash account holds balances rather than positions, so an empty list is the whole of the right
            // answer here and asking the venue would be asking about something this market does not have.
            return [];
        }

        List<PositionStatusReport> reports = new();
        foreach (IReadOnlyDictionary<string, string> market in Markets(instrumentId))
        {
            Dictionary<string, string> q = new(market, StringComparer.Ordinal) { ["category"] = Category };
            JsonElement result = await _http.GetSignedAsync("/v5/position/list", q, ct).ConfigureAwait(false);
            foreach (JsonElement p in result.GetProperty("list").EnumerateArray())
            {
                decimal size = p.Dec("size");
                if (size == 0m)
                {
                    continue;
                }

                InstrumentId posId = BybitVenue.ToInstrumentId(p.Str("symbol"), _config.ProductType);
                Instrument? instrument = _instruments.Find(posId) ?? Services.Cache.Instrument(posId);
                if (instrument is null)
                {
                    continue;
                }

                reports.Add(new PositionStatusReport(AccountId, posId, p.Str("side") == "Buy" ? PositionSide.Long : PositionSide.Short, instrument.MakeQuantity(size),
                    p.Ms("updatedTime"), Clock.Timestamp, Guid.NewGuid(), null, p.Dec("avgPrice") > 0m ? p.Dec("avgPrice") : null));
            }
        }

        return reports;
    }

    private static OrderType MapOrderType(string orderType, string stopOrderType)
    {
        bool conditional = !string.IsNullOrEmpty(stopOrderType) && stopOrderType != "UNKNOWN";
        return (orderType, conditional) switch
        {
            ("Market", false) => OrderType.Market,
            ("Limit", false) => OrderType.Limit,
            ("Market", true) => OrderType.StopMarket,
            ("Limit", true) => OrderType.StopLimit,
            _ => OrderType.Market,
        };
    }

    private static TimeInForce MapTif(string tif) => tif switch
    {
        "IOC" => TimeInForce.Ioc,
        "FOK" => TimeInForce.Fok,
        _ => TimeInForce.Gtc,
    };

    private static OrderStatus MapStatus(string status) => status switch
    {
        "New" or "Untriggered" => OrderStatus.Accepted,
        "Triggered" => OrderStatus.Triggered,
        "PartiallyFilled" => OrderStatus.PartiallyFilled,
        "Filled" => OrderStatus.Filled,
        "Cancelled" or "PartiallyFilledCanceled" or "Deactivated" => OrderStatus.Canceled,
        "Rejected" => OrderStatus.Rejected,
        _ => OrderStatus.Accepted,
    };
}

public sealed class BybitExecutionClientFactory : IExecutionClientFactory
{
    public string Name => "BYBIT";

    public Type ConfigType => typeof(BybitExecutionClientConfig);

    public IExecutionClient Create(ClientId clientId, ExecutionClientConfig config, KernelServices services) =>
        new BybitExecutionClient(clientId, (BybitExecutionClientConfig)config, services);
}

public sealed class BybitPlugin : Core.Plugins.IPlugin, Core.Adapters.IVenuePlugin
{
    public string Id => "bytex.bybit";

    /// <summary>
    /// Bybit as four families behind one host. Unlike Binance the address does not change with the product - the
    /// category does - so what differs here is the fees, the funding and the configuration, not where to ask.
    /// <para>
    /// The option family is the one that does not fit the shape the other three share, and every difference below
    /// was measured rather than assumed: it is charged no funding, the venue has no candle endpoint for it and no
    /// candle socket topic that delivers, and it publishes neither a margin nor a leverage ceiling. Those are
    /// declared false and null rather than copied from the family beside it.
    /// </para>
    /// </summary>
    public Core.Adapters.VenueDescriptor Describe() => new()
    {
        Venue = BybitVenue.Venue,
        DisplayName = "Bybit",

        // A header on the order request, so an id changes nothing about the order itself.
        BrokerTag = Core.Adapters.BrokerTag.RequestHeader,
        BrokerProgramme = Core.Adapters.BrokerProgramme.Carried,
        Families =
        [
            new Core.Adapters.VenueFamily
            {
                Name = "spot",
                InstrumentClasses = [InstrumentClass.Spot],
                PaysFunding = false,
                Collateral = VenueCollateral.None,
                HttpBase = BybitVenue.DefaultHttpBase,
                WsBase = BybitVenue.DefaultWsBase,
                Key = BybitKey,
                Config = new Dictionary<string, string> { ["productType"] = nameof(BybitProductType.Spot) },
                DefaultFees = FeesOf(BybitProductType.Spot),
                Capabilities = new Core.Adapters.VenueCapabilities
                {
                    LoadOneInstrument = true,
                    ListInstruments = true,
                    BarHistory = true,

                    // Spot pays no funding, so there is none to fetch.
                    FundingHistory = false,
                    MarketData = true,
                    Execution = true,
                    AmendOrders = true,
                },
            },
            new Core.Adapters.VenueFamily
            {
                Name = "linear",
                InstrumentClasses = [InstrumentClass.Swap, InstrumentClass.Future],
                PaysFunding = true,
                Collateral = VenueCollateral.Quote,
                HttpBase = BybitVenue.DefaultHttpBase,
                WsBase = BybitVenue.DefaultWsBase,
                Key = BybitKey,
                Config = new Dictionary<string, string> { ["productType"] = nameof(BybitProductType.Linear) },
                DefaultFees = FeesOf(BybitProductType.Linear),
                Capabilities = new Core.Adapters.VenueCapabilities
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
            new Core.Adapters.VenueFamily
            {
                Name = "inverse",

                // The family every adapter in this repository used to exclude, and the stated reason for excluding
                // it was that a USD-quoted, base-settled contract cannot be sized in base units without a price.
                // It can be sized in the venue's own contracts, which is how the venue sizes it, and the engine's
                // money arithmetic inverts on its own from the instrument's IsInverse flag.
                InstrumentClasses = [InstrumentClass.Swap, InstrumentClass.Future],
                PaysFunding = true,
                Collateral = VenueCollateral.Base,
                HttpBase = BybitVenue.DefaultHttpBase,
                WsBase = BybitVenue.DefaultWsBase,
                Key = BybitKey,
                Config = new Dictionary<string, string> { ["productType"] = nameof(BybitProductType.Inverse) },
                DefaultFees = FeesOf(BybitProductType.Inverse),
                Capabilities = new Core.Adapters.VenueCapabilities
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
            new Core.Adapters.VenueFamily
            {
                Name = "option",
                InstrumentClasses = [InstrumentClass.Option],

                // Not funded, and that is what an option is rather than something this adapter cannot fetch: the
                // venue's funding endpoint refuses the category outright.
                PaysFunding = false,
                Collateral = VenueCollateral.Quote,
                HttpBase = BybitVenue.DefaultHttpBase,
                WsBase = BybitVenue.DefaultWsBase,
                Key = BybitKey,
                Config = new Dictionary<string, string> { ["productType"] = nameof(BybitProductType.Option) },
                DefaultFees = FeesOf(BybitProductType.Option),
                Capabilities = new Core.Adapters.VenueCapabilities
                {
                    LoadOneInstrument = true,
                    ListInstruments = true,

                    // The one capability of the four families that is false, and it is the venue's answer rather
                    // than this adapter's limit: /v5/market/kline refuses category=option, and the option socket
                    // accepts a kline subscription, reports it as a success and never sends a candle. So an option
                    // cannot be backtested from this venue's own history at all.
                    BarHistory = false,
                    FundingHistory = false,
                    MarketData = true,
                    Execution = true,

                    // The venue's amend endpoint takes the category this adapter already sends and the execution
                    // client is written per category rather than per family, so an option amend is attempted the
                    // same way a contract amend is. Whether the venue honours it could not be verified: the amend
                    // path is signed, and no option order could be placed to amend without a key.
                    AmendOrders = true,
                },
            },
        ],
    };

    /// <summary>
    /// What a family charges before an instrument is loaded, taken from the one place this adapter states it, so a
    /// declaration cannot drift from the rates the instruments it describes are built with.
    /// </summary>
    private static Core.Adapters.VenueFees FeesOf(BybitProductType type)
    {
        (decimal maker, decimal taker) = BybitInstrumentProvider.Fees(type);
        return new Core.Adapters.VenueFees(maker, taker);
    }

    private static Core.Adapters.VenueKey BybitKey => new()
    {
        Parts =
        [
            new Core.Adapters.VenueKeyPart("API key", BybitVenue.EnvApiKey, Secret: false),
            new Core.Adapters.VenueKeyPart("API secret", BybitVenue.EnvApiSecret, Secret: true),
        ],
    };

    public string Version => typeof(BybitPlugin).Assembly.GetName().Version?.ToString() ?? "0";

    public void Register(Core.Plugins.IPluginRegistry registry)
    {
        registry.AddDataClientFactory(new BybitDataClientFactory());
        registry.AddExecutionClientFactory(new BybitExecutionClientFactory());
    }
}
