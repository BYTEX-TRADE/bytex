using Bytex.Adapters.Bybit;
using Bytex.Adapters.Tests.Fixtures;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Adapters.Tests.Bybit;

// Why: this venue charges an option `min(feeRate x INDEX price of the underlying, 7% x premium) x size`. The engine
// prices a commission as a fraction of the TRADED notional, and an option's traded notional is its premium - a few
// hundred where the index is tens of thousands. So the venue's rate applied the engine's way charges against the
// wrong number entirely.
//
// The cap is the only term of that formula the engine can express exactly, because it IS a fraction of the premium.
// It is charged instead, and it is an UPPER bound: against the venue's own worked example the venue charges 2.52 and
// this charges 63. That is deliberate - an option backtest reads worse than reality rather than better, and a
// strategy discarded for looking unprofitable is a cheaper mistake than one traded because its costs were
// understated.
//
// What the venue charges and what this engine can charge are therefore different facts, and this file pins both
// separately: the declaration states the venue's published rates, the instrument carries the bound. A host asking
// the first must not be told the second.
public sealed class BybitOptionFeeTests
{
    // The venue's own published example, used so the arithmetic here can be checked against its documentation.
    // It is a MAKER fill - 0.02 % of a 42,000 index on 0.3 contracts is the 2.52 the venue prints.
    private const decimal ExampleIndex = 42_000m;
    private const decimal ExamplePremium = 3_000m;
    private const decimal ExampleSize = 0.3m;

    [Fact]
    public void The_declaration_states_what_the_venue_charges()
    {
        // Maker 0.02 % and taker 0.03 % - the venue's published schedule. The taker rate read 0.02 % until this was
        // checked against the venue's own fee page, which is a small error in the direction of understating a cost.
        VenueFamily option = new BybitPlugin().Describe().Families.Single(f => f.Name == "option");

        Assert.Equal(0.0002m, option.DefaultFees.Maker);
        Assert.Equal(0.0003m, option.DefaultFees.Taker);
    }

    [Fact]
    public void The_ceiling_is_the_share_of_the_premium_the_venue_publishes()
    {
        // Named rather than inline, and 7 % because that is what the venue's own fee page states.
        Assert.Equal(0.07m, BybitVenue.OptionFeeCapOfPremium);
    }

    [Fact]
    public async Task No_other_family_is_charged_at_a_ceiling()
    {
        // The divergence is specific to options, and only because their fee is charged against a price the engine
        // does not hold. A linear instrument is charged at the rate its own family declares.
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/v5/market/instruments-info", BybitPayloads.LinearInstrumentsPage1.Replace("cursor-page-2", string.Empty, StringComparison.Ordinal))
            .On("GET", BybitVenue.RiskLimitPath, BybitPayloads.LinearRiskLimits)
            .Handle);

        using BybitHttp http = new(new BybitDataClientConfig { ProductType = BybitProductType.Linear, BaseUrlHttp = server.HttpBase });
        BybitInstrumentProvider provider = new(http, BybitProductType.Linear);
        await provider.LoadAllAsync(CancellationToken.None);

        VenueFamily linear = new BybitPlugin().Describe().Families.Single(f => f.Name == "linear");
        Instrument perp = provider.GetAll().First();

        Assert.Equal(linear.DefaultFees.Maker, perp.MakerFee);
        Assert.Equal(linear.DefaultFees.Taker, perp.TakerFee);
        Assert.NotEqual(BybitVenue.OptionFeeCapOfPremium, perp.TakerFee);
    }

    [Fact]
    public void What_the_engine_charges_is_above_what_the_venue_would_charge()
    {
        // The property that matters, on the venue's own numbers. Being wrong is not the claim being made here -
        // being wrong in the expensive direction is.
        decimal venueCharges = Math.Min(BybitVenue.OptionMakerFee * ExampleIndex, BybitVenue.OptionFeeCapOfPremium * ExamplePremium) * ExampleSize;
        decimal engineCharges = BybitVenue.OptionFeeCapOfPremium * ExamplePremium * ExampleSize;

        Assert.Equal(2.52m, venueCharges);
        Assert.Equal(63m, engineCharges);
        Assert.True(engineCharges > venueCharges, "the bound has to be an upper one or it flatters a result");
    }

    [Fact]
    public void The_rate_the_venue_publishes_would_have_undercharged_by_the_same_order()
    {
        // Why the cap was taken rather than the published rate left in place. Both are wrong by a comparable factor;
        // only one of them tells somebody their strategy is cheaper to run than it is.
        decimal venueCharges = Math.Min(BybitVenue.OptionMakerFee * ExampleIndex, BybitVenue.OptionFeeCapOfPremium * ExamplePremium) * ExampleSize;
        decimal ratePremium = BybitVenue.OptionMakerFee * ExamplePremium * ExampleSize;

        Assert.True(ratePremium < venueCharges, "charging the rate against the premium undercharges");
        Assert.Equal(0.18m, ratePremium);
    }

    [Fact]
    public async Task A_loaded_option_carries_the_bound_and_not_the_venues_rate()
    {
        // Through the provider, so the instrument a run actually trades is the one asserted on.
        await using LoopbackServer server = new(new Routes()
            .On("GET", "/v5/market/instruments-info", BybitPayloads.OptionInstruments)
            .Handle);

        using BybitHttp http = new(new BybitDataClientConfig
        {
            ProductType = BybitProductType.Option,
            BaseUrlHttp = server.HttpBase,
        });

        BybitInstrumentProvider provider = new(http, BybitProductType.Option);
        await provider.LoadAllAsync(CancellationToken.None, new Dictionary<string, string>(StringComparer.Ordinal));

        OptionContract option = Assert.IsType<OptionContract>(provider.GetAll().First());

        Assert.Equal(BybitVenue.OptionFeeCapOfPremium, option.MakerFee);
        Assert.Equal(BybitVenue.OptionFeeCapOfPremium, option.TakerFee);

        // And the commission the engine will actually charge is the cap applied to the premium, which is the whole
        // point of expressing the bound this way rather than inventing a fee model.
        Money charged = option.CalculateCommission(
            option.MakeQuantity(1m),
            option.MakePrice(100m),
            LiquiditySide.Taker);

        Assert.Equal(7m, charged.Amount);
    }
}
