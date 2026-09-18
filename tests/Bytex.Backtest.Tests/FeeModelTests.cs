using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Timing;
using Bytex.Core.Trading;

namespace Bytex.Backtest.Tests;

// Why: fees are the difference between a profitable and an unprofitable high-turnover strategy, so every model is
// pinned to hand-computed amounts, the right currency, and the right maker/taker rate.
public sealed class FeeModelTests
{
    private static MarketOrder AnyOrder(Instrument instrument) =>
        new OrderFactory(new TraderId("TRADER-001"), new StrategyId("Fees-001"), new TestClock(Scripted.Epoch))
            .Market(instrument.Id, OrderSide.Buy, instrument.MakeQuantity(1m));

    [Theory]
    // Spot instrument: maker 0.1 %, taker 0.2 %. Notional = quantity * price.
    [InlineData(2, 100.00, LiquiditySide.Maker, 0.2)] // 200 * 0.001
    [InlineData(2, 100.00, LiquiditySide.Taker, 0.4)] // 200 * 0.002
    [InlineData(0.5, 40000.00, LiquiditySide.Maker, 20)] // 20 000 * 0.001
    [InlineData(0.5, 40000.00, LiquiditySide.Taker, 40)]
    [InlineData(0.001, 0.01, LiquiditySide.Taker, 0.00000002)] // 0.00001 * 0.002, still representable at 8 decimals
    public void Maker_taker_model_charges_the_instrument_rate_for_the_liquidity_side_in_quote_currency(decimal quantity, decimal price, LiquiditySide side, decimal expected)
    {
        CurrencyPair spot = TestInstruments.Spot();

        Money fee = new MakerTakerFeeModel().Commission(spot, AnyOrder(spot), spot.MakeQuantity(quantity), spot.MakePrice(price), side);

        Assert.Equal(new Money(expected, Currencies.USDT), fee);
    }

    [Fact]
    public void Maker_taker_model_charges_inverse_contracts_in_the_base_currency()
    {
        // 10 000 USD contracts at 50 000 are worth 10 000 / 50 000 = 0.2 BTC; taker 0.05 % of that is 0.0001 BTC.
        CryptoPerpetual inverse = TestInstruments.InversePerp();

        Money fee = new MakerTakerFeeModel().Commission(inverse, AnyOrder(inverse), inverse.MakeQuantity(10_000m), inverse.MakePrice(50_000m), LiquiditySide.Taker);

        Assert.Equal(new Money(0.0001m, Currencies.BTC), fee);
    }

    [Theory]
    [InlineData(1, 100.00, LiquiditySide.Maker)]
    [InlineData(250, 43210.55, LiquiditySide.Taker)]
    public void Fixed_model_charges_the_same_amount_whatever_the_size_price_or_liquidity_side(decimal quantity, decimal price, LiquiditySide side)
    {
        CurrencyPair spot = TestInstruments.Spot();
        FixedFeeModel model = new(new Money(1.25m, Currencies.USD));

        Money fee = model.Commission(spot, AnyOrder(spot), spot.MakeQuantity(quantity), spot.MakePrice(price), side);

        Assert.Equal(new Money(1.25m, Currencies.USD), fee);
    }

    [Theory]
    // 0.05 % of notional, identical for makers and takers.
    [InlineData(2, 100.00, LiquiditySide.Maker, 0.1)]
    [InlineData(2, 100.00, LiquiditySide.Taker, 0.1)]
    [InlineData(3, 1234.56, LiquiditySide.Taker, 1.85184)] // 3703.68 * 0.0005
    public void Percent_model_charges_a_flat_share_of_notional(decimal quantity, decimal price, LiquiditySide side, decimal expected)
    {
        CurrencyPair spot = TestInstruments.Spot();

        Money fee = new PercentFeeModel(0.0005m).Commission(spot, AnyOrder(spot), spot.MakeQuantity(quantity), spot.MakePrice(price), side);

        Assert.Equal(new Money(expected, Currencies.USDT), fee);
    }

    [Fact]
    public void Venue_uses_maker_taker_fees_by_default_and_reports_them_on_each_fill()
    {
        using SimHarness sim = SimHarness.Spot();
        MarketOrder? taker = null;
        LimitOrder? maker = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s =>
            {
                taker = s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(2m)));
                maker = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(2m), sim.Px(99.00m)));
            })
            .Quote(2000, 98.80m, 98.90m)
            .Run();

        Assert.Equal(new Money(0.4004m, Currencies.USDT), sim.SingleFill(taker!).Commission); // 2 * 100.10 * 0.002
        Assert.Equal(new Money(0.198m, Currencies.USDT), sim.SingleFill(maker!).Commission); // 2 * 99.00 * 0.001
    }

    [Fact]
    public void Fee_charged_in_a_third_currency_is_debited_from_that_currency_only()
    {
        using SimHarness sim = SimHarness.Spot(new SimOptions
        {
            FeeModel = new FixedFeeModel(new Money(0.5m, Currencies.USDC)),
            StartingBalances = [new Money(1000m, Currencies.USDT), new Money(10m, Currencies.USDC)],
        });
        sim.Quote(1000, 100.00m, 100.00m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(2000, 100.00m, 100.00m)
            .Run();

        Assert.Equal(900m, sim.Balance(Currencies.USDT));
        Assert.Equal(9.5m, sim.Balance(Currencies.USDC));
        Assert.Equal(1m, sim.Balance(Currencies.BTC));
    }

    [Fact]
    public void Commissions_accumulate_on_the_order_and_on_the_position()
    {
        using SimHarness sim = SimHarness.Perp(new SimOptions { FeeModel = new FixedFeeModel(new Money(3m, Currencies.USDT)) });
        MarketOrder? open = null;
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1500, s => open = s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .At(1600, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Sell, sim.Qty(1m))))
            .Quote(2000, 50_000.0m, 50_000.0m)
            .Run();

        Assert.Equal(new Money(3m, Currencies.USDT), open!.Commissions[Currencies.USDT]);
        Assert.Equal(new Money(6m, Currencies.USDT), Assert.Single(sim.Positions).Commissions[Currencies.USDT]);
        Assert.Equal(new Money(-6m, Currencies.USDT), sim.Positions[0].RealizedPnl); // flat round trip, fees only
    }
}
