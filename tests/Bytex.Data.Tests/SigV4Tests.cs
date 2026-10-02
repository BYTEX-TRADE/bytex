using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Bytex.Persistence.S3;

namespace Bytex.Data.Tests;

// Why: the signature is the only hard part of talking to object storage, and the only part that cannot be checked by
// reading the answer - a service that disagrees refuses everything, and one that agrees says nothing about why.
//
// What is checked here is the shape of what is signed, which is where the mistakes are: a path encoded twice, headers in
// the wrong order, a query the service will sort differently, a slash turned into %2F. None of these tests assert a
// signature, because a signature this code produced and this code expects is a test of nothing. The signature itself is
// checked by S3ObjectStoreTests against a real service, which is the only authority on it.
[Collection("environment")]
public sealed class SigV4Tests
{
    private static readonly S3Credentials _credentials = new("AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY");
    private static readonly DateTimeOffset _when = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    private static string Authorization(HttpRequestMessage request) => request.Headers.Authorization?.Parameter ?? string.Empty;

    private static HttpRequestMessage Signed(HttpMethod method, string url, string canonicalPath, string payloadHash)
    {
        HttpRequestMessage request = new(method, url);
        SigV4.Sign(request, canonicalPath, payloadHash, _credentials, "us-east-1", _when);
        return request;
    }

    [Fact]
    public void The_hash_of_an_empty_body_is_the_hash_of_nothing()
    {
        // Independently: SHA-256 of zero bytes, computed here rather than copied from anywhere.
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData([])), SigV4.EmptyPayloadHash);
    }

    [Fact]
    public void A_path_is_encoded_segment_by_segment_and_its_slashes_are_left_alone()
    {
        // A key the catalog makes from an instrument id carries a percent sign, and a space is what a person's own file
        // name carries. Encoding the slashes too would make one segment out of the whole path.
        Assert.Equal("/bucket/quotes/EUR%252FUSD/segment-example.parquet", SigV4.EncodePath("/bucket/quotes/EUR%2FUSD/segment-example.parquet"));
        Assert.Equal("/bucket/a%20b/c.parquet", SigV4.EncodePath("/bucket/a b/c.parquet"));
        Assert.Equal("/", SigV4.EncodePath(string.Empty));
    }

    [Fact]
    public void Everything_outside_the_unreserved_set_is_encoded_in_upper_case_hex()
    {
        Assert.Equal("AZaz09-._~", SigV4.Encode("AZaz09-._~"));
        Assert.Equal("%2F%3D%2B%20%25", SigV4.Encode("/=+ %"));

        // Not by code point: a character outside ASCII is encoded as the bytes it is in UTF-8.
        Assert.Equal("%D0%91", SigV4.Encode("Б"));
    }

    [Fact]
    public void The_signature_names_every_header_it_covers_in_lower_case_and_in_order()
    {
        HttpRequestMessage request = new(HttpMethod.Put, "https://s3.example.com/bucket/key");
        request.Headers.TryAddWithoutValidation("x-amz-copy-source", "/bucket/other");
        request.Content = new ByteArrayContent([1, 2, 3]);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        SigV4.Sign(request, "/bucket/key", SigV4.HexHash([1, 2, 3]), _credentials, "us-east-1", _when);

        string authorization = Authorization(request);
        Assert.Contains("SignedHeaders=content-type;host;x-amz-content-sha256;x-amz-copy-source;x-amz-date", authorization, StringComparison.Ordinal);
    }

    [Fact]
    public void The_credential_scope_is_the_day_the_region_and_the_service()
    {
        HttpRequestMessage request = Signed(HttpMethod.Get, "https://s3.example.com/bucket/key", "/bucket/key", SigV4.EmptyPayloadHash);

        Assert.Contains($"Credential={_credentials.AccessKey}/20260927/us-east-1/s3/aws4_request", Authorization(request), StringComparison.Ordinal);
        Assert.Equal("20260927T120000Z", Assert.Single(request.Headers.GetValues("x-amz-date")));
        Assert.Equal(SigV4.EmptyPayloadHash, Assert.Single(request.Headers.GetValues("x-amz-content-sha256")));
    }

    [Fact]
    public void A_session_token_is_sent_and_signed_when_there_is_one()
    {
        // Temporary credentials are the usual case on a machine with a role rather than a key, and a token that is sent
        // but not signed is refused.
        HttpRequestMessage request = new(HttpMethod.Get, "https://s3.example.com/bucket/key");
        SigV4.Sign(request, "/bucket/key", SigV4.EmptyPayloadHash, new S3Credentials("id", "secret", "the-token"), "eu-west-1", _when);

        Assert.Equal("the-token", Assert.Single(request.Headers.GetValues("x-amz-security-token")));
        Assert.Contains("x-amz-security-token", Authorization(request), StringComparison.Ordinal);
    }

    [Fact]
    public void Two_requests_differing_only_in_what_is_signed_do_not_share_a_signature()
    {
        // The reason for the whole exercise: a signature that did not cover the path, the body or the day would be one a
        // service could accept for a different request.
        string Sign(string path, string payload, string region, DateTimeOffset when)
        {
            HttpRequestMessage request = new(HttpMethod.Get, "https://s3.example.com" + path);
            SigV4.Sign(request, path, payload, _credentials, region, when);
            return Authorization(request);
        }

        string baseline = Sign("/bucket/key", SigV4.EmptyPayloadHash, "us-east-1", _when);

        Assert.NotEqual(baseline, Sign("/bucket/other", SigV4.EmptyPayloadHash, "us-east-1", _when));
        Assert.NotEqual(baseline, Sign("/bucket/key", SigV4.HexHash(Encoding.UTF8.GetBytes("body")), "us-east-1", _when));
        Assert.NotEqual(baseline, Sign("/bucket/key", SigV4.EmptyPayloadHash, "eu-west-1", _when));
        Assert.NotEqual(baseline, Sign("/bucket/key", SigV4.EmptyPayloadHash, "us-east-1", _when.AddDays(1)));
        Assert.Equal(baseline, Sign("/bucket/key", SigV4.EmptyPayloadHash, "us-east-1", _when));
    }

    [Fact]
    public void Credentials_from_the_environment_say_what_to_set_when_there_are_none()
    {
        string? access = Environment.GetEnvironmentVariable(S3Credentials.EnvAccessKey);
        string? secret = Environment.GetEnvironmentVariable(S3Credentials.EnvSecretKey);
        try
        {
            Environment.SetEnvironmentVariable(S3Credentials.EnvAccessKey, null);
            Environment.SetEnvironmentVariable(S3Credentials.EnvSecretKey, null);

            InvalidOperationException e = Assert.Throws<InvalidOperationException>(S3Credentials.FromEnvironment);

            Assert.Contains(S3Credentials.EnvAccessKey, e.Message, StringComparison.Ordinal);
            Assert.Contains(S3Credentials.EnvSecretKey, e.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(S3Credentials.EnvAccessKey, access);
            Environment.SetEnvironmentVariable(S3Credentials.EnvSecretKey, secret);
        }
    }

    [Fact]
    public void A_secret_never_appears_in_what_is_sent()
    {
        HttpRequestMessage request = Signed(HttpMethod.Get, "https://s3.example.com/bucket/key", "/bucket/key", SigV4.EmptyPayloadHash);

        string everything = string.Join('\n', request.Headers.Select(h => $"{h.Key}: {string.Join(',', h.Value)}"));

        Assert.DoesNotContain(_credentials.SecretKey, everything, StringComparison.Ordinal);
        Assert.Contains(_credentials.AccessKey, everything, StringComparison.Ordinal);
    }
}
