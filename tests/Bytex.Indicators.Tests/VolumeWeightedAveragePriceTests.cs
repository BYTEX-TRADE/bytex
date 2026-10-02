using Bytex.Core.Model.Primitives;
using Bytex.Indicators.Tests.Support;

namespace Bytex.Indicators.Tests;

// VWAP = sum(price * volume) / sum(volume), accumulated per UTC day (docs: "resets daily").
// Bars contribute their typical price (H + L + C) / 3, trades their price and size,
// quotes their mid weighted by the mean of the two sizes, raw values weigh 1.
public class VolumeWeightedAveragePriceTests
{
    private static readonly UnixNanos Day1 = Make.Epoch2024;
    private static readonly UnixNanos Day2 = Make.Epoch2024.AddNanos(UnixNanos.NanosPerDay);

    [Fact]
    public void Bars_are_weighted_by_volume_using_the_typical_price()
    {
        VolumeWeightedAveragePrice vwap = new();

        vwap.Update(Make.Bar(high: 10m, low: 8m, close: 9m, volume: 100m, ts: Day1)); // typical 9
        Assert.Equal(9m, vwap.Value);

        vwap.Update(Make.Bar(high: 12m, low: 10m, close: 11m, volume: 300m, ts: Day1)); // typical 11
        Assert.Equal(10.5m, vwap.Value); // (9 * 100 + 11 * 300) / 400
    }

    [Fact]
    public void Trades_are_weighted_by_size()
    {
        VolumeWeightedAveragePrice vwap = new();

        vwap.Update(Make.Trade(price: 100m, size: 1m, ts: Day1));
        vwap.Update(Make.Trade(price: 110m, size: 4m, ts: Day1));

        Assert.Equal(108m, vwap.Value); // (100 + 440) / 5
    }

    [Fact]
    public void Quotes_contribute_the_mid_weighted_by_the_mean_size()
    {
        VolumeWeightedAveragePrice vwap = new();

        vwap.Update(Make.Quote(bid: 99m, ask: 101m, bidSize: 1m, askSize: 3m, ts: Day1)); // mid 100, weight 2
        vwap.Update(Make.Quote(bid: 105m, ask: 107m, bidSize: 6m, askSize: 6m, ts: Day1)); // mid 106, weight 6

        Assert.Equal(104.5m, vwap.Value); // (200 + 636) / 8
    }

    [Fact]
    public void Raw_values_weigh_one_each()
    {
        VolumeWeightedAveragePrice vwap = new();

        vwap.UpdateRaw(10m);
        vwap.UpdateRaw(20m);
        vwap.UpdateRaw(60m);

        Assert.Equal(30m, vwap.Value);
    }

    [Fact]
    public void Accumulation_restarts_on_a_new_UTC_day()
    {
        VolumeWeightedAveragePrice vwap = new();
        vwap.Update(Make.Trade(price: 100m, size: 50m, ts: Day1));
        vwap.Update(Make.Trade(price: 102m, size: 50m, ts: Day2.AddNanos(-1)));
        Assert.Equal(101m, vwap.Value);

        vwap.Update(Make.Trade(price: 200m, size: 1m, ts: Day2)); // 00:00:00.000000000 belongs to the new day

        Assert.Equal(200m, vwap.Value);
    }

    [Fact]
    public void Within_one_day_the_time_of_day_does_not_matter()
    {
        VolumeWeightedAveragePrice vwap = new();

        vwap.Update(Make.Trade(price: 10m, size: 1m, ts: Day1));
        vwap.Update(Make.Trade(price: 20m, size: 1m, ts: Day1.AddNanos(23 * UnixNanos.NanosPerHour)));

        Assert.Equal(15m, vwap.Value);
    }

    [Fact]
    public void With_no_volume_at_all_the_value_is_the_latest_price()
    {
        VolumeWeightedAveragePrice vwap = new();

        vwap.Update(Make.Trade(price: 55m, size: 0m, ts: Day1));
        Assert.Equal(55m, vwap.Value);

        vwap.Update(Make.Trade(price: 57m, size: 0m, ts: Day1));
        Assert.Equal(57m, vwap.Value);
    }

    [Fact]
    public void A_zero_volume_update_does_not_move_an_established_value()
    {
        VolumeWeightedAveragePrice vwap = new();
        vwap.Update(Make.Trade(price: 50m, size: 10m, ts: Day1));

        vwap.Update(Make.Trade(price: 9999m, size: 0m, ts: Day1));

        Assert.Equal(50m, vwap.Value);
    }

    [Fact]
    public void It_is_initialized_by_the_first_input()
    {
        VolumeWeightedAveragePrice vwap = new();
        Assert.False(vwap.HasInputs);
        Assert.False(vwap.IsInitialized);

        vwap.Update(Make.Trade(price: 1m, size: 1m, ts: Day1));

        Assert.True(vwap.HasInputs);
        Assert.True(vwap.IsInitialized);
    }

    [Fact]
    public void Reset_discards_the_accumulated_volume_even_within_the_same_day()
    {
        VolumeWeightedAveragePrice vwap = new();
        vwap.Update(Make.Trade(price: 1000m, size: 1000m, ts: Day1));

        vwap.Reset();

        Assert.False(vwap.HasInputs);
        Assert.False(vwap.IsInitialized);
        Assert.Equal(0m, vwap.Value);

        vwap.Update(Make.Trade(price: 10m, size: 1m, ts: Day1));
        Assert.Equal(10m, vwap.Value);
    }

    [Fact]
    public void It_matches_a_from_scratch_computation_over_the_bar_series()
    {
        VolumeWeightedAveragePrice vwap = new();
        decimal priceVolume = 0m;
        decimal volume = 0m;

        for (int i = 0; i < Series.Bars.Length; i++)
        {
            Ohlcv bar = Series.Bars[i];
            vwap.Update(Make.Bar(bar, Day1.AddNanos(i * UnixNanos.NanosPerMinute)));
            priceVolume += (bar.High + bar.Low + bar.Close) / 3m * bar.Volume;
            volume += bar.Volume;

            Check.Close(priceVolume / volume, vwap.Value, because: $"index {i}");
        }
    }
}
