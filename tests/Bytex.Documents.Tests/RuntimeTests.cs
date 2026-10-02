using Bytex.Backtest;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Plugins;
using Bytex.Core.Trading;
using Bytex.Documents.Runtime;
using Bytex.Documents.Schema;
using Xunit;

namespace Bytex.Documents.Tests;

public sealed class RuntimeTests
{
    [Fact]
    public void Ema_cross_document_trades_and_is_deterministic()
    {
        StrategyDocument doc = Fixtures.Example("ema-cross");
        (BacktestResult first, DocumentStrategy strategy) = Fixtures.RunBacktest(doc);
        (BacktestResult second, _) = Fixtures.RunBacktest(doc);

        Assert.True(first.TotalOrders > 5, $"expected trades, got {first.TotalOrders} orders");
        Assert.True(first.TotalPositions > 2, $"expected positions, got {first.TotalPositions}");
        Assert.Equal(Fixtures.Fingerprint(first), Fixtures.Fingerprint(second));
        Assert.Contains(strategy.Decisions, d => d.Kind == "order");
        Assert.Contains(strategy.Decisions, d => d.Kind == "condition");
    }

    [Fact]
    public void Breakout_retest_places_protective_orders_and_is_deterministic()
    {
        StrategyDocument doc = Fixtures.Example("breakout-retest");
        (BacktestResult first, DocumentStrategy strategy) = Fixtures.RunBacktest(doc, bars: 4000);
        (BacktestResult second, _) = Fixtures.RunBacktest(doc, bars: 4000);

        Assert.True(first.TotalOrders > 0, "expected at least one entry");
        Assert.Contains(strategy.Decisions, d => d.Kind == "exit" && d.Message.StartsWith("protecting", StringComparison.Ordinal));
        Assert.Contains(strategy.Decisions, d => d.Kind == "transition");
        Assert.Equal(Fixtures.Fingerprint(first), Fixtures.Fingerprint(second));
    }

    [Fact]
    public void Parameter_overrides_change_the_outcome()
    {
        StrategyDocument doc = Fixtures.Example("ema-cross");
        (BacktestResult baseline, _) = Fixtures.RunBacktest(doc);
        (BacktestResult tweaked, _) = Fixtures.RunBacktest(doc, overrides: new Dictionary<string, decimal>(StringComparer.Ordinal) { ["fast"] = 5m, ["slow"] = 60m });

        Assert.NotEqual(Fixtures.Fingerprint(baseline), Fixtures.Fingerprint(tweaked));
    }

    [Fact]
    public void Fired_once_document_stops_after_the_first_position_closes()
    {
        StrategyDocument doc = Fixtures.Example("ema-cross") with { Repeat = new RepeatSettings { Enabled = false } };
        (BacktestResult result, DocumentStrategy strategy) = Fixtures.RunBacktest(doc);

        Assert.Equal("finished", strategy.StopReason);
        Assert.Equal(1, result.TotalPositions);
    }

    [Fact]
    public void Provider_creates_a_strategy_from_a_definition()
    {
        PluginRegistry registry = new();
        registry.AddPlugin(new DocumentsPlugin());
        string json = DocumentJson.Serialize(Fixtures.Example("ema-cross"));
        StrategyDefinition definition = new(DocumentStrategyProvider.ProviderId, "EMA cross", System.Text.Json.JsonDocument.Parse(json).RootElement.Clone(), new StrategyConfig { StrategyId = new StrategyId("Ema-777") });

        Strategy strategy = registry.CreateStrategy(definition);

        DocumentStrategy document = Assert.IsType<DocumentStrategy>(strategy);
        Assert.Equal("Ema-777", document.StrategyId.Value);
        Assert.Equal("EMA cross", document.Document.Name);
    }

    [Fact]
    public void Runtime_state_round_trips()
    {
        StrategyDocument doc = Fixtures.Example("breakout-retest");
        (_, DocumentStrategy strategy) = Fixtures.RunBacktest(doc, bars: 1500);
        IDictionary<string, byte[]> state = strategy.Save();
        Assert.True(state.ContainsKey("document"));
        string text = System.Text.Encoding.UTF8.GetString(state["document"]);
        Assert.Contains("\"index\":", text, StringComparison.Ordinal);
        Assert.Contains("\"phase\":", text, StringComparison.Ordinal);
    }
}
