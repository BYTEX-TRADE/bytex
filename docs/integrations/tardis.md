# Tardis

Package: `Bytex.Adapters.Tardis`. Plugin: `TardisPlugin`. Factory name: `TARDIS`.

Tardis provides historical market data across many venues. The adapter
downloads daily dataset files (`trades`, `quotes`, `book_snapshot_25`,
`incremental_book_L2`) and converts them into engine types. It does not stream
live data.

## A key is optional, because the first day of each month is free

**Tardis serves the first day of every month with no authentication at all**, so
the client is constructed without a key and sends no `Authorization` header when
it has none. A key is used when there is one, and a paid dataset requested
without one fails at the request with the vendor's own answer rather than at
construction with a message about configuration.

That matters for one thing in particular: **it is the only real order-book depth
reachable without a subscription.** The free archives the venues publish
themselves carry top-of-book (Binance's `bookTicker`) or a band metric
(`bookDepth`), so producing levels from either would mean inventing prices.

Measured against `binance-futures / 2024-03-01 / BTCUSDT`:

| Dataset | Gzipped | What it is |
|---|---|---|
| `book_snapshot_25` | ~89 MB | 1,531,104 rows, 104 columns, 25 levels a side, a full day |
| `incremental_book_L2` | ~700 MB | every change, more than a run can hold |
| `trades` | ~38 MB | |

## Order-book depth

`LoadBookSnapshotsAsync` reads `book_snapshot_25` and returns one
`OrderBookDepth` per row, twenty-five levels a side, flagged as a snapshot -
applying one replaces the book rather than merging into it.

**It takes a limit, and the limit is not decoration.** A day is about 1.5 million
rows of fifty levels. `LoadBookDeltasAsync`, beside it, takes none - which is why
the deltas cannot be used for this at any setting, and why snapshots are the
cheapest form that proves what needs proving: that a fill binds to the levels
*below* the best bid and ask rather than only to the size available at the top.

**A level the file left empty ends that side's ladder.** These rows are padded to
the full width, so a book eleven deep carries fourteen empty pairs after it.
Reading those as zeros would put a bid at zero in the ladder, and a fill would
find it.

## What the bytes are is checked, and not by their type

A success status is not a promise about the body. A request this vendor will not
serve - a ranged one among them, and **ranged requests must not be used against
it** - can answer with an HTML error page under HTTP 200. That body reached the
decompressor and died as an `InvalidDataException` naming neither the request nor
the reason.

The client now checks the first two bytes for the gzip marker and says what
arrived instead, with the body in the message, because the body of a wrong body
is usually the explanation.

It is checked that way rather than by the declared content type for a measured
reason: **the vendor serves these files as `text/csv`**, with no content
encoding and a gzip body. A check on the declared type would have refused every
real download.

Downloads go through the client's own rate limiter and retry policy. They used to
build an HTTP client per call, so the vendor's request budget was enforced for
every other request and not for the one fetching ninety-megabyte files.

## Configuration

```json
{ "factory": "TARDIS", "clientId": "TARDIS", "config": {
    "apiKey": null,
    "baseUrl": "https://datasets.tardis.dev/v1",
    "exchangeMap": {
      "BINANCE:Spot": "binance",
      "BINANCE:Swap": "binance-futures",
      "BINANCE:Swap:INVERSE": "binance-delivery",
      "BYBIT:Spot": "bybit-spot",
      "BYBIT:Swap": "bybit",
      "BYBIT:Option": "bybit-options"
    },
    "cacheDirectory": "./tardis-cache"
} }
```

`TARDIS_API_KEY` supplies the key when `apiKey` is null. `cacheDirectory` keeps
downloaded files for reuse.

`exchangeMap` translates an engine market to a Tardis dataset, keyed
**`VENUE:CLASS`** - and `VENUE:CLASS:INVERSE` where a venue publishes its
coin-settled market as a dataset of its own. The class is the instrument's own:
`Spot`, `Swap`, `Future` or `Option`.

The key used to be a venue with an optional `-PERP` suffix, which cannot carry a
product family: a Bybit option is neither spot nor a perpetual, so it fell to the
venue key and was fetched from `bybit-spot`, and a Binance coin-margined contract
took the `-PERP` key and was fetched from the USD-margined dataset. Asking the
wrong dataset is answered with an **empty result**, not an error, so it read as
the vendor holding no data for that period rather than as the wrong question -
the worst shape a data bug can take. A key of the old shape now matches nothing
and the market is refused by name, which is the intended outcome: the default map
covers eight venues, and a market absent from it is one nobody has verified this
vendor's own name for.

## Usage

Instruments must already be in the cache (load them from a venue adapter or
the catalog). Then:

```csharp
// from a strategy or runtimeModule
RequestTradeTicks(marketKey, start, end);   // handled by the TARDIS client when routed to it
```

or directly, to fill a catalog:

```csharp
TardisDataClient client = new(new ClientId("TARDIS"), config, tradingRuntime.Services);
IReadOnlyList<IData> trades = await client.LoadTradesAsync(marketKey, start, end, null, ct);
await catalog.WriteAsync(trades, ct);
```

Because the client has no venue of its own, register it as the market data service's
default client (`MarketDataService.RegisterDefaultClient`) or address it with an
explicit `clientId` in requests.
