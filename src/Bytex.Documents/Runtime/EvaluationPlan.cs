using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Documents.Catalog;
using Bytex.Documents.Schema;
using Bytex.Documents.Validation;

namespace Bytex.Documents.Runtime;

/// <summary>One compiled node: its evaluator, its wiring, and which phase it belongs to.</summary>
public sealed class PlannedNode
{
    public PlannedNode(NodeDef node, NodeTypeDescriptor descriptor, INodeEvaluator evaluator, IReadOnlyDictionary<string, string> inputs, CandleSeries? sourceCandleSeries, string? phaseId)
    {
        Node = node;
        Descriptor = descriptor;
        Evaluator = evaluator;
        Inputs = inputs;
        SourceCandleSeries = sourceCandleSeries;
        PhaseId = phaseId;
    }

    public NodeDef Node { get; }

    public NodeTypeDescriptor Descriptor { get; }

    public INodeEvaluator Evaluator { get; }

    /// <summary>input port → "sourceNode:sourcePort"</summary>
    public IReadOnlyDictionary<string, string> Inputs { get; }

    public CandleSeries? SourceCandleSeries { get; }

    /// <summary>null: always active.</summary>
    public string? PhaseId { get; }
}

/// <summary>
/// The compiled form of a document: nodes in evaluation order, resolved bar types and instruments, phases and transitions.
/// </summary>
public sealed class EvaluationPlan
{
    private EvaluationPlan(IReadOnlyList<PlannedNode> nodes, IReadOnlyDictionary<string, CandleSeries> candleSeriesDefinitions, CandleSeries primaryCandleSeries, MarketKey primaryInstrument, IReadOnlyList<MarketKey> instruments, string? initialPhase, IReadOnlyDictionary<string, IReadOnlyList<TransitionDef>> transitions, IReadOnlyDictionary<string, decimal> parameters, IReadOnlyDictionary<string, string> textParameters)
    {
        TextParameters = textParameters;
        Nodes = nodes;
        CandleSeriesDefinitions = candleSeriesDefinitions;
        PrimaryCandleSeries = primaryCandleSeries;
        PrimaryInstrument = primaryInstrument;
        Instruments = instruments;
        InitialPhase = initialPhase;
        Transitions = transitions;
        Parameters = parameters;
    }

    public IReadOnlyList<PlannedNode> Nodes { get; }

    /// <summary>Document bar type ref → engine bar type.</summary>
    public IReadOnlyDictionary<string, CandleSeries> CandleSeriesDefinitions { get; }

    /// <summary>The bar type whose close triggers a full evaluation (the document's first bar type).</summary>
    public CandleSeries PrimaryCandleSeries { get; }

    public MarketKey PrimaryInstrument { get; }

    public IReadOnlyList<MarketKey> Instruments { get; }

    public string? InitialPhase { get; }

    /// <summary>from phase → transitions</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<TransitionDef>> Transitions { get; }

    public IReadOnlyDictionary<string, decimal> Parameters { get; }

    /// <summary>String and enum document parameters, for params given as <c>{"$param": "name"}</c>.</summary>
    public IReadOnlyDictionary<string, string> TextParameters { get; }

    /// <summary>What starts a frame (R13.7). Quotes and trades also have to be subscribed to for it to happen.</summary>
    public string Evaluation { get; private set; } = EvaluationModes.BarClose;

    /// <summary>Whether a quote of the primary instrument starts a frame.</summary>
    public bool EvaluatesOnQuotes => Evaluation is EvaluationModes.Quote or EvaluationModes.Either;

    /// <summary>Whether a trade of the primary instrument starts a frame.</summary>
    public bool EvaluatesOnTrades => Evaluation is EvaluationModes.Trade or EvaluationModes.Either;

    public bool NeedsQuotes => EvaluatesOnQuotes || Nodes.Any(n => n.Node.Type == "data.quotes");

    public bool NeedsTrades => EvaluatesOnTrades || Nodes.Any(n => n.Node.Type == "data.trades");

    public bool NeedsBook => Nodes.Any(n => n.Node.Type == "data.book");

    public bool NeedsMarkPrice => Nodes.Any(n => n.Node.Type == "data.markPrice");

    public bool NeedsFunding => Nodes.Any(n => n.Node.Type == "data.funding");

    public bool NeedsAnnotations => Nodes.Any(n => n.Descriptor.Kind == NodeKind.Event || n.Node.Type == "risk.noEntryNear");

    public static EvaluationPlan Compile(StrategyDocument document, NodeCatalog catalog, IReadOnlyDictionary<string, decimal>? overrides, IStrategyServices services)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(services);

        IReadOnlyDictionary<string, decimal> parameters = DocumentValidator.ResolveParameters(document, overrides);
        IReadOnlyDictionary<string, string> textParameters = DocumentValidator.ResolveTextParameters(document);

        Dictionary<string, MarketKey> instrumentRefs = document.Instruments.ToDictionary(i => i.Ref, i => MarketKey.Parse(i.MarketKey), StringComparer.Ordinal);
        MarketKey primaryInstrument = instrumentRefs[document.Instruments[0].Ref];
        Dictionary<string, CandleSeries> candleSeriesDefinitions = document.CandleSeriesDefinitions.ToDictionary(b => b.Ref, b => DocumentValidator.ToCandleSeries(b, instrumentRefs[b.Instrument]), StringComparer.Ordinal);
        CandleSeries primaryCandleSeries = candleSeriesDefinitions[document.CandleSeriesDefinitions[0].Ref];

        List<NodeDef> active = document.Nodes.Where(n => !n.Disabled).ToList();
        Dictionary<string, NodeDef> byId = active.ToDictionary(n => n.Id, StringComparer.Ordinal);

        Dictionary<string, Dictionary<string, string>> inputs = new(StringComparer.Ordinal);
        Dictionary<string, List<string>> outgoing = new(StringComparer.Ordinal);
        Dictionary<string, int> indegree = active.ToDictionary(n => n.Id, _ => 0, StringComparer.Ordinal);
        foreach (EdgeDef e in document.Edges)
        {
            (string from, _) = EdgeDef.Split(e.From);
            (string to, string toPort) = EdgeDef.Split(e.To);
            if (!byId.ContainsKey(from) || !byId.ContainsKey(to))
            {
                continue;
            }

            (inputs.TryGetValue(to, out Dictionary<string, string>? map) ? map : inputs[to] = new Dictionary<string, string>(StringComparer.Ordinal))[toPort] = e.From;
            (outgoing.TryGetValue(from, out List<string>? list) ? list : outgoing[from] = new List<string>()).Add(to);
            indegree[to]++;
        }

        // Kahn's algorithm with a stable tie-break on document order.
        List<string> order = new();
        Dictionary<string, int> position = active.Select((n, i) => (n.Id, i)).ToDictionary(t => t.Id, t => t.i, StringComparer.Ordinal);
        PriorityQueue<string, int> ready = new();
        foreach ((string id, int degree) in indegree)
        {
            if (degree == 0)
            {
                ready.Enqueue(id, position[id]);
            }
        }

        while (ready.Count > 0)
        {
            string id = ready.Dequeue();
            order.Add(id);
            foreach (string next in outgoing.GetValueOrDefault(id) ?? new List<string>())
            {
                if (--indegree[next] == 0)
                {
                    ready.Enqueue(next, position[next]);
                }
            }
        }

        if (order.Count != active.Count)
        {
            throw new InvalidOperationException("The document graph contains a cycle; validate it before running.");
        }

        Dictionary<string, string> phaseOf = new(StringComparer.Ordinal);
        foreach (PhaseDef phase in document.Phases)
        {
            foreach (string nodeId in phase.Nodes)
            {
                phaseOf[nodeId] = phase.Id;
            }
        }

        Dictionary<string, CandleSeries?> sourceCandleSeries = new(StringComparer.Ordinal);
        List<PlannedNode> planned = new();
        foreach (string id in order)
        {
            NodeDef node = byId[id];
            NodeTypeDescriptor descriptor = catalog.Find(node.Type) ?? throw new InvalidOperationException($"Node '{id}' has unknown type '{node.Type}'.");
            NodeParams nodeParams = new(node.Params, parameters, textParameters);
            Dictionary<string, string> nodeInputs = inputs.GetValueOrDefault(id) ?? new Dictionary<string, string>(StringComparer.Ordinal);

            CandleSeries? source = null;
            if (node.Type == "data.bars")
            {
                string reference = nodeParams.Str("candleSeries", document.CandleSeriesDefinitions[0].Ref);
                source = candleSeriesDefinitions.TryGetValue(reference, out CandleSeries bt) ? bt : primaryCandleSeries;
            }
            else
            {
                foreach (string from in nodeInputs.Values)
                {
                    (string fromNode, _) = EdgeDef.Split(from);
                    if (sourceCandleSeries.TryGetValue(fromNode, out CandleSeries? upstream) && upstream is not null)
                    {
                        source = upstream;
                        break;
                    }
                }
            }

            sourceCandleSeries[id] = source;

            MarketKey marketKey = nodeParams.StrOrNull("instrument") is { } iref && instrumentRefs.TryGetValue(iref, out MarketKey resolved) ? resolved : source?.MarketKey ?? primaryInstrument;
            Instrument? instrument = services.Cache.Instrument(marketKey);
            NodeBuildContext build = new(document, node, descriptor, nodeParams, instrument, marketKey, source, nodeInputs.Keys.ToHashSet(StringComparer.Ordinal), services);
            INodeEvaluator evaluator = (descriptor.Factory ?? throw new InvalidOperationException($"Node type '{node.Type}' has no runtime."))(build);
            planned.Add(new PlannedNode(node, descriptor, evaluator, nodeInputs, source, phaseOf.GetValueOrDefault(id)));
        }

        string? initialPhase = document.Phases.FirstOrDefault(p => p.Initial)?.Id ?? document.Phases.FirstOrDefault()?.Id;
        Dictionary<string, IReadOnlyList<TransitionDef>> transitions = document.Transitions
            .GroupBy(t => t.From, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<TransitionDef>)g.ToList(), StringComparer.Ordinal);

        return new EvaluationPlan(planned, candleSeriesDefinitions, primaryCandleSeries, primaryInstrument, instrumentRefs.Values.Distinct().ToList(), initialPhase, transitions, parameters, textParameters)
        {
            // Whatever the document asked for, as it asked for it. The validator has already refused anything else,
            // and a plan built without one - a test, a host composing by hand - evaluates at the bar close.
            Evaluation = EvaluationModes.All.Contains(document.Evaluation, StringComparer.Ordinal) ? document.Evaluation : EvaluationModes.BarClose,
        };
    }
}
