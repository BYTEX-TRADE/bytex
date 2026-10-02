# Strategies

Everything that reacts to data is a `RuntimeModule`. A `Strategy` is a runtime module that
can also trade. An `OrderSchedule` is a runtime module that works parent orders by
spawning child orders; `TwapSchedule` is the one the engine ships, and
[orders](orders.md) says what it does with an order.

```
Component            lifecycle state machine (Ready → Running → Stopped → ...)
└── RuntimeModule            subscriptions · requests · timers · indicators · cache/portfolio access · custom data · signals
    ├── Strategy     + order factory · trading commands · order/position event handlers
    │   └── Strategy<TConfig>
    └── OrderSchedule
```

## Lifecycle

| Hook | When |
|---|---|
| `OnStart` | moduleHost starts; subscribe to data and request history here |
| `OnStop` | moduleHost stops; cancel orders and flatten positions here if desired |
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
| `SubscribeBars(candleSeries)` | `OnBar` |
| `SubscribeOrderBookDeltas(id, bookType, depth)` | `OnOrderBookDeltas` |
| `SubscribeOrderBook(id, bookType, depth, interval)` | `OnOrderBook` (periodic snapshots) |
| `SubscribeInstrumentStatus(id)` | `OnInstrumentStatus` |
| `SubscribeMarkPrices` / `SubscribeIndexPrices` / `SubscribeFundingRates` | `OnMarkPrice` / `OnIndexPrice` / `OnFundingRate` |
| `SubscribeData<T>(metadata)` | `OnData` |
| `SubscribeSignal(name)` | `OnSignal` |

Candle series with origin `computed` are built by MarketDataService from ticks
(`SubscribeBars` on a computed series subscribes to the underlying quotes or
trades automatically). Series with origin `provider` come from the venue.

Historical data: `RequestBars`, `RequestQuoteTicks`, `RequestTradeTicks`,
`RequestInstrument(s)`, `RequestData`. Results arrive in `OnHistoricalData`
(one element at a time) and `OnDataResponse` (the whole response); registered
indicators are updated with them. Requests return a correlation id and
`IsPendingRequest(id)` tells you whether it has completed.

Publishing: `PublishData(data)` and `PublishSignal(name, value)` reach any
runtimeModule that subscribed.

## Indicators

`RegisterIndicatorForBars(candleSeries, indicator)` (and the quote/trade variants)
updates the indicator automatically before `OnBar` runs. `IndicatorsInitialized`
is true once every registered indicator has enough input.

## Timers

`SetTimer(name, interval, callback)` and `SetTimeAlert(name, at, callback)`
run on the tradingRuntime thread; in backtests they fire deterministically relative
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
public record StrategyConfig : RuntimeModuleConfig
{
    StrategyId? StrategyId;              // "Name-Tag"; tag appears in generated order ids
    string? OrderIdTag;
    OmsType OmsType;                     // Netting (default via client) or Hedging
    IReadOnlyList<MarketKey> ExternalOrderClaims;   // venue orders this strategy adopts at reconciliation
    bool ManageContingentOrders;         // engine-side OCO/OUO handling for venues without native support
    bool ManageGtdExpiry;                // expire GTD orders locally with timers
    bool UseHyphensInClientOrderIds;
}
```

Use `Strategy<TConfig>` with your own record deriving from `StrategyConfig`;
the CLI and node configuration deserialise JSON straight into it.

## Guarantees

- Handlers run on the tradingRuntime thread, one at a time, in engine order.
- Data is in the cache before its handler runs; orders and positions reflect a
  fill before `OnOrderFilled` runs.
- `SubmitOrder` returns immediately; outcomes arrive as events.
- An exception in a handler faults the strategy (state `Faulted`) and is
  logged; the tradingRuntime and other strategies continue.
- Handlers must not block. Use `RunInBackground(work)` for I/O or heavy
  computation and `Post(action)` to get back onto the tradingRuntime thread.
