using Bytex.Adapters.Tests.Support;
using Bytex.Core.Adapters;
using Bytex.Core.Model;

namespace Bytex.Adapters.Tests;

// Why: `VenueDescriptor.FamilyFor` answers "which family of this venue holds this instrument class" with the FIRST
// family claiming it. That was unambiguous while a class belonged to one family. It no longer is - three venues now
// have two families claiming the same class, and they are genuinely different markets:
//
//   Binance   usdm-futures and coinm-futures   both Swap and Future, different hosts, different collateral
//   Bybit     linear and inverse               both Swap and Future, different settlement
//   Bitget    usdt-futures and usdc-futures    both Swap, different margin coin
//
// So anything keyed on venue-and-class silently gets whichever was declared first. Today every one of those pairs
// declares IDENTICAL capabilities, which means every such answer is right - by luck rather than by construction. The
// day COIN-M differs from USD-M in one flag, a caller asking "can this venue's Swap family amend an order" starts
// answering confidently for the wrong market, and nothing anywhere says so.
//
// This is what turns that luck into construction. It does not forbid the collision - two families genuinely holding
// one class is a fact about those venues, and forbidding it would mean leaving a real market undeclared. It requires
// that while they collide they must agree, so a caller that cannot tell them apart cannot be misled. A venue that
// needs them to differ has to be told apart by name or configuration first, and this test failing is how whoever
// makes them differ finds that out.
public sealed class ClassCollisionTests
{
    private static IEnumerable<(VenueDescriptor Venue, InstrumentClass Class, VenueFamily[] Families)> Collisions()
    {
        foreach (VenueDescriptor venue in Repo.ShippedVenues().Select(Repo.Describe).OfType<VenueDescriptor>())
        {
            foreach (InstrumentClass held in venue.Families.SelectMany(f => f.InstrumentClasses).Distinct())
            {
                VenueFamily[] claiming = [.. venue.Families.Where(f => f.InstrumentClasses.Contains(held))];
                if (claiming.Length > 1)
                {
                    yield return (venue, held, claiming);
                }
            }
        }
    }

    [Fact]
    public void The_collision_this_guards_actually_exists()
    {
        // Without this the assertions below would pass on a repository where no two families ever collide, which is
        // exactly the state this file was written because we had left.
        Assert.NotEmpty(Collisions());
    }

    /// <summary>
    /// A HOST DEPENDS ON THIS ONE, so read the consequence before relaxing it.
    ///
    /// <para>
    /// Told to us on 2026-09-26 by the host that ships these venues: it asks the FIRST family holding a class for
    /// every capability it reads - whether the venue can be papered, traded live, whether it amends orders, whether it
    /// serves bar history - and it does so deliberately, because this assertion makes the first answer the right
    /// answer. Splitting those reads per family would invent a distinction this test forbids.
    /// </para>
    ///
    /// <para>
    /// So weakening this does not produce a failing test somewhere else. It produces a product that quietly reports
    /// one family's capabilities for another market: an amend offered where the venue refuses it, or Live gated shut
    /// on a family that supports it. If a venue ever genuinely needs its colliding families to differ, the host has to
    /// be told before the change lands, not after - it can key those reads on the family it selected, but only once it
    /// knows it must.
    /// </para>
    /// </summary>
    [Fact]
    public void Two_families_holding_one_class_agree_about_what_they_can_do()
    {
        foreach ((VenueDescriptor venue, InstrumentClass held, VenueFamily[] claiming) in Collisions())
        {
            VenueCapabilities first = claiming[0].Capabilities;

            foreach (VenueFamily other in claiming.Skip(1))
            {
                Assert.True(
                    first == other.Capabilities,
                    $"{venue.Venue}'s '{claiming[0].Name}' and '{other.Name}' families both hold {held} and declare "
                    + "different capabilities. Anything asking this venue which family holds that class gets the first "
                    + "one declared and cannot tell it chose - so one of those two answers is now confidently wrong. "
                    + "Either make them agree, or give callers a way to name the family they mean before they differ.");
            }
        }
    }

    [Fact]
    public void Two_families_holding_one_class_can_be_told_apart_by_their_declared_configuration()
    {
        // The way out of the rule above. Families that collide are distinguishable by what selects them, so a caller
        // that knows which market it means can always say so - it is only the venue-and-class question that is
        // ambiguous, and that question has an answer precisely while they agree.
        foreach ((VenueDescriptor venue, InstrumentClass held, VenueFamily[] claiming) in Collisions())
        {
            string[] selectors = [.. claiming.Select(f => string.Join(
                ";",
                f.Config.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $"{c.Key}={c.Value}")))];

            Assert.Equal(
                selectors.Length,
                selectors.Distinct(StringComparer.Ordinal).Count());

            Assert.DoesNotContain(
                string.Empty,
                selectors);
        }
    }
}
