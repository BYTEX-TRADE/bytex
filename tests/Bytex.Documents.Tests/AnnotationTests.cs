using Bytex.Core.Model;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Documents.Annotations;

namespace Bytex.Documents.Tests;

// Why: an annotation is the document format's way of carrying an event (a calendar item, a venue status, a piece of
// news) into a run, and everything a strategy may later do with one depends on two answers being right: does this
// event apply to the instrument being traded, and who classified it. Both are pinned here.
public class AnnotationTests
{
    private static CurrencyPair BtcUsdt() => new(new InstrumentSpec
    {
        Id = InstrumentId.Parse("BTCUSDT.BINANCE"),
        AssetClass = AssetClass.Crypto,
        InstrumentClass = InstrumentClass.Spot,
        QuoteCurrency = Currencies.USDT,
        BaseCurrency = Currencies.BTC,
        PricePrecision = 2,
        SizePrecision = 5,
        PriceIncrement = Price.Parse("0.01"),
        SizeIncrement = Quantity.Parse("0.00001"),
    });

    private static Annotation Annotation(IReadOnlyList<AnnotationScope> scopes, string provenance = "venue:binance", UnixNanos? end = null) =>
        new(Guid.NewGuid(), new UnixNanos(1_700_000_000_000_000_000L), new UnixNanos(1_700_000_000_000_000_000L), end,
            scopes, "calendar", AnnotationSeverity.Warning, "Rate decision", null, null, provenance);

    [Fact]
    public void A_scope_matches_the_instruments_it_names_and_nothing_else()
    {
        CurrencyPair btcusdt = BtcUsdt();

        Assert.True(AnnotationScope.Global.Matches(btcusdt));
        Assert.True(AnnotationScope.ForAssetClass(AssetClass.Crypto).Matches(btcusdt));
        Assert.True(AnnotationScope.ForVenue(new Venue("BINANCE")).Matches(btcusdt));
        Assert.True(AnnotationScope.ForInstrument(InstrumentId.Parse("BTCUSDT.BINANCE")).Matches(btcusdt));

        Assert.False(AnnotationScope.ForAssetClass(AssetClass.Equity).Matches(btcusdt));
        Assert.False(AnnotationScope.ForVenue(new Venue("BYBIT")).Matches(btcusdt));
        Assert.False(AnnotationScope.ForInstrument(InstrumentId.Parse("ETHUSDT.BINANCE")).Matches(btcusdt));
    }

    [Fact]
    public void A_currency_scope_can_ask_for_the_side_the_currency_is_on()
    {
        CurrencyPair btcusdt = BtcUsdt();

        // A US rate decision is about the quote currency of BTCUSDT, not about BTC.
        Assert.True(AnnotationScope.ForCurrency("USDT", CurrencyRole.Quote).Matches(btcusdt));
        Assert.False(AnnotationScope.ForCurrency("USDT", CurrencyRole.Base).Matches(btcusdt));
        Assert.True(AnnotationScope.ForCurrency("BTC", CurrencyRole.Base).Matches(btcusdt));
        Assert.True(AnnotationScope.ForCurrency("btc").Matches(btcusdt));
        Assert.False(AnnotationScope.ForCurrency("EUR").Matches(btcusdt));
    }

    [Fact]
    public void An_annotation_applies_when_any_of_its_scopes_does()
    {
        CurrencyPair btcusdt = BtcUsdt();

        Assert.True(Annotation([AnnotationScope.ForVenue(new Venue("BYBIT")), AnnotationScope.ForCurrency("BTC")]).AppliesTo(btcusdt));
        Assert.False(Annotation([AnnotationScope.ForVenue(new Venue("BYBIT")), AnnotationScope.ForCurrency("EUR")]).AppliesTo(btcusdt));
    }

    [Fact]
    public void Who_classified_an_annotation_is_read_from_its_provenance()
    {
        Assert.False(Annotation([AnnotationScope.Global]).IsAiClassified);
        Assert.False(Annotation([AnnotationScope.Global], provenance: "user").IsAiClassified);
        Assert.True(Annotation([AnnotationScope.Global], provenance: "ai:some-model").IsAiClassified);
        Assert.True(Annotation([AnnotationScope.Global], provenance: "AI:some-model").IsAiClassified);
    }

    [Fact]
    public void An_annotation_with_an_end_is_a_window_and_carries_the_engines_data_stamps()
    {
        Annotation point = Annotation([AnnotationScope.Global]);
        Annotation window = Annotation([AnnotationScope.Global], end: new UnixNanos(1_700_000_060_000_000_000L));

        Assert.False(point.IsWindow);
        Assert.True(window.IsWindow);
        Assert.IsAssignableFrom<Bytex.Core.Model.Data.CustomData>(point);
    }

    [Fact]
    public void The_scopes_of_an_instrument_are_the_ones_a_store_has_to_look_up()
    {
        IReadOnlyList<AnnotationScope> scopes = ScopeResolver.For(BtcUsdt());

        Assert.Equal(
            ["global", "assetclass:Crypto", "venue:BINANCE", "instrument:BTCUSDT.BINANCE", "currency:USDT:quote", "currency:BTC:base"],
            scopes.Select(s => s.ToString()));
        Assert.All(scopes, s => Assert.True(s.Matches(BtcUsdt())));
    }

    [Fact]
    public void The_subscription_key_separates_one_category_from_the_rest()
    {
        Assert.NotEqual(Annotations.Annotation.DataTypeFor(), Annotations.Annotation.DataTypeFor("calendar"));
        Assert.Equal(Annotations.Annotation.DataTypeFor("calendar"), Annotations.Annotation.DataTypeFor("calendar"));
    }
}
