using Bytex.Adapters.Binance;
using Bytex.Adapters.Bybit;
using Bytex.Adapters.Tardis;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Model.Identifiers;

namespace Bytex.Adapters.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EnvironmentCollection
{
    public const string Name = "Process environment";
}

// Why: credentials normally arrive through environment variables (that is what `bytex run --env-file` sets).
// A missing key must be reported by the name of the variable it was looked for in.
// These tests change process-wide variables, so they run alone and restore every value they touch.
[Collection(EnvironmentCollection.Name)]
public sealed class CredentialResolutionTests : IDisposable
{
    private static readonly string[] _variables =
    [
        BinanceVenue.EnvApiKey, BinanceVenue.EnvApiSecret,
        BybitVenue.EnvApiKey, BybitVenue.EnvApiSecret,
        TardisDataClient.EnvApiKey,
    ];

    private readonly Dictionary<string, string?> _saved = _variables.ToDictionary(v => v, Environment.GetEnvironmentVariable);

    public CredentialResolutionTests()
    {
        foreach (string variable in _variables)
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    public void Dispose()
    {
        foreach ((string variable, string? value) in _saved)
        {
            Environment.SetEnvironmentVariable(variable, value);
        }
    }

    [Fact]
    public void The_variable_names_are_the_documented_ones()
    {
        Assert.Equal("BINANCE_API_KEY", BinanceVenue.EnvApiKey);
        Assert.Equal("BINANCE_API_SECRET", BinanceVenue.EnvApiSecret);

        Assert.Equal("BYBIT_API_KEY", BybitVenue.EnvApiKey);
        Assert.Equal("BYBIT_API_SECRET", BybitVenue.EnvApiSecret);

        Assert.Equal("TARDIS_API_KEY", TardisDataClient.EnvApiKey);
    }

    [Fact]
    public void Binance_credentials_come_from_the_venues_own_variables()
    {
        Environment.SetEnvironmentVariable("BINANCE_API_KEY", "the-key");
        Environment.SetEnvironmentVariable("BINANCE_API_SECRET", "the-secret");

        Assert.Equal(("the-key", "the-secret"), BinanceVenue.Credentials(new BinanceExecutionClientConfig()));
    }

    [Fact]
    public void Bybit_credentials_come_from_the_venues_own_variables()
    {
        Environment.SetEnvironmentVariable("BYBIT_API_KEY", "the-key");
        Environment.SetEnvironmentVariable("BYBIT_API_SECRET", "the-secret");

        Assert.Equal(("the-key", "the-secret"), BybitVenue.Credentials(new BybitExecutionClientConfig()));
    }

    [Fact]
    public void Configured_credentials_win_over_the_environment()
    {
        Environment.SetEnvironmentVariable("BYBIT_API_KEY", "env-key");
        Environment.SetEnvironmentVariable("BYBIT_API_SECRET", "env-secret");

        Assert.Equal(("config-key", "config-secret"), BybitVenue.Credentials(new BybitExecutionClientConfig { ApiKey = "config-key", ApiSecret = "config-secret" }));
    }

    [Fact]
    public void A_binance_execution_client_without_credentials_fails_naming_the_variable_to_set()
    {
        using TestKernel kernel = new();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => new BinanceExecutionClient(new ClientId("BINANCE"), new BinanceExecutionClientConfig { BaseUrlHttp = "http://127.0.0.1:9" }, kernel.Services));

        Assert.Contains("BINANCE_API_KEY", error.Message);
    }

    [Fact]
    public void A_bybit_execution_client_without_credentials_fails_naming_the_variable_to_set()
    {
        using TestKernel kernel = new();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => new BybitExecutionClient(new ClientId("BYBIT"), new BybitExecutionClientConfig { BaseUrlHttp = "http://127.0.0.1:9" }, kernel.Services));

        Assert.Contains("BYBIT_API_KEY", error.Message);
    }

    [Fact]
    public void A_tardis_client_without_a_key_fails_naming_the_variable_to_set()
    {
        using TestKernel kernel = new();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => new TardisDataClient(new ClientId("TARDIS"), new TardisDataClientConfig { BaseUrl = "http://127.0.0.1:9/v1" }, kernel.Services));

        Assert.Contains("TARDIS_API_KEY", error.Message);
    }

    [Fact]
    public async Task Public_binance_requests_work_without_any_credentials_and_send_no_key_header()
    {
        await using LoopbackServer server = new(_ => StubResponse.Json(Fixtures.BinancePayloads.SpotExchangeInfo));
        using BinanceHttp http = new(new BinanceDataClientConfig { BaseUrlHttp = server.HttpBase });

        await http.GetPublicAsync("/api/v3/exchangeInfo");

        Assert.False(http.HasCredentials);
        Assert.Null(Assert.Single(server.Requests).Header("X-MBX-APIKEY"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => http.GetSignedAsync("/api/v3/account"));
    }

    [Fact]
    public async Task Binance_credentials_from_the_environment_sign_requests_exactly_like_configured_ones()
    {
        Environment.SetEnvironmentVariable("BINANCE_API_KEY", "env-key");
        Environment.SetEnvironmentVariable("BINANCE_API_SECRET", "env-secret");
        await using LoopbackServer server = new(_ => StubResponse.Json("{}"));
        using BinanceHttp http = new(new BinanceExecutionClientConfig { BaseUrlHttp = server.HttpBase }, requireCredentials: true);

        await http.GetSignedAsync("/api/v3/account", new Dictionary<string, string> { ["omitZeroBalances"] = "true" });

        RecordedRequest request = Assert.Single(server.Requests);
        int marker = request.RawQuery.LastIndexOf("&signature=", StringComparison.Ordinal);
        string expected = Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData("env-secret"u8, System.Text.Encoding.UTF8.GetBytes(request.RawQuery[..marker])));
        Assert.Equal("env-key", request.Header("X-MBX-APIKEY"));
        Assert.Equal(expected, request.RawQuery[(marker + 11)..]);
        Assert.StartsWith("omitZeroBalances=true&timestamp=", request.RawQuery);
    }
}
