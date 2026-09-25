using System.Text.Json.Serialization;
using Bytex.Core.Model;
using Bytex.Core.Model.Identifiers;

namespace Bytex.Core.Adapters;

/// <summary>
/// What an adapter knows about its venue and everything above it would otherwise guess: which markets it covers, what
/// a key for it looks like, what its clients need configuring with, what it charges before an instrument is loaded,
/// and what it publishes for free.
/// <para>
/// Every one of those was being hard-coded by hosts, per venue, from reading this source - and one of them was being
/// inferred from a symbol's spelling, which is true of the venues that exist and is a convention rather than a fact.
/// A host that has never spoken to whoever wrote the adapter should be able to onboard a venue from this alone.
/// </para>
/// <para>
/// Almost nothing here is venue-wide, so almost nothing is declared at that level: hosts, keys, fees and datasets all
/// differ between a venue's spot and derivative markets, and a flat declaration would have to pick one and leave the
/// host qualifying it again.
/// </para>
/// </summary>
public sealed record VenueDescriptor
{
    /// <summary>The venue, as an instrument id names it.</summary>
    public required Venue Venue { get; init; }

    /// <summary>What the venue is called where a person reads it.</summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// How this venue would carry a broker or partner id on the orders an adapter sends (R11.13). Declared whether or
    /// not an id is configured: an adapter that cannot say how it would carry one has not thought about it.
    /// </summary>
    public required BrokerTag BrokerTag { get; init; }

    /// <summary>
    /// Whether this venue pays a rebate on the trades a host routes to it, and if it does not, what stands in the way.
    /// <para>
    /// Separate from <see cref="BrokerTag"/> because that field answers how an id is carried, which is only the same
    /// question while the answer is that one IS carried. Reading "no mechanism" as "no programme" is what put a false
    /// statement in this repository about a venue that runs two broker tiers, and the cost of that mistake is not a
    /// broken request - it is a rebate nobody knew was there.
    /// </para>
    /// </summary>
    public required BrokerProgramme BrokerProgramme { get; init; }

    /// <summary>The market families this venue offers, each with the facts that are true of it and not of the others.</summary>
    public required IReadOnlyList<VenueFamily> Families { get; init; }

    /// <summary>The family covering the given instrument class, or null when this venue does not offer it.</summary>
    public VenueFamily? FamilyFor(InstrumentClass instrumentClass) =>
        Families.FirstOrDefault(f => f.InstrumentClasses.Contains(instrumentClass));
}

/// <summary>
/// One market family of a venue - spot, USD-margined futures, linear perpetuals - and the facts that are its own. A
/// venue's families differ in the things that matter most to a host: Binance's spot and futures markets answer on
/// different hosts, charge different fees and publish different datasets.
/// </summary>
public sealed record VenueFamily
{
    /// <summary>What the adapter calls this family in its configuration, in the spelling a host would write.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// The instrument classes this family returns. Produced from the venue's own contract data, never from how a
    /// symbol is spelled: "-PERP" is a convention of the venues that exist today, and a venue that names a perpetual
    /// some other way would be read as spot by anything trusting the spelling.
    /// </summary>
    public required IReadOnlyList<InstrumentClass> InstrumentClasses { get; init; }

    /// <summary>Whether positions in this family are charged funding.</summary>
    public required bool PaysFunding { get; init; }

    /// <summary>
    /// Where this family's REST API answers, which is the default of the <c>baseUrlHttp</c> setting: written back
    /// into a client's configuration it changes nothing, and a host pointing the adapter at a proxy or a recording
    /// knows which field to set and what it is replacing. It is the root the adapter appends its own paths to, never
    /// one endpoint of it - which endpoints exist is the adapter's business and nobody else's.
    /// </summary>
    public required string HttpBase { get; init; }

    /// <summary>
    /// Where this family's websocket answers, on the same terms as <see cref="HttpBase"/>, or null when the venue
    /// hands the address out per connection and there is no fixed one to state. Null is a fact about the venue, not a
    /// gap in the declaration: a host must not offer a socket address for such a family, because nothing would read
    /// it until a connection is already being opened.
    /// <para>
    /// Written out even when it is null, against the engine-wide habit of leaving nulls out: this one is a
    /// statement rather than an absence, and a reader that has to tell "no socket base" from "this writer
    /// never mentioned it" cannot do it from a field that is not there. The declaration is read as JSON -
    /// a host takes it from <c>bytex venues --json</c> rather than by hosting the engine - so a required
    /// field that silently disappears is a reader that fails on the one venue the null case exists for.
    /// </para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public required string? WsBase { get; init; }

    /// <summary>What a key for this family is made of, and the variables it is read from.</summary>
    public required VenueKey Key { get; init; }

    /// <summary>
    /// The settings that select this family, by the name a client's configuration uses and the value to give it -
    /// <c>accountType: Spot</c>, <c>productType: Linear</c>. Naming the field without the value leaves a host
    /// guessing the spelling of a venue's own enum, which is the guess that starts a node that connects and then
    /// never receives anything; empty means the venue has one family and nothing selects it.
    /// </summary>
    public required IReadOnlyDictionary<string, string> Config { get; init; }

    /// <summary>
    /// Fields other venues need and this family ignores, so a host can tell a configuration that means nothing here
    /// from one that is wrong, rather than carrying a table of which venue wants which.
    /// </summary>
    public IReadOnlyList<string> IgnoredConfig { get; init; } = [];

    /// <summary>
    /// What this family charges before any instrument is loaded, for an estimate only: an instrument's own rates are
    /// the truth the moment there is an instrument.
    /// </summary>
    public required VenueFees DefaultFees { get; init; }

    /// <summary>
    /// The datasets this family publishes for anyone to download, with where they are. Empty means the venue
    /// publishes none for this family - which is a statement, not silence, and saves a host learning it from a 404.
    /// </summary>
    public IReadOnlyList<VenueDataset> FreeDatasets { get; init; } = [];

    /// <summary>What the ADAPTER can do with this family, which is not the same as what the venue offers.</summary>
    public required VenueCapabilities Capabilities { get; init; }
}

/// <summary>
/// What an adapter can actually do with one market family. Every one of these is about the ADAPTER and not the venue:
/// a venue may offer perpetuals for trading while the adapter to it has no execution client written yet, and those
/// are different facts. Hosts were keeping this as hand-maintained tables per venue, which is precisely what goes
/// stale - a family gains a capability and the table does not, or loses one and the table still promises it.
/// <para>
/// There are no derived answers here on purpose. "Can a node be papered" is market data plus instruments; "can it be
/// traded live" is execution. A single tradable flag would have been wrong for KuCoin's perpetuals for a day, which
/// is how this came to be needed: they were downloadable, backtestable and paperable with no execution client at all.
/// </para>
/// </summary>
public sealed record VenueCapabilities
{
    /// <summary>One instrument can be fetched by name, which is what an add-instrument path needs.</summary>
    public required bool LoadOneInstrument { get; init; }

    /// <summary>What the family holds can be listed, which is what an instrument picker needs.</summary>
    public required bool ListInstruments { get; init; }

    /// <summary>Bar history can be downloaded without a running node.</summary>
    public required bool BarHistory { get; init; }

    /// <summary>
    /// Funding history can be downloaded. Not the same as <see cref="VenueFamily.PaysFunding"/>, which is a fact
    /// about the venue: a family can be charged funding and have no helper to fetch what it was charged.
    /// </summary>
    public required bool FundingHistory { get; init; }

    /// <summary>A node can receive market data from this family, which is what a paper node needs.</summary>
    public required bool MarketData { get; init; }

    /// <summary>Orders can be sent to this family, which is what a live node needs and a paper node does not.</summary>
    public required bool Execution { get; init; }

    /// <summary>
    /// An order already placed can be changed. False is not a missing feature: KuCoin's perpetual futures cannot
    /// amend at all, neither plain orders nor stops.
    /// <para>
    /// It is declared because a strategy that resizes a protective order behaves differently where it cannot - and
    /// the alternative to declaring it is every caller either guessing from the venue's name or finding out when a
    /// position has grown past the stop guarding it.
    /// </para>
    /// </summary>
    public required bool AmendOrders { get; init; }
}

/// <summary>What a key is made of, so a host can hold one without knowing the venue.</summary>
public sealed record VenueKey
{
    /// <summary>The environment variable each part is read from, in the order a person would be asked for them.</summary>
    public required IReadOnlyList<VenueKeyPart> Parts { get; init; }
}

/// <summary>
/// One part of a key: what it is called, where it is read from, whether it is a secret, and whether it has to be
/// supplied. An optional part is still part of the key - KuCoin's key version defaults, and a right passphrase with
/// the wrong version fails to sign - so a host that never offers the field cannot fix that for a user.
/// </summary>
public sealed record VenueKeyPart(string Name, string Variable, bool Secret, bool Required = true);

/// <summary>Maker and taker as fractions - 0.001 is ten basis points.</summary>
public sealed record VenueFees(decimal Maker, decimal Taker);

/// <summary>A dataset a venue publishes for free: what it holds and where it is.</summary>
public sealed record VenueDataset(string Kind, string Address);

/// <summary>
/// How a venue carries a broker id, which differs on every venue and is the reason it cannot be retrofitted: one
/// wants it prefixed to the client order id, another in a header, another in a field on the order.
/// </summary>
public enum BrokerTag
{
    /// <summary>
    /// Nothing carries an id on this venue. Why not is <see cref="VenueDescriptor.BrokerProgramme"/>'s answer and not
    /// this field's - the venue may run no programme at all, or run one this adapter does not carry.
    /// </summary>
    None,

    /// <summary>
    /// The id prefixes the client order id. That changes the identity of every order placed, which is the key
    /// reconciliation matches on, so turning one on needs a reconciliation test of its own.
    /// </summary>
    ClientOrderIdPrefix,

    /// <summary>The id travels in a header on the order request.</summary>
    RequestHeader,

    /// <summary>The id travels in a field of the order itself.</summary>
    OrderField,

    /// <summary>
    /// The id travels as a signed credential on every request, not only on orders: an id, a name, and a signature
    /// over the timestamp, the id and the API key, made with a second secret the programme issues.
    /// <para>
    /// The shape that showed a single configured id is not enough for every venue. A prefix or a header needs one
    /// string; this needs three values and a signature, and it has to be on every REST request rather than on the
    /// ones that place orders - a venue can pay nothing while every order it received was tagged.
    /// </para>
    /// </summary>
    SignedRequestCredential,
}

/// <summary>
/// Whether a venue pays a host a rebate on the trades routed to it, and what stands in the way when it does not.
/// <para>
/// This is the question a broker programme is decided on, and it has three "no" answers that look identical from the
/// engine and are completely different to whoever signs the agreements: a venue with no programme is nothing to
/// pursue, a venue whose mechanism is known and uncarried is adapter work, and a venue that publishes its mechanism
/// only to approved applicants cannot be built at all until somebody applies.
/// </para>
/// </summary>
public enum BrokerProgramme
{
    /// <summary>The venue runs no broker or partner programme.</summary>
    None,

    /// <summary>
    /// It runs one and this adapter carries an id for it, by the mechanism <see cref="VenueDescriptor.BrokerTag"/>
    /// names. Configuring an id is all that is left.
    /// </summary>
    Carried,

    /// <summary>
    /// It runs one, its mechanism is published, and this adapter does not carry it. Every trade routed here earns a
    /// rebate that is not being claimed, and closing that is adapter work of a known size.
    /// </summary>
    NotCarried,

    /// <summary>
    /// It runs one and publishes the mechanism only to approved applicants. Nothing can be carried before applying,
    /// so this is not adapter work waiting to be scheduled - it waits on a decision and an application.
    /// </summary>
    MechanismUndisclosed,
}

/// <summary>
/// Implemented by a plugin that brings a venue, so a host can read what the venue is without constructing a client or
/// holding a key. Optional: a plugin that brings a strategy or a history service has no venue to describe.
/// </summary>
public interface IVenuePlugin
{
    /// <summary>What this plugin's venue is, and what is true of each of its families.</summary>
    VenueDescriptor Describe();
}
