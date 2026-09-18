using Bytex.Core.Model.Primitives;
using static Bytex.Core.Tests.Model.ModelFixtures;

namespace Bytex.Core.Tests.Model;

// Money is what balances, commissions and P&L are kept in. These tests protect rounding to the currency's
// precision, exact accumulation, the ban on mixing currencies, and culture-independent text.
public class MoneyTests
{
    [Theory]
    [InlineData("1.005", "USD", "1.00")] // midpoint, 0 is even
    [InlineData("1.015", "USD", "1.02")] // midpoint, 1 is odd
    [InlineData("-1.015", "USD", "-1.02")]
    [InlineData("2.5", "JPY", "2")] // JPY has no minor unit
    [InlineData("3.5", "JPY", "4")]
    [InlineData("0.123456785", "BTC", "0.12345678")] // midpoint, 8 is even
    [InlineData("0.123456775", "BTC", "0.12345678")] // midpoint, 7 is odd
    [InlineData("1850.1235", "XAU", "1850.124")] // midpoint, 3 is odd
    public void Constructor_rounds_to_the_currency_precision_with_bankers_rounding(string amount, string currency, string expected)
    {
        Money money = new(D(amount), Currency.FromCode(currency));

        Assert.Equal(D(expected), money.Amount);
    }

    [Fact]
    public void Constructor_rejects_a_missing_currency()
    {
        Assert.Throws<ArgumentNullException>(() => new Money(1m, null!));
    }

    [Theory]
    [InlineData("123.45 USD", "123.45", "USD")]
    [InlineData("  -0.5   BTC ", "-0.5", "BTC")]
    [InlineData("1,000 JPY", "1000", "JPY")]
    [InlineData("0.129 EUR", "0.13", "EUR")] // rounded to two decimals
    public void Parse_reads_amount_then_currency_code(string text, string expectedAmount, string expectedCurrency)
    {
        Money money = Money.Parse(text);

        Assert.Equal(D(expectedAmount), money.Amount);
        Assert.Equal(expectedCurrency, money.Currency.Code);
    }

    [Theory]
    [InlineData("123.45")]
    [InlineData("USD")]
    [InlineData("1 2 USD")]
    [InlineData("abc USD")]
    [InlineData("USD 10")]
    public void Parse_rejects_malformed_text(string text)
    {
        Assert.Throws<FormatException>(() => Money.Parse(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_rejects_missing_text(string? text)
    {
        Assert.ThrowsAny<ArgumentException>(() => Money.Parse(text!));
    }

    [Theory]
    [InlineData("1234.5", "USD", "1234.50 USD", "1,234.50 USD")]
    [InlineData("-1234.5", "USD", "-1234.50 USD", "-1,234.50 USD")]
    [InlineData("1234567", "JPY", "1234567 JPY", "1,234,567 JPY")]
    [InlineData("0.5", "BTC", "0.50000000 BTC", "0.50000000 BTC")]
    [InlineData("0", "USDT", "0.00000000 USDT", "0.00000000 USDT")]
    public void Text_forms_use_invariant_separators_and_the_currency_precision(string amount, string currency, string expectedPlain, string expectedFormatted)
    {
        Money money = new(D(amount), Currency.FromCode(currency));

        Assert.Equal(expectedPlain, money.ToString());
        Assert.Equal(expectedFormatted, money.ToFormattedString());
    }

    [Theory]
    [InlineData("1234.50 USD")]
    [InlineData("-0.00000001 BTC")]
    [InlineData("1234567 JPY")]
    public void Text_round_trips_through_Parse_and_ToString_unchanged(string text)
    {
        Assert.Equal(text, Money.Parse(text).ToString());
    }

    [Fact]
    public void Addition_is_exact_where_binary_floating_point_is_not()
    {
        Money sum = new Money(0.1m, Currencies.BTC) + new Money(0.2m, Currencies.BTC);

        Assert.Equal(new Money(0.3m, Currencies.BTC), sum);
        Assert.True(sum == new Money(0.3m, Currencies.BTC));
    }

    [Fact]
    public void A_thousand_one_cent_commissions_sum_to_exactly_ten_dollars()
    {
        Money total = Money.Zero(Currencies.USD);
        for (int i = 0; i < 1000; i++)
        {
            total += new Money(0.01m, Currencies.USD);
        }

        Assert.Equal(10.00m, total.Amount);
        Assert.Equal("10.00 USD", total.ToString());
    }

    [Fact]
    public void Subtraction_can_go_negative()
    {
        Money result = new Money(10m, Currencies.USD) - new Money(12.5m, Currencies.USD);

        Assert.Equal(-2.5m, result.Amount);
        Assert.True(result.IsNegative);
        Assert.False(result.IsPositive);
        Assert.False(result.IsZero);
    }

    [Fact]
    public void Arithmetic_between_different_currencies_throws()
    {
        Money usd = new(1m, Currencies.USD);
        Money eur = new(1m, Currencies.EUR);

        Assert.Throws<InvalidOperationException>(() => usd + eur);
        Assert.Throws<InvalidOperationException>(() => usd - eur);
    }

    [Fact]
    public void Comparison_between_different_currencies_throws()
    {
        Money usd = new(1m, Currencies.USD);
        Money eur = new(2m, Currencies.EUR);

        Assert.Throws<InvalidOperationException>(() => usd < eur);
        Assert.Throws<InvalidOperationException>(() => usd >= eur);
        Assert.Throws<InvalidOperationException>(() => usd.CompareTo(eur));
    }

    [Fact]
    public void Comparison_within_a_currency_orders_by_amount()
    {
        Money loss = new(-5m, Currencies.USD);
        Money gain = new(5m, Currencies.USD);

        Assert.True(loss < gain);
        Assert.True(gain > loss);
        Assert.True(loss <= new Money(-5.00m, Currencies.USD));
        Assert.True(gain >= new Money(5.00m, Currencies.USD));
        Assert.Equal(0, gain.CompareTo(new Money(5.00m, Currencies.USD)));
    }

    [Fact]
    public void Non_generic_CompareTo_rejects_other_types()
    {
        IComparable money = new Money(1m, Currencies.USD);

        Assert.Throws<ArgumentException>(() => money.CompareTo(1m));
    }

    [Theory]
    [InlineData("10.00", "0.333", "3.33")]
    [InlineData("0.05", "0.5", "0.02")] // 0.025 is a midpoint, 2 is even
    [InlineData("100.00", "-0.0005", "-0.05")]
    public void Multiplication_rounds_to_the_currency_precision(string amount, string factor, string expected)
    {
        Money result = new Money(D(amount), Currencies.USD) * D(factor);

        Assert.Equal(D(expected), result.Amount);
        Assert.Equal(Currencies.USD, result.Currency);
    }

    [Theory]
    [InlineData("10.00", "3", "3.33")]
    [InlineData("20.00", "3", "6.67")]
    [InlineData("0.01", "2", "0.00")] // 0.005 is a midpoint, 0 is even
    public void Division_rounds_to_the_currency_precision(string amount, string divisor, string expected)
    {
        Assert.Equal(D(expected), (new Money(D(amount), Currencies.USD) / D(divisor)).Amount);
    }

    [Fact]
    public void Division_by_zero_throws()
    {
        Assert.Throws<DivideByZeroException>(() => new Money(1m, Currencies.USD) / 0m);
    }

    [Fact]
    public void Negate_Abs_and_unary_minus_keep_the_currency()
    {
        Money loss = new(-12.34m, Currencies.EUR);

        Assert.Equal(new Money(12.34m, Currencies.EUR), loss.Negate());
        Assert.Equal(new Money(12.34m, Currencies.EUR), -loss);
        Assert.Equal(new Money(12.34m, Currencies.EUR), loss.Abs());
        Assert.Equal(new Money(12.34m, Currencies.EUR), loss.Abs().Abs());
    }

    [Fact]
    public void Equality_requires_the_same_amount_and_currency()
    {
        Money a = new(1m, Currencies.USD);
        Money sameWithTrailingZeros = new(1.00m, Currencies.USD);

        Assert.Equal(a, sameWithTrailingZeros);
        Assert.Equal(a.GetHashCode(), sameWithTrailingZeros.GetHashCode());
        Assert.NotEqual(a, new Money(1m, Currencies.EUR));
        Assert.NotEqual(a, new Money(1.01m, Currencies.USD));
    }

    [Fact]
    public void Zero_is_zero_in_the_requested_currency()
    {
        Money zero = Money.Zero(Currencies.JPY);

        Assert.True(zero.IsZero);
        Assert.Equal(Currencies.JPY, zero.Currency);
        Assert.Equal("0 JPY", zero.ToString());
    }
}
