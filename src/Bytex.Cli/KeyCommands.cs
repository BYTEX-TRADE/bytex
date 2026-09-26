using System.CommandLine;
using System.Globalization;
using System.Net;
using System.Security.Authentication;
using System.Text.Json;
using Bytex.Adapters.Binance;
using Bytex.Adapters.Bitget;
using Bytex.Adapters.Bybit;
using Bytex.Adapters.Gate;
using Bytex.Adapters.Hyperliquid;
using Bytex.Adapters.Kraken;
using Bytex.Adapters.Kucoin;
using Bytex.Adapters.Okx;
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
        Option<string> venue = new("--venue") { Description = "BINANCE | BITGET | BYBIT | GATE | HYPERLIQUID | KRAKEN | KUCOIN | OKX", Required = true };
        Option<string> envFile = new("--env-file") { Description = "File with KEY=VALUE lines (for example BYBIT_API_KEY=...; Bitget, KuCoin and OKX also need a passphrase; Hyperliquid takes HYPERLIQUID_PRIVATE_KEY and no key pair at all)", Required = true };
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
                    case "BITGET":
                        RequireKey(BitgetVenue.EnvApiKey, BitgetVenue.EnvApiSecret);
                        RequireKey(BitgetVenue.EnvApiPassphrase, BitgetVenue.EnvApiPassphrase);
                        await VerifyBitgetAsync(parseResult.GetValue(baseUrl), loggerFactory, report, limit.Token).ConfigureAwait(false);
                        break;
                    case "KUCOIN":
                        RequireKey(KucoinVenue.EnvApiKey, KucoinVenue.EnvApiSecret);
                        RequireKey(KucoinVenue.EnvApiPassphrase, KucoinVenue.EnvApiPassphrase);
                        await VerifyKucoinAsync(parseResult.GetValue(baseUrl), loggerFactory, report, limit.Token).ConfigureAwait(false);
                        break;
                    case "OKX":
                        RequireKey(OkxVenue.EnvApiKey, OkxVenue.EnvApiSecret);
                        RequireKey(OkxVenue.EnvApiPassphrase, OkxVenue.EnvApiPassphrase);
                        await VerifyOkxAsync(parseResult.GetValue(baseUrl), loggerFactory, report, limit.Token).ConfigureAwait(false);
                        break;
                    case "KRAKEN":
                        RequireKey(KrakenVenue.EnvApiKey, KrakenVenue.EnvApiSecret);
                        await VerifyKrakenAsync(parseResult.GetValue(baseUrl), loggerFactory, report, limit.Token).ConfigureAwait(false);
                        break;
                    case "GATE":
                        RequireKey(GateVenue.EnvApiKey, GateVenue.EnvApiSecret);
                        await VerifyGateAsync(parseResult.GetValue(baseUrl), loggerFactory, report, limit.Token).ConfigureAwait(false);
                        break;
                    case "HYPERLIQUID":
                        // ONE variable, and it is not a key pair: this venue issues nothing. The credential is a
                        // private key, so RequireKey is asked for the same name twice rather than for a secret
                        // that does not exist.
                        RequireKey(HyperliquidVenue.EnvPrivateKey, HyperliquidVenue.EnvPrivateKey);
                        await VerifyHyperliquidAsync(parseResult.GetValue(baseUrl), loggerFactory, report, limit.Token).ConfigureAwait(false);
                        break;
                    default:
                        report.Fail("venue", new Failure("venue_unknown", null, null, "expected one of BINANCE, BITGET, BYBIT, GATE, HYPERLIQUID, KRAKEN, KUCOIN or OKX"));
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

    /// <summary>
    /// Binance: the spot account read proves the key, and the key's own restrictions record says what it may do.
    /// <para>
    /// The coin-margined market is then asked in its own right. This venue grants futures with ONE permission and
    /// serves its two futures markets on two different hosts, so "futures on" does not establish that the key
    /// reaches the coin-margined one - and a key that does not is a live node that authenticates, starts, and is
    /// refused by the venue on its first order. Kraken's key test asks each of its platforms for the same reason,
    /// and that reason is stronger here, because one permission covering two hosts looks like nothing to check.
    /// </para>
    /// </summary>
    private static async Task VerifyBinanceAsync(string? baseUrl, ILoggerFactory loggerFactory, Report report, CancellationToken ct)
    {
        BinanceDataClientConfig config = new() { AccountType = BinanceAccountType.Spot, BaseUrlHttp = baseUrl };
        using BinanceHttp http = new(config, loggerFactory.CreateLogger("binance"), requireCredentials: true);
        using JsonDocument account = await http.GetSignedAsync(BinanceVenue.BalancePath(BinanceAccountType.Spot), null, BinanceVenue.Weights.Account, ct).ConfigureAwait(false);
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
            if (futures)
            {
                await CheckBinanceCoinMarginedAsync(baseUrl, loggerFactory, report, ct).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is VenueHttpException or HttpRequestException or InvalidOperationException or JsonException)
        {
            report.Checks.Add(new Check("warn", "restrictions", "could not read API restrictions: " + e.Message));
        }
    }

    /// <summary>
    /// Whether the key really works on Binance's coin-margined host, asked only where the restrictions record says
    /// futures are on at all: a key without the permission would be refused for the permission and the report would
    /// then say the same thing twice. A refusal here is a warning rather than a failure, because the key is known
    /// good by this point and what is being reported is which of the venue's markets it reaches.
    /// </summary>
    private static async Task CheckBinanceCoinMarginedAsync(string? baseUrl, ILoggerFactory loggerFactory, Report report, CancellationToken ct)
    {
        using BinanceHttp coinM = new(
            new BinanceDataClientConfig { AccountType = BinanceAccountType.CoinMFutures, BaseUrlHttp = baseUrl },
            loggerFactory.CreateLogger("binance"),
            requireCredentials: true);

        try
        {
            using JsonDocument balances = await coinM.GetSignedAsync(
                BinanceVenue.BalancePath(BinanceAccountType.CoinMFutures),
                null,
                BinanceVenue.Weights.FuturesBalance,
                ct).ConfigureAwait(false);

            int assets = balances.RootElement.ValueKind == JsonValueKind.Array ? balances.RootElement.GetArrayLength() : 0;
            report.Checks.Add(new Check("ok", "coinm-futures", $"coin-margined balances readable ({assets} assets)"));
        }
        catch (Exception e) when (e is VenueHttpException or HttpRequestException or InvalidOperationException or JsonException)
        {
            report.Checks.Add(new Check(
                "warn",
                "coinm-futures",
                "futures are enabled and the coin-margined host refused this key: " + e.Message));
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
    /// OKX: the key's own record says what it may do and where it may be used, and the account is then read to prove
    /// the key works on it.
    /// <para>
    /// One report for the whole venue and not one per market, which is this venue's own doing: it has one unified
    /// account covering spot, perpetuals and dated futures, and it publishes one permission list for all three. So a
    /// key that may trade may trade all three markets, and this report says so rather than inventing a distinction
    /// the venue does not make.
    /// </para>
    /// </summary>
    private static async Task VerifyOkxAsync(string? baseUrl, ILoggerFactory loggerFactory, Report report, CancellationToken ct)
    {
        using OkxHttp http = new(new OkxDataClientConfig { BaseUrlHttp = baseUrl }, loggerFactory.CreateLogger("okx"), requireCredentials: true);
        JsonElement config = First(await http.GetSignedAsync("/api/v5/account/config", null, ct).ConfigureAwait(false));

        report.KeyAccepted = true;
        report.Checks.Add(new Check("ok", "auth", $"key for account {Mask(Field(config, "uid"))}, account level {Field(config, "acctLv")}"));

        // The venue's permission word for the whole key: "read_only" or "trade", optionally with more beside it.
        string[] permissions = Field(config, "perm")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        bool canTrade = permissions.Contains("trade", StringComparer.OrdinalIgnoreCase);
        bool canWithdraw = permissions.Any(p => p.Contains("withdraw", StringComparison.OrdinalIgnoreCase));
        report.CanTrade = canTrade;
        report.Spot = canTrade;
        report.Futures = canTrade;
        report.CanWithdraw = canWithdraw;
        report.Checks.Add(new Check("ok", "permissions", (canTrade ? "trade" : "read-only") + (permissions.Length > 0 ? " · " + string.Join(", ", permissions) : string.Empty)));
        report.Checks.Add(new Check(canWithdraw ? "warn" : "ok", "withdraw", canWithdraw ? "withdrawal permission PRESENT; create a key without it" : "withdrawal permission absent"));

        string ips = Field(config, "ip");
        bool restricted = ips.Length > 0;
        report.IpRestricted = restricted;
        report.Checks.Add(new Check(restricted ? "ok" : "warn", "ip-allow-list", restricted ? ips : "no IP restriction; restrict the key to this machine"));

        // Which side of a netting account a strategy will trade on. Not a permission and not a failure, but the one
        // account setting that changes what an order has to carry: in long/short mode the venue demands a position
        // side on every derivative order, and this adapter sends none - so a node would be refused, and a report
        // that stayed silent about it would be green in front of a node that cannot place an order.
        string positionMode = Field(config, "posMode");
        bool netMode = positionMode.Length == 0 || positionMode.Equals("net_mode", StringComparison.OrdinalIgnoreCase);
        report.Checks.Add(new Check(
            netMode ? "ok" : "warn",
            "position-mode",
            netMode
                ? "net mode, which is what this engine trades"
                : $"{positionMode}: derivative orders need a position side this engine does not send. Switch the account to net mode."));

        try
        {
            await http.GetSignedAsync("/api/v5/account/balance", null, ct).ConfigureAwait(false);
            report.Checks.Add(new Check("ok", "account", "unified account readable"));
        }
        catch (OkxApiException e)
        {
            report.Checks.Add(new Check("warn", "account", "balance query returned " + e.Code + ": " + e.Msg));
        }
    }

    /// <summary>The first row of an OKX answer, which is an array even where exactly one row can ever come back.</summary>
    private static JsonElement First(JsonElement data) =>
        data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0 ? data[0] : data;

    /// <summary>
    /// Kraken, which is two platforms behind one venue name and one pair of variables. A spot key is issued on one
    /// site and a futures key on the other, they sign differently, and NEITHER WORKS ON THE OTHER - so the first
    /// thing this reports is which of the two the key in the file belongs to, because a key that is perfectly good
    /// and configured against the wrong family is the mistake this venue invites.
    /// <para>
    /// What it cannot report is the rest. Kraken publishes no endpoint that says what a key may do, whether it may
    /// withdraw, or whether it is restricted to an address: the other three venues each have one and this one has
    /// none. Those facts are left unknown rather than guessed at, and the report says so in as many words - a green
    /// line reading "withdrawals disabled" that nothing had checked would be worse than no line at all.
    /// </para>
    /// <para>
    /// Trade permission is not probed either. The spot platform would allow it - AddOrder takes a validate flag
    /// documented to check an order without submitting it - and a flag that turned out not to be honoured would
    /// place a real order on somebody's account while testing their key. That is not a risk worth a line of a
    /// report.
    /// </para>
    /// </summary>
    private static async Task VerifyKrakenAsync(string? baseUrl, ILoggerFactory loggerFactory, Report report, CancellationToken ct)
    {
        KrakenApiException? spotRefusal = null;
        using (KrakenHttp spot = new(
            new KrakenExecutionClientConfig { ProductType = KrakenProductType.Spot, BaseUrlHttp = baseUrl },
            loggerFactory.CreateLogger("kraken"),
            requireCredentials: true))
        {
            try
            {
                await spot.PostSignedAsync(KrakenKeyPaths.SpotBalance, null, ct).ConfigureAwait(false);
                report.KeyAccepted = true;
                report.Spot = true;
                report.Checks.Add(new Check("ok", "auth", "spot balances readable"));

                // What a live node needs beyond reading: the private socket is opened with a token from a signed
                // call, so a key that cannot fetch one cannot receive an order event however well it reads.
                try
                {
                    await spot.PostSignedAsync(KrakenKeyPaths.SpotWebSocketsToken, null, ct).ConfigureAwait(false);
                    report.Checks.Add(new Check("ok", "stream", "socket token issued; order events can be received"));
                }
                catch (KrakenApiException e)
                {
                    report.Checks.Add(new Check("warn", "stream", "no socket token: " + e.Code));
                }
            }
            catch (KrakenApiException e)
            {
                spotRefusal = e;
                report.Spot = false;
            }
        }

        using KrakenHttp futures = new(
            new KrakenExecutionClientConfig { ProductType = KrakenProductType.Futures, BaseUrlHttp = baseUrl },
            loggerFactory.CreateLogger("kraken"),
            requireCredentials: true);

        try
        {
            await futures.GetSignedAsync(KrakenFuturesVenue.AccountsPath, null, ct).ConfigureAwait(false);
            report.KeyAccepted = true;
            report.Futures = true;
            report.Checks.Add(new Check("ok", "auth", "futures accounts readable"));
        }
        catch (KrakenApiException e)
        {
            report.Futures = false;
            if (report.Spot != true)
            {
                // Neither platform took it. The spot refusal is the one reported, because it is the one that names a
                // reason: the futures platform answers every bad credential with the same single word.
                throw spotRefusal ?? e;
            }
        }

        report.Checks.Add(new Check(
            "ok",
            "platform",
            (report.Spot == true ? "spot" : string.Empty)
            + (report.Spot == true && report.Futures == true ? " and " : string.Empty)
            + (report.Futures == true ? "futures" : string.Empty)
            + " · a key for one of Kraken's two platforms does not work on the other"));

        report.Checks.Add(new Check(
            "warn",
            "permissions",
            "unknown: Kraken publishes no endpoint that reports a key's permissions, its withdrawal rights or its "
            + "address restrictions. Check them on the venue's own API management page."));
    }

    /// <summary>The two spot paths this check calls, named rather than written into the calls twice.</summary>
    private static class KrakenKeyPaths
    {
        public const string SpotBalance = KrakenVenue.RestVersion + "/private/BalanceEx";

        public const string SpotWebSocketsToken = KrakenVenue.RestVersion + "/private/GetWebSocketsToken";
    }

    /// <summary>
    /// Bitget: the key's own record says what it may do and which addresses it may be used from, and each family's
    /// account is then read to prove the key really works on it.
    /// <para>
    /// A Bitget key is three parts, so a missing passphrase is reported by name before anything is sent. The
    /// permissions arrive as a list of words the venue calls authorities, and the two that matter are whether the key
    /// may trade at all and whether it may withdraw - a key that may withdraw should be replaced rather than used.
    /// </para>
    /// <para>
    /// Which markets the key covers is answered by asking them rather than by reading a permission name. The venue
    /// grants a key per product type, and a key refused by one market and accepted by another has said exactly what a
    /// person needs to know - where a name in a list would still have to be believed.
    /// </para>
    /// </summary>
    private static async Task VerifyBitgetAsync(string? baseUrl, ILoggerFactory loggerFactory, Report report, CancellationToken ct)
    {
        BitgetExecutionClientConfig config = new() { BaseUrlHttp = baseUrl };
        using BitgetHttp http = new(config, loggerFactory.CreateLogger("bitget"), requireCredentials: true);
        JsonElement info = await http.GetSignedAsync(BitgetAccountInfoPath, null, ct).ConfigureAwait(false);
        report.KeyAccepted = true;
        report.Checks.Add(new Check("ok", "auth", $"key of user {Mask(Field(info, "userId"))}"));

        List<string> authorities = BitgetAuthorities(info);
        bool withdraw = authorities.Exists(a => a.Contains("withdraw", StringComparison.OrdinalIgnoreCase));
        bool readOnly = authorities.Count > 0 && authorities.TrueForAll(a => a.Contains("readonly", StringComparison.OrdinalIgnoreCase) || a.Contains("read_only", StringComparison.OrdinalIgnoreCase));
        report.CanWithdraw = withdraw;
        report.Checks.Add(new Check("ok", "permissions", (readOnly ? "read-only" : "trade") + (authorities.Count > 0 ? " - " + string.Join(", ", authorities) : string.Empty)));
        report.Checks.Add(new Check(withdraw ? "warn" : "ok", "withdraw", withdraw ? "withdrawal permission PRESENT; create a key without it" : "withdrawal permission absent"));

        string ips = Field(info, "ips");
        bool restricted = ips.Length > 0;
        report.IpRestricted = restricted;
        report.Checks.Add(new Check(restricted ? "ok" : "warn", "ip-allow-list", restricted ? ips : "no IP restriction; restrict the key to this machine"));

        bool spot = await BitgetMarketWorksAsync(http, BitgetSpotAssetsPath, null, ct).ConfigureAwait(false);
        report.Spot = spot && !readOnly;
        report.Checks.Add(new Check(spot ? "ok" : "warn", "spot", spot ? "spot account readable" : "spot account not readable with this key"));

        // Both perpetual families, because a node pointed at the one the key was not granted fails at its first
        // request rather than at its first order.
        bool usdt = await BitgetMarketWorksAsync(http, BitgetMixAccountsPath, BitgetUsdtFuturesProduct, ct).ConfigureAwait(false);
        bool usdc = await BitgetMarketWorksAsync(http, BitgetMixAccountsPath, BitgetUsdcFuturesProduct, ct).ConfigureAwait(false);
        report.Futures = (usdt || usdc) && !readOnly;
        report.Checks.Add(new Check(
            usdt || usdc ? "ok" : "warn",
            "futures",
            $"USDT-margined {(usdt ? "readable" : "not readable")}, USDC-margined {(usdc ? "readable" : "not readable")}"));

        report.CanTrade = !readOnly && (spot || usdt || usdc);
    }

    /// <summary>
    /// What a Bitget key is allowed to do. The venue sends the list as an array on some accounts and as one
    /// comma-separated string on others, and a report that read only one of the two shapes would call a trading key
    /// read-only.
    /// </summary>
    private static List<string> BitgetAuthorities(JsonElement info)
    {
        if (info.ValueKind == JsonValueKind.Object
            && info.TryGetProperty("authorities", out JsonElement list)
            && list.ValueKind == JsonValueKind.Array)
        {
            return list.EnumerateArray().Select(a => a.ToString()).Where(a => a.Length > 0).ToList();
        }

        return Field(info, "authorities")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();
    }

    /// <summary>Whether one Bitget market answers this key, which is the only way to tell what the key covers.</summary>
    private static async Task<bool> BitgetMarketWorksAsync(BitgetHttp http, string path, string? productType, CancellationToken ct)
    {
        try
        {
            Dictionary<string, string>? query = productType is null
                ? null
                : new Dictionary<string, string>(StringComparer.Ordinal) { ["productType"] = productType };

            await http.GetSignedAsync(path, query, ct).ConfigureAwait(false);
            return true;
        }
        catch (BitgetApiException)
        {
            return false;
        }
    }

    /// <summary>Where a Bitget key's own record answers: who owns it, what it may do, and where it may be used from.</summary>
    private const string BitgetAccountInfoPath = "/api/v2/spot/account/info";

    /// <summary>Where a Bitget spot balance answers, asked only to prove the key works on that market.</summary>
    private const string BitgetSpotAssetsPath = "/api/v2/spot/account/assets";

    /// <summary>Where a Bitget derivative balance answers, asked once per perpetual product type.</summary>
    private const string BitgetMixAccountsPath = "/api/v2/mix/account/accounts";

    /// <summary>The venue's name for its USDT-margined perpetuals, which a derivative request carries.</summary>
    private const string BitgetUsdtFuturesProduct = "USDT-FUTURES";

    /// <summary>The venue's name for its USDC-margined perpetuals.</summary>
    private const string BitgetUsdcFuturesProduct = "USDC-FUTURES";

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

    /// <summary>
    /// This venue's key test, which is a different question from every other venue's.
    /// <para>
    /// There is no key to authenticate and no endpoint that says what a key may do. The credential is a secp256k1
    /// private key, the venue never sees it, and it issues no permissions - so the whole of what can be established
    /// is WHICH ACCOUNT the key controls and WHAT KIND of key it is, and both of those are answered by arithmetic
    /// and public reads rather than by asking the venue about a key.
    /// </para>
    /// <para>
    /// The kind matters more here than a permission list does elsewhere. A key whose own address IS the account is
    /// the account's wallet key: it can move the funds, and there is nothing to switch that off, which is the
    /// loudest warning this report can carry. A key whose address differs is an API wallet the account approved -
    /// it can trade and cannot withdraw - and it is the one anybody should be running a node with.
    /// </para>
    /// <para>
    /// What this cannot establish: whether the venue has really approved an API wallet. The read that lists an
    /// account's agents answered with an empty array on every live account tried, so its populated shape was never
    /// seen and cannot be parsed on trust. The only proof would be a signed action, and every action this venue
    /// takes changes something - so the report says which key it is holding and stops short of claiming the venue
    /// agrees.
    /// </para>
    /// </summary>
    private static async Task VerifyHyperliquidAsync(string? baseUrl, ILoggerFactory loggerFactory, Report report, CancellationToken ct)
    {
        HyperliquidExecutionClientConfig config = new() { BaseUrlHttp = baseUrl };
        HyperliquidCredentials credentials;
        try
        {
            credentials = HyperliquidVenue.Credentials(config);
        }
        catch (ArgumentException e)
        {
            // A key that is not 32 bytes of hex, or is zero, or is past the curve's order. Caught here because it
            // is the commonest mistake on this venue - a pasted address instead of a key - and a stack trace would
            // say nothing about which.
            // Without the parameter name the framework appends, which names an argument of a method the reader of
            // this report has never seen.
            report.Fail("auth", new Failure("bad_key", null, null, e.Message.Split(" (Parameter", StringSplitOptions.None)[0]));
            return;
        }

        using HyperliquidHttp http = new(config, loggerFactory.CreateLogger("hyperliquid"), requireCredentials: true);
        string signer = credentials.Signer!;
        bool isWalletKey = signer.Equals(credentials.Account, StringComparison.OrdinalIgnoreCase);

        // Accepted, in the only sense this venue has one: the key is a valid scalar and it controls this address.
        // Nothing was sent to establish it, which is why nothing can refuse it.
        report.KeyAccepted = true;
        report.Checks.Add(new Check("ok", "auth", $"signs as {signer}, trading the account {credentials.Account}"));

        report.CanWithdraw = isWalletKey;
        report.Checks.Add(isWalletKey
            ? new Check(
                "warn",
                "withdraw",
                "this is the ACCOUNT'S OWN WALLET KEY, so it can move the funds and nothing can restrict it. "
                + $"Approve an API wallet instead and set {HyperliquidVenue.EnvAccountAddress} to this account.")
            : new Check(
                "ok",
                "withdraw",
                "an API wallet: it signs for the account and cannot withdraw. Whether the venue has approved it "
                + "cannot be read, so a refused order is the other thing to check."));

        // There is nothing to restrict a key to an address here: the key is not registered anywhere, so no
        // allow-list exists to be set. Said out loud rather than left blank, because a blank reads as "not checked".
        report.IpRestricted = false;
        report.Checks.Add(new Check("ok", "ip-allow-list", "not a concept on this venue: the key is never registered, so there is nothing to restrict"));

        JsonElement state = await http.InfoAsync(
            HyperliquidReads.ClearinghouseState,
            new Dictionary<string, object>(StringComparer.Ordinal) { [HyperliquidReads.User] = credentials.Account },
            ct).ConfigureAwait(false);

        string equity = state.TryGetProperty("marginSummary", out JsonElement summary) ? Field(summary, "accountValue") : string.Empty;
        int positions = state.TryGetProperty("assetPositions", out JsonElement held) && held.ValueKind == JsonValueKind.Array
            ? held.GetArrayLength()
            : 0;

        // Reading the account proves the address is one the venue knows, which is the half of "can this trade" that
        // does not need a write. An account with no collateral signs perfectly well and every order is refused.
        report.CanTrade = equity.Length > 0 && decimal.TryParse(equity, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal value) && value > 0m;
        report.Futures = true;
        report.Spot = false;
        report.Checks.Add(new Check(
            report.CanTrade == true ? "ok" : "warn",
            "account",
            equity.Length == 0
                ? "the venue holds no perpetuals account for this address"
                : $"{equity} {HyperliquidVenue.QuoteCurrency} of collateral, {positions} open position(s)"
                    + (report.CanTrade == true ? string.Empty : "; an order on an empty account is refused")));

        JsonElement fees = await http.InfoAsync(
            HyperliquidReads.UserFees,
            new Dictionary<string, object>(StringComparer.Ordinal) { [HyperliquidReads.User] = credentials.Account },
            ct).ConfigureAwait(false);

        string maker = Field(fees, "userAddRate");
        string taker = Field(fees, "userCrossRate");
        if (maker.Length > 0 || taker.Length > 0)
        {
            report.Checks.Add(new Check("ok", "fees", $"maker {maker}, taker {taker} on perpetuals"));
        }
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
            case KrakenApiException kraken:
                return new Failure(KrakenCode(kraken.Code) ?? "venue_error", kraken.HttpStatus == 200 ? null : kraken.HttpStatus, kraken.Code, e.Message);
            case BitgetApiException bitget:
                return new Failure(BitgetCode(bitget.Code) ?? "venue_error", bitget.HttpStatus == 200 ? null : bitget.HttpStatus, bitget.Code, e.Message);
            case GateApiException gate:
                return new Failure(GateCode(gate.Label) ?? (gate.HttpStatus is (int)HttpStatusCode.TooManyRequests ? "rate_limited" : "venue_error"), gate.HttpStatus, gate.Label.Length > 0 ? gate.Label : null, e.Message);
            case HyperliquidApiException hyperliquid:
                // No code to carry: this venue refuses with an English sentence and nothing structured. The one
                // worth separating is the refusal that means the digest was wrong rather than the key.
                return new Failure(
                    hyperliquid.Detail.Contains("recover signer", StringComparison.OrdinalIgnoreCase) ? "bad_signature" : "venue_error",
                    hyperliquid.HttpStatus == 200 ? null : hyperliquid.HttpStatus,
                    null,
                    e.Message);
            case KucoinApiException kucoin:
                return new Failure(KucoinCode(kucoin.Code) ?? (kucoin.HttpStatus is (int)HttpStatusCode.TooManyRequests ? "rate_limited" : "venue_error"), kucoin.HttpStatus == 200 ? null : kucoin.HttpStatus, kucoin.Code, e.Message);
            case OkxApiException okx:
                return new Failure(OkxCode(okx.Code) ?? (okx.HttpStatus is (int)HttpStatusCode.TooManyRequests ? "rate_limited" : "venue_error"), okx.HttpStatus == 200 ? null : okx.HttpStatus, okx.Code, e.Message);
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

    /// <summary>
    /// Kraken spot's refusal tokens. It answers with a stable <c>ECATEGORY:Message</c> string and an HTTP 200, so
    /// the token is the only thing that says what went wrong - a caller reading the status learns nothing at all.
    /// <para>
    /// "EAPI:Invalid key" is measured: it is what the live platform answers an unsigned request. The rest are the
    /// venue's published tokens and have not been provoked from here.
    /// </para>
    /// <para>
    /// The futures platform has no equivalent. It answers every bad credential with the single word
    /// <c>authenticationError</c> - measured - so a wrong key, a wrong signature and a clock that is off are one
    /// failure there and cannot be told apart by anything.
    /// </para>
    /// </summary>
    private static string? KrakenCode(string code) => code switch
    {
        "EAPI:Invalid key" => "bad_key",
        "EAPI:Invalid signature" => "bad_signature",
        "EAPI:Invalid nonce" => "clock_skew",
        "EGeneral:Permission denied" => "permission_denied",
        "EAPI:Rate limit exceeded" => "rate_limited",
        "EGeneral:Temporary lockout" => "rate_limited",
        "authenticationError" => "bad_key",
        _ => null,
    };

    /// <summary>
    /// What Bitget's own codes mean. Only two of these were seen from the live venue - 40006 when no key is sent at
    /// all and 40037 for a key it has never issued - because the key is checked before anything else, so a request
    /// without a real key can never produce a signature, timestamp or passphrase refusal to read. The rest are as the
    /// venue documents them and are unverified here.
    /// </summary>
    private static string? BitgetCode(string code) => code switch
    {
        BitgetVenue.ErrorNoApiKey => "bad_key",
        BitgetVenue.ErrorApiKeyUnknown => "bad_key",
        "40001" or "40002" or "40003" or "40011" or "40012" => "bad_key",
        "40009" => "bad_signature",
        "40005" or "40008" => "clock_skew",
        "40013" or "40014" => "permission_denied",
        "40018" => "ip_not_allowed",
        "429" => "rate_limited",
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
    /// OKX's refusals, in the terms this report speaks. Two of these were measured against the live venue with no
    /// key and with a made-up one - 50103 for a missing key header and 50111 for a key it does not know - and the
    /// rest come from the venue's own list, because it checks the key before anything else and will not say what it
    /// thinks of a signature or a passphrase until a real key is presented.
    /// </summary>
    private static string? OkxCode(string code) => code switch
    {
        // Measured: a request with no key header at all, and one with a key the venue does not have.
        "50103" or "50111" => "bad_key",

        "50113" => "bad_signature",
        "50104" or "50105" => "bad_key",
        "50102" => "clock_skew",
        "50110" => "ip_not_allowed",
        "50100" or "50114" => "permission_denied",

        // The one refusal that is neither the key nor the account: a live key sent to the demo account, or the other
        // way round. Worth its own word, because the key is perfectly good and nothing about it needs changing.
        "50101" => "wrong_environment",

        "50011" or "50061" => "rate_limited",
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
