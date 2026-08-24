using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Primitives;

namespace Bytex.Core.Model.Reports;

/// <summary>
/// A venue's view of an order, used for reconciliation.
/// </summary>
public sealed record OrderStatusReport(
    AccountId AccountId,
    InstrumentId InstrumentId,
    ClientOrderId? ClientOrderId,
    VenueOrderId VenueOrderId,
    OrderSide OrderSide,
    OrderType OrderType,
    TimeInForce TimeInForce,
    OrderStatus OrderStatus,
    Quantity Quantity,
    Quantity FilledQuantity,
    UnixNanos TsAccepted,
    UnixNanos TsLast,
    UnixNanos TsInit,
    Guid ReportId,
    Price? Price = null,
    Price? TriggerPrice = null,
    TriggerType TriggerType = TriggerType.Default,
    decimal? TrailingOffset = null,
    TrailingOffsetType TrailingOffsetType = TrailingOffsetType.Price,
    Quantity? DisplayQuantity = null,
    decimal? AvgPx = null,
    bool PostOnly = false,
    bool ReduceOnly = false,
    UnixNanos? ExpireTime = null,
    OrderListId? OrderListId = null,
    ContingencyType Contingency = ContingencyType.None,
    string? CancelReason = null,
    UnixNanos? TsTriggered = null)
{
    public Quantity LeavesQuantity => new(Math.Max(0m, Quantity.Value - FilledQuantity.Value), Quantity.Precision);

    public bool IsOpen => OrderStatus is OrderStatus.Accepted or OrderStatus.Triggered or OrderStatus.PendingUpdate or OrderStatus.PendingCancel or OrderStatus.PartiallyFilled;
}

/// <summary>
/// A venue's record of a fill, used for reconciliation.
/// </summary>
public sealed record FillReport(
    AccountId AccountId,
    InstrumentId InstrumentId,
    VenueOrderId VenueOrderId,
    TradeId TradeId,
    OrderSide OrderSide,
    Quantity LastQty,
    Price LastPx,
    Money Commission,
    LiquiditySide LiquiditySide,
    UnixNanos TsEvent,
    UnixNanos TsInit,
    Guid ReportId,
    ClientOrderId? ClientOrderId = null,
    PositionId? VenuePositionId = null);

/// <summary>
/// A venue's view of a position, used for reconciliation.
/// </summary>
public sealed record PositionStatusReport(
    AccountId AccountId,
    InstrumentId InstrumentId,
    PositionSide PositionSide,
    Quantity Quantity,
    UnixNanos TsLast,
    UnixNanos TsInit,
    Guid ReportId,
    PositionId? VenuePositionId = null,
    decimal? AvgPxOpen = null)
{
    public decimal SignedQuantity => PositionSide == PositionSide.Short ? -Quantity.Value : Quantity.Value;
}

/// <summary>
/// Everything a venue reports for an account: open orders, fills, and positions.
/// </summary>
public sealed record ExecutionMassStatus(
    ClientId ClientId,
    AccountId AccountId,
    Venue Venue,
    IReadOnlyList<OrderStatusReport> OrderReports,
    IReadOnlyList<FillReport> FillReports,
    IReadOnlyList<PositionStatusReport> PositionReports,
    UnixNanos TsInit,
    Guid ReportId)
{
    public IReadOnlyList<FillReport> FillsForOrder(VenueOrderId venueOrderId) => FillReports.Where(f => f.VenueOrderId == venueOrderId).ToList();
}
