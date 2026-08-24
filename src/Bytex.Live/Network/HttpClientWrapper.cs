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
    public static readonly RetryPolicy None = new(1);

    public TimeSpan Initial => InitialDelay ?? TimeSpan.FromMilliseconds(250);

    public TimeSpan Max => MaxDelay ?? TimeSpan.FromSeconds(5);

    public TimeSpan DelayFor(int attempt)
    {
        double ms = Initial.TotalMilliseconds * Math.Pow(BackoffFactor, attempt - 1);
        ms = Math.Min(ms, Max.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(ms + Random.Shared.Next(0, 100));
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

    public bool IsRateLimited => StatusCode == HttpStatusCode.TooManyRequests || (int)StatusCode == 418;
}

/// <summary>
/// HTTP client with base URL, default headers, rate limiting, retries, and JSON helpers.
/// </summary>
public sealed class HttpClientWrapper : IDisposable
{
    private readonly HttpClient _http;
    private readonly RateLimiter? _limiter;
    private readonly RetryPolicy _retry;
    private readonly ILogger _log;

    public HttpClientWrapper(Uri baseUrl, RateLimiter? limiter = null, RetryPolicy? retry = null, ILogger? logger = null, TimeSpan? timeout = null, IReadOnlyDictionary<string, string>? defaultHeaders = null)
    {
        _http = new HttpClient { BaseAddress = baseUrl, Timeout = timeout ?? TimeSpan.FromSeconds(30) };
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
        for (int attempt = 1; ; attempt++)
        {
            if (_limiter is not null)
            {
                await _limiter.WaitAsync(weight, ct).ConfigureAwait(false);
            }

            using HttpRequestMessage request = new(method, url);
            if (body is not null)
            {
                request.Content = body;
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
                bool retryable = error.IsRateLimited || (int)response.StatusCode >= 500;
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

    private static string Truncate(string text) => text.Length <= 500 ? text : text.Substring(0, 500) + "...";

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
