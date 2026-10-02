using System.Globalization;

namespace Bytex.Core.Model.Primitives;

/// <summary>
/// A price with an explicit number of decimal places.
/// </summary>
public readonly record struct Price : IComparable<Price>, IComparable
{
    public const byte MaxPrecision = 18;

    /// <summary>Where a decimal keeps its scale: the flags word of its bit layout, in the byte above the low sixteen.</summary>
    private const int ScaleFlagsIndex = 3;

    private const int ScaleShift = 16;

    private const int ScaleMask = 0xFF;

    public Price(decimal value, byte precision)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(precision, MaxPrecision);
        Precision = precision;
        Value = decimal.Round(value, precision, MidpointRounding.ToEven);
    }

    public decimal Value { get; }

    public byte Precision { get; }

    public bool IsZero => Value == 0m;

    public bool IsPositive => Value > 0m;

    public static Price Zero(byte precision) => new(0m, precision);

    public static Price FromDecimal(decimal value) => new(value, InferPrecision(value));

    public static Price Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        decimal value = decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture);
        return new Price(value, InferPrecision(text));
    }

    public static bool TryParse(string? text, out Price price)
    {
        price = default;
        if (string.IsNullOrWhiteSpace(text) || !decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal value))
        {
            return false;
        }

        price = new Price(value, InferPrecision(text));
        return true;
    }

    internal static byte InferPrecision(string text)
    {
        int dot = text.IndexOf('.');
        if (dot < 0)
        {
            return 0;
        }

        int digits = 0;
        for (int i = dot + 1; i < text.Length && char.IsDigit(text[i]); i++)
        {
            digits++;
        }

        return (byte)Math.Min(digits, MaxPrecision);
    }

    internal static byte InferPrecision(decimal value)
    {
        int scale = (decimal.GetBits(value)[ScaleFlagsIndex] >> ScaleShift) & ScaleMask;
        return (byte)Math.Min(scale, MaxPrecision);
    }

    public int CompareTo(Price other) => Value.CompareTo(other.Value);

    public int CompareTo(object? obj) => obj is Price other ? CompareTo(other) : throw new ArgumentException("Object is not a Price.", nameof(obj));

    public static bool operator <(Price left, Price right) => left.Value < right.Value;

    public static bool operator >(Price left, Price right) => left.Value > right.Value;

    public static bool operator <=(Price left, Price right) => left.Value <= right.Value;

    public static bool operator >=(Price left, Price right) => left.Value >= right.Value;

    public static Price operator +(Price left, Price right) => new(left.Value + right.Value, Math.Max(left.Precision, right.Precision));

    public static Price operator -(Price left, Price right) => new(left.Value - right.Value, Math.Max(left.Precision, right.Precision));

    public static Price operator +(Price left, decimal right) => new(left.Value + right, left.Precision);

    public static Price operator -(Price left, decimal right) => new(left.Value - right, left.Precision);

    public static Price operator *(Price left, decimal right) => new(left.Value * right, left.Precision);

    public static Price operator /(Price left, decimal right) => new(left.Value / right, left.Precision);

    public static implicit operator decimal(Price price) => price.Value;

    public override string ToString() => Value.ToString("F" + Precision.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
}
