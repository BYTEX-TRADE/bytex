using Bytex.Adapters.Kucoin;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Adapters.Tests.Kucoin;

// Why: KuCoin's futures market shares a key with its spot market and nothing else. It answers on a different host,
// spells its symbols differently, orders its candle rows differently, counts its volume in a different unit, and caps
// a page at a different number than either its spot market or its own documentation. Every one of those differences
// produces plausible wrong numbers rather than an error, which is why each has a test here and the test says what the
// wrong answer would have looked like.
//
// The shapes and the limits below were measured against the live venue on 2026-09-25 across all 690 contracts it
// listed, not read off its documentation - which says a page holds 500 rows where the venue gives 200.
public sealed class KucoinFuturesTests
{
    private const long Minute = 60_000_000_000L;
    private static readonly BarType _barType = BarType.Parse("XBTUSDT-PERP.KUCOIN-1-MINUTE-LAST-EXTERNAL");

    private static KucoinHttp Http(LoopbackServer server) =>
        new(new KucoinDataClientConfig { ProductType = KucoinProductType.Futures, BaseUrlHttp = server.HttpBase });

    private static Routes Contracts() => new Routes().On("GET", "/api/v1/contracts/active", KucoinPayloads.FuturesContracts);

    private static async Task<KucoinFuturesInstrumentProvider> LoadedAsync(LoopbackServer server)
    {
        KucoinFuturesInstrumentProvider provider = new(Http(server));
        await provider.LoadAllAsync(CancellationToken.None);
        return provider;
    }

    // ----- what the venue calls a contract, and what the engine calls it -----

    [Theory]
    [InlineData("XBTUSDTM", "XBTUSDT-PERP.KUCOIN")]
    [InlineData("DOGEUSDTM", "DOGEUSDT-PERP.KUCOIN")]
    [InlineData("XBTUSDCM", "XBTUSDC-PERP.KUCOIN")]
    public void A_contract_and_its_instrument_id_map_to_each_other_without_a_lookup(string raw, string id)
    {
        // Checked against every one of the 684 tradable contracts the venue listed: all end in M, the rule never
        // collides, and it runs backwards exactly. A mapping that needed a table would mean an id could not be
        // turned into a request without the catalog already loaded.
        Assert.Equal(id, KucoinFuturesVenue.ToInstrumentId(raw).ToString());
        Assert.Equal(raw, KucoinFuturesVenue.ToRawSymbol(InstrumentId.Parse(id)));
    }

    [Fact]
    public void Futures_ids_are_not_spot_ids()
    {
        // The venue writes bitcoin as XBT here and BTC on spot, and separates a spot pair with a dash. Three
        // spellings on one venue, so anything holding an id has to have got it from the right family.
        Assert.Equal("BTC-USDT.KUCOIN", KucoinVenue.ToInstrumentId("BTC-USDT").ToString());
        Assert.Equal("XBTUSDT-PERP.KUCOIN", KucoinFuturesVenue.ToInstrumentId("XBTUSDTM").ToString());
    }

    // ----- the instrument -----

    [Fact]
    public async Task A_contract_is_published_with_its_size_in_base_currency_rather_than_in_contracts()
    {
        // The whole of what this adapter hides. One XBTUSDTM contract is 0.001 XBT and the venue takes whole
        // contracts, so the smallest tradable quantity is 0.001 XBT and the step between quantities is 0.001 XBT -
        // published exactly as Binance and Bybit publish theirs. A strategy sizing in base units is then the same
        // strategy on all three venues, and nothing above the adapter ever sees a contract.
        await using LoopbackServer server = new(Contracts().Handle);
        KucoinFuturesInstrumentProvider provider = await LoadedAsync(server);

        CryptoPerpetual btc = Assert.IsType<CryptoPerpetual>(provider.Find(InstrumentId.Parse("XBTUSDT-PERP.KUCOIN")));
        Assert.Equal("XBTUSDTM", btc.RawSymbol!.Value);
        Assert.Equal(InstrumentClass.Swap, btc.InstrumentClass);
        Assert.Equal("XBT", btc.BaseCurrency.Code);
        Assert.Equal("USDT", btc.QuoteCurrency.Code);
        Assert.Equal("USDT", btc.SettlementCurrency.Code);
        Assert.Equal(new Quantity(0.001m, 3), btc.SizeIncrement);
        Assert.Equal(new Quantity(0.001m, 3), btc.MinQuantity);
        Assert.Equal(new Price(0.1m, 1), btc.PriceIncrement);
        Assert.Equal(0.0002m, btc.MakerFee);
        Assert.Equal(0.0006m, btc.TakerFee);

        // One thousand contracts is 1 XBT, and the maximum order is a million contracts.
        Assert.Equal(new Quantity(1000m, 3), btc.MaxQuantity);

        // One, not 0.001: a quantity is already in base currency by the time anything reads this, so notional is
        // quantity times price here exactly as everywhere else. Were the venue's own 0.001 left here, every notional,
        // margin and commission on this instrument would come out a thousandth of what it should be.
        Assert.Equal(new Quantity(1m, 0), btc.Multiplier);
        Assert.Equal(new Money(84_000m, Currencies.USDT), btc.NotionalValue(new Quantity(1m, 3), new Price(84_000m, 1)));
    }

    [Fact]
    public async Task A_contract_worth_more_than_one_unit_is_published_the_same_way()
    {
        // The multiplier is not always a fraction: it runs from 0.01 to 1000 across the family, so a test on
        // bitcoin alone would pass with the conversion inverted.
        await using LoopbackServer server = new(Contracts().Handle);
        KucoinFuturesInstrumentProvider provider = await LoadedAsync(server);

        Instrument doge = Assert.IsType<CryptoPerpetual>(provider.Find(InstrumentId.Parse("DOGEUSDT-PERP.KUCOIN")));
        Assert.Equal(new Quantity(10m, 0), doge.SizeIncrement);
        Assert.Equal(new Quantity(10m, 0), doge.MinQuantity);
        Assert.Equal(10m, KucoinFuturesVenue.Multiplier(doge));
    }

    [Fact]
    public async Task Inverse_contracts_are_not_offered_at_all()
    {
        // They are coin-margined and quoted in USD while settling in the base currency, so a quantity of one cannot
        // be expressed in base currency without a price - which is the one thing this adapter promises everything
        // above it. Leaving them out is the statement; offering them with a guessed size would not be.
        await using LoopbackServer server = new(Contracts().Handle);
        KucoinFuturesInstrumentProvider provider = await LoadedAsync(server);

        Assert.Equal(
            ["DOGEUSDT-PERP.KUCOIN", "XBTUSDT-PERP.KUCOIN"],
            provider.GetAll().Select(i => i.Id.ToString()).Order().ToArray());

        Assert.Null(provider.Find(InstrumentId.Parse("XBTUSD-PERP.KUCOIN")));
        Assert.Null(provider.Find(InstrumentId.Parse("XBTMU26.KUCOIN")));

        // The one whose only disqualification is being inverse - an ordinary positive contract size, so nothing
        // else in the parse would turn it away. Without the isInverse check it would be offered, sized as though a
        // contract were one ETH, and every quantity on it would be wrong by the price of ether.
        Assert.Null(provider.Find(InstrumentId.Parse("ETHUSDX-PERP.KUCOIN")));
    }

    [Fact]
    public async Task A_contract_with_a_delivery_date_is_not_published_as_a_perpetual()
    {
        // The family declares that it holds perpetuals. Publishing a dated contract as one would give it the wrong
        // instrument class, record no expiry for anything to warn about, assume it is charged funding, and make the
        // declaration untrue - and the test that compares the declaration against this provider would pass anyway,
        // because it would read the same wrong class from both sides.
        //
        // Every dated contract the venue lists is also inverse, so the fixture carries a linear one that the venue
        // does not return today. Without it, "not inverse" and "perpetual" would look like the same rule.
        await using LoopbackServer server = new(Contracts().Handle);
        KucoinFuturesInstrumentProvider provider = await LoadedAsync(server);

        Assert.Null(provider.Find(InstrumentId.Parse("ETHUSDTZ26.KUCOIN")));
        Assert.Null(provider.Find(InstrumentId.Parse("ETHUSDTZ2-PERP.KUCOIN")));

        Assert.Equal(
            ["DOGEUSDT-PERP.KUCOIN", "XBTUSDT-PERP.KUCOIN"],
            provider.GetAll().Select(i => i.Id.ToString()).Order().ToArray());

        Assert.All(provider.GetAll(), i => Assert.Equal(InstrumentClass.Swap, i.InstrumentClass));
    }

    [Fact]
    public async Task A_symbol_that_contains_another_currencys_name_keeps_its_own()
    {
        // AIXBT is a token, not bitcoin with a prefix, and ETHBTC is a base in its own right. Nothing here resolves
        // a currency by looking for it inside a symbol - the base and the quote are read from the venue's own fields
        // and the id mapping only touches the trailing M - and this is the test that says so rather than a claim.
        await using LoopbackServer server = new(
            new Routes().On("GET", "/api/v1/contracts/active", KucoinPayloads.FuturesLookalikeContracts).Handle);
        KucoinFuturesInstrumentProvider provider = await LoadedAsync(server);

        Assert.Equal(
            ["AIXBTUSDT-PERP.KUCOIN", "ETHBTCUSDT-PERP.KUCOIN", "XBTUSDT-PERP.KUCOIN"],
            provider.GetAll().Select(i => i.Id.ToString()).Order().ToArray());

        Instrument aixbt = provider.Find(InstrumentId.Parse("AIXBTUSDT-PERP.KUCOIN"))!;
        Assert.Equal("AIXBT", aixbt.BaseCurrency!.Code);
        Assert.Equal("AIXBTUSDTM", aixbt.RawSymbol!.Value);
        Assert.Equal(new Quantity(1m, 0), aixbt.SizeIncrement);

        Instrument ethbtc = provider.Find(InstrumentId.Parse("ETHBTCUSDT-PERP.KUCOIN"))!;
        Assert.Equal("ETHBTC", ethbtc.BaseCurrency!.Code);
        Assert.Equal("USDT", ethbtc.QuoteCurrency.Code);

        // And bitcoin itself is still XBT, not any of the symbols that contain those letters.
        Assert.Equal("XBT", provider.Find(InstrumentId.Parse("XBTUSDT-PERP.KUCOIN"))!.BaseCurrency!.Code);
    }

    // ----- contracts in, base currency out -----

    [Fact]
    public async Task A_quantity_crosses_into_contracts_and_back_without_changing()
    {
        await using LoopbackServer server = new(Contracts().Handle);
        KucoinFuturesInstrumentProvider provider = await LoadedAsync(server);
        Instrument btc = provider.Find(InstrumentId.Parse("XBTUSDT-PERP.KUCOIN"))!;

        Assert.Equal(1L, KucoinFuturesVenue.ToContracts(btc, new Quantity(0.001m, 3)));
        Assert.Equal(1000L, KucoinFuturesVenue.ToContracts(btc, new Quantity(1m, 3)));
        Assert.Equal(new Quantity(0.084m, 3), KucoinFuturesVenue.ToQuantity(btc, 84m));
        Assert.Equal(new Quantity(1m, 3), KucoinFuturesVenue.ToQuantity(btc, 1000m));
    }

    [Fact]
    public async Task A_quantity_that_is_not_a_whole_number_of_contracts_is_refused_rather_than_rounded()
    {
        // Rounding here would fill a different size than the caller asked for and report the size it asked for. The
        // engine rounds to the instrument's increment, which is one contract, so a quantity that fails this was
        // built by hand and the caller needs to hear about it.
        await using LoopbackServer server = new(Contracts().Handle);
        KucoinFuturesInstrumentProvider provider = await LoadedAsync(server);
        Instrument btc = provider.Find(InstrumentId.Parse("XBTUSDT-PERP.KUCOIN"))!;

        ArgumentException error = Assert.Throws<ArgumentException>(
            () => KucoinFuturesVenue.ToContracts(btc, new Quantity(0.0015m, 4)));

        Assert.Contains("whole ones", error.Message, StringComparison.Ordinal);
    }

    // ----- candles -----

    [Fact]
    public async Task A_candle_row_is_read_in_the_futures_order_not_the_spot_one()
    {
        // The single most dangerous difference on this venue. Futures is [start, open, HIGH, LOW, close, volume] and
        // spot is [start, open, CLOSE, HIGH, LOW, volume], so a fetch copied across from spot returns a bar whose
        // high is its close and whose close is its high - four prices, all real, all in the wrong places, and no
        // error anywhere. The four values here are deliberately distinct and ordered so that any permutation fails.
        long startMs = 1_758_441_600_000L;
        Routes routes = Contracts().On(
            "GET",
            "/api/v1/kline/query",
            KucoinPayloads.FuturesCandles(KucoinPayloads.FuturesCandle(startMs, "100.1", "400.4", "10.2", "250.5", "4000")));

        await using LoopbackServer server = new(routes.Handle);
        KucoinFuturesInstrumentProvider provider = await LoadedAsync(server);
        Instrument btc = provider.Find(InstrumentId.Parse("XBTUSDT-PERP.KUCOIN"))!;

        UnixNanos open = UnixNanos.FromMilliseconds(startMs);
        IReadOnlyList<Bar> bars = await KucoinHistory.FetchBarsAsync(
            Http(server), btc, _barType, open, open, null, new UnixNanos(open.Value + (5 * Minute)));

        Bar bar = Assert.Single(bars);
        Assert.Equal(new Price(100.1m, 1), bar.Open);
        Assert.Equal(new Price(400.4m, 1), bar.High);
        Assert.Equal(new Price(10.2m, 1), bar.Low);
        Assert.Equal(new Price(250.5m, 1), bar.Close);

        // And the volume is 4,000 contracts, which is 4 XBT. Reported as 4,000 it would read as a thousand times the
        // real turnover and make every volume-weighted number on this venue wrong.
        Assert.Equal(new Quantity(4m, 3), bar.Volume);

        // Stamped when it closed, as every other venue's bars are.
        Assert.Equal(new UnixNanos(open.Value + Minute), bar.TsEvent);
    }

    [Fact]
    public async Task A_window_wider_than_one_page_is_walked_forwards_until_the_venue_stops()
    {
        // The venue answers with the OLDEST rows of the window and caps them at 200 - not 500, which is what its own
        // documentation says. A loop that trusted the documented number would read one page, see 200 rows where it
        // expected up to 500, conclude the venue had no more and return a fifth of what was asked for. So the page
        // size is what the venue does, and the loop stops on a page that did not fill rather than on a count.
        long startMs = 1_758_441_600_000L;

        // What the venue really answers with, written out rather than taken from the constant under test. Taking it
        // from the constant made this test move with the mistake it exists to catch: told the page was 500, the
        // stub obligingly returned 500 and the walk succeeded. The whole defect is the code believing a number the
        // venue does not honour, so the stub has to be the venue.
        const int venuePage = 200;
        List<long> requested = new();

        Routes routes = Contracts().On("GET", "/api/v1/kline/query", r =>
        {
            long from = long.Parse(r.Query("from")!, System.Globalization.CultureInfo.InvariantCulture);

            // A full page for the first request and a short one for the second, each starting where it was asked
            // to start - which is what a venue that answers with the oldest rows of the window does.
            int rows = requested.Count == 0 ? venuePage : requested.Count == 1 ? 50 : 0;
            requested.Add(from);
            return StubResponse.Json(KucoinPayloads.FuturesCandles(
                [.. Enumerable.Range(0, rows).Select(i => KucoinPayloads.FuturesCandle(
                    from + (i * 60_000L), "1", "2", "0.5", "1.5", "1000"))]));
        });

        await using LoopbackServer server = new(routes.Handle);
        KucoinFuturesInstrumentProvider provider = await LoadedAsync(server);
        Instrument btc = provider.Find(InstrumentId.Parse("XBTUSDT-PERP.KUCOIN"))!;

        // The window ends where the venue's last candle closes. Asked for a window that ran on past it, the flat
        // bars would run on too - correctly, because this venue writes a quiet interval's candle only when the next
        // trade arrives, so absence is not evidence of no data. That behaviour has its own test; this one is about
        // the walk, so the window is the data.
        UnixNanos open = UnixNanos.FromMilliseconds(startMs);
        UnixNanos last = new(open.Value + ((venuePage + 49) * Minute));
        IReadOnlyList<Bar> bars = await KucoinHistory.FetchBarsAsync(Http(server), btc, _barType, open, last, null, last);

        // Two requests, the second starting after the first ended: the loop walks forwards and stops on a page
        // that did not fill, rather than on a row count it was told to expect.
        // The constant is the measured fact, and it is the thing that decides when the walk stops: a fetch that
        // expected more rows than the venue gives would read one page, take 200 where it wanted 500, and conclude
        // it had reached the end of the venue's history with four fifths of the window still unread.
        Assert.Equal(venuePage, KucoinFuturesVenue.CandlePage);

        Assert.Equal(2, requested.Count);
        Assert.True(requested[1] > requested[0], "the second page has to start after the first one");
        Assert.Equal(requested[0] + (venuePage * 60_000L), requested[1]);
        Assert.Equal(venuePage + 50, bars.Count);
        Assert.True(bars.Zip(bars.Skip(1)).All(p => p.First.TsEvent.Value < p.Second.TsEvent.Value), "oldest first");

        // Both pages are in, and nothing past the venue's last candle was invented to fill the window out.
        Assert.Equal(open, bars[0].TsEvent);
        Assert.Equal(last, bars[^1].TsEvent);
    }

    [Fact]
    public async Task An_interval_the_venue_left_out_comes_back_as_a_flat_bar()
    {
        // Measured on the live venue: ETHUSDCM answered 198 rows across 201 minutes, with no zero-volume row among
        // them - so an interval without a trade is simply absent rather than reported flat. Spot does the same, and
        // filling them here is what keeps a caller from telling the two markets apart by the shape of the answer.
        long startMs = 1_758_441_600_000L;
        Routes routes = Contracts().On("GET", "/api/v1/kline/query", KucoinPayloads.FuturesCandles(
            KucoinPayloads.FuturesCandle(startMs, "100", "110", "90", "105", "1000"),
            KucoinPayloads.FuturesCandle(startMs + 180_000L, "106", "112", "104", "108", "2000")));

        await using LoopbackServer server = new(routes.Handle);
        KucoinFuturesInstrumentProvider provider = await LoadedAsync(server);
        Instrument btc = provider.Find(InstrumentId.Parse("XBTUSDT-PERP.KUCOIN"))!;

        UnixNanos open = UnixNanos.FromMilliseconds(startMs);
        UnixNanos now = new(open.Value + (10 * Minute));
        IReadOnlyList<Bar> bars = await KucoinHistory.FetchBarsAsync(
            Http(server), btc, _barType, open, new UnixNanos(open.Value + (3 * Minute)), null, now);

        Assert.Equal(4, bars.Count);

        // The two the venue left out stand at the previous close, flat, with no volume.
        foreach (Bar quiet in bars.Skip(1).Take(2))
        {
            Assert.Equal(new Price(105m, 1), quiet.Open);
            Assert.Equal(new Price(105m, 1), quiet.High);
            Assert.Equal(new Price(105m, 1), quiet.Low);
            Assert.Equal(new Price(105m, 1), quiet.Close);
            Assert.Equal(new Quantity(0m, 3), quiet.Volume);
        }

        Assert.Equal(new Price(108m, 1), bars[^1].Close);
    }

    [Theory]
    [InlineData(BarAggregation.Minute, 1, 1)]
    [InlineData(BarAggregation.Minute, 30, 30)]
    [InlineData(BarAggregation.Hour, 4, 240)]
    [InlineData(BarAggregation.Day, 1, 1440)]
    [InlineData(BarAggregation.Week, 1, 10080)]
    public void A_bar_length_is_asked_for_as_a_number_of_minutes(BarAggregation aggregation, int step, int granularity)
    {
        // Spot names its lengths ("1min", "4hour"); futures counts minutes. Sending a spot word here is refused by
        // the venue rather than misread, which is the one difference on this market that fails loudly.
        Assert.Equal(granularity, KucoinFuturesVenue.Granularity(new BarSpecification(step, aggregation, PriceType.Last)));
    }

    [Fact]
    public void A_bar_length_the_venue_does_not_keep_is_refused_before_it_is_asked_for()
    {
        // The venue answers code 300000 "Unsupported granularity" for a length it does not keep - measured, with a
        // seven-minute bar. Refusing here says which lengths there are, instead of a caller reading a venue error.
        NotSupportedException error = Assert.Throws<NotSupportedException>(
            () => KucoinFuturesVenue.Granularity(new BarSpecification(7, BarAggregation.Minute, PriceType.Last)));

        Assert.Contains("1, 3, 5, 15 and 30 minutes", error.Message, StringComparison.Ordinal);
    }

    // ----- funding -----

    [Fact]
    public async Task Funding_settlements_come_back_oldest_first_however_the_venue_orders_them()
    {
        // The venue answers candles oldest first and funding newest first, on the same API. Everything that stores
        // history expects one order, so the adapter settles it rather than the caller.
        long at = 1_758_441_600_000L;
        Routes routes = new Routes().On("GET", "/api/v1/contract/funding-rates", KucoinPayloads.FundingRates(
            (at + 57_600_000L, "0.000034"),
            (at + 28_800_000L, "0.000102"),
            (at, "0.000058")));

        await using LoopbackServer server = new(routes.Handle);
        IReadOnlyList<FundingRateUpdate> rates = await KucoinHistory.FetchFundingRatesAsync(
            Http(server),
            InstrumentId.Parse("XBTUSDT-PERP.KUCOIN"),
            UnixNanos.FromMilliseconds(at).ToDateTimeOffset());

        Assert.Equal(3, rates.Count);
        Assert.Equal([0.000058m, 0.000102m, 0.000034m], rates.Select(r => r.Rate).ToArray());
        Assert.True(rates.Zip(rates.Skip(1)).All(p => p.First.TsEvent.Value < p.Second.TsEvent.Value));
        Assert.All(rates, r => Assert.Equal("XBTUSDT-PERP.KUCOIN", r.InstrumentId.ToString()));
    }

    // ----- which market a client is pointed at -----

    [Fact]
    public void The_product_decides_the_host_and_spot_is_what_a_configuration_without_one_means()
    {
        // Futures is a different host, not a different path, so a configuration that does not say which market it
        // wants has to keep meaning what it meant before futures existed.
        Assert.Equal("https://api.kucoin.com", KucoinVenue.HttpBase(new KucoinDataClientConfig()));
        Assert.Equal(
            "https://api.kucoin.com",
            KucoinVenue.HttpBase(new KucoinDataClientConfig { ProductType = KucoinProductType.Spot }));
        Assert.Equal(
            "https://api-futures.kucoin.com",
            KucoinVenue.HttpBase(new KucoinDataClientConfig { ProductType = KucoinProductType.Futures }));

        // And a host pointing either market somewhere else still gets to.
        Assert.Equal(
            "http://127.0.0.1:9",
            KucoinVenue.HttpBase(new KucoinDataClientConfig { ProductType = KucoinProductType.Futures, BaseUrlHttp = "http://127.0.0.1:9" }));
    }
}
