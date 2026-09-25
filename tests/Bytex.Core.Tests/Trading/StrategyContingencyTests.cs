using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Tests.Support;
using Bytex.Core.Trading;

namespace Bytex.Core.Tests.Trading;

// Why: R3.7/R3.8 - for venues without native contingent orders the engine side must do it
// (StrategyConfig.ManageContingentOrders): OCO cancels the sibling, OUO resizes or cancels it, and an OTO
// parent that dies takes its children with it.
public class StrategyContingencyTests
{
    private static StrategyConfig Managing(bool manage = true) => new() { StrategyId = TestIds.Strategy, ManageContingentOrders = manage };

    private static (LimitOrder A, LimitOrder B) LinkedPair(ContingencyType contingency)
    {
        ClientOrderId a = new("O-A");
        ClientOrderId b = new("O-B");
        return (
            LimitOrder.Create(TestOrders.Params("O-A", TestIds.BtcUsdt, OrderSide.Sell, "1.000", contingency: contingency, linked: [b]), Price.Parse("55000.00")),
            LimitOrder.Create(TestOrders.Params("O-B", TestIds.BtcUsdt, OrderSide.Sell, "1.000", contingency: contingency, linked: [a]), Price.Parse("45000.00")));
    }

    private static (ProbeStrategy Strategy, LimitOrder A, LimitOrder B) WorkingPair(KernelHarness h, ContingencyType contingency, bool manage = true)
    {
        ProbeStrategy strategy = h.StartWithStrategy(Managing(manage));
        (LimitOrder a, LimitOrder b) = LinkedPair(contingency);
        strategy.DoSubmit(a);
        strategy.DoSubmit(b);
        h.Accept(a);
        h.Accept(b);
        h.Client.Commands.Clear();
        return (strategy, a, b);
    }

    [Fact]
    public void Oco_fill_cancels_the_sibling()
    {
        using KernelHarness h = new();
        (_, LimitOrder a, LimitOrder b) = WorkingPair(h, ContingencyType.Oco);

        h.Client.EmitFilled(a, "T-1", "1.000", "55000.00");

        CancelOrder cancel = Assert.IsType<CancelOrder>(Assert.Single(h.Client.Commands));
        Assert.Equal(b.ClientOrderId, cancel.ClientOrderId);
    }

    [Fact]
    public void Oco_partial_fill_already_cancels_the_sibling()
    {
        using KernelHarness h = new();
        (_, LimitOrder a, LimitOrder b) = WorkingPair(h, ContingencyType.Oco);

        h.Client.EmitFilled(a, "T-1", "0.250", "55000.00");

        Assert.Equal(b.ClientOrderId, Assert.Single(h.Client.Received<CancelOrder>()).ClientOrderId);
    }

    [Fact]
    public void Oco_cancel_of_one_order_cancels_the_other()
    {
        using KernelHarness h = new();
        (_, LimitOrder a, LimitOrder b) = WorkingPair(h, ContingencyType.Oco);

        h.Client.EmitCanceled(b);

        Assert.Equal(a.ClientOrderId, Assert.Single(h.Client.Received<CancelOrder>()).ClientOrderId);
    }

    [Fact]
    public void Oco_does_not_cancel_a_sibling_that_is_already_closed()
    {
        using KernelHarness h = new();
        (_, LimitOrder a, LimitOrder b) = WorkingPair(h, ContingencyType.Oco);
        h.Client.EmitCanceled(b);
        h.Client.Commands.Clear();

        h.Client.EmitCanceled(a);

        Assert.Empty(h.Client.Commands);
    }

    [Fact]
    public void Ouo_partial_fill_resizes_the_sibling_to_the_remaining_quantity()
    {
        using KernelHarness h = new();
        (_, LimitOrder a, LimitOrder b) = WorkingPair(h, ContingencyType.Ouo);

        h.Client.EmitFilled(a, "T-1", "0.400", "55000.00");

        // 1.000 - 0.400 filled = 0.600 still to exit, so the other exit must shrink to 0.600.
        ModifyOrder modify = Assert.IsType<ModifyOrder>(Assert.Single(h.Client.Commands));
        Assert.Equal(b.ClientOrderId, modify.ClientOrderId);
        Assert.Equal(Quantity.Parse("0.600"), modify.Quantity);
        Assert.Null(modify.Price);
    }

    [Fact]
    public void Ouo_full_fill_cancels_the_sibling()
    {
        using KernelHarness h = new();
        (_, LimitOrder a, LimitOrder b) = WorkingPair(h, ContingencyType.Ouo);

        h.Client.EmitFilled(a, "T-1", "1.000", "55000.00");

        Assert.Equal(b.ClientOrderId, Assert.IsType<CancelOrder>(Assert.Single(h.Client.Commands)).ClientOrderId);
    }

    [Fact]
    public void Ouo_cancel_of_one_order_cancels_the_other()
    {
        using KernelHarness h = new();
        (_, LimitOrder a, LimitOrder b) = WorkingPair(h, ContingencyType.Ouo);

        h.Client.EmitExpired(a);

        Assert.Equal(b.ClientOrderId, Assert.Single(h.Client.Received<CancelOrder>()).ClientOrderId);
    }

    [Fact]
    public void Oto_parent_that_is_cancelled_takes_its_children_with_it()
    {
        using KernelHarness h = new();
        ProbeStrategy strategy = h.StartWithStrategy(Managing());
        OrderList bracket = strategy.Factory.BracketOrder(TestIds.BtcUsdt, OrderSide.Buy, Quantity.Parse("1.000"), Price.Parse("45000.00"), Price.Parse("55000.00"),
            entryPrice: Price.Parse("50000.00"), entryType: OrderType.Limit);
        strategy.DoSubmitList(bracket);
        foreach (Order order in bracket.Orders)
        {
            h.Accept(order);
        }

        h.Client.Commands.Clear();

        h.Client.EmitCanceled(bracket.First);

        Assert.Equal(
            [bracket.Orders[1].ClientOrderId, bracket.Orders[2].ClientOrderId],
            h.Client.Received<CancelOrder>().Select(c => c.ClientOrderId));
    }

    [Fact]
    public void Oto_children_go_to_the_venue_when_the_parent_fills_and_not_before()
    {
        // The rule a real venue needs: the exits are held until there is a position to exit. Sending them with the
        // entry leaves a stop and a target working against nothing, which at a venue is either a rejection or a
        // position opened the wrong way round.
        using KernelHarness h = new();
        ProbeStrategy strategy = h.StartWithStrategy(Managing());
        OrderList bracket = strategy.Factory.BracketOrder(TestIds.BtcUsdt, OrderSide.Buy, Quantity.Parse("1.000"), Price.Parse("45000.00"), Price.Parse("55000.00"));
        strategy.DoSubmitList(bracket);

        // Only the entry was sent, so only the entry can be accepted.
        Assert.Equal([bracket.First.ClientOrderId], h.Client.Received<SubmitOrder>().Select(c => c.Order.ClientOrderId));
        h.Accept(bracket.First);
        h.Client.Commands.Clear();

        h.Client.EmitFilled(bracket.First, "T-1", "1.000", "50000.00");

        // The fill releases them, and they are submitted as ordinary orders - so they are judged by the same rules
        // as anything else the strategy sends.
        Assert.Equal(
            [bracket.Orders[1].ClientOrderId, bracket.Orders[2].ClientOrderId],
            h.Client.Received<SubmitOrder>().Select(c => c.Order.ClientOrderId));
    }

    [Fact]
    public void Oto_children_are_dropped_when_the_parent_will_never_fill()
    {
        // Nothing should be left waiting on an order that is gone: a cancelled entry means the exits it would have
        // triggered are forgotten, not sent later against a position that never opened.
        using KernelHarness h = new();
        ProbeStrategy strategy = h.StartWithStrategy(Managing());
        OrderList bracket = strategy.Factory.BracketOrder(TestIds.BtcUsdt, OrderSide.Buy, Quantity.Parse("1.000"), Price.Parse("45000.00"), Price.Parse("55000.00"));
        strategy.DoSubmitList(bracket);
        h.Accept(bracket.First);
        h.Client.Commands.Clear();

        h.Client.EmitCanceled(bracket.First);

        Assert.Empty(h.Client.Received<SubmitOrder>());
    }

    [Fact]
    public void Nothing_is_managed_unless_the_strategy_opts_in()
    {
        using KernelHarness h = new();
        (_, LimitOrder a, _) = WorkingPair(h, ContingencyType.Oco, manage: false);

        h.Client.EmitFilled(a, "T-1", "1.000", "55000.00");

        Assert.Empty(h.Client.Commands);
    }
}
