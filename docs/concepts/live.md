# Live trading

## Trading node

`TradingNode` builds a kernel on a `LiveClock`, creates data and execution
clients from registered factories, wires them to the kernel thread through
dispatching sinks, and runs strategies. `TradingNodeConfig` (JSON-friendly):

| Field | Purpose |
|---|---|
| `kernel` | trader id, environment (`sandbox` or `live`), cache/risk/engine settings |
| `dataClients`, `executionClients` | `{ factory, clientId, config }` entries; the factory name selects the adapter and the config type |
| `strategies`, `actors`, `execAlgorithms` | definitions resolved through the plugin registry |
| `reconcileOnStart`, `reconciliationLookback` | venue reconciliation before strategies start |
| `cancelOrdersOnStop`, `closePositionsOnStop` | shutdown behaviour |
| `connectionTimeout`, `queueCapacity`, `heartbeatInterval`, `pluginDirectory` | operational settings |

## Threading

The `LiveKernelLoop` runs the kernel on one dedicated thread fed by a bounded
channel. Adapters receive callbacks on their own I/O threads and post into the
loop; timer callbacks are posted the same way. `Actor.Post` and
`RunInBackground` give strategies the same facility.

## Reconciliation

On start, each execution client produces an `ExecutionMassStatus` (open
orders, recent fills, positions). The execution engine then:

1. creates cached orders for venue orders it does not know (assigned to the
   strategy claiming the instrument via `ExternalOrderClaims`, otherwise to
   `EXTERNAL`);
2. generates the events a known order is missing (accepted, triggered,
   updated, fills, cancelled/expired);
3. synthesises a fill when the venue reports more filled quantity than the
   fills it returned;
4. compares net positions with the venue and logs mismatches.

## Sandbox execution

`SandboxExecutionClient` (factory `SANDBOX`) wraps the backtest venue. It
subscribes to all quotes, trades, and bars for its venue on the message bus
and matches orders against them, so any live data client can be paired with
simulated execution. Its account id is `{VENUE}-SANDBOX`.

## Persistence

With `CacheConfig.Persist = true` and a `RedisCacheDatabase`, the cache writes
orders (as event streams), positions (as fill streams), accounts, instruments,
currencies, and actor state to Redis and reloads them at startup, so a
restarted node continues with its previous state before reconciling with the
venue.

## Shutdown

`StopAsync` optionally cancels all open orders and/or submits reduce-only
market orders to flatten positions, waits briefly for acknowledgements, stops
the kernel, disconnects clients, and drains the loop. `RunAsync(ct)` does all
of this on cancellation (the CLI maps `Ctrl+C` to it).
