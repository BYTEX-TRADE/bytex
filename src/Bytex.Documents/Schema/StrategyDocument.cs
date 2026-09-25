using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bytex.Documents.Schema;

/// <summary>
/// A strategy document: a typed dataflow graph of nodes and edges plus a phase machine.
/// This is the file format a host produces and the engine runs.
/// </summary>
public sealed record StrategyDocument
{
    public const string CurrentSchemaVersion = "1.0";

    public string SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public required string Name { get; init; }

    public string? Description { get; init; }

    public DocumentMetadata Metadata { get; init; } = new();

    public IReadOnlyList<InstrumentRef> Instruments { get; init; } = [];

    public IReadOnlyList<BarTypeRef> BarTypes { get; init; } = [];

    public IReadOnlyList<ParameterDef> Parameters { get; init; } = [];

    public IReadOnlyList<NodeDef> Nodes { get; init; } = [];

    public IReadOnlyList<EdgeDef> Edges { get; init; } = [];

    public IReadOnlyList<PhaseDef> Phases { get; init; } = [];

    public IReadOnlyList<TransitionDef> Transitions { get; init; } = [];

    public RepeatSettings Repeat { get; init; } = new();

    public ModeSettings Modes { get; init; } = new();

    public AccountSettings Account { get; init; } = new();

    /// <summary>Front-end layout hints (node positions); ignored by the engine.</summary>
    public JsonElement? Layout { get; init; }

    public NodeDef? Node(string id) => Nodes.FirstOrDefault(n => string.Equals(n.Id, id, StringComparison.Ordinal));

    public ParameterDef? Parameter(string name) => Parameters.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    public InstrumentRef? Instrument(string reference) => Instruments.FirstOrDefault(i => string.Equals(i.Ref, reference, StringComparison.Ordinal));

    public BarTypeRef? BarType(string reference) => BarTypes.FirstOrDefault(b => string.Equals(b.Ref, reference, StringComparison.Ordinal));
}

public sealed record DocumentMetadata
{
    public bool Template { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public string? CreatedWith { get; init; }

    /// <summary>simple | intermediate | advanced</summary>
    public string? Difficulty { get; init; }

    /// <summary>constant | firedOnce</summary>
    public string RunMode { get; init; } = "constant";

    public string? MarketRegime { get; init; }

    public string? Author { get; init; }
}

public sealed record InstrumentRef
{
    public required string Ref { get; init; }

    /// <summary>Engine instrument id, e.g. BTCUSDT-PERP.BYBIT.</summary>
    public required string InstrumentId { get; init; }
}

public sealed record BarTypeRef
{
    public required string Ref { get; init; }

    /// <summary>The <see cref="InstrumentRef.Ref"/> this bar type belongs to.</summary>
    public required string Instrument { get; init; }

    public int Step { get; init; } = 1;

    /// <summary>minute | hour | day | second | tick | volume | value | week | month</summary>
    public string Aggregation { get; init; } = "minute";

    /// <summary>last | bid | ask | mid</summary>
    public string PriceType { get; init; } = "last";

    /// <summary>external | internal</summary>
    public string Source { get; init; } = "external";
}

public sealed record ParameterDef
{
    public required string Name { get; init; }

    /// <summary>decimal | int | bool | enum</summary>
    public string Type { get; init; } = "decimal";

    public string? Label { get; init; }

    public string? Description { get; init; }

    /// <summary>Default value, as a string (decimal and int are strings in JSON).</summary>
    public required string Value { get; init; }

    public string? Min { get; init; }

    public string? Max { get; init; }

    public string? Step { get; init; }

    public IReadOnlyList<string>? Choices { get; init; }
}

public sealed record NodeDef
{
    public required string Id { get; init; }

    /// <summary>Catalog type, e.g. cond.cross.</summary>
    public required string Type { get; init; }

    public string? Label { get; init; }

    /// <summary>Parameters as raw JSON; the catalog's param schema for the type describes the shape.</summary>
    public JsonElement? Params { get; init; }

    public bool Disabled { get; init; }
}

public sealed record EdgeDef
{
    /// <summary>nodeId:port</summary>
    public required string From { get; init; }

    /// <summary>nodeId:port</summary>
    public required string To { get; init; }

    public static (string NodeId, string Port) Split(string reference)
    {
        int idx = reference.LastIndexOf(':');
        return idx <= 0 ? (reference, string.Empty) : (reference[..idx], reference[(idx + 1)..]);
    }
}

public sealed record PhaseDef
{
    public required string Id { get; init; }

    public string? Name { get; init; }

    public IReadOnlyList<string> Nodes { get; init; } = [];

    public bool Initial { get; init; }
}

public sealed record TransitionDef
{
    public required string From { get; init; }

    public required string To { get; init; }

    /// <summary>nodeId:port of a pulse output that triggers the transition.</summary>
    public required string On { get; init; }
}

public sealed record RepeatSettings
{
    /// <summary>true: run constantly; false: fired once, the run completes when the position closes.</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// What the strategy needs of the account it trades on.
/// </summary>
public sealed record AccountSettings
{
    /// <summary>
    /// The leverage this strategy is written for. It is a requirement, not a request: the venue is what grants
    /// leverage, and a run whose venue grants less than this refuses to start rather than quietly running a strategy
    /// written for ten times its size at one. 1 means no leverage and is what a document that says nothing gets.
    /// </summary>
    public decimal Leverage { get; init; } = 1m;
}

public sealed record ModeSettings
{
    public LiveModeSettings Live { get; init; } = new();
}

public sealed record LiveModeSettings
{
    /// <summary>An annotation a model classified may gate a live strategy only with this explicit opt-in.</summary>
    public bool AllowAiAnnotationConditions { get; init; }
}

/// <summary>
/// A numeric value that is either a literal or a reference to a document parameter (<c>{"$param": "name"}</c>).
/// </summary>
[JsonConverter(typeof(ParamValueJsonConverter))]
public readonly record struct ParamValue
{
    public ParamValue(decimal literal)
    {
        Literal = literal;
        ParameterName = null;
    }

    public ParamValue(string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterName);
        Literal = null;
        ParameterName = parameterName;
    }

    public decimal? Literal { get; }

    public string? ParameterName { get; }

    public bool IsParameter => ParameterName is not null;

    public static implicit operator ParamValue(decimal value) => new(value);

    public static implicit operator ParamValue(int value) => new((decimal)value);

    public decimal Resolve(IReadOnlyDictionary<string, decimal> parameters)
    {
        if (Literal is { } literal)
        {
            return literal;
        }

        return parameters.TryGetValue(ParameterName!, out decimal value)
            ? value
            : throw new InvalidOperationException($"Parameter '{ParameterName}' is not defined.");
    }

    public override string ToString() => Literal is { } l ? l.ToString(System.Globalization.CultureInfo.InvariantCulture) : "$" + ParameterName;
}

public sealed class ParamValueJsonConverter : JsonConverter<ParamValue>
{
    public override ParamValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return new ParamValue(reader.GetDecimal());
            case JsonTokenType.String:
                return new ParamValue(decimal.Parse(reader.GetString()!, System.Globalization.CultureInfo.InvariantCulture));
            case JsonTokenType.StartObject:
                {
                    using JsonDocument doc = JsonDocument.ParseValue(ref reader);
                    if (doc.RootElement.TryGetProperty("$param", out JsonElement p) && p.ValueKind == JsonValueKind.String)
                    {
                        return new ParamValue(p.GetString()!);
                    }

                    throw new JsonException("A parameter reference must be {\"$param\": \"name\"}.");
                }

            default:
                throw new JsonException($"Unexpected token {reader.TokenType} for a numeric value.");
        }
    }

    public override void Write(Utf8JsonWriter writer, ParamValue value, JsonSerializerOptions options)
    {
        if (value.IsParameter)
        {
            writer.WriteStartObject();
            writer.WriteString("$param", value.ParameterName);
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteStringValue(value.Literal!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
