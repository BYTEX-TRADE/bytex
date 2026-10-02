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
    private readonly CandleSeries _candleSeries;
    private readonly byte _pricePrecision;
    private readonly byte _sizePrecision;
    private decimal? _open;
    private decimal _high;
    private decimal _low;
    private decimal _close;
    private decimal _volume;
    private decimal _lastClose;

    public BarBuilder(CandleSeries candleSeries, byte pricePrecision, byte sizePrecision)
    {
        _candleSeries = candleSeries;
        _pricePrecision = pricePrecision;
        _sizePrecision = sizePrecision;
    }

    public bool IsInitialized => _open is not null;

    public int Count { get; private set; }

    public UnixNanos TsLast { get; private set; }

    public void Update(Price price, Quantity size, UnixNanos eventTime)
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
        TsLast = eventTime;
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
        TsLast = bar.EventTime;
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

    public Bar Build(UnixNanos eventTime, UnixNanos createdTime)
    {
        decimal open = _open ?? _close;
        Bar bar = new(
            _candleSeries,
            new Price(open, _pricePrecision),
            new Price(_high, _pricePrecision),
            new Price(_low, _pricePrecision),
            new Price(_close, _pricePrecision),
            new Quantity(_volume, _sizePrecision),
            eventTime,
            createdTime);
        _lastClose = _close;
        Reset();
        return bar;
    }

    /// <summary>Builds a bar using the last close as the open (for empty intervals).</summary>
    public Bar BuildFromLastClose(UnixNanos eventTime, UnixNanos createdTime)
    {
        if (_open is null)
        {
            _open = _lastClose;
            _high = _lastClose;
            _low = _lastClose;
            _close = _lastClose;
        }

        return Build(eventTime, createdTime);
    }
}

/// <summary>
/// Base for bar aggregators that turn ticks into bars according to a <see cref="SamplingRule"/>.
/// </summary>
public abstract class BarAggregator
{
    protected BarAggregator(Instrument instrument, CandleSeries candleSeries, Action<Bar> handler, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(clock);
        Instrument = instrument;
        CandleSeries = candleSeries;
        Handler = handler;
        Clock = clock;
        Builder = new BarBuilder(candleSeries, instrument.PricePrecision, instrument.SizePrecision);
    }

    public Instrument Instrument { get; }

    public CandleSeries CandleSeries { get; }

    protected Action<Bar> Handler { get; }

    protected IClock Clock { get; }

    protected BarBuilder Builder { get; }

    public void HandleQuoteTick(QuoteTick tick)
    {
        Price price = tick.ExtractPrice(CandleSeries.Spec.PriceType);
        Quantity size = tick.ExtractSize(CandleSeries.Spec.PriceType);
        ApplyUpdate(price, size, tick.EventTime);
    }

    /// <summary>
    /// Virtual because a bar that signs its updates needs the side the trade took the liquidity from, which only the
    /// trade carries (R2.12); every other aggregation needs a price and a size and nothing else.
    /// </summary>
    public virtual void HandleTradeTick(TradeTick tick) => ApplyUpdate(tick.Price, tick.Size, tick.EventTime);

    public virtual void HandleBar(Bar bar)
    {
        Builder.UpdateBar(bar);
        AfterUpdate(bar.EventTime);
    }

    protected abstract void ApplyUpdate(Price price, Quantity size, UnixNanos eventTime);

    protected virtual void AfterUpdate(UnixNanos eventTime)
    {
    }

    protected void Emit(UnixNanos eventTime)
    {
        if (!Builder.IsInitialized)
        {
            return;
        }

        Bar bar = Builder.Build(eventTime, Clock.Timestamp);
        Handler(bar);
    }

    public virtual void Stop()
    {
    }
}

public sealed class TickBarAggregator : BarAggregator
{
    public TickBarAggregator(Instrument instrument, CandleSeries candleSeries, Action<Bar> handler, IClock clock)
        : base(instrument, candleSeries, handler, clock)
    {
    }

    protected override void ApplyUpdate(Price price, Quantity size, UnixNanos eventTime)
    {
        Builder.Update(price, size, eventTime);
        if (Builder.Count >= CandleSeries.Spec.Step)
        {
            Emit(eventTime);
        }
    }
}

public sealed class VolumeBarAggregator : BarAggregator
{
    private decimal _cumulative;

    public VolumeBarAggregator(Instrument instrument, CandleSeries candleSeries, Action<Bar> handler, IClock clock)
        : base(instrument, candleSeries, handler, clock)
    {
    }

    protected override void ApplyUpdate(Price price, Quantity size, UnixNanos eventTime)
    {
        decimal remaining = size.Value;
        decimal step = CandleSeries.Spec.Step;
        while (remaining > 0m)
        {
            decimal room = step - _cumulative;
            decimal take = Math.Min(room, remaining);
            Builder.Update(price, new Quantity(take, size.Precision), eventTime);
            _cumulative += take;
            remaining -= take;
            if (_cumulative >= step)
            {
                Emit(eventTime);
                _cumulative = 0m;
            }
        }
    }
}

public sealed class ValueBarAggregator : BarAggregator
{
    private decimal _cumulative;

    public ValueBarAggregator(Instrument instrument, CandleSeries candleSeries, Action<Bar> handler, IClock clock)
        : base(instrument, candleSeries, handler, clock)
    {
    }

    protected override void ApplyUpdate(Price price, Quantity size, UnixNanos eventTime)
    {
        decimal remainingValue = size.Value * price.Value;
        decimal step = CandleSeries.Spec.Step;
        while (remainingValue > 0m)
        {
            decimal room = step - _cumulative;
            decimal takeValue = Math.Min(room, remainingValue);
            decimal takeSize = price.Value == 0m ? 0m : takeValue / price.Value;
            Builder.Update(price, new Quantity(takeSize, size.Precision), eventTime);
            _cumulative += takeValue;
            remainingValue -= takeValue;
            if (_cumulative >= step)
            {
                Emit(eventTime);
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

    /// <summary>
    /// The first moment this aggregator was watching. A window that opened before it is a window this aggregator did
    /// not see the whole of, and the bar for it is never published.
    /// </summary>
    private UnixNanos _watchingFrom;

    /// <summary>Counted so that a run can say how many windows it declined to publish rather than only logging them.</summary>
    public int Incomplete { get; private set; }

    public TimeBarAggregator(Instrument instrument, CandleSeries candleSeries, Action<Bar> handler, IClock clock, bool buildWithNoUpdates = false, bool timestampOnClose = true)
        : base(instrument, candleSeries, handler, clock)
    {
        if (!candleSeries.Spec.IsTimeAggregated)
        {
            throw new ArgumentException("TimeBarAggregator requires a time-based specification.", nameof(candleSeries));
        }

        _intervalNanos = candleSeries.Spec.IntervalNanos;
        _timerName = $"bar-{candleSeries}";
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
        _watchingFrom = now;
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

    protected override void ApplyUpdate(Price price, Quantity size, UnixNanos eventTime)
    {
        if (!_started)
        {
            Start();
        }

        Builder.Update(price, size, eventTime);
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
        UnixNanos closeTime = e.EventTime;
        UnixNanos eventTime = _timestampOnClose ? closeTime : closeTime.AddNanos(-_intervalNanos);

        // The window this bar would claim to cover. A bar is a contract - open, high, low, close and volume over
        // exactly that span - so one built from part of it is not a smaller bar, it is a wrong one, and nothing
        // downstream can tell. An indicator warms on it and a strategy's first decision is made on it.
        if (closeTime.AddNanos(-_intervalNanos).Value < _watchingFrom.Value)
        {
            Incomplete++;
            Builder.Reset();
            _nextClose = closeTime.AddNanos(_intervalNanos);
            return;
        }

        if (Builder.IsInitialized)
        {
            Bar bar = Builder.Build(eventTime, Clock.Timestamp);
            Handler(bar);
        }
        else if (_buildWithNoUpdates && Builder.Count == 0)
        {
            Bar bar = Builder.BuildFromLastClose(eventTime, Clock.Timestamp);
            if (!bar.Close.IsZero)
            {
                Handler(bar);
            }
        }

        _nextClose = closeTime.AddNanos(_intervalNanos);
    }
}
