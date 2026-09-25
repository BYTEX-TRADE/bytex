using System.Globalization;
using System.Text.Json;
using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Microsoft.Extensions.Logging;

namespace Bytex.Adapters.Hyperliquid;

/// <summary>
/// What this venue publishes about one perpetual, and the two things it needs that an instrument cannot carry: its
/// position in the universe, and a price rule that is not a fixed step.
/// </summary>
public sealed record HyperliquidAsset
{
    /// <summary>
    /// Where this asset sits in the venue's <c>meta.universe</c> array, which is HOW AN ORDER NAMES IT. There is no
    /// symbol on the wire: the order carries the number.
    /// <para>
    /// It is the array position and nothing else - the venue publishes no id field, and it is not derivable from the
    /// coin's name, its listing date or anything else visible. So an order cannot be placed without having read the
    /// universe, which is why the index is carried on the instrument by whoever read it rather than looked up again.
    /// A delisting that removed an entry would renumber everything after it, which is the reason this is never
    /// cached across a run.
    /// </para>
    /// </summary>
    public required int Index { get; init; }

    /// <summary>The coin as the venue names it - <c>BTC</c>, <c>kPEPE</c> - with no suffix of any kind.</summary>
    public required string Coin { get; init; }

    /// <summary>
    /// The decimal places a SIZE may have. Measured against seven live books: every size in every one of them had
    /// at most this many places and the deepest had exactly this many, so the size step is ten to the minus this.
    /// </summary>
    public required int SizeDecimals { get; init; }

    /// <summary>
    /// The most leverage this asset allows, published per asset. Both margins come out of it: initial margin is one
    /// over it, and maintenance margin is half of that.
    /// </summary>
    public required int MaxLeverage { get; init; }

    /// <summary>Whether the venue has delisted it, in which case it is not tradable and is not published.</summary>
    public bool IsDelisted { get; init; }

    /// <summary>Whether the asset can only be held on its own margin, so a cross-margin leverage would be refused.</summary>
    public bool IsolatedOnly { get; init; }

    /// <summary>The decimal places a PRICE may have on this asset, which is the venue's cap less the size places.</summary>
    public int PriceDecimals => HyperliquidVenue.MaxPriceDecimals - SizeDecimals;

    /// <summary>The smallest step a price may take under the decimal rule alone; see <see cref="RoundPrice"/>.</summary>
    public decimal PriceIncrement => Step(PriceDecimals);

    /// <summary>The step between tradable sizes.</summary>
    public decimal SizeIncrement => Step(SizeDecimals);

    /// <summary>
    /// A price this venue will accept, which needs BOTH of its rules applied and is the reason an instrument's
    /// increment is not the whole story here.
    /// <para>
    /// A price is valid when it has at most <see cref="PriceDecimals"/> decimal places AND at most
    /// <see cref="HyperliquidVenue.MaxPriceSignificantFigures"/> significant figures. The first depends on the asset
    /// and the second on the price, so the smallest valid step MOVES AS THE PRICE MOVES - and an instrument carries
    /// one increment. Measured across seven live books on 2026-09-25: BTC stepped by 1 at 83697, ETH by 0.1 at
    /// 2681.6, SOL by 0.01 at 120.76, HYPE by 0.001 at 91.257, XRP by 0.0001 at 1.5598 - the significant-figure rule
    /// biting in every one of those - while DOGE and kPEPE stepped by 0.000001, where the decimal rule bit first.
    /// </para>
    /// <para>
    /// So the instrument publishes the decimal rule, which is the venue's own per-asset fact, and every price
    /// leaving this adapter comes through here. An integer is left alone: rounding it to five figures is what would
    /// break a high-priced asset, and the venue accepts a whole number at any size.
    /// </para>
    /// </summary>
    public decimal RoundPrice(decimal price)
    {
        decimal byDecimals = Math.Round(price, PriceDecimals, MidpointRounding.ToEven);
        if (byDecimals == decimal.Truncate(byDecimals))
        {
            return byDecimals;
        }

        // The exponent of the leading digit, so that the number of places to keep leaves five figures standing.
        int exponent = (int)Math.Floor(Math.Log10((double)Math.Abs(byDecimals)));
        int places = HyperliquidVenue.MaxPriceSignificantFigures - 1 - exponent;
        return places >= PriceDecimals
            ? byDecimals
            : Math.Round(byDecimals, Math.Max(0, places), MidpointRounding.ToEven);
    }

    /// <summary>Ten to the minus <paramref name="decimals"/>, as a decimal with that many places and no fewer.</summary>
    private static decimal Step(int decimals) =>
        decimals <= 0 ? 1m : 1m / (decimal)Math.Pow(10, decimals);

    /// <summary>
    /// What this venue says about an asset, read back off an instrument the provider built. Everything an order
    /// needs that the engine's instrument has no field for lives in the instrument's info, which is what that
    /// dictionary is for.
    /// </summary>
    public static HyperliquidAsset Of(Instrument instrument)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        if (instrument.Info is { } info
            && info.TryGetValue(HyperliquidVenue.AssetIndexInfo, out string? indexText)
            && int.TryParse(indexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
            && info.TryGetValue(HyperliquidVenue.SizeDecimalsInfo, out string? sizeText)
            && int.TryParse(sizeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int sizeDecimals)
            && info.TryGetValue(HyperliquidVenue.MaxLeverageInfo, out string? leverageText)
            && int.TryParse(leverageText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int maxLeverage))
        {
            return new HyperliquidAsset
            {
                Index = index,
                Coin = HyperliquidVenue.ToCoin(instrument.Id),
                SizeDecimals = sizeDecimals,
                MaxLeverage = maxLeverage,
            };
        }

        throw new InvalidOperationException(
            $"{instrument.Id} carries no Hyperliquid asset index, so nothing can name it in an order: this venue's "
            + "orders identify an asset by its position in the universe and never by its symbol. It was not loaded "
            + "by this venue's instrument provider.");
    }
}

/// <summary>
/// Loads the perpetuals from the <c>meta</c> read.
/// <para>
/// Two things about this are unlike the other venues and neither could be worked around.
/// </para>
/// <para>
/// There is no way to ask about ONE contract. <c>meta</c> takes no filter - measured: sent with both a <c>coin</c>
/// and a <c>name</c> field set to BTC it answered with all 234 assets, byte for byte the same 17628 bytes as the
/// bare request - so loading one instrument means reading the universe and picking it out. That is what
/// <see cref="LoadAsync"/> does, and it is why the declaration can still say one instrument is loadable: the
/// capability is about what the ADAPTER can do, and it can, at the price of the whole catalog per call. The
/// alternative reading - declaring it false - would make this venue unusable from an add-instrument path while the
/// thing it claims to be unable to do demonstrably works.
/// </para>
/// <para>
/// And the margins come from the venue. <c>meta</c> publishes a <c>maxLeverage</c> per asset and a table of tiers
/// beside it, so initial margin is one over the leverage at the first tier and maintenance margin is half of that -
/// both measured against live positions rather than assumed. Two adapters here hard-code 0.05 and 0.025 for every
/// contract they list, which is wrong for all but the handful that happen to be 20x, and it is wrong in the
/// direction that makes a backtest survive a move that would have liquidated it.
/// </para>
/// </summary>
public sealed class HyperliquidInstrumentProvider : InstrumentProviderBase
{
    private readonly HyperliquidHttp _http;

    public HyperliquidInstrumentProvider(HyperliquidHttp http, InstrumentProviderConfig? config = null, ILogger? logger = null)
        : base(HyperliquidVenue.Venue, config, logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public override async Task LoadAllAsync(CancellationToken ct, IReadOnlyDictionary<string, string>? filters = null)
    {
        JsonElement meta = await _http.InfoAsync(HyperliquidReads.Meta, null, ct).ConfigureAwait(false);
        int loaded = 0;
        int delisted = 0;
        foreach ((HyperliquidAsset asset, Instrument instrument) in Parse(meta))
        {
            if (asset.IsDelisted)
            {
                delisted++;
                continue;
            }

            // The only filter worth having on this family is the quote currency, and there is exactly one of those -
            // so a filter asking for anything else correctly returns nothing rather than being ignored.
            if (filters is not null && filters.TryGetValue("quote", out string? quote)
                && !instrument.QuoteCurrency.Code.Equals(quote, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Add(instrument);
            loaded++;
        }

        Log.LogInformation(
            "Loaded {Count} Hyperliquid perpetuals, leaving out {Delisted} the venue has delisted",
            loaded,
            delisted);
    }

    /// <summary>
    /// One instrument, out of the whole universe, because the venue offers nothing narrower. Only the one asked for
    /// is added: the answer carries every asset the venue lists, and adding them all would fill the provider with
    /// hundreds nobody asked about - which is the defect Binance's futures family shipped with.
    /// </summary>
    public override async Task LoadAsync(InstrumentId id, CancellationToken ct)
    {
        JsonElement meta = await _http.InfoAsync(HyperliquidReads.Meta, null, ct).ConfigureAwait(false);
        foreach ((HyperliquidAsset asset, Instrument instrument) in Parse(meta))
        {
            if (instrument.Id == id && !asset.IsDelisted)
            {
                Add(instrument);
                return;
            }
        }

        // Not listed is an answer and not a failure, the same on every venue: a caller looks the instrument up
        // afterwards and finds nothing. There is no refusal to swallow here, because the venue never refuses - it
        // simply does not have the asset in the array it always sends.
        Log.LogInformation("Hyperliquid does not list the perpetual {Instrument}", id);
    }

    /// <summary>
    /// Every asset of a <c>meta</c> answer, paired with the instrument it becomes. The pair is what the caller needs:
    /// the index lives on the instrument but the delisted flag decides whether to publish it at all.
    /// </summary>
    internal static IEnumerable<(HyperliquidAsset Asset, Instrument Instrument)> Parse(JsonElement meta)
    {
        if (!meta.TryGetProperty(HyperliquidReads.Universe, out JsonElement universe) || universe.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        int index = 0;
        foreach (JsonElement item in universe.EnumerateArray())
        {
            // The index is the POSITION, counted here as the array is walked. It has to be counted even for the
            // delisted assets that are then skipped, because the venue leaves them in the array and the entries
            // after them are numbered past them.
            HyperliquidAsset asset = new()
            {
                Index = index++,
                Coin = item.Str(HyperliquidReads.Name),
                SizeDecimals = (int)item.Long(HyperliquidReads.SizeDecimals),
                MaxLeverage = (int)item.Long(HyperliquidReads.MaxLeverage),
                IsDelisted = item.Bool(HyperliquidReads.IsDelisted),
                IsolatedOnly = item.Bool(HyperliquidReads.OnlyIsolated),
            };

            if (asset.Coin.Length == 0 || asset.MaxLeverage <= 0)
            {
                continue;
            }

            yield return (asset, ToInstrument(asset));
        }
    }

    private static Instrument ToInstrument(HyperliquidAsset asset)
    {
        byte pricePrecision = (byte)Math.Max(0, asset.PriceDecimals);
        byte sizePrecision = (byte)asset.SizeDecimals;
        Currency quote = Currency.FromCode(HyperliquidVenue.QuoteCurrency, HyperliquidVenue.QuoteCurrencyPrecision);
        Currency baseCurrency = Currency.FromCode(asset.Coin, HyperliquidVenue.QuoteCurrencyPrecision);
        UnixNanos now = UnixNanos.FromDateTimeOffset(DateTimeOffset.UtcNow);

        return new CryptoPerpetual(new InstrumentSpec
        {
            Id = HyperliquidVenue.ToInstrumentId(asset.Coin),

            // The coin as the venue spells it, which is what the socket names a subscription by - and is NOT what
            // an order names. An order carries the index.
            RawSymbol = new Symbol(asset.Coin),
            AssetClass = AssetClass.Crypto,
            InstrumentClass = InstrumentClass.Swap,
            QuoteCurrency = quote,
            BaseCurrency = baseCurrency,

            // One collateral token for the whole family, published once in meta rather than per contract.
            SettlementCurrency = quote,
            PricePrecision = pricePrecision,
            SizePrecision = sizePrecision,
            PriceIncrement = new Price(asset.PriceIncrement, pricePrecision),
            SizeIncrement = new Quantity(asset.SizeIncrement, sizePrecision),
            MinQuantity = new Quantity(asset.SizeIncrement, sizePrecision),

            // A size is already in base units on this venue - there is no contract to convert through - so notional
            // is quantity times price exactly as the engine assumes.
            Multiplier = new Quantity(1m, 0),
            MakerFee = HyperliquidVenue.BaseMakerFee,
            TakerFee = HyperliquidVenue.BaseTakerFee,

            // FROM THE VENUE, per asset, and not a constant. Initial margin is one over the asset's own maximum
            // leverage; maintenance margin is half of that, which was measured against two live cross positions to
            // the last decimal place rather than read off a page.
            MarginInit = 1m / asset.MaxLeverage,
            MarginMaint = 1m / (asset.MaxLeverage * HyperliquidVenue.MaintenanceMarginFraction),
            Info = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [HyperliquidVenue.AssetIndexInfo] = asset.Index.ToString(CultureInfo.InvariantCulture),
                [HyperliquidVenue.SizeDecimalsInfo] = asset.SizeDecimals.ToString(CultureInfo.InvariantCulture),
                [HyperliquidVenue.MaxLeverageInfo] = asset.MaxLeverage.ToString(CultureInfo.InvariantCulture),
            },
            TsEvent = now,
            TsInit = now,
        });
    }
}
