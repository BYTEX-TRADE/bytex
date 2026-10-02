# 0004 — Strategy SDK contract

Status: accepted (frozen for 0.x)

## Problem

Users need one programming model for anything that reacts to market data and
events: strategies that trade, monitors that only observe, execution algorithms
that split parent orders. The model must work identically in backtest and live
contexts and must be reachable from both hand-written C# and generated code
(plugins, see 0006).

## Decision

Three base classes, each a `Component` (lifecycle, see 0007):

```
Component
└── RuntimeModule                 data subscriptions, timers, cache/portfolio access, custom data and signals
    ├── Strategy          + order management, position management, order/position event handlers
    │   └── Strategy<TConfig>
    └── OrderSchedule     + receives parent orders, spawns child orders
```

### `RuntimeModule`

```csharp
public abstract class RuntimeModule : Component
{
    protected RuntimeModule(RuntimeModuleConfig? config = null);

    // Identity and services (set by the ModuleHost when registered; Component.Id is the ComponentId)
    public RuntimeModuleId RuntimeModuleId { get; }
    public ModuleHostId ModuleHostId { get; }
    protected IClock Clock { get; }
    protected ICache Cache { get; }
    protected IPortfolio Portfolio { get; }
    protected IMessageBus MessageBus { get; }
    protected ILogger Log { get; }
    public RuntimeModuleConfig Config { get; }

    // Lifecycle hooks (called on the tradingRuntime thread)
    protected virtual void OnStart() {}
    protected virtual void OnStop() {}
    protected virtual void OnResume() {}
    protected virtual void OnReset() {}
    protected virtual void OnDispose() {}
    protected virtual void OnDegrade() {}
    protected virtual void OnFault() {}
    protected virtual IDictionary<string, byte[]> OnSave() => empty;
    protected virtual void OnLoad(IDictionary<string, byte[]> state) {}

    // Data handlers
    protected virtual void OnInstrument(Instrument instrument) {}
    protected virtual void OnQuoteTick(QuoteTick tick) {}
    protected virtual void OnTradeTick(TradeTick tick) {}
    protected virtual void OnBar(Bar bar) {}
    protected virtual void OnOrderBookDeltas(OrderBookDeltas deltas) {}
    protected virtual void OnOrderBook(OrderBook book) {}
    protected virtual void OnInstrumentStatus(InstrumentStatus status) {}
    protected virtual void OnMarkPrice(MarkPriceUpdate update) {}
    protected virtual void OnIndexPrice(IndexPriceUpdate update) {}
    protected virtual void OnFundingRate(FundingRateUpdate update) {}
    protected virtual void OnData(IData data) {}                 // custom data
    protected virtual void OnSignal(Signal signal) {}
    protected virtual void OnHistoricalData(IData data) {}       // responses to Request*
    protected virtual void OnEvent(Event e) {}                   // catch-all, always called after the specific handler
    protected virtual void OnTimeEvent(TimeEvent e) {}           // for timers registered without a callback

    // Subscriptions (ClientId optional: default routing by venue)
    protected void SubscribeInstruments(Venue venue, ClientId? clientId = null);
    protected void SubscribeInstrument(MarketKey id, ClientId? clientId = null);
    protected void SubscribeQuoteTicks(MarketKey id, ClientId? clientId = null);
    protected void SubscribeTradeTicks(MarketKey id, ClientId? clientId = null);
    protected void SubscribeBars(CandleSeries candleSeries, ClientId? clientId = null);
    protected void SubscribeOrderBookDeltas(MarketKey id, BookType bookType, int depth = 0, ClientId? clientId = null);
    protected void SubscribeOrderBook(MarketKey id, BookType bookType, int depth = 0, TimeSpan interval = 1s, ClientId? clientId = null);
    protected void SubscribeInstrumentStatus(MarketKey id, ClientId? clientId = null);
    protected void SubscribeMarkPrices(MarketKey id, ClientId? clientId = null);
    protected void SubscribeIndexPrices(MarketKey id, ClientId? clientId = null);
    protected void SubscribeFundingRates(MarketKey id, ClientId? clientId = null);
    protected void SubscribeData(DataType dataType, ClientId? clientId = null);
    protected void SubscribeSignal(string name);
    protected void Unsubscribe…(same signatures)

    // Historical requests: responses arrive via OnHistoricalData; returns a correlation id
    protected Guid RequestInstrument(MarketKey id, ClientId? clientId = null);
    protected Guid RequestInstruments(Venue venue, ClientId? clientId = null);
    protected Guid RequestQuoteTicks(MarketKey id, UnixNanos? start, UnixNanos? end, int? limit, ClientId? clientId = null);
    protected Guid RequestTradeTicks(MarketKey id, UnixNanos? start, UnixNanos? end, int? limit, ClientId? clientId = null);
    protected Guid RequestBars(CandleSeries candleSeries, UnixNanos? start, UnixNanos? end, int? limit, ClientId? clientId = null);
    protected Guid RequestData(DataType dataType, UnixNanos? start, UnixNanos? end, int? limit, ClientId? clientId = null);

    // Publishing
    protected void PublishData(DataType dataType, IData data);
    protected void PublishSignal(string name, decimal value, UnixNanos? eventTime = null);

    // Indicators (automatic updates on subscribed data)
    protected void RegisterIndicatorForQuoteTicks(MarketKey id, IIndicator indicator);
    protected void RegisterIndicatorForTradeTicks(MarketKey id, IIndicator indicator);
    protected void RegisterIndicatorForBars(CandleSeries candleSeries, IIndicator indicator);
    protected IReadOnlyList<IIndicator> RegisteredIndicators { get; }
    protected bool IndicatorsInitialized { get; }

    // Background work: run on a thread-pool task, result delivered to the tradingRuntime thread
    protected void RunInBackground(Func<CancellationToken, Task> work, Action<Exception>? onError = null);
    protected void Post(Action action);   // marshal onto the tradingRuntime thread from anywhere
}

public record RuntimeModuleConfig
{
    public RuntimeModuleId? RuntimeModuleId { get; init; }
    public bool LogEvents { get; init; } = true;
    public bool LogCommands { get; init; } = true;
}
```

### `Strategy`

```csharp
public abstract class Strategy : RuntimeModule
{
    protected Strategy(StrategyConfig? config = null);

    public StrategyId StrategyId { get; }
    public new StrategyConfig Config { get; }
    protected OrderFactory OrderFactory { get; }
    public OmsType OmsType { get; }
    public IReadOnlyList<MarketKey> ExternalOrderClaims { get; }

    // Order and position event handlers (specific first, then OnOrderEvent/OnPositionEvent, then OnEvent)
    protected virtual void OnOrderInitialized(OrderInitialized e) {}
    protected virtual void OnOrderDenied(OrderDenied e) {}
    protected virtual void OnOrderEmulated(OrderEmulated e) {}
    protected virtual void OnOrderReleased(OrderReleased e) {}
    protected virtual void OnOrderSubmitted(OrderSubmitted e) {}
    protected virtual void OnOrderRejected(OrderRejected e) {}
    protected virtual void OnOrderAccepted(OrderAccepted e) {}
    protected virtual void OnOrderCanceled(OrderCanceled e) {}
    protected virtual void OnOrderExpired(OrderExpired e) {}
    protected virtual void OnOrderTriggered(OrderTriggered e) {}
    protected virtual void OnOrderPendingUpdate(OrderPendingUpdate e) {}
    protected virtual void OnOrderPendingCancel(OrderPendingCancel e) {}
    protected virtual void OnOrderModifyRejected(OrderModifyRejected e) {}
    protected virtual void OnOrderCancelRejected(OrderCancelRejected e) {}
    protected virtual void OnOrderUpdated(OrderUpdated e) {}
    protected virtual void OnOrderFilled(OrderFilled e) {}
    protected virtual void OnOrderEvent(OrderEvent e) {}
    protected virtual void OnPositionOpened(PositionOpened e) {}
    protected virtual void OnPositionChanged(PositionChanged e) {}
    protected virtual void OnPositionClosed(PositionClosed e) {}
    protected virtual void OnPositionEvent(PositionEvent e) {}

    // Trading commands
    protected void SubmitOrder(Order order, PositionId? positionId = null, ClientId? clientId = null);
    protected void SubmitOrderList(OrderList list, PositionId? positionId = null, ClientId? clientId = null);
    protected void ModifyOrder(Order order, Quantity? quantity = null, Price? price = null, Price? triggerPrice = null, ClientId? clientId = null);
    protected void CancelOrder(Order order, ClientId? clientId = null);
    protected void CancelOrders(IReadOnlyList<Order> orders, ClientId? clientId = null);
    protected void CancelAllOrders(MarketKey id, OrderSide? side = null, ClientId? clientId = null);
    protected void ClosePosition(Position position, ClientId? clientId = null, IReadOnlyList<string>? tags = null, bool reduceOnly = true);
    protected void CloseAllPositions(MarketKey id, PositionSide? side = null, ClientId? clientId = null, bool reduceOnly = true);
    protected void QueryOrder(Order order, ClientId? clientId = null);
    protected void CancelGtdExpiry(Order order);

    // Bulk lifecycle helpers
    protected void CancelAllOrdersAllInstruments();
    protected void CloseAllPositionsAllInstruments();
}

public abstract class Strategy<TConfig> : Strategy where TConfig : StrategyConfig
{
    protected Strategy(TConfig config);
    public new TConfig Config { get; }
}

public record StrategyConfig : RuntimeModuleConfig
{
    public StrategyId? StrategyId { get; init; }
    public string? OrderIdTag { get; init; }              // tag segment in generated ids
    public OmsType OmsType { get; init; } = OmsType.Unspecified;
    public IReadOnlyList<MarketKey> ExternalOrderClaims { get; init; } = [];
    public bool ManageContingentOrders { get; init; }      // engine-side OCO/OUO management for venues without native support
    public bool ManageGtdExpiry { get; init; }             // engine-side GTD expiry timers
    public bool UseHyphensInClientOrderIds { get; init; } = true;
}
```

### `OrderFactory`

```csharp
public sealed class OrderFactory
{
    MarketOrder Market(MarketKey id, OrderSide side, Quantity qty, TimeInForce tif = Gtc, bool reduceOnly = false,
                       bool quoteQuantity = false, OrderScheduleId? orderSchedule = null, IReadOnlyDictionary<string,object>? execParams = null, IReadOnlyList<string>? tags = null);
    LimitOrder Limit(MarketKey id, OrderSide side, Quantity qty, Price price, TimeInForce tif = Gtc, UnixNanos? expireTime = null,
                     bool postOnly = false, bool reduceOnly = false, bool quoteQuantity = false, Quantity? displayQty = null,
                     TriggerType emulationTrigger = Default, ...);
    StopMarketOrder StopMarket(MarketKey id, OrderSide side, Quantity qty, Price triggerPrice, TriggerType triggerType = Default, ...);
    StopLimitOrder StopLimit(MarketKey id, OrderSide side, Quantity qty, Price price, Price triggerPrice, ...);
    MarketIfTouchedOrder MarketIfTouched(...);  LimitIfTouchedOrder LimitIfTouched(...);
    TrailingStopMarketOrder TrailingStopMarket(MarketKey id, OrderSide side, Quantity qty, decimal trailingOffset,
                                               TrailingOffsetType offsetType = Price, Price? activationPrice = null, ...);
    TrailingStopLimitOrder TrailingStopLimit(...);  MarketToLimitOrder MarketToLimit(...);

    OrderList BracketOrder(MarketKey id, OrderSide side, Quantity qty, Price? entryPrice, Price stopLossTrigger, Price takeProfitPrice,
                           OrderType entryType = Market, OrderType slType = StopMarket, OrderType tpType = Limit, TimeInForce tif = Gtc, ...);
    OrderList CreateList(IReadOnlyList<Order> orders);
    ClientOrderId GenerateClientOrderId();
    OrderListId GenerateOrderListId();
}
```

### `OrderSchedule`

```csharp
public abstract class OrderSchedule : RuntimeModule
{
    public OrderScheduleId Id { get; }
    protected abstract void OnOrder(Order order);               // parent order received
    protected MarketOrder SpawnMarket(Order primary, Quantity qty, TimeInForce tif = Gtc, bool reduceOnly = false, IReadOnlyList<string>? tags = null);
    protected LimitOrder SpawnLimit(Order primary, Quantity qty, Price price, ...);
    protected MarketToLimitOrder SpawnMarketToLimit(Order primary, Quantity qty, ...);
    protected void SubmitOrder(Order order);  ModifyOrder; CancelOrder;
    protected void OnOrderEvent(OrderEvent e);                  // events for spawned orders
}
```

### Cache and portfolio read models

```csharp
public interface ICache
{
    // Instruments
    Instrument? Instrument(MarketKey id); IReadOnlyList<Instrument> Instruments(Venue? venue = null);
    // Market data (most recent first)
    QuoteTick? QuoteTick(MarketKey id, int index = 0); IReadOnlyList<QuoteTick> QuoteTicks(MarketKey id);
    TradeTick? TradeTick(MarketKey id, int index = 0); IReadOnlyList<TradeTick> TradeTicks(MarketKey id);
    Bar? Bar(CandleSeries type, int index = 0); IReadOnlyList<Bar> Bars(CandleSeries type);
    OrderBook? OrderBook(MarketKey id); Price? Price(MarketKey id, PriceType type);
    decimal? ExchangeRate(Currency from, Currency to, PriceType type = Mid);
    // Execution
    Order? Order(ClientOrderId id); Order? OrderForVenueId(VenueOrderId id);
    IReadOnlyList<Order> Orders(Venue? venue = null, MarketKey? id = null, StrategyId? strategy = null, OrderSide? side = null);
    IReadOnlyList<Order> OrdersOpen(...); OrdersClosed(...); OrdersEmulated(...); OrdersInflight(...);
    int OrdersOpenCount(...); bool IsOrderOpen(ClientOrderId id); ...
    OrderList? OrderList(OrderListId id); IReadOnlyList<OrderList> OrderLists(...);
    Position? Position(PositionId id); Position? PositionForOrder(ClientOrderId id);
    IReadOnlyList<Position> Positions(Venue? venue = null, MarketKey? id = null, StrategyId? strategy = null, PositionSide? side = null);
    IReadOnlyList<Position> PositionsOpen(...); PositionsClosed(...); bool IsPositionOpen(PositionId id); ...
    Account? Account(AccountId id); Account? AccountForVenue(Venue venue); IReadOnlyList<Account> Accounts();
    // Arbitrary state for runtimeModules
    void Add(string key, byte[] value); byte[]? Get(string key);
}

public interface IPortfolio
{
    Account? Account(Venue venue);
    IReadOnlyDictionary<Currency, Money> BalancesLocked(Venue venue);
    IReadOnlyDictionary<Currency, Money> MarginsInit(Venue venue); MarginsMaint(Venue venue);
    IReadOnlyDictionary<Currency, Money> UnrealizedPnls(Venue venue); RealizedPnls(Venue venue);
    IReadOnlyDictionary<Currency, Money> NetExposures(Venue venue);
    Money? UnrealizedPnl(MarketKey id); Money? RealizedPnl(MarketKey id); Money? NetExposure(MarketKey id);
    decimal NetPosition(MarketKey id);
    bool IsNetLong(MarketKey id); bool IsNetShort(MarketKey id); bool IsFlat(MarketKey id); bool IsCompletelyFlat();
}
```

### Execution semantics guaranteed to strategies

- Handlers are invoked on the tradingRuntime thread, one at a time, in the order the
  engine processed the underlying messages.
- By the time `OnQuoteTick` (or any data handler) runs, the data is already in
  the cache. By the time `OnOrderFilled` runs, the order and position in the
  cache already reflect the fill.
- `SubmitOrder` is asynchronous: it returns immediately after the order has
  been initialised and handed to the order policy. The strategy learns the
  outcome through events (`OnOrderDenied`, `OnOrderSubmitted`, ...).
- An exception thrown from any handler faults the runtimeModule (state `Faulted`) and
  is logged; other runtimeModules continue. The tradingRuntime does not crash because one
  strategy did.
- `OnStop` is the place to cancel orders and flatten positions; the engine
  does not do it automatically unless the moduleHost-level configuration says so.

## Alternatives considered

- **Interface-based handlers (`IHandle<T>`).** Rejected: virtual methods give
  discoverability in the IDE and a single obvious place to override.
- **Async handlers.** Rejected: handlers must not await; allowing `Task`
  returns invites blocking the tradingRuntime. Background work is explicit through
  `RunInBackground`.

## Consequences

- Generated strategies (plugins) subclass `Strategy` like everyone else; there
  is no second strategy model.
- The contract is large but flat; adding a data type means adding one
  `Subscribe*` method and one `On*` handler, which is an additive change.
