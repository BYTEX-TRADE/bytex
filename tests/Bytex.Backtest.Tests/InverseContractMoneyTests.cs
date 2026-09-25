using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Accounts;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Positions;
using Bytex.Core.Model.Primitives;

namespace Bytex.Backtest.Tests;

// Why: an inverse contract is quoted in USD, sized in USD contracts and settled in BTC, so every money number
// inverts - profit, margin, fees and funding all come out in the base currency and all of them go through
// 1/price. The whole suite had six tests mentioning inverse contracts, one of which was arithmetically wrong until
// the margin model was fixed, and the margin model changing is exactly the kind of change that quietly breaks the
// inverted case while the linear case stays green.
//
// Every number below is derived from the engine's own formula and written out, so a reader can check the arithmetic
// rather than trust it:
//   notional   = contracts / price                (base currency)
//   pnl long   = (1/open - 1/close) * contracts
//   initial    = notional * max(1/leverage, marginInit)
//   maintenance= notional * marginMaint
public sealed class InverseContractMoneyTests
{
    /// <summary>10 000 USD of contracts at 50 000 is 0.2 BTC of notional.</summary>
    private const decimal Contracts = 10_000m;

    private static SimOptions Inverse(decimal leverage = 20m, bool liquidate = false) => new()
    {
        AccountType = AccountType.Margin,
        StartingBalances = [new Money(1m, Currencies.BTC)],
        DefaultLeverage = leverage,
        FeeModel = new FixedFeeModel(Money.Zero(Currencies.BTC)),
        Liquidate = liquidate,
    };

    private static SimHarness Harness(SimOptions? options = null) =>
        SimHarness.For(TestInstruments.InversePerp(), options ?? Inverse());

    [Fact]
    public void Notional_and_margin_are_in_the_base_currency_and_go_through_the_inverse_price()
    {
        CryptoPerpetual inverse = TestInstruments.InversePerp();

        Money notional = inverse.NotionalValue(inverse.MakeQuantity(Contracts), inverse.MakePrice(50_000.0m));

        // 10 000 / 50 000 = 0.2 BTC, and the currency is the base one - a settlement in USDT here would be a
        // different contract entirely.
        Assert.Equal(Currencies.BTC, notional.Currency);
        Assert.Equal(0.2m, notional.Amount);

        // This instrument asks 5% of notional at least, so a twentieth is both the 20x share and the floor: 0.01 BTC
        // posted against 0.2 BTC of notional.
        Assert.Equal(0.05m, inverse.MarginInit);
        Assert.Equal(0.05m, inverse.InitialMarginRate(20m));
        Assert.Equal(0.01m, notional.Amount * inverse.InitialMarginRate(20m));

        // No leverage means posting the whole notional; leverage past the floor buys nothing at all.
        Assert.Equal(1m, inverse.InitialMarginRate(1m));
        Assert.Equal(0.1m, inverse.InitialMarginRate(10m));
        Assert.Equal(0.05m, inverse.InitialMarginRate(100m));
        Assert.Equal(0.05m, inverse.InitialMarginRate(500m));
    }

    [Fact]
    public void Maintenance_margin_does_not_move_with_leverage_on_an_inverse_contract_either()
    {
        CryptoPerpetual inverse = TestInstruments.InversePerp();
        Money notional = inverse.NotionalValue(inverse.MakeQuantity(Contracts), inverse.MakePrice(50_000.0m));

        Assert.Equal(inverse.MarginMaint, inverse.MaintenanceMarginRate);
        Assert.Equal(notional.Amount * inverse.MarginMaint, notional.Amount * inverse.MaintenanceMarginRate);
    }

    [Fact]
    public void A_long_that_gains_is_paid_in_the_base_currency_through_the_inverse_difference()
    {
        using SimHarness sim = Harness();
        sim.Quote(1000, 50_000.0m, 50_000.0m, size: 1_000_000m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(Contracts))))
            .Quote(2000, 62_500.0m, 62_500.0m, size: 1_000_000m)
            .At(2500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Sell, sim.Qty(Contracts))))
            .Quote(3000, 62_500.0m, 62_500.0m, size: 1_000_000m)
            .Run();

        // (1/50 000 - 1/62 500) * 10 000 = 0.04 BTC. A linear contract of the same size would have paid in USDT and
        // a different amount; getting this wrong pays a strategy in the wrong currency.
        Position position = Assert.Single(sim.Positions);
        Assert.Equal(Currencies.BTC, position.RealizedPnl.Currency);
        Assert.Equal(0.04m, position.RealizedPnl.Amount);
        Assert.Equal(1m + 0.04m, sim.Balance(Currencies.BTC));
    }

    [Fact]
    public void A_short_that_gains_is_the_same_arithmetic_with_the_sign_turned_round()
    {
        using SimHarness sim = Harness();
        sim.Quote(1000, 50_000.0m, 50_000.0m, size: 1_000_000m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Sell, sim.Qty(Contracts))))
            .Quote(2000, 40_000.0m, 40_000.0m, size: 1_000_000m)
            .At(2500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(Contracts))))
            .Quote(3000, 40_000.0m, 40_000.0m, size: 1_000_000m)
            .Run();

        // -(1/50 000 - 1/40 000) * 10 000 = 0.05 BTC to a short as the price falls.
        Position position = Assert.Single(sim.Positions);
        Assert.Equal(0.05m, position.RealizedPnl.Amount);
        Assert.Equal(1m + 0.05m, sim.Balance(Currencies.BTC));
    }

    [Fact]
    public void An_unrealised_gain_is_marked_in_the_base_currency_while_the_position_is_open()
    {
        using SimHarness sim = Harness();
        sim.Quote(1000, 50_000.0m, 50_000.0m, size: 1_000_000m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(Contracts))))
            .Quote(2000, 62_500.0m, 62_500.0m, size: 1_000_000m)
            .Run();

        Position open = Assert.Single(sim.Positions);
        Money unrealized = open.UnrealizedPnl(sim.Px(62_500.0m));
        Assert.Equal(Currencies.BTC, unrealized.Currency);
        Assert.Equal(0.04m, unrealized.Amount);
    }

    [Fact]
    public void A_partial_close_realises_only_the_part_that_closed()
    {
        using SimHarness sim = Harness();
        sim.Quote(1000, 50_000.0m, 50_000.0m, size: 1_000_000m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(Contracts))))
            .Quote(2000, 62_500.0m, 62_500.0m, size: 1_000_000m)
            .At(2500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Sell, sim.Qty(Contracts / 2m))))
            .Quote(3000, 62_500.0m, 62_500.0m, size: 1_000_000m)
            .Run();

        // Half of 0.04 realised, the rest still open and marked at the same price.
        Position position = Assert.Single(sim.Positions);
        Assert.Equal(0.02m, position.RealizedPnl.Amount);
        Assert.Equal(0.02m, position.UnrealizedPnl(sim.Px(62_500.0m)).Amount);
        Assert.Equal(Contracts / 2m, position.Quantity.Value);
    }

    [Fact]
    public void The_margin_the_venue_reports_for_an_inverse_position_is_in_the_base_currency()
    {
        using SimHarness sim = Harness();
        sim.Quote(1000, 50_000.0m, 50_000.0m, size: 1_000_000m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(Contracts))))
            .Quote(2000, 50_000.0m, 50_000.0m, size: 1_000_000m)
            .Run();

        MarginBalance margin = Assert.Single(sim.Events.OfType<AccountState>().Last().Margins);
        Assert.Equal(Currencies.BTC, margin.Initial.Currency);

        // 0.2 BTC of notional: a twentieth posted, a fortieth kept.
        Assert.Equal(0.2m * 0.05m, margin.Initial.Amount);
        Assert.Equal(0.2m * TestInstruments.InversePerp().MarginMaint, margin.Maintenance.Amount);
    }

    [Fact]
    public void A_fee_on_an_inverse_fill_is_charged_in_the_base_currency()
    {
        using SimHarness sim = SimHarness.For(
            TestInstruments.InversePerp(),
            Inverse() with { FeeModel = new MakerTakerFeeModel() });
        sim.Quote(1000, 50_000.0m, 50_000.0m, size: 1_000_000m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(Contracts))))
            .Quote(2000, 50_000.0m, 50_000.0m, size: 1_000_000m)
            .Run();

        OrderFilled fill = Assert.Single(sim.Events.OfType<OrderFilled>());
        Assert.Equal(Currencies.BTC, fill.Commission.Currency);

        // Taker on 0.2 BTC of notional. The rate is the instrument's own, so the test cannot drift from it.
        Assert.Equal(0.2m * TestInstruments.InversePerp().TakerFee, fill.Commission.Amount);
    }

    [Fact]
    public void An_inverse_position_that_the_account_can_no_longer_carry_is_liquidated()
    {
        // The end of the chain: if any of the inverted arithmetic above is wrong, the venue either never steps in or
        // steps in at the wrong price. 0.05 BTC of balance against 0.2 BTC of notional at 20x - 0.01 posted, 0.005
        // kept - and the price falling takes the account under.
        using SimHarness sim = SimHarness.For(
            TestInstruments.InversePerp(),
            Inverse(liquidate: true) with { StartingBalances = [new Money(0.012m, Currencies.BTC)] });
        sim.Quote(1000, 50_000.0m, 50_000.0m, size: 1_000_000m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(Contracts))))
            .Quote(2000, 30_000.0m, 30_000.0m, size: 1_000_000m)
            .Quote(3000, 30_000.0m, 30_000.0m, size: 1_000_000m)
            .Run();

        Liquidation liquidation = Assert.Single(sim.Exchange.Liquidations);
        Assert.Equal(Currencies.BTC, liquidation.Equity.Currency);
        Assert.Equal(0m, sim.Exchange.NetPosition(sim.Id));
    }
}
