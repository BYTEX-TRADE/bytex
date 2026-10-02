using System.Text;
using Bytex.Persistence.S3;

namespace Bytex.Data.Tests;

// Why: the catalog names where it lives with one string, and everything that names a catalog - a command line, a
// backtest configuration, a strategy document - passes that string through here. What is checked is the reading of it
// (a Windows drive letter is not a scheme), what a local store does with a key, and that a location with a scheme
// nobody registered says what to do rather than being taken for a relative path and quietly making a directory called
// "s3:".
public sealed class ObjectStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bytex-store-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private LocalObjectStore Local() => new(_root);

    // ----- reading a location -----

    [Fact]
    public void A_path_is_a_path_and_a_drive_letter_is_not_a_scheme()
    {
        Assert.Null(ObjectStores.SchemeOf("/var/lib/bytex"));
        Assert.Null(ObjectStores.SchemeOf(@"C:\data\catalog"));
        Assert.Null(ObjectStores.SchemeOf("catalog"));
        Assert.Equal("s3", ObjectStores.SchemeOf("s3://bucket/prefix"));
        Assert.Equal("gs", ObjectStores.SchemeOf("gs://bucket"));
    }

    [Fact]
    public void A_location_with_a_scheme_nobody_registered_says_what_to_register()
    {
        ArgumentException e = Assert.Throws<ArgumentException>(() => ObjectStores.Open("nowhere-in-particular://bucket/prefix"));

        Assert.Contains("Register the backend", e.Message, StringComparison.Ordinal);
        Assert.Contains("S3Location.Register()", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_registered_scheme_is_asked_for_the_store()
    {
        // A scheme of its own, because tests share the registry and share the process.
        string scheme = "test-" + Guid.NewGuid().ToString("N")[..8];
        string? asked = null;
        ObjectStores.Register(scheme, location =>
        {
            asked = location;
            return Local();
        });

        IObjectStore store = ObjectStores.Open($"{scheme}://bucket/prefix");

        Assert.Equal($"{scheme}://bucket/prefix", asked);
        Assert.Equal(_root, store.Location);
        Assert.Contains(scheme, ObjectStores.Registered());
    }

    [Fact]
    public void An_s3_location_is_read_into_a_bucket_a_prefix_and_where_to_reach_it()
    {
        S3Location.Register();

        Assert.Equal("s3://catalogs/eu/bars", ObjectStores.Open("s3://catalogs/eu/bars?endpoint=http://localhost:9000").Location);
        Assert.Equal("s3://catalogs", ObjectStores.Open("s3://catalogs?endpoint=http://localhost:9000").Location);

        ArgumentException e = Assert.Throws<ArgumentException>(() => S3Location.Open("s3://"));
        Assert.Contains("names no bucket", e.Message, StringComparison.Ordinal);
    }

    // ----- what a local store does -----

    [Fact]
    public async Task A_key_is_the_same_key_on_every_platform()
    {
        LocalObjectStore store = Local();

        await store.WriteTextAsync("candles/series-example/segment-example.parquet", "x");

        StoredObject stored = Assert.Single(store.List("candles"));
        Assert.Equal("candles/series-example/segment-example.parquet", stored.Key);
        Assert.True(File.Exists(Path.Combine(_root, "candles", "series-example", "segment-example.parquet")));
    }

    [Fact]
    public async Task A_listing_is_recursive_ordered_and_carries_sizes()
    {
        LocalObjectStore store = Local();
        await store.WriteTextAsync("bars/B/2.parquet", "second");
        await store.WriteTextAsync("bars/A/1.parquet", "first");

        IReadOnlyList<StoredObject> found = store.List("bars");

        Assert.Equal(["bars/A/1.parquet", "bars/B/2.parquet"], found.Select(o => o.Key));
        Assert.Equal(5, found[0].Size);
        Assert.Empty(store.List("quotes"));
    }

    [Fact]
    public async Task An_object_appears_only_once_it_is_whole()
    {
        // Half a Parquet file is not a file, and its name says it covers a range it does not hold. So a write that fails
        // leaves nothing behind - not a shorter file under the right name.
        LocalObjectStore store = Local();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.WriteAsync("bars/A/1.parquet", async stream =>
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes("half a file"));
            throw new InvalidOperationException("interrupted");
        }));

        Assert.False(store.Exists("bars/A/1.parquet"));
        Assert.Empty(store.List("bars"));
    }

    [Fact]
    public async Task A_move_replaces_what_is_there_and_leaves_nothing_behind()
    {
        LocalObjectStore store = Local();
        await store.WriteTextAsync("bars/A/1.parquet", "old");
        await store.WriteTextAsync("bars/A/1.consolidating", "new");

        store.Move("bars/A/1.consolidating", "bars/A/1.parquet");

        Assert.Equal("new", store.ReadText("bars/A/1.parquet"));
        Assert.False(store.Exists("bars/A/1.consolidating"));
    }

    [Fact]
    public void A_key_that_climbs_out_of_the_root_is_refused()
    {
        // Keys are made from instrument ids, which come from a venue. One reading "../../etc" must not be followed.
        LocalObjectStore store = Local();

        Assert.Throws<ArgumentException>(() => store.Exists("../outside.json"));
        Assert.Throws<ArgumentException>(() => store.ReadText("bars/../../outside.json"));
    }

    [Fact]
    public async Task A_read_of_an_object_can_seek_because_Parquet_reads_its_footer_first()
    {
        LocalObjectStore store = Local();
        await store.WriteTextAsync("bars/A/1.parquet", "0123456789");

        await using Stream stream = await store.OpenReadAsync("bars/A/1.parquet");

        Assert.True(stream.CanSeek);
        stream.Seek(-2, SeekOrigin.End);
        Assert.Equal('8', (char)stream.ReadByte());
    }
}
