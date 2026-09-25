using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;

namespace Bytex.Backtest.Tests;

// Why: time in force changes what happens to the part of an order that cannot trade now. The documented rules:
// IOC/FOK that cannot fill on arrival are cancelled, GTD expires at its time, at-the-open/close fill on the next
// bar's open or close.
public sealed class TimeInForceTests
{
    [Theory]
    [InlineData(TimeInForce.Ioc)]
    [InlineData(TimeInForce.Fok)]
    public void Immediate_order_that_cannot_fill_on_arrival_is_cancelled_and_never_rests(TimeInForce tif)
    {
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(99.50m), tif)))
            .Quote(2000, 99.00m, 99.10m) // would have filled a resting order
            .Run();

        Assert.Equal(OrderStatus.Canceled, order!.Status);
        Assert.Equal("Initialized,Submitted,Accepted,Canceled", SimHarness.EventNames(order));
        Assert.Equal(Scripted.Ms(1500), order.TsClosed);
        Assert.Empty(sim.Fills(order));
    }

    [Theory]
    [InlineData(TimeInForce.Ioc)]
    [InlineData(TimeInForce.Fok)]
    public void Immediate_order_that_crosses_on_arrival_fills_in_full_as_taker(TimeInForce tif)
    {
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(100.20m), tif)))
            .Quote(2000, 100.00m, 100.10m)
            .Run();

        OrderFilled fill = sim.SingleFill(order!);
        Assert.Equal(sim.Px(100.10m), fill.LastPx);
        Assert.Equal(sim.Qty(1m), fill.LastQty);
        Assert.Equal(LiquiditySide.Taker, fill.LiquiditySide);
    }

    [Fact]
    public void Gtd_order_fills_normally_before_its_expiry()
    {
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(99.50m), TimeInForce.Gtd, Scripted.Ms(5000))))
            .Quote(2000, 99.30m, 99.40m)
            .Run();

        Assert.Equal(OrderStatus.Filled, order!.Status);
        Assert.Equal(sim.Px(99.50m), sim.SingleFill(order).LastPx);
    }

    [Fact]
    public void Gtd_order_expires_at_its_time_and_a_later_crossing_quote_cannot_fill_it()
    {
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(99.50m), TimeInForce.Gtd, Scripted.Ms(2500))))
            .Quote(2000, 100.00m, 100.10m)
            .Quote(3000, 99.30m, 99.40m)
            .Run();

        Assert.Equal(OrderStatus.Expired, order!.Status);
        Assert.Equal("Initialized,Submitted,Accepted,Expired", SimHarness.EventNames(order));
        Assert.Empty(sim.Fills(order));
        Assert.Equal(0, sim.Exchange.OpenOrderCount);
    }

    [Fact]
    public void Gtd_order_expires_exactly_at_the_expiry_timestamp_not_one_tick_later()
    {
        // Expiry 2000 and a crossing quote stamped 2000: the order is already expired at that instant.
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(99.50m), TimeInForce.Gtd, Scripted.Ms(2000))))
            .Quote(2000, 99.30m, 99.40m)
            .Run();

        Assert.Equal(OrderStatus.Expired, order!.Status);
        Assert.Equal(Scripted.Ms(2000), order.TsClosed);
    }

    [Fact]
    public void Gtd_stop_order_expires_like_any_other_resting_order()
    {
        using SimHarness sim = SimHarness.Spot();
        StopMarketOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.StopMarket(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(101.00m), timeInForce: TimeInForce.Gtd, expireTime: Scripted.Ms(2500))))
            .Quote(3000, 101.50m, 101.60m)
            .Run();

        Assert.Equal(OrderStatus.Expired, order!.Status);
        Assert.Empty(sim.Fills(order));
    }

    [Fact]
    public void Day_order_works_like_a_resting_order_within_the_same_day()
    {
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(99.50m), TimeInForce.Day)))
            .Quote(3_600_000, 99.30m, 99.40m) // one hour later, same UTC day
            .Run();

        Assert.Equal(sim.Px(99.50m), sim.SingleFill(order!).LastPx);
    }

    [Fact]
    public void Day_order_is_not_filled_three_days_after_it_was_placed()
    {
        // Whatever session calendar a venue uses, "day" cannot span three calendar days.
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(99.50m), TimeInForce.Day)))
            .Quote(3L * 86_400_000L, 99.30m, 99.40m)
            .Run();

        Assert.Empty(sim.Fills(order!));
        Assert.Equal(OrderStatus.Expired, order!.Status);
    }

    [Fact]
    public void At_the_open_order_waits_for_the_next_bar_and_fills_at_its_open()
    {
        using SimHarness sim = SimHarness.Spot();
        MarketToLimitOrder? order = null;
        sim.Bar(60_000, 100.00m, 101.00m, 99.00m, 100.50m)
            .At(90_000, s => order = s.Submit(s.Orders.MarketToLimit(sim.Id, OrderSide.Buy, sim.Qty(1m), TimeInForce.AtTheOpen)))
            .At(100_000, s => s.Note("status " + order!.Status)) // a price exists, yet the order must wait for the open
            .Bar(120_000, 102.00m, 104.00m, 101.00m, 103.00m)
            .Run();

        Assert.Contains("100000|status Accepted", sim.Strategy.Journal);
        OrderFilled fill = sim.SingleFill(order!);
        Assert.Equal(sim.Px(102.00m), fill.LastPx);
        Assert.Equal(Scripted.Ms(120_000), fill.TsEvent);
        Assert.Equal(LiquiditySide.Taker, fill.LiquiditySide);
    }

    [Fact]
    public void At_the_close_order_waits_for_the_next_bar_and_fills_at_its_close()
    {
        using SimHarness sim = SimHarness.Spot();
        MarketToLimitOrder? order = null;
        sim.Bar(60_000, 100.00m, 101.00m, 99.00m, 100.50m)
            .At(90_000, s => order = s.Submit(s.Orders.MarketToLimit(sim.Id, OrderSide.Sell, sim.Qty(1m), TimeInForce.AtTheClose)))
            .Bar(120_000, 102.00m, 104.00m, 101.00m, 103.00m)
            .Run();

        Assert.Equal(sim.Px(103.00m), sim.SingleFill(order!).LastPx);
    }

    [Fact]
    public void Limit_on_open_order_never_fills_at_a_price_worse_than_its_limit()
    {
        // Buy limit 99.00 at-the-open; the next bar opens at 102.00 and never trades below 101.00.
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? order = null;
        sim.Bar(60_000, 100.00m, 101.00m, 99.50m, 100.50m)
            .At(90_000, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(99.00m), TimeInForce.AtTheOpen)))
            .Bar(120_000, 102.00m, 104.00m, 101.00m, 103.00m)
            .Run();

        Assert.All(sim.Fills(order!), fill => Assert.True(fill.LastPx <= new Price(99.00m, 2), $"filled at {fill.LastPx}"));
    }

    [Fact]
    public void Day_order_expires_at_the_first_bar_of_the_next_day_and_not_before()
    {
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(99.50m), TimeInForce.Day)))
            .Quote(86_399_000, 100.00m, 100.10m)  // last second of the same UTC day: still working
            .Quote(86_400_500, 99.30m, 99.40m)    // the next day, at a price that would have filled it
            .Run();

        Assert.Empty(sim.Fills(order!));
        Assert.Equal(OrderStatus.Expired, order!.Status);
        Assert.Equal(Scripted.Ms(86_400_500), order.TsClosed);
    }

    [Fact]
    public void Day_order_that_fills_on_its_own_day_is_not_touched_by_the_rollover()
    {
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(99.50m), TimeInForce.Day)))
            .Quote(80_000_000, 99.30m, 99.40m)
            .Quote(3L * 86_400_000L, 99.30m, 99.40m)
            .Run();

        Assert.Equal(sim.Px(99.50m), sim.SingleFill(order!).LastPx);
        Assert.Equal(OrderStatus.Filled, order!.Status);
    }
}
