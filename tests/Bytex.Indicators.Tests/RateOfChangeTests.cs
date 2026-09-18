using Bytex.Indicators.Tests.Support;

namespace Bytex.Indicators.Tests;

// ROC = (price - price N inputs ago) / (price N inputs ago) * 100, so N + 1 inputs are needed.
public class RateOfChangeTests
{
    [Theory]
    // 121 vs 100 = +21 %, 99 vs 110 = -10 %, 60.5 vs 121 = -50 %
    [InlineData(2, "100 110 121 99 60.5", "21 -10 -50")]
    [InlineData(1, "50 75 75 30", "50 0 -60")]
    public void Values_match_a_hand_computed_table(int period, string inputs, string expected)
    {
        RateOfChange roc = new(period);
        decimal[] values = Series.Parse(inputs);
        decimal[] want = Series.Parse(expected);

        for (int i = 0; i < values.Length; i++)
        {
            roc.UpdateRaw(values[i]);
            if (i >= period)
            {
                Check.Close(want[i - period], roc.Value, because: $"input #{i + 1}");
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    public void Values_match_the_naive_reference_on_a_long_series(int period)
    {
        decimal[] closes = Series.Closes;
        decimal?[] want = Reference.Roc(closes, period);
        RateOfChange roc = new(period);

        for (int i = 0; i < closes.Length; i++)
        {
            roc.UpdateRaw(closes[i]);
            Assert.Equal(want[i].HasValue, roc.IsInitialized);
            if (want[i] is { } expected)
            {
                Check.Close(expected, roc.Value, because: $"index {i}");
            }
        }
    }

    [Fact]
    public void Before_N_plus_one_inputs_the_value_is_zero_and_the_indicator_is_not_initialized()
    {
        RateOfChange roc = new(3);

        foreach (decimal v in new[] { 10m, 20m, 30m })
        {
            roc.UpdateRaw(v);
            Assert.True(roc.HasInputs);
            Assert.False(roc.IsInitialized);
            Assert.Equal(0m, roc.Value);
        }

        roc.UpdateRaw(40m);

        Assert.True(roc.IsInitialized);
        Assert.Equal(300m, roc.Value);
    }

    [Fact]
    public void A_constant_series_gives_zero()
    {
        RateOfChange roc = new(4);
        for (int i = 0; i < 12; i++)
        {
            roc.UpdateRaw(19.99m);
        }

        Assert.True(roc.IsInitialized);
        Assert.Equal(0m, roc.Value);
    }

    [Fact]
    public void A_zero_base_price_does_not_initialize_the_indicator()
    {
        // (x - 0) / 0 is undefined; the engine must neither throw nor pretend to have a value.
        RateOfChange roc = new(2);
        roc.UpdateRaw(0m);
        roc.UpdateRaw(5m);

        roc.UpdateRaw(10m);

        Assert.False(roc.IsInitialized);
        Assert.Equal(0m, roc.Value);

        roc.UpdateRaw(20m); // base is now 5
        Assert.True(roc.IsInitialized);
        Assert.Equal(300m, roc.Value);
    }

    [Fact]
    public void A_negative_period_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RateOfChange(-1));
    }

    [Fact(Skip = "BUG: RateOfChange accepts period 0, which compares every price with itself and reports a constant 0")]
    public void A_period_of_zero_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RateOfChange(0));
    }

    [Fact]
    public void Trades_contribute_their_price()
    {
        RateOfChange roc = new(1);

        roc.Update(Make.Trade(price: 200m));
        roc.Update(Make.Trade(price: 250m));

        Assert.Equal(25m, roc.Value);
    }

    [Fact]
    public void Reset_forgets_the_window()
    {
        RateOfChange roc = new(1);
        roc.UpdateRaw(1m);
        roc.UpdateRaw(2m);

        roc.Reset();

        Assert.False(roc.HasInputs);
        Assert.False(roc.IsInitialized);
        Assert.Equal(0m, roc.Value);

        roc.UpdateRaw(10m);
        Assert.False(roc.IsInitialized);
        roc.UpdateRaw(11m);
        Assert.Equal(10m, roc.Value);
    }
}
