using Bytex.Core.Indicators;
using Bytex.Core.Model.Primitives;
using Bytex.Indicators.Tests.Support;

namespace Bytex.Indicators.Tests;

// The IIndicator contract that strategies rely on, checked uniformly for every built-in:
// nothing is reported before the first input, the warm-up flag flips on a known bar,
// and Reset() returns the indicator to a state from which a replay reproduces the first run exactly.
public class IndicatorContractTests
{
    private static readonly BuiltinIndicatorFactory Factory = new();

    public static TheoryData<string> AllNames() => new(Factory.Names.Order(StringComparer.Ordinal));

    [Theory]
    [MemberData(nameof(AllNames))]
    public void A_new_indicator_has_no_inputs_is_not_initialized_and_reports_zero(string name)
    {
        IIndicator indicator = Factory.Create(name, new Dictionary<string, string>());

        Assert.False(indicator.HasInputs);
        Assert.False(indicator.IsInitialized);
        Assert.All(Check.Outputs(indicator), value => Assert.Equal(0m, value));
    }

    [Theory]
    [MemberData(nameof(AllNames))]
    public void The_first_bar_sets_HasInputs(string name)
    {
        IIndicator indicator = Factory.Create(name, new Dictionary<string, string>());

        indicator.Update(Make.Bar(Series.Bars[0]));

        Assert.True(indicator.HasInputs);
    }

    [Theory]
    [MemberData(nameof(AllNames))]
    public void After_Reset_a_replay_reproduces_the_first_run_bar_for_bar(string name)
    {
        IIndicator indicator = Factory.Create(name, new Dictionary<string, string> { ["period"] = "6", ["fast"] = "3", ["slow"] = "6", ["signal"] = "4", ["k"] = "5", ["d"] = "3", ["atr"] = "4" });

        List<(bool Initialized, decimal[] Values)> first = Run(indicator);
        indicator.Reset();

        Assert.False(indicator.HasInputs);
        Assert.False(indicator.IsInitialized);
        Assert.All(Check.Outputs(indicator), value => Assert.Equal(0m, value));

        List<(bool Initialized, decimal[] Values)> second = Run(indicator);

        Assert.True(first[^1].Initialized, "the series is long enough to warm every indicator up");
        for (int i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].Initialized, second[i].Initialized);
            Assert.Equal(first[i].Values, second[i].Values);
        }
    }

    [Theory]
    // Windowed indicators: the window must be full.
    [InlineData("SMA", "period=5", 5)]
    [InlineData("WMA", "period=5", 5)]
    [InlineData("BB", "period=5", 5)]
    [InlineData("DC", "period=5", 5)]
    [InlineData("ATR", "period=5", 5)]
    // One extra bar, because the first one only provides the reference price.
    [InlineData("RSI", "period=5", 6)]
    [InlineData("ROC", "period=5", 6)]
    [InlineData("AROON", "period=5", 6)]
    // Cumulative indicators are usable from the first bar.
    [InlineData("VWAP", "", 1)]
    [InlineData("OBV", "", 1)]
    // First-value-seeded EMA family: the engine's rule is "the longest period has been seen".
    [InlineData("EMA", "period=5", 5)]
    [InlineData("DEMA", "period=5", 5)]
    [InlineData("MACD", "fast=3;slow=6;signal=4", 6)]
    [InlineData("KC", "period=5;atr=3", 5)]
    [InlineData("KC", "period=3;atr=5", 5)]
    // %D with a period of 1 is %K itself, so nothing beyond the %K window is needed.
    [InlineData("STOCH", "k=5;d=1", 5)]
    public void IsInitialized_flips_on_exactly_the_expected_bar(string name, string parameters, int expectedBars)
    {
        IIndicator indicator = Factory.Create(name, Parse(parameters));

        for (int i = 0; i < expectedBars - 1; i++)
        {
            indicator.Update(Make.Bar(Series.Bars[i]));
            Assert.False(indicator.IsInitialized, $"{indicator.Name} after {i + 1} bars");
        }

        indicator.Update(Make.Bar(Series.Bars[expectedBars - 1]));
        Assert.True(indicator.IsInitialized, $"{indicator.Name} after {expectedBars} bars");
    }

    [Theory]
    [MemberData(nameof(AllNames))]
    public void Once_initialized_it_stays_initialized(string name)
    {
        IIndicator indicator = Factory.Create(name, new Dictionary<string, string> { ["period"] = "4", ["slow"] = "8" });
        bool seen = false;

        foreach (Ohlcv bar in Series.Bars)
        {
            indicator.Update(Make.Bar(bar));
            Assert.False(seen && !indicator.IsInitialized, $"{indicator.Name} fell back to uninitialized");
            seen |= indicator.IsInitialized;
        }

        Assert.True(seen);
    }

    private static List<(bool Initialized, decimal[] Values)> Run(IIndicator indicator)
    {
        List<(bool, decimal[])> states = [];
        for (int i = 0; i < Series.Bars.Length; i++)
        {
            indicator.Update(Make.Bar(Series.Bars[i], Make.Epoch2024.AddNanos(i * UnixNanos.NanosPerHour)));
            states.Add((indicator.IsInitialized, Check.Outputs(indicator)));
        }

        return states;
    }

    private static Dictionary<string, string> Parse(string text) =>
        text.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('='))
            .ToDictionary(kv => kv[0], kv => kv[1], StringComparer.Ordinal);
}
