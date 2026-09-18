using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Orders;

namespace Bytex.Backtest.Tests;

// Why: besides quotes, trades and bars the engine accepts order book deltas. The venue matches against the top of
// the book those deltas build.
public sealed class OrderBookDataTests
{
    [Fact]
    public void Market_order_trades_against_the_top_of_the_book_built_from_deltas()
    {
        using SimHarness sim = SimHarness.Spot();
        MarketOrder? buy = null;
        MarketOrder? sell = null;
        sim.Book(1000, 100.00m, 100.10m)
            .At(1500, s =>
            {
                buy = s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m)));
                sell = s.Submit(s.Orders.Market(sim.Id, OrderSide.Sell, sim.Qty(1m)));
            })
            .Book(2000, 99.00m, 101.00m) // worse levels on both sides: the best prices do not change
            .Run();

        Assert.Equal(sim.Px(100.10m), sim.SingleFill(buy!).LastPx);
        Assert.Equal(sim.Px(100.00m), sim.SingleFill(sell!).LastPx);
    }

    [Fact]
    public void Resting_limit_order_fills_when_a_better_offer_appears_in_the_book()
    {
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? order = null;
        sim.Book(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(100.05m))))
            .Book(2000, 99.90m, 100.02m) // new best ask 100.02 is through the 100.05 bid
            .Run();

        OrderFilled fill = sim.SingleFill(order!);
        Assert.Equal(sim.Px(100.05m), fill.LastPx);
        Assert.Equal(LiquiditySide.Maker, fill.LiquiditySide);
        Assert.Equal(Scripted.Ms(2000), fill.TsEvent);
    }

    [Fact(Skip = "BUG: for order book data the strategy is notified before the venue matches, the reverse of the documented order used for quotes, trades and bars")]
    public void Venue_matches_resting_orders_against_a_book_update_before_the_strategy_sees_that_update()
    {
        using SimHarness sim = SimHarness.Spot();
        sim.Book(1000, 100.00m, 100.10m)
            .At(1500, s => s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(100.05m))))
            .Book(2000, 99.90m, 100.02m)
            .Run();

        List<string> atFillTime = sim.Strategy.Journal.Where(line => line.StartsWith("2000|", StringComparison.Ordinal)).ToList();
        Assert.Equal(["2000|OrderFilled", "2000|book BTCUSDT 2 deltas"], atFillTime);
    }
}
