# Orders

## Types

| Type | Parameters | Behaviour |
|---|---|---|
| `Market` | — | fills immediately at the best available price |
| `Limit` | price | rests at the price; fills when the market reaches it |
| `StopMarket` | trigger | becomes a market order when the market moves through the trigger (buy: rises to it; sell: falls to it) |
| `StopLimit` | price, trigger | becomes a limit order when triggered |
| `MarketIfTouched` | trigger | becomes a market order when the market touches the trigger from the other side (buy: falls to it) |
| `LimitIfTouched` | price, trigger | becomes a limit order when touched |
| `TrailingStopMarket` | offset, offset type, optional activation | stop that follows the market by the offset (price, basis points, or ticks) |
| `TrailingStopLimit` | offset, limit offset | as above, releasing a limit order |
| `MarketToLimit` | — | fills the available liquidity at market; the remainder rests as a limit at the fill price |

## Time in force

`Gtc` (default), `Ioc`, `Fok`, `Gtd` (requires `expireTime`), `Day`,
`AtTheOpen`, `AtTheClose`. With `ManageGtdExpiry` the strategy cancels GTD
orders locally when they expire; otherwise the venue must support GTD.

In a backtest or sandbox, `Day` expires when the UTC date rolls over rather
than at a venue's session end. A fill is bounded by the size on offer where it
happens, so an `Ioc` order keeps what it got and gives up the rest, a `Fok`
order that cannot be filled whole is not filled at all, an iceberg shows one
slice at a time, and a market-to-limit order rests its remainder at the price
it got.

Where the venue has a book, an order that takes is not bounded by the touch: it
walks the levels it can reach, best price first, paying what each one costs and
never past its own limit price, so a size larger than the touch is filled now
at a worse average instead of over time. A `Fok` order is then decided over the
whole book it can reach rather than one level of it, and a market order that
eats the book and still has a remainder gives that remainder up. On bars the
bar's `BarVolumeShare` bounds the fill and no book is walked. A venue
configured with `FillSizing.WholeFills` fills every order whole at one price
instead, ignoring both the book and the bound, which is what the simulator did
before 0.5.

An order that rests is the other side of the same thing: it joins the queue at
its price behind whatever is quoted there, and a print at that price serves
that queue before it reaches the order. A node reports where each of its
working orders stands - `sizeAhead` and `queuePosition` on every order in a
`view` - so "it has not filled yet" and "it will not fill until five more go
through at that price" are not the same row on a screen. See
[known limitations](backtesting.md#known-limitations).

## Leverage

A document says what leverage it is written for with `account.leverage` (1 by
default, which is what a document that says nothing gets). It is a requirement
rather than a request: the venue grants leverage, and a run whose venue grants
less refuses to trade the strategy instead of sizing it at what the venue
allows. A strategy written for ten times leverage and run at one is a different
strategy, not a cautious version of the same one. A venue granting more is no
objection - the document states a floor.

Position sizing then uses the leverage the account holds for the instrument:
the most a document may take is the free balance times that leverage. Leverage
and nothing else - a venue's own margin arithmetic would allow more, and
folding its margin rate into the size as well would mean a document asking for
no leverage still opened many times what it can pay for.

## Instructions

- `postOnly` — reject instead of crossing the book.
- `reduceOnly` — reject if the order would open or increase a position.
- `displayQuantity` — iceberg display size.
- `quoteQuantity` — quantity expressed in quote currency (venues that support it).
- `triggerType` — `LastPrice`, `BidAsk`, `MarkPrice`, `IndexPrice` for conditional orders.

## Contingencies

`OrderFactory.BracketOrder` builds an entry with a stop-loss and take-profit:
the entry is `Oto` (children released when it fills), the exits are `Ouo`
(one updates/cancels the other). `CreateList` groups independent orders. The
simulated venue supports these natively; for live venues without native
support set `ManageContingentOrders` on the strategy and the engine cancels or
resizes linked orders itself.

## Identifiers

- `ClientOrderId`: `O-{yyyyMMdd}-{HHmmss}-{trader_tag}-{strategy_tag}-{count}`, seeded from the cache on restart so ids never repeat.
- `VenueOrderId`: assigned by the venue on acceptance.
- `PositionId` (netting): `{instrument}-{strategy}` for the first position; `{instrument}-{strategy}-{n}` for subsequent ones after a flat. Hedging: venue-supplied or `P-{date}-{time}-{n}`.

## Worked in slices

A size that would move the market if it went out at once can be worked as many
smaller orders at an even pace. An order asks for that by naming an execution
algorithm; the algorithm receives it and sends the slices.

```csharp
engine.AddExecAlgorithm(new TwapExecAlgorithm(new TwapExecAlgorithmConfig
{
    ExecAlgorithmId = new ExecAlgorithmId("TWAP"),
    Horizon = TimeSpan.FromMinutes(10),
    Interval = TimeSpan.FromMinutes(1),
}));

// Ten slices over ten minutes, the first one now.
SubmitOrder(OrderFactory.Market(instrumentId, OrderSide.Buy, quantity,
    execAlgorithmId: new ExecAlgorithmId("TWAP")));
```

`TwapExecAlgorithm` is the reference implementation: as many equal slices as the
interval fits into the horizon, the first at once and one every interval after
it, with whatever rounding is left over going out on the last so the slices add
up to the order exactly and never to more. A venue with a minimum order size
gets fewer, larger slices rather than a stream of rejections. A slice is a
market order when the order is a market order and a limit order at the order's
own price when it is a limit order: the pace is the algorithm's business, the
price is the order's.

An order may carry its own pace in `execAlgorithmParams`, as `horizon` and
`interval` written the way a time span is written (`"00:05:00"`).

A strategy document asks for the same thing in the `work` parameter of its
`act.order` node, in minutes, and nothing has to be registered by hand: the
document names `twap`, and the trader has a `TwapExecAlgorithm` running under
the id `TWAP` before the first bar. See
[documents](documents.md#working-an-order-in-pieces).

The order that asked is the instruction and never reaches a venue: the slices
are the orders, each carrying the instruction's id as its `ExecSpawnId`, so
`cache.OrdersForExecSpawn(id)` is what went out, what filled and at what
average. Cancelling the instruction stops the slices; an order the algorithm
cannot work in slices - anything that is not a market or a limit order - is
cancelled with the reason logged rather than left looking alive.

Because the instruction never reaches a venue, it also never fills and never
closes: its status stays as it was written. Anything following a worked order -
a node, a monitor - follows the slices, and
`TrackedOrder` in the document runtime is what that looks like: it counts the
slices' fills against the instruction's quantity, publishes the volume-weighted
average as the fill price, and, when a slice was refused and the pace has run
out, reports what the order did get rather than waiting on one that is already
over.

Writing another algorithm means deriving from `ExecAlgorithm`: `OnOrder` receives
the instruction, and `SpawnMarket`, `SpawnLimit` and `SpawnMarketToLimit` build
slices that carry its id.

## Triggered here instead of at the venue

A venue that has no stop orders is still a venue a strategy with a stop can
trade: the trigger can be this engine's business rather than the venue's. An
order asks for that by naming the price its trigger is judged against -
`emulationTrigger`, one of `LastPrice`, `BidAsk`, `MarkPrice` or `IndexPrice`;
`Default`, which is what an order carries unless it says otherwise, leaves the
trigger to the venue.

```csharp
StopMarketOrder stop = OrderFactory.StopMarket(
    instrumentId, OrderSide.Sell, quantity, triggerPrice: Price.Parse("49000.00"),
    reduceOnly: true, emulationTrigger: TriggerType.BidAsk);
```

Such an order is held in the execution engine and the venue is told nothing. It
is `Emulated` (`cache.OrdersEmulated()`), it can be cancelled like any other
order, and it expires here if its own `GTD` time passes first. When the market
reaches the trigger, the order is `Released` and the venue is told about a
market order (from a stop-market or a market-if-touched) or a limit order at the
price it was holding (from a stop-limit or a limit-if-touched) - something every
venue can hold.

The released order keeps the id its owner submitted, and its history says what
happened to it: `OrderInitialized`, `OrderEmulated`, `OrderReleased`, then the
venue's own answers. Everything that refers to an order refers to it by that id,
and the fill has to come back on it.

A stop is reached when the market trades at its trigger or through it; an
if-touched order is the mirror, reached when the market comes back to it. A
`BidAsk` trigger reads the side the order would have to cross - the ask for a
buy, the bid for a sell. A trigger the market has already passed is reached the
moment the order arrives, rather than on the next tick.

Only stop-market, stop-limit, market-if-touched and limit-if-touched can be
triggered here; anything else asking for it is denied with
`EMULATION_UNSUPPORTED` rather than sent to the venue as if nothing had been
asked. An order list is sent to the venue as a list, so a list carrying one is
refused whole (`EMULATION_IN_LIST`): its legs are contingent on each other, and
one of them leaving on its own is not the list its owner submitted.

## State machine

Every order evolves only through events applied in a validated state machine:

```
Initialized → Denied | Emulated | Released | Submitted
Submitted   → Accepted | Rejected | Canceled | Expired | Triggered | PartiallyFilled | Filled | PendingUpdate | PendingCancel
Accepted    → Canceled | Expired | Triggered | PendingUpdate | PendingCancel | PartiallyFilled | Filled
Triggered   → Canceled | Expired | PendingUpdate | PendingCancel | PartiallyFilled | Filled
PendingUpdate / PendingCancel → back to the previous state on rejection, or on to Accepted / Canceled / Filled ...
PartiallyFilled → Canceled | Expired | PendingUpdate | PendingCancel | PartiallyFilled | Filled
```

`OrderUpdated`, `OrderModifyRejected`, and `OrderCancelRejected` do not change
the status except to leave a pending state. Duplicate fills (same `TradeId`)
are ignored. Every event is kept on the order (`order.Events`), which is what
the Redis persistence stores and replays.

## Positions

Fills update a `Position`: average open price, realized P&amp;L (net of
commissions in the settlement currency), peak quantity, and timestamps. In
netting mode a fill larger than the open position closes it and opens a new
one on the other side with a fresh id. Unrealized P&amp;L is computed against
the latest bid (long) or ask (short).
