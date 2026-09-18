using Bytex.Core.Model;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Tests.Support;
using Bytex.Core.Timing;
using Bytex.Core.Trading;

namespace Bytex.Core.Tests.Trading;

// Why: R5.3 - orders.md fixes the id format "O-{yyyyMMdd}-{HHmmss}-{trader_tag}-{strategy_tag}-{count}" and
// the bracket layout (OTO entry, OUO exits). Ids must be reproducible from the clock alone.
public class OrderFactoryTests
{
    private static readonly Quantity _one = Quantity.Parse("1.000");

    private static OrderFactory NewFactory(out TestClock clock, bool useHyphens = true)
    {
        clock = new TestClock(UnixNanos.Parse("2024-01-01T00:00:00Z"));
        return new OrderFactory(new TraderId("TESTER-001"), new StrategyId("EMACross-007"), clock, useHyphens: useHyphens);
    }

    [Fact]
    public void Client_order_ids_follow_the_documented_format_and_count_up()
    {
        OrderFactory factory = NewFactory(out _);

        Assert.Equal("O-20240101-000000-001-007-1", factory.GenerateClientOrderId().Value);
        Assert.Equal("O-20240101-000000-001-007-2", factory.GenerateClientOrderId().Value);
        Assert.Equal(2, factory.OrderIdCount);
    }

    [Fact]
    public void Client_order_id_timestamp_comes_from_the_clock()
    {
        OrderFactory factory = NewFactory(out TestClock clock);
        clock.SetTime(UnixNanos.Parse("2025-12-31T23:59:58Z"));

        Assert.Equal("O-20251231-235958-001-007-1", factory.GenerateClientOrderId().Value);
    }

    [Fact]
    public void Hyphens_can_be_removed_for_venues_that_reject_them()
    {
        OrderFactory factory = NewFactory(out _, useHyphens: false);

        Assert.Equal("O202401010000000010071", factory.GenerateClientOrderId().Value);
    }

    [Fact]
    public void Counters_can_be_seeded_and_reset()
    {
        OrderFactory factory = NewFactory(out _);
        factory.SetOrderIdCount(41);
        factory.SetListIdCount(6);

        string seededOrder = factory.GenerateClientOrderId().Value;
        string seededList = factory.GenerateOrderListId().Value;
        factory.Reset();

        Assert.Equal("O-20240101-000000-001-007-42", seededOrder);
        Assert.Equal("OL-20240101-000000-001-007-7", seededList);
        Assert.Equal("O-20240101-000000-001-007-1", factory.GenerateClientOrderId().Value);
        Assert.Equal("OL-20240101-000000-001-007-1", factory.GenerateOrderListId().Value);
    }

    [Fact]
    public void Two_factories_on_identical_clocks_generate_identical_id_sequences()
    {
        static List<string> Run()
        {
            OrderFactory factory = NewFactory(out TestClock clock);
            List<string> ids = new();
            ids.Add(factory.Market(TestIds.BtcUsdt, OrderSide.Buy, _one).ClientOrderId.Value);
            clock.SetTime(clock.Timestamp + TimeSpan.FromSeconds(61));
            ids.AddRange(factory.BracketOrder(TestIds.BtcUsdt, OrderSide.Buy, _one, Price.Parse("45000.00"), Price.Parse("55000.00")).Orders.Select(o => o.ClientOrderId.Value));
            return ids;
        }

        List<string> first = Run();

        Assert.Equal(
            ["O-20240101-000000-001-007-1", "O-20240101-000101-001-007-2", "O-20240101-000101-001-007-3", "O-20240101-000101-001-007-4"],
            first);
        Assert.Equal(first, Run());
    }

    [Fact]
    public void Explicit_client_order_id_is_used_and_does_not_consume_a_generated_one()
    {
        OrderFactory factory = NewFactory(out _);

        MarketOrder order = factory.Market(TestIds.BtcUsdt, OrderSide.Buy, _one, clientOrderId: new ClientOrderId("MY-ID"));

        Assert.Equal("MY-ID", order.ClientOrderId.Value);
        Assert.Equal(0, factory.OrderIdCount);
    }

    [Fact]
    public void Every_order_carries_the_factory_identity_and_the_clock_time()
    {
        OrderFactory factory = NewFactory(out TestClock clock);
        clock.SetTime(UnixNanos.Parse("2024-01-01T00:00:05Z"));

        MarketOrder order = factory.Market(TestIds.BtcUsdt, OrderSide.Sell, _one, tags: ["ENTRY", "v2"]);

        Assert.Equal(new TraderId("TESTER-001"), order.TraderId);
        Assert.Equal(new StrategyId("EMACross-007"), order.StrategyId);
        Assert.Equal(TestIds.BtcUsdt, order.InstrumentId);
        Assert.Equal(UnixNanos.Parse("2024-01-01T00:00:05Z"), order.TsInit);
        Assert.Equal(OrderStatus.Initialized, order.Status);
        Assert.Equal(["ENTRY", "v2"], order.Tags);
        Assert.Equal(ContingencyType.None, order.Contingency);
    }

    [Fact]
    public void Market_order()
    {
        MarketOrder order = NewFactory(out _).Market(TestIds.BtcUsdt, OrderSide.Buy, _one, TimeInForce.Ioc, reduceOnly: true, quoteQuantity: true);

        Assert.Equal((OrderType.Market, OrderSide.Buy, _one, TimeInForce.Ioc), (order.Type, order.Side, order.Quantity, order.TimeInForce));
        Assert.True(order.IsReduceOnly);
        Assert.True(order.IsQuoteQuantity);
        Assert.Null(order.Price);
        Assert.Null(order.TriggerPrice);
    }

    [Fact]
    public void Limit_order()
    {
        LimitOrder order = NewFactory(out _).Limit(TestIds.BtcUsdt, OrderSide.Sell, _one, Price.Parse("50000.00"), postOnly: true, displayQuantity: Quantity.Parse("0.100"));

        Assert.Equal((OrderType.Limit, OrderSide.Sell, TimeInForce.Gtc), (order.Type, order.Side, order.TimeInForce));
        Assert.Equal(Price.Parse("50000.00"), order.Price);
        Assert.True(order.IsPostOnly);
        Assert.True(order.IsIceberg);
        Assert.Equal(Quantity.Parse("0.100"), order.DisplayQuantity);
    }

    [Fact]
    public void Gtd_limit_order_keeps_its_expiry_and_requires_one()
    {
        OrderFactory factory = NewFactory(out _);
        UnixNanos expiry = UnixNanos.Parse("2024-01-01T01:00:00Z");

        LimitOrder order = factory.Limit(TestIds.BtcUsdt, OrderSide.Buy, _one, Price.Parse("50000.00"), TimeInForce.Gtd, expiry);

        Assert.Equal(expiry, order.ExpireTime);
        Assert.Throws<ArgumentException>(() => factory.Limit(TestIds.BtcUsdt, OrderSide.Buy, _one, Price.Parse("50000.00"), TimeInForce.Gtd));
    }

    [Fact]
    public void Market_order_refuses_gtd()
    {
        Assert.Throws<ArgumentException>(() => NewFactory(out _).Market(TestIds.BtcUsdt, OrderSide.Buy, _one, TimeInForce.Gtd));
    }

    [Fact]
    public void Stop_market_order()
    {
        StopMarketOrder order = NewFactory(out _).StopMarket(TestIds.BtcUsdt, OrderSide.Sell, _one, Price.Parse("45000.00"), TriggerType.MarkPrice, reduceOnly: true);

        Assert.Equal(OrderType.StopMarket, order.Type);
        Assert.Equal(Price.Parse("45000.00"), order.TriggerPrice);
        Assert.Equal(TriggerType.MarkPrice, order.TriggerType);
        Assert.Null(order.Price);
        Assert.True(order.IsReduceOnly);
    }

    [Fact]
    public void Stop_limit_order()
    {
        StopLimitOrder order = NewFactory(out _).StopLimit(TestIds.BtcUsdt, OrderSide.Buy, _one, Price.Parse("51000.00"), Price.Parse("50900.00"), TriggerType.LastPrice);

        Assert.Equal(OrderType.StopLimit, order.Type);
        Assert.Equal((Price.Parse("51000.00"), Price.Parse("50900.00")), (order.Price!.Value, order.TriggerPrice!.Value));
        Assert.Equal(TriggerType.LastPrice, order.TriggerType);
    }

    [Fact]
    public void Market_if_touched_order()
    {
        MarketIfTouchedOrder order = NewFactory(out _).MarketIfTouched(TestIds.BtcUsdt, OrderSide.Buy, _one, Price.Parse("48000.00"));

        Assert.Equal(OrderType.MarketIfTouched, order.Type);
        Assert.Equal(Price.Parse("48000.00"), order.TriggerPrice);
        Assert.Null(order.Price);
    }

    [Fact]
    public void Limit_if_touched_order()
    {
        LimitIfTouchedOrder order = NewFactory(out _).LimitIfTouched(TestIds.BtcUsdt, OrderSide.Buy, _one, Price.Parse("47900.00"), Price.Parse("48000.00"));

        Assert.Equal(OrderType.LimitIfTouched, order.Type);
        Assert.Equal((Price.Parse("47900.00"), Price.Parse("48000.00")), (order.Price!.Value, order.TriggerPrice!.Value));
    }

    [Fact]
    public void Trailing_stop_market_order()
    {
        TrailingStopMarketOrder order = NewFactory(out _).TrailingStopMarket(TestIds.BtcUsdt, OrderSide.Sell, _one, 150m, TrailingOffsetType.BasisPoints, activationPrice: Price.Parse("52000.00"));

        Assert.Equal(OrderType.TrailingStopMarket, order.Type);
        Assert.Equal(150m, order.TrailingOffset);
        Assert.Equal(TrailingOffsetType.BasisPoints, order.TrailingOffsetType);
        Assert.Equal(Price.Parse("52000.00"), order.ActivationPrice);
        Assert.Null(order.TriggerPrice);
    }

    [Fact]
    public void Trailing_stop_limit_order()
    {
        TrailingStopLimitOrder order = NewFactory(out _).TrailingStopLimit(TestIds.BtcUsdt, OrderSide.Sell, _one, 100.5m, 20.25m, TrailingOffsetType.Price, triggerPrice: Price.Parse("49000.00"));

        Assert.Equal(OrderType.TrailingStopLimit, order.Type);
        Assert.Equal((100.5m, 20.25m), (order.TrailingOffset, order.LimitOffset));
        Assert.Equal(Price.Parse("49000.00"), order.TriggerPrice);
        Assert.Null(order.Price);
    }

    [Fact]
    public void Market_to_limit_order()
    {
        MarketToLimitOrder order = NewFactory(out _).MarketToLimit(TestIds.BtcUsdt, OrderSide.Buy, _one, displayQuantity: Quantity.Parse("0.250"));

        Assert.Equal(OrderType.MarketToLimit, order.Type);
        Assert.Equal(Quantity.Parse("0.250"), order.DisplayQuantity);
        Assert.Null(order.Price);
    }

    [Fact]
    public void Bracket_links_an_oto_entry_to_two_ouo_exits_on_the_opposite_side()
    {
        OrderFactory factory = NewFactory(out _);

        OrderList list = factory.BracketOrder(TestIds.BtcUsdt, OrderSide.Buy, _one, Price.Parse("45000.00"), Price.Parse("55000.00"),
            entryTags: ["E"], stopLossTags: ["SL"], takeProfitTags: ["TP"]);

        Assert.Equal("OL-20240101-000000-001-007-1", list.Id.Value);
        Assert.Equal(3, list.Orders.Count);
        Order entry = list.Orders[0];
        Order stopLoss = list.Orders[1];
        Order takeProfit = list.Orders[2];
        Assert.Same(entry, list.First);
        Assert.All(list.Orders, o => Assert.Equal(list.Id, o.OrderListId));
        Assert.All(list.Orders, o => Assert.Equal(_one, o.Quantity));

        Assert.Equal((OrderType.Market, OrderSide.Buy, ContingencyType.Oto), (entry.Type, entry.Side, entry.Contingency));
        Assert.Equal([stopLoss.ClientOrderId, takeProfit.ClientOrderId], entry.LinkedOrderIds);
        Assert.Null(entry.ParentOrderId);
        Assert.False(entry.IsReduceOnly);
        Assert.Equal(["E"], entry.Tags);

        Assert.Equal((OrderType.StopMarket, OrderSide.Sell, ContingencyType.Ouo), (stopLoss.Type, stopLoss.Side, stopLoss.Contingency));
        Assert.Equal(Price.Parse("45000.00"), stopLoss.TriggerPrice);
        Assert.Equal([takeProfit.ClientOrderId], stopLoss.LinkedOrderIds);
        Assert.Equal(entry.ClientOrderId, stopLoss.ParentOrderId);
        Assert.True(stopLoss.IsReduceOnly);
        Assert.Equal(["SL"], stopLoss.Tags);

        Assert.Equal((OrderType.Limit, OrderSide.Sell, ContingencyType.Ouo), (takeProfit.Type, takeProfit.Side, takeProfit.Contingency));
        Assert.Equal(Price.Parse("55000.00"), takeProfit.Price);
        Assert.Equal([stopLoss.ClientOrderId], takeProfit.LinkedOrderIds);
        Assert.Equal(entry.ClientOrderId, takeProfit.ParentOrderId);
        Assert.True(takeProfit.IsReduceOnly);
        Assert.True(takeProfit.IsPostOnly);
        Assert.Equal(["TP"], takeProfit.Tags);
    }

    [Fact]
    public void Bracket_for_a_short_entry_exits_with_buys()
    {
        OrderList list = NewFactory(out _).BracketOrder(TestIds.BtcUsdt, OrderSide.Sell, _one, Price.Parse("55000.00"), Price.Parse("45000.00"));

        Assert.Equal([OrderSide.Sell, OrderSide.Buy, OrderSide.Buy], list.Orders.Select(o => o.Side));
    }

    [Fact]
    public void Bracket_with_a_limit_entry_needs_an_entry_price()
    {
        OrderFactory factory = NewFactory(out _);

        OrderList list = factory.BracketOrder(TestIds.BtcUsdt, OrderSide.Buy, _one, Price.Parse("45000.00"), Price.Parse("55000.00"), entryPrice: Price.Parse("50000.00"), entryType: OrderType.Limit, entryPostOnly: true);

        Assert.Equal(Price.Parse("50000.00"), Assert.IsType<LimitOrder>(list.First).Price);
        Assert.True(list.First.IsPostOnly);
        Assert.Throws<ArgumentException>(() => factory.BracketOrder(TestIds.BtcUsdt, OrderSide.Buy, _one, Price.Parse("45000.00"), Price.Parse("55000.00"), entryType: OrderType.Limit));
    }

    [Theory]
    [InlineData(OrderType.TrailingStopMarket, OrderType.StopMarket, OrderType.Limit)]
    [InlineData(OrderType.Market, OrderType.Limit, OrderType.Limit)]
    [InlineData(OrderType.Market, OrderType.StopMarket, OrderType.Market)]
    public void Bracket_refuses_unsupported_leg_types(OrderType entry, OrderType stopLoss, OrderType takeProfit)
    {
        OrderFactory factory = NewFactory(out _);

        Assert.Throws<ArgumentException>(() => factory.BracketOrder(TestIds.BtcUsdt, OrderSide.Buy, _one, Price.Parse("45000.00"), Price.Parse("55000.00"),
            entryType: entry, stopLossType: stopLoss, takeProfitType: takeProfit));
    }

    [Fact]
    public void Create_list_groups_existing_orders_under_a_generated_list_id()
    {
        OrderFactory factory = NewFactory(out _);
        LimitOrder a = factory.Limit(TestIds.BtcUsdt, OrderSide.Buy, _one, Price.Parse("49000.00"));
        LimitOrder b = factory.Limit(TestIds.BtcUsdt, OrderSide.Buy, _one, Price.Parse("48000.00"));

        OrderList list = factory.CreateList([a, b]);

        Assert.Equal("OL-20240101-000000-001-007-1", list.Id.Value);
        Assert.Equal(new Order[] { a, b }, list.Orders);
        Assert.Equal(1, factory.ListIdCount);
        Assert.Throws<ArgumentException>(() => factory.CreateList([]));
    }
}
