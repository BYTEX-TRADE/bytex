using Bytex.Backtest;
using Bytex.Core.Model;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Orders;
using Bytex.Backtest.Tests.Support;

namespace Bytex.Backtest.Tests;

// Why: R8.24. Until 0.5 the simulator filled every order whole at one price whatever was on offer there, so a backtest
// traded against a market with infinite size at the touch - entries that could not have filled, at prices nobody would
// have got. A fill is now bounded by what the data says was there: the other side of the quote or the book for an
// order that takes, the print that reached a resting one. What is pinned here is that bound, what becomes of the
// remainder for each kind of order, that one touch cannot be taken twice, and that data saying nothing about size -
// a bar - bounds nothing and leaves those runs as they were.
public sealed class PartialFillTests
{
    [Fact]
    public void A_market_order_takes_what_is_on_offer_and_gives_up_the_rest()
    {
        // Two on the ask, five wanted: a venue fills two and has nothing more to give. A market order never rests, so
        // the remainder is cancelled rather than left looking alive.
        using SimHarness sim = SimHarness.Spot();
        MarketOrder? order = null;
        sim.Quote(1000, 99.90m, 100.00m, size: 2m)
            .At(1500, s => order = s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(5m))))
            .Quote(2000, 99.90m, 100.00m, size: 2m)
            .Run();

        Assert.Equal(sim.Qty(2m), order!.FilledQuantity);
        Assert.Equal(OrderStatus.Canceled, order.Status);
        Assert.Single(sim.Fills(order));
    }

    [Fact]
    public void An_immediate_or_cancel_order_keeps_what_it_got_and_gives_up_the_rest()
    {
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? order = null;
        sim.Quote(1000, 99.90m, 100.00m, size: 3m)
            .At(1500, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(8m), sim.Px(100.00m), TimeInForce.Ioc)))
            .Quote(2000, 99.90m, 100.00m, size: 3m)
            .Run();

        Assert.Equal(sim.Qty(3m), order!.FilledQuantity);
        Assert.Equal(OrderStatus.Canceled, order.Status);
        Assert.Single(sim.Fills(order));
    }

    [Fact]
    public void A_fill_or_kill_order_that_cannot_be_filled_whole_is_not_filled_at_all()
    {
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? order = null;
        sim.Quote(1000, 99.90m, 100.00m, size: 3m)
            .At(1500, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(8m), sim.Px(100.00m), TimeInForce.Fok)))
            .Quote(2000, 99.90m, 100.00m, size: 3m)
            .Run();

        Assert.Equal(OrderStatus.Canceled, order!.Status);
        Assert.Empty(sim.Fills(order));
        Assert.Empty(sim.Positions);
    }

    [Fact]
    public void A_market_to_limit_remainder_rests_as_a_limit_at_the_price_it_got()
    {
        // The type's whole point, and the third thing R8.24 names: what the market could not fill stays in the book at
        // the price the filled part paid, and fills there when the size arrives.
        using SimHarness sim = SimHarness.Spot();
        MarketToLimitOrder? order = null;
        sim.Quote(1000, 99.90m, 100.00m, size: 2m)
            .At(1500, s => order = s.Submit(s.Orders.MarketToLimit(sim.Id, OrderSide.Buy, sim.Qty(5m))))
            .Quote(2000, 99.90m, 100.00m, size: 4m)
            .Run();

        Assert.Equal(sim.Qty(5m), order!.FilledQuantity);
        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal(2, sim.Fills(order).Count);
        Assert.All(sim.Fills(order), f => Assert.Equal(sim.Px(100.00m), f.LastPx));
    }

    [Fact]
    public void A_market_to_limit_remainder_waits_at_its_price_rather_than_paying_more()
    {
        using SimHarness sim = SimHarness.Spot();
        MarketToLimitOrder? order = null;
        sim.Quote(1000, 99.90m, 100.00m, size: 2m)
            .At(1500, s => order = s.Submit(s.Orders.MarketToLimit(sim.Id, OrderSide.Buy, sim.Qty(5m))))
            .Quote(2000, 100.40m, 100.50m, size: 10m)
            .Run();

        // The market ran away from it: the remainder is still in the book at 100.00 and has not paid 100.50 for it.
        Assert.Equal(sim.Qty(2m), order!.FilledQuantity);
        Assert.Equal(OrderStatus.PartiallyFilled, order.Status);
        Assert.Equal(sim.Px(100.00m), order.Price);
    }

    [Fact]
    public void One_touch_cannot_be_taken_twice()
    {
        // Three on the ask and two orders wanting three each: the first takes what is there and the second finds the
        // level empty. A simulator that let both fill would be inventing the liquidity twice over.
        using SimHarness sim = SimHarness.Spot();
        MarketOrder? first = null;
        MarketOrder? second = null;
        sim.Quote(1000, 99.90m, 100.00m, size: 3m)
            .At(1500, s =>
            {
                first = s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(3m)));
                second = s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(3m)));
            })
            .Quote(2000, 99.90m, 100.00m, size: 3m)
            .Run();

        Assert.Equal(sim.Qty(3m), first!.FilledQuantity);
        Assert.Equal(sim.Qty(0m), second!.FilledQuantity);
        Assert.Equal(OrderStatus.Canceled, second.Status);
    }

    [Fact]
    public void A_new_touch_brings_new_size()
    {
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? order = null;
        sim.Quote(1000, 99.90m, 100.00m, size: 2m)
            .At(1500, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(6m), sim.Px(100.00m))))
            .Quote(2000, 99.90m, 100.00m, size: 2m)
            .Quote(3000, 99.90m, 100.00m, size: 2m)
            .Quote(4000, 99.90m, 100.00m, size: 2m)
            .Run();

        Assert.Equal(sim.Qty(6m), order!.FilledQuantity);
        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal(3, sim.Fills(order).Count);
    }

    [Fact]
    public void A_resting_order_takes_no_more_than_the_print_that_reached_it()
    {
        // Somebody sold one at our bid: we bought one, not the five we were bidding for.
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? order = null;
        sim.Quote(1000, 99.90m, 100.00m)
            .At(1500, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(5m), sim.Px(99.50m))))
            .Trade(2000, 99.40m, size: 1m)
            .Run();

        Assert.Equal(sim.Qty(1m), order!.FilledQuantity);
        Assert.Equal(sim.Qty(4m), order.LeavesQuantity);
        Assert.Equal(OrderStatus.PartiallyFilled, order.Status);
    }

    [Fact]
    public void A_bar_offers_the_share_of_its_volume_one_participant_may_take()
    {
        // A bar says what traded across its whole length rather than what was on offer at one price, so what it
        // offers is a share of that: what one participant could plausibly have been. A hundred traded, a tenth of it
        // to be had, and an order for fifty gets ten of them.
        using SimHarness sim = SimHarness.Spot();
        MarketOrder? order = null;
        sim.Bar(1000, 100m, 101m, 99m, 100m, volume: 100m)
            .At(1500, s => order = s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(50m))))
            .Bar(2000, 100m, 101m, 99m, 100m, volume: 100m)
            .Run();

        Assert.Equal(sim.Qty(10m), sim.Fills(order!)[0].LastQty);
        Assert.Equal(0.10m, SimulatedVenueConfig.DefaultBarVolumeShare);
    }

    [Fact]
    public void What_a_bar_could_not_take_takes_its_share_of_the_next_one()
    {
        // The bound is a rate of participation over time, not a book that ran out: the order goes on working and
        // takes its share of every bar until it is filled. Never more than a tenth of any one bar, and fifty units
        // out of bars that trade a hundred takes several of them.
        using SimHarness sim = SimHarness.Spot();
        MarketOrder? order = null;
        SimHarness script = sim.Bar(1000, 100m, 101m, 99m, 100m, volume: 100m)
            .At(1500, s => order = s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(50m))));
        for (int i = 0; i < 8; i++)
        {
            script = script.Bar(2000 + (i * 1000), 100m, 101m, 99m, 100m, volume: 100m);
        }

        script.Run();

        Assert.Equal(sim.Qty(50m), order!.FilledQuantity);
        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.True(sim.Fills(order).Count >= 3, $"the whole order went in {sim.Fills(order).Count} fills");
        Assert.All(sim.Fills(order), f => Assert.True(f.LastQty.Value <= 10m, $"one fill took {f.LastQty}, more than a tenth of the bar"));
    }

    [Fact]
    public void One_bar_is_one_budget_however_many_prices_its_path_walks()
    {
        // The venue walks four prices through a bar - open, an extreme, the other, close - and they are one length of
        // time. An order that took its share at each of them would take four times what the bar had to give. The
        // first bar traded nothing, so what fills here is one bar's share and not a leftover.
        using SimHarness sim = SimHarness.Spot();
        MarketOrder? order = null;
        sim.Bar(1000, 100m, 100m, 100m, 100m, volume: 0m)
            .At(1500, s => order = s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(50m))))
            .Bar(2000, 100m, 104m, 96m, 100m, volume: 100m)
            .Run();

        Assert.Equal(sim.Qty(10m), order!.FilledQuantity);
        Assert.Single(sim.Fills(order));
    }

    [Fact]
    public void A_bar_that_traded_nothing_fills_nothing_and_the_order_waits()
    {
        using SimHarness sim = SimHarness.Spot();
        MarketOrder? order = null;
        sim.Bar(1000, 100m, 101m, 99m, 100m, volume: 100m)
            .At(1500, s => order = s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(50m))))
            .Bar(2000, 100m, 100m, 100m, 100m, volume: 0m)
            .Run();

        // The tenth of the first bar it could have, and nothing from a bar in which nobody traded.
        Assert.Equal(sim.Qty(10m), order!.FilledQuantity);
        Assert.Equal(OrderStatus.PartiallyFilled, order.Status);
    }

    [Fact]
    public void A_venue_told_to_ignore_the_volume_fills_a_bar_whole()
    {
        // For a study that means to ignore liquidity, and for reproducing a run made before 0.5.
        using SimHarness sim = SimHarness.Spot(new SimOptions { BarVolumeShare = null });
        MarketOrder? order = null;
        sim.Bar(1000, 100m, 101m, 99m, 100m, volume: 100m)
            .At(1500, s => order = s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(50m))))
            .Bar(2000, 100m, 101m, 99m, 100m, volume: 100m)
            .Run();

        Assert.Equal(sim.Qty(50m), order!.FilledQuantity);
        Assert.Equal(OrderStatus.Filled, order.Status);
    }

    [Fact]
    public void The_share_is_the_runs_to_set()
    {
        using SimHarness sim = SimHarness.Spot(new SimOptions { BarVolumeShare = 0.5m });
        MarketOrder? order = null;
        sim.Bar(1000, 100m, 101m, 99m, 100m, volume: 100m)
            .At(1500, s => order = s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(50m))))
            .Bar(2000, 100m, 101m, 99m, 100m, volume: 100m)
            .Run();

        Assert.Equal(sim.Qty(50m), order!.FilledQuantity);
        Assert.Single(sim.Fills(order));
    }

    [Fact]
    public void A_bar_brings_its_own_volume_rather_than_the_size_the_last_quote_had()
    {
        // A bar that kept the last quote's size would bound fills by a number belonging to another moment, and would
        // go on doing it for the rest of the run. The bar brings what it traded: a thousand, a tenth to be had, and
        // twenty is inside that.
        using SimHarness sim = SimHarness.Spot();
        MarketOrder? order = null;
        sim.Quote(1000, 99.90m, 100.00m, size: 1m)
            .Bar(2000, 100m, 101m, 99m, 100m, volume: 1_000m)
            .At(2500, s => order = s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(20m))))
            .Bar(3000, 100m, 101m, 99m, 100m, volume: 1_000m)
            .Run();

        Assert.Equal(sim.Qty(20m), order!.FilledQuantity);
        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Single(sim.Fills(order));
    }

    [Fact]
    public void A_quote_after_a_bar_is_the_real_size_and_the_bars_share_is_over()
    {
        // The other way round: what a quote says was on offer is not a model, so it replaces the bar's budget rather
        // than being capped by it.
        using SimHarness sim = SimHarness.Spot();
        MarketOrder? order = null;
        sim.Bar(1000, 100m, 101m, 99m, 100m, volume: 1_000m)
            .Quote(2000, 99.90m, 100.00m, size: 3m)
            .At(2500, s => order = s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(20m))))
            .Quote(3000, 99.90m, 100.00m, size: 3m)
            .Run();

        Assert.Equal(sim.Qty(3m), order!.FilledQuantity);
        Assert.Equal(OrderStatus.Canceled, order.Status);
    }

    [Fact]
    public void Whole_fills_puts_the_simulator_back_as_it_was()
    {
        // For reproducing a run made before 0.5: the size on offer is ignored and the order fills whole at one price.
        using SimHarness sim = SimHarness.Spot(new SimOptions { FillSizing = FillSizing.WholeFills });
        MarketOrder? order = null;
        sim.Quote(1000, 99.90m, 100.00m, size: 2m)
            .At(1500, s => order = s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(5m))))
            .Quote(2000, 99.90m, 100.00m, size: 2m)
            .Run();

        Assert.Equal(sim.Qty(5m), order!.FilledQuantity);
        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Single(sim.Fills(order));
    }

    [Fact]
    public void A_book_offers_what_its_best_level_holds()
    {
        using SimHarness sim = SimHarness.Spot();
        MarketOrder? order = null;
        sim.Book(1000, 99.90m, 100.00m, size: 2m)
            .At(1500, s => order = s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(5m))))
            .Book(2000, 99.90m, 100.00m, size: 2m)
            .Run();

        Assert.Equal(sim.Qty(2m), order!.FilledQuantity);
        Assert.Equal(OrderStatus.Canceled, order.Status);
    }

    [Fact]
    public void A_result_says_what_the_simulator_actually_did_and_what_it_assumed()
    {
        // The list a product can put in front of a user. Configured is not done: a venue set up for partial fills and
        // fed nothing that says what was on offer has bounded nothing, and saying otherwise is the lie the list
        // exists to prevent.
        using SimHarness bounded = SimHarness.Spot();
        bounded.Bar(1000, 100m, 101m, 99m, 100m, volume: 100m)
            .At(1500, s => s.Submit(s.Orders.Market(bounded.Id, OrderSide.Buy, bounded.Qty(50m))))
            .Bar(2000, 100m, 101m, 99m, 100m, volume: 100m)
            .Run();

        BacktestResult result = bounded.Engine.GetResult();
        Assert.Contains(SimulationCapabilities.PartialFills, result.Applied);
        Assert.Equal(0.10m, result.Participation.BarVolumeShare);
        Assert.Equal(2, result.Participation.BoundedFills);
        Assert.Equal(20m, result.Participation.BoundedQuantity);

        using SimHarness untouched = SimHarness.Spot();
        untouched.Bar(1000, 100m, 101m, 99m, 100m, volume: 10_000m)
            .At(1500, s => s.Submit(s.Orders.Market(untouched.Id, OrderSide.Buy, untouched.Qty(1m))))
            .Bar(2000, 100m, 101m, 99m, 100m, volume: 10_000m)
            .Run();

        BacktestResult easy = untouched.Engine.GetResult();
        Assert.Contains(SimulationCapabilities.PartialFills, easy.Simulation);
        Assert.DoesNotContain(SimulationCapabilities.PartialFills, easy.Applied);
        Assert.Equal(0, easy.Participation.BoundedFills);
    }

    [Fact]
    public void What_filled_is_what_the_position_holds()
    {
        // The point of all of it: a position is the size that could actually be bought, not the size that was asked
        // for, and the account paid for exactly that.
        using SimHarness sim = SimHarness.Spot();
        sim.Quote(1000, 99.90m, 100.00m, size: 2m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(5m))))
            .Quote(2000, 99.90m, 100.00m, size: 2m)
            .Run();

        Assert.Equal(sim.Qty(2m), Assert.Single(sim.Positions).Quantity);
    }
}
