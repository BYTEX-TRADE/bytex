using Bytex.Core.Indicators;
using Bytex.Core.Model;
using Bytex.Indicators.Tests.Support;

namespace Bytex.Indicators.Tests;

// Configuration-driven creation: docs/concepts/indicators.md promises every built-in by name with string parameters.
// An indicator's Name embeds its parameters, which makes the parsed values observable through the public API.
public class BuiltinIndicatorFactoryTests
{
    private readonly BuiltinIndicatorFactory _factory = new();

    [Fact]
    public void It_offers_every_indicator_listed_in_the_documentation()
    {
        string[] documented = ["SMA", "EMA", "WMA", "DEMA", "HMA", "VWAP", "RSI", "MACD", "ROC", "STOCH", "OBV", "ATR", "BB", "KC", "DC", "ADX", "AROON"];

        Assert.Equal(documented.Order(StringComparer.Ordinal), _factory.Names.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_Donchian_channel_can_be_asked_to_exclude_the_current_bar()
    {
        BuiltinIndicatorFactory factory = new();

        IIndicator excluded = factory.Create("DC", new Dictionary<string, string> { ["period"] = "10", ["excludeCurrent"] = "true" });
        IIndicator included = factory.Create("DC", new Dictionary<string, string> { ["period"] = "10" });

        Assert.Equal("DC(10,excl)", excluded.Name);
        Assert.Equal("DC(10)", included.Name);
    }

    [Theory]
    [InlineData("SMA", typeof(SimpleMovingAverage), "SMA(20)")]
    [InlineData("EMA", typeof(ExponentialMovingAverage), "EMA(20)")]
    [InlineData("WMA", typeof(WeightedMovingAverage), "WMA(20)")]
    [InlineData("DEMA", typeof(DoubleExponentialMovingAverage), "DEMA(20)")]
    [InlineData("HMA", typeof(HullMovingAverage), "HMA(20)")]
    [InlineData("VWAP", typeof(VolumeWeightedAveragePrice), "VWAP")]
    [InlineData("RSI", typeof(RelativeStrengthIndex), "RSI(14)")]
    [InlineData("MACD", typeof(MovingAverageConvergenceDivergence), "MACD(12,26,9)")]
    [InlineData("ROC", typeof(RateOfChange), "ROC(10)")]
    [InlineData("STOCH", typeof(Stochastics), "STOCH(14,3)")]
    [InlineData("OBV", typeof(OnBalanceVolume), "OBV")]
    [InlineData("ATR", typeof(AverageTrueRange), "ATR(14)")]
    [InlineData("BB", typeof(BollingerBands), "BB(20,2)")]
    [InlineData("KC", typeof(KeltnerChannel), "KC(20,2,10)")]
    [InlineData("DC", typeof(DonchianChannel), "DC(20)")]
    [InlineData("ADX", typeof(AverageDirectionalIndex), "ADX(14)")]
    [InlineData("AROON", typeof(AroonOscillator), "AROON(25)")]
    public void Without_parameters_it_builds_the_conventional_defaults(string name, Type expectedType, string expectedName)
    {
        IIndicator indicator = _factory.Create(name, new Dictionary<string, string>());

        Assert.IsType(expectedType, indicator);
        Assert.Equal(expectedName, indicator.Name);
        Assert.False(indicator.HasInputs);
        Assert.False(indicator.IsInitialized);
    }

    [Theory]
    [InlineData("SMA", "period=7", "SMA(7)")]
    [InlineData("EMA", "period=9", "EMA(9)")]
    [InlineData("WMA", "period=3", "WMA(3)")]
    [InlineData("DEMA", "period=30", "DEMA(30)")]
    [InlineData("HMA", "period=16", "HMA(16)")]
    [InlineData("RSI", "period=2", "RSI(2)")]
    [InlineData("MACD", "fast=5;slow=35;signal=5", "MACD(5,35,5)")]
    [InlineData("MACD", "slow=30", "MACD(12,30,9)")]
    [InlineData("ROC", "period=1", "ROC(1)")]
    [InlineData("STOCH", "k=5;d=2", "STOCH(5,2)")]
    [InlineData("ATR", "period=21", "ATR(21)")]
    [InlineData("BB", "period=10;k=2.5", "BB(10,2.5)")]
    [InlineData("KC", "period=10;k=1.5;atr=7", "KC(10,1.5,7)")]
    [InlineData("DC", "period=55", "DC(55)")]
    [InlineData("ADX", "period=8", "ADX(8)")]
    [InlineData("AROON", "period=14", "AROON(14)")]
    public void String_parameters_reach_the_constructor(string name, string parameters, string expectedName)
    {
        IIndicator indicator = _factory.Create(name, Parameters(parameters));

        Assert.Equal(expectedName, indicator.Name);
    }

    [Theory]
    [InlineData("rsi")]
    [InlineData("Rsi")]
    [InlineData("RSI")]
    public void Names_are_matched_case_insensitively(string name)
    {
        Assert.IsType<RelativeStrengthIndex>(_factory.Create(name, new Dictionary<string, string>()));
    }

    [Theory]
    [InlineData("Bid", "99")]
    [InlineData("ask", "101")]
    [InlineData("MID", "100")]
    public void The_priceType_parameter_selects_the_quote_side(string priceType, string expected)
    {
        IIndicator indicator = _factory.Create("SMA", Parameters($"period=1;priceType={priceType}"));

        indicator.Update(Make.Quote(bid: 99m, ask: 101m));

        SimpleMovingAverage sma = Assert.IsType<SimpleMovingAverage>(indicator);
        Assert.Equal(Enum.Parse<PriceType>(priceType, ignoreCase: true), sma.PriceType);
        Assert.Equal(Series.Parse(expected)[0], sma.Value);
    }

    [Fact]
    public void A_decimal_parameter_is_parsed_with_a_dot_and_applied_to_the_bands()
    {
        BollingerBands bb = Assert.IsType<BollingerBands>(_factory.Create("BB", Parameters("period=8;k=2.5")));
        foreach (decimal v in new[] { 2m, 4m, 4m, 4m, 5m, 5m, 7m, 9m }) // mean 5, sigma 2
        {
            bb.UpdateRaw(v);
        }

        Assert.Equal(10m, bb.Upper);
        Assert.Equal(0m, bb.Lower);
    }

    [Fact]
    public void Each_call_returns_a_fresh_independent_instance()
    {
        SimpleMovingAverage first = (SimpleMovingAverage)_factory.Create("SMA", Parameters("period=2"));
        SimpleMovingAverage second = (SimpleMovingAverage)_factory.Create("SMA", Parameters("period=2"));

        first.UpdateRaw(100m);

        Assert.NotSame(first, second);
        Assert.False(second.HasInputs);
    }

    [Theory]
    [InlineData("SUPERTREND")]
    [InlineData("SMA(20)")]
    public void An_unknown_name_is_rejected_and_named_in_the_message(string name)
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() => _factory.Create(name, new Dictionary<string, string>()));

        Assert.Contains(name, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_name_is_rejected(string name)
    {
        Assert.Throws<ArgumentException>(() => _factory.Create(name, new Dictionary<string, string>()));
    }

    [Fact]
    public void An_out_of_range_parameter_surfaces_the_constructor_error()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _factory.Create("EMA", Parameters("period=0")));
    }

    private static Dictionary<string, string> Parameters(string text) =>
        text.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('='))
            .ToDictionary(kv => kv[0], kv => kv[1], StringComparer.Ordinal);
}
