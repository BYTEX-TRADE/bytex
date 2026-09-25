using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
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

    // ----- asking after one order -----

    [Fact]
    public void A_query_brings_in_a_fill_the_node_never_saw_with_the_venues_own_trade_id()
    {
        // What asking is for. The venue filled the order and the fill never arrived, so this node holds it open at
        // nothing filled. Until this, querying it fetched the venue's answer and wrote it to the log, leaving the
        // order exactly as wrong as it was.
        ExecHarness h = new();
        LimitOrder order = h.SubmitAndAccept(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));
        h.OrderEvents.Messages.Clear();
        h.Client.OrderReport = Report("V-O-1", OrderStatus.Filled, "1.000", "1.000", clientOrderId: "O-1", avgPx: 50_000m);
        h.Client.Fills = [Fill("V-O-1", "T-VENUE-9", "1.000", "50000.00", 5)];

        h.Bus.Send(
            Endpoints.ExecutionEngineExecute,
            new QueryOrder(order.TraderId, order.StrategyId, order.InstrumentId, order.ClientOrderId, order.VenueOrderId, null, Guid.NewGuid(), h.Clock.Timestamp));

        Assert.Equal(OrderStatus.Filled, h.Cache.Order(order.ClientOrderId)!.Status);
        OrderFilled filled = Assert.Single(h.OrderEvents.Of<OrderFilled>());

        // The venue's trade id, not one worked out by subtraction: a fill nobody can tell from an invented one is a
        // fill nobody can audit.
        Assert.Equal(new TradeId("T-VENUE-9"), filled.TradeId);
        Assert.Equal(Quantity.Parse("1.000"), filled.LastQty);
        Assert.Equal(Price.Parse("50000.00"), filled.LastPx);
        Assert.True(filled.Reconciliation, "it did not arrive live, and a report has to be able to say so");
    }

    [Fact]
    public void A_query_about_an_order_the_venue_agrees_about_changes_nothing()
    {
        // The ordinary case, and the one that would make the command unusable if it were wrong: asking must not be
        // able to alter anything by itself. A strategy that polls an order would otherwise fill its own position.
        ExecHarness h = new();
        MarketOrder order = h.FillMarket("O-1", OrderSide.Buy, "1.000", "50000.00");
        h.OrderEvents.Messages.Clear();
        h.Client.OrderReport = Report("V-O-1", OrderStatus.Filled, "1.000", "1.000", clientOrderId: "O-1", type: OrderType.Market, price: null, avgPx: 50_000m);
        h.Client.Fills = [Fill("V-O-1", order.TradeIds[0].Value, "1.000", "50000.00", 1)];

        h.Bus.Send(
            Endpoints.ExecutionEngineExecute,
            new QueryOrder(order.TraderId, order.StrategyId, order.InstrumentId, order.ClientOrderId, order.VenueOrderId, null, Guid.NewGuid(), h.Clock.Timestamp));

        Assert.Empty(h.OrderEvents.TypeNames);
        Assert.Equal(Quantity.Parse("1.000"), h.Cache.Order(order.ClientOrderId)!.FilledQuantity);
    }

    [Fact]
    public void A_query_the_venue_cannot_answer_changes_nothing_either()
    {
        // A venue that has never heard of the order, or a client that cannot ask after one: nothing comes back, so
        // nothing is applied. What must not happen is the order being treated as gone.
        ExecHarness h = new();
        LimitOrder order = h.SubmitAndAccept(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));
        h.OrderEvents.Messages.Clear();
        h.Client.OrderReport = null;

        h.Bus.Send(
            Endpoints.ExecutionEngineExecute,
            new QueryOrder(order.TraderId, order.StrategyId, order.InstrumentId, order.ClientOrderId, order.VenueOrderId, null, Guid.NewGuid(), h.Clock.Timestamp));

        Assert.Empty(h.OrderEvents.TypeNames);
        Assert.Equal(OrderStatus.Accepted, h.Cache.Order(order.ClientOrderId)!.Status);
    }

    [Fact]
    public void A_query_is_not_counted_as_a_reconciliation_of_the_venue()
    {
        // One order is not a venue. A query that bumped the reconciliation count would make the figure a monitor
        // shows - "when was this node last reconciled" - mean something else.
        ExecHarness h = new();
        LimitOrder order = h.SubmitAndAccept(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));
        long reconciliations = h.Engine.ReconciliationCount;
        h.Client.OrderReport = Report("V-O-1", OrderStatus.Canceled, "1.000", "0.000", clientOrderId: "O-1");

        h.Bus.Send(
            Endpoints.ExecutionEngineExecute,
            new QueryOrder(order.TraderId, order.StrategyId, order.InstrumentId, order.ClientOrderId, order.VenueOrderId, null, Guid.NewGuid(), h.Clock.Timestamp));

        Assert.Equal(OrderStatus.Canceled, h.Cache.Order(order.ClientOrderId)!.Status);
        Assert.Equal(reconciliations, h.Engine.ReconciliationCount);
    }

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

    private static PositionStatusReport PositionReport(PositionSide side, string quantity, decimal? avgPx = 50_000m, InstrumentId? instrumentId = null) =>
        new(TestIds.BinanceAccount, instrumentId ?? TestIds.BtcUsdt, side, Quantity.Parse(quantity), At(9), At(10), Guid.NewGuid(), AvgPxOpen: avgPx);

    private static ExecutionMassStatus PositionsOnly(ExecHarness h, params PositionStatusReport[] positions) =>
        new(h.Client.ClientId, TestIds.BinanceAccount, TestIds.Binance, [], [], positions, At(10), Guid.NewGuid());

    // Why: a venue reporting a position the engine did not have was written to the log and then ignored. A node restarted
    // later than its reconciliation window, so that the fill which opened the position was no longer in the reports, came
    // up believing it was flat: it would size the next entry against nothing and manage risk it could not see.
    [Fact]
    public void A_reported_position_the_engine_does_not_have_is_adopted_from_the_venues_average_price()
    {
        ExecHarness h = new();

        h.Engine.ReconcileMassStatus(PositionsOnly(h, PositionReport(PositionSide.Long, "0.250", 61_000m)));

        Position position = Assert.Single(h.Cache.PositionsOpen());
        Assert.Equal((PositionSide.Long, 0.250m, 61_000m), (position.Side, position.Quantity.Value, position.AvgPxOpen));
        Order adopted = Assert.Single(h.Cache.Orders());
        Assert.StartsWith("RECON-POS-", adopted.ClientOrderId.Value, StringComparison.Ordinal);
        Assert.Equal(StrategyId.External, adopted.StrategyId);
        OrderFilled fill = Assert.Single(h.OrderEvents.Of<OrderFilled>());
        Assert.True(fill.Reconciliation);
        Assert.Equal(Money.Zero(Currencies.USDT), fill.Commission);
    }

    [Fact]
    public void An_adopted_position_belongs_to_the_strategy_that_claims_the_instrument()
    {
        ExecHarness h = new();
        h.Engine.RegisterExternalOrderClaims(new StrategyId("Doc-001"), [TestIds.BtcUsdt]);

        h.Engine.ReconcileMassStatus(PositionsOnly(h, PositionReport(PositionSide.Short, "2.000")));

        Position position = Assert.Single(h.Cache.PositionsOpen());
        Assert.Equal((PositionSide.Short, 2.000m, new StrategyId("Doc-001")), (position.Side, position.Quantity.Value, position.StrategyId));
    }

    [Fact]
    public void Only_the_difference_to_what_the_engine_already_holds_is_booked()
    {
        ExecHarness h = new();
        h.Engine.RegisterExternalOrderClaims(TestIds.Strategy, [TestIds.BtcUsdt]);
        h.FillMarket("O-1", OrderSide.Buy, "1.000", "50000.00");

        h.Engine.ReconcileMassStatus(PositionsOnly(h, PositionReport(PositionSide.Long, "1.500")));

        // The claiming strategy owns both, so netting leaves one position of the size the venue reports.
        Assert.Equal(1.500m, Assert.Single(h.Cache.PositionsOpen()).SignedQuantity);
        Assert.Equal(Quantity.Parse("0.500"), h.Cache.Orders().Single(o => o.ClientOrderId.Value.StartsWith("RECON-POS-", StringComparison.Ordinal)).FilledQuantity);
    }

    [Fact]
    public void A_position_the_engine_and_the_venue_agree_on_is_left_alone()
    {
        ExecHarness h = new();
        h.FillMarket("O-1", OrderSide.Buy, "1.000", "50000.00");
        int events = h.OrderEvents.Messages.Count;

        h.Engine.ReconcileMassStatus(PositionsOnly(h, PositionReport(PositionSide.Long, "1.000")));

        Assert.Equal(events, h.OrderEvents.Messages.Count);
        Assert.Single(h.Cache.Orders());
    }

    // A position cannot be booked without a price, and inventing one would put a wrong average price into every later
    // profit figure, so it is refused and said so in the log.
    [Fact]
    public void A_reported_position_with_no_price_anywhere_is_not_adopted()
    {
        ExecHarness h = new();

        h.Engine.ReconcileMassStatus(PositionsOnly(h, PositionReport(PositionSide.Long, "1.000", avgPx: null)));

        Assert.Empty(h.Cache.PositionsOpen());
        Assert.Empty(h.Cache.Orders());
    }

    [Fact]
    public void A_reported_position_on_an_unknown_instrument_is_not_adopted()
    {
        ExecHarness h = new();
        InstrumentId unknown = InstrumentId.Parse("SOLUSDT.BINANCE");

        h.Engine.ReconcileMassStatus(PositionsOnly(h, PositionReport(PositionSide.Long, "1.000", instrumentId: unknown)));

        Assert.Empty(h.Cache.PositionsOpen());
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

    // Why: R10.8 made the check periodic, and a check that runs for ever needs a number a host can watch rather than a
    // log nobody reads. Every time the engine takes the venue's word - a fill it never received, an order it had never
    // heard of, a position it was not carrying - is counted, and a count that keeps climbing while a node runs is the
    // node and the venue drifting apart.
    [Fact]
    public void Every_difference_the_engine_took_the_venues_word_on_is_counted()
    {
        ExecHarness h = new();
        LimitOrder order = h.SubmitAndAccept(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));

        Assert.Equal(0, h.Engine.ReconciliationCount);
        Assert.Equal(0, h.Engine.ReconciledDifferences);
        Assert.Null(h.Engine.LastReconciliation);

        // A fill the node never received, and an order it had never heard of, in one report.
        h.Engine.ReconcileMassStatus(new ExecutionMassStatus(
            h.Client.ClientId, TestIds.BinanceAccount, TestIds.Binance,
            [
                Report("V-O-1", OrderStatus.Filled, "1.000", "1.000", clientOrderId: "O-1"),
                Report("V-EXT", OrderStatus.Accepted, "0.400", "0.000", side: OrderSide.Sell, price: "60000.00"),
            ],
            [Fill("V-O-1", "T-1", "1.000", "50000.00", atSeconds: 2)],
            [],
            At(10), Guid.NewGuid()));

        Assert.Equal(1, h.Engine.ReconciliationCount);
        Assert.Equal(2, h.Engine.ReconciledDifferences);
        Assert.Equal(h.Clock.Timestamp, h.Engine.LastReconciliation);
        Assert.Equal(Quantity.Parse("1.000"), order.FilledQuantity);

        // The same report again says the same thing, so there is nothing new to take its word on.
        h.Engine.ReconcileMassStatus(new ExecutionMassStatus(
            h.Client.ClientId, TestIds.BinanceAccount, TestIds.Binance,
            [Report("V-O-1", OrderStatus.Filled, "1.000", "1.000", clientOrderId: "O-1")],
            [Fill("V-O-1", "T-1", "1.000", "50000.00", atSeconds: 2)],
            [],
            At(10), Guid.NewGuid()));

        Assert.Equal(2, h.Engine.ReconciliationCount);
        Assert.Equal(2, h.Engine.ReconciledDifferences);
    }
}
