using System.Globalization;
using Bytex.Core.Model.Accounts;
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

    /// <summary>
    /// Which of the catalog's data sets to feed the run: <c>quotes</c>, <c>trades</c>, <c>bars</c>,
    /// <c>book_deltas</c>, <c>book_depth</c> or <c>funding</c>. Every kind the catalog can store can be read
    /// here, which a test holds to by driving the catalog's own listing of kinds.
    /// </summary>
    public required string DataKind { get; init; }

    public MarketKey? MarketKey { get; init; }

    public CandleSeries? CandleSeries { get; init; }

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

    /// <summary>How fills are charged: null or venue mode = the instrument's stored maker and taker rates.</summary>
    public FeeSettings? Fees { get; init; }

    /// <summary>
    /// When this venue's trading day ends, in UTC, which is what a DAY order lives until (R8.26). Left out, the venue
    /// never closes and a DAY order expires when the UTC date rolls over - what a crypto venue does with one.
    /// </summary>
    public TimeSpan? SessionEndUtc { get; init; }

    /// <summary>The bound on walking a bar in the instrument's own increment (R8.25); see <see cref="BarExecution"/>.</summary>
    public int MaxBarWalkSteps { get; init; } = 10_000;

    /// <summary>
    /// How this venue works out margin (R4.12). Left out, it is the engine's flat rate; a real venue charges by tiers.
    /// </summary>
    public MarginModelConfig? MarginModel { get; init; }

    /// <summary>
    /// Whether a margin account that falls below its maintenance margin has its positions closed by the venue (R8.16).
    /// On, as the simulated venue itself defaults, because a venue that never liquidates is the unusual one.
    ///
    /// <para>
    /// It had no member here at all, so a configuration saying <c>"liquidate": false</c> was read by nothing and the
    /// run liquidated anyway - and one saying <c>true</c> was equally unread and happened to agree with the default.
    /// The engine's own tests wrote it and passed for that reason, which is how it was found: they were refused the
    /// moment an unknown member stopped being skipped in silence.
    /// </para>
    /// </summary>
    public bool Liquidate { get; init; } = true;

    /// <summary>
    /// How a fill is sized against what was on offer where it happened (R8.24). Left out, a fill is bounded by the
    /// size really there, which is what a simulated venue defaults to; the other mode fills an order whole.
    /// </summary>
    public FillSizing FillSizing { get; init; } = FillSizing.AvailableSize;

    /// <summary>
    /// The share of a bar's volume one order may take when a run is driven by bars rather than by a book. The
    /// simulated venue's own default; null means no share is assumed and a bar-priced order is unbounded.
    /// </summary>
    public decimal? BarVolumeShare { get; init; } = SimulatedVenueConfig.DefaultBarVolumeShare;

    /// <summary>What depth this venue keeps: one level, or the whole book it is fed.</summary>
    public BookType BookType { get; init; } = BookType.L1;

    /// <summary>
    /// Whether this venue refuses to amend an order, so a strategy has to cancel and replace. Some venues do, and a
    /// run against one that does not is a run whose order flow could not happen there.
    /// </summary>
    public bool RefusesOrderAmends { get; init; }

    /// <summary>
    /// Whether this venue accepts contingent orders - a bracket, a one-cancels-other - as one instruction. Off, the
    /// engine manages the legs itself, which is what a venue without them forces.
    /// </summary>
    public bool SupportContingentOrders { get; init; } = true;

    /// <summary>
    /// Venue behaviours this run asks for by name (R8.19), each with the figures it needs. Empty is a venue that does
    /// nothing beyond what it models itself.
    /// </summary>
    public IReadOnlyList<SimulationModuleConfig> Modules { get; init; } = [];

    /// <summary>
    /// What a fill costs (R8.14). Left out, the instrument's own maker and taker rates are used, which is what a
    /// venue charges unless a desk has negotiated otherwise.
    /// </summary>
    public FeeModelConfig? FeeModel { get; init; }

    /// <inheritdoc cref="ToVenueConfig(IReadOnlyList{ISimulationModuleFactory})"/>
    public SimulatedVenueConfig ToVenueConfig() => ToVenueConfig([new BuiltinSimulationModuleFactory()]);

    /// <summary>
    /// This description as a venue, with the behaviours it names built by the factories given. The parameterless form
    /// knows the behaviours this engine ships; a host carrying plugins passes their factories too.
    /// </summary>
    public SimulatedVenueConfig ToVenueConfig(IReadOnlyList<ISimulationModuleFactory> moduleFactories) => new()
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
        MarginModel = MarginModel?.Build() ?? RateMarginModel.Default,
        Liquidate = Liquidate,
        FillSizing = FillSizing,
        BarVolumeShare = BarVolumeShare,
        BookType = BookType,
        RefusesOrderAmends = RefusesOrderAmends,
        SupportContingentOrders = SupportContingentOrders,
        FeeModel = Charge(),
        Modules = [.. Modules.Select(m => Build(m, moduleFactories))],
        Session = SessionEndUtc is { } end ? TradingSession.EndingAt(end) : TradingSession.Continuous,
        MaxBarWalkSteps = MaxBarWalkSteps,
    };

    /// <summary>
    /// What a fill costs, from the ONE place that says so.
    ///
    /// <para>
    /// This tree has two ways to say it, which arrived from different directions and mean the same thing to a venue:
    /// <c>fees</c>, the document's own assumption (the instrument's stored rates, or rates somebody typed), and
    /// <c>feeModel</c>, which names one of the engine's models and its figures. Both end up as the venue's
    /// <see cref="Bytex.Backtest.FeeModel"/>, so leaving both set would let the later assignment win in silence - and
    /// commissions nobody can trace to a decision is the shape of defect this release was about.
    /// </para>
    ///
    /// <para>
    /// So both are allowed and BOTH AT ONCE is refused by name. Neither is deprecated: the document's assumption is
    /// what a builder sets from an instrument's own rates, and the named model is what a hand-written run uses to
    /// charge something the instrument does not say.
    /// </para>
    /// </summary>
    private Bytex.Backtest.FeeModel? Charge()
    {
        if (Fees is not null && FeeModel is not null)
        {
            throw new InvalidOperationException(
                "This venue says what a fill costs twice: 'fees' carries the document's own assumption and 'feeModel' "
                + "names a model with its figures. They both become the venue's fee model, so one would quietly "
                + "override the other and the commissions in the result would trace to neither. Keep one.");
        }

        // Qualified, because this type has a PROPERTY called FeeModel now and an unqualified name would find it.
        return FeeModel is { } named ? named.Build() : Bytex.Backtest.FeeModel.From(Fees);
    }

    /// <summary>
    /// The behaviour a name asks for, or a refusal saying which names there are. A name nothing can build is refused
    /// rather than skipped: a run that quietly dropped a behaviour somebody asked for would charge less than it was
    /// told to and report that it applied nothing.
    /// </summary>
    private static ISimulationModule Build(SimulationModuleConfig module, IReadOnlyList<ISimulationModuleFactory> factories)
    {
        ArgumentNullException.ThrowIfNull(factories);
        foreach (ISimulationModuleFactory factory in factories)
        {
            if (factory.Names.Contains(module.Name, StringComparer.OrdinalIgnoreCase))
            {
                return factory.Create(module.Name, module.Parameters);
            }
        }

        throw new InvalidOperationException(
            $"No venue behaviour called '{module.Name}' is available here. Available: "
            + string.Join(", ", factories.SelectMany(f => f.Names).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal))
            + ". A behaviour from a plugin needs that plugin loaded.");
    }
}

/// <summary>
/// A margin model as a run's description carries it: a kind, and the tiers if it has any.
///
/// <para>
/// A model is an object and a description is data, so this is the shape between them. An unknown kind is refused by
/// name: falling back to the flat rate would margin a run by a rule its description did not ask for, and nothing in the
/// result would say so.
/// </para>
/// </summary>
/// <summary>
/// What a fill costs, as a run's description carries it: a kind and the figures it takes.
///
/// <para>
/// Shaped after <see cref="MarginModelConfig"/> because the problem is the same - a model is an object and a file is
/// not - and the four kinds are the ones this engine ships. Left out entirely, a venue charges the instrument's own
/// maker and taker rates.
/// </para>
/// </summary>
/// <summary>
/// A venue behaviour a run asks for by NAME, with the figures it needs (R8.19).
///
/// <para>
/// Shaped after an indicator in a document: a module is an object and a file is not, so a factory turns the name into
/// one. The engine ships <c>rolloverInterest</c>, which until now could only be constructed in C# - so the one
/// behaviour it has could not be switched on by anybody running from a configuration, which is the same defect as a
/// setting nothing reads.
/// </para>
/// </summary>
public sealed record SimulationModuleConfig
{
    /// <summary>The behaviour's own name, as it will appear in what the run reports it applied.</summary>
    public required string Name { get; init; }

    /// <summary>Its figures, as strings, because only the behaviour knows what it needs.</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

public sealed record FeeModelConfig
{
    /// <summary>The instrument's own maker and taker rates, which is what a venue charges by default.</summary>
    public const string MakerTaker = "makerTaker";

    /// <summary>A flat fraction of notional on every fill, maker or taker alike.</summary>
    public const string Percent = "percent";

    /// <summary>The same amount on every fill, whatever its size.</summary>
    public const string Fixed = "fixed";

    /// <summary>An amount per contract, optionally differing between taker and maker.</summary>
    public const string PerContract = "perContract";

    public string Kind { get; init; } = MakerTaker;

    /// <summary>The fraction of notional for <see cref="Percent"/>: 0.001 is a tenth of a percent, not 0.1.</summary>
    public decimal Rate { get; init; }

    /// <summary>The amount for <see cref="Fixed"/>, or the taker amount for <see cref="PerContract"/>, as "1.5 USDT".</summary>
    public string? Amount { get; init; }

    /// <summary>The maker amount for <see cref="PerContract"/>, when it differs from the taker one.</summary>
    public string? MakerAmount { get; init; }

    public FeeModel Build() => Kind.ToLowerInvariant() switch
    {
        "makertaker" => new MakerTakerFeeModel(),
        "percent" => new PercentFeeModel(Rate),
        "fixed" => new FixedFeeModel(Money.Parse(Amount ?? throw new InvalidOperationException(
            "A fixed fee model needs the amount it charges per fill, as \"1.5 USDT\"."))),
        "percontract" => MakerAmount is { } maker
            ? new PerContractFeeModel(
                Money.Parse(Amount ?? throw new InvalidOperationException("A per-contract fee model needs its taker amount.")),
                Money.Parse(maker))
            : new PerContractFeeModel(Money.Parse(Amount ?? throw new InvalidOperationException(
                "A per-contract fee model needs the amount it charges per contract, as \"0.5 USDT\"."))),
        _ => throw new InvalidOperationException(
            $"'{Kind}' is not a fee model this engine has. It has {MakerTaker}, {Percent}, {Fixed} and {PerContract}."),
    };
}

public sealed record MarginModelConfig
{
    public const string Rate = "rate";
    public const string Tiered = "tiered";

    public string Kind { get; init; } = Rate;

    /// <summary>The tiers, for a tiered model: from a notional, the fraction to open and the fraction to keep.</summary>
    public IReadOnlyList<MarginTierConfig> Tiers { get; init; } = [];

    public IMarginModel Build() => Kind.ToLowerInvariant() switch
    {
        Rate => RateMarginModel.Default,
        Tiered => new TieredMarginModel(Tiers.Select(t => new MarginTier(t.NotionalFrom, t.Initial, t.Maintenance))),
        _ => throw new ArgumentException($"'{Kind}' is not a margin model this engine has; it has '{Rate}' and '{Tiered}'.", nameof(Kind)),
    };
}

/// <param name="NotionalFrom">The notional this tier starts at; the first starts at nothing.</param>
/// <param name="Initial">The fraction of the notional needed to open a position in this tier.</param>
/// <param name="Maintenance">The fraction needed to keep it.</param>
public sealed record MarginTierConfig(decimal NotionalFrom, decimal Initial, decimal Maintenance);

/// <summary>
/// A complete run: engine settings, venues, data, and strategies.
/// </summary>
public sealed record BacktestRunConfig
{
    /// <summary>
    /// How many elements a run is fed at a time, or null to read the whole period at once.
    ///
    /// <para>
    /// Set it and the run pulls from the catalog as it goes, merging its data configs by timestamp and letting go of
    /// each chunk once it has been dispatched - so what is held is a chunk rather than a period. Leave it unset and
    /// nothing changes: the whole range is read, sorted and handed over, which is the right thing for data that fits
    /// and the only thing that was possible before.
    /// </para>
    ///
    /// <para>
    /// It is worth setting for quotes and book deltas and not for bars. Measured: a bar costs about 65 bytes stored,
    /// so a year of one-minute data for an instrument is some 34 MB, while quote data runs to 190 MB compressed per
    /// instrument-day - a month of which no machine is going to hold.
    /// </para>
    /// </summary>
    public int? ChunkSize { get; init; }

    public BacktestEngineConfig Engine { get; init; } = new();

    public required IReadOnlyList<BacktestVenueConfig> Venues { get; init; }

    public required IReadOnlyList<BacktestDataConfig> Data { get; init; }

    public IReadOnlyList<StrategyDefinition> Strategies { get; init; } = [];

    public IReadOnlyList<RuntimeModuleDefinition> RuntimeModules { get; init; } = [];

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
        if (run.ChunkSize is { } chunk)
        {
            await RunInChunksAsync(engine, run, chunk, ct).ConfigureAwait(false);
        }
        else
        {
            engine.Run(run.Start, run.End);
        }
        BacktestResult result = engine.GetResult();
        if (run.OutputDirectory is { } output)
        {
            ReportWriter.WriteAll(result, Path.Combine(output, result.RunId));
        }

        return result;
    }

    /// <summary>
    /// Feeds a run from the catalog a chunk at a time, in timestamp order across every data config, releasing each
    /// chunk once it has been dispatched.
    ///
    /// <para>
    /// The merge takes whichever source's next element is earliest, so what is held is one element per source and
    /// one chunk - never a period. Reading each source into a list and sorting them together would put the whole
    /// dataset back in memory while still looking as though a chunk size applied, which is the way this feature
    /// fails quietly.
    /// </para>
    /// </summary>
    private static async Task RunInChunksAsync(BacktestEngine engine, BacktestRunConfig run, int chunk, CancellationToken ct)
    {
        if (chunk <= 0)
        {
            throw new InvalidOperationException($"A chunk of {chunk} elements is not a chunk; leave it unset to read the whole period at once.");
        }

        List<IAsyncEnumerator<IData>> sources = [];
        try
        {
            foreach (BacktestDataConfig dataConfig in run.Data)
            {
                IAsyncEnumerator<IData> source = StreamFor(dataConfig, run, ct).GetAsyncEnumerator(ct);
                if (await source.MoveNextAsync().ConfigureAwait(false))
                {
                    sources.Add(source);
                }
                else
                {
                    await source.DisposeAsync().ConfigureAwait(false);
                }
            }

            List<IData> batch = new(chunk);
            bool ran = false;
            while (sources.Count > 0)
            {
                // The earliest head across the sources. With a handful of configs a scan is cheaper than a heap and
                // says plainly what it does.
                int earliest = 0;
                for (int i = 1; i < sources.Count; i++)
                {
                    if (sources[i].Current.CreatedTime < sources[earliest].Current.CreatedTime)
                    {
                        earliest = i;
                    }
                }

                batch.Add(sources[earliest].Current);
                if (!await sources[earliest].MoveNextAsync().ConfigureAwait(false))
                {
                    await sources[earliest].DisposeAsync().ConfigureAwait(false);
                    sources.RemoveAt(earliest);
                }

                if (batch.Count < chunk)
                {
                    continue;
                }

                engine.AddData(batch);
                engine.Run(run.Start, run.End, streaming: true);
                engine.ReleaseDispatched();
                batch = new List<IData>(chunk);
                ran = true;
            }

            if (batch.Count > 0)
            {
                engine.AddData(batch);
                engine.Run(run.Start, run.End, streaming: true);
                engine.ReleaseDispatched();
                ran = true;
            }

            if (!ran)
            {
                throw new InvalidOperationException("No data has been added to the backtest engine.");
            }
        }
        finally
        {
            foreach (IAsyncEnumerator<IData> source in sources)
            {
                await source.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>The catalog's streaming read for one data config, as the element type a run is fed.</summary>
    private static async IAsyncEnumerable<IData> StreamFor(BacktestDataConfig dataConfig, BacktestRunConfig run, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        MarketArchive catalog = new(dataConfig.CatalogPath);
        UnixNanos? start = dataConfig.Start ?? run.Start;
        UnixNanos? end = dataConfig.End ?? run.End;

        IAsyncEnumerable<IData> source = dataConfig.DataKind.ToLowerInvariant() switch
        {
            "quotes" => Cast(catalog.StreamQuoteTicksAsync(Require(dataConfig.MarketKey, dataConfig), start, end, ct)),
            "trades" => Cast(catalog.StreamTradeTicksAsync(Require(dataConfig.MarketKey, dataConfig), start, end, ct)),
            "bars" => Cast(catalog.StreamBarsAsync(dataConfig.CandleSeries ?? throw new InvalidOperationException("Bar data requires a bar type."), start, end, ct)),
            "book_deltas" => Cast(catalog.StreamOrderBookDeltasAsync(Require(dataConfig.MarketKey, dataConfig), start, end, ct)),
            "book_depth" => Cast(catalog.StreamOrderBookDepthAsync(Require(dataConfig.MarketKey, dataConfig), start, end, ct)),
            "funding" => Cast(catalog.StreamFundingRatesAsync(Require(dataConfig.MarketKey, dataConfig), start, end, ct)),
            _ => throw new InvalidOperationException($"Unknown data kind '{dataConfig.DataKind}'."),
        };

        await foreach (IData element in source.WithCancellation(ct).ConfigureAwait(false))
        {
            yield return element;
        }
    }

    private static async IAsyncEnumerable<IData> Cast<T>(IAsyncEnumerable<T> source) where T : IData
    {
        await foreach (T item in source.ConfigureAwait(false))
        {
            yield return item;
        }
    }

    /// <summary>
    /// Builds a configured engine without running it, for callers that want to inspect or drive it manually.
    /// </summary>
    public async Task<BacktestEngine> BuildEngineAsync(BacktestRunConfig run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        BacktestEngine engine = new(run.Engine, _loggerFactory);

        HashSet<MarketKey> marketKeys = new();
        foreach (BacktestDataConfig dataConfig in run.Data)
        {
            MarketKey? id = dataConfig.MarketKey ?? dataConfig.CandleSeries?.MarketKey;
            if (id is { } marketKey)
            {
                marketKeys.Add(marketKey);
            }
        }

        Dictionary<string, MarketArchive> catalogs = new(StringComparer.OrdinalIgnoreCase);
        foreach (BacktestDataConfig dataConfig in run.Data)
        {
            if (!catalogs.ContainsKey(dataConfig.CatalogPath))
            {
                catalogs[dataConfig.CatalogPath] = new MarketArchive(dataConfig.CatalogPath);
            }
        }

        foreach (MarketKey marketKey in marketKeys)
        {
            Instrument? instrument = catalogs.Values.Select(c => c.Instrument(marketKey)).FirstOrDefault(i => i is not null);
            if (instrument is null)
            {
                throw new InvalidOperationException($"Instrument {marketKey} not found in any configured catalog.");
            }

            engine.AddInstrument(instrument);
        }

        foreach (BacktestVenueConfig venue in run.Venues)
        {
            engine.AddVenue(venue.ToVenueConfig());
        }

        // A chunked run is fed from the catalog as it goes, so loading the whole period here would both defeat the
        // point and hand the engine every element twice - the second time out of order, because the chunks start
        // again at the beginning. Caught by the engine's own ordering check rather than by anybody noticing.
        foreach (BacktestDataConfig dataConfig in run.ChunkSize is null ? run.Data : [])
        {
            MarketArchive catalog = catalogs[dataConfig.CatalogPath];
            UnixNanos? start = dataConfig.Start ?? run.Start;
            UnixNanos? end = dataConfig.End ?? run.End;
            IEnumerable<IData> data = dataConfig.DataKind.ToLowerInvariant() switch
            {
                "quotes" => (await catalog.QuoteTicksAsync(Require(dataConfig.MarketKey, dataConfig), start, end, ct).ConfigureAwait(false)).Cast<IData>(),
                "trades" => (await catalog.TradeTicksAsync(Require(dataConfig.MarketKey, dataConfig), start, end, ct).ConfigureAwait(false)).Cast<IData>(),
                "bars" => (await catalog.BarsAsync(dataConfig.CandleSeries ?? throw new InvalidOperationException("Bar data requires a bar type."), start, end, ct).ConfigureAwait(false)).Cast<IData>(),
                "book_deltas" => (await catalog.OrderBookDeltasAsync(Require(dataConfig.MarketKey, dataConfig), start, end, ct).ConfigureAwait(false)).Cast<IData>(),
                "book_depth" => (await catalog.OrderBookDepthAsync(Require(dataConfig.MarketKey, dataConfig), start, end, ct).ConfigureAwait(false)).Cast<IData>(),
                "funding" => (await catalog.FundingRatesAsync(Require(dataConfig.MarketKey, dataConfig), start, end, ct).ConfigureAwait(false)).Cast<IData>(),
                _ => throw new InvalidOperationException($"Unknown data kind '{dataConfig.DataKind}'."),
            };
            engine.AddData(data);
        }

        foreach (RuntimeModuleDefinition definition in run.RuntimeModules)
        {
            engine.AddRuntimeModule(_registry.CreateRuntimeModule(definition));
        }

        foreach (StrategyDefinition definition in run.Strategies)
        {
            Strategy strategy = _registry.CreateStrategy(definition);
            engine.AddStrategy(strategy);
        }

        _log.LogInformation("Built backtest engine {RunId}: {Venues} venues, {Instruments} instruments, {Data} data elements, {Strategies} strategies",
            run.Engine.RunId, run.Venues.Count, marketKeys.Count, engine.Data.Count, run.Strategies.Count);
        return engine;
    }

    private static MarketKey Require(MarketKey? id, BacktestDataConfig config) =>
        id ?? throw new InvalidOperationException($"Data config for '{config.DataKind}' requires an instrument id.");
}
