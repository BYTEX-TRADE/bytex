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
└── Actor                 data subscriptions, timers, cache/portfolio access, custom data and signals
    ├── Strategy          + order management, position management, order/position event handlers
    │   └── Strategy<TConfig>
    └── ExecAlgorithm     + receives parent orders, spawns child orders
```

### `Actor`

```csharp
public abstract class Actor : Component
{
    protected Actor(ActorConfig? config = null);

    // Identity and services (set by the Trader when registered; Component.Id is the ComponentId)
    public ActorId ActorId { get; }
    public TraderId TraderId { get; }
    protected IClock Clock { get; }
    protected ICache Cache { get; }
    protected IPortfolio Portfolio { get; }
    protected IMessageBus MessageBus { get; }
    protected ILogger Log { get; }
    public ActorConfig Config { get; }

    // Lifecycle hooks (called on the kernel thread)
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
    protected void SubscribeInstrument(InstrumentId id, ClientId? clientId = null);
    protected void SubscribeQuoteTicks(InstrumentId id, ClientId? clientId = null);
    protected void SubscribeTradeTicks(InstrumentId id, ClientId? clientId = null);
    protected void SubscribeBars(BarType barType, ClientId? clientId = null);
    protected void SubscribeOrderBookDeltas(InstrumentId id, BookType bookType, int depth = 0, ClientId? clientId = null);
    protected void SubscribeOrderBook(InstrumentId id, BookType bookType, int depth = 0, TimeSpan interval = 1s, ClientId? clientId = null);
    protected void SubscribeInstrumentStatus(InstrumentId id, ClientId? clientId = null);
    protected void SubscribeMarkPrices(InstrumentId id, ClientId? clientId = null);
    protected void SubscribeIndexPrices(InstrumentId id, ClientId? clientId = null);
    protected void SubscribeFundingRates(InstrumentId id, ClientId? clientId = null);
    protected void SubscribeData(DataType dataType, ClientId? clientId = null);
    protected void SubscribeSignal(string name);
    protected void Unsubscribe…(same signatures)

    // Historical requests: responses arrive via OnHistoricalData; returns a correlation id
    protected Guid RequestInstrument(InstrumentId id, ClientId? clientId = null);
    protected Guid RequestInstruments(Venue venue, ClientId? clientId = null);
    protected Guid RequestQuoteTicks(InstrumentId id, UnixNanos? start, UnixNanos? end, int? limit, ClientId? clientId = null);
    protected Guid RequestTradeTicks(InstrumentId id, UnixNanos? start, UnixNanos? end, int? limit, ClientId? clientId = null);
    protected Guid RequestBars(BarType barType, UnixNanos? start, UnixNanos? end, int? limit, ClientId? clientId = null);
    protected Guid RequestData(DataType dataType, UnixNanos? start, UnixNanos? end, int? limit, ClientId? clientId = null);

    // Publishing
    protected void PublishData(DataType dataType, IData data);
    protected void PublishSignal(string name, decimal value, UnixNanos? tsEvent = null);

    // Indicators (automatic updates on subscribed data)
    protected void RegisterIndicatorForQuoteTicks(InstrumentId id, IIndicator indicator);
    protected void RegisterIndicatorForTradeTicks(InstrumentId id, IIndicator indicator);
    protected void RegisterIndicatorForBars(BarType barType, IIndicator indicator);
    protected IReadOnlyList<IIndicator> RegisteredIndicators { get; }
    protected bool IndicatorsInitialized { get; }

    // Background work: run on a thread-pool task, result delivered to the kernel thread
    protected void RunInBackground(Func<CancellationToken, Task> work, Action<Exception>? onError = null);
    protected void Post(Action action);   // marshal onto the kernel thread from anywhere
}

public record ActorConfig
{
    public ActorId? ActorId { get; init; }
    public bool LogEvents { get; init; } = true;
    public bool LogCommands { get; init; } = true;
}
```

### `Strategy`

```csharp
public abstract class Strategy : Actor
{
    protected Strategy(StrategyConfig? config = null);

    public StrategyId StrategyId { get; }
    public new StrategyConfig Config { get; }
    protected OrderFactory OrderFactory { get; }
    public OmsType OmsType { get; }
    public IReadOnlyList<InstrumentId> ExternalOrderClaims { get; }

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
    protected void CancelAllOrders(InstrumentId id, OrderSide? side = null, ClientId? clientId = null);
    protected void ClosePosition(Position position, ClientId? clientId = null, IReadOnlyList<string>? tags = null, bool reduceOnly = true);
    protected void CloseAllPositions(InstrumentId id, PositionSide? side = null, ClientId? clientId = null, bool reduceOnly = true);
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

public record StrategyConfig : ActorConfig
{
    public StrategyId? StrategyId { get; init; }
    public string? OrderIdTag { get; init; }              // tag segment in generated ids
    public OmsType OmsType { get; init; } = OmsType.Unspecified;
    public IReadOnlyList<InstrumentId> ExternalOrderClaims { get; init; } = [];
    public bool ManageContingentOrders { get; init; }      // engine-side OCO/OUO management for venues without native support
    public bool ManageGtdExpiry { get; init; }             // engine-side GTD expiry timers
    public bool UseHyphensInClientOrderIds { get; init; } = true;
}
```

### `OrderFactory`

```csharp
public sealed class OrderFactory
{
    MarketOrder Market(InstrumentId id, OrderSide side, Quantity qty, TimeInForce tif = Gtc, bool reduceOnly = false,
                       bool quoteQuantity = false, ExecAlgorithmId? execAlgorithm = null, IReadOnlyDictionary<string,object>? execParams = null, IReadOnlyList<string>? tags = null);
    LimitOrder Limit(InstrumentId id, OrderSide side, Quantity qty, Price price, TimeInForce tif = Gtc, UnixNanos? expireTime = null,
                     bool postOnly = false, bool reduceOnly = false, bool quoteQuantity = false, Quantity? displayQty = null,
                     TriggerType emulationTrigger = Default, ...);
    StopMarketOrder StopMarket(InstrumentId id, OrderSide side, Quantity qty, Price triggerPrice, TriggerType triggerType = Default, ...);
    StopLimitOrder StopLimit(InstrumentId id, OrderSide side, Quantity qty, Price price, Price triggerPrice, ...);
    MarketIfTouchedOrder MarketIfTouched(...);  LimitIfTouchedOrder LimitIfTouched(...);
    TrailingStopMarketOrder TrailingStopMarket(InstrumentId id, OrderSide side, Quantity qty, decimal trailingOffset,
                                               TrailingOffsetType offsetType = Price, Price? activationPrice = null, ...);
    TrailingStopLimitOrder TrailingStopLimit(...);  MarketToLimitOrder MarketToLimit(...);

    OrderList BracketOrder(InstrumentId id, OrderSide side, Quantity qty, Price? entryPrice, Price stopLossTrigger, Price takeProfitPrice,
                           OrderType entryType = Market, OrderType slType = StopMarket, OrderType tpType = Limit, TimeInForce tif = Gtc, ...);
    OrderList CreateList(IReadOnlyList<Order> orders);
    ClientOrderId GenerateClientOrderId();
    OrderListId GenerateOrderListId();
}
```

### `ExecAlgorithm`

```csharp
public abstract class ExecAlgorithm : Actor
{
    public ExecAlgorithmId Id { get; }
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
    Instrument? Instrument(InstrumentId id); IReadOnlyList<Instrument> Instruments(Venue? venue = null);
    // Market data (most recent first)
    QuoteTick? QuoteTick(InstrumentId id, int index = 0); IReadOnlyList<QuoteTick> QuoteTicks(InstrumentId id);
    TradeTick? TradeTick(InstrumentId id, int index = 0); IReadOnlyList<TradeTick> TradeTicks(InstrumentId id);
    Bar? Bar(BarType type, int index = 0); IReadOnlyList<Bar> Bars(BarType type);
    OrderBook? OrderBook(InstrumentId id); Price? Price(InstrumentId id, PriceType type);
    decimal? ExchangeRate(Currency from, Currency to, PriceType type = Mid);
    // Execution
    Order? Order(ClientOrderId id); Order? OrderForVenueId(VenueOrderId id);
    IReadOnlyList<Order> Orders(Venue? venue = null, InstrumentId? id = null, StrategyId? strategy = null, OrderSide? side = null);
    IReadOnlyList<Order> OrdersOpen(...); OrdersClosed(...); OrdersEmulated(...); OrdersInflight(...);
    int OrdersOpenCount(...); bool IsOrderOpen(ClientOrderId id); ...
    OrderList? OrderList(OrderListId id); IReadOnlyList<OrderList> OrderLists(...);
    Position? Position(PositionId id); Position? PositionForOrder(ClientOrderId id);
    IReadOnlyList<Position> Positions(Venue? venue = null, InstrumentId? id = null, StrategyId? strategy = null, PositionSide? side = null);
    IReadOnlyList<Position> PositionsOpen(...); PositionsClosed(...); bool IsPositionOpen(PositionId id); ...
    Account? Account(AccountId id); Account? AccountForVenue(Venue venue); IReadOnlyList<Account> Accounts();
    // Arbitrary state for actors
    void Add(string key, byte[] value); byte[]? Get(string key);
}

public interface IPortfolio
{
    Account? Account(Venue venue);
    IReadOnlyDictionary<Currency, Money> BalancesLocked(Venue venue);
    IReadOnlyDictionary<Currency, Money> MarginsInit(Venue venue); MarginsMaint(Venue venue);
    IReadOnlyDictionary<Currency, Money> UnrealizedPnls(Venue venue); RealizedPnls(Venue venue);
    IReadOnlyDictionary<Currency, Money> NetExposures(Venue venue);
    Money? UnrealizedPnl(InstrumentId id); Money? RealizedPnl(InstrumentId id); Money? NetExposure(InstrumentId id);
    decimal NetPosition(InstrumentId id);
    bool IsNetLong(InstrumentId id); bool IsNetShort(InstrumentId id); bool IsFlat(InstrumentId id); bool IsCompletelyFlat();
}
```

### Execution semantics guaranteed to strategies

- Handlers are invoked on the kernel thread, one at a time, in the order the
  engine processed the underlying messages.
- By the time `OnQuoteTick` (or any data handler) runs, the data is already in
  the cache. By the time `OnOrderFilled` runs, the order and position in the
  cache already reflect the fill.
- `SubmitOrder` is asynchronous: it returns immediately after the order has
  been initialised and handed to the risk engine. The strategy learns the
  outcome through events (`OnOrderDenied`, `OnOrderSubmitted`, ...).
- An exception thrown from any handler faults the actor (state `Faulted`) and
  is logged; other actors continue. The kernel does not crash because one
  strategy did.
- `OnStop` is the place to cancel orders and flatten positions; the engine
  does not do it automatically unless the trader-level configuration says so.

## Alternatives considered

- **Interface-based handlers (`IHandle<T>`).** Rejected: virtual methods give
  discoverability in the IDE and a single obvious place to override.
- **Async handlers.** Rejected: handlers must not await; allowing `Task`
  returns invites blocking the kernel. Background work is explicit through
  `RunInBackground`.

## Consequences

- Generated strategies (plugins) subclass `Strategy` like everyone else; there
  is no second strategy model.
- The contract is large but flat; adding a data type means adding one
  `Subscribe*` method and one `On*` handler, which is an additive change.
