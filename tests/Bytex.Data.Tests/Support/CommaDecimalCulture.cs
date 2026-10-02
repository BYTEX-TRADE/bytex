using System.Globalization;

namespace Bytex.Data.Tests.Support;

/// <summary>
/// Switches the current thread to a culture that writes 1234.5 as "1.234,5" (like de-DE or ru-RU) and restores the
/// previous one on dispose. The repository builds with InvariantGlobalization, where named cultures such as "de-DE"
/// cannot be created, so the culture is assembled by hand; what matters to the code under test is the separators,
/// not the name.
///
/// <para>
/// It proves itself before handing back: a helper that quietly stopped switching the culture would leave every test
/// that uses it passing and testing nothing, which is the one failure a culture guard cannot afford.
/// </para>
/// </summary>
internal sealed class CommaDecimalCulture : IDisposable
{
    private readonly CultureInfo _previousCulture = CultureInfo.CurrentCulture;
    private readonly CultureInfo _previousUiCulture = CultureInfo.CurrentUICulture;

    public CommaDecimalCulture()
    {
        CultureInfo culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NumberDecimalSeparator = ",";
        culture.NumberFormat.NumberGroupSeparator = ".";
        culture.NumberFormat.CurrencyDecimalSeparator = ",";
        culture.NumberFormat.CurrencyGroupSeparator = ".";
        culture.NumberFormat.PercentDecimalSeparator = ",";

        // A different minus as well as a different point. Plenty of real cultures use one - and without it a test
        // could not tell "parsed invariantly" from "parsed with whatever culture, and the digits happened to agree".
        culture.NumberFormat.NegativeSign = "~";
        culture.DateTimeFormat.DateSeparator = ".";
        culture.DateTimeFormat.ShortDatePattern = "dd.MM.yyyy";
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        if (0.5m.ToString(CultureInfo.CurrentCulture) != "0,5")
        {
            Dispose();
            throw new InvalidOperationException(
                "CommaDecimalCulture did not take: the thread still writes numbers the invariant way, so anything "
                + "measured under it would prove nothing.");
        }
    }

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _previousCulture;
        CultureInfo.CurrentUICulture = _previousUiCulture;
    }
}
