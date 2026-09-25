using Bytex.Adapters.Binance;
using Bytex.Adapters.Bybit;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Model.Identifiers;

namespace Bytex.Adapters.Tests;

// Why: a supervisor showed "market data disconnected" on a node whose prices were moving. One dropped socket marked the
// client disconnected, the socket reconnected by itself and resubscribed, and nothing ever marked the client connected
// again: the flag stayed wrong for the life of the node. On an execution client the same flag says whether orders can be
// trusted to reach the venue. Every client that reconnects by itself is held to the same rule here.
public sealed class ReconnectTests
{
    private const string BybitEmptyInstruments = """{"retCode":0,"retMsg":"OK","result":{"category":"spot","list":[],"nextPageCursor":""},"retExtInfo":{},"time":1}""";

    private static async Task AssertComesBackAsync(LoopbackServer server, Func<bool> isConnected, Func<Task<string>> nextEvent, int sockets = 1, int drop = 0)
    {
        List<WsSession> sessions = new();
        for (int i = 0; i < sockets; i++)
        {
            sessions.Add(await server.NextSessionAsync());
        }

        Assert.Equal("connected", await nextEvent());
        Assert.True(isConnected());

        sessions[drop].Drop();

        Assert.StartsWith("disconnected", await nextEvent(), StringComparison.Ordinal);
        await server.NextSessionAsync();
        Assert.Equal("connected", await nextEvent());
        Assert.True(isConnected());
    }

    [Fact]
    public async Task A_Bybit_data_client_is_connected_again_after_its_socket_reconnects()
    {
        await using LoopbackServer server = new(_ => StubResponse.Json(BybitEmptyInstruments));
        using TestKernel kernel = new();
        RecordingDataSink sink = new();
        BybitDataClient client = new(new ClientId("BYBIT"), new BybitDataClientConfig { BaseUrlHttp = server.HttpBase, BaseUrlWs = server.WsBase }, kernel.Services);
        client.AttachSink(sink);
        await client.ConnectAsync(CancellationToken.None).WaitAsync(Wait.Timeout);

        await AssertComesBackAsync(server, () => client.IsConnected, sink.NextConnectionEventAsync);

        await client.DisconnectAsync(CancellationToken.None);
        client.Dispose();
    }

    [Fact]
    public async Task A_Bybit_execution_client_is_connected_again_after_its_socket_reconnects()
    {
        await using LoopbackServer server = new(_ => StubResponse.Json(BybitEmptyInstruments));
        using TestKernel kernel = new();
        RecordingExecutionSink sink = new();
        BybitExecutionClient client = new(new ClientId("BYBIT"), new BybitExecutionClientConfig { ApiKey = "k", ApiSecret = "s", BaseUrlHttp = server.HttpBase, BaseUrlWs = server.WsBase }, kernel.Services);
        client.AttachSink(sink);
        await client.ConnectAsync(CancellationToken.None).WaitAsync(Wait.Timeout);

        await AssertComesBackAsync(server, () => client.IsConnected, sink.NextConnectionEventAsync);

        await client.DisconnectAsync(CancellationToken.None);
        client.Dispose();
    }

    [Fact]
    public async Task A_Binance_spot_data_client_is_connected_again_after_its_socket_reconnects()
    {
        await using LoopbackServer server = new(_ => StubResponse.Json("{}"));
        using TestKernel kernel = new();
        RecordingDataSink sink = new();
        BinanceDataClient client = new(new ClientId("BINANCE"), new BinanceDataClientConfig { AccountType = BinanceAccountType.Spot, BaseUrlHttp = server.HttpBase, BaseUrlWs = server.WsBase }, kernel.Services);
        client.AttachSink(sink);
        await client.ConnectAsync(CancellationToken.None).WaitAsync(Wait.Timeout);

        await AssertComesBackAsync(server, () => client.IsConnected, sink.NextConnectionEventAsync);

        await client.DisconnectAsync(CancellationToken.None);
        client.Dispose();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task A_Binance_futures_data_client_is_connected_again_when_the_route_that_dropped_is_back(int dropped)
    {
        await using LoopbackServer server = new(_ => StubResponse.Json("{}"));
        using TestKernel kernel = new();
        RecordingDataSink sink = new();
        BinanceDataClient client = new(new ClientId("BINANCE"), new BinanceDataClientConfig { AccountType = BinanceAccountType.UsdMFutures, BaseUrlHttp = server.HttpBase, BaseUrlWs = server.WsBase }, kernel.Services);
        client.AttachSink(sink);
        await client.ConnectAsync(CancellationToken.None).WaitAsync(Wait.Timeout);

        await AssertComesBackAsync(server, () => client.IsConnected, sink.NextConnectionEventAsync, sockets: 2, drop: dropped);

        await client.DisconnectAsync(CancellationToken.None);
        client.Dispose();
    }
}
