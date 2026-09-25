using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;

namespace Bytex.Backtest.Tests;

// Why: the engine is the event loop. It owns simulated time: data is replayed in timestamp order, timers fire
// between data events at their own simulated time, and nothing outside the requested range is seen by anyone.
public sealed class BacktestEngineTests
{
    [Fact]
    public void Run_without_data_is_refused()
    {
        using BacktestEngine engine = new();
        engine.AddVenue(new SimulatedVenueConfig { Venue = TestInstruments.Sim });

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => engine.Run());
        Assert.Contains("No data", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_without_a_venue_is_refused()
    {
        using BacktestEngine engine = new();
        engine.AddData([Scripted.Quote(TestInstruments.Spot(), 1000, 100m, 100.10m)]);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => engine.Run());
        Assert.Contains("No venues", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Adding_the_same_venue_twice_is_refused()
    {
        using BacktestEngine engine = new();
        engine.AddVenue(new SimulatedVenueConfig { Venue = TestInstruments.Sim });

        Assert.Throws<InvalidOperationException>(() => engine.AddVenue(new SimulatedVenueConfig { Venue = TestInstruments.Sim }));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Instrument_reaches_its_venue_whether_it_is_added_before_or_after_the_venue(bool instrumentFirst)
    {
        using BacktestEngine engine = new();
        CurrencyPair spot = TestInstruments.Spot();
        if (instrumentFirst)
        {
            engine.AddInstrument(spot);
        }

        SimulatedExchange exchange = engine.AddVenue(new SimulatedVenueConfig { Venue = TestInstruments.Sim });
        if (!instrumentFirst)
        {
            engine.AddInstrument(spot);
        }

        Assert.Same(spot, exchange.Instruments[spot.Id]);
        Assert.Same(spot, engine.Cache.Instrument(spot.Id));
    }

    [Fact]
    public void Venue_refuses_an_instrument_that_belongs_to_another_venue()
    {
        using BacktestEngine engine = new();
        SimulatedExchange exchange = engine.AddVenue(new SimulatedVenueConfig { Venue = TestInstruments.Sim });

        Assert.Throws<ArgumentException>(() => exchange.AddInstrument(TestInstruments.EthSpot(TestInstruments.Alt)));
    }

    [Fact]
    public void Data_added_out_of_order_is_replayed_in_timestamp_order()
    {
        using SimHarness sim = SimHarness.Spot();
        sim.Quote(3000, 103.00m, 103.10m)
            .Quote(1000, 101.00m, 101.10m)
            .Quote(2000, 102.00m, 102.10m)
            .Run();

        Assert.Equal(
            ["1000|quote BTCUSDT 101.00/101.10", "2000|quote BTCUSDT 102.00/102.10", "3000|quote BTCUSDT 103.00/103.10"],
            sim.Strategy.Journal);
        Assert.Equal(3, sim.Engine.Iteration);
    }

    [Fact]
    public void Elements_with_equal_timestamps_keep_their_insertion_order_across_data_types()
    {
        using SimHarness sim = SimHarness.Spot();
        sim.Trade(1000, 100.05m)
            .Quote(1000, 100.00m, 100.10m)
            .Bar(1000, 100.00m, 100.00m, 100.00m, 100.00m)
            .Quote(1000, 100.01m, 100.11m)
            .Run();

        Assert.Equal(
            ["1000|trade BTCUSDT 100.05", "1000|quote BTCUSDT 100.00/100.10", "1000|bar BTCUSDT 100.00", "1000|quote BTCUSDT 100.01/100.11"],
            sim.Strategy.Journal);
    }

    [Fact]
    public void Run_by_time_range_ignores_data_outside_the_range_for_strategy_and_venue_alike()
    {
        // A resting buy at 95.00 would be filled by the 90.00 quote at 5000, but the run stops at 4000.
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? order = null;
        sim.Quote(1000, 50.00m, 50.10m)
            .Quote(2000, 100.00m, 100.10m)
            .At(2500, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(95.00m))))
            .Quote(3000, 100.00m, 100.10m)
            .Quote(4000, 100.00m, 100.10m)
            .Quote(5000, 90.00m, 90.10m);

        sim.Engine.Run(Scripted.Ms(2000), Scripted.Ms(4000));

        Assert.Equal(
            ["2000|quote BTCUSDT 100.00/100.10", "2500|OrderInitialized", "2500|OrderSubmitted", "2500|OrderAccepted", "3000|quote BTCUSDT 100.00/100.10", "4000|quote BTCUSDT 100.00/100.10"],
            sim.Strategy.Journal);
        Assert.Equal(OrderStatus.Accepted, order!.Status);
        Assert.Equal(3, sim.Engine.Iteration);
        Assert.Equal(Scripted.Ms(2000), sim.Engine.BacktestStart);
        Assert.Equal(Scripted.Ms(4000), sim.Engine.BacktestEnd);
        Assert.Equal(Scripted.Ms(4000), sim.Engine.Clock.Timestamp);
    }

    [Fact]
    public void Range_bounds_are_inclusive_at_both_ends()
    {
        using SimHarness sim = SimHarness.Spot();
        sim.Quote(1000, 100.00m, 100.10m).Quote(2000, 100.00m, 100.10m).Quote(3000, 100.00m, 100.10m);

        sim.Engine.Run(Scripted.Ms(1000), Scripted.Ms(2000));

        Assert.Equal(2, sim.Engine.Iteration);
    }

    [Fact]
    public void Timers_fire_in_simulated_time_between_data_events_and_before_data_with_the_same_timestamp()
    {
        using SimHarness sim = SimHarness.Spot();
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1000, s => s.Timer("pulse", TimeSpan.FromMilliseconds(400), stop: Scripted.Ms(2200)))
            .Quote(2000, 100.00m, 100.10m)
            .Quote(3000, 100.00m, 100.10m)
            .Run();

        Assert.Equal(
            [
                "1000|quote BTCUSDT 100.00/100.10",
                "1400|timer pulse due 1400",
                "1800|timer pulse due 1800",
                "2000|quote BTCUSDT 100.00/100.10",
                "2200|timer pulse due 2200",
                "3000|quote BTCUSDT 100.00/100.10",
            ],
            sim.Strategy.Journal);
    }

    [Fact]
    public void Alert_due_at_the_timestamp_of_a_data_element_runs_before_that_element_is_processed()
    {
        // The order is sent at 2000 by a timer. It must trade on the 1000 quote, not on the 2000 quote.
        using SimHarness sim = SimHarness.Spot();
        MarketOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(2000, s => order = s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(2000, 200.00m, 200.10m)
            .Run();

        Assert.Equal(sim.Px(100.10m), sim.SingleFill(order!).LastPx);
    }

    [Fact]
    public void Strategy_is_started_for_the_run_and_stopped_when_the_run_ends()
    {
        using SimHarness sim = SimHarness.Spot();
        List<string> states = new();
        sim.Strategy.StopHandler = s => states.Add("stop handler at " + s.Now);
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => states.Add("running=" + s.IsRunning))
            .Quote(2000, 100.00m, 100.10m)
            .Run();

        Assert.Equal(["running=True", "stop handler at " + Scripted.Ms(2000)], states);
        Assert.True(sim.Strategy.IsStopped);
        Assert.False(sim.Engine.Kernel.IsRunning);
    }

    [Fact]
    public void Position_left_open_at_the_end_stays_open_and_is_valued_at_the_last_price()
    {
        using SimHarness sim = SimHarness.Perp(new SimOptions { FeeModel = new FixedFeeModel(Money.Zero(Currencies.USDT)) });
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(2m))))
            .Quote(2000, 50_300.0m, 50_300.2m)
            .Run();

        BacktestResult result = sim.Engine.GetResult();
        Assert.Equal(1, result.Trades.OpenPositions);
        CurrencyStatistics usdt = result.Currencies.Single(c => c.Currency.Equals(Currencies.USDT));
        Assert.Equal(600m, usdt.UnrealizedPnl); // long is marked at the bid: (50 300 - 50 000) * 2
        Assert.Equal(100_000m, usdt.EndingBalance); // nothing is realised
    }

    [Fact]
    public void Strategy_that_flattens_in_its_stop_handler_ends_the_run_flat_at_the_last_bid()
    {
        using SimHarness sim = SimHarness.Perp(new SimOptions { FlattenOnStop = true, FeeModel = new FixedFeeModel(Money.Zero(Currencies.USDT)) });
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(2m))))
            .Quote(2000, 50_300.0m, 50_300.2m)
            .Run();

        Assert.True(Assert.Single(sim.Positions).IsClosed);
        Assert.Equal(100_600m, sim.Balance(Currencies.USDT));
        OrderFilled closing = sim.Events.OfType<OrderFilled>().Last();
        Assert.Equal(sim.Px(50_300.0m), closing.LastPx);
        Assert.Equal(Scripted.Ms(2000), closing.TsEvent);
    }

    [Fact]
    public void Streaming_run_keeps_the_strategy_alive_between_batches_of_data()
    {
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(99.00m))))
            .Quote(2000, 100.00m, 100.10m);

        sim.Engine.Run(streaming: true);
        Assert.True(sim.Strategy.IsRunning);
        Assert.Equal(OrderStatus.Accepted, order!.Status);

        sim.Engine.ClearData();
        sim.Quote(3000, 98.80m, 98.90m);
        sim.Engine.Run(streaming: true);
        sim.Engine.End();

        Assert.True(sim.Strategy.IsStopped);
        OrderFilled fill = sim.SingleFill(order);
        Assert.Equal(sim.Px(99.00m), fill.LastPx);
        Assert.Equal(Scripted.Ms(3000), fill.TsEvent);
        Assert.Equal(3, sim.Engine.Iteration);
    }

    [Fact]
    public void Streaming_run_does_not_replay_earlier_batches_when_more_data_is_added()
    {
        // The buy at 101.00 is placed when the market is already at 105; it only ever sees 105 and 106 afterwards.
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? order = null;
        sim.Strategy.QuoteHandler = (s, quote) =>
        {
            if (order is null && quote.Bid.Value == 105.00m)
            {
                order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(101.00m)));
            }
        };
        sim.Quote(1000, 100.00m, 100.10m).Quote(2000, 105.00m, 105.10m);
        sim.Engine.Run(streaming: true);

        sim.Quote(3000, 106.00m, 106.10m);
        sim.Engine.Run(streaming: true);
        sim.Engine.End();

        Assert.Empty(sim.Fills(order!));
        Assert.Equal(3, sim.Engine.Iteration);
    }

    [Fact]
    public void Historical_bar_request_returns_only_bars_that_are_already_in_the_past_of_the_simulated_clock()
    {
        using SimHarness sim = SimHarness.Spot();
        List<Bar> received = new();
        sim.Bar(60_000, 100.00m, 101.00m, 99.00m, 100.50m)
            .Bar(120_000, 100.50m, 102.00m, 100.00m, 101.50m)
            .Bar(180_000, 101.50m, 103.00m, 101.00m, 102.50m)
            .Bar(240_000, 102.50m, 104.00m, 102.00m, 103.50m);
        HistoryProbe probe = new(Scripted.MinuteBars(sim.Instrument), Scripted.Ms(150_000), received);
        sim.Engine.AddActor(probe);
        sim.Run();

        Assert.Equal([Scripted.Ms(60_000), Scripted.Ms(120_000)], received.Select(b => b.TsEvent));
    }

    [Fact]
    public void Disposed_engine_refuses_to_run()
    {
        SimHarness sim = SimHarness.Spot();
        sim.Quote(1000, 100.00m, 100.10m);
        sim.Dispose();

        Assert.Throws<ObjectDisposedException>(() => sim.Engine.Run());
    }

    private sealed class HistoryProbe : Core.Trading.Actor
    {
        private readonly BarType _barType;
        private readonly UnixNanos _when;
        private readonly List<Bar> _sink;

        public HistoryProbe(BarType barType, UnixNanos when, List<Bar> sink)
            : base(new Core.Trading.ActorConfig { ActorId = new Core.Model.Identifiers.ActorId("HistoryProbe-001") })
        {
            _barType = barType;
            _when = when;
            _sink = sink;
        }

        protected override void OnStart() => SetTimeAlert("ask", _when, _ => RequestBars(_barType));

        protected override void OnHistoricalData(IData data)
        {
            if (data is Bar bar)
            {
                _sink.Add(bar);
            }
        }
    }
}
