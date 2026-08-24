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
