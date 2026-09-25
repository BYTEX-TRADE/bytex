using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bytex.Documents.Catalog;
using Bytex.Documents.Schema;

namespace Bytex.Documents.Tests;

// Why: the exported JSON Schema is what a builder validates a document against before the engine ever sees it, so it
// has to describe the same document the engine reads - every member, every node type of the catalog it was given, and
// a parameter reference wherever a number may be one.
public class SchemaExportTests
{
    private static JsonObject Schema() => DocumentSchemaExporter.Export();

    [Fact]
    public void The_schema_describes_every_member_of_the_document_it_validates()
    {
        JsonObject properties = Schema()["properties"]!.AsObject();

        IEnumerable<string> members = typeof(StrategyDocument)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetMethod is not null && p.Name != "EqualityContract")
            .Select(p => char.ToLowerInvariant(p.Name[0]) + p.Name[1..]);

        foreach (string member in members)
        {
            Assert.True(properties.ContainsKey(member), $"the schema says nothing about {member}, which a document carries");
        }

        // The schema refuses what the document has no room for, so the two cannot drift apart unnoticed.
        Assert.False(Schema()["additionalProperties"]!.GetValue<bool>());
        Assert.Equal(properties.Count, members.Count());
    }

    [Fact]
    public void A_node_type_the_catalog_does_not_know_is_not_allowed()
    {
        JsonObject schema = Schema();
        JsonArray allowed = schema["properties"]!["nodes"]!["items"]!["properties"]!["type"]!["enum"]!.AsArray();

        Assert.Equal(
            NodeCatalog.Default.Types.Select(t => t.Type).OrderBy(t => t, StringComparer.Ordinal),
            allowed.Select(t => t!.GetValue<string>()).OrderBy(t => t, StringComparer.Ordinal));
    }

    [Fact]
    public void Each_type_brings_the_schema_of_its_own_parameters()
    {
        JsonObject defs = Schema()["$defs"]!["nodeParams"]!.AsObject();

        Assert.Equal(NodeCatalog.Default.Types.Count, defs.Count);

        JsonObject donchian = defs["ind.donchian"]!.AsObject()["properties"]!.AsObject();
        Assert.Equal("#/$defs/paramValue", donchian["period"]!["$ref"]!.GetValue<string>());
        Assert.Equal(20, donchian["period"]!["default"]!.GetValue<int>());
        Assert.Equal("int", donchian["period"]!["x-bytex"]!["kind"]!.GetValue<string>());
        Assert.Equal("boolean", donchian["excludeCurrent"]!["type"]!.GetValue<string>());
        Assert.False(donchian["excludeCurrent"]!["default"]!.GetValue<bool>());
        Assert.Equal(["last", "bid", "ask", "mid"], donchian["priceType"]!["enum"]!.AsArray().Select(c => c!.GetValue<string>()));
    }

    [Fact]
    public void A_number_may_be_a_literal_a_numeric_string_or_a_parameter_reference()
    {
        JsonArray oneOf = Schema()["$defs"]!["paramValue"]!["oneOf"]!.AsArray();

        Assert.Equal(3, oneOf.Count);
        Assert.Equal("number", oneOf[0]!["type"]!.GetValue<string>());
        Assert.Equal("string", oneOf[1]!["type"]!.GetValue<string>());
        Assert.Equal(["$param"], oneOf[2]!["required"]!.AsArray().Select(r => r!.GetValue<string>()));
        Assert.False(oneOf[2]!["additionalProperties"]!.GetValue<bool>());
    }

    [Fact]
    public void A_smaller_catalog_makes_a_smaller_schema()
    {
        NodeCatalog only = new([NodeCatalog.Default.Find("ind.ema")!]);

        JsonObject schema = DocumentSchemaExporter.Export(only);

        Assert.Equal(["ind.ema"], schema["properties"]!["nodes"]!["items"]!["properties"]!["type"]!["enum"]!.AsArray().Select(t => t!.GetValue<string>()));
        Assert.Equal(["ind.ema"], schema["$defs"]!["nodeParams"]!.AsObject().Select(kv => kv.Key));
    }

    // Why: an option carries a label and a sentence for a person to read, but what a document stores is still the
    // bare value, and the schema a document is validated against must keep listing exactly those.
    [Fact]
    public void A_choice_in_the_schema_is_the_value_a_document_stores()
    {
        JsonObject schema = DocumentSchemaExporter.Export();
        JsonObject sizing = schema["$defs"]!["nodeParams"]!["act.order"]!["properties"]!["sizing"]!.AsObject();

        Assert.Equal(["fixed", "notional", "percentOfBalance", "riskPercent"],
            sizing["properties"]!["mode"]!["enum"]!.AsArray().Select(c => c!.GetValue<string>()));
    }

    [Fact]
    public void The_exported_text_is_json_and_names_the_schema_it_follows()
    {
        string json = DocumentSchemaExporter.ExportJson();

        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal("https://json-schema.org/draft/2020-12/schema", doc.RootElement.GetProperty("$schema").GetString());
        Assert.Equal("object", doc.RootElement.GetProperty("type").GetString());
        Assert.Contains("schemaVersion", doc.RootElement.GetProperty("required").EnumerateArray().Select(r => r.GetString()));
    }
}
