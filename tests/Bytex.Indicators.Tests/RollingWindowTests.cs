using Bytex.Indicators.Tests.Support;

namespace Bytex.Indicators.Tests;

// Every windowed indicator stands on this ring buffer, so eviction order and the running sum are checked directly.
public class RollingWindowTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_capacity_below_one_is_rejected(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RollingWindow(capacity));
    }

    [Fact]
    public void An_empty_window_reports_zero_for_every_aggregate()
    {
        RollingWindow window = new(3);

        Assert.Equal(0, window.Count);
        Assert.False(window.IsFull);
        Assert.Equal(0m, window.Sum);
        Assert.Equal(0m, window.Average);
        Assert.Equal(0m, window.Latest);
        Assert.Equal(0m, window.Oldest);
        Assert.Equal(0m, window.StandardDeviation());
    }

    [Fact]
    public void It_fills_up_to_capacity_and_then_evicts_the_oldest_value_first()
    {
        RollingWindow window = new(3);

        window.Add(1m);
        window.Add(2m);
        Assert.Equal(2, window.Count);
        Assert.False(window.IsFull);

        window.Add(3m);
        Assert.True(window.IsFull);

        window.Add(4m);
        window.Add(5m);

        Assert.Equal(3, window.Count);
        Assert.Equal(new[] { 3m, 4m, 5m }, new[] { window[0], window[1], window[2] });
        Assert.Equal(3m, window.Oldest);
        Assert.Equal(5m, window.Latest);
    }

    [Fact]
    public void Sum_and_average_cover_only_the_values_still_inside_the_window()
    {
        RollingWindow window = new(2);
        window.Add(10m);
        window.Add(20m);
        window.Add(40m);

        Assert.Equal(60m, window.Sum);
        Assert.Equal(30m, window.Average);
    }

    [Fact]
    public void The_average_of_a_partially_filled_window_divides_by_the_number_of_values_present()
    {
        RollingWindow window = new(10);
        window.Add(1m);
        window.Add(2m);

        Assert.Equal(1.5m, window.Average);
    }

    [Fact]
    public void Max_and_min_forget_an_extreme_once_it_has_been_evicted()
    {
        RollingWindow window = new(3);
        foreach (decimal value in new[] { 9m, -7m, 2m, 3m, 4m })
        {
            window.Add(value);
        }

        Assert.Equal(4m, window.Max());
        Assert.Equal(2m, window.Min());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void Indexing_outside_the_stored_values_is_rejected(int index)
    {
        RollingWindow window = new(5);
        window.Add(1m);
        window.Add(2m);

        Assert.Throws<ArgumentOutOfRangeException>(() => window[index]);
    }

    [Fact]
    public void Standard_deviation_is_the_population_one()
    {
        // The classic example: mean 5, squared deviations sum to 32, 32 / 8 = 4, root 2.
        // The sample deviation (divisor 7) would be 2.138...
        RollingWindow window = new(8);
        foreach (decimal value in new[] { 2m, 4m, 4m, 4m, 5m, 5m, 7m, 9m })
        {
            window.Add(value);
        }

        Assert.Equal(2m, window.StandardDeviation());
    }

    [Fact]
    public void Clear_empties_the_window_so_old_values_cannot_leak_into_new_aggregates()
    {
        RollingWindow window = new(2);
        window.Add(100m);
        window.Add(200m);
        window.Add(300m);

        window.Clear();
        window.Add(1m);

        Assert.Equal(1, window.Count);
        Assert.Equal(1m, window.Sum);
        Assert.Equal(1m, window.Oldest);
        Assert.Equal(1m, window.Max());
        Assert.Equal(1m, window.Min());
    }

    [Fact]
    public void The_running_sum_stays_exact_after_many_evictions()
    {
        // decimal add/subtract of values with the same scale is exact, so the incremental sum may not drift.
        RollingWindow window = new(7);
        decimal[] closes = Series.Closes;
        foreach (decimal close in closes)
        {
            window.Add(close);
        }

        Assert.Equal(closes.TakeLast(7).Sum(), window.Sum);
    }
}
