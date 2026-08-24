using System.Globalization;

namespace Bytex.Core.Model.Primitives;

/// <summary>
/// A non-negative quantity with an explicit number of decimal places.
/// </summary>
public readonly record struct Quantity : IComparable<Quantity>, IComparable
{
    public const byte MaxPrecision = 18;

    public Quantity(decimal value, byte precision)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(precision, MaxPrecision);
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        Precision = precision;
        Value = decimal.Round(value, precision, MidpointRounding.ToEven);
    }

    public decimal Value { get; }

    public byte Precision { get; }

    public bool IsZero => Value == 0m;

    public bool IsPositive => Value > 0m;

    public static Quantity Zero(byte precision) => new(0m, precision);

    public static Quantity FromDecimal(decimal value) => new(value, Price.InferPrecision(value));

    public static Quantity Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        decimal value = decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture);
        return new Quantity(value, Price.InferPrecision(text));
    }

    public static bool TryParse(string? text, out Quantity quantity)
    {
        quantity = default;
        if (string.IsNullOrWhiteSpace(text) || !decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal value) || value < 0m)
        {
            return false;
        }

        quantity = new Quantity(value, Price.InferPrecision(text));
        return true;
    }

    public int CompareTo(Quantity other) => Value.CompareTo(other.Value);

    public int CompareTo(object? obj) => obj is Quantity other ? CompareTo(other) : throw new ArgumentException("Object is not a Quantity.", nameof(obj));

    public static bool operator <(Quantity left, Quantity right) => left.Value < right.Value;

    public static bool operator >(Quantity left, Quantity right) => left.Value > right.Value;

    public static bool operator <=(Quantity left, Quantity right) => left.Value <= right.Value;

    public static bool operator >=(Quantity left, Quantity right) => left.Value >= right.Value;

    public static Quantity operator +(Quantity left, Quantity right) => new(left.Value + right.Value, Math.Max(left.Precision, right.Precision));

    /// <summary>
    /// Subtracts <paramref name="right"/> from <paramref name="left"/>; throws if the result would be negative.
    /// </summary>
    public static Quantity operator -(Quantity left, Quantity right) => new(left.Value - right.Value, Math.Max(left.Precision, right.Precision));

    public static Quantity operator *(Quantity left, decimal right) => new(left.Value * right, left.Precision);

    public static Quantity operator /(Quantity left, decimal right) => new(left.Value / right, left.Precision);

    public static implicit operator decimal(Quantity quantity) => quantity.Value;

    public static Quantity Min(Quantity a, Quantity b) => a <= b ? a : b;

    public static Quantity Max(Quantity a, Quantity b) => a >= b ? a : b;

    public override string ToString() => Value.ToString("F" + Precision.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
}
