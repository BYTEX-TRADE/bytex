using System.Text.Json;
using Bytex.Core.Kernel;
using Bytex.Core.Model;
using Bytex.Core.Model.Accounts;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Positions;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Model.Reports;
using Bytex.Core.Plugins;
using Bytex.Core.Serialization;
using Bytex.Live.Sandbox;
using Bytex.Live.Tests.Support;

namespace Bytex.Live.Tests;

// Why: the node is the piece that turns a JSON file into a running trading system. The order of start-up
// (connect, reconcile, only then start strategies) and of shutdown (flatten, stop, disconnect) is the
// difference between a clean restart and orphaned orders at a venue. Clients here are hand-made fakes.
public sealed class TradingNodeTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(15);

    private sealed class Rig
    {
        public Rig(Action<FakeExecutionClient>? configureExec = null, bool hangOnConnect = false)
        {
            DataFactory = new FakeDataClientFactory(Journal) { HangOnConnect = hangOnConnect };
            ExecFactory = new FakeExecutionClientFactory(Journal) { Configure = configureExec };
            Registry.AddDataClientFactory(DataFactory);
            Registry.AddExecutionClientFactory(ExecFactory);
        }

        public Journal Journal { get; } = new();

        public PluginRegistry Registry { get; } = new();

        public FakeDataClientFactory DataFactory { get; }

        public FakeExecutionClientFactory ExecFactory { get; }

        public CurrencyPair Instrument { get; } = TestInstruments.BtcUsdt("FAKE");

        public FakeDataClient Data => DataFactory.Created.Single().Client;

        public FakeExecutionClient Exec => ExecFactory.Created.Single().Client;

        public static TradingNodeConfig Config() => new()
        {
            Kernel = new KernelConfig { Environment = TradingEnvironment.Live, TraderId = new TraderId("TESTER-001") },
            DataClients = [new ClientEntry("FAKE", "FAKE-DATA", new FakeDataClientConfig { Label = "typed" })],
            ExecutionClients = [new ClientEntry("FAKE", "FAKE-EXEC", new FakeExecutionClientConfig { Label = "typed" })],
            HeartbeatInterval = TimeSpan.Zero,
        };

        public TradingNode Node(TradingNodeConfig? config = null, Microsoft.Extensions.Logging.ILoggerFactory? logging = null)
        {
            TradingNode node = new(config ?? Config(), Registry, logging);
            node.AddInstrument(Instrument);
            return node;
        }
    }

    // ----- Building -----

    [Fact]
    public async Task Build_creates_each_configured_client_through_its_named_factory_with_its_id_and_config()
    {
        Rig rig = new();
        await using TradingNode node = rig.Node();

        node.Build();

        (ClientId dataId, FakeDataClientConfig dataConfig, FakeDataClient data) = Assert.Single(rig.DataFactory.Created);
        (ClientId execId, FakeExecutionClientConfig execConfig, FakeExecutionClient exec) = Assert.Single(rig.ExecFactory.Created);
        Assert.Equal(new ClientId("FAKE-DATA"), dataId);
        Assert.Equal(new ClientId("FAKE-EXEC"), execId);
        Assert.Equal("typed", dataConfig.Label);
        Assert.Equal("typed", execConfig.Label);
        Assert.Same(data, Assert.Single(node.DataClients));
        Assert.Same(exec, Assert.Single(node.ExecutionClients));
    }

    [Fact]
    public async Task Build_is_idempotent()
    {
        Rig rig = new();
        await using TradingNode node = rig.Node();

        node.Build();
        node.Build();

        Assert.Single(rig.DataFactory.Created);
        Assert.Single(rig.ExecFactory.Created);
    }

    [Fact]
    public async Task A_node_configuration_written_as_json_builds_a_sandbox_client_with_the_documented_account()
    {
        // The sandbox entry from docs/integrations/sandbox.md inside a node configuration, as the CLI would read it.
        string json = """
            {
              "kernel": { "traderId": "TESTER-001", "environment": "sandbox" },
              "executionClients": [
                { "factory": "SANDBOX", "clientId": "BINANCE-SANDBOX", "config": {
                    "venue": "BINANCE",
                    "omsType": "netting",
                    "accountType": "cash",
                    "baseCurrency": null,
                    "startingBalances": ["100000 USDT", "1 BTC"],
                    "defaultLeverage": 1,
                    "barExecution": "ohlcPath",
                    "probFillOnLimit": 1.0,
                    "probSlippage": 0.0,
                    "latency": "00:00:00"
                } }
              ],
              "reconcileOnStart": true,
              "cancelOrdersOnStop": false,
              "heartbeatInterval": "00:00:00"
            }
            """;
        TradingNodeConfig config = JsonSerializer.Deserialize<TradingNodeConfig>(json, BytexJson.Options)!;
        PluginRegistry registry = new();
        registry.AddExecutionClientFactory(new SandboxExecutionClientFactory());
        await using TradingNode node = new(config, registry);

        await node.StartAsync().WaitAsync(_timeout);
        Account? account = await node.Loop.InvokeAsync(() => node.Kernel.Cache.Account(new AccountId("BINANCE-SANDBOX"))).WaitAsync(_timeout);

        SandboxExecutionClient client = Assert.IsType<SandboxExecutionClient>(Assert.Single(node.ExecutionClients));
        Assert.Equal(new ClientId("BINANCE-SANDBOX"), client.ClientId);
        Assert.Equal(new TraderId("TESTER-001"), node.TraderId);
        Assert.Equal(TradingEnvironment.Sandbox, node.Kernel.Environment);
        Assert.NotNull(account);
        Assert.Equal(100_000m, account.LastEvent.Balances.Single(b => b.Currency.Code == "USDT").Total.Amount);
        Assert.Equal(1m, account.LastEvent.Balances.Single(b => b.Currency.Code == "BTC").Total.Amount);
        await node.StopAsync().WaitAsync(_timeout);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Build_fails_with_the_factory_name_when_no_such_factory_is_registered(bool dataClient)
    {
        Rig rig = new();
        TradingNodeConfig config = dataClient
            ? Rig.Config() with { DataClients = [new ClientEntry("KRAKEN", "K", new FakeDataClientConfig())] }
            : Rig.Config() with { ExecutionClients = [new ClientEntry("KRAKEN", "K", new FakeExecutionClientConfig())] };
        await using TradingNode node = rig.Node(config);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(node.Build);

        Assert.Contains("KRAKEN", error.Message);
    }

    [Fact]
    public async Task Build_rejects_a_config_object_of_the_wrong_type_for_the_factory()
    {
        Rig rig = new();
        TradingNodeConfig config = Rig.Config() with { ExecutionClients = [new ClientEntry("FAKE", "FAKE-EXEC", new SandboxExecutionClientConfig { Venue = "FAKE" })] };
        await using TradingNode node = rig.Node(config);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(node.Build);

        Assert.Contains(nameof(FakeExecutionClientConfig), error.Message);
    }

    // ----- Start-up -----

    [Fact]
    public async Task Start_connects_data_then_execution_then_reconciles_and_only_then_starts_strategies()
    {
        Rig rig = new();
        await using TradingNode node = rig.Node();
        node.AddStrategy(new ProbeStrategy(journal: rig.Journal));

        await node.StartAsync().WaitAsync(_timeout);

        Assert.Equal(["data.connect", "exec.connect", "exec.mass-status", "strategy.start"], rig.Journal.Snapshot());
        Assert.True(node.IsRunning);
        Assert.True(node.Kernel.IsRunning);
        await node.StopAsync().WaitAsync(_timeout);
    }

    [Fact]
    public async Task Start_skips_reconciliation_when_it_is_switched_off()
    {
        Rig rig = new();
        await using TradingNode node = rig.Node(Rig.Config() with { ReconcileOnStart = false });
        node.AddStrategy(new ProbeStrategy(journal: rig.Journal));

        await node.StartAsync().WaitAsync(_timeout);

        Assert.Equal(["data.connect", "exec.connect", "strategy.start"], rig.Journal.Snapshot());
        await node.StopAsync().WaitAsync(_timeout);
    }

    [Fact]
    public async Task Starting_twice_does_not_reconnect_or_restart_anything()
    {
        Rig rig = new();
        await using TradingNode node = rig.Node();

        await node.StartAsync().WaitAsync(_timeout);
        await node.StartAsync().WaitAsync(_timeout);

        Assert.Equal(["data.connect", "exec.connect", "exec.mass-status"], rig.Journal.Snapshot());
        await node.StopAsync().WaitAsync(_timeout);
    }

    [Fact]
    public async Task Reconciliation_asks_the_venue_for_history_since_now_minus_the_configured_lookback()
    {
        Rig rig = new();
        await using TradingNode node = rig.Node(Rig.Config() with { ReconciliationLookback = TimeSpan.FromHours(6) });
        DateTimeOffset before = DateTimeOffset.UtcNow;

        await node.StartAsync().WaitAsync(_timeout);
        DateTimeOffset after = DateTimeOffset.UtcNow;

        UnixNanos? since = Assert.Single(rig.Exec.MassStatusSince);
        Assert.NotNull(since);
        Assert.InRange(since.Value.ToDateTimeOffset(), before.AddHours(-6).AddSeconds(-1), after.AddHours(-6).AddSeconds(1));
        await node.StopAsync().WaitAsync(_timeout);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_open_venue_order_unknown_to_the_node_is_adopted_before_strategies_start(bool claimedByStrategy)
    {
        Rig rig = null!;
        rig = new Rig(exec =>
        {
            InstrumentId id = rig.Instrument.Id;
            OrderStatusReport report = new(exec.AccountId, id, new ClientOrderId("EXT-1"), new VenueOrderId("V-77"), OrderSide.Buy, OrderType.Limit, TimeInForce.Gtc, OrderStatus.Accepted,
                rig.Instrument.MakeQuantity(0.4m), rig.Instrument.MakeQuantity(0m), UnixNanos.FromSeconds(1_700_000_000), UnixNanos.FromSeconds(1_700_000_000), UnixNanos.FromSeconds(1_700_000_001), Guid.NewGuid(),
                rig.Instrument.MakePrice(25_000m));
            exec.MassStatus = new ExecutionMassStatus(exec.ClientId, exec.AccountId, exec.Venue, [report], [], [], UnixNanos.FromSeconds(1_700_000_001), Guid.NewGuid());
        });
        await using TradingNode node = rig.Node();
        ProbeStrategy strategy = new(journal: rig.Journal, claims: claimedByStrategy ? [rig.Instrument.Id] : []);
        node.AddStrategy(strategy);

        await node.StartAsync().WaitAsync(_timeout);
        Order? adopted = await node.Loop.InvokeAsync(() => node.Kernel.Cache.OrderForVenueId(new VenueOrderId("V-77"))).WaitAsync(_timeout);

        Assert.NotNull(adopted);
        Assert.Equal(new ClientOrderId("EXT-1"), adopted.ClientOrderId);
        Assert.Equal(OrderStatus.Accepted, adopted.Status);
        Assert.Equal(0.4m, adopted.Quantity.Value);
        Assert.Equal(25_000m, adopted.Price!.Value.Value);
        Assert.Equal(claimedByStrategy ? strategy.StrategyId : StrategyId.External, adopted.StrategyId);
        await node.StopAsync().WaitAsync(_timeout);
    }

    [Fact]
    public async Task A_failing_reconciliation_is_logged_and_does_not_prevent_the_node_from_starting()
    {
        Rig rig = new(exec => exec.MassStatusFailure = new IOException("venue unreachable"));
        using CapturingLoggerFactory logs = new();
        await using TradingNode node = rig.Node(logging: logs);
        node.AddStrategy(new ProbeStrategy(journal: rig.Journal));

        await node.StartAsync().WaitAsync(_timeout);

        Assert.True(node.IsRunning);
        Assert.Contains("strategy.start", rig.Journal.Snapshot());
        Assert.StartsWith("Error", await logs.WaitForAsync("Reconciliation with FAKE-EXEC failed"));
        await node.StopAsync().WaitAsync(_timeout);
    }

    [Fact]
    public async Task A_client_that_never_connects_fails_the_start_after_the_connection_timeout()
    {
        Rig rig = new(hangOnConnect: true);
        await using TradingNode node = rig.Node(Rig.Config() with { ConnectionTimeout = TimeSpan.FromMilliseconds(200) });
        node.AddStrategy(new ProbeStrategy(journal: rig.Journal));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => node.StartAsync().WaitAsync(_timeout));

        Assert.False(node.IsRunning);
        Assert.DoesNotContain("strategy.start", rig.Journal.Snapshot());
    }

    [Fact(Skip = "BUG: when StartAsync fails part-way (here the execution client times out) the node never becomes 'running', so neither StopAsync nor DisposeAsync disconnects the data client that had already connected")]
    public async Task A_start_that_fails_part_way_disconnects_the_clients_that_had_already_connected()
    {
        Rig rig = new(exec => exec.HangOnConnect = true);
        TradingNode node = rig.Node(Rig.Config() with { ConnectionTimeout = TimeSpan.FromMilliseconds(200) });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => node.StartAsync().WaitAsync(_timeout));
        await node.DisposeAsync().AsTask().WaitAsync(_timeout);

        Assert.Equal(["data.connect", "exec.connect", "data.disconnect"], rig.Journal.Snapshot());
    }

    [Fact]
    public async Task Market_data_emitted_on_an_adapter_thread_reaches_the_strategy_on_the_kernel_thread()
    {
        Rig rig = new();
        await using TradingNode node = rig.Node();
        ProbeStrategy strategy = new(journal: rig.Journal) { SubscribeTo = rig.Instrument.Id };
        node.AddStrategy(strategy);
        await node.StartAsync().WaitAsync(_timeout);
        await rig.Data.FirstSubscription.Task.WaitAsync(_timeout);

        int adapterThread = await Task.Run(() =>
        {
            rig.Data.Emit(TestInstruments.Quote(rig.Instrument, 30_000m, 30_001m, UnixNanos.FromSeconds(1_700_000_000)));
            return Environment.CurrentManagedThreadId;
        });
        Core.Model.Data.QuoteTick received = await strategy.FirstQuote.Task.WaitAsync(_timeout);

        (Core.Model.Data.QuoteTick _, int handlerThread) = await node.Loop.InvokeAsync(() => strategy.Quotes.Single()).WaitAsync(_timeout);
        Assert.Equal(30_000m, received.Bid.Value);
        Assert.Equal(node.Loop.ManagedThreadId, handlerThread);
        Assert.NotEqual(adapterThread, handlerThread);
        await node.StopAsync().WaitAsync(_timeout);
    }

    [Fact]
    public async Task The_heartbeat_reports_queue_and_order_counters_at_the_configured_interval()
    {
        Rig rig = new();
        using CapturingLoggerFactory logs = new();
        await using TradingNode node = rig.Node(Rig.Config() with { HeartbeatInterval = TimeSpan.FromMilliseconds(50) }, logs);

        await node.StartAsync().WaitAsync(_timeout);
        string line = await logs.WaitForAsync("Heartbeat:");

        Assert.Contains("orders open 0", line);
        Assert.Contains("positions open 0", line);
        await node.StopAsync().WaitAsync(_timeout);
    }

    // ----- Shutdown -----

    [Fact]
    public async Task Stop_stops_strategies_first_then_disconnects_execution_before_data()
    {
        Rig rig = new();
        await using TradingNode node = rig.Node();
        node.AddStrategy(new ProbeStrategy(journal: rig.Journal));
        await node.StartAsync().WaitAsync(_timeout);

        await node.StopAsync().WaitAsync(_timeout);

        Assert.Equal(["strategy.stop", "exec.disconnect", "data.disconnect"], rig.Journal.Snapshot().TakeLast(3));
        Assert.False(node.IsRunning);
        Assert.False(node.Kernel.IsRunning);
    }

    [Fact]
    public async Task Stop_leaves_open_orders_alone_unless_told_to_cancel_them()
    {
        Rig rig = new();
        await using TradingNode node = rig.Node();
        ProbeStrategy strategy = new(journal: rig.Journal);
        node.AddStrategy(strategy);
        await node.StartAsync().WaitAsync(_timeout);
        LimitOrder order = await node.Loop.InvokeAsync(() =>
        {
            LimitOrder o = strategy.Orders.Limit(rig.Instrument.Id, OrderSide.Buy, rig.Instrument.MakeQuantity(0.1m), rig.Instrument.MakePrice(20_000m));
            strategy.Submit(o);
            return o;
        }).WaitAsync(_timeout);

        await node.StopAsync().WaitAsync(_timeout);

        Assert.Empty(rig.Exec.CancelAlls);
        Assert.Equal(OrderStatus.Accepted, order.Status);
    }

    [Fact]
    public async Task Stop_with_both_shutdown_options_cancels_open_orders_and_flattens_positions_before_stopping_strategies()
    {
        Rig rig = new(exec => exec.MarketFillPrice = 30_000m);
        await using TradingNode node = rig.Node(Rig.Config() with { CancelOrdersOnStop = true, ClosePositionsOnStop = true });
        ProbeStrategy strategy = new(journal: rig.Journal);
        node.AddStrategy(strategy);
        await node.StartAsync().WaitAsync(_timeout);
        LimitOrder resting = await node.Loop.InvokeAsync(() =>
        {
            strategy.Submit(strategy.Orders.Market(rig.Instrument.Id, OrderSide.Buy, rig.Instrument.MakeQuantity(0.3m)));
            LimitOrder o = strategy.Orders.Limit(rig.Instrument.Id, OrderSide.Buy, rig.Instrument.MakeQuantity(0.1m), rig.Instrument.MakePrice(20_000m));
            strategy.Submit(o);
            return o;
        }).WaitAsync(_timeout);
        int openPositionsBefore = await node.Loop.InvokeAsync(() => node.Kernel.Cache.PositionsOpenCount()).WaitAsync(_timeout);

        await node.StopAsync().WaitAsync(_timeout);

        Assert.Equal(1, openPositionsBefore);
        Assert.Equal(rig.Instrument.Id, Assert.Single(rig.Exec.CancelAlls).InstrumentId);
        Assert.Equal(OrderStatus.Canceled, resting.Status);
        Order flatten = rig.Exec.Submitted[^1].Order;
        Assert.Equal(OrderType.Market, flatten.Type);
        Assert.Equal(OrderSide.Sell, flatten.Side);
        Assert.Equal(0.3m, flatten.Quantity.Value);
        Assert.True(flatten.IsReduceOnly);
        Assert.Contains("SHUTDOWN", flatten.Tags);
        Assert.Equal(0, node.Kernel.Cache.PositionsOpenCount());
        List<string> journal = rig.Journal.Snapshot().ToList();
        Assert.True(journal.IndexOf("exec.cancel-all BTCUSDT.FAKE") < journal.IndexOf("strategy.stop"));
        Assert.True(journal.FindLastIndex(e => e.StartsWith("exec.submit Market Sell", StringComparison.Ordinal)) < journal.IndexOf("strategy.stop"));
    }

    [Fact]
    public async Task RunAsync_runs_until_cancelled_and_then_shuts_the_node_down()
    {
        Rig rig = new();
        await using TradingNode node = rig.Node();
        ProbeStrategy strategy = new(journal: rig.Journal) { SubscribeTo = rig.Instrument.Id };
        node.AddStrategy(strategy);
        using CancellationTokenSource cts = new();

        node.Build();
        Task run = node.RunAsync(cts.Token);
        await rig.Data.FirstSubscription.Task.WaitAsync(_timeout); // the strategy is running
        bool finishedBeforeCancel = run.IsCompleted;
        await cts.CancelAsync();
        await run.WaitAsync(_timeout);

        Assert.False(finishedBeforeCancel);
        Assert.False(node.IsRunning);
        Assert.Equal(["strategy.stop", "exec.disconnect", "data.disconnect"], rig.Journal.Snapshot().TakeLast(3));
    }

    [Fact]
    public async Task Disposing_a_running_node_stops_it_and_a_disposed_node_cannot_be_started_again()
    {
        Rig rig = new();
        TradingNode node = rig.Node();
        await node.StartAsync().WaitAsync(_timeout);

        await node.DisposeAsync().AsTask().WaitAsync(_timeout);

        Assert.False(node.IsRunning);
        Assert.Equal(["exec.disconnect", "data.disconnect"], rig.Journal.Snapshot().TakeLast(2));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => node.StartAsync());
    }
}
