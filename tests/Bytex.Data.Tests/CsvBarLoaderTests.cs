using Bytex.Core.Model.Data;
using Bytex.Core.Model.Primitives;
using Bytex.Data.Tests.Support;

namespace Bytex.Data.Tests;

// CsvLoader.LoadBars: the first line is always the header (matched case-insensitively), values take their
// precision from the instrument - prices are rounded to its price increment - and rows come back in time order.
// Files are written with explicit '\n' (or '\r\n' where that is the point) so the tests do not depend on the OS.
public sealed class CsvBarLoaderTests : IDisposable
{
    private const long Jan1 = 1_704_067_200_000_000_000L;
    private const long Minute = 60_000_000_000L;
    private const string ByteOrderMark = "\uFEFF";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Default_columns_map_to_every_bar_field()
    {
        string path = _temp.WriteLines(
            "bars.csv",
            "timestamp,open,high,low,close,volume",
            "2024-01-01T00:00:00Z,42000.10,42010.55,41990.00,42005.25,12.5",
            "2024-01-01T00:01:00Z,42005.25,42020.00,42001.01,42015.99,0.00001");

        IReadOnlyList<Bar> bars = CsvLoader.LoadBars(path, TestInstruments.BtcUsdt(), Sample.BtcMinute);

        Assert.Equal(2, bars.Count);
        Bar expected = new(
            Sample.BtcMinute,
            new Price(42000.10m, 2),
            new Price(42010.55m, 2),
            new Price(41990.00m, 2),
            new Price(42005.25m, 2),
            new Quantity(12.5m, 5),
            new UnixNanos(Jan1),
            new UnixNanos(Jan1));
        Assert.Equal(expected, bars[0]);
        Assert.Equal(new Quantity(0.00001m, 5), bars[1].Volume);
        Assert.Equal(Jan1 + Minute, bars[1].TsInit.Value);
        Assert.False(bars[0].IsRevision);
    }

    [Fact]
    public void Precision_comes_from_the_instrument_not_from_the_digits_in_the_file()
    {
        string path = _temp.WriteLines("bars.csv", "timestamp,open,high,low,close,volume", "2024-01-01T00:00:00Z,42000,42000.5,42000,42000.5,3");

        Bar bar = Assert.Single(CsvLoader.LoadBars(path, TestInstruments.BtcUsdt(), Sample.BtcMinute));

        Assert.Equal((byte)2, bar.Open.Precision);
        Assert.Equal("42000.50", bar.Close.ToString());
        Assert.Equal((byte)5, bar.Volume.Precision);
        Assert.Equal("3.00000", bar.Volume.ToString());
    }

    [Theory]
    // price increment 0.01
    [InlineData("BTCUSDT.BINANCE", "42000.126", "42000.13")]
    [InlineData("BTCUSDT.BINANCE", "42000.124", "42000.12")]
    // price increment 0.5 with one decimal: values snap to the half-point grid, exact midpoints go to the even step
    [InlineData("BTCUSD-PERP.BYBIT", "100.3", "100.5")]
    [InlineData("BTCUSD-PERP.BYBIT", "100.2", "100.0")]
    [InlineData("BTCUSD-PERP.BYBIT", "100.25", "100.0")]
    [InlineData("BTCUSD-PERP.BYBIT", "100.75", "101.0")]
    public void Prices_are_rounded_to_the_instruments_price_increment(string instrumentId, string raw, string expected)
    {
        string path = _temp.WriteLines("bars.csv", "timestamp,open,high,low,close", $"2024-01-01T00:00:00Z,{raw},{raw},{raw},{raw}");

        Bar bar = Assert.Single(CsvLoader.LoadBars(path, TestInstruments.All().Single(i => i.Id.Value == instrumentId), Sample.BtcMinute));

        Assert.Equal(expected, bar.Close.ToString());
    }

    [Fact]
    public void A_missing_volume_column_means_zero_volume()
    {
        string path = _temp.WriteLines("bars.csv", "timestamp,open,high,low,close", "2024-01-01T00:00:00Z,1,2,0.5,1.5");

        Bar bar = Assert.Single(CsvLoader.LoadBars(path, TestInstruments.BtcUsdt(), Sample.BtcMinute));

        Assert.Equal(new Quantity(0m, 5), bar.Volume);
    }

    [Fact]
    public void Custom_column_names_separator_and_timestamp_format_are_honoured()
    {
        string path = _temp.WriteLines(
            "export.csv",
            "Date;O;H;L;C;Vol;Ignored",
            "1704067260;10;12;9;11;100;x",
            "1704067200;9;10;8;10;50;y");
        CsvColumns columns = new()
        {
            Timestamp = "date",
            Open = "o",
            High = "h",
            Low = "l",
            Close = "c",
            Volume = "vol",
            Separator = ';',
            TimestampFormat = "unix_s",
        };

        IReadOnlyList<Bar> bars = CsvLoader.LoadBars(path, TestInstruments.BtcUsdt(), Sample.BtcMinute, columns);

        Assert.Equal(new[] { Jan1, Jan1 + Minute }, bars.Select(b => b.TsInit.Value));
        Assert.Equal(new[] { 10m, 11m }, bars.Select(b => b.Close.Value));
        Assert.Equal(new[] { 8m, 9m }, bars.Select(b => b.Low.Value));
        Assert.Equal(new[] { 50m, 100m }, bars.Select(b => b.Volume.Value));
    }

    [Fact]
    public void Column_order_does_not_matter_and_headers_are_case_insensitive()
    {
        string path = _temp.WriteLines("bars.csv", "VOLUME,Close,LOW,High,open,TimeStamp", "7,4,1,5,2,2024-01-01T00:00:00Z");

        Bar bar = Assert.Single(CsvLoader.LoadBars(path, TestInstruments.BtcUsdt(), Sample.BtcMinute));

        Assert.Equal((2m, 5m, 1m, 4m, 7m), (bar.Open.Value, bar.High.Value, bar.Low.Value, bar.Close.Value, bar.Volume.Value));
    }

    [Fact]
    public void Quotes_spaces_blank_lines_a_byte_order_mark_and_CRLF_are_tolerated()
    {
        string content = ByteOrderMark + "\"timestamp\", \"open\" ,\"high\",\"low\",\"close\",\"volume\"\r\n"
            + "\r\n"
            + "\"2024-01-01T00:00:00Z\", \"1.5\" ,2.5, 0.5 ,\"2\",\"10\"\r\n"
            + "   \r\n";
        string path = _temp.WriteText("excel.csv", content);

        Bar bar = Assert.Single(CsvLoader.LoadBars(path, TestInstruments.BtcUsdt(), Sample.BtcMinute));

        Assert.Equal((1.5m, 2.5m, 0.5m, 2m, 10m), (bar.Open.Value, bar.High.Value, bar.Low.Value, bar.Close.Value, bar.Volume.Value));
        Assert.Equal(Jan1, bar.TsInit.Value);
    }

    [Fact]
    public void Rows_are_returned_in_time_order()
    {
        string path = _temp.WriteLines(
            "bars.csv",
            "timestamp,open,high,low,close",
            "2024-01-01T00:02:00Z,3,3,3,3",
            "2024-01-01T00:00:00Z,1,1,1,1",
            "2024-01-01T00:01:00Z,2,2,2,2");

        IReadOnlyList<Bar> bars = CsvLoader.LoadBars(path, TestInstruments.BtcUsdt(), Sample.BtcMinute);

        Assert.Equal(new[] { 1m, 2m, 3m }, bars.Select(b => b.Close.Value));
    }

    [Fact]
    public void Decimal_points_are_read_as_such_under_a_comma_decimal_culture()
    {
        string path = _temp.WriteLines("bars.csv", "timestamp,open,high,low,close,volume", "2024-01-01T00:00:00Z,42000.10,42010.55,41990.00,42005.25,1234.5");

        using (new CommaDecimalCulture())
        {
            Assert.Equal("0,5", 0.5m.ToString()); // guard: the culture switch is really in effect

            Bar bar = Assert.Single(CsvLoader.LoadBars(path, TestInstruments.BtcUsdt(), Sample.BtcMinute));

            // Under de-DE/ru-RU rules "42000.10" would be read as 4200010 and "1234.5" as 12345.
            Assert.Equal(42000.10m, bar.Open.Value);
            Assert.Equal(1234.5m, bar.Volume.Value);
            Assert.Equal(Jan1, bar.TsInit.Value);
        }
    }

    [Fact]
    public void Scientific_notation_is_accepted()
    {
        string path = _temp.WriteLines("bars.csv", "timestamp,open,high,low,close,volume", "2024-01-01T00:00:00Z,4.2e4,4.2E4,4.2e4,4.2e4,1.5e-3");

        Bar bar = Assert.Single(CsvLoader.LoadBars(path, TestInstruments.BtcUsdt(), Sample.BtcMinute));

        Assert.Equal(42000m, bar.Close.Value);
        Assert.Equal(0.0015m, bar.Volume.Value);
    }

    [Fact]
    public void An_empty_file_gives_no_bars()
    {
        string path = _temp.WriteText("empty.csv", string.Empty);

        Assert.Empty(CsvLoader.LoadBars(path, TestInstruments.BtcUsdt(), Sample.BtcMinute));
    }

    [Fact]
    public void A_header_without_rows_gives_no_bars()
    {
        string path = _temp.WriteLines("header.csv", "timestamp,open,high,low,close,volume");

        Assert.Empty(CsvLoader.LoadBars(path, TestInstruments.BtcUsdt(), Sample.BtcMinute));
    }

    [Theory]
    [InlineData("timestamp,open,high,low,volume", "2024-01-01T00:00:00Z,1,2,0.5,10", "close")]
    [InlineData("time,open,high,low,close", "2024-01-01T00:00:00Z,1,2,0.5,1.5", "timestamp")]
    public void A_missing_required_column_is_reported_by_name(string header, string row, string missing)
    {
        string path = _temp.WriteLines("bars.csv", header, row);

        FormatException error = Assert.Throws<FormatException>(() => CsvLoader.LoadBars(path, TestInstruments.BtcUsdt(), Sample.BtcMinute));

        Assert.Contains($"'{missing}'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_without_a_header_row_is_rejected_because_the_first_line_is_always_the_header()
    {
        string path = _temp.WriteLines("noheader.csv", "2024-01-01T00:00:00Z,1,2,0.5,1.5,10", "2024-01-01T00:01:00Z,1,2,0.5,1.5,10");

        Assert.Throws<FormatException>(() => CsvLoader.LoadBars(path, TestInstruments.BtcUsdt(), Sample.BtcMinute));
    }

    [Theory]
    [InlineData("2024-01-01T00:00:00Z,1,2,0.5")] // truncated row: no close
    [InlineData("2024-01-01T00:00:00Z,1,2,0.5,abc,10")] // not a number
    [InlineData("2024-01-01T00:00:00Z,1,2,0.5,,10")] // empty price
    [InlineData("2024-01-01T00:00:00Z,1,2,0.5,1;5,10")] // wrong decimal separator
    [InlineData("yesterday,1,2,0.5,1.5,10")] // unparseable timestamp
    public void A_malformed_row_fails_the_load_instead_of_being_skipped(string row)
    {
        string path = _temp.WriteLines("bars.csv", "timestamp,open,high,low,close,volume", "2024-01-01T00:00:00Z,1,2,0.5,1.5,10", row);

        Assert.Throws<FormatException>(() => CsvLoader.LoadBars(path, TestInstruments.BtcUsdt(), Sample.BtcMinute));
    }

    [Fact]
    public void A_negative_volume_is_rejected()
    {
        string path = _temp.WriteLines("bars.csv", "timestamp,open,high,low,close,volume", "2024-01-01T00:00:00Z,1,2,0.5,1.5,-10");

        Assert.Throws<ArgumentOutOfRangeException>(() => CsvLoader.LoadBars(path, TestInstruments.BtcUsdt(), Sample.BtcMinute));
    }

    [Fact]
    public void A_missing_file_and_a_null_instrument_are_rejected()
    {
        Assert.Throws<FileNotFoundException>(() => CsvLoader.LoadBars(_temp.Combine("absent.csv"), TestInstruments.BtcUsdt(), Sample.BtcMinute));
        Assert.Throws<ArgumentNullException>(() => CsvLoader.LoadBars(_temp.Combine("absent.csv"), null!, Sample.BtcMinute));
    }

    [Fact]
    public async Task Loaded_bars_can_be_stored_in_the_catalog_and_read_back_unchanged()
    {
        // The `bytex catalog import-csv` path: CSV -> CsvLoader -> ParquetDataCatalog.
        string path = _temp.WriteLines(
            "bars.csv",
            "timestamp,open,high,low,close,volume",
            "2024-01-01T00:00:00Z,42000.10,42010.55,41990.00,42005.25,12.5",
            "2024-01-01T00:01:00Z,42005.25,42020.00,42001.01,42015.99,7.25");
        ParquetDataCatalog catalog = new(_temp.Combine("catalog"));
        IReadOnlyList<Bar> loaded = CsvLoader.LoadBars(path, TestInstruments.BtcUsdt(), Sample.BtcMinute);

        await catalog.WriteBarsAsync(loaded);

        Assert.Equal(loaded, await catalog.BarsAsync(Sample.BtcMinute));
    }
}
