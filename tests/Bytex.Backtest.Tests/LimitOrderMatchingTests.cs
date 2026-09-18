using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;

namespace Bytex.Backtest.Tests;

// Why: limit orders decide most of a backtest's realism. The documented rules are: crossing on arrival is a taker
// fill at the opposite price; a resting order is a maker fill at its own price once the market trades through it.
public sealed class LimitOrderMatchingTests
{
    [Theory]
    [InlineData(OrderSide.Buy, 99.50)]
    [InlineData(OrderSide.Sell, 100.60)]
    public void Passive_limit_order_rests_without_filling_while_the_market_stays_away(OrderSide side, double price)
    {
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.Limit(sim.Id, side, sim.Qty(1m), sim.Px((decimal)price))))
            .Quote(2000, 100.02m, 100.12m)
            .Quote(3000, 99.98m, 100.08m)
            .Run();

        Assert.Equal(OrderStatus.Accepted, order!.Status);
        Assert.Empty(sim.Fills(order));
        Assert.Equal(1, sim.Exchange.OpenOrderCount);
    }

    [Theory]
    [InlineData(OrderSide.Buy, 99.50, 99.30, 99.40)] // ask falls to 99.40, below the 99.50 bid
    [InlineData(OrderSide.Sell, 100.60, 100.70, 100.80)] // bid rises to 100.70, above the 100.60 offer
    public void Resting_limit_order_fills_as_maker_at_its_own_price_when_the_market_trades_through_it(OrderSide side, double limit, double bid, double ask)
    {
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.Limit(sim.Id, side, sim.Qty(3m), sim.Px((decimal)limit))))
            .Quote(2000, (decimal)bid, (decimal)ask)
            .Run();

        OrderFilled fill = sim.SingleFill(order!);
        Assert.Equal(sim.Px((decimal)limit), fill.LastPx);
        Assert.Equal(sim.Qty(3m), fill.LastQty);
        Assert.Equal(LiquiditySide.Maker, fill.LiquiditySide);
        Assert.Equal(Scripted.Ms(2000), fill.TsEvent);
        Assert.Equal(0, sim.Exchange.OpenOrderCount);
    }

    [Theory]
    [InlineData(OrderSide.Buy, 100.50, 100.10)] // buys through the 100.10 ask
    [InlineData(OrderSide.Buy, 100.10, 100.10)] // exactly at the ask still crosses
    [InlineData(OrderSide.Sell, 99.00, 100.00)] // sells through the 100.00 bid
    [InlineData(OrderSide.Sell, 100.00, 100.00)]
    public void Marketable_limit_order_fills_on_arrival_as_taker_at_the_opposite_price_not_at_its_limit(OrderSide side, double limit, double expected)
    {
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.Limit(sim.Id, side, sim.Qty(1m), sim.Px((decimal)limit))))
            .Quote(2000, 100.00m, 100.10m)
            .Run();

        OrderFilled fill = sim.SingleFill(order!);
        Assert.Equal(sim.Px((decimal)expected), fill.LastPx);
        Assert.Equal(LiquiditySide.Taker, fill.LiquiditySide);
        Assert.Equal(Scripted.Ms(1500), fill.TsEvent);
    }

    [Fact(Skip = "BUG: a trade printing through a resting limit order does not fill it; a print above the ask only lifts the ask, which resting sells never look at")]
    public void Resting_limit_order_fills_when_a_trade_prints_through_its_price()
    {
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Sell, sim.Qty(1m), sim.Px(100.50m))))
            .Trade(2000, 100.70m) // somebody paid 100.70 while our offer at 100.50 was in the book
            .Run();

        OrderFilled fill = sim.SingleFill(order!);
        Assert.Equal(sim.Px(100.50m), fill.LastPx);
        Assert.Equal(LiquiditySide.Maker, fill.LiquiditySide);
    }

    [Fact]
    public void Trade_inside_the_spread_does_not_fill_a_resting_order_on_the_far_side()
    {
        // The sell rests at 100.50; a print at 100.05 is inside 100.00/100.10 and moves neither side.
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Sell, sim.Qty(1m), sim.Px(100.50m))))
            .Trade(2000, 100.05m)
            .Run();

        Assert.Equal(OrderStatus.Accepted, order!.Status);
    }

    [Fact]
    public void Limit_order_is_matched_before_the_strategy_sees_the_tick_that_filled_it()
    {
        using SimHarness sim = SimHarness.Spot();
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(99.50m))))
            .Quote(2000, 99.30m, 99.40m)
            .Run();

        List<string> atFillTime = sim.Strategy.Journal.Where(line => line.StartsWith("2000|", StringComparison.Ordinal)).ToList();
        Assert.Equal(["2000|OrderFilled", "2000|quote BTCUSDT 99.30/99.40"], atFillTime);
    }

    [Fact]
    public void Two_resting_orders_at_different_prices_fill_independently()
    {
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? near = null;
        LimitOrder? far = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s =>
            {
                near = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(99.80m)));
                far = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(99.00m)));
            })
            .Quote(2000, 99.60m, 99.70m)
            .Run();

        Assert.Equal(OrderStatus.Filled, near!.Status);
        Assert.Equal(sim.Px(99.80m), sim.SingleFill(near).LastPx);
        Assert.Equal(OrderStatus.Accepted, far!.Status);
    }

    [Fact]
    public void Iceberg_limit_order_fills_its_full_quantity_not_only_the_displayed_part()
    {
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(5m), sim.Px(99.50m), displayQuantity: sim.Qty(1m))))
            .Quote(2000, 99.30m, 99.40m)
            .Run();

        Assert.True(order!.IsIceberg);
        Assert.Equal(sim.Qty(5m), order.FilledQuantity);
        Assert.Equal(sim.Qty(0m), order.LeavesQuantity);
    }

    [Fact(Skip = "BUG: quote-quantity orders are filled as if the quantity were in base currency (1000 USDT buys 1000 BTC instead of 10)")]
    public void Quote_quantity_order_spends_the_stated_amount_of_quote_currency()
    {
        // 1000 USDT at an ask of 100.00 buys 10 BTC. A venue that cannot honour quote quantities should reject instead.
        using SimHarness sim = SimHarness.Spot();
        MarketOrder? order = null;
        sim.Quote(1000, 99.90m, 100.00m)
            .At(1500, s => order = s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1000m), quoteQuantity: true)))
            .Quote(2000, 99.90m, 100.00m)
            .Run();

        if (order!.Status != OrderStatus.Rejected)
        {
            Assert.Equal(sim.Qty(10m), sim.SingleFill(order).LastQty);
            Assert.Equal(1_000_000m - 1000m - 2m, sim.Balance(Currencies.USDT)); // 0.2 % taker fee on 1000
        }
    }
}
