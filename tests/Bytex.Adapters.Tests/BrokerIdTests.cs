using System.Text.Json;
using Bytex.Adapters.Binance;
using Bytex.Adapters.Bitget;
using Bytex.Adapters.Bybit;
using Bytex.Adapters.Kucoin;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Trading;

namespace Bytex.Adapters.Tests;

// Why (R11.13): a broker or partner id has to travel on the orders an adapter sends, and every venue carries one a
// different way - Binance prefixes the client order id, Bybit puts it in a header, KuCoin needs a signed credential
// on every request and is not carried here at all. The declaration states the mechanism per venue; only the id is
// configured, and nothing above an adapter has to know which venues have a programme.
//
// WHETHER a venue pays is a different question from HOW an adapter carries an id, and BrokerProgrammeTests holds
// that one. They were the same field once, and reading "nothing carries an id here" as "this venue has no
// programme" is how a false statement about a venue that runs two broker tiers came to be written down twice.
//
// Two properties matter more than the tagging itself.
//
// UNTAGGED MUST BE UNCHANGED. Almost nobody will configure one, so if turning the field on could alter what a venue
// receives while it is off, this would change how every order is placed for everybody who does not use it.
//
// AND A PREFIX CHANGES AN ORDER'S IDENTITY. On Binance the id goes in front of the client order id, which is the key
// reconciliation matches an order on. Sending it is the easy half: everything that later names that order has to
// name it the same way, and everything the venue says about it has to be translated back. Six places on that venue
// send or read one, which is why the translation is one pair of methods - and why a fill arriving under the prefixed
// id still has to find the engine's order, which is the test that makes the prefix safe rather than merely present.
public sealed class BrokerIdTests
{
    private const string Broker = "bytex-";

    [Fact]
    public void Every_venue_declares_how_it_would_carry_a_broker_id()
    {
        // Declared whether or not an id is configured: an adapter that cannot say how it would carry one has not
        // thought about it, and a host cannot tell "this venue has no programme" from "nobody looked".
        Assert.Equal(BrokerTag.ClientOrderIdPrefix, new BinancePlugin().Describe().BrokerTag);
        Assert.Equal(BrokerTag.RequestHeader, new BybitPlugin().Describe().BrokerTag);

        // A header here too, under the venue's own name for it. Measured to be accepted and ignored on a request
        // carrying no key, so a code that is not a real one cannot break a request.
        Assert.Equal(BrokerTag.RequestHeader, new BitgetPlugin().Describe().BrokerTag);
        Assert.Equal("X-CHANNEL-API-CODE", BitgetVenue.BrokerIdHeader);

        // Nothing carries one here. That this venue nevertheless HAS a programme is the other field's answer, and
        // the reason the two were separated.
        Assert.Equal(BrokerTag.None, new KucoinPlugin().Describe().BrokerTag);
    }

    // ----- Binance: a prefix on the client order id -----

    private static string? SentOid(BinanceExecRig rig) =>
        rig.Server.RequestsTo("/api/v3/order").Last().Query("newClientOrderId");

    [Fact]
    public async Task Binance_sends_the_engines_own_id_when_no_broker_id_is_configured()
    {
        await using BinanceExecRig rig = new(BinanceAccountType.Spot);
        await rig.ConnectAsync("""{"balances":[{"asset":"USDT","free":"100000","locked":"0"}]}""");
        rig.Routes.On("POST", "/api/v3/order", """{"orderId":1,"status":"NEW"}""");

        MarketOrder order = rig.Orders.Market(rig.Instrument.Id, OrderSide.Buy, rig.Qty(0.01m));
        await rig.SubmitAsync(order);

        // Exactly what this venue received before the field existed.
        Assert.Equal(order.ClientOrderId.Value, SentOid(rig));
    }

    [Fact]
    public async Task Binance_prefixes_the_client_order_id_with_the_broker_id()
    {
        await using BinanceExecRig rig = new(BinanceAccountType.Spot, brokerId: Broker);
        await rig.ConnectAsync("""{"balances":[{"asset":"USDT","free":"100000","locked":"0"}]}""");
        rig.Routes.On("POST", "/api/v3/order", """{"orderId":1,"status":"NEW"}""");

        MarketOrder order = rig.Orders.Market(rig.Instrument.Id, OrderSide.Buy, rig.Qty(0.01m));
        await rig.SubmitAsync(order);

        Assert.Equal(Broker + order.ClientOrderId.Value, SentOid(rig));
    }

    [Fact]
    public async Task A_cancel_names_the_order_by_the_id_the_venue_was_given()
    {
        // The prefix is not only for placing. A cancel that named the engine's id would be cancelling an order this
        // venue has never heard of, and the venue would refuse it - leaving a live order nobody could reach.
        await using BinanceExecRig rig = new(BinanceAccountType.Spot, brokerId: Broker);
        await rig.ConnectAsync("""{"balances":[{"asset":"USDT","free":"100000","locked":"0"}]}""");
        rig.Routes
            .On("POST", "/api/v3/order", """{"orderId":1,"status":"NEW"}""")
            .On("DELETE", "/api/v3/order", """{"orderId":1,"status":"CANCELED"}""");

        MarketOrder order = rig.Orders.Market(rig.Instrument.Id, OrderSide.Buy, rig.Qty(0.01m));
        await rig.SubmitAsync(order);

        await rig.Client.CancelOrderAsync(
            new CancelOrder(rig.Kernel.Services.TraderId, BinanceExecRig.Strategy, order.InstrumentId, order.ClientOrderId, null, null, Guid.NewGuid(), TestKernel.Now),
            CancellationToken.None).WaitAsync(Wait.Timeout);

        Assert.Equal(
            Broker + order.ClientOrderId.Value,
            rig.Server.RequestsTo("/api/v3/order").Last(r => r.Method == "DELETE").Query("origClientOrderId"));
    }

    [Fact]
    public async Task A_fill_arriving_under_the_prefixed_id_still_finds_the_engines_order()
    {
        // The half that makes the prefix safe. The venue reports this order under the id IT was given, which is not
        // the id the engine knows - so without translating it back a fill would be attributed to no order at all,
        // and a position would never open. This is what makes turning a broker id on a reconciliation question
        // rather than a formatting one.
        await using BinanceExecRig rig = new(BinanceAccountType.Spot, brokerId: Broker);
        WsSession session = await rig.ConnectAsync("""{"balances":[{"asset":"USDT","free":"100000","locked":"0"}]}""");
        rig.Routes.On("POST", "/api/v3/order", """{"orderId":1,"status":"NEW"}""");

        MarketOrder order = rig.Orders.Market(rig.Instrument.Id, OrderSide.Buy, rig.Qty(0.01m));
        await rig.SubmitAsync(order);
        Assert.IsType<OrderSubmitted>(await rig.Sink.NextOrderEventAsync());

        await session.SendTextAsync(
            """
            {"e":"executionReport","E":1700000000000,"s":"BTCUSDT","c":"OID","S":"BUY","o":"MARKET","f":"GTC",
             "q":"0.01000000","p":"0.00000000","x":"TRADE","X":"FILLED","i":1,"l":"0.01000000","z":"0.01000000",
             "L":"50000.00","n":"0.50","N":"USDT","T":1700000000000,"t":11,"m":false}
            """.Replace("OID", Broker + order.ClientOrderId.Value, StringComparison.Ordinal));

        OrderFilled fill = Assert.IsType<OrderFilled>(await rig.Sink.NextOrderEventAsync());

        // The engine's id, not the one the venue was given.
        Assert.Equal(order.ClientOrderId, fill.ClientOrderId);
        Assert.DoesNotContain(Broker, fill.ClientOrderId.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_broker_id_that_pushes_the_client_order_id_over_the_venues_limit_is_refused_before_it_is_sent()
    {
        await using BinanceExecRig rig = new(BinanceAccountType.Spot, brokerId: new string('b', 30));
        await rig.ConnectAsync("""{"balances":[{"asset":"USDT","free":"100000","locked":"0"}]}""");
        rig.Routes.On("POST", "/api/v3/order", """{"orderId":1,"status":"NEW"}""");

        await rig.SubmitAsync(rig.Orders.Market(rig.Instrument.Id, OrderSide.Buy, rig.Qty(0.01m)));

        OrderRejected rejected = Assert.IsType<OrderRejected>(await rig.Sink.NextOrderEventAsync());
        Assert.Contains("broker id prefix", rejected.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(rig.Server.RequestsTo("/api/v3/order"), r => r.Method == "POST");
    }

    [Fact]
    public async Task The_prefix_travels_on_the_coin_margined_family_too_and_comes_back_off_again()
    {
        // The mechanism is the venue's and not a family's, so the same prefix has to reach the coin-margined host
        // and the same translation has to undo it. Worth its own test rather than assumed from the spot one: this
        // family's order path is a different path and its reports arrive as a different event, and a prefix that
        // went out and did not come back would leave every fill attributed to no order at all.
        await using BinanceExecRig rig = new(BinanceAccountType.CoinMFutures, brokerId: Broker);
        WsSession session = await rig.ConnectAsync("[]");
        rig.Routes
            .On("POST", "/dapi/v1/order", """{"orderId":1,"status":"NEW"}""")
            .On("DELETE", "/dapi/v1/order", """{"orderId":1,"status":"CANCELED"}""");

        // One whole contract, which is what this family trades in.
        MarketOrder order = rig.Orders.Market(rig.Instrument.Id, OrderSide.Buy, rig.Qty(1m));
        await rig.SubmitAsync(order);
        Assert.IsType<OrderSubmitted>(await rig.Sink.NextOrderEventAsync());

        RecordedRequest placed = rig.Server.RequestsTo("/dapi/v1/order").Last(r => r.Method == "POST");
        Assert.Equal(Broker + order.ClientOrderId.Value, placed.Query("newClientOrderId"));
        Assert.Equal("BTCUSD_PERP", placed.Query("symbol"));
        Assert.Equal("1", placed.Query("quantity"));

        // A cancel names the order by the id the venue was given, or it cancels an order this host never heard of.
        await rig.Client.CancelOrderAsync(
            new CancelOrder(rig.Kernel.Services.TraderId, BinanceExecRig.Strategy, order.InstrumentId, order.ClientOrderId, null, null, Guid.NewGuid(), TestKernel.Now),
            CancellationToken.None).WaitAsync(Wait.Timeout);

        Assert.Equal(
            Broker + order.ClientOrderId.Value,
            rig.Server.RequestsTo("/dapi/v1/order").Last(r => r.Method == "DELETE").Query("origClientOrderId"));

        // The cancel's own event, named by the ENGINE's id: the prefix is the venue's business and must not reach
        // anything a strategy reads.
        OrderPendingCancel pending = Assert.IsType<OrderPendingCancel>(await rig.Sink.NextOrderEventAsync());
        Assert.Equal(order.ClientOrderId, pending.ClientOrderId);

        // And the venue's report of that order, under the id IT was given, still finds the engine's order.
        await session.SendTextAsync(CoinMOrderUpdate.Replace("OID", Broker + order.ClientOrderId.Value, StringComparison.Ordinal));

        OrderFilled fill = Assert.IsType<OrderFilled>(await rig.Sink.NextOrderEventAsync());
        Assert.Equal(order.ClientOrderId, fill.ClientOrderId);
        Assert.DoesNotContain(Broker, fill.ClientOrderId.Value, StringComparison.Ordinal);

        // In the coin, because a coin-margined contract is charged in the coin it settles in.
        Assert.Equal(Core.Model.Primitives.Currencies.BTC, fill.Commission.Currency);
    }

    [Fact]
    public async Task The_length_limit_is_applied_on_the_coin_margined_family_before_an_order_is_sent()
    {
        // The same limit and the same refusal. The length at which this family refuses an id could not be measured
        // - an order cannot be placed without a key - so the venue's documented 36 is applied, and the direction is
        // the safe one: a refusal names both of the things that were too long, where a truncated id is an order the
        // engine can no longer match to anything the venue says about it.
        await using BinanceExecRig rig = new(BinanceAccountType.CoinMFutures, brokerId: new string('b', 30));
        await rig.ConnectAsync("[]");
        rig.Routes.On("POST", "/dapi/v1/order", """{"orderId":1,"status":"NEW"}""");

        await rig.SubmitAsync(rig.Orders.Market(rig.Instrument.Id, OrderSide.Buy, rig.Qty(1m)));

        OrderRejected rejected = Assert.IsType<OrderRejected>(await rig.Sink.NextOrderEventAsync());
        Assert.Contains("broker id prefix", rejected.Reason, StringComparison.Ordinal);
        Assert.Contains(
            BinanceVenue.MaxClientOrderIdLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
            rejected.Reason,
            StringComparison.Ordinal);
        Assert.DoesNotContain(rig.Server.RequestsTo("/dapi/v1/order"), r => r.Method == "POST");
    }

    /// <summary>
    /// The coin-margined family's own order report, in the shape its documentation gives: an ORDER_TRADE_UPDATE
    /// whose <c>o</c> object carries the same fields this adapter reads on the USD-margined family, plus a margin
    /// asset and a position side it does not read.
    /// <para>
    /// Written from documentation rather than recorded, and that limit is worth saying out loud: an order event
    /// cannot be produced on the live venue without a key and an order. What it is here for is the TRANSLATION
    /// rather than the venue's spelling - the prefixed id going out and the engine's own id coming back.
    /// </para>
    /// </summary>
    private const string CoinMOrderUpdate = """
        {"e":"ORDER_TRADE_UPDATE","E":1790373660000,"T":1790373659999,"o":{
          "s":"BTCUSD_PERP","c":"OID","S":"BUY","o":"MARKET","f":"GTC","q":"1","p":"0","ap":"83732.5",
          "x":"TRADE","X":"FILLED","i":1,"l":"1","z":"1","L":"83732.5","n":"0.0000048","N":"BTC",
          "T":1790373659999,"t":11,"b":"0","a":"0","m":false,"R":false,"wt":"CONTRACT_PRICE",
          "ot":"MARKET","ps":"BOTH","cp":false,"rp":"0","pP":false,"ma":"BTC"}}
        """;

    // ----- Bybit: a header, outside the signature -----

    [Fact]
    public async Task Bybit_carries_the_broker_id_in_a_header_and_leaves_the_order_itself_alone()
    {
        await using BybitExecRig rig = new(BybitProductType.Spot, brokerId: Broker);
        await rig.ConnectAsync("""{"list":[]}""");
        rig.Routes.On("POST", "/v5/order/create", BybitPayloads.Envelope("""{"orderId":"1","orderLinkId":"x"}"""));

        await rig.SubmitAsync(rig.Orders.Market(rig.Instrument.Id, OrderSide.Buy, rig.Qty(0.01m)));

        RecordedRequest request = rig.Server.RequestsTo("/v5/order/create").Last();
        Assert.Equal(Broker, request.Header(BybitVenue.BrokerIdHeader));

        // Nowhere in the order, so nothing about the order's identity changed - which is why this venue needs no
        // reconciliation test where Binance's mechanism does.
        Assert.DoesNotContain(Broker, request.Body, StringComparison.Ordinal);

        // And the request is still signed the way this venue signs every other one: the id rides beside the
        // signature rather than inside it, so the signed payload is untouched.
        BybitExecRig.AssertSigned(request);
    }

    [Fact]
    public async Task Bybit_sends_no_such_header_when_no_broker_id_is_configured()
    {
        await using BybitExecRig rig = new(BybitProductType.Spot);
        await rig.ConnectAsync("""{"list":[]}""");
        rig.Routes.On("POST", "/v5/order/create", BybitPayloads.Envelope("""{"orderId":"1","orderLinkId":"x"}"""));

        await rig.SubmitAsync(rig.Orders.Market(rig.Instrument.Id, OrderSide.Buy, rig.Qty(0.01m)));

        RecordedRequest request = rig.Server.RequestsTo("/v5/order/create").Last();
        Assert.Null(request.Header(BybitVenue.BrokerIdHeader));
        BybitExecRig.AssertSigned(request);
    }

    // ----- KuCoin: no programme, so a no-op -----

    [Fact]
    public async Task KuCoin_ignores_a_broker_id_rather_than_refusing_it()
    {
        // The owner's ruling: a venue with no broker id supported uses a default no-op. Configuring one is never an
        // error - nothing above an adapter has to know which venues have a programme - and it changes nothing about
        // what this venue receives.
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/api/v1/contracts/active", KucoinPayloads.FuturesContracts)
            .On("GET", "/api/v1/account-overview", KucoinPayloads.Envelope("""{"accountEquity":1.0,"availableBalance":1.0}"""))
            .On("POST", "/api/v1/orders", KucoinPayloads.Envelope("""{"orderId":"1"}"""))
            .Handle);

        using TestKernel kernel = new();
        KucoinFuturesExecutionClient client = new(new ClientId("KUCOIN"), new KucoinExecutionClientConfig
        {
            ProductType = KucoinProductType.Futures,
            ApiKey = "6705f5c311545b000157d3eb",
            ApiSecret = "c1f1e3e4-5a6b-4c7d-8e9f-0a1b2c3d4e5f",
            ApiPassphrase = "bytex-test",
            BrokerId = Broker,
            BaseUrlHttp = server.HttpBase,
            InstrumentProvider = new InstrumentProviderConfig { LoadAll = true },
        }, kernel.Services);

        RecordingExecutionSink sink = new();
        client.AttachSink(sink);
        await client.Instruments.LoadAllAsync(CancellationToken.None);

        Instrument contract = client.Instruments.Find(InstrumentId.Parse("XBTUSDT-PERP.KUCOIN"))!;
        StrategyId strategy = new("Probe-001");
        OrderFactory orders = new(kernel.Services.TraderId, strategy, kernel.Clock);
        MarketOrder order = orders.Market(contract.Id, OrderSide.Buy, contract.MakeQuantity(0.001m));
        kernel.Kernel.Cache.AddOrder(order);

        await client.SubmitOrderAsync(
            new SubmitOrder(kernel.Services.TraderId, strategy, order, null, null, null, Guid.NewGuid(), TestKernel.Now),
            CancellationToken.None).WaitAsync(Wait.Timeout);

        RecordedRequest sent = server.RequestsTo("/api/v1/orders").Last();

        // Not in the order, not in a header, and the order's own id untouched.
        Assert.DoesNotContain(Broker, sent.Body, StringComparison.Ordinal);
        Assert.Null(sent.Header(BybitVenue.BrokerIdHeader));

        using JsonDocument body = JsonDocument.Parse(sent.Body);
        Assert.Equal(order.ClientOrderId.Value, body.RootElement.GetProperty("clientOid").GetString());

        client.Dispose();
    }
}
