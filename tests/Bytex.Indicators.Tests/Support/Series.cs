namespace Bytex.Indicators.Tests.Support;

internal readonly record struct Ohlcv(decimal Open, decimal High, decimal Low, decimal Close, decimal Volume);

/// <summary>
/// Fixed input series. Everything is a literal so that a run never depends on a random generator.
/// </summary>
internal static class Series
{
    /// <summary>
    /// Closing prices of the RSI worked example published by StockCharts ChartSchool (cs-rsi.xls).
    /// </summary>
    public static readonly decimal[] StockChartsCloses =
    [
        44.3389m, 44.0902m, 44.1497m, 43.6124m, 44.3278m, 44.8264m, 45.0955m, 45.4245m, 45.8433m,
        46.0826m, 45.8931m, 46.0328m, 45.6140m, 46.2820m, 46.2820m, 46.0028m, 46.0328m, 46.4116m,
        46.2222m, 45.6439m, 46.2122m, 46.2521m, 45.7137m, 46.4515m, 45.7835m,
    ];

    /// <summary>
    /// Forty bars: a rally, a sell-off and a recovery, so that both directional movements,
    /// gaps against the previous close and window evictions are all exercised.
    /// </summary>
    public static readonly Ohlcv[] Bars =
    [
        new(100.00m, 100.36m, 99.40m, 100.18m, 82.596m),
        new(100.18m, 101.23m, 100.08m, 100.87m, 278.346m),
        new(100.87m, 101.29m, 100.25m, 100.36m, 90.821m),
        new(100.36m, 101.53m, 100.20m, 100.78m, 150.458m),
        new(100.78m, 102.55m, 100.24m, 101.69m, 228.506m),
        new(101.69m, 103.52m, 100.91m, 103.43m, 180.324m),
        new(103.43m, 103.58m, 102.87m, 103.18m, 417.257m),
        new(103.18m, 103.72m, 102.42m, 103.01m, 217.579m),
        new(103.01m, 103.82m, 102.91m, 103.72m, 142.681m),
        new(103.72m, 105.16m, 103.40m, 104.75m, 313.503m),
        new(104.75m, 105.54m, 104.02m, 105.24m, 364.547m),
        new(105.24m, 105.78m, 104.73m, 105.23m, 443.812m),
        new(105.23m, 105.52m, 104.10m, 104.98m, 103.130m),
        new(104.98m, 105.67m, 103.80m, 103.98m, 270.033m),
        new(103.98m, 104.60m, 101.37m, 102.07m, 307.862m),
        new(102.07m, 102.49m, 101.43m, 102.17m, 317.466m),
        new(102.17m, 102.61m, 100.80m, 101.56m, 475.106m),
        new(101.56m, 102.17m, 100.60m, 100.70m, 365.671m),
        new(100.70m, 101.59m, 99.50m, 100.25m, 178.068m),
        new(100.25m, 100.87m, 99.11m, 99.18m, 257.763m),
        new(99.18m, 99.33m, 97.48m, 97.58m, 395.705m),
        new(97.58m, 97.84m, 95.51m, 95.89m, 442.140m),
        new(95.89m, 96.32m, 93.56m, 94.08m, 447.523m),
        new(94.08m, 94.86m, 93.76m, 94.05m, 236.883m),
        new(94.05m, 94.85m, 92.05m, 92.91m, 117.914m),
        new(92.91m, 93.16m, 91.08m, 91.33m, 268.233m),
        new(91.33m, 92.31m, 91.28m, 92.04m, 238.526m),
        new(92.04m, 92.76m, 91.18m, 92.23m, 360.722m),
        new(92.23m, 93.34m, 91.61m, 92.77m, 74.297m),
        new(92.77m, 94.94m, 91.98m, 94.23m, 409.043m),
        new(94.23m, 94.86m, 94.09m, 94.47m, 335.430m),
        new(94.47m, 94.58m, 93.69m, 93.92m, 123.036m),
        new(93.92m, 94.13m, 93.87m, 94.04m, 118.069m),
        new(94.04m, 94.40m, 93.51m, 93.58m, 443.450m),
        new(93.58m, 94.53m, 93.32m, 94.35m, 206.325m),
        new(94.35m, 94.67m, 93.58m, 94.52m, 496.896m),
        new(94.52m, 95.40m, 94.40m, 94.94m, 95.984m),
        new(94.94m, 95.34m, 94.19m, 95.06m, 122.647m),
        new(95.06m, 95.92m, 93.92m, 94.42m, 115.971m),
        new(94.42m, 95.09m, 93.92m, 95.02m, 490.326m),
    ];

    public static decimal[] Closes => Bars.Select(b => b.Close).ToArray();

    public static decimal[] Parse(string values) =>
        values.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(v => decimal.Parse(v, System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
}
