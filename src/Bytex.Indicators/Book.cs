using Bytex.Core.Indicators;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;

namespace Bytex.Indicators;

/// <summary>
/// How lopsided the book is: the resting size on the bid against the size on the ask, over the top few levels (R7.8).
///
/// <para>
/// It answers between -1 and 1, which is a share rather than a quantity on purpose: the raw difference between two
/// sides is not comparable between one instrument and another, or between a quiet hour and a busy one, and a strategy
/// wanting a threshold needs a number that means the same thing in both.
/// </para>
///
/// <para>
/// What it is not: a prediction. Resting size is what somebody has offered to do and may withdraw, and the side with
/// more of it is not the side that wins. It is a measurement of the book as it stands.
/// </para>
/// </summary>
public sealed class BookImbalance : Indicator, IOrderBookIndicator
{
    public const int DefaultLevels = 5;

    private readonly int _levels;

    public BookImbalance(int levels = DefaultLevels)
        : base($"BOOKIMB({levels})")
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(levels, 1);
        _levels = levels;
    }

    /// <summary>How many levels of each side are counted.</summary>
    public int Levels => _levels;

    /// <summary>From -1 (only asks) through 0 (level) to 1 (only bids).</summary>
    public decimal Value { get; private set; }

    public decimal BidSize { get; private set; }

    public decimal AskSize { get; private set; }

    public void Update(OrderBook book)
    {
        ArgumentNullException.ThrowIfNull(book);
        BidSize = book.Bids(_levels).Sum(level => level.Size.Value);
        AskSize = book.Asks(_levels).Sum(level => level.Size.Value);
        HasInputs = true;

        decimal total = BidSize + AskSize;

        // An empty book is not level: it is nothing to measure, and a zero would read as "balanced". So the value stays
        // where it was and the indicator says it is not ready.
        if (total == 0m)
        {
            IsInitialized = false;
            return;
        }

        Value = (BidSize - AskSize) / total;
        IsInitialized = true;
    }

    /// <summary>
    /// A quote is the top of a book, so a quote's sizes are the imbalance of a one-level book. Useful where a venue
    /// gives quotes and no depth, and honest about what it measured: one level, however many were asked for.
    /// </summary>
    public override void Update(QuoteTick tick)
    {
        BidSize = tick.BidSize.Value;
        AskSize = tick.AskSize.Value;
        HasInputs = true;

        decimal total = BidSize + AskSize;
        if (total == 0m)
        {
            IsInitialized = false;
            return;
        }

        Value = (BidSize - AskSize) / total;
        IsInitialized = true;
    }

    public override void UpdateRaw(decimal value) =>
        throw new NotSupportedException("A book imbalance is two sides of a book, so a single value cannot feed it.");

    public override void Update(Bar bar) =>
        throw new NotSupportedException("A bar carries no resting size, so it cannot feed a book imbalance.");

    public override void Update(TradeTick tick) =>
        throw new NotSupportedException("A trade is size that has gone, not size resting in a book.");

    public override void Reset()
    {
        base.Reset();
        Value = 0m;
        BidSize = 0m;
        AskSize = 0m;
    }
}
