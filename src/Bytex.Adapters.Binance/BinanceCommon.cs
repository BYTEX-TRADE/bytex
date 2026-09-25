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


    string? BaseUrlHttp { get; }

    string? BaseUrlWs { get; }
}

public sealed record BinanceDataClientConfig : DataClientConfig, IBinanceSettings
{
    public string? ApiKey { get; init; }

    public string? ApiSecret { get; init; }

    public BinanceAccountType AccountType { get; init; } = BinanceAccountType.Spot;


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


    public string? BaseUrlHttp { get; init; }

    public string? BaseUrlWs { get; init; }

    /// <summary>Trigger price source for conditional orders on futures.</summary>
    public TriggerType DefaultTriggerType { get; init; } = TriggerType.LastPrice;

    public int RecvWindowMs { get; init; } = BinanceVenue.DefaultRecvWindowMs;
}

/// <summary>
/// URL and symbol conventions for the Binance venue.
/// </summary>
public static class BinanceVenue
{
    public static readonly Venue Venue = new("BINANCE");

    public const string EnvApiKey = "BINANCE_API_KEY";
    public const string EnvApiSecret = "BINANCE_API_SECRET";
    public const string PerpSuffix = "-PERP";

    /// <summary>
    /// The most characters this venue accepts in a client order id. A broker id prefixes that id here, so a long id
    /// and a long broker id together can exceed it - which is refused before the order is sent rather than by the
    /// venue, because a rejection from the venue names neither of the two things that were too long.
    /// </summary>
    public const int MaxClientOrderIdLength = 36;

    /// <summary>
    /// Where a leverage is set on this venue. It is account state per symbol rather than a field on an order, so an
    /// order carrying one would be ignored - the only way a strategy written for 3x is traded at 3x here is for the
    /// client to set it before it trades.
    /// </summary>
    public const string LeveragePath = "/fapi/v1/leverage";

    /// <summary>
    /// What this venue publishes as a percentage rather than a fraction. Its own exchangeInfo gives margin per
    /// symbol as `requiredMarginPercent` and `maintMarginPercent` - "5.0000" meaning a twentieth - while the engine
    /// holds margin as a fraction of notional. Named because a factor of a hundred is the kind of mistake that
    /// produces a plausible number.
    /// </summary>
    public const decimal MarginPercentToFraction = 100m;

    /// <summary>
    /// What this venue asks for when a symbol publishes no figure of its own. These were the adapter's hard-coded
    /// values for every contract; they are the venue-wide default and nothing more, which is why they are now a
    /// fallback behind the published per-symbol number rather than the answer.
    /// </summary>
    public const decimal DefaultMarginInit = 0.05m;

    /// <inheritdoc cref="DefaultMarginInit"/>
    public const decimal DefaultMarginMaint = 0.025m;

    /// <summary>
    /// This venue's leverage is a whole number: its own documentation gives the field as an integer from 1 to 125.
    /// Named here so the refusal below quotes the venue rather than an assumption.
    /// </summary>
    public const bool LeverageIsWholeNumber = true;

    /// <summary>
    /// What this venue says when asked about a symbol it does not list: <c>-1121</c> "Invalid symbol". It arrives as
    /// an error body rather than an envelope, so it is read out of the body of the HTTP failure. Asking about an
    /// instrument that is not listed leaves the provider empty rather than throwing, as on every venue.
    /// </summary>
    public const int ErrorInvalidSymbol = -1121;

    /// <summary>Whether a venue failure is this venue saying it does not list the symbol that was asked about.</summary>
    public static bool IsNoSuchInstrument(VenueHttpException failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(failure.Body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("code", out JsonElement code)
                && code.TryGetInt32(out int value)
                && value == ErrorInvalidSymbol;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // Where each market answers when nothing is configured. Named rather than written where they are used, because
    // the plugin declares the same four to anything that wants to know before it constructs a client, and two copies
    // of a host is how a declaration comes to describe a venue the adapter no longer talks to.
    public const string SpotHttpBase = "https://api.binance.com";
    public const string SpotWsBase = "wss://stream.binance.com:9443";
    public const string UsdMFuturesHttpBase = "https://fapi.binance.com";
    public const string UsdMFuturesWsBase = "wss://fstream.binance.com";

    public static string HttpBase(IBinanceSettings s) => s.BaseUrlHttp ?? s.AccountType switch
    {
        BinanceAccountType.Spot => SpotHttpBase,
        BinanceAccountType.UsdMFutures => UsdMFuturesHttpBase,
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };

    public static string WsBase(IBinanceSettings s) => s.BaseUrlWs ?? s.AccountType switch
    {
        BinanceAccountType.Spot => SpotWsBase,
        BinanceAccountType.UsdMFutures => UsdMFuturesWsBase,
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };

    public static string ApiPrefix(BinanceAccountType type) => type == BinanceAccountType.Spot ? "/api/v3" : "/fapi/v1";

    /// <summary>
    /// The most candles the venue returns for one klines request: 1,000 on spot, 1,500 on USD-margined futures. A fetch
    /// asks for no more than this per request and reads the answer against what it asked for, so a page that comes back
    /// short means the venue has no more rather than that a literal happened to match.
    /// </summary>
    public static int KlinePage(BinanceAccountType type) => type == BinanceAccountType.Spot ? 1000 : 1500;

    /// <summary>The most trades the venue returns for one trades or aggTrades request.</summary>
    public const int TradePage = 1000;

    /// <summary>
    /// How many funding rates one page of <c>/fapi/v1/fundingRate</c> holds. Measured on 2026-09-23; the venue's own
    /// maximum, and a short page is how a fetch knows it has reached the end.
    /// </summary>
    public const int FundingPage = 1000;

    /// <summary>The deepest book snapshot the venue serves, and the depth it serves when none is asked for.</summary>
    public const int MaxBookDepth = 1000;

    /// <summary>Book depth served when a subscription names none.</summary>
    public const int DefaultBookDepth = 100;

    /// <summary>How often the venue publishes book deltas on the stream this adapter subscribes to.</summary>
    public const string BookStreamInterval = "100ms";

    /// <summary>
    /// The request weight the venue allows per minute. It is spent per endpoint rather than per request, which is why
    /// every call passes its own weight; the budget here is a little under the venue's 1,200 to leave room for retries.
    /// </summary>
    public const int RequestWeightPerMinute = 1100;

    /// <summary>
    /// How long a signed request stays valid, in milliseconds. The venue refuses one whose timestamp is older than
    /// this, and a value much larger than a few seconds widens the window an intercepted request could be replayed in.
    /// </summary>
    public const int DefaultRecvWindowMs = 5000;

    /// <summary>How often a user-data listen key is renewed; the venue drops one that is not renewed within an hour.</summary>
    public static readonly TimeSpan ListenKeyKeepAlive = TimeSpan.FromMinutes(30);

    /// <summary>The callback rate of a futures trailing stop, as a percentage, between the venue's own bounds.</summary>
    public const decimal MinCallbackRate = 0.1m;

    /// <summary>The callback rate of a futures trailing stop, as a percentage, between the venue's own bounds.</summary>
    public const decimal MaxCallbackRate = 10m;

    /// <summary>
    /// The weight the venue charges for each endpoint this adapter calls, as its documentation states it. A wrong
    /// weight is not an error the venue reports: it bans the key when the minute's budget is exceeded.
    /// </summary>
    public static class Weights
    {
        public const int ExchangeInfo = 20;
        public const int Klines = 2;
        public const int Trades = 2;
        public const int BookSnapshot = 5;
        public const int Account = 20;
        public const int FuturesBalance = 5;
        public const int FuturesPositionRisk = 5;
        public const int Order = 2;
        /// <summary>Open orders for every symbol at once, which the venue charges far more for than for one.</summary>
        public const int OpenOrdersAllSymbols = 40;

        public const int OpenOrdersOneSymbol = 3;

        public const int AllOrders = 10;

        public const int OrderQuery = 10;
    }

    /// <summary>
    /// Maps a raw venue symbol to an instrument id. Futures perpetuals get a -PERP suffix; a dated contract carries its
    /// delivery date after an underscore (BTCUSDT_250926) and keeps its own name, whatever contract type the caller assumed.
    /// </summary>
    public static InstrumentId ToInstrumentId(string rawSymbol, BinanceAccountType type, string contractType = "PERPETUAL")
    {
        bool perpetual = type == BinanceAccountType.UsdMFutures && contractType == "PERPETUAL" && !rawSymbol.Contains('_', StringComparison.Ordinal);
        string symbol = perpetual ? rawSymbol + PerpSuffix : rawSymbol;
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
        string key = Secrets.Require(s.ApiKey, EnvApiKey);
        string secret = Secrets.Require(s.ApiSecret, EnvApiSecret);
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

    public BinanceHttp(IBinanceSettings settings, ILogger? logger = null, bool requireCredentials = false, int recvWindowMs = BinanceVenue.DefaultRecvWindowMs)
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
            _apiKey = Secrets.Optional(settings.ApiKey, BinanceVenue.EnvApiKey);
            _apiSecret = Secrets.Optional(settings.ApiSecret, BinanceVenue.EnvApiSecret);
        }

        Dictionary<string, string> headers = new();
        if (_apiKey is not null)
        {
            headers["X-MBX-APIKEY"] = _apiKey;
        }

        _http = new HttpClientWrapper(new Uri(BinanceVenue.HttpBase(settings)), new RateLimiter(BinanceVenue.RequestWeightPerMinute, TimeSpan.FromMinutes(1)), new RetryPolicy(), logger, defaultHeaders: headers);
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
