using Bytex.Indicators.Tests.Support;

namespace Bytex.Indicators.Tests;

// Keltner channel: middle = EMA(period) of the typical price (H + L + C) / 3, bands = middle +/- k * ATR(atrPeriod).
// The EMA follows the engine's first-value seed, the ATR is Wilder's. Both legs must be warm for IsInitialized.
public class KeltnerChannelTests
{
    [Fact]
    public void Values_match_a_hand_computed_table()
    {
        // period 3 (alpha 0.5), k 2, atrPeriod 2
        // H   L   C    typical  EMA     TR                        ATR                  upper   lower
        // 10  8   9    9        9       2                         2                    13      5
        // 13  11  12   12       10.5    max(2, |13-9|, |11-9|)=4  (2+4)/2 = 3          16.5    4.5
        // 12  10  11   11       10.75   max(2, |12-12|, |10-12|)=2 (3*1+2)/2 = 2.5     15.75   5.75
        KeltnerChannel kc = new(3, 2m, 2);

        kc.Update(Make.Bar(high: 10m, low: 8m, close: 9m));
        Assert.Equal((13m, 9m, 5m), (kc.Upper, kc.Middle, kc.Lower));

        kc.Update(Make.Bar(high: 13m, low: 11m, close: 12m));
        Assert.Equal((16.5m, 10.5m, 4.5m), (kc.Upper, kc.Middle, kc.Lower));

        kc.Update(Make.Bar(high: 12m, low: 10m, close: 11m));
        Assert.Equal((15.75m, 10.75m, 5.75m), (kc.Upper, kc.Middle, kc.Lower));
    }

    [Theory]
    [InlineData(20, "2", 10)]
    [InlineData(5, "1.5", 14)]
    public void Values_match_the_naive_reference_on_the_bar_series(int period, string kText, int atrPeriod)
    {
        decimal k = Series.Parse(kText)[0];
        decimal[] typical = Series.Bars.Select(b => (b.High + b.Low + b.Close) / 3m).ToArray();
        decimal[] middle = Reference.EmaSeededWithFirstValue(typical, period);
        decimal?[] atr = Reference.AtrWilder(Series.Bars, atrPeriod);
        KeltnerChannel kc = new(period, k, atrPeriod);

        for (int i = 0; i < Series.Bars.Length; i++)
        {
            kc.Update(Make.Bar(Series.Bars[i]));
            Check.Close(middle[i], kc.Middle, because: $"middle, index {i}");
            if (atr[i] is { } range)
            {
                Check.Close(middle[i] + k * range, kc.Upper, because: $"upper, index {i}");
                Check.Close(middle[i] - k * range, kc.Lower, because: $"lower, index {i}");
            }
        }
    }

    [Theory]
    [InlineData(5, 3, 5)]
    [InlineData(3, 6, 6)]
    public void It_becomes_initialized_when_both_the_average_and_the_range_are_warm(int period, int atrPeriod, int expectedBars)
    {
        KeltnerChannel kc = new(period, 2m, atrPeriod);

        for (int i = 1; i < expectedBars; i++)
        {
            kc.Update(Make.Bar(high: 11m, low: 9m, close: 10m));
            Assert.True(kc.HasInputs);
            Assert.False(kc.IsInitialized, $"after {i} bars");
        }

        kc.Update(Make.Bar(high: 11m, low: 9m, close: 10m));
        Assert.True(kc.IsInitialized);
    }

    [Fact]
    public void Bars_without_any_range_collapse_the_channel()
    {
        KeltnerChannel kc = new(4, 2m, 3);
        for (int i = 0; i < 10; i++)
        {
            kc.Update(Make.Bar(high: 70m, low: 70m, close: 70m));
        }

        Check.Close(70m, kc.Middle);
        Check.Close(70m, kc.Upper);
        Check.Close(70m, kc.Lower);
    }

    [Fact]
    public void Raw_values_use_the_value_as_price_and_the_absolute_change_as_range()
    {
        KeltnerChannel kc = new(3, 1m, 2);

        kc.UpdateRaw(10m); // EMA 10, TR 0
        kc.UpdateRaw(14m); // EMA 12, TR 4, ATR 2

        Assert.Equal((14m, 12m, 10m), (kc.Upper, kc.Middle, kc.Lower));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(20, 0)]
    [InlineData(-1, 10)]
    public void A_period_below_one_is_rejected(int period, int atrPeriod)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new KeltnerChannel(period, 2m, atrPeriod));
    }

    [Fact]
    public void Reset_clears_the_average_and_the_range()
    {
        KeltnerChannel kc = new(3, 2m, 2);
        kc.Update(Make.Bar(high: 5000m, low: 1000m, close: 3000m));
        kc.Update(Make.Bar(high: 5000m, low: 1000m, close: 3000m));

        kc.Reset();

        Assert.False(kc.HasInputs);
        Assert.False(kc.IsInitialized);
        Assert.Equal((0m, 0m, 0m), (kc.Upper, kc.Middle, kc.Lower));

        kc.Update(Make.Bar(high: 10m, low: 8m, close: 9m));
        Assert.Equal((13m, 9m, 5m), (kc.Upper, kc.Middle, kc.Lower));
    }
}
