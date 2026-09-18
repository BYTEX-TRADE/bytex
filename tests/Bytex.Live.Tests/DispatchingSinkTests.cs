using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Live.Tests.Support;

namespace Bytex.Live.Tests;

// Why: adapters call their sink from socket threads. The dispatching sinks are the only thing standing between
// those threads and the single-threaded kernel, so every callback must be re-posted, unchanged and in order.
public sealed class DispatchingSinkTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(15);

    private sealed class RecordingSink : IDataClientSink, IExecutionClientSink
    {
        public List<(string Call, object? Argument, int ThreadId)> Calls { get; } = new();

        private void Record(string call, object? argument) => Calls.Add((call, argument, Environment.CurrentManagedThreadId));

        public void OnData(IData data) => Record("data", data);

        public void OnInstrument(Instrument instrument) => Record("instrument", instrument);

        public void OnResponse(DataResponse response) => Record("response", response);

        public void OnConnected(ClientId clientId) => Record("connected", clientId);

        public void OnDisconnected(ClientId clientId, string reason) => Record("disconnected", (clientId, reason));

        public void OnSubscriptionFailed(ClientId clientId, SubscribeCommand command, string reason) => Record("subscription-failed", (clientId, command, reason));

        public void OnOrderEvent(OrderEvent e) => Record("order-event", e);

        public void OnAccountState(AccountState state) => Record("account-state", state);
    }

    [Fact]
    public async Task Data_callbacks_are_replayed_on_the_loop_thread_in_call_order_with_the_same_arguments()
    {
        using LiveKernelLoop loop = new();
        loop.Start();
        RecordingSink inner = new();
        DispatchingDataSink sink = new(inner, loop);
        CurrencyPair instrument = TestInstruments.BtcUsdt();
        ClientId clientId = new("BINANCE");
        QuoteTick quote = TestInstruments.Quote(instrument, 1m, 2m, UnixNanos.FromSeconds(1));
        DataResponse response = new(Guid.NewGuid(), clientId, instrument.Venue, typeof(QuoteTick), [quote], UnixNanos.FromSeconds(2));
        SubscribeQuoteTicks command = new(instrument.Id, clientId, Guid.NewGuid(), UnixNanos.FromSeconds(3));

        sink.OnConnected(clientId);
        sink.OnInstrument(instrument);
        sink.OnData(quote);
        sink.OnResponse(response);
        sink.OnSubscriptionFailed(clientId, command, "not supported");
        sink.OnDisconnected(clientId, "socket closed");
        await loop.InvokeAsync(() => { }).WaitAsync(_timeout);

        Assert.Equal(["connected", "instrument", "data", "response", "subscription-failed", "disconnected"], inner.Calls.Select(c => c.Call));
        Assert.All(inner.Calls, c => Assert.Equal(loop.ManagedThreadId, c.ThreadId));
        Assert.Equal(clientId, inner.Calls[0].Argument);
        Assert.Same(instrument, inner.Calls[1].Argument);
        Assert.Equal(quote, inner.Calls[2].Argument);
        Assert.Same(response, inner.Calls[3].Argument);
        Assert.Equal((clientId, (SubscribeCommand)command, "not supported"), inner.Calls[4].Argument);
        Assert.Equal((clientId, "socket closed"), inner.Calls[5].Argument);
        await loop.StopAsync();
    }

    [Fact]
    public async Task Execution_callbacks_are_replayed_on_the_loop_thread_in_call_order_with_the_same_arguments()
    {
        using LiveKernelLoop loop = new();
        loop.Start();
        RecordingSink inner = new();
        DispatchingExecutionSink sink = new(inner, loop);
        ClientId clientId = new("BINANCE");
        AccountId accountId = new("BINANCE-001");
        OrderAccepted accepted = new(new TraderId("T-1"), new StrategyId("S-1"), TestInstruments.BtcUsdt().Id, new ClientOrderId("O-1"), new VenueOrderId("1"), accountId, Guid.NewGuid(), UnixNanos.FromSeconds(1), UnixNanos.FromSeconds(1));
        AccountState state = new(accountId, AccountType.Cash, null, true, [], [], new Dictionary<string, string>(), Guid.NewGuid(), UnixNanos.FromSeconds(2), UnixNanos.FromSeconds(2));

        sink.OnConnected(clientId);
        sink.OnAccountState(state);
        sink.OnOrderEvent(accepted);
        sink.OnDisconnected(clientId, "listen key expired");
        await loop.InvokeAsync(() => { }).WaitAsync(_timeout);

        Assert.Equal(["connected", "account-state", "order-event", "disconnected"], inner.Calls.Select(c => c.Call));
        Assert.All(inner.Calls, c => Assert.Equal(loop.ManagedThreadId, c.ThreadId));
        Assert.Same(state, inner.Calls[1].Argument);
        Assert.Same(accepted, inner.Calls[2].Argument);
        Assert.Equal((clientId, "listen key expired"), inner.Calls[3].Argument);
        await loop.StopAsync();
    }

    [Fact]
    public void Nothing_reaches_the_inner_sink_until_the_loop_is_running()
    {
        using LiveKernelLoop loop = new();
        RecordingSink inner = new();
        DispatchingDataSink sink = new(inner, loop);

        sink.OnConnected(new ClientId("BINANCE"));

        Assert.Empty(inner.Calls);
        Assert.Equal(1, loop.Pending);
    }
}
