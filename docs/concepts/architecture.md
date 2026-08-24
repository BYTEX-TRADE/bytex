# Architecture

BYTEX is an event-driven system built around a **single-threaded kernel** and
a **message bus**. Components that participate in trading logic run on the
kernel thread and talk only through messages; anything that performs I/O runs
on background tasks and hands results to the kernel thread through a queue.

## Components

| Component | Role |
|---|---|
| `Kernel` | builds and owns every other component from a `KernelConfig`; exposes the environment context |
| `MessageBus` | topic pub/sub with `*`/`?` patterns, point-to-point endpoints, request/response |
| `IClock` | the only source of time; `TestClock` in backtests, `LiveClock` otherwise; hosts timers and alerts |
| `Cache` | in-memory instruments, market data windows, books, orders, positions, accounts, with secondary indexes |
| `DataEngine` | owns data clients; turns subscriptions into client calls; fans data out to cache and bus; builds bars internally |
| `ExecutionEngine` | owns execution clients; routes commands; applies events to orders and positions; reconciles with venues |
| `RiskEngine` | validates trading commands before execution |
| `Portfolio` | balances, exposures, P&amp;L across venues |
| `Trader` | hosts actors, strategies, execution algorithms and drives their lifecycle |

## Environment contexts

| Context | Clock | Data | Execution |
|---|---|---|---|
| Backtest | `TestClock` advanced by the data stream | historical data from catalog or memory | `SimulatedExchange` per venue |
| Sandbox | `LiveClock` | live data clients | `SandboxExecutionClient` (simulated fills) |
| Live | `LiveClock` | live data clients | venue execution clients |

The kernel class is identical in all three; only the clock and the clients
differ. Strategies read `Kernel.Environment` if they need to know.

## Execution model

- The kernel thread processes one message at a time. Handlers run to
  completion and never block on I/O.
- **Backtest:** the `BacktestEngine` pulls the next data element, advances the
  `TestClock` to its timestamp — firing due timers first — then lets the
  simulated venue match against the element, then hands the element to the
  data engine. Timers before data at the same timestamp; venue before
  strategies. This ordering is fixed.
- **Live:** `LiveKernelLoop` owns a bounded channel and a dedicated thread.
  Data clients, execution clients, and timer callbacks post onto it through
  `DispatchingDataSink`/`DispatchingExecutionSink` and the `LiveClock`
  dispatcher. A full queue blocks producers (back-pressure) rather than
  growing memory.

## Flows

Market data:

```
venue ─► data client (I/O task) ─► kernel queue ─► DataEngine.Process
      ─► Cache ─► bar aggregators ─► MessageBus.Publish("data.quotes.{venue}.{symbol}")
      ─► Actor.OnQuoteTick
```

Order:

```
Strategy.SubmitOrder ─► "RiskEngine.execute" ─► checks ─► "ExecutionEngine.execute"
      ─► execution client ─► venue
venue event ─► execution client ─► kernel queue ─► ExecutionEngine.Process
      ─► Order.Apply ─► Position.Apply ─► Cache ─► "events.order.{strategy}" / "events.position.{strategy}"
      ─► Strategy.OnOrderFilled / OnPositionOpened
```

## Failure policy

The engine fails fast on anything that would corrupt state: invalid order
state transitions, mismatched currencies, unknown instruments. Handler
exceptions inside an actor fault that actor only; the kernel and other actors
continue. Market-data parse failures in adapters are logged and the element is
dropped.

## Package layout

```
Bytex.Core            model · messaging · clock · cache · portfolio · engines · Strategy SDK · adapter SDK · plugins
Bytex.Indicators      technical indicators
Bytex.Data            Parquet catalog · CSV loaders · instrument JSON
Bytex.Backtest        simulated venue · matching engine · backtest engine/node · results
Bytex.Live            trading node · kernel loop · network infrastructure · sandbox execution (uses Bytex.Backtest's venue)
Bytex.Adapters.*      Binance · Bybit · Tardis
Bytex.Persistence.Redis
Bytex.Cli
```
