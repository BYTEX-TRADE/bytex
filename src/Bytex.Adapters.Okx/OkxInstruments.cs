using System.Globalization;
using System.Text.Json;
using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Microsoft.Extensions.Logging;

namespace Bytex.Adapters.Okx;

/// <summary>
/// Loads one of OKX's three markets from <c>/api/v5/public/instruments</c>, and the margin each instrument really
/// requires from <c>/api/v5/public/position-tiers</c>.
/// <para>
/// One provider for three markets, because there is one endpoint for three markets: what changes is the
/// <c>instType</c> on the request. The instrument class comes from the <c>instType</c> the venue puts on each record
/// and never from how the id is spelled - which matters more here than on any other venue this engine speaks to,
/// because OKX's ids would mislead a reader who trusted them. <c>BTC-USD-SWAP</c> is a perpetual and
/// <c>BTC-USD-261030</c> is a dated contract; <c>BTC-USD_UM-261030</c> is linear and <c>BTC-USD-261030</c> is
/// inverse, and the only visible difference between those two is three characters inside the base name.
/// </para>
/// <para>
/// Inverse contracts are left out of both derivative markets, for the reason they are left out of KuCoin's: they are
/// quoted in USD and settle in the base currency, so a quantity of one cannot be expressed in base units without a
/// price, and every quantity crossing this boundary is in base units. Fifteen of the venue's 492 perpetuals and
/// twelve of its 244 dated contracts are inverse, counted on 2026-09-25.
/// </para>
/// <para>
/// Sizes are published in base currency on all three markets, exactly as Binance and Bybit publish theirs, so a
/// strategy sizing in base units is the same strategy here. On the derivative markets that means converting: the
/// venue counts an order in contracts, one contract is <c>ctVal</c> of <c>ctValCcy</c>, and the adapter multiplies
/// and divides so that nothing above it has to know.
/// </para>
/// </summary>
public sealed class OkxInstrumentProvider : InstrumentProviderBase
{
    /// <summary>The path all three markets are listed from; the <c>instType</c> is what differs.</summary>
    private const string InstrumentsPath = "/api/v5/public/instruments";

    /// <summary>
    /// Where the margin an instrument really requires comes from. The alternative - a hard-coded initial and
    /// maintenance rate - is a defect two of this engine's adapters shipped with, and the numbers it would have
    /// guessed are not the venue's: BTC-USDT's perpetual starts at 1 percent initial and 0.4 percent maintenance,
    /// while the linear BTC futures start at 5 and 2.
    /// </summary>
    private const string TiersPath = "/api/v5/public/position-tiers";

    private readonly OkxHttp _http;

    public OkxInstrumentProvider(OkxHttp http, OkxInstrumentType instrumentType, InstrumentProviderConfig? config = null, ILogger? logger = null)
        : base(OkxVenue.Venue, config, logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        InstrumentType = instrumentType;
    }

    /// <summary>Which of the venue's markets this provider lists.</summary>
    public OkxInstrumentType InstrumentType { get; }

    private string InstType => OkxVenue.InstType(InstrumentType);

    private bool IsDerivative => InstrumentType is OkxInstrumentType.Swap or OkxInstrumentType.Futures;

    public override async Task LoadAllAsync(CancellationToken ct, IReadOnlyDictionary<string, string>? filters = null)
    {
        JsonElement data = await _http
            .GetPublicAsync(InstrumentsPath, new Dictionary<string, string>(StringComparer.Ordinal) { ["instType"] = InstType }, ct)
            .ConfigureAwait(false);

        List<JsonElement> records = [];
        int inverse = 0;
        foreach (JsonElement item in Rows(data))
        {
            if (IsInverse(item))
            {
                inverse++;
                continue;
            }

            records.Add(item.Clone());
        }

        // The margin comes from a second endpoint, so it is fetched for every family being loaded before any
        // instrument is published - an instrument published without it and corrected later would be an instrument
        // two callers disagree about.
        IReadOnlyDictionary<string, OkxMargin> margins = await MarginsAsync(records.Select(Family), ct).ConfigureAwait(false);

        int loaded = 0;
        foreach (JsonElement item in records)
        {
            Instrument? instrument = Parse(item, margins.GetValueOrDefault(Family(item)));
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
            "Loaded {Count} OKX {Market} instruments, leaving out {Inverse} inverse ones this adapter does not offer",
            loaded,
            InstType,
            inverse);
    }

    public override async Task LoadAsync(InstrumentId id, CancellationToken ct)
    {
        JsonElement data;
        try
        {
            data = await _http.GetPublicAsync(
                InstrumentsPath,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["instType"] = InstType,
                    ["instId"] = OkxVenue.ToRawSymbol(id),
                },
                ct).ConfigureAwait(false);
        }
        catch (OkxApiException e) when (e.Code == OkxVenue.ErrorNoSuchInstrument)
        {
            // Not listed is an answer, not a failure, and this venue gives the same answer for an id that does not
            // exist anywhere and for one that exists in a different market - asked for the spot pair BTC-USDT under
            // instType=SWAP it refuses with this code. Either way the provider is left empty and a caller that looks
            // the instrument up afterwards finds nothing, which reads the same on every venue.
            Log.LogInformation("OKX does not list {Instrument} in its {Market} market", id, InstType);
            return;
        }

        foreach (JsonElement item in Rows(data))
        {
            if (IsInverse(item) || OkxVenue.ToInstrumentId(item.Str("instId")) != id)
            {
                // Only the instrument that was asked for. The venue honours its own instId filter - which was
                // measured, and is not true of every venue: one of them answers a single-symbol request with its
                // entire contract list - so this is a guard rather than a filter, and it is here because that
                // guard is what a caller relies on when it asks for one instrument and expects one.
                continue;
            }

            IReadOnlyDictionary<string, OkxMargin> margins = await MarginsAsync([Family(item)], ct).ConfigureAwait(false);
            if (Parse(item, margins.GetValueOrDefault(Family(item))) is { } instrument)
            {
                Add(instrument);
            }

            return;
        }
    }

    private static IEnumerable<JsonElement> Rows(JsonElement data) =>
        data.ValueKind == JsonValueKind.Array ? data.EnumerateArray() : [];

    /// <summary>
    /// Whether a record is a coin-margined contract. The venue's own <c>ctType</c>, never the id: the linear and the
    /// inverse BTC contracts of the same expiry differ by <c>_UM</c> in the middle of the name, and reading that
    /// would be reading a naming convention as a fact about margin.
    /// </summary>
    private static bool IsInverse(JsonElement item) =>
        item.Str("ctType").Equals("inverse", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The family a record's tiers are published under. It is <c>instFamily</c> on the derivative markets and empty
    /// on spot, which is what makes spot need no tier request at all.
    /// </summary>
    private static string Family(JsonElement item) => item.Str("instFamily");

    /// <summary>
    /// The tier-one margin for each of the given families.
    /// <para>
    /// The venue will not answer this by <c>instType</c> alone and will not answer it by <c>instId</c> either for a
    /// swap or a future - both were tried, and both come back "Either parameter instFamily or uly is required" - so
    /// the families are named, and at most <see cref="OkxVenue.TierFamilyPage"/> of them per request, because a sixth
    /// is refused outright. A full derivative catalog is therefore a page of instruments plus a request per five
    /// families: a hundred requests for the 492 perpetuals, which at the measured rate limit is about ten seconds.
    /// That is the price of publishing the venue's own margin instead of a number somebody chose.
    /// </para>
    /// <para>
    /// The tier is asked for by number so that one row comes back per family rather than ninety-nine.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyDictionary<string, OkxMargin>> MarginsAsync(IEnumerable<string> families, CancellationToken ct)
    {
        Dictionary<string, OkxMargin> margins = new(StringComparer.Ordinal);
        if (!IsDerivative)
        {
            // A cash spot trade posts no margin, so there is nothing to fetch and nothing to publish. The venue does
            // list margin tiers for these pairs, under instType=MARGIN - but that is its margin-trading product and
            // this family trades cash, so publishing those rates would say a spot purchase needs ten percent down.
            return margins;
        }

        string[] wanted = [.. families.Where(f => f.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        for (int i = 0; i < wanted.Length; i += OkxVenue.TierFamilyPage)
        {
            string[] page = [.. wanted.Skip(i).Take(OkxVenue.TierFamilyPage)];
            JsonElement data;
            try
            {
                data = await _http.GetPublicAsync(
                    TiersPath,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["instType"] = InstType,
                        ["tdMode"] = OkxVenue.MarginMode(OkxMarginMode.Cross),
                        ["tier"] = OkxVenue.FirstTier,
                        ["instFamily"] = string.Join(',', page),
                    },
                    ct).ConfigureAwait(false);
            }
            catch (OkxApiException e) when (e.Code == OkxVenue.ErrorParameter)
            {
                // A family the tier endpoint does not know, which would otherwise stop a whole catalog load over one
                // contract. The instruments of that family are still published; they carry no margin, which is
                // visible, rather than a guessed one, which is not.
                Log.LogWarning(
                    "OKX publishes no position tiers for {Families}: {Reason}. Those instruments carry no margin requirement",
                    string.Join(", ", page),
                    e.Msg);
                continue;
            }

            foreach (JsonElement row in Rows(data))
            {
                string family = Family(row);
                if (family.Length > 0)
                {
                    margins[family] = new OkxMargin(row.Dec("imr"), row.Dec("mmr"), row.Dec("maxLever"));
                }
            }
        }

        return margins;
    }

    /// <summary>
    /// One record as an instrument, or null when the venue is not trading it. The class comes from
    /// <c>instType</c>, which is the venue's own statement about what this is.
    /// </summary>
    private Instrument? Parse(JsonElement item, OkxMargin? margin)
    {
        if (!item.Str("state").Equals("live", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string instId = item.Str("instId");
        decimal tick = item.Dec("tickSz");
        decimal lot = item.Dec("lotSz");
        decimal minimum = item.Dec("minSz");
        if (instId.Length == 0 || tick <= 0m || lot <= 0m)
        {
            return null;
        }

        return item.Str("instType") switch
        {
            "SPOT" => Spot(item, instId, tick, lot, minimum),
            "SWAP" => Derivative(item, instId, tick, lot, minimum, margin, expiring: false),
            "FUTURES" => Derivative(item, instId, tick, lot, minimum, margin, expiring: true),

            // A market this adapter was not built for, on a shared endpoint that could return one: OPTION records
            // have the same shape and would parse into something plausible. Left out rather than guessed at.
            _ => null,
        };
    }

    private static Instrument Spot(JsonElement item, string instId, decimal tick, decimal lot, decimal minimum)
    {
        byte pricePrecision = Json.Precision(tick);
        byte sizePrecision = Json.Precision(lot);
        Currency quote = Currency.FromCode(item.Str("quoteCcy"), 8);
        Currency baseCurrency = Currency.FromCode(item.Str("baseCcy"), 8);
        decimal maxLimitSize = item.Dec("maxLmtSz");
        decimal maxLimitAmount = item.Dec("maxLmtAmt");
        UnixNanos now = UnixNanos.FromDateTimeOffset(DateTimeOffset.UtcNow);

        return new CurrencyPair(new InstrumentSpec
        {
            Id = OkxVenue.ToInstrumentId(instId),
            RawSymbol = new Symbol(instId),
            AssetClass = AssetClass.Crypto,
            InstrumentClass = InstrumentClass.Spot,
            QuoteCurrency = quote,
            BaseCurrency = baseCurrency,
            SettlementCurrency = quote,
            PricePrecision = pricePrecision,
            SizePrecision = sizePrecision,
            PriceIncrement = new Price(tick, pricePrecision),
            SizeIncrement = new Quantity(lot, sizePrecision),
            MinQuantity = minimum > 0m ? new Quantity(minimum, sizePrecision) : null,
            MaxQuantity = maxLimitSize > 0m ? new Quantity(maxLimitSize, sizePrecision) : null,
            MaxNotional = maxLimitAmount > 0m ? new Money(maxLimitAmount, quote) : null,

            // The venue publishes no per-instrument fee on any market, only the account's tier, so the family's
            // published rate stands until an account is read. Stated here rather than left at zero: a backtest on a
            // zero-fee instrument is a backtest of a venue that does not exist.
            MakerFee = OkxFees.SpotMaker,
            TakerFee = OkxFees.SpotTaker,

            // Zero, and correct: a cash spot trade posts no margin. See MarginsAsync for why the venue's MARGIN
            // tiers for this same pair are not what this field means.
            MarginInit = 0m,
            MarginMaint = 0m,
            MarginSource = MarginSource.NotMargined,
            TsEvent = now,
            TsInit = now,
        });
    }

    /// <summary>
    /// A perpetual or a dated contract. The two differ in three things and nothing else: the class, whether there is
    /// a delivery date, and the fees the family charges - so they are built here together rather than twice.
    /// </summary>
    private static Instrument Derivative(JsonElement item, string instId, decimal tick, decimal lot, decimal minimum, OkxMargin? margin, bool expiring)
    {
        decimal contractValue = item.Dec("ctVal");
        decimal multiplier = item.Filled("ctMult") ? item.Dec("ctMult") : 1m;
        if (contractValue <= 0m || multiplier <= 0m)
        {
            throw new InvalidOperationException(
                $"OKX published {instId} with no contract value, so nothing can say what a size of it means.");
        }

        // One contract is ctVal of ctValCcy and an order's size is a number of contracts, which the venue allows in
        // fractions of one: BTC-USDT-SWAP has lotSz 0.01, so a hundredth of a 0.01 BTC contract is tradable. The
        // smallest tradable quantity, and the step between them, is therefore that fraction expressed in base units.
        decimal step = contractValue * multiplier * lot;
        decimal smallest = contractValue * multiplier * (minimum > 0m ? minimum : lot);
        byte pricePrecision = Json.Precision(tick);
        byte sizePrecision = Json.Precision(step);
        Currency baseCurrency = Currency.FromCode(item.Str("ctValCcy"), 8);
        Currency settlement = Currency.FromCode(item.Str("settleCcy"), 8);

        // The venue names a linear contract's quote currency nowhere on the record - quoteCcy is empty on every one
        // of them - and publishes the pair it belongs to as uly, "BTC-USDT". The quote is the half of that after the
        // dash, which is the venue's own field read the way the venue writes it rather than the instId guessed at:
        // an instId can be BTC-USD_UM-261030 while its uly is plain BTC-USD.
        string underlying = item.Str("uly");
        int dash = underlying.LastIndexOf('-');
        Currency quote = dash > 0 && dash < underlying.Length - 1
            ? Currency.FromCode(underlying[(dash + 1)..], 8)
            : settlement;

        decimal maxLimitSize = item.Dec("maxLmtSz");
        UnixNanos now = UnixNanos.FromDateTimeOffset(DateTimeOffset.UtcNow);
        InstrumentSpec spec = new()
        {
            Id = OkxVenue.ToInstrumentId(instId),
            RawSymbol = new Symbol(instId),
            AssetClass = AssetClass.Crypto,
            InstrumentClass = expiring ? InstrumentClass.Future : InstrumentClass.Swap,
            QuoteCurrency = quote,
            BaseCurrency = baseCurrency,
            SettlementCurrency = settlement,
            PricePrecision = pricePrecision,
            SizePrecision = sizePrecision,
            PriceIncrement = new Price(tick, pricePrecision),
            SizeIncrement = new Quantity(step, sizePrecision),
            MinQuantity = new Quantity(smallest, sizePrecision),
            MaxQuantity = maxLimitSize > 0m ? new Quantity(maxLimitSize * contractValue * multiplier, sizePrecision) : null,

            // One, because a quantity is already in base currency by the time anything above the adapter sees it, so
            // notional is quantity times price here exactly as it is on the other venues. The venue's contract value
            // is carried in Info below, where the adapter reads it and nothing else has to.
            Multiplier = new Quantity(1m, 0),
            MakerFee = expiring ? OkxFees.FuturesMaker : OkxFees.SwapMaker,
            TakerFee = expiring ? OkxFees.FuturesTaker : OkxFees.SwapTaker,

            // The venue's own tier-one requirement, or zero when it published none for this family and said so.
            MarginInit = margin?.Initial ?? 0m,
            MarginMaint = margin?.Maintenance ?? 0m,

            // Which of those two happened, since the figure cannot say: no position tiers for this instrument means
            // a margined contract the engine holds no requirement for.
            MarginSource = margin is not null ? MarginSource.VenuePerContract : MarginSource.VenueSilent,
            Info = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [OkxVenue.ContractValueInfo] = (contractValue * multiplier).ToString(CultureInfo.InvariantCulture),
                [OkxVenue.MaxLeverageInfo] = (margin?.MaxLeverage ?? item.Dec("lever")).ToString(CultureInfo.InvariantCulture),
                [OkxVenue.UnderlyingInfo] = underlying,
            },
            TsEvent = now,
            TsInit = now,
        };

        if (!expiring)
        {
            return new CryptoPerpetual(spec);
        }

        // A dated contract carries the two dates the venue publishes: when it was listed and when it delivers. Both
        // are in milliseconds, and expTime is the field that is empty on a perpetual - which is why it is read here
        // and nowhere else.
        return new CryptoFuture(spec, baseCurrency, item.Ms("listTime"), item.Ms("expTime"));
    }
}

/// <summary>What the venue requires of a position in its first tier, and the most leverage it allows there.</summary>
internal sealed record OkxMargin(decimal Initial, decimal Maintenance, decimal MaxLeverage);

/// <summary>
/// What each market charges an account with no volume and no token holding, which is the rate a host shows before an
/// account has been read. The venue publishes these as its fee schedule rather than on an instrument, so they are
/// named here once and declared per family rather than repeated in the plugin.
/// </summary>
internal static class OkxFees
{
    public const decimal SpotMaker = 0.0008m;

    public const decimal SpotTaker = 0.001m;

    public const decimal SwapMaker = 0.0002m;

    public const decimal SwapTaker = 0.0005m;

    public const decimal FuturesMaker = 0.0002m;

    public const decimal FuturesTaker = 0.0005m;
}
