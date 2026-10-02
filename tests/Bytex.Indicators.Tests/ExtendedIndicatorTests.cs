using Bytex.Indicators.Tests.Support;

namespace Bytex.Indicators.Tests;

// Why (R7.8): eight indicators a strategy could not have without writing them itself. Every number below is worked out
// from the definition in the comment above it rather than from a run of the code, and where an indicator has no single
// number to check - a stop that follows price - what is checked is the RULE that makes it what it is.
public class ExtendedIndicatorTests
{
    // ----- Commodity channel index -----

    // TP = (H+L+C)/3, and a bar with no range has TP = close.
    // CCI = (TP - mean(TP)) / (0.015 * mean(|TP - mean(TP)|)).
    // Closes 10, 11, 12 over three periods: mean 11, mean deviation (1+0+1)/3 = 2/3,
    // so CCI = (12 - 11) / (0.015 * 2/3) = 1 / 0.01 = 100.
    [Fact]
    public void A_commodity_channel_index_is_distance_from_the_mean_in_mean_deviations()
    {
        CommodityChannelIndex cci = new(3);

        cci.Update(Make.Bar(10m, 10m, 10m));
        cci.Update(Make.Bar(11m, 11m, 11m));
        Assert.False(cci.IsInitialized);

        cci.Update(Make.Bar(12m, 12m, 12m));

        Assert.True(cci.IsInitialized);
        Check.Close(100m, cci.Value);

        // Window 11, 12, 13: mean 12, mean deviation 2/3 again.
        cci.Update(Make.Bar(13m, 13m, 13m));
        Check.Close(100m, cci.Value);
    }

    [Fact]
    public void A_price_that_has_not_moved_is_not_far_from_its_average()
    {
        // No deviation is no scale to measure against, so zero rather than a division by nothing.
        CommodityChannelIndex cci = new(3);
        for (int i = 0; i < 3; i++)
        {
            cci.Update(Make.Bar(5m, 5m, 5m));
        }

        Assert.Equal(0m, cci.Value);
    }

    // ----- Chande momentum oscillator -----

    // CMO = 100 * (sum of up moves - sum of down moves) / (sum of both), over the period's moves.
    [Fact]
    public void A_chande_oscillator_is_the_share_of_movement_that_went_one_way()
    {
        ChandeMomentumOscillator cmo = new(2);

        cmo.UpdateRaw(10m);            // no move yet
        cmo.UpdateRaw(11m);            // +1, one move of two
        Assert.False(cmo.IsInitialized);

        cmo.UpdateRaw(13m);            // +2; moves +1 +2 -> all up
        Assert.True(cmo.IsInitialized);
        Assert.Equal(100m, cmo.Value);

        cmo.UpdateRaw(12m);            // -1; moves +2 -1 -> 100 * (2 - 1) / 3
        Check.Close(100m * 1m / 3m, cmo.Value, 0.0000000001m);

        cmo.UpdateRaw(12m);            // 0; moves -1 0 -> all down
        Assert.Equal(-100m, cmo.Value);

        cmo.UpdateRaw(12m);            // 0; moves 0 0 -> nothing moved
        Assert.Equal(0m, cmo.Value);
    }

    // ----- Money flow index -----

    // Raw money flow = TP * volume, counted positive when TP rose and negative when it fell; a TP that did not move
    // counts on neither side. MFI = 100 * positive / (positive + negative) over the period.
    [Fact]
    public void A_money_flow_index_weighs_the_flow_of_the_periods_that_rose_against_those_that_fell()
    {
        MoneyFlowIndex mfi = new(2);

        mfi.Update(Make.Bar(10m, 10m, 10m, volume: 1m));                 // the reference period
        mfi.Update(Make.Bar(11m, 11m, 11m, volume: 2m));                 // rose: +22
        Assert.False(mfi.IsInitialized);

        mfi.Update(Make.Bar(10m, 10m, 10m, volume: 3m));                 // fell: -30

        Assert.True(mfi.IsInitialized);
        Check.Close(100m * 22m / 52m, mfi.Value, 0.0000000001m);

        // Unchanged: on neither side, so the window is now -30 and nothing.
        mfi.Update(Make.Bar(10m, 10m, 10m, volume: 4m));
        Assert.Equal(0m, mfi.Value);
    }

    [Fact]
    public void A_money_flow_index_refuses_what_carries_no_volume()
    {
        MoneyFlowIndex mfi = new(2);

        Assert.Throws<NotSupportedException>(() => mfi.Update(Make.Quote(10m, 11m)));
        Assert.Throws<NotSupportedException>(() => mfi.UpdateRaw(10m));
    }

    // ----- Linear regression -----

    // Least squares through (0,2), (1,4), (2,6): slope 2, intercept 2, fitted value at the newest point 6, and a
    // perfect fit.
    [Fact]
    public void A_regression_through_a_straight_line_is_that_line()
    {
        LinearRegression linreg = new(3);

        linreg.UpdateRaw(2m);
        linreg.UpdateRaw(4m);
        Assert.False(linreg.IsInitialized);

        linreg.UpdateRaw(6m);

        Assert.True(linreg.IsInitialized);
        Check.Close(2m, linreg.Slope);
        Check.Close(2m, linreg.Intercept);
        Check.Close(6m, linreg.Value);
        Check.Close(1m, linreg.RSquared);

        // Window 4, 6, 8: the same slope, the line moved up.
        linreg.UpdateRaw(8m);
        Check.Close(2m, linreg.Slope);
        Check.Close(4m, linreg.Intercept);
        Check.Close(8m, linreg.Value);
    }

    [Fact]
    public void A_flat_series_has_no_slope_and_is_explained_perfectly()
    {
        // Nothing varies, so there is nothing left unexplained: 1 rather than a division by zero.
        LinearRegression linreg = new(3);
        for (int i = 0; i < 3; i++)
        {
            linreg.UpdateRaw(5m);
        }

        Assert.Equal(0m, linreg.Slope);
        Check.Close(5m, linreg.Value);
        Assert.Equal(1m, linreg.RSquared);
    }

    [Fact]
    public void A_regression_over_noise_has_a_slope_that_explains_little()
    {
        // The reason R squared travels with the slope: the same slope through a line and through noise mean different
        // things, and a strategy reading only the slope cannot tell them apart.
        LinearRegression straight = new(4);
        LinearRegression noisy = new(4);
        foreach (decimal value in new[] { 1m, 2m, 3m, 4m })
        {
            straight.UpdateRaw(value);
        }

        foreach (decimal value in new[] { 1m, 4m, 1m, 4m })
        {
            noisy.UpdateRaw(value);
        }

        Assert.Equal(1m, straight.RSquared);
        Assert.True(noisy.RSquared < 0.5m, $"a zig-zag is not explained by a line (R² {noisy.RSquared})");
    }

    // ----- Ichimoku -----

    // Six rising bars (H, L, C): (10,8,9) (12,10,11) (14,12,13) (16,14,15) (18,16,17) (20,18,19),
    // with conversion 2, base 3, span B 4 and a displacement of 2.
    //
    // At the sixth bar:
    //   conversion = (max H of 2, min L of 2) / 2 = (20 + 16) / 2 = 18
    //   base       = (max H of 3, min L of 3) / 2 = (20 + 14) / 2 = 17
    //   span A ahead = (18 + 17) / 2 = 17.5          span B ahead = (20 + 12) / 2 = 16
    // and the cloud over THIS bar is what was computed two bars ago, at the fourth:
    //   conversion = (16 + 12) / 2 = 14   base = (16 + 10) / 2 = 13   span A = 13.5   span B = (16 + 8) / 2 = 12
    // so the cloud is 12 to 13.5, and the chikou reference is the close of the fourth bar, 15.
    [Fact]
    public void Ichimoku_separates_the_lines_it_has_just_computed_from_the_cloud_over_this_bar()
    {
        Ichimoku ichimoku = new(conversionPeriod: 2, basePeriod: 3, spanBPeriod: 4, displacement: 2);
        foreach ((decimal high, decimal low, decimal close) in new[]
        {
            (10m, 8m, 9m), (12m, 10m, 11m), (14m, 12m, 13m), (16m, 14m, 15m), (18m, 16m, 17m), (20m, 18m, 19m),
        })
        {
            ichimoku.Update(Make.Bar(high, low, close));
        }

        Assert.True(ichimoku.IsInitialized);
        Assert.Equal(18m, ichimoku.ConversionLine);
        Assert.Equal(17m, ichimoku.BaseLine);
        Assert.Equal(17.5m, ichimoku.SpanAAhead);
        Assert.Equal(16m, ichimoku.SpanBAhead);
        Assert.Equal(13.5m, ichimoku.CloudTop);
        Assert.Equal(12m, ichimoku.CloudBottom);
        Assert.Equal(15m, ichimoku.ChikouReference);
        Assert.True(ichimoku.CloudIsBullish, "span A above span B is the bullish arrangement");
        Assert.Equal(2, ichimoku.Displacement);
    }

    [Fact]
    public void Ichimoku_is_not_ready_before_its_cloud_exists()
    {
        // The lines can be computed from the first bar; the cloud over the current bar cannot, and a strategy asking
        // whether price is above the cloud would otherwise be answered from zeroes.
        Ichimoku ichimoku = new(conversionPeriod: 2, basePeriod: 2, spanBPeriod: 2, displacement: 3);

        ichimoku.Update(Make.Bar(10m, 8m, 9m));
        ichimoku.Update(Make.Bar(12m, 10m, 11m));
        Assert.False(ichimoku.IsInitialized);
        Assert.Equal(0m, ichimoku.CloudTop);

        ichimoku.Update(Make.Bar(14m, 12m, 13m));
        ichimoku.Update(Make.Bar(16m, 14m, 15m));

        Assert.True(ichimoku.IsInitialized);
        Assert.Equal(9m, ichimoku.ChikouReference);
    }

    [Fact]
    public void Ichimoku_refuses_a_single_value()
    {
        Ichimoku ichimoku = new();

        Assert.Throws<NotSupportedException>(() => ichimoku.UpdateRaw(10m));
        Assert.Throws<NotSupportedException>(() => ichimoku.Update(Make.Quote(10m, 11m)));
    }

    // ----- Supertrend -----

    [Fact]
    public void A_supertrend_follows_a_rally_upwards_and_never_retreats_while_it_holds()
    {
        // The rule that makes it a stop rather than a band: while the trend holds, it only tightens.
        Supertrend supertrend = new(period: 3, multiplier: 1m);
        decimal previous = decimal.MinValue;
        bool sawTrend = false;

        for (int i = 0; i < 12; i++)
        {
            decimal close = 100m + (i * 2m);
            supertrend.Update(Make.Bar(close + 1m, close - 1m, close));
            if (supertrend.IsInitialized && supertrend.Direction == 1)
            {
                Assert.True(supertrend.Value >= previous, $"the stop moved down from {previous} to {supertrend.Value}");
                Assert.True(supertrend.Value < close, "in an uptrend the stop is below price");
                previous = supertrend.Value;
                sawTrend = true;
            }
        }

        Assert.True(sawTrend, "the rally has to put it in an uptrend");
    }

    [Fact]
    public void A_supertrend_flips_when_price_closes_through_it()
    {
        Supertrend supertrend = new(period: 3, multiplier: 1m);
        for (int i = 0; i < 12; i++)
        {
            decimal close = 100m + (i * 2m);
            supertrend.Update(Make.Bar(close + 1m, close - 1m, close));
        }

        Assert.Equal(1, supertrend.Direction);
        Assert.False(supertrend.Flipped);

        // A collapse well through the stop.
        supertrend.Update(Make.Bar(100m, 80m, 80m));

        Assert.Equal(-1, supertrend.Direction);
        Assert.True(supertrend.Flipped, "the bar that turns it is the signal this indicator exists for");
        Assert.True(supertrend.Value > 80m, "in a downtrend the stop is above price");
    }

    // ----- Parabolic SAR -----

    [Fact]
    public void A_parabolic_sar_climbs_behind_a_rally_and_never_falls_while_it_lasts()
    {
        ParabolicSar psar = new();
        decimal previous = decimal.MinValue;
        int rising = 0;

        for (int i = 0; i < 10; i++)
        {
            decimal close = 100m + (i * 2m);
            psar.Update(Make.Bar(close + 1m, close - 1m, close));
            if (psar.IsInitialized && psar.Direction == 1)
            {
                Assert.True(psar.Value >= previous, $"the stop fell from {previous} to {psar.Value}");
                Assert.True(psar.Value <= close, "in an uptrend the stop is at or below price");
                previous = psar.Value;
                rising++;
            }
        }

        Assert.True(rising > 3, "a ten-bar rally has to leave it in an uptrend");
    }

    [Fact]
    public void A_parabolic_sar_turns_over_when_price_trades_through_it()
    {
        ParabolicSar psar = new();
        for (int i = 0; i < 10; i++)
        {
            decimal close = 100m + (i * 2m);
            psar.Update(Make.Bar(close + 1m, close - 1m, close));
        }

        Assert.Equal(1, psar.Direction);

        psar.Update(Make.Bar(110m, 90m, 90m));

        Assert.Equal(-1, psar.Direction);
        Assert.True(psar.Flipped);
        Assert.True(psar.Value >= 90m, "the stop is now above price");
    }

    // ----- Book imbalance -----

    [Fact]
    public void A_book_imbalance_is_the_share_of_resting_size_on_each_side()
    {
        // Bids 3 + 2, asks 1 + 1 over two levels: (5 - 2) / 7.
        BookImbalance imbalance = new(levels: 2);

        imbalance.Update(Make.Book([(100m, 3m), (99m, 2m)], [(101m, 1m), (102m, 1m)]));

        Assert.True(imbalance.IsInitialized);
        Assert.Equal(5m, imbalance.BidSize);
        Assert.Equal(2m, imbalance.AskSize);
        Check.Close(3m / 7m, imbalance.Value, 0.0000000001m);
    }

    [Fact]
    public void Only_the_levels_it_was_asked_for_are_counted()
    {
        BookImbalance top = new(levels: 1);

        top.Update(Make.Book([(100m, 3m), (99m, 2m)], [(101m, 1m), (102m, 1m)]));

        Assert.Equal(3m, top.BidSize);
        Assert.Equal(1m, top.AskSize);
        Assert.Equal(0.5m, top.Value);
    }

    [Fact]
    public void A_book_with_nothing_in_it_is_not_balanced()
    {
        // Zero would read as "level". Nothing to measure is a different fact, so the indicator says it is not ready.
        BookImbalance imbalance = new();
        imbalance.Update(Make.Book([(100m, 3m)], [(101m, 1m)]));
        Assert.True(imbalance.IsInitialized);

        imbalance.Update(Make.Book([], []));

        Assert.False(imbalance.IsInitialized);
        Assert.Equal(0.5m, imbalance.Value);
    }

    [Fact]
    public void A_quote_is_a_one_level_book()
    {
        // For a venue that gives quotes and no depth. What it measured is one level, whatever was asked for.
        BookImbalance imbalance = new(levels: 5);

        imbalance.Update(Make.Quote(100m, 101m, bidSize: 4m, askSize: 1m));

        Assert.Equal(0.6m, imbalance.Value);
        Assert.Equal(4m, imbalance.BidSize);
    }

    [Fact]
    public void A_book_imbalance_is_bounded_by_one_either_way()
    {
        BookImbalance imbalance = new();

        imbalance.Update(Make.Book([(100m, 7m)], []));
        Assert.Equal(1m, imbalance.Value);

        imbalance.Update(Make.Book([], [(101m, 7m)]));
        Assert.Equal(-1m, imbalance.Value);
    }
}
