using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bytex.Live.Network;

/// <summary>
/// Token-bucket rate limiter: <c>capacity</c> requests per <c>interval</c>.
/// </summary>
/// <summary>Status codes the enum does not name but venues send.</summary>
public static class VenueStatus
{
    /// <summary>Binance answers a key it has banned for rate abuse with 418, meaning what 429 means.</summary>
    public const int Teapot = 418;
}

/// <summary>How much of a venue's own text a log line carries, so a parse failure is readable without being a dump.</summary>
public static class LogText
{
    /// <summary>Longest websocket or stream message written to a log.</summary>
    public const int MaxMessageLength = 300;

    /// <summary>Longest HTTP response body written to a log.</summary>
    public const int MaxBodyLength = 500;

    public static string Truncate(string text, int max) => text.Length <= max ? text : text.Substring(0, max) + "...";
}

public sealed class RateLimiter
{
    private readonly int _capacity;
    private readonly TimeSpan _interval;
    private readonly Queue<DateTime> _timestamps = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RateLimiter(int capacity, TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _interval = interval;
    }

    public async Task WaitAsync(int weight = 1, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (true)
            {
                DateTime now = DateTime.UtcNow;
                while (_timestamps.Count > 0 && now - _timestamps.Peek() > _interval)
                {
                    _timestamps.Dequeue();
                }

                if (_timestamps.Count + weight <= _capacity)
                {
                    for (int i = 0; i < weight; i++)
                    {
                        _timestamps.Enqueue(now);
                    }

                    return;
                }

                TimeSpan wait = _interval - (now - _timestamps.Peek()) + TimeSpan.FromMilliseconds(5);
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}

public sealed record RetryPolicy(int MaxAttempts = 3, TimeSpan? InitialDelay = null, double BackoffFactor = 2.0, TimeSpan? MaxDelay = null)
{
    /// <summary>How long to wait before the first retry when the policy names no delay.</summary>
    public static readonly TimeSpan DefaultInitialDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>The longest the backoff grows to when the policy names no ceiling.</summary>
    public static readonly TimeSpan DefaultMaxDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How much random delay is added to each retry. Without it, every client that failed on the same venue hiccup
    /// retries in the same millisecond and hiccups it again.
    /// </summary>
    public const int MaxJitterMs = 100;

    public static readonly RetryPolicy None = new(1);

    public TimeSpan Initial => InitialDelay ?? DefaultInitialDelay;

    public TimeSpan Max => MaxDelay ?? DefaultMaxDelay;

    public TimeSpan DelayFor(int attempt)
    {
        double ms = Initial.TotalMilliseconds * Math.Pow(BackoffFactor, attempt - 1);
        ms = Math.Min(ms, Max.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(ms + Random.Shared.Next(0, MaxJitterMs));
    }
}

public sealed class VenueHttpException : Exception
{
    public VenueHttpException(HttpStatusCode statusCode, string body, string message)
        : base(message)
    {
        StatusCode = statusCode;
        Body = body;
    }

    public HttpStatusCode StatusCode { get; }

    public string Body { get; }

    public bool IsRateLimited => StatusCode == HttpStatusCode.TooManyRequests || (int)StatusCode == VenueStatus.Teapot;
}

/// <summary>
/// HTTP client with base URL, default headers, rate limiting, retries, and JSON helpers.
/// </summary>
public sealed class HttpClientWrapper : IDisposable
{
    /// <summary>How long one request may take when the caller names no timeout.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly RateLimiter? _limiter;
    private readonly RetryPolicy _retry;
    private readonly ILogger _log;

    public HttpClientWrapper(Uri baseUrl, RateLimiter? limiter = null, RetryPolicy? retry = null, ILogger? logger = null, TimeSpan? timeout = null, IReadOnlyDictionary<string, string>? defaultHeaders = null)
    {
        _http = new HttpClient { BaseAddress = baseUrl, Timeout = timeout ?? DefaultTimeout };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("bytex", typeof(HttpClientWrapper).Assembly.GetName().Version?.ToString() ?? "0"));
        if (defaultHeaders is not null)
        {
            foreach ((string key, string value) in defaultHeaders)
            {
                _http.DefaultRequestHeaders.TryAddWithoutValidation(key, value);
            }
        }

        _limiter = limiter;
        _retry = retry ?? new RetryPolicy();
        _log = logger ?? NullLogger.Instance;
    }

    public Uri BaseUrl => _http.BaseAddress!;

    public Task<string> GetAsync(string path, IReadOnlyDictionary<string, string>? query = null, IReadOnlyDictionary<string, string>? headers = null, int weight = 1, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, path, query, null, headers, weight, ct);

    public Task<string> PostAsync(string path, IReadOnlyDictionary<string, string>? query = null, HttpContent? body = null, IReadOnlyDictionary<string, string>? headers = null, int weight = 1, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, path, query, body, headers, weight, ct);

    public Task<string> PutAsync(string path, IReadOnlyDictionary<string, string>? query = null, HttpContent? body = null, IReadOnlyDictionary<string, string>? headers = null, int weight = 1, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, path, query, body, headers, weight, ct);

    public Task<string> DeleteAsync(string path, IReadOnlyDictionary<string, string>? query = null, HttpContent? body = null, IReadOnlyDictionary<string, string>? headers = null, int weight = 1, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, path, query, body, headers, weight, ct);

    public async Task<JsonDocument> GetJsonAsync(string path, IReadOnlyDictionary<string, string>? query = null, IReadOnlyDictionary<string, string>? headers = null, int weight = 1, CancellationToken ct = default) =>
        JsonDocument.Parse(await GetAsync(path, query, headers, weight, ct).ConfigureAwait(false));

    public async Task<string> SendAsync(HttpMethod method, string path, IReadOnlyDictionary<string, string>? query, HttpContent? body, IReadOnlyDictionary<string, string>? headers, int weight, CancellationToken ct)
    {
        string url = query is { Count: > 0 } ? path + (path.Contains('?') ? "&" : "?") + BuildQuery(query) : path;

        // A request message disposes its content, so the caller's content can be sent once only. Keep the bytes and the content
        // headers and give every attempt a content of its own; otherwise a retried POST has nothing left to send.
        byte[]? payload = body is null ? null : await body.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        List<KeyValuePair<string, IEnumerable<string>>>? contentHeaders = body?.Headers.ToList();
        for (int attempt = 1; ; attempt++)
        {
            if (_limiter is not null)
            {
                await _limiter.WaitAsync(weight, ct).ConfigureAwait(false);
            }

            using HttpRequestMessage request = new(method, url);
            if (payload is not null)
            {
                ByteArrayContent content = new(payload);
                foreach ((string key, IEnumerable<string> values) in contentHeaders!)
                {
                    content.Headers.TryAddWithoutValidation(key, values);
                }

                request.Content = content;
            }

            if (headers is not null)
            {
                foreach ((string key, string value) in headers)
                {
                    request.Headers.TryAddWithoutValidation(key, value);
                }
            }

            try
            {
                using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return text;
                }

                VenueHttpException error = new(response.StatusCode, text, $"{method} {url} returned {(int)response.StatusCode}: {Truncate(text)}");
                bool retryable = error.IsRateLimited || (int)response.StatusCode >= (int)HttpStatusCode.InternalServerError;
                if (!retryable || attempt >= _retry.MaxAttempts)
                {
                    throw error;
                }

                _log.LogWarning("{Method} {Url} failed with {Status}; retrying (attempt {Attempt})", method, url, (int)response.StatusCode, attempt);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                if (attempt >= _retry.MaxAttempts)
                {
                    throw;
                }

                _log.LogWarning("{Method} {Url} failed: {Message}; retrying (attempt {Attempt})", method, url, e.Message, attempt);
            }

            await Task.Delay(_retry.DelayFor(attempt), ct).ConfigureAwait(false);
        }
    }

    public static string BuildQuery(IReadOnlyDictionary<string, string> query) =>
        string.Join('&', query.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));

    private static string Truncate(string text) => LogText.Truncate(text, LogText.MaxBodyLength);

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// HMAC signing helpers for request authentication.
/// </summary>
public static class HmacSigner
{
    public static string Sha256Hex(string secret, string payload)
    {
        byte[] hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexStringLower(hash);
    }

    public static string Sha256Base64(string secret, string payload)
    {
        byte[] hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(hash);
    }

    public static string Sha512Hex(string secret, string payload)
    {
        byte[] hash = HMACSHA512.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexStringLower(hash);
    }

    public static string Sha512Base64(byte[] secret, byte[] payload) => Convert.ToBase64String(HMACSHA512.HashData(secret, payload));
}

/// <summary>
/// Resolves secrets from explicit values or environment variables.
/// </summary>
public static class Secrets
{
    public static string Require(string? value, string environmentVariable)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        string? fromEnv = System.Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(fromEnv))
        {
            throw new InvalidOperationException($"Missing credential: set the configuration value or the {environmentVariable} environment variable.");
        }

        return fromEnv;
    }

    public static string? Optional(string? value, string environmentVariable) =>
        !string.IsNullOrWhiteSpace(value) ? value : System.Environment.GetEnvironmentVariable(environmentVariable);
}
