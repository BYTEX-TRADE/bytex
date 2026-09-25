using Bytex.Backtest;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Documents.Catalog;
using Bytex.Documents.Runtime;
using Bytex.Documents.Schema;
using Bytex.Documents.Validation;
using Xunit;

namespace Bytex.Documents.Tests;

// Why: a node type is a description plus a factory, and nothing about the catalog is meant to be closed. Whoever adds
// a type - a plugin, a venue integration, a private strategy library - gets it drawn, validated, schema-checked and
// evaluated on the same terms as a built-in one. This test is that promise held: a type the engine has never heard of,
// registered from outside, and run to a fill.
public sealed class CustomNodeTypeTests
{
    private const string Type = "custom.aboveValue";

    /// <summary>True while its input is above a level it carries; the sort of thing a private library would add.</summary>
    private sealed class AboveValueNode : INodeEvaluator
    {
        private readonly decimal _level;

        public AboveValueNode(NodeBuildContext ctx) => _level = ctx.Params.Dec("level", 0m);

        public void Evaluate(EvalContext ctx)
        {
            decimal? value = ctx.Dec("value");
            ctx.Set("out", value is { } v && v > _level);
            ctx.Set("level", _level);
        }
    }

    private static NodeTypeDescriptor Descriptor(Func<NodeBuildContext, INodeEvaluator>? factory) => new()
    {
        Type = Type,
        Kind = NodeKind.Condition,
        DisplayName = "Above a fixed value",
        Description = "True while the input is above the level this node carries.",
        FaceTemplate = "{value} is above {level}",
        Inputs = [new PortSpec("value", ValueKind.Series, Required: true)],
        Outputs = [new PortSpec("out", ValueKind.Bool), new PortSpec("level", ValueKind.Series)],
        Params = [new ParamSpec { Name = "level", Type = ParamType.Decimal, Default = "0", Min = "0", Max = "1000000" }],
        Factory = factory,
    };

    private static NodeCatalog CatalogWith(NodeTypeDescriptor descriptor)
    {
        NodeCatalog catalog = new(NodeCatalog.Default.Types);
        catalog.Register(descriptor);
        return catalog;
    }

    // A document that reads bars, feeds the close into the custom condition, and buys while it is true.
    private static StrategyDocument Document() => DocumentJson.Deserialize($$"""
        {
          "schemaVersion": "1.0",
          "id": "custom-node",
          "name": "Custom node",
          "instruments": [ { "ref": "primary", "instrumentId": "BTCUSDT.SIM" } ],
          "barTypes": [ { "ref": "main", "instrument": "primary", "step": 1, "aggregation": "minute", "priceType": "last", "source": "external" } ],
          "nodes": [
            { "id": "bars", "type": "data.bars", "params": { "barType": "main" } },
            { "id": "close", "type": "ind.sma", "params": { "period": 1 } },
            { "id": "above", "type": "{{Type}}", "params": { "level": "1" } },
            { "id": "buy", "type": "act.order", "params": { "side": "buy", "orderType": "market", "sizing": { "mode": "fixed", "value": "0.01" }, "onlyWhenFlat": true } }
          ],
          "edges": [
            { "from": "bars:bars", "to": "close:bars" },
            { "from": "close:value", "to": "above:value" },
            { "from": "above:out", "to": "buy:trigger" }
          ]
        }
        """);

    [Fact]
    public void A_type_the_engine_does_not_know_validates_once_the_catalog_carries_it()
    {
        StrategyDocument document = Document();

        ValidationReport without = new DocumentValidator().Validate(document);
        ValidationReport with = new DocumentValidator(CatalogWith(Descriptor(ctx => new AboveValueNode(ctx)))).Validate(document);

        Assert.Contains(without.Findings, f => f.Code == Codes.NodeTypeUnknown);
        Assert.True(with.IsValid, string.Join("; ", with.Findings.Select(f => f.Code + " " + f.Message)));
    }

    [Fact]
    public void A_described_type_without_a_factory_is_named_as_one_this_engine_cannot_run()
    {
        // A descriptor a builder can draw but this process cannot build: the validator says so before a run starts,
        // rather than the runtime failing on the first bar.
        ValidationReport report = new DocumentValidator(CatalogWith(Descriptor(factory: null))).Validate(Document());

        Assert.False(report.IsValid);
        Assert.Contains(report.Findings, f => f.Code == Codes.NodeNotRunnable && f.NodeId == "above");
    }

    [Fact]
    public void A_custom_node_is_evaluated_like_any_other_and_its_outputs_reach_the_frame()
    {
        NodeCatalog catalog = CatalogWith(Descriptor(ctx => new AboveValueNode(ctx)));
        StrategyDocument document = Document();

        (BacktestResult result, DocumentStrategy strategy) = Run(document, catalog);

        Assert.True(result.TotalOrders > 0, "the custom condition never fired");
        Assert.Equal(true, strategy.LastValues["above:out"]);
        Assert.Equal(1m, strategy.LastValues["above:level"]);
        Assert.Contains(strategy.Decisions, d => d.Kind == "order" && d.NodeId == "buy");
    }

    [Fact]
    public void A_custom_type_is_in_the_catalog_export_and_in_the_schema_that_validates_a_document()
    {
        NodeCatalog catalog = CatalogWith(Descriptor(ctx => new AboveValueNode(ctx)));

        Assert.Contains(Type, catalog.ExportJson());
        Assert.Contains(Type, DocumentSchemaExporter.ExportJson(catalog));
        Assert.DoesNotContain(Type, DocumentSchemaExporter.ExportJson());
    }

    [Fact]
    public void One_type_name_cannot_be_registered_twice()
    {
        NodeCatalog catalog = CatalogWith(Descriptor(ctx => new AboveValueNode(ctx)));

        Assert.Throws<InvalidOperationException>(() => catalog.Register(Descriptor(ctx => new AboveValueNode(ctx))));
    }

    private static (BacktestResult Result, DocumentStrategy Strategy) Run(StrategyDocument document, NodeCatalog catalog)
    {
        Instrument instrument = Fixtures.BtcUsdt();
        BarType barType = Fixtures.MinuteBars(instrument);
        using BacktestEngine engine = new(new BacktestEngineConfig { RunId = "custom-node" });
        engine.AddInstrument(instrument);
        engine.AddVenue(new SimulatedVenueConfig
        {
            Venue = new Venue("SIM"),
            AccountType = AccountType.Cash,
            StartingBalances = [new Money(1_000_000m, Currencies.USDT), new Money(10m, Currencies.BTC)],
        });
        engine.AddData(Fixtures.RandomWalkBars(instrument, barType, 200).Cast<IData>());
        DocumentStrategy strategy = new(new DocumentStrategyConfig { Document = document, StrategyId = new StrategyId("Doc-001") }, catalog);
        engine.AddStrategy(strategy);
        engine.Run();
        return (engine.GetResult(), strategy);
    }
}
