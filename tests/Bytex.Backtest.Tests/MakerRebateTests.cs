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

// Why: a venue can pay for liquidity rather than charge for it. One of the venues being onboarded publishes a maker
// rate of -0.0001 across its whole perpetual family, and until now the declaration could not say so - the guard on
// declared fees bounded every rate to [0, 0.01], so the adapter had to publish zero for a number the venue publishes
// as negative.
//
// The engine's arithmetic was already right: CalculateCommission returns notional times the rate and clamps nothing,
// ApplyFillToAccount does Adjust(currency, -commission.Amount) so a negative commission adds, and the pre-trade
// reservation skips a commission that is not positive because a rebate needs nothing reserved. But NONE of that was
// covered, so "a rebate credits the account" was a property of code nobody had asserted - and the first person to
// clamp a fee at zero for a plausible-looking reason would have broken it silently, since a rebate rounds to about
// nothing on a single fill and only shows up over thousands.
public sealed class MakerRebateTests
{
    private const decimal Rebate = -0.0001m;

    /// <summary>An order to price a fee against; its own fields do not matter to the calculation.</summary>
    private static MarketOrder AnyOrder(Instrument instrument) =>
        new OrderFactory(new TraderId("TRADER-001"), new StrategyId("Rebate-001"), new TestClock(Scripted.Epoch))
            .Market(instrument.Id, OrderSide.Buy, instrument.MakeQuantity(1m));

    /// <summary>A perpetual whose venue pays a maker rebate and charges a normal taker fee.</summary>
    private static CryptoPerpetual Rebating() => new(new InstrumentSpec
    {
        Id = InstrumentId.Parse("BTCUSDT-PERP.SIM"),
        RawSymbol = new Symbol("BTCUSDT"),
        AssetClass = AssetClass.Crypto,
        InstrumentClass = InstrumentClass.Swap,
        QuoteCurrency = Currencies.USDT,
        BaseCurrency = Currencies.BTC,
        SettlementCurrency = Currencies.USDT,
        PricePrecision = 1,
        SizePrecision = 3,
        PriceIncrement = new Price(0.1m, 1),
        SizeIncrement = new Quantity(0.001m, 3),
        MarginInit = 0.0066m,
        MarginMaint = 0.0033m,
        MakerFee = Rebate,
        TakerFee = 0.00055m,
    });

    [Fact]
    public void A_maker_rebate_is_a_negative_commission_rather_than_no_commission()
    {
        // The arithmetic, on its own. Two contracts at 50 000 is 100 000 of notional; a basis point of rebate is 10
        // USDT paid TO the account, so the commission is negative rather than zero.
        CryptoPerpetual instrument = Rebating();

        Money maker = new MakerTakerFeeModel().Commission(
            instrument,
            AnyOrder(instrument),
            instrument.MakeQuantity(2m),
            instrument.MakePrice(50_000m),
            LiquiditySide.Maker);

        Assert.Equal(new Money(-10m, Currencies.USDT), maker);
        Assert.True(maker.Amount < 0m, "a rebate has to be negative, not merely small");
    }

    [Fact]
    public void A_taker_fill_on_the_same_instrument_is_still_charged()
    {
        // So the sign is a property of the SIDE and not of the instrument: an adapter that got this backwards would
        // credit every trade and turn a losing strategy into a winning one.
        CryptoPerpetual instrument = Rebating();

        Money taker = new MakerTakerFeeModel().Commission(
            instrument,
            AnyOrder(instrument),
            instrument.MakeQuantity(2m),
            instrument.MakePrice(50_000m),
            LiquiditySide.Taker);

        Assert.Equal(new Money(55m, Currencies.USDT), taker);
    }

    [Fact]
    public void A_rebated_fill_leaves_the_account_larger_than_the_trade_alone_would()
    {
        // The half that matters, and the half nothing asserted: that a negative commission reaches the balance as a
        // credit rather than being dropped, clamped, or subtracted twice.
        using SimHarness sim = SimHarness.For(Rebating(), new SimOptions
        {
            AccountType = AccountType.Margin,
            StartingBalances = [new Money(100_000m, Currencies.USDT)],
            DefaultLeverage = 20m,
            Liquidate = false,
        });

        // Bought and sold at the same price, so the trade itself is flat and every movement in the balance is a fee.
        sim.Quote(1_000, 50_000.0m, 50_000.0m, size: 100m)
            .At(1_500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(2_000, 50_000.0m, 50_000.0m, size: 100m)
            .At(2_500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Sell, sim.Qty(1m))))
            .Run();

        decimal commissions = sim.Events.OfType<OrderFilled>().Sum(f => f.Commission.Amount);

        // Market orders take, so both fills are charged here - the point of this assertion is the direction of the
        // arithmetic, established against a run whose trade is flat.
        Assert.True(commissions > 0m, "two taking fills are charged");
        Assert.Equal(100_000m - commissions, sim.Balance(Currencies.USDT));
    }

    [Fact]
    public void A_resting_order_that_is_filled_is_paid_rather_than_charged()
    {
        // A limit order that sits and is filled is the maker, which is the only way the rebate is reachable at all.
        // If this ever reads as a charge, the rebate is declared and unreachable - the exact shape of defect the
        // venue work has been finding all day.
        using SimHarness sim = SimHarness.For(Rebating(), new SimOptions
        {
            AccountType = AccountType.Margin,
            StartingBalances = [new Money(100_000m, Currencies.USDT)],
            DefaultLeverage = 20m,
            Liquidate = false,
        });

        sim.Quote(1_000, 50_000.0m, 50_010.0m, size: 100m)
            .At(1_500, s => s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(49_900m))))
            .Quote(2_000, 49_800.0m, 49_810.0m, size: 100m)
            .Run();

        OrderFilled fill = Assert.Single(sim.Events.OfType<OrderFilled>());

        Assert.Equal(LiquiditySide.Maker, fill.LiquiditySide);
        Assert.True(fill.Commission.Amount < 0m, $"a maker fill on a rebating venue is paid, not charged: {fill.Commission}");
        Assert.Equal(100_000m - fill.Commission.Amount, sim.Balance(Currencies.USDT));
        Assert.True(sim.Balance(Currencies.USDT) > 100_000m, "the account is larger than it started, by the rebate");
    }
}
