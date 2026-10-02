using Bytex.Indicators.Tests.Support;

namespace Bytex.Indicators.Tests;

// Bollinger bands depend on this square root; a double-precision result would show up in the 16th digit.
public class DecimalMathTests
{
    [Theory]
    [InlineData("0", "0")]
    [InlineData("1", "1")]
    [InlineData("4", "2")]
    [InlineData("2.25", "1.5")]
    [InlineData("0.0001", "0.01")]
    [InlineData("152399025", "12345")]
    [InlineData("100000000000000000000", "10000000000")]
    public void Perfect_squares_have_exact_roots(string value, string expected)
    {
        Assert.Equal(Series.Parse(expected)[0], DecimalMath.Sqrt(Series.Parse(value)[0]));
    }

    [Fact]
    public void The_root_of_two_is_correct_well_beyond_double_precision()
    {
        // sqrt(2) = 1.41421356237309504880168872420969807856967... (OEIS A002193)
        Check.Close(1.4142135623730950488016887242m, DecimalMath.Sqrt(2m), 0.0000000000000000000000001m);
    }

    [Theory]
    [InlineData("0.0000000000000000000000000001")]
    [InlineData("0.000000000003")]
    [InlineData("7")]
    [InlineData("98765.4321")]
    [InlineData("12345678901234567890.123456789")]
    public void Squaring_the_root_gives_the_value_back(string text)
    {
        decimal value = Series.Parse(text)[0];

        decimal root = DecimalMath.Sqrt(value);

        // Relative error of 1e-24: the absolute error of root * root scales with the value.
        Check.Close(value, root * root, Math.Max(value, 1m) * 0.000000000000000000000001m);
    }

    [Fact]
    public void It_agrees_with_an_independent_bisection()
    {
        foreach (decimal value in new[] { 0.5m, 3m, 10m, 1234.5678m })
        {
            Check.Close(Reference.SqrtByBisection(value), DecimalMath.Sqrt(value), 0.0000000000000000000000001m);
        }
    }

    [Fact]
    public void A_negative_value_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DecimalMath.Sqrt(-0.0001m));
    }
}
