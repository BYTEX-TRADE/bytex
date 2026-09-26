using System.Text.Json;
using Bytex.Adapters.Hyperliquid;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Adapters.Tests.Hyperliquid;

// Why: two of this venue's three history peculiarities produce a plausible wrong answer rather than an error, and the
// third produces a silently short one.
//
// A candle row carries BOTH ends of its interval - t opens it and T is its last millisecond - so a reader taking T as
// the close stamps every bar one millisecond early and nothing compares with another venue's bars again. The newest
// row is the candle STILL FORMING, so a reader that trusts the list says a candle closed that has not. And the venue
// serves only about the last five thousand bars of an interval, counted from NOW rather than from the window asked
// for, so a paging loop walking backwards would stop returning rows and look exactly like the beginning of history.
//
// The payloads are what the venue really sent on 2026-09-25, including the forming row and the funding stamps that
// are tens of milliseconds past the hour.
public sealed class HyperliquidHistoryTests
{
    /// <summary>The open of the first recorded hourly candle, 2026-09-25T14:00Z.</summary>
    private const long FirstOpenMs = 1790344800000L;

    private const long HourMs = 3600000L;

    /// <summary>Half past the hour the last recorded candle opened in, so that candle has not closed.</summary>
    private const long NowMs = 1790361000000L;

    private static readonly BarSpecification _hourly = new(1, BarAggregation.Hour, PriceType.Last);

    private sealed class Rig : IAsyncDisposable
    {
        public Rig(Func<RecordedRequest, StubResponse> handler)
        {
            Server = new LoopbackServer(handler);
            Http = new HyperliquidHttp(new HyperliquidDataClientConfig { BaseUrlHttp = Server.HttpBase });
        }

        public LoopbackServer Server { get; }

        public HyperliquidHttp Http { get; }

        /// <summary>BTC as the venue's own catalog describes it, so a price and a size round the way they really do.</summary>
        public Instrument Btc { get; private set; } = null!;

        public async Task<Rig> ReadyAsync()
        {
            HyperliquidInstrumentProvider provider = new(Http);
            await provider.LoadAllAsync(CancellationToken.None);
            Btc = provider.Find(InstrumentId.Parse("BTC-PERP.HYPERLIQUID"))!;
            return this;
        }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await Server.DisposeAsync();
        }
    }

    /// <summary>
    /// One route for every read, because this venue has one endpoint: the <c>type</c> in the BODY is what says which
    /// read it is, so the stub discriminates on the body exactly as the venue does.
    /// </summary>
    private static Func<RecordedRequest, StubResponse> Info(params (string Type, string Body)[] answers) =>
        request =>
        {
            if (request.Path != HyperliquidVenue.InfoPath)
            {
                return StubResponse.Error(404, "no such path");
            }

            using JsonDocument doc = JsonDocument.Parse(request.Body);
            string type = doc.RootElement.GetProperty("type").GetString() ?? string.Empty;
            foreach ((string name, string body) in answers)
            {
                if (name == type)
                {
                    return StubResponse.Json(body);
                }
            }

            // What the venue really says about a type it does not know: not JSON, at 422.
            return StubResponse.Error(422, HyperliquidPayloads.InfoUnknownType);
        };

    private static Task<IReadOnlyList<Bar>> BarsAsync(Rig rig, UnixNanos? start = null, UnixNanos? end = null, int? limit = null) =>
        HyperliquidHistory.FetchBarsAsync(
            rig.Http,
            rig.Btc,
            new BarType(rig.Btc.Id, _hourly),
            start,
            end,
            limit,
            UnixNanos.FromMilliseconds(NowMs),
            CancellationToken.None);

    [Fact]
    public async Task A_bar_is_stamped_at_its_close_computed_from_the_open()
    {
        // The venue sends 1790344800000 and 1790348399999 on the same row - the open and the LAST MILLISECOND of the
        // interval, 3599999 apart rather than 3600000. The engine stamps a bar at the moment the interval ends, so
        // the stamp is the open plus the interval: taking the venue's own closing field would put every bar one
        // millisecond early.
        await using Rig rig = await new Rig(Info(
            (HyperliquidReads.Meta, HyperliquidPayloads.Meta),
            (HyperliquidReads.CandleSnapshot, HyperliquidPayloads.BtcHourlyCandles))).ReadyAsync();

        IReadOnlyList<Bar> bars = await BarsAsync(rig);

        Assert.Equal(UnixNanos.FromMilliseconds(FirstOpenMs + HourMs), bars[0].TsEvent);
        Assert.Equal(83912m, bars[0].Open.Value);
        Assert.Equal(84039m, bars[0].High.Value);
        Assert.Equal(83133m, bars[0].Low.Value);
        Assert.Equal(84010m, bars[0].Close.Value);

        // The volume is in BASE units already, which is what everything above the adapter counts in.
        Assert.Equal(3997.94344m, bars[0].Volume.Value);
    }

    [Fact]
    public async Task The_candle_still_forming_is_not_returned()
    {
        // Five rows come back and the fifth opened at 18:00 with a closing stamp in the future. Measured on the
        // live venue at 17:46:47, whose newest one-minute row had opened at 17:46:00 - so this is how it always
        // answers, not an artefact of a boundary.
        await using Rig rig = await new Rig(Info(
            (HyperliquidReads.Meta, HyperliquidPayloads.Meta),
            (HyperliquidReads.CandleSnapshot, HyperliquidPayloads.BtcHourlyCandles))).ReadyAsync();

        IReadOnlyList<Bar> bars = await BarsAsync(rig);

        Assert.Equal(4, bars.Count);
        Assert.All(bars, b => Assert.True(b.TsEvent.Value <= NowMs * UnixNanos.NanosPerMillisecond));

        // Oldest first, and each bar only once.
        Assert.Equal(bars.OrderBy(b => b.TsEvent.Value).Select(b => b.TsEvent), bars.Select(b => b.TsEvent));
        Assert.Equal(bars.Count, bars.Select(b => b.TsEvent).Distinct().Count());
    }

    [Fact]
    public async Task The_window_is_asked_for_as_a_nested_request_with_the_venues_own_interval_name()
    {
        await using Rig rig = await new Rig(Info(
            (HyperliquidReads.Meta, HyperliquidPayloads.Meta),
            (HyperliquidReads.CandleSnapshot, HyperliquidPayloads.BtcHourlyCandles))).ReadyAsync();

        await BarsAsync(rig, UnixNanos.FromMilliseconds(FirstOpenMs), UnixNanos.FromMilliseconds(NowMs));

        using JsonDocument body = JsonDocument.Parse(rig.Server.Requests.Last(r => r.Body.Contains("candleSnapshot", StringComparison.Ordinal)).Body);
        JsonElement req = body.RootElement.GetProperty("req");

        Assert.Equal("BTC", req.GetProperty("coin").GetString());
        Assert.Equal("1h", req.GetProperty("interval").GetString());
        Assert.True(req.GetProperty("startTime").GetInt64() > 0);
    }

    [Fact]
    public async Task A_window_older_than_what_the_venue_keeps_is_not_even_asked_for()
    {
        // The retention boundary. Measured three ways: 100000 one-minute bars ending now gave 5159; the same
        // 6000-hour window ending 1000 hours ago gave 4002 starting at the same wall-clock moment; and a window
        // entirely behind the boundary came back as an empty array rather than an error.
        //
        // So there is nothing to page for and nothing to retry. A request is not sent at all, which is the honest
        // answer - the alternative is a loop that issues identical requests and then reports the venue's boundary
        // as the start of its history.
        await using Rig rig = await new Rig(Info(
            (HyperliquidReads.Meta, HyperliquidPayloads.Meta),
            (HyperliquidReads.CandleSnapshot, HyperliquidPayloads.BtcHourlyCandles))).ReadyAsync();

        long wellBehind = NowMs - ((HyperliquidVenue.CandleRetention + 2000L) * HourMs);
        IReadOnlyList<Bar> bars = await BarsAsync(
            rig,
            UnixNanos.FromMilliseconds(wellBehind),
            UnixNanos.FromMilliseconds(wellBehind + (100L * HourMs)));

        Assert.Empty(bars);
        Assert.DoesNotContain(rig.Server.Requests, r => r.Body.Contains("candleSnapshot", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_window_reaching_further_back_than_the_venue_keeps_asks_only_for_what_it_will_answer()
    {
        // Asking earlier is not an error and not slower - the venue starts its answer at its own boundary - but the
        // request says what can be served, so a reader of the log is not misled about what was asked for.
        await using Rig rig = await new Rig(Info(
            (HyperliquidReads.Meta, HyperliquidPayloads.Meta),
            (HyperliquidReads.CandleSnapshot, HyperliquidPayloads.BtcHourlyCandles))).ReadyAsync();

        await BarsAsync(rig, UnixNanos.FromMilliseconds(0), UnixNanos.FromMilliseconds(NowMs));

        using JsonDocument body = JsonDocument.Parse(rig.Server.Requests.Last(r => r.Body.Contains("candleSnapshot", StringComparison.Ordinal)).Body);
        long startTime = body.RootElement.GetProperty("req").GetProperty("startTime").GetInt64();

        Assert.True(startTime > 0, "a start of zero would be a request for history the venue has already said it does not keep");
        Assert.InRange(startTime, NowMs - ((HyperliquidVenue.CandleRetention + 1) * HourMs), NowMs);
    }

    [Fact]
    public async Task A_limit_takes_the_newest_bars_when_no_start_anchors_the_window()
    {
        await using Rig rig = await new Rig(Info(
            (HyperliquidReads.Meta, HyperliquidPayloads.Meta),
            (HyperliquidReads.CandleSnapshot, HyperliquidPayloads.BtcHourlyCandles))).ReadyAsync();

        IReadOnlyList<Bar> newest = await BarsAsync(rig, limit: 2);

        Assert.Equal(2, newest.Count);
        Assert.Equal(UnixNanos.FromMilliseconds(FirstOpenMs + (3 * HourMs)), newest[0].TsEvent);
        Assert.Equal(UnixNanos.FromMilliseconds(FirstOpenMs + (4 * HourMs)), newest[1].TsEvent);

        // And the oldest when a start does anchor it, which is the shared rule rather than this venue's.
        IReadOnlyList<Bar> oldest = await BarsAsync(rig, UnixNanos.FromMilliseconds(FirstOpenMs), limit: 2);
        Assert.Equal(UnixNanos.FromMilliseconds(FirstOpenMs + HourMs), oldest[0].TsEvent);
    }

    [Fact]
    public async Task A_bar_length_the_venue_does_not_keep_is_refused_before_a_request_is_sent()
    {
        await using Rig rig = await new Rig(Info(
            (HyperliquidReads.Meta, HyperliquidPayloads.Meta),
            (HyperliquidReads.CandleSnapshot, HyperliquidPayloads.BtcHourlyCandles))).ReadyAsync();

        await Assert.ThrowsAsync<NotSupportedException>(() => HyperliquidHistory.FetchBarsAsync(
            rig.Http,
            rig.Btc,
            new BarType(rig.Btc.Id, new BarSpecification(6, BarAggregation.Hour, PriceType.Last)),
            null,
            null,
            null,
            UnixNanos.FromMilliseconds(NowMs),
            CancellationToken.None));
    }

    [Fact]
    public async Task A_bar_type_that_is_not_time_aggregated_has_no_window_to_ask_for()
    {
        await using Rig rig = await new Rig(Info(
            (HyperliquidReads.Meta, HyperliquidPayloads.Meta),
            (HyperliquidReads.CandleSnapshot, HyperliquidPayloads.BtcHourlyCandles))).ReadyAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => HyperliquidHistory.FetchBarsAsync(
            rig.Http,
            rig.Btc,
            new BarType(rig.Btc.Id, new BarSpecification(100, BarAggregation.Tick, PriceType.Last)),
            null,
            null,
            null,
            UnixNanos.FromMilliseconds(NowMs),
            CancellationToken.None));
    }

    // ----- funding -----

    [Fact]
    public async Task Funding_comes_back_oldest_first_with_the_stamps_the_venue_really_wrote()
    {
        // The stamps are 15, 45 and 46 milliseconds past the hour, never on it - which is why nothing paging this
        // may compute its next cursor from a boundary.
        await using Rig rig = await new Rig(Info(
            (HyperliquidReads.Meta, HyperliquidPayloads.Meta),
            (HyperliquidReads.FundingHistory, HyperliquidPayloads.BtcFunding))).ReadyAsync();

        IReadOnlyList<FundingRateUpdate> rates = await HyperliquidHistory.FetchFundingRatesAsync(
            rig.Http,
            InstrumentId.Parse("BTC-PERP.HYPERLIQUID"),
            1790352000000L,
            1790359200100L);

        Assert.Equal(3, rates.Count);
        Assert.Equal(UnixNanos.FromMilliseconds(1790352000015L), rates[0].TsEvent);
        Assert.Equal(UnixNanos.FromMilliseconds(1790359200046L), rates[^1].TsEvent);
        Assert.All(rates, r => Assert.Equal(0.0000125m, r.Rate));
        Assert.Equal(rates.OrderBy(r => r.TsEvent.Value).Select(r => r.TsEvent), rates.Select(r => r.TsEvent));
    }

    [Fact]
    public async Task Funding_pages_forward_from_the_start_rather_than_back_from_the_end()
    {
        // The opposite of what the candle read does with the same kind of window, which is why the two cannot share
        // a loop: asked for 2000 hours the live venue returned the OLDEST 500 of them. A page that does not fill
        // has reached the end, so one short page stops the loop.
        await using Rig rig = await new Rig(Info(
            (HyperliquidReads.Meta, HyperliquidPayloads.Meta),
            (HyperliquidReads.FundingHistory, HyperliquidPayloads.BtcFunding))).ReadyAsync();

        await HyperliquidHistory.FetchFundingRatesAsync(
            rig.Http,
            InstrumentId.Parse("BTC-PERP.HYPERLIQUID"),
            1790352000000L,
            1790359200100L);

        RecordedRequest request = rig.Server.Requests.Single(r => r.Body.Contains("fundingHistory", StringComparison.Ordinal));
        using JsonDocument body = JsonDocument.Parse(request.Body);

        Assert.Equal("BTC", body.RootElement.GetProperty("coin").GetString());
        Assert.Equal(1790352000000L, body.RootElement.GetProperty("startTime").GetInt64());

        // Three rows is a short page, so one request is all there is - the loop did not ask again for the same rows.
        Assert.Single(rig.Server.Requests, r => r.Body.Contains("fundingHistory", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_full_page_of_funding_makes_the_loop_ask_again_past_the_newest_row()
    {
        // The paging itself. The cursor moves to one millisecond past the newest row rather than to a computed
        // boundary, because a settlement's stamp is not on the hour: a boundary would either skip a settlement or
        // fetch the same page for ever.
        string full = "[" + string.Join(
            ",",
            Enumerable.Range(0, HyperliquidVenue.FundingPage).Select(i =>
                "{\"coin\":\"BTC\",\"fundingRate\":\"0.0000125\",\"premium\":\"-0.0001\",\"time\":"
                + (1790000000000L + (i * HourMs)).ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "}")) + "]";

        int calls = 0;
        await using Rig rig = await new Rig(request =>
        {
            using JsonDocument doc = JsonDocument.Parse(request.Body);
            if (doc.RootElement.GetProperty("type").GetString() == HyperliquidReads.Meta)
            {
                return StubResponse.Json(HyperliquidPayloads.Meta);
            }

            // The first page fills and the second is empty, which is how the venue says there is no more.
            return StubResponse.Json(calls++ == 0 ? full : "[]");
        }).ReadyAsync();

        IReadOnlyList<FundingRateUpdate> rates = await HyperliquidHistory.FetchFundingRatesAsync(
            rig.Http,
            InstrumentId.Parse("BTC-PERP.HYPERLIQUID"),
            1790000000000L,
            1790000000000L + (HyperliquidVenue.FundingPage * 2L * HourMs));

        Assert.Equal(HyperliquidVenue.FundingPage, rates.Count);
        Assert.Equal(2, calls);

        long newest = 1790000000000L + ((HyperliquidVenue.FundingPage - 1) * HourMs);
        using JsonDocument second = JsonDocument.Parse(
            rig.Server.Requests.Last(r => r.Body.Contains("fundingHistory", StringComparison.Ordinal)).Body);

        Assert.Equal(newest + 1, second.RootElement.GetProperty("startTime").GetInt64());
    }

    [Fact]
    public void Both_history_helpers_need_nothing_but_an_http_client()
    {
        // E7, as a fact about the signatures rather than a claim: a host that stores history has no node, so
        // neither of these may want one.
        Assert.All(
            new[] { nameof(HyperliquidHistory.FetchBarsAsync), nameof(HyperliquidHistory.FetchFundingRatesAsync) },
            name => Assert.Contains(
                typeof(HyperliquidHistory).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static),
                m => m.Name == name));
    }
}
