using Bytex.Adapters.Tests.Support;
using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Primitives;

namespace Bytex.Adapters.Tests;

// Why: three venues declare two families holding the same instrument class, and until this release the only thing
// separating them was a name the venue invented for its own enum. `ClassCollisionTests` proved the pairs agreed about
// their capabilities, so no answer was WRONG - and a host still reached only one of each pair, because it asks "which
// family holds a perpetual" and `FamilyFor` answers with the first. Three of twenty-one families were unreachable
// however the question was phrased.
//
// What separates them is collateral: Binance's coin-margined family settles in the coin and its USDⓈ-M one in the
// quote; Bybit's inverse family settles in the coin and its linear one in the quote; Bitget's two perpetual families
// are USDT- and USDC-margined and differ in nothing else at all. So the declaration states it, and these are the
// guards that keep the statement worth reading: that colliding families really are told apart by it, and that what a
// family says about itself is what its own instruments do.
public sealed class CollateralTests
{
    private static IEnumerable<(VenueDescriptor Venue, VenueFamily Family)> Families() =>
        Repo.ShippedVenues()
            .Select(Repo.Describe)
            .OfType<VenueDescriptor>()
            .SelectMany(v => v.Families.Select(f => (Venue: v, Family: f)));

    [Fact]
    public void Two_families_holding_one_class_are_told_apart_by_what_collateralises_them()
    {
        // The guard that makes the axis do its job. Two families a host cannot distinguish by class must differ here,
        // because this is the only fact it can put to a person in words they already understand.
        foreach (VenueDescriptor venue in Repo.ShippedVenues().Select(Repo.Describe).OfType<VenueDescriptor>())
        {
            foreach (InstrumentClass held in venue.Families.SelectMany(f => f.InstrumentClasses).Distinct())
            {
                VenueFamily[] claiming = [.. venue.Families.Where(f => f.InstrumentClasses.Contains(held))];
                if (claiming.Length <= 1)
                {
                    continue;
                }

                string[] collateral = [.. claiming.Select(f => f.Collateral.Kind + ":" + string.Join("+", f.Collateral.Currencies.Select(c => c.Code)))];

                Assert.Equal(collateral.Length, collateral.Distinct(StringComparer.Ordinal).Count());
            }
        }
    }

    [Fact]
    public void The_collision_this_guards_actually_exists()
    {
        // Without this the assertion above passes on a repository where no two families ever collide - which is the
        // vacuous pass this file exists to prevent, and one this suite has been caught by before.
        int colliding = Repo.ShippedVenues()
            .Select(Repo.Describe)
            .OfType<VenueDescriptor>()
            .SelectMany(v => v.Families
                .SelectMany(f => f.InstrumentClasses)
                .Distinct()
                .Select(c => v.Families.Count(f => f.InstrumentClasses.Contains(c))))
            .Count(count => count > 1);

        Assert.True(colliding > 0, "no class is claimed by two families, so the guard above proves nothing");
    }

    [Fact]
    public void Every_family_that_borrows_nothing_says_so_and_no_other_family_does()
    {
        // Spot is the only thing that posts no collateral, and it is the one case where zero margin is a fact rather
        // than a gap. A derivative family declaring None would be claiming its positions are unmargined.
        foreach ((VenueDescriptor venue, VenueFamily family) in Families())
        {
            bool spotOnly = family.InstrumentClasses.All(c => c == InstrumentClass.Spot);

            Assert.True(
                spotOnly == (family.Collateral.Kind == CollateralKind.None),
                $"{venue.Venue}'s '{family.Name}' holds {string.Join(", ", family.InstrumentClasses)} and declares "
                + $"collateral {family.Collateral.Kind}. Nothing is borrowed on spot and something is on every "
                + "derivative, so those two have to agree.");
        }
    }

    [Fact]
    public void A_fixed_set_names_its_currencies_and_nothing_else_carries_any()
    {
        // The invariant the type enforces on construction, asserted as behaviour so that relaxing the constructor
        // cannot pass unnoticed: a family cannot promise a fixed set and name none, nor name currencies while saying
        // the instrument decides.
        foreach ((_, VenueFamily family) in Families())
        {
            Assert.Equal(family.Collateral.Kind == CollateralKind.Currencies, family.Collateral.Currencies.Count > 0);
        }

        Assert.Throws<ArgumentException>(() => VenueCollateral.In());
    }

    [Fact]
    public void Both_shapes_of_collateral_are_in_use()
    {
        // Non-vacuity again, and it is what makes the pairs reachable: at least one family settles in the base
        // currency (an inverse market) and at least one names a fixed set, or the axis has only one value and
        // distinguishes nothing.
        CollateralKind[] kinds = [.. Families().Select(f => f.Family.Collateral.Kind).Distinct()];

        Assert.Contains(CollateralKind.BaseCurrency, kinds);
        Assert.Contains(CollateralKind.Currencies, kinds);
        Assert.Contains(CollateralKind.QuoteCurrency, kinds);
        Assert.Contains(CollateralKind.None, kinds);
    }

    [Fact]
    public void The_rule_for_reading_collateral_accepts_only_what_its_kind_means()
    {
        // Asserted directly because the venue tests exercise it only where it passes. Found by mutation: making the
        // base-currency case answer true for anything left every other test green, because a declaration that accepts
        // every settlement contradicts nothing - it just stops meaning anything.
        Assert.True(VenueCollateral.Base.Holds(Currencies.BTC, Currencies.BTC, Currencies.USDT));
        Assert.False(VenueCollateral.Base.Holds(Currencies.USDT, Currencies.BTC, Currencies.USDT));
        Assert.False(VenueCollateral.Base.Holds(Currencies.BTC, null, Currencies.USDT));

        Assert.True(VenueCollateral.Quote.Holds(Currencies.USDT, Currencies.BTC, Currencies.USDT));
        Assert.False(VenueCollateral.Quote.Holds(Currencies.BTC, Currencies.BTC, Currencies.USDT));

        Assert.True(VenueCollateral.In(Currencies.USDT).Holds(Currencies.USDT, Currencies.BTC, Currencies.USDT));
        Assert.False(VenueCollateral.In(Currencies.USDC).Holds(Currencies.USDT, Currencies.BTC, Currencies.USDT));

        // Spot has nothing to check, and saying so is not the same as accepting anything: the kind is what carries
        // that, and CollateralTests above forbids a derivative family from declaring it.
        Assert.True(VenueCollateral.None.Holds(Currencies.USDT, Currencies.BTC, Currencies.USDT));
    }

    [Fact]
    public void What_a_family_is_collateralised_in_answers_the_questions_a_host_asks_of_it()
    {
        // The three pairs, by name, because they are the ones a host could not reach and a reader of this file should
        // see the answers rather than derive them.
        Assert.Equal(CollateralKind.QuoteCurrency, Family("Binance", "usdm-futures").Collateral.Kind);
        Assert.Equal(CollateralKind.BaseCurrency, Family("Binance", "coinm-futures").Collateral.Kind);
        Assert.Equal(CollateralKind.QuoteCurrency, Family("Bybit", "linear").Collateral.Kind);
        Assert.Equal(CollateralKind.BaseCurrency, Family("Bybit", "inverse").Collateral.Kind);
        Assert.Equal([Currencies.USDT], Family("Bitget", "usdt-futures").Collateral.Currencies);
        Assert.Equal([Currencies.USDC], Family("Bitget", "usdc-futures").Collateral.Currencies);
    }

    private static VenueFamily Family(string venue, string family) =>
        Repo.Describe(venue)!.Families.Single(f => f.Name == family);
}
