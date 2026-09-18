using System.Text.Json;
using Bytex.Cli.Tests.Support;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Data;

namespace Bytex.Cli.Tests;

// Why: "prepare a catalog, then backtest from a JSON file" is the first thing the documentation asks a new
// user to do. These tests walk that path with the real executable against a temp directory and then read the
// catalog back through the library to check what was actually stored, not just what was printed.
public sealed class CatalogAndBacktestCommandTests
{
    private const string InstrumentJson = """
        {
          "kind": "CurrencyPair",
          "id": "BTCUSDT.SIM",
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
          "tsEvent": 0,
          "tsInit": 0
        }
        """;

    // Rows are deliberately out of order; 1700000040000 ms is 2023-11-14T22:14:00Z.
    private const string BarsCsv =
        "timestamp,open,high,low,close,volume\n" +
        "1700000160000,30020.00,30040.00,30010.00,30030.00,2.5\n" +
        "1700000040000,30000.00,30010.50,29990.25,30005.75,1.25\n" +
        "1700000100000,30005.75,30025.00,30000.00,30020.00,0.75\n";

    private const string BarType = "BTCUSDT.SIM-1-MINUTE-LAST-EXTERNAL";

    private static async Task<string> PreparedCatalogAsync(TempDirectory temp)
    {
        string catalog = temp.Combine("catalog");
        CliResult add = await CliRunner.RunAsync(["catalog", "add-instrument", "--path", catalog, "--file", temp.File("btcusdt.json", InstrumentJson)]);
        Assert.True(add.ExitCode == 0, add.AllOutput);
        CliResult import = await CliRunner.RunAsync(["catalog", "import-csv", "-p", catalog, "-f", temp.File("bars.csv", BarsCsv), "-k", "bars", "-i", "BTCUSDT.SIM", "--bar-type", BarType, "--timestamp-format", "unix_ms"]);
        Assert.True(import.ExitCode == 0, import.AllOutput);
        return catalog;
    }

    [Fact]
    public async Task Add_instrument_stores_the_definition_with_its_increments()
    {
        using TempDirectory temp = new();
        string catalog = temp.Combine("catalog");

        CliResult result = await CliRunner.RunAsync(["catalog", "add-instrument", "--path", catalog, "--file", temp.File("btcusdt.json", InstrumentJson)]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Added BTCUSDT.SIM", result.StdOut);
        Instrument stored = Assert.Single(new ParquetDataCatalog(catalog).Instruments());
        Assert.IsType<CurrencyPair>(stored);
        Assert.Equal(InstrumentId.Parse("BTCUSDT.SIM"), stored.Id);
        Assert.Equal(new Price(0.01m, 2), stored.PriceIncrement);
        Assert.Equal(new Quantity(0.00001m, 5), stored.SizeIncrement);
        Assert.Equal(0.001m, stored.TakerFee);
    }

    [Fact]
    public async Task Import_csv_writes_bars_sorted_by_time_with_instrument_precision_and_unix_ms_timestamps()
    {
        using TempDirectory temp = new();

        string catalog = await PreparedCatalogAsync(temp);

        IReadOnlyList<Bar> bars = await new ParquetDataCatalog(catalog).BarsAsync(Core.Model.Data.BarType.Parse(BarType));
        Assert.Equal(3, bars.Count);
        Assert.Equal([1_700_000_040_000_000_000L, 1_700_000_100_000_000_000L, 1_700_000_160_000_000_000L], bars.Select(b => b.TsEvent.Value));
        Bar first = bars[0];
        Assert.Equal(new Price(30_000.00m, 2), first.Open);
        Assert.Equal(new Price(30_010.50m, 2), first.High);
        Assert.Equal(new Price(29_990.25m, 2), first.Low);
        Assert.Equal(new Price(30_005.75m, 2), first.Close);
        Assert.Equal(new Quantity(1.25m, 5), first.Volume);
    }

    [Fact]
    public async Task Import_csv_reports_how_many_bars_it_stored()
    {
        using TempDirectory temp = new();
        string catalog = temp.Combine("catalog");
        await CliRunner.RunAsync(["catalog", "add-instrument", "--path", catalog, "--file", temp.File("btcusdt.json", InstrumentJson)]);

        CliResult result = await CliRunner.RunAsync(["catalog", "import-csv", "--path", catalog, "--file", temp.File("bars.csv", BarsCsv), "--kind", "BARS", "--instrument", "BTCUSDT.SIM", "--bar-type", BarType, "--timestamp-format", "unix_ms"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains($"Imported 3 bars for {BarType}", result.StdOut);
    }

    [Fact]
    public async Task Import_csv_stores_quotes_and_trades_under_the_instrument()
    {
        using TempDirectory temp = new();
        string catalog = temp.Combine("catalog");
        await CliRunner.RunAsync(["catalog", "add-instrument", "--path", catalog, "--file", temp.File("btcusdt.json", InstrumentJson)]);
        string quotes = temp.File("quotes.csv", "timestamp,bid,ask,bid_size,ask_size\n2023-11-14T22:14:00Z,29999.50,30000.50,1.5,2.5\n");
        string trades = temp.File("trades.csv", "timestamp,price,size,side,trade_id\n2023-11-14T22:14:01Z,30000.25,0.4,buy,T-1\n2023-11-14T22:14:02Z,30000.00,0.1,sell,T-2\n");

        CliResult quoteResult = await CliRunner.RunAsync(["catalog", "import-csv", "--path", catalog, "--file", quotes, "--kind", "quotes", "--instrument", "BTCUSDT.SIM"]);
        CliResult tradeResult = await CliRunner.RunAsync(["catalog", "import-csv", "--path", catalog, "--file", trades, "--kind", "trades", "--instrument", "BTCUSDT.SIM"]);

        Assert.True(quoteResult.ExitCode == 0, quoteResult.AllOutput);
        Assert.True(tradeResult.ExitCode == 0, tradeResult.AllOutput);
        Assert.Contains("Imported 1 quotes for BTCUSDT.SIM", quoteResult.StdOut);
        Assert.Contains("Imported 2 trades for BTCUSDT.SIM", tradeResult.StdOut);
        ParquetDataCatalog stored = new(catalog);
        QuoteTick quote = Assert.Single(await stored.QuoteTicksAsync(InstrumentId.Parse("BTCUSDT.SIM")));
        Assert.Equal(new Price(29_999.50m, 2), quote.Bid);
        Assert.Equal(new Price(30_000.50m, 2), quote.Ask);
        Assert.Equal(1_700_000_040_000_000_000L, quote.TsEvent.Value);
        IReadOnlyList<TradeTick> storedTrades = await stored.TradeTicksAsync(InstrumentId.Parse("BTCUSDT.SIM"));
        Assert.Equal(["T-1", "T-2"], storedTrades.Select(t => t.TradeId.Value));
        Assert.Equal([Core.Model.AggressorSide.Buyer, Core.Model.AggressorSide.Seller], storedTrades.Select(t => t.Aggressor));
    }

    [Fact]
    public async Task List_shows_instruments_and_data_sets_with_their_time_range()
    {
        using TempDirectory temp = new();
        string catalog = await PreparedCatalogAsync(temp);

        CliResult result = await CliRunner.RunAsync(["catalog", "list", "--path", catalog]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Instruments (1):", result.StdOut);
        Assert.Contains("BTCUSDT.SIM  CurrencyPair  tick=0.01 step=0.00001", result.StdOut);
        string dataLine = Assert.Single(result.StdOut.Split('\n'), l => l.Contains(BarType, StringComparison.Ordinal));
        Assert.Contains("bars", dataLine);
        Assert.Contains("files=1", dataLine);
        Assert.Contains("2023-11-14T22:14:00", dataLine);
        Assert.Contains("2023-11-14T22:16:00", dataLine);
    }

    [Fact]
    public async Task List_on_an_empty_catalog_reports_zero_instruments()
    {
        using TempDirectory temp = new();

        CliResult result = await CliRunner.RunAsync(["catalog", "list", "--path", temp.Combine("empty")]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Instruments (0):", result.StdOut);
    }

    [Fact]
    public async Task Importing_for_an_instrument_that_is_not_in_the_catalog_fails_and_says_how_to_fix_it()
    {
        using TempDirectory temp = new();

        CliResult result = await CliRunner.RunAsync(["catalog", "import-csv", "--path", temp.Combine("catalog"), "--file", temp.File("bars.csv", BarsCsv), "--kind", "bars", "--instrument", "ETHUSDT.SIM", "--bar-type", "ETHUSDT.SIM-1-MINUTE-LAST-EXTERNAL"]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("ETHUSDT.SIM is not in the catalog", result.AllOutput);
        Assert.Contains("add-instrument", result.AllOutput);
    }

    [Fact]
    public async Task Importing_bars_without_a_bar_type_fails_with_a_clear_message()
    {
        using TempDirectory temp = new();
        string catalog = temp.Combine("catalog");
        await CliRunner.RunAsync(["catalog", "add-instrument", "--path", catalog, "--file", temp.File("btcusdt.json", InstrumentJson)]);

        CliResult result = await CliRunner.RunAsync(["catalog", "import-csv", "--path", catalog, "--file", temp.File("bars.csv", BarsCsv), "--kind", "bars", "--instrument", "BTCUSDT.SIM"]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("--bar-type is required for bars", result.AllOutput);
    }

    [Fact]
    public async Task An_unknown_data_kind_exits_with_code_one_and_stores_nothing()
    {
        using TempDirectory temp = new();
        string catalog = temp.Combine("catalog");
        await CliRunner.RunAsync(["catalog", "add-instrument", "--path", catalog, "--file", temp.File("btcusdt.json", InstrumentJson)]);

        CliResult result = await CliRunner.RunAsync(["catalog", "import-csv", "--path", catalog, "--file", temp.File("bars.csv", BarsCsv), "--kind", "candles", "--instrument", "BTCUSDT.SIM"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Unknown kind", result.StdErr);
        Assert.Empty(new ParquetDataCatalog(catalog).BarTypes());
    }

    // ----- backtest -----

    private static string BacktestConfig(string catalog, string runId, string? output) => JsonSerializer.Serialize(new
    {
        engine = new { runId, kernel = new { traderId = "BACKTESTER-001", environment = "backtest", loadState = false, saveState = false } },
        venues = new[] { new { venue = "SIM", omsType = "netting", accountType = "cash", startingBalances = new[] { "100000 USDT", "1 BTC" } } },
        data = new[] { new { catalogPath = catalog, dataKind = "bars", barType = BarType } },
        outputDirectory = output,
    });

    [Fact]
    public async Task Backtest_runs_a_configuration_file_over_catalog_data_and_prints_a_summary()
    {
        using TempDirectory temp = new();
        string catalog = await PreparedCatalogAsync(temp);
        string config = temp.File("backtest.json", BacktestConfig(catalog, "cli-smoke", null));

        CliResult result = await CliRunner.RunAsync(["--log-level", "Warning", "backtest", "--config", config], temp.Path);

        Assert.True(result.ExitCode == 0, result.AllOutput);
        Assert.Contains("Backtest cli-smoke (BACKTESTER-001)", result.StdOut);
        Assert.Contains("Iterations:  3", result.StdOut); // one per imported bar
        Assert.Contains("Orders:      0", result.StdOut); // no strategy was configured
        Assert.Contains("[USDT] start 100,000.00  end 100,000.00", result.StdOut);
    }

    [Fact]
    public async Task A_file_with_an_array_runs_every_configuration_and_the_output_option_overrides_their_directories()
    {
        using TempDirectory temp = new();
        string catalog = await PreparedCatalogAsync(temp);
        string both = "[" + BacktestConfig(catalog, "first-run", temp.Combine("ignored")) + "," + BacktestConfig(catalog, "second-run", temp.Combine("ignored")) + "]";
        string reports = temp.Combine("reports");

        CliResult result = await CliRunner.RunAsync(["--log-level", "Warning", "backtest", "-c", temp.File("runs.json", both), "-o", reports], temp.Path);

        Assert.True(result.ExitCode == 0, result.AllOutput);
        Assert.True(result.StdOut.IndexOf("Backtest first-run", StringComparison.Ordinal) < result.StdOut.IndexOf("Backtest second-run", StringComparison.Ordinal));
        Assert.False(Directory.Exists(temp.Combine("ignored")));
        Assert.NotEmpty(Directory.EnumerateFileSystemEntries(reports, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_configuration_that_is_not_valid_json_fails_with_a_non_zero_exit_code()
    {
        using TempDirectory temp = new();

        CliResult result = await CliRunner.RunAsync(["backtest", "--config", temp.File("broken.json", "{ \"venues\": [ ")]);

        Assert.NotEqual(0, result.ExitCode);
    }
}
