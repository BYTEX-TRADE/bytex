namespace Bytex.Data.Tests;

// CsvColumns.TimestampFormat: "iso", "unix_s", "unix_ms", "unix_us", "unix_ns" or a .NET format string.
// Everything is UTC: text without a zone is taken as UTC, text with an offset is converted, and the result never
// depends on the machine's time zone or culture. Expected values are nanoseconds since 1970-01-01T00:00:00Z;
// 2024-01-01T00:00:00Z is 19723 days * 86400 s = 1 704 067 200 s.
public class CsvTimestampTests
{
    private const long Jan1 = 1_704_067_200_000_000_000L;

    [Theory]
    [InlineData("2024-01-01T00:00:00Z", Jan1)]
    [InlineData("2024-01-01T00:00:00+00:00", Jan1)]
    [InlineData("2024-01-01T02:00:00+02:00", Jan1)]
    [InlineData("2023-12-31T19:00:00-05:00", Jan1)]
    [InlineData("2024-01-01T00:00:00", Jan1)] // no zone: UTC, not local time
    [InlineData("2024-01-01 00:00:00", Jan1)]
    [InlineData("2024-01-01", Jan1)]
    [InlineData("1970-01-01T00:00:00Z", 0L)]
    [InlineData("2024-01-01T00:00:00.5Z", Jan1 + 500_000_000)]
    [InlineData("2024-01-01T00:00:00.123Z", Jan1 + 123_000_000)]
    [InlineData("2024-01-01T00:00:00.123456Z", Jan1 + 123_456_000)]
    [InlineData("2024-01-01T00:00:00.1234567Z", Jan1 + 123_456_700)]
    public void Iso_timestamps_are_read_as_UTC(string text, long expected)
    {
        Assert.Equal(expected, CsvLoader.ParseTimestamp(text, "iso").Value);
    }

    [Theory]
    [InlineData("2024-01-01T00:00:00.000000001Z", Jan1 + 1)]
    [InlineData("2024-01-01T00:00:00.123456749Z", Jan1 + 123_456_749)]
    [InlineData("2024-01-01T00:00:00.12345671Z", Jan1 + 123_456_710)]
    [InlineData("2024-01-01T02:00:00.000000042+02:00", Jan1 + 42)]
    public void Iso_timestamps_keep_the_eighth_and_ninth_fractional_digit(string text, long expected)
    {
        // .NET date types stop at 100 ns; the engine recovers the two digits below that from the text.
        Assert.Equal(expected, CsvLoader.ParseTimestamp(text, "iso").Value);
    }

    // The two digits below a tick are read from the text and added on, so what goes to DateTimeOffset has to be cut
    // to seven digits first: asked to read nine it rounds the seventh up, and the instant lands 100 ns late.
    [Theory]
    [InlineData("2024-01-01T00:00:00.123456789Z", Jan1 + 123_456_789)]
    [InlineData("2024-01-01T00:00:00.12345678Z", Jan1 + 123_456_780)]
    [InlineData("2024-01-01T00:00:00.000000099Z", Jan1 + 99)]
    [InlineData("2024-01-01T00:00:00.123456750Z", Jan1 + 123_456_750)]
    [InlineData("2024-01-01T00:00:00.999999999Z", Jan1 + 999_999_999)]
    public void Iso_timestamps_are_exact_when_the_sub_tick_digits_are_50_or_more(string text, long expected)
    {
        Assert.Equal(expected, CsvLoader.ParseTimestamp(text, "iso").Value);
    }

    [Theory]
    [InlineData("unix_s", "1704067200", Jan1)]
    [InlineData("unix_s", "0", 0L)]
    [InlineData("unix_ms", "1704067200123", Jan1 + 123_000_000)]
    [InlineData("unix_us", "1704067200123456", Jan1 + 123_456_000)]
    [InlineData("unix_ns", "1704067200123456789", Jan1 + 123_456_789)]
    public void Unix_epoch_formats_scale_to_nanoseconds(string format, string text, long expected)
    {
        Assert.Equal(expected, CsvLoader.ParseTimestamp(text, format).Value);
    }

    [Theory]
    [InlineData("yyyyMMdd HHmmss", "20240101 000000", Jan1)]
    [InlineData("dd.MM.yyyy HH:mm", "01.01.2024 00:01", Jan1 + 60_000_000_000)]
    [InlineData("MM/dd/yyyy HH:mm:ss.fff", "01/01/2024 00:00:00.250", Jan1 + 250_000_000)]
    [InlineData("yyyy-MM-dd'T'HH:mm:sszzz", "2024-01-01T03:00:00+03:00", Jan1)]
    public void A_custom_dotnet_format_is_read_as_UTC(string format, string text, long expected)
    {
        Assert.Equal(expected, CsvLoader.ParseTimestamp(text, format).Value);
    }

    [Theory]
    [InlineData("iso", "yesterday")]
    [InlineData("iso", "2024-13-45T00:00:00Z")]
    [InlineData("unix_s", "1704067200.5")]
    [InlineData("unix_ms", "2024-01-01T00:00:00Z")]
    [InlineData("unix_ns", "")]
    [InlineData("yyyyMMdd", "2024-01-01")]
    public void Text_that_does_not_match_the_format_is_rejected(string format, string text)
    {
        Exception error = Assert.ThrowsAny<Exception>(() => CsvLoader.ParseTimestamp(text, format));

        Assert.True(error is FormatException or ArgumentException, $"unexpected {error.GetType().Name}");
    }

    [Fact]
    public void A_unix_value_too_large_for_nanoseconds_overflows_loudly_instead_of_wrapping()
    {
        // 10^10 seconds is the year 2286; 10^10 * 10^9 ns does not fit into a signed 64-bit integer.
        Assert.Throws<OverflowException>(() => CsvLoader.ParseTimestamp("10000000000", "unix_s"));
    }
}
