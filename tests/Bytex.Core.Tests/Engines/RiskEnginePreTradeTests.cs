using Bytex.Core.Engines;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Tests.Support;

namespace Bytex.Core.Tests.Engines;

// Why: docs/concepts/risk.md lists the pre-trade checks and their reason codes. Each rule gets a test on
// both sides of its boundary; a denied order must produce OrderDenied and must not reach the execution engine.
public class RiskEnginePreTradeTests
{
    /// <summary>
    /// BTC/USDT with every limit set: quantity 0.010..100.000, price 1.00..500000.00, notional 10..1,000,000 USDT.
    /// </summary>
    private static CurrencyPair Limited() => TestInstruments.BtcUsdt(
        minQuantity: Quantity.Parse("0.010"),
        maxQuantity: Quantity.Parse("100.000"),
        minNotional: Money.Parse("10 USDT"),
        maxNotional: Money.Parse("1000000 USDT"),
        minPrice: Price.Parse("1.00"),
        maxPrice: Price.Parse("500000.00"));

    private static RiskHarness HarnessWith(Instrument instrument, RiskEngineConfig? config = null)
    {
        RiskHarness h = new(config);
        h.Cache.AddInstrument(instrument);
        return h;
    }

    private static void AssertDenied(RiskHarness h, Order order, string reasonPrefix)
    {
        Assert.Empty(h.Forwarded);
        OrderDenied denied = Assert.Single(h.Denied);
        Assert.Equal(order.ClientOrderId, denied.ClientOrderId);
        Assert.StartsWith(reasonPrefix, denied.Reason, StringComparison.Ordinal);
    }

    private static void AssertForwarded(RiskHarness h, TradingCommand command)
    {
        Assert.Empty(h.Events);
        Assert.Same(command, Assert.Single(h.Forwarded));
    }

    [Fact]
    public void Valid_limit_order_is_forwarded_unchanged_to_the_execution_engine()
    {
        RiskHarness h = HarnessWith(Limited());

        SubmitOrder command = h.Submit(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));

        AssertForwarded(h, command);
        Assert.Equal(1, h.Engine.CommandCount);
        Assert.Equal(0, h.Engine.DeniedCount);
    }

    [Fact]
    public void Order_for_an_instrument_missing_from_the_cache_is_denied()
    {
        RiskHarness h = new();
        LimitOrder order = TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00");

        h.Submit(order);

        Assert.Empty(h.Forwarded);
        OrderDenied denied = Assert.Single(h.Denied);
        Assert.Contains("BTCUSDT.BINANCE", denied.Reason, StringComparison.Ordinal);
        Assert.Contains("not found", denied.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Denied_event_identifies_the_order_and_is_stamped_with_the_clock()
    {
        UnixNanos now = UnixNanos.Parse("2024-03-05T10:20:30Z");
        RiskHarness h = new(now: now);
        LimitOrder order = TestOrders.Limit("O-77", TestIds.BtcUsdt, OrderSide.Sell, "1.000", "50000.00", TestIds.OtherStrategy);

        h.Submit(order);

        OrderDenied denied = Assert.Single(h.Denied);
        Assert.Equal(TestIds.Trader, denied.TraderId);
        Assert.Equal(TestIds.OtherStrategy, denied.StrategyId);
        Assert.Equal(TestIds.BtcUsdt, denied.InstrumentId);
        Assert.Equal(new ClientOrderId("O-77"), denied.ClientOrderId);
        Assert.Equal(now, denied.TsEvent);
        Assert.Equal(now, denied.TsInit);
        Assert.NotEqual(Guid.Empty, denied.EventId);
    }

    [Fact]
    public void Denied_event_is_a_legal_transition_for_the_initialized_order()
    {
        RiskHarness h = new();
        LimitOrder order = TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00");
        h.Submit(order);

        order.Apply(Assert.Single(h.Denied));

        Assert.Equal(OrderStatus.Denied, order.Status);
    }

    [Fact]
    public void Quantity_with_more_decimals_than_the_instrument_size_precision_is_denied()
    {
        RiskHarness h = HarnessWith(Limited());
        LimitOrder order = TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.0001", "50000.00");

        h.Submit(order);

        AssertDenied(h, order, "QUANTITY_PRECISION");
    }

    [Fact]
    public void Quantity_with_fewer_decimals_than_the_instrument_size_precision_is_accepted()
    {
        RiskHarness h = HarnessWith(Limited());

        SubmitOrder command = h.Submit(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.5", "50000.00"));

        AssertForwarded(h, command);
    }

    [Theory]
    [InlineData("100.000", null)]
    [InlineData("100.001", "QUANTITY_EXCEEDS_MAX")]
    [InlineData("0.010", null)]
    [InlineData("0.009", "QUANTITY_LESS_THAN_MIN")]
    public void Quantity_is_checked_against_instrument_min_and_max_inclusive(string quantity, string? expectedReason)
    {
        // Price 5000.00 keeps every notional inside 10..1,000,000: 0.009*5000 = 45, 100.001*5000 = 500,005.
        RiskHarness h = HarnessWith(Limited());
        LimitOrder order = TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, quantity, "5000.00");

        SubmitOrder command = h.Submit(order);

        if (expectedReason is null)
        {
            AssertForwarded(h, command);
        }
        else
        {
            AssertDenied(h, order, expectedReason);
        }
    }

    [Fact]
    public void Price_with_more_decimals_than_the_instrument_price_precision_is_denied()
    {
        RiskHarness h = HarnessWith(Limited());
        LimitOrder order = TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.001");

        h.Submit(order);

        AssertDenied(h, order, "PRICE_PRECISION");
    }

    [Theory]
    [InlineData("0.00")]
    [InlineData("-1.00")]
    public void Non_positive_limit_price_is_denied(string price)
    {
        RiskHarness h = HarnessWith(TestInstruments.BtcUsdt());
        LimitOrder order = TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", price);

        h.Submit(order);

        AssertDenied(h, order, "PRICE_NOT_POSITIVE");
    }

    [Theory]
    [InlineData("1.000", "500000.00", null)] // notional 500,000
    [InlineData("1.000", "500000.01", "PRICE_EXCEEDS_MAX")]
    [InlineData("10.000", "1.00", null)] // notional exactly 10, the minimum
    [InlineData("10.000", "0.99", "PRICE_LESS_THAN_MIN")]
    public void Limit_price_is_checked_against_instrument_min_and_max_inclusive(string quantity, string price, string? expectedReason)
    {
        RiskHarness h = HarnessWith(Limited());
        LimitOrder order = TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Sell, quantity, price);

        SubmitOrder command = h.Submit(order);

        if (expectedReason is null)
        {
            AssertForwarded(h, command);
        }
        else
        {
            AssertDenied(h, order, expectedReason);
        }
    }

    [Theory]
    [InlineData("50000.001", "PRICE_PRECISION")]
    [InlineData("0.00", "PRICE_NOT_POSITIVE")]
    [InlineData("500000.01", "PRICE_EXCEEDS_MAX")]
    [InlineData("0.99", "PRICE_LESS_THAN_MIN")]
    public void Trigger_price_of_a_stop_order_gets_the_same_checks_as_a_limit_price(string trigger, string expectedReason)
    {
        RiskHarness h = HarnessWith(Limited());
        StopMarketOrder order = TestOrders.StopMarket("O-1", TestIds.BtcUsdt, OrderSide.Sell, "1.000", trigger);

        h.Submit(order);

        AssertDenied(h, order, expectedReason);
    }

    [Fact]
    public void Stop_limit_with_a_valid_price_and_an_invalid_trigger_is_denied()
    {
        RiskHarness h = HarnessWith(Limited());
        StopLimitOrder order = TestOrders.StopLimit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00", "50000.123");

        h.Submit(order);

        AssertDenied(h, order, "PRICE_PRECISION");
    }

    [Theory]
    [InlineData("10.000", "100000.00", null)] // 10 * 100,000.00 = 1,000,000.00 == max
    [InlineData("10.000", "100000.01", "NOTIONAL_EXCEEDS_MAX")] // 1,000,000.10
    [InlineData("0.010", "1000.00", null)] // 0.010 * 1,000.00 = 10.00 == min
    [InlineData("0.010", "999.99", "NOTIONAL_LESS_THAN_MIN")] // 9.9999
    public void Notional_of_a_limit_order_is_checked_against_instrument_min_and_max_inclusive(string quantity, string price, string? expectedReason)
    {
        RiskHarness h = HarnessWith(Limited());
        LimitOrder order = TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, quantity, price);

        SubmitOrder command = h.Submit(order);

        if (expectedReason is null)
        {
            AssertForwarded(h, command);
        }
        else
        {
            AssertDenied(h, order, expectedReason);
        }
    }

    [Fact]
    public void Notional_of_a_stop_market_order_is_computed_from_its_trigger_price()
    {
        // 10 * 100,000.01 = 1,000,000.10 > 1,000,000
        RiskHarness h = HarnessWith(Limited());
        StopMarketOrder order = TestOrders.StopMarket("O-1", TestIds.BtcUsdt, OrderSide.Buy, "10.000", "100000.01");

        h.Submit(order);

        AssertDenied(h, order, "NOTIONAL_EXCEEDS_MAX");
    }

    [Fact]
    public void Max_notional_per_order_allows_an_order_exactly_at_the_cap()
    {
        RiskHarness h = HarnessWith(TestInstruments.BtcUsdt(), new RiskEngineConfig { MaxNotionalPerOrder = new Dictionary<InstrumentId, decimal> { [TestIds.BtcUsdt] = 50_000m } });

        SubmitOrder command = h.Submit(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));

        AssertForwarded(h, command);
    }

    [Fact]
    public void Max_notional_per_order_denies_an_order_one_tick_above_the_cap()
    {
        // 1 * 50,000.01 = 50,000.01 > 50,000
        RiskHarness h = HarnessWith(TestInstruments.BtcUsdt(), new RiskEngineConfig { MaxNotionalPerOrder = new Dictionary<InstrumentId, decimal> { [TestIds.BtcUsdt] = 50_000m } });
        LimitOrder order = TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.01");

        h.Submit(order);

        AssertDenied(h, order, "NOTIONAL_EXCEEDS_MAX_PER_ORDER");
    }

    [Fact]
    public void Max_notional_per_order_applies_only_to_the_configured_instrument()
    {
        RiskHarness h = HarnessWith(TestInstruments.BtcUsdt(), new RiskEngineConfig { MaxNotionalPerOrder = new Dictionary<InstrumentId, decimal> { [TestIds.BtcUsdt] = 50_000m } });
        h.Cache.AddInstrument(TestInstruments.EthUsdt());

        // 100 * 3,000 = 300,000, far above the BTC cap, but ETH has no cap.
        SubmitOrder command = h.Submit(TestOrders.Limit("O-1", TestIds.EthUsdt, OrderSide.Buy, "100.000", "3000.00"));

        AssertForwarded(h, command);
    }

    [Fact]
    public void Market_buy_is_valued_at_the_ask_and_market_sell_at_the_bid()
    {
        RiskHarness h = HarnessWith(TestInstruments.BtcUsdt(), new RiskEngineConfig { MaxNotionalPerOrder = new Dictionary<InstrumentId, decimal> { [TestIds.BtcUsdt] = 50_000m } });
        h.Cache.AddQuoteTick(new QuoteTick(TestIds.BtcUsdt, Price.Parse("49990.00"), Price.Parse("50010.00"), Quantity.Parse("1.000"), Quantity.Parse("1.000"), TestOrders.T0, TestOrders.T0));
        MarketOrder buy = TestOrders.Market("O-BUY", TestIds.BtcUsdt, OrderSide.Buy, "1.000");
        MarketOrder sell = TestOrders.Market("O-SELL", TestIds.BtcUsdt, OrderSide.Sell, "1.000");

        h.Submit(buy);
        SubmitOrder sellCommand = h.Submit(sell);

        // Buy: 1 * 50,010 > 50,000 denied. Sell: 1 * 49,990 <= 50,000 forwarded.
        OrderDenied denied = Assert.Single(h.Denied);
        Assert.Equal(buy.ClientOrderId, denied.ClientOrderId);
        Assert.StartsWith("NOTIONAL_EXCEEDS_MAX_PER_ORDER", denied.Reason, StringComparison.Ordinal);
        Assert.Same(sellCommand, Assert.Single(h.Forwarded));
    }

    [Fact]
    public void Market_order_is_valued_at_the_last_trade_when_there_are_no_quotes()
    {
        RiskHarness h = HarnessWith(TestInstruments.BtcUsdt(), new RiskEngineConfig { MaxNotionalPerOrder = new Dictionary<InstrumentId, decimal> { [TestIds.BtcUsdt] = 40_000m } });
        h.Cache.AddTradeTick(new TradeTick(TestIds.BtcUsdt, Price.Parse("50000.00"), Quantity.Parse("0.100"), AggressorSide.Buyer, new TradeId("T-1"), TestOrders.T0, TestOrders.T0));
        MarketOrder order = TestOrders.Market("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000");

        h.Submit(order);

        AssertDenied(h, order, "NOTIONAL_EXCEEDS_MAX_PER_ORDER");
    }

    [Fact]
    public void Market_order_with_no_market_data_at_all_cannot_be_valued_and_is_forwarded()
    {
        // There is no price to compute a notional from, so only the quantity checks can run.
        RiskHarness h = HarnessWith(Limited(), new RiskEngineConfig { MaxNotionalPerOrder = new Dictionary<InstrumentId, decimal> { [TestIds.BtcUsdt] = 1m } });

        SubmitOrder command = h.Submit(TestOrders.Market("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000"));

        AssertForwarded(h, command);
    }

    [Fact(Skip = "BUG: RiskEngine never checks the price increment (R4.1 requires 'precision and increments'); 50000.03 passes on a 0.05 tick")]
    public void Price_that_is_not_a_multiple_of_the_price_increment_is_denied()
    {
        RiskHarness h = HarnessWith(TestInstruments.BtcUsdt(priceIncrement: 0.05m));
        LimitOrder order = TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.03");

        h.Submit(order);

        Assert.Empty(h.Forwarded);
        Assert.Single(h.Denied);
    }

    [Fact(Skip = "BUG: RiskEngine never checks the size increment (R4.1 requires 'precision and increments'); 1.003 passes on a 0.005 step")]
    public void Quantity_that_is_not_a_multiple_of_the_size_increment_is_denied()
    {
        RiskHarness h = HarnessWith(TestInstruments.BtcUsdt(sizeIncrement: 0.005m));
        LimitOrder order = TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.003", "50000.00");

        h.Submit(order);

        Assert.Empty(h.Forwarded);
        Assert.Single(h.Denied);
    }

    [Fact]
    public void Price_and_quantity_on_the_increment_grid_are_accepted()
    {
        RiskHarness h = HarnessWith(TestInstruments.BtcUsdt(priceIncrement: 0.05m, sizeIncrement: 0.005m));

        SubmitOrder command = h.Submit(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.005", "50000.05"));

        AssertForwarded(h, command);
    }

    [Fact]
    public void Cancel_and_query_commands_pass_through_without_pre_trade_checks()
    {
        // The instrument is not even cached: cancelling must always be possible.
        RiskHarness h = new();
        CancelOrder cancel = new(TestIds.Trader, TestIds.Strategy, TestIds.BtcUsdt, new ClientOrderId("O-1"), null, null, Guid.NewGuid(), h.Clock.Timestamp);
        CancelAllOrders cancelAll = new(TestIds.Trader, TestIds.Strategy, TestIds.BtcUsdt, null, null, Guid.NewGuid(), h.Clock.Timestamp);
        BatchCancelOrders batch = new(TestIds.Trader, TestIds.Strategy, TestIds.BtcUsdt, [cancel], null, Guid.NewGuid(), h.Clock.Timestamp);
        QueryOrder query = new(TestIds.Trader, TestIds.Strategy, TestIds.BtcUsdt, new ClientOrderId("O-1"), null, null, Guid.NewGuid(), h.Clock.Timestamp);

        h.Bus.Send(Endpoints.RiskEngineExecute, cancel);
        h.Bus.Send(Endpoints.RiskEngineExecute, cancelAll);
        h.Bus.Send(Endpoints.RiskEngineExecute, batch);
        h.Bus.Send(Endpoints.RiskEngineExecute, query);

        Assert.Equal(new TradingCommand[] { cancel, cancelAll, batch, query }, h.Forwarded);
        Assert.Empty(h.Events);
    }

    [Fact]
    public void Message_that_is_not_a_trading_command_is_dropped()
    {
        RiskHarness h = new();

        h.Bus.Send(Endpoints.RiskEngineExecute, "not a command");

        Assert.Empty(h.Forwarded);
        Assert.Empty(h.Events);
        Assert.Equal(1, h.Engine.CommandCount);
    }

    [Fact]
    public void Counters_track_commands_and_denials_separately()
    {
        RiskHarness h = HarnessWith(Limited());

        h.Submit(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));
        h.Submit(TestOrders.Limit("O-2", TestIds.BtcUsdt, OrderSide.Buy, "200.000", "50000.00"));
        h.Submit(TestOrders.Limit("O-3", TestIds.EthUsdt, OrderSide.Buy, "1.000", "3000.00"));

        Assert.Equal(3, h.Engine.CommandCount);
        Assert.Equal(2, h.Engine.DeniedCount);
    }

    [Fact]
    public void Denials_are_still_emitted_when_denial_logging_is_switched_off()
    {
        RiskHarness h = new(new RiskEngineConfig { LogDenials = false });
        LimitOrder order = TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00");

        h.Submit(order);

        Assert.Single(h.Denied);
    }
}
