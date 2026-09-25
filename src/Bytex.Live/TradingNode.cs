using Bytex.Core.Adapters;
using Bytex.Core.Caching;
using Bytex.Core.Engines;
using Bytex.Core.Kernel;
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
    public KernelConfig Kernel { get; init; } = new() { Environment = TradingEnvironment.Live };

    public IReadOnlyList<ClientEntry> DataClients { get; init; } = [];

    public IReadOnlyList<ClientEntry> ExecutionClients { get; init; } = [];

    public IReadOnlyList<StrategyDefinition> Strategies { get; init; } = [];

    public IReadOnlyList<ActorDefinition> Actors { get; init; } = [];

    public IReadOnlyList<ExecAlgorithmDefinition> ExecAlgorithms { get; init; } = [];

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
    public IReadOnlyList<InstrumentId> DisplayPriceInstruments { get; init; } = [];

    /// <summary>Where the node keeps its journal and the state of its strategies; null keeps nothing.</summary>
    public NodeStoreConfig? Store { get; init; }
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
    private readonly NodeStore? _store;
    private readonly List<(string Topic, Action<object> Handler)> _journalTopics = new();
    private readonly List<IDataClient> _dataClients = new();
    private readonly List<IExecutionClient> _executionClients = new();
    private readonly List<Strategy> _strategies = new();
    private readonly List<InstrumentId> _displayPrices = new();
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

    /// <summary>The node's journal and saved state, or null when nothing is kept.</summary>
    public NodeStore? Store => _store;

    public TraderId TraderId => _kernel.TraderId;

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
    public IReadOnlyList<InstrumentId> DisplayPriceInstruments => _displayPrices;

    /// <summary>
    /// Subscribes the quotes a monitor needs to draw a moving price. Only the data command is sent: nothing
    /// subscribes to the quote topic on a strategy's behalf, so the ticks are cached and published for whoever is
    /// already listening and reach no strategy that did not ask for them.
    /// </summary>
    private void SubscribeDisplayPrices()
    {
        IReadOnlyList<InstrumentId> named = _config.DisplayPriceInstruments;
        if (named.Count == 0)
        {
            List<InstrumentId> held = _kernel.Cache.Instruments().Select(i => i.Id).Distinct().ToList();
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

        foreach (InstrumentId instrumentId in named)
        {
            _kernel.MessageBus.Send(Core.Model.Commands.Endpoints.DataEngineExecute,
                new SubscribeQuoteTicks(instrumentId, null, Guid.NewGuid(), _kernel.Clock.Timestamp));
            _displayPrices.Add(instrumentId);
        }

        if (_displayPrices.Count > 0)
        {
            _log.LogInformation("Display prices: quotes subscribed for {Instruments}", string.Join(", ", _displayPrices));

            // A sandbox venue matches on the data it sees, quotes included, so saying so is better than letting
            // someone wonder why the same run fills differently once it has a price to look at.
            if (_kernel.Environment == TradingEnvironment.Sandbox)
            {
                _log.LogWarning("Display prices: this is a sandbox node, so its venue matches on these quotes as well as on the bars");
            }
        }
    }

    /// <summary>Gives the display quotes back, so a node that stops leaves no subscription behind it.</summary>
    private void UnsubscribeDisplayPrices()
    {
        foreach (InstrumentId instrumentId in _displayPrices)
        {
            _kernel.MessageBus.Send(Core.Model.Commands.Endpoints.DataEngineExecute,
                new UnsubscribeQuoteTicks(instrumentId, null, Guid.NewGuid(), _kernel.Clock.Timestamp));
        }

        _displayPrices.Clear();
    }

    // ----- The switch -----

    /// <summary>What the node's risk engine is doing with orders: active, reducing only, or halted.</summary>
    public TradingState TradingState => _kernel.RiskEngine.TradingState;

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
        // On the kernel thread, like every other command a host sends: it reads the cache and submits orders.
        _loop.Post(() =>
        {
            if (cancelOrders || closePositions)
            {
                _kernel.RiskEngine.SetTradingState(TradingState.Reducing);
                foreach (Strategy strategy in _kernel.Trader.Strategies)
                {
                    ShutdownHelper.Flatten(strategy, _kernel, cancelOrders, closePositions);
                }
            }

            _kernel.RiskEngine.SetTradingState(TradingState.Halted);
            _log.LogWarning("Trading node {TraderId} halted{Reason} (cancelOrders={Cancel}, closePositions={Close})",
                TraderId, reason is null ? string.Empty : ": " + reason, cancelOrders, closePositions);
        });
    }

    /// <summary>Lets a halted node trade again. A node that was not halted is left as it is.</summary>
    public void Resume()
    {
        _loop.Post(() =>
        {
            if (_kernel.RiskEngine.TradingState == TradingState.Active)
            {
                return;
            }

            _kernel.RiskEngine.SetTradingState(TradingState.Active);
            _log.LogWarning("Trading node {TraderId} released", TraderId);
        });
    }

    /// <summary>
    /// Replaces what the node's risk engine enforces - the loss and exposure limits and the caps - while it runs.
    /// This is how a host applies a limit to a node it did not configure, without restarting a strategy.
    /// </summary>
    public void SetLimits(RiskLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        _loop.Post(() => _kernel.RiskEngine.SetLimits(limits));
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
                await ReconcileAsync(timeout.Token).ConfigureAwait(false);
            }

            if (_store is { RestoresState: true })
            {
                await _loop.InvokeAsync(RestoreSavedState).ConfigureAwait(false);
            }

            await _loop.InvokeAsync(_kernel.Start).ConfigureAwait(false);
            if (_config.DisplayPrices)
            {
                await _loop.InvokeAsync(SubscribeDisplayPrices).ConfigureAwait(false);
            }

            if (_config.StartHalted)
            {
                await _loop.InvokeAsync(() => _kernel.RiskEngine.SetTradingState(TradingState.Halted)).ConfigureAwait(false);
                _log.LogWarning("Trading node {TraderId} starts halted: it will place nothing until it is released", TraderId);
            }

            if (_config.HeartbeatInterval > TimeSpan.Zero)
            {
                _clock.SetTimer("node-heartbeat", _config.HeartbeatInterval, callback: _ => Heartbeat());
            }

            if (_config.ReconciliationInterval > TimeSpan.Zero)
            {
                _clock.SetTimer(ReconcileTimer, _config.ReconciliationInterval, callback: _ => ReconcileAgain());
                _log.LogInformation("Trading node {TraderId} will check itself against its venues every {Interval}", TraderId, _config.ReconciliationInterval);
            }
        }
        catch (Exception e)
        {
            _log.LogError(e, "Trading node {TraderId} failed to start; disconnecting what had connected", TraderId);
            await AbandonStartAsync(connectedData, connectedExecution).ConfigureAwait(false);
            throw;
        }

        _running = true;
        _log.LogInformation("Trading node {TraderId} running with {Strategies} strategies", TraderId, _kernel.Trader.Strategies.Count);
    }

    /// <summary>
    /// Puts back what a failed start had already done: the clients that connected are disconnected and the heartbeat
    /// is cancelled. The kernel loop is left running and idle, because its thread cannot be started a second time and
    /// a caller may well try to start again; disposing the node ends it. Nothing here throws - the caller is already
    /// carrying an exception, and a failure to clean up is logged rather than hiding what went wrong first.
    /// </summary>
    private async Task AbandonStartAsync(List<IDataClient> connectedData, List<IExecutionClient> connectedExecution)
    {
        _clock.CancelTimer("node-heartbeat");
        _clock.CancelTimer(ReconcileTimer);

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

    /// <summary>Whether a check is under way: 1 while it is. A check that overlaps the one before it asks a venue the
    /// same question twice and applies the older answer second, so one runs at a time.</summary>
    private int _reconciling;

    /// <summary>Where the last check looked back to, so the next one carries on from there.</summary>
    private UnixNanos? _reconciledTo;

    /// <summary>
    /// The periodic check. It runs on the kernel thread, where the cache can be read, and does the venue calls on a
    /// thread of its own, because the kernel thread must not wait on a network.
    /// </summary>
    private void ReconcileAgain()
    {
        if (!_running)
        {
            return;
        }

        // An order the node has sent and not yet heard back about is not a disagreement with the venue, it is a
        // conversation in progress. Reconciling over the top of it would adopt a position the fill already covers.
        int inflight = _kernel.Cache.OrdersInflight().Count;
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

                await _loop.InvokeAsync(() => _kernel.ExecutionEngine.ReconcileMassStatus(status)).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.LogError(e, "Reconciliation with {ClientId} failed", client.ClientId);
            }
        }
    }

    // Everything a supervisor would need to explain a run afterwards: the orders, the positions, the account and whatever
    // a strategy published about its own decisions. Journal handlers run on the kernel thread, so they only enqueue.
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

            _kernel.MessageBus.Subscribe(topic, Handler);
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

    private void SaveState()
    {
        foreach (Actor actor in _kernel.Trader.Strategies.Cast<Actor>().Concat(_kernel.Trader.Actors))
        {
            try
            {
                _store!.SaveState(actor.ActorId, actor.Save());
            }
            catch (Exception e) when (e is InvalidOperationException or NotSupportedException or JsonException)
            {
                _log.LogError(e, "State of {ActorId} could not be collected", actor.ActorId);
            }
        }
    }

    // Saved state goes into the cache first, which is where an actor reads it from, and only then is the actor told to
    // load it: a strategy that saved nothing, or whose state cannot be read, simply starts fresh.
    private void RestoreSavedState()
    {
        foreach (Actor actor in _kernel.Trader.Strategies.Cast<Actor>().Concat(_kernel.Trader.Actors))
        {
            if (_store!.LoadState(actor.ActorId) is not { } state)
            {
                continue;
            }

            try
            {
                _kernel.Cache.SaveActorState(actor.ActorId, state);
                actor.Load();
                _log.LogInformation("Restored the saved state of {ActorId}", actor.ActorId);
            }
            catch (Exception e) when (e is InvalidOperationException or NotSupportedException or JsonException or FormatException)
            {
                _log.LogError(e, "Saved state of {ActorId} could not be restored; it runs without it", actor.ActorId);
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
        _clock.CancelTimer(ReconcileTimer);

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

        if (_displayPrices.Count > 0)
        {
            await _loop.InvokeAsync(UnsubscribeDisplayPrices).ConfigureAwait(false);
        }

        if (_store is not null)
        {
            await _loop.InvokeAsync(SaveState).ConfigureAwait(false);
            foreach ((string topic, Action<object> handler) in _journalTopics)
            {
                _kernel.MessageBus.Unsubscribe(topic, handler);
            }

            _journalTopics.Clear();
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
            // A node that never finished starting still has a kernel thread waiting for work.
            try
            {
                await _loop.StopAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _log.LogWarning(e, "Kernel loop failed to stop while disposing a node that was not running");
            }
        }

        if (_store is not null)
        {
            await _store.DisposeAsync().ConfigureAwait(false);
        }

        _kernel.Dispose();
        _loop.Dispose();
        _clock.Dispose();
    }
}

/// <summary>
/// Helpers for flattening strategies at shutdown without exposing protected strategy members.
/// </summary>
public static class ShutdownHelper
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
