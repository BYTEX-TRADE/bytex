using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Positions;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Trading;

namespace Bytex.Backtest.Tests;

// Why: R3.11. A size that would move the market if it went out at once goes out in slices instead, and what makes
// that trustworthy is arithmetic rather than intent: the slices have to add up to the order exactly, arrive at the
// pace that was asked for, and stop the moment the instruction is taken back. The parent order is the instruction and
// never reaches a venue, which is what the algorithm contract has always said - so what a strategy can read afterwards
// is the slices, by the parent's id.
public sealed class TwapExecAlgorithmTests
{
    private static readonly ExecAlgorithmId Twap = new("TWAP");

    private static SimHarness Harness(TimeSpan horizon, TimeSpan interval) => Harness(horizon, interval, out _);

    private static SimHarness Harness(TimeSpan horizon, TimeSpan interval, out TwapExecAlgorithm algorithm, OmsType oms = OmsType.Netting)
    {
        SimHarness sim = SimHarness.Perp(new SimOptions { FeeModel = new FixedFeeModel(Money.Zero(Currencies.USDT)), OmsType = oms });
        algorithm = new TwapExecAlgorithm(new TwapExecAlgorithmConfig
        {
            ExecAlgorithmId = Twap,
            Horizon = horizon,
            Interval = interval,
        });
        sim.Engine.AddExecAlgorithm(algorithm);
        return sim;
    }

    /// <summary>Every order the venue was told about, in the order it heard about them.</summary>
    private static IReadOnlyList<Order> Slices(SimHarness sim) =>
        [.. sim.Engine.Kernel.Cache.Orders().Where(o => o.IsSpawned).OrderBy(o => o.ClientOrderId.Value, StringComparer.Ordinal)];

    [Fact]
    public void An_order_goes_out_in_equal_slices_that_add_up_to_it()
    {
        using SimHarness sim = Harness(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(1));
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m), execAlgorithmId: Twap)))
            .Quote(2000, 50_010.0m, 50_010.0m)
            .Quote(3000, 50_020.0m, 50_020.0m)
            .Quote(4000, 50_030.0m, 50_030.0m)
            .Quote(5000, 50_040.0m, 50_040.0m)
            .Quote(6000, 50_050.0m, 50_050.0m)
            .Run();

        IReadOnlyList<Order> slices = Slices(sim);

        Assert.Equal(4, slices.Count);
        Assert.All(slices, o => Assert.Equal(0.25m, o.Quantity.Value));
        Assert.Equal(1m, slices.Sum(o => o.Quantity.Value));
        Assert.All(slices, o => Assert.Equal(OrderStatus.Filled, o.Status));

        // One position of the whole size, at the average of the prices the slices paid rather than at one of them.
        Assert.Equal(1m, sim.Engine.Kernel.Portfolio.NetPosition(sim.Id));
        decimal average = slices.Sum(o => o.AvgPx!.Value * o.Quantity.Value);
        Assert.Equal(average, Assert.Single(sim.Engine.Kernel.Cache.Positions()).AvgPxOpen);
        Assert.InRange(average, 50_000m, 50_050m);
    }

    [Fact]
    public void The_pace_is_one_slice_an_interval()
    {
        using SimHarness sim = Harness(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1));
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(0.3m), execAlgorithmId: Twap)))
            .Quote(2000, 50_000.0m, 50_000.0m)
            .Quote(3000, 50_000.0m, 50_000.0m)
            .Quote(4000, 50_000.0m, 50_000.0m)
            .Quote(5000, 50_000.0m, 50_000.0m)
            .Run();

        long[] sent = [.. Slices(sim).Select(o => o.InitEvent.TsInit.Value)];

        Assert.Equal(3, sent.Length);
        Assert.Equal(Scripted.Ms(1500).Value, sent[0]);
        Assert.All(sent.Zip(sent.Skip(1)), pair => Assert.Equal(TimeSpan.FromSeconds(1).Ticks * UnixNanos.NanosPerTick, pair.Second - pair.First));
    }

    [Fact]
    public void The_parent_order_is_the_instruction_and_never_reaches_the_venue()
    {
        using SimHarness sim = Harness(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1));
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(0.2m), execAlgorithmId: Twap)))
            .Quote(2000, 50_000.0m, 50_000.0m)
            .Quote(3000, 50_000.0m, 50_000.0m)
            .Run();

        Order parent = Assert.Single(sim.Engine.Kernel.Cache.Orders(), o => !o.IsSpawned);

        Assert.Equal(OrderStatus.Initialized, parent.Status);
        Assert.DoesNotContain(sim.Events.OfType<OrderFilled>(), f => f.ClientOrderId == parent.ClientOrderId);

        // What a strategy reads afterwards is the slices, by the id of the instruction that made them.
        IReadOnlyList<Order> slices = sim.Engine.Kernel.Cache.OrdersForExecSpawn(parent.ClientOrderId);
        Assert.Equal(2, slices.Count);
        Assert.All(slices, o => Assert.Equal(Twap, o.ExecAlgorithmId));
        Assert.All(slices, o => Assert.Equal(parent.ClientOrderId, o.ExecSpawnId));
    }

    [Fact]
    public void An_order_may_carry_its_own_pace()
    {
        // The configuration says four seconds in one-second slices; this order says two seconds in halves.
        using SimHarness sim = Harness(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(1));
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m), execAlgorithmId: Twap,
                execAlgorithmParams: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [TwapExecAlgorithm.HorizonParam] = "00:00:02",
                    [TwapExecAlgorithm.IntervalParam] = "00:00:00.500",
                })))
            .Quote(2000, 50_000.0m, 50_000.0m)
            .Quote(3000, 50_000.0m, 50_000.0m)
            .Quote(4000, 50_000.0m, 50_000.0m)
            .Run();

        Assert.Equal(4, Slices(sim).Count);
        Assert.Equal(1m, Slices(sim).Sum(o => o.Quantity.Value));
    }

    [Fact]
    public void A_limit_order_is_worked_as_limit_orders_at_its_own_price()
    {
        using SimHarness sim = Harness(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1));
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1500, s => s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(0.2m), sim.Px(49_000m), execAlgorithmId: Twap)))
            .Quote(2000, 50_000.0m, 50_000.0m)
            .Quote(3000, 50_000.0m, 50_000.0m)
            .Run();

        IReadOnlyList<Order> slices = Slices(sim);

        Assert.Equal(2, slices.Count);
        Assert.All(slices, o => Assert.Equal(OrderType.Limit, o.Type));
        Assert.All(slices, o => Assert.Equal(sim.Px(49_000m), o.Price));

        // Nothing filled: the price it was willing to pay is still the order's own business, not the algorithm's.
        Assert.All(slices, o => Assert.Equal(OrderStatus.Accepted, o.Status));
    }

    [Fact]
    public void An_order_that_is_cancelled_stops_being_worked()
    {
        List<int> working = new();
        using SimHarness sim = Harness(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), out TwapExecAlgorithm algorithm);
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(0.5m), execAlgorithmId: Twap)))
            .Quote(2000, 50_000.0m, 50_000.0m)
            .At(2100, s => s.Cancel(sim.Engine.Kernel.Cache.Orders().First(o => !o.IsSpawned)))
            .At(2200, _ => working.Add(algorithm.Working.Count))
            .Quote(3000, 50_000.0m, 50_000.0m)
            .Quote(4000, 50_000.0m, 50_000.0m)
            .Quote(5000, 50_000.0m, 50_000.0m)
            .Quote(6000, 50_000.0m, 50_000.0m)
            .Run();

        // The one slice that had gone out before the cancel, and nothing after it.
        Assert.Single(Slices(sim));
        Assert.Equal(0.1m, sim.Engine.Kernel.Portfolio.NetPosition(sim.Id));

        // And it stopped working the order when the cancel arrived, not when the next slice would have gone out.
        Assert.Equal([0], working);
        Assert.Empty(algorithm.Working);
    }

    [Fact]
    public void An_order_it_cannot_slice_is_cancelled_rather_than_left_looking_alive()
    {
        using SimHarness sim = Harness(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1));
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1500, s => s.Submit(s.Orders.StopMarket(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(51_000m), execAlgorithmId: Twap)))
            .Quote(2000, 50_000.0m, 50_000.0m)
            .Quote(3000, 52_000.0m, 52_000.0m)
            .Run();

        Order parent = Assert.Single(sim.Engine.Kernel.Cache.Orders());

        Assert.Equal(OrderStatus.Canceled, parent.Status);
        Assert.Empty(sim.Events.OfType<OrderFilled>());
        Assert.Equal(0m, sim.Engine.Kernel.Portfolio.NetPosition(sim.Id));
    }

    [Fact]
    public void A_size_that_does_not_divide_evenly_is_still_worked_exactly()
    {
        // Three slices of one contract: two of a third and whatever rounding left over on the last, so the position
        // is the order and not a little more or less than it.
        using SimHarness sim = Harness(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1));
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(1m), execAlgorithmId: Twap)))
            .Quote(2000, 50_000.0m, 50_000.0m)
            .Quote(3000, 50_000.0m, 50_000.0m)
            .Quote(4000, 50_000.0m, 50_000.0m)
            .Quote(5000, 50_000.0m, 50_000.0m)
            .Run();

        IReadOnlyList<Order> slices = Slices(sim);

        Assert.Equal(3, slices.Count);
        Assert.Equal(1m, slices.Sum(o => o.Quantity.Value));
        Assert.Equal(1m, sim.Engine.Kernel.Portfolio.NetPosition(sim.Id));
    }

    [Fact]
    public void A_venue_with_a_minimum_size_gets_fewer_bigger_slices_rather_than_rejections()
    {
        // Four slices of 0.1 would each be under the venue's minimum of 0.15, and a stream of rejected orders is not
        // a paced order. The size is cut into as many pieces of at least the minimum as it can be: two of 0.2.
        CryptoPerpetual instrument = new(new InstrumentSpec
        {
            Id = new InstrumentId(new Symbol("BTCUSDT-MIN"), TestInstruments.Sim),
            AssetClass = AssetClass.Crypto,
            InstrumentClass = InstrumentClass.Swap,
            QuoteCurrency = Currencies.USDT,
            BaseCurrency = Currencies.BTC,
            SettlementCurrency = Currencies.USDT,
            PricePrecision = 1,
            SizePrecision = 3,
            PriceIncrement = new Price(0.1m, 1),
            SizeIncrement = new Quantity(0.001m, 3),
            MinQuantity = new Quantity(0.15m, 3),
            MarginInit = 0.05m,
            MarginMaint = 0.025m,
        });

        using SimHarness sim = SimHarness.For(instrument, new SimOptions
        {
            AccountType = AccountType.Margin,
            StartingBalances = [new Money(100_000m, Currencies.USDT)],
            FeeModel = new FixedFeeModel(Money.Zero(Currencies.USDT)),
        });
        sim.Engine.AddExecAlgorithm(new TwapExecAlgorithm(new TwapExecAlgorithmConfig
        {
            ExecAlgorithmId = Twap,
            Horizon = TimeSpan.FromSeconds(4),
            Interval = TimeSpan.FromSeconds(1),
        }));

        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(0.4m), execAlgorithmId: Twap)))
            .Quote(2000, 50_000.0m, 50_000.0m)
            .Quote(3000, 50_000.0m, 50_000.0m)
            .Quote(4000, 50_000.0m, 50_000.0m)
            .Quote(5000, 50_000.0m, 50_000.0m)
            .Run();

        IReadOnlyList<Order> slices = Slices(sim);

        Assert.Equal(2, slices.Count);
        Assert.All(slices, o => Assert.True(o.Quantity.Value >= 0.15m, $"a slice of {o.Quantity} is under the venue's minimum"));
        Assert.Equal(0.4m, slices.Sum(o => o.Quantity.Value));
        Assert.Empty(sim.Events.OfType<OrderRejected>());
    }

    [Fact]
    public void On_a_hedging_venue_every_slice_lands_on_the_position_the_order_was_submitted_under()
    {
        // A venue that hedges opens a position per fill unless it is told which position the order belongs to. Slices
        // that forgot would leave one instruction as three positions, and nothing afterwards could tell that they
        // were one order.
        PositionId position = new("P-TWAP");
        using SimHarness sim = Harness(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1), out _, OmsType.Hedging);
        sim.Quote(1000, 50_000.0m, 50_000.0m)
            .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(0.3m), execAlgorithmId: Twap), position))
            .Quote(2000, 50_000.0m, 50_000.0m)
            .Quote(3000, 50_000.0m, 50_000.0m)
            .Quote(4000, 50_000.0m, 50_000.0m)
            .Run();

        Assert.Equal(3, Slices(sim).Count);
        Assert.All(Slices(sim), o => Assert.Equal(position, sim.Engine.Kernel.Cache.PositionIdFor(o.ClientOrderId)));

        Position held = Assert.Single(sim.Engine.Kernel.Cache.Positions());
        Assert.Equal(position, held.Id);
        Assert.Equal(0.3m, held.Quantity.Value);
    }

    [Fact]
    public void Two_runs_of_one_script_work_the_order_the_same_way()
    {
        static SimHarness Run()
        {
            SimHarness sim = Harness(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1));
            return sim.Quote(1000, 50_000.0m, 50_000.0m)
                .At(1500, s => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, sim.Qty(0.9m), execAlgorithmId: Twap)))
                .Quote(2000, 50_010.0m, 50_010.0m)
                .Quote(3000, 50_020.0m, 50_020.0m)
                .Quote(4000, 50_030.0m, 50_030.0m)
                .Quote(5000, 50_040.0m, 50_040.0m)
                .Run();
        }

        using SimHarness first = Run();
        using SimHarness second = Run();

        Assert.Equal(Fingerprint(first), Fingerprint(second));
    }

    private static string Fingerprint(SimHarness sim) => string.Join(
        " | ",
        Slices(sim).Select(o => $"{o.Quantity} {o.Status} {o.AvgPx} {o.InitEvent.TsInit.Value}"));
}
