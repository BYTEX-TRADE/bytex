# 0008 - Strategy documents: the file format

Status: accepted

## Problem

Strategies written as C# classes are the engine's native form, but a visual
builder, a template library, and assisted authoring all need a strategy to be
*data*: a file that can be read without compiling, diffed, parameterised for
sweeps, explained in plain language, and run unchanged in backtest, sandbox,
and live contexts. The plugin contract ([0006](0006-plugin-contract.md))
already lets a provider build a strategy from an opaque definition; this note
defines the one such definition the engine ships.

## Scope

This note decides the **file format** only: what a strategy document contains
and how it is written to JSON. The node catalog that gives node types their
ports and parameters, the validator that judges a document before it runs, and
the runtime that executes one are separate decisions and arrive in their own
notes. Until they do, `Bytex.Documents` is the format and nothing more: an
engine build reads and writes documents, and the types below are the contract
anything that authors one writes against.

## Decision

`Bytex.Documents` carries a **strategy document**: a typed dataflow graph plus
a phase machine, stored as JSON.

### Document

```
schemaVersion  "1.0"                       additive within 1.x
id, name, description
metadata       { template, tags, createdWith, difficulty, runMode, marketRegime, author }   runMode: constant | firedOnce
instruments[]  { ref, instrumentId }       the first is the primary instrument
barTypes[]     { ref, instrument, step, aggregation, priceType, source }   the first drives evaluation
parameters[]   { name, type, value, min, max, step, label, description, choices }   what sweeps vary
nodes[]        { id, type, params, label, disabled }
edges[]        { from: "node:port", to: "node:port" }
phases[]       { id, name, nodes[], initial }   nodes outside every phase are always active
transitions[]  { from, to, on: "node:port" }
repeat         { enabled }                 false: the run completes when the first position closes
modes.live     { allowAiAnnotationConditions }
layout         opaque front-end hints, ignored by the engine
```

A node's `params` stay raw JSON in the schema: the catalog's parameter schema
for the node type describes their shape, so a new node type is a new catalog
entry and never a schema change. `ref` values (`instruments[].ref`,
`barTypes[].ref`) are local names the rest of the document points at, so a
document does not repeat instrument ids and can be retargeted by editing one
line. Ports are `node:port`, split on the **last** colon, because an id may
contain one.

### JSON conventions

The document follows the engine's conventions
([0002](0002-numeric-and-time-model.md)): camelCase names, enums as camelCase
strings, and anything that is money, a price, or a size written as a string and
read as `decimal`, so no such value passes through a binary float. Nulls are
not written, comments and trailing commas are accepted on the way in, and a
document that reads as nothing is refused rather than treated as empty.
`DocumentJson` holds the options, the serializer, and the reader, so no caller
assembles them by hand.

### Parameter references

Every numeric node parameter accepts either a literal or a reference to a
document parameter:

```json
{ "id": "fast", "type": "ind.ema", "params": { "period": { "$param": "fast" } } }
```

`ParamValue` is that value in C#: a literal `decimal` or a parameter name, with
`Resolve` binding it against the run's parameters and failing loudly on a name
the document never declared. A reference stays a reference through a round trip
rather than collapsing into the number it last resolved to, which is what makes
a document sweepable: the sweep varies `parameters[]`, and every node that
referenced a varied parameter follows.

### Annotations

`Annotation : CustomData` carries an event - a calendar item, a venue status, a
piece of news - with a time, an optional end, a category, a severity, a list of
scopes, and a provenance. Scopes are global, an asset class, a currency
(optionally pinned to the base or the quote side), a venue, or one instrument,
and an annotation applies to an instrument when any of its scopes does, so a
rate decision scoped to `USDT` as the quote currency reaches every USDT pair
without naming them. `IsAiClassified` is true when the provenance starts with
`ai:`; `modes.live.allowAiAnnotationConditions` is the document's explicit
opt-in before a strategy may let such an annotation gate it in live trading,
and the default is off. Annotations are ordinary engine data, so they replay in
a backtest exactly as they arrived live.

## Alternatives considered

- **A scripting language (expressions in strings).** Rejected: harder to check
  without running, harder to render as cards, and harder to explain; a graph is
  what a builder draws anyway.
- **Typed node parameters in the schema.** Rejected: every new node type would
  become a schema change, and the schema would have to know the catalog. Raw
  JSON plus a catalog schema keeps the format additive.
- **Binary floats for numbers.** Rejected for the same reason the engine rejects
  them everywhere else: a price that survives a file must be exact.

## Consequences

- A strategy built in a visual tool is a file the open engine reads without
  that tool.
- `schemaVersion` is `1.0` and additive within 1.x: a reader of a 1.x document
  may meet members it does not know and must ignore them.
- The format admits documents that cannot run - an edge between incompatible
  ports, a phase nothing enters. That is deliberate: judging a document is the
  validator's decision, not the format's.
