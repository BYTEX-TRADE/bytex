using System.Text.Json;
using Bytex.Adapters.Kraken;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Adapters.Tests.Kraken;

// Why: Kraken's futures platform is a different venue behind the same name, and three of its facts produce plausible
// wrong numbers rather than errors. All three were measured against the live catalog on 2026-09-25, across all 300
// contracts it listed.
//
// 1. THE TYPE FIELD DOES NOT SAY WHETHER A CONTRACT EXPIRES. `flexible_futures` covers PF_XBTUSD, which never
//    expires, and FF_XBTUSD_261225, which expires in December - 278 perpetuals and 10 dated contracts under one
//    value. Only `lastTradingTime` distinguishes them. A reader trusting the type publishes a dated contract as a
//    perpetual: the wrong class, no expiry recorded, and funding assumed on something that pays none.
//
// 2. MARGIN IS A SCHEDULE, AND ITS TIER THRESHOLD HAS TWO DIFFERENT FIELD NAMES. A flexible futures contract keys
//    its tiers by `numNonContractUnits` and a coin-margined one by `contracts`. A reader that knows one name finds
//    no tiers at all on the other kind, and no tiers reads as a contract that needs no margin.
//
// 3. FEES ARE PERCENTAGES. The schedule PF_XBTUSD points at carries makerFee 0.02 for two basis points. Taken as
//    written, every commission on this platform is a hundred times what the venue charges.
//
// And one more, on the catalog itself: ?symbol= is IGNORED. Asked for one contract it answered with all 300, and
// there is no per-symbol endpoint to ask instead - /instruments/PF_XBTUSD is a 404. That is Binance's USD-margined
// defect on a second venue, and it turns "load one instrument" into "load the venue" unless the adapter filters.
public sealed class KrakenFuturesTests
{
    private static readonly InstrumentId _perp = InstrumentId.Parse("PF_XBTUSD.KRAKEN");
    private static readonly InstrumentId _dated = InstrumentId.Parse("FF_XBTUSD_261225.KRAKEN");

    private static KrakenHttp Http(LoopbackServer server) =>
        new(new KrakenDataClientConfig { ProductType = KrakenProductType.Futures, BaseUrlHttp = server.HttpBase });

    private static Routes Catalog() => new Routes()
        .On("GET", "/derivatives/api/v3/instruments", KrakenPayloads.Instruments)
        .On("GET", "/derivatives/api/v3/feeschedules", KrakenPayloads.FeeSchedules);

    private static async Task<KrakenFuturesInstrumentProvider> LoadedAsync(LoopbackServer server)
    {
        KrakenFuturesInstrumentProvider provider = new(Http(server));
        await provider.LoadAllAsync(CancellationToken.None);
        return provider;
    }

    // ----- what the venue calls a contract -----

    [Theory]
    [InlineData("PF_XBTUSD")]
    [InlineData("PF_DOGEUSD")]
    [InlineData("FF_XBTUSD_261225")]
    public void A_contract_and_its_instrument_id_are_the_same_string(string raw)
    {
        // The venue's own symbol, untouched - including the XBT it writes bitcoin as HERE, where its spot platform
        // writes BTC and refuses XBT. Two spellings on one venue, each taken from the platform that uses it, and
        // neither renamed by this engine.
        Assert.Equal(raw + ".KRAKEN", KrakenFuturesVenue.ToInstrumentId(raw).ToString());
        Assert.Equal(raw, KrakenFuturesVenue.ToRawSymbol(InstrumentId.Parse(raw + ".KRAKEN")));
    }

    // ----- the class, from the venue's own expiry field -----

    [Fact]
    public async Task A_perpetual_and_a_dated_contract_of_the_same_type_are_published_as_different_classes()
    {
        // The fact this whole file exists for. Both of these are `flexible_futures`; only one of them expires.
        await using LoopbackServer server = new(Catalog().Handle);
        KrakenFuturesInstrumentProvider provider = await LoadedAsync(server);

        CryptoPerpetual perp = Assert.IsType<CryptoPerpetual>(provider.Find(_perp));
        Assert.Equal(InstrumentClass.Swap, perp.InstrumentClass);

        CryptoFuture dated = Assert.IsType<CryptoFuture>(provider.Find(_dated));
        Assert.Equal(InstrumentClass.Future, dated.InstrumentClass);

        // And the expiry is recorded rather than lost, which is the part that makes the class useful: a contract
        // that expires and says when can be rolled, and one that expires silently cannot.
        Assert.Equal(
            DateTimeOffset.Parse("2026-12-25T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            dated.Expiration.ToDateTimeOffset());
        Assert.Equal(
            DateTimeOffset.Parse("2026-05-22T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            dated.Activation.ToDateTimeOffset());
    }

    [Fact]
    public async Task The_class_is_not_read_off_the_symbol_prefix()
    {
        // PF_ and FF_ are a convention of the contracts that exist today. The test that matters is that the
        // decision is made on the expiry FIELD: both contracts here share a type, and the one without the field is
        // the perpetual whatever its prefix says.
        await using LoopbackServer server = new(Catalog().Handle);
        KrakenFuturesInstrumentProvider provider = await LoadedAsync(server);

        using JsonDocument catalog = JsonDocument.Parse(KrakenPayloads.Instruments);
        foreach (JsonElement contract in catalog.RootElement.GetProperty("instruments").EnumerateArray())
        {
            string symbol = contract.GetProperty("symbol").GetString()!;
            if (provider.Find(KrakenFuturesVenue.ToInstrumentId(symbol)) is not { } instrument)
            {
                continue;
            }

            bool hasExpiryField = contract.TryGetProperty(KrakenFuturesVenue.ExpiryField, out _);
            Assert.Equal(hasExpiryField ? InstrumentClass.Future : InstrumentClass.Swap, instrument.InstrumentClass);

            // Both classes come out of one type, which is the whole point.
            Assert.Equal(KrakenFuturesVenue.FlexibleFutures, contract.GetProperty("type").GetString());
        }
    }

    [Fact]
    public async Task Coin_margined_contracts_are_not_offered_at_all()
    {
        // They are quoted in USD and settled in the base currency, so a quantity of one cannot be expressed in base
        // units without a price - which is the one thing this adapter promises everything above it. Twelve of the
        // venue's 300 are of this type. Leaving them out is the statement; offering them with a guessed size would
        // not be.
        await using LoopbackServer server = new(Catalog().Handle);
        KrakenFuturesInstrumentProvider provider = await LoadedAsync(server);

        Assert.Equal(
            ["FF_XBTUSD_261225.KRAKEN", "PF_DOGEUSD.KRAKEN", "PF_XBTUSD.KRAKEN"],
            provider.GetAll().Select(i => i.Id.ToString()).Order(StringComparer.Ordinal).ToArray());

        // Including the DATED coin-margined one, which the first rule catches on its own - so being dated is not
        // what excludes it and being inverse is not what makes it dated.
        Assert.Null(provider.Find(InstrumentId.Parse("PI_XBTUSD.KRAKEN")));
        Assert.Null(provider.Find(InstrumentId.Parse("FI_XBTUSD_261225.KRAKEN")));
    }

    // ----- margin from the schedule -----

    [Fact]
    public async Task A_contracts_margin_comes_from_its_own_schedule_and_not_from_a_number_in_this_source()
    {
        // PF_XBTUSD starts at one percent initial and half a percent maintenance and rises through eight tiers to
        // fifty percent; PF_DOGEUSD starts at two and one. A fixed figure would be wrong on almost every contract
        // and wrong in both directions, which is why two contracts with different first tiers are checked here - a
        // test on bitcoin alone would pass with the number hard-coded.
        await using LoopbackServer server = new(Catalog().Handle);
        KrakenFuturesInstrumentProvider provider = await LoadedAsync(server);

        Instrument btc = provider.Find(_perp)!;
        Assert.Equal(0.01m, btc.MarginInit);
        Assert.Equal(0.005m, btc.MarginMaint);

        Instrument doge = provider.Find(InstrumentId.Parse("PF_DOGEUSD.KRAKEN"))!;
        Assert.Equal(0.02m, doge.MarginInit);
        Assert.Equal(0.01m, doge.MarginMaint);
    }

    [Fact]
    public void The_first_tier_is_the_lowest_threshold_and_not_the_first_row()
    {
        // Read as "the row at index zero" this would be right by accident on every contract the venue lists today,
        // because it happens to write its schedules in order. The rule is the lowest threshold.
        using JsonDocument reordered = JsonDocument.Parse("""
            {"marginLevels":[
              {"numNonContractUnits":3000000.0,"initialMargin":0.04,"maintenanceMargin":0.02},
              {"numNonContractUnits":0.0,"initialMargin":0.01,"maintenanceMargin":0.005}
            ]}
            """);

        (decimal initial, decimal maintenance, decimal maxLeverage) = KrakenFuturesVenue.FirstMarginTier(reordered.RootElement);

        Assert.Equal(0.01m, initial);
        Assert.Equal(0.005m, maintenance);
        Assert.Equal(100m, maxLeverage);
    }

    [Fact]
    public void A_schedule_keyed_by_contracts_is_read_as_well_as_one_keyed_by_units()
    {
        // The second measured trap. A coin-margined contract keys its tiers by `contracts` and a flexible futures
        // one by `numNonContractUnits`; a reader that knows one name finds no tiers on the other kind, and no tiers
        // reads as a contract that needs no margin at all.
        using JsonDocument byContracts = JsonDocument.Parse("""
            {"marginLevels":[
              {"contracts":0,"initialMargin":0.02,"maintenanceMargin":0.01},
              {"contracts":500000,"initialMargin":0.04,"maintenanceMargin":0.02}
            ]}
            """);

        (decimal initial, decimal maintenance, decimal maxLeverage) = KrakenFuturesVenue.FirstMarginTier(byContracts.RootElement);

        Assert.Equal(0.02m, initial);
        Assert.Equal(0.01m, maintenance);
        Assert.Equal(50m, maxLeverage);
    }

    [Fact]
    public void A_contract_with_no_schedule_is_not_read_as_one_that_needs_no_margin()
    {
        // Zero here means "nothing said", which the caller has to be able to tell from "no margin required". It is
        // never turned into a leverage: dividing by zero initial margin is how an unlimited leverage appears.
        using JsonDocument none = JsonDocument.Parse("""{"symbol":"PF_XBTUSD"}""");

        (decimal initial, decimal maintenance, decimal maxLeverage) = KrakenFuturesVenue.FirstMarginTier(none.RootElement);

        Assert.Equal(0m, initial);
        Assert.Equal(0m, maintenance);
        Assert.Equal(0m, maxLeverage);
    }

    [Fact]
    public async Task The_maximum_leverage_a_contract_allows_is_the_inverse_of_its_first_initial_margin()
    {
        // One percent initial margin is 100x, two percent is 50x. Carried on the instrument so that a client asked
        // for more can say what the contract permits instead of letting the venue refuse the preference for a
        // reason nobody can read. Cross-checked against the venue's own socket, whose ticker publishes
        // "leverage":"100x" for PF_XBTUSD.
        await using LoopbackServer server = new(Catalog().Handle);
        KrakenFuturesInstrumentProvider provider = await LoadedAsync(server);

        Assert.Equal("100", provider.Find(_perp)!.Info[KrakenFuturesInstrumentProvider.MaxLeverageInfo]);
        Assert.Equal("50", provider.Find(InstrumentId.Parse("PF_DOGEUSD.KRAKEN"))!.Info[KrakenFuturesInstrumentProvider.MaxLeverageInfo]);

        // And how many tiers there are, so a reader of the published initial margin knows it is the first of
        // several rather than the whole of the contract's requirement.
        Assert.Equal("3", provider.Find(_perp)!.Info[KrakenFuturesInstrumentProvider.MarginTiersInfo]);
    }

    // ----- fees from the schedule the contract points at -----

    [Fact]
    public async Task A_contracts_fees_come_from_its_schedule_and_are_divided_by_a_hundred()
    {
        // The schedule PF_XBTUSD points at carries makerFee 0.02 and takerFee 0.05, as PERCENTAGES. Two and five
        // basis points. Taken as written every commission on this platform would be a hundred times the truth,
        // which is the kind of wrongness a backtest reports as an unprofitable strategy.
        await using LoopbackServer server = new(Catalog().Handle);
        KrakenFuturesInstrumentProvider provider = await LoadedAsync(server);

        Instrument btc = provider.Find(_perp)!;
        Assert.Equal(0.0002m, btc.MakerFee);
        Assert.Equal(0.0005m, btc.TakerFee);

        // And that is one hundredth of what the endpoint published, not a number written down here.
        Assert.Equal(0.02m / KrakenFuturesVenue.FeePercent, btc.MakerFee);
    }

    [Fact]
    public async Task A_contract_whose_schedule_is_missing_keeps_the_family_default_rather_than_a_zero_fee()
    {
        // The schedules are a second request and can fail on their own. A zero fee would make every backtest over
        // this family look better than it is, which is the one direction a wrong fee must never go.
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/derivatives/api/v3/instruments", KrakenPayloads.Instruments)
            .Handle);

        KrakenFuturesInstrumentProvider provider = await LoadedAsync(server);
        Instrument btc = provider.Find(_perp)!;

        Assert.Equal(KrakenFuturesVenue.DefaultMakerFee, btc.MakerFee);
        Assert.Equal(KrakenFuturesVenue.DefaultTakerFee, btc.TakerFee);
        Assert.NotEqual(0m, btc.TakerFee);

        // And the catalog still loaded: losing the rates is a warning and not a reason to have no instruments.
        Assert.Equal(3, provider.Count);
    }

    [Fact]
    public void A_fee_schedule_this_adapter_cannot_find_is_answered_with_nothing_rather_than_with_zero()
    {
        using JsonDocument schedules = JsonDocument.Parse(KrakenPayloads.FeeSchedules);
        JsonElement published = schedules.RootElement.GetProperty("feeSchedules");

        Assert.Null(KrakenFuturesVenue.FeesFor(published, "not-a-uid"));
        Assert.Null(KrakenFuturesVenue.FeesFor(published, string.Empty));
        Assert.Equal((0.0002m, 0.0005m), KrakenFuturesVenue.FeesFor(published, "723888f7-0a8e-4183-8648-f920a22339e3"));
    }

    // ----- the instrument itself -----

    [Fact]
    public async Task A_contract_is_published_with_its_size_in_base_currency()
    {
        await using LoopbackServer server = new(Catalog().Handle);
        KrakenFuturesInstrumentProvider provider = await LoadedAsync(server);

        CryptoPerpetual btc = Assert.IsType<CryptoPerpetual>(provider.Find(_perp));

        Assert.Equal("PF_XBTUSD", btc.RawSymbol.Value);

        // From the venue's own base and quote fields, which say BTC here even though the symbol says XBT. Reading
        // the currency off the symbol is exactly what this must not do.
        Assert.Equal("BTC", btc.BaseCurrency.Code);
        Assert.Equal("USD", btc.QuoteCurrency.Code);
        Assert.Equal("USD", btc.SettlementCurrency.Code);

        Assert.Equal(new Price(1m, 0), btc.PriceIncrement);

        // contractValueTradePrecision 4, so the step is 0.0001 BTC and a size is stated in base units.
        Assert.Equal(new Quantity(0.0001m, 4), btc.SizeIncrement);
        Assert.Equal(new Quantity(0.0001m, 4), btc.MinQuantity);
        Assert.Equal(new Quantity(1200m, 4), btc.MaxQuantity);

        // One, not the venue's contract size: a quantity is already in base currency by the time anything above the
        // adapter reads this, so notional is quantity times price here as it is everywhere else.
        Assert.Equal(new Quantity(1m, 0), btc.Multiplier);
        Assert.Equal(new Money(8.4m, Currency.FromCode("USD")), btc.NotionalValue(new Quantity(0.0001m, 4), new Price(84_000m, 0)));
    }

    [Fact]
    public async Task A_contract_with_a_whole_number_step_is_published_the_same_way()
    {
        // DOGE's contractValueTradePrecision is 0, so the step is one whole DOGE. A test on bitcoin alone would
        // pass with the precision inverted.
        await using LoopbackServer server = new(Catalog().Handle);
        KrakenFuturesInstrumentProvider provider = await LoadedAsync(server);

        Instrument doge = provider.Find(InstrumentId.Parse("PF_DOGEUSD.KRAKEN"))!;
        Assert.Equal(new Quantity(1m, 0), doge.SizeIncrement);
        Assert.Equal(new Price(0.00001m, 5), doge.PriceIncrement);
    }

    // ----- the catalog that ignores its own filter -----

    [Fact]
    public async Task Loading_one_contract_loads_one_even_though_the_catalog_answers_with_all_of_them()
    {
        // Measured: ?symbol=PF_XBTUSD returned all 300 contracts, and /instruments/PF_XBTUSD is a 404, so there is
        // no way to ask for one. Without the filter here, loading a single instrument would load the venue - every
        // time, for every caller, with nothing throwing to say so.
        await using LoopbackServer server = new(Catalog().Handle);
        KrakenFuturesInstrumentProvider provider = new(Http(server));

        await provider.LoadAsync(_perp, CancellationToken.None);

        Instrument loaded = Assert.Single(provider.GetAll());
        Assert.Equal(_perp, loaded.Id);
    }

    [Fact]
    public async Task Loading_a_coin_margined_contract_by_name_loads_nothing()
    {
        // Asking by name for something this adapter does not offer must not half-succeed. The provider stays empty
        // and the caller's own lookup finds nothing, which is what every family here answers.
        await using LoopbackServer server = new(Catalog().Handle);
        KrakenFuturesInstrumentProvider provider = new(Http(server));

        await provider.LoadAsync(InstrumentId.Parse("PI_XBTUSD.KRAKEN"), CancellationToken.None);

        Assert.Empty(provider.GetAll());
    }

    // ----- candles the platform keeps -----

    [Theory]
    [InlineData("1-MINUTE", "1m")]
    [InlineData("5-MINUTE", "5m")]
    [InlineData("15-MINUTE", "15m")]
    [InlineData("30-MINUTE", "30m")]
    [InlineData("1-HOUR", "1h")]
    [InlineData("4-HOUR", "4h")]
    [InlineData("12-HOUR", "12h")]
    [InlineData("1-DAY", "1d")]
    [InlineData("1-WEEK", "1w")]
    public void The_bar_lengths_this_platform_keeps_are_the_ones_both_its_candle_services_answered_for(string spec, string resolution)
    {
        // Probed one by one against the charts service AND the socket, which accept the same nine and refuse
        // everything else - the service with an HTTP 400 "Invalid resolution", the socket with "Couldn't subscribe
        // to invalid feed".
        BarType barType = BarType.Parse($"PF_XBTUSD.KRAKEN-{spec}-LAST-EXTERNAL");
        Assert.Equal(resolution, KrakenFuturesVenue.Resolution(barType.Spec));
    }

    [Fact]
    public void No_monthly_length_is_offered_because_the_service_would_answer_with_minutes()
    {
        // The trap worth a test of its own. The charts service matches a resolution case-insensitively, so it
        // ACCEPTS "1M" and silently serves one MINUTE - measured, the rows came back 60 seconds apart. A caller
        // asking for monthly candles would receive minutes and no error at all, so the length is refused here
        // rather than translated.
        BarType monthly = BarType.Parse("PF_XBTUSD.KRAKEN-1-MONTH-LAST-EXTERNAL");
        NotSupportedException refused = Assert.Throws<NotSupportedException>(() => KrakenFuturesVenue.Resolution(monthly.Spec));
        Assert.Contains("12 hours", refused.Message, StringComparison.Ordinal);

        // And nothing this platform offers spells a minute with a capital M, which is what made "1M" ambiguous.
        foreach (string step in new[] { "1-MINUTE", "1-HOUR", "1-DAY", "1-WEEK" })
        {
            string resolution = KrakenFuturesVenue.Resolution(BarType.Parse($"PF_XBTUSD.KRAKEN-{step}-LAST-EXTERNAL").Spec);
            Assert.Equal(resolution.ToLowerInvariant(), resolution);
        }
    }

    [Theory]
    [InlineData("2-HOUR")]
    [InlineData("3-MINUTE")]
    [InlineData("8-HOUR")]
    public void A_bar_length_the_service_refuses_is_refused_here_too(string spec)
    {
        BarType barType = BarType.Parse($"PF_XBTUSD.KRAKEN-{spec}-LAST-EXTERNAL");
        Assert.Throws<NotSupportedException>(() => KrakenFuturesVenue.Resolution(barType.Spec));
    }

    [Fact]
    public void The_page_the_charts_service_serves_is_the_measured_one_and_not_the_documented_one()
    {
        // The service documents 5000 and gives 2000. A loop written to the documented number would ask for 5000,
        // receive 2000 and take the short page as the end of the venue's history - which is the quiet version of
        // this mistake and the reason the loop reads `more_candles` instead of counting rows.
        Assert.Equal(2000, KrakenFuturesVenue.CandlePage);
    }

    // ----- funding -----

    [Fact]
    public void Funding_settles_hourly_here_and_the_interval_is_named()
    {
        // Most venues settle every eight hours. Measured: PF_XBTUSD's funding history came back 8786 rows one hour
        // apart, so a caller assuming eight hours would price a perpetual's carry at an eighth of what it is.
        Assert.Equal(TimeSpan.FromHours(1), KrakenFuturesVenue.FundingInterval);
    }

    [Fact]
    public void The_funding_endpoint_is_the_version_that_answers_and_not_the_documented_one()
    {
        // /derivatives/api/v3/historicalfundingrates answers 404 NOT_FOUND for every symbol; the same request under
        // v4 answers with the rates. A reader following the documentation gets a 404 that reads as an unlisted
        // symbol.
        Assert.Equal("/derivatives/api/v4", KrakenFuturesVenue.ApiV4);
        Assert.NotEqual(KrakenFuturesVenue.ApiV3, KrakenFuturesVenue.ApiV4);
    }

    // ----- what a host reads about this family -----

    [Fact]
    public void The_capabilities_declared_for_this_family_say_what_it_can_really_do()
    {
        VenueFamily futures = new KrakenPlugin().Describe().Families.Single(f => f.Name == "futures");

        Assert.True(futures.Capabilities.MarketData);
        Assert.True(futures.Capabilities.Execution);
        Assert.True(futures.Capabilities.BarHistory);
        Assert.True(futures.Capabilities.FundingHistory);

        // TRUE, and the one private-surface fact on this platform that is measured rather than published:
        // /derivatives/api/v3/editorder parsed and refused a malformed order id BEFORE it refused the credentials,
        // which a path that does not exist cannot do - that answers 404 NOT_FOUND instead.
        Assert.True(futures.Capabilities.AmendOrders);

        // Both classes, because the venue lists both and its own type field does not tell them apart.
        Assert.Equal([InstrumentClass.Swap, InstrumentClass.Future], futures.InstrumentClasses);
    }
}
