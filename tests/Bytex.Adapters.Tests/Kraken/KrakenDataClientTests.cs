using System.Text.Json;
using Bytex.Adapters.Kraken;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Adapters.Tests.Kraken;

// Why: Kraken's two sockets are two protocols. Measured on both on 2026-09-25:
//
//                       spot ws.kraken.com/v2              futures futures.kraken.com/ws/v1
//   subscribing         {method,params:{channel,...}}       {event,feed,product_ids}
//   a message           {channel,type,data:[...]}           {feed,...} with the fields at the top level
//   a timestamp         an ISO-8601 instant                 milliseconds
//   a candle            stamped by its CLOSE                stamped by its OPEN
//   a candle's length   a parameter                         part of the FEED NAME
//   a book delta        a list of levels per side           one price at a time, side as a word
//   what closes a bar   nothing - the venue never says      nothing either
//
// And the spot platform has a fact no other venue here has: it serves public market data and private data on two
// different hosts and REFUSES each on the other's, saying so in the error text. There is no connection that can
// carry both, which is why this client only ever opens the public one.
public sealed class KrakenDataClientTests
{
    private static readonly InstrumentId _btc = InstrumentId.Parse("BTC-USD.KRAKEN");
    private static readonly InstrumentId _perp = InstrumentId.Parse("PF_XBTUSD.KRAKEN");
    private static readonly BarType _spotBars = BarType.Parse("BTC-USD.KRAKEN-1-MINUTE-LAST-EXTERNAL");
    private static readonly BarType _futuresBars = BarType.Parse("PF_XBTUSD.KRAKEN-1-MINUTE-LAST-EXTERNAL");

    private sealed class SpotRig : IAsyncDisposable
    {
        private WsSession? _session;

        public SpotRig()
        {
            Server = new LoopbackServer(new Routes()
                .On("GET", "/0/public/AssetPairs", KrakenPayloads.AssetPairs)
                .On("GET", "/0/public/OHLC", KrakenPayloads.Ohlc)
                .On("GET", "/0/public/Trades", KrakenPayloads.Trades)
                .Handle);

            Kernel = new TestKernel(Logs);
            Client = new KrakenDataClient(new ClientId("KRAKEN"), new KrakenDataClientConfig
            {
                BaseUrlHttp = Server.HttpBase,
                BaseUrlWs = Server.WsBase,
                InstrumentProvider = new InstrumentProviderConfig { LoadAll = true },
            }, Kernel.Services);

            Client.AttachSink(Sink);
        }

        public RecordingLogs Logs { get; } = new();

        public LoopbackServer Server { get; }

        public TestKernel Kernel { get; }

        public KrakenDataClient Client { get; }

        public RecordingDataSink Sink { get; } = new();

        public WsSession Session => _session ?? throw new InvalidOperationException("not connected");

        public async Task<SpotRig> ConnectAsync()
        {
            await Client.ConnectAsync(CancellationToken.None).WaitAsync(Wait.Timeout);
            _session = await Server.NextSessionAsync();
            await _session.SendTextAsync(KrakenPayloads.Status);
            return this;
        }

        /// <summary>The next request the client sent that is not a keep-alive ping.</summary>
        public async Task<JsonElement> NextRequestAsync()
        {
            while (true)
            {
                using JsonDocument doc = JsonDocument.Parse(await Session.ReceiveTextAsync(Wait.Timeout));
                if (doc.RootElement.TryGetProperty("method", out JsonElement method) && method.GetString() != "ping")
                {
                    return doc.RootElement.Clone();
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisconnectAsync(CancellationToken.None);
            Client.Dispose();
            Kernel.Dispose();
            await Server.DisposeAsync();
        }
    }

    private sealed class FuturesRig : IAsyncDisposable
    {
        private WsSession? _session;

        public FuturesRig()
        {
            Server = new LoopbackServer(new Routes()
                .On("GET", "/derivatives/api/v3/instruments", KrakenPayloads.Instruments)
                .On("GET", "/derivatives/api/v3/feeschedules", KrakenPayloads.FeeSchedules)
                .On("GET", "/api/charts/v1/trade/PF_XBTUSD/1m", KrakenPayloads.Candles)
                .On("GET", "/derivatives/api/v4/historicalfundingrates", KrakenPayloads.FundingRates)
                .Handle);

            Kernel = new TestKernel(Logs);
            Client = new KrakenFuturesDataClient(new ClientId("KRAKEN"), new KrakenDataClientConfig
            {
                ProductType = KrakenProductType.Futures,
                BaseUrlHttp = Server.HttpBase,
                BaseUrlWs = Server.WsBase,
                InstrumentProvider = new InstrumentProviderConfig { LoadAll = true },
            }, Kernel.Services);

            Client.AttachSink(Sink);
        }

        public RecordingLogs Logs { get; } = new();

        public LoopbackServer Server { get; }

        public TestKernel Kernel { get; }

        public KrakenFuturesDataClient Client { get; }

        public RecordingDataSink Sink { get; } = new();

        public WsSession Session => _session ?? throw new InvalidOperationException("not connected");

        public async Task<FuturesRig> ConnectAsync()
        {
            await Client.ConnectAsync(CancellationToken.None).WaitAsync(Wait.Timeout);
            _session = await Server.NextSessionAsync();
            await _session.SendTextAsync(KrakenPayloads.FuturesInfo);
            return this;
        }

        public async Task<JsonElement> NextRequestAsync()
        {
            using JsonDocument doc = JsonDocument.Parse(await Session.ReceiveTextAsync(Wait.Timeout));
            return doc.RootElement.Clone();
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisconnectAsync(CancellationToken.None);
            Client.Dispose();
            Kernel.Dispose();
            await Server.DisposeAsync();
        }
    }

    // ----- spot: the socket it opens -----

    [Fact]
    public async Task The_spot_client_opens_the_public_host_and_asks_by_the_name_that_host_accepts()
    {
        // The socket refuses the catalog's own wsname - "Currency pair not supported XBT/USD" - so what goes on the
        // wire has to be the corrected spelling. The whole chain is here: the catalog's XBT/USD becomes BTC/USD on
        // the instrument, and that is what is subscribed.
        await using SpotRig rig = await new SpotRig().ConnectAsync();

        await rig.Client.SubscribeAsync(Commands.Trades(_btc), CancellationToken.None);
        JsonElement request = await rig.NextRequestAsync();

        Assert.Equal("subscribe", request.Str("method"));
        JsonElement parameters = request.GetProperty("params");
        Assert.Equal("trade", parameters.Str("channel"));
        Assert.Equal("BTC/USD", parameters.GetProperty("symbol")[0].GetString());
    }

    [Fact]
    public async Task A_refused_subscription_is_reported_and_does_not_stop_the_client()
    {
        // The venue refuses a subscription in a reply whose `method` reads "subscribe" whatever was asked, so a
        // failure is recognised by its success flag rather than by the method it claims. A client that threw here
        // would take a node down over one bad symbol.
        await using SpotRig rig = await new SpotRig().ConnectAsync();

        await rig.Session.SendTextAsync(KrakenPayloads.SubscribeRefusedLegacyName);
        await rig.Session.SendTextAsync(KrakenPayloads.Ticker);

        Assert.IsType<QuoteTick>(await rig.Sink.NextDataAsync());
        Assert.True(rig.Client.IsConnected);
        Assert.Contains(rig.Logs.Warnings, w => w.Contains("Currency pair not supported", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_refusal_the_venue_gives_a_private_channel_here_is_reported_rather_than_retried()
    {
        // This is the venue telling the adapter it has the wrong host. Nothing can be done about it on this
        // connection, so it is logged in the venue's own words - which name the host that would work.
        await using SpotRig rig = await new SpotRig().ConnectAsync();

        await rig.Session.SendTextAsync(KrakenPayloads.PrivateRefusedOnPublicHost);
        await rig.Session.SendTextAsync(KrakenPayloads.Ticker);

        Assert.IsType<QuoteTick>(await rig.Sink.NextDataAsync());
        Assert.Contains(rig.Logs.Warnings, w => w.Contains("ws-auth.kraken.com", StringComparison.Ordinal));
    }

    // ----- spot: the data -----

    [Fact]
    public async Task A_spot_ticker_is_a_quote_with_the_venues_own_microsecond_timestamp()
    {
        await using SpotRig rig = await new SpotRig().ConnectAsync();

        await rig.Session.SendTextAsync(KrakenPayloads.Ticker);
        QuoteTick quote = Assert.IsType<QuoteTick>(await rig.Sink.NextDataAsync());

        Assert.Equal(_btc, quote.InstrumentId);
        Assert.Equal(83744.2m, quote.Bid.Value);
        Assert.Equal(83744.3m, quote.Ask.Value);
        Assert.Equal(0.56251979m, quote.BidSize.Value);
        Assert.Equal(0.00044780m, quote.AskSize.Value);

        // An ISO-8601 instant, to the microsecond, where this venue's futures platform writes milliseconds.
        Assert.Equal(
            UnixNanos.FromDateTimeOffset(DateTimeOffset.Parse("2026-09-25T17:48:26.844359Z", System.Globalization.CultureInfo.InvariantCulture)),
            quote.TsEvent);
    }

    [Fact]
    public async Task A_spot_trade_carries_the_takers_side_and_the_venues_trade_id()
    {
        await using SpotRig rig = await new SpotRig().ConnectAsync();

        await rig.Session.SendTextAsync(KrakenPayloads.Trade);
        TradeTick trade = Assert.IsType<TradeTick>(await rig.Sink.NextDataAsync());

        Assert.Equal(_btc, trade.InstrumentId);
        Assert.Equal(83744.3m, trade.Price.Value);
        Assert.Equal(0.00044780m, trade.Size.Value);
        Assert.Equal(AggressorSide.Buyer, trade.Aggressor);
        Assert.Equal("109116693", trade.TradeId.Value);
    }

    [Fact]
    public async Task The_top_of_the_book_is_the_top_of_the_book_and_not_whichever_level_moved()
    {
        // The measured trap. The venue sends the book as a snapshot and then deltas that touch only the levels that
        // changed, and this delta names 83743.4 - a level BELOW the best bid of 83744.2, which it does not mention.
        // A client publishing a quote straight off the delta would report 83743.4 as the best bid, which is a real
        // price at the wrong place in the book and nothing would catch it.
        await using SpotRig rig = await new SpotRig().ConnectAsync();

        await rig.Session.SendTextAsync(KrakenPayloads.BookSnapshot);
        QuoteTick fromSnapshot = Assert.IsType<QuoteTick>(await rig.Sink.NextDataAsync());
        Assert.Equal(83744.2m, fromSnapshot.Bid.Value);
        Assert.Equal(83744.3m, fromSnapshot.Ask.Value);

        await rig.Session.SendTextAsync(KrakenPayloads.BookUpdateBelowTop);
        QuoteTick afterDelta = Assert.IsType<QuoteTick>(await rig.Sink.NextDataAsync());
        Assert.Equal(83744.2m, afterDelta.Bid.Value);
        Assert.Equal(0.56251979m, afterDelta.BidSize.Value);
    }

    [Fact]
    public async Task A_level_the_venue_removes_with_a_zero_quantity_leaves_the_book()
    {
        // The venue has no "delete" message: a zero quantity IS the deletion. Kept as a level it would sit at the
        // top of the book forever with no size, and every quote afterwards would name a price nobody is bidding.
        await using SpotRig rig = await new SpotRig().ConnectAsync();

        await rig.Session.SendTextAsync(KrakenPayloads.BookSnapshot);
        Assert.IsType<QuoteTick>(await rig.Sink.NextDataAsync());

        await rig.Session.SendTextAsync(KrakenPayloads.BookUpdateRemovesTop);
        QuoteTick afterRemoval = Assert.IsType<QuoteTick>(await rig.Sink.NextDataAsync());

        Assert.Equal(83743.4m, afterRemoval.Bid.Value);
    }

    [Fact]
    public async Task A_spot_bar_is_stamped_with_the_close_the_venue_itself_published()
    {
        // Kraken spot is the only venue here whose candle messages carry the END of the interval. So the bar's
        // event time is READ rather than computed: interval_begin 17:48 with timestamp 17:49, and the bar is the
        // 17:49 bar.
        await using SpotRig rig = await new SpotRig().ConnectAsync();

        await rig.Client.SubscribeAsync(Commands.Bars(_spotBars), CancellationToken.None);
        await rig.NextRequestAsync();

        await rig.Session.SendTextAsync(KrakenPayloads.CandleRecorded);

        // The first message only starts the candle; a bar appears when the venue moves on to the next interval.
        await rig.Session.SendTextAsync(KrakenPayloads.Candle(
            "2026-09-25T17:49:00.000000000Z", "2026-09-25T17:50:00.000000Z", "83744.3", "83800.0", "83700.0", "83790.0", "1.5"));

        Bar bar = Assert.IsType<Bar>(await rig.Sink.NextDataAsync());

        Assert.Equal(_spotBars, bar.BarType);
        Assert.Equal(83729.5m, bar.Open.Value);
        Assert.Equal(83745.2m, bar.High.Value);
        Assert.Equal(83729.4m, bar.Low.Value);
        Assert.Equal(83744.3m, bar.Close.Value);
        Assert.Equal(0.21155628m, bar.Volume.Value);
        Assert.Equal(
            UnixNanos.FromDateTimeOffset(DateTimeOffset.Parse("2026-09-25T17:49:00Z", System.Globalization.CultureInfo.InvariantCulture)),
            bar.TsEvent);
    }

    [Fact]
    public async Task The_candle_still_forming_is_not_published_as_a_bar()
    {
        // The venue resends the forming candle as trades arrive and never says a candle is closed, so publishing on
        // arrival would produce a stream of bars for one interval, each claiming to be final.
        await using SpotRig rig = await new SpotRig().ConnectAsync();

        await rig.Client.SubscribeAsync(Commands.Bars(_spotBars), CancellationToken.None);
        await rig.NextRequestAsync();

        await rig.Session.SendTextAsync(KrakenPayloads.CandleRecorded);
        await rig.Session.SendTextAsync(KrakenPayloads.CandleRecorded);
        await rig.Session.SendTextAsync(KrakenPayloads.Ticker);

        // The quote arrives and no bar does, which is what proves the two updates produced none.
        Assert.IsType<QuoteTick>(await rig.Sink.NextDataAsync());
    }

    [Fact]
    public async Task A_bar_length_the_platform_does_not_keep_is_refused_before_anything_is_sent()
    {
        await using SpotRig rig = await new SpotRig().ConnectAsync();

        await Assert.ThrowsAsync<NotSupportedException>(() => rig.Client.SubscribeAsync(
            Commands.Bars(BarType.Parse("BTC-USD.KRAKEN-2-HOUR-LAST-EXTERNAL")), CancellationToken.None));
    }

    [Fact]
    public async Task A_command_this_client_cannot_serve_is_reported_rather_than_ignored()
    {
        await using SpotRig rig = await new SpotRig().ConnectAsync();

        await rig.Client.SubscribeAsync(Commands.Mark(_btc), CancellationToken.None);

        (Core.Model.Commands.SubscribeCommand command, string reason) = Assert.Single(rig.Sink.SubscriptionFailures);
        Assert.IsType<Core.Model.Commands.SubscribeMarkPrices>(command);
        Assert.Contains("Kraken spot data client", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recent_trades_over_rest_keep_their_fractional_seconds()
    {
        // The public trade row's time is in FRACTIONAL seconds. Read as whole seconds the second and third trades
        // in the fixture would share a timestamp and lose their order, which is the kind of loss that shows up as
        // an unexplainable backtest fill.
        await using SpotRig rig = await new SpotRig().ConnectAsync();

        await rig.Client.RequestAsync(Commands.RequestTrades(_btc), CancellationToken.None);
        DataResponse response = await rig.Sink.NextResponseAsync();

        Assert.Null(response.Error);
        TradeTick[] trades = [.. response.Data.Cast<TradeTick>()];
        Assert.Equal(3, trades.Length);
        Assert.Equal(1790359840_939291200L, trades[0].TsEvent.Value);
        Assert.Equal(AggressorSide.Seller, trades[0].Aggressor);
        Assert.Equal(AggressorSide.Buyer, trades[1].Aggressor);
    }

    [Fact]
    public async Task Unsubscribing_tells_the_venue_and_stops_the_data()
    {
        await using SpotRig rig = await new SpotRig().ConnectAsync();

        await rig.Client.SubscribeAsync(Commands.Trades(_btc), CancellationToken.None);
        await rig.NextRequestAsync();

        await rig.Client.UnsubscribeAsync(
            new Core.Model.Commands.UnsubscribeTradeTicks(_btc, null, Guid.NewGuid(), TestKernel.Now),
            CancellationToken.None);

        JsonElement request = await rig.NextRequestAsync();
        Assert.Equal("unsubscribe", request.Str("method"));
        Assert.Equal("trade", request.GetProperty("params").Str("channel"));
    }

    // ----- futures: the socket it opens -----

    [Fact]
    public async Task The_futures_client_subscribes_with_an_event_and_a_feed_rather_than_a_method_and_a_channel()
    {
        // A different protocol, not a different channel name. The spot message would be ignored here and this one
        // would be ignored there, and in both cases the socket stays open and nothing arrives.
        await using FuturesRig rig = await new FuturesRig().ConnectAsync();

        await rig.Client.SubscribeAsync(Commands.Trades(_perp), CancellationToken.None);
        JsonElement request = await rig.NextRequestAsync();

        Assert.Equal("subscribe", request.Str("event"));
        Assert.Equal("trade", request.Str("feed"));
        Assert.Equal("PF_XBTUSD", request.GetProperty("product_ids")[0].GetString());
    }

    [Fact]
    public async Task One_ticker_subscription_answers_the_quote_the_mark_price_and_the_funding_rate()
    {
        // The venue puts four kinds of data on one feed, so asking for any of them asks for the same feed once -
        // and a client that subscribed per command would subscribe three times to the same thing.
        await using FuturesRig rig = await new FuturesRig().ConnectAsync();

        await rig.Client.SubscribeAsync(Commands.Quotes(_perp), CancellationToken.None);
        Assert.Equal("ticker", (await rig.NextRequestAsync()).Str("feed"));

        await rig.Client.SubscribeAsync(Commands.Mark(_perp), CancellationToken.None);
        await rig.Client.SubscribeAsync(
            new Core.Model.Commands.SubscribeFundingRates(_perp, null, Guid.NewGuid(), TestKernel.Now),
            CancellationToken.None);

        await rig.Session.SendTextAsync(KrakenPayloads.FuturesTicker);

        QuoteTick quote = Assert.IsType<QuoteTick>(await rig.Sink.NextDataAsync());
        Assert.Equal(84001.0m, quote.Bid.Value);
        Assert.Equal(84002.0m, quote.Ask.Value);
        Assert.Equal(0.0273m, quote.BidSize.Value);

        MarkPriceUpdate mark = Assert.IsType<MarkPriceUpdate>(await rig.Sink.NextDataAsync());
        Assert.Equal(84004m, mark.Value.Value);

        IndexPriceUpdate index = Assert.IsType<IndexPriceUpdate>(await rig.Sink.NextDataAsync());
        Assert.Equal(83996m, index.Value.Value);

        FundingRateUpdate funding = Assert.IsType<FundingRateUpdate>(await rig.Sink.NextDataAsync());

        // The relative rate, not the absolute charge beside it. 0.912482213835 read as a rate is 91 per cent.
        Assert.Equal(0.00001089345m, funding.Rate);
        Assert.NotEqual(0.912482213835m, funding.Rate);

        // And the venue publishes when the next settlement is, which most do not - so a position's next charge can
        // be priced rather than guessed.
        Assert.Equal(UnixNanos.FromMilliseconds(1790362800000L), funding.NextFundingTime);
    }

    [Fact]
    public async Task A_futures_trade_snapshot_is_republished_oldest_first()
    {
        // The venue sends the snapshot NEWEST first, which is the opposite of the order everything downstream
        // expects. A client that forwarded it as it came would deliver a run of trades backwards, and a consumer
        // keeping the last price would end up holding the oldest.
        await using FuturesRig rig = await new FuturesRig().ConnectAsync();

        await rig.Client.SubscribeAsync(Commands.Trades(_perp), CancellationToken.None);
        await rig.NextRequestAsync();

        await rig.Session.SendTextAsync(KrakenPayloads.FuturesTradeSnapshot);

        TradeTick first = Assert.IsType<TradeTick>(await rig.Sink.NextDataAsync());
        TradeTick second = Assert.IsType<TradeTick>(await rig.Sink.NextDataAsync());
        TradeTick third = Assert.IsType<TradeTick>(await rig.Sink.NextDataAsync());

        Assert.True(first.TsEvent < second.TsEvent);
        Assert.True(second.TsEvent < third.TsEvent);
        Assert.Equal(AggressorSide.Buyer, first.Aggressor);
    }

    [Fact]
    public async Task A_futures_trade_is_stamped_in_milliseconds_and_carries_the_venues_uid()
    {
        await using FuturesRig rig = await new FuturesRig().ConnectAsync();

        await rig.Client.SubscribeAsync(Commands.Trades(_perp), CancellationToken.None);
        await rig.NextRequestAsync();

        await rig.Session.SendTextAsync(KrakenPayloads.FuturesTrade);
        TradeTick trade = Assert.IsType<TradeTick>(await rig.Sink.NextDataAsync());

        Assert.Equal(83994m, trade.Price.Value);
        Assert.Equal(0.0002m, trade.Size.Value);
        Assert.Equal(AggressorSide.Seller, trade.Aggressor);
        Assert.Equal("8a2be12f-7a3f-4f8f-a111-d1b5200ab1d0", trade.TradeId.Value);
        Assert.Equal(UnixNanos.FromMilliseconds(1790360004381L), trade.TsEvent);
    }

    [Fact]
    public async Task A_futures_book_delta_that_removes_a_level_moves_the_top()
    {
        // The delta shape here is one price at a time with the side as a word, which is nothing like the spot one -
        // and a zero quantity is again the deletion. Removing the best ask has to move the quote.
        await using FuturesRig rig = await new FuturesRig().ConnectAsync();

        await rig.Client.SubscribeAsync(Commands.Book(_perp), CancellationToken.None);
        await rig.NextRequestAsync();

        await rig.Session.SendTextAsync(KrakenPayloads.FuturesBookSnapshot);
        QuoteTick fromSnapshot = Assert.IsType<QuoteTick>(await rig.Sink.NextDataAsync());
        Assert.Equal(83998m, fromSnapshot.Bid.Value);
        Assert.Equal(83999m, fromSnapshot.Ask.Value);

        await rig.Session.SendTextAsync(KrakenPayloads.FuturesBookRemovesTopAsk);
        QuoteTick afterRemoval = Assert.IsType<QuoteTick>(await rig.Sink.NextDataAsync());
        Assert.Equal(84001m, afterRemoval.Ask.Value);
    }

    [Fact]
    public async Task A_futures_book_delta_that_arrives_before_the_snapshot_is_dropped()
    {
        // A delta is a delta of something. Applied to an empty book it would build one out of whichever levels
        // happened to move, and the resulting top would be a price with nothing above or below it.
        await using FuturesRig rig = await new FuturesRig().ConnectAsync();

        await rig.Client.SubscribeAsync(Commands.Book(_perp), CancellationToken.None);
        await rig.NextRequestAsync();

        await rig.Session.SendTextAsync(KrakenPayloads.FuturesBookRemovesTopAsk);
        await rig.Session.SendTextAsync(KrakenPayloads.FuturesBookSnapshot);

        // The first data to arrive is the snapshot's quote, not one built from the delta.
        QuoteTick quote = Assert.IsType<QuoteTick>(await rig.Sink.NextDataAsync());
        Assert.Equal(83998m, quote.Bid.Value);
        Assert.Equal(83999m, quote.Ask.Value);
    }

    [Fact]
    public async Task A_futures_candle_feed_carries_its_length_in_the_feed_name()
    {
        // The length is part of the feed NAME here - candles_trade_1m - so a client subscribes to a different feed
        // per bar length rather than sending a parameter. Asking for a length the venue does not keep is answered
        // with an alert that names no feed at all, so nothing could correlate it with the request.
        await using FuturesRig rig = await new FuturesRig().ConnectAsync();

        await rig.Client.SubscribeAsync(Commands.Bars(_futuresBars), CancellationToken.None);
        JsonElement request = await rig.NextRequestAsync();

        Assert.Equal("candles_trade_1m", request.Str("feed"));
    }

    [Fact]
    public async Task The_first_candle_of_a_subscription_arrives_under_a_different_feed_name_and_still_counts()
    {
        // Measured: the venue answers a candle subscription with candles_trade_1m_snapshot and then sends
        // candles_trade_1m. A client that matched the feed name exactly would ignore the first candle of every
        // subscription, which is the one that establishes where the interval started.
        await using FuturesRig rig = await new FuturesRig().ConnectAsync();

        await rig.Client.SubscribeAsync(Commands.Bars(_futuresBars), CancellationToken.None);
        await rig.NextRequestAsync();

        // 1790359920000 is 17:52:00; the snapshot starts that interval.
        await rig.Session.SendTextAsync(KrakenPayloads.FuturesCandleSnapshot);

        // And the next interval closes it.
        await rig.Session.SendTextAsync(KrakenPayloads.FuturesCandle(1790359980000L, "83999", "84010", "83990", "84005", "0.5"));

        Bar bar = Assert.IsType<Bar>(await rig.Sink.NextDataAsync());

        Assert.Equal(83954m, bar.Open.Value);
        Assert.Equal(84007m, bar.High.Value);
        Assert.Equal(83954m, bar.Low.Value);
        Assert.Equal(83999m, bar.Close.Value);
        Assert.Equal(0.9783m, bar.Volume.Value);

        // Stamped by the CLOSE, computed from the open the venue gave plus the interval - the opposite of the spot
        // socket, which publishes the close itself.
        Assert.Equal(UnixNanos.FromMilliseconds(1790359980000L), bar.TsEvent);
    }

    [Fact]
    public async Task An_alert_about_a_feed_that_does_not_exist_is_reported_rather_than_thrown()
    {
        await using FuturesRig rig = await new FuturesRig().ConnectAsync();

        await rig.Session.SendTextAsync(KrakenPayloads.FuturesInvalidFeed);
        await rig.Session.SendTextAsync(KrakenPayloads.FuturesTicker);

        Assert.IsType<QuoteTick>(await rig.Sink.NextDataAsync());
        Assert.Contains(rig.Logs.Warnings, w => w.Contains("invalid feed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_futures_client_answers_a_funding_history_request_out_of_the_shared_helper()
    {
        // The same helper a catalog download calls, so stored and live funding never disagree about what a
        // settlement was.
        await using FuturesRig rig = await new FuturesRig().ConnectAsync();

        await rig.Client.RequestAsync(
            Commands.RequestFundingRates(_perp, null, null, null),
            CancellationToken.None);

        DataResponse response = await rig.Sink.NextResponseAsync();
        Assert.Null(response.Error);
        Assert.Equal(3, response.Data.Count);
        Assert.Equal(0.000011795815277778m, ((FundingRateUpdate)response.Data[0]).Rate);
    }

    [Fact]
    public async Task The_futures_client_does_not_offer_spot_trade_history()
    {
        // The platform's trade history lives behind a separate service this adapter does not read, so the request
        // is answered with a reason rather than with an empty list a caller would read as "no trades".
        await using FuturesRig rig = await new FuturesRig().ConnectAsync();

        await rig.Client.RequestAsync(Commands.RequestTrades(_perp), CancellationToken.None);

        DataResponse response = await rig.Sink.NextResponseAsync();
        Assert.NotNull(response.Error);
        Assert.Contains("Kraken futures data client", response.Error!, StringComparison.Ordinal);
    }
}
