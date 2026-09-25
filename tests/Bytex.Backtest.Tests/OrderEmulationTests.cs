using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Positions;
using Bytex.Core.Model.Primitives;

namespace Bytex.Backtest.Tests;

// Why: the engine's own tests prove the trigger; this proves the whole way through. An emulated stop has to reach a
// venue as something the venue can hold, fill on its own id, and build the position it was placed for - and a venue
// that refuses stop orders outright, which is the reason emulation exists, has to be able to trade a strategy that
// uses them.
public sealed class OrderEmulationTests
{
    private static SimOptions NoStopsAtTheVenue => new()
    {
        AccountType = AccountType.Margin,
        StartingBalances = [new Money(100_000m, Currencies.USDT)],
        FeeModel = new FixedFeeModel(Money.Zero(Currencies.USDT)),
        SupportContingentOrders = false,
    };

    [Fact]
    public void An_emulated_stop_reaches_the_venue_as_a_market_order_and_fills_on_its_own_id()
    {
        using SimHarness sim = SimHarness.For(TestInstruments.Perp(), NoStopsAtTheVenue);
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1500, s => s.Submit(s.Orders.StopMarket(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(51_000m), emulationTrigger: TriggerType.BidAsk)))
            .Quote(2000, 50_500.0m, 50_500.0m)
            .Quote(3000, 51_000.0m, 51_000.0m)
            .Run();

        Order order = Assert.Single(sim.Engine.Kernel.Cache.Orders());
        OrderFilled fill = Assert.Single(sim.Events.OfType<OrderFilled>());

        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal(order.ClientOrderId, fill.ClientOrderId);
        Assert.Equal(OrderType.Market, order.Type);
        Assert.Equal(51_000m, fill.LastPx.Value);

        // Held, released, and then the venue's own answers: one order, one life, in order.
        Assert.Equal(
            ["OrderInitialized", "OrderEmulated", "OrderReleased", "OrderSubmitted", "OrderAccepted", "OrderFilled"],
            order.Events.Select(e => e.GetType().Name));
        Assert.Equal(1m, sim.Engine.Kernel.Portfolio.NetPosition(sim.Id));
    }

    [Fact]
    public void Nothing_reaches_the_venue_while_the_market_stays_away_from_the_trigger()
    {
        using SimHarness sim = SimHarness.For(TestInstruments.Perp(), NoStopsAtTheVenue);
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1500, s => s.Submit(s.Orders.StopMarket(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(51_000m), emulationTrigger: TriggerType.BidAsk)))
            .Quote(2000, 50_100.0m, 50_100.0m)
            .Quote(3000, 49_000.0m, 49_000.0m)
            .Run();

        Order order = Assert.Single(sim.Engine.Kernel.Cache.Orders());

        Assert.Equal(OrderStatus.Emulated, order.Status);
        Assert.Empty(sim.Events.OfType<OrderFilled>());
        Assert.Empty(sim.Events.OfType<OrderAccepted>());
        Assert.Equal(0m, sim.Engine.Kernel.Portfolio.NetPosition(sim.Id));
    }

    [Fact]
    public void A_stop_that_gets_a_position_out_is_released_reduce_only()
    {
        // The case a venue without stops makes hard: a long with a protective stop under it. The stop is held here,
        // and when the market reaches it the venue is told about a reduce-only market order, which closes the
        // position rather than opening a short.
        using SimHarness sim = SimHarness.For(TestInstruments.Perp(), NoStopsAtTheVenue);
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .At(1600, s => s.Submit(s.Orders.StopMarket(sim.Id, OrderSide.Sell, sim.Qty(1m), sim.Px(49_000m), reduceOnly: true, emulationTrigger: TriggerType.BidAsk)))
            .Quote(2000, 49_500.0m, 49_500.0m)
            .Quote(3000, 48_900.0m, 48_900.0m)
            .Run();

        Assert.Equal(0m, sim.Engine.Kernel.Portfolio.NetPosition(sim.Id));

        // What the venue was told is an order that can only reduce: released as anything else it could open a short
        // on a venue that nets, and on one that hedges it would open a second position beside the one it was under.
        Order stop = Assert.Single(sim.Engine.Kernel.Cache.Orders(), o => o.IsSell);
        Assert.Equal(OrderType.Market, stop.Type);
        Assert.True(stop.IsReduceOnly, "the stop was released as an order that could open a position");

        Position position = Assert.Single(sim.Engine.Kernel.Cache.Positions());
        Assert.True(position.IsClosed, "the stop did not close the position it was placed under");
        Assert.Equal(-1_100m, position.RealizedPnl.Amount); // out at 48,900 from 50,000
    }

    [Fact]
    public void A_backtest_that_holds_an_order_does_the_same_thing_twice()
    {
        // The trigger is judged on the engine's clock and the cache, so two runs of one script have to agree: a
        // release that depended on the order data happened to arrive in would make a backtest unrepeatable.
        static SimHarness Run()
        {
            SimHarness sim = SimHarness.For(TestInstruments.Perp(), NoStopsAtTheVenue);
            return sim.Quote(1000, 50_000.0m, 50_000.0m)
                .At(1500, s => s.Submit(s.Orders.StopMarket(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(50_500m), emulationTrigger: TriggerType.BidAsk)))
                .Quote(2000, 50_400.0m, 50_400.0m)
                .Trade(2500, 50_600.0m)
                .Quote(3000, 50_500.0m, 50_500.0m)
                .Quote(4000, 50_800.0m, 50_800.0m)
                .Run();
        }

        using SimHarness first = Run();
        using SimHarness second = Run();

        Assert.Equal(Fingerprint(first), Fingerprint(second));
    }

    private static string Fingerprint(SimHarness sim) => string.Join(
        " | ",
        sim.Engine.Kernel.Cache.Orders().Select(o => $"{o.ClientOrderId} {o.Type} {o.Status} {o.AvgPx}"));
}
