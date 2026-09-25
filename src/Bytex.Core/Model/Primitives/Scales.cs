namespace Bytex.Core.Model.Primitives;

/// <summary>
/// The unit scales the engine converts between, so that no formula carries the number itself. A scale belongs here
/// when more than one project needs it; a venue's own limits belong to that venue's adapter.
/// </summary>
public static class Scales
{
    /// <summary>A fraction becomes a percentage when multiplied by this.</summary>
    public const decimal Percent = 100m;

    /// <summary>A fraction becomes basis points when multiplied by this; one basis point is a hundredth of a percent.</summary>
    public const decimal BasisPoints = 10_000m;

    /// <summary>Basis points become a percentage when divided by this.</summary>
    public const decimal BasisPointsPerPercent = 100m;

    /// <summary>
    /// Trading days in a year, the convention daily performance ratios are annualised with. It is a convention rather
    /// than a count: no calendar year has exactly this many sessions.
    /// </summary>
    public const double TradingDaysPerYear = 252d;
}
