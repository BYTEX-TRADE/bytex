# 0010 - The node catalog

Status: accepted

## Problem

A strategy document ([0008](0008-strategy-documents.md)) names node types as
strings: `data.bars`, `ind.ema`, `cond.cross`. The format deliberately says
nothing about what those types mean, so everything that handles a document -
a palette offering the types, an inspector editing one node's parameters, a
validator judging a graph before it runs, a runtime evaluating it, an
assistant explaining or writing one - would otherwise carry its own copy of
that knowledge, and the copies would drift.

## Scope

This note decides the **catalog**: how a node type is described, how the
description is served, and how it is exported for anything outside the engine.
The runtime that builds a node from its description is a separate decision.

## Decision

`Bytex.Documents.Catalog` holds one `NodeTypeDescriptor` per node type and a
`NodeCatalog` that serves them. `NodeCatalog.Default` is the built-in catalog,
version 1.

### A node type

```
type          "ind.donchian"            the string a document writes
kind          data | indicator | level | condition | action | risk | flow | event | custom
displayName   "Donchian Channel"
description   one sentence, what it is
faceTemplate  "Donchian {period}"       plain language with {placeholders}
inputs[]      { name, kind, required, label, description }
outputs[]     { name, kind, ... }
params[]      { name, type, required, default, min, max, step, unit, choices, label, description, fields }
choices[]     { value, label, description, unit, governs, default, min, max, step }
modes         which of lab, paper and live the type may run in
```

The **face template** is what a card shows and what a plain-language rendering
of a strategy reads from, so a description is enough to draw a node; every
`{placeholder}` names a parameter or a port of the same type.

**Port kinds** are `bars`, `series`, `price`, `quantity`, `bool`, `pulse` and
`position`. `PortSpec.Compatible` is the one rule about wiring: the numeric
kinds are interchangeable, because a price and a quantity are numbers and a
node that reads a number does not care which; a `pulse` (true for one bar) may
feed anything that reads a `bool`, but not the reverse; `bars` and `position`
are handles and connect only to themselves.

**Parameter types** are `int`, `decimal`, `bool`, `string`, `enum`, `object`
(nested fields), `instrument`, `barType` and `time`. A parameter a builder has
to seed carries a default; a reference to something the document declares does
not, because an absent instrument means the primary one. `IsNumeric` marks the
parameters a document may bind to a document parameter with
`{"$param": "name"}`.

**A choice carries its own meaning.** An `enum` parameter's choices are
`ChoiceSpec` records, not strings: the value a document stores, the name to show
in its place, and one sentence saying what picking it does. A choice that decides
how a number beside it is read also names that parameter in `governs`, says what
the number is then measured in, and may carry the range and step it should take
under that choice. The reason is `act.order`: its size is a `mode` and a number
labelled "Value", and the mode changes what the number means entirely - under
`riskPercent` the quantity is chosen so that a stop-out costs that percentage of
the balance, so the nearer the stop the larger the position, which is the
opposite of what a reader takes from the word "percent". Leaving that to a
single prose sentence on the parent parameter left it to be guessed at both
ends: a dropdown showed the identifier, and an assistant reading the catalog
inferred the rest. The catalog's own tests refuse a choice with no sentence, and
refuse a `unit`, `mode` or `to` enum standing beside a number unless every
choice says which number it reads and in what - or says the number is not used.
What a document stores is unchanged, and the exported JSON Schema still lists
the bare values.

### Serving and exporting

`NodeCatalog` finds a type by name, refuses to register one name twice, and
takes types a plugin brings, so `custom.*` types are described the same way as
the built-in ones. Two exports carry the catalog out of the process:

- `ExportJson()` writes the catalog itself - every type with its ports,
  parameters and face - grouped by kind and ordered by name inside a kind, so
  two exports of one catalog diff cleanly.
- `DocumentSchemaExporter` writes a JSON Schema (draft 2020-12) for a whole
  document, with the node `type` enum taken from the catalog and a per-type
  parameter schema under `$defs/nodeParams`, so a builder can validate a
  document on the client before the engine sees it. Every numeric parameter
  points at one `paramValue` definition that admits a number, a numeric string
  or a `$param` reference.

### Versioning

`CatalogVersion` is `1.0` and the catalog is additive within 1.x: a new node
type is a new descriptor, never a schema change, and a reader that meets a
type it does not know rejects that one node rather than the document format.
Removing a type or changing what its parameters mean is a new catalog version.

## Alternatives considered

- **A schema per node type in the document format.** Rejected: every new type
  would become a format change, and every reader would have to know the
  catalog to read a document at all.
- **Describing types in the code that runs them.** Rejected: a palette, a
  validator and an assistant would have to load the runtime - and in a host
  that only edits documents, there is no runtime to load.
- **Free-form parameters with no specs.** Rejected: an inspector could not
  offer limits or choices, and a validator could only fail at run time, where a
  reason code costs a whole backtest to obtain.

## Consequences

- One description serves the palette, the inspector, the validator, the
  runtime and the assistant; a type that is wrong in one is wrong in all, which
  is why the catalog's own tests check every descriptor rather than sampling.
- Version 1 holds 80 types in eight families. A type's name carries its family,
  and a family may group its types further (`cond.pattern.pinBar`).
- A host that only edits documents needs `Bytex.Documents` and nothing else.
- A plugin's node types appear in the export beside the built-in ones, so
  tooling needs no special case for them.
