using System.Text.Json;
using Bytex.Core.Model;
using Bytex.Core.Plugins;
using Bytex.Documents.Runtime;
using Bytex.Documents.Schema;

namespace Bytex.Documents.Tests;

// Why: a document payload comes in three shapes - a path to a file, an inline document under "document", or a bare
// document - and two of them left the environment unset so the strategy ran in the environment of the node that
// loaded it, while the path shape filled in Backtest. So the same document, loaded by path into a paper node, ran
// with backtest rules: no warm-up was ever requested, and a node that should have replayed recent history started
// with empty indicators and looked dead.
//
// Nothing tested the shapes against each other, which is how one of three came to behave differently. These tests
// hold all three to the same rule: a payload that does not name an environment does not get one invented for it.
public sealed class PayloadEnvironmentTests
{
    private const string Document = """
        {
          "schemaVersion": "1.0",
          "id": "payload-shapes",
          "name": "Payload shapes",
          "instruments": [ { "ref": "primary", "instrumentId": "BTCUSDT.SIM" } ],
          "barTypes": [ { "ref": "main", "instrument": "primary", "step": 1, "aggregation": "minute", "priceType": "last", "source": "external" } ],
          "parameters": [],
          "nodes": [
            { "id": "bars", "type": "data.bars", "params": { "barType": "main" } },
            { "id": "ema", "type": "ind.ema", "params": { "period": 5 } },
            { "id": "positive", "type": "cond.compare", "params": { "op": "gt", "value": 0 } },
            { "id": "buy", "type": "act.order", "params": { "side": "buy", "orderType": "market", "sizing": { "mode": "fixed", "value": 0.05 }, "onlyWhenFlat": true } }
          ],
          "edges": [
            { "from": "bars:bars", "to": "ema:bars" },
            { "from": "ema:value", "to": "positive:a" },
            { "from": "positive:out", "to": "buy:trigger" }
          ],
          "repeat": { "enabled": true }
        }
        """;

    /// <summary>The three shapes the parser accepts, each written without an environment.</summary>
    public static TheoryData<string> ShapesWithoutAnEnvironment() =>
        new("documentPath", "document", "bare");

    /// <summary>The shapes that carry strategy settings of their own, and so can name an environment at all.</summary>
    public static TheoryData<string> ShapesThatCanNameOne() =>
        new("documentPath", "document");

    [Theory]
    [MemberData(nameof(ShapesWithoutAnEnvironment))]
    public void A_payload_that_names_no_environment_leaves_it_to_the_node(string shape)
    {
        using PayloadOnDisk payload = PayloadOnDisk.For(shape, environment: null);

        DocumentStrategyConfig config = DocumentStrategyProvider.ParsePayload(payload.Definition);

        Assert.Null(config.Environment);
    }

    [Theory]
    [MemberData(nameof(ShapesThatCanNameOne))]
    public void A_payload_that_names_an_environment_is_taken_at_its_word(string shape)
    {
        using PayloadOnDisk payload = PayloadOnDisk.For(shape, TradingEnvironment.Sandbox);

        DocumentStrategyConfig config = DocumentStrategyProvider.ParsePayload(payload.Definition);

        Assert.Equal(TradingEnvironment.Sandbox, config.Environment);
    }

    [Theory]
    [MemberData(nameof(ShapesWithoutAnEnvironment))]
    public void The_strategy_the_provider_builds_carries_the_same_answer(string shape)
    {
        // The parse result is not the thing that runs: what matters is that the strategy handed to the node still has
        // nothing in it to override the node with.
        using PayloadOnDisk payload = PayloadOnDisk.For(shape, environment: null);
        DocumentStrategyProvider provider = new();

        DocumentStrategy strategy = Assert.IsType<DocumentStrategy>(provider.Create(payload.Definition));

        Assert.Null(strategy.Config.Environment);
    }

    [Theory]
    [MemberData(nameof(ShapesWithoutAnEnvironment))]
    public void Every_shape_reads_the_same_document(string shape)
    {
        // The other half of the comparison: the shapes have to differ in nothing but where the document came from,
        // or a test above could pass while a shape quietly read something else.
        using PayloadOnDisk payload = PayloadOnDisk.For(shape, environment: null);

        DocumentStrategyConfig config = DocumentStrategyProvider.ParsePayload(payload.Definition);

        Assert.Equal("payload-shapes", config.Document.Id);
        Assert.Equal("Payload shapes", config.Document.Name);
        Assert.Equal(4, config.Document.Nodes.Count);
    }

    [Fact]
    public void No_branch_of_the_parser_invents_an_environment()
    {
        // The guard on the fix. The defect was one branch of three filling in a default the other two left alone, and
        // an assignment like `Environment = shell.Environment ?? Backtest` reads so reasonably that it survived
        // review. Whatever the shapes become, none of them may decide this on the node's behalf.
        string source = File.ReadAllText(Source("src", "Bytex.Documents", "DocumentStrategyProvider.cs"));

        string[] offenders = source
            .Split('\n')
            .Where(line => line.Contains("Environment =", StringComparison.Ordinal) && line.Contains("??", StringComparison.Ordinal))
            .Select(line => line.Trim())
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "the payload parser defaults the environment instead of leaving it null, so a document loaded that way "
            + "ignores the environment of the node running it: " + string.Join(" | ", offenders));
    }

    private static string Source(params string[] parts)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Bytex.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine([directory!.FullName, .. parts]);
    }

    /// <summary>One payload of the given shape, with the file on disk that the path shape needs.</summary>
    private sealed class PayloadOnDisk : IDisposable
    {
        private readonly string? _directory;

        private PayloadOnDisk(StrategyDefinition definition, string? directory)
        {
            Definition = definition;
            _directory = directory;
        }

        public StrategyDefinition Definition { get; }

        public static PayloadOnDisk For(string shape, TradingEnvironment? environment)
        {
            string? setting = environment is null
                ? null
                : $", \"environment\": {JsonSerializer.Serialize(environment.Value, DocumentJson.Options)}";

            if (shape == "bare")
            {
                Assert.Null(environment);
                return new PayloadOnDisk(Wrap(Document), null);
            }

            if (shape == "document")
            {
                return new PayloadOnDisk(Wrap($"{{ \"document\": {Document}{setting} }}"), null);
            }

            Assert.Equal("documentPath", shape);
            string directory = Path.Combine(Path.GetTempPath(), "bytex-payload-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(directory);
            string file = Path.Combine(directory, "document.json");
            File.WriteAllText(file, Document);
            return new PayloadOnDisk(
                Wrap($"{{ \"documentPath\": {JsonSerializer.Serialize(file)}{setting} }}"),
                directory);
        }

        public void Dispose()
        {
            if (_directory is not null && Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }

        private static StrategyDefinition Wrap(string payload) =>
            new(DocumentStrategyProvider.ProviderId, "doc", JsonDocument.Parse(payload).RootElement.Clone());
    }
}
