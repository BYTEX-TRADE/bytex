using Bytex.Indicators.Tests.Support;

namespace Bytex.Indicators.Tests;

// Granville's OBV: starts at 0; add the bar's volume when the close rises, subtract it when the close falls,
// leave it unchanged on an equal close.
public class OnBalanceVolumeTests
{
    [Fact]
    public void Values_match_a_hand_computed_table()
    {
        // close   10    11    11    9     9.5
        // volume  100   200   300   400   50
        // OBV     0     200   200   -200  -150
        OnBalanceVolume obv = new();
        (decimal Close, decimal Volume, decimal Expected)[] rows =
        [
            (10m, 100m, 0m),
            (11m, 200m, 200m),
            (11m, 300m, 200m),
            (9m, 400m, -200m),
            (9.5m, 50m, -150m),
        ];

        foreach ((decimal close, decimal volume, decimal expected) in rows)
        {
            obv.Update(Make.Bar(high: close + 1m, low: close - 1m, close: close, volume: volume));
            Assert.Equal(expected, obv.Value);
        }
    }

    [Fact]
    public void Values_match_the_naive_reference_on_the_bar_series()
    {
        decimal[] want = Reference.Obv(Series.Bars);
        OnBalanceVolume obv = new();

        for (int i = 0; i < Series.Bars.Length; i++)
        {
            obv.Update(Make.Bar(Series.Bars[i]));
            Assert.Equal(want[i], obv.Value);
        }
    }

    [Fact]
    public void Trades_use_price_as_the_close_and_size_as_the_volume()
    {
        OnBalanceVolume obv = new();

        obv.Update(Make.Trade(price: 100m, size: 7m));
        obv.Update(Make.Trade(price: 101m, size: 3m));
        obv.Update(Make.Trade(price: 100.5m, size: 1.5m));

        Assert.Equal(1.5m, obv.Value);
    }

    [Fact]
    public void The_first_bar_only_sets_the_reference_close()
    {
        OnBalanceVolume obv = new();
        Assert.False(obv.HasInputs);

        obv.Update(Make.Bar(high: 10m, low: 9m, close: 10m, volume: 12345m));

        Assert.True(obv.HasInputs);
        Assert.True(obv.IsInitialized);
        Assert.Equal(0m, obv.Value);
    }

    [Fact]
    public void A_raw_value_moves_the_reference_close_but_carries_no_volume()
    {
        OnBalanceVolume obv = new();
        obv.Update(Make.Bar(high: 11m, low: 9m, close: 10m, volume: 100m));

        obv.UpdateRaw(20m);
        Assert.Equal(0m, obv.Value);

        obv.Update(Make.Bar(high: 16m, low: 14m, close: 15m, volume: 40m)); // 15 < 20
        Assert.Equal(-40m, obv.Value);
    }

    [Fact]
    public void Reset_returns_the_running_total_to_zero()
    {
        OnBalanceVolume obv = new();
        obv.Update(Make.Bar(high: 2m, low: 1m, close: 1m, volume: 10m));
        obv.Update(Make.Bar(high: 3m, low: 2m, close: 2m, volume: 10m));

        obv.Reset();

        Assert.False(obv.HasInputs);
        Assert.False(obv.IsInitialized);
        Assert.Equal(0m, obv.Value);

        // The first bar after a reset must not be compared with the close remembered before it.
        obv.Update(Make.Bar(high: 101m, low: 99m, close: 100m, volume: 999m));
        Assert.Equal(0m, obv.Value);
    }
}
