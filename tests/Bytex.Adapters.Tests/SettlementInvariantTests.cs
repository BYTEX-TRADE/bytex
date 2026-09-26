using Bytex.Adapters.Binance;
using Bytex.Adapters.Bybit;
using Bytex.Adapters.Kucoin;
using Bytex.Adapters.Okx;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Instruments;

namespace Bytex.Adapters.Tests;

// Why: a family declares what collateralises it and its instruments carry what they actually settle in, and those two
// can drift apart without anything failing. This asks both and compares them, per venue, against recorded payloads.
//
// It also pins the arithmetic that hangs off the same fact. A contract settled in what it is priced against is LINEAR:
// notional is quantity x price. A contract settled in its own BASE currency is INVERSE: sized in quote-currency
// contracts and settled in the coin, so notional divides by the price instead of multiplying, and margin, commission,
// funding and the liquidation price all follow. `Instrument.IsInverse` is what tells them apart, and an instrument
// that settles in its base currency while claiming to be linear is wrong by a factor of the price squared - silently,
// with a plausible number.
//
// Both halves are asserted against venues whose payloads really contain the coin-settled case, and the test asserts
// that they do: a suite that checks an invariant only where it cannot be violated has checked nothing, and an earlier
// draft of this file did exactly that on a venue whose provider filters those contracts out.
public sealed class SettlementInvariantTests
{
    private static void AssertAgrees(IEnumerable<Instrument> instruments, VenueFamily family, string venue, int expectedInverse)
    {
        Instrument[] loaded = [.. instruments];
        int inverse = 0;

        Assert.NotEmpty(loaded);

        foreach (Instrument instrument in loaded)
        {
            if (instrument.InstrumentClass == InstrumentClass.Spot)
            {
                continue;
            }

            Assert.True(
                family.Collateral.Holds(instrument.SettlementCurrency, instrument.BaseCurrency, instrument.QuoteCurrency),
                $"{venue}'s '{family.Name}' declares collateral {family.Collateral.Kind} but {instrument.Id} settles in "
                + $"{instrument.SettlementCurrency.Code}, based on {instrument.BaseCurrency?.Code} and quoted in "
                + $"{instrument.QuoteCurrency.Code}. A declaration its own instruments contradict is worse than none: "
                + "a host reads it instead of looking.");

            bool settlesInBase = instrument.BaseCurrency is not null && instrument.SettlementCurrency == instrument.BaseCurrency;

            Assert.True(
                settlesInBase == instrument.IsInverse,
                $"{venue}'s {instrument.Id} settles in {instrument.SettlementCurrency.Code}, is based on "
                + $"{instrument.BaseCurrency?.Code} and reports IsInverse={instrument.IsInverse}. A contract settled in "
                + "its own base currency is inverse - its notional divides by the price instead of multiplying - so one "
                + "of those two facts is wrong and everything computed from the notional follows it.");

            if (instrument.IsInverse)
            {
                inverse++;
            }
        }

        Assert.Equal(expectedInverse > 0, inverse > 0);
    }

    [Fact]
    public async Task Bybits_inverse_family_settles_in_the_coin_and_says_both_things()
    {
        // The non-vacuous half: this family really produces inverse instruments, so the invariant is exercised rather
        // than stepped around.
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/v5/market/instruments-info", BybitPayloads.InverseInstruments)
            .On("GET", BybitVenue.RiskLimitPath, BybitPayloads.InverseRiskLimits)
            .Handle);

        using BybitHttp http = new(new BybitDataClientConfig { ProductType = BybitProductType.Inverse, BaseUrlHttp = server.HttpBase });
        BybitInstrumentProvider provider = new(http, BybitProductType.Inverse);
        await provider.LoadAllAsync(CancellationToken.None);

        AssertAgrees(provider.GetAll(), Family("Bybit", "inverse"), "BYBIT", expectedInverse: 1);
    }

    [Fact]
    public async Task Bybits_linear_family_settles_in_the_quote_and_is_not_inverse()
    {
        // The same venue's other half, so a change that made everything inverse would not pass by agreeing with the
        // test above.
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/v5/market/instruments-info", BybitPayloads.LinearInstrumentsPage1.Replace("cursor-page-2", string.Empty, StringComparison.Ordinal))
            .On("GET", BybitVenue.RiskLimitPath, BybitPayloads.LinearRiskLimits)
            .Handle);

        using BybitHttp http = new(new BybitDataClientConfig { ProductType = BybitProductType.Linear, BaseUrlHttp = server.HttpBase });
        BybitInstrumentProvider provider = new(http, BybitProductType.Linear);
        await provider.LoadAllAsync(CancellationToken.None);

        AssertAgrees(provider.GetAll(), Family("Bybit", "linear"), "BYBIT", expectedInverse: 0);
    }

    [Fact]
    public async Task Binances_coin_margined_family_settles_in_the_coin_and_says_both_things()
    {
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/dapi/v1/exchangeInfo", BinancePayloads.CoinMExchangeInfo)
            .Handle);

        using BinanceHttp http = new(new BinanceDataClientConfig { AccountType = BinanceAccountType.CoinMFutures, BaseUrlHttp = server.HttpBase }, null);
        BinanceInstrumentProvider provider = new(http, BinanceAccountType.CoinMFutures, null, null);
        await provider.LoadAllAsync(CancellationToken.None);

        AssertAgrees(provider.GetAll(), Family("Binance", "coinm-futures"), "BINANCE", expectedInverse: 1);
    }

    [Fact]
    public async Task Kucoins_futures_family_declares_the_quote_and_excludes_what_would_contradict_it()
    {
        // This venue lists six inverse contracts among its 690 and the provider leaves them out, counted and logged,
        // because the family declares perpetuals settled in the quote. The fixture carries three such rows - XBTUSDM,
        // XBTMU26, ETHUSDXM - so what is asserted here is that the exclusion holds: the declaration says quote, and
        // nothing the provider produced settles in a coin.
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/api/v1/contracts/active", KucoinPayloads.FuturesContracts)
            .Handle);

        KucoinHttp http = new(new KucoinDataClientConfig { ProductType = KucoinProductType.Futures, BaseUrlHttp = server.HttpBase });
        KucoinFuturesInstrumentProvider provider = new(http);
        await provider.LoadAllAsync(CancellationToken.None);

        AssertAgrees(provider.GetAll(), Family("Kucoin", "futures"), "KUCOIN", expectedInverse: 0);
    }

    [Fact]
    public async Task Okxs_swap_family_declares_the_quote_and_excludes_what_would_contradict_it()
    {
        // As KuCoin: this adapter drops the venue's inverse contracts by the venue's own ctType and says how many in
        // its log. The declaration is what tells a host that this family holds no coin-margined market at all.
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/api/v5/public/instruments", OkxPayloads.SwapInstruments)
            .On("GET", "/api/v5/public/position-tiers", _ => StubResponse.Json(OkxPayloads.SwapTiers))
            .Handle);

        OkxInstrumentProvider provider = new(
            new OkxHttp(new OkxDataClientConfig { InstrumentType = OkxInstrumentType.Swap, BaseUrlHttp = server.HttpBase }),
            OkxInstrumentType.Swap);
        await provider.LoadAllAsync(CancellationToken.None);

        AssertAgrees(provider.GetAll(), Family("Okx", "swap"), "OKX", expectedInverse: 0);
    }

    private static VenueFamily Family(string venue, string family) =>
        Repo.Describe(venue)!.Families.Single(f => f.Name == family);
}
