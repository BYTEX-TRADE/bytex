using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model.Primitives;

namespace Bytex.Backtest.Tests;

// Why (R8.22): the CSVs and result.json are read by programs; the tearsheet is the one file a person opens, and every
// way it can be wrong is quiet. A missing field looks like a complete page. A chart drawn from a stride of the samples
// looks like a chart. A number formatted in the machine's own culture looks right on the machine that wrote it.
//
// So: the page must be well-formed markup, it must reach for nothing outside itself, it must render every field the
// result carries, it must say the same thing twice for the same result, and a section with nothing in it must say so
// rather than vanish - because "no funding was charged" and "funding was never modelled" are different facts and an
// absence cannot tell them apart.
public sealed class TearsheetTests
{
    private static BacktestResult Result()
    {
        using SimHarness sim = StatisticsScenario.Run();
        return sim.Engine.GetResult();
    }

    /// <summary>The page as XML. A doctype is not a DTD to fetch, so it is skipped rather than resolved.</summary>
    private static XDocument Parse(string html)
    {
        XmlReaderSettings settings = new() { DtdProcessing = DtdProcessing.Ignore };
        using StringReader text = new(html);
        using XmlReader reader = XmlReader.Create(text, settings);
        return XDocument.Load(reader);
    }

    [Fact]
    public void The_page_is_well_formed_markup()
    {
        // Not pedantry: everything on this page came from a run - instrument ids, strategy ids, a statistic's name, a
        // module's name - and any of them may carry a character that ends an attribute early. A page that parses is a
        // page where none of them did.
        XDocument page = Parse(TearsheetWriter.ToHtml(Result()));

        Assert.Equal("html", page.Root!.Name.LocalName);
        Assert.Contains(page.Root.Elements(), e => e.Name.LocalName == "head");
        Assert.Contains(page.Root.Elements(), e => e.Name.LocalName == "body");
    }

    [Fact]
    public void Text_out_of_the_run_cannot_break_out_of_the_page()
    {
        // The attack and the accident are one bug. A run id is whatever a caller passed, and a caller that passed
        // a tag would otherwise be writing script into a file somebody opens.
        using SimHarness sim = SimHarness.Spot(new SimOptions { RunId = "x><script>alert</script> & <b>bold" });
        sim.Quote(1000, 100m, 100.1m).Run();

        string html = TearsheetWriter.ToHtml(sim.Engine.GetResult());

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("&amp;", html, StringComparison.Ordinal);
        Parse(html);
    }

    [Fact]
    public void The_page_fetches_nothing_from_anywhere()
    {
        // The promise is one file. A stylesheet, a font or a chart library from elsewhere would make the report render
        // differently next year, render as nothing on a machine with no route out, and tell whoever serves it which
        // strategies somebody is looking at.
        string html = TearsheetWriter.ToHtml(Result());

        foreach (string outside in new[] { "http://", "https://", "<script", "<img", "<iframe", "<link", "@import", "url(" })
        {
            Assert.DoesNotContain(outside, html, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Every_field_of_a_result_reaches_the_page()
    {
        // The guard result.json has, for the same reason: the page is a hand-written projection, so a field added to
        // BacktestResult is missing from it until somebody renders it - and the page still looks complete. Here the
        // link is machine-readable rather than a list kept in a test: every element that renders a field carries
        // data-field naming it, so this cannot be satisfied by editing the test.
        XDocument page = Parse(TearsheetWriter.ToHtml(Result()));

        string[] rendered = [.. page.Descendants()
            .Select(e => e.Attribute("data-field")?.Value)
            .OfType<string>()];
        string[] fields = [.. typeof(BacktestResult).GetProperties().Select(p => p.Name)];

        foreach (string field in fields)
        {
            Assert.Contains(field, rendered, StringComparer.Ordinal);
        }

        // And nothing claims to render a field that does not exist, which is what a rename leaves behind.
        Assert.All(rendered, name => Assert.Contains(name, fields, StringComparer.Ordinal));
    }

    [Fact]
    public void The_same_result_renders_the_same_page()
    {
        // Two runs of one backtest already produce the same fingerprint; a report that then differs - a dictionary in
        // hash order, a set iterated as it happens to come out - makes two identical runs look like two different ones.
        BacktestResult result = Result();

        Assert.Equal(TearsheetWriter.ToHtml(result), TearsheetWriter.ToHtml(result));

        // And two separate runs of the same backtest differ in nothing but what a clock measured. Those three are on
        // the page because a reader wants them; they are what makes this a comparison of everything else.
        Assert.Equal(WithoutTheClock(TearsheetWriter.ToHtml(Result())), WithoutTheClock(TearsheetWriter.ToHtml(Result())));
    }

    private static string WithoutTheClock(string html) => System.Text.RegularExpressions.Regex.Replace(
        html,
        "data-field=\"(Elapsed|RunStarted|RunFinished)\">[^<]*",
        "data-field=\"$1\">",
        System.Text.RegularExpressions.RegexOptions.None,
        TimeSpan.FromSeconds(5));

    [Fact]
    public void A_page_written_where_a_comma_is_the_decimal_point_is_the_same_page()
    {
        // The one place the machine can change what a file says. Every formatter here is invariant, and the chart is
        // why it has to stay that way: a polyline separates its coordinates with commas, so "12,34" as one number
        // becomes two, and the chart on a machine that writes numbers that way is not a wrong shape - it is noise.
        BacktestResult result = Result();
        string expected = TearsheetWriter.ToHtml(result);

        using (new CommaDecimalCulture())
        {
            Assert.Equal(expected, TearsheetWriter.ToHtml(result));
        }
    }

    [Fact]
    public void The_numbers_on_the_page_are_the_numbers_of_the_result()
    {
        BacktestResult result = Result();
        CurrencyStatistics usdt = result.Currencies.Single(c => c.Currency.Equals(Currencies.USDT));

        string html = TearsheetWriter.ToHtml(result);

        // The scenario's four trades come to +4.00 on 1,000,000.00 with 7.00 of fees - the same figures the text
        // summary is held to, so the two reports of one run cannot disagree.
        Assert.Contains("1,000,000.00", html, StringComparison.Ordinal);
        Assert.Contains("+" + usdt.TotalPnl.ToString("N2", CultureInfo.InvariantCulture), html, StringComparison.Ordinal);
        Assert.Contains(usdt.TotalCommissions.ToString("N2", CultureInfo.InvariantCulture), html, StringComparison.Ordinal);
        Assert.Contains("Max drawdown", html, StringComparison.Ordinal);
        Assert.Contains(usdt.MaxDrawdown.ToString("N2", CultureInfo.InvariantCulture), html, StringComparison.Ordinal);
        Assert.Contains(usdt.SharpeRatio.ToString("F2", CultureInfo.InvariantCulture), html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_return_is_printed_as_a_percentage_rather_than_as_the_fraction_it_is_stored_as()
    {
        // The model keeps both conventions and tells them apart by name: a field called ...Percent is already a
        // percentage, a field called ...Return is a fraction. Printing the second as the first understates it a
        // hundredfold and the page shows a rounded zero, which reads as "this trade went nowhere".
        //
        // The scenario's first trade buys one contract at 100 and sells at 110, paying 1 in fees each way: 8 on a
        // notional of 100.
        BacktestResult result = Result();
        PositionReportRow first = result.Positions.OrderBy(p => p.TsOpened.Value).First();

        Assert.Equal(0.08m, first.RealizedReturn);

        string html = TearsheetWriter.ToHtml(result);

        Assert.Contains("+8.00%", html, StringComparison.Ordinal);
        Assert.DoesNotContain("+0.08%", html, StringComparison.Ordinal);

        // And the average of them, which is the figure a reader compares two strategies on.
        Assert.Contains((result.Trades.AverageReturn * 100m).ToString("N2", CultureInfo.InvariantCulture) + "%", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_that_finished_says_so_and_a_run_that_stopped_says_so_first()
    {
        Assert.Contains("No strategy faulted", TearsheetWriter.ToHtml(Result()), StringComparison.Ordinal);

        string html = TearsheetWriter.ToHtml(Rewrite(Result(), faulted: ["Doomed-001"]));

        Assert.Contains("Doomed-001", html, StringComparison.Ordinal);
        Assert.Contains("Stopped.", html, StringComparison.Ordinal);

        // Above the figures, because every one of them is then the figure of a part of a run.
        Assert.True(
            html.IndexOf("Doomed-001", StringComparison.Ordinal) < html.IndexOf("Total P&amp;L", StringComparison.Ordinal),
            "the fault is reported below the numbers it explains");
    }

    [Fact]
    public void An_absent_cost_is_stated_rather_than_left_out()
    {
        // The scenario charges no funding, adds no venue behaviour and is never liquidated. A page that simply omitted
        // those three would leave a reader unable to tell "nothing happened" from "nothing was modelled".
        string html = TearsheetWriter.ToHtml(Result());

        Assert.Contains("No funding payment was made or taken", html, StringComparison.Ordinal);
        Assert.Contains("The run added none", html, StringComparison.Ordinal);
        Assert.Contains("The venue closed nothing itself", html, StringComparison.Ordinal);
    }

    [Fact]
    public void What_was_configured_and_never_applied_is_named_as_that()
    {
        // The gap between the two lists is the fact a reader needs: a venue told to charge funding on a run with no
        // rates charged nothing, and the result looks exactly like one that had nothing to charge.
        BacktestResult result = Result();

        Assert.Contains("funding", result.Simulation, StringComparer.Ordinal);
        Assert.DoesNotContain("funding", result.Applied, StringComparer.Ordinal);

        string html = TearsheetWriter.ToHtml(result);

        Assert.Contains("Configured and never applied:", html, StringComparison.Ordinal);
        Assert.Contains("The run was set up for it and the data gave it nothing to do.", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_curve_longer_than_the_chart_keeps_its_highest_and_lowest_points()
    {
        // The reduction is where a chart lies. Every nth sample is likely to skip exactly the point at the bottom of
        // the drawdown, and the chart would then contradict the maximum drawdown printed above it.
        List<EquityPoint> points = [];
        for (int i = 0; i < 5_000; i++)
        {
            points.Add(new EquityPoint(new UnixNanos(1_000_000_000L * i), 1_000m + (i % 7)));
        }

        points[3_777] = new EquityPoint(points[3_777].Timestamp, 1_500m);
        points[4_111] = new EquityPoint(points[4_111].Timestamp, 500m);

        string html = TearsheetWriter.ToHtml(Rewrite(
            Result(),
            curves: new Dictionary<Currency, IReadOnlyList<EquityPoint>> { [Currencies.USDT] = points }));

        Assert.Contains("Lowest 500.00, highest 1,500.00", html, StringComparison.Ordinal);
        Assert.Contains("5,000 samples", html, StringComparison.Ordinal);
        Assert.Contains("keeping the highest and lowest of each column", html, StringComparison.Ordinal);

        // And the drawn polyline is bounded rather than every point under another name.
        string drawn = Parse(html).Descendants()
            .First(e => e.Name.LocalName == "polyline")
            .Attribute("points")!.Value;

        Assert.True(drawn.Split(' ').Length < 2_000, "the chart drew " + drawn.Split(' ').Length + " points");
    }

    [Fact]
    public void A_flat_run_with_nothing_in_it_still_renders()
    {
        // The page a first-time reader sees most often: a strategy whose conditions never came true. It must not
        // divide by a zero range, and it must not be a blank file.
        using SimHarness sim = SimHarness.Spot();
        sim.Quote(1000, 100m, 100.1m).Run();

        string html = TearsheetWriter.ToHtml(sim.Engine.GetResult());

        Parse(html);
        Assert.Contains("No strategy faulted", html, StringComparison.Ordinal);
        Assert.DoesNotContain("NaN", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Infinity", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_puts_the_page_on_disk_under_the_name_the_report_set_uses()
    {
        BacktestResult result = Result();
        string directory = Path.Combine(Path.GetTempPath(), "bytex-tearsheet-" + Guid.NewGuid().ToString("N"));
        try
        {
            TearsheetWriter.Write(result, Path.Combine(directory, TearsheetWriter.FileName));

            string path = Path.Combine(directory, "tearsheet.html");
            Assert.True(File.Exists(path), "no page was written");
            Assert.Equal(TearsheetWriter.ToHtml(result), File.ReadAllText(path));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>A result with one part replaced, for the two cases a scripted run cannot produce.</summary>
    private static BacktestResult Rewrite(
        BacktestResult r,
        IReadOnlyList<string>? faulted = null,
        IReadOnlyDictionary<Currency, IReadOnlyList<EquityPoint>>? curves = null) => new()
        {
            RunId = r.RunId,
            ModuleHostId = r.ModuleHostId,
            RunStarted = r.RunStarted,
            RunFinished = r.RunFinished,
            BacktestStart = r.BacktestStart,
            BacktestEnd = r.BacktestEnd,
            Elapsed = r.Elapsed,
            Iterations = r.Iterations,
            TotalEvents = r.TotalEvents,
            TotalOrders = r.TotalOrders,
            TotalPositions = r.TotalPositions,
            FaultedStrategies = faulted ?? r.FaultedStrategies,
            Currencies = r.Currencies,
            Trades = r.Trades,
            EquityCurves = curves ?? r.EquityCurves,
            Orders = r.Orders,
            Fills = r.Fills,
            Positions = r.Positions,
            Accounts = r.Accounts,
            Funding = r.Funding,
            Liquidations = r.Liquidations,
            ModuleCharges = r.ModuleCharges,
            Simulation = r.Simulation,
            Applied = r.Applied,
            MarginSources = r.MarginSources,
            Leverages = r.Leverages,
            Participation = r.Participation,
        };
}
