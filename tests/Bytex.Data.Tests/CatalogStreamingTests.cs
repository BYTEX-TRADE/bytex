using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Primitives;
using Bytex.Data;
using Bytex.Data.Tests.Support;

namespace Bytex.Data.Tests;

// Why (R9.7): every read materialises its whole range into a list and then sorts it, so a run's dataset has to fit in
// memory roughly twice. Bars do not care - a year of one-minute data for an instrument is about 34 MB - but quotes
// and book deltas do: 190 MB compressed per instrument-day against a live archive, and a host that offers a month of
// it. A period that cannot be held cannot be read at all today.
//
// So there is a second way to read the same data, yielding a file at a time. What it must never do is quietly hand
// back a different answer from the method beside it, and what it must not pretend is that it can reorder a catalog
// it is not holding - so it says so instead, naming the files that disagree.
public sealed class CatalogStreamingTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private static readonly MarketKey _instrument = MarketKey.Parse("bx-market:v2/BINANCE/BTCUSDT");

    public void Dispose() => _temp.Dispose();

    private MarketArchive Catalog() => new(_temp.Root);

    private static QuoteTick Quote(long nanos) =>
        new(_instrument, new Price(100m, 2), new Price(101m, 2), new Quantity(1m, 3), new Quantity(1m, 3), new UnixNanos(nanos), new UnixNanos(nanos));

    private static TradeTick Trade(long nanos) =>
        new(_instrument, new Price(100m, 2), new Quantity(1m, 3), AggressorSide.Buyer, new TradeId(nanos.ToString(System.Globalization.CultureInfo.InvariantCulture)), new UnixNanos(nanos), new UnixNanos(nanos));

    private static async Task<List<T>> DrainAsync<T>(IAsyncEnumerable<T> source)
    {
        List<T> drained = new();
        await foreach (T item in source)
        {
            drained.Add(item);
        }

        return drained;
    }

    [Fact]
    public async Task Streaming_gives_the_same_records_in_the_same_order_as_reading()
    {
        // The property that matters most: two ways of reading must not be two answers. If they ever differ, a
        // backtest run one way disagrees with the same backtest run the other, and neither is obviously wrong.
        MarketArchive catalog = Catalog();
        await catalog.WriteQuoteTicksAsync([Quote(1_000), Quote(2_000), Quote(3_000)]);
        await catalog.WriteQuoteTicksAsync([Quote(4_000), Quote(5_000)]);

        IReadOnlyList<QuoteTick> read = await catalog.QuoteTicksAsync(_instrument);
        List<QuoteTick> streamed = await DrainAsync(catalog.StreamQuoteTicksAsync(_instrument));

        Assert.Equal(read.Select(q => q.CreatedTime), streamed.Select(q => q.CreatedTime));
        Assert.Equal(5, streamed.Count);
    }

    [Fact]
    public async Task A_range_narrows_a_stream_the_same_way_it_narrows_a_read()
    {
        MarketArchive catalog = Catalog();
        await catalog.WriteTradeTicksAsync([Trade(1_000), Trade(2_000), Trade(3_000), Trade(4_000)]);

        IReadOnlyList<TradeTick> read = await catalog.TradeTicksAsync(_instrument, new UnixNanos(2_000), new UnixNanos(3_000));
        List<TradeTick> streamed = await DrainAsync(catalog.StreamTradeTicksAsync(_instrument, new UnixNanos(2_000), new UnixNanos(3_000)));

        Assert.Equal(read.Select(t => t.CreatedTime), streamed.Select(t => t.CreatedTime));
        Assert.Equal(2, streamed.Count);
    }

    [Fact]
    public async Task A_stream_of_something_never_written_is_empty_rather_than_a_failure()
    {
        Assert.Empty(await DrainAsync(Catalog().StreamQuoteTicksAsync(MarketKey.Parse("bx-market:v2/BINANCE/NOTHING"))));
    }

    [Fact]
    public async Task A_stream_stops_rather_than_yielding_records_out_of_order()
    {
        // The honest limit. The list method sorts everything it has read, so an overlapping catalog still comes back
        // in order; streaming holds one file and cannot. Rather than handing back data whose order silently depends
        // on file names, it names the two files that disagree - because out-of-order market data is found much later,
        // through a strategy that behaved oddly.
        MarketArchive catalog = Catalog();
        // Two files whose ranges interleave rather than follow each other: 1000-5000 sorts before 2000-3000, so
        // reading them in name order walks forward to 5000 and then back to 2000.
        await catalog.WriteQuoteTicksAsync([Quote(1_000), Quote(5_000)]);
        await catalog.WriteQuoteTicksAsync([Quote(2_000), Quote(3_000)]);

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await DrainAsync(catalog.StreamQuoteTicksAsync(_instrument)));

        Assert.Contains("overlap in time", refused.Message, StringComparison.Ordinal);
        Assert.Contains(".parquet", refused.Message, StringComparison.Ordinal);

        // And the method beside it still answers, so the catalog is readable while somebody decides what to do.
        Assert.Equal(4, (await catalog.QuoteTicksAsync(_instrument)).Count);
    }

    [Fact]
    public async Task A_stream_can_be_stopped_early_without_reading_the_rest()
    {
        // The reason for streaming at all: a caller that wants a thousand records out of a month must not pay for
        // the month. Taking two from a five-record catalog is the smallest way to state that, and it is the shape a
        // chunked backtest depends on.
        MarketArchive catalog = Catalog();
        await catalog.WriteQuoteTicksAsync([Quote(1_000), Quote(2_000), Quote(3_000), Quote(4_000), Quote(5_000)]);

        List<QuoteTick> first = new();
        await foreach (QuoteTick quote in catalog.StreamQuoteTicksAsync(_instrument))
        {
            first.Add(quote);
            if (first.Count == 2)
            {
                break;
            }
        }

        Assert.Equal([new UnixNanos(1_000), new UnixNanos(2_000)], first.Select(q => q.CreatedTime));
    }
}
