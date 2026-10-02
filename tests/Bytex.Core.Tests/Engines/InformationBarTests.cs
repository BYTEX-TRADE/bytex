using Bytex.Core.Engines;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Tests.Support;
using Bytex.Core.Timing;

namespace Bytex.Core.Tests.Engines;

// Why (R2.12): three imbalance aggregations were NAMED in the model and refused by the engine, which is the worst of the
// three possible states - a caller could write the bar type down, and asking for it threw. They are built here, with runs
// bars beside them.
//
// What is pinned: how an update is signed (the venue's own word first, the tick rule second), that an imbalance cancels
// and a run does not, that an update is never split across two bars, and that the threshold is the one the caller stated
// rather than an estimate - which is a deliberate departure from the published form and is checked as such.
public class InformationBarTests
{
    private static readonly Instrument _instrument = TestInstruments.BtcUsdt();

    private static CandleSeries Type(SamplingMethod aggregation, int step) =>
        new(_instrument.Id, new SamplingRule(step, aggregation, PriceType.Last), CandleOrigin.Computed);

    private static (BarAggregator Aggregator, List<Bar> Bars) Build(SamplingMethod aggregation, int step)
    {
        List<Bar> bars = new();
        TestClock clock = new(TestOrders.T0);
        CandleSeries candleSeries = Type(aggregation, step);
        BarAggregator aggregator = aggregation.IsInformationBased() && aggregation is SamplingMethod.TickRuns or SamplingMethod.VolumeRuns or SamplingMethod.ValueRuns
            ? new RunsBarAggregator(_instrument, candleSeries, bars.Add, clock)
            : new ImbalanceBarAggregator(_instrument, candleSeries, bars.Add, clock);
        return (aggregator, bars);
    }

    private static TradeTick Trade(decimal price, decimal size, AggressorSide side, long seconds = 0) => new(
        _instrument.Id,
        _instrument.MakePrice(price),
        _instrument.MakeQuantity(size),
        side,
        new TradeId("T" + seconds),
        TestOrders.T0.AddNanos(seconds * UnixNanos.NanosPerSecond),
        TestOrders.T0.AddNanos(seconds * UnixNanos.NanosPerSecond));

    // ----- imbalance -----

    [Fact]
    public void A_tick_imbalance_bar_closes_when_one_side_has_outweighed_the_other_by_the_threshold()
    {
        // Three buys and one sell leave an imbalance of two, which is the threshold.
        (BarAggregator aggregator, List<Bar> bars) = Build(SamplingMethod.TickImbalance, 2);

        aggregator.HandleTradeTick(Trade(100m, 1m, AggressorSide.Buyer, 1));
        Assert.Empty(bars);
        aggregator.HandleTradeTick(Trade(100m, 1m, AggressorSide.Seller, 2));
        Assert.Empty(bars);
        aggregator.HandleTradeTick(Trade(100m, 1m, AggressorSide.Buyer, 3));
        Assert.Empty(bars);

        aggregator.HandleTradeTick(Trade(100m, 1m, AggressorSide.Buyer, 4));

        Bar bar = Assert.Single(bars);
        Assert.Equal(4m, bar.Volume.Value);
    }

    [Fact]
    public void Buys_and_sells_cancel_in_an_imbalance_bar_however_much_of_them_there_is()
    {
        // The point of the kind: a market being worked in both directions samples no information, so no bar appears.
        (BarAggregator aggregator, List<Bar> bars) = Build(SamplingMethod.VolumeImbalance, 10);

        for (int i = 0; i < 20; i++)
        {
            aggregator.HandleTradeTick(Trade(100m, 5m, i % 2 == 0 ? AggressorSide.Buyer : AggressorSide.Seller, i));
        }

        Assert.Empty(bars);
        Assert.Equal(0m, ((ImbalanceBarAggregator)aggregator).Imbalance);
    }

    [Fact]
    public void A_volume_imbalance_bar_counts_size_and_a_value_imbalance_bar_counts_value()
    {
        (BarAggregator volume, List<Bar> volumeBars) = Build(SamplingMethod.VolumeImbalance, 3);
        (BarAggregator value, List<Bar> valueBars) = Build(SamplingMethod.ValueImbalance, 300);

        volume.HandleTradeTick(Trade(100m, 2m, AggressorSide.Buyer, 1));
        Assert.Empty(volumeBars);
        volume.HandleTradeTick(Trade(100m, 1m, AggressorSide.Buyer, 2));
        Assert.Single(volumeBars);

        // 2 at 100 is 200 of value, which is not yet 300.
        value.HandleTradeTick(Trade(100m, 2m, AggressorSide.Buyer, 1));
        Assert.Empty(valueBars);
        value.HandleTradeTick(Trade(100m, 1m, AggressorSide.Buyer, 2));
        Assert.Single(valueBars);
    }

    [Fact]
    public void A_sellers_imbalance_closes_a_bar_as_readily_as_a_buyers()
    {
        // It is the size of the imbalance that matters, not its direction.
        (BarAggregator aggregator, List<Bar> bars) = Build(SamplingMethod.TickImbalance, 2);

        aggregator.HandleTradeTick(Trade(100m, 1m, AggressorSide.Seller, 1));
        aggregator.HandleTradeTick(Trade(100m, 1m, AggressorSide.Seller, 2));

        Assert.Single(bars);
    }

    [Fact]
    public void An_update_is_not_split_across_two_bars()
    {
        // A volume bar can take half a trade, because half a size is a size. Half a signed trade is not one: the sign
        // belongs to the whole of it. So the whole trade is in the bar it closed.
        (BarAggregator aggregator, List<Bar> bars) = Build(SamplingMethod.VolumeImbalance, 3);

        aggregator.HandleTradeTick(Trade(100m, 10m, AggressorSide.Buyer, 1));

        Bar bar = Assert.Single(bars);
        Assert.Equal(10m, bar.Volume.Value);
        Assert.Equal(0m, ((ImbalanceBarAggregator)aggregator).Imbalance);
    }

    // ----- runs -----

    [Fact]
    public void A_runs_bar_closes_on_one_sides_flow_alone()
    {
        // Where an imbalance bar would have seen nothing: both sides traded heavily and neither is subtracted from the
        // other.
        (BarAggregator runs, List<Bar> runsBars) = Build(SamplingMethod.VolumeRuns, 10);
        (BarAggregator imbalance, List<Bar> imbalanceBars) = Build(SamplingMethod.VolumeImbalance, 10);

        for (int i = 0; i < 4; i++)
        {
            TradeTick trade = Trade(100m, 5m, i % 2 == 0 ? AggressorSide.Buyer : AggressorSide.Seller, i);
            runs.HandleTradeTick(trade);
            imbalance.HandleTradeTick(trade);
        }

        Assert.Single(runsBars);
        Assert.Empty(imbalanceBars);
    }

    [Fact]
    public void A_runs_bar_resets_both_sides_when_it_closes()
    {
        (BarAggregator aggregator, List<Bar> bars) = Build(SamplingMethod.TickRuns, 2);

        aggregator.HandleTradeTick(Trade(100m, 1m, AggressorSide.Buyer, 1));
        aggregator.HandleTradeTick(Trade(100m, 1m, AggressorSide.Buyer, 2));
        Assert.Single(bars);

        RunsBarAggregator runs = (RunsBarAggregator)aggregator;
        Assert.Equal(0m, runs.Buys);
        Assert.Equal(0m, runs.Sells);

        aggregator.HandleTradeTick(Trade(100m, 1m, AggressorSide.Seller, 3));
        Assert.Equal(1m, runs.Sells);
        Assert.Single(bars);
    }

    // ----- how an update is signed -----

    [Fact]
    public void The_venues_own_word_on_who_took_the_liquidity_is_used_where_there_is_one()
    {
        // A trade that lifted the offer at an unchanged price is a buy, and the tick rule cannot see that.
        (BarAggregator aggregator, List<Bar> bars) = Build(SamplingMethod.TickImbalance, 3);

        for (int i = 0; i < 3; i++)
        {
            aggregator.HandleTradeTick(Trade(100m, 1m, AggressorSide.Buyer, i));
        }

        Assert.Single(bars);
    }

    [Fact]
    public void Without_an_aggressor_the_sign_is_the_direction_the_price_moved()
    {
        // Quotes carry no aggressor, so the tick rule is what is left: up is a buy, down is a sell.
        (BarAggregator aggregator, List<Bar> bars) = Build(SamplingMethod.TickImbalance, 3);

        aggregator.HandleQuoteTick(Quote(100m, 1m, 1));   // the first update counts as a buy by convention
        aggregator.HandleQuoteTick(Quote(101m, 1m, 2));   // up: a buy
        aggregator.HandleQuoteTick(Quote(102m, 1m, 3));   // up: a buy

        Assert.Single(bars);
    }

    [Fact]
    public void A_price_that_did_not_move_carries_the_last_sign_forward()
    {
        // Which is the tick rule as defined: an unchanged price is not a change of mind.
        (BarAggregator aggregator, List<Bar> bars) = Build(SamplingMethod.TickImbalance, 3);

        aggregator.HandleQuoteTick(Quote(100m, 1m, 1));   // buy by convention:      +1
        aggregator.HandleQuoteTick(Quote(99m, 1m, 2));    // down, a sell:             0
        aggregator.HandleQuoteTick(Quote(99m, 1m, 3));    // unchanged, still a sell: -1
        aggregator.HandleQuoteTick(Quote(99m, 1m, 4));    // unchanged, still a sell: -2

        // The direction is the point: an unchanged price read as a buy would be at +2 here, and would close a bar on the
        // same update in the opposite direction - which counting bars cannot tell apart.
        Assert.Equal(-2m, ((ImbalanceBarAggregator)aggregator).Imbalance);
        Assert.Empty(bars);

        aggregator.HandleQuoteTick(Quote(99m, 1m, 5));    // unchanged, still a sell: -3

        Assert.Single(bars);
    }

    [Fact]
    public void A_trade_after_a_quote_is_signed_by_its_own_aggressor_rather_than_by_the_quotes_price()
    {
        // The aggressor is kept only for the update it belongs to; nothing is carried into the next one.
        (BarAggregator aggregator, List<Bar> bars) = Build(SamplingMethod.TickImbalance, 2);

        aggregator.HandleQuoteTick(Quote(100m, 1m, 1));                        // buy by convention:            +1
        aggregator.HandleTradeTick(Trade(105m, 1m, AggressorSide.Seller, 2));  // a sell despite the higher price: 0
        Assert.Empty(bars);

        // And the trade's aggressor belongs to the trade alone: this quote is higher than the last price, so the tick
        // rule makes it a buy. A stale aggressor would make it a sell and leave the imbalance at -1.
        aggregator.HandleQuoteTick(Quote(110m, 1m, 3));

        Assert.Equal(1m, ((ImbalanceBarAggregator)aggregator).Imbalance);
    }

    // ----- what it refuses -----

    [Fact]
    public void An_aggregation_that_does_not_measure_flow_cannot_be_aggregated_this_way()
    {
        List<Bar> bars = new();
        TestClock clock = new(TestOrders.T0);

        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new ImbalanceBarAggregator(_instrument, Type(SamplingMethod.Minute, 1), bars.Add, clock));

        Assert.Contains("does not measure flow", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_threshold_of_nothing_is_refused()
    {
        // Every update would close a bar, and a bar per update is a tick bar with a misleading name.
        List<Bar> bars = new();
        TestClock clock = new(TestOrders.T0);

        Assert.Throws<ArgumentException>(
            () => new ImbalanceBarAggregator(_instrument, Type(SamplingMethod.TickImbalance, 0), bars.Add, clock));
    }

    [Fact]
    public void The_threshold_is_the_one_the_candle_series_states()
    {
        // The published form closes on an EXPECTATION of the imbalance, estimated from past bars. This closes on the
        // number in the bar type, so the same data produces the same bars in every run - which an estimator's state
        // cannot promise, and which no bar type could carry.
        (BarAggregator five, List<Bar> fiveBars) = Build(SamplingMethod.TickImbalance, 5);
        (BarAggregator two, List<Bar> twoBars) = Build(SamplingMethod.TickImbalance, 2);

        for (int i = 0; i < 10; i++)
        {
            TradeTick trade = Trade(100m, 1m, AggressorSide.Buyer, i);
            five.HandleTradeTick(trade);
            two.HandleTradeTick(trade);
        }

        Assert.Equal(2, fiveBars.Count);
        Assert.Equal(5, twoBars.Count);
    }

    [Fact]
    public void A_candle_series_of_one_of_these_kinds_is_written_and_read_back()
    {
        // The canonical form is the name in capitals; an underscore between the words is accepted too, because that is
        // how anybody writing one by hand writes it.
        CandleSeries canonical = CandleSeries.Parse("bx-candle:v2/BINANCE/BTCUSDT/volumeimbalance/500/last/computed");

        Assert.Equal(SamplingMethod.VolumeImbalance, canonical.Spec.Aggregation);
        Assert.Equal("bx-candle:v2/BINANCE/BTCUSDT/volumeimbalance/500/last/computed", canonical.ToString());
        Assert.Equal(canonical, CandleSeries.Parse("bx-candle:v2/BINANCE/BTCUSDT/volumeimbalance/500/last/computed"));
        Assert.Equal(SamplingMethod.ValueRuns, CandleSeries.Parse("bx-candle:v2/BINANCE/BTCUSDT/valueruns/5/last/computed").Spec.Aggregation);
    }

    [Fact]
    public void The_model_says_which_kinds_measure_flow_and_what_they_count_it_in()
    {
        Assert.True(SamplingMethod.TickImbalance.IsInformationBased());
        Assert.True(SamplingMethod.ValueRuns.IsInformationBased());
        Assert.False(SamplingMethod.Minute.IsInformationBased());
        Assert.False(SamplingMethod.Volume.IsInformationBased());

        Assert.Equal(SamplingMethod.Tick, SamplingMethod.TickRuns.FlowUnit());
        Assert.Equal(SamplingMethod.Volume, SamplingMethod.VolumeImbalance.FlowUnit());
        Assert.Equal(SamplingMethod.Value, SamplingMethod.ValueRuns.FlowUnit());
        Assert.Throws<InvalidOperationException>(() => SamplingMethod.Minute.FlowUnit());
    }

    private static QuoteTick Quote(decimal price, decimal size, long seconds) => new(
        _instrument.Id,
        _instrument.MakePrice(price),
        _instrument.MakePrice(price),
        _instrument.MakeQuantity(size),
        _instrument.MakeQuantity(size),
        TestOrders.T0.AddNanos(seconds * UnixNanos.NanosPerSecond),
        TestOrders.T0.AddNanos(seconds * UnixNanos.NanosPerSecond));
}
