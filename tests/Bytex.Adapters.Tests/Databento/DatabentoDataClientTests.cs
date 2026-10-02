using Bytex.Adapters.Databento;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Adapters.Tests.Databento;

// Why: this vendor's own client always asks for DBN, its binary format, compressed with zstd. This adapter asks for
// CSV instead, and that is a decision rather than a shortcut: a binary layout would have to be inferred field by
// field here, and a decoder that is subtly wrong reads a plausible price out of the wrong bytes rather than failing.
// The column names it reads were taken from the vendor's own format library - `bid_px_00` and the rest, including
// the two-digit level suffix - and the one thing no column name says is that **every price is a fixed-point integer
// of billionths**. Read as a decimal it is wrong by nine orders of magnitude, in the direction that looks like a
// very cheap instrument rather than like an error.
public sealed class DatabentoDataClientTests
{
    private static readonly MarketKey _instrument = MarketKey.Parse("bx-market:v2/DATABENTO/ESZ4");

    private const string TradesCsv =
        "ts_recv,ts_event,rtype,publisher_id,instrument_id,action,side,depth,price,size,flags,ts_in_delta,sequence\n" +
        "1704067200500000000,1704067200000000000,0,1,42,T,B,0,4750250000000,3,0,0,1001\n" +
        "1704067201500000000,1704067201000000000,0,1,42,T,A,0,4750500000000,1,0,0,1002\n" +
        "1704067202500000000,1704067202000000000,0,1,42,T,N,0,4750750000000,2,0,0,1003\n";

    private const string QuotesCsv =
        "ts_recv,ts_event,rtype,publisher_id,instrument_id,action,side,depth,price,size,flags,ts_in_delta,sequence," +
        "bid_px_00,ask_px_00,bid_sz_00,ask_sz_00,bid_ct_00,ask_ct_00\n" +
        "1704067200500000000,1704067200000000000,1,1,42,A,B,0,0,0,0,0,2001,4750000000000,4750250000000,5,7,2,3\n";

    private const string BarsCsv =
        "ts_event,rtype,publisher_id,instrument_id,open,high,low,close,volume\n" +
        "1704067200000000000,34,1,42,4750000000000,4752000000000,4749000000000,4751000000000,1234\n";

    private static Instrument Future() => new CurrencyPair(new InstrumentSpec
    {
        Id = _instrument,
        RawSymbol = new Symbol("ESZ4"),
        AssetClass = AssetClass.Index,
        InstrumentClass = InstrumentClass.Spot,
        QuoteCurrency = Currencies.USD,
        BaseCurrency = Currencies.USD,
        PricePrecision = 2,
        SizePrecision = 0,
        PriceIncrement = new Price(0.25m, 2),
        SizeIncrement = new Quantity(1m, 0),
    });

    private static (TestTradingRuntime TradingRuntime, DatabentoDataClient Client) Client(LoopbackServer server)
    {
        TestTradingRuntime tradingRuntime = new();
        tradingRuntime.TradingRuntime.Cache.AddInstrument(Future());
        DatabentoDataClient client = new(
            new ClientId("DATABENTO"),
            new DatabentoDataClientConfig { ApiKey = "test-key", BaseUrl = server.HttpBase.ToString().TrimEnd('/'), Dataset = "GLBX.MDP3" },
            tradingRuntime.Services);

        return (tradingRuntime, client);
    }

    [Fact]
    public async Task A_price_is_read_as_the_billionths_this_vendor_writes()
    {
        // 4,750,250,000,000 is 4750.25 and nothing in the number says so. This is the assertion that would fail if
        // anybody ever "simplified" the scale away.
        await using LoopbackServer server = new(new Routes()
            .On("POST", "/v0/timeseries.get_range", TradesCsv)
            .Handle);

        (_, DatabentoDataClient client) = Client(server);
        IReadOnlyList<TradeTick> trades = await client.LoadTradesAsync(_instrument, "ESZ4", new UnixNanos(1), new UnixNanos(2));

        Assert.Equal(3, trades.Count);
        Assert.Equal(4750.25m, trades[0].Price.Value);
        Assert.Equal(3m, trades[0].Size.Value);
        Assert.Equal(new UnixNanos(1704067200000000000), trades[0].EventTime);
        Assert.Equal(new UnixNanos(1704067200500000000), trades[0].CreatedTime);
    }

    [Fact]
    public async Task A_side_this_vendor_could_not_determine_stays_undetermined()
    {
        // It writes N when it does not know. Resolving that into a buy would invent an aggressor, and anything
        // counting buy volume would count it.
        await using LoopbackServer server = new(new Routes()
            .On("POST", "/v0/timeseries.get_range", TradesCsv)
            .Handle);

        (_, DatabentoDataClient client) = Client(server);
        IReadOnlyList<TradeTick> trades = await client.LoadTradesAsync(_instrument, "ESZ4", new UnixNanos(1), new UnixNanos(2));

        Assert.Equal(AggressorSide.Buyer, trades[0].Aggressor);
        Assert.Equal(AggressorSide.Seller, trades[1].Aggressor);
        Assert.Equal(AggressorSide.None, trades[2].Aggressor);
    }

    [Fact]
    public async Task A_quote_is_read_from_the_levelled_columns_this_vendor_writes()
    {
        // The two-digit level suffix is the vendor's own convention. A reader looking for `bid_px` finds nothing.
        await using LoopbackServer server = new(new Routes()
            .On("POST", "/v0/timeseries.get_range", QuotesCsv)
            .Handle);

        (_, DatabentoDataClient client) = Client(server);
        QuoteTick quote = Assert.Single(await client.LoadQuotesAsync(_instrument, "ESZ4", new UnixNanos(1), new UnixNanos(2)));

        Assert.Equal(4750.00m, quote.Bid.Value);
        Assert.Equal(4750.25m, quote.Ask.Value);
        Assert.Equal(5m, quote.BidSize.Value);
        Assert.Equal(7m, quote.AskSize.Value);
        Assert.Equal(new UnixNanos(1704067200000000000), quote.EventTime);
        Assert.Equal(new UnixNanos(1704067200500000000), quote.CreatedTime);
    }

    [Fact]
    public async Task Bars_carry_the_scale_too()
    {
        CandleSeries candleSeries = new CandleSeries(_instrument, new SamplingRule(1, SamplingMethod.Minute, PriceType.Last));

        await using LoopbackServer server = new(new Routes()
            .On("POST", "/v0/timeseries.get_range", BarsCsv)
            .Handle);

        (_, DatabentoDataClient client) = Client(server);
        Bar bar = Assert.Single(await client.LoadBarsAsync(candleSeries, "ESZ4", new UnixNanos(1), new UnixNanos(2)));

        Assert.Equal(4750.00m, bar.Open.Value);
        Assert.Equal(4752.00m, bar.High.Value);
        Assert.Equal(4749.00m, bar.Low.Value);
        Assert.Equal(4751.00m, bar.Close.Value);
        Assert.Equal(1234m, bar.Volume.Value);
        Assert.Equal(new UnixNanos(1704067200000000000), bar.EventTime);
        Assert.Equal(bar.EventTime, bar.CreatedTime);
    }

    [Theory]
    [InlineData("bx-sampling:v2/second/1/last", "ohlcv-1s")]
    [InlineData("bx-sampling:v2/minute/1/last", "ohlcv-1m")]
    [InlineData("bx-sampling:v2/hour/1/last", "ohlcv-1h")]
    [InlineData("bx-sampling:v2/day/1/last", "ohlcv-1d")]
    public void Each_aggregation_this_vendor_publishes_has_its_own_schema(string spec, string schema)
    {
        Assert.Equal(schema, DatabentoDataClient.SchemaFor(new CandleSeries(_instrument, SamplingRule.Parse(spec))));
    }

    [Fact]
    public void An_aggregation_this_vendor_does_not_publish_is_refused_rather_than_built_here()
    {
        // A five-minute bar is not a smaller ask than a one-minute one. Aggregating it here would hand back a bar
        // this adapter made, under the vendor's name, and nothing downstream could tell.
        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(
            () => DatabentoDataClient.SchemaFor(new CandleSeries(_instrument, new SamplingRule(5, SamplingMethod.Minute, PriceType.Last))));

        Assert.Contains("never published", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_request_carries_the_parameters_this_vendor_names()
    {
        // Read from the vendor's own client rather than guessed. A wrong parameter name is answered with an error
        // about something else entirely, which is a long way to travel to find a typo.
        await using LoopbackServer server = new(new Routes()
            .On("POST", "/v0/timeseries.get_range", TradesCsv)
            .Handle);

        (_, DatabentoDataClient client) = Client(server);
        await client.LoadTradesAsync(_instrument, "ESZ4", new UnixNanos(10), new UnixNanos(20));

        RecordedRequest request = Assert.Single(server.RequestsTo("/v0/timeseries.get_range"));

        Assert.Contains("dataset=GLBX.MDP3", request.Body, StringComparison.Ordinal);
        Assert.Contains("schema=trades", request.Body, StringComparison.Ordinal);
        Assert.Contains("stype_in=raw_symbol", request.Body, StringComparison.Ordinal);
        Assert.Contains("encoding=csv", request.Body, StringComparison.Ordinal);
        Assert.Contains("compression=none", request.Body, StringComparison.Ordinal);
        Assert.Contains("start=10", request.Body, StringComparison.Ordinal);
        Assert.Contains("end=20", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_dataset_nobody_named_is_refused_before_anything_is_asked()
    {
        // This vendor serves many datasets and assumes none. Picking one here would ask about instruments somebody
        // may not be entitled to and report the entitlement error as though it were theirs.
        await using LoopbackServer server = new(new Routes().On("POST", "/v0/timeseries.get_range", TradesCsv).Handle);
        TestTradingRuntime tradingRuntime = new();
        tradingRuntime.TradingRuntime.Cache.AddInstrument(Future());
        DatabentoDataClient client = new(
            new ClientId("DATABENTO"),
            new DatabentoDataClientConfig { ApiKey = "test-key", BaseUrl = server.HttpBase.ToString().TrimEnd('/') },
            tradingRuntime.Services);

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.LoadTradesAsync(_instrument, "ESZ4", new UnixNanos(1), new UnixNanos(2)));

        Assert.Contains("dataset", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task The_vendors_own_refusal_is_carried_through_rather_than_replaced()
    {
        // Its errors say which entitlement or dataset is the problem, and nothing this adapter could write would be
        // more use to the person reading it.
        await using LoopbackServer server = new(new Routes()
            .On("POST", "/v0/timeseries.get_range", _ => new StubResponse(403, "{\"detail\":\"no entitlement for GLBX.MDP3\"}"))
            .Handle);

        (_, DatabentoDataClient client) = Client(server);

        Exception failed = await Assert.ThrowsAnyAsync<Exception>(
            () => client.LoadTradesAsync(_instrument, "ESZ4", new UnixNanos(1), new UnixNanos(2)));

        Assert.Contains("no entitlement", failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_column_is_refused_rather_than_defaulted()
    {
        // A zero price reads as data. The whole reason for asking in a named format is that a column either is there
        // or the request was not what was meant.
        await using LoopbackServer server = new(new Routes()
            .On("POST", "/v0/timeseries.get_range", "ts_recv,ts_event,side,size\n1,2,B,3\n")
            .Handle);

        (_, DatabentoDataClient client) = Client(server);

        FormatException refused = await Assert.ThrowsAsync<FormatException>(
            () => client.LoadTradesAsync(_instrument, "ESZ4", new UnixNanos(1), new UnixNanos(2)));

        Assert.Contains("price", refused.Message, StringComparison.Ordinal);
    }
}
