# 0001 — System architecture and environment contexts

Status: accepted

## Problem

A trading platform has to serve three very different runtime situations —
replaying history, running against live markets without risking money, and
trading real accounts — while guaranteeing that a strategy behaves the same in
all three. It must also let several strategies share one portfolio and one
risk layer, and let new venues be added without modifying the engine.

## Decision

BYTEX is an event-driven system built around a **single-threaded kernel** and
a **message bus**. Every component that participates in trading logic runs on
the kernel thread and communicates only through messages. Everything that
performs I/O runs on background tasks and hands results to the kernel thread
through a queue.

### Components

```
                      ┌──────────────────────────────────────────────┐
                      │                   Kernel                     │
                      │  ┌──────────┐  ┌───────┐  ┌───────────────┐  │
   data clients ───►  │  │DataEngine│  │ Cache │  │   Portfolio   │  │
                      │  └────┬─────┘  └───┬───┘  └───────┬───────┘  │
                      │       │            │              │          │
                      │  ┌────▼────────────▼──────────────▼───────┐  │
                      │  │              MessageBus                │  │
                      │  └────▲────────────▲──────────────▲───────┘  │
                      │       │            │              │          │
                      │  ┌────┴─────┐ ┌────┴──────┐ ┌─────┴───────┐  │
   exec clients ◄───► │  │RiskEngine│ │ExecEngine │ │   Trader    │  │
                      │  └──────────┘ └───────────┘ │ actors,     │  │
                      │                             │ strategies, │  │
                      │        Clock ◄──────────────│ exec algos  │  │
                      │                             └─────────────┘  │
                      └──────────────────────────────────────────────┘
```

| Component | Responsibility |
|---|---|
| `Kernel` | owns the lifecycle of every other component; builds them from configuration; exposes the environment context |
| `MessageBus` | topic-based publish/subscribe, point-to-point send, request/response; the only coupling between components |
| `Clock` | the single source of time; `TestClock` (manually advanced) in backtests, `LiveClock` (wall clock) otherwise; hosts timers and time alerts |
| `Cache` | in-memory state: instruments, market data windows, order books, orders, positions, accounts; the read model for strategies |
| `DataEngine` | owns data clients; translates subscription/request commands into client calls; routes incoming data to the cache and the bus; aggregates bars internally when a venue cannot |
| `ExecutionEngine` | owns execution clients; routes trading commands to the right client; applies execution events to orders and positions; reconciles state with venues on startup |
| `RiskEngine` | validates every trading command before it reaches the execution engine |
| `Portfolio` | account balances, positions, exposures, and profit-and-loss across all venues |
| `Trader` | hosts actors, strategies, and execution algorithms; starts and stops them as a group |

### Environment contexts

| Context | Clock | Data | Execution |
|---|---|---|---|
| Backtest | `TestClock` advanced by the data stream | historical data fed from a catalog or memory | `SimulatedExchange` per venue |
| Sandbox | `LiveClock` | live data clients | `SandboxExecutionClient` (simulated fills on live prices) |
| Live | `LiveClock` | live data clients | venue execution clients |

The kernel is the same class in all three contexts; only the clock and the
clients differ. Strategies cannot tell which context they run in except by
reading `Kernel.Environment`.

### Execution model

- The kernel thread processes one message at a time. Handlers run to
  completion; they never block and never wait on I/O.
- Backtests run entirely on the calling thread: the `BacktestEngine` pulls the
  next data element, advances the `TestClock` to its timestamp (firing any
  due timers first), then dispatches the element. This ordering — timers
  before data at the same timestamp — is fixed and documented.
- Live nodes run the kernel loop on a dedicated thread fed by a bounded
  channel. Data clients and execution clients post into the channel from their
  I/O tasks. Timer callbacks are posted the same way so that strategy code
  always executes on the kernel thread.

### Flows

Market data:

```
venue ─► data client (I/O task) ─► kernel queue ─► DataEngine
      ─► Cache.Add ─► MessageBus.Publish("data.quotes.{venue}.{symbol}")
      ─► Actor.OnQuoteTick
```

Order:

```
Strategy.SubmitOrder ─► MessageBus.Send("RiskEngine.execute")
      ─► RiskEngine checks ─► MessageBus.Send("ExecutionEngine.execute")
      ─► execution client ─► venue
venue event ─► execution client ─► kernel queue ─► ExecutionEngine
      ─► Order.Apply(event) ─► Cache/Portfolio update
      ─► MessageBus.Publish("events.order.{strategy_id}") ─► Strategy.OnOrderFilled
```

### Failure policy

The engine fails fast on anything that would corrupt state: arithmetic on
mismatched precisions, invalid state transitions, unparseable venue payloads
in execution paths. Such failures raise exceptions that fault the owning
component. Market-data parsing failures are logged and the element is dropped,
because losing a tick is recoverable and halting trading over it is not.

### Layering

```
Bytex.Core            model · messaging · clock · cache · portfolio · engines · SDKs
   ▲          ▲               ▲                 ▲
Bytex.Indicators   Bytex.Data        Bytex.Backtest      Bytex.Live
                                          ▲                   ▲
                                       Bytex.Cli      Bytex.Adapters.*   Bytex.Persistence.Redis
```

`Bytex.Core` has no dependency on any other BYTEX package. Adapters depend on
`Bytex.Live` for network infrastructure only.

## Alternatives considered

- **Multi-threaded engines with locks.** Rejected: determinism and
  reproducibility are primary requirements and are only cheap to guarantee
  with a single logical thread of execution.
- **Separate engines for backtest and live.** Rejected: the whole point of the
  platform is that the two share components.
- **Actor frameworks (mailbox-per-actor).** Rejected for the core: a single
  ordered queue is simpler to reason about and to replay; actor-style mailboxes
  remain available to adapters internally.

## Consequences

- Strategy code must be non-blocking. Long computations stall the kernel;
  heavy work belongs on background tasks that post results back as custom
  data.
- Throughput is bounded by one core. This is adequate for the intended use
  (bar- and tick-level strategies across a handful of venues) and is a
  deliberate trade against complexity.
- Every component has a uniform lifecycle (see 0007), which makes the kernel
  small.
