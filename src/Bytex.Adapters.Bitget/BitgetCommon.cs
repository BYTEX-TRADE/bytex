using System.Globalization;
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

namespace Bytex.Adapters.Bitget;

/// <summary>
/// Which of Bitget's markets a client talks to. The venue answers for all of them on one host and one socket, and the
/// market is named in every request instead: spot endpoints live under their own path, and the derivative endpoints
/// take a <c>productType</c> that says which collateral the contracts settle in.
/// <para>
/// They are separate families rather than one because almost nothing is shared beyond the address. The symbols are
/// spelled differently - a USDT perpetual is <c>BTCUSDT</c> and a USDC one is <c>BTCPERP</c> - the fees differ by a
/// factor of five, only the derivatives are charged funding, and the candle endpoints even want the bar length spelled
/// differently on spot than on the derivatives.
/// </para>
/// </summary>
public enum BitgetProductType
{
    /// <summary>Spot pairs. The default, so a configuration that says nothing means spot.</summary>
    Spot,

    /// <summary>USDT-margined perpetuals, the venue's <c>USDT-FUTURES</c> product.</summary>
    UsdtFutures,

    /// <summary>USDC-margined perpetuals, the venue's <c>USDC-FUTURES</c> product.</summary>
    UsdcFutures,
}

/// <summary>
/// Whether a client trades the real venue or its demo one.
/// <para>
/// Measured on 2026-09-25 rather than taken from the documentation, because the mechanism that is widely written down
/// is not the one the venue uses. The demo markets answer on exactly the same REST host and the same public socket as
/// the live ones; what selects them is an <c>S</c> in front of the product type - <c>SUSDT-FUTURES</c> -
/// and correspondingly S-prefixed symbols (<c>SBTCSUSDT</c>). The separate <c>wspap</c> socket host that several
/// sources name does accept a connection and then refuses every demo subscription on it with
/// "instType:SUSDT-FUTURES,channel:ticker,instId:SBTCSUSDT,precision:null doesn't exist".
/// </para>
/// <para>
/// There is no demo spot market: the venue lists no S-prefixed spot pair, so a demo spot client has nothing to trade.
/// </para>
/// </summary>
public enum BitgetTradingMode
{
    /// <summary>The real venue. The default, so nothing changes for a configuration written without this field.</summary>
    Live,

    /// <summary>The venue's demo markets, reached by the S-prefixed product types on the same hosts.</summary>
    Demo,
}

/// <summary>What a Bitget client needs to be told, shared by the data and execution configurations.</summary>
public interface IBitgetSettings
{
    string? ApiKey { get; }

    string? ApiSecret { get; }

    /// <summary>The passphrase chosen when the key was made. A Bitget key is three parts, not two.</summary>
    string? ApiPassphrase { get; }

    BitgetProductType ProductType { get; }

    /// <summary>Live or demo. Demo exists for the derivative families only.</summary>
    BitgetTradingMode TradingMode { get; }

    string? BaseUrlHttp { get; }

    string? BaseUrlWs { get; }
}

public sealed record BitgetDataClientConfig : DataClientConfig, IBitgetSettings
{
    public string? ApiKey { get; init; }

    public string? ApiSecret { get; init; }

    public string? ApiPassphrase { get; init; }

    public BitgetProductType ProductType { get; init; } = BitgetProductType.Spot;

    public BitgetTradingMode TradingMode { get; init; } = BitgetTradingMode.Live;

    public string? BaseUrlHttp { get; init; }

    public string? BaseUrlWs { get; init; }
}

public sealed record BitgetExecutionClientConfig : ExecutionClientConfig, IBitgetSettings
{
    public string? ApiKey { get; init; }

    public string? ApiSecret { get; init; }

    public string? ApiPassphrase { get; init; }

    public BitgetProductType ProductType { get; init; } = BitgetProductType.Spot;

    public BitgetTradingMode TradingMode { get; init; } = BitgetTradingMode.Live;

    /// <summary>
    /// How a derivative position is margined. The venue demands it on every derivative order rather than taking it
    /// from the account, so it has to be configured; crossed is the default because that is the mode in which the one
    /// leverage this configuration carries means exactly one thing. Spot ignores it.
    /// </summary>
    public BitgetMarginMode MarginMode { get; init; } = BitgetMarginMode.Crossed;

    public string? BaseUrlHttp { get; init; }

    public string? BaseUrlWs { get; init; }
}

/// <summary>
/// What Bitget is, measured against the live venue on 2026-09-25 rather than read off its documentation. Every public
/// endpoint this adapter reads needs no key, so all of it could be and was checked; the places where the answer can
/// only come from the documentation are marked as such where they appear.
/// </summary>
public static class BitgetVenue
{
    public static readonly Venue Venue = new("BITGET");

    public const string EnvApiKey = "BITGET_API_KEY";
    public const string EnvApiSecret = "BITGET_API_SECRET";
    public const string EnvApiPassphrase = "BITGET_API_PASSPHRASE";

    /// <summary>The code the venue answers every successful request with, whatever the HTTP status says.</summary>
    public const string Ok = "00000";

    /// <summary>
    /// What the venue says about a symbol it does not list: <c>40034</c> "Parameter NOTACOINUSDT does not exist" for a
    /// name it has never had, <c>40309</c> "The symbol has been removed" for one it has retired. Both were measured on
    /// every endpoint this adapter asks about one instrument - the spot catalog, the contract catalog, the position
    /// tiers, the candles and the funding history - and both are refusals of a well-formed question, so asking about an
    /// unlisted instrument leaves a provider empty rather than throwing.
    /// </summary>
    public static readonly IReadOnlySet<string> ErrorsThatMeanNoSuchInstrument =
        new HashSet<string>(StringComparer.Ordinal) { "40034", "40309" };

    /// <summary>
    /// What the venue says when a request carries no key at all, as opposed to one it does not recognise
    /// (<c>40037</c>). Read by the key check so that "you sent nothing" is not reported as "your key is wrong".
    /// </summary>
    public const string ErrorNoApiKey = "40006";

    /// <summary>The refusal of a key the venue has never issued, which is what a mistyped key produces.</summary>
    public const string ErrorApiKeyUnknown = "40037";

    // One host and one socket root for every market. What changes is the path (spot or mix) and the product type
    // carried in the request, which is this adapter's business - so these two are the whole of what the plugin
    // declares, and writing either of them back into a configuration changes nothing.
    public const string DefaultHttpBase = "https://api.bitget.com";
    public const string DefaultWsBase = "wss://ws.bitget.com";

    /// <summary>The public stream, which needs no key and carries every market behind one connection.</summary>
    public const string WsPublicPath = "/v2/ws/public";

    /// <summary>
    /// The private stream. Measured: it accepts a connection without credentials and then refuses every subscription
    /// with code 30004 "User not logged in/User must be logged in", so a login is sent before anything else.
    /// </summary>
    public const string WsPrivatePath = "/v2/ws/private";

    /// <summary>
    /// The most candles <c>market/candles</c> returns for one request. Measured by asking for more: 1000 is served and
    /// 1200 is refused with 40053 "limit should be between (0, 1000]".
    /// <para>
    /// It is NOT the endpoint this adapter's history helper uses. See <see cref="HistoryCandlePage"/>.
    /// </para>
    /// </summary>
    public const int RecentCandlePage = 1000;

    /// <summary>
    /// The most candles <c>market/history-candles</c> returns for one request: 200 served, 201 refused with 40053
    /// "limit should be between 1~200", on both the spot and the derivative endpoint.
    /// <para>
    /// The venue has two candle endpoints with different caps AND different retention, which is why the smaller cap is
    /// the one that matters. Measured on one-minute BTCUSDT candles: <c>market/candles</c> answered a window thirty
    /// days old in full and a window six months or a year old with an EMPTY LIST and a success code, while
    /// <c>market/history-candles</c> answered all three. A history fetch written against the larger page would return
    /// nothing at all for anything older than about a month, successfully.
    /// </para>
    /// </summary>
    public const int HistoryCandlePage = 200;

    /// <summary>
    /// How many funding settlements one page of <c>market/history-fund-rate</c> holds. Measured, and the measurement is
    /// the reason this is a constant rather than a parameter: the venue's default page is 20, asking for 100 gives 100,
    /// and asking for 200 or 500 SILENTLY gives 100 as well - no error, no warning. A loop that trusted its own
    /// <c>pageSize</c> to decide whether a short page meant the end of history would stop at the first page.
    /// </summary>
    public const int FundingPage = 100;

    /// <summary>
    /// The first page of a paged answer. The venue pages funding history by page NUMBER rather than by a cursor or a
    /// timestamp, and the first page is one rather than zero.
    /// </summary>
    public const int FirstPage = 1;

    /// <summary>
    /// How many funding pages one request will walk before giving up. Paging by page number has no natural end - an
    /// answer that never shortens would loop for ever - so the walk is bounded, and at this page size it covers more
    /// than twenty years of eight-hourly settlements.
    /// </summary>
    public const int MaxFundingPages = 250;

    /// <summary>
    /// Requests this adapter allows itself in <see cref="RequestWindow"/>. The venue publishes its budget per endpoint
    /// and the tightest of the public endpoints read here is ten a second, which is what this is set from; the
    /// response header <c>x-mbx-used-remain-limit</c> counts down from ten on the position-tier endpoint, which agrees.
    /// </summary>
    public const int RequestsPerWindow = 10;

    /// <summary>The window <see cref="RequestsPerWindow"/> is counted over.</summary>
    public static readonly TimeSpan RequestWindow = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How many position-tier requests the instrument provider has outstanding at once. The tiers are one request per
    /// contract and there is no endpoint that answers for more than one, so listing the whole of a derivative family
    /// costs a request per contract; asking for several at a time keeps that bounded by the rate budget above rather
    /// than by the round trip, which was measured at about 0.7 s each.
    /// </summary>
    public const int TierRequestConcurrency = 8;

    /// <summary>
    /// How often to send <see cref="PingMessage"/>. The venue drops a socket it has heard nothing on; this is well
    /// inside that and was measured to be answered on both the public and the private stream.
    /// </summary>
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(20);

    /// <summary>
    /// What a ping is on this venue: the four characters, not JSON. Measured - the venue answers the literal
    /// <c>ping</c> with the literal <c>pong</c>, and both are plain text rather than an envelope.
    /// </summary>
    public const string PingMessage = "ping";

    /// <summary>The answer to <see cref="PingMessage"/>, which is not JSON and must not be parsed as any.</summary>
    public const string PongMessage = "pong";

    /// <summary>
    /// The header a broker id travels in on this venue, which the broker programme calls the channel API code. It is
    /// not part of the signed payload, so carrying one leaves the request the venue authenticates unchanged; measured
    /// to be accepted and ignored on a request that carries no key, so an id that is not a real channel code cannot
    /// break a request.
    /// </summary>
    public const string BrokerIdHeader = "X-CHANNEL-API-CODE";

    /// <summary>Where the leverage is set on the derivative families. Spot has none to set.</summary>
    public const string LeveragePath = "/api/v2/mix/account/set-leverage";

    // The four headers that authenticate a request. Only ACCESS-KEY could be confirmed against the live venue -
    // sending it turns the refusal from 40006 "Invalid ACCESS_KEY" into 40037 "Apikey does not exist", so the venue
    // is reading it - because the key is checked before anything else and no key can be borrowed to get past that.
    // The other three, and the string that is signed, are as the venue documents them and are UNVERIFIED here.
    public const string ApiKeyHeader = "ACCESS-KEY";
    public const string SignatureHeader = "ACCESS-SIGN";
    public const string TimestampHeader = "ACCESS-TIMESTAMP";
    public const string PassphraseHeader = "ACCESS-PASSPHRASE";

    /// <summary>
    /// The language the venue answers its own error messages in. Sent because the messages reach a person through a
    /// report, and a venue left to guess answers in whatever it decides from the address the request came from.
    /// </summary>
    public const string LocaleHeader = "locale";

    /// <summary>The value of <see cref="LocaleHeader"/>: English, so a refusal reads the same wherever a node runs.</summary>
    public const string Locale = "en-US";

    /// <summary>
    /// What the engine appends to a perpetual's symbol, as on the other venues, so a perpetual reads as one wherever
    /// it comes from.
    /// </summary>
    public const string PerpSuffix = "-PERP";

    /// <summary>
    /// The book depths the public stream publishes, and the channel that carries the whole book. Measured: books5 and
    /// books15 are served as repeated snapshots, and <c>books</c> is served as a snapshot followed by deltas.
    /// </summary>
    public const int SmallBookDepth = 5;

    /// <summary>The larger of the two snapshot depths the stream publishes.</summary>
    public const int LargeBookDepth = 15;

    /// <summary>
    /// What the venue calls the spot market in a stream subscription. The derivative families use their product type
    /// as the instrument type, so this is the only market whose stream name is not its product type.
    /// </summary>
    public const string SpotInstType = "SPOT";

    /// <summary>
    /// The prefix that turns a product type into its demo counterpart, and a symbol into its demo counterpart. One
    /// letter, in front of both.
    /// </summary>
    public const string DemoPrefix = "S";

    /// <summary>
    /// What the venue calls one contract of a USDC-margined perpetual. That family spells its symbols
    /// <c>BASE</c> + this - <c>BTCPERP</c>, <c>1000BONKPERP</c> - where the USDT family spells them
    /// <c>BASE</c> + <c>USDT</c>. Checked against all 49 contracts the family listed: every one of them.
    /// </summary>
    public const string UsdcContractSuffix = "PERP";

    /// <summary>The quote currency of the USDC-margined family, which its symbols do not name.</summary>
    public const string UsdcQuote = "USDC";

    /// <summary>The quote currency of the USDT-margined family, which its symbols do name.</summary>
    public const string UsdtQuote = "USDT";

    /// <summary>
    /// The contract type the derivative families hold, as the venue's own <c>symbolType</c> field spells it. The
    /// instrument class comes from this field and never from how a symbol is spelled: a venue that named a perpetual
    /// some other way would be read as spot by anything trusting the spelling.
    /// </summary>
    public const string PerpetualSymbolType = "perpetual";

    /// <summary>The status a spot pair the venue is trading reports.</summary>
    public const string SpotOnline = "online";

    /// <summary>The status a contract the venue is trading reports. Spot and the derivatives use different words.</summary>
    public const string ContractNormal = "normal";

    public static string HttpBase(IBitgetSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.BaseUrlHttp ?? DefaultHttpBase;
    }

    public static string WsPublic(IBitgetSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return (s.BaseUrlWs ?? DefaultWsBase) + WsPublicPath;
    }

    public static string WsPrivate(IBitgetSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return (s.BaseUrlWs ?? DefaultWsBase) + WsPrivatePath;
    }

    /// <summary>
    /// Which market a configured client really talks to, in the venue's own spelling: the instrument type a stream
    /// subscription names and, for the derivative families, the product type every REST request carries.
    /// <para>
    /// It is the whole of what selects a family here. Binance's families differ by host and Bybit's by the path its
    /// socket is opened on; Bitget's differ by this string and by nothing else, so this is what has to be compared
    /// when something asks whether a configuration really landed on the family it meant to.
    /// </para>
    /// </summary>
    public static string Market(IBitgetSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (s.ProductType == BitgetProductType.Spot)
        {
            // There is no demo spot market, so a demo spot client would be pointed at nothing. The client refuses
            // that combination when it is built; this stays plain SPOT so that the refusal is the only place it is
            // reported.
            return SpotInstType;
        }

        string quote = s.ProductType == BitgetProductType.UsdtFutures ? UsdtQuote : UsdcQuote;
        string prefix = s.TradingMode == BitgetTradingMode.Demo ? DemoPrefix : string.Empty;
        return prefix + quote + "-FUTURES";
    }

    /// <summary>Whether this configuration names one of the derivative families, which is where funding and leverage live.</summary>
    public static bool IsFutures(IBitgetSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.ProductType != BitgetProductType.Spot;
    }

    /// <summary>
    /// The instrument id of a venue symbol in the given family.
    /// <para>
    /// Spot keeps its own name: the venue spells a pair <c>BASE</c> + <c>QUOTE</c> with no separator, which is what an
    /// instrument id wants, and that was true of all 3169 pairs it listed.
    /// </para>
    /// <para>
    /// A perpetual gains the engine's <c>-PERP</c>, and the USDC family gains its quote currency with it, because the
    /// venue leaves the quote out of the symbol there: <c>BTCPERP</c> becomes <c>BTCUSDC-PERP</c>, so the id says what
    /// the position is margined in and cannot collide with the USDT family's <c>BTCUSDT-PERP</c>.
    /// </para>
    /// </summary>
    public static InstrumentId ToInstrumentId(string rawSymbol, BitgetProductType productType, BitgetTradingMode mode = BitgetTradingMode.Live)
    {
        ArgumentNullException.ThrowIfNull(rawSymbol);
        if (productType == BitgetProductType.Spot)
        {
            return new InstrumentId(new Symbol(rawSymbol), Venue);
        }

        if (productType == BitgetProductType.UsdtFutures)
        {
            return new InstrumentId(new Symbol(rawSymbol + PerpSuffix), Venue);
        }

        string suffix = ContractSuffix(mode);
        string baseCoin = rawSymbol.EndsWith(suffix, StringComparison.Ordinal) ? rawSymbol[..^suffix.Length] : rawSymbol;
        return new InstrumentId(new Symbol(baseCoin + Quote(productType, mode) + PerpSuffix), Venue);
    }

    /// <summary>The venue symbol behind an instrument id, which is <see cref="ToInstrumentId"/> run backwards.</summary>
    public static string ToRawSymbol(InstrumentId id, BitgetProductType productType, BitgetTradingMode mode = BitgetTradingMode.Live)
    {
        string symbol = id.Symbol.Value;
        if (productType == BitgetProductType.Spot)
        {
            return symbol;
        }

        if (!symbol.EndsWith(PerpSuffix, StringComparison.Ordinal))
        {
            return symbol;
        }

        string withoutPerp = symbol[..^PerpSuffix.Length];
        if (productType == BitgetProductType.UsdtFutures)
        {
            return withoutPerp;
        }

        string quote = Quote(productType, mode);
        string baseCoin = withoutPerp.EndsWith(quote, StringComparison.Ordinal) ? withoutPerp[..^quote.Length] : withoutPerp;
        return baseCoin + ContractSuffix(mode);
    }

    /// <summary>
    /// What a family's symbols are quoted in. The demo markets prefix their currencies as well as their product types -
    /// a demo USDT perpetual is <c>SBTCSUSDT</c>, quoted in <c>SUSDT</c> - so the prefix belongs on both.
    /// </summary>
    private static string Quote(BitgetProductType productType, BitgetTradingMode mode)
    {
        string prefix = mode == BitgetTradingMode.Demo ? DemoPrefix : string.Empty;
        return prefix + (productType == BitgetProductType.UsdcFutures ? UsdcQuote : UsdtQuote);
    }

    /// <summary>
    /// What the USDC family puts where the USDT family puts its quote currency. Prefixed in demo, where the contracts
    /// are spelled <c>SBTCSPERP</c>.
    /// </summary>
    private static string ContractSuffix(BitgetTradingMode mode) =>
        (mode == BitgetTradingMode.Demo ? DemoPrefix : string.Empty) + UsdcContractSuffix;

    /// <summary>
    /// What the REST candle endpoints call a bar of the given length. The two markets spell it differently for the
    /// same lengths - spot wants <c>1min</c> and <c>1day</c> where the derivatives want <c>1m</c> and <c>1D</c> - and
    /// the lengths they keep are not the same either: the derivatives keep two hours and spot does not.
    /// <para>
    /// Every entry here was accepted by the live endpoint and everything left out was refused by it. That is worth
    /// saying because the venue's own refusal message lists the lengths it accepts and the list is WRONG: it names
    /// neither <c>3day</c> on spot nor <c>2H</c> and <c>3D</c> on the derivatives, and all three are served.
    /// </para>
    /// </summary>
    public static string RestGranularity(BarSpecification spec, BitgetProductType productType) =>
        productType == BitgetProductType.Spot ? SpotRestGranularity(spec) : FuturesRestGranularity(spec);

    private static string SpotRestGranularity(BarSpecification spec) => (spec.Aggregation, spec.Step) switch
    {
        (BarAggregation.Minute, 1) => "1min",
        (BarAggregation.Minute, 3) => "3min",
        (BarAggregation.Minute, 5) => "5min",
        (BarAggregation.Minute, 15) => "15min",
        (BarAggregation.Minute, 30) => "30min",
        (BarAggregation.Hour, 1) => "1h",
        (BarAggregation.Hour, 4) => "4h",
        (BarAggregation.Hour, 6) => "6h",
        (BarAggregation.Hour, 12) => "12h",
        (BarAggregation.Day, 1) => "1day",
        (BarAggregation.Day, 3) => "3day",
        (BarAggregation.Week, 1) => "1week",
        (BarAggregation.Month, 1) => "1M",
        _ => throw new NotSupportedException(
            $"Bitget's spot candle endpoint does not keep {spec} candles. It keeps 1, 3, 5, 15 and 30 minutes, 1, 4, 6 "
            + "and 12 hours, 1 and 3 days, a week and a month. Two hours is kept on the derivative markets and not on "
            + "this one."),
    };

    private static string FuturesRestGranularity(BarSpecification spec) => (spec.Aggregation, spec.Step) switch
    {
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
        (BarAggregation.Day, 3) => "3D",
        (BarAggregation.Week, 1) => "1W",
        (BarAggregation.Month, 1) => "1M",
        _ => throw new NotSupportedException(
            $"Bitget's derivative candle endpoint does not keep {spec} candles. It keeps 1, 3, 5, 15 and 30 minutes, "
            + "1, 2, 4, 6 and 12 hours, 1 and 3 days, a week and a month. Eight hours is carried on the stream and not "
            + "by this endpoint."),
    };

    /// <summary>
    /// What the public stream calls a candle of the given length. One spelling for every market - the demo markets and
    /// the spot market use the same channel names as the derivatives - and it is NOT the spelling either REST endpoint
    /// uses: <c>candle1H</c> on the stream, <c>1h</c> on spot REST, <c>1H</c> on derivative REST.
    /// <para>
    /// Measured by subscribing: <c>candle1h</c> and <c>candle1min</c> are refused on the spot stream with code 30016
    /// "Param error", and <c>candle1H</c> is accepted there. A client that reused the spot REST spelling on the socket
    /// would subscribe to nothing and be told why only in a message it was not reading.
    /// </para>
    /// <para>
    /// The stream carries one length the REST endpoints do not: eight hours. It is offered here because a subscription
    /// is a separate capability from a history fetch, and a caller that asks for eight-hour bars over REST is told so
    /// by <see cref="RestGranularity"/> rather than being refused a live subscription that works.
    /// </para>
    /// </summary>
    public static string StreamGranularity(BarSpecification spec) => (spec.Aggregation, spec.Step) switch
    {
        (BarAggregation.Minute, 1) => "1m",
        (BarAggregation.Minute, 3) => "3m",
        (BarAggregation.Minute, 5) => "5m",
        (BarAggregation.Minute, 15) => "15m",
        (BarAggregation.Minute, 30) => "30m",
        (BarAggregation.Hour, 1) => "1H",
        (BarAggregation.Hour, 2) => "2H",
        (BarAggregation.Hour, 4) => "4H",
        (BarAggregation.Hour, 6) => "6H",
        (BarAggregation.Hour, 8) => "8H",
        (BarAggregation.Hour, 12) => "12H",
        (BarAggregation.Day, 1) => "1D",
        (BarAggregation.Day, 3) => "3D",
        (BarAggregation.Week, 1) => "1W",
        (BarAggregation.Month, 1) => "1M",
        _ => throw new NotSupportedException(
            $"Bitget's stream does not publish {spec} candles. It publishes 1, 3, 5, 15 and 30 minutes, 1, 2, 4, 6, 8 "
            + "and 12 hours, 1 and 3 days, a week and a month."),
    };

    public static BitgetCredentials Credentials(IBitgetSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new BitgetCredentials(
            Secrets.Require(s.ApiKey, EnvApiKey),
            Secrets.Require(s.ApiSecret, EnvApiSecret),
            Secrets.Require(s.ApiPassphrase, EnvApiPassphrase));
    }

    public static BitgetCredentials? OptionalCredentials(IBitgetSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        string? key = Secrets.Optional(s.ApiKey, EnvApiKey);
        string? secret = Secrets.Optional(s.ApiSecret, EnvApiSecret);
        string? passphrase = Secrets.Optional(s.ApiPassphrase, EnvApiPassphrase);
        return key is null || secret is null || passphrase is null ? null : new BitgetCredentials(key, secret, passphrase);
    }
}

/// <summary>A Bitget key has three parts: the key, the secret, and a passphrase chosen when the key was made.</summary>
public sealed record BitgetCredentials(string Key, string Secret, string Passphrase)
{
    // Never the secret or the passphrase in a log line or an exception text.
    public override string ToString() => "BitgetCredentials(three parts)";
}

/// <summary>
/// Bitget v2 REST access with request signing. Every answer is an envelope <c>{ code, msg, requestTime, data }</c>,
/// and a code other than <c>00000</c> is a refusal whatever the HTTP status says - the venue sends both 200 and 400
/// with the same shape of body, so the status alone tells a caller nothing.
/// </summary>
public sealed class BitgetHttp : IDisposable
{
    private readonly HttpClientWrapper _http;
    private readonly BitgetCredentials? _credentials;
    private readonly string? _brokerId;

    public BitgetHttp(IBitgetSettings settings, ILogger? logger = null, bool requireCredentials = false, string? brokerId = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _credentials = requireCredentials ? BitgetVenue.Credentials(settings) : BitgetVenue.OptionalCredentials(settings);

        // Only an execution client has one: there is nothing to attribute about reading a public order book.
        _brokerId = brokerId;
        ProductType = settings.ProductType;
        TradingMode = settings.TradingMode;
        Market = BitgetVenue.Market(settings);
        _http = new HttpClientWrapper(
            new Uri(BitgetVenue.HttpBase(settings)),
            new RateLimiter(BitgetVenue.RequestsPerWindow, BitgetVenue.RequestWindow),
            new RetryPolicy(),
            logger,
            defaultHeaders: new Dictionary<string, string>(StringComparer.Ordinal) { [BitgetVenue.LocaleHeader] = BitgetVenue.Locale });
    }

    /// <summary>Which family this client is pointed at, carried so a helper handed nothing but this answers for it.</summary>
    public BitgetProductType ProductType { get; }

    public BitgetTradingMode TradingMode { get; }

    /// <summary>The venue's own name for this family, which every derivative request carries as its product type.</summary>
    public string Market { get; }

    public bool HasCredentials => _credentials is not null;

    /// <summary>The instrument id of a venue symbol in this client's family.</summary>
    public InstrumentId ToInstrumentId(string rawSymbol) => BitgetVenue.ToInstrumentId(rawSymbol, ProductType, TradingMode);

    /// <summary>The venue symbol behind an instrument id in this client's family.</summary>
    public string ToRawSymbol(InstrumentId id) => BitgetVenue.ToRawSymbol(id, ProductType, TradingMode);

    /// <summary>
    /// The product type a derivative request carries, added to a query only for the derivative families: the spot
    /// endpoints refuse an unknown parameter rather than ignoring it.
    /// </summary>
    public Dictionary<string, string> Query(params (string Name, string Value)[] pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        Dictionary<string, string> query = new(StringComparer.Ordinal);
        if (ProductType != BitgetProductType.Spot)
        {
            query["productType"] = Market;
        }

        foreach ((string name, string value) in pairs)
        {
            query[name] = value;
        }

        return query;
    }

    public Task<JsonElement> GetPublicAsync(string path, IReadOnlyDictionary<string, string>? query = null, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, path, query, null, signed: false, ct);

    public Task<JsonElement> GetSignedAsync(string path, IReadOnlyDictionary<string, string>? query = null, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, path, query, null, signed: true, ct);

    public Task<JsonElement> PostSignedAsync(string path, object? body, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, path, null, body is null ? string.Empty : JsonSerializer.Serialize(body), signed: true, ct);

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, IReadOnlyDictionary<string, string>? query, string? json, bool signed, CancellationToken ct)
    {
        // The signature covers the path with its query exactly as it is sent, so the query is folded into the path
        // here and the wrapper is given nothing more to append.
        string endpoint = query is { Count: > 0 } ? path + "?" + HttpClientWrapper.BuildQuery(query) : path;
        Dictionary<string, string> headers = signed ? Sign(method.Method, endpoint, json ?? string.Empty) : new Dictionary<string, string>(StringComparer.Ordinal);
        if (_brokerId is { Length: > 0 } broker)
        {
            // Outside the signature, so an id changes nothing about the request the venue authenticates. Untagged,
            // these headers are byte-for-byte what they were before the field existed.
            headers[BitgetVenue.BrokerIdHeader] = broker;
        }

        StringContent? content = string.IsNullOrEmpty(json) ? null : new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        string text;
        try
        {
            text = await _http.SendAsync(method, endpoint, null, content, headers.Count > 0 ? headers : null, 1, ct).ConfigureAwait(false);
        }
        catch (VenueHttpException e) when (TryError(e.Body) is { } error)
        {
            // The venue sends its own code with a 400 as readily as with a 200, so the body is what decides.
            throw new BitgetApiException(error.Code, error.Message, (int)e.StatusCode);
        }

        return Unwrap(text);
    }

    /// <summary>
    /// The headers that authenticate a request. The signed string is the timestamp, the method in upper case, the path
    /// with its query, and the body - as the venue documents it. UNVERIFIED against the live venue: the key is checked
    /// before the signature, so a request with no valid key never gets far enough to say whether the signature was
    /// right.
    /// </summary>
    private Dictionary<string, string> Sign(string method, string endpoint, string body)
    {
        BitgetCredentials c = _credentials ?? throw new InvalidOperationException(
            $"Bitget API credentials are required for this request. Set {BitgetVenue.EnvApiKey}, "
            + $"{BitgetVenue.EnvApiSecret} and {BitgetVenue.EnvApiPassphrase}.");

        string timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BitgetVenue.ApiKeyHeader] = c.Key,
            [BitgetVenue.SignatureHeader] = HmacSigner.Sha256Base64(c.Secret, timestamp + method.ToUpperInvariant() + endpoint + body),
            [BitgetVenue.TimestampHeader] = timestamp,

            // In clear, which is what this venue wants: the passphrase is a third factor here rather than something
            // signed with the secret as it is on KuCoin.
            [BitgetVenue.PassphraseHeader] = c.Passphrase,
        };
    }

    private static JsonElement Unwrap(string text)
    {
        using JsonDocument doc = JsonDocument.Parse(text);
        JsonElement root = doc.RootElement;
        string code = root.Str("code");
        if (code.Length > 0 && !string.Equals(code, BitgetVenue.Ok, StringComparison.Ordinal))
        {
            throw new BitgetApiException(code, root.Str("msg"), (int)System.Net.HttpStatusCode.OK);
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

public sealed class BitgetApiException : Exception
{
    public BitgetApiException(string code, string message, int httpStatus)
        : base($"Bitget error {code}: {message}")
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

    public static string Str(this JsonElement e, string name) => e.TryGetProperty(name, out JsonElement p)
        ? p.ValueKind switch
        {
            JsonValueKind.String => p.GetString() ?? string.Empty,
            JsonValueKind.Number => p.GetRawText(),
            _ => string.Empty,
        }
        : string.Empty;

    public static long Long(this JsonElement e, string name) => e.TryGetProperty(name, out JsonElement p) ? p.LongValue() : 0;

    public static long LongValue(this JsonElement p) => p.ValueKind switch
    {
        JsonValueKind.Number => p.GetInt64(),
        JsonValueKind.String => long.TryParse(p.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long l) ? l : 0,
        _ => 0,
    };

    public static bool Has(this JsonElement e, string name) => e.TryGetProperty(name, out JsonElement p) && p.ValueKind != JsonValueKind.Null;

    public static UnixNanos Ms(this JsonElement e, string name) => UnixNanos.FromMilliseconds(e.Long(name));

    public static string Fmt(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The increment a number of decimal places describes. Bitget publishes precision rather than a tick - spot says
    /// "two decimal places" where other venues say "0.01" - so the tick is built from it.
    /// </summary>
    public static decimal Increment(int decimals) => decimals <= 0 ? 1m : 1m / Pow10(decimals);

    private static decimal Pow10(int exponent)
    {
        decimal result = 1m;
        for (int i = 0; i < exponent; i++)
        {
            result *= 10m;
        }

        return result;
    }
}

/// <summary>
/// One row of a contract's position-tier table: the notional band it applies to, the most leverage allowed in it, and
/// the maintenance margin rate charged in it.
/// </summary>
/// <param name="Level">The tier's number, one being the smallest positions.</param>
/// <param name="StartUnit">The notional, in the contract's quote currency, at which the tier begins.</param>
/// <param name="EndUnit">The notional at which it ends.</param>
/// <param name="Leverage">The most leverage the venue allows on a position of that size.</param>
/// <param name="KeepMarginRate">The maintenance margin rate charged on a position of that size.</param>
public sealed record BitgetPositionTier(int Level, decimal StartUnit, decimal EndUnit, decimal Leverage, decimal KeepMarginRate);

/// <summary>
/// Loads Bitget instrument definitions for one family: spot pairs from <c>/api/v2/spot/public/symbols</c> and
/// perpetual contracts from <c>/api/v2/mix/market/contracts</c>.
/// <para>
/// A contract's size is in base currency here and needs no conversion: the venue states a minimum order of 0.0001 BTC
/// and a step of 0.0001 BTC on BTCUSDT, so a quantity crossing this boundary means what it means on Binance and Bybit.
/// That is a fact about this venue rather than a choice, and it is why there is no contract multiplier anywhere in
/// this adapter.
/// </para>
/// <para>
/// The margin rates are the venue's own. Every other adapter in this repository publishes a flat 5 percent initial and
/// 2.5 percent maintenance on every contract, which is a guess that is wrong on almost all of them: the venue's tier
/// table gives BTCUSDT 0.67 percent initial and 0.4 percent maintenance, and gives IDOLUSDT 10 percent and 5 percent.
/// So the initial rate comes from the contract's own maximum leverage and the maintenance rate from the first row of
/// its tier table, which cost a request per contract and is the reason <see cref="LoadAllAsync"/> is slow on this
/// venue. It cannot be derived instead: three contracts measured at 50x maximum leverage carry maintenance rates of
/// 1, 1.4 and 1.5 percent, so the two figures are not a function of each other.
/// </para>
/// </summary>
public sealed class BitgetInstrumentProvider : InstrumentProviderBase
{
    /// <summary>Where the spot catalog answers.</summary>
    public const string SpotSymbolsPath = "/api/v2/spot/public/symbols";

    /// <summary>Where the derivative catalog answers.</summary>
    public const string ContractsPath = "/api/v2/mix/market/contracts";

    /// <summary>
    /// Where a contract's position tiers answer. It takes exactly one symbol: asked with a product type and no symbol
    /// it refuses with 400172, and asked with two comma-separated symbols it refuses with 40034.
    /// </summary>
    public const string PositionTiersPath = "/api/v2/mix/market/query-position-lever";

    /// <summary>
    /// What the maximum leverage of a contract is filed under on the instrument, so that whatever sets a leverage can
    /// see what the venue would allow before it asks.
    /// </summary>
    public const string MaxLeverageInfo = "maxLeverage";

    private readonly BitgetHttp _http;

    public BitgetInstrumentProvider(BitgetHttp http, InstrumentProviderConfig? config = null, ILogger? logger = null)
        : base(BitgetVenue.Venue, config, logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public BitgetProductType ProductType => _http.ProductType;

    public override async Task LoadAllAsync(CancellationToken ct, IReadOnlyDictionary<string, string>? filters = null)
    {
        if (ProductType == BitgetProductType.Spot)
        {
            JsonElement pairs = await _http.GetPublicAsync(SpotSymbolsPath, null, ct).ConfigureAwait(false);
            int spot = 0;
            foreach (JsonElement item in Rows(pairs))
            {
                if (ParseSpot(item) is { } pair && Wanted(pair, filters))
                {
                    Add(pair);
                    spot++;
                }
            }

            Log.LogInformation("Loaded {Count} Bitget spot pairs", spot);
            return;
        }

        JsonElement data = await _http.GetPublicAsync(ContractsPath, _http.Query(), ct).ConfigureAwait(false);
        List<JsonElement> contracts = [.. Rows(data)];
        int dated = contracts.Count(c => !c.Str("symbolType").Equals(BitgetVenue.PerpetualSymbolType, StringComparison.OrdinalIgnoreCase));

        // The tiers, asked for concurrently because there is one request per contract and no endpoint that answers for
        // more than one. A contract whose tiers cannot be read is loaded without a maintenance rate rather than left
        // out: an instrument nobody can find is worse than one whose margin a caller has to ask about.
        Dictionary<string, IReadOnlyList<BitgetPositionTier>> tiers = await TiersAsync(
            [.. contracts.Select(c => c.Str("symbol")).Where(s => s.Length > 0)],
            ct).ConfigureAwait(false);

        int loaded = 0;
        foreach (JsonElement item in contracts)
        {
            if (ParseContract(item, tiers.GetValueOrDefault(item.Str("symbol"))) is { } contract && Wanted(contract, filters))
            {
                Add(contract);
                loaded++;
            }
        }

        Log.LogInformation(
            "Loaded {Count} Bitget {Market} perpetual contracts with their position tiers, leaving out {Dated} that are not perpetual",
            loaded,
            _http.Market,
            dated);
    }

    public override async Task LoadAsync(InstrumentId id, CancellationToken ct)
    {
        string raw = _http.ToRawSymbol(id);
        JsonElement data;
        try
        {
            data = ProductType == BitgetProductType.Spot
                ? await _http.GetPublicAsync(SpotSymbolsPath, new Dictionary<string, string>(StringComparer.Ordinal) { ["symbol"] = raw }, ct).ConfigureAwait(false)
                : await _http.GetPublicAsync(ContractsPath, _http.Query(("symbol", raw)), ct).ConfigureAwait(false);
        }
        catch (BitgetApiException e) when (BitgetVenue.ErrorsThatMeanNoSuchInstrument.Contains(e.Code))
        {
            // Not listed is an answer, not a failure. Only this adapter knows the venue's codes for it, which is the
            // point: a caller looks the instrument up afterwards and finds nothing, the same on every venue.
            Log.LogInformation("Bitget does not list {Instrument} in its {Market} market", id, _http.Market);
            return;
        }

        foreach (JsonElement item in Rows(data))
        {
            if (ProductType == BitgetProductType.Spot)
            {
                // Only the instrument that was asked for, so "load one" means one whatever the venue returns.
                if (ParseSpot(item) is { } pair && pair.Id == id)
                {
                    Add(pair);
                }

                continue;
            }

            string symbol = item.Str("symbol");
            if (!string.Equals(symbol, raw, StringComparison.Ordinal))
            {
                continue;
            }

            Dictionary<string, IReadOnlyList<BitgetPositionTier>> tiers = await TiersAsync([symbol], ct).ConfigureAwait(false);
            if (ParseContract(item, tiers.GetValueOrDefault(symbol)) is { } contract && contract.Id == id)
            {
                Add(contract);
            }
        }
    }

    /// <summary>
    /// A contract's position tiers, oldest band first. Public: a host pricing a position of a given size needs the
    /// whole table rather than the first row, and there is nowhere on an instrument to put a table.
    /// </summary>
    public static async Task<IReadOnlyList<BitgetPositionTier>> FetchPositionTiersAsync(BitgetHttp http, string rawSymbol, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        JsonElement data = await http.GetPublicAsync(PositionTiersPath, http.Query(("symbol", rawSymbol)), ct).ConfigureAwait(false);
        List<BitgetPositionTier> tiers = [];
        foreach (JsonElement row in Rows(data))
        {
            tiers.Add(new BitgetPositionTier(
                (int)row.Long("level"),
                row.Dec("startUnit"),
                row.Dec("endUnit"),
                row.Dec("leverage"),
                row.Dec("keepMarginRate")));
        }

        return [.. tiers.OrderBy(t => t.Level)];
    }

    private async Task<Dictionary<string, IReadOnlyList<BitgetPositionTier>>> TiersAsync(IReadOnlyList<string> symbols, CancellationToken ct)
    {
        Dictionary<string, IReadOnlyList<BitgetPositionTier>> tiers = new(StringComparer.Ordinal);
        SemaphoreSlim gate = new(BitgetVenue.TierRequestConcurrency, BitgetVenue.TierRequestConcurrency);
        object writer = new();
        await Task.WhenAll(symbols.Select(async symbol =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                IReadOnlyList<BitgetPositionTier> rows = await FetchPositionTiersAsync(_http, symbol, ct).ConfigureAwait(false);
                lock (writer)
                {
                    tiers[symbol] = rows;
                }
            }
            catch (Exception e) when (e is BitgetApiException or VenueHttpException or HttpRequestException)
            {
                Log.LogWarning(
                    "Bitget would not say what the position tiers of {Symbol} are ({Reason}), so it is loaded without a maintenance margin rate",
                    symbol,
                    e.Message);
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        gate.Dispose();
        return tiers;
    }

    private static bool Wanted(Instrument instrument, IReadOnlyDictionary<string, string>? filters) =>
        filters is null
        || !filters.TryGetValue("quote", out string? quote)
        || instrument.QuoteCurrency.Code.Equals(quote, StringComparison.OrdinalIgnoreCase);

    /// <summary>The venue answers both catalogs with an array, and with an array of one when asked about one symbol.</summary>
    private static IEnumerable<JsonElement> Rows(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (JsonElement item in data.EnumerateArray())
        {
            yield return item;
        }
    }

    /// <summary>
    /// A spot pair. The venue states precision as a number of decimal places rather than as an increment, and states
    /// the smallest order in quote currency (<c>minTradeUSDT</c>) as well as in base currency.
    /// </summary>
    private static Instrument? ParseSpot(JsonElement item)
    {
        if (!item.Str("status").Equals(BitgetVenue.SpotOnline, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string raw = item.Str("symbol");
        if (raw.Length == 0)
        {
            return null;
        }

        byte pricePrecision = (byte)item.Long("pricePrecision");
        byte sizePrecision = (byte)item.Long("quantityPrecision");
        decimal tick = Json.Increment(pricePrecision);
        decimal step = Json.Increment(sizePrecision);

        // The venue spells a base currency in the case it is traded under - "rCVCO" - and the symbol in upper case, so
        // the currency code is taken as written and the symbol as written, and the two agree on every pair listed.
        Currency quote = Currency.FromCode(item.Str("quoteCoin"), 8);
        Currency baseCurrency = Currency.FromCode(item.Str("baseCoin"), 8);
        decimal minQty = item.Dec("minTradeAmount");
        decimal minNotional = item.Dec("minTradeUSDT");
        UnixNanos now = UnixNanos.FromDateTimeOffset(DateTimeOffset.UtcNow);

        return new CurrencyPair(new InstrumentSpec
        {
            Id = BitgetVenue.ToInstrumentId(raw, BitgetProductType.Spot),
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
            MinQuantity = minQty > 0m ? new Quantity(minQty, sizePrecision) : new Quantity(step, sizePrecision),
            MinNotional = minNotional > 0m ? new Money(minNotional, quote) : null,
            MakerFee = item.Dec("makerFeeRate"),
            TakerFee = item.Dec("takerFeeRate"),
            // Nothing is borrowed on a cash pair, so the absent margin above is a fact about spot and not a figure
            // nobody read. Said here because the default is "unrecorded", which would be a different claim.
            MarginSource = MarginSource.NotMargined,
            TsEvent = now,
            TsInit = now,
        });
    }

    /// <summary>
    /// A perpetual contract. The instrument class comes from the venue's <c>symbolType</c> field: a contract that is
    /// not a perpetual is left out rather than published as one, because that is what the families declare they hold.
    /// <para>
    /// The venue's only non-perpetual contracts today are its coin-margined delivery futures, and those are in a
    /// product type that answers its contract list with an empty array - so nothing here is filtered out today. The
    /// check is here because "the list is empty" and "this family is perpetuals" are two different facts that happen to
    /// coincide, and a delivery contract appearing in one of these lists must not be published as a perpetual: it
    /// would carry no expiry, be assumed funded, and contradict a declaration that nothing else could catch.
    /// </para>
    /// </summary>
    private Instrument? ParseContract(JsonElement item, IReadOnlyList<BitgetPositionTier>? tiers)
    {
        if (!item.Str("symbolStatus").Equals(BitgetVenue.ContractNormal, StringComparison.OrdinalIgnoreCase)
            || !item.Str("symbolType").Equals(BitgetVenue.PerpetualSymbolType, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string raw = item.Str("symbol");
        if (raw.Length == 0)
        {
            return null;
        }

        // A tick is the last-digit step at the stated number of places: BTCUSDT is one step at one place, so 0.1, and
        // a contract with priceEndStep 5 at one place moves in halves.
        byte pricePrecision = (byte)item.Long("pricePlace");
        decimal endStep = item.Dec("priceEndStep");
        decimal tick = (endStep > 0m ? endStep : 1m) * Json.Increment(pricePrecision);
        byte sizePrecision = (byte)item.Long("volumePlace");
        decimal step = item.Dec("sizeMultiplier");
        if (tick <= 0m || step <= 0m)
        {
            return null;
        }

        Currency quote = Currency.FromCode(item.Str("quoteCoin"), 8);
        Currency baseCurrency = Currency.FromCode(item.Str("baseCoin"), 8);

        // What the position is margined in, which the venue lists rather than states: every contract in both families
        // listed exactly one margin coin, and it is the family's own collateral.
        Currency settlement = item.TryGetProperty("supportMarginCoins", out JsonElement coins)
            && coins.ValueKind == JsonValueKind.Array && coins.GetArrayLength() > 0
            ? Currency.FromCode(coins[0].GetString() ?? quote.Code, 8)
            : quote;

        decimal maxLeverage = item.Dec("maxLever");
        decimal minQty = item.Dec("minTradeNum");
        decimal maxQty = item.Dec("maxOrderQty");
        decimal minNotional = item.Dec("minTradeUSDT");

        // The first tier is the one a position starts in, so its rates are the ones an empty account is held to. The
        // initial rate is the reciprocal of the leverage the venue allows there, which was measured to be the same
        // figure the contract list publishes as maxLever on all twelve contracts checked.
        BitgetPositionTier? first = tiers?.FirstOrDefault();
        decimal marginInit = first is { Leverage: > 0m } ? 1m / first.Leverage : maxLeverage > 0m ? 1m / maxLeverage : 0m;
        decimal marginMaint = first?.KeepMarginRate ?? 0m;
        UnixNanos now = UnixNanos.FromDateTimeOffset(DateTimeOffset.UtcNow);

        return new CryptoPerpetual(new InstrumentSpec
        {
            Id = BitgetVenue.ToInstrumentId(raw, ProductType, _http.TradingMode),
            RawSymbol = new Symbol(raw),
            AssetClass = AssetClass.Crypto,
            InstrumentClass = InstrumentClass.Swap,
            QuoteCurrency = quote,
            BaseCurrency = baseCurrency,
            SettlementCurrency = settlement,
            PricePrecision = pricePrecision,
            SizePrecision = sizePrecision,
            PriceIncrement = new Price(tick, pricePrecision),
            SizeIncrement = new Quantity(step, sizePrecision),
            MinQuantity = minQty > 0m ? new Quantity(minQty, sizePrecision) : new Quantity(step, sizePrecision),
            MaxQuantity = maxQty > 0m ? new Quantity(maxQty, sizePrecision) : null,
            MinNotional = minNotional > 0m ? new Money(minNotional, quote) : null,
            MakerFee = item.Dec("makerFeeRate"),
            TakerFee = item.Dec("takerFeeRate"),
            MarginInit = marginInit,
            MarginMaint = marginMaint,

            // Either figure above came from this contract's own tier table or from the ceiling published beside it,
            // both of them the venue's. A zero means neither was there, which is a gap and not a requirement of
            // none.
            MarginSource = marginInit > 0m ? MarginSource.VenuePerContract : MarginSource.VenueSilent,
            Info = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [MaxLeverageInfo] = Json.Fmt(maxLeverage),
            },
            TsEvent = now,
            TsInit = now,
        });
    }
}
