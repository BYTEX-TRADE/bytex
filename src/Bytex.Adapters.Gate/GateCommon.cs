using System.Globalization;
using System.Security.Cryptography;
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

namespace Bytex.Adapters.Gate;

/// <summary>
/// Which of Gate's markets a client talks to. One API version covers all three, and the family is chosen by the path:
/// <c>/spot</c>, <c>/futures/{settle}</c> and <c>/delivery/{settle}</c>. They are not three spellings of one market -
/// a spot amount is in base currency and a futures size is a whole number of contracts, a spot candle is an array and
/// a futures candle is an object, and only one of the three can amend an order - so a client has to be told which.
/// </summary>
public enum GateProductType
{
    /// <summary>Spot pairs, on <c>/spot</c>. The default, so a configuration that says nothing means spot.</summary>
    Spot,

    /// <summary>USDT-settled linear perpetual contracts, on <c>/futures/usdt</c>.</summary>
    Futures,

    /// <summary>USDT-settled dated (delivery) futures, on <c>/delivery/usdt</c>.</summary>
    Delivery,
}

/// <summary>
/// What a Gate client needs configuring with. The key is two parts and no more: Gate signs with an API key and a
/// secret and has no passphrase and no key version, which is worth stating because the three venues either side of it
/// in this repository all have a third part and a host carrying one would look for a field that does not exist.
/// </summary>
public interface IGateSettings
{
    string? ApiKey { get; }

    string? ApiSecret { get; }

    /// <summary>Which of the venue's three markets this client talks to. Spot by default.</summary>
    GateProductType ProductType { get; }

    string? BaseUrlHttp { get; }

    string? BaseUrlWs { get; }
}

public sealed record GateDataClientConfig : DataClientConfig, IGateSettings
{
    public string? ApiKey { get; init; }

    public string? ApiSecret { get; init; }

    public GateProductType ProductType { get; init; } = GateProductType.Spot;

    public string? BaseUrlHttp { get; init; }

    public string? BaseUrlWs { get; init; }
}

public sealed record GateExecutionClientConfig : ExecutionClientConfig, IGateSettings
{
    public string? ApiKey { get; init; }

    public string? ApiSecret { get; init; }

    public GateProductType ProductType { get; init; } = GateProductType.Spot;

    public string? BaseUrlHttp { get; init; }

    public string? BaseUrlWs { get; init; }
}

/// <summary>
/// What Gate is, measured against the live venue on 2026-09-25 rather than read off its documentation. Every number
/// here was asked for and counted, because five of them differ from what is written down and each of the five is the
/// silent kind: a plausible wrong answer rather than an error.
/// </summary>
public static class GateVenue
{
    public static readonly Venue Venue = new("GATE");

    public const string EnvApiKey = "GATE_API_KEY";
    public const string EnvApiSecret = "GATE_API_SECRET";

    /// <summary>
    /// Where spot answers when nothing is configured. This host serves all three of the venue's markets; the futures
    /// host below serves only the two derivative ones, which is what makes it the honest default for them.
    /// </summary>
    public const string DefaultHttpBase = "https://api.gateio.ws";

    /// <summary>
    /// The spot socket. A fixed address rather than one handed out per connection, and the trailing slash is the
    /// venue's own: the path is <c>/ws/v4/</c> and it answers 101 only at that spelling.
    /// </summary>
    public const string DefaultWsBase = "wss://api.gateio.ws/ws/v4/";

    /// <summary>Every path this adapter asks for is under this prefix, and the prefix is part of what is signed.</summary>
    public const string ApiPrefix = "/api/v4";

    /// <summary>
    /// Requests the venue allows in <see cref="RequestWindow"/>, per endpoint. Measured from the venue's own
    /// <c>X-Gate-RateLimit-Limit</c> header, which answered 200 on both the spot and the futures endpoints asked.
    /// The docs site still shows an older table of 900 per second; the header is what the venue is enforcing.
    /// </summary>
    public const int RequestsPerWindow = 200;

    /// <summary>The window <see cref="RequestsPerWindow"/> is counted over.</summary>
    public static readonly TimeSpan RequestWindow = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The most candle rows one spot request answers with, and the venue validates it: 1001 is refused outright with
    /// <c>INVALID_PARAM_VALUE</c>. A window given as <c>from</c>..<c>to</c> is capped separately - see
    /// <see cref="SpotCandleSpan"/> - and the two caps are not the same number, which is the trap.
    /// </summary>
    public const int SpotCandlePage = 1000;

    /// <summary>
    /// The widest <c>from</c>..<c>to</c> window spot will answer, counted in intervals. Measured: a span of 999
    /// intervals returns 1000 rows, and 1000 intervals is refused with "Candlestick range too broad. Maximum 1000
    /// data points are allowed per request". Both ends of the window are inclusive, so a span of N intervals is N+1
    /// rows - which is why this is one less than the page.
    /// </summary>
    public const int SpotCandleSpan = SpotCandlePage - 1;

    /// <summary>
    /// How many funding settlements one request answers with. The venue accepts up to 1000 and the limit is not what
    /// binds - see <see cref="FundingWindow"/>.
    /// </summary>
    public const int FundingPage = 1000;

    /// <summary>
    /// The width of one page of funding history, which is what really bounds the endpoint. Measured on three
    /// contracts with three different funding intervals: BTC_USDT (8 h) answered 90 rows, ACT_USDT (4 h) answered
    /// 180, and CXMT_USDT (1 h) answered 720 - thirty days in every case, and 720 is well under the 1000 the limit
    /// parameter allows. A paging loop that stopped when it got fewer rows than it asked for would read one month
    /// and call it the whole history.
    /// </summary>
    public static readonly TimeSpan FundingWindow = TimeSpan.FromDays(30);

    /// <summary>
    /// How far back funding history may be asked for. The venue refuses an older <c>from</c> with
    /// <c>INVALID_PARAM_VALUE</c> and the message "from time exceeds 180-day limit"; 179 days back is answered and
    /// 181 is not. So a request for a longer period is not a slow request, it is a refused one.
    /// </summary>
    public static readonly TimeSpan FundingHistoryReach = TimeSpan.FromDays(180);

    /// <summary>
    /// The longest client order id Gate accepts, counted after the prefix below. The venue carries a client id in
    /// the order's <c>text</c> field, and the documented rule is a <c>t-</c> prefix and at most 28 bytes after it.
    /// </summary>
    public const int MaxClientOrderIdLength = 28;

    /// <summary>
    /// What the venue demands in front of a user's own order id in the <c>text</c> field. It is not part of the id
    /// anything above the adapter holds, so it is added on the way out and stripped on the way back - and it is a
    /// named constant because an order sent without it is refused and an id read back with it still on would not
    /// match the order this node placed.
    /// </summary>
    public const string ClientOrderIdPrefix = "t-";

    /// <summary>
    /// The characters the venue allows in the <c>text</c> field, besides letters and digits. An id outside this set
    /// is refused, so it is checked before an order is sent rather than after.
    /// </summary>
    public const string ClientOrderIdExtraCharacters = "_-.";

    /// <summary>
    /// How often to ping the socket. Gate's own client sends this, and the ping travels as a JSON message on the
    /// application channel rather than as a WebSocket control frame - see <c>GateStream.Ping</c>.
    /// </summary>
    public static readonly TimeSpan DefaultPingInterval = TimeSpan.FromSeconds(20);

    /// <summary>
    /// What the venue answers when asked about a pair or a contract it does not list. Each is a refusal of a
    /// well-formed question, so asking about an unlisted instrument leaves the provider empty rather than throwing -
    /// which is what every venue in this repository does, and what lets a caller look the instrument up afterwards
    /// without knowing which venue answered.
    /// <para>
    /// There are THREE of them and spot needs two, which is only visible by asking. An unlisted pair whose spelling
    /// is well formed - <c>BOGUS_USDT</c> - is refused with <c>INVALID_CURRENCY</c> and the message "Invalid
    /// currency BOGUS", while a pair written without the underscore - <c>BTCUSDT</c>, which is how every other
    /// venue in this repository spells it and therefore the likeliest mistake on this one - is refused with
    /// <c>INVALID_CURRENCY_PAIR</c>. Holding only the second would have let the first arrive as a venue error.
    /// </para>
    /// <para>
    /// Both derivative markets answer <c>CONTRACT_NOT_FOUND</c> and, measured, send NO message field with it at all.
    /// </para>
    /// </summary>
    public static readonly IReadOnlySet<string> ErrorsThatMeanNoSuchInstrument =
        new HashSet<string>(StringComparer.Ordinal) { "INVALID_CURRENCY", "INVALID_CURRENCY_PAIR", "CONTRACT_NOT_FOUND" };

    /// <summary>
    /// The spot statuses that mean an order can be placed on both sides. The venue also publishes <c>sellable</c>,
    /// which takes sell orders only while a pair is being delisted, and <c>untradable</c>, which takes none; nine of
    /// the 2229 pairs listed were in one of those two states when this was measured. Publishing a sell-only pair as
    /// tradable would have a strategy's buy refused by the venue with nothing above the adapter able to say why.
    /// </summary>
    public const string SpotTradable = "tradable";

    /// <summary>
    /// The venue's own fee field on a spot pair is a PERCENTAGE - <c>"fee": "0.2"</c> is twenty basis points - and
    /// everything in this engine carries a fee as a fraction of the trade. Dividing is not optional and it is not
    /// obvious: read as written, every backtest on this venue would pay a hundred times the real commission.
    /// </summary>
    public const decimal SpotFeePercentToFraction = 100m;

    /// <summary>Gate names a pair BASE_QUOTE with an underscore, and the instrument id keeps that name exactly.</summary>
    public static InstrumentId ToInstrumentId(string rawSymbol) => new(new Symbol(rawSymbol), Venue);

    /// <summary>
    /// The venue's own name for an instrument, which is <see cref="ToInstrumentId"/> run backwards. The underscore is
    /// this venue's and no other's in this repository - Binance writes BTCUSDT, Bybit BTCUSDT, KuCoin BTC-USDT - so
    /// the round trip is pinned by a test in both directions rather than assumed to be the identity it looks like.
    /// </summary>
    public static string ToRawSymbol(InstrumentId id) => id.Symbol.Value;

    /// <summary>
    /// Where this client's REST calls go when nothing is configured. Spot answers only on the general host; the two
    /// derivative families answer on both, and the derivative host is declared for them because it is the one the
    /// venue documents for derivatives and the one that tells the families apart.
    /// </summary>
    public static string HttpBase(IGateSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.BaseUrlHttp ?? (s.ProductType == GateProductType.Spot
            ? DefaultHttpBase
            : GateFuturesVenue.DefaultHttpBase);
    }

    /// <summary>Where this client's socket connects when nothing is configured.</summary>
    public static string WsBase(IGateSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.BaseUrlWs ?? s.ProductType switch
        {
            GateProductType.Futures => GateFuturesVenue.DefaultWsBase,
            GateProductType.Delivery => GateFuturesVenue.DefaultDeliveryWsBase,
            _ => DefaultWsBase,
        };
    }

    /// <summary>
    /// What the venue calls a bar of the given length. Measured against the live endpoint rather than taken from the
    /// documented list, because the venue keeps several lengths the documentation does not mention (10 s, 30 s, 3 m,
    /// 6 h, 3 d) and refuses anything it does not keep with <c>INVALID_PARAM_VALUE</c> rather than answering
    /// something close to it. A week is <c>7d</c>: the venue also accepts <c>1w</c> and answers it with byte-identical
    /// rows, so only one of the two spellings is used here.
    /// <para>
    /// A month is deliberately absent. The venue accepts <c>30d</c> and answers rows 31 days apart, so it is neither
    /// thirty days nor a calendar month, and there is no length this engine could ask for that it would answer
    /// correctly.
    /// </para>
    /// </summary>
    public static string Interval(BarSpecification spec) => (spec.Aggregation, spec.Step) switch
    {
        (BarAggregation.Second, 10) => "10s",
        (BarAggregation.Second, 30) => "30s",
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
        (BarAggregation.Week, 1) => "7d",
        _ => throw new NotSupportedException(
            $"Gate does not keep {spec} candles. It keeps 10 and 30 seconds, 1, 3, 5, 15 and 30 minutes, 1, 2, 4, 6, "
            + "8 and 12 hours, a day, three days and a week."),
    };

    /// <summary>
    /// The id the venue carries in an order's <c>text</c> field, or null when this id cannot be carried. Null is an
    /// answer rather than a failure: the caller refuses the order and says which rule it broke, which is the only way
    /// a strategy finds out before the venue does.
    /// </summary>
    public static string? ToOrderText(ClientOrderId id)
    {
        string value = id.Value;
        if (value.Length == 0 || value.Length > MaxClientOrderIdLength)
        {
            return null;
        }

        foreach (char c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && !ClientOrderIdExtraCharacters.Contains(c, StringComparison.Ordinal))
            {
                return null;
            }
        }

        return ClientOrderIdPrefix + value;
    }

    /// <summary>
    /// The engine's id behind an order's <c>text</c> field, or null when the field holds none. The venue writes its
    /// own values there for orders it raised itself - <c>web</c>, <c>api</c>, <c>liquidation</c>, <c>insurance</c> -
    /// and none of those carries the prefix, so the prefix is what tells this node's orders from everybody else's.
    /// </summary>
    public static ClientOrderId? FromOrderText(string? text) =>
        text is not null && text.StartsWith(ClientOrderIdPrefix, StringComparison.Ordinal) && text.Length > ClientOrderIdPrefix.Length
            ? new ClientOrderId(text[ClientOrderIdPrefix.Length..])
            : null;

    public static GateCredentials Credentials(IGateSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new GateCredentials(Secrets.Require(s.ApiKey, EnvApiKey), Secrets.Require(s.ApiSecret, EnvApiSecret));
    }

    public static GateCredentials? OptionalCredentials(IGateSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        string? key = Secrets.Optional(s.ApiKey, EnvApiKey);
        string? secret = Secrets.Optional(s.ApiSecret, EnvApiSecret);
        return key is null || secret is null ? null : new GateCredentials(key, secret);
    }
}

/// <summary>A Gate API key has two parts and nothing else - no passphrase, no version.</summary>
public sealed record GateCredentials(string Key, string Secret)
{
    // Never the secret in a log line or an exception text.
    public override string ToString() => "GateCredentials(key " + (Key.Length <= 4 ? "set" : "…" + Key[^4..]) + ")";
}

/// <summary>
/// Gate REST access with request signing. Unlike the three venues either side of it there is no envelope: a
/// successful answer is the payload itself, and a refusal is an HTTP error status with a body carrying a
/// <c>label</c> and a <c>message</c>. So the status is the truth here, which is the opposite of KuCoin.
/// </summary>
public sealed class GateHttp : IDisposable
{
    private readonly HttpClientWrapper _http;
    private readonly GateCredentials? _credentials;

    public GateHttp(IGateSettings settings, ILogger? logger = null, bool requireCredentials = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _credentials = requireCredentials ? GateVenue.Credentials(settings) : GateVenue.OptionalCredentials(settings);
        _http = new HttpClientWrapper(
            new Uri(GateVenue.HttpBase(settings)),
            new RateLimiter(GateVenue.RequestsPerWindow, GateVenue.RequestWindow),
            new RetryPolicy(),
            logger);
        ProductType = settings.ProductType;
    }

    public bool HasCredentials => _credentials is not null;

    /// <summary>
    /// Which of the venue's markets this client is pointed at. Carried so that a helper handed nothing but an http
    /// client answers for the right one: the three families differ in the shape of a candle row, not only in the path.
    /// </summary>
    public GateProductType ProductType { get; }

    public Task<JsonElement> GetPublicAsync(string path, IReadOnlyDictionary<string, string>? query = null, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, path, query, null, signed: false, ct);

    public Task<JsonElement> GetSignedAsync(string path, IReadOnlyDictionary<string, string>? query = null, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, path, query, null, signed: true, ct);

    public Task<JsonElement> PostSignedAsync(string path, object? body, IReadOnlyDictionary<string, string>? query = null, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, path, query, body is null ? string.Empty : JsonSerializer.Serialize(body), signed: true, ct);

    public Task<JsonElement> PutSignedAsync(string path, object? body, IReadOnlyDictionary<string, string>? query = null, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, path, query, body is null ? string.Empty : JsonSerializer.Serialize(body), signed: true, ct);

    public Task<JsonElement> PatchSignedAsync(string path, object? body, IReadOnlyDictionary<string, string>? query = null, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Patch, path, query, body is null ? string.Empty : JsonSerializer.Serialize(body), signed: true, ct);

    public Task<JsonElement> DeleteSignedAsync(string path, IReadOnlyDictionary<string, string>? query = null, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, path, query, null, signed: true, ct);

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, IReadOnlyDictionary<string, string>? query, string? json, bool signed, CancellationToken ct)
    {
        string endpoint = GateVenue.ApiPrefix + path;
        string queryString = query is { Count: > 0 } ? HttpClientWrapper.BuildQuery(query) : string.Empty;
        Dictionary<string, string>? headers = signed ? Sign(method.Method, endpoint, queryString, json ?? string.Empty) : null;
        StringContent? content = string.IsNullOrEmpty(json)
            ? null
            : new StringContent(json, Encoding.UTF8, new System.Net.Http.Headers.MediaTypeHeaderValue("application/json"));

        string text;
        try
        {
            text = await _http
                .SendAsync(method, queryString.Length > 0 ? endpoint + "?" + queryString : endpoint, null, content, headers, 1, ct)
                .ConfigureAwait(false);
        }
        catch (VenueHttpException e)
        {
            (string label, string message) = ReadError(e.Body);
            throw new GateApiException(label, message, (int)e.StatusCode);
        }

        // An empty body is a legitimate answer to a cancel-all that found nothing, and JsonDocument.Parse refuses it.
        if (string.IsNullOrWhiteSpace(text))
        {
            return default;
        }

        using JsonDocument doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// The five lines Gate signs, in order: the method, the path with its <c>/api/v4</c> prefix, the query string
    /// exactly as sent, the hex SHA-512 of the body, and the timestamp in SECONDS. Two of those are easy to get
    /// wrong in a way that only shows as a rejected signature: the body's hash is the hash of the EMPTY STRING when
    /// there is no body rather than an empty field, and the timestamp is seconds where most venues take milliseconds.
    /// </summary>
    private Dictionary<string, string> Sign(string method, string endpoint, string queryString, string body)
    {
        GateCredentials c = _credentials ?? throw new InvalidOperationException("Gate API credentials are required for this request.");
        string timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        string payloadHash = Convert.ToHexStringLower(SHA512.HashData(Encoding.UTF8.GetBytes(body)));
        string signed = string.Join('\n', method, endpoint, queryString, payloadHash, timestamp);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["KEY"] = c.Key,
            ["Timestamp"] = timestamp,
            ["SIGN"] = HmacSigner.Sha512Hex(c.Secret, signed),
        };
    }

    /// <summary>
    /// The label and message out of a refusal body. The label is the venue's machine-readable reason
    /// (<c>INVALID_PARAM_VALUE</c>, <c>CONTRACT_NOT_FOUND</c>) and the message the sentence a person reads; a body
    /// that is not JSON at all - the CDN's own HTML on a bad gateway - leaves the label empty rather than throwing,
    /// because a client that cannot report a 502 is worse than one that reports it with no label.
    /// </summary>
    private static (string Label, string Message) ReadError(string body)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (string.Empty, body);
            }

            string label = doc.RootElement.Str("label");
            string message = doc.RootElement.Str("message");
            if (message.Length == 0)
            {
                // Delivery answers with `detail` where futures and spot answer with `message`, for the same refusal.
                message = doc.RootElement.Str("detail");
            }

            return (label, message.Length > 0 ? message : body);
        }
        catch (JsonException)
        {
            return (string.Empty, LogText.Truncate(body, LogText.MaxBodyLength));
        }
    }

    public void Dispose() => _http.Dispose();
}

public sealed class GateApiException : Exception
{
    public GateApiException(string label, string message, int httpStatus)
        : base(label.Length > 0 ? $"Gate error {label}: {message}" : "Gate error: " + message)
    {
        Label = label;
        Msg = message;
        HttpStatus = httpStatus;
    }

    /// <summary>The venue's machine-readable reason, or empty when the body carried none.</summary>
    public string Label { get; }

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
        // A number that is not an integer appears where the venue stamps in fractional seconds, so it is truncated
        // rather than refused: GetInt64 throws on 1790359375.258 and the whole message would be lost with it.
        JsonValueKind.Number => p.TryGetInt64(out long l) ? l : (long)p.GetDecimal(),
        JsonValueKind.String => long.TryParse(p.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long s)
            ? s
            : decimal.TryParse(p.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal d) ? (long)d : 0,
        _ => 0,
    };

    public static bool Bool(this JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement p)
        && (p.ValueKind == JsonValueKind.True || (p.ValueKind == JsonValueKind.String && p.GetString() == "true"));

    public static bool Has(this JsonElement e, string name) => e.TryGetProperty(name, out JsonElement p) && p.ValueKind != JsonValueKind.Null;

    /// <summary>
    /// A field Gate stamps in SECONDS, as nanoseconds. Gate counts in seconds nearly everywhere - candles, funding,
    /// contract launch times, the signature's own timestamp - where the rest of this repository's venues count in
    /// milliseconds, and the difference never fails: it silently places a bar in 1970.
    /// </summary>
    public static UnixNanos Seconds(this JsonElement e, string name) => UnixNanos.FromSeconds(e.Long(name));

    /// <summary>
    /// A field Gate stamps in fractional seconds, as nanoseconds, keeping the fraction. The two trade endpoints both
    /// call this field <c>create_time_ms</c> and neither is in milliseconds: spot sends the string
    /// <c>"1790359364969.221000"</c>, which IS milliseconds, and futures sends the number
    /// <c>1790359375.258</c>, which is seconds. Reading the futures one as milliseconds puts the trade in 1970;
    /// reading the spot one as seconds puts it 56000 years out. So the two are read by different helpers.
    /// </summary>
    public static UnixNanos FractionalSeconds(this JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement p)
            ? new UnixNanos((long)(p.DecValue() * UnixNanos.NanosPerSecond))
            : default;

    /// <summary>A field Gate stamps in fractional milliseconds, as nanoseconds, keeping the fraction.</summary>
    public static UnixNanos FractionalMilliseconds(this JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement p)
            ? new UnixNanos((long)(p.DecValue() * UnixNanos.NanosPerMillisecond))
            : default;

    /// <summary>A field Gate stamps in whole milliseconds, as nanoseconds. Only the sockets do this.</summary>
    public static UnixNanos Ms(this JsonElement e, string name) => UnixNanos.FromMilliseconds(e.Long(name));

    public static string Fmt(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// How many decimal places an increment has. Gate publishes a spot pair's price precision as a COUNT of places
    /// and a futures contract's as an INCREMENT ("0.1"), so both forms reach this file and only one of them goes
    /// through here.
    /// </summary>
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

    /// <summary>An increment of the given number of decimal places, which is the form the engine holds.</summary>
    public static decimal IncrementOf(int places) => places <= 0 ? 1m : 1m / Pow10(places);

    private static decimal Pow10(int places)
    {
        decimal value = 1m;
        for (int i = 0; i < places; i++)
        {
            value *= 10m;
        }

        return value;
    }
}

/// <summary>
/// Loads spot pairs from <c>/spot/currency_pairs</c>.
/// <para>
/// Two of this venue's own facts are handled here and are deliberately invisible above it. The fee field is a
/// percentage and becomes a fraction; and the precisions are published as COUNTS of decimal places rather than as
/// increments, so the increments are derived from them. A pair with 2 price places has a 0.01 tick, and reading the
/// count as an increment would give every pair on the venue a tick of two whole quote units.
/// </para>
/// <para>
/// Pairs that are not <c>tradable</c> are left out. The venue has two other states - <c>sellable</c>, which takes
/// sell orders only while a pair is delisted, and <c>untradable</c> - and nine of the 2229 pairs listed were in one
/// of them when this was measured. Publishing a sell-only pair as a tradable one would have a strategy's buy order
/// refused by the venue with nothing above the adapter able to explain it.
/// </para>
/// </summary>
public sealed class GateInstrumentProvider : InstrumentProviderBase
{
    private readonly GateHttp _http;

    public GateInstrumentProvider(GateHttp http, InstrumentProviderConfig? config = null, ILogger? logger = null)
        : base(GateVenue.Venue, config, logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public override async Task LoadAllAsync(CancellationToken ct, IReadOnlyDictionary<string, string>? filters = null)
    {
        JsonElement data = await _http.GetPublicAsync("/spot/currency_pairs", null, ct).ConfigureAwait(false);
        int loaded = 0;
        int notTradable = 0;
        foreach (JsonElement item in data.EnumerateArray())
        {
            if (!item.Str("trade_status").Equals(GateVenue.SpotTradable, StringComparison.Ordinal))
            {
                notTradable++;
                continue;
            }

            Instrument? instrument = Parse(item);
            if (instrument is null)
            {
                continue;
            }

            if (filters is not null && filters.TryGetValue("quote", out string? quote)
                && !instrument.QuoteCurrency.Code.Equals(quote, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Add(instrument);
            loaded++;
        }

        Log.LogInformation(
            "Loaded {Count} Gate spot pairs, leaving out {NotTradable} the venue does not currently take both sides of",
            loaded,
            notTradable);
    }

    public override async Task LoadAsync(InstrumentId id, CancellationToken ct)
    {
        JsonElement item;
        try
        {
            item = await _http
                .GetPublicAsync("/spot/currency_pairs/" + Uri.EscapeDataString(GateVenue.ToRawSymbol(id)), null, ct)
                .ConfigureAwait(false);
        }
        catch (GateApiException e) when (GateVenue.ErrorsThatMeanNoSuchInstrument.Contains(e.Label))
        {
            // Not listed is an answer, not a failure. The likeliest way to arrive here is a symbol typed without the
            // underscore, which is this venue's spelling and no other's, so it must not surface as a venue error.
            Log.LogInformation("Gate does not list the spot pair {Instrument}", id);
            return;
        }

        if (item.ValueKind == JsonValueKind.Object && Parse(item) is { } instrument && instrument.Id == id)
        {
            Add(instrument);
        }
    }

    private static Instrument? Parse(JsonElement item)
    {
        string raw = item.Str("id");
        if (raw.Length == 0)
        {
            return null;
        }

        byte pricePrecision = (byte)Math.Max(0, item.Long("precision"));
        byte sizePrecision = (byte)Math.Max(0, item.Long("amount_precision"));
        decimal tick = Json.IncrementOf(pricePrecision);
        decimal step = Json.IncrementOf(sizePrecision);
        Currency quote = Currency.FromCode(item.Str("quote"), 8);
        Currency baseCurrency = Currency.FromCode(item.Str("base"), 8);
        decimal minBase = item.Dec("min_base_amount");
        decimal maxBase = item.Dec("max_base_amount");
        decimal minQuote = item.Dec("min_quote_amount");
        decimal maxQuote = item.Dec("max_quote_amount");

        // A percentage, not a fraction: "0.2" is twenty basis points. The venue publishes one rate for both sides of
        // a pair and no separate maker figure, so both carry it.
        decimal fee = item.Dec("fee") / GateVenue.SpotFeePercentToFraction;
        UnixNanos now = UnixNanos.FromDateTimeOffset(DateTimeOffset.UtcNow);

        return new CurrencyPair(new InstrumentSpec
        {
            Id = GateVenue.ToInstrumentId(raw),
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
            MinQuantity = minBase > 0m ? new Quantity(minBase, sizePrecision) : null,
            MaxQuantity = maxBase > 0m ? new Quantity(maxBase, sizePrecision) : null,
            MinNotional = minQuote > 0m ? new Money(minQuote, quote) : null,
            MaxNotional = maxQuote > 0m ? new Money(maxQuote, quote) : null,
            MakerFee = fee,
            TakerFee = fee,
            TsEvent = now,
            TsInit = now,
        });
    }
}
