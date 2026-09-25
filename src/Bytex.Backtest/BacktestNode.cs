using System.Globalization;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Plugins;
using Bytex.Core.Trading;
using Bytex.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bytex.Backtest;

/// <summary>
/// Describes one data stream to load from a catalog for a run.
/// </summary>
public sealed record BacktestDataConfig
{
    public required string CatalogPath { get; init; }

    /// <summary>"quotes", "trades", "bars", or "book_deltas".</summary>
    public required string DataKind { get; init; }

    public InstrumentId? InstrumentId { get; init; }

    public BarType? BarType { get; init; }

    public UnixNanos? Start { get; init; }

    public UnixNanos? End { get; init; }
}

/// <summary>
/// Venue configuration in a run, JSON-friendly.
/// </summary>
public sealed record BacktestVenueConfig
{
    public required string Venue { get; init; }

    public OmsType OmsType { get; init; } = OmsType.Netting;

    public AccountType AccountType { get; init; } = AccountType.Cash;

    public string? BaseCurrency { get; init; }

    /// <summary>Starting balances as "amount CURRENCY".</summary>
    public IReadOnlyList<string> StartingBalances { get; init; } = [];

    public decimal DefaultLeverage { get; init; } = 1m;

    public BarExecutionMode BarExecution { get; init; } = BarExecutionMode.OhlcPath;

    public decimal ProbFillOnLimit { get; init; } = 1m;

    public decimal ProbSlippage { get; init; }

    public int FillModelSeed { get; init; } = FillModel.DefaultSeed;

    public TimeSpan Latency { get; init; } = TimeSpan.Zero;

    public bool RejectStopOrdersAtMarket { get; init; } = true;

    public SimulatedVenueConfig ToVenueConfig() => new()
    {
        Venue = new Venue(Venue),
        OmsType = OmsType,
        AccountType = AccountType,
        BaseCurrency = BaseCurrency is null ? null : Currency.FromCode(BaseCurrency),
        StartingBalances = StartingBalances.Select(Money.Parse).ToList(),
        DefaultLeverage = DefaultLeverage,
        BarExecution = BarExecution,
        FillModel = new FillModel(ProbFillOnLimit, 1m, ProbSlippage, FillModelSeed),
        LatencyModel = Latency == TimeSpan.Zero ? LatencyModel.Zero : LatencyModel.Uniform(Latency),
        RejectStopOrdersAtMarket = RejectStopOrdersAtMarket,
    };
}

/// <summary>
/// A complete run: engine settings, venues, data, and strategies.
/// </summary>
public sealed record BacktestRunConfig
{
    public BacktestEngineConfig Engine { get; init; } = new();

    public required IReadOnlyList<BacktestVenueConfig> Venues { get; init; }

    public required IReadOnlyList<BacktestDataConfig> Data { get; init; }

    public IReadOnlyList<StrategyDefinition> Strategies { get; init; } = [];

    public IReadOnlyList<ActorDefinition> Actors { get; init; } = [];

    public UnixNanos? Start { get; init; }

    public UnixNanos? End { get; init; }

    /// <summary>Directory to write reports into; null disables report output.</summary>
    public string? OutputDirectory { get; init; }
}

/// <summary>
/// Runs one or more <see cref="BacktestRunConfig"/>s, loading data from catalogs and building strategies through the plugin registry.
/// </summary>
public sealed class BacktestNode
{
    private readonly PluginRegistry _registry;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _log;

    public BacktestNode(PluginRegistry? registry = null, ILoggerFactory? loggerFactory = null)
    {
        _registry = registry ?? new PluginRegistry();
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _log = _loggerFactory.CreateLogger<BacktestNode>();
    }

    public PluginRegistry Registry => _registry;

    public async Task<IReadOnlyList<BacktestResult>> RunAsync(IEnumerable<BacktestRunConfig> runs, CancellationToken ct = default)
    {
        List<BacktestResult> results = new();
        foreach (BacktestRunConfig run in runs)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await RunAsync(run, ct).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>
    /// Runs everything one batch describes and hands back the runs side by side. A run that throws is carried as the
    /// reason it failed rather than taking the batch down with it, unless the batch says otherwise: a scan over three
    /// hundred instruments should not be lost because one of them has no data.
    /// </summary>
    public async Task<BacktestBatch> RunBatchAsync(BacktestBatchConfig batch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);

        // Expanded before anything runs, so a path that cannot be followed or a sweep with no values is refused now
        // rather than after an hour of runs.
        IReadOnlyList<(BacktestRunConfig Run, BacktestBatchRun Description)> planned = BacktestBatchPlanner.Expand(batch);
        BacktestBatchRun[] finished = new BacktestBatchRun[planned.Count];
        _log.LogInformation("Batch {RunId}: {Runs} run(s), {Parallel} at a time", batch.Run.Engine.RunId, planned.Count, batch.MaxParallel);

        await Parallel.ForEachAsync(
            planned.Select((p, i) => (Plan: p, At: i)),
            new ParallelOptions { MaxDegreeOfParallelism = batch.MaxParallel, CancellationToken = ct },
            async (item, token) =>
            {
                (BacktestRunConfig run, BacktestBatchRun description) = item.Plan;
                try
                {
                    BacktestResult result = await RunAsync(run, token).ConfigureAwait(false);
                    finished[item.At] = description with { Result = result };
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _log.LogError(e, "Run {RunId} of the batch failed", description.RunId);
                    finished[item.At] = description with { Failure = e.Message };
                    if (batch.StopOnFailure)
                    {
                        throw;
                    }
                }
            }).ConfigureAwait(false);

        BacktestBatch result = new() { Runs = finished };
        _log.LogInformation("Batch {RunId}: {Completed} run(s) finished, {Failed} failed", batch.Run.Engine.RunId, result.Completed, result.Failed);
        if (batch.Run.OutputDirectory is { } output)
        {
            ReportWriter.WriteBatch(result, output, batch.ReportName);
        }

        return result;
    }

    /// <summary>
    /// Runs a guided search: a generation of candidates at a time, each one judged by the caller and bred into the
    /// next. The running is a batch's running - every candidate is a run of its own engine - so what a candidate
    /// scores does not depend on what else was in its generation.
    /// </summary>
    /// <param name="search">What may vary, how far, and how hard to look.</param>
    /// <param name="currency">Which of a run's tables to read the figure off.</param>
    /// <param name="fitness">What makes one run better than another. Higher is better; the engine does not decide this.</param>
    /// <param name="ct">Stops the search between runs.</param>
    public async Task<BacktestSearch> RunSearchAsync(
        BacktestSearchConfig search,
        Currency currency,
        Func<BacktestBatchRow, decimal> fitness,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(fitness);

        // Checked before anything runs: a space that cannot be drawn from is refused now rather than after a
        // generation of runs has been paid for.
        BacktestSearchPlanner.Validate(search);

        Random random = new(search.Seed);
        Dictionary<string, BacktestSearchCandidate> seen = new(StringComparer.Ordinal);
        List<BacktestSearchGeneration> generations = new(search.Generations);
        IReadOnlyList<BacktestParameterSet> points = BacktestSearchPlanner.FirstGeneration(search, random);
        decimal? bestSoFar = null;
        int without = 0;
        bool stoppedEarly = false;
        string baseRunId = search.Run.Engine.RunId;

        for (int index = 0; index < search.Generations; index++)
        {
            ct.ThrowIfCancellationRequested();

            // Points this search has already run are not run again: a run is deterministic, so the answer would be
            // the same and the time would not. What is proposed is still recorded, or a generation would look
            // smaller than the search actually considered.
            List<BacktestParameterSet> toRun = [];
            foreach (BacktestParameterSet point in points)
            {
                if (!seen.ContainsKey(BacktestSearchPlanner.Key(point)) && !toRun.Any(p => BacktestSearchPlanner.Key(p) == BacktestSearchPlanner.Key(point)))
                {
                    toRun.Add(point);
                }
            }

            Dictionary<string, BacktestBatchRun> made = new(StringComparer.Ordinal);
            if (toRun.Count > 0)
            {
                BacktestBatch generation = await RunBatchAsync(
                    new BacktestBatchConfig
                    {
                        Run = search.Run with { Engine = search.Run.Engine with { RunId = $"{baseRunId}-g{index.ToString("00", CultureInfo.InvariantCulture)}" } },
                        ParameterSets = toRun,
                        MaxParallel = search.MaxParallel,
                        StopOnFailure = search.StopOnFailure,
                        ReportName = $"generation-{index.ToString("00", CultureInfo.InvariantCulture)}",
                    },
                    ct).ConfigureAwait(false);

                IReadOnlyList<BacktestBatchRow> rows = generation.Rows(currency);
                for (int at = 0; at < toRun.Count; at++)
                {
                    made[BacktestSearchPlanner.Key(toRun[at])] = generation.Runs[at];
                    BacktestBatchRow row = rows[at];
                    seen[BacktestSearchPlanner.Key(toRun[at])] = new BacktestSearchCandidate
                    {
                        Generation = index,
                        Index = at,
                        Point = toRun[at],
                        Run = generation.Runs[at],
                        Fitness = row.Failure.Length == 0 ? fitness(row) : null,
                    };
                }
            }

            // A generation can propose the same point more than once, and one run answered all of them: only the
            // candidate that caused the run counts as having been run, or the search would report a cost it never
            // paid - which is the one number that says whether searching beat crossing.
            HashSet<string> attributed = new(StringComparer.Ordinal);
            List<BacktestSearchCandidate> candidates = new(points.Count);
            for (int at = 0; at < points.Count; at++)
            {
                string key = BacktestSearchPlanner.Key(points[at]);
                candidates.Add(seen[key] with
                {
                    Generation = index,
                    Index = at,
                    Point = points[at],
                    Reused = !(made.ContainsKey(key) && attributed.Add(key)),
                });
            }

            BacktestSearchGeneration bred = new() { Index = index, Candidates = candidates };
            generations.Add(bred);
            _log.LogInformation(
                "Search {RunId}: generation {Generation} of {Generations}, {Ran} run(s), best {Best}",
                baseRunId, index, search.Generations, bred.Ran, bred.Best?.Fitness);

            if (bred.Best?.Fitness is { } best && (bestSoFar is null || best > bestSoFar))
            {
                bestSoFar = best;
                without = 0;
            }
            else
            {
                without++;
            }

            if (search.StopAfterGenerationsWithoutImprovement is { } patience && without >= patience)
            {
                // Nothing has improved for long enough that more generations of the same are not worth their runs.
                stoppedEarly = index < search.Generations - 1;
                break;
            }

            if (index < search.Generations - 1)
            {
                points = BacktestSearchPlanner.NextGeneration(search, candidates, random);
            }
        }

        BacktestSearch searched = new(search, generations, stoppedEarly);

        // A generation's table covers a generation. Nothing covered the search until this: what it tried, in what
        // order, and what each candidate was worth is the whole record of a search, and it was only ever on screen.
        if (search.Run.OutputDirectory is { } searchOutput)
        {
            ReportWriter.WriteSearch(searched, searchOutput);
        }

        return searched;
    }

    /// <summary>
    /// Runs a search that carries its own objective, for a caller that described what it wanted rather than writing
    /// it: which figure, in which currency, and whether less of it is better.
    /// </summary>
    public Task<BacktestSearch> RunSearchAsync(BacktestSearchConfig search, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(search);
        BacktestSearchObjective objective = search.Objective
            ?? throw new InvalidOperationException("This search has no objective, so there is nothing to judge a candidate by.");

        return RunSearchAsync(search, Currency.FromCode(objective.Currency), objective.Fitness(), ct);
    }

    public async Task<BacktestResult> RunAsync(BacktestRunConfig run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        using BacktestEngine engine = await BuildEngineAsync(run, ct).ConfigureAwait(false);
        engine.Run(run.Start, run.End);
        BacktestResult result = engine.GetResult();
        if (run.OutputDirectory is { } output)
        {
            ReportWriter.WriteAll(result, Path.Combine(output, result.RunId));
        }

        return result;
    }

    /// <summary>
    /// Builds a configured engine without running it, for callers that want to inspect or drive it manually.
    /// </summary>
    public async Task<BacktestEngine> BuildEngineAsync(BacktestRunConfig run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        BacktestEngine engine = new(run.Engine, _loggerFactory);

        HashSet<InstrumentId> instrumentIds = new();
        foreach (BacktestDataConfig dataConfig in run.Data)
        {
            InstrumentId? id = dataConfig.InstrumentId ?? dataConfig.BarType?.InstrumentId;
            if (id is { } instrumentId)
            {
                instrumentIds.Add(instrumentId);
            }
        }

        Dictionary<string, ParquetDataCatalog> catalogs = new(StringComparer.OrdinalIgnoreCase);
        foreach (BacktestDataConfig dataConfig in run.Data)
        {
            if (!catalogs.ContainsKey(dataConfig.CatalogPath))
            {
                catalogs[dataConfig.CatalogPath] = new ParquetDataCatalog(dataConfig.CatalogPath);
            }
        }

        foreach (InstrumentId instrumentId in instrumentIds)
        {
            Instrument? instrument = catalogs.Values.Select(c => c.Instrument(instrumentId)).FirstOrDefault(i => i is not null);
            if (instrument is null)
            {
                throw new InvalidOperationException($"Instrument {instrumentId} not found in any configured catalog.");
            }

            engine.AddInstrument(instrument);
        }

        foreach (BacktestVenueConfig venue in run.Venues)
        {
            engine.AddVenue(venue.ToVenueConfig());
        }

        foreach (BacktestDataConfig dataConfig in run.Data)
        {
            ParquetDataCatalog catalog = catalogs[dataConfig.CatalogPath];
            UnixNanos? start = dataConfig.Start ?? run.Start;
            UnixNanos? end = dataConfig.End ?? run.End;
            IEnumerable<IData> data = dataConfig.DataKind.ToLowerInvariant() switch
            {
                "quotes" => (await catalog.QuoteTicksAsync(Require(dataConfig.InstrumentId, dataConfig), start, end, ct).ConfigureAwait(false)).Cast<IData>(),
                "trades" => (await catalog.TradeTicksAsync(Require(dataConfig.InstrumentId, dataConfig), start, end, ct).ConfigureAwait(false)).Cast<IData>(),
                "bars" => (await catalog.BarsAsync(dataConfig.BarType ?? throw new InvalidOperationException("Bar data requires a bar type."), start, end, ct).ConfigureAwait(false)).Cast<IData>(),
                "book_deltas" => (await catalog.OrderBookDeltasAsync(Require(dataConfig.InstrumentId, dataConfig), start, end, ct).ConfigureAwait(false)).Cast<IData>(),
                "funding" => (await catalog.FundingRatesAsync(Require(dataConfig.InstrumentId, dataConfig), start, end, ct).ConfigureAwait(false)).Cast<IData>(),
                _ => throw new InvalidOperationException($"Unknown data kind '{dataConfig.DataKind}'."),
            };
            engine.AddData(data);
        }

        foreach (ActorDefinition definition in run.Actors)
        {
            engine.AddActor(_registry.CreateActor(definition));
        }

        foreach (StrategyDefinition definition in run.Strategies)
        {
            Strategy strategy = _registry.CreateStrategy(definition);
            engine.AddStrategy(strategy);
        }

        _log.LogInformation("Built backtest engine {RunId}: {Venues} venues, {Instruments} instruments, {Data} data elements, {Strategies} strategies",
            run.Engine.RunId, run.Venues.Count, instrumentIds.Count, engine.Data.Count, run.Strategies.Count);
        return engine;
    }

    private static InstrumentId Require(InstrumentId? id, BacktestDataConfig config) =>
        id ?? throw new InvalidOperationException($"Data config for '{config.DataKind}' requires an instrument id.");
}
