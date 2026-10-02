using Bytex.Core.Adapters;
using Bytex.Core.Caching;
using Bytex.Core.Engines;
using Bytex.Core.TradingRuntime;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Model.Reports;
using Bytex.Core.Plugins;
using Bytex.Core.Serialization;
using Bytex.Core.Timing;
using Bytex.Core.Trading;
using Bytex.Live.Persistence;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bytex.Live;

/// <summary>
/// Declares a data or execution client to build from a registered factory.
/// </summary>
public sealed record ClientEntry(string Factory, string ClientId, object Config);

public sealed record TradingNodeConfig
{
    public TradingRuntimeConfig TradingRuntime { get; init; } = new() { Environment = TradingEnvironment.Live };

    public IReadOnlyList<ClientEntry> DataClients { get; init; } = [];

    public IReadOnlyList<ClientEntry> ExecutionClients { get; init; } = [];

    public IReadOnlyList<StrategyDefinition> Strategies { get; init; } = [];

    /// <summary>
    /// How this node turns a path into a strategy, for one added while it runs (R5.10).
    ///
    /// <para>
    /// What a path means belongs to the host: a strategy document, a definition naming a plugin, something of its own.
    /// It is given as a function rather than built in because a node must not have to know about documents to be able
    /// to run one - and because a path, not a document, is what crosses a control channel. The node reads its own
    /// files, as it does with its keys.
    /// </para>
    /// </summary>
    public Func<string, Strategy>? StrategyFromPath { get; init; }

    public IReadOnlyList<RuntimeModuleDefinition> RuntimeModules { get; init; } = [];

    public IReadOnlyList<OrderScheduleDefinition> OrderSchedules { get; init; } = [];

    public bool ReconcileOnStart { get; init; } = true;

    public TimeSpan ReconciliationLookback { get; init; } = TimeSpan.FromDays(1);

    /// <summary>
    /// How often the node checks itself against its venues while it runs. Zero, the default, leaves it to the check at
    /// start-up: a mass status costs a request to every venue and some of them count those, so a host says how often
    /// it is worth paying for. What it buys is a node that notices a fill it never received, an order it did not know
    /// about and a position it is not carrying, rather than trading on a picture that has quietly gone stale.
    /// <para>
    /// Every check after the first looks back only as far as the one before it, with one interval of overlap, because
    /// what it is looking for is what has changed since; the check at start-up is the one that looks back over
    /// <see cref="ReconciliationLookback"/>.
    /// </para>
    /// </summary>
    public TimeSpan ReconciliationInterval { get; init; } = TimeSpan.Zero;

    public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public bool CancelOrdersOnStop { get; init; }

    public bool ClosePositionsOnStop { get; init; }

    public int QueueCapacity { get; init; } = 100_000;

    public string? PluginDirectory { get; init; }

    /// <summary>Interval for heartbeat log lines; zero disables them.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Starts the node with its trading halted: it runs, it sees data, and it places nothing until a host releases
    /// it. A supervising application that wants to look before anything trades starts the node this way.
    /// </summary>
    public bool StartHalted { get; init; }

    /// <summary>
    /// Subscribes quotes so that whatever watches this node has a moving price. A strategy on bars alone gives a
    /// monitor nothing but the last candle close, which between bars reads as a stalled node. The quotes are cached
    /// and reported; no strategy receives them, because a strategy is given the data it asked for and nothing else.
    /// </summary>
    public bool DisplayPrices { get; init; }

    /// <summary>
    /// The instruments to show a price for. Left empty, a node showing prices takes the instruments it was given -
    /// which is what a host adds for the strategies it runs - up to
    /// <see cref="TradingNode.MaxDisplayPriceInstruments"/>; past that it asks to be told, because a node holding a
    /// venue's whole catalogue must not open a quote stream per instrument to draw one price.
    /// </summary>
    public IReadOnlyList<MarketKey> DisplayPriceInstruments { get; init; } = [];

    /// <summary>Where the node keeps its journal and the state of its strategies; null keeps nothing.</summary>
    public NodeStoreConfig? Store { get; init; }
}

/// <summary>
/// A live (or sandbox) trading system: builds the tradingRuntime on a live clock, creates clients from factories,
/// reconciles with venues, runs strategies, and shuts down cleanly.
/// </summary>
public sealed class TradingNode : IAsyncDisposable
{
    private readonly TradingNodeConfig _config;
    private readonly PluginRegistry _registry;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _log;
    private readonly LiveTradingRuntimeLoop _loop;
    private readonly LiveClock _clock;
    private readonly TradingRuntime _tradingRuntime;
    private readonly NodeStore? _store;
    private readonly List<(string Topic, Action<object> Handler)> _journalTopics = new();
    private readonly List<IDataClient> _dataClients = new();
    private readonly List<IExecutionClient> _executionClients = new();
    private readonly List<Strategy> _strategies = new();
    private readonly List<MarketKey> _displayPrices = new();
    private bool _built;
    private bool _running;
    private bool _disposed;

    public TradingNode(TradingNodeConfig config, PluginRegistry? registry = null, ILoggerFactory? loggerFactory = null, ICacheDatabase? cacheDatabase = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _registry = registry ?? new PluginRegistry();
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _log = _loggerFactory.CreateLogger<TradingNode>();
        if (config.Store is { } storeConfig)
        {
            _store = new NodeStore(storeConfig, _loggerFactory);
            _loggerFactory = new JournalLoggerFactory(_loggerFactory, _store, () => UnixNanos.FromDateTimeOffset(DateTimeOffset.UtcNow));
            _log = _loggerFactory.CreateLogger<TradingNode>();
        }

        _loop = new LiveTradingRuntimeLoop(config.QueueCapacity, _loggerFactory);
        _clock = new LiveClock(handler => _loop.Post(handler.Handle));
        _tradingRuntime = new TradingRuntime(config.TradingRuntime, _clock, _loggerFactory, cacheDatabase, _loop.Post);

        if (config.PluginDirectory is { } pluginDir)
        {
            foreach (IPlugin plugin in PluginLoader.LoadFromDirectory(pluginDir))
            {
                _registry.AddPlugin(plugin);
            }
        }
    }

    public TradingNodeConfig Config => _config;

    public TradingRuntime TradingRuntime => _tradingRuntime;

    public PluginRegistry Registry => _registry;

    public LiveTradingRuntimeLoop Loop => _loop;

    /// <summary>The node's journal and saved state, or null when nothing is kept.</summary>
    public NodeStore? Store => _store;

    public ModuleHostId ModuleHostId => _tradingRuntime.ModuleHostId;

    public bool IsRunning => _running;

    public IReadOnlyList<IDataClient> DataClients => _dataClients;

    public IReadOnlyList<IExecutionClient> ExecutionClients => _executionClients;

    // ----- Prices for whoever is watching -----

    /// <summary>
    /// How many instruments a node subscribes display quotes for when it was not told which ones. A price to look at
    /// is worth one subscription an instrument, not a venue's whole catalogue.
    /// </summary>
    public const int MaxDisplayPriceInstruments = 25;

    /// <summary>The instruments this node is showing a price for; empty unless it was asked to show prices.</summary>
    public IReadOnlyList<MarketKey> DisplayPriceInstruments => _displayPrices;

    /// <summary>
    /// Subscribes the quotes a monitor needs to draw a moving price. Only the data command is sent: nothing
    /// subscribes to the quote topic on a strategy's behalf, so the ticks are cached and published for whoever is
    /// already listening and reach no strategy that did not ask for them.
    /// </summary>
    private void SubscribeDisplayPrices()
    {
        IReadOnlyList<MarketKey> named = _config.DisplayPriceInstruments;
        if (named.Count == 0)
        {
            List<MarketKey> held = _tradingRuntime.Cache.Instruments().Select(i => i.Id).Distinct().ToList();
            if (held.Count > MaxDisplayPriceInstruments)
            {
                _log.LogError(
                    "Display prices: this node holds {Count} instruments, more than the {Max} it will subscribe unasked. Name the instruments to show in DisplayPriceInstruments.",
                    held.Count, MaxDisplayPriceInstruments);
                return;
            }

            named = held;
        }

        if (named.Count == 0)
        {
            _log.LogInformation("Display prices: this node holds no instruments, so there is no price to show");
            return;
        }

        foreach (MarketKey marketKey in named)
        {
            _tradingRuntime.MessageBus.Send(Core.Model.Commands.Endpoints.MarketDataServiceExecute,
                new SubscribeQuoteTicks(marketKey, null, Guid.NewGuid(), _tradingRuntime.Clock.Timestamp));
            _displayPrices.Add(marketKey);
        }

        if (_displayPrices.Count > 0)
        {
            _log.LogInformation("Display prices: quotes subscribed for {Instruments}", string.Join(", ", _displayPrices));

            // A sandbox venue matches on the data it sees, quotes included, so saying so is better than letting
            // someone wonder why the same run fills differently once it has a price to look at.
            if (_tradingRuntime.Environment == TradingEnvironment.Sandbox)
            {
                _log.LogWarning("Display prices: this is a sandbox node, so its venue matches on these quotes as well as on the bars");
            }
        }
    }

    /// <summary>Gives the display quotes back, so a node that stops leaves no subscription behind it.</summary>
    private void UnsubscribeDisplayPrices()
    {
        foreach (MarketKey marketKey in _displayPrices)
        {
            _tradingRuntime.MessageBus.Send(Core.Model.Commands.Endpoints.MarketDataServiceExecute,
                new UnsubscribeQuoteTicks(marketKey, null, Guid.NewGuid(), _tradingRuntime.Clock.Timestamp));
        }

        _displayPrices.Clear();
    }

    // ----- The switch -----

    /// <summary>What the node's risk engine is doing with orders: active, reducing only, or halted.</summary>
    public TradingState TradingState => _tradingRuntime.OrderPolicy.TradingState;

    /// <summary>Whether the node is halted: it is running, and it is placing nothing.</summary>
    public bool IsHalted => TradingState == TradingState.Halted;

    /// <summary>
    /// Stops the node trading without stopping the node. Every order a strategy submits from here is denied with
    /// TRADING_HALTED, and the strategies keep running, keep their state and keep seeing data, so a host can look at
    /// what happened and let the node go again. What is already resting is left alone unless it is asked for:
    /// <paramref name="cancelOrders"/> takes the resting orders back and <paramref name="closePositions"/> flattens
    /// with reduce-only market orders, both of which happen before the halt is in force - a halt that denied the
    /// orders closing the book would be a switch nobody could use.
    /// </summary>
    public void Halt(bool cancelOrders = false, bool closePositions = false, string? reason = null)
    {
        // On the tradingRuntime thread, like every other command a host sends: it reads the cache and submits orders.
        _loop.Post(() =>
        {
            if (cancelOrders || closePositions)
            {
                _tradingRuntime.OrderPolicy.SetTradingState(TradingState.Reducing);
                foreach (Strategy strategy in _tradingRuntime.ModuleHost.Strategies)
                {
                    ShutdownHelper.Flatten(strategy, _tradingRuntime, cancelOrders, closePositions);
                }
            }

            _tradingRuntime.OrderPolicy.SetTradingState(TradingState.Halted);
            _log.LogWarning("Trading node {ModuleHostId} halted{Reason} (cancelOrders={Cancel}, closePositions={Close})",
                ModuleHostId, reason is null ? string.Empty : ": " + reason, cancelOrders, closePositions);
        });
    }

    /// <summary>Lets a halted node trade again. A node that was not halted is left as it is.</summary>
    public void Resume()
    {
        _loop.Post(() =>
        {
            if (_tradingRuntime.OrderPolicy.TradingState == TradingState.Active)
            {
                return;
            }

            _tradingRuntime.OrderPolicy.SetTradingState(TradingState.Active);
            _log.LogWarning("Trading node {ModuleHostId} released", ModuleHostId);
        });
    }

    /// <summary>
    /// Replaces what the node's risk engine enforces - the loss and exposure limits and the caps - while it runs.
    /// This is how a host applies a limit to a node it did not configure, without restarting a strategy.
    /// </summary>
    public void SetLimits(RiskLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        _loop.Post(() => _tradingRuntime.OrderPolicy.SetLimits(limits));
    }

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
            EnsureVenueDoes(entry, config, c => c.MarketData, "market data");
            IDataClient client = factory.Create(new ClientId(entry.ClientId), config, _tradingRuntime.Services);
            AddDataClient(client);
        }

        foreach (ClientEntry entry in _config.ExecutionClients)
        {
            if (!_registry.ExecutionClientFactories.TryGetValue(entry.Factory, out IExecutionClientFactory? factory))
            {
                throw new InvalidOperationException($"No execution client factory registered for '{entry.Factory}'.");
            }

            ExecutionClientConfig config = CoerceConfig<ExecutionClientConfig>(entry.Config, factory.ConfigType);
            EnsureVenueDoes(entry, config, c => c.Execution, "execution");
            IExecutionClient client = factory.Create(new ClientId(entry.ClientId), config, _tradingRuntime.Services);
            AddExecutionClient(client);
        }

        foreach (RuntimeModuleDefinition definition in _config.RuntimeModules)
        {
            _tradingRuntime.ModuleHost.AddRuntimeModule(_registry.CreateRuntimeModule(definition));
        }

        foreach (OrderScheduleDefinition definition in _config.OrderSchedules)
        {
            _tradingRuntime.ModuleHost.AddOrderSchedule(_registry.CreateOrderSchedule(definition));
        }

        foreach (StrategyDefinition definition in _config.Strategies)
        {
            AddStrategy(_registry.CreateStrategy(definition));
        }

        _built = true;
    }

    /// <summary>
    /// Refuses a client whose venue family does not declare what it is being built to do.
    /// <para>
    /// A host reads the declaration and does not offer what a venue will not do, which is the better message and the
    /// earlier one. It is not the only door: a node assembled by hand, or from a configuration file, or by a script
    /// arrives here having read nothing, and would start, connect and then be quiet for a reason appearing in no
    /// report. So the same fact is checked where every node passes, whatever built it.
    /// </para>
    /// <para>
    /// The venue is found by name among the registered plugins. A venue that declares nothing - a history service,
    /// or a plugin that brings no venue at all - is not checked, because there is nothing to check it against.
    /// </para>
    /// </summary>
    private void EnsureVenueDoes(ClientEntry entry, object config, Func<VenueCapabilities, bool> capability, string what)
    {
        VenueDescriptor? venue = _registry.Plugins
            .OfType<IVenuePlugin>()
            .Select(p => p.Describe())
            .FirstOrDefault(v => string.Equals(v.Venue.Value, entry.Factory, StringComparison.OrdinalIgnoreCase));

        if (venue is not null)
        {
            CapabilityGuard.EnsureDeclared(venue, config, capability, what, entry.ClientId);
        }
    }

    private static T CoerceConfig<T>(object config, Type expected) where T : class
    {
        if (config is T typed && expected.IsInstanceOfType(config))
        {
            return typed;
        }

        if (config is System.Text.Json.JsonElement element)
        {
            // Strict, because this is the part of a node's configuration a person writes by hand and the part where
            // being wrong is most expensive: a client's own settings. A member this client does not have is a setting
            // somebody believed they had made, and skipping it silently is how `"testnet": true` produced a node that
            // traded the real venue without a word. The type is named in the message because "unknown member" alone
            // does not tell anybody which of a node's clients was misconfigured.
            object? deserialized;
            try
            {
                deserialized = System.Text.Json.JsonSerializer.Deserialize(element.GetRawText(), expected, Core.Serialization.BytexJson.Strict);
            }
            catch (System.Text.Json.JsonException e)
            {
                throw new InvalidOperationException(
                    $"This client's configuration has a setting {expected.Name} does not have, so nothing would have read it: {e.Message} "
                    + "Remove it, or correct its spelling. A setting this engine does not know is not a setting.", e);
            }

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
        _tradingRuntime.MarketDataService.RegisterClient(client);
        client.AttachSink(new DispatchingDataSink(_tradingRuntime.MarketDataService, _loop));
        _dataClients.Add(client);
    }

    /// <summary>
    /// Registers an execution client built outside configuration.
    /// </summary>
    public void AddExecutionClient(IExecutionClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _tradingRuntime.OrderCoordinator.RegisterClient(client);
        client.AttachSink(new DispatchingExecutionSink(_tradingRuntime.OrderCoordinator, _loop));
        _executionClients.Add(client);
    }

    public void AddStrategy(Strategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        _tradingRuntime.ModuleHost.AddStrategy(strategy);
        _strategies.Add(strategy);
    }

    /// <summary>
    /// Takes a strategy off this node (R5.10). It has to be stopped first; see <see cref="Control.NodeController"/>,
    /// which is what a host uses and which says what happens to the orders and positions it was holding.
    /// </summary>
    /// <returns>Whether there was such a strategy to remove.</returns>
    public bool RemoveStrategy(StrategyId id)
    {
        if (!_tradingRuntime.ModuleHost.RemoveStrategy(id))
        {
            return false;
        }

        _strategies.RemoveAll(s => s.StrategyId == id);
        return true;
    }

    public void AddRuntimeModule(RuntimeModule runtimeModule) => _tradingRuntime.ModuleHost.AddRuntimeModule(runtimeModule);

    public void AddInstrument(Instrument instrument) => _tradingRuntime.Cache.AddInstrument(instrument);

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
        _log.LogInformation("Starting trading node {ModuleHostId} ({Environment})", ModuleHostId, _tradingRuntime.Environment);

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_config.ConnectionTimeout);

        // What has connected so far. A start that fails leaves the node as it was found, which means closing these:
        // until now a node whose second client timed out kept the first one's socket open, and neither StopAsync nor
        // DisposeAsync would close it, because a node that never finished starting was never "running".
        List<IDataClient> connectedData = new();
        List<IExecutionClient> connectedExecution = new();
        try
        {
            foreach (IDataClient client in _dataClients)
            {
                await _loop.InvokeAsync(() => StartComponent(client)).ConfigureAwait(false);
                await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
                connectedData.Add(client);
            }

            foreach (IExecutionClient client in _executionClients)
            {
                await _loop.InvokeAsync(() => StartComponent(client)).ConfigureAwait(false);
                await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
                connectedExecution.Add(client);
            }

            if (_store is not null)
            {
                JournalEvents();
            }

            if (_config.ReconcileOnStart)
            {
                try
                {
                    await ReconcileAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    // Recorded before it is rethrown, so that a node which failed to start still says why on the
                    // page rather than only in a log a host may never read.
                    _tradingRuntime.OrderCoordinator.RecordReconciliationFailure(e.Message);
                    throw;
                }
            }

            if (_store is { RestoresState: true })
            {
                await _loop.InvokeAsync(RestoreSavedState).ConfigureAwait(false);
            }

            await _loop.InvokeAsync(_tradingRuntime.Start).ConfigureAwait(false);
            if (_config.DisplayPrices)
            {
                await _loop.InvokeAsync(SubscribeDisplayPrices).ConfigureAwait(false);
            }

            if (_config.StartHalted)
            {
                await _loop.InvokeAsync(() => _tradingRuntime.OrderPolicy.SetTradingState(TradingState.Halted)).ConfigureAwait(false);
                _log.LogWarning("Trading node {ModuleHostId} starts halted: it will place nothing until it is released", ModuleHostId);
            }

            if (_config.HeartbeatInterval > TimeSpan.Zero)
            {
                _clock.SetTimer("node-heartbeat", _config.HeartbeatInterval, callback: _ => Heartbeat());
            }

            if (_config.ReconciliationInterval > TimeSpan.Zero)
            {
                _clock.SetTimer(ReconcileTimer, _config.ReconciliationInterval, callback: _ => ReconcileAgain());
                _log.LogInformation("Trading node {ModuleHostId} will check itself against its venues every {Interval}", ModuleHostId, _config.ReconciliationInterval);
            }

            if (_store is not null && _config.Store!.SaveInterval > TimeSpan.Zero)
            {
                _clock.SetTimer(SaveTimer, _config.Store.SaveInterval, callback: _ => SaveStateNow());
                _log.LogInformation("Trading node {ModuleHostId} will save what its strategies know every {Interval}", ModuleHostId, _config.Store.SaveInterval);
            }
        }
        catch (Exception e)
        {
            _log.LogError(e, "Trading node {ModuleHostId} failed to start; disconnecting what had connected", ModuleHostId);
            await AbandonStartAsync(connectedData, connectedExecution).ConfigureAwait(false);
            throw;
        }

        _running = true;
        _log.LogInformation("Trading node {ModuleHostId} running with {Strategies} strategies", ModuleHostId, _tradingRuntime.ModuleHost.Strategies.Count);
    }

    /// <summary>
    /// Puts back what a failed start had already done: the clients that connected are disconnected and the heartbeat
    /// is cancelled. The tradingRuntime loop is left running and idle, because its thread cannot be started a second time and
    /// a caller may well try to start again; disposing the node ends it. Nothing here throws - the caller is already
    /// carrying an exception, and a failure to clean up is logged rather than hiding what went wrong first.
    /// </summary>
    private async Task AbandonStartAsync(List<IDataClient> connectedData, List<IExecutionClient> connectedExecution)
    {
        _clock.CancelTimer("node-heartbeat");
        _clock.CancelTimer(ReconcileTimer);
        _clock.CancelTimer(SaveTimer);

        foreach (IExecutionClient client in connectedExecution)
        {
            try
            {
                await client.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _log.LogWarning(e, "Execution client {ClientId} failed to disconnect after a failed start", client.ClientId);
            }
        }

        foreach (IDataClient client in connectedData)
        {
            try
            {
                await client.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _log.LogWarning(e, "Data client {ClientId} failed to disconnect after a failed start", client.ClientId);
            }
        }

    }

    private static void StartComponent(Core.Common.IComponent component)
    {
        if (component.State is ComponentState.Ready or ComponentState.Stopped)
        {
            component.Start();
        }
    }

    private const string ReconcileTimer = "node-reconcile";

    private const string SaveTimer = "node-save";

    /// <summary>What the clock's repeating timers are filed under in the node's own state.</summary>
    private const string ClockState = "clock";

    /// <summary>Whether a check is under way: 1 while it is. A check that overlaps the one before it asks a venue the
    /// same question twice and applies the older answer second, so one runs at a time.</summary>
    private int _reconciling;

    /// <summary>Where the last check looked back to, so the next one carries on from there.</summary>
    private UnixNanos? _reconciledTo;

    /// <summary>
    /// The periodic check. It runs on the tradingRuntime thread, where the cache can be read, and does the venue calls on a
    /// thread of its own, because the tradingRuntime thread must not wait on a network.
    /// </summary>
    private void ReconcileAgain()
    {
        if (!_running)
        {
            return;
        }

        // An order the node has sent and not yet heard back about is not a disagreement with the venue, it is a
        // conversation in progress. Reconciling over the top of it would adopt a position the fill already covers.
        int inflight = _tradingRuntime.Cache.OrdersInflight().Count;
        if (inflight > 0)
        {
            _log.LogDebug("Skipping the check against the venues: {Inflight} order(s) are in flight", inflight);
            return;
        }

        if (Interlocked.CompareExchange(ref _reconciling, 1, 0) != 0)
        {
            _log.LogDebug("Skipping the check against the venues: the one before it has not finished");
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _log.LogError(e, "The periodic check against the venues failed");

                // Counted, not only logged: a node whose every check fails otherwise looks exactly like one
                // reconciling cleanly with nothing to correct - checks nothing, differences nothing, running.
                _tradingRuntime.OrderCoordinator.RecordReconciliationFailure(e.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _reconciling, 0);
            }
        });
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        // The first check looks back over the whole lookback; every one after it carries on from where the last one
        // looked, with an interval of overlap so nothing falls between two checks.
        UnixNanos now = _clock.Timestamp;
        UnixNanos since = _reconciledTo is { } last && _config.ReconciliationInterval > TimeSpan.Zero
            ? last - _config.ReconciliationInterval
            : now - _config.ReconciliationLookback;
        _reconciledTo = now;
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

                await _loop.InvokeAsync(() => _tradingRuntime.OrderCoordinator.ReconcileMassStatus(status)).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.LogError(e, "Reconciliation with {ClientId} failed", client.ClientId);
            }
        }
    }

    // Everything a supervisor would need to explain a run afterwards: the orders, the positions, the account and whatever
    // a strategy published about its own decisions. Journal handlers run on the tradingRuntime thread, so they only enqueue.
    private void JournalEvents()
    {
        NodeStore store = _store!;
        Journal(Topics.AllOrderEvents, "orderEvent");
        Journal(Topics.AllPositionEvents, "positionEvent");
        Journal(Topics.AllAccountEvents, "accountEvent");
        Journal("data.custom.*", "strategyData");

        void Journal(string topic, string kind)
        {
            void Handler(object message) => store.Append(new JournalRecord(_clock.Timestamp, kind, Topic: topic,
                Source: message.GetType().Name, Message: message.ToString(), Payload: Payload(message)));

            _tradingRuntime.MessageBus.Subscribe(topic, Handler);
            _journalTopics.Add((topic, Handler));
        }

        JsonElement? Payload(object message)
        {
            try
            {
                return JsonSerializer.SerializeToElement(message, message.GetType(), BytexJson.Options);
            }
            catch (Exception e) when (e is JsonException or NotSupportedException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// What every runtimeModule and strategy knows, read on the tradingRuntime loop because that is the only thread allowed to ask
    /// them. Writing it is the caller's business and happens off the loop: a node must not stall because a disk is
    /// slow, which is the rule the journal already follows.
    /// </summary>
    private List<(RuntimeModuleId Id, IDictionary<string, byte[]> State)> CollectState()
    {
        List<(RuntimeModuleId, IDictionary<string, byte[]>)> collected = [];
        foreach (RuntimeModule runtimeModule in _tradingRuntime.ModuleHost.Strategies.Cast<RuntimeModule>().Concat(_tradingRuntime.ModuleHost.RuntimeModules))
        {
            try
            {
                collected.Add((runtimeModule.RuntimeModuleId, runtimeModule.Save()));
            }
            catch (Exception e) when (e is InvalidOperationException or NotSupportedException or JsonException)
            {
                // One runtimeModule that cannot describe itself must not cost the others their memory.
                _log.LogError(e, "State of {RuntimeModuleId} could not be collected", runtimeModule.RuntimeModuleId);
            }
        }

        return collected;
    }

    /// <summary>
    /// The periodic save. It runs while the node runs, so what a crash loses is bounded by the interval rather than
    /// being everything since the node started.
    /// </summary>
    private void SaveStateNow()
    {
        if (!_running || _store is null)
        {
            return;
        }

        SaveClock();

        _ = Task.Run(async () =>
        {
            try
            {
                foreach ((RuntimeModuleId id, IDictionary<string, byte[]> state) in await _loop.InvokeAsync(CollectState).ConfigureAwait(false))
                {
                    _store.SaveState(id, state);
                }
            }
            catch (Exception e) when (e is InvalidOperationException or OperationCanceledException or ObjectDisposedException)
            {
                // A save that lost a race with a stop is not a failure worth alarming anybody about: the stop path
                // writes the same state before it returns.
                _log.LogDebug(e, "A periodic save did not complete; the node is stopping");
            }
        });
    }

    /// <summary>
    /// Where the clock's repeating timers stood, so that a schedule survives the process. A strategy re-registers its
    /// timers as it starts and <c>SetTimer</c> anchors an interval to "now" when nothing says otherwise - so without
    /// this a four-hourly rule on a node restarted at 03:59 next runs at 07:59 rather than 04:00, and drifts again at
    /// every restart.
    /// </summary>
    private void SaveClock()
    {
        try
        {
            _store!.SaveComponentState(ClockState, JsonSerializer.Serialize(_clock.Schedules(), BytexJson.Options));
        }
        catch (Exception e) when (e is NotSupportedException or JsonException)
        {
            _log.LogError(e, "The clock's schedules could not be saved; timers will start from now if this node restarts");
        }
    }

    /// <summary>
    /// Read before the tradingRuntime starts, because that is when the components owning those timers set them. Firings that
    /// fell while the node was down are not replayed - acting on a four-hour rule hours late acts on a market that
    /// has moved - so the clock lands on the first boundary still ahead and reports how many it passed over.
    /// </summary>
    private void RestoreClock()
    {
        if (_store!.LoadComponentState(ClockState) is not { } json)
        {
            return;
        }

        try
        {
            if (JsonSerializer.Deserialize<Dictionary<string, TimerSchedule>>(json, BytexJson.Options) is { Count: > 0 } schedules)
            {
                _clock.Resume(schedules);
                _log.LogInformation("Trading node {ModuleHostId} resumes {Count} timer schedule(s) where they stood", ModuleHostId, schedules.Count);
            }
        }
        catch (JsonException e)
        {
            _log.LogError(e, "The clock's saved schedules could not be read; timers start from now");
        }
    }

    // Saved state goes into the cache first, which is where an runtimeModule reads it from, and only then is the runtimeModule told to
    // load it: a strategy that saved nothing, or whose state cannot be read, simply starts fresh.
    private void RestoreSavedState()
    {
        RestoreClock();
        foreach (RuntimeModule runtimeModule in _tradingRuntime.ModuleHost.Strategies.Cast<RuntimeModule>().Concat(_tradingRuntime.ModuleHost.RuntimeModules))
        {
            if (_store!.LoadState(runtimeModule.RuntimeModuleId) is not { } state)
            {
                continue;
            }

            try
            {
                _tradingRuntime.Cache.SaveRuntimeModuleState(runtimeModule.RuntimeModuleId, state);
                runtimeModule.Load();
                _log.LogInformation("Restored the saved state of {RuntimeModuleId}", runtimeModule.RuntimeModuleId);
            }
            catch (Exception e) when (e is InvalidOperationException or NotSupportedException or JsonException or FormatException)
            {
                _log.LogError(e, "Saved state of {RuntimeModuleId} could not be restored; it runs without it", runtimeModule.RuntimeModuleId);
            }
        }
    }

    private void Heartbeat() =>
        _log.LogInformation("Heartbeat: queue {Pending} pending / {Processed} processed, orders open {Open}, positions open {Positions}, data {Data}, events {Events}",
            _loop.Pending, _loop.Processed, _tradingRuntime.Cache.OrdersOpenCount(), _tradingRuntime.Cache.PositionsOpenCount(), _tradingRuntime.MarketDataService.DataCount, _tradingRuntime.OrderCoordinator.EventCount);

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (!_running)
        {
            return;
        }

        _log.LogInformation("Stopping trading node {ModuleHostId}", ModuleHostId);
        _clock.CancelTimer("node-heartbeat");
        _clock.CancelTimer(ReconcileTimer);
        _clock.CancelTimer(SaveTimer);

        if (_config.CancelOrdersOnStop || _config.ClosePositionsOnStop)
        {
            await _loop.InvokeAsync(() =>
            {
                foreach (Strategy strategy in _tradingRuntime.ModuleHost.Strategies)
                {
                    ShutdownHelper.Flatten(strategy, _tradingRuntime, _config.CancelOrdersOnStop, _config.ClosePositionsOnStop);
                }
            }).ConfigureAwait(false);

            // Give venues a moment to acknowledge.
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }

        if (_displayPrices.Count > 0)
        {
            await _loop.InvokeAsync(UnsubscribeDisplayPrices).ConfigureAwait(false);
        }

        if (_store is not null)
        {
            SaveClock();
            foreach ((RuntimeModuleId id, IDictionary<string, byte[]> state) in await _loop.InvokeAsync(CollectState).ConfigureAwait(false))
            {
                _store.SaveState(id, state);
            }

            foreach ((string topic, Action<object> handler) in _journalTopics)
            {
                _tradingRuntime.MessageBus.Unsubscribe(topic, handler);
            }

            _journalTopics.Clear();
        }

        await _loop.InvokeAsync(_tradingRuntime.Stop).ConfigureAwait(false);

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
        _log.LogInformation("Trading node {ModuleHostId} stopped", ModuleHostId);
        if (_store is not null)
        {
            // The journal is complete by the time the node says it has stopped.
            await _store.FlushAsync().ConfigureAwait(false);
        }
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
        else
        {
            // A node that never finished starting still has a tradingRuntime thread waiting for work.
            try
            {
                await _loop.StopAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _log.LogWarning(e, "TradingRuntime loop failed to stop while disposing a node that was not running");
            }
        }

        if (_store is not null)
        {
            await _store.DisposeAsync().ConfigureAwait(false);
        }

        _tradingRuntime.Dispose();
        _loop.Dispose();
        _clock.Dispose();
    }
}

/// <summary>
/// Helpers for flattening strategies at shutdown without exposing protected strategy members.
/// </summary>
public static class ShutdownHelper
{
    public static void Flatten(Strategy strategy, TradingRuntime tradingRuntime, bool cancelOrders, bool closePositions)
    {
        ModuleHostId moduleHostId = tradingRuntime.ModuleHostId;
        UnixNanos now = tradingRuntime.Clock.Timestamp;
        if (cancelOrders)
        {
            foreach (MarketKey marketKey in tradingRuntime.Cache.OrdersOpen(strategyId: strategy.StrategyId).Select(o => o.MarketKey).Distinct().ToList())
            {
                tradingRuntime.MessageBus.Send(Core.Model.Commands.Endpoints.OrderPolicyExecute,
                    new Core.Model.Commands.CancelAllOrders(moduleHostId, strategy.StrategyId, marketKey, null, null, Guid.NewGuid(), now));
            }
        }

        if (closePositions)
        {
            OrderFactory factory = new(moduleHostId, strategy.StrategyId, tradingRuntime.Clock, tradingRuntime.Cache.Orders(strategyId: strategy.StrategyId).Count + 1000);
            foreach (Core.Model.Positions.Position position in tradingRuntime.Cache.PositionsOpen(strategyId: strategy.StrategyId))
            {
                Core.Model.Orders.MarketOrder order = factory.Market(position.MarketKey, position.Side.ClosingSide(), position.Quantity, reduceOnly: true, tags: ["SHUTDOWN"]);
                tradingRuntime.Cache.AddOrder(order, position.Id);
                tradingRuntime.MessageBus.Send(Core.Model.Commands.Endpoints.OrderPolicyExecute,
                    new Core.Model.Commands.SubmitOrder(moduleHostId, strategy.StrategyId, order, position.Id, null, null, Guid.NewGuid(), now));
            }
        }
    }
}
