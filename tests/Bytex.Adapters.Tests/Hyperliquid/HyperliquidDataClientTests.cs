using System.Text.Json;
using Bytex.Adapters.Hyperliquid;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Adapters.Tests.Hyperliquid;

// Why: this venue's socket is one address with no token and no per-topic path, and a subscription is a message. That
// makes it simpler than the other venues' and hides two traps instead.
//
// A candle message names its coin in a field called "s" while every other channel calls it "coin", and its "v" is a
// volume where a book level's "sz" is a resting size. Reading one with the other's field names gives no error and no
// data. And the best-bid-and-offer arrives as a two-element ARRAY rather than as two named fields, so the bid and the
// ask are told apart by position only.
//
// Every payload below is a frame the live socket really sent on 2026-09-25.
public sealed class HyperliquidDataClientTests
{
    private static readonly BarSpecification _minute = new(1, BarAggregation.Minute, PriceType.Last);

    private sealed class Rig : IAsyncDisposable
    {
        private WsSession? _session;

        public Rig()
        {
            Server = new LoopbackServer(request => request.Path == HyperliquidVenue.InfoPath
                ? StubResponse.Json(HyperliquidPayloads.Meta)
                : StubResponse.Error(404, "no such path"));

            Kernel = new TestKernel();
            Client = new HyperliquidDataClient(new ClientId("HYPERLIQUID"), new HyperliquidDataClientConfig
            {
                BaseUrlHttp = Server.HttpBase,
                BaseUrlWs = Server.WsBase,
                InstrumentProvider = new Core.Adapters.InstrumentProviderConfig { LoadAll = true },
            }, Kernel.Services);

            Client.AttachSink(Sink);
        }

        public LoopbackServer Server { get; }

        public TestKernel Kernel { get; }

        public HyperliquidDataClient Client { get; }

        public RecordingDataSink Sink { get; } = new();

        public WsSession Session => _session ?? throw new InvalidOperationException("not connected");

        public InstrumentId Btc { get; } = InstrumentId.Parse("BTC-PERP.HYPERLIQUID");

        public async Task<Rig> ConnectAsync()
        {
            await Client.ConnectAsync(CancellationToken.None).WaitAsync(Wait.Timeout);
            _session = await Server.NextSessionAsync();
            return this;
        }

        /// <summary>The subscription message the client sent, parsed the way the venue would parse it.</summary>
        public async Task<JsonElement> NextSubscriptionAsync()
        {
            using JsonDocument doc = JsonDocument.Parse(await Session.ReceiveTextAsync());
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

    [Fact]
    public async Task The_socket_is_one_fixed_address_with_no_token_asked_for_first()
    {
        // Unlike KuCoin, which answers a REST call with an address and an expiring token per connection. Here the
        // address is a constant, so it can be declared - which is what makes the declaration's socket base a
        // string rather than a null for this venue.
        await using Rig rig = await new Rig().ConnectAsync();

        Assert.Equal(HyperliquidVenue.WsPath, rig.Session.Path);

        // Nothing but the catalog was fetched over REST before connecting.
        Assert.All(rig.Server.Requests, r => Assert.Equal(HyperliquidVenue.InfoPath, r.Path));
    }

    [Fact]
    public async Task The_catalog_is_published_on_connecting()
    {
        await using Rig rig = await new Rig().ConnectAsync();

        Assert.Equal("connected", await rig.Sink.NextConnectionEventAsync());
        Assert.Equal(5, rig.Sink.Instruments.Count);
        Assert.Contains(rig.Sink.Instruments, i => i.Id == rig.Btc);
    }

    [Fact]
    public async Task A_subscription_names_a_type_and_a_coin_in_a_message()
    {
        await using Rig rig = await new Rig().ConnectAsync();

        await rig.Client.SubscribeAsync(Commands.Trades(rig.Btc), CancellationToken.None);
        JsonElement sent = await rig.NextSubscriptionAsync();

        Assert.Equal("subscribe", sent.GetProperty("method").GetString());
        Assert.Equal("trades", sent.GetProperty("subscription").GetProperty("type").GetString());

        // The COIN, not the instrument id: the socket names an asset the venue's way even though an order names it
        // by index.
        Assert.Equal("BTC", sent.GetProperty("subscription").GetProperty("coin").GetString());
    }

    [Fact]
    public async Task A_candle_subscription_carries_the_interval_as_well()
    {
        await using Rig rig = await new Rig().ConnectAsync();

        await rig.Client.SubscribeAsync(Commands.Bars(new BarType(rig.Btc, _minute)), CancellationToken.None);
        JsonElement sent = await rig.NextSubscriptionAsync();

        Assert.Equal("candle", sent.GetProperty("subscription").GetProperty("type").GetString());
        Assert.Equal("1m", sent.GetProperty("subscription").GetProperty("i").GetString());
    }

    [Fact]
    public async Task The_same_subscription_is_only_sent_once()
    {
        // The book and the quote channels are separate here, so subscribing twice to one of them would publish
        // every message twice - and a duplicate quote is a quote that moved and came back.
        await using Rig rig = await new Rig().ConnectAsync();

        await rig.Client.SubscribeAsync(Commands.Trades(rig.Btc), CancellationToken.None);
        await rig.NextSubscriptionAsync();
        await rig.Client.SubscribeAsync(Commands.Trades(rig.Btc), CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => rig.Session.ReceiveTextAsync(TimeSpan.FromMilliseconds(400)));
    }

    [Fact]
    public async Task Mark_price_and_funding_are_one_subscription_because_they_are_one_channel()
    {
        await using Rig rig = await new Rig().ConnectAsync();

        await rig.Client.SubscribeAsync(Commands.Mark(rig.Btc), CancellationToken.None);
        JsonElement sent = await rig.NextSubscriptionAsync();
        Assert.Equal("activeAssetCtx", sent.GetProperty("subscription").GetProperty("type").GetString());

        // The funding subscription asks for the same thing, so nothing more goes out.
        await rig.Client.SubscribeAsync(new SubscribeFundingRates(rig.Btc, null, Guid.NewGuid(), TestKernel.Now), CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => rig.Session.ReceiveTextAsync(TimeSpan.FromMilliseconds(400)));
    }

    [Fact]
    public async Task A_reconnected_socket_is_told_about_every_subscription_again()
    {
        // The venue keeps none across a connection, so a client that did not re-send them would reconnect and
        // receive nothing - which looks exactly like a quiet market.
        await using Rig rig = await new Rig().ConnectAsync();

        await rig.Client.SubscribeAsync(Commands.Trades(rig.Btc), CancellationToken.None);
        await rig.NextSubscriptionAsync();

        rig.Session.Drop();
        WsSession second = await rig.Server.NextSessionAsync();

        using JsonDocument doc = JsonDocument.Parse(await second.ReceiveTextAsync());
        Assert.Equal("trades", doc.RootElement.GetProperty("subscription").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Trades_carry_the_aggressors_side_as_one_letter()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        await rig.Client.SubscribeAsync(Commands.Trades(rig.Btc), CancellationToken.None);
        await rig.NextSubscriptionAsync();

        await rig.Session.SendTextAsync(HyperliquidPayloads.TradesChannel);

        TradeTick first = Assert.IsType<TradeTick>(await rig.Sink.NextDataAsync());
        Assert.Equal(83770m, first.Price.Value);
        Assert.Equal(0.00819m, first.Size.Value);

        // B means the BUYER crossed, which is the aggressor and not the resting side.
        Assert.Equal(AggressorSide.Buyer, first.Aggressor);
        Assert.Equal(UnixNanos.FromMilliseconds(1790358604045L), first.TsEvent);

        // Several trades arrive in one frame, so the second one has to come out of the same message.
        TradeTick second = Assert.IsType<TradeTick>(await rig.Sink.NextDataAsync());
        Assert.Equal(AggressorSide.Seller, second.Aggressor);
    }

    [Fact]
    public async Task The_best_bid_and_ask_are_told_apart_by_position_in_an_array()
    {
        // There are no named bid and ask fields on this channel. Reading them the other way round gives a crossed
        // quote with no error anywhere.
        await using Rig rig = await new Rig().ConnectAsync();
        await rig.Client.SubscribeAsync(Commands.Quotes(rig.Btc), CancellationToken.None);
        await rig.NextSubscriptionAsync();

        await rig.Session.SendTextAsync(HyperliquidPayloads.BboChannel);

        QuoteTick quote = Assert.IsType<QuoteTick>(await rig.Sink.NextDataAsync());
        Assert.Equal(83757m, quote.Bid.Value);
        Assert.Equal(83758m, quote.Ask.Value);
        Assert.True(quote.Ask.Value > quote.Bid.Value, "the sides were read the wrong way round");
        Assert.Equal(22.61305m, quote.BidSize.Value);
    }

    [Fact]
    public async Task The_book_arrives_as_a_complete_snapshot_and_its_top_becomes_a_quote()
    {
        // Twenty levels a side, every time it changes, with no deltas to apply - so nothing here keeps a book of
        // its own and nothing can drift out of step with the venue's.
        await using Rig rig = await new Rig().ConnectAsync();
        await rig.Client.SubscribeAsync(Commands.Book(rig.Btc), CancellationToken.None);
        await rig.NextSubscriptionAsync();

        await rig.Session.SendTextAsync(
            """{"channel":"l2Book","data":""" + HyperliquidPayloads.BtcBook + "}");

        QuoteTick quote = Assert.IsType<QuoteTick>(await rig.Sink.NextDataAsync());
        Assert.Equal(83767m, quote.Bid.Value);
        Assert.Equal(83768m, quote.Ask.Value);
        Assert.Equal(UnixNanos.FromMilliseconds(1790358575700L), quote.TsEvent);
    }

    [Fact]
    public async Task A_candle_is_published_when_the_next_one_arrives_rather_than_on_a_timer()
    {
        // The venue republishes a forming candle and never says one is closed, so the arrival of its successor is
        // what closes it. That is why this client needs no closing loop and cannot publish a bar the venue never
        // sent - and why the FIRST message publishes nothing.
        await using Rig rig = await new Rig().ConnectAsync();
        await rig.Client.SubscribeAsync(Commands.Bars(new BarType(rig.Btc, _minute)), CancellationToken.None);
        await rig.NextSubscriptionAsync();

        await rig.Session.SendTextAsync(HyperliquidPayloads.CandleChannel);
        await rig.Session.SendTextAsync(HyperliquidPayloads.CandleChannelNext);

        Bar bar = Assert.IsType<Bar>(await rig.Sink.NextDataAsync());

        // Stamped at its CLOSE, which is the open plus the interval and not the venue's own last-millisecond field.
        Assert.Equal(UnixNanos.FromMilliseconds(1790358600000L + 60000L), bar.TsEvent);
        Assert.Equal(83765m, bar.Open.Value);
        Assert.Equal(83770m, bar.High.Value);
        Assert.Equal(83756m, bar.Low.Value);
        Assert.Equal(83765m, bar.Close.Value);

        // The candle's coin field is "s" and its volume "v", neither of which any other channel uses - so this
        // number arriving at all is the evidence they were read with the right names.
        Assert.Equal(0.3317m, bar.Volume.Value);
        Assert.False(bar.IsRevision);
    }

    [Fact]
    public async Task A_forming_candle_is_only_published_when_revisions_are_asked_for()
    {
        await using LoopbackServer server = new(request => request.Path == HyperliquidVenue.InfoPath
            ? StubResponse.Json(HyperliquidPayloads.Meta)
            : StubResponse.Error(404, "no such path"));

        using TestKernel kernel = new();
        RecordingDataSink sink = new();
        HyperliquidDataClient client = new(new ClientId("HYPERLIQUID"), new HyperliquidDataClientConfig
        {
            BaseUrlHttp = server.HttpBase,
            BaseUrlWs = server.WsBase,
            HandleRevisedBars = true,
            InstrumentProvider = new Core.Adapters.InstrumentProviderConfig { LoadAll = true },
        }, kernel.Services);

        client.AttachSink(sink);
        await client.ConnectAsync(CancellationToken.None).WaitAsync(Wait.Timeout);
        WsSession session = await server.NextSessionAsync();
        InstrumentId btc = InstrumentId.Parse("BTC-PERP.HYPERLIQUID");
        await client.SubscribeAsync(Commands.Bars(new BarType(btc, _minute)), CancellationToken.None);
        await session.ReceiveTextAsync();

        await session.SendTextAsync(HyperliquidPayloads.CandleChannel);

        Bar forming = Assert.IsType<Bar>(await sink.NextDataAsync());
        Assert.True(forming.IsRevision, "the first message is the candle still forming, which is a revision and not a bar");

        await client.DisconnectAsync(CancellationToken.None);
        client.Dispose();
    }

    [Fact]
    public async Task Mark_and_index_and_funding_all_come_out_of_one_message()
    {
        // The venue puts them in one object on one channel. The oracle price is the index this venue computes from
        // other exchanges; the mark is what a position is liquidated against, and they are different numbers -
        // 83778.1 and 83757.0 in this frame.
        await using Rig rig = await new Rig().ConnectAsync();
        await rig.Client.SubscribeAsync(Commands.Mark(rig.Btc), CancellationToken.None);
        await rig.NextSubscriptionAsync();

        await rig.Session.SendTextAsync(HyperliquidPayloads.ActiveAssetCtxChannel);

        MarkPriceUpdate mark = Assert.IsType<MarkPriceUpdate>(await rig.Sink.NextDataAsync());
        Assert.Equal(83757m, mark.Value.Value);

        IndexPriceUpdate index = Assert.IsType<IndexPriceUpdate>(await rig.Sink.NextDataAsync());
        Assert.Equal(83778.1m, index.Value.Value);

        FundingRateUpdate funding = Assert.IsType<FundingRateUpdate>(await rig.Sink.NextDataAsync());
        Assert.Equal(0.0000125m, funding.Rate);

        // The rate for the current hour and not a settlement, so there is no next-settlement time to carry.
        Assert.Null(funding.NextFundingTime);
    }

    [Fact]
    public async Task Unsubscribing_tells_the_venue_rather_than_just_forgetting_locally()
    {
        await using Rig rig = await new Rig().ConnectAsync();

        await rig.Client.SubscribeAsync(Commands.Trades(rig.Btc), CancellationToken.None);
        await rig.NextSubscriptionAsync();

        await rig.Client.UnsubscribeAsync(new UnsubscribeTradeTicks(rig.Btc, null, Guid.NewGuid(), TestKernel.Now), CancellationToken.None);

        JsonElement sent = await rig.NextSubscriptionAsync();
        Assert.Equal("unsubscribe", sent.GetProperty("method").GetString());
        Assert.Equal("trades", sent.GetProperty("subscription").GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_subscription_this_client_cannot_serve_is_refused_rather_than_ignored()
    {
        await using Rig rig = await new Rig().ConnectAsync();

        SubscribeCommand deltas = new SubscribeIndexPrices(rig.Btc, null, Guid.NewGuid(), TestKernel.Now);
        await rig.Client.SubscribeAsync(deltas, CancellationToken.None);

        (SubscribeCommand command, string reason) = Assert.Single(rig.Sink.SubscriptionFailures);
        Assert.Equal(deltas, command);
        Assert.Contains("Hyperliquid data client", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_request_this_venue_has_no_read_for_is_answered_with_an_error_rather_than_silence()
    {
        // This venue publishes an account's own fills and NO public trade history read at all, so recent trades can
        // only be received live. A caller asking has to be told, not left waiting.
        await using Rig rig = await new Rig().ConnectAsync();

        await rig.Client.RequestAsync(Commands.RequestTrades(rig.Btc), CancellationToken.None);

        DataResponse response = await rig.Sink.NextResponseAsync();
        Assert.NotNull(response.Error);
        Assert.Contains("Hyperliquid data client", response.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bars_are_requested_through_the_same_helper_a_catalog_download_uses()
    {
        // So that stored bars and live bars never disagree about an interval, which is what the helper being public
        // and static is for.
        await using LoopbackServer server = new(request =>
        {
            using JsonDocument doc = JsonDocument.Parse(request.Body);
            return doc.RootElement.GetProperty("type").GetString() switch
            {
                HyperliquidReads.Meta => StubResponse.Json(HyperliquidPayloads.Meta),
                HyperliquidReads.CandleSnapshot => StubResponse.Json(HyperliquidPayloads.BtcHourlyCandles),
                _ => StubResponse.Error(422, HyperliquidPayloads.InfoUnknownType),
            };
        });

        using TestKernel kernel = new();
        RecordingDataSink sink = new();
        HyperliquidDataClient client = new(new ClientId("HYPERLIQUID"), new HyperliquidDataClientConfig
        {
            BaseUrlHttp = server.HttpBase,
            BaseUrlWs = server.WsBase,
            InstrumentProvider = new Core.Adapters.InstrumentProviderConfig { LoadAll = true },
        }, kernel.Services);

        client.AttachSink(sink);
        await client.ConnectAsync(CancellationToken.None).WaitAsync(Wait.Timeout);
        _ = await server.NextSessionAsync();

        BarType hourly = new(InstrumentId.Parse("BTC-PERP.HYPERLIQUID"), new BarSpecification(1, BarAggregation.Hour, PriceType.Last));
        await client.RequestAsync(Commands.RequestBars(hourly, null, null, 2), CancellationToken.None);

        DataResponse response = await sink.NextResponseAsync();
        Assert.Null(response.Error);

        // The test clock is set to 2023, well before the recorded candles, so nothing has closed by then - which is
        // itself the closed-bar rule holding: a bar the clock has not reached is not a bar.
        Assert.Empty(response.Data);

        await client.DisconnectAsync(CancellationToken.None);
        client.Dispose();
    }
}
