using System.Text.Json;
using Bytex.Cli.Tests.Support;
using Bytex.Core.Engines;
using Bytex.Live.Control;

namespace Bytex.Cli.Tests;

// Why: a host that supervises nodes it did not write sets their limits from outside, and the documentation offers
// three ways to do it - the node's JSON, the command line, and the control channel. These tests use the last one to
// read back what the first two set, with the real executable, so a renamed option or a limit that never reaches the
// risk engine fails here rather than in whatever a supervising application shows an operator.
public sealed class RunCommandLimitsTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

    /// <summary>A sandbox node: it needs no credentials and no plugins, so the test is about the options and nothing else.</summary>
    private static string NodeConfig(object? riskEngine = null, bool startHalted = false) => JsonSerializer.Serialize(new
    {
        kernel = new { traderId = "TESTER-003", environment = "sandbox", riskEngine },
        executionClients = new[] { new { factory = "SANDBOX", clientId = "BINANCE-SANDBOX", config = new { venue = "BINANCE", startingBalances = new[] { "100000 USDT" } } } },
        heartbeatInterval = "00:00:00",
        startHalted,
    });

    /// <summary>
    /// Runs a node with a control channel and answers with what it reports about itself once it is running. A host
    /// may connect while a node is still connecting to its venues, so the first status can be of a node that has not
    /// started yet; what this test is about is the node that came up.
    /// </summary>
    private static async Task<JsonElement> StatusOfAsync(string config, params string[] options)
    {
        // How long the node is given to come up and answer: a cold runner needs longer than a developer's machine,
        // and the run is only as long as the answer needs.
        string channel = "bytex-cli-" + Guid.NewGuid().ToString("N")[..8];
        Task<CliResult> run = CliRunner.RunAsync(["run", "--config", config, "--control", channel, "--duration", "00:00:45", .. options], timeout: TimeSpan.FromMinutes(3));
        try
        {
            await using NodeControlClient client = await NodeControlClient.ConnectAsync(channel, _timeout);
            using CancellationTokenSource limit = new(_timeout);
            await using IAsyncEnumerator<ControlMessage> messages = client.ReadAsync(limit.Token).GetAsyncEnumerator();
            JsonElement? last = null;
            await client.RequestStatusAsync();
            while (await messages.MoveNextAsync())
            {
                if (messages.Current.Type is not (ControlProtocol.Status or ControlProtocol.Heartbeat))
                {
                    continue;
                }

                last = messages.Current.Payload!.Value.Clone();
                if (last.Value.GetProperty("running").GetBoolean())
                {
                    return last.Value;
                }

                await client.RequestStatusAsync();
            }

            return last ?? throw new Xunit.Sdk.XunitException("the node closed its channel before it reported a status: " + (await run).AllOutput);
        }
        finally
        {
            // What the node printed, for whoever reads a failure here. The exit code is not the subject: a run cut
            // short by its own duration while it was still starting says nothing about the options under test.
            CliResult result = await run;
            Assert.Contains("TESTER-003", result.AllOutput, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task What_the_node_configuration_says_about_its_limits_reaches_the_risk_engine()
    {
        using TempDirectory temp = new();
        string config = temp.File("node.json", NodeConfig(riskEngine: new
        {
            limits = new
            {
                maxLossPerPeriod = "1000 USDT",
                lossPeriod = "06:00:00",
                maxExposure = "50%",
                maxOpenPositions = 5,
                maxWorkingOrders = 50,
            },
        }));

        JsonElement limits = (await StatusOfAsync(config)).GetProperty("limits");

        Assert.Equal(RiskLimit.Parse("1000 USDT").ToString(), limits.GetProperty("maxLossPerPeriod").GetString());
        Assert.Equal("06:00:00", limits.GetProperty("lossPeriod").GetString());
        Assert.Equal("50%", limits.GetProperty("maxExposure").GetString());
        Assert.Equal(5, limits.GetProperty("maxOpenPositions").GetInt32());
        Assert.Equal(50, limits.GetProperty("maxWorkingOrders").GetInt32());
    }

    [Fact]
    public async Task The_command_line_sets_a_limit_the_configuration_never_mentioned_and_overrides_one_it_did()
    {
        using TempDirectory temp = new();
        string config = temp.File("node.json", NodeConfig(riskEngine: new { limits = new { maxWorkingOrders = 50 } }));

        JsonElement status = await StatusOfAsync(config,
            "--max-loss", "250 USDT",
            "--loss-period", "01:00:00",
            "--max-exposure", "10%",
            "--max-open-positions", "2",
            "--max-open-positions-per-instrument", "1",
            "--max-working-orders", "7",
            "--max-working-orders-per-instrument", "3");

        JsonElement limits = status.GetProperty("limits");
        Assert.Equal(RiskLimit.Parse("250 USDT").ToString(), limits.GetProperty("maxLossPerPeriod").GetString());
        Assert.Equal("01:00:00", limits.GetProperty("lossPeriod").GetString());
        Assert.Equal("10%", limits.GetProperty("maxExposure").GetString());
        Assert.Equal(2, limits.GetProperty("maxOpenPositions").GetInt32());
        Assert.Equal(1, limits.GetProperty("maxOpenPositionsPerInstrument").GetInt32());
        Assert.Equal(7, limits.GetProperty("maxWorkingOrders").GetInt32());
        Assert.Equal(3, limits.GetProperty("maxWorkingOrdersPerInstrument").GetInt32());
    }

    [Fact]
    public async Task A_node_started_halted_says_so_and_keeps_running()
    {
        using TempDirectory temp = new();
        string config = temp.File("node.json", NodeConfig());

        JsonElement status = await StatusOfAsync(config, "--halted");

        Assert.True(status.GetProperty("halted").GetBoolean());
        Assert.Equal("Halted", status.GetProperty("tradingState").GetString());
        Assert.True(status.GetProperty("running").GetBoolean());
    }

    [Fact]
    public async Task A_node_whose_configuration_starts_it_halted_needs_no_flag()
    {
        using TempDirectory temp = new();
        string config = temp.File("node.json", NodeConfig(startHalted: true));

        JsonElement status = await StatusOfAsync(config);

        Assert.True(status.GetProperty("halted").GetBoolean());
    }

    [Fact]
    public async Task Display_prices_reaches_the_node_from_the_command_line()
    {
        // This node holds no instruments of its own, which is what it says: the flag reached it, and there was
        // nothing to show. A node with instruments subscribes a quote for each of them (see the node's own tests).
        using TempDirectory temp = new();
        string config = temp.File("node.json", NodeConfig());

        CliResult result = await CliRunner.RunAsync(["run", "--config", config, "--display-prices", "--duration", "00:00:30"], timeout: TimeSpan.FromMinutes(3));

        Assert.Contains("Display prices:", result.AllOutput, StringComparison.Ordinal);
        Assert.Contains("no price to show", result.AllOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_limit_that_is_not_a_limit_stops_the_run_and_says_what_it_should_look_like()
    {
        using TempDirectory temp = new();
        string config = temp.File("node.json", NodeConfig());

        CliResult result = await CliRunner.RunAsync(["run", "--config", config, "--max-loss", "a lot", "--duration", "00:00:01"]);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("--max-loss", result.AllOutput, StringComparison.Ordinal);
        Assert.Contains("1000 USDT", result.AllOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("Trading node TESTER-003 running", result.AllOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task What_a_loss_limit_does_when_it_is_reached_is_reported_and_settable()
    {
        // The default is the protection: the node stops trading by itself. A host that wants the old behaviour - one
        // order denied at a time - says so, and either way the node reports which it is holding, in the words the
        // configuration uses, so a host can send back what it read.
        using TempDirectory temp = new();
        string config = temp.File("node.json", NodeConfig());

        JsonElement byDefault = await StatusOfAsync(config, "--max-loss", "250 USDT");
        Assert.Equal("stopTrading", byDefault.GetProperty("limits").GetProperty("onLossLimit").GetString());

        JsonElement asked = await StatusOfAsync(config, "--max-loss", "250 USDT", "--on-loss-limit", "deny-adds");
        Assert.Equal("denyAdds", asked.GetProperty("limits").GetProperty("onLossLimit").GetString());

        // And the one that closes what is open, which has to be asked for by name.
        JsonElement flatten = await StatusOfAsync(config, "--max-loss", "250 USDT", "--on-loss-limit", "flatten");
        Assert.Equal("flatten", flatten.GetProperty("limits").GetProperty("onLossLimit").GetString());
    }

    [Fact]
    public async Task A_configuration_says_what_a_breach_does_and_the_command_line_overrides_it()
    {
        using TempDirectory temp = new();
        string config = temp.File("node.json", NodeConfig(riskEngine: new { limits = new { maxLossPerPeriod = "1000 USDT", onLossLimit = "denyAdds" } }));

        Assert.Equal("denyAdds", (await StatusOfAsync(config)).GetProperty("limits").GetProperty("onLossLimit").GetString());
        Assert.Equal("stopTrading", (await StatusOfAsync(config, "--on-loss-limit", "stop-trading")).GetProperty("limits").GetProperty("onLossLimit").GetString());
    }

    [Fact]
    public async Task A_breach_action_that_is_not_one_stops_the_run_and_says_what_it_should_look_like()
    {
        using TempDirectory temp = new();
        string config = temp.File("node.json", NodeConfig());

        CliResult result = await CliRunner.RunAsync(["run", "--config", config, "--on-loss-limit", "panic", "--duration", "00:00:01"]);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("--on-loss-limit", result.AllOutput, StringComparison.Ordinal);
        Assert.Contains("stop-trading", result.AllOutput, StringComparison.Ordinal);
        Assert.Contains("flatten", result.AllOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("Trading node TESTER-003 running", result.AllOutput, StringComparison.Ordinal);
    }
}
