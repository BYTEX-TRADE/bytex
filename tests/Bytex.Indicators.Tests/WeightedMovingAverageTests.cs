using Bytex.Indicators.Tests.Support;

namespace Bytex.Indicators.Tests;

// WMA: linear weights 1..N, the newest input weighs N, divisor N(N+1)/2.
// Before N inputs the engine applies the same rule to the k inputs it has (weights 1..k).
public class WeightedMovingAverageTests
{
    [Theory]
    // full windows: (1*1 + 2*2 + 3*3) / 6 = 14/6, (2 + 6 + 12) / 6 = 20/6; partial: 1, (1 + 4) / 3
    [InlineData(3, "1 2 3 4", "1 1.6666666666666666666666666667 2.3333333333333333333333333333 3.3333333333333333333333333333")]
    // (10*1 + 20*2) / 3 = 16.67, (20 + 2*5) / 3 = 10
    [InlineData(2, "10 20 5", "10 16.666666666666666666666666667 10")]
    public void Values_match_a_hand_computed_table(int period, string inputs, string expected)
    {
        WeightedMovingAverage wma = new(period);
        decimal[] values = Series.Parse(inputs);
        decimal[] want = Series.Parse(expected);

        for (int i = 0; i < values.Length; i++)
        {
            wma.UpdateRaw(values[i]);
            Check.Close(want[i], wma.Value, because: $"input #{i + 1}");
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(16)]
    public void Values_match_the_naive_reference_on_a_long_series(int period)
    {
        decimal[] closes = Series.Closes;
        decimal?[] want = Reference.Wma(closes, period);
        WeightedMovingAverage wma = new(period);

        for (int i = 0; i < closes.Length; i++)
        {
            wma.UpdateRaw(closes[i]);
            if (want[i] is { } expected)
            {
                Check.Close(expected, wma.Value, because: $"index {i}");
            }
        }
    }

    [Fact]
    public void The_newest_value_weighs_the_most()
    {
        WeightedMovingAverage rising = new(3);
        WeightedMovingAverage falling = new(3);
        foreach (decimal v in new[] { 1m, 2m, 9m })
        {
            rising.UpdateRaw(v);
        }

        foreach (decimal v in new[] { 9m, 2m, 1m })
        {
            falling.UpdateRaw(v);
        }

        // Same three numbers, same simple mean (4); only the ordering differs.
        Check.Close(32m / 6m, rising.Value);
        Check.Close(16m / 6m, falling.Value);
    }

    [Fact]
    public void It_becomes_initialized_on_exactly_the_Nth_input()
    {
        WeightedMovingAverage wma = new(3);

        wma.UpdateRaw(1m);
        wma.UpdateRaw(2m);
        Assert.True(wma.HasInputs);
        Assert.False(wma.IsInitialized);

        wma.UpdateRaw(3m);
        Assert.True(wma.IsInitialized);
    }

    [Fact]
    public void A_period_of_one_echoes_the_input()
    {
        WeightedMovingAverage wma = new(1);
        wma.UpdateRaw(3m);
        wma.UpdateRaw(11m);

        Assert.Equal(11m, wma.Value);
    }

    [Fact]
    public void A_constant_series_averages_to_the_constant()
    {
        WeightedMovingAverage wma = new(6);
        for (int i = 0; i < 15; i++)
        {
            wma.UpdateRaw(3.3m);
        }

        Check.Close(3.3m, wma.Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void A_period_below_one_is_rejected(int period)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WeightedMovingAverage(period));
    }

    [Fact]
    public void Reset_forgets_the_window()
    {
        WeightedMovingAverage wma = new(2);
        wma.UpdateRaw(500m);
        wma.UpdateRaw(700m);

        wma.Reset();

        Assert.False(wma.HasInputs);
        Assert.False(wma.IsInitialized);
        Assert.Equal(0m, wma.Value);

        wma.UpdateRaw(4m);
        Assert.Equal(4m, wma.Value);
    }
}
