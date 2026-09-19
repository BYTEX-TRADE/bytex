using System.Text.Json;
using Bytex.Adapters.Binance;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Primitives;

namespace Bytex.Adapters.Tests.Binance;

// Why: these are the messages a live strategy trades on. Each documented stream payload must become the right
// engine type with the right side, price, size and venue timestamp, and subscriptions must survive a reconnect.
// The venue is a stub on 127.0.0.1 that speaks the combined-stream protocol; payloads follow the Binance docs.
public sealed class BinanceDataClientStreamTests
{
    private static readonly InstrumentId _spotBtc = InstrumentId.Parse("BTCUSDT.BINANCE");
    private static readonly InstrumentId _perpBtc = InstrumentId.Parse("BTCUSDT-PERP.BINANCE");

    private sealed class Rig : IAsyncDisposable
    {
        private Rig(LoopbackServer server, TestKernel kernel, BinanceDataClient client, RecordingDataSink sink, WsSession session, WsSession? market)
        {
            Server = server;
            Kernel = kernel;
            Client = client;
            Sink = sink;
            Session = session;
            Market = market;
        }

        public LoopbackServer Server { get; }

        public TestKernel Kernel { get; }

        public BinanceDataClient Client { get; }

        public RecordingDataSink Sink { get; }

        /// <summary>Spot: the single combined stream. Futures: the /public route (book tickers, trades, depth).</summary>
        public WsSession Session { get; }

        /// <summary>Futures only: the /market route (klines, mark prices, aggregated trades).</summary>
        public WsSession? Market { get; }

        public static async Task<Rig> ConnectAsync(BinanceAccountType type, bool handleRevisedBars = false)
        {
            Routes routes = new Routes()
                .On("GET", "/api/v3/exchangeInfo", BinancePayloads.SpotExchangeInfo)
                .On("GET", "/fapi/v1/exchangeInfo", BinancePayloads.FuturesExchangeInfo)
                .On("GET", "/api/v3/depth", BinancePayloads.DepthSnapshot)
                .On("GET", "/fapi/v1/depth", BinancePayloads.DepthSnapshot);
            LoopbackServer server = new(routes.Handle);
            TestKernel kernel = new();
            BinanceDataClientConfig config = new()
            {
                AccountType = type,
                BaseUrlHttp = server.HttpBase,
                BaseUrlWs = server.WsBase,
                HandleRevisedBars = handleRevisedBars,
                InstrumentProvider = new Core.Adapters.InstrumentProviderConfig { LoadAll = true },
            };
            BinanceDataClient client = new(new ClientId("BINANCE"), config, kernel.Services);
            RecordingDataSink sink = new();
            client.AttachSink(sink);
            await client.ConnectAsync(CancellationToken.None).WaitAsync(Wait.Timeout);
            WsSession session = await server.NextSessionAsync();
            WsSession? market = null;
            if (type == BinanceAccountType.UsdMFutures)
            {
                // The server hands sessions over as their handshakes finish, which is not always the order the client opened them in.
                WsSession other = await server.NextSessionAsync();
                (session, market) = session.Path.StartsWith("/market", StringComparison.Ordinal) ? (other, session) : (session, other);
            }

            return new Rig(server, kernel, client, sink, session, market);
        }

        /// <summary>Next control message from the client as "METHOD stream,stream" with the streams sorted.</summary>
        public async Task<string> NextControlMessageAsync(WsSession? session = null)
        {
            using JsonDocument doc = JsonDocument.Parse(await (session ?? Session).ReceiveTextAsync());
            return doc.RootElement.GetProperty("method").GetString() + " " + string.Join(",", doc.RootElement.GetProperty("params").EnumerateArray().Select(p => p.GetString()!).Order(StringComparer.Ordinal));
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            Kernel.Dispose();
            await Server.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(BinanceAccountType.Spot, 2)]
    [InlineData(BinanceAccountType.UsdMFutures, 2)]
    public async Task Connecting_loads_instruments_publishes_them_and_opens_the_combined_stream_route(BinanceAccountType type, int expectedInstruments)
    {
        await using Rig rig = await Rig.ConnectAsync(type);

        Assert.Equal(type == BinanceAccountType.Spot ? "/stream" : "/public/stream", rig.Session.Path);
        Assert.Equal(type == BinanceAccountType.Spot ? null : "/market/stream", rig.Market?.Path);
        Assert.True(rig.Client.IsConnected);
        Assert.Equal("connected", await rig.Sink.NextConnectionEventAsync());
        Assert.Equal(expectedInstruments, rig.Sink.Instruments.Count);
        Assert.Equal(type == BinanceAccountType.Spot ? "/api/v3/exchangeInfo" : "/fapi/v1/exchangeInfo", Assert.Single(rig.Server.Requests).Path);
    }

    [Fact]
    public async Task A_quote_subscription_requests_the_lower_case_bookTicker_stream_once()
    {
        await using Rig rig = await Rig.ConnectAsync(BinanceAccountType.UsdMFutures);

        await rig.Client.SubscribeAsync(Commands.Quotes(_perpBtc), CancellationToken.None);
        await rig.Client.SubscribeAsync(Commands.Quotes(_perpBtc), CancellationToken.None);
        await rig.Client.SubscribeAsync(Commands.Trades(_perpBtc), CancellationToken.None);

        Assert.Equal("SUBSCRIBE btcusdt@bookTicker", await rig.NextControlMessageAsync());
        Assert.Equal("SUBSCRIBE btcusdt@trade", await rig.NextControlMessageAsync()); // no duplicate bookTicker request in between
    }

    [Fact]
    public async Task A_spot_bookTicker_becomes_a_quote_stamped_with_the_clock_because_the_payload_has_no_event_time()
    {
        await using Rig rig = await Rig.ConnectAsync(BinanceAccountType.Spot);
        await rig.Client.SubscribeAsync(Commands.Quotes(_spotBtc), CancellationToken.None);

        await rig.Session.SendTextAsync(BinancePayloads.SubscriptionAck);
        await rig.Session.SendTextAsync(BinancePayloads.SpotBookTicker);

        QuoteTick quote = Assert.IsType<QuoteTick>(await rig.Sink.NextDataAsync());
        Assert.Equal(_spotBtc, quote.InstrumentId);
        Assert.Equal(new Price(25_000.35m, 2), quote.Bid);
        Assert.Equal(new Price(25_000.36m, 2), quote.Ask);
        Assert.Equal(new Quantity(31.21m, 5), quote.BidSize);
        Assert.Equal(new Quantity(40.66m, 5), quote.AskSize);
        Assert.Equal(TestKernel.Now, quote.TsEvent);
        Assert.Equal(TestKernel.Now, quote.TsInit);
    }

    [Fact]
    public async Task A_futures_bookTicker_maps_to_the_perpetual_id_and_carries_the_venue_event_time_in_nanoseconds()
    {
        await using Rig rig = await Rig.ConnectAsync(BinanceAccountType.UsdMFutures);

        await rig.Session.SendTextAsync(BinancePayloads.FuturesBookTicker);

        QuoteTick quote = Assert.IsType<QuoteTick>(await rig.Sink.NextDataAsync());
        Assert.Equal(_perpBtc, quote.InstrumentId);
        Assert.Equal(new Price(25_000.3m, 1), quote.Bid);
        Assert.Equal(new Price(25_000.4m, 1), quote.Ask);
        Assert.Equal(new Quantity(31.21m, 3), quote.BidSize);
        Assert.Equal(1_568_014_460_893_000_000L, quote.TsEvent.Value);
        Assert.Equal(TestKernel.Now, quote.TsInit);
    }

    [Fact]
    public async Task The_trade_aggressor_is_the_seller_when_the_buyer_was_the_maker_and_the_buyer_otherwise()
    {
        await using Rig rig = await Rig.ConnectAsync(BinanceAccountType.Spot);

        await rig.Session.SendTextAsync(BinancePayloads.SpotTradeBuyerIsMaker);
        await rig.Session.SendTextAsync(BinancePayloads.SpotTradeBuyerIsTaker);

        TradeTick first = Assert.IsType<TradeTick>(await rig.Sink.NextDataAsync());
        TradeTick second = Assert.IsType<TradeTick>(await rig.Sink.NextDataAsync());
        Assert.Equal(AggressorSide.Seller, first.Aggressor);
        Assert.Equal(new Price(25_000.10m, 2), first.Price);
        Assert.Equal(new Quantity(0.1m, 5), first.Size);
        Assert.Equal(new TradeId("12345"), first.TradeId);
        Assert.Equal(1_672_515_782_134_000_000L, first.TsEvent.Value); // trade time "T", not event time "E"
        Assert.Equal(AggressorSide.Buyer, second.Aggressor);
        Assert.Equal(new TradeId("12346"), second.TradeId);
    }

    [Fact]
    public async Task Only_the_closed_kline_becomes_a_bar_and_it_is_stamped_with_the_close_of_its_interval()
    {
        await using Rig rig = await Rig.ConnectAsync(BinanceAccountType.Spot);
        BarType barType = BarType.Parse("BTCUSDT.BINANCE-1-MINUTE-LAST-EXTERNAL");
        await rig.Client.SubscribeAsync(Commands.Bars(barType), CancellationToken.None);
        Assert.Equal("SUBSCRIBE btcusdt@kline_1m", await rig.NextControlMessageAsync());

        await rig.Session.SendTextAsync(BinancePayloads.KlineOpen);
        await rig.Session.SendTextAsync(BinancePayloads.KlineClosed);

        Bar bar = Assert.IsType<Bar>(await rig.Sink.NextDataAsync());
        Assert.Equal(barType, bar.BarType);
        Assert.Equal(new Price(25_000.00m, 2), bar.Open);
        Assert.Equal(new Price(25_030.00m, 2), bar.High);
        Assert.Equal(new Price(24_990.00m, 2), bar.Low);
        Assert.Equal(new Price(25_015.50m, 2), bar.Close);
        Assert.Equal(new Quantity(18.75m, 5), bar.Volume);
        Assert.Equal(1_672_515_840_000_000_000L, bar.TsEvent.Value); // 1672515839999 ms close time + 1 ms
        Assert.False(bar.IsRevision);
    }

    [Fact]
    public async Task With_handleRevisedBars_the_forming_kline_is_forwarded_and_flagged_as_a_revision()
    {
        await using Rig rig = await Rig.ConnectAsync(BinanceAccountType.Spot, handleRevisedBars: true);
        await rig.Client.SubscribeAsync(Commands.Bars(BarType.Parse("BTCUSDT.BINANCE-1-MINUTE-LAST-EXTERNAL")), CancellationToken.None);
        await rig.NextControlMessageAsync();

        await rig.Session.SendTextAsync(BinancePayloads.KlineOpen);
        await rig.Session.SendTextAsync(BinancePayloads.KlineClosed);

        Bar forming = Assert.IsType<Bar>(await rig.Sink.NextDataAsync());
        Bar closed = Assert.IsType<Bar>(await rig.Sink.NextDataAsync());
        Assert.True(forming.IsRevision);
        Assert.Equal(new Price(25_010.00m, 2), forming.Close);
        Assert.False(closed.IsRevision);
    }

    [Fact]
    public async Task A_book_subscription_publishes_a_rest_snapshot_first_and_then_stream_deltas()
    {
        await using Rig rig = await Rig.ConnectAsync(BinanceAccountType.Spot);

        await rig.Client.SubscribeAsync(Commands.Book(_spotBtc, depth: 5000), CancellationToken.None);
        await rig.Session.SendTextAsync(BinancePayloads.DepthUpdate);

        Assert.Equal("SUBSCRIBE btcusdt@depth@100ms", await rig.NextControlMessageAsync());
        RecordedRequest depthRequest = Assert.Single(rig.Server.RequestsTo("/api/v3/depth"));
        Assert.Equal("BTCUSDT", depthRequest.Query("symbol"));
        Assert.Equal("1000", depthRequest.Query("limit")); // the venue maximum, not the 5000 that was asked for

        OrderBookDeltas snapshot = Assert.IsType<OrderBookDeltas>(await rig.Sink.NextDataAsync());
        Assert.True(snapshot.IsSnapshot);
        Assert.Equal(1_027_024UL, snapshot.Sequence);
        Assert.Equal([BookAction.Clear, BookAction.Add, BookAction.Add, BookAction.Add], snapshot.Deltas.Select(d => d.Action));
        Assert.Equal([OrderSide.Buy, OrderSide.Buy, OrderSide.Sell], snapshot.Deltas.Skip(1).Select(d => d.Order.Side));
        Assert.Equal(new Price(25_000.02m, 2), snapshot.Deltas[3].Order.Price);
        Assert.Equal(new Quantity(12m, 5), snapshot.Deltas[3].Order.Size);

        OrderBookDeltas update = Assert.IsType<OrderBookDeltas>(await rig.Sink.NextDataAsync());
        Assert.False(update.IsSnapshot);
        Assert.Equal(160UL, update.Sequence); // final update id "u"
        Assert.Equal(1_672_515_782_136_000_000L, update.TsEvent.Value);
        Assert.Equal([BookAction.Update, BookAction.Delete, BookAction.Update], update.Deltas.Select(d => d.Action)); // zero size removes the level
        Assert.Equal([OrderSide.Buy, OrderSide.Buy, OrderSide.Sell], update.Deltas.Select(d => d.Order.Side));
        Assert.Equal(new Price(24_999.99m, 2), update.Deltas[1].Order.Price);
    }

    [Fact]
    public async Task One_markPrice_message_yields_mark_price_index_price_and_funding_rate_updates()
    {
        await using Rig rig = await Rig.ConnectAsync(BinanceAccountType.UsdMFutures);
        await rig.Client.SubscribeAsync(Commands.Mark(_perpBtc), CancellationToken.None);
        Assert.Equal("SUBSCRIBE btcusdt@markPrice@1s", await rig.NextControlMessageAsync(rig.Market));

        await rig.Market!.SendTextAsync(BinancePayloads.MarkPriceUpdate);

        MarkPriceUpdate mark = Assert.IsType<MarkPriceUpdate>(await rig.Sink.NextDataAsync());
        IndexPriceUpdate index = Assert.IsType<IndexPriceUpdate>(await rig.Sink.NextDataAsync());
        FundingRateUpdate funding = Assert.IsType<FundingRateUpdate>(await rig.Sink.NextDataAsync());
        Assert.Equal(_perpBtc, mark.InstrumentId);
        Assert.Equal(11_794.2m, mark.Value.Value); // 11794.15 on a 0.1 tick grid
        Assert.Equal(11_784.6m, index.Value.Value);
        Assert.Equal(0.00038167m, funding.Rate);
        Assert.Equal(1_562_306_400_000_000_000L, funding.NextFundingTime!.Value.Value);
        Assert.Equal(1_562_305_380_000_000_000L, mark.TsEvent.Value);
    }

    [Fact]
    public async Task On_futures_klines_go_to_the_market_route_and_quotes_to_the_public_route()
    {
        // The venue acknowledges a kline subscription on the public route and then never delivers it, so the route is part of the contract.
        await using Rig rig = await Rig.ConnectAsync(BinanceAccountType.UsdMFutures);
        BarType barType = BarType.Parse("BTCUSDT-PERP.BINANCE-1-MINUTE-LAST-EXTERNAL");

        await rig.Client.SubscribeAsync(Commands.Bars(barType), CancellationToken.None);
        await rig.Client.SubscribeAsync(Commands.Quotes(_perpBtc), CancellationToken.None);

        Assert.Equal("SUBSCRIBE btcusdt@kline_1m", await rig.NextControlMessageAsync(rig.Market));
        Assert.Equal("SUBSCRIBE btcusdt@bookTicker", await rig.NextControlMessageAsync(rig.Session));

        await rig.Market!.SendTextAsync(BinancePayloads.KlineClosed);
        Bar bar = Assert.IsType<Bar>(await rig.Sink.NextDataAsync());
        Assert.Equal(barType, bar.BarType);
        Assert.Equal(new Price(25_015.5m, 1), bar.Close);

        await rig.Client.UnsubscribeAsync(new UnsubscribeBars(barType, null, Guid.NewGuid(), TestKernel.Now), CancellationToken.None);
        Assert.Equal("UNSUBSCRIBE btcusdt@kline_1m", await rig.NextControlMessageAsync(rig.Market));
    }

    [Fact]
    public async Task On_futures_a_dropped_route_resubscribes_only_its_own_streams()
    {
        await using Rig rig = await Rig.ConnectAsync(BinanceAccountType.UsdMFutures);
        await rig.Client.SubscribeAsync(Commands.Bars(BarType.Parse("BTCUSDT-PERP.BINANCE-1-MINUTE-LAST-EXTERNAL")), CancellationToken.None);
        await rig.Client.SubscribeAsync(Commands.Mark(_perpBtc), CancellationToken.None);
        await rig.Client.SubscribeAsync(Commands.Quotes(_perpBtc), CancellationToken.None);
        await rig.NextControlMessageAsync(rig.Market);
        await rig.NextControlMessageAsync(rig.Market);
        await rig.NextControlMessageAsync(rig.Session);

        rig.Market!.Drop();
        WsSession second = await rig.Server.NextSessionAsync();

        Assert.Equal("/market/stream", second.Path);
        Assert.Equal("SUBSCRIBE btcusdt@kline_1m,btcusdt@markPrice@1s", await rig.NextControlMessageAsync(second));
    }

    [Fact]
    public async Task Mark_price_subscriptions_are_refused_on_spot_with_a_reason_instead_of_being_ignored()
    {
        await using Rig rig = await Rig.ConnectAsync(BinanceAccountType.Spot);
        SubscribeMarkPrices command = Commands.Mark(_spotBtc);

        await rig.Client.SubscribeAsync(command, CancellationToken.None);

        (SubscribeCommand failed, string reason) = Assert.Single(rig.Sink.SubscriptionFailures);
        Assert.Same(command, failed);
        Assert.Contains("SubscribeMarkPrices", reason);
    }

    [Fact]
    public async Task Unsubscribing_sends_UNSUBSCRIBE_for_that_stream_only_when_it_was_subscribed()
    {
        await using Rig rig = await Rig.ConnectAsync(BinanceAccountType.Spot);
        await rig.Client.SubscribeAsync(Commands.Trades(_spotBtc), CancellationToken.None);

        await rig.Client.UnsubscribeAsync(new UnsubscribeQuoteTicks(_spotBtc, null, Guid.NewGuid(), TestKernel.Now), CancellationToken.None);
        await rig.Client.UnsubscribeAsync(new UnsubscribeTradeTicks(_spotBtc, null, Guid.NewGuid(), TestKernel.Now), CancellationToken.None);

        Assert.Equal("SUBSCRIBE btcusdt@trade", await rig.NextControlMessageAsync());
        Assert.Equal("UNSUBSCRIBE btcusdt@trade", await rig.NextControlMessageAsync());
    }

    [Fact]
    public async Task After_a_dropped_connection_every_active_stream_is_resubscribed_and_the_gap_is_reported()
    {
        await using Rig rig = await Rig.ConnectAsync(BinanceAccountType.Spot);
        await rig.Client.SubscribeAsync(Commands.Quotes(_spotBtc), CancellationToken.None);
        await rig.Client.SubscribeAsync(Commands.Trades(_spotBtc), CancellationToken.None);
        await rig.NextControlMessageAsync();
        await rig.NextControlMessageAsync();
        Assert.Equal("connected", await rig.Sink.NextConnectionEventAsync());

        rig.Session.Drop();
        WsSession second = await rig.Server.NextSessionAsync();
        string resubscription = await rig.NextControlMessageAsync(second);

        Assert.StartsWith("disconnected", await rig.Sink.NextConnectionEventAsync());
        Assert.Equal("SUBSCRIBE btcusdt@bookTicker,btcusdt@trade", resubscription);
    }

    [Fact]
    public async Task Malformed_and_unknown_symbol_messages_are_skipped_without_breaking_the_stream()
    {
        await using Rig rig = await Rig.ConnectAsync(BinanceAccountType.Spot);

        await rig.Session.SendTextAsync("{not json");
        await rig.Session.SendTextAsync(BinancePayloads.SpotBookTicker.Replace("BTCUSDT", "DOGEUSDT", StringComparison.Ordinal));
        await rig.Session.SendTextAsync(BinancePayloads.SpotTradeBuyerIsTaker);

        TradeTick first = Assert.IsType<TradeTick>(await rig.Sink.NextDataAsync());
        Assert.Equal(new TradeId("12346"), first.TradeId);
    }

    [Fact(Skip = "BUG: BinanceDataClient.Resolve maps every futures symbol as a perpetual (BTCUSDT_250926 -> BTCUSDT_250926-PERP), so all stream data for dated contracts is silently dropped")]
    public async Task Stream_data_for_a_dated_futures_contract_is_published_under_its_dated_instrument_id()
    {
        await using Rig rig = await Rig.ConnectAsync(BinanceAccountType.UsdMFutures);
        string datedTicker = BinancePayloads.FuturesBookTicker.Replace("BTCUSDT", "BTCUSDT_250926", StringComparison.Ordinal).Replace("btcusdt", "btcusdt_250926", StringComparison.Ordinal);

        await rig.Session.SendTextAsync(datedTicker);
        await rig.Session.SendTextAsync(BinancePayloads.FuturesBookTicker);

        QuoteTick first = Assert.IsType<QuoteTick>(await rig.Sink.NextDataAsync());
        Assert.Equal(InstrumentId.Parse("BTCUSDT_250926.BINANCE"), first.InstrumentId);
    }
}
