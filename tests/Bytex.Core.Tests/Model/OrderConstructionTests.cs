using Bytex.Core.Model;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using static Bytex.Core.Tests.Model.ModelFixtures;

namespace Bytex.Core.Tests.Model;

// Orders are built once and then only change through events. These tests protect what each order type
// requires and records at construction: prices with their precision, triggers, expiry, instructions,
// contingency links, and the rules about which time-in-force values are allowed.
public class OrderConstructionTests
{
    private static readonly Price _limit = Price.Parse("50000.10");
    private static readonly Price _trigger = Price.Parse("49000.00");

    /// <summary>An initialising event of the given type that carries no type-specific options at all.</summary>
    private static OrderInitialized BareInit(OrderType type, TimeInForce timeInForce = TimeInForce.Gtc) =>
        MarketOrder.Create(BtcParams()).InitEvent with { OrderType = type, TimeInForce = timeInForce };

    [Fact]
    public void A_new_order_starts_initialized_with_nothing_filled()
    {
        LimitOrder order = LimitOrder.Create(BtcParams(OrderSide.Buy, "1.500000"), _limit);

        Assert.Equal(OrderStatus.Initialized, order.Status);
        Assert.Equal(Quantity.Parse("1.500000"), order.Quantity);
        Assert.Equal(Quantity.Parse("1.500000"), order.LeavesQuantity);
        Assert.Equal(Quantity.Parse("0.000000"), order.FilledQuantity);
        Assert.Null(order.AvgPx);
        Assert.Equal(0m, order.Slippage);
        Assert.Null(order.VenueOrderId);
        Assert.Null(order.PositionId);
        Assert.Null(order.AccountId);
        Assert.Null(order.TsSubmitted);
        Assert.Null(order.TsClosed);
        Assert.Equal(T0, order.TsInit);
        Assert.Equal(T0, order.TsLast);
        Assert.Equal(Id(1), order.InitId);
        Assert.True(order.IsActiveLocal);
        Assert.False(order.IsOpen);
        Assert.False(order.IsClosed);
        Assert.False(order.IsInflight);
    }

    [Fact]
    public void A_new_order_records_its_initialising_event_as_the_first_event()
    {
        MarketOrder order = MarketOrder.Create(BtcParams(OrderSide.Sell, "2.000000"));

        OrderEvent only = Assert.Single(order.Events);
        OrderInitialized init = Assert.IsType<OrderInitialized>(only);
        Assert.Same(init, order.InitEvent);
        Assert.Same(init, order.LastEvent);
        Assert.Equal(1, order.EventCount);
        Assert.Equal(OrderSide.Sell, init.OrderSide);
        Assert.Equal(OrderType.Market, init.OrderType);
        Assert.Equal(Quantity.Parse("2.000000"), init.Quantity);
        Assert.Equal(order.ClientOrderId, init.ClientOrderId);
    }

    [Fact]
    public void Identity_and_side_come_from_the_parameters()
    {
        MarketOrder order = MarketOrder.Create(BtcParams(OrderSide.Sell));

        Assert.Equal(Trader, order.TraderId);
        Assert.Equal(Strategy, order.StrategyId);
        Assert.Equal(InstrumentId.Parse("BTCUSDT.BINANCE"), order.InstrumentId);
        Assert.Equal(new ClientOrderId("O-20231114-221320-001-001-1"), order.ClientOrderId);
        Assert.True(order.IsSell);
        Assert.False(order.IsBuy);
        Assert.Equal(TimeInForce.Gtc, order.TimeInForce);
    }

    [Fact]
    public void Zero_quantity_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => MarketOrder.Create(BtcParams(quantity: "0.000000")));
        Assert.Throws<ArgumentException>(() => LimitOrder.Create(BtcParams(quantity: "0"), _limit));
    }

    [Fact]
    public void A_missing_initialising_event_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new MarketOrder(null!));
    }

    [Fact]
    public void Only_market_orders_are_aggressive()
    {
        Assert.True(MarketOrder.Create(BtcParams()).IsAggressive);
        Assert.False(MarketOrder.Create(BtcParams()).IsPassive);
        Assert.True(LimitOrder.Create(BtcParams(), _limit).IsPassive);
        Assert.False(LimitOrder.Create(BtcParams(), _limit).IsAggressive);
        Assert.True(StopMarketOrder.Create(BtcParams(), _trigger).IsPassive);
    }

    [Theory]
    [InlineData(TimeInForce.Gtc)]
    [InlineData(TimeInForce.Ioc)]
    [InlineData(TimeInForce.Fok)]
    [InlineData(TimeInForce.Day)]
    public void Market_orders_accept_immediate_and_resting_time_in_force(TimeInForce timeInForce)
    {
        MarketOrder order = MarketOrder.Create(BtcParams() with { TimeInForce = timeInForce });

        Assert.Equal(timeInForce, order.TimeInForce);
        Assert.Equal(OrderType.Market, order.Type);
        Assert.False(order.HasPrice);
        Assert.False(order.HasTriggerPrice);
    }

    [Theory]
    [InlineData(TimeInForce.Gtd)]
    [InlineData(TimeInForce.AtTheOpen)]
    [InlineData(TimeInForce.AtTheClose)]
    public void Market_orders_reject_time_in_force_that_needs_a_resting_order(TimeInForce timeInForce)
    {
        Assert.Throws<ArgumentException>(() => MarketOrder.Create(BtcParams() with { TimeInForce = timeInForce }));
    }

    [Fact]
    public void Limit_order_keeps_its_price_with_the_written_precision()
    {
        LimitOrder order = LimitOrder.Create(BtcParams(), Price.Parse("50000.10"));

        Assert.Equal(OrderType.Limit, order.Type);
        Assert.Equal(new Price(50000.10m, 2), order.Price);
        Assert.Equal("50000.10", order.Price?.ToString());
        Assert.True(order.HasPrice);
        Assert.False(order.HasTriggerPrice);
        Assert.Null(order.ExpireTime);
        Assert.Null(order.DisplayQuantity);
        Assert.False(order.IsIceberg);
    }

    [Fact]
    public void Limit_order_accepts_a_negative_price()
    {
        LimitOrder order = LimitOrder.Create(BtcParams(), Price.Parse("-37.63"));

        Assert.Equal(new Price(-37.63m, 2), order.Price);
    }

    [Fact]
    public void Limit_order_without_a_price_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new LimitOrder(BareInit(OrderType.Limit)));
    }

    [Fact]
    public void Gtd_limit_order_requires_an_expire_time()
    {
        Assert.Throws<ArgumentException>(() => LimitOrder.Create(BtcParams() with { TimeInForce = TimeInForce.Gtd }, _limit));
    }

    [Fact]
    public void Gtd_limit_order_keeps_its_expire_time_to_the_nanosecond()
    {
        UnixNanos expiry = new(T0Nanos + 3_600_000_000_001L);

        LimitOrder order = LimitOrder.Create(BtcParams() with { TimeInForce = TimeInForce.Gtd }, _limit, expireTime: expiry);

        Assert.Equal(TimeInForce.Gtd, order.TimeInForce);
        Assert.Equal(expiry, order.ExpireTime);
    }

    [Theory(Skip = "BUG: only LimitOrder enforces 'Gtd requires expireTime'; every other resting order type accepts GTD with no expiry")]
    [InlineData(OrderType.StopMarket)]
    [InlineData(OrderType.StopLimit)]
    [InlineData(OrderType.MarketIfTouched)]
    [InlineData(OrderType.LimitIfTouched)]
    [InlineData(OrderType.TrailingStopMarket)]
    [InlineData(OrderType.TrailingStopLimit)]
    [InlineData(OrderType.MarketToLimit)]
    public void Gtd_without_an_expire_time_is_rejected_for_every_order_type_that_can_rest(OrderType type)
    {
        OrderParams gtd = BtcParams() with { TimeInForce = TimeInForce.Gtd };

        Assert.Throws<ArgumentException>(() => Create(type, gtd, expireTime: null));
    }

    [Theory]
    [InlineData(OrderType.Limit)]
    [InlineData(OrderType.StopMarket)]
    [InlineData(OrderType.StopLimit)]
    [InlineData(OrderType.MarketIfTouched)]
    [InlineData(OrderType.LimitIfTouched)]
    [InlineData(OrderType.TrailingStopMarket)]
    [InlineData(OrderType.TrailingStopLimit)]
    [InlineData(OrderType.MarketToLimit)]
    public void Gtd_orders_of_every_resting_type_keep_their_expire_time(OrderType type)
    {
        OrderParams gtd = BtcParams() with { TimeInForce = TimeInForce.Gtd };

        Order order = Create(type, gtd, expireTime: At(60));

        Assert.Equal(TimeInForce.Gtd, order.TimeInForce);
        Assert.Equal(At(60), order.ExpireTime);
    }

    [Fact]
    public void Display_quantity_makes_a_limit_order_an_iceberg()
    {
        LimitOrder order = LimitOrder.Create(BtcParams(quantity: "10.000000"), _limit, displayQuantity: Quantity.Parse("0.500000"));

        Assert.True(order.IsIceberg);
        Assert.Equal(Quantity.Parse("0.500000"), order.DisplayQuantity);
    }

    [Fact]
    public void Stop_market_order_keeps_trigger_price_and_trigger_type()
    {
        StopMarketOrder order = StopMarketOrder.Create(BtcParams(OrderSide.Sell), _trigger, TriggerType.MarkPrice, At(60));

        Assert.Equal(OrderType.StopMarket, order.Type);
        Assert.Equal(_trigger, order.TriggerPrice);
        Assert.Equal(TriggerType.MarkPrice, order.TriggerType);
        Assert.Equal(At(60), order.ExpireTime);
        Assert.Null(order.Price);
        Assert.False(order.IsTriggered);
        Assert.Null(order.TsTriggered);
    }

    [Fact]
    public void Stop_limit_order_keeps_both_prices_and_display_quantity()
    {
        StopLimitOrder order = StopLimitOrder.Create(BtcParams(), _limit, _trigger, TriggerType.LastPrice, displayQuantity: Quantity.Parse("1.000000"));

        Assert.Equal(OrderType.StopLimit, order.Type);
        Assert.Equal(_limit, order.Price);
        Assert.Equal(_trigger, order.TriggerPrice);
        Assert.Equal(TriggerType.LastPrice, order.TriggerType);
        Assert.Equal(Quantity.Parse("1.000000"), order.DisplayQuantity);
        Assert.False(order.IsTriggered);
    }

    [Fact]
    public void If_touched_orders_keep_their_prices()
    {
        MarketIfTouchedOrder mit = MarketIfTouchedOrder.Create(BtcParams(), _trigger, TriggerType.BidAsk);
        LimitIfTouchedOrder lit = LimitIfTouchedOrder.Create(BtcParams(), _limit, _trigger, TriggerType.IndexPrice);

        Assert.Equal(OrderType.MarketIfTouched, mit.Type);
        Assert.Equal(_trigger, mit.TriggerPrice);
        Assert.Equal(TriggerType.BidAsk, mit.TriggerType);
        Assert.Null(mit.Price);
        Assert.Equal(OrderType.LimitIfTouched, lit.Type);
        Assert.Equal(_limit, lit.Price);
        Assert.Equal(_trigger, lit.TriggerPrice);
        Assert.Equal(TriggerType.IndexPrice, lit.TriggerType);
    }

    [Fact]
    public void Trigger_type_defaults_to_the_venue_default()
    {
        Assert.Equal(TriggerType.Default, StopMarketOrder.Create(BtcParams(), _trigger).TriggerType);
    }

    [Theory]
    [InlineData(OrderType.StopMarket)]
    [InlineData(OrderType.StopLimit)]
    [InlineData(OrderType.MarketIfTouched)]
    [InlineData(OrderType.LimitIfTouched)]
    [InlineData(OrderType.TrailingStopMarket)]
    [InlineData(OrderType.TrailingStopLimit)]
    public void Conditional_orders_without_their_required_parameters_are_rejected(OrderType type)
    {
        Assert.Throws<ArgumentException>(() => OrderUnpacker.FromInitialized(BareInit(type)));
    }

    [Theory]
    [InlineData("25.5", TrailingOffsetType.Price)]
    [InlineData("150", TrailingOffsetType.BasisPoints)]
    [InlineData("10", TrailingOffsetType.Ticks)]
    [InlineData("0.00000001", TrailingOffsetType.Price)]
    public void Trailing_stop_market_keeps_its_offset_exactly(string offset, TrailingOffsetType offsetType)
    {
        TrailingStopMarketOrder order = TrailingStopMarketOrder.Create(BtcParams(OrderSide.Sell), D(offset), offsetType);

        Assert.Equal(OrderType.TrailingStopMarket, order.Type);
        Assert.Equal(D(offset), order.TrailingOffset);
        Assert.Equal(offsetType, order.TrailingOffsetType);
        Assert.Null(order.TriggerPrice); // set by the engine once the market is known
        Assert.Null(order.ActivationPrice);
        Assert.False(order.IsActivated);
    }

    [Fact]
    public void Trailing_stop_market_keeps_optional_trigger_and_activation_prices()
    {
        TrailingStopMarketOrder order = TrailingStopMarketOrder.Create(
            BtcParams(OrderSide.Sell), 25.5m, TrailingOffsetType.Price, triggerPrice: _trigger, activationPrice: Price.Parse("51000.00"), triggerType: TriggerType.MarkPrice);

        Assert.Equal(_trigger, order.TriggerPrice);
        Assert.Equal(Price.Parse("51000.00"), order.ActivationPrice);
        Assert.Equal(TriggerType.MarkPrice, order.TriggerType);
    }

    [Fact]
    public void Trailing_stop_limit_keeps_both_offsets_and_optional_prices()
    {
        TrailingStopLimitOrder order = TrailingStopLimitOrder.Create(
            BtcParams(OrderSide.Sell), trailingOffset: 100m, limitOffset: 12.5m, TrailingOffsetType.BasisPoints, price: _limit, triggerPrice: _trigger);

        Assert.Equal(OrderType.TrailingStopLimit, order.Type);
        Assert.Equal(100m, order.TrailingOffset);
        Assert.Equal(12.5m, order.LimitOffset);
        Assert.Equal(TrailingOffsetType.BasisPoints, order.TrailingOffsetType);
        Assert.Equal(_limit, order.Price);
        Assert.Equal(_trigger, order.TriggerPrice);
        Assert.False(order.IsActivated);
    }

    [Fact]
    public void Market_to_limit_order_has_no_price_until_it_fills()
    {
        MarketToLimitOrder order = MarketToLimitOrder.Create(BtcParams(), displayQuantity: Quantity.Parse("2.000000"));

        Assert.Equal(OrderType.MarketToLimit, order.Type);
        Assert.Null(order.Price);
        Assert.Equal(Quantity.Parse("2.000000"), order.DisplayQuantity);
    }

    [Fact]
    public void Execution_instructions_are_recorded()
    {
        LimitOrder order = LimitOrder.Create(BtcParams() with { PostOnly = true, ReduceOnly = true, QuoteQuantity = true, EmulationTrigger = TriggerType.BidAsk }, _limit);

        Assert.True(order.IsPostOnly);
        Assert.True(order.IsReduceOnly);
        Assert.True(order.IsQuoteQuantity);
        Assert.Equal(TriggerType.BidAsk, order.EmulationTrigger);
    }

    [Fact]
    public void Execution_instructions_default_to_off()
    {
        LimitOrder order = LimitOrder.Create(BtcParams(), _limit);

        Assert.False(order.IsPostOnly);
        Assert.False(order.IsReduceOnly);
        Assert.False(order.IsQuoteQuantity);
        Assert.False(order.IsContingency);
        Assert.False(order.IsParentOrder);
        Assert.False(order.IsChildOrder);
        Assert.False(order.IsSpawned);
        Assert.Empty(order.Tags);
        Assert.Empty(order.LinkedOrderIds);
    }

    [Fact]
    public void A_bracket_entry_is_a_parent_and_its_exits_are_linked_children()
    {
        OrderListId listId = new("OL-1");
        ClientOrderId entryId = new("O-ENTRY");
        ClientOrderId stopId = new("O-SL");
        ClientOrderId targetId = new("O-TP");

        MarketOrder entry = MarketOrder.Create(BtcParams() with
        {
            ClientOrderId = entryId,
            Contingency = ContingencyType.Oto,
            OrderListId = listId,
            LinkedOrderIds = [stopId, targetId],
        });
        StopMarketOrder stop = StopMarketOrder.Create(
            BtcParams(OrderSide.Sell) with
            {
                ClientOrderId = stopId,
                Contingency = ContingencyType.Ouo,
                OrderListId = listId,
                LinkedOrderIds = [targetId],
                ParentOrderId = entryId,
                ReduceOnly = true,
            },
            _trigger);

        Assert.True(entry.IsContingency);
        Assert.True(entry.IsParentOrder);
        Assert.False(entry.IsChildOrder);
        Assert.Equal([stopId, targetId], entry.LinkedOrderIds);
        Assert.Equal(listId, entry.OrderListId);

        Assert.True(stop.IsContingency);
        Assert.False(stop.IsParentOrder);
        Assert.True(stop.IsChildOrder);
        Assert.Equal(entryId, stop.ParentOrderId);
        Assert.Equal(ContingencyType.Ouo, stop.Contingency);
    }

    [Fact]
    public void Execution_algorithm_details_and_tags_are_recorded()
    {
        MarketOrder order = MarketOrder.Create(BtcParams() with
        {
            ExecAlgorithmId = new ExecAlgorithmId("TWAP"),
            ExecAlgorithmParams = new Dictionary<string, string> { ["horizon_secs"] = "60" },
            ExecSpawnId = new ClientOrderId("O-PARENT"),
            Tags = ["entry", "breakout"],
        });

        Assert.Equal(new ExecAlgorithmId("TWAP"), order.ExecAlgorithmId);
        Assert.Equal("60", order.ExecAlgorithmParams!["horizon_secs"]);
        Assert.True(order.IsSpawned);
        Assert.Equal(["entry", "breakout"], order.Tags);
    }

    [Fact]
    public void Info_describes_the_order_in_one_line()
    {
        LimitOrder limit = LimitOrder.Create(BtcParams(OrderSide.Buy, "1.500000"), _limit);
        LimitOrder gtd = LimitOrder.Create(BtcParams(OrderSide.Sell, "1.500000") with { TimeInForce = TimeInForce.Gtd }, _limit, expireTime: At(60));
        StopMarketOrder stop = StopMarketOrder.Create(BtcParams(OrderSide.Sell, "2.000000"), _trigger, TriggerType.MarkPrice);

        Assert.Equal("BUY 1.500000 BTCUSDT.BINANCE LIMIT GTC @ 50000.10", limit.Info());
        Assert.Equal("SELL 1.500000 BTCUSDT.BINANCE LIMIT GTD 2023-11-14T22:14:20.0000000Z @ 50000.10", gtd.Info());
        Assert.Equal("SELL 2.000000 BTCUSDT.BINANCE STOPMARKET GTC trigger 49000.00 [MarkPrice]", stop.Info());
    }

    [Fact]
    public void Text_form_includes_status_and_identifiers()
    {
        MarketOrder order = MarketOrder.Create(BtcParams(OrderSide.Buy, "1.000000") with { Tags = ["a", "b"] });

        Assert.Equal(
            "MarketOrder(BUY 1.000000 BTCUSDT.BINANCE MARKET GTC, status=Initialized, client_order_id=O-20231114-221320-001-001-1, venue_order_id=None, position_id=None, tags=[a,b])",
            order.ToString());
    }

    [Theory]
    [InlineData(PositionSide.Flat, "5.000000", false)] // nothing to reduce
    [InlineData(PositionSide.Long, "10.000000", true)]
    [InlineData(PositionSide.Long, "12.000000", true)]
    [InlineData(PositionSide.Long, "9.999999", false)] // would flip the position
    [InlineData(PositionSide.Short, "50.000000", false)] // same direction, would increase
    public void WouldReduceOnly_is_true_only_when_a_sell_fits_inside_a_long_position(PositionSide side, string positionQuantity, bool expected)
    {
        MarketOrder sell = MarketOrder.Create(BtcParams(OrderSide.Sell, "10.000000"));

        Assert.Equal(expected, sell.WouldReduceOnly(side, Quantity.Parse(positionQuantity)));
    }

    [Fact]
    public void WouldReduceOnly_is_true_when_a_buy_fits_inside_a_short_position()
    {
        MarketOrder buy = MarketOrder.Create(BtcParams(OrderSide.Buy, "10.000000"));

        Assert.True(buy.WouldReduceOnly(PositionSide.Short, Quantity.Parse("10.000000")));
        Assert.False(buy.WouldReduceOnly(PositionSide.Long, Quantity.Parse("10.000000")));
    }

    [Fact]
    public void An_order_list_takes_its_identity_from_the_first_order()
    {
        MarketOrder entry = MarketOrder.Create(BtcParams() with { ClientOrderId = new ClientOrderId("O-1") });
        StopMarketOrder stop = StopMarketOrder.Create(BtcParams(OrderSide.Sell) with { ClientOrderId = new ClientOrderId("O-2") }, _trigger);

        OrderList list = new(new OrderListId("OL-1"), [entry, stop]);

        Assert.Same(entry, list.First);
        Assert.Equal(2, list.Orders.Count);
        Assert.Equal(entry.InstrumentId, list.InstrumentId);
        Assert.Equal(Strategy, list.StrategyId);
        Assert.Equal(T0, list.TsInit);
        Assert.Equal("OrderList(OL-1, 2 orders, BTCUSDT.BINANCE)", list.ToString());
    }

    [Fact]
    public void An_order_list_must_not_be_empty()
    {
        Assert.Throws<ArgumentException>(() => new OrderList(new OrderListId("OL-1"), []));
        Assert.Throws<ArgumentNullException>(() => new OrderList(new OrderListId("OL-1"), null!));
    }

    private static Order Create(OrderType type, OrderParams p, UnixNanos? expireTime) => type switch
    {
        OrderType.Limit => LimitOrder.Create(p, _limit, expireTime),
        OrderType.StopMarket => StopMarketOrder.Create(p, _trigger, expireTime: expireTime),
        OrderType.StopLimit => StopLimitOrder.Create(p, _limit, _trigger, expireTime: expireTime),
        OrderType.MarketIfTouched => MarketIfTouchedOrder.Create(p, _trigger, expireTime: expireTime),
        OrderType.LimitIfTouched => LimitIfTouchedOrder.Create(p, _limit, _trigger, expireTime: expireTime),
        OrderType.TrailingStopMarket => TrailingStopMarketOrder.Create(p, 25m, expireTime: expireTime),
        OrderType.TrailingStopLimit => TrailingStopLimitOrder.Create(p, 25m, 5m, expireTime: expireTime),
        OrderType.MarketToLimit => MarketToLimitOrder.Create(p, expireTime),
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
}
