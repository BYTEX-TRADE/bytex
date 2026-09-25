using System.Reflection;
using Bytex.Adapters.Tests.Support;
using Bytex.Core.Adapters;
using Bytex.Core.Model.Identifiers;

namespace Bytex.Adapters.Tests;

// Why: hosts were keeping hand-maintained tables of what each venue can do - one for "can it be traded", one for
// "can it be papered" - because the declaration said which markets a venue OFFERS and not which of them this adapter
// can actually work. Those are different facts: KuCoin's perpetuals were downloadable, backtestable and paperable for
// a day with no execution client written at all, and a single "tradable" flag would have been wrong about them in
// both directions.
//
// So an adapter declares what it can do, per family, and this file is what stops the declaration becoming a second
// place for the truth to be wrong. A declaration nobody checks is worse than no declaration: the table a host used to
// keep at least went stale in public.
//
// Each capability below is compared with the thing it describes - the provider's own methods, the presence of a
// client, the history helpers, and for AmendOrders the execution client's actual behaviour. A capability claimed and
// not implemented fails here, and so does one implemented and denied.
public sealed class VenueCapabilityTests
{
    private static IReadOnlyList<(string Venue, VenueDescriptor Descriptor)> Declared()
    {
        List<(string, VenueDescriptor)> found = [];
        foreach (string venue in Repo.ShippedVenues())
        {
            Type? plugin = Assembly.Load("Bytex.Adapters." + venue).GetTypes()
                .FirstOrDefault(t => !t.IsAbstract && typeof(IVenuePlugin).IsAssignableFrom(t));

            if (plugin is not null)
            {
                found.Add((venue, ((IVenuePlugin)Activator.CreateInstance(plugin)!).Describe()));
            }
        }

        return found;
    }

    public static TheoryData<string, string> Families()
    {
        TheoryData<string, string> data = new();
        foreach ((string venue, VenueDescriptor descriptor) in Declared())
        {
            foreach (VenueFamily family in descriptor.Families)
            {
                data.Add(venue, family.Name);
            }
        }

        return data;
    }

    private static VenueFamily Family(string venue, string name) =>
        Declared().Single(d => d.Venue == venue).Descriptor.Families.Single(f => f.Name == name);

    /// <summary>
    /// The types an adapter ships, by the naming every adapter follows. A family with its own clients - as KuCoin's
    /// futures market has - is looked for under that name first.
    /// </summary>
    private static Type? Type(string venue, string family, string suffix)
    {
        Assembly assembly = Assembly.Load("Bytex.Adapters." + venue);
        string capitalised = char.ToUpperInvariant(family[0]) + family[1..];
        return assembly.GetType($"Bytex.Adapters.{venue}.{venue}{capitalised}{suffix}", throwOnError: false)
            ?? assembly.GetType($"Bytex.Adapters.{venue}.{venue}{suffix}", throwOnError: false);
    }

    private static bool Implements(Type? type, string method) =>
        type is not null && type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Any(m => m.Name == method && m.DeclaringType == type);

    [Theory]
    [MemberData(nameof(Families))]
    public void A_family_that_claims_it_can_fetch_one_instrument_has_a_provider_that_does(string venue, string name)
    {
        VenueFamily family = Family(venue, name);
        Type? provider = Type(venue, name, "InstrumentProvider");

        Assert.Equal(family.Capabilities.LoadOneInstrument, Implements(provider, "LoadAsync"));
        Assert.Equal(family.Capabilities.ListInstruments, Implements(provider, "LoadAllAsync"));
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void A_family_that_claims_history_has_a_helper_that_needs_no_node(string venue, string name)
    {
        // The point of E7: a host that stores history has no node, so the helper has to be callable with an http
        // client and an instrument and nothing else.
        VenueFamily family = Family(venue, name);
        Type? history = Assembly.Load("Bytex.Adapters." + venue)
            .GetType($"Bytex.Adapters.{venue}.{venue}History", throwOnError: false);

        bool bars = history is not null && history.GetMethods(BindingFlags.Public | BindingFlags.Static).Any(m => m.Name == "FetchBarsAsync");
        bool funding = history is not null && history.GetMethods(BindingFlags.Public | BindingFlags.Static).Any(m => m.Name == "FetchFundingRatesAsync");

        if (family.Capabilities.BarHistory)
        {
            Assert.True(bars, $"{venue}.{name} claims bar history and {venue}History cannot fetch any");
        }

        if (family.Capabilities.FundingHistory)
        {
            Assert.True(funding, $"{venue}.{name} claims funding history and {venue}History cannot fetch any");
        }
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void A_family_that_claims_market_data_or_execution_has_a_client_for_it(string venue, string name)
    {
        VenueFamily family = Family(venue, name);

        Assert.Equal(family.Capabilities.MarketData, Type(venue, name, "DataClient") is not null);
        Assert.Equal(family.Capabilities.Execution, Type(venue, name, "ExecutionClient") is not null);
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void A_family_that_denies_amending_has_a_client_that_refuses_it(string venue, string name)
    {
        // The capability that has to be true about BEHAVIOUR rather than about a method existing, because every
        // execution client has a ModifyOrderAsync - the question is whether it does anything. A client that denies
        // amending must refuse, so a strategy is told rather than left with a protective order it believes was
        // resized; and a client that claims it must not refuse unconditionally.
        VenueFamily family = Family(venue, name);
        Type? client = Type(venue, name, "ExecutionClient");

        if (client is null)
        {
            Assert.False(family.Capabilities.Execution);
            return;
        }

        string source = File.ReadAllText(Repo.SourceFiles(venue).Single(f => Path.GetFileNameWithoutExtension(f) == client.Name));
        int modify = source.IndexOf("public override Task ModifyOrderAsync", StringComparison.Ordinal);
        if (modify < 0)
        {
            modify = source.IndexOf("public override async Task ModifyOrderAsync", StringComparison.Ordinal);
        }

        Assert.True(modify >= 0, $"{client.Name} does not implement ModifyOrderAsync at all, so nothing here can tell what it does");

        // The body up to the next method: enough to see whether the first thing it does is refuse.
        int end = source.IndexOf("    public ", modify + 10, StringComparison.Ordinal);
        string body = end > modify ? source[modify..end] : source[modify..];
        bool refusesOutright = body.Contains("GenerateOrderModifyRejected", StringComparison.Ordinal)
            && !body.Contains("await", StringComparison.Ordinal);

        Assert.Equal(!family.Capabilities.AmendOrders, refusesOutright);
    }

    [Fact]
    public void Nothing_claims_execution_without_market_data()
    {
        // Not a rule of the venues - a rule of what a node is. An execution client with no data client would be a
        // family orders could be sent to and nothing could decide to send them, which is not a node anybody can run.
        foreach ((string venue, VenueDescriptor descriptor) in Declared())
        {
            foreach (VenueFamily family in descriptor.Families)
            {
                if (family.Capabilities.Execution)
                {
                    Assert.True(
                        family.Capabilities.MarketData,
                        $"{venue}.{family.Name} claims orders can be sent and no market data can be received");
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void A_family_that_is_charged_no_funding_does_not_claim_to_fetch_any(string venue, string name)
    {
        // The direction that IS checkable. A venue-level history helper exists or it does not, so a family claiming
        // funding history cannot be caught out by reflection alone - KuCoin's spot market would pass on its futures
        // sibling's helper. What can be caught is the claim making no sense: a market charged no funding has no
        // funding to fetch, so claiming it is either a copied line or a misunderstanding of the field.
        VenueFamily family = Family(venue, name);
        if (!family.PaysFunding)
        {
            Assert.False(
                family.Capabilities.FundingHistory,
                $"{venue}.{name} is charged no funding and claims funding history. There is nothing for it to "
                + "fetch, so the claim is either copied from the family beside it or a misreading of the field.");
        }
    }

    [Fact]
    public void A_family_that_pays_funding_and_cannot_fetch_it_says_so_rather_than_hiding_it()
    {
        // The two are separate on purpose, and this is the combination worth noticing: a family charged funding
        // whose history cannot be fetched will backtest free and cost money live. It is allowed - it is a real
        // state an adapter can be in - but it must be visible in the declaration rather than assumed away.
        foreach ((string venue, VenueDescriptor descriptor) in Declared())
        {
            foreach (VenueFamily family in descriptor.Families)
            {
                if (family.PaysFunding && !family.Capabilities.FundingHistory)
                {
                    Assert.Fail(
                        $"{venue}.{family.Name} is charged funding and declares no funding history, so a backtest "
                        + "over it is free and a live run is not. If that is really the state, this test is where "
                        + "to say why.");
                }
            }
        }
    }

    [Fact]
    public void The_capability_that_exists_for_KuCoin_futures_says_what_it_is_there_to_say()
    {
        // The case that made all of this necessary, pinned by name so a later change has to face it. These
        // perpetuals can be downloaded, backtested, papered and traded - and not amended.
        VenueFamily futures = Family("Kucoin", "futures");

        Assert.True(futures.Capabilities.MarketData);
        Assert.True(futures.Capabilities.Execution);
        Assert.True(futures.Capabilities.BarHistory);
        Assert.True(futures.Capabilities.FundingHistory);
        Assert.False(futures.Capabilities.AmendOrders);

        // And its spot sibling can amend, which is why this cannot be a per-venue fact.
        Assert.True(Family("Kucoin", "spot").Capabilities.AmendOrders);
    }

    [Fact]
    public void A_capability_is_declared_for_every_family_and_read_from_the_declaration()
    {
        // Completeness, read off the declaration rather than a list here: a venue that grows a family cannot be
        // signed off by the family beside it.
        Assert.NotEmpty(Declared());
        foreach ((string venue, VenueDescriptor descriptor) in Declared())
        {
            Assert.NotEmpty(descriptor.Families);
            foreach (VenueFamily family in descriptor.Families)
            {
                Assert.NotNull(family.Capabilities);
                Assert.True(
                    family.Capabilities.ListInstruments,
                    $"{venue}.{family.Name} cannot list its instruments, so nothing can offer it in a picker - if "
                    + "that is really true it is a family a host cannot use at all.");
            }
        }
    }
}
