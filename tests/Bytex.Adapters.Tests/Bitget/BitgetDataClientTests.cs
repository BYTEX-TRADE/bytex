using System.Text.Json;
using Bytex.Adapters.Bitget;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Primitives;

namespace Bytex.Adapters.Tests.Bitget;

// Why: the stream is where this venue's spellings stop agreeing with themselves. One bar length has three names on
// one venue - 1h on spot REST, 1H on derivative REST, candle1H on the socket for both - and the socket is the only
// place a wrong name is reported at all: it answers code 30016 "Param error" and stays open, so a client that reused
// a REST spelling would subscribe to nothing and look connected. Measured by subscribing: candle1h and candle1min are
// both refused on the spot stream and candle1H is served there.
//
// The second thing measured here is how a bar is closed. The venue publishes the candle being built and never says
// one has finished, and it does not reliably push at the interval boundary either: on the quietest contract the venue
// lists, over two hundred seconds, it pushed a fresh candle at one of the three minute boundaries and nothing at the
// other two. So a bar is closed when the next one opens AND by the clock, and both are asserted below.
//
// Every payload in this file is what ws.bitget.com really sent on 2026-09-25.
public sealed class BitgetDataClientTests
{
    private static readonly InstrumentId _spot = InstrumentId.Parse("BTCUSDT.BITGET");
    private static readonly InstrumentId _perp = InstrumentId.Parse("BTCUSDT-PERP.BITGET");
    private static readonly BarType _spotBars = BarType.Parse("BTCUSDT.BITGET-1-MINUTE-LAST-EXTERNAL");

    private sealed class Rig : IAsyncDisposable
    {
        private WsSession? _session;

        public Rig(BitgetProductType type = BitgetProductType.Spot, bool handleRevisedBars = false, RecordingLogs? logs = null)
        {
            Routes routes = new Routes()
                .On("GET", BitgetInstrumentProvider.SpotSymbolsPath, BitgetPayloads.SpotSymbols)
                .On("GET", BitgetInstrumentProvider.ContractsPath, BitgetPayloads.UsdtContracts)
                .On("GET", BitgetInstrumentProvider.PositionTiersPath, r => StubResponse.Json(r.Query("symbol") == "BTCUSDT"
                    ? BitgetPayloads.BtcUsdtPositionTiers
                    : BitgetPayloads.Error("40034", "Parameter does not exist")))
                .On("GET", BitgetHistory.SpotHistoryCandlesPath, BitgetPayloads.SpotHistoryCandles)
                .On("GET", BitgetHistory.FuturesHistoryCandlesPath, BitgetPayloads.FuturesHistoryCandles)
                .On("GET", BitgetHistory.FundingHistoryPath, BitgetPayloads.FundingHistory)
                .On("GET", "/api/v2/spot/market/fills", BitgetPayloads.Envelope("""
                    [{"symbol":"BTCUSDT","tradeId":"1487442472944254976","side":"buy","price":"83964.93","size":"0.003154","ts":"1790359841571"},
                     {"symbol":"BTCUSDT","tradeId":"1487442469337153536","side":"sell","price":"83953.87","size":"0.002382","ts":"1790359840711"}]
                    """));

            Server = new LoopbackServer(routes.Handle);
            Kernel = new TestKernel(logs);
            Client = new BitgetDataClient(new ClientId("BITGET"), new BitgetDataClientConfig
            {
                ProductType = type,
                BaseUrlHttp = Server.HttpBase,
                BaseUrlWs = Server.WsBase,
                HandleRevisedBars = handleRevisedBars,
                InstrumentProvider = new InstrumentProviderConfig { LoadAll = true },
            }, Kernel.Services);

            Client.AttachSink(Sink);
        }

        public LoopbackServer Server { get; }

        public TestKernel Kernel { get; }

        public BitgetDataClient Client { get; }

        public RecordingDataSink Sink { get; } = new();

        public WsSession Session => _session ?? throw new InvalidOperationException("not connected");

        public async Task<Rig> ConnectAsync()
        {
            await Client.ConnectAsync(CancellationToken.None).WaitAsync(Wait.Timeout);
            _session = await Server.NextSessionAsync();
            return this;
        }

        /// <summary>The next control message the client sent, as "op instType/channel/instId,...".</summary>
        public async Task<string> NextControlMessageAsync(WsSession? session = null)
        {
            using JsonDocument doc = JsonDocument.Parse(await (session ?? Session).ReceiveTextAsync());
            IEnumerable<string> args = doc.RootElement.GetProperty("args").EnumerateArray().Select(a =>
                a.GetProperty("instType").GetString() + "/" + a.GetProperty("channel").GetString() + "/" + a.GetProperty("instId").GetString());

            return doc.RootElement.GetProperty("op").GetString() + " " + string.Join(",", args.Order(StringComparer.Ordinal));
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            Kernel.Dispose();
            await Server.DisposeAsync();
        }
    }

    // ----- subscriptions -----

    [Fact]
    public async Task A_subscription_names_the_market_the_channel_and_the_instrument()
    {
        await using Rig rig = await new Rig().ConnectAsync();

        await rig.Client.SubscribeAsync(Commands.Quotes(_spot), CancellationToken.None);

        Assert.Equal("subscribe SPOT/ticker/BTCUSDT", await rig.NextControlMessageAsync());
    }

    [Fact]
    public async Task A_derivative_subscription_names_the_product_type_as_the_instrument_type()
    {
        // The only thing that tells this venue's families apart: the address is the same and so is the channel name.
        await using Rig rig = await new Rig(BitgetProductType.UsdtFutures).ConnectAsync();

        await rig.Client.SubscribeAsync(Commands.Trades(_perp), CancellationToken.None);

        Assert.Equal("subscribe USDT-FUTURES/trade/BTCUSDT", await rig.NextControlMessageAsync());
    }

    [Fact]
    public async Task A_candle_subscription_uses_the_spelling_only_the_socket_accepts()
    {
        // candle1min and candle1h are refused with code 30016 and candle1m and candle1H are served, on both markets.
        await using Rig rig = await new Rig().ConnectAsync();

        await rig.Client.SubscribeAsync(Commands.Bars(_spotBars), CancellationToken.None);
        Assert.Equal("subscribe SPOT/candle1m/BTCUSDT", await rig.NextControlMessageAsync());

        await rig.Client.SubscribeAsync(Commands.Bars(BarType.Parse("BTCUSDT.BITGET-1-HOUR-LAST-EXTERNAL")), CancellationToken.None);
        Assert.Equal("subscribe SPOT/candle1H/BTCUSDT", await rig.NextControlMessageAsync());
    }

    [Theory]
    [InlineData(0, "books")]
    [InlineData(5, "books5")]
    [InlineData(15, "books15")]
    [InlineData(50, "books")]
    public async Task A_book_subscription_is_served_by_a_channel_at_least_as_deep_as_it_asked_for(int depth, string channel)
    {
        // The venue publishes two fixed depths as repeated snapshots and the whole book as deltas. A depth it does not
        // publish is served by the whole book rather than by a nearer snapshot: fifty levels answered with fifteen
        // would be quietly short.
        await using Rig rig = await new Rig().ConnectAsync();

        await rig.Client.SubscribeAsync(Commands.Book(_spot, depth), CancellationToken.None);

        Assert.Equal($"subscribe SPOT/{channel}/BTCUSDT", await rig.NextControlMessageAsync());
    }

    [Fact]
    public async Task The_same_subscription_twice_is_sent_once()
    {
        await using Rig rig = await new Rig().ConnectAsync();

        await rig.Client.SubscribeAsync(Commands.Quotes(_spot), CancellationToken.None);
        Assert.Equal("subscribe SPOT/ticker/BTCUSDT", await rig.NextControlMessageAsync());

        await rig.Client.SubscribeAsync(Commands.Quotes(_spot), CancellationToken.None);
        await rig.Client.SubscribeAsync(Commands.Trades(_spot), CancellationToken.None);

        // The trade subscription is the next thing on the wire, so nothing was sent for the repeat.
        Assert.Equal("subscribe SPOT/trade/BTCUSDT", await rig.NextControlMessageAsync());
    }

    [Fact]
    public async Task Unsubscribing_a_book_stops_every_book_channel_of_that_instrument()
    {
        // The command names no depth, so which of the three channels was subscribed is not in it. Anything less than
        // all of them would leave one running.
        await using Rig rig = await new Rig().ConnectAsync();

        await rig.Client.SubscribeAsync(Commands.Book(_spot, 5), CancellationToken.None);
        await rig.NextControlMessageAsync();
        await rig.Client.SubscribeAsync(Commands.Book(_spot, 15), CancellationToken.None);
        await rig.NextControlMessageAsync();

        await rig.Client.UnsubscribeAsync(new UnsubscribeOrderBookDeltas(_spot, null, Guid.NewGuid(), TestKernel.Now), CancellationToken.None);

        Assert.Equal("unsubscribe SPOT/books15/BTCUSDT,SPOT/books5/BTCUSDT", await rig.NextControlMessageAsync());
    }

    [Fact]
    public async Task Everything_is_subscribed_again_after_the_socket_comes_back()
    {
        await using Rig rig = await new Rig().ConnectAsync();

        await rig.Client.SubscribeAsync(Commands.Quotes(_spot), CancellationToken.None);
        Assert.Equal("subscribe SPOT/ticker/BTCUSDT", await rig.NextControlMessageAsync());

        rig.Session.Drop();
        WsSession second = await rig.Server.NextSessionAsync();

        Assert.Equal("subscribe SPOT/ticker/BTCUSDT", await rig.NextControlMessageAsync(second));
    }

    [Fact]
    public async Task A_subscription_this_client_cannot_serve_is_reported_rather_than_dropped()
    {
        // Spot has no mark price, no index price and no funding, so a subscription for one of them has to be refused
        // where it is asked rather than accepted and never answered.
        await using Rig rig = await new Rig().ConnectAsync();

        await rig.Client.SubscribeAsync(Commands.Mark(_spot), CancellationToken.None);

        Assert.Contains("SubscribeMarkPrices", Assert.Single(rig.Sink.SubscriptionFailures).Reason, StringComparison.Ordinal);
    }

    // ----- what the venue really sent -----

    [Fact]
    public async Task A_spot_ticker_becomes_a_quote()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        await rig.Client.SubscribeAsync(Commands.Quotes(_spot), CancellationToken.None);
        await rig.NextControlMessageAsync();

        await rig.Session.SendTextAsync(BitgetPayloads.SpotTicker);
        QuoteTick quote = Assert.IsType<QuoteTick>(await rig.Sink.NextDataAsync());

        Assert.Equal(_spot, quote.InstrumentId);
        Assert.Equal(83715.69m, quote.Bid.Value);
        Assert.Equal(83715.7m, quote.Ask.Value);
        Assert.Equal(0.074435m, quote.BidSize.Value);
        Assert.Equal(2.145777m, quote.AskSize.Value);
        Assert.Equal(UnixNanos.FromMilliseconds(1_790_358_774_065), quote.TsEvent);
    }

    [Fact]
    public async Task A_derivative_ticker_carries_four_kinds_of_data_on_one_channel()
    {
        // The quote, the mark price, the index price and the funding rate with the time of the next settlement all
        // arrive in one row, which is why three different subscriptions ask for the same channel.
        await using Rig rig = await new Rig(BitgetProductType.UsdtFutures).ConnectAsync();
        await rig.Client.SubscribeAsync(Commands.Mark(_perp), CancellationToken.None);
        await rig.NextControlMessageAsync();

        await rig.Session.SendTextAsync(BitgetPayloads.FuturesTicker);

        List<IData> published = [];
        for (int i = 0; i < 4; i++)
        {
            published.Add(await rig.Sink.NextDataAsync());
        }

        Assert.Single(published.OfType<QuoteTick>());
        Assert.Equal(83692.3m, Assert.Single(published.OfType<MarkPriceUpdate>()).Value.Value);
        // Rounded to the contract's own tick, which is a tenth: a price crossing into the engine is the
        // instrument's price and not a free-form number.
        Assert.Equal(83720.9m, Assert.Single(published.OfType<IndexPriceUpdate>()).Value.Value);

        FundingRateUpdate funding = Assert.Single(published.OfType<FundingRateUpdate>());
        Assert.Equal(0.0001m, funding.Rate);
        Assert.Equal(UnixNanos.FromMilliseconds(1_790_380_800_000), funding.NextFundingTime);
    }

    [Fact]
    public async Task A_trade_becomes_a_trade_tick_with_the_takers_side()
    {
        // The rows carry no symbol of their own - it is in the subscription argument - so a reader that looked for
        // one on the row would publish nothing.
        await using Rig rig = await new Rig().ConnectAsync();
        await rig.Client.SubscribeAsync(Commands.Trades(_spot), CancellationToken.None);
        await rig.NextControlMessageAsync();

        await rig.Session.SendTextAsync(BitgetPayloads.SpotTrade);
        TradeTick trade = Assert.IsType<TradeTick>(await rig.Sink.NextDataAsync());

        Assert.Equal(_spot, trade.InstrumentId);
        Assert.Equal(83715.7m, trade.Price.Value);
        Assert.Equal(0.000012m, trade.Size.Value);
        Assert.Equal(AggressorSide.Buyer, trade.Aggressor);
        Assert.Equal("1487438024184414208", trade.TradeId.Value);
    }

    [Fact]
    public async Task A_depth_limited_book_arrives_as_a_snapshot_every_time()
    {
        // books5 and books15 never send a delta: each message is the whole of that depth again, so each one clears
        // what came before it.
        await using Rig rig = await new Rig().ConnectAsync();
        await rig.Client.SubscribeAsync(Commands.Book(_spot, 5), CancellationToken.None);
        await rig.NextControlMessageAsync();

        await rig.Session.SendTextAsync(BitgetPayloads.SpotBooks5);
        OrderBookDeltas book = Assert.IsType<OrderBookDeltas>(await rig.Sink.NextDataAsync());

        Assert.True(book.Flags.HasFlag(RecordFlags.Snapshot));
        Assert.Equal(BookAction.Clear, book.Deltas[0].Action);
        Assert.Equal(5, book.Deltas.Count);
        Assert.All(book.Deltas.Skip(1), d => Assert.Equal(BookAction.Add, d.Action));

        // And the top of it as a quote, which is the only way to get one at a depth here: the ticker channel carries
        // one level.
        QuoteTick quote = Assert.IsType<QuoteTick>(await rig.Sink.NextDataAsync());
        Assert.Equal(83715.69m, quote.Bid.Value);
        Assert.Equal(83715.7m, quote.Ask.Value);
    }

    [Fact]
    public async Task The_whole_book_channel_removes_a_level_with_a_size_of_zero()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        await rig.Client.SubscribeAsync(Commands.Book(_spot, 0), CancellationToken.None);
        await rig.NextControlMessageAsync();

        // The snapshot first, because a delta on its own describes changes to a book nothing has.
        await rig.Session.SendTextAsync(BitgetPayloads.SpotBooks5.Replace("books5", "books", StringComparison.Ordinal));
        await rig.Sink.NextDataAsync();
        await rig.Sink.NextDataAsync();

        await rig.Session.SendTextAsync(BitgetPayloads.SpotBooksDelta);
        OrderBookDeltas deltas = Assert.IsType<OrderBookDeltas>(await rig.Sink.NextDataAsync());

        Assert.False(deltas.Flags.HasFlag(RecordFlags.Snapshot));
        Assert.Equal(BookAction.Delete, deltas.Deltas[0].Action);
        Assert.Equal(BookAction.Update, deltas.Deltas[1].Action);
    }

    [Fact]
    public async Task A_pong_is_not_read_as_a_message()
    {
        // The venue's heartbeat is four characters of plain text. A client that parsed it as JSON would log a warning
        // every twenty seconds for the life of the node.
        RecordingLogs logs = new();
        await using Rig rig = await new Rig(logs: logs).ConnectAsync();

        await rig.Session.SendTextAsync("pong");
        await rig.Session.SendTextAsync(BitgetPayloads.SpotTicker);
        await rig.Client.SubscribeAsync(Commands.Quotes(_spot), CancellationToken.None);
        await rig.NextControlMessageAsync();
        await rig.Session.SendTextAsync(BitgetPayloads.SpotTicker);
        await rig.Sink.NextDataAsync();

        Assert.DoesNotContain(logs.Warnings, w => w.Contains("unreadable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_refused_subscription_is_reported_because_nothing_else_would_say_so()
    {
        // The socket stays open and simply sends nothing, so this message is the only place a channel name the venue
        // does not publish is visible at all.
        RecordingLogs logs = new();
        await using Rig rig = await new Rig(logs: logs).ConnectAsync();
        await rig.Client.SubscribeAsync(Commands.Quotes(_spot), CancellationToken.None);
        await rig.NextControlMessageAsync();

        await rig.Session.SendTextAsync(BitgetPayloads.SubscribeError);

        // A ticker behind it, and waiting for the quote it becomes. The client reads one message at a time off the
        // socket, so a quote built from the SECOND message proves the first has already been handled - which is what
        // this test needs and is a condition rather than a guess at how long the handler takes.
        await rig.Session.SendTextAsync(BitgetPayloads.SpotTicker);
        Assert.IsType<QuoteTick>(await rig.Sink.NextDataAsync());

        Assert.Contains(logs.Warnings, w => w.Contains("30016", StringComparison.Ordinal));
    }

    // ----- bars, which the venue never says are finished -----

    [Fact]
    public async Task The_opening_snapshot_publishes_nothing_and_only_seeds_the_candle_being_built()
    {
        // The venue answers a one-minute candle subscription with hours of historical rows. Publishing them would
        // turn a subscription into an unasked-for history request with no window and no limit; history is
        // RequestBars's job, and the last row of the snapshot is the candle being built.
        await using Rig rig = await new Rig().ConnectAsync();
        await rig.Client.SubscribeAsync(Commands.Bars(_spotBars), CancellationToken.None);
        await rig.NextControlMessageAsync();

        await rig.Session.SendTextAsync(BitgetPayloads.CandleSnapshot);

        // The next bar published is the one the snapshot's last row became, closed by the update that follows it -
        // not one of the rows before it.
        await rig.Session.SendTextAsync(BitgetPayloads.CandleUpdate("1790358840000", "83730.0", "2"));
        Bar bar = Assert.IsType<Bar>(await rig.Sink.NextDataAsync());

        Assert.Equal(UnixNanos.FromMilliseconds(1_790_358_780_000 + 60_000), bar.TsEvent);
        Assert.Equal(0m, bar.Volume.Value);
    }

    [Fact]
    public async Task A_bar_is_closed_when_the_next_one_opens_and_carries_its_last_state()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        await rig.Client.SubscribeAsync(Commands.Bars(_spotBars), CancellationToken.None);
        await rig.NextControlMessageAsync();

        await rig.Session.SendTextAsync(BitgetPayloads.CandleUpdate("1790358780000", "83720.0", "1"));
        await rig.Session.SendTextAsync(BitgetPayloads.CandleUpdate("1790358780000", "83725.0", "3"));
        await rig.Session.SendTextAsync(BitgetPayloads.CandleUpdate("1790358840000", "83730.0", "1"));

        Bar bar = Assert.IsType<Bar>(await rig.Sink.NextDataAsync());

        // Close-stamped, and the LAST state of that minute rather than the first.
        Assert.Equal(UnixNanos.FromMilliseconds(1_790_358_840_000), bar.TsEvent);
        Assert.Equal(83725.0m, bar.Close.Value);
        Assert.Equal(3m, bar.Volume.Value);
        Assert.False(bar.IsRevision);
    }

    [Fact]
    public async Task A_bar_is_closed_by_the_clock_when_the_venue_goes_quiet()
    {
        // Measured on the quietest contract the venue lists: over two hundred seconds it pushed a fresh candle at one
        // of the three minute boundaries and nothing at the other two. Waiting for the next candle would publish a
        // bar minutes late, or never, and a strategy on bars would simply stop being called.
        await using Rig rig = await new Rig().ConnectAsync();
        await rig.Client.SubscribeAsync(Commands.Bars(_spotBars), CancellationToken.None);
        await rig.NextControlMessageAsync();

        await rig.Session.SendTextAsync(BitgetPayloads.CandleUpdate("1790358780000", "83720.0", "1"));

        // Past the end of that minute and past the grace the client allows for a late update.
        rig.Kernel.Clock.SetTime(UnixNanos.FromMilliseconds(1_790_358_780_000 + 60_000 + 5_000));

        Bar bar = Assert.IsType<Bar>(await rig.Sink.NextDataAsync());
        Assert.Equal(UnixNanos.FromMilliseconds(1_790_358_840_000), bar.TsEvent);
        Assert.Equal(83720.0m, bar.Close.Value);
    }

    [Fact]
    public async Task The_candle_being_built_is_published_as_a_revision_only_when_that_is_asked_for()
    {
        await using Rig rig = await new Rig(handleRevisedBars: true).ConnectAsync();
        await rig.Client.SubscribeAsync(Commands.Bars(_spotBars), CancellationToken.None);
        await rig.NextControlMessageAsync();

        await rig.Session.SendTextAsync(BitgetPayloads.CandleUpdate("1790358780000", "83720.0", "1"));
        Bar revision = Assert.IsType<Bar>(await rig.Sink.NextDataAsync());

        Assert.True(revision.IsRevision);
        Assert.Equal(83720.0m, revision.Close.Value);
    }

    [Fact]
    public async Task A_candle_older_than_the_one_being_built_is_ignored()
    {
        // The venue repeats rows when a subscription is made again, and a row older than the one in hand must not
        // close it: that would publish a bar covering the wrong minute.
        await using Rig rig = await new Rig().ConnectAsync();
        await rig.Client.SubscribeAsync(Commands.Bars(_spotBars), CancellationToken.None);
        await rig.NextControlMessageAsync();

        await rig.Session.SendTextAsync(BitgetPayloads.CandleUpdate("1790358840000", "83730.0", "1"));
        await rig.Session.SendTextAsync(BitgetPayloads.CandleUpdate("1790358780000", "83720.0", "1"));
        await rig.Session.SendTextAsync(BitgetPayloads.CandleUpdate("1790358900000", "83740.0", "1"));

        Bar bar = Assert.IsType<Bar>(await rig.Sink.NextDataAsync());
        Assert.Equal(UnixNanos.FromMilliseconds(1_790_358_900_000), bar.TsEvent);
        Assert.Equal(83730.0m, bar.Close.Value);
    }

    // ----- requests -----

    [Fact]
    public async Task Bars_are_requested_through_the_shared_history_helper()
    {
        // Which matters because a catalog download calls the same method: stored bars and a node's bars come from one
        // piece of code rather than two that agree until the venue changes a default.
        await using Rig rig = await new Rig().ConnectAsync();

        // The recorded candles are from the day they were recorded, and a bar that has not closed by "now" is not a
        // bar. The test clock is moved to just after the last of them so that all five have.
        rig.Kernel.Clock.SetTime(UnixNanos.FromMilliseconds(1_790_358_960_000));

        await rig.Client.RequestAsync(
            Commands.RequestBars(_spotBars, null, null, 5),
            CancellationToken.None).WaitAsync(Wait.Timeout);

        DataResponse response = await rig.Sink.NextResponseAsync();
        Assert.Equal(typeof(Bar), response.DataType);
        Assert.Equal(5, response.Data.Count);

        // The spot spelling, on the endpoint that holds the venue's whole history.
        Assert.Equal("1min", Assert.Single(rig.Server.RequestsTo(BitgetHistory.SpotHistoryCandlesPath)).Query("granularity"));
    }

    [Fact]
    public async Task Funding_is_requested_on_the_perpetual_families_and_refused_on_spot()
    {
        await using Rig futures = await new Rig(BitgetProductType.UsdtFutures).ConnectAsync();

        await futures.Client.RequestAsync(
            Commands.RequestFundingRates(_perp, null, null, null),
            CancellationToken.None).WaitAsync(Wait.Timeout);

        DataResponse answered = await futures.Sink.NextResponseAsync();
        Assert.Equal(typeof(FundingRateUpdate), answered.DataType);
        Assert.Equal(5, answered.Data.Count);

        await using Rig spot = await new Rig().ConnectAsync();
        await spot.Client.RequestAsync(
            Commands.RequestFundingRates(_spot, null, null, null),
            CancellationToken.None).WaitAsync(Wait.Timeout);

        DataResponse refused = await spot.Sink.NextResponseAsync();
        Assert.NotNull(refused.Error);
    }

    [Fact]
    public async Task Recent_trades_come_back_oldest_first()
    {
        // The venue answers newest first here as it does for funding, and everything above the adapter reads a series
        // forwards.
        await using Rig rig = await new Rig().ConnectAsync();

        await rig.Client.RequestAsync(Commands.RequestTrades(_spot), CancellationToken.None).WaitAsync(Wait.Timeout);
        DataResponse response = await rig.Sink.NextResponseAsync();

        Assert.Equal(2, response.Data.Count);
        TradeTick[] trades = [.. response.Data.Cast<TradeTick>()];
        Assert.True(trades[0].TsEvent < trades[1].TsEvent);
        Assert.Equal(AggressorSide.Seller, trades[0].Aggressor);
    }
}
