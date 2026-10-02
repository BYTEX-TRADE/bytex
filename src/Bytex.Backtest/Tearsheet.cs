using System.Globalization;
using System.Text;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Backtest;

/// <summary>
/// A run as one page: a self-contained visual report of a <see cref="BacktestResult"/> (R8.22).
///
/// <para>
/// The tables and <c>result.json</c> are written for programs. This is written for the person who has to decide
/// whether a run is worth keeping, and who otherwise opens seven CSVs in a spreadsheet and plots the equity curve by
/// hand. It carries the same numbers - nothing here is computed that the result does not already hold.
/// </para>
///
/// <para>
/// <b>Self-contained is the whole point.</b> One file, no script, no stylesheet, no font and no image fetched from
/// anywhere: the charts are SVG this writer draws from the result's own points. A report that needs the network is a
/// report that renders differently next year, renders nothing on a machine without a route out, and tells whoever
/// hosts that route which strategies somebody is looking at. It is also why there is no new dependency behind this:
/// a charting library would decide what a result looks like, and it would have to be trusted with the file.
/// </para>
///
/// <para>
/// <b>It says what the run accounts for, not only what it earned.</b> A number produced by a simulator that did not
/// bound fills, never charged funding and could not liquidate is a different number from the same figure produced by
/// one that did all three, and a page that shows only the figure invites the reader to assume the second. So the
/// modelled and applied capabilities, where each margin requirement came from, the leverage that was asked for beside
/// the leverage that was granted, and what bounding fills came to are all on the page - and a section with nothing in
/// it says so rather than being left out, because "no funding was charged" and "funding was never modelled" look
/// identical when the answer is an absence.
/// </para>
///
/// <para>
/// The same result renders the same page, byte for byte: nothing here reads the clock or the machine. Every element
/// that renders a field of the result carries <c>data-field</c> naming it, which is what a test uses to hold this to
/// the whole result rather than to whatever somebody remembered to render.
/// </para>
/// </summary>
public static class TearsheetWriter
{
    /// <summary>The name <see cref="ReportWriter.WriteAll"/> writes it under.</summary>
    public const string FileName = "tearsheet.html";

    /// <summary>At most this many equity samples are drawn per chart; see <see cref="Reduce"/> for what is kept.</summary>
    private const int ChartColumns = 400;

    /// <summary>At most this many closed trades get a bar of their own, and at most this many rows are tabled.</summary>
    private const int MaxBars = 500;
    private const int MaxRows = 25;

    private const int ChartWidth = 1000;
    private const int ChartHeight = 260;
    private const int UnderwaterHeight = 120;

    /// <summary>The page, as text.</summary>
    public static string ToHtml(BacktestResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        StringBuilder sb = new();
        sb.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\" />\n");
        sb.Append("<title>Backtest ").Append(E(result.RunId)).Append("</title>\n<style>\n").Append(Css).Append("</style>\n</head>\n<body>\n");

        Header(sb, result);
        Faults(sb, result);
        Currencies(sb, result);
        Charts(sb, result);
        Trades(sb, result);
        Accounted(sb, result);
        Costs(sb, result);
        Tables(sb, result);

        sb.Append("</body>\n</html>\n");
        return sb.ToString();
    }

    /// <summary>The page, on disk. The directory is created if it is not there.</summary>
    public static void Write(BacktestResult result, string path)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, ToHtml(result), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    // ----- sections -----

    private static void Header(StringBuilder sb, BacktestResult r)
    {
        sb.Append("<header>\n<h1 data-field=\"RunId\">").Append(E(r.RunId)).Append("</h1>\n<p class=\"sub\">");
        sb.Append("<span data-field=\"ModuleHostId\">").Append(E(r.ModuleHostId.Value)).Append("</span>");
        sb.Append(" · <span data-field=\"BacktestStart\">").Append(Time(r.BacktestStart)).Append("</span>");
        sb.Append(" to <span data-field=\"BacktestEnd\">").Append(Time(r.BacktestEnd)).Append("</span>");
        sb.Append("</p>\n<p class=\"sub\">");
        sb.Append("<span data-field=\"Iterations\">").Append(r.Iterations.ToString("N0", CultureInfo.InvariantCulture)).Append(" iterations</span>");
        sb.Append(" · <span data-field=\"TotalEvents\">").Append(r.TotalEvents.ToString("N0", CultureInfo.InvariantCulture)).Append(" events</span>");
        sb.Append(" · <span data-field=\"TotalOrders\">").Append(r.TotalOrders.ToString("N0", CultureInfo.InvariantCulture)).Append(" orders</span>");
        sb.Append(" · <span data-field=\"TotalPositions\">").Append(r.TotalPositions.ToString("N0", CultureInfo.InvariantCulture)).Append(" positions</span>");
        sb.Append(" · <span data-field=\"Elapsed\">ran in ").Append(r.Elapsed.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture)).Append("s</span>");
        sb.Append(" · <span data-field=\"RunStarted\">").Append(Time(r.RunStarted)).Append("</span>");
        sb.Append(" to <span data-field=\"RunFinished\">").Append(Time(r.RunFinished)).Append("</span>");
        sb.Append("</p>\n</header>\n");
    }

    private static void Faults(StringBuilder sb, BacktestResult r)
    {
        // Above every number, because if a strategy stopped then every number below it is the number of a part of a
        // run. An absence is stated for the same reason: a reader has to be able to tell a run that finished from one
        // whose page simply does not mention it.
        if (r.FaultedStrategies.Count == 0)
        {
            sb.Append("<p class=\"note ok\" data-field=\"FaultedStrategies\">No strategy faulted: the run finished as written.</p>\n");
            return;
        }

        sb.Append("<p class=\"note bad\" data-field=\"FaultedStrategies\"><strong>Stopped.</strong> ")
          .Append(E(string.Join(", ", r.FaultedStrategies)))
          .Append(" faulted during the run, so the figures below are those of however much of it happened first. The log says why.</p>\n");
    }

    private static void Currencies(StringBuilder sb, BacktestResult r)
    {
        sb.Append("<section data-field=\"Currencies\">\n<h2>Result</h2>\n");
        if (r.Currencies.Count == 0)
        {
            sb.Append("<p class=\"note\">No currency was touched: nothing was funded and nothing traded.</p>\n</section>\n");
            return;
        }

        foreach (CurrencyStatistics c in Ordered(r))
        {
            sb.Append("<div class=\"cards\">\n");
            sb.Append("<h3>").Append(E(c.Currency.Code)).Append("</h3>\n");
            Card(sb, "Total P&amp;L", Signed(c.TotalPnl), c.TotalPnl);
            Card(sb, "Return", Signed(c.ReturnPercent) + "%", c.ReturnPercent);
            Card(sb, "Start", N(c.StartingBalance), null);
            Card(sb, "End", N(c.EndingBalance), null);
            Card(sb, "Realised", Signed(c.RealizedPnl), c.RealizedPnl);
            Card(sb, "Unrealised", Signed(c.UnrealizedPnl), c.UnrealizedPnl);
            Card(sb, "Fees", N(c.TotalCommissions), null);
            Card(sb, "Max drawdown", N(c.MaxDrawdown) + " (" + N(c.MaxDrawdownPercent) + "%)", null);
            Card(sb, "Sharpe", F(c.SharpeRatio), null);
            Card(sb, "Sortino", F(c.SortinoRatio), null);

            // No losing trade leaves the ratio undefined rather than enormous: a page showing a 29-digit number would
            // be showing decimal.MaxValue and calling it a result.
            Card(sb, "Profit factor", c.ProfitFactor is { } pf ? N(pf) : "undefined (no losing trade)", null);
            Card(sb, "Expectancy", Signed(c.Expectancy), c.Expectancy);
            foreach ((string name, decimal? value) in c.Custom.OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                // A figure that was asked for and could not be worked out shows as what it is, not as a zero somebody
                // might act on.
                Card(sb, name, value is { } v ? v.ToString("N4", CultureInfo.InvariantCulture) : "not available", null);
            }

            sb.Append("</div>\n");
        }

        sb.Append("</section>\n");
    }

    private static void Charts(StringBuilder sb, BacktestResult r)
    {
        sb.Append("<section data-field=\"EquityCurves\">\n<h2>Equity</h2>\n");
        if (r.EquityCurves.Count == 0)
        {
            sb.Append("<p class=\"note\">No equity was sampled: the run held nothing and traded nothing.</p>\n</section>\n");
            return;
        }

        foreach ((Currency currency, IReadOnlyList<EquityPoint> curve) in r.EquityCurves.OrderBy(kv => kv.Key.Code, StringComparer.Ordinal))
        {
            sb.Append("<h3>").Append(E(currency.Code)).Append("</h3>\n");
            if (curve.Count == 0)
            {
                sb.Append("<p class=\"note\">No points.</p>\n");
                continue;
            }

            IReadOnlyList<EquityPoint> drawn = Reduce(curve);
            Equity(sb, drawn);
            Underwater(sb, drawn);
            sb.Append("<p class=\"caption\">")
              .Append(curve.Count.ToString("N0", CultureInfo.InvariantCulture))
              .Append(curve.Count == 1 ? " sample" : " samples");
            if (drawn.Count < curve.Count)
            {
                // Said out loud, because a chart drawn from fewer points than the run produced is a chart somebody
                // might measure a drawdown off. The reduction keeps every bucket's extremes, so the depth below is
                // the real depth - but the reader is told all the same.
                sb.Append(", drawn as ").Append(drawn.Count.ToString("N0", CultureInfo.InvariantCulture))
                  .Append(" keeping the highest and lowest of each column");
            }

            sb.Append(". The lower chart is how far below the running peak the account was.</p>\n");
            Trades(sb, r, currency);
        }

        sb.Append("</section>\n");
    }

    private static void Trades(StringBuilder sb, BacktestResult r)
    {
        TradeStatistics t = r.Trades;
        sb.Append("<section data-field=\"Trades\">\n<h2>Trades</h2>\n<table>\n<tbody>\n");
        Row(sb, "Positions", t.TotalPositions.ToString("N0", CultureInfo.InvariantCulture)
            + " (" + t.ClosedPositions.ToString("N0", CultureInfo.InvariantCulture) + " closed, "
            + t.OpenPositions.ToString("N0", CultureInfo.InvariantCulture) + " still open)");
        Row(sb, "Long / short", t.LongPositions.ToString("N0", CultureInfo.InvariantCulture) + " / " + t.ShortPositions.ToString("N0", CultureInfo.InvariantCulture));
        Row(sb, "Win rate", N(t.WinRate * Scales.Percent) + "% (" + t.Winners.ToString("N0", CultureInfo.InvariantCulture) + " won, "
            + t.Losers.ToString("N0", CultureInfo.InvariantCulture) + " lost, " + t.Breakeven.ToString("N0", CultureInfo.InvariantCulture) + " flat)");
        Row(sb, "Average winner / loser", Signed(t.AverageWinner) + " / " + Signed(t.AverageLoser));
        Row(sb, "Largest winner / loser", Signed(t.LargestWinner) + " / " + Signed(t.LargestLoser));
        // A fraction in the model, like every other ...Return: only the fields named ...Percent are already percentages,
        // and printing one as the other is a hundredfold understatement that looks like a rounded zero.
        Row(sb, "Average return", Signed(t.AverageReturn * Scales.Percent) + "%");
        Row(sb, "Average duration", t.AverageDuration.ToString(null, CultureInfo.InvariantCulture));
        sb.Append("</tbody>\n</table>\n</section>\n");
    }

    /// <summary>One bar per closed trade in this currency, in the order they closed.</summary>
    private static void Trades(StringBuilder sb, BacktestResult r, Currency currency)
    {
        List<PositionReportRow> closed = [.. r.Positions
            .Where(p => p.TsClosed is not null && p.RealizedPnl.Currency.Equals(currency))
            .OrderBy(p => p.TsClosed!.Value.Value)];

        if (closed.Count == 0)
        {
            return;
        }

        List<PositionReportRow> drawn = closed.Count > MaxBars ? [.. closed.Take(MaxBars)] : closed;
        decimal peak = drawn.Max(p => Math.Abs(p.RealizedPnl.Amount));
        sb.Append("<svg class=\"chart bars\" viewBox=\"0 0 ").Append(ChartWidth).Append(' ').Append(UnderwaterHeight)
          .Append("\" preserveAspectRatio=\"none\" role=\"img\"><title>Profit and loss of each closed trade in ")
          .Append(E(currency.Code)).Append("</title>\n");
        sb.Append("<line class=\"axis\" x1=\"0\" y1=\"").Append(UnderwaterHeight / 2).Append("\" x2=\"").Append(ChartWidth)
          .Append("\" y2=\"").Append(UnderwaterHeight / 2).Append("\" />\n");

        double width = (double)ChartWidth / drawn.Count;
        for (int i = 0; i < drawn.Count; i++)
        {
            decimal pnl = drawn[i].RealizedPnl.Amount;
            double height = peak == 0m ? 0d : Math.Abs((double)(pnl / peak)) * (UnderwaterHeight / 2d - 2d);
            double x = i * width;
            double y = pnl >= 0m ? UnderwaterHeight / 2d - height : UnderwaterHeight / 2d;
            sb.Append("<rect class=\"").Append(pnl >= 0m ? "up" : "down").Append("\" x=\"").Append(Num(x))
              .Append("\" y=\"").Append(Num(y)).Append("\" width=\"").Append(Num(Math.Max(width - 0.4d, 0.2d)))
              .Append("\" height=\"").Append(Num(Math.Max(height, 0.4d))).Append("\" />\n");
        }

        sb.Append("</svg>\n<p class=\"caption\">Each closed trade in ").Append(E(currency.Code)).Append(", in the order it closed");
        if (drawn.Count < closed.Count)
        {
            sb.Append(" - the first ").Append(drawn.Count.ToString("N0", CultureInfo.InvariantCulture))
              .Append(" of ").Append(closed.Count.ToString("N0", CultureInfo.InvariantCulture));
        }

        sb.Append(". The scale is the largest of them, ").Append(N(peak)).Append(".</p>\n");
    }

    private static void Accounted(StringBuilder sb, BacktestResult r)
    {
        sb.Append("<section>\n<h2>What these numbers account for</h2>\n");

        List<string> applied = [.. r.Applied.OrderBy(s => s, StringComparer.Ordinal)];
        List<string> configured = [.. r.Simulation.Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal)];
        List<string> never = [.. configured.Except(applied, StringComparer.Ordinal)];

        sb.Append("<p data-field=\"Applied\"><strong>Applied.</strong> ")
          .Append(applied.Count == 0 ? "Nothing beyond plain matching: no fill was bounded, no funding charged, no position liquidated." : E(string.Join(", ", applied)))
          .Append("</p>\n");
        sb.Append("<p data-field=\"Simulation\"><strong>Modelled.</strong> ")
          .Append(configured.Count == 0 ? "The venues were configured for plain matching only." : E(string.Join(", ", configured)))
          .Append("</p>\n");
        if (never.Count > 0)
        {
            // The gap between the two is the interesting part: a venue told to charge funding on a run with no rates
            // in it charged nothing, and the result looks exactly like one that had nothing to charge.
            sb.Append("<p class=\"note\"><strong>Configured and never applied:</strong> ").Append(E(string.Join(", ", never)))
              .Append(". The run was set up for it and the data gave it nothing to do.</p>\n");
        }

        Participation(sb, r);
        Margin(sb, r);
        Leverage(sb, r);
        sb.Append("</section>\n");
    }

    private static void Participation(StringBuilder sb, BacktestResult r)
    {
        ParticipationSummary p = r.Participation;
        sb.Append("<p data-field=\"Participation\"><strong>Fills against the size on offer.</strong> ");
        if (p.BarVolumeShare is { } share)
        {
            sb.Append("One participant was allowed ").Append(N(share * Scales.Percent)).Append("% of a bar's volume. ");
        }
        else
        {
            sb.Append("No venue was told to bound a fill by a bar's volume. ");
        }

        sb.Append(p.BoundedFills == 0
            ? "No fill was held back by what was on offer."
            : p.BoundedFills.ToString("N0", CultureInfo.InvariantCulture) + " fills were bounded, "
              + N(p.BoundedQuantity, 6) + " of quantity went in under a bound.");
        sb.Append("</p>\n");
    }

    private static void Margin(StringBuilder sb, BacktestResult r)
    {
        sb.Append("<p data-field=\"MarginSources\"><strong>Where the margin requirement came from.</strong> ");
        if (r.MarginSources.Count == 0)
        {
            sb.Append("The run held no position, so nothing posted margin.</p>\n");
            return;
        }

        sb.Append(E(string.Join(", ", r.MarginSources.Select(s => s.ToString()).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal))));
        sb.Append('.');
        if (r.MarginSources.Contains(MarginSource.Unrecorded))
        {
            // The one value that explains a disagreement between two reports on the same strategy and period.
            sb.Append(" <em>Unrecorded</em> means the catalog entry predates the marker: the requirement behind these numbers"
                + " is whatever that entry held, which is why a fresh run of the same strategy can disagree with this page.");
        }

        if (r.MarginSources.Contains(MarginSource.AdapterDefault))
        {
            sb.Append(" <em>AdapterDefault</em> means the venue published nothing usable and the engine substituted a figure of its own,"
                + " so a liquidation here happened at a price nobody measured.");
        }

        if (r.MarginSources.Contains(MarginSource.VenueWideDefault))
        {
            sb.Append(" <em>VenueWideDefault</em> means the venue published one figure for every contract rather than for this one:"
                + " sourced, and not the same as right.");
        }

        if (r.MarginSources.Contains(MarginSource.VenueSilent))
        {
            sb.Append(" <em>VenueSilent</em> means the venue publishes no margin for a product it really does margin, so a position"
                + " in it posted nothing here and a liquidation could not fire on the requirement.");
        }

        sb.Append("</p>\n");
    }

    private static void Leverage(StringBuilder sb, BacktestResult r)
    {
        sb.Append("<div data-field=\"Leverages\">\n");
        if (r.Leverages.Count == 0)
        {
            sb.Append("<p><strong>Leverage.</strong> Nothing was leveraged: every order was for an instrument that borrows nothing.</p>\n</div>\n");
            return;
        }

        sb.Append("<p><strong>Leverage: asked for, and granted.</strong> A backtest is not refused when it asks for more than the"
            + " instrument allows - it is clamped, and a strategy written for one figure is then measured at another.</p>\n");
        sb.Append("<table>\n<thead>\n<tr><th>Instrument</th><th>Asked</th><th>Granted</th><th>Initial margin</th><th>Model</th><th>Source</th></tr>\n</thead>\n<tbody>\n");
        foreach (LeverageReportRow row in r.Leverages.OrderBy(l => l.MarketKey.ToString(), StringComparer.Ordinal))
        {
            sb.Append(row.Capped ? "<tr class=\"bad\">" : "<tr>");
            sb.Append("<td>").Append(E(row.MarketKey.ToString())).Append("</td>");
            sb.Append("<td>").Append(N(row.Requested)).Append("x</td>");
            sb.Append("<td>").Append(N(row.Applied)).Append(row.Capped ? "x (clamped)" : "x").Append("</td>");
            sb.Append("<td>").Append(row.MarginInit.ToString("0.######", CultureInfo.InvariantCulture)).Append("</td>");
            sb.Append("<td>").Append(E(row.MarginModel)).Append("</td>");
            sb.Append("<td>").Append(E(row.MarginSource.ToString())).Append("</td></tr>\n");
        }

        sb.Append("</tbody>\n</table>\n</div>\n");
    }

    private static void Costs(StringBuilder sb, BacktestResult r)
    {
        sb.Append("<section>\n<h2>What the venue took</h2>\n");

        sb.Append("<p data-field=\"Funding\"><strong>Funding.</strong> ");
        if (r.Funding.Count == 0)
        {
            sb.Append("No funding payment was made or taken - either nothing perpetual was held, or the run was given no rates.");
        }
        else
        {
            sb.Append(r.Funding.Count.ToString("N0", CultureInfo.InvariantCulture)).Append(" payments: ");
            sb.Append(E(string.Join(", ", r.Funding
                .GroupBy(f => f.Currency.Code, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => Signed(g.Sum(f => f.Amount)) + " " + g.Key))));
            sb.Append(" (negative is what the account paid).");
        }

        sb.Append("</p>\n");

        sb.Append("<p data-field=\"ModuleCharges\"><strong>Venue behaviours.</strong> ");
        if (r.ModuleCharges.Count == 0)
        {
            sb.Append("The run added none, so nothing was charged by one.");
        }
        else
        {
            IEnumerable<string> byModule = r.ModuleCharges
                .GroupBy(m => m.Module, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => g.Key + ": " + string.Join(", ", g
                    .GroupBy(m => m.Currency.Code, StringComparer.Ordinal)
                    .OrderBy(c => c.Key, StringComparer.Ordinal)
                    .Select(c => Signed(c.Sum(m => m.Amount)) + " " + c.Key))
                    + " over " + g.Count().ToString("N0", CultureInfo.InvariantCulture) + " charges");

            sb.Append(E(string.Join("; ", byModule)));
            sb.Append(". A result smaller than expected may be this rather than the strategy.");
        }

        sb.Append("</p>\n");

        sb.Append("<p data-field=\"Liquidations\"><strong>Liquidations.</strong> ");
        if (r.Liquidations.Count == 0)
        {
            sb.Append("The venue closed nothing itself.");
        }
        else
        {
            sb.Append(r.Liquidations.Count.ToString("N0", CultureInfo.InvariantCulture))
              .Append(" positions were closed by the venue for want of margin - trades the strategy did not choose to end: ");
            sb.Append(E(string.Join(", ", r.Liquidations
                .GroupBy(l => l.MarketKey.ToString(), StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => g.Key + " (" + g.Count().ToString(CultureInfo.InvariantCulture) + ")"))));
            sb.Append('.');
        }

        sb.Append("</p>\n</section>\n");
    }

    private static void Tables(StringBuilder sb, BacktestResult r)
    {
        sb.Append("<section>\n<h2>The run in rows</h2>\n");

        sb.Append("<p data-field=\"Orders\"><strong>Orders.</strong> ").Append(r.Orders.Count.ToString("N0", CultureInfo.InvariantCulture));
        if (r.Orders.Count > 0)
        {
            sb.Append(": ").Append(E(string.Join(", ", r.Orders
                .GroupBy(o => o.Status.ToString(), StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => g.Count().ToString("N0", CultureInfo.InvariantCulture) + " " + g.Key.ToLowerInvariant()))));
            sb.Append(". Slippage against their own reference price came to ")
              .Append(Signed(r.Orders.Sum(o => o.Slippage))).Append('.');
        }

        sb.Append(" The whole table is in <code>orders.csv</code>.</p>\n");

        sb.Append("<p data-field=\"Fills\"><strong>Fills.</strong> ").Append(r.Fills.Count.ToString("N0", CultureInfo.InvariantCulture));
        if (r.Fills.Count > 0)
        {
            sb.Append(": ").Append(E(string.Join(", ", r.Fills
                .GroupBy(f => f.LiquiditySide.ToString(), StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => g.Count().ToString("N0", CultureInfo.InvariantCulture) + " " + g.Key.ToLowerInvariant()))));
            sb.Append('.');
        }

        sb.Append(" The whole table is in <code>fills.csv</code>.</p>\n");

        sb.Append("<div data-field=\"Accounts\">\n<h3>Balances at the end</h3>\n");
        if (r.Accounts.Count == 0)
        {
            sb.Append("<p class=\"note\">No account was reported.</p>\n");
        }
        else
        {
            // Every balance the account ever held is in here, one row per event: tabling them all put twenty-three
            // thousand pixels of history where a reader was looking for what they ended with. The last state of each
            // account and currency is the balance sheet; the history is a file away.
            List<AccountReportRow> ending = [.. r.Accounts
                .GroupBy(a => (a.AccountId.Value, a.Currency.Code))
                .Select(g => g.MaxBy(a => a.Timestamp.Value)!)
                .OrderBy(a => a.AccountId.Value, StringComparer.Ordinal)
                .ThenBy(a => a.Currency.Code, StringComparer.Ordinal)];

            sb.Append("<table>\n<thead>\n<tr><th>Account</th><th>Currency</th><th>Total</th><th>Locked</th><th>Free</th></tr>\n</thead>\n<tbody>\n");
            foreach (AccountReportRow row in ending)
            {
                sb.Append("<tr><td>").Append(E(row.AccountId.Value)).Append("</td><td>").Append(E(row.Currency.Code))
                  .Append("</td><td>").Append(N(row.Total)).Append("</td><td>").Append(N(row.Locked))
                  .Append("</td><td>").Append(N(row.Free)).Append("</td></tr>\n");
            }

            sb.Append("</tbody>\n</table>\n<p class=\"caption\">The last of ")
              .Append(r.Accounts.Count.ToString("N0", CultureInfo.InvariantCulture))
              .Append(" balance states the run recorded; every one of them is in <code>accounts.csv</code>.</p>\n");
        }

        sb.Append("</div>\n");

        sb.Append("<div data-field=\"Positions\">\n<h3>Positions</h3>\n");
        if (r.Positions.Count == 0)
        {
            sb.Append("<p class=\"note\">The run opened none.</p>\n");
        }
        else
        {
            List<PositionReportRow> rows = [.. r.Positions.OrderBy(p => p.TsOpened.Value)];
            // The last of them rather than the first: somebody scanning a report wants where the strategy ended up.
            List<PositionReportRow> shown = rows.Count > MaxRows ? [.. rows.TakeLast(MaxRows)] : rows;
            sb.Append("<table>\n<thead>\n<tr><th>Opened</th><th>Instrument</th><th>Side</th><th>Quantity</th><th>Entry</th><th>Exit</th><th>P&amp;L</th><th>Return</th><th>Held</th></tr>\n</thead>\n<tbody>\n");
            foreach (PositionReportRow p in shown)
            {
                decimal pnl = p.RealizedPnl.Amount + (p.UnrealizedPnl?.Amount ?? 0m);
                sb.Append(pnl > 0m ? "<tr class=\"up\">" : pnl < 0m ? "<tr class=\"down\">" : "<tr>");
                sb.Append("<td>").Append(Time(p.TsOpened)).Append("</td>");
                sb.Append("<td>").Append(E(p.MarketKey.ToString())).Append("</td>");
                sb.Append("<td>").Append(E(p.EntrySide.ToString())).Append("</td>");
                sb.Append("<td>").Append(N(p.PeakQuantity.Value, 6)).Append("</td>");
                sb.Append("<td>").Append(N(p.AvgPxOpen)).Append("</td>");
                sb.Append("<td>").Append(p.AvgPxClose is { } exit ? N(exit) : "open").Append("</td>");
                sb.Append("<td>").Append(Signed(pnl)).Append(' ').Append(E(p.RealizedPnl.Currency.Code)).Append("</td>");
                sb.Append("<td>").Append(Signed(p.RealizedReturn * Scales.Percent)).Append("%</td>");
                sb.Append("<td>").Append(p.Duration is { } held ? E(held.ToString(null, CultureInfo.InvariantCulture)) : "open").Append("</td></tr>\n");
            }

            sb.Append("</tbody>\n</table>\n");
            if (shown.Count < rows.Count)
            {
                sb.Append("<p class=\"caption\">The last ").Append(shown.Count.ToString("N0", CultureInfo.InvariantCulture))
                  .Append(" of ").Append(rows.Count.ToString("N0", CultureInfo.InvariantCulture))
                  .Append("; the whole table is in <code>positions.csv</code>.</p>\n");
            }
        }

        sb.Append("</div>\n</section>\n");
    }

    // ----- charts -----

    private static void Equity(StringBuilder sb, IReadOnlyList<EquityPoint> points)
    {
        decimal low = points.Min(p => p.Equity);
        decimal high = points.Max(p => p.Equity);
        sb.Append("<svg class=\"chart\" viewBox=\"0 0 ").Append(ChartWidth).Append(' ').Append(ChartHeight)
          .Append("\" preserveAspectRatio=\"none\" role=\"img\"><title>Equity</title>\n");
        Grid(sb, ChartHeight);
        sb.Append("<polyline class=\"line\" points=\"");
        Points(sb, points, low, high, ChartHeight, p => p.Equity);
        sb.Append("\" />\n</svg>\n");
        sb.Append("<p class=\"caption\">Lowest ").Append(N(low)).Append(", highest ").Append(N(high)).Append(".</p>\n");
    }

    private static void Underwater(StringBuilder sb, IReadOnlyList<EquityPoint> points)
    {
        // Drawn from the same reduced points as the curve above, so the two charts agree with each other.
        List<decimal> under = new(points.Count);
        decimal peak = points[0].Equity;
        foreach (EquityPoint point in points)
        {
            peak = Math.Max(peak, point.Equity);
            under.Add(peak == 0m ? 0m : (point.Equity - peak) / Math.Abs(peak) * Scales.Percent);
        }

        decimal worst = under.Min();
        if (worst == 0m)
        {
            sb.Append("<p class=\"caption\">Never below its running peak.</p>\n");
            return;
        }

        sb.Append("<svg class=\"chart underwater\" viewBox=\"0 0 ").Append(ChartWidth).Append(' ').Append(UnderwaterHeight)
          .Append("\" preserveAspectRatio=\"none\" role=\"img\"><title>Distance below the running peak, in percent</title>\n");
        sb.Append("<polyline class=\"line\" points=\"");
        for (int i = 0; i < points.Count; i++)
        {
            double x = X(points, i);
            double y = worst == 0m ? 1d : (double)(under[i] / worst) * (UnderwaterHeight - 2d);
            sb.Append(Num(x)).Append(',').Append(Num(y)).Append(i == points.Count - 1 ? string.Empty : " ");
        }

        sb.Append("\" />\n</svg>\n");
    }

    private static void Grid(StringBuilder sb, int height)
    {
        for (int i = 1; i < 4; i++)
        {
            int y = height * i / 4;
            sb.Append("<line class=\"grid\" x1=\"0\" y1=\"").Append(y).Append("\" x2=\"").Append(ChartWidth).Append("\" y2=\"").Append(y).Append("\" />\n");
        }
    }

    private static void Points(StringBuilder sb, IReadOnlyList<EquityPoint> points, decimal low, decimal high, int height, Func<EquityPoint, decimal> value)
    {
        decimal span = high - low;
        for (int i = 0; i < points.Count; i++)
        {
            double y = span == 0m
                ? height / 2d
                : (double)((high - value(points[i])) / span) * (height - 4d) + 2d;
            sb.Append(Num(X(points, i))).Append(',').Append(Num(y)).Append(i == points.Count - 1 ? string.Empty : " ");
        }
    }

    /// <summary>The x of a point, spaced by its timestamp rather than by its position, so a gap in the data looks like one.</summary>
    private static double X(IReadOnlyList<EquityPoint> points, int index)
    {
        long first = points[0].Timestamp.Value;
        long last = points[^1].Timestamp.Value;
        return last == first
            ? (points.Count == 1 ? ChartWidth / 2d : (double)index / (points.Count - 1) * ChartWidth)
            : (double)(points[index].Timestamp.Value - first) / (last - first) * ChartWidth;
    }

    /// <summary>
    /// At most <see cref="ChartColumns"/> columns of points, each column keeping its first, last, highest and lowest
    /// sample in the order they happened.
    ///
    /// <para>
    /// Every point of a million-sample run would be a polyline no browser draws and nobody can read. Taking every nth
    /// sample instead would be the version that lies: the sample at the bottom of the drawdown is exactly the one a
    /// stride is likely to skip, and the chart would then disagree with the maximum drawdown printed above it.
    /// </para>
    /// </summary>
    private static IReadOnlyList<EquityPoint> Reduce(IReadOnlyList<EquityPoint> points)
    {
        if (points.Count <= ChartColumns)
        {
            return points;
        }

        List<EquityPoint> reduced = new(ChartColumns * 4);
        for (int column = 0; column < ChartColumns; column++)
        {
            int from = (int)((long)column * points.Count / ChartColumns);
            int to = (int)((long)(column + 1) * points.Count / ChartColumns);
            if (to <= from)
            {
                continue;
            }

            int lowest = from;
            int highest = from;
            for (int i = from + 1; i < to; i++)
            {
                if (points[i].Equity < points[lowest].Equity)
                {
                    lowest = i;
                }

                if (points[i].Equity > points[highest].Equity)
                {
                    highest = i;
                }
            }

            foreach (int index in new[] { from, lowest, highest, to - 1 }.Distinct().Order())
            {
                reduced.Add(points[index]);
            }
        }

        return reduced;
    }

    // ----- pieces -----

    /// <summary>
    /// The currency the run actually traded first, then the rest by code. An account funded in two and trading one
    /// would otherwise open with a column of zeroes, with the figures somebody came for below the fold.
    /// </summary>
    private static IEnumerable<CurrencyStatistics> Ordered(BacktestResult r) =>
        r.Currencies
            .OrderByDescending(c => c.TotalPnl != 0m || c.TotalCommissions != 0m)
            .ThenBy(c => c.Currency.Code, StringComparer.Ordinal);

    private static void Card(StringBuilder sb, string label, string value, decimal? sign)
    {
        string cls = sign is null ? "card" : sign > 0m ? "card up" : sign < 0m ? "card down" : "card";
        sb.Append("<div class=\"").Append(cls).Append("\"><span class=\"label\">").Append(label)
          .Append("</span><span class=\"value\">").Append(value).Append("</span></div>\n");
    }

    private static void Row(StringBuilder sb, string label, string value) =>
        sb.Append("<tr><th>").Append(label).Append("</th><td>").Append(value).Append("</td></tr>\n");

    private static string N(decimal value, int places = 2) =>
        value.ToString("N" + places.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    private static string Signed(decimal value) => (value > 0m ? "+" : string.Empty) + N(value);

    private static string F(double value) =>
        double.IsFinite(value) ? value.ToString("F2", CultureInfo.InvariantCulture) : "not available";

    private static string Num(double value) => Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);

    private static string Time(UnixNanos? ts) => ts is { } t
        ? t.ToDateTimeUtc().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "Z"
        : "not stated";

    /// <summary>
    /// The five characters that would otherwise make the page something other than what it says. Everything written
    /// into the page goes through here or through a number formatter; nothing is interpolated raw.
    /// </summary>
    private static string E(string? text) => string.IsNullOrEmpty(text)
        ? string.Empty
        : text.Replace("&", "&amp;", StringComparison.Ordinal)
              .Replace("<", "&lt;", StringComparison.Ordinal)
              .Replace(">", "&gt;", StringComparison.Ordinal)
              .Replace("\"", "&quot;", StringComparison.Ordinal)
              .Replace("'", "&apos;", StringComparison.Ordinal);

    private const string Css = """
        :root { color-scheme: light dark; }
        body { font-family: -apple-system, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif;
               margin: 0 auto; max-width: 1100px; padding: 2rem 1.25rem 4rem; line-height: 1.5;
               color: #1c1f24; background: #fff; }
        h1 { font-size: 1.6rem; margin: 0 0 0.25rem; letter-spacing: -0.01em; }
        h2 { font-size: 1.1rem; margin: 2.5rem 0 0.75rem; padding-bottom: 0.35rem; border-bottom: 1px solid #e3e6ea; }
        h3 { font-size: 0.95rem; margin: 1.5rem 0 0.5rem; color: #4a5260; }
        p { margin: 0.5rem 0; }
        .sub { color: #6b7280; font-size: 0.85rem; margin: 0.15rem 0; }
        .note { padding: 0.6rem 0.8rem; border-radius: 6px; background: #f4f6f8; font-size: 0.88rem; }
        .note.ok { background: #f1f7f2; color: #2c5233; }
        .note.bad { background: #fdf2f2; color: #8a2020; }
        .cards { display: grid; grid-template-columns: repeat(auto-fill, minmax(9rem, 1fr)); gap: 0.5rem; margin-bottom: 0.5rem; }
        .cards h3 { grid-column: 1 / -1; margin: 1rem 0 0.25rem; }
        .card { padding: 0.6rem 0.7rem; border: 1px solid #e3e6ea; border-radius: 6px; }
        .card .label { display: block; font-size: 0.7rem; text-transform: uppercase; letter-spacing: 0.04em; color: #6b7280; }
        .card .value { display: block; font-size: 1.05rem; font-variant-numeric: tabular-nums; margin-top: 0.15rem; }
        .card.up .value { color: #1c6b34; }
        .card.down .value { color: #a32020; }
        table { border-collapse: collapse; width: 100%; font-size: 0.85rem; font-variant-numeric: tabular-nums; }
        th, td { text-align: left; padding: 0.3rem 0.5rem; border-bottom: 1px solid #eef0f3; }
        thead th { font-size: 0.7rem; text-transform: uppercase; letter-spacing: 0.04em; color: #6b7280; }
        tbody th { font-weight: 500; color: #4a5260; width: 14rem; }
        tr.up td { color: #1c6b34; }
        tr.down td { color: #a32020; }
        tr.bad td { background: #fdf2f2; }
        .chart { display: block; width: 100%; height: 260px; background: #fbfcfd; border: 1px solid #e3e6ea; border-radius: 6px; }
        .chart.underwater, .chart.bars { height: 120px; margin-top: 0.4rem; }
        .chart .line { fill: none; stroke: #2a6ebb; stroke-width: 1.4; vector-effect: non-scaling-stroke; }
        .chart.underwater .line { stroke: #a32020; }
        .chart .grid { stroke: #e8ebef; stroke-width: 1; vector-effect: non-scaling-stroke; }
        .chart .axis { stroke: #c9ced6; stroke-width: 1; vector-effect: non-scaling-stroke; }
        .chart .up { fill: #2f8a4c; }
        .chart .down { fill: #b83232; }
        .caption { color: #6b7280; font-size: 0.78rem; }
        code { font-family: ui-monospace, "SFMono-Regular", Consolas, monospace; font-size: 0.85em; }
        @media (prefers-color-scheme: dark) {
          body { color: #e6e8ec; background: #15171b; }
          h2 { border-color: #2a2e35; }
          h3, .sub, .card .label, thead th, tbody th, .caption { color: #9aa3b2; }
          .note { background: #1d2025; }
          .note.ok { background: #16241a; color: #9ad0a8; }
          .note.bad { background: #2a1717; color: #eda2a2; }
          .card { border-color: #2a2e35; }
          .card.up .value { color: #7fd39b; }
          .card.down .value { color: #ef9a9a; }
          th, td { border-color: #23262c; }
          tr.up td { color: #7fd39b; }
          tr.down td { color: #ef9a9a; }
          tr.bad td { background: #2a1717; }
          .chart { background: #1a1d22; border-color: #2a2e35; }
          .chart .grid { stroke: #262a31; }
          .chart .axis { stroke: #3a3f48; }
        }
        """;
}
