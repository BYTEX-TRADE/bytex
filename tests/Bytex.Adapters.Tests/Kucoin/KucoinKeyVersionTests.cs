using System.Reflection;
using Bytex.Adapters.Kucoin;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;

namespace Bytex.Adapters.Tests.Kucoin;

// Why: KuCoin wants to be told which version of key it was given, and a key made before its v3 is refused for a
// reason that depends on what the venue checks first - so the only way to tell is to try. `verify-keys` tried 3 then
// 2 and reported success. The clients did not: they took the stated version, or 3, and signed with it forever.
//
// A v2 key with nothing configured therefore PASSED its key test and then failed the moment a node started. That is
// worse than a rejection, because the report had already told the person their key was fine - and it is the report a
// host's live gate reads. So the clients now find the version the same way the check does, both read one named list
// of which refusals are not about the version, and a test below fails if those two ever drift apart again.
public sealed class KucoinKeyVersionTests
{
    private const string Key = "6705f5c311545b000157d3eb";
    private const string Secret = "c1f1e3e4-5a6b-4c7d-8e9f-0a1b2c3d4e5f";
    private const string Passphrase = "bytex-test";

    /// <summary>"KC-API-KEY-VERSION doesn't match", which is what a v2 key signed as v3 is told.</summary>
    private const string WrongVersion = "400004";

    private static KucoinDataClientConfig Config(LoopbackServer server, string? version = null) => new()
    {
        ApiKey = Key,
        ApiSecret = Secret,
        ApiPassphrase = Passphrase,
        ApiKeyVersion = version,
        BaseUrlHttp = server.HttpBase,
    };

    /// <summary>A venue that refuses everything not signed as <paramref name="accepts"/>.</summary>
    private static Routes OnlyAccepts(string accepts, List<string> versionsSeen) =>
        new Routes().On("GET", "/api/v1/accounts", r =>
        {
            string seen = r.Header("KC-API-KEY-VERSION") ?? "?";
            versionsSeen.Add(seen);
            return seen == accepts
                ? StubResponse.Json(KucoinPayloads.Accounts)
                : StubResponse.Json(KucoinPayloads.Error(WrongVersion, "KC-API-KEY-VERSION doesn't match"));
        });

    [Fact]
    public async Task A_key_of_the_older_version_works_without_being_configured()
    {
        // The defect, as a test. Nothing states a version, the key is v2, and the first signed request is refused.
        // Before this the client gave up there and the node died; now it signs the same request as the other version
        // and carries on.
        List<string> seen = new();
        await using LoopbackServer server = new(OnlyAccepts(KucoinVenue.FallbackApiKeyVersion, seen).Handle);
        using KucoinHttp http = new(Config(server), requireCredentials: true);

        await http.GetSignedAsync("/api/v1/accounts", null, CancellationToken.None);

        Assert.Equal([KucoinVenue.DefaultApiKeyVersion, KucoinVenue.FallbackApiKeyVersion], seen);
        Assert.Equal(KucoinVenue.FallbackApiKeyVersion, http.KeyVersion);
    }

    [Fact]
    public async Task The_version_that_worked_is_the_one_every_later_request_uses()
    {
        // Falling back on every signed request would double the venue's traffic for the whole life of the node and
        // put a refused request in its log every time. It is settled once.
        List<string> seen = new();
        await using LoopbackServer server = new(OnlyAccepts(KucoinVenue.FallbackApiKeyVersion, seen).Handle);
        using KucoinHttp http = new(Config(server), requireCredentials: true);

        await http.GetSignedAsync("/api/v1/accounts", null, CancellationToken.None);
        await http.GetSignedAsync("/api/v1/accounts", null, CancellationToken.None);
        await http.GetSignedAsync("/api/v1/accounts", null, CancellationToken.None);

        Assert.Equal(
            [KucoinVenue.DefaultApiKeyVersion, KucoinVenue.FallbackApiKeyVersion, KucoinVenue.FallbackApiKeyVersion, KucoinVenue.FallbackApiKeyVersion],
            seen);
    }

    [Fact]
    public async Task A_key_of_the_current_version_is_never_asked_about_twice()
    {
        List<string> seen = new();
        await using LoopbackServer server = new(OnlyAccepts(KucoinVenue.DefaultApiKeyVersion, seen).Handle);
        using KucoinHttp http = new(Config(server), requireCredentials: true);

        await http.GetSignedAsync("/api/v1/accounts", null, CancellationToken.None);
        await http.GetSignedAsync("/api/v1/accounts", null, CancellationToken.None);

        Assert.Equal([KucoinVenue.DefaultApiKeyVersion, KucoinVenue.DefaultApiKeyVersion], seen);
    }

    [Fact]
    public async Task A_version_that_has_already_worked_is_not_abandoned_on_a_later_refusal()
    {
        // The guess is over the moment a signed request succeeds. Without that, a working client stays willing to
        // switch for the rest of its life, and one odd refusal later - the venue having a moment, a permission
        // changed under it - would move every subsequent order onto the wrong version and latch it there. The key
        // was never the problem and nothing would say so.
        List<string> seen = new();
        int calls = 0;
        await using LoopbackServer server = new(new Routes().On("GET", "/api/v1/accounts", r =>
        {
            seen.Add(r.Header("KC-API-KEY-VERSION") ?? "?");
            return ++calls == 1
                ? StubResponse.Json(KucoinPayloads.Accounts)
                : StubResponse.Json(KucoinPayloads.Error(WrongVersion, "KC-API-KEY-VERSION doesn't match"));
        }).Handle);

        using KucoinHttp http = new(Config(server), requireCredentials: true);

        await http.GetSignedAsync("/api/v1/accounts", null, CancellationToken.None);
        await Assert.ThrowsAsync<KucoinApiException>(
            () => http.GetSignedAsync("/api/v1/accounts", null, CancellationToken.None));

        // Two requests, both as the version that worked, and no third attempt under the other one.
        Assert.Equal([KucoinVenue.DefaultApiKeyVersion, KucoinVenue.DefaultApiKeyVersion], seen);
        Assert.Equal(KucoinVenue.DefaultApiKeyVersion, http.KeyVersion);
    }

    [Fact]
    public async Task A_version_somebody_wrote_down_is_not_second_guessed()
    {
        // A person who stated a version gets the venue's own refusal for it. Retrying under another one would hide
        // the fact that what they wrote down is wrong, and they are the only one who can fix that.
        List<string> seen = new();
        await using LoopbackServer server = new(OnlyAccepts(KucoinVenue.FallbackApiKeyVersion, seen).Handle);
        using KucoinHttp http = new(Config(server, KucoinVenue.DefaultApiKeyVersion), requireCredentials: true);

        KucoinApiException refused = await Assert.ThrowsAsync<KucoinApiException>(
            () => http.GetSignedAsync("/api/v1/accounts", null, CancellationToken.None));

        Assert.Equal(WrongVersion, refused.Code);
        Assert.Equal([KucoinVenue.DefaultApiKeyVersion], seen);
    }

    [Theory]
    [InlineData("400002")]
    [InlineData("400003")]
    [InlineData("400006")]
    [InlineData("429000")]
    public async Task A_refusal_that_is_not_about_the_version_is_not_retried_under_another_one(string code)
    {
        // The clock being off, the key not existing, the address not being allowed, the rate limit being hit. None of
        // them is the version, and signing again under the other one would turn one refusal into two and report the
        // second - which would say the key version is wrong when the clock is.
        int calls = 0;
        await using LoopbackServer server = new(new Routes().On("GET", "/api/v1/accounts", _ =>
        {
            calls++;
            return StubResponse.Json(KucoinPayloads.Error(code, "refused"));
        }).Handle);

        using KucoinHttp http = new(Config(server), requireCredentials: true);

        KucoinApiException refused = await Assert.ThrowsAsync<KucoinApiException>(
            () => http.GetSignedAsync("/api/v1/accounts", null, CancellationToken.None));

        Assert.Equal(code, refused.Code);
        Assert.Equal(1, calls);
        Assert.Equal(KucoinVenue.DefaultApiKeyVersion, http.KeyVersion);
    }

    [Fact]
    public async Task A_key_that_works_for_neither_version_reports_the_venues_refusal()
    {
        List<string> seen = new();
        await using LoopbackServer server = new(OnlyAccepts("nothing", seen).Handle);
        using KucoinHttp http = new(Config(server), requireCredentials: true);

        KucoinApiException refused = await Assert.ThrowsAsync<KucoinApiException>(
            () => http.GetSignedAsync("/api/v1/accounts", null, CancellationToken.None));

        Assert.Equal(WrongVersion, refused.Code);
        Assert.Equal([KucoinVenue.DefaultApiKeyVersion, KucoinVenue.FallbackApiKeyVersion], seen);
    }

    [Fact]
    public async Task An_unsigned_request_never_changes_which_version_is_used()
    {
        // Public calls carry no version at all, so a public refusal says nothing about the key and must not settle
        // the guess - or one 404 on a market endpoint would decide how every later order is signed.
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/api/v2/symbols", _ => StubResponse.Json(KucoinPayloads.Error("400100", "bad parameter")))
            .Handle);

        using KucoinHttp http = new(Config(server), requireCredentials: true);

        await Assert.ThrowsAsync<KucoinApiException>(() => http.GetPublicAsync("/api/v2/symbols", null, CancellationToken.None));

        Assert.Equal(KucoinVenue.DefaultApiKeyVersion, http.KeyVersion);
    }

    [Fact]
    public void The_key_test_and_the_clients_decide_the_version_from_the_same_list()
    {
        // The guard for the defect itself rather than for its symptom. The two used to hold this list separately,
        // and one of them had no fallback at all, so `verify-keys` said yes to a key a node could not use. A copy of
        // those codes anywhere near the key check fails this.
        string[] lines = File.ReadAllLines(Repo.Path_("src", "Bytex.Cli", "KeyCommands.cs"));
        string source = string.Join(Environment.NewLine, lines);

        Assert.Contains(nameof(KucoinVenue.ErrorsThatAreNotTheKeyVersion), source, StringComparison.Ordinal);

        // Individual codes appear elsewhere in this file for a different and legitimate reason - turning a venue
        // code into a reason a person reads - so what is banned is the LIST: any one line naming all of them is a
        // second copy of the decision, which is how the two came to disagree.
        string[] relisted = [.. lines.Where(l =>
            KucoinVenue.ErrorsThatAreNotTheKeyVersion.All(c => l.Contains($"\"{c}\"", StringComparison.Ordinal)))];

        Assert.True(
            relisted.Length == 0,
            "the key check lists the version-agnostic refusals itself again: " + string.Join(" / ", relisted));

        // And the versions it tries are the venue's two, named once.
        Assert.Contains(nameof(KucoinVenue.DefaultApiKeyVersion), source, StringComparison.Ordinal);
        Assert.Contains(nameof(KucoinVenue.FallbackApiKeyVersion), source, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_client_of_this_venue_signs_through_the_one_place_that_can_fall_back()
    {
        // A second http type, or a client building its own headers, would reintroduce the split this fixes. Every
        // signed request on this venue goes through KucoinHttp, which is the only thing that holds the version.
        (string Name, int Count)[] places = [.. Repo.SourceFiles("Kucoin")
            .Select(f => (Name: Path.GetFileName(f), Count: File.ReadAllText(f).Split("KC-API-KEY-VERSION").Length - 1))
            .Where(p => p.Count > 0)];

        Assert.Equal(1, places.Sum(p => p.Count));
        Assert.Equal("KucoinCommon.cs", Assert.Single(places).Name);
    }
}
