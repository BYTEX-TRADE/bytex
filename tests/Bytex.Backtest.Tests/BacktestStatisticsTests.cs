using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model.Primitives;

namespace Bytex.Backtest.Tests;

// Why: performance statistics are what people quote from a backtest, so each one is recomputed here by hand from
// the four scripted trades described in StatisticsScenario (net P&L +8, -8, +2 and one open position).
public sealed class BacktestStatisticsTests
{
    private static CurrencyStatistics Usdt(BacktestResult result) => Assert.Single(result.Currencies, c => c.Currency.Equals(Currencies.USDT));

    [Fact]
    public void Run_identity_period_and_counters_describe_what_was_replayed()
    {
        using SimHarness sim = StatisticsScenario.Run();

        BacktestResult result = sim.Engine.GetResult();

        Assert.Equal("statistics", result.RunId);
        Assert.Equal("TRADER-001", result.TraderId.Value);
        Assert.Equal(Scripted.Ms(1000), result.BacktestStart);
        Assert.Equal(Scripted.Ms((3 * StatisticsScenario.Day) + 3000), result.BacktestEnd);
        Assert.Equal(8, result.Iterations); // two quotes per trade
        Assert.Equal(7, result.TotalOrders);
        Assert.Equal(4, result.TotalPositions);
        Assert.Equal(29, result.TotalEvents); // per order: submitted, accepted, filled (21) + account states: initial and one per fill (8)
        Assert.NotNull(result.RunStarted);
        Assert.NotNull(result.RunFinished);
    }

    [Fact]
    public void Profit_and_loss_by_currency_separates_realised_unrealised_and_commissions()
    {
        using SimHarness sim = StatisticsScenario.Run();

        CurrencyStatistics usdt = Usdt(sim.Engine.GetResult());

        Assert.Equal(1_000_000m, usdt.StartingBalance);
        Assert.Equal(1_000_001m, usdt.EndingBalance); // +10 -6 +4 gross, minus 7 fills at 1 USDT
        Assert.Equal(1m, usdt.RealizedPnl); // 8 - 8 + 2 - 1
        Assert.Equal(3m, usdt.UnrealizedPnl); // long 1 from 100, marked at the 103.0 bid
        Assert.Equal(4m, usdt.TotalPnl);
        Assert.Equal(7m, usdt.TotalCommissions);
        Assert.Equal(0.0004m, usdt.ReturnPercent); // 4 / 1 000 000 * 100
    }

    [Fact]
    public void Profit_factor_and_expectancy_are_computed_over_closed_positions()
    {
        using SimHarness sim = StatisticsScenario.Run();

        CurrencyStatistics usdt = Usdt(sim.Engine.GetResult());

        Assert.Equal(1.25m, usdt.ProfitFactor); // gross profit 8 + 2 = 10, gross loss 8
        Assert.Equal(2m / 3m, usdt.Expectancy); // (8 - 8 + 2) / 3
    }

    [Fact]
    public void Trade_statistics_count_winners_losers_and_direction()
    {
        using SimHarness sim = StatisticsScenario.Run();

        TradeStatistics trades = sim.Engine.GetResult().Trades;

        Assert.Equal(4, trades.TotalPositions);
        Assert.Equal(3, trades.ClosedPositions);
        Assert.Equal(1, trades.OpenPositions);
        Assert.Equal(2, trades.Winners);
        Assert.Equal(1, trades.Losers);
        Assert.Equal(0, trades.Breakeven);
        Assert.Equal(2m / 3m, trades.WinRate);
        Assert.Equal(5m, trades.AverageWinner); // (8 + 2) / 2
        Assert.Equal(-8m, trades.AverageLoser);
        Assert.Equal(8m, trades.LargestWinner);
        Assert.Equal(-8m, trades.LargestLoser);
        Assert.Equal(3, trades.LongPositions);
        Assert.Equal(1, trades.ShortPositions);
        Assert.Equal(TimeSpan.FromSeconds(120), trades.AverageDuration); // (60 + 120 + 180) / 3
    }

    [Fact]
    public void Average_return_is_the_mean_of_each_closed_positions_return_on_its_entry_notional()
    {
        using SimHarness sim = StatisticsScenario.Run();

        decimal expected = ((8m / 100m) + (-8m / 110m) + (2m / 104m)) / 3m;
        Assert.Equal(expected, sim.Engine.GetResult().Trades.AverageReturn, 12);
    }

    [Fact]
    public void Equity_curve_has_one_point_per_fill_accumulating_realised_profit_net_of_fees()
    {
        using SimHarness sim = StatisticsScenario.Run();
        long day = StatisticsScenario.Day;

        IReadOnlyList<EquityPoint> curve = sim.Engine.GetResult().EquityCurves[Currencies.USDT];

        Assert.Equal(
            [
                new EquityPoint(Scripted.Ms(2000), 999_999m), // entry fee
                new EquityPoint(Scripted.Ms(62_000), 1_000_008m), // +10 - 1
                new EquityPoint(Scripted.Ms(day + 2000), 1_000_007m),
                new EquityPoint(Scripted.Ms(day + 122_000), 1_000_000m), // -6 - 1
                new EquityPoint(Scripted.Ms((2 * day) + 2000), 999_999m),
                new EquityPoint(Scripted.Ms((2 * day) + 182_000), 1_000_002m), // +4 - 1
                new EquityPoint(Scripted.Ms((3 * day) + 2000), 1_000_001m),
            ],
            curve);
    }

    [Fact]
    public void Max_drawdown_is_the_largest_peak_to_trough_fall_of_the_equity_curve()
    {
        using SimHarness sim = StatisticsScenario.Run();

        CurrencyStatistics usdt = Usdt(sim.Engine.GetResult());

        Assert.Equal(9m, usdt.MaxDrawdown); // peak 1 000 008 -> trough 999 999
        Assert.Equal(9m / 1_000_008m * 100m, usdt.MaxDrawdownPercent);
    }

    [Fact]
    public void Sharpe_ratio_is_the_annualised_mean_over_sample_deviation_of_daily_returns()
    {
        using SimHarness sim = StatisticsScenario.Run();

        // Daily P&L +8, -8, +2 on 1 000 000. Against that balance compounding is irrelevant at the asserted precision.
        double[] r = [8e-6, -8e-6, 2e-6];
        double mean = (r[0] + r[1] + r[2]) / 3d;
        double sampleDeviation = Math.Sqrt((Math.Pow(r[0] - mean, 2) + Math.Pow(r[1] - mean, 2) + Math.Pow(r[2] - mean, 2)) / 2d);
        double expected = mean / sampleDeviation * Math.Sqrt(252d); // about 1.3093

        Assert.Equal(1.3093, expected, 4);
        Assert.Equal(expected, Usdt(sim.Engine.GetResult()).SharpeRatio, 4);
    }

    [Fact]
    public void Sortino_ratio_divides_by_the_downside_deviation_only()
    {
        using SimHarness sim = StatisticsScenario.Run();

        // Only the -8e-6 day is below zero: downside deviation = sqrt((8e-6)^2 / 3 observations).
        double mean = (8e-6 - 8e-6 + 2e-6) / 3d;
        double downside = Math.Sqrt(8e-6 * 8e-6 / 3d);
        double expected = mean / downside * Math.Sqrt(252d); // about 2.2913

        Assert.Equal(2.2913, expected, 4);
        Assert.Equal(expected, Usdt(sim.Engine.GetResult()).SortinoRatio, 4);
    }

    [Fact(Skip = "BUG: days without a closed position are dropped from the daily return series, although the ratio is annualised with sqrt(252) as if the series were daily")]
    public void Sharpe_ratio_counts_a_day_without_trades_as_a_zero_return_day()
    {
        // Same three closed trades, but with an idle day between the first and the second: +8, 0, -8, +2.
        using SimHarness sim = StatisticsScenario.Run(dayA: 0, dayB: 2, dayC: 3, dayD: 4);

        double[] r = [8e-6, 0d, -8e-6, 2e-6];
        double mean = r.Average();
        double sampleDeviation = Math.Sqrt(r.Sum(x => Math.Pow(x - mean, 2)) / (r.Length - 1));
        double expected = mean / sampleDeviation * Math.Sqrt(252d); // about 1.2011

        Assert.Equal(expected, Usdt(sim.Engine.GetResult()).SharpeRatio, 4);
    }

    [Fact(Skip = "BUG: the drawdown scan takes the first equity point (already net of the first fee) as its first peak and ignores the starting balance")]
    public void Max_drawdown_is_measured_from_the_starting_balance_when_the_run_starts_with_a_loss()
    {
        // Start 100 000. Entry fee 10 -> 99 990. Exit 1000 lower plus fee 10 -> 98 980. Peak-to-trough: 100 000 - 98 980.
        using SimHarness sim = SimHarness.Perp(new SimOptions { FeeModel = new FixedFeeModel(new Money(10m, Currencies.USDT)) });
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, Core.Model.OrderSide.Buy, sim.Qty(1m))))
            .Quote(2000, 49_000.0m, 49_000.0m)
            .At(2500, s => s.Submit(s.Orders.Market(sim.Id, Core.Model.OrderSide.Sell, sim.Qty(1m))))
            .Quote(3000, 49_000.0m, 49_000.0m)
            .Run();

        CurrencyStatistics usdt = Usdt(sim.Engine.GetResult());

        Assert.Equal(98_980m, usdt.EndingBalance);
        Assert.Equal(1_020m, usdt.MaxDrawdown);
        Assert.Equal(1.02m, usdt.MaxDrawdownPercent);
    }

    [Fact(Skip = "BUG: commissions paid in a currency that no position settles in are missing from the per-currency statistics")]
    public void Commissions_paid_in_a_third_currency_are_reported_under_that_currency()
    {
        using SimHarness sim = SimHarness.Spot(new SimOptions
        {
            FeeModel = new FixedFeeModel(new Money(0.5m, Currencies.USDC)),
            StartingBalances = [new Money(1000m, Currencies.USDT), new Money(10m, Currencies.USDC)],
        });
        sim.Quote(1000, 100.00m, 100.00m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, Core.Model.OrderSide.Buy, sim.Qty(1m))))
            .Quote(2000, 100.00m, 100.00m)
            .Run();

        CurrencyStatistics usdc = Assert.Single(sim.Engine.GetResult().Currencies, c => c.Currency.Equals(Currencies.USDC));

        Assert.Equal(9.5m, usdc.EndingBalance);
        Assert.Equal(0.5m, usdc.TotalCommissions);
    }

    [Fact]
    public void Run_with_only_winning_trades_reports_an_unbounded_profit_factor_and_no_drawdown_beyond_fees()
    {
        using SimHarness sim = SimHarness.Perp(new SimOptions { FeeModel = new FixedFeeModel(Money.Zero(Currencies.USDT)) });
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, Core.Model.OrderSide.Buy, sim.Qty(1m))))
            .Quote(2000, 50_100.0m, 50_100.0m)
            .At(2500, s => s.Submit(s.Orders.Market(sim.Id, Core.Model.OrderSide.Sell, sim.Qty(1m))))
            .Quote(3000, 50_100.0m, 50_100.0m)
            .Run();

        BacktestResult result = sim.Engine.GetResult();
        CurrencyStatistics usdt = Usdt(result);

        Assert.Equal(decimal.MaxValue, usdt.ProfitFactor);
        Assert.Equal(0m, usdt.MaxDrawdown);
        Assert.Equal(0d, usdt.SharpeRatio); // a single daily observation has no deviation
        Assert.Equal(0.1m, usdt.ReturnPercent); // 100 / 100 000 * 100
        Assert.Contains("profit factor inf", result.Summary(), StringComparison.Ordinal);
    }

    [Fact]
    public void Run_without_any_trade_reports_zeroes_rather_than_failing()
    {
        using SimHarness sim = SimHarness.Perp();
        sim.Quote(1000, 50_000.0m, 50_000.0m).Quote(2000, 50_100.0m, 50_100.0m).Run();

        BacktestResult result = sim.Engine.GetResult();
        CurrencyStatistics usdt = Usdt(result);

        Assert.Equal(100_000m, usdt.EndingBalance);
        Assert.Equal(0m, usdt.TotalPnl);
        Assert.Equal(0m, usdt.ProfitFactor);
        Assert.Equal(0m, usdt.Expectancy);
        Assert.Equal(0m, result.Trades.WinRate);
        Assert.Equal(TimeSpan.Zero, result.Trades.AverageDuration);
        Assert.Empty(result.EquityCurves[Currencies.USDT]);
        Assert.Empty(result.Fills);
    }

    [Fact]
    public void Multi_currency_cash_account_reports_every_starting_currency()
    {
        using SimHarness sim = SimHarness.Spot(new SimOptions { StartingBalances = [new Money(1000m, Currencies.USDT), new Money(2m, Currencies.BTC)] });
        sim.Quote(1000, 100.00m, 100.00m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, Core.Model.OrderSide.Sell, sim.Qty(1m))))
            .Quote(2000, 100.00m, 100.00m)
            .Run();

        BacktestResult result = sim.Engine.GetResult();

        CurrencyStatistics usdt = Usdt(result);
        CurrencyStatistics btc = Assert.Single(result.Currencies, c => c.Currency.Equals(Currencies.BTC));
        Assert.Equal(1000m, usdt.StartingBalance);
        Assert.Equal(1099.8m, usdt.EndingBalance); // +100 - 0.2 taker fee
        Assert.Equal(2m, btc.StartingBalance);
        Assert.Equal(1m, btc.EndingBalance);
    }
}
