using System.Globalization;

namespace Bytex.Data.Tests.Support;

/// <summary>
/// Switches the current thread to a culture that writes 1234.5 as "1.234,5" (like de-DE or ru-RU) and restores
/// the previous one on dispose. The repository builds with InvariantGlobalization, where named cultures such as
/// "de-DE" cannot be created, so the culture is assembled by hand; what matters to parsing code is the
/// separators, not the name.
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
        culture.DateTimeFormat.DateSeparator = ".";
        culture.DateTimeFormat.ShortDatePattern = "dd.MM.yyyy";
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _previousCulture;
        CultureInfo.CurrentUICulture = _previousUiCulture;
    }
}
