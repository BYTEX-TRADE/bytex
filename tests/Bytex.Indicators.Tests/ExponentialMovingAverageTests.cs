using Bytex.Core.Model;
using Bytex.Indicators.Tests.Support;

namespace Bytex.Indicators.Tests;

// EMA convention used by the engine: alpha = 2 / (N + 1), seeded with the FIRST input (pandas ewm(adjust=False)),
// not with the SMA of the first N inputs. With that seed every value is defined from the first input on;
// IsInitialized only says that N inputs have been seen.
public class ExponentialMovingAverageTests
{
    [Theory]
    [InlineData(1, "1")]
    [InlineData(3, "0.5")]
    [InlineData(9, "0.2")]
    [InlineData(19, "0.1")]
    public void Alpha_is_two_over_period_plus_one(int period, string expected)
    {
        Assert.Equal(Series.Parse(expected)[0], new ExponentialMovingAverage(period).Alpha);
    }

    [Theory]
    // alpha 0.5: 2 -> (4+2)/2 = 3 -> (8+3)/2 = 5.5 -> (16+5.5)/2 = 10.75
    [InlineData(3, "2 4 8 16", "2 3 5.5 10.75")]
    // alpha 0.2: 10 -> 0.2*20 + 0.8*10 = 12 -> 0.2*0 + 0.8*12 = 9.6 -> 0.2*9.6 + 0.8*9.6 = 9.6
    [InlineData(9, "10 20 0 9.6", "10 12 9.6 9.6")]
    public void Values_match_a_hand_computed_table(int period, string inputs, string expected)
    {
        ExponentialMovingAverage ema = new(period);
        decimal[] values = Series.Parse(inputs);
        decimal[] want = Series.Parse(expected);

        for (int i = 0; i < values.Length; i++)
        {
            ema.UpdateRaw(values[i]);
            Check.Close(want[i], ema.Value, because: $"input #{i + 1}");
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(10)]
    [InlineData(26)]
    public void Values_match_the_naive_reference_on_a_long_series(int period)
    {
        decimal[] closes = Series.Closes;
        decimal[] want = Reference.EmaSeededWithFirstValue(closes, period);
        ExponentialMovingAverage ema = new(period);

        for (int i = 0; i < closes.Length; i++)
        {
            ema.UpdateRaw(closes[i]);
            Check.Close(want[i], ema.Value, because: $"index {i}");
        }
    }

    [Fact]
    public void It_becomes_initialized_on_exactly_the_Nth_input()
    {
        ExponentialMovingAverage ema = new(5);

        for (int i = 1; i <= 4; i++)
        {
            ema.UpdateRaw(i);
            Assert.True(ema.HasInputs);
            Assert.False(ema.IsInitialized, $"after {i} inputs");
        }

        ema.UpdateRaw(5m);

        Assert.True(ema.IsInitialized);
        Assert.Equal(5, ema.Count);
    }

    [Fact]
    public void A_period_of_one_echoes_the_input()
    {
        ExponentialMovingAverage ema = new(1);

        ema.UpdateRaw(5m);
        ema.UpdateRaw(9m);

        Assert.True(ema.IsInitialized);
        Assert.Equal(9m, ema.Value);
    }

    [Fact]
    public void A_constant_series_stays_on_the_constant()
    {
        ExponentialMovingAverage ema = new(12);
        for (int i = 0; i < 50; i++)
        {
            ema.UpdateRaw(42.42m);
        }

        Check.Close(42.42m, ema.Value);
    }

    [Fact]
    public void On_a_rising_series_it_lags_below_the_input_and_above_the_longer_average()
    {
        ExponentialMovingAverage fast = new(3);
        ExponentialMovingAverage slow = new(10);

        for (int i = 1; i <= 40; i++)
        {
            fast.UpdateRaw(i);
            slow.UpdateRaw(i);
        }

        Assert.True(fast.Value < 40m);
        Assert.True(slow.Value < fast.Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_period_below_one_is_rejected(int period)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExponentialMovingAverage(period));
    }

    [Fact]
    public void Quotes_contribute_the_configured_side()
    {
        ExponentialMovingAverage ema = new(3, PriceType.Ask);

        ema.Update(Make.Quote(bid: 10m, ask: 12m));
        ema.Update(Make.Quote(bid: 10m, ask: 16m));

        Assert.Equal(14m, ema.Value); // 0.5 * 16 + 0.5 * 12
    }

    [Fact]
    public void Reset_makes_the_next_input_the_new_seed()
    {
        ExponentialMovingAverage ema = new(3);
        ema.UpdateRaw(1000m);
        ema.UpdateRaw(2000m);

        ema.Reset();

        Assert.False(ema.HasInputs);
        Assert.False(ema.IsInitialized);
        Assert.Equal(0m, ema.Value);
        Assert.Equal(0, ema.Count);

        ema.UpdateRaw(8m);
        Assert.Equal(8m, ema.Value);
    }
}
