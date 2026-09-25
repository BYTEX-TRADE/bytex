using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Accounts;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Backtest.Tests;

// Why: on a margin account no asset changes hands; only realised P&L and fees touch the balance, in the
// settlement currency, and open positions reserve margin. Perpetual: taker 0.05 %, margin 5 % / 2.5 %.
public sealed class MarginAccountTests
{
    [Fact]
    public void Opening_a_position_costs_only_the_commission()
    {
        using SimHarness sim = SimHarness.Perp();
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(2000, 50_000.0m, 50_000.0m)
            .Run();

        // 50 000 * 0.0005 = 25
        Assert.Equal(99_975m, sim.Balance(Currencies.USDT));
        Assert.Equal(0m, sim.Balance(Currencies.BTC));
        Assert.IsType<MarginAccount>(sim.Account);
    }

    [Theory]
    [InlineData(OrderSide.Buy, 51_000.0, 949.5)] // long 1: +1000 gross, fees 25 + 25.5
    [InlineData(OrderSide.Buy, 49_000.0, -1_049.5)] // long 1: -1000 gross, fees 25 + 24.5
    [InlineData(OrderSide.Sell, 49_000.0, 950.5)] // short 1: +1000 gross, fees 25 + 24.5
    [InlineData(OrderSide.Sell, 51_000.0, -1_050.5)] // short 1: -1000 gross, fees 25 + 25.5
    public void Closing_a_position_settles_price_difference_minus_fees_in_the_settlement_currency(OrderSide entrySide, decimal exitPrice, decimal expectedChange)
    {
        using SimHarness sim = SimHarness.Perp();
        OrderSide exitSide = entrySide == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, entrySide, sim.Qty(1m))))
            .Quote(2000, exitPrice, exitPrice)
            .At(2500, s => s.Submit(s.Orders.Market(sim.Id, exitSide, sim.Qty(1m))))
            .Quote(3000, exitPrice, exitPrice)
            .Run();

        Assert.Equal(100_000m + expectedChange, sim.Balance(Currencies.USDT));
        Assert.Equal(new Money(expectedChange, Currencies.USDT), Assert.Single(sim.Positions).RealizedPnl);
    }

    [Fact]
    public void Partial_close_realises_profit_only_on_the_closed_quantity_at_the_average_entry_price()
    {
        using SimHarness sim = SimHarness.Perp(new SimOptions { FeeModel = new FixedFeeModel(Money.Zero(Currencies.USDT)) });
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1100, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(2000, 52_000.0m, 52_000.0m)
            .At(2100, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m)))) // average entry 51 000
            .Quote(3000, 53_000.0m, 53_000.0m)
            .At(3100, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Sell, sim.Qty(0.5m)))) // (53 000 - 51 000) * 0.5 = 1000
            .Quote(4000, 53_000.0m, 53_000.0m)
            .Run();

        Assert.Equal(101_000m, sim.Balance(Currencies.USDT));
        Assert.Equal(1.5m, sim.Engine.Kernel.Portfolio.NetPosition(sim.Id));
        Assert.Equal(51_000m, Assert.Single(sim.Positions).AvgPxOpen);
    }

    [Fact]
    public void Flipping_a_position_realises_only_the_closed_part_and_restarts_the_entry_price()
    {
        using SimHarness sim = SimHarness.Perp(new SimOptions { FeeModel = new FixedFeeModel(Money.Zero(Currencies.USDT)) });
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1100, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(2000, 51_000.0m, 51_000.0m)
            .At(2100, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Sell, sim.Qty(3m)))) // closes 1 (+1000), opens short 2 @ 51 000
            .Quote(3000, 50_500.0m, 50_500.0m)
            .At(3100, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(2m)))) // (51 000 - 50 500) * 2 = +1000
            .Quote(4000, 50_500.0m, 50_500.0m)
            .Run();

        Assert.Equal(102_000m, sim.Balance(Currencies.USDT));
        Assert.Equal(0m, sim.Engine.Kernel.Portfolio.NetPosition(sim.Id));
    }

    [Fact]
    public void Inverse_contract_settles_profit_and_fees_in_the_base_currency()
    {
        // Long 10 000 USD contracts from 50 000 to 62 500: (1/50 000 - 1/62 500) * 10 000 = 0.04 BTC.
        // Fees: 0.2 BTC * 0.0005 = 0.0001 on entry, 0.16 BTC * 0.0005 = 0.00008 on exit.
        Instrument inverse = TestInstruments.InversePerp();
        using SimHarness sim = SimHarness.For(inverse, new SimOptions { AccountType = AccountType.Margin, StartingBalances = [new Money(1m, Currencies.BTC)] });
        sim.Quote(1000, 50_000.0m, 50_000.0m, size: 10_000m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(10_000m))))
            .Quote(2000, 62_500.0m, 62_500.0m, size: 10_000m)
            .At(2500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Sell, sim.Qty(10_000m))))
            .Quote(3000, 62_500.0m, 62_500.0m, size: 10_000m)
            .Run();

        Assert.Equal(1m + 0.04m - 0.0001m - 0.00008m, sim.Balance(Currencies.BTC));
        Assert.False(sim.Exchange.Balances.ContainsKey(Currencies.USD));
    }

    [Fact]
    public void Margin_is_reported_per_instrument_while_a_position_is_open_and_released_when_it_closes()
    {
        // Leverage 20, notional 50 000. What has to be posted is a twentieth of the notional, which is also the
        // instrument's 5% floor: 2 500. What has to be kept is the instrument's 2.5% of notional, 1 250, and it does
        // not depend on leverage - what was chosen decides what opens a position, not what holds it.
        using SimHarness sim = SimHarness.Perp(new SimOptions { Leverages = new Dictionary<InstrumentId, decimal> { [TestInstruments.Perp().Id] = 20m } });
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(2000, 50_000.0m, 50_000.0m)
            .At(2500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Sell, sim.Qty(1m))))
            .Quote(3000, 50_000.0m, 50_000.0m)
            .Run();

        // Four: the opening balance, the order posting its margin, the fill that turned that hold into a position's
        // margin, and the close that gave it back. The closing order posts nothing, because closing gives margin back
        // rather than asking for more.
        List<AccountState> states = sim.Events.OfType<AccountState>().ToList();
        Assert.Equal(4, states.Count);
        Assert.Empty(states[0].Margins);
        Assert.Empty(states[1].Margins);

        MarginBalance open = Assert.Single(states[2].Margins);
        Assert.Equal(sim.Id, open.InstrumentId);
        Assert.Equal(new Money(2_500m, Currencies.USDT), open.Initial);
        Assert.Equal(new Money(1_250m, Currencies.USDT), open.Maintenance);
        AccountBalance balance = Assert.Single(states[2].Balances);
        Assert.Equal(new Money(99_975m, Currencies.USDT), balance.Total);
        Assert.Equal(new Money(2_500m, Currencies.USDT), balance.Locked);
        Assert.Equal(new Money(97_475m, Currencies.USDT), balance.Free);

        // The hold the order took before it filled is the same 2 500, and it is held out of a balance that has not
        // moved yet: the order cannot be paid for twice over, once as an order and again as a position.
        AccountBalance held = Assert.Single(states[1].Balances);
        Assert.Equal(new Money(100_000m, Currencies.USDT), held.Total);
        Assert.Equal(new Money(2_500m, Currencies.USDT), held.Locked);

        Assert.Empty(states[3].Margins);
        Assert.Equal(Money.Zero(Currencies.USDT), Assert.Single(states[3].Balances).Locked);
    }

    [Fact]
    public void Default_leverage_applies_to_instruments_without_their_own_setting()
    {
        // Default leverage 25 and no per-instrument override. The instrument asks 5% of notional at least, so 25x
        // buys nothing past 20x and the initial margin is 2 500 either way; the maintenance owed is the instrument's
        // 2.5% of the 50 000 notional, 1 250, whatever leverage was set.
        using SimHarness sim = SimHarness.Perp(new SimOptions { DefaultLeverage = 25m });
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Sell, sim.Qty(1m))))
            .Quote(2000, 50_000.0m, 50_000.0m)
            .Run();

        MarginBalance margin = Assert.Single(sim.Events.OfType<AccountState>().Last().Margins);
        Assert.Equal(new Money(1_250m, Currencies.USDT), margin.Maintenance);
        Assert.Equal(new Money(2_500m, Currencies.USDT), margin.Initial);
    }

    // One formula for one number: the venue used to floor the initial margin at a ratio of its own, so a backtest's
    // margin and the account's disagreed at any leverage above 1 / MarginInit.
    [Fact]
    public void Venue_initial_margin_agrees_with_the_margin_account_formula_for_the_same_leverage()
    {
        using SimHarness sim = SimHarness.Perp(new SimOptions { DefaultLeverage = 10m });
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(2000, 50_000.0m, 50_000.0m)
            .Run();

        MarginAccount account = Assert.IsType<MarginAccount>(sim.Account);
        account.SetDefaultLeverage(10m);
        Money expected = account.CalculateInitialMargin(sim.Instrument, sim.Qty(1m), sim.Px(50_000.0m)); // a tenth of 50 000
        Assert.Equal(new Money(5_000m, Currencies.USDT), expected);
        Assert.Equal(expected, Assert.Single(sim.Events.OfType<AccountState>().Last().Margins).Initial);
    }

    [Fact]
    public void A_venue_configured_with_a_leverage_its_arithmetic_cannot_use_says_so_when_it_is_built()
    {
        // An account's own setter refuses a leverage below 1; the venue that feeds it has to refuse one too, where
        // the number was written. Zero used to divide the margin arithmetic by nothing when the first position opened.
        Assert.Throws<ArgumentOutOfRangeException>(() => SimHarness.Perp(new SimOptions { DefaultLeverage = 0m }));
        Assert.Throws<ArgumentOutOfRangeException>(() => SimHarness.Perp(new SimOptions { DefaultLeverage = 0.5m }));
        Assert.Throws<ArgumentOutOfRangeException>(() => SimHarness.Perp(new SimOptions { Leverages = new Dictionary<InstrumentId, decimal> { [TestInstruments.Perp().Id] = 0m } }));
    }

    [Fact]
    public void Margin_account_lets_a_strategy_sell_short_without_holding_the_base_asset()
    {
        using SimHarness sim = SimHarness.Perp();
        Core.Model.Orders.MarketOrder? order = null;
        sim.Quote(1000, 50_000.0m, 50_000.1m)
            .At(1500, s => order = s.Submit(s.Orders.Market(sim.Id, OrderSide.Sell, sim.Qty(3m))))
            .Quote(2000, 50_000.0m, 50_000.1m)
            .Run();

        Assert.Equal(OrderStatus.Filled, order!.Status);
        Assert.Equal(-3m, sim.Engine.Kernel.Portfolio.NetPosition(sim.Id));
    }
}
