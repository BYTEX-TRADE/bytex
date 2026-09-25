using System.Globalization;
using System.Text.Json;
using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Microsoft.Extensions.Logging;

namespace Bytex.Adapters.Kraken;

/// <summary>
/// What Kraken's futures platform is, measured against the live venue on 2026-09-25 across all 300 contracts it
/// listed. Its public endpoints need no key either, so nothing here is read off documentation - and in three places
/// the documentation and the venue disagree, each noted where it matters.
/// </summary>
public static class KrakenFuturesVenue
{
    /// <summary>
    /// Where the futures platform answers. A different host from spot, not a different path on the same one, and it
    /// serves three separate APIs: the derivatives API under <see cref="DerivativesPrefix"/>, the charts service
    /// under <see cref="ChartsPath"/>, and the socket.
    /// </summary>
    public const string DefaultHttpBase = "https://futures.kraken.com";

    /// <summary>
    /// Where the futures socket answers. Unlike spot, one host serves both the public feeds and the private ones:
    /// the private feeds are opened on this same connection after a challenge is signed.
    /// </summary>
    public const string DefaultWsBase = "wss://futures.kraken.com";

    /// <summary>The socket path. Version 1 is the current one; there is no version 2 on this platform.</summary>
    public const string WsPath = "/ws/v1";

    /// <summary>
    /// The prefix every derivatives path carries and the request signature does NOT. The signature is taken over the
    /// path with this removed, which is the part of the scheme easiest to get wrong.
    /// </summary>
    public const string DerivativesPrefix = "/derivatives";

    /// <summary>The derivatives API version in use for everything except funding history.</summary>
    public const string ApiV3 = DerivativesPrefix + "/api/v3";

    /// <summary>
    /// The version funding history answers on, and the reason this is a separate constant. The documented v3 path
    /// <c>/derivatives/api/v3/historicalfundingrates</c> answers 404 NOT_FOUND for every symbol; the same request
    /// under v4 answers with the rates. A reader following the documentation gets a 404 that looks like an unlisted
    /// symbol.
    /// </summary>
    public const string ApiV4 = DerivativesPrefix + "/api/v4";

    /// <summary>Where the candle service answers. Not under the derivatives API and not sharing its envelope.</summary>
    public const string ChartsPath = "/api/charts/v1";

    /// <summary>
    /// Which price a candle is built from. The service offers <c>trade</c>, <c>mark</c> and <c>spot</c>; only
    /// <c>trade</c> carries a volume - the other two measured zero on every row - so it is the only one that
    /// produces a bar rather than a price series.
    /// </summary>
    public const string TradeCandles = "trade";

    /// <summary>
    /// The most candle rows one charts request answers with. Measured: a request spanning 6000 minutes of
    /// one-minute candles returned exactly 2000 rows with <c>more_candles: true</c>, and so did one spanning 2500.
    /// The service documents 5000. A loop written to the documented number would ask for 5000, get 2000, and take
    /// the short page as the end of the venue's history.
    /// </summary>
    public const int CandlePage = 2000;

    /// <summary>
    /// Requests this adapter allows itself in <see cref="RequestWindow"/>. The platform publishes a cost-based
    /// budget per endpoint group that nothing unauthenticated can read, so this is a conservative rate rather than
    /// the measured ceiling.
    /// </summary>
    public const int RequestsPerWindow = 100;

    /// <summary>The window <see cref="RequestsPerWindow"/> is counted over.</summary>
    public static readonly TimeSpan RequestWindow = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The contract type the venue gives its USD-quoted, multi-collateral contracts - what it calls flexible
    /// futures, and the only type this adapter offers.
    /// </summary>
    public const string FlexibleFutures = "flexible_futures";

    /// <summary>
    /// The contract type the venue gives its coin-margined contracts. Not offered: they are quoted in USD and
    /// settled in the base currency, so a quantity of one cannot be expressed in base units without a price, which
    /// is the one thing this adapter promises everything above it. Twelve of the venue's 300 are of this type.
    /// </summary>
    public const string InverseFutures = "futures_inverse";

    /// <summary>
    /// The field that says a contract expires, and the ONLY thing on this venue that distinguishes a perpetual from
    /// a dated contract.
    /// <para>
    /// The type field does not: <c>flexible_futures</c> covers both <c>PF_XBTUSD</c>, which never expires, and
    /// <c>FF_XBTUSD_261225</c>, which expires in December - 278 perpetuals and 10 dated contracts share it. Reading
    /// the class off the symbol prefix would work today and is a convention rather than a fact, and reading it off
    /// the type would publish a dated contract as a perpetual: the wrong class, no expiry recorded, and funding
    /// assumed on something that pays none.
    /// </para>
    /// </summary>
    public const string ExpiryField = "lastTradingTime";

    /// <summary>The field carrying the tier threshold on a coin-margined contract's margin schedule.</summary>
    public const string ContractTierField = "contracts";

    /// <summary>
    /// The field carrying it on a flexible futures contract's schedule. The same array means the same thing and is
    /// keyed by a differently named field depending on the contract type, so a reader that knows only one name finds
    /// no tiers at all on half the venue - and a margin of zero reads as a contract that needs none.
    /// </summary>
    public const string UnitTierField = "numNonContractUnits";

    /// <summary>The fee schedule an instrument's <c>feeScheduleUid</c> points into, on a public endpoint.</summary>
    public const string FeeSchedulesPath = ApiV3 + "/feeschedules";

    /// <summary>
    /// What a fee schedule's numbers are divided by. The platform publishes them as PERCENTAGES - the schedule
    /// <c>PF_XBTUSD</c> points at carries <c>makerFee: 0.02</c> for two basis points - where everything in this
    /// engine is a fraction of a trade. Taken as written, every commission on this platform would be a hundred times
    /// what the venue charges.
    /// </summary>
    public const decimal FeePercent = 100m;

    /// <summary>
    /// The volume tier a fee schedule starts at, which is the rate to publish before an account is known.
    /// </summary>
    public const decimal FirstFeeTierVolume = 0m;

    /// <summary>What the flexible futures family charges at the first tier, measured from the schedule it points at.</summary>
    public const decimal DefaultMakerFee = 0.0002m;

    /// <inheritdoc cref="DefaultMakerFee"/>
    public const decimal DefaultTakerFee = 0.0005m;

    /// <summary>
    /// How often a perpetual settles funding here. Measured: the funding history of <c>PF_XBTUSD</c> came back with
    /// 8786 rows one hour apart. Most venues settle every eight hours, so a caller assuming that would price a
    /// perpetual's carry at an eighth of what it is.
    /// </summary>
    public static readonly TimeSpan FundingInterval = TimeSpan.FromHours(1);

    /// <summary>
    /// The relative rate is the one to publish. The funding endpoint carries two numbers per settlement:
    /// <c>fundingRate</c>, which is an absolute amount per contract in the settlement currency - 1.33 on
    /// <c>PF_XBTUSD</c> and 1.5e-10 on <c>PI_XBTUSD</c> at the same hour - and <c>relativeFundingRate</c>, which is
    /// the fraction of notional a position is charged. Only the second is a rate, and only the second is comparable
    /// with what every other venue here publishes.
    /// </summary>
    public const string RelativeRateField = "relativeFundingRate";

    /// <summary>
    /// Where a leverage preference is set, and the field of it. Setting a maximum leverage for a symbol ALSO selects
    /// isolated margin for that symbol; deleting the preference selects cross margin. The two are one call on this
    /// venue and cannot be chosen separately.
    /// </summary>
    public const string LeveragePreferencesPath = ApiV3 + "/leveragepreferences";

    /// <summary>The maximum leverage an order may use, once a preference selects isolated margin for the symbol.</summary>
    public const string MaxLeverageField = "maxLeverage";

    /// <summary>Where an order is placed, amended and cancelled.</summary>
    public const string SendOrderPath = ApiV3 + "/sendorder";

    /// <inheritdoc cref="SendOrderPath"/>
    public const string EditOrderPath = ApiV3 + "/editorder";

    /// <inheritdoc cref="SendOrderPath"/>
    public const string CancelOrderPath = ApiV3 + "/cancelorder";

    /// <inheritdoc cref="SendOrderPath"/>
    public const string CancelAllOrdersPath = ApiV3 + "/cancelallorders";

    /// <summary>Where the account, its open orders, its fills and its positions are read.</summary>
    public const string AccountsPath = ApiV3 + "/accounts";

    /// <inheritdoc cref="AccountsPath"/>
    public const string OpenOrdersPath = ApiV3 + "/openorders";

    /// <inheritdoc cref="AccountsPath"/>
    public const string FillsPath = ApiV3 + "/fills";

    /// <inheritdoc cref="AccountsPath"/>
    public const string OpenPositionsPath = ApiV3 + "/openpositions";

    /// <summary>The catalog, which needs no key.</summary>
    public const string InstrumentsPath = ApiV3 + "/instruments";

    /// <summary>
    /// The private socket feeds a trading node needs: its orders, its fills, its balances and its positions. Each is
    /// subscribed with a signed challenge rather than with a token fetched over REST.
    /// </summary>
    public static readonly IReadOnlyList<string> PrivateFeeds =
    [
        "open_orders",
        "fills",
        "balances",
        "open_positions",
    ];

    /// <summary>
    /// The multi-collateral account every flexible futures contract is margined out of. The platform keeps several
    /// accounts under one key - one per coin-margined collateral plus this one - and answers all of them in one
    /// call, so this names the one whose balances belong to this family.
    /// </summary>
    public const string MultiCollateralAccount = "flex";

    /// <summary>
    /// A contract's symbol is its instrument id here. The venue's own spelling is kept exactly - including the XBT
    /// it writes bitcoin as on this platform, where its spot platform writes BTC - so an id can be turned into a
    /// request with no catalog loaded and nothing renames a currency on the way.
    /// </summary>
    public static InstrumentId ToInstrumentId(string rawSymbol) => new(new Symbol(rawSymbol), KrakenVenue.Venue);

    /// <inheritdoc cref="ToInstrumentId"/>
    public static string ToRawSymbol(InstrumentId id) => id.Symbol.Value;

    /// <summary>
    /// What the candle service and the socket call a bar of the given length. Measured against both: each accepts
    /// 1m, 5m, 15m, 30m, 1h, 4h, 12h, 1d and 1w and refuses everything else - the service with an HTTP 400 reading
    /// "Invalid resolution", the socket with "Couldn't subscribe to invalid feed".
    /// <para>
    /// One trap: the service matches a resolution case-insensitively, so <c>1M</c> is accepted and silently means
    /// one MINUTE rather than one month. A caller asking for monthly candles would receive minutes and no error, so
    /// no monthly length is offered here at all.
    /// </para>
    /// </summary>
    public static string Resolution(BarSpecification spec) => (spec.Aggregation, spec.Step) switch
    {
        (BarAggregation.Minute, 1) => "1m",
        (BarAggregation.Minute, 5) => "5m",
        (BarAggregation.Minute, 15) => "15m",
        (BarAggregation.Minute, 30) => "30m",
        (BarAggregation.Hour, 1) => "1h",
        (BarAggregation.Hour, 4) => "4h",
        (BarAggregation.Hour, 12) => "12h",
        (BarAggregation.Day, 1) => "1d",
        (BarAggregation.Week, 1) => "1w",
        _ => throw new NotSupportedException(
            $"Kraken futures does not keep {spec} candles. It keeps 1, 5, 15 and 30 minutes, 1, 4 and 12 hours, a "
            + "day and a week."),
    };

    /// <summary>
    /// The initial and maintenance margin rates a contract really charges, from the first tier of the schedule the
    /// venue publishes on it, and the maximum leverage that first tier implies.
    /// <para>
    /// This is a schedule and not a number. <c>PF_XBTUSD</c> starts at one percent initial and half a percent
    /// maintenance and rises through eight tiers to fifty percent as a position grows; <c>PI_XBTUSD</c> starts at
    /// two and one through seven tiers. Publishing a fixed figure would be wrong on almost every contract and wrong
    /// in both directions - and the first tier is the honest one to publish, because it is what a position is
    /// charged until it is large enough to move up.
    /// </para>
    /// </summary>
    public static (decimal Initial, decimal Maintenance, decimal MaxLeverage) FirstMarginTier(JsonElement instrument)
    {
        if (!instrument.TryGetProperty("marginLevels", out JsonElement levels) || levels.ValueKind != JsonValueKind.Array)
        {
            return (0m, 0m, 0m);
        }

        decimal initial = 0m;
        decimal maintenance = 0m;
        decimal lowest = decimal.MaxValue;
        foreach (JsonElement tier in levels.EnumerateArray())
        {
            // Whichever of the two names this contract type uses for the threshold; a tier that carries neither is
            // the first one on a schedule written some third way, and is read as the floor rather than skipped.
            decimal threshold = tier.Has(ContractTierField)
                ? tier.Dec(ContractTierField)
                : tier.Has(UnitTierField) ? tier.Dec(UnitTierField) : 0m;

            if (threshold >= lowest)
            {
                continue;
            }

            lowest = threshold;
            initial = tier.Dec("initialMargin");
            maintenance = tier.Dec("maintenanceMargin");
        }

        return (initial, maintenance, initial > 0m ? decimal.Round(1m / initial, 4) : 0m);
    }

    /// <summary>
    /// The maker and taker rates of the schedule an instrument points at, as fractions. Returns null when the
    /// schedules do not carry the uid the instrument names, which leaves the family default to stand rather than
    /// publishing a zero fee - a backtest over a zero fee is the one that looks profitable and is not.
    /// </summary>
    public static (decimal Maker, decimal Taker)? FeesFor(JsonElement schedules, string uid)
    {
        if (uid.Length == 0 || schedules.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement schedule in schedules.EnumerateArray())
        {
            if (schedule.Str("uid") != uid
                || !schedule.TryGetProperty("tiers", out JsonElement tiers)
                || tiers.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement tier in tiers.EnumerateArray())
            {
                if (tier.Dec("usdVolume") == FirstFeeTierVolume)
                {
                    return (tier.Dec("makerFee") / FeePercent, tier.Dec("takerFee") / FeePercent);
                }
            }
        }

        return null;
    }
}

/// <summary>
/// Loads Kraken's futures contracts from <c>/derivatives/api/v3/instruments</c>, which needs no key.
/// <para>
/// Two things here are measured rather than assumed and both would otherwise be silent mistakes.
/// </para>
/// <para>
/// The catalog IGNORES its symbol filter. <c>?symbol=PF_XBTUSD</c> answered with all 300 contracts, and there is no
/// per-symbol endpoint - <c>/instruments/PF_XBTUSD</c> is a 404. This is the same defect Binance's USD-margined
/// futures has, and it is what turns "load this one instrument" into "load the venue" for every caller, every time.
/// So one instrument is loaded by fetching the catalog and keeping the one that was asked for.
/// </para>
/// <para>
/// A contract's class comes from its expiry field and NOT from its type or its symbol. See
/// <see cref="KrakenFuturesVenue.ExpiryField"/>: the type covers both perpetuals and dated contracts, so a reader
/// trusting it would publish a contract that expires in December as one that never does.
/// </para>
/// <para>
/// Coin-margined contracts are left out, because a quantity of one cannot be expressed in base currency without a
/// price. That is a statement about what this adapter offers, and it is what the declaration says.
/// </para>
/// </summary>
public sealed class KrakenFuturesInstrumentProvider : InstrumentProviderBase
{
    private readonly KrakenHttp _http;

    public KrakenFuturesInstrumentProvider(KrakenHttp http, InstrumentProviderConfig? config = null, ILogger? logger = null)
        : base(KrakenVenue.Venue, config, logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public override async Task LoadAllAsync(CancellationToken ct, IReadOnlyDictionary<string, string>? filters = null)
    {
        (JsonElement contracts, JsonElement schedules) = await CatalogAsync(ct).ConfigureAwait(false);
        int loaded = 0;
        int inverse = 0;
        foreach (JsonElement item in contracts.EnumerateArray())
        {
            if (Parse(item, schedules, ref inverse) is not { } instrument)
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
            "Loaded {Count} Kraken futures contracts, leaving out {Inverse} coin-margined ones this adapter does not offer",
            loaded,
            inverse);
    }

    public override async Task LoadAsync(InstrumentId id, CancellationToken ct)
    {
        (JsonElement contracts, JsonElement schedules) = await CatalogAsync(ct).ConfigureAwait(false);
        int ignored = 0;
        foreach (JsonElement item in contracts.EnumerateArray())
        {
            // Only the one that was asked for. The catalog answers with all 300 whatever is asked, so without this
            // a caller loading one contract would fill its provider with the venue - which is not a crash and so
            // would never be noticed.
            if (item.Str("symbol") != KrakenFuturesVenue.ToRawSymbol(id))
            {
                continue;
            }

            if (Parse(item, schedules, ref ignored) is { } instrument && instrument.Id == id)
            {
                Add(instrument);
            }

            return;
        }

        Log.LogInformation("Kraken futures does not list the contract {Instrument}", id);
    }

    /// <summary>
    /// The contracts and the fee schedules they point at. Two requests, because a contract carries the uid of its
    /// schedule and not its rates: without the second request every contract would be published at the family
    /// default, which is right for most of them and wrong for the ones on a rebate or a consumer schedule.
    /// </summary>
    private async Task<(JsonElement Contracts, JsonElement Schedules)> CatalogAsync(CancellationToken ct)
    {
        JsonElement catalog = await _http
            .GetPublicAsync(KrakenFuturesVenue.InstrumentsPath, null, ct)
            .ConfigureAwait(false);

        JsonElement contracts = catalog.TryGetProperty("instruments", out JsonElement list)
            ? list
            : default;

        JsonElement schedules = default;
        try
        {
            JsonElement fees = await _http
                .GetPublicAsync(KrakenFuturesVenue.FeeSchedulesPath, null, ct)
                .ConfigureAwait(false);

            if (fees.TryGetProperty("feeSchedules", out JsonElement published))
            {
                schedules = published;
            }
        }
        catch (Exception e) when (e is KrakenApiException or Bytex.Live.Network.VenueHttpException or HttpRequestException)
        {
            // The catalog is the instruments; the schedules only sharpen their rates. Losing them leaves every
            // contract at the family default, which is worth a warning and not worth failing a load for.
            Log.LogWarning(e, "Kraken futures fee schedules could not be read; contracts get the family default rates");
        }

        return (contracts, schedules);
    }

    private static Instrument? Parse(JsonElement item, JsonElement schedules, ref int inverse)
    {
        string raw = item.Str("symbol");
        string type = item.Str("type");
        if (raw.Length == 0 || !item.Bool("tradeable") || item.Bool("isExpired"))
        {
            return null;
        }

        if (type == KrakenFuturesVenue.InverseFutures)
        {
            inverse++;
            return null;
        }

        if (type != KrakenFuturesVenue.FlexibleFutures)
        {
            // A third contract type this adapter has never seen. Publishing it under a guessed class is how a
            // contract ends up traded as something it is not, so it is left out and counted as unoffered.
            inverse++;
            return null;
        }

        decimal tick = item.Dec("tickSize");
        if (tick <= 0m)
        {
            return null;
        }

        byte pricePrecision = Json.Precision(tick);
        byte sizePrecision = (byte)item.Long("contractValueTradePrecision");
        decimal step = Json.IncrementFor(sizePrecision);
        Currency baseCurrency = Currency.FromCode(item.Str("base"), 8);
        Currency quote = Currency.FromCode(item.Str("quote"), 8);
        decimal maxPosition = item.Dec("maxPositionSize");
        (decimal initial, decimal maintenance, decimal maxLeverage) = KrakenFuturesVenue.FirstMarginTier(item);
        (decimal Maker, decimal Taker)? fees = KrakenFuturesVenue.FeesFor(schedules, item.Str("feeScheduleUid"));
        UnixNanos now = UnixNanos.FromDateTimeOffset(DateTimeOffset.UtcNow);
        UnixNanos activation = item.Iso("openingDate");
        bool dated = item.Has(KrakenFuturesVenue.ExpiryField);

        InstrumentSpec spec = new()
        {
            Id = KrakenFuturesVenue.ToInstrumentId(raw),
            RawSymbol = new Symbol(raw),
            AssetClass = AssetClass.Crypto,
            InstrumentClass = dated ? InstrumentClass.Future : InstrumentClass.Swap,
            QuoteCurrency = quote,
            BaseCurrency = baseCurrency,

            // A flexible futures contract is margined out of a multi-collateral account and its profit and loss is
            // denominated in the quote currency, which is what the settlement currency means here.
            SettlementCurrency = quote,
            PricePrecision = pricePrecision,
            SizePrecision = sizePrecision,
            PriceIncrement = new Price(tick, pricePrecision),
            SizeIncrement = new Quantity(step, sizePrecision),
            MinQuantity = new Quantity(step, sizePrecision),
            MaxQuantity = maxPosition > 0m ? new Quantity(maxPosition, sizePrecision) : null,

            // One. A quantity on this family is already in base currency - the contract size is 1 on every contract
            // measured and a size is stated in base units - so notional is quantity times price exactly as on the
            // other venues.
            Multiplier = new Quantity(1m, 0),
            MakerFee = fees?.Maker ?? KrakenFuturesVenue.DefaultMakerFee,
            TakerFee = fees?.Taker ?? KrakenFuturesVenue.DefaultTakerFee,

            // From the venue's own schedule rather than from a number in this source. See FirstMarginTier.
            MarginInit = initial,
            MarginMaint = maintenance,

            // The ceiling the venue's first margin tier implies, as the typed fact every venue publishes it as. It
            // stays in Info too, because this adapter reads it from there for its own margin-mode reporting.
            MaxLeverage = maxLeverage > 0m ? maxLeverage : null,
            Info = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [MaxLeverageInfo] = maxLeverage.ToString(CultureInfo.InvariantCulture),
                [MarginTiersInfo] = item.TryGetProperty("marginLevels", out JsonElement levels) && levels.ValueKind == JsonValueKind.Array
                    ? levels.GetArrayLength().ToString(CultureInfo.InvariantCulture)
                    : "0",
            },
            TsEvent = now,
            TsInit = now,
        };

        return dated
            ? new CryptoFuture(spec, baseCurrency, activation, item.Iso(KrakenFuturesVenue.ExpiryField))
            : new CryptoPerpetual(spec);
    }

    /// <summary>
    /// The highest leverage this contract's first margin tier allows, kept on the instrument so that a client asked
    /// for more can say what the contract permits instead of letting the venue refuse an order for a reason nobody
    /// can read.
    /// </summary>
    public const string MaxLeverageInfo = "krakenMaxLeverage";

    /// <summary>
    /// How many tiers the contract's margin schedule has, so that a reader of the published initial margin knows it
    /// is the first of several and not the whole of the contract's requirement.
    /// </summary>
    public const string MarginTiersInfo = "krakenMarginTiers";
}
