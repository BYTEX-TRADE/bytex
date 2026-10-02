namespace Bytex.Core.Adapters;

/// <summary>
/// A venue's declared number of decimal places, turned into the precision and increment an instrument carries.
///
/// <para>
/// <b>Why this exists, and it is not a tidying-up.</b> Adapters read a venue's declared places and cast them straight
/// to a byte. Kraken's futures market publishes <c>contractValueTradePrecision</c> as a SIGNED number and serves
/// negative values on live contracts - a contract that trades in multiples of a thousand declares -3, meaning the
/// increment is larger than one rather than smaller. Cast to a byte, -3 becomes 253, and <c>Quantity</c> refuses a
/// precision above 18. The exception left the provider before it returned anything, so it was not one odd contract
/// being skipped: <b>the whole family failed to load and no Kraken perpetual or dated future could be listed, added
/// or traded at all.</b>
/// </para>
///
/// <para>
/// Nothing caught it on either side. The adapter's recorded payloads carry only 0 and 4, because they were written
/// from the venue's documentation rather than from the contracts it actually serves; and the host's venue check
/// walked each venue's FIRST market, which for Kraken is spot. Two green checks over a family neither had touched.
/// </para>
///
/// <para>
/// So the reading is done in one place, for every venue, and it never throws. A single strange contract must not cost
/// a market its whole instrument list - that is the failure this exists to prevent, and it is worse than any figure
/// this could get slightly wrong.
/// </para>
/// </summary>
public static class VenuePrecision
{
    /// <summary>What a <c>Price</c> or <c>Quantity</c> will accept. Past this they throw.</summary>
    public const byte Max = 18;

    /// <summary>
    /// The precision and increment for a venue's declared decimal places.
    ///
    /// <list type="bullet">
    /// <item>Negative places mean an increment LARGER than one: -3 is a step of 1000, at precision zero, because
    /// there are no decimal places to keep.</item>
    /// <item>Zero to eighteen are places as ordinarily meant: 2 is a step of 0.01.</item>
    /// <item>More than eighteen is beyond what the engine's decimal can express, so it is held at eighteen -
    /// finer than any venue's real tick, and survivable where refusing would cost the whole family.</item>
    /// </list>
    /// </summary>
    public static (byte Precision, decimal Increment) FromDeclaredPlaces(long places)
    {
        if (places < 0)
        {
            decimal step = 1m;
            for (long i = 0; i < -places && step < decimal.MaxValue / 10m; i++)
            {
                step *= 10m;
            }

            return (0, step);
        }

        byte precision = (byte)Math.Min(places, Max);
        decimal increment = 1m;
        for (int i = 0; i < precision; i++)
        {
            increment /= 10m;
        }

        return (precision, increment);
    }
}
