using System.Text.Json;
using Bytex.Cli.Tests.Support;
using Bytex.Live.Control;

namespace Bytex.Cli.Tests;

// Why: adding a strategy to a running node (R5.10, control protocol 6) shipped in 0.9.0 and could not be reached from
// the tool that ships it. The engine leaves the meaning of a path to the host - a path, not a strategy, is what crosses
// a control channel - and this command supplied nothing, so `add-strategy` was refused on every node it ever started,
// with a message about a configuration the operator cannot write in JSON.
//
// Driven over the real control channel against the real executable, because "the node reports it can read strategies"
// and "a strategy is actually added" are different claims and only the second one is the feature.
public sealed class RunCommandAddStrategyTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

    /// <summary>A sandbox node: no credentials, no plugins, so the test is about the command and nothing else.</summary>
    private static string NodeConfig() => JsonSerializer.Serialize(new
    {
        tradingRuntime = new { moduleHostId = "TESTER-004", environment = "sandbox" },
        executionClients = new[] { new { factory = "SANDBOX", clientId = "BINANCE-SANDBOX", config = new { venue = "BINANCE", startingBalances = new[] { "100000 USDT" } } } },
        heartbeatInterval = "00:00:00",
    });

    /// <summary>
    /// One strategy definition in a file: the same object as an entry of the node's own `strategies`. It names the
    /// document provider, which this command registers itself, so the test needs no plugin - the first version of it
    /// named an example strategy CLASS and failed for want of an assembly rather than for want of the fix.
    /// </summary>
    private static string Definition(string strategyId) => JsonSerializer.Serialize(new
    {
        providerId = "bytex.document",
        name = "EMA cross (added while running)",
        payload = new
        {
            documentPath = Path.Combine(AppContext.BaseDirectory, "Examples", "ema-cross.json"),
            strategyId,
        },
    });

    private static async Task<(bool Ok, string? Reason, string[] Ids, string Output)> AddAsync(string config, string definitionPath)
    {
        string channel = "bytex-cli-" + Guid.NewGuid().ToString("N")[..8];
        Task<CliResult> run = CliRunner.RunAsync(["run", "--config", config, "--control", channel, "--duration", "00:00:45"], timeout: TimeSpan.FromMinutes(3));
        try
        {
            await using NodeControlClient client = await NodeControlClient.ConnectAsync(channel, _timeout);
            using CancellationTokenSource limit = new(_timeout);
            await using IAsyncEnumerator<ControlMessage> messages = client.ReadAsync(limit.Token).GetAsyncEnumerator();

            // Wait for the node to be up before asking it for anything: a host may connect while it is still starting.
            await client.RequestStatusAsync();
            bool running = false;
            while (!running && await messages.MoveNextAsync())
            {
                if (messages.Current.Type is not (ControlProtocol.Status or ControlProtocol.Heartbeat))
                {
                    continue;
                }

                running = messages.Current.Payload!.Value.GetProperty("running").GetBoolean();
                if (!running)
                {
                    await client.RequestStatusAsync();
                }
            }

            await client.AddStrategyAsync(definitionPath);
            while (await messages.MoveNextAsync())
            {
                ControlMessage message = messages.Current;
                if (message.Type is ControlProtocol.Status or ControlProtocol.Heartbeat)
                {
                    continue;
                }

                JsonElement payload = message.Payload!.Value.Clone();
                bool ok = payload.TryGetProperty("done", out JsonElement okElement) && okElement.GetBoolean();
                string? reason = payload.TryGetProperty("refused", out JsonElement r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
                string[] ids = payload.TryGetProperty("strategies", out JsonElement list) && list.ValueKind == JsonValueKind.Array
                    ? [.. list.EnumerateArray().Select(s => s.TryGetProperty("id", out JsonElement id) ? id.GetString() ?? string.Empty : string.Empty)]
                    : [];

                return (ok, reason, ids, (await run).AllOutput);
            }

            throw new Xunit.Sdk.XunitException("the node closed its channel without answering add-strategy: " + (await run).AllOutput);
        }
        finally
        {
            await run;
        }
    }

    /// <summary>
    /// The defect, as a test: a node started by this command adds a strategy from a path. Before the fix the answer was
    /// always a refusal naming <c>StrategyFromPath</c>, which is not something an operator can put in a JSON file.
    /// </summary>
    [Fact]
    public async Task ANodeStartedByTheCliAddsAStrategyFromAPath()
    {
        using TempDirectory temp = new();
        string config = temp.File("node.json", NodeConfig());
        string definition = temp.File("added.json", Definition("AddedByControl-001"));

        (bool ok, string? reason, string[] ids, string output) = await AddAsync(config, definition);

        Assert.True(ok, "add-strategy was refused: " + reason + "\n" + output);
        Assert.Contains("AddedByControl-001", ids, StringComparer.Ordinal);
        Assert.Null(reason);

        // And the node said so in its log, because a strategy appearing in a running node is worth a line.
        Assert.Contains("add-strategy", output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A path that is not a strategy definition is refused with something to act on, rather than accepted and then
    /// failing somewhere inside the node. A definition read from a file is held to the same rule as one read from the
    /// configuration: a member this engine does not know is refused, not skipped.
    /// </summary>
    [Fact]
    public async Task APathThatIsNotADefinitionIsRefusedWithAReason()
    {
        using TempDirectory temp = new();
        string config = temp.File("node.json", NodeConfig());
        string definition = temp.File("wrong.json", """{ "providerId": "bytex.importable", "name": "Bytex.Examples.Strategies.EmaCross", "payload": {}, "startNow": true }""");

        (bool ok, string? reason, _, string output) = await AddAsync(config, definition);

        Assert.False(ok, "a file with an unknown member was accepted: " + output);
        Assert.NotNull(reason);
        Assert.Contains("startNow", reason, StringComparison.Ordinal);
    }
}
