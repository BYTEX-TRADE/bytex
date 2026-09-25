using System.Text.Json;
using Bytex.Adapters.Bitget;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Model.Reports;
using Bytex.Core.Trading;

namespace Bytex.Adapters.Tests.Bitget;

// Why: none of this could be measured. Every private endpoint on this venue checks the key before anything else -
// sending only an ACCESS-KEY header turns the refusal from 40006 "Invalid ACCESS_KEY" into 40037 "Apikey does not
// exist", and nothing gets past that without a real key - so what a signed request looks like, and what the venue
// sends back, is the venue's published interface and not something this adapter has seen work.
//
// What CAN be held to is the request this client builds. These tests are that: the body of an order on each of the
// three families, which of them carries a product type and a margin coin, what a spot amend does that a derivative
// amend does not, and the two shapes the leverage takes because the venue holds it per SIDE in isolated margin. A
// wrong field name here would be refused by the venue with a message; a wrong SHAPE - a leverage set on one side
// only, a spot market buy sized in the wrong currency - would be accepted and be a different order from the one the
// strategy asked for, which is what these are here to prevent.
//
// The routes were confirmed to exist against the live venue: every path below answers 40006 without a key and a path
// that does not exist answers 40404 "Request URL NOT FOUND", so none of them is a guess.
public sealed class BitgetExecutionClientTests
{
    private const string Key = "bg-test-key";
    private const string Secret = "bg-test-secret";
    private const string Passphrase = "bg-test-passphrase";
    private const string Broker = "bytex-channel";

    private const string SpotPlaceOrder = "/api/v2/spot/trade/place-order";
    private const string SpotCancelOrder = "/api/v2/spot/trade/cancel-order";
    private const string SpotCancelReplace = "/api/v2/spot/trade/cancel-replace-order";
    private const string SpotCancelSymbol = "/api/v2/spot/trade/cancel-symbol-order";
    private const string SpotAssets = "/api/v2/spot/account/assets";
    private const string SpotFills = "/api/v2/spot/trade/fills";
    private const string SpotOpenOrders = "/api/v2/spot/trade/unfilled-orders";

    private const string FuturesPlaceOrder = "/api/v2/mix/order/place-order";
    private const string FuturesModifyOrder = "/api/v2/mix/order/modify-order";
    private const string FuturesCancelOrder = "/api/v2/mix/order/cancel-order";
    private const string FuturesCancelAll = "/api/v2/mix/order/cancel-all-orders";
    private const string FuturesAccounts = "/api/v2/mix/account/accounts";
    private const string FuturesPositions = "/api/v2/mix/position/all-position";
    private const string FuturesFills = "/api/v2/mix/order/fills";

    private static readonly StrategyId _strategy = new("Probe-001");

    private sealed class Rig : IAsyncDisposable
    {
        private WsSession? _session;

        public Rig(
            BitgetProductType type = BitgetProductType.Spot,
            decimal? leverage = null,
            BitgetMarginMode marginMode = BitgetMarginMode.Crossed,
            string? brokerId = null)
        {
            Routes = new Routes()
                .On("GET", BitgetInstrumentProvider.SpotSymbolsPath, BitgetPayloads.SpotSymbols)
                .On("GET", BitgetInstrumentProvider.ContractsPath, BitgetPayloads.UsdtContracts)
                .On("GET", BitgetInstrumentProvider.PositionTiersPath, r => StubResponse.Json(r.Query("symbol") == "BTCUSDT"
                    ? BitgetPayloads.BtcUsdtPositionTiers
                    : BitgetPayloads.Error("40034", "Parameter does not exist")))
                .On("GET", SpotAssets, BitgetPayloads.Envelope("""
                    [{"coin":"USDT","available":"1000","frozen":"10","locked":"0","limitAvailable":"0","uTime":"1790358274804"}]
                    """))
                .On("GET", FuturesAccounts, BitgetPayloads.Envelope("""
                    [{"marginCoin":"USDT","available":"900","frozen":"0","locked":"0","accountEquity":"1000","usdtEquity":"1000","unrealizedPL":"0"}]
                    """))
                .On("POST", SpotPlaceOrder, BitgetPayloads.Envelope("""{"orderId":"1","clientOid":"c-1"}"""))
                .On("POST", SpotCancelOrder, BitgetPayloads.Envelope("""{"orderId":"1","clientOid":"c-1"}"""))
                .On("POST", SpotCancelReplace, BitgetPayloads.Envelope("""{"orderId":"2","clientOid":"c-1-R","success":"success"}"""))
                .On("POST", SpotCancelSymbol, BitgetPayloads.Envelope("""{"symbol":"BTCUSDT"}"""))
                .On("POST", FuturesPlaceOrder, BitgetPayloads.Envelope("""{"orderId":"1","clientOid":"c-1"}"""))
                .On("POST", FuturesModifyOrder, BitgetPayloads.Envelope("""{"orderId":"1","clientOid":"c-1"}"""))
                .On("POST", FuturesCancelOrder, BitgetPayloads.Envelope("""{"orderId":"1","clientOid":"c-1"}"""))
                .On("POST", FuturesCancelAll, BitgetPayloads.Envelope("""{"successList":[],"failureList":[]}"""))
                .On("POST", BitgetVenue.LeveragePath, BitgetPayloads.Envelope("""{"symbol":"BTCUSDT","marginCoin":"USDT","longLeverage":"3","shortLeverage":"3","crossMarginLeverage":"3","marginMode":"crossed"}"""));

            Server = new LoopbackServer(r => Routes.Handle(r));
            Kernel = new TestKernel();
            Client = new BitgetExecutionClient(new ClientId("BITGET"), new BitgetExecutionClientConfig
            {
                ProductType = type,
                MarginMode = marginMode,
                Leverage = leverage,
                BrokerId = brokerId,
                ApiKey = Key,
                ApiSecret = Secret,
                ApiPassphrase = Passphrase,
                BaseUrlHttp = Server.HttpBase,
                BaseUrlWs = Server.WsBase,
                InstrumentProvider = new InstrumentProviderConfig { LoadAll = true },
            }, Kernel.Services);

            Client.AttachSink(Sink);
            Instrument = type == BitgetProductType.Spot
                ? InstrumentId.Parse("BTCUSDT.BITGET")
                : InstrumentId.Parse("BTCUSDT-PERP.BITGET");
        }

        public Routes Routes { get; }

        public LoopbackServer Server { get; }

        public TestKernel Kernel { get; }

        public BitgetExecutionClient Client { get; }

        public RecordingExecutionSink Sink { get; } = new();

        public InstrumentId Instrument { get; }

        public OrderFactory Orders { get; private set; } = null!;

        public WsSession Session => _session ?? throw new InvalidOperationException("not connected");

        public async Task<Rig> ConnectAsync()
        {
            await Client.ConnectAsync(CancellationToken.None).WaitAsync(Wait.Timeout);
            _session = await Server.NextSessionAsync();
            Orders = new OrderFactory(Kernel.Services.TraderId, _strategy, Kernel.Clock);
            return this;
        }

        public Instrument Definition => Client.Instruments.Find(Instrument)!;

        public Quantity Qty(decimal value) => Definition.MakeQuantity(value);

        public Price At(decimal value) => Definition.MakePrice(value);

        public async Task SubmitAsync(Order order)
        {
            Kernel.Kernel.Cache.AddOrder(order);
            await Client.SubmitOrderAsync(
                new SubmitOrder(Kernel.Services.TraderId, _strategy, order, null, null, null, Guid.NewGuid(), TestKernel.Now),
                CancellationToken.None).WaitAsync(Wait.Timeout);
        }

        public JsonDocument LastBody(string path) => JsonDocument.Parse(Server.RequestsTo(path).Last().Body);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            Kernel.Dispose();
            await Server.DisposeAsync();
        }
    }

    // ----- signing, which is the same on every request -----

    [Fact]
    public async Task Every_signed_request_carries_the_four_headers_the_venue_names()
    {
        // Only ACCESS-KEY could be confirmed against the live venue, by watching the refusal change when it was sent.
        // The other three and the string that is signed are the venue's published interface, so what this asserts is
        // that they are all present and that the passphrase travels as itself - this venue does not want it signed
        // with the secret the way KuCoin does.
        await using Rig rig = await new Rig().ConnectAsync();

        RecordedRequest signed = rig.Server.RequestsTo(SpotAssets).Last();

        Assert.Equal(Key, signed.Header("ACCESS-KEY"));
        Assert.Equal(Passphrase, signed.Header("ACCESS-PASSPHRASE"));
        Assert.NotNull(signed.Header("ACCESS-SIGN"));
        Assert.NotNull(signed.Header("ACCESS-TIMESTAMP"));

        // And the language its own error messages come back in, because those reach a person through a report.
        Assert.Equal("en-US", signed.Header("locale"));
    }

    [Fact]
    public void A_client_without_a_passphrase_fails_naming_the_variable_to_set()
    {
        // A Bitget key is three parts. Two of them plus a right key and secret is a configuration that looks complete
        // and cannot sign anything, so it is refused when the client is built rather than at the first request.
        using TestKernel kernel = new();

        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => new BitgetExecutionClient(
            new ClientId("BITGET"),
            new BitgetExecutionClientConfig { ApiKey = Key, ApiSecret = Secret, BaseUrlHttp = "http://127.0.0.1:9" },
            kernel.Services));

        Assert.Contains(BitgetVenue.EnvApiPassphrase, refused.Message, StringComparison.Ordinal);
    }

    // ----- orders -----

    [Fact]
    public async Task A_spot_limit_order_carries_the_venues_own_words_and_nothing_a_derivative_needs()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        LimitOrder order = rig.Orders.Limit(rig.Instrument, OrderSide.Buy, rig.Qty(0.01m), rig.At(50000m));

        await rig.SubmitAsync(order);

        using JsonDocument body = rig.LastBody(SpotPlaceOrder);
        Assert.Equal("BTCUSDT", body.RootElement.GetProperty("symbol").GetString());
        Assert.Equal("buy", body.RootElement.GetProperty("side").GetString());
        Assert.Equal("limit", body.RootElement.GetProperty("orderType").GetString());
        Assert.Equal("gtc", body.RootElement.GetProperty("force").GetString());
        Assert.Equal("0.010000", body.RootElement.GetProperty("size").GetString());
        Assert.Equal("50000.00", body.RootElement.GetProperty("price").GetString());
        Assert.Equal(order.ClientOrderId.Value, body.RootElement.GetProperty("clientOid").GetString());

        // Spot has no product type, no margin mode and no margin coin, and the spot endpoints refuse a parameter they
        // do not know rather than ignoring it.
        Assert.False(body.RootElement.TryGetProperty("productType", out _));
        Assert.False(body.RootElement.TryGetProperty("marginMode", out _));
        Assert.False(body.RootElement.TryGetProperty("marginCoin", out _));

        Assert.IsType<OrderSubmitted>(await rig.Sink.NextOrderEventAsync());
    }

    [Fact]
    public async Task A_derivative_order_carries_the_product_type_the_margin_mode_and_the_margin_coin()
    {
        // All three are required by the endpoint. The margin coin is the contract's own settlement currency, which
        // the venue publishes as a list on the contract rather than as a field.
        await using Rig rig = await new Rig(BitgetProductType.UsdtFutures).ConnectAsync();
        LimitOrder order = rig.Orders.Limit(rig.Instrument, OrderSide.Sell, rig.Qty(0.01m), rig.At(50000m));

        await rig.SubmitAsync(order);

        using JsonDocument body = rig.LastBody(FuturesPlaceOrder);
        Assert.Equal("USDT-FUTURES", body.RootElement.GetProperty("productType").GetString());
        Assert.Equal("crossed", body.RootElement.GetProperty("marginMode").GetString());
        Assert.Equal("USDT", body.RootElement.GetProperty("marginCoin").GetString());
        Assert.Equal("sell", body.RootElement.GetProperty("side").GetString());
    }

    [Fact]
    public async Task An_isolated_order_says_so_because_the_venue_takes_the_mode_per_order()
    {
        await using Rig rig = await new Rig(BitgetProductType.UsdtFutures, marginMode: BitgetMarginMode.Isolated).ConnectAsync();

        await rig.SubmitAsync(rig.Orders.Limit(rig.Instrument, OrderSide.Buy, rig.Qty(0.01m), rig.At(50000m)));

        using JsonDocument body = rig.LastBody(FuturesPlaceOrder);
        Assert.Equal("isolated", body.RootElement.GetProperty("marginMode").GetString());
    }

    [Fact]
    public async Task A_reduce_only_order_says_so_only_where_the_venue_has_positions()
    {
        await using Rig rig = await new Rig(BitgetProductType.UsdtFutures).ConnectAsync();
        MarketOrder order = rig.Orders.Market(rig.Instrument, OrderSide.Sell, rig.Qty(0.01m), reduceOnly: true);

        await rig.SubmitAsync(order);

        using JsonDocument body = rig.LastBody(FuturesPlaceOrder);
        Assert.Equal("YES", body.RootElement.GetProperty("reduceOnly").GetString());
    }

    [Theory]
    [InlineData(TimeInForce.Gtc, false, "gtc")]
    [InlineData(TimeInForce.Ioc, false, "ioc")]
    [InlineData(TimeInForce.Fok, false, "fok")]
    [InlineData(TimeInForce.Gtc, true, "post_only")]
    public async Task Post_only_is_one_of_the_venues_time_in_force_values_rather_than_a_flag_beside_them(TimeInForce tif, bool postOnly, string expected)
    {
        await using Rig rig = await new Rig().ConnectAsync();
        LimitOrder order = rig.Orders.Limit(rig.Instrument, OrderSide.Buy, rig.Qty(0.01m), rig.At(50000m), timeInForce: tif, postOnly: postOnly);

        await rig.SubmitAsync(order);

        using JsonDocument body = rig.LastBody(SpotPlaceOrder);
        Assert.Equal(expected, body.RootElement.GetProperty("force").GetString());
    }

    [Fact]
    public async Task A_spot_market_buy_is_refused_unless_it_carries_a_quote_quantity()
    {
        // The venue sizes a spot market BUY in the QUOTE currency - the same shape Binance has - and offers no field
        // that would take a base amount instead. Converting one would need a price this client does not have and
        // would fill a different size than was asked for, so it is refused and named.
        await using Rig rig = await new Rig().ConnectAsync();

        await rig.SubmitAsync(rig.Orders.Market(rig.Instrument, OrderSide.Buy, rig.Qty(0.01m)));

        OrderRejected rejected = Assert.IsType<OrderRejected>(await rig.Sink.NextOrderEventAsync());
        Assert.Contains("quote quantity", rejected.Reason, StringComparison.Ordinal);
        Assert.Empty(rig.Server.RequestsTo(SpotPlaceOrder));
    }

    [Fact]
    public async Task A_spot_market_sell_is_sized_in_base_currency_as_everywhere_else()
    {
        await using Rig rig = await new Rig().ConnectAsync();

        await rig.SubmitAsync(rig.Orders.Market(rig.Instrument, OrderSide.Sell, rig.Qty(0.01m)));

        using JsonDocument body = rig.LastBody(SpotPlaceOrder);
        Assert.Equal("market", body.RootElement.GetProperty("orderType").GetString());
        Assert.Equal("0.010000", body.RootElement.GetProperty("size").GetString());
    }

    [Fact]
    public async Task A_quote_quantity_is_refused_where_the_venue_would_ignore_it()
    {
        // A quote quantity means something on exactly one order on this venue. Anywhere else the venue would read the
        // number as base currency and fill an order tens of thousands of times the intended size.
        await using Rig rig = await new Rig(BitgetProductType.UsdtFutures).ConnectAsync();

        await rig.SubmitAsync(rig.Orders.Market(rig.Instrument, OrderSide.Buy, rig.Qty(100m), quoteQuantity: true));

        OrderRejected rejected = Assert.IsType<OrderRejected>(await rig.Sink.NextOrderEventAsync());
        Assert.Contains("quote quantity", rejected.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_trigger_order_is_refused_and_says_what_this_client_does_send()
    {
        // The venue takes a trigger order through a separate family of endpoints - its plan orders - with their own
        // cancel, their own amend and their own stream channel. None of that could be checked against a real account,
        // and a stop that is placed and cannot be cancelled is worse than one that was never accepted.
        await using Rig rig = await new Rig(BitgetProductType.UsdtFutures).ConnectAsync();

        await rig.SubmitAsync(rig.Orders.StopMarket(rig.Instrument, OrderSide.Sell, rig.Qty(0.01m), rig.At(45000m)));

        OrderRejected rejected = Assert.IsType<OrderRejected>(await rig.Sink.NextOrderEventAsync());
        Assert.Contains("market and limit orders", rejected.Reason, StringComparison.Ordinal);
        Assert.Empty(rig.Server.RequestsTo(FuturesPlaceOrder));
    }

    [Fact]
    public async Task An_instrument_this_client_does_not_hold_is_refused_before_anything_is_sent()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        Instrument other = rig.Definition;
        MarketOrder order = rig.Orders.Market(InstrumentId.Parse("NOTACOINUSDT.BITGET"), OrderSide.Sell, other.MakeQuantity(1m));

        await rig.SubmitAsync(order);

        OrderRejected rejected = Assert.IsType<OrderRejected>(await rig.Sink.NextOrderEventAsync());
        Assert.Contains("unknown to the Bitget", rejected.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refusal_from_the_venue_becomes_the_rejection_a_strategy_reads()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        rig.Routes.On("POST", SpotPlaceOrder, _ => new StubResponse(400, BitgetPayloads.Error("43012", "Insufficient balance")));

        await rig.SubmitAsync(rig.Orders.Limit(rig.Instrument, OrderSide.Buy, rig.Qty(0.01m), rig.At(50000m)));

        Assert.IsType<OrderSubmitted>(await rig.Sink.NextOrderEventAsync());
        OrderRejected rejected = Assert.IsType<OrderRejected>(await rig.Sink.NextOrderEventAsync());

        // The venue's own words, because the venue is the only thing that knows why.
        Assert.Equal("Insufficient balance", rejected.Reason);
    }

    // ----- amending, which the two markets do differently -----

    [Fact]
    public async Task A_derivative_order_is_amended_in_place()
    {
        await using Rig rig = await new Rig(BitgetProductType.UsdtFutures).ConnectAsync();
        LimitOrder order = rig.Orders.Limit(rig.Instrument, OrderSide.Buy, rig.Qty(0.01m), rig.At(50000m));
        await rig.SubmitAsync(order);
        Assert.IsType<OrderSubmitted>(await rig.Sink.NextOrderEventAsync());

        await rig.Client.ModifyOrderAsync(
            new ModifyOrder(rig.Kernel.Services.TraderId, _strategy, rig.Instrument, order.ClientOrderId, null, rig.Qty(0.02m), rig.At(49000m), null, null, Guid.NewGuid(), TestKernel.Now),
            CancellationToken.None).WaitAsync(Wait.Timeout);

        using JsonDocument body = rig.LastBody(FuturesModifyOrder);
        Assert.Equal("USDT-FUTURES", body.RootElement.GetProperty("productType").GetString());
        Assert.Equal("0.0200", body.RootElement.GetProperty("newSize").GetString());
        Assert.Equal("49000.0", body.RootElement.GetProperty("newPrice").GetString());

        // The order keeps its identity: the id it was placed under is the id it is amended under.
        Assert.Equal(order.ClientOrderId.Value, body.RootElement.GetProperty("clientOid").GetString());
        Assert.Equal(order.ClientOrderId.Value, body.RootElement.GetProperty("newClientOid").GetString());
    }

    [Fact]
    public async Task A_spot_order_is_amended_by_the_venues_own_cancel_and_replace()
    {
        // Spot has no amend. The venue's cancel-replace takes the cancellation and the replacement together, so there
        // is no window in which neither order exists - which is what a strategy resizing a protective order needs.
        await using Rig rig = await new Rig().ConnectAsync();
        LimitOrder order = rig.Orders.Limit(rig.Instrument, OrderSide.Buy, rig.Qty(0.01m), rig.At(50000m));
        await rig.SubmitAsync(order);
        Assert.IsType<OrderSubmitted>(await rig.Sink.NextOrderEventAsync());

        await rig.Client.ModifyOrderAsync(
            new ModifyOrder(rig.Kernel.Services.TraderId, _strategy, rig.Instrument, order.ClientOrderId, null, rig.Qty(0.02m), rig.At(49000m), null, null, Guid.NewGuid(), TestKernel.Now),
            CancellationToken.None).WaitAsync(Wait.Timeout);

        using JsonDocument body = rig.LastBody(SpotCancelReplace);
        Assert.Equal(order.ClientOrderId.Value, body.RootElement.GetProperty("clientOid").GetString());

        // The replacement is a new order at the venue and the venue refuses one that reuses the id being cancelled,
        // so the engine's id carries a marker and everything that later names the order translates it back.
        Assert.Equal(order.ClientOrderId.Value + BitgetExecutionClient.ReplacementSuffix, body.RootElement.GetProperty("newClientOid").GetString());
        Assert.Equal("0.020000", body.RootElement.GetProperty("size").GetString());
        Assert.Equal("49000.00", body.RootElement.GetProperty("price").GetString());
    }

    [Fact]
    public async Task An_amend_the_venue_refuses_is_reported_as_a_modify_rejection()
    {
        await using Rig rig = await new Rig(BitgetProductType.UsdtFutures).ConnectAsync();
        rig.Routes.On("POST", FuturesModifyOrder, _ => new StubResponse(400, BitgetPayloads.Error("43025", "Plan order does not exist")));
        LimitOrder order = rig.Orders.Limit(rig.Instrument, OrderSide.Buy, rig.Qty(0.01m), rig.At(50000m));
        await rig.SubmitAsync(order);
        Assert.IsType<OrderSubmitted>(await rig.Sink.NextOrderEventAsync());

        await rig.Client.ModifyOrderAsync(
            new ModifyOrder(rig.Kernel.Services.TraderId, _strategy, rig.Instrument, order.ClientOrderId, null, rig.Qty(0.02m), null, null, null, Guid.NewGuid(), TestKernel.Now),
            CancellationToken.None).WaitAsync(Wait.Timeout);

        Assert.IsType<OrderPendingUpdate>(await rig.Sink.NextOrderEventAsync());
        OrderModifyRejected rejected = Assert.IsType<OrderModifyRejected>(await rig.Sink.NextOrderEventAsync());
        Assert.Equal("Plan order does not exist", rejected.Reason);
    }

    // ----- cancelling -----

    [Fact]
    public async Task A_cancel_names_the_order_by_the_id_the_engine_gave_it()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        LimitOrder order = rig.Orders.Limit(rig.Instrument, OrderSide.Buy, rig.Qty(0.01m), rig.At(50000m));
        await rig.SubmitAsync(order);
        Assert.IsType<OrderSubmitted>(await rig.Sink.NextOrderEventAsync());

        await rig.Client.CancelOrderAsync(
            new CancelOrder(rig.Kernel.Services.TraderId, _strategy, rig.Instrument, order.ClientOrderId, null, null, Guid.NewGuid(), TestKernel.Now),
            CancellationToken.None).WaitAsync(Wait.Timeout);

        using JsonDocument body = rig.LastBody(SpotCancelOrder);
        Assert.Equal(order.ClientOrderId.Value, body.RootElement.GetProperty("clientOid").GetString());
        Assert.Equal("BTCUSDT", body.RootElement.GetProperty("symbol").GetString());
        Assert.IsType<OrderPendingCancel>(await rig.Sink.NextOrderEventAsync());
    }

    [Fact]
    public async Task A_derivative_cancel_carries_the_product_type_and_the_margin_coin()
    {
        await using Rig rig = await new Rig(BitgetProductType.UsdtFutures).ConnectAsync();

        await rig.Client.CancelOrderAsync(
            new CancelOrder(rig.Kernel.Services.TraderId, _strategy, rig.Instrument, new ClientOrderId("O-1"), null, null, Guid.NewGuid(), TestKernel.Now),
            CancellationToken.None).WaitAsync(Wait.Timeout);

        using JsonDocument body = rig.LastBody(FuturesCancelOrder);
        Assert.Equal("USDT-FUTURES", body.RootElement.GetProperty("productType").GetString());
        Assert.Equal("USDT", body.RootElement.GetProperty("marginCoin").GetString());
    }

    [Fact]
    public async Task Cancelling_everything_on_an_instrument_is_one_request_per_market()
    {
        await using Rig spot = await new Rig().ConnectAsync();
        await spot.Client.CancelAllOrdersAsync(
            new CancelAllOrders(spot.Kernel.Services.TraderId, _strategy, spot.Instrument, null, null, Guid.NewGuid(), TestKernel.Now),
            CancellationToken.None).WaitAsync(Wait.Timeout);

        Assert.Equal("BTCUSDT", spot.LastBody(SpotCancelSymbol).RootElement.GetProperty("symbol").GetString());

        await using Rig futures = await new Rig(BitgetProductType.UsdtFutures).ConnectAsync();
        await futures.Client.CancelAllOrdersAsync(
            new CancelAllOrders(futures.Kernel.Services.TraderId, _strategy, futures.Instrument, null, null, Guid.NewGuid(), TestKernel.Now),
            CancellationToken.None).WaitAsync(Wait.Timeout);

        using JsonDocument body = futures.LastBody(FuturesCancelAll);
        Assert.Equal("USDT-FUTURES", body.RootElement.GetProperty("productType").GetString());
        Assert.Equal("USDT", body.RootElement.GetProperty("marginCoin").GetString());
    }

    // ----- leverage, which this venue holds per side -----

    [Fact]
    public async Task Nothing_is_set_when_no_leverage_is_configured()
    {
        // Null is what every configuration written before the field existed means, and it has to keep meaning it: a
        // field nobody set must not start changing accounts.
        await using Rig rig = await new Rig(BitgetProductType.UsdtFutures).ConnectAsync();

        Assert.Empty(rig.Server.RequestsTo(BitgetVenue.LeveragePath));
    }

    [Fact]
    public async Task Crossed_margin_takes_one_figure_for_a_contract_and_no_side()
    {
        // Crossed margin holds one leverage per contract, so the side is left out - and that is the mode in which the
        // single figure this configuration carries means exactly one thing.
        await using Rig rig = await new Rig(BitgetProductType.UsdtFutures, leverage: 3).ConnectAsync();

        List<RecordedRequest> set = [.. rig.Server.RequestsTo(BitgetVenue.LeveragePath)];
        Assert.Equal(3, set.Count);

        foreach (RecordedRequest request in set)
        {
            using JsonDocument body = JsonDocument.Parse(request.Body);
            Assert.Equal("3", body.RootElement.GetProperty("leverage").GetString());
            Assert.Equal("USDT-FUTURES", body.RootElement.GetProperty("productType").GetString());
            Assert.Equal("USDT", body.RootElement.GetProperty("marginCoin").GetString());
            Assert.False(body.RootElement.TryGetProperty("holdSide", out _));
        }
    }

    [Fact]
    public async Task Isolated_margin_is_set_on_both_sides_because_it_holds_one_figure_per_side()
    {
        // The venue's set-leverage takes a holdSide, and isolated margin holds a figure per side. The engine's
        // configuration carries ONE figure, so both sides are set to it - which means asymmetric leverage cannot be
        // expressed here at all, and saying so is better than a second field nothing else in the engine has.
        await using Rig rig = await new Rig(BitgetProductType.UsdtFutures, leverage: 5, marginMode: BitgetMarginMode.Isolated).ConnectAsync();

        List<RecordedRequest> set = [.. rig.Server.RequestsTo(BitgetVenue.LeveragePath)];
        Assert.Equal(6, set.Count);

        string[] sides = [.. set.Select(r => JsonDocument.Parse(r.Body).RootElement.GetProperty("holdSide").GetString()!)];
        Assert.Equal(3, sides.Count(s => s == "long"));
        Assert.Equal(3, sides.Count(s => s == "short"));
    }

    [Fact]
    public async Task Spot_has_no_leverage_to_set()
    {
        await using Rig rig = await new Rig(leverage: 3).ConnectAsync();

        Assert.Empty(rig.Server.RequestsTo(BitgetVenue.LeveragePath));
    }

    [Fact]
    public async Task A_fractional_leverage_is_sent_as_written()
    {
        // A strategy document carries leverage as a decimal, so 2.5 is reachable. The venue is entitled to accept or
        // refuse it and says so for itself; what must not happen is the number being rounded on the way here, because
        // that silently changes the size of every position from the one that was backtested.
        await using Rig rig = await new Rig(BitgetProductType.UsdtFutures, leverage: 2.5m).ConnectAsync();

        using JsonDocument body = JsonDocument.Parse(rig.Server.RequestsTo(BitgetVenue.LeveragePath)[0].Body);
        Assert.Equal("2.5", body.RootElement.GetProperty("leverage").GetString());
    }

    [Fact]
    public async Task A_venue_refusing_the_leverage_does_not_stop_the_node()
    {
        // Usually the venue saying the account is not entitled to the figure asked for, which a node cannot fix.
        await using Rig rig = new(BitgetProductType.UsdtFutures, leverage: 200);
        rig.Routes.On("POST", BitgetVenue.LeveragePath, _ => new StubResponse(400, BitgetPayloads.Error("40808", "Parameter verification exception leverage")));

        await rig.ConnectAsync();

        Assert.True(rig.Client.IsConnected, "a refused leverage must not stop the node from starting");
        Assert.NotEmpty(rig.Server.RequestsTo(BitgetVenue.LeveragePath));
    }

    // ----- the broker id -----

    [Fact]
    public async Task A_broker_id_travels_in_a_header_and_leaves_the_order_alone()
    {
        await using Rig rig = await new Rig(brokerId: Broker).ConnectAsync();
        LimitOrder order = rig.Orders.Limit(rig.Instrument, OrderSide.Buy, rig.Qty(0.01m), rig.At(50000m));

        await rig.SubmitAsync(order);

        RecordedRequest sent = rig.Server.RequestsTo(SpotPlaceOrder).Last();
        Assert.Equal(Broker, sent.Header(BitgetVenue.BrokerIdHeader));

        // Nowhere in the order, so nothing about the order's identity changed - which is why this venue's mechanism
        // needs no reconciliation test where a prefix on the client order id would.
        Assert.DoesNotContain(Broker, sent.Body, StringComparison.Ordinal);

        using JsonDocument body = JsonDocument.Parse(sent.Body);
        Assert.Equal(order.ClientOrderId.Value, body.RootElement.GetProperty("clientOid").GetString());
    }

    [Fact]
    public async Task No_such_header_is_sent_when_no_broker_id_is_configured()
    {
        // Untagged must stay byte-for-byte what the venue received before the field existed, because almost nobody
        // will configure one.
        await using Rig rig = await new Rig().ConnectAsync();

        await rig.SubmitAsync(rig.Orders.Limit(rig.Instrument, OrderSide.Buy, rig.Qty(0.01m), rig.At(50000m)));

        Assert.Null(rig.Server.RequestsTo(SpotPlaceOrder).Last().Header(BitgetVenue.BrokerIdHeader));
    }

    // ----- the private stream -----

    [Fact]
    public async Task The_stream_is_logged_in_to_before_anything_is_subscribed()
    {
        // Measured: the private socket accepts a connection without credentials and refuses every subscription on it
        // with code 30004 "User not logged in". So the login goes first, and the channels are only asked for once the
        // venue has answered it.
        await using Rig rig = await new Rig().ConnectAsync();

        using JsonDocument login = JsonDocument.Parse(await rig.Session.ReceiveTextAsync());
        Assert.Equal("login", login.RootElement.GetProperty("op").GetString());

        JsonElement argument = login.RootElement.GetProperty("args")[0];
        Assert.Equal(Key, argument.GetProperty("apiKey").GetString());
        Assert.Equal(Passphrase, argument.GetProperty("passphrase").GetString());
        Assert.NotNull(argument.GetProperty("sign").GetString());

        // In SECONDS here, where every REST request signs a timestamp in milliseconds. The venue's own inconsistency.
        Assert.True(long.Parse(argument.GetProperty("timestamp").GetString()!, System.Globalization.CultureInfo.InvariantCulture) < 100_000_000_000L);
    }

    [Fact]
    public async Task The_channels_are_subscribed_once_the_login_is_answered()
    {
        await using Rig rig = await new Rig(BitgetProductType.UsdtFutures).ConnectAsync();
        await rig.Session.ReceiveTextAsync();

        await rig.Session.SendTextAsync("""{"event":"login","code":"0","msg":"login success"}""");

        using JsonDocument subscribe = JsonDocument.Parse(await rig.Session.ReceiveTextAsync());
        Assert.Equal("subscribe", subscribe.RootElement.GetProperty("op").GetString());

        string[] channels = [.. subscribe.RootElement.GetProperty("args").EnumerateArray().Select(a => a.GetProperty("channel").GetString()!).Order(StringComparer.Ordinal)];

        // Positions only on the derivative families, because a spot account holds balances and nothing else.
        Assert.Equal(["account", "orders", "positions"], channels);
        Assert.All(
            subscribe.RootElement.GetProperty("args").EnumerateArray(),
            a => Assert.Equal("USDT-FUTURES", a.GetProperty("instType").GetString()));
    }

    [Fact]
    public async Task A_spot_account_subscribes_to_no_positions()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        await rig.Session.ReceiveTextAsync();

        await rig.Session.SendTextAsync("""{"event":"login","code":"0","msg":"login success"}""");

        using JsonDocument subscribe = JsonDocument.Parse(await rig.Session.ReceiveTextAsync());
        string[] channels = [.. subscribe.RootElement.GetProperty("args").EnumerateArray().Select(a => a.GetProperty("channel").GetString()!).Order(StringComparer.Ordinal)];

        Assert.Equal(["account", "orders"], channels);
    }

    [Fact]
    public async Task An_order_the_venue_has_accepted_becomes_an_acceptance()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        LimitOrder order = rig.Orders.Limit(rig.Instrument, OrderSide.Buy, rig.Qty(0.01m), rig.At(50000m));
        await rig.SubmitAsync(order);
        Assert.IsType<OrderSubmitted>(await rig.Sink.NextOrderEventAsync());

        await rig.Session.SendTextAsync($$"""
            {"action":"snapshot","arg":{"instType":"SPOT","channel":"orders","instId":"default"},
             "data":[{"instId":"BTCUSDT","orderId":"1","clientOid":"{{order.ClientOrderId.Value}}","price":"50000",
                      "size":"0.01","orderType":"limit","force":"gtc","side":"buy","status":"live",
                      "cTime":"1790358274804","uTime":"1790358274900"}],"ts":1790358274900}
            """);

        OrderAccepted accepted = await rig.Sink.NextOrderEventAsync<OrderAccepted>();
        Assert.Equal(order.ClientOrderId, accepted.ClientOrderId);
        Assert.Equal("1", accepted.VenueOrderId!.Value.Value);
    }

    [Fact]
    public async Task A_fill_arrives_on_the_orders_channel_with_the_venues_own_fee()
    {
        // Fills are taken from the orders channel rather than the venue's separate fill channel: one channel carrying
        // the whole of an order's life cannot be missing while orders arrive at all, where a second channel that
        // quietly delivered nothing would lose every fill and open no position.
        await using Rig rig = await new Rig().ConnectAsync();
        LimitOrder order = rig.Orders.Limit(rig.Instrument, OrderSide.Buy, rig.Qty(0.01m), rig.At(50000m));
        await rig.SubmitAsync(order);
        Assert.IsType<OrderSubmitted>(await rig.Sink.NextOrderEventAsync());

        await rig.Session.SendTextAsync(Fill(order.ClientOrderId.Value, "t-1", "0.004"));

        OrderFilled filled = await rig.Sink.NextOrderEventAsync<OrderFilled>();
        Assert.Equal(0.004m, filled.LastQty.Value);
        Assert.Equal(50000m, filled.LastPx.Value);
        Assert.Equal(LiquiditySide.Maker, filled.LiquiditySide);

        // The fee the venue reported, turned positive: a commission is a cost everywhere above the adapter and the
        // venue states it as a negative number.
        Assert.Equal(0.02m, filled.Commission.Amount);
    }

    [Fact]
    public async Task The_same_fill_twice_is_booked_once()
    {
        // The venue repeats an order's state on a reconnection and on a subscription, so without the trade id a
        // resubscribe would double every position.
        await using Rig rig = await new Rig().ConnectAsync();
        LimitOrder order = rig.Orders.Limit(rig.Instrument, OrderSide.Buy, rig.Qty(0.01m), rig.At(50000m));
        await rig.SubmitAsync(order);
        Assert.IsType<OrderSubmitted>(await rig.Sink.NextOrderEventAsync());

        await rig.Session.SendTextAsync(Fill(order.ClientOrderId.Value, "t-1", "0.004"));
        await rig.Sink.NextOrderEventAsync<OrderFilled>();

        await rig.Session.SendTextAsync(Fill(order.ClientOrderId.Value, "t-1", "0.004"));
        await rig.Session.SendTextAsync(Fill(order.ClientOrderId.Value, "t-2", "0.006"));

        OrderFilled second = await rig.Sink.NextOrderEventAsync<OrderFilled>();
        Assert.Equal(0.006m, second.LastQty.Value);
        Assert.Equal(2, rig.Sink.AllOrderEvents.Count(e => e is OrderFilled));
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("canceled")]
    public async Task A_cancellation_is_read_whichever_way_the_venue_spells_it(string status)
    {
        // The venue spells this with one L on one market and two on the other. Reading only one of the two would
        // leave a cancelled order live in the engine for ever.
        await using Rig rig = await new Rig().ConnectAsync();
        LimitOrder order = rig.Orders.Limit(rig.Instrument, OrderSide.Buy, rig.Qty(0.01m), rig.At(50000m));
        await rig.SubmitAsync(order);
        Assert.IsType<OrderSubmitted>(await rig.Sink.NextOrderEventAsync());

        await rig.Session.SendTextAsync($$"""
            {"action":"snapshot","arg":{"instType":"SPOT","channel":"orders","instId":"default"},
             "data":[{"instId":"BTCUSDT","orderId":"1","clientOid":"{{order.ClientOrderId.Value}}","price":"50000",
                      "size":"0.01","orderType":"limit","force":"gtc","side":"buy","status":"{{status}}",
                      "cTime":"1790358274804","uTime":"1790358274900"}],"ts":1790358274900}
            """);

        await rig.Sink.NextOrderEventAsync<OrderCanceled>();
    }

    private static string Fill(string clientOid, string tradeId, string size) => $$"""
        {"action":"snapshot","arg":{"instType":"SPOT","channel":"orders","instId":"default"},
         "data":[{"instId":"BTCUSDT","orderId":"1","clientOid":"{{clientOid}}","price":"50000","size":"0.01",
                  "orderType":"limit","force":"gtc","side":"buy","status":"partially_filled","fillPrice":"50000",
                  "tradeId":"{{tradeId}}","baseVolume":"{{size}}","fillFee":"-0.02","fillFeeCoin":"USDT",
                  "tradeScope":"maker","cTime":"1790358274804","uTime":"1790358274900"}],"ts":1790358274900}
        """;

    [Fact]
    public async Task A_state_change_carrying_no_increment_is_not_booked_as_a_fill()
    {
        // The venue closes an order that filled in an earlier push by sending it again with the final status and no
        // fill beside it. Booking that would move the position twice.
        await using Rig rig = await new Rig().ConnectAsync();
        LimitOrder order = rig.Orders.Limit(rig.Instrument, OrderSide.Buy, rig.Qty(0.01m), rig.At(50000m));
        await rig.SubmitAsync(order);
        Assert.IsType<OrderSubmitted>(await rig.Sink.NextOrderEventAsync());

        await rig.Session.SendTextAsync($$"""
            {"action":"snapshot","arg":{"instType":"SPOT","channel":"orders","instId":"default"},
             "data":[{"instId":"BTCUSDT","orderId":"1","clientOid":"{{order.ClientOrderId.Value}}","price":"50000",
                      "size":"0.01","orderType":"limit","force":"gtc","side":"buy","status":"filled",
                      "cTime":"1790358274804","uTime":"1790358274900"}],"ts":1790358274900}
            """);

        await rig.Session.SendTextAsync(Fill(order.ClientOrderId.Value, "t-9", "0.01"));

        // The only fill is the one that carried a trade.
        OrderFilled filled = await rig.Sink.NextOrderEventAsync<OrderFilled>();
        Assert.Equal("t-9", filled.TradeId.Value);
    }

    // ----- balances and reports -----

    [Fact]
    public async Task A_spot_balance_is_what_is_free_plus_what_is_held()
    {
        await using Rig rig = await new Rig().ConnectAsync();

        AccountState state = await rig.Sink.NextAccountStateAsync();
        AccountBalance balance = Assert.Single(state.Balances);

        Assert.Equal(1010m, balance.Total.Amount);
        Assert.Equal(1000m, balance.Free.Amount);
        Assert.Equal(10m, balance.Locked.Amount);
    }

    [Fact]
    public async Task A_derivative_balance_is_the_equity_the_venue_reports()
    {
        // The derivative account reports an equity that already includes what is locked behind positions, where the
        // spot account reports what is free and what is frozen separately. Adding the derivative fields together
        // would count the collateral twice.
        await using Rig rig = await new Rig(BitgetProductType.UsdtFutures).ConnectAsync();

        AccountState state = await rig.Sink.NextAccountStateAsync();
        AccountBalance balance = Assert.Single(state.Balances);

        Assert.Equal(1000m, balance.Total.Amount);
        Assert.Equal(900m, balance.Free.Amount);
    }

    [Fact]
    public async Task Open_orders_come_back_as_reports_from_the_bare_array_spot_answers_with()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        rig.Routes.On("GET", SpotOpenOrders, BitgetPayloads.Envelope("""
            [{"symbol":"BTCUSDT","orderId":"1","clientOid":"c-1","price":"50000","size":"0.01","orderType":"limit",
              "side":"buy","status":"live","priceAvg":"0","baseVolume":"0","force":"post_only",
              "cTime":"1790358274804","uTime":"1790358274900"}]
            """));

        OrderStatusReport report = Assert.Single(await rig.Client.GenerateOrderStatusReportsAsync(null, null, null, openOnly: true, CancellationToken.None));

        Assert.Equal(InstrumentId.Parse("BTCUSDT.BITGET"), report.InstrumentId);
        Assert.Equal("c-1", report.ClientOrderId!.Value.Value);
        Assert.Equal(OrderStatus.Accepted, report.OrderStatus);
        Assert.Equal(OrderType.Limit, report.OrderType);
        Assert.True(report.PostOnly);
    }

    [Fact]
    public async Task A_replaced_spot_order_is_reported_under_the_engines_own_id()
    {
        // The venue knows it by the id this client gave the replacement; the engine knows it by its own. Without
        // translating it back, reconciliation would see an order nothing in the engine owns.
        await using Rig rig = await new Rig().ConnectAsync();
        rig.Routes.On("GET", SpotOpenOrders, BitgetPayloads.Envelope($$"""
            [{"symbol":"BTCUSDT","orderId":"2","clientOid":"c-1{{BitgetExecutionClient.ReplacementSuffix}}","price":"49000",
              "size":"0.02","orderType":"limit","side":"buy","status":"live","priceAvg":"0","baseVolume":"0",
              "force":"gtc","cTime":"1790358274804","uTime":"1790358274900"}]
            """));

        OrderStatusReport report = Assert.Single(await rig.Client.GenerateOrderStatusReportsAsync(null, null, null, openOnly: true, CancellationToken.None));

        Assert.Equal("c-1", report.ClientOrderId!.Value.Value);
    }

    [Fact]
    public async Task Fills_are_read_from_both_shapes_the_venue_puts_a_fee_in()
    {
        // Spot puts the fee in a feeDetail OBJECT and the derivatives in a feeDetail ARRAY, and both report it as a
        // negative number. One reader for both, and a commission that is a cost rather than a credit.
        await using Rig spot = await new Rig().ConnectAsync();
        spot.Routes.On("GET", SpotFills, BitgetPayloads.Envelope("""
            [{"symbol":"BTCUSDT","orderId":"1","tradeId":"t-1","orderType":"limit","side":"buy","priceAvg":"50000",
              "size":"0.01","amount":"500","tradeScope":"maker","cTime":"1790358274804","uTime":"1790358274900",
              "feeDetail":{"deduction":"no","feeCoin":"USDT","totalDeductionFee":"0","totalFee":"-1.00"}}]
            """));

        FillReport fromSpot = Assert.Single(await spot.Client.GenerateFillReportsAsync(null, null, null, null, CancellationToken.None));
        Assert.Equal(1.00m, fromSpot.Commission.Amount);
        Assert.Equal(LiquiditySide.Maker, fromSpot.LiquiditySide);
        Assert.Equal(0.01m, fromSpot.LastQty.Value);

        await using Rig futures = await new Rig(BitgetProductType.UsdtFutures).ConnectAsync();
        futures.Routes.On("GET", FuturesFills, BitgetPayloads.Envelope("""
            {"fillList":[{"tradeId":"t-2","symbol":"BTCUSDT","orderId":"1","price":"50000","baseVolume":"0.01",
              "side":"sell","quoteVolume":"500","tradeScope":"taker","cTime":"1790358274804",
              "feeDetail":[{"feeCoin":"USDT","fee":"-0.30"}]}],"endId":"1"}
            """));

        FillReport fromFutures = Assert.Single(await futures.Client.GenerateFillReportsAsync(null, null, null, null, CancellationToken.None));
        Assert.Equal(0.30m, fromFutures.Commission.Amount);
        Assert.Equal(LiquiditySide.Taker, fromFutures.LiquiditySide);
    }

    [Fact]
    public async Task Positions_come_back_with_the_side_the_venue_names_rather_than_a_sign()
    {
        await using Rig rig = await new Rig(BitgetProductType.UsdtFutures).ConnectAsync();
        rig.Routes.On("GET", FuturesPositions, BitgetPayloads.Envelope("""
            [{"symbol":"BTCUSDT","marginCoin":"USDT","holdSide":"short","total":"0.05","available":"0.05",
              "openPriceAvg":"50000","marginMode":"crossed","uTime":"1790358274900"},
             {"symbol":"ETHUSDT","marginCoin":"USDT","holdSide":"long","total":"0","available":"0",
              "openPriceAvg":"0","marginMode":"crossed","uTime":"1790358274900"}]
            """));

        IReadOnlyList<PositionStatusReport> reports = await rig.Client.GeneratePositionStatusReportsAsync(null, null, null, CancellationToken.None);

        Assert.Equal(2, reports.Count);
        Assert.Equal(PositionSide.Short, reports[0].PositionSide);
        Assert.Equal(0.05m, reports[0].Quantity.Value);
        Assert.Equal(50000m, reports[0].AvgPxOpen);

        // Flat is still a position the venue lists, and reporting it keeps reconciliation able to close one this node
        // thinks is open.
        Assert.Equal(PositionSide.Flat, reports[1].PositionSide);
    }

    [Fact]
    public async Task A_spot_account_reports_no_positions_because_it_holds_none()
    {
        await using Rig rig = await new Rig().ConnectAsync();

        Assert.Empty(await rig.Client.GeneratePositionStatusReportsAsync(null, null, null, CancellationToken.None));
        Assert.Empty(rig.Server.RequestsTo(FuturesPositions));
    }

    [Fact]
    public async Task A_derivative_report_is_asked_for_with_the_product_type()
    {
        await using Rig rig = await new Rig(BitgetProductType.UsdtFutures).ConnectAsync();
        rig.Routes.On("GET", FuturesPositions, BitgetPayloads.Envelope("[]"));

        await rig.Client.GeneratePositionStatusReportsAsync(null, null, null, CancellationToken.None);

        Assert.Equal("USDT-FUTURES", Assert.Single(rig.Server.RequestsTo(FuturesPositions)).Query("productType"));
    }
}
