using Bytex.Indicators.Tests.Support;

namespace Bytex.Indicators.Tests;

// HMA (Alan Hull): WMA( 2 * WMA(x, N/2) - WMA(x, N), round(sqrt(N)) ), integer division for N/2.
// The outer WMA needs round(sqrt(N)) values of the inner difference, each of which needs a full N-window,
// so the first textbook value exists at input N + round(sqrt(N)) - 1.
public class HullMovingAverageTests
{
    [Fact]
    public void Values_match_a_hand_computed_table()
    {
        // N = 4: half = 2, root = 2. Inputs 1 2 3 4 5 6.
        // WMA2: -, 5/3, 8/3, 11/3, 14/3, 17/3        WMA4: -, -, -, 3, 4, 5
        // raw = 2*WMA2 - WMA4 (from input 4): 13/3, 16/3, 19/3
        // HMA = WMA2(raw): input 5 -> (13/3 + 2*16/3) / 3 = 5, input 6 -> (16/3 + 2*19/3) / 3 = 6
        HullMovingAverage hma = new(4);
        foreach (decimal v in new[] { 1m, 2m, 3m, 4m })
        {
            hma.UpdateRaw(v);
        }

        hma.UpdateRaw(5m);
        Check.Close(5m, hma.Value);

        hma.UpdateRaw(6m);
        Check.Close(6m, hma.Value);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(9)]
    [InlineData(16)]
    public void From_the_first_textbook_value_on_it_matches_the_naive_reference(int period)
    {
        decimal[] closes = Series.Closes;
        decimal?[] want = Reference.Hma(closes, period);
        HullMovingAverage hma = new(period);
        int compared = 0;

        for (int i = 0; i < closes.Length; i++)
        {
            hma.UpdateRaw(closes[i]);
            if (want[i] is { } expected)
            {
                Check.Close(expected, hma.Value, because: $"index {i}");
                compared++;
            }
        }

        Assert.Equal(closes.Length - (period + (int)Math.Round(Math.Sqrt(period)) - 2), compared);
    }

    [Fact(Skip = "BUG: HMA reports IsInitialized after N inputs, while its value is still built from partial-window WMAs until input N + round(sqrt(N)) - 1")]
    public void Every_value_reported_as_initialized_equals_the_textbook_value()
    {
        decimal[] closes = Series.Closes;
        decimal?[] want = Reference.Hma(closes, 16);
        HullMovingAverage hma = new(16);

        for (int i = 0; i < closes.Length; i++)
        {
            hma.UpdateRaw(closes[i]);
            if (hma.IsInitialized)
            {
                Assert.True(want[i].HasValue, $"initialized at index {i}, before the textbook value exists (index 18)");
                Check.Close(want[i]!.Value, hma.Value, because: $"index {i}");
            }
        }
    }

    [Fact]
    public void It_is_not_initialized_before_N_inputs()
    {
        HullMovingAverage hma = new(9);
        for (int i = 1; i <= 8; i++)
        {
            hma.UpdateRaw(i);
            Assert.True(hma.HasInputs);
            Assert.False(hma.IsInitialized, $"after {i} inputs");
        }
    }

    [Fact]
    public void It_is_initialized_once_the_textbook_value_exists()
    {
        HullMovingAverage hma = new(9); // 9 + 3 - 1 = 11
        for (int i = 1; i <= 11; i++)
        {
            hma.UpdateRaw(i);
        }

        Assert.True(hma.IsInitialized);
        Assert.Equal(11, hma.Count);
    }

    [Fact]
    public void On_a_unit_ramp_with_N_16_it_settles_two_thirds_below_the_input()
    {
        // A WMA of length n lags a unit ramp by (n - 1) / 3.
        // raw = 2 * (x - 7/3) - (x - 15/3) = x + 1/3; the outer WMA(4) lags by 1, so HMA = x - 2/3.
        HullMovingAverage hma = new(16);
        for (int i = 1; i <= 60; i++)
        {
            hma.UpdateRaw(i);
        }

        Check.Close(60m - 2m / 3m, hma.Value);
    }

    [Fact]
    public void A_period_of_one_echoes_the_input()
    {
        HullMovingAverage hma = new(1);
        hma.UpdateRaw(5m);
        hma.UpdateRaw(3m);

        Assert.Equal(3m, hma.Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    public void A_period_below_one_is_rejected(int period)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HullMovingAverage(period));
    }

    [Fact]
    public void Reset_clears_all_three_inner_averages()
    {
        HullMovingAverage hma = new(4);
        for (int i = 0; i < 10; i++)
        {
            hma.UpdateRaw(1000m + i);
        }

        hma.Reset();

        Assert.False(hma.HasInputs);
        Assert.False(hma.IsInitialized);
        Assert.Equal(0m, hma.Value);
        Assert.Equal(0, hma.Count);

        foreach (decimal v in new[] { 1m, 2m, 3m, 4m, 5m })
        {
            hma.UpdateRaw(v);
        }

        Check.Close(5m, hma.Value);
    }
}
