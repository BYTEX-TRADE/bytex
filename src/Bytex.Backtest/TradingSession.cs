using Bytex.Core.Model.Primitives;

namespace Bytex.Backtest;

/// <summary>
/// When a venue's trading day ends, which is what a DAY order lives until (R8.26).
///
/// <para>
/// <b>Why this is not just a date.</b> A DAY order expires at the end of the session it was placed in, and a session is
/// not a calendar day on most venues: an equity index future's session ends in the evening and the next one begins
/// minutes later, so an order placed after that boundary belongs to tomorrow's session and must not expire the moment
/// the clock passes midnight. Treating the UTC date as the session - which is what this engine did before - expires an
/// evening order hours early and keeps a morning one hours late.
/// </para>
///
/// <para>
/// A venue with no session at all - a crypto venue that never closes - has no end, and a DAY order there expires when
/// the UTC date rolls over. That is what those venues do with one, and it is what this engine did for every venue
/// before there was a session to state.
/// </para>
/// </summary>
/// <param name="End">
/// The time of day, in UTC, at which the session ends. <see cref="TimeSpan.Zero"/> and a full day both mean midnight.
/// </param>
public readonly record struct TradingSession(TimeSpan End)
{
    /// <summary>A venue that never closes: the session is the UTC day.</summary>
    public static readonly TradingSession Continuous = new(TimeSpan.Zero);

    /// <summary>A session ending at a time of day in UTC.</summary>
    public static TradingSession EndingAt(TimeSpan endOfDayUtc)
    {
        if (endOfDayUtc < TimeSpan.Zero || endOfDayUtc > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(endOfDayUtc), endOfDayUtc, "A session ends at a time of day, so it is between nothing and a whole day.");
        }

        return new TradingSession(endOfDayUtc == TimeSpan.FromDays(1) ? TimeSpan.Zero : endOfDayUtc);
    }

    /// <summary>Whether this session ends anywhere other than at midnight.</summary>
    public bool IsContinuous => End == TimeSpan.Zero;

    /// <summary>
    /// Which session an instant belongs to, as the date that session ends on.
    ///
    /// <para>
    /// An instant at or after the end of day belongs to the NEXT session, which is the whole reason this type exists: an
    /// order placed a minute after the close is an order for tomorrow.
    /// </para>
    /// </summary>
    public DateOnly SessionOf(UnixNanos timestamp)
    {
        DateTime moment = timestamp.ToDateTimeUtc();
        DateOnly date = DateOnly.FromDateTime(moment);
        return IsContinuous || moment.TimeOfDay < End ? date : date.AddDays(1);
    }

    /// <summary>Whether a session boundary lies between when an order was accepted and now.</summary>
    public bool HasEnded(UnixNanos placed, UnixNanos now) => SessionOf(now) > SessionOf(placed);
}
