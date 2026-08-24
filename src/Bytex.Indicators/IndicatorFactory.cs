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
        ["SMA"] = p => new SimpleMovingAverage(Int(p, "period", 20), PriceTypeOf(p)),
        ["EMA"] = p => new ExponentialMovingAverage(Int(p, "period", 20), PriceTypeOf(p)),
        ["WMA"] = p => new WeightedMovingAverage(Int(p, "period", 20), PriceTypeOf(p)),
        ["DEMA"] = p => new DoubleExponentialMovingAverage(Int(p, "period", 20), PriceTypeOf(p)),
        ["HMA"] = p => new HullMovingAverage(Int(p, "period", 20), PriceTypeOf(p)),
        ["VWAP"] = _ => new VolumeWeightedAveragePrice(),
        ["RSI"] = p => new RelativeStrengthIndex(Int(p, "period", 14), PriceTypeOf(p)),
        ["MACD"] = p => new MovingAverageConvergenceDivergence(Int(p, "fast", 12), Int(p, "slow", 26), Int(p, "signal", 9), PriceTypeOf(p)),
        ["ROC"] = p => new RateOfChange(Int(p, "period", 10), PriceTypeOf(p)),
        ["STOCH"] = p => new Stochastics(Int(p, "k", 14), Int(p, "d", 3)),
        ["OBV"] = _ => new OnBalanceVolume(),
        ["ATR"] = p => new AverageTrueRange(Int(p, "period", 14)),
        ["BB"] = p => new BollingerBands(Int(p, "period", 20), Dec(p, "k", 2m), PriceTypeOf(p)),
        ["KC"] = p => new KeltnerChannel(Int(p, "period", 20), Dec(p, "k", 2m), Int(p, "atr", 10)),
        ["DC"] = p => new DonchianChannel(Int(p, "period", 20)),
        ["ADX"] = p => new AverageDirectionalIndex(Int(p, "period", 14)),
        ["AROON"] = p => new AroonOscillator(Int(p, "period", 25)),
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

    private static decimal Dec(IReadOnlyDictionary<string, string> p, string key, decimal fallback) =>
        p.TryGetValue(key, out string? v) && decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal d) ? d : fallback;

    private static PriceType PriceTypeOf(IReadOnlyDictionary<string, string> p) =>
        p.TryGetValue("priceType", out string? v) && Enum.TryParse(v, true, out PriceType t) ? t : PriceType.Last;
}
