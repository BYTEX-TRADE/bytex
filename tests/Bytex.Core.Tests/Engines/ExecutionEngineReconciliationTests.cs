using Bytex.Core.Model;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Positions;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Model.Reports;
using Bytex.Core.Tests.Support;

namespace Bytex.Core.Tests.Engines;

// Why: R10.3/R3.14 - after a restart the cache must be brought in line with what the venue reports: missed
// events are generated (flagged as reconciliation), unknown venue orders are adopted, and nothing is applied twice.
public class ExecutionEngineReconciliationTests
{
    private static UnixNanos At(int seconds) => TestOrders.T0 + TimeSpan.FromSeconds(seconds);

    private static OrderStatusReport Report(
        string venueOrderId,
        OrderStatus status,
        string quantity,
        string filled,
        string? clientOrderId = null,
        OrderSide side = OrderSide.Buy,
        OrderType type = OrderType.Limit,
        string? price = "50000.00",
        string? trigger = null,
        decimal? avgPx = null,
        InstrumentId? instrumentId = null,
        string? cancelReason = null,
        UnixNanos? tsTriggered = null) => new(
            TestIds.BinanceAccount,
            instrumentId ?? TestIds.BtcUsdt,
            clientOrderId is null ? null : new ClientOrderId(clientOrderId),
            new VenueOrderId(venueOrderId),
            side,
            type,
            TimeInForce.Gtc,
            status,
            Quantity.Parse(quantity),
            Quantity.Parse(filled),
            At(1),
            At(9),
            At(10),
            Guid.NewGuid(),
            Price: price is null ? null : Price.Parse(price),
            TriggerPrice: trigger is null ? null : Price.Parse(trigger),
            AvgPx: avgPx,
            CancelReason: cancelReason,
            TsTriggered: tsTriggered);

    private static FillReport Fill(string venueOrderId, string tradeId, string quantity, string price, int atSeconds, OrderSide side = OrderSide.Buy, string commission = "0") => new(
        TestIds.BinanceAccount, TestIds.BtcUsdt, new VenueOrderId(venueOrderId), new TradeId(tradeId), side, Quantity.Parse(quantity), Price.Parse(price),
        Money.Parse($"{commission} USDT"), LiquiditySide.Maker, At(atSeconds), At(atSeconds), Guid.NewGuid());

    [Fact]
    public void Venue_order_unknown_to_the_cache_is_adopted_as_an_external_order()
    {
        ExecHarness h = new();

        h.Engine.ReconcileOrderReport(Report("V-100", OrderStatus.Accepted, "1.000", "0.000"), []);

        Order order = Assert.Single(h.Cache.Orders());
        Assert.Equal(new ClientOrderId("O-V-100"), order.ClientOrderId);
        Assert.Equal(StrategyId.External, order.StrategyId);
        Assert.Equal(OrderStatus.Accepted, order.Status);
        Assert.Equal(OrderType.Limit, order.Type);
        Assert.Equal(Price.Parse("50000.00"), order.Price);
        Assert.Equal(At(1), order.TsAccepted);
        Assert.Same(order, h.Cache.OrderForVenueId(new VenueOrderId("V-100")));
        Assert.Equal(["OrderInitialized", "OrderAccepted"], h.OrderEvents.TypeNames);
        Assert.True(h.OrderEvents.Of<OrderAccepted>()[0].Reconciliation);
    }

    [Fact]
    public void External_order_on_a_claimed_instrument_belongs_to_the_claiming_strategy()
    {
        ExecHarness h = new();
        h.Engine.RegisterExternalOrderClaims(TestIds.Strategy, [TestIds.BtcUsdt]);

        h.Engine.ReconcileOrderReport(Report("V-100", OrderStatus.Accepted, "1.000", "0.000"), []);

        Assert.Equal(TestIds.Strategy, Assert.Single(h.Cache.Orders()).StrategyId);
    }

    [Fact]
    public void Instrument_can_be_claimed_by_only_one_strategy()
    {
        ExecHarness h = new();
        h.Engine.RegisterExternalOrderClaims(TestIds.Strategy, [TestIds.BtcUsdt]);

        h.Engine.RegisterExternalOrderClaims(TestIds.Strategy, [TestIds.BtcUsdt]);
        Assert.Throws<InvalidOperationException>(() => h.Engine.RegisterExternalOrderClaims(TestIds.OtherStrategy, [TestIds.BtcUsdt]));
    }

    [Fact]
    public void Report_with_the_client_order_id_of_an_external_order_keeps_that_id()
    {
        ExecHarness h = new();

        h.Engine.ReconcileOrderReport(Report("V-100", OrderStatus.Accepted, "1.000", "0.000", clientOrderId: "MANUAL-1"), []);

        Assert.True(h.Cache.OrderExists(new ClientOrderId("MANUAL-1")));
    }

    [Fact]
    public void External_order_for_an_instrument_missing_from_the_cache_is_skipped()
    {
        ExecHarness h = new();

        h.Engine.ReconcileOrderReport(Report("V-100", OrderStatus.Accepted, "1.000", "0.000", instrumentId: TestIds.EthBtc), []);

        Assert.Empty(h.Cache.Orders());
        Assert.Empty(h.OrderEvents.Messages);
    }

    [Fact]
    public void Cached_order_still_in_flight_is_accepted_from_the_report()
    {
        ExecHarness h = new();
        LimitOrder order = TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00");
        h.Submit(order);
        h.Engine.Process(TestEvents.Submitted(order));

        h.Engine.ReconcileOrderReport(Report("V-100", OrderStatus.Accepted, "1.000", "0.000", clientOrderId: "O-1"), []);

        Assert.Equal(OrderStatus.Accepted, order.Status);
        Assert.Equal(new VenueOrderId("V-100"), order.VenueOrderId);
        Assert.Single(h.Cache.Orders());
    }

    [Fact]
    public void Cached_order_is_found_by_venue_order_id_when_the_report_has_no_client_order_id()
    {
        ExecHarness h = new();
        LimitOrder order = h.SubmitAndAccept(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));

        h.Engine.ReconcileOrderReport(Report("V-O-1", OrderStatus.Canceled, "1.000", "0.000"), []);

        Assert.Equal(OrderStatus.Canceled, order.Status);
        Assert.Single(h.Cache.Orders());
    }

    [Fact]
    public void Reported_rejection_rejects_the_in_flight_order_with_the_venue_reason()
    {
        ExecHarness h = new();
        LimitOrder order = TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00");
        h.Submit(order);
        h.Engine.Process(TestEvents.Submitted(order));

        h.Engine.ReconcileOrderReport(Report("V-100", OrderStatus.Rejected, "1.000", "0.000", clientOrderId: "O-1", cancelReason: "INSUFFICIENT_MARGIN"), []);

        Assert.Equal(OrderStatus.Rejected, order.Status);
        OrderRejected rejected = Assert.Single(h.OrderEvents.Of<OrderRejected>());
        Assert.Equal("INSUFFICIENT_MARGIN", rejected.Reason);
        Assert.True(rejected.Reconciliation);
    }

    [Fact]
    public void Fill_reports_are_applied_in_time_order_and_open_a_position()
    {
        ExecHarness h = new();
        LimitOrder order = h.SubmitAndAccept(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50010.00"));
        FillReport later = Fill("V-O-1", "T-LATE", "0.400", "50000.00", atSeconds: 5);
        FillReport earlier = Fill("V-O-1", "T-EARLY", "0.600", "50010.00", atSeconds: 3);

        h.Engine.ReconcileOrderReport(Report("V-O-1", OrderStatus.Filled, "1.000", "1.000", clientOrderId: "O-1", price: "50010.00"), [later, earlier]);

        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal([new TradeId("T-EARLY"), new TradeId("T-LATE")], order.TradeIds);
        // (0.6 * 50,010 + 0.4 * 50,000) / 1 = 50,006
        Assert.Equal(50_006m, order.AvgPx);
        Position position = Assert.Single(h.Cache.PositionsOpen());
        Assert.Equal(Quantity.Parse("1.000"), position.Quantity);
        Assert.Equal(50_006m, position.AvgPxOpen);
        Assert.All(h.OrderEvents.Of<OrderFilled>(), f => Assert.True(f.Reconciliation));
    }

    [Fact]
    public void Filled_quantity_without_fill_reports_is_synthesised_at_the_reported_average_price()
    {
        ExecHarness h = new();
        LimitOrder order = h.SubmitAndAccept(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));

        h.Engine.ReconcileOrderReport(Report("V-O-1", OrderStatus.PartiallyFilled, "1.000", "0.400", clientOrderId: "O-1", avgPx: 49_990m), []);

        Assert.Equal(OrderStatus.PartiallyFilled, order.Status);
        Assert.Equal(Quantity.Parse("0.400"), order.FilledQuantity);
        Assert.Equal(49_990m, order.AvgPx);
        OrderFilled synthetic = Assert.Single(h.OrderEvents.Of<OrderFilled>());
        Assert.StartsWith("RECON-V-O-1", synthetic.TradeId.Value, StringComparison.Ordinal);
        Assert.Equal(Money.Parse("0 USDT"), synthetic.Commission);
        Assert.Equal(0.4m, Assert.Single(h.Cache.PositionsOpen()).SignedQuantity);
    }

    [Fact]
    public void Only_the_quantity_missing_after_the_fill_reports_is_synthesised()
    {
        ExecHarness h = new();
        LimitOrder order = h.SubmitAndAccept(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));

        h.Engine.ReconcileOrderReport(
            Report("V-O-1", OrderStatus.PartiallyFilled, "1.000", "0.700", clientOrderId: "O-1", avgPx: 50_000m),
            [Fill("V-O-1", "T-1", "0.500", "50000.00", atSeconds: 2)]);

        // 0.700 reported - 0.500 from the fill report = 0.200 synthesised.
        IReadOnlyList<OrderFilled> fills = h.OrderEvents.Of<OrderFilled>();
        Assert.Equal([Quantity.Parse("0.500"), Quantity.Parse("0.200")], fills.Select(f => f.LastQty));
        Assert.Equal(Quantity.Parse("0.700"), order.FilledQuantity);
    }

    [Fact]
    public void Reconciling_the_same_mass_status_twice_changes_nothing_the_second_time()
    {
        ExecHarness h = new();
        LimitOrder order = h.SubmitAndAccept(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));
        ExecutionMassStatus status = new(
            h.Client.ClientId, TestIds.BinanceAccount, TestIds.Binance,
            [Report("V-O-1", OrderStatus.Filled, "1.000", "1.000", clientOrderId: "O-1")],
            [Fill("V-O-1", "T-1", "1.000", "50000.00", atSeconds: 2, commission: "4")],
            [new PositionStatusReport(TestIds.BinanceAccount, TestIds.BtcUsdt, PositionSide.Long, Quantity.Parse("1.000"), At(9), At(10), Guid.NewGuid())],
            At(10), Guid.NewGuid());

        h.Engine.ReconcileMassStatus(status);
        int eventsAfterFirst = h.OrderEvents.Messages.Count + h.PositionEvents.Messages.Count;
        h.Engine.ReconcileMassStatus(status);

        Assert.Equal(eventsAfterFirst, h.OrderEvents.Messages.Count + h.PositionEvents.Messages.Count);
        Assert.Equal(Quantity.Parse("1.000"), order.FilledQuantity);
        Assert.Equal(Money.Parse("4 USDT"), order.Commissions[Currencies.USDT]);
        Assert.Equal(1.000m, Assert.Single(h.Cache.PositionsOpen()).SignedQuantity);
    }

    [Fact]
    public void Mass_status_pairs_each_order_report_with_its_own_fills()
    {
        ExecHarness h = new();
        LimitOrder a = h.SubmitAndAccept(TestOrders.Limit("O-A", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));
        LimitOrder b = h.SubmitAndAccept(TestOrders.Limit("O-B", TestIds.BtcUsdt, OrderSide.Buy, "2.000", "49000.00"));
        ExecutionMassStatus status = new(
            h.Client.ClientId, TestIds.BinanceAccount, TestIds.Binance,
            [
                Report("V-O-A", OrderStatus.Filled, "1.000", "1.000", clientOrderId: "O-A"),
                Report("V-O-B", OrderStatus.PartiallyFilled, "2.000", "0.500", clientOrderId: "O-B", price: "49000.00"),
            ],
            [Fill("V-O-B", "T-B", "0.500", "49000.00", atSeconds: 4), Fill("V-O-A", "T-A", "1.000", "50000.00", atSeconds: 2)],
            [],
            At(10), Guid.NewGuid());

        h.Engine.ReconcileMassStatus(status);

        Assert.Equal([new TradeId("T-A")], a.TradeIds);
        Assert.Equal([new TradeId("T-B")], b.TradeIds);
        Assert.Equal(OrderStatus.Filled, a.Status);
        Assert.Equal(OrderStatus.PartiallyFilled, b.Status);
    }

    [Theory]
    [InlineData(OrderStatus.Canceled)]
    [InlineData(OrderStatus.Expired)]
    public void Reported_terminal_status_closes_the_open_order(OrderStatus reported)
    {
        ExecHarness h = new();
        LimitOrder order = h.SubmitAndAccept(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));

        h.Engine.ReconcileOrderReport(Report("V-O-1", reported, "1.000", "0.000", clientOrderId: "O-1"), []);

        Assert.Equal(reported, order.Status);
        Assert.True(h.Cache.IsOrderClosed(order.ClientOrderId));
        Assert.Equal(At(9), order.TsClosed);
    }

    [Fact]
    public void Quantity_and_price_changed_at_the_venue_are_taken_over()
    {
        ExecHarness h = new();
        LimitOrder order = h.SubmitAndAccept(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));

        h.Engine.ReconcileOrderReport(Report("V-O-1", OrderStatus.Accepted, "2.000", "0.000", clientOrderId: "O-1", price: "49500.00"), []);

        Assert.Equal(Quantity.Parse("2.000"), order.Quantity);
        Assert.Equal(Price.Parse("49500.00"), order.Price);
        Assert.True(Assert.Single(h.OrderEvents.Of<OrderUpdated>()).Reconciliation);
    }

    [Fact]
    public void Report_that_matches_the_cached_order_generates_no_events()
    {
        ExecHarness h = new();
        h.SubmitAndAccept(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));
        h.OrderEvents.Messages.Clear();

        h.Engine.ReconcileOrderReport(Report("V-O-1", OrderStatus.Accepted, "1.000", "0.000", clientOrderId: "O-1"), []);

        Assert.Empty(h.OrderEvents.Messages);
    }

    [Fact]
    public void Reported_trigger_time_triggers_an_accepted_stop_order()
    {
        ExecHarness h = new();
        StopLimitOrder order = h.SubmitAndAccept(TestOrders.StopLimit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "51000.00", "50900.00"));

        h.Engine.ReconcileOrderReport(
            Report("V-O-1", OrderStatus.Triggered, "1.000", "0.000", clientOrderId: "O-1", type: OrderType.StopLimit, price: "51000.00", trigger: "50900.00", tsTriggered: At(7)), []);

        Assert.Equal(OrderStatus.Triggered, order.Status);
        Assert.Equal(At(7), Assert.Single(h.OrderEvents.Of<OrderTriggered>()).TsEvent);
    }

    [Fact]
    public void External_stop_order_is_rebuilt_with_its_trigger_price()
    {
        ExecHarness h = new();

        h.Engine.ReconcileOrderReport(Report("V-200", OrderStatus.Accepted, "1.000", "0.000", side: OrderSide.Sell, type: OrderType.StopMarket, price: null, trigger: "45000.00"), []);

        StopMarketOrder order = Assert.IsType<StopMarketOrder>(Assert.Single(h.Cache.Orders()));
        Assert.Equal(Price.Parse("45000.00"), order.TriggerPrice);
        Assert.Equal(OrderSide.Sell, order.Side);
    }
}
