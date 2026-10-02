using Bytex.Core.TradingRuntime;
using Bytex.Core.Model;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Plugins;
using Bytex.Core.Trading;
using Bytex.Live.Control;
using Bytex.Live.Tests.Support;

namespace Bytex.Live.Tests;

// Why (R5.10): a node that has to be restarted to gain or lose a strategy is a node whose other strategies are stopped
// to change one. What is pinned here is not only that adding, starting, stopping and removing work while it runs, but
// what each of them refuses - because every refusal is a decision nobody would want made for them:
//
//   * a strategy is added STOPPED, so nothing trades on a decision that was never taken;
//   * stopping says what happens to the orders and positions it was holding, and does nothing to them unless asked;
//   * a strategy still holding orders or positions is not removed at all, because removing it takes away the thing
//     that would manage them.
public sealed class NodeControllerTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(20);

    private sealed record Rig(TradingNode Node, NodeController Controller, FakeExecutionClient Exec, Instrument Instrument) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Node.DisposeAsync();
    }

    private static async Task<Rig> StartAsync(Func<string, Strategy>? fromPath = null, Strategy? initial = null)
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

        await node.StartAsync().WaitAsync(_timeout);
        return new Rig(node, new NodeController(node), execFactory.Created.Single().Client, instrument);
    }

    private static StrategyStatus Single(ControllerOutcome outcome, string id) =>
        outcome.Strategies.Single(s => s.Id == id);

    // ----- adding -----

    [Fact]
    public async Task A_strategy_added_while_the_node_runs_is_added_stopped()
    {
        // The node is trading. A strategy that started itself on arrival would be trading on nobody's decision.
        await using Rig rig = await StartAsync();

        ControllerOutcome outcome = await rig.Controller.AddAsync(new ProbeStrategy("Added-001")).WaitAsync(_timeout);

        Assert.True(outcome.Done);
        Assert.Equal("Ready", Single(outcome, "Added-001").State);
        Assert.True(rig.Node.IsRunning);
    }

    [Fact]
    public async Task A_strategy_that_is_started_runs_and_sees_what_arrives_after_it()
    {
        await using Rig rig = await StartAsync();
        ProbeStrategy added = new("Added-001") { SubscribeTo = rig.Instrument.Id };
        await rig.Controller.AddAsync(added).WaitAsync(_timeout);

        ControllerOutcome outcome = await rig.Controller.StartAsync(new StrategyId("Added-001")).WaitAsync(_timeout);

        Assert.True(outcome.Done);
        Assert.Equal("Running", Single(outcome, "Added-001").State);
        Assert.Equal(ComponentState.Running, added.State);
    }

    [Fact]
    public async Task Two_strategies_cannot_share_an_id()
    {
        await using Rig rig = await StartAsync(initial: new ProbeStrategy("Probe-001"));

        ControllerOutcome outcome = await rig.Controller.AddAsync(new ProbeStrategy("Probe-001")).WaitAsync(_timeout);

        Assert.False(outcome.Done);
        Assert.Contains("already has a strategy called Probe-001", outcome.Refused, StringComparison.Ordinal);
        Assert.Single(outcome.Strategies);
    }

    [Fact]
    public async Task A_node_that_was_not_given_a_way_to_read_a_strategy_says_so()
    {
        // Rather than failing with something about a file: what a path means is the host's to decide, and a node that
        // was not told cannot guess.
        await using Rig rig = await StartAsync();

        Assert.False(rig.Controller.CanReadStrategies);

        ControllerOutcome outcome = await rig.Controller.AddFromPathAsync("anything.json").WaitAsync(_timeout);

        Assert.False(outcome.Done);
        Assert.Contains("StrategyFromPath", outcome.Refused, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_strategy_is_read_from_a_path_by_whatever_the_node_was_given()
    {
        string asked = string.Empty;
        await using Rig rig = await StartAsync(fromPath: path =>
        {
            asked = path;
            return new ProbeStrategy("FromFile-001");
        });

        ControllerOutcome outcome = await rig.Controller.AddFromPathAsync("strategies/ema.json").WaitAsync(_timeout);

        Assert.True(outcome.Done);
        Assert.Equal("strategies/ema.json", asked);
        Assert.Equal("Ready", Single(outcome, "FromFile-001").State);
    }

    [Fact]
    public async Task A_path_that_reads_as_nothing_is_a_refusal_and_not_a_crash()
    {
        // The node goes on trading whatever a host sent it.
        await using Rig rig = await StartAsync(fromPath: _ => throw new FormatException("that is not a strategy document"));

        ControllerOutcome outcome = await rig.Controller.AddFromPathAsync("notes.txt").WaitAsync(_timeout);

        Assert.False(outcome.Done);
        Assert.Contains("that is not a strategy document", outcome.Refused, StringComparison.Ordinal);
        Assert.True(rig.Node.IsRunning);
        Assert.Empty(outcome.Strategies);
    }

    // ----- starting and stopping -----

    [Fact]
    public async Task A_strategy_that_is_already_running_is_not_started_twice()
    {
        await using Rig rig = await StartAsync(initial: new ProbeStrategy("Probe-001"));

        ControllerOutcome outcome = await rig.Controller.StartAsync(new StrategyId("Probe-001")).WaitAsync(_timeout);

        Assert.False(outcome.Done);
        Assert.Contains("already Running", outcome.Refused, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_strategy_nobody_has_heard_of_is_named_in_the_refusal()
    {
        await using Rig rig = await StartAsync();

        foreach (ControllerOutcome outcome in new[]
        {
            await rig.Controller.StartAsync(new StrategyId("Ghost-001")).WaitAsync(_timeout),
            await rig.Controller.StopAsync(new StrategyId("Ghost-001")).WaitAsync(_timeout),
            await rig.Controller.RemoveAsync(new StrategyId("Ghost-001")).WaitAsync(_timeout),
        })
        {
            Assert.False(outcome.Done);
            Assert.Contains("no strategy called Ghost-001", outcome.Refused, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Stopping_a_strategy_leaves_its_orders_where_they_are_unless_it_is_asked_otherwise()
    {
        // The dangerous default would be either one: cancelling what an operator meant to keep, or keeping what they
        // meant to cancel. So nothing happens to them unless the command says so, and the count is reported.
        ProbeStrategy probe = new("Probe-001");
        await using Rig rig = await StartAsync(initial: probe);
        await RestingOrderAsync(rig, probe).WaitAsync(_timeout);

        ControllerOutcome outcome = await rig.Controller.StopAsync(new StrategyId("Probe-001")).WaitAsync(_timeout);

        Assert.True(outcome.Done);
        Assert.Equal("Stopped", Single(outcome, "Probe-001").State);
        Assert.Equal(1, Single(outcome, "Probe-001").OpenOrders);
    }

    [Fact]
    public async Task Stopping_a_strategy_cancels_what_it_holds_when_that_is_asked_for()
    {
        ProbeStrategy probe = new("Probe-001");
        await using Rig rig = await StartAsync(initial: probe);
        await RestingOrderAsync(rig, probe).WaitAsync(_timeout);

        ControllerOutcome outcome = await rig.Controller.StopAsync(new StrategyId("Probe-001"), cancelOrders: true).WaitAsync(_timeout);

        Assert.True(outcome.Done);
        // The cancel is submitted while the strategy is still running, so its own cancel comes back to something that
        // is listening. The venue's answer may arrive after the stop, so this waits for the cache rather than the reply.
        await Eventually(rig, () => rig.Node.TradingRuntime.Cache.OrdersOpen(strategyId: new StrategyId("Probe-001")).Count == 0);
    }

    [Fact]
    public async Task A_strategy_that_is_not_running_is_not_stopped_again()
    {
        await using Rig rig = await StartAsync();
        await rig.Controller.AddAsync(new ProbeStrategy("Added-001")).WaitAsync(_timeout);

        ControllerOutcome outcome = await rig.Controller.StopAsync(new StrategyId("Added-001")).WaitAsync(_timeout);

        Assert.False(outcome.Done);
        Assert.Contains("is Ready, not running", outcome.Refused, StringComparison.Ordinal);
    }

    // ----- removing -----

    [Fact]
    public async Task A_stopped_strategy_holding_nothing_is_removed()
    {
        await using Rig rig = await StartAsync(initial: new ProbeStrategy("Probe-001"));
        await rig.Controller.StopAsync(new StrategyId("Probe-001")).WaitAsync(_timeout);

        ControllerOutcome outcome = await rig.Controller.RemoveAsync(new StrategyId("Probe-001")).WaitAsync(_timeout);

        Assert.True(outcome.Done);
        Assert.Empty(outcome.Strategies);
        Assert.Null(await rig.Node.Loop.InvokeAsync(() => rig.Node.TradingRuntime.ModuleHost.Strategy(new StrategyId("Probe-001"))).WaitAsync(_timeout));
    }

    [Fact]
    public async Task A_running_strategy_is_not_removed_from_under_itself()
    {
        await using Rig rig = await StartAsync(initial: new ProbeStrategy("Probe-001"));

        ControllerOutcome outcome = await rig.Controller.RemoveAsync(new StrategyId("Probe-001")).WaitAsync(_timeout);

        Assert.False(outcome.Done);
        Assert.Contains("has to be stopped before it can be removed", outcome.Refused, StringComparison.Ordinal);
        Assert.Single(outcome.Strategies);
    }

    [Fact]
    public async Task A_strategy_still_holding_a_working_order_is_not_removed()
    {
        // This is the one that matters. Removing it would leave an order at a venue with nothing on the node that knows
        // what it was for, and the next thing to notice would be a fill nobody expected.
        ProbeStrategy probe = new("Probe-001");
        await using Rig rig = await StartAsync(initial: probe);
        await RestingOrderAsync(rig, probe).WaitAsync(_timeout);
        await rig.Controller.StopAsync(new StrategyId("Probe-001")).WaitAsync(_timeout);

        ControllerOutcome outcome = await rig.Controller.RemoveAsync(new StrategyId("Probe-001")).WaitAsync(_timeout);

        Assert.False(outcome.Done);
        Assert.Contains("1 working order(s)", outcome.Refused, StringComparison.Ordinal);
        Assert.Contains("cancelOrders", outcome.Refused, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_removed_strategy_gives_up_the_instruments_it_claimed()
    {
        // The claim is registered with the execution engine, which refuses a second claimant by name. A strategy taken
        // off without clearing it makes its instrument unclaimable by anything else - and the refusal names a strategy
        // the operator can no longer see.
        await using Rig rig = await StartAsync(initial: new ProbeStrategy("First-001", claims: [TestInstruments.BtcUsdt("FAKE").Id]));
        await rig.Controller.StopAsync(new StrategyId("First-001")).WaitAsync(_timeout);
        await rig.Controller.RemoveAsync(new StrategyId("First-001")).WaitAsync(_timeout);

        ControllerOutcome outcome = await rig.Controller
            .AddAsync(new ProbeStrategy("Second-001", claims: [TestInstruments.BtcUsdt("FAKE").Id]))
            .WaitAsync(_timeout);

        Assert.True(outcome.Done, outcome.Refused);
    }

    [Fact]
    public async Task The_id_of_a_removed_strategy_can_be_used_again()
    {
        await using Rig rig = await StartAsync(initial: new ProbeStrategy("Probe-001"));
        await rig.Controller.StopAsync(new StrategyId("Probe-001")).WaitAsync(_timeout);
        await rig.Controller.RemoveAsync(new StrategyId("Probe-001")).WaitAsync(_timeout);

        ControllerOutcome outcome = await rig.Controller.AddAsync(new ProbeStrategy("Probe-001")).WaitAsync(_timeout);

        Assert.True(outcome.Done, outcome.Refused);
        Assert.Equal("Ready", Single(outcome, "Probe-001").State);
    }

    [Fact]
    public async Task Everything_the_node_holds_is_listed_with_what_each_strategy_is_holding()
    {
        ProbeStrategy probe = new("Probe-001");
        await using Rig rig = await StartAsync(initial: probe);
        await RestingOrderAsync(rig, probe).WaitAsync(_timeout);
        await rig.Controller.AddAsync(new ProbeStrategy("Added-001")).WaitAsync(_timeout);

        IReadOnlyList<StrategyStatus> listed = await rig.Controller.ListAsync().WaitAsync(_timeout);

        Assert.Equal(2, listed.Count);
        Assert.Equal("Running", listed.Single(s => s.Id == "Probe-001").State);
        Assert.Equal(1, listed.Single(s => s.Id == "Probe-001").OpenOrders);
        Assert.Equal("Ready", listed.Single(s => s.Id == "Added-001").State);
        Assert.Equal(0, listed.Single(s => s.Id == "Added-001").OpenOrders);
    }

    // ----- rig helpers -----

    /// <summary>A limit well below the market, so it rests at the venue rather than filling.</summary>
    private static async Task RestingOrderAsync(Rig rig, ProbeStrategy strategy)
    {
        await rig.Node.Loop.InvokeAsync(() =>
        {
            LimitOrder limit = strategy.Orders.Limit(
                rig.Instrument.Id, OrderSide.Buy, rig.Instrument.MakeQuantity(0.01m), rig.Instrument.MakePrice(1_000m));
            strategy.Submit(limit);
        }).WaitAsync(_timeout);

        await Eventually(rig, () => rig.Node.TradingRuntime.Cache.OrdersOpen(strategyId: strategy.StrategyId).Count == 1);
    }

    private static async Task Eventually(Rig rig, Func<bool> until)
    {
        DateTime deadline = DateTime.UtcNow + _timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await rig.Node.Loop.InvokeAsync(until).WaitAsync(_timeout))
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("the condition never came about");
    }
}
