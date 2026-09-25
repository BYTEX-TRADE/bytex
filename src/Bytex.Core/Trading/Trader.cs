using Bytex.Core.Caching;
using Bytex.Core.Common;
using Bytex.Core.Engines;
using Bytex.Core.Messaging;
using Bytex.Core.Model;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Portfolios;
using Bytex.Core.Timing;
using Microsoft.Extensions.Logging;

namespace Bytex.Core.Trading;

/// <summary>
/// Hosts actors, strategies, and execution algorithms and drives their lifecycle as a group.
/// </summary>
public sealed class Trader : Component
{
    private readonly IClock _clock;
    private readonly ICache _cache;
    private readonly IMessageBus _bus;
    private readonly IPortfolio _portfolio;
    private readonly ExecutionEngine _executionEngine;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Action<Action>? _post;

    private readonly TradingEnvironment _environment;
    private readonly List<Actor> _actors = new();
    private readonly List<Strategy> _strategies = new();
    private readonly List<ExecAlgorithm> _execAlgorithms = new();

    /// <summary>
    /// Algorithms registered because a strategy asked for them rather than because the host said so. A host's own
    /// algorithm under the same id replaces one of these, so it does not matter which of the two was added first.
    /// </summary>
    private readonly HashSet<ExecAlgorithmId> _forStrategies = new();

    public Trader(TraderId traderId, IClock clock, ICache cache, IMessageBus bus, IPortfolio portfolio, ExecutionEngine executionEngine, ILoggerFactory loggerFactory, Action<Action>? post = null, TradingEnvironment environment = TradingEnvironment.Backtest)
        : base(new ComponentId(traderId.Value))
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(portfolio);
        ArgumentNullException.ThrowIfNull(executionEngine);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        TraderId = traderId;
        _clock = clock;
        _cache = cache;
        _bus = bus;
        _portfolio = portfolio;
        _executionEngine = executionEngine;
        _loggerFactory = loggerFactory;
        _environment = environment;
        _post = post;
        Initialize(clock, loggerFactory);
    }

    public TraderId TraderId { get; }

    public IReadOnlyList<Actor> Actors => _actors;

    public IReadOnlyList<Strategy> Strategies => _strategies;

    public IReadOnlyList<ExecAlgorithm> ExecAlgorithms => _execAlgorithms;

    public IEnumerable<Actor> AllComponents => _actors.Concat<Actor>(_execAlgorithms).Concat(_strategies);

    public void AddActor(Actor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor is Strategy strategy)
        {
            AddStrategy(strategy);
            return;
        }

        if (actor is ExecAlgorithm algorithm)
        {
            AddExecAlgorithm(algorithm);
            return;
        }

        EnsureUnique(actor.ActorId);
        actor.RunningIn = _environment;
        actor.Register(TraderId, _clock, _cache, _bus, _portfolio, _loggerFactory, _post);
        _actors.Add(actor);
        Log.LogInformation("Registered actor {ActorId}", actor.ActorId);
    }

    public void AddStrategy(Strategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        EnsureUnique(strategy.ActorId);
        strategy.RunningIn = _environment;
        strategy.Register(TraderId, _clock, _cache, _bus, _portfolio, _loggerFactory, _post);
        _executionEngine.RegisterOmsType(strategy.StrategyId, strategy.OmsType);
        if (strategy.ExternalOrderClaims.Count > 0)
        {
            _executionEngine.RegisterExternalOrderClaims(strategy.StrategyId, strategy.ExternalOrderClaims);
        }

        _strategies.Add(strategy);
        Log.LogInformation("Registered strategy {StrategyId}", strategy.StrategyId);

        if (strategy is INeedsExecAlgorithms needs)
        {
            foreach (ExecAlgorithm algorithm in needs.RequiredExecAlgorithms())
            {
                if (_execAlgorithms.Any(a => a.ExecAlgorithmId == algorithm.ExecAlgorithmId))
                {
                    Log.LogInformation("{StrategyId} needs {ExecAlgorithmId}, which is already registered; that one is left in place",
                        strategy.StrategyId, algorithm.ExecAlgorithmId);
                    continue;
                }

                AddExecAlgorithm(algorithm);
                _forStrategies.Add(algorithm.ExecAlgorithmId);
                Log.LogInformation("Registered {ExecAlgorithmId} because {StrategyId} asks for it", algorithm.ExecAlgorithmId, strategy.StrategyId);
            }
        }
    }

    public void AddExecAlgorithm(ExecAlgorithm algorithm)
    {
        ArgumentNullException.ThrowIfNull(algorithm);
        if (_forStrategies.Remove(algorithm.ExecAlgorithmId)
            && _execAlgorithms.FirstOrDefault(a => a.ExecAlgorithmId == algorithm.ExecAlgorithmId) is { } standIn)
        {
            // Registered for a strategy that asked for the id; the host has now brought its own, and its own is the
            // one that runs. Nothing is working yet either way: strategies and algorithms are added before the start.
            _executionEngine.DeregisterExecAlgorithm(standIn.ExecAlgorithmId);
            _execAlgorithms.Remove(standIn);
            standIn.Dispose();
            Log.LogInformation("Replaced the {ExecAlgorithmId} registered for a strategy with the host's own", algorithm.ExecAlgorithmId);
        }

        EnsureUnique(algorithm.ActorId);
        algorithm.RunningIn = _environment;
        algorithm.Register(TraderId, _clock, _cache, _bus, _portfolio, _loggerFactory, _post);
        _executionEngine.RegisterExecAlgorithm(algorithm.ExecAlgorithmId, algorithm.HandleCommand);
        _execAlgorithms.Add(algorithm);
        Log.LogInformation("Registered execution algorithm {ExecAlgorithmId}", algorithm.ExecAlgorithmId);
    }

    public void AddActors(IEnumerable<Actor> actors)
    {
        foreach (Actor actor in actors)
        {
            AddActor(actor);
        }
    }

    public void AddStrategies(IEnumerable<Strategy> strategies)
    {
        foreach (Strategy strategy in strategies)
        {
            AddStrategy(strategy);
        }
    }

    private void EnsureUnique(ActorId id)
    {
        if (AllComponents.Any(a => a.ActorId == id))
        {
            throw new InvalidOperationException($"An actor with id {id} is already registered.");
        }
    }

    public Strategy? Strategy(StrategyId id) => _strategies.FirstOrDefault(s => s.StrategyId == id);

    public Actor? Actor(ActorId id) => AllComponents.FirstOrDefault(a => a.ActorId == id);

    protected override void OnStart()
    {
        foreach (Actor actor in AllComponents)
        {
            if (actor.State is ComponentState.Ready or ComponentState.Stopped)
            {
                try
                {
                    actor.Start();
                }
                catch (Exception e)
                {
                    Log.LogError(e, "Actor {ActorId} failed to start", actor.ActorId);
                }
            }
        }
    }

    protected override void OnStop()
    {
        foreach (Actor actor in AllComponents.Reverse())
        {
            if (actor.State is ComponentState.Running or ComponentState.Degraded)
            {
                try
                {
                    actor.Stop();
                }
                catch (Exception e)
                {
                    Log.LogError(e, "Actor {ActorId} failed to stop", actor.ActorId);
                }
            }
        }
    }

    protected override void OnReset()
    {
        foreach (Actor actor in AllComponents)
        {
            if (actor.State is ComponentState.Ready or ComponentState.Stopped)
            {
                actor.Reset();
                (actor as Strategy)?.ResetIdSequence();
            }
        }
    }

    protected override void OnDispose()
    {
        foreach (Actor actor in AllComponents.Reverse())
        {
            actor.Dispose();
        }

        _actors.Clear();
        _strategies.Clear();
        _execAlgorithms.Clear();
        _forStrategies.Clear();
    }

    /// <summary>
    /// Removes all actors and strategies so the trader can be reconfigured.
    /// </summary>
    public void Clear()
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("Cannot clear a running trader.");
        }

        foreach (Actor actor in AllComponents.ToList())
        {
            if (actor.State != ComponentState.Disposed)
            {
                actor.Dispose();
            }
        }

        foreach (ExecAlgorithm algorithm in _execAlgorithms)
        {
            _executionEngine.DeregisterExecAlgorithm(algorithm.ExecAlgorithmId);
        }

        _actors.Clear();
        _strategies.Clear();
        _execAlgorithms.Clear();
        _forStrategies.Clear();
    }

    public void SaveState()
    {
        foreach (Actor actor in AllComponents)
        {
            try
            {
                actor.Save();
            }
            catch (Exception e)
            {
                Log.LogError(e, "Failed to save state for {ActorId}", actor.ActorId);
            }
        }
    }

    public void LoadState()
    {
        foreach (Actor actor in AllComponents)
        {
            try
            {
                actor.Load();
            }
            catch (Exception e)
            {
                Log.LogError(e, "Failed to load state for {ActorId}", actor.ActorId);
            }
        }
    }
}
