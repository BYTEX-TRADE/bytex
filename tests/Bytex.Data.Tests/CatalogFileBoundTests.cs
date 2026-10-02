using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Primitives;
using Bytex.Data.Tests.Support;

namespace Bytex.Data.Tests;

// A file is the unit a read holds. StreamAsync opens one file at a time, but every row in that file is materialised
// before the first is yielded - so an unbounded file is an unbounded read no matter how lazy the caller is, and
// "stream it" was not an answer while one write produced one file of any size.
//
// It is measurable rather than theoretical: 25-level book depth costs about 3,011 bytes a snapshot, so an
// instrument-day is 1.53 million snapshots and 4.3 GiB in one file that nothing could open.
//
// So a write now fills files to MaxRowsPerFile and starts another, each named for the range actually in it. These
// tests use the internal constructor to set a small bound, because the property under test is "it starts a new file
// at the bound", not the size of the bound.
//
// Consolidation obeys the same bound - it reduces the file COUNT to the fewest the bound allows, not to one - or it
// would undo the thing on the next `bytex catalog consolidate`.
public sealed class CatalogFileBoundTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private MarketArchive Catalog(int bound = 10) => new(new LocalObjectStore(_temp.Root), bound);

    private static QuoteTick[] Quotes(int count) =>
        [.. Enumerable.Range(0, count).Select(i => Sample.Quote(i * 1_000_000_000L, 42000.10m + i))];

    private IReadOnlyList<string> FilesOf(string kind, string key) =>
        [.. Catalog().Segments(kind, key).Select(s => s.ObjectKey)];

    /// <summary>The bound is what starts a new file, and nothing is lost at the seam.</summary>
    [Theory]
    [InlineData(9, 1)]
    [InlineData(10, 1)]
    [InlineData(11, 2)]
    [InlineData(25, 3)]
    [InlineData(30, 3)]
    public async Task A_write_past_the_bound_starts_another_file(int records, int expectedFiles)
    {
        await Catalog().WriteQuoteTicksAsync(Quotes(records));

        Assert.Equal(expectedFiles, FilesOf("quotes", Sample.Btc.Value).Count);
        Assert.Equal(records, (await Catalog().QuoteTicksAsync(Sample.Btc)).Count);
    }

    /// <summary>Everything comes back, in order, across the seams.</summary>
    [Fact]
    public async Task Records_read_back_in_order_across_file_boundaries()
    {
        QuoteTick[] written = Quotes(25);
        await Catalog().WriteQuoteTicksAsync(written);

        IReadOnlyList<QuoteTick> restored = await Catalog().QuoteTicksAsync(Sample.Btc);

        Assert.Equal(written, restored);
        Assert.Equal(restored.OrderBy(q => q.CreatedTime.Value), restored);
    }

    /// <summary>And streaming crosses them without the overlap guard firing, because the ranges do not overlap.</summary>
    [Fact]
    public async Task Streaming_crosses_file_boundaries_in_order()
    {
        QuoteTick[] written = Quotes(25);
        await Catalog().WriteQuoteTicksAsync(written);

        List<QuoteTick> streamed = [];
        await foreach (QuoteTick quote in Catalog().StreamQuoteTicksAsync(Sample.Btc))
        {
            streamed.Add(quote);
        }

        Assert.Equal(written, streamed);
    }

    /// <summary>
    /// <b>Each file is named for what is in it, which is what keeps a windowed read cheap.</b> The name carries the
    /// range, and <c>Overlaps</c> skips a file whose range falls outside the window — so a name covering rows the
    /// file does not hold would either read too much or, worse, skip a file that was needed.
    /// </summary>
    [Fact]
    public async Task Each_file_is_named_for_the_range_it_actually_holds()
    {
        await Catalog().WriteQuoteTicksAsync(Quotes(25));

        IReadOnlyList<string> files = FilesOf("quotes", Sample.Btc.Value);
        Assert.Equal(3, files.Count);

        // Ranges in ascending order, each starting after the previous one ended, so nothing overlaps.
        long previousEnd = long.MinValue;
        foreach (ArchiveSegment segment in Catalog().Segments("quotes", Sample.Btc.Value))
        {
            string file = segment.ObjectKey;
            Assert.True(Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out _));
            long start = segment.Start;
            long end = segment.End;

            Assert.True(start <= end, $"{file} is named for an empty range");
            Assert.True(start > previousEnd, $"{file} starts at {start}, which is not after the previous file's {previousEnd}");
            previousEnd = end;
        }

        // And a window inside the second file returns exactly what is in that window.
        IReadOnlyList<QuoteTick> window = await Catalog().QuoteTicksAsync(Sample.Btc, Sample.At(12_000_000_000L), Sample.At(14_000_000_000L));
        Assert.Equal([12, 13, 14], window.Select(q => (int)((q.CreatedTime.Value - Sample.T0) / 1_000_000_000L)));
    }

    /// <summary>
    /// <b>Consolidation reduces the file count to the fewest the bound allows, not to one.</b> Merging a chunked key
    /// back into a single file would put the whole data set into one read and undo the bound entirely.
    /// </summary>
    [Fact]
    public async Task Consolidation_keeps_the_bound()
    {
        // Five separate writes of five, so the key starts as five files.
        foreach (QuoteTick[] batch in Quotes(25).Chunk(5))
        {
            await Catalog().WriteQuoteTicksAsync(batch);
        }

        Assert.Equal(5, FilesOf("quotes", Sample.Btc.Value).Count);

        int rows = await Catalog().ConsolidateAsync("quotes", Sample.Btc.Value);

        Assert.Equal(25, rows);
        Assert.Equal(3, FilesOf("quotes", Sample.Btc.Value).Count);
        Assert.Equal(Quotes(25), await Catalog().QuoteTicksAsync(Sample.Btc));
    }

    /// <summary>A key that already fits in one file still consolidates to one, and says it had nothing to do.</summary>
    [Fact]
    public async Task Consolidation_of_a_small_key_still_makes_one_file()
    {
        await Catalog().WriteQuoteTicksAsync(Quotes(4));
        await Catalog().WriteQuoteTicksAsync(Quotes(4));

        Assert.Equal(2, FilesOf("quotes", Sample.Btc.Value).Count);
        Assert.Equal(4, await Catalog().ConsolidateAsync("quotes", Sample.Btc.Value));
        Assert.Single(FilesOf("quotes", Sample.Btc.Value));
    }

    /// <summary>And it still drops what is held twice, which is the reason it exists.</summary>
    [Fact]
    public async Task Consolidation_still_drops_records_held_twice()
    {
        await Catalog().WriteQuoteTicksAsync(Quotes(25));
        await Catalog().WriteQuoteTicksAsync(Quotes(25));

        Assert.Equal(6, FilesOf("quotes", Sample.Btc.Value).Count);
        Assert.Equal(25, await Catalog().ConsolidateAsync("quotes", Sample.Btc.Value));
        Assert.Equal(Quotes(25), await Catalog().QuoteTicksAsync(Sample.Btc));
    }

    /// <summary>
    /// <b>Every kind the catalog admits to holding can actually be consolidated.</b>
    ///
    /// <para>
    /// This is the guard for a real shipped defect: <c>book_depth</c> was added to the table of kinds but not to the
    /// dispatch that consolidates one, so it passed the name check - the catalog does hold it - and then threw
    /// "'book_depth' is not a kind this catalog holds", a message contradicting itself. `bytex catalog consolidate`
    /// with no --kind walks every entry, so storing depth broke consolidating everything, and aborted the loop
    /// before the kinds after it. Driving this from the catalog's own listing means the next kind added cannot
    /// repeat it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Every_kind_the_catalog_holds_can_be_consolidated()
    {
        // Written twice each, so no key takes the "already one file" path and every kind reaches the dispatch.
        for (int pass = 0; pass < 2; pass++)
        {
            MarketArchive catalog = Catalog();
            await catalog.WriteQuoteTicksAsync([Sample.Quote(0)]);
            await catalog.WriteTradeTicksAsync([Sample.Trade(0)]);
            await catalog.WriteBarsAsync([Sample.Bar(0)]);
            await catalog.WriteOrderBookDeltasAsync([Sample.Delta(0, 1)]);
            await catalog.WriteOrderBookDepthAsync([Sample.Depth(0)]);
            await catalog.WriteFundingRatesAsync([new FundingRateUpdate(Sample.Btc, 0.0001m, Sample.At(3600), Sample.At(-1), Sample.At(0))]);
        }

        CatalogEntry[] entries = [.. Catalog().Entries()];
        Assert.Equal(6, entries.Length);

        foreach (CatalogEntry entry in entries)
        {
            // One record written twice is one record after consolidation - and, crucially, no throw.
            int rows = await Catalog().ConsolidateAsync(entry.Kind, entry.Key);
            Assert.Equal(1, rows);
        }

        // Named explicitly as well, so a listing that stopped returning depth could not make this test vacuous.
        Assert.Contains(entries, e => e.Kind == "book_depth");
    }

    /// <summary>Depth consolidates and reads back, since it is the kind the bound was sized from.</summary>
    [Fact]
    public async Task Depth_consolidates_and_keeps_the_bound()
    {
        OrderBookDepth[] written = [.. Enumerable.Range(0, 25).Select(i => Sample.Depth(i * 1_000_000_000L, sequence: (ulong)i + 1))];

        await Catalog().WriteOrderBookDepthAsync(written);
        Assert.Equal(3, FilesOf("book_depth", Sample.Btc.Value).Count);

        Assert.Equal(25, await Catalog().ConsolidateAsync("book_depth", Sample.Btc.Value));
        Assert.Equal(3, FilesOf("book_depth", Sample.Btc.Value).Count);

        IReadOnlyList<OrderBookDepth> restored = await Catalog().OrderBookDepthAsync(Sample.Btc);
        Assert.Equal(25, restored.Count);
        Assert.Equal(written.Select(d => d.Sequence), restored.Select(d => d.Sequence));

        // Compared side by side rather than with one Assert.Equal on the record: a snapshot holds its two ladders as
        // IReadOnlyList, so record equality compares the list references and two identical books are never equal.
        // That is being fixed elsewhere; this test must pass on its own either way.
        for (int i = 0; i < written.Length; i++)
        {
            Assert.Equal(written[i].Bids, restored[i].Bids);
            Assert.Equal(written[i].Asks, restored[i].Asks);
            Assert.Equal(written[i].EventTime, restored[i].EventTime);
            Assert.Equal(written[i].CreatedTime, restored[i].CreatedTime);
            Assert.Equal(written[i].Flags, restored[i].Flags);
        }
    }

    /// <summary>A bound below one would mean a file holding nothing, and a write that never finished.</summary>
    [Fact]
    public void A_bound_below_one_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MarketArchive(new LocalObjectStore(_temp.Root), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MarketArchive(new LocalObjectStore(_temp.Root), -1));
    }

    /// <summary>The default is the documented constant, so the public constructors are not quietly different.</summary>
    [Fact]
    public async Task The_public_constructor_uses_the_documented_bound()
    {
        Assert.Equal(100_000, MarketArchive.MaxRowsPerFile);

        // Far below the real bound, so the default must produce exactly one file.
        await new MarketArchive(_temp.Root).WriteQuoteTicksAsync(Quotes(25));
        Assert.Single(FilesOf("quotes", Sample.Btc.Value));
    }
}
