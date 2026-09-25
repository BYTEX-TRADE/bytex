using System.Globalization;
using System.Text.Json;
using Bytex.Backtest;
using Bytex.Core.Model.Primitives;
using Bytex.Documents.Runtime;
using Bytex.Documents.Schema;
using Bytex.Documents.Validation;
using Xunit;

namespace Bytex.Documents.Tests;

// Why: a strategy that wants to act three percent above a level had no way to say so - the catalog could measure the
// distance between two series but could not compute a price. The arithmetic itself is trivial; what is worth pinning
// is what the node does when it has nothing to compute from, because a value computed from a missing input would look
// exactly like a real one and would be traded on.
public sealed class MathNodeTests
{
    private const string Subject = "math";

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>
    /// The smallest strategy the validator accepts - bars, an average, a condition, an order - with the arithmetic
    /// node fed by one pinned price, and by a second one only when the test wires it.
    /// </summary>
    private static StrategyDocument Document(string op, decimal a, decimal? b, decimal constant)
    {
        string number(decimal d) => d.ToString(CultureInfo.InvariantCulture);
        List<NodeDef> nodes =
        [
            new NodeDef { Id = "bars", Type = "data.bars", Params = Json("""{ "barType": "main" }""") },
            new NodeDef { Id = "avg", Type = "ind.ema", Params = Json("""{ "period": 5 }""") },
            new NodeDef { Id = "gate", Type = "cond.compare", Params = Json("""{ "op": "gt", "value": "0" }""") },
            new NodeDef { Id = "entry", Type = "act.order", Params = Json("""{ "side": "buy", "orderType": "market", "sizing": { "mode": "fixed", "value": "0.01" }, "onlyWhenFlat": true }""") },
            new NodeDef { Id = "left", Type = "level.pinned", Params = Json($$"""{ "price": "{{number(a)}}" }""") },
            new NodeDef { Id = Subject, Type = "ind.math", Params = Json($$"""{ "op": "{{op}}", "value": "{{number(constant)}}" }""") },
        ];
        List<EdgeDef> edges =
        [
            new EdgeDef { From = "bars:bars", To = "avg:bars" },
            new EdgeDef { From = "avg:value", To = "gate:a" },
            new EdgeDef { From = "gate:out", To = "entry:trigger" },
            new EdgeDef { From = "left:price", To = Subject + ":a" },
        ];

        if (b is { } other)
        {
            nodes.Add(new NodeDef { Id = "right", Type = "level.pinned", Params = Json($$"""{ "price": "{{number(other)}}" }""") });
            edges.Add(new EdgeDef { From = "right:price", To = Subject + ":b" });
        }

        return new StrategyDocument
        {
            Id = "arithmetic",
            Name = "Arithmetic on pinned prices",
            Instruments = [new InstrumentRef { Ref = "primary", InstrumentId = Fixtures.BtcUsdt().Id.Value }],
            BarTypes = [new BarTypeRef { Ref = "main", Instrument = "primary", Step = 1, Aggregation = "minute" }],
            Nodes = nodes,
            Edges = edges,
        };
    }

    private const int Bars = 50;

    private static (decimal? Value, BacktestResult Result, DocumentStrategy Strategy) Run(string op, decimal a, decimal? b = null, decimal constant = 1m)
    {
        StrategyDocument document = Document(op, a, b, constant);
        ValidationReport report = new DocumentValidator().Validate(document);
        Assert.True(report.IsValid, string.Join("; ", report.Findings.Select(f => f.Code + " " + f.Message)));

        (BacktestResult result, DocumentStrategy strategy) = Fixtures.RunBacktest(document, bars: Bars);

        decimal? value = strategy.LastValues.TryGetValue(Subject + ":value", out object? published)
            ? published switch { Price price => price.Value, decimal d => d, _ => null }
            : null;
        return (value, result, strategy);
    }

    private static decimal? Value(string op, decimal a, decimal? b = null, decimal constant = 1m) => Run(op, a, b, constant).Value;

    [Theory]
    [InlineData("add", 100, 25, 125)]
    [InlineData("subtract", 100, 25, 75)]
    [InlineData("multiply", 100, 25, 2500)]
    [InlineData("divide", 100, 25, 4)]
    public void Two_inputs_are_added_subtracted_multiplied_or_divided(string op, decimal a, decimal b, decimal expected)
        => Assert.Equal(expected, Value(op, a, b));

    // The case asked for in words: three percent above a level, as a price to compare against.
    [Theory]
    [InlineData("multiply", 1.03, 103)]
    [InlineData("add", 3, 103)]
    [InlineData("subtract", 3, 97)]
    [InlineData("divide", 4, 25)]
    public void The_constant_stands_in_while_the_second_input_is_unconnected(string op, decimal constant, decimal expected)
        => Assert.Equal(expected, Value(op, 100m, b: null, constant: constant));

    [Fact]
    public void A_divisor_of_zero_publishes_nothing_and_leaves_the_run_standing()
    {
        // Written into the document as the constant.
        (decimal? value, BacktestResult result, DocumentStrategy strategy) = Run("divide", 100m, b: null, constant: 0m);

        Assert.Null(value);

        // An unguarded divide throws, and a node that throws takes its strategy out of the run, so the bars after it
        // are never evaluated. The count says the strategy was still working on the last bar.
        Assert.Equal(Bars, (int)result.Iterations);

        // And it says so rather than going quiet, on every bar it cannot divide: a node cannot tell a warm-up bar
        // from a live one, and a report made during warm-up is thrown away.
        Assert.Contains(strategy.Decisions, d => d.Kind == "math" && d.NodeId == Subject
            && d.Message.Contains("divisor is zero", StringComparison.Ordinal));

        // The same when the zero arrives on the second input: the distance from a series to itself is zero on every
        // bar, which is the shape this happens in for real.
        StrategyDocument document = Document("divide", 100m, null, 1m);
        document = document with
        {
            Nodes = [.. document.Nodes, new NodeDef { Id = "zero", Type = "ind.distance", Params = Json("""{ "unit": "absolute" }""") }],
            Edges =
            [
                .. document.Edges,
                new EdgeDef { From = "left:price", To = "zero:value" },
                new EdgeDef { From = "left:price", To = "zero:reference" },
                new EdgeDef { From = "zero:value", To = Subject + ":b" },
            ],
        };
        Assert.True(new DocumentValidator().Validate(document).IsValid);

        (BacktestResult second, DocumentStrategy fromASeries) = Fixtures.RunBacktest(document, bars: Bars);

        Assert.False(fromASeries.LastValues.ContainsKey(Subject + ":value"));
        Assert.Equal(Bars, (int)second.Iterations);
        Assert.Contains(fromASeries.Decisions, d => d.Kind == "math" && d.NodeId == Subject);

        // The guard disturbs nothing else: the same graph divides when the divisor is not zero.
        Assert.Equal(50m, Value("divide", 100m, 2m));
    }

    [Fact]
    public void Multiplying_by_the_default_constant_leaves_the_value_as_it_is()
        => Assert.Equal(1234.5m, Value("multiply", 1234.5m));

    [Fact]
    public void Nothing_is_published_while_the_second_input_has_no_value_yet()
    {
        // A second input that is wired is waited for, not read as zero: multiplying by a zero that only means "not
        // warmed up yet" would publish 0 on every bar of the warm-up, and a stop placed there would be at zero.
        StrategyDocument document = Document("multiply", 100m, 2m, 1m);
        document = document with
        {
            Nodes = [.. document.Nodes.Where(n => n.Id != "right"), new NodeDef { Id = "slow", Type = "ind.ema", Params = Json("""{ "period": 500 }""") }],
            Edges =
            [
                .. document.Edges.Where(e => e.To != Subject + ":b"),
                new EdgeDef { From = "bars:bars", To = "slow:bars" },
                new EdgeDef { From = "slow:value", To = Subject + ":b" },
            ],
        };

        ValidationReport report = new DocumentValidator().Validate(document);
        Assert.True(report.IsValid, string.Join("; ", report.Findings.Select(f => f.Code + " " + f.Message)));

        (_, DocumentStrategy strategy) = Fixtures.RunBacktest(document, bars: 20);

        Assert.False(strategy.LastValues.ContainsKey(Subject + ":value"));
    }

    [Fact]
    public void Nothing_is_published_while_the_input_has_no_value_yet()
    {
        // level.pinned publishes on every bar, so the only way to leave the input empty is a series that has not
        // warmed up: an average over a period longer than the run. A node that read the missing value as zero would
        // publish 0 x 1.03 here, and a comparison against it would fire on the first bar.
        StrategyDocument document = Document("multiply", 100m, null, 1.03m);
        document = document with
        {
            Nodes = [.. document.Nodes.Where(n => n.Id != "left"), new NodeDef { Id = "slow", Type = "ind.ema", Params = Json("""{ "period": 500 }""") }],
            Edges =
            [
                .. document.Edges.Where(e => e.To != Subject + ":a"),
                new EdgeDef { From = "bars:bars", To = "slow:bars" },
                new EdgeDef { From = "slow:value", To = Subject + ":a" },
            ],
        };

        ValidationReport report = new DocumentValidator().Validate(document);
        Assert.True(report.IsValid, string.Join("; ", report.Findings.Select(f => f.Code + " " + f.Message)));

        (_, DocumentStrategy strategy) = Fixtures.RunBacktest(document, bars: 20);

        Assert.False(strategy.LastValues.ContainsKey(Subject + ":value"));
    }
}
