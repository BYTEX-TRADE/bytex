using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Data.Tests.Support;

namespace Bytex.Data.Tests;

// `bytex catalog list` prints Instruments() and Entries(); the per-kind key listings let a caller discover
// what a catalog holds without knowing the directory layout.
public sealed class CatalogListingTests : IDisposable
{
    private const long Minute = 60_000_000_000L;

    private readonly TempDirectory _temp = new();
    private readonly ParquetDataCatalog _catalog;

    public CatalogListingTests()
    {
        _catalog = new ParquetDataCatalog(_temp.Root);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void An_empty_catalog_lists_nothing()
    {
        Assert.Empty(_catalog.Entries());
        Assert.Empty(_catalog.QuoteTickInstruments());
        Assert.Empty(_catalog.TradeTickInstruments());
        Assert.Empty(_catalog.OrderBookDeltaInstruments());
        Assert.Empty(_catalog.BarTypes());
    }

    [Fact]
    public async Task Keys_are_listed_per_data_kind()
    {
        await _catalog.WriteQuoteTicksAsync([Sample.Quote(0), Sample.Quote(0, id: Sample.Eth)]);
        await _catalog.WriteTradeTicksAsync([Sample.Trade(0, id: Sample.Eth)]);
        await _catalog.WriteOrderBookDeltasAsync([Sample.Delta(0, 1)]);
        await _catalog.WriteBarsAsync([Sample.Bar(0), Sample.Bar(0, barType: Sample.BtcHour)]);

        Assert.Equal(new[] { Sample.Btc, Sample.Eth }, _catalog.QuoteTickInstruments().Order());
        Assert.Equal(new[] { Sample.Eth }, _catalog.TradeTickInstruments());
        Assert.Equal(new[] { Sample.Btc }, _catalog.OrderBookDeltaInstruments());
        Assert.Equal(
            new[] { "BTCUSDT.BINANCE-1-HOUR-LAST-EXTERNAL", "BTCUSDT.BINANCE-1-MINUTE-LAST-EXTERNAL" },
            _catalog.BarTypes().Select(b => b.ToString()).Order(StringComparer.Ordinal));
        Assert.Contains(Sample.BtcHour, _catalog.BarTypes());
    }

    [Fact]
    public async Task An_entry_reports_kind_key_file_count_time_range_and_size()
    {
        await _catalog.WriteBarsAsync([Sample.Bar(2 * Minute), Sample.Bar(5 * Minute)]);
        await _catalog.WriteBarsAsync([Sample.Bar(0), Sample.Bar(Minute)]);

        CatalogEntry entry = Assert.Single(_catalog.Entries());

        Assert.Equal("bars", entry.Kind);
        Assert.Equal(Sample.BtcMinute.ToString(), entry.Key);
        Assert.Equal(2, entry.FileCount);
        Assert.Equal(Sample.At(0), entry.Start);
        Assert.Equal(Sample.At(5 * Minute), entry.End);

        long onDisk = Directory.GetFiles(_catalog.RootPath, "*.parquet", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        Assert.True(onDisk > 0);
        Assert.Equal(onDisk, entry.SizeBytes);
    }

    [Fact]
    public async Task There_is_one_entry_per_kind_and_key()
    {
        await _catalog.WriteAsync(
        [
            Sample.Quote(0),
            Sample.Quote(1, id: Sample.Eth),
            Sample.Trade(2),
            Sample.Delta(3, 1),
            Sample.Bar(4),
            new FundingRateUpdate(Sample.Btc, 0.0001m, null, Sample.At(5), Sample.At(5)),
        ]);

        IEnumerable<string> entries = _catalog.Entries().Select(e => $"{e.Kind}:{e.Key}").Order(StringComparer.Ordinal);

        // Every kind the catalog can hold is a kind it can list. One it stores and cannot list is worse than one it
        // cannot store: a screen built on this shows nothing, and a coverage check before a run cannot warn about
        // data it cannot see.
        Assert.Equal(
            new[]
            {
                "bars:BTCUSDT.BINANCE-1-MINUTE-LAST-EXTERNAL",
                "book_deltas:BTCUSDT.BINANCE",
                "funding:BTCUSDT.BINANCE",
                "quotes:BTCUSDT.BINANCE",
                "quotes:ETHUSDT.BINANCE",
                "trades:BTCUSDT.BINANCE",
            },
            entries);
    }

    [Fact]
    public async Task The_range_of_an_entry_is_the_TsInit_range_of_its_data()
    {
        QuoteTick first = Sample.Quote(0) with { TsEvent = Sample.At(-999) };
        await _catalog.WriteQuoteTicksAsync([first, Sample.Quote(77)]);

        CatalogEntry entry = Assert.Single(_catalog.Entries());

        Assert.Equal(Sample.At(0), entry.Start);
        Assert.Equal(Sample.At(77), entry.End);
    }

    [Fact]
    public async Task A_listing_made_by_a_second_catalog_instance_sees_the_same_data()
    {
        await _catalog.WriteTradeTicksAsync([Sample.Trade(0)]);

        ParquetDataCatalog reopened = new(_temp.Root);

        Assert.Equal(new[] { Sample.Btc }, reopened.TradeTickInstruments());
        Assert.Equal("trades", Assert.Single(reopened.Entries()).Kind);
    }

    [Fact]
    public async Task A_symbol_containing_a_slash_is_listed_under_its_real_id()
    {
        InstrumentId eurUsd = InstrumentId.Parse("EUR/USD.SIM");
        await _catalog.WriteQuoteTicksAsync([Sample.Quote(0, bid: 1.08m, id: eurUsd)]);

        Assert.Equal(new[] { eurUsd }, _catalog.QuoteTickInstruments());
        Assert.Equal("EUR/USD.SIM", Assert.Single(_catalog.Entries()).Key);
    }
}
