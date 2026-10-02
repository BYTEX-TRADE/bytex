using Bytex.Indicators.Tests.Support;

namespace Bytex.Indicators.Tests;

// DEMA (Mulloy, 1994): 2 * EMA(x) - EMA(EMA(x)). Both EMAs follow the engine's EMA convention (first-value seed),
// so there is no undefined prefix; the engine raises IsInitialized once N inputs have been seen.
public class DoubleExponentialMovingAverageTests
{
    [Fact]
    public void Values_match_a_hand_computed_table()
    {
        // N = 3, alpha = 0.5
        // x     2   4    8
        // EMA1  2   3    5.5
        // EMA2  2   2.5  4
        // DEMA  2   3.5  7
        DoubleExponentialMovingAverage dema = new(3);
        decimal[] want = [2m, 3.5m, 7m];
        decimal[] inputs = [2m, 4m, 8m];

        for (int i = 0; i < inputs.Length; i++)
        {
            dema.UpdateRaw(inputs[i]);
            Assert.Equal(want[i], dema.Value);
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(20)]
    public void Values_match_the_naive_reference_on_a_long_series(int period)
    {
        decimal[] closes = Series.Closes;
        decimal[] want = Reference.Dema(closes, period);
        DoubleExponentialMovingAverage dema = new(period);

        for (int i = 0; i < closes.Length; i++)
        {
            dema.UpdateRaw(closes[i]);
            Check.Close(want[i], dema.Value, because: $"index {i}");
        }
    }

    [Fact]
    public void It_tracks_a_steady_trend_more_closely_than_the_plain_EMA()
    {
        // The whole point of DEMA: the EMA lag on a linear ramp is cancelled.
        DoubleExponentialMovingAverage dema = new(10);
        ExponentialMovingAverage ema = new(10);
        for (int i = 1; i <= 200; i++)
        {
            dema.UpdateRaw(i);
            ema.UpdateRaw(i);
        }

        Assert.True(Math.Abs(200m - dema.Value) < 0.000001m, $"DEMA lag {200m - dema.Value}");
        Check.Close(4.5m, 200m - ema.Value, 0.000001m); // steady-state EMA lag on a unit ramp is (1 - alpha) / alpha = (N - 1) / 2
    }

    [Fact]
    public void It_becomes_initialized_on_exactly_the_Nth_input()
    {
        DoubleExponentialMovingAverage dema = new(4);

        for (int i = 1; i <= 3; i++)
        {
            dema.UpdateRaw(i);
            Assert.True(dema.HasInputs);
            Assert.False(dema.IsInitialized, $"after {i} inputs");
        }

        dema.UpdateRaw(4m);
        Assert.True(dema.IsInitialized);
        Assert.Equal(4, dema.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_period_below_one_is_rejected(int period)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DoubleExponentialMovingAverage(period));
    }

    [Fact]
    public void Reset_clears_both_inner_averages()
    {
        DoubleExponentialMovingAverage dema = new(3);
        dema.UpdateRaw(900m);
        dema.UpdateRaw(100m);

        dema.Reset();

        Assert.False(dema.HasInputs);
        Assert.False(dema.IsInitialized);
        Assert.Equal(0m, dema.Value);

        // If either inner EMA had kept its state the first value after a reset would not be the input itself.
        dema.UpdateRaw(2m);
        Assert.Equal(2m, dema.Value);
        dema.UpdateRaw(4m);
        Assert.Equal(3.5m, dema.Value);
    }
}
