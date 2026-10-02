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

namespace Bytex.Core.TradingRuntime;

public sealed record TradingRuntimeConfig
{
    public ModuleHostId ModuleHostId { get; init; } = new("TRADER-001");

    public TradingEnvironment Environment { get; init; } = TradingEnvironment.Backtest;

    public string InstanceId { get; init; } = Guid.NewGuid().ToString("N");

    public CacheConfig Cache { get; init; } = new();

    public MarketDataServiceConfig MarketDataService { get; init; } = new();

    public OrderPolicyConfig OrderPolicy { get; init; } = new();

    public OrderCoordinatorConfig OrderCoordinator { get; init; } = new();

    /// <summary>Load cached state from the configured database at startup.</summary>
    public bool LoadState { get; init; } = true;

    /// <summary>Save runtimeModule state to the cache database at shutdown.</summary>
    public bool SaveState { get; init; } = true;
}

/// <summary>
/// Builds and owns the engine components for one trading system instance.
/// </summary>
public sealed class TradingRuntime : IDisposable
{
    private readonly ILogger _log;
    private bool _disposed;

    public TradingRuntime(TradingRuntimeConfig config, IClock clock, ILoggerFactory? loggerFactory = null, ICacheDatabase? cacheDatabase = null, Action<Action>? post = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(clock);
        Config = config;
        Clock = clock;
        LoggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _log = LoggerFactory.CreateLogger<TradingRuntime>();

        MessageBus = new MessageBus(config.ModuleHostId, LoggerFactory);
        Cache = new Cache(config.Cache, cacheDatabase, LoggerFactory);
        Portfolio = new Portfolio(Cache, MessageBus);
        MarketDataService = new MarketDataService(MessageBus, Cache, config.MarketDataService, post);
        OrderPolicy = new OrderPolicy(MessageBus, Cache, Portfolio, config.OrderPolicy);
        OrderCoordinator = new OrderCoordinator(MessageBus, Cache, config.OrderCoordinator);
        ModuleHost = new ModuleHost(config.ModuleHostId, clock, Cache, MessageBus, Portfolio, OrderCoordinator, LoggerFactory, post, config.Environment);

        Portfolio.Initialize(clock, LoggerFactory);
        MarketDataService.Initialize(clock, LoggerFactory);
        OrderPolicy.Initialize(clock, LoggerFactory);
        OrderCoordinator.Initialize(clock, LoggerFactory);

        Services = new TradingRuntimeServices(clock, Cache, MessageBus, LoggerFactory, config.ModuleHostId, config.Environment);

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

    public TradingRuntimeConfig Config { get; }

    public ModuleHostId ModuleHostId => Config.ModuleHostId;

    public TradingEnvironment Environment => Config.Environment;

    public IClock Clock { get; }

    public ILoggerFactory LoggerFactory { get; }

    public MessageBus MessageBus { get; }

    public Cache Cache { get; }

    public Portfolio Portfolio { get; }

    public MarketDataService MarketDataService { get; }

    public OrderPolicy OrderPolicy { get; }

    public OrderCoordinator OrderCoordinator { get; }

    public ModuleHost ModuleHost { get; }

    public TradingRuntimeServices Services { get; }

    public bool IsRunning => ModuleHost.IsRunning;

    public void AddDataClient(IDataClient client) => MarketDataService.RegisterClient(client);

    public void AddExecutionClient(IExecutionClient client) => OrderCoordinator.RegisterClient(client);

    public void Start()
    {
        _log.LogInformation("Starting tradingRuntime {ModuleHostId} ({Environment}, instance {InstanceId})", ModuleHostId, Environment, Config.InstanceId);
        StartIfPossible(MarketDataService);
        StartIfPossible(OrderPolicy);
        StartIfPossible(OrderCoordinator);
        StartIfPossible(Portfolio);
        if (Config.LoadState)
        {
            ModuleHost.LoadState();
        }

        StartIfPossible(ModuleHost);
        _log.LogInformation("TradingRuntime {ModuleHostId} running", ModuleHostId);
    }

    public void Stop()
    {
        _log.LogInformation("Stopping tradingRuntime {ModuleHostId}", ModuleHostId);
        StopIfPossible(ModuleHost);
        if (Config.SaveState)
        {
            ModuleHost.SaveState();
        }

        StopIfPossible(OrderCoordinator);
        StopIfPossible(OrderPolicy);
        StopIfPossible(MarketDataService);
        StopIfPossible(Portfolio);
        Clock.CancelTimers();
        _log.LogInformation("TradingRuntime {ModuleHostId} stopped", ModuleHostId);
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

        ResetIfPossible(ModuleHost);
        ResetIfPossible(OrderCoordinator);
        ResetIfPossible(OrderPolicy);
        ResetIfPossible(MarketDataService);
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

        ModuleHost.Dispose();
        OrderCoordinator.Dispose();
        OrderPolicy.Dispose();
        MarketDataService.Dispose();
        Portfolio.Dispose();
        if (Clock is IDisposable disposableClock)
        {
            disposableClock.Dispose();
        }
    }
}
