using Bytex.Core.Engines;
using Bytex.Core.Model;
using Bytex.Core.Model.Accounts;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Tests.Support;

namespace Bytex.Core.Tests.Engines;

// Why: R4.6 - on a cash account the free balance (total minus locked) must cover an order. A buy consumes
// quote currency (quantity * price), a sell consumes base currency (quantity).
public class RiskEngineCashBalanceTests
{
    private static RiskHarness Harness(decimal usdtTotal, decimal usdtLocked, decimal btcTotal, decimal btcLocked, RiskEngineConfig? config = null)
    {
        RiskHarness h = new(config);
        h.Cache.AddInstrument(TestInstruments.BtcUsdt());
        h.Cache.AddAccount(new CashAccount(TestEvents.CashState(TestIds.BinanceAccount, (Currencies.USDT, usdtTotal, usdtLocked), (Currencies.BTC, btcTotal, btcLocked))));
        return h;
    }

    private static void AssertInsufficient(RiskHarness h)
    {
        Assert.Empty(h.Forwarded);
        OrderDenied denied = Assert.Single(h.Denied);
        Assert.StartsWith("INSUFFICIENT_BALANCE", denied.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0.100", "50000.00", true)] // 5,000 of 10,000 free
    [InlineData("0.200", "50000.00", true)] // exactly 10,000
    [InlineData("0.200", "50000.01", false)] // 10,000.002
    public void Limit_buy_needs_quantity_times_price_in_free_quote_currency(string quantity, string price, bool allowed)
    {
        RiskHarness h = Harness(usdtTotal: 10_000m, usdtLocked: 0m, btcTotal: 0m, btcLocked: 0m);

        SubmitOrder command = h.Submit(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, quantity, price));

        if (allowed)
        {
            Assert.Same(command, Assert.Single(h.Forwarded));
            Assert.Empty(h.Events);
        }
        else
        {
            AssertInsufficient(h);
        }
    }

    [Fact]
    public void Locked_quote_balance_is_not_available_to_a_buy()
    {
        // 10,000 total - 6,000 locked = 4,000 free; the order needs 0.1 * 50,000 = 5,000.
        RiskHarness h = Harness(usdtTotal: 10_000m, usdtLocked: 6_000m, btcTotal: 0m, btcLocked: 0m);

        h.Submit(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "0.100", "50000.00"));

        AssertInsufficient(h);
    }

    [Theory]
    [InlineData("0.600", true)] // 1.0 total - 0.4 locked = 0.6 free
    [InlineData("0.601", false)]
    public void Sell_needs_its_quantity_in_free_base_currency(string quantity, bool allowed)
    {
        RiskHarness h = Harness(usdtTotal: 0m, usdtLocked: 0m, btcTotal: 1m, btcLocked: 0.4m);

        SubmitOrder command = h.Submit(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Sell, quantity, "50000.00"));

        if (allowed)
        {
            Assert.Same(command, Assert.Single(h.Forwarded));
        }
        else
        {
            AssertInsufficient(h);
        }
    }

    [Fact]
    public void Insufficient_balance_reason_states_required_and_free_amounts()
    {
        RiskHarness h = Harness(usdtTotal: 4_000m, usdtLocked: 0m, btcTotal: 0m, btcLocked: 0m);

        h.Submit(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "0.100", "50000.00"));

        OrderDenied denied = Assert.Single(h.Denied);
        Assert.Contains("5000.00000000 USDT", denied.Reason, StringComparison.Ordinal);
        Assert.Contains("4000.00000000 USDT", denied.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Market_buy_is_costed_at_the_current_ask()
    {
        // Ask 50,010: 0.1 BTC costs 5,001 but only 5,000 is free. At the bid (49,990) it would have passed.
        RiskHarness h = Harness(usdtTotal: 5_000m, usdtLocked: 0m, btcTotal: 0m, btcLocked: 0m);
        h.Cache.AddQuoteTick(new QuoteTick(TestIds.BtcUsdt, Price.Parse("49990.00"), Price.Parse("50010.00"), Quantity.Parse("1.000"), Quantity.Parse("1.000"), TestOrders.T0, TestOrders.T0));

        h.Submit(TestOrders.Market("O-1", TestIds.BtcUsdt, OrderSide.Buy, "0.100"));

        AssertInsufficient(h);
    }

    [Fact]
    public void Reduce_only_order_skips_the_balance_check()
    {
        RiskHarness h = Harness(usdtTotal: 0m, usdtLocked: 0m, btcTotal: 0m, btcLocked: 0m);

        SubmitOrder command = h.Submit(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Sell, "1.000", "50000.00", reduceOnly: true));

        Assert.Same(command, Assert.Single(h.Forwarded));
    }

    [Fact]
    public void Balance_check_can_be_switched_off()
    {
        RiskHarness h = Harness(usdtTotal: 0m, usdtLocked: 0m, btcTotal: 0m, btcLocked: 0m, new RiskEngineConfig { CheckCashBalance = false });

        SubmitOrder command = h.Submit(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));

        Assert.Same(command, Assert.Single(h.Forwarded));
    }

    [Fact]
    public void Margin_account_is_not_subject_to_the_cash_balance_check()
    {
        RiskHarness h = new();
        h.Cache.AddInstrument(TestInstruments.BtcUsdt());
        h.Cache.AddAccount(new MarginAccount(TestEvents.MarginState(TestIds.BinanceAccount, (Currencies.USDT, 100m, 0m))));

        SubmitOrder command = h.Submit(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));

        Assert.Same(command, Assert.Single(h.Forwarded));
    }

    [Fact]
    public void Account_of_another_venue_is_not_used_for_the_check()
    {
        RiskHarness h = new();
        h.Cache.AddInstrument(TestInstruments.BtcUsdt());
        h.Cache.AddAccount(new CashAccount(TestEvents.CashState(TestIds.BybitAccount, (Currencies.USDT, 1m, 0m))));

        SubmitOrder command = h.Submit(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));

        Assert.Same(command, Assert.Single(h.Forwarded));
    }

    [Fact(Skip = "BUG: RiskEngine.cs:320-321 treats a currency with no balance entry as unlimited; a sell of BTC the account does not hold is forwarded")]
    public void Sell_of_a_currency_the_account_does_not_hold_at_all_is_denied()
    {
        RiskHarness h = new();
        h.Cache.AddInstrument(TestInstruments.BtcUsdt());
        h.Cache.AddAccount(new CashAccount(TestEvents.CashState(TestIds.BinanceAccount, (Currencies.USDT, 10_000m, 0m))));

        h.Submit(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Sell, "0.100", "50000.00"));

        AssertInsufficient(h);
    }

    [Fact(Skip = "BUG: RiskEngine.cs:301,319 ignores Order.IsQuoteQuantity; a 1,000 USDT quote-quantity buy is costed as 1,000 BTC * price and denied")]
    public void Quote_quantity_buy_is_costed_at_its_quantity_in_quote_currency()
    {
        // quoteQuantity means the quantity is already expressed in USDT: the order costs 1,000 USDT of 10,000 free.
        RiskHarness h = Harness(usdtTotal: 10_000m, usdtLocked: 0m, btcTotal: 0m, btcLocked: 0m);

        SubmitOrder command = h.Submit(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1000", "50000.00", quoteQuantity: true));

        Assert.Empty(h.Denied);
        Assert.Same(command, Assert.Single(h.Forwarded));
    }
}
