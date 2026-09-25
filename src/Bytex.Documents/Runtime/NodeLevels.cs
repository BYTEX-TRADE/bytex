using System.Globalization;
using System.Text.Json;
using Bytex.Core.Caching;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;

namespace Bytex.Documents.Runtime;

/// <summary>
/// One price level of a node that works several at once. A grid is the reason this exists: at every level a pair of
/// orders takes turns, and when the pair completes a round trip the level has to re-arm itself. That needs state per
/// level - which order is resting, what it is for, how many times the level has turned over - and node state alone
/// could not hold it, because a node had one tracker and one place to put its numbers.
/// </summary>
public sealed class NodeLevel
{
    internal NodeLevel(string key) => Key = key;

    /// <summary>What the node calls this level. Levels are always evaluated in the order of their keys.</summary>
    public string Key { get; }

    /// <summary>The price the level works at.</summary>
    public decimal Price { get; set; }

    /// <summary>The size the level works with.</summary>
    public decimal Size { get; set; }

    /// <summary>Completed round trips: a buy and the sell that closed it, or the other way round.</summary>
    public int RoundTrips { get; set; }

    /// <summary>The order resting on the entry side of this level, if the level has one out.</summary>
    public TrackedOrder Entry { get; } = new();

    /// <summary>The order resting on the exit side of this level, if the level has one out.</summary>
    public TrackedOrder Exit { get; } = new();

    /// <summary>Whether either side of the level has an order the venue has not finished with.</summary>
    public bool IsWorking => Entry.IsWorking || Exit.IsWorking;

    internal JsonElement Save() => JsonSerializer.SerializeToElement(new
    {
        key = Key,
        price = Price,
        size = Size,
        roundTrips = RoundTrips,
        entry = Entry.Save(),
        exit = Exit.Save(),
    });

    internal void Load(JsonElement state)
    {
        Price = Number(state, "price") ?? 0m;
        Size = Number(state, "size") ?? 0m;
        RoundTrips = state.TryGetProperty("roundTrips", out JsonElement trips) && trips.ValueKind == JsonValueKind.Number ? trips.GetInt32() : 0;
        if (state.TryGetProperty("entry", out JsonElement entry) && entry.ValueKind == JsonValueKind.Object)
        {
            Entry.Load(entry);
        }

        if (state.TryGetProperty("exit", out JsonElement exit) && exit.ValueKind == JsonValueKind.Object)
        {
            Exit.Load(exit);
        }
    }

    private static decimal? Number(JsonElement state, string name) =>
        state.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number ? value.GetDecimal() : null;
}

/// <summary>
/// The levels a node is working, addressable by key and always enumerated in the same order, so a rerun of the same
/// document repeats itself. It fans an order event out to every level, refreshes them all against the cache, and
/// saves and loads the lot as one value, which is what a node hands to <see cref="IStatefulNode"/>.
/// </summary>
public sealed class NodeLevels
{
    private readonly Dictionary<string, NodeLevel> _levels = new(StringComparer.Ordinal);
    private readonly List<string> _order = new();

    public int Count => _levels.Count;

    /// <summary>Every level, in key order: numeric keys in numeric order, anything else ordinally.</summary>
    public IReadOnlyList<NodeLevel> All => _order.Select(k => _levels[k]).ToList();

    /// <summary>The levels with an order the venue has not finished with.</summary>
    public IEnumerable<NodeLevel> Working => All.Where(l => l.IsWorking);

    public NodeLevel this[string key] => _levels[key];

    public bool Contains(string key) => _levels.ContainsKey(key);

    public NodeLevel? Find(string key) => _levels.GetValueOrDefault(key);

    /// <summary>The level under this key, created if the node has not worked it yet.</summary>
    public NodeLevel Ensure(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (_levels.TryGetValue(key, out NodeLevel? level))
        {
            return level;
        }

        level = new NodeLevel(key);
        _levels[key] = level;
        _order.Add(key);
        Sort();
        return level;
    }

    public bool Remove(string key)
    {
        if (!_levels.Remove(key))
        {
            return false;
        }

        _order.Remove(key);
        return true;
    }

    public void Clear()
    {
        _levels.Clear();
        _order.Clear();
    }

    /// <summary>Hands the event to every level; each ignores what is not its own order.</summary>
    public void OnOrderEvent(OrderEvent e)
    {
        foreach (NodeLevel level in All)
        {
            level.Entry.OnOrderEvent(e);
            level.Exit.OnOrderEvent(e);
        }
    }

    /// <summary>Brings every level's trackers up to date with what the cache says about their orders.</summary>
    public void Refresh(IStrategyServices services)
    {
        foreach (NodeLevel level in All)
        {
            level.Entry.Refresh(services);
            level.Exit.Refresh(services);
        }
    }

    public JsonElement Save() => JsonSerializer.SerializeToElement(All.Select(l => l.Save()).ToArray());

    public void Load(JsonElement state)
    {
        if (state.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        Clear();
        foreach (JsonElement saved in state.EnumerateArray())
        {
            if (saved.ValueKind != JsonValueKind.Object || !saved.TryGetProperty("key", out JsonElement key) || key.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            Ensure(key.GetString()!).Load(saved);
        }
    }

    private void Sort() => _order.Sort(static (a, b) =>
        int.TryParse(a, out int left) && int.TryParse(b, out int right) ? left.CompareTo(right) : string.CompareOrdinal(a, b));
}

/// <summary>
/// One order a node placed and is following: what it is, whether the venue is still working it, what it filled at,
/// and the position it landed in. A node that owns its orders can publish what happened to them and find them again
/// after a restart; one that does not can only place and hope.
/// <para>
/// An order handed to an execution algorithm is followed through the pieces the algorithm sends, each an order of its
/// own carrying this one's id, because the instruction itself never reaches a venue: it never fills and never closes,
/// so nothing about it would ever change. What the node then publishes is the instruction as one order - working until
/// the last piece is done or the algorithm's time is up, filled when the whole size is, at what the pieces averaged.
/// </para>
/// </summary>
public sealed class TrackedOrder
{
    public ClientOrderId? Id { get; set; }

    public long SubmittedAt { get; set; }

    public bool PendingSubmitted { get; set; }

    public bool PendingFilled { get; set; }

    public bool PendingRejected { get; set; }

    public PositionId? PositionId { get; set; }

    public decimal? FillPrice { get; set; }

    public bool IsWorking { get; set; }

    public string? LastReason { get; set; }

    /// <summary>True when an execution algorithm was asked to work the order, so the pieces are what to follow.</summary>
    public bool IsWorked { get; set; }

    /// <summary>What the instruction asked for, in base units, when it is being worked.</summary>
    public decimal WorkedQuantity { get; set; }

    /// <summary>How much of it the pieces have filled.</summary>
    public decimal FilledQuantity { get; set; }

    /// <summary>What those fills came to in the quote currency, which is what the average price is worked out of.</summary>
    public decimal FilledValue { get; set; }

    /// <summary>
    /// When the algorithm has had its say: the horizon it was given, and one interval of slack for the last piece.
    /// Past it, whatever was going to be sent has been sent, and the node is free to place another order.
    /// </summary>
    public UnixNanos? WorkedUntil { get; set; }

    /// <summary>
    /// Starts following an order the node has just written. <c>workedUntil</c> is when an execution algorithm working
    /// it will have finished with it, and null for an order that goes to the venue in one piece.
    /// </summary>
    public void Track(Order order, long index, UnixNanos? workedUntil = null)
    {
        ArgumentNullException.ThrowIfNull(order);
        Id = order.ClientOrderId;
        SubmittedAt = index;
        IsWorking = true;
        PendingSubmitted = true;
        PendingFilled = false;
        PendingRejected = false;
        PositionId = null;
        FillPrice = null;
        LastReason = null;
        IsWorked = order.ExecAlgorithmId is not null;
        WorkedQuantity = IsWorked ? order.Quantity.Value : 0m;
        WorkedUntil = IsWorked ? workedUntil : null;
        FilledQuantity = 0m;
        FilledValue = 0m;
    }

    /// <summary>
    /// Takes in what happened to the order, or to one of the pieces it is being worked in. The cache is needed only
    /// for the second case: a piece is an order of its own, and the cache is what says which instruction it belongs to.
    /// </summary>
    public void OnOrderEvent(OrderEvent e, ICache? cache = null)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (Id is not { } id)
        {
            return;
        }

        if (e.ClientOrderId != id)
        {
            if (!IsWorked || cache?.Order(e.ClientOrderId) is not { ExecSpawnId: { } spawn } || spawn != id)
            {
                return;
            }

            OnPieceEvent(e);
            return;
        }

        switch (e)
        {
            case OrderFilled f:
                PendingFilled = true;
                FillPrice = f.LastPx.Value;
                PositionId = f.PositionId ?? PositionId;
                break;
            case OrderDenied denied:
                PendingRejected = true;
                IsWorking = false;
                LastReason = "denied: " + denied.Reason;
                break;
            case OrderRejected rejected:
                PendingRejected = true;
                IsWorking = false;
                LastReason = "rejected: " + rejected.Reason;
                break;
            case OrderCanceled or OrderExpired:
                IsWorking = false;
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// One piece of an order being worked. Only its fills say anything about the instruction: a piece the venue
    /// refused, or cancelled unfilled, does not end it, because the algorithm goes on sending and the deadline is what
    /// decides that nothing more is coming.
    /// </summary>
    private void OnPieceEvent(OrderEvent e)
    {
        if (e is not OrderFilled f)
        {
            return;
        }

        FilledQuantity += f.LastQty.Value;
        FilledValue += f.LastQty.Value * f.LastPx.Value;
        FillPrice = FilledValue / FilledQuantity;
        PositionId = f.PositionId ?? PositionId;
        if (FilledQuantity >= WorkedQuantity)
        {
            // The whole size is in. Filled means filled in full for a worked order: a pace that is still going is not
            // a fill, and a node told otherwise would go on to the next thing with half a position.
            PendingFilled = true;
            IsWorking = false;
        }
    }

    public void Refresh(IStrategyServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (Id is not { } id)
        {
            return;
        }

        if (IsWorking)
        {
            Order? order = services.Cache.Order(id);
            if (order is not null && order.IsClosed)
            {
                IsWorking = false;
            }
        }

        if (IsWorked && IsWorking && WorkedUntil is { } until && services.Clock.Timestamp >= until)
        {
            // The algorithm's time is up with less than the whole size filled - a piece the venue refused, or a limit
            // piece nobody took. What filled is what the order got, and the node is told so rather than left waiting on
            // an instruction that nothing more will happen to.
            IsWorking = false;
            if (FilledQuantity > 0m)
            {
                PendingFilled = true;
                LastReason = "worked " + FilledQuantity.ToString(CultureInfo.InvariantCulture) + " of "
                    + WorkedQuantity.ToString(CultureInfo.InvariantCulture) + " before the pace ran out";
            }
        }

        // The venue's fill event may not carry the position id; the execution engine assigns it when the fill is applied.
        PositionId ??= services.Cache.PositionIdFor(id);
    }

    public JsonElement Save() => JsonSerializer.SerializeToElement(new
    {
        id = Id?.Value,
        submittedAt = SubmittedAt,
        working = IsWorking,
        position = PositionId?.Value,
        fillPrice = FillPrice,
        worked = IsWorked,
        workedQuantity = WorkedQuantity,
        filledQuantity = FilledQuantity,
        filledValue = FilledValue,
        workedUntil = WorkedUntil?.Value,
    });

    public void Load(JsonElement state)
    {
        if (state.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String)
        {
            Id = new ClientOrderId(id.GetString()!);
        }

        SubmittedAt = state.TryGetProperty("submittedAt", out JsonElement s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
        IsWorking = state.TryGetProperty("working", out JsonElement w) && w.ValueKind == JsonValueKind.True;
        if (state.TryGetProperty("position", out JsonElement p) && p.ValueKind == JsonValueKind.String)
        {
            PositionId = new PositionId(p.GetString()!);
        }

        if (state.TryGetProperty("fillPrice", out JsonElement fill) && fill.ValueKind == JsonValueKind.Number)
        {
            FillPrice = fill.GetDecimal();
        }

        IsWorked = state.TryGetProperty("worked", out JsonElement worked) && worked.ValueKind == JsonValueKind.True;
        WorkedQuantity = Number(state, "workedQuantity");
        FilledQuantity = Number(state, "filledQuantity");
        FilledValue = Number(state, "filledValue");
        WorkedUntil = state.TryGetProperty("workedUntil", out JsonElement until) && until.ValueKind == JsonValueKind.Number
            ? new UnixNanos(until.GetInt64())
            : null;
    }

    private static decimal Number(JsonElement state, string name) =>
        state.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.Number ? e.GetDecimal() : 0m;
}
