using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Bytex.Cli.Tests.Support;

namespace Bytex.Cli.Tests;

// Why: `bytex verify-keys --json` is the contract a supervising application builds its key report on. The facts decide
// whether a person is told "ready to trade" or "this key can withdraw", and the failure code decides which advice they get,
// so every fact and every code is pinned here against the answers the venues document. The venue is a stub on 127.0.0.1.
public sealed class VerifyKeysCommandTests
{
    private const string Key = "kAbCdEfGhIjKlMnOpQ";
    private const string Secret = "sEcReTsEcReTsEcReTsEcReT";
    private const string BybitEnv = $"BYBIT_API_KEY={Key}\nBYBIT_API_SECRET={Secret}\n";
    private const string BinanceEnv = $"BINANCE_API_KEY={Key}\nBINANCE_API_SECRET={Secret}\n";
    private const string BybitWallet = """{"retCode":0,"retMsg":"OK","result":{"list":[{"accountType":"UNIFIED","totalEquity":"1000"}]},"retExtInfo":{},"time":1672211918471}""";
    private const string BinanceAccount = """{"makerCommission":10,"canTrade":true,"canWithdraw":true,"canDeposit":true,"accountType":"SPOT","balances":[],"permissions":["SPOT"]}""";

    private static string BybitKeyInfo(int readOnly = 0, string permissions = """{"ContractTrade":["Order","Position"],"Spot":["SpotTrade"],"Wallet":["AccountTransfer"],"Options":[],"Derivatives":[],"Exchange":[]}""", string ips = """["203.0.113.7"]""", string expiredAt = "2027-01-01T00:00:00Z") => $$"""
        {"retCode":0,"retMsg":"","result":{"id":"13770661","note":"bot","apiKey":"{{Key}}","readOnly":{{readOnly}},"secret":"","permissions":{{permissions}},"ips":{{ips}},"type":1,"deadlineDay":83,"expiredAt":"{{expiredAt}}","createdAt":"2022-10-06T07:21:24Z","uta":1,"userID":24617703,"isMaster":true},"retExtInfo":{},"time":1672211918471}
        """;

    private static string BybitError(int code, string message) => $$"""{"retCode":{{code}},"retMsg":"{{message}}","result":{},"retExtInfo":{},"time":1672211918471}""";

    private static string BinanceError(int code, string message) => $$"""{"code":{{code}},"msg":"{{message}}"}""";

    private static StubResponse Bybit(RecordedRequest request, string keyInfo) => request.Path switch
    {
        "/v5/user/query-api" => StubResponse.Json(keyInfo),
        "/v5/account/wallet-balance" => StubResponse.Json(BybitWallet),
        _ => StubResponse.Error(404, "{}"),
    };

    private sealed record Run(CliResult Cli, JsonElement Json, IReadOnlyList<RecordedRequest> Requests);

    private static async Task<Run> VerifyAsync(string venue, string env, Func<RecordedRequest, StubResponse> handler, params string[] extra)
    {
        using TempDirectory temp = new();
        await using LoopbackServer server = new(handler);
        CliResult cli = await CliRunner.RunAsync(["verify-keys", "--venue", venue, "--env-file", temp.File("keys.env", env), "--json", "--base-url", server.HttpBase, .. extra]);
        return new Run(cli, Parse(cli), server.Requests);
    }

    private static JsonElement Parse(CliResult cli)
    {
        Assert.StartsWith("{", cli.StdOut.TrimStart(), StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(cli.StdOut);
        return document.RootElement.Clone();
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
            JsonValueKind.Null => null,
            _ => throw new Xunit.Sdk.XunitException($"fact {string.Join('.', path)} is {e.ValueKind}, expected a boolean or null"),
        };
    }

    private static void AssertFailure(Run run, string code, int? httpStatus, string? venueCode)
    {
        Assert.Equal(1, run.Cli.ExitCode);
        Assert.False(run.Json.GetProperty("ok").GetBoolean());
        JsonElement failure = run.Json.GetProperty("failure");
        Assert.Equal(code, failure.GetProperty("code").GetString());
        Assert.Equal(httpStatus, failure.GetProperty("httpStatus").ValueKind == JsonValueKind.Null ? null : failure.GetProperty("httpStatus").GetInt32());
        Assert.Equal(venueCode, failure.GetProperty("venueCode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(failure.GetProperty("message").GetString()));
        Assert.Contains(run.Json.GetProperty("checks").EnumerateArray(), c => c.GetProperty("status").GetString() == "fail");
        Assert.DoesNotContain(Secret, run.Cli.AllOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("signature=", run.Cli.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Bybit_trading_key_reports_every_fact_and_leaves_the_checks_as_they_were()
    {
        Run run = await VerifyAsync("bybit", BybitEnv, r => Bybit(r, BybitKeyInfo()));

        Assert.Equal(0, run.Cli.ExitCode);
        Assert.True(run.Json.GetProperty("ok").GetBoolean());
        Assert.Equal("BYBIT", run.Json.GetProperty("venue").GetString());
        Assert.Equal(JsonValueKind.Null, run.Json.GetProperty("failure").ValueKind);
        Assert.True(Fact(run.Json, "keyAccepted"));
        Assert.True(Fact(run.Json, "canTrade"));
        Assert.False(Fact(run.Json, "canWithdraw"));
        Assert.True(Fact(run.Json, "ipRestricted"));
        Assert.True(Fact(run.Json, "markets", "spot"));
        Assert.True(Fact(run.Json, "markets", "futures"));
        Assert.Equal("2027-01-01T00:00:00Z", run.Json.GetProperty("facts").GetProperty("keyExpiresAt").GetString());
        Assert.Equal(
            ["env-file", "auth", "permissions", "withdraw", "ip-allow-list", "account"],
            run.Json.GetProperty("checks").EnumerateArray().Select(c => c.GetProperty("name").GetString()));
        Assert.All(run.Json.GetProperty("checks").EnumerateArray(), c => Assert.Equal("ok", c.GetProperty("status").GetString()));
        Assert.Equal(Key, run.Requests[0].Header("X-BAPI-API-KEY"));
        Assert.DoesNotContain(Secret, run.Cli.AllOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, run.Cli.AllOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Bybit_read_only_key_passes_but_cannot_trade()
    {
        Run run = await VerifyAsync("BYBIT", BybitEnv, r => Bybit(r, BybitKeyInfo(readOnly: 1)));

        Assert.Equal(0, run.Cli.ExitCode);
        Assert.True(Fact(run.Json, "keyAccepted"));
        Assert.False(Fact(run.Json, "canTrade"));
        Assert.False(Fact(run.Json, "markets", "spot"));
        Assert.False(Fact(run.Json, "markets", "futures"));
    }

    [Fact]
    public async Task A_Bybit_key_with_the_withdraw_permission_is_reported_and_warned_about()
    {
        Run run = await VerifyAsync("BYBIT", BybitEnv, r => Bybit(r, BybitKeyInfo(permissions: """{"ContractTrade":["Order","Position"],"Spot":["SpotTrade"],"Wallet":["AccountTransfer","Withdraw"]}""")));

        Assert.Equal(0, run.Cli.ExitCode);
        Assert.True(Fact(run.Json, "canWithdraw"));
        JsonElement withdraw = Assert.Single(run.Json.GetProperty("checks").EnumerateArray(), c => c.GetProperty("name").GetString() == "withdraw");
        Assert.Equal("warn", withdraw.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("""["*"]""")]
    [InlineData("[]")]
    public async Task A_Bybit_key_open_to_every_address_is_not_ip_restricted(string ips)
    {
        Run run = await VerifyAsync("BYBIT", BybitEnv, r => Bybit(r, BybitKeyInfo(ips: ips)));

        Assert.False(Fact(run.Json, "ipRestricted"));
    }

    [Fact]
    public async Task A_Bybit_spot_only_key_without_an_expiry_reports_futures_off_and_no_date()
    {
        Run run = await VerifyAsync("BYBIT", BybitEnv, r => Bybit(r, BybitKeyInfo(permissions: """{"ContractTrade":[],"Spot":["SpotTrade"],"Wallet":[]}""", expiredAt: "1970-01-01T00:00:00Z")));

        Assert.True(Fact(run.Json, "canTrade"));
        Assert.True(Fact(run.Json, "markets", "spot"));
        Assert.False(Fact(run.Json, "markets", "futures"));
        Assert.Equal(JsonValueKind.Null, run.Json.GetProperty("facts").GetProperty("keyExpiresAt").ValueKind);
    }


    [Theory]
    [InlineData(10003, "API key is invalid.", "bad_key", false)]
    [InlineData(10004, "Error sign, please check your signature generation algorithm.", "bad_signature", false)]
    [InlineData(33004, "Your api key has expired", "key_expired", false)]
    [InlineData(10010, "Unmatched IP, please check your API key's bound IP addresses.", "ip_not_allowed", false)]
    [InlineData(10002, "The request time exceeds the time window range.", "clock_skew", null)]
    [InlineData(10005, "Permission denied, please check your API key permissions.", "permission_denied", null)]
    [InlineData(10009, "Service Restricted: Access is currently unavailable for your region.", "geo_blocked", null)]
    [InlineData(10006, "Too many visits. Exceeded the API Rate Limit.", "rate_limited", null)]
    [InlineData(10016, "Server error.", "venue_error", null)]
    public async Task A_Bybit_error_answer_becomes_its_failure_code(int retCode, string retMsg, string code, bool? keyAccepted)
    {
        Run run = await VerifyAsync("BYBIT", BybitEnv, _ => StubResponse.Json(BybitError(retCode, retMsg)));

        AssertFailure(run, code, null, retCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (keyAccepted is null)
        {
            Assert.Equal(JsonValueKind.Null, run.Json.GetProperty("facts").ValueKind);
        }
        else
        {
            Assert.Equal(keyAccepted, Fact(run.Json, "keyAccepted"));
            Assert.Null(Fact(run.Json, "canTrade"));
            Assert.Null(Fact(run.Json, "canWithdraw"));
        }
    }

    [Fact]
    public async Task A_Bybit_key_that_may_not_read_the_wallet_fails_with_the_facts_already_known()
    {
        Run run = await VerifyAsync("BYBIT", BybitEnv, r => r.Path == "/v5/account/wallet-balance" ? StubResponse.Json(BybitError(10005, "Permission denied, please check your API key permissions.")) : Bybit(r, BybitKeyInfo()));

        AssertFailure(run, "permission_denied", null, "10005");
        Assert.True(Fact(run.Json, "keyAccepted"));
        Assert.True(Fact(run.Json, "canTrade"));
    }

    [Fact]
    public async Task A_Bybit_wallet_of_another_account_type_is_a_warning_not_a_failure()
    {
        Run run = await VerifyAsync("BYBIT", BybitEnv, r => r.Path == "/v5/account/wallet-balance" ? StubResponse.Json(BybitError(10001, "accountType only support CONTRACT and SPOT.")) : Bybit(r, BybitKeyInfo()));

        Assert.Equal(0, run.Cli.ExitCode);
        JsonElement account = Assert.Single(run.Json.GetProperty("checks").EnumerateArray(), c => c.GetProperty("name").GetString() == "account");
        Assert.Equal("warn", account.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_Bybit_answer_blocked_by_location_is_geo_blocked()
    {
        Run run = await VerifyAsync("BYBIT", BybitEnv, _ => new StubResponse(403, "<html><body>The Amazon CloudFront distribution is configured to block access from your country.</body></html>", "text/html"));

        AssertFailure(run, "geo_blocked", 403, null);
    }

    [Fact]
    public async Task Binance_mainnet_reads_the_facts_from_the_key_restrictions()
    {
        Run run = await VerifyAsync("BINANCE", BinanceEnv, r => r.Path switch
        {
            "/api/v3/account" => StubResponse.Json(BinanceAccount),
            "/sapi/v1/account/apiRestrictions" => StubResponse.Json("""{"ipRestrict":true,"createTime":1623840271000,"enableWithdrawals":false,"enableInternalTransfer":true,"permitsUniversalTransfer":true,"enableVanillaOptions":false,"enableReading":true,"enableFutures":false,"enableMargin":false,"enableSpotAndMarginTrading":true,"tradingAuthorityExpirationTime":1628985600000}"""),
            _ => StubResponse.Error(404, "{}"),
        });

        Assert.Equal(0, run.Cli.ExitCode);
        Assert.Equal("BINANCE", run.Json.GetProperty("venue").GetString());
        Assert.True(Fact(run.Json, "keyAccepted"));
        Assert.True(Fact(run.Json, "canTrade"));
        Assert.False(Fact(run.Json, "canWithdraw"));
        Assert.True(Fact(run.Json, "ipRestricted"));
        Assert.True(Fact(run.Json, "markets", "spot"));
        Assert.False(Fact(run.Json, "markets", "futures"));
        Assert.Equal(Key, run.Requests[0].Header("X-MBX-APIKEY"));
        Assert.Equal(
            ["env-file", "auth", "permissions", "withdraw", "ip-allow-list", "trading"],
            run.Json.GetProperty("checks").EnumerateArray().Select(c => c.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task Binance_mainnet_with_withdrawals_on_and_no_ip_restriction_says_so()
    {
        Run run = await VerifyAsync("BINANCE", BinanceEnv, r => r.Path == "/api/v3/account"
            ? StubResponse.Json(BinanceAccount)
            : StubResponse.Json("""{"ipRestrict":false,"enableWithdrawals":true,"enableReading":true,"enableFutures":true,"enableSpotAndMarginTrading":false}"""));

        Assert.Equal(0, run.Cli.ExitCode);
        Assert.True(Fact(run.Json, "canWithdraw"));
        Assert.False(Fact(run.Json, "ipRestricted"));
        Assert.True(Fact(run.Json, "canTrade"));
        Assert.False(Fact(run.Json, "markets", "spot"));
        Assert.True(Fact(run.Json, "markets", "futures"));
    }

    [Fact]
    public async Task Binance_mainnet_with_unreadable_restrictions_keeps_the_unknown_facts_null()
    {
        Run run = await VerifyAsync("BINANCE", BinanceEnv, r => r.Path == "/api/v3/account" ? StubResponse.Json(BinanceAccount) : StubResponse.Error(400, BinanceError(-1002, "You are not authorized to execute this request.")));

        Assert.Equal(0, run.Cli.ExitCode);
        Assert.True(Fact(run.Json, "keyAccepted"));
        Assert.Null(Fact(run.Json, "canTrade"));
        Assert.Null(Fact(run.Json, "canWithdraw"));
        Assert.Null(Fact(run.Json, "ipRestricted"));
        Assert.Contains(run.Json.GetProperty("checks").EnumerateArray(), c => c.GetProperty("name").GetString() == "restrictions" && c.GetProperty("status").GetString() == "warn");
    }


    [Theory]
    [InlineData(401, -2014, "API-key format invalid.", "bad_key", false)]
    [InlineData(401, -2015, "Invalid API-key, IP, or permissions for action.", "bad_key_or_ip", false)]
    [InlineData(400, -1022, "Signature for this request is not valid.", "bad_signature", false)]
    [InlineData(400, -1021, "Timestamp for this request is outside of the recvWindow.", "clock_skew", null)]
    [InlineData(401, -1002, "You are not authorized to execute this request.", "permission_denied", null)]
    [InlineData(400, -1100, "Illegal characters found in a parameter.", "venue_error", null)]
    public async Task A_Binance_error_answer_becomes_its_failure_code(int status, int venueCode, string message, string code, bool? keyAccepted)
    {
        Run run = await VerifyAsync("BINANCE", BinanceEnv, _ => StubResponse.Error(status, BinanceError(venueCode, message)));

        AssertFailure(run, code, status, venueCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (keyAccepted is null)
        {
            Assert.Equal(JsonValueKind.Null, run.Json.GetProperty("facts").ValueKind);
        }
        else
        {
            Assert.Equal(keyAccepted, Fact(run.Json, "keyAccepted"));
        }
    }

    [Fact]
    public async Task A_Binance_answer_from_a_restricted_location_is_geo_blocked()
    {
        Run run = await VerifyAsync("BINANCE", BinanceEnv, _ => StubResponse.Error(451, """{"code":0,"msg":"Service unavailable from a restricted location according to 'b. Eligibility' in https://www.binance.com/en/terms."}"""));

        AssertFailure(run, "geo_blocked", 451, null);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(418)]
    public async Task A_rate_limit_answer_is_rate_limited_and_the_retry_log_stays_off_standard_output(int status)
    {
        Run run = await VerifyAsync("BINANCE", BinanceEnv, _ => StubResponse.Error(status, BinanceError(-1003, "Too much request weight used; current limit is 1200 request weight per 1 MINUTE.")));

        AssertFailure(run, "rate_limited", status, "-1003");
        Assert.True(run.Requests.Count > 1);
    }

    [Fact]
    public async Task A_server_error_without_a_venue_code_is_a_venue_error()
    {
        Run run = await VerifyAsync("BINANCE", BinanceEnv, _ => new StubResponse(502, "<html>Bad Gateway</html>", "text/html"));

        AssertFailure(run, "venue_error", 502, null);
    }

    [Fact]
    public async Task An_answer_that_is_not_json_is_unknown()
    {
        Run run = await VerifyAsync("BYBIT", BybitEnv, _ => new StubResponse(200, "<html>maintenance</html>", "text/html"));

        AssertFailure(run, "unknown", null, null);
    }

    [Fact]
    public async Task A_venue_that_refuses_the_connection_is_unreachable()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using TempDirectory temp = new();

        CliResult cli = await CliRunner.RunAsync(["verify-keys", "--venue", "BYBIT", "--env-file", temp.File("keys.env", BybitEnv), "--json", "--base-url", $"http://127.0.0.1:{port}"]);

        AssertFailure(new Run(cli, Parse(cli), []), "unreachable", null, null);
    }

    [Fact]
    public async Task A_venue_that_does_not_answer_within_the_timeout_is_unreachable_and_the_command_does_not_wait_for_it()
    {
        using ManualResetEventSlim release = new();
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

        Run run = await VerifyAsync("BYBIT", BybitEnv, r =>
        {
            release.Wait(TimeSpan.FromSeconds(60));
            return Bybit(r, BybitKeyInfo());
        }, "--timeout", "1.5");

        watch.Stop();
        release.Set();
        AssertFailure(run, "unreachable", null, null);
        Assert.Contains("within 1.5 s", run.Json.GetProperty("failure").GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, run.Json.GetProperty("facts").ValueKind);
        Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task A_timeout_of_zero_means_no_limit_rather_than_no_time()
    {
        Run run = await VerifyAsync("BYBIT", BybitEnv, r => Bybit(r, BybitKeyInfo()), "--timeout", "0");

        Assert.Equal(0, run.Cli.ExitCode);
        Assert.True(Fact(run.Json, "keyAccepted"));
    }

    [Fact]
    public async Task A_missing_environment_file_is_env_file_missing()
    {
        using TempDirectory temp = new();

        CliResult cli = await CliRunner.RunAsync(["verify-keys", "--venue", "BYBIT", "--env-file", temp.Combine("absent.env"), "--json"]);

        Run run = new(cli, Parse(cli), []);
        AssertFailure(run, "env_file_missing", null, null);
        Assert.Equal(JsonValueKind.Null, run.Json.GetProperty("facts").ValueKind);
    }

    [Theory]
    [InlineData("# nothing here\n")]
    [InlineData("BYBIT_API_KEY=\nBYBIT_API_SECRET=\n")]
    [InlineData($"BYBIT_API_KEY={Key}\n")]
    [InlineData($"BINANCE_API_KEY={Key}\nBINANCE_API_SECRET={Secret}\n")]
    public async Task A_file_without_this_venues_key_and_secret_is_no_key_in_file_and_nothing_is_sent(string env)
    {
        Run run = await VerifyAsync("BYBIT", env, r => Bybit(r, BybitKeyInfo()));

        AssertFailure(run, "no_key_in_file", null, null);
        Assert.Empty(run.Requests);
    }

    [Fact]
    public async Task An_unknown_venue_is_venue_unknown()
    {
        Run run = await VerifyAsync("KRAKEN", BybitEnv, r => Bybit(r, BybitKeyInfo()));

        AssertFailure(run, "venue_unknown", null, null);
        Assert.Equal("KRAKEN", run.Json.GetProperty("venue").GetString());
        Assert.Empty(run.Requests);
    }

    [Fact]
    public async Task Without_json_the_checks_are_printed_as_lines_and_the_secret_is_not()
    {
        using TempDirectory temp = new();
        await using LoopbackServer server = new(r => Bybit(r, BybitKeyInfo()));

        CliResult cli = await CliRunner.RunAsync(["verify-keys", "--venue", "BYBIT", "--env-file", temp.File("keys.env", BybitEnv), "--base-url", server.HttpBase]);

        Assert.Equal(0, cli.ExitCode);
        Assert.Contains("ok   auth", cli.StdOut, StringComparison.Ordinal);
        Assert.Contains("no secret was printed", cli.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, cli.AllOutput, StringComparison.Ordinal);
    }

    // ----- KuCoin: a key has three parts, and a version the venue wants to be told -----

    private const string KucoinEnv = $"KUCOIN_API_KEY={Key}\nKUCOIN_API_SECRET={Secret}\nKUCOIN_API_PASSPHRASE=open-sesame\n";
    private const string KucoinAccounts = """{"code":"200000","data":[{"id":"548674591753","currency":"USDT","type":"trade","balance":"26.6","available":"26.6","holds":"0"}]}""";

    private static string KucoinKeyInfo(string permission = "General,Spot", string ipWhitelist = "203.0.113.7", int apiVersion = 3) =>
        $$$"""{"code":"200000","data":{"remark":"bot","apiKey":"6705f5c311545b000157d3eb","apiVersion":{{{apiVersion}}},"permission":"{{{permission}}}","ipWhitelist":"{{{ipWhitelist}}}","createdAt":1728443843000,"uid":165111215,"isMaster":true}}""";

    private static StubResponse Kucoin(RecordedRequest request, string keyInfo) => request.Path switch
    {
        "/api/v1/user/api-key" => StubResponse.Json(keyInfo),
        "/api/v1/accounts" => StubResponse.Json(KucoinAccounts),
        _ => StubResponse.Error(404, "{}"),
    };

    [Fact]
    public async Task A_KuCoin_trading_key_reports_its_facts_and_the_passphrase_never_leaves_in_clear()
    {
        Run run = await VerifyAsync("kucoin", KucoinEnv, r => Kucoin(r, KucoinKeyInfo()));

        Assert.Equal(0, run.Cli.ExitCode);
        Assert.Equal("KUCOIN", run.Json.GetProperty("venue").GetString());
        Assert.True(Fact(run.Json, "keyAccepted"));
        Assert.True(Fact(run.Json, "canTrade"));
        Assert.False(Fact(run.Json, "canWithdraw"));
        Assert.True(Fact(run.Json, "ipRestricted"));
        Assert.True(Fact(run.Json, "markets", "spot"));
        Assert.False(Fact(run.Json, "markets", "futures"));
        Assert.Equal(
            ["env-file", "auth", "permissions", "withdraw", "ip-allow-list", "account"],
            run.Json.GetProperty("checks").EnumerateArray().Select(c => c.GetProperty("name").GetString()));
        RecordedRequest first = run.Requests[0];
        Assert.Equal((Key, "3"), (first.Header("KC-API-KEY"), first.Header("KC-API-KEY-VERSION")));
        Assert.NotEqual("open-sesame", first.Header("KC-API-PASSPHRASE"));
        Assert.DoesNotContain("open-sesame", run.Cli.AllOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, run.Cli.AllOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_KuCoin_key_of_version_2_is_found_without_the_file_saying_so()
    {
        Run run = await VerifyAsync("KUCOIN", KucoinEnv, r => r.Header("KC-API-KEY-VERSION") == "3"
            ? StubResponse.Error(400, """{"code":"400007","msg":"Invalid KC-API-KEY-VERSION"}""")
            : Kucoin(r, KucoinKeyInfo(apiVersion: 2)));

        Assert.Equal(0, run.Cli.ExitCode);
        Assert.True(Fact(run.Json, "keyAccepted"));
        // Version 3 refused, version 2 accepted, and the account read goes out with the version that worked.
        Assert.Equal(["3", "2", "2"], run.Requests.Select(r => r.Header("KC-API-KEY-VERSION")));
    }

    [Fact]
    public async Task A_KuCoin_key_that_may_withdraw_and_is_open_to_every_address_is_reported_as_such()
    {
        Run run = await VerifyAsync("KUCOIN", KucoinEnv, r => Kucoin(r, KucoinKeyInfo("General,Spot,Futures,Withdrawal", string.Empty)));

        Assert.Equal(0, run.Cli.ExitCode);
        Assert.True(Fact(run.Json, "canWithdraw"));
        Assert.False(Fact(run.Json, "ipRestricted"));
        Assert.True(Fact(run.Json, "markets", "futures"));
    }

    [Fact]
    public async Task A_KuCoin_read_only_key_passes_but_cannot_trade()
    {
        Run run = await VerifyAsync("KUCOIN", KucoinEnv, r => Kucoin(r, KucoinKeyInfo("General")));

        Assert.Equal(0, run.Cli.ExitCode);
        Assert.False(Fact(run.Json, "canTrade"));
    }

    // The first two are the live venue's own answers, to a made-up key and to a stale timestamp.
    [Theory]
    [InlineData(401, "400003", "The API key does not exist or site mismatch.", "bad_key", false)]
    [InlineData(400, "400002", "Invalid KC-API-TIMESTAMP.", "clock_skew", null)]
    [InlineData(400, "400004", "Invalid KC-API-PASSPHRASE", "bad_signature", false)]
    [InlineData(400, "400005", "Invalid KC-API-SIGN", "bad_signature", false)]
    [InlineData(403, "400007", "Access Denied", "permission_denied", null)]
    [InlineData(400, "400006", "The requested ip address is not in the api whitelist", "ip_not_allowed", false)]
    [InlineData(500, "500000", "Internal Server Error", "venue_error", null)]
    public async Task A_KuCoin_error_answer_becomes_its_failure_code(int status, string venueCode, string message, string code, bool? keyAccepted)
    {
        Run run = await VerifyAsync("KUCOIN", KucoinEnv, _ => StubResponse.Error(status, $$"""{"code":"{{venueCode}}","msg":"{{message}}"}"""));

        AssertFailure(run, code, status, venueCode);
        if (keyAccepted is null)
        {
            Assert.Equal(JsonValueKind.Null, run.Json.GetProperty("facts").ValueKind);
        }
        else
        {
            Assert.Equal(keyAccepted, Fact(run.Json, "keyAccepted"));
        }
    }

    [Theory]
    [InlineData($"KUCOIN_API_KEY={Key}\nKUCOIN_API_SECRET={Secret}\n")]
    [InlineData($"KUCOIN_API_KEY={Key}\nKUCOIN_API_SECRET={Secret}\nKUCOIN_API_PASSPHRASE=\n")]
    public async Task A_KuCoin_file_without_the_passphrase_is_no_key_in_file_and_nothing_is_sent(string env)
    {
        Run run = await VerifyAsync("KUCOIN", env, r => Kucoin(r, KucoinKeyInfo()));

        AssertFailure(run, "no_key_in_file", null, null);
        Assert.Contains("KUCOIN_API_PASSPHRASE", run.Json.GetProperty("failure").GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(run.Requests);
    }

}
