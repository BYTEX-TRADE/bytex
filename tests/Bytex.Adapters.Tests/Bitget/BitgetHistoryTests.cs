using System.Globalization;
using System.Text;
using Bytex.Adapters.Bitget;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Adapters.Tests.Bitget;

// Why: this venue has two candle endpoints per market and choosing the wrong one answers successfully with nothing.
// Measured on 2026-09-25, on one-minute BTCUSDT candles: market/candles serves up to a thousand rows and answered a
// window thirty days old in full, a window six months old with an EMPTY LIST, and a window a year old with an empty
// list - each time with code 00000. market/history-candles caps at two hundred and answered all three. So the helper
// uses the smaller endpoint, and the stub below behaves the way that endpoint was measured to behave:
//
//   - CLOSED candles only. Asked with an end inside the current minute it stopped at the previous one.
//   - startTime IGNORED. Asked for a year-old window with both ends it returned two hundred rows ENDING at the end
//     and BEGINNING BEFORE THE START; asked with a start alone it returned the newest rows it had.
//   - endTime bounds a candle's OPEN and excludes it. Asked to end exactly on a minute boundary it stopped at the
//     candle before.
//   - oldest first, two hundred to a page.
//
// And the funding endpoint takes no window at all: it pages by page NUMBER, newest first, and reduces a page size
// larger than a hundred SILENTLY - asked for 200 and for 500 it answered a hundred rows and code 00000 both times.
public sealed class BitgetHistoryTests
{
    /// <summary>A whole minute boundary, so a bar's open and close are exact.</summary>
    private const long FirstOpenMs = 1_700_000_040_000;

    private const long MinuteMs = 60_000;

    /// <summary>Eight hours, which is the settlement interval of most of this venue's contracts.</summary>
    private const long FundingIntervalMs = 8L * 60L * 60L * 1000L;

    private static readonly BarType _barType = BarType.Parse("BTCUSDT-PERP.BITGET-1-MINUTE-LAST-EXTERNAL");
    private static readonly InstrumentId _perp = InstrumentId.Parse("BTCUSDT-PERP.BITGET");

    private static long OpenOf(int i) => FirstOpenMs + (i * MinuteMs);

    private static long FundingAt(int i) => FirstOpenMs + (i * FundingIntervalMs);

    private static Instrument Contract() => new CryptoPerpetual(new InstrumentSpec
    {
        Id = _perp,
        RawSymbol = new Symbol("BTCUSDT"),
        AssetClass = AssetClass.Crypto,
        InstrumentClass = InstrumentClass.Swap,
        QuoteCurrency = Currencies.USDT,
        BaseCurrency = Currencies.BTC,
        SettlementCurrency = Currencies.USDT,
        PricePrecision = 1,
        SizePrecision = 4,
        PriceIncrement = new Price(0.1m, 1),
        SizeIncrement = new Quantity(0.0001m, 4),
    });

    /// <summary>
    /// The derivative history-candles endpoint as it was measured: closed candles only, oldest first, bounded above by
    /// an end that excludes the candle opening on it, a start that is ignored, and at most two hundred rows.
    /// </summary>
    private static StubResponse Candles(RecordedRequest request, int barsAtVenue)
    {
        long? end = request.Query("endTime") is { } e ? long.Parse(e, CultureInfo.InvariantCulture) : null;
        int limit = Math.Min(BitgetVenue.HistoryCandlePage, request.Query("limit") is { } l ? int.Parse(l, CultureInfo.InvariantCulture) : 100);

        // The start is deliberately not read. That is the venue's behaviour and the reason the helper does not send
        // one; a stub that honoured it would hide a fetch that depended on it.
        List<int> page = [.. Enumerable.Range(0, barsAtVenue).Where(i => end is null || OpenOf(i) < end).TakeLast(limit)];
        StringBuilder rows = new();
        foreach (int i in page)
        {
            if (rows.Length > 0)
            {
                rows.Append(',');
            }

            // Seven columns, as the derivative endpoint really answers: open time, open, high, low, close, the volume
            // in base currency, and the turnover in quote currency.
            rows.Append(CultureInfo.InvariantCulture, $"[\"{OpenOf(i)}\",\"{100 + i}\",\"{101 + i}\",\"{99 + i}\",\"{100 + i}\",\"{i + 1}\",\"0\"]");
        }

        return StubResponse.Json(BitgetPayloads.Envelope($"[{rows}]"));
    }

    /// <summary>
    /// The funding endpoint as it was measured: newest first, paged by page number from one, and a page size larger
    /// than a hundred reduced without a word.
    /// </summary>
    private static StubResponse Funding(RecordedRequest request, int settlementsAtVenue)
    {
        int size = Math.Min(BitgetVenue.FundingPage, request.Query("pageSize") is { } s ? int.Parse(s, CultureInfo.InvariantCulture) : 20);
        int page = request.Query("pageNo") is { } p ? int.Parse(p, CultureInfo.InvariantCulture) : 1;
        IEnumerable<int> rows = Enumerable.Range(0, settlementsAtVenue).Reverse().Skip((page - 1) * size).Take(size);
        StringBuilder body = new();
        foreach (int i in rows)
        {
            if (body.Length > 0)
            {
                body.Append(',');
            }

            body.Append(CultureInfo.InvariantCulture, $"{{\"symbol\":\"BTCUSDT\",\"fundingRate\":\"0.{i:D4}\",\"fundingTime\":\"{FundingAt(i)}\"}}");
        }

        return StubResponse.Json(BitgetPayloads.Envelope($"[{body}]"));
    }

    private static LoopbackServer Venue(int barsAtVenue = 0, int settlementsAtVenue = 0) => new(new Routes()
        .On("GET", BitgetHistory.FuturesHistoryCandlesPath, r => Candles(r, barsAtVenue))
        .On("GET", BitgetHistory.SpotHistoryCandlesPath, r => Candles(r, barsAtVenue))
        .On("GET", BitgetHistory.FundingHistoryPath, r => Funding(r, settlementsAtVenue))
        .Handle);

    private static BitgetHttp Http(LoopbackServer server, BitgetProductType type = BitgetProductType.UsdtFutures) =>
        new(new BitgetDataClientConfig { ProductType = type, BaseUrlHttp = server.HttpBase });

    // ----- bars -----

    [Fact]
    public async Task A_bar_is_stamped_at_its_close_and_not_at_its_open()
    {
        // The venue reports a candle by the time it OPENED. Everything above the adapter reads a bar's timestamp as
        // the moment it finished, so a history that kept the venue's stamp would place every bar one interval early.
        await using LoopbackServer server = Venue(barsAtVenue: 3);
        using BitgetHttp http = Http(server);

        IReadOnlyList<Bar> bars = await BitgetHistory.FetchBarsAsync(
            http, Contract(), _barType, null, null, null, UnixNanos.FromMilliseconds(OpenOf(3)));

        Assert.Equal(3, bars.Count);
        Assert.Equal(UnixNanos.FromMilliseconds(OpenOf(0) + MinuteMs), bars[0].TsEvent);
        Assert.Equal(UnixNanos.FromMilliseconds(OpenOf(2) + MinuteMs), bars[^1].TsEvent);
    }

    [Fact]
    public async Task Bars_come_back_oldest_first_with_the_columns_in_the_venues_order()
    {
        await using LoopbackServer server = Venue(barsAtVenue: 3);
        using BitgetHttp http = Http(server);

        IReadOnlyList<Bar> bars = await BitgetHistory.FetchBarsAsync(
            http, Contract(), _barType, null, null, null, UnixNanos.FromMilliseconds(OpenOf(3)));

        // open, high, low, close - and the volume from column five, which is the base-currency volume on both markets
        // even though the spot row has an extra column after it.
        Assert.Equal(100m, bars[0].Open.Value);
        Assert.Equal(101m, bars[0].High.Value);
        Assert.Equal(99m, bars[0].Low.Value);
        Assert.Equal(100m, bars[0].Close.Value);
        Assert.Equal(1m, bars[0].Volume.Value);
        Assert.True(bars[0].TsEvent < bars[^1].TsEvent);
    }

    [Fact]
    public async Task A_window_wider_than_a_page_is_walked_backwards_until_it_is_covered()
    {
        // Two hundred to a page and no way to ask for a start, so the only way to reach the beginning of a window is
        // to walk back from its end. Four hundred and fifty bars is three pages.
        const int atVenue = 450;
        await using LoopbackServer server = Venue(barsAtVenue: atVenue);
        using BitgetHttp http = Http(server);

        IReadOnlyList<Bar> bars = await BitgetHistory.FetchBarsAsync(
            http,
            Contract(),
            _barType,
            UnixNanos.FromMilliseconds(OpenOf(0)),
            UnixNanos.FromMilliseconds(OpenOf(atVenue - 1)),
            null,
            UnixNanos.FromMilliseconds(OpenOf(atVenue)));

        Assert.Equal(atVenue, bars.Count);
        Assert.Equal(UnixNanos.FromMilliseconds(OpenOf(0) + MinuteMs), bars[0].TsEvent);
        Assert.Equal(UnixNanos.FromMilliseconds(OpenOf(atVenue - 1) + MinuteMs), bars[^1].TsEvent);
        Assert.Equal(3, server.RequestsTo(BitgetHistory.FuturesHistoryCandlesPath).Count);
    }

    [Fact]
    public async Task A_start_with_no_limit_is_the_whole_window_and_not_one_page_of_it()
    {
        // The defect this rule exists for. The endpoint answers backwards from its end, so a page cap applied when a
        // start was given would return the LAST two hundred bars of a six-month window and drop the beginning -
        // successfully, which is what made it hard to see.
        const int atVenue = 300;
        await using LoopbackServer server = Venue(barsAtVenue: atVenue);
        using BitgetHttp http = Http(server);

        IReadOnlyList<Bar> bars = await BitgetHistory.FetchBarsAsync(
            http, Contract(), _barType, UnixNanos.FromMilliseconds(OpenOf(0)), null, null, UnixNanos.FromMilliseconds(OpenOf(atVenue)));

        Assert.Equal(atVenue, bars.Count);
    }

    [Fact]
    public async Task A_limit_with_no_start_is_the_newest_bars()
    {
        await using LoopbackServer server = Venue(barsAtVenue: 50);
        using BitgetHttp http = Http(server);

        IReadOnlyList<Bar> bars = await BitgetHistory.FetchBarsAsync(
            http, Contract(), _barType, null, null, 5, UnixNanos.FromMilliseconds(OpenOf(50)));

        Assert.Equal(5, bars.Count);
        Assert.Equal(UnixNanos.FromMilliseconds(OpenOf(45) + MinuteMs), bars[0].TsEvent);
        Assert.Equal(UnixNanos.FromMilliseconds(OpenOf(49) + MinuteMs), bars[^1].TsEvent);
    }

    [Fact]
    public async Task The_end_sent_to_the_venue_is_past_the_last_open_wanted_because_the_venue_excludes_it()
    {
        // Measured: an end exactly on a candle's open excludes that candle. So the bound sent is one millisecond
        // later, or the newest bar of every window would be missing.
        await using LoopbackServer server = Venue(barsAtVenue: 10);
        using BitgetHttp http = Http(server);

        IReadOnlyList<Bar> bars = await BitgetHistory.FetchBarsAsync(
            http,
            Contract(),
            _barType,
            null,
            UnixNanos.FromMilliseconds(OpenOf(4)),
            null,
            UnixNanos.FromMilliseconds(OpenOf(10)));

        Assert.Equal(
            (OpenOf(4) + 1).ToString(CultureInfo.InvariantCulture),
            server.RequestsTo(BitgetHistory.FuturesHistoryCandlesPath)[0].Query("endTime"));

        // And the bar that opened at the end of the window is in the answer.
        Assert.Equal(UnixNanos.FromMilliseconds(OpenOf(4) + MinuteMs), bars[^1].TsEvent);
    }

    [Fact]
    public async Task No_start_is_sent_because_the_venue_ignores_one()
    {
        // Sending a parameter a venue ignores is how a fetch comes to depend on behaviour that was never there. The
        // spot endpoint also refuses the request outright when a start is sent without an end.
        await using LoopbackServer server = Venue(barsAtVenue: 10);
        using BitgetHttp http = Http(server);

        await BitgetHistory.FetchBarsAsync(
            http, Contract(), _barType, UnixNanos.FromMilliseconds(OpenOf(2)), null, null, UnixNanos.FromMilliseconds(OpenOf(10)));

        RecordedRequest asked = server.RequestsTo(BitgetHistory.FuturesHistoryCandlesPath)[0];
        Assert.Null(asked.Query("startTime"));
        Assert.Equal("200", asked.Query("limit"));
        Assert.Equal("1m", asked.Query("granularity"));
        Assert.Equal("USDT-FUTURES", asked.Query("productType"));
        Assert.Equal("BTCUSDT", asked.Query("symbol"));
    }

    [Fact]
    public async Task No_bar_that_has_not_closed_by_now_comes_back()
    {
        // The endpoint was measured never to return the candle being built, and the shared window rule says the same
        // thing from the other side. Both are asserted, because a caller storing history must not be given a bar
        // that says a candle closed when it has not.
        await using LoopbackServer server = Venue(barsAtVenue: 10);
        using BitgetHttp http = Http(server);

        // Now is inside the interval that opened at OpenOf(9), so that bar has not closed.
        UnixNanos now = UnixNanos.FromMilliseconds(OpenOf(9) + 30_000);
        IReadOnlyList<Bar> bars = await BitgetHistory.FetchBarsAsync(http, Contract(), _barType, null, null, null, now);

        Assert.All(bars, b => Assert.True(b.TsEvent <= now));
        Assert.Equal(UnixNanos.FromMilliseconds(OpenOf(8) + MinuteMs), bars[^1].TsEvent);
    }

    [Fact]
    public async Task The_spot_endpoint_is_asked_with_the_spelling_only_it_accepts()
    {
        // A one-minute candle is "1min" here and "1m" on the derivatives, and each endpoint refuses the other's
        // spelling with 400171.
        await using LoopbackServer server = Venue(barsAtVenue: 5);
        using BitgetHttp http = Http(server, BitgetProductType.Spot);
        Instrument pair = new CurrencyPair(new InstrumentSpec
        {
            Id = InstrumentId.Parse("BTCUSDT.BITGET"),
            RawSymbol = new Symbol("BTCUSDT"),
            AssetClass = AssetClass.Crypto,
            InstrumentClass = InstrumentClass.Spot,
            QuoteCurrency = Currencies.USDT,
            BaseCurrency = Currencies.BTC,
            SettlementCurrency = Currencies.USDT,
            PricePrecision = 2,
            SizePrecision = 6,
            PriceIncrement = new Price(0.01m, 2),
            SizeIncrement = new Quantity(0.000001m, 6),
        });

        await BitgetHistory.FetchBarsAsync(
            http,
            pair,
            BarType.Parse("BTCUSDT.BITGET-1-MINUTE-LAST-EXTERNAL"),
            null,
            null,
            null,
            UnixNanos.FromMilliseconds(OpenOf(5)));

        RecordedRequest asked = server.RequestsTo(BitgetHistory.SpotHistoryCandlesPath)[0];
        Assert.Equal("1min", asked.Query("granularity"));

        // And no product type, because the spot endpoints refuse a parameter they do not know.
        Assert.Null(asked.Query("productType"));
    }

    [Fact]
    public async Task The_recorded_payloads_parse_into_the_bars_the_venue_really_sent()
    {
        // The stub above is this adapter's model of the venue; this is the venue's own answer, byte for byte, so the
        // model cannot be wrong about the shape of a row. Five one-minute candles, eight columns on spot and seven on
        // the derivatives, with the same six fields first.
        await using LoopbackServer server = new(new Routes()
            .On("GET", BitgetHistory.FuturesHistoryCandlesPath, BitgetPayloads.FuturesHistoryCandles)
            .Handle);

        using BitgetHttp http = Http(server);
        IReadOnlyList<Bar> bars = await BitgetHistory.FetchBarsAsync(
            http, Contract(), _barType, null, null, null, UnixNanos.FromMilliseconds(1_790_358_960_000));

        Assert.Equal(5, bars.Count);
        Assert.Equal(83733.2m, bars[0].Open.Value);
        Assert.Equal(83709m, bars[0].Close.Value);
        Assert.Equal(0.7569m, bars[0].Volume.Value);

        // Close-stamped: the row said it opened at 1790358660000 and a minute is 60000.
        Assert.Equal(UnixNanos.FromMilliseconds(1_790_358_720_000), bars[0].TsEvent);
    }

    // ----- funding -----

    [Fact]
    public async Task Funding_comes_back_oldest_first_although_the_venue_answers_newest_first()
    {
        await using LoopbackServer server = Venue(settlementsAtVenue: 5);
        using BitgetHttp http = Http(server);

        IReadOnlyList<FundingRateUpdate> rates = await BitgetHistory.FetchFundingRatesAsync(http, _perp, null, null, null);

        Assert.Equal(5, rates.Count);
        Assert.Equal(UnixNanos.FromMilliseconds(FundingAt(0)), rates[0].TsEvent);
        Assert.Equal(UnixNanos.FromMilliseconds(FundingAt(4)), rates[^1].TsEvent);
        Assert.Equal(0.0000m, rates[0].Rate);
        Assert.Equal(0.0004m, rates[^1].Rate);
    }

    [Fact]
    public async Task Funding_is_walked_by_page_number_because_the_venue_takes_no_window()
    {
        // There is no start, no end and no cursor on this endpoint - only a page number - so a window is reached by
        // paging back until the page passes the start asked for.
        await using LoopbackServer server = Venue(settlementsAtVenue: 250);
        using BitgetHttp http = Http(server);

        IReadOnlyList<FundingRateUpdate> rates = await BitgetHistory.FetchFundingRatesAsync(http, _perp, null, null, null);

        Assert.Equal(250, rates.Count);

        // A hundred to a page, so three pages, asked for by number from one.
        Assert.Equal(["1", "2", "3"], server.RequestsTo(BitgetHistory.FundingHistoryPath).Select(r => r.Query("pageNo")!));
        Assert.All(server.RequestsTo(BitgetHistory.FundingHistoryPath), r => Assert.Equal("100", r.Query("pageSize")));
    }

    [Fact]
    public async Task Funding_outside_the_window_asked_for_is_left_out()
    {
        await using LoopbackServer server = Venue(settlementsAtVenue: 20);
        using BitgetHttp http = Http(server);

        IReadOnlyList<FundingRateUpdate> rates = await BitgetHistory.FetchFundingRatesAsync(
            http, _perp, FundingAt(5), FundingAt(8), null);

        Assert.Equal(4, rates.Count);
        Assert.Equal(UnixNanos.FromMilliseconds(FundingAt(5)), rates[0].TsEvent);
        Assert.Equal(UnixNanos.FromMilliseconds(FundingAt(8)), rates[^1].TsEvent);
    }

    [Fact]
    public async Task The_walk_stops_at_the_page_that_reaches_the_start()
    {
        // Paging by number has no natural end, so the walk has to decide for itself when to stop. Once a page holds a
        // settlement at or before the start, there is nothing older to want.
        await using LoopbackServer server = Venue(settlementsAtVenue: 250);
        using BitgetHttp http = Http(server);

        await BitgetHistory.FetchFundingRatesAsync(http, _perp, FundingAt(200), null, null);

        Assert.Single(server.RequestsTo(BitgetHistory.FundingHistoryPath));
    }

    [Fact]
    public async Task The_recorded_funding_payload_parses_into_the_settlements_the_venue_really_sent()
    {
        await using LoopbackServer server = new(new Routes()
            .On("GET", BitgetHistory.FundingHistoryPath, BitgetPayloads.FundingHistory)
            .Handle);

        using BitgetHttp http = Http(server);
        IReadOnlyList<FundingRateUpdate> rates = await BitgetHistory.FetchFundingRatesAsync(http, _perp, null, null, null);

        Assert.Equal(5, rates.Count);

        // Turned round: the venue's first row was the newest, at 1790352000000.
        Assert.Equal(UnixNanos.FromMilliseconds(1_790_236_800_000), rates[0].TsEvent);
        Assert.Equal(UnixNanos.FromMilliseconds(1_790_352_000_000), rates[^1].TsEvent);
        Assert.Equal(0.000078m, rates[^1].Rate);
    }

    [Fact]
    public async Task Funding_cannot_be_asked_of_the_spot_market()
    {
        // Spot pays no funding and the endpoint belongs to the derivative markets, so asking is a caller mistake
        // rather than something to answer with an empty list - an empty list would read as "this pair was never
        // charged anything", which is a different statement.
        await using LoopbackServer server = Venue();
        using BitgetHttp http = Http(server, BitgetProductType.Spot);

        ArgumentException refused = await Assert.ThrowsAsync<ArgumentException>(
            () => BitgetHistory.FetchFundingRatesAsync(http, InstrumentId.Parse("BTCUSDT.BITGET"), null, null, null));

        Assert.Contains("charged no funding", refused.Message, StringComparison.Ordinal);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task A_window_given_as_dates_reads_the_same_as_one_given_in_milliseconds()
    {
        // The overload a catalog download calls, which is the whole point of E7: no node, no clock, an http client
        // and an instrument.
        await using LoopbackServer server = Venue(settlementsAtVenue: 20);
        using BitgetHttp http = Http(server);

        IReadOnlyList<FundingRateUpdate> rates = await BitgetHistory.FetchFundingRatesAsync(
            http,
            _perp,
            DateTimeOffset.FromUnixTimeMilliseconds(FundingAt(5)),
            DateTimeOffset.FromUnixTimeMilliseconds(FundingAt(8)));

        Assert.Equal(4, rates.Count);
    }

    [Fact]
    public async Task Bars_can_be_fetched_with_dates_and_no_node_either()
    {
        await using LoopbackServer server = Venue(barsAtVenue: 10);
        using BitgetHttp http = Http(server);

        IReadOnlyList<Bar> bars = await BitgetHistory.FetchBarsAsync(
            http,
            Contract(),
            _barType,
            DateTimeOffset.FromUnixTimeMilliseconds(OpenOf(0)),
            DateTimeOffset.FromUnixTimeMilliseconds(OpenOf(4)));

        Assert.Equal(5, bars.Count);
    }
}
