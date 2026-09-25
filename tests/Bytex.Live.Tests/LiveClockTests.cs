using System.Threading.Channels;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Timing;

namespace Bytex.Live.Tests;

// Why: in a live node timers fire on thread-pool threads. The clock must hand every due event to the
// dispatcher (the kernel loop) instead of running user callbacks itself, and cancelled timers must stay silent.
public sealed class LiveClockTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task A_due_timer_is_handed_to_the_dispatcher_and_the_callback_runs_only_when_the_dispatcher_runs_it()
    {
        Channel<TimeEventHandler> dispatched = Channel.CreateUnbounded<TimeEventHandler>();
        using LiveClock clock = new(handler => dispatched.Writer.TryWrite(handler));
        List<string> fired = new();

        clock.SetTimer("pulse", TimeSpan.FromMilliseconds(20), callback: e => fired.Add(e.Name));
        using CancellationTokenSource cts = new(_timeout);
        TimeEventHandler handler = await dispatched.Reader.ReadAsync(cts.Token);
        int firedBeforeDispatch = fired.Count;
        handler.Handle();

        Assert.Equal(0, firedBeforeDispatch);
        Assert.Equal(["pulse"], fired);
        Assert.Equal("pulse", handler.Event.Name);
    }

    [Fact]
    public async Task A_repeating_timer_fires_at_start_plus_whole_intervals()
    {
        Channel<TimeEventHandler> dispatched = Channel.CreateUnbounded<TimeEventHandler>();
        using LiveClock clock = new(handler => dispatched.Writer.TryWrite(handler));
        UnixNanos start = clock.Timestamp;

        clock.SetTimer("pulse", TimeSpan.FromMilliseconds(25), start: start);
        using CancellationTokenSource cts = new(_timeout);
        TimeEvent first = (await dispatched.Reader.ReadAsync(cts.Token)).Event;
        TimeEvent second = (await dispatched.Reader.ReadAsync(cts.Token)).Event;
        TimeEvent third = (await dispatched.Reader.ReadAsync(cts.Token)).Event;

        Assert.Equal(start.AddNanos(25_000_000), first.TsEvent);
        Assert.Equal(start.AddNanos(50_000_000), second.TsEvent);
        Assert.Equal(start.AddNanos(75_000_000), third.TsEvent);
    }

    [Fact]
    public async Task A_time_alert_fires_once_and_is_then_forgotten()
    {
        Channel<TimeEventHandler> dispatched = Channel.CreateUnbounded<TimeEventHandler>();
        using LiveClock clock = new(handler => dispatched.Writer.TryWrite(handler));

        // That a pending alert is registered is read from one that cannot have fired: due in a minute. Reading the
        // names of the 30-millisecond alert below raced its own timer - a stall of 30 ms between setting it and
        // reading them left the list empty - and that race, not the clock, is what went red now and then.
        UnixNanos later = clock.Timestamp.Add(TimeSpan.FromMinutes(1));
        clock.SetTimeAlert("pending", later);

        Assert.Equal(["pending"], clock.TimerNames);
        Assert.Equal(later, clock.NextTime("pending"));

        clock.CancelTimer("pending");
        Assert.Equal(0, clock.TimerCount);

        // That it fires once and is then forgotten is read after the event has arrived, which needs no timing.
        UnixNanos due = clock.Timestamp.Add(TimeSpan.FromMilliseconds(30));
        clock.SetTimeAlert("once", due);
        using CancellationTokenSource cts = new(_timeout);
        TimeEvent e = (await dispatched.Reader.ReadAsync(cts.Token)).Event;

        Assert.Equal("once", e.Name);
        Assert.Equal(due, e.TsEvent);
        Assert.Equal(0, clock.TimerCount);
        Assert.Null(clock.NextTime("once"));
    }

    [Fact]
    public async Task A_cancelled_timer_never_fires()
    {
        Channel<TimeEventHandler> dispatched = Channel.CreateUnbounded<TimeEventHandler>();
        using LiveClock clock = new(handler => dispatched.Writer.TryWrite(handler));

        // Half a second before the cancelled timer is due, against two statements: a loaded machine can be late, and
        // it cannot be half a second late between setting a timer and cancelling it. At a hundred milliseconds this
        // test failed in a full parallel suite run for a reason that was never the clock's.
        clock.SetTimer("cancelled", TimeSpan.FromMilliseconds(500));
        clock.SetTimer("sentinel", TimeSpan.FromMilliseconds(1_500));
        clock.CancelTimer("cancelled");
        using CancellationTokenSource cts = new(_timeout);
        TimeEvent first = (await dispatched.Reader.ReadAsync(cts.Token)).Event;

        Assert.Equal("sentinel", first.Name); // the cancelled timer was due a second earlier and stayed silent
        Assert.Equal(["sentinel"], clock.TimerNames);
    }

    // Why: an alert whose time had already passed was dispatched inside SetTimeAlert, on the caller's thread. An actor
    // that sets one while starting is not running yet, so it dropped its own alert and waited for it for ever.
    // The alert is set on a thread of its own. A continuation never runs on such a thread, only on the thread pool, so
    // the two thread ids say whether the clock fired it on the caller's stack, without depending on timing.
    private static async Task<(TimeEvent Event, int CallerThread, int FiringThread)> FiredAsync(Action<LiveClock> set)
    {
        Channel<TimeEventHandler> dispatched = Channel.CreateUnbounded<TimeEventHandler>();
        int firingThread = 0;
        using LiveClock clock = new(handler =>
        {
            firingThread = Environment.CurrentManagedThreadId;
            dispatched.Writer.TryWrite(handler);
        });

        int callerThread = 0;
        Thread caller = new(() =>
        {
            callerThread = Environment.CurrentManagedThreadId;
            set(clock);
        }) { IsBackground = true };
        caller.Start();
        caller.Join();

        using CancellationTokenSource cts = new(_timeout);
        TimeEvent e = (await dispatched.Reader.ReadAsync(cts.Token)).Event;
        return (e, callerThread, firingThread);
    }

    [Fact]
    public async Task An_alert_whose_time_has_passed_is_not_fired_by_the_thread_that_sets_it()
    {
        UnixNanos due = UnixNanos.FromDateTimeOffset(DateTimeOffset.UtcNow).Subtract(TimeSpan.FromMinutes(1));

        (TimeEvent e, int callerThread, int firingThread) = await FiredAsync(clock => clock.SetTimeAlert("already-due", due));

        Assert.NotEqual(callerThread, firingThread);
        Assert.Equal("already-due", e.Name);
        Assert.Equal(due, e.TsEvent);
    }

    // The same for a timer asked to fire at once: its first tick is not run by the caller either.
    [Fact]
    public async Task A_timer_that_fires_immediately_is_not_ticked_by_the_thread_that_sets_it()
    {
        (TimeEvent e, int callerThread, int firingThread) = await FiredAsync(clock => clock.SetTimer("immediate", TimeSpan.FromMinutes(5), fireImmediately: true));

        Assert.NotEqual(callerThread, firingThread);
        Assert.Equal("immediate", e.Name);
    }

    [Fact]
    public void An_alert_in_the_past_is_refused_when_past_alerts_are_not_allowed()
    {
        using LiveClock clock = new();

        Assert.Throws<ArgumentException>(() => clock.SetTimeAlert("late", clock.Timestamp.Subtract(TimeSpan.FromMinutes(1)), allowPast: false));
        Assert.Equal(0, clock.TimerCount);
    }

    [Fact]
    public void The_live_clock_reads_wall_clock_utc_time()
    {
        using LiveClock clock = new();
        DateTimeOffset before = DateTimeOffset.UtcNow;

        DateTimeOffset reading = clock.Timestamp.ToDateTimeOffset();

        Assert.InRange(reading, before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
    }
}
