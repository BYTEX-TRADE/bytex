using Bytex.Core.Adapters;
using Bytex.Core.Caching;
using Bytex.Core.Messaging;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Tests.Support;
using Bytex.Core.Timing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bytex.Core.Tests.Adapters;

// Why: a bracket is an entry and the exits that only make sense once the entry is on. Sending all three to the venue
// together leaves a stop and a take-profit working against a position that does not exist - on a live venue they can
// fill on their own, opening the position they were meant to close. The base class therefore holds the children until
// the entry fills, releases them through the execution engine so they are judged like any other order, and forgets
// them if the entry never fills.
//
// That hold shipped in 0.6.0 with no test at all: it was read in review and left at that. Nothing here needs a venue
// or a key - it is base-class behaviour every adapter inherits, and this is what it has to do.
public sealed class HeldBracketTests
{
    private static KernelServices Services(out TestClock clock, out MessageBus bus)
    {
        clock = new TestClock(TestOrders.T0);
        bus = new MessageBus(TestIds.Trader);
        return new KernelServices(clock, new Cache(), bus, NullLoggerFactory.Instance, TestIds.Trader, TradingEnvironment.Sandbox);
    }

    private sealed class Sink : IExecutionClientSink
    {
        private readonly List<string>? _trace;

        public Sink(List<string>? trace = null)
        {
            _trace = trace;
        }

        public List<OrderEvent> Events { get; } = new();

        public void OnOrderEvent(OrderEvent e)
        {
            Events.Add(e);

            // Recorded as it arrives, in the same list the released children go into, so their order is observed
            // rather than assembled afterwards by the test.
            if (e is OrderFilled filled)
            {
                _trace?.Add("filled:" + filled.ClientOrderId);
            }
        }

        public void OnAccountState(AccountState state)
        {
        }

        public void OnConnected(ClientId clientId)
        {
        }

        public void OnDisconnected(ClientId clientId, string reason)
        {
        }
    }

    /// <summary>An entry and the two exits that trigger off it, as the order factory builds a bracket.</summary>
    private static (MarketOrder Entry, StopMarketOrder StopLoss, LimitOrder TakeProfit) Bracket(string tag = "B")
    {
        ClientOrderId parent = new($"{tag}-ENTRY");
        MarketOrder entry = MarketOrder.Create(
            TestOrders.Params($"{tag}-ENTRY", TestIds.BtcUsdt, OrderSide.Buy, "1.000", contingency: ContingencyType.Oto));
        StopMarketOrder stopLoss = StopMarketOrder.Create(
            TestOrders.Params($"{tag}-SL", TestIds.BtcUsdt, OrderSide.Sell, "1.000", contingency: ContingencyType.Ouo, parent: parent),
            Price.Parse("45000.00"));
        LimitOrder takeProfit = LimitOrder.Create(
            TestOrders.Params($"{tag}-TP", TestIds.BtcUsdt, OrderSide.Sell, "1.000", contingency: ContingencyType.Ouo, parent: parent),
            Price.Parse("55000.00"));
        return (entry, stopLoss, takeProfit);
    }

    private static SubmitOrderList Submit(OrderList list, PositionId? positionId = null) =>
        new(TestIds.Trader, TestIds.Strategy, list, positionId, null, null, Guid.NewGuid(), TestOrders.T0);

    private static OrderList List(params Order[] orders) => new(new OrderListId("OL-1"), orders);

    /// <summary>What the execution engine is asked to submit, which is where released children go.</summary>
    private static List<SubmitOrder> WatchTheEngine(MessageBus bus)
    {
        List<SubmitOrder> released = new();
        bus.Register(Endpoints.ExecutionEngineExecute, message =>
        {
            if (message is SubmitOrder submit)
            {
                released.Add(submit);
            }
        });

        return released;
    }

    [Fact]
    public async Task Only_the_entry_of_a_bracket_reaches_the_venue()
    {
        RecordingExecutionClient client = new(Services(out _, out _), TestIds.Binance);
        client.AttachSink(new Sink());
        (MarketOrder entry, StopMarketOrder stopLoss, LimitOrder takeProfit) = Bracket();

        await client.SubmitOrderListAsync(Submit(List(entry, stopLoss, takeProfit)), CancellationToken.None);

        SubmitOrder sent = Assert.Single(client.Received<SubmitOrder>());
        Assert.Equal(entry.ClientOrderId, sent.Order.ClientOrderId);
    }

    [Fact]
    public async Task The_exits_go_out_when_the_entry_fills()
    {
        KernelServices services = Services(out _, out MessageBus bus);
        RecordingExecutionClient client = new(services, TestIds.Binance);
        Sink sink = new();
        client.AttachSink(sink);
        List<SubmitOrder> released = WatchTheEngine(bus);
        (MarketOrder entry, StopMarketOrder stopLoss, LimitOrder takeProfit) = Bracket();
        PositionId positionId = new("P-1");
        await client.SubmitOrderListAsync(Submit(List(entry, stopLoss, takeProfit), positionId), CancellationToken.None);

        client.EmitFilled(entry, "T-1", "1.000", "50000.00");

        Assert.Equal(
            new[] { stopLoss.ClientOrderId, takeProfit.ClientOrderId },
            released.Select(r => r.Order.ClientOrderId));

        // They go back through the execution engine, not straight at the venue: a child released into an account that
        // can no longer carry it has to be refused rather than quietly worked.
        Assert.Equal(entry.ClientOrderId, Assert.Single(client.Received<SubmitOrder>()).Order.ClientOrderId);

        // And they carry the list's own routing, or the exits would be filed against the wrong position.
        Assert.All(released, r => Assert.Equal((TestIds.Trader, TestIds.Strategy, positionId), (r.TraderId, r.StrategyId, r.PositionId!.Value)));
    }

    [Fact]
    public async Task The_fill_is_reported_before_the_exits_go_out()
    {
        // Order matters to anything watching: the entry must be seen as on before the orders that protect it appear,
        // or a listener sees exits for a position it has not been told about.
        KernelServices services = Services(out _, out MessageBus bus);
        RecordingExecutionClient client = new(services, TestIds.Binance);
        List<string> sequence = new();
        client.AttachSink(new Sink(sequence));
        bus.Register(Endpoints.ExecutionEngineExecute, message => sequence.Add("released:" + ((SubmitOrder)message).Order.ClientOrderId));
        (MarketOrder entry, StopMarketOrder stopLoss, LimitOrder takeProfit) = Bracket();
        await client.SubmitOrderListAsync(Submit(List(entry, stopLoss, takeProfit)), CancellationToken.None);

        client.EmitFilled(entry, "T-1", "1.000", "50000.00");

        Assert.Equal(
            new[] { "filled:" + entry.ClientOrderId, "released:" + stopLoss.ClientOrderId, "released:" + takeProfit.ClientOrderId },
            sequence);
    }

    public static TheoryData<string> EndingsThatAreNotAFill() => new("canceled", "rejected", "expired");

    [Theory]
    [MemberData(nameof(EndingsThatAreNotAFill))]
    public async Task An_entry_that_ends_any_other_way_takes_its_exits_with_it(string ending)
    {
        KernelServices services = Services(out _, out MessageBus bus);
        RecordingExecutionClient client = new(services, TestIds.Binance);
        client.AttachSink(new Sink());
        List<SubmitOrder> released = WatchTheEngine(bus);
        (MarketOrder entry, StopMarketOrder stopLoss, LimitOrder takeProfit) = Bracket();
        await client.SubmitOrderListAsync(Submit(List(entry, stopLoss, takeProfit)), CancellationToken.None);

        switch (ending)
        {
            case "canceled":
                client.EmitCanceled(entry);
                break;
            case "rejected":
                client.EmitRejected(entry, "insufficient margin");
                break;
            default:
                client.EmitExpired(entry);
                break;
        }

        Assert.Empty(released);

        // The probe for "forgotten" rather than "not yet released": a fill arriving for an order the venue has already
        // closed must find nothing waiting. If the children were still held, they would go out here - against a
        // position the strategy has been told it does not have.
        client.EmitFilled(entry, "T-LATE", "1.000", "50000.00");
        Assert.Empty(released);
    }

    [Fact]
    public async Task A_list_that_is_not_one_triggers_other_goes_to_the_venue_whole()
    {
        // One-cancels-other and one-updates-other are sets of orders that belong at the venue together: holding any
        // of them back would leave a position unprotected.
        RecordingExecutionClient client = new(Services(out _, out _), TestIds.Binance);
        client.AttachSink(new Sink());
        StopMarketOrder stopLoss = StopMarketOrder.Create(
            TestOrders.Params("X-SL", TestIds.BtcUsdt, OrderSide.Sell, "1.000", contingency: ContingencyType.Oco, linked: [new ClientOrderId("X-TP")]),
            Price.Parse("45000.00"));
        LimitOrder takeProfit = LimitOrder.Create(
            TestOrders.Params("X-TP", TestIds.BtcUsdt, OrderSide.Sell, "1.000", contingency: ContingencyType.Oco, linked: [new ClientOrderId("X-SL")]),
            Price.Parse("55000.00"));

        await client.SubmitOrderListAsync(Submit(List(stopLoss, takeProfit)), CancellationToken.None);

        Assert.Equal(
            new[] { stopLoss.ClientOrderId, takeProfit.ClientOrderId },
            client.Received<SubmitOrder>().Select(s => s.Order.ClientOrderId));
    }

    [Fact]
    public async Task An_order_with_no_parent_is_not_held_even_in_a_bracket()
    {
        // Only what waits on something is held. An order in the same list that triggers off nothing has nothing to
        // wait for, so holding it would strand it.
        RecordingExecutionClient client = new(Services(out _, out _), TestIds.Binance);
        client.AttachSink(new Sink());
        (MarketOrder entry, StopMarketOrder stopLoss, _) = Bracket();
        LimitOrder unrelated = TestOrders.Limit("B-OTHER", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "40000.00");

        await client.SubmitOrderListAsync(Submit(List(entry, stopLoss, unrelated)), CancellationToken.None);

        Assert.Equal(
            new[] { entry.ClientOrderId, unrelated.ClientOrderId },
            client.Received<SubmitOrder>().Select(s => s.Order.ClientOrderId));
    }

    [Fact]
    public async Task One_entry_filling_releases_only_its_own_exits()
    {
        // Two brackets at once is the ordinary case for a strategy that runs more than one instrument, and the hold
        // is keyed by parent for exactly that reason.
        KernelServices services = Services(out _, out MessageBus bus);
        RecordingExecutionClient client = new(services, TestIds.Binance);
        client.AttachSink(new Sink());
        List<SubmitOrder> released = WatchTheEngine(bus);
        (MarketOrder firstEntry, StopMarketOrder firstStop, LimitOrder firstTarget) = Bracket("ONE");
        (MarketOrder secondEntry, StopMarketOrder secondStop, LimitOrder secondTarget) = Bracket("TWO");
        await client.SubmitOrderListAsync(Submit(List(firstEntry, firstStop, firstTarget)), CancellationToken.None);
        await client.SubmitOrderListAsync(Submit(List(secondEntry, secondStop, secondTarget)), CancellationToken.None);

        client.EmitFilled(secondEntry, "T-2", "1.000", "50000.00");

        Assert.Equal(
            new[] { secondStop.ClientOrderId, secondTarget.ClientOrderId },
            released.Select(r => r.Order.ClientOrderId));
    }
}
