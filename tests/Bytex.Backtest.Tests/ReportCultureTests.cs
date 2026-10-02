using System.Text.Json;
using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Primitives;

namespace Bytex.Backtest.Tests;

// Why: every report a run writes leaves the process - result.json is parsed by hosts, the CSVs are opened in
// spreadsheets and compared between machines, the tearsheet is emailed. The engine writes them invariantly, which is
// correct and was, until this file, unpinned: the repository builds with InvariantGlobalization, so the current culture
// IS the invariant one in every test and nothing could tell the difference.
//
// A comma decimal separator is not a cosmetic difference here. It breaks a CSV, whose columns are separated by commas.
public sealed class ReportCultureTests
{
    private static BacktestResult Result()
    {
        using SimHarness sim = StatisticsScenario.Run();
        return sim.Engine.GetResult();
    }

    [Fact]
    public void The_json_a_host_parses_is_the_same_under_any_culture()
    {
        BacktestResult result = Result();
        string invariant = ReportWriter.ToJson(result);

        using (new CommaDecimalCulture())
        {
            Assert.Equal(invariant, ReportWriter.ToJson(result));
        }

        // And it really does carry numbers that a comma culture would have written differently.
        using JsonDocument document = JsonDocument.Parse(invariant);
        Assert.Equal(1_000_000m, document.RootElement.GetProperty("currencies")[0].GetProperty("startingBalance").GetDecimal());
    }

    [Fact]
    public void A_csv_written_under_a_comma_culture_still_has_its_columns()
    {
        // The failure this prevents is not an odd-looking number: a decimal written "1,5" in a comma-separated file
        // splits one column into two, and every column after it in that row moves.
        BacktestResult result = Result();
        string invariant = ReportWriter.ToCsv(result.Fills);
        int columns = invariant.Split('\n')[0].Split(',').Length;

        using (new CommaDecimalCulture())
        {
            string hostile = ReportWriter.ToCsv(result.Fills);

            Assert.Equal(invariant, hostile);
            Assert.All(
                hostile.Split('\n').Where(l => l.Trim().Length > 0),
                line => Assert.Equal(columns, line.Split(',').Length));
        }
    }

    [Fact]
    public void The_printed_summary_and_the_tearsheet_are_the_same_under_any_culture()
    {
        BacktestResult result = Result();
        string summary = result.Summary();
        string page = TearsheetWriter.ToHtml(result);

        using (new CommaDecimalCulture())
        {
            Assert.Equal(summary, result.Summary());
            Assert.Equal(page, TearsheetWriter.ToHtml(result));
        }
    }

    [Fact]
    public void A_reason_a_venue_behaviour_gives_is_the_same_under_any_culture()
    {
        // This one becomes ModuleChargeReportRow.Reason, so it is not a log line - it is a field of a report.
        string invariant = RolloverReason();

        using (new CommaDecimalCulture())
        {
            Assert.Equal(invariant, RolloverReason());
        }

        Assert.Contains("at 0.0002 for", invariant, StringComparison.Ordinal);
    }

    private static string RolloverReason()
    {
        using SimHarness sim = SimHarness.Perp(new SimOptions
        {
            FeeModel = new FixedFeeModel(Money.Zero(Currencies.USDT)),
            Modules = [new RolloverInterestModule(TimeSpan.FromHours(22), 0.0002m, 0.0002m)],
        });
        sim.Quote(1_000, 50_000.0m, 50_000.0m)
            .At(1_500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote((22 * 60 * 60 * 1_000) + 1_000, 50_000.0m, 50_000.0m)
            .Run();

        return Assert.Single(sim.Exchange.ModuleCharges).Reason;
    }

    [Fact]
    public void A_venue_that_refuses_an_order_gives_the_same_reason_under_any_culture()
    {
        // The rejection text reaches a strategy through OrderRejected and is written into the orders report. The
        // leverage in it is a bare decimal, which is the half a culture can move.
        string invariant = MarginRefusal();

        using (new CommaDecimalCulture())
        {
            Assert.Equal(invariant, MarginRefusal());
        }

        Assert.Contains("12.5x leverage", invariant, StringComparison.Ordinal);
    }

    private static string MarginRefusal()
    {
        using SimHarness sim = SimHarness.Perp(new SimOptions
        {
            AccountType = AccountType.Margin,
            StartingBalances = [new Money(3_000m, Currencies.USDT)],
            DefaultLeverage = 12.5m,
            BypassRisk = true,
        });
        sim.Quote(1_000, 50_000.0m, 50_000.0m, size: 100m)
            .At(1_500, s =>
            {
                s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(49_000.0m)));
                s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(49_000.0m)));
            })
            .Quote(2_000, 50_000.0m, 50_000.0m, size: 100m)
            .Run();

        return sim.Events.OfType<Core.Model.Events.OrderRejected>().First().Reason;
    }

    [Fact]
    public void A_stop_refused_on_arrival_quotes_the_market_the_same_way_under_any_culture()
    {
        // The two sides of the market in this refusal are bare decimals rather than Prices.
        string invariant = StopRefusal();

        using (new CommaDecimalCulture())
        {
            Assert.Equal(invariant, StopRefusal());
        }

        Assert.Contains("bid=100.05, ask=100.15", invariant, StringComparison.Ordinal);
    }

    private static string StopRefusal()
    {
        using SimHarness sim = SimHarness.Spot();
        Core.Model.Orders.StopMarketOrder? order = null;
        sim.Quote(1_000, 100.05m, 100.15m)
            .At(1_500, s => order = s.Submit(s.Orders.StopMarket(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(99.00m))))
            .Quote(2_000, 100.05m, 100.15m)
            .Run();

        return Assert.IsType<Core.Model.Events.OrderRejected>(order!.LastEvent).Reason;
    }

    [Fact]
    public void A_restored_position_that_cannot_be_restored_says_so_the_same_way()
    {
        using SimHarness sim = SimHarness.Perp(new SimOptions { AccountType = AccountType.Margin });

        string message = Assert.Throws<ArgumentException>(
            () => sim.Exchange.RestorePosition(sim.Id, 0m, 1.5m)).Message;

        using (new CommaDecimalCulture())
        {
            Assert.Equal(message, Assert.Throws<ArgumentException>(() => sim.Exchange.RestorePosition(sim.Id, 0m, 1.5m)).Message);
        }

        Assert.Contains("got 0 at 1.5", message, StringComparison.Ordinal);
    }
}
