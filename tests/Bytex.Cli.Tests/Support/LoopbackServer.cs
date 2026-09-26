using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace Bytex.Cli.Tests.Support;

/// <summary>
/// One HTTP request as the stub venue received it.
/// </summary>
internal sealed record RecordedRequest(string Method, string Path, string RawQuery, IReadOnlyDictionary<string, string> Headers, string Body)
{
    /// <summary>Decoded query parameters in the order they were sent.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> QueryPairs { get; } = RawQuery.Length == 0
        ? []
        : RawQuery.Split('&').Select(p => p.Split('=', 2)).Select(kv => new KeyValuePair<string, string>(Uri.UnescapeDataString(kv[0]), kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : string.Empty)).ToList();

    public string? Query(string name) => QueryPairs.Where(p => p.Key == name).Select(p => p.Value).FirstOrDefault();

    public string? Header(string name) => Headers.TryGetValue(name, out string? value) ? value : null;
}

internal sealed record StubResponse(int Status, string Body, string ContentType = "application/json", byte[]? Bytes = null)
{
    public static StubResponse Json(string body) => new(200, body);

    public static StubResponse Error(int status, string body) => new(status, body);

    public static StubResponse Binary(byte[] bytes) => new(200, string.Empty, "application/octet-stream", bytes);
}

/// <summary>
/// A WebSocket connection accepted by the stub venue.
/// </summary>
internal sealed class WsSession
{
    private readonly TcpClient _tcp;
    private readonly WebSocket _socket;
    private readonly Channel<string> _received = Channel.CreateUnbounded<string>();

    public WsSession(string path, IReadOnlyDictionary<string, string> headers, TcpClient tcp, WebSocket socket)
    {
        Path = path;
        Headers = headers;
        _tcp = tcp;
        _socket = socket;
        _ = Task.Run(ReceiveLoopAsync);
    }

    public string Path { get; }

    public IReadOnlyDictionary<string, string> Headers { get; }

    public Task SendTextAsync(string text) =>
        _socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

    /// <summary>Sends one text message split into several WebSocket frames.</summary>
    public async Task SendFragmentedTextAsync(params string[] fragments)
    {
        for (int i = 0; i < fragments.Length; i++)
        {
            await _socket.SendAsync(Encoding.UTF8.GetBytes(fragments[i]), WebSocketMessageType.Text, endOfMessage: i == fragments.Length - 1, CancellationToken.None);
        }
    }

    public Task SendBinaryAsync(byte[] payload) =>
        _socket.SendAsync(payload, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);

    /// <summary>Next text message sent by the client; fails the test after the timeout.</summary>
    public async Task<string> ReceiveTextAsync(TimeSpan? timeout = null)
    {
        using CancellationTokenSource cts = new(timeout ?? TimeSpan.FromSeconds(15));
        return await _received.Reader.ReadAsync(cts.Token);
    }

    /// <summary>Drops the TCP connection without a close handshake, as a network failure would.</summary>
    public void Drop() => _tcp.Close();

    private async Task ReceiveLoopAsync()
    {
        byte[] buffer = new byte[16 * 1024];
        using MemoryStream message = new();
        try
        {
            while (_socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result = await _socket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    break;
                }

                message.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    _received.Writer.TryWrite(Encoding.UTF8.GetString(message.ToArray()));
                    message.SetLength(0);
                }
            }
        }
        catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException or SocketException)
        {
            // Connection ended; nothing more to record.
        }
        finally
        {
            _received.Writer.TryComplete();
        }
    }
}

/// <summary>
/// A stub venue bound to 127.0.0.1 on an ephemeral port. It speaks just enough HTTP/1.1 and WebSocket for the
/// clients under test, records every request, and never touches the network beyond the loopback interface.
/// </summary>
internal sealed class LoopbackServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<WsSession> _sessions = Channel.CreateUnbounded<WsSession>();
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();
    private readonly Task _acceptLoop;

    public LoopbackServer(Func<RecordedRequest, StubResponse>? handler = null)
    {
        Handler = handler ?? (_ => StubResponse.Error(404, "{}"));
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public string HttpBase => $"http://127.0.0.1:{Port}";

    public string WsBase => $"ws://127.0.0.1:{Port}";

    public Func<RecordedRequest, StubResponse> Handler { get; set; }

    /// <summary>When false, WebSocket upgrade requests are refused with 503.</summary>
    public bool AcceptWebSockets { get; set; } = true;

    public IReadOnlyList<RecordedRequest> Requests => _requests.ToList();

    public IReadOnlyList<RecordedRequest> RequestsTo(string path) => _requests.Where(r => r.Path == path).ToList();

    /// <summary>Next accepted WebSocket connection; fails the test after the timeout.</summary>
    public async Task<WsSession> NextSessionAsync(TimeSpan? timeout = null)
    {
        using CancellationTokenSource cts = new(timeout ?? TimeSpan.FromSeconds(15));
        return await _sessions.Reader.ReadAsync(cts.Token);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            // See the same loop in Bytex.Adapters.Tests: DisposeAsync cancels and then STOPS the listener, so an
            // accept that has already passed the cancellation check runs against a stopped TcpListener, which answers
            // "Not listening. You must call the Start() method" - an InvalidOperationException this filter did not
            // name. There are two copies of this harness and the race was in both; fixing one was fixing half.
            //
            // Only while cancellation is requested, because a listener that was never started throws the very same
            // exception and that is a fault here which must still fail loudly.
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException
                || (e is InvalidOperationException && _cts.IsCancellationRequested))
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        bool keepOpen = false;
        try
        {
            client.NoDelay = true;
            NetworkStream stream = client.GetStream();
            (string head, byte[] rest) = await ReadHeadAsync(stream);
            if (head.Length == 0)
            {
                return;
            }

            string[] lines = head.Split("\r\n");
            string[] requestLine = lines[0].Split(' ');
            string method = requestLine[0];
            string target = requestLine[1];
            Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
            foreach (string line in lines.Skip(1))
            {
                int colon = line.IndexOf(':');
                if (colon > 0)
                {
                    headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                }
            }

            int q = target.IndexOf('?');
            string path = q < 0 ? target : target[..q];
            string query = q < 0 ? string.Empty : target[(q + 1)..];

            if (headers.TryGetValue("Upgrade", out string? upgrade) && upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase))
            {
                if (!AcceptWebSockets)
                {
                    await WriteResponseAsync(stream, new StubResponse(503, "{}"));
                    return;
                }

                string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(headers["Sec-WebSocket-Key"] + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                byte[] handshake = Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n");
                await stream.WriteAsync(handshake);
                WebSocket socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = Timeout.InfiniteTimeSpan });
                keepOpen = true;
                _sessions.Writer.TryWrite(new WsSession(path, headers, client, socket));
                return;
            }

            int length = headers.TryGetValue("Content-Length", out string? lengthText) ? int.Parse(lengthText, System.Globalization.CultureInfo.InvariantCulture) : 0;
            byte[] body = new byte[length];
            int copied = Math.Min(rest.Length, length);
            Array.Copy(rest, body, copied);
            while (copied < length)
            {
                int read = await stream.ReadAsync(body.AsMemory(copied, length - copied));
                if (read == 0)
                {
                    break;
                }

                copied += read;
            }

            RecordedRequest request = new(method, path, query, headers, Encoding.UTF8.GetString(body));
            _requests.Enqueue(request);
            StubResponse response;
            try
            {
                response = Handler(request);
            }
            catch (Exception e)
            {
                response = new StubResponse(500, "stub handler failed: " + e.Message, "text/plain");
            }

            await WriteResponseAsync(stream, response);
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
            // Client went away mid-request.
        }
        finally
        {
            if (!keepOpen)
            {
                client.Close();
            }
        }
    }

    private static async Task<(string Head, byte[] Remainder)> ReadHeadAsync(NetworkStream stream)
    {
        using MemoryStream buffer = new();
        byte[] chunk = new byte[8192];
        while (true)
        {
            int read = await stream.ReadAsync(chunk);
            if (read == 0)
            {
                return (string.Empty, []);
            }

            buffer.Write(chunk, 0, read);
            byte[] all = buffer.ToArray();
            int end = IndexOfHeaderEnd(all);
            if (end >= 0)
            {
                return (Encoding.ASCII.GetString(all, 0, end), all[(end + 4)..]);
            }
        }
    }

    private static int IndexOfHeaderEnd(byte[] data)
    {
        for (int i = 0; i + 3 < data.Length; i++)
        {
            if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
            {
                return i;
            }
        }

        return -1;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, StubResponse response)
    {
        byte[] payload = response.Bytes ?? Encoding.UTF8.GetBytes(response.Body);
        string head = $"HTTP/1.1 {response.Status} Stub\r\nContent-Type: {response.ContentType}\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(payload);
        await stream.FlushAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        try
        {
            await _acceptLoop;
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Listener already closed.
        }

        _sessions.Writer.TryComplete();
        _cts.Dispose();
    }
}
