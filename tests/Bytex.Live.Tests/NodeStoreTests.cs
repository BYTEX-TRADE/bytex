using Bytex.Core.Kernel;
using Bytex.Core.Model;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Plugins;
using Bytex.Live.Persistence;
using Bytex.Live.Tests.Support;

namespace Bytex.Live.Tests;

// Why: a live node kept everything in memory, so stopping it threw away the evidence of what it had done and the state
// its strategies were in. These tests run a node over a directory, stop it, and check that the journal is on disk and
// that a node started again over the same directory picks its strategy's state up.
public sealed class NodeStoreTests : IDisposable
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(20);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bytex-store-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record Rig(TradingNode Node, CountingStrategy Strategy, FakeDataClient Data, Instrument Instrument) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Node.DisposeAsync();
    }

    private async Task<Rig> StartAsync(bool restoreState = true, string trader = "TESTER-001")
    {
        Journal journal = new();
        PluginRegistry registry = new();
        FakeDataClientFactory dataFactory = new(journal);
        FakeExecutionClientFactory execFactory = new(journal) { Configure = exec => exec.MarketFillPrice = 50_000m };
        registry.AddDataClientFactory(dataFactory);
        registry.AddExecutionClientFactory(execFactory);
        Instrument instrument = TestInstruments.BtcUsdt("FAKE");

        TradingNode node = new(new TradingNodeConfig
        {
            Kernel = new KernelConfig { Environment = TradingEnvironment.Live, TraderId = new TraderId(trader) },
            DataClients = [new ClientEntry("FAKE", "FAKE-DATA", new FakeDataClientConfig())],
            ExecutionClients = [new ClientEntry("FAKE", "FAKE-EXEC", new FakeExecutionClientConfig())],
            HeartbeatInterval = TimeSpan.Zero,
            Store = new NodeStoreConfig { Directory = _directory, RestoreState = restoreState },
        }, registry);
        node.AddInstrument(instrument);
        CountingStrategy strategy = new(instrument.Id);
        node.AddStrategy(strategy);
        await node.StartAsync().WaitAsync(_timeout);
        return new Rig(node, strategy, dataFactory.Created.Single().Client, instrument);
    }

    // One quote for the strategy to count, and one order that fills, so the journal has a log line, an order event, a
    // position event and an account event in it.
    private static async Task TradeAsync(Rig rig)
    {
        rig.Data.Emit(TestInstruments.Quote(rig.Instrument, 49_999m, 50_001m, rig.Node.Kernel.Clock.Timestamp));
        await rig.Strategy.FirstQuote.Task.WaitAsync(_timeout);

        await rig.Node.Loop.InvokeAsync(() =>
        {
            MarketOrder order = rig.Strategy.Orders.Market(rig.Instrument.Id, OrderSide.Buy, rig.Instrument.MakeQuantity(0.01m));
            rig.Strategy.Submit(order);
        }).WaitAsync(_timeout);

        DateTime deadline = DateTime.UtcNow + _timeout;
        while (DateTime.UtcNow < deadline
            && await rig.Node.Loop.InvokeAsync(() => rig.Node.Kernel.Cache.PositionsOpen(instrumentId: rig.Instrument.Id).Count).WaitAsync(_timeout) == 0)
        {
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task What_a_node_logged_and_every_order_and_position_event_is_on_disk_after_it_stops()
    {
        await using (Rig rig = await StartAsync())
        {
            await TradeAsync(rig);
            await rig.Node.StopAsync().WaitAsync(_timeout);
        }

        await using NodeStore store = new(new NodeStoreConfig { Directory = _directory });
        List<JournalRecord> records = store.Read().ToList();

        Assert.Contains(records, r => r.Kind == "log" && r.Message!.Contains("Starting trading node TESTER-001", StringComparison.Ordinal));
        Assert.Contains(records, r => r.Kind == "log" && r.Message!.Contains("stopped", StringComparison.Ordinal));
        Assert.Contains(records, r => r.Kind == "orderEvent" && r.Source == "OrderFilled" && r.Payload!.Value.GetProperty("lastPx").GetString() == "50000.00");
        Assert.Contains(records, r => r.Kind == "positionEvent" && r.Source == "PositionOpened");
        Assert.All(records, r => Assert.True(r.Ts.Value > 0));
        Assert.Equal(0, store.Dropped);
        Assert.True(File.Exists(Path.Combine(store.StateDirectory, "Counter-001.json")));

        // The stop itself is in the journal, which means nothing was left in the queue when the node reported it had
        // stopped: a journal that loses its tail cannot say what a node did last.
        Assert.Equal("log", records[^1].Kind);
    }

    [Fact]
    public async Task A_node_started_again_over_the_same_directory_reads_its_strategys_state_back()
    {
        await using (Rig first = await StartAsync())
        {
            await TradeAsync(first);
            Assert.Equal(1, first.Strategy.Seen);
            await first.Node.StopAsync().WaitAsync(_timeout);
        }

        await using Rig second = await StartAsync();

        // The count came back with the state, and the strategy said so on the way up, into the journal of the second run.
        Assert.Equal(1, second.Strategy.Seen);
        await second.Node.StopAsync().WaitAsync(_timeout);
        await using NodeStore store = new(new NodeStoreConfig { Directory = _directory });
        Assert.Contains(store.Read(), r => r.Kind == "log" && r.Message!.Contains("started with 1 quotes behind it", StringComparison.Ordinal));
    }

    [Fact]
    public async Task With_restoreState_off_the_journal_is_still_written_and_the_strategy_starts_fresh()
    {
        await using (Rig first = await StartAsync())
        {
            await TradeAsync(first);
            await first.Node.StopAsync().WaitAsync(_timeout);
        }

        await using Rig second = await StartAsync(restoreState: false, trader: "TESTER-002");
        await second.Node.StopAsync().WaitAsync(_timeout);

        Assert.Equal(0, second.Strategy.Seen);
        await using NodeStore store = new(new NodeStoreConfig { Directory = _directory });
        Assert.Contains(store.Read(), r => r.Kind == "log" && r.Message!.Contains("Starting trading node TESTER-002", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_journal_of_the_first_run_is_still_there_after_the_second_one_and_both_runs_are_in_it()
    {
        await using (Rig first = await StartAsync())
        {
            await TradeAsync(first);
            await first.Node.StopAsync().WaitAsync(_timeout);
        }

        await using (Rig second = await StartAsync())
        {
            await second.Node.StopAsync().WaitAsync(_timeout);
        }

        await using NodeStore store = new(new NodeStoreConfig { Directory = _directory });
        List<JournalRecord> records = store.Read().ToList();

        Assert.Equal(2, records.Count(r => r.Kind == "log" && r.Message!.Contains("Starting trading node", StringComparison.Ordinal)));
        Assert.Contains(records, r => r.Kind == "orderEvent" && r.Source == "OrderFilled");
        Assert.Empty(store.Read(from: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1)));
    }

    [Fact]
    public async Task A_node_without_a_store_keeps_nothing_and_writes_nowhere()
    {
        PluginRegistry registry = new();
        registry.AddDataClientFactory(new FakeDataClientFactory(new Journal()));
        await using TradingNode node = new(new TradingNodeConfig
        {
            Kernel = new KernelConfig { Environment = TradingEnvironment.Live, TraderId = new TraderId("TESTER-003") },
            DataClients = [new ClientEntry("FAKE", "FAKE-DATA", new FakeDataClientConfig())],
            HeartbeatInterval = TimeSpan.Zero,
        }, registry);

        Assert.Null(node.Store);
        Assert.False(Directory.Exists(_directory));
    }
}
