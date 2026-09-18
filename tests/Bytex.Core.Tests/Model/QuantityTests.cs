using Bytex.Core.Model.Primitives;
using static Bytex.Core.Tests.Model.ModelFixtures;

namespace Bytex.Core.Tests.Model;

// Quantity carries order and position sizes. Beyond the fixed-point rules it shares with Price,
// it must never be negative: direction lives in OrderSide / PositionSide, not in the sign.
public class QuantityTests
{
    [Theory]
    [InlineData("1.2345", 3, "1.234")] // midpoint, 4 is even
    [InlineData("1.2355", 3, "1.236")] // midpoint, 5 is odd
    [InlineData("0.5", 0, "0")]
    [InlineData("1.5", 0, "2")]
    [InlineData("7.999999", 2, "8.00")]
    public void Constructor_rounds_to_precision_with_bankers_rounding(string input, byte precision, string expected)
    {
        Assert.Equal(expected, new Quantity(D(input), precision).ToString());
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("-0.000001")]
    [InlineData("-0.001")] // would round to zero at precision 2, but the sign is checked first
    public void Constructor_rejects_negative_values(string value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Quantity(D(value), 2));
    }

    [Fact]
    public void Constructor_rejects_precision_above_18()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Quantity(1m, 19));
    }

    [Theory]
    [InlineData("10", "10", 0)]
    [InlineData("0.500", "0.5", 3)]
    [InlineData("1,250.75", "1250.75", 2)]
    [InlineData("0.000001", "0.000001", 6)]
    public void Parse_takes_the_precision_from_the_number_of_decimals_written(string text, string expectedValue, byte expectedPrecision)
    {
        Quantity quantity = Quantity.Parse(text);

        Assert.Equal(D(expectedValue), quantity.Value);
        Assert.Equal(expectedPrecision, quantity.Precision);
    }

    [Fact]
    public void Parse_rejects_negative_text()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Quantity.Parse("-1.5"));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1e3")]
    [InlineData("1.0.0")]
    public void Parse_rejects_text_that_is_not_an_invariant_decimal(string text)
    {
        Assert.Throws<FormatException>(() => Quantity.Parse(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Parse_rejects_missing_text(string? text)
    {
        Assert.ThrowsAny<ArgumentException>(() => Quantity.Parse(text!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("-0.01")]
    [InlineData("79228162514264337593543950336")]
    public void TryParse_returns_false_for_missing_malformed_or_negative_text(string? text)
    {
        Assert.False(Quantity.TryParse(text, out Quantity quantity));
        Assert.Equal(default, quantity);
    }

    [Fact]
    public void TryParse_returns_the_same_quantity_as_Parse()
    {
        Assert.True(Quantity.TryParse("0.250", out Quantity quantity));
        Assert.Equal(Quantity.Parse("0.250"), quantity);
        Assert.Equal("0.250", quantity.ToString());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1.500000")]
    [InlineData("100000")]
    [InlineData("0.000000000000000001")]
    public void Text_round_trips_through_Parse_and_ToString_unchanged(string text)
    {
        Assert.Equal(text, Quantity.Parse(text).ToString());
    }

    [Fact]
    public void FromDecimal_takes_the_precision_from_the_decimal_scale()
    {
        Quantity quantity = Quantity.FromDecimal(2.50m);

        Assert.Equal(2, quantity.Precision);
        Assert.Equal("2.50", quantity.ToString());
    }

    [Fact]
    public void One_tenth_added_ten_times_is_exactly_one()
    {
        Quantity total = Quantity.Zero(1);
        for (int i = 0; i < 10; i++)
        {
            total += new Quantity(0.1m, 1);
        }

        Assert.Equal(new Quantity(1.0m, 1), total);

        double drifting = 0;
        for (int i = 0; i < 10; i++)
        {
            drifting += 0.1;
        }

        Assert.NotEqual(1.0, drifting); // what a double-based quantity would have produced
    }

    [Fact]
    public void Addition_and_subtraction_keep_the_wider_precision()
    {
        Quantity a = Quantity.Parse("1.5");
        Quantity b = Quantity.Parse("0.125");

        Assert.Equal(Quantity.Parse("1.625"), a + b);
        Assert.Equal(Quantity.Parse("1.375"), a - b);
    }

    [Fact]
    public void Subtracting_to_exactly_zero_is_allowed()
    {
        Quantity result = Quantity.Parse("0.300") - Quantity.Parse("0.300");

        Assert.True(result.IsZero);
        Assert.Equal("0.000", result.ToString());
    }

    [Fact]
    public void Subtracting_below_zero_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Quantity.Parse("1.0") - Quantity.Parse("1.1"));
    }

    [Theory]
    [InlineData("1.25", "3", "3.75")]
    [InlineData("0.05", "0.5", "0.02")] // 0.025 is a midpoint, 2 is even
    [InlineData("3", "0.5", "2")] // 1.5 is a midpoint, 1 is odd
    public void Multiplication_rounds_to_the_quantity_precision(string quantity, string factor, string expected)
    {
        Assert.Equal(expected, (Quantity.Parse(quantity) * D(factor)).ToString());
    }

    [Fact]
    public void Multiplying_by_a_negative_factor_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Quantity.Parse("1") * -1m);
    }

    [Theory]
    [InlineData("1.000", "3", "0.333")]
    [InlineData("2.000", "3", "0.667")]
    [InlineData("10", "4", "2")] // 2.5 is a midpoint, 2 is even
    public void Division_rounds_to_the_quantity_precision(string quantity, string divisor, string expected)
    {
        Assert.Equal(expected, (Quantity.Parse(quantity) / D(divisor)).ToString());
    }

    [Fact]
    public void Division_by_zero_throws()
    {
        Assert.Throws<DivideByZeroException>(() => Quantity.Parse("1") / 0m);
    }

    [Fact]
    public void Comparison_orders_by_value_regardless_of_precision()
    {
        Quantity small = Quantity.Parse("0.999");
        Quantity one = Quantity.Parse("1");
        Quantity onePadded = Quantity.Parse("1.000");

        Assert.True(small < one);
        Assert.True(one > small);
        Assert.True(one <= onePadded);
        Assert.True(one >= onePadded);
        Assert.Equal(0, one.CompareTo(onePadded));
        Assert.True(small.CompareTo(one) < 0);
    }

    [Fact]
    public void Min_and_Max_pick_by_value()
    {
        Quantity a = Quantity.Parse("0.4");
        Quantity b = Quantity.Parse("0.35");

        Assert.Equal(b, Quantity.Min(a, b));
        Assert.Equal(b, Quantity.Min(b, a));
        Assert.Equal(a, Quantity.Max(a, b));
        Assert.Equal(a, Quantity.Max(b, a));
    }

    [Fact]
    public void Non_generic_CompareTo_rejects_other_types()
    {
        IComparable quantity = Quantity.Parse("1");

        Assert.Equal(0, quantity.CompareTo(Quantity.Parse("1")));
        Assert.Throws<ArgumentException>(() => quantity.CompareTo(1m));
    }

    [Fact]
    public void Equal_value_and_precision_means_equal_quantity_and_equal_hash()
    {
        Quantity a = Quantity.Parse("1.50");
        Quantity b = new(1.5m, 2);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, Quantity.Parse("1.51"));
    }

    [Fact]
    public void Sign_predicates_and_decimal_conversion_follow_the_value()
    {
        decimal asDecimal = Quantity.Parse("2.5");

        Assert.Equal(2.5m, asDecimal);
        Assert.True(Quantity.Zero(3).IsZero);
        Assert.False(Quantity.Zero(3).IsPositive);
        Assert.True(Quantity.Parse("0.001").IsPositive);
    }
}
