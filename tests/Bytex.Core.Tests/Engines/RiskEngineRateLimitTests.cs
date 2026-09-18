using Bytex.Core.Engines;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Tests.Support;

namespace Bytex.Core.Tests.Engines;

// Why: R4.2 - no more than MaxOrderSubmitRate submissions (MaxOrderModifyRate modifications) may pass within
// one sliding OrderRateInterval. Time comes only from the test clock, so every boundary is exact.
public class RiskEngineRateLimitTests
{
    private static RiskHarness Harness(int submitRate = 3, int modifyRate = 2, UnixNanos? now = null)
    {
        RiskHarness h = new(new RiskEngineConfig { MaxOrderSubmitRate = submitRate, MaxOrderModifyRate = modifyRate, OrderRateInterval = TimeSpan.FromSeconds(1) }, now);
        h.Cache.AddInstrument(TestInstruments.BtcUsdt());
        return h;
    }

    private static LimitOrder NewOrder(int n) => TestOrders.Limit($"O-{n}", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00");

    private static void SubmitMany(RiskHarness h, int from, int count)
    {
        for (int i = 0; i < count; i++)
        {
            h.Submit(NewOrder(from + i));
        }
    }

    [Fact]
    public void Submissions_up_to_the_limit_pass_and_the_next_one_is_denied()
    {
        RiskHarness h = Harness(submitRate: 3);

        SubmitMany(h, 1, 4);

        Assert.Equal(3, h.Forwarded.Count);
        OrderDenied denied = Assert.Single(h.Denied);
        Assert.Equal("O-4", denied.ClientOrderId.Value);
        Assert.Contains("max order submit rate of 3", denied.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Limit_still_applies_one_nanosecond_before_the_window_ends()
    {
        RiskHarness h = Harness(submitRate: 3);
        SubmitMany(h, 1, 3);

        h.Clock.SetTime(TestOrders.T0.AddNanos(UnixNanos.NanosPerSecond - 1));
        h.Submit(NewOrder(4));

        Assert.Equal(3, h.Forwarded.Count);
        Assert.Single(h.Denied);
    }

    [Fact]
    public void Submission_exactly_one_interval_after_a_full_burst_is_still_denied()
    {
        // The docs do not define the boundary. This pins the implemented convention: the window is closed,
        // so a submission that is exactly one interval old still counts against the limit.
        RiskHarness h = Harness(submitRate: 3);
        SubmitMany(h, 1, 3);

        h.Clock.SetTime(TestOrders.T0.AddNanos(UnixNanos.NanosPerSecond));
        h.Submit(NewOrder(4));

        Assert.Equal(3, h.Forwarded.Count);
        Assert.Single(h.Denied);
    }

    [Fact]
    public void Window_frees_up_one_nanosecond_after_the_interval_has_passed()
    {
        RiskHarness h = Harness(submitRate: 3);
        SubmitMany(h, 1, 3);

        h.Clock.SetTime(TestOrders.T0.AddNanos(UnixNanos.NanosPerSecond + 1));
        SubmitMany(h, 4, 3);

        Assert.Equal(6, h.Forwarded.Count);
        Assert.Empty(h.Denied);
    }

    [Fact]
    public void Window_slides_with_each_submission_rather_than_resetting_in_blocks()
    {
        // Submissions at 0.0s, 0.4s, 0.8s fill the window. At 1.2s only the 0.0s entry has expired:
        // one slot is free, and the next attempt at 1.2s sees {0.4, 0.8, 1.2} and is denied.
        RiskHarness h = Harness(submitRate: 3);
        h.Submit(NewOrder(1));
        h.Clock.SetTime(TestOrders.T0 + TimeSpan.FromMilliseconds(400));
        h.Submit(NewOrder(2));
        h.Clock.SetTime(TestOrders.T0 + TimeSpan.FromMilliseconds(800));
        h.Submit(NewOrder(3));

        h.Clock.SetTime(TestOrders.T0 + TimeSpan.FromMilliseconds(1200));
        h.Submit(NewOrder(4));
        h.Submit(NewOrder(5));

        Assert.Equal(["O-1", "O-2", "O-3", "O-4"], h.Forwarded.OfType<SubmitOrder>().Select(c => c.Order.ClientOrderId.Value));
        Assert.Equal("O-5", Assert.Single(h.Denied).ClientOrderId.Value);
    }

    [Fact]
    public void Rate_denied_submissions_do_not_extend_the_window()
    {
        // Three pass at t0, then ten are denied at t0+0.5s. If denials were recorded, the window would still
        // be full at t0+1s+1ns; it must be empty because only the three t0 submissions ever counted.
        RiskHarness h = Harness(submitRate: 3);
        SubmitMany(h, 1, 3);
        h.Clock.SetTime(TestOrders.T0 + TimeSpan.FromMilliseconds(500));
        SubmitMany(h, 4, 10);

        h.Clock.SetTime(TestOrders.T0.AddNanos(UnixNanos.NanosPerSecond + 1));
        SubmitMany(h, 14, 3);

        Assert.Equal(6, h.Forwarded.Count);
        Assert.Equal(10, h.Denied.Count);
    }

    [Fact]
    public void Orders_denied_by_another_check_do_not_consume_rate()
    {
        RiskHarness h = Harness(submitRate: 1);
        h.Submit(TestOrders.Limit("O-BAD", TestIds.BtcUsdt, OrderSide.Buy, "1.0001", "50000.00"));

        SubmitOrder good = h.Submit(NewOrder(1));

        Assert.Same(good, Assert.Single(h.Forwarded));
        Assert.StartsWith("QUANTITY_PRECISION", Assert.Single(h.Denied).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Order_list_counts_as_one_submission_and_is_denied_as_a_whole_when_over_the_limit()
    {
        RiskHarness h = Harness(submitRate: 1);
        h.SubmitList(NewOrder(1), NewOrder(2), NewOrder(3));

        h.SubmitList(NewOrder(4), NewOrder(5));

        Assert.IsType<SubmitOrderList>(Assert.Single(h.Forwarded));
        Assert.Equal(["O-4", "O-5"], h.Denied.Select(d => d.ClientOrderId.Value));
        Assert.All(h.Denied, d => Assert.Contains("max order submit rate of 1", d.Reason, StringComparison.Ordinal));
    }

    [Fact]
    public void Modify_rate_is_counted_separately_from_submit_rate()
    {
        RiskHarness h = Harness(submitRate: 1, modifyRate: 2);
        LimitOrder resting = h.AddAccepted(NewOrder(100));
        h.Submit(NewOrder(1)); // uses the whole submit allowance

        h.Modify(resting, price: "49999.00");
        h.Modify(resting, price: "49998.00");
        h.Modify(resting, price: "49997.00");

        Assert.Equal(2, h.Forwarded.OfType<ModifyOrder>().Count());
        OrderModifyRejected rejected = Assert.Single(h.ModifyRejected);
        Assert.Contains("max order modify rate of 2", rejected.Reason, StringComparison.Ordinal);
        Assert.Empty(h.Denied);
    }

    [Fact]
    public void Modify_window_frees_up_after_the_interval()
    {
        RiskHarness h = Harness(modifyRate: 1);
        LimitOrder resting = h.AddAccepted(NewOrder(100));
        h.Modify(resting, price: "49999.00");

        h.Clock.SetTime(TestOrders.T0.AddNanos(UnixNanos.NanosPerSecond + 1));
        h.Modify(resting, price: "49998.00");

        Assert.Equal(2, h.Forwarded.Count);
        Assert.Empty(h.ModifyRejected);
    }

    [Fact]
    public void Reset_clears_the_rate_window()
    {
        RiskHarness h = Harness(submitRate: 1);
        h.Submit(NewOrder(1));

        h.Engine.Reset();
        h.Submit(NewOrder(2));

        Assert.Equal(2, h.Forwarded.Count);
        Assert.Empty(h.Denied);
    }

    [Fact]
    public void Rate_limit_works_when_the_clock_is_younger_than_one_interval()
    {
        // At the epoch the window start (now - interval) is negative; nothing may overflow or throw.
        RiskHarness h = Harness(submitRate: 2, now: UnixNanos.Zero);

        SubmitMany(h, 1, 3);

        Assert.Equal(2, h.Forwarded.Count);
        Assert.Single(h.Denied);
    }

    [Fact]
    public void Default_configuration_allows_one_hundred_submissions_per_second()
    {
        RiskHarness h = new();
        h.Cache.AddInstrument(TestInstruments.BtcUsdt());

        SubmitMany(h, 1, 101);

        Assert.Equal(100, h.Forwarded.Count);
        Assert.Equal("O-101", Assert.Single(h.Denied).ClientOrderId.Value);
    }
}
