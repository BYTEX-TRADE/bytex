using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bytex.Live.Network;

public sealed record WebSocketClientConfig
{
    public required Uri Url { get; init; }

    /// <summary>
    /// When set, asked for the address before every connection attempt, the first one and each reconnection. For venues
    /// that hand out a short-lived connection token in the address; <see cref="Url"/> is then only a label.
    /// </summary>
    public Func<CancellationToken, Task<Uri>>? UrlProvider { get; init; }

    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxReconnectDelay { get; init; } = TimeSpan.FromSeconds(30);

    public int MaxReconnectAttempts { get; init; } = int.MaxValue;

    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Text to send as an application-level ping; null uses the WebSocket ping frame only.</summary>
    public string? PingMessage { get; init; }

    public int ReceiveBufferSize { get; init; } = 64 * 1024;

    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// WebSocket connection with automatic reconnection, outbound queue, and text/binary handlers.
/// </summary>
public sealed class WebSocketClient : IAsyncDisposable
{
    /// <summary>How long to let a close handshake settle before the socket is dropped.</summary>
    private static readonly TimeSpan CloseSettle = TimeSpan.FromMilliseconds(200);

    /// <summary>How long to wait for the receive loop to notice a cancellation before moving on.</summary>
    private static readonly TimeSpan DrainSettle = TimeSpan.FromMilliseconds(100);

    private readonly WebSocketClientConfig _config;
    private readonly ILogger _log;
    private readonly Channel<(byte[] Payload, WebSocketMessageType Type)> _outbound = Channel.CreateUnbounded<(byte[], WebSocketMessageType)>();
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private Task? _sendLoop;
    private Task? _pingLoop;
    private int _reconnectAttempts;
    private bool _disposed;

    public WebSocketClient(WebSocketClientConfig config, ILogger? logger = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _log = logger ?? NullLogger.Instance;
    }

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    public Uri Url => _config.Url;

    /// <summary>Invoked for every text message.</summary>
    public Func<string, Task>? OnText { get; set; }

    /// <summary>Invoked for every binary message.</summary>
    public Func<byte[], Task>? OnBinary { get; set; }

    /// <summary>Invoked after each successful connection (including reconnections); use it to resubscribe.</summary>
    public Func<bool, Task>? OnConnected { get; set; }

    /// <summary>Invoked when the connection drops; the argument is the reason.</summary>
    public Func<string, Task>? OnDisconnected { get; set; }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsConnected)
            {
                return;
            }

            _cts?.Cancel();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            await OpenSocketAsync(_cts.Token, isReconnect: false).ConfigureAwait(false);
            _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token), CancellationToken.None);
            _sendLoop = Task.Run(() => SendLoopAsync(_cts.Token), CancellationToken.None);
            _pingLoop = Task.Run(() => PingLoopAsync(_cts.Token), CancellationToken.None);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private async Task OpenSocketAsync(CancellationToken ct, bool isReconnect)
    {
        ClientWebSocket socket = new();
        foreach ((string key, string value) in _config.Headers)
        {
            socket.Options.SetRequestHeader(key, value);
        }

        socket.Options.KeepAliveInterval = _config.PingInterval;
        Uri url = _config.UrlProvider is { } provider ? await provider(ct).ConfigureAwait(false) : _config.Url;
        await socket.ConnectAsync(url, ct).ConfigureAwait(false);
        _socket = socket;
        _reconnectAttempts = 0;
        // Without the query: it may carry a connection token.
        _log.LogInformation("WebSocket connected to {Url}", url.GetLeftPart(UriPartial.Path));
        if (OnConnected is { } handler)
        {
            await handler(isReconnect).ConfigureAwait(false);
        }
    }

    public async Task DisconnectAsync()
    {
        _cts?.Cancel();
        if (_socket is { State: WebSocketState.Open } socket)
        {
            try
            {
                using CancellationTokenSource closeCts = new(TimeSpan.FromSeconds(3));
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "client disconnect", closeCts.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                // Closing a dropped socket is best effort.
            }
        }

        _socket?.Dispose();
        _socket = null;
    }

    public void SendText(string text) => _outbound.Writer.TryWrite((Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text));

    public void SendBinary(byte[] payload) => _outbound.Writer.TryWrite((payload, WebSocketMessageType.Binary));

    private async Task SendLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach ((byte[] payload, WebSocketMessageType type) in _outbound.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                ClientWebSocket? socket = _socket;
                if (socket is not { State: WebSocketState.Open })
                {
                    // Re-queue and wait for reconnection.
                    _outbound.Writer.TryWrite((payload, type));
                    await Task.Delay(CloseSettle, ct).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    await socket.SendAsync(payload, type, endOfMessage: true, ct).ConfigureAwait(false);
                }
                catch (WebSocketException e)
                {
                    _log.LogWarning(e, "WebSocket send failed; message re-queued");
                    _outbound.Writer.TryWrite((payload, type));
                    await Task.Delay(CloseSettle, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        if (_config.PingMessage is null)
        {
            return;
        }

        try
        {
            using PeriodicTimer timer = new(_config.PingInterval);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (IsConnected)
                {
                    SendText(_config.PingMessage);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        byte[] buffer = new byte[_config.ReceiveBufferSize];
        using MemoryStream accumulator = new();
        while (!ct.IsCancellationRequested)
        {
            ClientWebSocket? socket = _socket;
            if (socket is null)
            {
                await Task.Delay(DrainSettle, ct).ConfigureAwait(false);
                continue;
            }

            try
            {
                WebSocketReceiveResult result;
                accumulator.SetLength(0);
                do
                {
                    result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        throw new WebSocketException($"Server closed connection: {result.CloseStatus} {result.CloseStatusDescription}");
                    }

                    accumulator.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                byte[] payload = accumulator.ToArray();
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    if (OnText is { } onText)
                    {
                        await onText(Encoding.UTF8.GetString(payload)).ConfigureAwait(false);
                    }
                }
                else if (OnBinary is { } onBinary)
                {
                    await onBinary(payload).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException)
            {
                _log.LogWarning("WebSocket {Url} disconnected: {Message}", _config.Url, e.Message);
                if (OnDisconnected is { } onDisconnected)
                {
                    await onDisconnected(e.Message).ConfigureAwait(false);
                }

                await ReconnectAsync(ct).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _log.LogError(e, "WebSocket handler threw; continuing");
            }
        }
    }

    private async Task ReconnectAsync(CancellationToken ct)
    {
        _socket?.Dispose();
        _socket = null;
        while (!ct.IsCancellationRequested && _reconnectAttempts < _config.MaxReconnectAttempts)
        {
            _reconnectAttempts++;
            double factor = Math.Min(Math.Pow(2, _reconnectAttempts - 1), _config.MaxReconnectDelay / _config.ReconnectDelay);
            TimeSpan delay = TimeSpan.FromMilliseconds(_config.ReconnectDelay.TotalMilliseconds * factor) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250));
            _log.LogInformation("Reconnecting to {Url} in {Delay} (attempt {Attempt})", _config.Url, delay, _reconnectAttempts);
            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
                await OpenSocketAsync(ct, isReconnect: true).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                _log.LogWarning("Reconnect attempt {Attempt} failed: {Message}", _reconnectAttempts, e.Message);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await DisconnectAsync().ConfigureAwait(false);
        _outbound.Writer.TryComplete();
        _cts?.Dispose();
        _connectGate.Dispose();
    }
}
