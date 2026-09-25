using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Orders;

namespace Bytex.Backtest.Tests;

// Why: without latency a backtest lets the strategy act on a tick at the very instant it sees it. With a latency
// model a command only reaches the venue base + (insert | update | cancel) later, and until then the market moves on.
public sealed class LatencyModelTests
{
    private static readonly LatencyModel Slow = new(
        Base: TimeSpan.FromMilliseconds(100),
        Insert: TimeSpan.FromMilliseconds(50),
        Update: TimeSpan.FromMilliseconds(20),
        Cancel: TimeSpan.FromMilliseconds(10));

    [Fact]
    public void Each_command_latency_is_the_base_latency_plus_its_own_component()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(150), Slow.InsertLatency);
        Assert.Equal(TimeSpan.FromMilliseconds(120), Slow.UpdateLatency);
        Assert.Equal(TimeSpan.FromMilliseconds(110), Slow.CancelLatency);
        Assert.False(Slow.IsZero);
    }

    [Fact]
    public void Uniform_model_applies_one_delay_to_every_command_and_zero_model_none()
    {
        LatencyModel uniform = LatencyModel.Uniform(TimeSpan.FromMilliseconds(40));

        Assert.Equal(TimeSpan.FromMilliseconds(40), uniform.InsertLatency);
        Assert.Equal(TimeSpan.FromMilliseconds(40), uniform.UpdateLatency);
        Assert.Equal(TimeSpan.FromMilliseconds(40), uniform.CancelLatency);
        Assert.True(LatencyModel.Zero.IsZero);
        Assert.False(uniform.IsZero);
    }

    [Fact]
    public void Market_order_trades_at_the_price_prevailing_when_it_arrives_not_when_it_was_sent()
    {
        // Sent at 1000 with 150 ms insert latency, so it arrives at 1150: after the 1100 quote, before the 1200 quote.
        using SimHarness sim = SimHarness.Spot(new SimOptions { LatencyModel = Slow });
        MarketOrder? order = null;
        sim.Quote(900, 100.00m, 100.10m)
            .At(1000, s => order = s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(1100, 101.00m, 101.10m)
            .Quote(1200, 102.00m, 102.10m)
            .Run();

        OrderFilled fill = sim.SingleFill(order!);
        Assert.Equal(sim.Px(101.10m), fill.LastPx);
        Assert.True(fill.TsEvent >= Scripted.Ms(1150), $"filled at {fill.TsEvent}, before the order could have arrived");
    }

    [Fact]
    public void Delayed_order_is_stamped_with_its_arrival_time()
    {
        using SimHarness sim = SimHarness.Spot(new SimOptions { LatencyModel = Slow });
        MarketOrder? order = null;
        sim.Quote(900, 100.00m, 100.10m)
            .At(1000, s => order = s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(1100, 101.00m, 101.10m)
            .Quote(1200, 102.00m, 102.10m)
            .Run();

        Assert.Equal(Scripted.Ms(1150), order!.TsSubmitted);
        Assert.Equal(Scripted.Ms(1150), sim.SingleFill(order).TsEvent);
    }

    [Fact]
    public void Order_is_still_unacknowledged_while_it_is_in_flight()
    {
        using SimHarness sim = SimHarness.Spot(new SimOptions { LatencyModel = Slow });
        LimitOrder? order = null;
        sim.Quote(900, 100.00m, 100.10m)
            .At(1000, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(99.00m))))
            .Quote(1100, 100.00m, 100.10m)
            .At(1120, s => s.Note("status " + order!.Status))
            .Quote(1200, 100.00m, 100.10m)
            .At(1250, s => s.Note("status " + order!.Status))
            .Quote(1300, 100.00m, 100.10m)
            .Run();

        Assert.Contains("1120|status Initialized", sim.Strategy.Journal);
        Assert.Contains("1250|status Accepted", sim.Strategy.Journal);
    }

    [Fact]
    public void Resting_order_cannot_be_filled_by_a_quote_that_precedes_its_arrival()
    {
        // The 1100 quote crosses the limit, but the order only reaches the venue at 1150.
        using SimHarness sim = SimHarness.Spot(new SimOptions { LatencyModel = Slow });
        LimitOrder? order = null;
        sim.Quote(900, 100.00m, 100.10m)
            .At(1000, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(99.50m))))
            .Quote(1100, 99.30m, 99.40m)
            .Quote(1140, 100.00m, 100.10m) // market is back above the limit when the order lands
            .Quote(1200, 100.00m, 100.10m)
            .Run();

        Assert.Equal(OrderStatus.Accepted, order!.Status);
        Assert.Empty(sim.Fills(order));
    }

    [Fact]
    public void Cancel_that_arrives_before_the_crossing_quote_wins_the_race()
    {
        // Cancel sent at 1500 with 110 ms latency arrives at 1610; the crossing quote comes at 1700.
        using SimHarness sim = SimHarness.Spot(new SimOptions { LatencyModel = Slow });
        LimitOrder? order = null;
        sim.Quote(900, 100.00m, 100.10m)
            .At(1000, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(99.50m))))
            .Quote(1200, 100.00m, 100.10m)
            .At(1500, s => s.Cancel(order!))
            .Quote(1650, 100.00m, 100.10m)
            .Quote(1700, 99.30m, 99.40m)
            .Run();

        Assert.Equal(OrderStatus.Canceled, order!.Status);
        Assert.Empty(sim.Fills(order));
    }

    [Fact]
    public void Cancel_that_arrives_after_the_fill_loses_the_race_and_is_rejected()
    {
        // Cancel sent at 1500 arrives at 1610; the crossing quote at 1600 has already filled the order.
        using SimHarness sim = SimHarness.Spot(new SimOptions { LatencyModel = Slow });
        LimitOrder? order = null;
        sim.Quote(900, 100.00m, 100.10m)
            .At(1000, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(99.50m))))
            .Quote(1200, 100.00m, 100.10m)
            .At(1500, s => s.Cancel(order!))
            .Quote(1600, 99.30m, 99.40m)
            .Quote(1700, 99.30m, 99.40m)
            .Run();

        Assert.Equal(OrderStatus.Filled, order!.Status);
        Assert.Equal(Scripted.Ms(1600), sim.SingleFill(order).TsEvent);
        OrderCancelRejected rejected = Assert.Single(sim.Events.OfType<OrderCancelRejected>());
        Assert.Equal("order not found at venue", rejected.Reason);
    }

    [Fact]
    public void Modification_takes_effect_only_after_the_update_latency()
    {
        // Re-price from 99.00 to 99.80 sent at 1500, effective 1620. The 1600 quote (ask 99.70) is through 99.80 but
        // arrives too early; the 1700 quote is back above it.
        using SimHarness sim = SimHarness.Spot(new SimOptions { LatencyModel = Slow });
        LimitOrder? order = null;
        sim.Quote(900, 100.00m, 100.10m)
            .At(1000, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(99.00m))))
            .Quote(1200, 100.00m, 100.10m)
            .At(1500, s => s.Modify(order!, price: sim.Px(99.80m)))
            .Quote(1600, 99.60m, 99.70m)
            .Quote(1610, 100.00m, 100.10m)
            .Quote(1700, 100.00m, 100.10m)
            .Run();

        Assert.Equal(sim.Px(99.80m), order!.Price);
        Assert.Equal(OrderStatus.Accepted, order.Status);
        Assert.Empty(sim.Fills(order));
    }

    [Fact]
    public void Modification_that_arrives_after_the_fill_is_rejected_by_the_venue()
    {
        using SimHarness sim = SimHarness.Spot(new SimOptions { LatencyModel = Slow });
        LimitOrder? order = null;
        sim.Quote(900, 100.00m, 100.10m)
            .At(1000, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(99.50m))))
            .Quote(1200, 100.00m, 100.10m)
            .At(1500, s => s.Modify(order!, price: sim.Px(98.00m)))
            .Quote(1600, 99.30m, 99.40m)
            .Quote(1700, 99.30m, 99.40m)
            .Run();

        Assert.Equal(sim.Px(99.50m), sim.SingleFill(order!).LastPx);
        OrderModifyRejected rejected = Assert.Single(sim.Events.OfType<OrderModifyRejected>());
        Assert.Equal("order not found at venue", rejected.Reason);
    }

    [Fact]
    public void Order_cancelled_while_its_submission_is_in_flight_is_not_cancelled_behind_the_venues_back()
    {
        // Submitted at 1000, so it reaches the venue at 1150; cancelled at 1050, so the cancel gets there at 1160. The
        // venue works the market order before the cancel arrives, and no venue un-fills an order because a cancel was
        // sent ten milliseconds too late. What must not happen - and did - is the engine reporting the order cancelled
        // while the venue goes on to fill it.
        using SimHarness sim = SimHarness.Spot(new SimOptions { LatencyModel = Slow });
        MarketOrder? order = null;
        sim.Quote(900, 100.00m, 100.10m)
            .At(1000, s => order = s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .At(1050, s => s.Cancel(order!))
            .Quote(1400, 100.00m, 100.10m)
            .Run();

        Assert.Equal(OrderStatus.Filled, order!.Status);
        Assert.Equal(sim.Px(100.10m), sim.SingleFill(order).LastPx);
        Assert.Empty(sim.Events.OfType<OrderCanceled>());
        Assert.Equal("order not found at venue", Assert.Single(sim.Events.OfType<OrderCancelRejected>()).Reason);
    }

    [Fact]
    public void A_resting_order_cancelled_while_it_was_in_flight_is_cancelled_when_the_venue_answers_for_it()
    {
        // The same race, with an order that does not fill on arrival: the cancel could not be sent while the order was
        // on its way, so it is sent the moment the venue accepts it, and the order is gone before the market reaches it.
        using SimHarness sim = SimHarness.Spot(new SimOptions { LatencyModel = Slow });
        LimitOrder? order = null;
        sim.Quote(900, 100.00m, 100.10m)
            .At(1000, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(99.00m))))
            .At(1050, s => s.Cancel(order!))
            .Quote(1400, 100.00m, 100.10m)
            .Quote(2000, 98.90m, 99.00m) // would have filled the limit
            .Run();

        Assert.Equal(OrderStatus.Canceled, order!.Status);
        Assert.Empty(sim.Fills(order));
        Assert.Empty(sim.Events.OfType<OrderCancelRejected>());
        Assert.Equal(0, sim.Exchange.OpenOrderCount);
    }

    [Fact]
    public void Zero_latency_model_executes_commands_at_the_instant_they_are_sent()
    {
        using SimHarness sim = SimHarness.Spot(new SimOptions { LatencyModel = LatencyModel.Zero });
        MarketOrder? order = null;
        sim.Quote(900, 100.00m, 100.10m)
            .At(1000, s => order = s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(1100, 101.00m, 101.10m)
            .Run();

        OrderFilled fill = sim.SingleFill(order!);
        Assert.Equal(Scripted.Ms(1000), fill.TsEvent);
        Assert.Equal(sim.Px(100.10m), fill.LastPx);
    }

    [Fact]
    public void A_cancel_that_overtakes_its_own_submission_is_sent_again_when_the_order_arrives()
    {
        // Submissions take half a second, cancels no time at all, so the cancel reaches the venue first and is refused:
        // there is no such order there yet. The engine sends it again the moment the venue accepts the order, instead of
        // leaving it working.
        LatencyModel cancelFirst = new(Base: TimeSpan.Zero, Insert: TimeSpan.FromMilliseconds(500), Update: TimeSpan.Zero, Cancel: TimeSpan.Zero);
        using SimHarness sim = SimHarness.Spot(new SimOptions { LatencyModel = cancelFirst });
        LimitOrder? order = null;
        sim.Quote(900, 100.00m, 100.10m)
            .At(1000, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(99.00m))))
            .At(1050, s => s.Cancel(order!))
            .Quote(2000, 100.00m, 100.10m)
            .Quote(3000, 98.90m, 99.00m) // would have filled the limit
            .Run();

        Assert.Equal(OrderStatus.Canceled, order!.Status);
        Assert.Empty(sim.Fills(order));
        Assert.Single(sim.Events.OfType<OrderCancelRejected>()); // the first cancel, for an order the venue did not hold
        Assert.Equal(0, sim.Exchange.OpenOrderCount);
    }
}
