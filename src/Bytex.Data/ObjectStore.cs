namespace Bytex.Data;

/// <summary>One thing a store holds, and how big it is.</summary>
/// <param name="Key">The key, relative to the store's root, with <c>/</c> between its parts on every platform.</param>
/// <param name="Size">Its size in bytes.</param>
public readonly record struct StoredObject(string Key, long Size);

/// <summary>
/// Where a catalog keeps its bytes (R9.5).
///
/// <para>
/// The catalog used to call <see cref="File"/> and <see cref="Directory"/> at fifty sites, which is why it could only
/// ever be a directory on the machine running it. This is the seam that lets the same catalog - the same layout, the
/// same file names, the same guards - sit on object storage instead, for a team whose data is larger than any one
/// machine's disk.
/// </para>
///
/// <para>
/// <b>Keys, not paths.</b> A key is relative to the store's root and always separated by <c>/</c>. The local store
/// maps that onto the platform's separator; a remote store uses it as it stands, because that is what object storage
/// means by a prefix.
/// </para>
///
/// <para>
/// <b>Why the shapes differ.</b> Listing, existence and deletion are synchronous because the paths that use them are
/// tools and startup - <c>catalog info</c>, an instrument lookup, a consolidation - and because the catalog's own
/// synchronous members are part of contracts elsewhere in the engine that cannot become asynchronous. Reading and
/// writing bytes are asynchronous because that is where the time goes and because a trading loop must never wait on
/// them. A remote store blocks the calling thread on the synchronous three, which is honest about what a network is
/// and is never done from the loop.
/// </para>
/// </summary>
public interface IObjectStore
{
    /// <summary>What to call this store in a message: a path, or a bucket and prefix.</summary>
    string Location { get; }

    /// <summary>
    /// Everything under a prefix, recursively, with sizes. A prefix nothing is stored under is empty rather than an
    /// error: a catalog that has never held quotes is not a broken catalog.
    /// </summary>
    IReadOnlyList<StoredObject> List(string prefix);

    bool Exists(string key);

    void Delete(string key);

    /// <summary>
    /// Moves an object, replacing whatever is at the destination. Used where something is written under a working name
    /// and put in place once it is complete, so an interruption costs the working copy rather than the data.
    /// </summary>
    void Move(string fromKey, string toKey);

    string ReadText(string key);

    Task WriteTextAsync(string key, string text, CancellationToken ct = default);

    /// <summary>
    /// Opens an object for reading, as a stream that can SEEK.
    ///
    /// <para>
    /// Parquet keeps its schema in a footer, so a reader's first move is to the end of the file. A store that cannot
    /// seek in place has to bring the object to somewhere that can - which is the cost of keeping data remotely, and
    /// is the store's business rather than the catalog's.
    /// </para>
    /// </summary>
    Task<Stream> OpenReadAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Writes an object through the seekable stream handed to <paramref name="write"/>.
    ///
    /// <para>
    /// A callback rather than a returned stream because the store decides where the bytes are assembled, and because
    /// the object appears only once the callback has finished: half a Parquet file is not a file, and its name says it
    /// covers a range it does not hold.
    /// </para>
    /// </summary>
    Task WriteAsync(string key, Func<Stream, Task> write, CancellationToken ct = default);
}

/// <summary>
/// The default: a directory on this machine, which is what the catalog has always been.
/// </summary>
public sealed class LocalObjectStore : IObjectStore
{
    private readonly string _root;

    public LocalObjectStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _root = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(_root);
    }

    public string Location => _root;

    public IReadOnlyList<StoredObject> List(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        string directory = Resolve(prefix);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        List<StoredObject> found = new();
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(".writing", StringComparison.Ordinal)) { continue; }
            try
            {
                found.Add(new StoredObject(KeyOf(file), new FileInfo(file).Length));
            }
            catch (FileNotFoundException)
            {
                // An immutable publication or concurrent removal may occur after enumeration.
            }
        }

        // Ordered by key, so every store answers in the same order and the catalog's file ordering does not depend on
        // what a file system happens to return.
        found.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
        return found;
    }

    public bool Exists(string key) => File.Exists(Resolve(key));

    public void Delete(string key) => File.Delete(Resolve(key));

    public void Move(string fromKey, string toKey)
    {
        string to = Resolve(toKey);
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Move(Resolve(fromKey), to, overwrite: true);
    }

    public string ReadText(string key) => File.ReadAllText(Resolve(key));

    public async Task WriteTextAsync(string key, string text, CancellationToken ct = default)
    {
        string path = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, text, ct).ConfigureAwait(false);
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct = default) =>
        Task.FromResult<Stream>(File.OpenRead(Resolve(key)));

    public async Task WriteAsync(string key, Func<Stream, Task> write, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        string path = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Written beside the destination and moved into place, so an interrupted write leaves a working file rather
        // than a partially published object that a reader could mistake for committed data.
        string working = path + ".writing";
        try
        {
            await using (FileStream stream = File.Create(working))
            {
                await write(stream).ConfigureAwait(false);
            }

            File.Move(working, path, overwrite: true);
        }
        catch
        {
            if (File.Exists(working))
            {
                File.Delete(working);
            }

            throw;
        }
    }

    private string Resolve(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        string relative = key.Replace('/', Path.DirectorySeparatorChar);
        string full = Path.GetFullPath(Path.Combine(_root, relative));

        // A key that climbs out of the root is refused rather than followed: a catalog key comes from an instrument id,
        // and one holding "../" would read and write outside the catalog it was given.
        string within = Path.GetRelativePath(_root, full);
        if (Path.IsPathRooted(within) || within == ".."
            || within.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException($"The key '{key}' resolves outside the store's root.", nameof(key));
        }

        return full;
    }

    private string KeyOf(string fullPath) =>
        Path.GetRelativePath(_root, fullPath).Replace(Path.DirectorySeparatorChar, '/');
}

/// <summary>
/// Turns a location a person wrote into a store.
///
/// <para>
/// A catalog is named by one string everywhere it is asked for - a command line option, a backtest's data
/// configuration, a strategy document - and the point of this is that <c>s3://bucket/prefix</c> works in every one of
/// them without any of them knowing what S3 is. A path is a path; anything with a scheme is looked up here.
/// </para>
///
/// <para>
/// A host turns a scheme on by registering its backend once, at startup. An unknown scheme therefore says which package
/// provides it and what to call, rather than only refusing.
/// </para>
/// </summary>
public static class ObjectStores
{
    private static readonly Dictionary<string, Func<string, IObjectStore>> Schemes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers a backend for one scheme, replacing any already registered for it.</summary>
    public static void Register(string scheme, Func<string, IObjectStore> open)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme);
        ArgumentNullException.ThrowIfNull(open);
        lock (Schemes)
        {
            Schemes[scheme] = open;
        }
    }

    /// <summary>The schemes a store can be opened for, besides a plain path.</summary>
    public static IReadOnlyList<string> Registered()
    {
        lock (Schemes)
        {
            return [.. Schemes.Keys.OrderBy(s => s, StringComparer.Ordinal)];
        }
    }

    public static IObjectStore Open(string location)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        if (SchemeOf(location) is not { } scheme)
        {
            return new LocalObjectStore(location);
        }

        Func<string, IObjectStore>? open;
        lock (Schemes)
        {
            Schemes.TryGetValue(scheme, out open);
        }

        if (open is null)
        {
            throw new ArgumentException(
                $"Nothing here reads '{scheme}://' locations. Register the backend that does at startup - "
                + "S3Location.Register() from Bytex.Persistence.S3, for 's3' - or give a path instead."
                + (Registered().Count > 0 ? $" Registered: {string.Join(", ", Registered())}." : string.Empty),
                nameof(location));
        }

        return open(location);
    }

    /// <summary>
    /// The scheme of a location, or null for a path.
    ///
    /// <para>
    /// A Windows drive is not a scheme, which is the whole difficulty: <c>C:\data</c> has a colon in the same place
    /// <c>s3://bucket</c> does, so a scheme is only read where the colon is followed by two slashes.
    /// </para>
    /// </summary>
    internal static string? SchemeOf(string location)
    {
        int colon = location.IndexOf("://", StringComparison.Ordinal);
        if (colon <= 0)
        {
            return null;
        }

        string scheme = location[..colon];
        return scheme.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.') ? scheme : null;
    }
}
