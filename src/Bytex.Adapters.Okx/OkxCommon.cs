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

namespace Bytex.Adapters.Okx;

/// <summary>
/// Which of OKX's three markets a client talks to. Unlike every other venue this engine speaks to, these are not
/// three APIs on three hosts: they are one API, on one host, selected by an <c>instType</c> parameter on almost every
/// request. What differs between them is what an order means - a size in base currency on spot, a number of contracts
/// on the two derivative markets - and which of them is charged funding.
/// </summary>
public enum OkxInstrumentType
{
    /// <summary>Cash spot pairs, <c>BTC-USDT</c>. The default, so a configuration that says nothing means spot.</summary>
    Spot,

    /// <summary>Perpetual contracts, <c>BTC-USDT-SWAP</c>. Charged funding every eight hours.</summary>
    Swap,

    /// <summary>
    /// Dated contracts that deliver, <c>BTC-USD_UM-261030</c>. Not <c>BTC-USDT-250926</c>: the venue lists no
    /// USDT-margined dated futures at all, which was checked against all 244 of them on 2026-09-25.
    /// </summary>
    Futures,
}

/// <summary>
/// Which margin the two derivative markets post against a position. The venue demands this on every order - there is
/// no default it will fall back on - and it is not a per-order decision a strategy makes, so it is configured once
/// per client. Spot ignores it: a cash trade posts no margin and the venue's own word for that is a third mode.
/// </summary>
public enum OkxMarginMode
{
    /// <summary>Margin shared across the account's positions, which is the venue's own default for a new account.</summary>
    Cross,

    /// <summary>Margin ring-fenced per position, so one liquidation cannot reach another position.</summary>
    Isolated,
}

public interface IOkxSettings
{
    string? ApiKey { get; }

    string? ApiSecret { get; }

    /// <summary>The passphrase chosen when the key was made. A third part the other venues' keys do not have.</summary>
    string? ApiPassphrase { get; }

    /// <summary>Which of the venue's three markets this client talks to.</summary>
    OkxInstrumentType InstrumentType { get; }

    /// <summary>
    /// Whether to trade the venue's demo account rather than the real one. It is not a different host for REST: the
    /// same address answers, and a header says which account the request is for - which was measured, on a public
    /// endpoint that answered identically with the header set.
    /// </summary>
    bool DemoTrading { get; }

    string? BaseUrlHttp { get; }

    string? BaseUrlWs { get; }
}

public sealed record OkxDataClientConfig : DataClientConfig, IOkxSettings
{
    public string? ApiKey { get; init; }

    public string? ApiSecret { get; init; }

    public string? ApiPassphrase { get; init; }

    public OkxInstrumentType InstrumentType { get; init; } = OkxInstrumentType.Spot;

    public bool DemoTrading { get; init; }

    public string? BaseUrlHttp { get; init; }

    public string? BaseUrlWs { get; init; }
}

public sealed record OkxExecutionClientConfig : ExecutionClientConfig, IOkxSettings
{
    public string? ApiKey { get; init; }

    public string? ApiSecret { get; init; }

    public string? ApiPassphrase { get; init; }

    public OkxInstrumentType InstrumentType { get; init; } = OkxInstrumentType.Spot;

    public bool DemoTrading { get; init; }

    /// <summary>
    /// Which margin the derivative markets post. Cross by default, matching the account default a new OKX account is
    /// created with, so a configuration that says nothing trades the way the venue's own interface would.
    /// </summary>
    public OkxMarginMode MarginMode { get; init; } = OkxMarginMode.Cross;

    public string? BaseUrlHttp { get; init; }

    public string? BaseUrlWs { get; init; }
}

/// <summary>
/// What OKX is, as measured against the live venue on 2026-09-25 rather than read off its documentation. Its public
/// endpoints need no key at all - instruments, candles, funding settlements and position tiers are all open - so
/// every number here was asked for and counted rather than copied, and three of them disagree with what the venue
/// publishes about itself.
/// </summary>
public static class OkxVenue
{
    public static readonly Venue Venue = new("OKX");

    /// <summary>
    /// The three parts of an OKX key. There is no fourth variable and there must not be: the declaration is compared
    /// with these fields, so a key part the adapter reads and does not declare is a key a host cannot assemble.
    /// </summary>
    public const string EnvApiKey = "OKX_API_KEY";

    public const string EnvApiSecret = "OKX_API_SECRET";

    /// <summary>
    /// The passphrase is chosen when the key is made and cannot be recovered afterwards, so a host that never offers
    /// the field leaves a user with two thirds of a key and no way to say so.
    /// </summary>
    public const string EnvApiPassphrase = "OKX_API_PASSPHRASE";

    /// <summary>The code every successful answer carries. Anything else is a refusal, whatever the HTTP status says.</summary>
    public const string Ok = "0";

    /// <summary>
    /// What the venue says about an instrument it does not list: measured identically for all three markets, and
    /// with HTTP 200, so a caller reading the status learns nothing.
    /// <para>
    /// A single code rather than one per family, which is worth saying out loud because it is not how the other
    /// multi-family venues behave: Binance's two families answer this question two different ways and Bybit's two
    /// answer it two more. OKX honours the <c>instType</c> filter as well - asked for the spot pair BTC-USDT under
    /// <c>instType=SWAP</c> it refuses with this code rather than returning the pair or the whole catalog.
    /// </para>
    /// </summary>
    public const string ErrorNoSuchInstrument = "51001";

    /// <summary>
    /// A parameter the venue will not accept - a bar length it does not keep, an <c>instType</c> that is not one of
    /// its own. Measured with HTTP 400, unlike <see cref="ErrorNoSuchInstrument"/>, and never treated as "not
    /// listed": a request that was malformed has to reach the caller as a failure rather than as an empty answer.
    /// </summary>
    public const string ErrorParameter = "51000";

    /// <summary>
    /// Where the API answers. One host for all three markets, and the same host for the demo account - what selects
    /// the demo account is <see cref="SimulatedTradingHeader"/> and not an address.
    /// </summary>
    public const string DefaultHttpBase = "https://www.okx.com";

    /// <summary>
    /// The socket root. A root and not an endpoint: three paths hang off it and which one a subscription belongs to
    /// is this adapter's business, so declaring a full path would be a base that cannot be written back.
    /// </summary>
    public const string DefaultWsBase = "wss://ws.okx.com:8443";

    /// <summary>Tickers, trades and books.</summary>
    public const string WsPublicPath = "/ws/v5/public";

    /// <summary>Orders, fills, balances and positions, after a login message.</summary>
    public const string WsPrivatePath = "/ws/v5/private";

    /// <summary>
    /// Candles, and candles only. Measured: subscribing to <c>candle1m</c> on the public socket is refused with code
    /// 60018 "wrong URL or channel", and the same subscription on this path delivers. A client that opened one socket
    /// for everything would have quotes and trades flowing and bars silently absent.
    /// </summary>
    public const string WsBusinessPath = "/ws/v5/business";

    /// <summary>
    /// The header that sends a request to the demo account instead of the real one. Measured on a public endpoint,
    /// which answered identically with it set; what it does to a private request cannot be measured without a key.
    /// <para>
    /// The socket is not the same story. The demo account has its own socket host - wspap.okx.com on port 8443, with
    /// the same three paths, which was reached and subscribed to - and a family declares one socket base, so there is
    /// nowhere in the declaration to put a second one. A host trading the demo account sets <c>baseUrlWs</c> to it.
    /// </para>
    /// </summary>
    public const string SimulatedTradingHeader = "x-simulated-trading";

    /// <summary>What the header carries when the request is for the demo account.</summary>
    public const string SimulatedTradingOn = "1";

    /// <summary>The four headers a signed request carries.</summary>
    public const string HeaderApiKey = "OK-ACCESS-KEY";

    public const string HeaderSign = "OK-ACCESS-SIGN";

    public const string HeaderTimestamp = "OK-ACCESS-TIMESTAMP";

    public const string HeaderPassphrase = "OK-ACCESS-PASSPHRASE";

    /// <summary>
    /// The timestamp format the signature is taken over: ISO 8601 in UTC, to milliseconds. Not the Unix milliseconds
    /// every other venue here signs, which is why it has a constant of its own rather than a call to a shared helper.
    /// </summary>
    public const string TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";

    /// <summary>
    /// The most candle rows one request answers with. The venue documents 300 for one candle endpoint and 100 for
    /// the other; asked for 500 on each, both returned exactly 300 with a success code and no complaint. A paging
    /// loop written to the documented 100 would make three times as many requests as it needs, and one written to
    /// believe a 300-row answer was short would stop at the first page.
    /// </summary>
    public const int CandlePage = 300;

    /// <summary>
    /// The most recent trades one request answers with. Asked for 1000 the venue returned 500, which is what it
    /// documents - the one page size here that its own documentation had right.
    /// </summary>
    public const int TradePage = 500;

    /// <summary>
    /// How many funding settlements one request answers with. The venue documents 100 as the maximum and served
    /// exactly 200 when asked for 200, so the documented cap understates it. Asked for 300 it answered 286, which is
    /// the end of what it keeps rather than a cap - roughly three months of eight-hourly settlements - so 200 is the
    /// largest page size that was seen to be honoured in full.
    /// </summary>
    public const int FundingPage = 200;

    /// <summary>
    /// How many instrument families one position-tier request may name. Measured: five are answered and six are
    /// refused outright with code 50025, "Parameter instFamily count exceeds the limit 5".
    /// </summary>
    public const int TierFamilyPage = 5;

    /// <summary>
    /// The tier a position starts in, and therefore the one whose margin an instrument publishes. The venue's tiers
    /// grow the requirement as a position grows - 99 of them on BTC-USDT's perpetual - and an instrument carries one
    /// number, so it carries the one that applies to a position being opened.
    /// </summary>
    public const string FirstTier = "1";

    /// <summary>
    /// Requests the venue allows in <see cref="RequestWindow"/> on the public endpoints this adapter calls. Measured
    /// rather than read: twenty requests fired at once were all answered and the twenty-first onwards came back 429.
    /// </summary>
    public const int RequestsPerWindow = 20;

    /// <summary>The window <see cref="RequestsPerWindow"/> is counted over.</summary>
    public static readonly TimeSpan RequestWindow = TimeSpan.FromSeconds(2);

    /// <summary>
    /// What the venue's socket expects as a keep-alive, and what it answers with. Plain text, not JSON, and measured:
    /// the string <c>ping</c> came back as the string <c>pong</c>. A JSON ping would be an unreadable message to the
    /// venue and a socket that goes quiet.
    /// </summary>
    public const string PingMessage = "ping";

    /// <summary>The answer to <see cref="PingMessage"/>, which is not JSON and must not be parsed as any.</summary>
    public const string PongMessage = "pong";

    /// <summary>How often to ping; the venue closes a socket that has been silent for thirty seconds.</summary>
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The longest client order id the venue accepts. It also accepts only letters and digits in one, which is the
    /// constraint that matters here: the engine's own order ids carry hyphens by default, so they are refused before
    /// they are sent rather than being quietly rewritten - an id the venue was given under a different spelling is an
    /// order reconciliation can no longer find.
    /// </summary>
    public const int MaxClientOrderIdLength = 32;

    /// <summary>Where a leverage is set: account state per instrument, not a field on an order.</summary>
    public const string SetLeveragePath = "/api/v5/account/set-leverage";

    /// <summary>Where an order already at the venue is changed.</summary>
    public const string AmendOrderPath = "/api/v5/trade/amend-order";

    /// <summary>
    /// The trade mode a cash spot order carries. The venue demands a mode on every order and this is the one that
    /// means "no margin", which is why spot has nothing to configure where the derivative markets have two choices.
    /// </summary>
    public const string CashTradeMode = "cash";

    /// <summary>
    /// What a spot order's size is counted in. The venue's own default for a spot market order is the QUOTE currency,
    /// so a market buy of 0.01 would buy 0.01 USDT of bitcoin rather than 0.01 bitcoin. Everything above an adapter
    /// counts in base currency, so this is stated on every spot order rather than left to the default.
    /// </summary>
    public const string SizeInBaseCurrency = "base_ccy";

    /// <summary>The same field when a caller really did mean a quantity of the quote currency.</summary>
    public const string SizeInQuoteCurrency = "quote_ccy";

    /// <summary>
    /// What one contract of a derivative is worth, kept on the instrument so an order can be denominated in it. The
    /// venue publishes it as <c>ctVal</c> of <c>ctValCcy</c> - 0.01 BTC on BTC-USDT-SWAP - and a size crossing into
    /// the venue is a number of contracts rather than a quantity of the base currency.
    /// </summary>
    public const string ContractValueInfo = "contractValue";

    /// <summary>
    /// The greatest leverage the venue allows on an instrument, as it publishes it on the instrument itself. Kept so
    /// that a host can say why a configured leverage was refused without asking the venue a second question.
    /// </summary>
    public const string MaxLeverageInfo = "maxLeverage";

    /// <summary>
    /// The index a derivative is priced against, as the venue publishes it in <c>uly</c> - "BTC-USDT" for both
    /// BTC-USDT-SWAP and BTC-USD_UM-261030. Kept because the index price channel is subscribed to by the INDEX's name
    /// rather than the contract's, and taking that name apart from the contract id would be reading a spelling.
    /// </summary>
    public const string UnderlyingInfo = "underlying";

    /// <summary>The venue's own <c>instType</c>, which is what selects a market on almost every request.</summary>
    public static string InstType(OkxInstrumentType type) => type switch
    {
        OkxInstrumentType.Spot => "SPOT",
        OkxInstrumentType.Swap => "SWAP",
        OkxInstrumentType.Futures => "FUTURES",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "not one of this venue's markets"),
    };

    /// <summary>The venue's word for a margin mode, which every derivative order and leverage change carries.</summary>
    public static string MarginMode(OkxMarginMode mode) => mode switch
    {
        OkxMarginMode.Cross => "cross",
        OkxMarginMode.Isolated => "isolated",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "not one of this venue's margin modes"),
    };

    public static string HttpBase(IOkxSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.BaseUrlHttp ?? DefaultHttpBase;
    }

    public static string WsBase(IOkxSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.BaseUrlWs ?? DefaultWsBase;
    }

    public static Uri WsPublic(IOkxSettings s) => new(WsBase(s) + WsPublicPath);

    public static Uri WsPrivate(IOkxSettings s) => new(WsBase(s) + WsPrivatePath);

    public static Uri WsBusiness(IOkxSettings s) => new(WsBase(s) + WsBusinessPath);

    /// <summary>
    /// The instrument id of an OKX instrument, which is the venue's own <c>instId</c> unchanged.
    /// <para>
    /// No suffix is added and none is stripped, which is deliberate and is the opposite of what this engine's other
    /// venues do. Bybit's linear BTCUSDT and its spot BTCUSDT are the same string, so one of them has to be renamed
    /// or a host cannot tell a perpetual from a pair; OKX already distinguishes all three of its markets in the id
    /// itself - BTC-USDT, BTC-USDT-SWAP, BTC-USD_UM-261030 - and there is no collision to resolve. Rewriting a name
    /// the venue chose would only be a second spelling to translate in both directions.
    /// </para>
    /// <para>
    /// Nothing here reads a class out of the spelling. Which market an instrument belongs to comes from the
    /// <c>instType</c> the venue itself puts on the record, and this method is a name and nothing more.
    /// </para>
    /// </summary>
    public static InstrumentId ToInstrumentId(string instId) => new(new Symbol(instId), Venue);

    /// <summary>The venue's <c>instId</c> behind an instrument id, which is <see cref="ToInstrumentId"/> run backwards.</summary>
    public static string ToRawSymbol(InstrumentId id) => id.Symbol.Value;

    /// <summary>
    /// What the venue calls a bar of the given length. Measured against the live candle endpoint one length at a
    /// time: everything below is answered, and <c>8H</c> - which KuCoin and Binance both keep - is refused with
    /// "Parameter bar error". A venue that refuses a length loudly is the good case; the bad one would be a venue
    /// answering something close to what was asked for.
    /// </summary>
    public static string Interval(BarSpecification spec) => (spec.Aggregation, spec.Step) switch
    {
        (BarAggregation.Second, 1) => "1s",
        (BarAggregation.Minute, 1) => "1m",
        (BarAggregation.Minute, 3) => "3m",
        (BarAggregation.Minute, 5) => "5m",
        (BarAggregation.Minute, 15) => "15m",
        (BarAggregation.Minute, 30) => "30m",
        (BarAggregation.Hour, 1) => "1H",
        (BarAggregation.Hour, 2) => "2H",
        (BarAggregation.Hour, 4) => "4H",
        (BarAggregation.Hour, 6) => "6H",
        (BarAggregation.Hour, 12) => "12H",
        (BarAggregation.Day, 1) => "1D",
        (BarAggregation.Week, 1) => "1W",
        (BarAggregation.Month, 1) => "1M",
        _ => throw new NotSupportedException(
            $"OKX does not keep {spec} candles. It keeps a second, 1, 3, 5, 15 and 30 minutes, 1, 2, 4, 6 and 12 "
            + "hours, a day, a week and a month - and not 8 hours, which it refuses outright."),
    };

    /// <summary>
    /// What one contract of this instrument is worth in its base currency - 0.01 BTC on BTC-USDT-SWAP, 0.1 ETH on
    /// ETH-USDT-SWAP - as the instrument provider recorded it from the venue. Every size crossing into or out of a
    /// derivative market goes through it.
    /// </summary>
    public static decimal ContractValue(Instrument instrument)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        if (instrument.Info is { } info
            && info.TryGetValue(ContractValueInfo, out string? text)
            && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal value)
            && value > 0m)
        {
            return value;
        }

        throw new InvalidOperationException(
            $"{instrument.Id} carries no OKX contract value, so nothing can say how many contracts a quantity of it "
            + "is. It was not loaded by this venue's instrument provider for a derivative market.");
    }

    /// <summary>
    /// A quantity in base currency as the number of contracts the venue takes. Not rounded to a whole one, and that
    /// is the venue's own doing: BTC-USDT-SWAP has a lot size of 0.01 contracts, so a hundredth of a contract is
    /// tradable there. The engine has already rounded the quantity to the instrument's size increment, which is the
    /// lot size expressed in base currency, so this division lands on a size the venue accepts.
    /// </summary>
    public static decimal ToContracts(Instrument instrument, Quantity quantity)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        return quantity.Value / ContractValue(instrument);
    }

    /// <summary>A number of contracts as a quantity in base currency, which is how everything above the adapter reads it.</summary>
    public static Quantity ToQuantity(Instrument instrument, decimal contracts)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        return instrument.MakeQuantity(contracts * ContractValue(instrument));
    }

    /// <summary>
    /// A size as the venue should be told it, in the unit that market counts in: base currency on spot, contracts on
    /// the two derivative markets. One method so that no caller has to remember which market it is talking to.
    /// </summary>
    public static decimal ToVenueSize(Instrument instrument, Quantity quantity)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        return instrument.InstrumentClass == InstrumentClass.Spot ? quantity.Value : ToContracts(instrument, quantity);
    }

    /// <summary>A size the venue reported, as a quantity in base currency. <see cref="ToVenueSize"/> run backwards.</summary>
    public static Quantity FromVenueSize(Instrument instrument, decimal size)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        return instrument.InstrumentClass == InstrumentClass.Spot
            ? instrument.MakeQuantity(size)
            : ToQuantity(instrument, size);
    }

    public static OkxCredentials Credentials(IOkxSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new OkxCredentials(
            Secrets.Require(s.ApiKey, EnvApiKey),
            Secrets.Require(s.ApiSecret, EnvApiSecret),
            Secrets.Require(s.ApiPassphrase, EnvApiPassphrase));
    }

    public static OkxCredentials? OptionalCredentials(IOkxSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        string? key = Secrets.Optional(s.ApiKey, EnvApiKey);
        string? secret = Secrets.Optional(s.ApiSecret, EnvApiSecret);
        string? passphrase = Secrets.Optional(s.ApiPassphrase, EnvApiPassphrase);
        return key is null || secret is null || passphrase is null ? null : new OkxCredentials(key, secret, passphrase);
    }

    /// <summary>
    /// Whether the venue would accept this as a client order id: letters and digits only, and no longer than
    /// <see cref="MaxClientOrderIdLength"/>. The engine's own ids carry hyphens unless a strategy is configured
    /// otherwise, so this is the check that decides whether an order is refused here or at the venue.
    /// </summary>
    public static bool IsAcceptableClientOrderId(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0 || value.Length > MaxClientOrderIdLength)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// An OKX key has three parts. The passphrase is chosen when the key is made and cannot be recovered, so a host that
/// holds two thirds of one holds nothing.
/// </summary>
public sealed record OkxCredentials(string Key, string Secret, string Passphrase)
{
    // Never the secret or the passphrase in a log line or an exception text.
    public override string ToString() => "OkxCredentials(three parts)";
}

/// <summary>
/// OKX REST access with request signing. Every answer is an envelope <c>{ code, msg, data }</c>; a code other than
/// <c>0</c> is a refusal whatever the HTTP status says - measured on both sides of that, since an unlisted instrument
/// is refused with HTTP 200 and an unknown <c>instType</c> with HTTP 400.
/// </summary>
public sealed class OkxHttp : IDisposable
{
    private readonly HttpClientWrapper _http;
    private readonly OkxCredentials? _credentials;
    private readonly bool _demo;

    public OkxHttp(IOkxSettings settings, ILogger? logger = null, bool requireCredentials = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _credentials = requireCredentials ? OkxVenue.Credentials(settings) : OkxVenue.OptionalCredentials(settings);
        _demo = settings.DemoTrading;
        _http = new HttpClientWrapper(
            new Uri(OkxVenue.HttpBase(settings)),
            new RateLimiter(OkxVenue.RequestsPerWindow, OkxVenue.RequestWindow),
            new RetryPolicy(),
            logger);

        InstrumentType = settings.InstrumentType;
    }

    public bool HasCredentials => _credentials is not null;

    /// <summary>
    /// Which of the venue's three markets this client is pointed at. Carried here so that a helper handed nothing but
    /// an http client answers for the right one: the endpoints are shared and the <c>instType</c> is not.
    /// </summary>
    public OkxInstrumentType InstrumentType { get; }

    /// <summary>The venue's own name for this client's market, which almost every request needs.</summary>
    public string InstType => OkxVenue.InstType(InstrumentType);

    public Task<JsonElement> GetPublicAsync(string path, IReadOnlyDictionary<string, string>? query = null, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, path, query, null, signed: false, ct);

    public Task<JsonElement> GetSignedAsync(string path, IReadOnlyDictionary<string, string>? query = null, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, path, query, null, signed: true, ct);

    public Task<JsonElement> PostSignedAsync(string path, object? body, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, path, null, body is null ? string.Empty : JsonSerializer.Serialize(body), signed: true, ct);

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, IReadOnlyDictionary<string, string>? query, string? json, bool signed, CancellationToken ct)
    {
        // The signature covers the path with its query exactly as it is sent, so the query is folded into the path
        // here rather than handed to the wrapper separately.
        string endpoint = query is { Count: > 0 } ? path + "?" + HttpClientWrapper.BuildQuery(query) : path;
        Dictionary<string, string> headers = signed ? Sign(method.Method, endpoint, json ?? string.Empty) : new Dictionary<string, string>(StringComparer.Ordinal);
        if (_demo)
        {
            // The demo account is the same address with this header on it, so it is set for public requests too:
            // a node reading the demo account's candles and trading the real one would be a node whose data and
            // whose fills came from two different places.
            headers[OkxVenue.SimulatedTradingHeader] = OkxVenue.SimulatedTradingOn;
        }

        StringContent? content = string.IsNullOrEmpty(json) ? null : new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        string text;
        try
        {
            text = await _http.SendAsync(method, endpoint, null, content, headers.Count > 0 ? headers : null, 1, ct).ConfigureAwait(false);
        }
        catch (VenueHttpException e) when (TryError(e.Body) is { } error)
        {
            throw new OkxApiException(error.Code, error.Message, (int)e.StatusCode);
        }

        return Unwrap(text);
    }

    private Dictionary<string, string> Sign(string method, string endpoint, string body)
    {
        OkxCredentials c = _credentials ?? throw new InvalidOperationException("OKX API credentials are required for this request.");

        // ISO 8601 to the millisecond, and the same string is both signed and sent. Every other venue here signs Unix
        // milliseconds, so a shared timestamp helper would have produced a signature this venue rejects.
        string timestamp = DateTimeOffset.UtcNow.ToString(OkxVenue.TimestampFormat, CultureInfo.InvariantCulture);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [OkxVenue.HeaderApiKey] = c.Key,
            [OkxVenue.HeaderSign] = HmacSigner.Sha256Base64(c.Secret, timestamp + method + endpoint + body),
            [OkxVenue.HeaderTimestamp] = timestamp,

            // In clear, unlike KuCoin's, which signs its passphrase with the secret. The venue checks it against what
            // was chosen when the key was made.
            [OkxVenue.HeaderPassphrase] = c.Passphrase,
        };
    }

    /// <summary>
    /// The <c>data</c> of an answer, or a refusal. The envelope's code is the truth here: an unlisted instrument comes
    /// back as HTTP 200 with a non-zero code and an empty array, so a caller that trusted the status would read it as
    /// a successful answer meaning "there is nothing".
    /// </summary>
    private static JsonElement Unwrap(string text)
    {
        using JsonDocument doc = JsonDocument.Parse(text);
        JsonElement root = doc.RootElement;
        string code = root.Str("code");
        if (code.Length > 0 && code != OkxVenue.Ok)
        {
            throw new OkxApiException(code, root.Str("msg"), (int)HttpStatusCode.OK);
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

public sealed class OkxApiException : Exception
{
    public OkxApiException(string code, string message, int httpStatus)
        : base($"OKX error {code}: {message}")
    {
        Code = code;
        Msg = message;
        HttpStatus = httpStatus;
    }

    public string Code { get; }

    public string Msg { get; }

    public int HttpStatus { get; }
}

/// <summary>
/// The shape of an OKX socket conversation: a subscription names channels and instruments in an <c>args</c> array,
/// and the venue answers each one with an event of its own. Kept in one place because the data client and the
/// execution client both speak it and a second copy would be a second spelling of the same message.
/// </summary>
internal static class OkxStream
{
    public const string OpSubscribe = "subscribe";

    public const string OpUnsubscribe = "unsubscribe";

    public const string OpLogin = "login";

    /// <summary>What the venue calls an event that is a refusal rather than a confirmation.</summary>
    public const string EventError = "error";

    /// <summary>A subscribe or unsubscribe message for one channel of one instrument.</summary>
    public static string Subscribe(string channel, string instId, bool subscribe = true) =>
        JsonSerializer.Serialize(new
        {
            op = subscribe ? OpSubscribe : OpUnsubscribe,
            args = new[] { new { channel, instId } },
        });

    /// <summary>
    /// A subscription to an account-wide private channel, which is selected by MARKET rather than by instrument: the
    /// orders and positions channels take an <c>instType</c> and report everything in that market, and the balance
    /// channel takes neither.
    /// </summary>
    public static string SubscribePrivate(string channel, string? instType) =>
        JsonSerializer.Serialize(new
        {
            op = OpSubscribe,
            args = instType is null
                ? [new Dictionary<string, string>(StringComparer.Ordinal) { ["channel"] = channel }]
                : new[]
                {
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["channel"] = channel,
                        ["instType"] = instType,
                    },
                },
        });

    /// <summary>
    /// The login message the private socket needs. The signature is over Unix SECONDS here, not the ISO timestamp a
    /// REST request signs - one venue, two timestamp formats, and a socket that answers "Invalid apiKey" either way.
    /// </summary>
    public static string Login(OkxCredentials credentials, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        string timestamp = at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return JsonSerializer.Serialize(new
        {
            op = OpLogin,
            args = new[]
            {
                new
                {
                    apiKey = credentials.Key,
                    passphrase = credentials.Passphrase,
                    timestamp,
                    sign = HmacSigner.Sha256Base64(credentials.Secret, timestamp + "GET" + "/users/self/verify"),
                },
            },
        });
    }
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

    public static bool Has(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement p) && p.ValueKind != JsonValueKind.Null;

    /// <summary>
    /// Whether a field is there AND carries something. This venue writes an empty string where a field does not apply
    /// rather than leaving it out - a spot pair has <c>"ctVal":""</c>, <c>"settleCcy":""</c> and <c>"expTime":""</c> -
    /// so a presence check alone answers yes to every field of every record.
    /// </summary>
    public static bool Filled(this JsonElement e, string name) => e.Str(name).Length > 0;

    public static UnixNanos Ms(this JsonElement e, string name) => UnixNanos.FromMilliseconds(e.Long(name));

    /// <summary>
    /// A number as this venue should be told it: the shortest exact form, with no trailing zeroes.
    /// <para>
    /// A quantity carries a precision, so dividing three bitcoin by a contract value of 0.01 produces the decimal
    /// 300.00 and printing it plainly sends "300.00" where the venue's own lot size is a whole contract. The venue
    /// does not need the padding and there is nothing to be gained by sending more digits than the number has, so
    /// they are trimmed - which also keeps a leverage of 3 from being sent as "3.0".
    /// </para>
    /// </summary>
    public static string Fmt(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);

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
