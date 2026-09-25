# 0011 - Validating a strategy document

Status: accepted

## Problem

A strategy document ([0008](0008-strategy-documents.md)) is data, and the
format admits documents that cannot run: an edge between incompatible ports, a
phase nothing enters, a size below the instrument's minimum, a strategy that
never places an order. Without a check before the run, every one of those
surfaces as an exception somewhere inside a backtest - after the data has been
loaded and the engine started, with a stack trace instead of a reason, and
nothing a builder can point at or an assistant can act on.

## Decision

`DocumentValidator` reads a document against a catalog
([0010](0010-node-catalog.md)) and returns a `ValidationReport`: a list of
`Finding`s, each with a level, a reason code, a message, the node and port it
concerns, and often a suggested fix.

### Three layers, and it stops at the one that failed

1. **Structural** - the document as a document: schema version, names,
   instrument ids, bar types, parameters and their ranges, node ids, node types
   the catalog knows, each node's parameters against its type's specs, edges
   between ports that exist and can carry the same kind, one edge per input,
   every required input connected, no cycles, the caps, phases with exactly one
   start, transitions onto ports that can fire.
2. **Semantic** - the strategy as a strategy: it never places an order (block);
   nothing protects a position (warning); a target on the wrong side of a stop;
   sizing by risk with no stop to measure against; contradicting comparisons
   feeding one "all of"; a phase nothing leads into (warning); a node wired to
   nothing (info); a node in a mode it is not allowed in; and the live rule on
   events a model classified.
3. **Context** - the strategy against the world it will run in: the instruments
   exist, prices sit on the tick, sizes clear the minimum quantity and
   notional, no short entry on a spot instrument, and the data covers the
   warm-up. This layer needs an `IValidationContext`; without one it is skipped
   rather than guessed at.

A block in one layer stops the walk, and the report says where it stopped. A
builder then shows findings about one thing instead of the wreckage the next
two layers would report on a document that was already broken.

### Levels and codes

`Block` means the document must not run. `Warning` means it will run and
someone should look - no exit rule, a phase nothing enters, a price off the
tick, less data than the warm-up needs. `Info` is a remark, such as a node
wired to nothing.

Every finding carries a stable reason code (`PORT_TYPE_MISMATCH`, `NO_ENTRY`,
`SHORT_ON_SPOT`, ...). The code is the contract: a host matches on it to point
at the right control, and an assistant to decide what to change. Messages are
for people and may be reworded; codes are not.

### What is a cap and what is a rule

Node, edge, phase, parameter and lookback limits are `ValidatorOptions`, not
part of the format: a host that wants larger documents raises them without a
new schema version. The rules above are not optional in the same way - they
describe what the engine can run.

## Alternatives considered

- **Validating inside the runtime, as it builds the graph.** Rejected: the
  answer arrives after a run has started, one problem at a time, and a host
  that only edits documents would have to start a run to check one.
- **One list of problems with no levels.** Rejected: "no exit rule" and "no
  entry at all" cannot share a severity - one is a choice, the other is a
  document that does nothing.
- **Messages only, no codes.** Rejected: a host cannot match on prose, and
  rewording a message would break every caller that tried.

## Consequences

- A document can be checked without a runtime, an instrument catalog or any
  data; each of those only adds a layer.
- The catalog is what the structural layer checks against, so a plugin's
  `custom.*` types are validated exactly like the built-in ones.
- A reason code is public API. Adding one is additive; changing what one means
  is not.
