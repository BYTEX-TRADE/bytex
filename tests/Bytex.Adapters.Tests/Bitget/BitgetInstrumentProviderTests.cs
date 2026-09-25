using Bytex.Adapters.Bitget;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Adapters.Tests.Bitget;

// Why: the catalog is where a venue's own numbers become the engine's, and Bitget states almost all of them in a
// shape no other venue here uses. Precision is a number of decimal places rather than an increment; a derivative
// tick is a last-digit step at that precision, so a contract with priceEndStep 5 moves in halves; the size step is a
// field called sizeMultiplier that is not the minimum order; the settlement currency is a LIST; and the margin rates
// are not in the catalog at all - they are one request per contract away, in a tier table.
//
// The margin rates are the reason this file matters most. Two other adapters in this repository publish a flat
// 5 percent initial and 2.5 percent maintenance on every contract they hold, and the venue's own tier table gives
// BTCUSDT 0.67 and 0.4 percent. That is not a rounding difference: it is a margin requirement seven times too large
// on the contract everybody trades. Every payload here was recorded from the live venue.
public sealed class BitgetInstrumentProviderTests
{
    private const string SpotSymbolsPath = BitgetInstrumentProvider.SpotSymbolsPath;
    private const string ContractsPath = BitgetInstrumentProvider.ContractsPath;
    private const string TiersPath = BitgetInstrumentProvider.PositionTiersPath;

    /// <summary>
    /// The tier table of whichever contract was asked about. The venue answers for one symbol at a time - asked with
    /// a product type and no symbol it refuses with 400172, and with two comma-separated symbols with 40034 - so this
    /// answers per symbol exactly as it does.
    /// </summary>
    private static StubResponse Tiers(RecordedRequest request) => request.Query("symbol") switch
    {
        "BTCUSDT" => StubResponse.Json(BitgetPayloads.BtcUsdtPositionTiers),
        "BTCPERP" => StubResponse.Json(BitgetPayloads.BtcPerpPositionTiers),
        _ => StubResponse.Json(BitgetPayloads.Error("40034", $"Parameter {request.Query("symbol")} does not exist")),
    };

    private static Routes Venue() => new Routes()
        .On("GET", SpotSymbolsPath, r => StubResponse.Json(r.Query("symbol") is null
            ? BitgetPayloads.SpotSymbols
            : OneSpotPair(r.Query("symbol")!)))
        .On("GET", ContractsPath, r => StubResponse.Json(Contracts(r)))
        .On("GET", TiersPath, Tiers);

    /// <summary>What the spot catalog answers when it is asked about one pair: an array of one, or a refusal.</summary>
    private static string OneSpotPair(string symbol)
    {
        string all = BitgetPayloads.SpotSymbols;
        return all.Contains($"\"symbol\":\"{symbol}\"", StringComparison.Ordinal)
            ? all
            : BitgetPayloads.Error("40034", $"Parameter {symbol} does not exist");
    }

    private static string Contracts(RecordedRequest request) => request.Query("productType") switch
    {
        "USDT-FUTURES" => BitgetPayloads.UsdtContracts,
        "USDC-FUTURES" => BitgetPayloads.UsdcContracts,
        _ => BitgetPayloads.Error("40034", $"Parameter {request.Query("productType")} does not exist"),
    };

    private static BitgetHttp Http(LoopbackServer server, BitgetProductType type) =>
        new(new BitgetDataClientConfig { ProductType = type, BaseUrlHttp = server.HttpBase });

    // ----- spot -----

    [Fact]
    public async Task Spot_pairs_come_back_with_the_precision_the_venue_states_as_decimal_places()
    {
        await using LoopbackServer server = new(Venue().Handle);
        using BitgetHttp http = Http(server, BitgetProductType.Spot);
        BitgetInstrumentProvider provider = new(http);

        await provider.LoadAllAsync(CancellationToken.None);

        Assert.Equal(3, provider.Count);
        Instrument btc = provider.Find(InstrumentId.Parse("BTCUSDT.BITGET"))!;

        Assert.Equal(InstrumentClass.Spot, btc.InstrumentClass);
        Assert.Equal("BTCUSDT", btc.RawSymbol!.Value);

        // pricePrecision 2 and quantityPrecision 6, which the venue states as numbers of places and nothing else.
        Assert.Equal(0.01m, btc.PriceIncrement.Value);
        Assert.Equal(0.000001m, btc.SizeIncrement.Value);
        Assert.Equal(Currencies.USDT, btc.QuoteCurrency);
        Assert.Equal(Currencies.BTC, btc.BaseCurrency);

        // minTradeUSDT, which is the smallest order the venue accepts in quote terms.
        Assert.Equal(1m, btc.MinNotional!.Value.Amount);
    }

    [Fact]
    public async Task A_spot_pair_carries_its_own_fee_and_not_the_venues_headline_one()
    {
        // BTCUSDT charges 0.2 percent where 3115 of the venue's 3169 pairs charge 0.1. The family's declared default
        // is the common figure and is an estimate; the instrument's own rate is the truth, and a backtest on the
        // default would understate this pair's cost by half.
        await using LoopbackServer server = new(Venue().Handle);
        using BitgetHttp http = Http(server, BitgetProductType.Spot);
        BitgetInstrumentProvider provider = new(http);

        await provider.LoadAllAsync(CancellationToken.None);
        Instrument btc = provider.Find(InstrumentId.Parse("BTCUSDT.BITGET"))!;

        Assert.Equal(0.002m, btc.MakerFee);
        Assert.Equal(0.002m, btc.TakerFee);
        Assert.Equal(0.001m, new BitgetPlugin().Describe().Families.Single(f => f.Name == "spot").DefaultFees.Maker);
    }

    [Fact]
    public async Task A_pair_with_a_minimum_of_its_own_keeps_it_and_one_without_gets_the_step()
    {
        await using LoopbackServer server = new(Venue().Handle);
        using BitgetHttp http = Http(server, BitgetProductType.Spot);
        BitgetInstrumentProvider provider = new(http);

        await provider.LoadAllAsync(CancellationToken.None);

        // ETHUSDT states 0.0005 and BTCUSDT states zero, which is the venue saying "no minimum beyond the step".
        Assert.Equal(0.0005m, provider.Find(InstrumentId.Parse("ETHUSDT.BITGET"))!.MinQuantity!.Value.Value);
        Assert.Equal(0.000001m, provider.Find(InstrumentId.Parse("BTCUSDT.BITGET"))!.MinQuantity!.Value.Value);
    }

    [Fact]
    public async Task One_spot_pair_can_be_loaded_by_name()
    {
        await using LoopbackServer server = new(Venue().Handle);
        using BitgetHttp http = Http(server, BitgetProductType.Spot);
        BitgetInstrumentProvider provider = new(http);
        InstrumentId id = InstrumentId.Parse("BTCUSDT.BITGET");

        await provider.LoadAsync(id, CancellationToken.None);

        // Only the one that was asked for, whatever else the venue put in the answer.
        Instrument loaded = Assert.Single(provider.GetAll());
        Assert.Equal(id, loaded.Id);
        Assert.Equal("BTCUSDT", Assert.Single(server.RequestsTo(SpotSymbolsPath)).Query("symbol"));
    }

    // ----- the perpetual families -----

    [Fact]
    public async Task A_derivative_tick_is_the_last_digit_step_at_the_stated_precision()
    {
        await using LoopbackServer server = new(Venue().Handle);
        using BitgetHttp http = Http(server, BitgetProductType.UsdtFutures);
        BitgetInstrumentProvider provider = new(http);

        await provider.LoadAllAsync(CancellationToken.None);

        Instrument btc = provider.Find(InstrumentId.Parse("BTCUSDT-PERP.BITGET"))!;
        Instrument xrp = provider.Find(InstrumentId.Parse("XRPUSDT-PERP.BITGET"))!;

        // pricePlace 1 with priceEndStep 1 is a tenth; pricePlace 4 with priceEndStep 1 is a ten-thousandth.
        Assert.Equal(0.1m, btc.PriceIncrement.Value);
        Assert.Equal(0.0001m, xrp.PriceIncrement.Value);

        // sizeMultiplier is the step, and it is not minTradeNum: XRPUSDT trades whole contracts.
        Assert.Equal(0.0001m, btc.SizeIncrement.Value);
        Assert.Equal(1m, xrp.SizeIncrement.Value);
    }

    [Fact]
    public async Task A_contract_is_sized_in_base_currency_so_nothing_has_to_convert_it()
    {
        // The fact that makes this venue simpler than KuCoin's futures market and worth asserting: the venue states a
        // minimum order of 0.0001 BTC and a step of 0.0001 BTC, in base currency, with no contract multiplier
        // anywhere. A quantity crossing this boundary means what it means on Binance and Bybit.
        await using LoopbackServer server = new(Venue().Handle);
        using BitgetHttp http = Http(server, BitgetProductType.UsdtFutures);
        BitgetInstrumentProvider provider = new(http);

        await provider.LoadAllAsync(CancellationToken.None);
        Instrument btc = provider.Find(InstrumentId.Parse("BTCUSDT-PERP.BITGET"))!;

        Assert.Equal(0.0001m, btc.MinQuantity!.Value.Value);
        Assert.Equal(1200m, btc.MaxQuantity!.Value.Value);
        Assert.Equal(1m, btc.Multiplier.Value);
    }

    [Fact]
    public async Task The_margin_rates_come_from_the_venues_own_tier_table()
    {
        // The defect being removed. BTCUSDT's first tier allows 150x, so the initial rate is 1/150 - not 0.05 - and
        // its maintenance rate is 0.4 percent, not 2.5. The two are not a function of each other either: three
        // contracts measured at 50x maximum leverage carry maintenance rates of 1, 1.4 and 1.5 percent, so nothing
        // could have derived one from the other.
        await using LoopbackServer server = new(Venue().Handle);
        using BitgetHttp http = Http(server, BitgetProductType.UsdtFutures);
        BitgetInstrumentProvider provider = new(http);

        await provider.LoadAllAsync(CancellationToken.None);
        Instrument btc = provider.Find(InstrumentId.Parse("BTCUSDT-PERP.BITGET"))!;

        Assert.Equal(1m / 150m, btc.MarginInit);
        Assert.Equal(0.0040m, btc.MarginMaint);
        Assert.NotEqual(0.05m, btc.MarginInit);
        Assert.NotEqual(0.025m, btc.MarginMaint);
    }

    [Fact]
    public async Task The_maximum_leverage_the_venue_allows_is_kept_on_the_instrument()
    {
        // So that whatever sets a leverage can see what would be refused before it asks, rather than learning it from
        // a rejection at connect time.
        await using LoopbackServer server = new(Venue().Handle);
        using BitgetHttp http = Http(server, BitgetProductType.UsdtFutures);
        BitgetInstrumentProvider provider = new(http);

        await provider.LoadAllAsync(CancellationToken.None);
        Instrument btc = provider.Find(InstrumentId.Parse("BTCUSDT-PERP.BITGET"))!;

        Assert.Equal("150", btc.Info![BitgetInstrumentProvider.MaxLeverageInfo]);
    }

    [Fact]
    public async Task A_contract_whose_tiers_the_venue_will_not_give_is_still_loaded()
    {
        // An instrument nobody can find is worse than one whose maintenance rate a caller has to ask about, so a
        // refused tier request loses the maintenance rate and nothing else - and the initial rate still comes from
        // the contract's own maximum leverage, which was measured to be the same figure the first tier allows.
        await using LoopbackServer server = new(Venue().Handle);
        using BitgetHttp http = Http(server, BitgetProductType.UsdtFutures);
        BitgetInstrumentProvider provider = new(http);

        await provider.LoadAllAsync(CancellationToken.None);
        Instrument xrp = provider.Find(InstrumentId.Parse("XRPUSDT-PERP.BITGET"))!;

        Assert.Equal(1m / 125m, xrp.MarginInit);
        Assert.Equal(0m, xrp.MarginMaint);
    }

    [Fact]
    public async Task The_tiers_are_asked_for_one_contract_at_a_time_because_that_is_all_the_venue_answers()
    {
        await using LoopbackServer server = new(Venue().Handle);
        using BitgetHttp http = Http(server, BitgetProductType.UsdtFutures);
        BitgetInstrumentProvider provider = new(http);

        await provider.LoadAllAsync(CancellationToken.None);

        // One request per contract in the list, each naming its own symbol. This is what makes listing a whole
        // derivative family expensive on this venue - 805 contracts, 805 requests - and there is no endpoint that
        // answers for more than one.
        Assert.Equal(3, server.RequestsTo(TiersPath).Count);
        Assert.Equal(
            ["BTCUSDT", "ETHUSDT", "XRPUSDT"],
            server.RequestsTo(TiersPath).Select(r => r.Query("symbol")!).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_whole_tier_table_can_be_read_without_a_node()
    {
        // A host pricing a position of a given size needs the bands and not the first row, and there is nowhere on an
        // instrument to put a table.
        await using LoopbackServer server = new(Venue().Handle);
        using BitgetHttp http = Http(server, BitgetProductType.UsdtFutures);

        IReadOnlyList<BitgetPositionTier> tiers = await BitgetInstrumentProvider.FetchPositionTiersAsync(http, "BTCUSDT");

        Assert.Equal(12, tiers.Count);
        Assert.Equal(1, tiers[0].Level);
        Assert.Equal(0m, tiers[0].StartUnit);
        Assert.Equal(200000m, tiers[0].EndUnit);
        Assert.Equal(150m, tiers[0].Leverage);
        Assert.Equal(0.0040m, tiers[0].KeepMarginRate);

        // And the last band is where leverage runs out: one times, at sixty percent maintenance.
        Assert.Equal(12, tiers[^1].Level);
        Assert.Equal(1m, tiers[^1].Leverage);
    }

    [Fact]
    public async Task A_usdc_contract_gains_the_quote_currency_its_symbol_leaves_out()
    {
        await using LoopbackServer server = new(Venue().Handle);
        using BitgetHttp http = Http(server, BitgetProductType.UsdcFutures);
        BitgetInstrumentProvider provider = new(http);

        await provider.LoadAllAsync(CancellationToken.None);

        Instrument btc = provider.Find(InstrumentId.Parse("BTCUSDC-PERP.BITGET"))!;

        // The id says what the position is margined in; the raw symbol is still the venue's own.
        Assert.Equal("BTCPERP", btc.RawSymbol!.Value);
        Assert.Equal(Currencies.USDC, btc.QuoteCurrency);
        Assert.Equal(Currencies.USDC, btc.SettlementCurrency);
        Assert.Equal(InstrumentClass.Swap, btc.InstrumentClass);

        // Its own tier table, which allows 125x where BTCUSDT allows 150x.
        Assert.Equal(1m / 125m, btc.MarginInit);
        Assert.Equal(0.0040m, btc.MarginMaint);
    }

    [Fact]
    public async Task The_settlement_currency_comes_from_the_list_the_venue_publishes()
    {
        // It is a list rather than a field - supportMarginCoins - and every contract in both families listed exactly
        // one entry, which is the family's own collateral.
        await using LoopbackServer server = new(Venue().Handle);
        using BitgetHttp usdt = Http(server, BitgetProductType.UsdtFutures);
        using BitgetHttp usdc = Http(server, BitgetProductType.UsdcFutures);
        BitgetInstrumentProvider linear = new(usdt);
        BitgetInstrumentProvider coin = new(usdc);

        await linear.LoadAllAsync(CancellationToken.None);
        await coin.LoadAllAsync(CancellationToken.None);

        Assert.Equal(Currencies.USDT, linear.Find(InstrumentId.Parse("BTCUSDT-PERP.BITGET"))!.SettlementCurrency);
        Assert.Equal(Currencies.USDC, coin.Find(InstrumentId.Parse("BTCUSDC-PERP.BITGET"))!.SettlementCurrency);
    }

    [Fact]
    public async Task Every_contract_a_perpetual_family_returns_is_a_perpetual()
    {
        // The class comes from the venue's own symbolType field and never from how a symbol is spelled, which is the
        // one fact hosts were inferring from a "-PERP" suffix that is a convention rather than a fact.
        await using LoopbackServer server = new(Venue().Handle);
        using BitgetHttp http = Http(server, BitgetProductType.UsdtFutures);
        BitgetInstrumentProvider provider = new(http);

        await provider.LoadAllAsync(CancellationToken.None);

        Assert.NotEmpty(provider.GetAll());
        Assert.All(provider.GetAll(), i => Assert.Equal(InstrumentClass.Swap, i.InstrumentClass));
        Assert.All(provider.GetAll(), i => Assert.IsType<CryptoPerpetual>(i));
    }

    [Fact]
    public async Task A_contract_the_venue_does_not_call_a_perpetual_is_left_out()
    {
        // Nothing is filtered out by this today - the venue's only delivery contracts are in a product type whose
        // list is empty - and the check is here because "the list is empty" and "this family is perpetuals" are two
        // facts that happen to coincide. A delivery contract published as a perpetual would carry no expiry, be
        // assumed funded, and contradict a declaration nothing else could catch.
        string delivered = BitgetPayloads.UsdtContracts
            .Replace("\"symbolType\":\"perpetual\"", "\"symbolType\":\"delivery\"", StringComparison.Ordinal);

        await using LoopbackServer server = new(new Routes()
            .On("GET", ContractsPath, delivered)
            .On("GET", TiersPath, Tiers)
            .Handle);

        using BitgetHttp http = Http(server, BitgetProductType.UsdtFutures);
        BitgetInstrumentProvider provider = new(http);

        await provider.LoadAllAsync(CancellationToken.None);

        Assert.Empty(provider.GetAll());
    }

    [Fact]
    public async Task A_halted_pair_is_left_out()
    {
        // Spot says "online" and the derivatives say "normal", and six of the venue's pairs were neither.
        string halted = BitgetPayloads.SpotSymbols.Replace("\"status\":\"online\"", "\"status\":\"halt\"", StringComparison.Ordinal);

        await using LoopbackServer server = new(new Routes().On("GET", SpotSymbolsPath, halted).Handle);
        using BitgetHttp http = Http(server, BitgetProductType.Spot);
        BitgetInstrumentProvider provider = new(http);

        await provider.LoadAllAsync(CancellationToken.None);

        Assert.Empty(provider.GetAll());
    }

    [Fact]
    public async Task A_quote_filter_keeps_only_that_quote_currency()
    {
        await using LoopbackServer server = new(Venue().Handle);
        using BitgetHttp http = Http(server, BitgetProductType.Spot);
        BitgetInstrumentProvider provider = new(http, new InstrumentProviderConfig { LoadAll = true });

        await provider.LoadAllAsync(CancellationToken.None, new Dictionary<string, string>(StringComparer.Ordinal) { ["quote"] = "usdt" });

        Assert.Equal(3, provider.Count);

        BitgetInstrumentProvider none = new(http);
        await none.LoadAllAsync(CancellationToken.None, new Dictionary<string, string>(StringComparer.Ordinal) { ["quote"] = "EUR" });
        Assert.Empty(none.GetAll());
    }
}
