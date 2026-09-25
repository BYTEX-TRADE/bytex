using System.Globalization;
using System.Text.Json;
using Bytex.Adapters.Kucoin;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Adapters.Tests.Kucoin;

// Why: KuCoin differs from the other venues in ways that silently produce wrong data: the stream address comes from a REST
// call with a token, a candle is [start, open, CLOSE, HIGH, LOW, volume] and is never marked closed, REST candles come
// newest first with the forming one included, and trade times are nanoseconds in a string. Each of those is pinned here
// against the venue's own message shapes, with expected values worked out by hand.
public sealed class KucoinDataClientTests
{
    private static readonly InstrumentId _btc = InstrumentId.Parse("BTC-USDT.KUCOIN");
    private static readonly BarType _barType = BarType.Parse("BTC-USDT.KUCOIN-1-MINUTE-LAST-EXTERNAL");

    // TestKernel.Now is 1,700,000,000 s; the minute that is forming then starts at 1,699,999,980.
    private const long FormingStart = 1_699_999_980;
    private const long Minute = 60;

    private sealed class Rig : IAsyncDisposable
    {
        private WsSession? _session;

        public Rig(Func<RecordedRequest, StubResponse>? candles = null, bool handleRevisedBars = false)
        {
            Routes routes = new Routes()
                .On("GET", "/api/v2/symbols", KucoinPayloads.Symbols)
                .On("POST", "/api/v1/bullet-public", _ => StubResponse.Json(KucoinPayloads.Bullet(Server!.WsBase, "public-token-" + (++Tokens))))
                .On("GET", "/api/v1/market/candles", candles ?? (_ => StubResponse.Json(KucoinPayloads.Envelope("[]"))))
                .On("GET", "/api/v1/market/histories", KucoinPayloads.Envelope("""
                    [{"sequence":"10976028003549185","price":"67122","size":"0.000025","side":"buy","time":1729177117877000000},
                     {"sequence":"10976028003549188","price":"67122.9","size":"0.01792257","side":"sell","time":1729177117878000000}]
                    """));
            Server = new LoopbackServer(routes.Handle);
            Kernel = new TestKernel();
            Client = new KucoinDataClient(new ClientId("KUCOIN"), new KucoinDataClientConfig
            {
                BaseUrlHttp = Server.HttpBase,
                HandleRevisedBars = handleRevisedBars,
                InstrumentProvider = new Core.Adapters.InstrumentProviderConfig { LoadAll = true },
            }, Kernel.Services);
            Client.AttachSink(Sink);
        }

        public int Tokens { get; private set; }

        public LoopbackServer Server { get; }

        public TestKernel Kernel { get; }

        public KucoinDataClient Client { get; }

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

    [Fact]
    public async Task Instruments_take_tick_step_minimums_and_fees_from_the_symbol_list_and_leave_out_pairs_that_do_not_trade()
    {
        await using Rig rig = await new Rig().ConnectAsync();

        Assert.Equal(["BTC-USDT.KUCOIN", "ETH-USDT.KUCOIN"], rig.Sink.Instruments.Select(i => i.Id.ToString()).Order(StringComparer.Ordinal));
        Instrument btc = rig.Client.Instruments.Find(_btc)!;
        Assert.IsType<CurrencyPair>(btc);
        Assert.Equal("BTC-USDT", btc.RawSymbol.Value);
        Assert.Equal(new Price(0.1m, 1), btc.PriceIncrement);
        Assert.Equal(new Quantity(0.00000001m, 8), btc.SizeIncrement);
        Assert.Equal(new Quantity(0.00001m, 8), btc.MinQuantity);
        // 10,000,000,000 is the venue's way of saying "no maximum".
        Assert.Null(btc.MaxQuantity);
        Assert.Equal(new Money(0.1m, Currencies.USDT), btc.MinNotional);
        Assert.Equal((0.001m, 0.001m), (btc.MakerFee, btc.TakerFee));

        // Base rate 0.1% times the symbol's coefficients 2 and 3.
        Instrument eth = rig.Client.Instruments.Find(InstrumentId.Parse("ETH-USDT.KUCOIN"))!;
        Assert.Equal((0.002m, 0.003m), (eth.MakerFee, eth.TakerFee));
        Assert.Equal(new Quantity(5000m, 7), eth.MaxQuantity);
    }

    [Fact]
    public async Task The_stream_address_and_its_token_come_from_the_public_bullet_call()
    {
        await using Rig rig = await new Rig().ConnectAsync();

        RecordedRequest bullet = Assert.Single(rig.Server.RequestsTo("/api/v1/bullet-public"));
        Assert.Equal("POST", bullet.Method);
        Assert.Null(bullet.Header("KC-API-KEY"));
        Assert.Equal("/endpoint", rig.Session.Path);
        Assert.Contains("token=public-token-1", rig.Session.RawQuery, StringComparison.Ordinal);
        Assert.Contains("connectId=", rig.Session.RawQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_dropped_stream_reconnects_with_a_new_token_and_subscribes_again()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        await rig.Client.SubscribeAsync(Commands.Trades(_btc), CancellationToken.None);
        await rig.NextControlAsync();

        rig.Session.Drop();
        WsSession second = await rig.Server.NextSessionAsync();

        Assert.Contains("token=public-token-2", second.RawQuery, StringComparison.Ordinal);
        using JsonDocument again = JsonDocument.Parse(await second.ReceiveTextAsync(Wait.Timeout));
        Assert.Equal(("subscribe", "/market/match:BTC-USDT"), (again.RootElement.GetProperty("type").GetString(), again.RootElement.GetProperty("topic").GetString()));
    }

    [Fact]
    public async Task Subscriptions_use_the_documented_topics_as_public_channels()
    {
        await using Rig rig = await new Rig().ConnectAsync();

        await rig.Client.SubscribeAsync(Commands.Quotes(_btc), CancellationToken.None);
        await rig.Client.SubscribeAsync(Commands.Trades(_btc), CancellationToken.None);
        await rig.Client.SubscribeAsync(Commands.Bars(_barType), CancellationToken.None);
        await rig.Client.SubscribeAsync(Commands.Book(_btc), CancellationToken.None);
        await rig.Client.SubscribeAsync(Commands.Trades(_btc), CancellationToken.None);

        List<JsonElement> sent = [await rig.NextControlAsync(), await rig.NextControlAsync(), await rig.NextControlAsync(), await rig.NextControlAsync()];
        Assert.Equal(
            ["/spotMarket/level1:BTC-USDT", "/market/match:BTC-USDT", "/market/candles:BTC-USDT_1min", "/spotMarket/level2Depth50:BTC-USDT"],
            sent.Select(m => m.GetProperty("topic").GetString()));
        Assert.All(sent, m =>
        {
            Assert.Equal("subscribe", m.GetProperty("type").GetString());
            Assert.False(m.GetProperty("privateChannel").GetBoolean());
            Assert.True(m.GetProperty("response").GetBoolean());
        });
    }

    [Fact]
    public async Task A_subscription_the_venue_does_not_offer_is_refused_with_a_reason()
    {
        await using Rig rig = await new Rig().ConnectAsync();

        await rig.Client.SubscribeAsync(Commands.Mark(_btc), CancellationToken.None);

        (SubscribeCommand command, string reason) = Assert.Single(rig.Sink.SubscriptionFailures);
        Assert.IsType<SubscribeMarkPrices>(command);
        Assert.Contains("KuCoin spot", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Level1_becomes_a_quote_with_both_sides_and_the_venue_time()
    {
        await using Rig rig = await new Rig().ConnectAsync();

        await rig.Session.SendTextAsync(KucoinPayloads.Level1);

        QuoteTick quote = Assert.IsType<QuoteTick>(await rig.Sink.NextDataAsync());
        Assert.Equal(_btc, quote.InstrumentId);
        Assert.Equal((new Price(68_145.7m, 1), new Price(68_145.8m, 1)), (quote.Bid, quote.Ask));
        Assert.Equal((new Quantity(1.29267802m, 8), new Quantity(0.51987471m, 8)), (quote.BidSize, quote.AskSize));
        Assert.Equal(1_729_816_058_766_000_000L, quote.TsEvent.Value);
    }

    [Fact]
    public async Task A_match_becomes_a_trade_with_the_takers_side_and_a_nanosecond_time()
    {
        await using Rig rig = await new Rig().ConnectAsync();

        await rig.Session.SendTextAsync(KucoinPayloads.Match);

        TradeTick trade = Assert.IsType<TradeTick>(await rig.Sink.NextDataAsync());
        Assert.Equal((new Price(67_523m, 1), new Quantity(0.003m, 8), AggressorSide.Buyer), (trade.Price, trade.Size, trade.Aggressor));
        Assert.Equal(new TradeId("11067996711960577"), trade.TradeId);
        Assert.Equal(1_729_843_222_921_000_000L, trade.TsEvent.Value);
    }

    [Fact]
    public async Task A_candle_is_published_once_when_the_next_one_starts_with_close_high_and_low_in_the_venues_order()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        await rig.Client.SubscribeAsync(Commands.Bars(_barType), CancellationToken.None);
        long start = FormingStart - Minute;

        // open 100, CLOSE 101, HIGH 105, LOW 99: the third and fourth fields are not high and low.
        await rig.Session.SendTextAsync(KucoinPayloads.Candle(start, "100", "100.5", "101", "99.5", "1"));
        await rig.Session.SendTextAsync(KucoinPayloads.Candle(start, "100", "101", "105", "99", "2.5"));
        await rig.Session.SendTextAsync(KucoinPayloads.Candle(FormingStart, "101", "102", "102", "101", "0.1"));
        await rig.Session.SendTextAsync(KucoinPayloads.Match);

        Bar bar = Assert.IsType<Bar>(await rig.Sink.NextDataAsync());
        Assert.Equal((new Price(100m, 1), new Price(105m, 1), new Price(99m, 1), new Price(101m, 1)), (bar.Open, bar.High, bar.Low, bar.Close));
        Assert.Equal(new Quantity(2.5m, 8), bar.Volume);
        Assert.Equal(FormingStart * UnixNanos.NanosPerSecond, bar.TsEvent.Value);
        Assert.False(bar.IsRevision);
        // Nothing else was published for the candle: the next thing out is the trade.
        Assert.IsType<TradeTick>(await rig.Sink.NextDataAsync());
    }

    [Fact]
    public async Task On_a_quiet_market_a_candle_is_closed_a_moment_after_its_interval_ends_and_late_updates_for_it_are_dropped()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        await rig.Client.SubscribeAsync(Commands.Bars(_barType), CancellationToken.None);
        await rig.Session.SendTextAsync(KucoinPayloads.Candle(FormingStart, "100", "101", "105", "99", "2.5"));
        await rig.Session.SendTextAsync(KucoinPayloads.Match);
        Assert.IsType<TradeTick>(await rig.Sink.NextDataAsync());

        // The interval ends at +60 s; three seconds later no new candle has come.
        rig.Kernel.Clock.SetTime(new UnixNanos((FormingStart + Minute + 3) * UnixNanos.NanosPerSecond));

        Bar bar = Assert.IsType<Bar>(await rig.Sink.NextDataAsync());
        Assert.Equal(((FormingStart + Minute) * UnixNanos.NanosPerSecond, new Price(101m, 1)), (bar.TsEvent.Value, bar.Close));

        await rig.Session.SendTextAsync(KucoinPayloads.Candle(FormingStart, "100", "90", "105", "90", "9"));
        await rig.Session.SendTextAsync(KucoinPayloads.Match);
        Assert.IsType<TradeTick>(await rig.Sink.NextDataAsync());
    }

    [Fact]
    public async Task An_interval_without_a_trade_is_published_as_a_flat_bar_at_the_previous_close_with_no_volume()
    {
        // Recorded from the live venue: its history shows a quiet minute as O=H=L=C=previous close, volume 0, but the stream
        // sends nothing for it. The client builds the same bar as time passes.
        await using Rig rig = await new Rig().ConnectAsync();
        await rig.Client.SubscribeAsync(Commands.Bars(_barType), CancellationToken.None);
        await rig.Session.SendTextAsync(KucoinPayloads.Candle(FormingStart, "100", "101", "105", "99", "2.5"));
        await rig.Session.SendTextAsync(KucoinPayloads.Match);
        Assert.IsType<TradeTick>(await rig.Sink.NextDataAsync());

        rig.Kernel.Clock.SetTime(new UnixNanos((FormingStart + Minute + 3) * UnixNanos.NanosPerSecond));
        Bar traded = Assert.IsType<Bar>(await rig.Sink.NextDataAsync());
        rig.Kernel.Clock.SetTime(new UnixNanos((FormingStart + 2 * Minute + 3) * UnixNanos.NanosPerSecond));
        Bar quiet = Assert.IsType<Bar>(await rig.Sink.NextDataAsync());

        Assert.Equal(new Quantity(2.5m, 8), traded.Volume);
        Assert.Equal((FormingStart + 2 * Minute) * UnixNanos.NanosPerSecond, quiet.TsEvent.Value);
        Assert.Equal((new Price(101m, 1), new Price(101m, 1), new Price(101m, 1), new Price(101m, 1), new Quantity(0m, 8)), (quiet.Open, quiet.High, quiet.Low, quiet.Close, quiet.Volume));
        Assert.False(quiet.IsRevision);
    }

    [Fact]
    public async Task A_trade_in_an_interval_that_started_flat_replaces_the_flat_candle_with_the_venues_own()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        await rig.Client.SubscribeAsync(Commands.Bars(_barType), CancellationToken.None);
        await rig.Session.SendTextAsync(KucoinPayloads.Candle(FormingStart, "100", "101", "105", "99", "2.5"));
        await rig.Session.SendTextAsync(KucoinPayloads.Match);
        Assert.IsType<TradeTick>(await rig.Sink.NextDataAsync());
        rig.Kernel.Clock.SetTime(new UnixNanos((FormingStart + Minute + 3) * UnixNanos.NanosPerSecond));
        Assert.IsType<Bar>(await rig.Sink.NextDataAsync());

        // The next interval is flat so far; then it trades.
        await rig.Session.SendTextAsync(KucoinPayloads.Candle(FormingStart + Minute, "102", "103", "104", "101.5", "0.7"));
        await rig.Session.SendTextAsync(KucoinPayloads.Match);
        Assert.IsType<TradeTick>(await rig.Sink.NextDataAsync());
        rig.Kernel.Clock.SetTime(new UnixNanos((FormingStart + 2 * Minute + 3) * UnixNanos.NanosPerSecond));

        Bar bar = Assert.IsType<Bar>(await rig.Sink.NextDataAsync());
        Assert.Equal((new Price(102m, 1), new Price(104m, 1), new Price(101.5m, 1), new Price(103m, 1), new Quantity(0.7m, 8)), (bar.Open, bar.High, bar.Low, bar.Close, bar.Volume));
    }

    [Fact]
    public async Task On_a_market_that_has_not_traded_since_the_subscription_bars_start_flat_from_the_last_closed_bar()
    {
        long lastTraded = FormingStart - 5 * Minute;
        await using Rig rig = await new Rig(_ => StubResponse.Json(KucoinPayloads.Envelope(CandleRows([lastTraded])))).ConnectAsync();

        await rig.Client.SubscribeAsync(Commands.Bars(_barType), CancellationToken.None);
        // The seed is a REST call; give it a moment, then let the forming interval end.
        for (int i = 0; i < 100 && rig.Server.RequestsTo("/api/v1/market/candles").Count == 0; i++)
        {
            await Task.Delay(20);
        }

        await Task.Delay(100);
        rig.Kernel.Clock.SetTime(new UnixNanos((FormingStart + Minute + 3) * UnixNanos.NanosPerSecond));

        Bar bar = Assert.IsType<Bar>(await rig.Sink.NextDataAsync());
        Assert.Equal((FormingStart + Minute) * UnixNanos.NanosPerSecond, bar.TsEvent.Value);
        Assert.Equal((new Price(101m, 1), new Quantity(0m, 8)), (bar.Close, bar.Volume));
    }

    [Fact]
    public async Task History_shows_quiet_intervals_as_flat_bars_up_to_the_last_closed_interval_as_the_venue_later_will()
    {
        // The venue answered with two traded minutes and nothing for the four quiet ones between and after them.
        long first = FormingStart - 5 * Minute;
        long second = FormingStart - 3 * Minute;
        await using Rig rig = await new Rig(_ => StubResponse.Json(KucoinPayloads.Envelope(
            "[[\"" + second + "\",\"110\",\"111\",\"112\",\"109\",\"3\",\"1\"],[\"" + first + "\",\"100\",\"101\",\"105\",\"99\",\"2\",\"1\"]]"))).ConnectAsync();

        await rig.Client.RequestAsync(Commands.RequestBars(_barType, null, null, 10), CancellationToken.None);

        List<Bar> bars = (await rig.Sink.NextResponseAsync()).Data.Cast<Bar>().ToList();
        // Closes: first+1 (traded), first+2 (flat at 101), second+1 (traded), then flat at 111 up to the forming candle's start.
        Assert.Equal(
            [first + Minute, first + 2 * Minute, second + Minute, second + 2 * Minute, second + 3 * Minute],
            bars.Select(b => b.TsEvent.Value / UnixNanos.NanosPerSecond));
        Assert.Equal([2m, 0m, 3m, 0m, 0m], bars.Select(b => b.Volume.Value));
        Assert.Equal([101m, 101m, 111m, 111m, 111m], bars.Select(b => b.Close.Value));
        Assert.All(bars.Where(b => b.Volume.Value == 0m), b => Assert.Equal((b.Close, b.Close, b.Close), (b.Open, b.High, b.Low)));
        Assert.Equal(FormingStart * UnixNanos.NanosPerSecond, bars[^1].TsEvent.Value);
    }

    [Fact]
    public async Task With_handleRevisedBars_every_update_of_the_forming_candle_is_forwarded_as_a_revision()
    {
        await using Rig rig = await new Rig(handleRevisedBars: true).ConnectAsync();
        await rig.Client.SubscribeAsync(Commands.Bars(_barType), CancellationToken.None);

        await rig.Session.SendTextAsync(KucoinPayloads.Candle(FormingStart, "100", "101", "105", "99", "2.5"));

        Bar bar = Assert.IsType<Bar>(await rig.Sink.NextDataAsync());
        Assert.True(bar.IsRevision);
        Assert.Equal(new Price(101m, 1), bar.Close);
    }

    [Fact]
    public async Task The_depth_channel_is_a_snapshot_every_time()
    {
        await using Rig rig = await new Rig().ConnectAsync();

        await rig.Session.SendTextAsync(KucoinPayloads.Depth);

        OrderBookDeltas deltas = Assert.IsType<OrderBookDeltas>(await rig.Sink.NextDataAsync());
        Assert.True(deltas.Flags.HasFlag(RecordFlags.Snapshot));
        Assert.Equal(BookAction.Clear, deltas.Deltas[0].Action);
        Assert.Equal(3, deltas.Deltas.Count(d => d.Action == BookAction.Add && d.Order.Side == OrderSide.Buy));
        Assert.Equal(2, deltas.Deltas.Count(d => d.Action == BookAction.Add && d.Order.Side == OrderSide.Sell));
        Assert.Contains(deltas.Deltas, d => d.Order.Side == OrderSide.Buy && d.Order.Price == new Price(95_960m, 1) && d.Order.Size == new Quantity(1.25m, 8));
        Assert.Equal(1_733_124_805_073_000_000L, deltas.TsEvent.Value);
    }

    private static string CandleRows(IEnumerable<long> startsNewestFirst) =>
        "[" + string.Join(",", startsNewestFirst.Select(s => $"[\"{s.ToString(CultureInfo.InvariantCulture)}\",\"100\",\"101\",\"105\",\"99\",\"2\",\"200\"]")) + "]";

    [Fact]
    public async Task Requested_bars_are_closed_bars_oldest_first_without_the_forming_candle()
    {
        List<RecordedRequest> seen = new();
        await using Rig rig = await new Rig(r =>
        {
            seen.Add(r);
            // Newest first, the forming candle on top, as the venue answers.
            return StubResponse.Json(KucoinPayloads.Envelope(CandleRows([FormingStart, FormingStart - Minute, FormingStart - 2 * Minute, FormingStart - 3 * Minute, FormingStart - 4 * Minute])));
        }).ConnectAsync();

        await rig.Client.RequestAsync(Commands.RequestBars(_barType, null, null, 3), CancellationToken.None);

        DataResponse response = await rig.Sink.NextResponseAsync();
        Assert.Null(response.Error);
        List<Bar> bars = response.Data.Cast<Bar>().ToList();
        Assert.Equal(
            [(FormingStart - 2 * Minute) * UnixNanos.NanosPerSecond, (FormingStart - Minute) * UnixNanos.NanosPerSecond, FormingStart * UnixNanos.NanosPerSecond],
            bars.Select(b => b.TsEvent.Value));
        Assert.All(bars, b => Assert.Equal((new Price(100m, 1), new Price(105m, 1), new Price(99m, 1), new Price(101m, 1)), (b.Open, b.High, b.Low, b.Close)));
        RecordedRequest request = Assert.Single(seen);
        Assert.Equal(("BTC-USDT", "1min"), (request.Query("symbol"), request.Query("type")));
        // Seconds, not milliseconds.
        Assert.Equal("1700000001", request.Query("endAt"));
    }

    [Fact]
    public async Task An_end_one_millisecond_before_a_boundary_takes_the_candle_that_closed_there_and_not_the_one_that_opens_there()
    {
        await using Rig rig = await new Rig(_ => StubResponse.Json(KucoinPayloads.Envelope(CandleRows([FormingStart, FormingStart - Minute, FormingStart - 2 * Minute])))).ConnectAsync();
        UnixNanos end = new(FormingStart * UnixNanos.NanosPerSecond - 1_000_000L);

        await rig.Client.RequestAsync(Commands.RequestBars(_barType, null, end, 10), CancellationToken.None);

        List<Bar> bars = (await rig.Sink.NextResponseAsync()).Data.Cast<Bar>().ToList();
        Assert.Equal([(FormingStart - Minute) * UnixNanos.NanosPerSecond, FormingStart * UnixNanos.NanosPerSecond], bars.Select(b => b.TsEvent.Value));
    }

    [Fact]
    public async Task More_than_one_page_of_bars_is_fetched_page_by_page_without_gaps_or_repeats()
    {
        int calls = 0;
        await using Rig rig = await new Rig(r =>
        {
            calls++;
            long startAt = long.Parse(r.Query("startAt")!, CultureInfo.InvariantCulture);
            long endAt = long.Parse(r.Query("endAt")!, CultureInfo.InvariantCulture);
            long newest = Math.Min(FormingStart, (endAt - 1) / Minute * Minute);
            List<long> starts = new();
            for (long s = newest; s >= startAt && starts.Count < 1500; s -= Minute)
            {
                starts.Add(s);
            }

            return StubResponse.Json(KucoinPayloads.Envelope(CandleRows(starts)));
        }).ConnectAsync();

        await rig.Client.RequestAsync(Commands.RequestBars(_barType, null, null, 2000), CancellationToken.None);

        List<Bar> bars = (await rig.Sink.NextResponseAsync()).Data.Cast<Bar>().ToList();
        Assert.Equal(2000, bars.Count);
        Assert.Equal(2, calls);
        Assert.Equal(FormingStart * UnixNanos.NanosPerSecond, bars[^1].TsEvent.Value);
        Assert.All(bars.Zip(bars.Skip(1)), pair => Assert.Equal(Minute * UnixNanos.NanosPerSecond, pair.Second.TsEvent.Value - pair.First.TsEvent.Value));
    }

    // A venue that has written only the given candles (start second -> close price) and answers a query the way KuCoin
    // does: the rows that open inside [startAt, endAt], newest first.
    private static Func<RecordedRequest, StubResponse> Written(params (long Start, string Close)[] candles) => r =>
    {
        long startAt = long.Parse(r.Query("startAt")!, CultureInfo.InvariantCulture);
        long endAt = long.Parse(r.Query("endAt")!, CultureInfo.InvariantCulture);
        IEnumerable<string> rows = candles.Where(c => c.Start >= startAt && c.Start <= endAt).OrderByDescending(c => c.Start)
            .Select(c => $"[\"{c.Start.ToString(CultureInfo.InvariantCulture)}\",\"{c.Close}\",\"{c.Close}\",\"{c.Close}\",\"{c.Close}\",\"2\",\"200\"]");
        return StubResponse.Json(KucoinPayloads.Envelope("[" + string.Join(",", rows) + "]"));
    };

    private static UnixNanos At(long seconds) => new(seconds * UnixNanos.NanosPerSecond);

    [Fact]
    public async Task A_limit_with_a_start_takes_the_oldest_bars_from_that_start()
    {
        // The limit is counted from whichever end the caller anchored, and it is the same on every venue: a start
        // says where the window begins, so the limit caps how many bars follow it. This venue took the NEWEST even
        // when a start was given, so the same request answered by KuCoin and by Binance gave different bars, and
        // nothing had ever asked it for a start and a limit together.
        long t = FormingStart - 60 * Minute;
        await using Rig rig = await new Rig(Written((t, "100"), (t + 3 * Minute, "103"))).ConnectAsync();

        await rig.Client.RequestAsync(Commands.RequestBars(_barType, At(t + Minute), At(t + 5 * Minute), 3), CancellationToken.None);

        List<Bar> bars = (await rig.Sink.NextResponseAsync()).Data.Cast<Bar>().ToList();
        Assert.Equal([t + Minute, t + 2 * Minute, t + 3 * Minute], bars.Select(b => b.TsEvent.Value / UnixNanos.NanosPerSecond));
    }

    [Fact]
    public async Task A_limit_with_no_start_takes_the_newest_bars()
    {
        // With nothing anchoring the front, the newest are the ones wanted - which is what a limit alone has always
        // meant, on this venue and the others.
        long t = FormingStart - 60 * Minute;
        await using Rig rig = await new Rig(Written((t, "100"), (t + 3 * Minute, "103"))).ConnectAsync();

        await rig.Client.RequestAsync(Commands.RequestBars(_barType, null, At(t + 5 * Minute), 3), CancellationToken.None);

        List<Bar> bars = (await rig.Sink.NextResponseAsync()).Data.Cast<Bar>().ToList();
        Assert.Equal([t + 4 * Minute, t + 5 * Minute, t + 6 * Minute], bars.Select(b => b.TsEvent.Value / UnixNanos.NanosPerSecond));
    }

    [Fact]
    public async Task A_window_that_lies_in_the_past_is_filled_inside_and_up_to_its_end_and_no_further()
    {
        // An hour ago: trades in the minutes opening at T and T+3 only. The window asks for closes T+1 .. T+6.
        long t = FormingStart - 60 * Minute;
        await using Rig rig = await new Rig(Written((t, "100"), (t + 3 * Minute, "103"), (t + 20 * Minute, "120"))).ConnectAsync();

        await rig.Client.RequestAsync(Commands.RequestBars(_barType, At(t + Minute), At(t + 5 * Minute), null), CancellationToken.None);

        List<Bar> bars = (await rig.Sink.NextResponseAsync()).Data.Cast<Bar>().ToList();
        Assert.Equal([t + Minute, t + 2 * Minute, t + 3 * Minute, t + 4 * Minute, t + 5 * Minute, t + 6 * Minute], bars.Select(b => b.TsEvent.Value / UnixNanos.NanosPerSecond));
        Assert.Equal([100m, 100m, 100m, 103m, 103m, 103m], bars.Select(b => b.Close.Value));
        Assert.Equal([2m, 0m, 0m, 2m, 0m, 0m], bars.Select(b => b.Volume.Value));
    }

    [Fact]
    public async Task A_window_that_starts_in_a_quiet_stretch_is_filled_from_the_close_before_it()
    {
        // The last trade before the window was ten minutes earlier, at 95; inside the window only T+2 traded.
        long t = FormingStart - 60 * Minute;
        await using Rig rig = await new Rig(Written((t - 10 * Minute, "95"), (t + 2 * Minute, "102"))).ConnectAsync();

        await rig.Client.RequestAsync(Commands.RequestBars(_barType, At(t + Minute), At(t + 3 * Minute), null), CancellationToken.None);

        List<Bar> bars = (await rig.Sink.NextResponseAsync()).Data.Cast<Bar>().ToList();
        Assert.Equal([t + Minute, t + 2 * Minute, t + 3 * Minute, t + 4 * Minute], bars.Select(b => b.TsEvent.Value / UnixNanos.NanosPerSecond));
        Assert.Equal([95m, 95m, 102m, 102m], bars.Select(b => b.Close.Value));
        Assert.Equal([0m, 0m, 2m, 0m], bars.Select(b => b.Volume.Value));
    }

    [Fact]
    public async Task A_window_without_a_single_trade_comes_back_flat_and_complete_rather_than_empty()
    {
        long t = FormingStart - 60 * Minute;
        await using Rig rig = await new Rig(Written((t - 10 * Minute, "95"), (t + 30 * Minute, "130"))).ConnectAsync();

        await rig.Client.RequestAsync(Commands.RequestBars(_barType, At(t + Minute), At(t + 3 * Minute), null), CancellationToken.None);

        List<Bar> bars = (await rig.Sink.NextResponseAsync()).Data.Cast<Bar>().ToList();
        Assert.Equal([t + Minute, t + 2 * Minute, t + 3 * Minute, t + 4 * Minute], bars.Select(b => b.TsEvent.Value / UnixNanos.NanosPerSecond));
        Assert.All(bars, b => Assert.Equal((95m, 95m, 95m, 95m, 0m), (b.Open.Value, b.High.Value, b.Low.Value, b.Close.Value, b.Volume.Value)));
    }

    [Fact]
    public async Task A_pair_that_never_traded_before_the_window_has_nothing_to_fill_from_and_starts_at_its_first_trade()
    {
        long t = FormingStart - 60 * Minute;
        await using Rig rig = await new Rig(Written((t + 2 * Minute, "102"))).ConnectAsync();

        await rig.Client.RequestAsync(Commands.RequestBars(_barType, At(t + Minute), At(t + 3 * Minute), null), CancellationToken.None);

        List<Bar> bars = (await rig.Sink.NextResponseAsync()).Data.Cast<Bar>().ToList();
        Assert.Equal([t + 3 * Minute, t + 4 * Minute], bars.Select(b => b.TsEvent.Value / UnixNanos.NanosPerSecond));
    }

    [Fact]
    public async Task The_public_history_helper_gives_a_catalog_the_same_bars_the_client_gives_a_node()
    {
        long t = FormingStart - 60 * Minute;
        await using Rig rig = await new Rig(Written((t - 10 * Minute, "95"), (t + 2 * Minute, "102"))).ConnectAsync();
        await rig.Client.RequestAsync(Commands.RequestBars(_barType, At(t + Minute), At(t + 3 * Minute), null), CancellationToken.None);
        List<Bar> fromClient = (await rig.Sink.NextResponseAsync()).Data.Cast<Bar>().ToList();

        using KucoinHttp http = new(new KucoinDataClientConfig { BaseUrlHttp = rig.Server.HttpBase });
        Instrument instrument = rig.Sink.Instruments.Single(i => i.Id == _btc);
        IReadOnlyList<Bar> fromHelper = await KucoinHistory.FetchBarsAsync(http, instrument, _barType, At(t + Minute).ToDateTimeOffset(), At(t + 3 * Minute).ToDateTimeOffset());

        Assert.Equal(fromClient, fromHelper);
        Assert.Equal(4, fromHelper.Count);
    }

    [Fact]
    public async Task Recent_trades_come_back_oldest_first_with_their_nanosecond_times()
    {
        await using Rig rig = await new Rig().ConnectAsync();

        await rig.Client.RequestAsync(Commands.RequestTrades(_btc), CancellationToken.None);

        List<TradeTick> trades = (await rig.Sink.NextResponseAsync()).Data.Cast<TradeTick>().ToList();
        Assert.Equal([1_729_177_117_877_000_000L, 1_729_177_117_878_000_000L], trades.Select(t => t.TsEvent.Value));
        Assert.Equal([AggressorSide.Buyer, AggressorSide.Seller], trades.Select(t => t.Aggressor));
        Assert.Equal(new Price(67_122.9m, 1), trades[1].Price);
    }

    [Fact]
    public async Task A_venue_error_answers_the_request_with_the_venues_message()
    {
        await using Rig rig = await new Rig(_ => StubResponse.Error(400, KucoinPayloads.Error("400100", "symbol not exists"))).ConnectAsync();

        await rig.Client.RequestAsync(Commands.RequestBars(_barType, null, null, 5), CancellationToken.None);

        DataResponse response = await rig.Sink.NextResponseAsync();
        Assert.Empty(response.Data);
        Assert.Contains("400100", response.Error, StringComparison.Ordinal);
        Assert.Contains("symbol not exists", response.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, BarAggregation.Minute, "1min")]
    [InlineData(15, BarAggregation.Minute, "15min")]
    [InlineData(1, BarAggregation.Hour, "1hour")]
    [InlineData(8, BarAggregation.Hour, "8hour")]
    [InlineData(1, BarAggregation.Day, "1day")]
    [InlineData(1, BarAggregation.Week, "1week")]
    public void Bar_steps_map_to_the_venues_candle_types(int step, BarAggregation aggregation, string expected)
    {
        Assert.Equal(expected, KucoinVenue.Interval(new BarSpecification(step, aggregation, PriceType.Last)));
    }

    [Fact]
    public void A_bar_step_the_venue_does_not_have_is_refused_by_name()
    {
        NotSupportedException error = Assert.Throws<NotSupportedException>(() => KucoinVenue.Interval(new BarSpecification(10, BarAggregation.Minute, PriceType.Last)));
        Assert.Contains("KuCoin", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pair_keeps_the_venues_name_in_its_instrument_id_and_maps_back_without_a_lookup()
    {
        InstrumentId id = KucoinVenue.ToInstrumentId("BTC-USDT");

        Assert.Equal("BTC-USDT.KUCOIN", id.ToString());
        Assert.Equal("BTC-USDT", KucoinVenue.ToRawSymbol(id));
    }

    [Fact]
    public async Task A_start_and_end_window_longer_than_one_page_returns_every_bar_in_the_window()
    {
        int calls = 0;
        await using Rig rig = await new Rig(r =>
        {
            calls++;
            long startAt = long.Parse(r.Query("startAt")!, CultureInfo.InvariantCulture);
            long endAt = long.Parse(r.Query("endAt")!, CultureInfo.InvariantCulture);
            long newest = Math.Min(FormingStart, (endAt - 1) / Minute * Minute);
            List<long> starts = new();
            for (long s = newest; s >= startAt && starts.Count < 1500; s -= Minute)
            {
                starts.Add(s);
            }

            return StubResponse.Json(KucoinPayloads.Envelope(CandleRows(starts)));
        }).ConnectAsync();

        // 2,500 closed minutes, which is more than the venue's 1,500-row answer can carry.
        long from = FormingStart - (2500 * Minute);
        await rig.Client.RequestAsync(
            Commands.RequestBars(_barType, new UnixNanos(from * UnixNanos.NanosPerSecond), new UnixNanos((FormingStart - Minute) * UnixNanos.NanosPerSecond), null),
            CancellationToken.None);

        List<Bar> bars = (await rig.Sink.NextResponseAsync()).Data.Cast<Bar>().ToList();

        // 2,501 because a start bounds a candle's close, so the candle that closes exactly at it is in the window, as
        // it is on the other adapters; the end bounds a candle's open, so the last one is the minute before the forming.
        Assert.Equal(2501, bars.Count);
        Assert.True(calls >= 2, $"one page cannot hold the window, but only {calls} request(s) were made");
        Assert.Equal(from * UnixNanos.NanosPerSecond, bars[0].TsEvent.Value);
        Assert.Equal(FormingStart * UnixNanos.NanosPerSecond, bars[^1].TsEvent.Value);
        Assert.All(bars.Zip(bars.Skip(1)), pair => Assert.Equal(Minute * UnixNanos.NanosPerSecond, pair.Second.TsEvent.Value - pair.First.TsEvent.Value));
    }
}
