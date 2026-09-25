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

    // Why: with the current bar in the window its own high IS the upper band, so "close crosses above the upper band",
    // the classic Donchian breakout, can never be true and a strategy written that way makes no trades.
    [Fact]
    public void With_the_current_bar_excluded_a_close_can_break_the_upper_band()
    {
        DonchianChannel included = new(3);
        DonchianChannel excluded = new(3, excludeCurrent: true);
        (decimal High, decimal Low, decimal Close)[] rows =
        [
            (10m, 9m, 9.5m),
            (10m, 9m, 9.5m),
            (10m, 9m, 9.5m),
            (12m, 9.5m, 11.5m),   // this bar breaks out of the 9 to 10 range the three before it held
        ];

        foreach ((decimal high, decimal low, decimal close) in rows)
        {
            included.Update(Make.Bar(high, low, close));
            excluded.Update(Make.Bar(high, low, close));
        }

        // The breakout bar's own high is the included channel's upper band, so its close is under it.
        Assert.Equal(12m, included.Upper);
        Assert.True(11.5m < included.Upper);

        // Excluding it, the band is the high of the three bars before, and the close is above it.
        Assert.Equal(10m, excluded.Upper);
        Assert.True(11.5m > excluded.Upper);
    }

    [Fact]
    public void The_excluded_channel_is_the_included_one_shifted_by_a_bar()
    {
        DonchianChannel included = new(4);
        DonchianChannel excluded = new(4, excludeCurrent: true);
        decimal?[] previous = new decimal?[Series.Bars.Length];

        for (int i = 0; i < Series.Bars.Length; i++)
        {
            excluded.Update(Make.Bar(Series.Bars[i]));
            if (i > 0)
            {
                // What the excluded channel reports now is what the included one reported for the bar before.
                Assert.Equal(previous[i - 1]!.Value, excluded.Upper);
            }

            included.Update(Make.Bar(Series.Bars[i]));
            previous[i] = included.Upper;
        }
    }

    [Fact]
    public void The_excluded_channel_is_initialized_one_bar_later()
    {
        DonchianChannel included = new(3);
        DonchianChannel excluded = new(3, excludeCurrent: true);

        for (int i = 0; i < 3; i++)
        {
            included.Update(Make.Bar(10m, 9m, 9.5m));
            excluded.Update(Make.Bar(10m, 9m, 9.5m));
        }

        Assert.True(included.IsInitialized);
        Assert.False(excluded.IsInitialized);

        excluded.Update(Make.Bar(10m, 9m, 9.5m));
        Assert.True(excluded.IsInitialized);
    }

    [Fact]
    public void The_option_is_named_so_two_channels_on_one_chart_are_told_apart()
    {
        Assert.Equal("DC(20)", new DonchianChannel().Name);
        Assert.Equal("DC(20,excl)", new DonchianChannel(20, excludeCurrent: true).Name);
    }
}
