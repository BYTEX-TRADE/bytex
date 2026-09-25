using Bytex.Core.Adapters;
using Bytex.Core.Caching;
using Bytex.Core.Engines;
using Bytex.Core.Messaging;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Tests.Support;
using Bytex.Core.Timing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bytex.Core.Tests.Engines;

// Why: the data engine is the only path from a venue to a strategy. Subscriptions must be reference counted
// towards the client, and every element must be in the cache before it is published on its topic.
public class DataEngineTests
{
    private sealed class Harness
    {
        public Harness(DataEngineConfig? config = null, bool registerClient = true)
        {
            Clock = new TestClock(TestOrders.T0);
            Bus = new MessageBus(TestIds.Trader);
            Cache = new Cache();
            Engine = new DataEngine(Bus, Cache, config);
            Engine.Initialize(Clock, NullLoggerFactory.Instance);
            Services = new KernelServices(Clock, Cache, Bus, NullLoggerFactory.Instance, TestIds.Trader, TradingEnvironment.Backtest);
            Client = new RecordingDataClient(Services, TestIds.Binance);
            if (registerClient)
            {
                Engine.RegisterClient(Client);
            }

            Cache.AddInstrument(TestInstruments.BtcUsdt());
        }

        public TestClock Clock { get; }

        public MessageBus Bus { get; }

        public Cache Cache { get; }

        public DataEngine Engine { get; }

        public KernelServices Services { get; }

        public RecordingDataClient Client { get; }

        public BusRecorder Record(string pattern)
        {
            BusRecorder recorder = new();
            Bus.Subscribe(pattern, recorder.Handle);
            return recorder;
        }

        public void Execute(object command) => Bus.Send(Endpoints.DataEngineExecute, command);
    }

    private static UnixNanos At(int minutes, int seconds) => TestOrders.T0 + new TimeSpan(0, minutes, seconds);

    private static QuoteTick Quote(string bid, string ask, UnixNanos? ts = null) =>
        new(TestIds.BtcUsdt, Price.Parse(bid), Price.Parse(ask), Quantity.Parse("1.000"), Quantity.Parse("1.000"), ts ?? TestOrders.T0, ts ?? TestOrders.T0);

    private static TradeTick Trade(string price, string size, UnixNanos ts, string id = "T") =>
        new(TestIds.BtcUsdt, Price.Parse(price), Quantity.Parse(size), AggressorSide.Buyer, new TradeId(id), ts, ts);

    private static SubscribeQuoteTicks SubscribeQuotes(ClientId? clientId = null) => new(TestIds.BtcUsdt, clientId, Guid.NewGuid(), TestOrders.T0);

    private static UnsubscribeQuoteTicks UnsubscribeQuotes() => new(TestIds.BtcUsdt, null, Guid.NewGuid(), TestOrders.T0);

    [Fact]
    public void Subscription_is_sent_to_the_client_of_the_instrument_venue()
    {
        Harness h = new();
        SubscribeQuoteTicks command = SubscribeQuotes();

        h.Execute(command);

        Assert.Same(command, Assert.Single(h.Client.Subscriptions));
        Assert.Equal(["data.quotes.BINANCE.BTCUSDT"], h.Engine.SubscribedTopics);
    }

    [Fact]
    public void Second_subscriber_to_the_same_stream_does_not_resubscribe_at_the_venue()
    {
        Harness h = new();

        h.Execute(SubscribeQuotes());
        h.Execute(SubscribeQuotes());

        Assert.Single(h.Client.Subscriptions);
    }

    [Fact]
    public void Venue_unsubscription_happens_only_when_the_last_subscriber_leaves()
    {
        Harness h = new();
        h.Execute(SubscribeQuotes());
        h.Execute(SubscribeQuotes());

        h.Execute(UnsubscribeQuotes());
        int afterFirst = h.Client.Unsubscriptions.Count;
        h.Execute(UnsubscribeQuotes());

        Assert.Equal(0, afterFirst);
        Assert.Single(h.Client.Unsubscriptions);
        Assert.Empty(h.Engine.SubscribedTopics);
    }

    [Fact]
    public void Unsubscribe_without_a_subscription_is_ignored()
    {
        Harness h = new();

        h.Execute(UnsubscribeQuotes());

        Assert.Empty(h.Client.Unsubscriptions);
    }

    [Fact]
    public void Explicit_client_id_wins_over_venue_routing()
    {
        Harness h = new();
        RecordingDataClient tardis = new(h.Services, null, "TARDIS");
        h.Engine.RegisterClient(tardis);

        h.Execute(SubscribeQuotes(new ClientId("TARDIS")));

        Assert.Empty(h.Client.Subscriptions);
        Assert.Single(tardis.Subscriptions);
    }

    [Fact]
    public void Default_client_serves_venues_without_their_own_client()
    {
        Harness h = new(registerClient: false);
        RecordingDataClient fallback = new(h.Services, null, "FALLBACK");
        h.Engine.RegisterDefaultClient(fallback);

        h.Execute(SubscribeQuotes());

        Assert.Single(fallback.Subscriptions);
    }

    [Fact]
    public void Subscription_without_any_client_is_remembered_but_goes_nowhere()
    {
        Harness h = new(registerClient: false);

        h.Execute(SubscribeQuotes());

        Assert.Empty(h.Client.Subscriptions);
        Assert.Single(h.Engine.SubscribedTopics);
    }

    [Fact]
    public void Registering_the_same_data_client_twice_is_an_error()
    {
        Harness h = new();

        Assert.Throws<InvalidOperationException>(() => h.Engine.RegisterClient(new RecordingDataClient(h.Services, TestIds.Binance)));
    }

    [Fact]
    public void Deregistered_client_loses_its_routing()
    {
        Harness h = new();

        h.Engine.DeregisterClient(h.Client);

        Assert.Null(h.Engine.ClientFor(null, TestIds.Binance));
        Assert.Empty(h.Engine.RegisteredClients);
    }

    [Fact]
    public void Quote_is_cached_before_it_is_published_on_its_topic()
    {
        Harness h = new();
        QuoteTick? cachedWhenHandled = null;
        h.Bus.Subscribe("data.quotes.BINANCE.BTCUSDT", _ => cachedWhenHandled = h.Cache.QuoteTick(TestIds.BtcUsdt));
        QuoteTick quote = Quote("49990.00", "50010.00");

        h.Client.Push(quote);

        Assert.Equal(quote, cachedWhenHandled);
        Assert.Equal(1, h.Engine.DataCount);
    }

    [Fact]
    public void Each_data_kind_is_published_on_its_own_topic_and_cached()
    {
        Harness h = new();
        BusRecorder all = h.Record("data.*");
        BusRecorder trades = h.Record("data.trades.BINANCE.BTCUSDT");
        BusRecorder bars = h.Record("data.bars.BTCUSDT.BINANCE-1-MINUTE-LAST-EXTERNAL");
        BusRecorder mark = h.Record("data.mark.BINANCE.BTCUSDT");
        BusRecorder index = h.Record("data.index.BINANCE.BTCUSDT");
        BusRecorder funding = h.Record("data.funding.BINANCE.BTCUSDT");
        BusRecorder status = h.Record("data.status.BINANCE.BTCUSDT");
        TradeTick trade = Trade("50000.00", "0.100", TestOrders.T0);
        BarType barType = BarType.Parse("BTCUSDT.BINANCE-1-MINUTE-LAST-EXTERNAL");
        Bar bar = new(barType, Price.Parse("1.00"), Price.Parse("2.00"), Price.Parse("0.50"), Price.Parse("1.50"), Quantity.Parse("9.000"), TestOrders.T0, TestOrders.T0);
        MarkPriceUpdate markPrice = new(TestIds.BtcUsdt, Price.Parse("50001.00"), TestOrders.T0, TestOrders.T0);
        IndexPriceUpdate indexPrice = new(TestIds.BtcUsdt, Price.Parse("50002.00"), TestOrders.T0, TestOrders.T0);
        FundingRateUpdate fundingRate = new(TestIds.BtcUsdt, 0.0001m, null, TestOrders.T0, TestOrders.T0);
        InstrumentStatus halted = new(TestIds.BtcUsdt, MarketStatus.Halted, "maintenance", TestOrders.T0, TestOrders.T0);

        h.Engine.Process(trade);
        h.Engine.Process(bar);
        h.Engine.Process(markPrice);
        h.Engine.Process(indexPrice);
        h.Engine.Process(fundingRate);
        h.Engine.Process(halted);

        Assert.Equal(trade, Assert.Single(trades.Messages));
        Assert.Equal(bar, Assert.Single(bars.Messages));
        Assert.Same(markPrice, Assert.Single(mark.Messages));
        Assert.Same(indexPrice, Assert.Single(index.Messages));
        Assert.Same(fundingRate, Assert.Single(funding.Messages));
        Assert.Same(halted, Assert.Single(status.Messages));
        Assert.Equal(6, all.Messages.Count);
        Assert.Equal(trade, h.Cache.TradeTick(TestIds.BtcUsdt));
        Assert.Equal(bar, h.Cache.Bar(barType));
        Assert.Same(markPrice, h.Cache.MarkPrice(TestIds.BtcUsdt));
        Assert.Same(indexPrice, h.Cache.IndexPrice(TestIds.BtcUsdt));
        Assert.Same(fundingRate, h.Cache.FundingRate(TestIds.BtcUsdt));
    }

    [Fact]
    public void Instrument_from_a_client_is_cached_and_reaches_both_exact_and_venue_wide_subscribers()
    {
        Harness h = new();
        BusRecorder exact = h.Record(Topics.Instrument(TestIds.EthUsdt));
        BusRecorder venueWide = h.Record(Topics.Instruments(TestIds.Binance));
        BusRecorder otherVenue = h.Record(Topics.Instruments(TestIds.Bybit));
        CurrencyPair instrument = TestInstruments.EthUsdt();

        h.Client.Push(instrument);

        Assert.Same(instrument, h.Cache.Instrument(TestIds.EthUsdt));
        Assert.Same(instrument, Assert.Single(exact.Messages));
        Assert.Same(instrument, Assert.Single(venueWide.Messages));
        Assert.Empty(otherVenue.Messages);
    }

    [Fact]
    public void Signal_and_custom_data_are_published_on_their_topics()
    {
        Harness h = new();
        BusRecorder signals = h.Record("signal.momentum");
        BusRecorder custom = h.Record("data.custom.NewsItem");
        BusRecorder customWithMetadata = h.Record("data.custom.NewsItem.lang=en");
        Signal signal = new("momentum", 0.75m, TestOrders.T0, TestOrders.T0);
        NewsItem news = new("hello", TestOrders.T0, TestOrders.T0);

        h.Engine.Process(signal);
        h.Engine.Process(news);
        h.Engine.ProcessCustom(DataType.Of<NewsItem>(new Dictionary<string, string> { ["lang"] = "en" }), news);

        Assert.Same(signal, Assert.Single(signals.Messages));
        Assert.Same(news, Assert.Single(custom.Messages));
        Assert.Same(news, Assert.Single(customWithMetadata.Messages));
    }

    [Fact]
    public void Out_of_sequence_ticks_are_dropped_when_sequence_validation_is_on()
    {
        Harness h = new(new DataEngineConfig { ValidateDataSequence = true });
        BusRecorder quotes = h.Record(Topics.Quotes(TestIds.BtcUsdt));

        h.Engine.Process(Quote("100.00", "100.10", At(0, 10)));
        h.Engine.Process(Quote("99.00", "99.10", At(0, 5)));
        h.Engine.Process(Quote("101.00", "101.10", At(0, 10)));

        Assert.Equal(2, quotes.Messages.Count);
        Assert.Equal(2, h.Cache.QuoteTickCount(TestIds.BtcUsdt));
        Assert.Equal(Price.Parse("101.00"), h.Cache.QuoteTick(TestIds.BtcUsdt)!.Value.Bid);
    }

    [Fact]
    public void Out_of_sequence_ticks_pass_when_sequence_validation_is_off()
    {
        Harness h = new();

        h.Engine.Process(Quote("100.00", "100.10", At(0, 10)));
        h.Engine.Process(Quote("99.00", "99.10", At(0, 5)));

        Assert.Equal(2, h.Cache.QuoteTickCount(TestIds.BtcUsdt));
    }

    [Fact]
    public void Revised_bar_is_dropped_unless_that_bar_type_is_subscribed()
    {
        Harness h = new();
        BarType barType = BarType.Parse("BTCUSDT.BINANCE-1-MINUTE-LAST-EXTERNAL");
        Bar revision = new(barType, Price.Parse("1.00"), Price.Parse("2.00"), Price.Parse("0.50"), Price.Parse("1.50"), Quantity.Parse("9.000"), At(1, 0), At(1, 0), IsRevision: true);

        h.Engine.Process(revision);

        Assert.Equal(0, h.Cache.BarCount(barType));
    }

    [Fact]
    public void Revised_bar_replaces_the_bar_with_the_same_timestamp_when_subscribed()
    {
        Harness h = new();
        BarType barType = BarType.Parse("BTCUSDT.BINANCE-1-MINUTE-LAST-EXTERNAL");
        h.Execute(new SubscribeBars(barType, null, Guid.NewGuid(), TestOrders.T0));
        Bar original = new(barType, Price.Parse("1.00"), Price.Parse("2.00"), Price.Parse("0.50"), Price.Parse("1.50"), Quantity.Parse("9.000"), At(1, 0), At(1, 0));
        Bar revision = original with { Close = Price.Parse("1.75"), IsRevision = true };

        h.Engine.Process(original);
        h.Engine.Process(revision);

        Assert.Equal(1, h.Cache.BarCount(barType));
        Assert.Equal(Price.Parse("1.75"), h.Cache.Bar(barType)!.Value.Close);
        Assert.IsType<SubscribeBars>(Assert.Single(h.Client.Subscriptions));
    }

    [Fact]
    public void Request_is_routed_and_its_response_is_cached_and_published_to_the_requester()
    {
        Harness h = new();
        ActorId requester = new("Monitor-001");
        BusRecorder responses = h.Record(Topics.DataResponses(requester));
        TradeTick historical = Trade("48000.00", "0.500", At(0, 1));
        h.Client.RequestResult = [historical];
        RequestTradeTicks request = new(TestIds.BtcUsdt, null, null, 10, null, Guid.NewGuid(), TestOrders.T0) { Requester = requester };

        h.Bus.Send(Endpoints.DataEngineRequest, request);

        Assert.Same(request, Assert.Single(h.Client.Requests));
        DataResponse response = Assert.IsType<DataResponse>(Assert.Single(responses.Messages));
        Assert.Equal(request.CommandId, response.CorrelationId);
        Assert.False(response.IsError);
        Assert.Equal(historical, h.Cache.TradeTick(TestIds.BtcUsdt));
        Assert.Equal(1, h.Engine.RequestCount);
        Assert.Equal(1, h.Engine.ResponseCount);
    }

    [Fact]
    public void Request_without_a_client_is_answered_with_an_error_response()
    {
        Harness h = new(registerClient: false);
        ActorId requester = new("Monitor-001");
        BusRecorder responses = h.Record(Topics.DataResponses(requester));
        RequestTradeTicks request = new(TestIds.BtcUsdt, null, null, null, null, Guid.NewGuid(), TestOrders.T0) { Requester = requester };

        h.Bus.Send(Endpoints.DataEngineRequest, request);

        DataResponse response = Assert.IsType<DataResponse>(Assert.Single(responses.Messages));
        Assert.True(response.IsError);
        Assert.Equal(request.CommandId, response.CorrelationId);
    }

    // Why: a client whose request throws was written to the log and nothing else. The actor that asked stayed in
    // _pendingRequests for the rest of the node's life, which in a document strategy is a warm-up that never completes.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Request_that_fails_in_the_client_is_answered_with_the_reason(bool failAsynchronously)
    {
        Harness h = new();
        ActorId requester = new("Monitor-001");
        BusRecorder responses = h.Record(Topics.DataResponses(requester));
        h.Client.RequestFailure = new InvalidOperationException("the venue refused the history request");
        h.Client.FailAsynchronously = failAsynchronously;
        RequestTradeTicks request = new(TestIds.BtcUsdt, null, null, 10, null, Guid.NewGuid(), TestOrders.T0) { Requester = requester };

        h.Bus.Send(Endpoints.DataEngineRequest, request);

        // An adapter that fails after its first await answers a moment later, on the kernel thread.
        for (int i = 0; i < 100 && responses.Messages.Count == 0; i++)
        {
            await Task.Delay(10);
        }

        DataResponse response = Assert.IsType<DataResponse>(Assert.Single(responses.Messages));
        Assert.Equal(request.CommandId, response.CorrelationId);
        Assert.True(response.IsError);
        Assert.Contains("the venue refused the history request", response.Error!, StringComparison.Ordinal);
        Assert.Empty(response.Data);
        Assert.Equal(1, h.Engine.ResponseCount);
    }

    [Fact]
    public void Client_without_history_support_answers_with_an_error_instead_of_silence()
    {
        Harness h = new();
        ActorId requester = new("Monitor-001");
        BusRecorder responses = h.Record(Topics.DataResponses(requester));
        RequestBars request = new(BarType.Parse("BTCUSDT.BINANCE-1-MINUTE-LAST-EXTERNAL"), null, null, null, null, Guid.NewGuid(), TestOrders.T0) { Requester = requester };

        h.Bus.Send(Endpoints.DataEngineRequest, request);

        DataResponse response = Assert.IsType<DataResponse>(Assert.Single(responses.Messages));
        Assert.True(response.IsError);
        Assert.Empty(response.Data);
    }

    [Fact]
    public void Internal_trade_bars_subscribe_to_trades_and_are_built_published_and_cached()
    {
        Harness h = new();
        BarType barType = BarType.Parse("BTCUSDT.BINANCE-2-TICK-LAST-INTERNAL");
        BusRecorder bars = h.Record(Topics.Bars(barType));

        h.Execute(new SubscribeBars(barType, null, Guid.NewGuid(), TestOrders.T0));
        h.Client.Push(Trade("100.00", "1.000", At(0, 1), "T-1"));
        h.Client.Push(Trade("101.00", "2.000", At(0, 2), "T-2"));

        // The venue is asked for trades, never for the internal bar type itself.
        Assert.IsType<SubscribeTradeTicks>(Assert.Single(h.Client.Subscriptions));
        Bar bar = Assert.IsType<Bar>(Assert.Single(bars.Messages));
        Assert.Equal((Price.Parse("100.00"), Price.Parse("101.00"), Price.Parse("100.00"), Price.Parse("101.00"), Quantity.Parse("3.000")), (bar.Open, bar.High, bar.Low, bar.Close, bar.Volume));
        Assert.Equal(bar, h.Cache.Bar(barType));
    }

    [Fact]
    public void Internal_quote_bars_subscribe_to_quotes_and_ignore_trades()
    {
        Harness h = new();
        BarType barType = BarType.Parse("BTCUSDT.BINANCE-2-TICK-BID-INTERNAL");
        BusRecorder bars = h.Record(Topics.Bars(barType));

        h.Execute(new SubscribeBars(barType, null, Guid.NewGuid(), TestOrders.T0));
        h.Client.Push(Trade("500.00", "1.000", At(0, 1)));
        h.Client.Push(Quote("100.00", "100.10", At(0, 1)));
        h.Client.Push(Trade("500.00", "1.000", At(0, 2), "T-2"));
        h.Client.Push(Quote("99.00", "99.10", At(0, 2)));

        Assert.IsType<SubscribeQuoteTicks>(Assert.Single(h.Client.Subscriptions));
        Bar bar = Assert.IsType<Bar>(Assert.Single(bars.Messages));
        Assert.Equal((Price.Parse("100.00"), Price.Parse("99.00")), (bar.Open, bar.Close));
    }

    [Fact]
    public void Internal_time_bars_are_closed_by_the_clock()
    {
        Harness h = new();
        BarType barType = BarType.Parse("BTCUSDT.BINANCE-1-MINUTE-LAST-INTERNAL");
        BusRecorder bars = h.Record(Topics.Bars(barType));
        h.Execute(new SubscribeBars(barType, null, Guid.NewGuid(), TestOrders.T0));

        h.Client.Push(Trade("100.00", "1.000", At(0, 10), "T-1"));
        h.Client.Push(Trade("98.00", "1.000", At(0, 20), "T-2"));
        h.Clock.AdvanceAndRun(At(1, 0));

        Bar bar = Assert.IsType<Bar>(Assert.Single(bars.Messages));
        Assert.Equal((Price.Parse("100.00"), Price.Parse("98.00"), At(1, 0)), (bar.Open, bar.Low, bar.TsEvent));
    }

    [Fact]
    public void Unsubscribing_internal_bars_stops_the_aggregator_and_releases_the_tick_stream()
    {
        Harness h = new();
        BarType barType = BarType.Parse("BTCUSDT.BINANCE-1-MINUTE-LAST-INTERNAL");
        BusRecorder bars = h.Record(Topics.Bars(barType));
        h.Execute(new SubscribeBars(barType, null, Guid.NewGuid(), TestOrders.T0));
        h.Client.Push(Trade("100.00", "1.000", At(0, 10)));

        h.Execute(new UnsubscribeBars(barType, null, Guid.NewGuid(), TestOrders.T0));
        h.Clock.AdvanceAndRun(At(1, 0));

        Assert.IsType<UnsubscribeTradeTicks>(Assert.Single(h.Client.Unsubscriptions));
        Assert.Empty(bars.Messages);
        Assert.Equal(0, h.Clock.TimerCount);
    }

    [Fact]
    public void Tick_stream_shared_with_an_actor_survives_the_end_of_internal_bars()
    {
        Harness h = new();
        BarType barType = BarType.Parse("BTCUSDT.BINANCE-2-TICK-LAST-INTERNAL");
        h.Execute(new SubscribeTradeTicks(TestIds.BtcUsdt, null, Guid.NewGuid(), TestOrders.T0));
        h.Execute(new SubscribeBars(barType, null, Guid.NewGuid(), TestOrders.T0));

        h.Execute(new UnsubscribeBars(barType, null, Guid.NewGuid(), TestOrders.T0));

        Assert.Single(h.Client.Subscriptions);
        Assert.Empty(h.Client.Unsubscriptions);
    }

    [Fact]
    public void Internal_bars_for_an_instrument_missing_from_the_cache_are_not_started()
    {
        Harness h = new();
        BarType barType = BarType.Parse("ETHUSDT.BINANCE-2-TICK-LAST-INTERNAL");

        h.Execute(new SubscribeBars(barType, null, Guid.NewGuid(), TestOrders.T0));

        Assert.Empty(h.Client.Subscriptions);
    }

    [Fact]
    public void External_minute_bars_feed_a_larger_internal_time_bar()
    {
        Harness h = new();
        BarType fiveMinutes = BarType.Parse("BTCUSDT.BINANCE-5-MINUTE-LAST-INTERNAL");
        BarType oneMinute = BarType.Parse("BTCUSDT.BINANCE-1-MINUTE-LAST-EXTERNAL");
        BusRecorder bars = h.Record(Topics.Bars(fiveMinutes));
        h.Execute(new SubscribeBars(fiveMinutes, null, Guid.NewGuid(), TestOrders.T0));

        h.Engine.Process(new Bar(oneMinute, Price.Parse("10.00"), Price.Parse("12.00"), Price.Parse("9.00"), Price.Parse("11.00"), Quantity.Parse("1.000"), At(1, 0), At(1, 0)));
        h.Engine.Process(new Bar(oneMinute, Price.Parse("11.00"), Price.Parse("15.00"), Price.Parse("10.00"), Price.Parse("14.00"), Quantity.Parse("2.000"), At(2, 0), At(2, 0)));
        h.Clock.AdvanceAndRun(At(5, 0));

        Bar bar = Assert.IsType<Bar>(Assert.Single(bars.Messages));
        Assert.Equal((Price.Parse("10.00"), Price.Parse("15.00"), Price.Parse("9.00"), Price.Parse("14.00"), Quantity.Parse("3.000")), (bar.Open, bar.High, bar.Low, bar.Close, bar.Volume));
    }

    [Fact]
    public void Book_deltas_build_the_cached_book_and_are_published_as_a_batch()
    {
        Harness h = new();
        BusRecorder deltas = h.Record(Topics.BookDeltas(TestIds.BtcUsdt));
        h.Execute(new SubscribeOrderBookDeltas(TestIds.BtcUsdt, BookType.L2, 0, null, Guid.NewGuid(), TestOrders.T0));
        OrderBookDelta bid = new(TestIds.BtcUsdt, BookAction.Add, new BookOrder(OrderSide.Buy, Price.Parse("49990.00"), Quantity.Parse("2.000"), 1), RecordFlags.None, 1, TestOrders.T0, TestOrders.T0);
        OrderBookDelta ask = new(TestIds.BtcUsdt, BookAction.Add, new BookOrder(OrderSide.Sell, Price.Parse("50010.00"), Quantity.Parse("3.000"), 2), RecordFlags.Last, 2, TestOrders.T0, TestOrders.T0);

        h.Client.Push(bid);
        h.Client.Push(ask);

        OrderBook book = h.Cache.OrderBook(TestIds.BtcUsdt)!;
        Assert.Equal(Price.Parse("49990.00"), book.BestBidPrice);
        Assert.Equal(Price.Parse("50010.00"), book.BestAskPrice);
        Assert.Equal(20.00m, book.Spread);
        Assert.Equal(2, deltas.Messages.Count);
        Assert.Equal(bid, Assert.Single(Assert.IsType<OrderBookDeltas>(deltas.Messages[0]).Deltas));
    }

    [Fact]
    public void Book_snapshots_are_published_on_a_timer_from_the_delta_fed_book()
    {
        Harness h = new();
        BusRecorder snapshots = h.Record(Topics.BookSnapshots(TestIds.BtcUsdt));
        h.Execute(new SubscribeOrderBookSnapshots(TestIds.BtcUsdt, BookType.L2, 10, TimeSpan.FromSeconds(1), null, Guid.NewGuid(), TestOrders.T0));
        h.Client.Push(new OrderBookDelta(TestIds.BtcUsdt, BookAction.Add, new BookOrder(OrderSide.Buy, Price.Parse("49990.00"), Quantity.Parse("2.000"), 1), RecordFlags.None, 1, TestOrders.T0, TestOrders.T0));

        h.Clock.AdvanceAndRun(At(0, 2));

        // Snapshots are local; the venue only ever sees a delta subscription.
        Assert.IsType<SubscribeOrderBookDeltas>(Assert.Single(h.Client.Subscriptions));
        Assert.Equal(2, snapshots.Messages.Count);
        Assert.Equal(Price.Parse("49990.00"), Assert.IsType<OrderBook>(snapshots.Messages[0]).BestBidPrice);
    }

    [Fact]
    public void Unsubscribing_snapshots_stops_the_timer_and_the_delta_feed()
    {
        Harness h = new();
        h.Execute(new SubscribeOrderBookSnapshots(TestIds.BtcUsdt, BookType.L2, 10, TimeSpan.FromSeconds(1), null, Guid.NewGuid(), TestOrders.T0));

        h.Execute(new UnsubscribeOrderBookSnapshots(TestIds.BtcUsdt, null, Guid.NewGuid(), TestOrders.T0));

        Assert.Equal(0, h.Clock.TimerCount);
        Assert.IsType<UnsubscribeOrderBookDeltas>(Assert.Single(h.Client.Unsubscriptions));
    }

    [Fact]
    public void Resubscribe_replays_every_active_subscription_of_that_client()
    {
        Harness h = new();
        SubscribeQuoteTicks quotes = SubscribeQuotes();
        SubscribeTradeTicks trades = new(TestIds.BtcUsdt, null, Guid.NewGuid(), TestOrders.T0);
        SubscribeTradeTicks dropped = new(TestIds.EthUsdt, null, Guid.NewGuid(), TestOrders.T0);
        h.Execute(quotes);
        h.Execute(trades);
        h.Execute(dropped);
        h.Execute(new UnsubscribeTradeTicks(TestIds.EthUsdt, null, Guid.NewGuid(), TestOrders.T0));
        h.Client.Subscriptions.Clear();

        h.Engine.Resubscribe(h.Client);

        Assert.Equal(new SubscribeCommand[] { quotes, trades }, h.Client.Subscriptions);
    }

    [Fact]
    public void Engine_lifecycle_drives_its_clients_and_reset_forgets_subscriptions()
    {
        Harness h = new();
        h.Execute(SubscribeQuotes());

        h.Engine.Start();
        ComponentState afterStart = h.Client.State;
        h.Engine.Stop();
        ComponentState afterStop = h.Client.State;
        h.Engine.Reset();

        Assert.Equal(ComponentState.Running, afterStart);
        Assert.Equal(ComponentState.Stopped, afterStop);
        Assert.Empty(h.Engine.SubscribedTopics);
        Assert.Equal(0, h.Engine.CommandCount);
    }

    [Fact]
    public void Message_that_is_not_a_data_command_is_ignored()
    {
        Harness h = new();

        h.Execute("noise");

        Assert.Empty(h.Client.Subscriptions);
        Assert.Equal(1, h.Engine.CommandCount);
    }

    private sealed record NewsItem(string Headline, UnixNanos TsEvent, UnixNanos TsInit) : CustomData(TsEvent, TsInit);
}
