using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Accounts;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;

namespace Bytex.Backtest.Tests;

// Why: on a cash (spot) account every fill moves two balances and a fee. The arithmetic is simple enough to do by
// hand, so every balance below is asserted to the last decimal.
public sealed class CashAccountTests
{
    [Fact]
    public void Starting_balances_are_published_as_the_first_account_state_in_every_configured_currency()
    {
        using SimHarness sim = SimHarness.Spot(new SimOptions
        {
            StartingBalances = [new Money(5000m, Currencies.USDT), new Money(2m, Currencies.BTC), new Money(30m, Currencies.ETH)],
        });
        sim.Quote(1000, 100.00m, 100.10m).Run();

        AccountState initial = Assert.Single(sim.Events.OfType<AccountState>());
        Assert.Equal(AccountType.Cash, initial.AccountType);
        Assert.Equal("SIM-001", initial.AccountId.Value);
        Dictionary<string, decimal> totals = initial.Balances.ToDictionary(b => b.Currency.Code, b => b.Total.Amount);
        Assert.Equal(3, totals.Count);
        Assert.Equal(5000m, totals["USDT"]);
        Assert.Equal(2m, totals["BTC"]);
        Assert.Equal(30m, totals["ETH"]);
        Assert.All(initial.Balances, b => Assert.Equal(b.Total, b.Free));
        Assert.Equal(Scripted.Ms(1000), initial.TsEvent);
    }

    [Fact]
    public void Buy_moves_quote_out_base_in_and_charges_the_taker_fee_in_quote_currency()
    {
        using SimHarness sim = SimHarness.Spot(new SimOptions { StartingBalances = [new Money(10_000m, Currencies.USDT)] });
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(2m))))
            .Quote(2000, 100.00m, 100.10m)
            .Run();

        // 2 * 100.10 = 200.20 notional; fee 200.20 * 0.002 = 0.4004; 10 000 - 200.20 - 0.4004 = 9 799.3996
        Assert.Equal(9_799.3996m, sim.Balance(Currencies.USDT));
        Assert.Equal(2m, sim.Balance(Currencies.BTC));
    }

    [Fact]
    public void Sell_moves_base_out_quote_in_and_charges_the_fee_in_quote_currency()
    {
        using SimHarness sim = SimHarness.Spot(new SimOptions { StartingBalances = [new Money(10m, Currencies.BTC)] });
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Sell, sim.Qty(4m))))
            .Quote(2000, 100.00m, 100.10m)
            .Run();

        // 4 * 100.00 = 400 received; fee 0.8; USDT = 399.2
        Assert.Equal(399.2m, sim.Balance(Currencies.USDT));
        Assert.Equal(6m, sim.Balance(Currencies.BTC));
    }

    [Fact]
    public void Maker_fill_is_charged_the_cheaper_maker_rate()
    {
        using SimHarness sim = SimHarness.Spot(new SimOptions { StartingBalances = [new Money(10_000m, Currencies.USDT)] });
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(10m), sim.Px(99.00m))))
            .Quote(2000, 98.80m, 98.90m)
            .Run();

        // 10 * 99.00 = 990; maker fee 0.99; 10 000 - 990 - 0.99 = 9 009.01
        Assert.Equal(9_009.01m, sim.Balance(Currencies.USDT));
        Assert.Equal(10m, sim.Balance(Currencies.BTC));
    }

    [Fact]
    public void Round_trip_leaves_the_price_difference_minus_both_fees()
    {
        using SimHarness sim = SimHarness.Spot(new SimOptions { StartingBalances = [new Money(10_000m, Currencies.USDT)] });
        sim.Quote(1000, 99.90m, 100.00m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(5m))))
            .Quote(2000, 110.00m, 110.10m)
            .At(2500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Sell, sim.Qty(5m))))
            .Quote(3000, 110.00m, 110.10m)
            .Run();

        // buy 5 @ 100.00 = 500, fee 1.00; sell 5 @ 110.00 = 550, fee 1.10; 10 000 - 500 - 1 + 550 - 1.1 = 10 047.9
        Assert.Equal(10_047.9m, sim.Balance(Currencies.USDT));
        Assert.Equal(0m, sim.Balance(Currencies.BTC));
    }

    [Fact]
    public void Account_state_after_each_fill_matches_the_venue_balances_and_is_stamped_with_the_fill_time()
    {
        using SimHarness sim = SimHarness.Spot(new SimOptions { StartingBalances = [new Money(10_000m, Currencies.USDT)] });
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(2m))))
            .Quote(2000, 100.00m, 100.10m)
            .Run();

        List<AccountState> states = sim.Events.OfType<AccountState>().ToList();
        Assert.Equal(2, states.Count);
        Assert.Equal(Scripted.Ms(1500), states[1].TsEvent);
        Assert.Equal(new Money(9_799.3996m, Currencies.USDT), states[1].Balances.Single(b => b.Currency.Equals(Currencies.USDT)).Total);
        Assert.Equal(new Money(2m, Currencies.BTC), states[1].Balances.Single(b => b.Currency.Equals(Currencies.BTC)).Total);

        Account cached = sim.Account;
        Assert.IsType<CashAccount>(cached);
        Assert.Equal(new Money(9_799.3996m, Currencies.USDT), cached.BalanceTotal(Currencies.USDT));
    }

    [Fact]
    public void Pre_trade_risk_check_denies_a_buy_the_account_cannot_afford_before_it_reaches_the_venue()
    {
        using SimHarness sim = SimHarness.Spot(new SimOptions { StartingBalances = [new Money(50m, Currencies.USDT)] });
        MarketOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(2000, 100.00m, 100.10m)
            .Run();

        Assert.Equal(OrderStatus.Denied, order!.Status);
        Assert.StartsWith("INSUFFICIENT_BALANCE", Assert.IsType<OrderDenied>(order.LastEvent).Reason, StringComparison.Ordinal);
        Assert.Equal(50m, sim.Balance(Currencies.USDT));
    }

    [Theory]
    [InlineData(OrderSide.Buy)] // 50 USDT cannot pay for 1 BTC at 100.10
    [InlineData(OrderSide.Sell)] // 0.5 BTC cannot deliver 1 BTC
    public void Venue_itself_rejects_an_order_the_account_cannot_cover(OrderSide side)
    {
        using SimHarness sim = SimHarness.Spot(new SimOptions
        {
            BypassRisk = true,
            StartingBalances = [new Money(50m, Currencies.USDT), new Money(0.5m, Currencies.BTC)],
        });
        MarketOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.Market(sim.Id, side, sim.Qty(1m))))
            .Quote(2000, 100.00m, 100.10m)
            .Run();

        Assert.Equal(OrderStatus.Rejected, order!.Status);
        Assert.Equal("insufficient balance", Assert.IsType<OrderRejected>(order.LastEvent).Reason);
        Assert.Equal(50m, sim.Balance(Currencies.USDT));
        Assert.Equal(0.5m, sim.Balance(Currencies.BTC));
    }

    [Fact]
    public void Venue_checks_a_limit_buy_against_its_limit_price_not_the_market_price()
    {
        // 95 USDT covers 1 BTC at a 90.00 limit even though the ask is 100.10.
        using SimHarness sim = SimHarness.Spot(new SimOptions { BypassRisk = true, StartingBalances = [new Money(95m, Currencies.USDT)] });
        LimitOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(90.00m))))
            .Quote(2000, 100.00m, 100.10m)
            .Run();

        Assert.Equal(OrderStatus.Accepted, order!.Status);
    }

    [Fact]
    public void Portfolio_reports_the_quote_currency_locked_by_a_resting_buy_order()
    {
        using SimHarness sim = SimHarness.Spot();
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(2m), sim.Px(99.00m))))
            .At(1600, s => s.Submit(s.Orders.Limit(sim.Id, OrderSide.Sell, sim.Qty(3m), sim.Px(101.00m))))
            .Quote(2000, 100.00m, 100.10m)
            .Run();

        IReadOnlyDictionary<Currency, Money> locked = sim.Engine.Kernel.Portfolio.BalancesLocked(TestInstruments.Sim);
        Assert.Equal(new Money(198m, Currencies.USDT), locked[Currencies.USDT]); // 2 * 99.00
        Assert.Equal(new Money(3m, Currencies.BTC), locked[Currencies.BTC]);
    }

    [Fact(Skip = "BUG: the simulated cash account never locks funds for resting orders (Locked is always 0), so the same balance backs any number of orders")]
    public void Resting_buy_order_locks_its_notional_in_the_account()
    {
        using SimHarness sim = SimHarness.Spot(new SimOptions { StartingBalances = [new Money(1000m, Currencies.USDT)] });
        LimitOrder? first = null;
        LimitOrder? second = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => first = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(8m), sim.Px(99.00m)))) // locks 792 of 1000
            .At(1600, s => second = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(8m), sim.Px(99.00m)))) // needs another 792
            .Quote(2000, 100.00m, 100.10m)
            .Run();

        Assert.Equal(OrderStatus.Accepted, first!.Status);
        Assert.Contains(second!.Status, new[] { OrderStatus.Denied, OrderStatus.Rejected });
        Assert.Equal(new Money(792m, Currencies.USDT), sim.Account.BalanceLocked(Currencies.USDT)); // 8 * 99.00
    }

    [Fact(Skip = "BUG: the affordability check ignores the commission, so a cash balance can go negative")]
    public void Cash_balance_never_goes_negative()
    {
        // Exactly 100.10 USDT buys 1 BTC at 100.10, but the 0.2002 taker fee is then taken from an empty balance.
        using SimHarness sim = SimHarness.Spot(new SimOptions { StartingBalances = [new Money(100.10m, Currencies.USDT)] });
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(2000, 100.00m, 100.10m)
            .Run();

        Assert.True(sim.Balance(Currencies.USDT) >= 0m, $"USDT balance is {sim.Balance(Currencies.USDT)}");
    }
}
