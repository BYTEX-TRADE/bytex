using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Primitives;

namespace Bytex.Core.Model.Orders;

/// <summary>
/// Where the node's own resting orders stand in the market's queue: how much size was ahead of each one at its price
/// when it joined, and how much of that is left.
/// </summary>
/// <remarks>
/// A limit order at the front of the queue and one at the back of it are the same order to a backtest that fills on
/// the first touch, and two completely different trades to whoever placed them. What decides between them is the size
/// that was already resting at that price: until it has been traded away, a print at that price belongs to the people
/// who were there first.
/// <para>
/// Two things move an order forwards, and the difference between them is worth stating because a book alone cannot
/// tell them apart. A print at the price is size traded away from the front of the queue, and that is what
/// <see cref="Consume"/> records. A level that has simply grown smaller since we last looked is size that left for
/// some reason we cannot see - traded against a print we were not shown, or cancelled - and
/// <see cref="Observe"/> takes it as a floor rather than a subtraction, so the two cannot count the same size twice.
/// A level that grows leaves the queue alone: size joining a price joins behind what is already there.
/// </para>
/// <para>
/// What this is not: a venue's queue. A venue matches on its own sequence numbers and we are not given them, so this
/// is the most that can be said from a book and a print feed, and it says it the pessimistic way - an order is never
/// further forward than the data proves.
/// </para>
/// </remarks>
public sealed class OwnOrderBook
{
    private readonly Dictionary<ClientOrderId, Entry> _entries = new();

    /// <summary>What was ahead of an order when it joined the queue at its price, and what is left of it.</summary>
    private sealed class Entry
    {
        public required InstrumentId InstrumentId { get; init; }

        public required OrderSide Side { get; init; }

        public required decimal Price { get; init; }

        public required decimal AheadAtJoin { get; init; }

        public decimal Ahead { get; set; }

        public required UnixNanos Joined { get; init; }
    }

    /// <summary>How many orders are being tracked.</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// An order came to rest at a price. What is resting there already is ahead of it; a price nobody is quoting has
    /// nothing ahead of it. Called again for the same order - a modify that moved its price - it joins the back of the
    /// queue at the new price, which is what a venue does with a repriced order.
    /// </summary>
    public void Joined(Order order, decimal price, OrderBook? book, UnixNanos now)
    {
        decimal ahead = book?.SizeAt(order.Side, price) ?? 0m;
        _entries[order.ClientOrderId] = new Entry
        {
            InstrumentId = order.InstrumentId,
            Side = order.Side,
            Price = price,
            AheadAtJoin = ahead,
            Ahead = ahead,
            Joined = now,
        };
    }

    /// <summary>An order is no longer resting: filled, cancelled, expired or rejected.</summary>
    public void Left(ClientOrderId clientOrderId) => _entries.Remove(clientOrderId);

    /// <summary>Forgets every order.</summary>
    public void Clear() => _entries.Clear();

    /// <summary>
    /// Size traded away at a price, against the orders resting on <paramref name="side"/>: it came out of the front
    /// of the queue, so everything resting at that price moves forwards by that much. Which side was resting is which
    /// side the aggressor took it from, and a caller that cannot tell says so by calling this for both.
    /// </summary>
    public void Consume(InstrumentId instrumentId, OrderSide side, decimal price, decimal quantity)
    {
        foreach (Entry entry in _entries.Values)
        {
            if (entry.InstrumentId == instrumentId && entry.Side == side && entry.Price == price)
            {
                entry.Ahead = Math.Max(0m, entry.Ahead - quantity);
            }
        }
    }

    /// <summary>
    /// A trade went past a price: every order the market traded through - a bid above what somebody sold at, an offer
    /// below what somebody paid - has no queue left, because everything in front of it went with the market. This
    /// does not ask who the aggressor was. A trade cannot happen beyond a resting order without that order's side of
    /// the book having been cleared to reach it, whichever way round the feed reports it.
    /// </summary>
    public void ClearThrough(InstrumentId instrumentId, decimal price)
    {
        foreach (Entry entry in _entries.Values)
        {
            if (entry.InstrumentId != instrumentId)
            {
                continue;
            }

            if (entry.Side == OrderSide.Buy ? entry.Price > price : entry.Price < price)
            {
                entry.Ahead = 0m;
            }
        }
    }

    /// <summary>
    /// What the book says now. A level smaller than what an order believes is ahead of it is proof that some of that
    /// size has gone, whether it was traded or cancelled, so it becomes the new bound. A larger level proves nothing:
    /// size arriving at a price arrives behind what is already resting there.
    /// </summary>
    public void Observe(OrderBook book)
    {
        ArgumentNullException.ThrowIfNull(book);
        if (_entries.Count == 0)
        {
            return;
        }

        foreach (Entry entry in _entries.Values)
        {
            if (entry.InstrumentId != book.InstrumentId)
            {
                continue;
            }

            entry.Ahead = Math.Min(entry.Ahead, book.SizeAt(entry.Side, entry.Price));
        }
    }

    /// <summary>
    /// How much size is still ahead of that order at its price, or null for an order this does not track - one that
    /// never rested, or one that has left the book.
    /// </summary>
    public decimal? SizeAhead(ClientOrderId clientOrderId) => _entries.TryGetValue(clientOrderId, out Entry? entry) ? entry.Ahead : null;

    /// <summary>What was ahead of that order when it joined, so a reader can see how far it has come.</summary>
    public decimal? SizeAheadAtJoin(ClientOrderId clientOrderId) => _entries.TryGetValue(clientOrderId, out Entry? entry) ? entry.AheadAtJoin : null;

    /// <summary>The price an order is being tracked at, so a caller can tell a repriced order from one that has not moved.</summary>
    public decimal? PriceOf(ClientOrderId clientOrderId) => _entries.TryGetValue(clientOrderId, out Entry? entry) ? entry.Price : null;

    /// <summary>
    /// Forgets every order but these. One call from whoever holds the orders is safer than a <see cref="Left"/> on
    /// each of the many ways an order can stop resting, because an order that stops being tracked too late is an
    /// order reported as standing in a queue it left.
    /// </summary>
    public void RetainOnly(IReadOnlyCollection<ClientOrderId> resting)
    {
        if (_entries.Count == 0)
        {
            return;
        }

        foreach (ClientOrderId id in _entries.Keys.Where(id => !resting.Contains(id)).ToList())
        {
            _entries.Remove(id);
        }
    }

    /// <summary>
    /// Where that order stands among the node's own orders at the same price and side: 1 for the one nearest the
    /// front. Its own orders are all a node can rank, because the rest of the queue is anonymous - what it knows
    /// about the strangers ahead of it is their size, which is <see cref="SizeAhead"/>.
    /// </summary>
    public int? QueuePosition(ClientOrderId clientOrderId)
    {
        if (!_entries.TryGetValue(clientOrderId, out Entry? mine))
        {
            return null;
        }

        int position = 1;
        foreach ((ClientOrderId id, Entry other) in _entries)
        {
            if (id == clientOrderId || other.InstrumentId != mine.InstrumentId || other.Side != mine.Side || other.Price != mine.Price)
            {
                continue;
            }

            // Whoever was there first is in front: less size ahead, or the same size ahead and an earlier arrival.
            if (other.Ahead < mine.Ahead || (other.Ahead == mine.Ahead && other.Joined.Value < mine.Joined.Value))
            {
                position++;
            }
        }

        return position;
    }

}
