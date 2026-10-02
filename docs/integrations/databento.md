# Databento

Historical market data from [Databento](https://databento.com), as trades, top-of-book quotes
and bars. Data only: this is a vendor, not a venue, so there is nothing to trade on it and no
execution client to trade with.

| | |
|---|---|
| Factory name | `DATABENTO` |
| Data | yes — trades, quotes (`mbp-1`), bars |
| Execution | no |
| Key | `DATABENTO_API_KEY` |
| Instruments | named by the caller; this adapter lists none |

## The key

Read from `DATABENTO_API_KEY` and sent as the user of HTTP basic authentication with an empty
password, which is how this vendor's own client authenticates. A host never has to hold it: the
history helper reads the environment itself.

## Datasets

This vendor serves many datasets — `GLBX.MDP3`, `XNAS.ITCH` and others — and this adapter assumes
none. One has to be named, because asking the wrong one is answered with an error about
entitlements rather than about the mistake.

```csharp
new DatabentoDataClientConfig { Dataset = "GLBX.MDP3" }
```

## Prices arrive as integers

This vendor's prices are fixed-point with **every unit a billionth**: `4750250000000` is
`4750.25`. Read as a decimal without the scale, a price is wrong by nine orders of magnitude — in
the direction that looks like a very cheap instrument rather than like an error. The adapter
applies the scale; anything reading this vendor's raw output has to.

## Bars

The aggregations this vendor publishes, and no others:

| Candle series | Schema |
|---|---|
| 1 second | `ohlcv-1s` |
| 1 minute | `ohlcv-1m` |
| 1 hour | `ohlcv-1h` |
| 1 day | `ohlcv-1d` |

A five-minute bar is refused by name. Aggregating one here would hand back a bar this adapter
made, under the vendor's name, and nothing downstream could tell the difference.

```csharp
IReadOnlyList<Bar> bars = await DatabentoHistory.FetchBarsAsync(
    instrument,
    CandleSeries.Parse("bx-candle:v2/DATABENTO/ESZ4/minute/1/last/provider"),
    symbol: "ESZ4",
    dataset: "GLBX.MDP3",
    start: from,
    end: to);
```

Bars are bounded to those that have **closed**, like every other history helper here: a candle
still forming is the one thing a stored bar must never be.

## CSV, not DBN

This vendor's own client always asks for DBN, its binary format, compressed with zstd. This
adapter asks for CSV.

That is a decision rather than an omission. A binary layout would have to be inferred field by
field here, and a decoder that is subtly wrong reads a plausible price out of the wrong bytes
rather than failing — which is the failure this repository spends most of its guards preventing.
Every CSV column this reads is a name the vendor publishes, including the two-digit level suffix
on `bid_px_00` and `ask_px_00`, and a column that is missing is refused rather than defaulted,
because a zero price reads as data.

**What that costs:** CSV is larger on the wire than DBN and slower to parse. For a period large
enough that this matters, fetch once into the catalog and read from there.

## What this adapter does not do

- **No live data.** This vendor has a live service; this adapter uses only the historical one, so
  there is no subscription of any kind rather than one that refuses.
- **No instrument list.** Symbols are named by the caller from the vendor's own symbology, so a
  Databento "venue" cannot be offered in an instrument picker.
- **No order book beyond the top.** `mbp-1` is one level; deeper books are a different schema and
  are not read here.
