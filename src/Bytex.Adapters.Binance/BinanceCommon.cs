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

    /// <summary>
    /// Coin-margined perpetual and delivery futures, on the venue's <c>dapi</c> host. Quoted in USD, sized in a whole
    /// number of USD contracts, and margined and settled in the contract's own base coin - so every money figure on
    /// this family is INVERSE and comes out in the coin rather than in USD.
    /// <para>
    /// A third family and not a variant of the second: measured on 2026-09-25 the two disagree about the host, the
    /// endpoint version of every account read, the field a contract's tradability is published in, and whether a
    /// contract carries a size at all. Selecting it with a flag on the USD-margined family would have meant every
    /// one of those differences being decided by an <c>if</c> somewhere rather than by the account type.
    /// </para>
    /// </summary>
    CoinMFutures = 3,
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
    /// <para>
    /// Per family, because each of this venue's futures markets answers on its own host under its own prefix. The
    /// coin-margined endpoint was confirmed to exist on 2026-09-25 the only way an unauthenticated caller can: a
    /// POST to it is refused for the key rather than for the route, where a path this venue does not serve answers
    /// with an HTML error page instead.
    /// </para>
    /// </summary>
    public static string LeveragePath(BinanceAccountType type) => ApiPrefix(type) + "/leverage";

    /// <summary>
    /// Where this venue publishes the margin it really requires, per symbol and per notional bracket. Signed on both
    /// futures families, and there is no public equivalent on either: measured 2026-09-25, the unauthenticated call
    /// is refused with -2014 on <c>fapi</c> and on <c>dapi</c> alike, the web interface's own bracket feed rejects
    /// it, and no futures-data path serves it.
    /// </summary>
    public static string LeverageBracketPath(BinanceAccountType type) => ApiPrefix(type) + "/leverageBracket";

    /// <summary>What the brackets request costs against the venue's weight budget.</summary>
    public const int LeverageBracketWeight = 1;

    /// <summary>
    /// Where this venue reports a futures account's balances, and the one place the two futures families differ by
    /// more than their prefix: the USD-margined read is at version 2 and the coin-margined one at version 1.
    /// Measured 2026-09-25 - <c>/dapi/v2/balance</c> and <c>/fapi/v1/balance</c> are both served an HTML error page,
    /// so neither family's path is reachable under the other's version and a shared prefix would have found the
    /// wrong one on whichever family was not tested.
    /// </summary>
    public static string BalancePath(BinanceAccountType type) => type switch
    {
        BinanceAccountType.UsdMFutures => "/fapi/v2/balance",
        BinanceAccountType.CoinMFutures => "/dapi/v1/balance",
        _ => "/api/v3/account",
    };

    /// <summary>
    /// Where this venue reports open positions, versioned per family for the same measured reason as
    /// <see cref="BalancePath"/>: version 2 on the USD-margined host and version 1 on the coin-margined one.
    /// </summary>
    public static string PositionRiskPath(BinanceAccountType type) => type switch
    {
        BinanceAccountType.UsdMFutures => "/fapi/v2/positionRisk",
        BinanceAccountType.CoinMFutures => "/dapi/v1/positionRisk",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "a spot account holds no positions to report"),
    };

    /// <summary>
    /// Where a user-data listen key is created, renewed and closed. The futures families name it under their own
    /// prefix; spot calls the same thing something else and takes the key back as a parameter.
    /// </summary>
    public static string ListenKeyPath(BinanceAccountType type) =>
        IsFutures(type) ? ApiPrefix(type) + "/listenKey" : "/api/v3/userDataStream";

    /// <summary>Where every open order for one symbol is cancelled at once.</summary>
    public static string AllOpenOrdersPath(BinanceAccountType type) =>
        IsFutures(type) ? ApiPrefix(type) + "/allOpenOrders" : "/api/v3/openOrders";

    /// <summary>Where an account's own fills are read back, for reconciliation.</summary>
    public static string UserTradesPath(BinanceAccountType type) =>
        IsFutures(type) ? ApiPrefix(type) + "/userTrades" : "/api/v3/myTrades";

    /// <summary>
    /// Whether this account type is one of the venue's two futures markets. Named because a great many facts are
    /// true of both and false of spot - a leverage to set, a mark price to stream, funding to be charged, positions
    /// to report - and every one of them was a comparison against the one futures family that existed.
    /// </summary>
    public static bool IsFutures(BinanceAccountType type) =>
        type is BinanceAccountType.UsdMFutures or BinanceAccountType.CoinMFutures;

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
    /// <para>
    /// The coin-margined family publishes exactly the same pair, and measurably as a default rather than as its own
    /// figure: on 2026-09-25 all 30 of its contracts answered <c>requiredMarginPercent</c> 5.0000 and
    /// <c>maintMarginPercent</c> 2.5000, from the 100-USD BTCUSD_PERP to the 10-USD altcoin quarterlies. A number
    /// identical across every contract of a market is not what that market requires of each of them.
    /// </para>
    /// </summary>
    public const decimal DefaultMarginInit = 0.05m;

    /// <inheritdoc cref="DefaultMarginInit"/>
    public const decimal DefaultMarginMaint = 0.025m;

    /// <summary>
    /// What the USD-margined family charges before an instrument is loaded, from this venue's published schedule for
    /// that market. An instrument's own rates replace these the moment there is an instrument, and an account's real
    /// rates are behind the signed <c>commissionRate</c> read.
    /// </summary>
    public const decimal UsdMFuturesMakerFee = 0.0002m;

    /// <inheritdoc cref="UsdMFuturesMakerFee"/>
    public const decimal UsdMFuturesTakerFee = 0.0005m;

    /// <summary>
    /// And what the coin-margined family charges, which is NOT the same and is why these are named per family rather
    /// than shared between the two futures markets: this venue publishes a lower schedule for its coin-margined
    /// contracts than for its USD-margined ones.
    /// <para>
    /// From the venue's published schedule rather than from measurement, and that limit is worth stating: there is no
    /// public fee endpoint on either futures host, <c>/dapi/v1/commissionRate</c> is signed, and the fee page itself
    /// answers an automated request with nothing. So this is the documented figure for a first-tier account and an
    /// estimate for anyone else, which is all a default fee ever is.
    /// </para>
    /// </summary>
    public const decimal CoinMFuturesMakerFee = 0.00015m;

    /// <inheritdoc cref="CoinMFuturesMakerFee"/>
    public const decimal CoinMFuturesTakerFee = 0.0004m;

    /// <summary>What a spot pair charges before an instrument is loaded, from this venue's published spot schedule.</summary>
    public const decimal SpotMakerFee = 0.001m;

    /// <inheritdoc cref="SpotMakerFee"/>
    public const decimal SpotTakerFee = 0.001m;

    /// <summary>
    /// This venue's leverage is a whole number: its own documentation gives the field as an integer from 1 to 125 on
    /// both futures families. Named here so the refusal below quotes the venue rather than an assumption.
    /// <para>
    /// Documented rather than measured, on the coin-margined family as on the other: the venue checks the API key
    /// before it looks at a parameter, so an unauthenticated call cannot be made to reject a fraction and say so.
    /// The refusal is kept because its direction is the safe one - a refused fraction is a sentence somebody reads,
    /// where a rounded one changes the size of every position and appears nowhere.
    /// </para>
    /// </summary>
    public const bool LeverageIsWholeNumber = true;

    /// <summary>
    /// What this venue says when asked about a symbol it does not list: <c>-1121</c> "Invalid symbol". It arrives as
    /// an error body rather than an envelope, so it is read out of the body of the HTTP failure. Asking about an
    /// instrument that is not listed leaves the provider empty rather than throwing, as on every venue.
    /// <para>
    /// Only spot ever says it about a catalog request. Measured 2026-09-25: both futures families ignore the
    /// <c>symbol</c> filter on <c>exchangeInfo</c> and answer HTTP 200 with their whole contract list - 907 on the
    /// USD-margined host, 30 on the coin-margined one, and the same for a symbol that exists, a symbol that does not,
    /// and no symbol at all. The code is still this venue's answer on the endpoints that DO honour a symbol, which is
    /// why it is read from a failure body rather than only from the catalog.
    /// </para>
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

    /// <summary>
    /// Where the coin-margined family answers. A host of its own, verified rather than derived from the USD-margined
    /// one: the two share a naming pattern, and a pattern is not a fact about where a venue listens.
    /// </summary>
    public const string CoinMFuturesHttpBase = "https://dapi.binance.com";

    /// <inheritdoc cref="CoinMFuturesHttpBase"/>
    public const string CoinMFuturesWsBase = "wss://dstream.binance.com";

    public static string HttpBase(IBinanceSettings s) => s.BaseUrlHttp ?? s.AccountType switch
    {
        BinanceAccountType.Spot => SpotHttpBase,
        BinanceAccountType.UsdMFutures => UsdMFuturesHttpBase,
        BinanceAccountType.CoinMFutures => CoinMFuturesHttpBase,
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };

    public static string WsBase(IBinanceSettings s) => s.BaseUrlWs ?? s.AccountType switch
    {
        BinanceAccountType.Spot => SpotWsBase,
        BinanceAccountType.UsdMFutures => UsdMFuturesWsBase,
        BinanceAccountType.CoinMFutures => CoinMFuturesWsBase,
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };

    public static string ApiPrefix(BinanceAccountType type) => type switch
    {
        BinanceAccountType.Spot => "/api/v3",
        BinanceAccountType.UsdMFutures => "/fapi/v1",
        BinanceAccountType.CoinMFutures => "/dapi/v1",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "no API prefix for this account type"),
    };

    /// <summary>The most candles one spot klines request answers with.</summary>
    public const int SpotKlinePage = 1000;

    /// <summary>
    /// And the most either futures family answers with, measured on both: 1,500 rows came back for a request asking
    /// for 1,500 on <c>/dapi/v1/klines</c>, and 1,501 was refused outright with -1130 rather than clamped. A cap that
    /// is refused rather than clamped is the useful kind - a paging loop asking for one row too many would fail
    /// loudly instead of believing it had reached the end of the venue's history.
    /// </summary>
    public const int FuturesKlinePage = 1500;

    /// <summary>
    /// The most candles the venue returns for one klines request. A fetch asks for no more than this per request and
    /// reads the answer against what it asked for, so a page that comes back short means the venue has no more rather
    /// than that a literal happened to match.
    /// </summary>
    public static int KlinePage(BinanceAccountType type) => type == BinanceAccountType.Spot ? SpotKlinePage : FuturesKlinePage;

    /// <summary>The most trades the venue returns for one trades or aggTrades request.</summary>
    public const int TradePage = 1000;

    /// <summary>
    /// How many funding rates one page of the venue's <c>fundingRate</c> read holds. Measured on 2026-09-23 on the
    /// USD-margined host and on 2026-09-25 on the coin-margined one: both accept a limit of 1,000, both refuse
    /// 1,001, and a page short of this is how a fetch knows it has reached the end.
    /// <para>
    /// Worth naming rather than leaving to the venue's default, because the two families do not share one: asked
    /// with no limit at all, the USD-margined host answers 100 rows and the coin-margined host 500. A loop that
    /// omitted the limit and treated a short page as the end would silently stop after 500 settlements here.
    /// </para>
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
    /// <para>
    /// Both futures families state a larger allowance in their own <c>exchangeInfo</c> - 2,400 a minute, measured on
    /// each on 2026-09-25, and 6,000 on the coin-margined test network. This budget is deliberately not raised to
    /// meet it: spending less than a venue allows costs a slower catalog fetch, and spending more than it allows
    /// costs the key a ban rather than an error, so one budget at the tightest family's figure is the safe shape.
    /// </para>
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
    /// Maps a raw venue symbol to an instrument id. USD-margined perpetuals get a -PERP suffix; a dated contract
    /// carries its delivery date after an underscore (BTCUSDT_250926) and keeps its own name, whatever contract type
    /// the caller assumed.
    /// <para>
    /// A coin-margined symbol is kept exactly as the venue spells it, and that is a decision rather than an omission.
    /// Measured on 2026-09-25, this family names its perpetuals BTCUSD_PERP and its dated contracts BTCUSD_261225 -
    /// the venue has already marked which is which, with an underscore where the engine's convention uses a dash. Two
    /// markings of one fact would have to be undone by <see cref="ToRawSymbol"/>, which is handed an id and not an
    /// account type: it could only rebuild "_PERP" by guessing from the spelling of the id it was given, which is
    /// the guess this adapter is not allowed to make and the one a wrong guess silently sends an order on.
    /// </para>
    /// </summary>
    public static InstrumentId ToInstrumentId(string rawSymbol, BinanceAccountType type, string contractType = PerpetualContractType)
    {
        bool perpetual = type == BinanceAccountType.UsdMFutures
            && contractType == PerpetualContractType
            && !rawSymbol.Contains('_', StringComparison.Ordinal);
        string symbol = perpetual ? rawSymbol + PerpSuffix : rawSymbol;
        return new InstrumentId(new Symbol(symbol), Venue);
    }

    /// <summary>Maps an instrument id back to the raw venue symbol.</summary>
    public static string ToRawSymbol(InstrumentId id)
    {
        string s = id.Symbol.Value;
        return s.EndsWith(PerpSuffix, StringComparison.Ordinal) ? s.Substring(0, s.Length - PerpSuffix.Length) : s;
    }

    /// <summary>
    /// What this venue calls a perpetual in its own <c>contractType</c> field. The instrument class is taken from
    /// that field and never from how a symbol is spelled: both futures families publish it, and the coin-margined
    /// one is the reason it matters - its dated contracts are CURRENT_QUARTER and NEXT_QUARTER, and a contract in
    /// delivery answers the compound "CURRENT_QUARTER DELIVERING", none of which a name would have revealed.
    /// </summary>
    public const string PerpetualContractType = "PERPETUAL";

    /// <summary>
    /// Where each futures family publishes whether a contract can be traded, and they do not agree. Measured
    /// 2026-09-25: every one of the USD-margined family's 907 symbols carries <c>status</c> and none carries
    /// <c>contractStatus</c>, and every one of the coin-margined family's 30 carries <c>contractStatus</c> and none
    /// carries <c>status</c>.
    /// <para>
    /// Both are read, because reading only the first meant every coin-margined contract fell back to the default of
    /// "tradable" - including the 13 PENDING_TRADING and 8 DELIVERING ones the venue's test network lists, which
    /// would have been offered in a picker and refused on the first order.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> StatusFields = ["status", "contractStatus"];

    /// <summary>The one value of <see cref="StatusFields"/> that means a contract can be traded now.</summary>
    public const string StatusTrading = "TRADING";

    /// <summary>
    /// What one coin-margined contract is worth, in the quote currency, as this venue publishes it per symbol:
    /// measured 2026-09-25 as 100 USD on every BTCUSD contract and 10 USD on the other 27. The USD-margined family
    /// publishes no such field at all - its quantities are in base units - so this is read where it exists and the
    /// instrument's multiplier is left at one where it does not.
    /// </summary>
    public const string ContractSizeField = "contractSize";

    /// <summary>Which asset a futures contract is margined and settled in; the base coin on the coin-margined family.</summary>
    public const string MarginAssetField = "marginAsset";

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
