using System.Text.Json;
using Bytex.Core.Kernel;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Plugins;
using Bytex.Documents.Runtime;
using Bytex.Documents.Schema;
using Bytex.Live.Control;
using Bytex.Live.Tests.Support;

namespace Bytex.Live.Tests;

// Why: a host that supervises a live node in another process sees it only through the control channel. `view` is what it
// draws the node from: what the strategy waits for, what it holds, its exit orders, the last price and the balances. The
// values are compared with what the node's own cache and strategy say, and with numbers worked out by hand.
public sealed class NodeControlViewTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(20);

    // Buys 0.05 at market when a 3-bar EMA exists (it is positive from then on), then protects with a stop 1% under the entry
    // and a target 2R above it.
    private const string Document = """
        {
          "schemaVersion": "1.0",
          "id": "view-doc",
          "name": "View",
          "instruments": [ { "ref": "primary", "instrumentId": "BTCUSDT.FAKE" } ],
          "barTypes": [ { "ref": "main", "instrument": "primary", "step": 1, "aggregation": "minute", "priceType": "last", "source": "external" } ],
          "parameters": [],
          "nodes": [
            { "id": "bars", "type": "data.bars", "params": { "barType": "main" } },
            { "id": "ema", "type": "ind.ema", "params": { "period": 3 } },
            { "id": "positive", "type": "cond.compare", "label": "EMA exists", "params": { "op": "gt", "value": 0 } },
            { "id": "buy", "type": "act.order", "params": { "side": "buy", "orderType": "market", "sizing": { "mode": "fixed", "value": 0.05 }, "onlyWhenFlat": true } },
            { "id": "protect", "type": "risk.exit", "params": { "stop": { "anchor": "percent", "offset": { "unit": "percent", "value": 1 } }, "target": { "unit": "r", "value": 2 }, "trail": { "enabled": false } } }
          ],
          "edges": [
            { "from": "bars:bars", "to": "ema:bars" },
            { "from": "ema:value", "to": "positive:a" },
            { "from": "positive:out", "to": "buy:trigger" },
            { "from": "buy:position", "to": "protect:position" }
          ],
          "repeat": { "enabled": true }
        }
        """;

    // The read is cancelled through the token the enumerator was created with, so a missing message fails the test
    // instead of leaving a read pending behind it.
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

    // The engine's JSON leaves null values out, so "null" on the wire is a missing property.
    private static bool IsAbsent(JsonElement parent, string name) => !parent.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null;

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

    [Fact]
    public async Task View_reports_what_the_strategy_waits_for_what_it_holds_the_last_price_and_the_balances()
    {
        Journal journal = new();
        PluginRegistry registry = new();
        FakeDataClientFactory dataFactory = new(journal);
        FakeExecutionClientFactory execFactory = new(journal) { Configure = exec => exec.MarketFillPrice = 50_000m };
        registry.AddDataClientFactory(dataFactory);
        registry.AddExecutionClientFactory(execFactory);
        CurrencyPair instrument = TestInstruments.BtcUsdt("FAKE");
        BarType barType = new(instrument.Id, new BarSpecification(1, BarAggregation.Minute, PriceType.Last));

        await using TradingNode node = new(new TradingNodeConfig
        {
            Kernel = new KernelConfig { Environment = TradingEnvironment.Live, TraderId = new TraderId("TESTER-001") },
            DataClients = [new ClientEntry("FAKE", "FAKE-DATA", new FakeDataClientConfig())],
            ExecutionClients = [new ClientEntry("FAKE", "FAKE-EXEC", new FakeExecutionClientConfig())],
            HeartbeatInterval = TimeSpan.Zero,
        }, registry);
        node.AddInstrument(instrument);
        DocumentStrategy strategy = new(new DocumentStrategyConfig { Document = DocumentJson.Deserialize(Document), StrategyId = new StrategyId("Doc-001"), Environment = TradingEnvironment.Live, WarmupBars = 0 });
        node.AddStrategy(strategy);

        string channel = "bytex-view-test-" + Guid.NewGuid().ToString("N");
        await using NodeControlServer server = new(channel, node, TimeSpan.FromMinutes(10));
        server.Start();
        await node.StartAsync().WaitAsync(_timeout);
        await using NodeControlClient client = await NodeControlClient.ConnectAsync(channel, _timeout);
        using CancellationTokenSource limit = new(TimeSpan.FromSeconds(60));
        await using IAsyncEnumerator<ControlMessage> messages = client.ReadAsync(limit.Token).GetAsyncEnumerator();

        JsonElement hello = await NextAsync(messages, ControlProtocol.Hello);
        Assert.Equal(ControlProtocol.Version, hello.GetProperty("version").GetInt32());

        // Before any bar: the document says what it runs on, nothing is held, the balances are the venue's.
        JsonElement first = await ViewWhenAsync(client, messages, _ => true);
        Assert.Equal("TESTER-001", first.GetProperty("traderId").GetString());
        Assert.True(first.GetProperty("running").GetBoolean());
        JsonElement idle = Assert.Single(first.GetProperty("strategies").EnumerateArray());
        Assert.Equal(("Doc-001", "Running"), (idle.GetProperty("id").GetString(), idle.GetProperty("state").GetString()));
        JsonElement document = idle.GetProperty("document");
        Assert.Equal("BTCUSDT.FAKE", document.GetProperty("instrumentId").GetString());
        Assert.Equal(barType.ToString(), document.GetProperty("barType").GetString());
        Assert.Equal(60, document.GetProperty("barIntervalSeconds").GetInt64());
        Assert.True(IsAbsent(document, "frame"));
        Assert.True(IsAbsent(document, "lastBarTs"));
        Assert.Empty(idle.GetProperty("positions").EnumerateArray());
        Assert.True(IsAbsent(Assert.Single(idle.GetProperty("instruments").EnumerateArray()), "lastPrice"));
        JsonElement account = Assert.Single(first.GetProperty("accounts").EnumerateArray());
        Assert.Contains(account.GetProperty("balances").EnumerateArray(), b => b.GetProperty("currency").GetString() == "USDT" && decimal.Parse(b.GetProperty("total").GetString()!, System.Globalization.CultureInfo.InvariantCulture) == 1_000_000m);

        // Three bars give the EMA a value: the strategy buys 0.05 at the fake venue's 50,000 and protects the position.
        FakeDataClient data = dataFactory.Created.Single().Client;
        UnixNanos ts = node.Kernel.Clock.Timestamp;
        long minute = 60L * UnixNanos.NanosPerSecond;
        long firstClose = ts.Value / minute * minute - 2 * minute;
        for (int i = 0; i < 3; i++)
        {
            UnixNanos close = new(firstClose + i * minute);
            data.Emit(new Bar(barType, instrument.MakePrice(49_990m), instrument.MakePrice(50_010m), instrument.MakePrice(49_980m), instrument.MakePrice(50_000m), instrument.MakeQuantity(1m), close, close));
        }

        data.Emit(new TradeTick(instrument.Id, instrument.MakePrice(50_100m), instrument.MakeQuantity(0.01m), AggressorSide.Buyer, new TradeId("X-1"), ts, ts));

        JsonElement view = await ViewWhenAsync(client, messages, v => v.GetProperty("strategies")[0].GetProperty("orders").GetArrayLength() == 2 && !IsAbsent(v.GetProperty("strategies")[0].GetProperty("instruments")[0], "lastPrice"));
        JsonElement s = view.GetProperty("strategies")[0];

        JsonElement position = Assert.Single(s.GetProperty("positions").EnumerateArray());
        Assert.Equal(("Long", "0.05000", "50000.00"), (position.GetProperty("side").GetString(), position.GetProperty("quantity").GetString(), position.GetProperty("avgPxOpen").GetString()));
        // (50,100 - 50,000) * 0.05 = 5
        Assert.Equal(5m, decimal.Parse(position.GetProperty("unrealizedPnl").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("USDT", position.GetProperty("currency").GetString());

        // Stop 1% under 50,000 = 49,500; risk 500; target 2R above = 51,000. Both reduce-only, tagged by the exit node.
        List<JsonElement> orders = s.GetProperty("orders").EnumerateArray().ToList();
        JsonElement stop = Assert.Single(orders, o => o.GetProperty("tags").EnumerateArray().Any(t => t.GetString() == "exit:stop"));
        JsonElement target = Assert.Single(orders, o => o.GetProperty("tags").EnumerateArray().Any(t => t.GetString() == "exit:target"));
        Assert.Equal(("StopMarket", "Sell", "49500.00", true), (stop.GetProperty("type").GetString(), stop.GetProperty("side").GetString(), stop.GetProperty("triggerPrice").GetString(), stop.GetProperty("reduceOnly").GetBoolean()));
        Assert.Equal(("Limit", "Sell", "51000.00", true), (target.GetProperty("type").GetString(), target.GetProperty("side").GetString(), target.GetProperty("price").GetString(), target.GetProperty("reduceOnly").GetBoolean()));
        Assert.True(IsAbsent(target, "triggerPrice"));

        JsonElement lastPrice = s.GetProperty("instruments")[0].GetProperty("lastPrice");
        Assert.Equal(("50100.00", "trade", ts.Value), (lastPrice.GetProperty("price").GetString(), lastPrice.GetProperty("kind").GetString(), lastPrice.GetProperty("ts").GetInt64()));

        // The frame is the strategy's own LastFrame, and the warm-up counts are its own.
        JsonElement frame = s.GetProperty("document").GetProperty("frame");
        FrameSnapshot own = strategy.LastFrame!;
        Assert.Equal((own.Index, own.BarTs.Value), (frame.GetProperty("index").GetInt64(), frame.GetProperty("barTs").GetInt64()));
        JsonElement condition = Assert.Single(frame.GetProperty("conditions").EnumerateArray());
        Assert.Equal(("positive", "cond.compare", "EMA exists", true), (condition.GetProperty("nodeId").GetString(), condition.GetProperty("type").GetString(), condition.GetProperty("label").GetString(), condition.GetProperty("output").GetBoolean()));
        Assert.Equal("gt", condition.GetProperty("params").GetProperty("op").GetString());
        Assert.Equal(50_000m, decimal.Parse(condition.GetProperty("inputs").GetProperty("a").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(firstClose + 2 * minute, s.GetProperty("document").GetProperty("lastBarTs").GetInt64());
        JsonElement warmup = Assert.Single(s.GetProperty("document").GetProperty("warmup").EnumerateArray());
        Assert.Equal((0, 3, 0, true), (warmup.GetProperty("needed").GetInt32(), warmup.GetProperty("received").GetInt32(), warmup.GetProperty("fromHistory").GetInt32(), warmup.GetProperty("done").GetBoolean()));
        Assert.False(s.GetProperty("document").GetProperty("warmupPending").GetBoolean());
    }

    [Fact]
    public async Task A_strategy_that_is_not_a_document_is_reported_without_a_document_part()
    {
        Journal journal = new();
        PluginRegistry registry = new();
        registry.AddDataClientFactory(new FakeDataClientFactory(journal));
        registry.AddExecutionClientFactory(new FakeExecutionClientFactory(journal));
        await using TradingNode node = new(new TradingNodeConfig
        {
            Kernel = new KernelConfig { Environment = TradingEnvironment.Live, TraderId = new TraderId("TESTER-001") },
            DataClients = [new ClientEntry("FAKE", "FAKE-DATA", new FakeDataClientConfig())],
            ExecutionClients = [new ClientEntry("FAKE", "FAKE-EXEC", new FakeExecutionClientConfig())],
            HeartbeatInterval = TimeSpan.Zero,
        }, registry);
        node.AddInstrument(TestInstruments.BtcUsdt("FAKE"));
        node.AddStrategy(new ProbeStrategy(journal: journal));
        string channel = "bytex-view-test-" + Guid.NewGuid().ToString("N");
        await using NodeControlServer server = new(channel, node, TimeSpan.FromMinutes(10));
        server.Start();
        await node.StartAsync().WaitAsync(_timeout);
        await using NodeControlClient client = await NodeControlClient.ConnectAsync(channel, _timeout);
        using CancellationTokenSource limit = new(TimeSpan.FromSeconds(60));
        await using IAsyncEnumerator<ControlMessage> messages = client.ReadAsync(limit.Token).GetAsyncEnumerator();

        JsonElement view = await ViewWhenAsync(client, messages, _ => true);

        JsonElement s = Assert.Single(view.GetProperty("strategies").EnumerateArray());
        Assert.Equal("Probe-001", s.GetProperty("id").GetString());
        Assert.False(view.TryGetProperty("error", out _));
        Assert.True(IsAbsent(s, "document"));
        Assert.Empty(s.GetProperty("instruments").EnumerateArray());
        Assert.Empty(s.GetProperty("orders").EnumerateArray());
    }

    [Fact]
    public async Task A_command_sent_before_the_hello_was_read_is_still_answered()
    {
        // A host may write first. With an unbuffered pipe the node, still writing its hello, and the host, writing its
        // command, waited for each other for ever.
        Journal journal = new();
        PluginRegistry registry = new();
        registry.AddDataClientFactory(new FakeDataClientFactory(journal));
        registry.AddExecutionClientFactory(new FakeExecutionClientFactory(journal));
        await using TradingNode node = new(new TradingNodeConfig
        {
            Kernel = new KernelConfig { Environment = TradingEnvironment.Live, TraderId = new TraderId("TESTER-001") },
            DataClients = [new ClientEntry("FAKE", "FAKE-DATA", new FakeDataClientConfig())],
            ExecutionClients = [new ClientEntry("FAKE", "FAKE-EXEC", new FakeExecutionClientConfig())],
            HeartbeatInterval = TimeSpan.Zero,
        }, registry);
        string channel = "bytex-view-test-" + Guid.NewGuid().ToString("N");
        await using NodeControlServer server = new(channel, node, TimeSpan.FromMinutes(10));
        server.Start();
        await node.StartAsync().WaitAsync(_timeout);
        await using NodeControlClient client = await NodeControlClient.ConnectAsync(channel, _timeout);

        await client.RequestStatusAsync().WaitAsync(_timeout);
        await client.RequestViewAsync().WaitAsync(_timeout);

        using CancellationTokenSource limit = new(TimeSpan.FromSeconds(60));
        await using IAsyncEnumerator<ControlMessage> messages = client.ReadAsync(limit.Token).GetAsyncEnumerator();
        List<string> types = new();
        while (!(types.Contains(ControlProtocol.Status) && types.Contains(ControlProtocol.View)) && await messages.MoveNextAsync())
        {
            types.Add(messages.Current.Type);
        }

        Assert.Equal(ControlProtocol.Hello, types[0]);
        Assert.Contains(ControlProtocol.Status, types);
        Assert.Contains(ControlProtocol.View, types);
    }
}
