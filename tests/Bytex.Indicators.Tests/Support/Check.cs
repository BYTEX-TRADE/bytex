using Bytex.Core.Indicators;
using Xunit.Sdk;

namespace Bytex.Indicators.Tests.Support;

internal static class Check
{
    /// <summary>
    /// Decimal carries 28-29 significant digits; an incremental and a from-scratch evaluation of the same
    /// formula differ only by rounding in the last few of them. 1e-18 is far below anything a price can express.
    /// </summary>
    public const decimal Tolerance = 0.000000000000000001m;

    public static void Close(decimal expected, decimal actual, decimal tolerance = Tolerance, string? because = null)
    {
        if (Math.Abs(expected - actual) > tolerance)
        {
            throw new XunitException($"Expected {expected} +/- {tolerance} but found {actual}{(because is null ? string.Empty : " (" + because + ")")}.");
        }
    }

    /// <summary>All numeric outputs of a built-in indicator, in a fixed order, for whole-state comparisons.</summary>
    public static decimal[] Outputs(IIndicator indicator) => indicator switch
    {
        MovingAverage ma => [ma.Value],
        VolumeWeightedAveragePrice vwap => [vwap.Value],
        RelativeStrengthIndex rsi => [rsi.Value],
        MovingAverageConvergenceDivergence macd => [macd.Value, macd.Signal, macd.Histogram],
        RateOfChange roc => [roc.Value],
        Stochastics stoch => [stoch.ValueK, stoch.ValueD],
        OnBalanceVolume obv => [obv.Value],
        AverageTrueRange atr => [atr.Value],
        BollingerBands bb => [bb.Upper, bb.Middle, bb.Lower, bb.Width],
        KeltnerChannel kc => [kc.Upper, kc.Middle, kc.Lower],
        DonchianChannel dc => [dc.Upper, dc.Middle, dc.Lower],
        AverageDirectionalIndex adx => [adx.Value, adx.PlusDi, adx.MinusDi],
        AroonOscillator aroon => [aroon.AroonUp, aroon.AroonDown, aroon.Value],
        _ => throw new ArgumentOutOfRangeException(nameof(indicator), indicator.GetType().Name, "Unknown indicator type."),
    };
}
