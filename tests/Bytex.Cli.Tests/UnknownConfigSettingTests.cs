using Bytex.Cli.Tests.Support;

namespace Bytex.Cli.Tests;

// Why: a setting this engine does not know used to be skipped in silence, so a configuration file could ask for
// something and get the opposite without a word. `"testnet": true` is the case that proved it: nothing here reads that
// member, so the node started, reported nothing unusual, and connected to the real venue with real money. The cost of
// being wrong in a configuration file is the whole account, and the cost of being strict is a typo that has to be
// corrected - so unknown members are refused by name.
//
// Refused before anything connects, which is what the tests check: the configurations below name a venue but the run
// never gets far enough to reach it, and an exit code alone would not prove that, so the message is asserted too.
public sealed class UnknownConfigSettingTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    // Modelled on examples/configs/live-bybit-ema-cross.json, so the only thing wrong with it is what a test adds.
    private static string NodeConfig(string extraTopLevel = "", string extraClientSetting = "") => $$"""
    {
      "tradingRuntime": { "moduleHostId": "TESTER-001", "environment": "live" },
      {{extraTopLevel}}
      "dataClients": [
        { "factory": "BYBIT", "clientId": "BYBIT", "config": { "productType": "linear"{{extraClientSetting}} } }
      ],
      "executionClients": [],
      "strategies": []
    }
    """;

    /// <summary>
    /// The reported case, exactly: <c>"testnet": true</c> inside a client's own settings. Nothing reads it, so before
    /// this it meant nothing at all - and "nothing" here is a live venue.
    /// </summary>
    [Fact]
    public async Task ATestnetSettingIsRefusedRatherThanIgnored()
    {
        string config = _temp.File("node.json", NodeConfig(extraClientSetting: ", \"testnet\": true"));

        CliResult result = await CliRunner.RunAsync(["run", "--config", config, "--duration", "00:00:03"], timeout: TimeSpan.FromMinutes(2));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("testnet", result.AllOutput, StringComparison.OrdinalIgnoreCase);

        // And it did not get as far as a venue: a node that had started would say so.
        Assert.DoesNotContain("running", result.AllOutput, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The same rule at the top level of the file, where a misremembered node setting would sit.</summary>
    [Fact]
    public async Task AnUnknownNodeSettingIsRefusedByName()
    {
        string config = _temp.File("node.json", NodeConfig(extraTopLevel: "\"reconcileOnStartup\": false,"));

        CliResult result = await CliRunner.RunAsync(["run", "--config", config, "--duration", "00:00:03"], timeout: TimeSpan.FromMinutes(2));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("reconcileOnStartup", result.AllOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the message says what to do about it, because a refusal that only says "invalid" sends somebody looking
    /// through a whole file for a member they believed was right.
    /// </summary>
    [Fact]
    public async Task TheRefusalSaysWhatToDoAboutIt()
    {
        string config = _temp.File("node.json", NodeConfig(extraClientSetting: ", \"testnet\": true"));

        CliResult result = await CliRunner.RunAsync(["run", "--config", config, "--duration", "00:00:03"], timeout: TimeSpan.FromMinutes(2));

        Assert.Contains("not a setting", result.AllOutput, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The other half, and the one that stops this being a rule nobody can satisfy: the same file WITHOUT the member
    /// gets past reading its configuration. It is not asked to trade - only to get further than the parser did above.
    /// </summary>
    [Fact]
    public async Task AConfigurationWithOnlyKnownSettingsIsNotRefused()
    {
        string config = _temp.File("node.json", NodeConfig());

        CliResult result = await CliRunner.RunAsync(["run", "--config", config, "--duration", "00:00:03"], timeout: TimeSpan.FromMinutes(2));

        Assert.DoesNotContain("not a setting", result.AllOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("does not have", result.AllOutput, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A backtest reads a file a person wrote too, and got the same treatment. Checked here rather than assumed from
    /// the node test, because they are different call sites and only one of them was the reported one.
    /// </summary>
    [Fact]
    public async Task ABacktestConfigurationIsReadTheSameWay()
    {
        string config = _temp.File("backtest.json", """
        {
          "engine": { "runId": "unknown-setting", "tradingRuntime": { "moduleHostId": "BACKTESTER-001" } },
          "venues": [ { "venue": "SIM", "accountType": "cash", "startingBalances": ["100000 USDT"] } ],
          "data": [],
          "strategies": [],
          "speculative": true
        }
        """);

        CliResult result = await CliRunner.RunAsync(["backtest", "--config", config], timeout: TimeSpan.FromMinutes(2));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("speculative", result.AllOutput, StringComparison.Ordinal);
    }
}
