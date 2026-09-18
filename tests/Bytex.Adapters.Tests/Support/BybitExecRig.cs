using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bytex.Adapters.Bybit;
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
/// A Bybit execution client wired to a stub venue on 127.0.0.1, with dummy credentials and one instrument in the cache.
/// </summary>
internal sealed class BybitExecRig : IAsyncDisposable
{
    public const string ApiKey = "test-key";
    public const string ApiSecret = "test-secret";

    public BybitExecRig(BybitProductType type, TriggerType defaultTrigger = TriggerType.LastPrice)
    {
        Linear = type == BybitProductType.Linear;
        Routes = new Routes().On("GET", "/v5/market/instruments-info", Linear ? BybitPayloads.LinearInstrumentsPage1.Replace("cursor-page-2", string.Empty, StringComparison.Ordinal) : BybitPayloads.SpotInstruments);
        Server = new LoopbackServer(Routes.Handle);
        Kernel = new TestKernel();
        Instrument = Linear ? Perpetual() : Spot();
        Kernel.Kernel.Cache.AddInstrument(Instrument);
        Client = new BybitExecutionClient(new ClientId("BYBIT"), new BybitExecutionClientConfig
        {
            ProductType = type,
            ApiKey = ApiKey,
            ApiSecret = ApiSecret,
            BaseUrlHttp = Server.HttpBase,
            BaseUrlWs = Server.WsBase,
            RecvWindowMs = 8000,
            DefaultTriggerType = defaultTrigger,
            InstrumentProvider = new Core.Adapters.InstrumentProviderConfig { LoadAll = true },
        }, Kernel.Services);
        Client.AttachSink(Sink);
        Orders = new OrderFactory(Kernel.Services.TraderId, Strategy, Kernel.Clock);
    }

    public static StrategyId Strategy { get; } = new("Probe-001");

    public bool Linear { get; }

    public Routes Routes { get; }

    public LoopbackServer Server { get; }

    public TestKernel Kernel { get; }

    public Instrument Instrument { get; }

    public BybitExecutionClient Client { get; }

    public RecordingExecutionSink Sink { get; } = new();

    public OrderFactory Orders { get; }

    public string Category => Linear ? "linear" : "spot";

    public Quantity Qty(decimal value) => Instrument.MakeQuantity(value);

    public Price Px(decimal value) => Instrument.MakePrice(value);

    public static CurrencyPair Spot() => new(new InstrumentSpec
    {
        Id = InstrumentId.Parse("BTCUSDT.BYBIT"),
        RawSymbol = new Symbol("BTCUSDT"),
        AssetClass = AssetClass.Crypto,
        InstrumentClass = InstrumentClass.Spot,
        QuoteCurrency = Currencies.USDT,
        BaseCurrency = Currencies.BTC,
        PricePrecision = 2,
        SizePrecision = 6,
        PriceIncrement = new Price(0.01m, 2),
        SizeIncrement = new Quantity(0.000001m, 6),
    });

    public static CryptoPerpetual Perpetual() => new(new InstrumentSpec
    {
        Id = InstrumentId.Parse("BTCUSDT-PERP.BYBIT"),
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

    public Task SubmitAsync(Order order)
    {
        Kernel.Kernel.Cache.AddOrder(order);
        return Client.SubmitOrderAsync(new SubmitOrder(Kernel.Services.TraderId, Strategy, order, null, null, null, Guid.NewGuid(), TestKernel.Now), CancellationToken.None).WaitAsync(Wait.Timeout);
    }

    /// <summary>Connects, answers the authentication handshake the way Bybit does and returns the private stream.</summary>
    public async Task<(WsSession Session, string AuthMessage, string SubscribeMessage)> ConnectAsync(string walletResult)
    {
        Routes.On("GET", "/v5/account/wallet-balance", BybitPayloads.Envelope(walletResult));
        await Client.ConnectAsync(CancellationToken.None).WaitAsync(Wait.Timeout);
        WsSession session = await Server.NextSessionAsync();
        string auth = await session.ReceiveTextAsync();
        await session.SendTextAsync("""{"success":true,"ret_msg":"","op":"auth","conn_id":"cejreaspqfh3sjdnldmg-p"}""");
        string subscribe = await session.ReceiveTextAsync();
        return (session, auth, subscribe);
    }

    /// <summary>
    /// Checks a request the way Bybit V5 does: X-BAPI-SIGN = HMAC-SHA256(secret, timestamp + apiKey + recvWindow + payload),
    /// where the payload is the raw query string for GET and the raw JSON body for POST.
    /// </summary>
    public static void AssertSigned(RecordedRequest request)
    {
        string timestamp = request.Header("X-BAPI-TIMESTAMP") ?? throw new Xunit.Sdk.XunitException("X-BAPI-TIMESTAMP header missing");
        string payload = request.Method == "GET" ? request.RawQuery : request.Body;
        string expected = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(ApiSecret), Encoding.UTF8.GetBytes(timestamp + ApiKey + "8000" + payload)));
        Assert.Equal(expected, request.Header("X-BAPI-SIGN"));
        Assert.Equal(ApiKey, request.Header("X-BAPI-API-KEY"));
        Assert.Equal("8000", request.Header("X-BAPI-RECV-WINDOW"));
        long sentAt = long.Parse(timestamp, CultureInfo.InvariantCulture);
        Assert.InRange(sentAt, DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds(), DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds());
    }

    /// <summary>
    /// Compares a JSON body with an expectation written as "key=value;key=value". Numbers are compared numerically
    /// whether the adapter sent them as JSON numbers or as strings; "true"/"false" match JSON booleans.
    /// </summary>
    public static void AssertBody(RecordedRequest request, string expected, string absent)
    {
        using JsonDocument doc = JsonDocument.Parse(request.Body);
        foreach (string pair in expected.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] kv = pair.Split('=', 2);
            Assert.True(doc.RootElement.TryGetProperty(kv[0], out JsonElement actual), $"field '{kv[0]}' is missing from {request.Body}");
            string text = actual.ValueKind switch
            {
                JsonValueKind.String => actual.GetString()!,
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => actual.GetRawText(),
            };
            if (decimal.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out decimal number))
            {
                Assert.Equal(number, decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture));
            }
            else
            {
                Assert.Equal(kv[1], text);
            }
        }

        foreach (string key in absent.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            Assert.False(doc.RootElement.TryGetProperty(key, out _), $"field '{key}' must not be sent but was: {request.Body}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        Kernel.Dispose();
        await Server.DisposeAsync();
    }
}
