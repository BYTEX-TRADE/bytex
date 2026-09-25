using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Orders;

namespace Bytex.Backtest.Tests;

// Why: the fill model is the only source of randomness in a backtest. Probabilities of exactly 0 and 1 must be
// certainties, a seed must pin the whole sequence, and slippage must be one tick against the order.
public sealed class FillModelTests
{
    [Theory]
    [InlineData(-0.01, 1, 0)]
    [InlineData(1.01, 1, 0)]
    [InlineData(1, -0.5, 0)]
    [InlineData(1, 2, 0)]
    [InlineData(1, 1, -1)]
    [InlineData(1, 1, 1.5)]
    public void Probabilities_outside_zero_to_one_are_refused(decimal limit, decimal stop, decimal slippage)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FillModel(limit, stop, slippage));
    }

    [Fact]
    public void Default_model_always_fills_limits_on_touch_never_worsens_stops_and_never_slips()
    {
        FillModel model = new();

        for (int i = 0; i < 1000; i++)
        {
            Assert.True(model.IsLimitFilled());
            Assert.True(model.IsStopFilled());
            Assert.False(model.IsSlipped());
        }
    }

    [Fact]
    public void Probability_zero_never_happens_and_probability_one_always_happens_for_any_seed()
    {
        for (int seed = 0; seed < 50; seed++)
        {
            FillModel never = new(probFillOnLimit: 0m, probFillOnStop: 0m, probSlippage: 0m, seed: seed);
            FillModel always = new(probFillOnLimit: 1m, probFillOnStop: 1m, probSlippage: 1m, seed: seed);
            for (int i = 0; i < 100; i++)
            {
                Assert.False(never.IsLimitFilled());
                Assert.False(never.IsStopFilled());
                Assert.False(never.IsSlipped());
                Assert.True(always.IsLimitFilled());
                Assert.True(always.IsStopFilled());
                Assert.True(always.IsSlipped());
            }
        }
    }

    [Fact]
    public void Same_seed_reproduces_the_same_sequence_of_outcomes()
    {
        FillModel first = new(probFillOnLimit: 0.5m, probSlippage: 0.5m, seed: 7);
        FillModel second = new(probFillOnLimit: 0.5m, probSlippage: 0.5m, seed: 7);

        List<bool> a = Enumerable.Range(0, 200).Select(i => i % 2 == 0 ? first.IsLimitFilled() : first.IsSlipped()).ToList();
        List<bool> b = Enumerable.Range(0, 200).Select(i => i % 2 == 0 ? second.IsLimitFilled() : second.IsSlipped()).ToList();

        Assert.Equal(a, b);
        Assert.Contains(true, a);
        Assert.Contains(false, a);
    }

    [Fact]
    public void Different_seeds_produce_different_sequences()
    {
        FillModel first = new(probFillOnLimit: 0.5m, seed: 1);
        FillModel second = new(probFillOnLimit: 0.5m, seed: 2);

        List<bool> a = Enumerable.Range(0, 200).Select(_ => first.IsLimitFilled()).ToList();
        List<bool> b = Enumerable.Range(0, 200).Select(_ => second.IsLimitFilled()).ToList();

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Observed_frequency_matches_the_configured_probability()
    {
        // 10 000 rolls at p = 0.3: the standard deviation of the frequency is sqrt(0.3 * 0.7 / 10 000) = 0.0046,
        // so +-0.03 is more than six sigma. The seed is fixed, so this cannot flake.
        FillModel model = new(probSlippage: 0.3m, seed: 42);

        int slipped = Enumerable.Range(0, 10_000).Count(_ => model.IsSlipped());

        Assert.InRange(slipped / 10_000d, 0.27, 0.33);
    }

    [Theory]
    [InlineData(OrderSide.Buy, 100.11)] // ask 100.10 plus one 0.01 tick
    [InlineData(OrderSide.Sell, 99.99)] // bid 100.00 minus one tick
    public void Certain_slippage_moves_a_market_order_exactly_one_tick_against_it(OrderSide side, decimal expected)
    {
        using SimHarness sim = SimHarness.Spot(new SimOptions { FillModel = new FillModel(probSlippage: 1m) });
        MarketOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.Market(sim.Id, side, sim.Qty(1m))))
            .Quote(2000, 100.00m, 100.10m)
            .Run();

        Assert.Equal(sim.Px(expected), sim.SingleFill(order!).LastPx);
    }

    [Fact]
    public void Slippage_uses_the_instruments_own_tick_size()
    {
        // The perpetual ticks in 0.1, so a slipped buy at an ask of 50 000.1 pays 50 000.2.
        using SimHarness sim = SimHarness.Perp(new SimOptions { FillModel = new FillModel(probSlippage: 1m) });
        MarketOrder? order = null;
        sim.Quote(1000, 50_000.0m, 50_000.1m)
            .At(1500, s => order = s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m))))
            .Quote(2000, 50_000.0m, 50_000.1m)
            .Run();

        Assert.Equal(sim.Px(50_000.2m), sim.SingleFill(order!).LastPx);
    }

    [Fact]
    public void Slippage_never_applies_to_limit_orders()
    {
        using SimHarness sim = SimHarness.Spot(new SimOptions { FillModel = new FillModel(probSlippage: 1m) });
        LimitOrder? marketable = null;
        LimitOrder? resting = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s =>
            {
                marketable = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(100.10m)));
                resting = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(99.00m)));
            })
            .Quote(2000, 98.00m, 98.10m)
            .Run();

        Assert.Equal(sim.Px(100.10m), sim.SingleFill(marketable!).LastPx);
        Assert.Equal(sim.Px(99.00m), sim.SingleFill(resting!).LastPx);
    }

    [Fact]
    public void With_zero_fill_probability_a_limit_is_skipped_on_touch_but_still_fills_when_traded_through()
    {
        using SimHarness sim = SimHarness.Spot(new SimOptions { FillModel = new FillModel(probFillOnLimit: 0m) });
        LimitOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(99.50m))))
            .Quote(2000, 99.40m, 99.50m) // ask touches the limit
            .At(2500, s => s.Note("status " + order!.Status))
            .Quote(3000, 99.30m, 99.49m) // ask one tick through
            .Run();

        Assert.Contains("2500|status Accepted", sim.Strategy.Journal);
        OrderFilled fill = sim.SingleFill(order!);
        Assert.Equal(Scripted.Ms(3000), fill.TsEvent);
        Assert.Equal(sim.Px(99.50m), fill.LastPx);
    }

    [Fact]
    public void With_certain_fill_probability_a_limit_fills_on_touch()
    {
        using SimHarness sim = SimHarness.Spot(new SimOptions { FillModel = new FillModel(probFillOnLimit: 1m) });
        LimitOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(99.50m))))
            .Quote(2000, 99.40m, 99.50m)
            .Run();

        OrderFilled fill = sim.SingleFill(order!);
        Assert.Equal(Scripted.Ms(2000), fill.TsEvent);
        Assert.Equal(LiquiditySide.Maker, fill.LiquiditySide);
    }

    [Theory]
    [InlineData(OrderSide.Buy, 101.00, 100.90, 101.00, 101.01)] // triggered at the 101.00 ask, filled one tick higher
    [InlineData(OrderSide.Sell, 99.00, 99.00, 99.10, 98.99)]
    public void With_zero_stop_fill_probability_a_triggered_stop_fills_one_tick_worse(OrderSide side, decimal trigger, decimal bid, decimal ask, decimal expected)
    {
        using SimHarness sim = SimHarness.Spot(new SimOptions { FillModel = new FillModel(probFillOnStop: 0m) });
        StopMarketOrder? order = null;
        sim.Quote(1000, 100.00m, 100.10m)
            .At(1500, s => order = s.Submit(s.Orders.StopMarket(sim.Id, side, sim.Qty(1m), sim.Px(trigger))))
            .Quote(2000, bid, ask)
            .Run();

        Assert.Equal(sim.Px(expected), sim.SingleFill(order!).LastPx);
    }

    [Fact]
    public void Two_venues_built_with_the_same_seed_slip_exactly_the_same_orders()
    {
        static List<decimal> FillPrices()
        {
            using SimHarness sim = SimHarness.Spot(new SimOptions { FillModel = new FillModel(probSlippage: 0.5m, seed: 11) });
            List<MarketOrder> orders = new();
            sim.Quote(1000, 100.00m, 100.10m);
            for (int i = 0; i < 40; i++)
            {
                sim.At(1100 + i, s => orders.Add(s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(0.01m)))));
            }

            sim.Quote(2000, 100.00m, 100.10m).Run();
            return orders.Select(o => o.AvgPx!.Value).ToList();
        }

        List<decimal> first = FillPrices();
        List<decimal> second = FillPrices();

        Assert.Equal(first, second);
        Assert.All(first, px => Assert.Contains(px, new[] { 100.10m, 100.11m }));
        Assert.Contains(100.10m, first);
        Assert.Contains(100.11m, first);
    }
}
