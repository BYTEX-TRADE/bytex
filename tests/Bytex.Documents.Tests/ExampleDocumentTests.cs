using Bytex.Backtest;
using Bytex.Documents.Runtime;
using Bytex.Documents.Schema;
using Bytex.Documents.Validation;
using Xunit;

namespace Bytex.Documents.Tests;

// Why: the examples are what a reader runs first and what a builder starts from, so they are held to the same bar as
// any other document - they validate with no findings, they survive a run, and the graph they describe is the graph
// the runtime evaluates. An example that stopped validating, or stopped trading, would otherwise be found by whoever
// tried it rather than here.
public sealed class ExampleDocumentTests
{
    private static readonly string[] _names = ["ema-cross", "breakout-retest", "support-bounce"];

    [Theory]
    [InlineData("ema-cross")]
    [InlineData("breakout-retest")]
    [InlineData("support-bounce")]
    public void An_example_validates_with_nothing_to_report(string name)
    {
        ValidationReport report = new DocumentValidator().Validate(Fixtures.Example(name));

        Assert.True(report.IsValid, string.Join("; ", report.Findings.Select(f => f.Code + " " + f.Message)));
        Assert.DoesNotContain(report.Findings, f => f.Level == FindingLevel.Warning);
    }

    [Theory]
    [InlineData("ema-cross")]
    [InlineData("breakout-retest")]
    [InlineData("support-bounce")]
    public void An_example_runs_over_bars_twice_to_the_same_result(string name)
    {
        StrategyDocument doc = Fixtures.Example(name);

        (BacktestResult first, DocumentStrategy strategy) = Fixtures.RunBacktest(doc, bars: 4000);
        (BacktestResult second, _) = Fixtures.RunBacktest(doc, bars: 4000);

        Assert.True(first.Iterations > 0, "the run read no data");
        // Determinism is the engine's promise, and a document is a strategy like any other: the same document over the
        // same bars places the same orders, takes the same fills and ends on the same equity.
        Assert.Equal(Fixtures.Fingerprint(first), Fixtures.Fingerprint(second));
        // Every node that belongs to no phase was evaluated on the last bar, not just the ones on the path to an
        // order. A node inside a phase is only evaluated while that phase is active - which is what makes a phase a
        // phase - so the last frame carries the active phase's nodes and none of the others.
        HashSet<string> phased = doc.Phases.SelectMany(p => p.Nodes).ToHashSet(StringComparer.Ordinal);
        IEnumerable<string> published = strategy.LastValues.Keys.Select(k => k.Split(':')[0]).Distinct();
        Assert.Empty(doc.Nodes.Select(n => n.Id).Where(id => !phased.Contains(id)).Except(published, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("ema-cross")]
    [InlineData("breakout-retest")]
    [InlineData("support-bounce")]
    public void An_example_round_trips_through_the_document_json(string name)
    {
        StrategyDocument doc = Fixtures.Example(name);

        StrategyDocument back = DocumentJson.Deserialize(DocumentJson.Serialize(doc));

        Assert.Equal(doc.Id, back.Id);
        Assert.Equal(doc.Nodes.Select(n => n.Id + " " + n.Type), back.Nodes.Select(n => n.Id + " " + n.Type));
        Assert.Equal(doc.Edges.Select(e => e.From + "->" + e.To), back.Edges.Select(e => e.From + "->" + e.To));
        Assert.Equal(DocumentJson.Serialize(doc), DocumentJson.Serialize(back));
    }

    [Fact]
    public void The_examples_the_cli_writes_are_the_three_the_tests_run()
    {
        // The command writes whatever is embedded, so a fourth example added to the project without a test here would
        // ship untested.
        IEnumerable<string> embedded = typeof(DocumentStrategy).Assembly
            .GetManifestResourceNames()
            .Where(n => n.StartsWith("Bytex.Documents.Examples.", StringComparison.Ordinal))
            .Select(n => n["Bytex.Documents.Examples.".Length..].Replace(".json", string.Empty, StringComparison.Ordinal));

        Assert.Equal(_names.Order(), embedded.Order());
    }
}
