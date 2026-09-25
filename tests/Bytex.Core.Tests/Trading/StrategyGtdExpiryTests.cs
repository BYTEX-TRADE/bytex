using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Tests.Support;
using Bytex.Core.Trading;

namespace Bytex.Core.Tests.Trading;

// Why: R3.9 - with ManageGtdExpiry the engine, not the venue, expires GTD orders: a clock alert at the expire
// time cancels the order, and the alert must disappear as soon as the order is done for any other reason.
public class StrategyGtdExpiryTests
{
    private static UnixNanos Expiry => TestOrders.T0 + TimeSpan.FromMinutes(5);

    private static StrategyConfig Config(bool manage = true) => new() { StrategyId = TestIds.Strategy, ManageGtdExpiry = manage };

    private static LimitOrder SubmitGtd(KernelHarness h, ProbeStrategy strategy)
    {
        LimitOrder order = strategy.Factory.Limit(TestIds.BtcUsdt, OrderSide.Buy, Quantity.Parse("1.000"), Price.Parse("50000.00"), TimeInForce.Gtd, Expiry);
        strategy.DoSubmit(order);
        h.Accept(order);
        h.Client.Commands.Clear();
        return order;
    }

    [Fact]
    public void Gtd_order_is_cancelled_by_a_clock_alert_at_its_expire_time()
    {
        using KernelHarness h = new();
        ProbeStrategy strategy = h.StartWithStrategy(Config());
        LimitOrder order = SubmitGtd(h, strategy);

        h.Clock.AdvanceAndRun(Expiry.AddNanos(-1));
        int before = h.Client.Commands.Count;
        h.Clock.AdvanceAndRun(Expiry);

        Assert.Equal(0, before);
        Assert.Equal(order.ClientOrderId, Assert.IsType<CancelOrder>(Assert.Single(h.Client.Commands)).ClientOrderId);
        Assert.Equal(0, h.Clock.TimerCount);
    }

    [Fact]
    public void Alert_is_registered_at_the_expire_time_under_a_name_scoped_to_the_strategy_and_order()
    {
        using KernelHarness h = new();
        ProbeStrategy strategy = h.StartWithStrategy(Config());

        LimitOrder order = SubmitGtd(h, strategy);

        Assert.Equal(Expiry, h.Clock.NextTime($"S-001:gtd:{order.ClientOrderId}"));
    }

    [Theory]
    [InlineData("filled")]
    [InlineData("canceled")]
    [InlineData("expired")]
    public void Alert_is_removed_when_the_order_closes_before_its_expiry(string ending)
    {
        using KernelHarness h = new();
        ProbeStrategy strategy = h.StartWithStrategy(Config());
        LimitOrder order = SubmitGtd(h, strategy);

        switch (ending)
        {
            case "filled":
                h.Client.EmitFilled(order, "T-1", "1.000", "50000.00");
                break;
            case "canceled":
                h.Client.EmitCanceled(order);
                break;
            default:
                h.Client.EmitExpired(order);
                break;
        }

        h.Clock.AdvanceAndRun(Expiry);

        Assert.Equal(0, h.Clock.TimerCount);
        Assert.Empty(h.Client.Received<CancelOrder>());
    }

    [Fact]
    public void Alert_is_removed_when_the_venue_rejects_the_order()
    {
        using KernelHarness h = new();
        ProbeStrategy strategy = h.StartWithStrategy(Config());
        LimitOrder order = strategy.Factory.Limit(TestIds.BtcUsdt, OrderSide.Buy, Quantity.Parse("1.000"), Price.Parse("50000.00"), TimeInForce.Gtd, Expiry);
        strategy.DoSubmit(order);
        h.Client.EmitSubmitted(order);

        h.Client.EmitRejected(order, "no");

        Assert.Equal(0, h.Clock.TimerCount);
    }

    [Fact]
    public void Partial_fill_keeps_the_alert_because_the_rest_is_still_working()
    {
        using KernelHarness h = new();
        ProbeStrategy strategy = h.StartWithStrategy(Config());
        LimitOrder order = SubmitGtd(h, strategy);

        h.Client.EmitFilled(order, "T-1", "0.400", "50000.00");
        h.Clock.AdvanceAndRun(Expiry);

        Assert.Equal(order.ClientOrderId, Assert.Single(h.Client.Received<CancelOrder>()).ClientOrderId);
    }

    [Fact]
    public void Strategy_can_drop_the_managed_expiry_of_an_order()
    {
        using KernelHarness h = new();
        ProbeStrategy strategy = h.StartWithStrategy(Config());
        LimitOrder order = SubmitGtd(h, strategy);

        strategy.DoCancelGtdExpiry(order);
        h.Clock.AdvanceAndRun(Expiry);

        Assert.Empty(h.Client.Commands);
    }

    [Fact]
    public void No_alert_is_set_when_expiry_management_is_off_or_the_order_is_not_gtd()
    {
        using KernelHarness unmanaged = new();
        SubmitGtd(unmanaged, unmanaged.StartWithStrategy(Config(manage: false)));
        using KernelHarness managed = new();
        ProbeStrategy strategy = managed.StartWithStrategy(Config());
        strategy.DoSubmit(strategy.Factory.Limit(TestIds.BtcUsdt, OrderSide.Buy, Quantity.Parse("1.000"), Price.Parse("50000.00")));

        Assert.Equal(0, unmanaged.Clock.TimerCount);
        Assert.Equal(0, managed.Clock.TimerCount);
    }
}
