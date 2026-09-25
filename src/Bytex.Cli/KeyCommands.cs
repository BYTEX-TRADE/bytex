using System.CommandLine;
using System.Globalization;
using System.Net;
using System.Security.Authentication;
using System.Text.Json;
using Bytex.Adapters.Binance;
using Bytex.Adapters.Bybit;
using Bytex.Adapters.Gate;
using Bytex.Adapters.Kucoin;
using Bytex.Live.Network;
using Microsoft.Extensions.Logging;

namespace Bytex.Cli;

/// <summary>
/// <c>bytex verify-keys</c>: checks a venue API key from an environment file, in this process only, and reports what the key
/// can do. A host application runs this as a separate process so it never handles the key itself.
/// </summary>
internal static class KeyCommands
{
    /// <summary>The venue codes this check reads rather than only reporting: what each one means is in the tables below.</summary>
    private const int BybitPermissionDenied = 10005;

    /// <summary>Binance answers a banned key with 418, which is not in the enum and means the same as 429 here.</summary>
    private const int BinanceTeapot = 418;

    /// <summary>An expiry before this year is the venue saying "no expiry" in a date field.</summary>
    private const int EarliestRealExpiryYear = 2000;

    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static Command Build(Option<string> logLevel)
    {
        Option<string> venue = new("--venue") { Description = "BINANCE | BYBIT | GATE | KUCOIN", Required = true };
        Option<string> envFile = new("--env-file") { Description = "File with KEY=VALUE lines (for example BYBIT_API_KEY=...; KuCoin also needs KUCOIN_API_PASSPHRASE)", Required = true };
        Option<bool> json = new("--json") { Description = "Print the result as JSON and nothing else on standard output" };
        Option<string?> baseUrl = new("--base-url") { Description = "Override the venue's REST address (a proxy or a test venue)" };
        Option<double> timeout = new("--timeout") { Description = "Seconds the whole check may take before it is reported as unreachable; 0 waits for as long as the venue takes", DefaultValueFactory = _ => 30 };
        Command command = new("verify-keys", "Check a venue API key: authentication, permissions, withdrawal rights, IP restrictions");
        command.Options.Add(venue);
        command.Options.Add(envFile);
        command.Options.Add(json);
        command.Options.Add(baseUrl);
        command.Options.Add(timeout);
        command.SetAction(async (parseResult, ct) =>
        {
            bool asJson = parseResult.GetValue(json);
            LogLevel level = Enum.TryParse(parseResult.GetValue(logLevel), true, out LogLevel l) ? l : LogLevel.Warning;
            using ILoggerFactory loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(level).AddConsole(o => o.LogToStandardErrorThreshold = asJson ? LogLevel.Trace : LogLevel.None));
            string venueName = parseResult.GetValue(venue)!.ToUpperInvariant();
            Report report = new(venueName);
            double seconds = parseResult.GetValue(timeout);
            using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (seconds > 0)
            {
                limit.CancelAfter(TimeSpan.FromSeconds(seconds));
            }

            try
            {
                int loaded = EnvFile.Load(parseResult.GetValue(envFile)!);
                report.Checks.Add(new Check("ok", "env-file", $"{loaded} variables loaded"));
                switch (venueName)
                {
                    case "BYBIT":
                        RequireKey(BybitVenue.EnvApiKey, BybitVenue.EnvApiSecret);
                        await VerifyBybitAsync(parseResult.GetValue(baseUrl), loggerFactory, report, limit.Token).ConfigureAwait(false);
                        break;
                    case "BINANCE":
                        RequireKey(BinanceVenue.EnvApiKey, BinanceVenue.EnvApiSecret);
                        await VerifyBinanceAsync(parseResult.GetValue(baseUrl), loggerFactory, report, limit.Token).ConfigureAwait(false);
                        break;
                    case "KUCOIN":
                        RequireKey(KucoinVenue.EnvApiKey, KucoinVenue.EnvApiSecret);
                        RequireKey(KucoinVenue.EnvApiPassphrase, KucoinVenue.EnvApiPassphrase);
                        await VerifyKucoinAsync(parseResult.GetValue(baseUrl), loggerFactory, report, limit.Token).ConfigureAwait(false);
                        break;
                    case "GATE":
                        RequireKey(GateVenue.EnvApiKey, GateVenue.EnvApiSecret);
                        await VerifyGateAsync(parseResult.GetValue(baseUrl), loggerFactory, report, limit.Token).ConfigureAwait(false);
                        break;
                    default:
                        report.Fail("venue", new Failure("venue_unknown", null, null, "expected BINANCE, BYBIT, GATE or KUCOIN"));
                        break;
                }
            }
#pragma warning disable CA1031 // The command's job is to turn every failure into a report; nothing may escape as a stack trace.
            catch (Exception e)
#pragma warning restore CA1031
            {
                bool timedOut = e is OperationCanceledException && limit.IsCancellationRequested && !ct.IsCancellationRequested;
                Failure failure = timedOut
                    ? new Failure("unreachable", null, null, string.Create(CultureInfo.InvariantCulture, $"no answer from the venue within {seconds} s"))
                    : Classify(e);
                if (failure.Code is "bad_key" or "bad_key_or_ip" or "bad_signature" or "key_expired" or "ip_not_allowed")
                {
                    report.KeyAccepted = false;
                }

                report.Fail("auth", failure);
            }

            bool failed = report.Checks.Any(c => c.Status == "fail");
            if (asJson)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { ok = !failed, venue = report.Venue, facts = report.Facts(), failure = report.Failure, checks = report.Checks }, _json));
            }
            else
            {
                foreach (Check c in report.Checks)
                {
                    Console.WriteLine($"{c.Status,-4} {c.Name} · {c.Detail}");
                }

                Console.WriteLine("no secret was printed");
            }

            return failed ? 1 : 0;
        });
        return command;
    }

    private sealed record Check(string Status, string Name, string Detail);

    private sealed record Markets(bool? Spot, bool? Futures);

    private sealed record KeyFacts(bool KeyAccepted, bool? CanTrade, bool? CanWithdraw, bool? IpRestricted, Markets Markets, string? KeyExpiresAt);

    private sealed record Failure(string Code, int? HttpStatus, string? VenueCode, string Message);

    private sealed class MissingKeyException(string message) : Exception(message);

    private sealed class Report(string venue)
    {
        public string Venue { get; } = venue;


        public List<Check> Checks { get; } = new();

        public Failure? Failure { get; private set; }

        public bool? KeyAccepted { get; set; }

        public bool? CanTrade { get; set; }

        public bool? CanWithdraw { get; set; }

        public bool? IpRestricted { get; set; }

        public bool? Spot { get; set; }

        public bool? Futures { get; set; }

        public string? KeyExpiresAt { get; set; }

        public void Fail(string check, Failure failure)
        {
            Failure ??= failure;
            Checks.Add(new Check("fail", check, failure.Message));
        }

        public KeyFacts? Facts() =>
            KeyAccepted is null ? null : new KeyFacts(KeyAccepted.Value, CanTrade, CanWithdraw, IpRestricted, new Markets(Spot, Futures), KeyExpiresAt);
    }

    private static void RequireKey(string keyVariable, string secretVariable)
    {
        foreach (string variable in new[] { keyVariable, secretVariable })
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
            {
                throw new MissingKeyException($"{variable} is not set in the environment file");
            }
        }
    }

    private static async Task VerifyBybitAsync(string? baseUrl, ILoggerFactory loggerFactory, Report report, CancellationToken ct)
    {
        BybitExecutionClientConfig config = new() { BaseUrlHttp = baseUrl };
        using BybitHttp http = new(config, loggerFactory.CreateLogger("bybit"), requireCredentials: true);
        JsonElement response = await http.GetSignedAsync("/v5/user/query-api", null, ct).ConfigureAwait(false);
        JsonElement result = response.TryGetProperty("result", out JsonElement r) ? r : response;
        string id = result.TryGetProperty("id", out JsonElement idEl) ? idEl.ToString() : "?";
        report.KeyAccepted = true;
        report.Checks.Add(new Check("ok", "auth", $"key id {Mask(id)}"));

        bool readOnly = result.TryGetProperty("readOnly", out JsonElement ro) && ro.ValueKind == JsonValueKind.Number && ro.GetInt32() == 1;
        List<string> groups = new();
        bool withdraw = false;
        bool spot = false;
        bool futures = false;
        bool hasPermissions = result.TryGetProperty("permissions", out JsonElement permissions) && permissions.ValueKind == JsonValueKind.Object;
        if (hasPermissions)
        {
            foreach (JsonProperty group in permissions.EnumerateObject())
            {
                if (group.Value.ValueKind == JsonValueKind.Array && group.Value.GetArrayLength() > 0)
                {
                    groups.Add(group.Name);
                    foreach (JsonElement item in group.Value.EnumerateArray())
                    {
                        string permission = item.ToString();
                        withdraw |= permission.Contains("withdraw", StringComparison.OrdinalIgnoreCase);
                        spot |= permission.Equals("SpotTrade", StringComparison.OrdinalIgnoreCase);
                        futures |= group.Name is "ContractTrade" or "Derivatives" && permission is "Order" or "Position" or "DerivativesTrade";
                    }
                }
            }

            report.CanWithdraw = withdraw;
            report.Spot = spot && !readOnly;
            report.Futures = futures && !readOnly;
            report.CanTrade = !readOnly && (spot || futures);
        }
        else if (readOnly)
        {
            report.CanTrade = false;
        }

        report.Checks.Add(new Check("ok", "permissions", (readOnly ? "read-only" : "trade") + (groups.Count > 0 ? " · " + string.Join(", ", groups) : string.Empty)));
        if (hasPermissions)
        {
            report.Checks.Add(new Check(withdraw ? "warn" : "ok", "withdraw", withdraw ? "withdrawal permission PRESENT; create a key without it" : "withdrawal permission absent"));
        }

        if (result.TryGetProperty("ips", out JsonElement ipsEl) && ipsEl.ValueKind == JsonValueKind.Array)
        {
            List<string> ips = ipsEl.EnumerateArray().Select(i => i.ToString()).Where(i => i.Length > 0).ToList();
            bool restricted = ips.Count > 0 && !ips.Contains("*");
            report.IpRestricted = restricted;
            report.Checks.Add(new Check(restricted ? "ok" : "warn", "ip-allow-list", restricted ? string.Join(", ", ips) : "no IP restriction; restrict the key to this machine"));
        }

        if (result.TryGetProperty("expiredAt", out JsonElement expiry) && expiry.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(expiry.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset expiresAt)
            && expiresAt.Year > EarliestRealExpiryYear)
        {
            report.KeyExpiresAt = expiresAt.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        }

        try
        {
            await http.GetSignedAsync("/v5/account/wallet-balance", new Dictionary<string, string> { ["accountType"] = "UNIFIED" }, ct).ConfigureAwait(false);
            report.Checks.Add(new Check("ok", "account", "unified wallet readable"));
        }
        catch (BybitApiException e) when (e.Code != BybitPermissionDenied)
        {
            report.Checks.Add(new Check("warn", "account", "wallet query returned " + e.Code.ToString(CultureInfo.InvariantCulture) + ": " + e.RetMsg));
        }
    }

    private static async Task VerifyBinanceAsync(string? baseUrl, ILoggerFactory loggerFactory, Report report, CancellationToken ct)
    {
        BinanceDataClientConfig config = new() { AccountType = BinanceAccountType.Spot, BaseUrlHttp = baseUrl };
        using BinanceHttp http = new(config, loggerFactory.CreateLogger("binance"), requireCredentials: true);
        using JsonDocument account = await http.GetSignedAsync("/api/v3/account", null, BinanceVenue.Weights.Account, ct).ConfigureAwait(false);
        JsonElement root = account.RootElement;
        report.KeyAccepted = true;
        report.Checks.Add(new Check("ok", "auth", "account readable"));
        if (root.TryGetProperty("permissions", out JsonElement perms) && perms.ValueKind == JsonValueKind.Array)
        {
            report.Checks.Add(new Check("ok", "permissions", string.Join(", ", perms.EnumerateArray().Select(p => p.ToString()))));
        }

        try
        {
            using JsonDocument restrictions = await http.GetSignedAsync("/sapi/v1/account/apiRestrictions", null, 1, ct).ConfigureAwait(false);
            JsonElement rr = restrictions.RootElement;
            bool withdrawals = rr.TryGetProperty("enableWithdrawals", out JsonElement w) && w.ValueKind == JsonValueKind.True;
            bool ipRestrict = rr.TryGetProperty("ipRestrict", out JsonElement ip) && ip.ValueKind == JsonValueKind.True;
            bool trading = rr.TryGetProperty("enableSpotAndMarginTrading", out JsonElement t) && t.ValueKind == JsonValueKind.True;
            bool futures = rr.TryGetProperty("enableFutures", out JsonElement f) && f.ValueKind == JsonValueKind.True;
            report.CanWithdraw = withdrawals;
            report.IpRestricted = ipRestrict;
            report.Spot = trading;
            report.Futures = futures;
            report.CanTrade = trading || futures;
            report.Checks.Add(new Check(withdrawals ? "warn" : "ok", "withdraw", withdrawals ? "withdrawals ENABLED; create a key without them" : "withdrawals disabled"));
            report.Checks.Add(new Check(ipRestrict ? "ok" : "warn", "ip-allow-list", ipRestrict ? "restricted to trusted IPs" : "no IP restriction; restrict the key to this machine"));
            report.Checks.Add(new Check("ok", "trading", $"spot/margin {(trading ? "on" : "off")}, futures {(futures ? "on" : "off")}"));
        }
        catch (Exception e) when (e is VenueHttpException or HttpRequestException or InvalidOperationException or JsonException)
        {
            report.Checks.Add(new Check("warn", "restrictions", "could not read API restrictions: " + e.Message));
        }
    }

    /// <summary>
    /// KuCoin: the key's own record says what it may do (permission list, IP whitelist); the trade account is then read to
    /// prove the key works on it. The key version is not something the file has to get right: 3 is tried, then 2.
    /// </summary>
    private static async Task VerifyKucoinAsync(string? baseUrl, ILoggerFactory loggerFactory, Report report, CancellationToken ct)
    {
        string? stated = Environment.GetEnvironmentVariable(KucoinVenue.EnvApiKeyVersion);
        string[] versions = string.IsNullOrWhiteSpace(stated)
            ? [KucoinVenue.DefaultApiKeyVersion, KucoinVenue.FallbackApiKeyVersion]
            : [stated];
        JsonElement info = default;
        KucoinHttp? http = null;
        KucoinApiException? first = null;
        try
        {
            for (int i = 0; i < versions.Length; i++)
            {
                http?.Dispose();
                http = new KucoinHttp(new KucoinDataClientConfig { BaseUrlHttp = baseUrl, ApiKeyVersion = versions[i] }, loggerFactory.CreateLogger("kucoin"), requireCredentials: true);
                try
                {
                    info = await http.GetSignedAsync("/api/v1/user/api-key", null, ct).ConfigureAwait(false);
                    first = null;
                    break;
                }
                catch (KucoinApiException e) when (!KucoinVenue.ErrorsThatAreNotTheKeyVersion.Contains(e.Code))
                {
                    // A key of another version is refused in a way that depends on what the venue checks first, so every
                    // refusal that is not about the clock, the key's existence or the address gives the next version its
                    // turn. If no version works, the first refusal is the one reported.
                    first ??= e;
                }
            }

            if (first is not null)
            {
                throw first;
            }

            report.KeyAccepted = true;
            string version = Field(info, "apiVersion");
            report.Checks.Add(new Check("ok", "auth", $"key {Mask(Field(info, "apiKey"))}, version {(version.Length > 0 ? version : "?")}"));

            // What a node will sign with, which is the thing this report is read to predict. The clients find the
            // version the same way this does, so a green report here means a node starts; naming it anyway lets a
            // person pin it in their configuration and save the extra request on the first signed call.
            if (string.IsNullOrWhiteSpace(stated) && !string.Equals(version, KucoinVenue.DefaultApiKeyVersion, StringComparison.Ordinal))
            {
                report.Checks.Add(new Check(
                    "ok",
                    "key-version",
                    $"not stated; a node finds version {version} by itself. Set {KucoinVenue.EnvApiKeyVersion}={version} to skip that."));
            }

            List<string> permissions = Field(info, "permission").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
            bool spot = permissions.Contains("Spot", StringComparer.OrdinalIgnoreCase);
            bool futures = permissions.Contains("Futures", StringComparer.OrdinalIgnoreCase);
            bool withdraw = permissions.Any(p => p.Contains("Withdraw", StringComparison.OrdinalIgnoreCase));
            report.CanTrade = spot || futures;
            report.Spot = spot;
            report.Futures = futures;
            report.CanWithdraw = withdraw;
            report.Checks.Add(new Check("ok", "permissions", (spot || futures ? "trade" : "read-only") + (permissions.Count > 0 ? " · " + string.Join(", ", permissions) : string.Empty)));
            report.Checks.Add(new Check(withdraw ? "warn" : "ok", "withdraw", withdraw ? "withdrawal permission PRESENT; create a key without it" : "withdrawal permission absent"));

            bool restricted = Field(info, "ipWhitelist").Length > 0;
            report.IpRestricted = restricted;
            report.Checks.Add(new Check(restricted ? "ok" : "warn", "ip-allow-list", restricted ? Field(info, "ipWhitelist") : "no IP restriction; restrict the key to this machine"));

            try
            {
                await http!.GetSignedAsync("/api/v1/accounts", new Dictionary<string, string> { ["type"] = "trade" }, ct).ConfigureAwait(false);
                report.Checks.Add(new Check("ok", "account", "trade account readable"));
            }
            catch (KucoinApiException e) when (e.Code != "400006" && e.Code != "400003")
            {
                report.Checks.Add(new Check("warn", "account", "trade account query returned " + e.Code + ": " + e.Msg));
            }
        }
        finally
        {
            http?.Dispose();
        }
    }

    /// <summary>
    /// Gate's key test. Two parts and no passphrase, so there is nothing to guess and no fallback to try - which is
    /// the whole of what makes this shorter than KuCoin's.
    /// <para>
    /// <c>/account/detail</c> is the one signed call that answers with what the KEY is rather than with what the
    /// account holds: the user id, the address allow list and the key's mode. The permissions a Gate key carries are
    /// not published on any endpoint, so what the key may DO is found the only way the venue offers - by reading the
    /// spot account, which a read-only key can do and a key with no spot permission cannot.
    /// </para>
    /// </summary>
    private static async Task VerifyGateAsync(string? baseUrl, ILoggerFactory loggerFactory, Report report, CancellationToken ct)
    {
        using GateHttp http = new(new GateExecutionClientConfig { BaseUrlHttp = baseUrl }, loggerFactory.CreateLogger("gate"), requireCredentials: true);
        JsonElement detail = await http.GetSignedAsync("/account/detail", null, ct).ConfigureAwait(false);
        report.KeyAccepted = true;
        report.Checks.Add(new Check("ok", "auth", $"user {Mask(Field(detail, "user_id"))}"));

        string allowList = Field(detail, "ip_whitelist");
        bool restricted = allowList.Length > 0 && allowList != "[]";
        report.IpRestricted = restricted;
        report.Checks.Add(new Check(
            restricted ? "ok" : "warn",
            "ip-allow-list",
            restricted ? allowList : "no IP restriction; restrict the key to this machine"));

        // Gate publishes no endpoint that lists a key's permissions, so trading rights cannot be reported as a fact.
        // Reading the spot account is the nearest thing the venue offers: it succeeds on a read-only key and fails on
        // one with no spot access at all, which is worth knowing and is not the same question.
        try
        {
            await http.GetSignedAsync("/spot/accounts", null, ct).ConfigureAwait(false);
            report.Spot = true;
            report.Checks.Add(new Check("ok", "account", "spot account readable"));
        }
        catch (GateApiException e)
        {
            report.Spot = false;
            report.Checks.Add(new Check("warn", "account", "spot account query returned " + e.Label + ": " + e.Msg));
        }

        try
        {
            await http.GetSignedAsync("/futures/usdt/accounts", null, ct).ConfigureAwait(false);
            report.Futures = true;
            report.Checks.Add(new Check("ok", "futures-account", "USDT futures account readable"));
        }
        catch (GateApiException e)
        {
            report.Futures = false;
            report.Checks.Add(new Check("warn", "futures-account", "futures account query returned " + e.Label + ": " + e.Msg));
        }

        report.Checks.Add(new Check(
            "warn",
            "permissions",
            "Gate publishes no endpoint that lists what a key may do, so trading and withdrawal rights cannot be "
            + "checked here. Create the key without withdrawal permission and confirm it on the venue's own key page."));
    }

    private static string Field(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement p)
        ? p.ValueKind == JsonValueKind.String ? p.GetString() ?? string.Empty : p.ValueKind == JsonValueKind.Number ? p.GetRawText() : string.Empty
        : string.Empty;

    private static Failure Classify(Exception e)
    {
        switch (e)
        {
            case MissingKeyException:
                return new Failure("no_key_in_file", null, null, e.Message);
            case IOException or UnauthorizedAccessException:
                return new Failure("env_file_missing", null, null, e.Message);
            case BybitApiException bybit:
                return new Failure(BybitCode(bybit.Code) ?? "venue_error", null, bybit.Code.ToString(CultureInfo.InvariantCulture), e.Message);
            case GateApiException gate:
                return new Failure(GateCode(gate.Label) ?? (gate.HttpStatus is (int)HttpStatusCode.TooManyRequests ? "rate_limited" : "venue_error"), gate.HttpStatus, gate.Label.Length > 0 ? gate.Label : null, e.Message);
            case KucoinApiException kucoin:
                return new Failure(KucoinCode(kucoin.Code) ?? (kucoin.HttpStatus is (int)HttpStatusCode.TooManyRequests ? "rate_limited" : "venue_error"), kucoin.HttpStatus == 200 ? null : kucoin.HttpStatus, kucoin.Code, e.Message);
            case VenueHttpException http:
                return ClassifyHttp(http);
            case HttpRequestException or OperationCanceledException or TimeoutException or AuthenticationException or System.Net.Sockets.SocketException:
                return new Failure("unreachable", null, null, e.Message);
            default:
                return new Failure("unknown", null, null, e.Message);
        }
    }

    private static Failure ClassifyHttp(VenueHttpException e)
    {
        int status = (int)e.StatusCode;
        int? venueCode = null;
        try
        {
            using JsonDocument body = JsonDocument.Parse(e.Body);
            foreach (string name in new[] { "code", "retCode" })
            {
                if (body.RootElement.ValueKind == JsonValueKind.Object && body.RootElement.TryGetProperty(name, out JsonElement c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out int value) && value != 0)
                {
                    venueCode = value;
                    break;
                }
            }
        }
        catch (JsonException)
        {
        }

        string? venueText = venueCode?.ToString(CultureInfo.InvariantCulture);
        string? code = venueCode is null ? null : venueCode < 0 ? BinanceCode(venueCode.Value) : BybitCode(venueCode.Value);
        if (code is null)
        {
            bool restrictedPlace = e.Body.Contains("restricted", StringComparison.OrdinalIgnoreCase) || e.Body.Contains("country", StringComparison.OrdinalIgnoreCase) || e.Body.Contains("region", StringComparison.OrdinalIgnoreCase);
            code = e.StatusCode switch
            {
                HttpStatusCode.TooManyRequests => "rate_limited",
                (HttpStatusCode)BinanceTeapot => "rate_limited",
                HttpStatusCode.UnavailableForLegalReasons => "geo_blocked",
                HttpStatusCode.Forbidden when restrictedPlace => "geo_blocked",
                HttpStatusCode.Unauthorized when venueCode is null => "bad_key",
                _ => "venue_error",
            };
        }

        return new Failure(code, status, venueText, WithoutQuery(e.Message));
    }

    // The request line in the message carries the signed query; the signature is of no use to a reader.
    private static string WithoutQuery(string message)
    {
        int start = message.IndexOf('?', StringComparison.Ordinal);
        int end = start < 0 ? -1 : message.IndexOf(' ', start);
        return start < 0 || end < 0 ? message : message.Remove(start, end - start);
    }

    private static string? BybitCode(int code) => code switch
    {
        10003 => "bad_key",
        10004 => "bad_signature",
        33004 => "key_expired",
        10010 => "ip_not_allowed",
        10002 => "clock_skew",
        10005 => "permission_denied",
        10009 or 10024 => "geo_blocked",
        10006 or 10018 => "rate_limited",
        _ => null,
    };

    private static string? KucoinCode(string code) => code switch
    {
        "400003" => "bad_key",
        "400004" => "bad_signature",
        "400005" => "bad_signature",
        "400006" => "ip_not_allowed",
        "400007" => "permission_denied",
        "400002" => "clock_skew",
        "411100" => "permission_denied",
        "400100" => "permission_denied",
        "403000" => "geo_blocked",
        "429000" => "rate_limited",
        "1015" => "rate_limited",
        _ => null,
    };

    /// <summary>
    /// What Gate's own refusal labels mean. The venue answers with a WORD rather than a number - every other venue
    /// here answers with a code - so this table is keyed on strings, and a label it does not know is reported as it
    /// came rather than translated into a guess.
    /// </summary>
    private static string? GateCode(string label) => label switch
    {
        "INVALID_KEY" => "bad_key",
        "INVALID_SIGNATURE" => "bad_signature",
        "MISSING_REQUIRED_HEADER" => "bad_signature",
        "REQUEST_EXPIRED" => "clock_skew",
        "IP_FORBIDDEN" => "ip_not_allowed",
        "READ_ONLY" => "permission_denied",
        "FORBIDDEN" => "permission_denied",
        "USER_NOT_FOUND" => "bad_key",
        "TOO_MANY_REQUESTS" => "rate_limited",
        _ => null,
    };

    private static string? BinanceCode(int code) => code switch
    {
        -2014 => "bad_key",
        -2015 => "bad_key_or_ip",
        -1022 => "bad_signature",
        -1021 => "clock_skew",
        -1002 => "permission_denied",
        -1003 => "rate_limited",
        _ => null,
    };

    private static string Mask(string value) => value.Length <= 4 ? "••••" : new string('•', Math.Max(4, value.Length - 4)) + value[^4..];
}
