using System.Globalization;
using System.Text.Json;
using Bytex.Core.Caching;
using Bytex.Core.Indicators;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Portfolios;
using Bytex.Core.Timing;
using Bytex.Core.Trading;
using Bytex.Documents.Annotations;
using Bytex.Documents.Catalog;
using Bytex.Documents.Schema;
using Microsoft.Extensions.Logging;

namespace Bytex.Documents.Runtime;

/// <summary>A node's runtime: computes its outputs once per evaluation frame.</summary>
public interface INodeEvaluator
{
    void Evaluate(EvalContext ctx);
}

/// <summary>Receives every bar of the node's source bar type before the frame is evaluated (rolling windows, levels).</summary>
public interface IBarObserver
{
    void OnBar(Bar bar);
}

/// <summary>Owns engine indicators that must be registered for automatic updates.</summary>
public interface IIndicatorHost
{
    IEnumerable<(BarType BarType, IIndicator Indicator)> Indicators { get; }
}

public interface IOrderEventObserver
{
    void OnOrderEvent(OrderEvent e);
}

public interface IPositionEventObserver
{
    void OnPositionEvent(PositionEvent e);
}

public interface IAnnotationObserver
{
    void OnAnnotation(Annotation annotation);
}

/// <summary>Nodes with state that must survive a restart (latches, counters, managed orders).</summary>
public interface IStatefulNode
{
    JsonElement SaveState();

    void LoadState(JsonElement state);
}

/// <summary>
/// Nodes that leave orders resting on the venue and have to take them back when the strategy stops. Called once, after
/// the last frame: what the node does here reaches the venue the same way anything it does in a frame would.
/// </summary>
public interface IStoppableNode
{
    void OnStrategyStopping();
}

/// <summary>Called when the phase containing the node becomes active or inactive.</summary>
public interface IPhaseAware
{
    void OnPhaseEntered();

    void OnPhaseExited();
}

/// <summary>What the runtime lets nodes do: read state and trade through the owning strategy.</summary>
public interface IStrategyServices
{
    IClock Clock { get; }

    ICache Cache { get; }

    IPortfolio Portfolio { get; }

    ILogger Log { get; }

    StrategyId StrategyId { get; }

    OrderFactory OrderFactory { get; }

    TradingEnvironment Environment { get; }

    void SubmitOrder(Order order);

    void SubmitOrderList(OrderList list);

    void ModifyOrder(Order order, Quantity? quantity = null, Price? price = null, Price? triggerPrice = null);

    void CancelOrder(Order order);

    void CancelAllOrders(InstrumentId instrumentId);

    void CloseAllPositions(InstrumentId instrumentId);

    void PublishSignal(string name, decimal value);

    void Emit(string nodeId, string kind, string message, IReadOnlyDictionary<string, string>? values = null);

    /// <summary>Asks the strategy to stop after this frame (fired-once strategies).</summary>
    void RequestStop(string reason);

    IReadOnlyList<Annotation> Annotations { get; }
}

/// <summary>Everything a factory needs to build a node's evaluator.</summary>
public sealed class NodeBuildContext
{
    public NodeBuildContext(StrategyDocument document, NodeDef node, NodeTypeDescriptor descriptor, NodeParams parameters, Instrument? instrument, InstrumentId instrumentId, BarType? sourceBarType, IReadOnlySet<string> connectedInputs, IStrategyServices services)
    {
        Document = document;
        Node = node;
        Descriptor = descriptor;
        Params = parameters;
        Instrument = instrument;
        InstrumentId = instrumentId;
        SourceBarType = sourceBarType;
        ConnectedInputs = connectedInputs;
        Services = services;
    }

    public StrategyDocument Document { get; }

    public NodeDef Node { get; }

    public NodeTypeDescriptor Descriptor { get; }

    public NodeParams Params { get; }

    /// <summary>The instrument the node acts on (resolved from its params or the document's primary instrument); null before the cache has it.</summary>
    public Instrument? Instrument { get; }

    public InstrumentId InstrumentId { get; }

    /// <summary>The bar type feeding this node, propagated from the nearest data node; null when the node has no bar source.</summary>
    public BarType? SourceBarType { get; }

    public IReadOnlySet<string> ConnectedInputs { get; }

    public IStrategyServices Services { get; }

    public bool IsConnected(string input) => ConnectedInputs.Contains(input);
}

/// <summary>Typed access to a node's params with document parameters resolved.</summary>

/// <summary>The value table of one evaluation: every output port's value for the current bar.</summary>
public sealed class Frame
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

    public long Index { get; internal set; }

    public Bar Bar { get; internal set; }

    public void Clear() => _values.Clear();

    public void Set(string key, object? value) => _values[key] = value;

    public bool TryGet(string key, out object? value) => _values.TryGetValue(key, out value);

    public IReadOnlyDictionary<string, object?> Values => _values;
}

/// <summary>The evaluation context handed to a node: its wired inputs, its outputs, the current bar.</summary>
public sealed class EvalContext
{
    private readonly Frame _frame;
    private readonly string _nodeId;
    private readonly IReadOnlyDictionary<string, string> _inputs;

    public EvalContext(Frame frame, string nodeId, IReadOnlyDictionary<string, string> inputs, IStrategyServices services)
    {
        _frame = frame;
        _nodeId = nodeId;
        _inputs = inputs;
        Services = services;
    }

    public Frame Frame => _frame;

    public string NodeId => _nodeId;

    public Bar Bar => _frame.Bar;

    public long Index => _frame.Index;

    public IStrategyServices Services { get; }

    public bool IsConnected(string input) => _inputs.ContainsKey(input);

    public object? Input(string input) => _inputs.TryGetValue(input, out string? key) && _frame.TryGet(key, out object? v) ? v : null;

    public decimal? Dec(string input) => Input(input) switch
    {
        decimal d => d,
        Price p => p.Value,
        Quantity q => q.Value,
        int i => i,
        long l => l,
        _ => null,
    };

    public bool Bool(string input) => Input(input) switch
    {
        bool b => b,
        decimal d => d != 0m,
        _ => false,
    };

    public Bar? Bars(string input) => Input(input) as Bar?;

    public PositionId? Position(string input) => Input(input) as PositionId?;

    public void Set(string output, object? value) => _frame.Set(_nodeId + ":" + output, value);

    public void Emit(string kind, string message, IReadOnlyDictionary<string, string>? values = null) => Services.Emit(_nodeId, kind, message, values);
}

/// <summary>A decision, fire, skip, or transition the runtime publishes so monitors and the assistant can explain behaviour.</summary>
public sealed record StrategyEvent(
    string StrategyId,
    string Kind,
    string NodeId,
    string Message,
    IReadOnlyDictionary<string, string> Values,
    UnixNanos TsEvent,
    UnixNanos TsInit) : CustomData(TsEvent, TsInit);
