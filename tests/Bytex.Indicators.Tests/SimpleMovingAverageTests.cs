using Bytex.Core.Model;
using Bytex.Indicators.Tests.Support;

namespace Bytex.Indicators.Tests;

// SMA: arithmetic mean of the last N inputs. Before N inputs the engine reports the mean of what it has seen.
public class SimpleMovingAverageTests
{
    [Theory]
    [InlineData(3, "1 2 3 4 5", "1 1.5 2 3 4")]
    [InlineData(2, "10 20 40 0", "10 15 30 20")]
    [InlineData(4, "-4 4 -8 8 12", "-4 0 -2.6666666666666666666666666667 0 4")]
    public void Values_match_a_hand_computed_table(int period, string inputs, string expected)
    {
        SimpleMovingAverage sma = new(period);
        decimal[] want = Series.Parse(expected);
        decimal[] values = Series.Parse(inputs);

        for (int i = 0; i < values.Length; i++)
        {
            sma.UpdateRaw(values[i]);
            Check.Close(want[i], sma.Value, because: $"input #{i + 1}");
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(14)]
    public void Values_match_the_naive_reference_on_a_long_series(int period)
    {
        decimal[] closes = Series.Closes;
        decimal?[] want = Reference.Sma(closes, period);
        SimpleMovingAverage sma = new(period);

        for (int i = 0; i < closes.Length; i++)
        {
            sma.UpdateRaw(closes[i]);
            if (want[i] is { } expected)
            {
                Check.Close(expected, sma.Value, because: $"index {i}");
            }
        }
    }

    [Fact]
    public void It_becomes_initialized_on_exactly_the_Nth_input()
    {
        SimpleMovingAverage sma = new(4);
        Assert.False(sma.HasInputs);

        for (int i = 1; i <= 3; i++)
        {
            sma.UpdateRaw(i);
            Assert.True(sma.HasInputs);
            Assert.False(sma.IsInitialized, $"after {i} inputs");
            Assert.Equal(i, sma.Count);
        }

        sma.UpdateRaw(4m);

        Assert.True(sma.IsInitialized);
        Assert.Equal(4, sma.Count);
    }

    [Fact]
    public void Count_saturates_at_the_period()
    {
        SimpleMovingAverage sma = new(2);
        sma.UpdateRaw(1m);
        sma.UpdateRaw(2m);
        sma.UpdateRaw(3m);

        Assert.Equal(2, sma.Count);
    }

    [Fact]
    public void A_period_of_one_echoes_the_input()
    {
        SimpleMovingAverage sma = new(1);

        sma.UpdateRaw(7.25m);
        Assert.True(sma.IsInitialized);
        Assert.Equal(7.25m, sma.Value);

        sma.UpdateRaw(-3m);
        Assert.Equal(-3m, sma.Value);
    }

    [Fact]
    public void A_constant_series_averages_to_the_constant_exactly()
    {
        SimpleMovingAverage sma = new(7);
        for (int i = 0; i < 20; i++)
        {
            sma.UpdateRaw(0.1m);
        }

        Assert.Equal(0.1m, sma.Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void A_period_below_one_is_rejected(int period)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SimpleMovingAverage(period));
    }

    [Fact]
    public void The_name_carries_the_period()
    {
        Assert.Equal("SMA(9)", new SimpleMovingAverage(9).Name);
        Assert.Equal("SMA(9)", new SimpleMovingAverage(9).ToString());
    }

    [Fact]
    public void Bars_contribute_their_close()
    {
        SimpleMovingAverage sma = new(2);

        sma.Update(Make.Bar(high: 12m, low: 8m, close: 10m));
        sma.Update(Make.Bar(high: 30m, low: 10m, close: 20m));

        Assert.Equal(15m, sma.Value);
    }

    [Fact]
    public void Trades_contribute_their_price()
    {
        SimpleMovingAverage sma = new(2);

        sma.Update(Make.Trade(price: 100m, size: 5m));
        sma.Update(Make.Trade(price: 101m, size: 500m));

        Assert.Equal(100.5m, sma.Value);
    }

    [Theory]
    [InlineData(PriceType.Bid, "99")]
    [InlineData(PriceType.Ask, "101")]
    [InlineData(PriceType.Mid, "100")]
    [InlineData(PriceType.Last, "100")] // a quote has no last price; the engine falls back to the mid
    public void Quotes_contribute_the_configured_side(PriceType priceType, string expected)
    {
        SimpleMovingAverage sma = new(1, priceType);

        sma.Update(Make.Quote(bid: 99m, ask: 101m));

        Assert.Equal(priceType, sma.PriceType);
        Assert.Equal(Series.Parse(expected)[0], sma.Value);
    }

    [Fact]
    public void The_mid_of_a_one_tick_spread_keeps_its_half_tick()
    {
        SimpleMovingAverage sma = new(1);

        sma.Update(Make.Quote(bid: 1.0001m, ask: 1.0002m));

        Assert.Equal(1.00015m, sma.Value);
    }

    [Fact]
    public void Reset_forgets_the_window()
    {
        SimpleMovingAverage sma = new(3);
        sma.UpdateRaw(1000m);
        sma.UpdateRaw(2000m);
        sma.UpdateRaw(3000m);

        sma.Reset();

        Assert.False(sma.HasInputs);
        Assert.False(sma.IsInitialized);
        Assert.Equal(0m, sma.Value);
        Assert.Equal(0, sma.Count);

        sma.UpdateRaw(6m);
        Assert.Equal(6m, sma.Value);
    }
}
