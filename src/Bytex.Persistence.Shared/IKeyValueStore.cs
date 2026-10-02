namespace Bytex.Persistence.Shared;

/// <summary>
/// The whole of what a backing store has to do: ten operations over keys.
///
/// <para>
/// <see cref="KeyValueCacheDatabase"/> talks to this rather than to a database, which is what makes a second
/// backend an afternoon instead of a rewrite - and what makes the key layout, the indexes and the rebuilding of
/// orders exercisable without a server running. A server is the one part of a store that is not this engine's code,
/// so the faults this engine can introduce are all above this line.
/// </para>
///
/// <para>
/// The names are Redis's, because that is where they came from and renaming them would gain nothing: a string, a
/// set and a list are ordinary enough that any store can hold them. What a backend must not do is reinterpret them.
/// A set is unordered and holds each member once; a list keeps insertion order and is appended at the right; a hash
/// is a map of fields under one key; a key deleted is a key absent, not a key holding nothing. Those are the
/// promises the store above depends on, and a conformance suite holds every backend to them.
/// </para>
/// </summary>
internal interface IKeyValueStore
{
    byte[]? StringGet(string key);

    void StringSet(string key, byte[] value);

    /// <summary>The members of a set, in no particular order. A key that holds nothing is empty, not an error.</summary>
    IReadOnlyList<string> SetMembers(string key);

    /// <summary>Adds a member to a set. Adding one twice leaves one.</summary>
    void SetAdd(string key, string member);

    /// <summary>A list in the order it was appended in, which is the order an order's events are replayed in.</summary>
    IReadOnlyList<string> ListRange(string key);

    long ListLength(string key);

    void ListRightPush(string key, string value);

    /// <summary>Removes a key of any kind. A key that was never there is not an error.</summary>
    void KeyDelete(string key);

    IReadOnlyDictionary<string, byte[]> HashGetAll(string key);

    void HashSet(string key, IReadOnlyDictionary<string, byte[]> fields);
}
