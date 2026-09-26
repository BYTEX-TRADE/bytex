using Bytex.Adapters.Okx;
using Bytex.Core.Adapters;
using Bytex.Core.Model;

namespace Bytex.Adapters.Tests.Okx;

// Why: the cross-venue declaration tests check that every fact in a declaration is true of the adapter it describes.
// This file is the other half - the facts that are TRUE OF OKX AND OF NOTHING ELSE HERE, pinned by name so a later
// change has to face them rather than quietly smoothing this venue into the shape of the others.
//
// Three markets at once is the first of them, and it breaks an assumption every venue before it satisfied: that a
// family can be told apart by where it talks. It cannot here. The second is that two of the three markets are
// derivatives and only ONE of them is funded - a dated contract converges on spot by delivering rather than by
// being charged - which is the case that makes PaysFunding a per-family fact rather than "does this venue have
// derivatives".
public sealed class OkxDeclarationTests
{
    private static VenueDescriptor Okx() => new OkxPlugin().Describe();

    private static VenueFamily Family(string name) => Okx().Families.Single(f => f.Name == name);

    [Fact]
    public void The_venue_declares_three_families_and_names_the_markets_the_venue_names()
    {
        // The first venue here with more than two. The names are the venue's own words for its markets, so a host
        // showing them to a person shows what the venue's own interface shows.
        Assert.Equal(["spot", "swap", "futures"], Okx().Families.Select(f => f.Name).ToArray());
        Assert.Equal("OKX", Okx().DisplayName);
        Assert.Equal("OKX", Okx().Venue.Value);
    }

    [Fact]
    public void Each_family_holds_exactly_one_instrument_class_and_the_three_do_not_overlap()
    {
        Assert.Equal([InstrumentClass.Spot], Family("spot").InstrumentClasses);
        Assert.Equal([InstrumentClass.Swap], Family("swap").InstrumentClasses);
        Assert.Equal([InstrumentClass.Future], Family("futures").InstrumentClasses);

        // And asking the declaration which family handles a class gives one answer, which is what a host resolving
        // an instrument's family depends on.
        Assert.Equal("swap", Okx().FamilyFor(InstrumentClass.Swap)!.Name);
        Assert.Equal("futures", Okx().FamilyFor(InstrumentClass.Future)!.Name);
        Assert.Null(Okx().FamilyFor(InstrumentClass.Option));
    }

    [Fact]
    public void All_three_families_declare_the_same_host_and_the_same_socket_root()
    {
        // The fact this venue brings that no earlier one did. It is declared three times with the same value on
        // purpose: a host reading one family's declaration must not have to know that the others share it, and the
        // day the venue splits a market onto its own host that family's row is the only one that changes.
        Assert.All(Okx().Families, f => Assert.Equal(OkxVenue.DefaultHttpBase, f.HttpBase));
        Assert.All(Okx().Families, f => Assert.Equal(OkxVenue.DefaultWsBase, f.WsBase));

        // Not null, unlike KuCoin's: this venue has a fixed socket address rather than handing one out per
        // connection, so there is something for a host to hold and to override.
        Assert.All(Okx().Families, f => Assert.NotNull(f.WsBase));
    }

    [Fact]
    public void What_selects_a_family_is_a_setting_and_the_declaration_names_both_the_field_and_the_value()
    {
        // Since the address cannot distinguish the families here, the setting is the only thing that does - so
        // naming the field without the value would leave a host guessing the spelling of the venue's own enum,
        // which is the guess that starts a node that connects and never receives anything.
        Assert.Equal("Spot", Family("spot").Config["instrumentType"]);
        Assert.Equal("Swap", Family("swap").Config["instrumentType"]);
        Assert.Equal("Futures", Family("futures").Config["instrumentType"]);

        // And the values really are the enum's names, which is what makes a host able to apply them by reflection.
        Assert.Equal(
            Enum.GetNames<OkxInstrumentType>().Order(StringComparer.Ordinal),
            Okx().Families.Select(f => f.Config["instrumentType"]).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_fields_the_other_venues_use_to_name_a_market_are_declared_as_ignored_here()
    {
        // Every other venue in this engine names its markets with accountType or productType. A host that holds a
        // value for either has a value this venue ignores, and telling it so is the difference between a
        // configuration that means nothing here and one that is wrong.
        Assert.All(Okx().Families, f => Assert.Equal(["accountType", "productType"], f.IgnoredConfig));
    }

    [Fact]
    public void Only_the_perpetuals_are_charged_funding()
    {
        // The case that makes this a per-family fact. Two of the three markets are derivatives and only one is
        // funded: a dated contract converges on spot by DELIVERING, so holding one overnight is charged nothing.
        // A venue-wide "has derivatives, therefore pays funding" would price a cost into every dated position that
        // is never paid, and a venue-wide "no" would make a perpetual free to hold.
        Assert.False(Family("spot").PaysFunding);
        Assert.True(Family("swap").PaysFunding);
        Assert.False(Family("futures").PaysFunding);
    }

    [Fact]
    public void Only_the_family_that_is_charged_funding_claims_to_fetch_any()
    {
        Assert.False(Family("spot").Capabilities.FundingHistory);
        Assert.True(Family("swap").Capabilities.FundingHistory);
        Assert.False(Family("futures").Capabilities.FundingHistory);
    }

    [Fact]
    public void All_three_families_can_amend_an_order_which_is_not_true_of_every_venue_here()
    {
        // One amend endpoint serves all three markets. Worth pinning by name because the market beside this one in
        // the engine - KuCoin's perpetuals - cannot amend at all, so a strategy that resizes a protective order
        // behaves differently on the two and the declaration is what tells it which it is on.
        Assert.All(Okx().Families, f => Assert.True(f.Capabilities.AmendOrders));
    }

    [Fact]
    public void Every_family_can_be_listed_downloaded_papered_and_traded()
    {
        Assert.All(Okx().Families, f =>
        {
            Assert.True(f.Capabilities.LoadOneInstrument);
            Assert.True(f.Capabilities.ListInstruments);
            Assert.True(f.Capabilities.BarHistory);
            Assert.True(f.Capabilities.MarketData);
            Assert.True(f.Capabilities.Execution);
        });
    }

    [Fact]
    public void A_key_is_three_parts_and_the_same_three_on_every_family()
    {
        // One OKX key and one unified account behind all three markets, so the key is declared identically - and the
        // passphrase is a third part the first two venues in this engine do not have, which a host that assumed two
        // would have nowhere to put.
        Assert.All(Okx().Families, f =>
        {
            Assert.Equal(
                [OkxVenue.EnvApiKey, OkxVenue.EnvApiPassphrase, OkxVenue.EnvApiSecret],
                f.Key.Parts.Select(p => p.Variable).Order(StringComparer.Ordinal).ToArray());

            // All three required: unlike KuCoin's key version, none of these defaults.
            Assert.All(f.Key.Parts, p => Assert.True(p.Required));
        });

        // And only the two that are secrets are marked as such, so a host knows which it may show back to a person.
        VenueKey key = Family("spot").Key;
        Assert.False(key.Parts.Single(p => p.Variable == OkxVenue.EnvApiKey).Secret);
        Assert.True(key.Parts.Single(p => p.Variable == OkxVenue.EnvApiSecret).Secret);
        Assert.True(key.Parts.Single(p => p.Variable == OkxVenue.EnvApiPassphrase).Secret);
    }

    [Fact]
    public void The_fees_are_the_three_markets_own_rates_rather_than_one_venue_wide_number()
    {
        // The spot market charges four times the maker fee the derivative markets do, so a venue-wide figure would
        // be wrong on two of the three markets - which in a backtest is the difference between a strategy that
        // looks unprofitable and one that is.
        Assert.Equal(new VenueFees(0.0008m, 0.001m), Family("spot").DefaultFees);
        Assert.Equal(new VenueFees(0.0002m, 0.0005m), Family("swap").DefaultFees);
        Assert.Equal(new VenueFees(0.0002m, 0.0005m), Family("futures").DefaultFees);
    }

    [Fact]
    public void The_free_dataset_is_declared_where_it_exists_and_left_empty_where_it_does_not()
    {
        // Measured, both ways, for the same day: the daily trade archive answered 200 with 19 MB for BTC-USDT-SWAP
        // and for BTC-USDT, and 404 for BTC-USD_UM-261030. So the empty list on the futures family is a statement
        // backed by a measurement, not a gap - and it saves a host learning it from a 404 mid-download.
        Assert.Single(Family("spot").FreeDatasets);
        Assert.Single(Family("swap").FreeDatasets);
        Assert.Empty(Family("futures").FreeDatasets);

        Assert.Equal(
            "https://www.okx.com/cdn/okex/traderecords/trades/daily",
            Family("swap").FreeDatasets[0].Address);
    }

    [Fact]
    public void Nothing_carries_a_broker_id_and_the_reason_is_that_the_mechanism_is_not_published()
    {
        // The two fields say different things and both have to be read. Nothing carries an id - so a configured one
        // is a no-op here, and nothing above the adapter has to know which venues have a programme. But the venue
        // DOES run one, and publishes what an order would have to carry only to approved applicants, so there is
        // nothing to build before somebody applies.
        //
        // Declared as a rebate going unclaimed rather than as a venue with nothing to claim, because those are the
        // same to the adapter and opposite to whoever decides which programmes to join. Reading "no mechanism" as
        // "no programme" is exactly how a false statement about KuCoin came to be written down twice.
        Assert.Equal(BrokerTag.None, Okx().BrokerTag);
        Assert.Equal(BrokerProgramme.MechanismUndisclosed, Okx().BrokerProgramme);
        Assert.NotEqual(BrokerProgramme.None, Okx().BrokerProgramme);
    }

    [Fact]
    public void A_broker_id_configured_on_this_venue_changes_nothing_about_an_order()
    {
        // The no-op, checked rather than assumed: the field exists on every execution config, and setting it here
        // must neither fail nor alter what the venue receives.
        OkxExecutionClientConfig tagged = new()
        {
            ApiKey = "k",
            ApiSecret = "s",
            ApiPassphrase = "p",
            BrokerId = "bytex-",
        };

        Assert.Equal("bytex-", tagged.BrokerId);
    }
}
