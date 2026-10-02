using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Primitives;

namespace Bytex.Core.Tests.Model;

// Bar types are written in configs, used as bus topics and stored in the catalog as text, so the text form
// must parse back to the same value. Interval arithmetic decides where bar boundaries fall.
public class CandleSeriesTests
{
    [Theory]
    [InlineData("bx-sampling:v2/minute/1/last", 1, SamplingMethod.Minute, PriceType.Last)]
    [InlineData("bx-sampling:v2/Minute/5/Bid", 5, SamplingMethod.Minute, PriceType.Bid)]
    [InlineData("bx-sampling:v2/Tick/100/Mid", 100, SamplingMethod.Tick, PriceType.Mid)]
    [InlineData("bx-sampling:v2/volume/1000/last", 1000, SamplingMethod.Volume, PriceType.Last)]
    [InlineData("bx-sampling:v2/day/1/ask", 1, SamplingMethod.Day, PriceType.Ask)]
    [InlineData("bx-sampling:v2/hour/8/mark", 8, SamplingMethod.Hour, PriceType.Mark)]
    public void Specification_parses_step_aggregation_and_price_type_ignoring_case(string text, int step, SamplingMethod aggregation, PriceType priceType)
    {
        Assert.Equal(new SamplingRule(step, aggregation, priceType), SamplingRule.Parse(text));
    }

    [Theory]
    [InlineData(SamplingMethod.Tick)]
    [InlineData(SamplingMethod.TickImbalance)]
    [InlineData(SamplingMethod.Volume)]
    [InlineData(SamplingMethod.VolumeImbalance)]
    [InlineData(SamplingMethod.Value)]
    [InlineData(SamplingMethod.ValueImbalance)]
    [InlineData(SamplingMethod.Millisecond)]
    [InlineData(SamplingMethod.Second)]
    [InlineData(SamplingMethod.Minute)]
    [InlineData(SamplingMethod.Hour)]
    [InlineData(SamplingMethod.Day)]
    [InlineData(SamplingMethod.Week)]
    [InlineData(SamplingMethod.Month)]
    public void Specification_text_round_trips_for_every_aggregation(SamplingMethod aggregation)
    {
        SamplingRule spec = new(3, aggregation, PriceType.Last);

        Assert.Equal(spec, SamplingRule.Parse(spec.ToString()));
    }

    [Fact]
    public void Specification_text_has_a_versioned_prefix_and_separate_fields()
    {
        Assert.Equal("bx-sampling:v2/minute/15/bid", new SamplingRule(15, SamplingMethod.Minute, PriceType.Bid).ToString());
        Assert.Equal("bx-sampling:v2/tickimbalance/500/last", new SamplingRule(500, SamplingMethod.TickImbalance, PriceType.Last).ToString());
    }

    [Theory]
    [InlineData("1-MINUTE")]
    [InlineData("1-MINUTE-LAST-EXTERNAL")]
    [InlineData("x-MINUTE-LAST")]
    [InlineData("1.bx-sampling:v2/minute/5/last")]
    public void Specification_rejects_malformed_text_with_a_format_exception(string text)
    {
        Assert.Throws<FormatException>(() => SamplingRule.Parse(text));
    }

    [Theory]
    [InlineData("bx-sampling:v2/fortnight/1/last")]
    [InlineData("bx-sampling:v2/minute/1/close")]
    public void Specification_rejects_unknown_aggregations_and_price_types(string text)
    {
        Assert.Throws<ArgumentException>(() => SamplingRule.Parse(text));
    }

    [Theory]
    [InlineData(250, SamplingMethod.Millisecond, 250_000_000L)]
    [InlineData(30, SamplingMethod.Second, 30_000_000_000L)]
    [InlineData(1, SamplingMethod.Minute, 60_000_000_000L)]
    [InlineData(15, SamplingMethod.Minute, 900_000_000_000L)]
    [InlineData(4, SamplingMethod.Hour, 14_400_000_000_000L)]
    [InlineData(1, SamplingMethod.Day, 86_400_000_000_000L)]
    [InlineData(2, SamplingMethod.Week, 1_209_600_000_000_000L)] // 14 days
    public void Interval_of_a_time_bar_is_step_times_unit(int step, SamplingMethod aggregation, long expectedNanos)
    {
        SamplingRule spec = new(step, aggregation, PriceType.Last);

        Assert.True(spec.IsTimeAggregated);
        Assert.Equal(expectedNanos, spec.IntervalNanos);
        Assert.Equal(TimeSpan.FromTicks(expectedNanos / 100), spec.Interval);
    }

    [Fact]
    public void Interval_of_a_large_day_step_does_not_overflow_32_bit_arithmetic()
    {
        // 365 days = 31 536 000 s, far beyond int range once expressed in nanoseconds.
        Assert.Equal(31_536_000_000_000_000L, new SamplingRule(365, SamplingMethod.Day, PriceType.Last).IntervalNanos);
    }

    [Theory]
    [InlineData(SamplingMethod.Tick)]
    [InlineData(SamplingMethod.Volume)]
    [InlineData(SamplingMethod.ValueImbalance)]
    public void Interval_is_undefined_for_bars_that_are_not_time_based(SamplingMethod aggregation)
    {
        SamplingRule spec = new(100, aggregation, PriceType.Last);

        Assert.False(spec.IsTimeAggregated);
        Assert.Throws<InvalidOperationException>(() => spec.IntervalNanos);
    }

    [Fact]
    public void Bar_type_parses_the_documented_form()
    {
        CandleSeries candleSeries = CandleSeries.Parse("bx-candle:v2/BINANCE/BTCUSDT/minute/1/last/provider");

        Assert.Equal(MarketKey.Parse("bx-market:v2/BINANCE/BTCUSDT"), candleSeries.MarketKey);
        Assert.Equal(new SamplingRule(1, SamplingMethod.Minute, PriceType.Last), candleSeries.Spec);
        Assert.Equal(CandleOrigin.Provider, candleSeries.Source);
        Assert.True(candleSeries.IsProvider);
        Assert.False(candleSeries.IsComputed);
    }

    [Fact]
    public void Bar_type_keeps_hyphens_that_belong_to_the_symbol()
    {
        CandleSeries candleSeries = CandleSeries.Parse("bx-candle:v2/OKX/ETH-USDT-SWAP/minute/5/bid/computed");

        Assert.Equal("bx-market:v2/OKX/ETH-USDT-SWAP", candleSeries.MarketKey.Value);
        Assert.Equal(new SamplingRule(5, SamplingMethod.Minute, PriceType.Bid), candleSeries.Spec);
        Assert.True(candleSeries.IsComputed);
    }

    [Fact]
    public void Bar_type_source_defaults_to_external()
    {
        CandleSeries candleSeries = new(MarketKey.Parse("bx-market:v2/BINANCE/BTCUSDT"), new SamplingRule(1, SamplingMethod.Hour, PriceType.Last));

        Assert.Equal(CandleOrigin.Provider, candleSeries.Source);
        Assert.Equal("bx-candle:v2/BINANCE/BTCUSDT/hour/1/last/provider", candleSeries.ToString());
    }

    [Theory]
    [InlineData("bx-candle:v2/BINANCE/BTCUSDT/minute/1/last/provider")]
    [InlineData("bx-candle:v2/OKX/ETH-USDT-SWAP/minute/5/bid/computed")]
    [InlineData("bx-candle:v2/NYSE/BRK.B/day/1/last/provider")]
    [InlineData("bx-candle:v2/BITMEX/XBTUSD/volume/1000/last/computed")]
    public void Bar_type_text_round_trips_unchanged(string text)
    {
        Assert.Equal(text, CandleSeries.Parse(text).ToString());
    }

    [Theory]
    [InlineData("bx-market:v2/BINANCE/BTCUSDT-1-MINUTE-LAST")] // source missing
    [InlineData("BTCUSDT-1-MINUTE-LAST-EXTERNAL")] // venue missing
    [InlineData("bx-market:v2/BINANCE/BTCUSDT-x-MINUTE-LAST-EXTERNAL")]
    public void Bar_type_Parse_rejects_malformed_text(string text)
    {
        Assert.Throws<FormatException>(() => CandleSeries.Parse(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bx-market:v2/BINANCE/BTCUSDT-1-MINUTE-LAST")]
    [InlineData("BTCUSDT-1-MINUTE-LAST-EXTERNAL")]
    [InlineData("bx-market:v2/BINANCE/BTCUSDT-x-MINUTE-LAST-EXTERNAL")]
    [InlineData("bx-candle:v2/BINANCE/BTCUSDT/fortnight/1/last/provider")]
    [InlineData("bx-market:v2/BINANCE/BTCUSDT-1-MINUTE-LAST-SOMEWHERE")]
    public void Bar_type_TryParse_returns_false_for_malformed_text(string? text)
    {
        Assert.False(CandleSeries.TryParse(text, out CandleSeries candleSeries));
        Assert.Equal(default, candleSeries);
    }

    [Fact]
    public void Bar_type_TryParse_returns_false_when_the_step_does_not_fit_an_int()
    {
        Assert.False(CandleSeries.TryParse("bx-candle:v2/BINANCE/BTCUSDT/minute/99999999999/last/provider", out _));
    }

    [Fact]
    public void Bar_type_TryParse_returns_the_same_value_as_Parse()
    {
        Assert.True(CandleSeries.TryParse("bx-candle:v2/BINANCE/BTCUSDT/minute/1/last/provider", out CandleSeries candleSeries));
        Assert.Equal(CandleSeries.Parse("bx-candle:v2/BINANCE/BTCUSDT/minute/1/last/provider"), candleSeries);
    }

    [Fact]
    public void Bar_types_with_the_same_parts_are_equal_and_usable_as_keys()
    {
        CandleSeries a = CandleSeries.Parse("bx-candle:v2/BINANCE/BTCUSDT/minute/1/last/provider");
        CandleSeries b = new(MarketKey.Parse("bx-market:v2/BINANCE/BTCUSDT"), new SamplingRule(1, SamplingMethod.Minute, PriceType.Last), CandleOrigin.Provider);
        Dictionary<CandleSeries, UnixNanos> lastSeen = new() { [a] = UnixNanos.Zero };

        Assert.Equal(a, b);
        Assert.True(lastSeen.ContainsKey(b));
        Assert.NotEqual(a, CandleSeries.Parse("bx-candle:v2/BINANCE/BTCUSDT/minute/1/last/computed"));
    }

    [Fact]
    public void Bar_type_Parse_says_why_an_oversized_step_is_refused()
    {
        // TryParse answers false for the same text.
        Assert.Throws<OverflowException>(() => CandleSeries.Parse("bx-candle:v2/BINANCE/BTCUSDT/minute/99999999999/last/provider"));
    }
}
