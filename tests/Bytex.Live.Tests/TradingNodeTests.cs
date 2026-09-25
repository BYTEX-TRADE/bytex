using System.Text.Json;
using Bytex.Core.Engines;
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
    public async Task A_node_can_be_told_to_start_halted_and_to_trade_when_it_is_released()
    {
        // A supervising application that wants to look before anything trades starts the node this way: it runs, it
        // holds its strategies and its data, and it places nothing until the host says so.
        Rig rig = new();
        TradingNodeConfig config = Rig.Config() with { StartHalted = true, Kernel = Rig.Config().Kernel };
        await using TradingNode node = rig.Node(config);

        await node.StartAsync().WaitAsync(_timeout);

        Assert.True(node.IsRunning);
        Assert.True(node.IsHalted);
        Assert.Equal(TradingState.Halted, node.TradingState);

        node.Resume();
        await node.Loop.InvokeAsync(() => { }).WaitAsync(_timeout);

        Assert.False(node.IsHalted);
        Assert.Equal(TradingState.Active, node.TradingState);
        await node.StopAsync().WaitAsync(_timeout);
    }

    [Fact]
    public async Task A_running_node_takes_the_limits_a_host_hands_it()
    {
        Rig rig = new();
        await using TradingNode node = rig.Node();
        await node.StartAsync().WaitAsync(_timeout);
        Assert.True(node.Kernel.RiskEngine.Limits.IsEmpty);

        node.SetLimits(new RiskLimits { MaxWorkingOrders = 4, MaxExposure = RiskLimit.Parse("25%") });
        await node.Loop.InvokeAsync(() => { }).WaitAsync(_timeout);

        Assert.Equal(4, node.Kernel.RiskEngine.Limits.MaxWorkingOrders);
        Assert.Equal(25m, node.Kernel.RiskEngine.Limits.MaxExposure!.Percent);
        await node.StopAsync().WaitAsync(_timeout);
    }

    [Fact]
    public async Task A_halted_node_places_nothing_and_says_so()
    {
        Rig rig = new();
        await using TradingNode node = rig.Node();
        await node.StartAsync().WaitAsync(_timeout);

        node.Halt(reason: "a test");
        await node.Loop.InvokeAsync(() => { }).WaitAsync(_timeout);

        Assert.True(node.IsHalted);
        Assert.True(node.IsRunning, "halting a node does not stop it");
        await node.StopAsync().WaitAsync(_timeout);
    }

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

    [Fact]
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

    [Fact]
    public async Task A_start_whose_first_client_never_connects_disconnects_nothing_and_leaves_no_node_running()
    {
        // The data client hangs, so nothing ever connected: there is nothing to close, and no disconnect should be
        // invented for a client that never opened anything.
        Rig rig = new(hangOnConnect: true);
        TradingNode node = rig.Node(Rig.Config() with { ConnectionTimeout = TimeSpan.FromMilliseconds(200) });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => node.StartAsync().WaitAsync(_timeout));

        Assert.False(node.IsRunning);
        Assert.Equal(["data.connect"], rig.Journal.Snapshot());

        await node.DisposeAsync().AsTask().WaitAsync(_timeout);
        Assert.Equal(["data.connect"], rig.Journal.Snapshot());
    }

    [Fact]
    public async Task A_node_whose_start_failed_can_be_started_again_once_the_venue_is_reachable()
    {
        // The execution client refuses the first connection and takes the second.
        Rig rig = new(exec => exec.FailConnectTimes = 1);
        TradingNode node = rig.Node();

        await Assert.ThrowsAsync<IOException>(() => node.StartAsync().WaitAsync(_timeout));
        Assert.False(node.IsRunning);
        Assert.Equal(["data.connect", "exec.connect", "data.disconnect"], rig.Journal.Snapshot());

        await node.StartAsync().WaitAsync(_timeout);

        Assert.True(node.IsRunning);

        // The second start connects both clients again and goes on to reconcile, as any start does.
        Assert.Equal(
            ["data.connect", "exec.connect", "data.disconnect", "data.connect", "exec.connect"],
            rig.Journal.Snapshot().Take(5));
        Assert.Contains("exec.mass-status", rig.Journal.Snapshot());
        await node.DisposeAsync().AsTask().WaitAsync(_timeout);
    }

    [Fact]
    public async Task A_failed_start_leaves_no_heartbeat_behind()
    {
        using CapturingLoggerFactory logs = new();
        Rig rig = new(exec => exec.HangOnConnect = true);
        TradingNode node = rig.Node(
            Rig.Config() with { ConnectionTimeout = TimeSpan.FromMilliseconds(200), HeartbeatInterval = TimeSpan.FromMilliseconds(20) },
            logs);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => node.StartAsync().WaitAsync(_timeout));

        // A heartbeat from a node that never started would be a node reporting itself alive: none should arrive in a
        // window several intervals long.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => logs.WaitForAsync("Heartbeat:", TimeSpan.FromMilliseconds(150)));
        Assert.False(node.IsRunning);
        await node.DisposeAsync().AsTask().WaitAsync(_timeout);
    }
// Why: R10.8. A node that checked itself against its venue once, at start-up, was right once. Everything that
    // happens afterwards - a fill whose event never arrived, an order cancelled at the venue, a position somebody
    // closed by hand - left it trading on a picture that had quietly gone stale, and nothing would say so until the
    // next restart. The check now runs while the node runs, and what it finds is counted where a host can see it.
    [Fact]
    public async Task The_node_keeps_checking_itself_against_its_venues_while_it_runs()
    {
        Rig rig = null!;
        rig = new Rig(exec => exec.MassStatus = new ExecutionMassStatus(exec.ClientId, exec.AccountId, exec.Venue, [], [], [], UnixNanos.FromSeconds(1_700_000_001), Guid.NewGuid()));
        await using TradingNode node = rig.Node(Rig.Config() with { ReconciliationInterval = TimeSpan.FromMilliseconds(100) });

        await node.StartAsync().WaitAsync(_timeout);
        await WaitUntilAsync(() => rig.Exec.MassStatusSince.Count >= 3);

        // The first is the one at start-up; the others are the periodic check.
        Assert.True(rig.Exec.MassStatusSince.Count >= 3, $"only {rig.Exec.MassStatusSince.Count} checks");
        Assert.True(node.Kernel.ExecutionEngine.ReconciliationCount >= 3, "the engine did not reconcile what the venue reported");
        Assert.NotNull(node.Kernel.ExecutionEngine.LastReconciliation);
        await node.StopAsync().WaitAsync(_timeout);

        // And it stops when the node stops. A check the node had already asked for can still arrive after the stop
        // returns - it was in flight - so what has to be true is that the count SETTLES, not that it has settled
        // within some number of milliseconds. Waiting a fixed 300 ms and asserting no growth was itself a wall-clock
        // assumption: under the whole suite running at once an in-flight check landed in the second window and the
        // test failed for a reason that was never the node's. So: wait for it to stop moving, then hold it.
        int settled = await SettlesAtAsync(() => rig.Exec.MassStatusSince.Count);
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Equal(settled, rig.Exec.MassStatusSince.Count);
    }

    [Fact]
    public async Task A_node_that_was_not_asked_to_keep_checking_checks_once()
    {
        Rig rig = new();
        await using TradingNode node = rig.Node();

        await node.StartAsync().WaitAsync(_timeout);

        // Wait for the one check it does make rather than guessing how long it takes, then hold long enough to see a
        // second if the node were going to make one: the claim is "once", which is a count that arrives and a count
        // that then stops moving.
        await WaitUntilAsync(() => rig.Exec.MassStatusSince.Count >= 1);
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        Assert.Single(rig.Exec.MassStatusSince);
        await node.StopAsync().WaitAsync(_timeout);
    }

    [Fact]
    public async Task Every_check_after_the_first_asks_only_for_what_has_happened_since_the_last_one()
    {
        // The check at start-up looks back over the whole lookback; asking a venue for a day of fills every interval
        // would be a day of fills every interval, for nothing.
        Rig rig = new();
        await using TradingNode node = rig.Node(Rig.Config() with
        {
            ReconciliationLookback = TimeSpan.FromDays(1),
            ReconciliationInterval = TimeSpan.FromMilliseconds(100),
        });

        await node.StartAsync().WaitAsync(_timeout);
        await WaitUntilAsync(() => rig.Exec.MassStatusSince.Count >= 2);
        await node.StopAsync().WaitAsync(_timeout);

        List<UnixNanos?> asked = rig.Exec.MassStatusSince.ToList();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.InRange(asked[0]!.Value.ToDateTimeOffset(), now.AddDays(-1).AddMinutes(-1), now.AddDays(-1).AddMinutes(1));
        Assert.All(asked.Skip(1), since => Assert.InRange(since!.Value.ToDateTimeOffset(), now.AddMinutes(-1), now.AddMinutes(1)));
    }

    [Fact]
    public async Task A_check_is_skipped_while_an_order_is_in_flight()
    {
        // The venue has been sent an order and has not answered yet. Its report cannot include what it has not
        // acknowledged, so a check now would find a difference that is only a conversation in progress - and adopt a
        // position the fill is about to account for.
        Rig rig = new(exec => exec.AcceptNothing = true);
        await using TradingNode node = rig.Node(Rig.Config() with { ReconciliationInterval = TimeSpan.FromMilliseconds(100) });
        ProbeStrategy strategy = new(journal: rig.Journal);
        node.AddStrategy(strategy);

        await node.StartAsync().WaitAsync(_timeout);
        int afterStart = rig.Exec.MassStatusSince.Count;

        await node.Loop.InvokeAsync(() => strategy.Submit(strategy.Orders.Market(rig.Instrument.Id, OrderSide.Buy, rig.Instrument.MakeQuantity(0.1m)))).WaitAsync(_timeout);
        await WaitUntilAsync(() => node.Kernel.Cache.OrdersInflight().Count > 0);
        await Task.Delay(TimeSpan.FromMilliseconds(400));

        Assert.Equal(afterStart, rig.Exec.MassStatusSince.Count);
        await node.StopAsync().WaitAsync(_timeout);
    }

    [Fact]
    public async Task What_the_venue_knew_and_the_node_did_not_is_counted_where_a_host_can_see_it()
    {
        // A fill the node never received: the venue reports the order filled, and the node has it as accepted.
        Rig rig = null!;
        rig = new Rig(exec =>
        {
            InstrumentId id = rig.Instrument.Id;
            OrderStatusReport report = new(exec.AccountId, id, new ClientOrderId("EXT-9"), new VenueOrderId("V-99"), OrderSide.Buy, OrderType.Limit, TimeInForce.Gtc, OrderStatus.Accepted,
                rig.Instrument.MakeQuantity(0.2m), rig.Instrument.MakeQuantity(0m), UnixNanos.FromSeconds(1_700_000_000), UnixNanos.FromSeconds(1_700_000_000), UnixNanos.FromSeconds(1_700_000_001), Guid.NewGuid(),
                rig.Instrument.MakePrice(25_000m));
            exec.MassStatus = new ExecutionMassStatus(exec.ClientId, exec.AccountId, exec.Venue, [report], [], [], UnixNanos.FromSeconds(1_700_000_001), Guid.NewGuid());
        });
        await using TradingNode node = rig.Node();

        await node.StartAsync().WaitAsync(_timeout);

        Assert.Equal(1, node.Kernel.ExecutionEngine.ReconciliationCount);
        Assert.Equal(1, node.Kernel.ExecutionEngine.ReconciledDifferences);
        await node.StopAsync().WaitAsync(_timeout);
    }

    /// <summary>Waits for a condition the kernel or a venue call will bring about, or fails the test.</summary>
    /// <summary>
    /// The value a counter comes to rest at: sampled until two readings a quarter of a second apart agree. How long
    /// that takes is the machine's business; that it happens at all is the node's.
    /// </summary>
    private static async Task<int> SettlesAtAsync(Func<int> count)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + _timeout;
        int last = count();
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            int now = count();
            if (now == last)
            {
                return now;
            }

            last = now;
        }

        Assert.Fail($"the count never stopped moving; it was {last}");
        return last;
    }

    private static async Task WaitUntilAsync(Func<bool> until)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + _timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (until())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }

        Assert.Fail("the condition never came about");
    }
}
