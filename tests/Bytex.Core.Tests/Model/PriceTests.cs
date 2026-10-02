using Bytex.Core.Model.Primitives;
using static Bytex.Core.Tests.Model.ModelFixtures;

namespace Bytex.Core.Tests.Model;

// Price is the value every order and fill is expressed in. These tests protect its fixed-point contract:
// explicit precision, banker's rounding on construction, exact decimal arithmetic, and culture-independent text.
public class PriceTests
{
    [Theory]
    [InlineData("1.2345", 2, "1.23")]
    [InlineData("1.2355", 2, "1.24")]
    [InlineData("1.005", 2, "1.00")] // midpoint, 0 is even
    [InlineData("1.015", 2, "1.02")] // midpoint, 1 is odd so round up to 2
    [InlineData("1.025", 2, "1.02")] // midpoint, 2 is even
    [InlineData("2.5", 0, "2")]
    [InlineData("3.5", 0, "4")]
    [InlineData("-2.5", 0, "-2")]
    [InlineData("-1.015", 2, "-1.02")]
    public void Constructor_rounds_to_precision_with_bankers_rounding(string input, byte precision, string expected)
    {
        Price price = new(D(input), precision);

        Assert.Equal(D(expected), price.Value);
        Assert.Equal(precision, price.Precision);
    }

    [Fact]
    public void Constructor_accepts_the_maximum_precision_of_18()
    {
        Price price = new(0.000000000000000001m, 18);

        Assert.Equal(0.000000000000000001m, price.Value);
        Assert.Equal("0.000000000000000001", price.ToString());
    }

    [Fact]
    public void Constructor_rejects_precision_above_18()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Price(1m, 19));
    }

    [Theory]
    [InlineData("100", "100", 0)]
    [InlineData("100.50", "100.5", 2)]
    [InlineData("0.00000001", "0.00000001", 8)]
    [InlineData("-12.345", "-12.345", 3)]
    [InlineData("1,000.5", "1000.5", 1)]
    [InlineData(" 42.10 ", "42.1", 2)]
    [InlineData("7.", "7", 0)]
    public void Parse_takes_the_precision_from_the_number_of_decimals_written(string text, string expectedValue, byte expectedPrecision)
    {
        Price price = Price.Parse(text);

        Assert.Equal(D(expectedValue), price.Value);
        Assert.Equal(expectedPrecision, price.Precision);
    }

    [Fact]
    public void Parse_caps_precision_at_18_and_rounds_the_excess_digit()
    {
        // 19 decimals; the 19th digit (9) rounds the 18th (8) up to 9.
        Price price = Price.Parse("0.1234567890123456789");

        Assert.Equal(18, price.Precision);
        Assert.Equal(0.123456789012345679m, price.Value);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1e5")]
    [InlineData("1.2.3")]
    [InlineData("$10")]
    [InlineData("10 USD")]
    public void Parse_rejects_text_that_is_not_an_invariant_decimal(string text)
    {
        Assert.Throws<FormatException>(() => Price.Parse(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_rejects_missing_text(string? text)
    {
        Assert.ThrowsAny<ArgumentException>(() => Price.Parse(text!));
    }

    [Fact]
    public void Parse_rejects_values_beyond_the_decimal_range()
    {
        // decimal.MaxValue is 79228162514264337593543950335.
        Assert.Throws<OverflowException>(() => Price.Parse("79228162514264337593543950336"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("abc")]
    [InlineData("1e5")]
    [InlineData("79228162514264337593543950336")]
    public void TryParse_returns_false_instead_of_throwing(string? text)
    {
        bool ok = Price.TryParse(text, out Price price);

        Assert.False(ok);
        Assert.Equal(default, price);
    }

    [Fact]
    public void TryParse_returns_the_same_price_as_Parse()
    {
        Assert.True(Price.TryParse("50000.10", out Price price));
        Assert.Equal(Price.Parse("50000.10"), price);
        Assert.Equal(2, price.Precision);
    }

    [Theory]
    [InlineData("100.5", 2, "100.50")]
    [InlineData("100", 0, "100")]
    [InlineData("0.1", 8, "0.10000000")]
    [InlineData("-5", 2, "-5.00")]
    [InlineData("1234567.891", 3, "1234567.891")] // no group separators
    public void ToString_pads_to_the_precision_and_uses_a_dot(string value, byte precision, string expected)
    {
        Assert.Equal(expected, new Price(D(value), precision).ToString());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("50000.10")]
    [InlineData("0.00000100")]
    [InlineData("-0.50")]
    [InlineData("1234567890.123456789012345678")] // 28 significant digits, the most a decimal holds exactly
    public void Text_round_trips_through_Parse_and_ToString_unchanged(string text)
    {
        Assert.Equal(text, Price.Parse(text).ToString());
    }

    [Theory]
    [InlineData("1.50", 2)]
    [InlineData("100", 0)]
    [InlineData("0.12500", 5)]
    public void FromDecimal_takes_the_precision_from_the_decimal_scale(string literal, byte expectedPrecision)
    {
        Price price = Price.FromDecimal(D(literal));

        Assert.Equal(expectedPrecision, price.Precision);
        Assert.Equal(literal, price.ToString());
    }

    [Fact]
    public void Addition_is_exact_where_binary_floating_point_is_not()
    {
        Price sum = new Price(0.1m, 1) + new Price(0.2m, 1);

        Assert.Equal(new Price(0.3m, 1), sum);
        Assert.NotEqual(0.3, 0.1 + 0.2); // the drift this type exists to avoid
    }

    [Fact]
    public void Ten_thousand_ticks_of_one_cent_sum_to_exactly_one_hundred()
    {
        Price total = Price.Zero(2);
        Price tick = new(0.01m, 2);
        for (int i = 0; i < 10_000; i++)
        {
            total += tick;
        }

        Assert.Equal(100.00m, total.Value);
        Assert.Equal("100.00", total.ToString());
    }

    [Fact]
    public void Adding_or_subtracting_prices_keeps_the_wider_precision()
    {
        Price a = new(1.5m, 1);
        Price b = new(0.25m, 2);

        Assert.Equal(new Price(1.75m, 2), a + b);
        Assert.Equal(new Price(1.25m, 2), a - b);
        Assert.Equal(new Price(-1.25m, 2), b - a);
    }

    [Theory]
    [InlineData("10.00", "0.004", "10.00")]
    [InlineData("10.00", "0.005", "10.00")] // 10.005 is a midpoint, 0 is even
    [InlineData("10.00", "0.015", "10.02")] // 10.015 is a midpoint, 1 is odd
    [InlineData("10.00", "-0.25", "9.75")]
    public void Adding_a_decimal_keeps_the_price_precision(string price, string delta, string expected)
    {
        Price result = Price.Parse(price) + D(delta);

        Assert.Equal(expected, result.ToString());
        Assert.Equal((Price.Parse(price) - (-D(delta))).ToString(), result.ToString());
    }

    [Theory]
    [InlineData("10.25", "3", "30.75")]
    [InlineData("0.07", "0.5", "0.04")] // 0.035 is a midpoint, 3 is odd so it rounds to 0.04
    [InlineData("0.05", "0.5", "0.02")] // 0.025 is a midpoint, 2 is even
    [InlineData("19.99", "-1", "-19.99")]
    public void Multiplication_rounds_the_product_to_the_price_precision(string price, string factor, string expected)
    {
        Assert.Equal(expected, (Price.Parse(price) * D(factor)).ToString());
    }

    [Theory]
    [InlineData("10.00", "3", "3.33")]
    [InlineData("20.00", "3", "6.67")]
    [InlineData("1.00", "8", "0.12")] // 0.125 is a midpoint, 2 is even
    public void Division_rounds_the_quotient_to_the_price_precision(string price, string divisor, string expected)
    {
        Assert.Equal(expected, (Price.Parse(price) / D(divisor)).ToString());
    }

    [Fact]
    public void Division_by_zero_throws()
    {
        Assert.Throws<DivideByZeroException>(() => Price.Parse("1.00") / 0m);
    }

    [Fact]
    public void Arithmetic_beyond_the_decimal_range_throws_instead_of_wrapping()
    {
        Price max = new(decimal.MaxValue, 0);

        Assert.Throws<OverflowException>(() => max + new Price(1m, 0));
        Assert.Throws<OverflowException>(() => max * 2m);
    }

    [Fact]
    public void Comparison_orders_by_value_regardless_of_precision()
    {
        Price low = new(99.99m, 2);
        Price high = new(100m, 0);
        Price highWithCents = new(100.00m, 2);

        Assert.True(low < high);
        Assert.True(high > low);
        Assert.True(low <= high);
        Assert.False(low >= high);
        Assert.True(high <= highWithCents);
        Assert.True(high >= highWithCents);
        Assert.Equal(0, high.CompareTo(highWithCents));
        Assert.True(low.CompareTo(high) < 0);
        Assert.True(high.CompareTo(low) > 0);
    }

    [Fact]
    public void Sorting_uses_the_numeric_order_including_negative_prices()
    {
        List<Price> prices = [Price.Parse("10.5"), Price.Parse("-3.25"), Price.Parse("0"), Price.Parse("10.49")];

        prices.Sort();

        Assert.Equal(["-3.25", "0", "10.49", "10.5"], prices.Select(p => p.ToString()));
    }

    [Fact]
    public void Non_generic_CompareTo_rejects_other_types()
    {
        IComparable price = Price.Parse("1.00");

        Assert.Equal(0, price.CompareTo(Price.Parse("1.00")));
        Assert.Throws<ArgumentException>(() => price.CompareTo(1.00m));
        Assert.Throws<ArgumentException>(() => price.CompareTo(null));
    }

    [Fact]
    public void Equal_value_and_precision_means_equal_price_and_equal_hash()
    {
        Price a = Price.Parse("50000.10");
        Price b = new(50000.1m, 2);

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, Price.Parse("50000.11"));
    }

    [Fact]
    public void Sign_predicates_follow_the_value()
    {
        Assert.True(Price.Zero(2).IsZero);
        Assert.False(Price.Zero(2).IsPositive);
        Assert.Equal("0.00", Price.Zero(2).ToString());
        Assert.True(Price.Parse("0.01").IsPositive);
        Assert.False(Price.Parse("-0.01").IsPositive);
        Assert.False(Price.Parse("-0.01").IsZero);
    }

    [Fact]
    public void Converts_implicitly_to_decimal()
    {
        decimal value = Price.Parse("12.34");

        Assert.Equal(12.34m, value);
    }
}
