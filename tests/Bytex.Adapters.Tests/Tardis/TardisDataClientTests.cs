using System.IO.Compression;
using System.Text;
using Bytex.Adapters.Tardis;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Adapters.Tests.Tardis;

// Why: Tardis files feed backtests. Column order (ask before bid in quotes!), microsecond timestamps, the
// per-day file layout and the exchange naming must all be right or the backtest silently runs on wrong data.
// Files follow the documented datasets CSV format and are served gzip-compressed by a stub on 127.0.0.1.
public sealed class TardisDataClientTests : IDisposable
{
    private const string TradesCsv =
        "exchange,symbol,timestamp,local_timestamp,id,side,price,amount\n" +
        "binance-futures,BTCUSDT,1585699200245000,1585699200355684,356714548,sell,6409.87,0.011\n" +
        "binance-futures,BTCUSDT,1585699200490000,1585699200597389,356714549,buy,6409.88,1.215\n" +
        "binance-futures,BTCUSDT,1585699201000000,1585699201100000,356714550,unknown,6410.00,0.500\n";

    private const string QuotesCsv =
        "exchange,symbol,timestamp,local_timestamp,ask_amount,ask_price,bid_price,bid_amount\n" +
        "binance-futures,BTCUSDT,1585699200245000,1585699200355684,2.5,6409.9,6409.8,1.25\n";

    private const string BookCsv =
        "exchange,symbol,timestamp,local_timestamp,is_snapshot,side,price,amount\n" +
        "binance-futures,BTCUSDT,1585699200245000,1585699200355684,true,bid,6409.8,1.25\n" +
        "binance-futures,BTCUSDT,1585699200245000,1585699200355684,true,ask,6409.9,2.5\n" +
        "binance-futures,BTCUSDT,1585699200345000,1585699200455684,false,bid,6409.8,0\n" +
        "binance-futures,BTCUSDT,1585699200345000,1585699200455684,false,ask,6409.9,3.75\n";

    private static readonly UnixNanos _dayStart = UnixNanos.FromDateTimeOffset(new DateTimeOffset(2020, 4, 1, 0, 0, 0, TimeSpan.Zero));
    private static readonly InstrumentId _perpId = InstrumentId.Parse("BTCUSDT-PERP.BINANCE");

    private readonly string _cacheDirectory = Path.Combine(Path.GetTempPath(), "bytex-tardis-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_cacheDirectory))
        {
            Directory.Delete(_cacheDirectory, recursive: true);
        }
    }

    private static byte[] Gzip(string text)
    {
        using MemoryStream buffer = new();
        using (GZipStream gzip = new(buffer, CompressionLevel.Fastest))
        {
            gzip.Write(Encoding.UTF8.GetBytes(text));
        }

        return buffer.ToArray();
    }

    private static CryptoPerpetual Perpetual() => new(new InstrumentSpec
    {
        Id = _perpId,
        RawSymbol = new Symbol("BTCUSDT"),
        AssetClass = AssetClass.Crypto,
        InstrumentClass = InstrumentClass.Swap,
        QuoteCurrency = Currencies.USDT,
        BaseCurrency = Currencies.BTC,
        SettlementCurrency = Currencies.USDT,
        PricePrecision = 2,
        SizePrecision = 3,
        PriceIncrement = new Price(0.01m, 2),
        SizeIncrement = new Quantity(0.001m, 3),
    });

    private static (TestKernel Kernel, TardisDataClient Client, RecordingDataSink Sink) Client(LoopbackServer server, string? cacheDirectory = null, string basePath = "/v1")
    {
        TestKernel kernel = new();
        kernel.Kernel.Cache.AddInstrument(Perpetual());
        TardisDataClient client = new(new ClientId("TARDIS"), new TardisDataClientConfig { ApiKey = "test-key", BaseUrl = server.HttpBase + basePath, CacheDirectory = cacheDirectory }, kernel.Services);
        RecordingDataSink sink = new();
        client.AttachSink(sink);
        return (kernel, client, sink);
    }

    [Fact]
    public async Task Trades_are_downloaded_from_the_per_day_dataset_path_with_the_bearer_key_and_mapped_column_by_column()
    {
        await using LoopbackServer server = new(_ => StubResponse.Binary(Gzip(TradesCsv)));
        (TestKernel kernel, TardisDataClient client, _) = Client(server);
        using TestKernel kernelLifetime = kernel;

        IReadOnlyList<IData> trades = await client.LoadTradesAsync(_perpId, _dayStart, _dayStart.Add(TimeSpan.FromHours(1)), null, CancellationToken.None).WaitAsync(Wait.Timeout);

        RecordedRequest request = Assert.Single(server.Requests);
        Assert.Equal("/v1/binance-futures/trades/2020/04/01/BTCUSDT.csv.gz", request.Path); // derivatives use the "-PERP" exchange mapping
        Assert.Equal("Bearer test-key", request.Header("Authorization"));
        Assert.Equal(3, trades.Count);
        TradeTick first = Assert.IsType<TradeTick>(trades[0]);
        Assert.Equal(_perpId, first.InstrumentId);
        Assert.Equal(new Price(6409.87m, 2), first.Price);
        Assert.Equal(new Quantity(0.011m, 3), first.Size);
        Assert.Equal(AggressorSide.Seller, first.Aggressor);
        Assert.Equal(new TradeId("356714548"), first.TradeId);
        Assert.Equal(1_585_699_200_245_000_000L, first.TsEvent.Value); // microseconds to nanoseconds
        Assert.Equal(1_585_699_200_355_684_000L, first.TsInit.Value);
        Assert.Equal(AggressorSide.Buyer, ((TradeTick)trades[1]).Aggressor);
        Assert.Equal(AggressorSide.None, ((TradeTick)trades[2]).Aggressor);
        client.Dispose();
    }

    [Fact]
    public async Task Quote_columns_are_read_in_the_tardis_order_ask_amount_ask_price_bid_price_bid_amount()
    {
        await using LoopbackServer server = new(_ => StubResponse.Binary(Gzip(QuotesCsv)));
        (TestKernel kernel, TardisDataClient client, _) = Client(server);
        using TestKernel kernelLifetime = kernel;

        IReadOnlyList<IData> quotes = await client.LoadQuotesAsync(_perpId, _dayStart, _dayStart.Add(TimeSpan.FromHours(1)), null, CancellationToken.None).WaitAsync(Wait.Timeout);

        Assert.Equal("/v1/binance-futures/quotes/2020/04/01/BTCUSDT.csv.gz", Assert.Single(server.Requests).Path);
        QuoteTick quote = Assert.IsType<QuoteTick>(Assert.Single(quotes));
        Assert.Equal(new Price(6409.80m, 2), quote.Bid);
        Assert.Equal(new Price(6409.90m, 2), quote.Ask);
        Assert.Equal(new Quantity(1.25m, 3), quote.BidSize);
        Assert.Equal(new Quantity(2.5m, 3), quote.AskSize);
        Assert.Equal(1_585_699_200_245_000_000L, quote.TsEvent.Value);
        client.Dispose();
    }

    [Fact]
    public async Task Book_rows_become_add_update_and_delete_deltas_with_snapshot_flags_and_rising_sequence()
    {
        await using LoopbackServer server = new(_ => StubResponse.Binary(Gzip(BookCsv)));
        (TestKernel kernel, TardisDataClient client, _) = Client(server);
        using TestKernel kernelLifetime = kernel;

        IReadOnlyList<IData> rows = await client.LoadBookDeltasAsync(_perpId, _dayStart, _dayStart.Add(TimeSpan.FromHours(1)), CancellationToken.None).WaitAsync(Wait.Timeout);

        Assert.Equal("/v1/binance-futures/incremental_book_L2/2020/04/01/BTCUSDT.csv.gz", Assert.Single(server.Requests).Path);
        List<OrderBookDelta> deltas = rows.Cast<OrderBookDelta>().ToList();
        Assert.Equal([BookAction.Add, BookAction.Add, BookAction.Delete, BookAction.Update], deltas.Select(d => d.Action));
        Assert.Equal([OrderSide.Buy, OrderSide.Sell, OrderSide.Buy, OrderSide.Sell], deltas.Select(d => d.Order.Side));
        Assert.Equal([RecordFlags.Snapshot, RecordFlags.Snapshot, RecordFlags.None, RecordFlags.None], deltas.Select(d => d.Flags));
        Assert.Equal([1UL, 2UL, 3UL, 4UL], deltas.Select(d => d.Sequence));
        Assert.Equal(new Quantity(3.75m, 3), deltas[3].Order.Size);
        client.Dispose();
    }

    [Fact]
    public async Task Rows_outside_the_requested_window_are_dropped_and_the_limit_stops_the_read()
    {
        await using LoopbackServer server = new(_ => StubResponse.Binary(Gzip(TradesCsv)));
        (TestKernel kernel, TardisDataClient client, _) = Client(server);
        using TestKernel kernelLifetime = kernel;
        UnixNanos afterFirst = UnixNanos.FromMicroseconds(1_585_699_200_300_000);
        UnixNanos beforeThird = UnixNanos.FromMicroseconds(1_585_699_200_900_000);

        IReadOnlyList<IData> windowed = await client.LoadTradesAsync(_perpId, afterFirst, beforeThird, null, CancellationToken.None).WaitAsync(Wait.Timeout);
        IReadOnlyList<IData> limited = await client.LoadTradesAsync(_perpId, _dayStart, _dayStart.Add(TimeSpan.FromHours(1)), 2, CancellationToken.None).WaitAsync(Wait.Timeout);

        Assert.Equal(new TradeId("356714549"), Assert.IsType<TradeTick>(Assert.Single(windowed)).TradeId);
        Assert.Equal(["356714548", "356714549"], limited.Cast<TradeTick>().Select(t => t.TradeId.Value));
        client.Dispose();
    }

    [Fact]
    public async Task A_window_spanning_midnight_reads_one_file_per_utc_day_and_a_missing_day_is_skipped()
    {
        await using LoopbackServer server = new(r => r.Path.Contains("/2020/04/02/", StringComparison.Ordinal) ? StubResponse.Error(404, string.Empty) : StubResponse.Binary(Gzip(TradesCsv)));
        (TestKernel kernel, TardisDataClient client, _) = Client(server);
        using TestKernel kernelLifetime = kernel;

        IReadOnlyList<IData> trades = await client.LoadTradesAsync(_perpId, _dayStart, _dayStart.Add(TimeSpan.FromDays(2)), null, CancellationToken.None).WaitAsync(Wait.Timeout);

        Assert.Equal(
            ["/v1/binance-futures/trades/2020/04/01/BTCUSDT.csv.gz", "/v1/binance-futures/trades/2020/04/02/BTCUSDT.csv.gz", "/v1/binance-futures/trades/2020/04/03/BTCUSDT.csv.gz"],
            server.Requests.Select(r => r.Path));
        Assert.Equal(6, trades.Count); // 3 rows from each of the two days that exist
        client.Dispose();
    }

    [Fact]
    public async Task Downloaded_files_are_kept_in_the_cache_directory_and_reused_without_a_second_download()
    {
        await using LoopbackServer server = new(_ => StubResponse.Binary(Gzip(TradesCsv)));
        (TestKernel kernel, TardisDataClient client, _) = Client(server, _cacheDirectory);
        using TestKernel kernelLifetime = kernel;

        IReadOnlyList<IData> first = await client.LoadTradesAsync(_perpId, _dayStart, _dayStart.Add(TimeSpan.FromHours(1)), null, CancellationToken.None).WaitAsync(Wait.Timeout);
        IReadOnlyList<IData> second = await client.LoadTradesAsync(_perpId, _dayStart, _dayStart.Add(TimeSpan.FromHours(1)), null, CancellationToken.None).WaitAsync(Wait.Timeout);

        Assert.Single(server.Requests);
        Assert.True(File.Exists(Path.Combine(_cacheDirectory, "binance-futures", "trades", "2020", "04", "01", "BTCUSDT.csv.gz")));
        Assert.Equal(first.Cast<TradeTick>(), second.Cast<TradeTick>());
        client.Dispose();
    }

    [Fact]
    public async Task A_spot_instrument_uses_the_plain_venue_mapping()
    {
        await using LoopbackServer server = new(_ => StubResponse.Binary(Gzip(TradesCsv)));
        (TestKernel kernel, TardisDataClient client, _) = Client(server);
        using TestKernel kernelLifetime = kernel;
        kernel.Kernel.Cache.AddInstrument(BybitExecRig.Spot());

        await client.LoadTradesAsync(InstrumentId.Parse("BTCUSDT.BYBIT"), _dayStart, _dayStart.Add(TimeSpan.FromHours(1)), null, CancellationToken.None).WaitAsync(Wait.Timeout);

        Assert.Equal("/v1/bybit-spot/trades/2020/04/01/BTCUSDT.csv.gz", Assert.Single(server.Requests).Path);
        client.Dispose();
    }

    [Fact]
    public async Task Requests_are_answered_through_the_sink_and_an_unknown_instrument_is_an_error_response()
    {
        await using LoopbackServer server = new(_ => StubResponse.Binary(Gzip(TradesCsv)));
        (TestKernel kernel, TardisDataClient client, RecordingDataSink sink) = Client(server);
        using TestKernel kernelLifetime = kernel;
        RequestTradeTicks known = new(_perpId, _dayStart, _dayStart.Add(TimeSpan.FromHours(1)), null, null, Guid.NewGuid(), TestKernel.Now);
        RequestTradeTicks unknown = known with { InstrumentId = InstrumentId.Parse("DOGEUSDT-PERP.BINANCE"), CommandId = Guid.NewGuid() };

        await client.RequestAsync(known, CancellationToken.None).WaitAsync(Wait.Timeout);
        await client.RequestAsync(unknown, CancellationToken.None).WaitAsync(Wait.Timeout);

        DataResponse ok = await sink.NextResponseAsync();
        DataResponse failed = await sink.NextResponseAsync();
        Assert.Equal(known.CommandId, ok.CorrelationId);
        Assert.Equal(typeof(TradeTick), ok.DataType);
        Assert.Equal(3, ok.Data.Count);
        Assert.True(failed.IsError);
        Assert.Contains("DOGEUSDT-PERP.BINANCE", failed.Error);
        client.Dispose();
    }

    [Fact]
    public async Task Live_subscriptions_are_refused_because_tardis_is_historical_only()
    {
        await using LoopbackServer server = new();
        (TestKernel kernel, TardisDataClient client, RecordingDataSink sink) = Client(server);
        using TestKernel kernelLifetime = kernel;

        await client.SubscribeAsync(Commands.Trades(_perpId), CancellationToken.None);

        Assert.Contains("historical", Assert.Single(sink.SubscriptionFailures).Reason);
        Assert.Empty(server.Requests);
        client.Dispose();
    }
}
