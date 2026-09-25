using System.Globalization;
using System.Text.Json;
using Bytex.Adapters.Binance;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Adapters.Tests.Binance;

// Why (R11.10): this venue's coin-margined market is a THIRD family and not the USD-margined one with a different
// host. Everything below was measured on 2026-09-25 against the live public endpoints, and every one of these is a
// place where copying the sibling family would have been wrong:
//
//   host            dapi.binance.com and dstream.binance.com, its own
//   balances        /dapi/v1/balance      - /dapi/v2/balance is not served at all
//   positions       /dapi/v1/positionRisk - /dapi/v2/positionRisk is not served at all
//   tradability     contractStatus        - the field `status` does not exist on this family
//   contract size   100 USD on BTCUSD, 10 USD on the other 27 - the other family publishes no size at all
//   quantity unit   whole contracts, step 1, quantityPrecision 0 - not base units
//   money           INVERSE: quoted in USD, margined and settled in the base coin
//   fees            cheaper than the USD-margined schedule
//   sockets         one; its unrouted /stream really carries all six stream kinds
//   symbol filter   IGNORED on exchangeInfo, the same defect as its sibling's - measured, not assumed
//
// The one that would have been quietest is the contract size. A multiplier left at one values 200 contracts of
// BTCUSD_PERP at 0.004 BTC instead of 0.4, so margin, commission and the liquidation price all come out a hundredth
// of what they are - and a backtest on it completes and looks like a result.
public sealed class BinanceCoinMTests
{
    private static BinanceDataClientConfig Config(string httpBase, bool withKey = false) => new()
    {
        AccountType = BinanceAccountType.CoinMFutures,
        BaseUrlHttp = httpBase,
        ApiKey = withKey ? "test-key" : null,
        ApiSecret = withKey ? "test-secret" : null,
    };

    private static Routes Catalog() => new Routes().On("GET", "/dapi/v1/exchangeInfo", BinancePayloads.CoinMExchangeInfo);

    private static async Task<BinanceInstrumentProvider> LoadedAsync(LoopbackServer server, bool withKey = false)
    {
        BinanceHttp http = new(Config(server.HttpBase, withKey), null);
        BinanceInstrumentProvider provider = new(http, BinanceAccountType.CoinMFutures, null, null);
        await provider.LoadAllAsync(CancellationToken.None);
        return provider;
    }

    // ----- where it answers -----

    [Fact]
    public void The_coin_margined_family_answers_on_its_own_hosts_under_its_own_prefix()
    {
        BinanceDataClientConfig config = new() { AccountType = BinanceAccountType.CoinMFutures };

        Assert.Equal("https://dapi.binance.com", BinanceVenue.HttpBase(config));
        Assert.Equal("wss://dstream.binance.com", BinanceVenue.WsBase(config));
        Assert.Equal("/dapi/v1", BinanceVenue.ApiPrefix(BinanceAccountType.CoinMFutures));

        // And none of them is the sibling family's. Stated because the three hosts differ by one letter, and a
        // pattern that holds is not the same thing as a host that was checked.
        Assert.NotEqual(BinanceVenue.UsdMFuturesHttpBase, BinanceVenue.HttpBase(config));
        Assert.NotEqual(BinanceVenue.UsdMFuturesWsBase, BinanceVenue.WsBase(config));
    }

    [Theory]
    [InlineData(BinanceAccountType.UsdMFutures, "/fapi/v2/balance", "/fapi/v2/positionRisk")]
    [InlineData(BinanceAccountType.CoinMFutures, "/dapi/v1/balance", "/dapi/v1/positionRisk")]
    public void The_two_futures_families_read_their_accounts_at_different_endpoint_versions(BinanceAccountType type, string balance, string positions)
    {
        // The difference that is invisible in a prefix. Measured: /dapi/v2/balance and /fapi/v1/balance are both
        // served an HTML error page, so neither family's path exists under the other's version, and a shared
        // prefix would have found the wrong one on whichever family nobody tested with a key.
        Assert.Equal(balance, BinanceVenue.BalancePath(type));
        Assert.Equal(positions, BinanceVenue.PositionRiskPath(type));
    }

    [Theory]
    [InlineData(BinanceAccountType.CoinMFutures, "/dapi/v1/leverage", "/dapi/v1/leverageBracket", "/dapi/v1/listenKey", "/dapi/v1/allOpenOrders", "/dapi/v1/userTrades")]
    [InlineData(BinanceAccountType.UsdMFutures, "/fapi/v1/leverage", "/fapi/v1/leverageBracket", "/fapi/v1/listenKey", "/fapi/v1/allOpenOrders", "/fapi/v1/userTrades")]
    public void Every_other_signed_path_hangs_off_the_family_prefix(
        BinanceAccountType type,
        string leverage,
        string brackets,
        string listenKey,
        string cancelAll,
        string fills)
    {
        Assert.Equal(leverage, BinanceVenue.LeveragePath(type));
        Assert.Equal(brackets, BinanceVenue.LeverageBracketPath(type));
        Assert.Equal(listenKey, BinanceVenue.ListenKeyPath(type));
        Assert.Equal(cancelAll, BinanceVenue.AllOpenOrdersPath(type));
        Assert.Equal(fills, BinanceVenue.UserTradesPath(type));
    }

    [Fact]
    public void Spot_is_not_a_futures_family_and_has_no_positions_to_report()
    {
        Assert.True(BinanceVenue.IsFutures(BinanceAccountType.UsdMFutures));
        Assert.True(BinanceVenue.IsFutures(BinanceAccountType.CoinMFutures));
        Assert.False(BinanceVenue.IsFutures(BinanceAccountType.Spot));

        // A cash account holds no position, so asking where it reports one is a question with no answer rather than
        // an endpoint to invent.
        Assert.Throws<ArgumentOutOfRangeException>(() => BinanceVenue.PositionRiskPath(BinanceAccountType.Spot));
    }

    // ----- the symbol, both ways -----

    [Theory]
    [InlineData("BTCUSD_PERP", "PERPETUAL", "BTCUSD_PERP.BINANCE")]
    [InlineData("ETHUSD_PERP", "PERPETUAL", "ETHUSD_PERP.BINANCE")]
    [InlineData("BTCUSD_261225", "CURRENT_QUARTER", "BTCUSD_261225.BINANCE")]
    [InlineData("BTCUSD_270326", "NEXT_QUARTER", "BTCUSD_270326.BINANCE")]
    [InlineData("BTCUSD_260626", "CURRENT_QUARTER DELIVERING", "BTCUSD_260626.BINANCE")]
    public void A_coin_margined_symbol_keeps_the_venues_own_spelling(string raw, string contractType, string expected)
    {
        // The venue has already marked which contract is which - an underscore where the engine's convention uses a
        // dash - so nothing is added. Including for the compound contract type a contract in delivery carries,
        // which no id may depend on parsing.
        InstrumentId id = BinanceVenue.ToInstrumentId(raw, BinanceAccountType.CoinMFutures, contractType);

        Assert.Equal(expected, id.ToString());
        Assert.Equal(new Venue("BINANCE"), id.Venue);
    }

    [Theory]
    [InlineData("BTCUSD_PERP")]
    [InlineData("ETHUSD_PERP")]
    [InlineData("BTCUSD_261225")]
    [InlineData("1000SHIBUSD_PERP")]
    public void A_coin_margined_symbol_survives_the_round_trip_exactly(string raw)
    {
        // Both directions, which is the reason for keeping the venue's spelling. ToRawSymbol is handed an id and no
        // account type, so a "-PERP" added here could only be undone by guessing from the id it was given.
        InstrumentId id = BinanceVenue.ToInstrumentId(raw, BinanceAccountType.CoinMFutures, "PERPETUAL");

        Assert.Equal(raw, BinanceVenue.ToRawSymbol(id));
    }

    [Fact]
    public void A_stream_frame_that_carries_no_contract_type_still_maps_to_the_same_id()
    {
        // Book tickers, trades and order rows name a symbol and not a contract type, so the default has to land on
        // the same id the catalog produced - otherwise a subscription arrives for an instrument nothing holds.
        Assert.Equal(
            BinanceVenue.ToInstrumentId("BTCUSD_PERP", BinanceAccountType.CoinMFutures, "PERPETUAL"),
            BinanceVenue.ToInstrumentId("BTCUSD_PERP", BinanceAccountType.CoinMFutures));

        Assert.Equal("btcusd_perp", BinanceVenue.ToRawSymbol(InstrumentId.Parse("BTCUSD_PERP.BINANCE")).ToLowerInvariant());
    }

    // ----- the catalog -----

    [Fact]
    public async Task The_class_of_a_contract_comes_from_the_venues_contract_type_and_not_from_its_name()
    {
        await using LoopbackServer server = new(Catalog().Handle);

        BinanceInstrumentProvider provider = await LoadedAsync(server);

        // Both perpetuals are swaps although their names end in _PERP rather than -PERP, and the quarterly is a
        // future although its name is the same shape. A reader splitting on the underscore would have made all
        // three dated.
        Assert.IsType<CryptoPerpetual>(provider.Find(InstrumentId.Parse("BTCUSD_PERP.BINANCE")));
        Assert.IsType<CryptoPerpetual>(provider.Find(InstrumentId.Parse("ETHUSD_PERP.BINANCE")));
        CryptoFuture dated = Assert.IsType<CryptoFuture>(provider.Find(InstrumentId.Parse("BTCUSD_261225.BINANCE")));

        Assert.Equal(InstrumentClass.Swap, provider.Find(InstrumentId.Parse("BTCUSD_PERP.BINANCE"))!.InstrumentClass);
        Assert.Equal(InstrumentClass.Future, dated.InstrumentClass);
        Assert.Equal(UnixNanos.FromMilliseconds(1798185600000), dated.Expiration);
    }

    [Fact]
    public async Task A_contract_that_is_not_trading_is_left_out_although_it_publishes_no_status_field()
    {
        // The defect this family would have shipped with. It states tradability in contractStatus and publishes no
        // `status` at all, so the reader that looked only for the sibling's field defaulted every contract to
        // tradable - and the venue's test network lists 13 pending and 8 delivering ones, which a picker would have
        // offered and the venue would have refused on the first order.
        await using LoopbackServer server = new(Catalog().Handle);

        BinanceInstrumentProvider provider = await LoadedAsync(server);

        Assert.Null(provider.Find(InstrumentId.Parse("BTCUSD_260626.BINANCE")));
        Assert.Null(provider.Find(InstrumentId.Parse("EGLDUSD_PERP.BINANCE")));
        Assert.Equal(3, provider.GetAll().Count);
    }

    [Fact]
    public async Task Exchange_info_ignores_its_symbol_filter_here_too_and_one_instrument_still_loads_one()
    {
        // Measured on this family rather than inherited from its sibling: a request naming one symbol, a request
        // naming one that does not exist, and a request naming a pair each answered with the whole contract list.
        await using LoopbackServer server = new(Catalog().Handle);

        BinanceHttp http = new(Config(server.HttpBase), null);
        BinanceInstrumentProvider provider = new(http, BinanceAccountType.CoinMFutures, null, null);
        InstrumentId asked = InstrumentId.Parse("BTCUSD_PERP.BINANCE");

        await provider.LoadAsync(asked, CancellationToken.None);

        Instrument only = Assert.Single(provider.GetAll());
        Assert.Equal(asked, only.Id);

        // And the venue really was asked about the one symbol - the filtering is the adapter making up for the
        // venue ignoring it, not the adapter forgetting to send it.
        Assert.Equal("BTCUSD_PERP", Assert.Single(server.RequestsTo("/dapi/v1/exchangeInfo")).Query("symbol"));
    }

    // ----- the money -----

    [Fact]
    public async Task A_contract_carries_the_venues_own_contract_size_as_its_multiplier()
    {
        await using LoopbackServer server = new(Catalog().Handle);

        BinanceInstrumentProvider provider = await LoadedAsync(server);

        // 100 USD on the BTCUSD contracts and 10 on ETHUSD, as the venue publishes them. Two different sizes in one
        // market is why this cannot be a constant.
        Assert.Equal(100m, provider.Find(InstrumentId.Parse("BTCUSD_PERP.BINANCE"))!.Multiplier.Value);
        Assert.Equal(10m, provider.Find(InstrumentId.Parse("ETHUSD_PERP.BINANCE"))!.Multiplier.Value);
        Assert.Equal(100m, provider.Find(InstrumentId.Parse("BTCUSD_261225.BINANCE"))!.Multiplier.Value);

        // Sized in whole contracts, which is what the venue's own quantity fields, candle volumes and order sizes
        // carry on this family.
        Instrument perp = provider.Find(InstrumentId.Parse("BTCUSD_PERP.BINANCE"))!;
        Assert.Equal(1m, perp.SizeIncrement.Value);
        Assert.Equal(0, perp.SizePrecision);
        Assert.Equal(1m, perp.MinQuantity!.Value.Value);
    }

    [Fact]
    public async Task Every_money_figure_on_a_coin_margined_contract_comes_out_in_the_coin_through_one_over_the_price()
    {
        await using LoopbackServer server = new(Catalog().Handle);

        BinanceInstrumentProvider provider = await LoadedAsync(server);
        Instrument perp = provider.Find(InstrumentId.Parse("BTCUSD_PERP.BINANCE"))!;

        Assert.True(perp.IsInverse);
        Assert.Equal(Currencies.USD, perp.QuoteCurrency);
        Assert.Equal(Currencies.BTC, perp.BaseCurrency);
        Assert.Equal(Currencies.BTC, perp.SettlementCurrency);

        // The engine's own formula for an inverse contract, written out: notional = contracts x multiplier / price.
        // 200 contracts of 100 USD at 50,000 is 20,000 USD, which is 0.4 BTC. A multiplier left at one would have
        // made it 0.004 - a hundredth - and margin, commission and liquidation all follow the notional.
        Money notional = perp.NotionalValue(perp.MakeQuantity(200m), perp.MakePrice(50_000m));

        Assert.Equal(Currencies.BTC, notional.Currency);
        Assert.Equal(0.4m, notional.Amount);

        // And the quote-denominated view of the same position, which is the contracts' own face value.
        Assert.Equal(20_000m, perp.NotionalValue(perp.MakeQuantity(200m), perp.MakePrice(50_000m), useQuoteForInverse: true).Amount);

        // Margin and commission are that notional times a rate, in the same currency. Both derived from the
        // instrument's own figures so the arithmetic cannot drift from what the venue published.
        Assert.Equal(0.4m * perp.MarginInit, perp.CalculateInitialMargin(perp.MakeQuantity(200m), perp.MakePrice(50_000m)).Amount);
        Assert.Equal(Currencies.BTC, perp.CalculateInitialMargin(perp.MakeQuantity(200m), perp.MakePrice(50_000m)).Currency);
        Assert.Equal(0.4m * perp.TakerFee, perp.CalculateCommission(perp.MakeQuantity(200m), perp.MakePrice(50_000m), LiquiditySide.Taker).Amount);
        Assert.Equal(Currencies.BTC, perp.CostCurrency);
    }

    [Fact]
    public async Task A_ten_usd_contract_and_a_hundred_usd_contract_of_the_same_market_value_differently()
    {
        // The point of reading the size per symbol. Same number of contracts, same price, ten times the notional -
        // so one figure for the family would be right on one contract and wrong on the other 27, or the reverse.
        await using LoopbackServer server = new(Catalog().Handle);

        BinanceInstrumentProvider provider = await LoadedAsync(server);
        Instrument btc = provider.Find(InstrumentId.Parse("BTCUSD_PERP.BINANCE"))!;
        Instrument eth = provider.Find(InstrumentId.Parse("ETHUSD_PERP.BINANCE"))!;

        Assert.Equal(1m, btc.NotionalValue(btc.MakeQuantity(100m), btc.MakePrice(10_000m)).Amount);
        Assert.Equal(0.1m, eth.NotionalValue(eth.MakeQuantity(100m), eth.MakePrice(10_000m)).Amount);
    }

    [Fact]
    public async Task What_the_contract_is_survives_being_written_to_the_catalog_and_read_back()
    {
        // Instruments are persisted, so a fact that does not survive the round trip is a fact the engine holds only
        // while it is online - and the two that matter most here are the two that are new on this family.
        await using LoopbackServer server = new(Catalog().Handle);

        BinanceInstrumentProvider provider = await LoadedAsync(server);
        Instrument perp = provider.Find(InstrumentId.Parse("BTCUSD_PERP.BINANCE"))!;
        Instrument read = Bytex.Data.InstrumentJson.Deserialize(Bytex.Data.InstrumentJson.Serialize(perp));

        Assert.True(read.IsInverse);
        Assert.Equal(100m, read.Multiplier.Value);
        Assert.Equal(perp.Id, read.Id);
        Assert.Equal(0.4m, read.NotionalValue(read.MakeQuantity(200m), read.MakePrice(50_000m)).Amount);
    }

    [Fact]
    public async Task The_coin_margined_family_charges_its_own_published_rates()
    {
        // Cheaper than the USD-margined market on this venue's published schedule, so one futures figure for both
        // would overstate what trading here costs on every fill.
        await using LoopbackServer server = new(Catalog().Handle);

        BinanceInstrumentProvider provider = await LoadedAsync(server);
        Instrument perp = provider.Find(InstrumentId.Parse("BTCUSD_PERP.BINANCE"))!;

        Assert.Equal(BinanceVenue.CoinMFuturesMakerFee, perp.MakerFee);
        Assert.Equal(BinanceVenue.CoinMFuturesTakerFee, perp.TakerFee);
        Assert.True(perp.TakerFee < BinanceVenue.UsdMFuturesTakerFee, "this venue's coin-margined schedule is the cheaper of the two");
    }

    // ----- what it publishes about margin, and what it does not -----

    [Fact]
    public async Task Without_a_key_the_venue_wide_default_is_kept_and_the_ceiling_is_unknown()
    {
        // The same split as the USD-margined family, and measurably a default rather than a per-contract figure:
        // every one of this family's 30 contracts answers 5.0000 and 2.5000, from the 100-USD BTCUSD_PERP to the
        // 10-USD altcoin quarterlies. Its brackets are behind the signed endpoint - an unauthenticated call is
        // refused with -2014 - and null is the honest answer for a ceiling nothing read.
        await using LoopbackServer server = new(Catalog().Handle);

        BinanceInstrumentProvider provider = await LoadedAsync(server);
        Instrument perp = provider.Find(InstrumentId.Parse("BTCUSD_PERP.BINANCE"))!;

        Assert.Equal(BinanceVenue.DefaultMarginInit, perp.MarginInit);
        Assert.Equal(BinanceVenue.DefaultMarginMaint, perp.MarginMaint);
        Assert.Null(perp.MaxLeverage);

        // And nothing was spent asking, because no key was held.
        Assert.Empty(server.RequestsTo(BinanceVenue.LeverageBracketPath(BinanceAccountType.CoinMFutures)));

        // The clamp that default leaves behind, stated rather than implied: asked for 50x, the engine charges the
        // margin of 20x, because an instrument's margin is a floor under one over the leverage.
        Assert.Equal(BinanceVenue.DefaultMarginInit, perp.InitialMarginRate(50m));
        Assert.Equal(perp.InitialMarginRate(20m), perp.InitialMarginRate(50m));
    }

    [Fact]
    public async Task With_a_key_the_brackets_replace_the_default_and_give_a_ceiling()
    {
        await using LoopbackServer server = new(Catalog()
            .On("GET", BinanceVenue.LeverageBracketPath(BinanceAccountType.CoinMFutures), CoinMBrackets)
            .Handle);

        BinanceInstrumentProvider provider = await LoadedAsync(server, withKey: true);
        Instrument perp = provider.Find(InstrumentId.Parse("BTCUSD_PERP.BINANCE"))!;

        Assert.Equal(1m / 125m, perp.MarginInit);
        Assert.Equal(0.004m, perp.MarginMaint);
        Assert.Equal(125m, perp.MaxLeverage);

        // The point of all of it: with the real floor, a leverage the venue grants is no longer clamped.
        Assert.Equal(1m / 50m, perp.InitialMarginRate(50m));
    }

    [Fact]
    public async Task A_bracket_row_that_names_a_pair_rather_than_a_symbol_is_still_matched()
    {
        // Which of the two a row carries could not be established on this family without a key, so both are
        // accepted and a contract is matched on both. Guessing one and being wrong would not have cost one figure -
        // reading a property that is not there would have thrown and lost the whole catalog.
        await using LoopbackServer server = new(Catalog()
            .On("GET", BinanceVenue.LeverageBracketPath(BinanceAccountType.CoinMFutures), PairKeyedBrackets)
            .Handle);

        BinanceInstrumentProvider provider = await LoadedAsync(server, withKey: true);

        // BTCUSD is the pair of the perpetual AND of the quarterly, so both take the figure.
        Assert.Equal(125m, provider.Find(InstrumentId.Parse("BTCUSD_PERP.BINANCE"))!.MaxLeverage);
        Assert.Equal(125m, provider.Find(InstrumentId.Parse("BTCUSD_261225.BINANCE"))!.MaxLeverage);

        // And a contract of another pair is untouched rather than given somebody else's ceiling.
        Assert.Null(provider.Find(InstrumentId.Parse("ETHUSD_PERP.BINANCE"))!.MaxLeverage);
    }

    [Fact]
    public async Task A_bracket_answer_in_a_shape_nothing_can_be_read_from_keeps_the_catalog()
    {
        // The rest of the same argument. An answer that is neither an array of rows nor a row - whatever this venue
        // turns out to send - must leave the instruments as they were rather than throw away a catalog over one
        // figure that was already unknown without a key.
        await using LoopbackServer server = new(Catalog()
            .On("GET", BinanceVenue.LeverageBracketPath(BinanceAccountType.CoinMFutures), """{"unexpected":true}""")
            .Handle);

        BinanceInstrumentProvider provider = await LoadedAsync(server, withKey: true);

        Assert.Equal(3, provider.GetAll().Count);
        Assert.Null(provider.Find(InstrumentId.Parse("BTCUSD_PERP.BINANCE"))!.MaxLeverage);
        Assert.Equal(BinanceVenue.DefaultMarginInit, provider.Find(InstrumentId.Parse("BTCUSD_PERP.BINANCE"))!.MarginInit);
    }

    [Fact]
    public async Task A_key_that_cannot_read_the_brackets_keeps_the_default_rather_than_failing_the_catalog()
    {
        await using LoopbackServer server = new(Catalog()
            .On("GET", BinanceVenue.LeverageBracketPath(BinanceAccountType.CoinMFutures), _ => new StubResponse(401, """{"code":-2015,"msg":"Invalid API-key, IP, or permissions for action."}"""))
            .Handle);

        BinanceInstrumentProvider provider = await LoadedAsync(server, withKey: true);
        Instrument perp = provider.Find(InstrumentId.Parse("BTCUSD_PERP.BINANCE"))!;

        Assert.Equal(BinanceVenue.DefaultMarginInit, perp.MarginInit);
        Assert.Null(perp.MaxLeverage);
        Assert.Equal(3, provider.GetAll().Count);
    }

    // ----- candles and funding, without a node -----

    [Fact]
    public async Task Candles_come_back_close_stamped_from_the_page_size_this_family_really_serves()
    {
        await using LoopbackServer server = new(Catalog()
            .On("GET", "/dapi/v1/klines", BinancePayloads.CoinMKlines)
            .Handle);

        BinanceHttp http = new(Config(server.HttpBase), null);
        BinanceInstrumentProvider provider = new(http, BinanceAccountType.CoinMFutures, null, null);
        await provider.LoadAllAsync(CancellationToken.None);
        Instrument perp = provider.Find(InstrumentId.Parse("BTCUSD_PERP.BINANCE"))!;
        BarType barType = BarType.Parse("BTCUSD_PERP.BINANCE-1-MINUTE-LAST-EXTERNAL");

        IReadOnlyList<Bar> bars = await BinanceHistory.FetchBarsAsync(
            http,
            perp,
            barType,
            UnixNanos.FromMilliseconds(1790373900000),
            UnixNanos.FromMilliseconds(1790374020000),
            null,
            UnixNanos.FromMilliseconds(1790374080000),
            CancellationToken.None);

        // Element 6 plus one millisecond: the end of the minute that opened at element 0, which is what a bar is
        // stamped with everywhere in this engine.
        Assert.Equal(2, bars.Count);
        Assert.Equal(UnixNanos.FromMilliseconds(1790373960000), bars[0].TsEvent);
        Assert.Equal(UnixNanos.FromMilliseconds(1790374020000), bars[1].TsEvent);

        // And the volume is a number of contracts, which is the unit this family's instruments are sized in - so it
        // needs no conversion and 2,804 stays 2,804.
        Assert.Equal(2804m, bars[0].Volume.Value);

        // The venue is never asked for more than it serves. 1,500 was measured as the most it answers and 1,501 as
        // refused outright, which is the useful kind of cap.
        Assert.Equal(1500, BinanceVenue.KlinePage(BinanceAccountType.CoinMFutures));
        Assert.Equal(BinanceVenue.FuturesKlinePage, BinanceVenue.KlinePage(BinanceAccountType.UsdMFutures));
        Assert.True(
            int.Parse(server.RequestsTo("/dapi/v1/klines")[0].Query("limit")!, CultureInfo.InvariantCulture) <= BinanceVenue.FuturesKlinePage,
            "a request must never ask for more rows than the venue will answer with");
    }

    [Fact]
    public async Task Funding_history_is_fetched_without_a_node_and_always_names_its_page_size()
    {
        // E7: a host that stores history has no node, so the helper takes an http client and an instrument id and
        // nothing else. The limit is always sent because this family's default page is 500 where its sibling's is
        // 100 - a loop leaving it out and treating a short page as the end would stop after 500 settlements.
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/dapi/v1/fundingRate", BinancePayloads.CoinMFundingRates)
            .Handle);

        BinanceHttp http = new(Config(server.HttpBase), null);
        InstrumentId id = InstrumentId.Parse("BTCUSD_PERP.BINANCE");

        IReadOnlyList<FundingRateUpdate> rates = await BinanceHistory.FetchFundingRatesAsync(
            http,
            id,
            DateTimeOffset.FromUnixTimeMilliseconds(1790323200000),
            DateTimeOffset.FromUnixTimeMilliseconds(1790380800000),
            CancellationToken.None);

        Assert.Equal(3, rates.Count);
        Assert.All(rates, r => Assert.Equal(id, r.InstrumentId));
        Assert.Equal(-0.00001838m, rates[0].Rate);
        Assert.Equal(-0.00000573m, rates[^1].Rate);
        Assert.Equal(UnixNanos.FromMilliseconds(1790323200000), rates[0].TsEvent);

        RecordedRequest asked = server.RequestsTo("/dapi/v1/fundingRate")[0];
        Assert.Equal("BTCUSD_PERP", asked.Query("symbol"));
        Assert.Equal(BinanceVenue.FundingPage.ToString(CultureInfo.InvariantCulture), asked.Query("limit"));
    }

    [Fact]
    public async Task A_dated_contract_of_this_family_is_charged_no_funding_and_answers_an_empty_history()
    {
        // The venue's own answer for BTCUSD_261225, measured: an empty array rather than a refusal. That is correct
        // and not a gap - a contract that delivers converges by delivering - so it has to read as "nothing was
        // charged" rather than as a failure a caller has to interpret.
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/dapi/v1/fundingRate", "[]")
            .Handle);

        BinanceHttp http = new(Config(server.HttpBase), null);

        IReadOnlyList<FundingRateUpdate> rates = await BinanceHistory.FetchFundingRatesAsync(
            http,
            InstrumentId.Parse("BTCUSD_261225.BINANCE"),
            DateTimeOffset.FromUnixTimeMilliseconds(1790323200000),
            null,
            CancellationToken.None);

        Assert.Empty(rates);
    }

    // ----- what the venue is told about itself -----

    [Fact]
    public void The_declaration_states_this_family_rather_than_leaving_a_host_to_find_it()
    {
        VenueFamily family = new BinancePlugin().Describe().Families.Single(f => f.Name == "coinm-futures");

        Assert.Equal(BinanceVenue.CoinMFuturesHttpBase, family.HttpBase);
        Assert.Equal(BinanceVenue.CoinMFuturesWsBase, family.WsBase);
        Assert.Equal("CoinMFutures", family.Config["accountType"]);
        Assert.True(family.PaysFunding);
        Assert.Equal([InstrumentClass.Swap, InstrumentClass.Future], family.InstrumentClasses);

        // Its own schedule, and not the figure its sibling declares.
        Assert.Equal(BinanceVenue.CoinMFuturesMakerFee, family.DefaultFees.Maker);
        Assert.Equal(BinanceVenue.CoinMFuturesTakerFee, family.DefaultFees.Taker);
        Assert.NotEqual(
            new BinancePlugin().Describe().Families.Single(f => f.Name == "usdm-futures").DefaultFees,
            family.DefaultFees);

        // Its own archive, under cm rather than um.
        Assert.All(family.FreeDatasets, d => Assert.Contains("/futures/cm/", d.Address, StringComparison.Ordinal));

        // The same two variables as the venue's other families, which is a statement rather than a copy: the
        // coin-margined host accepts the same key header and the same query signature and refuses a malformed key
        // with the same code, so there is no third part to declare.
        Assert.Equal(
            new BinancePlugin().Describe().Families.Single(f => f.Name == "spot").Key.Parts,
            family.Key.Parts);
    }

    [Fact]
    public void The_declared_settings_really_select_this_family_and_not_one_of_the_others()
    {
        // A host applies the declared settings by name and value, knowing nothing of what they mean. If the value's
        // spelling were wrong, the client would be built for whatever the default family is - which is a node that
        // starts, connects and receives the wrong market's data.
        VenueDescriptor venue = new BinancePlugin().Describe();
        VenueFamily selected = CapabilityGuard.FamilyOf(
            venue,
            new BinanceDataClientConfig { AccountType = BinanceAccountType.CoinMFutures })!;

        Assert.Equal("coinm-futures", selected.Name);
        Assert.Equal("usdm-futures", CapabilityGuard.FamilyOf(venue, new BinanceDataClientConfig { AccountType = BinanceAccountType.UsdMFutures })!.Name);
        Assert.Equal("spot", CapabilityGuard.FamilyOf(venue, new BinanceDataClientConfig { AccountType = BinanceAccountType.Spot })!.Name);
    }

    /// <summary>
    /// What a bracket answer keyed by SYMBOL looks like, in the shape this venue's USD-margined family really sends
    /// and its coin-margined family documents: the widest tier first, the highest leverage in it.
    /// </summary>
    private const string CoinMBrackets = """
        [
          {
            "symbol": "BTCUSD_PERP",
            "brackets": [
              { "bracket": 1, "initialLeverage": 125, "qtyCap": 50,    "qtyFloor": 0,   "maintMarginRatio": 0.004, "cum": 0.0 },
              { "bracket": 2, "initialLeverage": 100, "qtyCap": 1000,  "qtyFloor": 50,  "maintMarginRatio": 0.005, "cum": 50.0 },
              { "bracket": 3, "initialLeverage": 50,  "qtyCap": 20000, "qtyFloor": 1000,"maintMarginRatio": 0.01,  "cum": 3050.0 }
            ]
          }
        ]
        """;

    /// <summary>And the same answer keyed by PAIR, which is the other shape this family's read may take.</summary>
    private const string PairKeyedBrackets = """
        [
          {
            "pair": "BTCUSD",
            "brackets": [
              { "bracket": 1, "initialLeverage": 125, "qtyCap": 50,   "qtyFloor": 0,  "maintMarginRatio": 0.004, "cum": 0.0 },
              { "bracket": 2, "initialLeverage": 100, "qtyCap": 1000, "qtyFloor": 50, "maintMarginRatio": 0.005, "cum": 50.0 }
            ]
          }
        ]
        """;
}
