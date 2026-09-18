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
        UnixNanos due = clock.Timestamp.Add(TimeSpan.FromMilliseconds(30));

        clock.SetTimeAlert("once", due);
        IReadOnlyList<string> namesWhilePending = clock.TimerNames;
        using CancellationTokenSource cts = new(_timeout);
        TimeEvent e = (await dispatched.Reader.ReadAsync(cts.Token)).Event;

        Assert.Equal(["once"], namesWhilePending);
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

        clock.SetTimer("cancelled", TimeSpan.FromMilliseconds(100));
        clock.SetTimer("sentinel", TimeSpan.FromMilliseconds(300));
        clock.CancelTimer("cancelled");
        using CancellationTokenSource cts = new(_timeout);
        TimeEvent first = (await dispatched.Reader.ReadAsync(cts.Token)).Event;

        Assert.Equal("sentinel", first.Name); // the cancelled timer was due 200 ms earlier and stayed silent
        Assert.Equal(["sentinel"], clock.TimerNames);
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
