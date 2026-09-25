using System.Text.Json;
using Bytex.Cli.Tests.Support;

namespace Bytex.Cli.Tests;

// Why: the documentation's shortest path to a running strategy is "write the examples, validate one, backtest it",
// and it is the path a reader tries first. These tests walk it with the real executable, so a broken example, a
// renamed option or a provider that no longer reads a document path fails here and not in someone's terminal.
public sealed class DocumentCommandTests
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

    private const string BarType = "BTCUSDT.SIM-1-MINUTE-LAST-EXTERNAL";

    [Fact]
    public async Task Examples_writes_every_example_document_and_each_one_validates()
    {
        using TempDirectory temp = new();
        string dir = temp.Combine("documents");

        CliResult written = await CliRunner.RunAsync(["documents", "examples", "--out", dir]);

        Assert.True(written.ExitCode == 0, written.AllOutput);
        string[] files = Directory.GetFiles(dir, "*.json").Order().ToArray();
        Assert.Equal(["breakout-retest.json", "ema-cross.json", "support-bounce.json"], files.Select(Path.GetFileName));
        foreach (string file in files)
        {
            Assert.Contains(file, written.StdOut, StringComparison.Ordinal);
            CliResult report = await CliRunner.RunAsync(["documents", "validate", "--document", file]);
            Assert.True(report.ExitCode == 0, report.AllOutput);
        }
    }

    [Fact]
    public async Task An_example_written_by_the_cli_backtests_from_a_configuration_that_names_it()
    {
        using TempDirectory temp = new();
        string catalog = temp.Combine("catalog");
        await CliRunner.RunAsync(["catalog", "add-instrument", "--path", catalog, "--file", temp.File("btcusdt.json", InstrumentJson)]);
        await CliRunner.RunAsync(["catalog", "import-csv", "-p", catalog, "-f", temp.File("bars.csv", Bars()), "-k", "bars", "-i", "BTCUSDT.SIM", "--bar-type", BarType, "--timestamp-format", "unix_ms"]);
        string documents = temp.Combine("documents");
        await CliRunner.RunAsync(["documents", "examples", "-o", documents]);

        string config = temp.File("backtest-document.json", JsonSerializer.Serialize(new
        {
            engine = new { runId = "document-ema-cross", kernel = new { traderId = "BACKTESTER-001" } },
            venues = new[] { new { venue = "SIM", accountType = "cash", startingBalances = new[] { "1000000 USDT", "10 BTC" } } },
            data = new[] { new { catalogPath = catalog, dataKind = "bars", barType = BarType } },
            strategies = new[]
            {
                new
                {
                    providerId = "bytex.document",
                    name = "EMA cross (document)",
                    payload = new { documentPath = Path.Combine(documents, "ema-cross.json"), strategyId = "EmaCrossDoc-001", parameterOverrides = new { fast = 2, slow = 3 } },
                },
            },
        }));

        CliResult result = await CliRunner.RunAsync(["--log-level", "Warning", "backtest", "--config", config], temp.Path);

        Assert.True(result.ExitCode == 0, result.AllOutput);
        Assert.Contains("Backtest document-ema-cross (BACKTESTER-001)", result.StdOut);
        Assert.Contains("Iterations:  6", result.StdOut); // one per imported bar, so the document saw the whole set
    }

    [Fact]
    public async Task A_configuration_that_names_a_run_and_a_space_is_run_as_a_search()
    {
        // The search form on the command line, which is the whole reason a search belongs in the engine rather than
        // only in a host: someone who never opens a user interface can still search. What is proved here is that the
        // configuration is recognised as a search rather than a batch, that generations actually run, and that what
        // it says it found can be read.
        using TempDirectory temp = new();
        string catalog = temp.Combine("catalog");
        await CliRunner.RunAsync(["catalog", "add-instrument", "--path", catalog, "--file", temp.File("btcusdt.json", InstrumentJson)]);
        await CliRunner.RunAsync(["catalog", "import-csv", "-p", catalog, "-f", temp.File("bars.csv", Bars()), "-k", "bars", "-i", "BTCUSDT.SIM", "--bar-type", BarType, "--timestamp-format", "unix_ms"]);
        string documents = temp.Combine("documents");
        await CliRunner.RunAsync(["documents", "examples", "-o", documents]);

        string config = temp.File("search-document.json", JsonSerializer.Serialize(new
        {
            run = new
            {
                engine = new { runId = "document-search", kernel = new { traderId = "BACKTESTER-001" } },
                venues = new[] { new { venue = "SIM", accountType = "cash", startingBalances = new[] { "1000000 USDT", "10 BTC" } } },
                data = new[] { new { catalogPath = catalog, dataKind = "bars", barType = BarType } },
                strategies = new[]
                {
                    new
                    {
                        providerId = "bytex.document",
                        name = "EMA cross (document)",
                        payload = new { documentPath = Path.Combine(documents, "ema-cross.json"), strategyId = "EmaCrossDoc-001", parameterOverrides = new { fast = 2, slow = 3 } },
                    },
                },
            },
            space = new[] { new { path = "parameterOverrides.fast", min = 2, max = 4, step = 1 } },
            population = 3,
            generations = 2,
            elites = 1,
            seed = 5,
            objective = new { figure = "returnPercent", currency = "USDT" },
        }));

        CliResult result = await CliRunner.RunAsync(["--log-level", "Warning", "backtest", "--config", config], temp.Path);

        Assert.True(result.ExitCode == 0, result.AllOutput);
        Assert.Contains("2 generation(s), 6 candidate(s)", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("Best: parameterOverrides.fast=", result.StdOut, StringComparison.Ordinal);

        // The point it reports is one the space allows, so it can be re-run as it stands.
        Assert.Matches(@"Best: parameterOverrides\.fast=[234] ", result.StdOut);
    }

    [Fact]
    public async Task A_search_that_does_not_say_what_it_is_looking_for_is_refused()
    {
        // A configuration cannot carry a function, so it has to name the figure. Guessing one would be the engine
        // deciding what better means, which is not its judgement to make - and a search judged by the wrong figure
        // looks exactly like a search judged by the right one.
        using TempDirectory temp = new();
        string config = temp.File("no-objective.json", JsonSerializer.Serialize(new
        {
            run = new
            {
                engine = new { runId = "no-objective", kernel = new { traderId = "BACKTESTER-001" } },
                venues = new[] { new { venue = "SIM", accountType = "cash", startingBalances = new[] { "1000 USDT" } } },
                data = Array.Empty<object>(),
                strategies = Array.Empty<object>(),
            },
            space = new[] { new { path = "parameterOverrides.fast", min = 2, max = 4, step = 1 } },
        }));

        CliResult result = await CliRunner.RunAsync(["--log-level", "Warning", "backtest", "--config", config], temp.Path);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("needs an objective", result.AllOutput, StringComparison.Ordinal);
    }

    // Six one-minute bars climbing then falling, enough for a 2/3 EMA cross in each direction.
    private static string Bars() =>
        "timestamp,open,high,low,close,volume\n" +
        "1700000040000,30000.00,30010.00,29990.00,30000.00,1\n" +
        "1700000100000,30000.00,30060.00,30000.00,30050.00,1\n" +
        "1700000160000,30050.00,30160.00,30050.00,30150.00,1\n" +
        "1700000220000,30150.00,30260.00,30150.00,30250.00,1\n" +
        "1700000280000,30250.00,30250.00,30100.00,30100.00,1\n" +
        "1700000340000,30100.00,30100.00,29950.00,29950.00,1\n";
}
