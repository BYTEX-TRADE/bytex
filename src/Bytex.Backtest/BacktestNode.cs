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

    public int FillModelSeed { get; init; } = 42;

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
