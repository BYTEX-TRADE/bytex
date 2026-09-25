using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Primitives;

namespace Bytex.Core.Model.Events;

/// <summary>
/// Base type for everything the engine publishes as an event.
/// </summary>
public abstract record Event(Guid EventId, UnixNanos TsEvent, UnixNanos TsInit);

public sealed record ComponentStateChanged(
    ComponentId ComponentId,
    ComponentState PreviousState,
    ComponentState State,
    Guid EventId,
    UnixNanos TsEvent,
    UnixNanos TsInit) : Event(EventId, TsEvent, TsInit);

public sealed record TimeEvent(string Name, Guid EventId, UnixNanos TsEvent, UnixNanos TsInit) : Event(EventId, TsEvent, TsInit);

/// <summary>
/// Base type for all order lifecycle events.
/// </summary>
public abstract record OrderEvent(
    TraderId TraderId,
    StrategyId StrategyId,
    InstrumentId InstrumentId,
    ClientOrderId ClientOrderId,
    VenueOrderId? VenueOrderId,
    AccountId? AccountId,
    Guid EventId,
    UnixNanos TsEvent,
    UnixNanos TsInit,
    bool Reconciliation = false) : Event(EventId, TsEvent, TsInit);

public sealed record OrderInitialized(
    TraderId TraderId,
    StrategyId StrategyId,
    InstrumentId InstrumentId,
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
    InstrumentId? TriggerInstrumentId,
    ContingencyType Contingency,
    OrderListId? OrderListId,
    IReadOnlyList<ClientOrderId> LinkedOrderIds,
    ClientOrderId? ParentOrderId,
    ExecAlgorithmId? ExecAlgorithmId,
    IReadOnlyDictionary<string, string>? ExecAlgorithmParams,
    ClientOrderId? ExecSpawnId,
    IReadOnlyList<string> Tags,
    Guid EventId,
    UnixNanos TsEvent,
    UnixNanos TsInit,
    bool Reconciliation = false)
    : OrderEvent(TraderId, StrategyId, InstrumentId, ClientOrderId, null, null, EventId, TsEvent, TsInit, Reconciliation);

public sealed record OrderDenied(
    TraderId TraderId, StrategyId StrategyId, InstrumentId InstrumentId, ClientOrderId ClientOrderId,
    string Reason, Guid EventId, UnixNanos TsEvent, UnixNanos TsInit)
    : OrderEvent(TraderId, StrategyId, InstrumentId, ClientOrderId, null, null, EventId, TsEvent, TsInit);

public sealed record OrderEmulated(
    TraderId TraderId, StrategyId StrategyId, InstrumentId InstrumentId, ClientOrderId ClientOrderId,
    Guid EventId, UnixNanos TsEvent, UnixNanos TsInit)
    : OrderEvent(TraderId, StrategyId, InstrumentId, ClientOrderId, null, null, EventId, TsEvent, TsInit);

public sealed record OrderReleased(
    TraderId TraderId, StrategyId StrategyId, InstrumentId InstrumentId, ClientOrderId ClientOrderId,
    Price ReleasedPrice, Guid EventId, UnixNanos TsEvent, UnixNanos TsInit)
    : OrderEvent(TraderId, StrategyId, InstrumentId, ClientOrderId, null, null, EventId, TsEvent, TsInit);

public sealed record OrderSubmitted(
    TraderId TraderId, StrategyId StrategyId, InstrumentId InstrumentId, ClientOrderId ClientOrderId,
    AccountId? AccountId, Guid EventId, UnixNanos TsEvent, UnixNanos TsInit)
    : OrderEvent(TraderId, StrategyId, InstrumentId, ClientOrderId, null, AccountId, EventId, TsEvent, TsInit);

public sealed record OrderAccepted(
    TraderId TraderId, StrategyId StrategyId, InstrumentId InstrumentId, ClientOrderId ClientOrderId,
    VenueOrderId? VenueOrderId, AccountId? AccountId, Guid EventId, UnixNanos TsEvent, UnixNanos TsInit, bool Reconciliation = false)
    : OrderEvent(TraderId, StrategyId, InstrumentId, ClientOrderId, VenueOrderId, AccountId, EventId, TsEvent, TsInit, Reconciliation);

public sealed record OrderRejected(
    TraderId TraderId, StrategyId StrategyId, InstrumentId InstrumentId, ClientOrderId ClientOrderId,
    AccountId? AccountId, string Reason, Guid EventId, UnixNanos TsEvent, UnixNanos TsInit, bool Reconciliation = false)
    : OrderEvent(TraderId, StrategyId, InstrumentId, ClientOrderId, null, AccountId, EventId, TsEvent, TsInit, Reconciliation);

public sealed record OrderCanceled(
    TraderId TraderId, StrategyId StrategyId, InstrumentId InstrumentId, ClientOrderId ClientOrderId,
    VenueOrderId? VenueOrderId, AccountId? AccountId, Guid EventId, UnixNanos TsEvent, UnixNanos TsInit, bool Reconciliation = false)
    : OrderEvent(TraderId, StrategyId, InstrumentId, ClientOrderId, VenueOrderId, AccountId, EventId, TsEvent, TsInit, Reconciliation);

public sealed record OrderExpired(
    TraderId TraderId, StrategyId StrategyId, InstrumentId InstrumentId, ClientOrderId ClientOrderId,
    VenueOrderId? VenueOrderId, AccountId? AccountId, Guid EventId, UnixNanos TsEvent, UnixNanos TsInit, bool Reconciliation = false)
    : OrderEvent(TraderId, StrategyId, InstrumentId, ClientOrderId, VenueOrderId, AccountId, EventId, TsEvent, TsInit, Reconciliation);

public sealed record OrderTriggered(
    TraderId TraderId, StrategyId StrategyId, InstrumentId InstrumentId, ClientOrderId ClientOrderId,
    VenueOrderId? VenueOrderId, AccountId? AccountId, Guid EventId, UnixNanos TsEvent, UnixNanos TsInit, bool Reconciliation = false)
    : OrderEvent(TraderId, StrategyId, InstrumentId, ClientOrderId, VenueOrderId, AccountId, EventId, TsEvent, TsInit, Reconciliation);

public sealed record OrderPendingUpdate(
    TraderId TraderId, StrategyId StrategyId, InstrumentId InstrumentId, ClientOrderId ClientOrderId,
    VenueOrderId? VenueOrderId, AccountId? AccountId, Guid EventId, UnixNanos TsEvent, UnixNanos TsInit, bool Reconciliation = false)
    : OrderEvent(TraderId, StrategyId, InstrumentId, ClientOrderId, VenueOrderId, AccountId, EventId, TsEvent, TsInit, Reconciliation);

public sealed record OrderPendingCancel(
    TraderId TraderId, StrategyId StrategyId, InstrumentId InstrumentId, ClientOrderId ClientOrderId,
    VenueOrderId? VenueOrderId, AccountId? AccountId, Guid EventId, UnixNanos TsEvent, UnixNanos TsInit, bool Reconciliation = false)
    : OrderEvent(TraderId, StrategyId, InstrumentId, ClientOrderId, VenueOrderId, AccountId, EventId, TsEvent, TsInit, Reconciliation);

public sealed record OrderModifyRejected(
    TraderId TraderId, StrategyId StrategyId, InstrumentId InstrumentId, ClientOrderId ClientOrderId,
    VenueOrderId? VenueOrderId, AccountId? AccountId, string Reason, Guid EventId, UnixNanos TsEvent, UnixNanos TsInit, bool Reconciliation = false)
    : OrderEvent(TraderId, StrategyId, InstrumentId, ClientOrderId, VenueOrderId, AccountId, EventId, TsEvent, TsInit, Reconciliation);

public sealed record OrderCancelRejected(
    TraderId TraderId, StrategyId StrategyId, InstrumentId InstrumentId, ClientOrderId ClientOrderId,
    VenueOrderId? VenueOrderId, AccountId? AccountId, string Reason, Guid EventId, UnixNanos TsEvent, UnixNanos TsInit, bool Reconciliation = false)
    : OrderEvent(TraderId, StrategyId, InstrumentId, ClientOrderId, VenueOrderId, AccountId, EventId, TsEvent, TsInit, Reconciliation);

public sealed record OrderUpdated(
    TraderId TraderId, StrategyId StrategyId, InstrumentId InstrumentId, ClientOrderId ClientOrderId,
    VenueOrderId? VenueOrderId, AccountId? AccountId, Quantity Quantity, Price? Price, Price? TriggerPrice,
    Guid EventId, UnixNanos TsEvent, UnixNanos TsInit, bool Reconciliation = false)
    : OrderEvent(TraderId, StrategyId, InstrumentId, ClientOrderId, VenueOrderId, AccountId, EventId, TsEvent, TsInit, Reconciliation);

public sealed record OrderFilled(
    TraderId TraderId, StrategyId StrategyId, InstrumentId InstrumentId, ClientOrderId ClientOrderId,
    VenueOrderId? VenueOrderId, AccountId? AccountId, TradeId TradeId, PositionId? PositionId,
    OrderSide OrderSide, OrderType OrderType, Quantity LastQty, Price LastPx, Currency Currency,
    Money Commission, LiquiditySide LiquiditySide, Guid EventId, UnixNanos TsEvent, UnixNanos TsInit,
    bool Reconciliation = false, string? Info = null)
    : OrderEvent(TraderId, StrategyId, InstrumentId, ClientOrderId, VenueOrderId, AccountId, EventId, TsEvent, TsInit, Reconciliation)
{
    public bool IsBuy => OrderSide == OrderSide.Buy;

    public bool IsSell => OrderSide == OrderSide.Sell;
}

/// <summary>
/// Base type for position lifecycle events.
/// </summary>
public abstract record PositionEvent(
    TraderId TraderId,
    StrategyId StrategyId,
    InstrumentId InstrumentId,
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
    UnixNanos TsEvent,
    UnixNanos TsInit) : Event(EventId, TsEvent, TsInit);

public sealed record PositionOpened(
    TraderId TraderId, StrategyId StrategyId, InstrumentId InstrumentId, PositionId PositionId, AccountId? AccountId,
    ClientOrderId OpeningOrderId, PositionSide EntrySide, PositionSide Side, Quantity SignedQuantity, Quantity Quantity,
    Quantity PeakQuantity, Quantity LastQty, Price LastPx, Currency Currency, decimal AvgPxOpen, Money RealizedPnl,
    Money UnrealizedPnl, UnixNanos TsOpened, Guid EventId, UnixNanos TsEvent, UnixNanos TsInit)
    : PositionEvent(TraderId, StrategyId, InstrumentId, PositionId, AccountId, OpeningOrderId, null, EntrySide, Side,
        SignedQuantity, Quantity, PeakQuantity, LastQty, LastPx, Currency, AvgPxOpen, null, RealizedPnl, UnrealizedPnl,
        TsOpened, null, null, EventId, TsEvent, TsInit);

public sealed record PositionChanged(
    TraderId TraderId, StrategyId StrategyId, InstrumentId InstrumentId, PositionId PositionId, AccountId? AccountId,
    ClientOrderId OpeningOrderId, PositionSide EntrySide, PositionSide Side, Quantity SignedQuantity, Quantity Quantity,
    Quantity PeakQuantity, Quantity LastQty, Price LastPx, Currency Currency, decimal AvgPxOpen, decimal? AvgPxClose,
    Money RealizedPnl, Money UnrealizedPnl, UnixNanos TsOpened, Guid EventId, UnixNanos TsEvent, UnixNanos TsInit)
    : PositionEvent(TraderId, StrategyId, InstrumentId, PositionId, AccountId, OpeningOrderId, null, EntrySide, Side,
        SignedQuantity, Quantity, PeakQuantity, LastQty, LastPx, Currency, AvgPxOpen, AvgPxClose, RealizedPnl, UnrealizedPnl,
        TsOpened, null, null, EventId, TsEvent, TsInit);

public sealed record PositionClosed(
    TraderId TraderId, StrategyId StrategyId, InstrumentId InstrumentId, PositionId PositionId, AccountId? AccountId,
    ClientOrderId OpeningOrderId, ClientOrderId? ClosingOrderId, PositionSide EntrySide, Quantity PeakQuantity,
    Quantity LastQty, Price LastPx, Currency Currency, decimal AvgPxOpen, decimal? AvgPxClose, Money RealizedPnl,
    UnixNanos TsOpened, UnixNanos? TsClosed, TimeSpan? Duration, Guid EventId, UnixNanos TsEvent, UnixNanos TsInit)
    : PositionEvent(TraderId, StrategyId, InstrumentId, PositionId, AccountId, OpeningOrderId, ClosingOrderId, EntrySide,
        PositionSide.Flat, Quantity.Zero(0), Quantity.Zero(0), PeakQuantity, LastQty, LastPx, Currency, AvgPxOpen, AvgPxClose,
        RealizedPnl, Money.Zero(Currency), TsOpened, TsClosed, Duration, EventId, TsEvent, TsInit);

public sealed record AccountBalance(Money Total, Money Locked, Money Free)
{
    public Currency Currency => Total.Currency;

    public static AccountBalance Of(Money total, Money locked) => new(total, locked, total - locked);

    public static AccountBalance Unlocked(Money total) => new(total, Money.Zero(total.Currency), total);
}

public sealed record MarginBalance(Money Initial, Money Maintenance, InstrumentId InstrumentId)
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
    UnixNanos TsEvent,
    UnixNanos TsInit,
    IReadOnlyDictionary<InstrumentId, decimal>? Leverages = null,
    decimal? DefaultLeverage = null) : Event(EventId, TsEvent, TsInit);
