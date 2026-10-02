using System.Net.Sockets;
using Npgsql;

namespace Bytex.Live.Tests.Support;

/// <summary>
/// Whether a real PostgreSQL is reachable, and where.
///
/// <para>
/// Same shape as <see cref="RedisServer"/>, and for the same reason. The store's own logic - the key layout, the
/// indexes, the rebuilding of orders - is exercised against an in-memory double, which is right for what the store
/// DECIDES and says nothing about the database. A dictionary cannot refuse a connection, roll a transaction back,
/// order a query's rows differently from how they went in, or hold a key in one table and not another. The checks
/// guarded by <see cref="PostgresFactAttribute"/> are the ones that need a server.
/// </para>
///
/// <para>
/// Skipped rather than quietly passing, because a test that returns early when its subject is absent reports
/// success for having checked nothing.
/// </para>
/// </summary>
internal static class PostgresServer
{
    public const string EnvVariable = "BYTEX_TEST_POSTGRES";

    private static readonly Lazy<bool> Probe = new(Reach, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The environment first, then an ordinary local development server. By address rather than by name, as the
    /// Redis probe is and for the same reason: "localhost" resolves to the IPv6 loopback first on this platform and
    /// a server bound to IPv4 answers there with nothing rather than with a refusal, so every attempt costs the
    /// whole connect timeout and a server that is running reads as absent.
    /// </summary>
    public static string ConnectionString =>
        Environment.GetEnvironmentVariable(EnvVariable) is { Length: > 0 } configured
            ? configured
            : "Host=127.0.0.1;Port=5432;Database=postgres;Username=postgres;Password=postgres;Timeout=3";

    public static bool Reachable => Probe.Value;

    /// <summary>
    /// A table prefix of its own per test, so a test never reads what another left behind and the four tables of
    /// one test can be dropped without touching another's.
    /// </summary>
    public static string Tables() => "bytex_test_" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>Removes the four tables a test created, whatever it did to their rows.</summary>
    public static void Drop(string tables)
    {
        if (!Reachable)
        {
            return;
        }

        using NpgsqlConnection connection = new(ConnectionString);
        connection.Open();
        foreach (string suffix in new[] { "_kv", "_set", "_list", "_hash" })
        {
            using NpgsqlCommand command = new($"DROP TABLE IF EXISTS {tables}{suffix}", connection);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// The port first and the handshake second. A port that accepts a connection is not necessarily a database -
    /// and a database that is listening may still refuse these credentials, which is the same answer as absent for
    /// a test's purposes and a better one to give as a skip than as a failure.
    /// </summary>
    private static bool Reach()
    {
        try
        {
            using NpgsqlConnection connection = new(ConnectionString);
            connection.Open();
            using NpgsqlCommand command = new("SELECT 1", connection);
            return Equals(command.ExecuteScalar(), 1);
        }
        catch (Exception e) when (e is NpgsqlException or SocketException or TimeoutException or AggregateException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>A fact that needs a real PostgreSQL, and is skipped with a reason where there is not one.</summary>
internal sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (!PostgresServer.Reachable)
        {
            Skip = $"No PostgreSQL answering at the configured address; set {PostgresServer.EnvVariable} to point at one.";
        }
    }
}
