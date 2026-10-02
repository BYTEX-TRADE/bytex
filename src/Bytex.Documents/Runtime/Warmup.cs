using System.Globalization;
using System.Text.Json;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Primitives;
using Bytex.Documents.Catalog;

namespace Bytex.Documents.Runtime;

/// <summary>
/// How far one bar type is into its warm-up. <see cref="Received"/> counts every closed bar the strategy has evaluated on
/// this bar type, historical and live, so with a short history it goes on counting bar by bar until <see cref="Done"/>.
/// </summary>
public sealed record WarmupState(CandleSeries CandleSeries, int Needed, int Received, int FromHistory, bool Done, UnixNanos? First, UnixNanos? Last);

/// <summary>One condition node as the last frame left it: what it answered and what it looked at.</summary>
/// <param name="NodeId">The node's id in the document.</param>
/// <param name="Type">The catalog type, for example <c>cond.compare</c>.</param>
/// <param name="Label">The label the user gave the node, if any.</param>
/// <param name="Output">The node's <c>out</c> port; null when the node produced nothing this frame (not ready yet).</param>
/// <param name="Inputs">Every connected input port that carried a value this frame, as invariant text.</param>
/// <param name="Params">The node's scalar parameters with document parameters resolved, as invariant text.</param>
public sealed record ConditionSnapshot(string NodeId, string Type, string? Label, bool? Output, IReadOnlyDictionary<string, string> Inputs, IReadOnlyDictionary<string, string> Params);

/// <summary>The condition nodes that are active in the phase the strategy is in after a frame, in evaluation order.</summary>
public sealed record FrameSnapshot(long Index, UnixNanos BarTs, string? Phase, IReadOnlyList<ConditionSnapshot> Conditions, IReadOnlyDictionary<string, string> Values);

/// <summary>
/// What a document strategy tells a monitor about itself: plain values only, so it serializes the same everywhere.
/// Times are nanoseconds since the Unix epoch.
/// </summary>
public sealed record DocumentMonitorView(
    string MarketKey,
    string CandleSeries,
    long BarIntervalSeconds,
    long? LastBarTs,
    string? Phase,
    long BarIndex,
    bool WarmupPending,
    bool WarmedUp,
    IReadOnlyList<DocumentMonitorWarmup> Warmup,
    DocumentMonitorFrame? Frame,

    // Every bar the warm-up replayed and what the nodes produced on it, oldest first - see
    // DocumentMonitorWarmupPoint for why these are the engine's own numbers rather than recomputed ones.
    IReadOnlyList<DocumentMonitorWarmupPoint> WarmupSeries);

public sealed record DocumentMonitorWarmup(string CandleSeries, int Needed, int Received, int FromHistory, bool Done, long? First, long? Last);

/// <summary>
/// One bar of the warm-up and what every node produced on it, keyed <c>nodeId:port</c> as invariant text.
///
/// <para>
/// This is what lets a chart draw an indicator back over the history the node warmed on, instead of starting the
/// line where the node started. Recomputing it outside the engine would not do: a second implementation cannot
/// verify the first - if the two agreed it would prove nothing, and if they disagreed nobody could say which was
/// right. These are the engine's own numbers.
/// </para>
/// </summary>
public sealed record DocumentMonitorWarmupPoint(long BarTs, IReadOnlyDictionary<string, string> Values);

/// <summary>
/// The last evaluated bar. <paramref name="Values"/> is every node output on it, keyed <c>nodeId:port</c> and
/// rendered as invariant text - the same thing a run in this process reads from <c>LastValues</c>, so a node
/// watched over the control channel is not told less than one watched in process. It is what a chart needs to draw
/// the indicator a condition is waiting on, rather than recomputing it and proving nothing.
/// </summary>
public sealed record DocumentMonitorFrame(long Index, long BarTs, string? Phase, IReadOnlyList<ConditionSnapshot> Conditions, IReadOnlyDictionary<string, string> Values);

internal static class WarmupPlan
{
    /// <summary>Parameters that make a node look back over bars; the largest one on a bar type, plus one, is what that bar type needs.</summary>
    private static readonly string[] _lookbackKeys = ["lookback", "period", "slow", "k", "bars"];

    /// <summary>Exponential averages keep moving well past their period; three times the need lets them settle.</summary>
    private const int ConvergenceFactor = 3;

    public const int MaxRequest = 1000;

    public static Dictionary<CandleSeries, int> Needed(EvaluationPlan plan)
    {
        Dictionary<CandleSeries, int> needed = plan.CandleSeriesDefinitions.Values.Distinct().ToDictionary(b => b, _ => 0);
        foreach (PlannedNode node in plan.Nodes)
        {
            NodeParams p = new(node.Node.Params, plan.Parameters, plan.TextParameters);
            CandleSeries candleSeries = node.SourceCandleSeries ?? plan.PrimaryCandleSeries;
            foreach (string key in _lookbackKeys)
            {
                if (p.Has(key) && p.DecOrNull(key) is { } v && v > needed.GetValueOrDefault(candleSeries) && v < 1_000_000m)
                {
                    needed[candleSeries] = (int)v;
                }
            }
        }

        foreach (CandleSeries candleSeries in needed.Keys.ToList())
        {
            needed[candleSeries] = needed[candleSeries] == 0 ? 0 : needed[candleSeries] + 1;
        }

        return needed;
    }

    public static int RequestSize(int needed) => Math.Min(MaxRequest, needed * ConvergenceFactor);

    /// <summary>
    /// The <c>end</c> of the history request. The last bar wanted is the one that closed at the latest boundary before
    /// <paramref name="now"/>, or, when a live bar is already waiting, the one before that bar. Venues read <c>end</c> against
    /// a candle's open time, so a millisecond before that close takes the wanted candle and leaves out the next one: the
    /// forming candle, or the live bar that is about to be replayed.
    /// </summary>
    public static UnixNanos RequestEnd(CandleSeries candleSeries, UnixNanos now, Bar? firstLive)
    {
        long interval = candleSeries.Spec.IntervalNanos;
        if (interval <= 0)
        {
            return now;
        }

        long lastClose = now.Value / interval * interval;
        if (firstLive is { } live && live.CandleSeries == candleSeries)
        {
            lastClose = Math.Min(lastClose, live.EventTime.Value - interval);
        }

        return new UnixNanos(Math.Max(0, lastClose - 1_000_000L));
    }

    public static string Text(object? value) => value switch
    {
        null => string.Empty,
        bool b => b ? "true" : "false",
        decimal d => d.ToString("0.########", CultureInfo.InvariantCulture),
        Price p => p.Value.ToString("0.########", CultureInfo.InvariantCulture),
        Quantity q => q.Value.ToString("0.########", CultureInfo.InvariantCulture),
        Bar bar => bar.Close.Value.ToString("0.########", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    public static IReadOnlyDictionary<string, string> ScalarParams(PlannedNode node, IReadOnlyDictionary<string, decimal> parameters)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        if (node.Node.Params is not { ValueKind: JsonValueKind.Object } root)
        {
            return result;
        }

        foreach (JsonProperty property in root.EnumerateObject())
        {
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.String:
                    result[property.Name] = property.Value.GetString()!;
                    break;
                case JsonValueKind.Number:
                    result[property.Name] = property.Value.GetDecimal().ToString("0.########", CultureInfo.InvariantCulture);
                    break;
                case JsonValueKind.True or JsonValueKind.False:
                    result[property.Name] = property.Value.GetBoolean() ? "true" : "false";
                    break;
                case JsonValueKind.Object when property.Value.TryGetProperty("$param", out JsonElement name) && name.ValueKind == JsonValueKind.String
                    && parameters.TryGetValue(name.GetString()!, out decimal resolved):
                    result[property.Name] = resolved.ToString("0.########", CultureInfo.InvariantCulture);
                    break;
                default:
                    break;
            }
        }

        return result;
    }

    public static FrameSnapshot Snapshot(EvaluationPlan plan, Frame frame, string? phase)
    {
        List<ConditionSnapshot> conditions = new();
        foreach (PlannedNode node in plan.Nodes)
        {
            if (node.Descriptor.Kind != NodeKind.Condition || (node.PhaseId is { } own && own != phase))
            {
                continue;
            }

            bool? output = frame.TryGet(node.Node.Id + ":out", out object? o) ? o as bool? : null;
            Dictionary<string, string> inputs = new(StringComparer.Ordinal);
            foreach ((string port, string source) in node.Inputs)
            {
                if (frame.TryGet(source, out object? value) && value is not null)
                {
                    inputs[port] = Text(value);
                }
            }

            conditions.Add(new ConditionSnapshot(node.Node.Id, node.Node.Type, node.Node.Label, output, inputs, ScalarParams(node, plan.Parameters)));
        }

        // Every node's output, not only the conditions': a chart draws the indicator, and the condition tells it
        // what to look at. Text, because this crosses a wire and a decimal must not become a double on the way.
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach ((string key, object? value) in frame.Values)
        {
            if (value is not null)
            {
                values[key] = Text(value);
            }
        }

        return new FrameSnapshot(frame.Index, frame.Bar.EventTime, phase, conditions, values);
    }
}
