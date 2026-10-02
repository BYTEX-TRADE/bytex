using Bytex.Cli.Tests.Support;

namespace Bytex.Cli.Tests;

// Why (R9.9): the loader could read a headerless file and honour a quoted field, and `catalog import-csv` could ask
// for neither - so the feature was real for a host that references the library and absent for anybody using the tool
// it was built for. An exchange's own archive is exactly the headerless, sometimes semicolon-separated, sometimes
// quoted file in question.
//
// These drive the real executable, because the gap was never in the loader.
public sealed class CsvImportCommandTests
{
    private const string InstrumentJson = """
        {
          "kind": "CurrencyPair",
          "id": "bx-market:v2/SIM/BTCUSDT",
          "rawSymbol": "BTCUSDT",
          "assetClass": "crypto",
          "instrumentClass": "spot",
          "quoteCurrency": "USDT",
          "baseCurrency": "BTC",
          "pricePrecision": 2,
          "sizePrecision": 5,
          "priceIncrement": "0.01",
          "sizeIncrement": "0.00001",
          "makerFee": 0.001,
          "takerFee": 0.001,
          "eventTime": 0,
          "createdTime": 0
        }
        """;

    private const string CandleSeries = "bx-candle:v2/SIM/BTCUSDT/minute/1/last/provider";

    private static async Task<string> CatalogWithInstrumentAsync(TempDirectory temp)
    {
        string catalog = temp.Combine("catalog");
        CliResult added = await CliRunner.RunAsync(["catalog", "add-instrument", "--path", catalog, "--file", temp.File("btcusdt.json", InstrumentJson)]);

        Assert.Equal(0, added.ExitCode);
        return catalog;
    }

    [Fact]
    public async Task A_file_whose_first_line_is_data_is_imported_by_position()
    {
        // The case the option exists for: no header, so each field is given its column number.
        using TempDirectory temp = new();
        string catalog = await CatalogWithInstrumentAsync(temp);
        string file = temp.File("headerless.csv", "1700000040000,100.00,110.00,90.00,105.00,12.5\n1700000100000,105.00,115.00,95.00,110.00,3.5\n");

        CliResult imported = await CliRunner.RunAsync([
            "catalog", "import-csv", "-p", catalog, "-f", file, "-k", "bars", "-i", "bx-market:v2/SIM/BTCUSDT",
            "--candle-series", CandleSeries, "--timestamp-format", "unix_ms",
            "--no-header", "--columns", "timestamp=0,open=1,high=2,low=3,close=4,volume=5",
        ]);

        Assert.True(imported.ExitCode == 0, imported.AllOutput);
        Assert.Contains("Imported 2 bars", imported.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_the_option_the_first_line_is_still_the_header()
    {
        // What happened before, and must go on happening by default. The file loses its first row to a header it does
        // not have, and the second row is then refused for column names that are prices.
        using TempDirectory temp = new();
        string catalog = await CatalogWithInstrumentAsync(temp);
        string file = temp.File("headerless.csv", "1700000040000,100.00,110.00,90.00,105.00,12.5\n1700000100000,105.00,115.00,95.00,110.00,3.5\n");

        CliResult imported = await CliRunner.RunAsync([
            "catalog", "import-csv", "-p", catalog, "-f", file, "-k", "bars", "-i", "bx-market:v2/SIM/BTCUSDT",
            "--candle-series", CandleSeries, "--timestamp-format", "unix_ms",
        ]);

        Assert.NotEqual(0, imported.ExitCode);
    }

    [Fact]
    public async Task An_import_that_read_nothing_says_what_usually_causes_that()
    {
        // A one-row headerless file loses its only row to the header and imports nothing at all. Reporting
        // "Imported 0" and success is the same words as a file that really was empty, which is the wrong answer to a
        // question somebody is about to ask again with the same command.
        using TempDirectory temp = new();
        string catalog = await CatalogWithInstrumentAsync(temp);
        string file = temp.File("one.csv", "1700000040000,100.00,110.00,90.00,105.00,12.5\n");

        CliResult imported = await CliRunner.RunAsync([
            "catalog", "import-csv", "-p", catalog, "-f", file, "-k", "bars", "-i", "bx-market:v2/SIM/BTCUSDT",
            "--candle-series", CandleSeries, "--timestamp-format", "unix_ms",
        ]);

        Assert.Equal(0, imported.ExitCode);
        Assert.Contains("Imported 0 bars", imported.StdOut, StringComparison.Ordinal);
        Assert.Contains("add --no-header together with --columns", imported.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Asking_for_no_header_without_saying_which_column_is_which_is_refused_by_name()
    {
        // A headerless file has no names to match, so there is nothing to guess from - and guessing is the one thing
        // that must not happen, because a column read from the wrong position parses into a plausible price.
        using TempDirectory temp = new();
        string catalog = await CatalogWithInstrumentAsync(temp);
        string file = temp.File("headerless.csv", "1700000040000,100.00,110.00,90.00,105.00,12.5\n");

        CliResult imported = await CliRunner.RunAsync([
            "catalog", "import-csv", "-p", catalog, "-f", file, "-k", "bars", "-i", "bx-market:v2/SIM/BTCUSDT",
            "--candle-series", CandleSeries, "--timestamp-format", "unix_ms", "--no-header",
        ]);

        Assert.NotEqual(0, imported.ExitCode);
        Assert.Contains("--no-header needs --columns", imported.AllOutput, StringComparison.Ordinal);
        Assert.Contains("timestamp=0,open=1", imported.AllOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_column_name_given_for_a_headerless_file_is_refused_as_such()
    {
        using TempDirectory temp = new();
        string catalog = await CatalogWithInstrumentAsync(temp);
        string file = temp.File("headerless.csv", "1700000040000,100.00,110.00,90.00,105.00,12.5\n");

        CliResult imported = await CliRunner.RunAsync([
            "catalog", "import-csv", "-p", catalog, "-f", file, "-k", "bars", "-i", "bx-market:v2/SIM/BTCUSDT",
            "--candle-series", CandleSeries, "--no-header", "--columns", "timestamp=0,open=opening",
        ]);

        Assert.NotEqual(0, imported.ExitCode);
        Assert.Contains("is a column name, and a file with no header has none", imported.AllOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_field_no_csv_can_be_mapped_to_is_refused_with_the_list_of_those_that_can()
    {
        using TempDirectory temp = new();
        string catalog = await CatalogWithInstrumentAsync(temp);
        string file = temp.File("bars.csv", "timestamp,open,high,low,close,volume\n1700000040000,1,1,1,1,1\n");

        CliResult imported = await CliRunner.RunAsync([
            "catalog", "import-csv", "-p", catalog, "-f", file, "-k", "bars", "-i", "bx-market:v2/SIM/BTCUSDT",
            "--candle-series", CandleSeries, "--columns", "closing=close",
        ]);

        Assert.NotEqual(0, imported.ExitCode);
        Assert.Contains("is not a field a CSV can be mapped to", imported.AllOutput, StringComparison.Ordinal);
        Assert.Contains("timestamp, open, high, low, close, volume", imported.AllOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_header_under_another_name_is_mapped_to_the_field_it_is()
    {
        // The other half of --columns: the file has names, they are simply not ours.
        using TempDirectory temp = new();
        string catalog = await CatalogWithInstrumentAsync(temp);
        string file = temp.File("named.csv", "time,o,h,l,last,vol\n1700000040000,100.00,110.00,90.00,105.00,12.5\n");

        CliResult imported = await CliRunner.RunAsync([
            "catalog", "import-csv", "-p", catalog, "-f", file, "-k", "bars", "-i", "bx-market:v2/SIM/BTCUSDT",
            "--candle-series", CandleSeries, "--timestamp-format", "unix_ms",
            "--columns", "timestamp=time,open=o,high=h,low=l,close=last,volume=vol",
        ]);

        Assert.True(imported.ExitCode == 0, imported.AllOutput);
        Assert.Contains("Imported 1 bars", imported.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_semicolon_separated_file_is_imported_when_the_separator_is_given()
    {
        using TempDirectory temp = new();
        string catalog = await CatalogWithInstrumentAsync(temp);
        string file = temp.File("semi.csv", "timestamp;open;high;low;close;volume\n1700000040000;100.00;110.00;90.00;105.00;12.5\n");

        CliResult imported = await CliRunner.RunAsync([
            "catalog", "import-csv", "-p", catalog, "-f", file, "-k", "bars", "-i", "bx-market:v2/SIM/BTCUSDT",
            "--candle-series", CandleSeries, "--timestamp-format", "unix_ms", "--separator", ";",
        ]);

        Assert.True(imported.ExitCode == 0, imported.AllOutput);
        Assert.Contains("Imported 1 bars", imported.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tab_separated_file_is_imported_when_the_separator_is_named()
    {
        // A tab cannot be typed as itself on a command line, so it is named.
        using TempDirectory temp = new();
        string catalog = await CatalogWithInstrumentAsync(temp);
        string file = temp.File("tabs.csv", "timestamp\topen\thigh\tlow\tclose\tvolume\n1700000040000\t100.00\t110.00\t90.00\t105.00\t12.5\n");

        CliResult imported = await CliRunner.RunAsync([
            "catalog", "import-csv", "-p", catalog, "-f", file, "-k", "bars", "-i", "bx-market:v2/SIM/BTCUSDT",
            "--candle-series", CandleSeries, "--timestamp-format", "unix_ms", "--separator", "tab",
        ]);

        Assert.True(imported.ExitCode == 0, imported.AllOutput);
        Assert.Contains("Imported 1 bars", imported.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_separator_that_is_not_one_is_refused()
    {
        using TempDirectory temp = new();
        string catalog = await CatalogWithInstrumentAsync(temp);
        string file = temp.File("bars.csv", "timestamp,open,high,low,close,volume\n1700000040000,1,1,1,1,1\n");

        CliResult imported = await CliRunner.RunAsync([
            "catalog", "import-csv", "-p", catalog, "-f", file, "-k", "bars", "-i", "bx-market:v2/SIM/BTCUSDT",
            "--candle-series", CandleSeries, "--separator", "semicolon",
        ]);

        Assert.NotEqual(0, imported.ExitCode);
        Assert.Contains("is not a separator", imported.AllOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_quoted_field_holding_the_separator_does_not_shift_the_columns()
    {
        // No option for this one - quoting is honoured always - but the reach is what was never tested: a file like
        // this used to import a close price taken from the volume column, and say nothing.
        using TempDirectory temp = new();
        string catalog = await CatalogWithInstrumentAsync(temp);
        string file = temp.File("quoted.csv", "timestamp,name,open,high,low,close,volume\n1700000040000,\"Bitcoin, spot\",100.00,110.00,90.00,105.00,12.5\n");

        CliResult imported = await CliRunner.RunAsync([
            "catalog", "import-csv", "-p", catalog, "-f", file, "-k", "bars", "-i", "bx-market:v2/SIM/BTCUSDT",
            "--candle-series", CandleSeries, "--timestamp-format", "unix_ms",
        ]);

        Assert.True(imported.ExitCode == 0, imported.AllOutput);
        Assert.Contains("Imported 1 bars", imported.StdOut, StringComparison.Ordinal);
    }
}
