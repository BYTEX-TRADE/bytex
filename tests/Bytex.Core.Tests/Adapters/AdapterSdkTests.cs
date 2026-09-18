using Bytex.Core.Adapters;
using Bytex.Core.Caching;
using Bytex.Core.Messaging;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Tests.Support;
using Bytex.Core.Timing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bytex.Core.Tests.Adapters;

// Why: R11.1 - venue adapters are built on these base classes. Their defaults (fan-out of lists and batch
// cancels, consistent event construction, "not supported" answers) are behaviour every adapter inherits.
public class AdapterSdkTests
{
    private sealed class RecordingExecSink : IExecutionClientSink
    {
        public List<object> Received { get; } = new();

        public void OnOrderEvent(OrderEvent e) => Received.Add(e);

        public void OnAccountState(AccountState state) => Received.Add(state);

        public void OnConnected(ClientId clientId) => Received.Add($"connected:{clientId}");

        public void OnDisconnected(ClientId clientId, string reason) => Received.Add($"disconnected:{clientId}:{reason}");
    }

    private sealed class RecordingDataSink : IDataClientSink
    {
        public List<object> Received { get; } = new();

        public void OnData(IData data) => Received.Add(data);

        public void OnInstrument(Instrument instrument) => Received.Add(instrument);

        public void OnResponse(DataResponse response) => Received.Add(response);

        public void OnConnected(ClientId clientId) => Received.Add($"connected:{clientId}");

        public void OnDisconnected(ClientId clientId, string reason) => Received.Add($"disconnected:{clientId}:{reason}");

        public void OnSubscriptionFailed(ClientId clientId, SubscribeCommand command, string reason) => Received.Add($"failed:{reason}");
    }

    private sealed class RecordingProvider : InstrumentProviderBase
    {
        public RecordingProvider(InstrumentProviderConfig config)
            : base(TestIds.Binance, config)
        {
        }

        public List<string> Calls { get; } = new();

        public override Task LoadAllAsync(CancellationToken ct, IReadOnlyDictionary<string, string>? filters = null)
        {
            Calls.Add("all:" + string.Join(',', (filters ?? new Dictionary<string, string>()).Select(kv => kv.Key + "=" + kv.Value)));
            return Task.CompletedTask;
        }

        public override Task LoadAsync(InstrumentId id, CancellationToken ct)
        {
            Calls.Add("one:" + id);
            return Task.CompletedTask;
        }
    }

    private static KernelServices Services(out TestClock clock, out Cache cache)
    {
        clock = new TestClock(TestOrders.T0);
        cache = new Cache();
        return new KernelServices(clock, cache, new MessageBus(TestIds.Trader), NullLoggerFactory.Instance, TestIds.Trader, TradingEnvironment.Backtest);
    }

    [Fact]
    public void Instrument_provider_indexes_instruments_and_collects_their_currencies()
    {
        InstrumentProviderBase provider = new(TestIds.Binance);
        CurrencyPair btc = TestInstruments.BtcUsdt();

        provider.Add(btc);
        provider.Add(TestInstruments.EthBtc());

        Assert.Equal(2, provider.Count);
        Assert.Same(btc, provider.Find(TestIds.BtcUsdt));
        Assert.Null(provider.Find(TestIds.EthUsdt));
        Assert.Equal(2, provider.GetAll().Count);
        Assert.Equal(["BTC", "ETH", "USDT"], provider.Currencies.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Instrument_provider_initialization_loads_everything_when_configured_to()
    {
        RecordingProvider provider = new(new InstrumentProviderConfig { LoadAll = true, LoadIds = [TestIds.BtcUsdt], Filters = new Dictionary<string, string> { ["quote"] = "USDT" } });

        await provider.InitializeAsync(CancellationToken.None);

        Assert.Equal(["all:quote=USDT"], provider.Calls);
    }

    [Fact]
    public async Task Instrument_provider_initialization_loads_the_configured_ids_in_order()
    {
        RecordingProvider provider = new(new InstrumentProviderConfig { LoadIds = [TestIds.EthUsdt, TestIds.BtcUsdt] });

        await provider.InitializeAsync(CancellationToken.None);

        Assert.Equal(["one:ETHUSDT.BINANCE", "one:BTCUSDT.BINANCE"], provider.Calls);
    }

    [Fact]
    public async Task Instrument_provider_initialization_does_nothing_without_configuration()
    {
        RecordingProvider provider = new(new InstrumentProviderConfig());

        await provider.InitializeAsync(CancellationToken.None);

        Assert.Empty(provider.Calls);
    }

    [Fact]
    public void Execution_client_is_ready_after_construction_and_named_after_its_client_id()
    {
        DefaultsExecutionClient client = new(Services(out _, out _), TestIds.Binance);

        Assert.Equal(ComponentState.Ready, client.State);
        Assert.Equal("ExecClient-BINANCE", client.Id.Value);
        Assert.Equal(new AccountId("BINANCE-001"), client.AccountId);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task Execution_client_without_a_sink_cannot_report_anything()
    {
        DefaultsExecutionClient client = new(Services(out _, out _), TestIds.Binance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync(CancellationToken.None));
        Assert.Throws<ArgumentNullException>(() => client.AttachSink(null!));
    }

    [Fact]
    public async Task Execution_client_connect_and_disconnect_update_the_flag_and_tell_the_sink()
    {
        DefaultsExecutionClient client = new(Services(out _, out _), TestIds.Binance);
        RecordingExecSink sink = new();
        client.AttachSink(sink);

        await client.ConnectAsync(CancellationToken.None);
        bool connected = client.IsConnected;
        await client.DisconnectAsync(CancellationToken.None);

        Assert.True(connected);
        Assert.False(client.IsConnected);
        Assert.Equal(new object[] { "connected:BINANCE", "disconnected:BINANCE:disconnect requested" }, sink.Received);
    }

    [Fact]
    public async Task Default_order_list_submission_submits_each_order_in_list_order()
    {
        DefaultsExecutionClient client = new(Services(out _, out _), TestIds.Binance);
        MarketOrder entry = TestOrders.Market("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000");
        StopMarketOrder stop = TestOrders.StopMarket("O-2", TestIds.BtcUsdt, OrderSide.Sell, "1.000", "45000.00");
        OrderList list = new(new OrderListId("OL-1"), [entry, stop]);
        PositionId positionId = new("P-1");

        await client.SubmitOrderListAsync(new SubmitOrderList(TestIds.Trader, TestIds.Strategy, list, positionId, null, null, Guid.NewGuid(), TestOrders.T0), CancellationToken.None);

        Assert.Equal(new Order[] { entry, stop }, client.Submits.Select(s => s.Order));
        Assert.All(client.Submits, s => Assert.Equal((TestIds.Strategy, positionId), (s.StrategyId, s.PositionId!.Value)));
    }

    [Fact]
    public async Task Default_cancel_all_cancels_the_open_orders_matching_strategy_instrument_and_side()
    {
        KernelServices services = Services(out _, out Cache cache);
        DefaultsExecutionClient client = new(services, TestIds.Binance);
        LimitOrder match = Open(cache, TestOrders.Limit("O-MATCH", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));
        Open(cache, TestOrders.Limit("O-SELL", TestIds.BtcUsdt, OrderSide.Sell, "1.000", "51000.00"));
        Open(cache, TestOrders.Limit("O-ETH", TestIds.EthUsdt, OrderSide.Buy, "1.000", "2500.00"));
        Open(cache, TestOrders.Limit("O-OTHER", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00", TestIds.OtherStrategy));
        cache.AddOrder(TestOrders.Limit("O-NOT-OPEN", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));

        await client.CancelAllOrdersAsync(new CancelAllOrders(TestIds.Trader, TestIds.Strategy, TestIds.BtcUsdt, OrderSide.Buy, null, Guid.NewGuid(), TestOrders.T0), CancellationToken.None);

        CancelOrder cancel = Assert.Single(client.Cancels);
        Assert.Equal((match.ClientOrderId, match.VenueOrderId), (cancel.ClientOrderId, cancel.VenueOrderId));
    }

    [Fact]
    public async Task Default_batch_cancel_forwards_every_cancel_unchanged()
    {
        DefaultsExecutionClient client = new(Services(out _, out _), TestIds.Binance);
        CancelOrder a = new(TestIds.Trader, TestIds.Strategy, TestIds.BtcUsdt, new ClientOrderId("O-1"), null, null, Guid.NewGuid(), TestOrders.T0);
        CancelOrder b = new(TestIds.Trader, TestIds.Strategy, TestIds.BtcUsdt, new ClientOrderId("O-2"), null, null, Guid.NewGuid(), TestOrders.T0);

        await client.BatchCancelOrdersAsync(new BatchCancelOrders(TestIds.Trader, TestIds.Strategy, TestIds.BtcUsdt, [a, b], null, Guid.NewGuid(), TestOrders.T0), CancellationToken.None);

        Assert.Equal([a, b], client.Cancels);
    }

    [Fact]
    public async Task Default_reports_are_empty_so_reconciliation_is_a_no_op_for_simple_clients()
    {
        DefaultsExecutionClient client = new(Services(out _, out _), TestIds.Binance);

        Assert.Null(await client.GenerateMassStatusAsync(null, CancellationToken.None));
        Assert.Null(await client.GenerateOrderStatusReportAsync(TestIds.BtcUsdt, null, null, CancellationToken.None));
        Assert.Empty(await client.GenerateOrderStatusReportsAsync(null, null, null, false, CancellationToken.None));
        Assert.Empty(await client.GenerateFillReportsAsync(null, null, null, null, CancellationToken.None));
        Assert.Empty(await client.GeneratePositionStatusReportsAsync(null, null, null, CancellationToken.None));
    }

    [Fact]
    public void Generated_fill_is_stamped_with_the_clients_trader_account_and_clock()
    {
        KernelServices services = Services(out TestClock clock, out _);
        RecordingExecutionClient client = new(services, TestIds.Binance);
        RecordingExecSink sink = new();
        client.AttachSink(sink);
        LimitOrder order = TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Sell, "1.000", "50000.00");
        clock.SetTime(TestOrders.T0 + TimeSpan.FromSeconds(9));

        client.EmitFilled(order, "T-1", "0.250", "50001.00", commission: "1.5");

        OrderFilled fill = Assert.IsType<OrderFilled>(Assert.Single(sink.Received));
        Assert.Equal((TestIds.Trader, TestIds.Strategy, TestIds.BtcUsdt, order.ClientOrderId), (fill.TraderId, fill.StrategyId, fill.InstrumentId, fill.ClientOrderId));
        Assert.Equal(new AccountId("BINANCE-001"), fill.AccountId);
        Assert.Equal((new TradeId("T-1"), OrderSide.Sell, OrderType.Limit), (fill.TradeId, fill.OrderSide, fill.OrderType));
        Assert.Equal((Quantity.Parse("0.250"), Price.Parse("50001.00"), Money.Parse("1.5 USDT")), (fill.LastQty, fill.LastPx, fill.Commission));
        Assert.Equal(TestOrders.T0 + TimeSpan.FromSeconds(9), fill.TsInit);
        Assert.False(fill.Reconciliation);
    }

    [Fact]
    public void Generated_account_state_carries_the_clients_account_identity()
    {
        RecordingExecutionClient client = new(Services(out _, out _), TestIds.Binance, accountType: AccountType.Margin);
        RecordingExecSink sink = new();
        client.AttachSink(sink);

        client.EmitAccountState(AccountBalance.Of(Money.Parse("1000 USDT"), Money.Parse("250 USDT")));

        AccountState state = Assert.IsType<AccountState>(Assert.Single(sink.Received));
        Assert.Equal((new AccountId("BINANCE-001"), AccountType.Margin, true), (state.AccountId, state.AccountType, state.Reported));
        Assert.Equal(Money.Parse("750 USDT"), Assert.Single(state.Balances).Free);
    }

    [Fact]
    public void Connection_notifications_from_an_adapter_update_the_flag()
    {
        RecordingExecutionClient client = new(Services(out _, out _), TestIds.Binance);
        RecordingExecSink sink = new();
        client.AttachSink(sink);

        client.SignalConnected();
        bool connected = client.IsConnected;
        client.SignalDisconnected("socket closed");

        Assert.True(connected);
        Assert.False(client.IsConnected);
        Assert.Equal("disconnected:BINANCE:socket closed", sink.Received[^1]);
    }

    [Fact]
    public async Task Data_client_connects_pushes_data_and_answers_unsupported_requests_with_an_error()
    {
        KernelServices services = Services(out _, out _);
        RecordingDataClient client = new(services, TestIds.Binance);
        RecordingDataSink sink = new();
        client.AttachSink(sink);
        QuoteTick quote = new(TestIds.BtcUsdt, Price.Parse("1.00"), Price.Parse("1.10"), Quantity.Parse("1.000"), Quantity.Parse("1.000"), TestOrders.T0, TestOrders.T0);
        RequestQuoteTicks request = new(TestIds.BtcUsdt, null, null, null, null, Guid.NewGuid(), TestOrders.T0) { Requester = new ActorId("A-001") };

        await client.ConnectAsync(CancellationToken.None);
        client.Push(quote);
        await client.RequestAsync(request, CancellationToken.None);
        await client.DisconnectAsync(CancellationToken.None);

        Assert.Equal("DataClient-BINANCE", client.Id.Value);
        Assert.Equal("connected:BINANCE", sink.Received[0]);
        Assert.Equal(quote, sink.Received[1]);
        DataResponse response = Assert.IsType<DataResponse>(sink.Received[2]);
        Assert.Equal((request.CommandId, new ActorId("A-001"), true), (response.CorrelationId, response.Requester!.Value, response.IsError));
        Assert.Equal("disconnected:BINANCE:disconnect requested", sink.Received[3]);
        Assert.False(client.IsConnected);
    }

    private static T Open<T>(Cache cache, T order) where T : Order
    {
        cache.AddOrder(order);
        order.Apply(TestEvents.Submitted(order));
        order.Apply(TestEvents.Accepted(order, "V-" + order.ClientOrderId.Value));
        cache.UpdateOrder(order);
        return order;
    }
}
