# Strategy documents

A strategy document is a strategy as data: a JSON file describing a graph of
nodes (data, indicators, conditions, actions, risk, flow) and the phases the
strategy moves through. A visual builder writes one; the open engine reads it
without that builder.

`Bytex.Documents` is the format, the catalog, the validator and the runtime: a
document is read, described, checked and run. The same document backtests,
paper trades and trades live, exactly like a hand-written strategy.

The catalog is complete at 80 types: the families a graph reads from (data,
indicators, levels) and the families it acts with (conditions, actions, risk,
flow, events).

## Anatomy

```json
{
  "schemaVersion": "1.0",
  "id": "ema-cross",
  "name": "EMA cross",
  "instruments": [ { "ref": "primary", "instrumentId": "BTCUSDT.BINANCE" } ],
  "barTypes":    [ { "ref": "main", "instrument": "primary", "step": 1, "aggregation": "minute", "priceType": "last", "source": "external" } ],
  "parameters":  [ { "name": "fast", "type": "int", "value": "10", "min": "2", "max": "200" } ],
  "nodes": [
    { "id": "bars",    "type": "data.bars",  "params": { "barType": "main" } },
    { "id": "fast",    "type": "ind.ema",    "params": { "period": { "$param": "fast" } } },
    { "id": "slow",    "type": "ind.ema",    "params": { "period": 30 } },
    { "id": "crossUp", "type": "cond.cross", "params": { "direction": "above" } },
    { "id": "buy",     "type": "act.order",  "params": { "side": "buy", "orderType": "market", "sizing": { "mode": "fixed", "value": "0.5" } } }
  ],
  "edges": [
    { "from": "bars:bars", "to": "fast:bars" }, { "from": "bars:bars", "to": "slow:bars" },
    { "from": "fast:value", "to": "crossUp:value" }, { "from": "slow:value", "to": "crossUp:reference" },
    { "from": "crossUp:out", "to": "buy:trigger" }
  ]
}
```

- **Instruments and bar types** are named once under a `ref` and referred to by
  that name, so retargeting a document to another symbol is one edit. The first
  instrument is the primary one; the first bar type drives evaluation.
- **Edges** connect ports written as `node:port`. The colon that separates them
  is the last one, so a node id may contain a colon.
- **Parameters** are what a sweep varies. Any numeric node parameter can be
  `{"$param": "name"}` instead of a number, and stays a reference when the
  document is written back out.
- **Phases** group nodes that are only active while the phase is; transitions
  fire on a port such as `buy:filled`. Nodes outside every phase are always
  active, which is where actions and risk nodes usually belong.
- **Fired-once** documents (`repeat.enabled = false`) finish when their first
  position closes.
- **An action node owns the orders it places.** It tags them with its own id,
  follows them to a terminal state, and publishes what happened: `act.order`
  and `act.bracket` their submission, fill and rejection, `act.close` the price
  it got the position out at. A node that owns its orders can also find them
  again after a restart, which is how an exit goes on protecting a position the
  node did not open in this run.
- **Layout** is whatever a builder needs to redraw the graph. The engine reads
  and preserves it and never interprets it.

## The catalog

A document names node types as strings. What those strings mean lives in the
node catalog, one `NodeTypeDescriptor` per type: its kind, a display name, a
plain-language face such as `Donchian {period}`, its typed input and output
ports, and its parameters with their limits, units and choices.

An `enum` parameter's choices are not bare strings. Each one carries the name to
show, one sentence saying what picking it does, and - when the choice decides
how the number beside it is read - that parameter's name and the unit the number
is then in, plus the range and step it should take there. This is not decoration:
`act.order` has one box labelled "Value" whose meaning changes with the sizing
mode, and `riskPercent` sizes the position so that a stop-out costs that
percentage of the balance, which with a near stop is a far larger position than
"percent" suggests. A document still stores the bare value, and the JSON Schema
still lists exactly those values.

### Working an order in pieces

`act.order` sends its order to the venue in one piece unless its `work`
parameter says otherwise. `twap` hands it to an execution algorithm that cuts it
into equal pieces and sends them at an even pace, so a size the book cannot take
at once is not paid for in slippage:

```json
{ "work": { "algorithm": "twap", "horizonMinutes": "5", "intervalMinutes": "1" } }
```

Five minutes, a piece a minute: five pieces. The horizon divided by the interval
is how many there are, and a piece the venue would refuse as too small is not
sent on its own - the order is then cut into as many pieces as it can be.

Nothing has to be registered by the host. A document that names `twap` is a
document that cannot run without one, so the trader starts a `TwapExecAlgorithm`
with it, under the id `TWAP`; a host that has its own tuned algorithm registers
it under that id and its own runs instead, whichever of the two was added first.

The node still reports one order, because one order is what the document asked
for: `working` stays true until the last piece is done, `filled` fires once,
and `fillPrice` is what the pieces averaged - which is what a time-weighted
average price is. When a piece is refused and the pace runs out with part of the
order in, the node reports that much rather than waiting for the rest forever.
Only a market or a limit order can be worked this way, and `cancelAfterBars` is
not a bound on a worked order - the horizon is - so the validator refuses a
document that asks for either combination
(`ORDER_CANNOT_BE_WORKED`).

### Sizing

Beside the mode and its number, sizing carries a **ceiling**: `maxNotional`,
the most the position may be worth in the quote currency, and
`maxPercentOfBalance`, the most it may be as a share of the free quote balance.
They are read after the mode has worked its number out, the smaller of the two
holds, and both are zero by default - which is no ceiling, so a document written
before they existed sizes exactly as it did.

A mode says *how* to size and cannot say how much of the account may be in one
order. `riskPercent` is the case that needs the difference: the nearer the stop,
the larger the position it buys, so "risk one percent" with a stop a tenth of a
percent away is the whole account. With `maxPercentOfBalance: 25` the same rule
risks one percent and never holds more than a quarter of the balance, and when
the ceiling holds an order back the run log says what was asked for and what was
allowed.

```json
{ "sizing": { "mode": "riskPercent", "value": "1", "maxPercentOfBalance": "25" } }
```

```csharp
ParamSpec mode = NodeCatalog.Default.Find("act.order")!.Param("sizing")!
    .Fields!.Single(f => f.Name == "mode");

mode.ChoiceValues;                       // fixed, notional, percentOfBalance, riskPercent
ChoiceSpec risk = mode.Choice("riskPercent")!;
risk.Label;                              // "Percent of the balance to risk on the stop"
risk.Unit;                               // "% of the balance lost if the stop is hit"
risk.Governs;                            // "value" - the number beside it
risk.Max;                                // "100"
```

```csharp
NodeTypeDescriptor ema = NodeCatalog.Default.Find("ind.ema")!;

string face = ema.FaceTemplate;                      // "EMA {period}"
ParamSpec period = ema.Param("period")!;             // int, default 20, min 1
ValueKind output = ema.Output("value")!.Kind;        // series
bool wired = PortSpec.Compatible(output, ValueKind.Price);   // true
```

Port kinds are `bars`, `series`, `price`, `quantity`, `bool`, `pulse` and
`position`. `PortSpec.Compatible` is the whole wiring rule: the numeric kinds
are interchangeable, a `pulse` feeds anything that reads a `bool` but not the
reverse, and `bars` and `position` connect only to themselves.

| Family | Types |
|---|---|
| Data (7) | bars, quotes, trades, book, mark price, funding, position |
| Indicators (20) | every engine indicator, plus slope, distance, z-score |
| Levels (9) | range, swing points, session levels, VWAP bands, Fibonacci, pinned price, previous bar, regression channel, round numbers |
| Conditions (14) | cross, compare, inside band, hold for N bars, pin bar, engulfing, divergence, time window, session phase, after N bars, all of, any of, not, once |
| Actions (13) | place order, bracket, ladder, safety orders, grid, cancel, close, modify, trail, move stop, scale out, publish signal, note |
| Risk (8) | exit and risk, position size, max positions, max orders, daily loss, cooldown, exposure, no entry near events |
| Flow (6) | every N bars, counter, latch, complete run, gate, delay |
| Events (2) | near an event, event window active |

A type's name says which family it belongs to, and a family may group its types
further (`cond.pattern.pinBar`, `cond.time.window`). Every action reads a
condition telling it when to act - `act.trail` is enabled for as long as the
condition holds, the rest fire on it - and every condition publishes something
another condition can read.

The built-in catalog is version 1 and additive: a new type is a new
descriptor, never a change to the document format. A plugin registers its own
`custom.*` types into a catalog and they are described - and exported - exactly
like the built-in ones.

Two exports take the catalog out of the process:

```csharp
string catalog = NodeCatalog.Default.ExportJson();     // the types, for a palette or an assistant
string schema = DocumentSchemaExporter.ExportJson();   // a JSON Schema for a whole document
```

The JSON Schema (draft 2020-12) allows exactly the node types of the catalog it
was given and carries each type's parameter schema under `$defs/nodeParams`, so
a builder can check a document before the engine ever reads it. Wherever a
number is allowed, the schema accepts a number, a numeric string or a
`{"$param": "name"}` reference.

See [design note 0010](../design/0010-node-catalog.md) for the catalog
contract.

## Numbers, names, and nulls

`DocumentJson` holds the conventions: camelCase names, enums as camelCase
strings, prices, sizes, and money as strings parsed to `decimal`, nulls left
out, comments and trailing commas tolerated on the way in.

```csharp
StrategyDocument document = DocumentJson.Deserialize(File.ReadAllText("ema-cross.json"));
string json = DocumentJson.Serialize(document);

ParameterDef? fast = document.Parameter("fast");
NodeDef? entry = document.Node("fast");
ParamValue period = DocumentJson.ReadParamValue(entry!.Params!.Value.GetProperty("period"))!.Value;
decimal resolved = period.Resolve(new Dictionary<string, decimal> { ["fast"] = 8m });
```

`schemaVersion` is `1.0` and grows additively within 1.x: a reader may meet
members it does not know and ignores them.

## Running one

```json
"strategies": [
  { "providerId": "bytex.document", "name": "EMA cross", "payload": { "document": { "...": "..." }, "parameterOverrides": { "fast": "8" } } }
]
```

The provider `bytex.document` builds a `DocumentStrategy` from that payload -
the bare document works too - so `bytex backtest --config run.json` runs a
document without a line of C#. The command group `bytex documents` validates one
(`--catalog` adds the instrument and data checks, `--environment live` the live
rules), prints the node catalog, and prints the JSON Schema.

The runtime compiles the document once at start: nodes in topological order with
document order as the tie-break, each node's bar type propagated from the
`data.bars` node it reads, and the warm-up it needs taken from its parameters.
Then, on every close of the primary bar type, it evaluates the graph into a
frame of one value per output port. `LastValues` holds that frame, `Decisions`
holds every fire, skip, order and transition the run produced, and
`OnSave`/`OnLoad` carry the phase and the stateful nodes across a restart. An
action node places ordinary orders through the strategy's own order factory, so
the risk engine, the cache and the reports see nothing unusual.

See [design note 0012](../design/0012-document-runtime.md) for what the runtime
guarantees.

## The examples

Three documents ship inside `Bytex.Documents`, and `bytex documents examples
--out documents` writes them where you can read and edit them:

| Document | What it does |
|---|---|
| `ema-cross` | Long when the fast EMA crosses above the slow one, flat when it crosses back. The document twin of the `EmaCross` C# example. |
| `breakout-retest` | Buys a close that breaks above the 20-bar range while RSI is below 70, protects it with a stop under the range low, targets 2R and trails to breakeven after 1R. |
| `support-bounce` | Waits for price to hold above the range low for several bars, buys the bounce, stops under support and targets the range midpoint. |

`examples/configs/backtest-document.json` runs the first of them:

```bash
bytex documents examples --out ./documents
bytex backtest --config examples/configs/backtest-document.json
```

Each example validates with nothing to report, runs over bars, and ends on the
same fingerprint of orders, fills and equity when it is run twice - the tests
hold all three to that, so an example cannot rot unnoticed.

## Validation

```csharp
DocumentValidator validator = new();                 // NodeCatalog.Default unless another is given
ValidationReport report = validator.Validate(document);

if (!report.IsValid)
{
    foreach (Finding block in report.Blocks)
    {
        Console.WriteLine(block);                    // BLOCK PORT_TYPE_MISMATCH [cross]: Cannot wire ...
    }
}
```

Findings come at three levels. **Block** means the document must not run - an
edge between incompatible ports, a required input left unconnected, a cycle, a
strategy that never places an order, a size below the instrument's minimum.
**Warning** means it will run and someone should look - no exit rule, a phase
nothing leads into, a price off the tick, less data than the warm-up needs.
**Info** is a remark, such as a node wired to nothing.

Each finding carries a reason code, the node and port it concerns, and often a
fix. The code is the part to match on: `NO_ENTRY`, `PORT_TYPE_MISMATCH`,
`SHORT_ON_SPOT`, `PARAM_OUT_OF_RANGE` and the rest are stable, while messages
are written for people and may be reworded.

The check runs in three layers - the document as a document, the strategy as a
strategy, then the strategy against the world it will run in - and stops at the
first layer that found a reason to stop, which `report.StoppedAt` names. The
third layer needs to know about instruments and data:

```csharp
ValidationReport checked = validator.Validate(document, context);   // context: IValidationContext
```

Without a context that layer is skipped rather than guessed at, so a document
can be checked with no catalog, no data and no engine at all. Caps on nodes,
edges, phases, parameters and lookback are `ValidatorOptions`, not part of the
format.

See [design note 0011](../design/0011-document-validation.md) for the layers
and the levels.

## Annotations

An `Annotation` is an event carried into a run - a calendar item, a venue
status, a piece of news - with a time, an optional end, a category, a severity,
and a scope. Scopes are global, an asset class, a currency (optionally only
where it is the base or the quote), a venue, or one instrument, and an
annotation applies wherever any of its scopes matches:

```csharp
Annotation rates = new(Guid.NewGuid(), ts, ts, null,
    [AnnotationScope.ForCurrency("USDT", CurrencyRole.Quote)],
    category: "calendar", AnnotationSeverity.Warning, "Rate decision", null, null, provenance: "calendar:fed");

bool applies = rates.AppliesTo(instrument);
```

Annotations are engine data, so they replay in a backtest as they arrived live.
`Provenance` records who classified one, and `IsAiClassified` is true when it
starts with `ai:`. A document must opt in through
`modes.live.allowAiAnnotationConditions` before such an annotation may gate a
strategy in live trading; the default is off.

See [design note 0008](../design/0008-strategy-documents.md) for the format,
[0010](../design/0010-node-catalog.md) for the catalog,
[0011](../design/0011-document-validation.md) for validation and
[0012](../design/0012-document-runtime.md) for the runtime.
