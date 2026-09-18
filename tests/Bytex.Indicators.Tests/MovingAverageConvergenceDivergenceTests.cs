using Bytex.Indicators.Tests.Support;

namespace Bytex.Indicators.Tests;

// MACD (Appel): line = EMA(fast) - EMA(slow), signal = EMA(signal) of the line, histogram = line - signal.
// All three EMAs follow the engine's first-value seed, so the line starts at exactly 0 and nothing is undefined;
// the engine raises IsInitialized once the longest of the three periods has been seen.
public class MovingAverageConvergenceDivergenceTests
{
    [Fact]
    public void Values_match_a_hand_computed_table()
    {
        // fast 1 (alpha 1), slow 3 (alpha 0.5), signal 3 (alpha 0.5)
        // x        2    4    8     4
        // fast     2    4    8     4
        // slow     2    3    5.5   4.75
        // line     0    1    2.5  -0.75
        // signal   0    0.5  1.5   0.375
        // hist     0    0.5  1    -1.125
        MovingAverageConvergenceDivergence macd = new(1, 3, 3);
        decimal[] inputs = [2m, 4m, 8m, 4m];
        decimal[] line = [0m, 1m, 2.5m, -0.75m];
        decimal[] signal = [0m, 0.5m, 1.5m, 0.375m];
        decimal[] histogram = [0m, 0.5m, 1m, -1.125m];

        for (int i = 0; i < inputs.Length; i++)
        {
            macd.UpdateRaw(inputs[i]);
            Assert.Equal(line[i], macd.Value);
            Assert.Equal(signal[i], macd.Signal);
            Assert.Equal(histogram[i], macd.Histogram);
        }
    }

    [Theory]
    [InlineData(12, 26, 9)]
    [InlineData(3, 10, 16)]
    [InlineData(5, 8, 1)]
    public void Values_match_the_naive_reference_on_a_long_series(int fast, int slow, int signal)
    {
        decimal[] closes = Series.Closes;
        (decimal[] line, decimal[] signalLine) = Reference.Macd(closes, fast, slow, signal);
        MovingAverageConvergenceDivergence macd = new(fast, slow, signal);

        for (int i = 0; i < closes.Length; i++)
        {
            macd.UpdateRaw(closes[i]);
            Check.Close(line[i], macd.Value, because: $"line, index {i}");
            Check.Close(signalLine[i], macd.Signal, because: $"signal, index {i}");
            Check.Close(line[i] - signalLine[i], macd.Histogram, because: $"histogram, index {i}");
        }
    }

    [Theory]
    [InlineData(3, 6, 4, 6)]
    [InlineData(2, 4, 9, 9)]
    [InlineData(12, 26, 9, 26)]
    public void It_becomes_initialized_when_the_longest_period_has_been_seen(int fast, int slow, int signal, int expectedInputs)
    {
        MovingAverageConvergenceDivergence macd = new(fast, slow, signal);

        for (int i = 1; i < expectedInputs; i++)
        {
            macd.UpdateRaw(100m + i);
            Assert.True(macd.HasInputs);
            Assert.False(macd.IsInitialized, $"after {i} inputs");
        }

        macd.UpdateRaw(1m);
        Assert.True(macd.IsInitialized);
    }

    [Fact]
    public void A_constant_series_gives_zero_everywhere()
    {
        MovingAverageConvergenceDivergence macd = new();
        for (int i = 0; i < 60; i++)
        {
            macd.UpdateRaw(250m);
        }

        Check.Close(0m, macd.Value);
        Check.Close(0m, macd.Signal);
        Check.Close(0m, macd.Histogram);
    }

    [Fact]
    public void The_line_is_positive_in_a_rally_and_negative_in_a_sell_off()
    {
        MovingAverageConvergenceDivergence macd = new(3, 6, 4);
        for (int i = 1; i <= 30; i++)
        {
            macd.UpdateRaw(100m + i);
        }

        Assert.True(macd.Value > 0m);

        for (int i = 1; i <= 30; i++)
        {
            macd.UpdateRaw(130m - 2m * i);
        }

        Assert.True(macd.Value < 0m);
    }

    [Fact]
    public void Defaults_are_the_classic_12_26_9()
    {
        Assert.Equal("MACD(12,26,9)", new MovingAverageConvergenceDivergence().Name);
    }

    [Theory]
    [InlineData(0, 26, 9)]
    [InlineData(12, 0, 9)]
    [InlineData(12, 26, 0)]
    [InlineData(-1, 26, 9)]
    public void A_period_below_one_is_rejected(int fast, int slow, int signal)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MovingAverageConvergenceDivergence(fast, slow, signal));
    }

    [Fact]
    public void Reset_clears_all_three_averages()
    {
        MovingAverageConvergenceDivergence macd = new(1, 3, 3);
        foreach (decimal v in new[] { 500m, 900m, 100m })
        {
            macd.UpdateRaw(v);
        }

        macd.Reset();

        Assert.False(macd.HasInputs);
        Assert.False(macd.IsInitialized);
        Assert.Equal(0m, macd.Value);
        Assert.Equal(0m, macd.Signal);

        macd.UpdateRaw(2m);
        macd.UpdateRaw(4m);
        Assert.Equal(1m, macd.Value);
        Assert.Equal(0.5m, macd.Signal);
    }
}
