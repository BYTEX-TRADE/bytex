using Bytex.Core.Model;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using static Bytex.Core.Tests.Model.ModelFixtures;

namespace Bytex.Core.Tests.Model;

// Fill accounting on the order: filled / leaves quantities, volume-weighted average price, slippage,
// commissions per currency, duplicate suppression and overfills. All expected numbers are worked by hand.
public class OrderFillTests
{
    private static LimitOrder AcceptedLimit(OrderSide side = OrderSide.Buy, string quantity = "10.000000", string price = "100.00")
    {
        LimitOrder order = LimitOrder.Create(BtcParams(side, quantity), Price.Parse(price));
        order.Apply(Submitted(order));
        order.Apply(Accepted(order));
        return order;
    }

    [Fact]
    public void A_partial_fill_reduces_leaves_and_keeps_the_order_open()
    {
        LimitOrder order = AcceptedLimit();

        order.Apply(Fill(order, "4.000000", "100.00", "T-1"));

        Assert.Equal(OrderStatus.PartiallyFilled, order.Status);
        Assert.Equal(Quantity.Parse("4.000000"), order.FilledQuantity);
        Assert.Equal(Quantity.Parse("6.000000"), order.LeavesQuantity);
        Assert.Equal(100.00m, order.AvgPx);
        Assert.True(order.IsOpen);
        Assert.Null(order.TsClosed);
    }

    [Fact]
    public void The_fill_that_completes_the_quantity_closes_the_order()
    {
        LimitOrder order = AcceptedLimit();

        order.Apply(Fill(order, "4.000000", "100.00", "T-1", t: 6));
        order.Apply(Fill(order, "6.000000", "99.00", "T-2", t: 8));

        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal(Quantity.Parse("10.000000"), order.FilledQuantity);
        Assert.Equal(Quantity.Parse("0.000000"), order.LeavesQuantity);
        Assert.True(order.IsClosed);
        Assert.Equal(At(8), order.TsClosed);
        // (4 * 100.00 + 6 * 99.00) / 10 = (400 + 594) / 10 = 99.4
        Assert.Equal(99.4m, order.AvgPx);
    }

    [Fact]
    public void Average_price_is_weighted_by_fill_size_across_many_fills()
    {
        LimitOrder order = AcceptedLimit(quantity: "4.000000", price: "105.00");

        order.Apply(Fill(order, "1.000000", "100.00", "T-1"));
        order.Apply(Fill(order, "2.000000", "101.00", "T-2"));
        order.Apply(Fill(order, "1.000000", "103.00", "T-3"));

        // (100 + 202 + 103) / 4 = 405 / 4 = 101.25
        Assert.Equal(101.25m, order.AvgPx);
    }

    [Fact]
    public void Tiny_fills_accumulate_without_drift()
    {
        LimitOrder order = AcceptedLimit(quantity: "1.000000");

        for (int i = 1; i <= 10; i++)
        {
            order.Apply(Fill(order, "0.100000", "100.10", "T-" + i));
        }

        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal(Quantity.Parse("1.000000"), order.FilledQuantity);
        Assert.True(order.LeavesQuantity.IsZero);
        Assert.Equal(100.10m, order.AvgPx);
    }

    [Fact]
    public void Filled_quantity_is_kept_at_the_order_precision()
    {
        LimitOrder order = AcceptedLimit(quantity: "10.000");

        order.Apply(Fill(order, "4.5", "100.00", "T-1"));

        Assert.Equal("4.500", order.FilledQuantity.ToString());
        Assert.Equal("5.500", order.LeavesQuantity.ToString());
    }

    [Fact]
    public void A_repeated_trade_id_is_ignored()
    {
        LimitOrder order = AcceptedLimit();
        order.Apply(Fill(order, "4.000000", "100.00", "T-1"));
        int eventsBefore = order.EventCount;

        order.Apply(Fill(order, "4.000000", "100.00", "T-1", t: 7));

        Assert.Equal(Quantity.Parse("4.000000"), order.FilledQuantity);
        Assert.Equal(eventsBefore, order.EventCount);
        Assert.Equal([new TradeId("T-1")], order.TradeIds);
        Assert.Equal(OrderStatus.PartiallyFilled, order.Status);
    }

    [Fact]
    public void Trade_ids_are_recorded_in_fill_order()
    {
        LimitOrder order = AcceptedLimit();

        order.Apply(Fill(order, "1.000000", "100.00", "T-9"));
        order.Apply(Fill(order, "1.000000", "100.00", "T-3"));

        Assert.Equal([new TradeId("T-9"), new TradeId("T-3")], order.TradeIds);
        Assert.Equal(new TradeId("T-3"), order.LastTradeId);
    }

    [Fact]
    public void Commissions_accumulate_per_currency()
    {
        LimitOrder order = AcceptedLimit();

        order.Apply(Fill(order, "2.000000", "100.00", "T-1", commission: Usdt("0.10")));
        order.Apply(Fill(order, "2.000000", "100.00", "T-2", commission: Usdt("0.15")));
        order.Apply(Fill(order, "2.000000", "100.00", "T-3", commission: new Money(0.0002m, Currencies.BNB)));

        Assert.Equal(2, order.Commissions.Count);
        Assert.Equal(Usdt("0.25"), order.Commissions[Currencies.USDT]);
        Assert.Equal(new Money(0.0002m, Currencies.BNB), order.Commissions[Currencies.BNB]);
    }

    [Theory]
    [InlineData(OrderSide.Buy, "100.00", "100.05", "0.05")] // paid more than the limit
    [InlineData(OrderSide.Buy, "100.00", "99.90", "-0.10")] // price improvement
    [InlineData(OrderSide.Sell, "100.00", "99.90", "0.10")] // received less than the limit
    [InlineData(OrderSide.Sell, "100.00", "100.10", "-0.10")]
    [InlineData(OrderSide.Buy, "100.00", "100.00", "0")]
    public void Slippage_is_positive_when_the_average_price_is_worse_than_the_limit(OrderSide side, string limit, string fillPrice, string expected)
    {
        LimitOrder order = AcceptedLimit(side, price: limit);

        order.Apply(Fill(order, "10.000000", fillPrice, "T-1"));

        Assert.Equal(D(expected), order.Slippage);
    }

    [Fact]
    public void Slippage_of_a_stop_order_is_measured_from_its_trigger()
    {
        StopMarketOrder stop = StopMarketOrder.Create(BtcParams(OrderSide.Sell), Price.Parse("95.00"));
        stop.Apply(Submitted(stop));
        stop.Apply(Accepted(stop));

        stop.Apply(Fill(stop, "10.000000", "94.40", "T-1"));

        Assert.Equal(0.60m, stop.Slippage); // sold 0.60 below the trigger
    }

    [Fact]
    public void A_market_order_has_no_reference_price_so_no_slippage()
    {
        MarketOrder order = MarketOrder.Create(BtcParams());
        order.Apply(Submitted(order));

        order.Apply(Fill(order, "10.000000", "100.00", "T-1"));

        Assert.Equal(0m, order.Slippage);
        Assert.Equal(100.00m, order.AvgPx);
    }

    [Fact]
    public void An_overfill_closes_the_order_and_never_makes_leaves_negative()
    {
        LimitOrder order = AcceptedLimit();

        order.Apply(Fill(order, "12.000000", "100.00", "T-1"));

        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal(Quantity.Parse("12.000000"), order.FilledQuantity); // what the venue actually executed
        Assert.Equal(Quantity.Parse("0.000000"), order.LeavesQuantity);
        Assert.Equal(Quantity.Parse("10.000000"), order.Quantity);
    }

    [Fact]
    public void The_position_id_of_the_first_fill_sticks()
    {
        LimitOrder order = AcceptedLimit();

        order.Apply(Fill(order, "1.000000", "100.00", "T-1", positionId: "P-A"));
        order.Apply(Fill(order, "1.000000", "100.00", "T-2", positionId: "P-B"));

        Assert.Equal(new PositionId("P-A"), order.PositionId);
    }

    [Fact]
    public void Shrinking_the_order_to_the_filled_quantity_leaves_nothing_open()
    {
        LimitOrder order = AcceptedLimit();
        order.Apply(Fill(order, "4.000000", "100.00", "T-1"));

        order.Apply(Updated(order, "4.000000"));

        Assert.Equal(Quantity.Parse("4.000000"), order.Quantity);
        Assert.True(order.LeavesQuantity.IsZero);
    }

    [Fact]
    public void Replaying_the_event_history_rebuilds_the_same_order()
    {
        LimitOrder original = AcceptedLimit();
        original.Apply(Fill(original, "4.000000", "100.00", "T-1", commission: Usdt("0.20"), t: 6));
        original.Apply(PendingUpdate(original, t: 7));
        original.Apply(Updated(original, "8.000000", price: "99.50", t: 8));
        original.Apply(Fill(original, "4.000000", "99.00", "T-2", commission: Usdt("0.20"), t: 9));

        Order rebuilt = OrderUnpacker.FromEvents(original.Events);

        Assert.IsType<LimitOrder>(rebuilt);
        Assert.Equal(OrderStatus.Filled, rebuilt.Status);
        Assert.Equal(original.Quantity, rebuilt.Quantity);
        Assert.Equal(original.FilledQuantity, rebuilt.FilledQuantity);
        Assert.Equal(original.Price, rebuilt.Price);
        Assert.Equal(99.5m, rebuilt.AvgPx); // (4 * 100 + 4 * 99) / 8
        Assert.Equal(Usdt("0.40"), rebuilt.Commissions[Currencies.USDT]);
        Assert.Equal(original.VenueOrderId, rebuilt.VenueOrderId);
        Assert.Equal(original.TsClosed, rebuilt.TsClosed);
        Assert.Equal(original.EventCount, rebuilt.EventCount);
    }

    [Fact]
    public void Replay_requires_the_history_to_start_with_the_initialising_event()
    {
        LimitOrder order = AcceptedLimit();

        Assert.Throws<ArgumentException>(() => OrderUnpacker.FromEvents([]));
        Assert.Throws<ArgumentException>(() => OrderUnpacker.FromEvents(order.Events.Skip(1).ToList()));
        Assert.Throws<ArgumentNullException>(() => OrderUnpacker.FromEvents(null!));
    }

    [Fact]
    public void Replay_of_an_impossible_history_is_rejected()
    {
        LimitOrder order = AcceptedLimit();
        order.Apply(Canceled(order));

        List<OrderEvent> tampered = [.. order.Events, Accepted(order, t: 20)];

        Assert.Throws<InvalidOrderTransitionException>(() => OrderUnpacker.FromEvents(tampered));
    }

    public static TheoryData<OrderType> OrderTypes => new(Enum.GetValues<OrderType>());

    [Theory]
    [MemberData(nameof(OrderTypes))]
    public void Every_order_type_is_rebuilt_from_its_initialising_event_with_identical_parameters(OrderType type)
    {
        OrderParams p = BtcParams(OrderSide.Sell, "3.000000") with { TimeInForce = type == OrderType.Market ? TimeInForce.Ioc : TimeInForce.Gtd, ReduceOnly = true };
        Price limit = Price.Parse("50000.10");
        Price trigger = Price.Parse("49000.00");
        UnixNanos? expiry = type == OrderType.Market ? null : At(3600);
        Order original = type switch
        {
            OrderType.Market => MarketOrder.Create(p),
            OrderType.Limit => LimitOrder.Create(p, limit, expiry, Quantity.Parse("1.000000")),
            OrderType.StopMarket => StopMarketOrder.Create(p, trigger, TriggerType.MarkPrice, expiry),
            OrderType.StopLimit => StopLimitOrder.Create(p, limit, trigger, TriggerType.LastPrice, expiry),
            OrderType.MarketIfTouched => MarketIfTouchedOrder.Create(p, trigger, TriggerType.BidAsk, expiry),
            OrderType.LimitIfTouched => LimitIfTouchedOrder.Create(p, limit, trigger, TriggerType.IndexPrice, expiry),
            OrderType.TrailingStopMarket => TrailingStopMarketOrder.Create(p, 25.5m, TrailingOffsetType.Ticks, trigger, limit, TriggerType.MarkPrice, expiry),
            OrderType.TrailingStopLimit => TrailingStopLimitOrder.Create(p, 25.5m, 1.25m, TrailingOffsetType.BasisPoints, limit, trigger, null, TriggerType.MarkPrice, expiry),
            OrderType.MarketToLimit => MarketToLimitOrder.Create(p, expiry),
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };

        Order rebuilt = OrderUnpacker.FromInitialized(original.InitEvent);

        Assert.IsType(original.GetType(), rebuilt);
        Assert.Equal(type, rebuilt.Type);
        Assert.Equal(original.Price, rebuilt.Price);
        Assert.Equal(original.TriggerPrice, rebuilt.TriggerPrice);
        Assert.Equal(original.TriggerType, rebuilt.TriggerType);
        Assert.Equal(original.ExpireTime, rebuilt.ExpireTime);
        Assert.Equal(original.DisplayQuantity, rebuilt.DisplayQuantity);
        Assert.Equal(original.TimeInForce, rebuilt.TimeInForce);
        Assert.True(rebuilt.IsReduceOnly);
        Assert.Equal(Quantity.Parse("3.000000"), rebuilt.Quantity);
        if (rebuilt is TrailingStopMarketOrder tsm)
        {
            Assert.Equal(25.5m, tsm.TrailingOffset);
            Assert.Equal(TrailingOffsetType.Ticks, tsm.TrailingOffsetType);
            Assert.Equal(limit, tsm.ActivationPrice);
        }

        if (rebuilt is TrailingStopLimitOrder tsl)
        {
            Assert.Equal(25.5m, tsl.TrailingOffset);
            Assert.Equal(1.25m, tsl.LimitOffset);
            Assert.Equal(TrailingOffsetType.BasisPoints, tsl.TrailingOffsetType);
        }
    }

    [Fact]
    public void An_unknown_order_type_cannot_be_rebuilt()
    {
        OrderInitialized init = MarketOrder.Create(BtcParams()).InitEvent with { OrderType = (OrderType)99 };

        Assert.Throws<ArgumentOutOfRangeException>(() => OrderUnpacker.FromInitialized(init));
    }
}
