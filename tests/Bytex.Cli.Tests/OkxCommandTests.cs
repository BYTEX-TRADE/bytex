using System.Text.Json;
using Bytex.Cli.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Data;

namespace Bytex.Cli.Tests;

// Why: OKX is the first venue this command line can be pointed at that has THREE markets, and --futures is a
// two-way switch. So a person asking for its dated contracts through the flag every other venue uses would get its
// perpetuals instead - silently, with a full catalog stored and nothing to say it was the wrong one. That is what
// --instrument-type exists for, and the command line is this engine's public API, so both paths are exercised here.
//
// The key report is the other half. It is one report for the whole venue and not one per market, which is this
// venue's own doing: one unified account covers spot, perpetuals and dated futures, and it publishes one permission
// list for all three.
public sealed class OkxCommandTests
{
    private const string Key = "kAbCdEfGhIjKlMnOpQ";
    private const string Secret = "sEcReTsEcReTsEcReTsEcReT";
    private const string Passphrase = "open-sesame";
    private const string OkxEnv = $"OKX_API_KEY={Key}\nOKX_API_SECRET={Secret}\nOKX_API_PASSPHRASE={Passphrase}\n";

    /// <summary>BTC-USDT as the live venue describes it, trimmed to the fields the provider reads.</summary>
    private const string SpotInstruments = """
        {"code":"0","msg":"","data":[
          {"instType":"SPOT","instId":"BTC-USDT","baseCcy":"BTC","quoteCcy":"USDT","settleCcy":"","ctVal":"","ctValCcy":"","ctMult":"","ctType":"","uly":"","instFamily":"","tickSz":"0.1","lotSz":"0.00000001","minSz":"0.00001","maxLmtSz":"9999999999","maxLmtAmt":"20000000","lever":"10","state":"live","expTime":"","listTime":"1611907686000"}
        ]}
        """;

    /// <summary>BTC-USDT-SWAP, whose contract is 0.01 BTC.</summary>
    private const string SwapInstruments = """
        {"code":"0","msg":"","data":[
          {"instType":"SWAP","instId":"BTC-USDT-SWAP","baseCcy":"","quoteCcy":"","settleCcy":"USDT","ctVal":"0.01","ctValCcy":"BTC","ctMult":"1","ctType":"linear","uly":"BTC-USDT","instFamily":"BTC-USDT","tickSz":"0.1","lotSz":"0.01","minSz":"0.01","maxLmtSz":"100000000","lever":"100","state":"live","expTime":"","listTime":"1573557408000"}
        ]}
        """;

    /// <summary>The linear dated contract, whose id is BTC-USD_UM-261030 and not BTC-USDT-261030.</summary>
    private const string FuturesInstruments = """
        {"code":"0","msg":"","data":[
          {"instType":"FUTURES","instId":"BTC-USD_UM-261030","baseCcy":"","quoteCcy":"","settleCcy":"USD","ctVal":"0.01","ctValCcy":"BTC","ctMult":"1","ctType":"linear","uly":"BTC-USD","instFamily":"BTC-USD_UM","tickSz":"0.1","lotSz":"0.01","minSz":"0.01","maxLmtSz":"1000000","lever":"20","state":"live","expTime":"1793347200000","listTime":"1787904600714"}
        ]}
        """;

    private const string SwapTiers = """
        {"code":"0","msg":"","data":[{"instType":"SWAP","instFamily":"BTC-USDT","uly":"BTC-USDT","tier":"1","minSz":"0","maxSz":"1000","imr":"0.01","mmr":"0.004","maxLever":"100"}]}
        """;

    private const string FuturesTiers = """
        {"code":"0","msg":"","data":[{"instType":"FUTURES","instFamily":"BTC-USD_UM","uly":"BTC-USD","tier":"1","minSz":"0","maxSz":"4000","imr":"0.05","mmr":"0.02","maxLever":"20"}]}
        """;

    /// <summary>The venue answering whichever market the request names, which is what selects one here.</summary>
    private static StubResponse Okx(RecordedRequest request) => request.Path switch
    {
        "/api/v5/public/instruments" => StubResponse.Json(request.Query("instType") switch
        {
            "SWAP" => SwapInstruments,
            "FUTURES" => FuturesInstruments,
            _ => SpotInstruments,
        }),
        "/api/v5/public/position-tiers" => StubResponse.Json(request.Query("instType") == "FUTURES" ? FuturesTiers : SwapTiers),
        _ => StubResponse.Error(404, """{"code":"51000","data":[],"msg":"Parameter error"}"""),
    };

    // ----- the catalog -----

    [Fact]
    public async Task Fetching_spot_stores_the_pair_with_the_venues_own_grid()
    {
        using TempDirectory temp = new();
        await using LoopbackServer venue = new(Okx);
        string catalog = temp.Combine("catalog");

        CliResult result = await CliRunner.RunAsync(["catalog", "fetch-instruments", "--path", catalog, "--venue", "OKX", "--base-url", venue.HttpBase]);

        Assert.True(result.ExitCode == 0, result.AllOutput);
        Instrument only = Assert.Single(new ParquetDataCatalog(catalog).Instruments());
        Assert.Equal(InstrumentId.Parse("BTC-USDT.OKX"), only.Id);
        Assert.Equal(InstrumentClass.Spot, only.InstrumentClass);
    }

    [Fact]
    public async Task The_futures_flag_fetches_the_perpetuals_because_that_is_what_it_means_everywhere_else()
    {
        // Every other venue's --futures means "the perpetuals", so it has to mean that here too: a flag that
        // silently changed meaning on one venue is worse than a flag that covers two of three markets.
        using TempDirectory temp = new();
        await using LoopbackServer venue = new(Okx);
        string catalog = temp.Combine("catalog");

        CliResult result = await CliRunner.RunAsync(["catalog", "fetch-instruments", "--path", catalog, "--venue", "OKX", "--futures", "--base-url", venue.HttpBase]);

        Assert.True(result.ExitCode == 0, result.AllOutput);
        Instrument only = Assert.Single(new ParquetDataCatalog(catalog).Instruments());
        Assert.Equal(InstrumentId.Parse("BTC-USDT-SWAP.OKX"), only.Id);
        Assert.Equal(InstrumentClass.Swap, only.InstrumentClass);
    }

    [Fact]
    public async Task The_third_market_is_reached_by_naming_it_because_a_two_way_flag_cannot()
    {
        // The dated contracts, which --futures cannot select. Note the id: BTC-USD_UM-261030, and the margin is the
        // venue's own tier - five percent initial, where the perpetual on the same coin requires one.
        using TempDirectory temp = new();
        await using LoopbackServer venue = new(Okx);
        string catalog = temp.Combine("catalog");

        CliResult result = await CliRunner.RunAsync(
            ["catalog", "fetch-instruments", "--path", catalog, "--venue", "OKX", "--instrument-type", "Futures", "--base-url", venue.HttpBase]);

        Assert.True(result.ExitCode == 0, result.AllOutput);
        Instrument only = Assert.Single(new ParquetDataCatalog(catalog).Instruments());
        Assert.Equal(InstrumentId.Parse("BTC-USD_UM-261030.OKX"), only.Id);
        Assert.Equal(InstrumentClass.Future, only.InstrumentClass);
        Assert.Equal(0.05m, only.MarginInit);
        Assert.Equal(0.02m, only.MarginMaint);
    }

    [Fact]
    public async Task Naming_the_market_wins_over_the_flag()
    {
        // Both given is a person who has said what they want in the more specific way, so the name is honoured and
        // the flag is left alone rather than one of them being applied by accident of ordering.
        using TempDirectory temp = new();
        await using LoopbackServer venue = new(Okx);
        string catalog = temp.Combine("catalog");

        CliResult result = await CliRunner.RunAsync(
            ["catalog", "fetch-instruments", "--path", catalog, "--venue", "OKX", "--futures", "--instrument-type", "spot", "--base-url", venue.HttpBase]);

        Assert.True(result.ExitCode == 0, result.AllOutput);
        Assert.Equal(InstrumentId.Parse("BTC-USDT.OKX"), Assert.Single(new ParquetDataCatalog(catalog).Instruments()).Id);
    }

    // ----- the key report -----

    private const string AccountConfig = """
        {"code":"0","msg":"","data":[{"uid":"44705892343619584","acctLv":"2","posMode":"net_mode","level":"Lv1","kycLv":"3","label":"bytex","ip":"203.0.113.7","perm":"read_only,trade","mainUid":"44705892343619584"}]}
        """;

    private const string Balance = """
        {"code":"0","msg":"","data":[{"uTime":"1700000000000","totalEq":"1000","details":[{"ccy":"USDT","eq":"1000","availEq":"1000"}]}]}
        """;

    private static StubResponse OkxKey(RecordedRequest request, string config) => request.Path switch
    {
        "/api/v5/account/config" => StubResponse.Json(config),
        "/api/v5/account/balance" => StubResponse.Json(Balance),
        _ => StubResponse.Error(404, """{"code":"51000","msg":"Parameter error"}"""),
    };

    private static async Task<(CliResult Cli, JsonElement Json, IReadOnlyList<RecordedRequest> Requests)> VerifyAsync(
        Func<RecordedRequest, StubResponse> handler,
        string env = OkxEnv)
    {
        using TempDirectory temp = new();
        await using LoopbackServer server = new(handler);
        CliResult cli = await CliRunner.RunAsync(
            ["verify-keys", "--venue", "OKX", "--env-file", temp.File("keys.env", env), "--json", "--base-url", server.HttpBase]);

        Assert.StartsWith("{", cli.StdOut.TrimStart(), StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(cli.StdOut);
        return (cli, document.RootElement.Clone(), server.Requests);
    }

    private static bool? Fact(JsonElement json, params string[] path)
    {
        JsonElement e = json.GetProperty("facts");
        foreach (string name in path)
        {
            e = e.GetProperty(name);
        }

        return e.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    [Fact]
    public async Task A_trading_key_reports_its_facts_and_no_secret_leaves_the_process()
    {
        (CliResult cli, JsonElement json, IReadOnlyList<RecordedRequest> requests) = await VerifyAsync(r => OkxKey(r, AccountConfig));

        Assert.Equal(0, cli.ExitCode);
        Assert.Equal("OKX", json.GetProperty("venue").GetString());
        Assert.True(Fact(json, "keyAccepted"));
        Assert.True(Fact(json, "canTrade"));
        Assert.False(Fact(json, "canWithdraw"));
        Assert.True(Fact(json, "ipRestricted"));

        // Both markets, because the venue publishes one permission list for a unified account covering all three -
        // so reporting them separately would invent a distinction the venue does not make.
        Assert.True(Fact(json, "markets", "spot"));
        Assert.True(Fact(json, "markets", "futures"));

        Assert.Equal(
            ["env-file", "auth", "permissions", "withdraw", "ip-allow-list", "position-mode", "account"],
            json.GetProperty("checks").EnumerateArray().Select(c => c.GetProperty("name").GetString()));

        // The four headers this venue signs with, and the passphrase in clear - which is how it differs from
        // KuCoin's, whose passphrase travels signed.
        RecordedRequest first = requests[0];
        Assert.Equal(Key, first.Header("OK-ACCESS-KEY"));
        Assert.NotNull(first.Header("OK-ACCESS-SIGN"));
        Assert.NotNull(first.Header("OK-ACCESS-TIMESTAMP"));
        Assert.Equal(Passphrase, first.Header("OK-ACCESS-PASSPHRASE"));

        // And nothing secret was printed, which is the whole reason a host runs this as a separate process.
        Assert.DoesNotContain(Secret, cli.AllOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(Passphrase, cli.AllOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_read_only_key_is_reported_as_one_rather_than_as_a_failure()
    {
        (CliResult cli, JsonElement json, _) = await VerifyAsync(
            r => OkxKey(r, AccountConfig.Replace("read_only,trade", "read_only", StringComparison.Ordinal)));

        Assert.Equal(0, cli.ExitCode);
        Assert.True(Fact(json, "keyAccepted"));
        Assert.False(Fact(json, "canTrade"));
    }

    [Fact]
    public async Task An_account_in_long_short_mode_is_warned_about_rather_than_passed_silently()
    {
        // The one account setting that changes what an order has to carry: in long/short mode the venue demands a
        // position side on every derivative order and this engine sends none, so a node would be refused. A green
        // report in front of a node that cannot place an order is worse than a red one.
        (CliResult cli, JsonElement json, _) = await VerifyAsync(
            r => OkxKey(r, AccountConfig.Replace("net_mode", "long_short_mode", StringComparison.Ordinal)));

        Assert.Equal(0, cli.ExitCode);
        Assert.True(Fact(json, "keyAccepted"));

        string mode = json.GetProperty("checks").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "position-mode")
            .GetProperty("status").GetString()!;

        Assert.Equal("warn", mode);
        Assert.Contains("net mode", cli.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_withdrawal_permission_is_a_warning_because_a_trading_key_should_not_have_one()
    {
        (CliResult cli, JsonElement json, _) = await VerifyAsync(
            r => OkxKey(r, AccountConfig.Replace("read_only,trade", "read_only,trade,withdraw", StringComparison.Ordinal)));

        Assert.Equal(0, cli.ExitCode);
        Assert.True(Fact(json, "canWithdraw"));
        Assert.Equal(
            "warn",
            json.GetProperty("checks").EnumerateArray()
                .Single(c => c.GetProperty("name").GetString() == "withdraw")
                .GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_key_with_no_ip_restriction_is_a_warning_and_not_a_failure()
    {
        (CliResult cli, JsonElement json, _) = await VerifyAsync(
            r => OkxKey(r, AccountConfig.Replace("\"ip\":\"203.0.113.7\"", "\"ip\":\"\"", StringComparison.Ordinal)));

        Assert.Equal(0, cli.ExitCode);
        Assert.False(Fact(json, "ipRestricted"));
    }

    [Fact]
    public async Task A_key_the_venue_does_not_know_is_reported_as_a_bad_key()
    {
        // Measured against the live venue with a made-up key: HTTP 401 and code 50111, "Invalid OK-ACCESS-KEY". The
        // venue checks the key before anything else, which is why the codes for a bad signature or a bad passphrase
        // could not be measured and are taken from the venue's own list.
        (CliResult cli, JsonElement json, _) = await VerifyAsync(
            _ => StubResponse.Error(401, """{"msg":"Invalid OK-ACCESS-KEY","code":"50111"}"""));

        Assert.Equal(1, cli.ExitCode);
        Assert.Equal("bad_key", json.GetProperty("failure").GetProperty("code").GetString());
        Assert.Equal("50111", json.GetProperty("failure").GetProperty("venueCode").GetString());
        Assert.False(Fact(json, "keyAccepted"));
    }

    [Fact]
    public async Task A_live_key_used_against_the_demo_account_is_its_own_answer()
    {
        // Neither the key nor the account is wrong here - the key is in the wrong environment - so it gets a word of
        // its own rather than being reported as a bad key somebody would then go and replace.
        (CliResult cli, JsonElement json, _) = await VerifyAsync(
            _ => StubResponse.Error(401, """{"msg":"APIKey does not match current environment.","code":"50101"}"""));

        Assert.Equal(1, cli.ExitCode);
        Assert.Equal("wrong_environment", json.GetProperty("failure").GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_file_missing_the_passphrase_is_refused_by_the_name_of_the_variable_to_set()
    {
        // This venue's key has a third part that cannot be recovered after the key is made, so a file with two
        // thirds of one has to say which third is missing rather than failing at the venue.
        (CliResult cli, JsonElement json, IReadOnlyList<RecordedRequest> requests) = await VerifyAsync(
            r => OkxKey(r, AccountConfig),
            env: $"OKX_API_KEY={Key}\nOKX_API_SECRET={Secret}\n");

        Assert.Equal(1, cli.ExitCode);
        Assert.Equal("no_key_in_file", json.GetProperty("failure").GetProperty("code").GetString());
        Assert.Contains("OKX_API_PASSPHRASE", cli.StdOut, StringComparison.Ordinal);

        // And nothing was sent, so an incomplete key never reaches the venue.
        Assert.Empty(requests);
    }
}
