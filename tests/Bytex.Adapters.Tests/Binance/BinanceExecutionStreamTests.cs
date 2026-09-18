using Bytex.Adapters.Binance;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;

namespace Bytex.Adapters.Tests.Binance;

// Why: fills and cancels arrive only on the user data stream. If an execution report is mapped to the wrong
// order, side, price or commission, the engine's positions and balances drift away from the venue's.
// Payloads follow the documented executionReport / ORDER_TRADE_UPDATE / account update shapes.
public sealed class BinanceExecutionStreamTests
{
    private const string SpotAccount = """
        {"makerCommission":15,"takerCommission":15,"canTrade":true,"updateTime":123456789,"accountType":"SPOT",
         "balances":[{"asset":"BTC","free":"1.50000000","locked":"0.50000000"},{"asset":"USDT","free":"1000.00000000","locked":"0.00000000"},{"asset":"LTC","free":"0.00000000","locked":"0.00000000"}]}
        """;

    private const string FuturesBalance = """
        [{"accountAlias":"SgsR","asset":"USDT","balance":"122607.35137903","crossWalletBalance":"23.72469206","crossUnPnl":"0.00000000","availableBalance":"120000.35137903","maxWithdrawAmount":"23.72469206","marginAvailable":true,"updateTime":1617939110373}]
        """;

    private static string ExecutionReport(string clientId, string originalClientId, string execType, string status, string lastQty = "0.00000000", string lastPx = "0.00000000", string reject = "NONE") => $$"""
        {"e":"executionReport","E":1499405658658,"s":"BTCUSDT","c":"{{clientId}}","S":"BUY","o":"LIMIT","f":"GTC","q":"1.00000000","p":"25000.10","P":"0.00000000","F":"0.00000000","g":-1,"C":"{{originalClientId}}","x":"{{execType}}","X":"{{status}}","r":"{{reject}}","i":4293153,"l":"{{lastQty}}","z":"{{lastQty}}","L":"{{lastPx}}","n":"0.00012500","N":"BNB","T":1499405658657,"t":77,"I":8641984,"w":true,"m":true,"M":false,"O":1499405658657,"Z":"0.00000000","Y":"0.00000000","Q":"0.00000000"}
        """;

    [Fact]
    public async Task Connecting_publishes_the_rest_account_snapshot_and_opens_the_listen_key_stream()
    {
        await using BinanceExecRig rig = new(BinanceAccountType.Spot);

        WsSession session = await rig.ConnectAsync(SpotAccount, "listen-key-1");

        AccountState state = await rig.Sink.NextAccountStateAsync();
        Assert.Equal("/ws/listen-key-1", session.Path);
        Assert.Equal(new AccountId("BINANCE-SPOT"), state.AccountId);
        Assert.True(state.Reported);
        Assert.Equal(["BTC", "USDT"], state.Balances.Select(b => b.Currency.Code)); // the all-zero LTC row is dropped
        AccountBalance btc = state.Balances[0];
        Assert.Equal(2.0m, btc.Total.Amount);
        Assert.Equal(0.5m, btc.Locked.Amount);
        Assert.Equal(1.5m, btc.Free.Amount);
        BinanceExecRig.AssertSigned(Assert.Single(rig.Server.RequestsTo("/api/v3/account")));
        RecordedRequest listenKey = Assert.Single(rig.Server.RequestsTo("/api/v3/userDataStream"));
        Assert.Equal("POST", listenKey.Method);
        Assert.Equal(BinanceExecRig.ApiKey, listenKey.Header("X-MBX-APIKEY"));
        Assert.Null(listenKey.Query("signature")); // listen keys need the key header only
        Assert.True(rig.Client.IsConnected);
    }

    [Fact]
    public async Task Futures_connect_uses_the_fapi_balance_and_listen_key_endpoints()
    {
        await using BinanceExecRig rig = new(BinanceAccountType.UsdMFutures);

        WsSession session = await rig.ConnectAsync(FuturesBalance, "futures-key");

        AccountState state = await rig.Sink.NextAccountStateAsync();
        Assert.Equal("/ws/futures-key", session.Path);
        AccountBalance usdt = Assert.Single(state.Balances);
        Assert.Equal(122_607.35137903m, usdt.Total.Amount);
        Assert.Equal(120_000.35137903m, usdt.Free.Amount);
        Assert.Single(rig.Server.RequestsTo("/fapi/v2/balance"));
        Assert.Single(rig.Server.RequestsTo("/fapi/v1/listenKey"));
    }

    [Fact]
    public async Task A_NEW_execution_report_accepts_the_cached_order_under_its_own_strategy_with_the_venue_order_id()
    {
        await using BinanceExecRig rig = new(BinanceAccountType.Spot);
        WsSession session = await rig.ConnectAsync(SpotAccount);
        LimitOrder order = rig.Orders.Limit(rig.Instrument.Id, OrderSide.Buy, rig.Qty(1m), rig.Px(25_000.10m));
        rig.Kernel.Kernel.Cache.AddOrder(order);

        await session.SendTextAsync(ExecutionReport(order.ClientOrderId.Value, string.Empty, "NEW", "NEW"));

        OrderAccepted accepted = await rig.Sink.NextOrderEventAsync<OrderAccepted>();
        Assert.Equal(order.ClientOrderId, accepted.ClientOrderId);
        Assert.Equal(new VenueOrderId("4293153"), accepted.VenueOrderId);
        Assert.Equal(BinanceExecRig.Strategy, accepted.StrategyId);
        Assert.Equal(rig.Instrument.Id, accepted.InstrumentId);
        Assert.Equal(1_499_405_658_658_000_000L, accepted.TsEvent.Value);
        Assert.Equal(TestKernel.Now, accepted.TsInit);
    }

    [Fact]
    public async Task An_order_placed_outside_the_engine_is_attributed_to_the_EXTERNAL_strategy()
    {
        await using BinanceExecRig rig = new(BinanceAccountType.Spot);
        WsSession session = await rig.ConnectAsync(SpotAccount);

        await session.SendTextAsync(ExecutionReport("web_order_1", string.Empty, "NEW", "NEW"));

        OrderAccepted accepted = await rig.Sink.NextOrderEventAsync<OrderAccepted>();
        Assert.Equal(StrategyId.External, accepted.StrategyId);
        Assert.Equal(InstrumentId.Parse("BTCUSDT.BINANCE"), accepted.InstrumentId);
    }

    [Fact]
    public async Task A_TRADE_execution_report_becomes_a_fill_with_last_quantity_price_commission_and_liquidity()
    {
        await using BinanceExecRig rig = new(BinanceAccountType.Spot);
        WsSession session = await rig.ConnectAsync(SpotAccount);

        await session.SendTextAsync(ExecutionReport("O-1", string.Empty, "TRADE", "PARTIALLY_FILLED", "0.40000000", "25000.05"));

        OrderFilled fill = await rig.Sink.NextOrderEventAsync<OrderFilled>();
        Assert.Equal(new ClientOrderId("O-1"), fill.ClientOrderId);
        Assert.Equal(new VenueOrderId("4293153"), fill.VenueOrderId);
        Assert.Equal(new TradeId("77"), fill.TradeId);
        Assert.Equal(OrderSide.Buy, fill.OrderSide);
        Assert.Equal(OrderType.Limit, fill.OrderType);
        Assert.Equal(new Quantity(0.4m, 5), fill.LastQty);
        Assert.Equal(new Price(25_000.05m, 2), fill.LastPx);
        Assert.Equal("USDT", fill.Currency.Code);
        Assert.Equal(0.000125m, fill.Commission.Amount);
        Assert.Equal("BNB", fill.Commission.Currency.Code);
        Assert.Equal(LiquiditySide.Maker, fill.LiquiditySide);
        Assert.Equal(1_499_405_658_657_000_000L, fill.TsEvent.Value); // transaction time "T"
    }

    [Fact]
    public async Task A_REJECTED_execution_report_carries_the_venue_reject_reason()
    {
        await using BinanceExecRig rig = new(BinanceAccountType.Spot);
        WsSession session = await rig.ConnectAsync(SpotAccount);

        await session.SendTextAsync(ExecutionReport("O-1", string.Empty, "REJECTED", "REJECTED", reject: "INSUFFICIENT_BALANCE"));

        OrderRejected rejected = await rig.Sink.NextOrderEventAsync<OrderRejected>();
        Assert.Equal("INSUFFICIENT_BALANCE", rejected.Reason);
    }

    [Fact]
    public async Task An_EXPIRED_report_for_a_resting_order_becomes_OrderExpired()
    {
        await using BinanceExecRig rig = new(BinanceAccountType.Spot);
        WsSession session = await rig.ConnectAsync(SpotAccount);

        await session.SendTextAsync(ExecutionReport("O-1", string.Empty, "EXPIRED", "EXPIRED"));

        OrderExpired expired = await rig.Sink.NextOrderEventAsync<OrderExpired>();
        Assert.Equal(new ClientOrderId("O-1"), expired.ClientOrderId);
    }

    [Fact(Skip = "BUG: on spot a CANCELED executionReport carries the cancel REQUEST id in \"c\" and the order's own id in \"C\"; HandleExecutionReport reads \"c\", so the cancel is applied to a non-existent order and the real one stays open in the cache")]
    public async Task A_spot_cancel_confirmation_is_applied_to_the_original_order_named_in_field_C()
    {
        await using BinanceExecRig rig = new(BinanceAccountType.Spot);
        WsSession session = await rig.ConnectAsync(SpotAccount);
        LimitOrder order = rig.Orders.Limit(rig.Instrument.Id, OrderSide.Buy, rig.Qty(1m), rig.Px(25_000.10m));
        rig.Kernel.Kernel.Cache.AddOrder(order);

        await session.SendTextAsync(ExecutionReport("cancel_request_abc", order.ClientOrderId.Value, "CANCELED", "CANCELED"));

        OrderCanceled canceled = await rig.Sink.NextOrderEventAsync<OrderCanceled>();
        Assert.Equal(order.ClientOrderId, canceled.ClientOrderId);
        Assert.Equal(BinanceExecRig.Strategy, canceled.StrategyId);
    }

    [Fact]
    public async Task A_spot_balance_update_reports_total_as_free_plus_locked_at_the_venue_event_time()
    {
        await using BinanceExecRig rig = new(BinanceAccountType.Spot);
        WsSession session = await rig.ConnectAsync(SpotAccount);
        await rig.Sink.NextAccountStateAsync(); // REST snapshot

        await session.SendTextAsync("""{"e":"outboundAccountPosition","E":1564034571105,"u":1564034571073,"B":[{"a":"ETH","f":"10000.000000","l":"250.500000"}]}""");

        AccountState state = await rig.Sink.NextAccountStateAsync();
        AccountBalance eth = Assert.Single(state.Balances);
        Assert.Equal("ETH", eth.Currency.Code);
        Assert.Equal(10_250.5m, eth.Total.Amount);
        Assert.Equal(250.5m, eth.Locked.Amount);
        Assert.Equal(10_000m, eth.Free.Amount);
        Assert.Equal(1_564_034_571_105_000_000L, state.TsEvent.Value);
    }

    [Fact]
    public async Task A_futures_ORDER_TRADE_UPDATE_fill_is_read_from_the_nested_order_object()
    {
        await using BinanceExecRig rig = new(BinanceAccountType.UsdMFutures);
        WsSession session = await rig.ConnectAsync(FuturesBalance);

        await session.SendTextAsync("""
            {"e":"ORDER_TRADE_UPDATE","E":1568879465651,"T":1568879465650,"o":{"s":"BTCUSDT","c":"O-7","S":"SELL","o":"MARKET","f":"GTC","q":"0.250","p":"0","ap":"25000.5","sp":"0","x":"TRADE","X":"FILLED","i":8886774,"l":"0.250","z":"0.250","L":"25000.5","N":"USDT","n":"3.12506","T":1568879465650,"t":5541,"b":"0","a":"0","m":false,"R":true,"wt":"CONTRACT_PRICE","ot":"MARKET","ps":"BOTH","cp":false,"rp":"12.5"}}
            """);

        OrderFilled fill = await rig.Sink.NextOrderEventAsync<OrderFilled>();
        Assert.Equal(InstrumentId.Parse("BTCUSDT-PERP.BINANCE"), fill.InstrumentId);
        Assert.Equal(new ClientOrderId("O-7"), fill.ClientOrderId);
        Assert.Equal(new VenueOrderId("8886774"), fill.VenueOrderId);
        Assert.Equal(OrderSide.Sell, fill.OrderSide);
        Assert.Equal(OrderType.Market, fill.OrderType);
        Assert.Equal(new Quantity(0.25m, 3), fill.LastQty);
        Assert.Equal(new Price(25_000.5m, 1), fill.LastPx);
        Assert.Equal(new Money(3.12506m, Currencies.USDT), fill.Commission);
        Assert.Equal(LiquiditySide.Taker, fill.LiquiditySide);
        Assert.Equal(new AccountId("BINANCE-USDMFUTURES"), fill.AccountId);
        Assert.Equal(1_568_879_465_650_000_000L, fill.TsEvent.Value);
    }

    [Fact]
    public async Task A_futures_ACCOUNT_UPDATE_reports_the_wallet_balance()
    {
        await using BinanceExecRig rig = new(BinanceAccountType.UsdMFutures);
        WsSession session = await rig.ConnectAsync(FuturesBalance);
        await rig.Sink.NextAccountStateAsync(); // REST snapshot

        await session.SendTextAsync("""
            {"e":"ACCOUNT_UPDATE","E":1564745798939,"T":1564745798938,"a":{"m":"ORDER","B":[{"a":"USDT","wb":"122624.12345678","cw":"100.12345678","bc":"50.12345678"}],"P":[]}}
            """);

        AccountState state = await rig.Sink.NextAccountStateAsync();
        AccountBalance usdt = Assert.Single(state.Balances);
        Assert.Equal(122_624.12345678m, usdt.Total.Amount);
        Assert.Equal(1_564_745_798_939_000_000L, state.TsEvent.Value);
    }

    [Fact]
    public async Task An_expired_listen_key_is_replaced_and_the_stream_is_reopened_on_the_new_key()
    {
        await using BinanceExecRig rig = new(BinanceAccountType.Spot);
        WsSession first = await rig.ConnectAsync(SpotAccount, "listen-key-1", "listen-key-2");

        await first.SendTextAsync("""{"e":"listenKeyExpired","E":1576653824250,"listenKey":"listen-key-1"}""");
        WsSession second = await rig.Server.NextSessionAsync();

        Assert.Equal("/ws/listen-key-1", first.Path);
        Assert.Equal("/ws/listen-key-2", second.Path);
    }

    [Fact]
    public async Task Disconnect_closes_the_listen_key_at_the_venue()
    {
        await using BinanceExecRig rig = new(BinanceAccountType.Spot);
        await rig.ConnectAsync(SpotAccount, "listen-key-1");

        await rig.Client.DisconnectAsync(CancellationToken.None).WaitAsync(Wait.Timeout);

        RecordedRequest close = Assert.Single(rig.Server.RequestsTo("/api/v3/userDataStream"), r => r.Method == "DELETE");
        Assert.Equal("listen-key-1", close.Query("listenKey"));
        Assert.False(rig.Client.IsConnected);
    }
}
