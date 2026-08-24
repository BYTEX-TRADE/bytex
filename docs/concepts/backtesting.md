# Backtesting

## Two entry points

- **`BacktestEngine`** — build everything in code: add instruments, venues,
  data, and strategies; call `Run`. Full control, good for notebooks and tests.
- **`BacktestNode`** — run one or more `BacktestRunConfig`s: data comes from
  catalogs, strategies from plugin providers, reports go to a directory. This
  is what `bytex backtest` uses.

## Simulated venue

Each `SimulatedVenueConfig` creates a `SimulatedExchange` with one matching
engine per instrument:

| Setting | Effect |
|---|---|
| `AccountType` | `Cash` (balances move per fill: quote out, base in) or `Margin` (realized P&amp;L settles in the settlement currency; margins reported) |
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
  through them, or on touch with `ProbFillOnLimit`.
- Stops trigger on the bid/ask (or last price for `TriggerType.LastPrice`),
  then behave as market or limit orders. Trailing stops update their trigger
  as the market moves in their favour.
- `Ioc`/`Fok` orders that cannot fill on arrival are cancelled; `Gtd` orders
  expire at their time; `AtTheOpen`/`AtTheClose` fill on the next bar's open or
  close.
- Reduce-only orders that would increase a position and post-only orders that
  would cross are rejected.
- The venue sees each data element before strategies do, so resting orders
  match before the strategy reacts to the same tick.

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
- equity curves per currency;
- tables of orders, fills, positions, and account balances.

`ReportWriter.WriteAll(result, dir)` writes them as CSV plus `summary.txt`.

## Resetting and re-running

`engine.Reset()` clears orders, positions, accounts, and the clock but keeps
venues, instruments, data, and strategies, so parameter sweeps can reuse one
engine. `ClearData()` and `ClearStrategies()` drop those too.
