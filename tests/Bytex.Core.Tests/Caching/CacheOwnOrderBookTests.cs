using Bytex.Core.Caching;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Tests.Support;

namespace Bytex.Core.Tests.Caching;

// Why: R3.15 - a node's own working orders tracked against the market book. The venue matches on a sequence it never
// shows anybody, so what a node can say about its place in a queue is what it can work out from the book and the
// prints it was given. This is that: what an order finds ahead of it when it joins, and what moves it forwards.
public class CacheOwnOrderBookTests
{
    private static readonly CurrencyPair Btc = TestInstruments.BtcUsdt();

    private static Cache WithBook(decimal bidSize = 5m, decimal askSize = 5m)
    {
        Cache cache = new();
        cache.AddInstrument(Btc);
        OrderBook book = cache.GetOrCreateOrderBook(Btc.Id, BookType.L2);
        book.Apply(Deltas(
            (OrderSide.Buy, 49_000m, bidSize, 1UL),
            (OrderSide.Buy, 48_999m, bidSize, 2UL),
            (OrderSide.Sell, 49_001m, askSize, 3UL),
            (OrderSide.Sell, 49_002m, askSize, 4UL)));
        return cache;
    }

    private static OrderBookDeltas Deltas(params (OrderSide Side, decimal Price, decimal Size, ulong Id)[] rows) =>
        new(
            Btc.Id,
            rows.Select(r => new OrderBookDelta(
                Btc.Id,
                BookAction.Update,
                new BookOrder(r.Side, Btc.MakePrice(r.Price), Btc.MakeQuantity(r.Size), r.Id),
                RecordFlags.None,
                1UL,
                TestOrders.T0,
                TestOrders.T0)).ToList(),
            RecordFlags.None,
            1UL,
            TestOrders.T0,
            TestOrders.T0);

    private static TradeTick Trade(decimal price, decimal size, AggressorSide aggressor) =>
        new(Btc.Id, Btc.MakePrice(price), Btc.MakeQuantity(size), aggressor, new TradeId("T-1"), TestOrders.T0, TestOrders.T0);

    private static LimitOrder Resting(Cache cache, string id, OrderSide side, decimal price, string quantity = "1.000")
    {
        LimitOrder order = TestOrders.Limit(id, Btc.Id, side, quantity, price.ToString(System.Globalization.CultureInfo.InvariantCulture));
        cache.AddOrder(order);
        order.Apply(TestEvents.Submitted(order));
        cache.UpdateOrder(order);
        order.Apply(TestEvents.Accepted(order, "V-" + id));
        cache.UpdateOrder(order);
        return order;
    }

    [Fact]
    public void An_order_resting_at_a_quoted_price_stands_behind_what_is_quoted_there()
    {
        Cache cache = WithBook();

        LimitOrder order = Resting(cache, "O-1", OrderSide.Buy, 49_000m);

        Assert.Equal(5m, cache.OwnOrders.SizeAhead(order.ClientOrderId));
        Assert.Equal(5m, cache.OwnOrders.SizeAheadAtJoin(order.ClientOrderId));
        Assert.Equal(1, cache.OwnOrders.QueuePosition(order.ClientOrderId));
    }

    [Fact]
    public void An_order_the_cache_is_not_tracking_answers_nothing_rather_than_zero()
    {
        // The difference a screen has to be able to show: an order at the front of the queue, and an order that is in
        // no queue at all.
        Cache cache = WithBook();

        MarketOrder market = TestOrders.Market("O-M", Btc.Id, OrderSide.Buy, "1.000");
        cache.AddOrder(market);
        market.Apply(TestEvents.Submitted(market));
        cache.UpdateOrder(market);

        Assert.Null(cache.OwnOrders.SizeAhead(market.ClientOrderId));
        Assert.Null(cache.OwnOrders.QueuePosition(market.ClientOrderId));
    }

    [Fact]
    public void A_price_nobody_is_quoting_puts_the_order_at_the_front()
    {
        Cache cache = WithBook();

        LimitOrder order = Resting(cache, "O-1", OrderSide.Buy, 48_500m);

        Assert.Equal(0m, cache.OwnOrders.SizeAhead(order.ClientOrderId));
    }

    [Fact]
    public void A_print_at_the_price_moves_the_order_forwards()
    {
        Cache cache = WithBook();
        LimitOrder order = Resting(cache, "O-1", OrderSide.Buy, 49_000m);

        cache.AddTradeTick(Trade(49_000m, 3m, AggressorSide.Seller));

        Assert.Equal(2m, cache.OwnOrders.SizeAhead(order.ClientOrderId));
        Assert.Equal(5m, cache.OwnOrders.SizeAheadAtJoin(order.ClientOrderId));
    }

    [Fact]
    public void A_print_taken_from_the_other_side_leaves_this_queue_where_it_was()
    {
        Cache cache = WithBook();
        LimitOrder order = Resting(cache, "O-1", OrderSide.Buy, 49_000m);

        cache.AddTradeTick(Trade(49_000m, 3m, AggressorSide.Buyer));

        Assert.Equal(5m, cache.OwnOrders.SizeAhead(order.ClientOrderId));
    }

    [Fact]
    public void A_print_that_says_nothing_about_its_aggressor_is_taken_to_have_hit_both_sides()
    {
        Cache cache = WithBook();
        LimitOrder buying = Resting(cache, "O-B", OrderSide.Buy, 49_000m);
        LimitOrder selling = Resting(cache, "O-S", OrderSide.Sell, 49_001m);

        cache.AddTradeTick(Trade(49_000m, 3m, AggressorSide.None));
        cache.AddTradeTick(Trade(49_001m, 3m, AggressorSide.None));

        Assert.Equal(2m, cache.OwnOrders.SizeAhead(buying.ClientOrderId));
        Assert.Equal(2m, cache.OwnOrders.SizeAhead(selling.ClientOrderId));
    }

    [Fact]
    public void A_trade_past_the_price_leaves_no_queue_whoever_the_aggressor_was()
    {
        Cache cache = WithBook();
        LimitOrder order = Resting(cache, "O-1", OrderSide.Buy, 49_000m);

        cache.AddTradeTick(Trade(48_900m, 1m, AggressorSide.Buyer));

        Assert.Equal(0m, cache.OwnOrders.SizeAhead(order.ClientOrderId));
    }

    [Fact]
    public void A_level_that_has_grown_smaller_moves_the_order_forwards_and_one_that_has_grown_does_not()
    {
        Cache cache = WithBook();
        LimitOrder order = Resting(cache, "O-1", OrderSide.Buy, 49_000m);

        cache.OwnOrders.Observe(Applied(cache, (OrderSide.Buy, 49_000m, 2m, 1UL)));
        Assert.Equal(2m, cache.OwnOrders.SizeAhead(order.ClientOrderId));

        cache.OwnOrders.Observe(Applied(cache, (OrderSide.Buy, 49_000m, 50m, 1UL)));
        Assert.Equal(2m, cache.OwnOrders.SizeAhead(order.ClientOrderId));
    }

    [Fact]
    public void An_order_that_has_stopped_resting_stands_in_no_queue()
    {
        Cache cache = WithBook();
        LimitOrder order = Resting(cache, "O-1", OrderSide.Buy, 49_000m);

        order.Apply(TestEvents.Canceled(order));
        cache.UpdateOrder(order);

        Assert.Null(cache.OwnOrders.SizeAhead(order.ClientOrderId));
        Assert.Equal(0, cache.OwnOrders.Count);
    }

    [Fact]
    public void A_repriced_order_joins_the_back_of_the_queue_at_its_new_price()
    {
        Cache cache = WithBook();
        LimitOrder order = Resting(cache, "O-1", OrderSide.Buy, 48_500m);
        Assert.Equal(0m, cache.OwnOrders.SizeAhead(order.ClientOrderId));

        order.Apply(TestEvents.Updated(order, "1.000", "49000.00"));
        cache.UpdateOrder(order);

        Assert.Equal(5m, cache.OwnOrders.SizeAhead(order.ClientOrderId));
    }

    [Fact]
    public void A_stop_waiting_for_its_trigger_stands_in_no_queue()
    {
        // It is a promise to the venue, not size in the book, so there is nobody in front of it to count.
        Cache cache = WithBook();
        StopLimitOrder stop = TestOrders.StopLimit("O-SL", Btc.Id, OrderSide.Buy, "1.000", "49500.00", "49400.00");
        cache.AddOrder(stop);
        stop.Apply(TestEvents.Submitted(stop));
        cache.UpdateOrder(stop);
        stop.Apply(TestEvents.Accepted(stop, "V-SL"));
        cache.UpdateOrder(stop);

        Assert.Null(cache.OwnOrders.SizeAhead(stop.ClientOrderId));
    }

    [Fact]
    public void Two_of_our_own_orders_at_one_price_are_ranked_by_who_is_nearer_the_front()
    {
        Cache cache = WithBook();
        LimitOrder first = Resting(cache, "O-1", OrderSide.Buy, 49_000m);
        cache.AddTradeTick(Trade(49_000m, 3m, AggressorSide.Seller));
        LimitOrder second = Resting(cache, "O-2", OrderSide.Buy, 49_000m);

        Assert.Equal(2m, cache.OwnOrders.SizeAhead(first.ClientOrderId));
        Assert.Equal(5m, cache.OwnOrders.SizeAhead(second.ClientOrderId));
        Assert.Equal(1, cache.OwnOrders.QueuePosition(first.ClientOrderId));
        Assert.Equal(2, cache.OwnOrders.QueuePosition(second.ClientOrderId));
    }

    private static OrderBook Applied(Cache cache, params (OrderSide Side, decimal Price, decimal Size, ulong Id)[] rows)
    {
        OrderBook book = cache.GetOrCreateOrderBook(Btc.Id, BookType.L2);
        book.Apply(Deltas(rows));
        return book;
    }
}
