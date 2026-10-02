using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Positions;
using Bytex.Core.Model.Primitives;

namespace Bytex.Backtest.Tests;

// Why (R6.6): a position's fills are kept, so everything realised about it can be worked out again. What cannot be
// worked out again is what it was worth WHILE IT WAS OPEN - that needs the price at that moment, and a price that has
// moved on is gone. So a snapshot carries the mark it was measured against and the unrealised profit at it.
//
// This engine does not need snapshots to keep a position's history: a netting position that flips is given a new id and
// the old one keeps its own record. That is worth saying because it is why snapshots exist elsewhere.
public sealed class PositionSnapshotTests
{
    /// <summary>
    /// No fees, so the figures in these tests are the position's own arithmetic rather than that minus a commission.
    /// What is being checked is what a snapshot carries, and a fee would only make every number here harder to read.
    /// </summary>
    private static SimOptions Margin() => new()
    {
        AccountType = AccountType.Margin,
        StartingBalances = [new Money(1_000_000m, Currencies.USDT)],
        DefaultLeverage = 20m,
        FeeModel = new PercentFeeModel(0m),
    };

    [Fact]
    public void A_position_that_closes_is_kept_as_it_was_at_the_fill_that_closed_it()
    {
        using SimHarness sim = SimHarness.Perp(Margin());
        sim.Quote(1000, 50_000.0m, 50_000.0m, size: 100m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(2000, 51_000.0m, 51_000.0m, size: 100m)
            .At(2500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Sell, sim.Qty(1m))))
            .Quote(3000, 52_000.0m, 52_000.0m, size: 100m)
            .Run();

        PositionSnapshot snapshot = Assert.Single(sim.Engine.Cache.PositionSnapshots());

        Assert.Equal(PositionSide.Flat, snapshot.Side);
        Assert.Equal(50_000m, snapshot.AvgPxOpen);
        Assert.Equal(51_000m, snapshot.AvgPxClose);
        Assert.Equal(1_000m, snapshot.RealizedPnl.Amount);

        // A closed position has nothing left to be up or down on, so there is no mark and no unrealised figure - rather
        // than a zero, which would read as "measured, and flat".
        Assert.Null(snapshot.MarkPrice);
        Assert.Null(snapshot.UnrealizedPnl);
        Assert.Equal(snapshot.RealizedPnl, snapshot.TotalPnl);
    }

    [Fact]
    public void A_snapshot_of_an_open_position_carries_the_price_it_was_measured_against()
    {
        // The whole reason the type exists: this figure cannot be recovered from the fills afterwards.
        using SimHarness sim = SimHarness.Perp(Margin());
        sim.Quote(1000, 50_000.0m, 50_000.0m, size: 100m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(2000, 51_000.0m, 51_100.0m, size: 100m)
            .At(2500, _ => { })
            .Run();

        Position position = Assert.Single(sim.Engine.Cache.PositionsOpen());
        PositionSnapshot snapshot = sim.Engine.Cache.SnapshotPosition(position);

        Assert.Equal(PositionSide.Long, snapshot.Side);
        // Marked at the bid, which is what a long would be closed at.
        Assert.Equal(51_000m, snapshot.MarkPrice!.Value);
        Assert.Equal(1_000m, snapshot.UnrealizedPnl!.Value.Amount);
        Assert.Equal(0m, snapshot.RealizedPnl.Amount);
        Assert.Equal(1_000m, snapshot.TotalPnl.Amount);
        Assert.Null(snapshot.TsClosed);
    }

    [Fact]
    public void A_price_given_by_the_caller_is_the_one_it_is_marked_at()
    {
        // A report asking "what was it worth at the close" gives the close, not whatever the cache holds now.
        using SimHarness sim = SimHarness.Perp(Margin());
        sim.Quote(1000, 50_000.0m, 50_000.0m, size: 100m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(2000, 51_000.0m, 51_100.0m, size: 100m)
            .Run();

        Position position = Assert.Single(sim.Engine.Cache.PositionsOpen());
        PositionSnapshot snapshot = sim.Engine.Cache.SnapshotPosition(position, sim.Px(49_000.0m));

        Assert.Equal(49_000m, snapshot.MarkPrice!.Value);
        Assert.Equal(-1_000m, snapshot.UnrealizedPnl!.Value.Amount);
    }

    [Fact]
    public void Snapshots_are_kept_in_the_order_they_were_taken_and_can_be_narrowed()
    {
        using SimHarness sim = SimHarness.Perp(Margin());
        sim.Quote(1000, 50_000.0m, 50_000.0m, size: 100m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(2000, 51_000.0m, 51_000.0m, size: 100m)
            .Run();

        Position position = Assert.Single(sim.Engine.Cache.PositionsOpen());
        sim.Engine.Cache.SnapshotPosition(position, sim.Px(51_000.0m));
        sim.Engine.Cache.SnapshotPosition(position, sim.Px(52_000.0m));

        IReadOnlyList<PositionSnapshot> all = sim.Engine.Cache.PositionSnapshots();

        Assert.Equal(2, all.Count);
        Assert.Equal(51_000m, all[0].MarkPrice!.Value);
        Assert.Equal(52_000m, all[1].MarkPrice!.Value);
        Assert.Equal(2, sim.Engine.Cache.PositionSnapshots(positionId: position.Id).Count);
        Assert.Equal(2, sim.Engine.Cache.PositionSnapshots(marketKey: sim.Id).Count);
        Assert.Empty(sim.Engine.Cache.PositionSnapshots(positionId: new PositionId("P-NOWHERE")));
        Assert.Empty(sim.Engine.Cache.PositionSnapshots(strategyId: new StrategyId("Nobody-001")));
    }

    [Fact]
    public void A_flipped_netting_position_keeps_its_own_record_without_needing_a_snapshot()
    {
        // Which is why snapshots are not this engine's way of keeping history: the old position is still there, with its
        // own id, its own fills and its own realised result.
        using SimHarness sim = SimHarness.Perp(Margin());
        sim.Quote(1000, 50_000.0m, 50_000.0m, size: 100m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(2000, 51_000.0m, 51_000.0m, size: 100m)
            .At(2500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Sell, sim.Qty(3m))))
            .Quote(3000, 51_000.0m, 51_000.0m, size: 100m)
            .Run();

        IReadOnlyList<Position> positions = sim.Engine.Cache.Positions();

        Assert.Equal(2, positions.Count);
        Assert.Single(positions, p => p.IsClosed);
        Assert.Single(positions, p => p.IsOpen && p.IsShort);
        Assert.NotEqual(positions[0].Id, positions[1].Id);
    }
}
