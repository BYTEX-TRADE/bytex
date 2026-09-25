using System.Globalization;
using System.Net;
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

namespace Bytex.Adapters.Kraken;

/// <summary>
/// Which of Kraken's two platforms a client talks to. They are not two products of one API the way a venue's spot and
/// linear categories usually are: they are separate platforms on separate hosts, with separate credentials, separate
/// request signing, separate error shapes and separate symbol conventions. Nothing above the adapter sees any of
/// that, but a client has to be told which platform it is.
/// </summary>
public enum KrakenProductType
{
    /// <summary>
    /// Spot, on <c>api.kraken.com</c>. The default, so a configuration that names no platform means the one whose
    /// key most people already have.
    /// </summary>
    Spot,

    /// <summary>Perpetual and dated futures, on <c>futures.kraken.com</c>.</summary>
    Futures,
}

public interface IKrakenSettings
{
    string? ApiKey { get; }

    string? ApiSecret { get; }

    /// <summary>
    /// Which of the venue's two platforms this client talks to. Spot by default, so a configuration written before
    /// futures existed still means spot.
    /// </summary>
    KrakenProductType ProductType { get; }

    string? BaseUrlHttp { get; }

    string? BaseUrlWs { get; }
}

public sealed record KrakenDataClientConfig : DataClientConfig, IKrakenSettings
{
    public string? ApiKey { get; init; }

    public string? ApiSecret { get; init; }

    public KrakenProductType ProductType { get; init; } = KrakenProductType.Spot;

    public string? BaseUrlHttp { get; init; }

    public string? BaseUrlWs { get; init; }
}

public sealed record KrakenExecutionClientConfig : ExecutionClientConfig, IKrakenSettings
{
    public string? ApiKey { get; init; }

    public string? ApiSecret { get; init; }

    public KrakenProductType ProductType { get; init; } = KrakenProductType.Spot;

    public string? BaseUrlHttp { get; init; }

    public string? BaseUrlWs { get; init; }
}

/// <summary>
/// What Kraken's spot platform is, measured against the live venue on 2026-09-25 rather than read off its
/// documentation. Its public endpoints need no key, so every number here was obtained by asking.
/// <para>
/// The venue contradicts itself about how a pair is spelled, which is the single most expensive fact about it and is
/// handled by <see cref="ToWireSymbol"/>.
/// </para>
/// </summary>
public static class KrakenVenue
{
    public static readonly Venue Venue = new("KRAKEN");

    /// <summary>
    /// The key, which is two parts on both platforms - but NOT the same two values. A spot key is issued on
    /// <c>kraken.com</c> and a futures key on <c>futures.kraken.com</c>, and neither signs for the other: the
    /// signing schemes differ as well as the credentials. The variables are shared because the shape is shared and
    /// a venue declares one key shape; which platform's key is in them follows from which family is configured.
    /// </summary>
    public const string EnvApiKey = "KRAKEN_API_KEY";

    /// <inheritdoc cref="EnvApiKey"/>
    public const string EnvApiSecret = "KRAKEN_API_SECRET";

    /// <summary>Where Kraken spot answers when nothing is configured.</summary>
    public const string DefaultHttpBase = "https://api.kraken.com";

    /// <summary>
    /// Where Kraken spot's PUBLIC socket answers. The venue serves public market data and private data on two
    /// different hosts and refuses each on the other's, in as many words: asked for the <c>executions</c> channel
    /// here it answers "Private data and trading are unavailable on this endpoint. Try ws-auth.kraken.com", and
    /// asked for <c>trade</c> on that one it answers the mirror image. So this is the base a DATA client uses;
    /// <see cref="PrivateWsHost"/> is the other half, and it is not a second base a host can be offered because a
    /// family declares one.
    /// </summary>
    public const string DefaultWsBase = "wss://ws.kraken.com";

    /// <summary>
    /// The host the private socket answers on, substituted into <see cref="DefaultWsBase"/> rather than written out
    /// as an address of its own: the scheme, the version and any override a host has configured all belong to the
    /// public base, and only the host differs.
    /// </summary>
    public const string PrivateWsHost = "ws-auth.kraken.com";

    /// <summary>The version segment every spot REST path and both socket addresses carry.</summary>
    public const string RestVersion = "/0";

    /// <summary>The socket protocol version this adapter speaks. Version 1 is a different message shape entirely.</summary>
    public const string WsVersion = "/v2";

    /// <summary>
    /// The most candle rows one <c>/0/public/OHLC</c> request answers with, and a hard limit on what the endpoint
    /// will serve at all.
    /// <para>
    /// Measured, and it is not a paging cap: <c>since=0</c> on one-minute candles returned the 720 most recent plus
    /// the one still forming, and <c>since</c> set 2000 minutes back returned the same 720. The venue clamps the
    /// window to the newest 720 intervals and offers no parameter to page further back, so one-minute history older
    /// than twelve hours cannot be fetched from this endpoint at any price. A paging loop written against it would
    /// ask for an older window, receive the newest page again, and either loop forever or report the newest bars as
    /// though they were the oldest.
    /// </para>
    /// </summary>
    public const int CandlePage = 720;

    /// <summary>
    /// Requests this adapter allows itself in <see cref="RequestWindow"/> on the public endpoints. The venue
    /// publishes a decaying counter whose size depends on the account's verification tier, which nothing
    /// unauthenticated can read; a tight unthrottled loop over 40 pairs was answered with empty bodies, so this is
    /// set to the rate the slowest tier sustains rather than to a number that happens to work from here.
    /// </summary>
    public const int RequestsPerWindow = 15;

    /// <summary>The window <see cref="RequestsPerWindow"/> is counted over.</summary>
    public static readonly TimeSpan RequestWindow = TimeSpan.FromSeconds(15);

    /// <summary>
    /// What the venue charges on spot before an instrument is loaded, from its published schedule and NOT from the
    /// API: <c>/0/public/AssetPairs</c> carries <c>fees</c> and <c>fees_maker</c> arrays for every pair and all 1451
    /// of them measured EMPTY, as did <c>?info=fees</c>. There is nothing to read, so an instrument with no rate of
    /// its own is published with these.
    /// </summary>
    public const decimal DefaultMakerFee = 0.0016m;

    /// <inheritdoc cref="DefaultMakerFee"/>
    public const decimal DefaultTakerFee = 0.0025m;

    /// <summary>
    /// The only pair status this adapter publishes. The venue also lists <c>cancel_only</c> and <c>post_only</c>
    /// pairs - 80 and 17 of the 1451 measured - and neither can take the orders a strategy sends, so publishing them
    /// would put instruments in a picker that refuse a market order.
    /// </summary>
    public const string TradingStatus = "online";

    /// <summary>
    /// What the venue says when asked about a pair it does not list. Both are refusals of a well-formed question -
    /// an unknown name and a name whose assets exist but form no pair - so asking about an instrument that is not
    /// listed leaves the provider empty rather than throwing, which is what every venue here does.
    /// </summary>
    public static readonly IReadOnlySet<string> ErrorsThatMeanNoSuchInstrument =
        new HashSet<string>(StringComparer.Ordinal) { "EQuery:Unknown asset pair", "EQuery:Invalid asset pair" };

    /// <summary>
    /// The two asset codes Kraken spells one way on its REST catalog and another way everywhere a request is
    /// accepted. This is the venue disagreeing with itself, measured across all 1451 pairs:
    /// <para>
    /// <c>/0/public/AssetPairs</c> publishes a <c>wsname</c> of <c>XBT/USD</c> for bitcoin and <c>XDG/USD</c> for
    /// dogecoin. The socket REFUSES both - "Currency pair not supported XBT/USD" - and so does REST, with
    /// "EQuery:Unknown asset pair". What both accept is <c>BTC/USD</c> and <c>DOGE/USD</c>, which is also what the
    /// socket's own <c>instrument</c> channel publishes for those pairs. Applying these two substitutions to every
    /// <c>wsname</c> reproduced the socket's list of 1451 symbols exactly, with no mismatches.
    /// </para>
    /// <para>
    /// So this is not an alias and no currency is being renamed. It is the venue's working spelling, taken from the
    /// endpoint that accepts it, because the legacy field is a name nothing on the venue answers to. On the FUTURES
    /// platform bitcoin really is XBT in the symbol - <c>PF_XBTUSD</c> - and that spelling is kept there untouched.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> LegacyAssetCodes =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["XBT"] = "BTC", ["XDG"] = "DOGE" };

    /// <summary>The separator the venue puts between the two assets of a pair, on the wire.</summary>
    public const char WireSeparator = '/';

    /// <summary>
    /// The separator an instrument id puts there instead. A slash would make the id a path fragment everywhere it is
    /// written down - a stored history file, a log line, a URL - and the venue's own asset codes contain neither
    /// character, so the two spellings convert to each other exactly and back again.
    /// </summary>
    public const char SymbolSeparator = '-';

    /// <summary>
    /// The name both the REST endpoints and the socket accept for a pair, from the <c>wsname</c> the catalog
    /// publishes. See <see cref="LegacyAssetCodes"/> for why a substitution is needed at all.
    /// </summary>
    public static string ToWireSymbol(string wsName)
    {
        ArgumentNullException.ThrowIfNull(wsName);
        int slash = wsName.IndexOf(WireSeparator, StringComparison.Ordinal);
        if (slash <= 0)
        {
            return wsName;
        }

        string baseAsset = wsName[..slash];
        string quoteAsset = wsName[(slash + 1)..];
        return Current(baseAsset) + WireSeparator + Current(quoteAsset);
    }

    /// <summary>One asset code as the venue currently answers to it.</summary>
    public static string Current(string assetCode) =>
        LegacyAssetCodes.TryGetValue(assetCode, out string? current) ? current : assetCode;

    /// <summary>Where the venue publishes its assets, and what a private balance is keyed by.</summary>
    public const string AssetsPath = RestVersion + "/public/Assets";

    /// <summary>
    /// Every asset id the venue lists, mapped to the code it currently answers to. Needed because a private balance
    /// is keyed by the ASSET ID and not by the code any instrument is denominated in: the id of the dollar is
    /// <c>ZUSD</c> and of bitcoin <c>XXBT</c>, while the pairs are quoted in <c>USD</c> and based on <c>BTC</c>.
    /// <para>
    /// A balance published under <c>XXBT</c> is a balance in a currency nothing on this venue trades, so an account
    /// holding bitcoin would look like an account holding nothing tradable. There are 849 assets and eleven of them
    /// carry a legacy id, which is why this is read from the venue rather than written down: the venue's own
    /// <c>altname</c> is the answer, with the two dead codes of <see cref="LegacyAssetCodes"/> corrected after it.
    /// </para>
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string>> AssetCodesAsync(KrakenHttp http, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        JsonElement assets = await http.GetPublicAsync(AssetsPath, null, ct).ConfigureAwait(false);
        Dictionary<string, string> codes = new(StringComparer.Ordinal);
        if (assets.ValueKind != JsonValueKind.Object)
        {
            return codes;
        }

        foreach (JsonProperty asset in assets.EnumerateObject())
        {
            string altname = asset.Value.Str("altname");
            codes[asset.Name] = Current(altname.Length > 0 ? altname : asset.Name);
        }

        return codes;
    }

    /// <summary>The instrument id of a pair, from the name that goes on the wire: <c>BTC/USD</c> becomes <c>BTC-USD</c>.</summary>
    public static InstrumentId ToInstrumentId(string wireSymbol)
    {
        ArgumentNullException.ThrowIfNull(wireSymbol);
        return new InstrumentId(new Symbol(wireSymbol.Replace(WireSeparator, SymbolSeparator)), Venue);
    }

    /// <summary>The wire name behind an instrument id, which is <see cref="ToInstrumentId"/> run backwards.</summary>
    public static string ToRawSymbol(InstrumentId id) => id.Symbol.Value.Replace(SymbolSeparator, WireSeparator);

    /// <summary>
    /// The interval, in whole minutes, that this platform calls a bar of the given length. Measured against the live
    /// endpoint: it answers 1, 5, 15, 30, 60, 240, 1440, 10080 and 21600, and refuses anything else with
    /// "EGeneral:Invalid arguments" rather than rounding to something near it. 21600 minutes is fifteen days, which
    /// no aggregation here names, so it is not offered.
    /// </summary>
    public static int Interval(BarSpecification spec) => (spec.Aggregation, spec.Step) switch
    {
        (BarAggregation.Minute, 1) => 1,
        (BarAggregation.Minute, 5) => 5,
        (BarAggregation.Minute, 15) => 15,
        (BarAggregation.Minute, 30) => 30,
        (BarAggregation.Hour, 1) => 60,
        (BarAggregation.Hour, 4) => 240,
        (BarAggregation.Day, 1) => 1440,
        (BarAggregation.Week, 1) => 10080,
        _ => throw new NotSupportedException(
            $"Kraken spot does not keep {spec} candles. It keeps 1, 5, 15 and 30 minutes, 1 and 4 hours, a day and "
            + "a week."),
    };

    /// <summary>Where the configured platform's REST API answers.</summary>
    public static string HttpBase(IKrakenSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.BaseUrlHttp ?? (s.ProductType == KrakenProductType.Futures
            ? KrakenFuturesVenue.DefaultHttpBase
            : DefaultHttpBase);
    }

    /// <summary>Where the configured platform's socket answers - the public one on spot, the only one on futures.</summary>
    public static string WsBase(IKrakenSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.BaseUrlWs ?? (s.ProductType == KrakenProductType.Futures
            ? KrakenFuturesVenue.DefaultWsBase
            : DefaultWsBase);
    }

    /// <summary>
    /// The address the spot private socket is opened on: the configured public base with its host replaced, so that
    /// a host pointing the adapter at a recording or a proxy moves both sockets at once.
    /// </summary>
    public static Uri PrivateWsAddress(IKrakenSettings s)
    {
        Uri publicBase = new(WsBase(s) + WsVersion);

        // A loopback recording is one server serving both halves, so the substitution is only applied to the venue's
        // own host. Rewriting 127.0.0.1 would point the private socket at a machine that is not there.
        if (publicBase.IsLoopback)
        {
            return publicBase;
        }

        return new UriBuilder(publicBase) { Host = PrivateWsHost }.Uri;
    }

    public static KrakenCredentials Credentials(IKrakenSettings s) => new(
        Secrets.Require(s?.ApiKey, EnvApiKey),
        Secrets.Require(s?.ApiSecret, EnvApiSecret));

    public static KrakenCredentials? OptionalCredentials(IKrakenSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        string? key = Secrets.Optional(s.ApiKey, EnvApiKey);
        string? secret = Secrets.Optional(s.ApiSecret, EnvApiSecret);
        return key is null || secret is null ? null : new KrakenCredentials(key, secret);
    }
}

/// <summary>A Kraken key is a public part and a base64 private part, on both platforms.</summary>
public sealed record KrakenCredentials(string Key, string Secret)
{
    // Never the secret in a log line or an exception text.
    public override string ToString() => "KrakenCredentials(key " + (Key.Length > 4 ? Key[..4] + "..." : "...") + ")";
}

/// <summary>
/// Kraken REST access for whichever of the two platforms a client is pointed at. One class rather than two because a
/// caller holding an instrument and a window - a history download, say - has no business knowing that this venue
/// answers out of two APIs; what differs is hidden here.
/// <para>
/// Nothing is shared between the two beyond the shape of a key. Spot answers <c>{ error: [], result: {} }</c> with a
/// non-empty error array meaning failure whatever the HTTP status says, signs a form body with SHA-256 over the nonce
/// and HMAC-SHA-512 over the path, and takes the nonce in the body. Futures answers
/// <c>{ result: "success" | "error" }</c> with the failure in EITHER a single <c>error</c> string OR an <c>errors</c>
/// array of objects - both measured on the same API - signs over the body, the nonce and the path with the version
/// segment dropped, and takes the nonce in a header.
/// </para>
/// </summary>
public sealed class KrakenHttp : IDisposable
{
    private readonly HttpClientWrapper _http;
    private readonly KrakenCredentials? _credentials;
    private long _nonce;

    public KrakenHttp(IKrakenSettings settings, ILogger? logger = null, bool requireCredentials = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _credentials = requireCredentials ? KrakenVenue.Credentials(settings) : KrakenVenue.OptionalCredentials(settings);
        ProductType = settings.ProductType;
        _http = new HttpClientWrapper(
            new Uri(KrakenVenue.HttpBase(settings)),
            new RateLimiter(
                ProductType == KrakenProductType.Futures ? KrakenFuturesVenue.RequestsPerWindow : KrakenVenue.RequestsPerWindow,
                ProductType == KrakenProductType.Futures ? KrakenFuturesVenue.RequestWindow : KrakenVenue.RequestWindow),
            new RetryPolicy(),
            logger);

        // A nonce must never repeat and must never go backwards for a key, and the venue keeps the highest one it has
        // seen. Milliseconds since the epoch with a counter behind it means two requests in the same millisecond -
        // which a paging loop produces - still ascend.
        _nonce = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    public bool HasCredentials => _credentials is not null;

    /// <summary>
    /// Which platform this client is pointed at. Carried so that a helper handed nothing but an http client answers
    /// for the right one: the two differ in the shape of a candle, not only in the path.
    /// </summary>
    public KrakenProductType ProductType { get; }

    /// <summary>A public endpoint of whichever platform is configured, with the envelope taken off.</summary>
    public async Task<JsonElement> GetPublicAsync(string path, IReadOnlyDictionary<string, string>? query = null, CancellationToken ct = default)
    {
        string text = await _http.SendAsync(HttpMethod.Get, path, query, null, null, 1, ct).ConfigureAwait(false);
        return Unwrap(text, ProductType);
    }

    /// <summary>
    /// An endpoint that answers with no envelope at all. The futures charts service is the only one: it returns
    /// <c>{ candles, more_candles }</c> on success and PLAIN TEXT with an HTTP 400 on failure - "Invalid resolution",
    /// "Invalid instrument" - so neither the success shape nor the failure shape is the one the rest of that platform
    /// uses.
    /// </summary>
    public async Task<JsonElement> GetUnwrappedAsync(string path, IReadOnlyDictionary<string, string>? query = null, CancellationToken ct = default)
    {
        string text;
        try
        {
            text = await _http.SendAsync(HttpMethod.Get, path, query, null, null, 1, ct).ConfigureAwait(false);
        }
        catch (VenueHttpException e)
        {
            throw new KrakenApiException(e.Body.Trim(), (int)e.StatusCode);
        }

        using JsonDocument doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    public Task<JsonElement> GetSignedAsync(string path, IReadOnlyDictionary<string, string>? query = null, CancellationToken ct = default) =>
        SendSignedAsync(HttpMethod.Get, path, query, ct);

    public Task<JsonElement> PostSignedAsync(string path, IReadOnlyDictionary<string, string>? form = null, CancellationToken ct = default) =>
        SendSignedAsync(HttpMethod.Post, path, form, ct);

    public Task<JsonElement> PutSignedAsync(string path, IReadOnlyDictionary<string, string>? query = null, CancellationToken ct = default) =>
        SendSignedAsync(HttpMethod.Put, path, query, ct);

    private async Task<JsonElement> SendSignedAsync(HttpMethod method, string path, IReadOnlyDictionary<string, string>? parameters, CancellationToken ct)
    {
        KrakenCredentials c = _credentials
            ?? throw new InvalidOperationException("Kraken API credentials are required for this request.");

        string nonce = NextNonce();
        string text;
        try
        {
            text = ProductType == KrakenProductType.Futures
                ? await SendFuturesSignedAsync(method, path, parameters, nonce, c, ct).ConfigureAwait(false)
                : await SendSpotSignedAsync(path, parameters, nonce, c, ct).ConfigureAwait(false);
        }
        catch (VenueHttpException e) when (TryEnvelopeError(e.Body, ProductType) is { } refusal)
        {
            throw new KrakenApiException(refusal, (int)e.StatusCode);
        }

        return Unwrap(text, ProductType);
    }

    /// <summary>
    /// Spot: everything private is a POST of a form body carrying the nonce, and the signature covers the path
    /// followed by a SHA-256 of the nonce and that same body, under HMAC-SHA-512 with the base64-decoded secret.
    /// </summary>
    private async Task<string> SendSpotSignedAsync(string path, IReadOnlyDictionary<string, string>? parameters, string nonce, KrakenCredentials c, CancellationToken ct)
    {
        Dictionary<string, string> form = new(StringComparer.Ordinal) { ["nonce"] = nonce };
        if (parameters is not null)
        {
            foreach ((string name, string value) in parameters)
            {
                form[name] = value;
            }
        }

        string body = HttpClientWrapper.BuildQuery(form);
        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["API-Key"] = c.Key,
            ["API-Sign"] = SignSpot(c.Secret, path, nonce, body),
        };

        using StringContent content = new(body, Encoding.UTF8, "application/x-www-form-urlencoded");
        return await _http.SendAsync(HttpMethod.Post, path, null, content, headers, 1, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Futures: the nonce travels in a header, the signature covers the request data followed by the nonce and the
    /// path with the <c>/derivatives</c> prefix removed, and the whole is SHA-256'd before HMAC-SHA-512.
    /// <para>
    /// UNVERIFIED. Every other fact in this adapter was measured against the live venue; this one cannot be, because
    /// the futures platform answers an unsigned or wrongly signed request with <c>authenticationError</c> and
    /// nothing else - it does not distinguish a bad signature from a bad key, and it validates neither the
    /// parameters nor the path before the signature. So this follows the venue's published scheme and has never had
    /// a correct signature put through it.
    /// </para>
    /// </summary>
    private async Task<string> SendFuturesSignedAsync(HttpMethod method, string path, IReadOnlyDictionary<string, string>? parameters, string nonce, KrakenCredentials c, CancellationToken ct)
    {
        string data = parameters is { Count: > 0 } ? HttpClientWrapper.BuildQuery(parameters) : string.Empty;
        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["APIKey"] = c.Key,
            ["Nonce"] = nonce,
            ["Authent"] = SignFutures(c.Secret, path, nonce, data),
        };

        if (method == HttpMethod.Get)
        {
            return await _http.SendAsync(HttpMethod.Get, path, parameters, null, headers, 1, ct).ConfigureAwait(false);
        }

        // A POST or a PUT carries the same data as a form body, which is what the signature was taken over.
        using StringContent content = new(data, Encoding.UTF8, "application/x-www-form-urlencoded");
        return await _http.SendAsync(method, path, null, content, headers, 1, ct).ConfigureAwait(false);
    }

    /// <summary>The spot signature, exposed so a test can check it against the venue's own worked example.</summary>
    internal static string SignSpot(string secret, string path, string nonce, string body)
    {
        byte[] hashed = SHA256.HashData(Encoding.UTF8.GetBytes(nonce + body));
        byte[] payload = [.. Encoding.UTF8.GetBytes(path), .. hashed];
        return HmacSigner.Sha512Base64(Convert.FromBase64String(secret), payload);
    }

    /// <summary>
    /// The futures signature. The path is signed WITHOUT the <c>/derivatives</c> prefix, which is the part of the
    /// scheme a reader is most likely to get wrong: the request goes to
    /// <c>/derivatives/api/v3/sendorder</c> and the signature covers <c>/api/v3/sendorder</c>.
    /// </summary>
    internal static string SignFutures(string secret, string path, string nonce, string data)
    {
        string signedPath = path.StartsWith(KrakenFuturesVenue.DerivativesPrefix, StringComparison.Ordinal)
            ? path[KrakenFuturesVenue.DerivativesPrefix.Length..]
            : path;

        byte[] hashed = SHA256.HashData(Encoding.UTF8.GetBytes(data + nonce + signedPath));
        return HmacSigner.Sha512Base64(Convert.FromBase64String(secret), hashed);
    }

    /// <summary>A nonce that never repeats and never goes backwards for this key.</summary>
    internal string NextNonce()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long next = Interlocked.Increment(ref _nonce);
        while (next < now && Interlocked.CompareExchange(ref _nonce, now, next) != next)
        {
            next = Interlocked.Increment(ref _nonce);
        }

        return Math.Max(next, now).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The payload out of a platform's envelope, or a refusal. Spot puts its failures in an array of strings and
    /// answers HTTP 200 while doing it, so a caller checking the status learns nothing; futures puts its failures in
    /// two different places depending on which layer refused.
    /// </summary>
    private static JsonElement Unwrap(string text, KrakenProductType productType)
    {
        using JsonDocument doc = JsonDocument.Parse(text);
        JsonElement root = doc.RootElement;
        if (TryError(root, productType) is { } refusal)
        {
            throw new KrakenApiException(refusal, (int)HttpStatusCode.OK);
        }

        return root.TryGetProperty("result", out JsonElement result) && productType == KrakenProductType.Spot
            ? result.Clone()
            : root.Clone();
    }

    private static string? TryEnvelopeError(string body, KrakenProductType productType)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            return TryError(doc.RootElement, productType);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryError(JsonElement root, KrakenProductType productType)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (productType == KrakenProductType.Spot)
        {
            return root.TryGetProperty("error", out JsonElement errors)
                && errors.ValueKind == JsonValueKind.Array
                && errors.GetArrayLength() > 0
                ? string.Join("; ", errors.EnumerateArray().Select(e => e.GetString() ?? string.Empty))
                : null;
        }

        // Futures. "result" is the word "error" on both failure shapes, and then the message is in one of two
        // completely different fields - a single string for a refusal by the platform, an array of objects for a
        // refusal by the request parser. Both were measured on the same API within one minute.
        if (!(root.TryGetProperty("result", out JsonElement outcome)
            && outcome.ValueKind == JsonValueKind.String
            && outcome.GetString() == "error"))
        {
            return null;
        }

        if (root.TryGetProperty("errors", out JsonElement list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0)
        {
            return string.Join("; ", list.EnumerateArray().Select(e => e.Str("message")));
        }

        return root.TryGetProperty("error", out JsonElement single) && single.ValueKind == JsonValueKind.String
            ? single.GetString()
            : "error";
    }

    public void Dispose() => _http.Dispose();
}

public sealed class KrakenApiException : Exception
{
    public KrakenApiException(string? code, int httpStatus)
        : base("Kraken error: " + (code ?? "unknown"))
    {
        Code = code ?? string.Empty;
        HttpStatus = httpStatus;
    }

    /// <summary>
    /// The venue's own text. Spot's is a stable <c>ECATEGORY:Message</c> token - "EQuery:Unknown asset pair",
    /// "EAPI:Invalid key" - which is why it can be matched on; futures' is a sentence and is only reported.
    /// </summary>
    public string Code { get; }

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
        JsonValueKind.Number => (long)p.GetDouble(),
        JsonValueKind.String => long.TryParse(p.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long l) ? l : 0,
        _ => 0,
    };

    public static bool Bool(this JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement p)
        && (p.ValueKind == JsonValueKind.True || (p.ValueKind == JsonValueKind.String && p.GetString() == "true"));

    public static bool Has(this JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement p) && p.ValueKind != JsonValueKind.Null;

    public static string Fmt(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A timestamp Kraken wrote as an ISO-8601 instant. Spot's sockets and the futures REST API both use them, to
    /// microsecond and nanosecond precision, where the same platforms use whole seconds and milliseconds elsewhere.
    /// </summary>
    public static UnixNanos Iso(this JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement p)
        && p.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(p.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset at)
            ? UnixNanos.FromDateTimeOffset(at)
            : default;

    public static UnixNanos Ms(this JsonElement e, string name) => UnixNanos.FromMilliseconds(e.Long(name));

    /// <summary>The number of decimals an increment has, which is the precision to publish it at.</summary>
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

    /// <summary>The increment a number of decimal places describes: 8 places is 0.00000001.</summary>
    public static decimal IncrementFor(int decimals)
    {
        decimal increment = 1m;
        for (int i = 0; i < decimals; i++)
        {
            increment /= 10m;
        }

        return increment;
    }
}

/// <summary>
/// Loads Kraken's spot pairs from <c>/0/public/AssetPairs</c>, which needs no key and answered with all 1451 of them
/// in one request when measured.
/// <para>
/// One thing here is this platform's own and is deliberately not visible above it: the venue publishes a
/// <c>wsname</c> that nothing on the venue accepts for two of its assets. See
/// <see cref="KrakenVenue.LegacyAssetCodes"/>; the instrument's raw symbol is the name that works.
/// </para>
/// <para>
/// The catalog is keyed by a pair id - <c>XXBTZUSD</c> - which is a third spelling and is not used for anything: it
/// is neither what a request takes nor what the socket publishes, and the venue echoes back whichever of its
/// spellings a request was made with, so the key of a one-pair answer is not a name that can be relied upon.
/// </para>
/// </summary>
public sealed class KrakenInstrumentProvider : InstrumentProviderBase
{
    private readonly KrakenHttp _http;

    public KrakenInstrumentProvider(KrakenHttp http, InstrumentProviderConfig? config = null, ILogger? logger = null)
        : base(KrakenVenue.Venue, config, logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public override async Task LoadAllAsync(CancellationToken ct, IReadOnlyDictionary<string, string>? filters = null)
    {
        JsonElement data = await _http
            .GetPublicAsync(KrakenVenue.RestVersion + "/public/AssetPairs", null, ct)
            .ConfigureAwait(false);

        int loaded = 0;
        int notTrading = 0;
        foreach (JsonProperty pair in data.EnumerateObject())
        {
            Instrument? instrument = Parse(pair.Value, ref notTrading);
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
            "Loaded {Count} Kraken spot pairs, leaving out {NotTrading} that are cancel-only or post-only",
            loaded,
            notTrading);
    }

    public override async Task LoadAsync(InstrumentId id, CancellationToken ct)
    {
        JsonElement data;
        try
        {
            data = await _http.GetPublicAsync(
                KrakenVenue.RestVersion + "/public/AssetPairs",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["pair"] = KrakenVenue.ToRawSymbol(id) },
                ct).ConfigureAwait(false);
        }
        catch (KrakenApiException e) when (KrakenVenue.ErrorsThatMeanNoSuchInstrument.Contains(e.Code))
        {
            // Not listed is an answer, not a failure - the same answer on every venue here, so a caller that looks
            // the instrument up afterwards and finds nothing needs to know nothing about Kraken's error strings.
            Log.LogInformation("Kraken does not list {Instrument}", id);
            return;
        }

        int ignored = 0;
        foreach (JsonProperty pair in data.EnumerateObject())
        {
            // Only the instrument that was asked for, whatever the venue chose to return. This endpoint honours the
            // filter today, and the futures catalog of the same venue does not, so nothing here relies on it.
            if (Parse(pair.Value, ref ignored) is { } instrument && instrument.Id == id)
            {
                Add(instrument);
            }
        }
    }

    private static Instrument? Parse(JsonElement item, ref int notTrading)
    {
        if (!item.Str("status").Equals(KrakenVenue.TradingStatus, StringComparison.Ordinal))
        {
            notTrading++;
            return null;
        }

        string wsName = item.Str("wsname");
        if (wsName.Length == 0)
        {
            return null;
        }

        string wire = KrakenVenue.ToWireSymbol(wsName);
        int slash = wire.IndexOf(KrakenVenue.WireSeparator, StringComparison.Ordinal);
        if (slash <= 0)
        {
            return null;
        }

        decimal tick = item.Dec("tick_size");
        if (tick <= 0m)
        {
            return null;
        }

        // The venue publishes the size step as a number of decimal places rather than as an increment, and its
        // socket's instrument channel publishes the increment - 1e-8 against lot_decimals 8 on BTC/USD, which is how
        // this reading was confirmed rather than assumed.
        byte pricePrecision = (byte)item.Long("pair_decimals");
        byte sizePrecision = (byte)item.Long("lot_decimals");
        decimal step = Json.IncrementFor(sizePrecision);
        Currency baseCurrency = Currency.FromCode(wire[..slash], 8);
        Currency quote = Currency.FromCode(wire[(slash + 1)..], 8);
        decimal minQty = item.Dec("ordermin");
        decimal minCost = item.Dec("costmin");
        UnixNanos now = UnixNanos.FromDateTimeOffset(DateTimeOffset.UtcNow);

        return new CurrencyPair(new InstrumentSpec
        {
            Id = KrakenVenue.ToInstrumentId(wire),
            RawSymbol = new Symbol(wire),
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
            MinNotional = minCost > 0m ? new Money(minCost, quote) : null,

            // The venue's own per-pair fee arrays are empty on every pair it lists, so there is nothing to read and
            // the published schedule stands in. See KrakenVenue.DefaultMakerFee.
            MakerFee = KrakenVenue.DefaultMakerFee,
            TakerFee = KrakenVenue.DefaultTakerFee,
            TsEvent = now,
            TsInit = now,
        });
    }
}
