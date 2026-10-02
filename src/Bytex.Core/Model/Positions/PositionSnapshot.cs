using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Primitives;

namespace Bytex.Core.Model.Positions;

/// <summary>
/// What a position looked like at one moment (R6.6).
///
/// <para>
/// <b>What this is for, and what it is not.</b> A position's fills are kept, so its realised figures can always be
/// worked out again from them. What cannot be worked out again is what it was worth while it was still open: that
/// needs the price at that moment, and a price that has moved on is gone. So a snapshot carries the mark it was
/// measured against and the unrealised profit at it, and a report of how a position stood through its life is made of
/// snapshots rather than of arithmetic after the fact.
/// </para>
///
/// <para>
/// It is not needed to keep a position's history. A netting position that flips is given a NEW id here and the old one
/// keeps its own record, which is the reason snapshots exist in engines that overwrite it instead.
/// </para>
/// </summary>
/// <param name="TsSnapshot">When the snapshot was taken.</param>
/// <param name="PositionId">The position.</param>
/// <param name="MarketKey">Its instrument.</param>
/// <param name="StrategyId">Whose it is.</param>
/// <param name="Side">Long, short, or flat for one that had been closed by then.</param>
/// <param name="Quantity">How much was held.</param>
/// <param name="SignedQuantity">The same, negative for a short, so a reader does not have to combine two fields.</param>
/// <param name="AvgPxOpen">The average price it was opened at.</param>
/// <param name="AvgPxClose">The average price it was closed at, where it had been.</param>
/// <param name="MarkPrice">The price the unrealised profit was measured against, or null where none was given.</param>
/// <param name="RealizedPnl">What it had made or lost for certain.</param>
/// <param name="UnrealizedPnl">What it was up or down on top of that at the mark, or null without one.</param>
/// <param name="Commissions">What it had paid, by currency.</param>
/// <param name="TsOpened">When it was opened.</param>
/// <param name="TsClosed">When it was closed, where it had been.</param>
public sealed record PositionSnapshot(
    UnixNanos TsSnapshot,
    PositionId PositionId,
    MarketKey MarketKey,
    StrategyId StrategyId,
    PositionSide Side,
    Quantity Quantity,
    decimal SignedQuantity,
    decimal AvgPxOpen,
    decimal? AvgPxClose,
    Price? MarkPrice,
    Money RealizedPnl,
    Money? UnrealizedPnl,
    IReadOnlyDictionary<Currency, Money> Commissions,
    UnixNanos TsOpened,
    UnixNanos? TsClosed)
{
    /// <summary>
    /// The snapshot of a position as it stands, measured against <paramref name="markPrice"/> where one is given.
    ///
    /// <para>
    /// A closed position needs no mark - there is nothing left to be up or down on - so the unrealised figure is left
    /// out rather than reported as zero, which would read as "measured, and flat".
    /// </para>
    /// </summary>
    public static PositionSnapshot Of(Position position, UnixNanos ts, Price? markPrice = null)
    {
        ArgumentNullException.ThrowIfNull(position);
        Money? unrealized = markPrice is { } mark && position.IsOpen ? position.UnrealizedPnl(mark) : null;

        return new PositionSnapshot(
            ts,
            position.Id,
            position.MarketKey,
            position.StrategyId,
            position.Side,
            position.Quantity,
            position.SignedQuantity,
            position.AvgPxOpen,
            position.AvgPxClose,
            position.IsOpen ? markPrice : null,
            position.RealizedPnl,
            unrealized,
            new Dictionary<Currency, Money>(position.Commissions),
            position.TsOpened,
            position.TsClosed);
    }

    /// <summary>Realised and unrealised together, which is what the position was worth at that moment.</summary>
    public Money TotalPnl => UnrealizedPnl is { } unrealized ? RealizedPnl + unrealized : RealizedPnl;
}
