using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Primitives;

namespace Bytex.Indicators;

/// <summary>
/// Commodity channel index: how far the typical price is from its average, in mean deviations (R7.8).
///
/// <para>
/// The 0.015 is not a parameter of anything; it is the constant the index was defined with, chosen so that most values
/// land between -100 and 100. It is named here rather than written into the arithmetic so that nobody reads it as a
/// coincidence.
/// </para>
/// </summary>
public sealed class CommodityChannelIndex : Indicator
{
    public const int DefaultPeriod = 20;

    /// <summary>The constant the index is defined with, which scales mean deviations into its familiar range.</summary>
    public const decimal Constant = 0.015m;

    private readonly RollingWindow _typical;

    public CommodityChannelIndex(int period = DefaultPeriod)
        : base($"CCI({period})")
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 1);
        _typical = new RollingWindow(period);
    }

    public int Period => _typical.Capacity;

    public decimal Value { get; private set; }

    public override void Update(Bar bar)
    {
        Add((bar.High.Value + bar.Low.Value + bar.Close.Value) / 3m);
    }

    /// <summary>
    /// Feeds a single value as the typical price, for a caller with no bars. A quote or a trade has no range, so its
    /// price IS its typical price.
    /// </summary>
    public override void UpdateRaw(decimal value) => Add(value);

    public override void Reset()
    {
        base.Reset();
        _typical.Clear();
        Value = 0m;
    }

    private void Add(decimal typicalPrice)
    {
        _typical.Add(typicalPrice);
        HasInputs = true;
        if (!_typical.IsFull)
        {
            return;
        }

        decimal average = _typical.Average;
        decimal deviation = 0m;
        for (int i = 0; i < _typical.Count; i++)
        {
            deviation += Math.Abs(_typical[i] - average);
        }

        deviation /= _typical.Count;

        // A window with no deviation at all has no scale to measure against, so the index is zero rather than infinite:
        // a price that has not moved is not far from its average.
        Value = deviation == 0m ? 0m : (typicalPrice - average) / (Constant * deviation);
        IsInitialized = true;
    }
}

/// <summary>
/// Money flow index: the relative strength index applied to typical price times volume (R7.8).
///
/// <para>
/// It needs volume, which is why it takes bars and nothing else: a quote carries no volume, and feeding it one would
/// produce an index of a quantity nobody traded.
/// </para>
/// </summary>
public sealed class MoneyFlowIndex : Indicator
{
    public const int DefaultPeriod = 14;

    private readonly RollingWindow _positive;
    private readonly RollingWindow _negative;
    private decimal _previousTypical;
    private bool _hasPrevious;

    public MoneyFlowIndex(int period = DefaultPeriod)
        : base($"MFI({period})")
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 1);
        _positive = new RollingWindow(period);
        _negative = new RollingWindow(period);
    }

    public int Period => _positive.Capacity;

    /// <summary>Value from 0 to 100.</summary>
    public decimal Value { get; private set; }

    public override void Update(Bar bar)
    {
        decimal typical = (bar.High.Value + bar.Low.Value + bar.Close.Value) / 3m;
        decimal flow = typical * bar.Volume.Value;
        HasInputs = true;

        if (!_hasPrevious)
        {
            _previousTypical = typical;
            _hasPrevious = true;
            return;
        }

        // A typical price that did not move belongs to neither side, which is the definition rather than a convenience:
        // counting it as positive would read a flat market as buying.
        _positive.Add(typical > _previousTypical ? flow : 0m);
        _negative.Add(typical < _previousTypical ? flow : 0m);
        _previousTypical = typical;

        if (!_positive.IsFull)
        {
            return;
        }

        decimal total = _positive.Sum + _negative.Sum;
        Value = total == 0m ? Scales.Percent / 2m : Scales.Percent * _positive.Sum / total;
        IsInitialized = true;
    }

    public override void UpdateRaw(decimal value) =>
        throw new NotSupportedException("A money flow index is price and volume, so a single value cannot feed it.");

    public override void Update(QuoteTick tick) =>
        throw new NotSupportedException("A money flow index needs volume, which a quote does not carry.");

    public override void Update(TradeTick tick) =>
        throw new NotSupportedException("A money flow index is built from bars, whose volume is the whole of the period's.");

    public override void Reset()
    {
        base.Reset();
        _positive.Clear();
        _negative.Clear();
        _previousTypical = 0m;
        _hasPrevious = false;
        Value = 0m;
    }
}

/// <summary>
/// Chande momentum oscillator: what share of the period's movement went one way, from -100 to 100 (R7.8).
///
/// <para>
/// It is the relative strength index's arithmetic without the smoothing, which is the point of it: an unsmoothed
/// measure turns at the bar it turns on rather than several bars later.
/// </para>
/// </summary>
public sealed class ChandeMomentumOscillator : Indicator
{
    public const int DefaultPeriod = 14;

    private readonly RollingWindow _up;
    private readonly RollingWindow _down;
    private decimal _previous;
    private bool _hasPrevious;

    public ChandeMomentumOscillator(int period = DefaultPeriod, PriceType priceType = PriceType.Last)
        : base($"CMO({period})", priceType)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 1);
        _up = new RollingWindow(period);
        _down = new RollingWindow(period);
    }

    public int Period => _up.Capacity;

    /// <summary>Value from -100 to 100.</summary>
    public decimal Value { get; private set; }

    public override void UpdateRaw(decimal value)
    {
        HasInputs = true;
        if (!_hasPrevious)
        {
            _previous = value;
            _hasPrevious = true;
            return;
        }

        decimal change = value - _previous;
        _previous = value;
        _up.Add(change > 0m ? change : 0m);
        _down.Add(change < 0m ? -change : 0m);

        if (!_up.IsFull)
        {
            return;
        }

        decimal total = _up.Sum + _down.Sum;

        // A period that did not move is neither up nor down, so zero rather than a division by nothing.
        Value = total == 0m ? 0m : Scales.Percent * (_up.Sum - _down.Sum) / total;
        IsInitialized = true;
    }

    public override void Reset()
    {
        base.Reset();
        _up.Clear();
        _down.Clear();
        _previous = 0m;
        _hasPrevious = false;
        Value = 0m;
    }
}
