using System.Reflection;
using System.Text.Json;
using Bytex.Core.Model;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Plugins;
using Bytex.Core.Trading;
using Bytex.Documents.Runtime;

namespace Bytex.Documents.Tests;

// Why: a document payload comes in three shapes, and the one that names a FILE built its config from a hand-written
// list of three fields - parameterOverrides, strategyId, environment - while the inline shape deserialized the whole
// object. A DocumentStrategyConfig has eighteen settable members, so loading by path silently dropped fifteen of
// them.
//
// What that costs is not evenly spread. "useHyphensInClientOrderIds": false loaded by path stayed true, and OKX
// refuses a client order id containing a hyphen - so every order of every document loaded that way was refused by
// the venue, while the adapter's own message told the operator to set the flag that could not be set. warmupBars
// went the same way, which is a live node starting with empty indicators.
//
// PayloadEnvironmentTests is the same defect one member earlier: `environment` was added to that list of three when
// a paper run came up with backtest rules, and the other fifteen were left. A list of fields to keep in step with a
// record is a list that falls behind it, so the fix removes the list rather than lengthening it - the path shape now
// rewrites its payload into the inline shape and there is one deserialization for both. These tests hold that: what
// a payload names is honoured whichever shape named it, compared member by member rather than by the few somebody
// thought to check.
public sealed class PayloadSettingsTests
{
    private const string Document = """
        {
          "schemaVersion": "2.0",
          "id": "payload-settings",
          "name": "Payload settings",
          "instruments": [ { "ref": "primary", "marketKey": "bx-market:v2/SIM/BTCUSDT" } ],
          "candleSeriesDefinitions": [ { "ref": "main", "instrument": "primary", "step": 1, "aggregation": "minute", "priceType": "last", "source": "provider" } ],
          "parameters": [],
          "nodes": [
            { "id": "bars", "type": "data.bars", "params": { "candleSeries": "main" } },
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

    /// <summary>
    /// Every setting the two settings-carrying shapes accept, each written away from its default so that a value
    /// left behind reads as a default rather than as a coincidence.
    /// </summary>
    private const string Settings = """
          "strategyId": "Named-1",
          "orderIdTag": "TAG",
          "omsType": "hedging",
          "externalOrderClaims": [ "bx-market:v2/SIM/ETHUSDT" ],
          "manageContingentOrders": true,
          "manageGtdExpiry": true,
          "useHyphensInClientOrderIds": false,
          "runtimeModuleId": "RuntimeModule-1",
          "logEvents": false,
          "logCommands": false,
          "emitDecisionEvents": false,
          "decisionHistory": 17,
          "annotationWindow": "02:00:00",
          "environment": "sandbox",
          "warmupBars": 42,
          "parameterOverrides": { "period": 9 }
        """;

    public static TheoryData<string> Shapes() => new("documentPath", "document");

    /// <summary>
    /// The reported defect, and the one that cost real orders: a document loaded by path kept hyphens in its client
    /// order ids however the payload was written, and OKX refuses those.
    /// </summary>
    [Theory]
    [MemberData(nameof(Shapes))]
    public void A_payload_that_turns_hyphens_off_is_obeyed_whichever_shape_it_took(string shape)
    {
        using Payload payload = Payload.For(shape, Settings);

        DocumentStrategyConfig config = DocumentStrategyProvider.ParsePayload(payload.Definition);

        Assert.False(
            config.UseHyphensInClientOrderIds,
            $"the {shape} shape ignored useHyphensInClientOrderIds, so every order of this document is refused on a "
            + "venue that will not take a hyphen");
    }

    /// <summary>
    /// And the rest of them, named one by one, because a fix that carried only the reported member would leave the
    /// other fourteen exactly as they were.
    /// </summary>
    [Theory]
    [MemberData(nameof(Shapes))]
    public void Every_setting_a_payload_names_is_honoured(string shape)
    {
        using Payload payload = Payload.For(shape, Settings);

        DocumentStrategyConfig config = DocumentStrategyProvider.ParsePayload(payload.Definition);

        Assert.Equal(new StrategyId("Named-1"), config.StrategyId);
        Assert.Equal("TAG", config.OrderIdTag);
        Assert.Equal(OmsType.Hedging, config.OmsType);
        Assert.Equal([MarketKey.Parse("bx-market:v2/SIM/ETHUSDT")], config.ExternalOrderClaims);
        Assert.True(config.ManageContingentOrders);
        Assert.True(config.ManageGtdExpiry);
        Assert.False(config.UseHyphensInClientOrderIds);
        Assert.Equal(new RuntimeModuleId("RuntimeModule-1"), config.RuntimeModuleId);
        Assert.False(config.LogEvents);
        Assert.False(config.LogCommands);
        Assert.False(config.EmitDecisionEvents);
        Assert.Equal(17, config.DecisionHistory);
        Assert.Equal(TimeSpan.FromHours(2), config.AnnotationWindow);
        Assert.Equal(TradingEnvironment.Sandbox, config.Environment);
        Assert.Equal(42, config.WarmupBars);
        Assert.NotNull(config.ParameterOverrides);
        Assert.Equal(9m, Assert.Contains("period", config.ParameterOverrides));
    }

    /// <summary>
    /// The guard, and the reason this file exists rather than one more assertion in the last one: the two shapes are
    /// compared MEMBER BY MEMBER, by reflection, so a setting added to the record next year is compared without
    /// anybody remembering to compare it. Only the document itself may differ, and only in where it was read from.
    /// </summary>
    [Fact]
    public void The_two_shapes_agree_on_every_member_they_both_carry()
    {
        using Payload byPath = Payload.For("documentPath", Settings);
        using Payload inline = Payload.For("document", Settings);

        DocumentStrategyConfig fromPath = DocumentStrategyProvider.ParsePayload(byPath.Definition);
        DocumentStrategyConfig fromInline = DocumentStrategyProvider.ParsePayload(inline.Definition);

        List<string> differ = [];
        foreach (PropertyInfo property in typeof(DocumentStrategyConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.Name is nameof(DocumentStrategyConfig.Document))
            {
                continue;
            }

            object? path = property.GetValue(fromPath);
            object? name = property.GetValue(fromInline);
            if (!Equal(path, name))
            {
                differ.Add($"{property.Name}: by path {Show(path)}, inline {Show(name)}");
            }
        }

        Assert.True(
            differ.Count == 0,
            "the shapes disagree, so the same payload means different things depending on where the document was "
            + "written: " + string.Join(" | ", differ));
    }

    /// <summary>
    /// A SETTING THIS ENGINE DOES NOT HAVE IS REFUSED BY NAME, whichever shape named it.
    ///
    /// <para>
    /// The rule 0.9.1 set for every configuration, applied here late. Widening which settings a payload
    /// honours without reading it strictly left a worse gap than the one it closed: fifteen settings now
    /// work, so somebody who misspells the sixteenth has every reason to believe it took effect. On a venue
    /// that refuses a hyphen in a client order id, <c>useHyphensInClientOrderIdz</c> means every order of
    /// that document is refused with nothing pointing at the typo.
    /// </para>
    ///
    /// <para>
    /// The node's own <c>config</c> block already refused it, which is what made the gap visible: the same
    /// typo in the same run was named in one place and ignored in the other.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Shapes))]
    public void A_setting_this_engine_does_not_have_is_refused_by_name(string shape)
    {
        using Payload payload = Payload.For(shape, "  \"useHyphensInClientOrderIdz\": false");

        Exception refused = Assert.ThrowsAny<Exception>(() => DocumentStrategyProvider.ParsePayload(payload.Definition));

        Assert.Contains("useHyphensInClientOrderIdz", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// And inside the DOCUMENT, where the cost is worse still.
    ///
    /// <para>
    /// A misspelled <c>evaluation</c> fell back to the bar close: the run then evaluated on closed bars
    /// while the document said it evaluated on ticks, and every figure it produced looked like a figure.
    /// Silence as forward compatibility is how that happens, so a field this engine does not model is
    /// refused rather than skipped - the owner's ruling, strict everywhere.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Shapes))]
    public void A_field_the_document_schema_does_not_have_is_refused_by_name(string shape)
    {
        using Payload payload = Payload.For(shape, settings: null, documentField: "\"evaluationn\": \"quote\"");

        Exception refused = Assert.ThrowsAny<Exception>(() => DocumentStrategyProvider.ParsePayload(payload.Definition));

        Assert.Contains("evaluationn", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other half of the same defect, one level up: a node's own <c>config</c> block was merged member by member
    /// from a hand-written list of six, and <c>StrategyConfig</c> has seven. The seventh was
    /// <c>UseHyphensInClientOrderIds</c>, so the setting could not be reached from a node either.
    /// </summary>
    [Fact]
    public void A_nodes_own_config_block_reaches_the_strategy_whole()
    {
        using Payload payload = Payload.For("document", settings: null);
        StrategyConfig fromNode = new()
        {
            StrategyId = new StrategyId("FromNode-1"),
            OrderIdTag = "NODE",
            OmsType = OmsType.Netting,
            ExternalOrderClaims = [MarketKey.Parse("bx-market:v2/SIM/SOLUSDT")],
            ManageContingentOrders = true,
            ManageGtdExpiry = true,
            UseHyphensInClientOrderIds = false,
        };

        DocumentStrategyConfig config = DocumentStrategyProvider.ParsePayload(payload.Definition with { Config = fromNode });

        Assert.Equal(new StrategyId("FromNode-1"), config.StrategyId);
        Assert.Equal("NODE", config.OrderIdTag);
        Assert.Equal(OmsType.Netting, config.OmsType);
        Assert.Equal([MarketKey.Parse("bx-market:v2/SIM/SOLUSDT")], config.ExternalOrderClaims);
        Assert.True(config.ManageContingentOrders);
        Assert.True(config.ManageGtdExpiry);
        Assert.False(
            config.UseHyphensInClientOrderIds,
            "a node's config block does not carry useHyphensInClientOrderIds to the strategy, so the setting cannot "
            + "be reached from a node configuration at all");
    }

    private static bool Equal(object? left, object? right) =>
        (left, right) switch
        {
            (null, null) => true,
            (null, _) or (_, null) => false,
            (System.Collections.IEnumerable a, System.Collections.IEnumerable b) when left is not string =>
                a.Cast<object>().SequenceEqual(b.Cast<object>()),
            _ => left.Equals(right),
        };

    private static string Show(object? value) =>
        value switch
        {
            null => "null",
            string text => text,
            System.Collections.IEnumerable items => "[" + string.Join(", ", items.Cast<object>()) + "]",
            _ => value.ToString() ?? "null",
        };

    /// <summary>One payload of the given shape, with the document on disk where the path shape needs it.</summary>
    private sealed class Payload : IDisposable
    {
        private readonly string? _directory;

        private Payload(StrategyDefinition definition, string? directory)
        {
            Definition = definition;
            _directory = directory;
        }

        public StrategyDefinition Definition { get; }

        public static Payload For(string shape, string? settings, string? documentField = null)
        {
            string tail = settings is null ? string.Empty : ",\n" + settings;
            string document = documentField is null
                ? Document
                : Document.TrimEnd().TrimEnd('}').TrimEnd() + ",\n  " + documentField + "\n}";

            if (shape == "document")
            {
                return new Payload(Wrap($"{{ \"document\": {document}{tail} }}"), null);
            }

            Assert.Equal("documentPath", shape);
            string directory = Path.Combine(Path.GetTempPath(), "bytex-settings-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(directory);
            string file = Path.Combine(directory, "document.json");
            File.WriteAllText(file, document);
            return new Payload(Wrap($"{{ \"documentPath\": {JsonSerializer.Serialize(file)}{tail} }}"), directory);
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
