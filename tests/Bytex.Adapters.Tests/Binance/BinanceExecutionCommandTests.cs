using Bytex.Adapters.Binance;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;

namespace Bytex.Adapters.Tests.Binance;

// Why: this is where an engine order becomes a venue order. Each order type must reach the right endpoint
// with the parameters the Binance API documents (and docs/integrations/binance.md promises), correctly signed,
// and every venue refusal must come back as a rejection event carrying the venue's own message.
public sealed class BinanceExecutionCommandTests
{
    private const string AckJson = """{"symbol":"BTCUSDT","orderId":28,"orderListId":-1,"clientOrderId":"x","transactTime":1507725176595}""";

    private static Order Build(BinanceExecRig rig, string kind)
    {
        InstrumentId id = rig.Instrument.Id;
        return kind switch
        {
            "market-buy" => rig.Orders.Market(id, OrderSide.Buy, rig.Qty(0.5m)),
            "market-sell" => rig.Orders.Market(id, OrderSide.Sell, rig.Qty(0.5m)),
            "market-buy-quote-quantity" => rig.Orders.Market(id, OrderSide.Buy, new Quantity(100m, 2), quoteQuantity: true),
            "market-reduce-only" => rig.Orders.Market(id, OrderSide.Sell, rig.Qty(0.5m), reduceOnly: true),
            "limit-gtc" => rig.Orders.Limit(id, OrderSide.Buy, rig.Qty(0.5m), rig.Px(25_000.1m)),
            "limit-ioc" => rig.Orders.Limit(id, OrderSide.Buy, rig.Qty(0.5m), rig.Px(25_000.1m), TimeInForce.Ioc),
            "limit-fok" => rig.Orders.Limit(id, OrderSide.Sell, rig.Qty(0.5m), rig.Px(25_000.1m), TimeInForce.Fok),
            "limit-gtd" => rig.Orders.Limit(id, OrderSide.Buy, rig.Qty(0.5m), rig.Px(25_000.1m), TimeInForce.Gtd, UnixNanos.FromMilliseconds(1_700_003_600_000)),
            "limit-post-only" => rig.Orders.Limit(id, OrderSide.Buy, rig.Qty(0.5m), rig.Px(25_000.1m), postOnly: true),
            "limit-iceberg" => rig.Orders.Limit(id, OrderSide.Buy, rig.Qty(0.5m), rig.Px(25_000.1m), displayQuantity: rig.Qty(0.1m)),
            "stop-market" => rig.Orders.StopMarket(id, OrderSide.Sell, rig.Qty(0.5m), rig.Px(24_000m)),
            "stop-market-mark" => rig.Orders.StopMarket(id, OrderSide.Sell, rig.Qty(0.5m), rig.Px(24_000m), TriggerType.MarkPrice),
            "stop-limit" => rig.Orders.StopLimit(id, OrderSide.Sell, rig.Qty(0.5m), rig.Px(23_990m), rig.Px(24_000m)),
            "market-if-touched" => rig.Orders.MarketIfTouched(id, OrderSide.Sell, rig.Qty(0.5m), rig.Px(26_000m)),
            "limit-if-touched" => rig.Orders.LimitIfTouched(id, OrderSide.Sell, rig.Qty(0.5m), rig.Px(26_010m), rig.Px(26_000m)),
            "trailing-stop" => rig.Orders.TrailingStopMarket(id, OrderSide.Sell, rig.Qty(0.5m), 150m, TrailingOffsetType.BasisPoints, activationPrice: rig.Px(26_000m)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }

    [Theory]
    [InlineData("market-buy", "symbol=BTCUSDT;side=BUY;type=MARKET;quantity=0.5;newOrderRespType=ACK", "price;timeInForce;stopPrice;quoteOrderQty;reduceOnly;workingType")]
    [InlineData("market-sell", "side=SELL;type=MARKET;quantity=0.5", "price;timeInForce")]
    [InlineData("market-buy-quote-quantity", "side=BUY;type=MARKET;quoteOrderQty=100", "quantity;price")]
    [InlineData("limit-gtc", "side=BUY;type=LIMIT;timeInForce=GTC;quantity=0.5;price=25000.1", "stopPrice;icebergQty")]
    [InlineData("limit-ioc", "type=LIMIT;timeInForce=IOC;price=25000.1", "stopPrice")]
    [InlineData("limit-fok", "side=SELL;type=LIMIT;timeInForce=FOK;price=25000.1", "stopPrice")]
    [InlineData("limit-post-only", "type=LIMIT_MAKER;quantity=0.5;price=25000.1", "timeInForce")]
    [InlineData("limit-iceberg", "type=LIMIT;timeInForce=GTC;icebergQty=0.1", "stopPrice")]
    [InlineData("stop-market", "side=SELL;type=STOP_LOSS;quantity=0.5;stopPrice=24000", "price;timeInForce;workingType")]
    [InlineData("stop-limit", "type=STOP_LOSS_LIMIT;price=23990;stopPrice=24000;timeInForce=GTC", "workingType")]
    [InlineData("market-if-touched", "type=TAKE_PROFIT;stopPrice=26000", "price;timeInForce")]
    [InlineData("limit-if-touched", "type=TAKE_PROFIT_LIMIT;price=26010;stopPrice=26000;timeInForce=GTC", "workingType")]
    public async Task Spot_orders_are_posted_to_api_v3_order_with_the_documented_parameters(string kind, string expected, string absent)
    {
        await using BinanceExecRig rig = new(BinanceAccountType.Spot);
        rig.Routes.On("POST", "/api/v3/order", AckJson);
        Order order = Build(rig, kind);

        await rig.SubmitAsync(order);

        RecordedRequest request = Assert.Single(rig.Server.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/api/v3/order", request.Path);
        Assert.Equal(order.ClientOrderId.Value, request.Query("newClientOrderId"));
        BinanceExecRig.AssertParameters(request, expected, absent);
        BinanceExecRig.AssertSigned(request);
    }

    [Theory]
    [InlineData("market-buy", "symbol=BTCUSDT;side=BUY;type=MARKET;quantity=0.5", "reduceOnly;price;timeInForce;workingType")]
    [InlineData("market-reduce-only", "side=SELL;type=MARKET;quantity=0.5;reduceOnly=true", "price")]
    [InlineData("limit-gtc", "type=LIMIT;timeInForce=GTC;price=25000.1", "stopPrice")]
    [InlineData("limit-post-only", "type=LIMIT;timeInForce=GTX;price=25000.1", "stopPrice")]
    [InlineData("limit-gtd", "type=LIMIT;timeInForce=GTD;goodTillDate=1700003600000", "stopPrice")]
    [InlineData("stop-market", "type=STOP_MARKET;stopPrice=24000;workingType=CONTRACT_PRICE", "price;timeInForce")]
    [InlineData("stop-market-mark", "type=STOP_MARKET;stopPrice=24000;workingType=MARK_PRICE", "price")]
    [InlineData("stop-limit", "type=STOP;price=23990;stopPrice=24000;timeInForce=GTC;workingType=CONTRACT_PRICE", "")]
    [InlineData("market-if-touched", "type=TAKE_PROFIT_MARKET;stopPrice=26000;workingType=CONTRACT_PRICE", "price")]
    [InlineData("limit-if-touched", "type=TAKE_PROFIT;price=26010;stopPrice=26000;timeInForce=GTC", "")]
    [InlineData("trailing-stop", "type=TRAILING_STOP_MARKET;callbackRate=1.5;activationPrice=26000;quantity=0.5", "price;stopPrice")]
    public async Task Futures_orders_are_posted_to_fapi_v1_order_with_the_documented_parameters(string kind, string expected, string absent)
    {
        await using BinanceExecRig rig = new(BinanceAccountType.UsdMFutures);
        rig.Routes.On("POST", "/fapi/v1/order", AckJson);
        Order order = Build(rig, kind);

        await rig.SubmitAsync(order);

        RecordedRequest request = Assert.Single(rig.Server.Requests);
        Assert.Equal("/fapi/v1/order", request.Path);
        Assert.Equal(order.ClientOrderId.Value, request.Query("newClientOrderId"));
        BinanceExecRig.AssertParameters(request, expected, absent);
        BinanceExecRig.AssertSigned(request);
    }

    [Fact]
    public async Task The_configured_default_trigger_type_selects_the_working_type_of_futures_stops()
    {
        await using BinanceExecRig rig = new(BinanceAccountType.UsdMFutures, TriggerType.MarkPrice);
        rig.Routes.On("POST", "/fapi/v1/order", AckJson);

        await rig.SubmitAsync(Build(rig, "stop-market"));

        Assert.Equal("MARK_PRICE", Assert.Single(rig.Server.Requests).Query("workingType"));
    }

    [Fact]
    public async Task A_successful_submission_emits_only_OrderSubmitted_because_acceptance_comes_from_the_user_stream()
    {
        await using BinanceExecRig rig = new(BinanceAccountType.Spot);
        rig.Routes.On("POST", "/api/v3/order", AckJson);
        Order order = Build(rig, "limit-gtc");

        await rig.SubmitAsync(order);

        OrderSubmitted submitted = Assert.IsType<OrderSubmitted>(Assert.Single(rig.Sink.AllOrderEvents));
        Assert.Equal(order.ClientOrderId, submitted.ClientOrderId);
        Assert.Equal(BinanceExecRig.Strategy, submitted.StrategyId);
        Assert.Equal(new AccountId("BINANCE-SPOT"), submitted.AccountId);
    }

    [Theory]
    [InlineData(-2010, "Account has insufficient balance for requested action.")]
    [InlineData(-1013, "Filter failure: NOTIONAL")]
    [InlineData(-2019, "Margin is insufficient.")]
    [InlineData(-1121, "Invalid symbol.")]
    public async Task A_venue_error_becomes_OrderRejected_with_the_venue_message_as_reason(int code, string message)
    {
        await using BinanceExecRig rig = new(BinanceAccountType.Spot);
        rig.Routes.On("POST", "/api/v3/order", _ => StubResponse.Error(400, $"{{\"code\":{code},\"msg\":\"{message}\"}}"));
        Order order = Build(rig, "limit-gtc");

        await rig.SubmitAsync(order);

        Assert.Equal([typeof(OrderSubmitted), typeof(OrderRejected)], rig.Sink.AllOrderEvents.Select(e => e.GetType()));
        OrderRejected rejected = (OrderRejected)rig.Sink.AllOrderEvents[1];
        Assert.Equal(order.ClientOrderId, rejected.ClientOrderId);
        Assert.Equal(message, rejected.Reason);
    }

    [Fact]
    public async Task An_order_type_the_venue_does_not_offer_is_rejected_locally_without_any_request()
    {
        await using BinanceExecRig rig = new(BinanceAccountType.Spot);
        Order order = Build(rig, "trailing-stop");

        await rig.SubmitAsync(order);

        OrderRejected rejected = Assert.IsType<OrderRejected>(Assert.Single(rig.Sink.AllOrderEvents));
        Assert.Contains("TrailingStopMarket", rejected.Reason);
        Assert.Empty(rig.Server.Requests);
    }

    [Fact]
    public async Task An_order_for_an_instrument_the_client_does_not_know_is_rejected_locally()
    {
        await using BinanceExecRig rig = new(BinanceAccountType.Spot);
        Order order = rig.Orders.Market(InstrumentId.Parse("DOGEUSDT.BINANCE"), OrderSide.Buy, new Quantity(10m, 0));

        await rig.SubmitAsync(order);

        OrderRejected rejected = Assert.IsType<OrderRejected>(Assert.Single(rig.Sink.AllOrderEvents));
        Assert.Contains("DOGEUSDT.BINANCE", rejected.Reason);
        Assert.Empty(rig.Server.Requests);
    }

    [Theory]
    [InlineData(BinanceAccountType.Spot, "/api/v3/order")]
    [InlineData(BinanceAccountType.UsdMFutures, "/fapi/v1/order")]
    public async Task Cancel_deletes_the_order_by_raw_symbol_and_original_client_order_id(BinanceAccountType type, string path)
    {
        await using BinanceExecRig rig = new(type);
        rig.Routes.On("DELETE", path, AckJson);
        CancelOrder command = new(rig.Kernel.Services.TraderId, BinanceExecRig.Strategy, rig.Instrument.Id, new ClientOrderId("O-1"), new VenueOrderId("28"), null, Guid.NewGuid(), TestKernel.Now);

        await rig.Client.CancelOrderAsync(command, CancellationToken.None).WaitAsync(Wait.Timeout);

        RecordedRequest request = Assert.Single(rig.Server.Requests);
        Assert.Equal("DELETE", request.Method);
        Assert.Equal(path, request.Path);
        BinanceExecRig.AssertParameters(request, "symbol=BTCUSDT;origClientOrderId=O-1", string.Empty);
        BinanceExecRig.AssertSigned(request);
        OrderPendingCancel pending = Assert.IsType<OrderPendingCancel>(Assert.Single(rig.Sink.AllOrderEvents));
        Assert.Equal(new VenueOrderId("28"), pending.VenueOrderId);
    }

    [Fact]
    public async Task A_refused_cancel_becomes_OrderCancelRejected_with_the_venue_message()
    {
        await using BinanceExecRig rig = new(BinanceAccountType.Spot);
        rig.Routes.On("DELETE", "/api/v3/order", _ => StubResponse.Error(400, "{\"code\":-2011,\"msg\":\"Unknown order sent.\"}"));
        CancelOrder command = new(rig.Kernel.Services.TraderId, BinanceExecRig.Strategy, rig.Instrument.Id, new ClientOrderId("O-1"), null, null, Guid.NewGuid(), TestKernel.Now);

        await rig.Client.CancelOrderAsync(command, CancellationToken.None).WaitAsync(Wait.Timeout);

        Assert.Equal([typeof(OrderPendingCancel), typeof(OrderCancelRejected)], rig.Sink.AllOrderEvents.Select(e => e.GetType()));
        Assert.Equal("Unknown order sent.", ((OrderCancelRejected)rig.Sink.AllOrderEvents[1]).Reason);
    }

    [Theory]
    [InlineData(BinanceAccountType.Spot, "/api/v3/openOrders")]
    [InlineData(BinanceAccountType.UsdMFutures, "/fapi/v1/allOpenOrders")]
    public async Task Cancel_all_uses_the_venue_endpoint_for_the_whole_symbol(BinanceAccountType type, string path)
    {
        await using BinanceExecRig rig = new(type);
        rig.Routes.On("DELETE", path, "[]");
        CancelAllOrders command = new(rig.Kernel.Services.TraderId, BinanceExecRig.Strategy, rig.Instrument.Id, null, null, Guid.NewGuid(), TestKernel.Now);

        await rig.Client.CancelAllOrdersAsync(command, CancellationToken.None).WaitAsync(Wait.Timeout);

        RecordedRequest request = Assert.Single(rig.Server.Requests);
        Assert.Equal("DELETE", request.Method);
        Assert.Equal(path, request.Path);
        Assert.Equal("BTCUSDT", request.Query("symbol"));
        BinanceExecRig.AssertSigned(request);
    }

    [Fact]
    public async Task Spot_modification_is_a_cancel_replace_that_keeps_the_client_order_id()
    {
        await using BinanceExecRig rig = new(BinanceAccountType.Spot);
        rig.Routes.On("POST", "/api/v3/order/cancelReplace", "{\"cancelResult\":\"SUCCESS\",\"newOrderResult\":\"SUCCESS\"}");
        Order order = Build(rig, "limit-gtc");
        rig.Kernel.Kernel.Cache.AddOrder(order);
        ModifyOrder command = new(rig.Kernel.Services.TraderId, BinanceExecRig.Strategy, order.InstrumentId, order.ClientOrderId, new VenueOrderId("28"), null, rig.Px(24_900m), null, null, Guid.NewGuid(), TestKernel.Now);

        await rig.Client.ModifyOrderAsync(command, CancellationToken.None).WaitAsync(Wait.Timeout);

        RecordedRequest request = Assert.Single(rig.Server.Requests);
        Assert.Equal("POST", request.Method);
        BinanceExecRig.AssertParameters(request, $"symbol=BTCUSDT;side=BUY;type=LIMIT;cancelReplaceMode=STOP_ON_FAILURE;cancelOrigClientOrderId={order.ClientOrderId};newClientOrderId={order.ClientOrderId};timeInForce=GTC;quantity=0.5;price=24900", string.Empty);
        BinanceExecRig.AssertSigned(request);
        Assert.Equal([typeof(OrderPendingUpdate), typeof(OrderUpdated)], rig.Sink.AllOrderEvents.Select(e => e.GetType()));
        OrderUpdated updated = (OrderUpdated)rig.Sink.AllOrderEvents[1];
        Assert.Equal(0.5m, updated.Quantity.Value); // unchanged quantity is carried over
        Assert.Equal(24_900m, updated.Price!.Value.Value);
    }

    [Fact]
    public async Task Futures_modification_is_a_put_on_the_order_endpoint()
    {
        await using BinanceExecRig rig = new(BinanceAccountType.UsdMFutures);
        rig.Routes.On("PUT", "/fapi/v1/order", AckJson);
        Order order = Build(rig, "limit-gtc");
        rig.Kernel.Kernel.Cache.AddOrder(order);
        ModifyOrder command = new(rig.Kernel.Services.TraderId, BinanceExecRig.Strategy, order.InstrumentId, order.ClientOrderId, null, rig.Qty(0.75m), rig.Px(24_900m), null, null, Guid.NewGuid(), TestKernel.Now);

        await rig.Client.ModifyOrderAsync(command, CancellationToken.None).WaitAsync(Wait.Timeout);

        RecordedRequest request = Assert.Single(rig.Server.Requests);
        Assert.Equal("PUT", request.Method);
        BinanceExecRig.AssertParameters(request, $"symbol=BTCUSDT;origClientOrderId={order.ClientOrderId};side=BUY;quantity=0.75;price=24900", string.Empty);
        BinanceExecRig.AssertSigned(request);
        Assert.Equal(0.75m, ((OrderUpdated)rig.Sink.AllOrderEvents[1]).Quantity.Value);
    }

    [Fact]
    public async Task Modifying_anything_but_a_limit_order_is_refused_locally()
    {
        await using BinanceExecRig rig = new(BinanceAccountType.Spot);
        Order order = Build(rig, "stop-market");
        rig.Kernel.Kernel.Cache.AddOrder(order);
        ModifyOrder command = new(rig.Kernel.Services.TraderId, BinanceExecRig.Strategy, order.InstrumentId, order.ClientOrderId, null, null, null, rig.Px(23_000m), null, Guid.NewGuid(), TestKernel.Now);

        await rig.Client.ModifyOrderAsync(command, CancellationToken.None).WaitAsync(Wait.Timeout);

        OrderModifyRejected rejected = Assert.IsType<OrderModifyRejected>(Assert.Single(rig.Sink.AllOrderEvents));
        Assert.Contains("limit", rejected.Reason);
        Assert.Empty(rig.Server.Requests);
    }

    [Fact]
    public async Task A_refused_modification_becomes_OrderModifyRejected_with_the_venue_message()
    {
        await using BinanceExecRig rig = new(BinanceAccountType.UsdMFutures);
        rig.Routes.On("PUT", "/fapi/v1/order", _ => StubResponse.Error(400, "{\"code\":-5027,\"msg\":\"No need to modify the order.\"}"));
        Order order = Build(rig, "limit-gtc");
        rig.Kernel.Kernel.Cache.AddOrder(order);
        ModifyOrder command = new(rig.Kernel.Services.TraderId, BinanceExecRig.Strategy, order.InstrumentId, order.ClientOrderId, null, null, rig.Px(25_000.1m), null, null, Guid.NewGuid(), TestKernel.Now);

        await rig.Client.ModifyOrderAsync(command, CancellationToken.None).WaitAsync(Wait.Timeout);

        Assert.Equal([typeof(OrderPendingUpdate), typeof(OrderModifyRejected)], rig.Sink.AllOrderEvents.Select(e => e.GetType()));
        Assert.Equal("No need to modify the order.", ((OrderModifyRejected)rig.Sink.AllOrderEvents[1]).Reason);
    }

    [Theory]
    [InlineData(BinanceAccountType.Spot, "BINANCE-SPOT", AccountType.Cash)]
    [InlineData(BinanceAccountType.UsdMFutures, "BINANCE-USDMFUTURES", AccountType.Margin)]
    public async Task The_account_identity_follows_the_account_type(BinanceAccountType type, string accountId, AccountType accountType)
    {
        await using BinanceExecRig rig = new(type);

        Assert.Equal(new AccountId(accountId), rig.Client.AccountId);
        Assert.Equal(accountType, rig.Client.AccountType);
        Assert.Equal(new Venue("BINANCE"), rig.Client.Venue);
        Assert.Equal(OmsType.Netting, rig.Client.OmsType);
    }
}
