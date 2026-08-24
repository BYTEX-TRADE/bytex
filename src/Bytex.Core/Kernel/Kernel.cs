using Bytex.Core.Adapters;
using Bytex.Core.Caching;
using Bytex.Core.Common;
using Bytex.Core.Engines;
using Bytex.Core.Messaging;
using Bytex.Core.Model;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Portfolios;
using Bytex.Core.Timing;
using Bytex.Core.Trading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bytex.Core.Kernel;

public sealed record KernelConfig
{
    public TraderId TraderId { get; init; } = new("TRADER-001");

    public TradingEnvironment Environment { get; init; } = TradingEnvironment.Backtest;

    public string InstanceId { get; init; } = Guid.NewGuid().ToString("N");

    public CacheConfig Cache { get; init; } = new();

    public DataEngineConfig DataEngine { get; init; } = new();

    public RiskEngineConfig RiskEngine { get; init; } = new();

    public ExecutionEngineConfig ExecutionEngine { get; init; } = new();

    /// <summary>Load cached state from the configured database at startup.</summary>
    public bool LoadState { get; init; } = true;

    /// <summary>Save actor state to the cache database at shutdown.</summary>
    public bool SaveState { get; init; } = true;
}

/// <summary>
/// Builds and owns the engine components for one trading system instance.
/// </summary>
public sealed class Kernel : IDisposable
{
    private readonly ILogger _log;
    private bool _disposed;

    public Kernel(KernelConfig config, IClock clock, ILoggerFactory? loggerFactory = null, ICacheDatabase? cacheDatabase = null, Action<Action>? post = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(clock);
        Config = config;
        Clock = clock;
        LoggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _log = LoggerFactory.CreateLogger<Kernel>();

        MessageBus = new MessageBus(config.TraderId, LoggerFactory);
        Cache = new Cache(config.Cache, cacheDatabase, LoggerFactory);
        Portfolio = new Portfolio(Cache, MessageBus);
        DataEngine = new DataEngine(MessageBus, Cache, config.DataEngine);
        RiskEngine = new RiskEngine(MessageBus, Cache, Portfolio, config.RiskEngine);
        ExecutionEngine = new ExecutionEngine(MessageBus, Cache, config.ExecutionEngine);
        Trader = new Trader(config.TraderId, clock, Cache, MessageBus, Portfolio, ExecutionEngine, LoggerFactory, post);

        Portfolio.Initialize(clock, LoggerFactory);
        DataEngine.Initialize(clock, LoggerFactory);
        RiskEngine.Initialize(clock, LoggerFactory);
        ExecutionEngine.Initialize(clock, LoggerFactory);

        Services = new KernelServices(clock, Cache, MessageBus, LoggerFactory, config.TraderId, config.Environment);

        if (config.LoadState && cacheDatabase is not null)
        {
            if (config.Cache.FlushOnStart)
            {
                Cache.Flush();
            }
            else
            {
                Cache.LoadFromDatabase();
            }
        }
    }

    public KernelConfig Config { get; }

    public TraderId TraderId => Config.TraderId;

    public TradingEnvironment Environment => Config.Environment;

    public IClock Clock { get; }

    public ILoggerFactory LoggerFactory { get; }

    public MessageBus MessageBus { get; }

    public Cache Cache { get; }

    public Portfolio Portfolio { get; }

    public DataEngine DataEngine { get; }

    public RiskEngine RiskEngine { get; }

    public ExecutionEngine ExecutionEngine { get; }

    public Trader Trader { get; }

    public KernelServices Services { get; }

    public bool IsRunning => Trader.IsRunning;

    public void AddDataClient(IDataClient client) => DataEngine.RegisterClient(client);

    public void AddExecutionClient(IExecutionClient client) => ExecutionEngine.RegisterClient(client);

    public void Start()
    {
        _log.LogInformation("Starting kernel {TraderId} ({Environment}, instance {InstanceId})", TraderId, Environment, Config.InstanceId);
        StartIfPossible(DataEngine);
        StartIfPossible(RiskEngine);
        StartIfPossible(ExecutionEngine);
        StartIfPossible(Portfolio);
        if (Config.LoadState)
        {
            Trader.LoadState();
        }

        StartIfPossible(Trader);
        _log.LogInformation("Kernel {TraderId} running", TraderId);
    }

    public void Stop()
    {
        _log.LogInformation("Stopping kernel {TraderId}", TraderId);
        StopIfPossible(Trader);
        if (Config.SaveState)
        {
            Trader.SaveState();
        }

        StopIfPossible(ExecutionEngine);
        StopIfPossible(RiskEngine);
        StopIfPossible(DataEngine);
        StopIfPossible(Portfolio);
        Clock.CancelTimers();
        _log.LogInformation("Kernel {TraderId} stopped", TraderId);
    }

    /// <summary>
    /// Resets all components to their initial state while keeping registrations (clients, strategies).
    /// </summary>
    public void Reset()
    {
        if (IsRunning)
        {
            Stop();
        }

        ResetIfPossible(Trader);
        ResetIfPossible(ExecutionEngine);
        ResetIfPossible(RiskEngine);
        ResetIfPossible(DataEngine);
        Cache.Reset();
        Clock.CancelTimers();
    }

    private static void StartIfPossible(IComponent component)
    {
        if (component.State is ComponentState.Ready or ComponentState.Stopped)
        {
            component.Start();
        }
    }

    private static void StopIfPossible(IComponent component)
    {
        if (component.State is ComponentState.Running or ComponentState.Degraded)
        {
            component.Stop();
        }
    }

    private static void ResetIfPossible(IComponent component)
    {
        if (component.State is ComponentState.Ready or ComponentState.Stopped)
        {
            component.Reset();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (IsRunning)
        {
            Stop();
        }

        Trader.Dispose();
        ExecutionEngine.Dispose();
        RiskEngine.Dispose();
        DataEngine.Dispose();
        Portfolio.Dispose();
        if (Clock is IDisposable disposableClock)
        {
            disposableClock.Dispose();
        }
    }
}
