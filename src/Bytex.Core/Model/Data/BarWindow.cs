using Bytex.Core.Model.Primitives;

namespace Bytex.Core.Model.Data;

/// <summary>
/// Which bars a window contains. One rule, in one place, because a caller asking three venues for the same period
/// has to get the same period back from all three: every venue filters differently - by a candle's open, by its
/// close, in seconds or in milliseconds, including or excluding whatever is still forming - and every one of those
/// differences is the venue's business and nobody else's. An adapter does whatever its venue needs in order to
/// return the right rows; what "the right rows" means is decided here.
/// </summary>
public static class BarWindow
{
    /// <summary>
    /// How many bars a request is really for. A limit says how many; with no limit, a START says the caller wants
    /// the whole window and no start says they want one page of the most recent.
    /// <para>
    /// Every venue's history helper needs this and each had its own copy. One of those copies defaulted to a page
    /// when a start WAS given, which turned "the last six months" into "the last thousand bars" without saying so -
    /// a request that succeeded and answered a different question. It is one function now so a venue cannot answer
    /// that question differently from the others.
    /// </para>
    /// </summary>
    public static int Wanted(int? limit, UnixNanos? start, int page) =>
        limit ?? (start is null ? page : int.MaxValue);

    /// <summary>
    /// A limit applied from whichever end the caller anchored. A start says where the window begins, so the limit
    /// caps how many bars follow it; with no start there is nothing anchoring the front and the newest are the ones
    /// wanted.
    /// <para>
    /// Every venue had to get this right and only one of them said so out loud. Binance and Bybit were taking the
    /// first N of what they had collected, which is correct only because they collect exactly N - so their
    /// correctness rested on the fetch and not on the rule, and anything that later ADDED a bar (KuCoin fills the
    /// intervals a venue leaves out, which is exactly that) would have silently returned the oldest N instead of
    /// the newest. The rule is applied the same way everywhere now, whatever a helper collected.
    /// </para>
    /// </summary>
    public static IReadOnlyList<Bar> Capped(IReadOnlyList<Bar> bars, UnixNanos? start, int limit)
    {
        ArgumentNullException.ThrowIfNull(bars);

        // A whole window - what Wanted returns when a start was given and no limit - arrives here as int.MaxValue and
        // is covered by this same test, since a count cannot exceed it. Saying so twice only looks like two rules.
        if (bars.Count <= limit)
        {
            return bars;
        }

        return start is null ? [.. bars.TakeLast(limit)] : [.. bars.Take(limit)];
    }

    /// <summary>
    /// The bars of the window, oldest first and each one only once: every bar that closes at or after
    /// <paramref name="start"/>, opens at or before <paramref name="end"/>, and has closed by <paramref name="now"/>.
    /// <para>
    /// A bar is named by the interval it covers, so the one that opens before the start and closes inside it belongs
    /// to the window - the period asked for is covered from its first moment. The bar still forming does not: it is
    /// not a bar yet, and a history that includes it says a candle closed when it has not.
    /// </para>
    /// </summary>
    public static IReadOnlyList<Bar> Closed(IEnumerable<Bar> bars, BarType barType, UnixNanos? start, UnixNanos? end, UnixNanos now)
    {
        ArgumentNullException.ThrowIfNull(bars);
        long interval = barType.Spec.IntervalNanos;
        if (interval <= 0)
        {
            throw new ArgumentException($"Bar type {barType} is not time-aggregated, so it has no window to bound.", nameof(barType));
        }

        // Nothing past the end, and nothing past now whatever the end says: a venue asked for a window reaching into
        // the future answers with the candle it is building.
        long lastOpen = Math.Min((end ?? now).Value, now.Value);

        return
        [
            .. bars
                .Where(b => b.TsEvent.Value - interval <= lastOpen
                    && b.TsEvent.Value <= now.Value
                    && (start is null || b.TsEvent.Value >= start.Value.Value))
                .DistinctBy(b => b.TsEvent)
                .OrderBy(b => b.TsEvent.Value)
        ];
    }
}
