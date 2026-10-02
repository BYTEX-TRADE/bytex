using System.Text.Json;
using Bytex.Core.Plugins;
using Bytex.Documents.Catalog;
using Bytex.Documents.Runtime;

namespace Bytex.Examples.Nodes;

/// <summary>
/// Two node types from a plugin, and the plugin that carries them (R13.8).
///
/// <para>
/// They are deliberately the two shapes that behave differently rather than two variations of one: a node that computes
/// from its inputs and remembers nothing, and a node that remembers something a restart must not lose. The second is
/// where node authors go wrong - a latch that forgets it has fired re-fires on the first bar after a restart, and a
/// strategy that should have acted once acts twice.
/// </para>
///
/// <para>
/// Build it and point a host at the directory: <c>bytex --plugins path/to/output</c>. The type ids all begin with
/// <c>example.</c>, which is the prefix this provider declares; a document that uses them says so under
/// <c>requires</c>, and a host without the plugin then refuses the document by naming what is missing rather than by
/// naming a type nobody recognises.
/// </para>
/// </summary>
public sealed class ExampleNodesPlugin : IPlugin, INodeTypeProvider
{
    /// <summary>The one word every type from this plugin begins with.</summary>
    public const string Prefix = "example";

    public string Id => "bytex.examples.nodes";

    public string Version => typeof(ExampleNodesPlugin).Assembly.GetName().Version?.ToString() ?? "0";

    public string TypePrefix => Prefix;

    private const string DocumentResource = "Bytex.Examples.Nodes.Documents.first-wide-bar.json";

    /// <summary>
    /// A plugin that only adds node types registers nothing else: the host composes the catalog from every provider it
    /// loaded, and the documents plugin is what runs documents with it.
    /// </summary>
    public void Register(IPluginRegistry registry)
    {
    }

    /// <summary>
    /// A document that uses both of these node types, as JSON: the worked example of a <c>requires</c> block and of the
    /// two nodes wired to an order. It travels inside the assembly so a host can run it without knowing a file path.
    /// </summary>
    public static string ExampleDocumentJson()
    {
        using Stream stream = typeof(ExampleNodesPlugin).Assembly.GetManifestResourceStream(DocumentResource)
            ?? throw new InvalidOperationException($"The example document '{DocumentResource}' is not embedded.");
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    public IEnumerable<NodeTypeDescriptor> NodeTypes()
    {
        yield return new NodeTypeDescriptor
        {
            Type = Prefix + ".range",
            Kind = NodeKind.Custom,
            DisplayName = "Bar range",
            Description = "How far the bar travelled, as a share of its close. Nothing is remembered between bars.",
            FaceTemplate = "Range of the bar as a share of its close",
            Inputs = [new PortSpec("bars", ValueKind.Bars, Required: true, Label: "bars")],
            Outputs =
            [
                new PortSpec("value", ValueKind.Series, Label: "range"),
                new PortSpec("wide", ValueKind.Bool, Description: "true while the range is above the threshold"),
            ],
            Params =
            [
                new ParamSpec
                {
                    Name = "threshold",
                    Type = ParamType.Decimal,
                    Default = "0.002",
                    Min = "0",
                    Step = "0.0005",
                    Label = "Wide above",
                    Unit = "share of the close",
                    Description = "The range above which the bar counts as wide: 0.002 is two tenths of one percent of the close.",
                },
            ],
            Factory = ctx => new BarRangeNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = Prefix + ".oncePerSession",
            Kind = NodeKind.Custom,
            DisplayName = "Once per session",
            Description = "Passes the first true it sees each day and swallows the rest, remembering across a restart.",
            FaceTemplate = "The first {in} of each day only",
            // Bool in, pulse out: a pulse is accepted wherever a bool is expected, so this reads a condition and drives
            // an action. An input declared as a pulse could not be fed by a condition at all.
            Inputs = [new PortSpec("in", ValueKind.Bool, Required: true, Label: "when")],
            Outputs = [new PortSpec("out", ValueKind.Pulse, Label: "first of the day")],
            Params = [],
            Factory = ctx => new OncePerSessionNode(ctx),
        };
    }
}

/// <summary>
/// The stateless shape: everything it answers comes from the bar in front of it.
/// </summary>
internal sealed class BarRangeNode : INodeEvaluator
{
    private readonly decimal _threshold;

    public BarRangeNode(NodeBuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        _threshold = ctx.Params.Dec("threshold", 0.01m);
    }

    public void Evaluate(EvalContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        decimal close = ctx.Bar.Close.Value;
        decimal range = close == 0m ? 0m : (ctx.Bar.High.Value - ctx.Bar.Low.Value) / close;

        ctx.Set("value", range);
        ctx.Set("wide", range > _threshold);
    }
}

/// <summary>
/// The stateful shape: what it has already done this session has to survive a restart, or the strategy does it twice.
///
/// <para>
/// The day is taken from the bar's own timestamp rather than from the wall clock, so a backtest and a live node agree
/// and a replay of the same data gives the same answer.
/// </para>
/// </summary>
internal sealed class OncePerSessionNode : INodeEvaluator, IStatefulNode
{
    private DateOnly? _firedOn;

    public OncePerSessionNode(NodeBuildContext ctx) => ArgumentNullException.ThrowIfNull(ctx);

    public void Evaluate(EvalContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        DateOnly day = DateOnly.FromDateTime(ctx.Bar.EventTime.ToDateTimeUtc());

        if (!ctx.Bool("in") || _firedOn == day)
        {
            ctx.Set("out", false);
            return;
        }

        _firedOn = day;
        ctx.Set("out", true);
        ctx.Emit("fired", "first pulse of the day passed");
    }

    public JsonElement SaveState() =>
        JsonSerializer.SerializeToElement(new StoredState(_firedOn?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)));

    public void LoadState(JsonElement state)
    {
        StoredState? stored = state.ValueKind == JsonValueKind.Object ? state.Deserialize<StoredState>() : null;
        _firedOn = stored?.FiredOn is { Length: > 0 } text
            && DateOnly.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out DateOnly day)
                ? day
                : null;
    }

    private sealed record StoredState(string? FiredOn);
}
