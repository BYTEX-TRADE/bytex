# Backtesting

## Two entry points

- **`BacktestEngine`** — build everything in code: add instruments, venues,
  data, and strategies; call `Run`. Full control, good for notebooks and tests.
- **`BacktestNode`** — run one or more `BacktestRunConfig`s, or a
  `BacktestBatchConfig` that varies one of them: data comes from catalogs,
  strategies from plugin providers, reports go to a directory. This is what
  `bytex backtest` uses.

## Many runs from one description

A question about a strategy is rarely about one run: which of a venue's
instruments does it suit, how did it do in each quarter, which parameters were
best. A `BacktestBatchConfig` is one run and what to vary in it, and every
combination is a run of its own.

```csharp
BacktestBatch batch = await node.RunBatchAsync(new BacktestBatchConfig
{
    Run = run,
    Instruments = [btc, eth, sol],
    InstrumentPaths = ["document.instruments.0.instrumentId"],
    Periods = [new BacktestPeriod(q1Start, q1End, "Q1"), new BacktestPeriod(q2Start, q2End, "Q2")],
    Sweeps = [new BacktestSweep { Path = "parameterOverrides.fast", Values = ["8", "12", "20"] }],
    MaxParallel = 4,
});

foreach (BacktestBatchRow row in batch.Best(Currencies.USDT, r => r.ReturnPercent))
{
    Console.WriteLine($"{row.Instrument} {row.Period} {row.Parameters}: {row.ReturnPercent}%");
}
```

- **Instruments** point every data stream of the run at the instrument of the
  run - a bar type keeps its step, its price and its source and changes only
  its instrument - and reach a strategy that carries the instrument itself
  through `InstrumentPaths` and `BarTypePaths`.
- **Periods** are the run's start and end, with a name to show them under.
- **Sweeps** set a value in a strategy's own payload at a dotted path:
  `parameterOverrides.fast`, or `document.instruments.0.instrumentId` where a
  segment that is a number indexes an array. The object a value goes into is
  created if the payload has not got it yet, which is how `parameterOverrides`
  appears on a document that had none; anything missing further up is a typo
  and is refused before a single run is made. A value is written as its text
  reads: a number, `true`/`false`, or a string.
- **ParameterSets** are the other way to say which points to run: each set is
  the value for every path of one point, run as it is, one run each. A sweep
  says "try each of these" and the batch crosses them; a set says "run exactly
  this". That is what a search needs, because a generation is the particular
  points the last one argued for rather than a product of ranges, and what a
  caller needs to re-run a handful of results without the grid around them. A
  batch uses sweeps or sets, not both: giving both is refused rather than
  guessed at. Instruments and periods still multiply over them, so twenty-four
  points over three instruments is seventy-two runs of one batch. A set's paths
  are written in path order, so the same point chosen twice is the same
  payload.
- The runs expand in a settled order - instrument, then period, then the sweeps
  or sets as they were given - each with a run id of its own (`{runId}-000`), so run
  seven of yesterday's report is run seven of today's, and two runs never write
  their reports over each other.
- `MaxParallel` is how many run at once; one by default. The runs are
  independent engines, so what a run produces does not depend on it.
- A run that cannot be made or throws is a line in the table with its reason,
  not a hole in it: a scan over three hundred instruments should not be lost
  because one of them has no data, and a scan that quietly dropped what it
  could not run would report the best of an unknown number. `StopOnFailure`
  says otherwise.

`batch.Rows(currency)` is the comparable table - what was varied, trades,
profit, return, drawdown, Sharpe, profit factor, and the reason it failed if it
did - `batch.Summary(currency)` is that table as text, and `batch.Best(...)`
orders it by whatever is being looked for. With an output directory,
`batch_{CURRENCY}.csv` is written beside each run's own report.

`bytex backtest --config` reads it: a file whose object names a `run` is a
batch, anything else is the run, or array of runs, it has always been.

A walk-forward check is two batches: sweep the grid over the in-sample period,
take the parameters that won, and run those over the out-of-sample period that
follows it. The engine does not choose for you - which figure makes one run
better than another is not the engine's judgement to make - but the table each
batch produces is what the choice is made from.

## Guided search

A space worth searching is a space nobody can cross: six parameters at ten
values each is a million runs. `BacktestSearchConfig` describes one instead -
the run to vary, and a `Space` of parameters, each either a list of `Choices`
or a numeric range walked in `Step`s - and `node.RunSearchAsync(search,
currency, row => row.ReturnPercent)` breeds generations of candidates until it
runs out of generations or stops improving.

```csharp
BacktestSearch searched = await node.RunSearchAsync(
    new BacktestSearchConfig
    {
        Run = run,
        Space =
        [
            new BacktestSearchParameter { Path = "fastPeriod", Min = 4, Max = 30, Step = 2 },
            new BacktestSearchParameter { Path = "slowPeriod", Min = 20, Max = 120, Step = 5 },
        ],
        Population = 16,
        Generations = 8,
        StopAfterGenerationsWithoutImprovement = 3,
    },
    Currencies.USDT,
    row => row.ReturnPercent);

Console.WriteLine(searched.Summary());
Console.WriteLine(searched.Best?.PointText);
```

- A generation is a batch of chosen points, so every candidate is its own
  engine and what a candidate scores does not depend on what else was in its
  generation.
- **What is drawn** is only ever a value the space allows, so anything the
  search reports can be re-run on its own and be the same run.
- **Which figure makes one candidate better than another is the caller's** -
  the function is yours to pass. A caller that cannot pass one (a
  configuration file) names an `Objective` instead: the figure, the currency
  to read it in, and whether less of it is better.
- **The elites** go into the next generation unchanged, so a search cannot go
  backwards, and `Best` is the best it ever saw rather than the best of its
  last generation.
- **A candidate whose run produced no figure is never bred from.** A run that
  failed is not evidence that its parameters were bad.
- **The same seed proposes the same candidates**, so a search is as repeatable
  as the runs it is made of - and a point already run earlier in the search is
  not run again, because a run is deterministic and the answer would be the
  same.
- A search varies parameters and nothing else. Instruments and periods are
  whatever the run says: candidates judged on different instruments are not
  comparable, and how to weigh them together is not the engine's judgement to
  make. A search per instrument is a search per instrument.

`bytex backtest --config` reads one: a file whose object names both a `run` and
a `space` is a search. `examples/configs/search-ema-cross.json` is one, and it
prints a line a generation, then the best point it found and the run it was.

## Simulated venue

Each `SimulatedVenueConfig` creates a `SimulatedExchange` with one matching
engine per instrument:

| Setting | Effect |
|---|---|
| `AccountType` | `Cash` (balances move per fill: quote out, base in; spot instruments only) or `Margin` (realized P&amp;L settles in the settlement currency; margins reported) |
| `OmsType` | `Netting` or `Hedging` for position handling |
| `StartingBalances` | initial balances by currency |
| `FillModel` | probability a resting limit fills on touch, probability a stop fills at its trigger, probability of one-tick slippage on market orders; seeded for reproducibility |
| `FeeModel` | `MakerTakerFeeModel` (instrument rates, default), `FixedFeeModel`, `PercentFeeModel` |
| `LatencyModel` | base/insert/update/cancel delays before the venue acts on a command |
| `BarExecution` | `OhlcPath` replays open → nearer extreme → farther extreme → close; `CloseOnly` uses closes |
| `RejectStopOrdersAtMarket` | reject stops whose trigger is already through the market |
| `SupportContingentOrders` | OCO/OTO/OUO handled natively by the venue |

## Matching rules

- Market orders fill at the opposite best price (plus one tick when slipped).
- Limit orders that cross on arrival fill as taker at the opposite price;
  resting limits fill as maker at their own price when the market trades
  through them, or on touch with `ProbFillOnLimit`. A trade tick counts: a
  print through a resting order fills it, because that trade would have taken
  it, and a print exactly at its price is a touch.
- With trade ticks and no quotes, both sides of the book follow the last
  print. Beside real quotes, a print outside the spread drags the side it went
  through until the venue quotes again.
- Stops trigger on the bid/ask (or last price for `TriggerType.LastPrice`),
  then behave as market or limit orders. Trailing stops update their trigger
  as the market moves in their favour.
- `Ioc`/`Fok` orders that cannot fill on arrival are cancelled; `Gtd` orders
  expire at their time; `Day` orders expire when the UTC date rolls over;
  `AtTheOpen`/`AtTheClose` fill on the next bar's open or close.
- A trailing stop with an activation price is dormant until the market reaches
  that price: it neither trails nor triggers before then, and starts trailing
  from the price that activated it.
- An order is in the venue's book before its owner is told it was accepted, so
  a command sent on that news reaches a venue that already holds the order.
- Reduce-only orders that would increase a position and post-only orders that
  would cross are rejected. Under `OmsType.Hedging` the venue does not judge a
  reduce-only order against its own netted book - a long leg and a short leg
  net to nothing - and leaves the leg to the execution engine, which applies
  the fill to the position the order names.
- The venue sees each data element before strategies do, so resting orders
  match before the strategy reacts to the same tick.
- The venue holds what an order will need while it rests, and publishes the
  account so `Locked` and `Free` say what is still available. On a cash account
  that is the quote currency a buy will spend plus its commission, or the base
  currency a sell will deliver, and a second order the free balance cannot cover
  is rejected with `insufficient balance`. On a margin account it is the initial
  margin the order would have to post, and one it cannot carry is rejected with
  `insufficient margin`, saying what it needed, at what leverage, against what
  was free. What the venue calls free is free of both those holds and the margin
  its open positions are already posting, so an account cannot back several
  orders with the same money - nor open position after position by sending each
  order after the last one filled.
  A hold is given back when the order fills, is amended, cancels, expires or is
  rejected. Reduce-only exits hold nothing, so a stop and a target over one
  position cost one position's funds; on a netting venue any order that closes
  holds nothing, because closing gives margin back rather than asking for more,
  while on a hedging venue the same order opens a position of its own and pays
  for it like any other.

## Determinism

Data is sorted by `TsInit` with insertion order as tie-break; timers fire
before data at the same timestamp; the fill model is seeded. Running the same
inputs twice produces identical events and results.

## Results

`engine.GetResult()` returns a `BacktestResult`:

- per-currency statistics: starting/ending balance, realized and unrealized
  P&amp;L, commissions, return, max drawdown, Sharpe, Sortino, profit factor,
  expectancy;
- trade statistics: counts, win rate, average winner/loser, largest
  winner/loser, average duration, long/short split;
- equity curves per currency: one point per timestamp of data, each point the
  starting balance plus realized profit plus what the open positions were worth
  at that moment;
- tables of orders, fills, positions, and account balances.

How the ratios and the drawdown are computed:

- daily returns come from the equity curve: the last equity of each day of
  data against the day before it, the first against the starting balance. A day
  the run held a position through has a return whether or not it closed a
  trade; a day with no data has no point and no return, and both ratios are
  annualised with `sqrt(252)`;
- Sharpe divides the mean by the sample deviation, Sortino by the downside
  deviation of the same series;
- the max drawdown is peak to trough of the equity curve with the starting
  balance as the first peak, so what the first fill cost counts - and, because
  the curve marks open positions, so does a fall a position went through
  without being closed;
- a commission is reported under the currency it was paid in, whether or not a
  position settles in that currency;
- the profit factor is gross profit over gross loss, and is `null` when there
  was no losing trade at all: the ratio has no value then, and a sentinel read
  as a number is worse than nothing.

`ReportWriter.WriteAll(result, dir)` writes them as CSV plus `summary.txt`.

## Resetting and re-running

`engine.Reset()` clears orders, positions, accounts, and the clock but keeps
venues, instruments, data, and strategies, so parameter sweeps can reuse one
engine. `ClearData()` and `ClearStrategies()` drop those too.

## Known limitations

The simulator is a model, and these are the places where it is simpler than a
venue. Read results with them in mind.

- **With a book, a taker pays for the depth it eats; without one, only the
  touch is modelled.** Given depth, an order that takes walks the levels it can
  reach, best price first, paying what each one costs and stopping at its own
  limit - so a size larger than the touch is filled now, at a worse average,
  which is what a venue does. Given an L1 feed instead, there is no book to
  walk: the fill is bounded by the size at the best price - the other side of
  the quote, the print that reached a resting order - and what is left waits
  for the next one, so a large order is filled over time rather than at one
  worse price. `BacktestResult.Applied` contains `bookDepth` when a run
  actually walked a book, `Simulation` only that the venue could have.
  What is still a model: the book is the one the data describes and nothing an
  order does changes it, and a level is eaten once per state of the book rather
  than refilled by whoever else is quoting.
- **On bars, a share of the volume - and never the book.** A bar covers a
  length of time and a book is one moment, so a run holding both does not pay a
  minute's worth of size out of a book belonging to an instant of it: while the
  latest thing the venue heard is a bar, the bar's share bounds the fill and no
  book is walked. The next book, quote or print puts depth back on. A bar says
  what traded across its whole length rather than what was on offer at a price,
  so what it offers is
  `BarVolumeShare` of that: what one participant could plausibly have been. A
  tenth by default. One bar is one budget however many prices its path walks,
  and what an order cannot take goes on working and takes its share of the next
  bar - the bound is a rate over time, not a book that ran out. It is a model
  and it is stated: `BacktestResult.Participation` says the share a run
  assumed, how many fills it bounded and how much went in under a bound. Set
  the share to null to fill any size, which is what the simulator did before
  0.5.
- **A resting order waits its turn, as far as the data shows.** With a book,
  an order that comes to rest at a price stands behind whatever is already
  quoted there, and a print at that price serves that queue before it reaches
  the order - so a limit order at the back of the book fills later than the
  same order at the front, which a fill on first touch could not tell apart. A
  trade past the price clears the queue, because the market could not have
  traded there otherwise; a level that has grown smaller is size that has gone,
  traded or cancelled, and moves the order forwards; a level that has grown
  leaves it where it was, because size arriving at a price arrives behind what
  is resting. What this is not is a venue's queue: a venue matches on its own
  sequence numbers and does not publish them, so this is the most that can be
  said from a book and a print feed, and it says it the pessimistic way - an
  order is never further forward than the data proves. With no book data every
  resting order is at the front of its queue, which is how the simulator
  behaved before 0.6.
- **A part-filled order stops holding for its remainder.** A fill releases the
  whole hold the order was carrying and nothing re-reserves what is left, so the
  unfilled part of a part-filled order rests unheld until it fills. The account
  cannot be over-committed by it - the position that opened holds margin for
  what filled - but a second order judged in that window is judged against a
  balance the first order still has a claim on. Cash and margin accounts behave
  the same way here.
- **Fees are a model, not a venue's schedule.** `MakerTakerFeeModel` uses the
  instrument's own rates, `PercentFeeModel` a flat share of notional,
  `FixedFeeModel` the same amount per fill, and `PerContractFeeModel` a fee for
  every contract traded, which is how futures and options venues bill. None of
  them knows about volume tiers, rebates or a venue's promotions.
- **Liquidation is modelled at the touch.** A margin account is checked against
  its maintenance margin on every price the venue sees, at that price rather
  than at a mark price of the venue's own, and a position it can no longer carry
  is closed with a market order tagged `LIQUIDATION` after its working orders
  are cancelled. A real venue has an insurance fund, a liquidation fee, partial
  liquidations and a mark price that lags the last trade; this has none of
  those, so treat a liquidation here as "the account would have been closed out
  about here" rather than as the price it would have got.
- **Funding needs funding data.** A perpetual position is charged only for the
  rates a run was given. With no funding data in the catalog the position is
  held for nothing, which flatters anything that holds one for long - so read a
  perpetual backtest with `funding.csv` beside it, and an empty one as a run
  that was never charged rather than a strategy that never paid.
- **No market impact.** A fill consumes the size it took, and nothing else: the
  price does not move against the order that took it, and the next quote
  arrives as the data recorded it rather than as the market would have
  answered. Results for sizes that are large against the instrument's real
  depth are still optimistic.
- **Queue position is not modelled.** Whether a resting limit fills when the
  market only touches its price is decided by `ProbFillOnLimit`, not by the
  order's place in the queue.
- **The path inside a bar is an assumption.** With bar data the venue sees
  four prices per bar in the order given by `BarExecution`. When a stop and a
  target both sit inside one bar, which of them fills first follows that
  assumed order, not what the market did. Use quote or trade ticks where the
  answer matters.
- **Two bar execution modes.** `OhlcPath` and `CloseOnly` are the only ones.
- **Day orders keep to UTC.** `TimeInForce.Day` expires when the UTC date
  rolls over, whatever session calendar the venue itself keeps.
