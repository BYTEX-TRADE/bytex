using System.Text.Json;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Documents.Catalog;
using Bytex.Documents.Schema;
using Bytex.Documents.Validation;
using Xunit;

namespace Bytex.Documents.Tests;

// Why: the validator is the only thing that tells somebody their strategy will not work before they run it, and each
// reason it can give is a promise to explain one mistake. Counting the codes against the tests found six of the
// forty-six with nothing exercising them - and then found that one of the six could never be given at all.
//
// A reason nobody tests is a reason nobody can rely on, and a reason nobody can reach is worse: a host renders
// these codes, so a dead one is a branch in somebody's interface that no document will ever take.
public sealed class ValidatorCodeCoverageTests
{
    private sealed class Catalogued : IValidationContext
    {
        private readonly Instrument _instrument = Spot();
        private readonly (UnixNanos Start, UnixNanos End)? _range;

        public Catalogued(TradingEnvironment target = TradingEnvironment.Backtest, (UnixNanos Start, UnixNanos End)? range = null)
        {
            TargetEnvironment = target;
            _range = range;
        }

        public TradingEnvironment TargetEnvironment { get; }

        public bool ProvidesInstruments => true;

        public Instrument? Instrument(InstrumentId id) => id == _instrument.Id ? _instrument : null;

        public (UnixNanos Start, UnixNanos End)? DataRange(BarType barType) => _range;
    }

    private static CurrencyPair Spot() => new(new InstrumentSpec
    {
        Id = InstrumentId.Parse("BTCUSDT.SIM"),
        AssetClass = AssetClass.Crypto,
        InstrumentClass = InstrumentClass.Spot,
        QuoteCurrency = Currencies.USDT,
        BaseCurrency = Currencies.BTC,
        PricePrecision = 2,
        SizePrecision = 5,
        PriceIncrement = new Price(0.01m, 2),
        SizeIncrement = new Quantity(0.00001m, 5),
    });

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static StrategyDocument Sound() => new()
    {
        Id = "codes",
        Name = "Codes",
        Instruments = [new InstrumentRef { Ref = "primary", InstrumentId = "BTCUSDT.SIM" }],
        BarTypes = [new BarTypeRef { Ref = "main", Instrument = "primary", Step = 1, Aggregation = "minute" }],
        Nodes =
        [
            new NodeDef { Id = "bars", Type = "data.bars", Params = Json("""{ "barType": "main" }""") },
            new NodeDef { Id = "avg", Type = "ind.ema", Params = Json("""{ "period": 10 }""") },
            new NodeDef { Id = "above", Type = "cond.compare", Params = Json("""{ "op": "gt", "value": "100" }""") },
            new NodeDef { Id = "buy", Type = "act.order", Params = Json("""{ "side": "buy", "orderType": "market", "sizing": { "mode": "fixed", "value": "1" } }""") },
        ],
        Edges =
        [
            new EdgeDef { From = "bars:bars", To = "avg:bars" },
            new EdgeDef { From = "avg:value", To = "above:a" },
            new EdgeDef { From = "above:out", To = "buy:trigger" },
        ],
    };

    [Fact]
    public void A_condition_that_can_never_be_true_is_refused()
    {
        // Above 200 and below 100 at once: the strategy would validate, run, and never place an order, which is the
        // hardest kind of mistake to see in a picture of a graph.
        StrategyDocument document = Sound() with
        {
            Nodes =
            [
                .. Sound().Nodes.Select(n => n.Id == "above" ? n with { Params = Json("""{ "op": "gt", "value": "200" }""") } : n),
                new NodeDef { Id = "below", Type = "cond.compare", Params = Json("""{ "op": "lt", "value": "100" }""") },
                new NodeDef { Id = "both", Type = "cond.all" },
            ],
            Edges =
            [
                .. Sound().Edges,
                new EdgeDef { From = "avg:value", To = "below:a" },
                new EdgeDef { From = "above:out", To = "both:a" },
                new EdgeDef { From = "below:out", To = "both:b" },
            ],
        };

        ValidationReport report = new DocumentValidator().Validate(document, new Catalogued());

        Assert.Contains(Codes.Contradiction, report.Blocks.Select(f => f.Code));
    }

    [Fact]
    public void A_target_at_or_behind_the_stop_is_refused()
    {
        // A target of zero R is the stop. Somebody who typed it means a number they have not typed yet.
        StrategyDocument document = Sound() with
        {
            Nodes =
            [
                .. Sound().Nodes,
                new NodeDef
                {
                    Id = "exit",
                    Type = "risk.exit",
                    Params = Json("""{ "stop": { "unit": "percent", "value": "1" }, "target": { "unit": "r", "value": "0" } }"""),
                },
            ],
            Edges = [.. Sound().Edges, new EdgeDef { From = "buy:position", To = "exit:position" }],
        };

        ValidationReport report = new DocumentValidator().Validate(document, new Catalogued());

        Assert.Contains(Codes.TargetInsideStop, report.Blocks.Select(f => f.Code));
    }

    [Fact]
    public void A_lookback_past_the_cap_is_refused()
    {
        StrategyDocument document = Sound() with
        {
            Nodes = [.. Sound().Nodes.Select(n => n.Id == "avg" ? n with { Params = Json("""{ "period": 99 }""") } : n)],
        };

        ValidationReport report = new DocumentValidator(options: new ValidatorOptions { MaxLookback = 50 })
            .Validate(document, new Catalogued());

        Assert.Contains(Codes.LookbackTooLong, report.Blocks.Select(f => f.Code));
    }

    [Fact]
    public void Less_history_than_the_warm_up_needs_is_warned_about()
    {
        // Not a block: the run can go ahead, and the person should know the first part of it is a strategy that has
        // not woken up yet. A warning nobody tested is a warning that can stop appearing unnoticed.
        UnixNanos start = UnixNanos.FromSeconds(1_700_000_000);
        StrategyDocument document = Sound();

        ValidationReport report = new DocumentValidator().Validate(
            document,
            new Catalogued(range: (start, new UnixNanos(start.Value + (2 * 60_000_000_000L)))));

        Assert.Contains(Codes.DataRangeInsufficient, report.Findings.Select(f => f.Code));
    }

    [Fact]
    public void A_node_its_own_catalog_forbids_in_this_mode_is_refused()
    {
        // No shipped node type restricts the modes it runs in, so this rule was unreachable with the default
        // catalog - the reason it had no test. A custom type can set them, and anybody registering one is relying on
        // the validator enforcing it.
        NodeTypeDescriptor labOnly = new()
        {
            Type = "custom.labOnly",
            Kind = NodeKind.Condition,
            DisplayName = "Lab only",
            Description = "A condition its author allows in the Lab and nowhere else.",
            FaceTemplate = "lab only",
            Inputs = [new PortSpec("value", ValueKind.Series, Required: true)],
            Outputs = [new PortSpec("out", ValueKind.Bool)],
            Modes = NodeModes.Lab,
            Factory = _ => throw new NotSupportedException("never evaluated in this test"),
        };
        NodeCatalog catalog = new(NodeCatalog.Default.Types);
        catalog.Register(labOnly);

        StrategyDocument document = Sound() with
        {
            Nodes = [.. Sound().Nodes, new NodeDef { Id = "lab", Type = "custom.labOnly" }],
            Edges = [.. Sound().Edges, new EdgeDef { From = "avg:value", To = "lab:value" }],
        };

        // In the Lab it is allowed; in Live the same document is refused, and the reason names the node.
        Assert.DoesNotContain(
            Codes.NodeModeNotAllowed,
            new DocumentValidator(catalog).Validate(document, new Catalogued()).Blocks.Select(f => f.Code));
        Assert.Contains(
            Codes.NodeModeNotAllowed,
            new DocumentValidator(catalog).Validate(document, new Catalogued(TradingEnvironment.Live)).Blocks.Select(f => f.Code));
    }

    [Fact]
    public void Every_reason_the_validator_can_give_is_exercised_by_a_test()
    {
        // The guard on the guards, the same shape as the risk engine's. It reads the validator's own source for the
        // codes it declares and the codes it raises, and asks two questions of each: can it be given at all, and has
        // anybody tried it. Counting these by hand is what found six untested and one that could never be given.
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "Bytex.Documents", "Validation", "DocumentValidator.cs")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        string source = File.ReadAllText(Path.Combine(dir!.FullName, "src", "Bytex.Documents", "Validation", "DocumentValidator.cs"));
        (string Name, string Code)[] declared = System.Text.RegularExpressions.Regex
            .Matches(source, @"public const string (?<name>\w+)\s*=\s*""(?<code>[A-Z_]+)""")
            .Select(m => (m.Groups["name"].Value, m.Groups["code"].Value))
            .ToArray();
        Assert.NotEmpty(declared);

        string tests = string.Join(
            "\n",
            new DirectoryInfo(Path.Combine(dir.FullName, "tests"))
                .GetFiles("*.cs", SearchOption.AllDirectories)
                .Where(f => !f.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    && !f.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Select(f => File.ReadAllText(f.FullName)));

        // Is the code right: a reason declared and never raised is a branch in somebody's interface that no document
        // can take.
        string[] unraisable = declared
            .Where(d => !source.Contains($"Codes.{d.Name},", StringComparison.Ordinal))
            .Select(d => d.Code)
            .ToArray();
        Assert.True(
            unraisable.Length == 0,
            $"the validator declares reasons it never gives: {string.Join(", ", unraisable)}. Remove them, or raise them.");

        // Can a user reach it: a reason nobody tests is a reason nobody can rely on.
        string[] untested = declared
            .Where(d => !tests.Contains($"Codes.{d.Name}", StringComparison.Ordinal) && !tests.Contains(d.Code, StringComparison.Ordinal))
            .Select(d => d.Code)
            .ToArray();
        Assert.True(
            untested.Length == 0,
            $"the validator can refuse a document for reasons no test exercises: {string.Join(", ", untested)}.");
    }
}
