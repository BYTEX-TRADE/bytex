# 0009 — Node control protocol

Status: accepted

## Problem

A host application (a desktop client, an operator script) needs to watch a
live node and ask it to stop, cancel, or flatten, without holding the
node's venue credentials and without the node exposing a network surface.
Log scraping is not enough: the host needs typed events and a way to send
commands.

## Decision

`bytex run --control <name>` serves a **control channel**: newline-delimited
JSON over a named pipe on Windows (`\\.\pipe\<name>`) or a Unix domain
socket elsewhere (`$TMPDIR/bytex-<name>.sock`, or an absolute path). One
client at a time; the node keeps running whether or not anyone is
connected.

Credentials stay out of the host entirely: `bytex run --env-file keys/venue.env`
loads `KEY=VALUE` lines into the node process itself, so the launcher passes a
path and never reads the file. `bytex verify-keys --venue … --env-file …`
checks a key the same way, in its own process, and prints what the key can
do (authentication, permissions, withdrawal rights, IP restrictions)
without printing the secret.

### Messages

Every line is `{ "type": string, "payload": object | null, "ts": unixNanos }`.

Node → host:

| type | payload |
|---|---|
| `hello` | `{ version, pid, traderId, environment }` on connect |
| `heartbeat` | `{ traderId, environment, running, tradingState, halted, limits, prices?, queue, processed, orders, positions, strategies[] }` every `--control-heartbeat` (default 5 s) |
| `event` | `{ channel: order \| position \| account \| decision, data }` — order events (fills carry quantity, price, commission, liquidity), position events, account states, and `StrategyEvent` decisions from document strategies |
| `status` | as heartbeat, in reply to a `status` command |
| `view` | the node as a monitor draws it, in reply to a `view` command; see below |
| `bye` | `{ reason }` before the node stops |

Host → node:

| type | payload | effect |
|---|---|---|
| `status` | — | reply with `status` |
| `view` | — | reply with `view` |
| `cancel-all` | — | every strategy cancels all its open orders |
| `flatten` | — | cancel all and close every position with reduce-only market orders |
| `stop` | `{ cancelOrders, closePositions }` | optional flatten, then the node stops as on Ctrl+C |
| `halt` | `{ cancelOrders, closePositions }` | the node stops trading and keeps running: every order is denied with `TRADING_HALTED`. The optional flatten happens before the halt is in force; the node answers with a `status` once it is |
| `resume` | — | a halted node trades again; answered with a `status` |
| `limits` | a `RiskLimits` object | replaces what the node's risk engine enforces; answered with a `status` carrying what it now holds |

Commands execute on the kernel thread through the live loop; the channel
thread never touches engine state directly. One task writes to the stream:
replies go through the same outbox as events, so lines never interleave. The
pipe is buffered, so a host may send a command before it has read `hello`.

### The switch and the limits (protocol version 3)

`hello.version` is 3 from this message on. Every `status`, `heartbeat` and
`view` carries `tradingState` (`Active`, `Reducing` or `Halted`), `halted`, and
`limits` - the loss and exposure limits, what reaching the loss limit does
(`onLossLimit`: `"stopTrading"` or `"denyAdds"`) and the caps, all written as
the text a host would send back (`"1000.00 USDT"`, `"50%"`, whole numbers for
the caps). A node that stopped its own trading on a loss limit reports
`tradingState: "Reducing"`, and the same `resume` command releases it.
A host halts a node, releases it and sets its limits with the three commands
above; `bytex run --halted` and the `--max-*` options do the same at start-up,
and `kernel.riskEngine.limits` in the node's configuration does it in the file.

Every `status`, `heartbeat` and `view` also carries `reconciliation`:
`count`, `differences`, `lastTs` and the `interval` the node was configured
with. `differences` is what the node had to take its venue's word on, and a
number that keeps climbing is a node drifting apart from its venue.

A node asked to show prices (`displayPrices`, or `bytex run --display-prices`)
also carries `prices`: one entry an instrument, with the same `lastPrice` shape
the view uses, so a monitor drawing a node from heartbeats alone has a price
that moves between bars. A node that was not asked leaves `prices` out
entirely, which is how a host tells "not asked for" from "asked for and not
arrived yet".
Nothing about the switch needs the node to be restarted, and a halted node
keeps its strategies, its subscriptions and its state.

### What a venue is matching against (protocol version 5)

`hello.version` is 5 from this message on. Every `view` carries `venues`: one
entry for each venue that matches its own orders - a paper venue, a simulated
one - and for each of them one row an instrument.

```
venues: [ { venue, instruments: [ { instrumentId, against, wantsBook, bookRequested } ] } ]
```

`against` is `"book"`, `"quotes"`, `"bars"` or `"nothing"`: **the best thing the
venue has for that instrument right now, not what it was configured to want.**
A venue told to match against a book but not yet sent one says `quotes`, because
that is what its fills would come out of. `wantsBook` is the configuration
(`MatchAgainstBook`) and `bookRequested` says whether it has asked its data
client for that instrument's depth.

A fill answers "would this have filled, and at what", and the answer is worth
very different amounts measured against a book, a quote or a bar - a book says
where in the queue the order stood and what the depth cost, a bar says what
traded over a whole minute. A host showing a paper fill can now say which it
was. `venues` is **left out entirely** when no venue at this node matches
anything itself, which is every live node: that is how a host tells "no such
venue" from "a venue that has been sent nothing yet".

### Where a working order stands (protocol version 4)

`hello.version` is 4 from this message on. Every order in a `view` carries
`sizeAhead` - how much size is still resting ahead of it at its price - and
`queuePosition`, its place among the node's own orders at that price and side,
1 being the one nearest the front. Both are left out for an order standing in
no queue: one that never rested, or a stop still waiting for its trigger, which
is a promise to the venue rather than size in the book. `sizeAhead` of zero is
a different thing from no `sizeAhead` at all: it is an order at the front of
its queue.

The node works this out from the book and the prints it is given, because a
venue matches on a sequence it never shows anybody. A print at the price serves
the queue in front first; a trade past the price clears it, since the market
could not have traded there otherwise; and a level that has grown smaller since
the order joined is size that has gone, whether it was traded or cancelled. A
node with no book data reports every resting order at the front of its queue,
which is what it knew before this existed.

### The `view` payload (protocol version 2)

`view` was added in version 2; a host checks the version before it relies on
`view`. Prices, quantities and money are invariant text, times are nanoseconds
since the Unix epoch, and a value that is null is left out of the JSON.

```
{ traderId, environment, running,
  accounts:   [ { accountId, balances: [ { currency, total, free, locked } ] } ],
  strategies: [ { id, state,
      document?,                      // what the strategy says about itself (IStrategyMonitorView), else absent
      instruments: [ { instrumentId, lastPrice?: { price, kind: "trade" | "quote", ts, bid?, ask? } } ],
      positions:   [ { positionId, instrumentId, side, quantity, avgPxOpen, unrealizedPnl?, realizedPnl, currency, tsOpened } ],
      orders:      [ { clientOrderId, venueOrderId?, instrumentId, side, type, status, quantity, filledQuantity,
                       price?, triggerPrice?, reduceOnly, sizeAhead?, queuePosition?, tags[] } ] } ] }
```

`lastPrice` is the newer of the last trade and the last quote the node holds
(a quote is reported as its mid, with bid and ask); a node that subscribes to
bars only has none. `unrealizedPnl` is marked at the last price, else the mid.
Open orders carry their tags, so a monitor can tell a document's stop
(`exit:stop`) from its target (`exit:target`).

A document strategy's `document` part:

```
{ instrumentId, barType, barIntervalSeconds, lastBarTs?, phase?, barIndex, warmupPending, warmedUp,
  warmup: [ { barType, needed, received, fromHistory, done, first?, last? } ],
  frame?: { index, barTs, phase?, conditions: [ { nodeId, type, label?, output?, inputs: {port: text}, params: {name: text} } ] } }
```

If the view cannot be built the reply is `{ error }`; the node goes on.

### Library

`Bytex.Live.Control` provides `NodeControlServer` (used by the CLI) and
`NodeControlClient` (for hosts): `ConnectAsync(name)`, `ReadAsync()` as an
async stream of `ControlMessage`, and `StopAsync`, `CancelAllAsync`,
`FlattenAsync`, `RequestStatusAsync`, `RequestViewAsync`.

## Alternatives considered

- **An HTTP endpoint in the node.** Rejected: a listening port on a
  credential-holding process is a larger surface than a local pipe, and
  the host would need a token to protect it.
- **Signals only (SIGTERM).** Rejected: no way to say "flatten first" and
  no events back.
- **Relaying through Redis persistence.** Rejected: optional component,
  and polling state is not the same as receiving events.

## Consequences

- Nothing on the channel can leak a secret: no message carries
  configuration, environment, or documents.
- The host learns what the node does the moment it happens, at the
  granularity of engine events, which is what a monitor needs.
- The protocol is versioned in `hello`; additions are new message types or
  new payload fields, never changed meanings.
