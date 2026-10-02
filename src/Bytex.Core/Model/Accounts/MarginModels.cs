using System.Globalization;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Core.Model.Accounts;

/// <summary>
/// What is being margined: one instrument, one size, one price, at the leverage the account holds for it.
/// </summary>
/// <param name="Instrument">The contract.</param>
/// <param name="Quantity">How much of it.</param>
/// <param name="Price">The price the requirement is measured at.</param>
/// <param name="Leverage">What the account was granted for this instrument.</param>
/// <param name="UseQuoteForInverse">
/// For an inverse contract, whether the notional is wanted in the quote currency rather than the base one. It is the
/// same choice <see cref="Instrument.NotionalValue"/> takes, and it is passed through rather than decided here.
/// </param>
public readonly record struct MarginRequest(
    Instrument Instrument,
    Quantity Quantity,
    Price Price,
    decimal Leverage,
    bool UseQuoteForInverse = false)
{
    /// <summary>What the position is worth, in the currency the requirement will be in.</summary>
    public Money Notional => Instrument.NotionalValue(Quantity, Price, UseQuoteForInverse);
}

/// <summary>
/// How much a position has to be paid for (R4.12).
///
/// <para>
/// This exists because a venue's real requirement is not one number times a notional. Every large venue charges more
/// margin for a larger position - a table of tiers by notional - and the engine's own rule, one rate for any size,
/// understates what a big position costs. Understating margin in a backtest is the dangerous direction: a run sizes
/// positions it could not have afforded and reports a return nobody could have had.
/// </para>
///
/// <para>
/// So the rule is replaceable, and the two that ship are the engine's own and a tiered one. A venue whose rule is
/// neither - portfolio margin that offsets a hedged book, a broker's own formula - is a model somebody writes without
/// touching the engine.
/// </para>
/// </summary>
public interface IMarginModel
{
    /// <summary>What to call this model where a result or a log says which one was used.</summary>
    string Name { get; }

    /// <summary>What has to be there before the position can be taken.</summary>
    Money Initial(in MarginRequest request);

    /// <summary>What has to stay there for it to be kept, below which a venue liquidates.</summary>
    Money Maintenance(in MarginRequest request);
}

/// <summary>
/// The engine's own rule: the notional times a rate, for any size.
///
/// <para>
/// The initial rate is the larger of what the requested leverage implies and what the instrument says it requires, so a
/// venue's floor wins over a leverage nobody would be granted. That clamp is the whole of why leverage in a result is
/// reported as what was APPLIED rather than what was asked for.
/// </para>
/// </summary>
public sealed class RateMarginModel : IMarginModel
{
    /// <summary>The one every account uses until it is given another.</summary>
    public static readonly RateMarginModel Default = new();

    public string Name => "rate";

    public Money Initial(in MarginRequest request)
    {
        Money notional = request.Notional;
        return new Money(notional.Amount * request.Instrument.InitialMarginRate(request.Leverage), notional.Currency);
    }

    public Money Maintenance(in MarginRequest request)
    {
        Money notional = request.Notional;
        return new Money(notional.Amount * request.Instrument.MaintenanceMarginRate, notional.Currency);
    }
}

/// <summary>One step of a tiered requirement: everything from this notional up, until the next tier.</summary>
/// <param name="NotionalFrom">The notional this tier starts at. The first tier starts at nothing.</param>
/// <param name="InitialRate">The fraction of the notional needed to open a position in this tier.</param>
/// <param name="MaintenanceRate">The fraction needed to keep it.</param>
public readonly record struct MarginTier(decimal NotionalFrom, decimal InitialRate, decimal MaintenanceRate);

/// <summary>
/// A requirement that rises with the size of the position, which is what every large venue actually charges.
///
/// <para>
/// The tier is chosen by the notional of the whole request, which is what a venue does: a position is in one bracket,
/// not spread across several. The leverage clamp is the same as the flat model's - the tier's rate is a floor, and a
/// leverage asking for less margin than the tier requires does not get it.
/// </para>
///
/// <para>
/// A table is checked when it is built rather than when it is read: tiers in order, starting at nothing, with rates
/// that are fractions and a maintenance rate no larger than the initial one. A table with a gap in it would quietly
/// margin a position at the tier below the one it belongs to, which is the same understatement this model exists to
/// remove.
/// </para>
/// </summary>
public sealed class TieredMarginModel : IMarginModel
{
    private readonly MarginTier[] _tiers;

    public TieredMarginModel(IEnumerable<MarginTier> tiers, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(tiers);
        _tiers = [.. tiers];
        Name = name ?? "tiered";

        if (_tiers.Length == 0)
        {
            throw new ArgumentException("A tiered margin model needs at least one tier.", nameof(tiers));
        }

        if (_tiers[0].NotionalFrom != 0m)
        {
            throw new ArgumentException(
                $"The first tier has to start at nothing; this one starts at {_tiers[0].NotionalFrom}, which leaves every position below that unmargined.",
                nameof(tiers));
        }

        for (int i = 0; i < _tiers.Length; i++)
        {
            MarginTier tier = _tiers[i];
            if (i > 0 && tier.NotionalFrom <= _tiers[i - 1].NotionalFrom)
            {
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture, $"Tiers have to rise: tier {i} starts at {tier.NotionalFrom}, which is not above {_tiers[i - 1].NotionalFrom}."),
                    nameof(tiers));
            }

            if (tier.InitialRate <= 0m || tier.InitialRate > 1m)
            {
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture, $"Tier {i} asks for an initial rate of {tier.InitialRate}, which is not a fraction of a notional."),
                    nameof(tiers));
            }

            if (tier.MaintenanceRate <= 0m || tier.MaintenanceRate > tier.InitialRate)
            {
                throw new ArgumentException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Tier {i} asks to keep {tier.MaintenanceRate} of the notional and to open with {tier.InitialRate}: a position that could be opened would be liquidated at once."),
                    nameof(tiers));
            }
        }
    }

    public string Name { get; }

    public IReadOnlyList<MarginTier> Tiers => _tiers;

    /// <summary>The tier a notional of this size falls in: the highest one that starts at or below it.</summary>
    public MarginTier TierFor(decimal notional)
    {
        MarginTier found = _tiers[0];
        foreach (MarginTier tier in _tiers)
        {
            if (tier.NotionalFrom <= notional)
            {
                found = tier;
            }
        }

        return found;
    }

    public Money Initial(in MarginRequest request)
    {
        Money notional = request.Notional;
        decimal rate = TierFor(Math.Abs(notional.Amount)).InitialRate;
        decimal fromLeverage = request.Leverage <= 0m ? 1m : 1m / request.Leverage;
        return new Money(notional.Amount * Math.Max(rate, fromLeverage), notional.Currency);
    }

    public Money Maintenance(in MarginRequest request)
    {
        Money notional = request.Notional;
        return new Money(notional.Amount * TierFor(Math.Abs(notional.Amount)).MaintenanceRate, notional.Currency);
    }
}
