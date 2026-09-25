using System.Text.Json;
using Bytex.Core.Kernel;
using Bytex.Core.Model;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Plugins;
using Bytex.Live.Control;
using Bytex.Live.Sandbox;
using Bytex.Live.Tests.Support;

namespace Bytex.Live.Tests;

// Why: a paper node's fills are worth very different amounts depending on what they were matched against - a live
// book, a quote, or a share of a bar - so the control view carries that per instrument and a host shows it. On a real
// paper node the array came back EMPTY: the page had the field and nothing to put in it.
//
// The client's own report is covered in SandboxExecutionClientTests, where the instrument is in the cache before the
// venue connects. That is not how a node gets its instruments. Every shipped adapter loads its catalog and publishes
// it from inside ConnectAsync, and on a live node that hands it to the kernel loop to be applied on the kernel
// thread - so the venue may connect, and the view may be asked, before any instrument has landed. This file is the
// view asked for on a node assembled the way a real one is.
public sealed class NodeControlMatchingTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(20);

    private static async Task<JsonElement> NextAsync(IAsyncEnumerator<ControlMessage> messages, string type)
    {
        while (await messages.MoveNextAsync())
        {
            if (messages.Current.Type == type)
            {
                return messages.Current.Payload!.Value.Clone();
            }
        }

        throw new Xunit.Sdk.XunitException($"the channel closed before a '{type}' message");
    }

    /// <summary>
    /// Asks for the view until it says what is expected, or until the deadline - so a view that only becomes right
    /// once something has drained is not read as wrong, and one that never becomes right still fails.
    /// </summary>
    private static async Task<JsonElement> ViewWhenAsync(NodeControlClient client, IAsyncEnumerator<ControlMessage> messages, Func<JsonElement, bool> ready)
    {
        DateTime deadline = DateTime.UtcNow + _timeout;
        while (true)
        {
            await client.RequestViewAsync();
            JsonElement view = await NextAsync(messages, ControlProtocol.View);
            if (ready(view) || DateTime.UtcNow > deadline)
            {
                return view;
            }

            await Task.Delay(50);
        }
    }

    private static JsonElement? Matching(JsonElement view) =>
        view.TryGetProperty("venues", out JsonElement venues) && venues.ValueKind == JsonValueKind.Array
            ? venues
            : null;

    [Fact]
    public async Task A_paper_venue_says_what_it_is_matching_against_for_instruments_its_adapter_published()
    {
        // The instrument is NOT put into the cache by hand. It arrives the way a venue's own instruments arrive: the
        // data client publishes it while connecting, and the live kernel loop applies it on the kernel thread.
        CurrencyPair instrument = TestInstruments.BtcUsdt("FAKE");
        Journal journal = new();
        PluginRegistry registry = new();
        registry.AddDataClientFactory(new FakeDataClientFactory(journal)
        {
            Configure = client => client.InstrumentsOnConnect.Add(instrument),
        });
        registry.AddExecutionClientFactory(new SandboxExecutionClientFactory());

        await using TradingNode node = new(new TradingNodeConfig
        {
            Kernel = new KernelConfig { Environment = TradingEnvironment.Live, TraderId = new TraderId("MATCH-001") },
            DataClients = [new ClientEntry("FAKE", "FAKE-DATA", new FakeDataClientConfig())],
            ExecutionClients =
            [
                new ClientEntry("SANDBOX", "FAKE-PAPER", new SandboxExecutionClientConfig
                {
                    Venue = "FAKE",
                    StartingBalances = ["100000 USDT"],
                }),
            ],
            HeartbeatInterval = TimeSpan.Zero,
        }, registry);

        string channel = "bytex-matching-test-" + Guid.NewGuid().ToString("N");
        await using NodeControlServer server = new(channel, node, TimeSpan.FromMinutes(10));
        server.Start();
        await node.StartAsync().WaitAsync(_timeout);
        await using NodeControlClient control = await NodeControlClient.ConnectAsync(channel, _timeout);
        using CancellationTokenSource limit = new(TimeSpan.FromSeconds(60));
        await using IAsyncEnumerator<ControlMessage> messages = control.ReadAsync(limit.Token).GetAsyncEnumerator();

        // The FIRST view, not one polled for until it agrees. A host draws its page when it connects, and what was
        // reported from a real paper node was an empty array - looked at once. Polling here would hide that.
        await control.RequestViewAsync();
        JsonElement view = await NextAsync(messages, ControlProtocol.View);
        JsonElement venue = Assert.Single(Matching(view)!.Value.EnumerateArray());
        Assert.Equal("FAKE", venue.GetProperty("venue").GetString());

        // And the instrument its adapter published has to be in it. This is the array that came back empty.
        JsonElement row = Assert.Single(venue.GetProperty("instruments").EnumerateArray());
        Assert.Equal(instrument.Id.ToString(), row.GetProperty("instrumentId").GetString());
        Assert.False(string.IsNullOrEmpty(row.GetProperty("against").GetString()));
    }

    [Fact]
    public async Task A_paper_venue_named_after_no_data_client_holds_nothing_and_says_so_rather_than_vanishing()
    {
        // The configuration trap behind an empty array, and the one shape of it a host can be handed without any
        // race being involved: the paper venue is named something the data clients do not publish under, so it
        // holds no instruments, matches nothing, and fills nothing - forever, not just until something drains.
        //
        // What the view must NOT do here is disappear. The protocol distinguishes the two cases deliberately:
        // `venues` absent means no venue in this node matches its own orders, and a venue present with an EMPTY
        // instruments array means a paper venue that has been sent nothing. A host that reads those as one thing
        // cannot tell "there is no paper venue" from "the paper venue has nothing", and the second is a broken node.
        CurrencyPair instrument = TestInstruments.BtcUsdt("FAKE");
        Journal journal = new();
        PluginRegistry registry = new();
        registry.AddDataClientFactory(new FakeDataClientFactory(journal)
        {
            Configure = client => client.InstrumentsOnConnect.Add(instrument),
        });
        registry.AddExecutionClientFactory(new SandboxExecutionClientFactory());

        await using TradingNode node = new(new TradingNodeConfig
        {
            Kernel = new KernelConfig { Environment = TradingEnvironment.Live, TraderId = new TraderId("MATCH-002") },
            DataClients = [new ClientEntry("FAKE", "FAKE-DATA", new FakeDataClientConfig())],
            ExecutionClients =
            [
                // Data arrives under FAKE; this paper venue calls itself something else.
                new ClientEntry("SANDBOX", "OTHER-PAPER", new SandboxExecutionClientConfig
                {
                    Venue = "ELSEWHERE",
                    StartingBalances = ["100000 USDT"],
                }),
            ],
            HeartbeatInterval = TimeSpan.Zero,
        }, registry);

        string channel = "bytex-matching-test-" + Guid.NewGuid().ToString("N");
        await using NodeControlServer server = new(channel, node, TimeSpan.FromMinutes(10));
        server.Start();
        await node.StartAsync().WaitAsync(_timeout);
        await using NodeControlClient control = await NodeControlClient.ConnectAsync(channel, _timeout);
        using CancellationTokenSource limit = new(TimeSpan.FromSeconds(60));
        await using IAsyncEnumerator<ControlMessage> messages = control.ReadAsync(limit.Token).GetAsyncEnumerator();

        await control.RequestViewAsync();
        JsonElement view = await NextAsync(messages, ControlProtocol.View);

        // The venue is still reported - it exists and it matches its own orders - and it holds nothing.
        JsonElement venue = Assert.Single(Matching(view)!.Value.EnumerateArray());
        Assert.Equal("ELSEWHERE", venue.GetProperty("venue").GetString());
        Assert.Empty(venue.GetProperty("instruments").EnumerateArray());
    }
}
