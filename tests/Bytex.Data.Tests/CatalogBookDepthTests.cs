using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Primitives;
using Bytex.Data.Tests.Support;

namespace Bytex.Data.Tests;

// The matcher has walked an order book since 0.6 and the catalog had nowhere to put one: `grep Depth` over this
// library returned nothing. A loader that produces depth the catalog cannot store leaves every run fetching it from
// the vendor again, which re-pays a rate limit per sweep and makes a backtest depend on the network.
//
// Two properties are held here, and both are about not inventing levels.
//
// A LADDER KEEPS ITS OWN LENGTH. Twenty-five is a property of one vendor's dataset, not of depth, so nothing in the
// schema knows a count - the ladders are parquet list columns. A snapshot written at N levels reads back at N.
//
// AND A TRUNCATED LADDER STAYS TRUNCATED. A fixed-width schema would need a value in every column, and a zero there
// is a price a fill can reach, so a book eleven deep must not come back as eleven levels followed by fourteen at
// zero. That rule was mutation-checked in the loader that reads these files; it has to survive parquet too, or it
// was only ever true in memory.
//
// Every read below goes through a freshly opened catalog, like the other round-trip tests, so nothing is being
// answered out of the writer's own state.
public sealed class CatalogBookDepthTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// Compares a snapshot field by field instead of with one Assert.Equal on the record.
    ///
    /// <para>
    /// The other round-trip tests can compare a whole record, because a quote or a bar is made of value types.
    /// A snapshot is not: it holds each side as an <see cref="IReadOnlyList{T}"/>, so the record equality the
    /// compiler generates compares the two list REFERENCES and two snapshots with identical levels never come out
    /// equal. That is a property of the model rather than of this catalog, so it is worked around here and not
    /// papered over - the levels themselves are still compared element for element.
    /// </para>
    /// </summary>
    private static void AssertSame(OrderBookDepth expected, OrderBookDepth actual)
    {
        Assert.Equal(expected.MarketKey, actual.MarketKey);
        Assert.Equal(expected.Bids, actual.Bids);
        Assert.Equal(expected.Asks, actual.Asks);
        Assert.Equal(expected.Flags, actual.Flags);
        Assert.Equal(expected.Sequence, actual.Sequence);
        Assert.Equal(expected.EventTime, actual.EventTime);
        Assert.Equal(expected.CreatedTime, actual.CreatedTime);
    }

    private static void AssertSame(IReadOnlyList<OrderBookDepth> expected, IReadOnlyList<OrderBookDepth> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            AssertSame(expected[i], actual[i]);
        }
    }

    /// <summary>
    /// The whole book, out and back. <see cref="OrderBookDepth"/> is a record over value types, so one Assert.Equal
    /// per side covers every level's price, size and order count WITH precision, and the rest covers the row.
    /// </summary>
    [Fact]
    public async Task Depth_round_trips_exactly()
    {
        OrderBookDepth written = Sample.Depth(0, sequence: 7);

        await new MarketArchive(_temp.Root).WriteOrderBookDepthAsync([written]);
        OrderBookDepth restored = Assert.Single(await new MarketArchive(_temp.Root).OrderBookDepthAsync(Sample.Btc));

        Assert.Equal(written.Bids, restored.Bids);
        Assert.Equal(written.Asks, restored.Asks);
        Assert.Equal(written.MarketKey, restored.MarketKey);
        Assert.Equal(written.Flags, restored.Flags);
        Assert.Equal(7ul, restored.Sequence);
        Assert.Equal(Sample.T0 - 5, restored.EventTime.Value);
        Assert.Equal(Sample.T0, restored.CreatedTime.Value);

        // Named explicitly, because a decimal that lost its trailing zeros would still compare equal to a caller
        // reading .Value and only differ where it matters - formatting a price and sizing an order.
        Assert.Equal((byte)2, restored.Bids[0].Price.Precision);
        Assert.Equal((byte)5, restored.Bids[0].Size.Precision);
        Assert.Equal(3.00000m, restored.Bids[0].Size.Value);
    }

    /// <summary>
    /// <b>A ladder reads back at the length it was written.</b> This is the test the storage shape exists for: a
    /// fixed-width schema would have to put something in the columns a short ladder does not fill, and a zero there
    /// is a level a fill can bind to.
    /// </summary>
    [Theory]
    [InlineData(25, 25)]
    [InlineData(11, 3)]
    [InlineData(1, 25)]
    [InlineData(25, 1)]
    [InlineData(0, 4)]
    [InlineData(4, 0)]
    [InlineData(0, 0)]
    public async Task A_ladder_reads_back_at_the_length_it_was_written(int bidLevels, int askLevels)
    {
        await new MarketArchive(_temp.Root).WriteOrderBookDepthAsync([Sample.Depth(0, bidLevels, askLevels)]);
        OrderBookDepth restored = Assert.Single(await new MarketArchive(_temp.Root).OrderBookDepthAsync(Sample.Btc));

        Assert.Equal(bidLevels, restored.Bids.Count);
        Assert.Equal(askLevels, restored.Asks.Count);

        // Not one padded level among them, on either side: no zero price, no zero size, no zero order count.
        Assert.All(restored.Bids.Concat(restored.Asks), level =>
        {
            Assert.NotEqual(0m, level.Price.Value);
            Assert.NotEqual(0m, level.Size.Value);
            Assert.NotEqual(0, level.Count);
        });
    }

    /// <summary>
    /// Snapshots of differing depth in one file, because a real book changes depth minute to minute and a list column
    /// has to hold rows of different lengths beside each other rather than widening them all to the deepest.
    /// </summary>
    [Fact]
    public async Task Snapshots_of_different_depths_share_a_file()
    {
        OrderBookDepth[] written =
        [
            Sample.Depth(0, 25, 25, sequence: 1),
            Sample.Depth(1_000_000_000, 4, 19, sequence: 2),
            Sample.Depth(2_000_000_000, 1, 1, sequence: 3),
        ];

        await new MarketArchive(_temp.Root).WriteOrderBookDepthAsync(written);
        IReadOnlyList<OrderBookDepth> restored = await new MarketArchive(_temp.Root).OrderBookDepthAsync(Sample.Btc);

        AssertSame(written, restored);
        Assert.Equal<IEnumerable<int>>([25, 4, 1], restored.Select(d => d.Bids.Count));
        Assert.Equal<IEnumerable<int>>([25, 19, 1], restored.Select(d => d.Asks.Count));
    }

    /// <summary>A window over depth selects on the same timestamp the other kinds do, and streaming agrees with it.</summary>
    [Fact]
    public async Task Depth_can_be_read_for_a_window_and_streamed()
    {
        OrderBookDepth[] written =
        [
            Sample.Depth(0, sequence: 1),
            Sample.Depth(1_000_000_000, sequence: 2),
            Sample.Depth(2_000_000_000, sequence: 3),
        ];

        await new MarketArchive(_temp.Root).WriteOrderBookDepthAsync(written);

        IReadOnlyList<OrderBookDepth> window = await new MarketArchive(_temp.Root)
            .OrderBookDepthAsync(Sample.Btc, Sample.At(1_000_000_000), Sample.At(1_000_000_000));
        Assert.Equal(2ul, Assert.Single(window).Sequence);

        List<OrderBookDepth> streamed = [];
        await foreach (OrderBookDepth depth in new MarketArchive(_temp.Root).StreamOrderBookDepthAsync(Sample.Btc))
        {
            streamed.Add(depth);
        }

        AssertSame(written, streamed);
    }

    /// <summary>And it is a data set a person can see listed, like every other kind the catalog holds.</summary>
    [Fact]
    public async Task Depth_is_listed_as_something_the_catalog_holds()
    {
        MarketArchive catalog = new(_temp.Root);
        await catalog.WriteOrderBookDepthAsync([Sample.Depth(0)]);

        Assert.Contains(Sample.Btc, new MarketArchive(_temp.Root).OrderBookDepthInstruments());
        CatalogEntry entry = Assert.Single(new MarketArchive(_temp.Root).Entries(), e => e.Kind == "book_depth");
        Assert.Equal(Sample.Btc.Value, entry.Key);
    }

    /// <summary>
    /// <b>A mixed batch writes the depth in it.</b> The dispatch ran five type filters and returned success, so
    /// depth handed to it went in and was never written - no error, no count, nothing to notice.
    /// </summary>
    [Fact]
    public async Task A_mixed_batch_writes_the_depth_in_it()
    {
        List<IData> batch = [Sample.Depth(0), Sample.Trade(1), Sample.Quote(2), Sample.Bar(3)];

        await new MarketArchive(_temp.Root).WriteAsync(batch);

        MarketArchive reader = new(_temp.Root);
        Assert.Single(await reader.OrderBookDepthAsync(Sample.Btc));
        Assert.Single(await reader.TradeTicksAsync(Sample.Btc));
        Assert.Single(await reader.QuoteTicksAsync(Sample.Btc));
        Assert.Single(await reader.BarsAsync(Sample.BtcMinute));
    }

    /// <summary>
    /// And a kind the catalog genuinely cannot store is REFUSED by name and count, rather than dropped. Silence was
    /// the defect: a caller handing over a list got back a promise the catalog had not kept, and what went missing
    /// was missing rather than visibly wrong.
    /// </summary>
    [Fact]
    public async Task A_kind_the_catalog_cannot_store_is_refused_by_name()
    {
        List<IData> batch =
        [
            Sample.Trade(0),
            new MarkPriceUpdate(Sample.Btc, new Price(42000.55m, 2), Sample.At(-3), Sample.At(0)),
            new MarkPriceUpdate(Sample.Btc, new Price(42000.56m, 2), Sample.At(-2), Sample.At(1)),
            new Signal("mood", 1m, Sample.At(-3), Sample.At(0)),
        ];

        NotSupportedException refused = await Assert.ThrowsAsync<NotSupportedException>(
            () => new MarketArchive(_temp.Root).WriteAsync(batch));

        Assert.Contains("MarkPriceUpdate x2", refused.Message, StringComparison.Ordinal);
        Assert.Contains("Signal x1", refused.Message, StringComparison.Ordinal);

        // It says what a catalog CAN hold, so the message answers the question it raises.
        Assert.Contains("book depth", refused.Message, StringComparison.Ordinal);

        // And the rest of the batch was written: the refusal reports the part that was not stored, not that
        // nothing was.
        Assert.Single(await new MarketArchive(_temp.Root).TradeTicksAsync(Sample.Btc));
    }

    /// <summary>A batch of deltas is deltas, so it is unrolled into them rather than turned away.</summary>
    [Fact]
    public async Task A_batch_of_deltas_is_unrolled_into_the_delta_store()
    {
        OrderBookDelta[] deltas = [Sample.Delta(0, 1), Sample.Delta(1_000_000_000, 2, side: OrderSide.Sell)];

        await new MarketArchive(_temp.Root).WriteAsync(
            [new OrderBookDeltas(Sample.Btc, deltas, RecordFlags.Snapshot, 2, Sample.At(-5), Sample.At(1_000_000_000))]);

        Assert.Equal(deltas, await new MarketArchive(_temp.Root).OrderBookDeltasAsync(Sample.Btc));
    }
}
