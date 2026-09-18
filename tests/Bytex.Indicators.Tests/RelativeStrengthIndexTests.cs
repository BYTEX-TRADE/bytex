using Bytex.Indicators.Tests.Support;

namespace Bytex.Indicators.Tests;

// Wilder RSI: the first average gain/loss is the simple mean of the first N changes (so N + 1 prices are needed),
// then avg = (avg * (N - 1) + current) / N. RSI = 100 - 100 / (1 + avgGain / avgLoss).
// With no losses in the look-back the engine reports 100, also on a completely flat series
// (the TradingView rule; TA-Lib would say 0 and some libraries 50 for the 0/0 case).
public class RelativeStrengthIndexTests
{
    [Fact]
    public void It_reproduces_the_published_StockCharts_worked_example()
    {
        // ChartSchool "Relative Strength Index", 14-period, values as printed in the spreadsheet (2 decimals).
        decimal[] published = [70.53m, 66.32m, 66.55m, 69.41m, 66.36m, 57.97m, 62.93m, 63.26m, 56.06m, 62.38m, 54.71m];
        RelativeStrengthIndex rsi = new(14);
        List<decimal> actual = [];

        foreach (decimal close in Series.StockChartsCloses)
        {
            rsi.UpdateRaw(close);
            if (rsi.IsInitialized)
            {
                actual.Add(Math.Round(rsi.Value, 2, MidpointRounding.AwayFromZero));
            }
        }

        Assert.Equal(published, actual);
    }

    [Fact]
    public void Values_match_a_hand_computed_table()
    {
        // N = 2, prices 10 11 10 12 -> changes +1 -1 +2
        // after change 2: avgGain 0.5, avgLoss 0.5 -> RS 1 -> RSI 50
        // after change 3: avgGain (0.5 + 2) / 2 = 1.25, avgLoss 0.25 -> RS 5 -> RSI 100 - 100/6 = 83.33..
        RelativeStrengthIndex rsi = new(2);
        rsi.UpdateRaw(10m);
        rsi.UpdateRaw(11m);
        rsi.UpdateRaw(10m);
        Check.Close(50m, rsi.Value);

        rsi.UpdateRaw(12m);
        Check.Close(100m - 100m / 6m, rsi.Value);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(14)]
    public void Values_match_the_naive_reference_on_a_long_series(int period)
    {
        decimal[] closes = Series.Closes;
        decimal?[] want = Reference.RsiWilder(closes, period);
        RelativeStrengthIndex rsi = new(period);

        for (int i = 0; i < closes.Length; i++)
        {
            rsi.UpdateRaw(closes[i]);
            Assert.Equal(want[i].HasValue, rsi.IsInitialized);
            if (want[i] is { } expected)
            {
                Check.Close(expected, rsi.Value, because: $"index {i}");
            }
        }
    }

    [Fact]
    public void It_needs_N_changes_that_is_N_plus_one_prices()
    {
        RelativeStrengthIndex rsi = new(3);
        Assert.False(rsi.HasInputs);

        rsi.UpdateRaw(1m);
        Assert.True(rsi.HasInputs);
        Assert.Equal(0m, rsi.Value); // a single price carries no change yet

        rsi.UpdateRaw(2m);
        rsi.UpdateRaw(3m);
        Assert.False(rsi.IsInitialized);

        rsi.UpdateRaw(4m);
        Assert.True(rsi.IsInitialized);
    }

    [Fact]
    public void A_strictly_rising_series_gives_100()
    {
        RelativeStrengthIndex rsi = new(5);
        for (int i = 1; i <= 20; i++)
        {
            rsi.UpdateRaw(i);
        }

        Assert.Equal(100m, rsi.Value);
    }

    [Fact]
    public void A_strictly_falling_series_gives_0()
    {
        RelativeStrengthIndex rsi = new(5);
        for (int i = 20; i >= 1; i--)
        {
            rsi.UpdateRaw(i);
        }

        Assert.Equal(0m, rsi.Value);
    }

    [Fact]
    public void A_flat_series_gives_100_because_there_are_no_losses()
    {
        RelativeStrengthIndex rsi = new(5);
        for (int i = 0; i < 20; i++)
        {
            rsi.UpdateRaw(77m);
        }

        Assert.True(rsi.IsInitialized);
        Assert.Equal(100m, rsi.Value);
    }

    [Fact]
    public void Equal_gains_and_losses_give_50()
    {
        RelativeStrengthIndex rsi = new(4);
        foreach (decimal v in new[] { 10m, 11m, 10m, 11m, 10m })
        {
            rsi.UpdateRaw(v);
        }

        Check.Close(50m, rsi.Value);
    }

    [Fact]
    public void With_a_period_of_one_only_the_last_change_counts()
    {
        RelativeStrengthIndex rsi = new(1);
        rsi.UpdateRaw(10m);

        rsi.UpdateRaw(11m);
        Assert.True(rsi.IsInitialized);
        Assert.Equal(100m, rsi.Value);

        rsi.UpdateRaw(9m);
        Assert.Equal(0m, rsi.Value);
    }

    [Fact]
    public void Every_value_stays_within_0_and_100()
    {
        RelativeStrengthIndex rsi = new(3);
        foreach (decimal close in Series.Closes)
        {
            rsi.UpdateRaw(close);
            Assert.InRange(rsi.Value, 0m, 100m);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-14)]
    public void A_period_below_one_is_rejected(int period)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RelativeStrengthIndex(period));
    }

    [Fact]
    public void Bars_contribute_their_close()
    {
        RelativeStrengthIndex fromBars = new(3);
        RelativeStrengthIndex fromValues = new(3);

        foreach (Ohlcv bar in Series.Bars.Take(10))
        {
            fromBars.Update(Make.Bar(bar));
            fromValues.UpdateRaw(bar.Close);
        }

        Assert.Equal(fromValues.Value, fromBars.Value);
        Assert.NotEqual(0m, fromBars.Value);
    }

    [Fact]
    public void Reset_forgets_the_previous_price_and_both_averages()
    {
        RelativeStrengthIndex rsi = new(2);
        foreach (decimal v in new[] { 100m, 90m, 80m, 70m })
        {
            rsi.UpdateRaw(v);
        }

        rsi.Reset();

        Assert.False(rsi.HasInputs);
        Assert.False(rsi.IsInitialized);
        Assert.Equal(0m, rsi.Value);

        // Were the old price (70) still remembered, 10 would count as a loss and the result would not be 50.
        foreach (decimal v in new[] { 10m, 11m, 10m })
        {
            rsi.UpdateRaw(v);
        }

        Check.Close(50m, rsi.Value);
    }
}
