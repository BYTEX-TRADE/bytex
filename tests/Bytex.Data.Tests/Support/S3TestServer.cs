using System.Net.Sockets;
using Bytex.Persistence.S3;

namespace Bytex.Data.Tests.Support;

/// <summary>
/// Whether there is an S3-compatible service to test against, and how to reach it.
///
/// <para>
/// Nothing about object storage can be learned from a double. A dictionary pretending to be a bucket would agree with
/// whatever this engine believes about paths, signatures, pagination and XML, which is exactly the set of beliefs worth
/// checking. So these tests talk to a real service and are SKIPPED where there is none, because a test that returns
/// early because its subject is absent reports success for having checked nothing.
/// </para>
///
/// <para>
/// No endpoint and no credentials live in this repository. Set <c>BYTEX_TEST_S3</c> to a service - AWS, another
/// provider, or anything speaking the API on your own machine - together with the usual
/// <c>AWS_ACCESS_KEY_ID</c> and <c>AWS_SECRET_ACCESS_KEY</c>, and the suite includes them.
/// </para>
/// </summary>
internal static class S3TestServer
{
    public const string EnvEndpoint = "BYTEX_TEST_S3";
    public const string EnvRegion = "BYTEX_TEST_S3_REGION";
    public const string EnvBucket = "BYTEX_TEST_S3_BUCKET";

    private static readonly Lazy<bool> Probe = new(Reach, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string? Endpoint => Environment.GetEnvironmentVariable(EnvEndpoint) is { Length: > 0 } endpoint ? endpoint : null;

    public static string Region => Environment.GetEnvironmentVariable(EnvRegion) is { Length: > 0 } region ? region : "us-east-1";

    /// <summary>The bucket to work in. One a test can create and fill, not one holding anything else.</summary>
    public static string Bucket => Environment.GetEnvironmentVariable(EnvBucket) is { Length: > 0 } bucket ? bucket : "bytex-test";

    public static bool Available => Probe.Value;

    /// <summary>A prefix of its own per test, so tests never read each other's objects.</summary>
    public static string Prefix() => "run-" + Guid.NewGuid().ToString("N")[..8];

    public static S3ObjectStore Store(string prefix) => new(
        new S3ObjectStoreConfig
        {
            Bucket = Bucket,
            Prefix = prefix,
            Region = Region,
            Endpoint = Endpoint,
        },
        Credentials());

    public static S3Credentials Credentials() => S3Credentials.FromEnvironment();

    private static bool Reach()
    {
        if (Endpoint is null)
        {
            return false;
        }

        if (Environment.GetEnvironmentVariable(S3Credentials.EnvAccessKey) is not { Length: > 0 }
            || Environment.GetEnvironmentVariable(S3Credentials.EnvSecretKey) is not { Length: > 0 })
        {
            return false;
        }

        try
        {
            Uri uri = new(Endpoint);
            using TcpClient client = new();
            return client.ConnectAsync(uri.Host, uri.Port).Wait(TimeSpan.FromSeconds(3)) && client.Connected;
        }
        catch (Exception e) when (e is UriFormatException or SocketException or AggregateException)
        {
            return false;
        }
    }
}

/// <summary>A fact that needs an S3-compatible service, skipped with a reason where there is none.</summary>
internal sealed class S3FactAttribute : FactAttribute
{
    public S3FactAttribute()
    {
        if (!S3TestServer.Available)
        {
            Skip = $"No object storage to test against: set {S3TestServer.EnvEndpoint}, {S3Credentials.EnvAccessKey} and {S3Credentials.EnvSecretKey}.";
        }
    }
}
