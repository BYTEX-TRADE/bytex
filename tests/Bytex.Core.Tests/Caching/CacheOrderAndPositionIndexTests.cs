using Bytex.Core.Caching;
using Bytex.Core.Model;
using Bytex.Core.Model.Accounts;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Positions;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Tests.Support;

namespace Bytex.Core.Tests.Caching;

// Why: R6.2 - engines and strategies query orders and positions through secondary indexes (status, venue,
// instrument, strategy, side). An index that lags behind the order state makes CancelAll miss orders.
public class CacheOrderAndPositionIndexTests
{
    private static T Track<T>(Cache cache, T order, params Func<Order, OrderEvent>[] events) where T : Order
    {
        cache.AddOrder(order);
        foreach (Func<Order, OrderEvent> make in events)
        {
            order.Apply(make(order));
            cache.UpdateOrder(order);
        }

        return order;
    }

    private static OrderEvent Submitted(Order o) => TestEvents.Submitted(o);

    private static OrderEvent Accepted(Order o) => TestEvents.Accepted(o, "V-" + o.ClientOrderId.Value);

    private static OrderEvent Canceled(Order o) => TestEvents.Canceled(o);

    private static OrderEvent PendingCancel(Order o) => TestEvents.PendingCancel(o);

    private static Position OpenPosition(Cache cache, Instrument instrument, string positionId, OrderSide side, string qty, string px, StrategyId? strategy = null, int tsSeconds = 0)
    {
        MarketOrder order = TestOrders.Market("O-" + positionId, instrument.Id, side, qty, strategy);
        UnixNanos ts = TestOrders.T0 + TimeSpan.FromSeconds(tsSeconds);
        Position position = new(instrument, TestEvents.Filled(order, "T-" + positionId, qty, px, positionId: new PositionId(positionId), ts: ts));
        cache.AddPosition(position);
        return position;
    }

    [Fact]
    public void Order_is_found_by_client_id_and_after_acceptance_by_venue_id()
    {
        Cache cache = new();
        LimitOrder order = Track(cache, TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"), Submitted, Accepted);

        Assert.Same(order, cache.Order(new ClientOrderId("O-1")));
        Assert.Same(order, cache.OrderForVenueId(new VenueOrderId("V-O-1")));
        Assert.Equal(new ClientOrderId("O-1"), cache.ClientOrderIdFor(new VenueOrderId("V-O-1")));
        Assert.Equal(new VenueOrderId("V-O-1"), cache.VenueOrderIdFor(new ClientOrderId("O-1")));
        Assert.Equal(TestIds.Strategy, cache.StrategyIdForOrder(order.ClientOrderId));
        Assert.Null(cache.Order(new ClientOrderId("O-404")));
        Assert.Null(cache.OrderForVenueId(new VenueOrderId("V-404")));
    }

    [Fact]
    public void Adding_the_same_client_order_id_twice_is_an_error()
    {
        Cache cache = new();
        cache.AddOrder(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));

        Assert.Throws<InvalidOperationException>(() => cache.AddOrder(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Sell, "2.000", "60000.00")));
    }

    [Fact]
    public void Status_indexes_follow_the_order_through_its_life()
    {
        Cache cache = new();
        LimitOrder order = TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00");
        ClientOrderId id = order.ClientOrderId;
        List<string> observed = new();

        void Observe(string stage) => observed.Add(
            $"{stage}: inflight={cache.IsOrderInflight(id)} open={cache.IsOrderOpen(id)} closed={cache.IsOrderClosed(id)}");

        cache.AddOrder(order);
        Observe("initialized");
        (string Stage, Func<Order, OrderEvent> Make)[] steps =
        [
            ("submitted", Submitted), ("accepted", Accepted), ("pending-cancel", PendingCancel), ("canceled", Canceled),
        ];
        foreach ((string stage, Func<Order, OrderEvent> make) in steps)
        {
            order.Apply(make(order));
            cache.UpdateOrder(order);
            Observe(stage);
        }

        Assert.Equal(
            [
                "initialized: inflight=False open=False closed=False",
                "submitted: inflight=True open=False closed=False",
                "accepted: inflight=False open=True closed=False",
                "pending-cancel: inflight=True open=True closed=False",
                "canceled: inflight=False open=False closed=True",
            ],
            observed);
    }

    [Fact]
    public void Emulated_index_holds_orders_only_while_they_are_emulated()
    {
        Cache cache = new();
        LimitOrder order = TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00");
        cache.AddOrder(order);

        order.Apply(new OrderEmulated(order.TraderId, order.StrategyId, order.InstrumentId, order.ClientOrderId, Guid.NewGuid(), TestOrders.T0, TestOrders.T0));
        cache.UpdateOrder(order);
        bool whileEmulated = cache.IsOrderEmulated(order.ClientOrderId);
        order.Apply(new OrderReleased(order.TraderId, order.StrategyId, order.InstrumentId, order.ClientOrderId, Price.Parse("50000.00"), Guid.NewGuid(), TestOrders.T0, TestOrders.T0));
        cache.UpdateOrder(order);

        Assert.True(whileEmulated);
        Assert.False(cache.IsOrderEmulated(order.ClientOrderId));
        Assert.Empty(cache.OrdersEmulated());
    }

    [Fact]
    public void Order_queries_filter_by_venue_instrument_strategy_and_side()
    {
        Cache cache = new();
        Track(cache, TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"), Submitted, Accepted);
        Track(cache, TestOrders.Limit("O-2", TestIds.BtcUsdt, OrderSide.Sell, "1.000", "51000.00"), Submitted, Accepted);
        Track(cache, TestOrders.Limit("O-3", TestIds.EthUsdt, OrderSide.Buy, "1.000", "2500.00", TestIds.OtherStrategy), Submitted, Accepted);
        Track(cache, TestOrders.Limit("O-4", TestIds.BtcUsdtBybit, OrderSide.Buy, "1.000", "50000.00"), Submitted, Accepted);
        Track(cache, TestOrders.Limit("O-5", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "49000.00"), Submitted, Accepted, Canceled);

        static IEnumerable<string> Ids(IEnumerable<Order> orders) => orders.Select(o => o.ClientOrderId.Value).Order(StringComparer.Ordinal);

        Assert.Equal(["O-1", "O-2", "O-3", "O-4"], Ids(cache.OrdersOpen()));
        Assert.Equal(["O-1", "O-2", "O-3"], Ids(cache.OrdersOpen(venue: TestIds.Binance)));
        Assert.Equal(["O-1", "O-2"], Ids(cache.OrdersOpen(instrumentId: TestIds.BtcUsdt)));
        Assert.Equal(["O-3"], Ids(cache.OrdersOpen(strategyId: TestIds.OtherStrategy)));
        Assert.Equal(["O-2"], Ids(cache.OrdersOpen(side: OrderSide.Sell)));
        Assert.Equal(["O-1"], Ids(cache.OrdersOpen(TestIds.Binance, TestIds.BtcUsdt, TestIds.Strategy, OrderSide.Buy)));
        Assert.Equal(["O-5"], Ids(cache.OrdersClosed()));
        Assert.Equal(["O-1", "O-5"], Ids(cache.Orders(instrumentId: TestIds.BtcUsdt, side: OrderSide.Buy)));
        Assert.Equal(4, cache.OrdersOpenCount());
        Assert.Equal(1, cache.OrdersClosedCount(strategyId: TestIds.Strategy));
        Assert.Equal(5, cache.OrdersTotalCount());
        Assert.Equal(0, cache.OrdersOpenCount(venue: TestIds.Bybit, side: OrderSide.Sell));
    }

    [Fact]
    public void Order_queries_return_orders_oldest_first()
    {
        Cache cache = new();
        cache.AddOrder(TestOrders.Limit("O-LATE", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "1.00", tsInit: TestOrders.T0 + TimeSpan.FromSeconds(20)));
        cache.AddOrder(TestOrders.Limit("O-EARLY", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "1.00", tsInit: TestOrders.T0 + TimeSpan.FromSeconds(10)));
        cache.AddOrder(TestOrders.Limit("O-MIDDLE", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "1.00", tsInit: TestOrders.T0 + TimeSpan.FromSeconds(15)));

        Assert.Equal(["O-EARLY", "O-MIDDLE", "O-LATE"], cache.Orders().Select(o => o.ClientOrderId.Value));
    }

    [Fact]
    public void Inflight_query_lists_submitted_and_pending_orders()
    {
        Cache cache = new();
        Track(cache, TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"), Submitted);
        Track(cache, TestOrders.Limit("O-2", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"), Submitted, Accepted);
        Track(cache, TestOrders.Limit("O-3", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"), Submitted, Accepted, PendingCancel);

        Assert.Equal(["O-1", "O-3"], cache.OrdersInflight().Select(o => o.ClientOrderId.Value).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Local_pending_cancel_flag_is_dropped_when_the_order_closes()
    {
        Cache cache = new();
        LimitOrder order = Track(cache, TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"), Submitted, Accepted);

        cache.UpdateOrderPendingCancelLocal(order);
        bool flagged = cache.IsOrderPendingCancelLocal(order.ClientOrderId);
        order.Apply(TestEvents.Canceled(order));
        cache.UpdateOrder(order);

        Assert.True(flagged);
        Assert.False(cache.IsOrderPendingCancelLocal(order.ClientOrderId));
    }

    [Fact]
    public void Orders_are_indexed_by_the_position_they_were_submitted_against()
    {
        Cache cache = new();
        MarketOrder closing = TestOrders.Market("O-CLOSE", TestIds.BtcUsdt, OrderSide.Sell, "1.000");
        MarketOrder unrelated = TestOrders.Market("O-OTHER", TestIds.BtcUsdt, OrderSide.Sell, "1.000");

        cache.AddOrder(closing, new PositionId("P-1"));
        cache.AddOrder(unrelated);

        Assert.Equal(new PositionId("P-1"), cache.PositionIdFor(closing.ClientOrderId));
        Assert.Same(closing, Assert.Single(cache.OrdersForPosition(new PositionId("P-1"))));
        Assert.Null(cache.PositionIdFor(unrelated.ClientOrderId));
        Assert.Empty(cache.OrdersForPosition(new PositionId("P-2")));
    }

    [Fact]
    public void Orders_are_indexed_by_execution_algorithm_and_spawning_order()
    {
        Cache cache = new();
        ExecAlgorithmId twap = new("TWAP");
        MarketOrder parent = MarketOrder.Create(TestOrders.Params("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", execAlgorithmId: twap));
        MarketOrder child = MarketOrder.Create(TestOrders.Params("O-1-E1", TestIds.BtcUsdt, OrderSide.Buy, "0.500", execAlgorithmId: twap, execSpawnId: parent.ClientOrderId));
        cache.AddOrder(parent);
        cache.AddOrder(child);
        cache.AddOrder(TestOrders.Market("O-2", TestIds.BtcUsdt, OrderSide.Buy, "1.000"));

        Assert.Equal(2, cache.OrdersForExecAlgorithm(twap).Count);
        Assert.Same(child, Assert.Single(cache.OrdersForExecSpawn(parent.ClientOrderId)));
        Assert.Empty(cache.OrdersForExecAlgorithm(new ExecAlgorithmId("VWAP")));
    }

    [Fact]
    public void Order_lists_are_stored_and_filtered()
    {
        Cache cache = new();
        OrderList btc = new(new OrderListId("OL-1"), [TestOrders.Market("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000")]);
        OrderList eth = new(new OrderListId("OL-2"), [TestOrders.Market("O-2", TestIds.EthUsdt, OrderSide.Buy, "1.000", TestIds.OtherStrategy)]);
        cache.AddOrderList(btc);
        cache.AddOrderList(eth);

        Assert.Same(btc, cache.OrderList(new OrderListId("OL-1")));
        Assert.True(cache.OrderListExists(new OrderListId("OL-2")));
        Assert.False(cache.OrderListExists(new OrderListId("OL-3")));
        Assert.Same(eth, Assert.Single(cache.OrderLists(strategyId: TestIds.OtherStrategy)));
        Assert.Same(btc, Assert.Single(cache.OrderLists(instrumentId: TestIds.BtcUsdt)));
        Assert.Equal(2, cache.OrderLists(venue: TestIds.Binance).Count);
    }

    [Fact]
    public void Position_indexes_follow_the_position_from_open_to_closed()
    {
        Cache cache = new();
        CurrencyPair instrument = TestInstruments.BtcUsdt();
        Position position = OpenPosition(cache, instrument, "P-1", OrderSide.Buy, "1.000", "50000.00");
        bool openBefore = cache.IsPositionOpen(position.Id);

        MarketOrder closing = TestOrders.Market("O-CLOSE", TestIds.BtcUsdt, OrderSide.Sell, "1.000");
        position.Apply(TestEvents.Filled(closing, "T-CLOSE", "1.000", "51000.00", positionId: position.Id));
        cache.UpdatePosition(position);

        Assert.True(openBefore);
        Assert.False(cache.IsPositionOpen(position.Id));
        Assert.True(cache.IsPositionClosed(position.Id));
        Assert.Same(position, Assert.Single(cache.PositionsClosed()));
        Assert.Empty(cache.PositionsOpen());
        Assert.Same(position, cache.PositionForOrder(closing.ClientOrderId));
        Assert.Equal(TestIds.Strategy, cache.StrategyIdForPosition(position.Id));
    }

    [Fact]
    public void Adding_the_same_position_id_twice_is_an_error()
    {
        Cache cache = new();
        CurrencyPair instrument = TestInstruments.BtcUsdt();
        OpenPosition(cache, instrument, "P-1", OrderSide.Buy, "1.000", "50000.00");

        Assert.Throws<InvalidOperationException>(() => OpenPosition(cache, instrument, "P-1", OrderSide.Buy, "1.000", "50000.00"));
    }

    [Fact]
    public void Position_queries_filter_by_venue_instrument_strategy_and_side_oldest_first()
    {
        Cache cache = new();
        OpenPosition(cache, TestInstruments.BtcUsdt(), "P-3", OrderSide.Buy, "1.000", "50000.00", tsSeconds: 30);
        OpenPosition(cache, TestInstruments.BtcUsdt(), "P-1", OrderSide.Sell, "1.000", "50000.00", TestIds.OtherStrategy, tsSeconds: 10);
        OpenPosition(cache, TestInstruments.EthUsdt(), "P-2", OrderSide.Buy, "1.000", "2500.00", tsSeconds: 20);
        OpenPosition(cache, TestInstruments.BtcPerp(), "P-4", OrderSide.Sell, "1.000", "50000.0", tsSeconds: 40);

        static IEnumerable<string> Ids(IEnumerable<Position> positions) => positions.Select(p => p.Id.Value);

        Assert.Equal(["P-1", "P-2", "P-3", "P-4"], Ids(cache.PositionsOpen()));
        Assert.Equal(["P-1", "P-2", "P-3"], Ids(cache.PositionsOpen(venue: TestIds.Binance)));
        Assert.Equal(["P-1", "P-3"], Ids(cache.PositionsOpen(instrumentId: TestIds.BtcUsdt)));
        Assert.Equal(["P-1"], Ids(cache.PositionsOpen(strategyId: TestIds.OtherStrategy)));
        Assert.Equal(["P-1", "P-4"], Ids(cache.PositionsOpen(side: PositionSide.Short)));
        Assert.Equal(["P-3"], Ids(cache.Positions(TestIds.Binance, TestIds.BtcUsdt, TestIds.Strategy, PositionSide.Long)));
        Assert.Equal(4, cache.PositionsOpenCount());
        Assert.Equal(0, cache.PositionsClosedCount());
        Assert.Equal(2, cache.PositionsTotalCount(side: PositionSide.Long));
        Assert.True(cache.PositionExists(new PositionId("P-4")));
        Assert.False(cache.PositionExists(new PositionId("P-5")));
    }

    [Fact]
    public void Accounts_are_found_by_id_and_by_venue()
    {
        Cache cache = new();
        CashAccount binance = new(TestEvents.CashState(TestIds.BinanceAccount, (Currencies.USDT, 100m, 0m)));
        MarginAccount bybit = new(TestEvents.MarginState(TestIds.BybitAccount, (Currencies.USDT, 200m, 0m)));
        cache.AddAccount(binance);
        cache.AddAccount(bybit);

        Assert.Same(binance, cache.Account(TestIds.BinanceAccount));
        Assert.Same(bybit, cache.AccountForVenue(TestIds.Bybit));
        Assert.Equal(TestIds.BinanceAccount, cache.AccountIdFor(TestIds.Binance));
        Assert.Null(cache.AccountForVenue(new Venue("KRAKEN")));
        Assert.Equal(2, cache.Accounts().Count);
    }

    [Fact]
    public void General_and_actor_state_storage_round_trip()
    {
        Cache cache = new();
        ActorId actor = new("Monitor-001");
        Dictionary<string, byte[]> state = new() { ["k"] = [1, 2, 3] };

        cache.Add("blob", [9, 8]);
        cache.SaveActorState(actor, state);

        Assert.Equal(new byte[] { 9, 8 }, cache.Get("blob"));
        Assert.Null(cache.Get("missing"));
        Assert.Same(state, cache.LoadActorState(actor));
        Assert.Null(cache.LoadActorState(new ActorId("Other-001")));
    }

    [Fact]
    public void Reset_clears_orders_positions_accounts_and_every_index()
    {
        Cache cache = new();
        LimitOrder order = Track(cache, TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"), Submitted, Accepted);
        OpenPosition(cache, TestInstruments.BtcUsdt(), "P-1", OrderSide.Buy, "1.000", "50000.00");
        cache.AddAccount(new CashAccount(TestEvents.CashState(TestIds.BinanceAccount, (Currencies.USDT, 100m, 0m))));
        cache.Add("blob", [1]);

        cache.Reset();

        Assert.Empty(cache.Orders());
        Assert.Empty(cache.OrdersOpen());
        Assert.Null(cache.OrderForVenueId(order.VenueOrderId!.Value));
        Assert.Empty(cache.Positions());
        Assert.Empty(cache.PositionsOpen());
        Assert.Empty(cache.Accounts());
        Assert.Null(cache.AccountForVenue(TestIds.Binance));
        Assert.Null(cache.Get("blob"));
    }

    [Fact]
    public void Integrity_check_passes_for_maintained_indexes_and_detects_a_stale_one()
    {
        Cache cache = new();
        LimitOrder order = Track(cache, TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"), Submitted, Accepted);
        bool healthy = cache.CheckIntegrity();

        // The order closes but nobody tells the cache: the open index now lies.
        order.Apply(TestEvents.Canceled(order));

        Assert.True(healthy);
        Assert.False(cache.CheckIntegrity());
    }
}
