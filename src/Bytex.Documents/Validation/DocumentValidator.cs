using System.Globalization;
using System.Text.Json;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Documents.Catalog;
using Bytex.Documents.Runtime;
using Bytex.Documents.Schema;

namespace Bytex.Documents.Validation;

public enum FindingLevel
{
    Info,
    Warning,
    Block,
}

public sealed record Finding(FindingLevel Level, string Code, string Message, string? NodeId = null, string? Port = null, string? Fix = null)
{
    public override string ToString() => $"{Level.ToString().ToUpperInvariant()} {Code}{(NodeId is null ? string.Empty : " [" + NodeId + "]")}: {Message}";
}

public sealed record ValidationReport(IReadOnlyList<Finding> Findings, string StoppedAt)
{
    public IEnumerable<Finding> Blocks => Findings.Where(f => f.Level == FindingLevel.Block);

    public IEnumerable<Finding> Warnings => Findings.Where(f => f.Level == FindingLevel.Warning);

    public IEnumerable<Finding> Infos => Findings.Where(f => f.Level == FindingLevel.Info);

    public bool IsValid => !Blocks.Any();

    public static ValidationReport Empty { get; } = new([], "context");
}

public sealed record ValidatorOptions
{
    public int MaxNodes { get; init; } = 200;

    public int MaxEdges { get; init; } = 400;

    public int MaxPhases { get; init; } = 8;

    public int MaxParameters { get; init; } = 32;

    public int MaxLookback { get; init; } = 5000;

    public static ValidatorOptions Default { get; } = new();
}

/// <summary>What the context layer needs from the outside world: instruments and available data.</summary>
public interface IValidationContext
{
    /// <summary>False when the context cannot look instruments up at all; the context layer is then skipped rather than failed.</summary>
    bool ProvidesInstruments { get; }

    Instrument? Instrument(InstrumentId id);

    (UnixNanos Start, UnixNanos End)? DataRange(BarType barType);

    TradingEnvironment TargetEnvironment { get; }
}

public sealed class EmptyValidationContext : IValidationContext
{
    public EmptyValidationContext(TradingEnvironment target = TradingEnvironment.Backtest)
    {
        TargetEnvironment = target;
    }

    public TradingEnvironment TargetEnvironment { get; }

    public bool ProvidesInstruments => false;

    public Instrument? Instrument(InstrumentId id) => null;

    public (UnixNanos Start, UnixNanos End)? DataRange(BarType barType) => null;
}

public static class Codes
{
    public const string SchemaVersion = "SCHEMA_VERSION";
    public const string NameMissing = "NAME_MISSING";
    public const string InstrumentMissing = "INSTRUMENT_MISSING";
    public const string InstrumentIdInvalid = "INSTRUMENT_ID_INVALID";
    public const string BarTypeMissing = "BARTYPE_MISSING";
    public const string BarTypeInvalid = "BARTYPE_INVALID";
    public const string ParameterDuplicate = "PARAMETER_DUPLICATE";
    public const string ParameterInvalid = "PARAMETER_INVALID";
    public const string ParameterUnknown = "PARAMETER_UNKNOWN";
    public const string NodeIdDuplicate = "NODE_ID_DUPLICATE";
    public const string NodeTypeUnknown = "NODE_TYPE_UNKNOWN";
    public const string NodeNotRunnable = "NODE_NOT_RUNNABLE";
    public const string LeverageInvalid = "LEVERAGE_INVALID";
    public const string ParamMissing = "PARAM_MISSING";
    public const string ParamInvalid = "PARAM_INVALID";
    public const string ParamUnknown = "PARAM_UNKNOWN";
    public const string ParamOutOfRange = "PARAM_OUT_OF_RANGE";
    public const string EdgeDangling = "EDGE_DANGLING";
    public const string PortUnknown = "PORT_UNKNOWN";
    public const string PortTypeMismatch = "PORT_TYPE_MISMATCH";
    public const string InputMultiple = "INPUT_MULTIPLE";
    public const string InputRequired = "INPUT_REQUIRED";
    public const string GraphCycle = "GRAPH_CYCLE";
    public const string CapExceeded = "CAP_EXCEEDED";
    public const string PhaseDuplicate = "PHASE_DUPLICATE";
    public const string PhaseNodeUnknown = "PHASE_NODE_UNKNOWN";
    public const string PhaseInitial = "PHASE_INITIAL";
    public const string TransitionInvalid = "TRANSITION_INVALID";
    public const string NoEntry = "NO_ENTRY";
    public const string NoExit = "NO_EXIT";
    public const string Contradiction = "CONTRADICTION";
    public const string UnreachablePhase = "UNREACHABLE_PHASE";
    public const string TargetInsideStop = "TARGET_INSIDE_STOP";
    public const string SizingNeedsStop = "SIZING_NEEDS_STOP";
    public const string OrderCannotBeWorked = "ORDER_CANNOT_BE_WORKED";
    public const string DisconnectedNode = "DISCONNECTED_NODE";
    public const string NodeModeNotAllowed = "NODE_MODE_NOT_ALLOWED";
    public const string LiveAiEventCondition = "LIVE_AI_EVENT_CONDITION";
    public const string LookbackTooLong = "LOOKBACK_TOO_LONG";
    public const string InstrumentUnknown = "INSTRUMENT_UNKNOWN";
    public const string Precision = "PRECISION";
    public const string MinQuantity = "MIN_QUANTITY";
    public const string MinNotional = "MIN_NOTIONAL";
    public const string ShortOnSpot = "SHORT_ON_SPOT";
    public const string DataRangeInsufficient = "DATA_RANGE_INSUFFICIENT";
}

/// <summary>
/// Validates a document in three layers: structural (schema and graph), semantic (does the strategy make sense),
/// and context (instruments and data). Findings carry reason codes so the builder and the assistant can act on them.
/// </summary>
public sealed class DocumentValidator
{
    private static readonly HashSet<string> EntryTypes = new(StringComparer.Ordinal) { "act.order", "act.bracket", "act.ladder", "act.grid" };
    private static readonly HashSet<string> ExitTypes = new(StringComparer.Ordinal) { "risk.exit", "act.bracket", "act.close", "act.trail", "act.moveStop", "act.grid", "flow.completeRun" };

    private readonly NodeCatalog _catalog;
    private readonly ValidatorOptions _options;

    public DocumentValidator(NodeCatalog? catalog = null, ValidatorOptions? options = null)
    {
        _catalog = catalog ?? NodeCatalog.Default;
        _options = options ?? ValidatorOptions.Default;
    }

    public ValidationReport Validate(StrategyDocument document, IValidationContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        context ??= new EmptyValidationContext();
        List<Finding> findings = new();

        Structural(document, findings);
        if (findings.Any(f => f.Level == FindingLevel.Block))
        {
            return new ValidationReport(findings, "structural");
        }

        Semantic(document, context, findings);
        if (findings.Any(f => f.Level == FindingLevel.Block))
        {
            return new ValidationReport(findings, "semantic");
        }

        Context(document, context, findings);
        return new ValidationReport(findings, "context");
    }

    // ----- Structural -----

    private void Structural(StrategyDocument doc, List<Finding> f)
    {
        if (!string.Equals(doc.SchemaVersion, StrategyDocument.CurrentSchemaVersion, StringComparison.Ordinal) && !doc.SchemaVersion.StartsWith("1.", StringComparison.Ordinal))
        {
            f.Add(new Finding(FindingLevel.Block, Codes.SchemaVersion, $"Schema version '{doc.SchemaVersion}' is not supported; this engine reads 1.x.", Fix: "Open the document in a newer host, or set schemaVersion to 1.0."));
        }

        if (string.IsNullOrWhiteSpace(doc.Name))
        {
            f.Add(new Finding(FindingLevel.Block, Codes.NameMissing, "The strategy has no name."));
        }

        if (doc.Instruments.Count == 0)
        {
            f.Add(new Finding(FindingLevel.Block, Codes.InstrumentMissing, "The document declares no instrument.", Fix: "Add an instrument such as BTCUSDT-PERP.BYBIT."));
        }

        if (doc.Account.Leverage < 1m)
        {
            f.Add(new Finding(
                FindingLevel.Block,
                Codes.LeverageInvalid,
                $"Leverage {doc.Account.Leverage} is below 1, which is not leverage but a fraction of the account.",
                Fix: "Set account.leverage to 1 for no leverage, or to the multiple the strategy is written for."));
        }

        foreach (InstrumentRef i in doc.Instruments)
        {
            if (!TryParseInstrument(i.InstrumentId))
            {
                f.Add(new Finding(FindingLevel.Block, Codes.InstrumentIdInvalid, $"Instrument '{i.InstrumentId}' is not a valid id (expected SYMBOL.VENUE)."));
            }
        }

        if (doc.BarTypes.Count == 0)
        {
            f.Add(new Finding(FindingLevel.Block, Codes.BarTypeMissing, "The document declares no bar type; the strategy has nothing to evaluate on.", Fix: "Add a bar type, for example 15-minute last-price bars."));
        }

        foreach (BarTypeRef b in doc.BarTypes)
        {
            if (doc.Instrument(b.Instrument) is null)
            {
                f.Add(new Finding(FindingLevel.Block, Codes.BarTypeInvalid, $"Bar type '{b.Ref}' refers to unknown instrument '{b.Instrument}'."));
            }

            if (!Enum.TryParse<BarAggregation>(b.Aggregation, true, out _) || !Enum.TryParse<PriceType>(b.PriceType, true, out _) || !Enum.TryParse<AggregationSource>(b.Source, true, out _) || b.Step <= 0)
            {
                f.Add(new Finding(FindingLevel.Block, Codes.BarTypeInvalid, $"Bar type '{b.Ref}' has an invalid step, aggregation, price type, or source."));
            }
        }

        HashSet<string> parameterNames = new(StringComparer.Ordinal);
        foreach (ParameterDef p in doc.Parameters)
        {
            if (!parameterNames.Add(p.Name))
            {
                f.Add(new Finding(FindingLevel.Block, Codes.ParameterDuplicate, $"Parameter '{p.Name}' is declared twice."));
            }

            if (p.Type is "decimal" or "int")
            {
                if (!TryDec(p.Value, out decimal v))
                {
                    f.Add(new Finding(FindingLevel.Block, Codes.ParameterInvalid, $"Parameter '{p.Name}' has a non-numeric value '{p.Value}'."));
                }
                else
                {
                    if (p.Min is not null && TryDec(p.Min, out decimal min) && v < min)
                    {
                        f.Add(new Finding(FindingLevel.Block, Codes.ParameterInvalid, $"Parameter '{p.Name}' value {p.Value} is below its minimum {p.Min}."));
                    }

                    if (p.Max is not null && TryDec(p.Max, out decimal max) && v > max)
                    {
                        f.Add(new Finding(FindingLevel.Block, Codes.ParameterInvalid, $"Parameter '{p.Name}' value {p.Value} is above its maximum {p.Max}."));
                    }
                }
            }
        }

        if (doc.Parameters.Count > _options.MaxParameters)
        {
            f.Add(new Finding(FindingLevel.Block, Codes.CapExceeded, $"{doc.Parameters.Count} parameters exceed the cap of {_options.MaxParameters}."));
        }

        if (doc.Nodes.Count > _options.MaxNodes)
        {
            f.Add(new Finding(FindingLevel.Block, Codes.CapExceeded, $"{doc.Nodes.Count} nodes exceed the cap of {_options.MaxNodes}."));
        }

        if (doc.Edges.Count > _options.MaxEdges)
        {
            f.Add(new Finding(FindingLevel.Block, Codes.CapExceeded, $"{doc.Edges.Count} edges exceed the cap of {_options.MaxEdges}."));
        }

        if (doc.Phases.Count > _options.MaxPhases)
        {
            f.Add(new Finding(FindingLevel.Block, Codes.CapExceeded, $"{doc.Phases.Count} phases exceed the cap of {_options.MaxPhases}."));
        }

        Dictionary<string, NodeDef> nodes = new(StringComparer.Ordinal);
        Dictionary<string, NodeTypeDescriptor> descriptors = new(StringComparer.Ordinal);
        foreach (NodeDef n in doc.Nodes)
        {
            if (!nodes.TryAdd(n.Id, n))
            {
                f.Add(new Finding(FindingLevel.Block, Codes.NodeIdDuplicate, $"Node id '{n.Id}' is used twice.", n.Id));
                continue;
            }

            NodeTypeDescriptor? d = _catalog.Find(n.Type);
            if (d is null)
            {
                f.Add(new Finding(FindingLevel.Block, Codes.NodeTypeUnknown, $"Node '{n.Id}' has unknown type '{n.Type}'.", n.Id, Fix: "Pick a type from the catalog, or load the plugin that provides it."));
                continue;
            }

            if (d.Factory is null)
            {
                // Described but not buildable here: a plugin has to supply the type before a document naming it runs.
                f.Add(new Finding(FindingLevel.Block, Codes.NodeNotRunnable, $"Node '{n.Id}' of type '{n.Type}' has no runtime in this engine.", n.Id));
            }

            descriptors[n.Id] = d;
            ValidateParams(doc, n, d, parameterNames, f);
        }

        // Edges
        Dictionary<string, Dictionary<string, string>> inputs = new(StringComparer.Ordinal);
        Dictionary<string, List<string>> adjacency = new(StringComparer.Ordinal);
        foreach (EdgeDef e in doc.Edges)
        {
            (string fromNode, string fromPort) = EdgeDef.Split(e.From);
            (string toNode, string toPort) = EdgeDef.Split(e.To);
            if (!descriptors.TryGetValue(fromNode, out NodeTypeDescriptor? fromDesc) || !descriptors.TryGetValue(toNode, out NodeTypeDescriptor? toDesc))
            {
                f.Add(new Finding(FindingLevel.Block, Codes.EdgeDangling, $"Edge {e.From} → {e.To} refers to a node that does not exist."));
                continue;
            }

            PortSpec? output = fromDesc.Output(fromPort);
            PortSpec? input = toDesc.Input(toPort);
            if (output is null)
            {
                f.Add(new Finding(FindingLevel.Block, Codes.PortUnknown, $"Node '{fromNode}' ({fromDesc.Type}) has no output '{fromPort}'.", fromNode, fromPort));
                continue;
            }

            if (input is null)
            {
                f.Add(new Finding(FindingLevel.Block, Codes.PortUnknown, $"Node '{toNode}' ({toDesc.Type}) has no input '{toPort}'.", toNode, toPort));
                continue;
            }

            if (!PortSpec.Compatible(output.Kind, input.Kind))
            {
                f.Add(new Finding(FindingLevel.Block, Codes.PortTypeMismatch, $"Cannot wire {output.Kind} output {e.From} into {input.Kind} input {e.To}.", toNode, toPort));
            }

            Dictionary<string, string> map = inputs.TryGetValue(toNode, out Dictionary<string, string>? existing) ? existing : inputs[toNode] = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!map.TryAdd(toPort, e.From))
            {
                f.Add(new Finding(FindingLevel.Block, Codes.InputMultiple, $"Input {e.To} has more than one incoming edge.", toNode, toPort, "Use an 'All of' or 'Any of' node to combine conditions."));
            }

            (adjacency.TryGetValue(fromNode, out List<string>? list) ? list : adjacency[fromNode] = new List<string>()).Add(toNode);
        }

        foreach ((string id, NodeTypeDescriptor d) in descriptors)
        {
            NodeDef node = nodes[id];
            if (node.Disabled)
            {
                continue;
            }

            foreach (PortSpec input in d.Inputs.Where(p => p.Required))
            {
                if (!inputs.TryGetValue(id, out Dictionary<string, string>? map) || !map.ContainsKey(input.Name))
                {
                    f.Add(new Finding(FindingLevel.Block, Codes.InputRequired, $"Node '{id}' ({d.DisplayName}) needs its '{input.Name}' input connected.", id, input.Name));
                }
            }
        }

        if (HasCycle(nodes.Keys, adjacency, out string? cycleNode))
        {
            f.Add(new Finding(FindingLevel.Block, Codes.GraphCycle, $"The graph contains a cycle through node '{cycleNode}'.", cycleNode, Fix: "Break the loop with a latch or a delay node."));
        }

        // Phases and transitions
        HashSet<string> phaseIds = new(StringComparer.Ordinal);
        int initial = 0;
        foreach (PhaseDef p in doc.Phases)
        {
            if (!phaseIds.Add(p.Id))
            {
                f.Add(new Finding(FindingLevel.Block, Codes.PhaseDuplicate, $"Phase id '{p.Id}' is used twice."));
            }

            if (p.Initial)
            {
                initial++;
            }

            foreach (string nodeId in p.Nodes)
            {
                if (!nodes.ContainsKey(nodeId))
                {
                    f.Add(new Finding(FindingLevel.Block, Codes.PhaseNodeUnknown, $"Phase '{p.Id}' lists unknown node '{nodeId}'."));
                }
            }
        }

        if (doc.Phases.Count > 0 && initial != 1)
        {
            f.Add(new Finding(FindingLevel.Block, Codes.PhaseInitial, "Exactly one phase must be marked initial.", Fix: "Mark the entry phase as initial."));
        }

        foreach (TransitionDef t in doc.Transitions)
        {
            if (!phaseIds.Contains(t.From) || !phaseIds.Contains(t.To))
            {
                f.Add(new Finding(FindingLevel.Block, Codes.TransitionInvalid, $"Transition {t.From} → {t.To} refers to an unknown phase."));
                continue;
            }

            (string onNode, string onPort) = EdgeDef.Split(t.On);
            if (!descriptors.TryGetValue(onNode, out NodeTypeDescriptor? d) || d.Output(onPort) is not { } port)
            {
                f.Add(new Finding(FindingLevel.Block, Codes.TransitionInvalid, $"Transition {t.From} → {t.To} fires on unknown port '{t.On}'."));
            }
            else if (port.Kind is not (ValueKind.Pulse or ValueKind.Bool))
            {
                f.Add(new Finding(FindingLevel.Block, Codes.TransitionInvalid, $"Transition {t.From} → {t.To} must fire on a pulse or condition port, not {port.Kind}.", onNode, onPort));
            }
        }
    }

    private void ValidateParams(StrategyDocument doc, NodeDef node, NodeTypeDescriptor d, HashSet<string> parameterNames, List<Finding> f)
    {
        JsonElement? raw = node.Params;
        if (raw is { ValueKind: not JsonValueKind.Object and not JsonValueKind.Undefined and not JsonValueKind.Null })
        {
            f.Add(new Finding(FindingLevel.Block, Codes.ParamInvalid, $"Node '{node.Id}' params must be an object.", node.Id));
            return;
        }

        ValidateParamObject(doc, node.Id, d.Params, raw, parameterNames, f, string.Empty);
    }

    private void ValidateParamObject(StrategyDocument doc, string nodeId, IReadOnlyList<ParamSpec> specs, JsonElement? raw, HashSet<string> parameterNames, List<Finding> f, string prefix)
    {
        foreach (ParamSpec spec in specs)
        {
            JsonElement? value = raw is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(spec.Name, out JsonElement v) && v.ValueKind != JsonValueKind.Null ? v : null;
            string path = prefix + spec.Name;
            if (value is null)
            {
                if (spec.Required && spec.Type != ParamType.Object)
                {
                    f.Add(new Finding(FindingLevel.Block, Codes.ParamMissing, $"Node '{nodeId}' is missing required parameter '{path}'.", nodeId));
                }
                else if (spec.Type == ParamType.Object && spec.Fields is not null)
                {
                    // Nested defaults apply; still check required nested fields when the object is required.
                    if (spec.Required)
                    {
                        ValidateParamObject(doc, nodeId, spec.Fields, null, parameterNames, f, path + ".");
                    }
                }

                continue;
            }

            switch (spec.Type)
            {
                case ParamType.Int:
                case ParamType.Decimal:
                    {
                        ParamValue? pv = DocumentJson.ReadParamValue(value.Value);
                        if (pv is null)
                        {
                            f.Add(new Finding(FindingLevel.Block, Codes.ParamInvalid, $"Node '{nodeId}' parameter '{path}' must be a number or a parameter reference.", nodeId));
                            break;
                        }

                        decimal? resolved = pv.Value.Literal;
                        if (pv.Value.IsParameter)
                        {
                            if (!parameterNames.Contains(pv.Value.ParameterName!))
                            {
                                f.Add(new Finding(FindingLevel.Block, Codes.ParameterUnknown, $"Node '{nodeId}' parameter '{path}' refers to unknown document parameter '{pv.Value.ParameterName}'.", nodeId));
                                break;
                            }

                            ParameterDef def = doc.Parameter(pv.Value.ParameterName!)!;
                            resolved = TryDec(def.Value, out decimal dv) ? dv : null;
                        }

                        if (resolved is { } r)
                        {
                            if (spec.Type == ParamType.Int && r != Math.Round(r))
                            {
                                f.Add(new Finding(FindingLevel.Block, Codes.ParamInvalid, $"Node '{nodeId}' parameter '{path}' must be a whole number.", nodeId));
                            }

                            if (spec.Min is not null && TryDec(spec.Min, out decimal min) && r < min)
                            {
                                f.Add(new Finding(FindingLevel.Block, Codes.ParamOutOfRange, $"Node '{nodeId}' parameter '{path}' = {DocumentJson.FormatDecimal(r)} is below the minimum {spec.Min}.", nodeId));
                            }

                            if (spec.Max is not null && TryDec(spec.Max, out decimal max) && r > max)
                            {
                                f.Add(new Finding(FindingLevel.Block, Codes.ParamOutOfRange, $"Node '{nodeId}' parameter '{path}' = {DocumentJson.FormatDecimal(r)} is above the maximum {spec.Max}.", nodeId));
                            }

                            if ((spec.Name is "lookback" or "period") && r > _options.MaxLookback)
                            {
                                f.Add(new Finding(FindingLevel.Block, Codes.LookbackTooLong, $"Node '{nodeId}' looks back {DocumentJson.FormatDecimal(r)} bars; the cap is {_options.MaxLookback}.", nodeId));
                            }
                        }

                        break;
                    }

                case ParamType.Bool:
                    if (value.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        f.Add(new Finding(FindingLevel.Block, Codes.ParamInvalid, $"Node '{nodeId}' parameter '{path}' must be true or false.", nodeId));
                    }

                    break;
                case ParamType.Enum:
                    {
                        // The choice may come from a document parameter, so that one switch flips every node that reads it.
                        if (TextReference(value.Value) is { } referenced)
                        {
                            ParameterDef? def = doc.Parameter(referenced);
                            if (def is null)
                            {
                                f.Add(new Finding(FindingLevel.Block, Codes.ParameterUnknown, $"Node '{nodeId}' parameter '{path}' refers to unknown document parameter '{referenced}'.", nodeId));
                            }
                            else if (def.Type != "enum")
                            {
                                f.Add(new Finding(FindingLevel.Block, Codes.ParamInvalid, $"Node '{nodeId}' parameter '{path}' refers to document parameter '{referenced}', which is a {def.Type}; a choice needs a parameter of type enum.", nodeId));
                            }
                            else if (spec.Choices is not null && spec.Choice(def.Value) is null)
                            {
                                f.Add(new Finding(FindingLevel.Block, Codes.ParamInvalid, $"Node '{nodeId}' parameter '{path}' refers to document parameter '{referenced}' = '{def.Value}', which is not one of: {string.Join(", ", spec.ChoiceValues)}.", nodeId));
                            }

                            break;
                        }

                        if (value.Value.ValueKind != JsonValueKind.String || (spec.Choices is not null && spec.Choice(value.Value.GetString()!) is null))
                        {
                            f.Add(new Finding(FindingLevel.Block, Codes.ParamInvalid, $"Node '{nodeId}' parameter '{path}' must be one of: {string.Join(", ", spec.ChoiceValues)}.", nodeId));
                        }

                        break;
                    }

                case ParamType.String:
                case ParamType.Time:
                    if (spec.Type == ParamType.String && TextReference(value.Value) is { } text)
                    {
                        ParameterDef? def = doc.Parameter(text);
                        if (def is null)
                        {
                            f.Add(new Finding(FindingLevel.Block, Codes.ParameterUnknown, $"Node '{nodeId}' parameter '{path}' refers to unknown document parameter '{text}'.", nodeId));
                        }
                        else if (def.Type is not ("enum" or "string"))
                        {
                            f.Add(new Finding(FindingLevel.Block, Codes.ParamInvalid, $"Node '{nodeId}' parameter '{path}' refers to document parameter '{text}', which is a {def.Type}; a text needs a parameter of type string or enum.", nodeId));
                        }
                    }
                    else if (value.Value.ValueKind != JsonValueKind.String)
                    {
                        f.Add(new Finding(FindingLevel.Block, Codes.ParamInvalid, $"Node '{nodeId}' parameter '{path}' must be a string.", nodeId));
                    }
                    else if (spec.Type == ParamType.Time && !TimeOnly.TryParseExact(value.Value.GetString(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    {
                        f.Add(new Finding(FindingLevel.Block, Codes.ParamInvalid, $"Node '{nodeId}' parameter '{path}' must be a UTC time HH:mm.", nodeId));
                    }

                    break;
                case ParamType.BarType:
                    if (value.Value.ValueKind != JsonValueKind.String || doc.BarType(value.Value.GetString()!) is null)
                    {
                        f.Add(new Finding(FindingLevel.Block, Codes.ParamInvalid, $"Node '{nodeId}' parameter '{path}' must reference a declared bar type.", nodeId, Fix: $"Declared: {string.Join(", ", doc.BarTypes.Select(b => b.Ref))}."));
                    }

                    break;
                case ParamType.Instrument:
                    if (value.Value.ValueKind != JsonValueKind.String || doc.Instrument(value.Value.GetString()!) is null)
                    {
                        f.Add(new Finding(FindingLevel.Block, Codes.ParamInvalid, $"Node '{nodeId}' parameter '{path}' must reference a declared instrument.", nodeId));
                    }

                    break;
                case ParamType.Object:
                    if (value.Value.ValueKind != JsonValueKind.Object)
                    {
                        f.Add(new Finding(FindingLevel.Block, Codes.ParamInvalid, $"Node '{nodeId}' parameter '{path}' must be an object.", nodeId));
                    }
                    else if (spec.Fields is not null)
                    {
                        ValidateParamObject(doc, nodeId, spec.Fields, value, parameterNames, f, path + ".");
                    }

                    break;
                default:
                    break;
            }
        }

        if (raw is { ValueKind: JsonValueKind.Object } obj)
        {
            foreach (JsonProperty prop in obj.EnumerateObject())
            {
                if (specs.All(s => !string.Equals(s.Name, prop.Name, StringComparison.Ordinal)))
                {
                    f.Add(new Finding(FindingLevel.Warning, Codes.ParamUnknown, $"Node '{nodeId}' has unknown parameter '{prefix}{prop.Name}'; it is ignored.", nodeId));
                }
            }
        }
    }

    private static bool HasCycle(IEnumerable<string> nodes, Dictionary<string, List<string>> adjacency, out string? offender)
    {
        Dictionary<string, int> state = nodes.ToDictionary(n => n, _ => 0, StringComparer.Ordinal);
        foreach (string start in state.Keys.ToList())
        {
            if (state[start] != 0)
            {
                continue;
            }

            Stack<(string Node, int Next)> stack = new();
            stack.Push((start, 0));
            state[start] = 1;
            while (stack.Count > 0)
            {
                (string node, int next) = stack.Pop();
                List<string> targets = adjacency.GetValueOrDefault(node) ?? new List<string>();
                if (next < targets.Count)
                {
                    stack.Push((node, next + 1));
                    string target = targets[next];
                    if (!state.TryGetValue(target, out int s) || s == 0)
                    {
                        state[target] = 1;
                        stack.Push((target, 0));
                    }
                    else if (s == 1)
                    {
                        offender = target;
                        return true;
                    }
                }
                else
                {
                    state[node] = 2;
                }
            }
        }

        offender = null;
        return false;
    }

    // ----- Semantic -----

    private void Semantic(StrategyDocument doc, IValidationContext context, List<Finding> f)
    {
        List<NodeDef> active = doc.Nodes.Where(n => !n.Disabled).ToList();
        if (!active.Any(n => EntryTypes.Contains(n.Type)))
        {
            f.Add(new Finding(FindingLevel.Block, Codes.NoEntry, "The strategy never places an order.", Fix: "Add a Place order, Bracket, Ladder, or Grid node."));
        }

        if (!active.Any(n => ExitTypes.Contains(n.Type)))
        {
            f.Add(new Finding(FindingLevel.Warning, Codes.NoExit, "No stop-loss or exit rule protects a position. The run can start only with explicit consent.", Fix: "Add an Exit & risk node fed by the order's position output."));
        }

        Dictionary<string, NodeDef> nodes = active.ToDictionary(n => n.Id, StringComparer.Ordinal);
        Dictionary<string, List<(string Port, string From)>> incoming = new(StringComparer.Ordinal);
        HashSet<string> touched = new(StringComparer.Ordinal);
        foreach (EdgeDef e in doc.Edges)
        {
            (string fromNode, _) = EdgeDef.Split(e.From);
            (string toNode, string toPort) = EdgeDef.Split(e.To);
            touched.Add(fromNode);
            touched.Add(toNode);
            (incoming.TryGetValue(toNode, out List<(string, string)>? list) ? list : incoming[toNode] = new List<(string, string)>()).Add((toPort, e.From));
        }

        foreach (NodeDef n in active)
        {
            NodeTypeDescriptor? d = _catalog.Find(n.Type);
            if (d is null)
            {
                continue;
            }

            if (!touched.Contains(n.Id) && d.Kind is not (NodeKind.Action or NodeKind.Risk or NodeKind.Flow))
            {
                f.Add(new Finding(FindingLevel.Info, Codes.DisconnectedNode, $"Node '{n.Id}' ({d.DisplayName}) is not connected to anything.", n.Id));
            }

            NodeModes required = context.TargetEnvironment switch { TradingEnvironment.Live => NodeModes.Live, TradingEnvironment.Sandbox => NodeModes.Paper, _ => NodeModes.Lab };
            if (!d.Modes.HasFlag(required))
            {
                f.Add(new Finding(FindingLevel.Block, Codes.NodeModeNotAllowed, $"Node '{n.Id}' ({d.DisplayName}) is not allowed in {context.TargetEnvironment} mode.", n.Id));
            }

            if (context.TargetEnvironment == TradingEnvironment.Live && (d.Kind == NodeKind.Event || n.Type == "risk.noEntryNear"))
            {
                NodeParams p = new(n.Params, new Dictionary<string, decimal>(StringComparer.Ordinal));
                if (p.Bool("includeAiClassified", false) && !doc.Modes.Live.AllowAiAnnotationConditions)
                {
                    f.Add(new Finding(FindingLevel.Block, Codes.LiveAiEventCondition, $"Node '{n.Id}' uses AI-classified events as a condition; in Live this needs the document's opt-in.", n.Id, Fix: "Set modes.live.allowAiAnnotationConditions to true (a warning is shown when the strategy goes live), or switch the node to deterministic feeds."));
                }
            }

            if (n.Type == "risk.exit")
            {
                NodeParams p = new(n.Params, ResolveParameters(doc, null), ResolveTextParameters(doc));
                NodeParams target = p.Obj("target");
                if (target.Str("unit", "r") == "r" && target.Dec("value", 2m) <= 0m)
                {
                    f.Add(new Finding(FindingLevel.Block, Codes.TargetInsideStop, $"Node '{n.Id}': a target of {DocumentJson.FormatDecimal(target.Dec("value", 2m))}R would sit at or behind the stop.", n.Id));
                }
            }

            if (n.Type is "act.order" or "act.ladder")
            {
                NodeParams p = new(n.Params, ResolveParameters(doc, null), ResolveTextParameters(doc));
                bool Wired(string port) => incoming.TryGetValue(n.Id, out List<(string Port, string From)>? ins) && ins.Any(i => i.Port == port);
                if (p.Obj("sizing").Str("mode", "fixed") == "riskPercent" && !Wired("quantity") && (n.Type == "act.ladder" || !Wired("stop")))
                {
                    f.Add(new Finding(FindingLevel.Block, Codes.SizingNeedsStop, $"Node '{n.Id}' sizes by risk percent but has no stop price to measure the risk against, so it can never produce a quantity.", n.Id,
                        Fix: n.Type == "act.ladder" ? "A ladder has no stop input: use another sizing mode or wire the quantity input." : "Wire a price into the node's stop input, or use another sizing mode."));
                }
            }

            if (n.Type == "act.order")
            {
                NodeParams p = new(n.Params, ResolveParameters(doc, null), ResolveTextParameters(doc));
                if (p.Obj(OrderWork.Param).Str(OrderWork.Algorithm, OrderWork.None) != OrderWork.None)
                {
                    string orderType = p.Str("orderType", "market");
                    if (!OrderWork.WorkableOrderTypes.Contains(orderType))
                    {
                        // An algorithm sends pieces at a pace; there is no pace for an order that is waiting for a price
                        // to be reached, and one cut into pieces before it triggers would not be the order at all.
                        f.Add(new Finding(FindingLevel.Block, Codes.OrderCannotBeWorked,
                            $"Node '{n.Id}' asks for a {orderType} order to be worked in pieces, which no execution algorithm can do.", n.Id,
                            Fix: "Work a market or a limit order, or send this one in one piece."));
                    }

                    if (p.Int("cancelAfterBars", 0) > 0)
                    {
                        // The instruction never reaches a venue, so there is nothing there to cancel: the horizon is
                        // what ends a worked order, and a document saying both would mean one of the two.
                        f.Add(new Finding(FindingLevel.Block, Codes.OrderCannotBeWorked,
                            $"Node '{n.Id}' both works its order in pieces and cancels it after bars; a worked order ends when its horizon is up and cannot be cancelled after bars.", n.Id,
                            Fix: "Set cancelAfterBars to 0 and let the horizon bound the order, or send the order in one piece."));
                    }
                }
            }

            if (n.Type is "cond.all")
            {
                CheckContradiction(doc, n, incoming, nodes, f);
            }

            if (d.Kind is NodeKind.Indicator or NodeKind.Level && d.Inputs.Any(i => i.Name == "bars" && i.Required))
            {
                // Handled structurally by InputRequired; nothing more here.
            }
        }

        if (doc.Phases.Count > 0)
        {
            HashSet<string> reachable = doc.Transitions.Select(t => t.To).ToHashSet(StringComparer.Ordinal);
            foreach (PhaseDef phase in doc.Phases.Where(p => !p.Initial && !reachable.Contains(p.Id)))
            {
                f.Add(new Finding(FindingLevel.Warning, Codes.UnreachablePhase, $"Phase '{phase.Id}' has no transition leading into it.", Fix: "Add a transition, or remove the phase."));
            }
        }
    }

    private void CheckContradiction(StrategyDocument doc, NodeDef all, Dictionary<string, List<(string Port, string From)>> incoming, Dictionary<string, NodeDef> nodes, List<Finding> f)
    {
        IReadOnlyDictionary<string, decimal> parameters = ResolveParameters(doc, null);
        List<(string Source, string Op, decimal Value, string NodeId)> compares = new();
        foreach ((_, string from) in incoming.GetValueOrDefault(all.Id) ?? new List<(string, string)>())
        {
            (string fromNode, _) = EdgeDef.Split(from);
            if (!nodes.TryGetValue(fromNode, out NodeDef? cmp) || cmp.Type != "cond.compare")
            {
                continue;
            }

            List<(string Port, string From)> cmpInputs = incoming.GetValueOrDefault(cmp.Id) ?? new List<(string, string)>();
            if (cmpInputs.Any(i => i.Port == "b"))
            {
                continue;
            }

            string? source = cmpInputs.FirstOrDefault(i => i.Port == "a").From;
            if (source is null)
            {
                continue;
            }

            NodeParams p = new(cmp.Params, parameters, ResolveTextParameters(doc));
            compares.Add((source, p.Str("op", "gt"), p.Dec("value", 0m), cmp.Id));
        }

        foreach (IGrouping<string, (string Source, string Op, decimal Value, string NodeId)> group in compares.GroupBy(c => c.Source, StringComparer.Ordinal))
        {
            decimal? lowerBound = group.Where(c => c.Op is "gt" or "gte").Select(c => (decimal?)c.Value).Max();
            decimal? upperBound = group.Where(c => c.Op is "lt" or "lte").Select(c => (decimal?)c.Value).Min();
            if (lowerBound is { } lo && upperBound is { } hi && lo >= hi)
            {
                f.Add(new Finding(FindingLevel.Block, Codes.Contradiction, $"Node '{all.Id}' requires {group.Key} to be above {DocumentJson.FormatDecimal(lo)} and below {DocumentJson.FormatDecimal(hi)} at once; it can never fire.", all.Id, Fix: "Loosen one of the comparisons."));
            }
        }
    }

    // ----- Context -----

    private void Context(StrategyDocument doc, IValidationContext context, List<Finding> f)
    {
        if (!context.ProvidesInstruments)
        {
            f.Add(new Finding(FindingLevel.Info, "CONTEXT_SKIPPED", "Instrument and data checks were skipped: no catalog or cache was supplied."));
            return;
        }

        Dictionary<string, Instrument?> instruments = new(StringComparer.Ordinal);
        foreach (InstrumentRef i in doc.Instruments)
        {
            Instrument? instrument = context.Instrument(InstrumentId.Parse(i.InstrumentId));
            instruments[i.Ref] = instrument;
            if (instrument is null)
            {
                f.Add(new Finding(FindingLevel.Block, Codes.InstrumentUnknown, $"Instrument {i.InstrumentId} is not in the catalog or cache.", Fix: "Fetch instrument definitions for the venue, or check the id."));
            }
        }

        if (instruments.Values.Any(i => i is null))
        {
            return;
        }

        IReadOnlyDictionary<string, decimal> parameters = ResolveParameters(doc, null);
        IReadOnlyDictionary<string, string> texts = ResolveTextParameters(doc);
        Instrument primary = instruments[doc.Instruments[0].Ref]!;
        int warmup = 0;
        foreach (NodeDef n in doc.Nodes.Where(n => !n.Disabled))
        {
            NodeParams p = new(n.Params, parameters, texts);
            Instrument instrument = p.StrOrNull("instrument") is { } r && instruments.TryGetValue(r, out Instrument? found) && found is not null ? found : primary;
            foreach (string key in new[] { "lookback", "period", "slow", "k" })
            {
                if (p.Has(key) && p.DecOrNull(key) is { } v && v > warmup && v < 1_000_000m)
                {
                    warmup = (int)v;
                }
            }

            if (n.Type == "level.pinned" && p.Dec("price", 0m) is { } price && price > 0m)
            {
                decimal remainder = price % instrument.PriceIncrement.Value;
                if (remainder != 0m)
                {
                    f.Add(new Finding(FindingLevel.Warning, Codes.Precision, $"Node '{n.Id}' price {DocumentJson.FormatDecimal(price)} is not a multiple of the tick {instrument.PriceIncrement}; it will be rounded.", n.Id));
                }
            }

            if (n.Type is "act.order" or "act.bracket" or "act.ladder" or "risk.sizing")
            {
                NodeParams sizing = p.Obj("sizing");
                string mode = sizing.Str("mode", "fixed");
                decimal value = sizing.Dec("value", 1m);
                if (mode == "fixed")
                {
                    if (instrument.MinQuantity is { } min && value < min.Value)
                    {
                        f.Add(new Finding(FindingLevel.Block, Codes.MinQuantity, $"Node '{n.Id}' size {DocumentJson.FormatDecimal(value)} is below the instrument minimum {min}.", n.Id));
                    }

                    if (value < instrument.SizeIncrement.Value)
                    {
                        f.Add(new Finding(FindingLevel.Block, Codes.MinQuantity, $"Node '{n.Id}' size {DocumentJson.FormatDecimal(value)} is below the size step {instrument.SizeIncrement}.", n.Id));
                    }
                }
                else if (mode == "notional" && instrument.MinNotional is { } minNotional && value < minNotional.Amount)
                {
                    f.Add(new Finding(FindingLevel.Warning, Codes.MinNotional, $"Node '{n.Id}' notional {DocumentJson.FormatDecimal(value)} is below the instrument minimum {minNotional}.", n.Id));
                }
            }
        }

        ShortOnSpot(doc, instruments, parameters, texts, f);

        foreach (BarTypeRef b in doc.BarTypes)
        {
            InstrumentRef? iref = doc.Instrument(b.Instrument);
            if (iref is null)
            {
                continue;
            }

            BarType barType = ToBarType(b, InstrumentId.Parse(iref.InstrumentId));
            if (context.DataRange(barType) is { } range && barType.Spec.IsTimeAggregated)
            {
                long available = range.End.Value - range.Start.Value;
                long needed = (warmup + 1) * barType.Spec.IntervalNanos;
                if (available < needed)
                {
                    f.Add(new Finding(FindingLevel.Warning, Codes.DataRangeInsufficient, $"Bar type '{b.Ref}' has less data than the strategy's warm-up of {warmup} bars.", Fix: "Fetch more history or shorten the lookbacks."));
                }
            }
        }
    }

    // A spot market holds no short: a sell that opens a position is refused by the venue, and a simulated run that lets
    // it through reports trades the strategy can never make.
    private static void ShortOnSpot(StrategyDocument doc, Dictionary<string, Instrument?> instruments, IReadOnlyDictionary<string, decimal> parameters, IReadOnlyDictionary<string, string> texts, List<Finding> f)
    {
        Instrument primary = instruments[doc.Instruments[0].Ref]!;
        List<(NodeDef Node, NodeParams Params, Instrument Instrument, string Side)> entries = new();
        foreach (NodeDef n in doc.Nodes.Where(n => !n.Disabled && EntryTypes.Contains(n.Type)))
        {
            NodeParams p = new(n.Params, parameters, texts);
            Instrument instrument = p.StrOrNull("instrument") is { } r && instruments.TryGetValue(r, out Instrument? found) && found is not null ? found : primary;
            entries.Add((n, p, instrument, p.Str("side", "buy")));
        }

        foreach ((NodeDef n, NodeParams p, Instrument instrument, string side) in entries)
        {
            if (side != "sell" || instrument.InstrumentClass != InstrumentClass.Spot)
            {
                continue;
            }

            bool opensOnly = n.Type == "act.bracket" || (n.Type == "act.order" && p.Bool("onlyWhenFlat", true) && !p.Bool("reduceOnly", false));
            bool canHold = entries.Any(e => e.Side == "buy" && e.Instrument.Id == instrument.Id);
            if (opensOnly || !canHold)
            {
                f.Add(new Finding(FindingLevel.Block, Codes.ShortOnSpot, $"Node '{n.Id}' opens a short position on {instrument.Id}, a spot instrument. A spot market cannot be sold short.", n.Id,
                    Fix: "Use a futures or perpetual instrument for a short strategy, or make the entry a buy."));
            }
        }
    }

    // ----- Helpers -----

    /// <summary>The name a <c>{"$param": "name"}</c> reference points at, or null when the value is not a reference.</summary>
    private static string? TextReference(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty("$param", out JsonElement p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    /// <summary>Document parameters that carry text: the string and enum ones, by name.</summary>
    public static IReadOnlyDictionary<string, string> ResolveTextParameters(StrategyDocument doc, IReadOnlyDictionary<string, string>? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        foreach (ParameterDef p in doc.Parameters.Where(p => p.Type is "enum" or "string"))
        {
            result[p.Name] = p.Value;
        }

        if (overrides is not null)
        {
            foreach ((string name, string value) in overrides)
            {
                result[name] = value;
            }
        }

        return result;
    }

    public static IReadOnlyDictionary<string, decimal> ResolveParameters(StrategyDocument doc, IReadOnlyDictionary<string, decimal>? overrides)
    {
        Dictionary<string, decimal> result = new(StringComparer.Ordinal);
        foreach (ParameterDef p in doc.Parameters)
        {
            if (TryDec(p.Value, out decimal v))
            {
                result[p.Name] = v;
            }
            else if (p.Type == "bool")
            {
                result[p.Name] = string.Equals(p.Value, "true", StringComparison.OrdinalIgnoreCase) ? 1m : 0m;
            }
        }

        if (overrides is not null)
        {
            foreach ((string name, decimal value) in overrides)
            {
                result[name] = value;
            }
        }

        return result;
    }

    public static BarType ToBarType(BarTypeRef reference, InstrumentId instrumentId) => new(
        instrumentId,
        new BarSpecification(reference.Step, Enum.Parse<BarAggregation>(reference.Aggregation, true), Enum.Parse<PriceType>(reference.PriceType, true)),
        Enum.Parse<AggregationSource>(reference.Source, true));

    private static bool TryParseInstrument(string id)
    {
        try
        {
            InstrumentId.Parse(id);
            return true;
        }
        catch (Exception e) when (e is ArgumentException or FormatException)
        {
            return false;
        }
    }

    private static bool TryDec(string? text, out decimal value) => decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
}
