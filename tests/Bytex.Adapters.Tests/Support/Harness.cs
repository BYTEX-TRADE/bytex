using System.Threading.Channels;
using Bytex.Core.Adapters;
using Bytex.Core.Kernel;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Timing;

namespace Bytex.Adapters.Tests.Support;

internal static class Wait
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public static async Task<T> NextAsync<T>(this ChannelReader<T> reader)
    {
        using CancellationTokenSource cts = new(Timeout);
        return await reader.ReadAsync(cts.Token);
    }
}

/// <summary>
/// A kernel on a fixed test clock, used only as the source of <see cref="KernelServices"/> for the adapters.
/// </summary>
internal sealed class TestKernel : IDisposable
{
    /// <summary>2023-11-14T22:13:20Z.</summary>
    public static readonly UnixNanos Now = UnixNanos.FromSeconds(1_700_000_000);

    public TestKernel()
    {
        Clock = new TestClock(Now);
        Kernel = new Kernel(new KernelConfig { Environment = TradingEnvironment.Live, TraderId = new TraderId("TESTER-001") }, Clock);
    }

    public TestClock Clock { get; }

    public Kernel Kernel { get; }

    public KernelServices Services => Kernel.Services;

    public void Dispose() => Kernel.Dispose();
}

internal sealed class RecordingDataSink : IDataClientSink
{
    private readonly Channel<IData> _data = Channel.CreateUnbounded<IData>();
    private readonly Channel<DataResponse> _responses = Channel.CreateUnbounded<DataResponse>();
    private readonly Channel<string> _connection = Channel.CreateUnbounded<string>();

    public List<Instrument> Instruments { get; } = new();

    public List<(SubscribeCommand Command, string Reason)> SubscriptionFailures { get; } = new();

    public Task<IData> NextDataAsync() => _data.Reader.NextAsync();

    public Task<DataResponse> NextResponseAsync() => _responses.Reader.NextAsync();

    public Task<string> NextConnectionEventAsync() => _connection.Reader.NextAsync();

    public void OnData(IData data) => _data.Writer.TryWrite(data);

    public void OnInstrument(Instrument instrument)
    {
        lock (Instruments)
        {
            Instruments.Add(instrument);
        }
    }

    public void OnResponse(DataResponse response) => _responses.Writer.TryWrite(response);

    public void OnConnected(ClientId clientId) => _connection.Writer.TryWrite("connected");

    public void OnDisconnected(ClientId clientId, string reason) => _connection.Writer.TryWrite("disconnected: " + reason);

    public void OnSubscriptionFailed(ClientId clientId, SubscribeCommand command, string reason) => SubscriptionFailures.Add((command, reason));
}

internal sealed class RecordingExecutionSink : IExecutionClientSink
{
    private readonly Channel<OrderEvent> _orderEvents = Channel.CreateUnbounded<OrderEvent>();
    private readonly Channel<AccountState> _accountStates = Channel.CreateUnbounded<AccountState>();
    private readonly Channel<string> _connection = Channel.CreateUnbounded<string>();
    private readonly List<OrderEvent> _all = new();

    public IReadOnlyList<OrderEvent> AllOrderEvents
    {
        get
        {
            lock (_all)
            {
                return _all.ToList();
            }
        }
    }

    public Task<OrderEvent> NextOrderEventAsync() => _orderEvents.Reader.NextAsync();

    public async Task<T> NextOrderEventAsync<T>()
        where T : OrderEvent => Assert.IsType<T>(await NextOrderEventAsync());

    public Task<AccountState> NextAccountStateAsync() => _accountStates.Reader.NextAsync();

    public Task<string> NextConnectionEventAsync() => _connection.Reader.NextAsync();

    public void OnOrderEvent(OrderEvent e)
    {
        lock (_all)
        {
            _all.Add(e);
        }

        _orderEvents.Writer.TryWrite(e);
    }

    public void OnAccountState(AccountState state) => _accountStates.Writer.TryWrite(state);

    public void OnConnected(ClientId clientId) => _connection.Writer.TryWrite("connected");

    public void OnDisconnected(ClientId clientId, string reason) => _connection.Writer.TryWrite("disconnected: " + reason);
}

/// <summary>
/// Routes stub requests by "METHOD /path"; unknown routes answer 404 so a wrong URL fails the test loudly.
/// </summary>
internal sealed class Routes
{
    private readonly Dictionary<string, Func<RecordedRequest, StubResponse>> _routes = new(StringComparer.Ordinal);

    public Routes On(string method, string path, Func<RecordedRequest, StubResponse> handler)
    {
        _routes[method + " " + path] = handler;
        return this;
    }

    public Routes On(string method, string path, string json) => On(method, path, _ => StubResponse.Json(json));

    public StubResponse Handle(RecordedRequest request) =>
        _routes.TryGetValue(request.Method + " " + request.Path, out Func<RecordedRequest, StubResponse>? handler)
            ? handler(request)
            : StubResponse.Error(404, $"{{\"code\":-1,\"msg\":\"no stub for {request.Method} {request.Path}\"}}");
}

internal static class Commands
{
    public static SubscribeQuoteTicks Quotes(InstrumentId id) => new(id, null, Guid.NewGuid(), TestKernel.Now);

    public static SubscribeTradeTicks Trades(InstrumentId id) => new(id, null, Guid.NewGuid(), TestKernel.Now);

    public static SubscribeBars Bars(BarType barType) => new(barType, null, Guid.NewGuid(), TestKernel.Now);

    public static SubscribeOrderBookDeltas Book(InstrumentId id, int depth = 0) => new(id, BookType.L2, depth, null, Guid.NewGuid(), TestKernel.Now);

    public static SubscribeMarkPrices Mark(InstrumentId id) => new(id, null, Guid.NewGuid(), TestKernel.Now);

    public static RequestBars RequestBars(BarType barType, UnixNanos? start, UnixNanos? end, int? limit) => new(barType, start, end, limit, null, Guid.NewGuid(), TestKernel.Now);

    public static RequestTradeTicks RequestTrades(InstrumentId id, UnixNanos? start = null, UnixNanos? end = null, int? limit = null) => new(id, start, end, limit, null, Guid.NewGuid(), TestKernel.Now);
}
