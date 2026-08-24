# Strategies

Everything that reacts to data is an `Actor`. A `Strategy` is an actor that
can also trade. An `ExecAlgorithm` is an actor that works parent orders by
spawning child orders.

```
Component            lifecycle state machine (Ready → Running → Stopped → ...)
└── Actor            subscriptions · requests · timers · indicators · cache/portfolio access · custom data · signals
    ├── Strategy     + order factory · trading commands · order/position event handlers
    │   └── Strategy<TConfig>
    └── ExecAlgorithm
```

## Lifecycle

| Hook | When |
|---|---|
| `OnStart` | trader starts; subscribe to data and request history here |
| `OnStop` | trader stops; cancel orders and flatten positions here if desired |
| `OnResume`, `OnReset`, `OnDispose`, `OnDegrade`, `OnFault` | the corresponding transitions |
| `OnSave` / `OnLoad` | return/restore a `Dictionary<string, byte[]>` of state, persisted through the cache |

## Data

Subscribe in `OnStart`; the matching handler receives each element after it is
already in the cache:

| Subscribe | Handler |
|---|---|
| `SubscribeInstruments(venue)` / `SubscribeInstrument(id)` | `OnInstrument` |
| `SubscribeQuoteTicks(id)` | `OnQuoteTick` |
| `SubscribeTradeTicks(id)` | `OnTradeTick` |
| `SubscribeBars(barType)` | `OnBar` |
| `SubscribeOrderBookDeltas(id, bookType, depth)` | `OnOrderBookDeltas` |
| `SubscribeOrderBook(id, bookType, depth, interval)` | `OnOrderBook` (periodic snapshots) |
| `SubscribeInstrumentStatus(id)` | `OnInstrumentStatus` |
| `SubscribeMarkPrices` / `SubscribeIndexPrices` / `SubscribeFundingRates` | `OnMarkPrice` / `OnIndexPrice` / `OnFundingRate` |
| `SubscribeData<T>(metadata)` | `OnData` |
| `SubscribeSignal(name)` | `OnSignal` |

Bar types ending in `-INTERNAL` are built by the data engine from ticks
(`SubscribeBars` on an internal bar type subscribes to the underlying quotes or
trades automatically). `-EXTERNAL` bars come from the venue.

Historical data: `RequestBars`, `RequestQuoteTicks`, `RequestTradeTicks`,
`RequestInstrument(s)`, `RequestData`. Results arrive in `OnHistoricalData`
(one element at a time) and `OnDataResponse` (the whole response); registered
indicators are updated with them. Requests return a correlation id and
`IsPendingRequest(id)` tells you whether it has completed.

Publishing: `PublishData(data)` and `PublishSignal(name, value)` reach any
actor that subscribed.

## Indicators

`RegisterIndicatorForBars(barType, indicator)` (and the quote/trade variants)
updates the indicator automatically before `OnBar` runs. `IndicatorsInitialized`
is true once every registered indicator has enough input.

## Timers

`SetTimer(name, interval, callback)` and `SetTimeAlert(name, at, callback)`
run on the kernel thread; in backtests they fire deterministically relative
to the data. Without a callback the event arrives in `OnTimeEvent`.

## Trading

`OrderFactory` builds orders with engine-generated identifiers:
`Market`, `Limit`, `StopMarket`, `StopLimit`, `MarketIfTouched`,
`LimitIfTouched`, `TrailingStopMarket`, `TrailingStopLimit`, `MarketToLimit`,
plus `BracketOrder` (entry + stop-loss + take-profit) and `CreateList`.

Commands: `SubmitOrder`, `SubmitOrderList`, `ModifyOrder`, `CancelOrder`,
`CancelOrders`, `CancelAllOrders`, `ClosePosition`, `CloseAllPositions`,
`QueryOrder`, and the bulk helpers `CancelAllOrdersAllInstruments` and
`CloseAllPositionsAllInstruments`.

Events: `OnOrderSubmitted`, `OnOrderAccepted`, `OnOrderRejected`,
`OnOrderFilled`, `OnOrderCanceled`, `OnOrderExpired`, `OnOrderTriggered`,
`OnOrderUpdated`, `OnOrderDenied`, `OnOrderModifyRejected`,
`OnOrderCancelRejected`, `OnOrderPendingUpdate`, `OnOrderPendingCancel`,
then `OnOrderEvent` for everything; `OnPositionOpened`, `OnPositionChanged`,
`OnPositionClosed`, then `OnPositionEvent`; finally `OnEvent`.

## Configuration

```csharp
public record StrategyConfig : ActorConfig
{
    StrategyId? StrategyId;              // "Name-Tag"; tag appears in generated order ids
    string? OrderIdTag;
    OmsType OmsType;                     // Netting (default via client) or Hedging
    IReadOnlyList<InstrumentId> ExternalOrderClaims;   // venue orders this strategy adopts at reconciliation
    bool ManageContingentOrders;         // engine-side OCO/OUO handling for venues without native support
    bool ManageGtdExpiry;                // expire GTD orders locally with timers
    bool UseHyphensInClientOrderIds;
}
```

Use `Strategy<TConfig>` with your own record deriving from `StrategyConfig`;
the CLI and node configuration deserialise JSON straight into it.

## Guarantees

- Handlers run on the kernel thread, one at a time, in engine order.
- Data is in the cache before its handler runs; orders and positions reflect a
  fill before `OnOrderFilled` runs.
- `SubmitOrder` returns immediately; outcomes arrive as events.
- An exception in a handler faults the strategy (state `Faulted`) and is
  logged; the kernel and other strategies continue.
- Handlers must not block. Use `RunInBackground(work)` for I/O or heavy
  computation and `Post(action)` to get back onto the kernel thread.
