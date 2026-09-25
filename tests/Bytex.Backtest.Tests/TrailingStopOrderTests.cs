using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Orders;

namespace Bytex.Backtest.Tests;

// Why: a trailing stop must ratchet only in the holder's favour. A sell trailing stop follows the bid up and never
// down; a buy trailing stop follows the ask down and never up. Offsets can be a price, basis points or ticks.
public sealed class TrailingStopOrderTests
{
    [Theory]
    [InlineData(TrailingOffsetType.Price, 1.50, 98.50)] // 100.00 - 1.50
    [InlineData(TrailingOffsetType.BasisPoints, 200, 98.00)] // 100.00 * 200 / 10000 = 2.00 below
    [InlineData(TrailingOffsetType.Ticks, 25, 99.75)] // 25 ticks * 0.01 = 0.25 below
    public void Sell_trailing_stop_starts_the_offset_below_the_bid(TrailingOffsetType offsetType, decimal offset, decimal expectedTrigger)
    {
        using SimHarness sim = SimHarness.Spot();
        TrailingStopMarketOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.TrailingStopMarket(sim.Id, OrderSide.Sell, sim.Qty(1m), offset, offsetType)))
            .Quote(2000, 100.00m, 100.10m)
            .Run();

        Assert.Equal(sim.Px(expectedTrigger), order!.TriggerPrice);
        Assert.Equal(OrderStatus.Accepted, order.Status);
    }

    [Theory]
    [InlineData(TrailingOffsetType.Price, 1.50, 101.60)] // 100.10 + 1.50
    [InlineData(TrailingOffsetType.BasisPoints, 1000, 110.11)] // 100.10 * 10 % = 10.01 above
    [InlineData(TrailingOffsetType.Ticks, 25, 100.35)]
    public void Buy_trailing_stop_starts_the_offset_above_the_ask(TrailingOffsetType offsetType, decimal offset, decimal expectedTrigger)
    {
        using SimHarness sim = SimHarness.Spot();
        TrailingStopMarketOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.TrailingStopMarket(sim.Id, OrderSide.Buy, sim.Qty(1m), offset, offsetType)))
            .Quote(2000, 100.00m, 100.10m)
            .Run();

        Assert.Equal(sim.Px(expectedTrigger), order!.TriggerPrice);
    }

    [Fact]
    public void Sell_trailing_stop_follows_the_bid_up_holds_on_pullbacks_and_fills_when_the_bid_falls_to_it()
    {
        using SimHarness sim = SimHarness.Spot();
        TrailingStopMarketOrder? order = null;
        List<string> triggers = new();
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.TrailingStopMarket(sim.Id, OrderSide.Sell, sim.Qty(1m), 1.00m)))
            .Quote(2000, 102.00m, 102.10m) // trigger ratchets to 101.00
            .At(2500, _ => triggers.Add(order!.TriggerPrice.ToString()!))
            .Quote(3000, 101.50m, 101.60m) // pullback: trigger must stay at 101.00
            .At(3500, _ => triggers.Add(order!.TriggerPrice.ToString()!))
            .Quote(4000, 103.00m, 103.10m) // new high: 102.00
            .At(4500, _ => triggers.Add(order!.TriggerPrice.ToString()!))
            .Quote(5000, 101.90m, 102.00m) // bid 101.90 <= 102.00: stopped out
            .Run();

        Assert.Equal(["101.00", "101.00", "102.00"], triggers);
        OrderFilled fill = sim.SingleFill(order!);
        Assert.Equal(sim.Px(101.90m), fill.LastPx);
        Assert.Equal(Scripted.Ms(5000), fill.TsEvent);
        Assert.Equal(LiquiditySide.Taker, fill.LiquiditySide);
    }

    [Fact]
    public void Buy_trailing_stop_follows_the_ask_down_holds_on_bounces_and_fills_when_the_ask_rises_to_it()
    {
        using SimHarness sim = SimHarness.Spot();
        TrailingStopMarketOrder? order = null;
        List<string> triggers = new();
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.TrailingStopMarket(sim.Id, OrderSide.Buy, sim.Qty(1m), 1.00m)))
            .At(1600, _ => triggers.Add(order!.TriggerPrice.ToString()!)) // 101.10
            .Quote(2000, 98.00m, 98.10m) // 99.10
            .At(2500, _ => triggers.Add(order!.TriggerPrice.ToString()!))
            .Quote(3000, 98.50m, 98.60m) // bounce: stays 99.10
            .At(3500, _ => triggers.Add(order!.TriggerPrice.ToString()!))
            .Quote(4000, 99.10m, 99.20m) // ask 99.20 >= 99.10
            .Run();

        Assert.Equal(["101.10", "99.10", "99.10"], triggers);
        Assert.Equal(sim.Px(99.20m), sim.SingleFill(order!).LastPx);
    }

    [Fact]
    public void Basis_point_offset_is_recomputed_from_the_new_price_each_time_the_stop_ratchets()
    {
        // 100 bp = 1 %. At a bid of 100 the stop sits 1.00 below; at a bid of 200 it sits 2.00 below.
        using SimHarness sim = SimHarness.Spot();
        TrailingStopMarketOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.TrailingStopMarket(sim.Id, OrderSide.Sell, sim.Qty(1m), 100m, TrailingOffsetType.BasisPoints)))
            .Quote(2000, 200.00m, 200.10m)
            .Run();

        Assert.Equal(sim.Px(198.00m), order!.TriggerPrice);
    }

    [Fact]
    public void Trailing_stop_with_an_explicit_trigger_price_starts_from_that_trigger()
    {
        using SimHarness sim = SimHarness.Spot();
        TrailingStopMarketOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.TrailingStopMarket(sim.Id, OrderSide.Sell, sim.Qty(1m), 1.00m, triggerPrice: sim.Px(97.00m))))
            .Quote(2000, 97.50m, 97.60m) // above the explicit trigger; 100.00 - 1.00 = 99.00 would already have fired
            .At(2500, s => s.Note("status " + order!.Status))
            .Quote(3000, 96.90m, 97.00m)
            .Run();

        Assert.Contains("2500|status Accepted", sim.Strategy.Journal);
        Assert.Equal(sim.Px(96.90m), sim.SingleFill(order!).LastPx);
    }

    [Fact]
    public void Each_ratchet_is_reported_to_the_strategy_as_an_order_update_carrying_the_new_trigger()
    {
        using SimHarness sim = SimHarness.Spot();
        TrailingStopMarketOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.TrailingStopMarket(sim.Id, OrderSide.Sell, sim.Qty(1m), 1.00m)))
            .Quote(2000, 102.00m, 102.10m)
            .Quote(3000, 101.50m, 101.60m)
            .Run();

        List<OrderUpdated> updates = order!.Events.OfType<OrderUpdated>().ToList();
        Assert.Equal([sim.Px(99.00m), sim.Px(101.00m)], updates.Select(u => u.TriggerPrice!.Value));
        Assert.Equal([Scripted.Ms(1500), Scripted.Ms(2000)], updates.Select(u => u.TsEvent));
    }

    [Fact]
    public void Trailing_stop_limit_moves_trigger_and_limit_together_and_fills_as_a_limit_once_triggered()
    {
        // Sell, trail 1.00, limit 0.20 below the trigger.
        using SimHarness sim = SimHarness.Spot();
        TrailingStopLimitOrder? order = null;
        List<string> prices = new();
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.TrailingStopLimit(sim.Id, OrderSide.Sell, sim.Qty(1m), 1.00m, 0.20m)))
            .At(1600, _ => prices.Add($"{order!.TriggerPrice}/{order.Price}")) // 99.00 / 98.80
            .Quote(2000, 102.00m, 102.10m)
            .At(2500, _ => prices.Add($"{order!.TriggerPrice}/{order.Price}")) // 101.00 / 100.80
            .Quote(3000, 100.95m, 101.05m) // bid 100.95 <= 101.00 triggers; 100.95 >= limit 100.80 so it trades at the bid
            .Run();

        Assert.Equal(["99.00/98.80", "101.00/100.80"], prices);
        OrderFilled fill = sim.SingleFill(order!);
        Assert.Equal(sim.Px(100.95m), fill.LastPx);
        Assert.Equal(LiquiditySide.Taker, fill.LiquiditySide);
    }

    [Fact]
    public void Trailing_stop_limit_that_gaps_below_its_limit_does_not_sell_under_the_limit()
    {
        using SimHarness sim = SimHarness.Spot();
        TrailingStopLimitOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.TrailingStopLimit(sim.Id, OrderSide.Sell, sim.Qty(1m), 1.00m, 0.20m)))
            .Quote(2000, 98.00m, 98.10m) // trigger 99.00 hit, but the bid is under the 98.80 limit
            .Run();

        Assert.Equal(OrderStatus.Triggered, order!.Status);
        Assert.Empty(sim.Fills(order));
    }

    [Fact]
    public void Trailing_stop_with_an_activation_price_is_dormant_until_the_market_reaches_it()
    {
        // Sell trailing stop, offset 1.00, activates at 105.00. The bid drops from 100 to 98 without ever reaching 105.
        using SimHarness sim = SimHarness.Spot();
        TrailingStopMarketOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.TrailingStopMarket(sim.Id, OrderSide.Sell, sim.Qty(1m), 1.00m, activationPrice: sim.Px(105.00m))))
            .Quote(2000, 98.00m, 98.10m)
            .Run();

        Assert.Empty(sim.Fills(order!));
    }

    [Fact]
    public void Trailing_stop_starts_trailing_from_the_price_that_activated_it()
    {
        // Sell trailing stop, offset 1.00, activates at 105.00. The market climbs to 106, which activates it and sets
        // the stop at 105.00, then falls back: the stop fires at 105.00, not at the 99.00 it would have had from the
        // price it was placed at.
        using SimHarness sim = SimHarness.Spot();
        TrailingStopMarketOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.TrailingStopMarket(sim.Id, OrderSide.Sell, sim.Qty(1m), 1.00m, activationPrice: sim.Px(105.00m))))
            .Quote(2000, 103.00m, 103.10m) // not there yet: dormant, no trail
            .Quote(2500, 106.00m, 106.10m) // activated, stop at 105.00
            .Quote(3000, 104.90m, 105.00m) // through the stop
            .Run();

        OrderFilled fill = sim.SingleFill(order!);
        Assert.Equal(Scripted.Ms(3000), fill.TsEvent);
        Assert.Equal(sim.Px(104.90m), fill.LastPx);
    }

    [Fact]
    public void A_dormant_trailing_stop_does_not_move_its_trigger_while_it_waits()
    {
        using SimHarness sim = SimHarness.Spot();
        TrailingStopMarketOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.TrailingStopMarket(sim.Id, OrderSide.Sell, sim.Qty(1m), 1.00m, activationPrice: sim.Px(105.00m))))
            .Quote(2000, 102.00m, 102.10m)
            .Quote(2500, 104.90m, 105.00m)
            .Run();

        // Nothing has been announced about its trigger, because it has not started trailing.
        Assert.Empty(sim.Events.OfType<OrderUpdated>());
        Assert.Equal(OrderStatus.Accepted, order!.Status);
        Assert.Empty(sim.Fills(order));
    }
}
