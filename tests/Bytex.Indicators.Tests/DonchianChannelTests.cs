using Bytex.Indicators.Tests.Support;

namespace Bytex.Indicators.Tests;

// Donchian channel: upper = highest high and lower = lowest low of the last N bars (the current bar included),
// middle = their mean. Quotes contribute the ask as high and the bid as low.
public class DonchianChannelTests
{
    [Fact]
    public void Values_match_a_hand_computed_table()
    {
        // N = 3
        // H   L     upper  lower  middle
        // 10  8     10     8      9        (partial window)
        // 12  9     12     8      10       (partial window)
        // 11  7     12     7      9.5
        // 9   8     12     7      9.5
        // 9   8.5   11     7      9        the 12 has left the window
        // 9   8.5   9      8      8.5      the 7 has left the window
        DonchianChannel dc = new(3);
        (decimal High, decimal Low, decimal Upper, decimal Lower, decimal Middle)[] rows =
        [
            (10m, 8m, 10m, 8m, 9m),
            (12m, 9m, 12m, 8m, 10m),
            (11m, 7m, 12m, 7m, 9.5m),
            (9m, 8m, 12m, 7m, 9.5m),
            (9m, 8.5m, 11m, 7m, 9m),
            (9m, 8.5m, 9m, 8m, 8.5m),
        ];

        foreach ((decimal high, decimal low, decimal upper, decimal lower, decimal middle) in rows)
        {
            dc.Update(Make.Bar(high, low, close: low));
            Assert.Equal((upper, lower, middle), (dc.Upper, dc.Lower, dc.Middle));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(20)]
    public void Values_match_the_naive_reference_on_the_bar_series(int period)
    {
        (decimal?[] upper, decimal?[] lower) = Reference.Donchian(Series.Bars, period);
        DonchianChannel dc = new(period);

        for (int i = 0; i < Series.Bars.Length; i++)
        {
            dc.Update(Make.Bar(Series.Bars[i]));
            Assert.Equal(upper[i].HasValue, dc.IsInitialized);
            if (upper[i] is { } expectedUpper)
            {
                Assert.Equal(expectedUpper, dc.Upper);
                Assert.Equal(lower[i]!.Value, dc.Lower);
                Assert.Equal((expectedUpper + lower[i]!.Value) / 2m, dc.Middle);
            }
        }
    }

    [Fact]
    public void Quotes_contribute_the_ask_as_high_and_the_bid_as_low()
    {
        DonchianChannel dc = new(3);

        dc.Update(Make.Quote(bid: 99m, ask: 101m));
        dc.Update(Make.Quote(bid: 98m, ask: 100m));
        dc.Update(Make.Quote(bid: 100m, ask: 103m));

        Assert.Equal(103m, dc.Upper);
        Assert.Equal(98m, dc.Lower);
        Assert.Equal(100.5m, dc.Middle);
    }

    [Fact]
    public void Trades_contribute_their_price_as_both_high_and_low()
    {
        DonchianChannel dc = new(3);

        dc.Update(Make.Trade(price: 50m));
        dc.Update(Make.Trade(price: 47m));
        dc.Update(Make.Trade(price: 52m));

        Assert.Equal(52m, dc.Upper);
        Assert.Equal(47m, dc.Lower);
    }

    [Fact]
    public void A_constant_series_collapses_the_channel()
    {
        DonchianChannel dc = new(4);
        for (int i = 0; i < 6; i++)
        {
            dc.UpdateRaw(12.5m);
        }

        Assert.Equal((12.5m, 12.5m, 12.5m), (dc.Upper, dc.Middle, dc.Lower));
    }

    [Fact]
    public void On_a_rising_series_the_lower_band_trails_by_N_minus_one_bars()
    {
        DonchianChannel dc = new(5);
        for (int i = 1; i <= 30; i++)
        {
            dc.UpdateRaw(i);
        }

        Assert.Equal(30m, dc.Upper);
        Assert.Equal(26m, dc.Lower);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_period_below_one_is_rejected(int period)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DonchianChannel(period));
    }

    [Fact]
    public void Reset_forgets_old_extremes()
    {
        DonchianChannel dc = new(5);
        dc.Update(Make.Bar(high: 9999m, low: 1m, close: 5m));

        dc.Reset();

        Assert.False(dc.HasInputs);
        Assert.False(dc.IsInitialized);
        Assert.Equal((0m, 0m, 0m), (dc.Upper, dc.Middle, dc.Lower));

        dc.Update(Make.Bar(high: 11m, low: 9m, close: 10m));
        Assert.Equal((11m, 9m), (dc.Upper, dc.Lower));
    }
}
