# Redis

Two separate things, in one package (`Bytex.Persistence.Redis`) because they
talk to one server:

| | What it is | Requirement |
|---|---|---|
| `RedisCacheDatabase` | the cache's state, written as it changes and reloaded at startup | R6.5 |
| `RedisBusStream` | the node's bus messages, published for other programs to read | R1.11 |

Neither is on by default and neither is needed to trade. A node without Redis
keeps its state in memory and loses it when it stops.

## The cache in Redis

```csharp
RedisCacheDatabase database = new(new RedisCacheConfig { ModuleHostId = "TRADER-001" });
TradingNode node = new(config with { TradingRuntime = config.TradingRuntime with { Cache = new CacheConfig { Persist = true } } }, cacheDatabase: database);
```

With `Persist = true` every state change is written through as it happens, and
`LoadFromDatabase` at startup rebuilds the cache from what is there — so a
restarted node continues with the orders and positions it had before it
reconciles with the venue.

### What is stored, and where

Everything in this section is the **shared** layout, applied by every backing
store this engine has: [PostgreSQL](postgres.md) holds exactly these keys. One
class decides it, compiled into each library, because a rule written twice is a
rule that ends up right in one copy.

Every key begins with `{KeyPrefix}:{ModuleHostId}`, so two moduleHosts on one server
never read each other's state, and `KeyPrefix` (default `bytex`) separates a
staging node from a live one. Beside each kind is an index set, which is how a
reload finds what is there without scanning the server.

| Kind | Structure | Why that structure |
|---|---|---|
| `orders:<client order id>` | list | the order's events, in order; the order is rebuilt by replaying them |
| `positions:<position id>` | list | the fills that opened and changed it, likewise |
| `accounts:<account id>` | list | the account states it has reported |
| `instruments:<instrument id>` | string | JSON |
| `currencies:<code>` | string | JSON |
| `orderlists:<order list id>` | string | the **ids** of its orders, not the orders |
| `runtimeModules:<runtimeModule id>` | hash | whatever the runtimeModule saves, field by field |
| `general:<key>` | string | bytes, as given |
| `index:<kind>` | set | which ids of that kind exist |

An update **appends** rather than rewriting: only the events the server has not
got are pushed. Rewriting the stream on every event would cost the whole
history per event, and pushing what is already there would replay a fill on the
next load.

### Currencies travel with the records that need them

A currency is stored whenever an instrument or an account that uses it is
stored, and before it.

This is not tidiness. Most currencies are built in; a venue's own token is not,
and an unknown code is not an error anywhere — `Currency.FromCode` invents one
at the default precision of eight. Without the currency, an instrument quoted
in a six-place token comes back after a restart quoted in an eight-place
currency of the same name, and every amount in it rounds two places too far
out, silently and for as long as the node runs.

One currency is written once however many instruments share it, and a corrected
precision is written again — two currencies with one code are *equal* here, so
remembering them by code alone would keep the wrong number after a venue fixed
it.

### What is not stored

Quotes, trades, bars and order books are not. They are a rolling window of what
just happened, they are re-subscribed on start, and writing every tick through
to a server is how a slow disk becomes a slow trading loop.

An order list whose orders are not all in the reloaded cache is **not** handed
back as a shorter list. A bracket missing a leg would read as a complete
bracket to everything downstream, and the missing leg is the one that was going
to close the position.

## The bus in Redis

```csharp
RedisBusStream stream = new(
    new RedisBusStreamConfig { ModuleHostId = "TRADER-001", Topics = ["data.quotes", "events.order"] },
    node.TradingRuntime.MessageBus);
```

One Redis stream per topic (`{KeyPrefix}:{ModuleHostId}:stream:{topic}`), so a
reader subscribes to what it wants rather than filtering everything. Each entry
carries four fields:

| Field | Contents |
|---|---|
| `topic` | the bus topic it was published on |
| `type` | the payload's .NET type name |
| `encoding` | `json`, named rather than assumed |
| `payload` | the message |

**The type is carried beside the payload** because a reader cannot infer one
from a topic: several kinds of message share a topic, and a consumer guessing
from the topic decodes the wrong shape the day a second kind appears on it.

**This shape is unstable until 1.0, deliberately and in writing.** Anything
reading these streams pins it, and promising it before the engine's own types
are frozen would be promising on their behalf.

### Nothing is published unless it is asked for

`Topics` is empty by default. Publishing everything a busy node says would be a
decision made for somebody rather than by them.

### A full queue drops, and says so

Writing happens on a thread of its own, and when the queue is full the oldest
messages are dropped. The alternative is back-pressure, which means a slow or
unreachable Redis stalls the thread that is trading — a dashboard falling
behind would slow down the orders it is watching. What is dropped is counted
(`Dropped`, beside `Published`) and logged on disposal, because silently losing
messages while appearing to publish them is the one behaviour worse than
dropping them.

`MaxLength` (default 100,000) bounds each stream so one nobody reads cannot
fill the server. Redis trims to whole nodes rather than to an exact count, so a
stream may hold somewhat more than that and never unboundedly more.

### What this does not do

- **It does not read.** Entries already in a stream when a reader starts are
  that reader's business, and replaying them is not a publisher's decision.
- **No consumer groups.** A reader tracks its own position.

## Connecting

`ConnectionString` is passed to StackExchange.Redis, so anything it accepts
works — `host:port`, several hosts, `password=`, `ssl=true`.

One caveat about the default: `localhost` can resolve to the IPv6 loopback
first, and a server listening only on IPv4 does not answer there — not with a
refusal, which would be quick, but with nothing, so the attempt costs the whole
connect timeout. Use `127.0.0.1:6379` where that happens.

## What has been verified against a server

The store and the stream are tested both ways: against an in-memory double, for
what this engine decides — the key layout, the indexes, what an update appends,
what a reload refuses — and against a real Redis 7.2, for what only a server
can show: that a value survives the client's own conversions, that a list keeps
its order, that a stream is trimmed, that two moduleHosts on one server really do
not share a key.

The server tests are **skipped, not silently passed**, where there is no server
to run them against; `BYTEX_TEST_REDIS` points them at one. A test that
returned early because its subject was absent would report success for having
checked nothing.

Nothing here has been run against a Redis cluster, and a cluster would need the
key layout looked at again — keys of one moduleHost are not in one hash slot.
