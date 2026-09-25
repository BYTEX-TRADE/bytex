# 0007 — Message bus and kernel execution model

Status: accepted

## Problem

Components must communicate without referencing each other, timers must fire
deterministically relative to data, and every component needs the same
lifecycle so the kernel can start, stop, and recover them uniformly.

## Decision

### Message bus

```csharp
public interface IMessageBus
{
    // Publish/subscribe with topic patterns: "*" matches any run of characters, "?" one character.
    void Subscribe(string topicPattern, Action<object> handler, int priority = 0);
    void Unsubscribe(string topicPattern, Action<object> handler);
    void Publish(string topic, object message);

    // Point-to-point endpoints
    void Register(string endpoint, Action<object> handler);
    void Deregister(string endpoint);
    void Send(string endpoint, object message);

    // Request/response with correlation
    void Request(string endpoint, Request request, Action<Response> callback);
    void Respond(Response response);

    bool HasSubscribers(string topic);
    IReadOnlyList<string> Topics { get; }
}
```

Topic conventions:

```
data.instrument.{venue}.{symbol}         data.quotes.{venue}.{symbol}        data.trades.{venue}.{symbol}
data.bars.{bar_type}                     data.book.deltas.{venue}.{symbol}   data.book.snapshots.{venue}.{symbol}
data.status.{venue}.{symbol}             data.custom.{type}.{metadata}       signal.{name}
events.order.{strategy_id}               events.position.{strategy_id}       events.account.{account_id}
events.system.{component_id}
```

Endpoints:

```
DataEngine.execute   DataEngine.process   DataEngine.request   DataEngine.response
RiskEngine.execute   ExecutionEngine.execute   ExecutionEngine.process   Portfolio.update_account
```

Dispatch is synchronous: `Publish` invokes every matching handler, in
priority order (higher first), before returning. Pattern subscriptions are
resolved once per topic and cached. Handlers that throw are isolated: the
exception is logged and attributed to the subscriber's owning component,
which is faulted; remaining handlers still run.

### Clock and timers

```csharp
public interface IClock
{
    UnixNanos Timestamp { get; }
    DateTimeOffset UtcNow { get; }
    IReadOnlyList<string> TimerNames { get; }
    int TimerCount { get; }
    void SetTimeAlert(string name, UnixNanos alertTime, Action<TimeEvent>? callback = null, bool allowPast = true);
    void SetTimer(string name, TimeSpan interval, UnixNanos? start = null, UnixNanos? stop = null,
                  Action<TimeEvent>? callback = null, bool fireImmediately = false);
    UnixNanos? NextTime(string name);
    void CancelTimer(string name);
    void CancelTimers();
    void RegisterDefaultHandler(Action<TimeEvent> handler);
}

public sealed record TimeEvent(string Name, Guid EventId, UnixNanos TsEvent, UnixNanos TsInit);
```

- `TestClock` — `SetTime(UnixNanos)` and `AdvanceTime(UnixNanos to)`; the
  latter returns the due `TimeEvent`s in chronological order (ties by timer
  registration order) without firing them. The backtest engine fires them,
  then processes the data element that caused the advance.
- `LiveClock` — timers are scheduled with `PeriodicTimer`/`Task.Delay` on a
  background task; firing posts the `TimeEvent` onto the kernel queue so the
  callback runs on the kernel thread.

### Component lifecycle

```csharp
public interface IComponent
{
    ComponentId Id { get; }
    ComponentState State { get; }
    void Start(); void Stop(); void Resume(); void Reset(); void Dispose(); void Degrade(); void Fault();
    event Action<ComponentStateChanged>? StateChanged;
}
```

Transitions (anything else throws `InvalidStateTransitionException`):

```
PreInitialized → Ready
Ready          → Starting → Running
Running        → Stopping → Stopped | Degrading → Degraded | Faulting → Faulted
Stopped        → Resuming → Running | Resetting → Ready | Disposing → Disposed
Degraded       → Resuming → Running | Stopping → Stopped
Faulted        → Disposing → Disposed
```

`Component` implements the machine and calls the `On*` hooks from the
transitional states. The `Trader` starts actors in registration order and
stops them in reverse.

### Kernel

```csharp
public sealed class Kernel : IDisposable
{
    public Kernel(KernelConfig config, IClock clock, IMessageBus bus, ICache cache, ILoggerFactory logging);
    public Environment Environment { get; }
    public TraderId TraderId { get; }
    public IClock Clock { get; }  IMessageBus MessageBus { get; }  ICache Cache { get; }
    public IPortfolio Portfolio { get; }  DataEngine DataEngine { get; }  ExecutionEngine ExecutionEngine { get; }
    public RiskEngine RiskEngine { get; }  Trader Trader { get; }
    public void Start(); void Stop(); void Dispose();
}
```

In backtests the kernel has no queue: the engine calls into it directly. In
live nodes the `LiveKernelLoop` owns a bounded `Channel<object>` and a
dedicated thread; everything that enters from I/O or timers is a message on
that channel. Back-pressure is by blocking the producer (data clients) — a
slow strategy slows its own feed rather than growing memory without bound.

### Generation of identifiers

- `ClientOrderId`: `O-{yyyyMMdd}-{HHmmss}-{trader_tag}-{strategy_tag}-{count}` from `ClientOrderIdGenerator`, which is seeded from the cache on startup so ids never repeat after a restart.
- `PositionId` (netting): `{instrument_id}-{strategy_id}`; (hedging): venue-supplied.
- `OrderListId`: `OL-{yyyyMMdd}-{HHmmss}-{trader_tag}-{strategy_tag}-{count}`.

## Alternatives considered

- **Async message handlers with per-subscriber queues.** Rejected: ordering
  across subscribers would depend on scheduling.
- **Reactive (observable) pipelines.** Rejected: harder to make deterministic
  and to replay; the plain bus is simpler to reason about.

## Consequences

- A backtest is a pure function of its inputs; `TsInit` values and timer
  firing order are reproducible.
- Live nodes are bounded in memory and degrade gracefully under load.
