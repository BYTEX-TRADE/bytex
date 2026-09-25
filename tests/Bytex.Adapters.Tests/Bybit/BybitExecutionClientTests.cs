using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bytex.Adapters.Bybit;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Model.Reports;

namespace Bytex.Adapters.Tests.Bybit;

// Why: the Bybit V5 order API is JSON-bodied, header-signed and reports errors inside HTTP 200 envelopes.
// Orders must carry the documented fields, venue refusals must become rejection events with the venue's
// message, and the private stream must turn order/execution/wallet topics into the right engine events.
public sealed class BybitExecutionClientTests
{
    private static readonly string _ok = BybitPayloads.Envelope("{\"orderId\":\"1321003749386327552\",\"orderLinkId\":\"x\"}");

    private const string Wallet = """
        {"list":[{"accountType":"UNIFIED","totalEquity":"3.31216591","coin":[
          {"coin":"USDT","equity":"1000.5","walletBalance":"1000.5","locked":"0","totalOrderIM":"100.25","totalPositionIM":"50.25","availableToWithdraw":"850"},
          {"coin":"BTC","equity":"0.5","walletBalance":"0.5","locked":"0.1","totalOrderIM":"0","totalPositionIM":"0"}]}]}
        """;

    private static Order Build(BybitExecRig rig, string kind)
    {
        InstrumentId id = rig.Instrument.Id;
        return kind switch
        {
            "market-buy" => rig.Orders.Market(id, OrderSide.Buy, rig.Qty(0.5m)),
            "market-sell-reduce-only" => rig.Orders.Market(id, OrderSide.Sell, rig.Qty(0.5m), reduceOnly: true),
            "market-buy-quote-quantity" => rig.Orders.Market(id, OrderSide.Buy, new Quantity(100m, 2), quoteQuantity: true),
            "limit-gtc" => rig.Orders.Limit(id, OrderSide.Buy, rig.Qty(0.5m), rig.Px(25_000.1m)),
            "limit-ioc" => rig.Orders.Limit(id, OrderSide.Sell, rig.Qty(0.5m), rig.Px(25_000.1m), TimeInForce.Ioc),
            "limit-fok" => rig.Orders.Limit(id, OrderSide.Buy, rig.Qty(0.5m), rig.Px(25_000.1m), TimeInForce.Fok),
            "limit-post-only" => rig.Orders.Limit(id, OrderSide.Buy, rig.Qty(0.5m), rig.Px(25_000.1m), postOnly: true),
            "stop-market-sell" => rig.Orders.StopMarket(id, OrderSide.Sell, rig.Qty(0.5m), rig.Px(24_000m)),
            "stop-market-buy-mark" => rig.Orders.StopMarket(id, OrderSide.Buy, rig.Qty(0.5m), rig.Px(26_000m), TriggerType.MarkPrice),
            "stop-limit-sell-index" => rig.Orders.StopLimit(id, OrderSide.Sell, rig.Qty(0.5m), rig.Px(23_990m), rig.Px(24_000m), TriggerType.IndexPrice),
            "market-if-touched-sell" => rig.Orders.MarketIfTouched(id, OrderSide.Sell, rig.Qty(0.5m), rig.Px(26_000m)),
            "limit-if-touched-buy" => rig.Orders.LimitIfTouched(id, OrderSide.Buy, rig.Qty(0.5m), rig.Px(23_990m), rig.Px(24_000m)),
            "trailing-stop" => rig.Orders.TrailingStopMarket(id, OrderSide.Sell, rig.Qty(0.5m), 100m),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }

    // ----- Commands -----

    [Theory]
    [InlineData("market-buy", "category=linear;symbol=BTCUSDT;side=Buy;orderType=Market;qty=0.5", "price;triggerPrice;reduceOnly;marketUnit;orderFilter")]
    [InlineData("market-sell-reduce-only", "side=Sell;orderType=Market;reduceOnly=true", "price")]
    [InlineData("limit-gtc", "side=Buy;orderType=Limit;price=25000.1;timeInForce=GTC;qty=0.5", "triggerPrice")]
    [InlineData("limit-ioc", "side=Sell;orderType=Limit;timeInForce=IOC", "triggerPrice")]
    [InlineData("limit-fok", "orderType=Limit;timeInForce=FOK", "triggerPrice")]
    [InlineData("limit-post-only", "orderType=Limit;timeInForce=PostOnly", "triggerPrice")]
    [InlineData("stop-market-sell", "orderType=Market;triggerPrice=24000;triggerDirection=2;triggerBy=LastPrice", "price;orderFilter")]
    [InlineData("stop-market-buy-mark", "orderType=Market;triggerPrice=26000;triggerDirection=1;triggerBy=MarkPrice", "price")]
    [InlineData("stop-limit-sell-index", "orderType=Limit;price=23990;triggerPrice=24000;triggerDirection=2;triggerBy=IndexPrice;timeInForce=GTC", "")]
    [InlineData("market-if-touched-sell", "orderType=Market;triggerPrice=26000;triggerDirection=1", "price")]
    [InlineData("limit-if-touched-buy", "orderType=Limit;price=23990;triggerPrice=24000;triggerDirection=2", "")]
    public async Task Linear_orders_are_posted_to_order_create_with_the_documented_fields(string kind, string expected, string absent)
    {
        await using BybitExecRig rig = new(BybitProductType.Linear);
        rig.Routes.On("POST", "/v5/order/create", _ok);
        Order order = Build(rig, kind);

        await rig.SubmitAsync(order);

        RecordedRequest request = Assert.Single(rig.Server.Requests);
        Assert.Equal("POST", request.Method);
        Assert.StartsWith("application/json", request.Header("Content-Type"));
        BybitExecRig.AssertBody(request, expected + ";orderLinkId=" + order.ClientOrderId.Value, absent);
        BybitExecRig.AssertSigned(request);
    }

    [Theory]
    [InlineData("market-buy", "category=spot;symbol=BTCUSDT;side=Buy;orderType=Market;qty=0.5", "marketUnit;reduceOnly")]
    [InlineData("market-buy-quote-quantity", "category=spot;orderType=Market;qty=100;marketUnit=quoteCoin", "price")]
    [InlineData("stop-market-sell", "category=spot;orderType=Market;triggerPrice=24000;orderFilter=StopOrder", "reduceOnly")]
    public async Task Spot_orders_carry_the_spot_category_and_spot_only_fields(string kind, string expected, string absent)
    {
        await using BybitExecRig rig = new(BybitProductType.Spot);
        rig.Routes.On("POST", "/v5/order/create", _ok);

        await rig.SubmitAsync(Build(rig, kind));

        BybitExecRig.AssertBody(Assert.Single(rig.Server.Requests), expected, absent);
    }

    [Fact]
    public async Task The_configured_default_trigger_type_is_used_when_the_order_does_not_choose_one()
    {
        await using BybitExecRig rig = new(BybitProductType.Linear, TriggerType.MarkPrice);
        rig.Routes.On("POST", "/v5/order/create", _ok);

        await rig.SubmitAsync(Build(rig, "stop-market-sell"));

        BybitExecRig.AssertBody(Assert.Single(rig.Server.Requests), "triggerBy=MarkPrice", string.Empty);
    }

    [Fact]
    public async Task A_successful_submission_emits_only_OrderSubmitted_under_the_unified_account()
    {
        await using BybitExecRig rig = new(BybitProductType.Linear);
        rig.Routes.On("POST", "/v5/order/create", _ok);
        Order order = Build(rig, "limit-gtc");

        await rig.SubmitAsync(order);

        OrderSubmitted submitted = Assert.IsType<OrderSubmitted>(Assert.Single(rig.Sink.AllOrderEvents));
        Assert.Equal(order.ClientOrderId, submitted.ClientOrderId);
        Assert.Equal(new AccountId("BYBIT-UNIFIED"), submitted.AccountId);
        Assert.Equal(AccountType.Margin, rig.Client.AccountType);
    }

    [Theory]
    [InlineData(110007, "Insufficient available balance")]
    [InlineData(110094, "Order does not meet minimum order value 5USDT")]
    [InlineData(10001, "params error: price invalid")]
    public async Task An_error_envelope_inside_http_200_becomes_OrderRejected_with_the_venue_message(int code, string message)
    {
        await using BybitExecRig rig = new(BybitProductType.Linear);
        rig.Routes.On("POST", "/v5/order/create", BybitPayloads.Error(code, message));
        Order order = Build(rig, "limit-gtc");

        await rig.SubmitAsync(order);

        Assert.Equal([typeof(OrderSubmitted), typeof(OrderRejected)], rig.Sink.AllOrderEvents.Select(e => e.GetType()));
        Assert.Equal(message, ((OrderRejected)rig.Sink.AllOrderEvents[1]).Reason);
    }

    [Fact]
    public async Task An_unsupported_order_type_is_rejected_locally_without_any_request()
    {
        await using BybitExecRig rig = new(BybitProductType.Linear);

        await rig.SubmitAsync(Build(rig, "trailing-stop"));

        OrderRejected rejected = Assert.IsType<OrderRejected>(Assert.Single(rig.Sink.AllOrderEvents));
        Assert.Contains("TrailingStopMarket", rejected.Reason);
        Assert.Empty(rig.Server.Requests);
    }

    [Fact]
    public async Task Cancel_posts_category_symbol_and_order_link_id_and_a_refusal_becomes_OrderCancelRejected()
    {
        await using BybitExecRig rig = new(BybitProductType.Linear);
        int calls = 0;
        rig.Routes.On("POST", "/v5/order/cancel", _ => StubResponse.Json(Interlocked.Increment(ref calls) == 1 ? _ok : BybitPayloads.Error(110001, "Order does not exist")));
        CancelOrder command = new(rig.Kernel.Services.TraderId, BybitExecRig.Strategy, rig.Instrument.Id, new ClientOrderId("O-1"), null, null, Guid.NewGuid(), TestKernel.Now);

        await rig.Client.CancelOrderAsync(command, CancellationToken.None).WaitAsync(Wait.Timeout);
        await rig.Client.CancelOrderAsync(command, CancellationToken.None).WaitAsync(Wait.Timeout);

        BybitExecRig.AssertBody(rig.Server.Requests[0], "category=linear;symbol=BTCUSDT;orderLinkId=O-1", string.Empty);
        BybitExecRig.AssertSigned(rig.Server.Requests[0]);
        Assert.Equal([typeof(OrderPendingCancel), typeof(OrderPendingCancel), typeof(OrderCancelRejected)], rig.Sink.AllOrderEvents.Select(e => e.GetType()));
        Assert.Equal("Order does not exist", ((OrderCancelRejected)rig.Sink.AllOrderEvents[2]).Reason);
    }

    [Fact]
    public async Task Amend_sends_only_the_fields_being_changed_and_a_refusal_becomes_OrderModifyRejected()
    {
        await using BybitExecRig rig = new(BybitProductType.Linear);
        int calls = 0;
        rig.Routes.On("POST", "/v5/order/amend", _ => StubResponse.Json(Interlocked.Increment(ref calls) == 1 ? _ok : BybitPayloads.Error(10001, "The order remains unchanged")));
        ModifyOrder priceOnly = new(rig.Kernel.Services.TraderId, BybitExecRig.Strategy, rig.Instrument.Id, new ClientOrderId("O-1"), null, null, rig.Px(24_900m), null, null, Guid.NewGuid(), TestKernel.Now);
        ModifyOrder everything = priceOnly with { Quantity = rig.Qty(0.75m), TriggerPrice = rig.Px(24_000m) };

        await rig.Client.ModifyOrderAsync(priceOnly, CancellationToken.None).WaitAsync(Wait.Timeout);
        await rig.Client.ModifyOrderAsync(everything, CancellationToken.None).WaitAsync(Wait.Timeout);

        BybitExecRig.AssertBody(rig.Server.Requests[0], "category=linear;symbol=BTCUSDT;orderLinkId=O-1;price=24900", "qty;triggerPrice");
        BybitExecRig.AssertBody(rig.Server.Requests[1], "price=24900;qty=0.75;triggerPrice=24000", string.Empty);
        Assert.Equal([typeof(OrderPendingUpdate), typeof(OrderPendingUpdate), typeof(OrderModifyRejected)], rig.Sink.AllOrderEvents.Select(e => e.GetType()));
        Assert.Equal("The order remains unchanged", ((OrderModifyRejected)rig.Sink.AllOrderEvents[2]).Reason);
    }

    [Fact]
    public async Task Cancel_all_uses_the_venue_endpoint_for_the_symbol()
    {
        await using BybitExecRig rig = new(BybitProductType.Spot);
        rig.Routes.On("POST", "/v5/order/cancel-all", BybitPayloads.Envelope("{\"list\":[]}"));

        await rig.Client.CancelAllOrdersAsync(new CancelAllOrders(rig.Kernel.Services.TraderId, BybitExecRig.Strategy, rig.Instrument.Id, null, null, Guid.NewGuid(), TestKernel.Now), CancellationToken.None).WaitAsync(Wait.Timeout);

        BybitExecRig.AssertBody(Assert.Single(rig.Server.Requests), "category=spot;symbol=BTCUSDT", string.Empty);
    }

    // ----- Private stream -----

    [Fact]
    public async Task Connecting_publishes_the_wallet_authenticates_the_private_stream_and_subscribes_after_success()
    {
        await using BybitExecRig rig = new(BybitProductType.Linear);

        (WsSession session, string authMessage, string subscribeMessage) = await rig.ConnectAsync(Wallet);

        Assert.Equal("/v5/private", session.Path);
        using JsonDocument auth = JsonDocument.Parse(authMessage);
        JsonElement args = auth.RootElement.GetProperty("args");
        long expires = args[1].GetInt64();
        string expectedSignature = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(BybitExecRig.ApiSecret), Encoding.UTF8.GetBytes("GET/realtime" + expires.ToString(CultureInfo.InvariantCulture))));
        Assert.Equal("auth", auth.RootElement.GetProperty("op").GetString());
        Assert.Equal(BybitExecRig.ApiKey, args[0].GetString());
        Assert.Equal(expectedSignature, args[2].GetString());
        Assert.InRange(expires, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds());
        using JsonDocument subscribe = JsonDocument.Parse(subscribeMessage);
        Assert.Equal(["execution", "order", "wallet"], subscribe.RootElement.GetProperty("args").EnumerateArray().Select(a => a.GetString()).Order());

        RecordedRequest walletRequest = Assert.Single(rig.Server.RequestsTo("/v5/account/wallet-balance"));
        Assert.Equal("UNIFIED", walletRequest.Query("accountType"));
        BybitExecRig.AssertSigned(walletRequest);
        AccountState state = await rig.Sink.NextAccountStateAsync();
        AccountBalance usdt = state.Balances.Single(b => b.Currency.Code == "USDT");
        AccountBalance btc = state.Balances.Single(b => b.Currency.Code == "BTC");
        Assert.Equal(1000.5m, usdt.Total.Amount);
        Assert.Equal(150.5m, usdt.Locked.Amount); // order margin 100.25 + position margin 50.25
        Assert.Equal(850m, usdt.Free.Amount);
        Assert.Equal(0.1m, btc.Locked.Amount);
    }

    private static string OrderMessage(string status, string linkId = "O-1", string category = "linear", string rejectReason = "EC_NoError", string qty = "0.500", string price = "25000.0") => $$"""
        {"id":"5923240c6880ab-c59f-420b-9adb-3639adc9dd90","topic":"order","creationTime":1672364262474,"data":[{"symbol":"BTCUSDT","orderId":"5cf98598-39a7-459e-97bf-76ca765ee020","side":"Buy","orderType":"Limit","cancelType":"UNKNOWN","price":"{{price}}","qty":"{{qty}}","orderIv":"","timeInForce":"GTC","orderStatus":"{{status}}","orderLinkId":"{{linkId}}","reduceOnly":false,"leavesQty":"","cumExecQty":"0","avgPrice":"","createdTime":"1672364262444","updatedTime":"1672364262457","rejectReason":"{{rejectReason}}","stopOrderType":"","triggerPrice":"","category":"{{category}}"}]}
        """;

    [Theory]
    [InlineData("New", typeof(OrderAccepted))]
    [InlineData("Untriggered", typeof(OrderAccepted))]
    [InlineData("Triggered", typeof(OrderTriggered))]
    [InlineData("Cancelled", typeof(OrderCanceled))]
    [InlineData("Deactivated", typeof(OrderCanceled))]
    [InlineData("PartiallyFilledCanceled", typeof(OrderCanceled))]
    [InlineData("Rejected", typeof(OrderRejected))]
    public async Task Each_order_topic_status_produces_its_engine_event(string status, Type expected)
    {
        await using BybitExecRig rig = new(BybitProductType.Linear);
        (WsSession session, _, _) = await rig.ConnectAsync(Wallet);

        await session.SendTextAsync(OrderMessage(status, rejectReason: status == "Rejected" ? "EC_PostOnlyWillTakeLiquidity" : "EC_NoError"));

        OrderEvent e = await rig.Sink.NextOrderEventAsync();
        Assert.IsType(expected, e);
        Assert.Equal(new ClientOrderId("O-1"), e.ClientOrderId);
        Assert.Equal(InstrumentId.Parse("BTCUSDT-PERP.BYBIT"), e.InstrumentId);
        Assert.Equal(StrategyId.External, e.StrategyId);
        Assert.Equal(1_672_364_262_457_000_000L, e.TsEvent.Value);
        if (e is OrderRejected rejected)
        {
            Assert.Equal("EC_PostOnlyWillTakeLiquidity", rejected.Reason);
        }
        else
        {
            Assert.Equal(new VenueOrderId("5cf98598-39a7-459e-97bf-76ca765ee020"), e.VenueOrderId);
        }
    }

    [Fact]
    public async Task Order_updates_of_another_category_are_ignored_by_this_client()
    {
        await using BybitExecRig rig = new(BybitProductType.Linear);
        (WsSession session, _, _) = await rig.ConnectAsync(Wallet);

        await session.SendTextAsync(OrderMessage("New", linkId: "SPOT-ORDER", category: "spot"));
        await session.SendTextAsync(OrderMessage("New", linkId: "LINEAR-ORDER"));

        OrderEvent first = await rig.Sink.NextOrderEventAsync();
        Assert.Equal(new ClientOrderId("LINEAR-ORDER"), first.ClientOrderId);
    }

    [Fact]
    public async Task A_New_status_after_a_pending_update_confirms_the_amendment_with_the_new_quantity_and_price()
    {
        await using BybitExecRig rig = new(BybitProductType.Linear);
        (WsSession session, _, _) = await rig.ConnectAsync(Wallet);
        LimitOrder order = rig.Orders.Limit(rig.Instrument.Id, OrderSide.Buy, rig.Qty(0.5m), rig.Px(25_000m));
        AccountId account = rig.Client.AccountId;
        order.Apply(new OrderSubmitted(order.TraderId, order.StrategyId, order.InstrumentId, order.ClientOrderId, account, Guid.NewGuid(), TestKernel.Now, TestKernel.Now));
        order.Apply(new OrderAccepted(order.TraderId, order.StrategyId, order.InstrumentId, order.ClientOrderId, new VenueOrderId("5cf98598-39a7-459e-97bf-76ca765ee020"), account, Guid.NewGuid(), TestKernel.Now, TestKernel.Now));
        order.Apply(new OrderPendingUpdate(order.TraderId, order.StrategyId, order.InstrumentId, order.ClientOrderId, order.VenueOrderId, account, Guid.NewGuid(), TestKernel.Now, TestKernel.Now));
        rig.Kernel.Kernel.Cache.AddOrder(order);

        await session.SendTextAsync(OrderMessage("New", linkId: order.ClientOrderId.Value, qty: "0.750", price: "24900.0"));

        OrderUpdated updated = await rig.Sink.NextOrderEventAsync<OrderUpdated>();
        Assert.Equal(BybitExecRig.Strategy, updated.StrategyId);
        Assert.Equal(new Quantity(0.75m, 3), updated.Quantity);
        Assert.Equal(new Price(24_900m, 1), updated.Price);
    }

    private static string ExecutionMessage(string execId, string execType = "Trade", string linkId = "O-1", string feeCurrency = "") => $$"""
        {"id":"592324803b2785-26fa-4214-9963-bdd4727f07be","topic":"execution","creationTime":1672364174455,"data":[{"category":"linear","symbol":"BTCUSDT","execFee":"0.0075015","execId":"{{execId}}","execPrice":"25005.0","execQty":"0.500","execType":"{{execType}}","execValue":"12502.5","isMaker":false,"feeRate":"0.0006","orderId":"5cf98598-39a7-459e-97bf-76ca765ee020","orderLinkId":"{{linkId}}","orderPrice":"25010.0","orderQty":"0.500","orderType":"Market","stopOrderType":"UNKNOWN","side":"Sell","execTime":"1672364174443","feeCurrency":"{{feeCurrency}}","closedSize":"0"}]}
        """;

    [Fact]
    public async Task An_execution_becomes_a_fill_and_a_replayed_execId_is_not_applied_twice()
    {
        await using BybitExecRig rig = new(BybitProductType.Linear);
        (WsSession session, _, _) = await rig.ConnectAsync(Wallet);

        await session.SendTextAsync(ExecutionMessage("exec-1"));
        await session.SendTextAsync(ExecutionMessage("exec-1"));
        await session.SendTextAsync(ExecutionMessage("exec-2", feeCurrency: "USDC"));

        OrderFilled first = await rig.Sink.NextOrderEventAsync<OrderFilled>();
        OrderFilled second = await rig.Sink.NextOrderEventAsync<OrderFilled>();
        Assert.Equal(new TradeId("exec-1"), first.TradeId);
        Assert.Equal(new TradeId("exec-2"), second.TradeId); // the duplicate exec-1 produced nothing in between
        Assert.Equal(new ClientOrderId("O-1"), first.ClientOrderId);
        Assert.Equal(new VenueOrderId("5cf98598-39a7-459e-97bf-76ca765ee020"), first.VenueOrderId);
        Assert.Equal(OrderSide.Sell, first.OrderSide);
        Assert.Equal(OrderType.Market, first.OrderType);
        Assert.Equal(new Quantity(0.5m, 3), first.LastQty);
        Assert.Equal(new Price(25_005m, 1), first.LastPx);
        Assert.Equal(LiquiditySide.Taker, first.LiquiditySide);
        Assert.Equal(new Money(0.0075015m, Currencies.USDT), first.Commission); // no feeCurrency on derivatives: the settle coin
        Assert.Equal("USDC", second.Commission.Currency.Code);
        Assert.Equal(1_672_364_174_443_000_000L, first.TsEvent.Value);
    }

    [Fact]
    public async Task A_funding_fee_record_on_the_execution_topic_is_not_booked_as_a_fill()
    {
        await using BybitExecRig rig = new(BybitProductType.Linear);
        (WsSession session, _, _) = await rig.ConnectAsync(Wallet);

        await session.SendTextAsync(ExecutionMessage("funding-1", execType: "Funding", linkId: string.Empty));
        await session.SendTextAsync(ExecutionMessage("exec-9"));

        OrderFilled first = await rig.Sink.NextOrderEventAsync<OrderFilled>();
        Assert.Equal(new TradeId("exec-9"), first.TradeId);
    }

    [Theory]
    [InlineData("Funding")]
    [InlineData("Delivery")]
    [InlineData("Settle")]
    [InlineData("MovePosition")]
    public async Task A_row_on_the_execution_topic_that_is_not_a_trade_is_not_a_fill(string execType)
    {
        // Each of these carries an execQty that is the position, not a trade: taking it for a fill would double the
        // position, and funding arrives every interval for as long as the position is held.
        await using BybitExecRig rig = new(BybitProductType.Linear);
        (WsSession session, _, _) = await rig.ConnectAsync(Wallet);

        await session.SendTextAsync(ExecutionMessage("not-a-trade", execType: execType, linkId: string.Empty));
        await session.SendTextAsync(ExecutionMessage("exec-9"));

        OrderFilled filled = await rig.Sink.NextOrderEventAsync<OrderFilled>();
        Assert.Equal(new TradeId("exec-9"), filled.TradeId);
        Assert.Single(rig.Sink.AllOrderEvents.OfType<OrderFilled>());
    }

    [Theory]
    [InlineData("Trade")]
    [InlineData("AdlTrade")]
    [InlineData("BustTrade")]
    [InlineData("BlockTrade")]
    [InlineData("")]
    public async Task Every_kind_of_trade_row_is_a_fill(string execType)
    {
        // A liquidation and an auto-deleverage are trades the venue made for the account; dropping them would leave a
        // position the engine thinks is still open. A row with no type at all is a trade, which is what spot sends.
        await using BybitExecRig rig = new(BybitProductType.Linear);
        (WsSession session, _, _) = await rig.ConnectAsync(Wallet);

        await session.SendTextAsync(ExecutionMessage("exec-7", execType: execType));

        OrderFilled filled = await rig.Sink.NextOrderEventAsync<OrderFilled>();
        Assert.Equal(new TradeId("exec-7"), filled.TradeId);
        Assert.Equal(new Quantity(0.5m, 3), filled.LastQty);
    }

    [Fact]
    public async Task A_wallet_push_reports_balances_at_the_message_creation_time()
    {
        await using BybitExecRig rig = new(BybitProductType.Linear);
        (WsSession session, _, _) = await rig.ConnectAsync(Wallet);
        await rig.Sink.NextAccountStateAsync(); // REST snapshot

        await session.SendTextAsync("""
            {"id":"592324d2bce751-ad38-48eb-8f42-4671d1fb4d4e","topic":"wallet","creationTime":1700034722104,"data":[{"accountType":"UNIFIED","coin":[{"coin":"USDT","equity":"2000","walletBalance":"2000","locked":"0","totalOrderIM":"0","totalPositionIM":"500"}]}]}
            """);

        AccountState state = await rig.Sink.NextAccountStateAsync();
        AccountBalance usdt = Assert.Single(state.Balances);
        Assert.Equal(2000m, usdt.Total.Amount);
        Assert.Equal(1500m, usdt.Free.Amount);
        Assert.Equal(1_700_034_722_104_000_000L, state.TsEvent.Value);
    }

    // ----- Reports -----

    private static string OrderRow(string status = "New", string orderType = "Limit", string stopOrderType = "", string tif = "GTC", string symbol = "BTCUSDT") => $$"""
        {"symbol":"{{symbol}}","orderId":"1321052653536515584","orderLinkId":"O-20231114-001","side":"Sell","orderType":"{{orderType}}","stopOrderType":"{{stopOrderType}}","price":"25000.5","qty":"1.000","timeInForce":"{{tif}}","orderStatus":"{{status}}","cumExecQty":"0.400","avgPrice":"25001.0","triggerPrice":"0","reduceOnly":true,"rejectReason":"EC_NoError","createdTime":"1672217748277","updatedTime":"1672217748287"}
        """;

    private static async Task<OrderStatusReport> SingleReportAsync(BybitExecRig rig, string row)
    {
        rig.Routes.On("GET", "/v5/order/realtime", BybitPayloads.Envelope("{\"list\":[" + row + "],\"nextPageCursor\":\"\",\"category\":\"linear\"}"));
        OrderStatusReport? report = await rig.Client.GenerateOrderStatusReportAsync(rig.Instrument.Id, new ClientOrderId("O-20231114-001"), null, CancellationToken.None).WaitAsync(Wait.Timeout);
        return Assert.IsType<OrderStatusReport>(report);
    }

    [Fact]
    public async Task Asking_after_one_order_reaches_the_venue()
    {
        // The command a strategy sends when it wants to know what became of an order. It used to be answered here by
        // a completed task and no request at all, which reads to the caller exactly like a query that worked.
        await using BybitExecRig rig = new(BybitProductType.Linear);
        rig.Routes
            .On("GET", "/v5/order/realtime", BybitPayloads.Envelope("{\"list\":[" + OrderRow() + "],\"nextPageCursor\":\"\",\"category\":\"linear\"}"))
            .On("GET", "/v5/execution/list", BybitPayloads.Envelope("""
                {"list":[{"symbol":"BTCUSDT","orderId":"1321052653536515584","orderLinkId":"O-20231114-001","side":"Sell","execId":"e-1","execPrice":"25001.0","execQty":"0.400","execFee":"0.0060","feeCurrency":"","isMaker":true,"execType":"Trade","execTime":"1672282722429"}],"nextPageCursor":""}
                """));

        await rig.Client.QueryOrderAsync(
            new QueryOrder(rig.Kernel.Services.TraderId, BybitExecRig.Strategy, rig.Instrument.Id, new ClientOrderId("O-20231114-001"), null, null, Guid.NewGuid(), TestKernel.Now),
            CancellationToken.None).WaitAsync(Wait.Timeout);

        RecordedRequest order = Assert.Single(rig.Server.RequestsTo("/v5/order/realtime"));
        Assert.Equal("O-20231114-001", order.Query("orderLinkId"));
        BybitExecRig.AssertSigned(order);

        // And that order's fills, by the id the venue gave it.
        RecordedRequest fills = Assert.Single(rig.Server.RequestsTo("/v5/execution/list"));
        Assert.Equal("1321052653536515584", fills.Query("orderId"));
        BybitExecRig.AssertSigned(fills);
    }

    [Fact]
    public async Task An_order_query_maps_identity_quantities_prices_flags_and_times()
    {
        await using BybitExecRig rig = new(BybitProductType.Linear);

        OrderStatusReport report = await SingleReportAsync(rig, OrderRow(status: "PartiallyFilled", tif: "PostOnly"));

        RecordedRequest request = Assert.Single(rig.Server.Requests);
        Assert.Equal("linear", request.Query("category"));
        Assert.Equal("BTCUSDT", request.Query("symbol"));
        Assert.Equal("O-20231114-001", request.Query("orderLinkId"));
        BybitExecRig.AssertSigned(request);
        Assert.Equal(new AccountId("BYBIT-UNIFIED"), report.AccountId);
        Assert.Equal(InstrumentId.Parse("BTCUSDT-PERP.BYBIT"), report.InstrumentId);
        Assert.Equal(new ClientOrderId("O-20231114-001"), report.ClientOrderId);
        Assert.Equal(new VenueOrderId("1321052653536515584"), report.VenueOrderId);
        Assert.Equal(OrderSide.Sell, report.OrderSide);
        Assert.Equal(OrderStatus.PartiallyFilled, report.OrderStatus);
        Assert.Equal(new Quantity(1m, 3), report.Quantity);
        Assert.Equal(new Quantity(0.4m, 3), report.FilledQuantity);
        Assert.Equal(new Price(25_000.5m, 1), report.Price);
        Assert.Null(report.TriggerPrice);
        Assert.Equal(25_001.0m, report.AvgPx);
        Assert.True(report.PostOnly);
        Assert.True(report.ReduceOnly);
        Assert.Equal(1_672_217_748_277_000_000L, report.TsAccepted.Value);
        Assert.Equal(1_672_217_748_287_000_000L, report.TsLast.Value);
    }

    [Theory]
    [InlineData("New", OrderStatus.Accepted)]
    [InlineData("Untriggered", OrderStatus.Accepted)]
    [InlineData("Triggered", OrderStatus.Triggered)]
    [InlineData("PartiallyFilled", OrderStatus.PartiallyFilled)]
    [InlineData("Filled", OrderStatus.Filled)]
    [InlineData("Cancelled", OrderStatus.Canceled)]
    [InlineData("PartiallyFilledCanceled", OrderStatus.Canceled)]
    [InlineData("Deactivated", OrderStatus.Canceled)]
    [InlineData("Rejected", OrderStatus.Rejected)]
    public async Task Every_documented_order_status_maps_to_its_engine_status(string venueStatus, OrderStatus expected)
    {
        await using BybitExecRig rig = new(BybitProductType.Linear);

        Assert.Equal(expected, (await SingleReportAsync(rig, OrderRow(status: venueStatus))).OrderStatus);
    }

    [Theory]
    [InlineData("Market", "", OrderType.Market)]
    [InlineData("Limit", "", OrderType.Limit)]
    [InlineData("Market", "UNKNOWN", OrderType.Market)]
    [InlineData("Market", "Stop", OrderType.StopMarket)]
    [InlineData("Limit", "Stop", OrderType.StopLimit)]
    public async Task Order_type_and_stop_order_type_together_decide_the_engine_order_type(string orderType, string stopOrderType, OrderType expected)
    {
        await using BybitExecRig rig = new(BybitProductType.Linear);

        Assert.Equal(expected, (await SingleReportAsync(rig, OrderRow(orderType: orderType, stopOrderType: stopOrderType))).OrderType);
    }

    [Theory]
    [InlineData("GTC", TimeInForce.Gtc)]
    [InlineData("IOC", TimeInForce.Ioc)]
    [InlineData("FOK", TimeInForce.Fok)]
    [InlineData("PostOnly", TimeInForce.Gtc)]
    public async Task Every_time_in_force_maps_back(string venueTif, TimeInForce expected)
    {
        await using BybitExecRig rig = new(BybitProductType.Linear);

        Assert.Equal(expected, (await SingleReportAsync(rig, OrderRow(tif: venueTif))).TimeInForce);
    }

    [Fact]
    public async Task A_fill_history_query_leaves_out_the_rows_that_are_not_trades()
    {
        // The same endpoint returns funding and settlement rows, so reconciliation would report trades that never
        // happened - and it is reconciliation that is meant to be the answer when the stream has missed something.
        await using BybitExecRig rig = new(BybitProductType.Linear);
        rig.Routes.On("GET", "/v5/execution/list", BybitPayloads.Envelope("""
            {"list":[{"symbol":"BTCUSDT","orderId":"1321052653536515584","orderLinkId":"","side":"Sell","execId":"f-1","execPrice":"0","execQty":"0.400","execFee":"0.0031","feeCurrency":"","isMaker":false,"execType":"Funding","execTime":"1672282700000"},{"symbol":"BTCUSDT","orderId":"1321052653536515584","orderLinkId":"O-20231114-001","side":"Sell","execId":"e-1","execPrice":"25001.0","execQty":"0.400","execFee":"0.0060","feeCurrency":"","isMaker":true,"execType":"Trade","execTime":"1672282722429"}],"nextPageCursor":""}
            """));

        IReadOnlyList<FillReport> fills = await rig.Client.GenerateFillReportsAsync(null, null, null, null, CancellationToken.None).WaitAsync(Wait.Timeout);

        Assert.Equal(new TradeId("e-1"), Assert.Single(fills).TradeId);
    }

    [Fact]
    public async Task Linear_mass_status_pages_through_open_orders_and_adds_executions_and_positions()
    {
        await using BybitExecRig rig = new(BybitProductType.Linear);
        rig.Routes
            .On("GET", "/v5/order/realtime", r => StubResponse.Json(BybitPayloads.Envelope(r.Query("cursor") is null
                ? "{\"list\":[" + OrderRow() + "],\"nextPageCursor\":\"page-2\"}"
                : "{\"list\":[" + OrderRow(symbol: "UNKNOWNUSDT") + "],\"nextPageCursor\":\"\"}")))
            .On("GET", "/v5/execution/list", BybitPayloads.Envelope("""
                {"list":[{"symbol":"BTCUSDT","orderId":"1321052653536515584","orderLinkId":"O-20231114-001","side":"Sell","execId":"e-1","execPrice":"25001.0","execQty":"0.400","execFee":"0.0060","feeCurrency":"","isMaker":true,"execType":"Trade","execTime":"1672282722429"}],"nextPageCursor":""}
                """))
            .On("GET", "/v5/position/list", BybitPayloads.Envelope("""
                {"list":[{"symbol":"BTCUSDT","side":"Sell","size":"0.400","avgPrice":"25001.0","updatedTime":"1672282722500"},{"symbol":"ETHUSDT","side":"","size":"0","avgPrice":"0","updatedTime":"1672282722500"}],"nextPageCursor":""}
                """));

        ExecutionMassStatus? status = await rig.Client.GenerateMassStatusAsync(UnixNanos.FromMilliseconds(1_672_000_000_000), CancellationToken.None).WaitAsync(Wait.Timeout);

        Assert.NotNull(status);
        Assert.Equal(new ClientOrderId("O-20231114-001"), Assert.Single(status.OrderReports).ClientOrderId);
        IReadOnlyList<RecordedRequest> orderRequests = rig.Server.RequestsTo("/v5/order/realtime");
        Assert.Equal(2, orderRequests.Count);
        Assert.Equal("0", orderRequests[0].Query("openOnly")); // 0 = open orders only in the V5 API
        Assert.Equal("USDT", orderRequests[0].Query("settleCoin"));
        Assert.Equal("page-2", orderRequests[1].Query("cursor"));
        Assert.All(orderRequests, BybitExecRig.AssertSigned);

        FillReport fill = Assert.Single(status.FillReports);
        Assert.Equal(new TradeId("e-1"), fill.TradeId);
        Assert.Equal(new ClientOrderId("O-20231114-001"), fill.ClientOrderId);
        Assert.Equal(OrderSide.Sell, fill.OrderSide);
        Assert.Equal(new Quantity(0.4m, 3), fill.LastQty);
        Assert.Equal(new Price(25_001m, 1), fill.LastPx);
        Assert.Equal(new Money(0.006m, Currencies.USDT), fill.Commission);
        Assert.Equal(LiquiditySide.Maker, fill.LiquiditySide);
        Assert.Equal(1_672_282_722_429_000_000L, fill.TsEvent.Value);
        Assert.Equal("1672000000000", Assert.Single(rig.Server.RequestsTo("/v5/execution/list")).Query("startTime"));

        PositionStatusReport position = Assert.Single(status.PositionReports);
        Assert.Equal(PositionSide.Short, position.PositionSide);
        Assert.Equal(new Quantity(0.4m, 3), position.Quantity);
        Assert.Equal(25_001m, position.AvgPxOpen);
    }

    [Fact]
    public async Task Spot_reports_no_positions_without_calling_the_venue()
    {
        await using BybitExecRig rig = new(BybitProductType.Spot);

        IReadOnlyList<PositionStatusReport> positions = await rig.Client.GeneratePositionStatusReportsAsync(null, null, null, CancellationToken.None).WaitAsync(Wait.Timeout);

        Assert.Empty(positions);
        Assert.Empty(rig.Server.Requests);
        Assert.Equal(AccountType.Cash, rig.Client.AccountType);
    }
}
