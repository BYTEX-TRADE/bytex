using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Tests.Support;
using Bytex.Core.Trading;

namespace Bytex.Core.Tests.Trading;

// Why: R5.8 - the ExecAlgorithm contract ships in the release: parent orders carrying the algorithm id are
// routed to it, children are spawned as "{parent}-E{n}" and their events come back to the algorithm.
public class ExecAlgorithmTests
{
    /// <summary>Splits every parent into a 40% market child and a 60% limit child.</summary>
    private sealed class SplitAlgorithm : ExecAlgorithm
    {
        public SplitAlgorithm(ExecAlgorithmConfig? config = null)
            : base(config)
        {
        }

        public List<Order> Children { get; } = new();

        public List<OrderEvent> Events { get; } = new();

        public bool Throw { get; set; }

        protected override void OnOrder(Order order)
        {
            if (Throw)
            {
                throw new InvalidOperationException("algorithm failed");
            }

            MarketOrder first = SpawnMarket(order, new Quantity(order.Quantity.Value * 0.4m, order.Quantity.Precision), TimeInForce.Ioc);
            LimitOrder second = SpawnLimit(order, new Quantity(order.Quantity.Value * 0.6m, order.Quantity.Precision), Price.Parse("49900.00"), postOnly: true, tags: ["PASSIVE"]);
            Children.Add(first);
            Children.Add(second);
            SubmitOrder(first);
            SubmitOrder(second);
        }

        protected override void OnOrderEvent(OrderEvent e) => Events.Add(e);

        public void Amend(Order child, Price price) => ModifyOrder(child, price: price);

        public void Pull(Order child) => CancelOrder(child);
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Harness = new KernelHarness();
            Algorithm = new SplitAlgorithm();
            Harness.Kernel.Trader.AddExecAlgorithm(Algorithm);
            Strategy = Harness.StartWithStrategy();
        }

        public KernelHarness Harness { get; }

        public ProbeStrategy Strategy { get; }

        public SplitAlgorithm Algorithm { get; }

        public void Dispose() => Harness.Dispose();
    }

    private static MarketOrder SubmitParent(ProbeStrategy strategy, string quantity = "1.000")
    {
        MarketOrder parent = strategy.Factory.Market(TestIds.BtcUsdt, OrderSide.Buy, Quantity.Parse(quantity), execAlgorithmId: new ExecAlgorithmId("SplitAlgorithm"), tags: ["ALGO"]);
        strategy.DoSubmit(parent);
        return parent;
    }

    [Fact]
    public void Algorithm_id_defaults_to_the_type_name_and_can_be_configured()
    {
        SplitAlgorithm configured = new(new ExecAlgorithmConfig { ExecAlgorithmId = new ExecAlgorithmId("TWAP") });

        Assert.Equal("SplitAlgorithm", new SplitAlgorithm().ExecAlgorithmId.Value);
        Assert.Equal("TWAP", configured.ExecAlgorithmId.Value);
        Assert.Equal("TWAP", configured.ActorId.Value);
    }

    [Fact]
    public void Parent_order_goes_to_the_algorithm_and_only_its_children_reach_the_venue()
    {
        using Fixture f = new();
        (KernelHarness h, ProbeStrategy strategy, SplitAlgorithm algorithm) = (f.Harness, f.Strategy, f.Algorithm);

        MarketOrder parent = SubmitParent(strategy);

        Assert.Equal(algorithm.Children.Select(c => c.ClientOrderId), h.Client.Received<SubmitOrder>().Select(c => c.Order.ClientOrderId));
        Assert.DoesNotContain(h.Client.Received<SubmitOrder>(), c => c.Order.ClientOrderId == parent.ClientOrderId);
    }

    [Fact]
    public void Children_are_numbered_after_their_parent_and_inherit_its_identity()
    {
        using Fixture f = new();
        (KernelHarness h, ProbeStrategy strategy, SplitAlgorithm algorithm) = (f.Harness, f.Strategy, f.Algorithm);

        MarketOrder parent = SubmitParent(strategy);

        Order market = algorithm.Children[0];
        Order limit = algorithm.Children[1];
        Assert.Equal($"{parent.ClientOrderId}-E1", market.ClientOrderId.Value);
        Assert.Equal($"{parent.ClientOrderId}-E2", limit.ClientOrderId.Value);
        Assert.All(algorithm.Children, c =>
        {
            Assert.Equal(parent.ClientOrderId, c.ExecSpawnId);
            Assert.Equal(new ExecAlgorithmId("SplitAlgorithm"), c.ExecAlgorithmId);
            Assert.Equal((parent.StrategyId, parent.InstrumentId, parent.Side), (c.StrategyId, c.InstrumentId, c.Side));
        });

        // 40% and 60% of 1.000
        Assert.Equal((Quantity.Parse("0.400"), TimeInForce.Ioc), (market.Quantity, market.TimeInForce));
        Assert.Equal(["ALGO"], market.Tags); // inherited from the parent
        Assert.Equal((Quantity.Parse("0.600"), Price.Parse("49900.00")), (limit.Quantity, limit.Price!.Value));
        Assert.True(limit.IsPostOnly);
        Assert.Equal(["PASSIVE"], limit.Tags);
        Assert.Equal(2, h.Cache.OrdersForExecSpawn(parent.ClientOrderId).Count);
    }

    [Fact]
    public void Spawn_numbering_is_per_parent()
    {
        using Fixture f = new();
        (KernelHarness h, ProbeStrategy strategy, SplitAlgorithm algorithm) = (f.Harness, f.Strategy, f.Algorithm);

        MarketOrder first = SubmitParent(strategy);
        MarketOrder second = SubmitParent(strategy);

        Assert.Equal(
            [$"{first.ClientOrderId}-E1", $"{first.ClientOrderId}-E2", $"{second.ClientOrderId}-E1", $"{second.ClientOrderId}-E2"],
            algorithm.Children.Select(c => c.ClientOrderId.Value));
    }

    [Fact]
    public void Events_of_child_orders_come_back_to_the_algorithm_and_other_orders_do_not()
    {
        using Fixture f = new();
        (KernelHarness h, ProbeStrategy strategy, SplitAlgorithm algorithm) = (f.Harness, f.Strategy, f.Algorithm);
        SubmitParent(strategy);
        LimitOrder unrelated = strategy.Factory.Limit(TestIds.BtcUsdt, OrderSide.Buy, Quantity.Parse("1.000"), Price.Parse("40000.00"));
        strategy.DoSubmit(unrelated);

        h.Accept(algorithm.Children[1]);
        h.Accept(unrelated);

        Assert.Equal(["OrderSubmitted", "OrderAccepted"], algorithm.Events.Select(e => e.GetType().Name));
        Assert.All(algorithm.Events, e => Assert.Equal(algorithm.Children[1].ClientOrderId, e.ClientOrderId));
    }

    [Fact]
    public void Algorithm_can_modify_and_cancel_its_children_through_the_risk_engine()
    {
        using Fixture f = new();
        (KernelHarness h, ProbeStrategy strategy, SplitAlgorithm algorithm) = (f.Harness, f.Strategy, f.Algorithm);
        SubmitParent(strategy);
        Order child = algorithm.Children[1];
        h.Accept(child);

        algorithm.Amend(child, Price.Parse("49950.00"));
        algorithm.Pull(child);

        Assert.Equal(Price.Parse("49950.00"), Assert.Single(h.Client.Received<ModifyOrder>()).Price);
        Assert.Equal(child.ClientOrderId, Assert.Single(h.Client.Received<CancelOrder>()).ClientOrderId);
    }

    [Fact]
    public void Failing_algorithm_is_faulted_without_affecting_the_strategy_or_the_kernel()
    {
        using Fixture f = new();
        (KernelHarness h, ProbeStrategy strategy, SplitAlgorithm algorithm) = (f.Harness, f.Strategy, f.Algorithm);
        algorithm.Throw = true;

        SubmitParent(strategy);

        Assert.Equal(ComponentState.Faulted, algorithm.State);
        Assert.Equal(ComponentState.Running, strategy.State);
        Assert.True(h.Kernel.IsRunning);
        Assert.Empty(h.Client.Commands);
    }

    [Fact]
    public void Order_list_with_the_algorithm_id_hands_every_order_to_the_algorithm()
    {
        using Fixture f = new();
        (KernelHarness h, SplitAlgorithm algorithm) = (f.Harness, f.Algorithm);
        MarketOrder a = MarketOrder.Create(TestOrders.Params("O-A", TestIds.BtcUsdt, OrderSide.Buy, "1.000"));
        MarketOrder b = MarketOrder.Create(TestOrders.Params("O-B", TestIds.BtcUsdt, OrderSide.Buy, "2.000"));
        h.Cache.AddOrder(a);
        h.Cache.AddOrder(b);
        OrderList list = new(new OrderListId("OL-1"), [a, b]);

        algorithm.HandleCommand(new SubmitOrderList(TestIds.Trader, TestIds.Strategy, list, null, algorithm.ExecAlgorithmId, null, Guid.NewGuid(), TestOrders.T0));

        Assert.Equal(["O-A-E1", "O-A-E2", "O-B-E1", "O-B-E2"], algorithm.Children.Select(c => c.ClientOrderId.Value));
    }
}
