using Bytex.Core.Adapters;
using Bytex.Core.Caching;
using Bytex.Core.Kernel;
using Bytex.Core.Model;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Model.Reports;
using Bytex.Core.Plugins;
using Bytex.Core.Timing;
using Bytex.Core.Trading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bytex.Live;

/// <summary>
/// Declares a data or execution client to build from a registered factory.
/// </summary>
public sealed record ClientEntry(string Factory, string ClientId, object Config);

public sealed record TradingNodeConfig
{
    public KernelConfig Kernel { get; init; } = new() { Environment = TradingEnvironment.Live };

    public IReadOnlyList<ClientEntry> DataClients { get; init; } = [];

    public IReadOnlyList<ClientEntry> ExecutionClients { get; init; } = [];

    public IReadOnlyList<StrategyDefinition> Strategies { get; init; } = [];

    public IReadOnlyList<ActorDefinition> Actors { get; init; } = [];

    public IReadOnlyList<ExecAlgorithmDefinition> ExecAlgorithms { get; init; } = [];

    public bool ReconcileOnStart { get; init; } = true;

    public TimeSpan ReconciliationLookback { get; init; } = TimeSpan.FromDays(1);

    public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public bool CancelOrdersOnStop { get; init; }

    public bool ClosePositionsOnStop { get; init; }

    public int QueueCapacity { get; init; } = 100_000;

    public string? PluginDirectory { get; init; }

    /// <summary>Interval for heartbeat log lines; zero disables them.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// A live (or sandbox) trading system: builds the kernel on a live clock, creates clients from factories,
/// reconciles with venues, runs strategies, and shuts down cleanly.
/// </summary>
public sealed class TradingNode : IAsyncDisposable
{
    private readonly TradingNodeConfig _config;
    private readonly PluginRegistry _registry;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _log;
    private readonly LiveKernelLoop _loop;
    private readonly LiveClock _clock;
    private readonly Kernel _kernel;
    private readonly List<IDataClient> _dataClients = new();
    private readonly List<IExecutionClient> _executionClients = new();
    private readonly List<Strategy> _strategies = new();
    private bool _built;
    private bool _running;
    private bool _disposed;

    public TradingNode(TradingNodeConfig config, PluginRegistry? registry = null, ILoggerFactory? loggerFactory = null, ICacheDatabase? cacheDatabase = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _registry = registry ?? new PluginRegistry();
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _log = _loggerFactory.CreateLogger<TradingNode>();
        _loop = new LiveKernelLoop(config.QueueCapacity, _loggerFactory);
        _clock = new LiveClock(handler => _loop.Post(handler.Handle));
        _kernel = new Kernel(config.Kernel, _clock, _loggerFactory, cacheDatabase, _loop.Post);

        if (config.PluginDirectory is { } pluginDir)
        {
            foreach (IPlugin plugin in PluginLoader.LoadFromDirectory(pluginDir))
            {
                _registry.AddPlugin(plugin);
            }
        }
    }

    public TradingNodeConfig Config => _config;

    public Kernel Kernel => _kernel;

    public PluginRegistry Registry => _registry;

    public LiveKernelLoop Loop => _loop;

    public TraderId TraderId => _kernel.TraderId;

    public bool IsRunning => _running;

    public IReadOnlyList<IDataClient> DataClients => _dataClients;

    public IReadOnlyList<IExecutionClient> ExecutionClients => _executionClients;

    // ----- Building -----

    /// <summary>
    /// Creates clients and strategies from configuration. Called automatically by <see cref="StartAsync"/>.
    /// </summary>
    public void Build()
    {
        if (_built)
        {
            return;
        }

        foreach (ClientEntry entry in _config.DataClients)
        {
            if (!_registry.DataClientFactories.TryGetValue(entry.Factory, out IDataClientFactory? factory))
            {
                throw new InvalidOperationException($"No data client factory registered for '{entry.Factory}'.");
            }

            DataClientConfig config = CoerceConfig<DataClientConfig>(entry.Config, factory.ConfigType);
            IDataClient client = factory.Create(new ClientId(entry.ClientId), config, _kernel.Services);
            AddDataClient(client);
        }

        foreach (ClientEntry entry in _config.ExecutionClients)
        {
            if (!_registry.ExecutionClientFactories.TryGetValue(entry.Factory, out IExecutionClientFactory? factory))
            {
                throw new InvalidOperationException($"No execution client factory registered for '{entry.Factory}'.");
            }

            ExecutionClientConfig config = CoerceConfig<ExecutionClientConfig>(entry.Config, factory.ConfigType);
            IExecutionClient client = factory.Create(new ClientId(entry.ClientId), config, _kernel.Services);
            AddExecutionClient(client);
        }

        foreach (ActorDefinition definition in _config.Actors)
        {
            _kernel.Trader.AddActor(_registry.CreateActor(definition));
        }

        foreach (ExecAlgorithmDefinition definition in _config.ExecAlgorithms)
        {
            _kernel.Trader.AddExecAlgorithm(_registry.CreateExecAlgorithm(definition));
        }

        foreach (StrategyDefinition definition in _config.Strategies)
        {
            AddStrategy(_registry.CreateStrategy(definition));
        }

        _built = true;
    }

    private static T CoerceConfig<T>(object config, Type expected) where T : class
    {
        if (config is T typed && expected.IsInstanceOfType(config))
        {
            return typed;
        }

        if (config is System.Text.Json.JsonElement element)
        {
            object? deserialized = System.Text.Json.JsonSerializer.Deserialize(element.GetRawText(), expected, Core.Serialization.BytexJson.Options);
            if (deserialized is T fromJson)
            {
                return fromJson;
            }
        }

        throw new InvalidOperationException($"Client config of type {config.GetType().Name} cannot be used where {expected.Name} is expected.");
    }

    /// <summary>
    /// Registers a data client built outside configuration.
    /// </summary>
    public void AddDataClient(IDataClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _kernel.DataEngine.RegisterClient(client);
        client.AttachSink(new DispatchingDataSink(_kernel.DataEngine, _loop));
        _dataClients.Add(client);
    }

    /// <summary>
    /// Registers an execution client built outside configuration.
    /// </summary>
    public void AddExecutionClient(IExecutionClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _kernel.ExecutionEngine.RegisterClient(client);
        client.AttachSink(new DispatchingExecutionSink(_kernel.ExecutionEngine, _loop));
        _executionClients.Add(client);
    }

    public void AddStrategy(Strategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        _kernel.Trader.AddStrategy(strategy);
        _strategies.Add(strategy);
    }

    public void AddActor(Actor actor) => _kernel.Trader.AddActor(actor);

    public void AddInstrument(Instrument instrument) => _kernel.Cache.AddInstrument(instrument);

    // ----- Lifecycle -----

    public async Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_running)
        {
            return;
        }

        Build();
        _loop.Start();
        _log.LogInformation("Starting trading node {TraderId} ({Environment})", TraderId, _kernel.Environment);

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_config.ConnectionTimeout);

        foreach (IDataClient client in _dataClients)
        {
            await _loop.InvokeAsync(() => StartComponent(client)).ConfigureAwait(false);
            await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
        }

        foreach (IExecutionClient client in _executionClients)
        {
            await _loop.InvokeAsync(() => StartComponent(client)).ConfigureAwait(false);
            await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
        }

        if (_config.ReconcileOnStart)
        {
            await ReconcileAsync(timeout.Token).ConfigureAwait(false);
        }

        await _loop.InvokeAsync(_kernel.Start).ConfigureAwait(false);
        if (_config.HeartbeatInterval > TimeSpan.Zero)
        {
            _clock.SetTimer("node-heartbeat", _config.HeartbeatInterval, callback: _ => Heartbeat());
        }

        _running = true;
        _log.LogInformation("Trading node {TraderId} running with {Strategies} strategies", TraderId, _kernel.Trader.Strategies.Count);
    }

    private static void StartComponent(Core.Common.IComponent component)
    {
        if (component.State is ComponentState.Ready or ComponentState.Stopped)
        {
            component.Start();
        }
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        UnixNanos since = _clock.Timestamp - _config.ReconciliationLookback;
        foreach (IExecutionClient client in _executionClients)
        {
            try
            {
                ExecutionMassStatus? status = await client.GenerateMassStatusAsync(since, ct).ConfigureAwait(false);
                if (status is null)
                {
                    _log.LogInformation("Execution client {ClientId} provided no mass status; skipping reconciliation", client.ClientId);
                    continue;
                }

                await _loop.InvokeAsync(() => _kernel.ExecutionEngine.ReconcileMassStatus(status)).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.LogError(e, "Reconciliation with {ClientId} failed", client.ClientId);
            }
        }
    }

    private void Heartbeat() =>
        _log.LogInformation("Heartbeat: queue {Pending} pending / {Processed} processed, orders open {Open}, positions open {Positions}, data {Data}, events {Events}",
            _loop.Pending, _loop.Processed, _kernel.Cache.OrdersOpenCount(), _kernel.Cache.PositionsOpenCount(), _kernel.DataEngine.DataCount, _kernel.ExecutionEngine.EventCount);

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (!_running)
        {
            return;
        }

        _log.LogInformation("Stopping trading node {TraderId}", TraderId);
        _clock.CancelTimer("node-heartbeat");

        if (_config.CancelOrdersOnStop || _config.ClosePositionsOnStop)
        {
            await _loop.InvokeAsync(() =>
            {
                foreach (Strategy strategy in _kernel.Trader.Strategies)
                {
                    ShutdownHelper.Flatten(strategy, _kernel, _config.CancelOrdersOnStop, _config.ClosePositionsOnStop);
                }
            }).ConfigureAwait(false);

            // Give venues a moment to acknowledge.
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }

        await _loop.InvokeAsync(_kernel.Stop).ConfigureAwait(false);

        foreach (IExecutionClient client in _executionClients)
        {
            try
            {
                await client.DisconnectAsync(ct).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _log.LogWarning(e, "Execution client {ClientId} failed to disconnect", client.ClientId);
            }
        }

        foreach (IDataClient client in _dataClients)
        {
            try
            {
                await client.DisconnectAsync(ct).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _log.LogWarning(e, "Data client {ClientId} failed to disconnect", client.ClientId);
            }
        }

        await _loop.StopAsync().ConfigureAwait(false);
        _running = false;
        _log.LogInformation("Trading node {TraderId} stopped", TraderId);
    }

    /// <summary>
    /// Starts the node and runs until cancellation, then stops it.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        await StartAsync(ct).ConfigureAwait(false);
        try
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested.
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_running)
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }

        _kernel.Dispose();
        _loop.Dispose();
        _clock.Dispose();
    }
}

/// <summary>
/// Helpers for flattening strategies at shutdown without exposing protected strategy members.
/// </summary>
internal static class ShutdownHelper
{
    public static void Flatten(Strategy strategy, Kernel kernel, bool cancelOrders, bool closePositions)
    {
        TraderId traderId = kernel.TraderId;
        UnixNanos now = kernel.Clock.Timestamp;
        if (cancelOrders)
        {
            foreach (InstrumentId instrumentId in kernel.Cache.OrdersOpen(strategyId: strategy.StrategyId).Select(o => o.InstrumentId).Distinct().ToList())
            {
                kernel.MessageBus.Send(Core.Model.Commands.Endpoints.RiskEngineExecute,
                    new Core.Model.Commands.CancelAllOrders(traderId, strategy.StrategyId, instrumentId, null, null, Guid.NewGuid(), now));
            }
        }

        if (closePositions)
        {
            OrderFactory factory = new(traderId, strategy.StrategyId, kernel.Clock, kernel.Cache.Orders(strategyId: strategy.StrategyId).Count + 1000);
            foreach (Core.Model.Positions.Position position in kernel.Cache.PositionsOpen(strategyId: strategy.StrategyId))
            {
                Core.Model.Orders.MarketOrder order = factory.Market(position.InstrumentId, position.Side.ClosingSide(), position.Quantity, reduceOnly: true, tags: ["SHUTDOWN"]);
                kernel.Cache.AddOrder(order, position.Id);
                kernel.MessageBus.Send(Core.Model.Commands.Endpoints.RiskEngineExecute,
                    new Core.Model.Commands.SubmitOrder(traderId, strategy.StrategyId, order, position.Id, null, null, Guid.NewGuid(), now));
            }
        }
    }
}
