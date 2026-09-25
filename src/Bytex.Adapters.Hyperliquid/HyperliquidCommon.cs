using System.Globalization;
using System.Text;
using System.Text.Json;
using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Primitives;
using Bytex.Live.Network;
using Microsoft.Extensions.Logging;

namespace Bytex.Adapters.Hyperliquid;

/// <summary>
/// The settings both of this venue's clients read. There is no API key here and nothing named one, because this venue
/// does not issue keys: the credential is a secp256k1 private key that controls an address, and the address is the
/// account. See <see cref="HyperliquidVenue.EnvPrivateKey"/> for what that changes.
/// </summary>
public interface IHyperliquidSettings
{
    /// <summary>
    /// The private key requests are signed with, as hex. Either the account's own wallet key or an API wallet the
    /// account has approved; <see cref="AccountAddress"/> is what tells the two apart.
    /// </summary>
    string? PrivateKey { get; }

    /// <summary>
    /// The account to read and trade, when it is not the address the private key itself controls.
    /// <para>
    /// This is not a convenience. An API wallet signs on behalf of an account and has an address of its own, so with
    /// one configured the signing address and the account address are DIFFERENT - and every read on this venue is
    /// keyed by the account address, not by whoever signed. Left empty, the account is the address derived from the
    /// key, which is right for a wallet key and silently wrong for an API wallet: positions, orders and balances
    /// would all be read for an address that has never traded, and every one of those reads succeeds and returns
    /// nothing.
    /// </para>
    /// </summary>
    string? AccountAddress { get; }

    /// <summary>Replaces the REST address, which is one host with two endpoints on it rather than a path per resource.</summary>
    string? BaseUrlHttp { get; }

    /// <summary>Replaces the websocket address.</summary>
    string? BaseUrlWs { get; }
}

public sealed record HyperliquidDataClientConfig : DataClientConfig, IHyperliquidSettings
{
    public string? PrivateKey { get; init; }

    public string? AccountAddress { get; init; }

    public string? BaseUrlHttp { get; init; }

    public string? BaseUrlWs { get; init; }
}

public sealed record HyperliquidExecutionClientConfig : ExecutionClientConfig, IHyperliquidSettings
{
    public string? PrivateKey { get; init; }

    public string? AccountAddress { get; init; }

    public string? BaseUrlHttp { get; init; }

    public string? BaseUrlWs { get; init; }

    /// <summary>
    /// Whether leverage is set as cross margin rather than isolated, which is the second half of what this venue's
    /// leverage action carries. Cross by default, which is what an account is on before anything touches it.
    /// <para>
    /// A separate field because <see cref="ExecutionClientConfig.Leverage"/> cannot express it: this venue's
    /// <c>updateLeverage</c> takes the asset, the leverage AND the margin mode in one call, so setting a leverage
    /// here necessarily states a mode, and defaulting that silently to isolated would move every position off the
    /// shared collateral it was sized against.
    /// </para>
    /// </summary>
    public bool CrossMargin { get; init; } = true;
}

/// <summary>
/// What this venue is, measured against the live API on 2026-09-25 rather than read off its documentation. Every
/// number here was produced by a request whose answer is recorded in this adapter's tests.
/// <para>
/// Three things about it are unlike every other venue here and are the reason so much of this class exists.
/// </para>
/// <para>
/// There is no path per resource. Every read is <c>POST /info</c> with a <c>type</c> field saying which read, and
/// every write is <c>POST /exchange</c> with a signed action. So nothing here is a path, and the
/// <c>GetPublicAsync(path, query)</c> shape the other adapters' HTTP helpers are built on has no meaning: the
/// equivalent is <see cref="HyperliquidHttp.InfoAsync"/>, which takes a request type and its fields.
/// </para>
/// <para>
/// An asset is an INDEX, not a symbol. The venue's <c>meta</c> answers with a <c>universe</c> array and an order
/// names its asset by position in it. The index is not published anywhere else and is not stable against a listing,
/// so it is held on the instrument, by the provider that read the array, and nothing above the adapter sees it.
/// </para>
/// <para>
/// And a read of an account needs no credential at all. Positions, open orders, fills and fee tiers are all public,
/// keyed by address - measured, not assumed. Only a write needs the key.
/// </para>
/// </summary>
public static class HyperliquidVenue
{
    /// <summary>The venue, as an instrument id names it.</summary>
    public static readonly Venue Venue = new("HYPERLIQUID");

    /// <summary>
    /// The private key, as hex with or without a 0x prefix. Named a private key and not a secret or an API key
    /// because it is neither: it is the key itself, the venue never sees it, and there is no page to revoke it from.
    /// A wallet key can withdraw; an API wallet the account approved cannot.
    /// </summary>
    public const string EnvPrivateKey = "HYPERLIQUID_PRIVATE_KEY";

    /// <summary>The account address, needed only when the key that signs is an API wallet rather than the account's own.</summary>
    public const string EnvAccountAddress = "HYPERLIQUID_ACCOUNT_ADDRESS";

    /// <summary>
    /// Where the venue answers. One host and two endpoints on it, so this is a root the adapter appends
    /// <see cref="InfoPath"/> or <see cref="ExchangePath"/> to and never one of the two.
    /// </summary>
    public const string DefaultHttpBase = "https://api.hyperliquid.xyz";

    /// <summary>Where the websocket answers. A root: the adapter appends <see cref="WsPath"/>.</summary>
    public const string DefaultWsBase = "wss://api.hyperliquid.xyz";

    /// <summary>
    /// Every read. Discriminated by a <c>type</c> field in the JSON body, and POST only - a GET is refused with 405,
    /// measured, so nothing here can be a cacheable URL.
    /// </summary>
    public const string InfoPath = "/info";

    /// <summary>Every write, as a signed action. The same path for an order, a cancel, an amend and a leverage change.</summary>
    public const string ExchangePath = "/exchange";

    /// <summary>The websocket's path on the socket base.</summary>
    public const string WsPath = "/ws";

    /// <summary>
    /// What the venue calls a perpetual, appended to the coin: <c>BTC</c> is the asset and <c>BTC-PERP</c> the symbol
    /// of its instrument id. The suffix is what the other venues' perpetuals use, so a perpetual reads as one
    /// wherever it came from - and this venue publishes no suffix at all, which would otherwise read as spot.
    /// </summary>
    public const string PerpSuffix = "-PERP";

    /// <summary>
    /// What a perpetual is quoted and settled in here. Every contract, without exception: the venue's <c>meta</c>
    /// names one collateral token for the whole family rather than per contract, which is why this is a constant and
    /// not a field read off each asset.
    /// </summary>
    public const string QuoteCurrency = "USDC";

    /// <summary>The precision the collateral is held to, which is what a balance and a commission are rounded at.</summary>
    public const int QuoteCurrencyPrecision = 8;

    /// <summary>
    /// The maker fee at the base tier, measured from <c>userFees.feeSchedule.add</c> with no key: 1.5 basis points.
    /// Every account starts here and only volume moves it, so it is the right default for an estimate.
    /// </summary>
    public const decimal BaseMakerFee = 0.00015m;

    /// <summary>The taker fee at the base tier, from <c>feeSchedule.cross</c>: 4.5 basis points.</summary>
    public const decimal BaseTakerFee = 0.00045m;

    /// <summary>
    /// How often a perpetual settles funding here, in hours. One hour, from <c>predictedFundings</c>, where the
    /// venue publishes its own interval beside the other exchanges' four - so a rate off this venue is an hourly
    /// charge and a rate off Binance or Bybit is an eight-hourly one, and comparing them as printed is wrong by
    /// eight.
    /// </summary>
    public const int FundingIntervalHours = 1;

    /// <summary>
    /// The most funding settlements one <c>fundingHistory</c> request answers with. Measured: asked for 2000 hours it
    /// returned exactly 500, the oldest 500 of the window, so paging goes FORWARD from the start.
    /// </summary>
    public const int FundingPage = 500;

    /// <summary>
    /// How many bars of an interval the venue will serve, counted back from NOW rather than from the window asked
    /// for. This is not a page size and a paging loop cannot get past it.
    /// <para>
    /// Measured, because it is the single most surprising thing about this venue's history and nothing says it out
    /// loud. Asked for 100000 one-minute bars ending now it gave 5159; the same 6000-hour window ending 1000 hours
    /// ago gave 4002, starting at the same wall-clock moment - about 5000 hourly bars before now. A window entirely
    /// older than that returns an empty array, not an error. So an hour of minutes from last month cannot be
    /// downloaded from here at all, at any page size, and a loop that walked backwards would simply stop returning
    /// rows and look like it had reached the beginning of the venue's history.
    /// </para>
    /// <para>
    /// The number is the count observed with a little slack taken off, because the venue's own boundary drifts by a
    /// few bars between calls. It bounds how far back a fetch bothers to ask.
    /// </para>
    /// </summary>
    public const int CandleRetention = 5000;

    /// <summary>
    /// Levels per side in an <c>l2Book</c> answer and on the book channel. Measured: twenty either way, and asking
    /// for fewer significant figures rounds the prices without deepening the book.
    /// </summary>
    public const int BookDepth = 20;

    /// <summary>
    /// The most decimal places a perpetual's PRICE may have, before the per-asset size precision is taken off it.
    /// <para>
    /// This is half of this venue's price rule and the reason an instrument cannot carry the whole of it. A price is
    /// valid when it has at most <c>MaxPriceDecimals - szDecimals</c> decimal places AND at most
    /// <see cref="MaxPriceSignificantFigures"/> significant figures. The first is a fact about the asset; the second
    /// is a fact about the price, so the smallest valid step CHANGES as the price moves, and an instrument's
    /// increment is one number. <see cref="HyperliquidAsset.RoundPrice"/> applies both.
    /// </para>
    /// </summary>
    public const int MaxPriceDecimals = 6;

    /// <summary>
    /// The other half of the price rule. Measured across seven live books: BTC at 83697 steps by 1, ETH at 2681.6 by
    /// 0.1, SOL at 120.76 by 0.01, HYPE at 91.257 by 0.001, XRP at 1.5598 by 0.0001 - every one of them five
    /// significant figures, and DOGE and kPEPE stepping by 0.000001 where the decimal rule bites first instead.
    /// </summary>
    public const int MaxPriceSignificantFigures = 5;

    /// <summary>
    /// What the maintenance margin of a position is, as a fraction of the initial margin at maximum leverage: a half.
    /// <para>
    /// MEASURED, against two live positions rather than taken from documentation, because the alternative was the
    /// hard-coded 0.025 that two other adapters carry for every contract they list. A cross account holding 4937.99
    /// of BTC notional reported 61.724923 of maintenance margin used, and 4937.99391 / (2 x 40) is 61.72492; another
    /// holding 368455.21 reported 4605.690107, and 368455.20858 / (2 x 40) is 4605.6901. Both to the last digit, at
    /// the asset's published maxLeverage of 40 and not at the 6x and 10x those two accounts had chosen - so the
    /// divisor is the asset's maximum and not the position's leverage.
    /// </para>
    /// </summary>
    public const int MaintenanceMarginFraction = 2;

    /// <summary>
    /// Requests this adapter allows itself in <see cref="RequestWindow"/>. A self-imposed bound rather than a cap
    /// measured at the venue: 120 back-to-back <c>l2Book</c> reads all answered 200, so the ceiling was not reached
    /// and is not known from here.
    /// </summary>
    public const int RequestsPerWindow = 20;

    /// <summary>The window <see cref="RequestsPerWindow"/> is counted over.</summary>
    public static readonly TimeSpan RequestWindow = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How often to ping the websocket. The venue's own keepalive is a JSON ping rather than a protocol frame, and it
    /// closes a socket that has been silent for a while.
    /// </summary>
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(30);

    /// <summary>The keepalive the socket takes, which is a message and not a websocket ping frame.</summary>
    public const string PingMessage = """{"method":"ping"}""";

    /// <summary>
    /// How far through the book a market order reaches.
    /// <para>
    /// This venue HAS NO MARKET ORDER. Measured: an order whose type is <c>{"market":{}}</c> is refused at HTTP 422
    /// before the signature is examined - the only two order types it will deserialise are a limit and a trigger.
    /// So a market order has to be an immediate-or-cancel limit placed past the far touch, and something has to
    /// decide how far past.
    /// </para>
    /// <para>
    /// Five per cent, and this is THIS ADAPTER'S CHOICE rather than a number the venue publishes - there is nothing
    /// to measure here, because the venue has no opinion about it. It is named so that it is visible and changeable
    /// rather than buried at the one place a price is computed. Too small and a market order for more than the top
    /// level is partly cancelled; too large and a thin book fills it far from the touch. Five per cent past the
    /// opposite touch of a twenty-level book fills a normal size and is bounded, which is the trade being made.
    /// </para>
    /// </summary>
    public const decimal MarketOrderSlippage = 0.05m;

    /// <summary>
    /// The length of a client order id on this venue: sixteen bytes, written as hex. Not a string of the caller's
    /// choosing, which is what every other venue here accepts - so an engine client order id cannot be sent as it
    /// stands and <see cref="HyperliquidVenue.CloidFor"/> is what stands in for it.
    /// </summary>
    public const int CloidBytes = 16;

    /// <summary>
    /// Where the asset's index is kept on the instrument. It has to be kept somewhere: an order names the asset by
    /// its position in the venue's universe, and an <see cref="InstrumentId"/> has nowhere to put a number.
    /// </summary>
    public const string AssetIndexInfo = "hyperliquidAssetIndex";

    /// <summary>Where the asset's size precision is kept, which the price rule is computed from as well.</summary>
    public const string SizeDecimalsInfo = "hyperliquidSizeDecimals";

    /// <summary>Where the asset's maximum leverage is kept, which both margins are computed from.</summary>
    public const string MaxLeverageInfo = "hyperliquidMaxLeverage";

    /// <summary>The instrument id of a perpetual, from the coin the venue names it by.</summary>
    public static InstrumentId ToInstrumentId(string coin) => new(new Symbol(coin + PerpSuffix), Venue);

    /// <summary>The coin behind an instrument id, which is <see cref="ToInstrumentId"/> run backwards.</summary>
    public static string ToCoin(InstrumentId id)
    {
        string symbol = id.Symbol.Value;
        return symbol.EndsWith(PerpSuffix, StringComparison.Ordinal) ? symbol[..^PerpSuffix.Length] : symbol;
    }

    /// <summary>Where this venue answers, or whatever a configuration points it at instead.</summary>
    public static string HttpBase(IHyperliquidSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.BaseUrlHttp ?? DefaultHttpBase;
    }

    /// <summary>Where this venue's socket answers, as a root without <see cref="WsPath"/> on it.</summary>
    public static string WsBase(IHyperliquidSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.BaseUrlWs ?? DefaultWsBase;
    }

    /// <summary>
    /// Whether requests are being signed for mainnet, which decides one letter of every digest. Taken from the host
    /// rather than configured separately, so that pointing a client at the test network cannot leave it signing
    /// mainnet-valid signatures: the two would then be replayable against each other, which the letter exists to
    /// prevent.
    /// </summary>
    public static bool IsMainnet(IHyperliquidSettings settings) =>
        HttpBase(settings).Equals(DefaultHttpBase, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The credential a configuration or the environment holds, or null when neither does. Every read on this venue
    /// is public, so a data client runs perfectly well with nothing here.
    /// </summary>
    public static HyperliquidCredentials? OptionalCredentials(IHyperliquidSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string? key = Secrets.Optional(settings.PrivateKey, EnvPrivateKey);
        string? account = Secrets.Optional(settings.AccountAddress, EnvAccountAddress);
        if (key is null)
        {
            return account is null ? null : new HyperliquidCredentials(null, AddressBytes(account));
        }

        byte[] privateKey = Hex.Decode(key, Secp256k1.ScalarSize, "Hyperliquid private key");
        return new HyperliquidCredentials(privateKey, account is null ? Secp256k1.AddressOf(privateKey) : AddressBytes(account));
    }

    /// <summary>The credential, insisted on. Only a write needs one.</summary>
    public static HyperliquidCredentials Credentials(IHyperliquidSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string key = Secrets.Require(settings.PrivateKey, EnvPrivateKey);
        string? account = Secrets.Optional(settings.AccountAddress, EnvAccountAddress);
        byte[] privateKey = Hex.Decode(key, Secp256k1.ScalarSize, "Hyperliquid private key");
        return new HyperliquidCredentials(privateKey, account is null ? Secp256k1.AddressOf(privateKey) : AddressBytes(account));
    }

    /// <summary>An address as bytes, from the hex a person configures it as.</summary>
    public static byte[] AddressBytes(string address) => Hex.Decode(address, Secp256k1.AddressSize, "Hyperliquid account address");

    /// <summary>
    /// The sixteen-byte client order id that stands for an engine one, as the first half of its SHA-256.
    /// <para>
    /// This venue's client order id is a 128-bit number and the engine's is a string of the caller's choosing, so
    /// there is no way to send the engine's id as it is. A hash rather than a counter because it has to survive a
    /// restart: the mapping cannot be looked up in a table that a fresh process does not have, and an order this
    /// node placed before it stopped has to be recognisable when it comes back. Recomputing the id from the order in
    /// the cache does that; a table would not.
    /// </para>
    /// <para>
    /// SHA-256 rather than Keccak because nothing about this is the venue's hash - it is this adapter's own mapping,
    /// and borrowing the signing hash for it would suggest otherwise.
    /// </para>
    /// </summary>
    public static string CloidFor(ClientOrderId clientOrderId)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(clientOrderId.Value));
        return Hex.Encode(hash.AsSpan(0, CloidBytes));
    }

    /// <summary>
    /// What this venue calls a bar of the given length. Fourteen lengths, measured one by one: it took 1m, 3m, 5m,
    /// 15m, 30m, 1h, 2h, 4h, 8h, 12h, 1d, 3d, 1w and 1M, and refused 2m, 6h and 1y with a deserialisation error
    /// rather than an empty answer. The six hours KuCoin serves is one of the ones it refuses.
    /// </summary>
    public static string Interval(BarSpecification spec) => (spec.Aggregation, spec.Step) switch
    {
        (BarAggregation.Minute, 1) => "1m",
        (BarAggregation.Minute, 3) => "3m",
        (BarAggregation.Minute, 5) => "5m",
        (BarAggregation.Minute, 15) => "15m",
        (BarAggregation.Minute, 30) => "30m",
        (BarAggregation.Hour, 1) => "1h",
        (BarAggregation.Hour, 2) => "2h",
        (BarAggregation.Hour, 4) => "4h",
        (BarAggregation.Hour, 8) => "8h",
        (BarAggregation.Hour, 12) => "12h",
        (BarAggregation.Day, 1) => "1d",
        (BarAggregation.Day, 3) => "3d",
        (BarAggregation.Week, 1) => "1w",
        (BarAggregation.Month, 1) => "1M",
        _ => throw new NotSupportedException(
            $"Hyperliquid does not keep {spec} candles. It keeps 1, 3, 5, 15 and 30 minutes, 1, 2, 4, 8 and 12 "
            + "hours, a day, three days, a week and a month."),
    };
}

/// <summary>
/// The reads this adapter makes, and the field names it reads out of the answers.
/// <para>
/// A request TYPE rather than a path, because that is what this venue has: <c>POST /info</c> answers every read and
/// the type in the body says which one. They are named here for the same reason the other adapters name their paths
/// - so that a typo is a compiler error rather than a 422 with the body "Failed to deserialize the JSON body into
/// the target type", which is the whole of what the venue says about an unknown type. Measured.
/// </para>
/// </summary>
public static class HyperliquidReads
{
    /// <summary>Every perpetual the venue lists, with its margin table. Takes no filter of any kind.</summary>
    public const string Meta = "meta";

    /// <summary>The same universe with a context beside each asset - mark, oracle, funding, open interest.</summary>
    public const string MetaAndAssetCtxs = "metaAndAssetCtxs";

    /// <summary>Candles. The only read that wraps its arguments in a nested <c>req</c> object.</summary>
    public const string CandleSnapshot = "candleSnapshot";

    /// <summary>What a perpetual was charged at each settlement, 500 at a time going forward from the start.</summary>
    public const string FundingHistory = "fundingHistory";

    /// <summary>A book snapshot, twenty levels a side.</summary>
    public const string L2Book = "l2Book";

    /// <summary>An account's balances and positions. Public, keyed by address, and needs no credential - measured.</summary>
    public const string ClearinghouseState = "clearinghouseState";

    /// <summary>An account's resting orders, with the trigger fields the plain <c>openOrders</c> read leaves out.</summary>
    public const string FrontendOpenOrders = "frontendOpenOrders";

    /// <summary>An account's fills, newest first, mixing its perpetual and spot trades in one list.</summary>
    public const string UserFills = "userFills";

    /// <summary>What became of one order, by the venue's own id.</summary>
    public const string OrderStatus = "orderStatus";

    /// <summary>The fee schedule and the account's own place in it. Public, so the base tier is readable with no key.</summary>
    public const string UserFees = "userFees";

    // The field names, on the same terms: read once here rather than spelled out at each use.

    public const string Universe = "universe";
    public const string Name = "name";
    public const string SizeDecimals = "szDecimals";
    public const string MaxLeverage = "maxLeverage";
    public const string IsDelisted = "isDelisted";
    public const string OnlyIsolated = "onlyIsolated";
    public const string User = "user";
    public const string Coin = "coin";
    public const string StartTime = "startTime";
    public const string EndTime = "endTime";
    public const string Interval = "interval";
    public const string Request = "req";
}

/// <summary>
/// What this venue's credential is: a private key and the account it acts for. Not a key pair, not issued by the
/// exchange, and not revocable from a settings page - which is why it is not called a key anywhere.
/// </summary>
public sealed record HyperliquidCredentials(byte[]? PrivateKey, byte[] AccountAddress)
{
    /// <summary>Whether this credential can sign, which is the difference between reading the venue and trading on it.</summary>
    public bool CanSign => PrivateKey is not null;

    /// <summary>The account this credential reads and trades, as the hex every request writes it in.</summary>
    public string Account => Hex.Encode(AccountAddress);

    /// <summary>
    /// The address that SIGNS, which is the account's own only when the key is the account's wallet key. With an API
    /// wallet these two differ, and a mismatch is worth reporting because it is otherwise invisible: everything
    /// signs, everything is accepted, and every read comes back empty.
    /// </summary>
    public string? Signer => PrivateKey is null ? null : Hex.Encode(Secp256k1.AddressOf(PrivateKey));

    // Never the key itself in a log line or an exception text. The account address is public information - it is on
    // a blockchain - so it is the half worth printing.
    public override string ToString() =>
        $"HyperliquidCredentials(account {Account}, {(CanSign ? "can sign" : "read only")})";
}

/// <summary>
/// One signed write, with its fields in the order the VENUE will re-encode them in.
/// <para>
/// The order is the whole reason this type exists. The venue does not hash the JSON it was sent: it parses the action
/// into its own types, re-encodes it as MessagePack, and signs that. A MessagePack map is an ordered list of pairs,
/// so a field in the wrong place produces a different digest, a signature that recovers a different address, and a
/// refusal that says the account does not exist. Declaring the order once, here, and generating BOTH the JSON and
/// the MessagePack from it means the two cannot disagree about a value either.
/// </para>
/// </summary>
public sealed record HyperliquidAction
{
    private readonly PackMap _fields;

    internal HyperliquidAction(PackMap fields)
    {
        _fields = fields;
        Packed = ActionPack.Encode(fields);
        Json = ToJson(fields);
    }

    /// <summary>The MessagePack bytes the signature covers.</summary>
    public byte[] Packed { get; }

    /// <summary>The JSON the venue is sent, built from the same fields so it cannot say anything different.</summary>
    public string Json { get; }

    /// <summary>What the action is, for a log line and for a refusal that has to name what was refused.</summary>
    public string Type => _fields.Fields.Count > 0 && _fields.Fields[0].Value is PackText type ? type.Value : "?";

    private static string ToJson(PackValue value)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            WriteJson(writer, value);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteJson(Utf8JsonWriter writer, PackValue value)
    {
        switch (value)
        {
            case PackMap map:
                writer.WriteStartObject();
                foreach ((string name, PackValue field) in map.Fields)
                {
                    writer.WritePropertyName(name);
                    WriteJson(writer, field);
                }

                writer.WriteEndObject();
                break;

            case PackArray array:
                writer.WriteStartArray();
                foreach (PackValue item in array.Items)
                {
                    WriteJson(writer, item);
                }

                writer.WriteEndArray();
                break;

            case PackText text:
                writer.WriteStringValue(text.Value);
                break;

            case PackNumber number:
                writer.WriteNumberValue(number.Value);
                break;

            case PackBool flag:
                writer.WriteBooleanValue(flag.Value);
                break;

            default:
                writer.WriteNullValue();
                break;
        }
    }
}

/// <summary>
/// The writes this adapter makes, each with its fields in the venue's own order.
/// <para>
/// The short field names are the venue's: <c>a</c> is the asset index, <c>b</c> whether it is a buy, <c>p</c> the
/// price, <c>s</c> the size, <c>r</c> whether it only reduces a position, <c>t</c> the order type and <c>c</c> the
/// client order id. They are not abbreviations this adapter chose and cannot be spelled out - they are part of the
/// digest.
/// </para>
/// </summary>
public static class HyperliquidActions
{
    /// <summary>Time in force, as the venue spells it: good till cancelled.</summary>
    public const string Gtc = "Gtc";

    /// <summary>Immediate or cancel.</summary>
    public const string Ioc = "Ioc";

    /// <summary>Post only, which this venue calls "add liquidity only" and refuses outright rather than repricing.</summary>
    public const string Alo = "Alo";

    /// <summary>What a set of orders with no relationship to each other is grouped as.</summary>
    public const string NoGrouping = "na";

    /// <summary>
    /// One or more orders. Always the batch shape even for a single order, because the venue has no single-order
    /// action - a lone order is a batch of one, and the digest is over the batch.
    /// </summary>
    public static HyperliquidAction Order(IEnumerable<HyperliquidOrderWire> orders) =>
        new(PackMap.Of(
            ("type", new PackText("order")),
            ("orders", PackArray.Of((orders ?? throw new ArgumentNullException(nameof(orders))).Select(o => o.Pack()))),
            ("grouping", new PackText(NoGrouping))));

    /// <summary>A cancel by the venue's own order id, which is what the venue answers an accepted order with.</summary>
    public static HyperliquidAction Cancel(int asset, ulong orderId) =>
        new(PackMap.Of(
            ("type", new PackText("cancel")),
            ("cancels", PackArray.Of([PackMap.Of(("a", new PackNumber((ulong)asset)), ("o", new PackNumber(orderId)))]))));

    /// <summary>
    /// A cancel by client order id. A separate action from <see cref="Cancel"/> with differently spelled fields -
    /// <c>asset</c> and <c>cloid</c> where the other says <c>a</c> and <c>o</c> - which is the venue's choice and
    /// part of both digests.
    /// </summary>
    public static HyperliquidAction CancelByCloid(int asset, string cloid) =>
        new(PackMap.Of(
            ("type", new PackText("cancelByCloid")),
            ("cancels", PackArray.Of([PackMap.Of(("asset", new PackNumber((ulong)asset)), ("cloid", new PackText(cloid)))]))));

    /// <summary>
    /// An amendment: the order to change, and the order it becomes. The venue replaces the whole order rather than
    /// patching a field, so price, size, side and type all travel again whether they changed or not.
    /// </summary>
    public static HyperliquidAction Modify(ulong orderId, HyperliquidOrderWire order) =>
        new(PackMap.Of(
            ("type", new PackText("modify")),
            ("oid", new PackNumber(orderId)),
            ("order", (order ?? throw new ArgumentNullException(nameof(order))).Pack())));

    /// <summary>
    /// The leverage of one asset, and the margin mode with it. Per asset and not per account, and the two cannot be
    /// set separately: whoever sets a leverage here has also said whether the position is on shared collateral.
    /// </summary>
    public static HyperliquidAction UpdateLeverage(int asset, bool isCross, int leverage) =>
        new(PackMap.Of(
            ("type", new PackText("updateLeverage")),
            ("asset", new PackNumber((ulong)asset)),
            ("isCross", new PackBool(isCross)),
            ("leverage", new PackNumber((ulong)leverage))));
}

/// <summary>
/// One order as this venue takes it. A record rather than a method's worth of arguments because it is built in two
/// places - a new order and an amendment - and the two must build it identically.
/// </summary>
public sealed record HyperliquidOrderWire
{
    /// <summary>The asset's index in the venue's universe, which is how an instrument is named on the wire.</summary>
    public required int Asset { get; init; }

    public required bool IsBuy { get; init; }

    /// <summary>The price as a decimal string, already rounded to what the venue will accept.</summary>
    public required string Price { get; init; }

    /// <summary>The size in base units, as a decimal string.</summary>
    public required string Size { get; init; }

    public required bool ReduceOnly { get; init; }

    /// <summary>The time in force of a limit order, or null when this is a trigger order.</summary>
    public string? TimeInForce { get; init; }

    /// <summary>The trigger price of a stop or take-profit, or null for a plain limit order.</summary>
    public string? TriggerPrice { get; init; }

    /// <summary>Whether a trigger order fires as a market order once touched.</summary>
    public bool TriggerIsMarket { get; init; }

    /// <summary>
    /// Whether the trigger is a take-profit rather than a stop-loss. The venue needs to be told which because it
    /// decides the direction the price has to cross the trigger from.
    /// </summary>
    public bool TriggerIsTakeProfit { get; init; }

    /// <summary>The sixteen-byte client order id as hex, or null to let the venue give the order only its own id.</summary>
    public string? Cloid { get; init; }

    internal PackMap Pack()
    {
        List<(string, PackValue)> fields =
        [
            ("a", new PackNumber((ulong)Asset)),
            ("b", new PackBool(IsBuy)),
            ("p", new PackText(Price)),
            ("s", new PackText(Size)),
            ("r", new PackBool(ReduceOnly)),
            ("t", OrderType()),
        ];

        if (Cloid is { } cloid)
        {
            fields.Add(("c", new PackText(cloid)));
        }

        return new PackMap(fields);
    }

    private PackValue OrderType() => TriggerPrice is { } trigger
        ? PackMap.Of(("trigger", PackMap.Of(
            ("isMarket", new PackBool(TriggerIsMarket)),
            ("triggerPx", new PackText(trigger)),
            ("tpsl", new PackText(TriggerIsTakeProfit ? "tp" : "sl")))))
        : PackMap.Of(("limit", PackMap.Of(("tif", new PackText(TimeInForce ?? HyperliquidActions.Gtc)))));
}

/// <summary>
/// This venue's HTTP access: one method for every read and one for every write, because that is how many endpoints
/// there are.
/// <para>
/// The other adapters' helpers take a path and a query. There is no path to take here - <c>POST /info</c> answers
/// every read and the <c>type</c> field in the body says which - so the shape is a request type and its fields
/// instead. Nothing was contorted to fit: the shared <see cref="HttpClientWrapper"/> underneath takes a path and a
/// body and is perfectly happy with two paths, and it is only the per-adapter convenience layer that has no path in
/// it.
/// </para>
/// <para>
/// Both endpoints answer HTTP 200 for a refused request, in different ways. <c>/info</c> refuses an unknown request
/// type with 422 and a plain-text body that is not JSON at all; <c>/exchange</c> answers a bad signature with 200
/// and <c>{"status":"err","response":"..."}</c> in the body. So neither the status nor the shape can be read the same
/// way, and both are unwrapped here.
/// </para>
/// </summary>
public sealed class HyperliquidHttp : IDisposable
{
    private readonly HttpClientWrapper _http;
    private readonly ILogger? _logger;
    private readonly bool _mainnet;
    private readonly object _nonceGate = new();
    private long _lastNonce;

    public HyperliquidHttp(IHyperliquidSettings settings, ILogger? logger = null, bool requireCredentials = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Credentials = requireCredentials
            ? HyperliquidVenue.Credentials(settings)
            : HyperliquidVenue.OptionalCredentials(settings);

        _http = new HttpClientWrapper(
            new Uri(HyperliquidVenue.HttpBase(settings)),
            new RateLimiter(HyperliquidVenue.RequestsPerWindow, HyperliquidVenue.RequestWindow),
            new RetryPolicy(),
            logger);

        _logger = logger;
        _mainnet = HyperliquidVenue.IsMainnet(settings);
    }

    /// <summary>The credential this client holds, or null when it has none and can only read.</summary>
    public HyperliquidCredentials? Credentials { get; }

    /// <summary>Whether this client signs for mainnet, which is one letter of every digest it makes.</summary>
    public bool IsMainnet => _mainnet;

    /// <summary>
    /// A read. <paramref name="type"/> is the venue's own request type and <paramref name="fields"/> the rest of the
    /// body - an address, a coin, a window - as strings and numbers already formatted.
    /// </summary>
    public async Task<JsonElement> InfoAsync(string type, IReadOnlyDictionary<string, object>? fields = null, CancellationToken ct = default)
    {
        string json = InfoBody(type, fields);
        using StringContent content = new(json, Encoding.UTF8, "application/json");
        string text;
        try
        {
            text = await _http.SendAsync(HttpMethod.Post, HyperliquidVenue.InfoPath, null, content, null, 1, ct).ConfigureAwait(false);
        }
        catch (VenueHttpException e)
        {
            // Not JSON: an unknown request type comes back as 422 with the text "Failed to deserialize the JSON body
            // into the target type" and nothing structured to read a code out of. Measured.
            throw new HyperliquidApiException($"{type}: {LogText.Truncate(e.Body, LogText.MaxBodyLength)}", (int)e.StatusCode, e);
        }

        using JsonDocument doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    /// <summary>The body of a read, exposed so a test can pin what one really looks like.</summary>
    public static string InfoBody(string type, IReadOnlyDictionary<string, object>? fields)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", type);
            foreach ((string name, object value) in fields ?? new Dictionary<string, object>(StringComparer.Ordinal))
            {
                switch (value)
                {
                    case string text:
                        writer.WriteString(name, text);
                        break;
                    case long number:
                        writer.WriteNumber(name, number);
                        break;
                    case int number:
                        writer.WriteNumber(name, number);
                        break;
                    case bool flag:
                        writer.WriteBoolean(name, flag);
                        break;

                    // One read nests: candleSnapshot puts its coin, interval and window inside a "req" object
                    // rather than beside the type like every other read does. So a field can be a group of fields.
                    case IReadOnlyDictionary<string, object> nested:
                        writer.WritePropertyName(name);
                        writer.WriteRawValue(InfoObject(nested));
                        break;
                    default:
                        throw new ArgumentException($"A Hyperliquid read carries strings, numbers, booleans and one nested group; {name} is a {value.GetType().Name}.", nameof(fields));
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The nested group of a read that has one, which today is only the candle window.</summary>
    private static string InfoObject(IReadOnlyDictionary<string, object> fields)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            foreach ((string name, object value) in fields)
            {
                switch (value)
                {
                    case string text:
                        writer.WriteString(name, text);
                        break;
                    case long number:
                        writer.WriteNumber(name, number);
                        break;
                    case int number:
                        writer.WriteNumber(name, number);
                        break;
                    default:
                        throw new ArgumentException($"A nested Hyperliquid read group carries strings and numbers; {name} is a {value.GetType().Name}.", nameof(fields));
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// The next nonce, which has to be one this account has not used. The venue refuses a repeat outright -
    /// measured: two actions signed with the same nonce came back "Invalid nonce: duplicate nonce 1700000000000" -
    /// so a millisecond clock is not enough on its own. Two orders placed inside one millisecond would otherwise
    /// see the second refused for a reason that reads like a clock problem and is really a collision.
    /// </summary>
    public long NextNonce()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (_nonceGate)
        {
            _lastNonce = Math.Max(now, _lastNonce + 1);
            return _lastNonce;
        }
    }

    /// <summary>
    /// A write: the action, a nonce, and the signature over both. The nonce is the millisecond clock, which is what
    /// the venue expects and what it rejects a replay by - so it has to move forward, and two actions signed in the
    /// same millisecond would collide.
    /// </summary>
    public async Task<JsonElement> ExchangeAsync(HyperliquidAction action, long nonce, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        HyperliquidCredentials credentials = Credentials
            ?? throw new InvalidOperationException("A Hyperliquid write is a signature, so it needs a private key; this client has none.");

        byte[] privateKey = credentials.PrivateKey
            ?? throw new InvalidOperationException($"This Hyperliquid client holds the account {credentials.Account} and no key, so it can read and not write.");

        HyperliquidSignature signature = HyperliquidSigner.Sign(action.Packed, nonce, [], _mainnet, privateKey);
        string body = ExchangeBody(action, nonce, signature);
        using StringContent content = new(body, Encoding.UTF8, "application/json");
        string text;
        try
        {
            text = await _http.SendAsync(HttpMethod.Post, HyperliquidVenue.ExchangePath, null, content, null, 1, ct).ConfigureAwait(false);
        }
        catch (VenueHttpException e)
        {
            throw new HyperliquidApiException($"{action.Type}: {LogText.Truncate(e.Body, LogText.MaxBodyLength)}", (int)e.StatusCode, e);
        }

        using JsonDocument doc = JsonDocument.Parse(text);
        JsonElement root = doc.RootElement;

        // A refusal arrives as HTTP 200 with the reason in the body, so the status says nothing. "Unable to recover
        // signer" is the one worth naming: it means the digest this adapter built is not the digest the venue built,
        // which is a fault in the action's encoding rather than anything about the key or the account.
        if (root.Str("status") == "err")
        {
            string reason = root.TryGetProperty("response", out JsonElement response) && response.ValueKind == JsonValueKind.String
                ? response.GetString() ?? string.Empty
                : root.GetRawText();

            throw new HyperliquidApiException($"{action.Type}: {reason}", (int)System.Net.HttpStatusCode.OK);
        }

        _logger?.LogDebug("Hyperliquid accepted a {Action} with nonce {Nonce}", action.Type, nonce);
        return root.TryGetProperty("response", out JsonElement ok) ? ok.Clone() : root.Clone();
    }

    /// <summary>The body of a write, exposed so a test can pin the three fields and their spelling.</summary>
    public static string ExchangeBody(HyperliquidAction action, long nonce, HyperliquidSignature signature)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(signature);
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();

            // The action goes in as raw JSON it built itself, because the bytes the signature covers were produced
            // from the same fields: rebuilding it here from a parsed copy would be a second chance to differ.
            writer.WritePropertyName("action");
            writer.WriteRawValue(action.Json);
            writer.WriteNumber("nonce", nonce);
            writer.WriteStartObject("signature");
            writer.WriteString("r", signature.R);
            writer.WriteString("s", signature.S);
            writer.WriteNumber("v", signature.V);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// A refusal from this venue. There is no error code to carry: <c>/info</c> refuses with a plain-text body and
/// <c>/exchange</c> with an English sentence, so the message is all there is and a caller cannot switch on anything.
/// </summary>
public sealed class HyperliquidApiException : Exception
{
    public HyperliquidApiException(string message, int httpStatus, Exception? inner = null)
        : base("Hyperliquid refused a request - " + message, inner)
    {
        HttpStatus = httpStatus;
        Detail = message;
    }

    /// <summary>The refusal without this class's own prefix, for a message that goes to a person.</summary>
    public string Detail { get; }

    public int HttpStatus { get; }
}

/// <summary>Reading this venue's JSON, which puts every number in a string except the ones it does not.</summary>
internal static class Json
{
    public static decimal Dec(this JsonElement e, string name) => e.TryGetProperty(name, out JsonElement p) ? p.DecValue() : 0m;

    public static decimal DecValue(this JsonElement p) => p.ValueKind switch
    {
        JsonValueKind.Number => p.GetDecimal(),
        JsonValueKind.String => decimal.TryParse(p.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal d) ? d : 0m,
        _ => 0m,
    };

    public static string Str(this JsonElement e, string name) => e.TryGetProperty(name, out JsonElement p) ? p.ValueKind switch
    {
        JsonValueKind.String => p.GetString() ?? string.Empty,
        JsonValueKind.Number => p.GetRawText(),
        _ => string.Empty,
    } : string.Empty;

    public static long Long(this JsonElement e, string name) => e.TryGetProperty(name, out JsonElement p) ? p.LongValue() : 0;

    public static long LongValue(this JsonElement p) => p.ValueKind switch
    {
        JsonValueKind.Number => p.GetInt64(),
        JsonValueKind.String => long.TryParse(p.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long l) ? l : 0,
        _ => 0,
    };

    public static bool Bool(this JsonElement e, string name) => e.TryGetProperty(name, out JsonElement p)
        && (p.ValueKind == JsonValueKind.True || (p.ValueKind == JsonValueKind.String && p.GetString() == "true"));

    public static bool Has(this JsonElement e, string name) => e.TryGetProperty(name, out JsonElement p) && p.ValueKind != JsonValueKind.Null;

    public static UnixNanos Ms(this JsonElement e, string name) => UnixNanos.FromMilliseconds(e.Long(name));

    public static string Fmt(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A decimal without a trailing zero or a trailing point, which is how a price and a size have to reach this
    /// venue: it counts the decimal places of the string it was sent, so "83000.0" is one decimal place where
    /// "83000" is none, and on an asset that allows none the first is refused.
    /// </summary>
    public static string Trim(decimal value)
    {
        string text = value.ToString("0.############################", CultureInfo.InvariantCulture);
        return text.Length == 0 ? "0" : text;
    }

    public static byte Precision(decimal increment)
    {
        string text = increment.ToString(CultureInfo.InvariantCulture);
        if (text.Contains('.', StringComparison.Ordinal))
        {
            text = text.TrimEnd('0');
        }

        int dot = text.IndexOf('.', StringComparison.Ordinal);
        return (byte)(dot < 0 ? 0 : text.Length - dot - 1);
    }
}
