using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Orders;

namespace Bytex.Backtest.Tests;

// Why: conditional orders have mirrored trigger rules that are easy to get backwards. A buy stop triggers when the
// ask rises to the trigger, a buy if-touched when the ask falls to it; sells mirror this on the bid.
public sealed class StopAndTouchOrderTests
{
    [Theory]
    [InlineData(OrderSide.Buy, 101.00, 100.90, 101.00, 101.00)] // ask rises exactly to the trigger
    [InlineData(OrderSide.Buy, 101.00, 101.40, 101.50, 101.50)] // ask gaps through: market order pays the new ask
    [InlineData(OrderSide.Sell, 99.00, 99.00, 99.10, 99.00)] // bid falls exactly to the trigger
    [InlineData(OrderSide.Sell, 99.00, 98.50, 98.60, 98.50)] // bid gaps through: market order hits the new bid
    public void Stop_market_order_triggers_when_the_market_moves_through_it_and_fills_at_the_market(OrderSide side, decimal trigger, decimal bid, decimal ask, decimal expectedFill)
    {
        using SimHarness sim = SimHarness.Spot();
        StopMarketOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.StopMarket(sim.Id, side, sim.Qty(1m), sim.Px(trigger))))
            .Quote(2000, bid, ask)
            .Run();

        OrderFilled fill = sim.SingleFill(order!);
        Assert.Equal(sim.Px(expectedFill), fill.LastPx);
        Assert.Equal(LiquiditySide.Taker, fill.LiquiditySide);
        Assert.Equal("Initialized,Submitted,Accepted,Triggered,Filled", SimHarness.EventNames(order!));
    }

    [Theory]
    [InlineData(OrderSide.Buy, 101.00, 100.80, 100.90)] // ask still one tick-step below a buy trigger
    [InlineData(OrderSide.Sell, 99.00, 99.10, 99.20)] // bid still above a sell trigger
    [InlineData(OrderSide.Buy, 101.00, 95.00, 95.10)] // moving away from a buy stop never triggers it
    [InlineData(OrderSide.Sell, 99.00, 105.00, 105.10)]
    public void Stop_market_order_stays_untriggered_while_the_market_has_not_reached_it(OrderSide side, decimal trigger, decimal bid, decimal ask)
    {
        using SimHarness sim = SimHarness.Spot();
        StopMarketOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.StopMarket(sim.Id, side, sim.Qty(1m), sim.Px(trigger))))
            .Quote(2000, bid, ask)
            .Run();

        Assert.Equal(OrderStatus.Accepted, order!.Status);
        Assert.Empty(sim.Fills(order));
    }

    [Theory]
    [InlineData(OrderSide.Buy, 100.10)] // trigger equal to the ask
    [InlineData(OrderSide.Buy, 99.00)]
    [InlineData(OrderSide.Sell, 100.00)] // trigger equal to the bid
    [InlineData(OrderSide.Sell, 101.00)]
    public void Stop_order_whose_trigger_is_already_through_the_market_is_rejected_on_arrival(OrderSide side, decimal trigger)
    {
        using SimHarness sim = SimHarness.Spot();
        StopMarketOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.StopMarket(sim.Id, side, sim.Qty(1m), sim.Px(trigger))))
            .Quote(2000, 100.00m, 100.10m)
            .Run();

        Assert.Equal(OrderStatus.Rejected, order!.Status);
        Assert.Contains("already through the market", Assert.IsType<OrderRejected>(order.LastEvent).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Stop_order_through_the_market_is_accepted_and_triggers_at_once_when_the_venue_does_not_reject_such_orders()
    {
        using SimHarness sim = SimHarness.Spot(new SimOptions { RejectStopOrdersAtMarket = false });
        StopMarketOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.StopMarket(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(99.00m))))
            .Quote(2000, 100.00m, 100.10m)
            .Run();

        OrderFilled fill = sim.SingleFill(order!);
        Assert.Equal(sim.Px(100.10m), fill.LastPx);
        Assert.Equal(Scripted.Ms(1500), fill.TsEvent);
    }

    [Fact]
    public void Stop_order_with_last_price_trigger_ignores_quotes_and_waits_for_a_trade_print()
    {
        using SimHarness sim = SimHarness.Spot();
        StopMarketOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .Trade(1100, 100.05m)
            .At(1500, s => order = s.Submit(s.Orders.StopMarket(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(101.00m), TriggerType.LastPrice)))
            .Quote(2000, 101.00m, 101.10m) // the ask is through the trigger, the last trade is not
            .At(2500, s => s.Note("status " + order!.Status))
            .Trade(3000, 101.05m)
            .Run();

        Assert.Contains("2500|status Accepted", sim.Strategy.Journal);
        OrderFilled fill = sim.SingleFill(order!);
        Assert.Equal(Scripted.Ms(3000), fill.TsEvent);
        Assert.Equal(sim.Px(101.10m), fill.LastPx); // a buy market order still pays the ask
    }

    [Fact]
    public void Stop_limit_order_becomes_a_taker_limit_when_triggered_with_the_market_inside_its_limit()
    {
        using SimHarness sim = SimHarness.Spot();
        StopLimitOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.StopLimit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(101.50m), sim.Px(101.00m))))
            .Quote(2000, 101.10m, 101.20m)
            .Run();

        OrderFilled fill = sim.SingleFill(order!);
        Assert.Equal(sim.Px(101.20m), fill.LastPx);
        Assert.Equal(LiquiditySide.Taker, fill.LiquiditySide);
        Assert.Equal("Initialized,Submitted,Accepted,Triggered,Filled", SimHarness.EventNames(order!));
    }

    [Fact]
    public void Stop_limit_order_that_gaps_past_its_limit_rests_as_triggered_and_fills_as_maker_when_the_market_returns()
    {
        using SimHarness sim = SimHarness.Spot();
        StopLimitOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.StopLimit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(101.50m), sim.Px(101.00m))))
            .Quote(2000, 101.90m, 102.00m) // triggered, but the ask is above the 101.50 limit
            .At(2500, s => s.Note("status " + order!.Status))
            .Quote(3000, 101.30m, 101.40m) // ask back below the limit
            .Run();

        Assert.Contains("2500|status Triggered", sim.Strategy.Journal);
        OrderFilled fill = sim.SingleFill(order!);
        Assert.Equal(sim.Px(101.50m), fill.LastPx);
        Assert.Equal(LiquiditySide.Maker, fill.LiquiditySide);
        Assert.Equal(Scripted.Ms(3000), fill.TsEvent);
    }

    [Theory]
    [InlineData(OrderSide.Buy, 99.50, 99.40, 99.50, 99.50)] // ask falls to the trigger: buy at the ask
    [InlineData(OrderSide.Buy, 99.50, 99.10, 99.20, 99.20)]
    [InlineData(OrderSide.Sell, 100.50, 100.50, 100.60, 100.50)] // bid rises to the trigger: sell at the bid
    [InlineData(OrderSide.Sell, 100.50, 100.80, 100.90, 100.80)]
    public void Market_if_touched_order_triggers_from_the_other_side_and_fills_at_the_market(OrderSide side, decimal trigger, decimal bid, decimal ask, decimal expectedFill)
    {
        using SimHarness sim = SimHarness.Spot();
        MarketIfTouchedOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.MarketIfTouched(sim.Id, side, sim.Qty(1m), sim.Px(trigger))))
            .Quote(2000, bid, ask)
            .Run();

        OrderFilled fill = sim.SingleFill(order!);
        Assert.Equal(sim.Px(expectedFill), fill.LastPx);
        Assert.Equal(LiquiditySide.Taker, fill.LiquiditySide);
        Assert.Equal("Initialized,Submitted,Accepted,Triggered,Filled", SimHarness.EventNames(order!));
    }

    [Theory]
    [InlineData(OrderSide.Buy, 99.50, 100.40, 100.50)] // market rallies away from a buy-if-touched
    [InlineData(OrderSide.Sell, 100.50, 99.00, 99.10)]
    public void Market_if_touched_order_is_not_triggered_by_a_move_in_the_stop_direction(OrderSide side, decimal trigger, decimal bid, decimal ask)
    {
        using SimHarness sim = SimHarness.Spot();
        MarketIfTouchedOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.MarketIfTouched(sim.Id, side, sim.Qty(1m), sim.Px(trigger))))
            .Quote(2000, bid, ask)
            .Run();

        Assert.Equal(OrderStatus.Accepted, order!.Status);
    }

    [Fact]
    public void Limit_if_touched_order_rests_at_its_limit_after_the_touch_and_fills_as_maker_later()
    {
        using SimHarness sim = SimHarness.Spot();
        LimitIfTouchedOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.LimitIfTouched(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(99.30m), sim.Px(99.50m))))
            .Quote(2000, 99.40m, 99.50m) // touched; 99.30 bid is still below the ask
            .At(2500, s => s.Note("status " + order!.Status))
            .Quote(3000, 99.10m, 99.20m)
            .Run();

        Assert.Contains("2500|status Triggered", sim.Strategy.Journal);
        OrderFilled fill = sim.SingleFill(order!);
        Assert.Equal(sim.Px(99.30m), fill.LastPx);
        Assert.Equal(LiquiditySide.Maker, fill.LiquiditySide);
    }

    [Fact]
    public void Limit_if_touched_order_whose_limit_is_marketable_at_the_touch_fills_as_taker_at_the_market()
    {
        using SimHarness sim = SimHarness.Spot();
        LimitIfTouchedOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.LimitIfTouched(sim.Id, OrderSide.Sell, sim.Qty(1m), sim.Px(100.40m), sim.Px(100.50m))))
            .Quote(2000, 100.60m, 100.70m)
            .Run();

        OrderFilled fill = sim.SingleFill(order!);
        Assert.Equal(sim.Px(100.60m), fill.LastPx);
        Assert.Equal(LiquiditySide.Taker, fill.LiquiditySide);
    }
}
