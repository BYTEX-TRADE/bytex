# Extending the engine

Nine places where code of your own becomes part of the engine, with the contract
each one implements and where that contract is written down. Nothing here needs a
fork: every one of them is a public interface and a registration.

| What you want to add | Implement | Register with | Contract |
|---|---|---|---|
| A strategy | `Strategy` / `Strategy<TConfig>` | `node.AddStrategy`, or a plugin's `IStrategyProvider` | [0004](design/0004-strategy-sdk.md) |
| A non-trading component (a monitor, a recorder) | `RuntimeModule` | `node.AddRuntimeModule` | [0004](design/0004-strategy-sdk.md) |
| A venue or a data vendor | `IDataClient`, `IExecutionClient`, `IInstrumentProvider` | a plugin's client factories | [0005](design/0005-adapter-sdk.md) |
| An execution algorithm | `OrderSchedule` | `node.AddOrderSchedule`, or a plugin | [0004](design/0004-strategy-sdk.md) |
| An indicator | `Indicator`, or `IOrderBookIndicator` for one that reads the book | `RegisterIndicatorFor…`, and `IIndicatorFactory` to name it in configuration | [Indicators](concepts/indicators.md) |
| A venue behaviour a backtest should charge for | `ISimulationModule` | `SimulatedVenueConfig.Modules` | [Backtesting](concepts/backtesting.md) |
| A margin rule | `IMarginModel` | `SimulatedVenueConfig.MarginModel`, or `MarginAccount.MarginModel` | [Risk](concepts/risk.md) |
| A performance figure | `IPerformanceStatistic` | `BacktestEngineConfig.Statistics` | [Backtesting](concepts/backtesting.md) |
| A node type a document can use | `INodeEvaluator`, described by a `NodeTypeDescriptor` | a plugin's `INodeTypeProvider` | [Documents](concepts/documents.md#node-types-from-a-plugin) |

Everything above is loadable from an assembly the engine did not ship: see
[plugins](concepts/plugins.md) and [design note 0006](design/0006-plugin-contract.md)
for the one interface a package implements to carry any number of these, and
`bytex --plugins <dir>` for loading it.

## The three rules that apply to all of them

**Nothing blocks the tradingRuntime thread.** Every handler runs on the one thread that
owns engine state, in order, and a handler that waits on a network call stops the
node. `RunInBackground` on a runtime module does work off the thread and marshals the
result back onto it; that is the only way to wait for anything. See
[design note 0007](design/0007-messaging-and-tradingRuntime.md).

**State that must survive a restart is saved through the hooks.** `OnSaveState`
and `OnLoadState` are what a node writes and reads; anything a component keeps in
a field is gone when the process is. The node saves on an interval as well as at a
stop, so a crash costs at most that interval.

**A component that throws is isolated rather than fatal.** A handler that throws
faults its own component and the node keeps running with the rest, which is why a
run's result names the strategies that faulted: a strategy that stopped is a
different thing from one whose conditions never came true, and the numbers alone
cannot tell them apart.

## What a contract being frozen means

The interfaces in design notes 0004 to 0006 are frozen for the 0.x line:
additions are allowed, and a change that breaks them needs a superseding note and
a minor version. From 1.0 the whole public surface is frozen the same way and
held to it by a test - see [versioning](versioning.md).

## Where to look first

- The smallest working strategy, and the shape of a config-driven one:
  [your first backtest](getting-started/first-backtest.md).
- A venue adapter end to end, including the network helpers every one of them
  uses: [design note 0005](design/0005-adapter-sdk.md) and any file under
  `src/Bytex.Adapters.*`.
- The examples: `examples/Bytex.Examples` for code, `examples/configs` for
  configuration the CLI reads, `examples/reference` for the backtests that run on
  every commit.
