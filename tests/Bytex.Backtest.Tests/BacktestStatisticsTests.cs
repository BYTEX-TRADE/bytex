using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model.Primitives;

namespace Bytex.Backtest.Tests;

// Why: performance statistics are what people quote from a backtest, so each one is recomputed here by hand from
// the four scripted trades described in StatisticsScenario (net P&L +8, -8, +2 and one open position).
public sealed class BacktestStatisticsTests
{
    private static CurrencyStatistics Usdt(BacktestResult result) => Assert.Single(result.Currencies, c => c.Currency.Equals(Currencies.USDT));

    /// <summary>
    /// The four daily returns of the scenario, from the last equity of each day of the curve: each day against the
    /// one before it, the first against the starting balance. Computed here so the ratios below are checked against
    /// arithmetic rather than against themselves.
    /// </summary>
    private static double[] DailyReturns()
    {
        decimal[] closes = [1_000_009m, 1_000_001m, 1_000_003m, 1_000_004m];
        decimal previous = 1_000_000m;
        List<double> returns = new();
        foreach (decimal close in closes)
        {
            returns.Add((double)((close - previous) / previous));
            previous = close;
        }

        return returns.ToArray();
    }

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
        // per order: submitted, accepted, filled (21) + account states: initial, one when an entry order posts its
        // margin (4) and one per fill (8)
        Assert.Equal(33, result.TotalEvents);
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
    public void Equity_curve_has_a_point_per_timestamp_with_every_open_position_marked_to_market()
    {
        // The curve a report draws. One point per timestamp of data - eight quotes here - each the starting balance
        // plus what was realised by then plus what the open position was worth at that quote. Built from fills
        // instead, it had no point between an entry and its exit, so it could not fall while a position was held and
        // a strategy that never closed a loser showed no drawdown at all.
        using SimHarness sim = StatisticsScenario.Run();
        long day = StatisticsScenario.Day;

        IReadOnlyList<EquityPoint> curve = sim.Engine.GetResult().EquityCurves[Currencies.USDT];

        Assert.Equal(
            [
                new EquityPoint(Scripted.Ms(1000), 1_000_000m), // nothing traded yet
                new EquityPoint(Scripted.Ms(61_000), 1_000_009m), // long 1 from 100 at 110: -1 fee, +10 open
                new EquityPoint(Scripted.Ms(day + 1000), 1_000_008m), // sold at 110: +8 realised, flat
                new EquityPoint(Scripted.Ms(day + 121_000), 1_000_001m), // long 1 from 110 at 104: +7 realised, -6 open
                new EquityPoint(Scripted.Ms((2 * day) + 1000), 1_000_000m), // sold at 104: level again, flat
                new EquityPoint(Scripted.Ms((2 * day) + 181_000), 1_000_003m), // short 1 from 104 at 100: -1 realised, +4 open
                new EquityPoint(Scripted.Ms((3 * day) + 1000), 1_000_002m), // bought back at 100: +2 realised, flat
                new EquityPoint(Scripted.Ms((3 * day) + 3000), 1_000_004m), // long 1 from 100 at the 103.0 bid: +1, +3 open
            ],
            curve);
    }

    [Fact]
    public void Max_drawdown_is_the_largest_peak_to_trough_fall_of_the_equity_curve()
    {
        using SimHarness sim = StatisticsScenario.Run();

        CurrencyStatistics usdt = Usdt(sim.Engine.GetResult());

        // Peak 1 000 009 while the first position was open, trough 1 000 000 when the second was six under water.
        Assert.Equal(9m, usdt.MaxDrawdown);
        Assert.Equal(9m / 1_000_009m * 100m, usdt.MaxDrawdownPercent);
    }

    [Fact]
    public void Sharpe_ratio_is_the_annualised_mean_over_sample_deviation_of_daily_returns()
    {
        using SimHarness sim = StatisticsScenario.Run();

        // The last equity of each day of the curve, against the day before it: four days of data, four returns,
        // including the day whose open position was under water and closed nothing.
        double[] r = DailyReturns();
        double mean = r.Average();
        double sampleDeviation = Math.Sqrt(r.Sum(x => Math.Pow(x - mean, 2)) / (r.Length - 1));
        double expected = mean / sampleDeviation * Math.Sqrt(252d);

        Assert.Equal(4, r.Length);
        Assert.Equal(expected, Usdt(sim.Engine.GetResult()).SharpeRatio, 6);
    }

    [Fact]
    public void Sortino_ratio_divides_by_the_downside_deviation_only()
    {
        using SimHarness sim = StatisticsScenario.Run();

        // One day of the four is below zero, and the downside deviation divides by every observation.
        double[] r = DailyReturns();
        double mean = r.Average();
        double downside = Math.Sqrt(r.Where(x => x < 0d).Sum(x => x * x) / r.Length);
        double expected = mean / downside * Math.Sqrt(252d);

        Assert.Single(r, x => x < 0d);
        Assert.Equal(expected, Usdt(sim.Engine.GetResult()).SortinoRatio, 6);
    }

    [Fact]
    public void A_day_with_no_data_is_not_in_the_series_and_a_day_with_data_always_is()
    {
        // The same four trades with an idle day between the first and the second. The idle day carries no data, so
        // the curve has no point in it and the series has four returns, not five: a day the run never saw is not a
        // day of zero return. What the series does have is one return for every day of data, whether or not a trade
        // closed in it, which is the difference this fix makes.
        using SimHarness sim = StatisticsScenario.Run(dayA: 0, dayB: 2, dayC: 3, dayD: 4);

        BacktestResult result = sim.Engine.GetResult();
        IReadOnlyList<EquityPoint> curve = result.EquityCurves[Currencies.USDT];

        Assert.Equal(8, curve.Count);
        Assert.Equal(4, curve.Select(p => DateOnly.FromDateTime(p.Timestamp.ToDateTimeUtc())).Distinct().Count());
        Assert.NotEqual(0d, Usdt(result).SharpeRatio);
    }

    [Fact]
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

    [Fact]
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

        Assert.Null(usdt.ProfitFactor); // no losing trade leaves the ratio without a value
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
        Assert.Equal(0m, usdt.ProfitFactor); // no trade at all: nothing gained and nothing lost
        Assert.Equal(0m, usdt.Expectancy);
        Assert.Equal(0m, result.Trades.WinRate);
        Assert.Equal(TimeSpan.Zero, result.Trades.AverageDuration);

        // The curve is the account over time, so a run that traded nothing is a flat line at its balance rather
        // than no line at all - which is what a host would have nothing to draw.
        Assert.Equal([100_000m, 100_000m], result.EquityCurves[Currencies.USDT].Select(p => p.Equity));
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

    [Fact]
    public void An_idle_day_lowers_the_ratios_instead_of_being_left_out_of_the_series()
    {
        using SimHarness dense = StatisticsScenario.Run();
        using SimHarness withGap = StatisticsScenario.Run(dayA: 0, dayB: 2, dayC: 3, dayD: 4);

        CurrencyStatistics packed = Usdt(dense.Engine.GetResult());
        CurrencyStatistics idle = Usdt(withGap.Engine.GetResult());

        // The same four trades, spread over five calendar days instead of four. The returns are the same four
        // numbers either way, because a day without data has no point on the curve - so neither ratio moves.
        Assert.Equal(packed.SharpeRatio, idle.SharpeRatio, 6);
        Assert.Equal(packed.SortinoRatio, idle.SortinoRatio, 6);
    }

    [Fact]
    public void A_fee_paid_in_another_currency_is_not_counted_against_the_one_the_trade_settled_in()
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

        BacktestResult result = sim.Engine.GetResult();

        // The fee is reported once, under the currency that paid it, and nowhere else.
        Assert.Equal(0.5m, Assert.Single(result.Currencies, c => c.Currency.Equals(Currencies.USDC)).TotalCommissions);
        Assert.Equal(0m, Usdt(result).TotalCommissions);
    }

    [Fact]
    public void A_run_that_only_loses_reports_the_drawdown_from_the_first_fee_on()
    {
        // No price move at all: the two fixed fees are the whole loss, and both belong to the drawdown.
        using SimHarness sim = SimHarness.Perp(new SimOptions { FeeModel = new FixedFeeModel(new Money(25m, Currencies.USDT)) });
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, Core.Model.OrderSide.Buy, sim.Qty(1m))))
            .Quote(2000, 50_000.0m, 50_000.0m)
            .At(2500, s => s.Submit(s.Orders.Market(sim.Id, Core.Model.OrderSide.Sell, sim.Qty(1m))))
            .Quote(3000, 50_000.0m, 50_000.0m)
            .Run();

        CurrencyStatistics usdt = Usdt(sim.Engine.GetResult());

        Assert.Equal(99_950m, usdt.EndingBalance);
        Assert.Equal(50m, usdt.MaxDrawdown);
        Assert.Equal(0.05m, usdt.MaxDrawdownPercent);
    }
}
