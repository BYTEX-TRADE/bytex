using System.Globalization;
using System.Text.Json;
using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Microsoft.Extensions.Logging;

namespace Bytex.Adapters.Binance;

/// <summary>
/// Loads instrument definitions from the Binance exchange information endpoint.
/// </summary>
public sealed class BinanceInstrumentProvider : InstrumentProviderBase
{
    private readonly BinanceHttp _http;
    private readonly BinanceAccountType _accountType;

    public BinanceInstrumentProvider(BinanceHttp http, BinanceAccountType accountType, InstrumentProviderConfig? config = null, ILogger? logger = null)
        : base(BinanceVenue.Venue, config, logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _accountType = accountType;
    }

    public override async Task LoadAllAsync(CancellationToken ct, IReadOnlyDictionary<string, string>? filters = null)
    {
        using JsonDocument doc = await _http.GetPublicAsync(_http.Prefix + "/exchangeInfo", null, 20, ct).ConfigureAwait(false);
        int loaded = 0;
        foreach (JsonElement symbol in doc.RootElement.GetProperty("symbols").EnumerateArray())
        {
            Instrument? instrument = Parse(symbol);
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

        Log.LogInformation("Loaded {Count} Binance {Type} instruments", loaded, _accountType);
    }

    public override async Task LoadAsync(InstrumentId id, CancellationToken ct)
    {
        Dictionary<string, string> query = new() { ["symbol"] = BinanceVenue.ToRawSymbol(id) };
        using JsonDocument doc = await _http.GetPublicAsync(_http.Prefix + "/exchangeInfo", query, 2, ct).ConfigureAwait(false);
        foreach (JsonElement symbol in doc.RootElement.GetProperty("symbols").EnumerateArray())
        {
            Instrument? instrument = Parse(symbol);
            if (instrument is not null)
            {
                Add(instrument);
            }
        }
    }

    private Instrument? Parse(JsonElement symbol)
    {
        string status = symbol.StrOpt("status") ?? "TRADING";
        if (status != "TRADING")
        {
            return null;
        }

        string raw = symbol.Str("symbol");
        string baseAsset = symbol.Str("baseAsset");
        string quoteAsset = symbol.Str("quoteAsset");
        string contractType = symbol.StrOpt("contractType") ?? "PERPETUAL";

        decimal tickSize = 0m;
        decimal stepSize = 0m;
        decimal minQty = 0m;
        decimal maxQty = 0m;
        decimal minNotional = 0m;
        decimal maxNotional = 0m;
        decimal minPrice = 0m;
        decimal maxPrice = 0m;
        foreach (JsonElement filter in symbol.GetProperty("filters").EnumerateArray())
        {
            switch (filter.Str("filterType"))
            {
                case "PRICE_FILTER":
                    tickSize = filter.Dec("tickSize");
                    minPrice = filter.Dec("minPrice");
                    maxPrice = filter.Dec("maxPrice");
                    break;
                case "LOT_SIZE":
                    stepSize = filter.Dec("stepSize");
                    minQty = filter.Dec("minQty");
                    maxQty = filter.Dec("maxQty");
                    break;
                case "MIN_NOTIONAL":
                case "NOTIONAL":
                    minNotional = filter.Has("minNotional") ? filter.Dec("minNotional") : filter.Has("notional") ? filter.Dec("notional") : 0m;
                    if (filter.Has("maxNotional"))
                    {
                        maxNotional = filter.Dec("maxNotional");
                    }

                    break;
            }
        }

        if (tickSize <= 0m || stepSize <= 0m)
        {
            return null;
        }

        byte pricePrecision = Precision(tickSize);
        byte sizePrecision = Precision(stepSize);
        Currency quote = Currency.FromCode(quoteAsset, 8);
        Currency baseCurrency = Currency.FromCode(baseAsset, 8);
        UnixNanos now = UnixNanos.FromDateTimeOffset(DateTimeOffset.UtcNow);

        InstrumentSpec spec = new()
        {
            Id = BinanceVenue.ToInstrumentId(raw, _accountType, contractType),
            RawSymbol = new Symbol(raw),
            AssetClass = AssetClass.Crypto,
            InstrumentClass = _accountType == BinanceAccountType.Spot ? InstrumentClass.Spot : InstrumentClass.Swap,
            QuoteCurrency = quote,
            BaseCurrency = baseCurrency,
            SettlementCurrency = _accountType == BinanceAccountType.Spot ? quote : Currency.FromCode(symbol.StrOpt("marginAsset") ?? quoteAsset, 8),
            PricePrecision = pricePrecision,
            SizePrecision = sizePrecision,
            PriceIncrement = new Price(tickSize, pricePrecision),
            SizeIncrement = new Quantity(stepSize, sizePrecision),
            MinQuantity = minQty > 0m ? new Quantity(minQty, sizePrecision) : null,
            MaxQuantity = maxQty > 0m ? new Quantity(maxQty, sizePrecision) : null,
            MinNotional = minNotional > 0m ? new Money(minNotional, quote) : null,
            MaxNotional = maxNotional > 0m ? new Money(maxNotional, quote) : null,
            MinPrice = minPrice > 0m ? new Price(minPrice, pricePrecision) : null,
            MaxPrice = maxPrice > 0m ? new Price(maxPrice, pricePrecision) : null,
            MakerFee = _accountType == BinanceAccountType.Spot ? 0.001m : 0.0002m,
            TakerFee = _accountType == BinanceAccountType.Spot ? 0.001m : 0.0005m,
            MarginInit = _accountType == BinanceAccountType.Spot ? 0m : 0.05m,
            MarginMaint = _accountType == BinanceAccountType.Spot ? 0m : 0.025m,
            TsEvent = now,
            TsInit = now,
            Info = new Dictionary<string, string>(StringComparer.Ordinal) { ["raw"] = symbol.GetRawText() },
        };

        if (_accountType == BinanceAccountType.Spot)
        {
            return new CurrencyPair(spec);
        }

        if (contractType == "PERPETUAL")
        {
            return new CryptoPerpetual(spec);
        }

        long delivery = symbol.Has("deliveryDate") ? symbol.Long("deliveryDate") : 0;
        long onboard = symbol.Has("onboardDate") ? symbol.Long("onboardDate") : 0;
        return new CryptoFuture(spec with { InstrumentClass = InstrumentClass.Future }, baseCurrency, UnixNanos.FromMilliseconds(onboard), UnixNanos.FromMilliseconds(delivery));
    }

    private static byte Precision(decimal increment)
    {
        string text = increment.ToString(CultureInfo.InvariantCulture).TrimEnd('0');
        int dot = text.IndexOf('.');
        return (byte)(dot < 0 ? 0 : text.Length - dot - 1);
    }
}
