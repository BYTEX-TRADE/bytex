using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Primitives;

namespace Bytex.Indicators;

/// <summary>
/// Average true range with Wilder's smoothing.
/// </summary>
public sealed class AverageTrueRange : Indicator
{
    /// <summary>The period this indicator is conventionally used with.</summary>
    public const int DefaultPeriod = 14;

    private readonly int _period;
    private decimal _lastClose;
    private int _count;

    public AverageTrueRange(int period = DefaultPeriod)
        : base($"ATR({period})")
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 1);
        _period = period;
    }

    public int Period => _period;

    public decimal Value { get; private set; }

    public override void Update(Bar bar) => Update(bar.High.Value, bar.Low.Value, bar.Close.Value);

    public override void UpdateRaw(decimal value) => Update(value, value, value);

    public void Update(decimal high, decimal low, decimal close)
    {
        decimal trueRange = high - low;
        if (HasInputs)
        {
            trueRange = Math.Max(trueRange, Math.Max(Math.Abs(high - _lastClose), Math.Abs(low - _lastClose)));
        }

        _lastClose = close;
        HasInputs = true;
        _count++;
        if (_count <= _period)
        {
            Value += (trueRange - Value) / _count;
        }
        else
        {
            Value = (Value * (_period - 1) + trueRange) / _period;
        }

        IsInitialized = _count >= _period;
    }

    public override void Reset()
    {
        base.Reset();
        _lastClose = 0m;
        _count = 0;
        Value = 0m;
    }
}

public sealed class BollingerBands : Indicator
{
    private readonly RollingWindow _window;
    private readonly decimal _k;

    /// <summary>The period and the band width this indicator is conventionally used with.</summary>
    public const int DefaultPeriod = 20;

    /// <summary>The period and the band width this indicator is conventionally used with.</summary>
    public const decimal DefaultBandWidth = 2m;

    public BollingerBands(int period = DefaultPeriod, decimal k = DefaultBandWidth, PriceType priceType = PriceType.Last)
        : base($"BB({period},{k})", priceType)
    {
        _window = new RollingWindow(period);
        _k = k;
    }

    public decimal Upper { get; private set; }

    public decimal Middle { get; private set; }

    public decimal Lower { get; private set; }

    public decimal Width => Middle == 0m ? 0m : (Upper - Lower) / Middle;

    public override void Update(Bar bar) => UpdateRaw((bar.High.Value + bar.Low.Value + bar.Close.Value) / 3m);

    public override void UpdateRaw(decimal value)
    {
        _window.Add(value);
        HasInputs = true;
        Middle = _window.Average;
        decimal deviation = _window.StandardDeviation() * _k;
        Upper = Middle + deviation;
        Lower = Middle - deviation;
        IsInitialized = _window.IsFull;
    }

    public override void Reset()
    {
        base.Reset();
        _window.Clear();
        Upper = Middle = Lower = 0m;
    }
}

public sealed class KeltnerChannel : Indicator
{
    private readonly ExponentialMovingAverage _ema;
    private readonly AverageTrueRange _atr;
    private readonly decimal _k;

    /// <summary>The periods and the band width this indicator is conventionally used with.</summary>
    public const int DefaultPeriod = 20;

    /// <summary>The periods and the band width this indicator is conventionally used with.</summary>
    public const decimal DefaultBandWidth = 2m;

    /// <summary>The periods and the band width this indicator is conventionally used with.</summary>
    public const int DefaultAtrPeriod = 10;

    public KeltnerChannel(int period = DefaultPeriod, decimal k = DefaultBandWidth, int atrPeriod = DefaultAtrPeriod)
        : base($"KC({period},{k},{atrPeriod})")
    {
        _ema = new ExponentialMovingAverage(period);
        _atr = new AverageTrueRange(atrPeriod);
        _k = k;
    }

    public decimal Upper { get; private set; }

    public decimal Middle { get; private set; }

    public decimal Lower { get; private set; }

    public override void Update(Bar bar)
    {
        decimal typical = (bar.High.Value + bar.Low.Value + bar.Close.Value) / 3m;
        _ema.UpdateRaw(typical);
        _atr.Update(bar.High.Value, bar.Low.Value, bar.Close.Value);
        Compute();
    }

    public override void UpdateRaw(decimal value)
    {
        _ema.UpdateRaw(value);
        _atr.Update(value, value, value);
        Compute();
    }

    private void Compute()
    {
        Middle = _ema.Value;
        Upper = Middle + _k * _atr.Value;
        Lower = Middle - _k * _atr.Value;
        HasInputs = true;
        IsInitialized = _ema.IsInitialized && _atr.IsInitialized;
    }

    public override void Reset()
    {
        base.Reset();
        _ema.Reset();
        _atr.Reset();
        Upper = Middle = Lower = 0m;
    }
}

public sealed class DonchianChannel : Indicator
{
    private readonly RollingWindow _highs;
    private readonly RollingWindow _lows;
    private readonly bool _excludeCurrent;

    /// <summary>The period this indicator is conventionally used with.</summary>
    public const int DefaultPeriod = 20;

    /// <param name="period">How many bars the bands cover.</param>
    /// <param name="excludeCurrent">
    /// True computes the bands over the <paramref name="period"/> bars BEFORE the current one. With the current bar in
    /// the window its own high is the upper band, so "the close crossed above the upper band" can never be true and a
    /// breakout strategy written that way never trades. The default keeps the current bar in, as before.
    /// </param>
    public DonchianChannel(int period = DefaultPeriod, bool excludeCurrent = false)
        : base(excludeCurrent ? $"DC({period},excl)" : $"DC({period})")
    {
        _excludeCurrent = excludeCurrent;
        _highs = new RollingWindow(period);
        _lows = new RollingWindow(period);
    }

    public decimal Upper { get; private set; }

    public decimal Middle { get; private set; }

    public decimal Lower { get; private set; }

    public override void Update(Bar bar) => Update(bar.High.Value, bar.Low.Value);

    public override void Update(QuoteTick tick) => Update(tick.Ask.Value, tick.Bid.Value);

    public override void UpdateRaw(decimal value) => Update(value, value);

    public void Update(decimal high, decimal low)
    {
        if (_excludeCurrent)
        {
            // Read the bands from the bars already in the window, then let this bar in: the values a caller sees
            // describe the period before the bar it just passed, which is what a breakout is measured against.
            bool ready = _highs.IsFull;
            if (_highs.Count > 0)
            {
                Upper = _highs.Max();
                Lower = _lows.Min();
                Middle = (Upper + Lower) / 2m;
            }

            _highs.Add(high);
            _lows.Add(low);
            HasInputs = true;
            IsInitialized = ready;
            return;
        }

        _highs.Add(high);
        _lows.Add(low);
        Upper = _highs.Max();
        Lower = _lows.Min();
        Middle = (Upper + Lower) / 2m;
        HasInputs = true;
        IsInitialized = _highs.IsFull;
    }

    public override void Reset()
    {
        base.Reset();
        _highs.Clear();
        _lows.Clear();
        Upper = Middle = Lower = 0m;
    }
}

/// <summary>
/// Average directional index with +DI and -DI.
/// </summary>
public sealed class AverageDirectionalIndex : Indicator
{
    /// <summary>The period this indicator is conventionally used with.</summary>
    public const int DefaultPeriod = 14;

    private readonly int _period;
    private decimal _lastHigh;
    private decimal _lastLow;
    private decimal _lastClose;
    private decimal _smoothedTr;
    private decimal _smoothedPlusDm;
    private decimal _smoothedMinusDm;
    private decimal _adx;
    private decimal _dxSum;
    private int _dxCount;
    private int _count;

    public AverageDirectionalIndex(int period = DefaultPeriod)
        : base($"ADX({period})")
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 1);
        _period = period;
    }

    public decimal Value => _adx;

    public decimal PlusDi { get; private set; }

    public decimal MinusDi { get; private set; }

    public override void Update(Bar bar) => Update(bar.High.Value, bar.Low.Value, bar.Close.Value);

    public override void UpdateRaw(decimal value) => Update(value, value, value);

    public void Update(decimal high, decimal low, decimal close)
    {
        if (!HasInputs)
        {
            _lastHigh = high;
            _lastLow = low;
            _lastClose = close;
            HasInputs = true;
            return;
        }

        decimal upMove = high - _lastHigh;
        decimal downMove = _lastLow - low;
        decimal plusDm = upMove > downMove && upMove > 0m ? upMove : 0m;
        decimal minusDm = downMove > upMove && downMove > 0m ? downMove : 0m;
        decimal tr = Math.Max(high - low, Math.Max(Math.Abs(high - _lastClose), Math.Abs(low - _lastClose)));

        _lastHigh = high;
        _lastLow = low;
        _lastClose = close;
        _count++;

        if (_count <= _period)
        {
            _smoothedTr += tr;
            _smoothedPlusDm += plusDm;
            _smoothedMinusDm += minusDm;
        }
        else
        {
            _smoothedTr = _smoothedTr - _smoothedTr / _period + tr;
            _smoothedPlusDm = _smoothedPlusDm - _smoothedPlusDm / _period + plusDm;
            _smoothedMinusDm = _smoothedMinusDm - _smoothedMinusDm / _period + minusDm;
        }

        if (_smoothedTr == 0m)
        {
            return;
        }

        PlusDi = _smoothedPlusDm / _smoothedTr * Scales.Percent;
        MinusDi = _smoothedMinusDm / _smoothedTr * Scales.Percent;
        if (_count < _period)
        {
            // The directional movement sums are still filling, so the DI pair they give is not the real one yet and
            // the DX taken from it does not belong in the seed. Averaging those in never converged on Wilder's ADX.
            return;
        }

        decimal sum = PlusDi + MinusDi;
        decimal dx = sum == 0m ? 0m : Math.Abs(PlusDi - MinusDi) / sum * Scales.Percent;

        if (_dxCount < _period)
        {
            // Wilder seeds the ADX with the mean of the first N real DX values, which is why it appears on bar 2N-1.
            _dxSum += dx;
            _dxCount++;
            _adx = _dxSum / _dxCount;
        }
        else
        {
            _adx = (_adx * (_period - 1) + dx) / _period;
        }

        IsInitialized = _dxCount >= _period;
    }

    public override void Reset()
    {
        base.Reset();
        _lastHigh = _lastLow = _lastClose = 0m;
        _smoothedTr = _smoothedPlusDm = _smoothedMinusDm = 0m;
        _adx = _dxSum = 0m;
        _dxCount = 0;
        _count = 0;
        PlusDi = MinusDi = 0m;
    }
}

public sealed class AroonOscillator : Indicator
{
    /// <summary>The period this indicator is conventionally used with.</summary>
    public const int DefaultPeriod = 25;

    private readonly RollingWindow _highs;
    private readonly RollingWindow _lows;
    private readonly int _period;

    public AroonOscillator(int period = DefaultPeriod)
        : base($"AROON({period})")
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 1);
        _period = period;
        _highs = new RollingWindow(period + 1);
        _lows = new RollingWindow(period + 1);
    }

    public decimal AroonUp { get; private set; }

    public decimal AroonDown { get; private set; }

    public decimal Value => AroonUp - AroonDown;

    public override void Update(Bar bar) => Update(bar.High.Value, bar.Low.Value);

    public override void UpdateRaw(decimal value) => Update(value, value);

    public void Update(decimal high, decimal low)
    {
        _highs.Add(high);
        _lows.Add(low);
        HasInputs = true;

        int periodsSinceHigh = 0;
        int periodsSinceLow = 0;
        decimal maxHigh = decimal.MinValue;
        decimal minLow = decimal.MaxValue;
        for (int i = 0; i < _highs.Count; i++)
        {
            if (_highs[i] >= maxHigh)
            {
                maxHigh = _highs[i];
                periodsSinceHigh = _highs.Count - 1 - i;
            }

            if (_lows[i] <= minLow)
            {
                minLow = _lows[i];
                periodsSinceLow = _lows.Count - 1 - i;
            }
        }

        AroonUp = (_period - periodsSinceHigh) / (decimal)_period * Scales.Percent;
        AroonDown = (_period - periodsSinceLow) / (decimal)_period * Scales.Percent;
        IsInitialized = _highs.IsFull;
    }

    public override void Reset()
    {
        base.Reset();
        _highs.Clear();
        _lows.Clear();
        AroonUp = AroonDown = 0m;
    }
}
