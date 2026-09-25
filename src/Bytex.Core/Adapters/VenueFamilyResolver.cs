using Bytex.Core.Model;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;

namespace Bytex.Core.Adapters;

/// <summary>
/// Which of a venue's market families holds a given instrument, and the instrument itself.
/// <para>
/// A venue's families are separate APIs: a spot instrument is loaded through one and a perpetual through another, so
/// choosing between them is choosing which client to build - and that choice has to be made before anything can be
/// loaded. Hosts were making it by reading the symbol's spelling, which works on the venues that exist today and is
/// a convention rather than a fact: "-PERP" is what Binance and Bybit happen to call a perpetual, KuCoin's futures
/// market calls the same thing "XBTUSDTM", and a venue naming one some other way would be read as spot.
/// </para>
/// <para>
/// So it is asked rather than inferred. Loading one instrument needs no key - every provider goes through the
/// venue's public endpoint - so each family is asked for that single id and the family that answers owns it. Three
/// unauthenticated requests at worst on a venue with three families, once, and then the answer is known.
/// </para>
/// </summary>
public sealed class VenueFamilyResolver
{
    private readonly VenueDescriptor _venue;
    private readonly Func<VenueFamily, IInstrumentProvider> _providerFor;

    /// <summary>
    /// Takes a venue's declaration and a way to build a provider for one of its families. The caller supplies the
    /// second because only an adapter knows how to construct its own clients; everything else here is the loop,
    /// which is identical on every venue and is the reason this is not written once per host.
    /// </summary>
    public VenueFamilyResolver(VenueDescriptor venue, Func<VenueFamily, IInstrumentProvider> providerFor)
    {
        _venue = venue ?? throw new ArgumentNullException(nameof(venue));
        _providerFor = providerFor ?? throw new ArgumentNullException(nameof(providerFor));
    }

    /// <summary>
    /// The family that lists this instrument and the instrument as that family describes it, or null when no family
    /// of this venue has it.
    /// <para>
    /// Families are asked in the order the venue declares them, and the first that answers wins. Asking stops there:
    /// an instrument belongs to one family, and a venue that listed the same id in two would be ambiguous in a way
    /// the declaration already refuses.
    /// </para>
    /// <para>
    /// A family that cannot fetch one instrument by name is skipped rather than asked and failed, which is what its
    /// declaration is for. A family that refuses the id - which is every family that does not list it - leaves its
    /// provider empty, which is the same answer on every venue since that was made uniform.
    /// </para>
    /// </summary>
    public async Task<VenueFamilyMatch?> ResolveAsync(InstrumentId instrumentId, CancellationToken ct = default)
    {
        if (instrumentId.Venue != _venue.Venue)
        {
            return null;
        }

        foreach (VenueFamily family in _venue.Families)
        {
            if (!family.Capabilities.LoadOneInstrument)
            {
                continue;
            }

            ct.ThrowIfCancellationRequested();
            IInstrumentProvider provider = _providerFor(family);

            try
            {
                await provider.LoadAsync(instrumentId, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // A family that could not be asked is not a family that answered no. Carry on to the next one and
                // let the caller see "no family has it" only when every family really was asked and said so - a
                // venue being briefly unreachable must not read as an instrument that does not exist.
                continue;
            }

            if (provider.Find(instrumentId) is { } instrument)
            {
                return new VenueFamilyMatch(family, instrument);
            }
        }

        return null;
    }

    /// <summary>
    /// The family covering an instrument class, without asking the venue anything. Useful when the class is already
    /// known - from an instrument already loaded, or from a person choosing "perpetuals" rather than a symbol - and
    /// no substitute for <see cref="ResolveAsync"/> when all that is held is an id.
    /// </summary>
    public VenueFamily? ForClass(InstrumentClass instrumentClass) => _venue.FamilyFor(instrumentClass);
}

/// <summary>Which family holds an instrument, and the instrument as that family describes it.</summary>
public sealed record VenueFamilyMatch(VenueFamily Family, Instrument Instrument);
