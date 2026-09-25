using System.Text.Json;
using Bytex.Backtest;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Instruments;
using Bytex.Documents.Catalog;
using Bytex.Documents.Runtime;
using Bytex.Documents.Schema;
using Bytex.Documents.Validation;

namespace Bytex.Documents.Tests;

// Why: a descriptor is a promise - these ports, these parameters, this kind of value on each port - and the runtime is
// what keeps it. Testing the families by example leaves the promise unchecked for every type no example happens to
// use, and a type whose implementation sets a port the descriptor never declared, or declares one it never sets, is
// wrong in the builder, in the validator and in whatever an assistant tells someone about it. So every type in the
// catalog gets a document of its own here, built from its description alone, and has to survive a run.
public class EveryNodeTypeRunsTests
{
    private const int Bars = 400;

    public static TheoryData<string> EveryType()
    {
        TheoryData<string> data = new();
        foreach (NodeTypeDescriptor d in NodeCatalog.Default.Types.OrderBy(t => t.Type, StringComparer.Ordinal))
        {
            data.Add(d.Type);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryType))]
    public void A_document_holding_one_node_of_this_type_validates_and_runs(string type)
    {
        NodeTypeDescriptor descriptor = NodeCatalog.Default.Find(type)!;
        StrategyDocument document = DocumentFor(descriptor);

        // Built from the description alone, the document has to be one the validator accepts: a required input left
        // unwired or a parameter the description does not admit would be the generator's fault, and is worth knowing.
        ValidationReport report = new DocumentValidator().Validate(document);
        Assert.True(report.IsValid, $"{type}: {string.Join("; ", report.Findings.Select(f => f.ToString()))}");

        (BacktestResult result, DocumentStrategy strategy) = Fixtures.RunBacktest(document, bars: Bars);

        Assert.True(result.Iterations > 0, $"{type}: nothing was evaluated");

        // Every output the type declares was published on the last bar, except the ports named below with the reason
        // they are silent. An output the description promises and the node never sets is a port a builder would offer,
        // a validator would accept an edge from, and nothing would ever feed.
        IReadOnlyDictionary<string, object?> published = strategy.LastValues;
        foreach (PortSpec output in descriptor.Outputs)
        {
            string port = type + ":" + output.Name;
            bool wasPublished = published.ContainsKey(NodeId + ":" + output.Name);
            if (Silent.TryGetValue(port, out string? why))
            {
                Assert.False(wasPublished, $"{port} published a value although {why}");
                continue;
            }

            Assert.True(wasPublished, $"{type} declares the output '{output.Name}' and never published it");
        }
    }

    /// <summary>
    /// The ports that stay silent in a run of bars alone, and why. Each is a fact about the run rather than about the
    /// type: a backtest over bars carries no quotes, no trade ticks, no order book, no mark price, no funding and no
    /// annotations, and a price nobody pinned is not a price. Silence is the right answer for these, so the test holds
    /// them to it - a node that starts inventing a value without a source fails here too.
    /// </summary>
    private static readonly Dictionary<string, string> Silent = new(StringComparer.Ordinal)
    {
        ["data.position:unrealizedR"] = "R is measured from a stop, and nothing wired one",
        ["data.position:lastExitPrice"] = "the scaffold opens a position and never closes it, so there is no exit to report",
        ["data.position:lastExitBarsAgo"] = "the scaffold opens a position and never closes it, so there is no exit to report",
        ["data.quotes:bid"] = "a run of bars carries no quotes",
        ["data.quotes:ask"] = "a run of bars carries no quotes",
        ["data.quotes:mid"] = "a run of bars carries no quotes",
        ["data.quotes:spread"] = "a run of bars carries no quotes",
        ["data.trades:last"] = "a run of bars carries no trade ticks",
        ["data.trades:size"] = "a run of bars carries no trade ticks",
        ["data.book:bestBid"] = "a run of bars carries no order book",
        ["data.book:bestAsk"] = "a run of bars carries no order book",
        ["data.book:imbalance"] = "a run of bars carries no order book",
        ["data.markPrice:mark"] = "a backtest venue publishes no mark price",
        ["data.funding:rate"] = "a backtest venue publishes no funding rate",
        ["data.funding:hoursToNext"] = "a backtest venue publishes no funding rate",
        ["event.proximity:minutesToNext"] = "a run of bars carries no annotations, so no event is coming",
        ["risk.noEntryNear:minutesToNext"] = "a run of bars carries no annotations, so no event is coming",
        ["level.pinned:price"] = "the default price is zero, which means nothing was pinned",
        ["act.bracket:position"] = "a bracket publishes its position once its entry has filled",
        ["act.trail:stopPrice"] = "a trailing stop publishes its stop once it has a position to trail",
    };

    [Fact]
    public void A_quote_node_publishes_as_soon_as_the_run_carries_quotes()
    {
        // The same document that publishes nothing above, over data that has what it reads.
        StrategyDocument document = DocumentFor(NodeCatalog.Default.Find("data.quotes")!);

        (_, DocumentStrategy strategy) = Fixtures.RunWithTicks(document, bars: Bars);

        Assert.Equal(["bid", "ask", "mid", "spread"], NodeCatalog.Default.Find("data.quotes")!.Outputs.Select(o => o.Name));
        foreach (string port in new[] { "bid", "ask", "mid", "spread" })
        {
            Assert.True(strategy.LastValues.ContainsKey(NodeId + ":" + port), $"data.quotes published no {port} over data that has quotes");
        }
    }

    [Fact]
    public void Two_runs_of_one_document_do_the_same_thing()
    {
        StrategyDocument document = DocumentFor(NodeCatalog.Default.Find("act.order")!);

        (BacktestResult first, _) = Fixtures.RunBacktest(document, bars: Bars);
        (BacktestResult second, _) = Fixtures.RunBacktest(document, bars: Bars);

        Assert.Equal(Fixtures.Fingerprint(first), Fixtures.Fingerprint(second));
    }

    private const string NodeId = "subject";

    private const string EntryId = "entry";

    /// <summary>
    /// The smallest document that exercises one node type: a bar source, a moving average over it, a condition over
    /// that, an order the condition places - the smallest strategy the validator accepts - and the node under test,
    /// wired from whichever of those publishes the kind of value each of its required inputs needs. The node's
    /// parameters come from its own description, so a type that changes its ports or parameters changes what is built
    /// here without anyone editing this file.
    /// </summary>
    private static StrategyDocument DocumentFor(NodeTypeDescriptor descriptor)
    {
        bool subjectIsTheEntry = descriptor.Type is "act.order" or "act.bracket" or "act.ladder";
        List<NodeDef> nodes =
        [
            new NodeDef { Id = "bars", Type = "data.bars", Params = Json("""{ "barType": "main" }""") },
            new NodeDef { Id = "avg", Type = "ind.ema", Params = Json("""{ "period": 5 }""") },
            new NodeDef { Id = "gate", Type = "cond.compare", Params = Json("""{ "op": "gt", "value": "0" }""") },
        ];
        List<EdgeDef> edges =
        [
            new EdgeDef { From = "bars:bars", To = "avg:bars" },
            new EdgeDef { From = "avg:value", To = "gate:a" },
        ];

        if (!subjectIsTheEntry)
        {
            // Every document has to place an order somewhere, and a position gives the risk and action families
            // something to work on.
            nodes.Add(new NodeDef { Id = EntryId, Type = "act.order", Params = Json("""{ "side": "buy", "orderType": "market", "sizing": { "mode": "fixed", "value": "0.01" }, "onlyWhenFlat": true }""") });
            edges.Add(new EdgeDef { From = "gate:out", To = $"{EntryId}:trigger" });
        }

        nodes.Add(new NodeDef { Id = NodeId, Type = descriptor.Type, Params = ParamsFor(descriptor) });
        foreach (PortSpec input in descriptor.Inputs.Where(x => x.Required))
        {
            edges.Add(new EdgeDef { From = SourceFor(input.Kind, subjectIsTheEntry), To = $"{NodeId}:{input.Name}" });
        }

        return new StrategyDocument
        {
            Id = "one-" + descriptor.Type,
            Name = "One " + descriptor.DisplayName,
            Instruments = [new InstrumentRef { Ref = "primary", InstrumentId = Fixtures.BtcUsdt().Id.Value }],
            BarTypes = [new BarTypeRef { Ref = "main", Instrument = "primary", Step = 1, Aggregation = "minute" }],
            Nodes = nodes,
            Edges = edges,
        };
    }

    /// <summary>The port of the scaffold that publishes the kind of value a required input needs.</summary>
    private static string SourceFor(ValueKind kind, bool subjectIsTheEntry) => kind switch
    {
        ValueKind.Bars => "bars:bars",
        ValueKind.Series or ValueKind.Price or ValueKind.Quantity => "avg:value",
        ValueKind.Bool => "gate:out",

        // A pulse is not a condition: it is true for the one bar something happened, so it comes from the order.
        ValueKind.Pulse when !subjectIsTheEntry => EntryId + ":submitted",
        ValueKind.Position when !subjectIsTheEntry => EntryId + ":position",
        _ => throw new InvalidOperationException($"the scaffold publishes nothing of kind {kind}"),
    };

    /// <summary>Whatever the description says the node needs, taken from the description's own defaults.</summary>
    private static JsonElement? ParamsFor(NodeTypeDescriptor descriptor)
    {
        Dictionary<string, object?> values = new(StringComparer.Ordinal);
        foreach (ParamSpec spec in descriptor.Params)
        {
            object? value = ValueFor(spec);
            if (value is not null)
            {
                values[spec.Name] = value;
            }
        }

        return values.Count == 0 ? null : JsonSerializer.SerializeToElement(values, DocumentJson.Options);
    }

    private static object? ValueFor(ParamSpec spec)
    {
        if (spec.Type == ParamType.Object)
        {
            Dictionary<string, object?> nested = new(StringComparer.Ordinal);
            foreach (ParamSpec field in spec.Fields ?? [])
            {
                object? inner = ValueFor(field);
                if (inner is not null)
                {
                    nested[field.Name] = inner;
                }
            }

            return nested.Count == 0 ? null : nested;
        }

        return spec.Type switch
        {
            ParamType.BarType => "main",
            ParamType.Instrument => "primary",

            // A boolean parameter is a JSON boolean, not the word "false": the description carries its default as text.
            ParamType.Bool => spec.Default is null ? null : bool.Parse(spec.Default),
            _ => spec.Default,
        };
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
