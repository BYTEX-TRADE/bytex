using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Accounts;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Primitives;

namespace Bytex.Backtest.Tests;

// Why: a venue does not let one balance back several orders. The margin an order would have to post is held the
// moment the order is worked, not when it fills - without that an account could commit the same money as many times
// over as it could send orders, every order would pass the margin check, and leverage and liquidation were both out
// of reach of anything a user could do. What is pinned here is the hold: that it is taken, that it is the right size,
// that closing asks for nothing, and that it is given back.
public sealed class MarginHoldTests
{
    private static readonly InstrumentId Perp = TestInstruments.Perp().Id;

    /// <summary>
    /// A balance that can carry one 1 BTC order and not two. The hold is judged at the price the order names, so a
    /// limit at 49 000 holds 2 450 at twenty times leverage - a twentieth of the notional, which is also the least
    /// this instrument will take.
    /// </summary>
    private static SimOptions Tight(decimal leverage = 20m, bool bypassRisk = false, OmsType oms = OmsType.Netting) => new()
    {
        AccountType = AccountType.Margin,
        OmsType = oms,
        StartingBalances = [new Money(3_000m, Currencies.USDT)],
        DefaultLeverage = leverage,
        BypassRisk = bypassRisk,
    };

    [Fact]
    public void Two_orders_cannot_be_backed_by_the_same_balance()
    {
        using SimHarness sim = SimHarness.Perp(Tight());
        sim.Quote(1000, 50_000.0m, 50_000.0m, size: 100m)
            .At(1500, s =>
            {
                s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(49_000.0m)));
                s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(49_000.0m)));
            })
            .Quote(2000, 50_000.0m, 50_000.0m, size: 100m)
            .Run();

        // The first holds its 2 500 of a 3 000 balance; the second is refused because the money is spoken for, and
        // what refuses it says margin rather than leaving somebody to work it out.
        Assert.Equal(1, sim.Exchange.OpenOrderCount);
        string reason = sim.Events.OfType<OrderDenied>().Select(e => e.Reason)
            .Concat(sim.Events.OfType<OrderRejected>().Select(e => e.Reason))
            .Single();
        Assert.Contains("MARGIN", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_venue_refuses_it_itself_and_does_not_lean_on_the_risk_engine()
    {
        // The risk engine can be turned off, and a venue that only held together with it was not holding anything.
        using SimHarness sim = SimHarness.Perp(Tight(bypassRisk: true));
        sim.Quote(1000, 50_000.0m, 50_000.0m, size: 100m)
            .At(1500, s =>
            {
                s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(49_000.0m)));
                s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(49_000.0m)));
            })
            .Quote(2000, 50_000.0m, 50_000.0m, size: 100m)
            .Run();

        Assert.Equal(1, sim.Exchange.OpenOrderCount);
        OrderRejected rejected = Assert.Single(sim.Events.OfType<OrderRejected>());
        Assert.Contains("insufficient margin", rejected.Reason, StringComparison.Ordinal);
        Assert.Contains("20x leverage", rejected.Reason, StringComparison.Ordinal);
    }

    [Theory]
    // 1 BTC at 49 000 is 49 000 of notional. What is held is the larger of the leverage share and the instrument's
    // 5% floor: the whole notional at 1x, a tenth at 10x, a twentieth at 20x - and still a twentieth at 50x, because
    // leverage past the floor buys nothing. Setting leverage is what makes the hold smaller, and only down to there.
    [InlineData(1, 49_000)]
    [InlineData(10, 4_900)]
    [InlineData(20, 2_450)]
    [InlineData(50, 2_450)]
    public void What_is_held_is_the_leverage_share_of_the_notional_or_the_instruments_floor(decimal leverage, decimal held)
    {
        using SimHarness sim = SimHarness.Perp(Tight(leverage: leverage) with { StartingBalances = [new Money(60_000m, Currencies.USDT)] });
        sim.Quote(1000, 50_000.0m, 50_000.0m, size: 100m)
            .At(1500, s => s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(49_000.0m))))
            .Quote(2000, 50_000.0m, 50_000.0m, size: 100m)
            .Run();

        Assert.Equal(new Money(held, Currencies.USDT), Locked(sim));
    }

    [Fact]
    public void Closing_a_position_asks_for_nothing()
    {
        // A venue never refuses a close for want of margin: closing gives margin back rather than wanting more. An
        // account that could not close what it holds is an account that can only be liquidated.
        using SimHarness sim = SimHarness.Perp(Tight());
        sim.Quote(1000, 50_000.0m, 50_000.0m, size: 100m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(2000, 50_000.0m, 50_000.0m, size: 100m)
            .At(2500, s => s.Submit(s.Orders.Limit(sim.Id, OrderSide.Sell, sim.Qty(1m), sim.Px(51_000.0m))))
            .Quote(3000, 50_000.0m, 50_000.0m, size: 100m)
            .Run();

        Assert.Empty(sim.Events.OfType<OrderRejected>());
        Assert.Empty(sim.Events.OfType<OrderDenied>());
        Assert.Equal(1, sim.Exchange.OpenOrderCount);
    }

    [Fact]
    public void On_a_hedging_venue_an_order_against_the_position_still_has_to_be_paid_for()
    {
        // The same order that closes a position on a netting venue opens one of its own on a hedging venue, and that
        // one has to be paid for like any other.
        using SimHarness sim = SimHarness.Perp(Tight(bypassRisk: true, oms: OmsType.Hedging));
        sim.Quote(1000, 50_000.0m, 50_000.0m, size: 100m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(2000, 50_000.0m, 50_000.0m, size: 100m)
            .At(2500, s => s.Submit(s.Orders.Limit(sim.Id, OrderSide.Sell, sim.Qty(1m), sim.Px(51_000.0m))))
            .Quote(3000, 50_000.0m, 50_000.0m, size: 100m)
            .Run();

        OrderRejected rejected = Assert.Single(sim.Events.OfType<OrderRejected>());
        Assert.Contains("insufficient margin", rejected.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Cancelling_an_order_gives_its_margin_back()
    {
        using SimHarness sim = SimHarness.Perp(Tight());
        Core.Model.Orders.LimitOrder? first = null;
        sim.Quote(1000, 50_000.0m, 50_000.0m, size: 100m)
            .At(1500, s => first = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(49_000.0m))))
            .Quote(2000, 50_000.0m, 50_000.0m, size: 100m)
            .At(2500, s => s.Cancel(first!))
            .Quote(3000, 50_000.0m, 50_000.0m, size: 100m)
            .At(3500, s => s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(49_000.0m))))
            .Quote(4000, 50_000.0m, 50_000.0m, size: 100m)
            .Run();

        Assert.Empty(sim.Events.OfType<OrderRejected>());
        Assert.Empty(sim.Events.OfType<OrderDenied>());
        Assert.Equal(new Money(2_450m, Currencies.USDT), Locked(sim));
    }

    [Fact]
    public void A_cash_account_is_still_refused_in_the_words_it_always_was()
    {
        using SimHarness sim = SimHarness.Spot(new SimOptions
        {
            AccountType = AccountType.Cash,
            StartingBalances = [new Money(1_000m, Currencies.USDT)],
            BypassRisk = true,
        });
        sim.Quote(1000, 100.00m, 100.00m, size: 1_000m)
            .At(1500, s => s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(100m), sim.Px(100.00m))))
            .Quote(2000, 100.00m, 100.00m, size: 1_000m)
            .Run();

        Assert.Equal("insufficient balance", Assert.Single(sim.Events.OfType<OrderRejected>()).Reason);
    }

    [Fact]
    public void The_account_is_told_the_leverage_the_venue_grants()
    {
        // Nothing set it before this: every account in the cache stood at 1x however the venue was configured, so the
        // risk engine judged margin at 1x and anything sizing against the account could not know better.
        using SimHarness sim = SimHarness.Perp(new SimOptions
        {
            AccountType = AccountType.Margin,
            StartingBalances = [new Money(100_000m, Currencies.USDT)],
            DefaultLeverage = 10m,
            Leverages = new Dictionary<InstrumentId, decimal> { [Perp] = 20m },
        });
        sim.Quote(1000, 50_000.0m, 50_000.0m, size: 100m).Run();

        MarginAccount account = Assert.IsType<MarginAccount>(sim.Engine.Kernel.Cache.AccountForVenue(sim.Id.Venue));
        Assert.Equal(20m, account.Leverage(Perp));
        Assert.Equal(10m, account.Leverage(new InstrumentId(new Symbol("ETHUSDT-PERP"), sim.Id.Venue)));
    }

    private static Money Locked(SimHarness sim) =>
        sim.Engine.Kernel.Cache.AccountForVenue(sim.Id.Venue)!.BalanceLocked(Currencies.USDT)!.Value;
}
