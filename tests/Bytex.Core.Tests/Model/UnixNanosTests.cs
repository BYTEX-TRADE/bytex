using Bytex.Core.Model.Primitives;

namespace Bytex.Core.Tests.Model;

// UnixNanos orders every event in the engine. These tests protect unit conversions, the documented lossy
// boundary with 100 ns .NET ticks, overflow behaviour, bar-boundary flooring, and nanosecond-exact text.
public class UnixNanosTests
{
    // 2024-01-01T00:00:00Z is 19723 days after the epoch: 19723 * 86400 = 1 704 067 200 seconds.
    public const long Jan2024Seconds = 1_704_067_200L;
    public const long Jan2024Nanos = Jan2024Seconds * 1_000_000_000L;

    [Fact]
    public void Unit_factories_scale_to_nanoseconds()
    {
        Assert.Equal(3_000_000_000L, UnixNanos.FromSeconds(3).Value);
        Assert.Equal(3_000_000L, UnixNanos.FromMilliseconds(3).Value);
        Assert.Equal(3_000L, UnixNanos.FromMicroseconds(3).Value);
        Assert.Equal(-1_000_000_000L, UnixNanos.FromSeconds(-1).Value);
    }

    [Fact]
    public void Unit_factories_throw_on_overflow_instead_of_wrapping()
    {
        // long.MaxValue nanoseconds is about 9.22e9 seconds (year 2262).
        Assert.Throws<OverflowException>(() => UnixNanos.FromSeconds(9_223_372_037L));
        Assert.Throws<OverflowException>(() => UnixNanos.FromMilliseconds(long.MaxValue));
        Assert.Throws<OverflowException>(() => UnixNanos.FromMicroseconds(long.MinValue));
    }

    [Fact]
    public void Unit_accessors_truncate_toward_zero()
    {
        UnixNanos value = new(1_999_999_999L);

        Assert.Equal(1L, value.ToSeconds());
        Assert.Equal(1_999L, value.ToMilliseconds());
        Assert.Equal(1_999_999L, value.ToMicroseconds());
    }

    [Fact]
    public void FromDateTimeOffset_converts_a_known_instant()
    {
        UnixNanos value = UnixNanos.FromDateTimeOffset(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(Jan2024Nanos, value.Value);
    }

    [Fact]
    public void FromDateTimeOffset_normalises_the_offset_to_utc()
    {
        UnixNanos value = UnixNanos.FromDateTimeOffset(new DateTimeOffset(2024, 1, 1, 3, 0, 0, TimeSpan.FromHours(3)));

        Assert.Equal(Jan2024Nanos, value.Value);
    }

    [Fact]
    public void FromDateTimeOffset_handles_instants_before_the_epoch()
    {
        UnixNanos value = UnixNanos.FromDateTimeOffset(new DateTimeOffset(1969, 12, 31, 23, 59, 59, TimeSpan.Zero));

        Assert.Equal(-1_000_000_000L, value.Value);
    }

    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Unspecified)]
    public void FromDateTime_reads_utc_and_unspecified_kinds_as_utc(DateTimeKind kind)
    {
        DateTime dateTime = new(2024, 1, 1, 0, 0, 1, kind);

        Assert.Equal(Jan2024Nanos + 1_000_000_000L, UnixNanos.FromDateTime(dateTime).Value);
    }

    [Fact]
    public void ToDateTimeOffset_truncates_below_one_hundred_nanoseconds()
    {
        UnixNanos value = new(Jan2024Nanos + 199L);

        DateTimeOffset result = value.ToDateTimeOffset();

        // 199 ns is one whole 100 ns tick; the remaining 99 ns cannot be represented.
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(1), result);
        Assert.Equal(TimeSpan.Zero, result.Offset);
        Assert.Equal(DateTimeKind.Utc, value.ToDateTimeUtc().Kind);
    }

    [Fact]
    public void DateTimeOffset_round_trip_is_exact_at_tick_resolution()
    {
        DateTimeOffset instant = new DateTimeOffset(2026, 9, 18, 12, 34, 56, TimeSpan.Zero).AddTicks(1_234_567);

        Assert.Equal(instant, UnixNanos.FromDateTimeOffset(instant).ToDateTimeOffset());
    }

    [Fact]
    public void Add_and_Subtract_move_by_the_time_span()
    {
        UnixNanos start = new(Jan2024Nanos);

        Assert.Equal(Jan2024Nanos + 1_500_000_000L, start.Add(TimeSpan.FromMilliseconds(1500)).Value);
        Assert.Equal(Jan2024Nanos - 60_000_000_000L, start.Subtract(TimeSpan.FromMinutes(1)).Value);
        Assert.Equal(start.Add(TimeSpan.FromHours(1)), start + TimeSpan.FromHours(1));
        Assert.Equal(start.Subtract(TimeSpan.FromHours(1)), start - TimeSpan.FromHours(1));
    }

    [Fact]
    public void AddNanos_keeps_single_nanosecond_resolution()
    {
        Assert.Equal(Jan2024Nanos + 1, new UnixNanos(Jan2024Nanos).AddNanos(1).Value);
        Assert.Equal(Jan2024Nanos - 1, new UnixNanos(Jan2024Nanos).AddNanos(-1).Value);
    }

    [Fact]
    public void Arithmetic_throws_on_overflow_instead_of_wrapping()
    {
        Assert.Throws<OverflowException>(() => UnixNanos.MaxValue.AddNanos(1));
        Assert.Throws<OverflowException>(() => UnixNanos.MaxValue.Add(TimeSpan.FromTicks(1)));
        Assert.Throws<OverflowException>(() => new UnixNanos(long.MinValue).Subtract(TimeSpan.FromTicks(1)));
    }

    [Fact]
    public void Difference_is_a_time_span_truncated_to_ticks()
    {
        UnixNanos earlier = new(Jan2024Nanos);
        UnixNanos later = new(Jan2024Nanos + 2_500_000_250L);

        // 2.500000250 s = 25 000 002 ticks plus 50 ns that a TimeSpan cannot hold.
        Assert.Equal(TimeSpan.FromTicks(25_000_002), later - earlier);
        Assert.Equal(TimeSpan.FromTicks(25_000_002), later.Since(earlier));
        Assert.Equal(TimeSpan.FromTicks(-25_000_002), earlier - later);
    }

    [Theory]
    [InlineData(125L, 60L, 120L)]
    [InlineData(120L, 60L, 120L)]
    [InlineData(59L, 60L, 0L)]
    [InlineData(0L, 60L, 0L)]
    [InlineData(-1L, 60L, -60L)] // floors toward negative infinity, not toward zero
    [InlineData(-60L, 60L, -60L)]
    public void FloorTo_snaps_down_to_the_interval_boundary(long seconds, long intervalSeconds, long expectedSeconds)
    {
        UnixNanos value = UnixNanos.FromSeconds(seconds);

        Assert.Equal(UnixNanos.FromSeconds(expectedSeconds), value.FloorTo(intervalSeconds * UnixNanos.NanosPerSecond));
    }

    [Theory]
    [InlineData(125L, 60L, 180L)]
    [InlineData(120L, 60L, 120L)]
    [InlineData(1L, 60L, 60L)]
    [InlineData(-1L, 60L, 0L)]
    public void CeilTo_snaps_up_to_the_interval_boundary(long seconds, long intervalSeconds, long expectedSeconds)
    {
        UnixNanos value = UnixNanos.FromSeconds(seconds);

        Assert.Equal(UnixNanos.FromSeconds(expectedSeconds), value.CeilTo(intervalSeconds * UnixNanos.NanosPerSecond));
    }

    [Fact]
    public void FloorTo_keeps_sub_second_boundaries_exact()
    {
        UnixNanos value = new(Jan2024Nanos + 123_456_789L);

        Assert.Equal(Jan2024Nanos + 123_000_000L, value.FloorTo(UnixNanos.NanosPerMillisecond).Value);
        Assert.Equal(Jan2024Nanos + 124_000_000L, value.CeilTo(UnixNanos.NanosPerMillisecond).Value);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void FloorTo_and_CeilTo_reject_non_positive_intervals(long interval)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => UnixNanos.Zero.FloorTo(interval));
        Assert.Throws<ArgumentOutOfRangeException>(() => UnixNanos.Zero.CeilTo(interval));
    }

    [Theory]
    [InlineData("1704067200000000000", Jan2024Nanos)]
    [InlineData("0", 0L)]
    [InlineData("-5", -5L)]
    [InlineData("2024-01-01T00:00:00Z", Jan2024Nanos)]
    [InlineData("2024-01-01T00:00:00", Jan2024Nanos)] // no zone means UTC, never the machine's zone
    [InlineData("2024-01-01T02:00:00+02:00", Jan2024Nanos)]
    [InlineData("2024-01-01T00:00:00.5Z", Jan2024Nanos + 500_000_000L)]
    [InlineData("2024-01-01T00:00:00.1234567Z", Jan2024Nanos + 123_456_700L)]
    [InlineData("2024-01-01T00:00:00.123456701Z", Jan2024Nanos + 123_456_701L)]
    [InlineData("2024-01-01T00:00:00.000000001Z", Jan2024Nanos + 1L)]
    public void Parse_reads_raw_nanoseconds_and_iso_8601(string text, long expected)
    {
        Assert.Equal(expected, UnixNanos.Parse(text).Value);
    }

    [Theory]
    [InlineData("2024-01-01T00:00:00.123456789Z", Jan2024Nanos + 123_456_789L)]
    [InlineData("2024-01-01T00:00:00.00000015Z", Jan2024Nanos + 150L)]
    [InlineData("2024-01-01T00:00:00.999999999Z", Jan2024Nanos + 999_999_999L)]
    public void Parse_keeps_all_nine_fractional_digits_when_the_eighth_is_five_or_more(string text, long expected)
    {
        Assert.Equal(expected, UnixNanos.Parse(text).Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Parse_rejects_missing_text(string? text)
    {
        Assert.ThrowsAny<ArgumentException>(() => UnixNanos.Parse(text!));
    }

    [Theory]
    [InlineData("yesterday")]
    [InlineData("2024-13-45T00:00:00Z")]
    public void Parse_rejects_text_that_is_neither_an_integer_nor_a_date(string text)
    {
        Assert.Throws<FormatException>(() => UnixNanos.Parse(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("yesterday")]
    [InlineData("2024-13-45T00:00:00Z")]
    public void TryParse_returns_false_for_missing_or_malformed_text(string? text)
    {
        Assert.False(UnixNanos.TryParse(text, out UnixNanos value));
        Assert.Equal(UnixNanos.Zero, value);
    }

    [Fact]
    public void TryParse_returns_false_for_a_date_beyond_the_representable_range()
    {
        // The 64-bit nanosecond range ends in 2262; year 9999 is a valid date that does not fit.
        bool ok = UnixNanos.TryParse("9999-12-31T23:59:59Z", out UnixNanos value);

        Assert.False(ok);
        Assert.Equal(UnixNanos.Zero, value);
    }

    [Fact]
    public void TryParse_returns_the_same_value_as_Parse()
    {
        Assert.True(UnixNanos.TryParse("2024-01-01T00:00:00Z", out UnixNanos value));
        Assert.Equal(Jan2024Nanos, value.Value);
    }

    [Theory]
    [InlineData(Jan2024Nanos, "2024-01-01T00:00:00.0000000Z")]
    [InlineData(Jan2024Nanos + 123_456_700L, "2024-01-01T00:00:00.1234567Z")]
    [InlineData(Jan2024Nanos + 123_456_789L, "2024-01-01T00:00:00.123456789Z")]
    [InlineData(Jan2024Nanos + 5L, "2024-01-01T00:00:00.000000005Z")]
    [InlineData(0L, "1970-01-01T00:00:00.0000000Z")]
    public void ToString_is_iso_8601_utc_and_shows_nanoseconds_only_when_present(long nanos, string expected)
    {
        Assert.Equal(expected, new UnixNanos(nanos).ToString());
    }

    [Fact]
    public void ToString_renders_instants_before_the_epoch()
    {
        // One nanosecond before 1970 is the last nanosecond of 1969.
        Assert.Equal("1969-12-31T23:59:59.999999999Z", new UnixNanos(-1L).ToString());
    }

    // The last two digits stay below 50 here; 50 and above is the skipped Parse case above.
    [Theory]
    [InlineData(Jan2024Nanos)]
    [InlineData(Jan2024Nanos + 1L)]
    [InlineData(Jan2024Nanos + 123_456_701L)]
    [InlineData(1_700_000_000_000_000_042L)]
    public void Text_round_trips_through_ToString_and_Parse_to_the_nanosecond(long nanos)
    {
        UnixNanos original = new(nanos);

        Assert.Equal(original, UnixNanos.Parse(original.ToString()));
    }

    [Fact]
    public void Comparison_orders_by_time()
    {
        UnixNanos earlier = new(1);
        UnixNanos later = new(2);

        Assert.True(earlier < later);
        Assert.True(later > earlier);
        Assert.True(earlier <= new UnixNanos(1));
        Assert.True(later >= new UnixNanos(2));
        Assert.True(earlier.CompareTo(later) < 0);
        Assert.Equal(0, earlier.CompareTo(new UnixNanos(1)));

        IComparable boxed = earlier;
        Assert.Throws<ArgumentException>(() => boxed.CompareTo(1L));
    }

    [Fact]
    public void Constants_and_long_conversion_expose_the_raw_count()
    {
        long raw = new UnixNanos(42);

        Assert.Equal(42L, raw);
        Assert.Equal(0L, UnixNanos.Zero.Value);
        Assert.Equal(long.MaxValue, UnixNanos.MaxValue.Value);
        Assert.Equal(86_400_000_000_000L, UnixNanos.NanosPerDay);
    }

    [Theory]
    [InlineData("2024-01-01T00:00:00.1234567891Z", Jan2024Nanos + 123_456_789L)]
    [InlineData("2024-01-01T00:00:00.123456789999Z", Jan2024Nanos + 123_456_789L)]
    public void Parse_cuts_what_sits_below_a_nanosecond_rather_than_rounding_it(string text, long expected)
    {
        Assert.Equal(expected, UnixNanos.Parse(text).Value);
    }

    [Fact]
    public void Parse_refuses_an_instant_the_range_cannot_hold()
    {
        // TryParse answers false for the same text; Parse says why.
        Assert.Throws<OverflowException>(() => UnixNanos.Parse("9999-12-31T23:59:59Z"));
    }

    [Theory]
    [InlineData(-1L, "1969-12-31T23:59:59.999999999Z")]
    [InlineData(-100L, "1969-12-31T23:59:59.9999999Z")]
    [InlineData(-150L, "1969-12-31T23:59:59.999999850Z")]
    [InlineData(-1_000_000_000L, "1969-12-31T23:59:59.0000000Z")]
    public void An_instant_before_the_epoch_reads_as_the_time_it_is(long nanos, string expected)
    {
        UnixNanos value = new(nanos);

        Assert.Equal(expected, value.ToString());
        Assert.Equal(nanos, UnixNanos.Parse(value.ToString()).Value);
    }

    [Fact]
    public void The_tick_of_an_instant_before_the_epoch_is_the_one_it_falls_in()
    {
        // Truncation towards zero would name the tick after it and put the instant in 1970.
        Assert.Equal(new DateTimeOffset(1969, 12, 31, 23, 59, 59, TimeSpan.Zero).AddTicks(9_999_999L), new UnixNanos(-1L).ToDateTimeOffset());
        Assert.Equal(1969, new UnixNanos(-1L).ToDateTimeUtc().Year);
    }
}
