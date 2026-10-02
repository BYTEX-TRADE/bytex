using System.Text.Json;
using Bytex.Cli.Tests.Support;
using Bytex.Examples.Nodes;

namespace Bytex.Cli.Tests;

// Why (R13.8): a plugin may bring node types a document uses, and the library half of that was reachable from a host
// that composes its own catalog. The tool nobody replaces is this one, and `documents validate`, `documents catalog` and
// `documents schema` each built their own catalog from the built-in types - so the feature existed and a user could not
// get at it. Worse than absent: `validate` would refuse a document that `backtest` then ran.
//
// This drives the real executable with a real plugin directory, which is the only way to find that out.
public sealed class PluginNodeCommandTests
{
    /// <summary>The plugin as a host receives it: one assembly in a directory, nothing else.</summary>
    private static string PluginDirectory(TempDirectory temp)
    {
        string dir = temp.Combine("plugins");
        Directory.CreateDirectory(dir);
        const string assembly = "Bytex.Examples.Nodes.dll";
        File.Copy(Path.Combine(AppContext.BaseDirectory, assembly), Path.Combine(dir, assembly));
        return dir;
    }

    [Fact]
    public async Task A_document_using_a_plugins_nodes_validates_with_the_plugin_and_is_refused_without_it()
    {
        using TempDirectory temp = new();
        string document = temp.File("first-wide-bar.json", ExampleNodesPlugin.ExampleDocumentJson());

        CliResult without = await CliRunner.RunAsync(["documents", "validate", "--document", document]);

        Assert.Equal(1, without.ExitCode);
        Assert.Contains("NODE_PROVIDER_MISSING", without.StdOut, StringComparison.Ordinal);
        Assert.Contains("Bytex.Examples.Nodes (example) 1.0 or later", without.StdOut, StringComparison.Ordinal);

        // And nothing else: an unloaded plugin reports the nodes it supplies, not every edge that touched them.
        Assert.DoesNotContain("EDGE_DANGLING", without.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("INPUT_REQUIRED", without.StdOut, StringComparison.Ordinal);

        CliResult with = await CliRunner.RunAsync(["--plugins", PluginDirectory(temp), "documents", "validate", "--document", document]);

        Assert.True(with.ExitCode == 0, with.AllOutput);
        Assert.Contains("valid (0 warnings)", with.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_catalog_and_the_schema_carry_a_plugins_types_only_where_it_is_loaded()
    {
        using TempDirectory temp = new();
        string plugins = PluginDirectory(temp);

        CliResult catalog = await CliRunner.RunAsync(["--plugins", plugins, "documents", "catalog"]);
        CliResult schema = await CliRunner.RunAsync(["--plugins", plugins, "documents", "schema"]);
        CliResult builtIn = await CliRunner.RunAsync(["documents", "catalog"]);

        // A builder reads one of these two to draw a palette and to check a document before the engine sees it, so a
        // plugin's type has to appear in both or the node it brought cannot be used by anything but hand-written JSON.
        Assert.Contains("example.range", catalog.StdOut, StringComparison.Ordinal);
        Assert.Contains("example.oncePerSession", catalog.StdOut, StringComparison.Ordinal);
        Assert.Contains("example.range", schema.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("example.range", builtIn.StdOut, StringComparison.Ordinal);
        Assert.Contains("ind.rsi", builtIn.StdOut, StringComparison.Ordinal);
    }
    [Fact]
    public async Task A_backtest_runs_a_document_whose_nodes_came_from_a_plugin()
    {
        // The other half of the promise, and the half that would have been easy to leave broken: validating a document
        // is a report, running it is the product. The strategy provider is registered with the composed catalog, so a
        // plugin's node has to reach the runtime through a configuration that names nothing but a file path.
        using TempDirectory temp = new();
        string catalog = temp.Combine("catalog");
        await CliRunner.RunAsync(["catalog", "add-instrument", "--path", catalog, "--file", temp.File("btcusdt.json", InstrumentJson)]);
        await CliRunner.RunAsync(["catalog", "import-csv", "-p", catalog, "-f", temp.File("bars.csv", Bars()), "-k", "bars", "-i", "bx-market:v2/SIM/BTCUSDT", "--candle-series", CandleSeries, "--timestamp-format", "unix_ms"]);
        string document = temp.File("first-wide-bar.json", ExampleNodesPlugin.ExampleDocumentJson());

        string config = temp.File("backtest.json", JsonSerializer.Serialize(new
        {
            engine = new { runId = "plugin-nodes", tradingRuntime = new { moduleHostId = "BACKTESTER-001" } },
            venues = new[] { new { venue = "SIM", accountType = "cash", startingBalances = new[] { "1000000 USDT", "10 BTC" } } },
            data = new[] { new { catalogPath = catalog, dataKind = "bars", candleSeries = CandleSeries } },
            strategies = new[]
            {
                new
                {
                    providerId = "bytex.document",
                    name = "First wide bar (document)",
                    payload = new { documentPath = document, strategyId = "WideBarDoc-001" },
                },
            },
        }));

        CliResult without = await CliRunner.RunAsync(["--log-level", "Warning", "backtest", "--config", config], temp.Path);

        // Without the plugin the run is refused rather than run with the nodes missing, and by the same name the
        // validator uses: a run that quietly did nothing would look exactly like a strategy whose conditions never came
        // true.
        Assert.NotEqual(0, without.ExitCode);
        Assert.Contains("Bytex.Examples.Nodes (example) 1.0 or later", without.AllOutput, StringComparison.Ordinal);

        CliResult result = await CliRunner.RunAsync(["--log-level", "Warning", "--plugins", PluginDirectory(temp), "backtest", "--config", config], temp.Path);

        Assert.True(result.ExitCode == 0, result.AllOutput);
        Assert.Contains("Backtest plugin-nodes (BACKTESTER-001)", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("Iterations:  6", result.StdOut, StringComparison.Ordinal);

        // The wide bar is the third of the six, so the plugin's condition fired and the order node acted on it.
        Assert.DoesNotContain("Orders:      0", result.StdOut, StringComparison.Ordinal);
    }

    // The instrument and the bars the backtest needs, the same six one-minute bars the other document tests import.
    // The third of them travels 110 points on a 30,150 close - wide by the document's own threshold.
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

    private static string Bars() =>
        "timestamp,open,high,low,close,volume\n" +
        "1700000040000,30000.00,30010.00,29990.00,30000.00,1\n" +
        "1700000100000,30000.00,30060.00,30000.00,30050.00,1\n" +
        "1700000160000,30050.00,30160.00,30050.00,30150.00,1\n" +
        "1700000220000,30150.00,30260.00,30150.00,30250.00,1\n" +
        "1700000280000,30250.00,30250.00,30100.00,30100.00,1\n" +
        "1700000340000,30100.00,30100.00,29950.00,29950.00,1\n";
}
