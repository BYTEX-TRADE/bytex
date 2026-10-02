using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Primitives;
using Bytex.Data.Tests.Support;

namespace Bytex.Data.Tests;

// Range queries feed the backtester. The contract exercised here: data is keyed by instrument id / bar type,
// filtered on CreatedTime with BOTH bounds inclusive, merged across all files of the key and returned in CreatedTime order.
// (docs/concepts/data.md shows start/end but does not spell out inclusiveness; a backtest configured
// "from 00:00 to 00:59" must see both the 00:00 and the 00:59 bar, which is what inclusive bounds give.)
public sealed class CatalogQueryTests : IDisposable
{
    private const long Minute = 60_000_000_000L;

    private readonly TempDirectory _temp = new();
    private readonly MarketArchive _catalog;

    public CatalogQueryTests()
    {
        _catalog = new MarketArchive(_temp.Root);
    }

    public void Dispose() => _temp.Dispose();

    private static Bar[] TenBars() => Enumerable.Range(0, 10).Select(i => Sample.Bar(i * Minute, 42000m + i)).ToArray();

    [Theory]
    [InlineData(2, 5, new[] { 2, 3, 4, 5 })] // both bounds hit a bar exactly: both are included
    [InlineData(0, 0, new[] { 0 })]
    [InlineData(9, 9, new[] { 9 })]
    [InlineData(0, 9, new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 })]
    public async Task Both_bounds_are_inclusive(int startMinute, int endMinute, int[] expectedMinutes)
    {
        await _catalog.WriteBarsAsync(TenBars());

        IReadOnlyList<Bar> bars = await _catalog.BarsAsync(Sample.BtcMinute, Sample.At(startMinute * Minute), Sample.At(endMinute * Minute));

        Assert.Equal(expectedMinutes.Select(m => Sample.At(m * Minute)), bars.Select(b => b.CreatedTime));
    }

    [Fact]
    public async Task One_nanosecond_inside_the_bounds_excludes_the_boundary_bars()
    {
        await _catalog.WriteBarsAsync(TenBars());

        IReadOnlyList<Bar> bars = await _catalog.BarsAsync(Sample.BtcMinute, Sample.At(2 * Minute + 1), Sample.At(5 * Minute - 1));

        Assert.Equal(new[] { Sample.At(3 * Minute), Sample.At(4 * Minute) }, bars.Select(b => b.CreatedTime));
    }

    [Fact]
    public async Task An_open_start_or_an_open_end_is_unbounded_on_that_side()
    {
        await _catalog.WriteBarsAsync(TenBars());

        IReadOnlyList<Bar> untilThree = await _catalog.BarsAsync(Sample.BtcMinute, end: Sample.At(3 * Minute));
        IReadOnlyList<Bar> fromSeven = await _catalog.BarsAsync(Sample.BtcMinute, start: Sample.At(7 * Minute));

        Assert.Equal(4, untilThree.Count);
        Assert.Equal(Sample.At(0), untilThree[0].CreatedTime);
        Assert.Equal(3, fromSeven.Count);
        Assert.Equal(Sample.At(9 * Minute), fromSeven[^1].CreatedTime);
    }

    [Theory]
    [InlineData(-100, -1)] // entirely before the data
    [InlineData(10, 20)] // entirely after the data
    [InlineData(5, 4)] // start after end
    public async Task A_range_without_data_gives_an_empty_list(int startMinute, int endMinute)
    {
        await _catalog.WriteBarsAsync(TenBars());

        Assert.Empty(await _catalog.BarsAsync(Sample.BtcMinute, Sample.At(startMinute * Minute), Sample.At(endMinute * Minute)));
    }

    [Fact]
    public async Task A_gap_between_two_bars_gives_an_empty_list()
    {
        await _catalog.WriteBarsAsync(TenBars());

        Assert.Empty(await _catalog.BarsAsync(Sample.BtcMinute, Sample.At(4 * Minute + 1), Sample.At(5 * Minute - 1)));
    }

    [Fact]
    public async Task The_filter_uses_CreatedTime_not_EventTime()
    {
        QuoteTick late = Sample.Quote(0) with { EventTime = new UnixNanos(100), CreatedTime = new UnixNanos(5_000) };
        await _catalog.WriteQuoteTicksAsync([late]);

        Assert.Empty(await _catalog.QuoteTicksAsync(Sample.Btc, new UnixNanos(0), new UnixNanos(1_000)));
        Assert.Single(await _catalog.QuoteTicksAsync(Sample.Btc, new UnixNanos(4_000), new UnixNanos(6_000)));
    }

    [Fact]
    public async Task An_unknown_key_gives_an_empty_list_for_every_data_kind()
    {
        await _catalog.WriteBarsAsync(TenBars());
        MarketKey unknown = MarketKey.Parse("bx-market:v2/SIM/NOPE");

        Assert.Empty(await _catalog.QuoteTicksAsync(unknown));
        Assert.Empty(await _catalog.TradeTicksAsync(unknown));
        Assert.Empty(await _catalog.OrderBookDeltasAsync(unknown));
        Assert.Empty(await _catalog.BarsAsync(Sample.BtcHour));
    }

    [Fact]
    public async Task Different_instruments_and_bar_types_never_mix()
    {
        await _catalog.WriteBarsAsync([Sample.Bar(0, 1m), Sample.Bar(0, 2m, Sample.BtcHour)]);
        await _catalog.WriteTradeTicksAsync([Sample.Trade(0, "btc"), Sample.Trade(0, "eth", id: Sample.Eth)]);

        Assert.Equal(1m, Assert.Single(await _catalog.BarsAsync(Sample.BtcMinute)).Close.Value);
        Assert.Equal(2m, Assert.Single(await _catalog.BarsAsync(Sample.BtcHour)).Close.Value);
        Assert.Equal("btc", Assert.Single(await _catalog.TradeTicksAsync(Sample.Btc)).TradeId.Value);
        Assert.Equal("eth", Assert.Single(await _catalog.TradeTicksAsync(Sample.Eth)).TradeId.Value);
    }

    [Fact]
    public async Task Unsorted_input_is_returned_in_time_order()
    {
        Bar[] shuffled = [Sample.Bar(3 * Minute), Sample.Bar(0), Sample.Bar(2 * Minute), Sample.Bar(Minute)];

        await _catalog.WriteBarsAsync(shuffled);

        IReadOnlyList<Bar> bars = await _catalog.BarsAsync(Sample.BtcMinute);
        Assert.Equal(new[] { 0L, Minute, 2 * Minute, 3 * Minute }.Select(Sample.At), bars.Select(b => b.CreatedTime));
    }

    [Fact]
    public async Task Several_writes_become_several_files_that_are_merged_in_time_order()
    {
        // Written newest first, and the third write interleaves with the first two.
        await _catalog.WriteBarsAsync([Sample.Bar(6 * Minute), Sample.Bar(8 * Minute)]);
        await _catalog.WriteBarsAsync([Sample.Bar(0), Sample.Bar(2 * Minute)]);
        await _catalog.WriteBarsAsync([Sample.Bar(Minute), Sample.Bar(7 * Minute)]);

        IReadOnlyList<Bar> all = await _catalog.BarsAsync(Sample.BtcMinute);
        IReadOnlyList<Bar> middle = await _catalog.BarsAsync(Sample.BtcMinute, Sample.At(Minute), Sample.At(7 * Minute));

        Assert.Equal(3, Assert.Single(_catalog.Entries()).FileCount);
        Assert.Equal(new[] { 0L, Minute, 2 * Minute, 6 * Minute, 7 * Minute, 8 * Minute }.Select(Sample.At), all.Select(b => b.CreatedTime));
        Assert.Equal(new[] { Minute, 2 * Minute, 6 * Minute, 7 * Minute }.Select(Sample.At), middle.Select(b => b.CreatedTime));
    }

    [Fact]
    public async Task Writing_the_same_range_twice_keeps_both_files_and_does_not_deduplicate()
    {
        // The catalog is append-only: a second import of the same range is stored next to the first
        // in a separate opaque segment and a read returns every row of both. Callers must not import twice.
        Bar[] bars = [Sample.Bar(0), Sample.Bar(Minute)];

        await _catalog.WriteBarsAsync(bars);
        await _catalog.WriteBarsAsync(bars);

        IReadOnlyList<Bar> restored = await _catalog.BarsAsync(Sample.BtcMinute);
        Assert.Equal(2, Assert.Single(_catalog.Entries()).FileCount);
        Assert.Equal(new[] { bars[0], bars[0], bars[1], bars[1] }, restored);
    }

    [Fact]
    public async Task Segments_whose_manifest_range_lies_outside_the_query_are_not_opened()
    {
        // A corrupt committed segment proves pruning: only a query overlapping its manifest range touches it.
        await _catalog.WriteBarsAsync([Sample.Bar(0)]);
        await _catalog.WriteBarsAsync([Sample.Bar(0) with { CreatedTime = new UnixNanos(10) }, Sample.Bar(0) with { CreatedTime = new UnixNanos(20) }]);
        ArchiveSegment corrupt = _catalog.Segments("bars", Sample.BtcMinute.ToString()).Single(s => s.Start == 10);
        File.WriteAllText(Path.Combine(_catalog.RootPath, corrupt.ObjectKey), "not parquet");

        IReadOnlyList<Bar> bars = await _catalog.BarsAsync(Sample.BtcMinute, Sample.At(-1), Sample.At(1));

        Assert.Single(bars);
        await Assert.ThrowsAnyAsync<Exception>(() => _catalog.BarsAsync(Sample.BtcMinute, new UnixNanos(15), Sample.At(1)));
    }

    [Fact]
    public async Task Records_sharing_a_timestamp_keep_their_written_order()
    {
        // A snapshot: one CLEAR followed by 199 ADDs, all stamped with the same instant.
        List<OrderBookDelta> snapshot = [OrderBookDelta.Clear(Sample.Btc, 0, Sample.At(0), Sample.At(0))];
        for (ulong i = 1; i < 200; i++)
        {
            snapshot.Add(Sample.Delta(0, sequence: i, orderId: i) with { EventTime = Sample.At(0) });
        }

        await _catalog.WriteOrderBookDeltasAsync(snapshot);

        IReadOnlyList<OrderBookDelta> restored = await _catalog.OrderBookDeltasAsync(Sample.Btc);
        Assert.Equal(snapshot.Select(d => d.Sequence), restored.Select(d => d.Sequence));
        Assert.Equal(BookAction.Clear, restored[0].Action);
    }

    [Fact]
    public async Task A_cancelled_token_stops_a_read()
    {
        await _catalog.WriteBarsAsync(TenBars());
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _catalog.BarsAsync(Sample.BtcMinute, ct: cts.Token));
    }
}
