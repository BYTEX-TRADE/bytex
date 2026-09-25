using System.Globalization;
using System.Text.Json;
using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Microsoft.Extensions.Logging;

namespace Bytex.Adapters.Kucoin;

/// <summary>
/// Which of KuCoin's two markets a client talks to. They are separate APIs on separate hosts that happen to share a
/// key: the symbols are spelled differently, the candle rows are ordered differently, and an order is denominated
/// differently. Nothing above the adapter sees any of that, but a client has to be told which one it is.
/// </summary>
public enum KucoinProductType
{
    /// <summary>Spot, on <c>api.kucoin.com</c>. The default, so configurations written before futures existed mean what they meant.</summary>
    Spot,

    /// <summary>USDT- and USDC-margined perpetual contracts, on <c>api-futures.kucoin.com</c>.</summary>
    Futures,
}

/// <summary>
/// What KuCoin's futures market is, as measured against the live venue rather than read off its documentation - the
/// two disagree in three places that matter, and each disagreement is the kind that produces plausible wrong numbers
/// rather than an error.
/// </summary>
public static class KucoinFuturesVenue
{
    /// <summary>Where the futures API answers. A different host from spot, not a different path on the same one.</summary>
    public const string DefaultHttpBase = "https://api-futures.kucoin.com";

    /// <summary>
    /// What the venue calls a perpetual, appended: <c>XBTUSDTM</c> is the contract, <c>XBTUSDT-PERP</c> the symbol of
    /// its instrument id. The suffix is what the other venues use, so a perpetual reads as one wherever it comes from.
    /// </summary>
    public const string PerpSuffix = "-PERP";

    /// <summary>
    /// The venue's own suffix on every contract in this family. Checked against all 684 tradable contracts on
    /// 2026-09-25: every one ends in it, and stripping it never collides with another contract's id.
    /// </summary>
    public const string ContractSuffix = "M";

    /// <summary>
    /// The most candle rows one request answers with. The documentation says 500 and the venue gives 200: asked for
    /// three days of minutes it returned exactly 200 rows and stopped. A paging loop written to the documented number
    /// would read 200, believe it had reached the end of the venue's history, and silently return a fifth of a window.
    /// </summary>
    public const int CandlePage = 200;

    /// <summary>
    /// How many funding settlements one request answers with. The venue documents no limit and answered every
    /// settlement in the windows measured, so this bounds a request rather than describing a cap the venue enforces.
    /// </summary>
    public const int FundingPage = 1000;

    /// <summary>What a contract is worth in base currency, kept on the instrument so an order can be denominated in it.</summary>
    public const string MultiplierInfo = "contractMultiplier";

    /// <summary>
    /// The leverage an order states when nothing is configured. The venue demands one on every order and has no
    /// default of its own, so something must supply it, and one - no leverage - is the only safe choice: any other
    /// default would lever a position nobody asked to lever.
    /// </summary>
    public const int DefaultLeverage = 1;

    /// <summary>
    /// The private topics a trading node needs: its orders, its balances, its positions, and its stop orders which
    /// live in a list of their own until they trigger.
    /// </summary>
    public static readonly IReadOnlyList<string> PrivateTopics =
    [
        "/contractMarket/tradeOrders",
        "/contractAccount/wallet",
        "/contract/positionAll",
        "/contractMarket/advancedOrders",
    ];

    /// <summary>
    /// What the linear family settles in. The venue keeps a separate balance per settlement currency and answers
    /// its account overview for one of them at a time, so both are asked for; 679 of the 684 contracts settle in
    /// USDT and the other five in USDC.
    /// </summary>
    public static readonly IReadOnlyList<string> SettlementCurrencies = ["USDT", "USDC"];

    /// <summary>
    /// Which price a stop watches. The venue offers the trade price, the index price and the mark price; the mark
    /// price is what it liquidates against, so a stop that guards a position has to watch the same one or it can be
    /// liquidated without ever triggering.
    /// </summary>
    public const string StopPriceType = "MP";

    /// <summary>The instrument id of a contract: its symbol with the venue's M traded for the engine's -PERP.</summary>
    public static InstrumentId ToInstrumentId(string rawSymbol) =>
        new(new Symbol(rawSymbol.EndsWith(ContractSuffix, StringComparison.Ordinal)
            ? rawSymbol[..^ContractSuffix.Length] + PerpSuffix
            : rawSymbol), KucoinVenue.Venue);

    /// <summary>The contract behind an instrument id, which is <see cref="ToInstrumentId"/> run backwards.</summary>
    public static string ToRawSymbol(InstrumentId id)
    {
        string symbol = id.Symbol.Value;
        return symbol.EndsWith(PerpSuffix, StringComparison.Ordinal)
            ? symbol[..^PerpSuffix.Length] + ContractSuffix
            : symbol;
    }

    /// <summary>
    /// The granularity, in whole minutes, that this venue calls a bar of the given length. Futures takes a number of
    /// minutes where spot takes a word ("1min"), and refuses a length it does not keep with code 300000 rather than
    /// answering something close to it.
    /// </summary>
    public static int Granularity(BarSpecification spec) => (spec.Aggregation, spec.Step) switch
    {
        (BarAggregation.Minute, 1) => 1,
        (BarAggregation.Minute, 3) => 3,
        (BarAggregation.Minute, 5) => 5,
        (BarAggregation.Minute, 15) => 15,
        (BarAggregation.Minute, 30) => 30,
        (BarAggregation.Hour, 1) => 60,
        (BarAggregation.Hour, 2) => 120,
        (BarAggregation.Hour, 4) => 240,
        (BarAggregation.Hour, 8) => 480,
        (BarAggregation.Hour, 12) => 720,
        (BarAggregation.Day, 1) => 1440,
        (BarAggregation.Week, 1) => 10080,
        _ => throw new NotSupportedException(
            $"KuCoin futures does not keep {spec} candles. It keeps 1, 3, 5, 15 and 30 minutes, 1, 2, 4, 8 and 12 "
            + "hours, a day and a week."),
    };

    /// <summary>
    /// What one contract of this instrument is worth in its base currency - 0.001 XBT on XBTUSDTM, and anything from
    /// 0.01 to 1000 elsewhere in the family. Every quantity crossing into or out of this venue goes through it.
    /// </summary>
    public static decimal Multiplier(Instrument instrument)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        if (instrument.Info is { } info
            && info.TryGetValue(MultiplierInfo, out string? text)
            && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal multiplier)
            && multiplier > 0m)
        {
            return multiplier;
        }

        throw new InvalidOperationException(
            $"{instrument.Id} carries no KuCoin contract size, so nothing can say how many contracts a quantity of it "
            + "is. It was not loaded by this venue's futures instrument provider.");
    }

    /// <summary>
    /// A quantity in base currency as the whole number of contracts the venue takes. The engine rounds a quantity to
    /// the instrument's size increment, which is one contract, so this divides exactly; a quantity that does not is a
    /// caller that built one by hand, and rounding it silently would fill a different size than was asked for.
    /// </summary>
    public static long ToContracts(Instrument instrument, Quantity quantity)
    {
        decimal multiplier = Multiplier(instrument);
        decimal contracts = quantity.Value / multiplier;
        decimal whole = decimal.Round(contracts, 0, MidpointRounding.ToEven);
        if (Math.Abs(contracts - whole) > 0.0000000001m)
        {
            throw new ArgumentException(
                $"{quantity.Value} of {instrument.Id} is {contracts} contracts, and this venue trades whole ones. "
                + $"Round the quantity to the instrument's size increment of {multiplier} first.",
                nameof(quantity));
        }

        return (long)whole;
    }

    /// <summary>A number of contracts as a quantity in base currency, which is how everything above the adapter reads it.</summary>
    public static Quantity ToQuantity(Instrument instrument, decimal contracts) =>
        instrument.MakeQuantity(contracts * Multiplier(instrument));
}

/// <summary>
/// Loads the perpetual contracts from <c>/api/v1/contracts/active</c>.
/// <para>
/// Two things here are this venue's own and are deliberately not visible above it. A contract is a fraction of the
/// base currency - 0.001 XBT on XBTUSDTM - and an order is a whole number of contracts, so the instrument's size
/// increment is published in base currency (0.001 XBT) exactly as Binance and Bybit publish theirs, and the adapter
/// converts. A strategy sizing in base units is then the same strategy on all three venues.
/// </para>
/// <para>
/// Two kinds of contract are left out, each for its own reason rather than as a side effect of the other.
/// </para>
/// <para>
/// Inverse contracts, because they are coin-margined and quoted in USD while settling in the base currency, so a
/// quantity of one cannot be expressed in base units at all without a price. Six of the venue's 690 are inverse.
/// </para>
/// <para>
/// Dated contracts, because this family is perpetuals - which is what it declares. The venue's only two dated
/// contracts are inverse as well, so today the first rule would already have caught them; the expiry is checked on
/// its own because "not inverse" and "perpetual" are two separate facts that currently coincide, and a contract that
/// expires must not be published as one that never does.
/// </para>
/// </summary>
public sealed class KucoinFuturesInstrumentProvider : InstrumentProviderBase
{
    private readonly KucoinHttp _http;

    public KucoinFuturesInstrumentProvider(KucoinHttp http, InstrumentProviderConfig? config = null, ILogger? logger = null)
        : base(KucoinVenue.Venue, config, logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public override async Task LoadAllAsync(CancellationToken ct, IReadOnlyDictionary<string, string>? filters = null)
    {
        JsonElement data = await _http.GetPublicAsync("/api/v1/contracts/active", null, ct).ConfigureAwait(false);
        int loaded = 0;
        int inverse = 0;
        int dated = 0;
        foreach (JsonElement item in data.EnumerateArray())
        {
            if (item.Has("isInverse") && item.Bool("isInverse"))
            {
                inverse++;
                continue;
            }

            // This family is perpetuals, which is what it declares, so a contract with a delivery date is not one of
            // them. Every dated contract the venue lists today is also inverse and would have gone already, so this
            // changes nothing now - it is here because "not inverse" and "perpetual" are two different facts that
            // happen to coincide, and a linear dated contract would otherwise be published as a swap: the wrong
            // class, no expiry recorded, funding assumed, and a declaration that says the family holds only
            // perpetuals. Nothing would catch it either, because the test that compares the declaration with what
            // this provider returns would read the same wrong answer from both sides.
            if (item.Has("expireDate") && item.GetProperty("expireDate").ValueKind is not JsonValueKind.Null)
            {
                dated++;
                continue;
            }

            Instrument? instrument = Parse(item);
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
            "Loaded {Count} KuCoin perpetual contracts, leaving out {Inverse} inverse and {Dated} dated ones this adapter does not offer",
            loaded,
            inverse,
            dated);
    }

    public override async Task LoadAsync(InstrumentId id, CancellationToken ct)
    {
        JsonElement item;
        try
        {
            item = await _http
                .GetPublicAsync("/api/v1/contracts/" + Uri.EscapeDataString(KucoinFuturesVenue.ToRawSymbol(id)), null, ct)
                .ConfigureAwait(false);
        }
        catch (KucoinApiException e) when (KucoinVenue.ErrorsThatMeanNoSuchInstrument.Contains(e.Code))
        {
            // As spot. This is the case a person reaches by typing BTC where this market says XBT, which is the
            // most likely mistake on this venue, so it must not arrive anywhere as a venue error string.
            Log.LogInformation("KuCoin does not list the contract {Instrument}", id);
            return;
        }

        if (item.ValueKind == JsonValueKind.Object && !(item.Has("isInverse") && item.Bool("isInverse"))
            && Parse(item) is { } instrument && instrument.Id == id)
        {
            Add(instrument);
        }
    }

    private static Instrument? Parse(JsonElement item)
    {
        if (!item.Str("status").Equals("Open", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string raw = item.Str("symbol");
        decimal tick = item.Dec("tickSize");
        decimal multiplier = item.Dec("multiplier");
        decimal lot = item.Has("lotSize") ? item.Dec("lotSize") : 1m;
        if (raw.Length == 0 || tick <= 0m || multiplier <= 0m || lot <= 0m)
        {
            return null;
        }

        // One contract is `multiplier` of the base currency and an order is a whole number of them, so the smallest
        // tradable quantity - and the step between tradable quantities - is that, in base currency.
        decimal step = multiplier * lot;
        byte pricePrecision = Json.Precision(tick);
        byte sizePrecision = Json.Precision(step);
        Currency quote = Currency.FromCode(item.Str("quoteCurrency"), 8);
        Currency baseCurrency = Currency.FromCode(item.Str("baseCurrency"), 8);
        Currency settlement = Currency.FromCode(item.Str("settleCurrency"), 8);
        decimal maxContracts = item.Has("maxOrderQty") ? item.Dec("maxOrderQty") : 0m;
        decimal maxPrice = item.Has("maxPrice") ? item.Dec("maxPrice") : 0m;
        UnixNanos now = UnixNanos.FromDateTimeOffset(DateTimeOffset.UtcNow);

        return new CryptoPerpetual(new InstrumentSpec
        {
            Id = KucoinFuturesVenue.ToInstrumentId(raw),
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
            MinQuantity = new Quantity(step, sizePrecision),
            MaxQuantity = maxContracts > 0m ? new Quantity(maxContracts * multiplier, sizePrecision) : null,
            MaxPrice = maxPrice > 0m ? new Price(maxPrice, pricePrecision) : null,

            // One, because a quantity is already in base currency by the time anything above the adapter sees it, so
            // notional is quantity times price here exactly as it is on the other two venues. The venue's own
            // contract size is below, where the adapter reads it and nothing else has to.
            Multiplier = new Quantity(1m, 0),
            MakerFee = item.Has("makerFeeRate") ? item.Dec("makerFeeRate") : 0.0002m,
            TakerFee = item.Has("takerFeeRate") ? item.Dec("takerFeeRate") : 0.0006m,
            MarginInit = item.Has("initialMargin") ? item.Dec("initialMargin") : 0m,
            MarginMaint = item.Has("maintainMargin") ? item.Dec("maintainMargin") : 0m,
            Info = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [KucoinFuturesVenue.MultiplierInfo] = multiplier.ToString(CultureInfo.InvariantCulture),
            },
            TsEvent = now,
            TsInit = now,
        });
    }
}
