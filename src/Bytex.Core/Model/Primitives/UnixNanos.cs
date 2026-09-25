using System.Globalization;

namespace Bytex.Core.Model.Primitives;

/// <summary>
/// A point in time expressed as nanoseconds since the Unix epoch (1970-01-01T00:00:00Z).
/// </summary>
public readonly record struct UnixNanos(long Value) : IComparable<UnixNanos>, IComparable
{
    public const long NanosPerMicrosecond = 1_000L;
    public const long NanosPerMillisecond = 1_000_000L;
    public const long NanosPerSecond = 1_000_000_000L;
    public const long NanosPerMinute = 60L * NanosPerSecond;
    public const long NanosPerHour = 60L * NanosPerMinute;
    public const long NanosPerDay = 24L * NanosPerHour;

    /// <summary>
    /// The length of a month for bar aggregation. A month is not a fixed length, and a bar type that says "one month"
    /// has to mean something, so it means this many days.
    /// </summary>
    public const long DaysPerMonth = 30L;

    /// <summary>Nanoseconds in one .NET tick, the resolution TimeSpan and DateTime carry.</summary>
    public const long NanosPerTick = 100L;

    public static readonly UnixNanos Zero = new(0);
    public static readonly UnixNanos MaxValue = new(long.MaxValue);

    public static UnixNanos FromSeconds(long seconds) => new(checked(seconds * NanosPerSecond));

    public static UnixNanos FromMilliseconds(long milliseconds) => new(checked(milliseconds * NanosPerMillisecond));

    public static UnixNanos FromMicroseconds(long microseconds) => new(checked(microseconds * NanosPerMicrosecond));

    public static UnixNanos FromDateTimeOffset(DateTimeOffset value)
    {
        long ticks = value.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        return new UnixNanos(checked(ticks * NanosPerTick));
    }

    public static UnixNanos FromDateTime(DateTime value)
    {
        DateTime utc = value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();
        return FromDateTimeOffset(new DateTimeOffset(utc));
    }

    public static UnixNanos Parse(string iso8601)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(iso8601);
        if (long.TryParse(iso8601, NumberStyles.Integer, CultureInfo.InvariantCulture, out long raw))
        {
            return new UnixNanos(raw);
        }

        // The last two digits of a nanosecond stamp are below the resolution of a tick, so they are taken from the text
        // and added on. What the text hands to DateTimeOffset has to be cut to seven digits first: asked to read nine, it
        // rounds the seventh up, and the two digits added on top of a rounded tick put the instant 100 ns in the future.
        string text = iso8601;
        long extraNanos = 0L;
        int dot = iso8601.IndexOf('.');
        if (dot >= 0)
        {
            int end = dot + 1;
            while (end < iso8601.Length && char.IsDigit(iso8601[end]))
            {
                end++;
            }

            string fraction = iso8601.Substring(dot + 1, end - dot - 1);
            if (fraction.Length > 7)
            {
                extraNanos = long.Parse(fraction.Substring(7).PadRight(2, '0').Substring(0, 2), CultureInfo.InvariantCulture);
                text = string.Concat(iso8601.AsSpan(0, dot + 1), fraction.AsSpan(0, 7), iso8601.AsSpan(end));
            }
        }

        DateTimeOffset parsed = DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        return FromDateTimeOffset(parsed).AddNanos(extraNanos);
    }

    public static bool TryParse(string? text, out UnixNanos value)
    {
        value = Zero;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            value = Parse(text);
            return true;
        }
        catch (Exception e) when (e is FormatException or OverflowException or ArgumentOutOfRangeException)
        {
            // A date the calendar accepts can still be outside the 64-bit nanosecond range, which ends in 2262.
            value = Zero;
            return false;
        }
    }

    public DateTimeOffset ToDateTimeOffset() => DateTimeOffset.UnixEpoch.AddTicks(Ticks);

    /// <summary>
    /// The tick the instant falls in. Division truncates towards zero, which for an instant before the epoch names the
    /// tick after it, so the value is floored: one nanosecond before 1970 belongs to the last tick of 1969.
    /// </summary>
    private long Ticks => Value >= 0 ? Value / NanosPerTick : (Value - (NanosPerTick - 1)) / NanosPerTick;

    public DateTime ToDateTimeUtc() => ToDateTimeOffset().UtcDateTime;

    public long ToSeconds() => Value / NanosPerSecond;

    public long ToMilliseconds() => Value / NanosPerMillisecond;

    public long ToMicroseconds() => Value / NanosPerMicrosecond;

    public UnixNanos Add(TimeSpan span) => new(checked(Value + span.Ticks * NanosPerTick));

    public UnixNanos Subtract(TimeSpan span) => new(checked(Value - span.Ticks * NanosPerTick));

    public UnixNanos AddNanos(long nanos) => new(checked(Value + nanos));

    public TimeSpan Since(UnixNanos earlier) => TimeSpan.FromTicks((Value - earlier.Value) / NanosPerTick);

    public UnixNanos FloorTo(long intervalNanos)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(intervalNanos, 0);
        return new UnixNanos(Value - ((Value % intervalNanos) + intervalNanos) % intervalNanos);
    }

    public UnixNanos CeilTo(long intervalNanos)
    {
        UnixNanos floor = FloorTo(intervalNanos);
        return floor.Value == Value ? floor : floor.AddNanos(intervalNanos);
    }

    public int CompareTo(UnixNanos other) => Value.CompareTo(other.Value);

    public int CompareTo(object? obj) => obj is UnixNanos other ? CompareTo(other) : throw new ArgumentException("Object is not a UnixNanos.", nameof(obj));

    public static bool operator <(UnixNanos left, UnixNanos right) => left.Value < right.Value;

    public static bool operator >(UnixNanos left, UnixNanos right) => left.Value > right.Value;

    public static bool operator <=(UnixNanos left, UnixNanos right) => left.Value <= right.Value;

    public static bool operator >=(UnixNanos left, UnixNanos right) => left.Value >= right.Value;

    public static UnixNanos operator +(UnixNanos left, TimeSpan right) => left.Add(right);

    public static UnixNanos operator -(UnixNanos left, TimeSpan right) => left.Subtract(right);

    public static TimeSpan operator -(UnixNanos left, UnixNanos right) => left.Since(right);

    public static implicit operator long(UnixNanos value) => value.Value;

    public override string ToString()
    {
        DateTimeOffset dto = ToDateTimeOffset();
        long subTick = Value - (Ticks * NanosPerTick);
        string body = dto.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
        return subTick == 0 ? body + "Z" : body + subTick.ToString("00", CultureInfo.InvariantCulture) + "Z";
    }
}
