using Bytex.Core.Model;
using Bytex.Core.Model.Accounts;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Positions;
using Bytex.Core.Model.Primitives;
using static Bytex.Core.Tests.Model.ModelFixtures;

namespace Bytex.Core.Tests.Model;

// Accounts hold the balances that risk checks and reports read. These tests protect balance bookkeeping
// (total / locked / free per currency), rejection of foreign events, and the hand-computed formulas for
// cash movements, locked amounts, leveraged margins and realized P&L settlement.
public class AccountTests
{
    private static AccountState State(
        AccountType type,
        Currency? baseCurrency,
        IReadOnlyList<AccountBalance> balances,
        IReadOnlyList<MarginBalance>? margins = null,
        string accountId = "BINANCE-001",
        long t = 0) =>
        new(new AccountId(accountId), type, baseCurrency, true, balances, margins ?? [], new Dictionary<string, string>(), Id(300), At(t), At(t));

    private static CashAccount MultiCurrencyCash() => new(State(
        AccountType.Cash,
        null,
        [
            AccountBalance.Of(Usdt("100000"), Usdt("25000")),
            AccountBalance.Unlocked(new Money(2m, Currencies.BTC)),
        ]));

    private static MarginAccount UsdtMargin(IReadOnlyList<MarginBalance>? margins = null) =>
        new(State(AccountType.Margin, Currencies.USDT, [AccountBalance.Unlocked(Usdt("50000"))], margins));

    [Fact]
    public void Balance_Of_derives_free_as_total_minus_locked()
    {
        AccountBalance balance = AccountBalance.Of(new Money(1000m, Currencies.USD), new Money(250.50m, Currencies.USD));

        Assert.Equal(new Money(749.50m, Currencies.USD), balance.Free);
        Assert.Equal(Currencies.USD, balance.Currency);
    }

    [Fact]
    public void Balance_Unlocked_has_everything_free()
    {
        AccountBalance balance = AccountBalance.Unlocked(new Money(2m, Currencies.BTC));

        Assert.Equal(new Money(2m, Currencies.BTC), balance.Free);
        Assert.True(balance.Locked.IsZero);
        Assert.Equal(Currencies.BTC, balance.Locked.Currency);
    }

    [Fact]
    public void Balance_Of_rejects_mixed_currencies()
    {
        Assert.Throws<InvalidOperationException>(() => AccountBalance.Of(new Money(1m, Currencies.USD), new Money(1m, Currencies.EUR)));
    }

    [Fact]
    public void The_initial_state_sets_identity_and_balances()
    {
        CashAccount account = MultiCurrencyCash();

        Assert.Equal(new AccountId("BINANCE-001"), account.Id);
        Assert.Equal(AccountType.Cash, account.Type);
        Assert.Null(account.BaseCurrency);
        Assert.True(account.IsMultiCurrency);
        Assert.False(account.Calculated);
        Assert.Equal(1, account.EventCount);
        Assert.Equal(2, account.Currencies.Count);
        Assert.Equal(Usdt("100000"), account.BalanceTotal(Currencies.USDT));
        Assert.Equal(Usdt("25000"), account.BalanceLocked(Currencies.USDT));
        Assert.Equal(Usdt("75000"), account.BalanceFree(Currencies.USDT));
        Assert.Equal(new Money(2m, Currencies.BTC), account.BalanceFree(Currencies.BTC));
    }

    [Fact]
    public void A_currency_the_account_does_not_hold_has_no_balance()
    {
        CashAccount account = MultiCurrencyCash();

        Assert.Null(account.BalanceTotal(Currencies.ETH));
        Assert.Null(account.BalanceFree(Currencies.ETH));
        Assert.Null(account.BalanceLocked(Currencies.ETH));
        Assert.Null(account.Balance(Currencies.ETH));
    }

    [Fact]
    public void A_multi_currency_account_needs_the_currency_to_be_named()
    {
        Assert.Throws<InvalidOperationException>(() => MultiCurrencyCash().BalanceTotal());
    }

    [Fact]
    public void A_single_currency_account_defaults_to_its_base_currency()
    {
        MarginAccount account = UsdtMargin();

        Assert.False(account.IsMultiCurrency);
        Assert.Equal(Usdt("50000"), account.BalanceTotal());
        Assert.Equal(Usdt("50000"), account.BalanceFree());
        Assert.Equal(Usdt("0"), account.BalanceLocked());
    }

    [Fact]
    public void Applying_a_state_replaces_reported_balances_and_keeps_the_others()
    {
        CashAccount account = MultiCurrencyCash();
        AccountState update = State(AccountType.Cash, null, [AccountBalance.Of(Usdt("90000"), Usdt("10000"))], t: 5);

        account.Apply(update);

        Assert.Equal(Usdt("90000"), account.BalanceTotal(Currencies.USDT));
        Assert.Equal(Usdt("80000"), account.BalanceFree(Currencies.USDT));
        Assert.Equal(new Money(2m, Currencies.BTC), account.BalanceTotal(Currencies.BTC)); // not in the update, so untouched
        Assert.Equal(2, account.EventCount);
        Assert.Same(update, account.LastEvent);
    }

    [Fact]
    public void A_state_for_another_account_is_rejected_and_changes_nothing()
    {
        CashAccount account = MultiCurrencyCash();
        AccountState foreign = State(AccountType.Cash, null, [AccountBalance.Unlocked(Usdt("1"))], accountId: "BINANCE-999");

        Assert.Throws<ArgumentException>(() => account.Apply(foreign));
        Assert.Equal(Usdt("100000"), account.BalanceTotal(Currencies.USDT));
        Assert.Equal(1, account.EventCount);
    }

    [Fact]
    public void Missing_arguments_are_rejected()
    {
        CashAccount account = MultiCurrencyCash();

        Assert.Throws<ArgumentNullException>(() => new CashAccount(null!));
        Assert.Throws<ArgumentNullException>(() => account.Apply(null!));
        Assert.Throws<ArgumentNullException>(() => account.UpdateBalance(null!));
    }

    [Fact]
    public void Engine_side_balance_updates_do_not_add_events()
    {
        CashAccount account = new(State(AccountType.Cash, null, [AccountBalance.Unlocked(Usdt("1000"))]), calculateAccountState: true);

        account.UpdateBalance(AccountBalance.Of(Usdt("1000"), Usdt("400")));
        account.UpdateBalances([AccountBalance.Unlocked(new Money(0.5m, Currencies.BTC)), AccountBalance.Unlocked(new Money(3m, Currencies.ETH))]);

        Assert.True(account.Calculated);
        Assert.Equal(Usdt("600"), account.BalanceFree(Currencies.USDT));
        Assert.Equal(new Money(0.5m, Currencies.BTC), account.BalanceTotal(Currencies.BTC));
        Assert.Equal(new Money(3m, Currencies.ETH), account.BalanceTotal(Currencies.ETH));
        Assert.Equal(1, account.EventCount);
    }

    [Fact]
    public void A_cash_buy_pays_quote_currency_and_receives_base_currency()
    {
        CurrencyPair spot = BtcUsdt();
        OrderFilled fill = Fill(spot, OrderSide.Buy, "0.500000", "50000.00", "T-1");

        IReadOnlyList<Money> changes = MultiCurrencyCash().CalculatePnls(spot, fill, null);

        // 0.5 BTC * 50 000 = 25 000 USDT.
        Assert.Equal([new Money(0.5m, Currencies.BTC), Usdt("-25000")], changes);
    }

    [Fact]
    public void A_cash_sell_delivers_base_currency_and_receives_quote_currency()
    {
        CurrencyPair spot = BtcUsdt();
        OrderFilled fill = Fill(spot, OrderSide.Sell, "0.250000", "48000.00", "T-1");

        IReadOnlyList<Money> changes = MultiCurrencyCash().CalculatePnls(spot, fill, null);

        // 0.25 BTC * 48 000 = 12 000 USDT.
        Assert.Equal([new Money(-0.25m, Currencies.BTC), Usdt("12000")], changes);
    }

    [Fact]
    public void A_cash_trade_in_an_instrument_without_a_base_currency_moves_only_the_quote_currency()
    {
        Equity stock = new(EsFutureSpec() with { Id = InstrumentId.Parse("AAPL.XNAS"), Multiplier = null, PriceIncrement = new Price(0.01m, 2) });
        OrderFilled fill = Fill(stock, OrderSide.Buy, "10", "150.25", "T-1");

        IReadOnlyList<Money> changes = MultiCurrencyCash().CalculatePnls(stock, fill, null);

        Assert.Equal([new Money(-1502.50m, Currencies.USD)], changes);
    }

    [Fact]
    public void An_open_buy_locks_its_notional_and_an_open_sell_locks_the_base_quantity()
    {
        CashAccount account = MultiCurrencyCash();
        CurrencyPair spot = BtcUsdt();

        Money buyLock = account.CalculateBalanceLocked(spot, OrderSide.Buy, Quantity.Parse("0.500000"), Price.Parse("50000.00"));
        Money sellLock = account.CalculateBalanceLocked(spot, OrderSide.Sell, Quantity.Parse("0.500000"), Price.Parse("50000.00"));

        Assert.Equal(Usdt("25000"), buyLock);
        Assert.Equal(new Money(0.5m, Currencies.BTC), sellLock);
    }

    [Fact]
    public void Commission_is_the_instrument_fee_rate_on_the_fill_notional()
    {
        CashAccount account = MultiCurrencyCash();
        CurrencyPair spot = BtcUsdt();

        // Notional 25 000 USDT; taker 5 bps = 12.50, maker 2 bps = 5.00.
        Assert.Equal(Usdt("12.50"), account.CalculateCommission(spot, Quantity.Parse("0.500000"), Price.Parse("50000.00"), LiquiditySide.Taker));
        Assert.Equal(Usdt("5.00"), account.CalculateCommission(spot, Quantity.Parse("0.500000"), Price.Parse("50000.00"), LiquiditySide.Maker));
    }

    [Fact]
    public void Leverage_defaults_to_one_and_can_be_set_per_instrument()
    {
        MarginAccount account = UsdtMargin();
        InstrumentId eth = EthPerp().Id;
        InstrumentId other = BtcUsdt().Id;

        Assert.Equal(1m, account.Leverage(eth));

        account.SetDefaultLeverage(5m);
        account.SetLeverage(eth, 20m);

        Assert.Equal(20m, account.Leverage(eth));
        Assert.Equal(5m, account.Leverage(other));
        Assert.Equal(20m, account.Leverages[eth]);
    }

    [Theory]
    [InlineData("0.99")]
    [InlineData("0")]
    [InlineData("-10")]
    public void Leverage_below_one_is_rejected(string leverage)
    {
        MarginAccount account = UsdtMargin();

        Assert.Throws<ArgumentOutOfRangeException>(() => account.SetDefaultLeverage(D(leverage)));
        Assert.Throws<ArgumentOutOfRangeException>(() => account.SetLeverage(EthPerp().Id, D(leverage)));
        Assert.Equal(1m, account.DefaultLeverage);
    }

    [Fact]
    public void Initial_margin_is_the_larger_of_the_leverage_share_and_the_instruments_floor()
    {
        MarginAccount account = UsdtMargin();
        CryptoPerpetual perp = EthPerp();

        // Notional 2 * 2000 = 4000. At 10x a tenth of it has to be posted: 400. Maintenance does not depend on
        // leverage at all - what was chosen decides what has to be posted to open, not what has to be kept - so it
        // stays the instrument's 2.5% of notional: 100.
        account.SetLeverage(perp.Id, 10m);
        Assert.Equal(Usdt("400"), account.CalculateInitialMargin(perp, Quantity.Parse("2.000"), Price.Parse("2000.00")));
        Assert.Equal(Usdt("100"), account.CalculateMaintenanceMargin(perp, Quantity.Parse("2.000"), Price.Parse("2000.00")));

        // No leverage means posting the whole notional; the maintenance owed is the same either way.
        account.SetLeverage(perp.Id, 1m);
        Assert.Equal(Usdt("4000"), account.CalculateInitialMargin(perp, Quantity.Parse("2.000"), Price.Parse("2000.00")));
        Assert.Equal(Usdt("100"), account.CalculateMaintenanceMargin(perp, Quantity.Parse("2.000"), Price.Parse("2000.00")));

        // The instrument asks 5% at least, so leverage past 20x buys nothing: 5% of 4000 is 200 at 20x and at 50x.
        account.SetLeverage(perp.Id, 20m);
        Assert.Equal(Usdt("200"), account.CalculateInitialMargin(perp, Quantity.Parse("2.000"), Price.Parse("2000.00")));
        account.SetLeverage(perp.Id, 50m);
        Assert.Equal(Usdt("200"), account.CalculateInitialMargin(perp, Quantity.Parse("2.000"), Price.Parse("2000.00")));
    }

    [Fact]
    public void Inverse_margins_are_in_base_currency()
    {
        MarginAccount account = UsdtMargin();
        CryptoPerpetual inverse = XbtUsdInverse();
        account.SetLeverage(inverse.Id, 20m);

        // Notional 100 000 / 50 000 = 2 BTC. At 20x a twentieth has to be posted, which is more than the
        // instrument's 1% floor, so 0.1 BTC.
        Assert.Equal(new Money(0.1m, Currencies.BTC), account.CalculateInitialMargin(inverse, Quantity.Parse("100000"), Price.Parse("50000.0")));
    }

    [Fact]
    public void Reported_margins_are_kept_per_instrument()
    {
        InstrumentId eth = EthPerp().Id;
        InstrumentId btc = BtcUsdt().Id;
        MarginAccount account = UsdtMargin([new MarginBalance(Usdt("200"), Usdt("100"), eth)]);

        Assert.Equal(Usdt("200"), account.Margins[eth].Initial);

        account.Apply(State(AccountType.Margin, Currencies.USDT, [], [new MarginBalance(Usdt("300"), Usdt("150"), eth), new MarginBalance(Usdt("50"), Usdt("25"), btc)], t: 1));

        Assert.Equal(Usdt("300"), account.Margins[eth].Initial);
        Assert.Equal(Usdt("150"), account.Margins[eth].Maintenance);
        Assert.Equal(Usdt("25"), account.Margins[btc].Maintenance);
        Assert.Equal(Currencies.USDT, account.Margins[btc].Currency);

        account.UpdateMargin(new MarginBalance(Usdt("75"), Usdt("30"), btc));
        account.ClearMargin(eth);

        Assert.False(account.Margins.ContainsKey(eth));
        Assert.Equal(Usdt("75"), account.Margins[btc].Initial);
        Assert.Throws<ArgumentNullException>(() => account.UpdateMargin(null!));
    }

    [Fact]
    public void A_margin_fill_that_only_opens_a_position_settles_nothing()
    {
        MarginAccount account = UsdtMargin();
        CryptoPerpetual perp = EthPerp();
        OrderFilled open = Fill(perp, OrderSide.Buy, "10.000", "2000.00", "T-1", Usdt("4.00"));
        Position position = new(perp, open);

        Assert.Empty(account.CalculatePnls(perp, open, position));
        Assert.Empty(account.CalculatePnls(perp, open, null));
    }

    [Fact]
    public void A_margin_fill_that_closes_settles_the_gross_pnl_excluding_commission()
    {
        MarginAccount account = UsdtMargin();
        CryptoPerpetual perp = EthPerp();
        Position position = new(perp, Fill(perp, OrderSide.Buy, "10.000", "2000.00", "T-1", Usdt("4.00")));
        OrderFilled close = Fill(perp, OrderSide.Sell, "10.000", "2010.00", "T-2", Usdt("4.02"));
        position.Apply(close);

        IReadOnlyList<Money> pnls = account.CalculatePnls(perp, close, position);

        // (2010 - 2000) * 10 = 100; the 4.02 commission is charged separately by the caller.
        Assert.Equal([Usdt("100")], pnls);
    }

    [Fact]
    public void A_margin_fill_settles_only_its_own_part_of_a_staged_exit()
    {
        MarginAccount account = UsdtMargin();
        CryptoPerpetual perp = EthPerp();
        Position position = new(perp, Fill(perp, OrderSide.Buy, "10.000", "2000.00", "T-1", Usdt("1.00")));
        position.Apply(Fill(perp, OrderSide.Sell, "4.000", "2010.00", "T-2", Usdt("1.00")));
        OrderFilled last = Fill(perp, OrderSide.Sell, "6.000", "2020.00", "T-3", Usdt("1.00"));
        position.Apply(last);

        // Only the last fill: (2020 - 2000) * 6 = 120; the earlier 40 was settled with T-2.
        Assert.Equal([Usdt("120")], account.CalculatePnls(perp, last, position));
    }

    [Fact]
    public void A_margin_fill_that_is_not_the_positions_latest_settles_nothing()
    {
        MarginAccount account = UsdtMargin();
        CryptoPerpetual perp = EthPerp();
        OrderFilled open = Fill(perp, OrderSide.Buy, "10.000", "2000.00", "T-1");
        Position position = new(perp, open);
        position.Apply(Fill(perp, OrderSide.Sell, "10.000", "2010.00", "T-2"));

        Assert.Empty(account.CalculatePnls(perp, open, position));
    }
}
