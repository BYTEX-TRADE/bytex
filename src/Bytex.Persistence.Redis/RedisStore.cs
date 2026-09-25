using StackExchange.Redis;

namespace Bytex.Persistence.Redis;

/// <summary>
/// The handful of Redis operations the state store needs, named for what they do rather than for the client that
/// serves them. The store talks to this instead of to a connection, so the key layout, the indexes it maintains and
/// the way it rebuilds orders and positions can be exercised without a server - which is where its own faults would
/// be, a server being the one part of it that is not this engine's code.
/// </summary>
internal interface IRedisStore
{
    byte[]? StringGet(string key);

    void StringSet(string key, byte[] value);

    IReadOnlyList<string> SetMembers(string key);

    void SetAdd(string key, string member);

    IReadOnlyList<string> ListRange(string key);

    long ListLength(string key);

    void ListRightPush(string key, string value);

    void KeyDelete(string key);

    IReadOnlyDictionary<string, byte[]> HashGetAll(string key);

    void HashSet(string key, IReadOnlyDictionary<string, byte[]> fields);
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
}
