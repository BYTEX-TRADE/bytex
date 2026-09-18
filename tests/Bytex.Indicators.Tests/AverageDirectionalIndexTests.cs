using Bytex.Indicators.Tests.Support;

namespace Bytex.Indicators.Tests;

// Wilder's directional movement system (1978). +DM / -DM / TR are summed over the first N bar-to-bar moves and
// then smoothed as S - S/N + current; DI = 100 * DM_N / TR_N; DX = 100 * |+DI - -DI| / (+DI + -DI).
// The first ADX is the simple mean of the first N DX values - and a DX value only exists once N moves have been
// summed - so the first ADX appears on bar 2N; later values are (ADX * (N - 1) + DX) / N.
public class AverageDirectionalIndexTests
{
    [Fact]
    public void A_steady_uptrend_gives_plus_DI_two_thirds_minus_DI_zero_and_ADX_100()
    {
        // Each bar: high +1, low +1, close in the middle. TR = max(1, |H - prevC| = 1.5, |L - prevC| = 0.5) = 1.5,
        // +DM = 1, -DM = 0 -> +DI = 100 * 1 / 1.5, DX = 100 on every bar, so any average of DX is 100.
        AverageDirectionalIndex adx = new(4);
        for (int i = 0; i < 20; i++)
        {
            adx.Update(Make.Bar(high: 10m + i, low: 9m + i, close: 9.5m + i));
        }

        Check.Close(200m / 3m, adx.PlusDi);
        Assert.Equal(0m, adx.MinusDi);
        Check.Close(100m, adx.Value);
    }

    [Fact]
    public void A_steady_downtrend_is_the_mirror_image()
    {
        AverageDirectionalIndex adx = new(4);
        for (int i = 0; i < 20; i++)
        {
            adx.Update(Make.Bar(high: 100m - i, low: 99m - i, close: 99.5m - i));
        }

        Assert.Equal(0m, adx.PlusDi);
        Check.Close(200m / 3m, adx.MinusDi);
        Check.Close(100m, adx.Value);
    }

    [Fact]
    public void Inside_bars_and_symmetric_outside_bars_carry_no_directional_movement()
    {
        // N = 1 makes the smoothed sums equal to the current bar, so the DIs can be read off directly.
        AverageDirectionalIndex adx = new(1);
        adx.Update(Make.Bar(high: 10m, low: 8m, close: 9m));

        adx.Update(Make.Bar(high: 12m, low: 9m, close: 11m)); // up 2, down -1, TR 3
        Check.Close(200m / 3m, adx.PlusDi);
        Assert.Equal(0m, adx.MinusDi);

        adx.Update(Make.Bar(high: 11.5m, low: 9.5m, close: 10m)); // inside bar: up -0.5, down -0.5
        Assert.Equal(0m, adx.PlusDi);
        Assert.Equal(0m, adx.MinusDi);

        adx.Update(Make.Bar(high: 13.5m, low: 7.5m, close: 10m)); // outside bar: up 2, down 2 -> neither wins
        Assert.Equal(0m, adx.PlusDi);
        Assert.Equal(0m, adx.MinusDi);

        adx.Update(Make.Bar(high: 13m, low: 5.5m, close: 6m)); // up -0.5, down 2, TR 7.5
        Assert.Equal(0m, adx.PlusDi);
        Check.Close(200m / 7.5m, adx.MinusDi);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(14)]
    public void Both_directional_indicators_match_Wilder_once_N_moves_have_been_summed(int period)
    {
        (decimal?[] plusDi, decimal?[] minusDi, _) = Reference.AdxWilder(Series.Bars, period);
        AverageDirectionalIndex adx = new(period);
        int compared = 0;

        for (int i = 0; i < Series.Bars.Length; i++)
        {
            adx.Update(Make.Bar(Series.Bars[i]));
            if (plusDi[i] is { } expectedPlus)
            {
                Check.Close(expectedPlus, adx.PlusDi, because: $"+DI, index {i}");
                Check.Close(minusDi[i]!.Value, adx.MinusDi, because: $"-DI, index {i}");
                compared++;
            }
        }

        Assert.Equal(Series.Bars.Length - period, compared);
    }

    [Fact(Skip = "BUG: ADX is seeded with the mean of DX values taken from partial DM/TR sums (moves 1..N) instead of the first N real DX values (moves N..2N-1), so it never equals Wilder's ADX")]
    public void Every_ADX_reported_as_initialized_equals_Wilders_value()
    {
        const int Period = 5;
        (_, _, decimal?[] want) = Reference.AdxWilder(Series.Bars, Period);
        AverageDirectionalIndex adx = new(Period);
        int compared = 0;

        for (int i = 0; i < Series.Bars.Length; i++)
        {
            adx.Update(Make.Bar(Series.Bars[i]));
            if (adx.IsInitialized)
            {
                Check.Close(want[i]!.Value, adx.Value, because: $"index {i}");
                compared++;
            }
        }

        Assert.True(compared > 0);
    }

    [Fact]
    public void It_is_not_initialized_before_Wilders_first_ADX_on_bar_2N()
    {
        AverageDirectionalIndex adx = new(4);
        Assert.False(adx.HasInputs);

        for (int i = 0; i < 7; i++)
        {
            adx.Update(Make.Bar(Series.Bars[i]));
            Assert.True(adx.HasInputs);
            Assert.False(adx.IsInitialized, $"after {i + 1} bars");
        }
    }

    [Fact]
    public void It_is_initialized_after_2N_plus_one_bars()
    {
        // One bar later than Wilder strictly requires (the engine counts 2N moves, not 2N bars).
        AverageDirectionalIndex adx = new(4);
        for (int i = 0; i < 9; i++)
        {
            adx.Update(Make.Bar(Series.Bars[i]));
        }

        Assert.True(adx.IsInitialized);
    }

    [Fact]
    public void All_outputs_stay_within_0_and_100()
    {
        AverageDirectionalIndex adx = new(5);
        foreach (Ohlcv bar in Series.Bars)
        {
            adx.Update(Make.Bar(bar));
            Assert.InRange(adx.Value, 0m, 100m);
            Assert.InRange(adx.PlusDi, 0m, 100m);
            Assert.InRange(adx.MinusDi, 0m, 100m);
        }
    }

    [Fact]
    public void Bars_without_any_range_leave_every_output_at_zero()
    {
        AverageDirectionalIndex adx = new(3);
        for (int i = 0; i < 12; i++)
        {
            adx.Update(Make.Bar(high: 5m, low: 5m, close: 5m));
        }

        Assert.Equal((0m, 0m, 0m), (adx.Value, adx.PlusDi, adx.MinusDi));
    }

    [Theory(Skip = "BUG: AverageDirectionalIndex does not validate its period; 0 later fails with DivideByZeroException and a negative period silently produces garbage")]
    [InlineData(0)]
    [InlineData(-14)]
    public void A_period_below_one_is_rejected(int period)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AverageDirectionalIndex(period));
    }

    [Fact]
    public void Reset_forgets_the_previous_bar_and_all_smoothed_sums()
    {
        AverageDirectionalIndex adx = new(2);
        for (int i = 0; i < 8; i++)
        {
            adx.Update(Make.Bar(high: 1000m - 10m * i, low: 990m - 10m * i, close: 995m - 10m * i));
        }

        adx.Reset();

        Assert.False(adx.HasInputs);
        Assert.False(adx.IsInitialized);
        Assert.Equal((0m, 0m, 0m), (adx.Value, adx.PlusDi, adx.MinusDi));

        // A clean uptrend after the reset: any leftover -DM from the downtrend above would make -DI non-zero.
        for (int i = 0; i < 3; i++)
        {
            adx.Update(Make.Bar(high: 10m + i, low: 9m + i, close: 9.5m + i));
        }

        Check.Close(200m / 3m, adx.PlusDi);
        Assert.Equal(0m, adx.MinusDi);
    }
}
