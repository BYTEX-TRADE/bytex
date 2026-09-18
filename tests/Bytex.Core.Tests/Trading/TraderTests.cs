using Bytex.Core.Model;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Model.Reports;
using Bytex.Core.Tests.Support;
using Bytex.Core.Trading;

namespace Bytex.Core.Tests.Trading;

// Why: the trader owns the actors' lifecycle as a group. Start order, reverse stop order, unique ids and the
// hand-over of OMS type and external order claims to the execution engine are all observable contracts.
public class TraderTests
{
    private static ProbeActor NewActor(string id) => new(new ActorConfig { ActorId = new ActorId(id) });

    private static ProbeStrategy NewStrategy(string id, OmsType oms = OmsType.Unspecified, params InstrumentId[] claims) =>
        new(new StrategyConfig { StrategyId = new StrategyId(id), OmsType = oms, ExternalOrderClaims = claims });

    [Fact]
    public void Add_actor_sorts_components_into_actors_and_strategies_and_registers_them()
    {
        using KernelHarness h = new();
        ProbeActor actor = NewActor("Monitor-001");
        ProbeStrategy strategy = NewStrategy("S-001");

        h.Kernel.Trader.AddActors([actor, strategy]);

        Assert.Same(actor, Assert.Single(h.Kernel.Trader.Actors));
        Assert.Same(strategy, Assert.Single(h.Kernel.Trader.Strategies));
        Assert.True(actor.IsRegistered);
        Assert.True(strategy.IsRegistered);
        Assert.Same(strategy, h.Kernel.Trader.Strategy(new StrategyId("S-001")));
        Assert.Same(actor, h.Kernel.Trader.Actor(new ActorId("Monitor-001")));
        Assert.Same(strategy, h.Kernel.Trader.Actor(new ActorId("S-001")));
        Assert.Null(h.Kernel.Trader.Strategy(new StrategyId("S-404")));
    }

    [Fact]
    public void Two_components_with_the_same_id_are_refused()
    {
        using KernelHarness h = new();
        h.Kernel.Trader.AddActor(NewActor("Same-001"));

        Assert.Throws<InvalidOperationException>(() => h.Kernel.Trader.AddActor(NewActor("Same-001")));
        Assert.Throws<InvalidOperationException>(() => h.Kernel.Trader.AddStrategy(NewStrategy("Same-001")));
    }

    [Fact]
    public void Start_runs_actors_before_strategies_and_stop_unwinds_in_reverse()
    {
        using KernelHarness h = new();
        List<string> order = new();
        ProbeStrategy s1 = NewStrategy("S-001");
        ProbeActor a1 = NewActor("A-001");
        ProbeStrategy s2 = NewStrategy("S-002");
        ProbeActor a2 = NewActor("A-002");
        foreach (Actor component in new Actor[] { s1, a1, s2, a2 })
        {
            component.StateChanged += e =>
            {
                if (e.State is ComponentState.Running or ComponentState.Stopped)
                {
                    order.Add($"{e.ComponentId}:{e.State}");
                }
            };
            h.Kernel.Trader.AddActor(component);
        }

        h.Kernel.Trader.Start();
        h.Kernel.Trader.Stop();

        Assert.Equal(
            [
                "A-001:Running", "A-002:Running", "S-001:Running", "S-002:Running",
                "S-002:Stopped", "S-001:Stopped", "A-002:Stopped", "A-001:Stopped",
            ],
            order);
    }

    [Fact]
    public void Component_that_fails_to_start_is_faulted_without_stopping_the_others()
    {
        using KernelHarness h = new();
        ProbeActor broken = NewActor("Broken-001");
        broken.ThrowIn = "OnStart";
        ProbeActor fine = NewActor("Fine-001");
        h.Kernel.Trader.AddActors([broken, fine]);

        h.Kernel.Trader.Start();

        Assert.Equal(ComponentState.Faulted, broken.State);
        Assert.Equal(ComponentState.Running, fine.State);
        Assert.True(h.Kernel.Trader.IsRunning);
    }

    [Fact]
    public void Strategy_oms_type_is_handed_to_the_execution_engine()
    {
        using KernelHarness h = new(clientOms: OmsType.Netting);
        ProbeStrategy strategy = NewStrategy("S-001", OmsType.Hedging);
        h.Kernel.Trader.AddStrategy(strategy);
        h.Kernel.Start();

        foreach (string id in new[] { "1", "2" })
        {
            MarketOrder order = strategy.Factory.Market(TestIds.BtcUsdt, OrderSide.Buy, Quantity.Parse("1.000"));
            strategy.DoSubmit(order);
            h.Accept(order);
            h.Client.EmitFilled(order, "T-" + id, "1.000", "50000.00");
        }

        // Under the client's NETTING there would be one position of 2.000; the strategy asked for HEDGING.
        Assert.Equal(2, h.Cache.PositionsOpenCount());
    }

    [Fact]
    public void Strategy_external_order_claims_are_handed_to_the_execution_engine()
    {
        using KernelHarness h = new();
        h.Kernel.Trader.AddStrategy(NewStrategy("S-001", OmsType.Unspecified, TestIds.BtcUsdt));
        OrderStatusReport report = new(
            TestIds.BinanceAccount, TestIds.BtcUsdt, null, new VenueOrderId("V-EXT"), OrderSide.Buy, OrderType.Limit, TimeInForce.Gtc, OrderStatus.Accepted,
            Quantity.Parse("1.000"), Quantity.Parse("0.000"), TestOrders.T0, TestOrders.T0, TestOrders.T0, Guid.NewGuid(), Price: Price.Parse("50000.00"));

        h.Kernel.ExecutionEngine.ReconcileOrderReport(report, []);

        Assert.Equal(new StrategyId("S-001"), Assert.Single(h.Cache.Orders()).StrategyId);
    }

    [Fact]
    public void Reset_resets_every_stopped_component()
    {
        using KernelHarness h = new();
        ProbeActor actor = NewActor("A-001");
        h.Kernel.Trader.AddActor(actor);
        h.Kernel.Trader.Start();
        h.Kernel.Trader.Stop();

        h.Kernel.Trader.Reset();

        Assert.Equal(ComponentState.Ready, actor.State);
        Assert.Contains("OnReset", actor.Calls);
    }

    [Fact]
    public void Dispose_disposes_every_component_and_empties_the_trader()
    {
        using KernelHarness h = new();
        ProbeActor actor = NewActor("A-001");
        ProbeStrategy strategy = NewStrategy("S-001");
        h.Kernel.Trader.AddActors([actor, strategy]);
        h.Kernel.Trader.Start();

        h.Kernel.Trader.Dispose();

        Assert.Equal(ComponentState.Disposed, actor.State);
        Assert.Equal(ComponentState.Disposed, strategy.State);
        Assert.Empty(h.Kernel.Trader.AllComponents);
    }

    [Fact]
    public void Clear_is_refused_while_running_and_otherwise_removes_everything()
    {
        using KernelHarness h = new();
        ProbeActor actor = NewActor("A-001");
        h.Kernel.Trader.AddActor(actor);
        h.Kernel.Trader.Start();

        Assert.Throws<InvalidOperationException>(h.Kernel.Trader.Clear);
        h.Kernel.Trader.Stop();
        h.Kernel.Trader.Clear();

        Assert.Empty(h.Kernel.Trader.AllComponents);
        Assert.Equal(ComponentState.Disposed, actor.State);
        h.Kernel.Trader.AddActor(NewActor("A-001")); // the id is free again
    }

    [Fact]
    public void State_is_saved_and_loaded_for_every_component_even_if_one_of_them_fails()
    {
        using KernelHarness h = new();
        ProbeActor failing = NewActor("Failing-001");
        failing.ThrowIn = "OnSave";
        ProbeActor saving = NewActor("Saving-001");
        saving.StateToSave = new Dictionary<string, byte[]> { ["k"] = [42] };
        h.Kernel.Trader.AddActors([failing, saving]);

        h.Kernel.Trader.SaveState();
        h.Kernel.Trader.LoadState();

        Assert.Equal(new byte[] { 42 }, saving.LoadedState!["k"]);
        Assert.Null(failing.LoadedState);
    }
}
