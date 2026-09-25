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
        // What the venue really requires, once for the whole catalog and only where a key is held.
        Dictionary<string, (decimal Initial, decimal Maintenance, decimal MaxLeverage)> brackets = await BracketsAsync(null, ct).ConfigureAwait(false);
        using JsonDocument doc = await _http.GetPublicAsync(_http.Prefix + "/exchangeInfo", null, BinanceVenue.Weights.ExchangeInfo, ct).ConfigureAwait(false);
        int loaded = 0;
        foreach (JsonElement symbol in doc.RootElement.GetProperty("symbols").EnumerateArray())
        {
            Instrument? instrument = Parse(symbol, brackets);
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

        Dictionary<string, (decimal Initial, decimal Maintenance, decimal MaxLeverage)> brackets =
            await BracketsAsync(BinanceVenue.ToRawSymbol(id), ct).ConfigureAwait(false);

        using (doc)
        {
            // Only the instrument that was asked for. BOTH futures families IGNORE the symbol filter and answer
            // with their whole contract list - 909 of them on the USD-margined host, measured, and all 30 on the
            // coin-margined one - for a known symbol, an unknown one, or anything else, so without this, loading
            // one instrument loaded the whole market, and asking about a symbol that does not exist filled the
            // provider with contracts that do while still not finding the one requested. Spot honours the filter
            // and refuses an unknown symbol outright, which is why this went unnoticed: the families disagree.
            //
            // Measured on the coin-margined family in its own right rather than inferred from its sibling, which
            // is the inference this rule exists to forbid: ?symbol=BTCUSD_PERP, ?symbol=NOSUCH_PERP and
            // ?pair=BTCUSD each answered HTTP 200 with the same 30 contracts on 2026-09-25.
            foreach (JsonElement symbol in doc.RootElement.GetProperty("symbols").EnumerateArray())
            {
                if (Parse(symbol, brackets) is { } instrument && instrument.Id == id)
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

    /// <summary>
    /// What this venue really requires per symbol, at the bracket a position starts in, or an empty map where no
    /// credential is held (R4.12).
    /// <para>
    /// The venue-wide default this venue publishes publicly cannot be its minimum: 5 percent supports at most 20x
    /// and the venue grants 125x. Since <see cref="Instrument.InitialMarginRate"/> takes the LARGER of 1/leverage
    /// and the instrument's margin, publishing that default is a floor that sizes and liquidates a 50x strategy as
    /// though it were 20x - a result for a strategy nobody wrote, with nothing to indicate it.
    /// </para>
    /// <para>
    /// So it is read where a key exists. An unauthenticated caller gets an empty map and keeps the venue-wide
    /// default with a null ceiling, which says "not published" rather than "unlimited" - and because instruments are
    /// persisted, fetching this once with a key leaves every later run reading the corrected figures.
    /// </para>
    /// <para>
    /// Read for BOTH futures families, on the same terms. The coin-margined endpoint was confirmed to exist and to
    /// be signed - an unauthenticated call is refused with -2014, and nothing public serves the same figures - but
    /// the SHAPE of its answer could not be confirmed without a key, and this venue's coin-margined reads are not
    /// reliably its USD-margined ones with a different prefix. So each row is matched by whichever identifier it
    /// carries, against both a contract's symbol and its pair; both of those are fields this venue's own contract
    /// data publishes, and accepting either costs nothing while guessing one and being wrong would throw away every
    /// instrument in the catalog rather than one figure on each.
    /// </para>
    /// </summary>
    private async Task<Dictionary<string, (decimal Initial, decimal Maintenance, decimal MaxLeverage)>> BracketsAsync(string? symbol, CancellationToken ct)
    {
        Dictionary<string, (decimal, decimal, decimal)> brackets = new(StringComparer.Ordinal);
        if (!BinanceVenue.IsFutures(_accountType) || !_http.HasCredentials)
        {
            return brackets;
        }

        Dictionary<string, string>? query = symbol is null ? null : new(StringComparer.Ordinal) { ["symbol"] = symbol };
        JsonDocument doc;
        try
        {
            doc = await _http.GetSignedAsync(BinanceVenue.LeverageBracketPath(_accountType), query, BinanceVenue.LeverageBracketWeight, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A key that cannot read brackets is not a reason to refuse an instrument list. Logged loudly, because
            // what is lost is the difference between the venue's real margin and a default six times larger.
            Log.LogWarning(e, "Binance would not say what margin it requires, so its instruments keep the venue-wide default, which is a floor at 20x");
            return brackets;
        }

        using (doc)
        {
            // The answer is an array per symbol, each holding the notional brackets newest-widest first. The
            // instrument's own figure is the tier a position STARTS in - the highest leverage the venue grants and
            // the lowest maintenance it takes - and everything above it is the venue charging more as a position
            // grows, which is the venue's business rather than a property of the instrument.
            // One row per symbol, or a single object rather than an array where the venue answers about one thing.
            IEnumerable<JsonElement> rows = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray()
                : [doc.RootElement];

            foreach (JsonElement row in rows)
            {
                if (!row.TryGetProperty("brackets", out JsonElement tiers) || tiers.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                decimal leverage = 0m;
                decimal maintenance = 0m;
                foreach (JsonElement tier in tiers.EnumerateArray())
                {
                    decimal candidate = tier.TryGetProperty("initialLeverage", out JsonElement l) ? l.DecValue() : 0m;
                    if (candidate <= leverage)
                    {
                        continue;
                    }

                    leverage = candidate;
                    maintenance = tier.TryGetProperty("maintMarginRatio", out JsonElement m) ? m.DecValue() : 0m;
                }

                // Under whichever name the row gave itself. A row naming neither is one nothing could be matched
                // to, so it is dropped rather than filed under an empty key that a contract could then collide with.
                string? key = row.StrOpt("symbol") ?? row.StrOpt("pair");
                if (leverage > 0m && key is { Length: > 0 })
                {
                    brackets[key] = (1m / leverage, maintenance, leverage);
                }
            }
        }

        return brackets;
    }

    private Instrument? Parse(JsonElement symbol, IReadOnlyDictionary<string, (decimal Initial, decimal Maintenance, decimal MaxLeverage)> brackets)
    {
        // Whichever field this family states tradability in. Both are read because the two futures families do not
        // agree on the name: the coin-margined one publishes contractStatus and no status at all, so reading only
        // the first left every one of its contracts on the default - including the ones the venue lists as pending
        // or in delivery, which would have been offered in a picker and refused on the first order.
        string? status = null;
        foreach (string field in BinanceVenue.StatusFields)
        {
            status ??= symbol.StrOpt(field);
        }

        if ((status ?? BinanceVenue.StatusTrading) != BinanceVenue.StatusTrading)
        {
            return null;
        }

        string raw = symbol.Str("symbol");
        string pair = symbol.StrOpt("pair") ?? raw;

        // What this venue requires here, if a key was held when the catalog was read. By the contract's own name
        // first; and on the coin-margined family by its PAIR as well, because which of the two a bracket row names
        // could not be established there without a key, and that family's own read takes either.
        //
        // Only there, and the difference matters: the USD-margined family names a dated contract's brackets by that
        // contract's own symbol, so letting the perpetual's row match by pair would give BTCUSDT_250926 the
        // perpetual's margin instead of the one the venue publishes for it.
        (decimal Initial, decimal Maintenance, decimal MaxLeverage)? bracket =
            brackets.TryGetValue(raw, out (decimal Initial, decimal Maintenance, decimal MaxLeverage) found)
                || (_accountType == BinanceAccountType.CoinMFutures && brackets.TryGetValue(pair, out found))
                ? found
                : null;
        string baseAsset = symbol.Str("baseAsset");
        string quoteAsset = symbol.Str("quoteAsset");
        string contractType = symbol.StrOpt("contractType") ?? BinanceVenue.PerpetualContractType;

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
        bool coinMargined = _accountType == BinanceAccountType.CoinMFutures;

        // What one contract is worth in the quote currency, where this venue publishes it. Only the coin-margined
        // family does: 100 USD on the BTCUSD contracts and 10 USD on the other 27, measured 2026-09-25. It becomes
        // the instrument's multiplier, which is what the engine puts a quantity through - on an inverse contract
        // notional is quantity x multiplier / price, so a contract size left at one would value every BTCUSD
        // position at a hundredth of what it is, and margin, commission and liquidation would all follow it down.
        decimal contractSize = symbol.TryGetProperty(BinanceVenue.ContractSizeField, out JsonElement published)
            ? published.DecValue()
            : 0m;

        InstrumentSpec spec = new()
        {
            Id = BinanceVenue.ToInstrumentId(raw, _accountType, contractType),
            RawSymbol = new Symbol(raw),
            AssetClass = AssetClass.Crypto,
            InstrumentClass = _accountType == BinanceAccountType.Spot ? InstrumentClass.Spot : InstrumentClass.Swap,
            QuoteCurrency = quote,
            BaseCurrency = baseCurrency,
            SettlementCurrency = _accountType == BinanceAccountType.Spot ? quote : Currency.FromCode(symbol.StrOpt(BinanceVenue.MarginAssetField) ?? quoteAsset, 8),

            // Coin-margined contracts are quoted in USD and margined and settled in the base coin, so every money
            // figure on them - notional, margin, profit, commission, funding - comes out in the coin and goes
            // through one over the price. Declared from the account type rather than inferred from the settlement
            // currency matching the base one, which is a coincidence on some venues and the definition on none.
            IsInverse = coinMargined,
            PricePrecision = pricePrecision,
            SizePrecision = sizePrecision,
            PriceIncrement = new Price(tickSize, pricePrecision),

            // In the units the venue itself trades in. That is a whole number of contracts on the coin-margined
            // family - its LOT_SIZE step and minimum are both 1 and its quantityPrecision is 0 - and base units
            // everywhere else, which is what the venue's own quantity fields, candle volumes and order sizes carry.
            SizeIncrement = new Quantity(stepSize, sizePrecision),
            Multiplier = contractSize > 0m ? new Quantity(contractSize, Precision(contractSize)) : null,
            MinQuantity = minQty > 0m ? new Quantity(minQty, sizePrecision) : null,
            MaxQuantity = maxQty > 0m ? new Quantity(maxQty, sizePrecision) : null,
            MinNotional = minNotional > 0m ? new Money(minNotional, quote) : null,
            MaxNotional = maxNotional > 0m ? new Money(maxNotional, quote) : null,
            MinPrice = minPrice > 0m ? new Price(minPrice, pricePrecision) : null,
            MaxPrice = maxPrice > 0m ? new Price(maxPrice, pricePrecision) : null,
            // Per family, from this venue's published schedules: the coin-margined market is cheaper than the
            // USD-margined one, so one futures figure for both would have overstated what trading here costs.
            MakerFee = _accountType switch
            {
                BinanceAccountType.Spot => BinanceVenue.SpotMakerFee,
                BinanceAccountType.CoinMFutures => BinanceVenue.CoinMFuturesMakerFee,
                _ => BinanceVenue.UsdMFuturesMakerFee,
            },
            TakerFee = _accountType switch
            {
                BinanceAccountType.Spot => BinanceVenue.SpotTakerFee,
                BinanceAccountType.CoinMFutures => BinanceVenue.CoinMFuturesTakerFee,
                _ => BinanceVenue.UsdMFuturesTakerFee,
            },

            // Read from the venue rather than assumed: published per symbol in the same response the instruments
            // come from, where the adapter used to carry one hard-coded pair for all 909 of them.
            //
            // KNOWN COARSE, AND NOT YET THE REAL FIGURE. What this venue publishes without a key is a venue-wide
            // default, not the minimum it will actually take. The arithmetic says so on its own: 5 percent supports
            // at most 20x, and this venue grants 125x on BTCUSDT, which cannot need more than 0.8 percent. So this
            // number is still a floor under InitialMarginRate that clamps every leverage above 20x - the same defect
            // corrected on Bybit, where the real figures were public.
            //
            // The true per-notional brackets are behind the venue's signed leverageBracket read. Measured
            // 2026-09-25 on both futures hosts: the documented endpoint refuses an unauthenticated call, the web
            // interface's own bracket feed rejects it, and there is no futures-data equivalent - so there is no
            // public source, and closing this needs the brackets fetched where credentials exist. Sourcing the
            // figure is an improvement over inventing it; it is not the same as being right.
            //
            // The coin-margined family is the same story with the same numbers, and measurably a default rather
            // than a per-contract figure: all 30 of its contracts publish 5.0000 and 2.5000, from the 100-USD
            // BTCUSD_PERP to the 10-USD altcoin quarterlies.
            //
            // A spot account borrows nothing, so its margin is zero rather than unpublished.
            // The venue's real requirement where a key could read it, and its public venue-wide default otherwise.
            // The difference is not small: the default is 5 percent, a floor supporting 20x, where the brackets give
            // 0.8 percent and 125x on the same contract.
            MarginInit = _accountType == BinanceAccountType.Spot
                ? 0m
                : bracket?.Initial ?? PublishedMargin(symbol, "requiredMarginPercent", BinanceVenue.DefaultMarginInit),
            MarginMaint = _accountType == BinanceAccountType.Spot
                ? 0m
                : bracket?.Maintenance ?? PublishedMargin(symbol, "maintMarginPercent", BinanceVenue.DefaultMarginMaint),

            // Null, and deliberately: this venue keeps its notional brackets behind a signed endpoint, so the most
            // leverage it will grant cannot be read from public data. Null says "not published", which is a
            // different thing from unlimited to anybody deciding whether a configured leverage is reachable.
            // Null where no key could read the brackets, which says "this venue did not say" rather than
            // "unlimited" - opposite answers to anything deciding whether a configured leverage is reachable.
            MaxLeverage = bracket?.MaxLeverage,
            TsEvent = now,
            TsInit = now,
            Info = new Dictionary<string, string>(StringComparer.Ordinal) { ["raw"] = symbol.GetRawText() },
        };

        if (_accountType == BinanceAccountType.Spot)
        {
            return new CurrencyPair(spec);
        }

        // From the venue's own contractType field and never from the symbol. The coin-margined family is why that
        // matters here: its perpetuals are spelled BTCUSD_PERP and its quarterlies BTCUSD_261225, so a reader
        // splitting on the underscore would have called every one of them dated.
        if (contractType == BinanceVenue.PerpetualContractType)
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
