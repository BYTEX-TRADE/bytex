using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Bytex.Persistence.S3;

/// <summary>
/// Signature Version 4, which is how every S3-compatible service authenticates a request.
///
/// <para>
/// Written here rather than taken from a vendor SDK. The whole of what this engine needs from object storage is five
/// requests - get, put, head, delete, list - and a vendor's client brings a dependency tree larger than this
/// repository to serve them. The signing is the only hard part of the five, it is specified rather than guessed, and it
/// fails loudly and completely when it is wrong: a service answers <c>SignatureDoesNotMatch</c> and nothing else
/// happens. That is the opposite of the failure this repository fears, which is a plausible answer to a wrong
/// question.
/// </para>
/// </summary>
internal static class SigV4
{
    private const string Algorithm = "AWS4-HMAC-SHA256";

    /// <summary>The hash of an empty body, which every request without one sends.</summary>
    public static readonly string EmptyPayloadHash = HexHash([]);

    /// <summary>
    /// Signs a request in place: the headers a service checks, and the Authorization that ties them together.
    /// </summary>
    /// <param name="request">The request, with its URI and any headers already set.</param>
    /// <param name="canonicalPath">
    /// The request path, encoded exactly as it is in the URI. Passed rather than read back off the URI: reading it back
    /// and encoding it again turns a key with a space in it into one with a %2520 in it, and the signature then covers a
    /// path the service never saw.
    /// </param>
    /// <param name="payloadHash">Hex SHA-256 of the body, or <see cref="EmptyPayloadHash"/>.</param>
    /// <param name="credentials">Access key, secret key and optional session token.</param>
    /// <param name="region">The region the service expects in the credential scope.</param>
    /// <param name="now">The moment to sign for; a service refuses a signature more than fifteen minutes old.</param>
    public static void Sign(HttpRequestMessage request, string canonicalPath, string payloadHash, S3Credentials credentials, string region, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(canonicalPath);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(credentials);
        Uri uri = request.RequestUri ?? throw new ArgumentException("The request has no URI to sign.", nameof(request));

        string amzDate = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        string dateStamp = now.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        if (!string.IsNullOrEmpty(credentials.SessionToken))
        {
            request.Headers.TryAddWithoutValidation("x-amz-security-token", credentials.SessionToken);
        }

        // Host is signed, and HttpClient sets it only when the request goes out, so it is named here.
        string host = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";

        SortedDictionary<string, string> headers = new(StringComparer.Ordinal) { ["host"] = host };
        foreach ((string name, IEnumerable<string> values) in Enumerate(request))
        {
            headers[name.ToLowerInvariant()] = string.Join(",", values.Select(v => v.Trim()));
        }

        string canonicalHeaders = string.Concat(headers.Select(h => $"{h.Key}:{h.Value}\n"));
        string signedHeaders = string.Join(";", headers.Keys);

        string canonicalRequest = string.Join('\n',
            request.Method.Method,
            canonicalPath,
            CanonicalQuery(uri),
            canonicalHeaders,
            signedHeaders,
            payloadHash);

        string scope = $"{dateStamp}/{region}/s3/aws4_request";
        string stringToSign = string.Join('\n', Algorithm, amzDate, scope, HexHash(Encoding.UTF8.GetBytes(canonicalRequest)));

        byte[] signingKey = Hmac(Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes("AWS4" + credentials.SecretKey), dateStamp), region), "s3"), "aws4_request");
        string signature = Convert.ToHexStringLower(Hmac(signingKey, stringToSign));

        request.Headers.Authorization = new AuthenticationHeaderValue(
            Algorithm,
            $"Credential={credentials.AccessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");
    }

    /// <summary>Every header on the request or its content, because both are signed if both are sent.</summary>
    private static IEnumerable<(string Name, IEnumerable<string> Values)> Enumerate(HttpRequestMessage request)
    {
        foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
        {
            yield return (header.Key, header.Value);
        }

        if (request.Content is { } content)
        {
            foreach (KeyValuePair<string, IEnumerable<string>> header in content.Headers)
            {
                yield return (header.Key, header.Value);
            }
        }
    }

    /// <summary>
    /// A path encoded segment by segment, for both the URI and the signature over it.
    ///
    /// <para>
    /// A slash separates segments and is not encoded; everything outside the unreserved set is. S3 signs the path once
    /// rather than twice, unlike most other AWS services, which is the detail that makes a hand-written signer for S3
    /// specifically worth stating: a key with a space or a percent in it is where a double-encoded path stops matching.
    /// </para>
    /// </summary>
    public static string EncodePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.Length == 0 ? "/" : string.Join('/', path.Split('/').Select(Encode));
    }

    /// <summary>Query parameters sorted by name, each encoded, as the service will sort them before checking.</summary>
    internal static string CanonicalQuery(Uri uri)
    {
        string query = uri.Query.TrimStart('?');
        if (query.Length == 0)
        {
            return string.Empty;
        }

        List<(string Key, string Value)> pairs = new();
        foreach (string part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = part.IndexOf('=', StringComparison.Ordinal);
            pairs.Add(equals < 0
                ? (Uri.UnescapeDataString(part), string.Empty)
                : (Uri.UnescapeDataString(part[..equals]), Uri.UnescapeDataString(part[(equals + 1)..])));
        }

        return string.Join('&', pairs
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .ThenBy(p => p.Value, StringComparer.Ordinal)
            .Select(p => $"{Encode(p.Key)}={Encode(p.Value)}"));
    }

    /// <summary>
    /// Percent-encoding as the specification defines it: unreserved characters as they are, everything else in upper
    /// case hex. <c>Uri.EscapeDataString</c> agrees today, and this does not depend on it continuing to.
    /// </summary>
    internal static string Encode(string value)
    {
        StringBuilder encoded = new(value.Length);
        foreach (byte b in Encoding.UTF8.GetBytes(value))
        {
            char c = (char)b;
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.' or '_' or '~')
            {
                encoded.Append(c);
            }
            else
            {
                encoded.Append(CultureInfo.InvariantCulture, $"%{b:X2}");
            }
        }

        return encoded.ToString();
    }

    public static string HexHash(byte[] payload) => Convert.ToHexStringLower(SHA256.HashData(payload));

    public static async Task<string> HexHashAsync(Stream stream, CancellationToken ct)
    {
        byte[] hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));
}

/// <summary>
/// What a store signs with. Read from the environment when it is not given, under the names every S3 tool already
/// uses, so a machine already set up for one does not have to be set up again for this.
/// </summary>
public sealed record S3Credentials(string AccessKey, string SecretKey, string? SessionToken = null)
{
    public const string EnvAccessKey = "AWS_ACCESS_KEY_ID";
    public const string EnvSecretKey = "AWS_SECRET_ACCESS_KEY";
    public const string EnvSessionToken = "AWS_SESSION_TOKEN";

    public static S3Credentials FromEnvironment()
    {
        string? access = Environment.GetEnvironmentVariable(EnvAccessKey);
        string? secret = Environment.GetEnvironmentVariable(EnvSecretKey);
        if (string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException(
                $"No credentials for object storage: set {EnvAccessKey} and {EnvSecretKey}, or pass them to the store.");
        }

        return new S3Credentials(access, secret, Environment.GetEnvironmentVariable(EnvSessionToken));
    }
}
