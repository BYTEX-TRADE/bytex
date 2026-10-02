using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Primitives;
using Bytex.Data;
using Bytex.Data.Tests.Support;

namespace Bytex.Data.Tests;

// Why (R9.6): a streaming read holds one file at a time and therefore refuses a catalog whose files overlap, which
// is the honest answer and on its own leaves somebody stuck - the data is readable the slow way and not the fast
// way, and nothing says which keys are the problem or what to do.
//
// So the catalog can be asked what is wrong with it, and can be put right. Consolidating answers everything the
// check reports at once: one file cannot overlap itself, its name is a time range by construction, and the duplicate
// records a re-downloaded day leaves behind go with it.
public sealed class CatalogMaintenanceTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private static readonly MarketKey _instrument = MarketKey.Parse("bx-market:v2/BINANCE/BTCUSDT");

    public void Dispose() => _temp.Dispose();

    private MarketArchive Catalog() => new(_temp.Root);

    private static QuoteTick Quote(long nanos, decimal bid = 100m) =>
        new(_instrument, new Price(bid, 2), new Price(bid + 1m, 2), new Quantity(1m, 3), new Quantity(1m, 3), new UnixNanos(nanos), new UnixNanos(nanos));

    [Fact]
    public async Task A_catalog_whose_files_do_not_overlap_has_nothing_to_report()
    {
        // The rule has to cost nothing in the ordinary case, or nobody will run it twice.
        MarketArchive catalog = Catalog();
        await catalog.WriteQuoteTicksAsync([Quote(1_000), Quote(2_000)]);
        await catalog.WriteQuoteTicksAsync([Quote(3_000), Quote(4_000)]);

        Assert.Empty(catalog.Check());
    }

    [Fact]
    public async Task Overlapping_files_are_named_with_what_to_do_about_them()
    {
        MarketArchive catalog = Catalog();
        await catalog.WriteQuoteTicksAsync([Quote(1_000), Quote(5_000)]);
        await catalog.WriteQuoteTicksAsync([Quote(2_000), Quote(3_000)]);

        CatalogProblem problem = Assert.Single(catalog.Check());

        Assert.Equal("quotes", problem.Kind);
        Assert.Equal(_instrument.Value, problem.Key);
        Assert.Contains("overlap", problem.Detail, StringComparison.Ordinal);
        Assert.Contains("consolidate", problem.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Consolidating_makes_an_overlapping_key_readable_by_a_stream()
    {
        // The point of the whole exercise: before, the fast read refuses; after, it works and gives the same records
        // the slow one always did.
        MarketArchive catalog = Catalog();
        await catalog.WriteQuoteTicksAsync([Quote(1_000), Quote(5_000)]);
        await catalog.WriteQuoteTicksAsync([Quote(2_000), Quote(3_000)]);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (QuoteTick _ in catalog.StreamQuoteTicksAsync(_instrument))
            {
            }
        });

        int rows = await catalog.ConsolidateAsync("quotes", _instrument.Value);

        Assert.Equal(4, rows);
        Assert.Empty(catalog.Check());

        List<UnixNanos> streamed = new();
        await foreach (QuoteTick quote in catalog.StreamQuoteTicksAsync(_instrument))
        {
            streamed.Add(quote.CreatedTime);
        }

        Assert.Equal([new UnixNanos(1_000), new UnixNanos(2_000), new UnixNanos(3_000), new UnixNanos(5_000)], streamed);
    }

    [Fact]
    public async Task A_day_downloaded_twice_stops_being_two_copies()
    {
        // What a re-download actually leaves: the same records written again. They are the same record when every
        // field matches, so one copy survives.
        MarketArchive catalog = Catalog();
        await catalog.WriteQuoteTicksAsync([Quote(1_000), Quote(2_000)]);
        await catalog.WriteQuoteTicksAsync([Quote(1_000), Quote(2_000)]);

        int rows = await catalog.ConsolidateAsync("quotes", _instrument.Value);

        Assert.Equal(2, rows);
        Assert.Equal(2, (await catalog.QuoteTicksAsync(_instrument)).Count);
    }

    [Fact]
    public async Task Two_different_records_sharing_a_moment_are_both_kept()
    {
        // The line that matters when de-duplicating market data: two quotes can genuinely share a microsecond, and
        // dropping one because of its timestamp would be losing data rather than tidying it.
        MarketArchive catalog = Catalog();
        await catalog.WriteQuoteTicksAsync([Quote(1_000, 100m)]);
        await catalog.WriteQuoteTicksAsync([Quote(1_000, 101m)]);

        int rows = await catalog.ConsolidateAsync("quotes", _instrument.Value);

        Assert.Equal(2, rows);
    }

    [Fact]
    public async Task Consolidating_a_key_with_one_file_leaves_it_alone()
    {
        MarketArchive catalog = Catalog();
        await catalog.WriteQuoteTicksAsync([Quote(1_000), Quote(2_000)]);

        // Nothing to do, said as zero rather than as a count of what it did not rewrite.
        Assert.Equal(0, await catalog.ConsolidateAsync("quotes", _instrument.Value));
        Assert.Equal(2, (await catalog.QuoteTicksAsync(_instrument)).Count);
    }

    [Fact]
    public async Task A_kind_this_catalog_does_not_hold_is_refused_by_name()
    {
        ArgumentException refused = await Assert.ThrowsAsync<ArgumentException>(
            () => Catalog().ConsolidateAsync("candles", _instrument.Value));

        Assert.Contains("quotes", refused.Message, StringComparison.Ordinal);
    }
}
