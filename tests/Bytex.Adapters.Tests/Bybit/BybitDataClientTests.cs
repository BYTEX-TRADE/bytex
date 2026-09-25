using System.Globalization;
using System.Text;
using System.Text.Json;
using Bytex.Adapters.Bybit;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Primitives;

namespace Bytex.Adapters.Tests.Bybit;

// Why: Bybit streams differ from Binance in every detail (topics, snapshot/delta semantics, newest-first REST
// lists). Each documented payload must become the right engine object, and paginated history must be complete.
// The venue is a stub on 127.0.0.1; payloads follow the Bybit V5 documentation.
public sealed class BybitDataClientTests
{
    private const long FirstOpenMs = 1_690_000_200_000; // a whole 5-minute boundary
    private const long FiveMinutesMs = 300_000;
    private const long EightHoursMs = 8L * 60L * 60L * 1000L;
    private static readonly InstrumentId _perp = InstrumentId.Parse("BTCUSDT-PERP.BYBIT");
    private static readonly BarType _barType = BarType.Parse("BTCUSDT-PERP.BYBIT-5-MINUTE-LAST-EXTERNAL");

    private sealed class Rig : IAsyncDisposable
    {
        private WsSession? _session;

        public Rig(int barsAtVenue = 0, bool handleRevisedBars = false, BybitProductType type = BybitProductType.Linear, string? instrumentsAtVenue = null, int fundingAtVenue = 0)
        {
            string instruments = instrumentsAtVenue ?? (type == BybitProductType.Linear ? BybitPayloads.LinearInstrumentsPage1.Replace("cursor-page-2", string.Empty, StringComparison.Ordinal) : BybitPayloads.SpotInstruments);
            Routes routes = new Routes()
                .On("GET", "/v5/market/instruments-info", instruments)
                .On("GET", "/v5/market/kline", r => Klines(r, barsAtVenue))
                .On("GET", "/v5/market/funding/history", r => Funding(r, fundingAtVenue))
                .On("GET", "/v5/market/recent-trade", BybitPayloads.Envelope("""
                    {"category":"linear","list":[
                      {"execId":"e-newest","symbol":"BTCUSDT","price":"16618.5","size":"0.250","side":"Sell","time":"1672052955758","isBlockTrade":false},
                      {"execId":"e-oldest","symbol":"BTCUSDT","price":"16618.0","size":"0.001","side":"Buy","time":"1672052955000","isBlockTrade":false}]}
                    """));
            Server = new LoopbackServer(routes.Handle);
            Kernel = new TestKernel();
            Client = new BybitDataClient(new ClientId("BYBIT"), new BybitDataClientConfig
            {
                ProductType = type,
                BaseUrlHttp = Server.HttpBase,
                BaseUrlWs = Server.WsBase,
                HandleRevisedBars = handleRevisedBars,
                InstrumentProvider = new Core.Adapters.InstrumentProviderConfig { LoadAll = true },
            }, Kernel.Services);
            Client.AttachSink(Sink);
        }

        public LoopbackServer Server { get; }

        public TestKernel Kernel { get; }

        public BybitDataClient Client { get; }

        public RecordingDataSink Sink { get; } = new();

        public WsSession Session => _session ?? throw new InvalidOperationException("not connected");

        public async Task<Rig> ConnectAsync()
        {
            await Client.ConnectAsync(CancellationToken.None).WaitAsync(Wait.Timeout);
            _session = await Server.NextSessionAsync();
            return this;
        }

        /// <summary>Next control message from the client as "op topic,topic" with the topics sorted.</summary>
        public async Task<string> NextControlMessageAsync(WsSession? session = null)
        {
            using JsonDocument doc = JsonDocument.Parse(await (session ?? Session).ReceiveTextAsync());
            return doc.RootElement.GetProperty("op").GetString() + " " + string.Join(",", doc.RootElement.GetProperty("args").EnumerateArray().Select(a => a.GetString()!).Order(StringComparer.Ordinal));
        }

        public async Task<DataResponse> RequestAsync(RequestCommand command)
        {
            await Client.Instruments.LoadAllAsync(CancellationToken.None);
            await Client.RequestAsync(command, CancellationToken.None).WaitAsync(Wait.Timeout);
            return await Sink.NextResponseAsync();
        }

        /// <summary>
        /// Bybit semantics for funding history, measured on the live venue on 2026-09-23: rows inside
        /// [startTime, endTime], the most recent of them, newest first, at most two hundred to a page. The rate of a
        /// row is its index in ten-thousandths, so a test can say which rows it got.
        /// </summary>
        private static StubResponse Funding(RecordedRequest request, int fundingAtVenue)
        {
            long? start = request.Query("startTime") is { } s ? long.Parse(s, CultureInfo.InvariantCulture) : null;
            long? end = request.Query("endTime") is { } e ? long.Parse(e, CultureInfo.InvariantCulture) : null;
            int limit = Math.Min(200, request.Query("limit") is { } l ? int.Parse(l, CultureInfo.InvariantCulture) : 200);
            IEnumerable<int> page = Enumerable.Range(0, fundingAtVenue)
                .Where(i => (start is null || FundingAt(i) >= start) && (end is null || FundingAt(i) <= end))
                .TakeLast(limit)
                .Reverse();
            StringBuilder rows = new();
            foreach (int i in page)
            {
                if (rows.Length > 0)
                {
                    rows.Append(',');
                }

                rows.Append(CultureInfo.InvariantCulture, $"{{\"symbol\":\"BTCUSDT\",\"fundingRate\":\"0.000{i:D4}\",\"fundingRateTimestamp\":\"{FundingAt(i)}\"}}");
            }

            return StubResponse.Json(BybitPayloads.Envelope($"{{\"category\":\"linear\",\"list\":[{rows}]}}"));
        }

        /// <summary>Bybit semantics: rows inside [start, end], the most recent `limit` of them, newest first.</summary>
        private static StubResponse Klines(RecordedRequest request, int barsAtVenue)
        {
            long? start = request.Query("start") is { } s ? long.Parse(s, CultureInfo.InvariantCulture) : null;
            long? end = request.Query("end") is { } e ? long.Parse(e, CultureInfo.InvariantCulture) : null;
            int limit = Math.Min(1000, request.Query("limit") is { } l ? int.Parse(l, CultureInfo.InvariantCulture) : 200);
            IEnumerable<int> page = Enumerable.Range(0, barsAtVenue).Where(i => (start is null || OpenOf(i) >= start) && (end is null || OpenOf(i) <= end)).TakeLast(limit).Reverse();
            StringBuilder rows = new();
            foreach (int i in page)
            {
                if (rows.Length > 0)
                {
                    rows.Append(',');
                }

                rows.Append(CultureInfo.InvariantCulture, $"[\"{OpenOf(i)}\",\"{10_000 + i}.0\",\"{10_000 + i}.5\",\"{10_000 + i - 1}.5\",\"{10_000 + i}.1\",\"{i + 1}.000\",\"0\"]");
            }

            return StubResponse.Json(BybitPayloads.Envelope($"{{\"symbol\":\"BTCUSDT\",\"category\":\"linear\",\"list\":[{rows}]}}"));
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            Kernel.Dispose();
            await Server.DisposeAsync();
        }
    }

    private static long OpenOf(int index) => FirstOpenMs + (index * FiveMinutesMs);

    /// <summary>When the funding of a given index was charged: every eight hours, as a perpetual charges it.</summary>
    private static long FundingAt(int index) => FirstOpenMs + (index * EightHoursMs);

    private static int IndexOf(Bar bar) => (int)(bar.Open.Value - 10_000m);

    // ----- Streams -----

    [Theory]
    [InlineData(BybitProductType.Spot, "/v5/public/spot")]
    [InlineData(BybitProductType.Linear, "/v5/public/linear")]
    public async Task Connecting_opens_the_public_stream_of_the_configured_category(BybitProductType type, string route)
    {
        await using Rig rig = await new Rig(type: type).ConnectAsync();

        Assert.Equal(route, rig.Session.Path);
        Assert.Equal(type == BybitProductType.Spot ? "spot" : "linear", Assert.Single(rig.Server.Requests).Query("category"));
        Assert.Single(rig.Sink.Instruments);
        Assert.True(rig.Client.IsConnected);
    }

    [Fact]
    public async Task Subscriptions_use_the_documented_topic_names_with_the_raw_symbol()
    {
        await using Rig rig = await new Rig().ConnectAsync();

        await rig.Client.SubscribeAsync(Commands.Quotes(_perp), CancellationToken.None);
        await rig.Client.SubscribeAsync(Commands.Trades(_perp), CancellationToken.None);
        await rig.Client.SubscribeAsync(Commands.Bars(_barType), CancellationToken.None);
        await rig.Client.SubscribeAsync(Commands.Book(_perp, depth: 25), CancellationToken.None);
        await rig.Client.SubscribeAsync(Commands.Book(InstrumentId.Parse("ETHUSDT-PERP.BYBIT"), depth: 100), CancellationToken.None);
        await rig.Client.SubscribeAsync(Commands.Mark(_perp), CancellationToken.None);

        Assert.Equal("subscribe orderbook.1.BTCUSDT", await rig.NextControlMessageAsync());
        Assert.Equal("subscribe publicTrade.BTCUSDT", await rig.NextControlMessageAsync());
        Assert.Equal("subscribe kline.5.BTCUSDT", await rig.NextControlMessageAsync());
        Assert.Equal("subscribe orderbook.50.BTCUSDT", await rig.NextControlMessageAsync());
        Assert.Equal("subscribe orderbook.200.ETHUSDT", await rig.NextControlMessageAsync());
        Assert.Equal("subscribe tickers.BTCUSDT", await rig.NextControlMessageAsync());
    }

    [Fact]
    public async Task Top_of_book_snapshot_then_delta_yield_quotes_that_keep_the_untouched_side()
    {
        await using Rig rig = await new Rig().ConnectAsync();

        await rig.Session.SendTextAsync(BybitPayloads.SubscribeAck);
        await rig.Session.SendTextAsync(BybitPayloads.TopOfBookSnapshot);
        await rig.Session.SendTextAsync(BybitPayloads.TopOfBookDelta);

        QuoteTick first = Assert.IsType<QuoteTick>(await rig.Sink.NextDataAsync());
        QuoteTick second = Assert.IsType<QuoteTick>(await rig.Sink.NextDataAsync());
        Assert.Equal(_perp, first.InstrumentId);
        Assert.Equal(new Price(16_493.5m, 1), first.Bid);
        Assert.Equal(new Price(16_611.0m, 1), first.Ask);
        Assert.Equal(new Quantity(0.006m, 3), first.BidSize);
        Assert.Equal(new Quantity(0.029m, 3), first.AskSize);
        Assert.Equal(1_672_304_484_978_000_000L, first.TsEvent.Value);
        Assert.Equal(TestKernel.Now, first.TsInit);
        Assert.Equal(new Price(16_493.0m, 1), second.Bid); // the removed 16493.50 level must not survive
        Assert.Equal(new Quantity(0.5m, 3), second.BidSize);
        Assert.Equal(new Price(16_611.0m, 1), second.Ask);
        Assert.Equal(new Quantity(0.029m, 3), second.AskSize);
    }

    [Fact]
    public async Task Public_trades_map_the_taker_side_to_the_aggressor_and_keep_the_venue_trade_id()
    {
        await using Rig rig = await new Rig().ConnectAsync();

        await rig.Session.SendTextAsync(BybitPayloads.PublicTrades);

        TradeTick buy = Assert.IsType<TradeTick>(await rig.Sink.NextDataAsync());
        TradeTick sell = Assert.IsType<TradeTick>(await rig.Sink.NextDataAsync());
        Assert.Equal(AggressorSide.Buyer, buy.Aggressor);
        Assert.Equal(new Price(16_578.5m, 1), buy.Price);
        Assert.Equal(new Quantity(0.001m, 3), buy.Size);
        Assert.Equal(new TradeId("20f43950-d8dd-5b31-9112-a178eb6023af"), buy.TradeId);
        Assert.Equal(1_672_304_486_865_000_000L, buy.TsEvent.Value);
        Assert.Equal(AggressorSide.Seller, sell.Aggressor);
        Assert.Equal(new Quantity(0.25m, 3), sell.Size);
    }

    [Fact]
    public async Task Stream_data_for_a_dated_linear_contract_is_published_under_its_dated_instrument_id()
    {
        await using Rig rig = await new Rig(instrumentsAtVenue: BybitPayloads.LinearInstrumentsPage2).ConnectAsync();

        await rig.Session.SendTextAsync(BybitPayloads.PublicTrades.Replace("BTCUSDT", "BTCUSDT-26SEP25", StringComparison.Ordinal));

        TradeTick trade = Assert.IsType<TradeTick>(await rig.Sink.NextDataAsync());
        Assert.Equal(InstrumentId.Parse("BTCUSDT-26SEP25.BYBIT"), trade.InstrumentId);
    }

    [Fact]
    public async Task Only_the_confirmed_kline_becomes_a_bar_stamped_with_the_close_of_its_interval()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        await rig.Client.SubscribeAsync(Commands.Bars(_barType), CancellationToken.None);

        await rig.Session.SendTextAsync(BybitPayloads.KlineForming);
        await rig.Session.SendTextAsync(BybitPayloads.KlineConfirmed);

        Bar bar = Assert.IsType<Bar>(await rig.Sink.NextDataAsync());
        Assert.Equal(_barType, bar.BarType);
        Assert.Equal(new Price(16_649.5m, 1), bar.Open);
        Assert.Equal(new Price(16_690.0m, 1), bar.High);
        Assert.Equal(new Price(16_608.0m, 1), bar.Low);
        Assert.Equal(new Price(16_680.5m, 1), bar.Close);
        Assert.Equal(new Quantity(3.5m, 3), bar.Volume);
        Assert.Equal(1_672_325_100_000_000_000L, bar.TsEvent.Value); // end 1672325099999 ms + 1 ms
        Assert.False(bar.IsRevision);
    }

    [Fact]
    public async Task With_handleRevisedBars_the_forming_kline_is_forwarded_as_a_revision()
    {
        await using Rig rig = await new Rig(handleRevisedBars: true).ConnectAsync();
        await rig.Client.SubscribeAsync(Commands.Bars(_barType), CancellationToken.None);

        await rig.Session.SendTextAsync(BybitPayloads.KlineForming);

        Bar bar = Assert.IsType<Bar>(await rig.Sink.NextDataAsync());
        Assert.True(bar.IsRevision);
        Assert.Equal(new Price(16_677.0m, 1), bar.Close);
    }

    [Fact]
    public async Task A_book_snapshot_clears_and_adds_levels_and_a_delta_updates_or_deletes_them()
    {
        await using Rig rig = await new Rig().ConnectAsync();

        await rig.Session.SendTextAsync(BybitPayloads.BookSnapshot);
        await rig.Session.SendTextAsync(BybitPayloads.BookDelta);

        OrderBookDeltas snapshot = Assert.IsType<OrderBookDeltas>(await rig.Sink.NextDataAsync());
        OrderBookDeltas delta = Assert.IsType<OrderBookDeltas>(await rig.Sink.NextDataAsync());
        Assert.True(snapshot.IsSnapshot);
        Assert.Equal(7_961_638_724UL, snapshot.Sequence);
        Assert.Equal([BookAction.Clear, BookAction.Add, BookAction.Add, BookAction.Add], snapshot.Deltas.Select(d => d.Action));
        Assert.Equal([OrderSide.Buy, OrderSide.Buy, OrderSide.Sell], snapshot.Deltas.Skip(1).Select(d => d.Order.Side));
        Assert.False(delta.IsSnapshot);
        Assert.Equal([BookAction.Delete, BookAction.Update], delta.Deltas.Select(d => d.Action));
        Assert.Equal(new Price(16_493.5m, 1), delta.Deltas[0].Order.Price);
        Assert.Equal(new Quantity(0.129m, 3), delta.Deltas[1].Order.Size);
        Assert.Equal(1_672_304_485_978_000_000L, delta.TsEvent.Value);
    }

    [Fact]
    public async Task A_ticker_snapshot_yields_mark_index_and_funding_and_a_delta_only_what_changed()
    {
        await using Rig rig = await new Rig().ConnectAsync();

        await rig.Session.SendTextAsync(BybitPayloads.TickerSnapshot);
        await rig.Session.SendTextAsync(BybitPayloads.TickerDelta);
        await rig.Session.SendTextAsync(BybitPayloads.PublicTrades);

        MarkPriceUpdate mark = Assert.IsType<MarkPriceUpdate>(await rig.Sink.NextDataAsync());
        IndexPriceUpdate index = Assert.IsType<IndexPriceUpdate>(await rig.Sink.NextDataAsync());
        FundingRateUpdate funding = Assert.IsType<FundingRateUpdate>(await rig.Sink.NextDataAsync());
        MarkPriceUpdate markDelta = Assert.IsType<MarkPriceUpdate>(await rig.Sink.NextDataAsync());
        Assert.IsType<TradeTick>(await rig.Sink.NextDataAsync()); // nothing else came out of the delta
        Assert.Equal(17_217.3m, mark.Value.Value);
        Assert.Equal(17_227.4m, index.Value.Value);
        Assert.Equal(-0.000212m, funding.Rate);
        Assert.Equal(1_673_280_000_000_000_000L, funding.NextFundingTime!.Value.Value);
        Assert.Equal(1_673_272_861_686_000_000L, mark.TsEvent.Value);
        Assert.Equal(17_218.1m, markDelta.Value.Value);
    }

    [Fact]
    public async Task Mark_price_subscriptions_are_refused_on_spot_with_a_reason()
    {
        await using Rig rig = await new Rig(type: BybitProductType.Spot).ConnectAsync();
        SubscribeMarkPrices command = Commands.Mark(InstrumentId.Parse("BTCUSDT.BYBIT"));

        await rig.Client.SubscribeAsync(command, CancellationToken.None);

        (SubscribeCommand failed, string reason) = Assert.Single(rig.Sink.SubscriptionFailures);
        Assert.Same(command, failed);
        Assert.Contains("Spot", reason);
    }

    [Fact]
    public async Task After_a_dropped_connection_all_topics_are_resubscribed_in_one_request()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        await rig.Client.SubscribeAsync(Commands.Quotes(_perp), CancellationToken.None);
        await rig.Client.SubscribeAsync(Commands.Trades(_perp), CancellationToken.None);
        await rig.NextControlMessageAsync();
        await rig.NextControlMessageAsync();

        rig.Session.Drop();
        WsSession second = await rig.Server.NextSessionAsync();

        Assert.Equal("subscribe orderbook.1.BTCUSDT,publicTrade.BTCUSDT", await rig.NextControlMessageAsync(second));
    }

    [Fact]
    public async Task Unsubscribing_sends_unsubscribe_for_a_topic_that_was_active()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        await rig.Client.SubscribeAsync(Commands.Trades(_perp), CancellationToken.None);

        await rig.Client.UnsubscribeAsync(new UnsubscribeTradeTicks(_perp, null, Guid.NewGuid(), TestKernel.Now), CancellationToken.None);

        Assert.Equal("subscribe publicTrade.BTCUSDT", await rig.NextControlMessageAsync());
        Assert.Equal("unsubscribe publicTrade.BTCUSDT", await rig.NextControlMessageAsync());
    }

    // ----- History -----

    [Fact]
    public async Task A_kline_row_becomes_a_bar_stamped_with_start_plus_interval_and_rows_are_returned_oldest_first()
    {
        await using Rig rig = new(barsAtVenue: 3);

        DataResponse response = await rig.RequestAsync(Commands.RequestBars(_barType, null, UnixNanos.FromMilliseconds(OpenOf(2)), 10));

        Assert.Equal([0, 1, 2], response.Data.Cast<Bar>().Select(IndexOf));
        Bar second = (Bar)response.Data[1];
        Assert.Equal(new Price(10_001.0m, 1), second.Open);
        Assert.Equal(new Price(10_001.5m, 1), second.High);
        Assert.Equal(new Price(10_000.5m, 1), second.Low);
        Assert.Equal(new Price(10_001.1m, 1), second.Close);
        Assert.Equal(new Quantity(2m, 3), second.Volume);
        Assert.Equal(UnixNanos.FromMilliseconds(OpenOf(1) + FiveMinutesMs), second.TsEvent);
        RecordedRequest request = Assert.Single(rig.Server.RequestsTo("/v5/market/kline"));
        Assert.Equal("linear", request.Query("category"));
        Assert.Equal("BTCUSDT", request.Query("symbol"));
        Assert.Equal("5", request.Query("interval"));
    }

    [Fact]
    public async Task The_start_and_end_helper_gives_the_whole_window_and_not_one_page_of_it()
    {
        // As Binance, with the opposite half lost: this venue answers newest first, so a page cap returned the LAST
        // thousand bars of the window and dropped its beginning.
        await using Rig rig = await new Rig(barsAtVenue: 2_400).ConnectAsync();
        using BybitHttp http = new(new BybitDataClientConfig { BaseUrlHttp = rig.Server.HttpBase, ProductType = BybitProductType.Linear });

        IReadOnlyList<Bar> bars = await BybitHistory.FetchBarsAsync(
            http,
            rig.Client.Instruments.Find(_barType.InstrumentId)!,
            _barType,
            UnixNanos.FromMilliseconds(OpenOf(0)).ToDateTimeOffset(),
            UnixNanos.FromMilliseconds(OpenOf(2_399)).ToDateTimeOffset());

        Assert.Equal(2_400, bars.Count);
        Assert.Equal(bars.OrderBy(b => b.TsInit.Value).Select(b => b.TsInit), bars.Select(b => b.TsInit));
    }

    [Fact]
    public async Task The_bar_helper_needs_no_client_and_gives_what_the_client_gives()
    {
        // E7 of the venue checklist, and the reason for it: a host that stores history has no node, so if the paging
        // is private to the data client it gets written a second time somewhere else - and the second copy stops
        // matching this one the day the venue changes a page size. It was written twice, until this.
        await using Rig rig = await new Rig(barsAtVenue: 2_400).ConnectAsync();
        DataResponse throughTheClient = await rig.RequestAsync(Commands.RequestBars(_barType, null, UnixNanos.FromMilliseconds(OpenOf(2_400)), 1_500));

        using BybitHttp http = new(new BybitDataClientConfig { BaseUrlHttp = rig.Server.HttpBase, ProductType = BybitProductType.Linear });
        IReadOnlyList<Bar> fromHelper = await BybitHistory.FetchBarsAsync(
            http, rig.Client.Instruments.Find(_barType.InstrumentId)!, _barType, null, UnixNanos.FromMilliseconds(OpenOf(2_400)), 1_500, UnixNanos.FromMilliseconds(OpenOf(2_400)));

        // More than one page, so this is the paging and the reversal agreeing, not a single request agreeing.
        Assert.Equal(1_500, fromHelper.Count);
        Assert.Equal(throughTheClient.Data.Cast<Bar>(), fromHelper);
        Assert.Equal(fromHelper.OrderBy(b => b.TsInit.Value).Select(b => b.TsInit), fromHelper.Select(b => b.TsInit));
    }

    [Fact]
    public async Task The_funding_helper_needs_no_client_and_gives_what_the_client_gives()
    {
        // What a catalog download calls: no node, no bus, no data client. Paging and the newest-first reversal are
        // venue behaviour and live in one place, so what is stored and what a running node sees cannot disagree.
        await using Rig rig = new(fundingAtVenue: 450);
        using BybitHttp http = new(new BybitDataClientConfig { BaseUrlHttp = rig.Server.HttpBase, ProductType = BybitProductType.Linear });

        IReadOnlyList<FundingRateUpdate> rates = await BybitHistory.FetchFundingRatesAsync(http, _perp, DateTimeOffset.UnixEpoch);

        Assert.Equal(450, rates.Count);
        Assert.Equal(rates.OrderBy(r => r.TsEvent.Value).Select(r => r.TsEvent), rates.Select(r => r.TsEvent));
        DataResponse throughTheClient = await rig.RequestAsync(Commands.RequestFundingRates(_perp, null, null, null));
        Assert.Equal(throughTheClient.Data.Cast<FundingRateUpdate>().Select(r => r.Rate), rates.Select(r => r.Rate));
    }

    [Fact]
    public async Task Funding_history_arrives_newest_first_and_is_returned_oldest_first()
    {
        // R8.15 needs rates in the catalog before a perpetual backtest can be charged for holding one. The venue
        // answers newest first; history is stored and replayed oldest first, like every other kind.
        await using Rig rig = new(fundingAtVenue: 5);

        DataResponse response = await rig.RequestAsync(Commands.RequestFundingRates(_perp, null, null, null));

        Assert.Equal([0.0000000m, 0.0000001m, 0.0000002m, 0.0000003m, 0.0000004m], response.Data.Cast<FundingRateUpdate>().Select(f => f.Rate));
        Assert.Equal(_perp, response.Data.Cast<FundingRateUpdate>().First().InstrumentId);
        RecordedRequest request = Assert.Single(rig.Server.RequestsTo("/v5/market/funding/history"));
        Assert.Equal("linear", request.Query("category"));
        Assert.Equal("BTCUSDT", request.Query("symbol"));
        Assert.Equal("200", request.Query("limit"));
    }

    [Fact]
    public async Task Funding_history_longer_than_a_page_is_walked_backwards_and_comes_back_whole_and_in_order()
    {
        // Two hundred to a page, so a year of an eight-hourly rate is several pages. Each one asks for what is older
        // than the last, or the venue answers the same page for ever.
        await using Rig rig = new(fundingAtVenue: 450);

        DataResponse response = await rig.RequestAsync(Commands.RequestFundingRates(_perp, null, null, null));

        Assert.Equal(450, response.Data.Count);
        Assert.Equal(response.Data.Cast<FundingRateUpdate>().OrderBy(f => f.TsEvent.Value).Select(f => f.TsEvent), response.Data.Cast<FundingRateUpdate>().Select(f => f.TsEvent));
        Assert.Equal(450, response.Data.Cast<FundingRateUpdate>().Select(f => f.TsEvent).Distinct().Count());
        Assert.Equal(3, rig.Server.RequestsTo("/v5/market/funding/history").Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Multi_page_history_returns_every_bar_of_the_window_once_and_in_order(bool withStart)
    {
        await using Rig rig = new(barsAtVenue: 3000);
        UnixNanos? start = withStart ? UnixNanos.FromMilliseconds(OpenOf(400)) : null;

        DataResponse response = await rig.RequestAsync(Commands.RequestBars(_barType, start, UnixNanos.FromMilliseconds(OpenOf(2899)), 2500));

        Assert.Equal(Enumerable.Range(400, 2500), response.Data.Cast<Bar>().Select(IndexOf));
        Assert.Equal(["1000", "1000", "500"], rig.Server.RequestsTo("/v5/market/kline").Select(r => r.Query("limit")));
    }

    [Fact]
    public async Task Recent_trades_arrive_newest_first_and_are_returned_oldest_first()
    {
        await using Rig rig = new();

        DataResponse response = await rig.RequestAsync(Commands.RequestTrades(_perp, limit: 2));

        Assert.Equal(["e-oldest", "e-newest"], response.Data.Cast<TradeTick>().Select(t => t.TradeId.Value));
        TradeTick newest = (TradeTick)response.Data[1];
        Assert.Equal(AggressorSide.Seller, newest.Aggressor);
        Assert.Equal(new Price(16_618.5m, 1), newest.Price);
        Assert.Equal(new Quantity(0.25m, 3), newest.Size);
        Assert.Equal(1_672_052_955_758_000_000L, newest.TsEvent.Value);
        RecordedRequest request = Assert.Single(rig.Server.RequestsTo("/v5/market/recent-trade"));
        Assert.Equal("2", request.Query("limit"));
        Assert.Equal("BTCUSDT", request.Query("symbol"));
    }

    [Fact]
    public async Task A_venue_error_envelope_becomes_an_error_response_with_the_venue_message()
    {
        await using Rig rig = new();
        await rig.Client.Instruments.LoadAllAsync(CancellationToken.None);
        rig.Server.Handler = _ => StubResponse.Json(BybitPayloads.Error(10001, "Illegal category"));
        RequestBars command = Commands.RequestBars(_barType, null, null, 10);

        await rig.Client.RequestAsync(command, CancellationToken.None).WaitAsync(Wait.Timeout);
        DataResponse response = await rig.Sink.NextResponseAsync();

        Assert.True(response.IsError);
        Assert.Equal(command.CommandId, response.CorrelationId);
        Assert.Contains("Illegal category", response.Error);
    }
}
