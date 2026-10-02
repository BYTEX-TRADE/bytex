using Bytex.Persistence.Shared;
using Npgsql;

namespace Bytex.Persistence.Postgres;

/// <summary>
/// The ten key operations on PostgreSQL, over four tables.
///
/// <para>
/// <b>Four tables and not one.</b> A string, a set, a list and a hash are four shapes, and a single table of keys
/// and blobs would mean writing the shape into the value and working it out again on every read - a second
/// serialization format nobody asked for, and the place a store begins to disagree with itself. One table per
/// shape keeps each read a plain query and each promise in the schema: a set cannot hold a member twice because
/// its primary key says so, and a list cannot lose its order because the order is a column.
/// </para>
///
/// <para>
/// <b>The order of a list is the whole point.</b> An order is rebuilt by replaying its events in the order they
/// were appended, so a store handing them back in whatever order the planner finds convenient rebuilds a different
/// order - an acceptance after a fill, a cancel before a submit. PostgreSQL promises no ordering without ORDER BY,
/// so every read of a list has one, over a sequence that only ever increases.
/// </para>
///
/// <para>
/// <b>Synchronous on purpose.</b> ICacheDatabase is synchronous and this is an ADO.NET provider, so the blocking
/// API used here is the one Npgsql publishes rather than something wrapped around an asynchronous one. Connections
/// come from a pooled data source built once, so an operation borrows one instead of opening a socket.
/// </para>
/// </summary>
internal sealed class PostgresStore : IKeyValueStore, IDisposable
{
    private readonly NpgsqlDataSource _source;
    private readonly string _kv;
    private readonly string _set;
    private readonly string _list;
    private readonly string _hash;

    /// <param name="source">A pooled data source; this store owns it and disposes it.</param>
    /// <param name="table">The prefix every table takes, so two engines can share a database without sharing rows.</param>
    public PostgresStore(NpgsqlDataSource source, string table)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _kv = table + "_kv";
        _set = table + "_set";
        _list = table + "_list";
        _hash = table + "_hash";
    }

    /// <summary>
    /// Creates what is missing and leaves what is there.
    ///
    /// <para>
    /// A node starting against an empty database has to work, and a node starting against a database that holds the
    /// state it is about to reload must not have it dropped from under it - so this creates and never drops.
    /// Flushing is a decision the caller makes out loud, and it deletes rows rather than tables.
    /// </para>
    /// </summary>
    public void EnsureSchema() =>
        Execute(
            $"CREATE TABLE IF NOT EXISTS {_kv} (key text PRIMARY KEY, value bytea NOT NULL);"
            + $"CREATE TABLE IF NOT EXISTS {_set} (key text NOT NULL, member text NOT NULL, PRIMARY KEY (key, member));"
            + $"CREATE TABLE IF NOT EXISTS {_list} (key text NOT NULL, seq bigserial PRIMARY KEY, value text NOT NULL);"
            + $"CREATE INDEX IF NOT EXISTS {_list}_key_seq ON {_list} (key, seq);"
            + $"CREATE TABLE IF NOT EXISTS {_hash} (key text NOT NULL, field text NOT NULL, value bytea NOT NULL, PRIMARY KEY (key, field));");

    public byte[]? StringGet(string key)
    {
        using NpgsqlConnection connection = _source.OpenConnection();
        using NpgsqlCommand command = new($"SELECT value FROM {_kv} WHERE key = @key", connection);
        command.Parameters.AddWithValue("key", key);
        return command.ExecuteScalar() as byte[];
    }

    public void StringSet(string key, byte[] value) =>
        Execute(
            $"INSERT INTO {_kv} (key, value) VALUES (@key, @value) ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value",
            ("key", key),
            ("value", value));

    public IReadOnlyList<string> SetMembers(string key) => Strings($"SELECT member FROM {_set} WHERE key = @key", key);

    /// <summary>Adding a member twice leaves one, which the primary key decides rather than a read before the write.</summary>
    public void SetAdd(string key, string member) =>
        Execute(
            $"INSERT INTO {_set} (key, member) VALUES (@key, @member) ON CONFLICT DO NOTHING",
            ("key", key),
            ("member", member));

    public IReadOnlyList<string> ListRange(string key) =>
        Strings($"SELECT value FROM {_list} WHERE key = @key ORDER BY seq", key);

    public long ListLength(string key)
    {
        using NpgsqlConnection connection = _source.OpenConnection();
        using NpgsqlCommand command = new($"SELECT count(*) FROM {_list} WHERE key = @key", connection);
        command.Parameters.AddWithValue("key", key);
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    public void ListRightPush(string key, string value) =>
        Execute($"INSERT INTO {_list} (key, value) VALUES (@key, @value)", ("key", key), ("value", value));

    /// <summary>
    /// Removes a key of any kind, which is why all four tables are named here: the caller deletes a key without
    /// saying what shape it held, and a row left behind in one table would come back as a half-present key.
    /// </summary>
    public void KeyDelete(string key) =>
        Execute(
            $"DELETE FROM {_kv} WHERE key = @key;"
            + $"DELETE FROM {_set} WHERE key = @key;"
            + $"DELETE FROM {_list} WHERE key = @key;"
            + $"DELETE FROM {_hash} WHERE key = @key;",
            ("key", key));

    public IReadOnlyDictionary<string, byte[]> HashGetAll(string key)
    {
        Dictionary<string, byte[]> fields = new(StringComparer.Ordinal);
        using NpgsqlConnection connection = _source.OpenConnection();
        using NpgsqlCommand command = new($"SELECT field, value FROM {_hash} WHERE key = @key", connection);
        command.Parameters.AddWithValue("key", key);
        using NpgsqlDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            fields[reader.GetString(0)] = (byte[])reader[1];
        }

        return fields;
    }

    /// <summary>
    /// Sets the fields given, in one transaction, leaving any other field of the same key alone - which is what
    /// setting fields of a hash means, and what the runtimeModule state above relies on.
    /// </summary>
    public void HashSet(string key, IReadOnlyDictionary<string, byte[]> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        if (fields.Count == 0)
        {
            return;
        }

        using NpgsqlConnection connection = _source.OpenConnection();
        using NpgsqlTransaction transaction = connection.BeginTransaction();
        foreach (KeyValuePair<string, byte[]> field in fields)
        {
            using NpgsqlCommand command = new(
                $"INSERT INTO {_hash} (key, field, value) VALUES (@key, @field, @value)"
                + " ON CONFLICT (key, field) DO UPDATE SET value = EXCLUDED.value",
                connection,
                transaction);

            command.Parameters.AddWithValue("key", key);
            command.Parameters.AddWithValue("field", field.Key);
            command.Parameters.AddWithValue("value", field.Value);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private IReadOnlyList<string> Strings(string sql, string key)
    {
        List<string> values = new();
        using NpgsqlConnection connection = _source.OpenConnection();
        using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue("key", key);
        using NpgsqlDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private void Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using NpgsqlConnection connection = _source.OpenConnection();
        using NpgsqlCommand command = new(sql, connection);
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

    public void Dispose() => _source.Dispose();
}
