using Bytex.Indicators.Tests.Support;

namespace Bytex.Indicators.Tests;

// Wilder ATR: TR = max(H - L, |H - prevClose|, |L - prevClose|), the first bar's TR is H - L;
// ATR_N = simple mean of the first N true ranges, afterwards (ATR * (N - 1) + TR) / N.
// Before N bars the engine reports the running mean of the true ranges seen so far.
public class AverageTrueRangeTests
{
    [Fact]
    public void Values_match_a_hand_computed_table_including_gaps()
    {
        // N = 3
        // H   L   C     TR                                   ATR
        // 10  8   9     2                                    2           (running mean)
        // 11  9   10    max(2, |11-9|, |9-9|)      = 2       2
        // 15  13  14    max(2, |15-10|, |13-10|)   = 5       (2+2+5)/3 = 3          gap up
        // 14  12  13    max(2, |14-14|, |12-14|)   = 2       (3*2 + 2)/3 = 8/3
        // 9   8   8.5   max(1, |9-13|, |8-13|)     = 5       (8/3*2 + 5)/3 = 31/9   gap down
        AverageTrueRange atr = new(3);
        (decimal High, decimal Low, decimal Close, decimal Expected)[] rows =
        [
            (10m, 8m, 9m, 2m),
            (11m, 9m, 10m, 2m),
            (15m, 13m, 14m, 3m),
            (14m, 12m, 13m, 8m / 3m),
            (9m, 8m, 8.5m, 31m / 9m),
        ];

        foreach ((decimal high, decimal low, decimal close, decimal expected) in rows)
        {
            atr.Update(Make.Bar(high, low, close));
            Check.Close(expected, atr.Value);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(14)]
    public void Values_match_the_naive_reference_on_the_bar_series(int period)
    {
        decimal?[] want = Reference.AtrWilder(Series.Bars, period);
        AverageTrueRange atr = new(period);

        for (int i = 0; i < Series.Bars.Length; i++)
        {
            atr.Update(Make.Bar(Series.Bars[i]));
            Assert.Equal(want[i].HasValue, atr.IsInitialized);
            if (want[i] is { } expected)
            {
                Check.Close(expected, atr.Value, because: $"index {i}");
            }
        }
    }

    [Fact]
    public void It_becomes_initialized_on_exactly_the_Nth_bar()
    {
        AverageTrueRange atr = new(4);
        Assert.False(atr.HasInputs);

        for (int i = 1; i <= 3; i++)
        {
            atr.Update(Make.Bar(high: 11m, low: 9m, close: 10m));
            Assert.True(atr.HasInputs);
            Assert.False(atr.IsInitialized, $"after {i} bars");
        }

        atr.Update(Make.Bar(high: 11m, low: 9m, close: 10m));
        Assert.True(atr.IsInitialized);
    }

    [Fact]
    public void With_a_period_of_one_it_is_the_true_range_itself()
    {
        AverageTrueRange atr = new(1);

        atr.Update(Make.Bar(high: 10m, low: 9m, close: 9.5m));
        Assert.Equal(1m, atr.Value);

        atr.Update(Make.Bar(high: 20m, low: 19.5m, close: 20m)); // gap: |20 - 9.5|
        Assert.Equal(10.5m, atr.Value);
    }

    [Fact]
    public void Bars_without_any_range_give_zero()
    {
        AverageTrueRange atr = new(5);
        for (int i = 0; i < 12; i++)
        {
            atr.Update(Make.Bar(high: 50m, low: 50m, close: 50m));
        }

        Assert.True(atr.IsInitialized);
        Assert.Equal(0m, atr.Value);
    }

    [Fact]
    public void A_constant_true_range_is_reproduced_exactly()
    {
        AverageTrueRange atr = new(7);
        for (int i = 0; i < 30; i++)
        {
            atr.Update(Make.Bar(high: 101.5m, low: 100m, close: 100.75m));
        }

        Check.Close(1.5m, atr.Value);
    }

    [Fact]
    public void Raw_values_measure_the_absolute_change_between_inputs()
    {
        AverageTrueRange atr = new(2);

        atr.UpdateRaw(100m); // TR 0
        atr.UpdateRaw(104m); // TR 4 -> mean 2
        Assert.Equal(2m, atr.Value);

        atr.UpdateRaw(101m); // TR 3 -> (2 * 1 + 3) / 2
        Assert.Equal(2.5m, atr.Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-14)]
    public void A_period_below_one_is_rejected(int period)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AverageTrueRange(period));
    }

    [Fact]
    public void Reset_forgets_the_previous_close()
    {
        AverageTrueRange atr = new(2);
        atr.Update(Make.Bar(high: 1001m, low: 999m, close: 1000m));
        atr.Update(Make.Bar(high: 1001m, low: 999m, close: 1000m));

        atr.Reset();

        Assert.False(atr.HasInputs);
        Assert.False(atr.IsInitialized);
        Assert.Equal(0m, atr.Value);

        // With the old close (1000) still in memory this bar's true range would be 991, not 2.
        atr.Update(Make.Bar(high: 11m, low: 9m, close: 10m));
        Assert.Equal(2m, atr.Value);
    }
}
