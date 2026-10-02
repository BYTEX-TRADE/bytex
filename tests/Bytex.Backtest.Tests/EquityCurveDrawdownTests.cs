using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Primitives;

namespace Bytex.Backtest.Tests;

// Why: the equity curve was built from fills, one point each, so nothing that happened to an open position existed in
// it. The curve could not fall while a position was held, and everything taken from it agreed: a strategy that bought
// and held through a 20% fall reported a maximum drawdown of nothing, and its Sharpe and Sortino were computed over
// the days that happened to close a trade. A report that says a strategy which never closes a loser has no risk is
// worse than no report. These tests hold the curve to the account: a point per timestamp, marked to market.
public sealed class EquityCurveDrawdownTests
{
    private static CurrencyStatistics Usdt(BacktestResult result) =>
        Assert.Single(result.Currencies, c => c.Currency.Equals(Currencies.USDT));

    /// <summary>
    /// Buys one contract at 50 000 and holds it. The price falls to 40 000 - twenty percent, ten thousand of a hundred
    /// thousand - and comes back to 50 000, where the run ends. Nothing is ever closed, so a fill-built curve had two
    /// points and no fall at all.
    /// </summary>
    private static SimHarness BuyAndHoldThroughAFall()
    {
        SimHarness sim = SimHarness.Perp(new SimOptions { FeeModel = new FixedFeeModel(Money.Zero(Currencies.USDT)) });
        return sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(2000, 45_000.0m, 45_000.0m)
            .Quote(3000, 40_000.0m, 40_000.0m)
            .Quote(4000, 47_000.0m, 47_000.0m)
            .Quote(5000, 50_000.0m, 50_000.0m)
            .Run();
    }

    [Fact]
    public void The_curve_falls_while_a_position_is_held_and_nothing_is_closed()
    {
        using SimHarness sim = BuyAndHoldThroughAFall();

        BacktestResult result = sim.Engine.GetResult();
        IReadOnlyList<EquityPoint> curve = result.EquityCurves[Currencies.USDT];

        // One point per quote, and the middle of the run is ten thousand below the start although no trade closed.
        Assert.Equal([100_000m, 95_000m, 90_000m, 97_000m, 100_000m], curve.Select(p => p.Equity));
        Assert.DoesNotContain(result.Positions, p => p.TsClosed is not null);
    }

    [Fact]
    public void Max_drawdown_is_what_the_open_position_cost_at_its_worst()
    {
        using SimHarness sim = BuyAndHoldThroughAFall();

        CurrencyStatistics usdt = Usdt(sim.Engine.GetResult());

        // Peak 100 000 at the start, trough 90 000 while holding: ten thousand, ten percent of the peak. The
        // fill-built curve reported zero, because it had no point between the entry and the end of the run.
        Assert.Equal(10_000m, usdt.MaxDrawdown);
        Assert.Equal(10m, usdt.MaxDrawdownPercent);
        Assert.Equal(0m, usdt.RealizedPnl);
        Assert.Equal(0m, usdt.TotalPnl); // it came back: the drawdown is the only trace of the fall
    }

    [Fact]
    public void A_position_that_is_still_open_at_the_end_is_worth_what_it_is_worth()
    {
        // The same hold, ended while the position is under water: the last point of the curve is the loss it carries.
        using SimHarness sim = SimHarness.Perp(new SimOptions { FeeModel = new FixedFeeModel(Money.Zero(Currencies.USDT)) });
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(2000, 43_000.0m, 43_000.0m)
            .Run();

        BacktestResult result = sim.Engine.GetResult();
        CurrencyStatistics usdt = Usdt(result);

        Assert.Equal([100_000m, 93_000m], result.EquityCurves[Currencies.USDT].Select(p => p.Equity));
        Assert.Equal(-7_000m, usdt.UnrealizedPnl);
        Assert.Equal(-7_000m, usdt.TotalPnl);
        Assert.Equal(7_000m, usdt.MaxDrawdown);
    }

    [Fact]
    public void A_timestamp_carrying_more_than_one_element_gets_one_point_not_one_each()
    {
        // A quote and a trade at the same millisecond - two instruments of one venue do the same thing. The curve is
        // the account over time, so a moment is one point: sampling per element would draw a step for each of them
        // and make the series depend on how much data a timestamp happened to carry.
        using SimHarness sim = SimHarness.Perp(new SimOptions { FeeModel = new FixedFeeModel(Money.Zero(Currencies.USDT)) });
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(2000, 48_000.0m, 48_000.0m)
            .Trade(2000, 48_000.0m)
            .Run();

        IReadOnlyList<EquityPoint> curve = sim.Engine.GetResult().EquityCurves[Currencies.USDT];

        Assert.Equal([100_000m, 98_000m], curve.Select(p => p.Equity));
        Assert.Equal(curve.Select(p => p.Timestamp).Distinct().Count(), curve.Count);
    }

    [Fact]
    public void Every_day_of_a_held_position_has_a_return_of_its_own()
    {
        // Three days, one position held across all of them, nothing closed. Realised profit is zero every day, so the
        // old series had no observations at all and the ratios were zero; the curve gives a return per day.
        const long day = 86_400_000L;
        using SimHarness sim = SimHarness.Perp(new SimOptions { FeeModel = new FixedFeeModel(Money.Zero(Currencies.USDT)) });
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(day + 1000, 48_000.0m, 48_000.0m)
            .Quote((2 * day) + 1000, 52_000.0m, 52_000.0m)
            .Run();

        CurrencyStatistics usdt = Usdt(sim.Engine.GetResult());

        // Day one ends level, day two two thousand down, day three four thousand up on the day before.
        Assert.NotEqual(0d, usdt.SharpeRatio);
        Assert.NotEqual(0d, usdt.SortinoRatio);
        Assert.Equal(2_000m, usdt.MaxDrawdown);
    }
}
