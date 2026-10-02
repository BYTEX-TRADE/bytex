using Bytex.Data.Tests.Support;
using Parquet.Serialization;

namespace Bytex.Data.Tests;

public sealed class LegacyArchiveMigrationTests : IDisposable
{
    private readonly TempDirectory _source = new();
    private readonly TempDirectory _target = new();
    public void Dispose() { _source.Dispose(); _target.Dispose(); }

    [Fact]
    public async Task Legacy_timestamps_decimals_and_written_order_survive_and_resume_does_not_duplicate()
    {
        LocalObjectStore source = new(_source.Root);
        const string file = "quotes/BTCUSDT.BINANCE/0-100.parquet";
        LegacyQuote[] rows = [new() { TsEvent = -11, TsInit = -10, Bid = 1.123456789m, Ask = 2.987654321m, BidSize = 1.23456m, AskSize = 9.87654m, PricePrecision = 9, SizePrecision = 5 },
            new() { TsEvent = -9, TsInit = -10, Bid = 3m, Ask = 4m, BidSize = 1m, AskSize = 2m, PricePrecision = 9, SizePrecision = 5 }];
        await source.WriteAsync(file, stream => ParquetSerializer.SerializeAsync(rows, stream));
        byte[] before = await File.ReadAllBytesAsync(Path.Combine(_source.Root, file));
        LocalObjectStore target = new(_target.Root);
        Assert.Equal(1, await LegacyArchiveMigration.ConvertAsync(source, target));
        Assert.Equal(1, await LegacyArchiveMigration.ConvertAsync(source, target));
        MarketArchive archive = new(target);
        var restored = await archive.QuoteTicksAsync(Sample.Btc);
        Assert.Equal(2, restored.Count);
        Assert.Equal(rows.Select(r => r.Bid), restored.Select(r => r.Bid.Value));
        Assert.Equal(rows.Select(r => r.AskSize), restored.Select(r => r.AskSize.Value));
        Assert.Equal(rows.Select(r => r.TsEvent), restored.Select(r => r.EventTime.Value));
        Assert.Equal(rows.Select(r => r.TsInit), restored.Select(r => r.CreatedTime.Value));
        Assert.Single(archive.Segments("quotes", Sample.Btc.Value));
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(_source.Root, file)));
        Assert.Empty(archive.Check());
    }

    [Fact]
    public async Task A_changed_source_is_refused_on_resume()
    {
        LocalObjectStore source = new(_source.Root);
        const string file = "quotes/BTCUSDT.BINANCE/1-2.parquet";
        await source.WriteAsync(file, stream => ParquetSerializer.SerializeAsync(new[] { new LegacyQuote() }, stream));
        LocalObjectStore target = new(_target.Root);
        await LegacyArchiveMigration.ConvertAsync(source, target);
        await source.WriteTextAsync(file, "changed");
        await Assert.ThrowsAsync<InvalidDataException>(() => LegacyArchiveMigration.ConvertAsync(source, target));
    }

    [Fact]
    public async Task Nested_or_same_destinations_are_refused_before_writing()
    {
        LocalObjectStore source = new(_source.Root);
        await Assert.ThrowsAsync<ArgumentException>(() => LegacyArchiveMigration.ConvertAsync(source, source));
        await Assert.ThrowsAsync<ArgumentException>(() => LegacyArchiveMigration.ConvertAsync(source, new LocalObjectStore(Path.Combine(_source.Root, "nested"))));
        Assert.Empty(source.List(string.Empty));
    }

    [Fact]
    public async Task Unknown_source_objects_are_refused_not_dropped()
    {
        LocalObjectStore source = new(_source.Root);
        await source.WriteTextAsync("custom.json", "{}");
        await Assert.ThrowsAsync<InvalidDataException>(() => LegacyArchiveMigration.ConvertAsync(source, new LocalObjectStore(_target.Root)));
    }

    public sealed class LegacyQuote
    {
        public long TsEvent { get; set; }
        public long TsInit { get; set; }
        public decimal Bid { get; set; }
        public decimal Ask { get; set; }
        public decimal BidSize { get; set; }
        public decimal AskSize { get; set; }
        public byte PricePrecision { get; set; }
        public byte SizePrecision { get; set; }
    }
}
