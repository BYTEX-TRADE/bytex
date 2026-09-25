using Bytex.Adapters.Gate;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Adapters.Tests.Gate;

// Why: one venue, three markets, one message envelope - and inside it almost every field means something different
// per market. The best bid and ask arrive under the same letters with sizes in base currency on spot and in contracts
// on the derivatives; `v` on a candle is the QUOTE volume on spot and a number of CONTRACTS on the derivatives;
// `create_time_ms` is milliseconds on the sockets and SECONDS on one of the REST endpoints; a depth snapshot's event
// is "update" on spot and "all" on the derivatives; and only two of the three markets say when a candle has closed.
//
// Every one of those was recorded off the venue's own sockets, and every one of them produces a plausible wrong
// number rather than an error. So each has a test, per market.
public sealed class GateDataClientTests
{
    private static readonly BarSpecification _oneMinute = new(1, BarAggregation.Minute, PriceType.Last);

    private sealed record Rig(LoopbackServer Server, WsSession Session, RecordingDataSink Sink, TestKernel Kernel, Core.Adapters.IDataClient Client) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Client.DisconnectAsync(CancellationToken.None);
            Client.Dispose();
            Kernel.Dispose();
            await Server.DisposeAsync();
        }
    }

    private static async Task<Rig> ConnectAsync(GateProductType product)
    {
        Routes routes = product switch
        {
            GateProductType.Futures => new Routes().On("GET", "/api/v4/futures/usdt/contracts", GatePayloads.FuturesContracts),
            GateProductType.Delivery => new Routes().On("GET", "/api/v4/delivery/usdt/contracts", GatePayloads.DeliveryContracts),
            _ => new Routes().On("GET", "/api/v4/spot/currency_pairs", GatePayloads.CurrencyPairs),
        };

        LoopbackServer server = new(routes.Handle);
        TestKernel kernel = new();
        GateDataClientConfig config = new()
        {
            ProductType = product,
            BaseUrlHttp = server.HttpBase,
            BaseUrlWs = server.WsBase,
            InstrumentProvider = new Core.Adapters.InstrumentProviderConfig { LoadAll = true },
        };

        Core.Adapters.IDataClient client = new GateDataClientFactory().Create(new ClientId("GATE"), config, kernel.Services);
        RecordingDataSink sink = new();
        client.AttachSink(sink);
        await client.ConnectAsync(CancellationToken.None);
        WsSession session = await server.NextSessionAsync();
        return new Rig(server, session, sink, kernel, client);
    }

    private static Core.Adapters.IDataClient Client(Rig rig) => rig.Client;

    // ----- spot -----

    [Fact]
    public async Task A_spot_quote_carries_sizes_in_base_currency()
    {
        await using Rig rig = await ConnectAsync(GateProductType.Spot);
        await Client(rig).SubscribeAsync(Commands.Quotes(InstrumentId.Parse("BTC_USDT.GATE")), CancellationToken.None);

        string subscribe = await rig.Session.ReceiveTextAsync();
        Assert.Contains("spot.book_ticker", subscribe, StringComparison.Ordinal);
        Assert.Contains("BTC_USDT", subscribe, StringComparison.Ordinal);

        await rig.Session.SendTextAsync(GatePayloads.SpotBookTicker);
        QuoteTick quote = Assert.IsType<QuoteTick>(await rig.Sink.NextDataAsync());

        Assert.Equal(83790.9m, quote.Bid.Value);
        Assert.Equal(83791m, quote.Ask.Value);
        Assert.Equal(0.497321m, quote.BidSize.Value);
        Assert.Equal(0.055894m, quote.AskSize.Value);

        // The socket stamps in whole milliseconds - unlike this venue's REST endpoints, which are in seconds.
        Assert.Equal(UnixNanos.FromMilliseconds(1790359165710), quote.TsEvent);
    }

    [Fact]
    public async Task A_spot_trade_reads_its_time_out_of_a_string_of_milliseconds()
    {
        // `create_time_ms` is a STRING here - "1790359171119.653000" - and a NUMBER OF SECONDS on the derivative
        // markets' REST endpoint under the same name. Reading this one as seconds would place the trade in the year
        // 58,700.
        await using Rig rig = await ConnectAsync(GateProductType.Spot);
        await Client(rig).SubscribeAsync(Commands.Trades(InstrumentId.Parse("BTC_USDT.GATE")), CancellationToken.None);
        await rig.Session.ReceiveTextAsync();

        await rig.Session.SendTextAsync(GatePayloads.SpotTrade);
        TradeTick trade = Assert.IsType<TradeTick>(await rig.Sink.NextDataAsync());

        Assert.Equal(83790.9m, trade.Price.Value);
        Assert.Equal(0.002984m, trade.Size.Value);
        Assert.Equal(AggressorSide.Seller, trade.Aggressor);
        Assert.Equal("220643714", trade.TradeId.Value);
        // The fraction the venue publishes is kept: 1790359171119.653 ms is 653 microseconds past the
        // millisecond, and rounding it away would move every trade on this venue by up to a millisecond.
        Assert.Equal(new UnixNanos(1790359171119653000L), trade.TsEvent);
    }

    [Fact]
    public async Task A_spot_candle_takes_its_volume_from_the_field_whose_letter_says_otherwise()
    {
        // On this market `a` is the BASE volume and `v` the QUOTE volume, which is the reverse of what the letters
        // suggest and the reverse of the REST row's order. Taking `v` would make every bar read about eighty
        // thousand times as busy as it was on BTC_USDT, which is a number nothing would question.
        await using Rig rig = await ConnectAsync(GateProductType.Spot);
        BarType barType = new(InstrumentId.Parse("BTC_USDT.GATE"), _oneMinute, AggregationSource.External);
        await Client(rig).SubscribeAsync(Commands.Bars(barType), CancellationToken.None);

        string subscribe = await rig.Session.ReceiveTextAsync();
        Assert.Contains("spot.candlesticks", subscribe, StringComparison.Ordinal);

        // Two elements, an interval and a pair: the joined "1m_BTC_USDT" form the venue's messages come back under
        // is refused as a single payload element.
        Assert.Contains("\"1m\"", subscribe, StringComparison.Ordinal);
        Assert.Contains("\"BTC_USDT\"", subscribe, StringComparison.Ordinal);

        await rig.Session.SendTextAsync(GatePayloads.SpotCandle(1790349960, "83853.4", "83864.5", "83829.6", "83859", "4.23818400", windowClosed: true));
        Bar bar = Assert.IsType<Bar>(await rig.Sink.NextDataAsync());

        Assert.Equal(83853.4m, bar.Open.Value);
        Assert.Equal(83864.5m, bar.High.Value);
        Assert.Equal(83829.6m, bar.Low.Value);
        Assert.Equal(83859m, bar.Close.Value);
        Assert.Equal(4.238184m, bar.Volume.Value);

        // Close-stamped: the row's own stamp is the open, in seconds, and one interval is added.
        Assert.Equal(UnixNanos.FromSeconds(1790349960 + 60), bar.TsEvent);
    }

    [Fact]
    public async Task A_spot_candle_still_forming_produces_no_bar_until_it_closes()
    {
        await using Rig rig = await ConnectAsync(GateProductType.Spot);
        BarType barType = new(InstrumentId.Parse("BTC_USDT.GATE"), _oneMinute, AggregationSource.External);
        await Client(rig).SubscribeAsync(Commands.Bars(barType), CancellationToken.None);
        await rig.Session.ReceiveTextAsync();

        // Two updates of one interval, neither closed, then the same interval marked closed. Only one bar comes out.
        await rig.Session.SendTextAsync(GatePayloads.SpotCandle(1790349960, "83853.4", "83860", "83840", "83855", "1", windowClosed: false));
        await rig.Session.SendTextAsync(GatePayloads.SpotCandle(1790349960, "83853.4", "83864.5", "83829.6", "83859", "4.23818400", windowClosed: false));
        await rig.Session.SendTextAsync(GatePayloads.SpotCandle(1790349960, "83853.4", "83864.5", "83829.6", "83859", "4.23818400", windowClosed: true));

        Bar bar = Assert.IsType<Bar>(await rig.Sink.NextDataAsync());
        Assert.Equal(4.238184m, bar.Volume.Value);
        Assert.Equal(83859m, bar.Close.Value);
    }

    [Fact]
    public async Task A_spot_depth_snapshot_becomes_a_quote_off_its_best_levels()
    {
        await using Rig rig = await ConnectAsync(GateProductType.Spot);
        await Client(rig).SubscribeAsync(Commands.Book(InstrumentId.Parse("BTC_USDT.GATE")), CancellationToken.None);

        string subscribe = await rig.Session.ReceiveTextAsync();
        Assert.Contains("spot.order_book", subscribe, StringComparison.Ordinal);

        await rig.Session.SendTextAsync(GatePayloads.SpotOrderBook);
        QuoteTick quote = Assert.IsType<QuoteTick>(await rig.Sink.NextDataAsync());

        // Levels are [price, size] arrays here and objects on the derivative markets.
        Assert.Equal(84005.6m, quote.Bid.Value);
        Assert.Equal(84005.7m, quote.Ask.Value);
        Assert.Equal(0.035056m, quote.BidSize.Value);
        Assert.Equal(0.115252m, quote.AskSize.Value);
    }

    [Fact]
    public async Task A_spot_client_refuses_what_spot_does_not_have()
    {
        // Spot is not margined, so there is no mark price and no funding rate to subscribe to. Saying so is what
        // stops a node connecting, being told the subscription worked, and waiting for data that never comes.
        await using Rig rig = await ConnectAsync(GateProductType.Spot);
        await Client(rig).SubscribeAsync(Commands.Mark(InstrumentId.Parse("BTC_USDT.GATE")), CancellationToken.None);

        Assert.Single(rig.Sink.SubscriptionFailures);
        Assert.Contains("not supported", rig.Sink.SubscriptionFailures[0].Reason, StringComparison.Ordinal);
    }

    // ----- perpetual futures -----

    [Fact]
    public async Task A_derivative_quote_carries_sizes_converted_out_of_contracts()
    {
        // The recorded sizes are the integers 13855 and 11465, which are contracts of 0.0001 BTC - 1.3855 and 1.1465
        // BTC. Left as published the quote would claim ten thousand BTC on the bid.
        await using Rig rig = await ConnectAsync(GateProductType.Futures);
        await Client(rig).SubscribeAsync(Commands.Quotes(InstrumentId.Parse("BTC_USDT.GATE")), CancellationToken.None);

        string subscribe = await rig.Session.ReceiveTextAsync();
        Assert.Contains("futures.book_ticker", subscribe, StringComparison.Ordinal);

        await rig.Session.SendTextAsync(GatePayloads.FuturesBookTicker);
        QuoteTick quote = Assert.IsType<QuoteTick>(await rig.Sink.NextDataAsync());

        Assert.Equal(83746.8m, quote.Bid.Value);
        Assert.Equal(1.3855m, quote.BidSize.Value);
        Assert.Equal(1.1465m, quote.AskSize.Value);
    }

    [Fact]
    public async Task A_derivative_trades_side_is_the_sign_of_its_size()
    {
        // There is no side field on a derivative trade at all. The recorded row carries size -3, which is three
        // contracts sold; reading the magnitude without the sign would report every trade as a buy.
        await using Rig rig = await ConnectAsync(GateProductType.Futures);
        await Client(rig).SubscribeAsync(Commands.Trades(InstrumentId.Parse("BTC_USDT.GATE")), CancellationToken.None);
        await rig.Session.ReceiveTextAsync();

        await rig.Session.SendTextAsync(GatePayloads.FuturesTrade);
        TradeTick trade = Assert.IsType<TradeTick>(await rig.Sink.NextDataAsync());

        Assert.Equal(AggressorSide.Seller, trade.Aggressor);
        Assert.Equal(0.0003m, trade.Size.Value);
        Assert.Equal(83746.9m, trade.Price.Value);

        // Here `create_time_ms` really is milliseconds, where the REST row of the same name is seconds.
        Assert.Equal(UnixNanos.FromMilliseconds(1790359195732), trade.TsEvent);
    }

    [Fact]
    public async Task A_derivative_candles_volume_is_contracts_where_spot_puts_the_quote_volume()
    {
        // One letter, one venue, two markets, two meanings: `v` counts CONTRACTS here and quote units on spot, and
        // both are numbers that look plausible either way.
        await using Rig rig = await ConnectAsync(GateProductType.Futures);
        BarType barType = new(InstrumentId.Parse("BTC_USDT.GATE"), _oneMinute, AggregationSource.External);
        await Client(rig).SubscribeAsync(Commands.Bars(barType), CancellationToken.None);
        await rig.Session.ReceiveTextAsync();

        await rig.Session.SendTextAsync(GatePayloads.FuturesCandle(1790349960, "83807.3", "83823", "83788.1", "83817.9", 291170, windowClosed: true));
        Bar bar = Assert.IsType<Bar>(await rig.Sink.NextDataAsync());

        Assert.Equal(29.117m, bar.Volume.Value);
        Assert.Equal(83807.3m, bar.Open.Value);
        Assert.Equal(83817.9m, bar.Close.Value);
        Assert.Equal(UnixNanos.FromSeconds(1790349960 + 60), bar.TsEvent);
    }

    [Fact]
    public async Task A_derivative_depth_snapshot_arrives_under_a_different_event_name_than_spots()
    {
        // The event is "all" here and "update" on spot, for the same kind of message, and a level is an object with
        // p and s rather than a two-element array. A client that filtered on spot's word would silently drop every
        // book message on this market.
        await using Rig rig = await ConnectAsync(GateProductType.Futures);
        await Client(rig).SubscribeAsync(Commands.Book(InstrumentId.Parse("BTC_USDT.GATE")), CancellationToken.None);
        await rig.Session.ReceiveTextAsync();

        await rig.Session.SendTextAsync(GatePayloads.FuturesOrderBook);
        QuoteTick quote = Assert.IsType<QuoteTick>(await rig.Sink.NextDataAsync());

        Assert.Equal(83956.3m, quote.Bid.Value);
        Assert.Equal(83956.4m, quote.Ask.Value);
        Assert.Equal(1.307m, quote.BidSize.Value);
        Assert.Equal(4.5979m, quote.AskSize.Value);
    }

    [Fact]
    public async Task The_mark_price_the_index_and_the_funding_rate_arrive_on_one_channel()
    {
        await using Rig rig = await ConnectAsync(GateProductType.Futures);
        InstrumentId id = InstrumentId.Parse("BTC_USDT.GATE");
        await Client(rig).SubscribeAsync(Commands.Mark(id), CancellationToken.None);

        string subscribe = await rig.Session.ReceiveTextAsync();
        Assert.Contains("futures.tickers", subscribe, StringComparison.Ordinal);

        await rig.Session.SendTextAsync(GatePayloads.FuturesTicker);

        MarkPriceUpdate mark = Assert.IsType<MarkPriceUpdate>(await rig.Sink.NextDataAsync());
        Assert.Equal(83746.8m, mark.Value.Value);

        IndexPriceUpdate index = Assert.IsType<IndexPriceUpdate>(await rig.Sink.NextDataAsync());
        Assert.Equal(83786.4m, index.Value.Value);

        FundingRateUpdate funding = Assert.IsType<FundingRateUpdate>(await rig.Sink.NextDataAsync());
        Assert.Equal(0.000125m, funding.Rate);

        // The venue names the next settlement in seconds; carrying it lets a host say when the charge falls.
        Assert.Equal(UnixNanos.FromSeconds(1790380800), funding.NextFundingTime);
    }

    // ----- delivery -----

    [Fact]
    public async Task A_dated_contracts_ticker_publishes_no_funding_rate_of_nought()
    {
        // The measurement this exists for. The delivery socket sends the same ticker channel with the funding fields
        // as EMPTY STRINGS rather than absent, and an empty string parses to zero - so a rate of nought would be
        // published as though it had been measured, and a backtest would price a charge that is never made.
        await using Rig rig = await ConnectAsync(GateProductType.Delivery);
        InstrumentId id = InstrumentId.Parse("BTC_USDT_20261009.GATE");
        await Client(rig).SubscribeAsync(Commands.Mark(id), CancellationToken.None);
        await rig.Session.ReceiveTextAsync();

        await rig.Session.SendTextAsync(GatePayloads.DeliveryTicker);

        // A second message of a different kind follows the ticker, so what comes THIRD proves the point without a
        // sleep: if the empty funding string had been published as a rate of nought, it would sit between the index
        // price and this quote.
        await rig.Session.SendTextAsync(
            """
            {"time":1790360097,"time_ms":1790360097100,"channel":"futures.book_ticker","event":"update","result":{"t":1790360097090,"u":9,"s":"BTC_USDT_20261009","b":"84150.1","B":4,"a":"84160.2","A":6}}
            """);

        MarkPriceUpdate mark = Assert.IsType<MarkPriceUpdate>(await rig.Sink.NextDataAsync());
        Assert.Equal(84128.5m, mark.Value.Value);

        IndexPriceUpdate index = Assert.IsType<IndexPriceUpdate>(await rig.Sink.NextDataAsync());
        Assert.Equal(83999.8m, index.Value.Value);

        QuoteTick next = Assert.IsType<QuoteTick>(await rig.Sink.NextDataAsync());
        Assert.Equal(84150.1m, next.Bid.Value);
    }

    [Fact]
    public async Task A_dated_contract_has_no_funding_to_subscribe_to()
    {
        await using Rig rig = await ConnectAsync(GateProductType.Delivery);
        await Client(rig).SubscribeAsync(
            new SubscribeFundingRates(InstrumentId.Parse("BTC_USDT_20261009.GATE"), null, Guid.NewGuid(), TestKernel.Now),
            CancellationToken.None);

        Assert.Single(rig.Sink.SubscriptionFailures);
    }

    [Fact]
    public async Task A_delivery_candle_closes_on_the_arrival_of_the_next_one()
    {
        // The delivery socket sends no window-closed flag at all, where the perpetual one does - so an interval
        // here is closed by a later one arriving, which is the only signal every market gives. The recorded
        // message carries the previous interval alongside the forming one, so one bar comes out of two rows.
        await using Rig rig = await ConnectAsync(GateProductType.Delivery);
        BarType barType = new(InstrumentId.Parse("BTC_USDT_20261009.GATE"), _oneMinute, AggregationSource.External);
        await Client(rig).SubscribeAsync(Commands.Bars(barType), CancellationToken.None);
        await rig.Session.ReceiveTextAsync();

        await rig.Session.SendTextAsync(GatePayloads.DeliveryCandle(1790360040, "84112.5", "84181.7", "84100.8", "84181.7", 31));
        await rig.Session.SendTextAsync(GatePayloads.DeliveryCandle(1790360100, "84181.7", "84181.7", "84181.7", "84181.7", 0));

        Bar bar = Assert.IsType<Bar>(await rig.Sink.NextDataAsync());
        Assert.Equal(UnixNanos.FromSeconds(1790360040 + 60), bar.TsEvent);
        Assert.Equal(84112.5m, bar.Open.Value);
        Assert.Equal(84181.7m, bar.Close.Value);

        // 31 contracts of 0.0001 BTC.
        Assert.Equal(0.0031m, bar.Volume.Value);
    }

    // ----- REST requests over the data client -----

    [Fact]
    public async Task A_derivative_rest_trade_reads_its_time_as_fractional_seconds()
    {
        // The trap this venue sets twice under one field name. `create_time_ms` is 1790359375.258 here - a NUMBER
        // OF SECONDS - and 1790359195732 on the socket. Read as milliseconds this would be 1970-01-21.
        await using Rig rig = await ConnectAsync(GateProductType.Futures);
        rig.Server.Handler = new Routes()
            .On("GET", "/api/v4/futures/usdt/contracts", GatePayloads.FuturesContracts)
            .On("GET", "/api/v4/futures/usdt/trades", GatePayloads.FuturesTrades)
            .Handle;

        await Client(rig).RequestAsync(
            Commands.RequestTrades(InstrumentId.Parse("BTC_USDT.GATE")),
            CancellationToken.None);

        DataResponse response = await rig.Sink.NextResponseAsync();
        Assert.Null(response.Error);
        Assert.Equal(2, response.Data.Count);

        TradeTick oldest = Assert.IsType<TradeTick>(response.Data[0]);
        // 1790359375.111 SECONDS, not milliseconds - the field is called create_time_ms all the same.
        Assert.Equal(new UnixNanos(1790359375111000000L), oldest.TsEvent);
        Assert.Equal(AggressorSide.Seller, oldest.Aggressor);
        Assert.Equal(0.0002m, oldest.Size.Value);

        TradeTick newest = Assert.IsType<TradeTick>(response.Data[1]);
        Assert.Equal(AggressorSide.Buyer, newest.Aggressor);
        Assert.Equal(0.0009m, newest.Size.Value);
    }

    [Fact]
    public async Task A_spot_rest_trade_reads_its_time_as_fractional_milliseconds()
    {
        await using Rig rig = await ConnectAsync(GateProductType.Spot);
        rig.Server.Handler = new Routes()
            .On("GET", "/api/v4/spot/currency_pairs", GatePayloads.CurrencyPairs)
            .On("GET", "/api/v4/spot/trades", GatePayloads.SpotTrades)
            .Handle;

        await Client(rig).RequestAsync(
            Commands.RequestTrades(InstrumentId.Parse("BTC_USDT.GATE")),
            CancellationToken.None);

        DataResponse response = await rig.Sink.NextResponseAsync();
        Assert.Equal(2, response.Data.Count);

        TradeTick oldest = Assert.IsType<TradeTick>(response.Data[0]);
        Assert.Equal(new UnixNanos(1790359364413017000L), oldest.TsEvent);
        Assert.Equal(0.000449m, oldest.Size.Value);
    }

    [Fact]
    public async Task Bar_history_over_the_data_client_is_the_same_helper_a_catalog_download_calls()
    {
        await using Rig rig = await ConnectAsync(GateProductType.Futures);
        rig.Server.Handler = new Routes()
            .On("GET", "/api/v4/futures/usdt/contracts", GatePayloads.FuturesContracts)
            .On("GET", "/api/v4/futures/usdt/candlesticks", GatePayloads.FuturesCandles)
            .Handle;

        // The recorded candles are from 2026 and the test clock starts in 2023, and a bar that has not closed by
        // "now" is not a bar - so the clock is moved to the moment the recording was taken.
        rig.Kernel.Clock.SetTime(UnixNanos.FromSeconds(1790350320));

        BarType barType = new(InstrumentId.Parse("BTC_USDT.GATE"), _oneMinute, AggregationSource.External);
        await Client(rig).RequestAsync(
            Commands.RequestBars(barType, UnixNanos.FromSeconds(1790349960), UnixNanos.FromSeconds(1790350260), null),
            CancellationToken.None);

        DataResponse response = await rig.Sink.NextResponseAsync();
        Assert.Null(response.Error);
        Assert.NotEmpty(response.Data);
        Assert.All(response.Data, d => Assert.IsType<Bar>(d));
    }

    [Fact]
    public async Task A_refused_subscription_is_logged_rather_than_read_as_data()
    {
        // The venue answers a bad subscription with an error object and status "fail" on the same channel it would
        // send data on. Reading that as data would be a crash; ignoring it silently would be a node that connects
        // and receives nothing. It is logged.
        RecordingLogs logs = new();
        LoopbackServer server = new(new Routes().On("GET", "/api/v4/futures/usdt/contracts", GatePayloads.FuturesContracts).Handle);
        using TestKernel kernel = new(logs);
        GateFuturesDataClient client = new(
            new ClientId("GATE"),
            new GateDataClientConfig { ProductType = GateProductType.Futures, BaseUrlHttp = server.HttpBase, BaseUrlWs = server.WsBase },
            kernel.Services);

        client.AttachSink(new RecordingDataSink());
        await client.ConnectAsync(CancellationToken.None);
        WsSession session = await server.NextSessionAsync();
        await session.SendTextAsync(GatePayloads.SubscribeRefused);

        // The socket's receive loop is another thread, so the warning is waited for, with a deadline, rather than
        // asserted at once.
        DateTime deadline = DateTime.UtcNow + Wait.Timeout;
        while (logs.Warnings.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.Contains(logs.Warnings, w => w.Contains("refused", StringComparison.OrdinalIgnoreCase));
        await client.DisconnectAsync(CancellationToken.None);
        client.Dispose();
        await server.DisposeAsync();
    }

    // ----- the two clients of one venue, compared with each other -----

    [Fact]
    public void Every_market_answers_the_same_commands()
    {
        // The parity table finds a venue's clients by naming convention and so sees only the spot ones. This is the
        // half that keeps the other two honest: a market that quietly stopped implementing something the others
        // implement would otherwise be invisible until somebody ran a node on it.
        string[] spot = Commands.Overridden(typeof(GateDataClient));
        string[] futures = Commands.Overridden(typeof(GateFuturesDataClient));

        Assert.Equal(spot, futures);

        // The delivery client is the perpetual one pointed at another address, so it overrides nothing of its own -
        // which is the statement that the two markets really do behave the same way apart from what is declared.
        Assert.Empty(Commands.Overridden(typeof(GateDeliveryDataClient)));
    }

    [Fact]
    public void A_client_handed_the_wrong_markets_configuration_refuses_to_be_built()
    {
        // The failure this closes: a client pointed at one market with another market's contracts loaded would
        // connect, subscribe successfully and receive nothing, because the venue has no such instrument there.
        using TestKernel kernel = new();

        Assert.Throws<ArgumentException>(() => new GateDataClient(
            new ClientId("GATE"),
            new GateDataClientConfig { ProductType = GateProductType.Futures },
            kernel.Services));

        Assert.Throws<ArgumentException>(() => new GateFuturesDataClient(
            new ClientId("GATE"),
            new GateDataClientConfig { ProductType = GateProductType.Delivery },
            kernel.Services));

        Assert.Throws<ArgumentException>(() => new GateDeliveryDataClient(
            new ClientId("GATE"),
            new GateDataClientConfig { ProductType = GateProductType.Futures },
            kernel.Services));
    }

    [Fact]
    public void The_factory_builds_the_client_the_configuration_names()
    {
        using TestKernel kernel = new();
        GateDataClientFactory factory = new();

        Assert.Equal("GATE", factory.Name);
        Assert.IsType<GateDataClient>(factory.Create(new ClientId("GATE"), new GateDataClientConfig(), kernel.Services));
        Assert.IsType<GateFuturesDataClient>(factory.Create(new ClientId("GATE"), new GateDataClientConfig { ProductType = GateProductType.Futures }, kernel.Services));
        Assert.IsType<GateDeliveryDataClient>(factory.Create(new ClientId("GATE"), new GateDataClientConfig { ProductType = GateProductType.Delivery }, kernel.Services));
    }
}
