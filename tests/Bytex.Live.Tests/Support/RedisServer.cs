using System.Net.Sockets;

namespace Bytex.Live.Tests.Support;

/// <summary>
/// Whether a real Redis is reachable, and where.
///
/// <para>
/// The rest of the Redis suite runs against an in-memory double, which is right for what the store decides - key
/// layout, indexes, what an update appends - and says nothing about Redis itself. A dictionary cannot lose a
/// connection, evict a key, trim a stream approximately, or hand a set back in an order nobody chose. The tests
/// guarded by <see cref="RedisFactAttribute"/> are the ones that need a server, and they are SKIPPED rather than
/// failed where there is none, because a developer without Redis installed must still be able to run the suite.
/// </para>
///
/// <para>
/// Skipped rather than quietly passing: a test that returns early when its subject is absent reports success for
/// having checked nothing, which is the one outcome worse than a failure.
/// </para>
/// </summary>
internal static class RedisServer
{
    public const string EnvVariable = "BYTEX_TEST_REDIS";

    private static readonly Lazy<bool> Probe = new(Reach, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Where to look: the environment first, then the default port on this machine, by ADDRESS and not by name.
    ///
    /// <para>
    /// The address is deliberate. "localhost" resolves to an IPv6 loopback first on this platform, and a server bound
    /// to IPv4 does not answer there - not with a refusal, which would be quick, but with nothing, so every attempt
    /// costs the whole connect timeout and a server that is running reads as absent.
    /// </para>
    /// </summary>
    public static string ConnectionString =>
        Environment.GetEnvironmentVariable(EnvVariable) is { Length: > 0 } configured ? configured : "127.0.0.1:6379";

    public static bool Reachable => Probe.Value;

    /// <summary>A prefix of its own per test, so a test never reads what another left behind.</summary>
    public static string Prefix() => "bytex-test-" + Guid.NewGuid().ToString("N")[..8];

    private static bool Reach()
    {
        string[] parts = ConnectionString.Split(',')[0].Split(':');
        string host = parts[0];
        int port = parts.Length > 1 && int.TryParse(parts[1], out int parsed) ? parsed : 6379;

        try
        {
            using TcpClient client = new();
            return client.ConnectAsync(host, port).Wait(TimeSpan.FromSeconds(2)) && client.Connected;
        }
        catch (Exception e) when (e is SocketException or AggregateException or ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>A fact that needs a real Redis, and is skipped with a reason where there is not one.</summary>
internal sealed class RedisFactAttribute : FactAttribute
{
    public RedisFactAttribute()
    {
        if (!RedisServer.Reachable)
        {
            Skip = $"No Redis server at {RedisServer.ConnectionString}; set {RedisServer.EnvVariable} to point at one.";
        }
    }
}
