using Bytex.Backtest;
using Bytex.Core.Model.Data;
using Bytex.Documents.Catalog;
using Bytex.Documents.Runtime;
using Bytex.Documents.Schema;
using Bytex.Documents.Validation;
using Bytex.Examples.Nodes;

namespace Bytex.Documents.Tests;

// Why (R13.8): PluginNodeTypeTests pins the composition rules with a node that does nothing. This runs the worked
// example instead - the plugin that ships in the repository, the document that ships beside it - through the same
// backtest path a real one takes, so the example people copy is proven rather than merely present.
//
// The stateful node is the half worth the trouble: a latch that forgets it has fired re-fires on the first bar after a
// restart, and the strategy acts twice. That is asserted here, not described.
public sealed class ExamplePluginNodeTests
{
    private const string OnceNodeId = "firstToday";

    private static NodeCatalog Catalog() => NodeCatalogComposer.Compose([new ExampleNodesPlugin()]);

    private static StrategyDocument Document() => DocumentJson.Deserialize(ExampleNodesPlugin.ExampleDocumentJson());

    [Fact]
    public void The_example_document_says_what_it_needs_and_survives_a_round_trip()
    {
        StrategyDocument document = Document();

        RequirementDef requirement = Assert.Single(document.Requires);
        Assert.Equal(ExampleNodesPlugin.Prefix, requirement.Prefix);
        Assert.Equal("Bytex.Examples.Nodes", requirement.Package);

        StrategyDocument again = DocumentJson.Deserialize(DocumentJson.Serialize(document));

        Assert.Equal(document.Requires, again.Requires);
    }

    [Fact]
    public void The_example_document_is_valid_where_the_plugin_is_loaded()
    {
        ValidationReport report = new DocumentValidator(Catalog()).Validate(Document());

        Assert.True(report.IsValid, string.Join("; ", report.Findings.Select(f => f.Code + " " + f.Message)));
    }

    [Fact]
    public void Without_the_plugin_the_same_document_is_refused_by_naming_the_package()
    {
        ValidationReport report = new DocumentValidator(NodeCatalog.Default).Validate(Document());

        Assert.Contains(report.Blocks, b => b.Code == Codes.NodeProviderMissing
            && b.Message.Contains("Bytex.Examples.Nodes (example) 1.0 or later", StringComparison.Ordinal));
    }

    [Fact]
    public void The_plugins_nodes_are_evaluated_and_the_document_reaches_a_fill()
    {
        // One day of bars, so the stateful node may pass exactly one bar: the entry count is exact rather than
        // approximate. The protective stop and target are orders too, which is why this counts the entry node's own
        // decisions rather than every order the run placed.
        (BacktestResult result, DocumentStrategy strategy) = Fixtures.RunBacktest(Document(), bars: 200, catalog: Catalog());

        Assert.Single(strategy.Decisions, d => d.Kind == "order" && d.NodeId == "buy");
        Assert.Single(strategy.Decisions, d => d.Kind == "fired" && d.NodeId == OnceNodeId);
        Assert.NotEmpty(result.Fills);

        // And the number it published is the one the node is documented to compute, off the last bar the run saw.
        Bar last = Fixtures.RandomWalkBars(Fixtures.BtcUsdt(), Fixtures.MinuteBars(Fixtures.BtcUsdt()), 200)[^1];
        decimal expected = (last.High.Value - last.Low.Value) / last.Close.Value;

        Assert.Equal(expected, Assert.IsType<decimal>(strategy.LastValues["range:value"]));
    }

    [Fact]
    public void The_stateful_node_fires_at_most_once_a_day()
    {
        // Fifty hours of bars, so the day rolls over twice while the strategy runs.
        (_, DocumentStrategy strategy) = Fixtures.RunBacktest(Document(), bars: 3000, catalog: Catalog());

        DateOnly[] days = [.. strategy.Decisions
            .Where(d => d.Kind == "fired" && d.NodeId == OnceNodeId)
            .Select(d => DateOnly.FromDateTime(d.EventTime.ToDateTimeUtc()))];

        Assert.True(days.Length > 1, "the node never saw a second day");
        Assert.Equal(days.Length, days.Distinct().Count());
    }

    [Fact]
    public void A_restart_inside_the_same_day_does_not_let_it_fire_again()
    {
        (BacktestResult first, DocumentStrategy before) = Fixtures.RunBacktest(Document(), bars: 200, catalog: Catalog());
        IDictionary<string, byte[]> state = before.Save();

        // The fresh run is the control: on this data the node does fire, so what stops it below is the state.
        Assert.Contains(before.Decisions, d => d.Kind == "fired" && d.NodeId == OnceNodeId);
        Assert.NotEmpty(first.Fills);
        Assert.Contains("2025-01-01", System.Text.Encoding.UTF8.GetString(state["document"]), StringComparison.Ordinal);

        DocumentStrategy after = Fixtures.RunWithSavedState(Document(), state, bars: 200, catalog: Catalog());

        Assert.DoesNotContain(after.Decisions, d => d.Kind == "fired" && d.NodeId == OnceNodeId);
        Assert.DoesNotContain(after.Decisions, d => d.Kind == "order" && d.NodeId == "buy");
    }

    [Fact]
    public void The_example_documents_own_schema_accepts_it()
    {
        // The requires block is part of the document, so the schema a builder validates against has to allow it.
        System.Text.Json.Nodes.JsonObject schema = DocumentSchemaExporter.Export(Catalog());

        System.Text.Json.Nodes.JsonObject requires = schema["properties"]!["requires"]!.AsObject();

        Assert.Equal("array", requires["type"]!.GetValue<string>());
        Assert.Contains("prefix", requires["items"]!["properties"]!.AsObject().Select(p => p.Key));
    }
}
