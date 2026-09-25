using System.Text.Json;
using Bytex.Adapters.Okx;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Reports;
using Bytex.Core.Trading;

namespace Bytex.Adapters.Tests.Okx;

// Why: this client trades three markets through one set of endpoints, and what changes between them is what an order
// has to CARRY. Three of those things are this venue's own and none of the other venues here has them:
//
//   - a trade mode on every order, which the venue demands and has no default for
//   - a size in CONTRACTS on a derivative and in base currency on spot
//   - a statement, on spot, of which currency the size is in, because the venue's own default for a market order is
//     the QUOTE currency - so a market buy of 0.01 without it spends 0.01 USDT rather than buying 0.01 bitcoin
//
// And one thing that refuses an order before it is sent: the venue accepts a client order id of letters and digits
// only, and the engine's own ids carry hyphens unless a strategy says otherwise. Rewriting the id would place the
// order under a name reconciliation could not match it by - the same trap the broker-id prefix on another venue had
// to be built carefully around - so it is refused with the setting that fixes it named.
//
// What could NOT be measured is stated rather than implied: every endpoint below needs a key, so the private shapes
// are the venue's specification and the fixtures say so.
public sealed class OkxExecutionClientTests
{
    private const string InstrumentsPath = "/api/v5/public/instruments";
    private const string TiersPath = "/api/v5/public/position-tiers";
    private const string OrderPath = "/api/v5/trade/order";
    private const string CancelPath = "/api/v5/trade/cancel-order";
    private const string CancelBatchPath = "/api/v5/trade/cancel-batch-orders";
    private const string PendingPath = "/api/v5/trade/orders-pending";
    private const string FillsPath = "/api/v5/trade/fills";
    private const string BalancePath = "/api/v5/account/balance";
    private const string PositionsPath = "/api/v5/account/positions";

    private const string Key = "d5ea2e30-5ba5-4c3d-a0f0-000000000000";
    private const string Secret = "8A4C3F2E1D0B9A8C7E6F5D4C3B2A1908";
    private const string Passphrase = "bytex-test";

    /// <summary>One BTC-USDT-SWAP contract, so three bitcoin is 300 contracts.</summary>
    private const decimal Contract = 0.01m;

    private static readonly InstrumentId _pair = InstrumentId.Parse("BTC-USDT.OKX");
    private static readonly InstrumentId _perpetual = InstrumentId.Parse("BTC-USDT-SWAP.OKX");
    private static readonly StrategyId _strategy = new("Probe-001");

    private sealed class Rig : IAsyncDisposable
    {
        public Rig(OkxInstrumentType type = OkxInstrumentType.Spot, decimal? leverage = null, OkxMarginMode margin = OkxMarginMode.Cross, bool demo = false, bool useHyphens = false)
        {
            Routes = new Routes()
                .On("GET", InstrumentsPath, r => StubResponse.Json(r.Query("instType") switch
                {
                    "SWAP" => OkxPayloads.SwapInstruments,
                    "FUTURES" => OkxPayloads.FuturesInstruments,
                    _ => OkxPayloads.SpotInstruments,
                }))
                .On("GET", TiersPath, r => StubResponse.Json(r.Query("instType") == "FUTURES" ? OkxPayloads.FuturesTiers : OkxPayloads.SwapTiers))
                .On("GET", BalancePath, OkxPayloads.AccountBalance)
                .On("POST", OkxVenue.SetLeveragePath, OkxPayloads.Envelope("""[{"lever":"3","mgnMode":"cross","instId":"BTC-USDT-SWAP","posSide":"net"}]"""));

            Server = new LoopbackServer(Routes.Handle);
            Kernel = new TestKernel();
            Client = new OkxExecutionClient(new ClientId("OKX"), new OkxExecutionClientConfig
            {
                InstrumentType = type,
                MarginMode = margin,
                Leverage = leverage,
                DemoTrading = demo,
                ApiKey = Key,
                ApiSecret = Secret,
                ApiPassphrase = Passphrase,
                BaseUrlHttp = Server.HttpBase,
                BaseUrlWs = Server.WsBase,
                InstrumentProvider = new InstrumentProviderConfig { LoadAll = true },
            }, Kernel.Services);

            Client.AttachSink(Sink);
            Orders = new OrderFactory(Kernel.Services.TraderId, _strategy, Kernel.Clock, useHyphens: useHyphens);
        }

        public Routes Routes { get; }

        public LoopbackServer Server { get; }

        public TestKernel Kernel { get; }

        public OkxExecutionClient Client { get; }

        public RecordingExecutionSink Sink { get; } = new();

        public OrderFactory Orders { get; }

        public WsSession Session { get; private set; } = null!;

        public async Task ConnectAsync()
        {
            await Client.ConnectAsync(CancellationToken.None).WaitAsync(Wait.Timeout);
            Session = await Server.NextSessionAsync();
        }

        /// <summary>Connects and answers the login, which is what the private subscriptions wait for.</summary>
        public async Task LoggedInAsync()
        {
            await ConnectAsync();
            await Session.ReceiveTextAsync();
            await Session.SendTextAsync(OkxPayloads.LoginAck);
        }

        public Instrument Instrument(InstrumentId id) => Client.Instruments.Find(id)!;

        public async Task SubmitAsync(Order order)
        {
            Kernel.Kernel.Cache.AddOrder(order);
            await Client.SubmitOrderAsync(
                new SubmitOrder(Kernel.Services.TraderId, _strategy, order, null, null, null, Guid.NewGuid(), TestKernel.Now),
                CancellationToken.None).WaitAsync(Wait.Timeout);
        }

        public RecordedRequest LastOrder() => Server.RequestsTo(OrderPath).Last(r => r.Method == "POST");

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Server.DisposeAsync();
            Kernel.Dispose();
        }
    }

    private static JsonElement Body(RecordedRequest request)
    {
        using JsonDocument doc = JsonDocument.Parse(request.Body);
        return doc.RootElement.Clone();
    }

    // ----- signing -----

    [Fact]
    public async Task Every_signed_request_carries_the_venues_four_headers()
    {
        // Four, where the other venues here send two or three, and the timestamp is ISO 8601 to the millisecond
        // rather than the Unix milliseconds every other venue signs - so a shared timestamp helper would produce a
        // signature this venue rejects.
        await using Rig rig = new();
        await rig.ConnectAsync();

        RecordedRequest balance = Assert.Single(rig.Server.RequestsTo(BalancePath));

        Assert.Equal(Key, balance.Header(OkxVenue.HeaderApiKey));
        Assert.NotNull(balance.Header(OkxVenue.HeaderSign));

        // The passphrase travels in clear here, unlike KuCoin's, which signs it with the secret.
        Assert.Equal(Passphrase, balance.Header(OkxVenue.HeaderPassphrase));

        string timestamp = balance.Header(OkxVenue.HeaderTimestamp)!;
        Assert.EndsWith("Z", timestamp, StringComparison.Ordinal);
        Assert.True(DateTimeOffset.TryParse(timestamp, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out _));
    }

    [Fact]
    public async Task No_demo_header_is_sent_unless_the_demo_account_was_asked_for()
    {
        await using Rig live = new();
        await live.ConnectAsync();
        Assert.Null(Assert.Single(live.Server.RequestsTo(BalancePath)).Header(OkxVenue.SimulatedTradingHeader));

        await using Rig demo = new(demo: true);
        await demo.ConnectAsync();
        Assert.Equal(OkxVenue.SimulatedTradingOn, Assert.Single(demo.Server.RequestsTo(BalancePath)).Header(OkxVenue.SimulatedTradingHeader));
    }

    [Fact]
    public async Task The_demo_header_is_on_public_requests_too()
    {
        // A node reading the demo account's catalog and trading the real one would be a node whose instruments and
        // whose fills came from two different places, so the header is not limited to signed requests.
        await using Rig rig = new(demo: true);
        await rig.ConnectAsync();

        Assert.All(
            rig.Server.RequestsTo(InstrumentsPath),
            r => Assert.Equal(OkxVenue.SimulatedTradingOn, r.Header(OkxVenue.SimulatedTradingHeader)));
    }

    [Fact]
    public void A_client_without_all_three_parts_of_a_key_fails_naming_the_variable_to_set()
    {
        using TestKernel kernel = new();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => new OkxExecutionClient(
            new ClientId("OKX"),
            new OkxExecutionClientConfig { ApiKey = "k", ApiSecret = "s", BaseUrlHttp = "http://127.0.0.1:9" },
            kernel.Services));

        Assert.Contains("OKX_API_PASSPHRASE", error.Message, StringComparison.Ordinal);
    }

    // ----- the private socket -----

    [Fact]
    public async Task The_private_subscriptions_wait_for_the_login_to_be_answered()
    {
        // The venue refuses a subscription on an unauthenticated socket, and it answers a login asynchronously - so
        // subscribing at connect would have every subscription refused, on the first connection and on every
        // reconnection after it.
        await using Rig rig = new(OkxInstrumentType.Swap);
        await rig.ConnectAsync();

        using JsonDocument login = JsonDocument.Parse(await rig.Session.ReceiveTextAsync());
        Assert.Equal("login", login.RootElement.GetProperty("op").GetString());

        // The signature is over Unix SECONDS here, not the ISO timestamp a REST request signs: one venue, two
        // timestamp formats.
        JsonElement args = login.RootElement.GetProperty("args")[0];
        Assert.Equal(Key, args.GetProperty("apiKey").GetString());
        Assert.Equal(Passphrase, args.GetProperty("passphrase").GetString());
        Assert.Matches("^[0-9]+$", args.GetProperty("timestamp").GetString()!);

        await rig.Session.SendTextAsync(OkxPayloads.LoginAck);

        List<string> channels = [];
        for (int i = 0; i < 3; i++)
        {
            using JsonDocument message = JsonDocument.Parse(await rig.Session.ReceiveTextAsync());
            channels.Add(message.RootElement.GetProperty("args")[0].GetProperty("channel").GetString()!);
        }

        Assert.Equal(["account", "orders", "positions"], channels.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task A_login_the_venue_refuses_leaves_the_client_taking_messages()
    {
        // Measured against the live socket with a made-up key: {"event":"error","msg":"Invalid apiKey","code":"60005"}.
        // An error event carries no arg and no data, so a handler reaching for them would throw on every refusal.
        await using Rig rig = new();
        await rig.ConnectAsync();
        await rig.Session.ReceiveTextAsync();

        await rig.Session.SendTextAsync(OkxPayloads.LoginRefused);
        await rig.Session.SendTextAsync(OkxVenue.PongMessage);

        Assert.True(rig.Client.IsConnected);
    }

    // ----- what an order carries -----

    [Fact]
    public async Task A_spot_order_says_it_is_a_cash_trade_and_that_its_size_is_in_the_base_currency()
    {
        // The two fields that make a spot order mean what the engine meant. Without tgtCcy, the venue's own default
        // for a market order is the QUOTE currency - so this order would spend 0.01 USDT instead of buying 0.01
        // bitcoin, and both are perfectly good orders.
        await using Rig rig = new();
        await rig.LoggedInAsync();
        rig.Routes.On("POST", OrderPath, r => StubResponse.Json(OkxPayloads.OrderPlaced(Body(r).GetProperty("clOrdId").GetString()!)));

        MarketOrder order = rig.Orders.Market(_pair, OrderSide.Buy, rig.Instrument(_pair).MakeQuantity(0.01m));
        await rig.SubmitAsync(order);

        JsonElement body = Body(rig.LastOrder());
        Assert.Equal("BTC-USDT", body.GetProperty("instId").GetString());
        Assert.Equal("cash", body.GetProperty("tdMode").GetString());
        Assert.Equal("base_ccy", body.GetProperty("tgtCcy").GetString());
        Assert.Equal("buy", body.GetProperty("side").GetString());
        Assert.Equal("market", body.GetProperty("ordType").GetString());
        Assert.Equal("0.01", body.GetProperty("sz").GetString());
    }

    [Fact]
    public async Task A_spot_order_for_a_quote_quantity_says_the_size_is_in_the_quote_currency()
    {
        // The venue takes either, so a caller that really meant "spend this much" is honoured rather than refused.
        await using Rig rig = new();
        await rig.LoggedInAsync();
        rig.Routes.On("POST", OrderPath, r => StubResponse.Json(OkxPayloads.OrderPlaced(Body(r).GetProperty("clOrdId").GetString()!)));

        MarketOrder order = rig.Orders.Market(_pair, OrderSide.Buy, rig.Instrument(_pair).MakeQuantity(0.01m), quoteQuantity: true);
        await rig.SubmitAsync(order);

        Assert.Equal("quote_ccy", Body(rig.LastOrder()).GetProperty("tgtCcy").GetString());
    }

    [Fact]
    public async Task A_derivative_order_is_sized_in_contracts_and_carries_the_configured_margin_mode()
    {
        // Three bitcoin is 300 contracts of a 0.01 BTC contract. Sending three would be a hundredth of the position
        // asked for, and the venue would accept it.
        await using Rig rig = new(OkxInstrumentType.Swap, margin: OkxMarginMode.Isolated);
        await rig.LoggedInAsync();
        rig.Routes.On("POST", OrderPath, r => StubResponse.Json(OkxPayloads.OrderPlaced(Body(r).GetProperty("clOrdId").GetString()!)));

        MarketOrder order = rig.Orders.Market(_perpetual, OrderSide.Buy, rig.Instrument(_perpetual).MakeQuantity(3m));
        await rig.SubmitAsync(order);

        JsonElement body = Body(rig.LastOrder());
        Assert.Equal("BTC-USDT-SWAP", body.GetProperty("instId").GetString());
        Assert.Equal("isolated", body.GetProperty("tdMode").GetString());
        Assert.Equal("300", body.GetProperty("sz").GetString());

        // No tgtCcy: it is a spot-only field, and no posSide, which is what this venue's net mode means.
        Assert.False(body.TryGetProperty("tgtCcy", out _));
        Assert.False(body.TryGetProperty("posSide", out _));
    }

    [Fact]
    public async Task A_derivative_order_for_a_quote_quantity_is_refused()
    {
        await using Rig rig = new(OkxInstrumentType.Swap);
        await rig.LoggedInAsync();

        MarketOrder order = rig.Orders.Market(_perpetual, OrderSide.Buy, rig.Instrument(_perpetual).MakeQuantity(3m), quoteQuantity: true);
        await rig.SubmitAsync(order);

        OrderRejected rejected = await rig.Sink.NextOrderEventAsync<OrderRejected>();
        Assert.Contains("contracts", rejected.Reason, StringComparison.Ordinal);
        Assert.Empty(rig.Server.RequestsTo(OrderPath));
    }

    [Theory]
    [InlineData(TimeInForce.Gtc, false, "limit")]
    [InlineData(TimeInForce.Gtc, true, "post_only")]
    [InlineData(TimeInForce.Ioc, false, "ioc")]
    [InlineData(TimeInForce.Fok, false, "fok")]
    public async Task A_limit_orders_time_in_force_travels_inside_the_order_type(TimeInForce tif, bool postOnly, string ordType)
    {
        // This venue carries the time in force IN the order type rather than in a field of its own, which is why
        // post-only and fill-or-kill are spellings of "limit" here rather than flags beside it.
        await using Rig rig = new();
        await rig.LoggedInAsync();
        rig.Routes.On("POST", OrderPath, r => StubResponse.Json(OkxPayloads.OrderPlaced(Body(r).GetProperty("clOrdId").GetString()!)));

        LimitOrder order = rig.Orders.Limit(
            _pair,
            OrderSide.Buy,
            rig.Instrument(_pair).MakeQuantity(0.01m),
            rig.Instrument(_pair).MakePrice(100m),
            timeInForce: tif,
            postOnly: postOnly);

        await rig.SubmitAsync(order);

        JsonElement body = Body(rig.LastOrder());
        Assert.Equal(ordType, body.GetProperty("ordType").GetString());
        Assert.Equal("100", body.GetProperty("px").GetString());
    }

    [Fact]
    public async Task A_reduce_only_order_says_so()
    {
        await using Rig rig = new(OkxInstrumentType.Swap);
        await rig.LoggedInAsync();
        rig.Routes.On("POST", OrderPath, r => StubResponse.Json(OkxPayloads.OrderPlaced(Body(r).GetProperty("clOrdId").GetString()!)));

        MarketOrder order = rig.Orders.Market(_perpetual, OrderSide.Sell, rig.Instrument(_perpetual).MakeQuantity(3m), reduceOnly: true);
        await rig.SubmitAsync(order);

        Assert.True(Body(rig.LastOrder()).GetProperty("reduceOnly").GetBoolean());
    }

    // ----- what an order is refused for -----

    [Fact]
    public async Task An_order_id_this_venue_cannot_accept_is_refused_with_the_setting_that_fixes_it()
    {
        // The engine's default id is O-20231114-221320-TESTER-Probe-1, and this venue takes letters and digits only.
        // Refused rather than rewritten: an order the venue was given under a different name is an order
        // reconciliation can no longer find, and a position that opened under it would never be matched to a
        // strategy.
        await using Rig rig = new(useHyphens: true);
        await rig.LoggedInAsync();

        MarketOrder order = rig.Orders.Market(_pair, OrderSide.Buy, rig.Instrument(_pair).MakeQuantity(0.01m));
        await rig.SubmitAsync(order);

        OrderRejected rejected = await rig.Sink.NextOrderEventAsync<OrderRejected>();
        Assert.Contains("letters and digits", rejected.Reason, StringComparison.Ordinal);
        Assert.Contains("UseHyphensInClientOrderIds", rejected.Reason, StringComparison.Ordinal);

        // And nothing was sent, so the venue never saw an order it would have refused.
        Assert.Empty(rig.Server.RequestsTo(OrderPath));
    }

    [Fact]
    public async Task A_triggered_order_is_refused_because_the_venue_keeps_those_somewhere_else()
    {
        // This venue holds every triggered order in a separate algo-order system with its own endpoint, its own ids
        // and its own stream channel, which this adapter does not speak. Saying so is the honest answer; sending a
        // trigger price on a plain order would have it accepted as a working order with no trigger at all.
        await using Rig rig = new(OkxInstrumentType.Swap);
        await rig.LoggedInAsync();

        StopMarketOrder order = rig.Orders.StopMarket(
            _perpetual,
            OrderSide.Sell,
            rig.Instrument(_perpetual).MakeQuantity(3m),
            rig.Instrument(_perpetual).MakePrice(90m));

        await rig.SubmitAsync(order);

        OrderRejected rejected = await rig.Sink.NextOrderEventAsync<OrderRejected>();
        Assert.Contains("algo-order", rejected.Reason, StringComparison.Ordinal);
        Assert.Empty(rig.Server.RequestsTo(OrderPath));
    }

    [Fact]
    public async Task An_order_the_venue_refuses_inside_a_successful_envelope_is_reported_as_refused()
    {
        // The trap this venue sets: a refused order comes back with HTTP 200 and an envelope code of 0, and the
        // per-order verdict is in sCode. A client reading only the envelope would report every refused order as
        // accepted and wait for a fill that never comes.
        await using Rig rig = new();
        await rig.LoggedInAsync();
        rig.Routes.On("POST", OrderPath, r => StubResponse.Json(
            OkxPayloads.OrderRefused(Body(r).GetProperty("clOrdId").GetString()!, "51008", "Order placement failed due to insufficient balance")));

        MarketOrder order = rig.Orders.Market(_pair, OrderSide.Buy, rig.Instrument(_pair).MakeQuantity(0.01m));
        await rig.SubmitAsync(order);

        Assert.IsType<OrderSubmitted>(await rig.Sink.NextOrderEventAsync());
        OrderRejected rejected = await rig.Sink.NextOrderEventAsync<OrderRejected>();
        Assert.Contains("insufficient balance", rejected.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_order_for_an_instrument_this_client_does_not_know_is_refused()
    {
        await using Rig rig = new();
        await rig.LoggedInAsync();

        Instrument known = rig.Instrument(_pair);
        MarketOrder order = rig.Orders.Market(InstrumentId.Parse("NOTACOIN-USDT.OKX"), OrderSide.Buy, known.MakeQuantity(0.01m));
        await rig.SubmitAsync(order);

        OrderRejected rejected = await rig.Sink.NextOrderEventAsync<OrderRejected>();
        Assert.Contains("unknown", rejected.Reason, StringComparison.Ordinal);
    }

    // ----- amending -----

    [Fact]
    public async Task An_order_can_be_amended_which_is_what_all_three_families_declare()
    {
        // Worth naming because the market next to this one in the engine - KuCoin's perpetuals - cannot amend at
        // all, so a strategy that resizes a protective order behaves differently on the two.
        await using Rig rig = new();
        await rig.LoggedInAsync();
        rig.Routes
            .On("POST", OrderPath, r => StubResponse.Json(OkxPayloads.OrderPlaced(Body(r).GetProperty("clOrdId").GetString()!)))
            .On("POST", OkxVenue.AmendOrderPath, r => StubResponse.Json(OkxPayloads.Amended(Body(r).GetProperty("clOrdId").GetString()!)));

        LimitOrder order = rig.Orders.Limit(_pair, OrderSide.Buy, rig.Instrument(_pair).MakeQuantity(0.01m), rig.Instrument(_pair).MakePrice(100m));
        await rig.SubmitAsync(order);

        await rig.Client.ModifyOrderAsync(
            new ModifyOrder(
                rig.Kernel.Services.TraderId,
                _strategy,
                _pair,
                order.ClientOrderId,
                null,
                rig.Instrument(_pair).MakeQuantity(0.02m),
                rig.Instrument(_pair).MakePrice(99m),
                null,
                null,
                Guid.NewGuid(),
                TestKernel.Now),
            CancellationToken.None).WaitAsync(Wait.Timeout);

        JsonElement body = Body(Assert.Single(rig.Server.RequestsTo(OkxVenue.AmendOrderPath)));
        Assert.Equal("BTC-USDT", body.GetProperty("instId").GetString());
        Assert.Equal(order.ClientOrderId.Value, body.GetProperty("clOrdId").GetString());
        Assert.Equal("0.02", body.GetProperty("newSz").GetString());
        Assert.Equal("99", body.GetProperty("newPx").GetString());
    }

    [Fact]
    public async Task A_derivative_amendment_restates_the_size_in_contracts()
    {
        await using Rig rig = new(OkxInstrumentType.Swap);
        await rig.LoggedInAsync();
        rig.Routes.On("POST", OkxVenue.AmendOrderPath, r => StubResponse.Json(OkxPayloads.Amended(Body(r).GetProperty("clOrdId").GetString()!)));

        await rig.Client.ModifyOrderAsync(
            new ModifyOrder(
                rig.Kernel.Services.TraderId,
                _strategy,
                _perpetual,
                new ClientOrderId("O1"),
                null,
                rig.Instrument(_perpetual).MakeQuantity(3m),
                null,
                null,
                null,
                Guid.NewGuid(),
                TestKernel.Now),
            CancellationToken.None).WaitAsync(Wait.Timeout);

        Assert.Equal("300", Body(Assert.Single(rig.Server.RequestsTo(OkxVenue.AmendOrderPath))).GetProperty("newSz").GetString());
    }

    [Fact]
    public async Task An_amendment_that_changes_nothing_is_refused_here_rather_than_at_the_venue()
    {
        await using Rig rig = new();
        await rig.LoggedInAsync();

        await rig.Client.ModifyOrderAsync(
            new ModifyOrder(rig.Kernel.Services.TraderId, _strategy, _pair, new ClientOrderId("O1"), null, null, null, null, null, Guid.NewGuid(), TestKernel.Now),
            CancellationToken.None).WaitAsync(Wait.Timeout);

        OrderModifyRejected rejected = await rig.Sink.NextOrderEventAsync<OrderModifyRejected>();
        Assert.Contains("neither the size nor the price", rejected.Reason, StringComparison.Ordinal);
        Assert.Empty(rig.Server.RequestsTo(OkxVenue.AmendOrderPath));
    }

    // ----- leverage -----

    [Fact]
    public async Task A_configured_leverage_is_set_at_the_venue_per_instrument_before_anything_trades()
    {
        // This venue holds leverage as account state and ignores anything sent with an order, so a strategy written
        // for 3x trades at 3x only if this happens. The scope is the instrument and the margin mode - not the
        // currency, which is the venue's cross-margin scope for its margin-trading product, and no position side,
        // which it only takes in isolated long/short mode.
        await using Rig rig = new(OkxInstrumentType.Swap, leverage: 3m);
        await rig.ConnectAsync();

        IReadOnlyList<RecordedRequest> set = rig.Server.RequestsTo(OkxVenue.SetLeveragePath);
        Assert.Equal(2, set.Count);

        JsonElement body = Body(set[0]);
        Assert.Equal("3", body.GetProperty("lever").GetString());
        Assert.Equal("cross", body.GetProperty("mgnMode").GetString());
        Assert.False(body.TryGetProperty("posSide", out _));
        Assert.False(body.TryGetProperty("ccy", out _));
    }

    [Fact]
    public async Task Nothing_is_set_when_no_leverage_is_configured()
    {
        // Null is what every configuration written before the field existed means, and it has to keep meaning it: a
        // field nobody set must not start changing accounts.
        await using Rig rig = new(OkxInstrumentType.Swap);
        await rig.ConnectAsync();

        Assert.Empty(rig.Server.RequestsTo(OkxVenue.SetLeveragePath));
    }

    [Fact]
    public async Task Spot_has_no_leverage_to_set()
    {
        await using Rig rig = new(leverage: 3m);
        await rig.ConnectAsync();

        Assert.Empty(rig.Server.RequestsTo(OkxVenue.SetLeveragePath));
    }

    [Fact]
    public async Task A_fractional_leverage_is_sent_as_written()
    {
        // A strategy document carries leverage as a decimal, so 2.5 is reachable. Rounding here would silently
        // change the size of every position from the one that was backtested; the venue is entitled to accept or
        // refuse the figure and says so for itself.
        await using Rig rig = new(OkxInstrumentType.Swap, leverage: 2.5m);
        await rig.ConnectAsync();

        Assert.Equal("2.5", Body(rig.Server.RequestsTo(OkxVenue.SetLeveragePath)[0]).GetProperty("lever").GetString());
    }

    [Fact]
    public async Task A_venue_refusing_the_leverage_does_not_stop_the_node()
    {
        // Usually the venue saying the account is not entitled to the figure asked for, which a node cannot fix.
        // Abandoning a start over it would be worse than trading at a leverage somebody can read about and change.
        await using Rig rig = new(OkxInstrumentType.Swap, leverage: 125m);
        rig.Routes.On("POST", OkxVenue.SetLeveragePath, _ => StubResponse.Json(OkxPayloads.Error("51004", "Order amount exceeds current tier limit")));

        await rig.ConnectAsync();

        Assert.True(rig.Client.IsConnected);
        Assert.NotEmpty(rig.Server.RequestsTo(OkxVenue.SetLeveragePath));
    }

    // ----- the account -----

    [Fact]
    public async Task Connecting_publishes_the_unified_accounts_balances_from_one_request()
    {
        // One request for the whole account, which is the shape of this venue: spot balances and derivative margin
        // live in one account rather than one per market, so there is nothing to ask twice. The venue nests the
        // currencies one level down under `details`, which is where a reader expecting a flat array finds nothing.
        await using Rig rig = new();
        await rig.ConnectAsync();

        AccountState state = await rig.Sink.NextAccountStateAsync();

        Assert.Equal(2, state.Balances.Count);
        AccountBalance usdt = state.Balances.Single(b => b.Total.Currency.Code == "USDT");
        Assert.Equal(100000m, usdt.Total.Amount);
        Assert.Equal(10000m, usdt.Locked.Amount);
        Assert.Single(rig.Server.RequestsTo(BalancePath));
    }

    [Fact]
    public async Task An_account_message_on_the_stream_updates_the_balances()
    {
        await using Rig rig = new();
        await rig.LoggedInAsync();
        await rig.Sink.NextAccountStateAsync();

        await rig.Session.SendTextAsync("""
            {"arg":{"channel":"account"},"data":[{"uTime":"1700000001000","totalEq":"120000","details":[
              {"ccy":"USDT","eq":"120000","cashBal":"120000","availEq":"120000","availBal":"120000","frozenBal":"0"}
            ]}]}
            """);

        AccountState state = await rig.Sink.NextAccountStateAsync();
        Assert.Equal(120000m, state.Balances.Single(b => b.Total.Currency.Code == "USDT").Total.Amount);
    }

    // ----- fills on the stream -----

    [Fact]
    public async Task A_fill_on_the_stream_carries_the_fee_the_venue_charged_as_a_commission()
    {
        // The venue signs a fee it CHARGED as negative and a rebate it paid as positive, which is the opposite of
        // what a commission is. Taking the figure unchanged would report a credit on every trade and make a
        // profitable-looking strategy out of a losing one.
        await using Rig rig = new();
        await rig.LoggedInAsync();

        LimitOrder order = rig.Orders.Limit(_pair, OrderSide.Buy, rig.Instrument(_pair).MakeQuantity(1m), rig.Instrument(_pair).MakePrice(100m));
        rig.Kernel.Kernel.Cache.AddOrder(order);

        await rig.Session.SendTextAsync(OkxPayloads.OrderFilled(order.ClientOrderId.Value));

        OrderFilled fill = await rig.Sink.NextOrderEventAsync<OrderFilled>();
        Assert.Equal(1m, fill.LastQty.Value);
        Assert.Equal(100m, fill.LastPx.Value);
        Assert.Equal(0.08m, fill.Commission.Amount);
        Assert.Equal("USDT", fill.Commission.Currency.Code);
        Assert.Equal(LiquiditySide.Maker, fill.LiquiditySide);
        Assert.Equal("90957997", fill.TradeId.Value);
    }

    [Fact]
    public async Task A_derivative_fills_size_is_contracts_and_becomes_base_currency()
    {
        await using Rig rig = new(OkxInstrumentType.Swap);
        await rig.LoggedInAsync();

        LimitOrder order = rig.Orders.Limit(_perpetual, OrderSide.Buy, rig.Instrument(_perpetual).MakeQuantity(3m), rig.Instrument(_perpetual).MakePrice(100m));
        rig.Kernel.Kernel.Cache.AddOrder(order);

        await rig.Session.SendTextAsync(OkxPayloads.SwapOrderFilled(order.ClientOrderId.Value));

        OrderFilled fill = await rig.Sink.NextOrderEventAsync<OrderFilled>();
        Assert.Equal(300m * Contract, fill.LastQty.Value);
        Assert.Equal(LiquiditySide.Taker, fill.LiquiditySide);
    }

    [Fact]
    public async Task The_same_trade_arriving_twice_is_booked_once()
    {
        // A reconnection replays, and a phantom fill drifts the position away from the venue's for good.
        await using Rig rig = new();
        await rig.LoggedInAsync();

        LimitOrder order = rig.Orders.Limit(_pair, OrderSide.Buy, rig.Instrument(_pair).MakeQuantity(1m), rig.Instrument(_pair).MakePrice(100m));
        rig.Kernel.Kernel.Cache.AddOrder(order);

        await rig.Session.SendTextAsync(OkxPayloads.OrderFilled(order.ClientOrderId.Value));
        await rig.Sink.NextOrderEventAsync<OrderFilled>();

        await rig.Session.SendTextAsync(OkxPayloads.OrderFilled(order.ClientOrderId.Value));
        await rig.Session.SendTextAsync(OkxPayloads.OrderCanceled(order.ClientOrderId.Value));

        // The cancel, and no second fill between it and the first one.
        await rig.Sink.NextOrderEventAsync<OrderCanceled>();
        Assert.Single(rig.Sink.AllOrderEvents.OfType<OrderFilled>());
    }

    [Fact]
    public async Task An_order_the_venue_is_working_is_reported_as_accepted()
    {
        await using Rig rig = new();
        await rig.LoggedInAsync();

        LimitOrder order = rig.Orders.Limit(_pair, OrderSide.Buy, rig.Instrument(_pair).MakeQuantity(1m), rig.Instrument(_pair).MakePrice(100m));
        rig.Kernel.Kernel.Cache.AddOrder(order);

        await rig.Session.SendTextAsync(OkxPayloads.OrderLive(order.ClientOrderId.Value));

        OrderAccepted accepted = await rig.Sink.NextOrderEventAsync<OrderAccepted>();
        Assert.Equal("312269865356374016", accepted.VenueOrderId!.Value.Value);
    }

    [Fact]
    public async Task An_order_placed_by_something_else_on_the_account_is_not_attributed_to_a_strategy()
    {
        // An order with no client id of ours is one placed by hand or by another process on the same account. There
        // is no id to attribute it to, so reporting it would invent an order the engine never sent.
        await using Rig rig = new();
        await rig.LoggedInAsync();

        await rig.Session.SendTextAsync("""
            {"arg":{"channel":"orders","instType":"SPOT"},"data":[{"instType":"SPOT","instId":"BTC-USDT","ordId":"999","clOrdId":"","px":"100","sz":"1","ordType":"limit","side":"buy","tdMode":"cash","accFillSz":"0","fillSz":"0","state":"live","uTime":"1700000000000","cTime":"1700000000000"}]}
            """);

        await rig.Session.SendTextAsync(OkxVenue.PongMessage);

        Assert.Empty(rig.Sink.AllOrderEvents);
    }

    // ----- reports -----

    [Fact]
    public async Task An_open_order_is_reported_from_the_pending_endpoint_with_the_market_named()
    {
        await using Rig rig = new();
        await rig.ConnectAsync();
        rig.Routes.On("GET", PendingPath, _ => StubResponse.Json(OkxPayloads.PendingOrders("O1")));

        IReadOnlyList<OrderStatusReport> reports = await rig.Client
            .GenerateOrderStatusReportsAsync(null, null, null, openOnly: true, CancellationToken.None)
            .WaitAsync(Wait.Timeout);

        OrderStatusReport report = Assert.Single(reports);
        Assert.Equal(_pair, report.InstrumentId);
        Assert.Equal(OrderStatus.Accepted, report.OrderStatus);
        Assert.Equal(OrderType.Limit, report.OrderType);
        Assert.Equal(TimeInForce.Gtc, report.TimeInForce);
        Assert.Equal(100m, report.Price!.Value.Value);
        Assert.Equal("SPOT", Assert.Single(rig.Server.RequestsTo(PendingPath)).Query("instType"));
    }

    [Fact]
    public async Task A_fill_report_negates_the_venues_fee_and_names_the_engines_order()
    {
        await using Rig rig = new();
        await rig.ConnectAsync();
        rig.Routes.On("GET", FillsPath, _ => StubResponse.Json(OkxPayloads.Fills("O1")));

        IReadOnlyList<FillReport> fills = await rig.Client
            .GenerateFillReportsAsync(null, null, null, null, CancellationToken.None)
            .WaitAsync(Wait.Timeout);

        FillReport fill = Assert.Single(fills);
        Assert.Equal(0.08m, fill.Commission.Amount);
        Assert.Equal(LiquiditySide.Maker, fill.LiquiditySide);
        Assert.Equal("O1", fill.ClientOrderId!.Value.Value);
        Assert.Equal(1m, fill.LastQty.Value);
    }

    [Fact]
    public async Task A_position_is_reported_in_base_currency_with_its_sign_read_as_a_side()
    {
        // The venue counts a position in contracts and signs it, so 300 contracts long is three bitcoin long. Both
        // halves matter: the size and the side.
        await using Rig rig = new(OkxInstrumentType.Swap);
        await rig.ConnectAsync();
        rig.Routes.On("GET", PositionsPath, OkxPayloads.Positions);

        IReadOnlyList<PositionStatusReport> reports = await rig.Client
            .GeneratePositionStatusReportsAsync(null, null, null, CancellationToken.None)
            .WaitAsync(Wait.Timeout);

        Assert.Equal(2, reports.Count);
        PositionStatusReport btc = reports.Single(r => r.InstrumentId == _perpetual);
        Assert.Equal(PositionSide.Long, btc.PositionSide);
        Assert.Equal(300m * Contract, btc.Quantity.Value);
        Assert.Equal(100m, btc.AvgPxOpen);

        // Flat is still reported, which keeps reconciliation able to close a position this node thinks is open.
        Assert.Contains(reports, r => r.PositionSide == PositionSide.Flat && r.Quantity.Value == 0m);
    }

    [Fact]
    public async Task A_negative_position_is_a_short()
    {
        await using Rig rig = new(OkxInstrumentType.Swap);
        await rig.ConnectAsync();
        rig.Routes.On("GET", PositionsPath, OkxPayloads.ShortPosition);

        IReadOnlyList<PositionStatusReport> reports = await rig.Client
            .GeneratePositionStatusReportsAsync(null, null, null, CancellationToken.None)
            .WaitAsync(Wait.Timeout);

        PositionStatusReport btc = Assert.Single(reports);
        Assert.Equal(PositionSide.Short, btc.PositionSide);
        Assert.Equal(300m * Contract, btc.Quantity.Value);
    }

    [Fact]
    public async Task A_spot_client_asks_for_no_positions_because_a_cash_account_holds_none()
    {
        // Asking anyway would return this unified account's DERIVATIVE positions, which belong to another client -
        // and reconciliation would then try to close a perpetual position through a spot client.
        await using Rig rig = new();
        await rig.ConnectAsync();
        rig.Routes.On("GET", PositionsPath, OkxPayloads.Positions);

        IReadOnlyList<PositionStatusReport> reports = await rig.Client
            .GeneratePositionStatusReportsAsync(null, null, null, CancellationToken.None)
            .WaitAsync(Wait.Timeout);

        Assert.Empty(reports);
        Assert.Empty(rig.Server.RequestsTo(PositionsPath));
    }

    [Fact]
    public async Task A_mass_status_gathers_the_orders_the_fills_and_the_positions()
    {
        await using Rig rig = new(OkxInstrumentType.Swap);
        await rig.ConnectAsync();
        rig.Routes
            .On("GET", PendingPath, _ => StubResponse.Json(OkxPayloads.PendingOrders("O1")))
            .On("GET", FillsPath, _ => StubResponse.Json(OkxPayloads.SwapFills("O1")))
            .On("GET", PositionsPath, OkxPayloads.Positions);

        ExecutionMassStatus status = (await rig.Client.GenerateMassStatusAsync(null, CancellationToken.None).WaitAsync(Wait.Timeout))!;

        Assert.NotEmpty(status.PositionReports);
        Assert.NotEmpty(status.FillReports);

        // The swap fill's 300 contracts as three bitcoin, which is the conversion a reconciliation depends on: a
        // fill report that disagreed with the fill event would replace a correct position with a wrong one.
        Assert.Equal(300m * Contract, status.FillReports[0].LastQty.Value);

        // And an order of a different market is not in this client's report: the pending fixture is a spot order,
        // which this swap client does not know, so it is left out rather than reported against an instrument it has
        // never loaded.
        Assert.Empty(status.OrderReports);
    }

    // ----- cancelling -----

    [Fact]
    public async Task A_cancel_names_the_order_by_the_id_the_engine_gave_it()
    {
        await using Rig rig = new();
        await rig.LoggedInAsync();
        rig.Routes.On("POST", CancelPath, OkxPayloads.Envelope("""[{"clOrdId":"O1","ordId":"312269865356374016","sCode":"0","sMsg":""}]"""));

        await rig.Client.CancelOrderAsync(
            new CancelOrder(rig.Kernel.Services.TraderId, _strategy, _pair, new ClientOrderId("O1"), null, null, Guid.NewGuid(), TestKernel.Now),
            CancellationToken.None).WaitAsync(Wait.Timeout);

        JsonElement body = Body(Assert.Single(rig.Server.RequestsTo(CancelPath)));
        Assert.Equal("BTC-USDT", body.GetProperty("instId").GetString());
        Assert.Equal("O1", body.GetProperty("clOrdId").GetString());
    }

    [Fact]
    public async Task Cancelling_everything_reads_the_open_orders_and_cancels_them_in_one_batch()
    {
        // The venue has no cancel-all for this market, so the open orders are read and batched. One request per
        // twenty orders rather than one per order, which matters when a node is shutting down and the orders are
        // the only thing left at the venue.
        await using Rig rig = new();
        await rig.LoggedInAsync();
        rig.Routes
            .On("GET", PendingPath, _ => StubResponse.Json(OkxPayloads.PendingOrders("O1")))
            .On("POST", CancelBatchPath, OkxPayloads.Envelope("""[{"clOrdId":"O1","ordId":"312269865356374016","sCode":"0","sMsg":""}]"""));

        await rig.Client.CancelAllOrdersAsync(
            new CancelAllOrders(rig.Kernel.Services.TraderId, _strategy, _pair, null, null, Guid.NewGuid(), TestKernel.Now),
            CancellationToken.None).WaitAsync(Wait.Timeout);

        RecordedRequest batch = Assert.Single(rig.Server.RequestsTo(CancelBatchPath));
        using JsonDocument body = JsonDocument.Parse(batch.Body);
        Assert.Equal(JsonValueKind.Array, body.RootElement.ValueKind);
        Assert.Equal("O1", body.RootElement[0].GetProperty("clOrdId").GetString());
    }

    [Fact]
    public async Task A_refused_cancel_does_not_throw_at_the_caller()
    {
        // The usual reason is an order that has already gone - filled or cancelled - and a node shutting down must
        // not fail over one.
        await using Rig rig = new();
        await rig.LoggedInAsync();
        rig.Routes.On("POST", CancelPath, _ => StubResponse.Json(OkxPayloads.Error("51400", "Cancellation failed as the order does not exist")));

        await rig.Client.CancelOrderAsync(
            new CancelOrder(rig.Kernel.Services.TraderId, _strategy, _pair, new ClientOrderId("O1"), null, null, Guid.NewGuid(), TestKernel.Now),
            CancellationToken.None).WaitAsync(Wait.Timeout);

        Assert.True(rig.Client.IsConnected);
    }

    // ----- the account a client claims to be -----

    [Theory]
    [InlineData(OkxInstrumentType.Spot, AccountType.Cash, "OKX-SPOT")]
    [InlineData(OkxInstrumentType.Swap, AccountType.Margin, "OKX-SWAP")]
    [InlineData(OkxInstrumentType.Futures, AccountType.Margin, "OKX-FUTURES")]
    public async Task A_client_names_the_account_after_the_market_it_trades(OkxInstrumentType type, AccountType accountType, string accountId)
    {
        // One venue account and three clients on it, so the ids have to differ: two clients sharing one account id
        // would have each other's balances and positions applied to them.
        await using Rig rig = new(type);
        await rig.ConnectAsync();

        Assert.Equal(accountType, rig.Client.AccountType);
        Assert.Equal(accountId, rig.Client.AccountId.Value);

        // Netting, because this client never sends a position side - which is what the venue's net mode means.
        Assert.Equal(OmsType.Netting, rig.Client.OmsType);
    }
}
