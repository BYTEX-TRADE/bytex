using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Orders;

namespace Bytex.Backtest.Tests;

// Why: R8.9. With a book, a size larger than the touch does not wait for liquidity to come to it - it pays for the
// levels it eats, now. That is the difference between 0.5's bound and this: 0.5 says how much could be had at the
// touch, 0.6 says what the rest of it costs. What is pinned here is the walk: which levels, in which order, at what
// prices, where it stops, and that a venue without a book still fills exactly as it did.
public sealed class BookMatchingTests
{
    [Fact]
    public void A_taker_eats_the_levels_it_can_reach_and_pays_what_each_one_costs()
    {
        // Three levels of one unit at 100.00, 100.01 and 100.02: a three-unit market order pays all three prices,
        // not the touch three times over.
        using SimHarness sim = SimHarness.Spot();
        MarketOrder? order = null;
        sim.BookDepth(1000, bid: 99.99m, ask: 100.00m, size: 1m, levels: 3)
            .At(1500, s => order = s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(3m))))
            .BookDepth(2000, bid: 99.99m, ask: 100.00m, size: 1m, levels: 3)
            .Run();

        Assert.Equal(sim.Qty(3m), order!.FilledQuantity);
        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal([100.00m, 100.01m, 100.02m], sim.Fills(order).Select(f => f.LastPx.Value));
        Assert.Equal([1m, 1m, 1m], sim.Fills(order).Select(f => f.LastQty.Value));
    }

    [Fact]
    public void The_average_it_pays_is_worse_than_the_touch()
    {
        // The number a person feels: the order asked at 100.00 and the position opened above it.
        using SimHarness sim = SimHarness.Spot();
        sim.BookDepth(1000, bid: 99.99m, ask: 100.00m, size: 1m, levels: 5)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(4m))))
            .BookDepth(2000, bid: 99.99m, ask: 100.00m, size: 1m, levels: 5)
            .Run();

        Assert.Equal(100.015m, Assert.Single(sim.Positions).AvgPxOpen);
    }

    [Fact]
    public void A_limit_order_takes_no_further_than_its_own_price()
    {
        // It crossed, so it takes - but a limit is a promise about price, and the levels above it are not its to eat.
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? order = null;
        sim.BookDepth(1000, bid: 99.99m, ask: 100.00m, size: 1m, levels: 5)
            .At(1500, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(5m), sim.Px(100.01m))))
            .BookDepth(2000, bid: 99.99m, ask: 100.00m, size: 1m, levels: 5)
            .Run();

        // Two levels when it arrived, and whatever the next state of the book gave it afterwards - but never a
        // price above the one it promised, which is the whole meaning of a limit.
        Assert.Equal([100.00m, 100.01m], sim.Fills(order!).Take(2).Select(f => f.LastPx.Value));
        Assert.All(sim.Fills(order!), f => Assert.True(f.LastPx.Value <= 100.01m, $"paid {f.LastPx}, above its own limit"));
        Assert.True(order!.FilledQuantity.Value < 5m, "the whole order went in, so nothing bounded it");
    }

    [Fact]
    public void An_order_larger_than_the_book_takes_all_of_it_and_gives_up_the_rest()
    {
        using SimHarness sim = SimHarness.Spot();
        MarketOrder? order = null;
        sim.BookDepth(1000, bid: 99.99m, ask: 100.00m, size: 1m, levels: 3)
            .At(1500, s => order = s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(10m))))
            .BookDepth(2000, bid: 99.99m, ask: 100.00m, size: 1m, levels: 3)
            .Run();

        Assert.Equal(sim.Qty(3m), order!.FilledQuantity);
        Assert.Equal(OrderStatus.Canceled, order.Status);
    }

    [Fact]
    public void A_seller_walks_down_the_bids()
    {
        using SimHarness sim = SimHarness.Spot();
        MarketOrder? order = null;
        sim.BookDepth(1000, bid: 99.99m, ask: 100.00m, size: 1m, levels: 3)
            .At(1500, s => order = s.Submit(s.Orders.Market(sim.Id, OrderSide.Sell, sim.Qty(3m))))
            .BookDepth(2000, bid: 99.99m, ask: 100.00m, size: 1m, levels: 3)
            .Run();

        Assert.Equal([99.99m, 99.98m, 99.97m], sim.Fills(order!).Select(f => f.LastPx.Value));
    }

    [Fact]
    public void One_book_cannot_be_eaten_twice()
    {
        // Two orders against three units of depth: the first takes it, the second finds what is left and no more.
        using SimHarness sim = SimHarness.Spot();
        MarketOrder? first = null;
        MarketOrder? second = null;
        sim.BookDepth(1000, bid: 99.99m, ask: 100.00m, size: 1m, levels: 3)
            .At(1500, s =>
            {
                first = s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(2m)));
                second = s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(2m)));
            })
            .BookDepth(2000, bid: 99.99m, ask: 100.00m, size: 1m, levels: 3)
            .Run();

        Assert.Equal(sim.Qty(2m), first!.FilledQuantity);
        Assert.Equal(sim.Qty(1m), second!.FilledQuantity);
        Assert.Equal([100.02m], sim.Fills(second).Select(f => f.LastPx.Value));
    }

    [Fact]
    public void Fill_or_kill_is_decided_over_the_whole_book_it_can_reach()
    {
        // Four wanted, three on offer: none of it, rather than three of it. Against a touch this was decided on one
        // level, which is not what a venue does with a book.
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? order = null;
        sim.BookDepth(1000, bid: 99.99m, ask: 100.00m, size: 1m, levels: 3)
            .At(1500, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(4m), sim.Px(100.05m), TimeInForce.Fok)))
            .BookDepth(2000, bid: 99.99m, ask: 100.00m, size: 1m, levels: 3)
            .Run();

        Assert.Equal(OrderStatus.Canceled, order!.Status);
        Assert.Empty(sim.Fills(order));
    }

    [Fact]
    public void Fill_or_kill_takes_the_whole_book_when_the_book_can_carry_it()
    {
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? order = null;
        sim.BookDepth(1000, bid: 99.99m, ask: 100.00m, size: 1m, levels: 5)
            .At(1500, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(4m), sim.Px(100.05m), TimeInForce.Fok)))
            .BookDepth(2000, bid: 99.99m, ask: 100.00m, size: 1m, levels: 5)
            .Run();

        Assert.Equal(sim.Qty(4m), order!.FilledQuantity);
        Assert.Equal(OrderStatus.Filled, order.Status);
    }

    [Fact]
    public void A_run_says_whether_it_walked_a_book_or_only_could_have()
    {
        // The difference a product has to be able to show: a result that paid for depth, and one that never met any.
        // Configured is not done, here as everywhere else.
        using SimHarness walked = SimHarness.Spot();
        walked.BookDepth(1000, bid: 99.99m, ask: 100.00m, size: 1m, levels: 3)
            .At(1500, s => s.Submit(s.Orders.Market(walked.Id, OrderSide.Buy, walked.Qty(3m))))
            .BookDepth(2000, bid: 99.99m, ask: 100.00m, size: 1m, levels: 3)
            .Run();

        Assert.Contains(SimulationCapabilities.BookDepth, walked.Engine.GetResult().Applied);

        using SimHarness quoted = SimHarness.Spot();
        quoted.Quote(1000, 99.90m, 100.00m, size: 5m)
            .At(1500, s => s.Submit(s.Orders.Market(quoted.Id, OrderSide.Buy, quoted.Qty(3m))))
            .Quote(2000, 99.90m, 100.00m, size: 5m)
            .Run();

        BacktestResult onQuotes = quoted.Engine.GetResult();
        Assert.Contains(SimulationCapabilities.BookDepth, onQuotes.Simulation);
        Assert.DoesNotContain(SimulationCapabilities.BookDepth, onQuotes.Applied);
    }

    [Fact]
    public void A_fresh_book_offers_its_depth_again()
    {
        // What was eaten is eaten only for as long as that state of the book stands. The next one is a new market,
        // and an order meeting it finds all of it - otherwise a long run would slowly starve itself of liquidity.
        using SimHarness sim = SimHarness.Spot();
        MarketOrder? first = null;
        MarketOrder? second = null;
        sim.BookDepth(1000, bid: 99.99m, ask: 100.00m, size: 1m, levels: 3)
            .At(1500, s => first = s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(3m))))
            .BookDepth(2000, bid: 99.99m, ask: 100.00m, size: 1m, levels: 3)
            .At(2500, s => second = s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(3m))))
            .BookDepth(3000, bid: 99.99m, ask: 100.00m, size: 1m, levels: 3)
            .Run();

        Assert.Equal(sim.Qty(3m), first!.FilledQuantity);
        Assert.Equal(sim.Qty(3m), second!.FilledQuantity);
        Assert.Equal([100.00m, 100.01m, 100.02m], sim.Fills(second).Select(f => f.LastPx.Value));
    }

    [Fact]
    public void A_fill_on_a_bar_is_bounded_by_the_bar_and_never_by_a_book()
    {
        // A bar covers a length of time; a book is one moment. A run holding both must not pay a minute's worth of
        // size out of a book belonging to some instant of it - that liquidity was never there twice.
        using SimHarness sim = SimHarness.Spot();
        MarketOrder? order = null;
        sim.BookDepth(1000, bid: 99.99m, ask: 100.00m, size: 1m, levels: 5)
            .Bar(2000, open: 100.00m, high: 100.00m, low: 100.00m, close: 100.00m, volume: 20m)
            .At(2500, s => order = s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(5m))))
            .Bar(3000, open: 100.00m, high: 100.00m, low: 100.00m, close: 100.00m, volume: 20m)
            .Run();

        // Two units a bar - a tenth of twenty - at the bar's own price, rather than five levels of the book.
        Assert.Equal([100.00m], sim.Fills(order!).Select(f => f.LastPx.Value).Distinct());
        Assert.Equal(2m, sim.Fills(order!)[0].LastQty.Value);
        Assert.DoesNotContain(SimulationCapabilities.BookDepth, sim.Engine.GetResult().Applied);
    }

    [Theory]
    [InlineData("book")]
    [InlineData("quote")]
    [InlineData("print")]
    public void The_bar_rule_lifts_as_soon_as_the_market_speaks_for_itself_again(string then)
    {
        // The other half of the rule. A bar holds the walk off only for as long as the bar is the latest thing the
        // venue heard. The next book, quote or print is a moment again, and depth is back on.
        using SimHarness sim = SimHarness.Spot();
        MarketOrder? order = null;
        sim.BookDepth(1000, bid: 99.99m, ask: 100.00m, size: 1m, levels: 3)
            .Bar(2000, open: 100.00m, high: 100.00m, low: 100.00m, close: 100.00m, volume: 20m);

        switch (then)
        {
            case "book":
                sim.BookDepth(3000, bid: 99.99m, ask: 100.00m, size: 1m, levels: 3);
                break;
            case "quote":
                sim.Quote(3000, 99.99m, 100.00m, size: 1m);
                break;
            default:
                sim.Trade(3000, 100.00m, size: 1m);
                break;
        }

        sim.At(3500, s => order = s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(3m))))
            .BookDepth(4000, bid: 99.99m, ask: 100.00m, size: 1m, levels: 3)
            .Run();

        Assert.Equal([100.00m, 100.01m, 100.02m], sim.Fills(order!).Select(f => f.LastPx.Value));
    }

    [Fact]
    public void A_bar_without_a_share_still_does_not_walk_a_book()
    {
        // The rule is about what the price came from, not about whether a share was configured. With the bound
        // turned off a bar fills whole, at the bar's price, as it did before any of this existed.
        using SimHarness sim = SimHarness.Spot(new SimOptions { BarVolumeShare = null });
        MarketOrder? order = null;
        sim.BookDepth(1000, bid: 99.99m, ask: 100.00m, size: 1m, levels: 5)
            .Bar(2000, open: 100.00m, high: 100.00m, low: 100.00m, close: 100.00m, volume: 20m)
            .At(2500, s => order = s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(5m))))
            .Bar(3000, open: 100.00m, high: 100.00m, low: 100.00m, close: 100.00m, volume: 20m)
            .Run();

        OrderFilled fill = Assert.Single(sim.Fills(order!));
        Assert.Equal(5m, fill.LastQty.Value);
        Assert.Equal(100.00m, fill.LastPx.Value);
    }

    [Fact]
    public void A_venue_with_no_book_fills_at_the_touch_as_it_always_did()
    {
        // Quotes and bars are not a book, and nothing about them changed in 0.6.
        using SimHarness sim = SimHarness.Spot();
        MarketOrder? order = null;
        sim.Quote(1000, 99.90m, 100.00m, size: 5m)
            .At(1500, s => order = s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(3m))))
            .Quote(2000, 99.90m, 100.00m, size: 5m)
            .Run();

        OrderFilled fill = Assert.Single(sim.Fills(order!));
        Assert.Equal(100.00m, fill.LastPx.Value);
        Assert.Equal(3m, fill.LastQty.Value);
    }

    [Fact]
    public void Whole_fills_ignores_the_book_as_it_ignores_everything_else()
    {
        using SimHarness sim = SimHarness.Spot(new SimOptions { FillSizing = FillSizing.WholeFills });
        MarketOrder? order = null;
        sim.BookDepth(1000, bid: 99.99m, ask: 100.00m, size: 1m, levels: 3)
            .At(1500, s => order = s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(10m))))
            .BookDepth(2000, bid: 99.99m, ask: 100.00m, size: 1m, levels: 3)
            .Run();

        OrderFilled fill = Assert.Single(sim.Fills(order!));
        Assert.Equal(10m, fill.LastQty.Value);
        Assert.Equal(100.00m, fill.LastPx.Value);
    }
}
