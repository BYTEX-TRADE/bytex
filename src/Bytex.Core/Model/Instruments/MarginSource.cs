namespace Bytex.Core.Model.Instruments;

/// <summary>
/// Where an instrument's margin requirement came from.
///
/// <para>
/// <b>Why a number needs a provenance.</b> <see cref="Instrument.MarginInit"/> decides how much of an account a
/// leveraged position ties up and therefore the price at which it is liquidated, and <see
/// cref="Instrument.InitialMarginRate"/> takes the LARGER of it and 1/leverage - so it is also a floor under every
/// leverage a strategy asks for. Two of the three venues shipped before 0.7.0 carried 0.05 and 0.025 for every
/// contract, hard-coded: on Bybit's BTCUSDT the venue's own risk limits give 0.0066, so the figure in use was 7.6
/// times the truth and clamped every leverage above 20x. Runs completed, results looked like results, and nothing
/// anywhere said the margin had been invented.
/// </para>
///
/// <para>
/// Reading the venue fixed the number going forward and could not fix what is already stored. A person with a saved
/// result and a fresh one that disagrees needs the report to say why, and a report that only carries the figure
/// cannot: 0.05 read from a venue and 0.05 chosen by an adapter are the same number and a different claim. That is
/// what this records, per instrument, at the moment the figure is decided - so it travels with the instrument into
/// the catalog and into the run that used it.
/// </para>
///
/// <para>
/// Every member below is a case measured in a shipped adapter, not a shape allowed for. They are ordered from least
/// to most known.
/// </para>
/// </summary>
public enum MarginSource
{
    /// <summary>
    /// Nothing recorded it. The default, and it must stay the default: an instrument stored before this existed, or
    /// built by hand in a test or a host, reads back as this rather than as a claim about a venue. A run reporting
    /// this is saying "the catalog entry I used predates the marker", which is the honest answer and the reason a
    /// stored result can disagree with a fresh one.
    /// </summary>
    Unrecorded = 0,

    /// <summary>
    /// Nothing is borrowed here, so there is no requirement to read and zero is the right figure rather than a gap.
    /// Spot on every venue.
    /// </summary>
    NotMargined,

    /// <summary>
    /// The venue publishes no margin for this instrument and the engine holds none, so a position in it posts
    /// nothing. Distinct from <see cref="NotMargined"/> because this IS a margined product at the venue - the engine
    /// simply cannot say by how much, and a liquidation therefore will not fire on the requirement. Bybit's options
    /// are the measured case: the risk-limit endpoint refuses the category, the contract data carries no
    /// leverageFilter, and a short option is really margined there by a portfolio calculation the adapter cannot
    /// see.
    /// </summary>
    VenueSilent,

    /// <summary>
    /// The engine substituted a figure of its own because the venue published nothing usable. The number is not the
    /// venue's and liquidation happens at a price nobody measured - which is survivable only because it is said out
    /// loud here.
    /// </summary>
    AdapterDefault,

    /// <summary>
    /// The venue published it, but not for this contract: a venue-wide figure that stands for every instrument.
    /// Binance without a credential is the measured case - <c>requiredMarginPercent</c> reads 5.0 on all 909 USD-M
    /// contracts and on all 30 coin-margined ones, from a 100-USD perpetual to a 10-USD quarterly, and 5 percent
    /// supports at most 20x where the venue grants 125x. Sourced beats invented and is not the same as right.
    /// </summary>
    VenueWideDefault,

    /// <summary>
    /// The venue published it for this contract, at the tier a position starts in. The answer this release was for.
    /// </summary>
    VenuePerContract,
}
