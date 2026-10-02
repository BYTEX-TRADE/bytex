using Bytex.Adapters.Tests.Support;
using Bytex.Core.Adapters;
using Bytex.Core.Model;

namespace Bytex.Adapters.Tests;

// Why: these are assumptions a host made about the shape of this catalog, held honestly, true of the three venues that
// existed when they were formed, and false now. They were reported by the host that had them rather than found here,
// and each one had already been written into code that would have failed quietly on the venue that broke it.
//
// The value of asserting them on this side is that they are facts about what the ENGINE declares. A host cannot stop
// somebody here from shipping a catalog where they are true again - a release that happened to have spot on every
// venue would let a host reintroduce "every venue has spot" and pass its own tests. So the facts live where the
// declarations do, and a change that makes one of them false again fails here first.
//
// The third assumption of that set - that an instrument class identifies a family - is guarded by ClassCollisionTests
// and CollateralTests, which is where it belongs, because the answer to it is the collateral axis.
public sealed class CatalogShapeTests
{
    private static VenueDescriptor[] Venues() =>
        [.. Repo.ShippedVenues().Select(Repo.Describe).OfType<VenueDescriptor>()];

    [Fact]
    public void Not_every_venue_offers_spot()
    {
        // Hyperliquid's only market is perpetuals. A host routing "show me this venue's spot pairs" through an
        // assumption that one exists gets an empty answer at best and a null at worst, and the venue looks broken
        // rather than specialised.
        VenueDescriptor[] venues = Venues();
        VenueDescriptor[] withoutSpot = [.. venues.Where(v => !v.Families.Any(f => f.InstrumentClasses.Contains(InstrumentClass.Spot)))];

        Assert.NotEmpty(venues);
        Assert.NotEmpty(withoutSpot);
    }

    [Fact]
    public void A_derivative_is_not_always_a_perpetual()
    {
        // Dated futures on four venues and options on one. "Not spot" and "pays funding, never expires" are two
        // different facts, and a host treating the first as the second offers funding on an option and an expiry on
        // nothing.
        VenueDescriptor[] venues = Venues();
        InstrumentClass[] classes = [.. venues.SelectMany(v => v.Families).SelectMany(f => f.InstrumentClasses).Distinct()];

        Assert.Contains(InstrumentClass.Swap, classes);
        Assert.Contains(InstrumentClass.Future, classes);
        Assert.Contains(InstrumentClass.Option, classes);
    }

    [Fact]
    public void A_family_that_pays_no_funding_can_still_be_a_derivative()
    {
        // The same assumption from the other end, and the case that makes it concrete: an option is margined, expires
        // and is charged no funding. Anything deciding "derivative therefore funded" is wrong about it.
        VenueFamily[] unfunded = [.. Venues()
            .SelectMany(v => v.Families)
            .Where(f => !f.PaysFunding && f.InstrumentClasses.Any(c => c != InstrumentClass.Spot))];

        Assert.NotEmpty(unfunded);
    }
}
