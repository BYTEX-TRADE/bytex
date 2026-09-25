using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Orders;

namespace Bytex.Backtest.Tests;

// Why: R3.15. A limit order at the front of the queue and the same order at the back of it are one order to a
// backtest that fills on the first touch, and two different trades to whoever placed them. What is pinned here is the
// queue: what an order finds ahead of it when it joins, what moves it forwards, and that a print at its price belongs
// to the people who were there first until they have been served.
public sealed class QueuePositionTests
{
    [Fact]
    public void An_order_joining_a_price_finds_what_is_already_resting_there_ahead_of_it()
    {
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? order = null;
        sim.BookDepth(1000, bid: 99.99m, ask: 100.00m, size: 5m, levels: 3)
            .At(1500, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(2m), sim.Px(99.99m))))
            .BookDepth(2000, bid: 99.99m, ask: 100.00m, size: 5m, levels: 3)
            .Run();

        Assert.Equal(5m, sim.Exchange.SizeAhead(order!));
        Assert.Equal(1, sim.Exchange.QueuePosition(order!));
    }

    [Fact]
    public void A_price_nobody_is_quoting_has_nothing_ahead_of_it()
    {
        // Inside the spread: the order is the only thing at that price, so it is the front of its own queue. Zero
        // ahead and no queue at all have to read differently - one is an order at the front, the other is an order
        // the venue is not tracking.
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? order = null;
        sim.BookDepth(1000, bid: 99.90m, ask: 100.00m, size: 5m, levels: 3)
            .At(1500, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(2m), sim.Px(99.95m))))
            .BookDepth(2000, bid: 99.90m, ask: 100.00m, size: 5m, levels: 3)
            .Run();

        Assert.Equal(0m, sim.Exchange.SizeAhead(order!));
    }

    [Fact]
    public void A_print_that_only_serves_the_queue_ahead_leaves_the_order_unfilled()
    {
        // Five ahead of it, three traded: all three belonged to somebody else.
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? order = null;
        sim.BookDepth(1000, bid: 99.99m, ask: 100.00m, size: 5m, levels: 3)
            .At(1500, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(2m), sim.Px(99.99m))))
            .Trade(2000, 99.99m, size: 3m, aggressor: AggressorSide.Seller)
            .Run();

        Assert.Empty(sim.Fills(order!));
        Assert.Equal(2m, sim.Exchange.SizeAhead(order!));
    }

    [Fact]
    public void What_is_left_of_a_print_once_the_queue_is_served_reaches_the_order()
    {
        // Five ahead, seven traded: the first five were theirs, the last two are ours.
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? order = null;
        sim.BookDepth(1000, bid: 99.99m, ask: 100.00m, size: 5m, levels: 3)
            .At(1500, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(4m), sim.Px(99.99m))))
            .Trade(2000, 99.99m, size: 7m, aggressor: AggressorSide.Seller)
            .Run();

        Assert.Equal(2m, Assert.Single(sim.Fills(order!)).LastQty.Value);
        Assert.Equal(0m, sim.Exchange.SizeAhead(order!));
    }

    [Fact]
    public void The_queue_moves_forwards_across_several_prints()
    {
        // Three, then three: the first print is all theirs, the second serves the last of them and reaches us.
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? order = null;
        sim.BookDepth(1000, bid: 99.99m, ask: 100.00m, size: 5m, levels: 3)
            .At(1500, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(4m), sim.Px(99.99m))))
            .Trade(2000, 99.99m, size: 3m, aggressor: AggressorSide.Seller)
            .Trade(3000, 99.99m, size: 3m, aggressor: AggressorSide.Seller)
            .Run();

        Assert.Equal(1m, Assert.Single(sim.Fills(order!)).LastQty.Value);
    }

    [Fact]
    public void A_trade_through_the_price_leaves_no_queue_at_all()
    {
        // Somebody sold below our bid while we were resting above it. Everything at our price went with the market:
        // a queue cannot survive the market trading past it.
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? order = null;
        sim.BookDepth(1000, bid: 99.99m, ask: 100.00m, size: 5m, levels: 3)
            .At(1500, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(2m), sim.Px(99.99m))))
            .Trade(2000, 99.90m, size: 1m, aggressor: AggressorSide.Seller)
            .Run();

        // One unit printed, so one unit is what the order got - the queue is gone, the print is still the bound.
        Assert.Equal(0m, sim.Exchange.SizeAhead(order!));
        Assert.Equal(sim.Qty(1m), order!.FilledQuantity);
    }

    [Fact]
    public void A_level_that_has_grown_smaller_moves_the_order_forwards()
    {
        // Nothing printed, and yet the level halved: whoever was ahead of us cancelled. A book cannot say which of
        // the two it was, and for our place in the queue it does not matter.
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? order = null;
        sim.BookDepth(1000, bid: 99.99m, ask: 100.00m, size: 6m, levels: 3)
            .At(1500, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(2m), sim.Px(99.99m))))
            .BookDepth(2000, bid: 99.99m, ask: 100.00m, size: 2m, levels: 3)
            .Run();

        Assert.Equal(2m, sim.Exchange.SizeAhead(order!));
    }

    [Fact]
    public void A_level_that_has_grown_leaves_the_queue_where_it_was()
    {
        // Size arriving at a price arrives behind what is already resting there, ours included.
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? order = null;
        sim.BookDepth(1000, bid: 99.99m, ask: 100.00m, size: 4m, levels: 3)
            .At(1500, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(2m), sim.Px(99.99m))))
            .BookDepth(2000, bid: 99.99m, ask: 100.00m, size: 40m, levels: 3)
            .Run();

        Assert.Equal(4m, sim.Exchange.SizeAhead(order!));
    }

    [Fact]
    public void A_repriced_order_joins_the_back_of_the_queue_at_its_new_price()
    {
        // The cost of moving a limit order, which is the thing a queue exists to show: it gives up its place.
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? order = null;
        sim.BookDepth(1000, bid: 99.99m, ask: 100.00m, size: 5m, levels: 3)
            .At(1500, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(2m), sim.Px(99.95m))))
            .BookDepth(2000, bid: 99.99m, ask: 100.00m, size: 5m, levels: 3)
            .At(2500, s => s.Modify(order!, price: sim.Px(99.99m)))
            .BookDepth(3000, bid: 99.99m, ask: 100.00m, size: 5m, levels: 3)
            .Run();

        Assert.Equal(5m, sim.Exchange.SizeAhead(order!));
    }

    [Fact]
    public void Two_of_our_own_orders_at_one_price_are_ranked_by_who_arrived_first()
    {
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? first = null;
        LimitOrder? second = null;
        sim.BookDepth(1000, bid: 99.99m, ask: 100.00m, size: 5m, levels: 3)
            .At(1500, s => first = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(2m), sim.Px(99.99m))))
            .BookDepth(2000, bid: 99.99m, ask: 100.00m, size: 5m, levels: 3)
            .At(2500, s => second = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(2m), sim.Px(99.99m))))
            .BookDepth(3000, bid: 99.99m, ask: 100.00m, size: 5m, levels: 3)
            .Run();

        Assert.Equal(1, sim.Exchange.QueuePosition(first!));
        Assert.Equal(2, sim.Exchange.QueuePosition(second!));
    }

    [Fact]
    public void An_order_that_has_stopped_resting_stands_in_no_queue()
    {
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? order = null;
        sim.BookDepth(1000, bid: 99.99m, ask: 100.00m, size: 5m, levels: 3)
            .At(1500, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(2m), sim.Px(99.99m))))
            .BookDepth(2000, bid: 99.99m, ask: 100.00m, size: 5m, levels: 3)
            .At(2500, s => s.Cancel(order!))
            .BookDepth(3000, bid: 99.99m, ask: 100.00m, size: 5m, levels: 3)
            .Run();

        Assert.Null(sim.Exchange.SizeAhead(order!));
        Assert.Null(sim.Exchange.QueuePosition(order!));
    }

    [Fact]
    public void A_stop_waiting_for_its_trigger_is_not_in_any_queue_and_joins_one_when_it_triggers()
    {
        // A stop is a promise to the venue, not size in the book, so there is nobody in front of it to count.
        using SimHarness sim = SimHarness.Spot();
        StopLimitOrder? order = null;
        sim.BookDepth(1000, bid: 99.99m, ask: 100.00m, size: 5m, levels: 3)
            .At(1500, s => order = s.Submit(s.Orders.StopLimit(sim.Id, OrderSide.Buy, sim.Qty(2m), sim.Px(100.50m), sim.Px(100.40m))))
            .BookDepth(2000, bid: 99.99m, ask: 100.00m, size: 5m, levels: 3)
            .Run();

        Assert.Null(sim.Exchange.SizeAhead(order!));

        // The same order once the market has fallen to its trigger: it is a limit order at 99.48 now, resting among
        // the offers, and what is resting there is ahead of it like anybody else's.
        using SimHarness triggered = SimHarness.Spot();
        StopLimitOrder? stop = null;
        triggered.BookDepth(1000, bid: 99.99m, ask: 100.00m, size: 5m, levels: 3)
            .At(1500, s => stop = s.Submit(s.Orders.StopLimit(triggered.Id, OrderSide.Sell, triggered.Qty(2m), triggered.Px(99.48m), triggered.Px(99.50m))))
            .BookSnapshot(2000, bid: 99.45m, ask: 99.46m, size: 5m, levels: 3)
            .BookSnapshot(3000, bid: 99.45m, ask: 99.46m, size: 5m, levels: 3)
            .Run();

        Assert.Equal(5m, triggered.Exchange.SizeAhead(stop!));
    }

    [Fact]
    public void A_buyer_lifting_the_ask_serves_the_ask_queue_and_not_the_bid_queue()
    {
        // Which queue a trade served is which side was resting, and the aggressor says which that was. Without this
        // a trade would move both sides forwards, which is one trade counted twice.
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? selling = null;
        LimitOrder? buying = null;
        sim.BookDepth(1000, bid: 99.99m, ask: 100.00m, size: 5m, levels: 3)
            .At(1500, s =>
            {
                selling = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Sell, sim.Qty(2m), sim.Px(100.00m)));
                buying = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(2m), sim.Px(99.99m)));
            })
            .Trade(2000, 100.00m, size: 3m, aggressor: AggressorSide.Buyer)
            .Run();

        Assert.Equal(2m, sim.Exchange.SizeAhead(selling!));
        Assert.Equal(5m, sim.Exchange.SizeAhead(buying!));
    }

    [Fact]
    public void A_print_taken_from_the_other_side_of_the_market_does_not_serve_this_queue()
    {
        // The trade stream and the book stream race each other: an offer moved down to our bid price and somebody
        // lifted it before the new book arrived. The print is at our price and has nothing to do with our queue -
        // it was taken from the offers, and the aggressor is what says so. Without reading it, one trade would move
        // both sides of the book forwards, which is the same size counted twice.
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? order = null;
        sim.BookDepth(1000, bid: 99.99m, ask: 100.00m, size: 5m, levels: 3)
            .At(1500, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(2m), sim.Px(99.99m))))
            .Trade(2000, 99.99m, size: 3m, aggressor: AggressorSide.Buyer)
            .Run();

        Assert.Equal(5m, sim.Exchange.SizeAhead(order!));
        Assert.Empty(sim.Fills(order!));
    }

    [Fact]
    public void A_venue_with_no_book_has_no_queue_and_fills_as_it_always_did()
    {
        // Quotes and trades alone say nothing about who was resting where, so an order is filled by the print that
        // reached it exactly as it was before any of this.
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? order = null;
        sim.Quote(1000, 99.99m, 100.00m, size: 5m)
            .At(1500, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(5m), sim.Px(99.99m))))
            .Trade(2000, 99.99m, size: 3m, aggressor: AggressorSide.Seller)
            .Run();

        // Still resting, so still tracked - and nothing is ahead of it, because nothing told the venue there was.
        Assert.Equal(0m, sim.Exchange.SizeAhead(order!));
        Assert.Equal(sim.Qty(3m), order!.FilledQuantity);
    }
}
