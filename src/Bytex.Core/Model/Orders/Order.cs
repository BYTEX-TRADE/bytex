using System.Globalization;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Primitives;

namespace Bytex.Core.Model.Orders;

/// <summary>
/// Thrown when an order event is applied in a state that does not permit it.
/// </summary>
public sealed class InvalidOrderTransitionException : InvalidOperationException
{
    public InvalidOrderTransitionException(ClientOrderId orderId, OrderStatus from, string eventName)
        : base($"Order {orderId}: cannot apply {eventName} in state {from}.")
    {
    }
}

/// <summary>
/// An order aggregate. State evolves only by applying <see cref="OrderEvent"/>s through a validated state machine.
/// </summary>
public abstract class Order
{
    private static readonly Dictionary<(OrderStatus From, Type Event), OrderStatus> Transitions = BuildTransitions();

    private readonly List<OrderEvent> _events = new();
    private readonly List<VenueOrderId> _venueOrderIds = new();
    private readonly List<TradeId> _tradeIds = new();
    private readonly Dictionary<Currency, Money> _commissions = new();
    private OrderStatus _previousStatus;

    protected Order(OrderInitialized init)
    {
        ArgumentNullException.ThrowIfNull(init);
        if (init.Quantity.IsZero)
        {
            throw new ArgumentException("Order quantity must be positive.", nameof(init));
        }

        // A market order refuses GTD outright and says so itself; every other type rests until its expiry, and GTD
        // without one is a contradiction. The check lives here because eight types each had to remember it and one did.
        if (init.TimeInForce == TimeInForce.Gtd && init.OrderType != OrderType.Market && OrderOptions.GetTime(init.Options, OptionKeys.ExpireTime) is null)
        {
            throw new ArgumentException("GTD orders require an expire time.", nameof(init));
        }

        TraderId = init.TraderId;
        StrategyId = init.StrategyId;
        InstrumentId = init.InstrumentId;
        ClientOrderId = init.ClientOrderId;
        Side = init.OrderSide;
        Type = init.OrderType;
        Quantity = init.Quantity;
        LeavesQuantity = init.Quantity;
        FilledQuantity = Quantity.Zero(init.Quantity.Precision);
        TimeInForce = init.TimeInForce;
        IsPostOnly = init.PostOnly;
        IsReduceOnly = init.ReduceOnly;
        IsQuoteQuantity = init.QuoteQuantity;
        EmulationTrigger = init.EmulationTrigger;
        TriggerInstrumentId = init.TriggerInstrumentId;
        Contingency = init.Contingency;
        OrderListId = init.OrderListId;
        LinkedOrderIds = init.LinkedOrderIds;
        ParentOrderId = init.ParentOrderId;
        ExecAlgorithmId = init.ExecAlgorithmId;
        ExecAlgorithmParams = init.ExecAlgorithmParams;
        ExecSpawnId = init.ExecSpawnId;
        Tags = init.Tags;
        Status = OrderStatus.Initialized;
        _previousStatus = OrderStatus.Initialized;
        InitId = init.EventId;
        TsInit = init.TsInit;
        TsLast = init.TsInit;
        _events.Add(init);
    }

    public TraderId TraderId { get; }

    public StrategyId StrategyId { get; }

    public InstrumentId InstrumentId { get; }

    public ClientOrderId ClientOrderId { get; }

    public VenueOrderId? VenueOrderId { get; private set; }

    public PositionId? PositionId { get; private set; }

    public AccountId? AccountId { get; private set; }

    public TradeId? LastTradeId { get; private set; }

    public OrderSide Side { get; }

    public OrderType Type { get; }

    public Quantity Quantity { get; private set; }

    public Quantity FilledQuantity { get; private set; }

    public Quantity LeavesQuantity { get; private set; }

    public TimeInForce TimeInForce { get; }

    public OrderStatus Status { get; private set; }

    public decimal? AvgPx { get; private set; }

    /// <summary>Difference between average fill price and the order's reference price, positive when adverse.</summary>
    public decimal Slippage { get; private set; }

    public bool IsPostOnly { get; }

    public bool IsReduceOnly { get; }

    public bool IsQuoteQuantity { get; }

    public TriggerType EmulationTrigger { get; }

    public InstrumentId? TriggerInstrumentId { get; }

    public ContingencyType Contingency { get; }

    public OrderListId? OrderListId { get; }

    public IReadOnlyList<ClientOrderId> LinkedOrderIds { get; internal set; }

    public ClientOrderId? ParentOrderId { get; }

    public ExecAlgorithmId? ExecAlgorithmId { get; }

    public IReadOnlyDictionary<string, string>? ExecAlgorithmParams { get; }

    public ClientOrderId? ExecSpawnId { get; }

    public IReadOnlyList<string> Tags { get; }

    public Guid InitId { get; }

    public UnixNanos TsInit { get; }

    public UnixNanos TsLast { get; private set; }

    public UnixNanos? TsSubmitted { get; private set; }

    public UnixNanos? TsAccepted { get; private set; }

    public UnixNanos? TsClosed { get; private set; }

    public IReadOnlyList<OrderEvent> Events => _events;

    public OrderEvent LastEvent => _events[^1];

    public int EventCount => _events.Count;

    public IReadOnlyList<VenueOrderId> VenueOrderIds => _venueOrderIds;

    public IReadOnlyList<TradeId> TradeIds => _tradeIds;

    public IReadOnlyDictionary<Currency, Money> Commissions => _commissions;

    public OrderInitialized InitEvent => (OrderInitialized)_events[0];

    // Price-related members are populated by subclasses where applicable.
    public Price? Price { get; protected set; }

    public Price? TriggerPrice { get; protected set; }

    public TriggerType TriggerType { get; protected set; } = TriggerType.Default;

    public UnixNanos? ExpireTime { get; protected set; }

    public Quantity? DisplayQuantity { get; protected set; }

    public bool HasPrice => Price is not null;

    public bool HasTriggerPrice => TriggerPrice is not null;

    public bool IsBuy => Side == OrderSide.Buy;

    public bool IsSell => Side == OrderSide.Sell;

    public bool IsPassive => Type != OrderType.Market;

    public bool IsAggressive => Type == OrderType.Market;

    public bool IsEmulated => Status == OrderStatus.Emulated;

    public bool IsActiveLocal => Status is OrderStatus.Initialized or OrderStatus.Emulated or OrderStatus.Released;

    public bool IsInflight => Status is OrderStatus.Submitted or OrderStatus.PendingUpdate or OrderStatus.PendingCancel;

    public bool IsOpen => Status is OrderStatus.Accepted or OrderStatus.Triggered or OrderStatus.PendingUpdate or OrderStatus.PendingCancel or OrderStatus.PartiallyFilled;

    public bool IsClosed => Status is OrderStatus.Denied or OrderStatus.Rejected or OrderStatus.Canceled or OrderStatus.Expired or OrderStatus.Filled;

    public bool IsPending => Status is OrderStatus.PendingUpdate or OrderStatus.PendingCancel;

    public bool IsContingency => Contingency != ContingencyType.None;

    public bool IsParentOrder => Contingency == ContingencyType.Oto;

    public bool IsChildOrder => ParentOrderId is not null;

    public bool IsSpawned => ExecSpawnId is not null;

    public bool WouldReduceOnly(PositionSide positionSide, Quantity positionQuantity)
    {
        if (positionSide == PositionSide.Flat)
        {
            return false;
        }

        bool opposite = (positionSide == PositionSide.Long && IsSell) || (positionSide == PositionSide.Short && IsBuy);
        return opposite && LeavesQuantity <= positionQuantity;
    }

    /// <summary>
    /// Applies an event, validating the transition and updating state.
    /// </summary>
    public void Apply(OrderEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.ClientOrderId != ClientOrderId)
        {
            throw new ArgumentException($"Event client order id {e.ClientOrderId} does not match order {ClientOrderId}.", nameof(e));
        }

        if (e is OrderInitialized)
        {
            throw new InvalidOrderTransitionException(ClientOrderId, Status, nameof(OrderInitialized));
        }

        if (e is OrderFilled fill && _tradeIds.Contains(fill.TradeId))
        {
            // Duplicate fill, ignore.
            return;
        }

        switch (e)
        {
            case OrderUpdated updated:
                ApplyUpdated(updated);
                break;
            case OrderModifyRejected:
            case OrderCancelRejected:
                if (IsPending)
                {
                    Status = _previousStatus;
                }

                break;
            default:
                Status = NextStatus(e);
                break;
        }

        if (e.VenueOrderId is { } venueOrderId && (VenueOrderId is null || VenueOrderId.Value != venueOrderId))
        {
            VenueOrderId = venueOrderId;
            _venueOrderIds.Add(venueOrderId);
        }

        if (e.AccountId is { } accountId)
        {
            AccountId = accountId;
        }

        switch (e)
        {
            case OrderSubmitted:
                TsSubmitted = e.TsEvent;
                break;
            case OrderAccepted:
                TsAccepted ??= e.TsEvent;
                break;
            case OrderFilled f:
                ApplyFilled(f);
                break;
        }

        if (IsClosed)
        {
            TsClosed = e.TsEvent;
        }

        TsLast = e.TsEvent;
        _events.Add(e);
    }

    private OrderStatus NextStatus(OrderEvent e)
    {
        Type eventType = e.GetType();
        if (eventType == typeof(OrderFilled))
        {
            OrderFilled fill = (OrderFilled)e;
            bool complete = FilledQuantity.Value + fill.LastQty.Value >= Quantity.Value;
            eventType = complete ? typeof(FilledMarker) : typeof(PartialFillMarker);
        }

        if (!Transitions.TryGetValue((Status, eventType), out OrderStatus next))
        {
            throw new InvalidOrderTransitionException(ClientOrderId, Status, e.GetType().Name);
        }

        if (next is OrderStatus.PendingUpdate or OrderStatus.PendingCancel)
        {
            _previousStatus = Status is OrderStatus.PendingUpdate or OrderStatus.PendingCancel ? _previousStatus : Status;
        }

        return next;
    }

    private void ApplyUpdated(OrderUpdated updated)
    {
        if (IsPending)
        {
            Status = _previousStatus;
        }

        if (updated.Quantity.Value > 0m && updated.Quantity != Quantity)
        {
            Quantity = updated.Quantity;
            LeavesQuantity = new Quantity(Math.Max(0m, Quantity.Value - FilledQuantity.Value), Quantity.Precision);
        }

        OnUpdated(updated);
    }

    private void ApplyFilled(OrderFilled fill)
    {
        PositionId ??= fill.PositionId;
        LastTradeId = fill.TradeId;
        _tradeIds.Add(fill.TradeId);

        decimal previousFilled = FilledQuantity.Value;
        decimal newFilled = previousFilled + fill.LastQty.Value;
        decimal previousAvg = AvgPx ?? 0m;
        AvgPx = newFilled == 0m ? null : ((previousAvg * previousFilled) + (fill.LastPx.Value * fill.LastQty.Value)) / newFilled;

        FilledQuantity = new Quantity(newFilled, Quantity.Precision);
        LeavesQuantity = new Quantity(Math.Max(0m, Quantity.Value - newFilled), Quantity.Precision);

        if (_commissions.TryGetValue(fill.Commission.Currency, out Money existing))
        {
            _commissions[fill.Commission.Currency] = existing + fill.Commission;
        }
        else
        {
            _commissions[fill.Commission.Currency] = fill.Commission;
        }

        Price? reference = Price ?? TriggerPrice;
        if (reference is { } refPx && AvgPx is { } avg)
        {
            Slippage = IsBuy ? avg - refPx.Value : refPx.Value - avg;
        }
    }

    /// <summary>
    /// Lets subclasses apply price/trigger changes from an update event.
    /// </summary>
    protected virtual void OnUpdated(OrderUpdated updated)
    {
    }

    /// <summary>
    /// Sets the position identifier when the order is first associated with a position.
    /// </summary>
    internal void AssignPosition(PositionId positionId) => PositionId ??= positionId;

    internal void SetPosition(PositionId positionId) => PositionId = positionId;

    /// <summary>
    /// Human-readable summary of the order's defining parameters.
    /// </summary>
    public virtual string Info()
    {
        string tif = TimeInForce == TimeInForce.Gtd && ExpireTime is { } exp ? $"GTD {exp}" : TimeInForce.ToString().ToUpperInvariant();
        return $"{Side.ToString().ToUpperInvariant()} {Quantity} {InstrumentId} {Type.ToString().ToUpperInvariant()} {tif}";
    }

    public override string ToString() =>
        $"{GetType().Name}({Info()}, status={Status}, client_order_id={ClientOrderId}, venue_order_id={VenueOrderId?.Value ?? "None"}, position_id={PositionId?.Value ?? "None"}, tags=[{string.Join(',', Tags)}])";

    private sealed class FilledMarker;

    private sealed class PartialFillMarker;

    private static Dictionary<(OrderStatus, Type), OrderStatus> BuildTransitions()
    {
        Dictionary<(OrderStatus, Type), OrderStatus> t = new();

        void Add(OrderStatus from, Type e, OrderStatus to) => t[(from, e)] = to;

        Type denied = typeof(OrderDenied), emulated = typeof(OrderEmulated), released = typeof(OrderReleased),
            submitted = typeof(OrderSubmitted), accepted = typeof(OrderAccepted), rejected = typeof(OrderRejected),
            canceled = typeof(OrderCanceled), expired = typeof(OrderExpired), triggered = typeof(OrderTriggered),
            pendingUpdate = typeof(OrderPendingUpdate), pendingCancel = typeof(OrderPendingCancel),
            partial = typeof(PartialFillMarker), filled = typeof(FilledMarker);

        Add(OrderStatus.Initialized, denied, OrderStatus.Denied);
        Add(OrderStatus.Initialized, emulated, OrderStatus.Emulated);
        Add(OrderStatus.Initialized, released, OrderStatus.Released);
        Add(OrderStatus.Initialized, submitted, OrderStatus.Submitted);
        Add(OrderStatus.Initialized, rejected, OrderStatus.Rejected);
        Add(OrderStatus.Initialized, accepted, OrderStatus.Accepted);
        Add(OrderStatus.Initialized, canceled, OrderStatus.Canceled);
        Add(OrderStatus.Initialized, expired, OrderStatus.Expired);
        Add(OrderStatus.Initialized, triggered, OrderStatus.Triggered);
        Add(OrderStatus.Initialized, partial, OrderStatus.PartiallyFilled);
        Add(OrderStatus.Initialized, filled, OrderStatus.Filled);

        Add(OrderStatus.Emulated, canceled, OrderStatus.Canceled);
        Add(OrderStatus.Emulated, expired, OrderStatus.Expired);
        Add(OrderStatus.Emulated, released, OrderStatus.Released);

        Add(OrderStatus.Released, denied, OrderStatus.Denied);
        Add(OrderStatus.Released, submitted, OrderStatus.Submitted);
        Add(OrderStatus.Released, canceled, OrderStatus.Canceled);

        Add(OrderStatus.Submitted, pendingUpdate, OrderStatus.PendingUpdate);
        Add(OrderStatus.Submitted, pendingCancel, OrderStatus.PendingCancel);
        Add(OrderStatus.Submitted, rejected, OrderStatus.Rejected);
        Add(OrderStatus.Submitted, canceled, OrderStatus.Canceled);
        Add(OrderStatus.Submitted, accepted, OrderStatus.Accepted);
        Add(OrderStatus.Submitted, expired, OrderStatus.Expired);
        Add(OrderStatus.Submitted, triggered, OrderStatus.Triggered);
        Add(OrderStatus.Submitted, partial, OrderStatus.PartiallyFilled);
        Add(OrderStatus.Submitted, filled, OrderStatus.Filled);

        Add(OrderStatus.Accepted, rejected, OrderStatus.Rejected);
        Add(OrderStatus.Accepted, pendingUpdate, OrderStatus.PendingUpdate);
        Add(OrderStatus.Accepted, pendingCancel, OrderStatus.PendingCancel);
        Add(OrderStatus.Accepted, canceled, OrderStatus.Canceled);
        Add(OrderStatus.Accepted, triggered, OrderStatus.Triggered);
        Add(OrderStatus.Accepted, expired, OrderStatus.Expired);
        Add(OrderStatus.Accepted, partial, OrderStatus.PartiallyFilled);
        Add(OrderStatus.Accepted, filled, OrderStatus.Filled);

        Add(OrderStatus.Canceled, partial, OrderStatus.PartiallyFilled);
        Add(OrderStatus.Canceled, filled, OrderStatus.Filled);

        Add(OrderStatus.PendingUpdate, rejected, OrderStatus.Rejected);
        Add(OrderStatus.PendingUpdate, accepted, OrderStatus.Accepted);
        Add(OrderStatus.PendingUpdate, canceled, OrderStatus.Canceled);
        Add(OrderStatus.PendingUpdate, expired, OrderStatus.Expired);
        Add(OrderStatus.PendingUpdate, triggered, OrderStatus.Triggered);
        Add(OrderStatus.PendingUpdate, pendingUpdate, OrderStatus.PendingUpdate);
        Add(OrderStatus.PendingUpdate, pendingCancel, OrderStatus.PendingCancel);
        Add(OrderStatus.PendingUpdate, partial, OrderStatus.PartiallyFilled);
        Add(OrderStatus.PendingUpdate, filled, OrderStatus.Filled);

        Add(OrderStatus.PendingCancel, rejected, OrderStatus.Rejected);
        Add(OrderStatus.PendingCancel, pendingCancel, OrderStatus.PendingCancel);
        Add(OrderStatus.PendingCancel, canceled, OrderStatus.Canceled);
        Add(OrderStatus.PendingCancel, accepted, OrderStatus.Accepted);
        Add(OrderStatus.PendingCancel, expired, OrderStatus.Expired);
        Add(OrderStatus.PendingCancel, partial, OrderStatus.PartiallyFilled);
        Add(OrderStatus.PendingCancel, filled, OrderStatus.Filled);

        Add(OrderStatus.Triggered, rejected, OrderStatus.Rejected);
        Add(OrderStatus.Triggered, pendingUpdate, OrderStatus.PendingUpdate);
        Add(OrderStatus.Triggered, pendingCancel, OrderStatus.PendingCancel);
        Add(OrderStatus.Triggered, canceled, OrderStatus.Canceled);
        Add(OrderStatus.Triggered, expired, OrderStatus.Expired);
        Add(OrderStatus.Triggered, partial, OrderStatus.PartiallyFilled);
        Add(OrderStatus.Triggered, filled, OrderStatus.Filled);

        Add(OrderStatus.PartiallyFilled, pendingUpdate, OrderStatus.PendingUpdate);
        Add(OrderStatus.PartiallyFilled, pendingCancel, OrderStatus.PendingCancel);
        Add(OrderStatus.PartiallyFilled, canceled, OrderStatus.Canceled);
        Add(OrderStatus.PartiallyFilled, expired, OrderStatus.Expired);
        Add(OrderStatus.PartiallyFilled, partial, OrderStatus.PartiallyFilled);
        Add(OrderStatus.PartiallyFilled, filled, OrderStatus.Filled);

        return t;
    }

    /// <summary>
    /// Helpers for packing typed order parameters into the string-keyed options of an <see cref="OrderInitialized"/> event.
    /// </summary>
    internal static class OptionKeys
    {
        public const string Price = "price";
        public const string TriggerPrice = "trigger_price";
        public const string TriggerType = "trigger_type";
        public const string ExpireTime = "expire_time";
        public const string DisplayQuantity = "display_qty";
        public const string TrailingOffset = "trailing_offset";
        public const string TrailingOffsetType = "trailing_offset_type";
        public const string LimitOffset = "limit_offset";
        public const string ActivationPrice = "activation_price";

        public static string Format(decimal value) => value.ToString(CultureInfo.InvariantCulture);

        public static decimal ParseDecimal(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);
    }
}
