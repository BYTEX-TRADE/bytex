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

namespace Bytex.Adapters.Bybit;

public enum BybitProductType
{
    Spot = 1,

    /// <summary>USDT/USDC-margined perpetuals and futures.</summary>
    Linear = 2,

    /// <summary>
    /// Coin-margined perpetuals and dated futures: quoted in USD, sized in USD contracts and settled in the base
    /// coin, so every money figure comes out in the base currency and goes through one over the price.
    /// </summary>
    Inverse = 3,

    /// <summary>
    /// European options on this venue's own underlyings, settled in USDT. Unlike the three families beside it this
    /// one is charged no funding, has no candles and publishes neither a margin nor a leverage ceiling.
    /// </summary>
    Option = 4,
}

public interface IBybitSettings
{
    string? ApiKey { get; }

    string? ApiSecret { get; }

    BybitProductType ProductType { get; }


    string? BaseUrlHttp { get; }

    string? BaseUrlWs { get; }
}

public sealed record BybitDataClientConfig : DataClientConfig, IBybitSettings
{
    public string? ApiKey { get; init; }

    public string? ApiSecret { get; init; }

    public BybitProductType ProductType { get; init; } = BybitProductType.Spot;


    public string? BaseUrlHttp { get; init; }

    public string? BaseUrlWs { get; init; }
}

public sealed record BybitExecutionClientConfig : ExecutionClientConfig, IBybitSettings
{
    public string? ApiKey { get; init; }

    public string? ApiSecret { get; init; }

    public BybitProductType ProductType { get; init; } = BybitProductType.Spot;


    public string? BaseUrlHttp { get; init; }

    public string? BaseUrlWs { get; init; }

    public int RecvWindowMs { get; init; } = BybitVenue.DefaultRecvWindowMs;

    public TriggerType DefaultTriggerType { get; init; } = TriggerType.LastPrice;
}

public static class BybitVenue
{
    /// <summary>
    /// The most candles the venue returns for one kline request. A fetch asks for no more than this per request and
    /// reads the answer against what it asked for, so a short page means the venue has no more.
    /// </summary>
    public const int KlinePage = 1000;

    /// <summary>The most trades the venue returns for one recent-trade request.</summary>
    public const int TradePage = 1000;

    /// <summary>
    /// How many contracts one page of /v5/market/instruments-info holds. The venue's own maximum, measured on
    /// 2026-09-26: asking for 1001 is refused with "Parameter verification failed for 'limit'".
    /// </summary>
    public const int InstrumentPage = 1000;

    /// <summary>
    /// The filter a host sets to choose which option underlyings are listed, as a comma-separated list of coins.
    /// <para>
    /// It exists because this venue's option catalog cannot be listed whole. Measured on 2026-09-26: asked for
    /// <c>category=option</c> with no <c>baseCoin</c>, the venue answers 730 BTC contracts and an empty cursor -
    /// not a page of everything, but the whole of ONE underlying - while the same request per coin gives 730 BTC,
    /// 610 ETH, 368 SOL and 326 XRP. There is no public read that enumerates which coins have options, so a host
    /// that wants an underlying other than the venue's default has to name it, and this is where.
    /// </para>
    /// </summary>
    public const string OptionBaseCoinFilter = "baseCoin";

    /// <summary>
    /// How many funding rates one page of /v5/market/funding/history holds. The venue's own maximum, measured on
    /// 2026-09-23: asking for more is refused rather than served.
    /// </summary>
    public const int FundingPage = 200;

    /// <summary>
    /// Requests the venue allows in <see cref="RequestWindow"/>. Bybit counts requests rather than weights, per key
    /// and per endpoint group; this budget is the conservative one that holds for all of them.
    /// </summary>
    public const int RequestsPerWindow = 100;

    /// <summary>
    /// How long a signed request stays valid, in milliseconds. The venue refuses one whose timestamp is older than
    /// this, and a value much larger than a few seconds widens the window an intercepted request could be replayed in.
    /// </summary>
    public const int DefaultRecvWindowMs = 5000;

    /// <summary>The window <see cref="RequestsPerWindow"/> is counted over.</summary>
    public static readonly TimeSpan RequestWindow = TimeSpan.FromSeconds(5);

    /// <summary>The orderbook depth that IS the top of book, on the three families whose socket serves it.</summary>
    public const int TopOfBookDepth = 1;

    /// <summary>The book depths the venue publishes on its orderbook topics; a subscription is served by the nearest.</summary>
    public const int SmallBookDepth = 50;

    /// <summary>The book depths the venue publishes on its orderbook topics; a subscription is served by the nearest.</summary>
    public const int LargeBookDepth = 200;

    /// <summary>
    /// The book depths the OPTION socket publishes, which are not the ones the other three families publish.
    /// Measured on 2026-09-26: subscribing an option to orderbook.25 and orderbook.100 delivers snapshots, and
    /// subscribing the same contract to orderbook.1 and orderbook.50 delivers NOTHING - while the venue's
    /// subscription reply lists all four under <c>successTopics</c> and refuses none of them. A client that reused
    /// the depths above would therefore connect, be told it had subscribed, and receive no book at all.
    /// </summary>
    public const int SmallOptionBookDepth = 25;

    /// <inheritdoc cref="SmallOptionBookDepth"/>
    public const int LargeOptionBookDepth = 100;

    /// <summary>
    /// What the venue charges on an inverse contract, maker and taker, as its published standard schedule states
    /// them. They are the venue's figures for the family rather than for a symbol, in the same way the spot and
    /// linear defaults above are, and an instrument's own rates replace them the moment a key can read
    /// <c>/v5/account/fee-rate</c> - which is the reason these could not be measured: that endpoint needs one.
    /// <para>
    /// A fee on an inverse contract is charged in the BASE coin, because that is the currency its notional is in.
    /// Nothing here has to arrange that: the engine takes a commission out of <c>NotionalValue</c>, which inverts
    /// for an inverse instrument on its own.
    /// </para>
    /// </summary>
    public const decimal InverseMakerFee = 0.0001m;

    /// <inheritdoc cref="InverseMakerFee"/>
    public const decimal InverseTakerFee = 0.0006m;

    /// <summary>
    /// What the venue charges on an option, maker and taker, from its published standard schedule. These are the
    /// venue's own rates and the declaration states them; they are NOT what an instrument is charged at - see
    /// <see cref="OptionFeeCapOfPremium"/> for why, which is the most important comment in this file.
    /// </summary>
    public const decimal OptionMakerFee = 0.0002m;

    /// <inheritdoc cref="OptionMakerFee"/>
    public const decimal OptionTakerFee = 0.0003m;

    /// <summary>
    /// The venue's own ceiling on an option's fee, as a share of the premium, and what this engine charges an option
    /// instrument at.
    /// <para>
    /// The venue charges <c>min(feeRate x INDEX price of the underlying, 7% x premium) x size</c>. This engine
    /// prices a commission as a fraction of the TRADED notional, and an option's traded notional is its premium -
    /// so the rate applied the engine's way charges against the wrong number entirely and comes out far too small.
    /// The cap is the only term of that formula the engine can express exactly, because it IS a fraction of the
    /// premium.
    /// </para>
    /// <para>
    /// **It is an upper bound and a loose one.** Against the venue's own worked example - index 42,000, premium
    /// 3,000, size 0.3 - the venue charges 2.52 and this charges 63, about twenty-five times more, because the index
    /// term binds almost always and the cap is a remote ceiling. That is the deliberate direction: an option
    /// backtest reads worse than reality rather than better, and a strategy discarded for being unprofitable is a
    /// cheaper mistake than one traded because its costs were understated.
    /// </para>
    /// <para>
    /// Charging what the venue really charges needs an index price series, and the engine has nowhere to keep one:
    /// nothing persists an index, the simulator never receives one, and this venue does not serve historical index
    /// prices for the option category at all - only for linear, under a symbol this adapter would have to assume is
    /// the same index. That assumption is the reason this is a bound rather than a number.
    /// </para>
    /// </summary>
    public const decimal OptionFeeCapOfPremium = 0.07m;

    /// <summary>
    /// The coin every inverse contract on this venue is quoted in. Measured on 2026-09-26: all 26 contracts of the
    /// inverse category carry <c>quoteCoin: USD</c>, the 22 perpetuals are named <c>&lt;base&gt;USD</c>, and the 4
    /// dated contracts carry a delivery code after it - <c>BTCUSDZ26</c>, <c>BTCUSDH27</c>.
    /// <para>
    /// That is what makes the id spelling decidable both ways: a perpetual's symbol ENDS where its quote coin does
    /// and a dated contract's does not, so one string rule names every contract the venue lists and strips back to
    /// the venue's own symbol. It decides the NAME only. Which class a contract is comes from the venue's own
    /// <c>contractType</c> field and never from this - the venue's dated inverse symbols carry no dash, so the
    /// rule the linear family uses would have called all four of them perpetuals.
    /// </para>
    /// </summary>
    public const string InverseQuoteCoin = "USD";

    /// <summary>How long a websocket authentication signature stays valid; the venue refuses one that has expired.</summary>
    public static readonly TimeSpan AuthExpiry = TimeSpan.FromSeconds(10);

    /// <summary>How often to ping a stream; the venue drops a socket that has been silent for twice this.</summary>
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The execution types that are a trade. The venue's execution topic carries more than trades - a funding payment,
    /// a delivery, a settlement and a position transfer all arrive there, each with a quantity - so a reader that
    /// takes every row for a fill books a phantom fill every funding interval and drifts away from the real position.
    /// A type that is not in this set is not a fill; one the venue adds later is ignored until it is listed here,
    /// which loses a fill that reconciliation then finds, rather than inventing one that nothing corrects.
    /// </summary>
    private static readonly HashSet<string> TradeExecutionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Trade",
        "AdlTrade",
        "BustTrade",
        "BlockTrade",
    };

    /// <summary>Whether an execution-topic row describes a trade. A row with no type at all is one, as spot sends.</summary>
    public static bool IsTradeExecution(string execType) => execType.Length == 0 || TradeExecutionTypes.Contains(execType);

    public static readonly Venue Venue = new("BYBIT");

    public const string EnvApiKey = "BYBIT_API_KEY";
    public const string EnvApiSecret = "BYBIT_API_SECRET";
    public const string PerpSuffix = "-PERP";

    /// <summary>
    /// The venue's parameter-error code. On a request that carries exactly one symbol - which is what loading a
    /// single instrument is - the only parameter that can be wrong is that symbol, so in that one place this means
    /// "not listed". It is NOT treated that way anywhere that sends more than a symbol, because there it could be
    /// any parameter.
    /// <para>
    /// This venue's families disagree about it: spot answers an unknown symbol with success and an empty list,
    /// while linear and inverse refuse with this code and "params error: symbol invalid" and option refuses with
    /// this code and "Parameter verification failed for 'symbol'." - one code, three sentences, all measured live.
    /// The code is what is read, so the wording is the venue's business.
    /// </para>
    /// </summary>
    public const int ErrorParamsInvalid = 10001;

    /// <summary>
    /// Where this venue publishes the margin it requires per symbol, by risk tier. Public, no key - for the two
    /// contract families. The option category is refused outright there (see <see cref="PublishesRiskLimits"/>).
    /// </summary>
    public const string RiskLimitPath = "/v5/market/risk-limit";

    /// <summary>
    /// The tier a position starts in, as this venue marks it. Risk tiers raise the margin as a position grows, and
    /// the instrument's own margin is the requirement at the smallest size - everything above it is the venue
    /// charging more, which is the venue's business and not a property of the instrument.
    /// </summary>
    public const int LowestRiskTier = 1;

    /// <summary>How many order rows to ask for per page of an order-status read.</summary>
    public const int OrderReportPage = 50;

    /// <summary>How many execution rows to ask for per page of a fill read.</summary>
    public const int FillReportPage = 100;

    /// <summary>
    /// The coin a whole-market read is asked for on the linear family when this client holds no instruments to
    /// take the answer from. That family settles in USDT and in USDC and the venue takes one settle coin per
    /// request, so a client holding nothing asks for the larger of the two markets rather than for neither.
    /// </summary>
    public const string LinearSettleCoin = "USDT";

    /// <summary>
    /// How many risk-limit rows to ask for at once. The rows are tiers rather than symbols - a single page of a
    /// thousand covered fifteen symbols when this was measured - so the walk is over tiers and the cursor decides
    /// when it ends.
    /// </summary>
    public const int RiskLimitPage = 1000;

    /// <summary>
    /// The header a broker id travels in on this venue. It is not part of the signed payload, so carrying one leaves
    /// the request this venue authenticates byte-for-byte unchanged - which is why this venue's mechanism costs an
    /// order nothing, where Binance's changes the order's own identity.
    /// </summary>
    public const string BrokerIdHeader = "X-Referer";

    /// <summary>
    /// Where a leverage is set on this venue. As on Binance it is account state per symbol rather than a field on an
    /// order, so setting it before trading is the only way a strategy is traded at the leverage it was written for.
    /// </summary>
    public const string LeveragePath = "/v5/position/set-leverage";

    /// <summary>
    /// What this venue answers when asked to set the leverage already in force. It is not a failure - the account is
    /// exactly where it was asked to be - so it is not reported as one.
    /// </summary>
    public const int ErrorLeverageUnchanged = 110043;

    // One host and one socket root for every product: what changes with the product is the path, which is this
    // adapter's business. The plugin declares these two, so a host writing them back into a configuration changes
    // nothing - which is what makes them worth declaring at all.
    public const string DefaultHttpBase = "https://api.bybit.com";
    public const string DefaultWsBase = "wss://stream.bybit.com";

    public static string HttpBase(IBybitSettings s) => s.BaseUrlHttp ?? DefaultHttpBase;

    public static string WsPublic(IBybitSettings s) => (s.BaseUrlWs ?? DefaultWsBase) + "/v5/public/" + Category(s.ProductType);

    public static string WsPrivate(IBybitSettings s) => (s.BaseUrlWs ?? DefaultWsBase) + "/v5/private";

    public static string Category(BybitProductType type) => type switch
    {
        BybitProductType.Spot => "spot",
        BybitProductType.Linear => "linear",
        BybitProductType.Inverse => "inverse",
        BybitProductType.Option => "option",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Bybit has no such product family."),
    };

    /// <summary>
    /// Whether positions in this family are charged funding. Measured on 2026-09-26: the two contract families are,
    /// and <c>/v5/market/funding/history</c> refuses <c>category=option</c> with "Illegal category" - so an option
    /// is not funded on this venue at all, rather than funded at a rate nothing fetches.
    /// </summary>
    public static bool PaysFunding(BybitProductType type) => type is BybitProductType.Linear or BybitProductType.Inverse;

    /// <summary>
    /// Whether this venue publishes candles for the family. Measured on 2026-09-26: <c>/v5/market/kline</c> refuses
    /// <c>category=option</c> with "params error: Category is invalid", and the option socket accepts a kline
    /// subscription, reports it under <c>successTopics</c> and delivers nothing for it. So an option has no bar
    /// history here by any route, which is a fact about the venue and not a gap in this adapter.
    /// </summary>
    public static bool HasCandles(BybitProductType type) => type != BybitProductType.Option;

    /// <summary>
    /// Whether the venue publishes what it requires per symbol for the family. Measured on 2026-09-26: the two
    /// contract families answer <see cref="RiskLimitPath"/>, spot borrows nothing so its margin is none rather than
    /// unpublished, and the option category is refused there with "Illegal category" while carrying no
    /// <c>leverageFilter</c> in its contract data either. So an option's margin is not published anywhere public.
    /// </summary>
    public static bool PublishesRiskLimits(BybitProductType type) => type is BybitProductType.Linear or BybitProductType.Inverse;

    /// <summary>
    /// Whether a leverage can be set for the family. The two contract families hold it as account state per symbol;
    /// spot borrows nothing, and the venue's option margin is a portfolio calculation with no per-symbol leverage
    /// to set and no ceiling published to check one against.
    /// </summary>
    public static bool AppliesLeverage(BybitProductType type) => type is BybitProductType.Linear or BybitProductType.Inverse;

    /// <summary>
    /// Maps a raw venue symbol to an instrument id, in both directions with <see cref="ToRawSymbol"/>.
    /// <para>
    /// Linear perpetuals get a -PERP suffix and a dated linear contract carries its delivery date after a dash
    /// (BTCUSDT-26SEP25), so the dash is what tells them apart there. Inverse contracts cannot use that rule -
    /// their dated contracts are spelled BTCUSDZ26, with no dash anywhere - so the suffix goes on the symbols that
    /// end where their quote coin does, which is every inverse perpetual and none of the dated ones. Spot and
    /// option ids are the venue's own symbol untouched.
    /// </para>
    /// <para>
    /// The suffix is not decoration on this venue. Spot lists BTCUSD and ETHUSD, both trading, and the inverse
    /// family lists perpetuals of exactly those names - so without it one id would mean two different instruments
    /// on one venue, and the family resolver would answer every one of them with the spot pair.
    /// </para>
    /// </summary>
    public static InstrumentId ToInstrumentId(string rawSymbol, BybitProductType type) =>
        new(new Symbol(IsPerpetualSpelling(rawSymbol, type) ? rawSymbol + PerpSuffix : rawSymbol), Venue);

    /// <summary>Whether the id for this raw symbol carries the perpetual suffix. See <see cref="ToInstrumentId"/>.</summary>
    private static bool IsPerpetualSpelling(string rawSymbol, BybitProductType type) => type switch
    {
        BybitProductType.Linear => !rawSymbol.Contains('-', StringComparison.Ordinal),
        BybitProductType.Inverse => rawSymbol.EndsWith(InverseQuoteCoin, StringComparison.Ordinal),
        _ => false,
    };

    public static string ToRawSymbol(InstrumentId id)
    {
        string s = id.Symbol.Value;
        return s.EndsWith(PerpSuffix, StringComparison.Ordinal) ? s.Substring(0, s.Length - PerpSuffix.Length) : s;
    }

    public static string Interval(BarSpecification spec) => (spec.Aggregation, spec.Step) switch
    {
        (BarAggregation.Minute, 1) => "1",
        (BarAggregation.Minute, 3) => "3",
        (BarAggregation.Minute, 5) => "5",
        (BarAggregation.Minute, 15) => "15",
        (BarAggregation.Minute, 30) => "30",
        (BarAggregation.Hour, 1) => "60",
        (BarAggregation.Hour, 2) => "120",
        (BarAggregation.Hour, 4) => "240",
        (BarAggregation.Hour, 6) => "360",
        (BarAggregation.Hour, 12) => "720",
        (BarAggregation.Day, 1) => "D",
        (BarAggregation.Week, 1) => "W",
        (BarAggregation.Month, 1) => "M",
        _ => throw new NotSupportedException($"Bybit does not support bar specification {spec}."),
    };

    public static (string Key, string Secret) Credentials(IBybitSettings s) =>
        (Secrets.Require(s.ApiKey, EnvApiKey), Secrets.Require(s.ApiSecret, EnvApiSecret));
}

/// <summary>
/// Bybit V5 REST access with request signing.
/// </summary>
public sealed class BybitHttp : IDisposable
{
    private readonly HttpClientWrapper _http;
    private readonly string? _apiKey;
    private readonly string? _apiSecret;
    private readonly int _recvWindow;
    private readonly string? _brokerId;

    public BybitHttp(IBybitSettings settings, ILogger? logger = null, bool requireCredentials = false, int recvWindowMs = BybitVenue.DefaultRecvWindowMs, string? brokerId = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ProductType = settings.ProductType;
        _recvWindow = recvWindowMs;

        // Only an execution client has one: there is nothing to attribute about reading a public order book.
        _brokerId = brokerId;
        if (requireCredentials)
        {
            (_apiKey, _apiSecret) = BybitVenue.Credentials(settings);
        }
        else
        {
            _apiKey = Secrets.Optional(settings.ApiKey, BybitVenue.EnvApiKey);
            _apiSecret = Secrets.Optional(settings.ApiSecret, BybitVenue.EnvApiSecret);
        }

        _http = new HttpClientWrapper(new Uri(BybitVenue.HttpBase(settings)), new RateLimiter(BybitVenue.RequestsPerWindow, BybitVenue.RequestWindow), new RetryPolicy(), logger);
    }

    public BybitProductType ProductType { get; }

    public string Category => BybitVenue.Category(ProductType);

    public bool HasCredentials => _apiKey is not null && _apiSecret is not null;

    public async Task<JsonElement> GetPublicAsync(string path, IReadOnlyDictionary<string, string>? query = null, CancellationToken ct = default)
    {
        string text = await _http.GetAsync(path, query, null, 1, ct).ConfigureAwait(false);
        return Unwrap(text);
    }

    public async Task<JsonElement> GetSignedAsync(string path, IReadOnlyDictionary<string, string>? query = null, CancellationToken ct = default)
    {
        string queryString = query is { Count: > 0 } ? HttpClientWrapper.BuildQuery(query) : string.Empty;
        string text = await _http.SendAsync(HttpMethod.Get, path, query, null, Sign(queryString), 1, ct).ConfigureAwait(false);
        return Unwrap(text);
    }

    public async Task<JsonElement> PostSignedAsync(string path, object body, CancellationToken ct = default)
    {
        string json = JsonSerializer.Serialize(body);
        StringContent content = new(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        Dictionary<string, string> headers = Sign(json);
        if (_brokerId is { Length: > 0 } broker)
        {
            // Orders are placed with a signed POST here, and the id rides beside the signature rather than inside
            // it. Untagged, these headers are exactly what they were before the field existed.
            headers[BybitVenue.BrokerIdHeader] = broker;
        }

        string text = await _http.SendAsync(HttpMethod.Post, path, null, content, headers, 1, ct).ConfigureAwait(false);
        return Unwrap(text);
    }

    private Dictionary<string, string> Sign(string payload)
    {
        if (_apiKey is null || _apiSecret is null)
        {
            throw new InvalidOperationException("Bybit API credentials are required for this request.");
        }

        string timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        string recv = _recvWindow.ToString(CultureInfo.InvariantCulture);
        string signature = HmacSigner.Sha256Hex(_apiSecret, timestamp + _apiKey + recv + payload);
        return new Dictionary<string, string>
        {
            ["X-BAPI-API-KEY"] = _apiKey,
            ["X-BAPI-TIMESTAMP"] = timestamp,
            ["X-BAPI-RECV-WINDOW"] = recv,
            ["X-BAPI-SIGN"] = signature,
        };
    }

    /// <summary>Validates the V5 envelope and returns the <c>result</c> element.</summary>
    private static JsonElement Unwrap(string text)
    {
        JsonDocument doc = JsonDocument.Parse(text);
        JsonElement root = doc.RootElement;
        int code = root.TryGetProperty("retCode", out JsonElement rc) ? rc.GetInt32() : 0;
        if (code != 0)
        {
            string message = root.TryGetProperty("retMsg", out JsonElement rm) ? rm.GetString() ?? "error" : "error";
            throw new BybitApiException(code, message);
        }

        return root.TryGetProperty("result", out JsonElement result) ? result.Clone() : root.Clone();
    }

    public void Dispose() => _http.Dispose();
}

public sealed class BybitApiException : Exception
{
    public BybitApiException(int code, string message)
        : base($"Bybit error {code}: {message}")
    {
        Code = code;
        RetMsg = message;
    }

    public int Code { get; }

    public string RetMsg { get; }
}

internal static class Json
{
    public static decimal Dec(this JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out JsonElement p))
        {
            return 0m;
        }

        return p.DecValue();
    }

    public static decimal DecValue(this JsonElement p) => p.ValueKind switch
    {
        JsonValueKind.Number => p.GetDecimal(),
        JsonValueKind.String => decimal.TryParse(p.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal d) ? d : 0m,
        _ => 0m,
    };

    public static string Str(this JsonElement e, string name) => e.TryGetProperty(name, out JsonElement p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? string.Empty : string.Empty;

    public static long Long(this JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out JsonElement p))
        {
            return 0;
        }

        return p.ValueKind == JsonValueKind.Number ? p.GetInt64() : long.TryParse(p.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long l) ? l : 0;
    }

    public static bool Bool(this JsonElement e, string name) => e.TryGetProperty(name, out JsonElement p) && (p.ValueKind == JsonValueKind.True || (p.ValueKind == JsonValueKind.String && p.GetString() == "true"));

    public static bool Has(this JsonElement e, string name) => e.TryGetProperty(name, out JsonElement p) && p.ValueKind != JsonValueKind.Null;

    public static UnixNanos Ms(this JsonElement e, string name) => UnixNanos.FromMilliseconds(e.Long(name));

    public static string Fmt(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    public static byte Precision(decimal increment)
    {
        string text = increment.ToString(CultureInfo.InvariantCulture).TrimEnd('0');
        int dot = text.IndexOf('.');
        return (byte)(dot < 0 ? 0 : text.Length - dot - 1);
    }
}

/// <summary>
/// Loads instrument definitions from Bybit's instruments-info endpoint.
/// </summary>
public sealed class BybitInstrumentProvider : InstrumentProviderBase
{
    private readonly BybitHttp _http;
    private readonly BybitProductType _productType;

    public BybitInstrumentProvider(BybitHttp http, BybitProductType productType, InstrumentProviderConfig? config = null, ILogger? logger = null)
        : base(BybitVenue.Venue, config, logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _productType = productType;
    }

    public override async Task LoadAllAsync(CancellationToken ct, IReadOnlyDictionary<string, string>? filters = null)
    {
        // Once for the whole category, before any instrument is built: what this venue requires is a fact about each
        // symbol and asking per symbol would be one request per contract.
        Dictionary<string, (decimal Initial, decimal Maintenance)> riskLimits = await RiskLimitsAsync(null, ct).ConfigureAwait(false);
        int loaded = 0;
        foreach (string? underlying in Underlyings(filters))
        {
            string? cursor = null;
            do
            {
                Dictionary<string, string> query = new()
                {
                    ["category"] = _http.Category,
                    ["limit"] = BybitVenue.InstrumentPage.ToString(CultureInfo.InvariantCulture),
                };

                if (underlying is not null)
                {
                    query[BybitVenue.OptionBaseCoinFilter] = underlying;
                }

                if (cursor is not null)
                {
                    query["cursor"] = cursor;
                }

                JsonElement result = await _http.GetPublicAsync("/v5/market/instruments-info", query, ct).ConfigureAwait(false);
                foreach (JsonElement item in result.GetProperty("list").EnumerateArray())
                {
                    Instrument? instrument = Parse(item, riskLimits);
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

                cursor = result.Str("nextPageCursor");
            }
            while (!string.IsNullOrEmpty(cursor));
        }

        Log.LogInformation("Loaded {Count} Bybit {Type} instruments", loaded, _productType);
    }

    /// <summary>
    /// The option underlyings to ask for, one request set each, or a single null meaning "ask with no underlying".
    /// <para>
    /// Only the option family has more than one, and only because this venue's option catalog cannot be listed
    /// whole - see <see cref="BybitVenue.OptionBaseCoinFilter"/>. Left unnamed, the venue answers with its own
    /// default underlying and an empty cursor, which is a complete answer about one coin and looks exactly like a
    /// complete answer about the category; a host is told so rather than left to infer it from the count.
    /// </para>
    /// </summary>
    private IEnumerable<string?> Underlyings(IReadOnlyDictionary<string, string>? filters)
    {
        if (_productType != BybitProductType.Option)
        {
            return [null];
        }

        if (filters is not null
            && filters.TryGetValue(BybitVenue.OptionBaseCoinFilter, out string? coins)
            && coins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } named)
        {
            return named;
        }

        Log.LogWarning(
            "No {Filter} was given for Bybit options, so the venue answers for its own default underlying only - not "
            + "for every option it lists. Name the underlyings to list them.",
            BybitVenue.OptionBaseCoinFilter);

        return [null];
    }

    public override async Task LoadAsync(InstrumentId id, CancellationToken ct)
    {
        string raw = BybitVenue.ToRawSymbol(id);
        Dictionary<string, string> query = new() { ["category"] = _http.Category, ["symbol"] = raw };
        JsonElement result;
        try
        {
            result = await _http.GetPublicAsync("/v5/market/instruments-info", query, ct).ConfigureAwait(false);
        }
        catch (BybitApiException e) when (e.Code == BybitVenue.ErrorParamsInvalid)
        {
            // The one parameter this request carries beyond the category is the symbol, so this is the venue
            // saying it does not list it. Linear refuses; spot answers with an empty list and never gets here.
            Log.LogInformation("Bybit does not list {Instrument}", id);
            return;
        }

        // What the venue requires here, asked for only once it has confirmed it lists the symbol. The family
        // resolver probes each family with exactly this call to find out which one owns an instrument, so a probe
        // that comes back empty must stay one request rather than two.
        JsonElement[] listed = [.. result.GetProperty("list").EnumerateArray()];
        Dictionary<string, (decimal Initial, decimal Maintenance)> riskLimits = listed.Length == 0
            ? []
            : await RiskLimitsAsync(raw, ct).ConfigureAwait(false);

        foreach (JsonElement item in listed)
        {
            // Only the instrument that was asked for, so "load one" means one on every venue whatever it returns.
            if (Parse(item, riskLimits) is { } instrument && instrument.Id == id)
            {
                Add(instrument);
            }
        }
    }

    /// <summary>
    /// What this venue requires per symbol, at the tier a position starts in (R4.12). Read rather than assumed: the
    /// adapter used to declare 0.05 initial and 0.025 maintenance for every contract, and this venue's own risk
    /// limits give 0.0066 and 0.0033 on BTCUSDT - the initial figure was 7.6 times the truth.
    /// <para>
    /// That was not cosmetic. <see cref="Instrument.InitialMarginRate"/> takes the LARGER of 1/leverage and the
    /// instrument's margin, so a wrong 0.05 was a floor: every leverage above 20x silently cost the margin of 20x,
    /// on a venue that grants 150x.
    /// </para>
    /// <para>
    /// One symbol asks for one symbol; a whole category walks the cursor, because a page of these is tiers and not
    /// symbols. Spot never calls this - nothing is borrowed there, so its margin is zero rather than unpublished -
    /// and neither does the option family, which the venue refuses this endpoint for outright.
    /// </para>
    /// </summary>
    private async Task<Dictionary<string, (decimal Initial, decimal Maintenance)>> RiskLimitsAsync(string? symbol, CancellationToken ct)
    {
        Dictionary<string, (decimal, decimal)> limits = new(StringComparer.Ordinal);
        if (!BybitVenue.PublishesRiskLimits(_productType))
        {
            return limits;
        }

        string? cursor = null;
        do
        {
            Dictionary<string, string> query = new(StringComparer.Ordinal)
            {
                ["category"] = _http.Category,
                ["limit"] = BybitVenue.RiskLimitPage.ToString(CultureInfo.InvariantCulture),
            };

            if (symbol is not null)
            {
                query["symbol"] = symbol;
            }

            if (cursor is { Length: > 0 })
            {
                query["cursor"] = cursor;
            }

            JsonElement result;
            try
            {
                result = await _http.GetPublicAsync(BybitVenue.RiskLimitPath, query, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // A venue that will not say what it requires leaves the instruments to their fallback below, which
                // is stated where it is used. Logged rather than thrown: an instrument list is still worth having.
                Log.LogWarning(e, "Bybit did not answer for its {Category} risk limits, so margin falls back to the ceiling its leverage implies", _http.Category);
                return limits;
            }

            foreach (JsonElement row in result.GetProperty("list").EnumerateArray())
            {
                if (row.Long("isLowestRisk") != BybitVenue.LowestRiskTier)
                {
                    continue;
                }

                limits[row.Str("symbol")] = (row.Dec("initialMargin"), row.Dec("maintenanceMargin"));
            }

            cursor = result.TryGetProperty("nextPageCursor", out JsonElement next) ? next.GetString() : null;
        }
        while (symbol is null && cursor is { Length: > 0 });

        return limits;
    }

    private Instrument? Parse(JsonElement item, IReadOnlyDictionary<string, (decimal Initial, decimal Maintenance)> riskLimits)
    {
        if (item.Str("status") != "Trading")
        {
            return null;
        }

        string raw = item.Str("symbol");
        JsonElement priceFilter = item.GetProperty("priceFilter");

        // The ceiling the venue grants here, and the margin it requires at the tier a position starts in. A symbol
        // the risk-limit walk did not cover falls back to what the ceiling implies, which is stated at its use.
        decimal maxLeverage = item.TryGetProperty("leverageFilter", out JsonElement leverageFilter) ? leverageFilter.Dec("maxLeverage") : 0m;
        decimal implied = maxLeverage > 0m ? 1m / maxLeverage : 0m;
        (decimal Initial, decimal Maintenance) margin = riskLimits.TryGetValue(raw, out (decimal Initial, decimal Maintenance) published)
            ? published
            : (implied, implied);
        JsonElement lotFilter = item.GetProperty("lotSizeFilter");
        decimal tickSize = priceFilter.Dec("tickSize");
        decimal step = _productType == BybitProductType.Spot ? lotFilter.Dec("basePrecision") : lotFilter.Dec("qtyStep");
        if (tickSize <= 0m || step <= 0m)
        {
            return null;
        }

        byte pricePrecision = Json.Precision(tickSize);
        byte sizePrecision = Json.Precision(step);
        Currency quote = Currency.FromCode(item.Str("quoteCoin"), 8);
        Currency baseCurrency = Currency.FromCode(item.Str("baseCoin"), 8);
        Currency settlement = _productType == BybitProductType.Spot ? quote : Currency.FromCode(item.Has("settleCoin") ? item.Str("settleCoin") : item.Str("quoteCoin"), 8);
        decimal minQty = lotFilter.Dec("minOrderQty");
        decimal maxQty = lotFilter.Dec("maxOrderQty");
        // Spot reports the minimum order value as minOrderAmt; the derivative categories call it minNotionalValue.
        decimal minNotional = _productType == BybitProductType.Spot ? lotFilter.Dec("minOrderAmt") : lotFilter.Dec("minNotionalValue");
        UnixNanos now = UnixNanos.FromDateTimeOffset(DateTimeOffset.UtcNow);
        (decimal Maker, decimal Taker) fees = InstrumentFees(_productType);

        InstrumentSpec spec = new()
        {
            Id = BybitVenue.ToInstrumentId(raw, _productType),
            RawSymbol = new Symbol(raw),
            AssetClass = AssetClass.Crypto,
            InstrumentClass = _productType switch
            {
                BybitProductType.Spot => InstrumentClass.Spot,
                BybitProductType.Option => InstrumentClass.Option,
                _ => InstrumentClass.Swap,
            },
            QuoteCurrency = quote,
            BaseCurrency = baseCurrency,
            SettlementCurrency = settlement,

            // The whole point of the inverse family: quoted in USD, sized in USD contracts and settled in the base
            // coin. Setting this is all the arithmetic needs - NotionalValue divides by the price and answers in the
            // base currency, and margin, commission and funding all follow it from there.
            IsInverse = _productType == BybitProductType.Inverse,
            PricePrecision = pricePrecision,
            SizePrecision = sizePrecision,
            PriceIncrement = new Price(tickSize, pricePrecision),
            SizeIncrement = new Quantity(step, sizePrecision),
            MinQuantity = minQty > 0m ? new Quantity(minQty, sizePrecision) : null,
            MaxQuantity = maxQty > 0m ? new Quantity(maxQty, sizePrecision) : null,
            MinNotional = minNotional > 0m ? new Money(minNotional, quote) : null,
            MinPrice = priceFilter.Dec("minPrice") > 0m ? new Price(priceFilter.Dec("minPrice"), pricePrecision) : null,
            MaxPrice = priceFilter.Dec("maxPrice") > 0m ? new Price(priceFilter.Dec("maxPrice"), pricePrecision) : null,
            MakerFee = fees.Maker,
            TakerFee = fees.Taker,
            // Read from the venue rather than assumed. This pair was 0.05 and 0.025 for every contract while the
            // venue's own risk limits give 0.0066 and 0.0033 on BTCUSDT - the initial figure was 7.6 times the
            // truth, and because InitialMarginRate takes the LARGER of 1/leverage and this number, that made it a
            // floor: every leverage above 20x silently cost the margin of 20x on a venue granting 150x.
            //
            // The fallback when the venue would not answer is the margin its own ceiling implies, 1/maxLeverage,
            // which is the least it can accept by definition rather than a number anybody chose. Maintenance falls
            // back to the same figure, which liquidates earlier than the venue would rather than later - the
            // survivable direction to be wrong in when the real number is unknown.
            //
            // Spot borrows nothing, so its margin is none. An OPTION's is none for a different reason and the
            // difference matters: this venue publishes no option margin at all - the risk-limit endpoint refuses
            // the category and the contract data carries no leverageFilter - so nothing is read, nothing is
            // invented, and the ceiling below stays null, which means "the venue did not say" rather than
            // "unlimited". A short option really is margined here, by a portfolio calculation this adapter cannot
            // see; that is recorded as a gap rather than covered by a figure somebody chose.
            MarginInit = BybitVenue.PublishesRiskLimits(_productType) ? margin.Initial : 0m,
            MarginMaint = BybitVenue.PublishesRiskLimits(_productType) ? margin.Maintenance : 0m,

            // The two zeroes above are not one answer, and this is where they stop looking like one. Spot's zero is
            // correct - nothing is borrowed. An option's zero is a gap: the product is margined here and the engine
            // holds no figure for it, so a position in one posts nothing and no liquidation can fire on the
            // requirement. A contract family reports what the venue said for that symbol, whether from the
            // risk-limit walk or from the ceiling it publishes beside the contract; a zero there means the venue
            // answered neither way.
            MarginSource = _productType switch
            {
                BybitProductType.Spot => MarginSource.NotMargined,
                BybitProductType.Option => MarginSource.VenueSilent,
                _ => margin.Initial > 0m ? MarginSource.VenuePerContract : MarginSource.VenueSilent,
            },

            // Free: the two contract families state their own ceiling per symbol in the response the instrument
            // came from. Spot and option carry no leverageFilter, so maxLeverage is zero and this stays null.
            MaxLeverage = maxLeverage <= 0m ? null : maxLeverage,
            TsEvent = now,
            TsInit = now,
        };

        if (_productType == BybitProductType.Spot)
        {
            return new CurrencyPair(spec);
        }

        if (_productType == BybitProductType.Option)
        {
            return ParseOption(item, spec, raw, baseCurrency, pricePrecision);
        }

        // From the venue's own field and never from the name. The inverse family is where that stops being a
        // principle and starts mattering: its dated contracts are spelled BTCUSDZ26, with nothing in the name to
        // separate them from a perpetual, and contractType says InverseFutures.
        string contractType = item.Str("contractType");
        if (contractType.Contains("Perpetual", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(contractType))
        {
            return new CryptoPerpetual(spec);
        }

        return new CryptoFuture(spec with { InstrumentClass = InstrumentClass.Future }, baseCurrency, item.Ms("launchTime"), item.Ms("deliveryTime"));
    }

    /// <summary>
    /// One option contract, with everything the venue publishes taken from its own fields: the underlying from
    /// <c>baseCoin</c>, call or put from <c>optionsType</c>, and the two dates from <c>launchTime</c> and
    /// <c>deliveryTime</c>.
    /// <para>
    /// The strike is the exception, and it is the venue's doing rather than a shortcut here. Measured on
    /// 2026-09-26 across all 2,034 option contracts the venue lists: the contract data carries thirteen fields and
    /// not one of them is the strike. The tickers read does not carry it either, and the option category is refused
    /// by every endpoint that might - risk-limit, funding history, kline. The only place the strike exists is the
    /// symbol, BTC-25JUN27-106000-P-USDT, so it is read from there.
    /// </para>
    /// <para>
    /// Reading a value out of a name is what this repository forbids for an instrument's CLASS, and that rule is
    /// kept: the class is Option because the category is, and call or put comes from the venue's own field. What
    /// the name is read for is the one number the venue does not publish, and it is checked against the venue
    /// while it is read - the first part against <c>baseCoin</c> and the letter against <c>optionsType</c>. A
    /// symbol that does not agree with both is skipped rather than guessed at, because a wrong strike on an option
    /// is not a rounding error: it is a different contract.
    /// </para>
    /// </summary>
    private Instrument? ParseOption(JsonElement item, InstrumentSpec spec, string raw, Currency underlying, byte pricePrecision)
    {
        string[] parts = raw.Split('-');
        if (parts.Length < OptionSymbolParts)
        {
            Log.LogWarning("Bybit option {Symbol} is not named the way this venue names options, so its strike cannot be read", raw);
            return null;
        }

        OptionKind? kind = item.Str("optionsType") switch
        {
            "Call" => OptionKind.Call,
            "Put" => OptionKind.Put,
            _ => null,
        };

        if (kind is not { } optionKind
            || !parts[0].Equals(underlying.Code, StringComparison.Ordinal)
            || !parts[OptionKindPart].Equals(optionKind == OptionKind.Call ? "C" : "P", StringComparison.Ordinal)
            || !decimal.TryParse(parts[OptionStrikePart], NumberStyles.Float, CultureInfo.InvariantCulture, out decimal strike)
            || strike <= 0m)
        {
            Log.LogWarning("Bybit option {Symbol} does not agree with the venue's own fields for it, so it is skipped rather than guessed at", raw);
            return null;
        }

        // A strike may be finer than the premium's tick - this venue quotes XRP options in ten-thousandths and
        // strikes them at 0.4 - so the strike carries whichever of the two needs more digits. Rounding it to the
        // tick would move the strike, and a strike is the contract rather than a price on it.
        byte strikePrecision = (byte)Math.Max(pricePrecision, Json.Precision(strike));
        return new OptionContract(
            spec,
            underlying.Code,
            optionKind,
            new Price(strike, strikePrecision),
            item.Ms("launchTime"),
            item.Ms("deliveryTime"));
    }

    /// <summary>
    /// What the venue charges this family, maker and taker, before an instrument has its own rates. Spot and
    /// linear are the figures this adapter has always carried; the other two are on
    /// <see cref="BybitVenue.InverseMakerFee"/> and <see cref="BybitVenue.OptionMakerFee"/>, which say where they
    /// came from and, for options, why the engine's arithmetic makes one of them a lower bound.
    /// </summary>
    /// <summary>
    /// What an INSTRUMENT of this family is charged at, which is the venue's rate everywhere except options.
    /// <para>
    /// An option is charged at <see cref="BybitVenue.OptionFeeCapOfPremium"/>, the venue's own ceiling, because the
    /// venue's rate applies to the index price and this engine multiplies the traded premium. The declaration still
    /// states the venue's published rates through <see cref="Fees"/>: what the venue charges and what this engine
    /// can charge are different facts, and a host asking the first must not be told the second.
    /// </para>
    /// </summary>
    internal static (decimal Maker, decimal Taker) InstrumentFees(BybitProductType type) => type == BybitProductType.Option
        ? (BybitVenue.OptionFeeCapOfPremium, BybitVenue.OptionFeeCapOfPremium)
        : Fees(type);

    internal static (decimal Maker, decimal Taker) Fees(BybitProductType type) => type switch
    {
        BybitProductType.Spot => (SpotMakerFee, SpotTakerFee),
        BybitProductType.Linear => (LinearMakerFee, LinearTakerFee),
        BybitProductType.Inverse => (BybitVenue.InverseMakerFee, BybitVenue.InverseTakerFee),
        BybitProductType.Option => (BybitVenue.OptionMakerFee, BybitVenue.OptionTakerFee),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Bybit has no such product family."),
    };

    /// <summary>What this venue charges on spot, from its published schedule.</summary>
    private const decimal SpotMakerFee = 0.001m;

    /// <inheritdoc cref="SpotMakerFee"/>
    private const decimal SpotTakerFee = 0.001m;

    /// <summary>What this venue charges on a linear contract, from its published schedule.</summary>
    private const decimal LinearMakerFee = 0.0002m;

    /// <inheritdoc cref="LinearMakerFee"/>
    private const decimal LinearTakerFee = 0.00055m;

    /// <summary>
    /// The fewest dash-separated parts a symbol must have to be one of this venue's options, and where the strike
    /// and the call/put letter sit in it. Measured on 2026-09-26: every one of the 2,034 listed options is spelled
    /// in five parts, <c>BTC-25JUN27-106000-P-USDT</c>, the fifth being the settlement coin. Four is the minimum
    /// rather than five because the last part is the only one this adapter reads nothing from, and a contract
    /// settled the way this venue's earlier options were - without that part - still carries its strike where
    /// these do. Nothing is inferred from the count beyond those two positions.
    /// </summary>
    private const int OptionSymbolParts = 4;

    /// <inheritdoc cref="OptionSymbolParts"/>
    private const int OptionStrikePart = 2;

    /// <inheritdoc cref="OptionSymbolParts"/>
    private const int OptionKindPart = 3;
}
