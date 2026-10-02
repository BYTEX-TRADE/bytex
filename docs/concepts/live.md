# Live trading

## Trading node

`TradingNode` builds a tradingRuntime on a `LiveClock`, creates data and execution
clients from registered factories, wires them to the tradingRuntime thread through
dispatching sinks, and runs strategies. `TradingNodeConfig` (JSON-friendly):

| Field | Purpose |
|---|---|
| `tradingRuntime` | moduleHost id, environment (`sandbox` or `live`), cache/risk/engine settings |
| `dataClients`, `executionClients` | `{ factory, clientId, config }` entries; the factory name selects the adapter and the config type |
| `strategies`, `runtimeModules`, `orderSchedules` | definitions resolved through the plugin registry |
| `reconcileOnStart`, `reconciliationLookback` | venue reconciliation before strategies start |
| `reconciliationInterval` | how often the node checks itself against its venues while it runs; zero checks only at start-up |
| `cancelOrdersOnStop`, `closePositionsOnStop` | shutdown behaviour |
| `connectionTimeout`, `queueCapacity`, `heartbeatInterval`, `pluginDirectory` | operational settings |

## Threading

The `LiveTradingRuntimeLoop` runs the tradingRuntime on one dedicated thread fed by a bounded
channel. Adapters receive callbacks on their own I/O threads and post into the
loop; timer callbacks are posted the same way. `RuntimeModule.Post` and
`RunInBackground` give strategies the same facility.

A start that fails part-way leaves the node as it was found: the clients that
had already connected are disconnected, the heartbeat is cancelled, and
`StartAsync` rethrows what went wrong. The node can be started again once the
venue is reachable; disposing it ends the tradingRuntime thread whether it ever ran or
not.

## Reconciliation

On start, each execution client produces an `ExecutionMassStatus` (open
orders, recent fills, positions). The order coordinator then:

1. creates cached orders for venue orders it does not know (assigned to the
   strategy claiming the instrument via `ExternalOrderClaims`, otherwise to
   `EXTERNAL`);
2. generates the events a known order is missing (accepted, triggered,
   updated, fills, cancelled/expired);
3. synthesises a fill when the venue reports more filled quantity than the
   fills it returned;
4. compares net positions with the venue and takes the venue's word for the
   difference, booking it as a fill on an order of its own so that a position
   the node was not carrying exists with the venue's average price behind it.

Reconciliation is the only place an external order is adopted. A venue's
private stream carries everything that happens to an **account** - a person
trading the same key by hand, another node on the same key, an order left
behind by a previous run - and an adapter ignores a stream message for an order
this node never placed, matching on the client order id and on the venue order
id before it does. It is not attributed to `EXTERNAL` there: the engine looks a
stream event up by both ids and, finding neither, logs it as an event for an
unknown order and drops it, so the attribution would produce a log line and
nothing else, and an `ExternalOrderClaims` claim would never be honoured
because claims are read here rather than in a stream handler. What a person
does by hand at the venue therefore appears at the next reconciliation pass,
under the claiming strategy where there is one.

### While it runs

`reconciliationInterval` (or `bytex run --reconcile-interval 00:05:00`) repeats
the check for as long as the node runs, which is what notices a fill whose event
never arrived, an order cancelled at the venue, or a position somebody closed by
hand. Zero, the default, leaves it to the check at start-up: a mass status costs
a request to every venue and some of them count those, so how often it is worth
paying for is the host's to say.

Every check after the first asks only for what has happened since the one before
it, with one interval of overlap, rather than re-reading the whole lookback. A
check is skipped while any order is in flight - the venue cannot report what it
has not acknowledged, so a difference found then is a conversation in progress
rather than a disagreement - and while a check is still running.

`OrderCoordinator.ReconciliationCount`, `ReconciledDifferences` and
`LastReconciliation` say how many checks there have been, how many times the
venue and the node disagreed, and when the last one was; the node reports all
three over its control channel, so a host can watch a node drifting apart from
its venue instead of finding out at the next restart.

## Sandbox execution

`SandboxExecutionClient` (factory `SANDBOX`) wraps the backtest venue. It
subscribes to all quotes, trades, and bars for its venue on the message bus
and matches orders against them, so any live data client can be paired with
simulated execution. Its account id is `{VENUE}-SANDBOX`.

**It matches against the venue's book as well.** With `MatchAgainstBook` (on by
default) it asks the data client for the book of every instrument it holds and
matches against the book the cache maintains - the same thing a backtest matches
against, by the same route. That is where depth is worth having: paper is the
step before somebody risks money, and whether an order would have filled, and at
what, is the question a book answers and a bar does not. The data is live, free
and already streaming, and none of it is stored. `SubscribeOrderBook = false`
leaves the asking to whatever else wants depth; `MatchAgainstBook = false`
leaves the venue on quotes and bars as it was before 0.6.

## Persistence

With `CacheConfig.Persist = true` and a `RedisCacheDatabase`, the cache writes
orders (as event streams), positions (as fill streams), accounts, instruments,
currencies, order lists and runtimeModule state to Redis and reloads them at startup,
so a restarted node continues with its previous state before reconciling with
the venue. Market data is not written: it is a rolling window of what just
happened, and it is re-subscribed on start.

The keys, what is stored and what is deliberately not, and how to publish the
node's bus to Redis streams for another program to read, are in
[Redis](redis.md).

## Changing what a node runs

A node does not have to be restarted to gain or lose a strategy (R5.10).
`NodeController` - or the five control-channel commands over it - adds a
strategy, starts it, stops it and takes it off again while everything else the
node runs carries on.

```csharp
NodeController controller = new(node);
await controller.AddFromPathAsync("strategies/ema-cross.json");   // registered, stopped
await controller.StartAsync(new StrategyId("EmaCross-001"));
await controller.StopAsync(new StrategyId("EmaCross-001"), cancelOrders: true);
await controller.RemoveAsync(new StrategyId("EmaCross-001"));
```

What it refuses is the point:

- A strategy is added **stopped**, and starts when somebody says so.
- **Stopping does nothing** to the orders and positions it was holding unless it
  is asked to cancel or close them. The counts come back either way.
- A strategy still holding orders or positions is **not removed**: removing it
  takes away the thing that would manage them.

A path, not a document, names the strategy to add - the node reads its own
files, as it does with its keys - and `TradingNodeConfig.StrategyFromPath` is
how a host says what a path means. The protocol is in
[design note 0009](../design/0009-node-control-protocol.md).

## Shutdown

`StopAsync` optionally cancels all open orders and/or submits reduce-only
market orders to flatten positions, waits briefly for acknowledgements, stops
the tradingRuntime, disconnects clients, and drains the loop. `RunAsync(ct)` does all
of this on cancellation (the CLI maps `Ctrl+C` to it).
