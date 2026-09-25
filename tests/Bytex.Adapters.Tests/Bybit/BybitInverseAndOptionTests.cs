using System.Text.Json;
using Bytex.Adapters.Bybit;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;

namespace Bytex.Adapters.Tests.Bybit;

// Why: this venue's two new families are the two nothing in this repository had covered, and each is unlike the
// four families that shipped before it in a different way.
//
// INVERSE contracts were excluded by every adapter here, and the stated reason was that a USD-quoted, base-settled
// contract cannot be sized in base units without a price. That reason never applied: the venue sizes them in its own
// USD contracts, and the engine's money arithmetic inverts from one flag on the instrument - NotionalValue divides by
// the price and answers in the base currency, and margin, commission and funding all follow it. What the family really
// costs is two things a spelling rule cannot survive. Its dated contracts are named BTCUSDZ26, with no dash anywhere,
// so the linear family's "no dash means perpetual" rule would have called all four of them perpetuals. And spot lists
// BTCUSD and ETHUSD, both trading, so an inverse perpetual named for its own symbol would share an id with a spot pair
// and the family resolver would answer every one of them with the pair.
//
// OPTIONS are unlike everything else here, and that is the interesting part rather than an inconvenience. Measured
// against the live venue on 2026-09-26:
//
//   funding        /v5/market/funding/history refuses category=option: "Illegal category"
//   candles        /v5/market/kline refuses it too: "params error: Category is invalid" - and the kline SOCKET topic
//                  is accepted, reported under successTopics and then silent, so there is no bar history by any route
//   margin         /v5/market/risk-limit refuses the category, and the contract data carries no leverageFilter, so
//                  the venue publishes neither a margin nor a ceiling for an option anywhere public
//   strike         the contract data has thirteen fields and none of them is the strike; it exists only in the symbol
//   listing        instruments-info with no baseCoin answers 730 BTC contracts and an empty cursor - the whole of ONE
//                  underlying, looking exactly like the whole of the category
//   book           the option socket publishes depths 25 and 100; the 1 and 50 the other families use are accepted,
//                  reported as successes and never delivered
//   trades         publicTrade is named for the UNDERLYING, not the contract; publicTrade.<contract> is silent
//   quotes         so is orderbook.1 - an option's top of book arrives on the tickers topic, under its own field names
//
// Every one of those is declared for what it is rather than copied from the family beside it, and every one is
// checked below against a payload recorded from the venue that produced the measurement.
public sealed class BybitInverseAndOptionTests
{
    private static readonly InstrumentId _inversePerp = InstrumentId.Parse("BTCUSD-PERP.BYBIT");
    private static readonly InstrumentId _inverseFuture = InstrumentId.Parse("BTCUSDZ26.BYBIT");
    private static readonly InstrumentId _put = InstrumentId.Parse("BTC-25JUN27-106000-P-USDT.BYBIT");
    private static readonly InstrumentId _call = InstrumentId.Parse("BTC-25JUN27-106000-C-USDT.BYBIT");

    private static BybitHttp Http(LoopbackServer server, BybitProductType type) =>
        new(new BybitDataClientConfig { ProductType = type, BaseUrlHttp = server.HttpBase });

    private static Routes InverseVenue() => new Routes()
        .On("GET", "/v5/market/instruments-info", BybitPayloads.InverseInstruments)
        .On("GET", BybitVenue.RiskLimitPath, BybitPayloads.InverseRiskLimits);

    private static async Task<BybitInstrumentProvider> LoadedAsync(LoopbackServer server, BybitProductType type, Microsoft.Extensions.Logging.ILoggerFactory? logs = null)
    {
        BybitInstrumentProvider provider = new(Http(server, type), type, null, logs?.CreateLogger("bybit"));
        await provider.LoadAllAsync(CancellationToken.None);
        return provider;
    }

    // ----- the routes and the names -----

    [Theory]
    [InlineData(BybitProductType.Inverse, "inverse", "wss://stream.bybit.com/v5/public/inverse")]
    [InlineData(BybitProductType.Option, "option", "wss://stream.bybit.com/v5/public/option")]
    public void Each_new_family_asks_for_its_own_category_on_the_one_host(BybitProductType type, string category, string socket)
    {
        // One host and one socket root for all four families: what changes is the category in the request and the
        // last segment of the socket path, which is what makes the declaration's two addresses the same for each.
        Assert.Equal(category, BybitVenue.Category(type));
        Assert.Equal(BybitVenue.DefaultHttpBase, BybitVenue.HttpBase(new BybitDataClientConfig { ProductType = type }));
        Assert.Equal(socket, BybitVenue.WsPublic(new BybitDataClientConfig { ProductType = type }));
    }

    [Theory]
    [InlineData("BTCUSD", "BTCUSD-PERP.BYBIT")]
    [InlineData("ETHUSD", "ETHUSD-PERP.BYBIT")]
    [InlineData("XRPUSD", "XRPUSD-PERP.BYBIT")]
    [InlineData("BTCUSDZ26", "BTCUSDZ26.BYBIT")]
    [InlineData("BTCUSDH27", "BTCUSDH27.BYBIT")]
    [InlineData("ETHUSDZ26", "ETHUSDZ26.BYBIT")]
    public void An_inverse_id_carries_the_suffix_only_where_the_symbol_ends_at_its_quote_coin(string raw, string expected)
    {
        // The linear family tells a perpetual from a dated contract by a dash, and this family has no dashes at all:
        // BTCUSDZ26 would have been read as a perpetual by that rule. What decides here is where the symbol ends -
        // a perpetual's ends at USD and a dated one carries a delivery code past it - and it decides the NAME only.
        // Both directions, because an id that does not strip back to the venue's own symbol is an id that cannot be
        // traded.
        InstrumentId id = BybitVenue.ToInstrumentId(raw, BybitProductType.Inverse);

        Assert.Equal(expected, id.ToString());
        Assert.Equal(raw, BybitVenue.ToRawSymbol(id));
    }

    [Theory]
    [InlineData("BTC-25JUN27-106000-P-USDT")]
    [InlineData("XRP-30OCT26-0.4-P-USDT")]
    public void An_option_id_is_the_venues_own_symbol_untouched(string raw)
    {
        InstrumentId id = BybitVenue.ToInstrumentId(raw, BybitProductType.Option);

        Assert.Equal(raw + ".BYBIT", id.ToString());
        Assert.Equal(raw, BybitVenue.ToRawSymbol(id));
    }

    [Fact]
    public void The_spot_pair_and_the_inverse_perpetual_of_one_name_are_not_one_instrument()
    {
        // Measured on 2026-09-26: spot lists BTCUSD and ETHUSD, both status Trading, and the inverse family lists
        // perpetuals of exactly those names. Without the suffix one id would mean two different instruments on one
        // venue - and because the declaration lists spot first, the family resolver would answer both with the pair
        // and the inverse contract would be unreachable by id.
        Assert.NotEqual(
            BybitVenue.ToInstrumentId("BTCUSD", BybitProductType.Spot),
            BybitVenue.ToInstrumentId("BTCUSD", BybitProductType.Inverse));

        Assert.Equal("BTCUSD.BYBIT", BybitVenue.ToInstrumentId("BTCUSD", BybitProductType.Spot).ToString());
    }

    // ----- inverse instruments -----

    [Fact]
    public async Task An_inverse_perpetual_is_inverse_quoted_in_USD_and_settled_in_the_base_coin()
    {
        await using LoopbackServer server = new(InverseVenue().Handle);
        BybitInstrumentProvider provider = await LoadedAsync(server, BybitProductType.Inverse);

        CryptoPerpetual perp = Assert.IsType<CryptoPerpetual>(provider.Find(_inversePerp));

        Assert.True(perp.IsInverse, "an inverse contract that does not say so is priced as a linear one");
        Assert.Equal("BTCUSD", perp.RawSymbol.Value);
        Assert.Equal(InstrumentClass.Swap, perp.InstrumentClass);
        Assert.Equal("USD", perp.QuoteCurrency.Code);
        Assert.Equal("BTC", perp.BaseCurrency.Code);

        // Settlement in the base coin is what makes the family what it is, and CostCurrency follows it: profit,
        // margin and fees all come out in BTC.
        Assert.Equal("BTC", perp.SettlementCurrency.Code);
        Assert.Equal("BTC", perp.CostCurrency.Code);

        // Sized in the venue's own USD contracts, whole ones, which is how the venue sizes them - and the reason
        // the family could be published at all without a price to convert with.
        Assert.Equal(new Quantity(1m, 0), perp.SizeIncrement);
        Assert.Equal(new Quantity(1m, 0), perp.MinQuantity);
        Assert.Equal(new Price(0.1m, 1), perp.PriceIncrement);

        // The minimum order value is in the quote coin, which for this family is USD rather than a stablecoin.
        Assert.Equal(new Money(5m, Currency.FromCode("USD")), perp.MinNotional);
        Assert.Equal(BybitVenue.InverseMakerFee, perp.MakerFee);
        Assert.Equal(BybitVenue.InverseTakerFee, perp.TakerFee);
    }

    [Fact]
    public async Task The_inverse_family_takes_its_margin_and_its_ceiling_from_the_venue()
    {
        await using LoopbackServer server = new(InverseVenue().Handle);
        BybitInstrumentProvider provider = await LoadedAsync(server, BybitProductType.Inverse);

        Instrument perp = provider.Find(_inversePerp)!;

        // R4.12: the tier a position starts in, from the venue's own risk limits. The fixture carries a second
        // tier so this proves the lowest is chosen rather than the first row taken.
        Assert.Equal(0.01m, perp.MarginInit);
        Assert.Equal(0.005m, perp.MarginMaint);

        // And the ceiling, from the leverageFilter in the same response the instrument came from - so it costs no
        // extra request, and the leverage guard has a figure to refuse against.
        Assert.Equal(100m, perp.MaxLeverage);
        Assert.Equal("inverse", Assert.Single(server.RequestsTo(BybitVenue.RiskLimitPath)).Query("category"));
    }

    [Fact]
    public async Task A_dated_inverse_contract_is_a_future_because_the_venue_says_so_and_its_name_says_nothing()
    {
        await using LoopbackServer server = new(InverseVenue().Handle);
        BybitInstrumentProvider provider = await LoadedAsync(server, BybitProductType.Inverse);

        CryptoFuture future = Assert.IsType<CryptoFuture>(provider.Find(_inverseFuture));

        // BTCUSDZ26 carries no dash, no PERP and no date a reader could parse. contractType says InverseFutures and
        // deliveryTime is a real timestamp where the perpetual's is zero; that is the whole of the evidence, and it
        // is the venue's.
        Assert.Equal(InstrumentClass.Future, future.InstrumentClass);
        Assert.Equal("BTCUSDZ26", future.RawSymbol.Value);
        Assert.True(future.IsInverse);
        Assert.Equal("BTC", future.Underlying.Code);
        Assert.Equal(new DateTimeOffset(2026, 6, 12, 8, 0, 0, TimeSpan.Zero), future.Activation.ToDateTimeOffset());
        Assert.Equal(new DateTimeOffset(2026, 12, 25, 8, 0, 0, TimeSpan.Zero), future.Expiration.ToDateTimeOffset());

        // And the perpetual beside it is still a perpetual: one page, two contracts, two classes, neither read off
        // a name.
        Assert.Equal(2, provider.Count);
        Assert.IsType<CryptoPerpetual>(provider.Find(_inversePerp));
    }

    [Fact]
    public async Task The_money_on_a_published_inverse_contract_is_the_arithmetic_the_backtest_suite_expects()
    {
        // The claim that makes publishing this family worthwhile, checked against the venue's real contract rather
        // than a hand-built one. The formulas are InverseContractMoneyTests's, in that file's own words:
        //   notional   = contracts / price            (base currency)
        //   initial    = notional * max(1/leverage, marginInit)
        //   maintenance= notional * marginMaint
        await using LoopbackServer server = new(InverseVenue().Handle);
        BybitInstrumentProvider provider = await LoadedAsync(server, BybitProductType.Inverse);

        Instrument perp = provider.Find(_inversePerp)!;
        Quantity contracts = perp.MakeQuantity(10_000m);
        Price price = perp.MakePrice(50_000m);

        Money notional = perp.NotionalValue(contracts, price);
        Assert.Equal("BTC", notional.Currency.Code);
        Assert.Equal(0.2m, notional.Amount);

        // 1 percent is what this venue requires, so leverage of 100 is the point past which nothing further is
        // bought and leverage of 20 costs the twentieth that it should.
        Assert.Equal(0.01m, perp.InitialMarginRate(100m));
        Assert.Equal(0.05m, perp.InitialMarginRate(20m));
        Assert.Equal(0.2m * 0.01m, perp.CalculateInitialMargin(contracts, price, useQuoteForInverse: false).Amount);
        Assert.Equal(0.2m * 0.005m, perp.CalculateMaintenanceMargin(contracts, price).Amount);

        // A fee on an inverse contract is charged in the base coin because that is what its notional is in, and
        // nothing in the adapter arranges that.
        Money commission = perp.CalculateCommission(contracts, price, LiquiditySide.Taker);
        Assert.Equal("BTC", commission.Currency.Code);
        Assert.Equal(0.2m * BybitVenue.InverseTakerFee, commission.Amount);

        // And the quote-denominated view is still the contracts themselves, which is what makes a USD risk limit
        // comparable with a position size.
        Assert.Equal(10_000m, perp.NotionalValue(contracts, price, useQuoteForInverse: true).Amount);
        Assert.Equal("USD", perp.NotionalValue(contracts, price, useQuoteForInverse: true).Currency.Code);
    }

    // ----- inverse history -----

    [Fact]
    public async Task Inverse_candles_are_fetched_from_the_inverse_category()
    {
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/v5/market/instruments-info", BybitPayloads.InverseInstruments)
            .On("GET", BybitVenue.RiskLimitPath, BybitPayloads.InverseRiskLimits)
            .On("GET", "/v5/market/kline", BybitPayloads.Envelope("""
                {"symbol":"BTCUSD","category":"inverse","list":[
                  ["1790370000000","83826.5","83866.8","83561.9","83776.4","2808928","33.56368586"],
                  ["1790366400000","83967.5","84133.5","83773.6","83826.5","4534302","54.0302595"]]}
                """))
            .Handle);

        using BybitHttp http = Http(server, BybitProductType.Inverse);
        BybitInstrumentProvider provider = new(http, BybitProductType.Inverse);
        await provider.LoadAllAsync(CancellationToken.None);

        BarType hourly = BarType.Parse("BTCUSD-PERP.BYBIT-1-HOUR-LAST-EXTERNAL");
        IReadOnlyList<Bar> bars = await BybitHistory.FetchBarsAsync(
            http,
            provider.Find(_inversePerp)!,
            hourly,
            UnixNanos.FromMilliseconds(1_790_366_400_000L),
            UnixNanos.FromMilliseconds(1_790_373_600_000L),
            null,
            UnixNanos.FromMilliseconds(1_790_373_600_000L),
            CancellationToken.None);

        RecordedRequest asked = Assert.Single(server.RequestsTo("/v5/market/kline"));
        Assert.Equal("inverse", asked.Query("category"));
        Assert.Equal("BTCUSD", asked.Query("symbol"));

        // Oldest first and stamped at the close, as every family of this venue is: the venue reports a candle by
        // the time it opened.
        Assert.Equal(2, bars.Count);
        Assert.Equal(1_790_370_000_000_000_000L, bars[0].TsEvent.Value);
        Assert.Equal(1_790_373_600_000_000_000L, bars[1].TsEvent.Value);
    }

    [Fact]
    public async Task Inverse_funding_is_fetched_from_the_inverse_category()
    {
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/v5/market/funding/history", BybitPayloads.Envelope("""
                {"category":"inverse","list":[
                  {"symbol":"BTCUSD","fundingRate":"-0.00001471","fundingRateTimestamp":"1790352000000"},
                  {"symbol":"BTCUSD","fundingRate":"-0.00001751","fundingRateTimestamp":"1790323200000"}]}
                """))
            .Handle);

        using BybitHttp http = Http(server, BybitProductType.Inverse);
        IReadOnlyList<FundingRateUpdate> rates = await BybitHistory.FetchFundingRatesAsync(
            http,
            _inversePerp,
            DateTimeOffset.FromUnixTimeMilliseconds(1_790_300_000_000L),
            DateTimeOffset.FromUnixTimeMilliseconds(1_790_400_000_000L),
            CancellationToken.None);

        Assert.Equal("inverse", Assert.Single(server.RequestsTo("/v5/market/funding/history")).Query("category"));

        // Oldest first, turned round from the venue's newest-first answer.
        Assert.Equal(2, rates.Count);
        Assert.Equal(-0.00001751m, rates[0].Rate);
        Assert.Equal(-0.00001471m, rates[1].Rate);
    }

    // ----- options: what the venue publishes, and what it does not -----

    [Fact]
    public async Task An_option_is_built_from_the_venues_fields_with_the_strike_read_where_it_is_the_only_copy()
    {
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/v5/market/instruments-info", BybitPayloads.OptionInstruments)
            .Handle);
        BybitInstrumentProvider provider = await LoadedAsync(server, BybitProductType.Option);

        OptionContract put = Assert.IsType<OptionContract>(provider.Find(_put));
        OptionContract call = Assert.IsType<OptionContract>(provider.Find(_call));

        Assert.Equal(InstrumentClass.Option, put.InstrumentClass);

        // Call or put from optionsType, never from the letter in the name - the letter is only checked against it.
        Assert.Equal(OptionKind.Put, put.Kind);
        Assert.Equal(OptionKind.Call, call.Kind);

        // The underlying from baseCoin, and the two dates from launchTime and deliveryTime.
        Assert.Equal("BTC", put.Underlying);
        Assert.Equal(new DateTimeOffset(2026, 9, 21, 0, 20, 0, TimeSpan.Zero), put.Activation.ToDateTimeOffset());
        Assert.Equal(new DateTimeOffset(2027, 6, 25, 8, 0, 0, TimeSpan.Zero), put.Expiration.ToDateTimeOffset());

        // And the strike from the symbol, because the venue publishes it nowhere else at all.
        Assert.Equal(new Price(106_000m, 0), put.StrikePrice);
        Assert.Equal(new Price(106_000m, 0), call.StrikePrice);

        // The premium is in USDT and the size in the underlying: a contract is one BTC of exposure here, stepped in
        // hundredths, which is nothing like the USD contracts of the family beside it.
        Assert.Equal("USDT", put.QuoteCurrency.Code);
        Assert.Equal("USDT", put.SettlementCurrency.Code);
        Assert.Equal(new Price(5m, 0), put.PriceIncrement);
        Assert.Equal(new Quantity(0.01m, 2), put.SizeIncrement);
        Assert.False(put.IsInverse);
    }

    [Fact]
    public async Task An_option_strike_keeps_its_own_digits_rather_than_the_premiums_tick()
    {
        // XRP options are struck at 0.4 and quoted in ten-thousandths, so the tick has more digits than the strike
        // here - but the pair can fall the other way, and rounding a strike to a premium tick would move the
        // contract rather than a price on it.
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/v5/market/instruments-info", BybitPayloads.OptionInstruments)
            .Handle);
        BybitInstrumentProvider provider = await LoadedAsync(server, BybitProductType.Option);

        OptionContract xrp = Assert.IsType<OptionContract>(provider.Find(InstrumentId.Parse("XRP-30OCT26-0.4-P-USDT.BYBIT")));

        Assert.Equal(0.4m, xrp.StrikePrice.Value);
        Assert.Equal("XRP", xrp.Underlying);
        Assert.Equal(new Quantity(10m, 0), xrp.SizeIncrement);
    }

    [Fact]
    public async Task An_option_whose_name_disagrees_with_the_venues_own_fields_is_skipped_rather_than_guessed_at()
    {
        // A strike is the contract, so a name that does not agree with the fields beside it is not a rounding
        // problem to be papered over: the letter says put and the venue says Call, and the only safe answer is to
        // publish nothing for it. The valid contract in the same page still loads, so one bad row is not an outage.
        string mixed = BybitPayloads.OptionInstruments
            .Replace("\"symbol\": \"BTC-25JUN27-106000-C-USDT\", \"status\": \"Trading\", \"baseCoin\": \"BTC\"", "\"symbol\": \"BTC-25JUN27-106000-P-USDT\", \"status\": \"Trading\", \"baseCoin\": \"BTC\"", StringComparison.Ordinal)
            .Replace("\"symbol\": \"BTC-25JUN27-106000-P-USDT\", \"status\": \"Trading\", \"baseCoin\": \"BTC\"", "\"symbol\": \"BTC-25JUN27-106000-X-USDT\", \"status\": \"Trading\", \"baseCoin\": \"BTC\"", StringComparison.Ordinal);

        await using LoopbackServer server = new(new Routes().On("GET", "/v5/market/instruments-info", mixed).Handle);
        BybitInstrumentProvider provider = await LoadedAsync(server, BybitProductType.Option);

        Assert.DoesNotContain(provider.GetAll(), i => i.RawSymbol.Value.Contains("-X-", StringComparison.Ordinal));
        Assert.NotNull(provider.Find(InstrumentId.Parse("XRP-30OCT26-0.4-P-USDT.BYBIT")));
    }

    [Fact]
    public async Task An_option_publishes_no_margin_and_no_ceiling_so_neither_is_invented()
    {
        // The venue's answer rather than a gap here: risk-limit refuses category=option with "Illegal category",
        // and the contract data carries no leverageFilter. Null on the ceiling means "the venue did not say", which
        // is the opposite of unlimited to anything deciding whether a configured leverage is reachable - and the
        // risk-limit endpoint is not asked at all, so no request is spent learning what the declaration knows.
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/v5/market/instruments-info", BybitPayloads.OptionInstruments)
            .Handle);
        BybitInstrumentProvider provider = await LoadedAsync(server, BybitProductType.Option);

        Instrument put = provider.Find(_put)!;

        Assert.Equal(0m, put.MarginInit);
        Assert.Equal(0m, put.MarginMaint);
        Assert.Null(put.MaxLeverage);
        Assert.Empty(server.RequestsTo(BybitVenue.RiskLimitPath));
    }

    [Fact]
    public async Task Listing_options_without_an_underlying_gets_the_venues_default_and_says_so()
    {
        // Measured: instruments-info with no baseCoin answers 730 BTC contracts and an EMPTY cursor. That is a
        // complete answer about one underlying that looks exactly like a complete answer about the category, so a
        // host is told rather than left to infer it from the count.
        RecordingLogs logs = new();
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/v5/market/instruments-info", BybitPayloads.OptionInstruments)
            .Handle);

        await LoadedAsync(server, BybitProductType.Option, logs);

        Assert.Null(Assert.Single(server.RequestsTo("/v5/market/instruments-info")).Query(BybitVenue.OptionBaseCoinFilter));
        Assert.Contains(logs.Warnings, w => w.Contains(BybitVenue.OptionBaseCoinFilter, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Named_option_underlyings_are_asked_for_one_at_a_time()
    {
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/v5/market/instruments-info", BybitPayloads.OptionInstruments)
            .Handle);

        BybitInstrumentProvider provider = new(Http(server, BybitProductType.Option), BybitProductType.Option);
        await provider.LoadAllAsync(
            CancellationToken.None,
            new Dictionary<string, string> { [BybitVenue.OptionBaseCoinFilter] = "BTC, XRP" });

        IReadOnlyList<RecordedRequest> asked = server.RequestsTo("/v5/market/instruments-info");
        Assert.Equal(2, asked.Count);
        Assert.Equal("BTC", asked[0].Query(BybitVenue.OptionBaseCoinFilter));
        Assert.Equal("XRP", asked[1].Query(BybitVenue.OptionBaseCoinFilter));
    }

    [Fact]
    public async Task There_is_no_option_bar_history_and_the_adapter_says_so_rather_than_the_venue()
    {
        // /v5/market/kline refuses category=option outright, so a caller storing history would otherwise read a
        // category error it cannot interpret. Nothing is sent: the refusal is this adapter's own sentence.
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/v5/market/instruments-info", BybitPayloads.OptionInstruments)
            .Handle);

        using BybitHttp http = Http(server, BybitProductType.Option);
        BybitInstrumentProvider provider = new(http, BybitProductType.Option);
        await provider.LoadAllAsync(CancellationToken.None);

        NotSupportedException refused = await Assert.ThrowsAsync<NotSupportedException>(() => BybitHistory.FetchBarsAsync(
            http,
            provider.Find(_put)!,
            BarType.Parse("BTC-25JUN27-106000-P-USDT.BYBIT-1-HOUR-LAST-EXTERNAL"),
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddHours(1),
            CancellationToken.None));

        Assert.Contains("option", refused.Message, StringComparison.Ordinal);
        Assert.Empty(server.RequestsTo("/v5/market/kline"));
    }

    [Theory]
    [InlineData(BybitProductType.Option)]
    [InlineData(BybitProductType.Spot)]
    public async Task A_family_that_is_charged_no_funding_has_none_to_fetch_and_refuses_the_question(BybitProductType type)
    {
        // An empty list would read as "this instrument was never charged anything", which is a different statement
        // from "this market is never charged". The venue agrees about the option market: the funding endpoint
        // refuses the category.
        await using LoopbackServer server = new(new Routes().Handle);
        using BybitHttp http = Http(server, type);

        await Assert.ThrowsAsync<NotSupportedException>(() => BybitHistory.FetchFundingRatesAsync(
            http,
            _put,
            DateTimeOffset.UnixEpoch,
            null,
            CancellationToken.None));

        Assert.Empty(server.RequestsTo("/v5/market/funding/history"));
    }

    // ----- options on the stream -----

    private sealed class DataRig : IAsyncDisposable
    {
        private WsSession? _session;

        public DataRig(BybitProductType type)
        {
            Routes = new Routes()
                .On("GET", "/v5/market/instruments-info", type == BybitProductType.Option ? BybitPayloads.OptionInstruments : BybitPayloads.InverseInstruments)
                .On("GET", BybitVenue.RiskLimitPath, BybitPayloads.InverseRiskLimits);
            Server = new LoopbackServer(Routes.Handle);
            Kernel = new TestKernel();
            Client = new BybitDataClient(new ClientId("BYBIT"), new BybitDataClientConfig
            {
                ProductType = type,
                BaseUrlHttp = Server.HttpBase,
                BaseUrlWs = Server.WsBase,
                InstrumentProvider = new InstrumentProviderConfig { LoadAll = true },
            }, Kernel.Services);
            Client.AttachSink(Sink);
        }

        public Routes Routes { get; }

        public LoopbackServer Server { get; }

        public TestKernel Kernel { get; }

        public BybitDataClient Client { get; }

        public RecordingDataSink Sink { get; } = new();

        public WsSession Session => _session ?? throw new InvalidOperationException("not connected");

        public async Task<DataRig> ConnectAsync()
        {
            await Client.ConnectAsync(CancellationToken.None).WaitAsync(Wait.Timeout);
            _session = await Server.NextSessionAsync();
            return this;
        }

        /// <summary>Next control message from the client as "op topic".</summary>
        public async Task<string> NextControlMessageAsync()
        {
            using JsonDocument doc = JsonDocument.Parse(await Session.ReceiveTextAsync());
            return doc.RootElement.GetProperty("op").GetString()
                + " "
                + string.Join(",", doc.RootElement.GetProperty("args").EnumerateArray().Select(a => a.GetString()!).Order(StringComparer.Ordinal));
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            Kernel.Dispose();
            await Server.DisposeAsync();
        }
    }

    [Fact]
    public async Task The_inverse_stream_uses_the_same_topics_as_the_linear_one()
    {
        // Measured by subscribing: this family's socket serves orderbook.1, orderbook.50, orderbook.200,
        // publicTrade by contract, kline and tickers exactly as the linear one does. Pinned so that the option
        // family's differences below read as the option family's and not as "derivatives are different".
        await using DataRig rig = await new DataRig(BybitProductType.Inverse).ConnectAsync();

        await rig.Client.SubscribeAsync(Commands.Quotes(_inversePerp), CancellationToken.None);
        await rig.Client.SubscribeAsync(Commands.Trades(_inversePerp), CancellationToken.None);
        await rig.Client.SubscribeAsync(Commands.Book(_inversePerp, depth: 100), CancellationToken.None);
        await rig.Client.SubscribeAsync(Commands.Bars(BarType.Parse("BTCUSD-PERP.BYBIT-5-MINUTE-LAST-EXTERNAL")), CancellationToken.None);
        await rig.Client.SubscribeAsync(Commands.Mark(_inversePerp), CancellationToken.None);

        Assert.Equal("subscribe orderbook.1.BTCUSD", await rig.NextControlMessageAsync());
        Assert.Equal("subscribe publicTrade.BTCUSD", await rig.NextControlMessageAsync());
        Assert.Equal("subscribe orderbook.200.BTCUSD", await rig.NextControlMessageAsync());
        Assert.Equal("subscribe kline.5.BTCUSD", await rig.NextControlMessageAsync());
        Assert.Equal("subscribe tickers.BTCUSD", await rig.NextControlMessageAsync());
        Assert.Empty(rig.Sink.SubscriptionFailures);
    }

    [Fact]
    public async Task The_inverse_ticker_carries_the_mark_the_index_and_the_funding_this_family_is_charged()
    {
        await using DataRig rig = await new DataRig(BybitProductType.Inverse).ConnectAsync();

        await rig.Session.SendTextAsync(BybitPayloads.InverseTickerSnapshot);

        Assert.Equal(_inversePerp, Assert.IsType<MarkPriceUpdate>(await rig.Sink.NextDataAsync()).InstrumentId);
        Assert.IsType<IndexPriceUpdate>(await rig.Sink.NextDataAsync());
        FundingRateUpdate funding = Assert.IsType<FundingRateUpdate>(await rig.Sink.NextDataAsync());
        Assert.Equal(-0.0000392m, funding.Rate);
        Assert.Equal(1_790_380_800_000_000_000L, funding.NextFundingTime!.Value.Value);
    }

    [Fact]
    public async Task An_options_quotes_are_subscribed_on_the_tickers_topic_because_orderbook_1_delivers_nothing()
    {
        await using DataRig rig = await new DataRig(BybitProductType.Option).ConnectAsync();

        await rig.Client.SubscribeAsync(Commands.Quotes(_put), CancellationToken.None);

        Assert.Equal("subscribe tickers.BTC-25JUN27-106000-P-USDT", await rig.NextControlMessageAsync());
    }

    [Fact]
    public async Task An_option_ticker_becomes_a_quote_and_the_side_that_did_not_change_is_carried_forward()
    {
        await using DataRig rig = await new DataRig(BybitProductType.Option).ConnectAsync();

        await rig.Session.SendTextAsync(BybitPayloads.OptionSubscribeAck);
        await rig.Session.SendTextAsync(BybitPayloads.OptionTickerSnapshot);

        QuoteTick first = Assert.IsType<QuoteTick>(await rig.Sink.NextDataAsync());
        Assert.Equal(_put, first.InstrumentId);

        // The option market's own field names: bidPrice and bidSize, where the contract markets write bid1Price.
        Assert.Equal(new Price(22_915m, 0), first.Bid);
        Assert.Equal(new Price(26_205m, 0), first.Ask);
        Assert.Equal(new Quantity(18.55m, 2), first.BidSize);

        // The mark and the index follow on the same message, which is why an option's quotes share this topic.
        Assert.IsType<MarkPriceUpdate>(await rig.Sink.NextDataAsync());
        Assert.IsType<IndexPriceUpdate>(await rig.Sink.NextDataAsync());

        await rig.Session.SendTextAsync(BybitPayloads.OptionTickerDelta);

        QuoteTick second = Assert.IsType<QuoteTick>(await rig.Sink.NextDataAsync());
        Assert.Equal(new Price(26_200m, 0), second.Ask);
        Assert.Equal(new Quantity(20m, 2), second.AskSize);

        // A delta carries only what changed, so a bid dropped here would publish a one-sided quote.
        Assert.Equal(new Price(22_915m, 0), second.Bid);
        Assert.Equal(new Quantity(18.55m, 2), second.BidSize);
    }

    [Fact]
    public async Task An_options_trades_are_subscribed_by_underlying_and_arrive_named_by_contract()
    {
        await using DataRig rig = await new DataRig(BybitProductType.Option).ConnectAsync();

        await rig.Client.SubscribeAsync(Commands.Trades(_call), CancellationToken.None);

        // The underlying, taken from the instrument rather than from the symbol's first three letters.
        Assert.Equal("subscribe publicTrade.BTC", await rig.NextControlMessageAsync());

        await rig.Session.SendTextAsync(BybitPayloads.OptionPublicTrades);

        TradeTick trade = Assert.IsType<TradeTick>(await rig.Sink.NextDataAsync());
        Assert.Equal(_call, trade.InstrumentId);
        Assert.Equal(new Price(240m, 0), trade.Price);
        Assert.Equal(AggressorSide.Buyer, trade.Aggressor);

        // One subscription carries every BTC option's trades, and the second row of that payload is a contract this
        // client never loaded - so it produces nothing rather than a tick for an instrument nobody knows.
        Assert.Empty(rig.Sink.SubscriptionFailures);
    }

    [Fact]
    public async Task An_option_trade_subscription_needs_the_instrument_and_refuses_rather_than_guessing_the_underlying()
    {
        await using DataRig rig = await new DataRig(BybitProductType.Option).ConnectAsync();

        await rig.Client.SubscribeAsync(Commands.Trades(InstrumentId.Parse("DOGE-30OCT26-1-C-USDT.BYBIT")), CancellationToken.None);

        (SubscribeCommand _, string reason) = Assert.Single(rig.Sink.SubscriptionFailures);
        Assert.Contains("underlying", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unsubscribing_one_option_leaves_the_trades_of_the_others_on_the_same_underlying()
    {
        // The topic is shared by every contract on a coin, so the naive unsubscribe would have taken the trades of
        // all of them away - silently, while each of those subscriptions still looked alive.
        await using DataRig rig = await new DataRig(BybitProductType.Option).ConnectAsync();

        await rig.Client.SubscribeAsync(Commands.Trades(_call), CancellationToken.None);
        await rig.Client.SubscribeAsync(Commands.Trades(_put), CancellationToken.None);
        Assert.Equal("subscribe publicTrade.BTC", await rig.NextControlMessageAsync());

        await rig.Client.UnsubscribeAsync(new UnsubscribeTradeTicks(_put, null, Guid.NewGuid(), TestKernel.Now), CancellationToken.None);
        await rig.Session.SendTextAsync(BybitPayloads.OptionPublicTrades);

        // Still delivering, because the call is still subscribed.
        Assert.Equal(_call, Assert.IsType<TradeTick>(await rig.Sink.NextDataAsync()).InstrumentId);

        await rig.Client.UnsubscribeAsync(new UnsubscribeTradeTicks(_call, null, Guid.NewGuid(), TestKernel.Now), CancellationToken.None);

        Assert.Equal("unsubscribe publicTrade.BTC", await rig.NextControlMessageAsync());
    }

    [Fact]
    public async Task An_option_book_subscription_uses_the_depths_this_market_really_publishes()
    {
        await using DataRig rig = await new DataRig(BybitProductType.Option).ConnectAsync();

        await rig.Client.SubscribeAsync(Commands.Book(_put, depth: 10), CancellationToken.None);
        await rig.Client.SubscribeAsync(Commands.Book(_call, depth: 200), CancellationToken.None);

        // 25 and 100, not the 50 and 200 of the other three families - which the option socket accepts, reports as
        // successes and never delivers.
        Assert.Equal("subscribe orderbook.25.BTC-25JUN27-106000-P-USDT", await rig.NextControlMessageAsync());
        Assert.Equal("subscribe orderbook.100.BTC-25JUN27-106000-C-USDT", await rig.NextControlMessageAsync());

        await rig.Session.SendTextAsync(BybitPayloads.OptionBookSnapshot);

        OrderBookDeltas deltas = Assert.IsType<OrderBookDeltas>(await rig.Sink.NextDataAsync());
        Assert.Equal(_put, deltas.InstrumentId);
        Assert.Equal(4, deltas.Deltas.Count); // the clear, two bids and one ask
    }

    [Fact]
    public async Task An_option_bar_subscription_is_refused_rather_than_accepted_and_silent()
    {
        // The venue would accept it, report it under successTopics and send nothing, which is exactly the failure
        // the capability declaration exists to prevent. So the client refuses instead and the caller is told.
        await using DataRig rig = await new DataRig(BybitProductType.Option).ConnectAsync();

        await rig.Client.SubscribeAsync(
            Commands.Bars(BarType.Parse("BTC-25JUN27-106000-P-USDT.BYBIT-5-MINUTE-LAST-EXTERNAL")),
            CancellationToken.None);

        Assert.Single(rig.Sink.SubscriptionFailures);
    }

    [Fact]
    public async Task An_option_funding_subscription_is_refused_because_an_option_is_never_charged_any()
    {
        await using DataRig rig = await new DataRig(BybitProductType.Option).ConnectAsync();

        await rig.Client.SubscribeAsync(new SubscribeFundingRates(_put, null, Guid.NewGuid(), TestKernel.Now), CancellationToken.None);

        Assert.Single(rig.Sink.SubscriptionFailures);
    }

    // ----- orders -----

    [Fact]
    public async Task An_inverse_order_carries_the_inverse_category_and_may_reduce_a_position()
    {
        await using BybitExecRig rig = new(BybitProductType.Inverse);
        rig.Routes.On("POST", "/v5/order/create", BybitPayloads.Envelope("""{"orderId":"1","orderLinkId":"x"}"""));

        MarketOrder order = rig.Orders.Market(rig.Instrument.Id, OrderSide.Sell, rig.Qty(1_000m), reduceOnly: true);
        await rig.SubmitAsync(order);

        RecordedRequest sent = Assert.Single(rig.Server.RequestsTo("/v5/order/create"));
        BybitExecRig.AssertBody(sent, "category=inverse;symbol=BTCUSD;side=Sell;qty=1000;reduceOnly=true", string.Empty);
        BybitExecRig.AssertSigned(sent);
        Assert.Equal(AccountType.Margin, rig.Client.AccountType);
    }

    [Fact]
    public async Task An_option_order_carries_the_option_category_and_no_reduce_only_flag()
    {
        // reduceOnly is left off deliberately: this venue documents the field for its option market and that could
        // not be confirmed without a key, and a flag the venue might reject would fail the whole order.
        await using BybitExecRig rig = new(BybitProductType.Option);
        rig.Routes.On("POST", "/v5/order/create", BybitPayloads.Envelope("""{"orderId":"1","orderLinkId":"x"}"""));

        MarketOrder order = rig.Orders.Market(rig.Instrument.Id, OrderSide.Buy, rig.Qty(0.1m), reduceOnly: true);
        await rig.SubmitAsync(order);

        RecordedRequest sent = Assert.Single(rig.Server.RequestsTo("/v5/order/create"));
        BybitExecRig.AssertBody(sent, "category=option;symbol=BTC-25JUN27-106000-P-USDT;side=Buy;qty=0.1", "reduceOnly");
    }

    [Fact]
    public async Task An_inverse_fill_with_no_fee_currency_is_charged_in_the_coin_the_contract_settles_in()
    {
        // The venue leaves feeCurrency empty on this topic, and on an inverse contract the fee really is charged in
        // the base coin - which is what the instrument settles in, so nothing has to special-case it.
        await using BybitExecRig rig = new(BybitProductType.Inverse);
        (WsSession session, _, _) = await rig.ConnectAsync("""{"list":[]}""");

        await session.SendTextAsync("""
            {"topic":"execution","id":"1","creationTime":1672364174455,"data":[{"category":"inverse","symbol":"BTCUSD","orderId":"1","orderLinkId":"O-1","side":"Buy","execId":"x-1","execPrice":"50000.0","execQty":"1000","execFee":"0.0000012","feeCurrency":"","isMaker":false,"execType":"Trade","execTime":"1672364174443"}]}
            """);

        OrderFilled filled = Assert.IsType<OrderFilled>(await rig.Sink.NextOrderEventAsync());
        Assert.Equal("BTC", filled.Commission.Currency.Code);
        Assert.Equal(0.0000012m, filled.Commission.Amount);
        Assert.Equal(_inversePerp, filled.InstrumentId);
    }

    [Fact]
    public async Task A_whole_market_read_on_the_inverse_family_asks_per_coin_it_settles_in()
    {
        // This family settles each contract in its OWN base coin, so there is no single settle coin for the market
        // the way the linear family has one. The coins come from the contracts the client holds.
        await using BybitExecRig rig = new(BybitProductType.Inverse);
        rig.Routes.On("GET", "/v5/position/list", BybitPayloads.Envelope("""{"list":[],"nextPageCursor":""}"""));

        await rig.Client.GeneratePositionStatusReportsAsync(null, null, null, CancellationToken.None).WaitAsync(Wait.Timeout);

        Assert.Equal("BTC", Assert.Single(rig.Server.RequestsTo("/v5/position/list")).Query("settleCoin"));
    }

    // ----- leverage -----

    [Fact]
    public async Task The_inverse_family_refuses_to_connect_at_a_leverage_the_venue_does_not_grant()
    {
        // The fixture's own leverageFilter grants 100x. Asking for 150x would be accepted by the venue, granted at
        // 100x and traded - so the run refuses before the socket opens, which is the guarantee the leverage guard
        // exists for and it has to hold on a family as much as on a venue.
        await using BybitExecRig rig = new(BybitProductType.Inverse, leverage: 150m);
        rig.Routes
            .On("POST", BybitVenue.LeveragePath, BybitPayloads.Envelope("{}"))
            .On("GET", BybitVenue.RiskLimitPath, BybitPayloads.InverseRiskLimits);

        ArgumentOutOfRangeException refused = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => rig.ConnectAsync("""{"list":[]}"""));

        Assert.Contains("100", refused.Message, StringComparison.Ordinal);
        Assert.Empty(rig.Server.RequestsTo(BybitVenue.LeveragePath));
    }

    [Fact]
    public async Task The_inverse_family_sets_both_sides_of_a_leverage_the_venue_does_grant()
    {
        await using BybitExecRig rig = new(BybitProductType.Inverse, leverage: 25m);
        rig.Routes
            .On("POST", BybitVenue.LeveragePath, BybitPayloads.Envelope("{}"))
            .On("GET", BybitVenue.RiskLimitPath, BybitPayloads.InverseRiskLimits);

        await rig.ConnectAsync("""{"list":[]}""");

        using JsonDocument body = JsonDocument.Parse(rig.Server.RequestsTo(BybitVenue.LeveragePath)[0].Body);
        Assert.Equal("inverse", body.RootElement.GetProperty("category").GetString());
        Assert.Equal("25", body.RootElement.GetProperty("buyLeverage").GetString());
        Assert.Equal("25", body.RootElement.GetProperty("sellLeverage").GetString());
    }

    [Fact]
    public async Task A_leverage_configured_on_an_option_client_is_not_sent_and_is_not_silent_either()
    {
        // This venue has no per-symbol leverage for options and publishes no ceiling to check one against, so the
        // guard has nothing to refuse and nothing is sent. A configured figure that changes nothing must still be
        // said out loud - a configuration field nothing reads is the failure the guard beside it exists for.
        RecordingLogs logs = new();
        await using BybitExecRig rig = new(BybitProductType.Option, leverage: 5m, logs: logs);

        await rig.ConnectAsync("""{"list":[]}""");

        Assert.Empty(rig.Server.RequestsTo(BybitVenue.LeveragePath));
        Assert.Contains(logs.Warnings, w => w.Contains("option", StringComparison.Ordinal));
    }

    // ----- the declaration -----

    [Fact]
    public void Bybit_declares_four_families_and_the_option_one_denies_what_this_venue_cannot_do()
    {
        VenueDescriptor bybit = new BybitPlugin().Describe();

        Assert.Equal(["spot", "linear", "inverse", "option"], bybit.Families.Select(f => f.Name));

        VenueFamily inverse = bybit.Families.Single(f => f.Name == "inverse");
        Assert.Equal([InstrumentClass.Swap, InstrumentClass.Future], inverse.InstrumentClasses);
        Assert.True(inverse.PaysFunding);
        Assert.True(inverse.Capabilities.BarHistory);
        Assert.True(inverse.Capabilities.FundingHistory);

        VenueFamily option = bybit.Families.Single(f => f.Name == "option");
        Assert.Equal([InstrumentClass.Option], option.InstrumentClasses);

        // Each of these is the venue's answer and not a copy of the family beside it: no funding endpoint for the
        // category, no kline endpoint and no kline topic that delivers.
        Assert.False(option.PaysFunding);
        Assert.False(option.Capabilities.FundingHistory);
        Assert.False(option.Capabilities.BarHistory);

        // And it can still be listed, subscribed and traded, which is what makes the one false capability worth
        // declaring rather than dropping the family.
        Assert.True(option.Capabilities.ListInstruments);
        Assert.True(option.Capabilities.MarketData);
        Assert.True(option.Capabilities.Execution);

        // The two families a class is claimed by twice are told apart by the configuration each declares, which is
        // what lets a host select between them at all.
        Assert.NotEqual(inverse.Config, bybit.Families.Single(f => f.Name == "linear").Config);
    }
}
