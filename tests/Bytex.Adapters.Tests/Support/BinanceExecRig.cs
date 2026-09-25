using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Bytex.Adapters.Binance;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Trading;

namespace Bytex.Adapters.Tests.Support;

/// <summary>
/// A Binance execution client wired to a stub venue on 127.0.0.1, with dummy credentials and one instrument in the cache.
/// </summary>
internal sealed class BinanceExecRig : IAsyncDisposable
{
    public const string ApiKey = "test-key";
    public const string ApiSecret = "test-secret";

    public BinanceExecRig(BinanceAccountType type, TriggerType defaultTrigger = TriggerType.LastPrice, string? brokerId = null, decimal? leverage = null)
    {
        AccountType = type;
        Routes = VenueRoutes();
        Server = new LoopbackServer(Routes.Handle);
        Kernel = new TestKernel();
        Instrument = InstrumentFor(type);
        Kernel.Kernel.Cache.AddInstrument(Instrument);
        Client = new BinanceExecutionClient(new ClientId("BINANCE"), new BinanceExecutionClientConfig
        {
            AccountType = type,
            ApiKey = ApiKey,
            ApiSecret = ApiSecret,
            BaseUrlHttp = Server.HttpBase,
            BaseUrlWs = Server.WsBase,
            RecvWindowMs = 7000,
            DefaultTriggerType = defaultTrigger,
            BrokerId = brokerId,
            Leverage = leverage,
        }, Kernel.Services);
        Client.AttachSink(Sink);
        Orders = new OrderFactory(Kernel.Services.TraderId, Strategy, Kernel.Clock);
    }

    /// <summary>
    /// The same rig, configured the way a host configures one: a trading node is handed a client entry whose config
    /// is JSON and deserialises it into the type the factory declares. Building the config in code - what every other
    /// test here does - proves an adapter honours a field, not that anybody can reach it.
    /// </summary>
    /// <param name="type">Which Binance account the client trades, as the configuration also states.</param>
    /// <param name="config">
    /// The JSON, given the stub venue's addresses, which are not known until its server is listening.
    /// </param>
    public BinanceExecRig(BinanceAccountType type, Func<string, string, string> config)
    {
        AccountType = type;
        Routes = VenueRoutes();
        Server = new LoopbackServer(Routes.Handle);
        Kernel = new TestKernel();
        Instrument = InstrumentFor(type);
        Kernel.Kernel.Cache.AddInstrument(Instrument);

        BinanceExecutionClientFactory factory = new();
        object? coerced = System.Text.Json.JsonSerializer.Deserialize(
            config(Server.HttpBase, Server.WsBase), factory.ConfigType, Core.Serialization.BytexJson.Options);
        Client = (BinanceExecutionClient)factory.Create(
            new ClientId("BINANCE"), (Core.Adapters.ExecutionClientConfig)coerced!, Kernel.Services);

        Client.AttachSink(Sink);
        Orders = new OrderFactory(Kernel.Services.TraderId, Strategy, Kernel.Clock);
    }

    public static StrategyId Strategy { get; } = new("Probe-001");

    /// <summary>Which of the venue's three markets this rig trades, which decides every path below.</summary>
    public BinanceAccountType AccountType { get; }

    public bool Futures => BinanceVenue.IsFutures(AccountType);

    public Routes Routes { get; }

    public LoopbackServer Server { get; }

    public TestKernel Kernel { get; }

    public Instrument Instrument { get; }

    public BinanceExecutionClient Client { get; }

    public RecordingExecutionSink Sink { get; } = new();

    public OrderFactory Orders { get; }

    public string OrderPath => BinanceVenue.ApiPrefix(AccountType) + "/order";

    public Quantity Qty(decimal value) => Instrument.MakeQuantity(value);

    public Price Px(decimal value) => Instrument.MakePrice(value);

    /// <summary>The catalog route of each of the venue's markets, so a rig for any of them finds its own.</summary>
    private static Routes VenueRoutes() => new Routes()
        .On("GET", "/api/v3/exchangeInfo", BinancePayloads.SpotExchangeInfo)
        .On("GET", "/fapi/v1/exchangeInfo", BinancePayloads.FuturesExchangeInfo)
        .On("GET", "/dapi/v1/exchangeInfo", BinancePayloads.CoinMExchangeInfo);

    /// <summary>The one instrument a rig trades, in the shape the configured market really holds.</summary>
    private static Instrument InstrumentFor(BinanceAccountType type) => type switch
    {
        BinanceAccountType.UsdMFutures => Perpetual(),
        BinanceAccountType.CoinMFutures => CoinMPerpetual(),
        _ => Spot(),
    };

    public static CurrencyPair Spot() => new(new InstrumentSpec
    {
        Id = InstrumentId.Parse("BTCUSDT.BINANCE"),
        RawSymbol = new Symbol("BTCUSDT"),
        AssetClass = AssetClass.Crypto,
        InstrumentClass = InstrumentClass.Spot,
        QuoteCurrency = Currencies.USDT,
        BaseCurrency = Currencies.BTC,
        PricePrecision = 2,
        SizePrecision = 5,
        PriceIncrement = new Price(0.01m, 2),
        SizeIncrement = new Quantity(0.00001m, 5),
    });

    public static CryptoPerpetual Perpetual() => new(new InstrumentSpec
    {
        Id = InstrumentId.Parse("BTCUSDT-PERP.BINANCE"),
        RawSymbol = new Symbol("BTCUSDT"),
        AssetClass = AssetClass.Crypto,
        InstrumentClass = InstrumentClass.Swap,
        QuoteCurrency = Currencies.USDT,
        BaseCurrency = Currencies.BTC,
        SettlementCurrency = Currencies.USDT,
        PricePrecision = 1,
        SizePrecision = 3,
        PriceIncrement = new Price(0.1m, 1),
        SizeIncrement = new Quantity(0.001m, 3),
    });

    /// <summary>
    /// The coin-margined perpetual as its own family really publishes it: the venue's own spelling, sized in whole
    /// 100-USD contracts, inverse, and settled in the coin. Not the USD-margined one with a different id - the
    /// differences here are the ones an order's quantity and every money figure go through.
    /// </summary>
    /// <param name="maxLeverage">
    /// What the venue grants here, and null by default rather than a number: this venue publishes no ceiling
    /// without a key, so a rig that carried one by default would describe a state no unauthenticated run is ever
    /// in. A test of the leverage guard passes the figure its own bracket fixture answers with.
    /// </param>
    public static CryptoPerpetual CoinMPerpetual(decimal? maxLeverage = null) => new(new InstrumentSpec
    {
        Id = InstrumentId.Parse("BTCUSD_PERP.BINANCE"),
        RawSymbol = new Symbol("BTCUSD_PERP"),
        AssetClass = AssetClass.Crypto,
        InstrumentClass = InstrumentClass.Swap,
        QuoteCurrency = Currencies.USD,
        BaseCurrency = Currencies.BTC,
        SettlementCurrency = Currencies.BTC,
        IsInverse = true,
        PricePrecision = 1,
        SizePrecision = 0,
        PriceIncrement = new Price(0.1m, 1),
        SizeIncrement = new Quantity(1m, 0),
        Multiplier = new Quantity(100m, 0),
        MaxLeverage = maxLeverage,
    });

    public static CryptoFuture DatedFuture() => new(
        new InstrumentSpec
        {
            Id = InstrumentId.Parse("BTCUSDT_250926.BINANCE"),
            RawSymbol = new Symbol("BTCUSDT_250926"),
            AssetClass = AssetClass.Crypto,
            InstrumentClass = InstrumentClass.Future,
            QuoteCurrency = Currencies.USDT,
            BaseCurrency = Currencies.BTC,
            SettlementCurrency = Currencies.USDT,
            PricePrecision = 1,
            SizePrecision = 3,
            PriceIncrement = new Price(0.1m, 1),
            SizeIncrement = new Quantity(0.001m, 3),
        },
        Currencies.BTC,
        UnixNanos.FromMilliseconds(1_742_544_000_000),
        UnixNanos.FromMilliseconds(1_758_873_600_000));

    public Task SubmitAsync(Order order)
    {
        Kernel.Kernel.Cache.AddOrder(order);
        return Client.SubmitOrderAsync(new SubmitOrder(Kernel.Services.TraderId, Strategy, order, null, null, null, Guid.NewGuid(), TestKernel.Now), CancellationToken.None).WaitAsync(Wait.Timeout);
    }

    /// <summary>Stubs the endpoints used while connecting and returns the accepted user-data stream.</summary>
    public async Task<WsSession> ConnectAsync(string accountJson, params string[] listenKeys)
    {
        int issued = 0;
        string[] keys = listenKeys.Length == 0 ? ["listen-key-1"] : listenKeys;
        string listenKeyPath = BinanceVenue.ListenKeyPath(AccountType);
        Routes.On("GET", BinanceVenue.BalancePath(AccountType), accountJson)
            .On("POST", listenKeyPath, _ => StubResponse.Json($"{{\"listenKey\":\"{keys[Math.Min(Interlocked.Increment(ref issued), keys.Length) - 1]}\"}}"))
            .On("PUT", listenKeyPath, "{}")
            .On("DELETE", listenKeyPath, "{}");
        await Client.ConnectAsync(CancellationToken.None).WaitAsync(Wait.Timeout);
        return await Server.NextSessionAsync();
    }

    /// <summary>
    /// Checks a request the way Binance does: HMAC-SHA256 of everything before "&amp;signature=", keyed with the secret,
    /// plus the API key header, a fresh timestamp and the configured recvWindow.
    /// </summary>
    public static void AssertSigned(RecordedRequest request)
    {
        int marker = request.RawQuery.LastIndexOf("&signature=", StringComparison.Ordinal);
        Assert.True(marker > 0, "signature must be the last query parameter");
        string payload = request.RawQuery[..marker];
        string signature = request.RawQuery[(marker + "&signature=".Length)..];
        string expected = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(ApiSecret), Encoding.UTF8.GetBytes(payload)));
        Assert.Equal(expected, signature);
        Assert.Equal(ApiKey, request.Header("X-MBX-APIKEY"));
        Assert.Equal("7000", request.Query("recvWindow"));
        long timestamp = long.Parse(request.Query("timestamp")!, CultureInfo.InvariantCulture);
        Assert.InRange(timestamp, DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds(), DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds());
    }

    /// <summary>
    /// Compares request parameters with an expectation written as "key=value;key=value". Values that are numbers are
    /// compared numerically, so "0.50000" satisfies "0.5": the venue parses them the same way.
    /// </summary>
    public static void AssertParameters(RecordedRequest request, string expected, string absent)
    {
        foreach (string pair in expected.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] kv = pair.Split('=', 2);
            string? actual = request.Query(kv[0]);
            Assert.True(actual is not null, $"parameter '{kv[0]}' is missing from {request.RawQuery}");
            if (decimal.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out decimal number))
            {
                Assert.Equal(number, decimal.Parse(actual, NumberStyles.Float, CultureInfo.InvariantCulture));
            }
            else
            {
                Assert.Equal(kv[1], actual);
            }
        }

        foreach (string key in absent.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            Assert.True(request.Query(key) is null, $"parameter '{key}' must not be sent but was: {request.RawQuery}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        Kernel.Dispose();
        await Server.DisposeAsync();
    }
}
