using System.Globalization;
using System.Text.Json;
using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Primitives;
using Bytex.Live.Network;
using Microsoft.Extensions.Logging;

namespace Bytex.Adapters.Binance;

public enum BinanceAccountType
{
    Spot = 1,

    /// <summary>USDⓈ-margined perpetual and delivery futures.</summary>
    UsdMFutures = 2,
}

/// <summary>
/// Settings shared by the Binance data and execution clients.
/// </summary>
public interface IBinanceSettings
{
    string? ApiKey { get; }

    string? ApiSecret { get; }

    BinanceAccountType AccountType { get; }

    bool Testnet { get; }

    string? BaseUrlHttp { get; }

    string? BaseUrlWs { get; }
}

public sealed record BinanceDataClientConfig : DataClientConfig, IBinanceSettings
{
    public string? ApiKey { get; init; }

    public string? ApiSecret { get; init; }

    public BinanceAccountType AccountType { get; init; } = BinanceAccountType.Spot;

    public bool Testnet { get; init; }

    public string? BaseUrlHttp { get; init; }

    public string? BaseUrlWs { get; init; }

    /// <summary>Use aggregated trades for historical trade requests.</summary>
    public bool UseAggTrades { get; init; } = true;
}

public sealed record BinanceExecutionClientConfig : ExecutionClientConfig, IBinanceSettings
{
    public string? ApiKey { get; init; }

    public string? ApiSecret { get; init; }

    public BinanceAccountType AccountType { get; init; } = BinanceAccountType.Spot;

    public bool Testnet { get; init; }

    public string? BaseUrlHttp { get; init; }

    public string? BaseUrlWs { get; init; }

    /// <summary>Trigger price source for conditional orders on futures.</summary>
    public TriggerType DefaultTriggerType { get; init; } = TriggerType.LastPrice;

    public int RecvWindowMs { get; init; } = 5000;
}

/// <summary>
/// URL and symbol conventions for the Binance venue.
/// </summary>
public static class BinanceVenue
{
    public static readonly Venue Venue = new("BINANCE");

    public const string EnvApiKey = "BINANCE_API_KEY";
    public const string EnvApiSecret = "BINANCE_API_SECRET";
    public const string EnvTestnetApiKey = "BINANCE_TESTNET_API_KEY";
    public const string EnvTestnetApiSecret = "BINANCE_TESTNET_API_SECRET";
    public const string PerpSuffix = "-PERP";

    public static string HttpBase(IBinanceSettings s) => s.BaseUrlHttp ?? (s.AccountType, s.Testnet) switch
    {
        (BinanceAccountType.Spot, false) => "https://api.binance.com",
        (BinanceAccountType.Spot, true) => "https://testnet.binance.vision",
        (BinanceAccountType.UsdMFutures, false) => "https://fapi.binance.com",
        (BinanceAccountType.UsdMFutures, true) => "https://testnet.binancefuture.com",
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };

    public static string WsBase(IBinanceSettings s) => s.BaseUrlWs ?? (s.AccountType, s.Testnet) switch
    {
        (BinanceAccountType.Spot, false) => "wss://stream.binance.com:9443",
        (BinanceAccountType.Spot, true) => "wss://stream.testnet.binance.vision",
        (BinanceAccountType.UsdMFutures, false) => "wss://fstream.binance.com",
        (BinanceAccountType.UsdMFutures, true) => "wss://stream.binancefuture.com",
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };

    public static string ApiPrefix(BinanceAccountType type) => type == BinanceAccountType.Spot ? "/api/v3" : "/fapi/v1";

    /// <summary>Maps a raw venue symbol to an instrument id (futures perpetuals get a -PERP suffix).</summary>
    public static InstrumentId ToInstrumentId(string rawSymbol, BinanceAccountType type, string contractType = "PERPETUAL")
    {
        string symbol = type == BinanceAccountType.UsdMFutures && contractType == "PERPETUAL" ? rawSymbol + PerpSuffix : rawSymbol;
        return new InstrumentId(new Symbol(symbol), Venue);
    }

    /// <summary>Maps an instrument id back to the raw venue symbol.</summary>
    public static string ToRawSymbol(InstrumentId id)
    {
        string s = id.Symbol.Value;
        return s.EndsWith(PerpSuffix, StringComparison.Ordinal) ? s.Substring(0, s.Length - PerpSuffix.Length) : s;
    }

    public static string Interval(BarSpecification spec) => (spec.Aggregation, spec.Step) switch
    {
        (BarAggregation.Second, 1) => "1s",
        (BarAggregation.Minute, 1) => "1m",
        (BarAggregation.Minute, 3) => "3m",
        (BarAggregation.Minute, 5) => "5m",
        (BarAggregation.Minute, 15) => "15m",
        (BarAggregation.Minute, 30) => "30m",
        (BarAggregation.Hour, 1) => "1h",
        (BarAggregation.Hour, 2) => "2h",
        (BarAggregation.Hour, 4) => "4h",
        (BarAggregation.Hour, 6) => "6h",
        (BarAggregation.Hour, 8) => "8h",
        (BarAggregation.Hour, 12) => "12h",
        (BarAggregation.Day, 1) => "1d",
        (BarAggregation.Day, 3) => "3d",
        (BarAggregation.Week, 1) => "1w",
        (BarAggregation.Month, 1) => "1M",
        _ => throw new NotSupportedException($"Binance does not support bar specification {spec}."),
    };

    public static (string Key, string Secret) Credentials(IBinanceSettings s)
    {
        string key = Secrets.Require(s.ApiKey, s.Testnet ? EnvTestnetApiKey : EnvApiKey);
        string secret = Secrets.Require(s.ApiSecret, s.Testnet ? EnvTestnetApiSecret : EnvApiSecret);
        return (key, secret);
    }
}

/// <summary>
/// Signed and unsigned HTTP access to the Binance REST API.
/// </summary>
public sealed class BinanceHttp : IDisposable
{
    private readonly HttpClientWrapper _http;
    private readonly string? _apiKey;
    private readonly string? _apiSecret;
    private readonly int _recvWindow;

    public BinanceHttp(IBinanceSettings settings, ILogger? logger = null, bool requireCredentials = false, int recvWindowMs = 5000)
    {
        ArgumentNullException.ThrowIfNull(settings);
        AccountType = settings.AccountType;
        _recvWindow = recvWindowMs;
        if (requireCredentials)
        {
            (_apiKey, _apiSecret) = BinanceVenue.Credentials(settings);
        }
        else
        {
            _apiKey = Secrets.Optional(settings.ApiKey, settings.Testnet ? BinanceVenue.EnvTestnetApiKey : BinanceVenue.EnvApiKey);
            _apiSecret = Secrets.Optional(settings.ApiSecret, settings.Testnet ? BinanceVenue.EnvTestnetApiSecret : BinanceVenue.EnvApiSecret);
        }

        Dictionary<string, string> headers = new();
        if (_apiKey is not null)
        {
            headers["X-MBX-APIKEY"] = _apiKey;
        }

        _http = new HttpClientWrapper(new Uri(BinanceVenue.HttpBase(settings)), new RateLimiter(1100, TimeSpan.FromMinutes(1)), new RetryPolicy(3), logger, defaultHeaders: headers);
    }

    public BinanceAccountType AccountType { get; }

    public string Prefix => BinanceVenue.ApiPrefix(AccountType);

    public bool HasCredentials => _apiKey is not null && _apiSecret is not null;

    public async Task<JsonDocument> GetPublicAsync(string path, IReadOnlyDictionary<string, string>? query = null, int weight = 1, CancellationToken ct = default) =>
        JsonDocument.Parse(await _http.GetAsync(path, query, null, weight, ct).ConfigureAwait(false));

    public Task<JsonDocument> GetSignedAsync(string path, Dictionary<string, string>? query = null, int weight = 1, CancellationToken ct = default) =>
        SendSignedAsync(HttpMethod.Get, path, query, weight, ct);

    public Task<JsonDocument> PostSignedAsync(string path, Dictionary<string, string>? query = null, int weight = 1, CancellationToken ct = default) =>
        SendSignedAsync(HttpMethod.Post, path, query, weight, ct);

    public Task<JsonDocument> PutSignedAsync(string path, Dictionary<string, string>? query = null, int weight = 1, CancellationToken ct = default) =>
        SendSignedAsync(HttpMethod.Put, path, query, weight, ct);

    public Task<JsonDocument> DeleteSignedAsync(string path, Dictionary<string, string>? query = null, int weight = 1, CancellationToken ct = default) =>
        SendSignedAsync(HttpMethod.Delete, path, query, weight, ct);

    /// <summary>Requests that need the API key header but no signature (listen keys).</summary>
    public async Task<JsonDocument> SendKeyedAsync(HttpMethod method, string path, IReadOnlyDictionary<string, string>? query = null, CancellationToken ct = default)
    {
        if (_apiKey is null)
        {
            throw new InvalidOperationException("Binance API key is required for this request.");
        }

        string text = await _http.SendAsync(method, path, query, null, null, 1, ct).ConfigureAwait(false);
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
    }

    private async Task<JsonDocument> SendSignedAsync(HttpMethod method, string path, Dictionary<string, string>? query, int weight, CancellationToken ct)
    {
        if (_apiKey is null || _apiSecret is null)
        {
            throw new InvalidOperationException("Binance API credentials are required for this request.");
        }

        query ??= new Dictionary<string, string>();
        query["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        query["recvWindow"] = _recvWindow.ToString(CultureInfo.InvariantCulture);
        string queryString = HttpClientWrapper.BuildQuery(query);
        string signature = HmacSigner.Sha256Hex(_apiSecret, queryString);
        string url = $"{path}?{queryString}&signature={signature}";
        string text = await _http.SendAsync(method, url, null, null, null, weight, ct).ConfigureAwait(false);
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
    }

    public void Dispose() => _http.Dispose();
}

internal static class Json
{
    public static decimal Dec(this JsonElement e, string name) => decimal.Parse(e.GetProperty(name).GetString() ?? "0", NumberStyles.Float, CultureInfo.InvariantCulture);

    public static decimal DecAny(this JsonElement e, string name)
    {
        JsonElement p = e.GetProperty(name);
        return p.ValueKind == JsonValueKind.Number ? p.GetDecimal() : decimal.Parse(p.GetString() ?? "0", NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    public static decimal DecValue(this JsonElement p) => p.ValueKind == JsonValueKind.Number ? p.GetDecimal() : decimal.Parse(p.GetString() ?? "0", NumberStyles.Float, CultureInfo.InvariantCulture);

    public static string Str(this JsonElement e, string name) => e.GetProperty(name).GetString() ?? string.Empty;

    public static string? StrOpt(this JsonElement e, string name) => e.TryGetProperty(name, out JsonElement p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    public static long Long(this JsonElement e, string name)
    {
        JsonElement p = e.GetProperty(name);
        return p.ValueKind == JsonValueKind.Number ? p.GetInt64() : long.Parse(p.GetString() ?? "0", CultureInfo.InvariantCulture);
    }

    public static bool Bool(this JsonElement e, string name) => e.TryGetProperty(name, out JsonElement p) && p.ValueKind == JsonValueKind.True;

    public static bool Has(this JsonElement e, string name) => e.TryGetProperty(name, out _);

    public static UnixNanos Ms(this JsonElement e, string name) => UnixNanos.FromMilliseconds(e.Long(name));

    public static string Fmt(decimal value) => value.ToString(CultureInfo.InvariantCulture);
}
