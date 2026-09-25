using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;

namespace Bytex.Backtest.Tests;

// Why: R8.17. Futures and options venues bill per contract, not per notional: a contract costs what it costs to
// trade whether it is worth a hundred dollars or a hundred thousand. Charging a percentage of notional against such a
// venue is wrong in both directions - far too much on a large contract, far too little on a small one - and a
// strategy that trades many cheap contracts pays most of its edge in fees that a percentage model never shows.
public sealed class PerContractFeeTests
{
    private static readonly Money Taker = new(0.85m, Currencies.USDT);
    private static readonly Money Maker = new(0.25m, Currencies.USDT);

    private static SimOptions WithFees(FeeModel fees) => new()
    {
        AccountType = AccountType.Margin,
        StartingBalances = [new Money(1_000_000m, Currencies.USDT)],
        FeeModel = fees,
    };

    [Fact]
    public void The_bill_follows_the_number_of_contracts()
    {
        using SimHarness sim = SimHarness.For(TestInstruments.Perp(), WithFees(new PerContractFeeModel(Taker)));
        sim.Quote(1000, 50_000.0m, 50_000.0m, size: 100m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(4m))))
            .Quote(2000, 50_000.0m, 50_000.0m, size: 100m)
            .Run();

        Assert.Equal(new Money(3.40m, Currencies.USDT), sim.Events.OfType<OrderFilled>().Single().Commission);
    }

    [Fact]
    public void What_a_contract_is_worth_does_not_change_what_it_costs_to_trade()
    {
        // The same four contracts at a tenth of the price: a percentage model would charge a tenth, this charges the
        // same 3.40, which is what the venue's schedule says.
        using SimHarness sim = SimHarness.For(TestInstruments.Perp(), WithFees(new PerContractFeeModel(Taker)));
        sim.Quote(1000, 5_000.0m, 5_000.0m, size: 100m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(4m))))
            .Quote(2000, 5_000.0m, 5_000.0m, size: 100m)
            .Run();

        Assert.Equal(new Money(3.40m, Currencies.USDT), sim.Events.OfType<OrderFilled>().Single().Commission);
    }

    [Fact]
    public void A_maker_pays_its_own_fee_where_the_venue_has_one()
    {
        using SimHarness sim = SimHarness.For(TestInstruments.Perp(), WithFees(new PerContractFeeModel(Taker, Maker)));
        sim.Quote(1000, 50_000.0m, 50_100.0m, size: 100m)
            .At(1500, s => s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(2m), sim.Px(49_900.0m))))
            .Quote(2000, 49_800.0m, 49_850.0m, size: 100m)
            .Run();

        OrderFilled fill = sim.Events.OfType<OrderFilled>().Single();
        Assert.Equal(LiquiditySide.Maker, fill.LiquiditySide);
        Assert.Equal(new Money(0.50m, Currencies.USDT), fill.Commission);
    }

    [Fact]
    public void One_fee_covers_both_sides_when_the_venue_has_one_rate()
    {
        using SimHarness sim = SimHarness.For(TestInstruments.Perp(), WithFees(new PerContractFeeModel(Taker)));
        sim.Quote(1000, 50_000.0m, 50_100.0m, size: 100m)
            .At(1500, s => s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(2m), sim.Px(49_900.0m))))
            .Quote(2000, 49_800.0m, 49_850.0m, size: 100m)
            .Run();

        Assert.Equal(new Money(1.70m, Currencies.USDT), sim.Events.OfType<OrderFilled>().Single().Commission);
    }

    [Fact]
    public void Each_piece_of_an_order_pays_for_the_contracts_it_took()
    {
        // With fills bounded by the size on offer, one order can pay in several bills: each covers what that fill
        // took, and together they come to what the whole order was charged.
        using SimHarness sim = SimHarness.For(TestInstruments.Perp(), WithFees(new PerContractFeeModel(Taker)));
        sim.Quote(1000, 50_000.0m, 50_000.0m, size: 2m)
            .At(1500, s => s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(6m), sim.Px(50_000.0m))))
            .Quote(2000, 50_000.0m, 50_000.0m, size: 2m)
            .Quote(3000, 50_000.0m, 50_000.0m, size: 2m)
            .Quote(4000, 50_000.0m, 50_000.0m, size: 2m)
            .Run();

        IReadOnlyList<OrderFilled> fills = sim.Events.OfType<OrderFilled>().ToList();
        Assert.Equal(3, fills.Count);
        Assert.All(fills, f => Assert.Equal(new Money(1.70m, Currencies.USDT), f.Commission));
        Assert.Equal(5.10m, fills.Sum(f => f.Commission.Amount));
    }

    [Fact]
    public void A_fee_cannot_be_charged_in_two_currencies()
    {
        Assert.Throws<ArgumentException>(() => new PerContractFeeModel(Taker, new Money(0.25m, Currencies.BTC)));
    }
}
