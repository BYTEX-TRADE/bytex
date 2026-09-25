using System.Globalization;
using Bytex.Core.Indicators;
using Bytex.Core.Model;

namespace Bytex.Indicators;

/// <summary>
/// Creates the built-in indicators by name ("SMA", "EMA", "RSI", ...) with string parameters.
/// </summary>
public sealed class BuiltinIndicatorFactory : IIndicatorFactory
{
    private static readonly Dictionary<string, Func<IReadOnlyDictionary<string, string>, IIndicator>> Builders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SMA"] = p => new SimpleMovingAverage(Int(p, "period", MovingAverage.DefaultPeriod), PriceTypeOf(p)),
        ["EMA"] = p => new ExponentialMovingAverage(Int(p, "period", MovingAverage.DefaultPeriod), PriceTypeOf(p)),
        ["WMA"] = p => new WeightedMovingAverage(Int(p, "period", MovingAverage.DefaultPeriod), PriceTypeOf(p)),
        ["DEMA"] = p => new DoubleExponentialMovingAverage(Int(p, "period", MovingAverage.DefaultPeriod), PriceTypeOf(p)),
        ["HMA"] = p => new HullMovingAverage(Int(p, "period", MovingAverage.DefaultPeriod), PriceTypeOf(p)),
        ["VWAP"] = _ => new VolumeWeightedAveragePrice(),
        ["RSI"] = p => new RelativeStrengthIndex(Int(p, "period", RelativeStrengthIndex.DefaultPeriod), PriceTypeOf(p)),
        ["MACD"] = p => new MovingAverageConvergenceDivergence(Int(p, "fast", MovingAverageConvergenceDivergence.DefaultFastPeriod), Int(p, "slow", MovingAverageConvergenceDivergence.DefaultSlowPeriod), Int(p, "signal", MovingAverageConvergenceDivergence.DefaultSignalPeriod), PriceTypeOf(p)),
        ["ROC"] = p => new RateOfChange(Int(p, "period", RateOfChange.DefaultPeriod), PriceTypeOf(p)),
        ["STOCH"] = p => new Stochastics(Int(p, "k", Stochastics.DefaultKPeriod), Int(p, "d", Stochastics.DefaultDPeriod)),
        ["OBV"] = _ => new OnBalanceVolume(),
        ["ATR"] = p => new AverageTrueRange(Int(p, "period", AverageTrueRange.DefaultPeriod)),
        ["BB"] = p => new BollingerBands(Int(p, "period", BollingerBands.DefaultPeriod), Dec(p, "k", BollingerBands.DefaultBandWidth), PriceTypeOf(p)),
        ["KC"] = p => new KeltnerChannel(Int(p, "period", KeltnerChannel.DefaultPeriod), Dec(p, "k", KeltnerChannel.DefaultBandWidth), Int(p, "atr", KeltnerChannel.DefaultAtrPeriod)),
        ["DC"] = p => new DonchianChannel(Int(p, "period", DonchianChannel.DefaultPeriod), Bool(p, "excludeCurrent", false)),
        ["ADX"] = p => new AverageDirectionalIndex(Int(p, "period", AverageDirectionalIndex.DefaultPeriod)),
        ["AROON"] = p => new AroonOscillator(Int(p, "period", AroonOscillator.DefaultPeriod)),
    };

    public IReadOnlyList<string> Names => Builders.Keys.ToList();

    public IIndicator Create(string name, IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!Builders.TryGetValue(name, out Func<IReadOnlyDictionary<string, string>, IIndicator>? builder))
        {
            throw new ArgumentException($"Unknown indicator '{name}'.", nameof(name));
        }

        return builder(parameters ?? new Dictionary<string, string>());
    }

    private static int Int(IReadOnlyDictionary<string, string> p, string key, int fallback) =>
        p.TryGetValue(key, out string? v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : fallback;

    private static bool Bool(IReadOnlyDictionary<string, string> p, string key, bool fallback) =>
        p.TryGetValue(key, out string? v) && bool.TryParse(v, out bool b) ? b : fallback;

    private static decimal Dec(IReadOnlyDictionary<string, string> p, string key, decimal fallback) =>
        p.TryGetValue(key, out string? v) && decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal d) ? d : fallback;

    private static PriceType PriceTypeOf(IReadOnlyDictionary<string, string> p) =>
        p.TryGetValue("priceType", out string? v) && Enum.TryParse(v, true, out PriceType t) ? t : PriceType.Last;
}
