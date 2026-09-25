using System.Globalization;
using Bytex.Adapters.Kraken;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Adapters.Tests.Kraken;

// Why: one caller asks for a window and Kraken answers for it out of two services that agree about nothing. Measured
// against both on 2026-09-25:
//
//                              spot /0/public/OHLC            futures /api/charts/v1
//   envelope                   {error:[],result:{...}}        {candles,more_candles} - no wrapper
//   a row                      a flat ARRAY                   an OBJECT
//   row layout                 [open,o,h,l,c,vwap,vol,count]  {time,open,high,low,close,volume}
//   the clock                  SECONDS                        MILLISECONDS
//   the window parameter       `since` only                   `from` and `to`, both in seconds
//   paging                     NONE AT ALL                    forward, and it says when it cut the window
//   one page                   720, and that is the maximum    2000, where the service documents 5000
//   the last row               the candle STILL FORMING        a closed candle
//
// The spot row is the one that bites twice: the close is the FIFTH field where a KuCoin spot row puts it third, and
// the final row is the forming candle - so a helper copied from either neighbour returns real prices in the wrong
// slots and reports a candle as closed when it has not.
//
// And the spot endpoint does not page. `since=0` returned the newest 720 one-minute candles and `since` set 2000
// minutes back returned the SAME newest 720, so a loop would ask for an older window, be handed the newest page
// again, and either spin or return the newest bars believing they were the oldest. The consequence is worth stating:
// one-minute spot history older than twelve hours cannot be fetched from Kraken at any price.
public sealed class KrakenHistoryTests
{
    private const long Hour = 3_600L * UnixNanos.NanosPerSecond;

    private static readonly BarType _spotBars = BarType.Parse("BTC-USD.KRAKEN-1-HOUR-LAST-EXTERNAL");
    private static readonly BarType _futuresBars = BarType.Parse("PF_XBTUSD.KRAKEN-1-HOUR-LAST-EXTERNAL");

    /// <summary>The close of the last candle in the spot fixture that has really closed.</summary>
    private static readonly UnixNanos _spotNow = UnixNanos.FromSeconds(KrakenPayloads.OhlcFormingOpenSeconds + 1800);

    /// <summary>The same for the futures fixture, whose third candle is still forming at this moment.</summary>
    private static readonly UnixNanos _futuresNow = UnixNanos.FromMilliseconds(KrakenPayloads.CandlesFirstOpenMs + (2 * 3_600_000L) + 1_800_000L);

    private static Instrument Spot() => new CurrencyPair(new InstrumentSpec
    {
        Id = InstrumentId.Parse("BTC-USD.KRAKEN"),
        RawSymbol = new Symbol("BTC/USD"),
        AssetClass = AssetClass.Crypto,
        InstrumentClass = InstrumentClass.Spot,
        QuoteCurrency = Currency.FromCode("USD"),
        BaseCurrency = Currency.FromCode("BTC"),
        SettlementCurrency = Currency.FromCode("USD"),
        PricePrecision = 1,
        SizePrecision = 8,
        PriceIncrement = new Price(0.1m, 1),
        SizeIncrement = new Quantity(0.00000001m, 8),
    });

    private static Instrument Futures() => new CryptoPerpetual(new InstrumentSpec
    {
        Id = InstrumentId.Parse("PF_XBTUSD.KRAKEN"),
        RawSymbol = new Symbol("PF_XBTUSD"),
        AssetClass = AssetClass.Crypto,
        InstrumentClass = InstrumentClass.Swap,
        QuoteCurrency = Currency.FromCode("USD"),
        BaseCurrency = Currency.FromCode("BTC"),
        SettlementCurrency = Currency.FromCode("USD"),
        PricePrecision = 0,
        SizePrecision = 4,
        PriceIncrement = new Price(1m, 0),
        SizeIncrement = new Quantity(0.0001m, 4),
        Multiplier = new Quantity(1m, 0),
    });

    private static KrakenHttp SpotHttp(LoopbackServer server) =>
        new(new KrakenDataClientConfig { BaseUrlHttp = server.HttpBase });

    private static KrakenHttp FuturesHttp(LoopbackServer server) =>
        new(new KrakenDataClientConfig { ProductType = KrakenProductType.Futures, BaseUrlHttp = server.HttpBase });

    // ----- spot -----

    [Fact]
    public async Task A_spot_bar_carries_the_venues_close_price_and_is_stamped_with_its_own_close()
    {
        // The row's fifth field is the close. Every number in the fixture is distinct, so a layout read as
        // [open, open, close, high, low] - which is where a KuCoin spot row puts them - fails here rather than
        // returning four real prices in the wrong places.
        await using LoopbackServer server = new(new Routes().On("GET", "/0/public/OHLC", KrakenPayloads.Ohlc).Handle);
        using KrakenHttp http = SpotHttp(server);

        IReadOnlyList<Bar> bars = await KrakenHistory.FetchBarsAsync(
            http, Spot(), _spotBars, null, null, null, _spotNow, CancellationToken.None);

        Bar first = bars[0];
        Assert.Equal(84001.9m, first.Open.Value);
        Assert.Equal(84075.9m, first.High.Value);
        Assert.Equal(83351.5m, first.Low.Value);
        Assert.Equal(83777.5m, first.Close.Value);

        // The seventh field, not the sixth - the sixth is the vwap, and 83714.6 read as a volume would be a bar
        // claiming eighty-three thousand bitcoin traded in an hour.
        Assert.Equal(159.04484404m, first.Volume.Value);

        // Close-stamped: the venue gives the OPEN in seconds, so the interval is added.
        Assert.Equal(
            UnixNanos.FromSeconds(KrakenPayloads.OhlcFirstOpenSeconds).Value + Hour,
            first.TsEvent.Value);
    }

    [Fact]
    public async Task The_candle_the_venue_is_still_building_is_not_returned_as_a_bar()
    {
        // The last row of every answer this endpoint gives is the forming candle - its own `last` cursor points one
        // interval earlier. A history that included it would say a candle closed when it has not, which is the one
        // thing a stored bar must never be.
        await using LoopbackServer server = new(new Routes().On("GET", "/0/public/OHLC", KrakenPayloads.Ohlc).Handle);
        using KrakenHttp http = SpotHttp(server);

        IReadOnlyList<Bar> bars = await KrakenHistory.FetchBarsAsync(
            http, Spot(), _spotBars, null, null, null, _spotNow, CancellationToken.None);

        Assert.Equal(3, bars.Count);
        Assert.DoesNotContain(bars, b => b.TsEvent.Value > _spotNow.Value);
        Assert.Equal(UnixNanos.FromSeconds(KrakenPayloads.OhlcFormingOpenSeconds).Value, bars[^1].TsEvent.Value);
    }

    [Fact]
    public async Task Bars_come_back_oldest_first()
    {
        await using LoopbackServer server = new(new Routes().On("GET", "/0/public/OHLC", KrakenPayloads.Ohlc).Handle);
        using KrakenHttp http = SpotHttp(server);

        IReadOnlyList<Bar> bars = await KrakenHistory.FetchBarsAsync(
            http, Spot(), _spotBars, null, null, null, _spotNow, CancellationToken.None);

        Assert.Equal(bars.Select(b => b.TsEvent.Value).Order().ToArray(), bars.Select(b => b.TsEvent.Value).ToArray());
    }

    [Fact]
    public async Task A_spot_window_is_one_request_because_the_endpoint_cannot_be_paged()
    {
        // The measured fact, enforced. A window two hundred intervals wide still produces ONE request, because
        // there is no parameter that reaches past what the venue chooses to serve: asked for an older window it
        // hands back the newest page, so a second request would return the same rows and a loop would never end.
        await using LoopbackServer server = new(new Routes().On("GET", "/0/public/OHLC", KrakenPayloads.Ohlc).Handle);
        using KrakenHttp http = SpotHttp(server);

        UnixNanos start = new(UnixNanos.FromSeconds(KrakenPayloads.OhlcFirstOpenSeconds).Value - (200 * Hour));
        await KrakenHistory.FetchBarsAsync(http, Spot(), _spotBars, start, null, null, _spotNow, CancellationToken.None);

        Assert.Single(server.RequestsTo("/0/public/OHLC"));
    }

    [Fact]
    public async Task The_start_of_a_window_is_asked_for_one_interval_early()
    {
        // A candle is filtered by its OPEN and the one covering the start of the window opens before it, so asking
        // from the start exactly would lose the first bar of every window asked for. The window rule then drops
        // anything that closed too early, so nothing extra comes back.
        await using LoopbackServer server = new(new Routes().On("GET", "/0/public/OHLC", KrakenPayloads.Ohlc).Handle);
        using KrakenHttp http = SpotHttp(server);

        UnixNanos start = UnixNanos.FromSeconds(KrakenPayloads.OhlcFirstOpenSeconds);
        await KrakenHistory.FetchBarsAsync(http, Spot(), _spotBars, start, null, null, _spotNow, CancellationToken.None);

        RecordedRequest asked = server.RequestsTo("/0/public/OHLC").Single();
        Assert.Equal(
            (KrakenPayloads.OhlcFirstOpenSeconds - 3600).ToString(CultureInfo.InvariantCulture),
            asked.Query("since"));

        // And the interval goes as a number of minutes, which is what this platform takes.
        Assert.Equal("60", asked.Query("interval"));

        // Under the name both the REST endpoint and the socket accept, which is not the one the catalog publishes.
        Assert.Equal("BTC/USD", asked.Query("pair"));
    }

    [Fact]
    public async Task With_no_start_a_limit_takes_the_newest_bars()
    {
        // The shared rule, and the direction that matters: nothing anchors the front of the window, so "give me
        // one" means the most recent closed bar rather than the oldest the venue happened to send.
        await using LoopbackServer server = new(new Routes().On("GET", "/0/public/OHLC", KrakenPayloads.Ohlc).Handle);
        using KrakenHttp http = SpotHttp(server);

        IReadOnlyList<Bar> bars = await KrakenHistory.FetchBarsAsync(
            http, Spot(), _spotBars, null, null, 1, _spotNow, CancellationToken.None);

        Bar only = Assert.Single(bars);
        Assert.Equal(UnixNanos.FromSeconds(KrakenPayloads.OhlcFormingOpenSeconds).Value, only.TsEvent.Value);
    }

    [Fact]
    public async Task With_a_start_a_limit_counts_forwards_from_it()
    {
        await using LoopbackServer server = new(new Routes().On("GET", "/0/public/OHLC", KrakenPayloads.Ohlc).Handle);
        using KrakenHttp http = SpotHttp(server);

        UnixNanos start = UnixNanos.FromSeconds(KrakenPayloads.OhlcFirstOpenSeconds);
        IReadOnlyList<Bar> bars = await KrakenHistory.FetchBarsAsync(
            http, Spot(), _spotBars, start, null, 1, _spotNow, CancellationToken.None);

        Bar only = Assert.Single(bars);
        Assert.Equal(UnixNanos.FromSeconds(KrakenPayloads.OhlcFirstOpenSeconds).Value + Hour, only.TsEvent.Value);
    }

    [Fact]
    public async Task The_rows_are_found_by_being_the_array_rather_than_by_the_name_beside_them()
    {
        // The venue echoes back whichever of its three spellings a request used - pair=XBTUSD comes back keyed
        // XXBTZUSD and pair=BTC/USD comes back keyed BTC/USD - so a helper that looked the rows up by name would
        // work for one spelling and find nothing for another.
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/0/public/OHLC", KrakenPayloads.Ohlc.Replace("BTC/USD", "XXBTZUSD", StringComparison.Ordinal))
            .Handle);

        using KrakenHttp http = SpotHttp(server);

        IReadOnlyList<Bar> bars = await KrakenHistory.FetchBarsAsync(
            http, Spot(), _spotBars, null, null, null, _spotNow, CancellationToken.None);

        Assert.Equal(3, bars.Count);
    }

    [Fact]
    public async Task A_bar_length_the_platform_does_not_keep_never_reaches_the_venue()
    {
        await using LoopbackServer server = new(new Routes().On("GET", "/0/public/OHLC", KrakenPayloads.Ohlc).Handle);
        using KrakenHttp http = SpotHttp(server);

        await Assert.ThrowsAsync<NotSupportedException>(() => KrakenHistory.FetchBarsAsync(
            http,
            Spot(),
            BarType.Parse("BTC-USD.KRAKEN-2-HOUR-LAST-EXTERNAL"),
            null,
            null,
            null,
            _spotNow,
            CancellationToken.None));

        Assert.Empty(server.RequestsTo("/0/public/OHLC"));
    }

    // ----- futures -----

    private const string CandlesPath = "/api/charts/v1/trade/PF_XBTUSD/1h";

    [Fact]
    public async Task A_futures_bar_is_stamped_with_a_close_computed_from_the_open_the_venue_gave()
    {
        // This service stamps a candle with its OPEN, in milliseconds - the opposite of the spot socket, which
        // stamps the close. So the close is computed, and it has to be computed from milliseconds: read as seconds
        // the timestamp would be fifty thousand years out.
        await using LoopbackServer server = new(new Routes().On("GET", CandlesPath, KrakenPayloads.Candles).Handle);
        using KrakenHttp http = FuturesHttp(server);

        IReadOnlyList<Bar> bars = await KrakenHistory.FetchBarsAsync(
            http, Futures(), _futuresBars, null, null, null, _futuresNow, CancellationToken.None);

        Bar first = bars[0];
        Assert.Equal(84406m, first.Open.Value);
        Assert.Equal(84924m, first.High.Value);
        Assert.Equal(84009m, first.Low.Value);
        Assert.Equal(84476m, first.Close.Value);
        Assert.Equal(250.905m, first.Volume.Value);
        Assert.Equal(
            UnixNanos.FromMilliseconds(KrakenPayloads.CandlesFirstOpenMs).Value + Hour,
            first.TsEvent.Value);
    }

    [Fact]
    public async Task The_futures_candle_that_has_not_closed_yet_is_left_out_as_well()
    {
        await using LoopbackServer server = new(new Routes().On("GET", CandlesPath, KrakenPayloads.Candles).Handle);
        using KrakenHttp http = FuturesHttp(server);

        IReadOnlyList<Bar> bars = await KrakenHistory.FetchBarsAsync(
            http, Futures(), _futuresBars, null, null, null, _futuresNow, CancellationToken.None);

        Assert.Equal(2, bars.Count);
        Assert.DoesNotContain(bars, b => b.TsEvent.Value > _futuresNow.Value);
    }

    [Fact]
    public async Task The_resolution_is_in_the_path_and_the_window_is_in_seconds()
    {
        // The service takes the length as a path segment rather than a parameter, and its window in SECONDS even
        // though it answers in milliseconds. Both in one request, because both are easy to get wrong and neither
        // produces an error - a window in milliseconds is a window sixteen thousand years wide.
        await using LoopbackServer server = new(new Routes().On("GET", CandlesPath, KrakenPayloads.Candles).Handle);
        using KrakenHttp http = FuturesHttp(server);

        UnixNanos start = UnixNanos.FromMilliseconds(KrakenPayloads.CandlesFirstOpenMs);
        await KrakenHistory.FetchBarsAsync(http, Futures(), _futuresBars, start, null, null, _futuresNow, CancellationToken.None);

        RecordedRequest asked = Assert.Single(server.RequestsTo(CandlesPath));
        Assert.Equal(
            ((KrakenPayloads.CandlesFirstOpenMs / 1000) - 3600).ToString(CultureInfo.InvariantCulture),
            asked.Query("from"));

        Assert.Equal(
            (_futuresNow.Value / UnixNanos.NanosPerSecond).ToString(CultureInfo.InvariantCulture),
            asked.Query("to"));
    }

    [Fact]
    public async Task A_window_the_service_cut_short_is_walked_until_it_says_it_did_not()
    {
        // The service says for itself whether it truncated, so the end of the history is read off its own answer
        // rather than guessed from a page that did not fill - which is what a count-the-rows loop does, and what
        // would stop after one page on a venue whose documented page size is wrong.
        int page = 0;
        await using LoopbackServer server = new(new Routes()
            .On("GET", CandlesPath, _ => StubResponse.Json(++page == 1 ? KrakenPayloads.CandlesTruncated : KrakenPayloads.Candles))
            .Handle);

        using KrakenHttp http = FuturesHttp(server);

        UnixNanos start = UnixNanos.FromMilliseconds(KrakenPayloads.CandlesFirstOpenMs);
        IReadOnlyList<Bar> bars = await KrakenHistory.FetchBarsAsync(
            http, Futures(), _futuresBars, start, null, null, _futuresNow, CancellationToken.None);

        Assert.Equal(2, server.RequestsTo(CandlesPath).Count);

        // The truncated page's one candle and the full page's two, the duplicate dropped, oldest first.
        Assert.Equal(2, bars.Count);

        // And the second request started after the newest candle of the first.
        Assert.Equal(
            ((KrakenPayloads.CandlesFirstOpenMs / 1000) + 3600).ToString(CultureInfo.InvariantCulture),
            server.RequestsTo(CandlesPath)[1].Query("from"));
    }

    [Fact]
    public async Task A_refusal_from_the_charts_service_is_not_read_as_an_envelope()
    {
        // This service answers a failure with PLAIN TEXT and an HTTP 400 - "Invalid resolution", "Invalid
        // instrument" - where the rest of the platform answers with JSON. A reader expecting the envelope would
        // fail to parse the refusal and report a parse error instead of what the venue said.
        await using LoopbackServer server = new(new Routes()
            .On("GET", CandlesPath, _ => new StubResponse(400, KrakenPayloads.InvalidResolution, "text/plain"))
            .Handle);

        using KrakenHttp http = FuturesHttp(server);

        KrakenApiException refusal = await Assert.ThrowsAsync<KrakenApiException>(() => KrakenHistory.FetchBarsAsync(
            http, Futures(), _futuresBars, null, null, null, _futuresNow, CancellationToken.None));

        Assert.Equal(KrakenPayloads.InvalidResolution, refusal.Code);
        Assert.Equal(400, refusal.HttpStatus);
    }

    // ----- funding -----

    private const string FundingPath = "/derivatives/api/v4/historicalfundingrates";

    [Fact]
    public async Task The_funding_rate_published_is_the_fraction_of_notional_and_not_the_charge_per_contract()
    {
        // The endpoint carries two numbers per settlement and only one is a rate. `fundingRate` is an absolute
        // charge - 1.33 on PF_XBTUSD and 1.5e-10 on PI_XBTUSD at the same hour, which is not a scale any rate has -
        // and `relativeFundingRate` is the fraction a position is charged. Taking the first would price a
        // perpetual's carry at 133 per cent an hour.
        await using LoopbackServer server = new(new Routes().On("GET", FundingPath, KrakenPayloads.FundingRates).Handle);
        using KrakenHttp http = FuturesHttp(server);

        IReadOnlyList<FundingRateUpdate> rates = await KrakenHistory.FetchFundingRatesAsync(
            http, InstrumentId.Parse("PF_XBTUSD.KRAKEN"), null, null, null, CancellationToken.None);

        Assert.Equal(3, rates.Count);
        Assert.Equal(0.000011795815277778m, rates[0].Rate);
        Assert.NotEqual(1.3285114808978307m, rates[0].Rate);
    }

    [Fact]
    public async Task Funding_settlements_come_back_oldest_first_and_stamped_with_the_settlement_time()
    {
        await using LoopbackServer server = new(new Routes().On("GET", FundingPath, KrakenPayloads.FundingRates).Handle);
        using KrakenHttp http = FuturesHttp(server);

        IReadOnlyList<FundingRateUpdate> rates = await KrakenHistory.FetchFundingRatesAsync(
            http, InstrumentId.Parse("PF_XBTUSD.KRAKEN"), null, null, null, CancellationToken.None);

        Assert.Equal(
            UnixNanos.FromDateTimeOffset(DateTimeOffset.Parse("2025-09-24T08:00:00Z", CultureInfo.InvariantCulture)),
            rates[0].TsEvent);

        Assert.Equal(rates.Select(r => r.TsEvent.Value).Order().ToArray(), rates.Select(r => r.TsEvent.Value).ToArray());

        // An hour apart, which is this platform's settlement interval and not the eight hours most venues use.
        Assert.Equal(
            KrakenFuturesVenue.FundingInterval.Ticks * UnixNanos.NanosPerTick,
            rates[1].TsEvent.Value - rates[0].TsEvent.Value);
    }

    [Fact]
    public async Task The_window_is_applied_here_because_the_endpoint_ignores_its_own_parameters()
    {
        // Measured: the endpoint answered 8786 hourly settlements with no window, and answered the IDENTICAL 8786
        // for a request carrying `from` and `to` an hour apart. It ignores both silently, so a loop written against
        // them would fetch the same year over and over - and a caller that trusted them would price a month's
        // funding out of a year's worth of settlements.
        await using LoopbackServer server = new(new Routes().On("GET", FundingPath, KrakenPayloads.FundingRates).Handle);
        using KrakenHttp http = FuturesHttp(server);

        UnixNanos from = UnixNanos.FromDateTimeOffset(DateTimeOffset.Parse("2025-09-24T09:00:00Z", CultureInfo.InvariantCulture));
        IReadOnlyList<FundingRateUpdate> rates = await KrakenHistory.FetchFundingRatesAsync(
            http, InstrumentId.Parse("PF_XBTUSD.KRAKEN"), from, null, null, CancellationToken.None);

        Assert.Equal(2, rates.Count);
        Assert.Equal(from, rates[0].TsEvent);

        // And nothing was asked of the venue except the symbol, because nothing else would be read.
        RecordedRequest asked = Assert.Single(server.RequestsTo(FundingPath));
        Assert.Equal("PF_XBTUSD", asked.Query("symbol"));
        Assert.Null(asked.Query("from"));
        Assert.Null(asked.Query("to"));
        Assert.Single(asked.QueryPairs);
    }

    [Fact]
    public async Task A_limit_on_funding_counts_from_the_end_the_caller_anchored()
    {
        await using LoopbackServer server = new(new Routes().On("GET", FundingPath, KrakenPayloads.FundingRates).Handle);
        using KrakenHttp http = FuturesHttp(server);
        InstrumentId perp = InstrumentId.Parse("PF_XBTUSD.KRAKEN");

        IReadOnlyList<FundingRateUpdate> newest = await KrakenHistory.FetchFundingRatesAsync(
            http, perp, null, null, 1, CancellationToken.None);

        Assert.Equal(
            UnixNanos.FromDateTimeOffset(DateTimeOffset.Parse("2025-09-24T10:00:00Z", CultureInfo.InvariantCulture)),
            Assert.Single(newest).TsEvent);

        UnixNanos from = UnixNanos.FromDateTimeOffset(DateTimeOffset.Parse("2025-09-24T08:00:00Z", CultureInfo.InvariantCulture));
        IReadOnlyList<FundingRateUpdate> oldest = await KrakenHistory.FetchFundingRatesAsync(
            http, perp, from, null, 1, CancellationToken.None);

        Assert.Equal(from, Assert.Single(oldest).TsEvent);
    }

    [Fact]
    public async Task Asking_the_spot_platform_for_funding_says_what_is_wrong_rather_than_asking_it()
    {
        // Spot pays no funding and has no endpoint for it. A request that reached the host would be a 404 the
        // caller would read as "this instrument has no funding history", which is a different statement.
        await using LoopbackServer server = new(new Routes().On("GET", FundingPath, KrakenPayloads.FundingRates).Handle);
        using KrakenHttp http = SpotHttp(server);

        ArgumentException refused = await Assert.ThrowsAsync<ArgumentException>(() => KrakenHistory.FetchFundingRatesAsync(
            http, InstrumentId.Parse("BTC-USD.KRAKEN"), null, null, null, CancellationToken.None));

        Assert.Contains("pays no funding", refused.Message, StringComparison.Ordinal);
        Assert.Empty(server.Requests);
    }

    // ----- and it needs no node -----

    [Fact]
    public void Both_helpers_are_public_and_static_so_a_history_download_needs_no_node()
    {
        // E7. A host that stores history has no node, so a helper has to be callable with an http client, an
        // instrument and a window and nothing else. A private one on a data client is how paging comes to be
        // written twice and to disagree with itself.
        Type history = typeof(KrakenHistory);

        Assert.Contains(
            history.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static),
            m => m.Name == nameof(KrakenHistory.FetchBarsAsync));

        Assert.Contains(
            history.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static),
            m => m.Name == nameof(KrakenHistory.FetchFundingRatesAsync));
    }

    [Fact]
    public async Task One_entry_point_answers_for_both_platforms()
    {
        // A caller storing history has an instrument and a window and no business knowing that this venue answers
        // for them out of two services that disagree about every detail. Which one is asked follows from the http
        // client's own platform.
        await using LoopbackServer spot = new(new Routes().On("GET", "/0/public/OHLC", KrakenPayloads.Ohlc).Handle);
        await using LoopbackServer futures = new(new Routes().On("GET", CandlesPath, KrakenPayloads.Candles).Handle);
        using KrakenHttp spotHttp = SpotHttp(spot);
        using KrakenHttp futuresHttp = FuturesHttp(futures);

        Assert.NotEmpty(await KrakenHistory.FetchBarsAsync(spotHttp, Spot(), _spotBars, null, null, null, _spotNow, CancellationToken.None));
        Assert.NotEmpty(await KrakenHistory.FetchBarsAsync(futuresHttp, Futures(), _futuresBars, null, null, null, _futuresNow, CancellationToken.None));

        Assert.NotEmpty(spot.RequestsTo("/0/public/OHLC"));
        Assert.NotEmpty(futures.RequestsTo(CandlesPath));
    }
}
