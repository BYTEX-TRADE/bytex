using System.Text.Json;
using Bytex.Adapters.Kucoin;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Adapters.Tests.Kucoin;

// Why: this client reads the same venue as the spot one and shares nothing with it but the envelope. Measured against
// the live socket on 2026-09-25, on one connection:
//
//   - a quote's sizes are CONTRACTS and its timestamp is NANOSECONDS
//   - a trade's size is CONTRACTS and its timestamp is NANOSECONDS
//   - a book snapshot's sizes are CONTRACTS and its timestamp is MILLISECONDS
//   - a candle's start is SECONDS, and its row is [start, open, close, high, low, TURNOVER, VOLUME]
//   - the REST candle for the same market is [start in ms, open, HIGH, LOW, CLOSE, VOLUME, turnover]
//
// So the two candle layouts on one venue's one market disagree about where the prices are, which of two numbers is the
// volume, and what unit the clock is in. Reading either with the other's layout gives four real prices in the wrong
// places and a volume out by the contract size, with nothing to catch it. Every one of those is pinned below with
// values chosen so that any permutation fails.
public sealed class KucoinFuturesDataClientTests
{
    private static readonly InstrumentId _btc = InstrumentId.Parse("XBTUSDT-PERP.KUCOIN");
    private static readonly BarType _barType = BarType.Parse("XBTUSDT-PERP.KUCOIN-1-MINUTE-LAST-EXTERNAL");

    /// <summary>XBTUSDTM: one contract is 0.001 XBT, so a size of 1 contract is 0.001 and 4,000 is 4.</summary>
    private const decimal Contract = 0.001m;

    private sealed class Rig : IAsyncDisposable
    {
        private WsSession? _session;

        public Rig(bool handleRevisedBars = false)
        {
            Routes routes = new Routes()
                .On("GET", "/api/v1/contracts/active", KucoinPayloads.FuturesContracts)
                .On("POST", "/api/v1/bullet-public", _ => StubResponse.Json(KucoinPayloads.Bullet(Server!.WsBase, "public-token-" + (++Tokens))))
                .On("GET", "/api/v1/kline/query", _ => StubResponse.Json(KucoinPayloads.Envelope("[]")))
                .On("GET", "/api/v1/trade/history", KucoinPayloads.Envelope("""
                    [{"symbol":"XBTUSDTM","side":"buy","size":12,"price":"84117","tradeId":"1944971621109","ts":1729177117878000000},
                     {"symbol":"XBTUSDTM","side":"sell","size":3,"price":"84116.9","tradeId":"1944971621108","ts":1729177117877000000}]
                    """));

            Server = new LoopbackServer(routes.Handle);
            Kernel = new TestKernel();
            Client = new KucoinFuturesDataClient(new ClientId("KUCOIN"), new KucoinDataClientConfig
            {
                ProductType = KucoinProductType.Futures,
                BaseUrlHttp = Server.HttpBase,
                HandleRevisedBars = handleRevisedBars,
                InstrumentProvider = new Core.Adapters.InstrumentProviderConfig { LoadAll = true },
            }, Kernel.Services);
            Client.AttachSink(Sink);
        }

        public int Tokens { get; private set; }

        public LoopbackServer Server { get; }

        public TestKernel Kernel { get; }

        public KucoinFuturesDataClient Client { get; }

        public RecordingDataSink Sink { get; } = new();

        public WsSession Session => _session ?? throw new InvalidOperationException("not connected");

        public async Task<Rig> ConnectAsync()
        {
            await Client.ConnectAsync(CancellationToken.None).WaitAsync(Wait.Timeout);
            _session = await Server.NextSessionAsync();
            await _session.SendTextAsync(KucoinPayloads.Welcome);
            return this;
        }

        public async Task<JsonElement> NextControlAsync()
        {
            while (true)
            {
                using JsonDocument doc = JsonDocument.Parse(await Session.ReceiveTextAsync(Wait.Timeout));
                if (doc.RootElement.GetProperty("type").GetString() != "ping")
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

    // ----- the family it serves -----

    [Fact]
    public void A_spot_configuration_is_refused_rather_than_served_against_the_wrong_host()
    {
        // The two clients answer for different hosts and different message shapes. Handed a spot configuration this
        // one would ask api.kucoin.com for /api/v1/contracts/active and get nothing it understands, so it says so.
        using TestKernel kernel = new();

        ArgumentException error = Assert.Throws<ArgumentException>(() => new KucoinFuturesDataClient(
            new ClientId("KUCOIN"),
            new KucoinDataClientConfig { ProductType = KucoinProductType.Spot },
            kernel.Services));

        Assert.Contains("KucoinDataClient", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_factory_picks_the_client_the_configuration_asks_for()
    {
        // One factory name for the venue and two clients behind it, so a host configuring KUCOIN does not also have
        // to know the venue has two markets - which is what the declaration is for.
        using TestKernel kernel = new();
        KucoinDataClientFactory factory = new();

        Assert.Equal("KUCOIN", factory.Name);

        KucoinFuturesDataClient futures = Assert.IsType<KucoinFuturesDataClient>(factory.Create(
            new ClientId("KUCOIN"),
            new KucoinDataClientConfig { ProductType = KucoinProductType.Futures },
            kernel.Services));

        KucoinDataClient spot = Assert.IsType<KucoinDataClient>(factory.Create(
            new ClientId("KUCOIN"),
            new KucoinDataClientConfig(),
            kernel.Services));

        futures.Dispose();
        spot.Dispose();
    }

    [Fact]
    public async Task Contracts_are_loaded_and_published_in_base_currency()
    {
        await using Rig rig = await new Rig().ConnectAsync();

        Assert.Equal(
            ["DOGEUSDT-PERP.KUCOIN", "XBTUSDT-PERP.KUCOIN"],
            rig.Sink.Instruments.Select(i => i.Id.ToString()).Order(StringComparer.Ordinal));

        Instrument btc = rig.Client.Instruments.Find(_btc)!;
        Assert.IsType<CryptoPerpetual>(btc);
        Assert.Equal(new Quantity(Contract, 3), btc.SizeIncrement);
    }

    [Fact]
    public async Task The_stream_address_and_its_token_come_from_the_futures_bullet_call()
    {
        await using Rig rig = await new Rig().ConnectAsync();

        RecordedRequest bullet = Assert.Single(rig.Server.RequestsTo("/api/v1/bullet-public"));
        Assert.Equal("POST", bullet.Method);
        Assert.Null(bullet.Header("KC-API-KEY"));
        Assert.Contains("token=public-token-1", rig.Session.RawQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Subscriptions_use_the_futures_topics_not_the_spot_ones()
    {
        await using Rig rig = await new Rig().ConnectAsync();

        await rig.Client.SubscribeAsync(Commands.Quotes(_btc), CancellationToken.None);
        JsonElement quotes = await rig.NextControlAsync();
        Assert.Equal("/contractMarket/tickerV2:XBTUSDTM", quotes.GetProperty("topic").GetString());

        await rig.Client.SubscribeAsync(Commands.Trades(_btc), CancellationToken.None);
        JsonElement trades = await rig.NextControlAsync();
        Assert.Equal("/contractMarket/execution:XBTUSDTM", trades.GetProperty("topic").GetString());

        await rig.Client.SubscribeAsync(Commands.Bars(_barType), CancellationToken.None);
        JsonElement bars = await rig.NextControlAsync();

        // The socket names a bar length the way spot does while the REST endpoint on this market wants a number of
        // minutes. Two spellings of one fact, and this is the one the socket takes.
        Assert.Equal("/contractMarket/limitCandle:XBTUSDTM_1min", bars.GetProperty("topic").GetString());

        await rig.Client.SubscribeAsync(Commands.Book(_btc), CancellationToken.None);
        JsonElement book = await rig.NextControlAsync();
        Assert.Equal("/contractMarket/level2Depth50:XBTUSDTM", book.GetProperty("topic").GetString());

        Assert.All(
            new[] { quotes, trades, bars, book },
            m => Assert.False(m.GetProperty("privateChannel").GetBoolean()));
    }

    // ----- the unit every size arrives in -----

    [Fact]
    public async Task A_quote_carries_its_sizes_in_base_currency_and_its_time_from_nanoseconds()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        await rig.Client.SubscribeAsync(Commands.Quotes(_btc), CancellationToken.None);
        await rig.NextControlAsync();

        await rig.Session.SendTextAsync("""
            {"topic":"/contractMarket/tickerV2:XBTUSDTM","type":"message","subject":"tickerV2","data":
             {"symbol":"XBTUSDTM","sequence":1747887438553,"bestBidSize":804,"bestBidPrice":"84116.9",
              "bestAskPrice":"84117","bestAskSize":65,"ts":1729177117878000000}}
            """);

        QuoteTick quote = Assert.IsType<QuoteTick>(await rig.Sink.NextDataAsync());
        Assert.Equal(new Price(84116.9m, 1), quote.Bid);
        Assert.Equal(new Price(84117m, 1), quote.Ask);

        // 804 and 65 CONTRACTS, which is 0.804 and 0.065 XBT. Passed through as 804 they would read as 804 bitcoin.
        Assert.Equal(new Quantity(0.804m, 3), quote.BidSize);
        Assert.Equal(new Quantity(0.065m, 3), quote.AskSize);

        // Nanoseconds already, unlike the book on the same socket, which stamps in milliseconds.
        Assert.Equal(new UnixNanos(1729177117878000000), quote.TsEvent);
    }

    [Fact]
    public async Task A_trade_carries_its_size_in_base_currency_and_the_takers_side()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        await rig.Client.SubscribeAsync(Commands.Trades(_btc), CancellationToken.None);
        await rig.NextControlAsync();

        await rig.Session.SendTextAsync("""
            {"topic":"/contractMarket/execution:XBTUSDTM","type":"message","subject":"match","data":
             {"symbol":"XBTUSDTM","sequence":1944971621109,"side":"sell","size":12,"price":"84117",
              "takerOrderId":"492813530635034624","makerOrderId":"492813485483241472",
              "tradeId":"1944971621109","ts":1729177117878000000}}
            """);

        TradeTick trade = Assert.IsType<TradeTick>(await rig.Sink.NextDataAsync());
        Assert.Equal(new Price(84117m, 1), trade.Price);
        Assert.Equal(new Quantity(0.012m, 3), trade.Size);
        Assert.Equal(AggressorSide.Seller, trade.Aggressor);
        Assert.Equal("1944971621109", trade.TradeId.Value);
        Assert.Equal(new UnixNanos(1729177117878000000), trade.TsEvent);
    }

    [Fact]
    public async Task A_book_snapshot_becomes_a_quote_with_its_sizes_converted_and_its_time_from_milliseconds()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        await rig.Client.SubscribeAsync(Commands.Book(_btc), CancellationToken.None);
        await rig.NextControlAsync();

        await rig.Session.SendTextAsync("""
            {"topic":"/contractMarket/level2Depth50:XBTUSDTM","type":"message","subject":"level2","data":
             {"sequence":1747887438793,"asks":[["84117",204],["84122.7",5]],
              "bids":[["84116.9",3121],["84116.1",7]],"timestamp":1729177117878}}
            """);

        QuoteTick quote = Assert.IsType<QuoteTick>(await rig.Sink.NextDataAsync());
        Assert.Equal(new Price(84116.9m, 1), quote.Bid);
        Assert.Equal(new Price(84117m, 1), quote.Ask);
        Assert.Equal(new Quantity(3.121m, 3), quote.BidSize);
        Assert.Equal(new Quantity(0.204m, 3), quote.AskSize);

        // Milliseconds here, nanoseconds on the ticker. Read as nanoseconds this stamp would land in 1970.
        Assert.Equal(UnixNanos.FromMilliseconds(1729177117878), quote.TsEvent);
    }

    // ----- the candle, and the layout that is not the REST one -----

    [Fact]
    public async Task A_streamed_candle_is_read_in_the_socket_layout_not_the_REST_one()
    {
        // The row is [start in SECONDS, open, close, high, low, turnover, volume in contracts]. The REST endpoint on
        // this same market answers [start in ms, open, high, low, close, volume, turnover]. The five numbers below
        // are distinct and ordered so that reading them the REST way puts the high at 250.5 and the close at 400.4,
        // and reading the volume from field 5 gives 1.5986451 XBT instead of 0.019.
        await using Rig rig = await new Rig(handleRevisedBars: true).ConnectAsync();
        await rig.Client.SubscribeAsync(Commands.Bars(_barType), CancellationToken.None);
        await rig.NextControlAsync();

        await rig.Session.SendTextAsync("""
            {"topic":"/contractMarket/limitCandle:XBTUSDTM_1min","type":"message","subject":"candle.stick","data":
             {"symbol":"XBTUSDTM","candles":["1699999980","100.1","250.5","400.4","10.2","1598.6451","19"],
              "time":1699999985000}}
            """);

        Bar bar = Assert.IsType<Bar>(await rig.Sink.NextDataAsync());
        Assert.Equal(new Price(100.1m, 1), bar.Open);
        Assert.Equal(new Price(400.4m, 1), bar.High);
        Assert.Equal(new Price(10.2m, 1), bar.Low);
        Assert.Equal(new Price(250.5m, 1), bar.Close);

        // Field 6 is the volume, in contracts: 19 contracts is 0.019 XBT. Field 5 is the turnover in USDT and is not
        // a size at all - taken for one it would report a volume eighty thousand times too big.
        Assert.Equal(new Quantity(0.019m, 3), bar.Volume);
    }

    [Fact]
    public async Task A_candle_is_closed_when_the_next_one_starts_because_the_venue_never_says_so()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        await rig.Client.SubscribeAsync(Commands.Bars(_barType), CancellationToken.None);
        await rig.NextControlAsync();

        // TestKernel.Now is 1,700,000,000s, so the minute forming then began at 1,699,999,980.
        await rig.Session.SendTextAsync("""
            {"topic":"/contractMarket/limitCandle:XBTUSDTM_1min","type":"message","subject":"candle.stick","data":
             {"symbol":"XBTUSDTM","candles":["1699999980","100","110","120","90","1000","50"],"time":1699999985000}}
            """);
        await rig.Session.SendTextAsync("""
            {"topic":"/contractMarket/limitCandle:XBTUSDTM_1min","type":"message","subject":"candle.stick","data":
             {"symbol":"XBTUSDTM","candles":["1700000040","111","112","113","110","1000","10"],"time":1700000045000}}
            """);

        // The first interval is published only once the second appears, and it is stamped when it closed.
        Bar closed = Assert.IsType<Bar>(await rig.Sink.NextDataAsync());
        Assert.Equal(new Price(100m, 1), closed.Open);
        Assert.Equal(new Price(110m, 1), closed.Close);
        Assert.Equal(new Quantity(0.05m, 3), closed.Volume);
        Assert.Equal(new UnixNanos(1700000040L * 1_000_000_000L), closed.TsEvent);
        Assert.False(closed.IsRevision);
    }

    // ----- what a perpetual is priced against -----

    [Fact]
    public async Task The_mark_price_the_index_and_the_funding_rate_arrive_on_one_topic_under_two_subjects()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        await rig.Client.SubscribeAsync(Commands.Mark(_btc), CancellationToken.None);
        JsonElement sub = await rig.NextControlAsync();
        Assert.Equal("/contract/instrument:XBTUSDTM", sub.GetProperty("topic").GetString());

        await rig.Session.SendTextAsync("""
            {"topic":"/contract/instrument:XBTUSDTM","type":"message","subject":"mark.index.price","data":
             {"markPrice":84119.88,"indexPrice":84161.58,"granularity":1000,"timestamp":1729177117878}}
            """);

        MarkPriceUpdate mark = Assert.IsType<MarkPriceUpdate>(await rig.Sink.NextDataAsync());
        Assert.Equal(new Price(84119.9m, 1), mark.Value);
        Assert.Equal(UnixNanos.FromMilliseconds(1729177117878), mark.TsEvent);

        IndexPriceUpdate index = Assert.IsType<IndexPriceUpdate>(await rig.Sink.NextDataAsync());
        Assert.Equal(new Price(84161.6m, 1), index.Value);

        await rig.Session.SendTextAsync("""
            {"topic":"/contract/instrument:XBTUSDTM","type":"message","subject":"funding.rate","data":
             {"period":1,"granularity":60000,"fundingRate":0.000046,"timestamp":1729177117879}}
            """);

        FundingRateUpdate funding = Assert.IsType<FundingRateUpdate>(await rig.Sink.NextDataAsync());
        Assert.Equal(0.000046m, funding.Rate);

        // The venue puts no next-settlement time on this subject, so none is invented. Settlement is every eight
        // hours and what was actually charged comes from the funding history endpoint.
        Assert.Null(funding.NextFundingTime);
    }

    // ----- REST -----

    [Fact]
    public async Task Recent_trades_come_back_oldest_first_with_their_sizes_converted()
    {
        await using Rig rig = await new Rig().ConnectAsync();

        await rig.Client.RequestAsync(Commands.RequestTrades(_btc), CancellationToken.None);

        TradeTick[] trades = [.. (await rig.Sink.NextResponseAsync()).Data.Cast<TradeTick>()];
        Assert.Equal(2, trades.Length);
        Assert.True(trades[0].TsEvent < trades[1].TsEvent, "oldest first");
        Assert.Equal(new Quantity(0.003m, 3), trades[0].Size);
        Assert.Equal(new Quantity(0.012m, 3), trades[1].Size);
        Assert.Equal(AggressorSide.Seller, trades[0].Aggressor);
    }

    // ----- parity with the market beside it -----

    [Fact]
    public void The_futures_client_answers_every_command_the_spot_client_answers()
    {
        // The parity table finds a venue's clients by naming convention, so this second data client on one venue is
        // invisible to it. Without this, the futures client could quietly stop implementing something the spot one
        // does and the table that exists to catch exactly that would not notice.
        string[] spot = Commands.Overridden(typeof(KucoinDataClient));
        string[] futures = Commands.Overridden(typeof(KucoinFuturesDataClient));

        string[] missing = [.. spot.Except(futures, StringComparer.Ordinal)];
        Assert.True(
            missing.Length == 0,
            $"the KuCoin futures data client does not implement {string.Join(", ", missing)}, which the spot client "
            + "does. The base class would answer with a completed task and a caller would wait for nothing.");
    }
}
