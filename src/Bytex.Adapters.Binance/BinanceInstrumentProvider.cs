using System.Globalization;
using System.Text.Json;
using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Live.Network;
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
        using JsonDocument doc = await _http.GetPublicAsync(_http.Prefix + "/exchangeInfo", null, BinanceVenue.Weights.ExchangeInfo, ct).ConfigureAwait(false);
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
        JsonDocument doc;
        try
        {
            doc = await _http.GetPublicAsync(_http.Prefix + "/exchangeInfo", query, 2, ct).ConfigureAwait(false);
        }
        catch (VenueHttpException e) when (BinanceVenue.IsNoSuchInstrument(e))
        {
            // Not listed is an answer, not a failure: a caller looks the instrument up afterwards and finds
            // nothing, the same on every venue.
            Log.LogInformation("Binance does not list {Instrument}", id);
            return;
        }

        using (doc)
        {
            // Only the instrument that was asked for. USD-margined futures IGNORES the symbol filter and answers
            // with its whole contract list - 909 of them, measured, for a known symbol, an unknown one, or
            // anything else - so without this, loading one instrument loaded the entire venue, and asking about a
            // symbol that does not exist filled the provider with hundreds that do while still not finding the one
            // requested. Spot honours the filter and refuses an unknown symbol outright, which is why this went
            // unnoticed: the two families of one venue disagree.
            foreach (JsonElement symbol in doc.RootElement.GetProperty("symbols").EnumerateArray())
            {
                if (Parse(symbol) is { } instrument && instrument.Id == id)
                {
                    Add(instrument);
                }
            }
        }
    }

    /// <summary>
    /// What this venue requires here, from the same response the instrument came from (R4.12). Published per symbol
    /// as a percentage - "5.0000" meaning a twentieth - where the engine holds a fraction of notional.
    /// <para>
    /// Read rather than assumed: the adapter carried one hard-coded pair for all 909 contracts, which happened to
    /// match BTCUSDT and was a guess on every other one. A symbol that publishes nothing keeps the venue-wide
    /// default, because a zero here would mean this venue asks for no margin at all.
    /// </para>
    /// </summary>
    private static decimal PublishedMargin(JsonElement symbol, string field, decimal whenAbsent) =>
        symbol.TryGetProperty(field, out JsonElement published)
            && decimal.TryParse(published.ValueKind == JsonValueKind.String ? published.GetString() : published.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal percent)
            && percent > 0m
                ? percent / BinanceVenue.MarginPercentToFraction
                : whenAbsent;

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
            // Read from the venue rather than assumed: published per symbol in the same response the instruments
            // come from, where the adapter used to carry one hard-coded pair for all 909 of them.
            //
            // KNOWN COARSE, AND NOT YET THE REAL FIGURE. What this venue publishes without a key is a venue-wide
            // default, not the minimum it will actually take. The arithmetic says so on its own: 5 percent supports
            // at most 20x, and this venue grants 125x on BTCUSDT, which cannot need more than 0.8 percent. So this
            // number is still a floor under InitialMarginRate that clamps every leverage above 20x - the same defect
            // corrected on Bybit, where the real figures were public.
            //
            // The true per-notional brackets are behind /fapi/v1/leverageBracket, which is signed. Measured
            // 2026-09-25: the documented endpoint refuses an unauthenticated call, the web interface's own bracket
            // feed rejects it, and there is no futures-data equivalent - so there is no public source, and closing
            // this needs the brackets fetched where credentials exist. Sourcing the figure is an improvement over
            // inventing it; it is not the same as being right.
            //
            // A spot account borrows nothing, so its margin is zero rather than unpublished.
            MarginInit = _accountType == BinanceAccountType.Spot ? 0m : PublishedMargin(symbol, "requiredMarginPercent", BinanceVenue.DefaultMarginInit),
            MarginMaint = _accountType == BinanceAccountType.Spot ? 0m : PublishedMargin(symbol, "maintMarginPercent", BinanceVenue.DefaultMarginMaint),

            // Null, and deliberately: this venue keeps its notional brackets behind a signed endpoint, so the most
            // leverage it will grant cannot be read from public data. Null says "not published", which is a
            // different thing from unlimited to anybody deciding whether a configured leverage is reachable.
            MaxLeverage = null,
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
