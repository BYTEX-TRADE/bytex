using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Orders;

namespace Bytex.Backtest.Tests;

// Why: most users backtest on bars, and a bar hides the order in which its high and low were reached. The
// documented replay is open -> nearer extreme -> farther extreme -> close; CloseOnly uses closes alone. Which
// extreme comes first decides whether a take-profit or a stop-loss is hit inside the same bar.
public sealed class BarExecutionTests
{
    [Fact]
    public void Market_order_sent_from_the_bar_handler_fills_at_that_bars_close()
    {
        using SimHarness sim = SimHarness.Spot();
        MarketOrder? order = null;
        sim.Strategy.BarHandler = (s, _) => order ??= s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m)));
        sim.Bar(60_000, 100.00m, 101.00m, 99.00m, 100.50m)
            .Bar(120_000, 100.50m, 103.00m, 100.00m, 102.00m)
            .Run();

        OrderFilled fill = sim.SingleFill(order!);
        Assert.Equal(sim.Px(100.50m), fill.LastPx);
        Assert.Equal(Scripted.Ms(60_000), fill.TsEvent);
    }

    [Theory]
    [InlineData(99.00, 99.50)] // low trades through the limit
    [InlineData(99.50, 99.50)] // low only touches it; the default fill model fills on touch
    public void Resting_buy_limit_fills_at_its_own_price_when_the_bar_low_reaches_it(decimal low, decimal limit)
    {
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? order = null;
        sim.Bar(60_000, 100.00m, 100.00m, 100.00m, 100.00m)
            .At(90_000, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(limit))))
            .Bar(120_000, 100.00m, 101.00m, low, 100.50m)
            .Run();

        OrderFilled fill = sim.SingleFill(order!);
        Assert.Equal(sim.Px(limit), fill.LastPx);
        Assert.Equal(LiquiditySide.Maker, fill.LiquiditySide);
        Assert.Equal(Scripted.Ms(120_000), fill.TsEvent);
    }

    [Fact]
    public void Resting_limit_is_untouched_by_a_bar_whose_range_stays_away_from_it()
    {
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? order = null;
        sim.Bar(60_000, 100.00m, 100.00m, 100.00m, 100.00m)
            .At(90_000, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(99.50m))))
            .Bar(120_000, 100.00m, 101.00m, 99.51m, 100.50m)
            .Run();

        Assert.Equal(OrderStatus.Accepted, order!.Status);
    }

    [Fact]
    public void Close_only_mode_ignores_the_bar_range_so_an_intrabar_dip_does_not_fill_a_limit()
    {
        using SimHarness sim = SimHarness.Spot(new SimOptions { BarExecution = BarExecutionMode.CloseOnly });
        LimitOrder? order = null;
        sim.Bar(60_000, 100.00m, 100.00m, 100.00m, 100.00m)
            .At(90_000, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(99.50m))))
            .Bar(120_000, 100.00m, 101.00m, 99.00m, 100.50m) // low is through the limit, close is not
            .Run();

        Assert.Equal(OrderStatus.Accepted, order!.Status);
        Assert.Empty(sim.Fills(order));
    }

    [Fact]
    public void Close_only_mode_fills_a_limit_when_the_close_itself_is_through_it()
    {
        using SimHarness sim = SimHarness.Spot(new SimOptions { BarExecution = BarExecutionMode.CloseOnly });
        LimitOrder? order = null;
        sim.Bar(60_000, 100.00m, 100.00m, 100.00m, 100.00m)
            .At(90_000, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(99.50m))))
            .Bar(120_000, 100.00m, 101.00m, 99.00m, 99.20m)
            .Run();

        Assert.Equal(sim.Px(99.50m), sim.SingleFill(order!).LastPx);
    }

    [Fact]
    public void Stop_order_fills_at_the_open_when_the_bar_gaps_open_beyond_its_trigger()
    {
        using SimHarness sim = SimHarness.Spot();
        StopMarketOrder? order = null;
        sim.Bar(60_000, 100.00m, 100.00m, 100.00m, 100.00m)
            .At(90_000, s => order = s.Submit(s.Orders.StopMarket(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(101.00m))))
            .Bar(120_000, 102.00m, 103.00m, 101.50m, 102.50m)
            .Run();

        Assert.Equal(sim.Px(102.00m), sim.SingleFill(order!).LastPx);
    }

    [Theory]
    // Long from 100.00 with a take-profit at 100.80 and a stop-loss at 96.00, linked one-cancels-other.
    [InlineData(100.00, 101.00, 99.50, 100.90, "take-profit")] // only the take-profit is inside the bar
    [InlineData(100.00, 100.50, 95.00, 95.50, "stop-loss")] // only the stop-loss is inside the bar
    [InlineData(100.00, 105.00, 95.50, 104.00, "stop-loss")] // both inside; low is 4.50 from the open, high 5.00: low first
    [InlineData(100.00, 101.00, 95.00, 96.00, "take-profit")] // both inside; high is 1.00 from the open, low 5.00: high first
    public void Linked_exits_inside_one_bar_resolve_in_the_order_open_nearer_extreme_farther_extreme(decimal open, decimal high, decimal low, decimal close, string expected)
    {
        Assert.Equal(expected, ResolveExitInsideBar(open, high, low, close));
    }

    [Theory(Skip = "BUG: OhlcPath orders the extremes by candle direction (up: low first, down: high first), not by distance from the open as documented")]
    [InlineData(100.00, 101.00, 95.00, 100.50, "take-profit")] // closes up, yet the high (1.00 away) is nearer than the low (5.00)
    [InlineData(100.00, 105.00, 95.50, 99.00, "stop-loss")] // closes down, yet the low (4.50 away) is nearer than the high (5.00)
    public void Nearer_extreme_is_visited_first_even_when_the_candle_closes_in_the_other_direction(decimal open, decimal high, decimal low, decimal close, string expected)
    {
        Assert.Equal(expected, ResolveExitInsideBar(open, high, low, close));
    }

    [Fact(Skip = "BUG: a stop triggered inside a bar is filled at the bar's extreme instead of at its trigger price")]
    public void Stop_triggered_inside_a_bar_fills_at_its_trigger_price_not_at_the_bars_high()
    {
        // FillModel documents that with probFillOnStop = 1 a triggered stop fills at the trigger price.
        // The bar opens below the trigger and trades continuously up through 101.00 on its way to 103.00.
        using SimHarness sim = SimHarness.Spot();
        StopMarketOrder? order = null;
        sim.Bar(60_000, 100.00m, 100.00m, 100.00m, 100.00m)
            .At(90_000, s => order = s.Submit(s.Orders.StopMarket(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(101.00m))))
            .Bar(120_000, 100.00m, 103.00m, 99.80m, 102.00m)
            .Run();

        Assert.Equal(sim.Px(101.00m), sim.SingleFill(order!).LastPx);
    }

    [Fact(Skip = "BUG: a market-if-touched order triggered inside a bar is filled at the bar's extreme, a better price than any real fill")]
    public void Buy_if_touched_order_triggered_inside_a_bar_does_not_fill_below_its_trigger_at_the_bars_low()
    {
        // The bar trades down through 99.50 to 98.00. The order becomes a market order at 99.50; buying the exact low
        // of the bar is look-ahead optimism.
        using SimHarness sim = SimHarness.Spot();
        MarketIfTouchedOrder? order = null;
        sim.Bar(60_000, 100.00m, 100.00m, 100.00m, 100.00m)
            .At(90_000, s => order = s.Submit(s.Orders.MarketIfTouched(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(99.50m))))
            .Bar(120_000, 100.00m, 100.20m, 98.00m, 99.00m)
            .Run();

        Assert.Equal(sim.Px(99.50m), sim.SingleFill(order!).LastPx);
    }

    /// <summary>
    /// Opens a long at 100.00, places a linked take-profit (100.80) and stop-loss (96.00), replays one bar and
    /// reports which exit was filled.
    /// </summary>
    private static string ResolveExitInsideBar(decimal open, decimal high, decimal low, decimal close)
    {
        using SimHarness sim = SimHarness.Spot();
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
}
