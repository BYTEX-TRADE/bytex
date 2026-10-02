using System.Text;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Primitives;
using Bytex.Data.Tests.Support;
using Bytex.Persistence.S3;

namespace Bytex.Data.Tests;

// Why (R9.5): the catalog reads and writes through a store, and this is the store that is not a directory. What is
// checked here is everything a dictionary standing in for a bucket would have agreed with and got wrong: that the
// signature a real service checks is the one this sends, that a listing comes back paginated and ordered, that a key
// with a space or a slash in it survives being signed, that a move is a copy and a delete, and - the point of all of it
// - that the SAME catalog, with the same layout and the same guards, works over object storage.
//
// Skipped where there is no service. See S3TestServer for why skipped rather than quietly passed.
[Collection("environment")]
public sealed class S3ObjectStoreTests : IAsyncLifetime
{
    private readonly string _prefix = S3TestServer.Prefix();
    private S3ObjectStore? _store;

    private S3ObjectStore Store => _store ?? throw new InvalidOperationException("no store");

    public async Task InitializeAsync()
    {
        if (!S3TestServer.Available)
        {
            return;
        }

        _store = S3TestServer.Store(_prefix);
        await _store.CreateBucketIfMissingAsync();
    }

    public async Task DisposeAsync()
    {
        if (_store is null)
        {
            return;
        }

        // Leave the bucket as it was found: every object this run wrote is under its own prefix.
        foreach (StoredObject stored in _store.List(string.Empty))
        {
            _store.Delete(stored.Key);
        }

        _store.Dispose();
        await Task.CompletedTask;
    }

    private static async Task<byte[]> BytesOf(Stream stream)
    {
        using MemoryStream copy = new();
        await stream.CopyToAsync(copy);
        return copy.ToArray();
    }

    [S3Fact]
    public async Task An_object_written_comes_back_byte_for_byte()
    {
        byte[] payload = [0x00, 0x7f, 0xff, 0x10, 0x00];

        await Store.WriteAsync("bytes/one.bin", stream => stream.WriteAsync(payload).AsTask());

        Assert.True(Store.Exists("bytes/one.bin"));
        await using Stream read = await Store.OpenReadAsync("bytes/one.bin");

        // Seekable, because Parquet's first move is to the footer at the end of the file.
        Assert.True(read.CanSeek);
        Assert.Equal(payload, await BytesOf(read));
    }

    [S3Fact]
    public async Task A_key_with_characters_that_have_to_be_encoded_is_still_the_key_that_comes_back()
    {
        // The catalog percent-encodes an instrument id into a key, and a signature over a differently encoded path is
        // refused outright - so this is the test that the signer and the URI agree about one string.
        string key = "quotes/EUR%bx-market:v2/SIM/2FUSD/1700000000-1700000001.parquet";

        await Store.WriteAsync(key, stream => stream.WriteAsync(Encoding.UTF8.GetBytes("payload")).AsTask());

        StoredObject stored = Assert.Single(Store.List("quotes"));
        Assert.Equal(key, stored.Key);
        Assert.Equal(7, stored.Size);
        Assert.Equal("payload", Encoding.UTF8.GetString(await BytesOf(await Store.OpenReadAsync(key))));
    }

    [S3Fact]
    public async Task A_listing_is_recursive_ordered_and_empty_for_a_prefix_nothing_is_under()
    {
        await Store.WriteTextAsync("bars/B/2.parquet", "second");
        await Store.WriteTextAsync("bars/A/1.parquet", "first");
        await Store.WriteTextAsync("trades/C/3.parquet", "third");

        Assert.Equal(["bars/A/1.parquet", "bars/B/2.parquet"], Store.List("bars").Select(o => o.Key));
        Assert.Equal(3, Store.List(string.Empty).Count);

        // A catalog that has never held funding rates is not a broken catalog.
        Assert.Empty(Store.List("funding"));
    }

    [S3Fact]
    public async Task A_listing_returns_everything_when_there_is_more_than_one_page_of_it()
    {
        // The service answers a thousand keys at a time and says so; a reader that ignored the continuation token would
        // silently see the first page of a catalog and call it the catalog.
        const int count = 12;
        for (int i = 0; i < count; i++)
        {
            await Store.WriteTextAsync($"many/{i:D4}.txt", i.ToString());
        }

        // Five at a time, so twelve keys really are three pages and a fourth request that ends it.
        S3ObjectStore paged = new(
            new S3ObjectStoreConfig
            {
                Bucket = S3TestServer.Bucket,
                Prefix = _prefix,
                Region = S3TestServer.Region,
                Endpoint = S3TestServer.Endpoint,
                PageSize = 5,
            },
            S3TestServer.Credentials());

        using (paged)
        {
            Assert.Equal(count, paged.List("many").Count);
        }
    }

    [S3Fact]
    public async Task A_move_puts_the_object_in_its_new_place_and_leaves_nothing_in_the_old_one()
    {
        // Object storage has no rename, and consolidation depends on this one: the finished file is put in place only
        // once it is whole.
        await Store.WriteTextAsync("bars/A/1.consolidating", "finished");

        Store.Move("bars/A/1.consolidating", "bars/A/1.parquet");

        Assert.False(Store.Exists("bars/A/1.consolidating"));
        Assert.Equal("finished", Store.ReadText("bars/A/1.parquet"));
    }

    [S3Fact]
    public async Task Deleting_something_that_is_not_there_is_not_an_error()
    {
        await Store.WriteTextAsync("bars/A/1.parquet", "x");
        Store.Delete("bars/A/1.parquet");

        Store.Delete("bars/A/1.parquet");

        Assert.False(Store.Exists("bars/A/1.parquet"));
    }

    [S3Fact]
    public void A_refused_request_says_what_the_service_called_it()
    {
        // The service's own code is the only part of a failure that says what to do about it.
        S3ObjectStore wrong = new(
            new S3ObjectStoreConfig
            {
                Bucket = S3TestServer.Bucket,
                Prefix = _prefix,
                Region = S3TestServer.Region,
                Endpoint = S3TestServer.Endpoint,
            },
            new S3Credentials("wrong-key", "wrong-secret"));

        using (wrong)
        {
            InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => wrong.ReadText("bars/A/1.parquet"));
            Assert.Contains("Object storage refused GET", e.Message, StringComparison.Ordinal);
        }
    }

    // ----- the catalog itself, on object storage -----

    [S3Fact]
    public async Task The_same_catalog_writes_reads_lists_and_consolidates_over_object_storage()
    {
        CandleSeries candleSeries = CandleSeries.Parse("bx-candle:v2/BINANCE/BTCUSDT/minute/1/last/provider");
        MarketArchive catalog = new(Store);

        await catalog.WriteBarsAsync([Bar(candleSeries, 1_000, 100m), Bar(candleSeries, 2_000, 101m)]);
        await catalog.WriteBarsAsync([Bar(candleSeries, 2_000, 101m), Bar(candleSeries, 3_000, 102m)]);

        Assert.Equal($"s3://{S3TestServer.Bucket}/{_prefix}", catalog.RootPath);
        Assert.Equal([candleSeries], catalog.CandleSeriesDefinitions());

        IReadOnlyList<Bar> read = await catalog.BarsAsync(candleSeries);
        Assert.Equal(4, read.Count);
        Assert.Equal([1_000L, 2_000L, 2_000L, 3_000L], read.Select(b => b.CreatedTime.Value));

        CatalogEntry entry = Assert.Single(catalog.Entries());
        Assert.Equal("bars", entry.Kind);
        Assert.Equal(2, entry.FileCount);
        Assert.Equal(1_000, entry.Start!.Value.Value);
        Assert.Equal(3_000, entry.End!.Value.Value);
        Assert.True(entry.SizeBytes > 0, "sizes come from the listing, which is the only place a remote store has them");

        // The two writes overlap, which is what a re-download leaves behind, and it is reported rather than discovered
        // later by a streaming read.
        Assert.NotEmpty(catalog.Check());

        int records = await catalog.ConsolidateAsync("bars", candleSeries.ToString());

        Assert.Equal(3, records);
        Assert.Empty(catalog.Check());
        Assert.Single(catalog.Entries());

        // And the streaming read, which refuses an overlapping catalog, now runs over the whole of it.
        List<Bar> streamed = new();
        await foreach (Bar bar in catalog.StreamBarsAsync(candleSeries))
        {
            streamed.Add(bar);
        }

        Assert.Equal([1_000L, 2_000L, 3_000L], streamed.Select(b => b.CreatedTime.Value));
    }

    [S3Fact]
    public async Task An_instrument_written_to_object_storage_is_read_back_by_the_catalog()
    {
        MarketArchive catalog = new(Store);
        Core.Model.Instruments.Instrument instrument = TestInstruments.BtcUsdt();

        await catalog.WriteInstrumentsAsync([instrument]);

        Assert.Equal(instrument.Id, catalog.Instrument(instrument.Id)!.Id);
        Assert.Single(catalog.Instruments());
        Assert.Null(catalog.Instrument(new MarketKey(new Symbol("NOTHERE"), instrument.Venue)));
    }

    private static Bar Bar(CandleSeries candleSeries, long ts, decimal close) => new(
        candleSeries,
        new Price(close, 2),
        new Price(close, 2),
        new Price(close, 2),
        new Price(close, 2),
        new Quantity(1m, 3),
        new UnixNanos(ts),
        new UnixNanos(ts));
}
