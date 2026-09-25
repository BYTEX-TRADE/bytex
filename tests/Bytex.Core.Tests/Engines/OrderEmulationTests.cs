using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Tests.Support;

namespace Bytex.Core.Tests.Engines;

// Why: R3.10. A venue that has no stops is not a venue a strategy with a stop can trade, unless the trigger is the
// engine's business: the order is held here, the venue is told nothing, and when the market reaches the trigger the
// venue is told about a market or a limit order, which every venue can hold. What is pinned here is that nothing
// reaches the venue early, that the trigger is judged on the price the order asked for and in the direction its own
// type implies, and that a released order keeps the id its owner submitted - a fill has to come back on it.
public class OrderEmulationTests
{
    private static OrderParams Emulated(string id, OrderSide side, string quantity = "1.000", TriggerType trigger = TriggerType.LastPrice,
        TimeInForce timeInForce = TimeInForce.Gtc, bool reduceOnly = false) =>
        TestOrders.Params(id, TestIds.BtcUsdt, side, quantity, timeInForce: timeInForce, reduceOnly: reduceOnly) with { EmulationTrigger = trigger };

    private static void Traded(ExecHarness h, string price)
    {
        TradeTick tick = new(TestIds.BtcUsdt, Price.Parse(price), Quantity.Parse("1.000"), AggressorSide.Buyer, new TradeId("T-" + price), h.Clock.Timestamp, h.Clock.Timestamp);
        h.Cache.AddTradeTick(tick);
        h.Bus.Publish(Topics.Trades(TestIds.BtcUsdt), tick);
    }

    private static void Quoted(ExecHarness h, string bid, string ask)
    {
        QuoteTick tick = new(TestIds.BtcUsdt, Price.Parse(bid), Price.Parse(ask), Quantity.Parse("1.000"), Quantity.Parse("1.000"), h.Clock.Timestamp, h.Clock.Timestamp);
        h.Cache.AddQuoteTick(tick);
        h.Bus.Publish(Topics.Quotes(TestIds.BtcUsdt), tick);
    }

    /// <summary>The order the venue was told about, which is never the one that was held.</summary>
    private static Order Routed(ExecHarness h) => Assert.Single(h.Client.Received<SubmitOrder>()).Order;

    [Fact]
    public void An_order_triggered_here_is_held_and_the_venue_is_told_nothing()
    {
        ExecHarness h = new();
        StopMarketOrder stop = StopMarketOrder.Create(Emulated("O-1", OrderSide.Buy), Price.Parse("51000.00"));

        h.Submit(stop);

        Assert.Empty(h.Client.Commands);
        Assert.Equal(1, h.Engine.OrdersHeldLocally);
        Assert.Equal(OrderStatus.Emulated, stop.Status);
        Assert.True(stop.IsEmulated);
        Assert.Contains(stop.ClientOrderId, h.Cache.OrdersEmulated().Select(o => o.ClientOrderId));
        Assert.True(h.Cache.IsOrderEmulated(stop.ClientOrderId));
        Assert.Single(h.OrderEvents.Messages.OfType<OrderEmulated>());
    }

    [Fact]
    public void A_stop_market_leaves_as_a_market_order_when_the_market_trades_through_its_trigger()
    {
        ExecHarness h = new();
        h.Submit(StopMarketOrder.Create(Emulated("O-1", OrderSide.Buy), Price.Parse("51000.00")));

        Traded(h, "50900.00");
        Assert.Empty(h.Client.Commands);

        Traded(h, "51000.00"); // at the trigger is through it

        Order released = Routed(h);
        Assert.Equal(OrderType.Market, released.Type);
        Assert.Equal(new ClientOrderId("O-1"), released.ClientOrderId);
        Assert.Equal(Quantity.Parse("1.000"), released.Quantity);
        Assert.Equal(OrderSide.Buy, released.Side);

        // The event says what released it, and the order that is now under that id is the one the venue has.
        OrderReleased release = Assert.Single(h.OrderEvents.Messages.OfType<OrderReleased>());
        Assert.Equal(Price.Parse("51000.00"), release.ReleasedPrice);
        Assert.Same(released, h.Cache.Order(new ClientOrderId("O-1")));
        Assert.Empty(h.Cache.OrdersEmulated());

        // Its own history says where it came from, so nothing that reads the order has to be told separately.
        Assert.Equal([typeof(OrderInitialized), typeof(OrderEmulated), typeof(OrderReleased)], released.Events.Select(e => e.GetType()));
    }

    [Fact]
    public void A_stop_limit_leaves_as_a_limit_order_at_the_price_it_was_holding()
    {
        ExecHarness h = new();
        h.Submit(StopLimitOrder.Create(Emulated("O-1", OrderSide.Sell), Price.Parse("49000.00"), Price.Parse("49500.00")));

        Traded(h, "49400.00");

        Order released = Routed(h);
        Assert.Equal(OrderType.Limit, released.Type);
        Assert.Equal(Price.Parse("49000.00"), released.Price);
        Assert.Equal(new ClientOrderId("O-1"), released.ClientOrderId);
    }

    [Theory]
    [InlineData(OrderSide.Buy, "51000.00", "50900.00", false)]  // a buy stop waits for the market to rise to it
    [InlineData(OrderSide.Buy, "51000.00", "51100.00", true)]
    [InlineData(OrderSide.Sell, "49000.00", "49100.00", false)] // a sell stop waits for it to fall
    [InlineData(OrderSide.Sell, "49000.00", "48900.00", true)]
    public void A_stop_is_reached_from_the_side_its_own_direction_implies(OrderSide side, string trigger, string traded, bool released)
    {
        ExecHarness h = new();
        h.Submit(StopMarketOrder.Create(Emulated("O-1", side), Price.Parse(trigger)));

        Traded(h, traded);

        Assert.Equal(released, h.Client.Commands.Count == 1);
    }

    [Theory]
    [InlineData(OrderSide.Buy, "49000.00", "49100.00", false)]  // a buy if-touched waits for the market to come down to it
    [InlineData(OrderSide.Buy, "49000.00", "48900.00", true)]
    [InlineData(OrderSide.Sell, "51000.00", "50900.00", false)]
    [InlineData(OrderSide.Sell, "51000.00", "51100.00", true)]
    public void An_if_touched_order_is_the_mirror_of_a_stop(OrderSide side, string trigger, string traded, bool released)
    {
        ExecHarness h = new();
        h.Submit(MarketIfTouchedOrder.Create(Emulated("O-1", side), Price.Parse(trigger)));

        Traded(h, traded);

        Assert.Equal(released, h.Client.Commands.Count == 1);
        if (released)
        {
            Assert.Equal(OrderType.Market, Routed(h).Type);
        }
    }

    [Fact]
    public void A_trigger_the_market_has_already_passed_is_reached_the_moment_the_order_arrives()
    {
        // The market was already through the trigger when the order was submitted. A stop that waited for the next
        // tick to notice would sit there while the move it was placed for ran away.
        ExecHarness h = new();
        Traded(h, "52000.00");

        h.Submit(StopMarketOrder.Create(Emulated("O-1", OrderSide.Buy), Price.Parse("51000.00")));

        Assert.Equal(OrderType.Market, Routed(h).Type);
    }

    [Fact]
    public void The_bid_ask_trigger_reads_the_side_the_order_would_have_to_cross()
    {
        // A buy is filled at the ask, so that is what its trigger is judged against: a bid through the trigger with
        // the ask still under it is not a level the order could have been filled at.
        ExecHarness h = new();
        h.Submit(StopMarketOrder.Create(Emulated("O-1", OrderSide.Buy, trigger: TriggerType.BidAsk), Price.Parse("51000.00")));

        Quoted(h, "51050.00", "50950.00"); // a crossed book: the bid is through, the ask is not
        Assert.Empty(h.Client.Commands);

        Quoted(h, "50950.00", "51000.00");
        Assert.Equal(OrderType.Market, Routed(h).Type);
    }

    [Fact]
    public void A_mark_price_can_be_what_a_trigger_watches()
    {
        ExecHarness h = new();
        h.Submit(StopMarketOrder.Create(Emulated("O-1", OrderSide.Sell, trigger: TriggerType.MarkPrice), Price.Parse("49000.00")));

        MarkPriceUpdate mark = new(TestIds.BtcUsdt, Price.Parse("48900.00"), h.Clock.Timestamp, h.Clock.Timestamp);
        h.Cache.AddMarkPrice(mark);
        h.Bus.Publish(Topics.MarkPrices(TestIds.BtcUsdt), mark);

        Assert.Equal(OrderType.Market, Routed(h).Type);
    }

    [Fact]
    public void A_held_order_that_is_cancelled_never_reaches_the_venue()
    {
        ExecHarness h = new();
        StopMarketOrder stop = StopMarketOrder.Create(Emulated("O-1", OrderSide.Buy), Price.Parse("51000.00"));
        h.Submit(stop);

        h.Bus.Send(Endpoints.ExecutionEngineExecute, new CancelOrder(stop.TraderId, stop.StrategyId, stop.InstrumentId, stop.ClientOrderId, null, null, Guid.NewGuid(), h.Clock.Timestamp));

        Assert.Equal(OrderStatus.Canceled, stop.Status);
        Assert.Empty(h.Client.Received<SubmitOrder>());

        // It left the engine's book when it was cancelled, not when a price next happened to arrive.
        Assert.Equal(0, h.Engine.OrdersHeldLocally);

        // And the price that would have released it does nothing.
        Traded(h, "52000.00");
        Assert.Empty(h.Client.Received<SubmitOrder>());
    }

    [Fact]
    public void A_held_order_whose_own_time_runs_out_expires_here()
    {
        ExecHarness h = new();
        UnixNanos expiry = h.Clock.Timestamp + TimeSpan.FromMinutes(5);
        h.Submit(StopMarketOrder.Create(Emulated("O-1", OrderSide.Buy, timeInForce: TimeInForce.Gtd), Price.Parse("51000.00"), expireTime: expiry));

        h.Clock.SetTime(expiry + TimeSpan.FromSeconds(1));
        Traded(h, "50000.00");

        Assert.Equal(OrderStatus.Expired, h.Cache.Order(new ClientOrderId("O-1"))!.Status);
        Assert.Empty(h.Client.Received<SubmitOrder>());
        Assert.Equal(0, h.Engine.OrdersHeldLocally);
    }

    [Fact]
    public void An_order_that_cannot_be_triggered_here_is_denied_rather_than_sent()
    {
        // A limit order has nothing to trigger on, and saying so is the only honest answer: sending it to the venue
        // would ignore what its owner asked for, and holding it would hold it for ever.
        ExecHarness h = new();
        h.Submit(LimitOrder.Create(Emulated("O-1", OrderSide.Buy), Price.Parse("50000.00")));

        OrderDenied denied = Assert.Single(h.OrderEvents.Messages.OfType<OrderDenied>());
        Assert.StartsWith("EMULATION_UNSUPPORTED", denied.Reason, StringComparison.Ordinal);

        // Which of the two things is wrong with it, in its own words: the type, not a missing trigger price.
        Assert.Contains("a Limit order cannot be triggered here", denied.Reason, StringComparison.Ordinal);
        Assert.Empty(h.Client.Commands);
        Assert.Equal(0, h.Engine.OrdersHeldLocally);
    }

    [Fact]
    public void An_order_list_carrying_one_is_refused_whole_rather_than_half_sent()
    {
        // The legs of a list are contingent on each other, and one of them leaving on its own is not the list its
        // owner submitted. Every order in it is told, so nothing is left waiting for a venue that never heard of it.
        ExecHarness h = new();
        MarketOrder entry = TestOrders.Market("O-ENTRY", TestIds.BtcUsdt, OrderSide.Buy, "1.000");
        StopMarketOrder stop = StopMarketOrder.Create(Emulated("O-STOP", OrderSide.Sell), Price.Parse("49000.00"));

        OrderList list = new(new OrderListId("OL-1"), [entry, stop]);
        h.Bus.Send(Endpoints.ExecutionEngineExecute, new SubmitOrderList(TestIds.Trader, list.StrategyId, list, null, null, null, Guid.NewGuid(), h.Clock.Timestamp));

        Assert.Equal(2, h.OrderEvents.Messages.OfType<OrderDenied>().Count());
        Assert.All(h.OrderEvents.Messages.OfType<OrderDenied>(), d => Assert.StartsWith("EMULATION_IN_LIST", d.Reason, StringComparison.Ordinal));
        Assert.Empty(h.Client.Commands);
    }

    [Fact]
    public void A_released_order_keeps_what_its_owner_gave_it()
    {
        ExecHarness h = new();
        PositionId position = new("P-1");
        h.Submit(StopMarketOrder.Create(Emulated("O-1", OrderSide.Sell, reduceOnly: true), Price.Parse("49000.00")), position);

        Traded(h, "48900.00");

        SubmitOrder sent = Assert.Single(h.Client.Received<SubmitOrder>());
        Order released = sent.Order;
        Assert.True(released.IsReduceOnly, "a stop placed to get out was released as an order that could open a position");
        Assert.Equal(TriggerType.Default, released.EmulationTrigger);

        // The position it was submitted under is what the venue is told and what the cache still says: a hedging
        // venue needs it, and the position the order belongs to does not change because the engine held it for a while.
        Assert.Equal(position, sent.PositionId);
        Assert.Equal(position, h.Cache.PositionIdFor(released.ClientOrderId));
    }

    [Fact]
    public void An_order_that_leaves_its_trigger_to_the_venue_is_sent_straight_there()
    {
        ExecHarness h = new();
        StopMarketOrder stop = TestOrders.StopMarket("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "51000.00");

        h.Submit(stop);

        Assert.Equal(OrderType.StopMarket, Routed(h).Type);
        Assert.False(stop.IsEmulated);
        Assert.Empty(h.OrderEvents.Messages.OfType<OrderEmulated>());
    }
}
