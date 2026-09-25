using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Identifiers;

namespace Bytex.Live.Tests;

// Why: a venue family declares what it does - carries market data, executes, allows an order to be amended - and a
// host reads that to stop offering somebody something the venue will not do. That guard is in the host, and a host is
// not the only way a node is built.
//
// A node assembled by hand, from a configuration file, or by a script reaches the same engine through the same
// factories having read nothing. It starts, it connects, and then it does nothing, for a reason that appears in no
// report. A gate in one client is a gate around one door, and this is the door nobody was watching.
//
// So the engine asks the same question where every node passes, whatever assembled it. Not instead of the host's
// check - the host's is earlier and can say more, in front of somebody while they are still building - but so that
// the answer cannot be walked around.
public sealed class CapabilityGuardTests
{
    /// <summary>A venue with one family that reads the market and cannot trade it.</summary>
    private static VenueDescriptor ReadOnly() => new()
    {
        Venue = new Venue("READONLY"),
        DisplayName = "Read Only",
        BrokerTag = BrokerTag.None,
        BrokerProgramme = BrokerProgramme.None,
        Families =
        [
            new VenueFamily
            {
                Name = "spot",
                InstrumentClasses = [InstrumentClass.Spot],
                PaysFunding = false,
                HttpBase = "https://example.invalid",
                WsBase = "wss://example.invalid",
                Key = new VenueKey { Parts = [] },
                Config = new Dictionary<string, string>(StringComparer.Ordinal),
                IgnoredConfig = [],
                DefaultFees = new VenueFees(0.001m, 0.002m),
                FreeDatasets = [],
                Capabilities = new VenueCapabilities
                {
                    LoadOneInstrument = true,
                    ListInstruments = true,
                    BarHistory = true,
                    FundingHistory = false,
                    MarketData = true,
                    Execution = false,
                    AmendOrders = false,
                },
            },
        ],
    };

    /// <summary>Two families on one host, told apart only by a value in the configuration.</summary>
    private static VenueDescriptor TwoFamilies() => new()
    {
        Venue = new Venue("TWO"),
        DisplayName = "Two",
        BrokerTag = BrokerTag.None,
        BrokerProgramme = BrokerProgramme.None,
        Families =
        [
            Family("spot", "Spot", execution: true, amend: true),
            Family("futures", "Futures", execution: true, amend: false),
        ],
    };

    private static VenueFamily Family(string name, string productType, bool execution, bool amend) => new()
    {
        Name = name,
        InstrumentClasses = name == "spot" ? [InstrumentClass.Spot] : [InstrumentClass.Swap],
        PaysFunding = name != "spot",
        HttpBase = "https://example.invalid",
        WsBase = "wss://example.invalid",
        Key = new VenueKey { Parts = [] },

        // The name AND the value, which is the whole point on a venue with one unified API: nothing but the value in
        // the request distinguishes the two markets.
        Config = new Dictionary<string, string>(StringComparer.Ordinal) { ["ProductType"] = productType },
        IgnoredConfig = [],
        DefaultFees = new VenueFees(0.001m, 0.002m),
        FreeDatasets = [],
        Capabilities = new VenueCapabilities
        {
            LoadOneInstrument = true,
            ListInstruments = true,
            BarHistory = true,
            FundingHistory = name != "spot",
            MarketData = true,
            Execution = execution,
            AmendOrders = amend,
        },
    };

    private sealed record Configured(string ProductType);

    [Fact]
    public void A_client_built_to_execute_on_a_family_that_cannot_is_refused()
    {
        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(
            () => CapabilityGuard.EnsureDeclared(ReadOnly(), new Configured("Spot"), c => c.Execution, "execution", "READONLY-1"));

        // The venue, the family, the capability and the client, because "capability missing" is not actionable.
        Assert.Contains("READONLY", refused.Message, StringComparison.Ordinal);
        Assert.Contains("spot", refused.Message, StringComparison.Ordinal);
        Assert.Contains("execution", refused.Message, StringComparison.Ordinal);
        Assert.Contains("READONLY-1", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_client_built_for_something_the_family_declares_is_allowed()
    {
        CapabilityGuard.EnsureDeclared(ReadOnly(), new Configured("Spot"), c => c.MarketData, "market data", "READONLY-1");
    }

    [Fact]
    public void The_family_is_the_one_the_configuration_selects_and_not_the_first_declared()
    {
        // The case a venue with one unified API creates: both families answer on the same host, so only the declared
        // configuration value says which one a client is. Taking the first would check the wrong family's
        // capabilities and pass a node that cannot do what it was built for.
        VenueDescriptor venue = TwoFamilies();

        Assert.Equal("spot", CapabilityGuard.FamilyOf(venue, new Configured("Spot"))!.Name);
        Assert.Equal("futures", CapabilityGuard.FamilyOf(venue, new Configured("Futures"))!.Name);
    }

    [Fact]
    public void A_venue_with_one_family_needs_no_configuration_to_select_it()
    {
        // Nothing selects the only family there is, so a family declaring no selector is not a gap.
        Assert.Equal("spot", CapabilityGuard.FamilyOf(ReadOnly(), new Configured("anything"))!.Name);
    }

    [Fact]
    public void A_configuration_matching_no_family_is_not_guessed_at()
    {
        // Null rather than the first family. A venue whose families cannot be told apart by configuration is a
        // declaration problem with its own tests, and refusing every node on such a venue would turn one venue's
        // under-declaration into an outage.
        Assert.Null(CapabilityGuard.FamilyOf(TwoFamilies(), new Configured("Options")));
    }

    [Fact]
    public void A_family_that_cannot_be_identified_refuses_nothing()
    {
        CapabilityGuard.EnsureDeclared(TwoFamilies(), new Configured("Options"), c => c.Execution, "execution", "TWO-1");
    }

    [Fact]
    public void Amending_is_asked_about_per_family_and_not_per_venue()
    {
        // The reason this is keyed per family at all: one venue, two answers. The spot market of this venue allows an
        // order to be changed and its futures market does not, so a question asked of the venue has no right answer.
        VenueDescriptor venue = TwoFamilies();

        Assert.True(CapabilityGuard.FamilyOf(venue, new Configured("Spot"))!.Capabilities.AmendOrders);
        Assert.False(CapabilityGuard.FamilyOf(venue, new Configured("Futures"))!.Capabilities.AmendOrders);
    }
}
