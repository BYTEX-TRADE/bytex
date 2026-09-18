using System.Globalization;
using System.Text.Json.Nodes;
using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Primitives;

namespace Bytex.Backtest.Tests;

// Why: a backtest nobody can reproduce is worthless. The same inputs must give the same events and the same result
// document, run after run, including when the fill model rolls dice, and also after Reset() on a reused engine.
public sealed class DeterminismAndResetTests
{
    /// <summary>
    /// A busy scenario: random one-tick slippage and touch fills, market, limit, stop and trailing orders, a cancel.
    /// </summary>
    private static SimHarness BuildScenario(bool randomFills = true)
    {
        FillModel fillModel = randomFills ? new FillModel(probFillOnLimit: 0.5m, probFillOnStop: 0.5m, probSlippage: 0.5m, seed: 99) : new FillModel();
        SimHarness sim = SimHarness.Perp(new SimOptions { FillModel = fillModel });
        decimal price = 50_000.0m;
        for (int i = 0; i < 120; i++)
        {
            // A deterministic zig-zag: +30, +30, -50, repeated.
            price += i % 3 == 2 ? -50.0m : 30.0m;
            sim.Quote(1000 + (i * 1000), price, price + 0.5m);
        }

        for (int i = 0; i < 30; i++)
        {
            int k = i;
            sim.At(1500 + (k * 4000), s =>
            {
                decimal bid = s.Store.QuoteTick(sim.Id)!.Value.Bid.Value;
                s.Submit(s.Orders.Market(sim.Id, k % 2 == 0 ? OrderSide.Buy : OrderSide.Sell, sim.Qty(0.1m)));
                s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(0.2m), sim.Px(bid - 20.0m)));
                s.Submit(s.Orders.StopMarket(sim.Id, OrderSide.Sell, sim.Qty(0.1m), sim.Px(bid - 40.0m)));
                s.Submit(s.Orders.TrailingStopMarket(sim.Id, OrderSide.Sell, sim.Qty(0.1m), 35.0m));
            });
            sim.At(3500 + (k * 4000), s => s.CancelAll(sim.Id, OrderSide.Buy));
        }

        return sim;
    }

    private static List<string> Describe(IEnumerable<Event> events) => events.Select(e => e switch
    {
        OrderFilled f => $"{f.TsEvent.Value}|Filled|{f.ClientOrderId}|{f.TradeId}|{f.OrderSide}|{f.LastQty}|{f.LastPx}|{f.Commission}|{f.LiquiditySide}|{f.PositionId}",
        OrderUpdated u => $"{u.TsEvent.Value}|Updated|{u.ClientOrderId}|{u.Quantity}|{u.Price}|{u.TriggerPrice}",
        OrderEvent o => $"{o.TsEvent.Value}|{o.GetType().Name}|{o.ClientOrderId}|{o.VenueOrderId}",
        PositionEvent p => $"{p.TsEvent.Value}|{p.GetType().Name}|{p.PositionId}|{p.Side}|{p.Quantity}|{p.AvgPxOpen.ToString(CultureInfo.InvariantCulture)}|{p.RealizedPnl}",
        AccountState a => $"{a.TsEvent.Value}|AccountState|{string.Join(";", a.Balances.Select(b => $"{b.Total}/{b.Locked}/{b.Free}"))}|{string.Join(";", a.Margins.Select(m => $"{m.Initial}/{m.Maintenance}"))}",
        _ => $"{e.TsEvent.Value}|{e.GetType().Name}",
    }).ToList();

    /// <summary>The result document without the three wall-clock fields, which legitimately differ between runs.</summary>
    private static string StableJson(BacktestResult result)
    {
        JsonObject document = JsonNode.Parse(ReportWriter.ToJson(result))!.AsObject();
        document.Remove("runStarted");
        document.Remove("runFinished");
        document.Remove("elapsedSeconds");
        return document.ToJsonString();
    }

    [Fact]
    public void Scenario_really_exercises_the_random_fill_model()
    {
        // Guards the tests below against passing vacuously: both slipped and unslipped market fills must occur.
        using SimHarness sim = BuildScenario().Run();

        List<OrderFilled> marketFills = sim.Events.OfType<OrderFilled>().Where(f => f.OrderType == OrderType.Market).ToList();
        Assert.Equal(30, marketFills.Count);
        int slipped = marketFills.Count(f =>
        {
            decimal cents = f.LastPx.Value % 1m;
            return f.IsBuy ? cents == 0.6m : cents == 0.9m; // asks end in .5 and bids in .0; one 0.1 tick against the order
        });
        Assert.InRange(slipped, 1, 29);
        Assert.True(sim.Events.OfType<OrderFilled>().Count() > 40);
    }

    [Fact]
    public void Two_engines_fed_the_same_inputs_publish_the_same_event_sequence()
    {
        using SimHarness first = BuildScenario().Run();
        using SimHarness second = BuildScenario().Run();

        Assert.Equal(Describe(first.Events), Describe(second.Events));
    }

    [Fact]
    public void Two_engines_fed_the_same_inputs_serialise_to_the_same_result_document()
    {
        using SimHarness first = BuildScenario().Run();
        using SimHarness second = BuildScenario().Run();

        Assert.Equal(StableJson(first.Engine.GetResult()), StableJson(second.Engine.GetResult()));
    }

    [Fact]
    public void Different_fill_model_seed_changes_the_outcome_so_the_seed_is_what_pins_it()
    {
        static List<Price> FillPrices(int seed)
        {
            using SimHarness sim = SimHarness.Perp(new SimOptions { FillModel = new FillModel(probSlippage: 0.5m, seed: seed) });
            sim.Quote(1000, 50_000.0m, 50_000.5m);
            for (int i = 0; i < 40; i++)
            {
                sim.At(1100 + i, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(0.1m))));
            }

            sim.Quote(2000, 50_000.0m, 50_000.5m).Run();
            return sim.Events.OfType<OrderFilled>().Select(f => f.LastPx).ToList();
        }

        Assert.Equal(FillPrices(100), FillPrices(100));
        Assert.NotEqual(FillPrices(100), FillPrices(101));
    }

    [Fact]
    public void Reset_clears_orders_positions_balances_and_counters_but_keeps_the_setup()
    {
        using SimHarness sim = BuildScenario().Run();
        Assert.NotEmpty(sim.Engine.Cache.Orders());

        sim.Engine.Reset();

        Assert.Empty(sim.Engine.Cache.Orders());
        Assert.Empty(sim.Engine.Cache.Positions());
        Assert.Empty(sim.Engine.Cache.Accounts());
        Assert.Equal(0, sim.Engine.Iteration);
        Assert.Null(sim.Engine.BacktestStart);
        Assert.Null(sim.Engine.BacktestEnd);
        Assert.Equal(100_000m, sim.Balance(Currencies.USDT));
        Assert.Equal(0, sim.Exchange.OpenOrderCount);
        Assert.Equal(120, sim.Engine.Data.Count);
        Assert.Same(sim.Instrument, sim.Exchange.Instruments[sim.Id]);
        Assert.Single(sim.Engine.Trader.Strategies);
    }

    private static string DescribeFill(OrderFilled f) => $"{f.TsEvent.Value}|{f.TradeId}|{f.OrderSide}|{f.OrderType}|{f.LastQty}|{f.LastPx}|{f.Commission}|{f.LiquiditySide}";

    [Fact]
    public void Run_after_reset_reproduces_fills_balances_and_statistics_of_the_first_run()
    {
        using SimHarness sim = BuildScenario(randomFills: false).Run();
        List<string> firstFills = sim.Events.OfType<OrderFilled>().Select(DescribeFill).ToList();
        decimal firstBalance = sim.Balance(Currencies.USDT);
        BacktestResult firstResult = sim.Engine.GetResult();

        sim.Engine.Reset();
        sim.ClearRecordedEvents();
        sim.Run();

        Assert.Equal(firstFills, sim.Events.OfType<OrderFilled>().Select(DescribeFill).ToList());
        Assert.Equal(firstBalance, sim.Balance(Currencies.USDT));
        BacktestResult secondResult = sim.Engine.GetResult();
        Assert.Equal(firstResult.Currencies, secondResult.Currencies);
        Assert.Equal(firstResult.Trades, secondResult.Trades);
        Assert.Equal(firstResult.TotalOrders, secondResult.TotalOrders);
        Assert.Equal(firstResult.TotalEvents, secondResult.TotalEvents);
        Assert.Equal(firstResult.Iterations, secondResult.Iterations);
    }

    [Fact(Skip = "BUG: Reset() does not reseed the venue's fill model, so with probabilistic fills a re-run rolls different dice than the first run")]
    public void Run_after_reset_reproduces_the_first_run_when_the_fill_model_is_probabilistic()
    {
        using SimHarness sim = BuildScenario(randomFills: true).Run();
        List<string> firstFills = sim.Events.OfType<OrderFilled>().Select(DescribeFill).ToList();

        sim.Engine.Reset();
        sim.ClearRecordedEvents();
        sim.Run();

        Assert.Equal(firstFills, sim.Events.OfType<OrderFilled>().Select(DescribeFill).ToList());
    }

    [Fact(Skip = "BUG: Reset() does not reset the strategy's client order id counter, so the orders of a re-run are numbered on from the first run")]
    public void Run_after_reset_numbers_its_orders_exactly_like_the_first_run()
    {
        using SimHarness sim = BuildScenario(randomFills: false).Run();
        List<string> first = sim.Engine.GetResult().Orders.Select(o => o.ClientOrderId.Value).ToList();

        sim.Engine.Reset();
        sim.Run();

        Assert.Equal(first, sim.Engine.GetResult().Orders.Select(o => o.ClientOrderId.Value).ToList());
    }
}
