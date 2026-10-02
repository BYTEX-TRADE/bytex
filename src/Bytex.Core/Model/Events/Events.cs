using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Primitives;

namespace Bytex.Core.Model.Events;

/// <summary>
/// Base type for everything the engine publishes as an event.
/// </summary>
public abstract record Event(Guid EventId, UnixNanos EventTime, UnixNanos CreatedTime);

public sealed record ComponentStateChanged(
    ComponentId ComponentId,
    ComponentState PreviousState,
    ComponentState State,
    Guid EventId,
    UnixNanos EventTime,
    UnixNanos CreatedTime) : Event(EventId, EventTime, CreatedTime);

public sealed record TimeEvent(string Name, Guid EventId, UnixNanos EventTime, UnixNanos CreatedTime) : Event(EventId, EventTime, CreatedTime);

/// <summary>
/// Base type for all order lifecycle events.
/// </summary>
public abstract record OrderEvent(
    ModuleHostId ModuleHostId,
    StrategyId StrategyId,
    MarketKey MarketKey,
    ClientOrderId ClientOrderId,
    VenueOrderId? VenueOrderId,
    AccountId? AccountId,
    Guid EventId,
    UnixNanos EventTime,
    UnixNanos CreatedTime,
    bool Reconciliation = false) : Event(EventId, EventTime, CreatedTime);

public sealed record OrderInitialized(
    ModuleHostId ModuleHostId,
    StrategyId StrategyId,
    MarketKey MarketKey,
    ClientOrderId ClientOrderId,
    OrderSide OrderSide,
    OrderType OrderType,
    Quantity Quantity,
    TimeInForce TimeInForce,
    bool PostOnly,
    bool ReduceOnly,
    bool QuoteQuantity,
    IReadOnlyDictionary<string, string> Options,
    TriggerType EmulationTrigger,
    MarketKey? TriggerMarketKey,
    ContingencyType Contingency,
    OrderListId? OrderListId,
    IReadOnlyList<ClientOrderId> LinkedOrderIds,
    ClientOrderId? ParentOrderId,
    OrderScheduleId? OrderScheduleId,
    IReadOnlyDictionary<string, string>? OrderScheduleParams,
    ClientOrderId? ExecSpawnId,
    IReadOnlyList<string> Tags,
    Guid EventId,
    UnixNanos EventTime,
    UnixNanos CreatedTime,
    bool Reconciliation = false)
    : OrderEvent(ModuleHostId, StrategyId, MarketKey, ClientOrderId, null, null, EventId, EventTime, CreatedTime, Reconciliation);

public sealed record OrderDenied(
    ModuleHostId ModuleHostId, StrategyId StrategyId, MarketKey MarketKey, ClientOrderId ClientOrderId,
    string Reason, Guid EventId, UnixNanos EventTime, UnixNanos CreatedTime)
    : OrderEvent(ModuleHostId, StrategyId, MarketKey, ClientOrderId, null, null, EventId, EventTime, CreatedTime);

public sealed record OrderEmulated(
    ModuleHostId ModuleHostId, StrategyId StrategyId, MarketKey MarketKey, ClientOrderId ClientOrderId,
    Guid EventId, UnixNanos EventTime, UnixNanos CreatedTime)
    : OrderEvent(ModuleHostId, StrategyId, MarketKey, ClientOrderId, null, null, EventId, EventTime, CreatedTime);

public sealed record OrderReleased(
    ModuleHostId ModuleHostId, StrategyId StrategyId, MarketKey MarketKey, ClientOrderId ClientOrderId,
    Price ReleasedPrice, Guid EventId, UnixNanos EventTime, UnixNanos CreatedTime)
    : OrderEvent(ModuleHostId, StrategyId, MarketKey, ClientOrderId, null, null, EventId, EventTime, CreatedTime);

public sealed record OrderSubmitted(
    ModuleHostId ModuleHostId, StrategyId StrategyId, MarketKey MarketKey, ClientOrderId ClientOrderId,
    AccountId? AccountId, Guid EventId, UnixNanos EventTime, UnixNanos CreatedTime)
    : OrderEvent(ModuleHostId, StrategyId, MarketKey, ClientOrderId, null, AccountId, EventId, EventTime, CreatedTime);

public sealed record OrderAccepted(
    ModuleHostId ModuleHostId, StrategyId StrategyId, MarketKey MarketKey, ClientOrderId ClientOrderId,
    VenueOrderId? VenueOrderId, AccountId? AccountId, Guid EventId, UnixNanos EventTime, UnixNanos CreatedTime, bool Reconciliation = false)
    : OrderEvent(ModuleHostId, StrategyId, MarketKey, ClientOrderId, VenueOrderId, AccountId, EventId, EventTime, CreatedTime, Reconciliation);

public sealed record OrderRejected(
    ModuleHostId ModuleHostId, StrategyId StrategyId, MarketKey MarketKey, ClientOrderId ClientOrderId,
    AccountId? AccountId, string Reason, Guid EventId, UnixNanos EventTime, UnixNanos CreatedTime, bool Reconciliation = false)
    : OrderEvent(ModuleHostId, StrategyId, MarketKey, ClientOrderId, null, AccountId, EventId, EventTime, CreatedTime, Reconciliation);

public sealed record OrderCanceled(
    ModuleHostId ModuleHostId, StrategyId StrategyId, MarketKey MarketKey, ClientOrderId ClientOrderId,
    VenueOrderId? VenueOrderId, AccountId? AccountId, Guid EventId, UnixNanos EventTime, UnixNanos CreatedTime, bool Reconciliation = false)
    : OrderEvent(ModuleHostId, StrategyId, MarketKey, ClientOrderId, VenueOrderId, AccountId, EventId, EventTime, CreatedTime, Reconciliation);

public sealed record OrderExpired(
    ModuleHostId ModuleHostId, StrategyId StrategyId, MarketKey MarketKey, ClientOrderId ClientOrderId,
    VenueOrderId? VenueOrderId, AccountId? AccountId, Guid EventId, UnixNanos EventTime, UnixNanos CreatedTime, bool Reconciliation = false)
    : OrderEvent(ModuleHostId, StrategyId, MarketKey, ClientOrderId, VenueOrderId, AccountId, EventId, EventTime, CreatedTime, Reconciliation);

public sealed record OrderTriggered(
    ModuleHostId ModuleHostId, StrategyId StrategyId, MarketKey MarketKey, ClientOrderId ClientOrderId,
    VenueOrderId? VenueOrderId, AccountId? AccountId, Guid EventId, UnixNanos EventTime, UnixNanos CreatedTime, bool Reconciliation = false)
    : OrderEvent(ModuleHostId, StrategyId, MarketKey, ClientOrderId, VenueOrderId, AccountId, EventId, EventTime, CreatedTime, Reconciliation);

public sealed record OrderPendingUpdate(
    ModuleHostId ModuleHostId, StrategyId StrategyId, MarketKey MarketKey, ClientOrderId ClientOrderId,
    VenueOrderId? VenueOrderId, AccountId? AccountId, Guid EventId, UnixNanos EventTime, UnixNanos CreatedTime, bool Reconciliation = false)
    : OrderEvent(ModuleHostId, StrategyId, MarketKey, ClientOrderId, VenueOrderId, AccountId, EventId, EventTime, CreatedTime, Reconciliation);

public sealed record OrderPendingCancel(
    ModuleHostId ModuleHostId, StrategyId StrategyId, MarketKey MarketKey, ClientOrderId ClientOrderId,
    VenueOrderId? VenueOrderId, AccountId? AccountId, Guid EventId, UnixNanos EventTime, UnixNanos CreatedTime, bool Reconciliation = false)
    : OrderEvent(ModuleHostId, StrategyId, MarketKey, ClientOrderId, VenueOrderId, AccountId, EventId, EventTime, CreatedTime, Reconciliation);

public sealed record OrderModifyRejected(
    ModuleHostId ModuleHostId, StrategyId StrategyId, MarketKey MarketKey, ClientOrderId ClientOrderId,
    VenueOrderId? VenueOrderId, AccountId? AccountId, string Reason, Guid EventId, UnixNanos EventTime, UnixNanos CreatedTime, bool Reconciliation = false)
    : OrderEvent(ModuleHostId, StrategyId, MarketKey, ClientOrderId, VenueOrderId, AccountId, EventId, EventTime, CreatedTime, Reconciliation);

public sealed record OrderCancelRejected(
    ModuleHostId ModuleHostId, StrategyId StrategyId, MarketKey MarketKey, ClientOrderId ClientOrderId,
    VenueOrderId? VenueOrderId, AccountId? AccountId, string Reason, Guid EventId, UnixNanos EventTime, UnixNanos CreatedTime, bool Reconciliation = false)
    : OrderEvent(ModuleHostId, StrategyId, MarketKey, ClientOrderId, VenueOrderId, AccountId, EventId, EventTime, CreatedTime, Reconciliation);

public sealed record OrderUpdated(
    ModuleHostId ModuleHostId, StrategyId StrategyId, MarketKey MarketKey, ClientOrderId ClientOrderId,
    VenueOrderId? VenueOrderId, AccountId? AccountId, Quantity Quantity, Price? Price, Price? TriggerPrice,
    Guid EventId, UnixNanos EventTime, UnixNanos CreatedTime, bool Reconciliation = false)
    : OrderEvent(ModuleHostId, StrategyId, MarketKey, ClientOrderId, VenueOrderId, AccountId, EventId, EventTime, CreatedTime, Reconciliation);

public sealed record OrderFilled(
    ModuleHostId ModuleHostId, StrategyId StrategyId, MarketKey MarketKey, ClientOrderId ClientOrderId,
    VenueOrderId? VenueOrderId, AccountId? AccountId, TradeId TradeId, PositionId? PositionId,
    OrderSide OrderSide, OrderType OrderType, Quantity LastQty, Price LastPx, Currency Currency,
    Money Commission, LiquiditySide LiquiditySide, Guid EventId, UnixNanos EventTime, UnixNanos CreatedTime,
    bool Reconciliation = false, string? Info = null)
    : OrderEvent(ModuleHostId, StrategyId, MarketKey, ClientOrderId, VenueOrderId, AccountId, EventId, EventTime, CreatedTime, Reconciliation)
{
    public bool IsBuy => OrderSide == OrderSide.Buy;

    public bool IsSell => OrderSide == OrderSide.Sell;
}

/// <summary>
/// Base type for position lifecycle events.
/// </summary>
public abstract record PositionEvent(
    ModuleHostId ModuleHostId,
    StrategyId StrategyId,
    MarketKey MarketKey,
    PositionId PositionId,
    AccountId? AccountId,
    ClientOrderId OpeningOrderId,
    ClientOrderId? ClosingOrderId,
    PositionSide EntrySide,
    PositionSide Side,
    Quantity SignedQuantity,
    Quantity Quantity,
    Quantity PeakQuantity,
    Quantity LastQty,
    Price LastPx,
    Currency Currency,
    decimal AvgPxOpen,
    decimal? AvgPxClose,
    Money RealizedPnl,
    Money UnrealizedPnl,
    UnixNanos TsOpened,
    UnixNanos? TsClosed,
    TimeSpan? Duration,
    Guid EventId,
    UnixNanos EventTime,
    UnixNanos CreatedTime) : Event(EventId, EventTime, CreatedTime);

public sealed record PositionOpened(
    ModuleHostId ModuleHostId, StrategyId StrategyId, MarketKey MarketKey, PositionId PositionId, AccountId? AccountId,
    ClientOrderId OpeningOrderId, PositionSide EntrySide, PositionSide Side, Quantity SignedQuantity, Quantity Quantity,
    Quantity PeakQuantity, Quantity LastQty, Price LastPx, Currency Currency, decimal AvgPxOpen, Money RealizedPnl,
    Money UnrealizedPnl, UnixNanos TsOpened, Guid EventId, UnixNanos EventTime, UnixNanos CreatedTime)
    : PositionEvent(ModuleHostId, StrategyId, MarketKey, PositionId, AccountId, OpeningOrderId, null, EntrySide, Side,
        SignedQuantity, Quantity, PeakQuantity, LastQty, LastPx, Currency, AvgPxOpen, null, RealizedPnl, UnrealizedPnl,
        TsOpened, null, null, EventId, EventTime, CreatedTime);

public sealed record PositionChanged(
    ModuleHostId ModuleHostId, StrategyId StrategyId, MarketKey MarketKey, PositionId PositionId, AccountId? AccountId,
    ClientOrderId OpeningOrderId, PositionSide EntrySide, PositionSide Side, Quantity SignedQuantity, Quantity Quantity,
    Quantity PeakQuantity, Quantity LastQty, Price LastPx, Currency Currency, decimal AvgPxOpen, decimal? AvgPxClose,
    Money RealizedPnl, Money UnrealizedPnl, UnixNanos TsOpened, Guid EventId, UnixNanos EventTime, UnixNanos CreatedTime)
    : PositionEvent(ModuleHostId, StrategyId, MarketKey, PositionId, AccountId, OpeningOrderId, null, EntrySide, Side,
        SignedQuantity, Quantity, PeakQuantity, LastQty, LastPx, Currency, AvgPxOpen, AvgPxClose, RealizedPnl, UnrealizedPnl,
        TsOpened, null, null, EventId, EventTime, CreatedTime);

public sealed record PositionClosed(
    ModuleHostId ModuleHostId, StrategyId StrategyId, MarketKey MarketKey, PositionId PositionId, AccountId? AccountId,
    ClientOrderId OpeningOrderId, ClientOrderId? ClosingOrderId, PositionSide EntrySide, Quantity PeakQuantity,
    Quantity LastQty, Price LastPx, Currency Currency, decimal AvgPxOpen, decimal? AvgPxClose, Money RealizedPnl,
    UnixNanos TsOpened, UnixNanos? TsClosed, TimeSpan? Duration, Guid EventId, UnixNanos EventTime, UnixNanos CreatedTime)
    : PositionEvent(ModuleHostId, StrategyId, MarketKey, PositionId, AccountId, OpeningOrderId, ClosingOrderId, EntrySide,
        PositionSide.Flat, Quantity.Zero(0), Quantity.Zero(0), PeakQuantity, LastQty, LastPx, Currency, AvgPxOpen, AvgPxClose,
        RealizedPnl, Money.Zero(Currency), TsOpened, TsClosed, Duration, EventId, EventTime, CreatedTime);

public sealed record AccountBalance(Money Total, Money Locked, Money Free)
{
    public Currency Currency => Total.Currency;

    public static AccountBalance Of(Money total, Money locked) => new(total, locked, total - locked);

    public static AccountBalance Unlocked(Money total) => new(total, Money.Zero(total.Currency), total);
}

public sealed record MarginBalance(Money Initial, Money Maintenance, MarketKey MarketKey)
{
    public Currency Currency => Initial.Currency;
}

public sealed record AccountState(
    AccountId AccountId,
    AccountType AccountType,
    Currency? BaseCurrency,
    bool Reported,
    IReadOnlyList<AccountBalance> Balances,
    IReadOnlyList<MarginBalance> Margins,
    IReadOnlyDictionary<string, string> Info,
    Guid EventId,
    UnixNanos EventTime,
    UnixNanos CreatedTime,
    IReadOnlyDictionary<MarketKey, decimal>? Leverages = null,
    decimal? DefaultLeverage = null) : Event(EventId, EventTime, CreatedTime);
