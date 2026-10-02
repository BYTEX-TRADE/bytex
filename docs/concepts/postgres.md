# PostgreSQL

A node's state in PostgreSQL instead of Redis (`Bytex.Persistence.Postgres`,
R6.7). Not on by default and not needed to trade: a node without a backing
store keeps its state in memory and loses it when it stops.

```csharp
PostgresCacheDatabase database = new(new PostgresCacheConfig
{
    ConnectionString = "Host=127.0.0.1;Database=bytex;Username=bytex;Password=…",
    ModuleHostId = "TRADER-001",
});

TradingNode node = new(
    config with { TradingRuntime = config.TradingRuntime with { Cache = new CacheConfig { Persist = true } } },
    cacheDatabase: database);
```

## What is stored is not decided here

**The key layout, the indexes and the rebuilding of orders are the same as
Redis's**, down to the last key — one class, compiled into both libraries. So
[the Redis page's "What is stored, and where"](redis.md#what-is-stored-and-where)
is the answer for this store too: the same `orders:<client order id>` list of
events replayed in order, the same index set per kind, the same refusal to hand
back an order list whose orders are not all present, the same rule that a
currency is written before the instrument that needs it.

That is deliberate, and it is the reason a second store was an afternoon rather
than a rewrite. An order-list rebuild that was right in one copy and stale in
the other would be a bracket missing a leg on exactly one of the two stores,
and no test would think to compare them.

Every key begins with `{KeyPrefix}:{ModuleHostId}`, so **two nodes can share one
database** and neither loads nor flushes the other's rows.

## Four tables

What is specific to this store is how those keys are held: four tables, created
on first use, named `{TablePrefix}_kv`, `_set`, `_list` and `_hash`.

| Table | Columns | Holds |
|---|---|---|
| `_kv` | `key` (primary key), `value` | instruments, currencies, order lists, general entries |
| `_set` | `key`, `member` (primary key together) | the index sets that make a reload possible |
| `_list` | `key`, `seq` (`bigserial`), `value` | the event streams of orders, positions and accounts |
| `_hash` | `key`, `field` (primary key together), `value` | runtimeModule and strategy state, field by field |

**Four and not one.** A string, a set, a list and a hash are four shapes, and a
single table of keys and blobs would mean writing the shape into the value and
working it out again on every read — a second serialization format nobody asked
for. A table per shape puts each promise in the schema instead: a set cannot
hold a member twice because its primary key says so.

**The order of a list is the whole point.** An order is rebuilt by replaying
its events in the order they were appended. PostgreSQL promises no ordering
without `ORDER BY`, so a query missing one rebuilds an order with its
acceptance after its fill — an order in the wrong state holding somebody's
money. Every read of a list orders by a sequence that only ever increases.

**A key deleted is a key absent**, of whatever shape it held, so a delete is
four deletes. A row left behind in one table would come back as a
half-present key.

### The schema, and who owns it

`CreateSchema` (default true) issues `CREATE TABLE IF NOT EXISTS` on
construction — it creates what is missing and never drops, because a node
starting against a database holding the state it is about to reload must not
have it taken away. Set it to false where a person or a migration tool owns the
schema and the node's credentials cannot create a table.

`TablePrefix` (default `bytex_state`) separates two *engines* sharing a
database, where `ModuleHostId` separates two *nodes*.

Flushing is the caller's explicit decision and deletes rows, not tables.

## Synchronous, on purpose

`ICacheDatabase` is synchronous, and Npgsql is an ADO.NET provider, so the
blocking API used here is the one it publishes rather than something wrapped
around an asynchronous one. Connections come from a pooled data source built
once, so an operation borrows one instead of opening a socket.

## What this is for, and what it is not

- **It is a recovery mechanism.** How a node that stopped becomes the node that
  starts.
- **It is not an archive of what happened.** The node's journal is that.
- **It is not a cache two nodes share.** Each node keeps its own; pointing two
  at one database makes their state durable, not coherent.

## There is no bus here

`Bytex.Persistence.Redis` carries two things: the cache state and the node's
bus messages on Redis streams. This library carries the first only.

`LISTEN`/`NOTIFY` is not a stream with history and would not behave like the
Redis one, so publishing the bus this way is a separate decision and has not
been made. A deployment that wants no Redis at all does not get bus publishing.

## What has been verified against a server

The same two ways as Redis, for the same reason. Against an in-memory double,
for what this engine decides. Against a real server, for what only a server can
show: that a list comes back in the order it went in, that a set refuses a
second copy of a member, that setting some fields of a hash leaves the rest,
that a delete takes every shape of a key and only that key, and that two
moduleHosts on one database neither read nor flush each other's rows.

`BackingStoreConformanceTests` holds every backing store to the same promises
through `ICacheDatabase`, which is the contract a node depends on.

The server tests are **skipped, not silently passed**, where there is no server
to run them against; `BYTEX_TEST_POSTGRES` points them at one, as a connection
string. A test that returned early because its subject was absent would report
success for having checked nothing.

Nothing here has been run against a replicated or partitioned PostgreSQL.
