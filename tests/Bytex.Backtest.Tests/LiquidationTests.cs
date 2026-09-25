using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Positions;
using Bytex.Core.Model.Primitives;

namespace Bytex.Backtest.Tests;

// Why: R8.16. A margin account that runs out of equity does not get to hold its position: the venue closes it, at the
// worst possible moment, and that is the difference between a backtest that shows a drawdown and one that shows an
// account that no longer exists. Until 0.5 the simulator let a position ride however far under water it went, so
// every leveraged backtest was a strategy nobody could have traded. What is pinned here is when the venue steps in,
// what it does when it does, and that it never does it to an account that owns what it bought.
public sealed class LiquidationTests
{
    private const decimal Leverage = 20m;

    /// <summary>
    /// 2 600 USDT at twenty times leverage. One contract at 50 000 costs 2 500 of initial margin to open - five
    /// percent of the notional, which is what the risk engine asks for before it lets the order go - and the venue
    /// keeps the instrument's 2.5% of notional, 1 250, as maintenance margin against it. So the account can just
    /// open it, and is 1 350 of adverse price away from not being able to hold it.
    /// </summary>
    private static SimOptions Thin(bool liquidate = true) => new()
    {
        AccountType = AccountType.Margin,
        StartingBalances = [new Money(2_600m, Currencies.USDT)],
        Leverages = new Dictionary<InstrumentId, decimal> { [TestInstruments.Perp().Id] = Leverage },
        FeeModel = new FixedFeeModel(Money.Zero(Currencies.USDT)),
        Liquidate = liquidate,
    };

    [Fact]
    public void A_position_the_account_can_no_longer_carry_is_closed_by_the_venue()
    {
        using SimHarness sim = SimHarness.For(TestInstruments.Perp(), Thin());
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(2000, 47_400.0m, 47_400.0m)
            .Quote(3000, 47_400.0m, 47_400.0m)
            .Run();

        Liquidation liquidation = Assert.Single(sim.Exchange.Liquidations);
        Assert.Equal(1m, liquidation.SignedQuantity);
        Assert.Equal(sim.Px(47_400.0m), liquidation.Price);
        Assert.True(liquidation.Equity.Amount < liquidation.MaintenanceMargin.Amount, "the venue closed a position the account could still carry");
        Assert.Equal(0m, sim.Exchange.NetPosition(sim.Id));
    }

    [Fact]
    public void The_closing_order_is_the_venues_own_and_says_so()
    {
        using SimHarness sim = SimHarness.For(TestInstruments.Perp(), Thin());
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(2000, 47_400.0m, 47_400.0m)
            .Quote(3000, 47_400.0m, 47_400.0m)
            .Run();

        Order closing = Assert.Single(sim.Engine.Cache.Orders(), o => o.Tags?.Contains(SimulatedExchange.LiquidationTag) == true);
        Assert.Equal(OrderSide.Sell, closing.Side);
        Assert.Equal(OrderType.Market, closing.Type);
        Assert.True(closing.IsReduceOnly, "a closing order that could open the other side is not a liquidation");
        Assert.Equal(OrderStatus.Filled, closing.Status);
        Assert.Equal(sim.Qty(1m), closing.FilledQuantity);
    }

    [Fact]
    public void A_short_is_liquidated_when_the_market_goes_up()
    {
        using SimHarness sim = SimHarness.For(TestInstruments.Perp(), Thin());
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Sell, sim.Qty(1m))))
            .Quote(2000, 52_600.0m, 52_600.0m)
            .Quote(3000, 52_600.0m, 52_600.0m)
            .Run();

        Assert.Equal(-1m, Assert.Single(sim.Exchange.Liquidations).SignedQuantity);
        Assert.Equal(0m, sim.Exchange.NetPosition(sim.Id));
        Assert.Equal(OrderSide.Buy, Assert.Single(sim.Engine.Cache.Orders(), o => o.Tags?.Contains(SimulatedExchange.LiquidationTag) == true).Side);
    }

    [Fact]
    public void The_accounts_own_working_orders_are_taken_off_the_book_first()
    {
        // A resting order of the account's own is money it has not got: the venue takes the book away before it closes
        // the position, so nothing of the account's can fill in the middle of its own liquidation.
        //
        // This one needs a balance the resting order fits in as well as the position: 2 500 of initial margin for the
        // contract, 2 000 for a resting buy of another at 40 000, so 4 600 leaves 100 free. Maintenance is 2.5% of the
        // notional at the price the venue is marking, which falls with it, so the two meet lower than a fixed 1 250
        // would suggest: equity is 4 600 - (50 000 - P) and maintenance is 0.025P, which cross at P = 46 564. A quote
        // at 46 500 is through it.
        SimOptions room = Thin() with { StartingBalances = [new Money(4_600m, Currencies.USDT)] };
        using SimHarness sim = SimHarness.For(TestInstruments.Perp(), room);
        LimitOrder? resting = null;
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1500, s =>
            {
                s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m)));
                resting = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(40_000.0m)));
            })
            .Quote(2000, 46_500.0m, 46_500.0m)
            .Quote(3000, 46_500.0m, 46_500.0m)
            .Run();

        Assert.Single(sim.Exchange.Liquidations);
        Assert.Equal(OrderStatus.Canceled, resting!.Status);
    }

    [Fact]
    public void A_position_the_account_can_still_carry_is_left_alone()
    {
        using SimHarness sim = SimHarness.For(TestInstruments.Perp(), Thin());
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(2000, 49_000.0m, 49_000.0m)
            .Quote(3000, 49_000.0m, 49_000.0m)
            .Run();

        // A thousand down on a 2 600 account with 62.50 to keep: still standing, and the venue has no business in it.
        Assert.Empty(sim.Exchange.Liquidations);
        Assert.Equal(1m, sim.Exchange.NetPosition(sim.Id));
    }

    [Fact]
    public void A_venue_that_was_told_not_to_liquidate_lets_the_position_ride()
    {
        // For studying what a strategy would have done without the venue in the way, and for reproducing a run made
        // before 0.5 - not the default, because it is not what happens to anybody's account.
        using SimHarness sim = SimHarness.For(TestInstruments.Perp(), Thin(liquidate: false));
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(2000, 40_000.0m, 40_000.0m)
            .Quote(3000, 40_000.0m, 40_000.0m)
            .Run();

        Assert.Empty(sim.Exchange.Liquidations);
        Assert.Equal(1m, sim.Exchange.NetPosition(sim.Id));
    }

    [Fact]
    public void A_cash_account_is_never_liquidated()
    {
        // It owns what it bought: there is no margin to fall short of, whatever the price does. The instrument carries
        // margin rates on purpose - a spot pair a venue also lends against - so that what refuses the liquidation is
        // the account being a cash one and not an instrument with nothing to require.
        CurrencyPair lendable = new(new InstrumentSpec
        {
            Id = new InstrumentId(new Symbol("BTCUSDT"), TestInstruments.Sim),
            AssetClass = AssetClass.Crypto,
            InstrumentClass = InstrumentClass.Spot,
            QuoteCurrency = Currencies.USDT,
            BaseCurrency = Currencies.BTC,
            PricePrecision = 2,
            SizePrecision = 3,
            PriceIncrement = new Price(0.01m, 2),
            SizeIncrement = new Quantity(0.001m, 3),
            MarginInit = 0.05m,
            MarginMaint = 0.025m,
        });
        using SimHarness sim = SimHarness.For(
            lendable,
            new SimOptions
            {
                AccountType = AccountType.Cash,
                StartingBalances = [new Money(150m, Currencies.USDT)],
                FeeModel = new FixedFeeModel(Money.Zero(Currencies.USDT)),
            });
        sim.Quote(1000, 100.00m, 100.00m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(2000, 1.00m, 1.00m)
            .Quote(3000, 1.00m, 1.00m)
            .Run();

        // Ninety-nine dollars under water on a fifty-dollar balance, and the coin is still the account's.
        Assert.Empty(sim.Exchange.Liquidations);
        Assert.Equal(1m, sim.Exchange.NetPosition(sim.Id));
    }

    [Fact]
    public void A_liquidated_account_is_not_liquidated_again()
    {
        using SimHarness sim = SimHarness.For(TestInstruments.Perp(), Thin());
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(2000, 47_400.0m, 47_400.0m)
            .Quote(3000, 47_300.0m, 47_300.0m)
            .Quote(4000, 47_200.0m, 47_200.0m)
            .Quote(5000, 47_100.0m, 47_100.0m)
            .Run();

        Assert.Single(sim.Exchange.Liquidations);
        Assert.Single(sim.Engine.Cache.Orders(), o => o.Tags?.Contains(SimulatedExchange.LiquidationTag) == true);
    }

    [Fact]
    public void The_position_the_engine_knows_about_is_closed_too()
    {
        // The venue's own books are not the engine's: a liquidation that closed a position at the venue and left the
        // strategy thinking it still held one would be the worst of both.
        using SimHarness sim = SimHarness.For(TestInstruments.Perp(), Thin());
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(2000, 47_400.0m, 47_400.0m)
            .Quote(3000, 47_400.0m, 47_400.0m)
            .Run();

        Position position = Assert.Single(sim.Positions);
        Assert.Equal(PositionSide.Flat, position.Side);
        Assert.True(position.IsClosed, "the engine still holds a position the venue has closed");
    }

    [Fact]
    public void A_result_says_what_the_simulator_it_came_out_of_models()
    {
        // So that a product built on this engine can tell its users what it can do without anybody having to read a
        // release note: the names are in the result, and a venue configured out of a thing does not claim it.
        using SimHarness modelled = SimHarness.For(TestInstruments.Perp(), Thin());
        modelled.Quote(1000, 50_000.0m, 50_000.0m).Run();

        Assert.Equal(
            new[] { SimulationCapabilities.BookDepth, SimulationCapabilities.Funding, SimulationCapabilities.Liquidation, SimulationCapabilities.PartialFills },
            modelled.Engine.GetResult().Simulation.OrderBy(c => c, StringComparer.Ordinal).ToArray());

        using SimHarness plain = SimHarness.For(
            TestInstruments.Perp(),
            Thin(liquidate: false) with { FillSizing = FillSizing.WholeFills });
        plain.Quote(1000, 50_000.0m, 50_000.0m).Run();

        Assert.Equal(new[] { SimulationCapabilities.Funding }, plain.Engine.GetResult().Simulation.ToArray());

        // A cash account holds no perpetual and cannot be liquidated, so it claims neither.
        using SimHarness cash = SimHarness.Spot();
        cash.Quote(1000, 100.00m, 100.00m).Run();

        Assert.Equal(
            new[] { SimulationCapabilities.BookDepth, SimulationCapabilities.PartialFills },
            cash.Engine.GetResult().Simulation.OrderBy(c => c, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void The_report_says_the_venue_closed_it()
    {
        using SimHarness sim = SimHarness.For(TestInstruments.Perp(), Thin());
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(2000, 47_400.0m, 47_400.0m)
            .Quote(3000, 47_400.0m, 47_400.0m)
            .Run();

        LiquidationReportRow row = Assert.Single(sim.Engine.GetResult().Liquidations);
        Assert.Equal(sim.Id, row.InstrumentId);
        Assert.Equal(1m, row.Quantity);
        Assert.Equal(47_400.0m, row.Price);
        Assert.Equal(Currencies.USDT, row.Currency);
        Assert.True(row.Equity < row.MaintenanceMargin);
    }
}
