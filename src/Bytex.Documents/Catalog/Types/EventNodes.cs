using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Documents.Annotations;
using Bytex.Documents.Runtime;

namespace Bytex.Documents.Catalog.Types;

internal static class EventNodes
{
    /// <summary>The severity floor, shared with the risk node that keeps entries away from events.</summary>
    internal static readonly ParamSpec MinSeverityParam = P.Enum("minSeverity", "notice", "Min severity", "How serious an event has to be to count.",
        P.Choice("info", "Info and above", "Everything on the timeline, down to the routine entries."),
        P.Choice("notice", "Notice and above", "Leaves out the routine information and keeps everything else."),
        P.Choice("warning", "Warning and above", "Only what is marked a warning or worse."),
        P.Choice("critical", "Critical only", "Only what is marked critical."));

    private static readonly ParamSpec[] Filters =
    [
        MinSeverityParam,
        P.Str("categories", "", false, "Categories", "Comma-separated; empty means all."),
        P.Enum("scope", "instrument", "Scope", "Which events apply to this node.",
            P.Choice("instrument", "Anything touching the instrument", "Global events, events for either of its currencies, events for its venue, and events for the instrument itself."),
            P.Choice("global", "Global only", "Only the events that apply to every market.")),
        P.Bool("includeAiClassified", false, "Include AI-classified events", "In Live this also needs the document's opt-in (modes.live.allowAiAnnotationConditions)."),
    ];

    public static IEnumerable<NodeTypeDescriptor> All()
    {
        yield return new NodeTypeDescriptor
        {
            Type = "event.proximity",
            Kind = NodeKind.Event,
            DisplayName = "Near an event",
            Description = "True inside a window around matching events on the chart's timeline.",
            FaceTemplate = "Within {before} min before to {after} min after a {categories} event",
            Outputs = [new PortSpec("near", ValueKind.Bool), new PortSpec("minutesToNext", ValueKind.Series), new PortSpec("count", ValueKind.Series)],
            Params = [P.Int("before", 30, 0, 1440, "Minutes before"), P.Int("after", 30, 0, 1440, "Minutes after"), .. Filters],
            Factory = ctx => new ProximityNode(ctx),
        };

        yield return new NodeTypeDescriptor
        {
            Type = "event.active",
            Kind = NodeKind.Event,
            DisplayName = "Event window active",
            Description = "True while a matching event window (for example venue maintenance) is in progress.",
            FaceTemplate = "During a {categories} window",
            Outputs = [new PortSpec("active", ValueKind.Bool)],
            Params = Filters,
            Factory = ctx => new ActiveNode(ctx),
        };
    }

    private static IEnumerable<Annotation> Matching(NodeBase node, NodeBuildContext ctx, Instrument? instrument)
    {
        NodeParams p = ctx.Params;
        AnnotationSeverity minSeverity = RiskNodes.Severity(p.Str("minSeverity", "notice"));
        HashSet<string> categories = p.Str("categories", string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool globalOnly = p.Str("scope", "instrument") == "global";
        bool allowAi = RiskNodes.AllowAi(ctx, p.Bool("includeAiClassified", false));
        foreach (Annotation a in ctx.Services.Annotations)
        {
            if (a.Severity < minSeverity || (categories.Count > 0 && !categories.Contains(a.Category)) || (a.IsAiClassified && !allowAi))
            {
                continue;
            }

            if (globalOnly ? !a.Scopes.Any(s => s.Level == ScopeLevel.Global) : instrument is not null && !a.AppliesTo(instrument))
            {
                continue;
            }

            yield return a;
        }
    }

    private sealed class ProximityNode : NodeBase
    {
        public ProximityNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public override void Evaluate(EvalContext ctx)
        {
            long now = ctx.Bar.TsEvent.Value;
            long before = P.Int("before", 30) * UnixNanos.NanosPerMinute;
            long after = P.Int("after", 30) * UnixNanos.NanosPerMinute;
            bool near = false;
            int count = 0;
            decimal? next = null;
            foreach (Annotation a in Matching(this, Ctx, Instrument))
            {
                long start = a.TsEvent.Value - before;
                long end = (a.TsEnd?.Value ?? a.TsEvent.Value) + after;
                if (now >= start && now <= end)
                {
                    near = true;
                    count++;
                }

                if (a.TsEvent.Value >= now)
                {
                    decimal minutes = (decimal)(a.TsEvent.Value - now) / UnixNanos.NanosPerMinute;
                    next = next is { } n ? Math.Min(n, minutes) : minutes;
                }
            }

            ctx.Set("near", near);
            ctx.Set("count", (decimal)count);
            if (next is { } m)
            {
                ctx.Set("minutesToNext", m);
            }
        }
    }

    private sealed class ActiveNode : NodeBase
    {
        public ActiveNode(NodeBuildContext ctx)
            : base(ctx)
        {
        }

        public override void Evaluate(EvalContext ctx)
        {
            long now = ctx.Bar.TsEvent.Value;
            bool active = Matching(this, Ctx, Instrument).Any(a => a.TsEnd is { } end && now >= a.TsEvent.Value && now <= end.Value);
            ctx.Set("active", active);
        }
    }
}
