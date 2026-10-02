using Bytex.Core.Model.Primitives;
using Bytex.Data.Tests.Support;

namespace Bytex.Data.Tests;

public sealed class ArchiveRecoveryTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task An_interrupted_batch_is_not_visible_and_retry_loses_no_committed_rows()
    {
        LocalObjectStore store = new(_temp.Root);
        MarketArchive archive = new(store);
        await archive.WriteQuoteTicksAsync([Sample.Quote(0)]);
        FaultStore fault = new(store) { FailCommit = true };
        await Assert.ThrowsAsync<IOException>(() => new MarketArchive(fault).WriteQuoteTicksAsync([Sample.Quote(1)]));
        Assert.Equal([Sample.Quote(0)], await new MarketArchive(store).QuoteTicksAsync(Sample.Btc));
        Assert.Equal("uncommitted", Assert.Single(archive.RecoveryObjects()).State);
        await archive.WriteQuoteTicksAsync([Sample.Quote(1)]);
        Assert.Equal([Sample.Quote(0), Sample.Quote(1)], await archive.QuoteTicksAsync(Sample.Btc));
    }

    [Fact]
    public async Task Interrupted_consolidation_keeps_originals_and_committed_retry_hides_them()
    {
        LocalObjectStore store = new(_temp.Root);
        MarketArchive archive = new(store);
        await archive.WriteQuoteTicksAsync([Sample.Quote(0)]);
        await archive.WriteQuoteTicksAsync([Sample.Quote(1)]);
        string[] original = [.. archive.Segments("quotes", Sample.Btc.Value).Select(s => s.ObjectKey)];
        await Assert.ThrowsAsync<IOException>(() => new MarketArchive(new FaultStore(store) { FailCommit = true })
            .ConsolidateAsync("quotes", Sample.Btc.Value));
        Assert.Equal(2, (await archive.QuoteTicksAsync(Sample.Btc)).Count);
        Assert.All(original, key => Assert.True(store.Exists(key)));
        Assert.Equal(2, await archive.ConsolidateAsync("quotes", Sample.Btc.Value));
        Assert.Single(archive.Segments("quotes", Sample.Btc.Value));
        Assert.All(original, key => Assert.True(store.Exists(key)));
        Assert.Equal(2, archive.RecoveryObjects().Count(o => o.State == "superseded"));
        Assert.Equal(2, (await new MarketArchive(store).QuoteTicksAsync(Sample.Btc)).Count);
    }

    [Fact]
    public async Task Negative_timestamps_and_equal_timestamp_order_survive_consolidation()
    {
        MarketArchive archive = new(_temp.Root);
        var first = Sample.Quote(0) with { CreatedTime = new UnixNanos(-10), EventTime = new UnixNanos(-11) };
        var second = Sample.Quote(0, bid: 2m) with { CreatedTime = new UnixNanos(-10), EventTime = new UnixNanos(-11) };
        await archive.WriteQuoteTicksAsync([first, second]);
        await archive.WriteQuoteTicksAsync([Sample.Quote(1)]);
        await archive.ConsolidateAsync("quotes", Sample.Btc.Value);
        Assert.Equal([first, second], await archive.QuoteTicksAsync(Sample.Btc, new UnixNanos(-10), new UnixNanos(-10)));
    }

    [Fact]
    public async Task Corrupted_committed_segment_is_refused_and_reported()
    {
        MarketArchive archive = new(_temp.Root);
        await archive.WriteQuoteTicksAsync([Sample.Quote(0)]);
        ArchiveSegment segment = Assert.Single(archive.Segments("quotes", Sample.Btc.Value));
        await archive.Store.WriteTextAsync(segment.ObjectKey, "corrupt");
        await Assert.ThrowsAsync<InvalidDataException>(() => archive.QuoteTicksAsync(Sample.Btc));
        Assert.Contains("integrity", Assert.Single(archive.Check()).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_appenders_do_not_overwrite_each_others_segments_or_manifests()
    {
        LocalObjectStore store = new(_temp.Root);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => new MarketArchive(store).WriteQuoteTicksAsync([Sample.Quote(i)])));
        MarketArchive archive = new(store);
        Assert.Equal(8, (await archive.QuoteTicksAsync(Sample.Btc)).Count);
        Assert.Equal(8, archive.Segments("quotes", Sample.Btc.Value).Count);
        Assert.Empty(archive.Check());
    }

    private sealed class FaultStore(IObjectStore inner) : IObjectStore
    {
        public bool FailCommit { get; init; }
        public string Location => inner.Location;
        public IReadOnlyList<StoredObject> List(string prefix) => inner.List(prefix);
        public bool Exists(string key) => inner.Exists(key);
        public void Delete(string key) => inner.Delete(key);
        public void Move(string fromKey, string toKey) => inner.Move(fromKey, toKey);
        public string ReadText(string key) => inner.ReadText(key);
        public Task WriteTextAsync(string key, string text, CancellationToken ct = default) => inner.WriteTextAsync(key, text, ct);
        public Task<Stream> OpenReadAsync(string key, CancellationToken ct = default) => inner.OpenReadAsync(key, ct);
        public Task WriteAsync(string key, Func<Stream, Task> write, CancellationToken ct = default) =>
            FailCommit && key.Contains("/commits/", StringComparison.Ordinal)
                ? Task.FromException(new IOException("Injected failure before manifest publication.")) : inner.WriteAsync(key, write, ct);
    }
}
