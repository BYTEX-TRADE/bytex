using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Plugins;

namespace Bytex.Backtest;

/// <summary>A stretch of time to run over, with a name to show it under.</summary>
public sealed record BacktestPeriod(UnixNanos? Start, UnixNanos? End, string? Label = null)
{
    /// <summary>What to call this period in a table: its own label, or the dates it covers.</summary>
    public string Name => Label ?? $"{Text(Start)}..{Text(End)}";

    private static string Text(UnixNanos? at) =>
        at is { } value ? value.ToDateTimeOffset().UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : string.Empty;
}

/// <summary>
/// One parameter to sweep: where it lives in a strategy's own payload and the values to try. The path is dotted -
/// <c>parameterOverrides.fast</c>, or <c>document.instruments.0.instrumentId</c> where a segment that is a number
/// indexes an array - and objects along the way are created if the payload does not have them yet. A value is written
/// as the text says: a number if it reads as one, true or false if it is one of those, and a string otherwise.
/// </summary>
public sealed record BacktestSweep
{
    public required string Path { get; init; }

    public required IReadOnlyList<string> Values { get; init; }

    /// <summary>Which strategy of the run it belongs to, when the run has more than one.</summary>
    public int Strategy { get; init; }
}

/// <summary>
/// One chosen point in the parameter space: the value for every path that point sets. Where a sweep says "try each of
/// these" and the batch crosses them, a set says "run exactly this", and the batch runs the sets it was given. That is
/// what a search needs - a generation is the particular points the last generation argued for, not a product of
/// ranges - and what a caller needs to re-run a handful of results without the grid around them.
/// </summary>
public sealed record BacktestParameterSet
{
    /// <summary>The value for each path, dotted as a sweep's path is.</summary>
    public required IReadOnlyDictionary<string, string> Values { get; init; }

    /// <summary>Which strategy of the run the paths belong to, when the run has more than one.</summary>
    public int Strategy { get; init; }
}

/// <summary>
/// One configuration and what to vary in it. Every combination of instrument, period and swept value is a run of its
/// own, and the batch is the runs side by side: the same strategy over a venue's instruments, over a set of periods,
/// over a grid of parameters, or all three at once.
/// </summary>
public sealed record BacktestBatchConfig
{
    public required BacktestRunConfig Run { get; init; }

    /// <summary>
    /// The instruments to run over, one run each. Every data stream in the configuration is pointed at the instrument
    /// of the run - a bar type keeps its own step and price - and the strategy is told about it through the
    /// paths below.
    /// </summary>
    public IReadOnlyList<InstrumentId> Instruments { get; init; } = [];

    /// <summary>
    /// Where the instrument lives in the strategy's payload, dotted, for a strategy that carries it itself: a document
    /// names its instrument at <c>document.instruments.0.instrumentId</c>, and a strategy of its own may name it
    /// anywhere. Empty leaves the payload alone, which is right for a strategy that takes whatever instrument its data
    /// carries.
    /// </summary>
    public IReadOnlyList<string> InstrumentPaths { get; init; } = [];

    /// <summary>
    /// Where a bar type lives in the strategy's payload. A bar type names an instrument as well as a step and a price,
    /// so it is not something to overwrite with an instrument id: what is there is read, its instrument is replaced,
    /// and everything else about it is kept. A document names its bar types by reference to its own instruments and
    /// needs none of this.
    /// </summary>
    public IReadOnlyList<string> BarTypePaths { get; init; } = [];

    /// <summary>Which strategy of the run <see cref="InstrumentPaths"/> and <see cref="BarTypePaths"/> belong to.</summary>
    public int InstrumentStrategy { get; init; }

    /// <summary>The periods to run over, one run each.</summary>
    public IReadOnlyList<BacktestPeriod> Periods { get; init; } = [];

    /// <summary>The parameter grid: one entry a parameter, a run per combination.</summary>
    public IReadOnlyList<BacktestSweep> Sweeps { get; init; } = [];

    /// <summary>
    /// The parameter points to run as given, one run each, instead of a grid. This and <see cref="Sweeps"/> are two
    /// ways of saying which points to run, so a batch uses one or the other: giving both is refused rather than
    /// guessed at. Instruments and periods still multiply over them, so a generation of twenty-four points over three
    /// instruments is seventy-two runs of one batch.
    /// </summary>
    public IReadOnlyList<BacktestParameterSet> ParameterSets { get; init; } = [];

    /// <summary>
    /// How many runs may be under way at once. One, the default, runs them in order and keeps one run's data in
    /// memory at a time; a host that knows its machine raises it. The results are in the order the batch describes
    /// them either way, and each run is its own engine, so what a run produces does not depend on this.
    /// </summary>
    public int MaxParallel { get; init; } = 1;

    /// <summary>
    /// What this batch's own comparable table is called in the output directory, without the currency or the
    /// extension: <c>batch</c> gives <c>batch_USDT.csv</c>. A search names each generation after itself, because
    /// several batches write into one directory and the last one to finish would otherwise be the only one left.
    /// </summary>
    public string ReportName { get; init; } = "batch";

    /// <summary>
    /// Whether the first run that throws ends the batch. Off by default: a scan over three hundred instruments should
    /// not be lost because one of them has no data, and what failed is carried on the run it failed for.
    /// </summary>
    public bool StopOnFailure { get; init; }
}

/// <summary>One run of a batch: what was varied for it, and what it did.</summary>
public sealed record BacktestBatchRun
{
    public required int Index { get; init; }

    public required string RunId { get; init; }

    public InstrumentId? Instrument { get; init; }

    public BacktestPeriod? Period { get; init; }

    /// <summary>The swept values this run used, by path.</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>What the run produced, or null when it failed.</summary>
    public BacktestResult? Result { get; init; }

    /// <summary>Why it produced nothing, or null when it did.</summary>
    public string? Failure { get; init; }

    /// <summary>The swept values as one line: <c>fast=10; slow=30</c>.</summary>
    public string ParameterText => string.Join("; ", Parameters.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"));
}

/// <summary>One line of the comparable table: what a run was, and the figures a run is judged by.</summary>
public sealed record BacktestBatchRow(
    int Index,
    string RunId,
    string Instrument,
    string Period,
    string Parameters,
    int Trades,
    decimal TotalPnl,
    decimal ReturnPercent,
    decimal MaxDrawdownPercent,
    double SharpeRatio,
    decimal? ProfitFactor,
    string Failure);

/// <summary>
/// What a batch did: every run in the order the batch described it, and the table that makes them comparable. A run
/// that failed is in it too, with the reason, because a scan that quietly dropped what it could not run would report
/// the best of an unknown number of instruments.
/// </summary>
public sealed record BacktestBatch
{
    public required IReadOnlyList<BacktestBatchRun> Runs { get; init; }

    public int Completed => Runs.Count(r => r.Result is not null);

    public int Failed => Runs.Count(r => r.Failure is not null);

    /// <summary>
    /// The runs as comparable lines, in one currency - the figures of a run are per currency, and two runs can only
    /// be set beside each other in the same one. A run that failed keeps its place, with its reason and no figures.
    /// </summary>
    public IReadOnlyList<BacktestBatchRow> Rows(Currency currency)
    {
        List<BacktestBatchRow> rows = new(Runs.Count);
        foreach (BacktestBatchRun run in Runs)
        {
            CurrencyStatistics? stats = run.Result?.Currencies.FirstOrDefault(c => c.Currency.Equals(currency));
            rows.Add(new BacktestBatchRow(
                run.Index,
                run.RunId,
                run.Instrument?.Value ?? string.Empty,
                run.Period?.Name ?? string.Empty,
                run.ParameterText,
                run.Result?.Trades.ClosedPositions ?? 0,
                stats?.TotalPnl ?? 0m,
                stats?.ReturnPercent ?? 0m,
                stats?.MaxDrawdownPercent ?? 0m,
                stats?.SharpeRatio ?? 0d,
                stats?.ProfitFactor,
                run.Failure ?? string.Empty));
        }

        return rows;
    }

    /// <summary>
    /// The batch as a table to read: a line per run with what was varied and what came of it, best first by what it
    /// returned, and the runs that produced nothing at the end with their reasons.
    /// </summary>
    public string Summary(Currency currency)
    {
        IReadOnlyList<BacktestBatchRow> rows = Rows(currency);
        List<string> lines =
        [
            $"Batch: {Runs.Count} run(s), {Completed} finished, {Failed} failed ({currency.Code})",
            string.Empty,
            string.Format(CultureInfo.InvariantCulture, "{0,4}  {1,-22} {2,-22} {3,-28} {4,6} {5,12} {6,9} {7,9} {8,7}",
                "#", "Instrument", "Period", "Parameters", "Trades", "PnL", "Return %", "MaxDD %", "Sharpe"),
        ];

        foreach (BacktestBatchRow row in rows.Where(r => r.Failure.Length == 0).OrderByDescending(r => r.ReturnPercent).Concat(rows.Where(r => r.Failure.Length > 0)))
        {
            lines.Add(row.Failure.Length > 0
                ? string.Format(CultureInfo.InvariantCulture, "{0,4}  {1,-22} {2,-22} {3,-28} failed: {4}", row.Index, Short(row.Instrument, 22), Short(row.Period, 22), Short(row.Parameters, 28), row.Failure)
                : string.Format(CultureInfo.InvariantCulture, "{0,4}  {1,-22} {2,-22} {3,-28} {4,6} {5,12:0.##} {6,9:0.##} {7,9:0.##} {8,7:0.##}",
                    row.Index, Short(row.Instrument, 22), Short(row.Period, 22), Short(row.Parameters, 28), row.Trades, row.TotalPnl, row.ReturnPercent, row.MaxDrawdownPercent, row.SharpeRatio));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string Short(string text, int width) => text.Length <= width ? text : text[..(width - 1)] + "~";

    /// <summary>
    /// The runs that produced something, best first by whatever is being looked for - <c>row => row.ReturnPercent</c>,
    /// <c>row => -row.MaxDrawdownPercent</c>. Which of them is best is the caller's business, not the engine's.
    /// </summary>
    public IReadOnlyList<BacktestBatchRow> Best(Currency currency, Func<BacktestBatchRow, decimal> by)
    {
        ArgumentNullException.ThrowIfNull(by);
        return [.. Rows(currency).Where(r => r.Failure.Length == 0).OrderByDescending(by)];
    }
}

/// <summary>
/// Turns one configuration and the things to vary in it into the runs to make. Separate from the node that runs them
/// so that a host can see what a batch will do before paying for it.
/// </summary>
public static class BacktestBatchPlanner
{
    /// <summary>
    /// Every run the batch describes, in order: instrument, then period, then the sweeps in the order they were
    /// given. A batch that varies nothing is the one run it was built from.
    /// </summary>
    public static IReadOnlyList<(BacktestRunConfig Run, BacktestBatchRun Description)> Expand(BacktestBatchConfig batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.MaxParallel < 1)
        {
            throw new InvalidOperationException("A batch runs at least one run at a time.");
        }

        List<InstrumentId?> instruments = batch.Instruments.Count == 0 ? [null] : [.. batch.Instruments.Select(i => (InstrumentId?)i)];
        List<BacktestPeriod?> periods = batch.Periods.Count == 0 ? [null] : [.. batch.Periods.Select(p => (BacktestPeriod?)p)];
        List<IReadOnlyList<Point>> grid = Points(batch);

        List<(BacktestRunConfig, BacktestBatchRun)> runs = new();
        string baseRunId = batch.Run.Engine.RunId;
        foreach (InstrumentId? instrument in instruments)
        {
            foreach (BacktestPeriod? period in periods)
            {
                foreach (IReadOnlyList<Point> combination in grid)
                {
                    int index = runs.Count;
                    BacktestRunConfig run = batch.Run;
                    if (instrument is { } id)
                    {
                        run = WithInstrument(run, id, batch);
                    }

                    if (period is { } window)
                    {
                        run = run with { Start = window.Start, End = window.End };
                    }

                    Dictionary<string, string> parameters = new(StringComparer.Ordinal);
                    foreach ((int strategy, string path, string value) in combination)
                    {
                        run = WithStrategyValue(run, strategy, path, value);
                        parameters[path] = value;
                    }

                    // Every run needs an id of its own: it is what its reports are written under, and two runs of one
                    // batch writing into the same directory would leave one of them.
                    string runId = $"{baseRunId}-{index.ToString("000", CultureInfo.InvariantCulture)}";
                    run = run with { Engine = run.Engine with { RunId = runId } };
                    runs.Add((run, new BacktestBatchRun { Index = index, RunId = runId, Instrument = instrument, Period = period, Parameters = parameters }));
                }
            }
        }

        return runs;
    }

    /// <summary>One parameter of one run: which strategy it belongs to, where it lives, and what it is set to.</summary>
    private readonly record struct Point(int Strategy, string Path, string Value);

    /// <summary>
    /// The points to run: the grid the sweeps describe, or the sets given as they were given. A batch that varies no
    /// parameter is one empty point, which is the single run it was built from.
    /// </summary>
    private static List<IReadOnlyList<Point>> Points(BacktestBatchConfig batch)
    {
        if (batch.Sweeps.Count > 0 && batch.ParameterSets.Count > 0)
        {
            // Crossing a grid with a list of chosen points is not something either of them means, and picking one of
            // them silently would run a batch nobody described.
            throw new InvalidOperationException(
                "A batch describes its parameter points either as sweeps to cross or as sets to run as given, not both.");
        }

        return batch.ParameterSets.Count > 0 ? Sets(batch.ParameterSets) : Grid(batch.Sweeps);
    }

    /// <summary>The sets as they were given, in order; a set is a run of its own whether or not another set repeats it.</summary>
    private static List<IReadOnlyList<Point>> Sets(IReadOnlyList<BacktestParameterSet> sets)
    {
        List<IReadOnlyList<Point>> points = new(sets.Count);
        foreach (BacktestParameterSet set in sets)
        {
            if (set.Values.Count == 0)
            {
                throw new InvalidOperationException("A parameter set sets no parameter, so it describes no run of its own.");
            }

            // Ordered by path so that a run's parameters read the same whichever order a caller wrote them in.
            points.Add([.. set.Values.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => new Point(set.Strategy, v.Key, v.Value))]);
        }

        return points;
    }

    /// <summary>Every combination of the swept values, in the order the sweeps were given; one empty combination when there are none.</summary>
    private static List<IReadOnlyList<Point>> Grid(IReadOnlyList<BacktestSweep> sweeps)
    {
        List<IReadOnlyList<Point>> grid = [[]];
        foreach (BacktestSweep sweep in sweeps)
        {
            if (sweep.Values.Count == 0)
            {
                throw new InvalidOperationException($"The sweep of '{sweep.Path}' has no values to try.");
            }

            List<IReadOnlyList<Point>> next = new(grid.Count * sweep.Values.Count);
            foreach (IReadOnlyList<Point> sofar in grid)
            {
                foreach (string value in sweep.Values)
                {
                    next.Add([.. sofar, new Point(sweep.Strategy, sweep.Path, value)]);
                }
            }

            grid = next;
        }

        return grid;
    }

    /// <summary>
    /// Points a run at one instrument: every data stream it loads, and the strategy that carries the instrument in
    /// its own payload. A bar type keeps its step, its price and its source, and changes only the instrument.
    /// </summary>
    private static BacktestRunConfig WithInstrument(BacktestRunConfig run, InstrumentId instrument, BacktestBatchConfig batch)
    {
        List<BacktestDataConfig> data = run.Data.Select(d => d with
        {
            InstrumentId = d.InstrumentId is null ? null : instrument,
            BarType = d.BarType is { } barType ? barType with { InstrumentId = instrument } : null,
        }).ToList();

        run = run with { Data = data };
        foreach (string path in batch.InstrumentPaths)
        {
            run = WithStrategyValue(run, batch.InstrumentStrategy, path, instrument.Value);
        }

        foreach (string path in batch.BarTypePaths)
        {
            StrategyDefinition definition = Strategy(run, batch.InstrumentStrategy, path);
            string text = GetText(definition.Payload, path)
                ?? throw new InvalidOperationException($"'{path}' does not name a bar type in this strategy's payload.");
            if (!BarType.TryParse(text, out BarType barType))
            {
                throw new InvalidOperationException($"'{path}' reads '{text}', which is not a bar type.");
            }

            run = WithStrategyValue(run, batch.InstrumentStrategy, path, (barType with { InstrumentId = instrument }).ToString());
        }

        return run;
    }

    private static StrategyDefinition Strategy(BacktestRunConfig run, int strategy, string path) =>
        strategy >= 0 && strategy < run.Strategies.Count
            ? run.Strategies[strategy]
            : throw new InvalidOperationException($"The batch sets '{path}' on strategy {strategy}, and the run has {run.Strategies.Count}.");

    /// <summary>Writes one value into a strategy's payload at a dotted path, leaving everything else as it was.</summary>
    private static BacktestRunConfig WithStrategyValue(BacktestRunConfig run, int strategy, string path, string value)
    {
        StrategyDefinition definition = Strategy(run, strategy, path);
        JsonElement payload = SetValue(definition.Payload, path, value);
        List<StrategyDefinition> strategies = [.. run.Strategies];
        strategies[strategy] = definition with { Payload = payload };
        return run with { Strategies = strategies };
    }

    /// <summary>
    /// The payload with one value set at a dotted path. A segment that is a number indexes an array that has to be
    /// there already; a segment that names a field creates the object it needs. A path that cannot be followed is
    /// refused here, before any run is made, because a typo in it would otherwise run the whole batch for nothing.
    /// </summary>
    public static JsonElement SetValue(JsonElement payload, string path, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        JsonNode root = JsonNode.Parse(payload.GetRawText()) ?? throw new InvalidOperationException("The strategy payload is empty.");
        string[] segments = path.Split('.');
        JsonNode current = root;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            // The object a value goes into is created if the payload has not got it yet - which is how
            // parameterOverrides appears on a document that had none. Anything missing further up is a typo in the
            // path, and building a tree for it would run the whole batch with a parameter nothing reads.
            current = Step(current, segments[i], path, mayCreate: i == segments.Length - 2);
        }

        string leaf = segments[^1];
        JsonValue node = Value(value);
        switch (current)
        {
            case JsonObject obj:
                obj[leaf] = node;
                break;
            case JsonArray array when int.TryParse(leaf, CultureInfo.InvariantCulture, out int index) && index >= 0 && index < array.Count:
                array[index] = node;
                break;
            default:
                throw new InvalidOperationException($"'{path}' cannot be set on this strategy's payload: '{leaf}' has nowhere to go.");
        }

        return JsonSerializer.Deserialize<JsonElement>(root.ToJsonString());
    }

    /// <summary>What is at a dotted path as text, or null when the path leads nowhere.</summary>
    public static string? GetText(JsonElement payload, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        JsonElement current = payload;
        foreach (string segment in path.Split('.'))
        {
            if (current.ValueKind == JsonValueKind.Array && int.TryParse(segment, CultureInfo.InvariantCulture, out int index))
            {
                if (index < 0 || index >= current.GetArrayLength())
                {
                    return null;
                }

                current = current[index];
                continue;
            }

            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out JsonElement next))
            {
                return null;
            }

            current = next;
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : current.ToString();
    }

    private static JsonNode Step(JsonNode current, string segment, string path, bool mayCreate)
    {
        if (current is JsonArray array)
        {
            if (!int.TryParse(segment, CultureInfo.InvariantCulture, out int index) || index < 0 || index >= array.Count)
            {
                throw new InvalidOperationException($"'{path}' cannot be followed: '{segment}' is not one of the {array.Count} element(s) it names.");
            }

            return array[index] ?? throw new InvalidOperationException($"'{path}' cannot be followed: element {index} is null.");
        }

        if (current is not JsonObject obj)
        {
            throw new InvalidOperationException($"'{path}' cannot be followed: '{segment}' is asked of a value, not an object.");
        }

        if (obj[segment] is { } existing)
        {
            return existing;
        }

        if (!mayCreate)
        {
            throw new InvalidOperationException($"'{path}' cannot be followed: '{segment}' is not in this strategy's payload.");
        }

        JsonObject created = new();
        obj[segment] = created;
        return created;
    }

    /// <summary>A value as the text says it: a number, true or false, or a string.</summary>
    private static JsonValue Value(string text) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal number) ? JsonValue.Create(number)
        : bool.TryParse(text, out bool flag) ? JsonValue.Create(flag)
        : JsonValue.Create(text);
}
