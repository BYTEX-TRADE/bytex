using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Orders;

namespace Bytex.Backtest.Tests;

// Why: a market order is the base case of the simulator. It must lift the ask / hit the bid that the venue
// last saw, never the mid and never a future price.
public sealed class MarketOrderMatchingTests
{
    [Theory]
    [InlineData(OrderSide.Buy, 100.10)]
    [InlineData(OrderSide.Sell, 100.00)]
    public void Market_order_fills_at_the_opposite_side_of_the_last_quote(OrderSide side, decimal expectedPrice)
    {
        using SimHarness sim = SimHarness.Spot();
        MarketOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.Market(sim.Id, side, sim.Qty(2m))))
            .Quote(2000, 105.00m, 105.10m)
            .Run();

        OrderFilled fill = sim.SingleFill(order!);
        Assert.Equal(sim.Px(expectedPrice), fill.LastPx);
        Assert.Equal(sim.Qty(2m), fill.LastQty);
        Assert.Equal(LiquiditySide.Taker, fill.LiquiditySide);
        Assert.Equal(Scripted.Ms(1500), fill.TsEvent);
        Assert.Equal(OrderStatus.Filled, order!.Status);
        Assert.Equal("Initialized,Submitted,Accepted,Filled", SimHarness.EventNames(order));
    }

    [Fact]
    public void Market_order_uses_the_last_trade_price_when_only_trades_are_available()
    {
        using SimHarness sim = SimHarness.Spot();
        MarketOrder? buy = null;
        MarketOrder? sell = null;
        sim.Trade(1000, 250.50m)
            .At(1500, s =>
            {
                buy = s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m)));
                sell = s.Submit(s.Orders.Market(sim.Id, OrderSide.Sell, sim.Qty(1m)));
            })
            .Trade(2000, 251.00m)
            .Run();

        Assert.Equal(sim.Px(250.50m), sim.SingleFill(buy!).LastPx);
        Assert.Equal(sim.Px(250.50m), sim.SingleFill(sell!).LastPx);
    }

    [Fact(Skip = "BUG: with trade-only data the synthetic bid/ask only ever widen (bid = lowest print, ask = highest print since the start)")]
    public void With_trade_only_data_market_orders_fill_at_the_most_recent_trade_price()
    {
        using SimHarness sim = SimHarness.Spot();
        MarketOrder? buy = null;
        MarketOrder? sell = null;
        sim.Trade(1000, 100.00m)
            .Trade(2000, 105.00m)
            .Trade(3000, 101.00m)
            .At(3500, s =>
            {
                buy = s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m)));
                sell = s.Submit(s.Orders.Market(sim.Id, OrderSide.Sell, sim.Qty(1m)));
            })
            .Trade(4000, 101.00m)
            .Run();

        Assert.Equal(sim.Px(101.00m), sim.SingleFill(buy!).LastPx);
        Assert.Equal(sim.Px(101.00m), sim.SingleFill(sell!).LastPx);
    }

    [Fact]
    public void Trade_outside_the_quoted_spread_drags_the_touched_side_of_the_book_with_it()
    {
        // Quote 100.00/100.10, then a print at 100.30 (above the ask): the ask can no longer be 100.10.
        using SimHarness sim = SimHarness.Spot();
        MarketOrder? buy = null;
        MarketOrder? sell = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .Trade(2000, 100.30m)
            .At(2500, s =>
            {
                buy = s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m)));
                sell = s.Submit(s.Orders.Market(sim.Id, OrderSide.Sell, sim.Qty(1m)));
            })
            .Quote(3000, 100.00m, 100.10m)
            .Run();

        Assert.Equal(sim.Px(100.30m), sim.SingleFill(buy!).LastPx);
        Assert.Equal(sim.Px(100.00m), sim.SingleFill(sell!).LastPx);
    }

    [Fact]
    public void Market_order_before_any_market_data_is_rejected_for_lack_of_a_price()
    {
        using SimHarness sim = SimHarness.Spot();
        MarketOrder? order = null;
        sim.At(500, s => order = s.Submit(s.Orders.Market(sim.Id, OrderSide.Sell, sim.Qty(1m))))
            .Quote(1000, 100.00m, 100.10m)
            .Run();

        Assert.Equal(OrderStatus.Rejected, order!.Status);
        OrderRejected rejected = Assert.IsType<OrderRejected>(order.LastEvent);
        Assert.Equal("no market price available", rejected.Reason);
        Assert.Empty(sim.Fills(order));
    }

    [Fact]
    public void Market_order_does_not_see_a_quote_that_arrives_at_a_later_timestamp()
    {
        using SimHarness sim = SimHarness.Spot();
        MarketOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1999, s => order = s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(2000, 90.00m, 90.10m)
            .Run();

        Assert.Equal(sim.Px(100.10m), sim.SingleFill(order!).LastPx);
    }

    [Fact]
    public void Market_to_limit_order_fills_at_market_and_records_the_fill_price_as_its_limit()
    {
        using SimHarness sim = SimHarness.Spot();
        MarketToLimitOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.MarketToLimit(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(2000, 100.00m, 100.10m)
            .Run();

        OrderFilled fill = sim.SingleFill(order!);
        Assert.Equal(sim.Px(100.10m), fill.LastPx);
        Assert.Equal(LiquiditySide.Taker, fill.LiquiditySide);
        Assert.Equal(sim.Px(100.10m), order!.Price);
        Assert.Equal("Initialized,Submitted,Accepted,Updated,Filled", SimHarness.EventNames(order));
    }

    [Fact]
    public void Every_fill_gets_a_unique_sequential_trade_id_and_every_order_a_venue_order_id()
    {
        using SimHarness sim = SimHarness.Spot();
        List<MarketOrder> orders = new();
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1100, s => orders.Add(s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m)))))
            .At(1200, s => orders.Add(s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m)))))
            .At(1300, s => orders.Add(s.Submit(s.Orders.Market(sim.Id, OrderSide.Sell, sim.Qty(1m)))))
            .Quote(2000, 100.00m, 100.10m)
            .Run();

        Assert.Equal(["T-1", "T-2", "T-3"], orders.Select(o => sim.SingleFill(o).TradeId.Value));
        Assert.Equal(["V-1", "V-2", "V-3"], orders.Select(o => o.VenueOrderId!.Value.Value));
    }
}
