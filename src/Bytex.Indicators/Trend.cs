using Bytex.Core.Model;
using Bytex.Core.Model.Data;

namespace Bytex.Indicators;

/// <summary>
/// Ichimoku Kinko Hyo: the five lines, and the cloud as it applies to the bar being looked at (R7.8).
///
/// <para>
/// <b>The displacement is where every implementation of this disagrees with every other.</b> Two of the lines are
/// plotted forward and one back, so "the cloud" means two different things: what has just been computed, which belongs
/// 26 bars in the future, and what applies to the bar in hand, which was computed 26 bars ago. A strategy asking
/// "is price above the cloud" means the second. Both are here, named for which they are, because an indicator that
/// exposed one of them as "the cloud" would be right for half its readers.
/// </para>
/// </summary>
public sealed class Ichimoku : Indicator
{
    public const int DefaultConversionPeriod = 9;
    public const int DefaultBasePeriod = 26;
    public const int DefaultSpanBPeriod = 52;

    private readonly int _conversion;
    private readonly int _base;
    private readonly int _spanB;
    private readonly int _displacement;
    private readonly RollingWindow _highs;
    private readonly RollingWindow _lows;
    private readonly RollingWindow _closes;
    private readonly Queue<(decimal A, decimal B)> _ahead = new();
    private readonly Queue<decimal> _pastCloses = new();

    public Ichimoku(
        int conversionPeriod = DefaultConversionPeriod,
        int basePeriod = DefaultBasePeriod,
        int spanBPeriod = DefaultSpanBPeriod,
        int? displacement = null)
        : base($"ICHIMOKU({conversionPeriod},{basePeriod},{spanBPeriod})")
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(conversionPeriod, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(basePeriod, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(spanBPeriod, 1);
        _conversion = conversionPeriod;
        _base = basePeriod;
        _spanB = spanBPeriod;

        // The displacement is conventionally the base period, and is separate because it is a different decision.
        _displacement = displacement ?? basePeriod;
        ArgumentOutOfRangeException.ThrowIfLessThan(_displacement, 1);

        _highs = new RollingWindow(Math.Max(spanBPeriod, Math.Max(conversionPeriod, basePeriod)));
        _lows = new RollingWindow(_highs.Capacity);
        _closes = new RollingWindow(_highs.Capacity);
    }

    /// <summary>Tenkan-sen: the midpoint of the conversion period's range.</summary>
    public decimal ConversionLine { get; private set; }

    /// <summary>Kijun-sen: the midpoint of the base period's range.</summary>
    public decimal BaseLine { get; private set; }

    /// <summary>Senkou span A as just computed, which belongs <see cref="Displacement"/> bars ahead of this one.</summary>
    public decimal SpanAAhead { get; private set; }

    /// <summary>Senkou span B as just computed, which belongs <see cref="Displacement"/> bars ahead of this one.</summary>
    public decimal SpanBAhead { get; private set; }

    /// <summary>The top of the cloud over THIS bar: the higher of the two spans computed <see cref="Displacement"/> bars ago.</summary>
    public decimal CloudTop { get; private set; }

    /// <summary>The bottom of the cloud over THIS bar.</summary>
    public decimal CloudBottom { get; private set; }

    /// <summary>
    /// The close <see cref="Displacement"/> bars ago, which is the price the chikou span compares the current close
    /// with. Zero until there have been that many bars.
    /// </summary>
    public decimal ChikouReference { get; private set; }

    /// <summary>Whether the cloud over this bar has span A above span B, which is the bullish arrangement.</summary>
    public bool CloudIsBullish { get; private set; }

    public int Displacement => _displacement;

    public override void Update(Bar bar)
    {
        _highs.Add(bar.High.Value);
        _lows.Add(bar.Low.Value);
        _closes.Add(bar.Close.Value);
        HasInputs = true;

        ConversionLine = Midpoint(_conversion);
        BaseLine = Midpoint(_base);
        SpanAAhead = (ConversionLine + BaseLine) / 2m;
        SpanBAhead = Midpoint(_spanB);

        // What is plotted forward is kept until the bar it belongs to arrives, which is the whole of the displacement.
        _ahead.Enqueue((SpanAAhead, SpanBAhead));
        if (_ahead.Count > _displacement)
        {
            (decimal a, decimal b) = _ahead.Dequeue();
            CloudTop = Math.Max(a, b);
            CloudBottom = Math.Min(a, b);
            CloudIsBullish = a >= b;
        }

        _pastCloses.Enqueue(bar.Close.Value);
        if (_pastCloses.Count > _displacement)
        {
            ChikouReference = _pastCloses.Dequeue();
        }

        // Ready when the longest line is full AND the cloud over this bar exists: an Ichimoku without its cloud is not
        // one, and a strategy waiting on IsInitialized is waiting for the whole of it.
        IsInitialized = _highs.Count >= _spanB && _ahead.Count == _displacement && _pastCloses.Count == _displacement;
    }

    public override void UpdateRaw(decimal value) =>
        throw new NotSupportedException("Ichimoku is built from the high, low and close of a bar, so a single value cannot feed it.");

    public override void Update(QuoteTick tick) => throw new NotSupportedException("Ichimoku needs bars.");

    public override void Update(TradeTick tick) => throw new NotSupportedException("Ichimoku needs bars.");

    public override void Reset()
    {
        base.Reset();
        _highs.Clear();
        _lows.Clear();
        _closes.Clear();
        _ahead.Clear();
        _pastCloses.Clear();
        ConversionLine = 0m;
        BaseLine = 0m;
        SpanAAhead = 0m;
        SpanBAhead = 0m;
        CloudTop = 0m;
        CloudBottom = 0m;
        ChikouReference = 0m;
        CloudIsBullish = false;
    }

    private decimal Midpoint(int period)
    {
        int count = Math.Min(period, _highs.Count);
        decimal high = decimal.MinValue;
        decimal low = decimal.MaxValue;
        for (int i = _highs.Count - count; i < _highs.Count; i++)
        {
            high = Math.Max(high, _highs[i]);
            low = Math.Min(low, _lows[i]);
        }

        return count == 0 ? 0m : (high + low) / 2m;
    }
}

/// <summary>
/// Supertrend: an ATR band that follows price on one side and only ever tightens, until it is crossed (R7.8).
///
/// <para>
/// The rule that makes it what it is: while the trend holds, a band never moves against the position. A new band
/// looser than the one in force is ignored, so the stop it represents does not retreat when volatility widens.
/// </para>
/// </summary>
public sealed class Supertrend : Indicator
{
    public const int DefaultPeriod = 10;
    public const decimal DefaultMultiplier = 3m;

    private readonly decimal _multiplier;
    private readonly AverageTrueRange _atr;
    private decimal _upper;
    private decimal _lower;
    private decimal _previousClose;
    private bool _hasBands;

    public Supertrend(int period = DefaultPeriod, decimal multiplier = DefaultMultiplier)
        : base($"SUPERTREND({period},{multiplier})")
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(multiplier, 0m);
        _multiplier = multiplier;
        _atr = new AverageTrueRange(period);
    }

    /// <summary>The band in force: below price in an uptrend, above it in a downtrend.</summary>
    public decimal Value { get; private set; }

    /// <summary>1 while the trend is up, -1 while it is down, 0 before there is one.</summary>
    public int Direction { get; private set; }

    /// <summary>Whether the direction changed on this bar, which is the signal this indicator exists for.</summary>
    public bool Flipped { get; private set; }

    public decimal Upper => _upper;

    public decimal Lower => _lower;

    public override void Update(Bar bar)
    {
        _atr.Update(bar);
        HasInputs = true;
        if (!_atr.IsInitialized)
        {
            _previousClose = bar.Close.Value;
            return;
        }

        decimal middle = (bar.High.Value + bar.Low.Value) / 2m;
        decimal basicUpper = middle + (_multiplier * _atr.Value);
        decimal basicLower = middle - (_multiplier * _atr.Value);
        int previousDirection = Direction;

        if (!_hasBands)
        {
            _upper = basicUpper;
            _lower = basicLower;
            Direction = bar.Close.Value >= middle ? 1 : -1;
            _hasBands = true;
        }
        else
        {
            // A band only tightens while its trend holds; it is allowed to loosen once price has passed it.
            _upper = basicUpper < _upper || _previousClose > _upper ? basicUpper : _upper;
            _lower = basicLower > _lower || _previousClose < _lower ? basicLower : _lower;

            if (Direction == 1 && bar.Close.Value < _lower)
            {
                Direction = -1;
            }
            else if (Direction == -1 && bar.Close.Value > _upper)
            {
                Direction = 1;
            }
        }

        Value = Direction == 1 ? _lower : _upper;
        Flipped = previousDirection != 0 && previousDirection != Direction;
        _previousClose = bar.Close.Value;
        IsInitialized = true;
    }

    public override void UpdateRaw(decimal value) =>
        throw new NotSupportedException("Supertrend is built from the high, low and close of a bar, so a single value cannot feed it.");

    public override void Update(QuoteTick tick) => throw new NotSupportedException("Supertrend needs bars.");

    public override void Update(TradeTick tick) => throw new NotSupportedException("Supertrend needs bars.");

    public override void Reset()
    {
        base.Reset();
        _atr.Reset();
        _upper = 0m;
        _lower = 0m;
        _previousClose = 0m;
        _hasBands = false;
        Value = 0m;
        Direction = 0;
        Flipped = false;
    }
}

/// <summary>
/// Parabolic SAR: a stop that accelerates towards price for as long as the trend keeps making extremes (R7.8).
/// </summary>
public sealed class ParabolicSar : Indicator
{
    public const decimal DefaultStep = 0.02m;
    public const decimal DefaultMaximum = 0.2m;

    private readonly decimal _step;
    private readonly decimal _maximum;
    private decimal _extreme;
    private decimal _acceleration;
    private decimal _previousHigh;
    private decimal _previousLow;
    private decimal _priorHigh;
    private decimal _priorLow;
    private bool _started;

    public ParabolicSar(decimal step = DefaultStep, decimal maximum = DefaultMaximum)
        : base($"PSAR({step},{maximum})")
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(step, 0m);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximum, step);
        _step = step;
        _maximum = maximum;
    }

    public decimal Value { get; private set; }

    /// <summary>1 while the stop is below price, -1 while it is above, 0 before there is a direction.</summary>
    public int Direction { get; private set; }

    public bool Flipped { get; private set; }

    public override void Update(Bar bar)
    {
        HasInputs = true;

        if (!_started)
        {
            // The first bar only gives the extremes to start from; a direction is taken on the second.
            _previousHigh = bar.High.Value;
            _previousLow = bar.Low.Value;
            _priorHigh = bar.High.Value;
            _priorLow = bar.Low.Value;
            _started = true;
            Flipped = false;
            return;
        }

        if (Direction == 0)
        {
            Direction = bar.Close.Value >= _previousHigh ? 1 : -1;
            _extreme = Direction == 1 ? Math.Max(bar.High.Value, _previousHigh) : Math.Min(bar.Low.Value, _previousLow);
            Value = Direction == 1 ? Math.Min(bar.Low.Value, _previousLow) : Math.Max(bar.High.Value, _previousHigh);
            _acceleration = _step;
            Remember(bar);
            IsInitialized = true;
            Flipped = false;
            return;
        }

        int previousDirection = Direction;
        decimal sar = Value + (_acceleration * (_extreme - Value));

        if (Direction == 1)
        {
            // Clamped to the two lows BEFORE this bar, which is the rule as defined - and the difference matters: a stop
            // clamped to this bar's own low can never be traded through by this bar, so it would never turn over at all.
            sar = Math.Min(sar, Math.Min(_previousLow, _priorLow));
            if (bar.Low.Value < sar)
            {
                Direction = -1;
                sar = _extreme;
                _extreme = bar.Low.Value;
                _acceleration = _step;
            }
            else if (bar.High.Value > _extreme)
            {
                _extreme = bar.High.Value;
                _acceleration = Math.Min(_maximum, _acceleration + _step);
            }
        }
        else
        {
            sar = Math.Max(sar, Math.Max(_previousHigh, _priorHigh));
            if (bar.High.Value > sar)
            {
                Direction = 1;
                sar = _extreme;
                _extreme = bar.High.Value;
                _acceleration = _step;
            }
            else if (bar.Low.Value < _extreme)
            {
                _extreme = bar.Low.Value;
                _acceleration = Math.Min(_maximum, _acceleration + _step);
            }
        }

        Value = sar;
        Flipped = previousDirection != Direction;
        Remember(bar);
        IsInitialized = true;
    }

    private void Remember(Bar bar)
    {
        _priorHigh = _previousHigh;
        _priorLow = _previousLow;
        _previousHigh = bar.High.Value;
        _previousLow = bar.Low.Value;
    }

    public override void UpdateRaw(decimal value) =>
        throw new NotSupportedException("A parabolic SAR is built from the high and low of a bar, so a single value cannot feed it.");

    public override void Update(QuoteTick tick) => throw new NotSupportedException("A parabolic SAR needs bars.");

    public override void Update(TradeTick tick) => throw new NotSupportedException("A parabolic SAR needs bars.");

    public override void Reset()
    {
        base.Reset();
        _extreme = 0m;
        _acceleration = 0m;
        _previousHigh = 0m;
        _previousLow = 0m;
        _priorHigh = 0m;
        _priorLow = 0m;
        _started = false;
        Value = 0m;
        Direction = 0;
        Flipped = false;
    }
}

/// <summary>
/// The least-squares line through the last <c>period</c> values: its slope, where it ends, and how well it fits (R7.8).
///
/// <para>
/// <c>Value</c> is the fitted value at the newest point rather than the line's intercept, because that is what a
/// strategy compares with price. <c>RSquared</c> is here because a slope without a fit is a number a trend can be
/// read into: the same slope through a straight line and through noise mean different things.
/// </para>
/// </summary>
public sealed class LinearRegression : Indicator
{
    public const int DefaultPeriod = 20;

    private readonly RollingWindow _window;

    public LinearRegression(int period = DefaultPeriod, PriceType priceType = PriceType.Last)
        : base($"LINREG({period})", priceType)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 2);
        _window = new RollingWindow(period);
    }

    public int Period => _window.Capacity;

    /// <summary>Change per step of the fitted line.</summary>
    public decimal Slope { get; private set; }

    /// <summary>The fitted value at the oldest point in the window.</summary>
    public decimal Intercept { get; private set; }

    /// <summary>The fitted value at the newest point, which is what price is compared with.</summary>
    public decimal Value { get; private set; }

    /// <summary>How much of the movement the line explains, from 0 to 1. Zero variation is reported as 1.</summary>
    public decimal RSquared { get; private set; }

    public override void UpdateRaw(decimal value)
    {
        _window.Add(value);
        HasInputs = true;
        if (!_window.IsFull)
        {
            return;
        }

        int n = _window.Count;
        decimal sumX = 0m;
        decimal sumY = 0m;
        decimal sumXy = 0m;
        decimal sumXx = 0m;
        for (int i = 0; i < n; i++)
        {
            decimal x = i;
            decimal y = _window[i];
            sumX += x;
            sumY += y;
            sumXy += x * y;
            sumXx += x * x;
        }

        decimal denominator = (n * sumXx) - (sumX * sumX);
        Slope = denominator == 0m ? 0m : ((n * sumXy) - (sumX * sumY)) / denominator;
        Intercept = (sumY - (Slope * sumX)) / n;
        Value = Intercept + (Slope * (n - 1));

        decimal mean = sumY / n;
        decimal totalVariation = 0m;
        decimal residual = 0m;
        for (int i = 0; i < n; i++)
        {
            decimal fitted = Intercept + (Slope * i);
            decimal deviation = _window[i] - mean;
            decimal error = _window[i] - fitted;
            totalVariation += deviation * deviation;
            residual += error * error;
        }

        RSquared = totalVariation == 0m ? 1m : 1m - (residual / totalVariation);
        IsInitialized = true;
    }

    public override void Reset()
    {
        base.Reset();
        _window.Clear();
        Slope = 0m;
        Intercept = 0m;
        Value = 0m;
        RSquared = 0m;
    }
}
