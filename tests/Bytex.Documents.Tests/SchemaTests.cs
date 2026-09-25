using System.Text.Json;
using Bytex.Documents.Schema;

namespace Bytex.Documents.Tests;

// Why: the document is the contract between the engine and anything that authors a strategy, so what it promises is
// pinned here: a document survives a round trip through the engine's JSON conventions unchanged, a parameter reference
// is data rather than a number, and what the format leaves out on the way to disk is what a reader may leave out.
public class SchemaTests
{
    private static StrategyDocument Sample() => new()
    {
        Id = "ema-cross",
        Name = "EMA cross",
        Metadata = new DocumentMetadata { Author = "someone", Tags = ["trend"], Difficulty = "simple", RunMode = "constant" },
        Instruments = [new InstrumentRef { Ref = "primary", InstrumentId = "BTCUSDT.BINANCE" }],
        BarTypes = [new BarTypeRef { Ref = "main", Instrument = "primary", Step = 1, Aggregation = "minute", PriceType = "last", Source = "external" }],
        Parameters =
        [
            new ParameterDef { Name = "fast", Type = "int", Value = "12", Min = "2", Max = "200" },
            new ParameterDef { Name = "slow", Type = "int", Value = "26" },
            new ParameterDef { Name = "dir", Type = "enum", Value = "buy", Choices = ["buy", "sell"] },
        ],
        Nodes =
        [
            new NodeDef { Id = "bars", Type = "data.bars", Params = Json("""{ "barType": "main" }""") },
            new NodeDef { Id = "fast", Type = "ind.ema", Label = "fast average", Params = Json("""{ "period": { "$param": "fast" } }""") },
            new NodeDef { Id = "off", Type = "ind.ema", Disabled = true },
        ],
        Edges = [new EdgeDef { From = "bars:bars", To = "fast:bars" }],
        Phases = [new PhaseDef { Id = "hunting", Initial = true, Nodes = ["bars", "fast"] }],
        Transitions = [new TransitionDef { From = "hunting", To = "hunting", On = "fast:crossed" }],
        Repeat = new RepeatSettings { Enabled = true },
    };

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void A_document_round_trips_through_the_engines_json_conventions()
    {
        StrategyDocument document = Sample();

        StrategyDocument back = DocumentJson.Deserialize(DocumentJson.Serialize(document));

        Assert.Equal(document.Id, back.Id);
        Assert.Equal(document.Name, back.Name);
        Assert.Equal(("someone", "simple"), (back.Metadata!.Author, back.Metadata.Difficulty));
        Assert.Equal(["trend"], back.Metadata.Tags);
        Assert.Equal("BTCUSDT.BINANCE", Assert.Single(back.Instruments).InstrumentId);
        Assert.Equal((1, "minute", "last", "external"), (back.BarTypes[0].Step, back.BarTypes[0].Aggregation, back.BarTypes[0].PriceType, back.BarTypes[0].Source));
        Assert.Equal(["fast", "slow", "dir"], back.Parameters.Select(p => p.Name));
        Assert.Equal(["buy", "sell"], back.Parameter("dir")!.Choices!);
        Assert.Equal(["bars", "fast", "off"], back.Nodes.Select(n => n.Id));
        Assert.True(back.Node("off")!.Disabled);
        Assert.Equal("fast average", back.Node("fast")!.Label);
        Assert.Equal(("bars:bars", "fast:bars"), (back.Edges[0].From, back.Edges[0].To));
        Assert.True(back.Phases[0].Initial);
        Assert.Equal("fast:crossed", back.Transitions[0].On);
        Assert.True(back.Repeat.Enabled);
    }

    [Fact]
    public void Names_are_camelCase_and_nothing_null_is_written()
    {
        string json = DocumentJson.Serialize(Sample());

        Assert.Contains("\"barTypes\"", json, StringComparison.Ordinal);
        Assert.Contains("\"instrumentId\"", json, StringComparison.Ordinal);
        Assert.Contains("\"schemaVersion\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"BarTypes\"", json, StringComparison.Ordinal);

        // The "off" node carries no params and no label, and neither appears as null.
        Assert.DoesNotContain("null", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_parameter_reference_survives_as_a_reference_and_a_number_as_a_number()
    {
        StrategyDocument document = DocumentJson.Deserialize("""
            {
              "schemaVersion": "1.0",
              "id": "refs",
              "name": "Refs",
              "instruments": [ { "ref": "primary", "instrumentId": "BTCUSDT.BINANCE" } ],
              "barTypes": [ { "ref": "main", "instrument": "primary", "step": 1, "aggregation": "minute" } ],
              "parameters": [ { "name": "period", "type": "int", "value": "12" } ],
              "nodes": [
                { "id": "byRef", "type": "ind.ema", "params": { "period": { "$param": "period" } } },
                { "id": "byValue", "type": "ind.ema", "params": { "period": 9 } }
              ],
              "edges": []
            }
            """);

        ParamValue reference = DocumentJson.ReadParamValue(document.Node("byRef")!.Params!.Value.GetProperty("period"))!.Value;
        ParamValue literal = DocumentJson.ReadParamValue(document.Node("byValue")!.Params!.Value.GetProperty("period"))!.Value;

        Assert.True(reference.IsParameter);
        Assert.Equal("period", reference.ParameterName);
        Assert.Equal(12m, reference.Resolve(new Dictionary<string, decimal>(StringComparer.Ordinal) { ["period"] = 12m }));
        Assert.False(literal.IsParameter);
        Assert.Equal(9m, literal.Literal);

        // And the reference is still a reference after a round trip, not the number it resolved to.
        string json = DocumentJson.Serialize(document);
        Assert.Contains("\"$param\": \"period\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_minimal_document_reads_with_the_defaults_the_format_promises()
    {
        StrategyDocument document = DocumentJson.Deserialize("""
            {
              "id": "bare",
              "name": "Bare",
              "instruments": [ { "ref": "primary", "instrumentId": "BTCUSDT.BINANCE" } ],
              "barTypes": [ { "ref": "main", "instrument": "primary", "step": 5, "aggregation": "minute" } ],
              "nodes": [ { "id": "bars", "type": "data.bars" } ],
              "edges": []
            }
            """);

        Assert.Equal("1.0", document.SchemaVersion);
        Assert.Equal("last", document.BarTypes[0].PriceType);
        Assert.Equal("external", document.BarTypes[0].Source);
        Assert.Empty(document.Parameters);
        Assert.Empty(document.Phases);
        Assert.Empty(document.Transitions);
        // Metadata is always there with its own defaults, so a reader never has to test for it.
        Assert.Equal("constant", document.Metadata!.RunMode);
        Assert.Empty(document.Metadata.Tags);
        Assert.Null(document.Metadata.Author);
        Assert.Null(document.Node("bars")!.Params);
        Assert.False(document.Node("bars")!.Disabled);
        Assert.Null(document.Node("missing"));
    }

    [Fact]
    public void An_empty_document_is_refused_rather_than_read_as_nothing()
    {
        Assert.Throws<JsonException>(() => DocumentJson.Deserialize("null"));
    }
}
