using System.Collections.Concurrent;
using System.Threading.Channels;
using Bytex.Live.Network;
using Bytex.Live.Tests.Support;

namespace Bytex.Live.Tests.Network;

// Why: streams drop in production. The client must deliver whole messages in order, survive handler bugs,
// reconnect by itself and tell the adapter it did so, so subscriptions can be restored.
// The peer is a WebSocket stub on 127.0.0.1; nothing leaves the machine.
public sealed class WebSocketClientTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(15);

    private static WebSocketClientConfig Config(LoopbackServer server, string path = "/stream", string? ping = null, IReadOnlyDictionary<string, string>? headers = null) => new()
    {
        Url = new Uri(server.WsBase + path),
        ReconnectDelay = TimeSpan.FromMilliseconds(10),
        MaxReconnectDelay = TimeSpan.FromMilliseconds(20),
        PingInterval = TimeSpan.FromMilliseconds(50),
        PingMessage = ping,
        Headers = headers ?? new Dictionary<string, string>(),
    };

    [Fact]
    public async Task Connect_opens_the_configured_route_with_custom_headers_and_reports_a_first_connection()
    {
        await using LoopbackServer server = new();
        TaskCompletionSource<bool> connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using WebSocketClient client = new(Config(server, "/v5/private", headers: new Dictionary<string, string> { ["X-Test"] = "abc" }))
        {
            OnConnected = isReconnect =>
            {
                connected.TrySetResult(isReconnect);
                return Task.CompletedTask;
            },
        };

        await client.ConnectAsync().WaitAsync(_timeout);
        WsSession session = await server.NextSessionAsync();

        Assert.True(client.IsConnected);
        Assert.Equal("/v5/private", session.Path);
        Assert.Equal("abc", session.Headers["X-Test"]);
        Assert.False(await connected.Task.WaitAsync(_timeout));
    }

    [Fact]
    public async Task Text_messages_arrive_whole_and_in_order_even_when_the_server_fragments_them()
    {
        await using LoopbackServer server = new();
        Channel<string> received = Channel.CreateUnbounded<string>();
        await using WebSocketClient client = new(Config(server)) { OnText = text => received.Writer.WriteAsync(text).AsTask() };
        await client.ConnectAsync().WaitAsync(_timeout);
        WsSession session = await server.NextSessionAsync();

        await session.SendTextAsync("{\"n\":1}");
        await session.SendFragmentedTextAsync("{\"n\":", "2,\"pad\":\"", new string('x', 100_000), "\"}");
        await session.SendTextAsync("{\"n\":3}");

        using CancellationTokenSource cts = new(_timeout);
        string first = await received.Reader.ReadAsync(cts.Token);
        string second = await received.Reader.ReadAsync(cts.Token);
        string third = await received.Reader.ReadAsync(cts.Token);
        Assert.Equal("{\"n\":1}", first);
        Assert.Equal("{\"n\":2,\"pad\":\"" + new string('x', 100_000) + "\"}", second);
        Assert.Equal("{\"n\":3}", third);
    }

    [Fact]
    public async Task Binary_messages_go_to_the_binary_handler_only()
    {
        await using LoopbackServer server = new();
        TaskCompletionSource<byte[]> binary = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConcurrentBag<string> texts = new();
        await using WebSocketClient client = new(Config(server))
        {
            OnText = text =>
            {
                texts.Add(text);
                return Task.CompletedTask;
            },
            OnBinary = payload =>
            {
                binary.TrySetResult(payload);
                return Task.CompletedTask;
            },
        };
        await client.ConnectAsync().WaitAsync(_timeout);
        WsSession session = await server.NextSessionAsync();

        await session.SendBinaryAsync([1, 2, 3, 250]);

        Assert.Equal(new byte[] { 1, 2, 3, 250 }, await binary.Task.WaitAsync(_timeout));
        Assert.Empty(texts);
    }

    [Fact]
    public async Task Messages_sent_before_the_connection_exists_are_queued_and_delivered_in_order()
    {
        await using LoopbackServer server = new();
        await using WebSocketClient client = new(Config(server));

        client.SendText("first");
        client.SendText("second");
        await client.ConnectAsync().WaitAsync(_timeout);
        client.SendText("third");
        WsSession session = await server.NextSessionAsync();

        Assert.Equal("first", await session.ReceiveTextAsync());
        Assert.Equal("second", await session.ReceiveTextAsync());
        Assert.Equal("third", await session.ReceiveTextAsync());
    }

    [Fact]
    public async Task A_throwing_text_handler_does_not_stop_later_messages_from_being_delivered()
    {
        await using LoopbackServer server = new();
        TaskCompletionSource<string> delivered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using WebSocketClient client = new(Config(server))
        {
            OnText = text =>
            {
                if (text == "poison")
                {
                    throw new FormatException("handler bug");
                }

                delivered.TrySetResult(text);
                return Task.CompletedTask;
            },
        };
        await client.ConnectAsync().WaitAsync(_timeout);
        WsSession session = await server.NextSessionAsync();

        await session.SendTextAsync("poison");
        await session.SendTextAsync("healthy");

        Assert.Equal("healthy", await delivered.Task.WaitAsync(_timeout));
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task A_dropped_connection_is_reported_then_reopened_and_flagged_as_a_reconnection()
    {
        await using LoopbackServer server = new();
        Channel<string> events = Channel.CreateUnbounded<string>();
        await using WebSocketClient client = new(Config(server))
        {
            OnConnected = isReconnect => events.Writer.WriteAsync(isReconnect ? "reconnected" : "connected").AsTask(),
            OnDisconnected = _ => events.Writer.WriteAsync("disconnected").AsTask(),
        };
        await client.ConnectAsync().WaitAsync(_timeout);
        WsSession first = await server.NextSessionAsync();

        first.Drop();
        WsSession second = await server.NextSessionAsync();

        using CancellationTokenSource cts = new(_timeout);
        Assert.Equal("connected", await events.Reader.ReadAsync(cts.Token));
        Assert.Equal("disconnected", await events.Reader.ReadAsync(cts.Token));
        Assert.Equal("reconnected", await events.Reader.ReadAsync(cts.Token));
        Assert.Equal(first.Path, second.Path);
    }

    [Fact]
    public async Task After_a_reconnection_traffic_flows_in_both_directions_on_the_new_socket()
    {
        await using LoopbackServer server = new();
        Channel<string> received = Channel.CreateUnbounded<string>();
        TaskCompletionSource reconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using WebSocketClient client = new(Config(server))
        {
            OnText = text => received.Writer.WriteAsync(text).AsTask(),
            OnConnected = isReconnect =>
            {
                if (isReconnect)
                {
                    reconnected.TrySetResult();
                }

                return Task.CompletedTask;
            },
        };
        await client.ConnectAsync().WaitAsync(_timeout);
        WsSession first = await server.NextSessionAsync();
        first.Drop();
        WsSession second = await server.NextSessionAsync();
        await reconnected.Task.WaitAsync(_timeout);

        client.SendText("resubscribe");
        await second.SendTextAsync("tick");

        using CancellationTokenSource cts = new(_timeout);
        Assert.Equal("resubscribe", await second.ReceiveTextAsync());
        Assert.Equal("tick", await received.Reader.ReadAsync(cts.Token));
    }

    [Fact]
    public async Task The_application_level_ping_is_sent_repeatedly_while_connected()
    {
        await using LoopbackServer server = new();
        await using WebSocketClient client = new(Config(server, ping: "{\"op\":\"ping\"}"));
        await client.ConnectAsync().WaitAsync(_timeout);
        WsSession session = await server.NextSessionAsync();

        string first = await session.ReceiveTextAsync();
        string second = await session.ReceiveTextAsync();

        Assert.Equal("{\"op\":\"ping\"}", first);
        Assert.Equal("{\"op\":\"ping\"}", second);
    }

    [Fact]
    public async Task Disconnect_closes_the_socket_and_the_client_reports_not_connected()
    {
        await using LoopbackServer server = new();
        await using WebSocketClient client = new(Config(server));
        await client.ConnectAsync().WaitAsync(_timeout);
        await server.NextSessionAsync();

        await client.DisconnectAsync().WaitAsync(_timeout);

        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task Connecting_to_a_peer_that_refuses_the_upgrade_fails_instead_of_pretending_to_be_connected()
    {
        await using LoopbackServer server = new() { AcceptWebSockets = false };
        await using WebSocketClient client = new(Config(server));

        await Assert.ThrowsAsync<System.Net.WebSockets.WebSocketException>(() => client.ConnectAsync().WaitAsync(_timeout));

        Assert.False(client.IsConnected);
    }
}
