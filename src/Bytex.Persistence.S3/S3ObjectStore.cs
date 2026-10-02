using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Bytex.Data;

namespace Bytex.Persistence.S3;

public sealed record S3ObjectStoreConfig
{
    public required string Bucket { get; init; }

    /// <summary>A prefix every key sits under, so one bucket can hold several catalogs.</summary>
    public string Prefix { get; init; } = string.Empty;

    public string Region { get; init; } = "us-east-1";

    /// <summary>
    /// The service to talk to, for anything that is not AWS itself: another provider's endpoint, or one running on this
    /// machine. Left unset, the bucket's AWS endpoint for its region is used.
    /// </summary>
    public string? Endpoint { get; init; }

    /// <summary>
    /// Whether the bucket goes in the path (<c>host/bucket/key</c>) rather than in the host name.
    ///
    /// <para>
    /// Unset means: path style for a named endpoint, host style for AWS. That is what each expects - AWS has retired
    /// path style for new buckets, and most other implementations, including every one that runs on a developer's own
    /// machine, only do path style.
    /// </para>
    /// </summary>
    public bool? UsePathStyle { get; init; }

    /// <summary>
    /// Above this many bytes, an object being read or written is spooled to a temporary file rather than held in
    /// memory. Parquet is read from its footer backwards, so a stream that cannot seek is no use, and a catalog file
    /// large enough to matter is exactly the one that must not be held twice in memory.
    /// </summary>
    public int SpoolThresholdBytes { get; init; } = 64 * 1024 * 1024;

    /// <summary>
    /// How many keys a listing asks for at a time. A thousand is the most any implementation returns, and the only
    /// reason to ask for fewer is a test proving the continuation token is followed - which it has to, because a reader
    /// that ignored it would see the first page of a catalog and call it the catalog.
    /// </summary>
    public int PageSize { get; init; } = 1_000;

    /// <summary>
    /// The largest object this writes. It is the limit of a single PUT, which is what this does: a larger object needs
    /// a multipart upload, which is not written here, so it is refused by name rather than half sent.
    /// </summary>
    public long MaxObjectBytes { get; init; } = 5L * 1024 * 1024 * 1024;
}

/// <summary>
/// A catalog on S3-compatible object storage (R9.5).
///
/// <para>
/// Five requests carry all of it - GET, PUT, HEAD, DELETE and a paginated list - and the layout, the names and every
/// guard over them belong to the catalog rather than to this. Anything speaking the S3 API serves it: AWS, another
/// provider, or something running beside the node.
/// </para>
///
/// <para>
/// <b>What it costs, said plainly.</b> Parquet is read from a footer at the end of the file, so reading one object here
/// means fetching the whole object. Reading a catalog remotely is therefore paid for per query; keeping the data beside
/// the machine that reads it, or downloading once into a local catalog, is faster and always will be. What this buys is
/// a catalog larger than any one machine's disk, shared by everything that reads it.
/// </para>
/// </summary>
public sealed class S3ObjectStore : IObjectStore, IDisposable
{
    private readonly S3ObjectStoreConfig _config;
    private readonly Lazy<S3Credentials> _credentials;
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly string _prefix;
    private readonly bool _pathStyle;

    public S3ObjectStore(S3ObjectStoreConfig config, S3Credentials? credentials = null, HttpClient? http = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        ArgumentException.ThrowIfNullOrWhiteSpace(config.Bucket);
        // Resolved when the first request is signed rather than here, so that naming a location is not the same as
        // having keys for it: a configuration is checked, printed and passed around in places that never read a byte.
        _credentials = credentials is null
            ? new Lazy<S3Credentials>(S3Credentials.FromEnvironment, LazyThreadSafetyMode.ExecutionAndPublication)
            : new Lazy<S3Credentials>(credentials);
        _ownsClient = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _prefix = config.Prefix.Trim('/');
        _pathStyle = config.UsePathStyle ?? config.Endpoint is not null;
    }

    public string Location => _prefix.Length == 0 ? $"s3://{_config.Bucket}" : $"s3://{_config.Bucket}/{_prefix}";

    public IReadOnlyList<StoredObject> List(string prefix) => Block(ListAsync(prefix, CancellationToken.None));

    public bool Exists(string key) => Block(ExistsAsync(key, CancellationToken.None));

    public void Delete(string key) => Block(DeleteAsync(key, CancellationToken.None));

    public void Move(string fromKey, string toKey) => Block(MoveAsync(fromKey, toKey, CancellationToken.None));

    public string ReadText(string key) => Block(ReadTextAsync(key, CancellationToken.None));

    public async Task WriteTextAsync(string key, string text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        using ByteArrayContent content = new(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        await PutAsync(key, content, SigV4.HexHash(bytes), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Brings the object somewhere it can be seeked in: memory for a small one, a temporary file for a large one that
    /// deletes itself when the stream is closed.
    /// </summary>
    public async Task<Stream> OpenReadAsync(string key, CancellationToken ct = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, Address(key));
        using HttpResponseMessage response = await SendAsync(request, KeyPath(key), SigV4.EmptyPayloadHash, ct).ConfigureAwait(false);
        await EnsureAsync(response, "GET", key, ct).ConfigureAwait(false);

        long? length = response.Content.Headers.ContentLength;
        Stream destination = length is { } size && size > _config.SpoolThresholdBytes ? TemporaryFile() : new MemoryStream();
        await using (Stream body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        {
            await body.CopyToAsync(destination, ct).ConfigureAwait(false);
        }

        destination.Position = 0;
        return destination;
    }

    /// <summary>
    /// Assembles the object where it can be seeked in, then sends it in one PUT.
    ///
    /// <para>
    /// Buffered rather than streamed because both halves need it: Parquet writes its footer last and seeks back to fix
    /// up what it wrote, and the signature covers a hash of the whole body, which cannot be known before the body is.
    /// </para>
    /// </summary>
    public async Task WriteAsync(string key, Func<Stream, Task> write, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        await using Stream buffer = TemporaryFile();
        await write(buffer).ConfigureAwait(false);
        await buffer.FlushAsync(ct).ConfigureAwait(false);

        if (buffer.Length > _config.MaxObjectBytes)
        {
            throw new InvalidOperationException(
                $"'{key}' is {buffer.Length} bytes, and this store sends an object in a single request, which cannot "
                + $"carry more than {_config.MaxObjectBytes}. Write the data in more than one file - the catalog reads "
                + "any number of them per key - or use storage that holds it in one piece.");
        }

        buffer.Position = 0;
        string hash = await SigV4.HexHashAsync(buffer, ct).ConfigureAwait(false);
        buffer.Position = 0;

        using StreamContent content = new(buffer);
        content.Headers.ContentLength = buffer.Length;
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        await PutAsync(key, content, hash, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates the bucket if it is not there, and says nothing when it already is.
    ///
    /// <para>
    /// Explicit, and never done on the way to a read or a write. A store that created a bucket because a key was
    /// misspelled would answer a typo by making somewhere for it to live, and the next read would find an empty catalog
    /// rather than an error.
    /// </para>
    /// </summary>
    public async Task CreateBucketIfMissingAsync(CancellationToken ct = default)
    {
        // Outside AWS's original region, a bucket has to be told where it is; inside it, saying so is an error.
        string body = string.Equals(_config.Region, "us-east-1", StringComparison.Ordinal)
            ? string.Empty
            : $"<CreateBucketConfiguration xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><LocationConstraint>{_config.Region}</LocationConstraint></CreateBucketConfiguration>";

        byte[] bytes = Encoding.UTF8.GetBytes(body);
        using HttpRequestMessage request = new(HttpMethod.Put, BucketAddress()) { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentLength = bytes.Length;
        using HttpResponseMessage response = await SendAsync(request, BucketPath(), SigV4.HexHash(bytes), ct).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.OK or HttpStatusCode.NoContent)
        {
            return;
        }

        await EnsureAsync(response, "CREATE BUCKET", _config.Bucket, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }

    // ----- the five requests -----

    private async Task<IReadOnlyList<StoredObject>> ListAsync(string prefix, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        string full = Qualify(prefix);
        List<StoredObject> found = new();
        string? token = null;

        do
        {
            StringBuilder query = new("?list-type=2&max-keys=" + Math.Clamp(_config.PageSize, 1, 1_000).ToString(CultureInfo.InvariantCulture));
            if (full.Length > 0)
            {
                query.Append("&prefix=").Append(SigV4.Encode(full.TrimEnd('/') + "/"));
            }

            if (token is not null)
            {
                query.Append("&continuation-token=").Append(SigV4.Encode(token));
            }

            using HttpRequestMessage request = new(HttpMethod.Get, BucketAddress() + query);
            using HttpResponseMessage response = await SendAsync(request, BucketPath(), SigV4.EmptyPayloadHash, ct).ConfigureAwait(false);
            await EnsureAsync(response, "LIST", full, ct).ConfigureAwait(false);

            string xml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            XDocument document = XDocument.Parse(xml);
            XNamespace ns = document.Root?.GetDefaultNamespace() ?? XNamespace.None;

            foreach (XElement item in document.Descendants(ns + "Contents"))
            {
                string? key = item.Element(ns + "Key")?.Value;
                if (key is null)
                {
                    continue;
                }

                long size = long.TryParse(item.Element(ns + "Size")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) ? parsed : 0;
                found.Add(new StoredObject(Relative(key), size));
            }

            token = document.Root?.Element(ns + "IsTruncated")?.Value is "true"
                ? document.Root?.Element(ns + "NextContinuationToken")?.Value
                : null;
        }
        while (token is not null);

        // Ordered by key so every store answers alike; a service is free to return pages in whatever order it keeps.
        found.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
        return found;
    }

    private async Task<bool> ExistsAsync(string key, CancellationToken ct)
    {
        using HttpRequestMessage request = new(HttpMethod.Head, Address(key));
        using HttpResponseMessage response = await SendAsync(request, KeyPath(key), SigV4.EmptyPayloadHash, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        await EnsureAsync(response, "HEAD", key, ct).ConfigureAwait(false);
        return true;
    }

    private async Task DeleteAsync(string key, CancellationToken ct)
    {
        using HttpRequestMessage request = new(HttpMethod.Delete, Address(key));
        using HttpResponseMessage response = await SendAsync(request, KeyPath(key), SigV4.EmptyPayloadHash, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        await EnsureAsync(response, "DELETE", key, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A move is a copy and a delete, because object storage has no rename. The copy happens inside the service - the
    /// bytes are not fetched and sent back - and the source goes only once the copy has been accepted.
    /// </summary>
    private async Task MoveAsync(string fromKey, string toKey, CancellationToken ct)
    {
        using HttpRequestMessage copy = new(HttpMethod.Put, Address(toKey));
        copy.Headers.TryAddWithoutValidation("x-amz-copy-source", SigV4.EncodePath($"/{_config.Bucket}/{Qualify(fromKey)}"));
        using HttpResponseMessage response = await SendAsync(copy, KeyPath(toKey), SigV4.EmptyPayloadHash, ct).ConfigureAwait(false);
        await EnsureAsync(response, "COPY", toKey, ct).ConfigureAwait(false);
        await DeleteAsync(fromKey, ct).ConfigureAwait(false);
    }

    private async Task<string> ReadTextAsync(string key, CancellationToken ct)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, Address(key));
        using HttpResponseMessage response = await SendAsync(request, KeyPath(key), SigV4.EmptyPayloadHash, ct).ConfigureAwait(false);
        await EnsureAsync(response, "GET", key, ct).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private async Task PutAsync(string key, HttpContent content, string payloadHash, CancellationToken ct)
    {
        using HttpRequestMessage request = new(HttpMethod.Put, Address(key)) { Content = content };
        using HttpResponseMessage response = await SendAsync(request, KeyPath(key), payloadHash, ct).ConfigureAwait(false);
        await EnsureAsync(response, "PUT", key, ct).ConfigureAwait(false);
    }

    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string canonicalPath, string payloadHash, CancellationToken ct)
    {
        SigV4.Sign(request, canonicalPath, payloadHash, _credentials.Value, _config.Region, DateTimeOffset.UtcNow);
        return _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    /// <summary>
    /// A failure carries the service's own error code, because that is the only part of it that says what to do:
    /// <c>NoSuchBucket</c>, <c>AccessDenied</c> and <c>SignatureDoesNotMatch</c> are three different jobs.
    /// </summary>
    private static async Task EnsureAsync(HttpResponseMessage response, string what, string key, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        string code = string.Empty;
        try
        {
            if (body.Length > 0 && body.TrimStart().StartsWith('<'))
            {
                XDocument document = XDocument.Parse(body);
                XNamespace ns = document.Root?.GetDefaultNamespace() ?? XNamespace.None;
                code = document.Descendants(ns + "Code").FirstOrDefault()?.Value ?? string.Empty;
            }
        }
        catch (System.Xml.XmlException)
        {
            // A body that is not the error document it claimed to be is reported as it arrived.
        }

        throw new InvalidOperationException(
            $"Object storage refused {what} '{key}' with {(int)response.StatusCode} {response.StatusCode}"
            + (code.Length > 0 ? $" ({code})" : string.Empty)
            + (body.Length > 0 ? $": {body[..Math.Min(body.Length, 400)]}" : "."));
    }

    // ----- addresses and keys -----

    private string Qualify(string key) => _prefix.Length == 0 ? key.TrimStart('/') : $"{_prefix}/{key.TrimStart('/')}";

    private string Relative(string key) =>
        _prefix.Length > 0 && key.StartsWith(_prefix + "/", StringComparison.Ordinal) ? key[(_prefix.Length + 1)..] : key;

    private string Host => _config.Endpoint?.TrimEnd('/')
        ?? (_pathStyle ? $"https://s3.{_config.Region}.amazonaws.com" : $"https://{_config.Bucket}.s3.{_config.Region}.amazonaws.com");

    /// <summary>The path a request is signed over: the bucket is part of it where the bucket is in the path.</summary>
    private string BucketPath() => _pathStyle ? SigV4.EncodePath($"/{_config.Bucket}") : "/";

    private string KeyPath(string key) => _pathStyle
        ? SigV4.EncodePath($"/{_config.Bucket}/{Qualify(key)}")
        : SigV4.EncodePath($"/{Qualify(key)}");

    private string BucketAddress() => _pathStyle ? $"{Host}/{_config.Bucket}" : Host;

    /// <summary>The URI, whose path is the same encoded string the signature covers.</summary>
    private string Address(string key) => Host + KeyPath(key);

    private Stream TemporaryFile() => new FileStream(
        Path.Combine(Path.GetTempPath(), "bytex-s3-" + Guid.NewGuid().ToString("N")),
        FileMode.CreateNew,
        FileAccess.ReadWrite,
        FileShare.None,
        bufferSize: 81920,
        FileOptions.DeleteOnClose);

    /// <summary>Runs one of the synchronous operations. See <see cref="IObjectStore"/> for why three of them are.</summary>
    private static T Block<T>(Task<T> task) => task.GetAwaiter().GetResult();

    private static void Block(Task task) => task.GetAwaiter().GetResult();
}
