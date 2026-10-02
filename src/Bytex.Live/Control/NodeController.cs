using Bytex.Core.Model;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Positions;
using Bytex.Core.Trading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bytex.Live.Control;

/// <summary>One strategy as a controller reports it.</summary>
/// <param name="Id">The strategy id.</param>
/// <param name="State">Its component state: <c>Ready</c>, <c>Running</c>, <c>Stopped</c> and so on.</param>
/// <param name="OpenOrders">How many of its orders are working at a venue.</param>
/// <param name="OpenPositions">How many positions it is holding.</param>
public sealed record StrategyStatus(string Id, string State, int OpenOrders, int OpenPositions);

/// <summary>
/// What a controller did, or why it would not.
///
/// <para>
/// A refusal is a sentence rather than an exception because the caller is usually an operator at the other end of the
/// control channel, and "this strategy is holding two positions" is the whole of what they need to decide what to do
/// next. The strategies are always included, so a host never has to ask again to see the result of its own command.
/// </para>
/// </summary>
public sealed record ControllerOutcome(bool Done, string? Refused, IReadOnlyList<StrategyStatus> Strategies)
{
    public static ControllerOutcome Ok(IReadOnlyList<StrategyStatus> strategies) => new(true, null, strategies);

    public static ControllerOutcome No(string reason, IReadOnlyList<StrategyStatus> strategies) => new(false, reason, strategies);
}

/// <summary>
/// Adds, starts, stops and removes strategies while a node runs (R5.10).
///
/// <para>
/// Everything here happens on the tradingRuntime thread, because every part of it reads or writes engine state: the moduleHost's
/// list, the execution engine's registrations, the cache's orders. A strategy is BUILT off that thread - reading a file
/// and constructing an object must not stall a trading loop - and only the registration is posted to it.
/// </para>
///
/// <para>
/// <b>What it will not decide for anybody.</b> A strategy is added stopped, and started when somebody says so: a node
/// that began trading with a strategy the moment it was uploaded would be trading on a decision nobody made. Stopping
/// takes the two flags every other command here takes, so what happens to the orders and positions it was managing is
/// said rather than assumed. And a strategy still holding orders or positions is not removed at all - removing it takes
/// away the thing that would manage them, and an operator who means that can cancel and flatten first, in one
/// <c>stop</c>.
/// </para>
/// </summary>
public sealed class NodeController
{
    private readonly TradingNode _node;
    private readonly ILogger _log;

    public NodeController(TradingNode node, ILoggerFactory? loggerFactory = null)
    {
        _node = node ?? throw new ArgumentNullException(nameof(node));
        _log = loggerFactory?.CreateLogger<NodeController>() ?? NullLogger<NodeController>.Instance;
    }

    /// <summary>Whether this node was given a way to read a strategy from a path.</summary>
    public bool CanReadStrategies => _node.Config.StrategyFromPath is not null;

    public Task<IReadOnlyList<StrategyStatus>> ListAsync() => _node.Loop.InvokeAsync(Statuses);

    /// <summary>
    /// Registers a strategy with the running node, stopped.
    /// </summary>
    public Task<ControllerOutcome> AddAsync(Strategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        return _node.Loop.InvokeAsync(() =>
        {
            if (Find(strategy.StrategyId) is not null)
            {
                return ControllerOutcome.No($"This node already has a strategy called {strategy.StrategyId}.", Statuses());
            }

            try
            {
                _node.AddStrategy(strategy);
            }
            catch (InvalidOperationException e)
            {
                // A clash of ids with an runtimeModule or an execution algorithm, or a claim on an instrument another strategy
                // already claims. Either way the operator gets the engine's own sentence.
                return ControllerOutcome.No(e.Message, Statuses());
            }

            _log.LogWarning("Strategy {StrategyId} added while the node runs; it is not started yet", strategy.StrategyId);
            return ControllerOutcome.Ok(Statuses());
        });
    }

    /// <summary>
    /// Reads a strategy from a path and registers it.
    ///
    /// <para>
    /// What a path means is the host's business: a strategy document, a definition naming a plugin, something of its
    /// own. The node is given a function that turns one into a strategy (<see cref="TradingNodeConfig.StrategyFromPath"/>),
    /// and a node that was not given one refuses rather than guessing. This is also why a path travels over the control
    /// channel and never a document: the node reads its own files, as it does with its keys.
    /// </para>
    /// </summary>
    public async Task<ControllerOutcome> AddFromPathAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (_node.Config.StrategyFromPath is not { } read)
        {
            return ControllerOutcome.No(
                "This node was not given a way to read a strategy from a path, so it cannot add one; set StrategyFromPath in its configuration.",
                await ListAsync().ConfigureAwait(false));
        }

        Strategy strategy;
        try
        {
            // Off the loop on purpose: reading a file and building an object must not stall a trading node.
            strategy = await Task.Run(() => read(path)).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException or FormatException)
        {
            _log.LogError(e, "A strategy could not be read from {Path}", path);
            return ControllerOutcome.No($"Nothing could be read from '{path}': {e.Message}", await ListAsync().ConfigureAwait(false));
        }

        return await AddAsync(strategy).ConfigureAwait(false);
    }

    public Task<ControllerOutcome> StartAsync(StrategyId id) => _node.Loop.InvokeAsync(() =>
    {
        if (Find(id) is not { } strategy)
        {
            return ControllerOutcome.No($"This node has no strategy called {id}.", Statuses());
        }

        if (strategy.State is ComponentState.Running or ComponentState.Degraded)
        {
            return ControllerOutcome.No($"{id} is already {strategy.State}.", Statuses());
        }

        try
        {
            strategy.Start();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A strategy that throws on start has not started, and the node goes on running without it.
            _log.LogError(e, "Strategy {StrategyId} failed to start", id);
            return ControllerOutcome.No($"{id} did not start: {e.Message}", Statuses());
        }

        _log.LogWarning("Strategy {StrategyId} started while the node runs", id);
        return ControllerOutcome.Ok(Statuses());
    });

    /// <summary>
    /// Stops a strategy, having first done whatever was asked about what it is holding.
    ///
    /// <para>
    /// The flatten happens BEFORE the stop, so the strategy is still running when its own cancels and closes come back
    /// to it. Stopping first would leave its last fills to a strategy that is no longer listening.
    /// </para>
    /// </summary>
    public Task<ControllerOutcome> StopAsync(StrategyId id, bool cancelOrders = false, bool closePositions = false) => _node.Loop.InvokeAsync(() =>
    {
        if (Find(id) is not { } strategy)
        {
            return ControllerOutcome.No($"This node has no strategy called {id}.", Statuses());
        }

        if (strategy.State is not (ComponentState.Running or ComponentState.Degraded))
        {
            return ControllerOutcome.No($"{id} is {strategy.State}, not running.", Statuses());
        }

        if (cancelOrders || closePositions)
        {
            ShutdownHelper.Flatten(strategy, _node.TradingRuntime, cancelOrders, closePositions);
        }

        try
        {
            strategy.Stop();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _log.LogError(e, "Strategy {StrategyId} failed to stop cleanly", id);
            return ControllerOutcome.No($"{id} did not stop cleanly: {e.Message}", Statuses());
        }

        // Saved on the way out, so a strategy started again is the one that was stopped rather than a fresh one.
        try
        {
            strategy.Save();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _log.LogError(e, "Strategy {StrategyId} stopped but its state was not saved", id);
        }

        _log.LogWarning("Strategy {StrategyId} stopped (cancelOrders={Cancel}, closePositions={Close})", id, cancelOrders, closePositions);
        return ControllerOutcome.Ok(Statuses());
    });

    /// <summary>
    /// Takes a strategy off the node.
    ///
    /// <para>
    /// Refused while it is running, and refused while it still holds orders or positions: removing it takes away the
    /// thing that would manage them, and nothing else on the node knows what they were for. An operator who means to
    /// let them go says so in the stop.
    /// </para>
    /// </summary>
    public Task<ControllerOutcome> RemoveAsync(StrategyId id) => _node.Loop.InvokeAsync(() =>
    {
        if (Find(id) is not { } strategy)
        {
            return ControllerOutcome.No($"This node has no strategy called {id}.", Statuses());
        }

        if (strategy.State is ComponentState.Running or ComponentState.Degraded)
        {
            return ControllerOutcome.No($"{id} is {strategy.State} and has to be stopped before it can be removed.", Statuses());
        }

        int orders = _node.TradingRuntime.Cache.OrdersOpen(strategyId: id).Count;
        int positions = _node.TradingRuntime.Cache.PositionsOpen(strategyId: id).Count;
        if (orders > 0 || positions > 0)
        {
            return ControllerOutcome.No(
                $"{id} still holds {orders} working order(s) and {positions} open position(s), and removing it would leave them to nobody. "
                + "Stop it with cancelOrders and closePositions, or close them by hand, and remove it then.",
                Statuses());
        }

        _node.RemoveStrategy(id);
        _log.LogWarning("Strategy {StrategyId} removed from the node", id);
        return ControllerOutcome.Ok(Statuses());
    });

    private Strategy? Find(StrategyId id) => _node.TradingRuntime.ModuleHost.Strategy(id);

    private IReadOnlyList<StrategyStatus> Statuses() =>
    [
        .. _node.TradingRuntime.ModuleHost.Strategies.Select(s => new StrategyStatus(
            s.StrategyId.Value,
            s.State.ToString(),
            _node.TradingRuntime.Cache.OrdersOpen(strategyId: s.StrategyId).Count,
            _node.TradingRuntime.Cache.PositionsOpen(strategyId: s.StrategyId).Count)),
    ];
}
