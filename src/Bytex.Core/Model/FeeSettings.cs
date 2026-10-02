using System.Globalization;
using Bytex.Core.Model.Instruments;

namespace Bytex.Core.Model;

/// <summary>
/// How fills are charged in a simulation (backtest, sandbox): the venue's stored maker and taker rates, the user's own rates, or a
/// fixed amount per fill. Stored on the strategy document so a backtest, a paper run and the report all state the same assumption;
/// the same shape configures a venue directly (backtest node, sandbox client). Rates are fractions: 0.001 is 0.1 percent.
/// </summary>
public sealed record FeeSettings
{
    public const string VenueMode = "venue";
    public const string CustomMode = "custom";
    public const string FixedMode = "fixed";

    /// <summary>Highest rate a user can set: 5 percent. Anything above is a typo (someone wrote 0.1 for 0.1 percent).</summary>
    public const decimal MaxRate = 0.05m;

    /// <summary>venue | custom | fixed</summary>
    public string Mode { get; init; } = VenueMode;

    /// <summary>custom: maker rate as a fraction (0.0002 = 0.02 percent).</summary>
    public decimal? Maker { get; init; }

    /// <summary>custom: taker rate as a fraction.</summary>
    public decimal? Taker { get; init; }

    /// <summary>fixed: amount in the instrument's quote currency charged on every fill.</summary>
    public decimal? PerFill { get; init; }

    /// <summary>Free text, e.g. "VIP 2 tier" or "includes the fee-token discount".</summary>
    public string? Note { get; init; }

    public bool IsVenue => string.Equals(Mode, VenueMode, StringComparison.OrdinalIgnoreCase);

    public bool IsCustom => string.Equals(Mode, CustomMode, StringComparison.OrdinalIgnoreCase);

    public bool IsFixed => string.Equals(Mode, FixedMode, StringComparison.OrdinalIgnoreCase);

    /// <summary>What is wrong with these settings; empty when they are usable.</summary>
    public IReadOnlyList<string> Problems()
    {
        List<string> problems = new();
        if (!IsVenue && !IsCustom && !IsFixed)
        {
            problems.Add($"fees.mode must be {VenueMode}, {CustomMode} or {FixedMode}; got '{Mode}'.");
            return problems;
        }

        if (IsCustom)
        {
            if (Maker is null || Taker is null)
            {
                problems.Add("fees.custom needs both maker and taker rates (fractions, 0.001 = 0.1 percent).");
            }

            if (Maker is < 0 or > MaxRate || Taker is < 0 or > MaxRate)
            {
                problems.Add($"fees.maker and fees.taker must be between 0 and {MaxRate} (0 to 5 percent); 0.001 is 0.1 percent.");
            }
        }

        if (IsFixed && (PerFill is null || PerFill < 0))
        {
            problems.Add("fees.fixed needs perFill, an amount in the quote currency of 0 or more.");
        }

        return problems;
    }

    /// <summary>One line for reports and labels: "venue 0.10% / 0.10%", "custom 0.02% / 0.05%", "fixed 1.5 USDT per fill".</summary>
    public string Describe(Instrument? instrument = null)
    {
        if (IsCustom && Maker is { } m && Taker is { } t)
        {
            return $"custom {Pct(m)} / {Pct(t)}{NoteSuffix}";
        }

        if (IsFixed && PerFill is { } p)
        {
            return $"fixed {p.ToString("0.####", CultureInfo.InvariantCulture)} {instrument?.QuoteCurrency.Code ?? "quote"} per fill{NoteSuffix}";
        }

        return instrument is null ? "venue rates" : $"venue {Pct(instrument.MakerFee)} / {Pct(instrument.TakerFee)}";
    }

    private string NoteSuffix => string.IsNullOrWhiteSpace(Note) ? string.Empty : $" ({Note.Trim()})";

    private static string Pct(decimal rate) => (rate * 100m).ToString("0.###", CultureInfo.InvariantCulture) + "%";
}
