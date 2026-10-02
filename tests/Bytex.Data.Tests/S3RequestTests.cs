using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bytex.Persistence.S3;

namespace Bytex.Data.Tests;

// Why: what the store puts on the wire, without a service to put it to.
//
// These exist because of what a lenient service hides. Two mutations of the signing - sending a body while claiming the
// hash of an empty one, and leaving the host out of the headers the signature covers - were accepted by the service the
// integration tests run against, and would be refused by AWS. A test that only asks "did the service accept it" cannot
// tell a correct request from one that happens to be forgiven, so the request itself is checked here.
public sealed class S3RequestTests
{
    private sealed class Recorder : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        public List<byte[]> Bodies { get; } = new();

        public string Response { get; set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Response) };
        }
    }

    private static (S3ObjectStore Store, Recorder Wire) Store(string prefix = "catalog")
    {
        Recorder wire = new();
        S3ObjectStore store = new(
            new S3ObjectStoreConfig
            {
                Bucket = "bucket",
                Prefix = prefix,
                Region = "eu-west-1",
                Endpoint = "https://storage.example.com",
            },
            new S3Credentials("key", "secret"),
            new HttpClient(wire));
        return (store, wire);
    }

    private static string HeaderOf(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out IEnumerable<string>? values) ? string.Join(',', values) : string.Empty;

    private static string SignedHeaders(HttpRequestMessage request)
    {
        string parameter = request.Headers.Authorization?.Parameter ?? string.Empty;
        int start = parameter.IndexOf("SignedHeaders=", StringComparison.Ordinal);
        int end = parameter.IndexOf(',', start);
        return start < 0 ? string.Empty : parameter[(start + "SignedHeaders=".Length)..(end < 0 ? parameter.Length : end)];
    }

    [Fact]
    public async Task What_is_written_is_hashed_and_the_hash_is_what_is_signed()
    {
        // A service is entitled to check that the body it received is the body that was signed for, and AWS does. Sending
        // the hash of an empty body with a body attached is refused there and forgiven elsewhere, which is exactly the
        // kind of difference a test against one service cannot see.
        byte[] payload = Encoding.UTF8.GetBytes("a parquet file, near enough");
        (S3ObjectStore store, Recorder wire) = Store();

        using (store)
        {
            await store.WriteAsync("bars/A/1.parquet", stream => stream.WriteAsync(payload).AsTask());
        }

        HttpRequestMessage request = Assert.Single(wire.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal(payload, wire.Bodies[0]);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(payload)), HeaderOf(request, "x-amz-content-sha256"));
        Assert.Contains("x-amz-content-sha256", SignedHeaders(request), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_host_is_among_the_headers_the_signature_covers()
    {
        // Without it a signature is valid for the same request sent to a different service.
        (S3ObjectStore store, Recorder wire) = Store();

        using (store)
        {
            await store.WriteTextAsync("instruments/bx-market%3Av2%2FBINANCE%2FBTCUSDT.json", "{}");
        }

        Assert.Contains("host", SignedHeaders(Assert.Single(wire.Requests)).Split(';'));
    }

    [Fact]
    public async Task The_bucket_and_the_prefix_are_where_the_request_goes()
    {
        (S3ObjectStore store, Recorder wire) = Store("eu/catalog");

        using (store)
        {
            await store.WriteTextAsync("bars/A/1.parquet", "x");
        }

        Assert.Equal("https://storage.example.com/bucket/eu/catalog/bars/A/1.parquet", Assert.Single(wire.Requests).RequestUri!.ToString());
    }

    [Fact]
    public void A_listing_asks_for_the_prefix_and_nothing_else()
    {
        (S3ObjectStore store, Recorder wire) = Store();
        wire.Response = "<ListBucketResult><IsTruncated>false</IsTruncated></ListBucketResult>";

        using (store)
        {
            Assert.Empty(store.List("bars"));
        }

        string uri = Assert.Single(wire.Requests).RequestUri!.ToString();
        Assert.Contains("list-type=2", uri, StringComparison.Ordinal);
        Assert.Contains("prefix=catalog%2Fbars%2F", uri, StringComparison.Ordinal);
    }

    [Fact]
    public void A_move_copies_inside_the_service_rather_than_fetching_the_bytes()
    {
        // The point of the copy header: a consolidated file put in place must not be downloaded and uploaded again.
        (S3ObjectStore store, Recorder wire) = Store();

        using (store)
        {
            store.Move("bars/A/1.consolidating", "bars/A/1.parquet");
        }

        Assert.Equal(2, wire.Requests.Count);
        Assert.Equal(HttpMethod.Put, wire.Requests[0].Method);
        Assert.Equal("/bucket/catalog/bars/A/1.consolidating", HeaderOf(wire.Requests[0], "x-amz-copy-source"));
        Assert.Contains("x-amz-copy-source", SignedHeaders(wire.Requests[0]), StringComparison.Ordinal);
        Assert.Equal(HttpMethod.Delete, wire.Requests[1].Method);
    }

    [Fact]
    public async Task An_object_larger_than_one_request_can_carry_is_refused_by_name()
    {
        // Rather than sent in part. A multipart upload is not written here, and a file cut short is a Parquet file whose
        // name says it covers a range it does not hold.
        Recorder wire = new();
        S3ObjectStore store = new(
            new S3ObjectStoreConfig
            {
                Bucket = "bucket",
                Region = "eu-west-1",
                Endpoint = "https://storage.example.com",
                MaxObjectBytes = 8,
            },
            new S3Credentials("key", "secret"),
            new HttpClient(wire));

        using (store)
        {
            InvalidOperationException e = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await store.WriteAsync("bars/A/1.parquet", stream => stream.WriteAsync(new byte[9]).AsTask()));

            Assert.Contains("single request", e.Message, StringComparison.Ordinal);
        }

        Assert.Empty(wire.Requests);
    }
}
