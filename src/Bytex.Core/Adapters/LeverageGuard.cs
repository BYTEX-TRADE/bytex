using Bytex.Core.Model.Instruments;

namespace Bytex.Core.Adapters;

/// <summary>
/// Refuses a configured leverage the venue will not grant, before anything is traded at a different one.
/// <para>
/// A venue asked for more leverage than it grants does not fail: it grants what it will and trades on. So a strategy
/// written, backtested and papered at 50x goes live at whatever the venue allowed, sized differently, liquidating at
/// a different price, with nothing anywhere saying the number changed. That is the same failure as a configuration
/// field nothing reads - the request was accepted and quietly not honoured - and it is worse here, because the thing
/// silently altered is the size of every position.
/// </para>
/// <para>
/// So the run refuses to start instead. Refusing names the venue's own figure and the one that was asked for, which
/// is a sentence somebody can act on; the alternative is a result, or a live position, belonging to a strategy
/// nobody wrote.
/// </para>
/// <para>
/// A venue that does not publish its ceiling is not second-guessed. <see cref="Instrument.MaxLeverage"/> is null
/// there, and null means "the venue did not say" rather than "unlimited" - so nothing is refused on a guess, and the
/// guarantee holds exactly where the venue states what it grants.
/// </para>
/// </summary>
public static class LeverageGuard
{
    /// <summary>
    /// Throws when <paramref name="configured"/> is more than the venue grants on any of <paramref name="tradable"/>.
    /// Does nothing when no leverage was configured, or where a venue publishes no ceiling to check against.
    /// </summary>
    /// <param name="configured">The leverage the run asked for; null means the venue's own setting is left alone.</param>
    /// <param name="tradable">The instruments this client may trade.</param>
    /// <param name="venue">Named in the refusal, because which venue said no is the first thing worth knowing.</param>
    public static void EnsureGranted(decimal? configured, IEnumerable<Instrument> tradable, string venue)
    {
        ArgumentNullException.ThrowIfNull(tradable);
        if (configured is not { } wanted)
        {
            return;
        }

        foreach (Instrument instrument in tradable)
        {
            if (instrument.MaxLeverage is not { } granted || wanted <= granted)
            {
                continue;
            }

            throw new ArgumentOutOfRangeException(
                nameof(configured),
                wanted,
                $"{venue} grants at most {granted}x on {instrument.Id} and {wanted}x was configured. The venue would "
                + $"have accepted the orders and traded at {granted}x, so the strategy would run at a size it was "
                + "never tested at. Configure a leverage the venue grants, or trade an instrument that grants this one.");
        }
    }
}
