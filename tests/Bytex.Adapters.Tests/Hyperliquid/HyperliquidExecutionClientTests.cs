using System.Text.Json;
using Bytex.Adapters.Hyperliquid;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Adapters;
using Bytex.Core.Model.Reports;
using Bytex.Core.Trading;

namespace Bytex.Adapters.Tests.Hyperliquid;

// Why: this is the venue where an order is signed rather than authenticated, where the asset is a number rather than
// a symbol, where the client order id is a number rather than a string, and where THERE IS NO MARKET ORDER.
//
// Every one of those is a silent wrong outcome rather than an error. An asset index off by one is a real order on a
// contract nobody chose. A client order id sent as the engine's string is refused at the deserialiser, before the
// signature, with a message about the JSON body. A market order sent as a market order does not exist, so something
// has to invent a price, and whatever it invents is what fills.
//
// The signing itself is covered by HyperliquidSigningTests against the live venue. This file is about what the
// execution client puts INSIDE the signature, and about what it does with an answer.
public sealed class HyperliquidExecutionClientTests
{
    /// <summary>A key nobody has used. It never leaves this process; only the signature does.</summary>
    private const string ProbeKey = "0x1111111111111111111111111111111111111111111111111111111111111112";

    private sealed class Rig : IAsyncDisposable
    {
        private WsSession? _session;

        public Rig(decimal? leverage = null, bool crossMargin = true)
        {
            Server = new LoopbackServer(Handle);
            Kernel = new TestKernel();
            Client = new HyperliquidExecutionClient(new ClientId("HYPERLIQUID"), new HyperliquidExecutionClientConfig
            {
                PrivateKey = ProbeKey,
                Leverage = leverage,
                CrossMargin = crossMargin,
                BaseUrlHttp = Server.HttpBase,
                BaseUrlWs = Server.WsBase,
                InstrumentProvider = new Core.Adapters.InstrumentProviderConfig { LoadAll = true },
            }, Kernel.Services);

            Client.AttachSink(Sink);
            Orders = new OrderFactory(Kernel.Services.TraderId, Strategy, Kernel.Clock);
        }

        public static StrategyId Strategy { get; } = new("Probe-001");

        public LoopbackServer Server { get; }

        public TestKernel Kernel { get; }

        public HyperliquidExecutionClient Client { get; }

        public RecordingExecutionSink Sink { get; } = new();

        public OrderFactory Orders { get; }

        public WsSession Session => _session ?? throw new InvalidOperationException("not connected");

        public Instrument Btc => Client.Instruments.Find(InstrumentId.Parse("BTC-PERP.HYPERLIQUID"))!;

        public Instrument Sol => Client.Instruments.Find(InstrumentId.Parse("SOL-PERP.HYPERLIQUID"))!;

        /// <summary>One handler for both endpoints, because this venue has exactly two.</summary>
        private StubResponse Handle(RecordedRequest request)
        {
            if (request.Path == HyperliquidVenue.ExchangePath)
            {
                return StubResponse.Json(HyperliquidPayloads.ExchangeOrderAccepted);
            }

            if (request.Path != HyperliquidVenue.InfoPath)
            {
                return StubResponse.Error(404, "no such path");
            }

            using JsonDocument doc = JsonDocument.Parse(request.Body);
            return doc.RootElement.GetProperty("type").GetString() switch
            {
                HyperliquidReads.Meta => StubResponse.Json(HyperliquidPayloads.Meta),
                HyperliquidReads.ClearinghouseState => StubResponse.Json(HyperliquidPayloads.ClearinghouseState),
                HyperliquidReads.FrontendOpenOrders => StubResponse.Json(HyperliquidPayloads.NoOpenOrders),
                HyperliquidReads.UserFills => StubResponse.Json(HyperliquidPayloads.NoOpenOrders),
                HyperliquidReads.L2Book => StubResponse.Json(HyperliquidPayloads.BtcBook),
                _ => StubResponse.Error(422, HyperliquidPayloads.InfoUnknownType),
            };
        }

        public async Task<Rig> ConnectAsync()
        {
            await Client.ConnectAsync(CancellationToken.None).WaitAsync(Wait.Timeout);
            _session = await Server.NextSessionAsync();
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

        /// <summary>The last signed action the venue was sent, as the venue would read it.</summary>
        public JsonElement LastAction()
        {
            using JsonDocument doc = JsonDocument.Parse(Server.RequestsTo(HyperliquidVenue.ExchangePath).Last().Body);
            return doc.RootElement.GetProperty("action").Clone();
        }

        public JsonElement LastBody()
        {
            using JsonDocument doc = JsonDocument.Parse(Server.RequestsTo(HyperliquidVenue.ExchangePath).Last().Body);
            return doc.RootElement.Clone();
        }

        public IReadOnlyList<JsonElement> Actions() =>
            [.. Server.RequestsTo(HyperliquidVenue.ExchangePath).Select(r =>
            {
                using JsonDocument doc = JsonDocument.Parse(r.Body);
                return doc.RootElement.GetProperty("action").Clone();
            })];

        public async ValueTask DisposeAsync()
        {
            await Client.DisconnectAsync(CancellationToken.None);
            Client.Dispose();
            Kernel.Dispose();
            await Server.DisposeAsync();
        }
    }

    /// <summary>
    /// Long enough for a message the socket has already delivered to have been handled. Used only where the
    /// assertion is that NOTHING was reported, which cannot be waited for by waiting for something.
    /// </summary>
    private static Task Settle() => Task.Delay(300);

    /// <summary>An order update on the account's own stream, with this adapter's client order id hashed into it.</summary>
    private static string OrderUpdate(ClientOrderId clientOrderId, string status = "open") =>
        HyperliquidPayloads.OrderUpdatesChannel
            .Replace("CLOID", HyperliquidVenue.CloidFor(clientOrderId), StringComparison.Ordinal)
            .Replace("\"status\":\"open\"", $"\"status\":\"{status}\"", StringComparison.Ordinal);

    private static string Fill(ClientOrderId clientOrderId) =>
        HyperliquidPayloads.UserFillsChannel.Replace("CLOID", HyperliquidVenue.CloidFor(clientOrderId), StringComparison.Ordinal);

    // ----- what a fractional leverage does -----

    [Fact]
    public void A_fractional_leverage_is_refused_when_the_client_is_built()
    {
        // MEASURED: an updateLeverage carrying 5.5 comes back at HTTP 422 with "Failed to deserialize the JSON body
        // into the target type" - before the signature is looked at, because the field is a whole number in the
        // venue's own type. The shared configuration carries a decimal because a strategy document does, so the
        // refusal has to be here rather than on the first order, or a node would start and then fail to set the
        // leverage it was written for.
        using TestKernel kernel = new();

        ArgumentException error = Assert.Throws<ArgumentException>(() => new HyperliquidExecutionClient(
            new ClientId("HYPERLIQUID"),
            new HyperliquidExecutionClientConfig { PrivateKey = ProbeKey, Leverage = 5.5m },
            kernel.Services));

        Assert.Contains("whole-number leverage", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_whole_leverage_written_as_a_decimal_is_accepted()
    {
        using TestKernel kernel = new();
        HyperliquidExecutionClient client = new(
            new ClientId("HYPERLIQUID"),
            new HyperliquidExecutionClientConfig { PrivateKey = ProbeKey, Leverage = 3.0m },
            kernel.Services);

        Assert.NotNull(client);
        client.Dispose();
    }

    [Fact]
    public void An_execution_client_without_a_key_is_refused_rather_than_left_unable_to_write()
    {
        // Reading this venue needs nothing, so a data client is happy with no credential - but an execution client
        // that cannot sign can do nothing at all, and finding that out on the first order would be worse.
        using TestKernel kernel = new();

        Assert.ThrowsAny<Exception>(() => new HyperliquidExecutionClient(
            new ClientId("HYPERLIQUID"),
            new HyperliquidExecutionClientConfig(),
            kernel.Services));
    }

    // ----- the account -----

    [Fact]
    public async Task The_account_is_read_without_the_key_being_used_at_all()
    {
        // The reverse of every other venue here: positions, orders and balances are PUBLIC reads keyed by an
        // address. So the account state request carries the account and nothing else - no signature, no timestamp,
        // no header.
        await using Rig rig = await new Rig().ConnectAsync();

        RecordedRequest read = rig.Server.RequestsTo(HyperliquidVenue.InfoPath)
            .Single(r => r.Body.Contains(HyperliquidReads.ClearinghouseState, StringComparison.Ordinal));

        Assert.DoesNotContain("signature", read.Body, StringComparison.OrdinalIgnoreCase);

        using JsonDocument body = JsonDocument.Parse(read.Body);
        Assert.Equal(
            HyperliquidVenue.Credentials(new HyperliquidExecutionClientConfig { PrivateKey = ProbeKey }).Account,
            body.RootElement.GetProperty("user").GetString());
    }

    [Fact]
    public async Task The_account_state_carries_one_collateral_balance_and_the_two_margin_numbers_beside_it()
    {
        // One pool of collateral for the whole account, so there is one balance. The venue's own margin numbers go
        // in the info rather than into a margin balance, because a MarginBalance is per instrument and this
        // venue's cross margin is not - the collateral behind a BTC position and an ETH position is the same
        // collateral.
        await using Rig rig = await new Rig().ConnectAsync();

        AccountState state = await rig.Sink.NextAccountStateAsync();
        AccountBalance balance = Assert.Single(state.Balances);

        Assert.Equal("USDC", balance.Total.Currency.Code);
        Assert.Equal(38252.7m, balance.Total.Amount);
        Assert.Equal(37766.7m, balance.Locked.Amount);
        Assert.Empty(state.Margins);
        Assert.Equal("37766.7", state.Info["totalMarginUsed"]);
        Assert.Equal("4720.8375", state.Info["crossMaintenanceMarginUsed"]);
    }

    [Fact]
    public async Task The_position_is_read_from_the_signed_size_so_a_short_is_a_short()
    {
        // The venue signs a position's size - negative is short - and this is the only signed size on it. Reading
        // it unsigned would report every short as a long of the same size.
        await using Rig rig = await new Rig().ConnectAsync();

        IReadOnlyList<PositionStatusReport> reports = await rig.Client
            .GeneratePositionStatusReportsAsync(null, null, null, CancellationToken.None);

        PositionStatusReport report = Assert.Single(reports);
        Assert.Equal(InstrumentId.Parse("BTC-PERP.HYPERLIQUID"), report.InstrumentId);
        Assert.Equal(PositionSide.Long, report.PositionSide);
        Assert.Equal(4.5m, report.Quantity.Value);
        Assert.Equal(81285m, report.AvgPxOpen);
    }

    [Fact]
    public async Task A_leverage_is_set_per_asset_with_the_margin_mode_in_the_same_call()
    {
        // Per asset and not per account, and the two cannot be separated: whoever sets a leverage here has also
        // said whether the position shares the account's collateral. Five instruments, five calls.
        await using Rig rig = await new Rig(leverage: 5m).ConnectAsync();

        IReadOnlyList<JsonElement> actions = rig.Actions();
        Assert.Equal(5, actions.Count);
        Assert.All(actions, a => Assert.Equal("updateLeverage", a.GetProperty("type").GetString()));
        Assert.All(actions, a => Assert.True(a.GetProperty("isCross").GetBoolean()));
        Assert.Equal([0, 1, 2, 4, 5], actions.Select(a => a.GetProperty("asset").GetInt32()).Order().ToArray());
        Assert.All(actions, a => Assert.Equal(5, a.GetProperty("leverage").GetInt32()));
    }

    [Fact]
    public async Task A_leverage_past_an_assets_own_maximum_leaves_that_asset_alone()
    {
        // The venue publishes a maximum per asset - 40x on BTC, 5x on ATOM - so one configured number is legal on
        // some and not on others. Refusing the whole connection over one of them would be worse than leaving that
        // one on whatever it had.
        await using Rig rig = await new Rig(leverage: 25m).ConnectAsync();

        int[] assets = [.. rig.Actions().Select(a => a.GetProperty("asset").GetInt32()).Order()];

        // BTC at 40x and ETH at 25x take it; ATOM and DYDX at 5x and SOL at 20x do not.
        Assert.Equal([0, 1], assets);
    }

    [Fact]
    public async Task Isolated_margin_is_what_the_configuration_says_rather_than_a_silent_default()
    {
        // Defaulting this silently to isolated would move every position off the shared collateral it was sized
        // against, which is why it is a field of its own rather than being inferred.
        await using Rig rig = await new Rig(leverage: 5m, crossMargin: false).ConnectAsync();

        Assert.All(rig.Actions(), a => Assert.False(a.GetProperty("isCross").GetBoolean()));
    }

    // ----- an order -----

    [Fact]
    public async Task A_limit_order_names_its_asset_by_index_and_its_price_and_size_as_strings()
    {
        // The three things about an order that are this venue's own. The asset is a NUMBER; the price and the size
        // are STRINGS - measured: a numeric price is refused at HTTP 422 before the signature - and the client
        // order id is a 128-bit number rather than the engine's string.
        await using Rig rig = await new Rig().ConnectAsync();
        LimitOrder order = rig.Track(rig.Orders.Limit(rig.Btc.Id, OrderSide.Buy, rig.Btc.MakeQuantity(0.01m), rig.Btc.MakePrice(83000m)));

        await rig.SubmitAsync(order);

        JsonElement action = rig.LastAction();
        Assert.Equal("order", action.GetProperty("type").GetString());
        Assert.Equal("na", action.GetProperty("grouping").GetString());

        JsonElement wire = action.GetProperty("orders")[0];
        Assert.Equal(0, wire.GetProperty("a").GetInt32());
        Assert.True(wire.GetProperty("b").GetBoolean());
        Assert.Equal("83000", wire.GetProperty("p").GetString());
        Assert.Equal("0.01", wire.GetProperty("s").GetString());
        Assert.False(wire.GetProperty("r").GetBoolean());
        Assert.Equal("Gtc", wire.GetProperty("t").GetProperty("limit").GetProperty("tif").GetString());
        Assert.Equal(HyperliquidVenue.CloidFor(order.ClientOrderId), wire.GetProperty("c").GetString());

        // And the signature beside it, which is what makes the request a request at all.
        JsonElement body = rig.LastBody();
        Assert.True(body.GetProperty("nonce").GetInt64() > 0);
        Assert.Equal(66, body.GetProperty("signature").GetProperty("r").GetString()!.Length);
        Assert.InRange(body.GetProperty("signature").GetProperty("v").GetInt32(), 27, 28);
    }

    [Fact]
    public async Task An_order_on_an_asset_past_the_delisted_one_carries_the_index_the_venue_gave_it()
    {
        // The defect this guards is a real order on the wrong contract. SOL is index 5 because the delisted MATIC
        // still holds index 3 - and an adapter that filtered before numbering would send this order to index 4,
        // which is DYDX.
        await using Rig rig = await new Rig().ConnectAsync();
        LimitOrder order = rig.Track(rig.Orders.Limit(rig.Sol.Id, OrderSide.Buy, rig.Sol.MakeQuantity(1m), rig.Sol.MakePrice(120m)));

        await rig.SubmitAsync(order);

        Assert.Equal(5, rig.LastAction().GetProperty("orders")[0].GetProperty("a").GetInt32());
    }

    [Fact]
    public async Task A_market_order_becomes_an_immediate_or_cancel_limit_through_the_book()
    {
        // THE venue fact of this file. There is no market order: measured, an order whose type is {"market":{}} is
        // refused at HTTP 422 before the signature, because the only two types the venue deserialises are a limit
        // and a trigger. So a market order is an IOC limit priced past the far touch, and the book has to be read
        // to know where that is.
        await using Rig rig = await new Rig().ConnectAsync();
        MarketOrder order = rig.Track(rig.Orders.Market(rig.Btc.Id, OrderSide.Buy, rig.Btc.MakeQuantity(0.01m)));

        await rig.SubmitAsync(order);

        // The book was read for it, which is the extra request a market order costs here.
        Assert.Contains(
            rig.Server.RequestsTo(HyperliquidVenue.InfoPath),
            r => r.Body.Contains(HyperliquidReads.L2Book, StringComparison.Ordinal));

        JsonElement wire = rig.LastAction().GetProperty("orders")[0];
        Assert.Equal("Ioc", wire.GetProperty("t").GetProperty("limit").GetProperty("tif").GetString());

        // The recorded book's best ask is 83768, reached through by the named slippage and then rounded by both of
        // the venue's price rules - so it is a whole number, five figures, and comfortably through the touch.
        decimal price = decimal.Parse(wire.GetProperty("p").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(price > 83768m, "a buy that does not reach past the best ask is not a market order");
        Assert.Equal(price, decimal.Truncate(price));
        Assert.Equal(83768m * (1m + HyperliquidVenue.MarketOrderSlippage), price, precision: 0);
    }

    [Fact]
    public async Task A_post_only_order_uses_the_venues_own_time_in_force_for_it()
    {
        // Post-only is a time in force here rather than a flag beside one, and the venue refuses an order that
        // would cross instead of repricing it.
        await using Rig rig = await new Rig().ConnectAsync();
        LimitOrder order = rig.Track(rig.Orders.Limit(
            rig.Btc.Id, OrderSide.Buy, rig.Btc.MakeQuantity(0.01m), rig.Btc.MakePrice(83000m), postOnly: true));

        await rig.SubmitAsync(order);

        Assert.Equal("Alo", rig.LastAction().GetProperty("orders")[0].GetProperty("t").GetProperty("limit").GetProperty("tif").GetString());
    }

    [Fact]
    public async Task A_stop_order_carries_a_trigger_and_says_it_is_a_loss_stop()
    {
        // The venue has to be told whether a trigger is a stop-loss or a take-profit, because that is what decides
        // the side the price has to cross it from. A stop that fires as a market order still needs a limit price,
        // which the venue uses as the worst fill it will take once triggered.
        await using Rig rig = await new Rig().ConnectAsync();
        StopMarketOrder order = rig.Track(rig.Orders.StopMarket(
            rig.Btc.Id, OrderSide.Sell, rig.Btc.MakeQuantity(0.01m), rig.Btc.MakePrice(80500m), reduceOnly: true));

        await rig.SubmitAsync(order);

        JsonElement trigger = rig.LastAction().GetProperty("orders")[0].GetProperty("t").GetProperty("trigger");
        Assert.True(trigger.GetProperty("isMarket").GetBoolean());
        Assert.Equal("80500", trigger.GetProperty("triggerPx").GetString());
        Assert.Equal("sl", trigger.GetProperty("tpsl").GetString());
        Assert.True(rig.LastAction().GetProperty("orders")[0].GetProperty("r").GetBoolean());
    }

    [Fact]
    public async Task An_order_for_an_instrument_this_client_never_loaded_is_rejected_rather_than_guessed_at()
    {
        // The asset index is the only thing an order can name an instrument by and it is not derivable from
        // anything visible, so there is nothing to guess with - and a guess would be a real order on whatever asset
        // happened to be at that position.
        await using Rig rig = await new Rig().ConnectAsync();
        InstrumentId unknown = InstrumentId.Parse("NOTACOIN-PERP.HYPERLIQUID");
        LimitOrder order = rig.Track(rig.Orders.Limit(unknown, OrderSide.Buy, new Quantity(1m, 2), new Price(1m, 2)));

        await rig.SubmitAsync(order);

        OrderRejected rejected = await rig.Sink.NextOrderEventAsync<OrderRejected>();
        Assert.Contains("unknown to the Hyperliquid client", rejected.Reason, StringComparison.Ordinal);
        Assert.Empty(rig.Server.RequestsTo(HyperliquidVenue.ExchangePath));
    }

    [Fact]
    public async Task A_quote_quantity_is_rejected_because_this_venue_sizes_in_base_units()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        MarketOrder order = rig.Track(rig.Orders.Market(
            rig.Btc.Id, OrderSide.Buy, rig.Btc.MakeQuantity(1000m), quoteQuantity: true));

        await rig.SubmitAsync(order);

        OrderRejected rejected = await rig.Sink.NextOrderEventAsync<OrderRejected>();
        Assert.Contains("base units", rejected.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refusal_arrives_as_a_rejection_carrying_the_venues_own_words()
    {
        // This venue refuses at HTTP 200 with the reason in the body, so a client checking the status would report
        // a refused order as accepted.
        await using Rig rig = await new Rig().ConnectAsync();
        rig.Server.Handler = request => request.Path == HyperliquidVenue.ExchangePath
            ? StubResponse.Json(HyperliquidPayloads.ExchangeBadSignature)
            : StubResponse.Json(HyperliquidPayloads.Meta);

        LimitOrder order = rig.Track(rig.Orders.Limit(rig.Btc.Id, OrderSide.Buy, rig.Btc.MakeQuantity(0.01m), rig.Btc.MakePrice(83000m)));
        await rig.SubmitAsync(order);

        // Submitted first, because the request went out before the refusal came back.
        await rig.Sink.NextOrderEventAsync<OrderSubmitted>();
        OrderRejected rejected = await rig.Sink.NextOrderEventAsync<OrderRejected>();
        Assert.Contains("Unable to recover signer", rejected.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_orders_never_share_a_nonce()
    {
        // Measured: the venue refuses a repeated nonce outright with "Invalid nonce: duplicate nonce N". A
        // millisecond clock is not enough on its own, so two orders placed inside one millisecond would otherwise
        // see the second refused for a reason that reads like a clock problem.
        await using Rig rig = await new Rig().ConnectAsync();

        for (int i = 0; i < 5; i++)
        {
            LimitOrder order = rig.Track(rig.Orders.Limit(rig.Btc.Id, OrderSide.Buy, rig.Btc.MakeQuantity(0.01m), rig.Btc.MakePrice(83000m - i)));
            await rig.SubmitAsync(order);
        }

        long[] nonces = [.. rig.Server.RequestsTo(HyperliquidVenue.ExchangePath).Select(r =>
        {
            using JsonDocument doc = JsonDocument.Parse(r.Body);
            return doc.RootElement.GetProperty("nonce").GetInt64();
        })];

        Assert.Equal(5, nonces.Length);
        Assert.Equal(nonces.Length, nonces.Distinct().Count());
        Assert.Equal(nonces.OrderBy(n => n), nonces);
    }

    // ----- the streams -----

    [Fact]
    public async Task The_order_and_fill_streams_are_subscribed_to_by_address_with_no_handshake()
    {
        // Measured: an arbitrary address's order stream was subscribed to over a socket that had authenticated
        // nothing, and the venue acknowledged it. So there is no signature here and nothing to get wrong about one.
        await using Rig rig = await new Rig().ConnectAsync();

        List<string> sent = [await rig.Session.ReceiveTextAsync(), await rig.Session.ReceiveTextAsync()];
        string account = HyperliquidVenue.Credentials(new HyperliquidExecutionClientConfig { PrivateKey = ProbeKey }).Account;

        Assert.Contains(sent, m => m.Contains("orderUpdates", StringComparison.Ordinal) && m.Contains(account, StringComparison.Ordinal));
        Assert.Contains(sent, m => m.Contains("userEvents", StringComparison.Ordinal) && m.Contains(account, StringComparison.Ordinal));
        Assert.All(sent, m => Assert.DoesNotContain("signature", m, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task An_order_this_node_placed_is_recognised_when_it_comes_back_as_a_number()
    {
        // The client order id round trip. The engine's string cannot travel, so what comes back is the hash of it -
        // and it is resolved by recomputing that for the orders this node holds, which is what makes it work after
        // a restart as well.
        await using Rig rig = await new Rig().ConnectAsync();
        LimitOrder order = rig.Track(rig.Orders.Limit(rig.Btc.Id, OrderSide.Buy, rig.Btc.MakeQuantity(0.01m), rig.Btc.MakePrice(83000m)));
        await rig.SubmitAsync(order);
        await rig.Sink.NextOrderEventAsync<OrderSubmitted>();

        await rig.Session.SendTextAsync(OrderUpdate(order.ClientOrderId));

        OrderAccepted accepted = await rig.Sink.NextOrderEventAsync<OrderAccepted>();
        Assert.Equal(order.ClientOrderId, accepted.ClientOrderId);
        Assert.Equal("556696349186", accepted.VenueOrderId!.Value.Value);
    }

    [Fact]
    public async Task A_fill_arrives_on_a_channel_named_differently_from_the_subscription()
    {
        // MEASURED, and the trap that would have made this client place orders and never hear that they filled: a
        // userEvents subscription is acknowledged as userEvents and its messages come back on "user".
        await using Rig rig = await new Rig().ConnectAsync();
        LimitOrder order = rig.Track(rig.Orders.Limit(rig.Btc.Id, OrderSide.Sell, rig.Btc.MakeQuantity(0.31975m), rig.Btc.MakePrice(83843m)));
        await rig.SubmitAsync(order);
        await rig.Sink.NextOrderEventAsync<OrderSubmitted>();

        await rig.Session.SendTextAsync(Fill(order.ClientOrderId));

        OrderFilled filled = await rig.Sink.NextOrderEventAsync<OrderFilled>();
        Assert.Equal(order.ClientOrderId, filled.ClientOrderId);
        Assert.Equal(0.31975m, filled.LastQty.Value);
        Assert.Equal(83843m, filled.LastPx.Value);
        Assert.Equal(OrderSide.Sell, filled.OrderSide);

        // "crossed" is what says this account was the taker, which is the liquidity side under another name.
        Assert.Equal(LiquiditySide.Taker, filled.LiquiditySide);

        // The fee is the venue's own figure in the token it charged, not the instrument's rate applied here.
        Assert.Equal(18.01551309m, filled.Commission.Amount);
        Assert.Equal("USDC", filled.Commission.Currency.Code);
    }

    [Fact]
    public async Task A_fill_is_only_reported_once_however_often_the_stream_repeats_it()
    {
        await using Rig rig = await new Rig().ConnectAsync();
        LimitOrder order = rig.Track(rig.Orders.Limit(rig.Btc.Id, OrderSide.Sell, rig.Btc.MakeQuantity(0.31975m), rig.Btc.MakePrice(83843m)));
        await rig.SubmitAsync(order);
        await rig.Sink.NextOrderEventAsync<OrderSubmitted>();

        await rig.Session.SendTextAsync(Fill(order.ClientOrderId));
        await rig.Sink.NextOrderEventAsync<OrderFilled>();
        await rig.Session.SendTextAsync(Fill(order.ClientOrderId));
        await Settle();

        // The trade id is what makes the second one a repeat rather than a second fill, and the venue does repeat.
        Assert.Single(rig.Sink.AllOrderEvents.OfType<OrderFilled>());
    }

    [Theory]

    // Only "open" was seen on the live stream, so the rest are mapped by what they SAY. The venue cancels an order
    // for several reasons it spells differently, and an unrecognised ending status would leave the order open in
    // this node for ever while the venue had closed it.
    [InlineData("canceled")]
    [InlineData("marginCanceled")]
    [InlineData("reduceOnlyCanceled")]
    public async Task Any_ending_status_that_says_canceled_closes_the_order(string status)
    {
        await using Rig rig = await new Rig().ConnectAsync();
        LimitOrder order = rig.Track(rig.Orders.Limit(rig.Btc.Id, OrderSide.Buy, rig.Btc.MakeQuantity(0.01m), rig.Btc.MakePrice(83000m)));
        await rig.SubmitAsync(order);
        await rig.Sink.NextOrderEventAsync<OrderSubmitted>();

        await rig.Session.SendTextAsync(OrderUpdate(order.ClientOrderId, status));

        OrderCanceled canceled = await rig.Sink.NextOrderEventAsync<OrderCanceled>();
        Assert.Equal(order.ClientOrderId, canceled.ClientOrderId);
    }

    [Fact]
    public async Task An_order_belonging_to_something_else_on_the_same_account_is_left_alone()
    {
        // The account's streams carry everything the account does, including orders this node did not place. There
        // is nothing to report them against, and inventing a client order id would create an order the cache has
        // never seen.
        await using Rig rig = await new Rig().ConnectAsync();

        await rig.Session.SendTextAsync(
            HyperliquidPayloads.OrderUpdatesChannel.Replace("CLOID", "0x0123456789abcdef0123456789abcdef", StringComparison.Ordinal));
        await Settle();

        Assert.Empty(rig.Sink.AllOrderEvents);
    }

    [Fact]
    public async Task A_spot_fill_on_the_same_account_is_skipped_rather_than_read_as_a_perpetual()
    {
        // Measured on a live account: its fills list carried "@142" - a SPOT pair - in the same array as its
        // perpetual fills, and builder-deployed exchange assets arrive as "xyz:CL". This family is the main
        // perpetuals, so those are skipped.
        await using Rig rig = await new Rig().ConnectAsync();
        LimitOrder order = rig.Track(rig.Orders.Limit(rig.Btc.Id, OrderSide.Sell, rig.Btc.MakeQuantity(0.31975m), rig.Btc.MakePrice(83843m)));
        await rig.SubmitAsync(order);
        await rig.Sink.NextOrderEventAsync<OrderSubmitted>();

        await rig.Session.SendTextAsync(
            Fill(order.ClientOrderId).Replace("\"coin\":\"BTC\"", "\"coin\":\"@142\"", StringComparison.Ordinal));
        await rig.Session.SendTextAsync(
            Fill(order.ClientOrderId).Replace("\"coin\":\"BTC\"", "\"coin\":\"xyz:CL\"", StringComparison.Ordinal));
        await Settle();

        Assert.Empty(rig.Sink.AllOrderEvents.OfType<OrderFilled>());
    }

    // ----- cancel and amend -----

    [Fact]
    public async Task A_cancel_goes_by_client_order_id_so_it_works_before_the_venues_id_is_known()
    {
        // The venue has a cancel-by-client-id action of its own, with differently spelled fields from the cancel by
        // its own id - "asset" and "cloid" rather than "a" and "o" - and both spellings are part of their digests.
        await using Rig rig = await new Rig().ConnectAsync();
        LimitOrder order = rig.Track(rig.Orders.Limit(rig.Btc.Id, OrderSide.Buy, rig.Btc.MakeQuantity(0.01m), rig.Btc.MakePrice(83000m)));

        await rig.Client.CancelOrderAsync(
            new CancelOrder(rig.Kernel.Services.TraderId, Rig.Strategy, rig.Btc.Id, order.ClientOrderId, null, null, Guid.NewGuid(), TestKernel.Now),
            CancellationToken.None);

        JsonElement action = rig.LastAction();
        Assert.Equal("cancelByCloid", action.GetProperty("type").GetString());
        Assert.Equal(0, action.GetProperty("cancels")[0].GetProperty("asset").GetInt32());
        Assert.Equal(HyperliquidVenue.CloidFor(order.ClientOrderId), action.GetProperty("cancels")[0].GetProperty("cloid").GetString());
    }

    [Fact]
    public async Task An_amendment_replaces_the_whole_order_because_that_is_what_the_venue_takes()
    {
        // The venue really has an amend, and it is a replacement rather than a patch: price, size, side, time in
        // force and any trigger all travel again whether they changed or not. It matches by the order's own numeric
        // id, which is why the venue id has to be known first.
        await using Rig rig = await new Rig().ConnectAsync();
        LimitOrder order = rig.Track(rig.Orders.Limit(rig.Btc.Id, OrderSide.Buy, rig.Btc.MakeQuantity(0.01m), rig.Btc.MakePrice(83000m)));
        await rig.SubmitAsync(order);
        await rig.Sink.NextOrderEventAsync<OrderSubmitted>();
        await rig.Session.SendTextAsync(OrderUpdate(order.ClientOrderId));
        await rig.Sink.NextOrderEventAsync<OrderAccepted>();

        await rig.Client.ModifyOrderAsync(
            new ModifyOrder(
                rig.Kernel.Services.TraderId, Rig.Strategy, rig.Btc.Id, order.ClientOrderId, null,
                rig.Btc.MakeQuantity(0.02m), rig.Btc.MakePrice(82500m), null, null, Guid.NewGuid(), TestKernel.Now),
            CancellationToken.None);

        JsonElement action = rig.LastAction();
        Assert.Equal("modify", action.GetProperty("type").GetString());
        Assert.Equal(556696349186L, action.GetProperty("oid").GetInt64());
        Assert.Equal("82500", action.GetProperty("order").GetProperty("p").GetString());
        Assert.Equal("0.02", action.GetProperty("order").GetProperty("s").GetString());

        await rig.Sink.NextOrderEventAsync<OrderPendingUpdate>();
        OrderUpdated updated = await rig.Sink.NextOrderEventAsync<OrderUpdated>();
        Assert.Equal(0.02m, updated.Quantity.Value);
    }

    [Fact]
    public async Task An_order_with_no_venue_id_yet_cannot_be_amended_and_is_told_so()
    {
        // The venue matches an amendment by its own numeric id, so there is nothing to match on. Saying so is the
        // honest answer; cancelling and replacing behind the caller's back would leave a position unguarded for the
        // length of two requests without anybody having asked for that.
        await using Rig rig = await new Rig().ConnectAsync();
        LimitOrder order = rig.Track(rig.Orders.Limit(rig.Btc.Id, OrderSide.Buy, rig.Btc.MakeQuantity(0.01m), rig.Btc.MakePrice(83000m)));

        await rig.Client.ModifyOrderAsync(
            new ModifyOrder(
                rig.Kernel.Services.TraderId, Rig.Strategy, rig.Btc.Id, order.ClientOrderId, null,
                rig.Btc.MakeQuantity(0.02m), rig.Btc.MakePrice(82500m), null, null, Guid.NewGuid(), TestKernel.Now),
            CancellationToken.None);

        OrderModifyRejected rejected = await rig.Sink.NextOrderEventAsync<OrderModifyRejected>();
        Assert.Contains("numeric id", rejected.Reason, StringComparison.Ordinal);
        Assert.Empty(rig.Server.RequestsTo(HyperliquidVenue.ExchangePath));
    }

    // ----- reports -----

    [Fact]
    public async Task Resting_orders_are_read_from_the_endpoint_that_carries_the_trigger_fields()
    {
        // The plain openOrders read leaves the trigger out, so a stop guarding a position would come back looking
        // like a limit order at its limit price - which is exactly the report a reconciliation must not be given.
        await using Rig rig = await new Rig().ConnectAsync();

        await rig.Client.GenerateOrderStatusReportsAsync(null, null, null, openOnly: true, CancellationToken.None);

        Assert.Contains(
            rig.Server.RequestsTo(HyperliquidVenue.InfoPath),
            r => r.Body.Contains(HyperliquidReads.FrontendOpenOrders, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_mass_status_asks_for_orders_fills_and_positions()
    {
        await using Rig rig = await new Rig().ConnectAsync();

        ExecutionMassStatus? status = await rig.Client.GenerateMassStatusAsync(null, CancellationToken.None);

        Assert.NotNull(status);
        Assert.Single(status!.PositionReports);
        Assert.Empty(status.OrderReports);
        Assert.Empty(status.FillReports);
    }

    [Fact]
    public async Task An_order_status_query_needs_the_venues_own_id_and_says_nothing_without_one()
    {
        // The venue answers by its numeric id only - there is no read that takes a client order id - so an order
        // this node has no venue id for cannot be asked about at all.
        await using Rig rig = await new Rig().ConnectAsync();

        OrderStatusReport? report = await rig.Client.GenerateOrderStatusReportAsync(
            rig.Btc.Id, new ClientOrderId("never-sent"), null, CancellationToken.None);

        Assert.Null(report);
        Assert.DoesNotContain(
            rig.Server.RequestsTo(HyperliquidVenue.InfoPath),
            r => r.Body.Contains(HyperliquidReads.OrderStatus, StringComparison.Ordinal));
    }

    [Fact]
    public void The_factory_builds_this_venues_clients_under_the_venues_own_name()
    {
        using TestKernel kernel = new();
        HyperliquidExecutionClientFactory execution = new();
        HyperliquidDataClientFactory data = new();

        Assert.Equal("HYPERLIQUID", execution.Name);
        Assert.Equal("HYPERLIQUID", data.Name);
        Assert.Equal(typeof(HyperliquidExecutionClientConfig), execution.ConfigType);
        Assert.Equal(typeof(HyperliquidDataClientConfig), data.ConfigType);

        IExecutionClient client = execution.Create(
            new ClientId("HYPERLIQUID"),
            new HyperliquidExecutionClientConfig { PrivateKey = ProbeKey },
            kernel.Services);

        Assert.IsType<HyperliquidExecutionClient>(client);
        client.Dispose();
    }
}
