# Data

## Types

All data implements `IData` with an optional `InstrumentId`, `TsEvent` (when it
happened at the source), and `TsInit` (when this process created it). The
engine orders data by `TsInit`.

| Type | Fields |
|---|---|
| `QuoteTick` | bid, ask, bid size, ask size |
| `TradeTick` | price, size, aggressor side, trade id |
| `Bar` | bar type, OHLC, volume, `IsRevision` for updates to an open bar |
| `OrderBookDelta` / `OrderBookDeltas` | add/update/delete/clear with side, price, size, order id, sequence, flags |
| `OrderBookDepth` | bid and ask levels |
| `OrderBook` | mutable L1/L2/L3 book maintained by the cache |
| `InstrumentStatus`, `MarkPriceUpdate`, `IndexPriceUpdate`, `FundingRateUpdate` | venue state |
| `Signal` | named decimal published by an actor |
| `CustomData` | base for user-defined records |

Prices and quantities are `decimal` value types with explicit precision;
`Instrument.MakePrice` and `MakeQuantity` round to the venue's increments.

## Bar types

`BTCUSDT.BINANCE-1-MINUTE-LAST-EXTERNAL` reads as instrument, step,
aggregation, price type, source.

- Aggregations: `TICK`, `VOLUME`, `VALUE`, `MILLISECOND`, `SECOND`, `MINUTE`, `HOUR`, `DAY`, `WEEK`, `MONTH`.
- Price type: `BID`, `ASK`, `MID`, `LAST`.
- Source: `EXTERNAL` (venue-built) or `INTERNAL` (built by the data engine from ticks, or from finer external bars of the same price type).

Internal time bars close on the clock's timer; set
`DataEngineConfig.BuildBarsWithNoUpdates` to emit bars for empty intervals and
`TimeBarsTimestampOnClose` to choose whether a bar is stamped with its close
(default) or open time.

## Subscriptions and routing

Actors subscribe to message-bus topics and send subscribe commands to the
data engine, which forwards the first subscription for a stream to the
responsible client and the last unsubscription back. Routing: explicit
`ClientId` → client registered for the instrument's venue → default client.

Topics follow `data.{kind}.{venue}.{symbol}`; order-book snapshots are produced
locally on a timer from the maintained book.

## The catalog

`ParquetDataCatalog` stores instruments (JSON) and quotes, trades, bars, and
book deltas (Parquet) under
`{root}/{kind}/{instrument or bar type}/{start}-{end}.parquet`:

```csharp
ParquetDataCatalog catalog = new("./catalog");
await catalog.WriteInstrumentsAsync([instrument]);
await catalog.WriteBarsAsync(bars);
IReadOnlyList<Bar> loaded = await catalog.BarsAsync(barType, start, end);
```

`CsvLoader` reads bars, quotes, and trades from CSV with a configurable column
mapping and timestamp format. The CLI wraps both (`bytex catalog ...`).

## Live data from adapters

Binance and Bybit stream quotes (top of book), trades, bars, book deltas, and
(for derivatives) mark/index prices and funding rates, and answer historical
bar and trade requests. Tardis serves historical trades, quotes, and book
deltas for any venue it covers; instruments must be in the cache first.
