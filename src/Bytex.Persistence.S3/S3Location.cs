using Bytex.Data;

namespace Bytex.Persistence.S3;

/// <summary>
/// Reads <c>s3://bucket/prefix</c> into a store, and registers itself so that every place a catalog is named by one
/// string accepts one.
///
/// <para>
/// A host turns the scheme on by calling <see cref="Register"/> once. It is a line rather than something that happens
/// on its own: a library that registered itself the moment it was loaded would decide for the program that loaded it,
/// and where that program keeps its data is the program's decision. Nothing in <c>Bytex.Data</c> knows about S3.
/// </para>
/// </summary>
public static class S3Location
{
    public const string Scheme = "s3";

    /// <summary>Where a service other than AWS is, under the name every AWS tool already reads it from.</summary>
    public const string EnvEndpoint = "AWS_ENDPOINT_URL_S3";

    public const string EnvEndpointAny = "AWS_ENDPOINT_URL";

    public const string EnvRegion = "AWS_REGION";

    public const string EnvRegionDefault = "AWS_DEFAULT_REGION";

    /// <summary>Makes <c>s3://bucket/prefix</c> readable everywhere a catalog is named. Calling it twice is harmless.</summary>
    public static void Register() => ObjectStores.Register(Scheme, Open);

    /// <summary>
    /// The store a location names.
    ///
    /// <para>
    /// The bucket and prefix come from the location. The region and the endpoint come from it too where it says so -
    /// <c>s3://bucket/prefix?region=eu-west-1&amp;endpoint=http://localhost:9000</c> - and otherwise from the
    /// environment, under the names the AWS tools use, so a machine already set up for one is set up for this.
    /// </para>
    /// </summary>
    public static IObjectStore Open(string location)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        if (!location.StartsWith(Scheme + "://", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"'{location}' is not an {Scheme}:// location.", nameof(location));
        }

        string rest = location[(Scheme.Length + 3)..];
        string query = string.Empty;
        int mark = rest.IndexOf('?', StringComparison.Ordinal);
        if (mark >= 0)
        {
            query = rest[(mark + 1)..];
            rest = rest[..mark];
        }

        string[] parts = rest.Split('/', 2);
        string bucket = parts[0];
        if (bucket.Length == 0)
        {
            throw new ArgumentException($"'{location}' names no bucket: it reads {Scheme}://bucket/prefix.", nameof(location));
        }

        Dictionary<string, string> options = new(StringComparer.OrdinalIgnoreCase);
        foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
            {
                options[Uri.UnescapeDataString(pair[..equals])] = Uri.UnescapeDataString(pair[(equals + 1)..]);
            }
        }

        string? endpoint = options.GetValueOrDefault("endpoint")
            ?? Environment.GetEnvironmentVariable(EnvEndpoint)
            ?? Environment.GetEnvironmentVariable(EnvEndpointAny);

        string region = options.GetValueOrDefault("region")
            ?? Environment.GetEnvironmentVariable(EnvRegion)
            ?? Environment.GetEnvironmentVariable(EnvRegionDefault)
            ?? "us-east-1";

        bool? pathStyle = options.TryGetValue("path-style", out string? style)
            ? !string.Equals(style, "false", StringComparison.OrdinalIgnoreCase)
            : null;

        return new S3ObjectStore(new S3ObjectStoreConfig
        {
            Bucket = bucket,
            Prefix = parts.Length > 1 ? parts[1] : string.Empty,
            Region = region,
            Endpoint = string.IsNullOrWhiteSpace(endpoint) ? null : endpoint,
            UsePathStyle = pathStyle,
        });
    }
}
