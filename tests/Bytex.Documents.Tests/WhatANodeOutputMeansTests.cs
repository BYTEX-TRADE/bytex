using Bytex.Documents.Catalog;
using Xunit;

namespace Bytex.Documents.Tests;

// A chart that draws an indicator has to know which axis it belongs on: an EMA of close sits among the candles,
// an RSI belongs in its own pane between 0 and 100, a MACD histogram oscillates about zero. Nothing said so, so
// anything drawing one had to keep its own list of node types - and such a list goes stale the moment an indicator
// is added, silently, with the first wrong line drawn some time later.
//
// The unit is declared beside the indicator instead. These tests are what stop the declaration going stale: a new
// indicator with an undeclared output fails here rather than on somebody's screen.
public sealed class WhatANodeOutputMeansTests
{
    private static IReadOnlyList<NodeTypeDescriptor> Indicators() =>
        [.. NodeCatalog.Default.Types.Where(d => d.Kind == NodeKind.Indicator)];

    /// <summary>Every numeric output of every indicator says what its numbers mean.</summary>
    [Fact]
    public void Every_indicator_output_declares_its_unit()
    {
        List<string> undeclared = [];

        foreach (NodeTypeDescriptor node in Indicators())
        {
            foreach (PortSpec port in node.Outputs.Where(p => p.Kind == ValueKind.Series))
            {
                if (port.Unit == PortUnit.Unknown)
                {
                    undeclared.Add($"{node.Type}:{port.Name}");
                }
            }
        }

        Assert.True(
            undeclared.Count == 0,
            "these outputs do not say what their numbers mean, so nothing can draw them without guessing: " + string.Join(", ", undeclared));
    }

    /// <summary>
    /// The scan has to have found something. Without this, an empty catalog - or a filter that stopped matching
    /// indicators - would make the test above pass by examining nothing.
    /// </summary>
    [Fact]
    public void The_catalog_really_holds_indicators_to_check()
    {
        Assert.True(Indicators().Count >= 20, $"only {Indicators().Count} indicators were found");
        Assert.Contains(Indicators(), d => d.Type == "ind.ema");
        Assert.Contains(Indicators(), d => d.Type == "ind.rsi");
    }

    /// <summary>
    /// The units are right, not merely present. A moving average of price is a price; an RSI is a bounded
    /// percentage; a MACD is a difference with no natural scale. Getting these wrong puts a line on the wrong axis,
    /// which is exactly what the declaration exists to prevent.
    /// </summary>
    [Theory]
    [InlineData("ind.ema", "value", PortUnit.Price)]
    [InlineData("ind.sma", "value", PortUnit.Price)]
    [InlineData("ind.vwap", "value", PortUnit.Price)]
    [InlineData("ind.bbands", "upper", PortUnit.Price)]
    [InlineData("ind.rsi", "value", PortUnit.Percent)]
    [InlineData("ind.adx", "value", PortUnit.Percent)]
    [InlineData("ind.macd", "histogram", PortUnit.Unbounded)]
    [InlineData("ind.obv", "value", PortUnit.Unbounded)]
    [InlineData("ind.atr", "value", PortUnit.Unbounded)]
    public void An_output_declares_the_unit_it_actually_has(string type, string port, PortUnit expected)
    {
        PortSpec spec = NodeCatalog.Default.Types.Single(d => d.Type == type).Outputs.Single(p => p.Name == port);

        Assert.Equal(expected, spec.Unit);
    }

    /// <summary>A bounded oscillator carries its bounds, so a pane is drawn without inferring them from the data.</summary>
    [Theory]
    [InlineData("ind.rsi", "value", 0, 100)]
    [InlineData("ind.stoch", "k", 0, 100)]
    [InlineData("ind.aroon", "value", -100, 100)]
    [InlineData("ind.linreg", "rSquared", 0, 1)]
    public void A_bounded_output_carries_its_range(string type, string port, int min, int max)
    {
        PortSpec spec = NodeCatalog.Default.Types.Single(d => d.Type == type).Outputs.Single(p => p.Name == port);

        Assert.Equal(min, spec.Min);
        Assert.Equal(max, spec.Max);
    }

    /// <summary>And a price output has no range invented for it: an instrument's price has no bounds to state.</summary>
    [Fact]
    public void A_price_output_states_no_range()
    {
        PortSpec ema = NodeCatalog.Default.Types.Single(d => d.Type == "ind.ema").Outputs.Single(p => p.Name == "value");

        Assert.Null(ema.Min);
        Assert.Null(ema.Max);
    }

    /// <summary>The unit reaches anything reading the exported catalog, which is how a separate process sees it.</summary>
    [Fact]
    public void The_exported_catalog_carries_the_unit_and_the_range()
    {
        string json = NodeCatalog.Default.ExportJson(indented: false);

        Assert.Contains("\"unit\":\"price\"", json, StringComparison.Ordinal);
        Assert.Contains("\"unit\":\"percent\"", json, StringComparison.Ordinal);
        Assert.Contains("\"min\":\"0\"", json, StringComparison.Ordinal);
        Assert.Contains("\"max\":\"100\"", json, StringComparison.Ordinal);

        // An undeclared unit writes nothing at all, so a reader can tell silence from a stated scale.
        Assert.DoesNotContain("\"unit\":\"unknown\"", json, StringComparison.Ordinal);
    }
}
