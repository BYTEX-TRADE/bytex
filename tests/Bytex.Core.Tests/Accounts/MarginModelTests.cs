using Bytex.Core.Model;
using Bytex.Core.Model.Accounts;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Tests.Support;

namespace Bytex.Core.Tests.Accounts;

// Why (R4.12): the engine's own rule is one rate for any size, and no large venue charges that. Every one of them
// charges by tiers - more margin for a bigger position - so a run using the flat rate UNDERSTATES what a big position
// costs, which is the dangerous direction: it sizes positions it could not have afforded and reports a return nobody
// could have had.
//
// What is pinned here is that the rule is replaceable, that the flat one still does exactly what it did (a change in
// these numbers would change every result ever compared against them), and that a tiered table refuses to be built
// wrong rather than quietly margining a position at the tier below the one it belongs to.
public class MarginModelTests
{
    private static readonly CryptoPerpetual _perp = TestInstruments.BtcPerp();

    private static MarginRequest Request(decimal quantity, decimal price, decimal leverage = 1m) =>
        new(_perp, _perp.MakeQuantity(quantity), _perp.MakePrice(price), leverage);

    // ----- the flat rate, unchanged -----

    [Fact]
    public void The_flat_model_is_the_notional_times_a_rate()
    {
        MarginRequest request = Request(1m, 50_000m);

        Assert.Equal("rate", RateMarginModel.Default.Name);
        Assert.Equal(
            new Money(50_000m * _perp.InitialMarginRate(1m), Currencies.USDT),
            RateMarginModel.Default.Initial(request));
        Assert.Equal(
            new Money(50_000m * _perp.MaintenanceMarginRate, Currencies.USDT),
            RateMarginModel.Default.Maintenance(request));
    }

    [Fact]
    public void An_account_uses_the_flat_model_until_it_is_given_another()
    {
        MarginAccount account = new(TestEvents.MarginState(TestIds.BinanceAccount, (Currencies.USDT, 100_000m, 0m)));

        Assert.Same(RateMarginModel.Default, account.MarginModel);
        Assert.Equal(
            RateMarginModel.Default.Initial(Request(1m, 50_000m)),
            account.CalculateInitialMargin(_perp, _perp.MakeQuantity(1m), _perp.MakePrice(50_000m)));
    }

    [Fact]
    public void A_venue_floor_wins_over_the_leverage_that_was_asked_for()
    {
        // The clamp is why a result reports the leverage APPLIED rather than the one requested: an instrument requiring
        // 1% of the notional cannot be traded at 200x however loudly a configuration asks.
        MarginRequest asked = Request(1m, 50_000m, leverage: 500m);

        Money margin = RateMarginModel.Default.Initial(asked);

        Assert.Equal(new Money(50_000m * _perp.MarginInit, Currencies.USDT), margin);
        Assert.True(margin.Amount > 50_000m / 500m, "the instrument's own requirement is the floor");
    }

    // ----- tiers -----

    private static TieredMarginModel Tiered() => new(
    [
        new MarginTier(0m, 0.05m, 0.025m),
        new MarginTier(100_000m, 0.10m, 0.05m),
        new MarginTier(500_000m, 0.20m, 0.10m),
    ]);

    /// <summary>Enough leverage that the flat rate lands on the instrument's own floor rather than above it.</summary>
    private const decimal Twenty = 20m;

    [Fact]
    public void A_bigger_position_is_margined_at_a_higher_tier()
    {
        // The whole point: one rate for any size is what no venue does.
        TieredMarginModel model = Tiered();

        Assert.Equal(0.05m, model.TierFor(50_000m).InitialRate);
        Assert.Equal(0.10m, model.TierFor(100_000m).InitialRate);
        Assert.Equal(0.10m, model.TierFor(499_999m).InitialRate);
        Assert.Equal(0.20m, model.TierFor(1_000_000m).InitialRate);
    }

    [Fact]
    public void The_whole_position_is_in_one_tier_rather_than_spread_across_them()
    {
        // Which is what a venue does, and the reason a position that crosses a boundary suddenly costs much more.
        TieredMarginModel model = Tiered();

        Money small = model.Initial(Request(1m, 50_000m, Twenty));
        Money large = model.Initial(Request(4m, 50_000m, Twenty));

        Assert.Equal(new Money(2_500m, Currencies.USDT), small);  // 50,000 at 5%
        Assert.Equal(new Money(20_000m, Currencies.USDT), large); // 200,000 at 10%, not 5% on the first 100,000
    }

    [Fact]
    public void Maintenance_follows_the_same_tier_as_the_initial_requirement()
    {
        TieredMarginModel model = Tiered();

        Assert.Equal(new Money(10_000m, Currencies.USDT), model.Maintenance(Request(4m, 50_000m, Twenty)));
    }

    [Fact]
    public void A_tier_is_a_floor_that_leverage_cannot_go_under()
    {
        // The same rule as the flat model's, so switching models does not quietly grant leverage a venue would refuse.
        TieredMarginModel model = Tiered();

        Assert.Equal(new Money(20_000m, Currencies.USDT), model.Initial(Request(4m, 50_000m, leverage: 100m)));
        Assert.Equal(new Money(100_000m, Currencies.USDT), model.Initial(Request(4m, 50_000m, leverage: 2m)));
    }

    [Fact]
    public void A_tiered_model_with_nothing_in_it_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new TieredMarginModel([]));
    }

    [Fact]
    public void A_table_that_does_not_start_at_nothing_is_refused()
    {
        // Otherwise every position below its first tier would be unmargined - free to open, and free to lose.
        ArgumentException e = Assert.Throws<ArgumentException>(() => new TieredMarginModel([new MarginTier(1_000m, 0.05m, 0.025m)]));

        Assert.Contains("start at nothing", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Tiers_out_of_order_are_refused()
    {
        // A table read in the order it was given would margin a position at whichever tier happened to be last.
        ArgumentException e = Assert.Throws<ArgumentException>(() => new TieredMarginModel(
        [
            new MarginTier(0m, 0.05m, 0.025m),
            new MarginTier(500_000m, 0.20m, 0.10m),
            new MarginTier(100_000m, 0.10m, 0.05m),
        ]));

        Assert.Contains("have to rise", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rate_that_is_not_a_fraction_of_a_notional_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new TieredMarginModel([new MarginTier(0m, 0m, 0m)]));
        Assert.Throws<ArgumentException>(() => new TieredMarginModel([new MarginTier(0m, 1.5m, 0.5m)]));
    }

    [Fact]
    public void A_tier_that_would_liquidate_what_it_lets_you_open_is_refused()
    {
        ArgumentException e = Assert.Throws<ArgumentException>(() => new TieredMarginModel([new MarginTier(0m, 0.05m, 0.10m)]));

        Assert.Contains("liquidated at once", e.Message, StringComparison.Ordinal);
    }

    // ----- what the risk engine judges an order against -----

    [Fact]
    public void An_account_given_a_tiered_model_asks_more_of_a_large_order()
    {
        MarginAccount account = new(TestEvents.MarginState(TestIds.BinanceAccount, (Currencies.USDT, 100_000m, 0m)))
        {
            MarginModel = Tiered(),
        };

        account.SetDefaultLeverage(Twenty);
        Money flat = RateMarginModel.Default.Initial(Request(4m, 50_000m, Twenty));
        Money tiered = account.CalculateInitialMargin(_perp, _perp.MakeQuantity(4m), _perp.MakePrice(50_000m));

        Assert.Equal(new Money(20_000m, Currencies.USDT), tiered);
        Assert.True(tiered.Amount > flat.Amount, $"the tier asks more than the flat rate ({tiered} against {flat})");
    }
}
