# Data

## Types

All data implements `IData` with an optional `MarketKey`, `EventTime` (when it
happened at the source), and `CreatedTime` (when this process created it). The
engine orders data by `CreatedTime`.

| Type | Fields |
|---|---|
| `QuoteTick` | bid, ask, bid size, ask size |
| `TradeTick` | price, size, aggressor side, trade id |
| `Bar` | candle series, OHLC, volume, `IsRevision` for updates to an open bar |
| `OrderBookDelta` / `OrderBookDeltas` | add/update/delete/clear with side, price, size, order id, sequence, flags |
| `OrderBookDepth` | bid and ask levels |
| `OrderBook` | mutable L1/L2/L3 book maintained by the cache; an L1 book keeps the best level of each side |
| `InstrumentStatus`, `MarkPriceUpdate`, `IndexPriceUpdate`, `FundingRateUpdate` | venue state |
| `Signal` | named decimal published by a runtime module |
| `CustomData` | base for user-defined records |

Prices and quantities are `decimal` value types with explicit precision;
`Instrument.MakePrice` and `MakeQuantity` round to the venue's increments.

## Candle series

`bx-candle:v2/BINANCE/BTCUSDT/minute/1/last/provider` reads as venue, escaped
symbol, sampling method, step, price basis, origin.

- Aggregations by amount: `TICK`, `VOLUME`, `VALUE`.
- Aggregations by time: `MILLISECOND`, `SECOND`, `MINUTE`, `HOUR`, `DAY`, `WEEK`, `MONTH`.
- Aggregations by one-sidedness (R2.12): `TICKIMBALANCE`, `VOLUMEIMBALANCE`,
  `VALUEIMBALANCE`, `TICKRUNS`, `VOLUMERUNS`, `VALUERUNS`. An underscore between
  the words is accepted when reading one (`TICK_IMBALANCE`); the canonical form
  written back is without.
- Price type: `BID`, `ASK`, `MID`, `LAST`.
- Origin: `provider` (venue-built) or `computed` (built by MarketDataService from ticks or finer provider candles of the same price basis).

Computed time candles close on the clock's timer; set
`MarketDataServiceConfig.BuildBarsWithNoUpdates` to emit bars for empty intervals and
`TimeBarsTimestampOnClose` to choose whether a bar is stamped with its close
(default) or open time.

### Bars that close on one-sidedness (R2.12)

A time bar closes on the clock, a volume bar on how much traded. These close on
how **one-sided** the trading was, which is why they are called
information-driven: a quiet market and a busy two-way market both produce few of
them, and a market where one side is pushing produces many.

| Kind | Closes when |
|---|---|
| `TICKIMBALANCE` | buys minus sells, counted in trades, reaches the step |
| `VOLUMEIMBALANCE` | the same, counted in size |
| `VALUEIMBALANCE` | the same, counted in size × price |
| `TICKRUNS` | one side alone, counted in trades, reaches the step |
| `VOLUMERUNS` | one side alone, counted in size |
| `VALUERUNS` | one side alone, counted in value |

The difference between the two families is what cancels. An imbalance is signed,
so a market worked evenly in both directions never closes a bar however much of
it there is. A run counts each side on its own, so the same market closes bars
steadily. Both are in the engine because they answer different questions.

**How an update is signed.** By the venue's own word where there is one - a trade
carries the side that took the liquidity, and that is a fact rather than an
inference. Where there is none, a quote or a trade from a source that does not
say, the tick rule is used: the sign of the change from the previous price,
carried forward while the price does not move. The first update of a series has
no previous price and no aggressor, so it counts as a buy by convention; it
shifts the first bar and nothing after it.

**An update is never split.** A volume bar can take half of a trade, because half
of a size is a size. Half of a signed trade is not one - the sign belongs to the
whole of it - so these bars close on the update that carries them over the
threshold, and that whole update is in the bar that closed.

**Where this departs from the published form.** In *Advances in Financial Machine
Learning* these bars close when the imbalance exceeds an **expectation** of it,
estimated from an exponentially weighted average of past bars, so the threshold
moves with the market. This engine closes at the threshold **the candle series
states**. The reason is reproducibility: an estimated threshold makes a bar depend
on the estimator's parameters and on everything it has seen, so the same data can
produce different bars in two runs, and nothing in a candle series could carry those
parameters. A run of a backtest here is repeatable, and a candle series means the same
thing everywhere it is written. An adaptive threshold is a strategy's to compute,
from bars it can already see.

## Subscriptions and routing

Runtime modules subscribe to message-bus topics and send subscribe commands to the
market data service, which forwards the first subscription for a stream to the
responsible client and the last unsubscription back. Routing: explicit
`ClientId` → client registered for the instrument's venue → default client.

Topics follow `data.{kind}.{venue}.{symbol}`; order-book snapshots are produced
locally on a timer from the maintained book.

## The catalog

`MarketArchive` stores instruments (JSON) and quotes, trades, bars, book
deltas, book depth and funding rates (Parquet) under
`{root}/{kind}/{encoded-key}/segments/{opaque-id}.parquet`. Immutable manifests
under the same stream's `commits/{opaque-id}.json` carry ranges, counts, hashes,
and order. See [Archive v2](archive-v2.md) for publication and recovery rules:

```csharp
MarketArchive catalog = new("./catalog");
await catalog.WriteInstrumentsAsync([instrument]);
await catalog.WriteBarsAsync(bars);
IReadOnlyList<Bar> loaded = await catalog.BarsAsync(candleSeries, start, end);
```

Depth is kept one row per snapshot, each row carrying its whole ladder, and the
two sides are list columns - so the number of levels belongs to whatever
published the book and not to the schema. **A snapshot written at N levels reads
back at N levels.** That matters because a fixed-width layout would have to put
a value in every column a short ladder does not fill, and a zero there is a
price a fill can reach: a book eleven deep must not come back as eleven levels
followed by fourteen at zero. Normalising instead to a row per level would turn
a 25-level instrument-day from about 1.5 million rows into 76 million.

`WriteAsync` takes a mixed `IData` batch and sorts it into these kinds. What the
catalog has no home for - instrument status, mark and index prices, signals,
custom data - is **refused by name and count**, after the rest of the batch has
been written, rather than dropped.

A write fills a file to `MarketArchive.MaxRowsPerFile` records and starts
another, each with an opaque unique name and manifest range. **A file is the unit a
read holds in memory:** `StreamAsync` opens one file at a time, but every row in
that file is materialised before the first is yielded, so an unbounded file
would mean an unbounded read however lazily the caller consumes it. The bound is
sized from the heaviest kind - 25-level depth at about 3,011 bytes a snapshot -
so a full file holds roughly 300 MB of records, and a full instrument-day of
depth becomes sixteen files rather than one 4.3 GiB file nothing could open.

It bounds a **file**, not a write: handing the catalog a whole day in one call
still builds that day in memory first, so what a caller passes in is the
caller's to limit.

`bytex catalog consolidate` rewrites a data set as the fewest ordered files the
bound allows - not as one file, which would undo the bound - dropping records
held twice. It still reads every file of a key at once to do so, so
consolidating a very large key costs what that key costs.

`CsvLoader` reads bars, quotes, and trades from CSV with a configurable column
mapping and timestamp format. The CLI wraps both (`bytex catalog ...`).

A quoted field is honoured, so a value containing the separator does not shift
every column after it. A file whose first line is already data - which is most
of what an exchange hands out of its own archive - is read by position:

```bash
# an archive with no header: each field is given its column, counting from 0
bytex catalog import-csv -p ./catalog -f trades.csv -k trades -i bx-market:v2/BINANCE/BTCUSDT \
  --no-header --columns timestamp=0,price=1,size=2,side=3 --timestamp-format unix_ms

# a file with headers of its own, and semicolons
bytex catalog import-csv -p ./catalog -f bars.csv -k bars -i bx-market:v2/BINANCE/BTCUSDT \
  --candle-series bx-candle:v2/BINANCE/BTCUSDT/minute/1/last/provider \
  --separator ";" --columns timestamp=time,open=o,high=h,low=l,close=last,volume=vol
```

`--columns` takes `field=source` pairs, where a source is a header name or a
0-based position; the fields are `timestamp`, `open`, `high`, `low`, `close`,
`volume`, `bid`, `ask`, `bidSize`, `askSize`, `price`, `size`, `side` and
`tradeId`. `--no-header` requires `--columns`: a file with no header has no
names to match, and guessing would only move the error somewhere less visible.
`--separator` takes one character or `tab`. An import that read no rows says so
rather than reporting a cheerful "Imported 0".

The catalog reads and writes through a store, so the same catalog also sits on
S3-compatible object storage - `new MarketArchive("s3://bucket/prefix")`,
and the same string anywhere a catalog is named. See
[object storage](object-storage.md) for how a location is read, what a remote
catalog costs, and what has been verified against a real service.

## Live data from adapters

Binance and Bybit stream quotes (top of book), trades, bars, book deltas, and
(for derivatives) mark/index prices and funding rates, and answer historical
bar and trade requests. Tardis serves historical trades, quotes, and book
deltas for any venue it covers; instruments must be in the cache first.
