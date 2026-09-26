using System.Text.Json;
using Bytex.Adapters.Gate;
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

namespace Bytex.Adapters.Tests.Gate;

// Why: four things about an order on this venue are the venue's own, and three of them would be ACCEPTED if got
// wrong rather than refused - which is the worst kind.
//
//   - There is no client-order-id field. The id travels in the order's `text`, prefixed t- and at most 28 characters
//     after the prefix, and the venue writes its own values there on orders it raised itself.
//   - A derivative order's size is SIGNED and there is no side field at all, so an unsigned size buys where a
//     strategy meant to sell. The venue would fill it.
//   - A derivative market order is a limit order at a price of ZERO with immediate-or-cancel. The venue has no
//     market type.
//   - Leverage is account state, per CONTRACT, and an order carrying one is ignored - so a strategy written for 3x
//     trades at whatever the account was left on unless something sets it.
//
// And one differs between the venue's own markets: only the dated contracts cannot amend an order.
public sealed class GateExecutionClientTests
{
    private const string ApiKey = "cafebabe0123456789abcdef";
    private const string ApiSecret = "0123456789abcdef0123456789abcdef";

    private sealed class Rig : IAsyncDisposable
    {
        public Rig(GateProductType product, decimal? leverage = null)
        {
            Product = product;
            Routes = product switch
            {
                GateProductType.Futures => new Routes()
                    .On("GET", "/api/v4/futures/usdt/contracts", GatePayloads.FuturesContracts)
                    .On("GET", "/api/v4/futures/usdt/accounts", GatePayloads.FuturesAccount)
                    .On("POST", "/api/v4/futures/usdt/orders", _ => StubResponse.Json(GatePayloads.FuturesOrder("t-O-1", 500)))
                    .On("PUT", "/api/v4/futures/usdt/orders/900000001", _ => StubResponse.Json(GatePayloads.FuturesOrder("t-O-1", 300)))
                    .On("DELETE", "/api/v4/futures/usdt/orders/900000001", _ => StubResponse.Json(GatePayloads.FuturesOrder("t-O-1", 500, status: "finished", finishAs: "cancelled")))
                    .On("DELETE", "/api/v4/futures/usdt/orders", "[]")
                    .On("GET", "/api/v4/futures/usdt/orders", "[]")
                    .On("GET", "/api/v4/futures/usdt/my_trades", "[]")
                    .On("GET", "/api/v4/futures/usdt/positions", GatePayloads.FuturesPositions)
                    .On("POST", "/api/v4/futures/usdt/positions/BTC_USDT/leverage", "{}")
                    .On("POST", "/api/v4/futures/usdt/positions/ETH_USDT/leverage", "{}")
                    .On("POST", "/api/v4/futures/usdt/positions/ARIA_USDT/leverage", "{}")
                    .On("POST", "/api/v4/futures/usdt/positions/AAPL_USDT/leverage", "{}"),

                GateProductType.Delivery => new Routes()
                    .On("GET", "/api/v4/delivery/usdt/contracts", GatePayloads.DeliveryContracts)
                    .On("GET", "/api/v4/delivery/usdt/accounts", GatePayloads.FuturesAccount)
                    .On("POST", "/api/v4/delivery/usdt/orders", _ => StubResponse.Json(GatePayloads.FuturesOrder("t-O-1", 500)))
                    .On("DELETE", "/api/v4/delivery/usdt/orders", "[]")
                    .On("GET", "/api/v4/delivery/usdt/orders", "[]")
                    .On("GET", "/api/v4/delivery/usdt/my_trades", "[]")
                    .On("GET", "/api/v4/delivery/usdt/positions", "[]"),

                _ => new Routes()
                    .On("GET", "/api/v4/spot/currency_pairs", GatePayloads.CurrencyPairs)
                    .On("GET", "/api/v4/spot/accounts", GatePayloads.SpotAccounts)
                    .On("POST", "/api/v4/spot/orders", _ => StubResponse.Json(GatePayloads.SpotOrder("t-O-1")))
                    .On("PATCH", "/api/v4/spot/orders/170000001", _ => StubResponse.Json(GatePayloads.SpotOrder("t-O-1")))
                    .On("DELETE", "/api/v4/spot/orders/170000001", _ => StubResponse.Json(GatePayloads.SpotOrder("t-O-1", status: "cancelled", finishAs: "cancelled")))
                    .On("DELETE", "/api/v4/spot/orders", "[]")
                    .On("GET", "/api/v4/spot/orders", _ => StubResponse.Json("[" + GatePayloads.SpotOrder("t-O-1") + "]"))
                    .On("GET", "/api/v4/spot/orders/170000001", _ => StubResponse.Json(GatePayloads.SpotOrder("t-O-1")))
                    .On("GET", "/api/v4/spot/my_trades", "[]"),
            };

            Server = new LoopbackServer(Routes.Handle);
            Kernel = new TestKernel();
            GateExecutionClientConfig config = new()
            {
                ProductType = product,
                Leverage = leverage,
                ApiKey = ApiKey,
                ApiSecret = ApiSecret,
                BaseUrlHttp = Server.HttpBase,
                BaseUrlWs = Server.WsBase,
                InstrumentProvider = new Core.Adapters.InstrumentProviderConfig { LoadAll = true },
            };

            Client = new GateExecutionClientFactory().Create(new ClientId("GATE"), config, Kernel.Services);
            Client.AttachSink(Sink);
            Orders = new OrderFactory(Kernel.Services.TraderId, Strategy, Kernel.Clock);
        }

        public static StrategyId Strategy { get; } = new("Probe-001");

        public GateProductType Product { get; }

        public Routes Routes { get; }

        public LoopbackServer Server { get; }

        public TestKernel Kernel { get; }

        public Core.Adapters.IExecutionClient Client { get; }

        public RecordingExecutionSink Sink { get; } = new();

        public OrderFactory Orders { get; }

        public string Prefix => Product switch
        {
            GateProductType.Futures => "/api/v4/futures/usdt",
            GateProductType.Delivery => "/api/v4/delivery/usdt",
            _ => "/api/v4/spot",
        };

        public Instrument Traded => Product switch
        {
            GateProductType.Delivery => Find("BTC_USDT_20261009"),
            _ => Find("BTC_USDT"),
        };

        private Instrument Find(string raw) =>
            (Client switch
            {
                GateExecutionClient spot => spot.Instruments.Find(InstrumentId.Parse(raw + ".GATE")),
                GateFuturesExecutionClient futures => futures.Instruments.Find(InstrumentId.Parse(raw + ".GATE")),
                _ => null,
            })!;

        public Quantity Qty(decimal value) => Traded.MakeQuantity(value);

        public Price Px(decimal value) => Traded.MakePrice(value);

        public async Task<Rig> ConnectAsync()
        {
            await Client.ConnectAsync(CancellationToken.None).WaitAsync(Wait.Timeout);
            return this;
        }

        public T Track<T>(T order)
            where T : Order
        {
            Kernel.Kernel.Cache.AddOrder(order);
            return order;
        }

        public Task SubmitAsync(Order order) =>
            Client.SubmitOrderAsync(
                new SubmitOrder(Kernel.Services.TraderId, Strategy, order, null, null, null, Guid.NewGuid(), TestKernel.Now),
                CancellationToken.None).WaitAsync(Wait.Timeout);

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

    // ----- the client order id in the text field -----

    [Fact]
    public async Task An_order_carries_its_client_id_in_the_text_field_prefixed()
    {
        await using Rig rig = await new Rig(GateProductType.Futures).ConnectAsync();
        MarketOrder order = rig.Track(rig.Orders.Market(rig.Traded.Id, OrderSide.Buy, rig.Qty(0.05m), clientOrderId: new ClientOrderId("O-1")));

        await rig.SubmitAsync(order);

        Assert.Equal("t-O-1", rig.Body(rig.Prefix + "/orders").GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_spot_order_carries_its_client_id_the_same_way()
    {
        await using Rig rig = await new Rig(GateProductType.Spot).ConnectAsync();
        LimitOrder order = rig.Track(rig.Orders.Limit(
            rig.Traded.Id, OrderSide.Buy, rig.Qty(0.01m), rig.Px(83_000m), clientOrderId: new ClientOrderId("O-1")));

        await rig.SubmitAsync(order);

        Assert.Equal("t-O-1", rig.Body("/api/v4/spot/orders").GetProperty("text").GetString());
    }

    [Fact]
    public async Task An_id_the_venue_would_refuse_is_refused_before_the_order_leaves()
    {
        // The venue takes at most 28 characters after the prefix and only letters, digits, underscore, hyphen and
        // dot. Refusing it here names the rule; letting the venue refuse it names nothing a strategy can act on.
        await using Rig rig = await new Rig(GateProductType.Futures).ConnectAsync();
        MarketOrder order = rig.Track(rig.Orders.Market(
            rig.Traded.Id,
            OrderSide.Buy,
            rig.Qty(0.05m),
            clientOrderId: new ClientOrderId("O-1:with:colons-and-far-too-many-characters")));

        await rig.SubmitAsync(order);

        OrderRejected rejected = await rig.Sink.NextOrderEventAsync<OrderRejected>();
        Assert.Contains("text field", rejected.Reason, StringComparison.Ordinal);
        Assert.Empty(rig.Server.RequestsTo(rig.Prefix + "/orders"));
    }

    // ----- the signed size, which is the side -----

    [Theory]
    [InlineData(true, 500)]
    [InlineData(false, -500)]
    public async Task A_derivative_orders_size_is_signed_by_its_side(bool buy, long expected)
    {
        // 0.05 BTC is 500 contracts of 0.0001. A buy sends +500 and a sell -500, and there is no side field on the
        // order at all - so an unsigned size would buy where the strategy meant to sell and the venue would fill it.
        await using Rig rig = await new Rig(GateProductType.Futures).ConnectAsync();
        await rig.SubmitAsync(rig.Track(rig.Orders.Market(rig.Traded.Id, buy ? OrderSide.Buy : OrderSide.Sell, rig.Qty(0.05m))));

        JsonElement body = rig.Body(rig.Prefix + "/orders");
        Assert.Equal(expected, body.GetProperty("size").GetInt64());
        Assert.False(body.TryGetProperty("side", out _));
    }

    [Fact]
    public async Task A_spot_orders_side_is_a_field_and_its_amount_is_base_currency()
    {
        // Spot is the other way round on both counts: a side field, and an amount in base currency rather than a
        // count of contracts. One venue, two conventions.
        await using Rig rig = await new Rig(GateProductType.Spot).ConnectAsync();
        await rig.SubmitAsync(rig.Track(rig.Orders.Limit(rig.Traded.Id, OrderSide.Sell, rig.Qty(0.01m), rig.Px(83_000m))));

        JsonElement body = rig.Body("/api/v4/spot/orders");
        Assert.Equal("sell", body.GetProperty("side").GetString());
        // The amount carries the pair's own size precision - six places on BTC_USDT - because that is what the
        // venue accepts: a quantity with more places than the pair allows is refused.
        Assert.Equal("0.010000", body.GetProperty("amount").GetString());
    }

    [Fact]
    public async Task A_derivative_orders_size_converts_by_the_contracts_own_multiplier()
    {
        // ARIA_USDT's contract is 100 units, so 500 of them is 50,000. The conversion is per instrument, not per
        // venue: this family's multipliers span eleven orders of magnitude.
        await using Rig rig = await new Rig(GateProductType.Futures).ConnectAsync();
        GateFuturesExecutionClient client = (GateFuturesExecutionClient)rig.Client;
        Instrument aria = client.Instruments.Find(InstrumentId.Parse("ARIA_USDT.GATE"))!;

        await rig.SubmitAsync(rig.Track(rig.Orders.Market(aria.Id, OrderSide.Buy, aria.MakeQuantity(50_000m))));

        Assert.Equal(500, rig.Body(rig.Prefix + "/orders").GetProperty("size").GetInt64());
    }

    [Fact]
    public async Task A_quantity_that_is_not_a_whole_number_of_contracts_is_refused_with_the_reason()
    {
        await using Rig rig = await new Rig(GateProductType.Futures).ConnectAsync();
        MarketOrder order = rig.Track(rig.Orders.Market(rig.Traded.Id, OrderSide.Buy, new Quantity(0.00015m, 8)));

        await rig.SubmitAsync(order);

        OrderRejected rejected = await rig.Sink.NextOrderEventAsync<OrderRejected>();
        Assert.Contains("whole ones", rejected.Reason, StringComparison.Ordinal);
    }

    // ----- the market order that is a limit order -----

    [Fact]
    public async Task A_derivative_market_order_is_a_zero_priced_immediate_or_cancel()
    {
        // The venue has no market order type. A price of zero with immediate-or-cancel crosses what it can and
        // leaves nothing resting, which is what a market order means; sending no price at all is refused.
        await using Rig rig = await new Rig(GateProductType.Futures).ConnectAsync();
        await rig.SubmitAsync(rig.Track(rig.Orders.Market(rig.Traded.Id, OrderSide.Buy, rig.Qty(0.05m))));

        JsonElement body = rig.Body(rig.Prefix + "/orders");
        Assert.Equal("0", body.GetProperty("price").GetString());
        Assert.Equal("ioc", body.GetProperty("tif").GetString());
    }

    [Fact]
    public async Task A_derivative_limit_order_carries_its_price_and_its_time_in_force()
    {
        await using Rig rig = await new Rig(GateProductType.Futures).ConnectAsync();
        await rig.SubmitAsync(rig.Track(rig.Orders.Limit(
            rig.Traded.Id, OrderSide.Buy, rig.Qty(0.05m), rig.Px(83_000m), postOnly: true)));

        JsonElement body = rig.Body(rig.Prefix + "/orders");
        Assert.Equal("83000.0", body.GetProperty("price").GetString());

        // "poc" is pending-or-cancelled, which is post-only by the venue's name for it.
        Assert.Equal("poc", body.GetProperty("tif").GetString());
    }

    [Fact]
    public async Task A_reduce_only_order_says_so()
    {
        await using Rig rig = await new Rig(GateProductType.Futures).ConnectAsync();
        await rig.SubmitAsync(rig.Track(rig.Orders.Limit(
            rig.Traded.Id, OrderSide.Sell, rig.Qty(0.05m), rig.Px(85_000m), reduceOnly: true)));

        Assert.True(rig.Body(rig.Prefix + "/orders").GetProperty("reduce_only").GetBoolean());
    }

    [Fact]
    public async Task An_order_type_this_venue_does_not_take_is_refused_rather_than_approximated()
    {
        // A stop order on Gate lives on a separate price-trigger endpoint with its own shape, which this adapter
        // does not place. Approximating it with a plain order would put an unprotected order at the venue.
        await using Rig rig = await new Rig(GateProductType.Futures).ConnectAsync();
        await rig.SubmitAsync(rig.Track(rig.Orders.StopMarket(rig.Traded.Id, OrderSide.Sell, rig.Qty(0.05m), rig.Px(80_000m))));

        OrderRejected rejected = await rig.Sink.NextOrderEventAsync<OrderRejected>();
        Assert.Contains("not supported", rejected.Reason, StringComparison.Ordinal);
        Assert.Empty(rig.Server.RequestsTo(rig.Prefix + "/orders"));
    }

    [Fact]
    public async Task A_quote_quantity_is_refused_because_this_venue_sizes_in_contracts()
    {
        await using Rig rig = await new Rig(GateProductType.Futures).ConnectAsync();
        await rig.SubmitAsync(rig.Track(rig.Orders.Market(rig.Traded.Id, OrderSide.Buy, rig.Qty(1m), quoteQuantity: true)));

        OrderRejected rejected = await rig.Sink.NextOrderEventAsync<OrderRejected>();
        Assert.Contains("quote quantity", rejected.Reason, StringComparison.Ordinal);
    }

    // ----- leverage -----

    [Fact]
    public async Task The_configured_leverage_is_set_at_the_venue_before_anything_trades()
    {
        // Leverage is account state here and an order carrying one is ignored, so a strategy written for 3x would
        // otherwise trade at whatever the account was last left on - and would backtest and paper at 3x while going
        // live at something else, with nothing saying so. It is a query parameter, not a body field.
        await using Rig rig = await new Rig(GateProductType.Futures, leverage: 3m).ConnectAsync();

        RecordedRequest set = Assert.Single(rig.Server.RequestsTo("/api/v4/futures/usdt/positions/BTC_USDT/leverage"));
        Assert.Equal("3", set.Query(GateFuturesVenue.LeverageParameter));

        // One call per contract covers BOTH legs of a dual position: on this venue the leverage belongs to the
        // contract rather than to a side, so there is nothing per-side to set.
        Assert.Null(set.Query(GateFuturesVenue.CrossLeverageLimitParameter));
        Assert.Equal(string.Empty, set.Body);
    }

    [Fact]
    public async Task No_configured_leverage_touches_nothing()
    {
        // Null means "do not touch it", which is what every configuration written before the field existed means.
        await using Rig rig = await new Rig(GateProductType.Futures).ConnectAsync();

        Assert.Empty(rig.Server.RequestsTo("/api/v4/futures/usdt/positions/BTC_USDT/leverage"));
    }

    [Fact]
    public async Task A_fractional_leverage_is_sent_as_written()
    {
        // The venue types the parameter as a string and documents no integer constraint, so it is passed through
        // and the venue speaks for itself rather than this adapter rounding in a direction nobody chose.
        await using Rig rig = await new Rig(GateProductType.Futures, leverage: 2.5m).ConnectAsync();

        RecordedRequest set = Assert.Single(rig.Server.RequestsTo("/api/v4/futures/usdt/positions/BTC_USDT/leverage"));
        Assert.Equal("2.5", set.Query(GateFuturesVenue.LeverageParameter));
    }

    [Fact]
    public void A_leverage_of_zero_is_refused_because_it_means_cross_margin_here()
    {
        // The unusual shape. Zero is not "no leverage" on this venue - it is the value that switches the contract to
        // CROSS margin, whose ceiling travels in a separate cross_leverage_limit that one decimal cannot carry.
        // Accepting it would put a strategy on cross margin at whatever ceiling the account was last left on.
        using TestKernel kernel = new();

        ArgumentOutOfRangeException refused = Assert.Throws<ArgumentOutOfRangeException>(() => new GateFuturesExecutionClient(
            new ClientId("GATE"),
            new GateExecutionClientConfig
            {
                ProductType = GateProductType.Futures,
                ApiKey = ApiKey,
                ApiSecret = ApiSecret,
                Leverage = 0m,
            },
            kernel.Services));

        Assert.Contains("cross margin", refused.Message, StringComparison.Ordinal);
        Assert.Contains(GateFuturesVenue.CrossLeverageLimitParameter, refused.Message, StringComparison.Ordinal);
        Assert.Equal("0", GateFuturesVenue.CrossMarginLeverage);
    }

    [Fact]
    public async Task A_venue_that_refuses_a_leverage_does_not_stop_the_node()
    {
        // A refusal is usually the venue saying the account is not entitled to the figure asked for, which a node
        // cannot fix and a person needs to read. It is logged and the node starts.
        RecordingLogs logs = new();
        LoopbackServer server = new();
        server.Handler = new Routes()
            .On("GET", "/api/v4/futures/usdt/contracts", GatePayloads.FuturesContracts)
            .On("GET", "/api/v4/futures/usdt/accounts", GatePayloads.FuturesAccount)
            .On("POST", "/api/v4/futures/usdt/positions/BTC_USDT/leverage", _ => new StubResponse(400, GatePayloads.Error("LEVERAGE_TOO_HIGH", "not entitled")))
            .Handle;

        using TestKernel kernel = new(logs);
        GateFuturesExecutionClient client = new(
            new ClientId("GATE"),
            new GateExecutionClientConfig
            {
                ProductType = GateProductType.Futures,
                ApiKey = ApiKey,
                ApiSecret = ApiSecret,
                Leverage = 200m,
                BaseUrlHttp = server.HttpBase,
                BaseUrlWs = server.WsBase,
                InstrumentProvider = new Core.Adapters.InstrumentProviderConfig
                {
                    LoadIds = [InstrumentId.Parse("BTC_USDT.GATE")],
                },
            },
            kernel.Services);

        client.AttachSink(new RecordingExecutionSink());
        server.Handler = new Routes()
            .On("GET", "/api/v4/futures/usdt/contracts/BTC_USDT", GatePayloads.FuturesContract)
            .On("GET", "/api/v4/futures/usdt/accounts", GatePayloads.FuturesAccount)
            .On("POST", "/api/v4/futures/usdt/positions/BTC_USDT/leverage", _ => new StubResponse(400, GatePayloads.Error("LEVERAGE_TOO_HIGH", "not entitled")))
            .Handle;

        await client.ConnectAsync(CancellationToken.None).WaitAsync(Wait.Timeout);

        Assert.True(client.IsConnected);
        Assert.Contains(logs.Errors, e => e.Contains("refused", StringComparison.OrdinalIgnoreCase));

        await client.DisconnectAsync(CancellationToken.None);
        client.Dispose();
        await server.DisposeAsync();
    }

    // ----- amendment, which differs per market -----

    [Fact]
    public async Task A_perpetual_order_is_amended_in_place_with_a_signed_size()
    {
        // The venue takes a new size, a new price, or both, and refuses a change of side - so the amended size keeps
        // the order's own side, read off the order this node holds rather than guessed from the amendment.
        await using Rig rig = await new Rig(GateProductType.Futures).ConnectAsync();
        LimitOrder order = rig.Track(rig.Orders.Limit(
            rig.Traded.Id, OrderSide.Sell, rig.Qty(0.05m), rig.Px(85_000m), clientOrderId: new ClientOrderId("O-1")));

        await rig.Client.ModifyOrderAsync(
            new ModifyOrder(
                rig.Kernel.Services.TraderId,
                Rig.Strategy,
                rig.Traded.Id,
                order.ClientOrderId,
                new VenueOrderId("900000001"),
                rig.Qty(0.03m),
                rig.Px(84_000m),
                null,
                null,
                Guid.NewGuid(),
                TestKernel.Now),
            CancellationToken.None);

        JsonElement body = rig.Body("/api/v4/futures/usdt/orders/900000001");
        Assert.Equal(-300, body.GetProperty("size").GetInt64());
        Assert.Equal("84000.0", body.GetProperty("price").GetString());
        Assert.Equal("PUT", rig.Server.RequestsTo("/api/v4/futures/usdt/orders/900000001").Last().Method);
    }

    [Fact]
    public async Task A_spot_order_is_amended_with_a_patch_and_an_amount_in_base_currency()
    {
        await using Rig rig = await new Rig(GateProductType.Spot).ConnectAsync();
        LimitOrder order = rig.Track(rig.Orders.Limit(
            rig.Traded.Id, OrderSide.Buy, rig.Qty(0.02m), rig.Px(83_000m), clientOrderId: new ClientOrderId("O-1")));

        await rig.Client.ModifyOrderAsync(
            new ModifyOrder(
                rig.Kernel.Services.TraderId,
                Rig.Strategy,
                rig.Traded.Id,
                order.ClientOrderId,
                new VenueOrderId("170000001"),
                rig.Qty(0.01m),
                null,
                null,
                null,
                Guid.NewGuid(),
                TestKernel.Now),
            CancellationToken.None);

        RecordedRequest sent = rig.Server.RequestsTo("/api/v4/spot/orders/170000001").Last();
        Assert.Equal("PATCH", sent.Method);

        // The venue needs the pair to find the order, in the query rather than the body.
        Assert.Equal("BTC_USDT", sent.Query("currency_pair"));
        Assert.Equal("0.010000", rig.Body("/api/v4/spot/orders/170000001").GetProperty("amount").GetString());
    }

    [Fact]
    public async Task A_dated_order_cannot_be_amended_and_the_strategy_is_told()
    {
        // The measurement this exists for. Gate's delivery surface has no amend endpoint of any kind - no PUT, no
        // PATCH, no batch - where spot has PATCH and perpetual futures has PUT. A cancel-and-replace here would
        // leave a dated position unguarded for the length of two requests without the caller having asked.
        await using Rig rig = await new Rig(GateProductType.Delivery).ConnectAsync();
        LimitOrder order = rig.Track(rig.Orders.Limit(
            rig.Traded.Id, OrderSide.Sell, rig.Qty(0.05m), rig.Px(85_000m), clientOrderId: new ClientOrderId("O-1")));

        await rig.Client.ModifyOrderAsync(
            new ModifyOrder(
                rig.Kernel.Services.TraderId,
                Rig.Strategy,
                rig.Traded.Id,
                order.ClientOrderId,
                new VenueOrderId("900000001"),
                rig.Qty(0.03m),
                null,
                null,
                null,
                Guid.NewGuid(),
                TestKernel.Now),
            CancellationToken.None);

        OrderModifyRejected rejected = await rig.Sink.NextOrderEventAsync<OrderModifyRejected>();
        Assert.Contains("cancel it and submit a new one", rejected.Reason, StringComparison.Ordinal);

        // And nothing was sent: no endpoint was guessed at.
        Assert.DoesNotContain(rig.Server.Requests, r => r.Method is "PUT" or "PATCH");
    }

    [Fact]
    public async Task An_amendment_with_nothing_to_change_is_refused_rather_than_sent()
    {
        await using Rig rig = await new Rig(GateProductType.Futures).ConnectAsync();

        await rig.Client.ModifyOrderAsync(
            new ModifyOrder(
                rig.Kernel.Services.TraderId,
                Rig.Strategy,
                rig.Traded.Id,
                new ClientOrderId("O-1"),
                new VenueOrderId("900000001"),
                null,
                null,
                null,
                null,
                Guid.NewGuid(),
                TestKernel.Now),
            CancellationToken.None);

        OrderModifyRejected rejected = await rig.Sink.NextOrderEventAsync<OrderModifyRejected>();
        Assert.Contains("price or a size", rejected.Reason, StringComparison.Ordinal);
    }

    // ----- cancels -----

    [Fact]
    public async Task A_cancel_names_the_order_by_the_venues_id_where_one_is_known()
    {
        // The venue takes either its own id or the user's text id in the path, and the text id works only while the
        // order is resting - so the venue id is preferred wherever one is known.
        await using Rig rig = await new Rig(GateProductType.Futures).ConnectAsync();

        await rig.Client.CancelOrderAsync(
            new CancelOrder(
                rig.Kernel.Services.TraderId,
                Rig.Strategy,
                rig.Traded.Id,
                new ClientOrderId("O-1"),
                new VenueOrderId("900000001"),
                null,
                Guid.NewGuid(),
                TestKernel.Now),
            CancellationToken.None);

        RecordedRequest sent = Assert.Single(rig.Server.RequestsTo("/api/v4/futures/usdt/orders/900000001"));
        Assert.Equal("DELETE", sent.Method);
    }

    [Fact]
    public async Task A_cancel_all_names_the_contract()
    {
        await using Rig rig = await new Rig(GateProductType.Futures).ConnectAsync();

        await rig.Client.CancelAllOrdersAsync(
            new CancelAllOrders(rig.Kernel.Services.TraderId, Rig.Strategy, rig.Traded.Id, null, null, Guid.NewGuid(), TestKernel.Now),
            CancellationToken.None);

        RecordedRequest sent = Assert.Single(rig.Server.RequestsTo("/api/v4/futures/usdt/orders"));
        Assert.Equal("DELETE", sent.Method);
        Assert.Equal("BTC_USDT", sent.Query("contract"));
    }

    // ----- reports -----

    [Fact]
    public async Task A_position_report_takes_its_side_from_the_sign_and_its_size_out_of_contracts()
    {
        // The venue signs a position: -500 contracts of 0.0001 BTC is 0.05 BTC short. Reading the magnitude without
        // the sign would report a short position as long, and reconciliation would then try to close it by selling.
        await using Rig rig = await new Rig(GateProductType.Futures).ConnectAsync();

        IReadOnlyList<PositionStatusReport> positions =
            await rig.Client.GeneratePositionStatusReportsAsync(null, null, null, CancellationToken.None);

        PositionStatusReport btc = positions.Single(p => p.InstrumentId == InstrumentId.Parse("BTC_USDT.GATE"));
        Assert.Equal(PositionSide.Short, btc.PositionSide);
        Assert.Equal(0.05m, btc.Quantity.Value);
        Assert.Equal(83500m, btc.AvgPxOpen);

        // A flat position the venue still lists is reported, so reconciliation can close one this node thinks open.
        PositionStatusReport eth = positions.Single(p => p.InstrumentId == InstrumentId.Parse("ETH_USDT.GATE"));
        Assert.Equal(PositionSide.Flat, eth.PositionSide);
        Assert.Equal(0m, eth.Quantity.Value);
    }

    [Fact]
    public async Task A_spot_account_has_no_position_to_report()
    {
        await using Rig rig = await new Rig(GateProductType.Spot).ConnectAsync();

        Assert.Empty(await rig.Client.GeneratePositionStatusReportsAsync(null, null, null, CancellationToken.None));
    }

    [Fact]
    public async Task A_spot_order_report_reads_the_client_id_back_out_of_the_text_field()
    {
        await using Rig rig = await new Rig(GateProductType.Spot).ConnectAsync();

        OrderStatusReport? report = await rig.Client.GenerateOrderStatusReportAsync(
            rig.Traded.Id,
            new ClientOrderId("O-1"),
            new VenueOrderId("170000001"),
            CancellationToken.None);

        Assert.NotNull(report);
        Assert.Equal(new ClientOrderId("O-1"), report!.ClientOrderId);
        Assert.Equal(OrderSide.Buy, report.OrderSide);
        Assert.Equal(OrderType.Limit, report.OrderType);
        Assert.Equal(OrderStatus.PartiallyFilled, report.OrderStatus);
        Assert.Equal(1m, report.Quantity.Value);
        Assert.Equal(0.5m, report.FilledQuantity.Value);
    }

    [Fact]
    public async Task A_spot_balance_is_published_on_connect()
    {
        await using Rig rig = await new Rig(GateProductType.Spot).ConnectAsync();

        AccountState state = await rig.Sink.NextAccountStateAsync();

        // available + locked, with the locked part reported as such.
        AccountBalance usdt = state.Balances.Single(b => b.Total.Currency.Code == "USDT");
        Assert.Equal(1026m, usdt.Total.Amount);
        Assert.Equal(25.5m, usdt.Locked.Amount);
    }

    [Fact]
    public async Task A_derivative_balance_is_published_on_connect()
    {
        await using Rig rig = await new Rig(GateProductType.Futures).ConnectAsync();

        AccountState state = await rig.Sink.NextAccountStateAsync();
        AccountBalance usdt = state.Balances.Single(b => b.Total.Currency.Code == "USDT");

        Assert.Equal(2500.25m, usdt.Total.Amount);
        Assert.Equal(100m, usdt.Locked.Amount);
    }

    [Fact]
    public async Task A_mass_status_asks_for_orders_fills_and_positions()
    {
        await using Rig rig = await new Rig(GateProductType.Futures).ConnectAsync();

        ExecutionMassStatus? status = await rig.Client.GenerateMassStatusAsync(null, CancellationToken.None);

        Assert.NotNull(status);
        Assert.NotEmpty(rig.Server.RequestsTo("/api/v4/futures/usdt/orders"));
        Assert.NotEmpty(rig.Server.RequestsTo("/api/v4/futures/usdt/my_trades"));
        Assert.NotEmpty(rig.Server.RequestsTo("/api/v4/futures/usdt/positions"));
    }

    // ----- the three markets against each other -----

    [Fact]
    public void Every_market_answers_the_same_commands_apart_from_amending()
    {
        // The parity table finds a venue's clients by naming convention and so sees only the spot ones. This keeps
        // the other two honest - and states the one difference out loud: the delivery client overrides exactly one
        // method, which is the amendment it cannot make.
        Assert.Equal(
            Commands.Overridden(typeof(GateExecutionClient)),
            Commands.Overridden(typeof(GateFuturesExecutionClient)));

        Assert.Equal(["ModifyOrderAsync"], Commands.Overridden(typeof(GateDeliveryExecutionClient)));
    }

    [Fact]
    public void A_client_handed_the_wrong_markets_configuration_refuses_to_be_built()
    {
        using TestKernel kernel = new();
        GateExecutionClientConfig spot = new() { ApiKey = ApiKey, ApiSecret = ApiSecret };
        GateExecutionClientConfig futures = new() { ApiKey = ApiKey, ApiSecret = ApiSecret, ProductType = GateProductType.Futures };

        Assert.Throws<ArgumentException>(() => new GateExecutionClient(new ClientId("GATE"), futures, kernel.Services));
        Assert.Throws<ArgumentException>(() => new GateFuturesExecutionClient(new ClientId("GATE"), spot, kernel.Services));
        Assert.Throws<ArgumentException>(() => new GateDeliveryExecutionClient(new ClientId("GATE"), futures, kernel.Services));
    }

    [Fact]
    public void The_factory_builds_the_client_the_configuration_names()
    {
        using TestKernel kernel = new();
        GateExecutionClientFactory factory = new();

        Assert.Equal("GATE", factory.Name);
        Assert.IsType<GateExecutionClient>(factory.Create(
            new ClientId("GATE"), new GateExecutionClientConfig { ApiKey = ApiKey, ApiSecret = ApiSecret }, kernel.Services));

        Assert.IsType<GateFuturesExecutionClient>(factory.Create(
            new ClientId("GATE"),
            new GateExecutionClientConfig { ApiKey = ApiKey, ApiSecret = ApiSecret, ProductType = GateProductType.Futures },
            kernel.Services));

        Assert.IsType<GateDeliveryExecutionClient>(factory.Create(
            new ClientId("GATE"),
            new GateExecutionClientConfig { ApiKey = ApiKey, ApiSecret = ApiSecret, ProductType = GateProductType.Delivery },
            kernel.Services));
    }

    [Fact]
    public async Task A_refused_order_reaches_the_strategy_with_the_venues_own_sentence()
    {
        await using Rig rig = await new Rig(GateProductType.Futures).ConnectAsync();
        rig.Routes.On("POST", "/api/v4/futures/usdt/orders", _ => new StubResponse(400, GatePayloads.Error("BALANCE_NOT_ENOUGH", "not enough balance")));

        await rig.SubmitAsync(rig.Track(rig.Orders.Market(rig.Traded.Id, OrderSide.Buy, rig.Qty(0.05m))));

        // Submitted first - the order really was sent - and then rejected with the venue's own sentence rather
        // than with a status code or a stack trace.
        await rig.Sink.NextOrderEventAsync<OrderSubmitted>();
        OrderRejected rejected = await rig.Sink.NextOrderEventAsync<OrderRejected>();
        Assert.Equal("not enough balance", rejected.Reason);
    }
}
