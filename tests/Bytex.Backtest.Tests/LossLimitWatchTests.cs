using Bytex.Backtest.Tests.Support;
using Bytex.Core.Engines;
using Bytex.Core.Model;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Primitives;

namespace Bytex.Backtest.Tests;

// Why: the risk engine's own tests prove the arithmetic; this proves the wiring. The open-loss watch is driven by the
// prices the engine sees, so a whole run has to show it working with nothing but data arriving: a strategy that buys,
// holds, and sends nothing while the market falls is exactly the case the old limit could not see, and the same run is
// what a live node does with the same class the same way.
public sealed class LossLimitWatchTests
{
    /// <summary>Buys one contract at 50,000 and holds it while the market falls away from it.</summary>
    private static SimHarness BuyAndHoldThroughAFall(RiskLimits limits)
    {
        SimHarness sim = SimHarness.Perp(new SimOptions
        {
            FeeModel = new FixedFeeModel(Money.Zero(Currencies.USDT)),
            Limits = limits,
        });
        return sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(2000, 48_000.0m, 48_000.0m)
            .Quote(3000, 44_000.0m, 44_000.0m)
            .Quote(4000, 44_000.0m, 44_000.0m)
            .Run();
    }

    [Fact]
    public void A_run_that_holds_through_a_fall_stops_trading_without_sending_anything()
    {
        using SimHarness sim = BuyAndHoldThroughAFall(new RiskLimits { MaxLossPerPeriod = RiskLimit.Of(new Money(5_000m, Currencies.USDT)) });
        RiskEngine risk = sim.Engine.Kernel.RiskEngine;

        // One order was ever submitted, and it filled. The 6,000 the position went on to lose was seen by the watch.
        Assert.Equal(TradingState.Reducing, risk.TradingState);
        Assert.Equal(1, risk.LossLimitStoppedCount);
        Assert.Equal(0, risk.LossLimitDeniedCount);
        Assert.True(risk.LossWatchCount > 0, "the watch never ran");
        Assert.Single(sim.Events.OfType<OrderFilled>());
    }

    [Fact]
    public void A_run_inside_its_limit_is_left_alone()
    {
        using SimHarness sim = BuyAndHoldThroughAFall(new RiskLimits { MaxLossPerPeriod = RiskLimit.Of(new Money(50_000m, Currencies.USDT)) });
        RiskEngine risk = sim.Engine.Kernel.RiskEngine;

        Assert.Equal(TradingState.Active, risk.TradingState);
        Assert.Equal(0, risk.LossLimitStoppedCount);
        Assert.True(risk.LossWatchCount > 0, "the watch never ran");
    }

    [Fact]
    public void A_run_with_no_limits_never_looks()
    {
        using SimHarness sim = BuyAndHoldThroughAFall(new RiskLimits());
        RiskEngine risk = sim.Engine.Kernel.RiskEngine;

        Assert.Equal(TradingState.Active, risk.TradingState);
        Assert.Equal(0, risk.LossWatchCount);
    }

    [Fact]
    public void What_the_run_stopped_at_is_what_the_report_says_it_lost()
    {
        // The two figures are taken from different places - the watch from the portfolio, the report from the equity
        // curve - and a limit that stopped a run at a loss the report then denied would be worse than either.
        using SimHarness sim = BuyAndHoldThroughAFall(new RiskLimits { MaxLossPerPeriod = RiskLimit.Of(new Money(5_000m, Currencies.USDT)) });

        BacktestResult result = sim.Engine.GetResult();
        CurrencyStatistics usdt = Assert.Single(result.Currencies, c => c.Currency.Equals(Currencies.USDT));

        Assert.Equal(-6_000m, usdt.UnrealizedPnl);
        Assert.Equal(6_000m, usdt.MaxDrawdown);
        Assert.Equal(TradingState.Reducing, sim.Engine.Kernel.RiskEngine.TradingState);
    }
}
