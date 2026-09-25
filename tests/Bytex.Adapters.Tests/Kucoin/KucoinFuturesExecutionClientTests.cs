using System.Text.Json;
using Bytex.Adapters.Kucoin;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Model.Reports;
using Bytex.Core.Trading;

namespace Bytex.Adapters.Tests.Kucoin;

// Why: this is the market where an order is denominated in something the engine never uses. A contract on XBTUSDTM is
// 0.001 XBT and the venue takes whole numbers of them, so every quantity crossing this boundary is divided on the way
// out and multiplied on the way back - orders, fills and positions alike. Get the direction wrong and an order for
// 0.084 XBT becomes 84 XBT, which is a real order at eighty-four times the intended size.
//
// The venue also requires a leverage on every order and offers no default, so one is stated here. It is 1, and that
// is a decision rather than an accident: any other default levers a position nobody asked to lever.
//
// The private side cannot be exercised against the real venue without a key, so the shapes below are the venue's own
// published ones and what it is NOT known to do is listed in docs/integrations/kucoin.md.
public sealed class KucoinFuturesExecutionClientTests
{
    private const string ApiKey = "6705f5c311545b000157d3eb";
    private const string ApiSecret = "c1f1e3e4-5a6b-4c7d-8e9f-0a1b2c3d4e5f";
    private const string Passphrase = "bytex-test";

    /// <summary>One contract of XBTUSDTM, in XBT.</summary>
    private const decimal Contract = 0.001m;

    private sealed class Rig : IAsyncDisposable
    {
        private WsSession? _session;

        public Rig(int leverage = 1)
        {
            Routes = new Routes()
                .On("GET", "/api/v1/contracts/active", KucoinPayloads.FuturesContracts)
                .On("POST", "/api/v1/bullet-private", _ => StubResponse.Json(KucoinPayloads.Bullet(Server!.WsBase, "private-token-1")))
                .On("GET", "/api/v1/account-overview", KucoinPayloads.Envelope("""
                    {"accountEquity":100000.0,"availableBalance":99000.0,"currency":"USDT"}
                    """))
                .On("POST", "/api/v1/orders", KucoinPayloads.Envelope("""{"orderId":"492813530635034624"}"""))
                .On("GET", "/api/v1/positions", KucoinPayloads.Envelope("[]"))
                .On("GET", "/api/v1/orders", KucoinPayloads.Envelope("""{"items":[]}"""))
                .On("GET", "/api/v1/fills", KucoinPayloads.Envelope("""{"items":[]}"""));

            Server = new LoopbackServer(Routes.Handle);
            Kernel = new TestKernel();
            Client = new KucoinFuturesExecutionClient(new ClientId("KUCOIN"), new KucoinExecutionClientConfig
            {
                ProductType = KucoinProductType.Futures,
                Leverage = leverage,
                ApiKey = ApiKey,
                ApiSecret = ApiSecret,
                ApiPassphrase = Passphrase,
                ApiKeyVersion = "3",
                BaseUrlHttp = Server.HttpBase,
                InstrumentProvider = new Core.Adapters.InstrumentProviderConfig { LoadAll = true },
            }, Kernel.Services);

            Client.AttachSink(Sink);
            Orders = new OrderFactory(Kernel.Services.TraderId, Strategy, Kernel.Clock);
        }

        public static StrategyId Strategy { get; } = new("Probe-001");

        public Routes Routes { get; }

        public LoopbackServer Server { get; }

        public TestKernel Kernel { get; }

        public KucoinFuturesExecutionClient Client { get; }

        public RecordingExecutionSink Sink { get; } = new();

        public OrderFactory Orders { get; }

        public WsSession Session => _session ?? throw new InvalidOperationException("not connected");

        public Instrument Contract => Client.Instruments.Find(InstrumentId.Parse("XBTUSDT-PERP.KUCOIN"))!;

        public Quantity Qty(decimal value) => Contract.MakeQuantity(value);

        public Price Px(decimal value) => Contract.MakePrice(value);

        public async Task<Rig> ConnectAsync()
        {
            await Client.ConnectAsync(CancellationToken.None).WaitAsync(Wait.Timeout);
            _session = await Server.NextSessionAsync();
            await _session.SendTextAsync(KucoinPayloads.Welcome);
            return this;
        }

        public T Track<T>(T order) where T : Order
        {
            Kernel.Kernel.Cache.AddOrder(order);
            return order;
        }

        public Task SubmitAsync(Order order) =>
            Client.SubmitOrderAsync(new SubmitOrder(Kernel.Services.TraderId, Strategy, order, null, null, null, Guid.NewGuid(), TestKernel.Now), CancellationToken.None).WaitAsync(Wait.Timeout);

        public JsonElement Body(string path)
        {
            using JsonDocument doc = JsonDocument.Parse(Server.RequestsTo(path).Last().Body);
            return doc.RootElement.Clone();
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisconnectAsync(CancellationToken.None);
            Client.Dispose();
            Kernel.Dispose();
            await Server.DisposeAsync();
        }
    }

    /// <summary>A match on the private order stream, with the size in CONTRACTS as the venue sends it.</summary>
    private static string Match(ClientOrderId clientOrderId, string size, string tradeId) =>
        """
        {"topic":"/contractMarket/tradeOrders","type":"message","subject":"orderChange","data":
         {"symbol":"XBTUSDTM","orderType":"market","side":"buy","orderId":"492813530635034624",
          "clientOid":"OID","type":"match","status":"match","matchSize":"SIZE","matchPrice":"84117",
          "liquidity":"taker","tradeId":"TRADE","ts":1729177117878000000}}
        """
        .Replace("OID", clientOrderId.Value, StringComparison.Ordinal)
        .Replace("SIZE", size, StringComparison.Ordinal)
        .Replace("TRADE", tradeId, StringComparison.Ordinal);

    // ----- which market it trades -----

    [Fact]
    public void A_spot_configuration_is_refused_rather_than_traded_against_the_wrong_host()
    {
        using TestKernel kernel = new();

        ArgumentException error = Assert.Throws<ArgumentException>(() => new KucoinFuturesExecutionClient(
            new ClientId("KUCOIN"),
            new KucoinExecutionClientConfig
            {
                ProductType = KucoinProductType.Spot,
                ApiKey = ApiKey,
                ApiSecret = ApiSecret,
                ApiPassphrase = Passphrase,
            },
            kernel.Services));

        Assert.Contains("KucoinExecutionClient", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_factory_picks_the_client_the_configuration_asks_for()
    {
        using TestKernel kernel = new();
        KucoinExecutionClientFactory factory = new();
        Assert.Equal("KUCOIN", factory.Name);

        KucoinExecutionClientConfig Config(KucoinProductType product) => new()
        {
            ProductType = product,
            ApiKey = ApiKey,
            ApiSecret = ApiSecret,
            ApiPassphrase = Passphrase,
        };

        KucoinFuturesExecutionClient futures = Assert.IsType<KucoinFuturesExecutionClient>(
            factory.Create(new ClientId("KUCOIN"), Config(KucoinProductType.Futures), kernel.Services));
        KucoinExecutionClient spot = Assert.IsType<KucoinExecutionClient>(
            factory.Create(new ClientId("KUCOIN"), Config(KucoinProductType.Spot), kernel.Services));

        futures.Dispose();
        spot.Dispose();
    }

    [Fact]
    public async Task The_account_is_a_margin_account_and_its_balance_comes_from_the_overview()
    {
        await using Rig rig = await new Rig().ConnectAsync();

        // The venue keeps a balance per settlement currency and answers for one at a time, so both are asked for.
        Assert.Equal(
            ["USDC", "USDT"],
            rig.Server.RequestsTo("/api/v1/account-overview").Select(r => r.Query("currency")!).Order().ToArray());

        AccountState state = await rig.Sink.NextAccountStateAsync();
        AccountBalance usdt = Assert.Single(state.Balances, b => b.Total.Currency == Currencies.USDT);
        Assert.Equal(100_000m, usdt.Total.Amount);

        // Equity 100,000 with 99,000 available means 1,000 is held against margin.
        Assert.Equal(1_000m, usdt.Locked.Amount);
    }

    // ----- an order, in contracts -----

    [Fact]
    public async Task A_market_order_is_sent_as_a_whole_number_of_contracts_with_a_leverage()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        MarketOrder order = rig.Track(rig.Orders.Market(rig.Contract.Id, OrderSide.Buy, rig.Qty(0.084m)));

        await rig.SubmitAsync(order);

        JsonElement body = rig.Body("/api/v1/orders");
        Assert.Equal("XBTUSDTM", body.GetProperty("symbol").GetString());
        Assert.Equal("buy", body.GetProperty("side").GetString());
        Assert.Equal("market", body.GetProperty("type").GetString());

        // 0.084 XBT divided by the 0.001 XBT contract: 84 contracts. Sent as 0.084 the venue would read it as a
        // fraction of a contract and refuse it; sent as 84 XBT it would be a thousand times the intended size.
        Assert.Equal(84, body.GetProperty("size").GetInt64());

        // The venue freezes margin against this and has no default of its own.
        Assert.Equal(1, body.GetProperty("leverage").GetInt32());

        Assert.IsType<OrderSubmitted>(await rig.Sink.NextOrderEventAsync());
    }

    [Fact]
    public async Task A_configured_leverage_is_what_goes_on_the_order()
    {
        await using Rig rig = await new Rig(leverage: 5).ConnectAsync();
        await rig.SubmitAsync(rig.Track(rig.Orders.Market(rig.Contract.Id, OrderSide.Sell, rig.Qty(0.01m))));

        Assert.Equal(5, rig.Body("/api/v1/orders").GetProperty("leverage").GetInt32());
    }

    [Fact]
    public async Task A_contract_worth_more_than_one_unit_converts_the_other_way()
    {
        // DOGEUSDTM's contract is 10 DOGE, so 500 DOGE is 50 contracts, not 5,000.
        await using Rig rig = await new Rig().ConnectAsync();
        Instrument doge = rig.Client.Instruments.Find(InstrumentId.Parse("DOGEUSDT-PERP.KUCOIN"))!;

        await rig.SubmitAsync(rig.Track(rig.Orders.Market(doge.Id, OrderSide.Buy, doge.MakeQuantity(500m))));

        Assert.Equal(50, rig.Body("/api/v1/orders").GetProperty("size").GetInt64());
    }

    [Fact]
    public async Task A_limit_order_carries_its_price_and_its_time_in_force()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        LimitOrder order = rig.Track(rig.Orders.Limit(
            rig.Contract.Id, OrderSide.Buy, rig.Qty(0.05m), rig.Px(84_000m), TimeInForce.Gtc, postOnly: true));

        await rig.SubmitAsync(order);

        JsonElement body = rig.Body("/api/v1/orders");
        Assert.Equal("limit", body.GetProperty("type").GetString());
        Assert.Equal("84000.0", body.GetProperty("price").GetString());
        Assert.Equal("GTC", body.GetProperty("timeInForce").GetString());
        Assert.True(body.GetProperty("postOnly").GetBoolean());
        Assert.Equal(50, body.GetProperty("size").GetInt64());
    }

    [Theory]
    [InlineData(OrderSide.Buy, "up")]
    [InlineData(OrderSide.Sell, "down")]
    public async Task A_stop_says_which_way_the_price_has_to_move_to_trigger_it(OrderSide side, string direction)
    {
        // A buy stop waits for a rise and a sell stop for a fall. Spot calls the same two directions "loss" and
        // "entry", which is why neither name is shared between the two clients.
        await using Rig rig = await new Rig().ConnectAsync();
        StopMarketOrder stop = rig.Track(rig.Orders.StopMarket(rig.Contract.Id, side, rig.Qty(0.02m), rig.Px(83_000m)));

        await rig.SubmitAsync(stop);

        JsonElement body = rig.Body("/api/v1/orders");
        Assert.Equal(direction, body.GetProperty("stop").GetString());
        Assert.Equal("83000.0", body.GetProperty("stopPrice").GetString());

        // Against the mark price, because that is what the venue liquidates against: a stop guarding a position has
        // to watch the same price, or the position can be liquidated without the stop ever triggering.
        Assert.Equal("MP", body.GetProperty("stopPriceType").GetString());
    }

    [Fact]
    public async Task A_quantity_that_is_not_a_whole_number_of_contracts_is_rejected_rather_than_sent()
    {
        await using Rig rig = await new Rig().ConnectAsync();

        // 0.0015 XBT is a contract and a half. The engine rounds to the instrument's increment, which is one
        // contract, so a quantity like this was built by hand - and rounding it here would fill a size nobody asked
        // for while reporting the one they did.
        MarketOrder order = rig.Track(rig.Orders.Market(rig.Contract.Id, OrderSide.Buy, new Quantity(0.0015m, 4)));

        await rig.SubmitAsync(order);

        OrderRejected rejected = Assert.IsType<OrderRejected>(await rig.Sink.NextOrderEventAsync());
        Assert.Contains("whole ones", rejected.Reason, StringComparison.Ordinal);
        Assert.Empty(rig.Server.RequestsTo("/api/v1/orders"));
    }

    [Fact]
    public async Task An_order_sized_in_the_quote_currency_is_refused_because_this_market_counts_contracts()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        MarketOrder order = rig.Track(rig.Orders.Market(
            rig.Contract.Id, OrderSide.Buy, rig.Qty(1m), quoteQuantity: true));

        await rig.SubmitAsync(order);

        OrderRejected rejected = Assert.IsType<OrderRejected>(await rig.Sink.NextOrderEventAsync());
        Assert.Contains("contracts", rejected.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Modifying_an_order_is_refused_with_the_reason_rather_than_attempted()
    {
        // The venue has no amend on this market - not for plain orders, which spot can amend, and not for stops. A
        // cancel-and-replace here would leave a perpetual position unguarded for the length of two requests without
        // the caller having asked for that, so the answer is the refusal and the reason.
        await using Rig rig = await new Rig().ConnectAsync();
        LimitOrder order = rig.Track(rig.Orders.Limit(rig.Contract.Id, OrderSide.Buy, rig.Qty(0.05m), rig.Px(80_000m)));
        await rig.SubmitAsync(order);
        await rig.Sink.NextOrderEventAsync();

        await rig.Client.ModifyOrderAsync(
            new ModifyOrder(rig.Kernel.Services.TraderId, Rig.Strategy, order.InstrumentId, order.ClientOrderId, null, rig.Qty(0.06m), rig.Px(81_000m), null, null, Guid.NewGuid(), TestKernel.Now),
            CancellationToken.None);

        OrderModifyRejected rejected = Assert.IsType<OrderModifyRejected>(await rig.Sink.NextOrderEventAsync());
        Assert.Contains("cancel it and submit a new one", rejected.Reason, StringComparison.Ordinal);
    }

    // ----- what comes back -----

    [Fact]
    public async Task A_fill_on_the_stream_is_reported_in_base_currency()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        MarketOrder order = rig.Track(rig.Orders.Market(rig.Contract.Id, OrderSide.Buy, rig.Qty(0.084m)));
        await rig.SubmitAsync(order);
        await rig.Sink.NextOrderEventAsync();

        await rig.Session.SendTextAsync(Match(order.ClientOrderId, size: "84", tradeId: "1944971621109"));

        OrderFilled fill = Assert.IsType<OrderFilled>(await rig.Sink.NextOrderEventAsync());

        // 84 contracts on the wire, 0.084 XBT to everything above the adapter.
        Assert.Equal(new Quantity(0.084m, 3), fill.LastQty);
        Assert.Equal(rig.Px(84_117m), fill.LastPx);
        Assert.Equal(LiquiditySide.Taker, fill.LiquiditySide);
        Assert.Equal("1944971621109", fill.TradeId.Value);

        // The order stream carries no fee on this market, so the commission comes from the instrument's taker rate:
        // 0.084 XBT at 84,117 is 7,065.828 USDT, times 0.0006.
        Assert.Equal(Currencies.USDT, fill.Commission.Currency);
        Assert.Equal(4.2394968m, fill.Commission.Amount);
    }

    [Fact]
    public async Task The_same_trade_arriving_twice_is_reported_once()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        MarketOrder order = rig.Track(rig.Orders.Market(rig.Contract.Id, OrderSide.Buy, rig.Qty(0.01m)));
        await rig.SubmitAsync(order);
        await rig.Sink.NextOrderEventAsync();

        string match = Match(order.ClientOrderId, size: "10", tradeId: "same-trade");

        await rig.Session.SendTextAsync(match);
        await rig.Session.SendTextAsync(match);

        Assert.IsType<OrderFilled>(await rig.Sink.NextOrderEventAsync());

        // The second copy produces nothing at all: a fill reported twice would double a position.
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using CancellationTokenSource quiet = new(TimeSpan.FromMilliseconds(400));
            await rig.Sink.NextOrderEventAsync().WaitAsync(quiet.Token);
        });
    }

    [Fact]
    public async Task A_position_is_reported_in_base_currency_with_its_side_from_the_sign()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        rig.Routes.On("GET", "/api/v1/positions", KucoinPayloads.Envelope("""
            [{"symbol":"XBTUSDTM","currentQty":-84,"avgEntryPrice":84117.0,"isOpen":true},
             {"symbol":"DOGEUSDTM","currentQty":50,"avgEntryPrice":0.42,"isOpen":true}]
            """));

        IReadOnlyList<PositionStatusReport> positions = await rig.Client
            .GeneratePositionStatusReportsAsync(null, null, null, CancellationToken.None);

        PositionStatusReport shortXbt = Assert.Single(positions, p => p.InstrumentId.ToString() == "XBTUSDT-PERP.KUCOIN");
        Assert.Equal(PositionSide.Short, shortXbt.PositionSide);

        // 84 contracts short is 0.084 XBT short, and the sign is the side rather than part of the size.
        Assert.Equal(new Quantity(0.084m, 3), shortXbt.Quantity);
        Assert.Equal(-0.084m, shortXbt.SignedQuantity);
        Assert.Equal(84_117.0m, shortXbt.AvgPxOpen);

        // And the 10-DOGE contract converts by its own multiplier, not bitcoin's.
        PositionStatusReport longDoge = Assert.Single(positions, p => p.InstrumentId.ToString() == "DOGEUSDT-PERP.KUCOIN");
        Assert.Equal(PositionSide.Long, longDoge.PositionSide);
        Assert.Equal(500m, longDoge.Quantity.Value);
    }

    [Fact]
    public async Task A_flat_position_is_still_reported_so_reconciliation_can_close_one_this_node_thinks_is_open()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        rig.Routes.On("GET", "/api/v1/positions", KucoinPayloads.Envelope("""
            [{"symbol":"XBTUSDTM","currentQty":0,"isOpen":false}]
            """));

        PositionStatusReport flat = Assert.Single(
            await rig.Client.GeneratePositionStatusReportsAsync(null, null, null, CancellationToken.None));

        Assert.Equal(PositionSide.Flat, flat.PositionSide);
        Assert.Equal(0m, flat.Quantity.Value);
    }

    [Fact]
    public async Task An_order_read_back_over_rest_reports_its_sizes_in_base_currency()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        rig.Routes.On("GET", "/api/v1/orders", KucoinPayloads.Envelope("""
            {"items":[{"id":"492813530635034624","symbol":"XBTUSDTM","type":"limit","side":"buy","price":"84000.0",
              "size":84,"dealSize":21,"dealValue":1764000.0,"isActive":true,"postOnly":true,"reduceOnly":false,
              "timeInForce":"GTC","clientOid":"probe-1","createdAt":1729177117878,"updatedAt":1729177117900}]}
            """));

        OrderStatusReport report = Assert.Single(
            await rig.Client.GenerateOrderStatusReportsAsync(null, null, null, openOnly: true, CancellationToken.None));

        Assert.Equal(new Quantity(0.084m, 3), report.Quantity);
        Assert.Equal(new Quantity(0.021m, 3), report.FilledQuantity);
        Assert.Equal(OrderStatus.PartiallyFilled, report.OrderStatus);
        Assert.Equal(OrderType.Limit, report.OrderType);
        Assert.Equal(rig.Px(84_000m), report.Price);
        Assert.True(report.PostOnly);
    }

    [Fact]
    public async Task A_fill_read_back_over_rest_carries_the_venues_own_fee()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        rig.Routes.On("GET", "/api/v1/fills", KucoinPayloads.Envelope("""
            {"items":[{"symbol":"XBTUSDTM","tradeId":"1944971621109","orderId":"492813530635034624","side":"buy",
              "size":84,"price":"84117","fee":"4.24","feeCurrency":"USDT","liquidity":"taker",
              "tradeTime":1729177117878000000}]}
            """));

        FillReport fill = Assert.Single(
            await rig.Client.GenerateFillReportsAsync(null, null, null, null, CancellationToken.None));

        Assert.Equal(new Quantity(0.084m, 3), fill.LastQty);
        Assert.Equal(rig.Px(84_117m), fill.LastPx);

        // The venue's figure, not the one worked out from the instrument's rate.
        Assert.Equal(4.24m, fill.Commission.Amount);
        Assert.Equal(Currencies.USDT, fill.Commission.Currency);
    }

    [Fact]
    public async Task The_private_stream_asks_for_orders_balances_positions_and_stops()
    {
        await using Rig rig = await new Rig().ConnectAsync();

        List<string> topics = new();
        for (int i = 0; i < KucoinFuturesVenue.PrivateTopics.Count; i++)
        {
            using JsonDocument doc = JsonDocument.Parse(await rig.Session.ReceiveTextAsync(Wait.Timeout));
            if (doc.RootElement.GetProperty("type").GetString() == "subscribe")
            {
                topics.Add(doc.RootElement.GetProperty("topic").GetString()!);
                Assert.True(doc.RootElement.GetProperty("privateChannel").GetBoolean());
            }
        }

        Assert.Equal(KucoinFuturesVenue.PrivateTopics.Order(StringComparer.Ordinal), topics.Order(StringComparer.Ordinal));
    }
}
