using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Primitives;

namespace Bytex.Core.Model.Data;

/// <summary>
/// A mutable limit order book maintained by applying deltas or snapshots.
/// Supports L1 (top of book), L2 (price levels), and L3 (individual orders) representations.
/// </summary>
public sealed class OrderBook
{
    private readonly SortedDictionary<decimal, Level> _bids = new(Comparer<decimal>.Create((a, b) => b.CompareTo(a)));
    private readonly SortedDictionary<decimal, Level> _asks = new();
    private readonly Dictionary<ulong, (OrderSide Side, decimal Price)> _orderIndex = new();

    public OrderBook(InstrumentId instrumentId, BookType bookType)
    {
        InstrumentId = instrumentId;
        BookType = bookType;
    }

    public InstrumentId InstrumentId { get; }

    public BookType BookType { get; }

    public ulong Sequence { get; private set; }

    public UnixNanos TsLast { get; private set; }

    public int UpdateCount { get; private set; }

    public Price? BestBidPrice => _bids.Count == 0 ? null : _bids.First().Value.Price;

    public Price? BestAskPrice => _asks.Count == 0 ? null : _asks.First().Value.Price;

    public Quantity? BestBidSize => _bids.Count == 0 ? null : _bids.First().Value.Size;

    public Quantity? BestAskSize => _asks.Count == 0 ? null : _asks.First().Value.Size;

    public decimal? Spread => BestBidPrice is { } bid && BestAskPrice is { } ask ? ask.Value - bid.Value : null;

    public decimal? MidPrice => BestBidPrice is { } bid && BestAskPrice is { } ask ? (ask.Value + bid.Value) / 2m : null;

    public int BidLevelCount => _bids.Count;

    public int AskLevelCount => _asks.Count;

    public IEnumerable<BookLevel> Bids(int depth = int.MaxValue) => _bids.Values.Take(depth).Select(l => new BookLevel(l.Price, l.Size, l.Count));

    public IEnumerable<BookLevel> Asks(int depth = int.MaxValue) => _asks.Values.Take(depth).Select(l => new BookLevel(l.Price, l.Size, l.Count));

    public void Apply(OrderBookDelta delta)
    {
        switch (delta.Action)
        {
            case BookAction.Clear:
                Clear();
                break;
            case BookAction.Add:
                Add(delta.Order);
                break;
            case BookAction.Update:
                Update(delta.Order);
                break;
            case BookAction.Delete:
                Delete(delta.Order);
                break;
        }

        Sequence = delta.Sequence;
        TsLast = delta.TsEvent;
        UpdateCount++;
    }

    public void Apply(OrderBookDeltas deltas)
    {
        foreach (OrderBookDelta delta in deltas.Deltas)
        {
            Apply(delta);
        }
    }

    public void Apply(OrderBookDepth depth)
    {
        Clear();
        ulong id = 1;
        foreach (BookLevel level in depth.Bids)
        {
            Add(new BookOrder(OrderSide.Buy, level.Price, level.Size, id++));
        }

        foreach (BookLevel level in depth.Asks)
        {
            Add(new BookOrder(OrderSide.Sell, level.Price, level.Size, id++));
        }

        Sequence = depth.Sequence;
        TsLast = depth.TsEvent;
        UpdateCount++;
    }

    /// <summary>
    /// Updates an L1 book from a quote.
    /// </summary>
    public void Apply(QuoteTick quote)
    {
        Clear();
        Add(new BookOrder(OrderSide.Buy, quote.Bid, quote.BidSize, 1));
        Add(new BookOrder(OrderSide.Sell, quote.Ask, quote.AskSize, 2));
        TsLast = quote.TsEvent;
        UpdateCount++;
    }

    public void Clear()
    {
        _bids.Clear();
        _asks.Clear();
        _orderIndex.Clear();
    }

    private void Add(BookOrder order)
    {
        if (order.Size.IsZero)
        {
            Delete(order);
            return;
        }

        SortedDictionary<decimal, Level> side = SideOf(order.Side);
        if (BookType == BookType.L1)
        {
            side.Clear();
            _orderIndex.Clear();
        }

        if (!side.TryGetValue(order.Price.Value, out Level? level))
        {
            level = new Level(order.Price);
            side[order.Price.Value] = level;
        }

        if (BookType == BookType.L3)
        {
            level.Orders[order.OrderId] = order.Size;
            _orderIndex[order.OrderId] = (order.Side, order.Price.Value);
            level.Recompute();
        }
        else
        {
            level.Set(order.Size);
        }
    }

    private void Update(BookOrder order)
    {
        if (order.Size.IsZero)
        {
            Delete(order);
            return;
        }

        if (BookType == BookType.L3 && _orderIndex.TryGetValue(order.OrderId, out (OrderSide Side, decimal Price) existing) && existing.Price != order.Price.Value)
        {
            Delete(new BookOrder(existing.Side, new Price(existing.Price, order.Price.Precision), order.Size, order.OrderId));
        }

        Add(order);
    }

    private void Delete(BookOrder order)
    {
        SortedDictionary<decimal, Level> side = SideOf(order.Side);
        if (BookType == BookType.L3)
        {
            if (_orderIndex.TryGetValue(order.OrderId, out (OrderSide Side, decimal Price) existing) && side.TryGetValue(existing.Price, out Level? l3))
            {
                l3.Orders.Remove(order.OrderId);
                _orderIndex.Remove(order.OrderId);
                l3.Recompute();
                if (l3.Orders.Count == 0)
                {
                    side.Remove(existing.Price);
                }
            }

            return;
        }

        side.Remove(order.Price.Value);
    }

    private SortedDictionary<decimal, Level> SideOf(OrderSide side) => side == OrderSide.Buy ? _bids : _asks;

    /// <summary>
    /// Simulates walking the book for a given quantity and returns the average fill price, or null if liquidity is insufficient.
    /// </summary>
    public Price? SimulateAveragePrice(OrderSide side, Quantity quantity)
    {
        SortedDictionary<decimal, Level> levels = side == OrderSide.Buy ? _asks : _bids;
        decimal remaining = quantity.Value;
        decimal cost = 0m;
        byte precision = 0;
        foreach (Level level in levels.Values)
        {
            precision = level.Price.Precision;
            decimal take = Math.Min(remaining, level.Size.Value);
            cost += take * level.Price.Value;
            remaining -= take;
            if (remaining <= 0m)
            {
                break;
            }
        }

        if (remaining > 0m || quantity.IsZero)
        {
            return null;
        }

        return new Price(cost / quantity.Value, precision);
    }

    /// <summary>
    /// Returns the fills obtained by walking the book for a quantity, best price first.
    /// </summary>
    public IReadOnlyList<(Price Price, Quantity Size)> SimulateFills(OrderSide side, Quantity quantity, Price? limitPrice = null)
    {
        SortedDictionary<decimal, Level> levels = side == OrderSide.Buy ? _asks : _bids;
        List<(Price, Quantity)> fills = new();
        decimal remaining = quantity.Value;
        foreach (Level level in levels.Values)
        {
            if (limitPrice is { } limit)
            {
                bool crosses = side == OrderSide.Buy ? level.Price.Value <= limit.Value : level.Price.Value >= limit.Value;
                if (!crosses)
                {
                    break;
                }
            }

            decimal take = Math.Min(remaining, level.Size.Value);
            fills.Add((level.Price, new Quantity(take, quantity.Precision)));
            remaining -= take;
            if (remaining <= 0m)
            {
                break;
            }
        }

        return fills;
    }

    public override string ToString() => $"OrderBook({InstrumentId}, {BookType}, bid={BestBidPrice}, ask={BestAskPrice})";

    private sealed class Level
    {
        public Level(Price price)
        {
            Price = price;
            Size = Quantity.Zero(0);
        }

        public Price Price { get; }

        public Quantity Size { get; private set; }

        public int Count => Orders.Count == 0 ? 1 : Orders.Count;

        public Dictionary<ulong, Quantity> Orders { get; } = new();

        public void Set(Quantity size) => Size = size;

        public void Recompute()
        {
            decimal total = 0m;
            byte precision = 0;
            foreach (Quantity q in Orders.Values)
            {
                total += q.Value;
                precision = Math.Max(precision, q.Precision);
            }

            Size = new Quantity(total, precision);
        }
    }
}
