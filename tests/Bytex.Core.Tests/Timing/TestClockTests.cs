using Bytex.Core.Model.Events;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Tests.Support;
using Bytex.Core.Timing;

namespace Bytex.Core.Tests.Timing;

// Why: R1.5 and R8.11 - in a backtest the clock is the only source of time, and the order in which due
// timers are returned decides the order in which strategies act. That order must be fully specified.
public class TestClockTests
{
    private static UnixNanos Sec(long seconds) => UnixNanos.FromSeconds(seconds);

    [Fact]
    public void Clock_starts_at_the_epoch_unless_told_otherwise()
    {
        Assert.Equal(UnixNanos.Zero, new TestClock().Timestamp);
        Assert.Equal(Sec(100), new TestClock(Sec(100)).Timestamp);
    }

    [Fact]
    public void Utc_now_is_the_timestamp_as_a_date()
    {
        TestClock clock = new(UnixNanos.Parse("2024-02-29T12:30:00Z"));

        Assert.Equal(new DateTimeOffset(2024, 2, 29, 12, 30, 0, TimeSpan.Zero), clock.UtcNow);
    }

    [Fact]
    public void Advance_moves_time_and_refuses_to_go_backwards()
    {
        TestClock clock = new(Sec(10));

        clock.AdvanceTime(Sec(20));

        Assert.Equal(Sec(20), clock.Timestamp);
        Assert.Throws<ArgumentException>(() => clock.AdvanceTime(Sec(19)));
    }

    [Fact]
    public void Advance_can_collect_due_events_without_moving_time()
    {
        TestClock clock = new(Sec(0));
        clock.SetTimeAlert("a", Sec(5));

        IReadOnlyList<TimeEventHandler> due = clock.AdvanceTime(Sec(10), setTime: false);

        Assert.Single(due);
        Assert.Equal(Sec(0), clock.Timestamp);
    }

    [Fact]
    public void Alert_fires_once_at_its_time_and_is_then_forgotten()
    {
        TestClock clock = new(Sec(0));
        List<TimeEvent> fired = new();
        clock.SetTimeAlert("wake", Sec(5), fired.Add);

        Assert.Empty(clock.AdvanceAndRun(Sec(4)));
        clock.AdvanceAndRun(Sec(5));
        clock.AdvanceAndRun(Sec(60));

        TimeEvent e = Assert.Single(fired);
        Assert.Equal("wake", e.Name);
        Assert.Equal(Sec(5), e.TsEvent);
        Assert.Equal(0, clock.TimerCount);
        Assert.Null(clock.NextTime("wake"));
    }

    [Fact]
    public void Advance_returns_handlers_without_running_them()
    {
        TestClock clock = new(Sec(0));
        int calls = 0;
        clock.SetTimeAlert("a", Sec(1), _ => calls++);

        IReadOnlyList<TimeEventHandler> due = clock.AdvanceTime(Sec(1));

        Assert.Equal(0, calls);
        Assert.Single(due).Handle();
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Alert_in_the_past_fires_on_the_next_advance_by_default_and_can_be_refused()
    {
        TestClock clock = new(Sec(100));
        List<TimeEvent> fired = new();

        clock.SetTimeAlert("late", Sec(50), fired.Add);
        clock.AdvanceAndRun(Sec(100));

        Assert.Equal(Sec(50), Assert.Single(fired).TsEvent);
        Assert.Throws<ArgumentException>(() => clock.SetTimeAlert("refused", Sec(50), null, allowPast: false));
    }

    [Fact]
    public void Repeating_timer_fires_at_every_interval_up_to_and_including_the_target_time()
    {
        TestClock clock = new(Sec(0));
        List<TimeEvent> fired = new();
        clock.SetTimer("pulse", TimeSpan.FromSeconds(10), callback: fired.Add);

        clock.AdvanceAndRun(Sec(30));

        Assert.Equal([Sec(10), Sec(20), Sec(30)], fired.Select(e => e.TsEvent));
        Assert.Equal(Sec(40), clock.NextTime("pulse"));
    }

    [Fact]
    public void Timer_with_fire_immediately_also_fires_at_its_start()
    {
        TestClock clock = new(Sec(100));
        List<TimeEvent> fired = new();
        clock.SetTimer("pulse", TimeSpan.FromSeconds(10), callback: fired.Add, fireImmediately: true);

        clock.AdvanceAndRun(Sec(120));

        Assert.Equal([Sec(100), Sec(110), Sec(120)], fired.Select(e => e.TsEvent));
    }

    [Fact]
    public void Timer_with_an_explicit_start_counts_intervals_from_that_start()
    {
        TestClock clock = new(Sec(7));
        List<TimeEvent> fired = new();
        clock.SetTimer("aligned", TimeSpan.FromSeconds(10), start: Sec(0), callback: fired.Add);

        clock.AdvanceAndRun(Sec(25));

        Assert.Equal([Sec(10), Sec(20)], fired.Select(e => e.TsEvent));
    }

    [Fact]
    public void Timer_stops_after_its_stop_time_which_is_inclusive()
    {
        TestClock clock = new(Sec(0));
        List<TimeEvent> fired = new();
        clock.SetTimer("bounded", TimeSpan.FromSeconds(10), stop: Sec(30), callback: fired.Add);

        clock.AdvanceAndRun(Sec(100));

        Assert.Equal([Sec(10), Sec(20), Sec(30)], fired.Select(e => e.TsEvent));
        Assert.Equal(0, clock.TimerCount);
    }

    [Fact]
    public void Due_events_of_different_timers_are_interleaved_by_time()
    {
        TestClock clock = new(Sec(0));
        clock.SetTimer("every3", TimeSpan.FromSeconds(3));
        clock.SetTimer("every5", TimeSpan.FromSeconds(5));
        clock.SetTimeAlert("at4", Sec(4));

        IReadOnlyList<TimeEvent> events = clock.AdvanceAndRun(Sec(10));

        Assert.Equal(
            ["every3@3", "at4@4", "every5@5", "every3@6", "every3@9", "every5@10"],
            events.Select(e => $"{e.Name}@{e.TsEvent.ToSeconds()}"));
    }

    [Fact]
    public void Events_with_the_same_timestamp_come_in_the_order_their_timers_were_set()
    {
        TestClock clock = new(Sec(0));
        clock.SetTimeAlert("b-set-first", Sec(10));
        clock.SetTimer("a-set-second", TimeSpan.FromSeconds(10));
        clock.SetTimeAlert("c-set-third", Sec(10));

        IReadOnlyList<TimeEvent> events = clock.AdvanceAndRun(Sec(10));

        // Not alphabetical and not by kind: strictly the order of registration.
        Assert.Equal(["b-set-first", "a-set-second", "c-set-third"], events.Select(e => e.Name));
    }

    [Fact]
    public void Cancelled_timer_never_fires()
    {
        TestClock clock = new(Sec(0));
        clock.SetTimer("doomed", TimeSpan.FromSeconds(1));
        clock.SetTimer("kept", TimeSpan.FromSeconds(1));

        clock.CancelTimer("doomed");

        Assert.Equal(["kept"], clock.AdvanceAndRun(Sec(1)).Select(e => e.Name));
        Assert.Equal(["kept"], clock.TimerNames);
    }

    [Fact]
    public void Cancel_all_removes_every_timer_and_cancelling_an_unknown_name_is_harmless()
    {
        TestClock clock = new(Sec(0));
        clock.SetTimer("a", TimeSpan.FromSeconds(1));
        clock.SetTimeAlert("b", Sec(1));

        clock.CancelTimer("unknown");
        clock.CancelTimers();

        Assert.Empty(clock.AdvanceAndRun(Sec(10)));
        Assert.Equal(0, clock.TimerCount);
    }

    [Fact]
    public void Setting_a_timer_under_an_existing_name_replaces_it()
    {
        TestClock clock = new(Sec(0));
        List<string> fired = new();
        clock.SetTimeAlert("x", Sec(5), _ => fired.Add("old"));

        clock.SetTimeAlert("x", Sec(8), _ => fired.Add("new"));
        clock.AdvanceAndRun(Sec(10));

        Assert.Equal(["new"], fired);
    }

    [Fact]
    public void Timers_without_a_callback_use_the_default_handler_registered_before_them()
    {
        TestClock clock = new(Sec(0));
        List<TimeEvent> byDefault = new();
        clock.RegisterDefaultHandler(byDefault.Add);
        clock.SetTimeAlert("plain", Sec(1));

        clock.AdvanceAndRun(Sec(1));

        Assert.Equal("plain", Assert.Single(byDefault).Name);
    }

    [Fact]
    public void Timer_set_by_a_handler_is_picked_up_by_the_next_advance()
    {
        TestClock clock = new(Sec(0));
        List<string> fired = new();
        clock.SetTimeAlert("first", Sec(5), _ => clock.SetTimeAlert("second", Sec(6), _ => fired.Add("second")));

        clock.AdvanceAndRun(Sec(10));
        clock.AdvanceAndRun(Sec(10));

        Assert.Equal(["second"], fired);
    }

    [Fact]
    public void Invalid_timer_arguments_are_rejected()
    {
        TestClock clock = new();

        Assert.Throws<ArgumentException>(() => clock.SetTimer(" ", TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.SetTimer("zero", TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.SetTimer("negative", TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentException>(() => clock.SetTimeAlert("", Sec(1)));
    }

    [Fact]
    public void Two_clocks_given_the_same_script_return_identical_event_sequences()
    {
        static List<string> Run()
        {
            TestClock clock = new(Sec(0));
            clock.SetTimer("t7", TimeSpan.FromSeconds(7));
            clock.SetTimer("t3", TimeSpan.FromSeconds(3), stop: Sec(12));
            clock.SetTimeAlert("a21", Sec(21));
            List<string> log = new();
            foreach (long target in new long[] { 5, 14, 21, 30 })
            {
                log.AddRange(clock.AdvanceAndRun(Sec(target)).Select(e => $"{e.Name}@{e.TsEvent.ToSeconds()}"));
            }

            return log;
        }

        List<string> first = Run();

        Assert.Equal(["t3@3", "t3@6", "t7@7", "t3@9", "t3@12", "t7@14", "t7@21", "a21@21", "t7@28"], first);
        Assert.Equal(first, Run());
    }
}
