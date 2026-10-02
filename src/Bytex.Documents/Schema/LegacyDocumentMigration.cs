using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using Bytex.Core.Migration;

namespace Bytex.Documents.Schema;

/// <summary>Converts a BYTEX v1 document without touching its original file.</summary>
public static class LegacyDocumentMigration
{
    public static string Convert(string json)
    {
        JsonObject root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        })?.AsObject() ?? throw new JsonException("The document is empty.");
        if (root["schemaVersion"]?.GetValue<string>() != "1.0")
        {
            throw new JsonException("Only BYTEX document version 1.0 is accepted by this migration.");
        }

        Rename(root);
        root["schemaVersion"] = StrategyDocument.CurrentSchemaVersion;
        if (!root.ContainsKey("id"))
        {
            // The document model's random default would make identical-input retries produce different output.
            root["id"] = System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("bytex-v2-document-migration\0" + json)))[..32].ToLowerInvariant();
        }
        foreach (JsonNode? market in root["instruments"]?.AsArray() ?? [])
        {
            if (market is JsonObject instrument && instrument["marketKey"] is JsonValue identity)
            {
                instrument["marketKey"] = LegacyIdentityReader.ReadMarket(identity.GetValue<string>()).Value;
            }
        }

        foreach (JsonNode? item in root["candleSeriesDefinitions"]?.AsArray() ?? [])
        {
            if (item is JsonObject series)
            {
                series["source"] = series["source"]?.GetValue<string>()?.ToLowerInvariant() switch
                {
                    null or "external" => "provider",
                    "internal" => "computed",
                    _ => throw new JsonException("The legacy candle origin is unknown."),
                };
            }
        }

        // Strict deserialization refuses unknown settings rather than losing them during conversion.
        return DocumentJson.Serialize(DocumentJson.Deserialize(root.ToJsonString()));
    }

    private static void Rename(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            foreach (JsonNode? child in array)
            {
                Rename(child);
            }
        }
        else if (node is JsonObject obj)
        {
            foreach (string key in obj.Select(pair => pair.Key).ToArray())
            {
                JsonNode? child = obj[key];
                Rename(child);
                string renamed = key switch
                {
                    "instrumentId" => "marketKey",
                    "barTypes" => "candleSeriesDefinitions",
                    "barType" => "candleSeries",
                    _ => key,
                };
                if (renamed != key)
                {
                    if (obj.ContainsKey(renamed))
                    {
                        throw new JsonException($"Both '{key}' and '{renamed}' are present.");
                    }
                    obj.Remove(key);
                    obj[renamed] = child;
                }
            }
        }
    }
}
