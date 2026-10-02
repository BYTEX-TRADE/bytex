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

/// <summary>
/// What caused an evaluation frame (R13.7).
///
/// <para>
/// Until 0.9 there was one answer and it did not need a name: a bar closed. A document may now ask to be evaluated on
/// ticks as well, which does not change what a node is - only how often it is asked. A node that behaves differently
/// inside a bar reads this; every other node ignores it and is right to.
/// </para>
/// </summary>
public enum FrameTrigger
{
    /// <summary>The primary bar type closed. Indicators and rolling windows have just been updated.</summary>
    BarClose,

    /// <summary>A quote arrived. The bar on the frame is the last CLOSED one, and no indicator has moved.</summary>
    Quote,

    /// <summary>A trade printed. The bar on the frame is the last CLOSED one, and no indicator has moved.</summary>
    Trade,
}

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
    IEnumerable<(CandleSeries CandleSeries, IIndicator Indicator)> Indicators { get; }
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

    void CancelAllOrders(MarketKey marketKey);

    void CloseAllPositions(MarketKey marketKey);

    void PublishSignal(string name, decimal value);

    void Emit(string nodeId, string kind, string message, IReadOnlyDictionary<string, string>? values = null);

    /// <summary>Asks the strategy to stop after this frame (fired-once strategies).</summary>
    void RequestStop(string reason);

    IReadOnlyList<Annotation> Annotations { get; }
}

/// <summary>Everything a factory needs to build a node's evaluator.</summary>
public sealed class NodeBuildContext
{
    public NodeBuildContext(StrategyDocument document, NodeDef node, NodeTypeDescriptor descriptor, NodeParams parameters, Instrument? instrument, MarketKey marketKey, CandleSeries? sourceCandleSeries, IReadOnlySet<string> connectedInputs, IStrategyServices services)
    {
        Document = document;
        Node = node;
        Descriptor = descriptor;
        Params = parameters;
        Instrument = instrument;
        MarketKey = marketKey;
        SourceCandleSeries = sourceCandleSeries;
        ConnectedInputs = connectedInputs;
        Services = services;
    }

    public StrategyDocument Document { get; }

    public NodeDef Node { get; }

    public NodeTypeDescriptor Descriptor { get; }

    public NodeParams Params { get; }

    /// <summary>The instrument the node acts on (resolved from its params or the document's primary instrument); null before the cache has it.</summary>
    public Instrument? Instrument { get; }

    public MarketKey MarketKey { get; }

    /// <summary>The bar type feeding this node, propagated from the nearest data node; null when the node has no bar source.</summary>
    public CandleSeries? SourceCandleSeries { get; }

    public IReadOnlySet<string> ConnectedInputs { get; }

    public IStrategyServices Services { get; }

    public bool IsConnected(string input) => ConnectedInputs.Contains(input);
}

/// <summary>Typed access to a node's params with document parameters resolved.</summary>

/// <summary>The value table of one evaluation: every output port's value for the current bar.</summary>
public sealed class Frame
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

    /// <summary>
    /// The bar index: how many bars of the primary type this strategy has evaluated. It does NOT advance on a tick
    /// frame, which is what keeps every node that counts bars - a time stop, a delay, bars held - counting bars.
    /// </summary>
    public long Index { get; internal set; }

    /// <summary>
    /// The primary bar. On a bar-close frame it is the bar that just closed; on a tick frame it is the last one that
    /// closed, so a node reading it sees a complete bar rather than a partial one.
    /// </summary>
    public Bar Bar { get; internal set; }

    /// <summary>What caused this frame (R13.7). <see cref="FrameTrigger.BarClose"/> unless a document asked for more.</summary>
    public FrameTrigger Trigger { get; internal set; }

    /// <summary>The quote that caused this frame, where one did.</summary>
    public QuoteTick? Quote { get; internal set; }

    /// <summary>The trade that caused this frame, where one did.</summary>
    public TradeTick? Trade { get; internal set; }

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

    /// <summary>The primary bar: the one that just closed, or the last one that closed on a tick frame.</summary>
    public Bar Bar => _frame.Bar;

    /// <summary>The bar index. Unchanged across the tick frames inside a bar.</summary>
    public long Index => _frame.Index;

    /// <summary>What caused this frame (R13.7).</summary>
    public FrameTrigger Trigger => _frame.Trigger;

    /// <summary>
    /// Whether this frame is a closed bar. A node whose answer is only meaningful on completed bars reads this and
    /// holds what it said last; <c>cond.cross</c> is the built-in example.
    /// </summary>
    public bool IsBarClose => _frame.Trigger == FrameTrigger.BarClose;

    /// <summary>The quote that caused this frame, where one did.</summary>
    public QuoteTick? Quote => _frame.Quote;

    /// <summary>The trade that caused this frame, where one did.</summary>
    public TradeTick? Trade => _frame.Trade;

    /// <summary>
    /// The price this frame is about, which is what an action acts at: the bar's close, the quote's midpoint, or the
    /// trade's price. On a bar-close frame it is exactly <c>Bar.Close</c>, which is what every node used before this
    /// existed - so reading it changes nothing until a document asks for tick evaluation, and then it is the only
    /// reading that makes sense. A node that specifically means "the last bar's close" says <c>Bar.Close</c>.
    /// </summary>
    public decimal Last => _frame.Trigger switch
    {
        FrameTrigger.Quote when _frame.Quote is { } q => (q.Bid.Value + q.Ask.Value) / 2m,
        FrameTrigger.Trade when _frame.Trade is { } t => t.Price.Value,
        _ => _frame.Bar.Close.Value,
    };

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
    UnixNanos EventTime,
    UnixNanos CreatedTime) : CustomData(EventTime, CreatedTime);
