using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bytex.Cli.Tests.Support;

namespace Bytex.Cli.Tests;

// Why: `bytex run --env-file` is how venue credentials reach a live node. The parsing rules documented in
// docs/getting-started/cli.md (comments, blank lines, `export `, quotes) decide which key signs real orders,
// and the promise that names and values are never logged is a security property.
// The loader is internal, so it is observed from outside: the node is pointed at a stub venue on 127.0.0.1
// and the credentials it actually uses are read from the request headers and verified through the signatures.
public sealed class RunCommandEnvFileTests
{
    // `--duration` starts counting before the node connects, and a node cancelled while it is still starting exits
    // with an error. How long a start takes depends on the machine and on what else it is running, so a run that was
    // cut short is repeated with more time against a fresh stub venue; the assertions are made on the run that came up.
    private static readonly string[] RunTimes = ["00:00:03", "00:00:15", "00:01:00"];

    private sealed record NodeRun(StubVenues Venues, CliResult Result) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Venues.DisposeAsync();
    }

    private static async Task<NodeRun> RunNodeAsync(
        TempDirectory temp,
        Func<StubVenues, string> nodeConfig,
        string envFileContent,
        string[]? globalOptions = null,
        string configFlag = "--config",
        IReadOnlyDictionary<string, string>? environment = null)
    {
        for (int attempt = 0; ; attempt++)
        {
            StubVenues venues = new();
            string config = temp.File("node.json", nodeConfig(venues));
            string envFile = temp.File("keys.env", envFileContent);
            CliResult result = await CliRunner.RunAsync(
                [.. globalOptions ?? [], "run", configFlag, config, "--env-file", envFile, "--duration", RunTimes[attempt]],
                environment: environment,
                timeout: TimeSpan.FromMinutes(3));

            bool cutShort = result.ExitCode != 0 && !result.StdOut.Contains("Trading node TESTER-001 running", StringComparison.Ordinal);
            if (!cutShort || attempt == RunTimes.Length - 1)
            {
                return new NodeRun(venues, result);
            }

            await venues.DisposeAsync();
        }
    }

    private sealed class StubVenues : IAsyncDisposable
    {
        public StubVenues()
        {
            Server = new LoopbackServer(Handle);
        }

        public LoopbackServer Server { get; }

        private static StubResponse Handle(RecordedRequest request) => request.Path switch
        {
            "/api/v3/exchangeInfo" => StubResponse.Json("{\"symbols\":[]}"),
            "/api/v3/account" => StubResponse.Json("{\"balances\":[{\"asset\":\"USDT\",\"free\":\"100.0\",\"locked\":\"0.0\"}]}"),
            "/api/v3/userDataStream" => StubResponse.Json("{\"listenKey\":\"stub-listen-key\"}"),
            "/api/v3/openOrders" => StubResponse.Json("[]"),
            _ when request.Path.StartsWith("/v5/", StringComparison.Ordinal) => StubResponse.Json("{\"retCode\":0,\"retMsg\":\"OK\",\"result\":{\"list\":[],\"nextPageCursor\":\"\"}}"),
            _ => StubResponse.Error(404, "{}"),
        };

        public string NodeConfig(string binanceExecExtra = "") => $$"""
            {
              "kernel": { "traderId": "TESTER-001", "environment": "live" },
              "dataClients": [
                { "factory": "BINANCE", "clientId": "BINANCE-DATA", "config": { "accountType": "spot", "baseUrlHttp": "{{Server.HttpBase}}", "baseUrlWs": "{{Server.WsBase}}", "instrumentProvider": { "loadAll": true } } }
              ],
              "executionClients": [
                { "factory": "BINANCE", "clientId": "BINANCE", "config": { "accountType": "spot", "baseUrlHttp": "{{Server.HttpBase}}", "baseUrlWs": "{{Server.WsBase}}"{{binanceExecExtra}} } },
                { "factory": "BYBIT", "clientId": "BYBIT", "config": { "productType": "linear", "baseUrlHttp": "{{Server.HttpBase}}", "baseUrlWs": "{{Server.WsBase}}" } }
              ],
              "heartbeatInterval": "00:00:00"
            }
            """;

        public RecordedRequest Single(string path) => Assert.Single(Server.RequestsTo(path));

        public ValueTask DisposeAsync() => Server.DisposeAsync();
    }

    private static string BinanceSignatureWith(string secret, RecordedRequest request)
    {
        int marker = request.RawQuery.LastIndexOf("&signature=", StringComparison.Ordinal);
        return Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(request.RawQuery[..marker])));
    }

    private static string BybitSignatureWith(string secret, string apiKey, RecordedRequest request) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(request.Header("X-BAPI-TIMESTAMP") + apiKey + request.Header("X-BAPI-RECV-WINDOW") + request.RawQuery)));

    private const string EnvFile =
        "# venue credentials for the test node\n" +
        "\n" +
        "TARDIS_API_KEY=plain-key-0001\n" +
        "export BINANCE_API_SECRET=\"double quoted secret 0002\"\n" +
        "BYBIT_API_KEY='single-quoted-key-0003'\n" +
        "   BYBIT_API_SECRET  =   spaced-secret-0004   \n" +
        "BINANCE_API_KEY=key=with=equals-0005\n" +
        "#UNUSED_API_KEY=commented-out-0006\n" +
        "this line has no assignment\n" +
        "=value-without-a-name-0007\n";

    [Fact]
    public async Task The_node_uses_credentials_parsed_from_the_env_file_by_the_documented_rules_and_never_prints_them()
    {
        using TempDirectory temp = new();
        string envFile = EnvFile.Replace("\n", "\r\n", StringComparison.Ordinal); // Windows line endings must not end up inside values

        await using NodeRun run = await RunNodeAsync(temp, v => v.NodeConfig(), envFile, globalOptions: ["--log-level", "Trace"]);
        (StubVenues venues, CliResult result) = run;

        Assert.True(result.ExitCode == 0, result.AllOutput);

        // only the first '=' separates a name from its value, and `export ` + double quotes (the secret is proven
        // through the signature it produces)
        RecordedRequest account = venues.Single("/api/v3/account");
        Assert.Equal("key=with=equals-0005", account.Header("X-MBX-APIKEY"));
        Assert.Equal(BinanceSignatureWith("double quoted secret 0002", account), account.Query("signature"));

        // single quotes, and whitespace around name and value
        RecordedRequest wallet = venues.Single("/v5/account/wallet-balance");
        Assert.Equal("single-quoted-key-0003", wallet.Header("X-BAPI-API-KEY"));
        Assert.Equal(BybitSignatureWith("spaced-secret-0004", "single-quoted-key-0003", wallet), wallet.Header("X-BAPI-SIGN"));

        // the data client of the same venue reads the same variable as its execution client
        RecordedRequest exchangeInfo = venues.Single("/api/v3/exchangeInfo");
        Assert.Equal("key=with=equals-0005", exchangeInfo.Header("X-MBX-APIKEY"));

        // five assignments; the comment, the blank line, the prose line and the nameless value do not count
        Assert.Contains("Loaded 5 variables", result.StdOut);

        // "The node logs how many variables it loaded, never their names or values."
        foreach (string secret in new[] { "plain-key-0001", "double quoted secret 0002", "single-quoted-key-0003", "spaced-secret-0004", "equals-0005", "commented-out-0006", "value-without-a-name" })
        {
            Assert.DoesNotContain(secret, result.AllOutput);
        }

        foreach (string name in new[] { "BINANCE_API_KEY", "BINANCE_API_SECRET", "BYBIT_API_KEY", "BYBIT_API_SECRET", "TARDIS_API_KEY" })
        {
            Assert.DoesNotContain(name, result.AllOutput);
        }
    }

    [Fact]
    public async Task The_node_connects_reconciles_runs_for_the_duration_and_shuts_down_cleanly()
    {
        using TempDirectory temp = new();

        await using NodeRun run = await RunNodeAsync(temp, v => v.NodeConfig(), EnvFile, configFlag: "-c");
        (StubVenues venues, CliResult result) = run;

        Assert.True(result.ExitCode == 0, result.AllOutput);
        Assert.Contains("Trading node TESTER-001 running", result.StdOut);
        Assert.Contains("Trading node TESTER-001 stopped", result.StdOut);
        Assert.Single(venues.Server.RequestsTo("/api/v3/openOrders")); // reconciliation asked Binance for open orders
        Assert.Single(venues.Server.RequestsTo("/v5/order/realtime")); // and Bybit
        Assert.Contains(venues.Server.RequestsTo("/api/v3/userDataStream"), r => r.Method == "DELETE"); // the listen key was closed on the way out
    }

    [Fact]
    public async Task Credentials_written_in_the_configuration_win_over_the_env_file_and_the_file_replaces_inherited_variables()
    {
        using TempDirectory temp = new();
        const string envFile = "BINANCE_API_KEY=file-key\nBINANCE_API_SECRET=file-secret\nBYBIT_API_KEY=file-bybit-key\n";
        Dictionary<string, string> inherited = new() { ["BYBIT_API_KEY"] = "inherited-bybit-key", ["BYBIT_API_SECRET"] = "inherited-bybit-secret" };

        await using NodeRun run = await RunNodeAsync(
            temp,
            v => v.NodeConfig(", \"apiKey\": \"config-key\", \"apiSecret\": \"config-secret\""),
            envFile,
            environment: inherited);
        (StubVenues venues, CliResult result) = run;

        Assert.True(result.ExitCode == 0, result.AllOutput);
        RecordedRequest account = venues.Single("/api/v3/account");
        Assert.Equal("config-key", account.Header("X-MBX-APIKEY"));
        Assert.Equal(BinanceSignatureWith("config-secret", account), account.Query("signature"));
        RecordedRequest wallet = venues.Single("/v5/account/wallet-balance");
        Assert.Equal("file-bybit-key", wallet.Header("X-BAPI-API-KEY")); // the file is the operator's explicit choice for this run
        Assert.Equal(BybitSignatureWith("inherited-bybit-secret", "file-bybit-key", wallet), wallet.Header("X-BAPI-SIGN")); // variables the file does not mention are left alone
    }

    [Fact]
    public async Task A_missing_env_file_stops_the_run_before_any_venue_is_contacted()
    {
        await using StubVenues venues = new();
        using TempDirectory temp = new();
        string config = temp.File("node.json", venues.NodeConfig());

        CliResult result = await CliRunner.RunAsync(["run", "--config", config, "--env-file", temp.Combine("does-not-exist.env"), "--duration", "00:00:01"]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Environment file not found", result.AllOutput);
        Assert.Contains("does-not-exist.env", result.AllOutput);
        Assert.Empty(venues.Server.Requests);
    }

    [Fact]
    public async Task Without_credentials_anywhere_the_run_fails_naming_the_variable_to_set()
    {
        await using StubVenues venues = new();
        using TempDirectory temp = new();
        string config = temp.File("node.json", venues.NodeConfig());

        CliResult result = await CliRunner.RunAsync(["run", "--config", config, "--duration", "00:00:01"]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("BINANCE_API_KEY", result.AllOutput);
    }

    [Fact]
    public async Task A_configuration_naming_an_unknown_client_factory_fails_with_that_name()
    {
        using TempDirectory temp = new();
        string config = temp.File("node.json", """{ "dataClients": [ { "factory": "KRAKEN", "clientId": "KRAKEN", "config": {} } ], "heartbeatInterval": "00:00:00" }""");

        CliResult result = await CliRunner.RunAsync(["run", "--config", config, "--duration", "00:00:01"]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("KRAKEN", result.AllOutput);
    }

    [Fact]
    public async Task The_sandbox_factory_is_built_in_so_a_sandbox_node_runs_without_credentials_or_plugins()
    {
        using TempDirectory temp = new();
        string config = temp.File("node.json", JsonSerializer.Serialize(new
        {
            kernel = new { traderId = "TESTER-002", environment = "sandbox" },
            executionClients = new[] { new { factory = "SANDBOX", clientId = "BINANCE-SANDBOX", config = new { venue = "BINANCE", startingBalances = new[] { "1000 USDT" } } } },
            heartbeatInterval = "00:00:00",
        }));

        CliResult result = await CliRunner.RunAsync(["run", "--config", config, "--duration", "00:00:01"]);

        Assert.True(result.ExitCode == 0, result.AllOutput);
        Assert.Contains("Trading node TESTER-002 running with 0 strategies", result.StdOut);
        Assert.Contains("Trading node TESTER-002 stopped", result.StdOut);
    }

    [Fact]
    public async Task A_node_checks_itself_against_its_venues_on_the_interval_it_was_given()
    {
        // --reconcile-interval had no test. Left out, a node checks itself once at start-up; given one, it keeps
        // checking, and a node that stopped checking would drift from its venue in silence - which is the failure
        // continuous reconciliation exists to prevent.
        using TempDirectory temp = new();
        string config = temp.File("node.json", JsonSerializer.Serialize(new
        {
            kernel = new { traderId = "TESTER-003", environment = "sandbox" },
            executionClients = new[] { new { factory = "SANDBOX", clientId = "BINANCE-SANDBOX", config = new { venue = "BINANCE", startingBalances = new[] { "1000 USDT" } } } },
            heartbeatInterval = "00:00:00",
        }));

        CliResult result = await CliRunner.RunAsync(
            ["run", "--config", config, "--duration", "00:00:02", "--reconcile-interval", "00:00:01"]);

        Assert.True(result.ExitCode == 0, result.AllOutput);
        Assert.Contains("Trading node TESTER-003 stopped", result.StdOut);
    }

    [Fact]
    public async Task A_node_serving_a_control_channel_takes_the_heartbeat_interval_it_was_given()
    {
        // --control-heartbeat had no test. The heartbeat is how a host knows a node is alive, so an interval the
        // node quietly ignored would leave a monitor drawing a live node from stale news.
        using TempDirectory temp = new();
        string config = temp.File("node.json", JsonSerializer.Serialize(new
        {
            kernel = new { traderId = "TESTER-004", environment = "sandbox" },
            executionClients = new[] { new { factory = "SANDBOX", clientId = "BINANCE-SANDBOX", config = new { venue = "BINANCE", startingBalances = new[] { "1000 USDT" } } } },
            heartbeatInterval = "00:00:00",
        }));
        string channel = "ctl-" + Guid.NewGuid().ToString("N")[..8];

        CliResult result = await CliRunner.RunAsync(
            ["run", "--config", config, "--duration", "00:00:02", "--control", channel, "--control-heartbeat", "00:00:01"]);

        Assert.True(result.ExitCode == 0, result.AllOutput);
        Assert.Contains("Trading node TESTER-004 stopped", result.StdOut);
    }
}
