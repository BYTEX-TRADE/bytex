# 0012 - Running a strategy document

Status: accepted

## Problem

A strategy document ([0008](0008-strategy-documents.md)) describes a graph, the
catalog ([0010](0010-node-catalog.md)) describes what each node type means, and
the validator ([0011](0011-document-validation.md)) says whether a document can
run. Nothing ran one. A document was data that tooling could read and check,
and the engine could not trade it.

## Decision

`DocumentStrategy : Strategy<DocumentStrategyConfig>` runs a document, and the
provider `bytex.document` builds one from a strategy configuration, so a
document is submitted to a backtest, a sandbox or a live node exactly like a
hand-written strategy.

### Compiled once, evaluated per bar

At start the document becomes an evaluation plan:

- nodes in Kahn topological order, with document order as the tie-break, so
  evaluation order is fixed and a rerun repeats it;
- each node's source bar type propagated from the nearest `data.bars` node, so
  an indicator node registers its engine indicator against the bar type it
  actually reads and the actor updates it before the node is evaluated;
- the warm-up each node needs, taken from its parameters, so history is
  requested once for the longest lookback rather than per node.

On every close of the **primary** bar type the plan is evaluated into a frame:
one value per output port, keyed `nodeId:port`. A node in a phase that is not
active is skipped and its outputs are absent for that bar, which is what makes
a phase a phase. `LastValues` is the frame of the last evaluated bar, and
`LastFrame` is the condition-level summary a monitor shows.

### Nodes place ordinary orders

An action node submits orders through the strategy's own `OrderFactory`, so the
risk engine checks them, the cache holds them and a report shows them like any
others; the node tags each order with its own id and tracks it from submission
to a terminal state. Order and position events reach the node that owns the
order, which is how `filled`, `rejected` and `position` come to be published.

### Everything it did is an event

Every fire, skip, order, transition and lifecycle step is a `StrategyEvent`
published on the bus and kept in `Decisions`, so a monitor or an assistant can
say what the strategy did and why without reading its graph.

### It survives a restart

`OnSave` and `OnLoad` persist the phase, the bar index and the state of the
nodes that carry state - tracked orders, latches, counters, managed exits - so a
sandbox or live node restarts where it left off rather than flat and blind. On
start a document asks for recent closed bars and replays them without placing
anything, so a strategy with a 200-bar lookback is ready on its first live bar
instead of two days later.

### Determinism

The runtime reads the engine clock and nothing else, uses `decimal` throughout
and has no randomness of its own. The same document over the same data produces
the same orders, fills and positions, which the tests check by fingerprint.

## Alternatives considered

- **Compiling a document to C# and loading it.** Rejected for 0.x: a Roslyn
  step complicates hosting and buys nothing an interpreter cannot do at
  bar-close throughput. The provider boundary leaves it open.
- **Evaluating every tick.** Deferred. The frame model allows it, but version 1
  evaluates on bar close, which keeps a backtest and a live run identical on
  bar data.
- **Letting nodes talk to the venue directly.** Rejected: an order that does
  not pass the risk engine and the cache is an order nothing else in the engine
  knows about.

## Consequences

- A document is a strategy in every context the engine has, and the same
  document backtests, paper trades and trades live.
- A node type is described in the catalog and built by the runtime, so a
  descriptor without a factory is a type this engine cannot run: the validator
  says so with `NODE_NOT_RUNNABLE` rather than failing halfway through a run.
- The catalog's promises are testable: every type in it gets a document of its
  own in the test suite, runs over scripted bars, and must publish the outputs
  it declares.
