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
/// Hosts runtimeModules, strategies, and execution algorithms and drives their lifecycle as a group.
/// </summary>
public sealed class ModuleHost : Component
{
    private readonly IClock _clock;
    private readonly ICache _cache;
    private readonly IMessageBus _bus;
    private readonly IPortfolio _portfolio;
    private readonly OrderCoordinator _orderCoordinator;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Action<Action>? _post;

    private readonly TradingEnvironment _environment;
    private readonly List<RuntimeModule> _runtimeModules = new();
    private readonly List<Strategy> _strategies = new();
    private readonly List<OrderSchedule> _orderSchedules = new();

    /// <summary>
    /// Algorithms registered because a strategy asked for them rather than because the host said so. A host's own
    /// algorithm under the same id replaces one of these, so it does not matter which of the two was added first.
    /// </summary>
    private readonly HashSet<OrderScheduleId> _forStrategies = new();

    public ModuleHost(ModuleHostId moduleHostId, IClock clock, ICache cache, IMessageBus bus, IPortfolio portfolio, OrderCoordinator orderCoordinator, ILoggerFactory loggerFactory, Action<Action>? post = null, TradingEnvironment environment = TradingEnvironment.Backtest)
        : base(new ComponentId(moduleHostId.Value))
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(portfolio);
        ArgumentNullException.ThrowIfNull(orderCoordinator);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ModuleHostId = moduleHostId;
        _clock = clock;
        _cache = cache;
        _bus = bus;
        _portfolio = portfolio;
        _orderCoordinator = orderCoordinator;
        _loggerFactory = loggerFactory;
        _environment = environment;
        _post = post;
        Initialize(clock, loggerFactory);
    }

    public ModuleHostId ModuleHostId { get; }

    public IReadOnlyList<RuntimeModule> RuntimeModules => _runtimeModules;

    public IReadOnlyList<Strategy> Strategies => _strategies;

    public IReadOnlyList<OrderSchedule> OrderSchedules => _orderSchedules;

    public IEnumerable<RuntimeModule> AllComponents => _runtimeModules.Concat<RuntimeModule>(_orderSchedules).Concat(_strategies);

    public void AddRuntimeModule(RuntimeModule runtimeModule)
    {
        ArgumentNullException.ThrowIfNull(runtimeModule);
        if (runtimeModule is Strategy strategy)
        {
            AddStrategy(strategy);
            return;
        }

        if (runtimeModule is OrderSchedule algorithm)
        {
            AddOrderSchedule(algorithm);
            return;
        }

        EnsureUnique(runtimeModule.RuntimeModuleId);
        runtimeModule.RunningIn = _environment;
        runtimeModule.Register(ModuleHostId, _clock, _cache, _bus, _portfolio, _loggerFactory, _post);
        _runtimeModules.Add(runtimeModule);
        Log.LogInformation("Registered runtimeModule {RuntimeModuleId}", runtimeModule.RuntimeModuleId);
    }

    public void AddStrategy(Strategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        EnsureUnique(strategy.RuntimeModuleId);
        strategy.RunningIn = _environment;
        strategy.Register(ModuleHostId, _clock, _cache, _bus, _portfolio, _loggerFactory, _post);
        _orderCoordinator.RegisterOmsType(strategy.StrategyId, strategy.OmsType);
        if (strategy.ExternalOrderClaims.Count > 0)
        {
            _orderCoordinator.RegisterExternalOrderClaims(strategy.StrategyId, strategy.ExternalOrderClaims);
        }

        _strategies.Add(strategy);
        Log.LogInformation("Registered strategy {StrategyId}", strategy.StrategyId);

        if (strategy is INeedsOrderSchedules needs)
        {
            foreach (OrderSchedule algorithm in needs.RequiredOrderSchedules())
            {
                if (_orderSchedules.Any(a => a.OrderScheduleId == algorithm.OrderScheduleId))
                {
                    Log.LogInformation("{StrategyId} needs {OrderScheduleId}, which is already registered; that one is left in place",
                        strategy.StrategyId, algorithm.OrderScheduleId);
                    continue;
                }

                AddOrderSchedule(algorithm);
                _forStrategies.Add(algorithm.OrderScheduleId);
                Log.LogInformation("Registered {OrderScheduleId} because {StrategyId} asks for it", algorithm.OrderScheduleId, strategy.StrategyId);
            }
        }
    }

    public void AddOrderSchedule(OrderSchedule algorithm)
    {
        ArgumentNullException.ThrowIfNull(algorithm);
        if (_forStrategies.Remove(algorithm.OrderScheduleId)
            && _orderSchedules.FirstOrDefault(a => a.OrderScheduleId == algorithm.OrderScheduleId) is { } standIn)
        {
            // Registered for a strategy that asked for the id; the host has now brought its own, and its own is the
            // one that runs. Nothing is working yet either way: strategies and algorithms are added before the start.
            _orderCoordinator.DeregisterOrderSchedule(standIn.OrderScheduleId);
            _orderSchedules.Remove(standIn);
            standIn.Dispose();
            Log.LogInformation("Replaced the {OrderScheduleId} registered for a strategy with the host's own", algorithm.OrderScheduleId);
        }

        EnsureUnique(algorithm.RuntimeModuleId);
        algorithm.RunningIn = _environment;
        algorithm.Register(ModuleHostId, _clock, _cache, _bus, _portfolio, _loggerFactory, _post);
        _orderCoordinator.RegisterOrderSchedule(algorithm.OrderScheduleId, algorithm.HandleCommand);
        _orderSchedules.Add(algorithm);
        Log.LogInformation("Registered execution algorithm {OrderScheduleId}", algorithm.OrderScheduleId);
    }

    public void AddRuntimeModules(IEnumerable<RuntimeModule> runtimeModules)
    {
        foreach (RuntimeModule runtimeModule in runtimeModules)
        {
            AddRuntimeModule(runtimeModule);
        }
    }

    public void AddStrategies(IEnumerable<Strategy> strategies)
    {
        foreach (Strategy strategy in strategies)
        {
            AddStrategy(strategy);
        }
    }

    private void EnsureUnique(RuntimeModuleId id)
    {
        if (AllComponents.Any(a => a.RuntimeModuleId == id))
        {
            throw new InvalidOperationException($"An runtimeModule with id {id} is already registered.");
        }
    }

    /// <summary>
    /// Takes a strategy off this moduleHost (R5.10).
    ///
    /// <para>
    /// It must be stopped first. A running strategy holds subscriptions, timers and possibly orders it is managing,
    /// and disposing it under itself would leave the orders to nobody - so this refuses rather than deciding on an
    /// operator's behalf what should happen to them.
    /// </para>
    ///
    /// <para>
    /// What goes with it: its position model, the instruments it claimed orders on, and any execution algorithm that
    /// was registered only because it asked for one. An algorithm the host registered itself stays, because the host
    /// did not ask for it to go.
    /// </para>
    /// </summary>
    /// <returns>Whether there was such a strategy to remove.</returns>
    public bool RemoveStrategy(StrategyId id)
    {
        Strategy? strategy = _strategies.FirstOrDefault(s => s.StrategyId == id);
        if (strategy is null)
        {
            return false;
        }

        if (strategy.State is ComponentState.Running or ComponentState.Degraded)
        {
            throw new InvalidOperationException($"Strategy {id} is {strategy.State} and has to be stopped before it can be removed.");
        }

        _strategies.Remove(strategy);
        _orderCoordinator.DeregisterStrategy(id);

        if (strategy is INeedsOrderSchedules needs)
        {
            foreach (OrderSchedule algorithm in _orderSchedules
                .Where(a => _forStrategies.Contains(a.OrderScheduleId) && needs.RequiredOrderSchedules().Any(r => r.OrderScheduleId == a.OrderScheduleId))
                .ToList())
            {
                RemoveOrderSchedule(algorithm);
            }
        }

        strategy.Dispose();
        Log.LogInformation("Removed strategy {StrategyId}", id);
        return true;
    }

    /// <summary>Takes an runtimeModule off this moduleHost, on the same terms as a strategy: stopped first.</summary>
    /// <returns>Whether there was such an runtimeModule to remove.</returns>
    public bool RemoveRuntimeModule(RuntimeModuleId id)
    {
        RuntimeModule? runtimeModule = _runtimeModules.FirstOrDefault(a => a.RuntimeModuleId == id);
        if (runtimeModule is null)
        {
            return false;
        }

        if (runtimeModule.State is ComponentState.Running or ComponentState.Degraded)
        {
            throw new InvalidOperationException($"RuntimeModule {id} is {runtimeModule.State} and has to be stopped before it can be removed.");
        }

        _runtimeModules.Remove(runtimeModule);
        runtimeModule.Dispose();
        Log.LogInformation("Removed runtimeModule {RuntimeModuleId}", id);
        return true;
    }

    private void RemoveOrderSchedule(OrderSchedule algorithm)
    {
        if (algorithm.State is ComponentState.Running or ComponentState.Degraded)
        {
            algorithm.Stop();
        }

        _orderCoordinator.DeregisterOrderSchedule(algorithm.OrderScheduleId);
        _orderSchedules.Remove(algorithm);
        _forStrategies.Remove(algorithm.OrderScheduleId);
        algorithm.Dispose();
        Log.LogInformation("Removed execution algorithm {OrderScheduleId}", algorithm.OrderScheduleId);
    }

    public Strategy? Strategy(StrategyId id) => _strategies.FirstOrDefault(s => s.StrategyId == id);

    public RuntimeModule? RuntimeModule(RuntimeModuleId id) => AllComponents.FirstOrDefault(a => a.RuntimeModuleId == id);

    protected override void OnStart()
    {
        foreach (RuntimeModule runtimeModule in AllComponents)
        {
            if (runtimeModule.State is ComponentState.Ready or ComponentState.Stopped)
            {
                try
                {
                    runtimeModule.Start();
                }
                catch (Exception e)
                {
                    Log.LogError(e, "RuntimeModule {RuntimeModuleId} failed to start", runtimeModule.RuntimeModuleId);
                }
            }
        }
    }

    protected override void OnStop()
    {
        foreach (RuntimeModule runtimeModule in AllComponents.Reverse())
        {
            if (runtimeModule.State is ComponentState.Running or ComponentState.Degraded)
            {
                try
                {
                    runtimeModule.Stop();
                }
                catch (Exception e)
                {
                    Log.LogError(e, "RuntimeModule {RuntimeModuleId} failed to stop", runtimeModule.RuntimeModuleId);
                }
            }
        }
    }

    protected override void OnReset()
    {
        foreach (RuntimeModule runtimeModule in AllComponents)
        {
            if (runtimeModule.State is ComponentState.Ready or ComponentState.Stopped)
            {
                runtimeModule.Reset();
                (runtimeModule as Strategy)?.ResetIdSequence();
            }
        }
    }

    protected override void OnDispose()
    {
        foreach (RuntimeModule runtimeModule in AllComponents.Reverse())
        {
            runtimeModule.Dispose();
        }

        _runtimeModules.Clear();
        _strategies.Clear();
        _orderSchedules.Clear();
        _forStrategies.Clear();
    }

    /// <summary>
    /// Removes all runtimeModules and strategies so the moduleHost can be reconfigured.
    /// </summary>
    public void Clear()
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("Cannot clear a running moduleHost.");
        }

        foreach (RuntimeModule runtimeModule in AllComponents.ToList())
        {
            if (runtimeModule.State != ComponentState.Disposed)
            {
                runtimeModule.Dispose();
            }
        }

        foreach (OrderSchedule algorithm in _orderSchedules)
        {
            _orderCoordinator.DeregisterOrderSchedule(algorithm.OrderScheduleId);
        }

        _runtimeModules.Clear();
        _strategies.Clear();
        _orderSchedules.Clear();
        _forStrategies.Clear();
    }

    public void SaveState()
    {
        foreach (RuntimeModule runtimeModule in AllComponents)
        {
            try
            {
                runtimeModule.Save();
            }
            catch (Exception e)
            {
                Log.LogError(e, "Failed to save state for {RuntimeModuleId}", runtimeModule.RuntimeModuleId);
            }
        }
    }

    public void LoadState()
    {
        foreach (RuntimeModule runtimeModule in AllComponents)
        {
            try
            {
                runtimeModule.Load();
            }
            catch (Exception e)
            {
                Log.LogError(e, "Failed to load state for {RuntimeModuleId}", runtimeModule.RuntimeModuleId);
            }
        }
    }
}
