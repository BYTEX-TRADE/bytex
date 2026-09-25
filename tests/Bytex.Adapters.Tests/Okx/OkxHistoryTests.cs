using System.Globalization;
using Bytex.Adapters.Okx;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Adapters.Tests.Okx;

// Why: this venue has TWO candle endpoints and only one of them is safe to page. Measured on 2026-09-25:
//
//   /api/v5/market/candles          asked for a window two days old -> HTTP 200, code 0, EMPTY array
//   /api/v5/market/history-candles  asked for the same window       -> 300 rows; and for 200 days old, 300 rows
//
// So a paging loop walking backwards through the first endpoint reads "no more history" two days in and stops,
// successfully, having returned a fraction of the window asked for. The second endpoint also reaches forward to the
// candle still forming, so one endpoint covers the whole range and there is no seam to get wrong.
//
// Three more things are pinned here, each measured and each the kind that produces plausible wrong numbers rather
// than an error:
//
//   - the stamp on a row is the candle's OPEN, in milliseconds, and the row's last field is the venue saying
//     whether the candle has closed. Both endpoints include the one still forming.
//   - the volume field is base currency on spot and a NUMBER OF CONTRACTS on a derivative, with the base figure in
//     the field beside it. One minute of BTC-USDT-SWAP: 3498.64 contracts against 34.9864 BTC.
//   - the venue writes the quiet intervals itself. Three of the least traded pairs each returned sixty consecutive
//     one-minute rows with zero volume and no gap, so nothing here fills gaps - unlike the two adapters whose
//     venues leave them out.
public sealed class OkxHistoryTests
{
    private const string CandlesPath = "/api/v5/market/history-candles";
    private const string RecentCandlesPath = "/api/v5/market/candles";
    private const string FundingPath = "/api/v5/public/funding-rate-history";

    /// <summary>The test clock: 2023-11-14T22:13:20Z, which falls inside the minute opening at 1699999980000.</summary>
    private static readonly UnixNanos _now = TestKernel.Now;

    private static readonly BarType _spotBars = BarType.Parse("BTC-USDT.OKX-1-MINUTE-LAST-EXTERNAL");
    private static readonly BarType _swapBars = BarType.Parse("BTC-USDT-SWAP.OKX-1-MINUTE-LAST-EXTERNAL");

    private static OkxHttp Http(LoopbackServer server, OkxInstrumentType type) =>
        new(new OkxDataClientConfig { InstrumentType = type, BaseUrlHttp = server.HttpBase });

    private static Instrument Pair() => new CurrencyPair(new InstrumentSpec
    {
        Id = InstrumentId.Parse("BTC-USDT.OKX"),
        RawSymbol = new Symbol("BTC-USDT"),
        AssetClass = Core.Model.AssetClass.Crypto,
        InstrumentClass = Core.Model.InstrumentClass.Spot,
        QuoteCurrency = Currencies.USDT,
        BaseCurrency = Currencies.BTC,
        SettlementCurrency = Currencies.USDT,
        PricePrecision = 1,
        SizePrecision = 8,
        PriceIncrement = new Price(0.1m, 1),
        SizeIncrement = new Quantity(0.00000001m, 8),
    });

    private static Instrument Perpetual() => new CryptoPerpetual(new InstrumentSpec
    {
        Id = InstrumentId.Parse("BTC-USDT-SWAP.OKX"),
        RawSymbol = new Symbol("BTC-USDT-SWAP"),
        AssetClass = Core.Model.AssetClass.Crypto,
        InstrumentClass = Core.Model.InstrumentClass.Swap,
        QuoteCurrency = Currencies.USDT,
        BaseCurrency = Currencies.BTC,
        SettlementCurrency = Currencies.USDT,
        PricePrecision = 1,
        SizePrecision = 4,
        PriceIncrement = new Price(0.1m, 1),
        SizeIncrement = new Quantity(0.0001m, 4),
        Info = new Dictionary<string, string>(StringComparer.Ordinal) { ["contractValue"] = "0.01" },
    });

    // ----- which endpoint -----

    [Fact]
    public async Task History_comes_from_the_endpoint_that_keeps_history()
    {
        // The route that would answer an empty page for anything old is stubbed to fail the test loudly if it is
        // called at all. Nothing must reach it: the whole reason for choosing one endpoint is that the other's
        // "no more history" and its "nothing here" are the same answer.
        await using LoopbackServer server = new(new Routes()
            .On("GET", CandlesPath, OkxPayloads.SpotCandles)
            .On("GET", RecentCandlesPath, _ => new StubResponse(500, "the recent-candles endpoint must not be paged"))
            .Handle);

        IReadOnlyList<Bar> bars = await OkxHistory.FetchBarsAsync(
            Http(server, OkxInstrumentType.Spot), Pair(), _spotBars, null, null, null, _now);

        Assert.NotEmpty(bars);
        Assert.Empty(server.RequestsTo(RecentCandlesPath));
    }

    // ----- a bar is close-stamped, and only a closed one is returned -----

    [Fact]
    public async Task Bars_are_close_stamped_oldest_first_and_the_forming_candle_is_left_out()
    {
        // The fixture's newest row opens at 1699999980000 and is marked unconfirmed, which is the minute the test
        // clock is inside. A history that included it would say a candle closed when it has not.
        await using LoopbackServer server = new(new Routes().On("GET", CandlesPath, OkxPayloads.SpotCandles).Handle);

        IReadOnlyList<Bar> bars = await OkxHistory.FetchBarsAsync(
            Http(server, OkxInstrumentType.Spot), Pair(), _spotBars, null, null, null, _now);

        Assert.Equal(3, bars.Count);
        Assert.Equal(
            [1_699_999_860_000L, 1_699_999_920_000L, 1_699_999_980_000L],
            bars.Select(b => b.TsEvent.ToMilliseconds()).ToArray());

        // And the row's four prices are where the venue puts them: [ts, open, HIGH, LOW, close, ...]. Any
        // permutation of the middle two would still give four real prices and no error.
        Bar oldest = bars[0];
        Assert.Equal(100m, oldest.Open.Value);
        Assert.Equal(102m, oldest.High.Value);
        Assert.Equal(99m, oldest.Low.Value);
        Assert.Equal(101m, oldest.Close.Value);
    }

    [Fact]
    public async Task A_row_the_venue_has_not_confirmed_is_left_out_even_when_its_interval_has_passed()
    {
        // The venue states this itself, which no other venue here does, so whether a bar is finished is its answer
        // rather than a comparison against a clock. A row that has passed but is still marked unconfirmed is the
        // venue saying it may yet change.
        string unconfirmed = """
            {"code":"0","msg":"","data":[
              ["1699999920000","103","108","102","104","3","312","312","0"]
            ]}
            """;

        await using LoopbackServer server = new(new Routes().On("GET", CandlesPath, unconfirmed).Handle);

        IReadOnlyList<Bar> bars = await OkxHistory.FetchBarsAsync(
            Http(server, OkxInstrumentType.Spot), Pair(), _spotBars, null, null, null, _now);

        Assert.Empty(bars);
    }

    // ----- volume -----

    [Fact]
    public async Task A_spot_bars_volume_is_the_field_that_holds_base_currency_on_spot()
    {
        await using LoopbackServer server = new(new Routes().On("GET", CandlesPath, OkxPayloads.SpotCandles).Handle);

        IReadOnlyList<Bar> bars = await OkxHistory.FetchBarsAsync(
            Http(server, OkxInstrumentType.Spot), Pair(), _spotBars, null, null, null, _now);

        Assert.Equal([1m, 2m, 3m], bars.Select(b => b.Volume.Value).ToArray());
    }

    [Fact]
    public async Task A_derivative_bars_volume_is_base_currency_and_not_the_contract_count_beside_it()
    {
        // The measured difference: on a swap the venue's volume field is a number of contracts and the base-currency
        // figure is the next field along. Reading the contract count would report a hundred times the volume on
        // every derivative bar of this instrument, with nothing anywhere to say so - which is why the fixture's two
        // numbers differ by exactly the contract value.
        await using LoopbackServer server = new(new Routes().On("GET", CandlesPath, OkxPayloads.SwapCandles).Handle);

        IReadOnlyList<Bar> bars = await OkxHistory.FetchBarsAsync(
            Http(server, OkxInstrumentType.Swap), Perpetual(), _swapBars, null, null, null, _now);

        Assert.Equal([1m, 2m, 3m], bars.Select(b => b.Volume.Value).ToArray());
        Assert.DoesNotContain(300m, bars.Select(b => b.Volume.Value));
    }

    // ----- the window and the paging -----

    [Fact]
    public async Task A_request_asks_for_the_window_the_caller_named_on_both_sides()
    {
        // `after` is the venue's "strictly before this open" and `before` is its "strictly after this open", which
        // was measured: the two together bounded a two-hour window at 120 one-minute rows. A bar belongs to a window
        // when it CLOSES inside it, so the far bound is one interval below the start.
        await using LoopbackServer server = new(new Routes().On("GET", CandlesPath, OkxPayloads.SpotCandles).Handle);

        UnixNanos start = UnixNanos.FromMilliseconds(1_699_999_860_000L);
        UnixNanos end = UnixNanos.FromMilliseconds(1_699_999_980_000L);

        await OkxHistory.FetchBarsAsync(Http(server, OkxInstrumentType.Spot), Pair(), _spotBars, start, end, null, _now);

        RecordedRequest asked = server.RequestsTo(CandlesPath)[0];
        Assert.Equal("BTC-USDT", asked.Query("instId"));
        Assert.Equal("1m", asked.Query("bar"));
        Assert.Equal("300", asked.Query("limit"));
        Assert.Equal((1_699_999_980_000L + 1).ToString(CultureInfo.InvariantCulture), asked.Query("after"));
        Assert.Equal((1_699_999_860_000L - 60_000L - 1).ToString(CultureInfo.InvariantCulture), asked.Query("before"));
    }

    [Fact]
    public async Task A_window_reaching_into_the_future_is_bounded_by_now_rather_than_by_the_end()
    {
        // A venue asked for a window reaching past now answers with the candle it is building. The end is clamped
        // here, so the request never asks for it in the first place.
        await using LoopbackServer server = new(new Routes().On("GET", CandlesPath, OkxPayloads.SpotCandles).Handle);

        await OkxHistory.FetchBarsAsync(
            Http(server, OkxInstrumentType.Spot),
            Pair(),
            _spotBars,
            null,
            UnixNanos.FromMilliseconds(_now.ToMilliseconds() + 3_600_000L),
            null,
            _now);

        Assert.Equal(
            (_now.ToMilliseconds() + 1).ToString(CultureInfo.InvariantCulture),
            server.RequestsTo(CandlesPath)[0].Query("after"));
    }

    [Fact]
    public async Task A_full_page_means_there_is_more_behind_it_and_a_short_page_means_there_is_not()
    {
        // The paging rule, and the one that matters: the venue caps a page at 300 and this loop reads the page COUNT
        // rather than how many bars it kept - a page can be full of rows outside the window and still mean "there is
        // more". A loop that counted kept bars would stop early on a quiet instrument.
        long newest = 1_699_999_980_000L;
        List<string> cursors = [];
        await using LoopbackServer server = new(new Routes()
            .On("GET", CandlesPath, r =>
            {
                cursors.Add(r.Query("after")!);
                long after = long.Parse(r.Query("after")!, CultureInfo.InvariantCulture);

                // A venue serving the page strictly below the cursor, as this one does: the newest whole minute that
                // opened before the cursor. The first answer fills and the second does not, which is what tells the
                // loop it has reached the end.
                long newestInPage = Math.Min(newest, (after - 1) / 60_000L * 60_000L);
                return StubResponse.Json(cursors.Count == 1
                    ? OkxPayloads.CandlePage(newestInPage, 300)
                    : OkxPayloads.CandlePage(newestInPage, 10));
            })
            .Handle);

        IReadOnlyList<Bar> bars = await OkxHistory.FetchBarsAsync(
            Http(server, OkxInstrumentType.Spot),
            Pair(),
            _spotBars,
            UnixNanos.FromMilliseconds(newest - (400L * 60_000L)),
            null,
            null,
            _now);

        // Two requests: a full page, then a short one that ends the walk.
        Assert.Equal(2, cursors.Count);

        // And the second asked from the oldest row of the first, not from where the first started.
        Assert.Equal((newest - (299L * 60_000L)).ToString(CultureInfo.InvariantCulture), cursors[1]);

        // 310 rows came back and 309 bars leave here. This stub marks every row confirmed, including the minute the
        // test clock is inside, so the one that has not closed by `now` is dropped by the shared window rule - which
        // is the backstop behind the venue's own confirmation flag, and worth seeing work.
        Assert.Equal(309, bars.Count);
        Assert.Equal(bars.Select(b => b.TsEvent).Distinct().Count(), bars.Count);
    }

    [Fact]
    public async Task A_venue_that_ignores_the_cursor_ends_the_walk_rather_than_hanging_the_caller()
    {
        // Found by writing the test above wrongly, which is the best way this could have been found: a stub that
        // answered a full page ignoring `after` left the cursor where it was, the same page came back, and the fetch
        // never returned. A history download would have hung with no error and no progress.
        //
        // It is a real shape and not only a broken stub - it is what a venue ignoring a paging bound looks like from
        // here, and one venue in this engine already ignores a filter it documents. So the loop stops when a page
        // does not move the cursor backwards and returns what it has.
        long newest = 1_699_999_980_000L;
        int requests = 0;
        await using LoopbackServer server = new(new Routes()
            .On("GET", CandlesPath, _ =>
            {
                requests++;
                return StubResponse.Json(OkxPayloads.CandlePage(newest, 300));
            })
            .Handle);

        IReadOnlyList<Bar> bars = await OkxHistory.FetchBarsAsync(
            Http(server, OkxInstrumentType.Spot),
            Pair(),
            _spotBars,
            UnixNanos.FromMilliseconds(newest - (10_000L * 60_000L)),
            null,
            null,
            _now).WaitAsync(Wait.Timeout);

        Assert.Equal(2, requests);
        Assert.NotEmpty(bars);
    }

    [Fact]
    public async Task A_page_the_venue_answers_empty_ends_the_walk()
    {
        await using LoopbackServer server = new(new Routes().On("GET", CandlesPath, OkxPayloads.NoCandles).Handle);

        IReadOnlyList<Bar> bars = await OkxHistory.FetchBarsAsync(
            Http(server, OkxInstrumentType.Spot),
            Pair(),
            _spotBars,
            UnixNanos.FromMilliseconds(1_600_000_000_000L),
            null,
            null,
            _now);

        Assert.Empty(bars);
        Assert.Single(server.RequestsTo(CandlesPath));
    }

    [Fact]
    public async Task Nothing_here_fills_a_quiet_interval()
    {
        // Measured, and the opposite of what two of this engine's adapters have to do: the venue writes the quiet
        // minute itself, as a zero-volume row at the previous close. Three of its least traded pairs each returned
        // sixty consecutive such rows with no gap. Filling here would be inventing bars beside real ones.
        string withAZeroVolumeRow = """
            {"code":"0","msg":"","data":[
              ["1699999920000","104","104","104","104","0","0","0","1"],
              ["1699999860000","101","105","100","104","2","206","206","1"]
            ]}
            """;

        await using LoopbackServer server = new(new Routes().On("GET", CandlesPath, withAZeroVolumeRow).Handle);

        IReadOnlyList<Bar> bars = await OkxHistory.FetchBarsAsync(
            Http(server, OkxInstrumentType.Spot), Pair(), _spotBars, null, null, null, _now);

        // Exactly the two rows the venue sent, and the quiet one kept as the venue wrote it.
        Assert.Equal(2, bars.Count);
        Assert.Equal(0m, bars[^1].Volume.Value);
        Assert.Equal(104m, bars[^1].Open.Value);
    }

    [Fact]
    public async Task A_bar_length_the_venue_does_not_keep_is_refused_before_a_request_is_made()
    {
        await using LoopbackServer server = new(new Routes().On("GET", CandlesPath, OkxPayloads.SpotCandles).Handle);
        BarType eightHours = BarType.Parse("BTC-USDT.OKX-8-HOUR-LAST-EXTERNAL");

        await Assert.ThrowsAsync<NotSupportedException>(
            () => OkxHistory.FetchBarsAsync(Http(server, OkxInstrumentType.Spot), Pair(), eightHours, null, null, null, _now));

        Assert.Empty(server.RequestsTo(CandlesPath));
    }

    // ----- funding -----

    [Fact]
    public async Task Funding_settlements_come_back_oldest_first_and_carry_what_was_charged()
    {
        // The venue answers newest first; everything that stores history expects oldest first, and so does every
        // other venue here. And it publishes two rates per settlement - what it predicted and what it charged - so
        // the charged one is taken: a backtest priced on a prediction is right until the two disagree.
        await using LoopbackServer server = new(new Routes().On("GET", FundingPath, OkxPayloads.FundingHistory).Handle);

        IReadOnlyList<FundingRateUpdate> rates = await OkxHistory.FetchFundingRatesAsync(
            Http(server, OkxInstrumentType.Swap),
            InstrumentId.Parse("BTC-USDT-SWAP.OKX"),
            UnixNanos.FromMilliseconds(1_699_900_000_000L).ToDateTimeOffset(),
            UnixNanos.FromMilliseconds(1_700_000_000_000L).ToDateTimeOffset());

        Assert.Equal(3, rates.Count);
        Assert.Equal(
            [1_699_941_600_000L, 1_699_970_400_000L, 1_699_999_200_000L],
            rates.Select(r => r.TsEvent.ToMilliseconds()).ToArray());

        // The realised rates, not the predicted ones that sit beside them in the same rows.
        Assert.Equal([0.0002m, 0.0004m, 0.0006m], rates.Select(r => r.Rate).ToArray());
    }

    [Fact]
    public async Task A_funding_request_walks_backwards_from_the_end_of_the_window()
    {
        // Measured: this endpoint's `after` means settlements OLDER than the timestamp, which is the opposite of
        // what the word suggests and the same as it means on the candle endpoint.
        await using LoopbackServer server = new(new Routes().On("GET", FundingPath, OkxPayloads.FundingHistory).Handle);

        await OkxHistory.FetchFundingRatesAsync(
            Http(server, OkxInstrumentType.Swap),
            InstrumentId.Parse("BTC-USDT-SWAP.OKX"),
            UnixNanos.FromMilliseconds(1_699_900_000_000L).ToDateTimeOffset(),
            UnixNanos.FromMilliseconds(1_700_000_000_000L).ToDateTimeOffset());

        RecordedRequest asked = Assert.Single(server.RequestsTo(FundingPath));
        Assert.Equal("BTC-USDT-SWAP", asked.Query("instId"));
        Assert.Equal("200", asked.Query("limit"));
        Assert.Equal((1_700_000_000_000L + 1).ToString(CultureInfo.InvariantCulture), asked.Query("after"));
    }

    [Fact]
    public async Task A_settlement_older_than_the_window_is_dropped_rather_than_returned()
    {
        await using LoopbackServer server = new(new Routes().On("GET", FundingPath, OkxPayloads.FundingHistory).Handle);

        IReadOnlyList<FundingRateUpdate> rates = await OkxHistory.FetchFundingRatesAsync(
            Http(server, OkxInstrumentType.Swap),
            InstrumentId.Parse("BTC-USDT-SWAP.OKX"),
            UnixNanos.FromMilliseconds(1_699_970_400_000L).ToDateTimeOffset(),
            UnixNanos.FromMilliseconds(1_700_000_000_000L).ToDateTimeOffset());

        Assert.Equal([1_699_970_400_000L, 1_699_999_200_000L], rates.Select(r => r.TsEvent.ToMilliseconds()).ToArray());
    }

    [Fact]
    public async Task A_page_shorter_than_the_cap_ends_the_funding_walk()
    {
        // Three rows against a cap of 200 is the end of what the venue holds in the window, so the loop stops rather
        // than asking again from the oldest settlement for ever.
        await using LoopbackServer server = new(new Routes().On("GET", FundingPath, OkxPayloads.FundingHistory).Handle);

        await OkxHistory.FetchFundingRatesAsync(
            Http(server, OkxInstrumentType.Swap),
            InstrumentId.Parse("BTC-USDT-SWAP.OKX"),
            UnixNanos.FromMilliseconds(1L).ToDateTimeOffset());

        Assert.Single(server.RequestsTo(FundingPath));
    }
}
