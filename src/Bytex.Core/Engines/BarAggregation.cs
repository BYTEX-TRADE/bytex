using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Timing;

namespace Bytex.Core.Engines;

/// <summary>
/// Accumulates prices and sizes into an open bar.
/// </summary>
public sealed class BarBuilder
{
    private readonly BarType _barType;
    private readonly byte _pricePrecision;
    private readonly byte _sizePrecision;
    private decimal? _open;
    private decimal _high;
    private decimal _low;
    private decimal _close;
    private decimal _volume;
    private decimal _lastClose;

    public BarBuilder(BarType barType, byte pricePrecision, byte sizePrecision)
    {
        _barType = barType;
        _pricePrecision = pricePrecision;
        _sizePrecision = sizePrecision;
    }

    public bool IsInitialized => _open is not null;

    public int Count { get; private set; }

    public UnixNanos TsLast { get; private set; }

    public void Update(Price price, Quantity size, UnixNanos tsEvent)
    {
        decimal p = price.Value;
        if (_open is null)
        {
            _open = p;
            _high = p;
            _low = p;
        }
        else
        {
            if (p > _high)
            {
                _high = p;
            }

            if (p < _low)
            {
                _low = p;
            }
        }

        _close = p;
        _volume += size.Value;
        Count++;
        TsLast = tsEvent;
    }

    public void UpdateBar(Bar bar)
    {
        if (_open is null)
        {
            _open = bar.Open.Value;
            _high = bar.High.Value;
            _low = bar.Low.Value;
        }
        else
        {
            _high = Math.Max(_high, bar.High.Value);
            _low = Math.Min(_low, bar.Low.Value);
        }

        _close = bar.Close.Value;
        _volume += bar.Volume.Value;
        Count++;
        TsLast = bar.TsEvent;
    }

    public void Reset()
    {
        _open = null;
        _high = 0m;
        _low = 0m;
        _close = 0m;
        _volume = 0m;
        Count = 0;
    }

    public Bar Build(UnixNanos tsEvent, UnixNanos tsInit)
    {
        decimal open = _open ?? _close;
        Bar bar = new(
            _barType,
            new Price(open, _pricePrecision),
            new Price(_high, _pricePrecision),
            new Price(_low, _pricePrecision),
            new Price(_close, _pricePrecision),
            new Quantity(_volume, _sizePrecision),
            tsEvent,
            tsInit);
        _lastClose = _close;
        Reset();
        return bar;
    }

    /// <summary>Builds a bar using the last close as the open (for empty intervals).</summary>
    public Bar BuildFromLastClose(UnixNanos tsEvent, UnixNanos tsInit)
    {
        if (_open is null)
        {
            _open = _lastClose;
            _high = _lastClose;
            _low = _lastClose;
            _close = _lastClose;
        }

        return Build(tsEvent, tsInit);
    }
}

/// <summary>
/// Base for bar aggregators that turn ticks into bars according to a <see cref="BarSpecification"/>.
/// </summary>
public abstract class BarAggregator
{
    protected BarAggregator(Instrument instrument, BarType barType, Action<Bar> handler, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(clock);
        Instrument = instrument;
        BarType = barType;
        Handler = handler;
        Clock = clock;
        Builder = new BarBuilder(barType, instrument.PricePrecision, instrument.SizePrecision);
    }

    public Instrument Instrument { get; }

    public BarType BarType { get; }

    protected Action<Bar> Handler { get; }

    protected IClock Clock { get; }

    protected BarBuilder Builder { get; }

    public void HandleQuoteTick(QuoteTick tick)
    {
        Price price = tick.ExtractPrice(BarType.Spec.PriceType);
        Quantity size = tick.ExtractSize(BarType.Spec.PriceType);
        ApplyUpdate(price, size, tick.TsEvent);
    }

    public void HandleTradeTick(TradeTick tick) => ApplyUpdate(tick.Price, tick.Size, tick.TsEvent);

    public virtual void HandleBar(Bar bar)
    {
        Builder.UpdateBar(bar);
        AfterUpdate(bar.TsEvent);
    }

    protected abstract void ApplyUpdate(Price price, Quantity size, UnixNanos tsEvent);

    protected virtual void AfterUpdate(UnixNanos tsEvent)
    {
    }

    protected void Emit(UnixNanos tsEvent)
    {
        if (!Builder.IsInitialized)
        {
            return;
        }

        Bar bar = Builder.Build(tsEvent, Clock.Timestamp);
        Handler(bar);
    }

    public virtual void Stop()
    {
    }
}

public sealed class TickBarAggregator : BarAggregator
{
    public TickBarAggregator(Instrument instrument, BarType barType, Action<Bar> handler, IClock clock)
        : base(instrument, barType, handler, clock)
    {
    }

    protected override void ApplyUpdate(Price price, Quantity size, UnixNanos tsEvent)
    {
        Builder.Update(price, size, tsEvent);
        if (Builder.Count >= BarType.Spec.Step)
        {
            Emit(tsEvent);
        }
    }
}

public sealed class VolumeBarAggregator : BarAggregator
{
    private decimal _cumulative;

    public VolumeBarAggregator(Instrument instrument, BarType barType, Action<Bar> handler, IClock clock)
        : base(instrument, barType, handler, clock)
    {
    }

    protected override void ApplyUpdate(Price price, Quantity size, UnixNanos tsEvent)
    {
        decimal remaining = size.Value;
        decimal step = BarType.Spec.Step;
        while (remaining > 0m)
        {
            decimal room = step - _cumulative;
            decimal take = Math.Min(room, remaining);
            Builder.Update(price, new Quantity(take, size.Precision), tsEvent);
            _cumulative += take;
            remaining -= take;
            if (_cumulative >= step)
            {
                Emit(tsEvent);
                _cumulative = 0m;
            }
        }
    }
}

public sealed class ValueBarAggregator : BarAggregator
{
    private decimal _cumulative;

    public ValueBarAggregator(Instrument instrument, BarType barType, Action<Bar> handler, IClock clock)
        : base(instrument, barType, handler, clock)
    {
    }

    protected override void ApplyUpdate(Price price, Quantity size, UnixNanos tsEvent)
    {
        decimal remainingValue = size.Value * price.Value;
        decimal step = BarType.Spec.Step;
        while (remainingValue > 0m)
        {
            decimal room = step - _cumulative;
            decimal takeValue = Math.Min(room, remainingValue);
            decimal takeSize = price.Value == 0m ? 0m : takeValue / price.Value;
            Builder.Update(price, new Quantity(takeSize, size.Precision), tsEvent);
            _cumulative += takeValue;
            remainingValue -= takeValue;
            if (_cumulative >= step)
            {
                Emit(tsEvent);
                _cumulative = 0m;
            }
        }
    }
}

/// <summary>
/// Emits a bar at each interval boundary using the clock's timer. The bar's timestamp is the interval close.
/// </summary>
public sealed class TimeBarAggregator : BarAggregator
{
    private readonly string _timerName;
    private readonly long _intervalNanos;
    private readonly bool _buildWithNoUpdates;
    private readonly bool _timestampOnClose;
    private UnixNanos _nextClose;
    private bool _started;

    public TimeBarAggregator(Instrument instrument, BarType barType, Action<Bar> handler, IClock clock, bool buildWithNoUpdates = false, bool timestampOnClose = true)
        : base(instrument, barType, handler, clock)
    {
        if (!barType.Spec.IsTimeAggregated)
        {
            throw new ArgumentException("TimeBarAggregator requires a time-based specification.", nameof(barType));
        }

        _intervalNanos = barType.Spec.IntervalNanos;
        _timerName = $"bar-{barType}";
        _buildWithNoUpdates = buildWithNoUpdates;
        _timestampOnClose = timestampOnClose;
    }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        UnixNanos now = Clock.Timestamp;
        _nextClose = now.FloorTo(_intervalNanos).AddNanos(_intervalNanos);
        Clock.SetTimer(_timerName, TimeSpan.FromTicks(_intervalNanos / UnixNanos.NanosPerTick), _nextClose.AddNanos(-_intervalNanos), null, OnTimer);
    }

    public override void Stop()
    {
        if (_started)
        {
            Clock.CancelTimer(_timerName);
            _started = false;
        }
    }

    protected override void ApplyUpdate(Price price, Quantity size, UnixNanos tsEvent)
    {
        if (!_started)
        {
            Start();
        }

        Builder.Update(price, size, tsEvent);
    }

    public override void HandleBar(Bar bar)
    {
        if (!_started)
        {
            Start();
        }

        base.HandleBar(bar);
    }

    private void OnTimer(TimeEvent e)
    {
        UnixNanos closeTime = e.TsEvent;
        UnixNanos tsEvent = _timestampOnClose ? closeTime : closeTime.AddNanos(-_intervalNanos);
        if (Builder.IsInitialized)
        {
            Bar bar = Builder.Build(tsEvent, Clock.Timestamp);
            Handler(bar);
        }
        else if (_buildWithNoUpdates && Builder.Count == 0)
        {
            Bar bar = Builder.BuildFromLastClose(tsEvent, Clock.Timestamp);
            if (!bar.Close.IsZero)
            {
                Handler(bar);
            }
        }

        _nextClose = closeTime.AddNanos(_intervalNanos);
    }
}
