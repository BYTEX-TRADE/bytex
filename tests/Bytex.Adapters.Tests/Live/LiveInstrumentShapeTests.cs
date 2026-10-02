using Bytex.Adapters.Binance;
using Bytex.Adapters.Bitget;
using Bytex.Adapters.Bybit;
using Bytex.Adapters.Gate;
using Bytex.Adapters.Hyperliquid;
using Bytex.Adapters.Kraken;
using Bytex.Adapters.Kucoin;
using Bytex.Adapters.Okx;
using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Instruments;

namespace Bytex.Adapters.Tests.Live;

// Why (R12.10): every other adapter test runs against a recording. A recording cannot notice a venue renaming a field,
// dropping one, or sending a number where it used to send a string - the offline suite goes on passing against the
// photograph while the adapter is broken against the venue. This is the periodic check that the photograph is still
// the venue.
//
// It is the same providers the offline tests use, pointed at nothing - no base URL override, so each one goes to its
// own venue - and held to the assertions the offline tests make. A renamed field does not throw; it parses as zero or
// null and these assertions catch exactly that.
[Collection(LiveVenueCollection.Name)]
public sealed class LiveInstrumentShapeTests(LiveVenueListings listings)
{
    private readonly LiveVenueListings _listings = listings;

    /// <summary>
    /// A market every one of these venues lists, per family, so the check is about the venue's shape rather than about
    /// which coins it happens to have today.
    /// </summary>
    public static TheoryData<string, string> Families()
    {
        TheoryData<string, string> data = new();
        foreach (string family in new[]
        {
            "binance-spot", "binance-usdm", "binance-coinm",
            "bybit-spot", "bybit-linear", "bybit-inverse",
            "okx-spot", "okx-swap",
            "kucoin-spot", "kucoin-futures",
            "kraken-spot", "kraken-futures",
            "bitget-spot", "bitget-usdt",
            "gate-spot", "gate-futures",
            "hyperliquid",
        })
        {
            data.Add(family, family.Split('-')[0].ToUpperInvariant());
        }

        return data;
    }

    private Task<IInstrumentProvider> LoadAsync(string family) => _listings.ListingAsync(family, () => LoadOnceAsync(family));

    private static async Task<IInstrumentProvider> LoadOnceAsync(string family)
    {
        InstrumentProviderConfig all = new() { LoadAll = true, LogWarnings = false };
        IInstrumentProvider provider = family switch
        {
            "binance-spot" => new BinanceInstrumentProvider(new BinanceHttp(new BinanceDataClientConfig { AccountType = BinanceAccountType.Spot }, null), BinanceAccountType.Spot, all, null),
            "binance-usdm" => new BinanceInstrumentProvider(new BinanceHttp(new BinanceDataClientConfig { AccountType = BinanceAccountType.UsdMFutures }, null), BinanceAccountType.UsdMFutures, all, null),
            "binance-coinm" => new BinanceInstrumentProvider(new BinanceHttp(new BinanceDataClientConfig { AccountType = BinanceAccountType.CoinMFutures }, null), BinanceAccountType.CoinMFutures, all, null),
            "bybit-spot" => new BybitInstrumentProvider(new BybitHttp(new BybitDataClientConfig { ProductType = BybitProductType.Spot }, null), BybitProductType.Spot, all, null),
            "bybit-linear" => new BybitInstrumentProvider(new BybitHttp(new BybitDataClientConfig { ProductType = BybitProductType.Linear }, null), BybitProductType.Linear, all, null),
            "bybit-inverse" => new BybitInstrumentProvider(new BybitHttp(new BybitDataClientConfig { ProductType = BybitProductType.Inverse }, null), BybitProductType.Inverse, all, null),
            "okx-spot" => new OkxInstrumentProvider(new OkxHttp(new OkxDataClientConfig(), null), OkxInstrumentType.Spot, all, null),
            "okx-swap" => new OkxInstrumentProvider(new OkxHttp(new OkxDataClientConfig(), null), OkxInstrumentType.Swap, all, null),
            "kucoin-spot" => new KucoinInstrumentProvider(new KucoinHttp(new KucoinDataClientConfig { ProductType = KucoinProductType.Spot }), all, null),
            "kucoin-futures" => new KucoinFuturesInstrumentProvider(new KucoinHttp(new KucoinDataClientConfig { ProductType = KucoinProductType.Futures }), all, null),
            "kraken-spot" => new KrakenInstrumentProvider(new KrakenHttp(new KrakenDataClientConfig { ProductType = KrakenProductType.Spot }), all, null),

            // The product type is what chooses the host here - spot answers on api.kraken.com and futures on
            // futures.kraken.com - so leaving it at its default asked the spot host for a futures path and got a 404
            // saying "Unknown method". Found by this test on its first live run, which is the test working.
            "kraken-futures" => new KrakenFuturesInstrumentProvider(new KrakenHttp(new KrakenDataClientConfig { ProductType = KrakenProductType.Futures }), all, null),
            "bitget-spot" => new BitgetInstrumentProvider(new BitgetHttp(new BitgetDataClientConfig { ProductType = BitgetProductType.Spot }), all, null),
            "bitget-usdt" => new BitgetInstrumentProvider(new BitgetHttp(new BitgetDataClientConfig { ProductType = BitgetProductType.UsdtFutures }), all, null),
            "gate-spot" => new GateInstrumentProvider(new GateHttp(new GateDataClientConfig { ProductType = GateProductType.Spot }), all, null),
            "gate-futures" => new GateFuturesInstrumentProvider(new GateHttp(new GateDataClientConfig { ProductType = GateProductType.Futures }), all, null),
            "hyperliquid" => new HyperliquidInstrumentProvider(new HyperliquidHttp(new HyperliquidDataClientConfig()), all, null),
            _ => throw new ArgumentOutOfRangeException(nameof(family), family, "no such family in this test"),
        };

        using CancellationTokenSource cts = new(LiveVenue.Timeout);
        await provider.LoadAllAsync(cts.Token);
        return provider;
    }

    /// <summary>
    /// <b>The shape check.</b> Every field these adapters read is read by NAME, so a rename is silent: the value comes
    /// back as zero, null or an empty string, the parse succeeds, and the instrument is wrong. So this asserts the
    /// things a parse cannot fake - a listing with markets in it, and per market the figures every order is built
    /// from.
    /// </summary>
    [LiveTheory]
    [MemberData(nameof(Families))]
    public async Task What_a_venue_lists_still_has_the_fields_the_recordings_assert(string family, string venue)
    {
        IInstrumentProvider provider = await LoadAsync(family);
        IReadOnlyList<Instrument> instruments = provider.GetAll();

        Assert.NotEmpty(instruments);

        foreach (Instrument instrument in instruments)
        {
            // An increment of zero is what a renamed tick-size field looks like, and it is the one that cannot be
            // worked around: every price and size this engine makes is rounded to it.
            Assert.True(instrument.PriceIncrement.Value > 0m, $"{venue} {instrument.Id}: no price increment");
            Assert.True(instrument.SizeIncrement.Value > 0m, $"{venue} {instrument.Id}: no size increment");

            // The precisions have to agree with the increments, because the engine rounds with one and formats with
            // the other; a venue changing how it publishes either is exactly the drift being looked for.
            Assert.Equal(instrument.PriceIncrement.Precision, instrument.PricePrecision);
            Assert.Equal(instrument.SizeIncrement.Precision, instrument.SizePrecision);

            Assert.False(string.IsNullOrWhiteSpace(instrument.RawSymbol.Value), $"{venue} {instrument.Id}: no venue symbol");
            Assert.False(string.IsNullOrWhiteSpace(instrument.QuoteCurrency.Code), $"{venue} {instrument.Id}: no quote currency");
        }

        // And a market everybody has, found by what it IS rather than by a spelling - the spellings are the thing
        // this engine cannot assume, which is why a listing is resolved rather than composed.
        Assert.Contains(instruments, i =>
            i.BaseCurrency?.Code.Contains("BTC", StringComparison.OrdinalIgnoreCase) == true
            || i.BaseCurrency?.Code.Contains("XBT", StringComparison.OrdinalIgnoreCase) == true
            || i.Id.Symbol.Value.Contains("BTC", StringComparison.OrdinalIgnoreCase)
            || i.Id.Symbol.Value.Contains("XBT", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// <b>What the margin recordings assert.</b> A derivative family whose venue publishes a requirement publicly must
    /// still publish it: a margin that silently becomes zero is a run that sizes every position as if nothing had to
    /// be posted, and `MarginSource` is the field that says which of the two happened.
    /// </summary>
    [LiveTheory]
    [InlineData("bybit-linear")]
    [InlineData("okx-swap")]
    [InlineData("kucoin-futures")]
    [InlineData("bitget-usdt")]
    [InlineData("gate-futures")]
    [InlineData("hyperliquid")]
    public async Task A_derivative_family_still_publishes_what_it_requires(string family)
    {
        IInstrumentProvider provider = await LoadAsync(family);
        IReadOnlyList<Instrument> instruments = provider.GetAll();

        Assert.NotEmpty(instruments);

        // Per contract, so one contract losing its tiers is visible rather than averaged away.
        IReadOnlyList<Instrument> silent = [.. instruments.Where(i => i.MarginSource == MarginSource.VenueSilent)];

        Assert.True(
            silent.Count < instruments.Count,
            $"{family}: every one of {instruments.Count} contracts came back with no margin requirement, which is what "
            + "a renamed or withdrawn margin field looks like");
    }

    /// <summary>
    /// <b>And the leverage ceiling</b>, on the venues that publish one. It reached only Info until 0.9.1, so the check
    /// that it is on the instrument is new and worth keeping pointed at the live answer: the guard that refuses an
    /// over-limit leverage reads this property and skips an instrument without it.
    /// </summary>
    [LiveTheory]
    [InlineData("bybit-linear")]
    [InlineData("okx-swap")]
    [InlineData("kucoin-futures")]
    [InlineData("bitget-usdt")]
    [InlineData("gate-futures")]
    [InlineData("hyperliquid")]
    public async Task A_venue_that_publishes_a_leverage_ceiling_still_puts_it_on_the_instrument(string family)
    {
        IInstrumentProvider provider = await LoadAsync(family);

        Assert.Contains(provider.GetAll(), i => i.MaxLeverage is > 1m);
    }
}
