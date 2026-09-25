# Risk

Every trading command passes through the `RiskEngine` before the execution
engine. Denied orders receive an `OrderDenied` event with the reason; denied
modifications receive `OrderModifyRejected`.

## Checks on submission

| Check | Reason code |
|---|---|
| Trading state is `Halted` | `TRADING_HALTED` |
| Trading state is `Reducing` and the order would not reduce a position | `TRADING_REDUCING` |
| Instrument not in the cache | instrument not found |
| Quantity precision above the instrument's, zero, above max, below min | `QUANTITY_*` |
| Price or trigger precision above the instrument's, non-positive, outside min/max | `PRICE_*` |
| Notional above the instrument's max / below min / above `MaxNotionalPerOrder` | `NOTIONAL_*` |
| Cash account free balance cannot cover a non-reduce-only order | `INSUFFICIENT_BALANCE` |
| Margin account free balance cannot post the initial margin an order needs | `INSUFFICIENT_MARGIN` |
| The period has lost `MaxLossPerPeriod`, realised and open together, and the order would add | `LOSS_LIMIT` |
| The order would carry the account past `MaxExposure` | `EXPOSURE_LIMIT` |
| The instrument or the account is at its cap of open positions | `POSITION_CAP` |
| The instrument or the account is at its cap of working orders | `ORDER_CAP` |
| More than `MaxOrderSubmitRate` submissions per `OrderRateInterval` | rate exceeded |

Modifications are checked for precision and limits on the new values and for
`MaxOrderModifyRate`.

## Configuration

```csharp
new RiskEngineConfig
{
    Bypass = false,
    MaxOrderSubmitRate = 100,
    MaxOrderModifyRate = 100,
    OrderRateInterval = TimeSpan.FromSeconds(1),
    MaxNotionalPerOrder = new Dictionary<InstrumentId, decimal> { [id] = 50_000m },
    CheckCashBalance = true,
    CheckMargin = true,
    Limits = new RiskLimits
    {
        MaxLossPerPeriod = RiskLimit.Of(new Money(1_000m, Currencies.USDT)),
        LossPeriod = TimeSpan.FromDays(1),
        OnLossLimit = LossLimitBreach.StopTrading,
        MaxExposure = RiskLimit.PercentOfEquity(50m),
        MaxOpenPositionsPerInstrument = 1,
        MaxOpenPositions = 5,
        MaxWorkingOrdersPerInstrument = 10,
        MaxWorkingOrders = 50,
    },
}
```

The same limits in a node's JSON, where a limit is the text a person would
type - an amount with its currency, or a share of the account's equity:

```json
{ "kernel": { "riskEngine": { "limits": {
  "maxLossPerPeriod": "1000 USDT",
  "lossPeriod": "1.00:00:00",
  "onLossLimit": "stopTrading",
  "maxExposure": "50%",
  "maxOpenPositionsPerInstrument": 1,
  "maxOpenPositions": 5,
  "maxWorkingOrdersPerInstrument": 10,
  "maxWorkingOrders": 50
} } } }
```

or on the command line, which overrides the file one value at a time:

```
bytex run --config node.json --max-loss "1000 USDT" --loss-period 1.00:00:00   --on-loss-limit stop-trading --max-exposure 50% --max-open-positions 5   --max-working-orders 50
```

`RiskEngine.SetLimits(limits)` replaces them while the engine runs, and
`TradingNode.SetLimits(limits)` does it on the node's own thread - which is
what the control channel's `limits` command uses. What a period has already
lost is not forgotten when a limit is set: a limit set at noon is measured
against the same day.

## Account limits

## Margin

On a margin account the question is not what an order costs but what has to be
posted to hold it. `CheckMargin` (on by default, like the cash check) computes
the initial margin from `MarginAccount.CalculateInitialMargin` - the notional
at the account's leverage for that instrument, times the instrument's
`MarginInit` - and compares it with the balance that is **free**: a venue
reports the margin already committed as the locked part of the balance, so what
is free is free of it. An order needing more is denied with
`INSUFFICIENT_MARGIN` naming the margin, the leverage and what was free, and
`RiskEngine.MarginDeniedCount` counts them.

Anything that reduces is let through, because closing a position gives margin
back rather than asking for more. A quote-quantity order is judged on the size
it converts to at the reference price. Turn the check off for a venue that
judges margin itself and is the only one that knows its own rules.

A leverage below 1 is a margin requirement larger than the notional, and zero
divides the arithmetic by nothing: `MarginAccount.SetLeverage`,
`SetDefaultLeverage` and a simulated venue's `DefaultLeverage` and `Leverages`
all refuse it where it is written.

## The switch

`TradingNode.Halt(cancelOrders, closePositions)` stops a node trading without
stopping the node: every order a strategy submits is denied with
`TRADING_HALTED`, and the strategies keep running, keep their state and keep
seeing data. `Resume()` lets it trade again, `IsHalted` and `TradingState` say
where it stands, and `StartHalted` in the node's configuration (or
`bytex run --halted`) starts a node that places nothing until a host releases
it. What is resting is left alone unless the halt asks for it: `cancelOrders`
takes the resting orders back and `closePositions` flattens with reduce-only
market orders, both before the halt is in force, because a halt that denied
the orders closing the book would be a switch nobody could use.

## Account limits

`MaxLossPerPeriod` and `MaxExposure` are off unless they are set. Both are a
`RiskLimit`: either `RiskLimit.Of(money)`, which is only ever compared with a
figure in its own currency, or `RiskLimit.PercentOfEquity(percent)`, which is
read against the equity the account held when the period opened.

Both are measured from the portfolio, and both let through anything that
reduces - a reduce-only order, or one on the other side of a position the
account holds. A limit that stopped someone closing a losing position would
turn a bad day into a worse one.

The loss is what the period has given up in total: the realised PnL it lost
and what its open positions are down, together. A position held through a fall
costs the account exactly what one closed in the fall costs it, so a limit that
counted only what was closed would report the loss rather than stop it. Periods
are counted off the epoch, so `TimeSpan.FromDays(1)` is a UTC day, and the mark
- realised and open both - is taken again whenever the period rolls, so a
position that is still under water from yesterday does not spend today's limit.
What the account realised before the engine started belongs to no period the
engine measures.

### The loss is watched between fills

A strategy that buys, holds and sends nothing while the market falls submits
nothing for the engine to judge, which is exactly when the damage is done. So
the loss is not only checked when an order arrives: the engine watches the
prices that move an open position's worth - quotes, trades, bars and mark
prices - and reviews the figure as they come in, at most once per
`LossWatchInterval` (a second by default, of the engine's own clock, which is
simulated time in a backtest and therefore the same every run).
`RiskEngine.WatchOpenLoss()` does it on demand, and `LossWatchCount` says how
often it has run. In a live node the review happens on the kernel thread, like
everything else the node does with data.

### Reaching the limit stops the node

`OnLossLimit` says what a breach does:

| `LossLimitBreach` | What happens |
|---|---|
| `StopTrading` (default) | The engine puts itself into `Reducing` and stays there: nothing that would add gets through, everything that gets a position out still does, and a host resumes it. Once per period, so a host that resumes a node is not overruled a second later by the same loss; from then on the rest of the period is judged order by order. |
| `DenyAdds` | Only the order in front of it is denied, and the next one is judged on its own merits. |
| `Flatten` | Everything `StopTrading` does, and then it closes what is open: every working order cancelled and a reduce-only market order for every open position, sent through the engine like any other order. Never a default - closing someone's position unasked is the one thing a limit must not decide for itself. `LossLimitFlattenedCount` counts the positions it closed. |

`LossLimitStoppedCount` counts the times the engine stopped its own trading. A
host that means to trade past the limit raises or clears the limit rather than
resuming a node - the limit still denies what would add while the loss stands.

Stopping is not closing. A stopped node holds what it held, and the account goes
on losing on the very position that breached the limit until somebody closes it -
which is why `Flatten` exists, and why it has to be asked for.

Exposure is what the portfolio carries on the venue plus what the order being
judged would add; a submitted order list is judged order by order against what
the ones before it in the same list would already have added, so a list cannot
walk past the limit one small order at a time.

`RiskEngine.LossLimitDeniedCount` and `RiskEngine.ExposureLimitDeniedCount`
count what each limit stopped, and every denial is an `OrderDenied` event whose
reason names the limit and the figures. Exposure is judged on submission only:
it is what an order would add, and an order is the only thing that adds.

## Caps

The four caps are the ceiling on how much may be going at once, counted from
the cache: open positions and working orders, on one instrument and across the
account. Each is off unless it is set, each denial names the cap it hit, and
`RiskEngine.CapDeniedCount` counts them.

A cap, like a limit, never stops an order that reduces. Adding to a position
the account already holds opens nothing new, so only an instrument the account
is flat on can take `MaxOpenPositions` past its cap; and the orders of one
submitted list count towards the order caps as they are judged.

`RiskEngine.SetTradingState(TradingState.Halted | Reducing | Active)` changes
behaviour at runtime, for example from an operator command or a monitoring
actor.
