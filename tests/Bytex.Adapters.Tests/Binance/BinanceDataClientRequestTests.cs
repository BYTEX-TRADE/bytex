using System.Globalization;
using System.Text;
using Bytex.Adapters.Binance;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Adapters.Tests.Binance;

// Why: a backtest or an indicator warm-up is only as good as the history it is fed. Pagination must return
// every bar of the requested window exactly once and in time order. The stub below answers /klines the way the
// Binance documentation describes: at most `limit` rows, the FIRST rows from startTime when startTime is given,
// otherwise the most recent rows up to endTime.
public sealed class BinanceDataClientRequestTests
{
    private const long FirstOpenMs = 1_690_000_020_000; // a whole minute
    private const long MinuteMs = 60_000;
    private const long EightHoursMs = 8L * 60L * 60L * 1000L;
    private static readonly BarType _barType = BarType.Parse("BTCUSDT.BINANCE-1-MINUTE-LAST-EXTERNAL");

    private sealed class Rig : IAsyncDisposable
    {
        public Rig(int barsAtVenue, BinanceDataClientConfig? config = null, int fundingAtVenue = 0)
        {
            Routes routes = new Routes()
                .On("GET", "/api/v3/exchangeInfo", BinancePayloads.SpotExchangeInfo)
                .On("GET", "/fapi/v1/exchangeInfo", BinancePayloads.SpotExchangeInfo)
                .On("GET", "/fapi/v1/fundingRate", r => Funding(r, fundingAtVenue))
                .On("GET", "/api/v3/klines", r => Klines(r, barsAtVenue))
                .On("GET", "/api/v3/aggTrades", """
                    [
                      {"a":26129,"p":"25000.01","q":"4.70443515","f":27781,"l":27781,"T":1498793709153,"m":true,"M":true},
                      {"a":26130,"p":"25000.02","q":"0.10000000","f":27782,"l":27783,"T":1498793709253,"m":false,"M":true}
                    ]
                    """)
                .On("GET", "/api/v3/trades", """
                    [
                      {"id":28457,"price":"25000.05","qty":"12.00000000","quoteQty":"300000.60","time":1499865549590,"isBuyerMaker":true,"isBestMatch":true}
                    ]
                    """);
            Server = new LoopbackServer(routes.Handle);
            Kernel = new TestKernel();
            Client = new BinanceDataClient(new ClientId("BINANCE"), (config ?? new BinanceDataClientConfig()) with { BaseUrlHttp = Server.HttpBase, BaseUrlWs = Server.WsBase }, Kernel.Services);
            Client.AttachSink(Sink);
        }

        public LoopbackServer Server { get; }

        public TestKernel Kernel { get; }

        public BinanceDataClient Client { get; }

        public RecordingDataSink Sink { get; } = new();

        public async Task<DataResponse> RequestAsync(RequestCommand command)
        {
            await Client.Instruments.LoadAllAsync(CancellationToken.None);
            await Client.RequestAsync(command, CancellationToken.None).WaitAsync(Wait.Timeout);
            return await Sink.NextResponseAsync();
        }

        /// <summary>
        /// Binance semantics for funding history, measured on the live venue on 2026-09-23: rows inside
        /// [startTime, endTime], oldest first, at most a thousand to a page. The rate of a row is its index, so a
        /// test can say which rows it got.
        /// </summary>
        private static StubResponse Funding(RecordedRequest request, int fundingAtVenue)
        {
            long? start = request.Query("startTime") is { } s ? long.Parse(s, CultureInfo.InvariantCulture) : null;
            long? end = request.Query("endTime") is { } e ? long.Parse(e, CultureInfo.InvariantCulture) : null;
            int limit = Math.Min(1000, request.Query("limit") is { } l ? int.Parse(l, CultureInfo.InvariantCulture) : 100);
            IEnumerable<int> page = Enumerable.Range(0, fundingAtVenue)
                .Where(i => (start is null || FundingAt(i) >= start) && (end is null || FundingAt(i) <= end))
                .Take(limit);
            StringBuilder rows = new();
            foreach (int i in page)
            {
                if (rows.Length > 0)
                {
                    rows.Append(',');
                }

                rows.Append(CultureInfo.InvariantCulture, $"{{\"symbol\":\"BTCUSDT\",\"fundingTime\":{FundingAt(i)},\"fundingRate\":\"0.000{i:D4}\",\"markPrice\":\"85324.70000000\"}}");
            }

            return StubResponse.Json($"[{rows}]");
        }

        private static StubResponse Klines(RecordedRequest request, int barsAtVenue)
        {
            long? start = request.Query("startTime") is { } s ? long.Parse(s, CultureInfo.InvariantCulture) : null;
            long? end = request.Query("endTime") is { } e ? long.Parse(e, CultureInfo.InvariantCulture) : null;
            int limit = Math.Min(1000, request.Query("limit") is { } l ? int.Parse(l, CultureInfo.InvariantCulture) : 500);
            List<int> matching = Enumerable.Range(0, barsAtVenue).Where(i => (start is null || OpenOf(i) >= start) && (end is null || OpenOf(i) <= end)).ToList();
            IEnumerable<int> page = start is null ? matching.TakeLast(limit) : matching.Take(limit);
            StringBuilder json = new("[");
            foreach (int i in page)
            {
                if (json.Length > 1)
                {
                    json.Append(',');
                }

                long open = OpenOf(i);
                json.Append(CultureInfo.InvariantCulture, $"[{open},\"{10_000 + i}.00\",\"{10_000 + i}.75\",\"{10_000 + i - 1}.25\",\"{10_000 + i}.50\",\"{i + 1}.00000\",{open + MinuteMs - 1},\"0\",10,\"0\",\"0\",\"0\"]");
            }

            return StubResponse.Json(json.Append(']').ToString());
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            Kernel.Dispose();
            await Server.DisposeAsync();
        }
    }

    private static long OpenOf(int index) => FirstOpenMs + (index * MinuteMs);

    /// <summary>When the funding of a given index was charged: every eight hours, as a perpetual charges it.</summary>
    private static long FundingAt(int index) => FirstOpenMs + (index * EightHoursMs);

    [Fact]
    public async Task The_funding_helper_needs_no_client_and_gives_what_the_client_gives()
    {
        // What a catalog download calls: no node, no bus, no data client, and the same paging as the client's answer.
        await using Rig rig = new(0, new BinanceDataClientConfig { AccountType = BinanceAccountType.UsdMFutures }, fundingAtVenue: 2_500);
        using BinanceHttp http = new(new BinanceDataClientConfig { AccountType = BinanceAccountType.UsdMFutures, BaseUrlHttp = rig.Server.HttpBase });

        IReadOnlyList<FundingRateUpdate> rates = await BinanceHistory.FetchFundingRatesAsync(http, InstrumentId.Parse("BTCUSDT.BINANCE"), DateTimeOffset.UnixEpoch);

        Assert.Equal(2_500, rates.Count);
        Assert.Equal(rates.OrderBy(r => r.TsEvent.Value).Select(r => r.TsEvent), rates.Select(r => r.TsEvent));
        DataResponse throughTheClient = await rig.RequestAsync(Commands.RequestFundingRates(InstrumentId.Parse("BTCUSDT.BINANCE"), null, null, null));
        Assert.Equal(throughTheClient.Data.Cast<FundingRateUpdate>().Select(r => r.Rate), rates.Select(r => r.Rate));
    }

    [Fact]
    public async Task Funding_history_comes_back_oldest_first_from_the_futures_endpoint()
    {
        // R8.15 needs the rates before a perpetual backtest can be charged for holding one. Binance answers oldest
        // first, which is the order history is stored and replayed in.
        await using Rig rig = new(0, new BinanceDataClientConfig { AccountType = BinanceAccountType.UsdMFutures }, fundingAtVenue: 4);

        DataResponse response = await rig.RequestAsync(Commands.RequestFundingRates(InstrumentId.Parse("BTCUSDT.BINANCE"), null, null, null));

        Assert.Equal([0.0000000m, 0.0000001m, 0.0000002m, 0.0000003m], response.Data.Cast<FundingRateUpdate>().Select(f => f.Rate));
        Assert.Equal(
            Enumerable.Range(0, 4).Select(i => UnixNanos.FromMilliseconds(FundingAt(i))),
            response.Data.Cast<FundingRateUpdate>().Select(f => f.TsEvent));
        Assert.Equal("1000", Assert.Single(rig.Server.RequestsTo("/fapi/v1/fundingRate")).Query("limit"));
    }

    [Fact]
    public async Task Funding_history_longer_than_a_page_is_walked_forwards_and_comes_back_whole()
    {
        await using Rig rig = new(0, new BinanceDataClientConfig { AccountType = BinanceAccountType.UsdMFutures }, fundingAtVenue: 2_500);

        DataResponse response = await rig.RequestAsync(Commands.RequestFundingRates(InstrumentId.Parse("BTCUSDT.BINANCE"), null, null, null));

        Assert.Equal(2_500, response.Data.Count);
        Assert.Equal(2_500, response.Data.Cast<FundingRateUpdate>().Select(f => f.TsEvent).Distinct().Count());
        Assert.Equal(3, rig.Server.RequestsTo("/fapi/v1/fundingRate").Count);
    }

    [Fact]
    public async Task A_spot_client_has_no_funding_to_give()
    {
        // Spot pays no funding, and the endpoint is not on the spot host: answering with an empty response says so
        // without asking the venue a question it has no answer to.
        await using Rig rig = new(0, fundingAtVenue: 4);

        DataResponse response = await rig.RequestAsync(Commands.RequestFundingRates(InstrumentId.Parse("BTCUSDT.BINANCE"), null, null, null));

        Assert.Empty(response.Data);
        Assert.Empty(rig.Server.RequestsTo("/fapi/v1/fundingRate"));
        Assert.Empty(rig.Server.RequestsTo("/api/v3/fundingRate"));
    }

    private static UnixNanos CloseOf(int index) => UnixNanos.FromMilliseconds(OpenOf(index) + MinuteMs);

    private static int IndexOf(Bar bar) => (int)(bar.Open.Value - 10_000m);

    [Fact]
    public async Task The_start_and_end_helper_gives_the_whole_window_and_not_one_page_of_it()
    {
        // The overload that says "bars between these two times" passed no limit, and no limit meant one page. So a
        // window of 1,080 hourly bars came back as 1,000 and whatever stored it stored a shorter history - a
        // truncation nothing complains about, because a backtest on less data simply covers less time.
        //
        // Both tests above pass a limit, which is why neither could have caught this: the no-limit path the
        // convenience overload actually takes was never run.
        await using Rig rig = new(barsAtVenue: 2_500);
        await rig.Client.Instruments.LoadAllAsync(CancellationToken.None);
        using BinanceHttp http = new(new BinanceDataClientConfig { BaseUrlHttp = rig.Server.HttpBase });

        IReadOnlyList<Bar> bars = await BinanceHistory.FetchBarsAsync(
            http,
            rig.Client.Instruments.Find(_barType.InstrumentId)!,
            _barType,
            UnixNanos.FromMilliseconds(OpenOf(0)).ToDateTimeOffset(),
            UnixNanos.FromMilliseconds(OpenOf(2_499)).ToDateTimeOffset());

        Assert.Equal(2_500, bars.Count);
        Assert.Equal(bars.OrderBy(b => b.TsInit.Value).Select(b => b.TsInit), bars.Select(b => b.TsInit));
    }

    [Fact]
    public async Task The_bar_helper_needs_no_client_and_gives_what_the_client_gives()
    {
        // E7 of the venue checklist, and the reason for it: a host that stores history has no node, so if the paging
        // is private to the data client it gets written a second time somewhere else - and the second copy stops
        // matching this one the day the venue changes a page size. It was written twice, until this.
        await using Rig rig = new(barsAtVenue: 2_500);
        DataResponse throughTheClient = await rig.RequestAsync(Commands.RequestBars(_barType, null, UnixNanos.FromMilliseconds(OpenOf(2_500)), 1_800));
        Instrument instrument = rig.Client.Instruments.Find(_barType.InstrumentId)!;

        using BinanceHttp http = new(new BinanceDataClientConfig { BaseUrlHttp = rig.Server.HttpBase });
        IReadOnlyList<Bar> fromHelper = await BinanceHistory.FetchBarsAsync(
            http, instrument, _barType, null, UnixNanos.FromMilliseconds(OpenOf(2_500)), 1_800, UnixNanos.FromMilliseconds(OpenOf(2_500)));

        // More than one page, so this is the paging agreeing and not a single request agreeing.
        Assert.Equal(1_800, fromHelper.Count);
        Assert.Equal(throughTheClient.Data.Cast<Bar>(), fromHelper);
    }

    [Fact]
    public async Task The_bar_helper_walks_a_window_forwards_when_it_is_given_a_start()
    {
        // The direction is venue behaviour, and it is the half a hand-written pager gets wrong: with startTime set
        // the venue answers forwards, so a caller that pages backwards from the end loses everything after the
        // first page of the window.
        await using Rig rig = new(barsAtVenue: 2_500);
        await rig.Client.Instruments.LoadAllAsync(CancellationToken.None);
        using BinanceHttp http = new(new BinanceDataClientConfig { BaseUrlHttp = rig.Server.HttpBase });

        IReadOnlyList<Bar> bars = await BinanceHistory.FetchBarsAsync(
            http, rig.Client.Instruments.Find(_barType.InstrumentId)!, _barType,
            UnixNanos.FromMilliseconds(OpenOf(0)), UnixNanos.FromMilliseconds(OpenOf(2_500)), 1_500, UnixNanos.FromMilliseconds(OpenOf(2_500)));

        Assert.Equal(1_500, bars.Count);
        Assert.Equal(bars.OrderBy(b => b.TsInit.Value).Select(b => b.TsInit), bars.Select(b => b.TsInit));
    }

    [Fact]
    public async Task A_kline_row_becomes_a_bar_with_ohlcv_and_the_close_of_its_interval_as_timestamp()
    {
        await using Rig rig = new(barsAtVenue: 3);

        DataResponse response = await rig.RequestAsync(Commands.RequestBars(_barType, null, UnixNanos.FromMilliseconds(OpenOf(3)), 10));

        Assert.False(response.IsError);
        Assert.Equal(typeof(Bar), response.DataType);
        Assert.Equal(3, response.Data.Count);
        Bar second = Assert.IsType<Bar>(response.Data[1]);
        Assert.Equal(_barType, second.BarType);
        Assert.Equal(new Price(10_001.00m, 2), second.Open);
        Assert.Equal(new Price(10_001.75m, 2), second.High);
        Assert.Equal(new Price(10_000.25m, 2), second.Low);
        Assert.Equal(new Price(10_001.50m, 2), second.Close);
        Assert.Equal(new Quantity(2m, 5), second.Volume);
        Assert.Equal(CloseOf(1), second.TsEvent);
        RecordedRequest request = Assert.Single(rig.Server.RequestsTo("/api/v3/klines"));
        Assert.Equal("BTCUSDT", request.Query("symbol"));
        Assert.Equal("1m", request.Query("interval"));
    }

    [Fact]
    public async Task Without_a_start_time_pages_walk_backwards_from_the_end_with_no_bar_lost_or_repeated()
    {
        await using Rig rig = new(barsAtVenue: 3000);

        DataResponse response = await rig.RequestAsync(Commands.RequestBars(_barType, null, UnixNanos.FromMilliseconds(OpenOf(2999)), 2500));

        List<int> indexes = response.Data.Cast<Bar>().Select(IndexOf).ToList();
        Assert.Equal(Enumerable.Range(500, 2500), indexes); // the most recent 2500 bars, oldest first, contiguous
        Assert.Equal(["1000", "1000", "500"], rig.Server.RequestsTo("/api/v3/klines").Select(r => r.Query("limit")));
        Assert.Equal(CloseOf(2999), response.Data[^1].TsEvent);
    }

    [Fact]
    public async Task A_window_that_fits_one_page_returns_exactly_the_bars_inside_it()
    {
        await using Rig rig = new(barsAtVenue: 500);

        DataResponse response = await rig.RequestAsync(Commands.RequestBars(_barType, UnixNanos.FromMilliseconds(OpenOf(100)), UnixNanos.FromMilliseconds(OpenOf(109)), null));

        // Eleven bars, not ten: bar 99 closes exactly at the window's start, so it covers the window's first moment
        // and belongs to it. Which bars a window holds is BarWindow's rule and the same on every venue; that this
        // venue has to be asked an interval earlier to get them is this venue's business.
        Assert.Equal(Enumerable.Range(99, 11), response.Data.Cast<Bar>().Select(IndexOf));
        RecordedRequest request = Assert.Single(rig.Server.RequestsTo("/api/v3/klines"));
        Assert.Equal(OpenOf(99).ToString(CultureInfo.InvariantCulture), request.Query("startTime"));
        Assert.Equal(OpenOf(109).ToString(CultureInfo.InvariantCulture), request.Query("endTime"));
    }

    [Fact]
    public async Task A_start_and_end_window_longer_than_one_page_returns_every_bar_in_the_window()
    {
        await using Rig rig = new(barsAtVenue: 3000);

        DataResponse response = await rig.RequestAsync(Commands.RequestBars(_barType, UnixNanos.FromMilliseconds(OpenOf(200)), UnixNanos.FromMilliseconds(OpenOf(2699)), 2500));

        // The window holds 2,501 bars once the one closing at its start is counted, and the limit takes the oldest
        // 2,500 of them.
        Assert.Equal(Enumerable.Range(199, 2500), response.Data.Cast<Bar>().Select(IndexOf));
    }

    [Fact]
    public async Task The_limit_caps_the_result_even_when_the_venue_has_more()
    {
        await using Rig rig = new(barsAtVenue: 300);

        DataResponse response = await rig.RequestAsync(Commands.RequestBars(_barType, null, UnixNanos.FromMilliseconds(OpenOf(299)), 25));

        Assert.Equal(Enumerable.Range(275, 25), response.Data.Cast<Bar>().Select(IndexOf));
    }

    [Fact]
    public async Task An_empty_venue_answer_yields_an_empty_response_not_an_error()
    {
        await using Rig rig = new(barsAtVenue: 0);

        DataResponse response = await rig.RequestAsync(Commands.RequestBars(_barType, null, null, 100));

        Assert.False(response.IsError);
        Assert.Empty(response.Data);
    }

    [Fact]
    public async Task Aggregated_trades_are_the_default_history_source_and_map_id_side_and_time()
    {
        await using Rig rig = new(barsAtVenue: 0);
        InstrumentId id = InstrumentId.Parse("BTCUSDT.BINANCE");

        DataResponse response = await rig.RequestAsync(Commands.RequestTrades(id, UnixNanos.FromMilliseconds(1_498_793_700_000), UnixNanos.FromMilliseconds(1_498_793_800_000), 5000));

        RecordedRequest request = Assert.Single(rig.Server.RequestsTo("/api/v3/aggTrades"));
        Assert.Equal("1000", request.Query("limit")); // venue maximum
        Assert.Equal("1498793700000", request.Query("startTime"));
        Assert.Equal("1498793800000", request.Query("endTime"));
        TradeTick first = Assert.IsType<TradeTick>(response.Data[0]);
        TradeTick second = Assert.IsType<TradeTick>(response.Data[1]);
        Assert.Equal(new TradeId("26129"), first.TradeId);
        Assert.Equal(AggressorSide.Seller, first.Aggressor);
        Assert.Equal(new Price(25_000.01m, 2), first.Price);
        Assert.Equal(new Quantity(4.70443m, 5), first.Size);
        Assert.Equal(1_498_793_709_153_000_000L, first.TsEvent.Value);
        Assert.Equal(AggressorSide.Buyer, second.Aggressor);
    }

    [Fact]
    public async Task With_aggregation_off_the_recent_trades_endpoint_and_its_field_names_are_used()
    {
        await using Rig rig = new(barsAtVenue: 0, new BinanceDataClientConfig { UseAggTrades = false });

        DataResponse response = await rig.RequestAsync(Commands.RequestTrades(InstrumentId.Parse("BTCUSDT.BINANCE"), limit: 50));

        Assert.Equal("50", Assert.Single(rig.Server.RequestsTo("/api/v3/trades")).Query("limit"));
        TradeTick trade = Assert.IsType<TradeTick>(Assert.Single(response.Data));
        Assert.Equal(new TradeId("28457"), trade.TradeId);
        Assert.Equal(AggressorSide.Seller, trade.Aggressor);
        Assert.Equal(new Quantity(12m, 5), trade.Size);
        Assert.Equal(1_499_865_549_590_000_000L, trade.TsEvent.Value);
    }

    [Fact]
    public async Task A_venue_failure_is_reported_as_an_error_response_with_the_same_correlation_id()
    {
        await using Rig rig = new(barsAtVenue: 0);
        rig.Server.Handler = r => r.Path.EndsWith("/klines", StringComparison.Ordinal)
            ? StubResponse.Error(400, "{\"code\":-1121,\"msg\":\"Invalid symbol.\"}")
            : StubResponse.Json(BinancePayloads.SpotExchangeInfo);
        RequestBars command = Commands.RequestBars(_barType, null, null, 10);

        DataResponse response = await rig.RequestAsync(command);

        Assert.True(response.IsError);
        Assert.Equal(command.CommandId, response.CorrelationId);
        Assert.Contains("Invalid symbol", response.Error);
        Assert.Empty(response.Data);
    }

    [Fact]
    public async Task Request_types_the_venue_cannot_serve_are_answered_with_an_error_instead_of_silence()
    {
        await using Rig rig = new(barsAtVenue: 0);
        RequestQuoteTicks command = new(InstrumentId.Parse("BTCUSDT.BINANCE"), null, null, null, null, Guid.NewGuid(), TestKernel.Now);

        DataResponse response = await rig.RequestAsync(command);

        Assert.True(response.IsError);
        Assert.Contains("RequestQuoteTicks", response.Error);
    }

    [Fact]
    public async Task A_window_is_walked_forwards_page_by_page_and_each_request_starts_after_the_last_bar()
    {
        await using Rig rig = new(barsAtVenue: 3000);

        DataResponse response = await rig.RequestAsync(Commands.RequestBars(_barType, UnixNanos.FromMilliseconds(OpenOf(0)), UnixNanos.FromMilliseconds(OpenOf(2199)), 2200));

        Assert.Equal(Enumerable.Range(0, 2200), response.Data.Cast<Bar>().Select(IndexOf));

        // Three pages: 1000, 1000, then the 200 that are left, each asking from just after the previous page's last bar.
        IReadOnlyList<RecordedRequest> requests = rig.Server.RequestsTo("/api/v3/klines");
        Assert.Equal(3, requests.Count);
        Assert.Equal(["1000", "1000", "200"], requests.Select(r => r.Query("limit")));
        Assert.Equal((OpenOf(0) - MinuteMs).ToString(CultureInfo.InvariantCulture), requests[0].Query("startTime"));
        Assert.Equal((OpenOf(999) + 1).ToString(CultureInfo.InvariantCulture), requests[1].Query("startTime"));
        Assert.Equal((OpenOf(1999) + 1).ToString(CultureInfo.InvariantCulture), requests[2].Query("startTime"));
        Assert.All(requests, r => Assert.Equal(OpenOf(2199).ToString(CultureInfo.InvariantCulture), r.Query("endTime")));
    }

    [Fact]
    public async Task A_window_the_venue_cannot_fill_ends_with_what_it_has_rather_than_asking_for_ever()
    {
        await using Rig rig = new(barsAtVenue: 1200);

        DataResponse response = await rig.RequestAsync(Commands.RequestBars(_barType, UnixNanos.FromMilliseconds(OpenOf(0)), UnixNanos.FromMilliseconds(OpenOf(2999)), 3000));

        Assert.Equal(1200, response.Data.Count);
        Assert.Equal(Enumerable.Range(0, 1200), response.Data.Cast<Bar>().Select(IndexOf));
        Assert.Equal(2, rig.Server.RequestsTo("/api/v3/klines").Count);
    }

    [Fact]
    public async Task A_window_of_exactly_one_page_is_one_request()
    {
        await using Rig rig = new(barsAtVenue: 3000);

        DataResponse response = await rig.RequestAsync(Commands.RequestBars(_barType, UnixNanos.FromMilliseconds(OpenOf(500)), UnixNanos.FromMilliseconds(OpenOf(1499)), 1000));

        // As above: the bar closing at the start is in the window, and the limit is met before its newest bar.
        Assert.Equal(Enumerable.Range(499, 1000), response.Data.Cast<Bar>().Select(IndexOf));
        Assert.Single(rig.Server.RequestsTo("/api/v3/klines"));
    }
}
