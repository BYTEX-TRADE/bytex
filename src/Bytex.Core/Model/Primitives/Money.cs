using System.Globalization;

namespace Bytex.Core.Model.Primitives;

/// <summary>
/// A monetary amount in a specific currency. Amounts may be negative.
/// </summary>
public readonly record struct Money : IComparable<Money>, IComparable
{
    public Money(decimal amount, Currency currency)
    {
        ArgumentNullException.ThrowIfNull(currency);
        Currency = currency;
        Amount = decimal.Round(amount, currency.Precision, MidpointRounding.ToEven);
    }

    public decimal Amount { get; }

    public Currency Currency { get; }

    public bool IsZero => Amount == 0m;

    public bool IsPositive => Amount > 0m;

    public bool IsNegative => Amount < 0m;

    public static Money Zero(Currency currency) => new(0m, currency);

    /// <summary>
    /// Parses text in the form "123.45 USD".
    /// </summary>
    public static Money Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        string[] parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            throw new FormatException($"Money text '{text}' must be in the form '<amount> <currency>'.");
        }

        decimal amount = decimal.Parse(parts[0], NumberStyles.Number, CultureInfo.InvariantCulture);
        return new Money(amount, Currency.FromCode(parts[1]));
    }

    public Money Negate() => new(-Amount, Currency);

    public Money Abs() => new(Math.Abs(Amount), Currency);

    public int CompareTo(Money other)
    {
        EnsureSameCurrency(other);
        return Amount.CompareTo(other.Amount);
    }

    public int CompareTo(object? obj) => obj is Money other ? CompareTo(other) : throw new ArgumentException("Object is not a Money.", nameof(obj));

    private void EnsureSameCurrency(Money other)
    {
        if (!Currency.Equals(other.Currency))
        {
            throw new InvalidOperationException($"Cannot combine {Currency} with {other.Currency}.");
        }
    }

    public static Money operator +(Money left, Money right)
    {
        left.EnsureSameCurrency(right);
        return new Money(left.Amount + right.Amount, left.Currency);
    }

    public static Money operator -(Money left, Money right)
    {
        left.EnsureSameCurrency(right);
        return new Money(left.Amount - right.Amount, left.Currency);
    }

    public static Money operator -(Money value) => value.Negate();

    public static Money operator *(Money left, decimal right) => new(left.Amount * right, left.Currency);

    public static Money operator /(Money left, decimal right) => new(left.Amount / right, left.Currency);

    public static bool operator <(Money left, Money right) => left.CompareTo(right) < 0;

    public static bool operator >(Money left, Money right) => left.CompareTo(right) > 0;

    public static bool operator <=(Money left, Money right) => left.CompareTo(right) <= 0;

    public static bool operator >=(Money left, Money right) => left.CompareTo(right) >= 0;

    public string ToFormattedString() => Amount.ToString("N" + Currency.Precision.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) + " " + Currency.Code;

    public override string ToString() => Amount.ToString("F" + Currency.Precision.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) + " " + Currency.Code;
}
