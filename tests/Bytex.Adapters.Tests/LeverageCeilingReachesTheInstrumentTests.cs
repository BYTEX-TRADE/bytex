using Bytex.Adapters.Bitget;
using Bytex.Adapters.Gate;
using Bytex.Adapters.Hyperliquid;
using Bytex.Adapters.Okx;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Adapters;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;

namespace Bytex.Adapters.Tests;

// Why: a run is documented to refuse to start when it is configured for more leverage than the venue grants, and on
// four of the eight venues it did not. LeverageGuard reads Instrument.MaxLeverage and skips an instrument without one,
// and OKX, Bitget, Gate and Hyperliquid each read the venue's published ceiling and filed it in Info without ever
// putting it on the instrument - so the guard had nothing to compare against and every over-limit configuration was
// accepted in silence. The venue then grants what it will and trades on, and the strategy runs at a size it was never
// tested at, which is the one failure a report cannot show because the numbers look like numbers.
//
// Three of the four also compute their margin requirement from the same figure, so it was demonstrably parsed. This is
// the test that the value reaches the property the guard reads, per venue, off each venue's own recorded response.
public sealed class LeverageCeilingReachesTheInstrumentTests
{
    /// <summary>Only that a real ceiling arrived: the figures themselves belong to the venues and change with them.</summary>
    private static void Granted(Instrument instrument, string venue)
    {
        Assert.NotNull(instrument);
        Assert.NotNull(instrument.MaxLeverage);
        Assert.True(instrument.MaxLeverage > 1m, $"{venue} publishes a ceiling above 1x and the instrument must carry it");

        // And the guard bites on it, which is the whole point of carrying it: asking for more than the venue grants
        // is refused rather than logged. Asserted through the guard rather than by reading the number again, because
        // the defect was never the number - it was that nothing compared it.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LeverageGuard.EnsureGranted(instrument.MaxLeverage.Value + 1m, [instrument], venue));
        LeverageGuard.EnsureGranted(instrument.MaxLeverage.Value, [instrument], venue);
    }

    [Fact]
    public async Task Okx_puts_its_published_ceiling_on_the_instrument()
    {
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/api/v5/public/instruments", r => StubResponse.Json(OkxPayloads.SwapInstruments))
            .On("GET", "/api/v5/public/position-tiers", r => StubResponse.Json(OkxPayloads.SwapTiers))
            .Handle);

        OkxHttp http = new(new OkxDataClientConfig { BaseUrlHttp = server.HttpBase }, null);
        OkxInstrumentProvider provider = new(http, OkxInstrumentType.Swap);

        await provider.LoadAllAsync(CancellationToken.None);

        Granted(provider.Find(MarketKey.Parse("bx-market:v2/OKX/BTC-USDT-SWAP"))!, "OKX");
    }

    [Fact]
    public async Task Bitget_puts_its_published_ceiling_on_the_instrument()
    {
        await using LoopbackServer server = new(new Routes()
            .On("GET", BitgetInstrumentProvider.ContractsPath, BitgetPayloads.UsdtContracts)
            .On("GET", BitgetInstrumentProvider.PositionTiersPath, r => StubResponse.Json(BitgetPayloads.BtcUsdtPositionTiers))
            .Handle);

        BitgetHttp http = new(new BitgetDataClientConfig { ProductType = BitgetProductType.UsdtFutures, BaseUrlHttp = server.HttpBase });
        BitgetInstrumentProvider provider = new(http);

        await provider.LoadAllAsync(CancellationToken.None);

        Granted(provider.Find(MarketKey.Parse("bx-market:v2/BITGET/BTCUSDT-PERP"))!, "BITGET");
    }

    [Fact]
    public async Task Gate_puts_its_published_ceiling_on_the_instrument()
    {
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/api/v4/futures/usdt/contracts", GatePayloads.FuturesContracts)
            .Handle);

        GateHttp http = new(new GateDataClientConfig { ProductType = GateProductType.Futures, BaseUrlHttp = server.HttpBase });
        GateFuturesInstrumentProvider provider = new(http);

        await provider.LoadAllAsync(CancellationToken.None);

        Granted(provider.Find(MarketKey.Parse("bx-market:v2/GATE/BTC_USDT"))!, "GATE");
    }

    [Fact]
    public async Task Hyperliquid_puts_its_published_ceiling_on_the_instrument()
    {
        await using LoopbackServer server = new(request => request.Path == HyperliquidVenue.InfoPath
            ? StubResponse.Json(HyperliquidPayloads.Meta)
            : StubResponse.Error(404, "no such path"));

        HyperliquidHttp http = new(new HyperliquidDataClientConfig { BaseUrlHttp = server.HttpBase });
        HyperliquidInstrumentProvider provider = new(http);

        await provider.LoadAllAsync(CancellationToken.None);

        Instrument btc = provider.GetAll().First(i => i.Id.Symbol.Value.StartsWith("BTC", StringComparison.Ordinal));

        Granted(btc, "HYPERLIQUID");
    }
}
