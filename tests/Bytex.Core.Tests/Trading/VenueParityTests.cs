using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Tests.Support;

namespace Bytex.Core.Tests.Trading;

// Why: for two releases the simulated venue held one-triggers-other children until the entry filled and the adapter
// base sent every leg at once, so a bracket behaved one way in paper and another way live - the divergence this
// engine's paper mode exists to prevent. It was missed because every contingency test ran against the simulator or a
// fake, and none of them asked whether the path a real venue takes does the same thing.
//
// These tests compare the two. They are about the SEQUENCE of what reaches a venue, which is the part a user cannot
// see until money is on it, and they are deliberately written so that a rule added to one side and not the other
// fails here rather than in somebody's account.
public sealed class VenueParityTests
{
    private static readonly Quantity _one = Quantity.Parse("1.000");

    [Fact]
    public void A_bracket_reaches_a_live_venue_as_the_entry_alone()
    {
        using KernelHarness h = new();
        ProbeStrategy strategy = h.StartWithStrategy();
        OrderList bracket = strategy.Factory.BracketOrder(TestIds.BtcUsdt, OrderSide.Buy, _one, Price.Parse("45000.00"), Price.Parse("55000.00"));

        strategy.DoSubmitList(bracket);

        // The exits protect a position that does not exist yet. A venue holding them would be holding naked orders.
        Assert.Equal(
            [bracket.First.ClientOrderId],
            h.Client.Received<SubmitOrder>().Select(c => c.Order.ClientOrderId));
    }

    [Fact]
    public void The_children_reach_it_when_the_entry_fills_and_in_the_order_they_were_listed()
    {
        using KernelHarness h = new();
        ProbeStrategy strategy = h.StartWithStrategy();
        OrderList bracket = strategy.Factory.BracketOrder(TestIds.BtcUsdt, OrderSide.Buy, _one, Price.Parse("45000.00"), Price.Parse("55000.00"));
        strategy.DoSubmitList(bracket);
        h.Accept(bracket.First);
        h.Client.Commands.Clear();

        h.Client.EmitFilled(bracket.First, "T-1", "1.000", "50000.00");

        Assert.Equal(
            [bracket.Orders[1].ClientOrderId, bracket.Orders[2].ClientOrderId],
            h.Client.Received<SubmitOrder>().Select(c => c.Order.ClientOrderId));
    }

    [Theory]
    [InlineData(ContingencyType.Oco)]
    [InlineData(ContingencyType.Ouo)]
    public void A_contingency_that_is_not_one_triggers_other_goes_to_the_venue_whole(ContingencyType contingency)
    {
        // The other contingencies are sets of orders that belong at the venue together: one cancels the other, one
        // updates the other. Holding those back would be a different bug in the same place.
        using KernelHarness h = new();
        ProbeStrategy strategy = h.StartWithStrategy();
        ClientOrderId idA = new("O-A");
        ClientOrderId idB = new("O-B");
        LimitOrder a = LimitOrder.Create(TestOrders.Params("O-A", TestIds.BtcUsdt, OrderSide.Sell, "1.000", contingency: contingency, linked: [idB]), Price.Parse("55000.00"));
        LimitOrder b = LimitOrder.Create(TestOrders.Params("O-B", TestIds.BtcUsdt, OrderSide.Sell, "1.000", contingency: contingency, linked: [idA]), Price.Parse("45000.00"));
        OrderList list = new(strategy.Factory.GenerateOrderListId(), [a, b]);

        strategy.DoSubmitList(list);

        Assert.Equal(
            [a.ClientOrderId, b.ClientOrderId],
            h.Client.Received<SubmitOrder>().Select(c => c.Order.ClientOrderId));
    }

    [Fact]
    public void Nothing_is_left_waiting_on_an_entry_that_never_fills()
    {
        using KernelHarness h = new();
        ProbeStrategy strategy = h.StartWithStrategy();
        OrderList bracket = strategy.Factory.BracketOrder(TestIds.BtcUsdt, OrderSide.Buy, _one, Price.Parse("45000.00"), Price.Parse("55000.00"));
        strategy.DoSubmitList(bracket);
        h.Accept(bracket.First);
        h.Client.Commands.Clear();

        h.Client.EmitCanceled(bracket.First);

        Assert.Empty(h.Client.Received<SubmitOrder>());
    }

    [Fact]
    public void An_entry_that_is_rejected_leaves_nothing_waiting_either()
    {
        using KernelHarness h = new();
        ProbeStrategy strategy = h.StartWithStrategy();
        OrderList bracket = strategy.Factory.BracketOrder(TestIds.BtcUsdt, OrderSide.Buy, _one, Price.Parse("45000.00"), Price.Parse("55000.00"));
        strategy.DoSubmitList(bracket);
        h.Client.Commands.Clear();

        h.Client.EmitRejected(bracket.First, "no");

        Assert.Empty(h.Client.Received<SubmitOrder>());
    }
}
