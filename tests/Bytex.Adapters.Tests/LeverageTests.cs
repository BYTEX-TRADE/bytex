using System.Text.Json;
using Bytex.Adapters.Binance;
using Bytex.Adapters.Bybit;
using Bytex.Adapters.Kucoin;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Trading;

namespace Bytex.Adapters.Tests;

// Why: a strategy written for 3x, backtested at 3x and papered at 3x went LIVE at whatever the account happened to
// be left on, and nothing said so. A backtest and a live run disagreeing about the size of every position is the
// failure that makes every test before it worthless.
//
// The cause was that leverage reached the simulator and reached no venue. The two shapes it has to cover are not the
// same: KuCoin's perpetual futures DEMAND a leverage on every order and have no default of their own, while Binance
// and Bybit hold it per symbol on the account and IGNORE anything sent with an order. So one field on every execution
// config, and each adapter honours it the way its venue works - sent per order on one, set at the venue on the others.
//
// Null still means "do not touch it", which is what every configuration written before the field existed means. That
// is the property worth guarding: a field nobody set must not start changing accounts.
public sealed class LeverageTests
{
    private const string Key = "k";
    private const string Secret = "s";

    // ----- one field, every venue -----

    [Fact]
    public void Leverage_is_on_every_execution_config_rather_than_one_venues()
    {
        // The reason it moved. A host setting what a strategy was written for should not have to know which venues
        // take leverage per order and which hold it as account state, so it cannot be a KuCoin-only field.
        foreach (Type config in new[]
        {
            typeof(BinanceExecutionClientConfig),
            typeof(BybitExecutionClientConfig),
            typeof(KucoinExecutionClientConfig),
        })
        {
            Assert.True(
                typeof(ExecutionClientConfig).IsAssignableFrom(config),
                $"{config.Name} should inherit the shared execution config");

            Assert.NotNull(config.GetProperty("Leverage"));

            // A decimal, and pinned as one. A strategy document carries leverage as a decimal and so does the
            // simulated venue, so a whole number here would force whoever crosses into live to round - silently, in
            // a direction they chose, changing the size of every position from the one that was backtested. It
            // happened: a host was flooring 2.5 to 2 to cross this gap. Where a venue really does take only whole
            // numbers its own adapter refuses the fraction, which is a sentence somebody can read.
            Assert.Equal(typeof(decimal?), config.GetProperty("Leverage")!.PropertyType);
        }
    }

    [Fact]
    public void Nothing_is_set_when_no_leverage_is_configured()
    {
        // Null is what every configuration written before the field existed means, and it has to keep meaning it:
        // a field nobody set must not start changing accounts.
        Assert.Null(new BinanceExecutionClientConfig { ApiKey = Key, ApiSecret = Secret }.Leverage);
        Assert.Null(new BybitExecutionClientConfig { ApiKey = Key, ApiSecret = Secret }.Leverage);
        Assert.Null(new KucoinExecutionClientConfig { ApiKey = Key, ApiSecret = Secret, ApiPassphrase = "p" }.Leverage);
    }

    // ----- KuCoin: sent with every order, because the venue demands one -----

    private static async Task<(LoopbackServer Server, KucoinFuturesExecutionClient Client, TestKernel Kernel)> KucoinAsync(decimal? leverage)
    {
        LoopbackServer server = new(new Routes()
            .On("GET", "/api/v1/contracts/active", KucoinPayloads.FuturesContracts)
            .On("GET", "/api/v1/account-overview", KucoinPayloads.Envelope("""{"accountEquity":1.0,"availableBalance":1.0}"""))
            .On("POST", "/api/v1/orders", KucoinPayloads.Envelope("""{"orderId":"1"}"""))
            .Handle);

        TestKernel kernel = new();
        KucoinFuturesExecutionClient client = new(new ClientId("KUCOIN"), new KucoinExecutionClientConfig
        {
            ProductType = KucoinProductType.Futures,
            Leverage = leverage,
            ApiKey = "6705f5c311545b000157d3eb",
            ApiSecret = "c1f1e3e4-5a6b-4c7d-8e9f-0a1b2c3d4e5f",
            ApiPassphrase = "bytex-test",
            BaseUrlHttp = server.HttpBase,
            InstrumentProvider = new InstrumentProviderConfig { LoadAll = true },
        }, kernel.Services);

        client.AttachSink(new RecordingExecutionSink());
        await client.Instruments.LoadAllAsync(CancellationToken.None);
        return (server, client, kernel);
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData(3, 3)]
    [InlineData(20, 20)]
    public async Task KuCoin_sends_the_leverage_on_the_order_because_the_venue_demands_one(int? configured, int expected)
    {
        (LoopbackServer server, KucoinFuturesExecutionClient client, TestKernel kernel) = await KucoinAsync(configured);
        await using LoopbackServer _ = server;
        using TestKernel __ = kernel;

        Instrument contract = client.Instruments.Find(InstrumentId.Parse("XBTUSDT-PERP.KUCOIN"))!;
        StrategyId strategy = new("Probe-001");
        OrderFactory orders = new(kernel.Services.TraderId, strategy, kernel.Clock);
        MarketOrder order = orders.Market(contract.Id, OrderSide.Buy, contract.MakeQuantity(0.001m));
        kernel.Kernel.Cache.AddOrder(order);

        await client.SubmitOrderAsync(
            new SubmitOrder(kernel.Services.TraderId, strategy, order, null, null, null, Guid.NewGuid(), TestKernel.Now),
            CancellationToken.None).WaitAsync(Wait.Timeout);

        using JsonDocument body = JsonDocument.Parse(server.RequestsTo("/api/v1/orders").Last().Body);

        // Unconfigured is 1 - no leverage - because this venue has no default of its own and something must supply
        // one. Any other default would lever a position nobody asked to lever.
        Assert.Equal(expected, body.RootElement.GetProperty("leverage").GetInt32());
        client.Dispose();
    }

    // ----- Binance and Bybit: set at the venue, because an order carrying one is ignored -----

    [Fact]
    public async Task Binance_sets_the_leverage_at_the_venue_before_it_trades()
    {
        await using BinanceExecRig rig = new(BinanceAccountType.UsdMFutures, leverage: 3);
        await rig.ConnectAsync("""{"assets":[],"positions":[]}""");

        RecordedRequest set = Assert.Single(rig.Server.RequestsTo(BinanceVenue.LeveragePath));
        Assert.Equal("POST", set.Method);
        Assert.Equal(rig.Instrument.RawSymbol!.Value, set.Query("symbol"));
        Assert.Equal("3", set.Query("leverage"));
    }

    [Fact]
    public async Task Binance_touches_nothing_when_no_leverage_is_configured()
    {
        await using BinanceExecRig rig = new(BinanceAccountType.UsdMFutures);
        await rig.ConnectAsync("""{"assets":[],"positions":[]}""");

        Assert.Empty(rig.Server.RequestsTo(BinanceVenue.LeveragePath));
    }

    [Fact]
    public async Task Binance_spot_has_no_leverage_to_set()
    {
        await using BinanceExecRig rig = new(BinanceAccountType.Spot, leverage: 3);
        await rig.ConnectAsync("""{"balances":[{"asset":"USDT","free":"1","locked":"0"}]}""");

        Assert.Empty(rig.Server.RequestsTo(BinanceVenue.LeveragePath));
    }

    [Fact]
    public async Task A_venue_refusing_the_leverage_does_not_stop_the_node()
    {
        // Usually the venue saying the account is not entitled to the figure asked for, which a node cannot fix. It
        // is logged and the node starts, because abandoning a start silently would be worse than trading at a
        // leverage a person can then read about and change.
        await using BinanceExecRig rig = new(BinanceAccountType.UsdMFutures, leverage: 125);
        rig.Routes.On("POST", BinanceVenue.LeveragePath, _ => new StubResponse(400, """{"code":-4028,"msg":"Leverage 125 is not valid"}"""));

        await rig.ConnectAsync("""{"assets":[],"positions":[]}""");

        Assert.True(rig.Client.IsConnected, "a refused leverage must not stop the node from starting");
        Assert.NotEmpty(rig.Server.RequestsTo(BinanceVenue.LeveragePath));
    }

    [Fact]
    public async Task Bybit_sets_both_sides_of_the_leverage_at_the_venue()
    {
        await using BybitExecRig rig = new(BybitProductType.Linear, leverage: 5);
        rig.Routes.On("POST", BybitVenue.LeveragePath, BybitPayloads.Envelope("{}"));
        await rig.ConnectAsync("""{"list":[]}""");

        RecordedRequest set = Assert.Single(rig.Server.RequestsTo(BybitVenue.LeveragePath));
        using JsonDocument body = JsonDocument.Parse(set.Body);

        // The venue takes the two sides separately and refuses a one-sided change on a netting account.
        Assert.Equal("5", body.RootElement.GetProperty("buyLeverage").GetString());
        Assert.Equal("5", body.RootElement.GetProperty("sellLeverage").GetString());
        Assert.Equal(rig.Instrument.RawSymbol!.Value, body.RootElement.GetProperty("symbol").GetString());
    }

    [Fact]
    public async Task Bybit_treats_the_leverage_already_being_right_as_success()
    {
        RecordingLogs logs = new();
        // The venue refuses a change to the figure already in force. The account is exactly where it was asked to
        // be, so reporting that as a failure would put an error in front of somebody for nothing.
        await using BybitExecRig rig = new(BybitProductType.Linear, leverage: 5, logs: logs);
        rig.Routes.On("POST", BybitVenue.LeveragePath, BybitPayloads.Error(BybitVenue.ErrorLeverageUnchanged, "leverage not modified"));

        await rig.ConnectAsync("""{"list":[]}""");

        Assert.True(rig.Client.IsConnected);
        Assert.NotEmpty(rig.Server.RequestsTo(BybitVenue.LeveragePath));

        // And nothing was reported as wrong. Without recognising the code this reaches the general handler and puts
        // an error in front of somebody whose account is exactly where they asked for it to be.
        Assert.DoesNotContain(logs.Errors, e => e.Contains("refused", StringComparison.Ordinal));
    }

    // ----- a leverage that is not a whole number -----

    [Fact]
    public async Task Bybit_sends_a_fractional_leverage_as_written()
    {
        // A document carries leverage as a decimal, so 2.5 is reachable. This venue takes the figure as a string and
        // is entitled to accept or refuse it - which it says for itself. What must not happen is the number being
        // rounded on the way here, because that silently changes the size of every position from the one that was
        // backtested.
        await using BybitExecRig rig = new(BybitProductType.Linear, leverage: 2.5m);
        rig.Routes.On("POST", BybitVenue.LeveragePath, BybitPayloads.Envelope("{}"));
        await rig.ConnectAsync("""{"list":[]}""");

        using JsonDocument body = JsonDocument.Parse(Assert.Single(rig.Server.RequestsTo(BybitVenue.LeveragePath)).Body);

        Assert.Equal("2.5", body.RootElement.GetProperty("buyLeverage").GetString());
        Assert.Equal("2.5", body.RootElement.GetProperty("sellLeverage").GetString());
    }

    [Fact]
    public void Binance_refuses_a_fractional_leverage_when_the_client_is_built()
    {
        // This venue documents the field as an integer, so a fraction cannot be honoured. Refused here rather than
        // rounded: rounding picks a direction on somebody's behalf, and a position 20 percent smaller than the one
        // that was tested is not something any result would show. Refused at construction rather than at connect, so
        // it is a configuration error and not a half-started node.
        using TestKernel kernel = new();

        ArgumentOutOfRangeException refused = Assert.Throws<ArgumentOutOfRangeException>(() => new BinanceExecutionClient(
            new ClientId("BINANCE"),
            new BinanceExecutionClientConfig
            {
                AccountType = BinanceAccountType.UsdMFutures,
                ApiKey = "k",
                ApiSecret = "s",
                Leverage = 2.5m,
            },
            kernel.Services));

        Assert.Contains("whole-number", refused.Message, StringComparison.Ordinal);
        Assert.Contains("2.5", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Binance_spot_has_no_leverage_to_refuse()
    {
        // A spot account never sends one, so there is nothing to be fractional. Refusing here would reject a
        // configuration that is harmless, which is its own way of losing a capability.
        using TestKernel kernel = new();

        BinanceExecutionClient client = new(
            new ClientId("BINANCE"),
            new BinanceExecutionClientConfig
            {
                AccountType = BinanceAccountType.Spot,
                ApiKey = "k",
                ApiSecret = "s",
                Leverage = 2.5m,
            },
            kernel.Services);

        client.Dispose();
    }

    [Fact]
    public async Task KuCoin_sends_a_fractional_leverage_on_the_order()
    {
        (LoopbackServer server, KucoinFuturesExecutionClient client, TestKernel kernel) = await KucoinAsync(2.5m);
        await using LoopbackServer _ = server;
        using TestKernel __ = kernel;

        Instrument contract = client.Instruments.Find(InstrumentId.Parse("XBTUSDT-PERP.KUCOIN"))!;
        StrategyId strategy = new("Probe-001");
        OrderFactory orders = new(kernel.Services.TraderId, strategy, kernel.Clock);
        MarketOrder order = orders.Market(contract.Id, OrderSide.Buy, contract.MakeQuantity(0.001m));
        kernel.Kernel.Cache.AddOrder(order);

        await client.SubmitOrderAsync(
            new SubmitOrder(kernel.Services.TraderId, strategy, order, null, null, null, Guid.NewGuid(), TestKernel.Now),
            CancellationToken.None).WaitAsync(Wait.Timeout);

        using JsonDocument body = JsonDocument.Parse(server.RequestsTo("/api/v1/orders").Last().Body);

        Assert.Equal(2.5m, body.RootElement.GetProperty("leverage").GetDecimal());
        client.Dispose();
    }

    [Fact]
    public async Task Bybit_spot_has_no_leverage_to_set()
    {
        await using BybitExecRig rig = new(BybitProductType.Spot, leverage: 5);
        await rig.ConnectAsync("""{"list":[]}""");

        Assert.Empty(rig.Server.RequestsTo(BybitVenue.LeveragePath));
    }
}
