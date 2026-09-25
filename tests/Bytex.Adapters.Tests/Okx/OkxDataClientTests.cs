using System.Text.Json;
using Bytex.Adapters.Okx;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Adapters.Tests.Okx;

// Why: this client opens TWO sockets and has to, which is the one structural surprise on this venue and was measured
// rather than read. Subscribing to candle1m on the venue's public path is refused with code 60018 "wrong URL or
// channel"; the same subscription on its business path delivers. A client that opened one socket would have quotes
// and trades flowing and bars silently absent - a node that starts, connects, receives data and never sees a bar.
//
// The second thing pinned here is the size unit. Measured on the live socket on 2026-09-25, on one connection:
//
//   spot  tickers/trades  sizes are BASE CURRENCY
//   swap  tickers/trades  sizes are CONTRACTS, and one BTC-USDT-SWAP contract is 0.01 BTC
//   candle rows          the volume field is contracts on a swap, with the base figure in the field beside it
//
// So the same channel on the same socket means two different things depending on which market the instrument is in,
// and reading a swap's sizes as base currency understates a quote by a factor of a hundred with nothing to say so.
// Every conversion below is checked against a number that could not be produced any other way.
public sealed class OkxDataClientTests
{
    private const string InstrumentsPath = "/api/v5/public/instruments";
    private const string TiersPath = "/api/v5/public/position-tiers";
    private const string CandlesPath = "/api/v5/market/history-candles";
    private const string TradesPath = "/api/v5/market/trades";
    private const string FundingPath = "/api/v5/public/funding-rate-history";

    private static readonly InstrumentId _pair = InstrumentId.Parse("BTC-USDT.OKX");
    private static readonly InstrumentId _perpetual = InstrumentId.Parse("BTC-USDT-SWAP.OKX");
    private static readonly BarType _pairBars = BarType.Parse("BTC-USDT.OKX-1-MINUTE-LAST-EXTERNAL");
    private static readonly BarType _perpetualBars = BarType.Parse("BTC-USDT-SWAP.OKX-1-MINUTE-LAST-EXTERNAL");

    /// <summary>One BTC-USDT-SWAP contract, so a size of 300 contracts is three bitcoin.</summary>
    private const decimal Contract = 0.01m;

    private sealed class Rig : IAsyncDisposable
    {
        public Rig(OkxInstrumentType type = OkxInstrumentType.Spot, bool handleRevisedBars = false)
        {
            Routes = new Routes()
                .On("GET", InstrumentsPath, r => StubResponse.Json(r.Query("instType") switch
                {
                    "SWAP" => OkxPayloads.SwapInstruments,
                    "FUTURES" => OkxPayloads.FuturesInstruments,
                    _ => OkxPayloads.SpotInstruments,
                }))
                .On("GET", TiersPath, r => StubResponse.Json(r.Query("instType") == "FUTURES" ? OkxPayloads.FuturesTiers : OkxPayloads.SwapTiers))
                .On("GET", CandlesPath, r => StubResponse.Json(r.Query("instId") == "BTC-USDT-SWAP" ? OkxPayloads.SwapCandles : OkxPayloads.SpotCandles))
                .On("GET", TradesPath, OkxPayloads.SpotTrade.Replace("\"arg\":{\"channel\":\"trades\",\"instId\":\"BTC-USDT\"},", string.Empty, StringComparison.Ordinal).Replace("\"data\"", "\"code\":\"0\",\"msg\":\"\",\"data\"", StringComparison.Ordinal))
                .On("GET", FundingPath, OkxPayloads.FundingHistory);

            Server = new LoopbackServer(Routes.Handle);
            Kernel = new TestKernel();
            Client = new OkxDataClient(new ClientId("OKX"), new OkxDataClientConfig
            {
                InstrumentType = type,
                BaseUrlHttp = Server.HttpBase,
                BaseUrlWs = Server.WsBase,
                HandleRevisedBars = handleRevisedBars,
                InstrumentProvider = new InstrumentProviderConfig { LoadAll = true },
            }, Kernel.Services);

            Client.AttachSink(Sink);
        }

        public Routes Routes { get; }

        public LoopbackServer Server { get; }

        public TestKernel Kernel { get; }

        public OkxDataClient Client { get; }

        public RecordingDataSink Sink { get; } = new();

        /// <summary>The socket that carries everything except candles.</summary>
        public WsSession Public { get; private set; } = null!;

        /// <summary>The socket that carries candles and nothing else.</summary>
        public WsSession Business { get; private set; } = null!;

        public async Task ConnectAsync()
        {
            await Client.ConnectAsync(CancellationToken.None).WaitAsync(Wait.Timeout);

            // Identified by the path rather than by the order they arrive in, because which is which is the fact
            // under test.
            WsSession first = await Server.NextSessionAsync();
            WsSession second = await Server.NextSessionAsync();
            (Public, Business) = first.Path == OkxVenue.WsPublicPath ? (first, second) : (second, first);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Server.DisposeAsync();
            Kernel.Dispose();
        }
    }

    /// <summary>The channel and instrument a subscription message names.</summary>
    private static (string Channel, string InstId) Subscription(string message)
    {
        using JsonDocument doc = JsonDocument.Parse(message);
        JsonElement arg = doc.RootElement.GetProperty("args")[0];
        return (arg.GetProperty("channel").GetString()!, arg.GetProperty("instId").GetString()!);
    }

    private static string Op(string message)
    {
        using JsonDocument doc = JsonDocument.Parse(message);
        return doc.RootElement.GetProperty("op").GetString()!;
    }

    // ----- two sockets -----

    [Fact]
    public async Task Connecting_opens_both_of_the_venues_public_paths()
    {
        // Both, at connect, rather than the second when a bar is first asked for: a socket that appears halfway
        // through a run is a socket whose failure looks like a missing subscription.
        await using Rig rig = new();
        await rig.ConnectAsync();

        Assert.Equal(OkxVenue.WsPublicPath, rig.Public.Path);
        Assert.Equal(OkxVenue.WsBusinessPath, rig.Business.Path);
        Assert.True(rig.Client.IsConnected);
    }

    [Fact]
    public async Task A_bar_subscription_goes_to_the_business_socket_and_nothing_else_does()
    {
        // The measured fact this client exists around. The candle channel is refused on the public path with code
        // 60018, so sending it there would be a subscription that is answered with an error and then silence.
        await using Rig rig = new();
        await rig.ConnectAsync();

        await rig.Client.SubscribeAsync(Commands.Quotes(_pair), CancellationToken.None);
        await rig.Client.SubscribeAsync(Commands.Bars(_pairBars), CancellationToken.None);

        Assert.Equal(("tickers", "BTC-USDT"), Subscription(await rig.Public.ReceiveTextAsync()));
        Assert.Equal(("candle1m", "BTC-USDT"), Subscription(await rig.Business.ReceiveTextAsync()));
    }

    [Fact]
    public async Task A_keep_alive_answer_is_the_bare_word_pong_and_is_not_parsed_as_json()
    {
        // Measured: the venue answers the string "ping" with the string "pong". Parsing it as JSON throws, and a
        // client that logged that as an unreadable message would log one every ping interval on both sockets for as
        // long as it ran.
        await using Rig rig = new();
        await rig.ConnectAsync();

        await rig.Public.SendTextAsync(OkxVenue.PongMessage);
        await rig.Business.SendTextAsync(OkxVenue.PongMessage);

        // Nothing reached the sink and nothing threw; the client is still taking messages.
        await rig.Public.SendTextAsync(OkxPayloads.SpotTicker);
        Assert.IsType<QuoteTick>(await rig.Sink.NextDataAsync());
    }

    [Fact]
    public async Task A_reconnection_replays_each_sockets_own_subscriptions()
    {
        // Two sockets means two subscription sets, and a reconnection of one must not replay the other's: a candle
        // subscription sent to the public socket is refused, and a ticker subscription sent to the business socket
        // is too.
        await using Rig rig = new();
        await rig.ConnectAsync();

        await rig.Client.SubscribeAsync(Commands.Quotes(_pair), CancellationToken.None);
        await rig.Client.SubscribeAsync(Commands.Bars(_pairBars), CancellationToken.None);
        Assert.Equal(("tickers", "BTC-USDT"), Subscription(await rig.Public.ReceiveTextAsync()));
        Assert.Equal(("candle1m", "BTC-USDT"), Subscription(await rig.Business.ReceiveTextAsync()));

        rig.Public.Drop();
        WsSession again = await rig.Server.NextSessionAsync();

        Assert.Equal(OkxVenue.WsPublicPath, again.Path);
        Assert.Equal(("tickers", "BTC-USDT"), Subscription(await again.ReceiveTextAsync()));
    }

    // ----- quotes and trades -----

    [Fact]
    public async Task A_spot_quote_carries_the_sizes_the_venue_sent()
    {
        await using Rig rig = new();
        await rig.ConnectAsync();
        await rig.Client.SubscribeAsync(Commands.Quotes(_pair), CancellationToken.None);

        await rig.Public.SendTextAsync(OkxPayloads.SpotTicker);
        QuoteTick quote = Assert.IsType<QuoteTick>(await rig.Sink.NextDataAsync());

        Assert.Equal(_pair, quote.InstrumentId);
        Assert.Equal(83774.1m, quote.Bid.Value);
        Assert.Equal(83774.2m, quote.Ask.Value);
        Assert.Equal(0.65302934m, quote.BidSize.Value);
        Assert.Equal(0.25697131m, quote.AskSize.Value);

        // Milliseconds, which is what this venue stamps everywhere - unlike KuCoin's futures socket, which uses
        // three different units across four topics.
        Assert.Equal(1_790_358_564_077L, quote.TsEvent.ToMilliseconds());
    }

    [Fact]
    public async Task A_perpetual_quotes_sizes_are_contracts_and_become_base_currency()
    {
        // 50 contracts on the bid and 25 on the ask, at 0.01 BTC each: half a bitcoin and a quarter. Read as base
        // currency the quote would say fifty bitcoin were bid, which is a hundred times the truth and would pass
        // every sanity check a caller could apply.
        await using Rig rig = new(OkxInstrumentType.Swap);
        await rig.ConnectAsync();
        await rig.Client.SubscribeAsync(Commands.Quotes(_perpetual), CancellationToken.None);

        await rig.Public.SendTextAsync(OkxPayloads.SwapTicker);
        QuoteTick quote = Assert.IsType<QuoteTick>(await rig.Sink.NextDataAsync());

        Assert.Equal(50m * Contract, quote.BidSize.Value);
        Assert.Equal(25m * Contract, quote.AskSize.Value);
    }

    [Fact]
    public async Task A_spot_trade_carries_the_takers_side()
    {
        await using Rig rig = new();
        await rig.ConnectAsync();
        await rig.Client.SubscribeAsync(Commands.Trades(_pair), CancellationToken.None);

        await rig.Public.SendTextAsync(OkxPayloads.SpotTrade);
        TradeTick trade = Assert.IsType<TradeTick>(await rig.Sink.NextDataAsync());

        Assert.Equal(83772.6m, trade.Price.Value);
        Assert.Equal(0.0000178m, trade.Size.Value);
        Assert.Equal(AggressorSide.Buyer, trade.Aggressor);
        Assert.Equal("1062992715", trade.TradeId.Value);
    }

    [Fact]
    public async Task A_perpetual_trades_size_is_contracts_and_becomes_base_currency()
    {
        await using Rig rig = new(OkxInstrumentType.Swap);
        await rig.ConnectAsync();
        await rig.Client.SubscribeAsync(Commands.Trades(_perpetual), CancellationToken.None);

        await rig.Public.SendTextAsync(OkxPayloads.SwapTrade);
        TradeTick trade = Assert.IsType<TradeTick>(await rig.Sink.NextDataAsync());

        Assert.Equal(3m, trade.Size.Value);
        Assert.Equal(AggressorSide.Seller, trade.Aggressor);
    }

    // ----- the book -----

    [Fact]
    public async Task A_shallow_book_subscription_asks_for_the_channel_that_serves_five_levels()
    {
        await using Rig rig = new();
        await rig.ConnectAsync();

        await rig.Client.SubscribeAsync(Commands.Book(_pair, depth: 5), CancellationToken.None);

        Assert.Equal(("books5", "BTC-USDT"), Subscription(await rig.Public.ReceiveTextAsync()));
    }

    [Fact]
    public async Task A_book_subscription_with_no_depth_asks_for_the_deep_channel()
    {
        await using Rig rig = new();
        await rig.ConnectAsync();

        await rig.Client.SubscribeAsync(Commands.Book(_pair), CancellationToken.None);

        Assert.Equal(("books", "BTC-USDT"), Subscription(await rig.Public.ReceiveTextAsync()));
    }

    [Fact]
    public async Task The_five_level_push_is_a_snapshot_every_time_because_the_venue_sends_the_whole_book()
    {
        await using Rig rig = new();
        await rig.ConnectAsync();
        await rig.Client.SubscribeAsync(Commands.Book(_pair, depth: 5), CancellationToken.None);

        await rig.Public.SendTextAsync(OkxPayloads.SpotBooks5);
        OrderBookDeltas deltas = Assert.IsType<OrderBookDeltas>(await rig.Sink.NextDataAsync());

        Assert.True(deltas.IsSnapshot);
        Assert.Equal(10, deltas.Deltas.Count);
        Assert.Equal(81_625_379_489UL, deltas.Sequence);
        Assert.All(deltas.Deltas, d => Assert.Equal(BookAction.Add, d.Action));
    }

    [Fact]
    public async Task A_change_to_the_deep_book_is_an_update_and_a_level_at_zero_is_a_deletion()
    {
        // The venue says which a message is, so nothing here has to guess. A level at size zero has gone, and
        // reading it as an update would leave a price in the book at no size at all.
        await using Rig rig = new();
        await rig.ConnectAsync();
        await rig.Client.SubscribeAsync(Commands.Book(_pair), CancellationToken.None);

        await rig.Public.SendTextAsync(OkxPayloads.SpotBooksSnapshot);
        OrderBookDeltas snapshot = Assert.IsType<OrderBookDeltas>(await rig.Sink.NextDataAsync());
        Assert.True(snapshot.IsSnapshot);

        await rig.Public.SendTextAsync(OkxPayloads.SpotBooksUpdate);
        OrderBookDeltas update = Assert.IsType<OrderBookDeltas>(await rig.Sink.NextDataAsync());

        Assert.False(update.IsSnapshot);
        Assert.Contains(update.Deltas, d => d.Action == BookAction.Delete && d.Order.Price.Value == 83802m);
        Assert.Contains(update.Deltas, d => d.Action == BookAction.Update && d.Order.Size.Value == 1.25m);
    }

    // ----- candles -----

    [Fact]
    public async Task A_bar_is_published_once_the_venue_says_the_candle_has_closed()
    {
        // The venue states this itself in the row's last field, which no other venue here does - so this client
        // needs no grace period and no timer to decide a bar is finished, and a bar is published exactly once.
        await using Rig rig = new();
        await rig.ConnectAsync();
        await rig.Client.SubscribeAsync(Commands.Bars(_pairBars), CancellationToken.None);

        await rig.Business.SendTextAsync(OkxPayloads.ClosedCandle);
        Bar bar = Assert.IsType<Bar>(await rig.Sink.NextDataAsync());

        // Close-stamped: the row opens at 1699999920000 and the bar is named by when the minute ended.
        Assert.Equal(1_699_999_980_000L, bar.TsEvent.ToMilliseconds());
        Assert.Equal(103m, bar.Open.Value);
        Assert.Equal(108m, bar.High.Value);
        Assert.Equal(102m, bar.Low.Value);
        Assert.Equal(104m, bar.Close.Value);
        Assert.Equal(3m, bar.Volume.Value);
        Assert.False(bar.IsRevision);
    }

    [Fact]
    public async Task The_forming_candle_is_dropped_unless_revisions_were_asked_for()
    {
        await using Rig rig = new();
        await rig.ConnectAsync();
        await rig.Client.SubscribeAsync(Commands.Bars(_pairBars), CancellationToken.None);

        await rig.Business.SendTextAsync(OkxPayloads.FormingCandle);
        await rig.Business.SendTextAsync(OkxPayloads.ClosedCandle);

        // The closed one, and only the closed one: publishing an unfinished minute as a bar says a candle closed
        // when it has not.
        Bar bar = Assert.IsType<Bar>(await rig.Sink.NextDataAsync());
        Assert.Equal(1_699_999_980_000L, bar.TsEvent.ToMilliseconds());
    }

    [Fact]
    public async Task The_forming_candle_is_forwarded_as_a_revision_when_revisions_were_asked_for()
    {
        await using Rig rig = new(handleRevisedBars: true);
        await rig.ConnectAsync();
        await rig.Client.SubscribeAsync(Commands.Bars(_pairBars), CancellationToken.None);

        await rig.Business.SendTextAsync(OkxPayloads.FormingCandle);
        Bar revision = Assert.IsType<Bar>(await rig.Sink.NextDataAsync());

        Assert.True(revision.IsRevision);
        Assert.Equal(1_700_000_040_000L, revision.TsEvent.ToMilliseconds());
    }

    [Fact]
    public async Task A_perpetual_bars_volume_is_base_currency_and_not_the_contract_count()
    {
        await using Rig rig = new(OkxInstrumentType.Swap);
        await rig.ConnectAsync();
        await rig.Client.SubscribeAsync(Commands.Bars(_perpetualBars), CancellationToken.None);

        await rig.Business.SendTextAsync(OkxPayloads.ClosedSwapCandle);
        Bar bar = Assert.IsType<Bar>(await rig.Sink.NextDataAsync());

        Assert.Equal(300m * Contract, bar.Volume.Value);
    }

    // ----- derivative-only data -----

    [Fact]
    public async Task A_funding_rate_carries_the_next_settlement_time_this_venue_publishes()
    {
        // Unlike the other venues here, whose funding channels carry a rate and nothing else, so this is the one
        // place a next-settlement time comes from a stream rather than being left null.
        await using Rig rig = new(OkxInstrumentType.Swap);
        await rig.ConnectAsync();
        await rig.Client.SubscribeAsync(new SubscribeFundingRates(_perpetual, null, Guid.NewGuid(), TestKernel.Now), CancellationToken.None);

        Assert.Equal(("funding-rate", "BTC-USDT-SWAP"), Subscription(await rig.Public.ReceiveTextAsync()));

        await rig.Public.SendTextAsync(OkxPayloads.FundingRateTick);
        FundingRateUpdate funding = Assert.IsType<FundingRateUpdate>(await rig.Sink.NextDataAsync());

        Assert.Equal(0.0000263463215391m, funding.Rate);
        Assert.Equal(1_790_380_800_000L, funding.NextFundingTime!.Value.ToMilliseconds());
    }

    [Fact]
    public async Task A_spot_client_refuses_a_funding_subscription_rather_than_sending_one()
    {
        // Spot pays no funding, so there is nothing to subscribe to. Refused with a reason, because a subscription
        // that is silently dropped is a caller waiting for data that is never coming.
        await using Rig rig = new();
        await rig.ConnectAsync();

        SubscribeFundingRates command = new(_pair, null, Guid.NewGuid(), TestKernel.Now);
        await rig.Client.SubscribeAsync(command, CancellationToken.None);

        (SubscribeCommand failed, string reason) = Assert.Single(rig.Sink.SubscriptionFailures);
        Assert.Same(command, failed);
        Assert.Contains("SPOT", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_mark_price_comes_from_the_channel_that_publishes_it()
    {
        await using Rig rig = new(OkxInstrumentType.Swap);
        await rig.ConnectAsync();
        await rig.Client.SubscribeAsync(Commands.Mark(_perpetual), CancellationToken.None);

        Assert.Equal(("mark-price", "BTC-USDT-SWAP"), Subscription(await rig.Public.ReceiveTextAsync()));

        await rig.Public.SendTextAsync(OkxPayloads.MarkPriceTick);
        MarkPriceUpdate mark = Assert.IsType<MarkPriceUpdate>(await rig.Sink.NextDataAsync());

        Assert.Equal(83679.8m, mark.Value.Value);
    }

    [Fact]
    public async Task An_index_subscription_names_the_index_rather_than_the_contract()
    {
        // The venue publishes an index under its own name - BTC-USDT for BTC-USDT-SWAP - and that name comes off the
        // instrument, where the provider recorded the venue's own field. Taking it out of the contract id would be
        // reading a spelling, and it would be wrong for the dated contracts: BTC-USD_UM-261030's index is BTC-USD.
        await using Rig rig = new(OkxInstrumentType.Swap);
        await rig.ConnectAsync();

        await rig.Client.SubscribeAsync(new SubscribeIndexPrices(_perpetual, null, Guid.NewGuid(), TestKernel.Now), CancellationToken.None);

        Assert.Equal(("index-tickers", "BTC-USDT"), Subscription(await rig.Public.ReceiveTextAsync()));

        await rig.Public.SendTextAsync(OkxPayloads.IndexTick);
        IndexPriceUpdate index = Assert.IsType<IndexPriceUpdate>(await rig.Sink.NextDataAsync());

        // The message arrives under the index's name and is attributed to the contract priced against it.
        Assert.Equal(_perpetual, index.InstrumentId);
        Assert.Equal(83700.1m, index.Value.Value);
    }

    [Fact]
    public async Task An_index_subscription_for_an_instrument_nobody_has_loaded_says_so()
    {
        // Without the instrument there is nothing to read the index's name off, and guessing one would subscribe to
        // a channel that either does not exist or belongs to something else.
        await using Rig rig = new(OkxInstrumentType.Swap);
        await rig.ConnectAsync();

        SubscribeIndexPrices command = new(InstrumentId.Parse("NOTACOIN-USDT-SWAP.OKX"), null, Guid.NewGuid(), TestKernel.Now);
        await rig.Client.SubscribeAsync(command, CancellationToken.None);

        (SubscribeCommand failed, string reason) = Assert.Single(rig.Sink.SubscriptionFailures);
        Assert.Same(command, failed);
        Assert.Contains("not loaded", reason, StringComparison.Ordinal);
    }

    // ----- unsubscribing -----

    [Fact]
    public async Task Unsubscribing_from_a_book_drops_both_channels_that_could_have_served_it()
    {
        // A subscription picks one of two channels by the depth asked for, and an unsubscribe carries no depth. So
        // both are dropped; dropping one the client never held is a no-op.
        await using Rig rig = new();
        await rig.ConnectAsync();

        await rig.Client.SubscribeAsync(Commands.Book(_pair, depth: 5), CancellationToken.None);
        Assert.Equal(("books5", "BTC-USDT"), Subscription(await rig.Public.ReceiveTextAsync()));

        await rig.Client.UnsubscribeAsync(new UnsubscribeOrderBookDeltas(_pair, null, Guid.NewGuid(), TestKernel.Now), CancellationToken.None);

        string sent = await rig.Public.ReceiveTextAsync();
        Assert.Equal("unsubscribe", Op(sent));
        Assert.Equal(("books5", "BTC-USDT"), Subscription(sent));
    }

    [Fact]
    public async Task Unsubscribing_from_bars_goes_to_the_business_socket()
    {
        await using Rig rig = new();
        await rig.ConnectAsync();

        await rig.Client.SubscribeAsync(Commands.Bars(_pairBars), CancellationToken.None);
        Assert.Equal("subscribe", Op(await rig.Business.ReceiveTextAsync()));

        await rig.Client.UnsubscribeAsync(new UnsubscribeBars(_pairBars, null, Guid.NewGuid(), TestKernel.Now), CancellationToken.None);

        string sent = await rig.Business.ReceiveTextAsync();
        Assert.Equal("unsubscribe", Op(sent));
        Assert.Equal(("candle1m", "BTC-USDT"), Subscription(sent));
    }

    // ----- errors and instruments -----

    [Fact]
    public async Task A_subscription_the_venue_refuses_is_logged_rather_than_parsed_as_data()
    {
        // The exact message that proved the candle channels live on the business socket. An error event carries no
        // data and no arg, so a handler that reached for them would throw on every refusal.
        await using Rig rig = new();
        await rig.ConnectAsync();

        await rig.Public.SendTextAsync(OkxPayloads.CandleOnTheWrongSocket);
        await rig.Public.SendTextAsync(OkxPayloads.SubscribeAck);

        // Still taking messages afterwards.
        await rig.Public.SendTextAsync(OkxPayloads.SpotTicker);
        Assert.IsType<QuoteTick>(await rig.Sink.NextDataAsync());
    }

    [Fact]
    public async Task Connecting_publishes_the_catalog_it_loaded()
    {
        await using Rig rig = new();
        await rig.ConnectAsync();

        Assert.Equal(2, rig.Sink.Instruments.Count);
        Assert.Contains(rig.Sink.Instruments, i => i.Id == _pair);
    }

    // ----- history over REST -----

    [Fact]
    public async Task A_bar_request_is_answered_out_of_the_shared_history_helper()
    {
        // The same code a catalog download calls, so stored bars and a node's bars cannot disagree about what an
        // interval looked like.
        await using Rig rig = new();
        await rig.ConnectAsync();

        await rig.Client.RequestAsync(Commands.RequestBars(_pairBars, null, null, null), CancellationToken.None);
        DataResponse response = await rig.Sink.NextResponseAsync();

        Assert.Null(response.Error);
        Assert.Equal(typeof(Bar), response.DataType);
        Assert.Equal(3, response.Data.Count);
    }

    [Fact]
    public async Task A_funding_request_is_refused_on_spot_and_answered_on_a_perpetual()
    {
        await using Rig spot = new();
        await spot.ConnectAsync();
        await spot.Client.RequestAsync(Commands.RequestFundingRates(_pair, null, null, null), CancellationToken.None);
        DataResponse refused = await spot.Sink.NextResponseAsync();
        Assert.NotNull(refused.Error);

        await using Rig swap = new(OkxInstrumentType.Swap);
        await swap.ConnectAsync();
        await swap.Client.RequestAsync(Commands.RequestFundingRates(_perpetual, null, null, null), CancellationToken.None);
        DataResponse answered = await swap.Sink.NextResponseAsync();

        Assert.Null(answered.Error);
        Assert.Equal(3, answered.Data.Count);
    }

    [Fact]
    public async Task A_trade_request_asks_for_no_more_than_the_page_the_venue_serves()
    {
        // Asked for 1000 the venue serves 500, so asking for more is a request that quietly answers fewer rows than
        // it claimed to want.
        await using Rig rig = new();
        await rig.ConnectAsync();

        await rig.Client.RequestAsync(Commands.RequestTrades(_pair, limit: 5000), CancellationToken.None);
        await rig.Sink.NextResponseAsync();

        Assert.Equal("500", rig.Server.RequestsTo(TradesPath)[0].Query("limit"));
    }

    [Fact]
    public async Task A_request_this_client_cannot_answer_says_so_rather_than_answering_nothing()
    {
        await using Rig rig = new();
        await rig.ConnectAsync();

        await rig.Client.RequestAsync(new RequestQuoteTicks(_pair, null, null, null, null, Guid.NewGuid(), TestKernel.Now), CancellationToken.None);
        DataResponse response = await rig.Sink.NextResponseAsync();

        Assert.NotNull(response.Error);
        Assert.Contains("RequestQuoteTicks", response.Error!, StringComparison.Ordinal);
    }
}
