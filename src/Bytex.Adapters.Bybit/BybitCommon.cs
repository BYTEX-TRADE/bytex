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

    bool Testnet { get; }

    string? BaseUrlHttp { get; }

    string? BaseUrlWs { get; }
}

public sealed record BybitDataClientConfig : DataClientConfig, IBybitSettings
{
    public string? ApiKey { get; init; }

    public string? ApiSecret { get; init; }

    public BybitProductType ProductType { get; init; } = BybitProductType.Spot;

    public bool Testnet { get; init; }

    public string? BaseUrlHttp { get; init; }

    public string? BaseUrlWs { get; init; }
}

public sealed record BybitExecutionClientConfig : ExecutionClientConfig, IBybitSettings
{
    public string? ApiKey { get; init; }

    public string? ApiSecret { get; init; }

    public BybitProductType ProductType { get; init; } = BybitProductType.Spot;

    public bool Testnet { get; init; }

    public string? BaseUrlHttp { get; init; }

    public string? BaseUrlWs { get; init; }

    public int RecvWindowMs { get; init; } = 5000;

    public TriggerType DefaultTriggerType { get; init; } = TriggerType.LastPrice;
}

public static class BybitVenue
{
    public static readonly Venue Venue = new("BYBIT");

    public const string EnvApiKey = "BYBIT_API_KEY";
    public const string EnvApiSecret = "BYBIT_API_SECRET";
    public const string EnvTestnetApiKey = "BYBIT_TESTNET_API_KEY";
    public const string EnvTestnetApiSecret = "BYBIT_TESTNET_API_SECRET";
    public const string PerpSuffix = "-PERP";

    public static string HttpBase(IBybitSettings s) => s.BaseUrlHttp ?? (s.Testnet ? "https://api-testnet.bybit.com" : "https://api.bybit.com");

    public static string WsPublic(IBybitSettings s) => (s.BaseUrlWs ?? (s.Testnet ? "wss://stream-testnet.bybit.com" : "wss://stream.bybit.com")) + "/v5/public/" + Category(s.ProductType);

    public static string WsPrivate(IBybitSettings s) => (s.BaseUrlWs ?? (s.Testnet ? "wss://stream-testnet.bybit.com" : "wss://stream.bybit.com")) + "/v5/private";

    public static string Category(BybitProductType type) => type == BybitProductType.Spot ? "spot" : "linear";

    public static InstrumentId ToInstrumentId(string rawSymbol, BybitProductType type) =>
        new(new Symbol(type == BybitProductType.Linear ? rawSymbol + PerpSuffix : rawSymbol), Venue);

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
        (Secrets.Require(s.ApiKey, s.Testnet ? EnvTestnetApiKey : EnvApiKey), Secrets.Require(s.ApiSecret, s.Testnet ? EnvTestnetApiSecret : EnvApiSecret));
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

    public BybitHttp(IBybitSettings settings, ILogger? logger = null, bool requireCredentials = false, int recvWindowMs = 5000)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ProductType = settings.ProductType;
        _recvWindow = recvWindowMs;
        if (requireCredentials)
        {
            (_apiKey, _apiSecret) = BybitVenue.Credentials(settings);
        }
        else
        {
            _apiKey = Secrets.Optional(settings.ApiKey, settings.Testnet ? BybitVenue.EnvTestnetApiKey : BybitVenue.EnvApiKey);
            _apiSecret = Secrets.Optional(settings.ApiSecret, settings.Testnet ? BybitVenue.EnvTestnetApiSecret : BybitVenue.EnvApiSecret);
        }

        _http = new HttpClientWrapper(new Uri(BybitVenue.HttpBase(settings)), new RateLimiter(100, TimeSpan.FromSeconds(5)), new RetryPolicy(3), logger);
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
        string text = await _http.SendAsync(HttpMethod.Post, path, null, content, Sign(json), 1, ct).ConfigureAwait(false);
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
        JsonElement result = await _http.GetPublicAsync("/v5/market/instruments-info", query, ct).ConfigureAwait(false);
        foreach (JsonElement item in result.GetProperty("list").EnumerateArray())
        {
            Instrument? instrument = Parse(item);
            if (instrument is not null)
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
        decimal minNotional = lotFilter.Dec("minNotionalValue");
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
