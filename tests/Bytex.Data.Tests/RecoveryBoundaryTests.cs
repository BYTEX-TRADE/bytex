using System.Text.Json.Nodes;
using Bytex.Data.Tests.Support;
using Parquet.Serialization;

namespace Bytex.Data.Tests;

public sealed class RecoveryBoundaryTests : IDisposable
{
    private readonly TempDirectory _source = new();
    private readonly TempDirectory _target = new();
    public void Dispose() { _source.Dispose(); _target.Dispose(); }
    private IObjectStore Store(bool memory, bool source = false) => memory ? new MemoryObjectStore() : new LocalObjectStore(source ? _source.Root : _target.Root);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_write_callback_never_publishes_partial_objects(bool memory)
    {
        IObjectStore store = Store(memory);
        await store.WriteTextAsync("sentinel", "committed");
        await Assert.ThrowsAsync<IOException>(() => store.WriteAsync("sentinel", async stream =>
        {
            await stream.WriteAsync(new byte[] { 1, 2, 3 });
            throw new IOException("Interrupted callback.");
        }));
        Assert.Equal("committed", store.ReadText("sentinel"));
        Assert.Single(store.List(string.Empty));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failure_in_second_chunk_keeps_entire_new_batch_invisible(bool memory)
    {
        IObjectStore store = Store(memory);
        await new MarketArchive(store).WriteQuoteTicksAsync([Sample.Quote(-1)]);
        var rows = Enumerable.Range(0, MarketArchive.MaxRowsPerFile + 1).Select(i => Sample.Quote(i)).ToArray();
        BoundaryFaultStore fault = new(store, key => key.Contains("/segments/", StringComparison.Ordinal), occurrence: 2);
        await Assert.ThrowsAsync<IOException>(() => new MarketArchive(fault).WriteQuoteTicksAsync(rows));
        MarketArchive restarted = new(store);
        Assert.Equal([Sample.Quote(-1)], await restarted.QuoteTicksAsync(Sample.Btc));
        Assert.Equal("uncommitted", Assert.Single(restarted.RecoveryObjects()).State);
        await restarted.WriteQuoteTicksAsync(rows);
        Assert.Equal(MarketArchive.MaxRowsPerFile + 2, (await new MarketArchive(store).QuoteTicksAsync(Sample.Btc)).Count);
        Assert.Empty(restarted.Check());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Migration_resumes_before_or_after_manifest_publication_without_duplicates(bool memory, bool after)
    {
        IObjectStore source = Store(memory, source: true), destination = Store(memory);
        await Seed(source);
        BoundaryFaultStore fault = new(destination, key => key.Contains("/commits/", StringComparison.Ordinal), occurrence: 2, afterPublication: after);
        await Assert.ThrowsAsync<IOException>(() => LegacyArchiveMigration.ConvertAsync(source, fault));
        Assert.False(destination.Exists("migration/completed.json"));
        Assert.Equal(after ? 2 : 1, (await new MarketArchive(destination).QuoteTicksAsync(Sample.Btc)).Count);
        Assert.Equal(2, await LegacyArchiveMigration.ConvertAsync(source, destination));
        Assert.Equal(2, await LegacyArchiveMigration.ConvertAsync(source, destination));
        MarketArchive restarted = new(destination);
        Assert.Equal(new long[] { 10, 20 }, (await restarted.QuoteTicksAsync(Sample.Btc)).Select(q => q.CreatedTime.Value));
        Assert.Equal(2, restarted.Segments("quotes", Sample.Btc.Value).Count);
        Assert.Empty(restarted.Check());
        Assert.True(destination.Exists("migration/completed.json"));
        Assert.Equal(2, source.List(string.Empty).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_completion_marker_can_be_retried_without_reimport(bool memory)
    {
        IObjectStore source = Store(memory, source: true), destination = Store(memory);
        await Seed(source);
        await Assert.ThrowsAsync<IOException>(() => LegacyArchiveMigration.ConvertAsync(source,
            new BoundaryFaultStore(destination, key => key == "migration/completed.json")));
        var before = new MarketArchive(destination).Segments("quotes", Sample.Btc.Value).Select(s => s.ObjectKey).ToArray();
        await LegacyArchiveMigration.ConvertAsync(source, destination);
        Assert.Equal(before, new MarketArchive(destination).Segments("quotes", Sample.Btc.Value).Select(s => s.ObjectKey));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_object_store_append_and_consolidation_preserve_rows(bool memory)
    {
        IObjectStore store = Store(memory);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => new MarketArchive(store).WriteQuoteTicksAsync([Sample.Quote(i)])));
        Assert.Equal(8, (await new MarketArchive(store).QuoteTicksAsync(Sample.Btc)).Count);
        Assert.Equal(8, await new MarketArchive(store).ConsolidateAsync("quotes", Sample.Btc.Value));
        MarketArchive restarted = new(store);
        Assert.Equal(Enumerable.Range(0, 8).Select(i => Sample.Quote(i)), await restarted.QuoteTicksAsync(Sample.Btc));
        Assert.Equal(8, restarted.RecoveryObjects().Count(o => o.State == "superseded"));
        Assert.Empty(restarted.Check());
    }

    [Theory]
    [InlineData("Segments")]
    [InlineData("ObjectKey")]
    [InlineData("Replaces")]
    public async Task Null_manifest_members_are_reported_as_invalid_data(string member)
    {
        IObjectStore store = Store(memory: true);
        MarketArchive archive = new(store);
        await archive.WriteQuoteTicksAsync([Sample.Quote(0)]);
        string key = Assert.Single(store.List(string.Empty), o => o.Key.Contains("/commits/", StringComparison.Ordinal)).Key;
        JsonObject manifest = JsonNode.Parse(store.ReadText(key))!.AsObject();
        if (member == "Segments") { manifest[member]![0] = null; }
        else if (member == "Replaces") { manifest[member] = new JsonArray((JsonNode?)null); }
        else { manifest["Segments"]![0]![member] = null; }
        await store.WriteTextAsync(key, manifest.ToJsonString());
        await Assert.ThrowsAsync<InvalidDataException>(() => archive.QuoteTicksAsync(Sample.Btc));
        Assert.Single(archive.Check());
    }

    private static async Task Seed(IObjectStore source)
    {
        foreach (long t in new long[] { 10, 20 })
        {
            LegacyArchiveMigrationTests.LegacyQuote row = new() { TsEvent = t - 1, TsInit = t, Bid = 1.2345m, Ask = 1.2346m,
                BidSize = 0.123m, AskSize = 0.456m, PricePrecision = 4, SizePrecision = 3 };
            await source.WriteAsync($"quotes/BTCUSDT.BINANCE/{t}-{t}.parquet", stream => ParquetSerializer.SerializeAsync(new[] { row }, stream));
        }
    }
}
