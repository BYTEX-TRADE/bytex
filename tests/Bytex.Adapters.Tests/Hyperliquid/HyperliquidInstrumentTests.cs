using System.Globalization;
using Bytex.Adapters.Hyperliquid;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;

namespace Bytex.Adapters.Tests.Hyperliquid;

// Why: this venue identifies an asset by its POSITION in an array, publishes its margins as a maximum leverage rather
// than as two fractions, and bounds a price by two rules at once where an instrument can carry one increment.
//
// Each of those is a wrong number rather than an error when it goes wrong. An index taken after filtering the
// delisted entries puts a real order on a contract nobody asked for. A margin hard-coded at 0.05 - which two other
// adapters here do for every contract they list - makes a backtest survive a move that would have liquidated it. And
// a price rounded by the decimal rule alone is refused by the venue for having six significant figures, on the one
// asset where it matters and at the one price where it matters.
//
// The catalog payload is the venue's own first six entries, recorded on 2026-09-25, and the fourth of them is
// delisted on purpose.
public sealed class HyperliquidInstrumentTests
{
    private static Routes MetaRoute() => new Routes().On("POST", HyperliquidVenue.InfoPath, HyperliquidPayloads.Meta);

    private static async Task<HyperliquidInstrumentProvider> LoadedAsync(LoopbackServer server)
    {
        HyperliquidHttp http = new(new HyperliquidDataClientConfig { BaseUrlHttp = server.HttpBase });
        HyperliquidInstrumentProvider provider = new(http);
        await provider.LoadAllAsync(CancellationToken.None);
        return provider;
    }

    [Fact]
    public async Task An_assets_index_is_its_position_in_the_array_including_the_delisted_ones()
    {
        // THE test of this file. The venue leaves a delisted contract in the universe, so the entries after it are
        // numbered past it: MATIC is index 3 and delisted, and DYDX after it is index 4 - not 3. An adapter that
        // filtered before numbering would send every order after the gap to the wrong asset, and the order would be
        // accepted.
        await using LoopbackServer server = new(MetaRoute().Handle);
        HyperliquidInstrumentProvider provider = await LoadedAsync(server);

        Assert.Equal(0, Index(provider, "BTC"));
        Assert.Equal(1, Index(provider, "ETH"));
        Assert.Equal(2, Index(provider, "ATOM"));
        Assert.Equal(4, Index(provider, "DYDX"));
        Assert.Equal(5, Index(provider, "SOL"));

        // And the delisted one is not published at all, which is the other half: it holds its number and is not
        // tradable.
        Assert.Null(provider.Find(InstrumentId.Parse("MATIC-PERP.HYPERLIQUID")));
        Assert.Equal(5, provider.Count);
    }

    [Fact]
    public async Task A_perpetual_is_named_by_the_coin_with_the_suffix_the_other_venues_use()
    {
        await using LoopbackServer server = new(MetaRoute().Handle);
        HyperliquidInstrumentProvider provider = await LoadedAsync(server);

        Instrument btc = provider.Find(InstrumentId.Parse("BTC-PERP.HYPERLIQUID"))!;

        // The suffix is this adapter's, not the venue's: the venue calls the asset "BTC" with no suffix at all, so
        // anything reading a perpetual off a symbol's spelling would have read it as spot.
        Assert.Equal("BTC", btc.RawSymbol!.Value);
        Assert.Equal(InstrumentClass.Swap, btc.InstrumentClass);
        Assert.Equal("BTC", HyperliquidVenue.ToCoin(btc.Id));

        // One collateral token for the whole family, published once rather than per contract.
        Assert.Equal("USDC", btc.QuoteCurrency.Code);
        Assert.Equal("USDC", btc.SettlementCurrency.Code);

        // A size is already in base units here - there is no contract to convert through - so notional is quantity
        // times price exactly as the engine assumes.
        Assert.Equal(1m, btc.Multiplier!.Value);
    }

    [Theory]

    // Every margin here is computed from the venue's own published maxLeverage for that asset, and the maintenance
    // half was measured against three live cross positions rather than read off a page: 4937.99391 / (2 x 40) gave
    // exactly the 61.724923 one account reported, 368455.20858 / 80 gave exactly 4605.690107, and 377667.0 / 80 gave
    // exactly the 4720.8375 in the recorded account payload. All at the asset's MAXIMUM leverage and not at the 6x
    // and 10x those accounts had chosen.
    [InlineData("BTC", 40, 0.025, 0.0125)]
    [InlineData("ETH", 25, 0.04, 0.02)]
    [InlineData("ATOM", 5, 0.2, 0.1)]
    [InlineData("SOL", 20, 0.05, 0.025)]
    public async Task Margin_comes_from_the_venues_own_maximum_leverage(string coin, int maxLeverage, double init, double maint)
    {
        await using LoopbackServer server = new(MetaRoute().Handle);
        HyperliquidInstrumentProvider provider = await LoadedAsync(server);
        Instrument instrument = provider.Find(HyperliquidVenue.ToInstrumentId(coin))!;

        Assert.Equal(maxLeverage, HyperliquidAsset.Of(instrument).MaxLeverage);
        Assert.Equal((decimal)init, instrument.MarginInit);
        Assert.Equal((decimal)maint, instrument.MarginMaint);

        // And nothing is the flat 0.05/0.025 that two other adapters carry for every contract, except where the
        // venue's own leverage happens to make it so - which SOL's 20x does, and which is why a constant looked
        // plausible.
        if (coin != "SOL")
        {
            Assert.NotEqual(0.05m, instrument.MarginInit);
        }
    }

    [Theory]

    // szDecimals is the size rule directly and the price rule by subtraction: a price may have at most 6 - szDecimals
    // decimal places. Measured against seven live books, where the deepest size on each had exactly szDecimals
    // places.
    [InlineData("BTC", 5, 1)]
    [InlineData("ETH", 4, 2)]
    [InlineData("ATOM", 2, 4)]
    [InlineData("SOL", 2, 4)]
    public async Task Size_and_price_precision_come_from_the_assets_size_decimals(string coin, int sizeDecimals, int priceDecimals)
    {
        await using LoopbackServer server = new(MetaRoute().Handle);
        HyperliquidInstrumentProvider provider = await LoadedAsync(server);
        Instrument instrument = provider.Find(HyperliquidVenue.ToInstrumentId(coin))!;
        HyperliquidAsset asset = HyperliquidAsset.Of(instrument);

        Assert.Equal(sizeDecimals, asset.SizeDecimals);
        Assert.Equal(priceDecimals, asset.PriceDecimals);
        Assert.Equal((byte)sizeDecimals, instrument.SizePrecision);
        Assert.Equal((byte)priceDecimals, instrument.PricePrecision);

        // The smallest tradable size is one step, and there is no minimum notional the venue publishes per asset.
        Assert.Equal(instrument.SizeIncrement.Value, instrument.MinQuantity!.Value);
    }

    [Theory]

    // The second price rule, and the reason an instrument's increment is not the whole story. Every expected value
    // here is the step MEASURED off a live book on 2026-09-25 at the price beside it - so this is the venue's own
    // behaviour and not an interpretation of it.
    //
    // The significant-figure rule bites on the expensive assets and the decimal rule on the cheap ones, which is
    // why neither can be dropped.
    [InlineData("BTC", "83697.4", "83697")]
    [InlineData("BTC", "83697.5", "83698")]
    [InlineData("ETH", "2681.64", "2681.6")]
    [InlineData("SOL", "120.7649", "120.76")]
    [InlineData("ATOM", "4.123456", "4.1235")]
    public async Task A_price_is_rounded_by_both_of_the_venues_rules(string coin, string wanted, string expected)
    {
        await using LoopbackServer server = new(MetaRoute().Handle);
        HyperliquidInstrumentProvider provider = await LoadedAsync(server);
        HyperliquidAsset asset = HyperliquidAsset.Of(provider.Find(HyperliquidVenue.ToInstrumentId(coin))!);

        decimal rounded = asset.RoundPrice(decimal.Parse(wanted, CultureInfo.InvariantCulture));

        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), rounded);
    }

    [Fact]
    public async Task An_integer_price_is_left_alone_however_many_figures_it_has()
    {
        // The venue accepts a whole number at any size, so rounding one to five figures is what would break an
        // expensive asset rather than what would save it.
        await using LoopbackServer server = new(MetaRoute().Handle);
        HyperliquidInstrumentProvider provider = await LoadedAsync(server);
        HyperliquidAsset btc = HyperliquidAsset.Of(provider.Find(InstrumentId.Parse("BTC-PERP.HYPERLIQUID"))!);

        Assert.Equal(1234567m, btc.RoundPrice(1234567m));
        Assert.Equal(83697m, btc.RoundPrice(83697m));
    }

    [Fact]
    public async Task Loading_one_instrument_loads_one_even_though_the_venue_answers_with_all_of_them()
    {
        // The venue has NO per-instrument read. Measured: `meta` sent with a coin and a name field set to BTC
        // answered with all 234 assets and the same 17628 bytes as the bare request, so a filter is ignored
        // outright. The capability is still true because the adapter can do it - and this is what stops it doing
        // what Binance's futures family did, which was to fill the provider with the whole venue every time
        // somebody asked for one contract.
        await using LoopbackServer server = new(MetaRoute().Handle);
        HyperliquidHttp http = new(new HyperliquidDataClientConfig { BaseUrlHttp = server.HttpBase });
        HyperliquidInstrumentProvider provider = new(http);

        await provider.LoadAsync(InstrumentId.Parse("ETH-PERP.HYPERLIQUID"), CancellationToken.None);

        Instrument only = Assert.Single(provider.GetAll());
        Assert.Equal(InstrumentId.Parse("ETH-PERP.HYPERLIQUID"), only.Id);
        Assert.Equal(1, HyperliquidAsset.Of(only).Index);
    }

    [Fact]
    public async Task A_delisted_contract_is_not_loadable_by_name_either()
    {
        // Otherwise an add-instrument path would offer a contract the venue will not trade, and the order would be
        // refused at the venue rather than at the point somebody chose it.
        await using LoopbackServer server = new(MetaRoute().Handle);
        HyperliquidHttp http = new(new HyperliquidDataClientConfig { BaseUrlHttp = server.HttpBase });
        HyperliquidInstrumentProvider provider = new(http);

        await provider.LoadAsync(InstrumentId.Parse("MATIC-PERP.HYPERLIQUID"), CancellationToken.None);

        Assert.Empty(provider.GetAll());
    }

    [Fact]
    public async Task A_quote_filter_that_no_asset_matches_returns_nothing_rather_than_everything()
    {
        // This family settles in one currency, so a filter for another is correctly empty. The failure being
        // guarded against is the opposite one - a filter silently ignored, which is what makes a picker show a
        // venue's whole catalog under the wrong heading.
        await using LoopbackServer server = new(MetaRoute().Handle);
        HyperliquidHttp http = new(new HyperliquidDataClientConfig { BaseUrlHttp = server.HttpBase });
        HyperliquidInstrumentProvider provider = new(http);

        await provider.LoadAllAsync(
            CancellationToken.None,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["quote"] = "USDT" });

        Assert.Empty(provider.GetAll());
    }

    [Fact]
    public async Task An_instrument_from_anywhere_else_cannot_be_ordered_at_all()
    {
        // The asset index is the only thing an order can name an instrument by, and it is not derivable from the
        // coin, the listing date or anything else visible. So an instrument that did not come from this provider
        // has to fail loudly rather than be sent with a guessed index - which would be a real order on whatever
        // asset happened to be at that position.
        await using LoopbackServer server = new(MetaRoute().Handle);
        HyperliquidInstrumentProvider provider = await LoadedAsync(server);
        Instrument btc = provider.Find(InstrumentId.Parse("BTC-PERP.HYPERLIQUID"))!;

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => HyperliquidAsset.Of(new CryptoPerpetual(new InstrumentSpec
            {
                Id = btc.Id,
                AssetClass = AssetClass.Crypto,
                InstrumentClass = InstrumentClass.Swap,
                QuoteCurrency = btc.QuoteCurrency,
                BaseCurrency = btc.BaseCurrency,
                PricePrecision = 1,
                SizePrecision = 5,
                PriceIncrement = btc.PriceIncrement,
                SizeIncrement = btc.SizeIncrement,
            })));

        Assert.Contains("asset index", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_read_is_a_request_type_in_the_body_rather_than_a_path()
    {
        // The shape the rest of this adapter is built on, pinned once: there is no path per resource here, so the
        // body is what says which read it is. A GET is refused with 405, measured, so none of these can ever be a
        // cacheable URL either.
        Assert.Equal("""{"type":"meta"}""", HyperliquidHttp.InfoBody(HyperliquidReads.Meta, null));

        Assert.Equal(
            """{"type":"clearinghouseState","user":"0x0000000000000000000000000000000000000001"}""",
            HyperliquidHttp.InfoBody(
                HyperliquidReads.ClearinghouseState,
                new Dictionary<string, object>(StringComparer.Ordinal) { ["user"] = "0x0000000000000000000000000000000000000001" }));

        // And one read nests, which is why a field may be a group of fields.
        Assert.Equal(
            """{"type":"candleSnapshot","req":{"coin":"BTC","interval":"1h"}}""",
            HyperliquidHttp.InfoBody(
                HyperliquidReads.CandleSnapshot,
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["req"] = new Dictionary<string, object>(StringComparer.Ordinal) { ["coin"] = "BTC", ["interval"] = "1h" },
                }));
    }

    [Fact]
    public void A_bar_length_the_venue_does_not_keep_is_refused_by_name()
    {
        // Fourteen lengths were tried one by one against the live venue. Six hours - which KuCoin serves - is one
        // of the three it refused, and it refused them with a deserialisation error rather than with an empty
        // answer, so a caller would otherwise see a failure with nothing in it about which length was wrong.
        Assert.Equal("1m", HyperliquidVenue.Interval(new BarSpecification(1, BarAggregation.Minute, PriceType.Last)));
        Assert.Equal("3d", HyperliquidVenue.Interval(new BarSpecification(3, BarAggregation.Day, PriceType.Last)));
        Assert.Equal("1M", HyperliquidVenue.Interval(new BarSpecification(1, BarAggregation.Month, PriceType.Last)));

        NotSupportedException error = Assert.Throws<NotSupportedException>(
            () => HyperliquidVenue.Interval(new BarSpecification(6, BarAggregation.Hour, PriceType.Last)));
        Assert.Contains("1, 2, 4, 8 and 12", error.Message, StringComparison.Ordinal);
    }

    private static int Index(HyperliquidInstrumentProvider provider, string coin) =>
        HyperliquidAsset.Of(provider.Find(HyperliquidVenue.ToInstrumentId(coin))!).Index;
}
