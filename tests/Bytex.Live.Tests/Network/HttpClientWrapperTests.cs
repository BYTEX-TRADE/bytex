using System.Net;
using System.Text;
using Bytex.Live.Network;
using Bytex.Live.Tests.Support;

namespace Bytex.Live.Tests.Network;

// Why: every venue adapter sends its REST traffic through this wrapper, so query encoding, header handling,
// the retry rules and the error surface decide whether orders reach the venue once, twice, or never.
// The "venue" is a stub bound to 127.0.0.1; nothing leaves the machine.
public sealed class HttpClientWrapperTests
{
    private static readonly RetryPolicy _fastRetry = new(3, TimeSpan.FromMilliseconds(1), 1.0, TimeSpan.FromMilliseconds(1));

    [Fact]
    public void BuildQuery_percent_encodes_keys_and_values_and_keeps_insertion_order()
    {
        Dictionary<string, string> query = new()
        {
            ["symbol"] = "BTC/USDT",
            ["note"] = "a b&c=d",
            ["price"] = "0.10",
            ["k y"] = "ü",
        };

        string text = HttpClientWrapper.BuildQuery(query);

        Assert.Equal("symbol=BTC%2FUSDT&note=a%20b%26c%3Dd&price=0.10&k%20y=%C3%BC", text);
    }

    [Fact]
    public async Task Get_sends_path_query_default_headers_and_request_headers_to_the_base_url()
    {
        await using LoopbackServer server = new(_ => StubResponse.Json("{\"ok\":true}"));
        using HttpClientWrapper http = new(new Uri(server.HttpBase), retry: RetryPolicy.None, defaultHeaders: new Dictionary<string, string> { ["X-Default"] = "d1" });

        string body = await http.GetAsync("/api/v3/ping", new Dictionary<string, string> { ["a"] = "1", ["b"] = "x y" }, new Dictionary<string, string> { ["X-Request"] = "r1" });

        RecordedRequest request = Assert.Single(server.Requests);
        Assert.Equal("{\"ok\":true}", body);
        Assert.Equal("GET", request.Method);
        Assert.Equal("/api/v3/ping", request.Path);
        Assert.Equal("a=1&b=x%20y", request.RawQuery);
        Assert.Equal("d1", request.Header("X-Default"));
        Assert.Equal("r1", request.Header("X-Request"));
        Assert.StartsWith("bytex/", request.Header("User-Agent"));
    }

    [Fact]
    public async Task Query_is_appended_with_an_ampersand_when_the_path_already_has_one()
    {
        await using LoopbackServer server = new(_ => StubResponse.Json("[]"));
        using HttpClientWrapper http = new(new Uri(server.HttpBase), retry: RetryPolicy.None);

        await http.GetAsync("/v1/items?fixed=1", new Dictionary<string, string> { ["extra"] = "2" });

        Assert.Equal("fixed=1&extra=2", Assert.Single(server.Requests).RawQuery);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Verb_helpers_use_the_matching_http_method_and_deliver_the_body(string verb)
    {
        await using LoopbackServer server = new(_ => StubResponse.Json("{}"));
        using HttpClientWrapper http = new(new Uri(server.HttpBase), retry: RetryPolicy.None);
        using StringContent content = new("{\"qty\":\"1\"}", Encoding.UTF8, "application/json");

        _ = verb switch
        {
            "POST" => await http.PostAsync("/order", body: content),
            "PUT" => await http.PutAsync("/order", body: content),
            _ => await http.DeleteAsync("/order", body: content),
        };

        RecordedRequest request = Assert.Single(server.Requests);
        Assert.Equal(verb, request.Method);
        Assert.Equal("{\"qty\":\"1\"}", request.Body);
        Assert.StartsWith("application/json", request.Header("Content-Type"));
    }

    [Theory]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(429)]
    [InlineData(418)]
    public async Task Server_errors_and_rate_limit_statuses_are_retried_until_a_success(int status)
    {
        int calls = 0;
        await using LoopbackServer server = new(_ => Interlocked.Increment(ref calls) < 3 ? StubResponse.Error(status, "{\"msg\":\"busy\"}") : StubResponse.Json("{\"done\":1}"));
        using HttpClientWrapper http = new(new Uri(server.HttpBase), retry: _fastRetry);

        string body = await http.GetAsync("/x");

        Assert.Equal("{\"done\":1}", body);
        Assert.Equal(3, server.Requests.Count);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    public async Task Client_errors_are_not_retried_and_carry_status_and_body(int status)
    {
        await using LoopbackServer server = new(_ => StubResponse.Error(status, "{\"code\":-2010,\"msg\":\"Account has insufficient balance\"}"));
        using HttpClientWrapper http = new(new Uri(server.HttpBase), retry: _fastRetry);

        VenueHttpException error = await Assert.ThrowsAsync<VenueHttpException>(() => http.GetAsync("/order"));

        Assert.Single(server.Requests);
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Equal("{\"code\":-2010,\"msg\":\"Account has insufficient balance\"}", error.Body);
        Assert.False(error.IsRateLimited);
    }

    [Fact]
    public async Task A_persistent_server_error_is_attempted_exactly_MaxAttempts_times_then_thrown()
    {
        await using LoopbackServer server = new(_ => StubResponse.Error(502, "bad gateway"));
        using HttpClientWrapper http = new(new Uri(server.HttpBase), retry: _fastRetry);

        VenueHttpException error = await Assert.ThrowsAsync<VenueHttpException>(() => http.GetAsync("/x"));

        Assert.Equal(3, server.Requests.Count);
        Assert.Equal(HttpStatusCode.BadGateway, error.StatusCode);
    }

    [Theory]
    [InlineData(429, true)]
    [InlineData(418, true)]
    [InlineData(500, false)]
    [InlineData(403, false)]
    public void IsRateLimited_is_true_only_for_429_and_the_binance_ban_status_418(int status, bool expected)
    {
        VenueHttpException error = new((HttpStatusCode)status, string.Empty, "x");

        Assert.Equal(expected, error.IsRateLimited);
    }

    [Fact]
    public async Task A_request_with_a_body_is_resent_intact_when_the_first_attempt_hits_a_server_error()
    {
        int calls = 0;
        await using LoopbackServer server = new(_ => Interlocked.Increment(ref calls) == 1 ? StubResponse.Error(500, "{}") : StubResponse.Json("{\"ok\":1}"));
        using HttpClientWrapper http = new(new Uri(server.HttpBase), retry: _fastRetry);
        using StringContent content = new("{\"symbol\":\"BTCUSDT\"}", Encoding.UTF8, "application/json");

        string body = await http.PostAsync("/v5/order/create", body: content);

        Assert.Equal("{\"ok\":1}", body);
        Assert.Equal(2, server.Requests.Count);
        Assert.All(server.Requests, r => Assert.Equal("{\"symbol\":\"BTCUSDT\"}", r.Body));
        Assert.All(server.Requests, r => Assert.StartsWith("application/json", r.Header("Content-Type"), StringComparison.Ordinal));
        Assert.Equal("{\"symbol\":\"BTCUSDT\"}", await content.ReadAsStringAsync()); // the caller's content is still the caller's
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task A_rate_limited_request_with_a_body_is_resent_intact(string method)
    {
        int calls = 0;
        await using LoopbackServer server = new(_ => Interlocked.Increment(ref calls) == 1 ? StubResponse.Error(429, "{}") : StubResponse.Json("{\"ok\":1}"));
        using HttpClientWrapper http = new(new Uri(server.HttpBase), retry: _fastRetry);
        using StringContent content = new("{\"orderId\":\"42\"}", Encoding.UTF8, "application/json");

        string body = method == "PUT" ? await http.PutAsync("/v5/order/amend", body: content) : await http.DeleteAsync("/v5/order/cancel", body: content);

        Assert.Equal("{\"ok\":1}", body);
        Assert.Equal(2, server.Requests.Count);
        Assert.All(server.Requests, r => Assert.Equal(method, r.Method));
        Assert.All(server.Requests, r => Assert.Equal("{\"orderId\":\"42\"}", r.Body));
    }

    [Fact]
    public async Task A_cancelled_token_stops_the_request_instead_of_retrying()
    {
        TaskCompletionSource firstAttempt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using LoopbackServer server = new(_ =>
        {
            firstAttempt.TrySetResult();
            return StubResponse.Error(500, "{}");
        });
        using HttpClientWrapper http = new(new Uri(server.HttpBase), retry: new RetryPolicy(5, TimeSpan.FromSeconds(30), 1.0, TimeSpan.FromSeconds(30)));
        using CancellationTokenSource cts = new();

        Task<string> pending = http.GetAsync("/x", ct: cts.Token);
        await firstAttempt.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task GetJsonAsync_parses_the_response_body()
    {
        await using LoopbackServer server = new(_ => StubResponse.Json("{\"serverTime\":1499827319559}"));
        using HttpClientWrapper http = new(new Uri(server.HttpBase), retry: RetryPolicy.None);

        using System.Text.Json.JsonDocument doc = await http.GetJsonAsync("/api/v3/time");

        Assert.Equal(1499827319559L, doc.RootElement.GetProperty("serverTime").GetInt64());
    }
}
