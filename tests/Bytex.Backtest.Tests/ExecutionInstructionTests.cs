using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Orders;

namespace Bytex.Backtest.Tests;

// Why: post-only and reduce-only are promises the venue makes to the strategy. Documented behaviour: a post-only
// order that would cross is rejected; a reduce-only order that would open or increase a position is rejected.
public sealed class ExecutionInstructionTests
{
    [Theory]
    [InlineData(OrderSide.Buy, 100.10)] // at the ask
    [InlineData(OrderSide.Buy, 100.50)] // through the ask
    [InlineData(OrderSide.Sell, 100.00)] // at the bid
    [InlineData(OrderSide.Sell, 99.00)] // through the bid
    public void Post_only_order_that_would_cross_is_rejected_and_never_trades(OrderSide side, decimal price)
    {
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.Limit(sim.Id, side, sim.Qty(1m), sim.Px(price), postOnly: true)))
            .Quote(2000, 100.00m, 100.10m)
            .Run();

        Assert.Equal(OrderStatus.Rejected, order!.Status);
        Assert.Equal("post-only order would have crossed the book", Assert.IsType<OrderRejected>(order.LastEvent).Reason);
        Assert.Empty(sim.Fills(order));
        Assert.Equal(0, sim.Exchange.OpenOrderCount);
    }

    [Fact]
    public void Post_only_order_that_does_not_cross_rests_and_later_fills_as_maker()
    {
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(100.05m), postOnly: true)))
            .Quote(2000, 99.90m, 100.00m)
            .Run();

        OrderFilled fill = sim.SingleFill(order!);
        Assert.Equal(sim.Px(100.05m), fill.LastPx);
        Assert.Equal(LiquiditySide.Maker, fill.LiquiditySide);
    }

    [Fact]
    public void Reduce_only_order_without_a_position_is_rejected()
    {
        using SimHarness sim = SimHarness.Perp();
        MarketOrder? order = null;
        sim.Quote(1000, 50_000.0m, 50_000.1m)
            .At(1500, s => order = s.Submit(s.Orders.Market(sim.Id, OrderSide.Sell, sim.Qty(1m), reduceOnly: true)))
            .Quote(2000, 50_000.0m, 50_000.1m)
            .Run();

        Assert.Equal(OrderStatus.Rejected, order!.Status);
        Assert.Equal("reduce-only order would increase position", Assert.IsType<OrderRejected>(order.LastEvent).Reason);
    }

    [Theory]
    [InlineData(OrderSide.Buy)] // long 1, reduce-only buy would increase it
    [InlineData(OrderSide.Sell)] // short 1, reduce-only sell would increase it
    public void Reduce_only_order_on_the_same_side_as_the_position_is_rejected(OrderSide side)
    {
        using SimHarness sim = SimHarness.Perp();
        MarketOrder? order = null;
        sim.Quote(1000, 50_000.0m, 50_000.1m)
            .At(1100, s => s.Submit(s.Orders.Market(sim.Id, side, sim.Qty(1m))))
            .At(1500, s => order = s.Submit(s.Orders.Market(sim.Id, side, sim.Qty(1m), reduceOnly: true)))
            .Quote(2000, 50_000.0m, 50_000.1m)
            .Run();

        Assert.Equal(OrderStatus.Rejected, order!.Status);
        Assert.Equal(side == OrderSide.Buy ? 1m : -1m, sim.Engine.Kernel.Portfolio.NetPosition(sim.Id));
    }

    [Fact]
    public void Reduce_only_order_against_the_position_fills_and_flattens_it()
    {
        using SimHarness sim = SimHarness.Perp();
        MarketOrder? close = null;
        sim.Quote(1000, 50_000.0m, 50_000.1m)
            .At(1100, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(2m))))
            .At(1500, s => close = s.Submit(s.Orders.Market(sim.Id, OrderSide.Sell, sim.Qty(2m), reduceOnly: true)))
            .Quote(2000, 50_000.0m, 50_000.1m)
            .Run();

        Assert.Equal(OrderStatus.Filled, close!.Status);
        Assert.Equal(0m, sim.Engine.Kernel.Portfolio.NetPosition(sim.Id));
    }

    [Fact]
    public void Reduce_only_order_larger_than_the_position_never_opens_the_opposite_position()
    {
        // Long 1, reduce-only sell 3. Rejecting or clipping to 1 are both acceptable; ending up short 2 is not.
        using SimHarness sim = SimHarness.Perp();
        sim.Quote(1000, 50_000.0m, 50_000.1m)
            .At(1100, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Sell, sim.Qty(3m), reduceOnly: true)))
            .Quote(2000, 50_000.0m, 50_000.1m)
            .Run();

        Assert.True(sim.Engine.Kernel.Portfolio.NetPosition(sim.Id) >= 0m, $"net position is {sim.Engine.Kernel.Portfolio.NetPosition(sim.Id)}");
    }
}
