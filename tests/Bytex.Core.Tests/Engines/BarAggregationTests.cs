using Bytex.Core.Engines;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Tests.Support;
using Bytex.Core.Timing;

namespace Bytex.Core.Tests.Engines;

// Why: bars drive most strategies, so OHLCV must be exact. Every expected bar below is worked out by hand
// from the input ticks; the instrument is BTC/USDT with 2 price decimals and 3 size decimals.
public class BarAggregationTests
{
    private static UnixNanos At(int minutes, int seconds) => TestOrders.T0 + new TimeSpan(0, minutes, seconds);

    private static BarType Type(string spec) => BarType.Parse($"BTCUSDT.BINANCE-{spec}-INTERNAL");

    private static TradeTick Trade(string price, string size, UnixNanos ts, int n = 0) =>
        new(TestIds.BtcUsdt, Price.Parse(price), Quantity.Parse(size), AggressorSide.Buyer, new TradeId($"T-{n}"), ts, ts);

    private static QuoteTick Quote(string bid, string ask, string bidSize, string askSize, UnixNanos ts) =>
        new(TestIds.BtcUsdt, Price.Parse(bid), Price.Parse(ask), Quantity.Parse(bidSize), Quantity.Parse(askSize), ts, ts);

    private static void AssertBar(Bar bar, string open, string high, string low, string close, string volume, UnixNanos tsEvent)
    {
        Assert.Equal(Price.Parse(open), bar.Open);
        Assert.Equal(Price.Parse(high), bar.High);
        Assert.Equal(Price.Parse(low), bar.Low);
        Assert.Equal(Price.Parse(close), bar.Close);
        Assert.Equal(Quantity.Parse(volume), bar.Volume);
        Assert.Equal(tsEvent, bar.TsEvent);
    }

    [Fact]
    public void Tick_bar_closes_on_the_nth_tick_with_the_ohlcv_of_exactly_those_ticks()
    {
        List<Bar> bars = new();
        TestClock clock = new(At(0, 0));
        TickBarAggregator aggregator = new(TestInstruments.BtcUsdt(), Type("3-TICK-LAST"), bars.Add, clock);

        aggregator.HandleTradeTick(Trade("100.00", "1.000", At(0, 1)));
        aggregator.HandleTradeTick(Trade("102.00", "2.000", At(0, 2)));
        Assert.Empty(bars);
        aggregator.HandleTradeTick(Trade("99.00", "3.000", At(0, 3)));

        Bar bar = Assert.Single(bars);
        AssertBar(bar, "100.00", "102.00", "99.00", "99.00", "6.000", At(0, 3));
        Assert.Equal(Type("3-TICK-LAST"), bar.BarType);
        Assert.Equal(At(0, 0), bar.TsInit);
    }

    [Fact]
    public void Tick_bar_starts_afresh_after_each_close()
    {
        List<Bar> bars = new();
        TickBarAggregator aggregator = new(TestInstruments.BtcUsdt(), Type("2-TICK-LAST"), bars.Add, new TestClock());

        aggregator.HandleTradeTick(Trade("100.00", "1.000", At(0, 1)));
        aggregator.HandleTradeTick(Trade("101.00", "1.000", At(0, 2)));
        aggregator.HandleTradeTick(Trade("50.00", "0.500", At(0, 3)));
        aggregator.HandleTradeTick(Trade("51.00", "0.250", At(0, 4)));

        Assert.Equal(2, bars.Count);
        // The second bar must not remember the first bar's high of 101 or its volume.
        AssertBar(bars[1], "50.00", "51.00", "50.00", "51.00", "0.750", At(0, 4));
    }

    [Theory]
    [InlineData("BID", "100.00", "99.00", "2.000")] // bid prices 100.00 then 99.00, bid sizes 1 + 1
    [InlineData("ASK", "100.04", "99.04", "6.000")] // ask sizes 3 + 3
    [InlineData("MID", "100.02", "99.02", "4.000")] // (100.00 + 100.04) / 2; sizes (1 + 3) / 2 per quote
    public void Quote_bars_use_the_price_and_size_of_the_configured_price_type(string priceType, string open, string close, string volume)
    {
        List<Bar> bars = new();
        TickBarAggregator aggregator = new(TestInstruments.BtcUsdt(), Type($"2-TICK-{priceType}"), bars.Add, new TestClock());

        aggregator.HandleQuoteTick(Quote("100.00", "100.04", "1.000", "3.000", At(0, 1)));
        aggregator.HandleQuoteTick(Quote("99.00", "99.04", "1.000", "3.000", At(0, 2)));

        AssertBar(Assert.Single(bars), open, open, close, close, volume, At(0, 2));
    }

    [Fact]
    public void Volume_bar_closes_when_the_step_is_reached_and_carries_the_excess_into_the_next_bar()
    {
        List<Bar> bars = new();
        VolumeBarAggregator aggregator = new(TestInstruments.BtcUsdt(), Type("10-VOLUME-LAST"), bars.Add, new TestClock());

        aggregator.HandleTradeTick(Trade("100.00", "4.000", At(0, 1)));
        aggregator.HandleTradeTick(Trade("101.00", "3.000", At(0, 2)));
        aggregator.HandleTradeTick(Trade("99.00", "5.000", At(0, 3))); // 3 complete bar one, 2 start bar two
        aggregator.HandleTradeTick(Trade("102.00", "8.000", At(0, 4))); // 2 + 8 = 10 completes bar two

        Assert.Equal(2, bars.Count);
        AssertBar(bars[0], "100.00", "101.00", "99.00", "99.00", "10.000", At(0, 3));
        AssertBar(bars[1], "99.00", "102.00", "99.00", "102.00", "10.000", At(0, 4));
    }

    [Fact]
    public void Volume_bar_splits_one_large_trade_across_several_bars()
    {
        List<Bar> bars = new();
        VolumeBarAggregator aggregator = new(TestInstruments.BtcUsdt(), Type("10-VOLUME-LAST"), bars.Add, new TestClock());

        aggregator.HandleTradeTick(Trade("100.00", "25.000", At(0, 1))); // 10 + 10 + 5 left over
        aggregator.HandleTradeTick(Trade("101.00", "5.000", At(0, 2))); // 5 + 5 = 10

        Assert.Equal(3, bars.Count);
        AssertBar(bars[0], "100.00", "100.00", "100.00", "100.00", "10.000", At(0, 1));
        AssertBar(bars[1], "100.00", "100.00", "100.00", "100.00", "10.000", At(0, 1));
        AssertBar(bars[2], "100.00", "101.00", "100.00", "101.00", "10.000", At(0, 2));
    }

    [Fact]
    public void Volume_bar_closes_on_a_trade_that_hits_the_step_exactly()
    {
        List<Bar> bars = new();
        VolumeBarAggregator aggregator = new(TestInstruments.BtcUsdt(), Type("10-VOLUME-LAST"), bars.Add, new TestClock());

        aggregator.HandleTradeTick(Trade("100.00", "10.000", At(0, 1)));
        aggregator.HandleTradeTick(Trade("101.00", "9.999", At(0, 2)));

        AssertBar(Assert.Single(bars), "100.00", "100.00", "100.00", "100.00", "10.000", At(0, 1));
    }

    [Fact]
    public void Value_bar_closes_on_traded_value_and_splits_a_trade_by_value()
    {
        List<Bar> bars = new();
        ValueBarAggregator aggregator = new(TestInstruments.BtcUsdt(), Type("1000-VALUE-LAST"), bars.Add, new TestClock());

        // 0.030 * 50,000 = 1,500: the first 1,000 (0.020) closes bar one, 500 (0.010) carries over.
        aggregator.HandleTradeTick(Trade("50000.00", "0.030", At(0, 1)));
        // 0.020 * 25,000 = 500: 500 + 500 = 1,000 closes bar two with volume 0.010 + 0.020.
        aggregator.HandleTradeTick(Trade("25000.00", "0.020", At(0, 2)));

        Assert.Equal(2, bars.Count);
        AssertBar(bars[0], "50000.00", "50000.00", "50000.00", "50000.00", "0.020", At(0, 1));
        AssertBar(bars[1], "50000.00", "50000.00", "25000.00", "25000.00", "0.030", At(0, 2));
    }

    [Fact]
    public void Value_bar_stays_open_below_the_step()
    {
        List<Bar> bars = new();
        ValueBarAggregator aggregator = new(TestInstruments.BtcUsdt(), Type("1000-VALUE-LAST"), bars.Add, new TestClock());

        aggregator.HandleTradeTick(Trade("999.99", "1.000", At(0, 1)));

        Assert.Empty(bars);
    }

    [Fact]
    public void Time_bar_closes_on_the_interval_boundary_and_is_stamped_with_the_close_time()
    {
        List<Bar> bars = new();
        TestClock clock = new(At(0, 30));
        TimeBarAggregator aggregator = new(TestInstruments.BtcUsdt(), Type("1-MINUTE-LAST"), bars.Add, clock);
        aggregator.Start();

        aggregator.HandleTradeTick(Trade("100.00", "1.000", At(0, 40)));
        aggregator.HandleTradeTick(Trade("101.50", "2.000", At(0, 50)));
        clock.AdvanceAndRun(At(0, 59));
        Assert.Empty(bars);
        clock.AdvanceAndRun(At(1, 0));

        Bar bar = Assert.Single(bars);
        AssertBar(bar, "100.00", "101.50", "100.00", "101.50", "3.000", At(1, 0));
        Assert.Equal(At(1, 0), bar.TsInit);
    }

    [Fact]
    public void Time_bar_can_be_stamped_with_the_interval_open_instead()
    {
        List<Bar> bars = new();
        TestClock clock = new(At(0, 30));
        TimeBarAggregator aggregator = new(TestInstruments.BtcUsdt(), Type("1-MINUTE-LAST"), bars.Add, clock, timestampOnClose: false);
        aggregator.Start();

        aggregator.HandleTradeTick(Trade("100.00", "1.000", At(0, 40)));
        clock.AdvanceAndRun(At(1, 0));

        Assert.Equal(At(0, 0), Assert.Single(bars).TsEvent);
    }

    [Fact]
    public void Time_bar_started_exactly_on_a_boundary_closes_one_full_interval_later()
    {
        List<Bar> bars = new();
        TestClock clock = new(At(1, 0));
        TimeBarAggregator aggregator = new(TestInstruments.BtcUsdt(), Type("1-MINUTE-LAST"), bars.Add, clock);
        aggregator.Start();

        Assert.Equal(At(2, 0), clock.NextTime("bar-BTCUSDT.BINANCE-1-MINUTE-LAST-INTERNAL"));
    }

    [Fact]
    public void Time_bars_separate_ticks_by_interval()
    {
        List<Bar> bars = new();
        TestClock clock = new(At(0, 0));
        TimeBarAggregator aggregator = new(TestInstruments.BtcUsdt(), Type("1-MINUTE-LAST"), bars.Add, clock);
        aggregator.Start();

        aggregator.HandleTradeTick(Trade("100.00", "1.000", At(0, 10)));
        clock.AdvanceAndRun(At(1, 0));
        aggregator.HandleTradeTick(Trade("105.00", "2.000", At(1, 10)));
        aggregator.HandleTradeTick(Trade("103.00", "2.000", At(1, 20)));
        clock.AdvanceAndRun(At(2, 0));

        Assert.Equal(2, bars.Count);
        AssertBar(bars[0], "100.00", "100.00", "100.00", "100.00", "1.000", At(1, 0));
        AssertBar(bars[1], "105.00", "105.00", "103.00", "103.00", "4.000", At(2, 0));
    }

    [Fact]
    public void Empty_intervals_produce_no_bars_by_default()
    {
        List<Bar> bars = new();
        TestClock clock = new(At(0, 0));
        TimeBarAggregator aggregator = new(TestInstruments.BtcUsdt(), Type("1-MINUTE-LAST"), bars.Add, clock);
        aggregator.Start();
        aggregator.HandleTradeTick(Trade("100.00", "1.000", At(0, 10)));

        clock.AdvanceAndRun(At(3, 30)); // boundaries at 1:00, 2:00 and 3:00; only the first interval had a tick

        Assert.Equal(At(1, 0), Assert.Single(bars).TsEvent);
    }

    [Fact]
    public void Empty_intervals_repeat_the_last_close_with_zero_volume_when_configured()
    {
        List<Bar> bars = new();
        TestClock clock = new(At(0, 0));
        TimeBarAggregator aggregator = new(TestInstruments.BtcUsdt(), Type("1-MINUTE-LAST"), bars.Add, clock, buildWithNoUpdates: true);
        aggregator.Start();
        aggregator.HandleTradeTick(Trade("100.00", "1.000", At(0, 10)));
        aggregator.HandleTradeTick(Trade("101.00", "1.000", At(0, 20)));

        clock.AdvanceAndRun(At(3, 30));

        Assert.Equal(3, bars.Count);
        AssertBar(bars[1], "101.00", "101.00", "101.00", "101.00", "0.000", At(2, 0));
        AssertBar(bars[2], "101.00", "101.00", "101.00", "101.00", "0.000", At(3, 0));
    }

    [Fact]
    public void No_bar_is_invented_before_the_first_tick_even_when_empty_bars_are_enabled()
    {
        List<Bar> bars = new();
        TestClock clock = new(At(0, 0));
        TimeBarAggregator aggregator = new(TestInstruments.BtcUsdt(), Type("1-MINUTE-LAST"), bars.Add, clock, buildWithNoUpdates: true);
        aggregator.Start();

        clock.AdvanceAndRun(At(5, 0));

        Assert.Empty(bars);
    }

    [Fact]
    public void Time_aggregator_starts_its_timer_on_the_first_tick_if_not_started_explicitly()
    {
        List<Bar> bars = new();
        TestClock clock = new(At(0, 30));
        TimeBarAggregator aggregator = new(TestInstruments.BtcUsdt(), Type("1-MINUTE-LAST"), bars.Add, clock);

        aggregator.HandleTradeTick(Trade("100.00", "1.000", At(0, 30)));
        clock.AdvanceAndRun(At(1, 0));

        Assert.Single(bars);
    }

    [Fact]
    public void Stop_cancels_the_timer_so_no_further_bars_are_emitted()
    {
        List<Bar> bars = new();
        TestClock clock = new(At(0, 0));
        TimeBarAggregator aggregator = new(TestInstruments.BtcUsdt(), Type("1-MINUTE-LAST"), bars.Add, clock);
        aggregator.Start();
        aggregator.HandleTradeTick(Trade("100.00", "1.000", At(0, 10)));

        aggregator.Stop();
        clock.AdvanceAndRun(At(2, 0));

        Assert.Equal(0, clock.TimerCount);
        Assert.Empty(bars);
    }

    [Fact]
    public void Time_aggregator_composes_smaller_bars_into_a_larger_bar()
    {
        List<Bar> bars = new();
        TestClock clock = new(At(0, 0));
        TimeBarAggregator aggregator = new(TestInstruments.BtcUsdt(), Type("5-MINUTE-LAST"), bars.Add, clock);
        BarType oneMinute = BarType.Parse("BTCUSDT.BINANCE-1-MINUTE-LAST-EXTERNAL");
        (string O, string H, string L, string C, string V)[] minutes =
        [
            ("100.00", "101.00", "99.50", "100.50", "1.000"),
            ("100.50", "103.00", "100.25", "102.00", "2.000"),
            ("102.00", "102.50", "98.00", "98.50", "3.000"),
            ("98.50", "99.00", "98.25", "98.75", "4.000"),
            ("98.75", "100.00", "98.50", "99.25", "5.000"),
        ];

        for (int i = 0; i < minutes.Length; i++)
        {
            (string o, string hi, string lo, string c, string v) = minutes[i];
            aggregator.HandleBar(new Bar(oneMinute, Price.Parse(o), Price.Parse(hi), Price.Parse(lo), Price.Parse(c), Quantity.Parse(v), At(i + 1, 0), At(i + 1, 0)));
        }

        clock.AdvanceAndRun(At(5, 0));

        // open of the first, highest high, lowest low, close of the last, 1+2+3+4+5 volume
        AssertBar(Assert.Single(bars), "100.00", "103.00", "98.00", "99.25", "15.000", At(5, 0));
    }

    [Fact]
    public void Time_aggregator_refuses_a_specification_that_is_not_time_based()
    {
        Assert.Throws<ArgumentException>(() => new TimeBarAggregator(TestInstruments.BtcUsdt(), Type("100-TICK-LAST"), _ => { }, new TestClock()));
    }

    [Fact]
    public void Bar_builder_reports_nothing_to_build_until_it_has_seen_a_price()
    {
        BarBuilder builder = new(Type("1-MINUTE-LAST"), 2, 3);

        Assert.False(builder.IsInitialized);
        builder.Update(Price.Parse("100.00"), Quantity.Parse("1.000"), At(0, 1));

        Assert.True(builder.IsInitialized);
        Assert.Equal(1, builder.Count);
        Assert.Equal(At(0, 1), builder.TsLast);
    }
}
