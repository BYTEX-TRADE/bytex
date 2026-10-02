using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Orders;

namespace Bytex.Backtest.Tests;

// Why (R8.25): a bar says what the high and the low were and not which came first, so every engine reading one is making
// an assumption the data cannot settle. Until now there was one assumption (the extreme nearer the open came first) and
// one way of dodging it (use the close only).
//
// Two modes state the assumption outright, so a run can be done both ways and the difference read as how much of the
// result rests on it - which is the only honest answer to "did my stop or my target come first". A third walks the range
// in the instrument's own increment instead of jumping between four prices.
public sealed class BarExecutionModeTests
{
    private static SimOptions Mode(BarExecutionMode mode) => new() { BarExecution = mode };

    /// <summary>
    /// Long from 100, a take-profit at 100.80 and a stop-loss at 96.00, linked one-cancels-other, then one bar that
    /// contains both. Which exit fills is decided entirely by the order the extremes are presented in.
    /// </summary>
    private static string ExitInsideOneBar(BarExecutionMode mode, decimal open, decimal high, decimal low, decimal close)
    {
        using SimHarness sim = SimHarness.Spot(Mode(mode));
        OrderList? exits = null;
        sim.Bar(60_000, 100.00m, 100.00m, 100.00m, 100.00m)
            .At(90_000, s =>
            {
                s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m)));
                exits = s.SubmitList(Contingent.SellExitPair(s, sim.Id, sim.Qty(1m), sim.Px(100.80m), sim.Px(96.00m)));
            })
            .Bar(120_000, open, high, low, close)
            .Run();

        Order takeProfit = exits!.Orders[0];
        Order stopLoss = exits.Orders[1];
        return (takeProfit.Status, stopLoss.Status) switch
        {
            (OrderStatus.Filled, OrderStatus.Canceled) => "take-profit",
            (OrderStatus.Canceled, OrderStatus.Filled) => "stop-loss",
            _ => $"take-profit {takeProfit.Status}, stop-loss {stopLoss.Status}",
        };
    }

    // ----- stating the order of the extremes -----

    [Fact]
    public void The_same_bar_gives_a_different_answer_under_each_stated_order()
    {
        // This is the measurement the two modes exist for. One bar, one pair of exits, both inside its range: the result
        // is whichever extreme the venue was told to present first. A run whose result changes here rests on an
        // assumption the data never settled.
        const decimal open = 100.00m;
        const decimal high = 101.00m;
        const decimal low = 95.00m;
        const decimal close = 96.00m;

        Assert.Equal("take-profit", ExitInsideOneBar(BarExecutionMode.HighFirst, open, high, low, close));
        Assert.Equal("stop-loss", ExitInsideOneBar(BarExecutionMode.LowFirst, open, high, low, close));
    }

    [Fact]
    public void A_stated_order_is_kept_even_where_the_open_suggests_the_other()
    {
        // The default would visit the high here, because it is one point from the open against the low's five. Told to
        // show the low first, the venue shows the low first.
        Assert.Equal("stop-loss", ExitInsideOneBar(BarExecutionMode.LowFirst, 100.00m, 101.00m, 95.00m, 100.50m));

        // And the other way round: the low is nearer, and HighFirst overrides that too.
        Assert.Equal("take-profit", ExitInsideOneBar(BarExecutionMode.HighFirst, 100.00m, 105.00m, 95.50m, 99.00m));
    }

    [Fact]
    public void A_bar_that_contains_only_one_of_the_exits_resolves_the_same_way_whatever_the_order()
    {
        // The modes change nothing where there is nothing to disagree about, which is most bars.
        foreach (BarExecutionMode mode in new[] { BarExecutionMode.OhlcPath, BarExecutionMode.HighFirst, BarExecutionMode.LowFirst })
        {
            Assert.Equal("take-profit", ExitInsideOneBar(mode, 100.00m, 101.00m, 99.50m, 100.90m));
            Assert.Equal("stop-loss", ExitInsideOneBar(mode, 100.00m, 100.50m, 95.00m, 95.50m));
        }
    }

    [Fact]
    public void A_run_says_that_it_stated_the_order_rather_than_assuming_it()
    {
        // A reader of a result cannot otherwise tell a run that took the default from one that chose.
        using SimHarness stated = SimHarness.Spot(Mode(BarExecutionMode.LowFirst));
        stated.Bar(60_000, 100.00m, 101.00m, 99.00m, 100.50m).Run();

        using SimHarness assumed = SimHarness.Spot(Mode(BarExecutionMode.OhlcPath));
        assumed.Bar(60_000, 100.00m, 101.00m, 99.00m, 100.50m).Run();

        Assert.Contains(SimulationCapabilities.StatedBarOrder, stated.Engine.GetResult().Applied);
        Assert.DoesNotContain(SimulationCapabilities.StatedBarOrder, assumed.Engine.GetResult().Applied);
    }

    // ----- walking the range -----

    [Fact]
    public void A_walked_bar_reaches_a_price_inside_its_range_that_a_jumped_one_skips()
    {
        // A market-if-touched at 99.50 in a bar that traded from 100.00 down to 98.00. Jumping from the open to the low
        // reaches 98.00 with the order still resting, and it becomes a market order there; walking the range touches
        // 99.50 on the way and the order is triggered where it would really have been.
        //
        // Both fill at 99.50 because a triggered if-touched order fills at its own trigger rather than at the price that
        // came next - what differs is when. So what this asserts is the price the walk visited, through an order that
        // could only have been triggered by a price the jump never presented.
        using SimHarness walked = SimHarness.Spot(Mode(BarExecutionMode.TickSizePath));
        MarketIfTouchedOrder? order = null;
        walked.Bar(60_000, 100.00m, 100.00m, 100.00m, 100.00m)
            .At(90_000, s => order = s.Submit(s.Orders.MarketIfTouched(walked.Id, OrderSide.Buy, walked.Qty(1m), walked.Px(99.50m))))
            .Bar(120_000, 100.00m, 100.20m, 98.00m, 99.00m)
            .Run();

        Assert.Equal(walked.Px(99.50m), walked.SingleFill(order!).LastPx);
    }

    [Fact]
    public void A_walk_visits_every_price_the_instrument_trades_in()
    {
        // Two stops a tick apart inside one bar. A jumped path presents the extremes only, so both are triggered by the
        // same price; a walked one reaches them in turn, and each fills at its own trigger.
        using SimHarness sim = SimHarness.Spot(Mode(BarExecutionMode.TickSizePath));
        StopMarketOrder? near = null;
        StopMarketOrder? far = null;
        sim.Bar(60_000, 100.00m, 100.00m, 100.00m, 100.00m)
            .At(90_000, s =>
            {
                near = s.Submit(s.Orders.StopMarket(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(100.01m)));
                far = s.Submit(s.Orders.StopMarket(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(100.02m)));
            })
            .Bar(120_000, 100.00m, 100.10m, 99.90m, 100.05m)
            .Run();

        Assert.Equal(sim.Px(100.01m), sim.SingleFill(near!).LastPx);
        Assert.Equal(sim.Px(100.02m), sim.SingleFill(far!).LastPx);
    }

    [Fact]
    public void A_run_that_walked_its_bars_says_so()
    {
        using SimHarness sim = SimHarness.Spot(Mode(BarExecutionMode.TickSizePath));
        sim.Bar(60_000, 100.00m, 100.10m, 99.90m, 100.00m).Run();

        Assert.Contains(SimulationCapabilities.TickSizePath, sim.Engine.GetResult().Applied);
        Assert.DoesNotContain(SimulationCapabilities.BarWalkBounded, sim.Engine.GetResult().Applied);
    }

    [Fact]
    public void A_bar_too_wide_to_walk_is_jumped_and_counted()
    {
        // The bound exists because an instrument with a wide range and a fine increment would turn one bar into hundreds
        // of thousands of iterations. What matters is that the run SAYS some of its bars were jumped: a run where some
        // were walked and others were not is a run whose fills mean two different things.
        using SimHarness sim = SimHarness.Spot(new SimOptions { BarExecution = BarExecutionMode.TickSizePath, MaxBarWalkSteps = 10 });
        sim.Bar(60_000, 100.00m, 100.02m, 99.98m, 100.00m)   // eight ticks of movement: walked
            .Bar(120_000, 100.00m, 110.00m, 90.00m, 100.00m) // four thousand: jumped
            .Run();

        Assert.Equal(1, sim.Exchange.BarsNotWalked);
        Assert.Contains(SimulationCapabilities.BarWalkBounded, sim.Engine.GetResult().Applied);
    }

    [Fact]
    public void Nothing_says_a_bar_was_jumped_when_none_was()
    {
        using SimHarness sim = SimHarness.Spot(Mode(BarExecutionMode.TickSizePath));
        sim.Bar(60_000, 100.00m, 100.05m, 99.95m, 100.00m).Run();

        Assert.Equal(0, sim.Exchange.BarsNotWalked);
    }

    [Fact]
    public void The_modes_that_only_jump_never_report_a_walk()
    {
        using SimHarness sim = SimHarness.Spot(Mode(BarExecutionMode.OhlcPath));
        sim.Bar(60_000, 100.00m, 110.00m, 90.00m, 100.00m).Run();

        Assert.Equal(0, sim.Exchange.BarsNotWalked);
        Assert.DoesNotContain(SimulationCapabilities.TickSizePath, sim.Engine.GetResult().Applied);
    }
}
