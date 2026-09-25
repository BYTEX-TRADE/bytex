using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Primitives;

namespace Bytex.Indicators;

/// <summary>
/// Relative strength index using Wilder's smoothing.
/// </summary>
public sealed class RelativeStrengthIndex : Indicator
{
    /// <summary>The period this indicator is conventionally used with.</summary>
    public const int DefaultPeriod = 14;

    private readonly int _period;
    private decimal _avgGain;
    private decimal _avgLoss;
    private decimal _last;
    private int _count;

    public RelativeStrengthIndex(int period, PriceType priceType = PriceType.Last)
        : base($"RSI({period})", priceType)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 1);
        _period = period;
    }

    public int Period => _period;

    /// <summary>Value in the range 0–100.</summary>
    public decimal Value { get; private set; }

    public override void UpdateRaw(decimal value)
    {
        if (!HasInputs)
        {
            _last = value;
            HasInputs = true;
            return;
        }

        decimal change = value - _last;
        _last = value;
        decimal gain = change > 0m ? change : 0m;
        decimal loss = change < 0m ? -change : 0m;
        _count++;

        if (_count <= _period)
        {
            _avgGain += gain / _period;
            _avgLoss += loss / _period;
        }
        else
        {
            _avgGain = (_avgGain * (_period - 1) + gain) / _period;
            _avgLoss = (_avgLoss * (_period - 1) + loss) / _period;
        }

        Value = _avgLoss == 0m ? Scales.Percent : Scales.Percent - (Scales.Percent / (1m + (_avgGain / _avgLoss)));
        IsInitialized = _count >= _period;
    }

    public override void Reset()
    {
        base.Reset();
        _avgGain = 0m;
        _avgLoss = 0m;
        _last = 0m;
        _count = 0;
        Value = 0m;
    }
}

/// <summary>
/// Moving average convergence/divergence.
/// </summary>
public sealed class MovingAverageConvergenceDivergence : Indicator
{
    private readonly ExponentialMovingAverage _fast;
    private readonly ExponentialMovingAverage _slow;
    private readonly ExponentialMovingAverage _signal;

    /// <summary>The periods this indicator is conventionally used with.</summary>
    public const int DefaultFastPeriod = 12;

    /// <summary>The periods this indicator is conventionally used with.</summary>
    public const int DefaultSlowPeriod = 26;

    /// <summary>The periods this indicator is conventionally used with.</summary>
    public const int DefaultSignalPeriod = 9;

    public MovingAverageConvergenceDivergence(int fastPeriod = DefaultFastPeriod, int slowPeriod = DefaultSlowPeriod, int signalPeriod = DefaultSignalPeriod, PriceType priceType = PriceType.Last)
        : base($"MACD({fastPeriod},{slowPeriod},{signalPeriod})", priceType)
    {
        _fast = new ExponentialMovingAverage(fastPeriod);
        _slow = new ExponentialMovingAverage(slowPeriod);
        _signal = new ExponentialMovingAverage(signalPeriod);
    }

    public decimal Value { get; private set; }

    public decimal Signal { get; private set; }

    public decimal Histogram => Value - Signal;

    public override void UpdateRaw(decimal value)
    {
        _fast.UpdateRaw(value);
        _slow.UpdateRaw(value);
        Value = _fast.Value - _slow.Value;
        _signal.UpdateRaw(Value);
        Signal = _signal.Value;
        HasInputs = true;
        IsInitialized = _slow.IsInitialized && _signal.IsInitialized;
    }

    public override void Reset()
    {
        base.Reset();
        _fast.Reset();
        _slow.Reset();
        _signal.Reset();
        Value = 0m;
        Signal = 0m;
    }
}

/// <summary>
/// Rate of change as a percentage over the period.
/// </summary>
public sealed class RateOfChange : Indicator
{
    /// <summary>The period this indicator is conventionally used with.</summary>
    public const int DefaultPeriod = 10;

    private readonly RollingWindow _window;

    public RateOfChange(int period, PriceType priceType = PriceType.Last)
        : base($"ROC({period})", priceType)
    {
        // Over no bars a price is compared with itself, which reads as a rate of change of zero for ever.
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 1);
        _window = new RollingWindow(period + 1);
    }

    public decimal Value { get; private set; }

    public override void UpdateRaw(decimal value)
    {
        _window.Add(value);
        HasInputs = true;
        if (_window.IsFull && _window.Oldest != 0m)
        {
            Value = (value - _window.Oldest) / _window.Oldest * Scales.Percent;
            IsInitialized = true;
        }
    }

    public override void Reset()
    {
        base.Reset();
        _window.Clear();
        Value = 0m;
    }
}

/// <summary>
/// Stochastic oscillator (%K and %D).
/// </summary>
public sealed class Stochastics : Indicator
{
    private readonly RollingWindow _highs;
    private readonly RollingWindow _lows;
    private readonly SimpleMovingAverage _d;

    /// <summary>%K when the window is flat: the close is neither at a high nor at a low, so it reads as the middle.</summary>
    private const decimal NeutralValue = 50m;

    /// <summary>The periods this indicator is conventionally used with.</summary>
    public const int DefaultKPeriod = 14;

    /// <summary>The periods this indicator is conventionally used with.</summary>
    public const int DefaultDPeriod = 3;

    public Stochastics(int kPeriod = DefaultKPeriod, int dPeriod = DefaultDPeriod)
        : base($"STOCH({kPeriod},{dPeriod})")
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(kPeriod, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(dPeriod, 1);
        _highs = new RollingWindow(kPeriod);
        _lows = new RollingWindow(kPeriod);
        _d = new SimpleMovingAverage(dPeriod);
    }

    public decimal ValueK { get; private set; }

    public decimal ValueD { get; private set; }

    public override void Update(Bar bar) => Update(bar.High.Value, bar.Low.Value, bar.Close.Value);

    public override void UpdateRaw(decimal value) => Update(value, value, value);

    public void Update(decimal high, decimal low, decimal close)
    {
        _highs.Add(high);
        _lows.Add(low);
        HasInputs = true;
        decimal highest = _highs.Max();
        decimal lowest = _lows.Min();
        decimal range = highest - lowest;
        ValueK = range == 0m ? NeutralValue : (close - lowest) / range * Scales.Percent;
        if (_highs.IsFull)
        {
            // %D averages %K, and a %K measured over a window that has not filled yet is not one of its inputs.
            _d.UpdateRaw(ValueK);
            ValueD = _d.Value;
        }

        IsInitialized = _d.IsInitialized;
    }

    public override void Reset()
    {
        base.Reset();
        _highs.Clear();
        _lows.Clear();
        _d.Reset();
        ValueK = 0m;
        ValueD = 0m;
    }
}

/// <summary>
/// On-balance volume.
/// </summary>
public sealed class OnBalanceVolume : Indicator
{
    private decimal _lastClose;

    public OnBalanceVolume()
        : base("OBV")
    {
    }

    public decimal Value { get; private set; }

    public override void Update(Bar bar)
    {
        if (HasInputs)
        {
            if (bar.Close.Value > _lastClose)
            {
                Value += bar.Volume.Value;
            }
            else if (bar.Close.Value < _lastClose)
            {
                Value -= bar.Volume.Value;
            }
        }

        _lastClose = bar.Close.Value;
        HasInputs = true;
        IsInitialized = true;
    }

    public override void Update(TradeTick tick)
    {
        if (HasInputs)
        {
            if (tick.Price.Value > _lastClose)
            {
                Value += tick.Size.Value;
            }
            else if (tick.Price.Value < _lastClose)
            {
                Value -= tick.Size.Value;
            }
        }

        _lastClose = tick.Price.Value;
        HasInputs = true;
        IsInitialized = true;
    }

    public override void UpdateRaw(decimal value)
    {
        _lastClose = value;
        HasInputs = true;
    }

    public override void Reset()
    {
        base.Reset();
        _lastClose = 0m;
        Value = 0m;
    }
}
