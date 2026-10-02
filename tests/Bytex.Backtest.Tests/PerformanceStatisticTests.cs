using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Primitives;

namespace Bytex.Backtest.Tests;

// Why (R8.21): the built-in statistics are a judgement about what matters, and whose money it is decides that. A firm
// judging by Calmar, by turnover, by the P&L of one hour of the day or by a measure of its own would otherwise recompute
// it outside the engine from the tables - and a figure computed twice from two readings of one run is how two numbers in
// one report come to disagree.
//
// So a run can be given figures of its own. What is pinned here: that they reach the result, the printed summary and the
// comparable table; that a search can be pointed at one by name; that "no answer" stays distinct from zero; and that a
// badly written one costs its own figure and not the run.
public sealed class PerformanceStatisticTests
{
    private static SimOptions Options(params IPerformanceStatistic[] statistics) => new()
    {
        AccountType = AccountType.Margin,
        StartingBalances = [new Money(100_000m, Currencies.USDT)],
        DefaultLeverage = 20m,
        FeeModel = new PercentFeeModel(0m),
        Statistics = statistics,
    };

    /// <summary>One round trip that makes 1 000, so every figure below has something to be worked out from.</summary>
    private static void OneWinner(SimHarness sim) => sim
        .Quote(1000, 50_000.0m, 50_000.0m, size: 100m)
        .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
        .Quote(2000, 51_000.0m, 51_000.0m, size: 100m)
        .At(2500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Sell, sim.Qty(1m))))
        .Quote(3000, 51_000.0m, 51_000.0m, size: 100m)
        .Run();

    [Fact]
    public void A_figure_a_run_was_asked_for_is_in_its_result()
    {
        using SimHarness sim = SimHarness.Perp(Options(
            new PerformanceStatistic("turnover", input => input.Closed.Sum(p => p.PeakQuantity.Value * p.AvgPxOpen))));

        OneWinner(sim);

        CurrencyStatistics stats = Assert.Single(sim.Engine.GetResult().Currencies, c => c.Currency.Equals(Currencies.USDT));

        Assert.Equal(50_000m, stats.Custom["turnover"]);
    }

    /// <summary>
    /// <b>And they reach result.json.</b> They reached the printed summary and a batch's rows and not this file, for a
    /// whole release, while the changelog said they were here - so the one audience that cannot recompute a figure, a
    /// program reading the run, was the one that did not get it.
    ///
    /// <para>
    /// The unanswerable one is asserted too, because a map that drops nulls would satisfy a test that only looked for
    /// the figures that had something to say, and "asked and unanswerable" against "never asked" is the distinction
    /// this whole feature makes.
    /// </para>
    /// </summary>
    [Fact]
    public void The_figures_reach_result_json_including_the_one_with_nothing_to_say()
    {
        using SimHarness sim = SimHarness.Perp(Options(
            new PerformanceStatistic("turnover", input => input.Closed.Sum(p => p.PeakQuantity.Value * p.AvgPxOpen)),
            new PerformanceStatistic("nothing", _ => null)));

        OneWinner(sim);

        using System.Text.Json.JsonDocument written = System.Text.Json.JsonDocument.Parse(ReportWriter.ToJson(sim.Engine.GetResult()));
        System.Text.Json.JsonElement custom = written.RootElement.GetProperty("currencies").EnumerateArray()
            .First(c => string.Equals(c.GetProperty("currency").GetString(), Currencies.USDT.Code, StringComparison.Ordinal))
            .GetProperty("custom");

        Assert.Equal(50_000m, custom.GetProperty("turnover").GetDecimal());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, custom.GetProperty("nothing").ValueKind);
    }

    [Fact]
    public void A_statistic_can_read_what_the_engine_already_worked_out()
    {
        // Rather than working it out again. Two readings of one run is how a report comes to contradict itself.
        using SimHarness sim = SimHarness.Perp(Options(
            new PerformanceStatistic("pnlPerTrade", input => input.Built.RealizedPnl / Math.Max(1, input.Closed.Count()))));

        OneWinner(sim);

        CurrencyStatistics stats = Assert.Single(sim.Engine.GetResult().Currencies, c => c.Currency.Equals(Currencies.USDT));

        Assert.Equal(stats.RealizedPnl, stats.Custom["pnlPerTrade"]);
        Assert.Equal(1_000m, stats.Custom["pnlPerTrade"]);
    }

    [Fact]
    public void A_figure_with_nothing_to_say_is_absent_rather_than_zero()
    {
        // "Not measurable here" and "measured, and zero" are different facts, and a reader who cannot tell them apart
        // will act on one of them believing the other.
        using SimHarness sim = SimHarness.Perp(Options(new PerformanceStatistic("nothing", _ => null)));

        OneWinner(sim);

        CurrencyStatistics stats = Assert.Single(sim.Engine.GetResult().Currencies, c => c.Currency.Equals(Currencies.USDT));

        Assert.True(stats.Custom.ContainsKey("nothing"), "asked and unanswerable is not the same as never asked");
        Assert.Null(stats.Custom["nothing"]);
    }

    [Fact]
    public void A_statistic_that_throws_costs_its_own_figure_and_not_the_run()
    {
        using SimHarness sim = SimHarness.Perp(Options(
            new PerformanceStatistic("broken", _ => throw new InvalidOperationException("written badly")),
            new PerformanceStatistic("fine", _ => 7m)));

        OneWinner(sim);

        CurrencyStatistics stats = Assert.Single(sim.Engine.GetResult().Currencies, c => c.Currency.Equals(Currencies.USDT));

        Assert.Null(stats.Custom["broken"]);
        Assert.Equal(7m, stats.Custom["fine"]);
        Assert.Equal(1_000m, stats.RealizedPnl);
    }

    [Fact]
    public void Two_statistics_cannot_share_a_name()
    {
        // A figure in a result has one meaning. Two of them under one name is a report that means whichever ran last.
        using SimHarness sim = SimHarness.Perp(Options(
            new PerformanceStatistic("same", _ => 1m),
            new PerformanceStatistic("same", _ => 2m)));

        OneWinner(sim);

        InvalidOperationException e = Assert.Throws<InvalidOperationException>(sim.Engine.GetResult);

        Assert.Contains("Two statistics are called 'same'", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_asked_for_nothing_reports_nothing_extra()
    {
        using SimHarness sim = SimHarness.Perp(Options());

        OneWinner(sim);

        Assert.Empty(Assert.Single(sim.Engine.GetResult().Currencies, c => c.Currency.Equals(Currencies.USDT)).Custom);
    }

    [Fact]
    public void The_printed_summary_carries_them()
    {
        // A figure a run was asked for and does not print is a figure nobody reads.
        using SimHarness sim = SimHarness.Perp(Options(
            new PerformanceStatistic("turnover", input => input.Closed.Sum(p => p.PeakQuantity.Value * p.AvgPxOpen)),
            new PerformanceStatistic("nothing", _ => null)));

        OneWinner(sim);

        string text = sim.Engine.GetResult().Summary();

        Assert.Contains("turnover 50,000.0000", text, StringComparison.Ordinal);
        Assert.Contains("nothing -", text, StringComparison.Ordinal);
    }
}
