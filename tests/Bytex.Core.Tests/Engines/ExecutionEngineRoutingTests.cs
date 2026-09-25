using Bytex.Core.Engines;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Tests.Support;

namespace Bytex.Core.Tests.Engines;

// Why: the execution engine decides which client receives a command (explicit client id, then venue routing,
// then the default client) and must turn "cannot be routed" into an OrderDenied rather than silence.
public class ExecutionEngineRoutingTests
{
    [Fact]
    public void Submit_is_routed_to_the_client_of_the_order_venue_and_the_order_is_cached()
    {
        ExecHarness h = new();
        LimitOrder order = TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00");

        SubmitOrder command = h.Submit(order);

        Assert.Same(command, Assert.Single(h.Client.Commands));
        Assert.Same(order, h.Cache.Order(order.ClientOrderId));
        Assert.Equal(1, h.Engine.CommandCount);
    }

    [Fact]
    public void Submit_of_an_order_the_strategy_already_cached_does_not_fail()
    {
        ExecHarness h = new();
        LimitOrder order = TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00");
        h.Cache.AddOrder(order);

        h.Submit(order);

        Assert.Single(h.Client.Commands);
    }

    [Fact]
    public void Explicit_client_id_wins_over_venue_routing()
    {
        ExecHarness h = new();
        RecordingExecutionClient alternative = new(h.Services, TestIds.Binance, clientId: "BINANCE-ALT");
        h.Engine.RegisterClient(alternative);

        h.Submit(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"), clientId: new ClientId("BINANCE-ALT"));

        Assert.Empty(h.Client.Commands);
        Assert.Single(alternative.Commands);
    }

    [Fact]
    public void First_client_registered_for_a_venue_keeps_the_venue_routing()
    {
        ExecHarness h = new();
        RecordingExecutionClient second = new(h.Services, TestIds.Binance, clientId: "BINANCE-ALT");
        h.Engine.RegisterClient(second);

        h.Submit(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));

        Assert.Single(h.Client.Commands);
        Assert.Empty(second.Commands);
    }

    [Fact]
    public void Default_client_receives_orders_for_venues_without_a_client()
    {
        ExecHarness h = new(registerClient: false);
        h.Cache.AddInstrument(TestInstruments.BtcUsdtOnBybit());
        h.Engine.RegisterDefaultClient(h.Client);

        h.Submit(TestOrders.Limit("O-1", TestIds.BtcUsdtBybit, OrderSide.Buy, "1.000", "50000.00"));

        Assert.Single(h.Client.Commands);
        Assert.Contains(h.Client.ClientId, h.Engine.RegisteredClients);
    }

    [Fact]
    public void Explicit_venue_routing_sends_another_venue_to_an_existing_client()
    {
        ExecHarness h = new();
        h.Cache.AddInstrument(TestInstruments.BtcUsdtOnBybit());
        h.Engine.RegisterVenueRouting(TestIds.Bybit, h.Client);

        h.Submit(TestOrders.Limit("O-1", TestIds.BtcUsdtBybit, OrderSide.Buy, "1.000", "50000.00"));

        Assert.Single(h.Client.Commands);
    }

    [Fact]
    public void Submit_without_any_matching_client_denies_the_order()
    {
        ExecHarness h = new(registerClient: false);
        LimitOrder order = TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00");

        h.Submit(order);

        Assert.Equal(OrderStatus.Denied, order.Status);
        OrderDenied denied = Assert.Single(h.OrderEvents.Of<OrderDenied>());
        Assert.Contains("BINANCE", denied.Reason, StringComparison.Ordinal);
        Assert.True(h.Cache.IsOrderClosed(order.ClientOrderId));
    }

    [Fact]
    public void Submit_for_an_instrument_missing_from_the_cache_is_denied_by_default()
    {
        ExecHarness h = new();
        LimitOrder order = TestOrders.Limit("O-1", TestIds.EthBtc, OrderSide.Buy, "1.000", "0.05000");

        h.Submit(order);

        Assert.Empty(h.Client.Commands);
        Assert.Equal(OrderStatus.Denied, order.Status);
        Assert.Contains("ETHBTC.BINANCE", Assert.Single(h.OrderEvents.Of<OrderDenied>()).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_instruments_are_routed_when_the_configuration_allows_them()
    {
        ExecHarness h = new(new ExecutionEngineConfig { AllowUnknownInstruments = true });

        h.Submit(TestOrders.Limit("O-1", TestIds.EthBtc, OrderSide.Buy, "1.000", "0.05000"));

        Assert.Single(h.Client.Commands);
        Assert.Empty(h.OrderEvents.Messages);
    }

    [Fact]
    public void Registering_the_same_client_id_twice_is_an_error()
    {
        ExecHarness h = new();
        RecordingExecutionClient duplicate = new(h.Services, TestIds.Binance);

        Assert.Throws<InvalidOperationException>(() => h.Engine.RegisterClient(duplicate));
    }

    [Fact]
    public void Deregistered_client_no_longer_receives_commands()
    {
        ExecHarness h = new();
        h.Engine.DeregisterClient(h.Client);
        LimitOrder order = TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00");

        h.Submit(order);

        Assert.Empty(h.Client.Commands);
        Assert.Equal(OrderStatus.Denied, order.Status);
        Assert.Empty(h.Engine.RegisteredClients);
    }

    [Fact]
    public void Modify_and_query_are_passed_to_the_client_as_they_are()
    {
        ExecHarness h = new();
        LimitOrder order = h.SubmitAndAccept(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));
        h.Client.Commands.Clear();
        ModifyOrder modify = new(TestIds.Trader, TestIds.Strategy, TestIds.BtcUsdt, order.ClientOrderId, order.VenueOrderId, null, Price.Parse("49000.00"), null, null, Guid.NewGuid(), h.Clock.Timestamp);
        QueryOrder query = new(TestIds.Trader, TestIds.Strategy, TestIds.BtcUsdt, order.ClientOrderId, order.VenueOrderId, null, Guid.NewGuid(), h.Clock.Timestamp);

        h.Bus.Send(Endpoints.ExecutionEngineExecute, modify);
        h.Bus.Send(Endpoints.ExecutionEngineExecute, query);

        Assert.Equal(new TradingCommand[] { modify, query }, h.Client.Commands);
    }

    [Fact]
    public void Batch_cancel_reaches_the_client_as_one_cancel_per_order()
    {
        // RecordingExecutionClient keeps the base-class default for batch cancel, which fans out to CancelOrderAsync.
        ExecHarness h = new();
        LimitOrder a = h.SubmitAndAccept(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));
        LimitOrder b = h.SubmitAndAccept(TestOrders.Limit("O-2", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "49000.00"));
        h.Client.Commands.Clear();
        CancelOrder cancelA = new(TestIds.Trader, TestIds.Strategy, TestIds.BtcUsdt, a.ClientOrderId, a.VenueOrderId, null, Guid.NewGuid(), h.Clock.Timestamp);
        CancelOrder cancelB = new(TestIds.Trader, TestIds.Strategy, TestIds.BtcUsdt, b.ClientOrderId, b.VenueOrderId, null, Guid.NewGuid(), h.Clock.Timestamp);

        h.Bus.Send(Endpoints.ExecutionEngineExecute, new BatchCancelOrders(TestIds.Trader, TestIds.Strategy, TestIds.BtcUsdt, [cancelA, cancelB], null, Guid.NewGuid(), h.Clock.Timestamp));

        Assert.Equal(new TradingCommand[] { cancelA, cancelB }, h.Client.Commands);
    }

    [Fact]
    public void Cancel_of_an_open_order_is_routed_and_flagged_as_pending_cancel_locally()
    {
        ExecHarness h = new();
        LimitOrder order = h.SubmitAndAccept(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));
        CancelOrder cancel = new(TestIds.Trader, TestIds.Strategy, TestIds.BtcUsdt, order.ClientOrderId, order.VenueOrderId, null, Guid.NewGuid(), h.Clock.Timestamp);

        h.Bus.Send(Endpoints.ExecutionEngineExecute, cancel);

        Assert.Same(cancel, h.Client.Commands[^1]);
        Assert.True(h.Cache.IsOrderPendingCancelLocal(order.ClientOrderId));
        Assert.Equal(OrderStatus.Accepted, order.Status);
    }

    [Fact]
    public void Cancel_of_an_order_that_never_left_the_engine_is_completed_locally()
    {
        ExecHarness h = new();
        LimitOrder order = TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00");
        h.Cache.AddOrder(order);

        h.Bus.Send(Endpoints.ExecutionEngineExecute, new CancelOrder(TestIds.Trader, TestIds.Strategy, TestIds.BtcUsdt, order.ClientOrderId, null, null, Guid.NewGuid(), h.Clock.Timestamp));

        Assert.Empty(h.Client.Commands);
        Assert.Equal(OrderStatus.Canceled, order.Status);
        Assert.Single(h.OrderEvents.Of<OrderCanceled>());
    }

    [Fact]
    public void Cancel_of_a_closed_order_is_not_sent_to_the_venue()
    {
        ExecHarness h = new();
        LimitOrder order = h.SubmitAndAccept(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));
        h.Engine.Process(TestEvents.Canceled(order));
        h.Client.Commands.Clear();

        h.Bus.Send(Endpoints.ExecutionEngineExecute, new CancelOrder(TestIds.Trader, TestIds.Strategy, TestIds.BtcUsdt, order.ClientOrderId, order.VenueOrderId, null, Guid.NewGuid(), h.Clock.Timestamp));

        Assert.Empty(h.Client.Commands);
    }

    [Fact]
    public void Cancel_of_an_unknown_order_is_not_sent_to_the_venue()
    {
        ExecHarness h = new();

        h.Bus.Send(Endpoints.ExecutionEngineExecute, new CancelOrder(TestIds.Trader, TestIds.Strategy, TestIds.BtcUsdt, new ClientOrderId("O-404"), null, null, Guid.NewGuid(), h.Clock.Timestamp));

        Assert.Empty(h.Client.Commands);
        Assert.Empty(h.OrderEvents.Messages);
    }

    [Fact]
    public void Cancel_all_closes_local_orders_itself_and_asks_the_client_for_the_rest()
    {
        ExecHarness h = new();
        LimitOrder open = h.SubmitAndAccept(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));
        LimitOrder local = TestOrders.Limit("O-2", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "49000.00");
        LimitOrder otherStrategy = TestOrders.Limit("O-3", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "48000.00", TestIds.OtherStrategy);
        h.Cache.AddOrder(local);
        h.Cache.AddOrder(otherStrategy);
        h.Client.Commands.Clear();

        h.Bus.Send(Endpoints.ExecutionEngineExecute, new CancelAllOrders(TestIds.Trader, TestIds.Strategy, TestIds.BtcUsdt, null, null, Guid.NewGuid(), h.Clock.Timestamp));

        Assert.Equal(OrderStatus.Canceled, local.Status);
        Assert.Equal(OrderStatus.Initialized, otherStrategy.Status);
        // The base client turns CancelAll into one CancelOrder per open order of that strategy and instrument.
        CancelOrder sent = Assert.IsType<CancelOrder>(Assert.Single(h.Client.Commands));
        Assert.Equal(open.ClientOrderId, sent.ClientOrderId);
    }

    [Fact]
    public void Order_list_is_cached_with_all_its_orders_and_submitted_in_order()
    {
        ExecHarness h = new();
        MarketOrder entry = TestOrders.Market("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000");
        StopMarketOrder stop = TestOrders.StopMarket("O-2", TestIds.BtcUsdt, OrderSide.Sell, "1.000", "45000.00");
        OrderList list = new(new OrderListId("OL-1"), [entry, stop]);

        h.Bus.Send(Endpoints.ExecutionEngineExecute, new SubmitOrderList(TestIds.Trader, TestIds.Strategy, list, null, null, null, Guid.NewGuid(), h.Clock.Timestamp));

        Assert.Same(list, h.Cache.OrderList(list.Id));
        Assert.True(h.Cache.OrderExists(entry.ClientOrderId));
        Assert.True(h.Cache.OrderExists(stop.ClientOrderId));
        Assert.Equal(["O-1", "O-2"], h.Client.Received<SubmitOrder>().Select(c => c.Order.ClientOrderId.Value));
    }

    [Fact]
    public void Order_with_an_execution_algorithm_goes_to_the_algorithm_not_the_venue()
    {
        ExecHarness h = new();
        List<object> received = new();
        ExecAlgorithmId twap = new("TWAP");
        h.Engine.RegisterExecAlgorithm(twap, received.Add);
        MarketOrder order = MarketOrder.Create(TestOrders.Params("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", execAlgorithmId: twap));

        SubmitOrder command = h.Submit(order, execAlgorithmId: twap);

        Assert.Same(command, Assert.Single(received));
        Assert.Empty(h.Client.Commands);
    }

    [Fact]
    public void Child_order_spawned_by_an_algorithm_goes_to_the_venue()
    {
        ExecHarness h = new();
        List<object> received = new();
        ExecAlgorithmId twap = new("TWAP");
        h.Engine.RegisterExecAlgorithm(twap, received.Add);
        MarketOrder child = MarketOrder.Create(TestOrders.Params("O-1-E1", TestIds.BtcUsdt, OrderSide.Buy, "0.250", execAlgorithmId: twap, execSpawnId: new ClientOrderId("O-1")));

        h.Submit(child, execAlgorithmId: twap);

        Assert.Empty(received);
        Assert.Single(h.Client.Commands);
    }

    [Fact]
    public void Order_for_an_unregistered_execution_algorithm_is_denied()
    {
        ExecHarness h = new();
        ExecAlgorithmId twap = new("TWAP");
        MarketOrder order = MarketOrder.Create(TestOrders.Params("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", execAlgorithmId: twap));

        h.Submit(order, execAlgorithmId: twap);

        Assert.Empty(h.Client.Commands);
        Assert.Equal(OrderStatus.Denied, order.Status);
        Assert.Contains("TWAP", Assert.Single(h.OrderEvents.Of<OrderDenied>()).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Client_that_throws_does_not_take_the_engine_down()
    {
        ExecHarness h = new();
        h.Client.ThrowOnSubmit = true;
        LimitOrder failing = TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00");

        h.Engine.Execute(new SubmitOrder(TestIds.Trader, TestIds.Strategy, failing, null, null, null, Guid.NewGuid(), h.Clock.Timestamp));
        h.Client.ThrowOnSubmit = false;
        h.Submit(TestOrders.Limit("O-2", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));

        Assert.Equal("O-2", Assert.Single(h.Client.Received<SubmitOrder>()).Order.ClientOrderId.Value);
    }

    [Fact]
    public void Message_that_is_not_a_trading_command_is_ignored()
    {
        ExecHarness h = new();

        h.Bus.Send(Endpoints.ExecutionEngineExecute, "not a command");

        Assert.Empty(h.Client.Commands);
        Assert.Equal(1, h.Engine.CommandCount);
    }

    [Fact]
    public void Engine_lifecycle_drives_its_clients()
    {
        ExecHarness h = new();

        h.Engine.Start();
        ComponentState afterStart = h.Client.State;
        h.Engine.Stop();
        ComponentState afterStop = h.Client.State;
        h.Engine.Dispose();

        Assert.Equal(ComponentState.Running, afterStart);
        Assert.Equal(ComponentState.Stopped, afterStop);
        Assert.Equal(ComponentState.Disposed, h.Client.State);
        Assert.Empty(h.Engine.RegisteredClients);
    }
}
