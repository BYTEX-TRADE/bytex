using System.Text.Json;
using Bytex.Documents.Catalog;
using Bytex.Documents.Runtime;
using Bytex.Documents.Schema;
using Bytex.Documents.Validation;

namespace Bytex.Documents.Tests;

// Why (R13.8): the runtime contract for a node type has been public since 0.2 and there was no way for a plugin to
// supply one - a host had to build a catalog itself, which the CLI did not do. So the contract was real and unreachable.
//
// What is pinned here is the composition and the rules that make a document mean the same thing everywhere: a plugin's
// types carry its own prefix, a plugin cannot claim one of the engine's, two plugins cannot claim the same one, and a
// document that names a type the engine has not loaded is refused with a sentence naming what is missing rather than
// with a type id nobody recognises.
public class PluginNodeTypeTests
{
    private sealed class Provider : INodeTypeProvider
    {
        private readonly NodeTypeDescriptor[] _types;

        public Provider(string prefix, params string[] types)
        {
            TypePrefix = prefix;
            _types = [.. types.Select(Descriptor)];
        }

        public string TypePrefix { get; }

        public IEnumerable<NodeTypeDescriptor> NodeTypes() => _types;

        private static NodeTypeDescriptor Descriptor(string type) => new()
        {
            Type = type,
            Kind = NodeKind.Custom,
            DisplayName = type,
            FaceTemplate = type,
            Outputs = [new PortSpec("value", ValueKind.Series), new PortSpec("out", ValueKind.Bool)],
            Factory = _ => new Nothing(),
        };
    }

    private sealed class Nothing : INodeEvaluator
    {
        public void Evaluate(EvalContext ctx) => ctx.Set("value", 0m);
    }

    [Fact]
    public void A_composed_catalog_has_the_built_ins_and_the_plugins_types()
    {
        NodeCatalog catalog = NodeCatalogComposer.Compose([new Provider("acme", "acme.squeeze", "acme.drift")]);

        Assert.NotNull(catalog.Find("acme.squeeze"));
        Assert.NotNull(catalog.Find("acme.drift"));
        Assert.NotNull(catalog.Find("ind.rsi"));
        Assert.Equal(NodeCatalog.Default.Types.Count + 2, catalog.Types.Count);
    }

    [Fact]
    public void A_plugin_cannot_redefine_a_built_in_node()
    {
        // The reason the prefix rule exists: a plugin registering act.market would change what every document that has
        // ever placed a market order means - on the machines that loaded it, and only there.
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => NodeCatalogComposer.Compose([new Provider("act", "act.market")]));

        Assert.Contains("one of the engine's own prefixes", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_type_outside_its_providers_prefix_is_refused()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => NodeCatalogComposer.Compose([new Provider("acme", "other.squeeze")]));

        Assert.Contains("does not begin with the prefix", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_plugins_cannot_claim_one_prefix()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => NodeCatalogComposer.Compose([new Provider("acme", "acme.a"), new Provider("acme", "acme.b")]));

        Assert.Contains("claim the prefix", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_prefix_with_a_dot_in_it_is_not_a_prefix()
    {
        Assert.Throws<InvalidOperationException>(() => NodeCatalogComposer.Compose([new Provider("acme.nodes", "acme.nodes.a")]));
    }

    [Fact]
    public void A_catalog_composed_from_nothing_is_the_built_in_one()
    {
        NodeCatalog catalog = NodeCatalogComposer.Compose([]);

        Assert.Equal(NodeCatalog.Default.Types.Count, catalog.Types.Count);
    }

    [Fact]
    public void A_plugins_type_is_exported_to_the_palette_like_any_other()
    {
        // The payoff: a builder shows a plugin's nodes without knowing the plugin exists.
        NodeCatalog catalog = NodeCatalogComposer.Compose([new Provider("acme", "acme.squeeze")]);

        using JsonDocument exported = JsonDocument.Parse(catalog.ExportJson());

        Assert.Contains(
            exported.RootElement.GetProperty("types").EnumerateArray(),
            t => t.GetProperty("type").GetString() == "acme.squeeze");
    }

    // ----- what a document says it needs -----

    private static StrategyDocument Document(IReadOnlyList<RequirementDef> requires) => new()
    {
        Name = "needs a plugin",
        Instruments = [new InstrumentRef { Ref = "i", MarketKey = "bx-market:v2/BINANCE/BTCUSDT" }],
        CandleSeriesDefinitions = [new CandleSeriesRef { Ref = "b", Instrument = "i", Step = 1, Aggregation = "minute", PriceType = "last", Source = "computed" }],
        Requires = requires,
        Nodes =
        [
            new NodeDef { Id = "bars", Type = "data.bars", Params = JsonSerializer.SerializeToElement(new { candleSeries = "b" }) },
            new NodeDef { Id = "custom", Type = "acme.squeeze", Params = JsonSerializer.SerializeToElement(new { }) },
            new NodeDef { Id = "buy", Type = "act.order", Params = JsonSerializer.SerializeToElement(new { side = "buy", orderType = "market", sizing = new { mode = "fixed", value = "0.01" }, onlyWhenFlat = true }) },
        ],
        Edges =
        [
            new EdgeDef { From = "bars:bars", To = "custom:bars" },
            new EdgeDef { From = "custom:out", To = "buy:trigger" },
        ],
    };

    [Fact]
    public void A_document_needing_a_plugin_that_is_not_loaded_says_which_plugin()
    {
        // Rather than "unknown type 'acme.squeeze'", which a person can only report.
        ValidationReport report = new DocumentValidator(NodeCatalog.Default)
            .Validate(Document([new RequirementDef("acme", "Acme Nodes", "1.2")]));

        Finding finding = Assert.Single(report.Blocks, b => b.Code == Codes.NodeProviderMissing);

        Assert.Contains("Acme Nodes (acme) 1.2 or later", finding.Message, StringComparison.Ordinal);
        Assert.Contains("not loaded", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_type_nobody_declared_is_still_an_unknown_type()
    {
        ValidationReport report = new DocumentValidator(NodeCatalog.Default).Validate(Document([]));

        Finding finding = Assert.Single(report.Blocks, b => b.Code == Codes.NodeTypeUnknown);

        Assert.Contains("unknown type 'acme.squeeze'", finding.Message, StringComparison.Ordinal);
        Assert.Contains("declare it under 'requires'", finding.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_document_validates_where_the_plugin_is_loaded()
    {
        NodeCatalog catalog = NodeCatalogComposer.Compose([new Provider("acme", "acme.squeeze")]);

        ValidationReport report = new DocumentValidator(catalog).Validate(Document([new RequirementDef("acme", "Acme Nodes", "1.2")]));

        Assert.DoesNotContain(report.Blocks, b => b.Code is Codes.NodeTypeUnknown or Codes.NodeProviderMissing);
    }

    [Fact]
    public void A_missing_plugin_is_one_finding_per_node_and_not_one_for_every_edge_that_touched_it()
    {
        // What a person sees decides whether they can act. An unloaded plugin used to report the two nodes it supplies,
        // then every edge that touched them as "a node that does not exist", then the order node as having nothing
        // wired to its trigger: six blocking findings of which two were true, and four that send somebody hunting for a
        // typo in an id that is correct.
        ValidationReport report = new DocumentValidator(NodeCatalog.Default)
            .Validate(Document([new RequirementDef("acme", "Acme Nodes", "1.2")]));

        Assert.Single(report.Blocks);
        Assert.DoesNotContain(report.Findings, f => f.Code is Codes.EdgeDangling or Codes.InputRequired);
    }

    [Fact]
    public void An_edge_to_a_node_that_really_is_not_there_is_still_refused()
    {
        // The other half of the same rule: what was silenced is the edge to a node the document does have.
        StrategyDocument document = Document([new RequirementDef("acme")]);
        document = document with { Edges = [.. document.Edges, new EdgeDef { From = "bars:bars", To = "ghost:in" }] };

        ValidationReport report = new DocumentValidator(NodeCatalog.Default).Validate(document);

        Assert.Contains(report.Blocks, b => b.Code == Codes.EdgeDangling && b.Message.Contains("ghost:in", StringComparison.Ordinal));
    }

    [Fact]
    public void A_requirement_matches_a_whole_prefix_and_not_the_start_of_one()
    {
        // 'ex' is not 'acme' and it is not 'example' either: a requirement that matched by the start of a string would
        // claim to explain a type it knows nothing about, and the message would name the wrong package.
        ValidationReport report = new DocumentValidator(NodeCatalog.Default).Validate(Document([new RequirementDef("ac", "Not This One")]));

        Assert.Contains(report.Blocks, b => b.Code == Codes.NodeTypeUnknown);
        Assert.DoesNotContain(report.Blocks, b => b.Code == Codes.NodeProviderMissing);
    }

    [Fact]
    public void A_requirement_that_is_not_a_prefix_is_refused()
    {
        ValidationReport report = new DocumentValidator(NodeCatalog.Default)
            .Validate(Document([new RequirementDef("acme.nodes")]));

        Assert.Contains(report.Blocks, b => b.Code == Codes.NodeProviderUndeclared);
    }
}
