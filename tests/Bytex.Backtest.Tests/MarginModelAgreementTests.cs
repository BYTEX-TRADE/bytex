using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Accounts;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Primitives;

namespace Bytex.Backtest.Tests;

// Why (R4.12): a margin model is only worth having if EVERYTHING in a run uses the same one. Two sides work margin out
// - the risk engine, before an order goes anywhere, and the venue, which holds the money and liquidates when it is not
// there - and a run that sized positions by one rule and liquidated by another would measure something that never
// existed.
//
// So the venue's model is what the account is judged against, and the way it gets there is the way leverage already
// does: the venue teaches the engine its own terms rather than the same thing being configured twice.
public sealed class MarginModelAgreementTests
{
    /// <summary>This instrument's own requirement as the first tier, and more above it - the shape a venue publishes.</summary>
    private static TieredMarginModel Tiers() => new(
    [
        new MarginTier(0m, 0.05m, 0.025m),
        new MarginTier(100_000m, 0.25m, 0.125m),
    ]);

    private static SimOptions Options(IMarginModel? model, bool bypassRisk = false, decimal balance = 20_000m) => new()
    {
        AccountType = AccountType.Margin,
        StartingBalances = [new Money(balance, Currencies.USDT)],
        DefaultLeverage = 20m,
        MarginModel = model,
        BypassRisk = bypassRisk,
    };

    /// <summary>
    /// Three BTC at 49 000 is 147 000 of notional: the second tier, at a quarter rather than a twentieth. Below the
    /// market on purpose, so the order RESTS and what is being measured is the margin it holds rather than a fill.
    /// </summary>
    private static void SubmitThree(SimHarness sim) => sim
        .Quote(1000, 50_000.0m, 50_000.0m, size: 100m)
        .At(1500, s => s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(3m), sim.Px(49_000.0m))))
        .Quote(2000, 50_000.0m, 50_000.0m, size: 100m)
        .Run();

    [Fact]
    public void The_flat_rate_lets_an_order_through_that_a_tier_would_not()
    {
        // 147 000 at a twentieth is 7 350, which a 20 000 balance covers easily.
        using SimHarness sim = SimHarness.Perp(Options(null));

        SubmitThree(sim);

        Assert.Equal(1, sim.Exchange.OpenOrderCount);
        Assert.Empty(sim.Events.OfType<OrderDenied>());
    }

    [Fact]
    public void The_order_policy_judges_the_order_by_the_venues_own_model()
    {
        // The same order under the tiered model needs 36 750 - a quarter of 147 000 - and the balance is 20 000. The
        // account only knows that because the venue taught it, which is what this is really testing.
        using SimHarness sim = SimHarness.Perp(Options(Tiers()));

        SubmitThree(sim);

        Assert.Equal(0, sim.Exchange.OpenOrderCount);
        OrderDenied denied = Assert.Single(sim.Events.OfType<OrderDenied>());
        Assert.Contains("MARGIN", denied.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_venue_refuses_it_itself_with_the_order_policy_turned_off()
    {
        // A venue that only held together with the risk engine was not holding anything, and it is the venue that has
        // the money.
        using SimHarness sim = SimHarness.Perp(Options(Tiers(), bypassRisk: true));

        SubmitThree(sim);

        Assert.Equal(0, sim.Exchange.OpenOrderCount);
        OrderRejected rejected = Assert.Single(sim.Events.OfType<OrderRejected>());
        Assert.Contains("MARGIN", rejected.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void What_a_held_position_costs_is_reported_at_the_tier_it_is_in()
    {
        // Three BTC is 150 000 of notional, in the SECOND tier: a quarter, 37 500, where the flat rate would say 7 500.
        // The position has to be large enough to leave the first tier, or the two models agree and the test would pass
        // whichever of them was used. The account's margin balance is what a report and a monitor read, so the tier has
        // to reach it and not only the check that let the order through.
        using SimHarness sim = SimHarness.Perp(Options(Tiers(), balance: 100_000m));
        sim.Quote(1000, 50_000.0m, 50_000.0m, size: 100m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(3m))))
            .Quote(2000, 50_000.0m, 50_000.0m, size: 100m)
            .Run();

        MarginAccount account = Assert.IsType<MarginAccount>(sim.Engine.TradingRuntime.Cache.AccountForVenue(sim.Instrument.Venue));
        MarginBalance margin = Assert.Single(account.Margins.Values);

        Assert.Equal(new Money(37_500m, Currencies.USDT), margin.Initial);
        Assert.Equal(new Money(18_750m, Currencies.USDT), margin.Maintenance);
        Assert.Same(Tiers().GetType(), account.MarginModel.GetType());
    }

    [Fact]
    public void The_result_says_which_model_worked_the_leverage_out()
    {
        // With tiers, one leverage number per instrument is the BEST case - the first tier's - and a reader who cannot
        // see which model was used would take it for the whole truth.
        using SimHarness sim = SimHarness.Perp(Options(Tiers()));
        sim.Quote(1000, 50_000.0m, 50_000.0m, size: 100m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(2000, 50_000.0m, 50_000.0m, size: 100m)
            .Run();

        Bytex.Backtest.LeverageReportRow row = Assert.Single(sim.Engine.GetResult().Leverages);

        Assert.Equal("tiered", row.MarginModel);
        Assert.Equal(20m, row.Applied);   // the first tier's twentieth, which is also this instrument's own floor
        Assert.Equal(20m, row.Requested);
        Assert.False(row.Capped);
    }

    [Fact]
    public void A_flat_run_says_so_too()
    {
        using SimHarness sim = SimHarness.Perp(Options(null));
        sim.Quote(1000, 50_000.0m, 50_000.0m, size: 100m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(2000, 50_000.0m, 50_000.0m, size: 100m)
            .Run();

        Assert.Equal("rate", Assert.Single(sim.Engine.GetResult().Leverages).MarginModel);
    }

    [Fact]
    public void A_run_told_to_use_the_flat_rate_leaves_the_account_alone()
    {
        // Nothing is taught where nothing was asked for, so an account's model is the engine's own unless a venue said
        // otherwise - and a run's numbers do not change because a knob exists.
        using SimHarness sim = SimHarness.Perp(Options(null, balance: 100_000m));
        sim.Quote(1000, 50_000.0m, 50_000.0m, size: 100m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(3m))))
            .Quote(2000, 50_000.0m, 50_000.0m, size: 100m)
            .Run();

        MarginAccount account = Assert.IsType<MarginAccount>(sim.Engine.TradingRuntime.Cache.AccountForVenue(sim.Instrument.Venue));

        Assert.Same(RateMarginModel.Default, account.MarginModel);
        // The same position the tiered run margins at 37 500: one rate for any size is the whole difference.
        Assert.Equal(new Money(7_500m, Currencies.USDT), Assert.Single(account.Margins.Values).Initial);
    }
}
