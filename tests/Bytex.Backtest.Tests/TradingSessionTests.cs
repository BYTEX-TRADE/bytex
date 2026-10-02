using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;

namespace Bytex.Backtest.Tests;

// Why (R8.26): a DAY order lives until the end of the session it was placed in, and a session is not a calendar day on
// most venues. An equity index future's session ends in the evening and the next begins minutes later, so an order placed
// after that boundary belongs to tomorrow's session - and this engine, which treated the UTC date as the session, expired
// an evening order hours early and kept a morning one hours late.
//
// The run's clock starts at 2025-01-01T00:00:00Z, so a time in milliseconds is a time of that day.
public sealed class TradingSessionTests
{
    /// <summary>Twenty-one hundred UTC, which is where a European index future's session ends.</summary>
    private static readonly TimeSpan _eveningClose = TimeSpan.FromHours(21);

    private static SimOptions Session(TradingSession session) => new() { Session = session };

    private static long AtHour(double hours) => (long)(hours * 3_600_000);

    // ----- the type itself -----

    [Fact]
    public void A_continuous_venues_session_is_the_utc_day()
    {
        TradingSession continuous = TradingSession.Continuous;

        Assert.True(continuous.IsContinuous);
        Assert.Equal(new DateOnly(2025, 1, 1), continuous.SessionOf(Scripted.Ms(AtHour(0.5))));
        Assert.Equal(new DateOnly(2025, 1, 1), continuous.SessionOf(Scripted.Ms(AtHour(23.9))));
        Assert.Equal(new DateOnly(2025, 1, 2), continuous.SessionOf(Scripted.Ms(AtHour(24.1))));
    }

    [Fact]
    public void An_instant_after_the_close_belongs_to_the_next_session()
    {
        // The whole reason the type exists. Both of these are the first of January by the calendar; only one of them is
        // in the session that ends on the first.
        TradingSession session = TradingSession.EndingAt(_eveningClose);

        Assert.Equal(new DateOnly(2025, 1, 1), session.SessionOf(Scripted.Ms(AtHour(20.5))));
        Assert.Equal(new DateOnly(2025, 1, 2), session.SessionOf(Scripted.Ms(AtHour(21.5))));
        // The close itself belongs to the session that opens at it.
        Assert.Equal(new DateOnly(2025, 1, 2), session.SessionOf(Scripted.Ms(AtHour(21))));
    }

    [Fact]
    public void A_session_boundary_is_between_two_instants_or_it_is_not()
    {
        TradingSession session = TradingSession.EndingAt(_eveningClose);

        Assert.False(session.HasEnded(Scripted.Ms(AtHour(9)), Scripted.Ms(AtHour(20))));
        Assert.True(session.HasEnded(Scripted.Ms(AtHour(9)), Scripted.Ms(AtHour(22))));

        // Placed after the close, so it is in tomorrow's session: the calendar rolling over at midnight is not its end.
        Assert.False(session.HasEnded(Scripted.Ms(AtHour(22)), Scripted.Ms(AtHour(26))));
        Assert.True(session.HasEnded(Scripted.Ms(AtHour(22)), Scripted.Ms(AtHour(46))));
    }

    [Fact]
    public void A_whole_day_and_nothing_both_mean_midnight()
    {
        Assert.True(TradingSession.EndingAt(TimeSpan.FromDays(1)).IsContinuous);
        Assert.True(TradingSession.EndingAt(TimeSpan.Zero).IsContinuous);
        Assert.Throws<ArgumentOutOfRangeException>(() => TradingSession.EndingAt(TimeSpan.FromHours(25)));
        Assert.Throws<ArgumentOutOfRangeException>(() => TradingSession.EndingAt(TimeSpan.FromHours(-1)));
    }

    // ----- what it does to a DAY order -----

    [Fact]
    public void A_day_order_expires_at_the_venues_close_rather_than_at_midnight()
    {
        // Placed in the morning, still resting at eight in the evening, gone by ten. Before this it would have rested
        // three hours past the close, and a fill in those hours is a fill that could not have happened.
        using SimHarness sim = SimHarness.Spot(Session(TradingSession.EndingAt(_eveningClose)));
        LimitOrder? order = null;
        sim.Quote(AtHour(9), 100.00m, 100.10m)
            .At(AtHour(9.1), s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(99.50m), TimeInForce.Day)))
            .Quote(AtHour(20), 100.00m, 100.10m)
            .Quote(AtHour(22), 99.30m, 99.40m)
            .Run();

        Assert.Empty(sim.Fills(order!));
        Assert.Equal(OrderStatus.Expired, order!.Status);
    }

    [Fact]
    public void A_day_order_placed_after_the_close_lives_through_the_next_session()
    {
        // This is what the old rule got wrong in the other direction: an order placed at ten in the evening was treated
        // as belonging to that calendar day and expired two hours later, at midnight, having seen no session at all.
        using SimHarness sim = SimHarness.Spot(Session(TradingSession.EndingAt(_eveningClose)));
        LimitOrder? order = null;
        sim.Quote(AtHour(22), 100.00m, 100.10m)
            .At(AtHour(22.1), s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(99.50m), TimeInForce.Day)))
            .Quote(AtHour(26), 100.00m, 100.10m)   // two in the morning: still the same session
            .Quote(AtHour(30), 99.30m, 99.40m)     // six in the morning: it fills
            .Run();

        Assert.Equal(sim.Px(99.50m), sim.SingleFill(order!).LastPx);
    }

    [Fact]
    public void And_expires_at_the_close_of_that_next_session()
    {
        using SimHarness sim = SimHarness.Spot(Session(TradingSession.EndingAt(_eveningClose)));
        LimitOrder? order = null;
        sim.Quote(AtHour(22), 100.00m, 100.10m)
            .At(AtHour(22.1), s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(99.50m), TimeInForce.Day)))
            .Quote(AtHour(46), 99.30m, 99.40m)     // ten in the evening the next day, an hour past its close
            .Run();

        Assert.Empty(sim.Fills(order!));
        Assert.Equal(OrderStatus.Expired, order!.Status);
    }

    [Fact]
    public void A_venue_that_never_closes_expires_a_day_order_when_the_date_rolls_over()
    {
        // Unchanged behaviour, and the default: what a crypto venue does with a DAY order, and what this engine did for
        // every venue before a session could be stated.
        using SimHarness sim = SimHarness.Spot();
        LimitOrder? survives = null;
        sim.Quote(AtHour(9), 100.00m, 100.10m)
            .At(AtHour(9.1), s => survives = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(99.50m), TimeInForce.Day)))
            .Quote(AtHour(22), 99.30m, 99.40m)     // ten in the evening, same UTC day: it fills
            .Run();

        Assert.Equal(sim.Px(99.50m), sim.SingleFill(survives!).LastPx);

        using SimHarness next = SimHarness.Spot();
        LimitOrder? expires = null;
        next.Quote(AtHour(9), 100.00m, 100.10m)
            .At(AtHour(9.1), s => expires = s.Submit(s.Orders.Limit(next.Id, OrderSide.Buy, next.Qty(1m), next.Px(99.50m), TimeInForce.Day)))
            .Quote(AtHour(26), 99.30m, 99.40m)     // two the next morning: gone
            .Run();

        Assert.Equal(OrderStatus.Expired, expires!.Status);
    }

    [Fact]
    public void A_session_does_nothing_to_an_order_that_is_not_a_day_order()
    {
        using SimHarness sim = SimHarness.Spot(Session(TradingSession.EndingAt(_eveningClose)));
        LimitOrder? order = null;
        sim.Quote(AtHour(9), 100.00m, 100.10m)
            .At(AtHour(9.1), s => order = s.Submit(s.Orders.Limit(sim.Id, OrderSide.Buy, sim.Qty(1m), sim.Px(99.50m))))
            .Quote(AtHour(46), 99.30m, 99.40m)
            .Run();

        Assert.Equal(sim.Px(99.50m), sim.SingleFill(order!).LastPx);
    }
}
