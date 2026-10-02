using System.Collections.Concurrent;
using System.Text;

namespace Bytex.Data.Tests.Support;

internal sealed class MemoryObjectStore : IObjectStore
{
    private readonly ConcurrentDictionary<string, byte[]> _objects = new(StringComparer.Ordinal);
    public string Location { get; } = "memory://" + Guid.NewGuid().ToString("N");
    public IReadOnlyList<StoredObject> List(string prefix) => [.. _objects.Where(o => o.Key.StartsWith(prefix, StringComparison.Ordinal))
        .Select(o => new StoredObject(o.Key, o.Value.Length))];
    public bool Exists(string key) => _objects.ContainsKey(key);
    public void Delete(string key) => _objects.TryRemove(key, out _);
    public void Move(string fromKey, string toKey)
    {
        if (!_objects.TryRemove(fromKey, out byte[]? bytes)) { throw new FileNotFoundException(fromKey); }
        _objects[toKey] = bytes;
    }
    public string ReadText(string key) => Encoding.UTF8.GetString(Read(key));
    public Task WriteTextAsync(string key, string text, CancellationToken ct = default) =>
        WriteAsync(key, stream => stream.WriteAsync(Encoding.UTF8.GetBytes(text), ct).AsTask(), ct);
    public Task<Stream> OpenReadAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Stream>(new MemoryStream(Read(key), writable: false));
    }
    public async Task WriteAsync(string key, Func<Stream, Task> write, CancellationToken ct = default)
    {
        using MemoryStream stream = new();
        await write(stream);
        ct.ThrowIfCancellationRequested();
        _objects[key] = stream.ToArray();
    }
    private byte[] Read(string key) => _objects.TryGetValue(key, out byte[]? bytes) ? bytes : throw new FileNotFoundException(key);
}

internal sealed class BoundaryFaultStore(IObjectStore inner, Func<string, bool> match, int occurrence = 1, bool afterPublication = false) : IObjectStore
{
    private int _writes;
    public string Location => inner.Location;
    public IReadOnlyList<StoredObject> List(string prefix) => inner.List(prefix);
    public bool Exists(string key) => inner.Exists(key);
    public void Delete(string key) => inner.Delete(key);
    public void Move(string fromKey, string toKey) => inner.Move(fromKey, toKey);
    public string ReadText(string key) => inner.ReadText(key);
    public Task WriteTextAsync(string key, string text, CancellationToken ct = default) => inner.WriteTextAsync(key, text, ct);
    public Task<Stream> OpenReadAsync(string key, CancellationToken ct = default) => inner.OpenReadAsync(key, ct);
    public async Task WriteAsync(string key, Func<Stream, Task> write, CancellationToken ct = default)
    {
        bool fail = match(key) && Interlocked.Increment(ref _writes) == occurrence;
        if (fail && !afterPublication) { throw new IOException("Injected failure before publication: " + key); }
        await inner.WriteAsync(key, write, ct);
        if (fail) { throw new IOException("Injected failure after publication: " + key); }
    }
}
