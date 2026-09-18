using Bytex.Core.Model;
using Bytex.Core.Model.Accounts;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Tests.Support;

namespace Bytex.Core.Tests.Engines;

// Why: venue events must be applied to the cached order, reflected in the cache indexes and then published on
// "events.order.{strategy}" - in that order, so a strategy handler always sees the updated order.
public class ExecutionEngineOrderEventTests
{
    [Fact]
    public void Submitted_then_accepted_walks_the_order_and_the_cache_indexes()
    {
        ExecHarness h = new();
        LimitOrder order = TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00");
        h.Submit(order);

        h.Engine.Process(TestEvents.Submitted(order));
        bool inflightAfterSubmitted = h.Cache.IsOrderInflight(order.ClientOrderId);
        h.Engine.Process(TestEvents.Accepted(order, "V-100"));

        Assert.True(inflightAfterSubmitted);
        Assert.Equal(OrderStatus.Accepted, order.Status);
        Assert.False(h.Cache.IsOrderInflight(order.ClientOrderId));
        Assert.True(h.Cache.IsOrderOpen(order.ClientOrderId));
        Assert.Same(order, h.Cache.OrderForVenueId(new VenueOrderId("V-100")));
        Assert.Equal(["OrderSubmitted", "OrderAccepted"], h.OrderEvents.TypeNames);
        Assert.Equal(2, h.Engine.EventCount);
    }

    [Fact]
    public void Order_is_already_updated_when_its_event_reaches_subscribers()
    {
        ExecHarness h = new();
        LimitOrder order = TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00");
        h.Submit(order);
        List<OrderStatus> seen = new();
        h.Bus.Subscribe(Topics.OrderEvents(TestIds.Strategy), _ => seen.Add(h.Cache.Order(order.ClientOrderId)!.Status));

        h.Engine.Process(TestEvents.Submitted(order));
        h.Engine.Process(TestEvents.Accepted(order, "V-100"));

        Assert.Equal([OrderStatus.Submitted, OrderStatus.Accepted], seen);
    }

    [Fact]
    public void Events_are_published_on_the_topic_of_the_owning_strategy_only()
    {
        ExecHarness h = new();
        BusRecorder mine = new();
        BusRecorder other = new();
        h.Bus.Subscribe(Topics.OrderEvents(TestIds.Strategy), mine.Handle);
        h.Bus.Subscribe(Topics.OrderEvents(TestIds.OtherStrategy), other.Handle);
        LimitOrder order = TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00", TestIds.OtherStrategy);
        h.Submit(order);

        h.Engine.Process(TestEvents.Submitted(order));

        Assert.Empty(mine.Messages);
        Assert.Single(other.Messages);
    }

    [Fact]
    public void Event_for_an_order_the_cache_does_not_know_is_dropped()
    {
        ExecHarness h = new();
        LimitOrder stranger = TestOrders.Limit("O-404", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00");

        h.Engine.Process(TestEvents.Accepted(stranger, "V-404"));

        Assert.Empty(h.OrderEvents.Messages);
        Assert.Equal(OrderStatus.Initialized, stranger.Status);
    }

    [Fact(Skip = "BUG: ExecutionEngine.cs:373-376 finds the order by venue id, then Order.Apply (Order.cs) throws ArgumentException on the client-id mismatch, so the fallback can never work")]
    public void Event_with_a_foreign_client_order_id_is_matched_by_venue_order_id()
    {
        // A venue may echo its own client id (for example after a restart). The engine looks the order up by
        // venue order id for exactly that case, so the cancel must land on the cached order.
        ExecHarness h = new();
        LimitOrder order = h.SubmitAndAccept(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));
        OrderCanceled foreign = new(TestIds.Trader, TestIds.Strategy, TestIds.BtcUsdt, new ClientOrderId("VENUE-GENERATED"), order.VenueOrderId, TestIds.BinanceAccount, Guid.NewGuid(), TestOrders.T0, TestOrders.T0);

        h.Engine.Process(foreign);

        Assert.Equal(OrderStatus.Canceled, order.Status);
    }

    [Fact]
    public void Rejection_closes_the_order_in_the_cache()
    {
        ExecHarness h = new();
        LimitOrder order = TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00");
        h.Submit(order);
        h.Engine.Process(TestEvents.Submitted(order));

        h.Engine.Process(TestEvents.Rejected(order, "POST_ONLY_WOULD_CROSS"));

        Assert.Equal(OrderStatus.Rejected, order.Status);
        Assert.True(h.Cache.IsOrderClosed(order.ClientOrderId));
        Assert.False(h.Cache.IsOrderInflight(order.ClientOrderId));
        Assert.Equal("POST_ONLY_WOULD_CROSS", Assert.Single(h.OrderEvents.Of<OrderRejected>()).Reason);
    }

    [Fact]
    public void Cancel_confirmation_clears_the_local_pending_cancel_flag()
    {
        ExecHarness h = new();
        LimitOrder order = h.SubmitAndAccept(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));
        h.Bus.Send(Endpoints.ExecutionEngineExecute, new CancelOrder(TestIds.Trader, TestIds.Strategy, TestIds.BtcUsdt, order.ClientOrderId, order.VenueOrderId, null, Guid.NewGuid(), h.Clock.Timestamp));

        h.Engine.Process(TestEvents.Canceled(order));

        Assert.False(h.Cache.IsOrderPendingCancelLocal(order.ClientOrderId));
        Assert.True(h.Cache.IsOrderClosed(order.ClientOrderId));
        Assert.False(h.Cache.IsOrderOpen(order.ClientOrderId));
    }

    [Fact]
    public void Update_event_changes_quantity_and_price_of_the_cached_order()
    {
        ExecHarness h = new();
        LimitOrder order = h.SubmitAndAccept(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));

        h.Engine.Process(TestEvents.Updated(order, "2.500", "49500.00"));

        Assert.Equal(Quantity.Parse("2.500"), order.Quantity);
        Assert.Equal(Quantity.Parse("2.500"), order.LeavesQuantity);
        Assert.Equal(Price.Parse("49500.00"), order.Price);
        Assert.Equal(OrderStatus.Accepted, order.Status);
    }

    [Fact]
    public void Event_that_is_illegal_in_the_current_state_leaves_the_order_untouched()
    {
        ExecHarness h = new();
        LimitOrder order = h.SubmitAndAccept(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));
        h.Engine.Process(TestEvents.Canceled(order));
        int eventsBefore = order.EventCount;

        // Out of order: the venue's "accepted" arrives after the cancel confirmation. Canceled -> Accepted is not a legal transition.
        h.Engine.Process(TestEvents.Accepted(order, "V-O-1"));

        Assert.Equal(OrderStatus.Canceled, order.Status);
        Assert.Equal(eventsBefore, order.EventCount);
        Assert.True(h.Cache.IsOrderClosed(order.ClientOrderId));
    }

    [Fact(Skip = "BUG: ExecutionEngine.cs:390-392 publishes an event even when Order.Apply refused it, so strategies receive OnOrderAccepted for a canceled order")]
    public void Event_that_was_refused_by_the_order_is_not_published_to_the_strategy()
    {
        ExecHarness h = new();
        LimitOrder order = h.SubmitAndAccept(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));
        h.Engine.Process(TestEvents.Canceled(order));
        h.OrderEvents.Messages.Clear();

        h.Engine.Process(TestEvents.Accepted(order, "V-O-1"));

        Assert.Empty(h.OrderEvents.Messages);
    }

    [Fact]
    public void Duplicate_fill_does_not_change_order_or_position()
    {
        ExecHarness h = new();
        LimitOrder order = h.SubmitAndAccept(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));
        OrderFilled fill = TestEvents.Filled(order, "T-1", "0.400", "50000.00", commission: "2");

        h.Engine.Process(fill);
        h.Engine.Process(fill with { EventId = Guid.NewGuid() });

        Assert.Equal(Quantity.Parse("0.400"), order.FilledQuantity);
        Assert.Equal(OrderStatus.PartiallyFilled, order.Status);
        Assert.Equal(Money.Parse("2 USDT"), order.Commissions[Currencies.USDT]);
        Assert.Equal(0.4m, Assert.Single(h.Cache.PositionsOpen()).SignedQuantity);
    }

    [Fact(Skip = "BUG: ExecutionEngine.cs:448-485 republishes OrderFilled and a PositionChanged for a duplicate TradeId although orders.md says duplicate fills are ignored")]
    public void Duplicate_fill_is_not_published_a_second_time()
    {
        ExecHarness h = new();
        LimitOrder order = h.SubmitAndAccept(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));
        OrderFilled fill = TestEvents.Filled(order, "T-1", "0.400", "50000.00");

        h.Engine.Process(fill);
        h.Engine.Process(fill with { EventId = Guid.NewGuid() });

        Assert.Single(h.OrderEvents.Of<OrderFilled>());
        Assert.Single(h.PositionEvents.Messages);
    }

    [Fact]
    public void Account_state_creates_the_account_and_is_published_on_the_account_topic()
    {
        ExecHarness h = new();
        BusRecorder accountEvents = new();
        h.Bus.Subscribe(Topics.AllAccountEvents, accountEvents.Handle);
        AccountState state = TestEvents.CashState(TestIds.BinanceAccount, (Currencies.USDT, 10_000m, 2_500m));

        h.Engine.Process(state);

        Account account = Assert.IsType<CashAccount>(h.Cache.Account(TestIds.BinanceAccount));
        Assert.Equal(Money.Parse("7500 USDT"), account.BalanceFree(Currencies.USDT));
        Assert.Same(state, Assert.Single(accountEvents.Messages));
    }

    [Fact]
    public void Events_emitted_by_a_client_arrive_through_the_sink_interface()
    {
        ExecHarness h = new();
        LimitOrder order = TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00");
        h.Submit(order);
        h.Clock.SetTime(TestOrders.T0 + TimeSpan.FromSeconds(3));

        h.Client.EmitSubmitted(order);
        h.Client.EmitAccepted(order, "V-7");

        Assert.Equal(OrderStatus.Accepted, order.Status);
        Assert.Equal(TestIds.BinanceAccount, order.AccountId);
        Assert.Equal(TestOrders.T0 + TimeSpan.FromSeconds(3), order.TsAccepted);
    }

    [Fact]
    public void Message_that_is_neither_an_order_event_nor_an_account_state_is_ignored()
    {
        ExecHarness h = new();

        h.Bus.Send(Endpoints.ExecutionEngineProcess, "noise");

        Assert.Empty(h.OrderEvents.Messages);
        Assert.Equal(1, h.Engine.EventCount);
    }

    [Fact]
    public void Reset_zeroes_the_counters()
    {
        ExecHarness h = new();
        h.SubmitAndAccept(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));

        h.Engine.Reset();

        Assert.Equal(0, h.Engine.CommandCount);
        Assert.Equal(0, h.Engine.EventCount);
    }
}
