# Benchmarks

What the engine costs per event, measured rather than asserted.

## What is measured, and why not a whole backtest

These are the small things the engine does millions of times: a message on the
bus, a tick into a bar, a delta into a book, a bar into an indicator, a price
rounded to a venue's increment. A number for a whole backtest would say more about
the data it was given than about the engine - double the ticks and it doubles -
so the published numbers are per event, and a run's cost is its events times
these.

Every benchmark is in [`bench/Bytex.Benchmarks`](../bench/Bytex.Benchmarks) and is
run with BenchmarkDotNet, which discards the warm-up, runs until the measurement
is stable, and reports the memory allocated per operation as well as the time.

## Reproducing them

```bash
dotnet run -c Release --project bench/Bytex.Benchmarks -- --filter "*"
```

One class at a time, which is what a change to that part of the engine wants:

```bash
dotnet run -c Release --project bench/Bytex.Benchmarks -- --filter "*OrderBookBenchmarks*"
```

Nothing else should be running on the machine, including a build: BenchmarkDotNet
will report a high error margin when something else is competing for the CPU, and
a number measured against a busy machine is worth nothing.

## The numbers

Measured on Windows 11 x64, .NET 10.0.12, BenchmarkDotNet 0.15.2, default job (the
error column is half of a 99.9% confidence interval). Ten thousand events means the
whole loop is timed and the per-event cost is the mean divided by ten thousand.

### Per event

| What | Mean | Per event | Allocated |
|---|---|---|---|
| Publish one quote to an exact topic, one subscriber | 30.7 ns | 30.7 ns | 152 B |
| Publish one quote to an exact topic, four subscribers | 33.9 ns | 33.9 ns | 152 B |
| Publish one quote to a wildcard subscriber | 34.9 ns | 34.9 ns | 152 B |
| Round a price to the instrument's increment | 31.9 ns | 31.9 ns | none |
| Notional value of a position | 14.7 ns | 14.7 ns | none |

A publish costs about the same whether the topic is matched exactly or by a
pattern, and about the same for one subscriber as for four: what is being paid for
is the dispatch, not the match. The 152 bytes are the arguments boxed on the way
through the bus, which is one small allocation per message.

### Per ten thousand events

| What | Mean | Per event | Allocated |
|---|---|---|---|
| Trades into tick bars of a hundred | 101.7 μs | 10.2 ns | 400 B |
| Trades into volume-imbalance bars | 288.1 μs | 28.8 ns | 544 B |
| Deltas applied to an order book | 364.5 μs | 36.5 ns | 4.92 KB |
| Bars through a 20-period SMA | 391.0 μs | 39.1 ns | 432 B |
| Bars through Ichimoku | 6.70 ms | 670 ns | 6.22 KB |
| Bars through a 20-period linear regression | 10.93 ms | 1.09 μs | 544 B |

What these say about the engine:

- **Aggregating a bar from a tick costs about as much as publishing it.** A tick
  bar is ten nanoseconds of work per trade; an imbalance bar is three times that,
  which is the signing and the running total.
- **A book is the most expensive ordinary data.** Thirty-six nanoseconds a delta,
  and the only path here that allocates per event in any quantity - a sorted book
  keeps nodes.
- **Indicators are where a strategy spends its money.** A moving average is
  nothing, forty nanoseconds a bar. Ichimoku is seventeen times that, because it
  scans three windows for extremes on every bar, and a linear regression is
  twenty-eight times, because it sums over its whole window. A strategy with
  twenty indicators on one candle series is paying for all twenty on every bar, and
  which ones matter is now a number rather than a guess.
- **The money arithmetic is free by comparison**, and allocates nothing: a price
  and a quantity are value types with explicit precision, and rounding one to a
  venue's increment costs the same as a bus publish.


## Reading them

**Allocation matters as much as time.** A managed allocation per event is a
garbage collection per so many events, and a collection is a pause that lands on
whatever the node was doing. Where a row says nothing was allocated, that path
does not add to collection pressure however many events go through it.

**These are one machine's numbers.** They are published so that a change can be
compared against them on the same machine, and so that somebody sizing a run has
an order of magnitude. A different processor will produce different numbers; the
ratios between rows are what travel.

**What is not here.** The cost of a venue's network, of reading Parquet from a
disk or from object storage, and of whatever a strategy itself does. Those are the
three things that dominate a real run, and none of them are the engine's to
measure.
