using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Positions;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Tests.Support;

namespace Bytex.Core.Tests.Engines;

// Why: fills become positions. Netting keeps one position per instrument and strategy and flips it with a
// fresh id; hedging opens one position per order. All P&L figures below are computed by hand.
public class ExecutionEnginePositionTests
{
    private static PositionId FirstNettingId => new("BTCUSDT.BINANCE-S-001");

    private static PositionId SecondNettingId => new("BTCUSDT.BINANCE-S-001-2");

    [Fact]
    public void Netting_first_fill_opens_a_position_named_after_instrument_and_strategy()
    {
        ExecHarness h = new();

        MarketOrder order = h.FillMarket("O-1", OrderSide.Buy, "1.000", "50000.00", commission: "5");

        Position position = Assert.Single(h.Cache.PositionsOpen());
        Assert.Equal(FirstNettingId, position.Id);
        Assert.Equal(PositionSide.Long, position.Side);
        Assert.Equal(Quantity.Parse("1.000"), position.Quantity);
        Assert.Equal(50_000m, position.AvgPxOpen);
        Assert.Equal(FirstNettingId, order.PositionId);
        Assert.Same(position, h.Cache.PositionForOrder(order.ClientOrderId));

        PositionOpened opened = Assert.IsType<PositionOpened>(Assert.Single(h.PositionEvents.Messages));
        Assert.Equal(FirstNettingId, opened.PositionId);
        Assert.Equal(PositionSide.Long, opened.EntrySide);
        Assert.Equal(order.ClientOrderId, opened.OpeningOrderId);
        Assert.Equal(Price.Parse("50000.00"), opened.LastPx);
        // Opening commission is the only realized P&L so far: -5 USDT. Marked at the fill price nothing is unrealized.
        Assert.Equal(Money.Parse("-5 USDT"), opened.RealizedPnl);
        Assert.Equal(Money.Parse("0 USDT"), opened.UnrealizedPnl);
    }

    [Fact]
    public void Position_already_reflects_the_fill_when_the_fill_event_is_published()
    {
        ExecHarness h = new();
        List<decimal> netSeenByHandler = new();
        h.Bus.Subscribe(Topics.OrderEvents(TestIds.Strategy), m =>
        {
            if (m is OrderFilled)
            {
                netSeenByHandler.Add(h.Portfolio.NetPosition(TestIds.BtcUsdt));
            }
        });

        h.FillMarket("O-1", OrderSide.Buy, "1.000", "50000.00");

        Assert.Equal([1.000m], netSeenByHandler);
    }

    [Fact]
    public void Netting_second_fill_on_the_same_side_averages_the_entry_price()
    {
        ExecHarness h = new();
        h.FillMarket("O-1", OrderSide.Buy, "1.000", "50000.00");

        h.FillMarket("O-2", OrderSide.Buy, "3.000", "51000.00");

        // (1 * 50,000 + 3 * 51,000) / 4 = 50,750
        Position position = Assert.Single(h.Cache.Positions());
        Assert.Equal(Quantity.Parse("4.000"), position.Quantity);
        Assert.Equal(50_750m, position.AvgPxOpen);
        PositionChanged changed = Assert.IsType<PositionChanged>(h.PositionEvents.Messages[^1]);
        Assert.Equal(Quantity.Parse("4.000"), changed.Quantity);
        Assert.Equal(50_750m, changed.AvgPxOpen);
        // Marked at the last fill (51,000): (51,000 - 50,750) * 4 = 1,000
        Assert.Equal(Money.Parse("1000 USDT"), changed.UnrealizedPnl);
    }

    [Fact]
    public void Netting_partial_close_realizes_pnl_net_of_both_commissions()
    {
        ExecHarness h = new();
        h.FillMarket("O-1", OrderSide.Buy, "1.000", "50000.00", commission: "5");

        h.FillMarket("O-2", OrderSide.Sell, "0.500", "52000.00", commission: "2");

        // (52,000 - 50,000) * 0.5 = 1,000 gross; minus 5 (open) and 2 (close) = 993.
        Position position = Assert.Single(h.Cache.PositionsOpen());
        Assert.Equal(Quantity.Parse("0.500"), position.Quantity);
        Assert.Equal(Money.Parse("993 USDT"), position.RealizedPnl);
        PositionChanged changed = Assert.IsType<PositionChanged>(h.PositionEvents.Messages[^1]);
        Assert.Equal(Money.Parse("993 USDT"), changed.RealizedPnl);
        Assert.Equal(52_000m, changed.AvgPxClose);
        // Remaining 0.5 marked at 52,000: (52,000 - 50,000) * 0.5 = 1,000
        Assert.Equal(Money.Parse("1000 USDT"), changed.UnrealizedPnl);
    }

    [Fact]
    public void Netting_full_close_emits_position_closed_and_moves_the_position_to_the_closed_index()
    {
        ExecHarness h = new();
        h.Clock.SetTime(TestOrders.T0);
        h.FillMarket("O-1", OrderSide.Buy, "1.000", "50000.00");

        MarketOrder closing = h.FillMarket("O-2", OrderSide.Sell, "1.000", "52000.00");

        Assert.Empty(h.Cache.PositionsOpen());
        Position position = Assert.Single(h.Cache.PositionsClosed());
        Assert.True(h.Cache.IsPositionClosed(FirstNettingId));
        Assert.Equal(PositionSide.Flat, position.Side);
        PositionClosed closed = Assert.IsType<PositionClosed>(h.PositionEvents.Messages[^1]);
        Assert.Equal(Money.Parse("2000 USDT"), closed.RealizedPnl);
        Assert.Equal(closing.ClientOrderId, closed.ClosingOrderId);
        Assert.Equal(50_000m, closed.AvgPxOpen);
        Assert.Equal(52_000m, closed.AvgPxClose);
        Assert.Equal(Quantity.Parse("1.000"), closed.PeakQuantity);
        Assert.Equal(["PositionOpened", "PositionClosed"], h.PositionEvents.TypeNames);
    }

    [Fact]
    public void Netting_short_position_profits_when_bought_back_lower()
    {
        ExecHarness h = new();
        h.FillMarket("O-1", OrderSide.Sell, "2.000", "50000.00");

        h.FillMarket("O-2", OrderSide.Buy, "2.000", "49000.00");

        // (50,000 - 49,000) * 2 = 2,000
        Assert.Equal(Money.Parse("2000 USDT"), Assert.Single(h.Cache.PositionsClosed()).RealizedPnl);
    }

    [Fact]
    public void Netting_position_opened_after_going_flat_gets_a_numbered_id()
    {
        ExecHarness h = new();
        h.FillMarket("O-1", OrderSide.Buy, "1.000", "50000.00");
        h.FillMarket("O-2", OrderSide.Sell, "1.000", "50000.00");

        MarketOrder reopening = h.FillMarket("O-3", OrderSide.Buy, "1.000", "50500.00");

        Assert.Equal(SecondNettingId, Assert.Single(h.Cache.PositionsOpen()).Id);
        Assert.Equal(SecondNettingId, reopening.PositionId);
        Assert.Equal(2, h.Cache.PositionsTotalCount());
    }

    [Fact]
    public void Netting_fill_larger_than_the_position_closes_it_and_opens_the_opposite_side_under_a_new_id()
    {
        ExecHarness h = new();
        h.FillMarket("O-1", OrderSide.Buy, "1.000", "50000.00");
        h.OrderEvents.Messages.Clear();
        h.PositionEvents.Messages.Clear();
        MarketOrder flipping = h.SubmitAndAccept(TestOrders.Market("O-2", TestIds.BtcUsdt, OrderSide.Sell, "1.500"));
        h.OrderEvents.Messages.Clear();

        h.Engine.Process(TestEvents.Filled(flipping, "T-2", "1.500", "52000.00", commission: "3"));

        // The 1.500 fill is split 1.000 (close) + 0.500 (open); the 3 USDT commission pro rata 2 + 1.
        IReadOnlyList<OrderFilled> fills = h.OrderEvents.Of<OrderFilled>();
        Assert.Equal(2, fills.Count);
        Assert.Equal((Quantity.Parse("1.000"), Money.Parse("2 USDT"), FirstNettingId), (fills[0].LastQty, fills[0].Commission, fills[0].PositionId!.Value));
        Assert.Equal((Quantity.Parse("0.500"), Money.Parse("1 USDT"), SecondNettingId), (fills[1].LastQty, fills[1].Commission, fills[1].PositionId!.Value));

        Assert.Equal(["PositionClosed", "PositionOpened"], h.PositionEvents.TypeNames);
        Position closed = Assert.Single(h.Cache.PositionsClosed());
        // (52,000 - 50,000) * 1 - 2 commission = 1,998
        Assert.Equal(Money.Parse("1998 USDT"), closed.RealizedPnl);
        Position opened = Assert.Single(h.Cache.PositionsOpen());
        Assert.Equal(SecondNettingId, opened.Id);
        Assert.Equal(PositionSide.Short, opened.Side);
        Assert.Equal(Quantity.Parse("0.500"), opened.Quantity);
        Assert.Equal(52_000m, opened.AvgPxOpen);
        Assert.Equal(Money.Parse("-1 USDT"), opened.RealizedPnl);

        Assert.Equal(OrderStatus.Filled, flipping.Status);
        Assert.Equal(Quantity.Parse("1.500"), flipping.FilledQuantity);
        Assert.Equal(SecondNettingId, flipping.PositionId);
    }

    [Fact(Skip = "BUG: ExecutionEngine.cs:429,433 renames the split fills to '<id>-C'/'<id>-O', so neither Order nor Position recognises a redelivery of the original TradeId and the new position is double counted")]
    public void Netting_redelivered_flipping_fill_is_ignored_like_any_duplicate()
    {
        ExecHarness h = new();
        h.FillMarket("O-1", OrderSide.Buy, "1.000", "50000.00");
        MarketOrder flipping = h.SubmitAndAccept(TestOrders.Market("O-2", TestIds.BtcUsdt, OrderSide.Sell, "1.500"));
        OrderFilled fill = TestEvents.Filled(flipping, "T-2", "1.500", "52000.00");
        h.Engine.Process(fill);

        h.Engine.Process(fill with { EventId = Guid.NewGuid() });

        // Still short 0.500 - the venue traded 1.500 once.
        Assert.Equal(-0.500m, Assert.Single(h.Cache.PositionsOpen()).SignedQuantity);
    }

    [Fact]
    public void Netting_keeps_separate_positions_per_strategy_and_per_instrument()
    {
        ExecHarness h = new();

        h.FillMarket("O-1", OrderSide.Buy, "1.000", "50000.00");
        h.FillMarket("O-2", OrderSide.Sell, "1.000", "50000.00", strategyId: TestIds.OtherStrategy);
        h.FillMarket("O-3", OrderSide.Buy, "2.000", "3000.00", instrumentId: TestIds.EthUsdt);

        Assert.Equal(
            ["BTCUSDT.BINANCE-S-001", "BTCUSDT.BINANCE-S-002", "ETHUSDT.BINANCE-S-001"],
            h.Cache.PositionsOpen().Select(p => p.Id.Value).Order(StringComparer.Ordinal));
        Assert.Equal(PositionSide.Short, h.Cache.Position(new PositionId("BTCUSDT.BINANCE-S-002"))!.Side);
    }

    [Fact]
    public void Netting_partial_fills_of_one_order_accumulate_in_one_position()
    {
        ExecHarness h = new();
        LimitOrder order = h.SubmitAndAccept(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));

        h.Engine.Process(TestEvents.Filled(order, "T-1", "0.250", "50000.00"));
        h.Engine.Process(TestEvents.Filled(order, "T-2", "0.750", "49990.00"));

        // (0.25 * 50,000 + 0.75 * 49,990) / 1 = 49,992.5
        Position position = Assert.Single(h.Cache.Positions());
        Assert.Equal(49_992.5m, position.AvgPxOpen);
        Assert.Equal(49_992.5m, order.AvgPx);
        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal(["PositionOpened", "PositionChanged"], h.PositionEvents.TypeNames);
    }

    [Fact]
    public void Hedging_opens_one_position_per_order_with_generated_ids()
    {
        ExecHarness h = new(clientOms: OmsType.Hedging);

        h.FillMarket("O-1", OrderSide.Buy, "1.000", "50000.00");
        h.FillMarket("O-2", OrderSide.Buy, "1.000", "50100.00");
        h.FillMarket("O-3", OrderSide.Sell, "0.500", "50200.00");

        // Clock is fixed at 2024-01-01T00:00:00Z.
        Assert.Equal(
            ["P-20240101-000000-001", "P-20240101-000000-002", "P-20240101-000000-003"],
            h.Cache.PositionsOpen().Select(p => p.Id.Value).Order(StringComparer.Ordinal));
        Assert.Equal(3, h.Engine.PositionIdCount);
        Assert.Equal(1.5m, h.Portfolio.NetPosition(TestIds.BtcUsdt));
    }

    [Fact]
    public void Hedging_uses_the_position_id_supplied_by_the_venue()
    {
        ExecHarness h = new(clientOms: OmsType.Hedging);
        MarketOrder first = h.SubmitAndAccept(TestOrders.Market("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000"));
        MarketOrder second = h.SubmitAndAccept(TestOrders.Market("O-2", TestIds.BtcUsdt, OrderSide.Sell, "1.000"));

        h.Engine.Process(TestEvents.Filled(first, "T-1", "1.000", "50000.00", positionId: new PositionId("VENUE-P-9")));
        h.Engine.Process(TestEvents.Filled(second, "T-2", "1.000", "50500.00", positionId: new PositionId("VENUE-P-9")));

        Position position = Assert.Single(h.Cache.Positions());
        Assert.Equal(new PositionId("VENUE-P-9"), position.Id);
        Assert.True(position.IsClosed);
        Assert.Equal(Money.Parse("500 USDT"), position.RealizedPnl);
    }

    [Fact]
    public void Hedging_partial_fills_of_one_order_stay_in_the_same_position()
    {
        ExecHarness h = new(clientOms: OmsType.Hedging);
        LimitOrder order = h.SubmitAndAccept(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));

        h.Engine.Process(TestEvents.Filled(order, "T-1", "0.400", "50000.00"));
        h.Engine.Process(TestEvents.Filled(order, "T-2", "0.600", "50000.00"));

        Assert.Equal(Quantity.Parse("1.000"), Assert.Single(h.Cache.Positions()).Quantity);
    }

    [Fact(Skip = "BUG: ExecutionEngine.cs:504-519 ResolvePositionId ignores SubmitOrder.PositionId (only indexed in the cache), so under hedging Strategy.ClosePosition opens a new opposite position instead of closing the target")]
    public void Hedging_order_submitted_against_a_position_id_closes_that_position()
    {
        ExecHarness h = new(clientOms: OmsType.Hedging);
        h.FillMarket("O-1", OrderSide.Buy, "1.000", "50000.00");
        PositionId target = Assert.Single(h.Cache.PositionsOpen()).Id;

        h.FillMarket("O-2", OrderSide.Sell, "1.000", "51000.00", commandPositionId: target);

        Assert.Empty(h.Cache.PositionsOpen());
        Assert.Equal(Money.Parse("1000 USDT"), h.Cache.Position(target)!.RealizedPnl);
    }

    [Fact]
    public void Strategy_oms_type_overrides_the_client_oms_type()
    {
        ExecHarness h = new(clientOms: OmsType.Netting);
        h.Engine.RegisterOmsType(TestIds.Strategy, OmsType.Hedging);

        h.FillMarket("O-1", OrderSide.Buy, "1.000", "50000.00");
        h.FillMarket("O-2", OrderSide.Buy, "1.000", "50000.00");

        Assert.Equal(2, h.Cache.PositionsOpenCount());
    }

    [Fact]
    public void Unspecified_strategy_oms_type_falls_back_to_the_client_oms_type()
    {
        ExecHarness h = new(clientOms: OmsType.Hedging);
        h.Engine.RegisterOmsType(TestIds.Strategy, OmsType.Unspecified);

        h.FillMarket("O-1", OrderSide.Buy, "1.000", "50000.00");
        h.FillMarket("O-2", OrderSide.Buy, "1.000", "50000.00");

        Assert.Equal(2, h.Cache.PositionsOpenCount());
    }

    [Fact]
    public void Netting_is_the_default_when_neither_strategy_nor_client_specify_an_oms_type()
    {
        ExecHarness h = new(clientOms: OmsType.Unspecified);

        h.FillMarket("O-1", OrderSide.Buy, "1.000", "50000.00");
        h.FillMarket("O-2", OrderSide.Buy, "1.000", "50000.00");

        Assert.Equal(Quantity.Parse("2.000"), Assert.Single(h.Cache.PositionsOpen()).Quantity);
    }

    [Fact]
    public void Reset_restarts_generated_position_ids()
    {
        ExecHarness h = new(clientOms: OmsType.Hedging);
        h.FillMarket("O-1", OrderSide.Buy, "1.000", "50000.00");

        h.Engine.Reset();

        Assert.Equal(0, h.Engine.PositionIdCount);
    }
}
