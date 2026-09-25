using Bytex.Indicators.Tests.Support;

namespace Bytex.Indicators.Tests;

// Bollinger bands: middle = SMA(N), upper/lower = middle +/- k * population standard deviation (divisor N),
// Width = (upper - lower) / middle. Raw values, quotes and trades feed the price itself;
// bars feed the typical price (H + L + C) / 3, a variant Bollinger himself lists next to the close.
public class BollingerBandsTests
{
    private static readonly decimal[] Classic = [2m, 4m, 4m, 4m, 5m, 5m, 7m, 9m]; // mean 5, population sigma exactly 2

    [Theory]
    [InlineData("2", "9", "1", "1.6")]
    [InlineData("1", "7", "3", "0.8")]
    [InlineData("0.5", "6", "4", "0.4")]
    public void Bands_match_the_hand_computed_example(string k, string upper, string lower, string width)
    {
        BollingerBands bb = new(8, Series.Parse(k)[0]);
        foreach (decimal v in Classic)
        {
            bb.UpdateRaw(v);
        }

        Assert.Equal(5m, bb.Middle);
        Assert.Equal(Series.Parse(upper)[0], bb.Upper);
        Assert.Equal(Series.Parse(lower)[0], bb.Lower);
        Assert.Equal(Series.Parse(width)[0], bb.Width);
    }

    [Theory]
    [InlineData(5, "2")]
    [InlineData(20, "2")]
    [InlineData(10, "1.5")]
    public void Bands_match_the_naive_reference_on_a_long_series(int period, string kText)
    {
        decimal k = Series.Parse(kText)[0];
        decimal[] closes = Series.Closes;
        (decimal?[] middle, decimal?[] upper, decimal?[] lower) = Reference.Bollinger(closes, period, k);
        BollingerBands bb = new(period, k);

        for (int i = 0; i < closes.Length; i++)
        {
            bb.UpdateRaw(closes[i]);
            Assert.Equal(middle[i].HasValue, bb.IsInitialized);
            if (middle[i] is { } expectedMiddle)
            {
                Check.Close(expectedMiddle, bb.Middle, because: $"middle, index {i}");
                Check.Close(upper[i]!.Value, bb.Upper, because: $"upper, index {i}");
                Check.Close(lower[i]!.Value, bb.Lower, because: $"lower, index {i}");
            }
        }
    }

    [Fact]
    public void Bars_feed_the_typical_price()
    {
        BollingerBands bb = new(2, 2m);

        bb.Update(Make.Bar(high: 12m, low: 6m, close: 9m)); // typical 9
        bb.Update(Make.Bar(high: 16m, low: 12m, close: 14m)); // typical 14

        // mean 11.5, deviations +/- 2.5, population sigma 2.5
        Assert.Equal(11.5m, bb.Middle);
        Assert.Equal(16.5m, bb.Upper);
        Assert.Equal(6.5m, bb.Lower);
    }

    [Fact]
    public void Quotes_feed_the_mid_price()
    {
        BollingerBands bb = new(2, 1m);

        bb.Update(Make.Quote(bid: 9m, ask: 11m));
        bb.Update(Make.Quote(bid: 13m, ask: 15m));

        Assert.Equal(12m, bb.Middle);
        Assert.Equal(14m, bb.Upper);
        Assert.Equal(10m, bb.Lower);
    }

    [Fact]
    public void A_constant_series_collapses_the_bands_onto_the_middle()
    {
        BollingerBands bb = new(5);
        for (int i = 0; i < 9; i++)
        {
            bb.UpdateRaw(31.4m);
        }

        Assert.Equal(31.4m, bb.Middle);
        Assert.Equal(31.4m, bb.Upper);
        Assert.Equal(31.4m, bb.Lower);
        Assert.Equal(0m, bb.Width);
    }

    [Fact]
    public void Width_is_zero_rather_than_a_division_by_zero_when_the_middle_is_zero()
    {
        BollingerBands bb = new(2);
        bb.UpdateRaw(-1m);
        bb.UpdateRaw(1m);

        Assert.Equal(0m, bb.Middle);
        Assert.Equal(2m, bb.Upper);
        Assert.Equal(0m, bb.Width);
    }

    [Fact]
    public void It_becomes_initialized_on_exactly_the_Nth_input()
    {
        BollingerBands bb = new(3);
        bb.UpdateRaw(1m);
        bb.UpdateRaw(2m);
        Assert.True(bb.HasInputs);
        Assert.False(bb.IsInitialized);

        bb.UpdateRaw(3m);
        Assert.True(bb.IsInitialized);
    }

    [Fact]
    public void With_a_period_of_one_there_is_no_dispersion()
    {
        BollingerBands bb = new(1);
        bb.UpdateRaw(3m);
        bb.UpdateRaw(8m);

        Assert.Equal(8m, bb.Upper);
        Assert.Equal(8m, bb.Lower);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-20)]
    public void A_period_below_one_is_rejected(int period)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BollingerBands(period));
    }

    [Fact]
    public void Reset_forgets_the_window()
    {
        BollingerBands bb = new(2, 2m);
        bb.UpdateRaw(1000m);
        bb.UpdateRaw(5000m);

        bb.Reset();

        Assert.False(bb.HasInputs);
        Assert.False(bb.IsInitialized);
        Assert.Equal(0m, bb.Upper);
        Assert.Equal(0m, bb.Middle);
        Assert.Equal(0m, bb.Lower);

        bb.UpdateRaw(9m);
        bb.UpdateRaw(14m);
        Assert.Equal(16.5m, bb.Upper);
    }
}
