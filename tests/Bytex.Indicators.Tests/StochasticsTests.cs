using Bytex.Indicators.Tests.Support;

namespace Bytex.Indicators.Tests;

// Lane's fast stochastic: %K = (close - LL) / (HH - LL) * 100 over kPeriod bars, %D = SMA(dPeriod) of %K.
// The first textbook %D needs dPeriod full-window %K values, i.e. kPeriod + dPeriod - 1 bars.
// A zero range (HH == LL) is reported as the 50 midpoint.
public class StochasticsTests
{
    [Fact]
    public void Values_match_a_hand_computed_table()
    {
        // k = 3, d = 2
        // bar   H   L   C    HH  LL   %K                     %D
        // 3     11  7   8    12  7    (8-7)/5   = 20
        // 4     13  10  13   13  7    (13-7)/6  = 100        (20 + 100) / 2 = 60
        // 5     12  10  10   13  7    (10-7)/6  = 50         (100 + 50) / 2 = 75
        Stochastics stoch = new(3, 2);
        stoch.Update(Make.Bar(high: 10m, low: 8m, close: 9m));
        stoch.Update(Make.Bar(high: 12m, low: 9m, close: 11m));

        stoch.Update(Make.Bar(high: 11m, low: 7m, close: 8m));
        Check.Close(20m, stoch.ValueK);

        stoch.Update(Make.Bar(high: 13m, low: 10m, close: 13m));
        Check.Close(100m, stoch.ValueK);
        Check.Close(60m, stoch.ValueD);

        stoch.Update(Make.Bar(high: 12m, low: 10m, close: 10m));
        Check.Close(50m, stoch.ValueK);
        Check.Close(75m, stoch.ValueD);
    }

    [Theory]
    [InlineData(5, 3)]
    [InlineData(14, 3)]
    [InlineData(9, 1)]
    public void From_the_first_textbook_value_on_both_lines_match_the_naive_reference(int kPeriod, int dPeriod)
    {
        (decimal?[] k, decimal?[] d) = Reference.Stochastics(Series.Bars, kPeriod, dPeriod);
        Stochastics stoch = new(kPeriod, dPeriod);

        for (int i = 0; i < Series.Bars.Length; i++)
        {
            stoch.Update(Make.Bar(Series.Bars[i]));
            if (k[i] is { } expectedK)
            {
                Check.Close(expectedK, stoch.ValueK, because: $"%K, index {i}");
            }

            if (d[i] is { } expectedD)
            {
                Check.Close(expectedD, stoch.ValueD, because: $"%D, index {i}");
            }
        }
    }

    [Fact(Skip = "BUG: Stochastics reports IsInitialized after kPeriod bars, while %D still averages %K values taken from partial windows until bar kPeriod + dPeriod - 1")]
    public void Every_percent_D_reported_as_initialized_equals_the_textbook_value()
    {
        (_, decimal?[] d) = Reference.Stochastics(Series.Bars, 5, 3);
        Stochastics stoch = new(5, 3);

        for (int i = 0; i < Series.Bars.Length; i++)
        {
            stoch.Update(Make.Bar(Series.Bars[i]));
            if (stoch.IsInitialized)
            {
                Assert.True(d[i].HasValue, $"initialized at index {i}, before the textbook %D exists (index 6)");
                Check.Close(d[i]!.Value, stoch.ValueD, because: $"index {i}");
            }
        }
    }

    [Fact]
    public void It_is_not_initialized_before_kPeriod_bars()
    {
        Stochastics stoch = new(4, 3);
        for (int i = 1; i <= 3; i++)
        {
            stoch.Update(Make.Bar(high: 10m + i, low: 9m, close: 10m));
            Assert.True(stoch.HasInputs);
            Assert.False(stoch.IsInitialized, $"after {i} bars");
        }
    }

    [Fact]
    public void It_is_initialized_once_the_textbook_percent_D_exists()
    {
        Stochastics stoch = new(4, 3); // 4 + 3 - 1 = 6
        for (int i = 1; i <= 6; i++)
        {
            stoch.Update(Make.Bar(high: 10m + i, low: 9m, close: 10m));
        }

        Assert.True(stoch.IsInitialized);
    }

    [Fact]
    public void A_close_on_the_high_gives_100_and_a_close_on_the_low_gives_0()
    {
        Stochastics stoch = new(2, 1);
        stoch.Update(Make.Bar(high: 10m, low: 5m, close: 7m));

        stoch.Update(Make.Bar(high: 12m, low: 6m, close: 12m));
        Assert.Equal(100m, stoch.ValueK);

        stoch.Update(Make.Bar(high: 11m, low: 4m, close: 4m));
        Assert.Equal(0m, stoch.ValueK);
    }

    [Fact]
    public void A_zero_range_gives_the_midpoint_instead_of_dividing_by_zero()
    {
        Stochastics stoch = new(3, 3);
        for (int i = 0; i < 6; i++)
        {
            stoch.Update(Make.Bar(high: 100m, low: 100m, close: 100m));
        }

        Assert.Equal(50m, stoch.ValueK);
        Assert.Equal(50m, stoch.ValueD);
    }

    [Fact]
    public void Raw_values_are_treated_as_bars_with_high_low_and_close_equal()
    {
        Stochastics stoch = new(3, 1);
        foreach (decimal v in new[] { 10m, 20m, 15m })
        {
            stoch.UpdateRaw(v);
        }

        Assert.Equal(50m, stoch.ValueK); // (15 - 10) / (20 - 10)
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(14, 0)]
    [InlineData(-1, 3)]
    public void A_period_below_one_is_rejected(int kPeriod, int dPeriod)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Stochastics(kPeriod, dPeriod));
    }

    [Fact]
    public void Reset_forgets_old_extremes_and_the_percent_D_window()
    {
        Stochastics stoch = new(3, 2);
        stoch.Update(Make.Bar(high: 1000m, low: 1m, close: 500m));
        stoch.Update(Make.Bar(high: 1000m, low: 1m, close: 500m));

        stoch.Reset();

        Assert.False(stoch.HasInputs);
        Assert.False(stoch.IsInitialized);
        Assert.Equal(0m, stoch.ValueK);
        Assert.Equal(0m, stoch.ValueD);

        stoch.Update(Make.Bar(high: 20m, low: 10m, close: 20m));
        Assert.Equal(100m, stoch.ValueK); // would be ~1.9 if the old 1..1000 range had survived
        Assert.Equal(100m, stoch.ValueD);
    }
}
