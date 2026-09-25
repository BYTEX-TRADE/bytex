using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bytex.Core.Serialization;

namespace Bytex.Documents.Schema;

/// <summary>
/// JSON conventions for strategy documents: camelCase, enums as strings, numbers as strings where money or precision is involved.
/// </summary>
public static class DocumentJson
{
    public static JsonSerializerOptions Options { get; } = Create(indented: false);

    public static JsonSerializerOptions IndentedOptions { get; } = Create(indented: true);

    public static JsonSerializerOptions Create(bool indented)
    {
        JsonSerializerOptions options = BytexJson.Create(indented);
        options.Converters.Add(new ParamValueJsonConverter());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.ReadCommentHandling = JsonCommentHandling.Skip;
        options.AllowTrailingCommas = true;
        return options;
    }

    public static string Serialize(StrategyDocument document, bool indented = true) =>
        JsonSerializer.Serialize(document, indented ? IndentedOptions : Options);

    public static StrategyDocument Deserialize(string json) =>
        JsonSerializer.Deserialize<StrategyDocument>(json, Options) ?? throw new JsonException("The document is empty.");

    public static StrategyDocument Deserialize(JsonElement element) =>
        element.Deserialize<StrategyDocument>(Options) ?? throw new JsonException("The document is empty.");

    /// <summary>Reads a numeric parameter value, accepting numbers, numeric strings, or {"$param": name}.</summary>
    public static ParamValue? ReadParamValue(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                return new ParamValue(element.GetDecimal());
            case JsonValueKind.String:
                return decimal.TryParse(element.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal d) ? new ParamValue(d) : null;
            case JsonValueKind.Object:
                return element.TryGetProperty("$param", out JsonElement p) && p.ValueKind == JsonValueKind.String ? new ParamValue(p.GetString()!) : null;
            default:
                return null;
        }
    }

    public static string FormatDecimal(decimal value) => value.ToString(CultureInfo.InvariantCulture);
}
