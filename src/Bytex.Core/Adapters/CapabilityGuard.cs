using System.Globalization;
using System.Reflection;

namespace Bytex.Core.Adapters;

/// <summary>
/// Refuses a client configured for something its venue's family does not do, before the node starts.
/// <para>
/// A venue family declares what it can do - whether it carries market data, whether it can execute, whether an order
/// can be amended once placed. Reading that is how a host stops offering a person something the venue will not do.
/// But a host is not the only way a node is built: one assembled by hand, or from a configuration file, or by a
/// script, reaches the same engine through the same factories and has read nothing. It starts, connects, and then
/// does nothing for a reason that appears in no report - which is the shape of failure this whole declaration exists
/// to remove, arriving through the one door nobody was watching.
/// </para>
/// <para>
/// So the engine asks as well. The same fact, checked where every node passes regardless of what assembled it, with
/// the same sentence - so a guard in a host is a better message rather than the only one.
/// </para>
/// </summary>
public static class CapabilityGuard
{
    /// <summary>
    /// The family of <paramref name="venue"/> that <paramref name="config"/> selects, or null when the venue declares
    /// exactly one family or none of them matches.
    /// <para>
    /// A family says which configuration values select it - the name AND the value, because on a venue with one
    /// unified API the product type in the request is the only thing that distinguishes two markets. So the match is
    /// made by reading those properties off the configuration rather than by guessing from a client's name.
    /// </para>
    /// </summary>
    public static VenueFamily? FamilyOf(VenueDescriptor venue, object config)
    {
        ArgumentNullException.ThrowIfNull(venue);
        ArgumentNullException.ThrowIfNull(config);

        if (venue.Families.Count == 1)
        {
            return venue.Families[0];
        }

        foreach (VenueFamily family in venue.Families)
        {
            if (family.Config.Count > 0 && family.Config.All(setting => Selects(config, setting.Key, setting.Value)))
            {
                return family;
            }
        }

        return null;
    }

    /// <summary>
    /// Throws when the family this configuration selects does not declare <paramref name="capability"/>.
    /// <para>
    /// Silent when the family cannot be identified: a venue whose families are not distinguishable by configuration
    /// is a declaration problem of its own, and refusing every node on such a venue would turn one venue's
    /// under-declaration into an outage. The declaration's own tests are where that is caught.
    /// </para>
    /// </summary>
    /// <param name="venue">The venue's declaration.</param>
    /// <param name="config">The configuration the node is building this client from.</param>
    /// <param name="capability">Reads the capability off the family it selected.</param>
    /// <param name="name">What the capability is called in the refusal.</param>
    /// <param name="clientId">Which client is being built, so the refusal names it.</param>
    public static void EnsureDeclared(
        VenueDescriptor venue,
        object config,
        Func<VenueCapabilities, bool> capability,
        string name,
        string clientId)
    {
        ArgumentNullException.ThrowIfNull(venue);
        ArgumentNullException.ThrowIfNull(capability);

        if (FamilyOf(venue, config) is not { } family || capability(family.Capabilities))
        {
            return;
        }

        throw new InvalidOperationException(
            $"{venue.Venue}'s {family.Name} family does not do {name}, and client '{clientId}' is configured for it. "
            + "The node would start and then be quiet about it, so it refuses instead. Configure a family that "
            + $"declares {name}, or remove this client.");
    }

    /// <summary>
    /// Whether the configuration carries this setting at this value. Compared as text, because a declared value is
    /// text and the property behind it may be an enum, a string or a number.
    /// </summary>
    private static bool Selects(object config, string setting, string value)
    {
        PropertyInfo? property = config.GetType().GetProperty(
            setting,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

        if (property?.GetValue(config) is not { } actual)
        {
            return false;
        }

        string text = actual is IFormattable formattable
            ? formattable.ToString(null, CultureInfo.InvariantCulture)
            : actual.ToString() ?? string.Empty;

        return string.Equals(text, value, StringComparison.OrdinalIgnoreCase);
    }
}
