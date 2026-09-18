using Bytex.Core.Model.Events;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Timing;

namespace Bytex.Core.Tests.Timing;

// Why: the live clock is wall-clock driven, so only the parts that are deterministic without waiting are
// tested: overdue alerts (which fire synchronously), dispatching, registration and cancellation.
public class LiveClockTests
{
    [Fact]
    public void Timestamp_is_the_current_wall_clock_time()
    {
        using LiveClock clock = new();

        UnixNanos before = UnixNanos.FromDateTimeOffset(DateTimeOffset.UtcNow);
        UnixNanos now = clock.Timestamp;
        UnixNanos after = UnixNanos.FromDateTimeOffset(DateTimeOffset.UtcNow);

        Assert.InRange(now.Value, before.Value, after.Value);
    }

    [Fact]
    public void Overdue_alert_is_handed_to_the_dispatcher_instead_of_being_run_by_the_clock()
    {
        List<TimeEventHandler> dispatched = new();
        using LiveClock clock = new(dispatched.Add);
        int calls = 0;

        clock.SetTimeAlert("overdue", UnixNanos.FromSeconds(1), _ => calls++);

        TimeEventHandler handler = Assert.Single(dispatched);
        Assert.Equal("overdue", handler.Event.Name);
        Assert.Equal(UnixNanos.FromSeconds(1), handler.Event.TsEvent);
        Assert.Equal(0, calls);
        handler.Handle();
        Assert.Equal(1, calls);
        Assert.Equal(0, clock.TimerCount);
    }

    [Fact]
    public void Without_a_dispatcher_the_callback_runs_inline()
    {
        using LiveClock clock = new();
        List<TimeEvent> fired = new();

        clock.SetTimeAlert("overdue", UnixNanos.FromSeconds(1), fired.Add);

        Assert.Equal("overdue", Assert.Single(fired).Name);
    }

    [Fact]
    public void Overdue_alert_without_a_callback_uses_the_default_handler()
    {
        using LiveClock clock = new();
        List<TimeEvent> fired = new();
        clock.RegisterDefaultHandler(fired.Add);

        clock.SetTimeAlert("overdue", UnixNanos.FromSeconds(1));

        Assert.Single(fired);
    }

    [Fact]
    public void Alert_in_the_past_can_be_refused()
    {
        using LiveClock clock = new();

        Assert.Throws<ArgumentException>(() => clock.SetTimeAlert("past", UnixNanos.FromSeconds(1), null, allowPast: false));
        Assert.Equal(0, clock.TimerCount);
    }

    [Fact]
    public void Future_timer_is_registered_with_its_next_time_and_can_be_cancelled()
    {
        using LiveClock clock = new();
        UnixNanos before = clock.Timestamp;

        clock.SetTimer("hourly", TimeSpan.FromHours(1));

        Assert.Equal(["hourly"], clock.TimerNames);
        UnixNanos next = Assert.NotNull(clock.NextTime("hourly"));
        Assert.True(next >= before + TimeSpan.FromHours(1));
        clock.CancelTimer("hourly");
        Assert.Equal(0, clock.TimerCount);
        Assert.Null(clock.NextTime("hourly"));
    }

    [Fact]
    public void Setting_a_timer_under_an_existing_name_replaces_it()
    {
        using LiveClock clock = new();
        clock.SetTimer("t", TimeSpan.FromHours(1));

        clock.SetTimer("t", TimeSpan.FromHours(2));

        Assert.Equal(1, clock.TimerCount);
    }

    [Fact]
    public void Dispose_cancels_all_timers()
    {
        LiveClock clock = new();
        clock.SetTimer("a", TimeSpan.FromHours(1));
        clock.SetTimeAlert("b", clock.Timestamp + TimeSpan.FromHours(1));

        clock.Dispose();

        Assert.Equal(0, clock.TimerCount);
    }

    [Fact]
    public void Invalid_timer_arguments_are_rejected()
    {
        using LiveClock clock = new();

        Assert.Throws<ArgumentOutOfRangeException>(() => clock.SetTimer("zero", TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => clock.SetTimer(" ", TimeSpan.FromSeconds(1)));
    }
}
