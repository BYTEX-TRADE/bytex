using System.Reflection;
using Bytex.Core.Indicators;

namespace Bytex.Indicators.Tests;

// Why: a period of zero or less is not a period, and an indicator that takes one does not fail where the mistake was
// made - it divides by zero on the first bar, or quietly reports a constant. Three indicators accepted one because
// each was expected to check for itself, so this sweeps every indicator the assembly ships rather than the three.
public class PeriodValidationTests
{
    private static IEnumerable<Type> IndicatorTypes() => typeof(SimpleMovingAverage).Assembly
        .GetTypes()
        .Where(t => t is { IsClass: true, IsAbstract: false, IsPublic: true } && typeof(IIndicator).IsAssignableFrom(t))
        .OrderBy(t => t.Name, StringComparer.Ordinal);

    /// <summary>The constructor to test with: the one taking the fewest arguments that begins with a whole number.</summary>
    private static ConstructorInfo? PeriodConstructor(Type type) => type
        .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
        .Where(c => c.GetParameters() is [{ ParameterType: Type p }, ..] && p == typeof(int))
        .OrderBy(c => c.GetParameters().Length)
        .FirstOrDefault();

    public static TheoryData<string> Named()
    {
        TheoryData<string> data = new();
        foreach (Type type in IndicatorTypes().Where(t => PeriodConstructor(t) is not null))
        {
            data.Add(type.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Named))]
    public void An_indicator_refuses_a_period_below_one(string typeName)
    {
        Type type = IndicatorTypes().Single(t => t.Name == typeName);
        ConstructorInfo ctor = PeriodConstructor(type)!;
        ParameterInfo[] parameters = ctor.GetParameters();

        foreach (int period in new[] { 0, -1, -25 })
        {
            object?[] args = new object?[parameters.Length];
            args[0] = period;
            for (int i = 1; i < parameters.Length; i++)
            {
                args[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : Activator.CreateInstance(parameters[i].ParameterType);
            }

            TargetInvocationException thrown = Assert.Throws<TargetInvocationException>(() => ctor.Invoke(args));
            Assert.IsType<ArgumentOutOfRangeException>(thrown.InnerException);
        }
    }

    [Fact]
    public void The_sweep_covers_the_indicators_that_take_a_period()
    {
        string[] covered = IndicatorTypes().Where(t => PeriodConstructor(t) is not null).Select(t => t.Name).ToArray();

        // The three that used to accept a period of zero, and a sample of those that always refused it.
        Assert.Contains("AverageDirectionalIndex", covered);
        Assert.Contains("AroonOscillator", covered);
        Assert.Contains("RateOfChange", covered);
        Assert.Contains("SimpleMovingAverage", covered);
        Assert.Contains("Stochastics", covered);
        Assert.True(covered.Length >= 12, $"only {covered.Length} indicators were swept: {string.Join(", ", covered)}");
    }

    [Fact]
    public void Stochastics_names_the_period_that_was_wrong()
    {
        // Both periods are refused, and each is refused under its own name: an inner window saying "capacity" or
        // "period" leaves the caller guessing which of the two arguments it got wrong.
        Assert.Equal("kPeriod", Assert.Throws<ArgumentOutOfRangeException>(() => new Stochastics(0, 3)).ParamName);
        Assert.Equal("dPeriod", Assert.Throws<ArgumentOutOfRangeException>(() => new Stochastics(14, 0)).ParamName);
        Assert.Equal("dPeriod", Assert.Throws<ArgumentOutOfRangeException>(() => new Stochastics(14, -3)).ParamName);
    }
}
