using System.Globalization;
using System.Text.Json;
using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Microsoft.Extensions.Logging;

namespace Bytex.Adapters.Gate;

/// <summary>
/// What Gate's two derivative markets are, measured against the live venue on 2026-09-25.
/// <para>
/// The venue splits its derivatives by SETTLEMENT CURRENCY in the path - <c>/futures/usdt</c>, <c>/futures/btc</c>,
/// <c>/futures/usd1</c>, <c>/delivery/usdt</c> - and each of those is a market of its own with its own contract list.
/// This adapter offers the two USDT-settled ones, and the reasons the others are left out are recorded here rather
/// than in a commit message, because "the venue has no such market" and "this adapter does not offer it" are
/// different facts and only one of them is true.
/// </para>
/// <para>
/// <c>/futures/btc</c> held exactly one contract, BTC_USD, and the venue reports its <c>type</c> as <c>inverse</c>
/// with a <c>quanto_multiplier</c> of zero. An inverse contract is quoted in USD and settles in the base currency,
/// so a quantity of one cannot be expressed in base units at all without a price - the same reason KuCoin's inverse
/// contracts are left out of that adapter. <c>/futures/usd1</c> held nine linear contracts settled in USD1, which
/// this adapter could carry; it is not offered because a venue declares each instrument class in exactly one family
/// and those nine are perpetual swaps, as the 1013 USDT-settled ones are. <c>/delivery/btc</c> answered 200 with an
/// empty list: the market exists and holds nothing.
/// </para>
/// </summary>
public static class GateFuturesVenue
{
    /// <summary>
    /// Where the derivatives answer when nothing is configured. The general host serves them too, but this one
    /// serves ONLY them - it answers 404 for <c>/spot/currency_pairs</c> - which is what makes it the honest default
    /// for a derivative family and what tells the families apart for anything reading the declaration.
    /// </summary>
    public const string DefaultHttpBase = "https://fx-api.gateio.ws";

    /// <summary>
    /// The perpetual socket, settlement currency and all. The address carries the settle currency in its path, so
    /// there is one of these per settled market rather than one for the venue.
    /// </summary>
    public const string DefaultWsBase = "wss://fx-ws.gateio.ws/v4/ws/usdt";

    /// <summary>The delivery socket, which is a different path on the same host.</summary>
    public const string DefaultDeliveryWsBase = "wss://fx-ws.gateio.ws/v4/ws/delivery/usdt";

    /// <summary>
    /// The settlement currency both offered families are keyed by, which appears in every path they use. A named
    /// constant rather than a literal in nine paths, because the venue's other settled markets are real and a path
    /// with the wrong one in it answers about a different market rather than failing.
    /// </summary>
    public const string Settle = "usdt";

    /// <summary>The path prefix of the perpetual market.</summary>
    public const string FuturesPrefix = "/futures/" + Settle;

    /// <summary>The path prefix of the dated market.</summary>
    public const string DeliveryPrefix = "/delivery/" + Settle;

    /// <summary>
    /// The status the venue gives a contract that can be traded. Measured: all 1013 USDT perpetuals were in it, and
    /// <c>in_delisting</c> was false on every one - but both are checked, because a contract being wound down is
    /// exactly the one a strategy must not be handed.
    /// </summary>
    public const string Trading = "trading";

    /// <summary>
    /// What the venue reports for a linear contract - settled in the quote currency. The BTC-settled market reports
    /// <c>inverse</c> instead, and the class is taken from this field and the endpoint rather than from how a symbol
    /// is spelled: BTC_USD and BTC_USDT differ by one character and by everything that matters.
    /// </summary>
    public const string LinearType = "direct";

    /// <summary>
    /// The most candle rows one request answers with on either derivative market, measured by asking for more: 2001
    /// is refused with <c>INVALID_PARAM_VALUE</c> and 2000 is answered. Twice the spot cap, on the same venue.
    /// </summary>
    public const int CandlePage = 2000;

    /// <summary>
    /// The widest <c>from</c>..<c>to</c> window to ask a derivative market for, counted in intervals. Both ends are
    /// inclusive, so this is one less than the page.
    /// <para>
    /// The two markets punish a wider window differently and one of the two punishments is silent. Delivery refuses
    /// it: a span of 2000 intervals comes back <c>INVALID_PARAM_VALUE</c> naming <c>from</c>. Perpetual futures
    /// answers it - with the newest 2001 rows and no indication that the front of the window was dropped. A fetch
    /// that asked for six months in one call would get the last day and a half and be told nothing, which is why the
    /// window is sized here rather than left to the venue.
    /// </para>
    /// </summary>
    public const int CandleSpan = CandlePage - 1;

    /// <summary>
    /// What one contract is worth in base currency, kept on the instrument so an order can be denominated in it. The
    /// venue publishes it as <c>quanto_multiplier</c>, and it ranges from 0.0001 to 10000000 across the family.
    /// </summary>
    public const string MultiplierInfo = "contractMultiplier";

    /// <summary>The settlement cycle of a dated contract - BI-WEEKLY, QUARTERLY - as the venue names it.</summary>
    public const string CycleInfo = "deliveryCycle";

    /// <summary>
    /// The value the leverage endpoint takes to mean CROSS margin, as a string because that is how the venue types
    /// the parameter. Any positive number is isolated margin at that leverage; zero is cross, and cross wants the
    /// ceiling in <see cref="CrossLeverageLimitParameter"/> instead.
    /// </summary>
    public const string CrossMarginLeverage = "0";

    /// <summary>The query parameter that carries the isolated-margin leverage.</summary>
    public const string LeverageParameter = "leverage";

    /// <summary>
    /// The query parameter that carries the cross-margin ceiling, which the venue accepts only while
    /// <see cref="LeverageParameter"/> is zero. Declared because the pair is a single decision with two fields: a
    /// host that set one without the other would get a refusal that names neither.
    /// </summary>
    public const string CrossLeverageLimitParameter = "cross_leverage_limit";

    /// <summary>
    /// The instrument id of a contract, which is the venue's own name unchanged. Gate names a perpetual
    /// <c>BTC_USDT</c> and a dated contract <c>BTC_USDT_20261009</c>, and neither carries a marker of what it is -
    /// so the class comes from the endpoint the contract was read from and from the venue's own <c>type</c> and
    /// <c>expire_time</c> fields, never from the name.
    /// </summary>
    public static InstrumentId ToInstrumentId(string rawSymbol) => GateVenue.ToInstrumentId(rawSymbol);

    /// <inheritdoc cref="GateVenue.ToRawSymbol"/>
    public static string ToRawSymbol(InstrumentId id) => GateVenue.ToRawSymbol(id);

    /// <summary>The path prefix of the market a client is pointed at.</summary>
    public static string Prefix(GateProductType product) => product switch
    {
        GateProductType.Futures => FuturesPrefix,
        GateProductType.Delivery => DeliveryPrefix,
        _ => throw new ArgumentOutOfRangeException(nameof(product), product, "not one of Gate's derivative markets"),
    };

    /// <summary>
    /// What one contract of this instrument is worth in its base currency - 0.0001 BTC on BTC_USDT. Every quantity
    /// crossing into or out of these two markets goes through it.
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
            $"{instrument.Id} carries no Gate contract size, so nothing can say how many contracts a quantity of it "
            + "is. It was not loaded by one of this venue's derivative instrument providers.");
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
        if (Math.Abs(contracts - whole) > ContractTolerance)
        {
            throw new ArgumentException(
                $"{quantity.Value} of {instrument.Id} is {contracts} contracts, and this venue trades whole ones. "
                + $"Round the quantity to the instrument's size increment of {multiplier} first.",
                nameof(quantity));
        }

        return (long)whole;
    }

    /// <summary>
    /// How far a quantity may miss a whole number of contracts before it is refused. It exists only to absorb the
    /// last place of a decimal division; anything larger is a real mis-sizing and has to be said out loud.
    /// </summary>
    private const decimal ContractTolerance = 0.0000000001m;

    /// <summary>A number of contracts as a quantity in base currency, which is how everything above the adapter reads it.</summary>
    public static Quantity ToQuantity(Instrument instrument, decimal contracts) =>
        instrument.MakeQuantity(contracts * Multiplier(instrument));

    /// <summary>
    /// The initial margin rate the venue charges on the smallest position in a contract, taken from the contract's
    /// own maximum leverage.
    /// <para>
    /// The venue publishes no initial-margin field on a contract, and it publishes the rate per RISK-LIMIT TIER at
    /// <c>/futures/{settle}/risk_limit_tiers</c>, whose first tier is the one a position starts in. Measured on five
    /// contracts spanning the family - BTC_USDT, ETH_USDT, ARIA_USDT, AAPL_USDT, XAU_USDT - tier one's
    /// <c>initial_rate</c> equals one over the contract's <c>leverage_max</c> exactly, and tier one's
    /// <c>maintenance_rate</c> equals the contract's own <c>maintenance_rate</c> exactly. So this is the venue's own
    /// number rather than a guess, and it is read from the contract already in hand rather than costing a second
    /// request per instrument across 1013 of them.
    /// </para>
    /// <para>
    /// It is the tier-one rate, which is what an instrument-level figure can mean: the rate rises with the size of
    /// the position and the tier table is the authority for a large one.
    /// </para>
    /// </summary>
    public static decimal MarginInit(decimal leverageMax) => leverageMax > 0m ? 1m / leverageMax : 0m;
}

/// <summary>
/// Loads the USDT-settled perpetual contracts from <c>/futures/usdt/contracts</c>.
/// <para>
/// A contract is a fraction of the base currency - 0.0001 BTC on BTC_USDT - and an order is a whole number of them,
/// so the instrument's size increment is published in base currency exactly as Binance and Bybit publish theirs and
/// the adapter converts. A strategy sizing in base units is then the same strategy on all four venues.
/// </para>
/// <para>
/// Inverse contracts are left out, and none of this market's 1013 contracts is one: every one reported
/// <c>type: direct</c>. The check is here anyway because "this market is linear" is a fact about today's listing and
/// not about the endpoint, and an inverse contract published as a linear one would be sized in base units it cannot
/// be sized in.
/// </para>
/// <para>
/// What is NOT filtered is the underlying. The venue's <c>contract_type</c> field said <c>stocks</c> on 397 of the
/// contracts, <c>indices</c> on 18, <c>metals</c> on 12, <c>forex</c> on 4 and <c>commodities</c> on 3, with the
/// remaining 579 blank for crypto. All of them are USDT-settled perpetual swaps traded on the same market with the
/// same fields, so all of them are published; the engine's perpetual type fixes the asset class at crypto, which
/// understates what a tokenised equity perpetual tracks and is recorded here rather than worked around.
/// </para>
/// </summary>
public sealed class GateFuturesInstrumentProvider : InstrumentProviderBase
{
    private readonly GateHttp _http;

    public GateFuturesInstrumentProvider(GateHttp http, InstrumentProviderConfig? config = null, ILogger? logger = null)
        : base(GateVenue.Venue, config, logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public override async Task LoadAllAsync(CancellationToken ct, IReadOnlyDictionary<string, string>? filters = null)
    {
        JsonElement data = await _http
            .GetPublicAsync(GateFuturesVenue.FuturesPrefix + "/contracts", null, ct)
            .ConfigureAwait(false);

        int loaded = 0;
        int inverse = 0;
        foreach (JsonElement item in data.EnumerateArray())
        {
            if (!item.Str("type").Equals(GateFuturesVenue.LinearType, StringComparison.Ordinal))
            {
                inverse++;
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
            "Loaded {Count} Gate USDT-settled perpetual contracts, leaving out {Inverse} inverse ones this adapter does not offer",
            loaded,
            inverse);
    }

    public override async Task LoadAsync(InstrumentId id, CancellationToken ct)
    {
        JsonElement item;
        try
        {
            item = await _http
                .GetPublicAsync(
                    GateFuturesVenue.FuturesPrefix + "/contracts/" + Uri.EscapeDataString(GateFuturesVenue.ToRawSymbol(id)),
                    null,
                    ct)
                .ConfigureAwait(false);
        }
        catch (GateApiException e) when (GateVenue.ErrorsThatMeanNoSuchInstrument.Contains(e.Label))
        {
            Log.LogInformation("Gate does not list the perpetual contract {Instrument}", id);
            return;
        }

        if (item.ValueKind == JsonValueKind.Object
            && item.Str("type").Equals(GateFuturesVenue.LinearType, StringComparison.Ordinal)
            && Parse(item) is { } instrument
            && instrument.Id == id)
        {
            Add(instrument);
        }
    }

    private static Instrument? Parse(JsonElement item)
    {
        if (!item.Str("status").Equals(GateFuturesVenue.Trading, StringComparison.Ordinal) || item.Bool("in_delisting"))
        {
            return null;
        }

        string raw = item.Str("name");
        decimal tick = item.Dec("order_price_round");
        decimal multiplier = item.Dec("quanto_multiplier");
        if (raw.Length == 0 || tick <= 0m || multiplier <= 0m)
        {
            return null;
        }

        (Currency baseCurrency, Currency quote) = GateContracts.Currencies(raw);

        // One contract is `multiplier` of the base currency and an order is a whole number of them, so the smallest
        // tradable quantity - and the step between tradable quantities - is that, in base currency.
        byte pricePrecision = Json.Precision(tick);
        byte sizePrecision = Json.Precision(multiplier);
        decimal minContracts = item.Dec("order_size_min");
        decimal maxContracts = item.Dec("order_size_max");
        decimal leverageMax = item.Dec("leverage_max");
        UnixNanos now = UnixNanos.FromDateTimeOffset(DateTimeOffset.UtcNow);

        return new CryptoPerpetual(new InstrumentSpec
        {
            Id = GateFuturesVenue.ToInstrumentId(raw),
            RawSymbol = new Symbol(raw),
            AssetClass = AssetClass.Crypto,
            InstrumentClass = InstrumentClass.Swap,
            QuoteCurrency = quote,
            BaseCurrency = baseCurrency,
            SettlementCurrency = quote,
            PricePrecision = pricePrecision,
            SizePrecision = sizePrecision,
            PriceIncrement = new Price(tick, pricePrecision),
            SizeIncrement = new Quantity(multiplier, sizePrecision),

            // The venue reports a minimum of zero on the fourteen contracts it has enabled fractional sizes on, and
            // one contract everywhere else. Zero is not a size anything can send, so the increment stands in.
            MinQuantity = new Quantity(Math.Max(minContracts, 1m) * multiplier, sizePrecision),
            MaxQuantity = maxContracts > 0m ? new Quantity(maxContracts * multiplier, sizePrecision) : null,

            // One, because a quantity is already in base currency by the time anything above the adapter sees it, so
            // notional is quantity times price here exactly as it is on the other venues. The venue's own contract
            // size is in Info below, where the adapter reads it and nothing else has to.
            Multiplier = new Quantity(1m, 0),

            // The venue's own rates, and the maker one is NEGATIVE on most of this family - a rebate. It is carried
            // as the venue states it, because an instrument's own rate is what a fill is priced with.
            MakerFee = item.Dec("maker_fee_rate"),
            TakerFee = item.Dec("taker_fee_rate"),
            MarginInit = GateFuturesVenue.MarginInit(leverageMax),
            MarginMaint = item.Dec("maintenance_rate"),

            // The contract's own leverage_max, so per contract - and a zero says the contract published none.
            MarginSource = leverageMax > 0m ? MarginSource.VenuePerContract : MarginSource.VenueSilent,
            Info = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [GateFuturesVenue.MultiplierInfo] = multiplier.ToString(CultureInfo.InvariantCulture),
            },
            TsEvent = now,
            TsInit = now,
        });
    }
}

/// <summary>
/// Loads the USDT-settled dated contracts from <c>/delivery/usdt/contracts</c>.
/// <para>
/// A separate provider from the perpetual one rather than a flag inside it, because what these contracts are is
/// different: they expire, they are not charged funding, and the venue's own object has no funding fields at all -
/// no rate, no interval, no next settlement. The expiry is the venue's <c>expire_time</c>, in SECONDS.
/// </para>
/// <para>
/// The venue publishes no activation time for a dated contract - there is no create, launch or list field on the
/// object - so the activation reported here is the absence rather than a date nobody published.
/// </para>
/// </summary>
public sealed class GateDeliveryInstrumentProvider : InstrumentProviderBase
{
    private readonly GateHttp _http;

    public GateDeliveryInstrumentProvider(GateHttp http, InstrumentProviderConfig? config = null, ILogger? logger = null)
        : base(GateVenue.Venue, config, logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public override async Task LoadAllAsync(CancellationToken ct, IReadOnlyDictionary<string, string>? filters = null)
    {
        JsonElement data = await _http
            .GetPublicAsync(GateFuturesVenue.DeliveryPrefix + "/contracts", null, ct)
            .ConfigureAwait(false);

        int loaded = 0;
        foreach (JsonElement item in data.EnumerateArray())
        {
            if (!item.Str("type").Equals(GateFuturesVenue.LinearType, StringComparison.Ordinal))
            {
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

        Log.LogInformation("Loaded {Count} Gate USDT-settled dated contracts", loaded);
    }

    public override async Task LoadAsync(InstrumentId id, CancellationToken ct)
    {
        JsonElement item;
        try
        {
            item = await _http
                .GetPublicAsync(
                    GateFuturesVenue.DeliveryPrefix + "/contracts/" + Uri.EscapeDataString(GateFuturesVenue.ToRawSymbol(id)),
                    null,
                    ct)
                .ConfigureAwait(false);
        }
        catch (GateApiException e) when (GateVenue.ErrorsThatMeanNoSuchInstrument.Contains(e.Label))
        {
            Log.LogInformation("Gate does not list the dated contract {Instrument}", id);
            return;
        }

        if (item.ValueKind == JsonValueKind.Object && Parse(item) is { } instrument && instrument.Id == id)
        {
            Add(instrument);
        }
    }

    private static Instrument? Parse(JsonElement item)
    {
        if (item.Bool("in_delisting"))
        {
            return null;
        }

        string raw = item.Str("name");
        decimal tick = item.Dec("order_price_round");
        decimal multiplier = item.Dec("quanto_multiplier");
        long expireSeconds = item.Long("expire_time");
        if (raw.Length == 0 || tick <= 0m || multiplier <= 0m || expireSeconds <= 0)
        {
            return null;
        }

        // The venue names what a dated contract tracks in `underlying` - "ETH_USDT" - which is its own statement
        // about the pair rather than a reading of the contract's name, and the name carries a date the pair does not.
        (Currency baseCurrency, Currency quote) = GateContracts.Currencies(
            item.Str("underlying") is { Length: > 0 } underlying ? underlying : raw);

        byte pricePrecision = Json.Precision(tick);
        byte sizePrecision = Json.Precision(multiplier);
        decimal minContracts = item.Dec("order_size_min");
        decimal maxContracts = item.Dec("order_size_max");
        decimal leverageMax = item.Dec("leverage_max");
        UnixNanos now = UnixNanos.FromDateTimeOffset(DateTimeOffset.UtcNow);

        return new CryptoFuture(
            new InstrumentSpec
            {
                Id = GateFuturesVenue.ToInstrumentId(raw),
                RawSymbol = new Symbol(raw),
                AssetClass = AssetClass.Crypto,
                InstrumentClass = InstrumentClass.Future,
                QuoteCurrency = quote,
                BaseCurrency = baseCurrency,
                SettlementCurrency = quote,
                PricePrecision = pricePrecision,
                SizePrecision = sizePrecision,
                PriceIncrement = new Price(tick, pricePrecision),
                SizeIncrement = new Quantity(multiplier, sizePrecision),
                MinQuantity = new Quantity(Math.Max(minContracts, 1m) * multiplier, sizePrecision),
                MaxQuantity = maxContracts > 0m ? new Quantity(maxContracts * multiplier, sizePrecision) : null,
                Multiplier = new Quantity(1m, 0),
                MakerFee = item.Dec("maker_fee_rate"),
                TakerFee = item.Dec("taker_fee_rate"),
                MarginInit = GateFuturesVenue.MarginInit(leverageMax),
                MarginMaint = item.Dec("maintenance_rate"),

                // As the perpetual family above: the dated contract's own ceiling, or a gap where it published none.
                MarginSource = leverageMax > 0m ? MarginSource.VenuePerContract : MarginSource.VenueSilent,
                Info = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [GateFuturesVenue.MultiplierInfo] = multiplier.ToString(CultureInfo.InvariantCulture),
                    [GateFuturesVenue.CycleInfo] = item.Str("cycle"),
                },
                TsEvent = now,
                TsInit = now,
            },
            baseCurrency,

            // The venue publishes no activation for a dated contract, and nothing here will invent one.
            default,
            UnixNanos.FromSeconds(expireSeconds));
    }
}

/// <summary>
/// The pair behind a contract's name, which is the one thing about these markets that has to be read off a string.
/// The venue publishes a contract's <c>name</c> and, for a dated contract, its <c>underlying</c>, and neither comes
/// with separate base and quote fields the way a spot pair does.
/// <para>
/// Only the CURRENCIES are read this way. What kind of instrument it is comes from the endpoint it was loaded from
/// and from the venue's own <c>type</c> and <c>expire_time</c> fields, never from the spelling - a rule a test in
/// this repository enforces, and one that matters here because BTC_USDT and BTC_USDT_20261009 differ only in the
/// name while being a perpetual and a dated contract.
/// </para>
/// </summary>
internal static class GateContracts
{
    /// <summary>
    /// The base and quote currencies of <paramref name="name"/>, which the venue spells BASE_QUOTE with an
    /// underscore and may follow with a settlement date. A name with no underscore at all cannot be split, and the
    /// whole of it is taken as the base with the settlement currency as the quote - which is what the two offered
    /// families settle in, so nothing is guessed.
    /// </summary>
    public static (Currency Base, Currency Quote) Currencies(string name)
    {
        string[] parts = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
        Currency quote = Currency.FromCode(
            parts.Length >= 2 ? parts[1] : GateFuturesVenue.Settle.ToUpperInvariant(),
            8);

        return (Currency.FromCode(parts.Length >= 1 ? parts[0] : name, 8), quote);
    }
}
