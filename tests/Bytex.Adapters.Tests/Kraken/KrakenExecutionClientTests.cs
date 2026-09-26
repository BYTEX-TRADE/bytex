using System.Text.Json;
using Bytex.Adapters.Kraken;
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

namespace Bytex.Adapters.Tests.Kraken;

// Why: Kraken's two platforms take an order in two different languages, and the differences are the quiet kind.
//
//   spot                                     futures
//   a form body                              a form body, different fields
//   pair / type / ordertype / volume          symbol / side / orderType / size
//   cl_ord_id                                cliOrdId
//   `price` is the LIMIT on a limit order     limitPrice
//   `price` is the TRIGGER on a stop, and     stopPrice, plus a triggerSignal that says which
//     `price2` is that order's limit            price the stop watches
//   post-only is a FLAG in `oflags`           post-only is its own orderType
//   ioc is a `timeinforce`                    ioc is its own orderType
//   a refusal is an error array               a refusal can arrive INSIDE a 200 as sendStatus.status
//
// The last one is the one that would ship: the futures platform answers a refused order with HTTP 200 and
// result:"success", and puts the refusal in a status field on the order. A client reading only the envelope would
// report an order as placed that the venue never accepted, and the strategy would hold a position it does not have.
//
// EVERYTHING HERE IS THE VENUE'S PUBLISHED CONTRACT AND NOT A RECORDING. Neither platform's private surface can be
// reached without a key, and neither validates anything before the credentials: spot answers "EAPI:Invalid key"
// first, futures answers "authenticationError" for a wrong key, a wrong signature and a wrong clock alike. One
// exception, and it is recorded: /derivatives/api/v3/editorder refused a malformed order id BEFORE it refused the
// credentials, which is how the amend endpoint is known to exist at all.
public sealed class KrakenExecutionClientTests
{
    private const string Key = "kraken-key";

    /// <summary>A base64 private key of the shape the venue issues, so the signer's decode has something to do.</summary>
    private const string Secret = "a2Vja2V5c2VjcmV0MTIzNDU2Nzg5MGFiY2RlZmdoaWprbG1ub3BxcnN0dXZ3eHl6MDEyMzQ1Njc4OQ==";

    private const string Broker = "BYTX1234ABCD5678";

    private static readonly StrategyId _strategy = new("Probe-001");
    private static readonly InstrumentId _btc = InstrumentId.Parse("BTC-USD.KRAKEN");
    private static readonly InstrumentId _perp = InstrumentId.Parse("PF_XBTUSD.KRAKEN");

    private sealed class SpotRig : IAsyncDisposable
    {
        private WsSession? _session;

        public SpotRig(string? brokerId = null, decimal? leverage = null)
        {
            Routes = new Routes()
                .On("GET", "/0/public/AssetPairs", KrakenPayloads.AssetPairs)
                .On("GET", "/0/public/Assets", KrakenPayloads.Assets)
                .On("POST", "/0/private/BalanceEx", KrakenPayloads.BalanceEx)
                .On("POST", "/0/private/GetWebSocketsToken", KrakenPayloads.WebSocketsToken)
                .On("POST", "/0/private/AddOrder", KrakenPayloads.AddOrder)
                .On("POST", "/0/private/AmendOrder", KrakenPayloads.Envelope("""{"amend_id":"TQ1234-ABCDE"}"""))
                .On("POST", "/0/private/CancelOrder", KrakenPayloads.Envelope("""{"count":1}"""));

            Server = new LoopbackServer(r => Routes.Handle(r));
            Kernel = new TestKernel(Logs);
            Client = new KrakenExecutionClient(new ClientId("KRAKEN"), new KrakenExecutionClientConfig
            {
                ApiKey = Key,
                ApiSecret = Secret,
                BrokerId = brokerId,
                Leverage = leverage,
                BaseUrlHttp = Server.HttpBase,
                BaseUrlWs = Server.WsBase,
                InstrumentProvider = new InstrumentProviderConfig { LoadAll = true },
            }, Kernel.Services);

            Client.AttachSink(Sink);
            Orders = new OrderFactory(Kernel.Services.TraderId, _strategy, Kernel.Clock);
        }

        public Routes Routes { get; }

        public RecordingLogs Logs { get; } = new();

        public LoopbackServer Server { get; }

        public TestKernel Kernel { get; }

        public KrakenExecutionClient Client { get; }

        public RecordingExecutionSink Sink { get; } = new();

        public OrderFactory Orders { get; }

        public WsSession Session => _session ?? throw new InvalidOperationException("not connected");

        public Instrument Instrument => Client.Instruments.Find(_btc)!;

        public async Task<SpotRig> ConnectAsync()
        {
            await Client.ConnectAsync(CancellationToken.None).WaitAsync(Wait.Timeout);
            _session = await Server.NextSessionAsync();
            return this;
        }

        public async Task SubmitAsync(Order order)
        {
            Kernel.Kernel.Cache.AddOrder(order);
            await Client.SubmitOrderAsync(
                new SubmitOrder(Kernel.Services.TraderId, _strategy, order, null, null, null, Guid.NewGuid(), TestKernel.Now),
                CancellationToken.None).WaitAsync(Wait.Timeout);
        }

        public RecordedRequest LastOrder() => Server.RequestsTo("/0/private/AddOrder").Last();

        public async ValueTask DisposeAsync()
        {
            await Client.DisconnectAsync(CancellationToken.None);
            Client.Dispose();
            Kernel.Dispose();
            await Server.DisposeAsync();
        }
    }

    private sealed class FuturesRig : IAsyncDisposable
    {
        private WsSession? _session;

        public FuturesRig(string? brokerId = null, decimal? leverage = null)
        {
            Routes = new Routes()
                .On("GET", "/derivatives/api/v3/instruments", KrakenPayloads.Instruments)
                .On("GET", "/derivatives/api/v3/feeschedules", KrakenPayloads.FeeSchedules)
                .On("GET", "/derivatives/api/v3/accounts", KrakenPayloads.Accounts)
                .On("PUT", "/derivatives/api/v3/leveragepreferences", KrakenPayloads.LeveragePreferenceSet)
                .On("POST", "/derivatives/api/v3/sendorder", _ => StubResponse.Json(KrakenPayloads.SendOrder()))
                .On("POST", "/derivatives/api/v3/editorder", _ => StubResponse.Json(KrakenPayloads.EditOrder()))
                .On("POST", "/derivatives/api/v3/cancelorder", KrakenPayloads.CancelAcknowledged)
                .On("POST", "/derivatives/api/v3/cancelallorders", KrakenPayloads.CancelAcknowledged);

            Server = new LoopbackServer(r => Routes.Handle(r));
            Kernel = new TestKernel(Logs);
            Client = new KrakenFuturesExecutionClient(new ClientId("KRAKEN"), new KrakenExecutionClientConfig
            {
                ProductType = KrakenProductType.Futures,
                ApiKey = Key,
                ApiSecret = Secret,
                BrokerId = brokerId,
                Leverage = leverage,
                BaseUrlHttp = Server.HttpBase,
                BaseUrlWs = Server.WsBase,
                InstrumentProvider = new InstrumentProviderConfig { LoadIds = [_perp] },
            }, Kernel.Services);

            Client.AttachSink(Sink);
            Orders = new OrderFactory(Kernel.Services.TraderId, _strategy, Kernel.Clock);
        }

        public Routes Routes { get; }

        public RecordingLogs Logs { get; } = new();

        public LoopbackServer Server { get; }

        public TestKernel Kernel { get; }

        public KrakenFuturesExecutionClient Client { get; }

        public RecordingExecutionSink Sink { get; } = new();

        public OrderFactory Orders { get; }

        public WsSession Session => _session ?? throw new InvalidOperationException("not connected");

        public Instrument Instrument => Client.Instruments.Find(_perp)!;

        public async Task<FuturesRig> ConnectAsync()
        {
            await Client.ConnectAsync(CancellationToken.None).WaitAsync(Wait.Timeout);
            _session = await Server.NextSessionAsync();
            return this;
        }

        public async Task SubmitAsync(Order order)
        {
            Kernel.Kernel.Cache.AddOrder(order);
            await Client.SubmitOrderAsync(
                new SubmitOrder(Kernel.Services.TraderId, _strategy, order, null, null, null, Guid.NewGuid(), TestKernel.Now),
                CancellationToken.None).WaitAsync(Wait.Timeout);
        }

        public RecordedRequest LastOrder() => Server.RequestsTo("/derivatives/api/v3/sendorder").Last();

        public async ValueTask DisposeAsync()
        {
            await Client.DisconnectAsync(CancellationToken.None);
            Client.Dispose();
            Kernel.Dispose();
            await Server.DisposeAsync();
        }
    }

    private static IReadOnlyDictionary<string, string> Form(RecordedRequest request) =>
        request.Body.Length == 0
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : request.Body.Split('&')
                .Select(p => p.Split('=', 2))
                .ToDictionary(kv => Uri.UnescapeDataString(kv[0]), kv => kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : string.Empty, StringComparer.Ordinal);

    // ----- spot: signing and the nonce -----

    [Fact]
    public async Task Every_private_spot_request_is_signed_and_carries_a_nonce_in_its_body()
    {
        // Both halves matter. Without the key header the venue answers "EAPI:Invalid key", and without a nonce in
        // the BODY - not the query, not a header - the signature covers a different string than the venue computes.
        await using SpotRig rig = await new SpotRig().ConnectAsync();

        RecordedRequest balance = rig.Server.RequestsTo("/0/private/BalanceEx").First();

        Assert.Equal("POST", balance.Method);
        Assert.Equal(Key, balance.Header("API-Key"));
        Assert.NotNull(balance.Header("API-Sign"));
        Assert.Contains("nonce", Form(balance).Keys);
    }

    [Fact]
    public async Task The_private_socket_is_opened_with_a_token_fetched_over_rest()
    {
        // This socket is not signed: it is opened with a short-lived token from a signed call, so a key that cannot
        // sign cannot even listen. The token is what the subscription carries, not the key.
        await using SpotRig rig = await new SpotRig().ConnectAsync();

        Assert.NotEmpty(rig.Server.RequestsTo("/0/private/GetWebSocketsToken"));

        using JsonDocument subscription = JsonDocument.Parse(await rig.Session.ReceiveTextAsync(Wait.Timeout));
        JsonElement parameters = subscription.RootElement.GetProperty("params");

        Assert.Equal("executions", parameters.GetProperty("channel").GetString());
        Assert.Equal("a-short-lived-token", parameters.GetProperty("token").GetString());
    }

    [Fact]
    public async Task A_spot_account_is_a_cash_account_and_no_leverage_is_sent_anywhere()
    {
        // Kraken spot offers margin trading and this client trades the cash account, so there is no per-symbol
        // leverage to set and an order carrying one would be refused. A configured leverage is therefore a no-op -
        // and it says so out loud, because a strategy written for 3x that silently runs at 1x is a position sized
        // wrongly rather than a failed request.
        await using SpotRig rig = await new SpotRig(leverage: 3m).ConnectAsync();

        Assert.Equal(AccountType.Cash, rig.Client.AccountType);
        Assert.DoesNotContain(rig.Server.Requests, r => r.Path.Contains("leverage", StringComparison.OrdinalIgnoreCase));

        // And it is not silent about it: a strategy written for 3x that quietly runs at 1x is a position sized
        // wrongly rather than a failed request, so the client says which family does take one.
        Assert.Contains(
            "cash account",
            File.ReadAllText(Repo.SourceFiles("Kraken").Single(f => Path.GetFileNameWithoutExtension(f) == nameof(KrakenExecutionClient))),
            StringComparison.Ordinal);
    }

    // ----- spot: the order -----

    [Fact]
    public async Task A_spot_limit_order_names_the_pair_by_the_wire_spelling_and_puts_the_limit_in_price()
    {
        await using SpotRig rig = await new SpotRig().ConnectAsync();

        LimitOrder order = rig.Orders.Limit(_btc, OrderSide.Buy, rig.Instrument.MakeQuantity(0.5m), rig.Instrument.MakePrice(50_000m));
        await rig.SubmitAsync(order);

        IReadOnlyDictionary<string, string> form = Form(rig.LastOrder());

        Assert.Equal("BTC/USD", form["pair"]);
        Assert.Equal("buy", form["type"]);
        Assert.Equal("limit", form["ordertype"]);
        Assert.Equal("0.50000000", form["volume"]);
        Assert.Equal("50000.0", form["price"]);
        Assert.Equal(order.ClientOrderId.Value, form["cl_ord_id"]);

        // A plain limit order has no trigger, so price2 must not appear: the venue reads it as the limit of a
        // trigger order and would refuse an order that has no trigger to go with it.
        Assert.DoesNotContain("price2", form.Keys);
    }

    [Fact]
    public async Task A_spot_stop_limit_puts_the_trigger_in_price_and_the_limit_in_price2()
    {
        // The most confusable pair of fields on this platform, and the failure is not an error: with the two
        // swapped the venue takes a perfectly valid order that triggers at the limit and limits at the trigger.
        await using SpotRig rig = await new SpotRig().ConnectAsync();

        StopLimitOrder order = rig.Orders.StopLimit(
            _btc,
            OrderSide.Sell,
            rig.Instrument.MakeQuantity(0.25m),
            rig.Instrument.MakePrice(48_000m),
            rig.Instrument.MakePrice(49_000m));

        await rig.SubmitAsync(order);
        IReadOnlyDictionary<string, string> form = Form(rig.LastOrder());

        Assert.Equal("stop-loss-limit", form["ordertype"]);
        Assert.Equal("49000.0", form["price"]);
        Assert.Equal("48000.0", form["price2"]);
    }

    [Fact]
    public async Task Post_only_is_a_flag_here_and_immediate_or_cancel_is_a_time_in_force()
    {
        await using SpotRig rig = await new SpotRig().ConnectAsync();

        LimitOrder maker = rig.Orders.Limit(
            _btc, OrderSide.Buy, rig.Instrument.MakeQuantity(0.5m), rig.Instrument.MakePrice(50_000m), postOnly: true);

        await rig.SubmitAsync(maker);
        Assert.Equal("post", Form(rig.LastOrder())["oflags"]);

        LimitOrder immediate = rig.Orders.Limit(
            _btc, OrderSide.Buy, rig.Instrument.MakeQuantity(0.5m), rig.Instrument.MakePrice(50_000m), TimeInForce.Ioc);

        await rig.SubmitAsync(immediate);
        Assert.Equal("IOC", Form(rig.LastOrder())["timeinforce"]);
    }

    [Fact]
    public async Task A_good_till_cancelled_order_says_nothing_about_its_time_in_force()
    {
        // The venue's default, so sending it would change the bytes every order carries for no reason - and the
        // bytes an order carries are what a venue's own logs show when something is disputed.
        await using SpotRig rig = await new SpotRig().ConnectAsync();

        await rig.SubmitAsync(rig.Orders.Limit(_btc, OrderSide.Buy, rig.Instrument.MakeQuantity(0.5m), rig.Instrument.MakePrice(50_000m)));

        Assert.DoesNotContain("timeinforce", Form(rig.LastOrder()).Keys);
    }

    [Fact]
    public async Task An_order_type_this_platform_does_not_take_is_refused_before_it_is_sent()
    {
        await using SpotRig rig = await new SpotRig().ConnectAsync();

        TrailingStopMarketOrder trailing = rig.Orders.TrailingStopMarket(
            _btc, OrderSide.Sell, rig.Instrument.MakeQuantity(0.5m), 100m);

        await rig.SubmitAsync(trailing);

        OrderRejected rejected = Assert.IsType<OrderRejected>(await rig.Sink.NextOrderEventAsync());
        Assert.Contains("TrailingStopMarket", rejected.Reason, StringComparison.Ordinal);
        Assert.Empty(rig.Server.RequestsTo("/0/private/AddOrder"));
    }

    [Fact]
    public async Task A_refusal_the_venue_puts_in_an_error_array_rejects_the_order()
    {
        // The platform answers a refused order with HTTP 200 and the reason in an error array, so a client checking
        // the status would report the order as placed.
        await using SpotRig rig = await new SpotRig().ConnectAsync();
        rig.Routes.On("POST", "/0/private/AddOrder", _ => StubResponse.Json(KrakenPayloads.Error("EOrder:Insufficient funds")));

        await rig.SubmitAsync(rig.Orders.Limit(_btc, OrderSide.Buy, rig.Instrument.MakeQuantity(0.5m), rig.Instrument.MakePrice(50_000m)));

        Assert.IsType<OrderSubmitted>(await rig.Sink.NextOrderEventAsync());
        OrderRejected rejected = Assert.IsType<OrderRejected>(await rig.Sink.NextOrderEventAsync());
        Assert.Contains("Insufficient funds", rejected.Reason, StringComparison.Ordinal);
    }

    // ----- spot: the broker id -----

    [Fact]
    public async Task No_broker_id_configured_means_the_venue_receives_exactly_what_it_used_to()
    {
        // Almost nobody will configure one, so the untagged path has to stay byte-for-byte what it was - otherwise
        // turning the field on would change how every order is placed for everybody who does not use it.
        await using SpotRig rig = await new SpotRig().ConnectAsync();

        await rig.SubmitAsync(rig.Orders.Limit(_btc, OrderSide.Buy, rig.Instrument.MakeQuantity(0.5m), rig.Instrument.MakePrice(50_000m)));

        Assert.DoesNotContain("broker", Form(rig.LastOrder()).Keys);
    }

    [Fact]
    public async Task A_broker_id_travels_in_a_field_of_the_order_and_leaves_its_identity_alone()
    {
        // The mechanism the venue publishes: a `broker` parameter on AddOrder carrying the partner's own Kraken
        // IIBAN. Because it is a field and not a prefix, the order's client id - which is what reconciliation
        // matches on - is untouched, so turning an id on is not a reconciliation question here.
        await using SpotRig rig = await new SpotRig(brokerId: Broker).ConnectAsync();

        LimitOrder order = rig.Orders.Limit(_btc, OrderSide.Buy, rig.Instrument.MakeQuantity(0.5m), rig.Instrument.MakePrice(50_000m));
        await rig.SubmitAsync(order);

        IReadOnlyDictionary<string, string> form = Form(rig.LastOrder());
        Assert.Equal(Broker, form["broker"]);
        Assert.Equal(order.ClientOrderId.Value, form["cl_ord_id"]);
        Assert.DoesNotContain(Broker, form["cl_ord_id"], StringComparison.Ordinal);
    }

    // ----- spot: amend rather than cancel and replace -----

    [Fact]
    public async Task A_spot_amend_is_a_real_amend_and_not_a_cancel_and_replace()
    {
        // The platform has AmendOrder, which keeps the order's identity and its queue place where it can. A cancel
        // and replace would take a protective order off the book for the length of two requests without the caller
        // having asked for that, which is the declaration's AmendOrders being true rather than nearly true.
        await using SpotRig rig = await new SpotRig().ConnectAsync();

        LimitOrder order = rig.Orders.Limit(_btc, OrderSide.Buy, rig.Instrument.MakeQuantity(0.5m), rig.Instrument.MakePrice(50_000m));
        await rig.SubmitAsync(order);

        await rig.Client.ModifyOrderAsync(
            new ModifyOrder(
                rig.Kernel.Services.TraderId,
                _strategy,
                _btc,
                order.ClientOrderId,
                new VenueOrderId("OQCLML-BW3P3-BUCMWZ"),
                rig.Instrument.MakeQuantity(0.75m),
                rig.Instrument.MakePrice(49_000m),
                null,
                null,
                Guid.NewGuid(),
                TestKernel.Now),
            CancellationToken.None).WaitAsync(Wait.Timeout);

        IReadOnlyDictionary<string, string> amend = Form(rig.Server.RequestsTo("/0/private/AmendOrder").Single());
        Assert.Equal("OQCLML-BW3P3-BUCMWZ", amend["txid"]);
        Assert.Equal("0.75000000", amend["order_qty"]);
        Assert.Equal("49000.0", amend["limit_price"]);

        // And nothing was cancelled on the way.
        Assert.Empty(rig.Server.RequestsTo("/0/private/CancelOrder"));
    }

    [Fact]
    public async Task An_amend_of_an_order_whose_venue_id_is_unknown_names_it_by_the_client_id()
    {
        // The venue takes either identity. The client order id is the one this node always has, so an amend can
        // reach an order whose acceptance message was missed.
        await using SpotRig rig = await new SpotRig().ConnectAsync();

        LimitOrder order = rig.Orders.Limit(_btc, OrderSide.Buy, rig.Instrument.MakeQuantity(0.5m), rig.Instrument.MakePrice(50_000m));
        await rig.SubmitAsync(order);

        await rig.Client.ModifyOrderAsync(
            new ModifyOrder(
                rig.Kernel.Services.TraderId, _strategy, _btc, order.ClientOrderId, null,
                rig.Instrument.MakeQuantity(0.75m), null, null, null, Guid.NewGuid(), TestKernel.Now),
            CancellationToken.None).WaitAsync(Wait.Timeout);

        Assert.Equal(order.ClientOrderId.Value, Form(rig.Server.RequestsTo("/0/private/AmendOrder").Single())["cl_ord_id"]);
    }

    [Fact]
    public async Task A_refused_amend_is_reported_rather_than_swallowed()
    {
        // A strategy that resized a protective order and was not told the venue refused would hold a position
        // larger than the stop guarding it.
        await using SpotRig rig = await new SpotRig().ConnectAsync();
        rig.Routes.On("POST", "/0/private/AmendOrder", _ => StubResponse.Json(KrakenPayloads.Error("EOrder:Invalid price")));

        LimitOrder order = rig.Orders.Limit(_btc, OrderSide.Buy, rig.Instrument.MakeQuantity(0.5m), rig.Instrument.MakePrice(50_000m));
        await rig.SubmitAsync(order);
        await rig.Sink.NextOrderEventAsync();
        await rig.Sink.NextOrderEventAsync();

        await rig.Client.ModifyOrderAsync(
            new ModifyOrder(
                rig.Kernel.Services.TraderId, _strategy, _btc, order.ClientOrderId, null,
                rig.Instrument.MakeQuantity(0.75m), null, null, null, Guid.NewGuid(), TestKernel.Now),
            CancellationToken.None).WaitAsync(Wait.Timeout);

        Assert.IsType<OrderPendingUpdate>(await rig.Sink.NextOrderEventAsync());
        OrderModifyRejected rejected = Assert.IsType<OrderModifyRejected>(await rig.Sink.NextOrderEventAsync());
        Assert.Contains("Invalid price", rejected.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancel_all_on_one_instrument_cancels_that_instruments_orders_one_by_one()
    {
        // The venue's own CancelAll takes no symbol and cancels the whole account, and a cancel-all command always
        // names an instrument. So the base class's loop is the correct behaviour here: sending the venue's call
        // would close orders on every other instrument the account holds.
        await using SpotRig rig = await new SpotRig().ConnectAsync();

        LimitOrder order = rig.Orders.Limit(_btc, OrderSide.Buy, rig.Instrument.MakeQuantity(0.5m), rig.Instrument.MakePrice(50_000m));
        await rig.SubmitAsync(order);

        await rig.Client.CancelAllOrdersAsync(
            new CancelAllOrders(rig.Kernel.Services.TraderId, _strategy, _btc, null, null, Guid.NewGuid(), TestKernel.Now),
            CancellationToken.None).WaitAsync(Wait.Timeout);

        Assert.DoesNotContain(rig.Server.Requests, r => r.Path == "/0/private/CancelAll");
    }

    // ----- spot: what the socket says happened -----

    [Fact]
    public async Task An_execution_message_that_names_an_order_this_node_placed_is_reported_against_it()
    {
        await using SpotRig rig = await new SpotRig().ConnectAsync();

        LimitOrder order = rig.Orders.Limit(_btc, OrderSide.Buy, rig.Instrument.MakeQuantity(0.5m), rig.Instrument.MakePrice(50_000m));
        await rig.SubmitAsync(order);
        Assert.IsType<OrderSubmitted>(await rig.Sink.NextOrderEventAsync());
        Assert.IsType<OrderAccepted>(await rig.Sink.NextOrderEventAsync());

        await rig.Session.SendTextAsync(KrakenPayloads.Fill(order.ClientOrderId.Value, "0.5", "50000", "80.0", "m"));

        OrderFilled fill = Assert.IsType<OrderFilled>(await rig.Sink.NextOrderEventAsync());
        Assert.Equal(order.ClientOrderId, fill.ClientOrderId);
        Assert.Equal(0.5m, fill.LastQty.Value);
        Assert.Equal(50_000m, fill.LastPx.Value);
        Assert.Equal(LiquiditySide.Maker, fill.LiquiditySide);

        // The venue's OWN fee, not the instrument's published rate: this account may be on a lower tier than the
        // one a catalog publishes, and a commission worked out from the rate would be a number nobody was charged.
        Assert.Equal(80.0m, fill.Commission.Amount);
    }

    [Fact]
    public async Task A_fill_the_venue_repeats_is_applied_once()
    {
        // The venue sends a snapshot of recent executions when the channel is subscribed and then sends updates, so
        // a fill can arrive twice. Applied twice it would double the position.
        await using SpotRig rig = await new SpotRig().ConnectAsync();

        LimitOrder order = rig.Orders.Limit(_btc, OrderSide.Buy, rig.Instrument.MakeQuantity(0.5m), rig.Instrument.MakePrice(50_000m));
        await rig.SubmitAsync(order);
        await rig.Sink.NextOrderEventAsync();
        await rig.Sink.NextOrderEventAsync();

        await rig.Session.SendTextAsync(KrakenPayloads.Fill(order.ClientOrderId.Value, "0.5", "50000", "80.0"));
        Assert.IsType<OrderFilled>(await rig.Sink.NextOrderEventAsync());

        await rig.Session.SendTextAsync(KrakenPayloads.Fill(order.ClientOrderId.Value, "0.5", "50000", "80.0"));
        await rig.Session.SendTextAsync(KrakenPayloads.Execution(order.ClientOrderId.Value, "canceled"));

        Assert.IsType<OrderCanceled>(await rig.Sink.NextOrderEventAsync());
        Assert.Single(rig.Sink.AllOrderEvents.OfType<OrderFilled>());
    }

    [Fact]
    public async Task An_execution_for_an_order_this_node_never_placed_is_ignored()
    {
        // There is nothing to report it against, and inventing a client order id for it would attach the venue's
        // activity to an order that does not exist.
        await using SpotRig rig = await new SpotRig().ConnectAsync();

        await rig.Session.SendTextAsync(KrakenPayloads.Execution("someone-elses-order", "new"));
        await rig.Session.SendTextAsync(KrakenPayloads.Execution(string.Empty, "new"));

        LimitOrder order = rig.Orders.Limit(_btc, OrderSide.Buy, rig.Instrument.MakeQuantity(0.5m), rig.Instrument.MakePrice(50_000m));
        await rig.SubmitAsync(order);

        // The first events to arrive are this order's own, which is what proves the two messages produced none.
        Assert.IsType<OrderSubmitted>(await rig.Sink.NextOrderEventAsync());
    }

    [Fact]
    public async Task A_balance_is_read_under_the_asset_code_the_venue_answers_to()
    {
        // The balances endpoint keys its holdings by the LEGACY code - XXBT - so a client that took the key as it
        // came would publish a balance in a currency no instrument on this venue is denominated in, and the account
        // would look like it held nothing tradable.
        await using SpotRig rig = await new SpotRig().ConnectAsync();

        AccountState state = await rig.Sink.NextAccountStateAsync();

        Assert.Contains(state.Balances, b => b.Currency.Code == "BTC" && b.Total.Amount == 1.5m);
        Assert.Contains(state.Balances, b => b.Currency.Code == "USD" && b.Total.Amount == 100_000m);
        Assert.DoesNotContain(state.Balances, b => b.Currency.Code == "XXBT");
    }

    // ----- spot: reports -----

    [Fact]
    public async Task A_fill_report_reads_the_trades_fractional_seconds_and_the_venues_fee()
    {
        await using SpotRig rig = await new SpotRig().ConnectAsync();
        rig.Routes.On("POST", "/0/private/TradesHistory", KrakenPayloads.Envelope("""
            {"trades":{"TRADE-1":{"ordertxid":"OQCLML-BW3P3-BUCMWZ","pair":"BTC/USD","time":1790359841.1382287,"type":"buy","ordertype":"limit","price":"50000.0","cost":"25000.0","fee":"40.0","vol":"0.5"}},"count":1}
            """));

        FillReport fill = Assert.Single(await rig.Client.GenerateFillReportsAsync(null, null, null, null, CancellationToken.None));

        Assert.Equal(_btc, fill.InstrumentId);
        Assert.Equal("TRADE-1", fill.TradeId.Value);
        Assert.Equal(0.5m, fill.LastQty.Value);
        Assert.Equal(40.0m, fill.Commission.Amount);
        Assert.Equal(1790359841_138228700L, fill.TsEvent.Value);
    }

    [Fact]
    public async Task An_order_report_reads_the_pair_out_of_the_description_the_venue_nests_it_in()
    {
        // The pair is inside `descr` and not beside the order, which is the field a reader is most likely to look
        // for at the top level and not find - and an order report with no instrument is a report nothing can match.
        await using SpotRig rig = await new SpotRig().ConnectAsync();
        rig.Routes.On("POST", "/0/private/OpenOrders", KrakenPayloads.Envelope("""
            {"open":{"OQCLML-BW3P3-BUCMWZ":{"cl_ord_id":"O-TEST-1","status":"open","opentm":1790359841.1382287,"descr":{"pair":"BTC/USD","type":"buy","ordertype":"limit","price":"50000.0"},"vol":"0.5","vol_exec":"0.2","price":"50000.0"}}}
            """));

        OrderStatusReport report = Assert.Single(
            await rig.Client.GenerateOrderStatusReportsAsync(null, null, null, openOnly: true, CancellationToken.None));

        Assert.Equal(_btc, report.InstrumentId);
        Assert.Equal("O-TEST-1", report.ClientOrderId!.Value.Value);
        Assert.Equal(OrderStatus.Accepted, report.OrderStatus);
        Assert.Equal(0.5m, report.Quantity.Value);
        Assert.Equal(0.2m, report.FilledQuantity.Value);
    }

    [Fact]
    public async Task A_cash_account_reports_no_positions_rather_than_an_empty_answer_nobody_asked_for()
    {
        await using SpotRig rig = await new SpotRig().ConnectAsync();
        rig.Routes.On("POST", "/0/private/TradesHistory", KrakenPayloads.Envelope("""{"trades":{},"count":0}"""));
        rig.Routes.On("POST", "/0/private/OpenOrders", KrakenPayloads.Envelope("""{"open":{}}"""));

        ExecutionMassStatus status = (await rig.Client.GenerateMassStatusAsync(null, CancellationToken.None))!;

        Assert.Empty(status.PositionReports);
        Assert.Equal(AccountType.Cash, rig.Client.AccountType);
    }

    // ----- futures: leverage and the margin mode it drags with it -----

    [Fact]
    public async Task No_leverage_configured_means_the_accounts_margin_mode_is_not_touched()
    {
        // Null means do not touch it, which is what every configuration written before the field existed means -
        // and here it means more than usual, because setting a leverage on this venue also changes the margin mode.
        await using FuturesRig rig = await new FuturesRig().ConnectAsync();

        Assert.Empty(rig.Server.RequestsTo("/derivatives/api/v3/leveragepreferences"));
    }

    [Fact]
    public async Task A_configured_leverage_is_set_per_symbol_and_the_margin_mode_it_selects_is_stated()
    {
        // The coupling that cannot be avoided. A leverage preference for a symbol sets a maximum leverage AND
        // selects ISOLATED margin for it; deleting the preference is what selects cross margin. There is no call
        // that does one without the other, so the mode change is logged rather than done quietly - a strategy sized
        // for cross margin behaves differently under isolated and nothing else would say it had changed.
        await using FuturesRig rig = await new FuturesRig(leverage: 5m).ConnectAsync();

        RecordedRequest set = Assert.Single(rig.Server.RequestsTo("/derivatives/api/v3/leveragepreferences"));

        Assert.Equal("PUT", set.Method);
        IReadOnlyDictionary<string, string> preference = Form(set);
        Assert.Equal("PF_XBTUSD", preference["symbol"]);
        Assert.Equal("5", preference["maxLeverage"]);

        // And the mode it drags with it is stated where a person will read it, rather than discovered from a
        // liquidation price. Checked against the source because it is reported at information level, which is where
        // it belongs: it is what happened, not something that went wrong.
        Assert.Contains(
            "ISOLATED margin",
            File.ReadAllText(Repo.SourceFiles("Kraken").Single(f => Path.GetFileNameWithoutExtension(f) == nameof(KrakenFuturesExecutionClient))),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_leverage_above_what_the_contract_allows_refuses_the_run()
    {
        // PF_XBTUSD's first margin tier is one percent, so it allows 100x. This used to log a warning and send the
        // preference anyway, which left the venue to grant what it allows and the run to trade at a leverage nobody
        // chose - the line in the log said so and nothing stopped. A warning is not a guard when the thing being
        // altered is the size of every position.
        await using FuturesRig rig = new(leverage: 150m);

        ArgumentOutOfRangeException refused = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => rig.ConnectAsync());

        Assert.Contains("100", refused.Message, StringComparison.Ordinal);
        Assert.Contains("150", refused.Message, StringComparison.Ordinal);

        // And nothing was asked of the venue: the refusal comes before the preference is sent.
        Assert.Empty(rig.Server.RequestsTo("/derivatives/api/v3/leveragepreferences"));
    }

    [Fact]
    public async Task A_refused_leverage_preference_does_not_stop_the_node()
    {
        // Usually the account not being entitled to the figure asked for, which a node cannot fix. Abandoning the
        // start silently would be worse than trading at a leverage a person can read about and change.
        await using FuturesRig rig = new(leverage: 5m);
        rig.Routes.On("PUT", "/derivatives/api/v3/leveragepreferences", _ => new StubResponse(400, KrakenPayloads.BadRequest));

        await rig.ConnectAsync();

        Assert.True(rig.Client.IsConnected);
        Assert.NotEmpty(rig.Server.RequestsTo("/derivatives/api/v3/leveragepreferences"));
    }

    [Fact]
    public async Task A_fractional_leverage_is_sent_as_written()
    {
        // A strategy document carries leverage as a decimal, so 2.5 is reachable. The venue is entitled to accept
        // or refuse it and says so for itself; what must not happen is the number being rounded on the way, because
        // that silently changes the size of every position from the one that was backtested.
        await using FuturesRig rig = await new FuturesRig(leverage: 2.5m).ConnectAsync();

        Assert.Equal("2.5", Form(rig.Server.RequestsTo("/derivatives/api/v3/leveragepreferences").Single())["maxLeverage"]);
    }

    // ----- futures: the order -----

    [Fact]
    public async Task A_futures_limit_order_uses_this_platforms_own_field_names()
    {
        await using FuturesRig rig = await new FuturesRig().ConnectAsync();

        LimitOrder order = rig.Orders.Limit(_perp, OrderSide.Buy, rig.Instrument.MakeQuantity(0.001m), rig.Instrument.MakePrice(50_000m));
        await rig.SubmitAsync(order);

        IReadOnlyDictionary<string, string> form = Form(rig.LastOrder());

        Assert.Equal("lmt", form["orderType"]);
        Assert.Equal("PF_XBTUSD", form["symbol"]);
        Assert.Equal("buy", form["side"]);
        Assert.Equal("0.0010", form["size"]);
        Assert.Equal("50000", form["limitPrice"]);
        Assert.Equal(order.ClientOrderId.Value, form["cliOrdId"]);
    }

    [Fact]
    public async Task Post_only_and_immediate_or_cancel_are_order_types_on_this_platform()
    {
        // Not flags and not a time in force, which is the difference from the spot platform of the same venue: the
        // same intent is a different VALUE of the same field here.
        await using FuturesRig rig = await new FuturesRig().ConnectAsync();

        LimitOrder maker = rig.Orders.Limit(
            _perp, OrderSide.Buy, rig.Instrument.MakeQuantity(0.001m), rig.Instrument.MakePrice(50_000m), postOnly: true);
        await rig.SubmitAsync(maker);
        Assert.Equal("post", Form(rig.LastOrder())["orderType"]);

        LimitOrder immediate = rig.Orders.Limit(
            _perp, OrderSide.Buy, rig.Instrument.MakeQuantity(0.001m), rig.Instrument.MakePrice(50_000m), TimeInForce.Ioc);
        await rig.SubmitAsync(immediate);
        Assert.Equal("ioc", Form(rig.LastOrder())["orderType"]);
    }

    [Fact]
    public async Task A_futures_stop_says_which_price_it_watches()
    {
        // The venue offers the last traded price, the index and the mark price. The mark price is what it liquidates
        // against, so a stop guarding a position has to watch the same one - or the position can be liquidated
        // without the stop ever triggering.
        await using FuturesRig rig = await new FuturesRig().ConnectAsync();

        StopMarketOrder stop = rig.Orders.StopMarket(
            _perp, OrderSide.Sell, rig.Instrument.MakeQuantity(0.001m), rig.Instrument.MakePrice(48_000m));

        await rig.SubmitAsync(stop);
        IReadOnlyDictionary<string, string> form = Form(rig.LastOrder());

        Assert.Equal("stp", form["orderType"]);
        Assert.Equal("48000", form["stopPrice"]);
        Assert.Equal("mark", form["triggerSignal"]);
    }

    [Fact]
    public async Task A_refusal_the_venue_hides_inside_a_successful_answer_rejects_the_order()
    {
        // The one that would have shipped. This platform answers a refused order with HTTP 200 and
        // result:"success", and puts the refusal in sendStatus.status. A client reading only the envelope would
        // report the order as placed and the strategy would believe it held a position it does not have.
        await using FuturesRig rig = await new FuturesRig().ConnectAsync();
        rig.Routes.On("POST", "/derivatives/api/v3/sendorder", _ => StubResponse.Json(KrakenPayloads.SendOrder("insufficientAvailableFunds")));

        await rig.SubmitAsync(rig.Orders.Limit(_perp, OrderSide.Buy, rig.Instrument.MakeQuantity(0.001m), rig.Instrument.MakePrice(50_000m)));

        Assert.IsType<OrderSubmitted>(await rig.Sink.NextOrderEventAsync());
        OrderRejected rejected = Assert.IsType<OrderRejected>(await rig.Sink.NextOrderEventAsync());
        Assert.Contains("insufficientAvailableFunds", rejected.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_order_the_venue_placed_is_accepted_under_the_id_the_venue_gave_it()
    {
        await using FuturesRig rig = await new FuturesRig().ConnectAsync();

        LimitOrder order = rig.Orders.Limit(_perp, OrderSide.Buy, rig.Instrument.MakeQuantity(0.001m), rig.Instrument.MakePrice(50_000m));
        await rig.SubmitAsync(order);

        Assert.IsType<OrderSubmitted>(await rig.Sink.NextOrderEventAsync());
        OrderAccepted accepted = Assert.IsType<OrderAccepted>(await rig.Sink.NextOrderEventAsync());
        Assert.NotNull(accepted.VenueOrderId);
        Assert.Equal("179f9af8-e45e-469d-b3e9-2fd4675cb7d0", accepted.VenueOrderId!.Value.Value);
    }

    // ----- futures: amend and cancel -----

    [Fact]
    public async Task A_futures_amend_goes_to_the_endpoint_that_is_known_to_exist()
    {
        // The one measured fact on this platform's private surface: editorder parsed and refused a malformed order
        // id BEFORE it refused the credentials, which a path that does not exist cannot do - that answers 404
        // NOT_FOUND. So AmendOrders is declared true on evidence rather than on documentation.
        await using FuturesRig rig = await new FuturesRig().ConnectAsync();

        LimitOrder order = rig.Orders.Limit(_perp, OrderSide.Buy, rig.Instrument.MakeQuantity(0.001m), rig.Instrument.MakePrice(50_000m));
        await rig.SubmitAsync(order);

        await rig.Client.ModifyOrderAsync(
            new ModifyOrder(
                rig.Kernel.Services.TraderId, _strategy, _perp, order.ClientOrderId,
                new VenueOrderId("179f9af8-e45e-469d-b3e9-2fd4675cb7d0"),
                rig.Instrument.MakeQuantity(0.002m), rig.Instrument.MakePrice(49_000m), null, null,
                Guid.NewGuid(), TestKernel.Now),
            CancellationToken.None).WaitAsync(Wait.Timeout);

        IReadOnlyDictionary<string, string> amend = Form(rig.Server.RequestsTo("/derivatives/api/v3/editorder").Single());
        Assert.Equal("179f9af8-e45e-469d-b3e9-2fd4675cb7d0", amend["orderId"]);
        Assert.Equal("0.0020", amend["size"]);
        Assert.Equal("49000", amend["limitPrice"]);
    }

    [Fact]
    public async Task A_refused_amend_arrives_inside_a_successful_answer_here_too()
    {
        await using FuturesRig rig = await new FuturesRig().ConnectAsync();
        rig.Routes.On("POST", "/derivatives/api/v3/editorder", _ => StubResponse.Json(KrakenPayloads.EditOrder("invalidSize")));

        LimitOrder order = rig.Orders.Limit(_perp, OrderSide.Buy, rig.Instrument.MakeQuantity(0.001m), rig.Instrument.MakePrice(50_000m));
        await rig.SubmitAsync(order);
        await rig.Sink.NextOrderEventAsync();
        await rig.Sink.NextOrderEventAsync();

        await rig.Client.ModifyOrderAsync(
            new ModifyOrder(
                rig.Kernel.Services.TraderId, _strategy, _perp, order.ClientOrderId, null,
                rig.Instrument.MakeQuantity(0.002m), null, null, null, Guid.NewGuid(), TestKernel.Now),
            CancellationToken.None).WaitAsync(Wait.Timeout);

        Assert.IsType<OrderPendingUpdate>(await rig.Sink.NextOrderEventAsync());
        OrderModifyRejected rejected = Assert.IsType<OrderModifyRejected>(await rig.Sink.NextOrderEventAsync());
        Assert.Contains("invalidSize", rejected.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_futures_cancel_all_names_the_contract_because_this_platform_takes_one()
    {
        // Unlike its spot sibling, which has no symbol on its cancel-all and therefore cannot honour a scoped
        // request as one call. Here the request a caller made is the request the venue receives.
        await using FuturesRig rig = await new FuturesRig().ConnectAsync();

        await rig.Client.CancelAllOrdersAsync(
            new CancelAllOrders(rig.Kernel.Services.TraderId, _strategy, _perp, null, null, Guid.NewGuid(), TestKernel.Now),
            CancellationToken.None).WaitAsync(Wait.Timeout);

        Assert.Equal("PF_XBTUSD", Form(rig.Server.RequestsTo("/derivatives/api/v3/cancelallorders").Single())["symbol"]);
    }

    // ----- futures: the socket challenge -----

    [Fact]
    public async Task The_futures_private_socket_asks_for_a_challenge_before_it_subscribes_to_anything()
    {
        // This socket is signed rather than tokened: the venue issues a challenge and the client answers it. The
        // challenge belongs to the connection, so there is nothing to subscribe with until it has arrived.
        await using FuturesRig rig = await new FuturesRig().ConnectAsync();

        using JsonDocument asked = JsonDocument.Parse(await rig.Session.ReceiveTextAsync(Wait.Timeout));
        Assert.Equal("challenge", asked.RootElement.GetProperty("event").GetString());
        Assert.Equal(Key, asked.RootElement.GetProperty("api_key").GetString());
    }

    [Fact]
    public async Task Every_private_feed_is_subscribed_once_the_challenge_is_answered()
    {
        await using FuturesRig rig = await new FuturesRig().ConnectAsync();
        using JsonDocument asked = JsonDocument.Parse(await rig.Session.ReceiveTextAsync(Wait.Timeout));
        Assert.Equal("challenge", asked.RootElement.GetProperty("event").GetString());

        await rig.Session.SendTextAsync(KrakenPayloads.FuturesChallenge);

        List<string> feeds = new();
        for (int i = 0; i < KrakenFuturesVenue.PrivateFeeds.Count; i++)
        {
            using JsonDocument subscription = JsonDocument.Parse(await rig.Session.ReceiveTextAsync(Wait.Timeout));
            Assert.Equal("subscribe", subscription.RootElement.GetProperty("event").GetString());

            // The challenge travels back beside its signature, and the key with it.
            Assert.Equal("5a2d547e-2226-4193-957e-7b19e32cf5b4", subscription.RootElement.GetProperty("original_challenge").GetString());
            Assert.NotEmpty(subscription.RootElement.GetProperty("signed_challenge").GetString()!);
            feeds.Add(subscription.RootElement.GetProperty("feed").GetString()!);
        }

        Assert.Equal(KrakenFuturesVenue.PrivateFeeds.Order(StringComparer.Ordinal), feeds.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_challenge_answer_is_deterministic_and_covers_the_challenge()
    {
        // The venue refuses a wrong answer and a wrong key with the same words, so there is nothing to tell them
        // apart from outside. What can be checked is that the answer really depends on the challenge - a signer
        // that ignored it would send one constant string for every connection.
        string first = KrakenFuturesOrders.SignChallenge(Secret, "5a2d547e-2226-4193-957e-7b19e32cf5b4");
        string second = KrakenFuturesOrders.SignChallenge(Secret, "00000000-0000-0000-0000-000000000000");

        Assert.NotEqual(first, second);
        Assert.Equal(first, KrakenFuturesOrders.SignChallenge(Secret, "5a2d547e-2226-4193-957e-7b19e32cf5b4"));
        Assert.Equal(64, Convert.FromBase64String(first).Length);
    }

    [Fact]
    public async Task A_refused_private_subscription_is_reported_rather_than_retried_blindly()
    {
        await using FuturesRig rig = await new FuturesRig().ConnectAsync();
        await rig.Session.ReceiveTextAsync(Wait.Timeout);

        await rig.Session.SendTextAsync(KrakenPayloads.FuturesAuthFailed);

        // The challenge behind it proves the alert was read: messages are handled in order, so a subscription
        // arriving back means the alert has already been through the handler.
        await rig.Session.SendTextAsync(KrakenPayloads.FuturesChallenge);
        await rig.Session.ReceiveTextAsync(Wait.Timeout);

        Assert.True(rig.Client.IsConnected);
        Assert.Contains(rig.Logs.Warnings, w => w.Contains("authenticated feed", StringComparison.Ordinal));
    }

    // ----- futures: the account and its positions -----

    [Fact]
    public async Task The_multi_collateral_account_is_the_one_this_family_is_margined_out_of()
    {
        // The platform keeps several accounts under one key - one per coin-margined collateral plus this one - and
        // answers all of them in one call. A client that took the first would publish the balances of a family this
        // adapter does not offer.
        await using FuturesRig rig = await new FuturesRig().ConnectAsync();

        AccountState state = await rig.Sink.NextAccountStateAsync();

        Assert.Equal(AccountType.Margin, rig.Client.AccountType);
        Assert.Contains(state.Balances, b => b.Currency.Code == "USD" && b.Total.Amount == 100_000m);
        Assert.Contains(state.Balances, b => b.Currency.Code == "BTC" && b.Total.Amount == 0.5m);

        // And the locked part is what the account cannot use, which the venue states as what it can.
        Assert.Contains(state.Balances, b => b.Currency.Code == "USD" && b.Locked.Amount == 1_000m);
    }

    [Fact]
    public async Task This_family_holds_positions_and_reports_them()
    {
        await using FuturesRig rig = await new FuturesRig().ConnectAsync();
        rig.Routes.On("GET", "/derivatives/api/v3/openpositions", KrakenPayloads.OpenPositions);

        PositionStatusReport position = Assert.Single(
            await rig.Client.GeneratePositionStatusReportsAsync(null, null, null, CancellationToken.None));

        Assert.Equal(_perp, position.InstrumentId);
        Assert.Equal(PositionSide.Long, position.PositionSide);
        Assert.Equal(0.0006m, position.Quantity.Value);
        Assert.Equal(50_000m, position.AvgPxOpen);
    }

    [Fact]
    public async Task An_order_report_adds_the_filled_and_unfilled_parts_back_together()
    {
        // The venue publishes what is LEFT and what has FILLED, never the original size. Taking `unfilledSize` as
        // the quantity would make every partially filled order look smaller than it was placed at, and
        // reconciliation would then believe the strategy had asked for less than it did.
        await using FuturesRig rig = await new FuturesRig().ConnectAsync();
        rig.Routes.On("GET", "/derivatives/api/v3/openorders", _ => StubResponse.Json(KrakenPayloads.OpenOrders("O-TEST-1")));

        OrderStatusReport report = Assert.Single(
            await rig.Client.GenerateOrderStatusReportsAsync(null, null, null, openOnly: true, CancellationToken.None));

        Assert.Equal(0.001m, report.Quantity.Value);
        Assert.Equal(0.0006m, report.FilledQuantity.Value);
        Assert.Equal(OrderStatus.PartiallyFilled, report.OrderStatus);
        Assert.Equal("O-TEST-1", report.ClientOrderId!.Value.Value);
    }

    [Fact]
    public async Task A_futures_fill_report_carries_the_venues_own_fill_id()
    {
        await using FuturesRig rig = await new FuturesRig().ConnectAsync();
        rig.Routes.On("GET", "/derivatives/api/v3/fills", _ => StubResponse.Json(KrakenPayloads.Fills("O-TEST-1")));

        FillReport fill = Assert.Single(await rig.Client.GenerateFillReportsAsync(null, null, null, null, CancellationToken.None));

        Assert.Equal("c14ee7cb-ae25-4029-b8cf-32a2a0b45dd9", fill.TradeId.Value);
        Assert.Equal(_perp, fill.InstrumentId);
        Assert.Equal(0.0006m, fill.LastQty.Value);
        Assert.Equal(LiquiditySide.Taker, fill.LiquiditySide);
    }

    // ----- the two clients of one venue, compared -----

    [Fact]
    public void The_two_execution_clients_answer_the_same_commands()
    {
        // The parity table finds a venue's clients by naming convention, so a venue with two of one kind has one
        // the table cannot see. The futures client is that one here, and this is what keeps it honest against its
        // sibling: only the commands where the two genuinely differ may differ.
        string[] spot = Commands.Overridden(typeof(KrakenExecutionClient));
        string[] futures = Commands.Overridden(typeof(KrakenFuturesExecutionClient));

        // Cancel-all, because only the futures platform's takes a symbol; and position reports, because only the
        // futures account holds a position.
        Assert.Equal(
            ["CancelAllOrdersAsync", "GeneratePositionStatusReportsAsync"],
            futures.Except(spot, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());

        Assert.Empty(spot.Except(futures, StringComparer.Ordinal));
    }
}
