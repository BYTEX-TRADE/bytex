using Bytex.Core.Model;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Model.Reports;
using Bytex.Core.Tests.Support;
using Bytex.Core.Trading;

namespace Bytex.Core.Tests.Trading;

// Why: the moduleHost owns the runtimeModules' lifecycle as a group. Start order, reverse stop order, unique ids and the
// hand-over of OMS type and external order claims to the execution engine are all observable contracts.
public class ModuleHostTests
{
    private static ProbeRuntimeModule NewRuntimeModule(string id) => new(new RuntimeModuleConfig { RuntimeModuleId = new RuntimeModuleId(id) });

    private static ProbeStrategy NewStrategy(string id, OmsType oms = OmsType.Unspecified, params MarketKey[] claims) =>
        new(new StrategyConfig { StrategyId = new StrategyId(id), OmsType = oms, ExternalOrderClaims = claims });

    /// <summary>
    /// A strategy whose orders name an execution algorithm, the way a document whose order nodes ask to be worked in
    /// pieces does: without it, every one of its orders would be denied.
    /// </summary>
    private sealed class NeedyStrategy : ProbeStrategy, INeedsOrderSchedules
    {
        private readonly Func<OrderSchedule> _algorithm;

        public NeedyStrategy(string id, Func<OrderSchedule> algorithm)
            : base(new StrategyConfig { StrategyId = new StrategyId(id) }) => _algorithm = algorithm;

        public int Asked { get; private set; }

        public IEnumerable<OrderSchedule> RequiredOrderSchedules()
        {
            Asked++;
            yield return _algorithm();
        }
    }

    private static TwapSchedule NewTwap() => new();

    [Fact]
    public void Add_runtimeModule_sorts_components_into_actors_and_strategies_and_registers_them()
    {
        using TradingRuntimeHarness h = new();
        ProbeRuntimeModule runtimeModule = NewRuntimeModule("Monitor-001");
        ProbeStrategy strategy = NewStrategy("S-001");

        h.TradingRuntime.ModuleHost.AddRuntimeModules([runtimeModule, strategy]);

        Assert.Same(runtimeModule, Assert.Single(h.TradingRuntime.ModuleHost.RuntimeModules));
        Assert.Same(strategy, Assert.Single(h.TradingRuntime.ModuleHost.Strategies));
        Assert.True(runtimeModule.IsRegistered);
        Assert.True(strategy.IsRegistered);
        Assert.Same(strategy, h.TradingRuntime.ModuleHost.Strategy(new StrategyId("S-001")));
        Assert.Same(runtimeModule, h.TradingRuntime.ModuleHost.RuntimeModule(new RuntimeModuleId("Monitor-001")));
        Assert.Same(strategy, h.TradingRuntime.ModuleHost.RuntimeModule(new RuntimeModuleId("S-001")));
        Assert.Null(h.TradingRuntime.ModuleHost.Strategy(new StrategyId("S-404")));
    }

    [Fact]
    public void Two_components_with_the_same_id_are_refused()
    {
        using TradingRuntimeHarness h = new();
        h.TradingRuntime.ModuleHost.AddRuntimeModule(NewRuntimeModule("Same-001"));

        Assert.Throws<InvalidOperationException>(() => h.TradingRuntime.ModuleHost.AddRuntimeModule(NewRuntimeModule("Same-001")));
        Assert.Throws<InvalidOperationException>(() => h.TradingRuntime.ModuleHost.AddStrategy(NewStrategy("Same-001")));
    }

    [Fact]
    public void Start_runs_actors_before_strategies_and_stop_unwinds_in_reverse()
    {
        using TradingRuntimeHarness h = new();
        List<string> order = new();
        ProbeStrategy s1 = NewStrategy("S-001");
        ProbeRuntimeModule a1 = NewRuntimeModule("A-001");
        ProbeStrategy s2 = NewStrategy("S-002");
        ProbeRuntimeModule a2 = NewRuntimeModule("A-002");
        foreach (RuntimeModule component in new RuntimeModule[] { s1, a1, s2, a2 })
        {
            component.StateChanged += e =>
            {
                if (e.State is ComponentState.Running or ComponentState.Stopped)
                {
                    order.Add($"{e.ComponentId}:{e.State}");
                }
            };
            h.TradingRuntime.ModuleHost.AddRuntimeModule(component);
        }

        h.TradingRuntime.ModuleHost.Start();
        h.TradingRuntime.ModuleHost.Stop();

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
        using TradingRuntimeHarness h = new();
        ProbeRuntimeModule broken = NewRuntimeModule("Broken-001");
        broken.ThrowIn = "OnStart";
        ProbeRuntimeModule fine = NewRuntimeModule("Fine-001");
        h.TradingRuntime.ModuleHost.AddRuntimeModules([broken, fine]);

        h.TradingRuntime.ModuleHost.Start();

        Assert.Equal(ComponentState.Faulted, broken.State);
        Assert.Equal(ComponentState.Running, fine.State);
        Assert.True(h.TradingRuntime.ModuleHost.IsRunning);
    }

    [Fact]
    public void Strategy_oms_type_is_handed_to_the_order_coordinator()
    {
        using TradingRuntimeHarness h = new(clientOms: OmsType.Netting);
        ProbeStrategy strategy = NewStrategy("S-001", OmsType.Hedging);
        h.TradingRuntime.ModuleHost.AddStrategy(strategy);
        h.TradingRuntime.Start();

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
    public void Strategy_external_order_claims_are_handed_to_the_order_coordinator()
    {
        using TradingRuntimeHarness h = new();
        h.TradingRuntime.ModuleHost.AddStrategy(NewStrategy("S-001", OmsType.Unspecified, TestIds.BtcUsdt));
        OrderStatusReport report = new(
            TestIds.BinanceAccount, TestIds.BtcUsdt, null, new VenueOrderId("V-EXT"), OrderSide.Buy, OrderType.Limit, TimeInForce.Gtc, OrderStatus.Accepted,
            Quantity.Parse("1.000"), Quantity.Parse("0.000"), TestOrders.T0, TestOrders.T0, TestOrders.T0, Guid.NewGuid(), Price: Price.Parse("50000.00"));

        h.TradingRuntime.OrderCoordinator.ReconcileOrderReport(report, []);

        Assert.Equal(new StrategyId("S-001"), Assert.Single(h.Cache.Orders()).StrategyId);
    }

    [Fact]
    public void Reset_resets_every_stopped_component()
    {
        using TradingRuntimeHarness h = new();
        ProbeRuntimeModule runtimeModule = NewRuntimeModule("A-001");
        h.TradingRuntime.ModuleHost.AddRuntimeModule(runtimeModule);
        h.TradingRuntime.ModuleHost.Start();
        h.TradingRuntime.ModuleHost.Stop();

        h.TradingRuntime.ModuleHost.Reset();

        Assert.Equal(ComponentState.Ready, runtimeModule.State);
        Assert.Contains("OnReset", runtimeModule.Calls);
    }

    [Fact]
    public void Dispose_disposes_every_component_and_empties_the_moduleHost()
    {
        using TradingRuntimeHarness h = new();
        ProbeRuntimeModule runtimeModule = NewRuntimeModule("A-001");
        ProbeStrategy strategy = NewStrategy("S-001");
        h.TradingRuntime.ModuleHost.AddRuntimeModules([runtimeModule, strategy]);
        h.TradingRuntime.ModuleHost.Start();

        h.TradingRuntime.ModuleHost.Dispose();

        Assert.Equal(ComponentState.Disposed, runtimeModule.State);
        Assert.Equal(ComponentState.Disposed, strategy.State);
        Assert.Empty(h.TradingRuntime.ModuleHost.AllComponents);
    }

    [Fact]
    public void Clear_is_refused_while_running_and_otherwise_removes_everything()
    {
        using TradingRuntimeHarness h = new();
        ProbeRuntimeModule runtimeModule = NewRuntimeModule("A-001");
        h.TradingRuntime.ModuleHost.AddRuntimeModule(runtimeModule);
        h.TradingRuntime.ModuleHost.Start();

        Assert.Throws<InvalidOperationException>(h.TradingRuntime.ModuleHost.Clear);
        h.TradingRuntime.ModuleHost.Stop();
        h.TradingRuntime.ModuleHost.Clear();

        Assert.Empty(h.TradingRuntime.ModuleHost.AllComponents);
        Assert.Equal(ComponentState.Disposed, runtimeModule.State);
        h.TradingRuntime.ModuleHost.AddRuntimeModule(NewRuntimeModule("A-001")); // the id is free again
    }

    [Fact]
    public void An_algorithm_a_strategy_cannot_do_without_is_registered_with_it()
    {
        // Nothing the host has to know: the strategy says what its orders name, and the moduleHost has it running before
        // the first bar. Without this, a document asking for a paced order would have every order denied.
        using TradingRuntimeHarness h = new();
        NeedyStrategy strategy = new("S-001", NewTwap);

        h.TradingRuntime.ModuleHost.AddStrategy(strategy);

        OrderSchedule registered = Assert.Single(h.TradingRuntime.ModuleHost.OrderSchedules);
        Assert.Equal(TwapSchedule.DefaultOrderScheduleId, registered.OrderScheduleId);
        Assert.True(registered.IsRegistered);
        Assert.Equal(1, strategy.Asked);
        Assert.Same(registered, h.TradingRuntime.ModuleHost.RuntimeModule(new RuntimeModuleId(TwapSchedule.DefaultOrderScheduleId.Value)));
    }

    [Fact]
    public void Two_strategies_asking_for_the_same_algorithm_get_the_one_that_is_already_running()
    {
        using TradingRuntimeHarness h = new();

        h.TradingRuntime.ModuleHost.AddStrategy(new NeedyStrategy("S-001", NewTwap));
        h.TradingRuntime.ModuleHost.AddStrategy(new NeedyStrategy("S-002", NewTwap));

        // One algorithm, not two under one id and not an exception: the second strategy asked for the id, and the id
        // is running.
        Assert.Single(h.TradingRuntime.ModuleHost.OrderSchedules);
    }

    [Fact]
    public void The_hosts_own_algorithm_runs_instead_of_the_one_a_strategy_asked_for_whichever_came_first()
    {
        // A host that has tuned its own TWAP gets to use it, and it must not matter whether it registered before or
        // after the strategy that needs one - the strategy asked for the id, not for that instance of it.
        foreach (bool hostFirst in new[] { true, false })
        {
            using TradingRuntimeHarness h = new();
            TwapSchedule mine = new(new TwapScheduleConfig { Horizon = TimeSpan.FromHours(1), Interval = TimeSpan.FromMinutes(10) });
            NeedyStrategy strategy = new("S-001", NewTwap);
            if (hostFirst)
            {
                h.TradingRuntime.ModuleHost.AddOrderSchedule(mine);
                h.TradingRuntime.ModuleHost.AddStrategy(strategy);
            }
            else
            {
                h.TradingRuntime.ModuleHost.AddStrategy(strategy);
                h.TradingRuntime.ModuleHost.AddOrderSchedule(mine);
            }

            Assert.Same(mine, Assert.Single(h.TradingRuntime.ModuleHost.OrderSchedules));
            Assert.Equal(TimeSpan.FromHours(1), ((TwapSchedule)Assert.Single(h.TradingRuntime.ModuleHost.OrderSchedules)).Config.Horizon);
            Assert.Same(mine, h.TradingRuntime.ModuleHost.RuntimeModule(new RuntimeModuleId(TwapSchedule.DefaultOrderScheduleId.Value)));
        }
    }

    [Fact]
    public void A_twap_answers_to_the_name_a_document_calls_it_by()
    {
        // The id is the contract between a document that says "twap" and the algorithm that works the order; a default
        // taken from the class name would leave every such order denied.
        Assert.Equal("TWAP", TwapSchedule.DefaultOrderScheduleId.Value);
        Assert.Equal(TwapSchedule.DefaultOrderScheduleId, new TwapSchedule().OrderScheduleId);
        Assert.Equal(new OrderScheduleId("MyTwap"), new TwapSchedule(new TwapScheduleConfig { OrderScheduleId = new OrderScheduleId("MyTwap") }).OrderScheduleId);
    }

    // ----- taking one off again (R5.10) -----

    [Fact]
    public void A_strategy_is_removed_and_its_id_is_free_again()
    {
        using TradingRuntimeHarness h = new();
        h.TradingRuntime.ModuleHost.AddStrategy(NewStrategy("S-001"));

        Assert.True(h.TradingRuntime.ModuleHost.RemoveStrategy(new StrategyId("S-001")));

        Assert.Empty(h.TradingRuntime.ModuleHost.Strategies);
        Assert.False(h.TradingRuntime.ModuleHost.RemoveStrategy(new StrategyId("S-001")), "there is nothing left to remove");
        h.TradingRuntime.ModuleHost.AddStrategy(NewStrategy("S-001"));
        Assert.Single(h.TradingRuntime.ModuleHost.Strategies);
    }

    [Fact]
    public void A_running_strategy_is_not_removed_from_under_itself()
    {
        // It holds subscriptions, timers and possibly orders it is managing. Disposing it where it stands would leave
        // those to nobody, so this refuses rather than deciding what should happen to them.
        using TradingRuntimeHarness h = new();
        ProbeStrategy strategy = NewStrategy("S-001");
        h.TradingRuntime.ModuleHost.AddStrategy(strategy);
        h.TradingRuntime.ModuleHost.Start();

        InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => h.TradingRuntime.ModuleHost.RemoveStrategy(new StrategyId("S-001")));

        Assert.Contains("has to be stopped", e.Message, StringComparison.Ordinal);
        Assert.Single(h.TradingRuntime.ModuleHost.Strategies);
    }

    [Fact]
    public void An_runtimeModule_is_removed_on_the_same_terms_as_a_strategy()
    {
        using TradingRuntimeHarness h = new();
        ProbeRuntimeModule runtimeModule = NewRuntimeModule("Monitor-001");
        h.TradingRuntime.ModuleHost.AddRuntimeModule(runtimeModule);
        h.TradingRuntime.ModuleHost.Start();

        Assert.Throws<InvalidOperationException>(() => h.TradingRuntime.ModuleHost.RemoveRuntimeModule(new RuntimeModuleId("Monitor-001")));

        runtimeModule.Stop();
        Assert.True(h.TradingRuntime.ModuleHost.RemoveRuntimeModule(new RuntimeModuleId("Monitor-001")));
        Assert.Empty(h.TradingRuntime.ModuleHost.RuntimeModules);
        Assert.False(h.TradingRuntime.ModuleHost.RemoveRuntimeModule(new RuntimeModuleId("Nobody-001")));
    }

    [Fact]
    public void A_removed_strategy_gives_up_the_instruments_it_claimed()
    {
        // The claims are the reason removal has to reach into the execution engine at all. They are keyed by instrument
        // and refuse a second claimant by name, so a strategy taken off without clearing them leaves its instruments
        // claimed by something that is gone - and the refusal names a strategy the operator can no longer see.
        using TradingRuntimeHarness h = new();
        h.TradingRuntime.ModuleHost.AddStrategy(NewStrategy("First-001", OmsType.Unspecified, TestIds.BtcUsdt));

        h.TradingRuntime.ModuleHost.RemoveStrategy(new StrategyId("First-001"));

        h.TradingRuntime.ModuleHost.AddStrategy(NewStrategy("Second-001", OmsType.Unspecified, TestIds.BtcUsdt));
        Assert.Single(h.TradingRuntime.ModuleHost.Strategies);
    }

    [Fact]
    public void An_execution_algorithm_registered_for_a_strategy_goes_when_the_strategy_does()
    {
        // It was registered because the strategy asked for it, so it has nothing left to work once the strategy is gone.
        using TradingRuntimeHarness h = new();
        NeedyStrategy needy = new("S-001", NewTwap);
        h.TradingRuntime.ModuleHost.AddStrategy(needy);
        Assert.Single(h.TradingRuntime.ModuleHost.OrderSchedules);

        h.TradingRuntime.ModuleHost.RemoveStrategy(new StrategyId("S-001"));

        Assert.Empty(h.TradingRuntime.ModuleHost.OrderSchedules);
    }

    [Fact]
    public void An_execution_algorithm_the_host_registered_itself_stays()
    {
        // Nobody asked for it to go, and another strategy may be working orders through it.
        using TradingRuntimeHarness h = new();
        h.TradingRuntime.ModuleHost.AddOrderSchedule(NewTwap());
        NeedyStrategy needy = new("S-001", NewTwap);
        h.TradingRuntime.ModuleHost.AddStrategy(needy);

        h.TradingRuntime.ModuleHost.RemoveStrategy(new StrategyId("S-001"));

        Assert.Single(h.TradingRuntime.ModuleHost.OrderSchedules);
    }

    [Fact]
    public void State_is_saved_and_loaded_for_every_component_even_if_one_of_them_fails()
    {
        using TradingRuntimeHarness h = new();
        ProbeRuntimeModule failing = NewRuntimeModule("Failing-001");
        failing.ThrowIn = "OnSave";
        ProbeRuntimeModule saving = NewRuntimeModule("Saving-001");
        saving.StateToSave = new Dictionary<string, byte[]> { ["k"] = [42] };
        h.TradingRuntime.ModuleHost.AddRuntimeModules([failing, saving]);

        h.TradingRuntime.ModuleHost.SaveState();
        h.TradingRuntime.ModuleHost.LoadState();

        Assert.Equal(new byte[] { 42 }, saving.LoadedState!["k"]);
        Assert.Null(failing.LoadedState);
    }
}
