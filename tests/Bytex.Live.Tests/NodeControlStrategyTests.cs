using System.Text.Json;
using Bytex.Core.TradingRuntime;
using Bytex.Core.Model;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Plugins;
using Bytex.Core.Trading;
using Bytex.Live.Control;
using Bytex.Live.Tests.Support;

namespace Bytex.Live.Tests;

// Why (R5.10 over the channel): an operator supervising a node from outside the process can now change what it runs,
// which is the part of the protocol that changes a node rather than reporting it. Two decisions in it are pinned here.
//
// A PATH crosses the channel and never a document. It is the rule the keys already follow - the launcher passes a path
// and the node reads the file - and what a path means is the node's, so a node that was not given a way to read one says
// so rather than guessing.
//
// Every one of the five commands is answered with the same message: whether it was done, why not if it was refused, and
// every strategy with its state and what it is holding. A host that sent a command never has to ask what happened.
public sealed class NodeControlStrategyTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(20);

    private sealed record Rig(TradingNode Node, NodeControlServer Server, string Channel, Instrument Instrument) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            await Node.DisposeAsync();
        }
    }

    private static async Task<Rig> StartAsync(Func<string, Strategy>? fromPath = null, Strategy? initial = null)
    {
        Journal journal = new();
        PluginRegistry registry = new();
        FakeDataClientFactory dataFactory = new(journal);
        FakeExecutionClientFactory execFactory = new(journal);
        registry.AddDataClientFactory(dataFactory);
        registry.AddExecutionClientFactory(execFactory);
        Instrument instrument = TestInstruments.BtcUsdt("FAKE");

        TradingNode node = new(new TradingNodeConfig
        {
            TradingRuntime = new TradingRuntimeConfig { Environment = TradingEnvironment.Live, ModuleHostId = new ModuleHostId("TESTER-001") },
            DataClients = [new ClientEntry("FAKE", "FAKE-DATA", new FakeDataClientConfig())],
            ExecutionClients = [new ClientEntry("FAKE", "FAKE-EXEC", new FakeExecutionClientConfig())],
            HeartbeatInterval = TimeSpan.Zero,
            ReconciliationInterval = TimeSpan.Zero,
            StrategyFromPath = fromPath,
        }, registry);
        node.AddInstrument(instrument);
        if (initial is not null)
        {
            node.AddStrategy(initial);
        }

        string channel = "ctl-" + Guid.NewGuid().ToString("N")[..8];
        NodeControlServer server = new(channel, node, TimeSpan.FromMinutes(10));
        server.Start();
        await node.StartAsync().WaitAsync(_timeout);
        return new Rig(node, server, channel, instrument);
    }

    private static async Task<JsonElement> NextAsync(IAsyncEnumerator<ControlMessage> messages, string type)
    {
        while (await messages.MoveNextAsync())
        {
            if (messages.Current.Type == type)
            {
                return messages.Current.Payload?.Clone() ?? default;
            }
        }

        throw new Xunit.Sdk.XunitException($"the channel closed before a '{type}' message");
    }

    private static IEnumerable<JsonElement> Listed(JsonElement payload) => payload.GetProperty("strategies").EnumerateArray();

    [Fact]
    public async Task The_handshake_says_the_protocol_carries_these_commands()
    {
        // A host reads the version before it relies on any of it.
        await using Rig rig = await StartAsync();
        await using NodeControlClient client = await NodeControlClient.ConnectAsync(rig.Channel, _timeout);
        using CancellationTokenSource limit = new(_timeout);
        await using IAsyncEnumerator<ControlMessage> messages = client.ReadAsync(limit.Token).GetAsyncEnumerator();

        JsonElement hello = await NextAsync(messages, ControlProtocol.Hello);

        Assert.Equal(6, hello.GetProperty("version").GetInt32());
        Assert.Equal(6, ControlProtocol.Version);
    }

    [Fact]
    public async Task Asking_what_a_node_runs_answers_with_every_strategy_and_what_it_holds()
    {
        await using Rig rig = await StartAsync(initial: new ProbeStrategy("Probe-001"));
        await using NodeControlClient client = await NodeControlClient.ConnectAsync(rig.Channel, _timeout);
        using CancellationTokenSource limit = new(_timeout);
        await using IAsyncEnumerator<ControlMessage> messages = client.ReadAsync(limit.Token).GetAsyncEnumerator();
        await NextAsync(messages, ControlProtocol.Hello);

        await client.RequestStrategiesAsync();
        JsonElement payload = await NextAsync(messages, ControlProtocol.Strategies);

        Assert.True(payload.GetProperty("done").GetBoolean());
        JsonElement only = Assert.Single(Listed(payload));
        Assert.Equal("Probe-001", only.GetProperty("id").GetString());
        Assert.Equal("Running", only.GetProperty("state").GetString());
        Assert.Equal(0, only.GetProperty("openOrders").GetInt32());
        Assert.Equal(0, only.GetProperty("openPositions").GetInt32());
    }

    [Fact]
    public async Task A_strategy_is_added_started_stopped_and_removed_over_the_channel()
    {
        // The whole of R5.10 in one exchange, on a node that never stops running.
        await using Rig rig = await StartAsync(fromPath: path => new ProbeStrategy(Path.GetFileNameWithoutExtension(path)));
        await using NodeControlClient client = await NodeControlClient.ConnectAsync(rig.Channel, _timeout);
        using CancellationTokenSource limit = new(_timeout);
        await using IAsyncEnumerator<ControlMessage> messages = client.ReadAsync(limit.Token).GetAsyncEnumerator();
        await NextAsync(messages, ControlProtocol.Hello);

        await client.AddStrategyAsync("strategies/Ema-001.json");
        JsonElement added = await NextAsync(messages, ControlProtocol.Strategies);
        Assert.True(added.GetProperty("done").GetBoolean());
        Assert.Equal("Ready", Assert.Single(Listed(added)).GetProperty("state").GetString());

        await client.StartStrategyAsync("Ema-001");
        JsonElement started = await NextAsync(messages, ControlProtocol.Strategies);
        Assert.Equal("Running", Assert.Single(Listed(started)).GetProperty("state").GetString());

        await client.StopStrategyAsync("Ema-001");
        JsonElement stopped = await NextAsync(messages, ControlProtocol.Strategies);
        Assert.Equal("Stopped", Assert.Single(Listed(stopped)).GetProperty("state").GetString());

        await client.RemoveStrategyAsync("Ema-001");
        JsonElement removed = await NextAsync(messages, ControlProtocol.Strategies);
        Assert.True(removed.GetProperty("done").GetBoolean());
        Assert.Empty(Listed(removed));

        Assert.True(rig.Node.IsRunning);
    }

    [Fact]
    public async Task A_refusal_comes_back_as_a_sentence_rather_than_as_silence()
    {
        await using Rig rig = await StartAsync(initial: new ProbeStrategy("Probe-001"));
        await using NodeControlClient client = await NodeControlClient.ConnectAsync(rig.Channel, _timeout);
        using CancellationTokenSource limit = new(_timeout);
        await using IAsyncEnumerator<ControlMessage> messages = client.ReadAsync(limit.Token).GetAsyncEnumerator();
        await NextAsync(messages, ControlProtocol.Hello);

        await client.RemoveStrategyAsync("Probe-001");
        JsonElement refused = await NextAsync(messages, ControlProtocol.Strategies);

        Assert.False(refused.GetProperty("done").GetBoolean());
        Assert.Contains("stopped before it can be removed", refused.GetProperty("refused").GetString(), StringComparison.Ordinal);
        Assert.Single(Listed(refused));
    }

    [Fact]
    public async Task A_command_missing_what_it_needs_is_answered_and_not_acted_on()
    {
        await using Rig rig = await StartAsync();
        await using NodeControlClient client = await NodeControlClient.ConnectAsync(rig.Channel, _timeout);
        using CancellationTokenSource limit = new(_timeout);
        await using IAsyncEnumerator<ControlMessage> messages = client.ReadAsync(limit.Token).GetAsyncEnumerator();
        await NextAsync(messages, ControlProtocol.Hello);

        // No path: a host that sent nonsense is told, rather than left watching for a change that never comes.
        await client.AddStrategyAsync(string.Empty);
        JsonElement answer = await NextAsync(messages, ControlProtocol.Strategies);

        Assert.False(answer.GetProperty("done").GetBoolean());
        Assert.Contains("needs a path", answer.GetProperty("refused").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_node_that_cannot_read_a_strategy_from_a_path_says_so_over_the_channel()
    {
        await using Rig rig = await StartAsync();
        await using NodeControlClient client = await NodeControlClient.ConnectAsync(rig.Channel, _timeout);
        using CancellationTokenSource limit = new(_timeout);
        await using IAsyncEnumerator<ControlMessage> messages = client.ReadAsync(limit.Token).GetAsyncEnumerator();
        await NextAsync(messages, ControlProtocol.Hello);

        await client.AddStrategyAsync("strategies/ema.json");
        JsonElement answer = await NextAsync(messages, ControlProtocol.Strategies);

        Assert.False(answer.GetProperty("done").GetBoolean());
        Assert.Contains("StrategyFromPath", answer.GetProperty("refused").GetString(), StringComparison.Ordinal);
    }
}
