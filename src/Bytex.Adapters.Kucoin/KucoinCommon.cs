using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Live.Network;
using Microsoft.Extensions.Logging;

namespace Bytex.Adapters.Kucoin;

public interface IKucoinSettings
{
    string? ApiKey { get; }

    string? ApiSecret { get; }

    string? ApiPassphrase { get; }

    /// <summary>The version of the API key as the venue's API management page shows it ("2" or "3"); null reads the environment, then assumes 3.</summary>
    string? ApiKeyVersion { get; }

    /// <summary>
    /// Which of the venue's two markets this client talks to. Spot by default, so a configuration written before
    /// futures existed still means spot.
    /// </summary>
    KucoinProductType ProductType { get; }

    string? BaseUrlHttp { get; }

    /// <summary>Replaces the stream server the venue names; the connection token is still asked for over REST.</summary>
    string? BaseUrlWs { get; }
}

public sealed record KucoinDataClientConfig : DataClientConfig, IKucoinSettings
{
    public string? ApiKey { get; init; }

    public string? ApiSecret { get; init; }

    public string? ApiPassphrase { get; init; }

    public string? ApiKeyVersion { get; init; }

    public KucoinProductType ProductType { get; init; } = KucoinProductType.Spot;

    public string? BaseUrlHttp { get; init; }

    public string? BaseUrlWs { get; init; }
}

public sealed record KucoinExecutionClientConfig : ExecutionClientConfig, IKucoinSettings
{
    public string? ApiKey { get; init; }

    public string? ApiSecret { get; init; }

    public string? ApiPassphrase { get; init; }

    public string? ApiKeyVersion { get; init; }

    public KucoinProductType ProductType { get; init; } = KucoinProductType.Spot;

    public string? BaseUrlHttp { get; init; }

    public string? BaseUrlWs { get; init; }
}

public static class KucoinVenue
{
    public static readonly Venue Venue = new("KUCOIN");

    public const string EnvApiKey = "KUCOIN_API_KEY";
    public const string EnvApiSecret = "KUCOIN_API_SECRET";
    public const string EnvApiPassphrase = "KUCOIN_API_PASSPHRASE";
    public const string EnvApiKeyVersion = "KUCOIN_API_KEY_VERSION";

    /// <summary>The key version assumed when nothing says otherwise, and the one tried first.</summary>
    public const string DefaultApiKeyVersion = "3";

    /// <summary>
    /// The version tried next. A key made before the venue's v3 signs its passphrase the same way and is refused for
    /// a reason that depends on what the venue happens to check first, so the only way to tell is to try.
    /// </summary>
    public const string FallbackApiKeyVersion = "2";

    /// <summary>
    /// Refusals that are NOT about the key version: the clock is off, the key does not exist, the address is not on
    /// the key's allow list, the rate limit is hit. Anything else may be the version, so it is worth trying the other
    /// one.
    /// <para>
    /// Named here and read by both the clients and <c>verify-keys</c>, because the two used to hold this list
    /// separately and only one of them had a fallback at all. A v2 key with nothing configured passed its key test
    /// and then failed when a node started - a green report followed by a dead node, which is worse than a red one.
    /// </para>
    /// </summary>
    /// <summary>
    /// What this venue says when asked about a symbol it does not list: <c>900001</c> "Trading pair ... does not
    /// exist" on spot, <c>404000</c> "The contract information you requested does not exist" on futures. Both are
    /// refusals of a perfectly well-formed question, so asking about an instrument that is not listed leaves the
    /// provider empty rather than throwing - which is what every venue does, and what a caller that then looks the
    /// instrument up can act on without knowing which venue answered.
    /// </summary>
    public static readonly IReadOnlySet<string> ErrorsThatMeanNoSuchInstrument =
        new HashSet<string>(StringComparer.Ordinal) { "900001", "404000" };

    public static readonly IReadOnlySet<string> ErrorsThatAreNotTheKeyVersion =
        new HashSet<string>(StringComparer.Ordinal) { "400002", "400003", "400006", "429000" };

    /// <summary>The key version a configuration or the environment states, or null when neither does.</summary>
    public static string? StatedApiKeyVersion(IKucoinSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return Secrets.Optional(s.ApiKeyVersion, EnvApiKeyVersion);
    }

    /// <summary>The venue answers every successful call with this code.</summary>
    public const string Ok = "200000";

    /// <summary>
    /// The most candles the venue returns for one request. A history fetch asks for a window no wider than this and
    /// walks the rest of the period page by page.
    /// </summary>
    public const int CandlePage = 1500;

    /// <summary>The longest client order id the venue accepts; it refuses a longer one outright.</summary>
    public const int MaxClientOrderIdLength = 40;

    /// <summary>Requests the venue allows in <see cref="RequestWindow"/> on the public endpoints this adapter calls.</summary>
    public const int RequestsPerWindow = 30;

    /// <summary>The window <see cref="RequestsPerWindow"/> is counted over.</summary>
    public static readonly TimeSpan RequestWindow = TimeSpan.FromSeconds(3);

    /// <summary>How often to ping the stream when the venue's own token does not say; it drops a silent socket.</summary>
    public static readonly TimeSpan DefaultPingInterval = TimeSpan.FromSeconds(18);

    /// <summary>
    /// The size the venue reports for an instrument with no maximum. It is a number rather than an absent field, and
    /// not every precision can hold it, so a maximum at or above this means there is none.
    /// </summary>
    public const decimal NoSizeLimit = 1_000_000_000m;

    /// <summary>
    /// Where KuCoin spot answers when nothing is configured. There is no companion socket base on either market: the
    /// venue answers a REST call with the address to connect to and a token that expires, so the socket is somewhere
    /// different each time.
    /// </summary>
    public const string DefaultHttpBase = "https://api.kucoin.com";

    public static string HttpBase(IKucoinSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.BaseUrlHttp ?? (s.ProductType == KucoinProductType.Futures
            ? KucoinFuturesVenue.DefaultHttpBase
            : DefaultHttpBase);
    }

    /// <summary>KuCoin names a pair BASE-QUOTE; the instrument id keeps that name, so the two map without a lookup.</summary>
    public static InstrumentId ToInstrumentId(string rawSymbol) => new(new Symbol(rawSymbol), Venue);

    public static string ToRawSymbol(InstrumentId id) => id.Symbol.Value;

    public static string Interval(BarSpecification spec) => (spec.Aggregation, spec.Step) switch
    {
        (BarAggregation.Minute, 1) => "1min",
        (BarAggregation.Minute, 3) => "3min",
        (BarAggregation.Minute, 5) => "5min",
        (BarAggregation.Minute, 15) => "15min",
        (BarAggregation.Minute, 30) => "30min",
        (BarAggregation.Hour, 1) => "1hour",
        (BarAggregation.Hour, 2) => "2hour",
        (BarAggregation.Hour, 4) => "4hour",
        (BarAggregation.Hour, 6) => "6hour",
        (BarAggregation.Hour, 8) => "8hour",
        (BarAggregation.Hour, 12) => "12hour",
        (BarAggregation.Day, 1) => "1day",
        (BarAggregation.Week, 1) => "1week",
        (BarAggregation.Month, 1) => "1month",
        _ => throw new NotSupportedException($"KuCoin does not support bar specification {spec}."),
    };

    public static KucoinCredentials Credentials(IKucoinSettings s) => new(
        Secrets.Require(s.ApiKey, EnvApiKey),
        Secrets.Require(s.ApiSecret, EnvApiSecret),
        Secrets.Require(s.ApiPassphrase, EnvApiPassphrase),
        StatedApiKeyVersion(s) ?? DefaultApiKeyVersion);

    public static KucoinCredentials? OptionalCredentials(IKucoinSettings s)
    {
        string? key = Secrets.Optional(s.ApiKey, EnvApiKey);
        string? secret = Secrets.Optional(s.ApiSecret, EnvApiSecret);
        string? passphrase = Secrets.Optional(s.ApiPassphrase, EnvApiPassphrase);
        return key is null || secret is null || passphrase is null ? null : new KucoinCredentials(key, secret, passphrase, StatedApiKeyVersion(s) ?? DefaultApiKeyVersion);
    }
}

/// <summary>A KuCoin API key has three parts, and the venue wants to be told which version of key it is.</summary>
public sealed record KucoinCredentials(string Key, string Secret, string Passphrase, string KeyVersion)
{
    // Never the secret or the passphrase in a log line or an exception text.
    public override string ToString() => $"KucoinCredentials(key version {KeyVersion})";
}

/// <summary>
/// KuCoin REST access with request signing. Every answer is an envelope <c>{ code, data }</c>; a code other than 200000 is an error,
/// whatever the HTTP status says.
/// </summary>
public sealed class KucoinHttp : IDisposable
{
    private readonly HttpClientWrapper _http;
    private readonly KucoinCredentials? _credentials;
    private readonly ILogger? _logger;
    private readonly object _versionGate = new();
    private string _keyVersion;
    private bool _versionSettled;

    public KucoinHttp(IKucoinSettings settings, ILogger? logger = null, bool requireCredentials = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _credentials = requireCredentials ? KucoinVenue.Credentials(settings) : KucoinVenue.OptionalCredentials(settings);
        _http = new HttpClientWrapper(new Uri(KucoinVenue.HttpBase(settings)), new RateLimiter(KucoinVenue.RequestsPerWindow, KucoinVenue.RequestWindow), new RetryPolicy(), logger);
        ProductType = settings.ProductType;
        _logger = logger;
        _keyVersion = _credentials?.KeyVersion ?? KucoinVenue.DefaultApiKeyVersion;

        // Stated, so not to be second-guessed: a person who wrote the version down gets the refusal the venue gives
        // for it rather than a quiet retry under another one.
        _versionSettled = KucoinVenue.StatedApiKeyVersion(settings) is not null;
    }

    public bool HasCredentials => _credentials is not null;

    /// <summary>
    /// Which of the venue's markets this client is pointed at. Carried so that a helper handed nothing but an http
    /// client answers for the right one: spot and futures differ in the shape of a candle row, not only in the path.
    /// </summary>
    public KucoinProductType ProductType { get; }

    /// <summary>The version requests are being signed with, which is the default until a refusal says otherwise.</summary>
    public string KeyVersion
    {
        get
        {
            lock (_versionGate)
            {
                return _keyVersion;
            }
        }
    }

    public Task<JsonElement> GetPublicAsync(string path, IReadOnlyDictionary<string, string>? query = null, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, path, query, null, signed: false, ct);

    public Task<JsonElement> PostPublicAsync(string path, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, path, null, null, signed: false, ct);

    public Task<JsonElement> GetSignedAsync(string path, IReadOnlyDictionary<string, string>? query = null, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, path, query, null, signed: true, ct);

    public Task<JsonElement> DeleteSignedAsync(string path, IReadOnlyDictionary<string, string>? query = null, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, path, query, null, signed: true, ct);

    public Task<JsonElement> PostSignedAsync(string path, object? body, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, path, null, body is null ? string.Empty : JsonSerializer.Serialize(body), signed: true, ct);

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, IReadOnlyDictionary<string, string>? query, string? json, bool signed, CancellationToken ct)
    {
        try
        {
            return await SendOnceAsync(method, path, query, json, signed, ct).ConfigureAwait(false);
        }
        catch (KucoinApiException e) when (signed && TrySettleOnTheOtherKeyVersion(e))
        {
            // The same request, signed as the other version. Only ever one extra attempt, and only while the version
            // is still a guess: once a request has succeeded, or once one has failed for a reason that is not the
            // version, this stops happening.
            return await SendOnceAsync(method, path, query, json, signed, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether this refusal is worth trying the other key version for, and if so switches to it. A key version is
    /// only ever guessed when nothing stated one, and it is only ever guessed once.
    /// </summary>
    private bool TrySettleOnTheOtherKeyVersion(KucoinApiException refusal)
    {
        if (KucoinVenue.ErrorsThatAreNotTheKeyVersion.Contains(refusal.Code))
        {
            return false;
        }

        lock (_versionGate)
        {
            if (_versionSettled)
            {
                return false;
            }

            _keyVersion = _keyVersion == KucoinVenue.DefaultApiKeyVersion
                ? KucoinVenue.FallbackApiKeyVersion
                : KucoinVenue.DefaultApiKeyVersion;
            _versionSettled = true;
        }

        _logger?.LogInformation(
            "KuCoin refused a signed request with {Code}; signing as key version {Version} from here on. Set {Variable} to skip this.",
            refusal.Code,
            _keyVersion,
            KucoinVenue.EnvApiKeyVersion);

        return true;
    }

    private async Task<JsonElement> SendOnceAsync(HttpMethod method, string path, IReadOnlyDictionary<string, string>? query, string? json, bool signed, CancellationToken ct)
    {
        // The signature covers the path with its query exactly as it is sent, so the query is put into the path here.
        string endpoint = query is { Count: > 0 } ? path + "?" + HttpClientWrapper.BuildQuery(query) : path;
        Dictionary<string, string>? headers = signed ? Sign(method.Method, endpoint, json ?? string.Empty) : null;
        StringContent? content = string.IsNullOrEmpty(json) ? null : new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        string text;
        try
        {
            text = await _http.SendAsync(method, endpoint, null, content, headers, 1, ct).ConfigureAwait(false);
        }
        catch (VenueHttpException e) when (TryError(e.Body) is { } error)
        {
            throw new KucoinApiException(error.Code, error.Message, (int)e.StatusCode);
        }

        JsonElement data = Unwrap(text);

        if (signed)
        {
            // It worked, so stop guessing.
            lock (_versionGate)
            {
                _versionSettled = true;
            }
        }

        return data;
    }

    private Dictionary<string, string> Sign(string method, string endpoint, string body)
    {
        KucoinCredentials c = _credentials ?? throw new InvalidOperationException("KuCoin API credentials are required for this request.");
        string timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        return new Dictionary<string, string>
        {
            ["KC-API-KEY"] = c.Key,
            ["KC-API-SIGN"] = HmacSigner.Sha256Base64(c.Secret, timestamp + method + endpoint + body),
            ["KC-API-TIMESTAMP"] = timestamp,
            // From key version 2 on the passphrase travels signed with the secret, never in clear.
            ["KC-API-PASSPHRASE"] = HmacSigner.Sha256Base64(c.Secret, c.Passphrase),
            ["KC-API-KEY-VERSION"] = KeyVersion,
        };
    }

    private static JsonElement Unwrap(string text)
    {
        using JsonDocument doc = JsonDocument.Parse(text);
        JsonElement root = doc.RootElement;
        string code = root.Str("code");
        if (code.Length > 0 && code != KucoinVenue.Ok)
        {
            // The venue answers a refused request with HTTP 200 and its own code in the body.
            throw new KucoinApiException(code, root.Str("msg"), (int)HttpStatusCode.OK);
        }

        return root.TryGetProperty("data", out JsonElement data) ? data.Clone() : root.Clone();
    }

    private static (string Code, string Message)? TryError(string body)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            string code = doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Str("code") : string.Empty;
            return code.Length > 0 ? (code, doc.RootElement.Str("msg")) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}

public sealed class KucoinApiException : Exception
{
    public KucoinApiException(string code, string message, int httpStatus)
        : base($"KuCoin error {code}: {message}")
    {
        Code = code;
        Msg = message;
        HttpStatus = httpStatus;
    }

    public string Code { get; }

    public string Msg { get; }

    public int HttpStatus { get; }
}

internal static class Json
{
    public static decimal Dec(this JsonElement e, string name) => e.TryGetProperty(name, out JsonElement p) ? p.DecValue() : 0m;

    public static decimal DecValue(this JsonElement p) => p.ValueKind switch
    {
        JsonValueKind.Number => p.GetDecimal(),
        JsonValueKind.String => decimal.TryParse(p.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal d) ? d : 0m,
        _ => 0m,
    };

    public static string Str(this JsonElement e, string name) => e.TryGetProperty(name, out JsonElement p) ? p.ValueKind switch
    {
        JsonValueKind.String => p.GetString() ?? string.Empty,
        JsonValueKind.Number => p.GetRawText(),
        _ => string.Empty,
    } : string.Empty;

    public static long Long(this JsonElement e, string name) => e.TryGetProperty(name, out JsonElement p) ? p.LongValue() : 0;

    public static long LongValue(this JsonElement p) => p.ValueKind switch
    {
        JsonValueKind.Number => p.GetInt64(),
        JsonValueKind.String => long.TryParse(p.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long l) ? l : 0,
        _ => 0,
    };

    public static bool Bool(this JsonElement e, string name) => e.TryGetProperty(name, out JsonElement p) && (p.ValueKind == JsonValueKind.True || (p.ValueKind == JsonValueKind.String && p.GetString() == "true"));

    public static bool Has(this JsonElement e, string name) => e.TryGetProperty(name, out JsonElement p) && p.ValueKind != JsonValueKind.Null;

    public static UnixNanos Ms(this JsonElement e, string name) => UnixNanos.FromMilliseconds(e.Long(name));

    public static UnixNanos Ns(this JsonElement e, string name) => new(e.Long(name));

    public static string Fmt(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    public static byte Precision(decimal increment)
    {
        string text = increment.ToString(CultureInfo.InvariantCulture);
        if (text.Contains('.', StringComparison.Ordinal))
        {
            text = text.TrimEnd('0');
        }

        int dot = text.IndexOf('.', StringComparison.Ordinal);
        return (byte)(dot < 0 ? 0 : text.Length - dot - 1);
    }
}

/// <summary>
/// Loads spot instrument definitions from <c>/api/v2/symbols</c>.
/// </summary>
public sealed class KucoinInstrumentProvider : InstrumentProviderBase
{
    /// <summary>The venue's base spot rate; a symbol's own rate is this times its fee coefficient.</summary>
    private const decimal BaseFee = 0.001m;

    private readonly KucoinHttp _http;

    public KucoinInstrumentProvider(KucoinHttp http, InstrumentProviderConfig? config = null, ILogger? logger = null)
        : base(KucoinVenue.Venue, config, logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public override async Task LoadAllAsync(CancellationToken ct, IReadOnlyDictionary<string, string>? filters = null)
    {
        JsonElement data = await _http.GetPublicAsync("/api/v2/symbols", null, ct).ConfigureAwait(false);
        int loaded = 0;
        foreach (JsonElement item in data.EnumerateArray())
        {
            Instrument? instrument = Parse(item);
            if (instrument is null)
            {
                continue;
            }

            if (filters is not null && filters.TryGetValue("quote", out string? quote) && !instrument.QuoteCurrency.Code.Equals(quote, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Add(instrument);
            loaded++;
        }

        Log.LogInformation("Loaded {Count} KuCoin spot instruments", loaded);
    }

    public override async Task LoadAsync(InstrumentId id, CancellationToken ct)
    {
        JsonElement item;
        try
        {
            item = await _http.GetPublicAsync("/api/v2/symbols/" + Uri.EscapeDataString(KucoinVenue.ToRawSymbol(id)), null, ct).ConfigureAwait(false);
        }
        catch (KucoinApiException e) when (KucoinVenue.ErrorsThatMeanNoSuchInstrument.Contains(e.Code))
        {
            // Not listed is an answer, not a failure. Only this venue knows its code for it, which is the point:
            // a caller looks the instrument up afterwards and finds nothing, the same on every venue.
            Log.LogInformation("KuCoin does not list {Instrument}", id);
            return;
        }

        if (item.ValueKind == JsonValueKind.Object && Parse(item) is { } instrument && instrument.Id == id)
        {
            Add(instrument);
        }
    }

    private static Instrument? Parse(JsonElement item)
    {
        if (!item.Bool("enableTrading"))
        {
            return null;
        }

        string raw = item.Str("symbol");
        decimal tick = item.Dec("priceIncrement");
        decimal step = item.Dec("baseIncrement");
        if (raw.Length == 0 || tick <= 0m || step <= 0m)
        {
            return null;
        }

        byte pricePrecision = Json.Precision(tick);
        byte sizePrecision = Json.Precision(step);
        Currency quote = Currency.FromCode(item.Str("quoteCurrency"), 8);
        Currency baseCurrency = Currency.FromCode(item.Str("baseCurrency"), 8);
        decimal minQty = item.Dec("baseMinSize");
        decimal maxQty = item.Dec("baseMaxSize");
        decimal minFunds = item.Dec("minFunds");
        decimal makerCoefficient = item.Has("makerFeeCoefficient") ? item.Dec("makerFeeCoefficient") : 1m;
        decimal takerCoefficient = item.Has("takerFeeCoefficient") ? item.Dec("takerFeeCoefficient") : 1m;
        UnixNanos now = UnixNanos.FromDateTimeOffset(DateTimeOffset.UtcNow);

        return new CurrencyPair(new InstrumentSpec
        {
            Id = KucoinVenue.ToInstrumentId(raw),
            RawSymbol = new Symbol(raw),
            AssetClass = AssetClass.Crypto,
            InstrumentClass = InstrumentClass.Spot,
            QuoteCurrency = quote,
            BaseCurrency = baseCurrency,
            SettlementCurrency = quote,
            PricePrecision = pricePrecision,
            SizePrecision = sizePrecision,
            PriceIncrement = new Price(tick, pricePrecision),
            SizeIncrement = new Quantity(step, sizePrecision),
            MinQuantity = minQty > 0m ? new Quantity(minQty, sizePrecision) : null,
            MaxQuantity = maxQty > 0m && maxQty < KucoinVenue.NoSizeLimit ? new Quantity(maxQty, sizePrecision) : null,
            MinNotional = minFunds > 0m ? new Money(minFunds, quote) : null,
            MakerFee = BaseFee * makerCoefficient,
            TakerFee = BaseFee * takerCoefficient,
            TsEvent = now,
            TsInit = now,
        });
    }
}
