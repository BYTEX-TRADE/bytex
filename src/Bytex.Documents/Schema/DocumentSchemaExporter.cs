using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bytex.Documents.Catalog;

namespace Bytex.Documents.Schema;

/// <summary>
/// Exports a JSON Schema (draft 2020-12) for strategy documents, including per-type node parameter schemas from the catalog.
/// The builder validates on the client with it; the assistant reads it as the contract.
/// </summary>
public static class DocumentSchemaExporter
{
    public static JsonObject Export(NodeCatalog? catalog = null)
    {
        catalog ??= NodeCatalog.Default;
        JsonObject schema = new()
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$id"] = "https://bytex.trade/schemas/strategy-document/1.0.json",
            ["title"] = "BYTEX strategy document",
            ["type"] = "object",
            ["required"] = new JsonArray("schemaVersion", "name", "instruments", "barTypes", "nodes"),
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                ["schemaVersion"] = new JsonObject { ["type"] = "string", ["pattern"] = "^1\\.[0-9]+$" },
                ["id"] = new JsonObject { ["type"] = "string" },
                ["name"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 },
                ["description"] = new JsonObject { ["type"] = "string" },
                ["metadata"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["template"] = new JsonObject { ["type"] = "boolean" },
                        ["tags"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                        ["createdWith"] = new JsonObject { ["type"] = "string" },
                        ["difficulty"] = new JsonObject { ["enum"] = new JsonArray("simple", "intermediate", "advanced") },
                        ["runMode"] = new JsonObject { ["enum"] = new JsonArray("constant", "firedOnce") },
                        ["marketRegime"] = new JsonObject { ["type"] = "string" },
                        ["author"] = new JsonObject { ["type"] = "string" },
                    },
                },
                ["instruments"] = new JsonObject
                {
                    ["type"] = "array",
                    ["minItems"] = 1,
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["required"] = new JsonArray("ref", "instrumentId"),
                        ["properties"] = new JsonObject { ["ref"] = new JsonObject { ["type"] = "string" }, ["instrumentId"] = new JsonObject { ["type"] = "string", ["pattern"] = "^[A-Za-z0-9_\\-.]+\\.[A-Z0-9_]+$" } },
                    },
                },
                ["barTypes"] = new JsonObject
                {
                    ["type"] = "array",
                    ["minItems"] = 1,
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["required"] = new JsonArray("ref", "instrument"),
                        ["properties"] = new JsonObject
                        {
                            ["ref"] = new JsonObject { ["type"] = "string" },
                            ["instrument"] = new JsonObject { ["type"] = "string" },
                            ["step"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 },
                            ["aggregation"] = new JsonObject { ["enum"] = new JsonArray("tick", "volume", "value", "second", "minute", "hour", "day", "week", "month") },
                            ["priceType"] = new JsonObject { ["enum"] = new JsonArray("last", "bid", "ask", "mid") },
                            ["source"] = new JsonObject { ["enum"] = new JsonArray("external", "internal") },
                        },
                    },
                },
                ["parameters"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["required"] = new JsonArray("name", "value"),
                        ["properties"] = new JsonObject
                        {
                            ["name"] = new JsonObject { ["type"] = "string", ["pattern"] = "^[A-Za-z][A-Za-z0-9_]*$" },
                            ["type"] = new JsonObject { ["enum"] = new JsonArray("decimal", "int", "bool", "enum") },
                            ["label"] = new JsonObject { ["type"] = "string" },
                            ["description"] = new JsonObject { ["type"] = "string" },
                            ["value"] = new JsonObject { ["type"] = "string" },
                            ["min"] = new JsonObject { ["type"] = "string" },
                            ["max"] = new JsonObject { ["type"] = "string" },
                            ["step"] = new JsonObject { ["type"] = "string" },
                            ["choices"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                        },
                    },
                },
                ["nodes"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["required"] = new JsonArray("id", "type"),
                        ["properties"] = new JsonObject
                        {
                            ["id"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 },
                            ["type"] = new JsonObject { ["enum"] = new JsonArray(catalog.Types.Select(t => (JsonNode?)t.Type).ToArray()) },
                            ["label"] = new JsonObject { ["type"] = "string" },
                            ["params"] = new JsonObject { ["type"] = "object" },
                            ["disabled"] = new JsonObject { ["type"] = "boolean" },
                        },
                        ["allOf"] = new JsonArray(catalog.Types.Select(t => (JsonNode?)new JsonObject
                        {
                            ["if"] = new JsonObject { ["properties"] = new JsonObject { ["type"] = new JsonObject { ["const"] = t.Type } } },
                            ["then"] = new JsonObject { ["properties"] = new JsonObject { ["params"] = new JsonObject { ["$ref"] = "#/$defs/nodeParams/" + t.Type } } },
                        }).ToArray()),
                    },
                },
                ["edges"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["required"] = new JsonArray("from", "to"),
                        ["properties"] = new JsonObject { ["from"] = PortRef(), ["to"] = PortRef() },
                    },
                },
                ["phases"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["required"] = new JsonArray("id"),
                        ["properties"] = new JsonObject
                        {
                            ["id"] = new JsonObject { ["type"] = "string" },
                            ["name"] = new JsonObject { ["type"] = "string" },
                            ["nodes"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                            ["initial"] = new JsonObject { ["type"] = "boolean" },
                        },
                    },
                },
                ["transitions"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["required"] = new JsonArray("from", "to", "on"),
                        ["properties"] = new JsonObject { ["from"] = new JsonObject { ["type"] = "string" }, ["to"] = new JsonObject { ["type"] = "string" }, ["on"] = PortRef() },
                    },
                },
                ["repeat"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["enabled"] = new JsonObject { ["type"] = "boolean" } } },
                ["modes"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["live"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["allowAiAnnotationConditions"] = new JsonObject { ["type"] = "boolean" } } },
                    },
                },
                ["account"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["leverage"] = new JsonObject { ["type"] = "number", ["minimum"] = 1 },
                    },
                },
                ["layout"] = new JsonObject { ["type"] = "object" },
            },
            ["$defs"] = new JsonObject
            {
                ["paramValue"] = new JsonObject
                {
                    ["oneOf"] = new JsonArray(
                        new JsonObject { ["type"] = "number" },
                        new JsonObject { ["type"] = "string", ["pattern"] = "^-?[0-9]+(\\.[0-9]+)?$" },
                        new JsonObject { ["type"] = "object", ["required"] = new JsonArray("$param"), ["properties"] = new JsonObject { ["$param"] = new JsonObject { ["type"] = "string" } }, ["additionalProperties"] = false }),
                },
                ["nodeParams"] = new JsonObject(catalog.Types.Select(t => KeyValuePair.Create(t.Type, (JsonNode?)ParamsSchema(t.Params)))),
            },
        };
        return schema;
    }

    public static string ExportJson(NodeCatalog? catalog = null) => Export(catalog).ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    private static JsonObject PortRef() => new() { ["type"] = "string", ["pattern"] = "^[^:]+:[^:]+$" };

    private static JsonObject ParamsSchema(IReadOnlyList<ParamSpec> specs)
    {
        JsonObject properties = new();
        JsonArray required = new();
        foreach (ParamSpec spec in specs)
        {
            properties[spec.Name] = ParamSchema(spec);
            if (spec.Required)
            {
                required.Add(spec.Name);
            }
        }

        JsonObject schema = new() { ["type"] = "object", ["properties"] = properties };
        if (required.Count > 0)
        {
            schema["required"] = required;
        }

        return schema;
    }

    private static JsonObject ParamSchema(ParamSpec spec)
    {
        JsonObject s = spec.Type switch
        {
            ParamType.Int or ParamType.Decimal => new JsonObject { ["$ref"] = "#/$defs/paramValue" },
            ParamType.Bool => new JsonObject { ["type"] = "boolean" },
            ParamType.Enum => new JsonObject { ["enum"] = new JsonArray(spec.ChoiceValues.Select(c => (JsonNode?)c).ToArray()) },
            ParamType.Object => ParamsSchema(spec.Fields ?? []),
            ParamType.Time => new JsonObject { ["type"] = "string", ["pattern"] = "^([01][0-9]|2[0-3]):[0-5][0-9]$" },
            _ => new JsonObject { ["type"] = "string" },
        };
        if (spec.Label is not null)
        {
            s["title"] = spec.Label;
        }

        if (spec.Description is not null)
        {
            s["description"] = spec.Description;
        }

        if (spec.Default is not null)
        {
            s["default"] = spec.Type switch
            {
                ParamType.Bool => JsonValue.Create(string.Equals(spec.Default, "true", StringComparison.OrdinalIgnoreCase)),
                ParamType.Int => JsonValue.Create(int.Parse(spec.Default, CultureInfo.InvariantCulture)),
                _ => JsonValue.Create(spec.Default),
            };
        }

        JsonObject hints = new();
        if (spec.Min is not null)
        {
            hints["min"] = spec.Min;
        }

        if (spec.Max is not null)
        {
            hints["max"] = spec.Max;
        }

        if (spec.Step is not null)
        {
            hints["step"] = spec.Step;
        }

        if (spec.Unit is not null)
        {
            hints["unit"] = spec.Unit;
        }

        hints["kind"] = spec.Type.ToString().ToLowerInvariant();
        s["x-bytex"] = hints;
        return s;
    }
}
