using Bytex.Persistence.Shared;
using StackExchange.Redis;

namespace Bytex.Persistence.Redis;

/// <summary>
/// What Redis serves this engine: the ten key operations every backing store provides, plus the one that is Redis's
/// own. The store above talks to <see cref="IKeyValueStore"/> and knows nothing about this; the bus needs a stream,
/// which not every store has, so it asks for this instead.
/// </summary>
internal interface IRedisStore : IKeyValueStore
{
    /// <summary>
    /// Appends to a stream, keeping roughly <paramref name="maxLength"/> entries.
    /// <para>
    /// Roughly, because Redis trims to whole nodes rather than to an exact count and being told to trim exactly
    /// costs more than it is worth here. What matters is that a stream nobody reads cannot grow without limit.
    /// </para>
    /// </summary>
    void StreamAdd(string key, IReadOnlyDictionary<string, byte[]> fields, int maxLength);
}

/// <summary>The real thing: every call goes straight to the Redis database it was given.</summary>
internal sealed class RedisStore : IRedisStore
{
    private readonly IDatabase _db;

    public RedisStore(IDatabase db) => _db = db;

    public byte[]? StringGet(string key)
    {
        RedisValue value = _db.StringGet(key);
        return value.HasValue ? (byte[])value! : null;
    }

    public void StringSet(string key, byte[] value) => _db.StringSet(key, value);

    public IReadOnlyList<string> SetMembers(string key) => _db.SetMembers(key).Select(v => (string)v!).ToList();

    public void SetAdd(string key, string member) => _db.SetAdd(key, member);

    public IReadOnlyList<string> ListRange(string key) => _db.ListRange(key).Select(v => (string)v!).ToList();

    public long ListLength(string key) => _db.ListLength(key);

    public void ListRightPush(string key, string value) => _db.ListRightPush(key, value);

    public void KeyDelete(string key) => _db.KeyDelete(key);

    public IReadOnlyDictionary<string, byte[]> HashGetAll(string key) =>
        _db.HashGetAll(key).ToDictionary(e => (string)e.Name!, e => (byte[])e.Value!, StringComparer.Ordinal);

    public void HashSet(string key, IReadOnlyDictionary<string, byte[]> fields) =>
        _db.HashSet(key, fields.Select(kv => new HashEntry(kv.Key, kv.Value)).ToArray());

    public void StreamAdd(string key, IReadOnlyDictionary<string, byte[]> fields, int maxLength) =>
        _db.StreamAdd(
            key,
            [.. fields.Select(kv => new NameValueEntry(kv.Key, kv.Value))],
            messageId: null,
            maxLength: maxLength,
            useApproximateMaxLength: true);
}
