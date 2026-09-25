using Bytex.Core.Model;
using Bytex.Core.Model.Data;

namespace Bytex.Indicators;

public abstract class MovingAverage : Indicator
{
    /// <summary>The period a moving average is conventionally used with when a caller names none.</summary>
    public const int DefaultPeriod = 20;

    protected MovingAverage(string name, int period, PriceType priceType)
        : base($"{name}({period})", priceType)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 1);
        Period = period;
    }

    public int Period { get; }

    public decimal Value { get; protected set; }

    public int Count { get; protected set; }

    public override void Reset()
    {
        base.Reset();
        Value = 0m;
        Count = 0;
    }
}

public sealed class SimpleMovingAverage : MovingAverage
{
    private readonly RollingWindow _window;

    public SimpleMovingAverage(int period, PriceType priceType = PriceType.Last)
        : base("SMA", period, priceType)
    {
        _window = new RollingWindow(period);
    }

    public override void UpdateRaw(decimal value)
    {
        _window.Add(value);
        HasInputs = true;
        Count = _window.Count;
        Value = _window.Average;
        IsInitialized = _window.IsFull;
    }

    public override void Reset()
    {
        base.Reset();
        _window.Clear();
    }
}

public sealed class ExponentialMovingAverage : MovingAverage
{
    public ExponentialMovingAverage(int period, PriceType priceType = PriceType.Last)
        : base("EMA", period, priceType)
    {
        Alpha = 2m / (period + 1);
    }

    public decimal Alpha { get; }

    public override void UpdateRaw(decimal value)
    {
        if (!HasInputs)
        {
            Value = value;
            HasInputs = true;
        }
        else
        {
            Value = Alpha * value + (1m - Alpha) * Value;
        }

        Count++;
        IsInitialized = Count >= Period;
    }
}

public sealed class WeightedMovingAverage : MovingAverage
{
    private readonly RollingWindow _window;
    private readonly decimal _weightSum;

    public WeightedMovingAverage(int period, PriceType priceType = PriceType.Last)
        : base("WMA", period, priceType)
    {
        _window = new RollingWindow(period);
        _weightSum = period * (period + 1) / 2m;
    }

    public override void UpdateRaw(decimal value)
    {
        _window.Add(value);
        HasInputs = true;
        Count = _window.Count;
        decimal weighted = 0m;
        for (int i = 0; i < _window.Count; i++)
        {
            weighted += _window[i] * (i + 1);
        }

        decimal denominator = _window.IsFull ? _weightSum : _window.Count * (_window.Count + 1) / 2m;
        Value = weighted / denominator;
        IsInitialized = _window.IsFull;
    }

    public override void Reset()
    {
        base.Reset();
        _window.Clear();
    }
}

public sealed class DoubleExponentialMovingAverage : MovingAverage
{
    private readonly ExponentialMovingAverage _ema1;
    private readonly ExponentialMovingAverage _ema2;

    public DoubleExponentialMovingAverage(int period, PriceType priceType = PriceType.Last)
        : base("DEMA", period, priceType)
    {
        _ema1 = new ExponentialMovingAverage(period);
        _ema2 = new ExponentialMovingAverage(period);
    }

    public override void UpdateRaw(decimal value)
    {
        _ema1.UpdateRaw(value);
        _ema2.UpdateRaw(_ema1.Value);
        Value = 2m * _ema1.Value - _ema2.Value;
        HasInputs = true;
        Count++;
        IsInitialized = _ema2.IsInitialized;
    }

    public override void Reset()
    {
        base.Reset();
        _ema1.Reset();
        _ema2.Reset();
    }
}

public sealed class HullMovingAverage : MovingAverage
{
    private readonly WeightedMovingAverage _half;
    private readonly WeightedMovingAverage _full;
    private readonly WeightedMovingAverage _sqrt;

    public HullMovingAverage(int period, PriceType priceType = PriceType.Last)
        : base("HMA", period, priceType)
    {
        _half = new WeightedMovingAverage(Math.Max(1, period / 2));
        _full = new WeightedMovingAverage(period);
        _sqrt = new WeightedMovingAverage(Math.Max(1, (int)Math.Round(Math.Sqrt(period))));
    }

    public override void UpdateRaw(decimal value)
    {
        _half.UpdateRaw(value);
        _full.UpdateRaw(value);
        if (_full.IsInitialized)
        {
            // The composite 2 * WMA(N/2) - WMA(N) is only itself once both of those averages are over full windows;
            // smoothing the partial ones gave a value that read as initialized while it was still settling.
            _sqrt.UpdateRaw(2m * _half.Value - _full.Value);
            Value = _sqrt.Value;
        }

        HasInputs = true;
        Count++;
        IsInitialized = _sqrt.IsInitialized;
    }

    public override void Reset()
    {
        base.Reset();
        _half.Reset();
        _full.Reset();
        _sqrt.Reset();
    }
}

/// <summary>
/// Volume-weighted average price, reset at the start of each day (UTC).
/// </summary>
public sealed class VolumeWeightedAveragePrice : Indicator
{
    private decimal _priceVolume;
    private decimal _volume;
    private DateOnly _day;

    public VolumeWeightedAveragePrice()
        : base("VWAP")
    {
    }

    public decimal Value { get; private set; }

    public override void Update(Bar bar)
    {
        decimal typical = (bar.High.Value + bar.Low.Value + bar.Close.Value) / 3m;
        UpdateRaw(typical, bar.Volume.Value, DateOnly.FromDateTime(bar.TsEvent.ToDateTimeUtc()));
    }

    public override void Update(TradeTick tick) => UpdateRaw(tick.Price.Value, tick.Size.Value, DateOnly.FromDateTime(tick.TsEvent.ToDateTimeUtc()));

    public override void Update(QuoteTick tick) => UpdateRaw(tick.Mid.Value, (tick.BidSize.Value + tick.AskSize.Value) / 2m, DateOnly.FromDateTime(tick.TsEvent.ToDateTimeUtc()));

    public override void UpdateRaw(decimal value) => UpdateRaw(value, 1m, _day);

    public void UpdateRaw(decimal price, decimal volume, DateOnly day)
    {
        if (day != _day)
        {
            _day = day;
            _priceVolume = 0m;
            _volume = 0m;
        }

        _priceVolume += price * volume;
        _volume += volume;
        Value = _volume == 0m ? price : _priceVolume / _volume;
        HasInputs = true;
        IsInitialized = true;
    }

    public override void Reset()
    {
        base.Reset();
        _priceVolume = 0m;
        _volume = 0m;
        Value = 0m;
    }
}
