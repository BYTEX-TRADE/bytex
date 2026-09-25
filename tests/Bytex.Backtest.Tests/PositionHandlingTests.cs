using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Positions;
using Bytex.Core.Model.Primitives;

namespace Bytex.Backtest.Tests;

// Why: the venue's OMS type decides how fills become positions. Netting keeps one position per instrument and
// strategy (a larger opposite fill closes it and opens a new one); hedging opens a position per entry order.
public sealed class PositionHandlingTests
{
    private static SimOptions ZeroFees(OmsType oms) => new() { OmsType = oms, FeeModel = new FixedFeeModel(Money.Zero(Currencies.USDT)) };

    [Fact]
    public void Netting_adds_same_side_fills_to_one_position_at_the_volume_weighted_entry_price()
    {
        using SimHarness sim = SimHarness.Perp(ZeroFees(OmsType.Netting));
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1100, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(2000, 53_000.0m, 53_000.0m)
            .At(2100, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(2m))))
            .Quote(3000, 53_000.0m, 53_000.0m)
            .Run();

        Position position = Assert.Single(sim.Positions);
        Assert.Equal("BTCUSDT-PERP.SIM-Scripted-001", position.Id.Value);
        Assert.Equal(PositionSide.Long, position.Side);
        Assert.Equal(sim.Qty(3m), position.Quantity);
        Assert.Equal(52_000m, position.AvgPxOpen); // (50 000 + 2 * 53 000) / 3
        Assert.Equal(["PositionOpened", "PositionChanged"], sim.Events.OfType<PositionEvent>().Select(e => e.GetType().Name));
    }

    [Fact]
    public void Netting_closes_the_position_when_an_opposite_fill_matches_its_size_and_numbers_the_next_one()
    {
        using SimHarness sim = SimHarness.Perp(ZeroFees(OmsType.Netting));
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1100, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .At(1200, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Sell, sim.Qty(1m))))
            .At(1300, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Sell, sim.Qty(1m))))
            .Quote(2000, 50_000.0m, 50_000.0m)
            .Run();

        Assert.Equal(2, sim.Positions.Count);
        Position first = sim.Positions.Single(p => p.Id.Value == "BTCUSDT-PERP.SIM-Scripted-001");
        Position second = sim.Positions.Single(p => p.Id.Value == "BTCUSDT-PERP.SIM-Scripted-001-2");
        Assert.True(first.IsClosed);
        Assert.Equal(PositionSide.Long, first.EntrySide);
        Assert.Equal(Scripted.Ms(1200), first.TsClosed);
        Assert.Equal(PositionSide.Short, second.Side);
        Assert.Equal(sim.Qty(1m), second.Quantity);
    }

    [Fact]
    public void Netting_splits_a_flipping_fill_into_a_closing_part_and_an_opening_part()
    {
        // Long 1 @ 50 000, then sell 3 @ 51 000 with a 0.05 % taker fee (76.5 in total).
        using SimHarness sim = SimHarness.Perp(new SimOptions { OmsType = OmsType.Netting });
        MarketOrder? flip = null;
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1100, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(2000, 51_000.0m, 51_000.0m)
            .At(2100, s => flip = s.Submit(s.Orders.Market(sim.Id, OrderSide.Sell, sim.Qty(3m))))
            .Quote(3000, 51_000.0m, 51_000.0m)
            .Run();

        List<OrderFilled> fills = sim.Fills(flip!).ToList();
        Assert.Equal(["T-2-C", "T-2-O"], fills.Select(f => f.TradeId.Value));
        Assert.Equal([sim.Qty(1m), sim.Qty(2m)], fills.Select(f => f.LastQty));
        Assert.Equal([new Money(25.5m, Currencies.USDT), new Money(51m, Currencies.USDT)], fills.Select(f => f.Commission)); // 76.5 split 1:2
        Assert.Equal(OrderStatus.Filled, flip!.Status);
        Assert.Equal(sim.Qty(3m), flip.FilledQuantity);

        Position closed = sim.Positions.Single(p => p.IsClosed);
        Position opened = sim.Positions.Single(p => p.IsOpen);
        Assert.Equal(new Money(1000m - 25m - 25.5m, Currencies.USDT), closed.RealizedPnl);
        Assert.Equal(PositionSide.Short, opened.Side);
        Assert.Equal(sim.Qty(2m), opened.Quantity);
        Assert.Equal(51_000m, opened.AvgPxOpen);
        Assert.Equal(new Money(-51m, Currencies.USDT), opened.RealizedPnl);
        Assert.Equal(
            ["PositionOpened", "PositionClosed", "PositionOpened"],
            sim.Events.OfType<PositionEvent>().Select(e => e.GetType().Name));
    }

    [Fact]
    public void Hedging_opens_a_separate_position_for_every_entry_order_even_in_opposite_directions()
    {
        using SimHarness sim = SimHarness.Perp(ZeroFees(OmsType.Hedging));
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1100, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .At(1200, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Sell, sim.Qty(2m))))
            .Quote(2000, 50_000.0m, 50_000.0m)
            .Run();

        Assert.Equal(2, sim.Positions.Count);
        Assert.All(sim.Positions, p => Assert.True(p.IsOpen));
        Assert.Equal(sim.Qty(1m), sim.Positions.Single(p => p.IsLong).Quantity);
        Assert.Equal(sim.Qty(2m), sim.Positions.Single(p => p.IsShort).Quantity);
        Assert.Equal(-1m, sim.Engine.Kernel.Portfolio.NetPosition(sim.Id));
    }

    // The position an order was submitted against has to survive until the fill is applied, or a closing order opens
    // a new opposite position and the book grows a leg nobody asked for.
    [Fact]
    public void Hedging_order_submitted_against_a_position_id_reduces_that_position_only()
    {
        using SimHarness sim = SimHarness.Perp(ZeroFees(OmsType.Hedging));
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1100, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(2m))))
            .At(1200, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(5m))))
            .Quote(2000, 52_000.0m, 52_000.0m)
            .At(2100, s =>
            {
                Position smaller = s.Store.PositionsOpen().Single(p => p.Quantity == sim.Qty(2m));
                s.Submit(s.Orders.Market(sim.Id, OrderSide.Sell, sim.Qty(2m)), smaller.Id);
            })
            .Quote(3000, 52_000.0m, 52_000.0m)
            .Run();

        Assert.Equal(2, sim.Positions.Count);
        Position closed = sim.Positions.Single(p => p.IsClosed);
        Position open = sim.Positions.Single(p => p.IsOpen);
        Assert.Equal(sim.Qty(2m), closed.PeakQuantity);
        Assert.Equal(new Money(4000m, Currencies.USDT), closed.RealizedPnl); // (52 000 - 50 000) * 2
        Assert.Equal(sim.Qty(5m), open.Quantity);
    }

    // The venue's own book is netted whatever the account does, so a long leg and a short leg net to nothing: judged
    // against that net, the close of either leg would be refused as an order that increases a position.
    [Fact]
    public void Hedging_close_position_helper_closes_one_leg_of_a_fully_hedged_book()
    {
        using SimHarness sim = SimHarness.Perp(ZeroFees(OmsType.Hedging));
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1100, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .At(1200, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Sell, sim.Qty(1m))))
            .At(1300, s => s.Close(s.Store.PositionsOpen().Single(p => p.IsLong)))
            .Quote(2000, 50_000.0m, 50_000.0m)
            .Run();

        Assert.True(sim.Positions.Single(p => p.EntrySide == PositionSide.Long).IsClosed);
        Assert.True(sim.Positions.Single(p => p.EntrySide == PositionSide.Short).IsOpen);
    }

    [Fact]
    public void Close_all_positions_flattens_a_netting_position_with_a_reduce_only_market_order()
    {
        using SimHarness sim = SimHarness.Perp(ZeroFees(OmsType.Netting));
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1100, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Sell, sim.Qty(2m))))
            .Quote(2000, 49_000.0m, 49_000.0m)
            .At(2100, s => s.CloseAll(sim.Id))
            .Quote(3000, 49_000.0m, 49_000.0m)
            .Run();

        Position position = Assert.Single(sim.Positions);
        Assert.True(position.IsClosed);
        Assert.Equal(new Money(2000m, Currencies.USDT), position.RealizedPnl);
        Order closing = sim.Engine.Cache.Orders().Single(o => o.IsReduceOnly);
        Assert.Equal(OrderSide.Buy, closing.Side);
        Assert.Equal(sim.Qty(2m), closing.Quantity);
        Assert.Equal(102_000m, sim.Balance(Currencies.USDT));
    }
}
