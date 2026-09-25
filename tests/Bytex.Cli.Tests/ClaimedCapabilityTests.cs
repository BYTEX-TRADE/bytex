using System.Text.Json;
using Bytex.Backtest;
using Bytex.Cli.Tests.Support;

namespace Bytex.Cli.Tests;

// Why: five defects shipped in 0.6.0 and a third party found all of them in an afternoon by running the released
// binary. Every one of the 3,600 tests here called a library, and three of the five were unreachable-in-production
// failures - code that was correct and that no user could get to. A test that hands a component the input the
// production path has to go and fetch cannot see that class of bug at all.
//
// So: for every capability a release CLAIMS, one test that drives it through the command line, which is the surface
// a user actually has. These are slow and few on purpose. What each one asserts is not that the arithmetic is right
// - the unit tests do that - but that the feature is REACHABLE, and that what the run reports about itself is true.
public sealed class ClaimedCapabilityTests
{
    private const string BarType = "BTCUSDT-PERP.SIM-1-MINUTE-LAST-EXTERNAL";

    private const string InstrumentJson = """
        {
          "kind": "CryptoPerpetual",
          "id": "BTCUSDT-PERP.SIM",
          "rawSymbol": "BTCUSDT",
          "assetClass": "crypto",
          "instrumentClass": "swap",
          "quoteCurrency": "USDT",
          "baseCurrency": "BTC",
          "settlementCurrency": "USDT",
          "isInverse": false,
          "pricePrecision": 1,
          "sizePrecision": 3,
          "priceIncrement": "0.1",
          "sizeIncrement": "0.001",
          "marginInit": 0.05,
          "marginMaint": 0.025,
          "makerFee": 0.0002,
          "takerFee": 0.0005,
          "tsEvent": 0,
          "tsInit": 0
        }
        """;

    /// <summary>A minute of bars that rises then falls hard, so a long position both opens and goes underwater.</summary>
    private static string Bars(int count = 60)
    {
        System.Text.StringBuilder sb = new();
        sb.AppendLine("timestamp,open,high,low,close,volume");
        decimal price = 50_000m;
        long ts = 1_700_000_000_000L;
        for (int i = 0; i < count; i++)
        {
            decimal next = i < 10 ? price + 20m : price * 0.988m;
            decimal high = Math.Max(price, next);
            decimal low = Math.Min(price, next);
            sb.AppendLine($"{ts},{price:F1},{high:F1},{low:F1},{next:F1},1000");
            price = next;
            ts += 60_000L;
        }

        return sb.ToString();
    }

    private static string Document(string sizing, decimal leverage) => $$"""
        {
          "schemaVersion": "1.0",
          "id": "claimed",
          "name": "Claimed capability",
          "instruments": [ { "ref": "primary", "instrumentId": "BTCUSDT-PERP.SIM" } ],
          "barTypes": [ { "ref": "main", "instrument": "primary", "step": 1, "aggregation": "minute", "priceType": "last", "source": "external" } ],
          "account": { "leverage": {{leverage}} },
          "nodes": [
            { "id": "bars", "type": "data.bars", "params": { "barType": "main" } },
            { "id": "avg", "type": "ind.ema", "params": { "period": 3 } },
            { "id": "gate", "type": "cond.compare", "params": { "op": "gt", "value": "0" } },
            { "id": "buy", "type": "act.order", "params": { "side": "buy", "orderType": "market", "onlyWhenFlat": true, "sizing": {{sizing}} } }
          ],
          "edges": [
            { "from": "bars:bars", "to": "avg:bars" },
            { "from": "avg:value", "to": "gate:a" },
            { "from": "gate:out", "to": "buy:trigger" }
          ]
        }
        """;

    /// <summary>Loads the instrument and the bars into a catalog and returns its path, all through the CLI.</summary>
    private static async Task<string> CatalogAsync(TempDirectory temp)
    {
        string catalog = temp.Combine("catalog");
        CliResult added = await CliRunner.RunAsync(["catalog", "add-instrument", "--path", catalog, "--file", temp.File("perp.json", InstrumentJson)]);
        Assert.True(added.ExitCode == 0, added.AllOutput);
        CliResult imported = await CliRunner.RunAsync(
            ["catalog", "import-csv", "-p", catalog, "-f", temp.File("bars.csv", Bars()), "-k", "bars", "-i", "BTCUSDT-PERP.SIM", "--bar-type", BarType, "--timestamp-format", "unix_ms"]);
        Assert.True(imported.ExitCode == 0, imported.AllOutput);
        return catalog;
    }

    private static string RunConfig(TempDirectory temp, string catalog, string documentPath, decimal leverage, bool liquidate, string? outputDir = null) =>
        temp.File($"run-{leverage}-{liquidate}.json", JsonSerializer.Serialize(new
        {
            engine = new { runId = "claimed", kernel = new { traderId = "BACKTESTER-001" } },
            venues = new[]
            {
                new
                {
                    venue = "SIM",
                    accountType = "margin",
                    startingBalances = new[] { "10000 USDT" },
                    defaultLeverage = leverage,
                    liquidate,
                },
            },
            data = new[] { new { catalogPath = catalog, dataKind = "bars", barType = BarType } },
            strategies = new[] { new { providerId = "bytex.document", name = "doc", payload = new { documentPath, strategyId = "Doc-001" } } },
            outputDirectory = outputDir,
        }));

    private static async Task<BacktestResult> ResultOfAsync(TempDirectory temp, string config, string outputDir)
    {
        CliResult run = await CliRunner.RunAsync(["backtest", "--config", config], timeout: TimeSpan.FromMinutes(3));
        Assert.True(run.ExitCode == 0, run.AllOutput);
        string json = Directory.GetFiles(outputDir, "result.json", SearchOption.AllDirectories).Single();
        BacktestResult? result = JsonSerializer.Deserialize<BacktestResult>(File.ReadAllText(json), Core.Serialization.BytexJson.Options);
        Assert.NotNull(result);
        return result!;
    }

    [Fact]
    public async Task Leverage_on_a_document_changes_what_a_run_takes_and_says_so_in_its_result()
    {
        // The claim: a document states its leverage and that changes the size it can take. Reachable only if the
        // document field, the run assembler, the account, the venue's hold and sizing all agree - which is five
        // components no unit test crosses.
        using TempDirectory temp = new();
        string catalog = await CatalogAsync(temp);
        string document = temp.File("doc-20x.json", Document("""{ "mode": "notional", "value": "150000" }""", 20m));
        string output = temp.Combine("out-20x");
        BacktestResult leveraged = await ResultOfAsync(temp, RunConfig(temp, catalog, document, 20m, liquidate: false, output), output);

        string document1x = temp.File("doc-1x.json", Document("""{ "mode": "notional", "value": "150000" }""", 1m));
        string output1x = temp.Combine("out-1x");
        BacktestResult plain = await ResultOfAsync(temp, RunConfig(temp, catalog, document1x, 1m, liquidate: false, output1x), output1x);

        Assert.Empty(leveraged.FaultedStrategies);
        Assert.Empty(plain.FaultedStrategies);
        Assert.True(leveraged.TotalOrders > 0, "the leveraged run placed nothing, so nothing was measured");
        Assert.True(
            leveraged.Trades.ClosedPositions + leveraged.Trades.OpenPositions > 0,
            "the leveraged run opened no position");

        // The whole point of the feature: the same document, the same data, a bigger position.
        Assert.True(
            leveraged.Currencies[0].TotalCommissions > plain.Currencies[0].TotalCommissions,
            $"leverage changed nothing a user can see: fees {leveraged.Currencies[0].TotalCommissions} at 20x against {plain.Currencies[0].TotalCommissions} at 1x");
    }

    [Fact]
    public async Task A_document_asking_for_more_leverage_than_the_venue_grants_says_so_and_fails_the_run()
    {
        // The claim: a venue granting less refuses to trade the strategy rather than sizing it down silently. The
        // defect this replaces: it exited 0 with no orders and one log line, which reads like a strategy that found
        // nothing to do.
        using TempDirectory temp = new();
        string catalog = await CatalogAsync(temp);
        string document = temp.File("doc-asks-10x.json", Document("""{ "mode": "notional", "value": "50000" }""", 10m));
        string output = temp.Combine("out-refused");

        CliResult run = await CliRunner.RunAsync(
            ["backtest", "--config", RunConfig(temp, catalog, document, 1m, liquidate: false, output)],
            timeout: TimeSpan.FromMinutes(3));

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("faulted", run.AllOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Doc-001", run.AllOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Liquidation_is_reachable_by_a_document_and_reported_where_a_user_looks()
    {
        // The claim 0.5 made and 0.6 corrected the arithmetic for. Reachable only if leverage, sizing and the
        // maintenance margin all line up; it was measured as unreachable twice on wrong premises.
        using TempDirectory temp = new();
        string catalog = await CatalogAsync(temp);
        string document = temp.File("doc-liq.json", Document("""{ "mode": "notional", "value": "150000" }""", 20m));
        string output = temp.Combine("out-liq");

        BacktestResult result = await ResultOfAsync(temp, RunConfig(temp, catalog, document, 20m, liquidate: true, output), output);

        Assert.NotEmpty(result.Liquidations);
        Assert.Contains(SimulationCapabilities.Liquidation, result.Simulation);
        Assert.Contains(SimulationCapabilities.Liquidation, result.Applied);

        // And a user reading the folder rather than the JSON finds it there too.
        Assert.True(
            Directory.GetFiles(output, "liquidations.csv", SearchOption.AllDirectories).Length > 0,
            "a liquidated run wrote no liquidations.csv");
    }

    [Fact]
    public async Task A_run_reports_only_the_capabilities_it_actually_applied()
    {
        // Simulation is what the venue could do; Applied is what this run did. Anything shown to a user has to come
        // from the second, and the two must not drift into agreeing by accident.
        using TempDirectory temp = new();
        string catalog = await CatalogAsync(temp);
        string document = temp.File("doc-applied.json", Document("""{ "mode": "notional", "value": "20000" }""", 20m));
        string output = temp.Combine("out-applied");

        BacktestResult result = await ResultOfAsync(temp, RunConfig(temp, catalog, document, 20m, liquidate: true, output), output);

        // Bars carry no book, so a bar-driven run can never have walked one however the venue was configured.
        Assert.Contains(SimulationCapabilities.BookDepth, result.Simulation);
        Assert.DoesNotContain(SimulationCapabilities.BookDepth, result.Applied);
        Assert.Contains(SimulationCapabilities.PartialFills, result.Simulation);
    }
}
