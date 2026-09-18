using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Tests.Support;

namespace Bytex.Core.Tests.Engines;

// Why: risk.md - "Modifications are checked for precision and limits on the new values"; a refused
// modification must come back as OrderModifyRejected (never OrderDenied) and must not reach the venue.
public class RiskEngineModifyOrderTests
{
    private static CurrencyPair Limited() => TestInstruments.BtcUsdt(
        minQuantity: Quantity.Parse("0.010"),
        maxQuantity: Quantity.Parse("100.000"),
        minPrice: Price.Parse("1.00"),
        maxPrice: Price.Parse("500000.00"));

    private static RiskHarness Harness()
    {
        RiskHarness h = new();
        h.Cache.AddInstrument(Limited());
        return h;
    }

    private static void AssertRejected(RiskHarness h, string reasonPrefix)
    {
        Assert.Empty(h.Forwarded);
        Assert.Empty(h.Denied);
        OrderModifyRejected rejected = Assert.Single(h.ModifyRejected);
        Assert.StartsWith(reasonPrefix, rejected.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Valid_modification_of_an_open_order_is_forwarded()
    {
        RiskHarness h = Harness();
        LimitOrder order = h.AddAccepted(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));

        ModifyOrder command = h.Modify(order, quantity: "2.000", price: "49000.00");

        Assert.Same(command, Assert.Single(h.Forwarded));
        Assert.Empty(h.Events);
    }

    [Fact]
    public void Modification_of_an_order_still_in_flight_is_forwarded()
    {
        RiskHarness h = Harness();
        LimitOrder order = TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00");
        h.Cache.AddOrder(order);
        order.Apply(TestEvents.Submitted(order));

        ModifyOrder command = h.Modify(order, price: "49000.00");

        Assert.Same(command, Assert.Single(h.Forwarded));
    }

    [Fact]
    public void Modification_of_an_order_unknown_to_the_cache_is_dropped_without_an_event()
    {
        RiskHarness h = Harness();
        LimitOrder neverCached = TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00");

        h.Modify(neverCached, price: "49000.00");

        Assert.Empty(h.Forwarded);
        Assert.Empty(h.Events);
    }

    [Fact]
    public void Modification_of_a_closed_order_is_rejected()
    {
        RiskHarness h = Harness();
        LimitOrder order = h.AddAccepted(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));
        order.Apply(TestEvents.Canceled(order));

        h.Modify(order, price: "49000.00");

        AssertRejected(h, "order already closed");
    }

    [Fact]
    public void Modification_while_a_previous_update_is_pending_is_rejected()
    {
        RiskHarness h = Harness();
        LimitOrder order = h.AddAccepted(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));
        order.Apply(TestEvents.PendingUpdate(order));

        h.Modify(order, price: "49000.00");

        AssertRejected(h, "order already PendingUpdate");
    }

    [Fact]
    public void Modification_while_a_cancel_is_pending_is_rejected()
    {
        RiskHarness h = Harness();
        LimitOrder order = h.AddAccepted(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));
        order.Apply(TestEvents.PendingCancel(order));

        h.Modify(order, price: "49000.00");

        AssertRejected(h, "order already PendingCancel");
    }

    [Theory]
    [InlineData("1.0001", "QUANTITY_PRECISION")]
    [InlineData("0.000", "QUANTITY_ZERO")]
    [InlineData("100.001", "QUANTITY_EXCEEDS_MAX")]
    [InlineData("0.009", "QUANTITY_LESS_THAN_MIN")]
    public void New_quantity_is_checked_like_an_order_quantity(string quantity, string expectedReason)
    {
        RiskHarness h = Harness();
        LimitOrder order = h.AddAccepted(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));

        h.Modify(order, quantity: quantity);

        AssertRejected(h, expectedReason);
    }

    [Theory]
    [InlineData("100.000")]
    [InlineData("0.010")]
    public void New_quantity_on_a_limit_is_accepted(string quantity)
    {
        RiskHarness h = Harness();
        LimitOrder order = h.AddAccepted(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));

        ModifyOrder command = h.Modify(order, quantity: quantity);

        Assert.Same(command, Assert.Single(h.Forwarded));
    }

    [Theory]
    [InlineData("50000.001", "PRICE_PRECISION")]
    [InlineData("0.00", "PRICE_NOT_POSITIVE")]
    [InlineData("500000.01", "PRICE_EXCEEDS_MAX")]
    [InlineData("0.99", "PRICE_LESS_THAN_MIN")]
    public void New_price_is_checked_like_an_order_price(string price, string expectedReason)
    {
        RiskHarness h = Harness();
        LimitOrder order = h.AddAccepted(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));

        h.Modify(order, price: price);

        AssertRejected(h, expectedReason);
    }

    [Theory]
    [InlineData("50000.001", "PRICE_PRECISION")]
    [InlineData("-5.00", "PRICE_NOT_POSITIVE")]
    [InlineData("500000.01", "PRICE_EXCEEDS_MAX")]
    public void New_trigger_price_is_checked_like_an_order_price(string trigger, string expectedReason)
    {
        RiskHarness h = Harness();
        StopMarketOrder order = h.AddAccepted(TestOrders.StopMarket("O-1", TestIds.BtcUsdt, OrderSide.Sell, "1.000", "45000.00"));

        h.Modify(order, trigger: trigger);

        AssertRejected(h, expectedReason);
    }

    [Fact]
    public void Rejection_event_carries_the_order_identity_venue_id_and_account()
    {
        RiskHarness h = Harness();
        LimitOrder order = h.AddAccepted(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));

        h.Modify(order, quantity: "1000.000");

        OrderModifyRejected rejected = Assert.Single(h.ModifyRejected);
        Assert.Equal(order.ClientOrderId, rejected.ClientOrderId);
        Assert.Equal(new VenueOrderId("V-O-1"), rejected.VenueOrderId);
        Assert.Equal(TestIds.BinanceAccount, rejected.AccountId);
        Assert.Equal(TestIds.Strategy, rejected.StrategyId);
        Assert.Equal(h.Clock.Timestamp, rejected.TsEvent);
        Assert.Equal(1, h.Engine.DeniedCount);
    }

    [Fact]
    public void Rejection_leaves_the_order_open_when_applied()
    {
        RiskHarness h = Harness();
        LimitOrder order = h.AddAccepted(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));
        h.Modify(order, quantity: "1000.000");

        order.Apply(Assert.Single(h.ModifyRejected));

        Assert.Equal(OrderStatus.Accepted, order.Status);
        Assert.Equal(Quantity.Parse("1.000"), order.Quantity);
    }
}
