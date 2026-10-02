using Bytex.Core.Indicators;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;

namespace Bytex.Indicators;

/// <summary>
/// Base class for indicators. Subclasses implement <see cref="UpdateRaw"/> for single-value inputs
/// and may override the bar update for indicators that need the full OHLCV.
/// </summary>
public abstract class Indicator : IIndicator
{
    protected Indicator(string name, PriceType priceType = PriceType.Last)
    {
        Name = name;
        PriceType = priceType;
    }

    public string Name { get; }

    public PriceType PriceType { get; }

    public bool HasInputs { get; protected set; }

    public bool IsInitialized { get; protected set; }

    public virtual void Update(Bar bar) => UpdateRaw(bar.Close.Value);

    public virtual void Update(QuoteTick tick) => UpdateRaw(tick.ExtractPrice(PriceType == PriceType.Last ? PriceType.Mid : PriceType).Value);

    public virtual void Update(TradeTick tick) => UpdateRaw(tick.Price.Value);

    public abstract void UpdateRaw(decimal value);

    public virtual void Reset()
    {
        HasInputs = false;
        IsInitialized = false;
    }

    public override string ToString() => Name;
}

/// <summary>
/// Fixed-capacity ring buffer of decimals, oldest first when enumerated.
/// </summary>
public sealed class RollingWindow
{
    private readonly decimal[] _buffer;
    private int _head;

    public RollingWindow(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _buffer = new decimal[capacity];
    }

    public int Capacity => _buffer.Length;

    public int Count { get; private set; }

    public bool IsFull => Count == _buffer.Length;

    public decimal Sum { get; private set; }

    public void Add(decimal value)
    {
        if (IsFull)
        {
            Sum -= _buffer[_head];
        }
        else
        {
            Count++;
        }

        _buffer[_head] = value;
        Sum += value;
        _head = (_head + 1) % _buffer.Length;
    }

    /// <summary>Index 0 is the oldest value.</summary>
    public decimal this[int index]
    {
        get
        {
            if (index < 0 || index >= Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            int start = IsFull ? _head : 0;
            return _buffer[(start + index) % _buffer.Length];
        }
    }

    public decimal Latest => Count == 0 ? 0m : this[Count - 1];

    public decimal Oldest => Count == 0 ? 0m : this[0];

    public decimal Max()
    {
        decimal max = decimal.MinValue;
        for (int i = 0; i < Count; i++)
        {
            max = Math.Max(max, this[i]);
        }

        return max;
    }

    public decimal Min()
    {
        decimal min = decimal.MaxValue;
        for (int i = 0; i < Count; i++)
        {
            min = Math.Min(min, this[i]);
        }

        return min;
    }

    public decimal Average => Count == 0 ? 0m : Sum / Count;

    public decimal StandardDeviation()
    {
        if (Count == 0)
        {
            return 0m;
        }

        decimal mean = Average;
        decimal variance = 0m;
        for (int i = 0; i < Count; i++)
        {
            decimal d = this[i] - mean;
            variance += d * d;
        }

        return DecimalMath.Sqrt(variance / Count);
    }

    public void Clear()
    {
        Count = 0;
        _head = 0;
        Sum = 0m;
        Array.Clear(_buffer);
    }
}

public static class DecimalMath
{
    public static decimal Sqrt(decimal value)
    {
        if (value < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        if (value == 0m)
        {
            return 0m;
        }

        decimal x = (decimal)Math.Sqrt((double)value);
        for (int i = 0; i < 4; i++)
        {
            x = (x + value / x) / 2m;
        }

        return x;
    }
}
