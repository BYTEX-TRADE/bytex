using System.Security.Cryptography;
using System.Text;
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

// Why: this client sends real orders. What must hold: every private call is signed the way the venue documents (checked
// here with an HMAC computed independently), an order reaches the venue with the fields it means, a stop order goes to the
// venue's stop endpoint with the right trigger direction, cancels go where the order actually lives, and the stream's
// events become the engine's events once each. Expected values are worked out by hand from the venue's message shapes.
public sealed class KucoinExecutionClientTests
{
    private const string ApiKey = "test-key";
    private const string ApiSecret = "test-secret";
    private const string Passphrase = "test-passphrase";

    private sealed class Rig : IAsyncDisposable
    {
        private WsSession? _session;

        public Rig(string keyVersion = "3")
        {
            Routes = new Routes()
                .On("GET", "/api/v2/symbols", KucoinPayloads.Symbols)
                .On("POST", "/api/v1/bullet-private", _ => StubResponse.Json(KucoinPayloads.Bullet(Server!.WsBase, "private-token-1")))
                .On("GET", "/api/v1/accounts", KucoinPayloads.Accounts);
            Server = new LoopbackServer(Routes.Handle);
            Kernel = new TestKernel();
            Instrument = Spot();
            Kernel.Kernel.Cache.AddInstrument(Instrument);
            Client = new KucoinExecutionClient(new ClientId("KUCOIN"), new KucoinExecutionClientConfig
            {
                ApiKey = ApiKey,
                ApiSecret = ApiSecret,
                ApiPassphrase = Passphrase,
                ApiKeyVersion = keyVersion,
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

        public Instrument Instrument { get; }

        public KucoinExecutionClient Client { get; }

        public RecordingExecutionSink Sink { get; } = new();

        public OrderFactory Orders { get; }

        public WsSession Session => _session ?? throw new InvalidOperationException("not connected");

        public Quantity Qty(decimal value) => Instrument.MakeQuantity(value);

        public Price Px(decimal value) => Instrument.MakePrice(value);

        public static CurrencyPair Spot() => new(new InstrumentSpec
        {
            Id = InstrumentId.Parse("BTC-USDT.KUCOIN"),
            RawSymbol = new Symbol("BTC-USDT"),
            AssetClass = AssetClass.Crypto,
            InstrumentClass = InstrumentClass.Spot,
            QuoteCurrency = Currencies.USDT,
            BaseCurrency = Currencies.BTC,
            SettlementCurrency = Currencies.USDT,
            PricePrecision = 1,
            SizePrecision = 8,
            PriceIncrement = new Price(0.1m, 1),
            SizeIncrement = new Quantity(0.00000001m, 8),
            MakerFee = 0.001m,
            TakerFee = 0.001m,
        });

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

    private static string Hmac(string secret, string text) => Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(text)));

    private static void AssertSigned(RecordedRequest request, string endpoint, string keyVersion = "3")
    {
        string timestamp = request.Header("KC-API-TIMESTAMP")!;
        Assert.Equal(ApiKey, request.Header("KC-API-KEY"));
        Assert.Equal(keyVersion, request.Header("KC-API-KEY-VERSION"));
        // timestamp + METHOD + path with its query + body, HMAC-SHA256 with the secret, base64.
        Assert.Equal(Hmac(ApiSecret, timestamp + request.Method + endpoint + request.Body), request.Header("KC-API-SIGN"));
        // The passphrase never travels in clear: it is signed with the secret too.
        Assert.Equal(Hmac(ApiSecret, Passphrase), request.Header("KC-API-PASSPHRASE"));
        Assert.DoesNotContain(Passphrase, string.Join('|', request.Headers.Values), StringComparison.Ordinal);
        Assert.True(Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - long.Parse(timestamp, System.Globalization.CultureInfo.InvariantCulture)) < 60_000);
    }

    [Fact]
    public async Task Connecting_signs_its_calls_publishes_the_trade_account_and_subscribes_the_private_channels()
    {
        await using Rig rig = await new Rig().ConnectAsync();

        AssertSigned(Assert.Single(rig.Server.RequestsTo("/api/v1/bullet-private")), "/api/v1/bullet-private");
        // The query is part of what is signed.
        RecordedRequest accounts = Assert.Single(rig.Server.RequestsTo("/api/v1/accounts"));
        Assert.Equal("trade", accounts.Query("type"));
        AssertSigned(accounts, "/api/v1/accounts?type=trade");
        Assert.Contains("token=private-token-1", rig.Session.RawQuery, StringComparison.Ordinal);

        AccountState state = await rig.Sink.NextAccountStateAsync();
        Assert.Equal(new AccountId("KUCOIN-SPOT"), state.AccountId);
        AccountBalance usdt = state.Balances.Single(b => b.Currency.Code == "USDT");
        Assert.Equal((26.66759503m, 1m, 25.66759503m), (usdt.Total.Amount, usdt.Locked.Amount, usdt.Free.Amount));
        Assert.Equal(0.5m, state.Balances.Single(b => b.Currency.Code == "BTC").Total.Amount);

        List<string> topics = new();
        while (topics.Count < 3)
        {
            using JsonDocument doc = JsonDocument.Parse(await rig.Session.ReceiveTextAsync(Wait.Timeout));
            if (doc.RootElement.GetProperty("type").GetString() == "subscribe")
            {
                Assert.True(doc.RootElement.GetProperty("privateChannel").GetBoolean());
                topics.Add(doc.RootElement.GetProperty("topic").GetString()!);
            }
        }

        Assert.Equal(["/account/balance", "/spotMarket/advancedOrders", "/spotMarket/tradeOrdersV2"], topics.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_key_version_the_configuration_names_is_the_one_sent()
    {
        await using Rig rig = await new Rig(keyVersion: "2").ConnectAsync();

        AssertSigned(Assert.Single(rig.Server.RequestsTo("/api/v1/bullet-private")), "/api/v1/bullet-private", keyVersion: "2");
    }

    [Fact]
    public async Task A_limit_order_reaches_the_venue_with_its_price_size_and_client_id()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        rig.Routes.On("POST", "/api/v1/hf/orders", KucoinPayloads.Envelope("""{"orderId":"670fd33bf9406e0007ab3945","clientOid":"x"}"""));
        LimitOrder order = rig.Track(rig.Orders.Limit(rig.Instrument.Id, OrderSide.Buy, rig.Qty(0.5m), rig.Px(50_000m), postOnly: true));

        await rig.SubmitAsync(order);

        Assert.IsType<OrderSubmitted>(await rig.Sink.NextOrderEventAsync());
        RecordedRequest request = Assert.Single(rig.Server.RequestsTo("/api/v1/hf/orders"));
        AssertSigned(request, "/api/v1/hf/orders");
        JsonElement body = rig.Body("/api/v1/hf/orders");
        Assert.Equal((order.ClientOrderId.Value, "BTC-USDT", "buy", "limit"), (body.GetProperty("clientOid").GetString(), body.GetProperty("symbol").GetString(), body.GetProperty("side").GetString(), body.GetProperty("type").GetString()));
        Assert.Equal(("50000.0", "0.50000000", "GTC"), (body.GetProperty("price").GetString(), body.GetProperty("size").GetString(), body.GetProperty("timeInForce").GetString()));
        Assert.True(body.GetProperty("postOnly").GetBoolean());
        Assert.False(body.TryGetProperty("stop", out _));
    }

    [Theory]
    [InlineData(TimeInForce.Ioc, "IOC")]
    [InlineData(TimeInForce.Fok, "FOK")]
    public async Task Immediate_orders_name_their_time_in_force_and_never_post_only(TimeInForce tif, string expected)
    {
        await using Rig rig = await new Rig().ConnectAsync();
        rig.Routes.On("POST", "/api/v1/hf/orders", KucoinPayloads.Envelope("""{"orderId":"1","clientOid":"x"}"""));

        await rig.SubmitAsync(rig.Track(rig.Orders.Limit(rig.Instrument.Id, OrderSide.Sell, rig.Qty(0.5m), rig.Px(50_000m), tif)));

        JsonElement body = rig.Body("/api/v1/hf/orders");
        Assert.Equal(expected, body.GetProperty("timeInForce").GetString());
        Assert.False(body.TryGetProperty("postOnly", out _));
    }

    [Fact]
    public async Task A_good_till_date_order_becomes_GTT_with_the_seconds_left()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        rig.Routes.On("POST", "/api/v1/hf/orders", KucoinPayloads.Envelope("""{"orderId":"1","clientOid":"x"}"""));
        UnixNanos expire = new(TestKernel.Now.Value + 90 * UnixNanos.NanosPerSecond);

        await rig.SubmitAsync(rig.Track(rig.Orders.Limit(rig.Instrument.Id, OrderSide.Buy, rig.Qty(0.5m), rig.Px(50_000m), TimeInForce.Gtd, expire)));

        JsonElement body = rig.Body("/api/v1/hf/orders");
        Assert.Equal(("GTT", 90L), (body.GetProperty("timeInForce").GetString(), body.GetProperty("cancelAfter").GetInt64()));
    }

    [Fact]
    public async Task A_market_order_sends_size_and_a_quote_quantity_order_sends_funds()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        rig.Routes.On("POST", "/api/v1/hf/orders", KucoinPayloads.Envelope("""{"orderId":"1","clientOid":"x"}"""));

        await rig.SubmitAsync(rig.Track(rig.Orders.Market(rig.Instrument.Id, OrderSide.Buy, rig.Qty(0.25m))));
        JsonElement bySize = rig.Body("/api/v1/hf/orders");
        await rig.SubmitAsync(rig.Track(rig.Orders.Market(rig.Instrument.Id, OrderSide.Buy, rig.Qty(1_000m), quoteQuantity: true)));
        JsonElement byFunds = rig.Body("/api/v1/hf/orders");

        Assert.Equal(("market", "0.25000000"), (bySize.GetProperty("type").GetString(), bySize.GetProperty("size").GetString()));
        Assert.False(bySize.TryGetProperty("funds", out _));
        Assert.False(bySize.TryGetProperty("price", out _));
        Assert.Equal("1000.00000000", byFunds.GetProperty("funds").GetString());
        Assert.False(byFunds.TryGetProperty("size", out _));
    }

    // "loss" triggers when the price falls to the stop price, "entry" when it rises to it.
    [Theory]
    [InlineData(OrderType.StopMarket, OrderSide.Sell, "loss", "market")]
    [InlineData(OrderType.StopMarket, OrderSide.Buy, "entry", "market")]
    [InlineData(OrderType.MarketIfTouched, OrderSide.Sell, "entry", "market")]
    [InlineData(OrderType.MarketIfTouched, OrderSide.Buy, "loss", "market")]
    [InlineData(OrderType.StopLimit, OrderSide.Sell, "loss", "limit")]
    [InlineData(OrderType.LimitIfTouched, OrderSide.Sell, "entry", "limit")]
    public async Task An_order_with_a_trigger_is_a_stop_order_with_the_direction_its_kind_and_side_imply(OrderType type, OrderSide side, string stop, string venueType)
    {
        await using Rig rig = await new Rig().ConnectAsync();
        rig.Routes.On("POST", "/api/v1/stop-order", KucoinPayloads.Envelope("""{"orderId":"vs93gptvr9t2fsql003l8k5p"}"""));
        InstrumentId id = rig.Instrument.Id;
        Order order = rig.Track<Order>(type switch
        {
            OrderType.StopMarket => rig.Orders.StopMarket(id, side, rig.Qty(0.5m), rig.Px(49_000m)),
            OrderType.MarketIfTouched => rig.Orders.MarketIfTouched(id, side, rig.Qty(0.5m), rig.Px(49_000m)),
            OrderType.StopLimit => rig.Orders.StopLimit(id, side, rig.Qty(0.5m), rig.Px(48_900m), rig.Px(49_000m)),
            _ => rig.Orders.LimitIfTouched(id, side, rig.Qty(0.5m), rig.Px(48_900m), rig.Px(49_000m)),
        });

        await rig.SubmitAsync(order);

        Assert.Empty(rig.Server.RequestsTo("/api/v1/hf/orders"));
        JsonElement body = rig.Body("/api/v1/stop-order");
        Assert.Equal((stop, "49000.0", venueType), (body.GetProperty("stop").GetString(), body.GetProperty("stopPrice").GetString(), body.GetProperty("type").GetString()));
        Assert.Equal(venueType == "limit", body.TryGetProperty("price", out JsonElement price) && price.GetString() == "48900.0");
        Assert.IsType<OrderSubmitted>(await rig.Sink.NextOrderEventAsync());
        // The venue id of a stop order is only in the answer to the request, so the acceptance is made from it.
        OrderAccepted accepted = await rig.Sink.NextOrderEventAsync<OrderAccepted>();
        Assert.Equal((order.ClientOrderId, new VenueOrderId("vs93gptvr9t2fsql003l8k5p")), (accepted.ClientOrderId, accepted.VenueOrderId));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(200)]
    public async Task A_refusal_rejects_the_order_with_the_venues_own_words_whatever_the_http_status(int status)
    {
        await using Rig rig = await new Rig().ConnectAsync();
        rig.Routes.On("POST", "/api/v1/hf/orders", _ => new StubResponse(status, KucoinPayloads.Error("200004", "Balance insufficient!")));
        LimitOrder order = rig.Track(rig.Orders.Limit(rig.Instrument.Id, OrderSide.Buy, rig.Qty(0.5m), rig.Px(50_000m)));

        await rig.SubmitAsync(order);

        Assert.IsType<OrderSubmitted>(await rig.Sink.NextOrderEventAsync());
        OrderRejected rejected = await rig.Sink.NextOrderEventAsync<OrderRejected>();
        Assert.Equal((order.ClientOrderId, "Balance insufficient!"), (rejected.ClientOrderId, rejected.Reason));
    }

    [Fact]
    public async Task A_client_order_id_longer_than_the_venue_takes_is_rejected_before_anything_is_sent()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        LimitOrder order = rig.Track(rig.Orders.Limit(rig.Instrument.Id, OrderSide.Buy, rig.Qty(0.5m), rig.Px(50_000m), clientOrderId: new ClientOrderId(new string('A', 41))));

        await rig.SubmitAsync(order);

        OrderRejected rejected = await rig.Sink.NextOrderEventAsync<OrderRejected>();
        Assert.Contains("40", rejected.Reason, StringComparison.Ordinal);
        Assert.Empty(rig.Server.RequestsTo("/api/v1/hf/orders"));
    }

    [Fact]
    public async Task The_order_channel_accepts_fills_once_updates_and_cancels_the_cached_order()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        LimitOrder order = rig.Track(rig.Orders.Limit(rig.Instrument.Id, OrderSide.Buy, rig.Qty(0.5m), rig.Px(50_000m)));
        string oid = order.ClientOrderId.Value;
        string match = KucoinPayloads.OrderChange(oid, "match", "match", ",\"matchPrice\":\"49990\",\"matchSize\":\"0.2\",\"tradeId\":\"T-1\",\"liquidity\":\"taker\",\"feeType\":\"takerFee\",\"filledSize\":\"0.2\",\"remainSize\":\"0.3\"");

        await rig.Session.SendTextAsync(KucoinPayloads.OrderChange(oid, "received", "new"));
        await rig.Session.SendTextAsync(match);
        await rig.Session.SendTextAsync(match);
        await rig.Session.SendTextAsync(KucoinPayloads.OrderChange(oid, "canceled", "done"));

        OrderAccepted accepted = await rig.Sink.NextOrderEventAsync<OrderAccepted>();
        Assert.Equal((order.ClientOrderId, new VenueOrderId("6720da3fa30a360007f5f832"), Rig.Strategy), (accepted.ClientOrderId, accepted.VenueOrderId, accepted.StrategyId));
        Assert.Equal(1_730_206_271_616_000_000L, accepted.TsEvent.Value);

        OrderFilled fill = await rig.Sink.NextOrderEventAsync<OrderFilled>();
        Assert.Equal((new Quantity(0.2m, 8), new Price(49_990m, 1), LiquiditySide.Taker, new TradeId("T-1")), (fill.LastQty, fill.LastPx, fill.LiquiditySide, fill.TradeId));
        Assert.Equal(OrderType.Limit, fill.OrderType);
        // The stream carries no fee: 49,990 x 0.2 x 0.1% = 9.998 USDT, from the instrument's taker rate.
        Assert.Equal(new Money(9.998m, Currencies.USDT), fill.Commission);

        // The repeated match produced nothing: the next event is the cancel.
        OrderCanceled canceled = Assert.IsType<OrderCanceled>(await rig.Sink.NextOrderEventAsync());
        Assert.Equal(order.ClientOrderId, canceled.ClientOrderId);
    }

    [Fact]
    public async Task An_order_placed_outside_the_engine_is_attributed_to_the_EXTERNAL_strategy()
    {
        await using Rig rig = await new Rig().ConnectAsync();

        await rig.Session.SendTextAsync(KucoinPayloads.OrderChange("manual-1", "open", "open"));

        OrderAccepted accepted = await rig.Sink.NextOrderEventAsync<OrderAccepted>();
        Assert.Equal((StrategyId.External, InstrumentId.Parse("BTC-USDT.KUCOIN")), (accepted.StrategyId, accepted.InstrumentId));
    }

    [Fact]
    public async Task A_balance_message_updates_its_currency_and_keeps_the_others()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        await rig.Sink.NextAccountStateAsync();

        await rig.Session.SendTextAsync(KucoinPayloads.Balance);

        AccountState state = await rig.Sink.NextAccountStateAsync();
        AccountBalance usdt = state.Balances.Single(b => b.Currency.Code == "USDT");
        // The venue sends 21.133773386762; money is held at the currency's eight decimals.
        Assert.Equal((21.13377339m, 1.001m), (usdt.Total.Amount, usdt.Locked.Amount));
        Assert.Equal(0.5m, state.Balances.Single(b => b.Currency.Code == "BTC").Total.Amount);
        Assert.Equal(1_730_269_283_892_000_000L, state.TsEvent.Value);
    }

    [Fact]
    public async Task A_plain_order_is_cancelled_by_client_id_among_the_plain_orders()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        LimitOrder order = rig.Track(rig.Orders.Limit(rig.Instrument.Id, OrderSide.Buy, rig.Qty(0.5m), rig.Px(50_000m)));
        string path = "/api/v1/hf/orders/client-order/" + order.ClientOrderId.Value;
        rig.Routes.On("DELETE", path, KucoinPayloads.Envelope("{\"clientOid\":\"x\"}"));

        await rig.Client.CancelOrderAsync(new CancelOrder(rig.Kernel.Services.TraderId, Rig.Strategy, order.InstrumentId, order.ClientOrderId, null, null, Guid.NewGuid(), TestKernel.Now), CancellationToken.None);

        RecordedRequest request = Assert.Single(rig.Server.RequestsTo(path));
        Assert.Equal("BTC-USDT", request.Query("symbol"));
        AssertSigned(request, path + "?symbol=BTC-USDT");
        Assert.IsType<OrderPendingCancel>(await rig.Sink.NextOrderEventAsync());
    }

    [Fact]
    public async Task A_stop_order_that_has_not_triggered_is_cancelled_in_the_stop_order_list()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        StopMarketOrder order = rig.Track(rig.Orders.StopMarket(rig.Instrument.Id, OrderSide.Sell, rig.Qty(0.5m), rig.Px(49_000m)));
        rig.Routes.On("DELETE", "/api/v1/stop-order/cancelOrderByClientOid", KucoinPayloads.Envelope("{\"cancelledOrderId\":\"vs8\",\"clientOid\":\"x\"}"));

        await rig.Client.CancelOrderAsync(new CancelOrder(rig.Kernel.Services.TraderId, Rig.Strategy, order.InstrumentId, order.ClientOrderId, null, null, Guid.NewGuid(), TestKernel.Now), CancellationToken.None);

        RecordedRequest request = Assert.Single(rig.Server.RequestsTo("/api/v1/stop-order/cancelOrderByClientOid"));
        Assert.Equal((order.ClientOrderId.Value, "BTC-USDT"), (request.Query("clientOid"), request.Query("symbol")));
        Assert.DoesNotContain(rig.Server.Requests, r => r.Path.StartsWith("/api/v1/hf/orders", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_refused_cancel_is_reported_with_the_venues_words()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        LimitOrder order = rig.Track(rig.Orders.Limit(rig.Instrument.Id, OrderSide.Buy, rig.Qty(0.5m), rig.Px(50_000m)));
        rig.Routes.On("DELETE", "/api/v1/hf/orders/client-order/" + order.ClientOrderId.Value, _ => StubResponse.Error(400, KucoinPayloads.Error("400100", "order_not_exist_or_not_allow_to_cancel")));

        await rig.Client.CancelOrderAsync(new CancelOrder(rig.Kernel.Services.TraderId, Rig.Strategy, order.InstrumentId, order.ClientOrderId, null, null, Guid.NewGuid(), TestKernel.Now), CancellationToken.None);

        Assert.IsType<OrderPendingCancel>(await rig.Sink.NextOrderEventAsync());
        OrderCancelRejected rejected = await rig.Sink.NextOrderEventAsync<OrderCancelRejected>();
        Assert.Equal("order_not_exist_or_not_allow_to_cancel", rejected.Reason);
    }

    [Fact]
    public async Task Cancel_all_clears_both_the_plain_orders_and_the_stop_orders_of_the_symbol()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        rig.Routes.On("DELETE", "/api/v1/hf/orders", KucoinPayloads.Envelope("\"success\""));
        rig.Routes.On("DELETE", "/api/v1/stop-order/cancel", KucoinPayloads.Envelope("{\"cancelledOrderIds\":[]}"));

        await rig.Client.CancelAllOrdersAsync(new CancelAllOrders(rig.Kernel.Services.TraderId, Rig.Strategy, rig.Instrument.Id, null, null, Guid.NewGuid(), TestKernel.Now), CancellationToken.None);

        Assert.Equal("BTC-USDT", Assert.Single(rig.Server.RequestsTo("/api/v1/hf/orders")).Query("symbol"));
        Assert.Equal("BTC-USDT", Assert.Single(rig.Server.RequestsTo("/api/v1/stop-order/cancel")).Query("symbol"));
    }

    [Fact]
    public async Task A_modification_of_a_plain_order_alters_price_and_size_by_client_id()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        rig.Routes.On("POST", "/api/v1/hf/orders/alter", KucoinPayloads.Envelope("{\"newOrderId\":\"67112258f9406e0007408827\",\"clientOid\":\"x\"}"));
        LimitOrder order = rig.Track(rig.Orders.Limit(rig.Instrument.Id, OrderSide.Buy, rig.Qty(0.5m), rig.Px(50_000m)));

        await rig.Client.ModifyOrderAsync(new ModifyOrder(rig.Kernel.Services.TraderId, Rig.Strategy, order.InstrumentId, order.ClientOrderId, null, rig.Qty(0.4m), rig.Px(49_500m), null, null, Guid.NewGuid(), TestKernel.Now), CancellationToken.None);

        JsonElement body = rig.Body("/api/v1/hf/orders/alter");
        Assert.Equal((order.ClientOrderId.Value, "BTC-USDT", "49500.0", "0.40000000"), (body.GetProperty("clientOid").GetString(), body.GetProperty("symbol").GetString(), body.GetProperty("newPrice").GetString(), body.GetProperty("newSize").GetString()));
        Assert.IsType<OrderPendingUpdate>(await rig.Sink.NextOrderEventAsync());
    }

    // ----- a stop order cannot be changed at the venue: a change is a cancel and a new stop order -----

    private static async Task<StopMarketOrder> PlacedStopAsync(Rig rig, string venueId = "stop-1")
    {
        rig.Routes.On("POST", "/api/v1/stop-order", KucoinPayloads.Envelope("{\"orderId\":\"" + venueId + "\"}"));
        rig.Routes.On("DELETE", "/api/v1/stop-order/cancelOrderByClientOid", KucoinPayloads.Envelope("{\"cancelledOrderId\":\"" + venueId + "\",\"clientOid\":\"x\"}"));
        StopMarketOrder stop = rig.Track(rig.Orders.StopMarket(rig.Instrument.Id, OrderSide.Sell, rig.Qty(0.5m), rig.Px(49_000m)));
        await rig.SubmitAsync(stop);
        Assert.IsType<OrderSubmitted>(await rig.Sink.NextOrderEventAsync());
        Assert.IsType<OrderAccepted>(await rig.Sink.NextOrderEventAsync());
        return stop;
    }

    private static Task MoveStopAsync(Rig rig, Order stop, decimal trigger) =>
        rig.Client.ModifyOrderAsync(new ModifyOrder(rig.Kernel.Services.TraderId, Rig.Strategy, stop.InstrumentId, stop.ClientOrderId, null, null, null, rig.Px(trigger), null, Guid.NewGuid(), TestKernel.Now), CancellationToken.None).WaitAsync(Wait.Timeout);

    [Fact]
    public async Task Moving_a_stop_cancels_it_and_places_a_new_one_at_the_new_trigger_and_the_engine_keeps_its_own_id()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        StopMarketOrder stop = await PlacedStopAsync(rig);
        rig.Routes.On("POST", "/api/v1/stop-order", KucoinPayloads.Envelope("{\"orderId\":\"stop-2\"}"));

        await MoveStopAsync(rig, stop, 49_400m);

        // Cancelled under the id it was placed with, then placed again under a new one: the venue takes a client id once.
        RecordedRequest cancel = Assert.Single(rig.Server.RequestsTo("/api/v1/stop-order/cancelOrderByClientOid"));
        Assert.Equal((stop.ClientOrderId.Value, "BTC-USDT"), (cancel.Query("clientOid"), cancel.Query("symbol")));
        List<RecordedRequest> all = rig.Server.Requests.ToList();
        Assert.True(all.IndexOf(cancel) < all.IndexOf(rig.Server.RequestsTo("/api/v1/stop-order").Last()));
        JsonElement body = rig.Body("/api/v1/stop-order");
        Assert.Equal((stop.ClientOrderId.Value + "-r1", "49400.0", "loss", "sell", "market", "0.50000000"),
            (body.GetProperty("clientOid").GetString(), body.GetProperty("stopPrice").GetString(), body.GetProperty("stop").GetString(), body.GetProperty("side").GetString(), body.GetProperty("type").GetString(), body.GetProperty("size").GetString()));

        Assert.IsType<OrderPendingUpdate>(await rig.Sink.NextOrderEventAsync());
        OrderUpdated updated = await rig.Sink.NextOrderEventAsync<OrderUpdated>();
        Assert.Equal((stop.ClientOrderId, new VenueOrderId("stop-2"), rig.Px(49_400m), rig.Qty(0.5m)), (updated.ClientOrderId, updated.VenueOrderId, updated.TriggerPrice, updated.Quantity));
    }

    [Fact]
    public async Task The_venues_cancel_of_the_replaced_stop_does_not_close_the_engines_order_and_the_new_one_is_followed_under_the_engines_id()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        StopMarketOrder stop = await PlacedStopAsync(rig);
        rig.Routes.On("POST", "/api/v1/stop-order", KucoinPayloads.Envelope("{\"orderId\":\"stop-2\"}"));
        await MoveStopAsync(rig, stop, 49_400m);
        Assert.IsType<OrderPendingUpdate>(await rig.Sink.NextOrderEventAsync());
        Assert.IsType<OrderUpdated>(await rig.Sink.NextOrderEventAsync());

        // The stream reports the old stop order cancelled (our own doing), then the new one triggers and fills under "-r1".
        await rig.Session.SendTextAsync(KucoinPayloads.StopOrderChange("stop-1", "cancel"));
        string replacement = stop.ClientOrderId.Value + "-r1";
        await rig.Session.SendTextAsync(KucoinPayloads.OrderChange(replacement, "match", "match", ",\"matchPrice\":\"49390\",\"matchSize\":\"0.5\",\"tradeId\":\"T-9\",\"liquidity\":\"taker\""));

        OrderFilled fill = Assert.IsType<OrderFilled>(await rig.Sink.NextOrderEventAsync());
        Assert.Equal((stop.ClientOrderId, Rig.Strategy, new Quantity(0.5m, 8)), (fill.ClientOrderId, fill.StrategyId, fill.LastQty));
        Assert.Equal(OrderType.StopMarket, fill.OrderType);
    }

    [Fact]
    public async Task A_cancel_of_the_current_stop_order_on_the_stream_cancels_the_engines_order()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        StopMarketOrder stop = await PlacedStopAsync(rig);

        await rig.Session.SendTextAsync(KucoinPayloads.StopOrderChange("stop-1", "cancel"));

        OrderCanceled canceled = Assert.IsType<OrderCanceled>(await rig.Sink.NextOrderEventAsync());
        Assert.Equal(stop.ClientOrderId, canceled.ClientOrderId);
    }

    [Fact]
    public async Task A_replaced_stop_is_later_cancelled_under_the_id_it_was_placed_again_with()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        StopMarketOrder stop = await PlacedStopAsync(rig);
        rig.Routes.On("POST", "/api/v1/stop-order", KucoinPayloads.Envelope("{\"orderId\":\"stop-2\"}"));
        await MoveStopAsync(rig, stop, 49_400m);

        await rig.Client.CancelOrderAsync(new CancelOrder(rig.Kernel.Services.TraderId, Rig.Strategy, stop.InstrumentId, stop.ClientOrderId, null, null, Guid.NewGuid(), TestKernel.Now), CancellationToken.None);

        Assert.Equal([stop.ClientOrderId.Value, stop.ClientOrderId.Value + "-r1"], rig.Server.RequestsTo("/api/v1/stop-order/cancelOrderByClientOid").Select(r => r.Query("clientOid")));
    }

    [Fact]
    public async Task When_the_venue_refuses_the_new_stop_the_previous_one_is_placed_again_and_the_modification_is_rejected()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        StopMarketOrder stop = await PlacedStopAsync(rig);
        rig.Routes.On("POST", "/api/v1/stop-order", r => r.Body.Contains("\"stopPrice\":\"60000.0\"", StringComparison.Ordinal)
            ? StubResponse.Error(400, KucoinPayloads.Error("400100", "Stop price is invalid"))
            : StubResponse.Json(KucoinPayloads.Envelope("{\"orderId\":\"stop-3\"}")));

        await MoveStopAsync(rig, stop, 60_000m);

        JsonElement restored = rig.Body("/api/v1/stop-order");
        Assert.Equal((stop.ClientOrderId.Value + "-r2", "49000.0"), (restored.GetProperty("clientOid").GetString(), restored.GetProperty("stopPrice").GetString()));
        Assert.IsType<OrderPendingUpdate>(await rig.Sink.NextOrderEventAsync());
        OrderModifyRejected rejected = await rig.Sink.NextOrderEventAsync<OrderModifyRejected>();
        Assert.Equal((stop.ClientOrderId, new VenueOrderId("stop-3")), (rejected.ClientOrderId, rejected.VenueOrderId));
        Assert.Contains("Stop price is invalid", rejected.Reason, StringComparison.Ordinal);
        Assert.Contains("placed again", rejected.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task When_neither_the_new_stop_nor_the_previous_one_can_be_placed_the_order_is_reported_cancelled()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        StopMarketOrder stop = await PlacedStopAsync(rig);
        rig.Routes.On("POST", "/api/v1/stop-order", _ => StubResponse.Error(400, KucoinPayloads.Error("200004", "Balance insufficient!")));

        await MoveStopAsync(rig, stop, 49_400m);

        Assert.IsType<OrderPendingUpdate>(await rig.Sink.NextOrderEventAsync());
        // That is the truth at the venue: the stop is gone. The strategy hears it and can act.
        OrderCanceled canceled = Assert.IsType<OrderCanceled>(await rig.Sink.NextOrderEventAsync());
        Assert.Equal(stop.ClientOrderId, canceled.ClientOrderId);
    }

    [Fact]
    public async Task When_the_stop_cannot_be_cancelled_nothing_is_placed_and_the_modification_is_rejected()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        StopMarketOrder stop = await PlacedStopAsync(rig);
        rig.Routes.On("DELETE", "/api/v1/stop-order/cancelOrderByClientOid", _ => StubResponse.Error(400, KucoinPayloads.Error("400100", "order not exist")));
        int placedBefore = rig.Server.RequestsTo("/api/v1/stop-order").Count;

        await MoveStopAsync(rig, stop, 49_400m);

        Assert.Equal(placedBefore, rig.Server.RequestsTo("/api/v1/stop-order").Count);
        Assert.IsType<OrderPendingUpdate>(await rig.Sink.NextOrderEventAsync());
        OrderModifyRejected rejected = await rig.Sink.NextOrderEventAsync<OrderModifyRejected>();
        Assert.Contains("order not exist", rejected.Reason, StringComparison.Ordinal);
        // Still the current stop: its cancel on the stream is believed.
        await rig.Session.SendTextAsync(KucoinPayloads.StopOrderChange("stop-1", "cancel"));
        Assert.IsType<OrderCanceled>(await rig.Sink.NextOrderEventAsync());
    }

    [Fact]
    public async Task A_replacement_id_stays_within_the_venues_forty_characters()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        rig.Routes.On("POST", "/api/v1/stop-order", KucoinPayloads.Envelope("{\"orderId\":\"stop-1\"}"));
        rig.Routes.On("DELETE", "/api/v1/stop-order/cancelOrderByClientOid", KucoinPayloads.Envelope("{\"cancelledOrderId\":\"stop-1\",\"clientOid\":\"x\"}"));
        string longest = new('A', 40);
        StopMarketOrder stop = rig.Track(rig.Orders.StopMarket(rig.Instrument.Id, OrderSide.Sell, rig.Qty(0.5m), rig.Px(49_000m), clientOrderId: new ClientOrderId(longest)));
        await rig.SubmitAsync(stop);

        await MoveStopAsync(rig, stop, 49_400m);

        string sent = rig.Body("/api/v1/stop-order").GetProperty("clientOid").GetString()!;
        Assert.Equal((40, true), (sent.Length, sent.EndsWith("-r1", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Asking_after_one_order_reaches_the_venue()
    {
        // The command a strategy sends when it wants to know what became of an order. It used to be answered here by
        // a completed task and no request at all, which reads to the caller exactly like a query that worked.
        await using Rig rig = await new Rig().ConnectAsync();
        rig.Routes.On("GET", "/api/v1/hf/orders/client-order/O-open-1", KucoinPayloads.Envelope("""
            {"id":"67120bbef094e200070976f6","clientOid":"O-open-1","symbol":"BTC-USDT","opType":"DEAL","type":"limit","side":"buy","price":"50000","size":"0.5","funds":"25000","dealSize":"0.2","dealFunds":"9990","fee":"9.99","feeCurrency":"USDT","stp":null,"timeInForce":"GTC","postOnly":true,"hidden":false,"iceberg":false,"visibleSize":"0","cancelAfter":0,"channel":"API","remark":null,"tags":null,"cancelExist":false,"tradeType":"TRADE","inOrderBook":true,"cancelledSize":"0","cancelledFunds":"0","remainSize":"0.3","remainFunds":"15000","tax":"0","active":true,"createdAt":1729235902748,"lastUpdatedAt":1729235909862}
            """));

        rig.Routes.On("GET", "/api/v1/hf/fills", KucoinPayloads.Fills);

        await rig.Client.QueryOrderAsync(
            new QueryOrder(rig.Kernel.Services.TraderId, Rig.Strategy, rig.Instrument.Id, new ClientOrderId("O-open-1"), null, null, Guid.NewGuid(), TestKernel.Now),
            CancellationToken.None).WaitAsync(Wait.Timeout);

        Assert.Single(rig.Server.RequestsTo("/api/v1/hf/orders/client-order/O-open-1"));

        // And that order's fills, by the id the venue gave it.
        Assert.Equal("67120bbef094e200070976f6", Assert.Single(rig.Server.RequestsTo("/api/v1/hf/fills")).Query("orderId"));
    }

    [Fact]
    public async Task Open_orders_are_read_symbol_by_symbol_and_include_the_stop_orders()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        rig.Routes.On("GET", "/api/v1/hf/orders/active/symbols", KucoinPayloads.Envelope("{\"symbols\":[\"BTC-USDT\"]}"));
        rig.Routes.On("GET", "/api/v1/hf/orders/active", KucoinPayloads.ActiveOrders);
        rig.Routes.On("GET", "/api/v1/stop-order", KucoinPayloads.StopOrders);

        IReadOnlyList<OrderStatusReport> reports = await rig.Client.GenerateOrderStatusReportsAsync(null, null, null, openOnly: true, CancellationToken.None).WaitAsync(Wait.Timeout);

        Assert.Equal("BTC-USDT", Assert.Single(rig.Server.RequestsTo("/api/v1/hf/orders/active")).Query("symbol"));
        OrderStatusReport open = Assert.Single(reports, r => r.ClientOrderId == new ClientOrderId("O-open-1"));
        Assert.Equal((OrderType.Limit, OrderSide.Buy, OrderStatus.PartiallyFilled, TimeInForce.Gtc), (open.OrderType, open.OrderSide, open.OrderStatus, open.TimeInForce));
        Assert.Equal((new Quantity(0.5m, 8), new Quantity(0.2m, 8), new Price(50_000m, 1)), (open.Quantity, open.FilledQuantity, open.Price));
        // 9,990 of quote for 0.2 of base.
        Assert.Equal(49_950m, open.AvgPx);
        Assert.True(open.PostOnly);
        Assert.Equal(1_729_235_902_748_000_000L, open.TsAccepted.Value);

        OrderStatusReport stop = Assert.Single(reports, r => r.ClientOrderId == new ClientOrderId("O-stop-1"));
        Assert.Equal((OrderType.StopLimit, OrderSide.Sell, OrderStatus.Accepted), (stop.OrderType, stop.OrderSide, stop.OrderStatus));
        Assert.Equal((new Price(49_000m, 1), new Price(48_900m, 1), new VenueOrderId("vs93gptvr9t2fsql003l8k5p")), (stop.TriggerPrice, stop.Price, stop.VenueOrderId));
    }

    [Fact]
    public async Task Fill_reports_carry_the_venues_own_fee()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        rig.Routes.On("GET", "/api/v1/hf/fills", KucoinPayloads.Fills);

        IReadOnlyList<FillReport> fills = await rig.Client.GenerateFillReportsAsync(rig.Instrument.Id, null, null, null, CancellationToken.None).WaitAsync(Wait.Timeout);

        FillReport fill = Assert.Single(fills);
        Assert.Equal((new VenueOrderId("6717422bd51c29000775ea03"), new TradeId("11029373945659392"), OrderSide.Buy, LiquiditySide.Taker), (fill.VenueOrderId, fill.TradeId, fill.OrderSide, fill.LiquiditySide));
        Assert.Equal((new Quantity(0.00001m, 8), new Price(67_717.6m, 1), new Money(0.000677176m, Currencies.USDT)), (fill.LastQty, fill.LastPx, fill.Commission));
        Assert.Equal(1_729_577_515_473_000_000L, fill.TsEvent.Value);
        Assert.Equal("BTC-USDT", Assert.Single(rig.Server.RequestsTo("/api/v1/hf/fills")).Query("symbol"));
    }

    [Fact]
    public async Task A_spot_account_reports_no_positions()
    {
        await using Rig rig = await new Rig().ConnectAsync();

        Assert.Empty(await rig.Client.GeneratePositionStatusReportsAsync(null, null, null, CancellationToken.None));
    }

    [Fact]
    public void Missing_credentials_are_named_by_their_environment_variable_and_never_echoed()
    {
        using TestKernel kernel = new();
        foreach (string name in new[] { KucoinVenue.EnvApiKey, KucoinVenue.EnvApiSecret, KucoinVenue.EnvApiPassphrase })
        {
            Assert.Null(Environment.GetEnvironmentVariable(name));
        }

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => new KucoinExecutionClient(new ClientId("KUCOIN"), new KucoinExecutionClientConfig { ApiKey = "k", ApiSecret = "very-secret" }, kernel.Services));

        Assert.Contains(KucoinVenue.EnvApiPassphrase, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("very-secret", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("very-secret", new KucoinCredentials("k", "very-secret", "p", "3").ToString(), StringComparison.Ordinal);
    }
}
