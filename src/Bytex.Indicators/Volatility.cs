using Bytex.Core.Model;
using Bytex.Core.Model.Data;

namespace Bytex.Indicators;

/// <summary>
/// Average true range with Wilder's smoothing.
/// </summary>
public sealed class AverageTrueRange : Indicator
{
    private readonly int _period;
    private decimal _lastClose;
    private int _count;

    public AverageTrueRange(int period = 14)
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

    public BollingerBands(int period = 20, decimal k = 2m, PriceType priceType = PriceType.Last)
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

    public KeltnerChannel(int period = 20, decimal k = 2m, int atrPeriod = 10)
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

    public DonchianChannel(int period = 20)
        : base($"DC({period})")
    {
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
    private readonly int _period;
    private decimal _lastHigh;
    private decimal _lastLow;
    private decimal _lastClose;
    private decimal _smoothedTr;
    private decimal _smoothedPlusDm;
    private decimal _smoothedMinusDm;
    private decimal _adx;
    private int _count;

    public AverageDirectionalIndex(int period = 14)
        : base($"ADX({period})")
    {
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

        PlusDi = _smoothedPlusDm / _smoothedTr * 100m;
        MinusDi = _smoothedMinusDm / _smoothedTr * 100m;
        decimal sum = PlusDi + MinusDi;
        decimal dx = sum == 0m ? 0m : Math.Abs(PlusDi - MinusDi) / sum * 100m;

        if (_count <= _period)
        {
            _adx += (dx - _adx) / _count;
        }
        else
        {
            _adx = (_adx * (_period - 1) + dx) / _period;
        }

        IsInitialized = _count >= _period * 2;
    }

    public override void Reset()
    {
        base.Reset();
        _lastHigh = _lastLow = _lastClose = 0m;
        _smoothedTr = _smoothedPlusDm = _smoothedMinusDm = 0m;
        _adx = 0m;
        _count = 0;
        PlusDi = MinusDi = 0m;
    }
}

public sealed class AroonOscillator : Indicator
{
    private readonly RollingWindow _highs;
    private readonly RollingWindow _lows;
    private readonly int _period;

    public AroonOscillator(int period = 25)
        : base($"AROON({period})")
    {
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

        AroonUp = (_period - periodsSinceHigh) / (decimal)_period * 100m;
        AroonDown = (_period - periodsSinceLow) / (decimal)_period * 100m;
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
