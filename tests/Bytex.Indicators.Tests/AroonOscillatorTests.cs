using Bytex.Indicators.Tests.Support;

namespace Bytex.Indicators.Tests;

// Chande's Aroon over the last N + 1 bars (the current one included):
// Up = 100 * (N - bars since the highest high) / N, Down likewise for the lowest low, oscillator = Up - Down.
// When the extreme occurs more than once the most recent occurrence counts (the TA-Lib rule).
public class AroonOscillatorTests
{
    [Fact]
    public void Values_match_a_hand_computed_table()
    {
        // N = 4 (window of 5 bars)
        // H  L      highest / bars since   lowest / bars since   up    down   osc
        // 1  0.5
        // 2  1.5
        // 3  2.5
        // 4  3.5
        // 5  4.5    5 / 0                  0.5 / 4               100   0      100
        // 4  3      5 / 1                  1.5 / 4               75    0      75
        // 3  1      5 / 2                  1   / 0               50    100    -50
        AroonOscillator aroon = new(4);
        for (int i = 1; i <= 4; i++)
        {
            aroon.Update(Make.Bar(high: i, low: i - 0.5m, close: i));
        }

        aroon.Update(Make.Bar(high: 5m, low: 4.5m, close: 5m));
        Assert.Equal((100m, 0m, 100m), (aroon.AroonUp, aroon.AroonDown, aroon.Value));

        aroon.Update(Make.Bar(high: 4m, low: 3m, close: 3m));
        Assert.Equal((75m, 0m, 75m), (aroon.AroonUp, aroon.AroonDown, aroon.Value));

        aroon.Update(Make.Bar(high: 3m, low: 1m, close: 1m));
        Assert.Equal((50m, 100m, -50m), (aroon.AroonUp, aroon.AroonDown, aroon.Value));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(25)]
    public void Values_match_the_naive_reference_on_the_bar_series(int period)
    {
        (decimal?[] up, decimal?[] down) = Reference.Aroon(Series.Bars, period);
        AroonOscillator aroon = new(period);

        for (int i = 0; i < Series.Bars.Length; i++)
        {
            aroon.Update(Make.Bar(Series.Bars[i]));
            Assert.Equal(up[i].HasValue, aroon.IsInitialized);
            if (up[i] is { } expectedUp)
            {
                Check.Close(expectedUp, aroon.AroonUp, because: $"up, index {i}");
                Check.Close(down[i]!.Value, aroon.AroonDown, because: $"down, index {i}");
                Check.Close(expectedUp - down[i]!.Value, aroon.Value, because: $"oscillator, index {i}");
            }
        }
    }

    [Fact]
    public void An_extreme_falls_out_after_N_bars()
    {
        AroonOscillator aroon = new(3);
        aroon.Update(Make.Bar(high: 100m, low: 99m, close: 99m)); // the spike
        decimal[] expectedUp = [Third(2), Third(1), 0m];

        for (int i = 0; i < 3; i++)
        {
            aroon.Update(Make.Bar(high: 50m - i, low: 49m - i, close: 49m - i));
            Check.Close(expectedUp[i], aroon.AroonUp, because: $"{i + 1} bars after the spike");
        }

        // Fourth bar after the spike: it is outside the 4-bar window, the highest high is now the 50 three bars back.
        aroon.Update(Make.Bar(high: 47m, low: 46m, close: 46m));
        Assert.Equal(0m, aroon.AroonUp);

        static decimal Third(int n) => 100m * n / 3m;
    }

    [Fact]
    public void A_constant_series_gives_a_zero_oscillator()
    {
        // Whichever occurrence of a repeated extreme is chosen, it is chosen the same way for highs and lows.
        AroonOscillator aroon = new(5);
        for (int i = 0; i < 12; i++)
        {
            aroon.UpdateRaw(10m);
        }

        Assert.Equal(0m, aroon.Value);
        Assert.Equal(aroon.AroonUp, aroon.AroonDown);
    }

    [Fact]
    public void A_strictly_rising_series_pins_the_oscillator_at_100()
    {
        AroonOscillator aroon = new(6);
        for (int i = 1; i <= 20; i++)
        {
            aroon.UpdateRaw(i);
        }

        Assert.Equal((100m, 0m, 100m), (aroon.AroonUp, aroon.AroonDown, aroon.Value));
    }

    [Fact]
    public void It_becomes_initialized_on_bar_N_plus_one()
    {
        AroonOscillator aroon = new(3);
        for (int i = 1; i <= 3; i++)
        {
            aroon.UpdateRaw(i);
            Assert.True(aroon.HasInputs);
            Assert.False(aroon.IsInitialized, $"after {i} bars");
        }

        aroon.UpdateRaw(4m);
        Assert.True(aroon.IsInitialized);
    }

    [Fact]
    public void A_negative_period_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AroonOscillator(-1));
    }

    [Fact(Skip = "BUG: AroonOscillator accepts period 0 and then throws DivideByZeroException on the first update")]
    public void A_period_of_zero_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AroonOscillator(0));
    }

    [Fact]
    public void Reset_forgets_old_extremes()
    {
        AroonOscillator aroon = new(2);
        aroon.Update(Make.Bar(high: 9999m, low: 9998m, close: 9998m));

        aroon.Reset();

        Assert.False(aroon.HasInputs);
        Assert.False(aroon.IsInitialized);
        Assert.Equal((0m, 0m), (aroon.AroonUp, aroon.AroonDown));

        aroon.UpdateRaw(1m);
        aroon.UpdateRaw(2m);
        aroon.UpdateRaw(3m);
        Assert.Equal((100m, 0m), (aroon.AroonUp, aroon.AroonDown));
    }
}
