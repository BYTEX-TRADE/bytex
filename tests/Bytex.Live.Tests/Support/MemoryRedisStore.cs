using Bytex.Persistence.Redis;

namespace Bytex.Live.Tests.Support;

/// <summary>
/// Redis as a dictionary: strings, lists, sets, hashes and streams, with the semantics the store relies on.
///
/// <para>
/// It is a double and it is honest about what that means. It cannot lose a connection, run out of memory, evict a
/// key or reorder anything, so a test passing here says the code is right about Redis' shape and nothing about
/// Redis' behaviour. The tests beside these run against a real server for that.
/// </para>
/// </summary>
internal sealed class MemoryRedisStore : IRedisStore
{
    public Dictionary<string, byte[]> Strings { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, List<string>> Lists { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, HashSet<string>> Sets { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, Dictionary<string, byte[]>> Hashes { get; } = new(StringComparer.Ordinal);

    public List<string> Deleted { get; } = new();

    /// <summary>Streams, as the entries appended to each, so a test can read what a publisher wrote.</summary>
    public Dictionary<string, List<IReadOnlyDictionary<string, byte[]>>> Streams { get; } = new(StringComparer.Ordinal);

    public void StreamAdd(string key, IReadOnlyDictionary<string, byte[]> fields, int maxLength)
    {
        if (!Streams.TryGetValue(key, out List<IReadOnlyDictionary<string, byte[]>>? entries))
        {
            entries = new List<IReadOnlyDictionary<string, byte[]>>();
            Streams[key] = entries;
        }

        entries.Add(fields);

        // Trimmed the way Redis trims: oldest first, and only roughly, which is what the real one promises.
        while (entries.Count > maxLength)
        {
            entries.RemoveAt(0);
        }
    }

    public byte[]? StringGet(string key) => Strings.GetValueOrDefault(key);

    public void StringSet(string key, byte[] value) => Strings[key] = value;

    public IReadOnlyList<string> SetMembers(string key) => Sets.TryGetValue(key, out HashSet<string>? set) ? set.Order(StringComparer.Ordinal).ToList() : [];

    public void SetAdd(string key, string member) => (Sets.TryGetValue(key, out HashSet<string>? set) ? set : Sets[key] = new(StringComparer.Ordinal)).Add(member);

    public IReadOnlyList<string> ListRange(string key) => Lists.TryGetValue(key, out List<string>? list) ? list : [];

    public long ListLength(string key) => Lists.TryGetValue(key, out List<string>? list) ? list.Count : 0;

    public void ListRightPush(string key, string value) => (Lists.TryGetValue(key, out List<string>? list) ? list : Lists[key] = new()).Add(value);

    public void KeyDelete(string key)
    {
        Deleted.Add(key);
        Strings.Remove(key);
        Lists.Remove(key);
        Sets.Remove(key);
        Hashes.Remove(key);
    }

    public IReadOnlyDictionary<string, byte[]> HashGetAll(string key) => Hashes.TryGetValue(key, out Dictionary<string, byte[]>? hash) ? hash : new Dictionary<string, byte[]>(StringComparer.Ordinal);

    public void HashSet(string key, IReadOnlyDictionary<string, byte[]> fields) => Hashes[key] = new Dictionary<string, byte[]>(fields, StringComparer.Ordinal);

    public IEnumerable<string> Keys => Strings.Keys.Concat(Lists.Keys).Concat(Sets.Keys).Concat(Hashes.Keys);
}
