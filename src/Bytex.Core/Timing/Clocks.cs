using Bytex.Core.Model.Events;
using Bytex.Core.Model.Primitives;

namespace Bytex.Core.Timing;

/// <summary>
/// The single source of time for a tradingRuntime. Hosts time alerts and repeating timers.
/// </summary>
public interface IClock
{
    UnixNanos Timestamp { get; }

    DateTimeOffset UtcNow { get; }

    IReadOnlyList<string> TimerNames { get; }

    int TimerCount { get; }

    /// <summary>
    /// Registers a default handler used by timers created without a callback.
    /// </summary>
    void RegisterDefaultHandler(Action<TimeEvent> handler);

    /// <summary>
    /// Schedules a one-shot alert at the given time.
    /// </summary>
    void SetTimeAlert(string name, UnixNanos alertTime, Action<TimeEvent>? callback = null, bool allowPast = true);

    /// <summary>
    /// Schedules a repeating timer.
    /// </summary>
    void SetTimer(string name, TimeSpan interval, UnixNanos? start = null, UnixNanos? stop = null, Action<TimeEvent>? callback = null, bool fireImmediately = false);

    UnixNanos? NextTime(string name);

    void CancelTimer(string name);

    void CancelTimers();
}

/// <summary>
/// Where a repeating timer stood: when it would next have fired, and how often it repeats.
///
/// <para>
/// Any past firing is a valid anchor, because a repeating schedule is the same schedule read from any of its own
/// boundaries - so the next time is all that has to survive a restart to reconstruct the phase.
/// </para>
/// </summary>
public readonly record struct TimerSchedule(long NextNanos, long IntervalNanos);

/// <summary>
/// What a repeating schedule does when the process that was keeping it comes back.
///
/// <para>
/// Separated from the clock so the rule can be read and tested on its own, rather than through whatever the wall
/// clock happens to say. It is a rule with two halves and both are decisions: the PHASE survives, and the firings
/// that fell while nothing was running are NOT replayed. A four-hour rule that should have run at 04:00 and 08:00 is
/// not run twice at 13:00 - that is acting on a market which has already moved - so the schedule lands on the first
/// boundary still ahead and says how many it passed over.
/// </para>
/// </summary>
public static class TimerResumption
{
    /// <summary>
    /// Where a saved schedule next falls after <paramref name="now"/>, and how many of its boundaries went by
    /// unrun. A schedule already in the future is kept exactly, and nothing is counted as missed.
    /// </summary>
    public static (UnixNanos Next, long Skipped) NextFrom(TimerSchedule schedule, UnixNanos now)
    {
        if (schedule.IntervalNanos <= 0)
        {
            return (new UnixNanos(schedule.NextNanos), 0);
        }

        long next = schedule.NextNanos;
        if (next > now.Value)
        {
            return (new UnixNanos(next), 0);
        }

        // Integer arithmetic rather than a loop: a node down for a month against a one-second timer is two and a half
        // million boundaries, and counting them one at a time is a pause nobody asked for at the moment of starting.
        long behind = now.Value - next;
        long steps = (behind / schedule.IntervalNanos) + 1;

        return (new UnixNanos(next + (steps * schedule.IntervalNanos)), steps);
    }
}

internal sealed class TimerEntry
{
    public TimerEntry(string name, long intervalNanos, UnixNanos start, UnixNanos? stop, Action<TimeEvent>? callback, bool repeating)
    {
        Name = name;
        IntervalNanos = intervalNanos;
        NextTime = start;
        Stop = stop;
        Callback = callback;
        Repeating = repeating;
    }

    public string Name { get; }

    public long IntervalNanos { get; }

    public UnixNanos NextTime { get; set; }

    public UnixNanos? Stop { get; }

    public Action<TimeEvent>? Callback { get; }

    public bool Repeating { get; }

    public bool IsExpired { get; set; }

    public long Sequence { get; set; }

    public TimeEvent Pop(UnixNanos createdTime)
    {
        TimeEvent e = new(Name, Guid.NewGuid(), NextTime, createdTime);
        if (Repeating)
        {
            NextTime = NextTime.AddNanos(IntervalNanos);
            if (Stop is { } stop && NextTime > stop)
            {
                IsExpired = true;
            }
        }
        else
        {
            IsExpired = true;
        }

        return e;
    }
}

/// <summary>
/// A handler paired with the event it should receive.
/// </summary>
public readonly record struct TimeEventHandler(TimeEvent Event, Action<TimeEvent>? Callback)
{
    public void Handle() => Callback?.Invoke(Event);
}

/// <summary>
/// Clock whose time only moves when explicitly set or advanced. Used by backtests and tests.
/// </summary>
public sealed class TestClock : IClock
{
    private readonly Dictionary<string, TimerEntry> _timers = new(StringComparer.Ordinal);
    private Action<TimeEvent>? _defaultHandler;
    private long _sequence;

    public TestClock(UnixNanos? initial = null)
    {
        Timestamp = initial ?? UnixNanos.Zero;
    }

    public UnixNanos Timestamp { get; private set; }

    public DateTimeOffset UtcNow => Timestamp.ToDateTimeOffset();

    public IReadOnlyList<string> TimerNames => _timers.Keys.ToList();

    public int TimerCount => _timers.Count;

    public void RegisterDefaultHandler(Action<TimeEvent> handler) => _defaultHandler = handler;

    public void SetTime(UnixNanos time) => Timestamp = time;

    public void SetTimeAlert(string name, UnixNanos alertTime, Action<TimeEvent>? callback = null, bool allowPast = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!allowPast && alertTime < Timestamp)
        {
            throw new ArgumentException($"Alert time {alertTime} is in the past (now {Timestamp}).", nameof(alertTime));
        }

        _timers[name] = new TimerEntry(name, 0, alertTime, null, callback ?? _defaultHandler, repeating: false) { Sequence = _sequence++ };
    }

    public void SetTimer(string name, TimeSpan interval, UnixNanos? start = null, UnixNanos? stop = null, Action<TimeEvent>? callback = null, bool fireImmediately = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        long intervalNanos = interval.Ticks * UnixNanos.NanosPerTick;
        UnixNanos startTime = start ?? Timestamp;
        UnixNanos first = fireImmediately ? startTime : startTime.AddNanos(intervalNanos);
        _timers[name] = new TimerEntry(name, intervalNanos, first, stop, callback ?? _defaultHandler, repeating: true) { Sequence = _sequence++ };
    }

    public UnixNanos? NextTime(string name) => _timers.TryGetValue(name, out TimerEntry? timer) ? timer.NextTime : null;

    public void CancelTimer(string name) => _timers.Remove(name);

    public void CancelTimers() => _timers.Clear();

    /// <summary>
    /// Advances the clock to <paramref name="to"/> and returns all time events that became due, in order.
    /// The handlers are not invoked; the caller decides when to run them.
    /// </summary>
    public IReadOnlyList<TimeEventHandler> AdvanceTime(UnixNanos to, bool setTime = true)
    {
        if (to < Timestamp)
        {
            throw new ArgumentException($"Cannot advance to {to}, before current time {Timestamp}.", nameof(to));
        }

        List<TimeEventHandler> due = new();
        if (_timers.Count > 0)
        {
            List<TimerEntry> ordered = _timers.Values.ToList();
            bool any = true;
            while (any)
            {
                any = false;
                TimerEntry? next = null;
                foreach (TimerEntry timer in ordered)
                {
                    if (timer.IsExpired || timer.NextTime > to)
                    {
                        continue;
                    }

                    if (next is null || timer.NextTime < next.NextTime || (timer.NextTime == next.NextTime && timer.Sequence < next.Sequence))
                    {
                        next = timer;
                    }
                }

                if (next is not null)
                {
                    any = true;
                    TimeEvent e = next.Pop(next.NextTime);
                    due.Add(new TimeEventHandler(e, next.Callback));
                }
            }

            foreach (TimerEntry timer in ordered.Where(t => t.IsExpired))
            {
                _timers.Remove(timer.Name);
            }
        }

        if (setTime)
        {
            Timestamp = to;
        }

        return due;
    }
}

/// <summary>
/// Wall-clock time. Timers run on background tasks and deliver events through a dispatcher so that
/// callbacks execute on the tradingRuntime thread.
/// </summary>
public sealed class LiveClock : IClock, IDisposable
{
    private readonly Dictionary<string, LiveTimer> _timers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TimerSchedule> _resumed = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly Action<TimeEventHandler> _dispatcher;
    private Action<TimeEvent>? _defaultHandler;

    /// <param name="dispatcher">Receives due events; responsible for running them on the tradingRuntime thread. Null runs them inline.</param>
    public LiveClock(Action<TimeEventHandler>? dispatcher = null)
    {
        _dispatcher = dispatcher ?? (h => h.Handle());
    }

    public UnixNanos Timestamp => UnixNanos.FromDateTimeOffset(DateTimeOffset.UtcNow);

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public IReadOnlyList<string> TimerNames
    {
        get
        {
            lock (_gate)
            {
                return _timers.Keys.ToList();
            }
        }
    }

    public int TimerCount
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count;
            }
        }
    }

    public void RegisterDefaultHandler(Action<TimeEvent> handler) => _defaultHandler = handler;

    public void SetTimeAlert(string name, UnixNanos alertTime, Action<TimeEvent>? callback = null, bool allowPast = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        UnixNanos now = Timestamp;
        if (!allowPast && alertTime < now)
        {
            throw new ArgumentException($"Alert time {alertTime} is in the past.", nameof(alertTime));
        }

        Schedule(new TimerEntry(name, 0, alertTime, null, callback ?? _defaultHandler, repeating: false));
    }

    public void SetTimer(string name, TimeSpan interval, UnixNanos? start = null, UnixNanos? stop = null, Action<TimeEvent>? callback = null, bool fireImmediately = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        long intervalNanos = interval.Ticks * UnixNanos.NanosPerTick;

        if (start is null && Resumption(name, intervalNanos) is { } resumed)
        {
            Schedule(new TimerEntry(name, intervalNanos, resumed, stop, callback ?? _defaultHandler, repeating: true));
            return;
        }

        UnixNanos startTime = start ?? Timestamp;
        UnixNanos first = fireImmediately ? startTime : startTime.AddNanos(intervalNanos);
        Schedule(new TimerEntry(name, intervalNanos, first, stop, callback ?? _defaultHandler, repeating: true));
    }

    /// <summary>
    /// Where every repeating timer stands, for a node that is about to stop and mean to come back on the same
    /// schedule. One-shot alerts are left out: an alert is a moment somebody asked for, and a moment that has passed
    /// while a node was down is not one it can keep.
    /// </summary>
    public IReadOnlyDictionary<string, TimerSchedule> Schedules()
    {
        lock (_gate)
        {
            return _timers
                .Where(t => t.Value.Entry.Repeating)
                .ToDictionary(t => t.Key, t => new TimerSchedule(t.Value.Entry.NextTime.Value, t.Value.Entry.IntervalNanos), StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Hands back schedules a previous run of this node was keeping, to be honoured by the next <see cref="SetTimer"/>
    /// that names one of them and asks for the same interval. Call it before the components that own those timers
    /// start, because they set their timers as they start.
    /// </summary>
    public void Resume(IReadOnlyDictionary<string, TimerSchedule> schedules)
    {
        ArgumentNullException.ThrowIfNull(schedules);
        lock (_gate)
        {
            _resumed.Clear();
            foreach ((string name, TimerSchedule schedule) in schedules)
            {
                _resumed[name] = schedule;
            }
        }
    }

    /// <summary>How many firings were skipped because the node was not running for them.</summary>
    public long Missed { get; private set; }

    /// <summary>
    /// The next boundary of a schedule this node was already keeping, or null where there is nothing to keep.
    ///
    /// <para>
    /// The interval has to match: a strategy that changed how often it runs is asking for a different schedule, and
    /// bending the old phase onto it would be honouring an instruction nobody gave. The phase survives, the missed
    /// firings do not - so the timer lands on the first boundary still ahead of now.
    /// </para>
    /// </summary>
    private UnixNanos? Resumption(string name, long intervalNanos)
    {
        lock (_gate)
        {
            if (!_resumed.Remove(name, out TimerSchedule schedule) || schedule.IntervalNanos != intervalNanos || intervalNanos <= 0)
            {
                return null;
            }

            (UnixNanos next, long skipped) = TimerResumption.NextFrom(schedule, Timestamp);
            Missed += skipped;
            return next;
        }
    }

    private void Schedule(TimerEntry entry)
    {
        lock (_gate)
        {
            if (_timers.Remove(entry.Name, out LiveTimer? existing))
            {
                existing.Dispose();
            }

            LiveTimer timer = new(entry, this);
            _timers[entry.Name] = timer;
            timer.Start();
        }
    }

    public UnixNanos? NextTime(string name)
    {
        lock (_gate)
        {
            return _timers.TryGetValue(name, out LiveTimer? timer) ? timer.Entry.NextTime : null;
        }
    }

    public void CancelTimer(string name)
    {
        lock (_gate)
        {
            if (_timers.Remove(name, out LiveTimer? timer))
            {
                timer.Dispose();
            }
        }
    }

    public void CancelTimers()
    {
        lock (_gate)
        {
            foreach (LiveTimer timer in _timers.Values)
            {
                timer.Dispose();
            }

            _timers.Clear();
        }
    }

    private void Fire(LiveTimer timer)
    {
        TimeEvent e;
        lock (_gate)
        {
            if (!_timers.TryGetValue(timer.Entry.Name, out LiveTimer? current) || !ReferenceEquals(current, timer))
            {
                return;
            }

            e = timer.Entry.Pop(Timestamp);
            if (timer.Entry.IsExpired)
            {
                _timers.Remove(timer.Entry.Name);
                timer.Dispose();
            }
        }

        _dispatcher(new TimeEventHandler(e, timer.Entry.Callback));
    }

    public void Dispose()
    {
        CancelTimers();
    }

    private sealed class LiveTimer : IDisposable
    {
        private readonly LiveClock _clock;
        private readonly CancellationTokenSource _cts = new();

        public LiveTimer(TimerEntry entry, LiveClock clock)
        {
            Entry = entry;
            _clock = clock;
        }

        public TimerEntry Entry { get; }

        public void Start() => _ = RunAsync();

        private async Task RunAsync()
        {
            // Never fire inside Start(), and so never inside the call that set the timer: an alert whose time has already
            // passed used to run synchronously inside OnStart, where the runtimeModule is not running yet and drops it, and the
            // thing it was waiting for then never came.
            await Task.Yield();
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    TimeSpan delay = Entry.NextTime - _clock.Timestamp;
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, _cts.Token).ConfigureAwait(false);
                    }

                    if (_cts.IsCancellationRequested)
                    {
                        return;
                    }

                    _clock.Fire(this);
                    if (Entry.IsExpired)
                    {
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Timer cancelled.
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
        }
    }
}
