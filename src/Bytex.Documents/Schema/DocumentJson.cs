using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bytex.Core.Serialization;

namespace Bytex.Documents.Schema;

/// <summary>
/// JSON conventions for strategy documents: camelCase, enums as strings, numbers as strings where money or precision
/// is involved - and a member this engine does not model is REFUSED rather than skipped.
///
/// <para>
/// That last part is the rule 0.9.1 set for every configuration, applied here on the owner's ruling of 2026-09-29:
/// strict everywhere. A document is read leniently in most engines as forward compatibility, and what that buys is
/// silence in the one place silence costs most. A misspelled <c>evaluation</c> fell back to the bar close, so the run
/// evaluated on closed bars while the document said it evaluated on ticks - and every figure it produced looked
/// exactly like a figure. The same applies one level up, to a strategy payload's settings: fifteen of them are
/// honoured, so somebody who misspells the sixteenth has every reason to believe it took effect.
/// </para>
///
/// <para>
/// The cost is accepted with open eyes: a document carrying a field a NEWER version of this schema understands is
/// refused by an older engine instead of being read without it. Refusing to run is the answer that can be acted on;
/// running something other than what was written is not.
/// </para>
/// </summary>
public static class DocumentJson
{
    public static JsonSerializerOptions Options { get; } = Create(indented: false);

    public static JsonSerializerOptions IndentedOptions { get; } = Create(indented: true);

    public static JsonSerializerOptions Create(bool indented)
    {
        // Strict, and from BytexJson.CreateStrict rather than by setting the flag here, so a document is read by
        // the same rule as every other configuration this engine accepts.
        JsonSerializerOptions options = BytexJson.CreateStrict(indented);
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
