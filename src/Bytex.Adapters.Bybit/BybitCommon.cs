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

    /// <summary>The book depths the venue publishes on its orderbook topics; a subscription is served by the nearest.</summary>
    public const int SmallBookDepth = 50;

    /// <summary>The book depths the venue publishes on its orderbook topics; a subscription is served by the nearest.</summary>
    public const int LargeBookDepth = 200;

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
    /// This venue's two families disagree: spot answers an unknown symbol with success and an empty list, and
    /// linear refuses it with this code and "params error: symbol invalid". Both were measured live.
    /// </para>
    /// </summary>
    public const int ErrorParamsInvalid = 10001;

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

    public static string Category(BybitProductType type) => type == BybitProductType.Spot ? "spot" : "linear";

    /// <summary>
    /// Maps a raw venue symbol to an instrument id. Linear perpetuals get a -PERP suffix; a dated contract carries its
    /// delivery date after a dash (BTCUSDT-26SEP25) and keeps its own name.
    /// </summary>
    public static InstrumentId ToInstrumentId(string rawSymbol, BybitProductType type) =>
        new(new Symbol(type == BybitProductType.Linear && !rawSymbol.Contains('-', StringComparison.Ordinal) ? rawSymbol + PerpSuffix : rawSymbol), Venue);

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
        string? cursor = null;
        int loaded = 0;
        do
        {
            Dictionary<string, string> query = new() { ["category"] = _http.Category, ["limit"] = "1000" };
            if (cursor is not null)
            {
                query["cursor"] = cursor;
            }

            JsonElement result = await _http.GetPublicAsync("/v5/market/instruments-info", query, ct).ConfigureAwait(false);
            foreach (JsonElement item in result.GetProperty("list").EnumerateArray())
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

            cursor = result.Str("nextPageCursor");
        }
        while (!string.IsNullOrEmpty(cursor));

        Log.LogInformation("Loaded {Count} Bybit {Type} instruments", loaded, _productType);
    }

    public override async Task LoadAsync(InstrumentId id, CancellationToken ct)
    {
        Dictionary<string, string> query = new() { ["category"] = _http.Category, ["symbol"] = BybitVenue.ToRawSymbol(id) };
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

        foreach (JsonElement item in result.GetProperty("list").EnumerateArray())
        {
            // Only the instrument that was asked for, so "load one" means one on every venue whatever it returns.
            if (Parse(item) is { } instrument && instrument.Id == id)
            {
                Add(instrument);
            }
        }
    }

    private Instrument? Parse(JsonElement item)
    {
        if (item.Str("status") != "Trading")
        {
            return null;
        }

        string raw = item.Str("symbol");
        JsonElement priceFilter = item.GetProperty("priceFilter");
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

        InstrumentSpec spec = new()
        {
            Id = BybitVenue.ToInstrumentId(raw, _productType),
            RawSymbol = new Symbol(raw),
            AssetClass = AssetClass.Crypto,
            InstrumentClass = _productType == BybitProductType.Spot ? InstrumentClass.Spot : InstrumentClass.Swap,
            QuoteCurrency = quote,
            BaseCurrency = baseCurrency,
            SettlementCurrency = settlement,
            PricePrecision = pricePrecision,
            SizePrecision = sizePrecision,
            PriceIncrement = new Price(tickSize, pricePrecision),
            SizeIncrement = new Quantity(step, sizePrecision),
            MinQuantity = minQty > 0m ? new Quantity(minQty, sizePrecision) : null,
            MaxQuantity = maxQty > 0m ? new Quantity(maxQty, sizePrecision) : null,
            MinNotional = minNotional > 0m ? new Money(minNotional, quote) : null,
            MinPrice = priceFilter.Dec("minPrice") > 0m ? new Price(priceFilter.Dec("minPrice"), pricePrecision) : null,
            MaxPrice = priceFilter.Dec("maxPrice") > 0m ? new Price(priceFilter.Dec("maxPrice"), pricePrecision) : null,
            MakerFee = _productType == BybitProductType.Spot ? 0.001m : 0.0002m,
            TakerFee = _productType == BybitProductType.Spot ? 0.001m : 0.00055m,
            MarginInit = _productType == BybitProductType.Spot ? 0m : 0.05m,
            MarginMaint = _productType == BybitProductType.Spot ? 0m : 0.025m,
            TsEvent = now,
            TsInit = now,
        };

        if (_productType == BybitProductType.Spot)
        {
            return new CurrencyPair(spec);
        }

        string contractType = item.Str("contractType");
        if (contractType.Contains("Perpetual", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(contractType))
        {
            return new CryptoPerpetual(spec);
        }

        return new CryptoFuture(spec with { InstrumentClass = InstrumentClass.Future }, baseCurrency, item.Ms("launchTime"), item.Ms("deliveryTime"));
    }
}
