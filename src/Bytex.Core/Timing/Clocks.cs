using Bytex.Core.Model.Events;
using Bytex.Core.Model.Primitives;

namespace Bytex.Core.Timing;

/// <summary>
/// The single source of time for a kernel. Hosts time alerts and repeating timers.
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

    public TimeEvent Pop(UnixNanos tsInit)
    {
        TimeEvent e = new(Name, Guid.NewGuid(), NextTime, tsInit);
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
        long intervalNanos = interval.Ticks * 100;
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
/// callbacks execute on the kernel thread.
/// </summary>
public sealed class LiveClock : IClock, IDisposable
{
    private readonly Dictionary<string, LiveTimer> _timers = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly Action<TimeEventHandler> _dispatcher;
    private Action<TimeEvent>? _defaultHandler;

    /// <param name="dispatcher">Receives due events; responsible for running them on the kernel thread. Null runs them inline.</param>
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
        long intervalNanos = interval.Ticks * 100;
        UnixNanos startTime = start ?? Timestamp;
        UnixNanos first = fireImmediately ? startTime : startTime.AddNanos(intervalNanos);
        Schedule(new TimerEntry(name, intervalNanos, first, stop, callback ?? _defaultHandler, repeating: true));
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
