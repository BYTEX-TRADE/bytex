using Bytex.Core.Model;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Timing;
using Microsoft.Extensions.Logging;

namespace Bytex.Core.Common;

public sealed class InvalidStateTransitionException : InvalidOperationException
{
    public InvalidStateTransitionException(ComponentId id, ComponentState from, string trigger)
        : base($"Component {id}: cannot {trigger} from state {from}.")
    {
    }
}

public interface IComponent
{
    ComponentId Id { get; }

    ComponentState State { get; }

    bool IsRunning { get; }

    void Start();

    void Stop();

    void Resume();

    void Reset();

    void Dispose();

    void Degrade();

    void Fault();

    event Action<ComponentStateChanged>? StateChanged;
}

/// <summary>
/// Base class providing the uniform lifecycle state machine for engines, clients, actors, and strategies.
/// </summary>
public abstract class Component : IComponent
{
    private IClock? _clock;
    private ILogger? _logger;

    protected Component(ComponentId id)
    {
        Id = id;
        State = ComponentState.PreInitialized;
    }

    public ComponentId Id { get; protected set; }

    public ComponentState State { get; private set; }

    public bool IsInitialized => State != ComponentState.PreInitialized;

    public bool IsRunning => State == ComponentState.Running;

    public bool IsStopped => State == ComponentState.Stopped;

    public bool IsDisposed => State == ComponentState.Disposed;

    public bool IsDegraded => State == ComponentState.Degraded;

    public bool IsFaulted => State == ComponentState.Faulted;

    public event Action<ComponentStateChanged>? StateChanged;

    /// <summary>
    /// Clock supplied by the owning kernel when the component is registered.
    /// </summary>
    protected IClock Clock => _clock ?? throw new InvalidOperationException($"Component {Id} has not been registered with a kernel.");

    protected ILogger Log => _logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    protected bool HasClock => _clock is not null;

    /// <summary>
    /// Called by the kernel to provide clock and logging before the component is used.
    /// </summary>
    public virtual void Initialize(IClock clock, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _clock = clock;
        _logger = loggerFactory.CreateLogger(GetType().FullName ?? GetType().Name);
        if (State == ComponentState.PreInitialized)
        {
            Transition(ComponentState.Ready);
        }
    }

    protected void SetReady()
    {
        if (State == ComponentState.PreInitialized)
        {
            Transition(ComponentState.Ready);
        }
    }

    public void Start()
    {
        Require("start", ComponentState.Ready, ComponentState.Stopped);
        Transition(ComponentState.Starting);
        try
        {
            OnStart();
        }
        catch (Exception e)
        {
            Log.LogError(e, "Component {Id} failed to start", Id);
            Transition(ComponentState.Faulting);
            Transition(ComponentState.Faulted);
            throw;
        }

        Transition(ComponentState.Running);
    }

    public void Stop()
    {
        Require("stop", ComponentState.Running, ComponentState.Degraded);
        Transition(ComponentState.Stopping);
        try
        {
            OnStop();
        }
        catch (Exception e)
        {
            Log.LogError(e, "Component {Id} failed to stop", Id);
            Transition(ComponentState.Faulting);
            Transition(ComponentState.Faulted);
            throw;
        }

        Transition(ComponentState.Stopped);
    }

    public void Resume()
    {
        Require("resume", ComponentState.Stopped, ComponentState.Degraded);
        Transition(ComponentState.Resuming);
        OnResume();
        Transition(ComponentState.Running);
    }

    public void Reset()
    {
        Require("reset", ComponentState.Ready, ComponentState.Stopped);
        Transition(ComponentState.Resetting);
        OnReset();
        Transition(ComponentState.Ready);
    }

    public void Dispose()
    {
        if (State == ComponentState.Disposed)
        {
            return;
        }

        if (State is ComponentState.Running or ComponentState.Degraded)
        {
            Stop();
        }

        Require("dispose", ComponentState.PreInitialized, ComponentState.Ready, ComponentState.Stopped, ComponentState.Faulted);
        Transition(ComponentState.Disposing);
        OnDispose();
        Transition(ComponentState.Disposed);
        GC.SuppressFinalize(this);
    }

    public void Degrade()
    {
        Require("degrade", ComponentState.Running);
        Transition(ComponentState.Degrading);
        OnDegrade();
        Transition(ComponentState.Degraded);
    }

    public void Fault()
    {
        if (State is ComponentState.Faulted or ComponentState.Disposed)
        {
            return;
        }

        Transition(ComponentState.Faulting);
        try
        {
            OnFault();
        }
        finally
        {
            Transition(ComponentState.Faulted);
        }
    }

    protected virtual void OnStart()
    {
    }

    protected virtual void OnStop()
    {
    }

    protected virtual void OnResume()
    {
    }

    protected virtual void OnReset()
    {
    }

    protected virtual void OnDispose()
    {
    }

    protected virtual void OnDegrade()
    {
    }

    protected virtual void OnFault()
    {
    }

    private void Require(string trigger, params ComponentState[] allowed)
    {
        if (Array.IndexOf(allowed, State) < 0)
        {
            throw new InvalidStateTransitionException(Id, State, trigger);
        }
    }

    private void Transition(ComponentState next)
    {
        ComponentState previous = State;
        State = next;
        if (StateChanged is { } handler)
        {
            Model.Primitives.UnixNanos now = _clock?.Timestamp ?? Model.Primitives.UnixNanos.FromDateTimeOffset(DateTimeOffset.UtcNow);
            handler(new ComponentStateChanged(Id, previous, next, Guid.NewGuid(), now, now));
        }
    }

    public override string ToString() => $"{GetType().Name}({Id}, {State})";
}
