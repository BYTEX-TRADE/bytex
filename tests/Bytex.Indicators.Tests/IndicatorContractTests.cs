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

    /// <summary>
    /// Periods small enough that the shared forty-bar series warms every indicator up, under every name the built-ins
    /// use for one. An indicator ignores the keys it does not know, so one dictionary serves them all - and an indicator
    /// whose warm-up is longer than the series would otherwise never be initialized and never be checked.
    /// </summary>
    private static Dictionary<string, string> Small() => new(StringComparer.Ordinal)
    {
        ["period"] = "6",
        ["fast"] = "3",
        ["slow"] = "6",
        ["signal"] = "4",
        ["k"] = "5",
        ["d"] = "3",
        ["atr"] = "4",
        ["conversion"] = "2",
        ["base"] = "3",
        ["spanB"] = "4",
        ["displacement"] = "2",
        ["levels"] = "1",
    };

    /// <summary>
    /// The indicators a bar can be fed to, which is every one except those that read the book.
    ///
    /// <para>
    /// A book indicator is not in this list because it refuses a bar rather than measuring nothing from it (R7.8), and a
    /// contract test that fed it one would be asserting the wrong contract. Its own is below.
    /// </para>
    /// </summary>
    public static TheoryData<string> AllNames() => new(Factory.Names
        .Where(name => Factory.Create(name, new Dictionary<string, string>()) is not IOrderBookIndicator)
        .Order(StringComparer.Ordinal));

    public static TheoryData<string> BookNames() => new(Factory.Names
        .Where(name => Factory.Create(name, new Dictionary<string, string>()) is IOrderBookIndicator)
        .Order(StringComparer.Ordinal));

    [Theory]
    [MemberData(nameof(BookNames))]
    public void A_book_indicator_reports_nothing_until_it_has_seen_a_book(string name)
    {
        IOrderBookIndicator indicator = (IOrderBookIndicator)Factory.Create(name, new Dictionary<string, string>());

        Assert.False(indicator.HasInputs);
        Assert.All(Check.Outputs(indicator), value => Assert.Equal(0m, value));

        indicator.Update(Make.Book([(100m, 3m)], [(101m, 1m)]));

        Assert.True(indicator.HasInputs);
        Assert.True(indicator.IsInitialized);
    }

    [Theory]
    [MemberData(nameof(BookNames))]
    public void A_book_indicator_refuses_what_it_cannot_measure(string name)
    {
        // A bar and a trade carry no resting size. Accepting them would leave an indicator that reads as ready and means
        // nothing, which is worse than refusing.
        IOrderBookIndicator indicator = (IOrderBookIndicator)Factory.Create(name, new Dictionary<string, string>());

        Assert.Throws<NotSupportedException>(() => indicator.Update(Make.Bar(1m, 1m, 1m)));
        Assert.Throws<NotSupportedException>(() => indicator.Update(Make.Trade(1m)));
    }

    [Theory]
    [MemberData(nameof(BookNames))]
    public void After_Reset_a_book_indicator_is_as_new(string name)
    {
        IOrderBookIndicator indicator = (IOrderBookIndicator)Factory.Create(name, new Dictionary<string, string>());
        indicator.Update(Make.Book([(100m, 3m)], [(101m, 1m)]));

        indicator.Reset();

        Assert.False(indicator.HasInputs);
        Assert.False(indicator.IsInitialized);
        Assert.All(Check.Outputs(indicator), value => Assert.Equal(0m, value));
    }

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
        IIndicator indicator = Factory.Create(name, Small());

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
        IIndicator indicator = Factory.Create(name, Small());
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
