using Bytex.Core.Model;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using static Bytex.Core.Tests.Model.ModelFixtures;

namespace Bytex.Core.Tests.Model;

// The order state machine is the engine's guard against impossible order lifecycles. The legal table below is
// the one published in docs/concepts/orders.md (plus the Emulated/Released rows of docs/design/0003); the
// illegal table lists transitions that no document allows. Each case drives a real order through real events.
public class OrderStateMachineTests
{
    /// <summary>A buy limit for 10 units; fills in these tests are 4 (partial) or 10 (complete).</summary>
    private static LimitOrder NewOrder() => LimitOrder.Create(BtcParams(OrderSide.Buy, "10.000000"), Price.Parse("100.00"));

    private static OrderEvent Make(Order order, string step, long t) => step switch
    {
        "Denied" => Denied(order, t),
        "Emulated" => Emulated(order, t),
        "Released" => Released(order, t),
        "Submitted" => Submitted(order, t),
        "Accepted" => Accepted(order, t),
        "Rejected" => Rejected(order, t),
        "Canceled" => Canceled(order, t),
        "Expired" => Expired(order, t),
        "Triggered" => Triggered(order, t),
        "PendingUpdate" => PendingUpdate(order, t),
        "PendingCancel" => PendingCancel(order, t),
        "ModifyRejected" => ModifyRejected(order, t),
        "CancelRejected" => CancelRejected(order, t),
        "Updated" => Updated(order, "10.000000", t: t),
        "PartialFill" => Fill(order, "4.000000", "100.00", "T-" + t, t: t),
        "Fill" => Fill(order, order.LeavesQuantity.ToString(), "100.00", "T-" + t, t: t),
        _ => throw new ArgumentOutOfRangeException(nameof(step), step, "unknown step"),
    };

    private static LimitOrder Drive(string path)
    {
        LimitOrder order = NewOrder();
        long t = 1;
        foreach (string step in path.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            order.Apply(Make(order, step, t++));
        }

        return order;
    }

    [Theory]
    // Initialized
    [InlineData("", "Denied", OrderStatus.Denied)]
    [InlineData("", "Emulated", OrderStatus.Emulated)]
    [InlineData("", "Released", OrderStatus.Released)]
    [InlineData("", "Submitted", OrderStatus.Submitted)]
    // Emulated
    [InlineData("Emulated", "Canceled", OrderStatus.Canceled)]
    [InlineData("Emulated", "Expired", OrderStatus.Expired)]
    [InlineData("Emulated", "Released", OrderStatus.Released)]
    // Released
    [InlineData("Emulated,Released", "Denied", OrderStatus.Denied)]
    [InlineData("Emulated,Released", "Submitted", OrderStatus.Submitted)]
    // Submitted
    [InlineData("Submitted", "Accepted", OrderStatus.Accepted)]
    [InlineData("Submitted", "Rejected", OrderStatus.Rejected)]
    [InlineData("Submitted", "Canceled", OrderStatus.Canceled)]
    [InlineData("Submitted", "Expired", OrderStatus.Expired)]
    [InlineData("Submitted", "Triggered", OrderStatus.Triggered)]
    [InlineData("Submitted", "PartialFill", OrderStatus.PartiallyFilled)]
    [InlineData("Submitted", "Fill", OrderStatus.Filled)]
    [InlineData("Submitted", "PendingUpdate", OrderStatus.PendingUpdate)]
    [InlineData("Submitted", "PendingCancel", OrderStatus.PendingCancel)]
    // Accepted
    [InlineData("Submitted,Accepted", "Canceled", OrderStatus.Canceled)]
    [InlineData("Submitted,Accepted", "Expired", OrderStatus.Expired)]
    [InlineData("Submitted,Accepted", "Triggered", OrderStatus.Triggered)]
    [InlineData("Submitted,Accepted", "PendingUpdate", OrderStatus.PendingUpdate)]
    [InlineData("Submitted,Accepted", "PendingCancel", OrderStatus.PendingCancel)]
    [InlineData("Submitted,Accepted", "PartialFill", OrderStatus.PartiallyFilled)]
    [InlineData("Submitted,Accepted", "Fill", OrderStatus.Filled)]
    // Triggered
    [InlineData("Submitted,Accepted,Triggered", "Canceled", OrderStatus.Canceled)]
    [InlineData("Submitted,Accepted,Triggered", "Expired", OrderStatus.Expired)]
    [InlineData("Submitted,Accepted,Triggered", "PendingUpdate", OrderStatus.PendingUpdate)]
    [InlineData("Submitted,Accepted,Triggered", "PendingCancel", OrderStatus.PendingCancel)]
    [InlineData("Submitted,Accepted,Triggered", "PartialFill", OrderStatus.PartiallyFilled)]
    [InlineData("Submitted,Accepted,Triggered", "Fill", OrderStatus.Filled)]
    // PendingUpdate
    [InlineData("Submitted,Accepted,PendingUpdate", "Accepted", OrderStatus.Accepted)]
    [InlineData("Submitted,Accepted,PendingUpdate", "Canceled", OrderStatus.Canceled)]
    [InlineData("Submitted,Accepted,PendingUpdate", "Expired", OrderStatus.Expired)]
    [InlineData("Submitted,Accepted,PendingUpdate", "Triggered", OrderStatus.Triggered)]
    [InlineData("Submitted,Accepted,PendingUpdate", "PendingCancel", OrderStatus.PendingCancel)]
    [InlineData("Submitted,Accepted,PendingUpdate", "PartialFill", OrderStatus.PartiallyFilled)]
    [InlineData("Submitted,Accepted,PendingUpdate", "Fill", OrderStatus.Filled)]
    // PendingCancel
    [InlineData("Submitted,Accepted,PendingCancel", "Canceled", OrderStatus.Canceled)]
    [InlineData("Submitted,Accepted,PendingCancel", "Accepted", OrderStatus.Accepted)]
    [InlineData("Submitted,Accepted,PendingCancel", "Expired", OrderStatus.Expired)]
    [InlineData("Submitted,Accepted,PendingCancel", "PartialFill", OrderStatus.PartiallyFilled)]
    [InlineData("Submitted,Accepted,PendingCancel", "Fill", OrderStatus.Filled)]
    // PartiallyFilled
    [InlineData("Submitted,Accepted,PartialFill", "Canceled", OrderStatus.Canceled)]
    [InlineData("Submitted,Accepted,PartialFill", "Expired", OrderStatus.Expired)]
    [InlineData("Submitted,Accepted,PartialFill", "PendingUpdate", OrderStatus.PendingUpdate)]
    [InlineData("Submitted,Accepted,PartialFill", "PendingCancel", OrderStatus.PendingCancel)]
    [InlineData("Submitted,Accepted,PartialFill", "PartialFill", OrderStatus.PartiallyFilled)]
    [InlineData("Submitted,Accepted,PartialFill", "Fill", OrderStatus.Filled)]
    public void Legal_transition_moves_to_the_documented_status(string path, string step, OrderStatus expected)
    {
        LimitOrder order = Drive(path);
        int eventsBefore = order.EventCount;
        OrderEvent e = Make(order, step, 50);

        order.Apply(e);

        Assert.Equal(expected, order.Status);
        Assert.Equal(eventsBefore + 1, order.EventCount);
        Assert.Same(e, order.LastEvent);
        Assert.Equal(At(50), order.TsLast);
    }

    [Theory]
    // terminal states accept nothing further
    [InlineData("Denied", "Submitted")]
    [InlineData("Denied", "Accepted")]
    [InlineData("Submitted,Rejected", "Accepted")]
    [InlineData("Submitted,Rejected", "Fill")]
    [InlineData("Submitted,Accepted,Expired", "Fill")]
    [InlineData("Submitted,Accepted,Expired", "Canceled")]
    [InlineData("Submitted,Accepted,Fill", "Canceled")]
    [InlineData("Submitted,Accepted,Fill", "Expired")]
    [InlineData("Submitted,Accepted,Fill", "PendingCancel")]
    [InlineData("Submitted,Accepted,Fill", "Accepted")]
    [InlineData("Submitted,Accepted,Canceled", "Accepted")]
    [InlineData("Submitted,Accepted,Canceled", "PendingUpdate")]
    // local states cannot skip the submit step
    [InlineData("Emulated", "Submitted")]
    [InlineData("Emulated", "Accepted")]
    [InlineData("Emulated", "Fill")]
    [InlineData("Emulated,Released", "Accepted")]
    [InlineData("Emulated,Released", "Fill")]
    [InlineData("", "PendingUpdate")]
    [InlineData("", "PendingCancel")]
    // no going backwards
    [InlineData("Submitted", "Submitted")]
    [InlineData("Submitted", "Denied")]
    [InlineData("Submitted", "Emulated")]
    [InlineData("Submitted,Accepted", "Submitted")]
    [InlineData("Submitted,Accepted", "Denied")]
    [InlineData("Submitted,Accepted", "Emulated")]
    [InlineData("Submitted,Accepted", "Released")]
    [InlineData("Submitted,Accepted,Triggered", "Triggered")]
    [InlineData("Submitted,Accepted,Triggered", "Accepted")]
    [InlineData("Submitted,Accepted,PartialFill", "Accepted")]
    [InlineData("Submitted,Accepted,PartialFill", "Rejected")]
    [InlineData("Submitted,Accepted,PartialFill", "Triggered")]
    [InlineData("Submitted,Accepted,PartialFill", "Submitted")]
    public void Illegal_transition_throws_and_leaves_the_order_untouched(string path, string step)
    {
        LimitOrder order = Drive(path);
        OrderStatus statusBefore = order.Status;
        int eventsBefore = order.EventCount;
        UnixNanos tsLastBefore = order.TsLast;
        Quantity filledBefore = order.FilledQuantity;
        OrderEvent e = Make(order, step, 50);

        InvalidOrderTransitionException ex = Assert.Throws<InvalidOrderTransitionException>(() => order.Apply(e));

        Assert.Contains(statusBefore.ToString(), ex.Message, StringComparison.Ordinal);
        Assert.Contains(order.ClientOrderId.Value, ex.Message, StringComparison.Ordinal);
        Assert.Equal(statusBefore, order.Status);
        Assert.Equal(eventsBefore, order.EventCount);
        Assert.Equal(tsLastBefore, order.TsLast);
        Assert.Equal(filledBefore, order.FilledQuantity);
    }

    [Fact]
    public void A_fill_on_a_completely_filled_order_is_rejected()
    {
        LimitOrder order = Drive("Submitted,Accepted,Fill");

        Assert.Throws<InvalidOrderTransitionException>(() => order.Apply(Fill(order, "1.000000", "100.00", "T-LATE")));
        Assert.Equal(Quantity.Parse("10.000000"), order.FilledQuantity);
    }

    [Fact]
    public void A_second_initialising_event_is_rejected()
    {
        LimitOrder order = NewOrder();

        Assert.Throws<InvalidOrderTransitionException>(() => order.Apply(order.InitEvent));
        Assert.Equal(1, order.EventCount);
    }

    [Fact]
    public void An_event_for_another_order_is_rejected()
    {
        LimitOrder order = NewOrder();
        LimitOrder other = LimitOrder.Create(BtcParams() with { ClientOrderId = new ClientOrderId("O-OTHER") }, Price.Parse("100.00"));

        Assert.Throws<ArgumentException>(() => order.Apply(Submitted(other)));
        Assert.Equal(OrderStatus.Initialized, order.Status);
        Assert.Equal(1, order.EventCount);
    }

    [Fact]
    public void A_null_event_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => NewOrder().Apply(null!));
    }

    [Theory]
    [InlineData("Submitted,Accepted,PendingUpdate", "ModifyRejected", OrderStatus.Accepted)]
    [InlineData("Submitted,Accepted,PendingCancel", "CancelRejected", OrderStatus.Accepted)]
    [InlineData("Submitted,Accepted,PartialFill,PendingCancel", "CancelRejected", OrderStatus.PartiallyFilled)]
    [InlineData("Submitted,Accepted,PartialFill,PendingUpdate", "ModifyRejected", OrderStatus.PartiallyFilled)]
    [InlineData("Submitted,Accepted,Triggered,PendingUpdate", "Updated", OrderStatus.Triggered)]
    [InlineData("Submitted,Accepted,PendingUpdate", "Updated", OrderStatus.Accepted)]
    [InlineData("Submitted,PendingCancel", "CancelRejected", OrderStatus.Submitted)]
    // a cancel requested while an update is pending still reverts to the last settled status
    [InlineData("Submitted,Accepted,PendingUpdate,PendingCancel", "CancelRejected", OrderStatus.Accepted)]
    [InlineData("Submitted,Accepted,PartialFill,PendingUpdate,PendingUpdate", "ModifyRejected", OrderStatus.PartiallyFilled)]
    public void A_pending_status_reverts_to_the_previous_status_when_the_request_is_answered(string path, string step, OrderStatus expected)
    {
        LimitOrder order = Drive(path);

        order.Apply(Make(order, step, 50));

        Assert.Equal(expected, order.Status);
        Assert.False(order.IsPending);
    }

    [Theory]
    [InlineData("ModifyRejected")]
    [InlineData("CancelRejected")]
    [InlineData("Updated")]
    public void Update_and_rejection_events_outside_a_pending_state_keep_the_status_but_are_recorded(string step)
    {
        LimitOrder order = Drive("Submitted,Accepted");
        OrderEvent e = Make(order, step, 50);

        order.Apply(e);

        Assert.Equal(OrderStatus.Accepted, order.Status);
        Assert.Same(e, order.LastEvent);
    }

    [Theory]
    [InlineData("", false, false, false, true)]
    [InlineData("Emulated", false, false, false, true)]
    [InlineData("Emulated,Released", false, false, false, true)]
    [InlineData("Denied", false, true, false, false)]
    [InlineData("Submitted", false, false, true, false)]
    [InlineData("Submitted,Rejected", false, true, false, false)]
    [InlineData("Submitted,Accepted", true, false, false, false)]
    [InlineData("Submitted,Accepted,Triggered", true, false, false, false)]
    [InlineData("Submitted,Accepted,PendingUpdate", true, false, true, false)]
    [InlineData("Submitted,Accepted,PendingCancel", true, false, true, false)]
    [InlineData("Submitted,Accepted,PartialFill", true, false, false, false)]
    [InlineData("Submitted,Accepted,Fill", false, true, false, false)]
    [InlineData("Submitted,Accepted,Canceled", false, true, false, false)]
    [InlineData("Submitted,Accepted,Expired", false, true, false, false)]
    public void Status_predicates_partition_the_lifecycle(string path, bool isOpen, bool isClosed, bool isInflight, bool isActiveLocal)
    {
        LimitOrder order = Drive(path);

        Assert.Equal(isOpen, order.IsOpen);
        Assert.Equal(isClosed, order.IsClosed);
        Assert.Equal(isInflight, order.IsInflight);
        Assert.Equal(isActiveLocal, order.IsActiveLocal);
        Assert.Equal(path == "Emulated", order.IsEmulated);
    }

    [Fact]
    public void The_event_history_keeps_every_applied_event_in_order()
    {
        LimitOrder order = Drive("Submitted,Accepted,PendingUpdate,Updated,PartialFill,PendingCancel,Canceled");

        Assert.Equal(
            [
                typeof(OrderInitialized), typeof(OrderSubmitted), typeof(OrderAccepted), typeof(OrderPendingUpdate),
                typeof(OrderUpdated), typeof(OrderFilled), typeof(OrderPendingCancel), typeof(OrderCanceled),
            ],
            order.Events.Select(e => e.GetType()));
        Assert.Equal(OrderStatus.Canceled, order.Status);
    }

    [Fact]
    public void Lifecycle_timestamps_are_taken_from_the_events()
    {
        LimitOrder order = NewOrder();

        order.Apply(Submitted(order, t: 1));
        order.Apply(Accepted(order, t: 2));
        order.Apply(PendingUpdate(order, t: 3));
        order.Apply(Accepted(order, t: 4));
        Assert.Null(order.TsClosed);
        order.Apply(Canceled(order, t: 7));

        Assert.Equal(At(1), order.TsSubmitted);
        Assert.Equal(At(2), order.TsAccepted); // the first acceptance, not the re-acceptance after the update
        Assert.Equal(At(7), order.TsClosed);
        Assert.Equal(At(7), order.TsLast);
        Assert.Equal(T0, order.TsInit);
    }

    [Fact]
    public void Account_and_venue_order_id_are_learned_from_events()
    {
        LimitOrder order = NewOrder();

        order.Apply(Submitted(order));
        Assert.Equal(Account, order.AccountId);
        Assert.Null(order.VenueOrderId);

        order.Apply(Accepted(order, venueOrderId: "V-1"));
        Assert.Equal(new VenueOrderId("V-1"), order.VenueOrderId);
        Assert.Equal([new VenueOrderId("V-1")], order.VenueOrderIds);
    }

    [Fact]
    public void A_cancel_replace_that_changes_the_venue_order_id_keeps_the_history_of_ids()
    {
        LimitOrder order = Drive("Submitted,Accepted");

        order.Apply(Updated(order, "10.000000", price: "101.00", venueOrderId: "V-2"));
        order.Apply(Updated(order, "10.000000", price: "102.00", venueOrderId: "V-2"));

        Assert.Equal(new VenueOrderId("V-2"), order.VenueOrderId);
        Assert.Equal([new VenueOrderId("V-1"), new VenueOrderId("V-2")], order.VenueOrderIds);
    }

    [Fact]
    public void An_update_changes_quantity_and_price_and_recomputes_leaves()
    {
        LimitOrder order = Drive("Submitted,Accepted,PartialFill"); // 4 of 10 filled

        order.Apply(Updated(order, "8.000000", price: "99.50"));

        Assert.Equal(Quantity.Parse("8.000000"), order.Quantity);
        Assert.Equal(Quantity.Parse("4.000000"), order.FilledQuantity);
        Assert.Equal(Quantity.Parse("4.000000"), order.LeavesQuantity); // 8 - 4
        Assert.Equal(Price.Parse("99.50"), order.Price);
        Assert.Equal(OrderStatus.PartiallyFilled, order.Status);
    }

    [Fact]
    public void An_update_without_a_price_keeps_the_price()
    {
        LimitOrder order = Drive("Submitted,Accepted");

        order.Apply(Updated(order, "12.000000"));

        Assert.Equal(Quantity.Parse("12.000000"), order.Quantity);
        Assert.Equal(Quantity.Parse("12.000000"), order.LeavesQuantity);
        Assert.Equal(Price.Parse("100.00"), order.Price);
    }

    [Fact]
    public void An_update_moves_the_trigger_of_a_stop_order_and_both_prices_of_a_stop_limit()
    {
        StopMarketOrder stop = StopMarketOrder.Create(BtcParams(OrderSide.Sell), Price.Parse("95.00"));
        StopLimitOrder stopLimit = StopLimitOrder.Create(BtcParams(OrderSide.Sell), Price.Parse("94.50"), Price.Parse("95.00"));

        stop.Apply(Submitted(stop));
        stop.Apply(Updated(stop, "10.000000", triggerPrice: "96.00"));
        stopLimit.Apply(Submitted(stopLimit));
        stopLimit.Apply(Updated(stopLimit, "10.000000", price: "95.50", triggerPrice: "96.00"));

        Assert.Equal(Price.Parse("96.00"), stop.TriggerPrice);
        Assert.Null(stop.Price);
        Assert.Equal(Price.Parse("95.50"), stopLimit.Price);
        Assert.Equal(Price.Parse("96.00"), stopLimit.TriggerPrice);
    }
}
