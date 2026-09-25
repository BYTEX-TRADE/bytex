namespace Bytex.Backtest;

/// <summary>
/// What a simulated venue models, as names a program can read. A product built on this engine tells its users what it
/// can and cannot do, and "can" has to come from the engine rather than from somebody remembering to read a release
/// note: a name is here only while the venue that produced it really models the thing, and a venue configured out of
/// it does not claim it.
/// </summary>
public static class SimulationCapabilities
{
    /// <summary>A fill is bounded by the size on offer where it happens, and what is left goes on working or is given up (R8.24).</summary>
    public const string PartialFills = "partialFills";

    /// <summary>A perpetual position is charged the funding rates the run was given (R8.15).</summary>
    public const string Funding = "funding";

    /// <summary>
    /// An order that takes eats the book level by level and pays what each one costs, rather than filling at the
    /// touch (R8.9). Distinct from <see cref="PartialFills"/> on purpose: that one says a fill was bounded by what
    /// was on offer at the touch, this one says the order paid for the depth it took, and they are different
    /// promises to whoever reads a result.
    /// </summary>
    public const string BookDepth = "bookDepth";

    /// <summary>A margin account that falls below its maintenance margin has its positions closed by the venue (R8.16).</summary>
    public const string Liquidation = "liquidation";

    /// <summary>
    /// A position held across the venue's rollover is charged interest on it (R8.19). Distinct from
    /// <see cref="Funding"/>: funding is a perpetual's own mechanism, paid between the two sides from rates the run
    /// was given, and this is the cost of the money behind a leveraged position - both sides pay it, it applies to
    /// instruments with no funding at all, and the two can be charged on the same position on the same day.
    /// </summary>
    public const string RolloverInterest = "rolloverInterest";

    /// <summary>What this venue models, given how it was configured.</summary>
    public static IReadOnlyList<string> Of(SimulatedVenueConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        List<string> capabilities = new();
        if (config.FillSizing == FillSizing.AvailableSize)
        {
            capabilities.Add(PartialFills);
        }

        // Funding is charged on a perpetual position from the rates a run was given; a cash account cannot hold one.
        if (config.AccountType == Core.Model.AccountType.Margin)
        {
            capabilities.Add(Funding);
        }

        if (config.AccountType == Core.Model.AccountType.Margin && config.Liquidate)
        {
            capabilities.Add(Liquidation);
        }

        // Matching against a book is a thing a venue can always do; whether it did is what a run says for itself,
        // because it depends on the data the run was given rather than on how the venue was set up.
        if (config.FillSizing == FillSizing.AvailableSize)
        {
            capabilities.Add(BookDepth);
        }

        // And whatever behaviours the run added itself, which the simulator knows nothing about beyond their names.
        foreach (ISimulationModule module in config.Modules)
        {
            capabilities.Add(module.Name);
        }

        return capabilities;
    }
}
