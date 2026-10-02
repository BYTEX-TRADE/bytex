using Bytex.Adapters.Bitget;
using Bytex.Adapters.Gate;
using Bytex.Adapters.Okx;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;

namespace Bytex.Adapters.Tests;

// Why: a host resolving a NAME - "the perpetual on ETH against USDT, whatever this venue calls it" - needs the venue's
// spelling for every market it lists and full detail for the one it finds. It had no way to ask for the first without
// the second, and on the venues that enrich a listing afterwards that is the entire cost: OKX fetches margin tiers five
// families at a time, so 477 contracts is about ninety-six extra requests whose answers a name lookup discards. Measured
// by a host: 2.4 s to load one known instrument, 40.8 s to find the same one by its pair.
//
// The answer is a listing ROW rather than a cheap instrument, and these tests are mostly about that distinction. An
// instrument without its margin requirement can be traded, and a position sized against no venue requirement looks
// exactly like a position; a row cannot be traded by anything, so the cheap path cannot quietly become the dangerous
// one.
public sealed class InstrumentListingTests
{
    private const string OkxInstrumentsPath = "/api/v5/public/instruments";
    private const string OkxTiersPath = "/api/v5/public/position-tiers";

    // ----- OKX: the venue the measurement came from -----

    [Fact]
    public async Task Listing_okx_asks_once_where_loading_asks_again_for_every_five_families()
    {
        List<string> paths = [];
        await using LoopbackServer server = new(new Routes()
            .On("GET", OkxInstrumentsPath, _ => { lock (paths) { paths.Add(OkxInstrumentsPath); } return StubResponse.Json(OkxPayloads.SwapInstruments); })
            .On("GET", OkxTiersPath, _ => { lock (paths) { paths.Add(OkxTiersPath); } return StubResponse.Json(OkxPayloads.SwapTiers); })
            .Handle);

        OkxHttp http = new(new OkxDataClientConfig { BaseUrlHttp = server.HttpBase }, null);
        OkxInstrumentProvider provider = new(http, OkxInstrumentType.Swap);

        IReadOnlyList<InstrumentListing> listed = await provider.ListAsync(CancellationToken.None);

        // One request, and none of it to the endpoint that costs a request per five families.
        Assert.Equal([OkxInstrumentsPath], paths);
        Assert.DoesNotContain(OkxTiersPath, paths, StringComparer.Ordinal);

        // And it answers the question it exists for: the venue's own spelling for a pair nobody could have composed.
        InstrumentListing btc = Assert.Single(listed, l => l.Id == MarketKey.Parse("bx-market:v2/OKX/BTC-USDT-SWAP"));
        Assert.Equal("BTC-USDT-SWAP", btc.RawSymbol.Value);
        Assert.Equal(InstrumentClass.Swap, btc.InstrumentClass);
        Assert.Equal("BTC", btc.BaseCurrency?.Code);
        Assert.Equal("USDT", btc.QuoteCurrency.Code);
        Assert.False(btc.IsInverse);
    }

    /// <summary>
    /// <b>The safety property.</b> A listing publishes nothing: the provider is as empty afterwards as before, so a
    /// market found in one still has to be loaded before anything can trade it. Without this the cheap path would be
    /// a way to obtain instruments carrying no margin requirement and then trade them, which is invisible in a result.
    /// </summary>
    [Fact]
    public async Task A_listing_publishes_nothing_so_nothing_can_be_traded_from_one()
    {
        await using LoopbackServer server = new(new Routes()
            .On("GET", OkxInstrumentsPath, _ => StubResponse.Json(OkxPayloads.SwapInstruments))
            .On("GET", OkxTiersPath, _ => StubResponse.Json(OkxPayloads.SwapTiers))
            .Handle);

        OkxHttp http = new(new OkxDataClientConfig { BaseUrlHttp = server.HttpBase }, null);
        OkxInstrumentProvider provider = new(http, OkxInstrumentType.Swap);

        IReadOnlyList<InstrumentListing> listed = await provider.ListAsync(CancellationToken.None);

        Assert.NotEmpty(listed);
        Assert.Equal(0, provider.Count);
        Assert.Empty(provider.GetAll());
        Assert.Null(provider.Find(MarketKey.Parse("bx-market:v2/OKX/BTC-USDT-SWAP")));

        // Loading the one that was found is what publishes it, with the margin a listing never fetched.
        await provider.LoadAllAsync(CancellationToken.None);

        Instrument loaded = provider.Find(MarketKey.Parse("bx-market:v2/OKX/BTC-USDT-SWAP"))!;
        Assert.NotNull(loaded);
        Assert.True(loaded.MarginInit > 0m, "a loaded instrument carries the margin requirement a listing row does not");
    }

    // ----- Bitget: one tier request per CONTRACT, so the saving is larger still -----

    [Fact]
    public async Task Listing_bitget_asks_once_where_loading_asks_for_every_contracts_tiers()
    {
        List<string> paths = [];
        await using LoopbackServer server = new(new Routes()
            .On("GET", BitgetInstrumentProvider.ContractsPath, _ => { lock (paths) { paths.Add("contracts"); } return StubResponse.Json(BitgetPayloads.UsdtContracts); })
            .On("GET", BitgetInstrumentProvider.PositionTiersPath, _ => { lock (paths) { paths.Add("tiers"); } return StubResponse.Json(BitgetPayloads.BtcUsdtPositionTiers); })
            .Handle);

        BitgetHttp http = new(new BitgetDataClientConfig { ProductType = BitgetProductType.UsdtFutures, BaseUrlHttp = server.HttpBase });
        BitgetInstrumentProvider provider = new(http);

        IReadOnlyList<InstrumentListing> listed = await provider.ListAsync(CancellationToken.None);

        Assert.Equal(["contracts"], paths);
        Assert.NotEmpty(listed);
        Assert.Equal(0, provider.Count);
        Assert.Contains(listed, l => l.Id == MarketKey.Parse("bx-market:v2/BITGET/BTCUSDT-PERP"));
    }

    // ----- and every other provider answers correctly without having to be changed -----

    /// <summary>
    /// The default is a full load described, which is right for a provider with nothing to skip - its listing endpoint
    /// already carried everything - and is what makes this safe to add without touching eight adapters at once. It is
    /// correct rather than cheap, and the distinction that matters is kept either way: the rows are rows.
    /// </summary>
    [Fact]
    public async Task A_provider_that_has_not_overridden_listing_still_answers_from_its_full_load()
    {
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/api/v4/futures/usdt/contracts", GatePayloads.FuturesContracts)
            .Handle);

        GateHttp http = new(new GateDataClientConfig { ProductType = GateProductType.Futures, BaseUrlHttp = server.HttpBase });
        GateFuturesInstrumentProvider provider = new(http);

        IReadOnlyList<InstrumentListing> listed = await provider.ListAsync(CancellationToken.None);

        Assert.NotEmpty(listed);
        Assert.Contains(listed, l => l.Id == MarketKey.Parse("bx-market:v2/GATE/BTC_USDT"));

        // This one DID load, because that is its only way to answer, so its instruments are published - which is the
        // behaviour that was there before and is not made worse by the row existing.
        Assert.True(provider.Count > 0);
    }

    /// <summary>
    /// A row carries what tells a coin-settled contract from its sibling, because that is exactly what a host
    /// resolving a pair has to choose between - BTC/USD on a venue that lists both is two different markets.
    /// </summary>
    [Fact]
    public void A_row_says_what_a_market_settles_in_and_whether_it_is_inverse()
    {
        Instrument perpetual = BybitExecRig.InversePerpetual();
        InstrumentListing row = InstrumentListing.Of(perpetual);

        Assert.Equal(perpetual.Id, row.Id);
        Assert.Equal(perpetual.RawSymbol, row.RawSymbol);
        Assert.Equal(perpetual.SettlementCurrency, row.SettlementCurrency);
        Assert.Equal(perpetual.IsInverse, row.IsInverse);
        Assert.Equal(perpetual.InstrumentClass, row.InstrumentClass);
    }
}
