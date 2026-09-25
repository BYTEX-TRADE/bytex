using System.Globalization;
using System.Text.Json;
using Bytex.Documents.Runtime;

namespace Bytex.Documents.Catalog;

public enum NodeKind
{
    Data,
    Indicator,
    Level,
    Condition,
    Action,
    Risk,
    Flow,
    Event,
    Custom,
}

/// <summary>The kind of value carried by a port. Edges must connect compatible kinds.</summary>
public enum ValueKind
{
    /// <summary>A bar stream; the value is the current bar of the source bar type.</summary>
    Bars,

    /// <summary>A decimal per bar (indicator output, level, computed number).</summary>
    Series,

    /// <summary>A price; interchangeable with Series for wiring.</summary>
    Price,

    /// <summary>A quantity; interchangeable with Series for wiring.</summary>
    Quantity,

    /// <summary>A boolean condition, evaluated every bar.</summary>
    Bool,

    /// <summary>A one-bar pulse (fill, close, timer); true only on the bar it happened.</summary>
    Pulse,

    /// <summary>A position handle (the position id, or null when flat).</summary>
    Position,
}

[Flags]
public enum NodeModes
{
    None = 0,
    Lab = 1,
    Paper = 2,
    Live = 4,
    All = Lab | Paper | Live,
}

public sealed record PortSpec(string Name, ValueKind Kind, bool Required = false, string? Label = null, string? Description = null)
{
    public static bool Compatible(ValueKind from, ValueKind to)
    {
        if (from == to)
        {
            return true;
        }

        bool Numeric(ValueKind k) => k is ValueKind.Series or ValueKind.Price or ValueKind.Quantity;
        if (Numeric(from) && Numeric(to))
        {
            return true;
        }

        // A pulse can be consumed anywhere a bool is expected.
        return from == ValueKind.Pulse && to == ValueKind.Bool;
    }
}

public enum ParamType
{
    Int,
    Decimal,
    Bool,
    String,
    Enum,
    Object,
    Instrument,
    BarType,
    Time,
}

/// <summary>
/// One option of an <see cref="ParamType.Enum"/> parameter, and what picking it means. A choice that decides how the
/// number beside it is read names that parameter in <see cref="Governs"/> and says what the number is then measured in,
/// so nothing about the option is left to be inferred from its identifier.
/// </summary>
public sealed record ChoiceSpec
{
    /// <summary>The value stored in the document.</summary>
    public required string Value { get; init; }

    /// <summary>What to show in its place. A name, never the identifier.</summary>
    public required string Label { get; init; }

    /// <summary>One sentence saying what this choice does, in the words a trader would use.</summary>
    public required string Description { get; init; }

    /// <summary>What <see cref="Governs"/> is measured in once this choice is picked.</summary>
    public string? Unit { get; init; }

    /// <summary>The sibling parameter whose meaning this choice decides, by name.</summary>
    public string? Governs { get; init; }

    /// <summary>Where the governed parameter should start under this choice, and the range and step it should take there. Editor hints; the governed parameter's own bounds are what the validator enforces.</summary>
    public string? Default { get; init; }

    /// <inheritdoc cref="Default"/>
    public string? Min { get; init; }

    /// <inheritdoc cref="Default"/>
    public string? Max { get; init; }

    /// <inheritdoc cref="Default"/>
    public string? Step { get; init; }
}

/// <summary>Describes one node parameter, its constraints, and the hints the builder and the AI use to edit it.</summary>
public sealed record ParamSpec
{
    public required string Name { get; init; }

    public ParamType Type { get; init; } = ParamType.Decimal;

    public bool Required { get; init; }

    public string? Default { get; init; }

    public string? Min { get; init; }

    public string? Max { get; init; }

    public string? Step { get; init; }

    public string? Unit { get; init; }

    /// <summary>For <see cref="ParamType.Enum"/>: every option, each carrying its own meaning.</summary>
    public IReadOnlyList<ChoiceSpec>? Choices { get; init; }

    public string? Label { get; init; }

    public string? Description { get; init; }

    /// <summary>Nested fields for <see cref="ParamType.Object"/>.</summary>
    public IReadOnlyList<ParamSpec>? Fields { get; init; }

    /// <summary>Numeric parameters may be bound to a document parameter with {"$param": name}.</summary>
    public bool IsNumeric => Type is ParamType.Int or ParamType.Decimal;

    /// <summary>The values an enum parameter accepts, in the order they are offered.</summary>
    public IReadOnlyList<string> ChoiceValues => Choices is null ? [] : [.. Choices.Select(c => c.Value)];

    /// <summary>The option stored as <paramref name="value"/>, if this parameter offers it.</summary>
    public ChoiceSpec? Choice(string value) => Choices?.FirstOrDefault(c => string.Equals(c.Value, value, StringComparison.Ordinal));
}

/// <summary>Everything the palette, inspector, validator, runtime, and the AI need to know about one node type.</summary>
public sealed record NodeTypeDescriptor
{
    public required string Type { get; init; }

    public required NodeKind Kind { get; init; }

    public required string DisplayName { get; init; }

    public string? Description { get; init; }

    /// <summary>Plain-language face, with {param} and {input} placeholders. Rendered on the node card.</summary>
    public required string FaceTemplate { get; init; }

    public IReadOnlyList<PortSpec> Inputs { get; init; } = [];

    public IReadOnlyList<PortSpec> Outputs { get; init; } = [];

    public IReadOnlyList<ParamSpec> Params { get; init; } = [];

    public NodeModes Modes { get; init; } = NodeModes.All;

    /// <summary>
    /// Whether this node changes an order after it has been placed, rather than only placing or cancelling one.
    /// <para>
    /// Declared because not every venue allows it. A venue family states whether it amends in its own capabilities, and
    /// a host validating a document against a venue needs both halves to say anything useful: which nodes in the
    /// document would amend, and whether the family they would run against permits it. Without this the host has to
    /// hard-code the engine's amending node types, which is a list that goes stale the day another one is added - and
    /// silently, because a document full of amending nodes is perfectly valid until it meets the wrong venue.
    /// </para>
    /// <para>
    /// What it costs to be wrong is not a failed request. A node that amends on a venue that refuses amendments leaves
    /// a protective order at the size it was first placed at, so a position that grows keeps a stop sized for the
    /// smaller one - still open, still at the right price, and covering less than it claims.
    /// </para>
    /// </summary>
    public bool AmendsOrders { get; init; }

    /// <summary>Runtime factory; null for types the engine cannot run (a plugin must supply them).</summary>
    public Func<NodeBuildContext, INodeEvaluator>? Factory { get; init; }

    public PortSpec? Input(string name) => Inputs.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    public PortSpec? Output(string name) => Outputs.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    public ParamSpec? Param(string name) => Params.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));
}

/// <summary>
/// The registry of node types. <see cref="Default"/> holds the built-in catalog; plugins can register more through <see cref="Register"/>.
/// </summary>
public sealed class NodeCatalog
{
    private readonly Dictionary<string, NodeTypeDescriptor> _types = new(StringComparer.Ordinal);

    public NodeCatalog()
    {
    }

    public NodeCatalog(IEnumerable<NodeTypeDescriptor> descriptors)
    {
        foreach (NodeTypeDescriptor descriptor in descriptors)
        {
            Register(descriptor);
        }
    }

    /// <summary>The built-in catalog, version 1.</summary>
    public static NodeCatalog Default { get; } = BuiltinNodes.Create();

    public const string CatalogVersion = "1.0";

    public IReadOnlyCollection<NodeTypeDescriptor> Types => _types.Values;

    public NodeTypeDescriptor? Find(string type) => _types.GetValueOrDefault(type);

    public bool Contains(string type) => _types.ContainsKey(type);

    public void Register(NodeTypeDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (_types.ContainsKey(descriptor.Type))
        {
            throw new InvalidOperationException($"Node type '{descriptor.Type}' is already registered.");
        }

        _types[descriptor.Type] = descriptor;
    }

    /// <summary>Exports the catalog as JSON for the builder palette and the AI tools.</summary>
    public string ExportJson(bool indented = true)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = indented }))
        {
            writer.WriteStartObject();
            writer.WriteString("catalogVersion", CatalogVersion);
            writer.WritePropertyName("types");
            writer.WriteStartArray();
            foreach (NodeTypeDescriptor d in _types.Values.OrderBy(t => t.Kind).ThenBy(t => t.Type, StringComparer.Ordinal))
            {
                WriteDescriptor(writer, d);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteDescriptor(Utf8JsonWriter w, NodeTypeDescriptor d)
    {
        w.WriteStartObject();
        w.WriteString("type", d.Type);
        w.WriteString("kind", d.Kind.ToString().ToLowerInvariant());
        w.WriteString("displayName", d.DisplayName);
        if (d.Description is not null)
        {
            w.WriteString("description", d.Description);
        }

        w.WriteString("face", d.FaceTemplate);
        w.WritePropertyName("modes");
        w.WriteStartArray();
        foreach (NodeModes m in new[] { NodeModes.Lab, NodeModes.Paper, NodeModes.Live })
        {
            if (d.Modes.HasFlag(m))
            {
                w.WriteStringValue(m.ToString().ToLowerInvariant());
            }
        }

        w.WriteEndArray();
        w.WritePropertyName("inputs");
        WritePorts(w, d.Inputs);
        w.WritePropertyName("outputs");
        WritePorts(w, d.Outputs);
        w.WriteBoolean("runnable", d.Factory is not null);

        // Written always rather than only when true: a host reading this to validate a document against a venue has
        // to be able to tell "this node does not amend" from "this engine is too old to say".
        w.WriteBoolean("amendsOrders", d.AmendsOrders);
        w.WritePropertyName("params");
        WriteParams(w, d.Params);
        w.WriteEndObject();
    }

    private static void WritePorts(Utf8JsonWriter w, IReadOnlyList<PortSpec> ports)
    {
        w.WriteStartArray();
        foreach (PortSpec p in ports)
        {
            w.WriteStartObject();
            w.WriteString("name", p.Name);
            w.WriteString("kind", p.Kind.ToString().ToLowerInvariant());
            w.WriteBoolean("required", p.Required);
            if (p.Label is not null)
            {
                w.WriteString("label", p.Label);
            }

            if (p.Description is not null)
            {
                w.WriteString("description", p.Description);
            }

            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    internal static void WriteParams(Utf8JsonWriter w, IReadOnlyList<ParamSpec> specs)
    {
        w.WriteStartArray();
        foreach (ParamSpec p in specs)
        {
            w.WriteStartObject();
            w.WriteString("name", p.Name);
            w.WriteString("type", p.Type.ToString().ToLowerInvariant());
            w.WriteBoolean("required", p.Required);
            if (p.Default is not null)
            {
                w.WriteString("default", p.Default);
            }

            if (p.Min is not null)
            {
                w.WriteString("min", p.Min);
            }

            if (p.Max is not null)
            {
                w.WriteString("max", p.Max);
            }

            if (p.Step is not null)
            {
                w.WriteString("step", p.Step);
            }

            if (p.Unit is not null)
            {
                w.WriteString("unit", p.Unit);
            }

            if (p.Label is not null)
            {
                w.WriteString("label", p.Label);
            }

            if (p.Description is not null)
            {
                w.WriteString("description", p.Description);
            }

            if (p.Choices is not null)
            {
                w.WritePropertyName("choices");
                w.WriteStartArray();
                foreach (ChoiceSpec c in p.Choices)
                {
                    w.WriteStartObject();
                    w.WriteString("value", c.Value);
                    w.WriteString("label", c.Label);
                    w.WriteString("description", c.Description);
                    if (c.Unit is not null)
                    {
                        w.WriteString("unit", c.Unit);
                    }

                    if (c.Governs is not null)
                    {
                        w.WriteString("governs", c.Governs);
                    }

                    if (c.Default is not null)
                    {
                        w.WriteString("default", c.Default);
                    }

                    if (c.Min is not null)
                    {
                        w.WriteString("min", c.Min);
                    }

                    if (c.Max is not null)
                    {
                        w.WriteString("max", c.Max);
                    }

                    if (c.Step is not null)
                    {
                        w.WriteString("step", c.Step);
                    }

                    w.WriteEndObject();
                }

                w.WriteEndArray();
            }

            if (p.Fields is not null)
            {
                w.WritePropertyName("fields");
                WriteParams(w, p.Fields);
            }

            w.WriteEndObject();
        }

        w.WriteEndArray();
    }
}

/// <summary>Terse builders for parameter specs, used by the built-in catalog.</summary>
internal static class P
{
    /// <summary>
    /// The largest whole number a node parameter takes unless its own spec says otherwise: a lookback or a count
    /// beyond this is a mistake rather than a strategy.
    /// </summary>
    private const int MaxWholeNumber = 10_000;

    public static ParamSpec Int(string name, int @default, int min = 1, int max = MaxWholeNumber, string? label = null, string? unit = null, string? description = null) => new()
    {
        Name = name,
        Type = ParamType.Int,
        Default = @default.ToString(CultureInfo.InvariantCulture),
        Min = min.ToString(CultureInfo.InvariantCulture),
        Max = max.ToString(CultureInfo.InvariantCulture),
        Label = label,
        Unit = unit,
        Description = description,
    };

    public static ParamSpec Dec(string name, decimal @default, decimal? min = null, decimal? max = null, decimal? step = null, string? label = null, string? unit = null, string? description = null) => new()
    {
        Name = name,
        Type = ParamType.Decimal,
        Default = @default.ToString(CultureInfo.InvariantCulture),
        Min = min?.ToString(CultureInfo.InvariantCulture),
        Max = max?.ToString(CultureInfo.InvariantCulture),
        Step = step?.ToString(CultureInfo.InvariantCulture),
        Label = label,
        Unit = unit,
        Description = description,
    };

    public static ParamSpec Bool(string name, bool @default, string? label = null, string? description = null) => new()
    {
        Name = name,
        Type = ParamType.Bool,
        Default = @default ? "true" : "false",
        Label = label,
        Description = description,
    };

    public static ParamSpec Str(string name, string? @default = null, bool required = false, string? label = null, string? description = null) => new()
    {
        Name = name,
        Type = ParamType.String,
        Default = @default,
        Required = required,
        Label = label,
        Description = description,
    };

    /// <summary>
    /// One option of an enum parameter. <paramref name="governs"/> names the sibling parameter whose meaning this choice
    /// decides, and <paramref name="unit"/> says what that number is then measured in; a choice that governs nothing
    /// needs neither.
    /// </summary>
    public static ChoiceSpec Choice(string value, string label, string description, string? unit = null, string? governs = null, string? @default = null, string? min = null, string? max = null, string? step = null) => new()
    {
        Value = value,
        Label = label,
        Description = description,
        Unit = unit,
        Governs = governs,
        Default = @default,
        Min = min,
        Max = max,
        Step = step,
    };

    public static ParamSpec Enum(string name, string @default, string? label = null, string? description = null, params ChoiceSpec[] choices) => new()
    {
        Name = name,
        Type = ParamType.Enum,
        Default = @default,
        Choices = choices,
        Label = label,
        Description = description,
    };

    public static ParamSpec Obj(string name, bool required = false, string? label = null, string? description = null, params ParamSpec[] fields) => new()
    {
        Name = name,
        Type = ParamType.Object,
        Required = required,
        Fields = fields,
        Label = label,
        Description = description,
    };

    public static ParamSpec BarType(string name = "barType", string? label = null) => new()
    {
        Name = name,
        Type = ParamType.BarType,
        Required = true,
        Label = label ?? "Bars",
        Description = "A bar type reference declared in the document's barTypes.",
    };

    public static ParamSpec Instrument(string name = "instrument", string? label = null) => new()
    {
        Name = name,
        Type = ParamType.Instrument,
        Required = false,
        Label = label ?? "Instrument",
        Description = "An instrument reference declared in the document's instruments; defaults to the primary instrument.",
    };

    public static ParamSpec Time(string name, string @default, string? label = null, string? description = null) => new()
    {
        Name = name,
        Type = ParamType.Time,
        Default = @default,
        Label = label,
        Description = description ?? "UTC time of day, HH:mm.",
    };
}
