using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;

namespace Bytex.Backtest.Tests;

// Why: nine order types and seven times in force is sixty-three combinations, and the interesting question about
// each is not what it fills at - the type's own tests cover that - but whether it ever FINISHES. An order that is
// neither filled nor cancelled nor expired nor rejected is an order the venue is still holding when the run ends,
// and a strategy waiting on it waits for ever. A grep can show that every pair is mentioned somewhere; only running
// them can show that none of them hangs.
//
// The matrix is generated rather than written out, so a new order type or a new time in force is covered the moment
// it exists rather than when somebody remembers.
public sealed class OrderLifecycleMatrixTests
{
    private static readonly OrderType[] _types =
    [
        OrderType.Market, OrderType.Limit, OrderType.StopMarket, OrderType.StopLimit,
        OrderType.MarketIfTouched, OrderType.LimitIfTouched, OrderType.TrailingStopMarket,
        OrderType.TrailingStopLimit, OrderType.MarketToLimit,
    ];

    private static readonly TimeInForce[] _tifs =
    [
        TimeInForce.Gtc, TimeInForce.Ioc, TimeInForce.Fok, TimeInForce.Gtd,
        TimeInForce.Day, TimeInForce.AtTheOpen, TimeInForce.AtTheClose,
    ];

    public static TheoryData<OrderType, TimeInForce> Matrix()
    {
        TheoryData<OrderType, TimeInForce> data = new();
        foreach (OrderType type in _types)
        {
            foreach (TimeInForce tif in _tifs)
            {
                data.Add(type, tif);
            }
        }

        return data;
    }

    [Fact]
    public void The_matrix_covers_every_type_and_every_time_in_force_the_engine_defines()
    {
        // The guard on the matrix: a new order type or time in force has to appear here, or the sweep silently stops
        // being a sweep.
        Assert.Equal(
            Enum.GetValues<OrderType>().Order().ToArray(),
            _types.Order().ToArray());
        Assert.Equal(
            Enum.GetValues<TimeInForce>().Order().ToArray(),
            _tifs.Order().ToArray());
        Assert.Equal(_types.Length * _tifs.Length, Matrix().Count);
    }

    /// <summary>
    /// Whether this time in force promises the order will be over by the end of a run that outlives it. Good till
    /// cancelled promises the opposite - a limit whose price never comes back rests for ever, and that is the point
    /// of it - so only the bounded ones are held to finishing.
    /// </summary>
    private static bool PromisesAnEnd(TimeInForce tif) => tif is not TimeInForce.Gtc;

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Every_order_whose_time_in_force_promises_an_end_reaches_one(OrderType type, TimeInForce tif)
    {
        using SimHarness sim = SimHarness.Spot();
        Order? order = null;

        // A market that moves through every trigger in both directions, and enough of it that a day order sees a
        // date roll over: whatever this order is waiting for, it happens.
        sim.Quote(1000, 99.99m, 100.00m, size: 1_000m)
            .At(1500, s => order = Submit(s, sim, type, tif))
            .Quote(2000, 104.99m, 105.00m, size: 1_000m)
            .Bar(3000, open: 105.00m, high: 110.00m, low: 94.00m, close: 95.00m, volume: 10_000m)
            .Quote(4000, 94.99m, 95.00m, size: 1_000m)
            .Bar(5000, open: 95.00m, high: 101.00m, low: 95.00m, close: 100.00m, volume: 10_000m)
            .Quote(90_000_000, 99.99m, 100.00m, size: 1_000m)
            .Run();

        if (order is null)
        {
            // The combination is one the engine refuses to build, which is a legitimate answer - a trailing stop
            // needs an offset, a GTD order needs an expiry. What must not happen is an order that exists and hangs.
            return;
        }

        if (PromisesAnEnd(tif))
        {
            Assert.True(
                order.IsClosed || order.Status is OrderStatus.Rejected or OrderStatus.Denied,
                $"a {type} {tif} order ended the run as {order.Status}. That time in force promises the order will "
                + "be over - immediately, at an expiry, at the end of a day or at an auction - so the venue still "
                + "holding it means anything waiting on it waits for ever.");
            return;
        }

        // Good till cancelled may rest, and resting is not hanging. What it must not do is sit in a state that is
        // waiting for the venue to answer: an order left pending at the end of a run is one whose last command
        // nobody completed.
        Assert.False(
            order.Status is OrderStatus.PendingUpdate or OrderStatus.PendingCancel or OrderStatus.Submitted,
            $"a {type} {tif} order ended the run as {order.Status}: a command was sent and never answered.");
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void No_order_ends_a_run_filled_for_more_than_it_asked(OrderType type, TimeInForce tif)
    {
        // The other thing worth asking of all sixty-three at once: whatever route an order took through triggers,
        // bars and quotes, it cannot come out the other side having traded more than its quantity. An overfill is
        // the one arithmetic mistake a strategy cannot correct for afterwards.
        using SimHarness sim = SimHarness.Spot();
        Order? order = null;
        sim.Quote(1000, 99.99m, 100.00m, size: 1_000m)
            .At(1500, s => order = Submit(s, sim, type, tif))
            .Quote(2000, 104.99m, 105.00m, size: 1_000m)
            .Bar(3000, open: 105.00m, high: 110.00m, low: 94.00m, close: 95.00m, volume: 10_000m)
            .Quote(4000, 94.99m, 95.00m, size: 1_000m)
            .Run();

        if (order is null)
        {
            return;
        }

        Assert.True(
            order.FilledQuantity.Value <= order.Quantity.Value,
            $"a {type} {tif} order asked for {order.Quantity} and filled {order.FilledQuantity}");
        Assert.Equal(
            Math.Max(0m, order.Quantity.Value - order.FilledQuantity.Value),
            order.LeavesQuantity.Value);
    }

    /// <summary>
    /// Builds one order of each type with whatever that type needs to be legal, or returns null where the
    /// combination cannot be built at all.
    /// </summary>
    private static Order? Submit(ScriptedStrategy s, SimHarness sim, OrderType type, TimeInForce tif)
    {
        Quantity quantity = sim.Qty(1m);
        UnixNanos expiry = Scripted.Ms(80_000);
        try
        {
            return type switch
            {
                OrderType.Market => s.Submit(s.Orders.Market(sim.Id, OrderSide.Buy, quantity, timeInForce: tif)),
                OrderType.Limit => s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, quantity, sim.Px(100.00m), tif, expireTime: Expiry(tif, expiry))),
                OrderType.StopMarket => s.Submit(s.Orders.StopMarket(sim.Id, OrderSide.Buy, quantity, sim.Px(104.00m), timeInForce: tif, expireTime: Expiry(tif, expiry))),
                OrderType.StopLimit => s.Submit(s.Orders.StopLimit(sim.Id, OrderSide.Buy, quantity, sim.Px(106.00m), sim.Px(104.00m), timeInForce: tif, expireTime: Expiry(tif, expiry))),
                OrderType.MarketIfTouched => s.Submit(s.Orders.MarketIfTouched(sim.Id, OrderSide.Buy, quantity, sim.Px(96.00m), timeInForce: tif, expireTime: Expiry(tif, expiry))),
                OrderType.LimitIfTouched => s.Submit(s.Orders.LimitIfTouched(sim.Id, OrderSide.Buy, quantity, sim.Px(97.00m), sim.Px(96.00m), timeInForce: tif, expireTime: Expiry(tif, expiry))),
                OrderType.TrailingStopMarket => s.Submit(s.Orders.TrailingStopMarket(sim.Id, OrderSide.Sell, quantity, 1.00m, timeInForce: tif, expireTime: Expiry(tif, expiry))),
                OrderType.TrailingStopLimit => s.Submit(s.Orders.TrailingStopLimit(sim.Id, OrderSide.Sell, quantity, 1.00m, 0.50m, timeInForce: tif, expireTime: Expiry(tif, expiry))),
                OrderType.MarketToLimit => s.Submit(s.Orders.MarketToLimit(sim.Id, OrderSide.Buy, quantity, timeInForce: tif)),
                _ => null,
            };
        }
        catch (ArgumentException)
        {
            // The type and the time in force cannot be combined, and the order factory says so rather than building
            // something the venue would have to guess about. That is the right answer, not a gap.
            return null;
        }
    }

    private static UnixNanos? Expiry(TimeInForce tif, UnixNanos when) => tif == TimeInForce.Gtd ? when : null;
}
