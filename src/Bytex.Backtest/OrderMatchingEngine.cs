using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;

namespace Bytex.Backtest;

/// <summary>
/// Callbacks the matching engine uses to report outcomes to the owning exchange.
/// </summary>
internal interface IMatchingEngineHost
{
    VenueOrderId NextVenueOrderId();

    TradeId NextTradeId();

    decimal NetPosition(InstrumentId instrumentId);

    void OnAccepted(Order order, VenueOrderId venueOrderId, UnixNanos ts);

    void OnRejected(Order order, string reason, UnixNanos ts);

    void OnCanceled(Order order, UnixNanos ts);

    void OnExpired(Order order, UnixNanos ts);

    void OnTriggered(Order order, UnixNanos ts);

    void OnUpdated(Order order, Quantity quantity, Price? price, Price? triggerPrice, UnixNanos ts);

    void OnModifyRejected(Order order, string reason, UnixNanos ts);

    void OnCancelRejected(Order order, string reason, UnixNanos ts);

    void OnFilled(Order order, Quantity lastQty, Price lastPx, LiquiditySide liquiditySide, UnixNanos ts);
}

/// <summary>
/// Simulates a venue's matching for one instrument against quotes, trades, and bars.
/// </summary>
internal sealed class OrderMatchingEngine
{
    private readonly Instrument _instrument;
    private readonly IMatchingEngineHost _host;
    private readonly FillModel _fillModel;
    private readonly BarExecutionMode _barMode;
    private readonly bool _rejectStopOrdersAtMarket;
    private readonly FillSizing _fillSizing;
    private readonly decimal? _barVolumeShare;
    private readonly List<Order> _orders = new();
    private readonly Dictionary<ClientOrderId, VenueOrderId> _venueIds = new();
    private readonly HashSet<ClientOrderId> _triggered = new();

    // True only while replaying the inside of a bar, where the price is assumed to have traded through everything
    // between the last step and this one. Between bars, and on a quote or a trade, it jumps, so a trigger in between
    // was never traded at.
    private bool _continuousMove;
    private readonly Dictionary<ClientOrderId, decimal> _trailingTriggers = new();
    private readonly Dictionary<ClientOrderId, decimal> _pendingActivation = new();
    private decimal? _bid;
    private decimal? _ask;
    private decimal? _last;
    private bool _quoted;
    private decimal? _print;
    private UnixNanos _now;

    // What is on offer at the touch, and what orders have already taken of it in this event. Bars carry a volume for a
    // whole bar rather than a size at a price, so a bar-driven run leaves these null: nothing there bounds a fill.
    private decimal? _bidSize;
    private decimal? _askSize;
    private decimal? _printSize;
    private decimal _takenBid;
    private decimal _takenAsk;
    private decimal _takenPrint;

    /// <summary>
    /// What is left of the share of this bar's volume one participant may take. A bar is one length of time and one
    /// budget: the four prices of its path draw on the same one, so an order cannot take the share four times over by
    /// being filled at each of them.
    /// </summary>
    private decimal? _barBudget;

    /// <summary>
    /// The book the venue is matching against, when it has one. A touch is one price and one size; a book is every
    /// price and every size, which is what lets an order larger than the touch pay for the levels it eats rather than
    /// waiting for them.
    /// </summary>
    private OrderBook? _book;

    /// <summary>What orders have already taken from each price of the current book state.</summary>
    private readonly Dictionary<decimal, decimal> _takenAtLevel = new();

    /// <summary>
    /// Whether the price the venue is on came off a bar. A bar covers a length of time and a book is one moment
    /// inside it or before it, so a fill on a bar is bounded by the bar's own share and never by walking a book.
    /// </summary>
    private bool _fromBar;

    /// <summary>
    /// Where this venue's resting orders stand in the queue at their price. Read while an order is being matched and
    /// advanced afterwards, so a print bounds the fill by the queue as it stood when the print arrived rather than by
    /// the queue the print itself emptied.
    /// </summary>
    private readonly OwnOrderBook _queue = new();

    public OrderMatchingEngine(Instrument instrument, IMatchingEngineHost host, FillModel fillModel, BarExecutionMode barMode, bool rejectStopOrdersAtMarket = true, OmsType oms = OmsType.Netting, FillSizing fillSizing = FillSizing.AvailableSize, decimal? barVolumeShare = null)
    {
        _instrument = instrument;
        _host = host;
        _fillModel = fillModel;
        _barMode = barMode;
        _rejectStopOrdersAtMarket = rejectStopOrdersAtMarket;
        _hedging = oms == OmsType.Hedging;
        _fillSizing = fillSizing;
        _barVolumeShare = barVolumeShare;
    }

    /// <summary>
    /// Whether the account keeps its legs apart. The venue's own book is netted whatever the account does, so on a
    /// hedged book it cannot tell which leg a reduce-only order is closing: long 1 and short 1 net to nothing, and
    /// judging either close against that net rejects it. Under hedging the leg is the execution engine's business -
    /// it applies the fill to the position the order names - so the venue takes the order and does not clamp it.
    /// </summary>
    private readonly bool _hedging;

    public InstrumentId InstrumentId => _instrument.Id;

    public decimal? BestBid => _bid;

    public decimal? BestAsk => _ask;

    public decimal? LastPrice => _last;

    public IReadOnlyList<Order> OpenOrders => _orders;

    public int OpenOrderCount => _orders.Count;

    private decimal Tick => _instrument.PriceIncrement.Value;

    // ----- Market data -----

    public void ProcessQuote(QuoteTick quote)
    {
        _now = quote.TsEvent;
        _bid = quote.Bid.Value;
        _ask = quote.Ask.Value;
        _bidSize = quote.BidSize.Value;
        _askSize = quote.AskSize.Value;
        _barBudget = null;
        _fromBar = false;
        _quoted = true;
        NewTouch();
        Iterate();
    }

    public void ProcessTrade(TradeTick trade)
    {
        _now = trade.TsEvent;
        _last = trade.Price.Value;
        _printSize = trade.Size.Value;
        _barBudget = null;
        _fromBar = false;
        NewTouch();
        if (!_quoted)
        {
            // Trade-only data: the print is the whole market, so both sides follow it. Dragging each side outwards and
            // never back left the bid at the lowest print of the run and the ask at the highest, which is not a market.
            _bid = _last;
            _ask = _last;
        }
        else
        {
            // Beside real quotes, a print outside the spread drags the side it went through until the next quote.
            if (_last < _bid)
            {
                _bid = _last;
            }

            if (_last > _ask)
            {
                _ask = _last;
            }
        }

        // A trade past a resting order is proof that the queue in front of it had already gone: the market could
        // not have reached that price with it still standing. That was true before this print, so the order is
        // matched against a queue that is already empty.
        _queue.ClearThrough(_instrument.Id, trade.Price.Value);

        _print = _last;
        try
        {
            Iterate();
        }
        finally
        {
            _print = null;
        }

        // A trade at the price is the other way round: it is what the orders in front of ours were waiting for, and
        // ours was behind them for the whole of it, so the queue moves after the matching rather than before it.
        // Which queue it served is the side the aggressor took it from; a print naming no aggressor is taken to have
        // hit both.
        if (trade.Aggressor != AggressorSide.Seller)
        {
            _queue.Consume(_instrument.Id, OrderSide.Sell, trade.Price.Value, trade.Size.Value);
        }

        if (trade.Aggressor != AggressorSide.Buyer)
        {
            _queue.Consume(_instrument.Id, OrderSide.Buy, trade.Price.Value, trade.Size.Value);
        }
    }

    public void ProcessBar(Bar bar)
    {
        _now = bar.TsEvent;
        _barBudget = _fillSizing == FillSizing.AvailableSize && _barVolumeShare is { } share && share > 0m
            ? bar.Volume.Value * share
            : null;

        if (_barMode == BarExecutionMode.CloseOnly)
        {
            SetPrice(bar.Close.Value);
            Iterate();
            return;
        }

        decimal toHigh = bar.High.Value - bar.Open.Value;
        decimal toLow = bar.Open.Value - bar.Low.Value;
        bool highFirst = toHigh < toLow || (toHigh == toLow && bar.Close < bar.Open);
        decimal[] path = highFirst
            ? [bar.Open.Value, bar.High.Value, bar.Low.Value, bar.Close.Value]
            : [bar.Open.Value, bar.Low.Value, bar.High.Value, bar.Close.Value];

        for (int i = 0; i < path.Length; i++)
        {
            SetPrice(path[i]);
            _continuousMove = i > 0;
            Iterate(isBarOpen: i == 0, isBarClose: i == path.Length - 1);
        }

        _continuousMove = false;
    }

    public void ProcessBook(OrderBook book, UnixNanos ts)
    {
        _now = ts;
        _bid = book.BestBidPrice?.Value;
        _ask = book.BestAskPrice?.Value;
        _bidSize = book.BestBidSize?.Value;
        _askSize = book.BestAskSize?.Value;
        _book = book;
        _barBudget = null;
        _fromBar = false;
        _quoted |= _bid is not null || _ask is not null;
        _queue.Observe(book);
        NewTouch();
        Iterate();
    }

    private void SetPrice(decimal price)
    {
        _bid = price;
        _ask = price;
        _last = price;

        // A bar says what traded over its whole length rather than what was on offer at this price, so what it offers
        // is a share of that: what one participant could plausibly have been. Every price on the bar's path offers
        // what is left of the same budget.
        _bidSize = _barBudget;
        _askSize = _barBudget;
        _printSize = null;
        _fromBar = true;
        NewTouch();
    }

    /// <summary>Fresh liquidity: what the last touch offered has been taken or has gone.</summary>
    private void NewTouch()
    {
        _takenBid = 0m;
        _takenAsk = 0m;
        _takenPrint = 0m;
        _takenAtLevel.Clear();
    }

    /// <summary>
    /// What is left at one price of the book: what the level holds, less what orders have already taken from it at
    /// this state of the book.
    /// </summary>
    private decimal LeftAt(decimal price, decimal size) => Math.Max(0m, size - _takenAtLevel.GetValueOrDefault(price));

    /// <summary>
    /// The levels an order that takes would eat, best price first, no further than its limit. A market order has no
    /// limit and eats as deep as the book goes.
    /// </summary>
    private IEnumerable<(decimal Price, decimal Size)> Reachable(Order order, decimal? limit)
    {
        if (_book is null)
        {
            yield break;
        }

        foreach (BookLevel level in order.IsBuy ? _book.Asks() : _book.Bids())
        {
            if (limit is { } bound && (order.IsBuy ? level.Price.Value > bound : level.Price.Value < bound))
            {
                yield break;
            }

            decimal left = LeftAt(level.Price.Value, level.Size.Value);
            if (left > 0m)
            {
                yield return (level.Price.Value, left);
            }
        }
    }

    /// <summary>Where the size an order can take at this moment comes from, if anywhere.</summary>
    private enum Liquidity
    {
        /// <summary>Nothing here says what was on offer, so nothing bounds the fill.</summary>
        None,

        /// <summary>The print that reached a resting order.</summary>
        Print,

        /// <summary>The bid: what a seller can hit.</summary>
        Bid,

        /// <summary>The ask: what a buyer can lift.</summary>
        Ask,
    }

    /// <summary>
    /// Which pool of size this fill comes out of. An order that rests is filled by the print that reached it, and
    /// where there is no print - a quote that crossed it - by the side of the market that came to meet it, which is
    /// the same pool an order taking that side would eat.
    /// </summary>
    private Liquidity PoolFor(Order order, LiquiditySide liquiditySide)
    {
        if (_fillSizing == FillSizing.WholeFills)
        {
            return Liquidity.None;
        }

        if (liquiditySide == LiquiditySide.Maker && _printSize is not null)
        {
            return Liquidity.Print;
        }

        if (order.IsBuy)
        {
            return _askSize is null ? Liquidity.None : Liquidity.Ask;
        }

        return _bidSize is null ? Liquidity.None : Liquidity.Bid;
    }

    /// <summary>
    /// What is left in that pool, or null when nothing bounds the fill. Several orders share one touch: whatever the
    /// first takes is not there for the second.
    /// </summary>
    private decimal? Offered(Liquidity pool) => pool switch
    {
        Liquidity.Print => Math.Max(0m, (_printSize ?? 0m) - _takenPrint),
        Liquidity.Bid => Math.Max(0m, (_bidSize ?? 0m) - _takenBid),
        Liquidity.Ask => Math.Max(0m, (_askSize ?? 0m) - _takenAsk),
        _ => null,
    };

    /// <summary>
    /// How many fills this engine bounded, and how much went in under a bound, so a run can say what it assumed
    /// rather than asking to be trusted. What was held back is not counted: an order bounded twice is held back twice
    /// for the same quantity, and adding those up is a number nobody can read.
    /// </summary>
    public int BoundedFills { get; private set; }

    /// <inheritdoc cref="BoundedFills"/>
    public decimal BoundedQuantity { get; private set; }

    /// <summary>Whether any fill here came out of walking a book, rather than out of a touch or a bar's share.</summary>
    public bool WalkedTheBook { get; private set; }

    /// <summary>Records what an order took, so the next one at the same touch finds that much less.</summary>
    private void Take(Liquidity pool, decimal quantity)
    {
        if (_barBudget is { } budget && pool is Liquidity.Bid or Liquidity.Ask)
        {
            _barBudget = Math.Max(0m, budget - quantity);
        }

        switch (pool)
        {
            case Liquidity.Print:
                _takenPrint += quantity;
                break;
            case Liquidity.Bid:
                _takenBid += quantity;
                break;
            case Liquidity.Ask:
                _takenAsk += quantity;
                break;
            default:
                break;
        }
    }

    // ----- Commands -----

    /// <summary>
    /// Works an order. <c>announcedAs</c> is the venue order id an order was already announced under, for one that was
    /// accepted while it waited for something else - an OTO child holding for its parent. Such an order keeps that id
    /// and is not announced as accepted a second time.
    /// </summary>
    public void ProcessOrder(Order order, UnixNanos ts, VenueOrderId? announcedAs = null)
    {
        try
        {
            SubmitOrder(order, ts, announcedAs);
        }
        finally
        {
            ReconcileQueue();
        }
    }

    private void SubmitOrder(Order order, UnixNanos ts, VenueOrderId? announcedAs)
    {
        _now = ts;
        if (_venueIds.ContainsKey(order.ClientOrderId))
        {
            _host.OnRejected(order, "duplicate client order id", ts);
            return;
        }

        if (order.IsReduceOnly && !_hedging && !WouldReduce(order))
        {
            _host.OnRejected(order, "reduce-only order would increase position", ts);
            return;
        }

        if (order.IsPostOnly && order.Price is { } postPrice && Crosses(order.Side, postPrice.Value))
        {
            _host.OnRejected(order, "post-only order would have crossed the book", ts);
            return;
        }

        if (_rejectStopOrdersAtMarket && order.TriggerPrice is { } trigger && order.Type is OrderType.StopMarket or OrderType.StopLimit && IsStopTriggered(order.Side, trigger.Value, order.TriggerType))
        {
            _host.OnRejected(order, $"stop trigger {trigger} is already through the market (bid={_bid}, ask={_ask})", ts);
            return;
        }

        VenueOrderId venueOrderId = announcedAs ?? _host.NextVenueOrderId();
        _venueIds[order.ClientOrderId] = venueOrderId;
        void Accept()
        {
            if (announcedAs is null)
            {
                _host.OnAccepted(order, venueOrderId, ts);
            }
        }

        if (order.Type == OrderType.Market || order.Type == OrderType.MarketToLimit)
        {
            if (order.TimeInForce is TimeInForce.AtTheOpen or TimeInForce.AtTheClose)
            {
                _orders.Add(order);
                Accept();
                return;
            }

            Accept();
            FillMarket(order, LiquiditySide.Taker);
            return;
        }

        // The order is in the book before its owner is told it was accepted: anything the owner does on that news - a
        // cancel it owed the order, for one - reaches a venue that already holds it.
        _orders.Add(order);
        Accept();
        if (order.Type is OrderType.TrailingStopMarket or OrderType.TrailingStopLimit)
        {
            InitializeTrailing(order);
        }

        if (AwaitingActivation(order))
        {
            return;
        }

        MatchOrder(order, aggressive: true);
    }

    /// <summary>
    /// Puts back an order that was resting here before a restart. Nothing is checked and no event is raised: the order was
    /// accepted once already, and its owner knows it as accepted. A trailing order keeps trailing from the trigger it had
    /// reached, so it is never reset to a trigger measured from today's price.
    /// </summary>
    public void RestoreOrder(Order order, VenueOrderId venueOrderId)
    {
        try
        {
            PutBack(order, venueOrderId);
        }
        finally
        {
            ReconcileQueue();
        }
    }

    private void PutBack(Order order, VenueOrderId venueOrderId)
    {
        ArgumentNullException.ThrowIfNull(order);
        bool trailing = order.Type is OrderType.TrailingStopMarket or OrderType.TrailingStopLimit;
        if (order.Type is OrderType.Market or OrderType.MarketToLimit && order.TimeInForce is not (TimeInForce.AtTheOpen or TimeInForce.AtTheClose))
        {
            throw new ArgumentException($"A {order.Type} order with time in force {order.TimeInForce} does not rest at a venue, so there is nothing to restore.", nameof(order));
        }

        if (trailing && order.TriggerPrice is null)
        {
            throw new ArgumentException($"A {order.Type} order can only be restored with the trigger price it had reached; without it the trail would start again from today's price.", nameof(order));
        }

        if (_venueIds.ContainsKey(order.ClientOrderId))
        {
            throw new InvalidOperationException($"Order {order.ClientOrderId} is already at the venue.");
        }

        _venueIds[order.ClientOrderId] = venueOrderId;
        _orders.Add(order);
        if (trailing)
        {
            _trailingTriggers[order.ClientOrderId] = order.TriggerPrice!.Value.Value;
        }
    }

    public void ProcessModify(Order order, Quantity? quantity, Price? price, Price? triggerPrice, UnixNanos ts)
    {
        try
        {
            ModifyOrder(order, quantity, price, triggerPrice, ts);
        }
        finally
        {
            ReconcileQueue();
        }
    }

    private void ModifyOrder(Order order, Quantity? quantity, Price? price, Price? triggerPrice, UnixNanos ts)
    {
        _now = ts;
        if (!_orders.Contains(order))
        {
            _host.OnModifyRejected(order, "order not open at venue", ts);
            return;
        }

        if (order.IsPostOnly && price is { } p && Crosses(order.Side, p.Value))
        {
            _host.OnModifyRejected(order, "post-only modification would have crossed the book", ts);
            return;
        }

        Quantity newQuantity = quantity ?? order.Quantity;
        if (newQuantity <= order.FilledQuantity)
        {
            _host.OnModifyRejected(order, "new quantity does not exceed filled quantity", ts);
            return;
        }

        _host.OnUpdated(order, newQuantity, price ?? order.Price, triggerPrice ?? order.TriggerPrice, ts);
        if (triggerPrice is not null && _trailingTriggers.ContainsKey(order.ClientOrderId))
        {
            _trailingTriggers[order.ClientOrderId] = triggerPrice.Value.Value;
        }

        MatchOrder(order, aggressive: true);
    }

    public void ProcessCancel(Order order, UnixNanos ts)
    {
        _now = ts;
        if (!_orders.Remove(order))
        {
            _host.OnCancelRejected(order, "order not open at venue", ts);
            return;
        }

        Cleanup(order);
        ReconcileQueue();
        _host.OnCanceled(order, ts);
    }

    public void ProcessCancelAll(OrderSide? side, StrategyId? strategyId, UnixNanos ts)
    {
        _now = ts;
        foreach (Order order in _orders.Where(o => (side is null || o.Side == side) && (strategyId is null || o.StrategyId == strategyId)).ToList())
        {
            _orders.Remove(order);
            Cleanup(order);
            ReconcileQueue();
            _host.OnCanceled(order, ts);
        }
    }

    public VenueOrderId? VenueOrderIdFor(ClientOrderId id) => _venueIds.TryGetValue(id, out VenueOrderId v) ? v : null;

    // ----- Matching -----

    private void Iterate(bool isBarOpen = false, bool isBarClose = false)
    {
        try
        {
            IterateOrders(isBarOpen, isBarClose);
        }
        finally
        {
            ReconcileQueue();
        }
    }

    /// <summary>
    /// Brings the queue into step with what is actually resting here: an order that has come to rest at a price joins
    /// the back of the queue at that price, one that was repriced joins the back of the new one, and one that has
    /// stopped resting is forgotten. Doing it in one place rather than on each of the many ways an order arrives or
    /// leaves is what keeps an order from being reported as standing in a queue it is not in.
    /// </summary>
    private void ReconcileQueue()
    {
        if (_orders.Count == 0 && _queue.Count == 0)
        {
            return;
        }

        List<ClientOrderId> resting = new();
        foreach (Order order in _orders)
        {
            if (RestingPrice(order) is not { } price)
            {
                continue;
            }

            resting.Add(order.ClientOrderId);
            if (_queue.PriceOf(order.ClientOrderId) != price)
            {
                _queue.Joined(order, price, _book, _now);
            }
        }

        _queue.RetainOnly(resting);
    }

    /// <summary>
    /// The price an order is standing in a queue at, or null for one that is not standing in any: a stop still
    /// waiting for its trigger is a promise to the venue rather than size in the book, and a market order never rests
    /// at a price at all.
    /// </summary>
    private decimal? RestingPrice(Order order)
    {
        if (order.Price is not { } price || AwaitingActivation(order))
        {
            return null;
        }

        return order.Type switch
        {
            OrderType.Limit or OrderType.MarketToLimit => price.Value,
            OrderType.StopLimit or OrderType.LimitIfTouched => _triggered.Contains(order.ClientOrderId) ? price.Value : null,
            _ => null,
        };
    }

    /// <summary>How much size is still ahead of that order at its price, for whoever asks the venue.</summary>
    public decimal? SizeAhead(ClientOrderId clientOrderId) => _queue.SizeAhead(clientOrderId);

    /// <inheritdoc cref="OwnOrderBook.QueuePosition"/>
    public int? QueuePosition(ClientOrderId clientOrderId) => _queue.QueuePosition(clientOrderId);

    private void IterateOrders(bool isBarOpen, bool isBarClose)
    {
        if (_orders.Count == 0)
        {
            return;
        }

        foreach (Order order in _orders.ToList())
        {
            if (!_orders.Contains(order))
            {
                continue;
            }

            if (order.ExpireTime is { } expire && _now >= expire)
            {
                _orders.Remove(order);
                Cleanup(order);
                _host.OnExpired(order, _now);
                continue;
            }

            // A day order does not outlive the day it was placed on. Whatever session calendar a venue keeps, the UTC
            // date rolling over is the end of it; without this the order rested like a GTC one and could fill weeks on.
            if (order.TimeInForce == TimeInForce.Day && Day(_now) > Day(order.TsAccepted ?? order.TsInit))
            {
                _orders.Remove(order);
                Cleanup(order);
                _host.OnExpired(order, _now);
                continue;
            }

            if (AwaitingActivation(order))
            {
                continue;
            }

            if (order.TimeInForce == TimeInForce.AtTheOpen && isBarOpen || order.TimeInForce == TimeInForce.AtTheClose && isBarClose)
            {
                FillAtTheBell(order);
                continue;
            }

            UpdateTrailing(order);
            MatchOrder(order, aggressive: false);
        }
    }

    private void MatchOrder(Order order, bool aggressive)
    {
        if (_bid is null && _ask is null)
        {
            return;
        }

        bool triggered = _triggered.Contains(order.ClientOrderId);

        switch (order.Type)
        {
            case OrderType.Limit:
                MatchLimit(order, order.Price!.Value.Value, aggressive);
                break;

            case OrderType.StopMarket:
            case OrderType.TrailingStopMarket:
                if (!triggered && IsStopTriggered(order.Side, CurrentTrigger(order), order.TriggerType))
                {
                    Trigger(order);
                    FillStopMarket(order);
                }

                break;

            case OrderType.StopLimit:
            case OrderType.TrailingStopLimit:
                if (!triggered && IsStopTriggered(order.Side, CurrentTrigger(order), order.TriggerType))
                {
                    Trigger(order);
                    MatchLimit(order, order.Price!.Value.Value, aggressive: true);
                }
                else if (triggered)
                {
                    MatchLimit(order, order.Price!.Value.Value, aggressive: false);
                }

                break;

            case OrderType.Market:
                // Here only as what is left of an order working at a participation rate: it keeps taking the market.
                FillMarket(order, LiquiditySide.Taker);
                break;

            case OrderType.MarketToLimit:
                // A remainder that could not be filled whole has taken the market's price and is a limit order at it
                // from then on. One waiting for the opening or the closing has no price yet and waits for its bell.
                if (order.Price is { } resting)
                {
                    MatchLimit(order, resting.Value, aggressive);
                }

                break;

            case OrderType.MarketIfTouched:
                if (!triggered && IsTouched(order.Side, order.TriggerPrice!.Value.Value, order.TriggerType))
                {
                    Trigger(order);
                    _orders.Remove(order);
                    FillMarket(order, LiquiditySide.Taker, order.TriggerPrice!.Value.Value);
                }

                break;

            case OrderType.LimitIfTouched:
                if (!triggered && IsTouched(order.Side, order.TriggerPrice!.Value.Value, order.TriggerType))
                {
                    Trigger(order);
                    MatchLimit(order, order.Price!.Value.Value, aggressive: true);
                }
                else if (triggered)
                {
                    MatchLimit(order, order.Price!.Value.Value, aggressive: false);
                }

                break;
        }
    }

    private void MatchLimit(Order order, decimal limitPrice, bool aggressive)
    {
        decimal? opposite = order.IsBuy ? _ask : _bid;
        if (opposite is null)
        {
            return;
        }

        bool through = order.IsBuy ? opposite.Value < limitPrice : opposite.Value > limitPrice;
        bool touch = opposite.Value == limitPrice;

        if (aggressive && (through || touch))
        {
            // Crossing on arrival takes as far into the book as its own price allows.
            Take(order, opposite.Value, limitPrice);
            return;
        }

        if (!aggressive && (through || (touch && _fillModel.IsLimitFilled())))
        {
            Fill(order, limitPrice, LiquiditySide.Maker);
            return;
        }

        if (!aggressive && _print is { } printed)
        {
            // A trade printed through a resting order is a trade that would have taken it: somebody paid more than our
            // offer, or sold for less than our bid, while we were in the book ahead of them. Beside real quotes this is
            // the only sign of it, because a print outside the spread moves the far side, which a resting order on the
            // near side never looks at. A print exactly at the price is a touch: the fill model decides, as on a quote.
            bool printedThrough = order.IsBuy ? printed < limitPrice : printed > limitPrice;
            if (printedThrough || (printed == limitPrice && _fillModel.IsLimitFilled()))
            {
                Fill(order, limitPrice, LiquiditySide.Maker);
                return;
            }
        }

        if (aggressive && order.TimeInForce is TimeInForce.Ioc or TimeInForce.Fok)
        {
            _orders.Remove(order);
            Cleanup(order);
            _host.OnCanceled(order, _now);
        }
    }

    /// <summary>
    /// An order that takes part in the opening or the closing. A limit order keeps its limit there: it fills only if the
    /// price of the auction is at or inside it, and otherwise expires, because there is no later auction for it to wait
    /// for. Anything without a price fills at the auction price.
    /// </summary>
    private void FillAtTheBell(Order order)
    {
        _orders.Remove(order);
        decimal? opposite = order.IsBuy ? _ask : _bid;
        if (order.Price is { } limit && order.Type is OrderType.Limit or OrderType.StopLimit or OrderType.LimitIfTouched && opposite is { } reference)
        {
            if (order.IsBuy ? reference <= limit.Value : reference >= limit.Value)
            {
                Fill(order, reference, LiquiditySide.Taker);
            }
            else
            {
                Cleanup(order);
                _host.OnExpired(order, _now);
            }

            return;
        }

        FillMarket(order, LiquiditySide.Taker);
    }

    private void FillMarket(Order order, LiquiditySide side, decimal? triggeredAt = null)
    {
        decimal? opposite = order.IsBuy ? _ask : _bid;
        if (opposite is null)
        {
            _orders.Remove(order);
            _host.OnRejected(order, "no market price available", _now);
            return;
        }

        // The price traded through the trigger to get here, so that is where the order became a market order. Filling
        // at the extreme the path reached would be a price no real fill could have had.
        decimal price = _continuousMove && triggeredAt is { } touched ? touched : opposite.Value;
        if (_fillModel.IsSlipped())
        {
            price += order.IsBuy ? Tick : -Tick;
        }

        if (order.Type == OrderType.MarketToLimit)
        {
            _host.OnUpdated(order, order.Quantity, _instrument.MakePrice(price), null, _now);
        }

        if (side == LiquiditySide.Taker)
        {
            // A market-to-limit order takes no further than the price it came to rest at; a market order has no such
            // bound and eats as deep as the book goes.
            Take(order, price, order.Type == OrderType.MarketToLimit ? price : null);
            return;
        }

        Fill(order, price, side);
    }

    private void FillStopMarket(Order order)
    {
        decimal? opposite = order.IsBuy ? _ask : _bid;
        if (opposite is null)
        {
            return;
        }

        decimal price = _continuousMove ? CurrentTrigger(order) : opposite.Value;
        if (!_fillModel.IsStopFilled())
        {
            price += order.IsBuy ? Tick : -Tick;
        }

        Take(order, price, null);
    }

    /// <summary>
    /// An order that takes. With a book, it eats the levels it can reach, best price first, paying what each one
    /// costs - a size larger than the touch pays for the depth it takes rather than waiting for it, which is the
    /// difference between the touch and a book. Without a book it fills at the touch as it did before.
    /// </summary>
    private void Take(Order order, decimal touchPrice, decimal? limit)
    {
        if (_fillSizing == FillSizing.WholeFills || _book is null || _fromBar)
        {
            Fill(order, touchPrice, LiquiditySide.Taker);
            return;
        }

        // Fill or kill against a book is decided over the whole book it can reach, not one level at a time.
        if (order.TimeInForce == TimeInForce.Fok
            && Reachable(order, limit).Sum(l => l.Size) < order.LeavesQuantity.Value)
        {
            Close(order);
            Cancel(order);
            return;
        }

        foreach ((decimal price, decimal size) in Reachable(order, limit).ToList())
        {
            if (order.LeavesQuantity.Value <= 0m || order.IsClosed)
            {
                break;
            }

            decimal before = order.FilledQuantity.Value;
            Fill(order, price, LiquiditySide.Taker, size, walking: true);
            decimal took = order.FilledQuantity.Value - before;
            if (took <= 0m)
            {
                break;
            }

            _takenAtLevel[price] = _takenAtLevel.GetValueOrDefault(price) + took;
            WalkedTheBook = true;
        }

        if (order.IsClosed || order.LeavesQuantity.Value <= 0m)
        {
            return;
        }

        // The walk is over and the order is not done: it has eaten everything it could reach. What becomes of the
        // rest is the same rule as anywhere else - a market order and an immediate-or-cancel order give it up, a
        // market-to-limit order rests it at the price it got, a limit order goes on resting at its own.
        if (order.Type == OrderType.Market || order.TimeInForce == TimeInForce.Ioc)
        {
            Close(order);
            Cancel(order);
            return;
        }

        if (order.Type == OrderType.MarketToLimit && !_orders.Contains(order))
        {
            _orders.Add(order);
        }
    }

    /// <summary>
    /// One fill of one order at one price. <c>offeredOverride</c> is what this fill may take when the caller has
    /// already worked it out - one level of a book it is walking; null leaves the fill to find its own bound from the
    /// touch.
    /// </summary>
    private void Fill(Order order, decimal price, LiquiditySide liquiditySide, decimal? offeredOverride = null, bool walking = false)
    {
        if (order.IsQuoteQuantity && !ConvertQuoteQuantity(order, price))
        {
            Close(order);
            return;
        }

        decimal wanted = order.LeavesQuantity.Value;
        if (wanted <= 0m)
        {
            Close(order);
            return;
        }

        // Reduce-only is checked when the order arrives and never again, so an order resting while something else
        // reduced the position would close what was left and open the other side with the rest. It closes at most what
        // is there, and whatever it cannot close is cancelled, as a venue does.
        bool cancelRemainder = false;
        if (order.IsReduceOnly && !_hedging)
        {
            decimal room = Math.Abs(_host.NetPosition(_instrument.Id));
            if (!WouldReduce(order) || room <= 0m)
            {
                Close(order);
                Cancel(order);
                return;
            }

            if (room < wanted)
            {
                wanted = room;
                cancelRemainder = true;
            }
        }

        // An iceberg shows only part of itself, and only what is shown can be taken before the rest becomes visible.
        Liquidity pool = offeredOverride is null ? PoolFor(order, liquiditySide) : Liquidity.None;
        decimal? offered = offeredOverride ?? Offered(pool);

        // A print at our price belongs to whoever was resting there before us. Only what is left of it once the queue
        // in front has been served reaches our order - which is the difference between a limit order at the front of
        // the book and the same limit order at the back of it.
        if (pool == Liquidity.Print && _queue.SizeAhead(order.ClientOrderId) is { } ahead && ahead > 0m)
        {
            offered = Math.Max(0m, (offered ?? 0m) - ahead);
        }

        if (liquiditySide == LiquiditySide.Maker && order.DisplayQuantity is { } shown && shown.Value > 0m)
        {
            offered = offered is { } available ? Math.Min(available, shown.Value) : shown.Value;
        }

        // Whether the bound is a rate of participation over a bar rather than a book: it decides what happens to what
        // the order could not take, because a bar that had no more to give is not a book that has run out.
        bool participating = _barBudget is not null && pool is Liquidity.Bid or Liquidity.Ask;
        Quantity fill = _instrument.MakeQuantity(offered is { } limit ? Math.Min(wanted, limit) : wanted);
        // Fill or kill is decided before a walk begins, over everything the order can reach; one level of it is not
        // the question.
        if (!walking && order.TimeInForce == TimeInForce.Fok && fill.Value < order.LeavesQuantity.Value)
        {
            // All of it at once or none of it: a fill-or-kill order that cannot be filled whole is not filled at all.
            Close(order);
            Cancel(order);
            return;
        }

        if (fill.IsZero)
        {
            // Nothing to be had here. An immediate-or-cancel order does not wait, and neither does a market order
            // against a book that has run out. A market order working at a participation rate does wait: the bar it
            // arrived on had nothing left of its share, or it arrived between bars, and the next bar is its turn.
            if (!walking && (order.TimeInForce == TimeInForce.Ioc || (order.Type == OrderType.Market && !participating)))
            {
                Close(order);
                Cancel(order);
            }
            else if (order.Type is OrderType.MarketToLimit or OrderType.Market && !_orders.Contains(order))
            {
                _orders.Add(order);
            }

            return;
        }

        // What becomes of what is left. A market order bounded by a book it has run out of never rests anywhere: it
        // takes what was on offer and gives up the rest, as a venue does. One bounded by a share of a bar's volume is
        // a different thing - the bound is a rate of participation over time, not a book - so it goes on working and
        // takes its share of the next bar, until it is filled or something cancels it. A market-to-limit order stays
        // either way: the remainder becomes a limit order at the price it got, which is the point of the type.
        bool whole = fill.Value >= order.LeavesQuantity.Value;
        bool givesUp = !walking
            && ((order.Type == OrderType.Market && !participating)
                || order.TimeInForce == TimeInForce.Ioc
                || (cancelRemainder && fill.Value >= wanted));
        bool done = whole || givesUp;
        if (done)
        {
            // The venue is finished with it either way: cleanup before the fill, as it has always been, so the fill
            // event is the last thing anyone sees of the order.
            Close(order);
        }
        else if (order.Type is OrderType.MarketToLimit or OrderType.Market && !_orders.Contains(order))
        {
            // A market order working at a participation rate has to be in the book to take its share of the next bar.
            _orders.Add(order);
        }

        if (!whole)
        {
            BoundedFills++;
            BoundedQuantity += fill.Value;
        }

        Take(pool, fill.Value);
        _host.OnFilled(order, fill, _instrument.MakePrice(price), liquiditySide, _now);
        if (done && !whole && !order.IsClosed)
        {
            // Part of it went in and the rest never will: what a reduce-only order could not close, what an
            // immediate-or-cancel order could not take at once, what the book did not have for a market order.
            Cancel(order);
        }
    }

    /// <summary>The venue has no more to do with this order: it leaves the book and its working state goes with it.</summary>
    private void Close(Order order)
    {
        _orders.Remove(order);
        Cleanup(order);
    }

    private void Cancel(Order order) => _host.OnCanceled(order, _now);

    /// <summary>
    /// A quote-quantity order names an amount of quote currency, so what it buys is only known at the price it fills at.
    /// The amount is converted to a base quantity there, rounded down to the size step so the order never spends more
    /// than it named, and the order is told its new size the way a venue tells it. False when the amount cannot buy one
    /// step, and the order is rejected rather than filled for nothing.
    /// </summary>
    private bool ConvertQuoteQuantity(Order order, decimal price)
    {
        if (price <= 0m)
        {
            _host.OnRejected(order, "quote quantity cannot be converted without a price", _now);
            return false;
        }

        Quantity converted = _instrument.MakeQuantity(order.LeavesQuantity.Value / price);
        if (converted.IsZero)
        {
            _host.OnRejected(order, $"quote quantity {order.LeavesQuantity} buys less than the size step {_instrument.SizeIncrement} at {price}", _now);
            return false;
        }

        _host.OnUpdated(order, converted, order.Price, order.TriggerPrice, _now);
        return true;
    }

    private void Trigger(Order order)
    {
        _triggered.Add(order.ClientOrderId);
        _host.OnTriggered(order, _now);
    }

    private void Cleanup(Order order)
    {
        _triggered.Remove(order.ClientOrderId);
        _trailingTriggers.Remove(order.ClientOrderId);
        _pendingActivation.Remove(order.ClientOrderId);
    }

    // ----- Trigger logic -----

    private decimal CurrentTrigger(Order order)
    {
        if (_trailingTriggers.TryGetValue(order.ClientOrderId, out decimal trailing))
        {
            return trailing;
        }

        return order.TriggerPrice?.Value ?? 0m;
    }

    private decimal TriggerReference(OrderSide side, TriggerType triggerType)
    {
        decimal? reference = triggerType switch
        {
            TriggerType.LastPrice => _last ?? (side == OrderSide.Buy ? _ask : _bid),
            _ => side == OrderSide.Buy ? _ask ?? _last : _bid ?? _last,
        };
        return reference ?? 0m;
    }

    /// <summary>A buy stop triggers when the market rises to the trigger; a sell stop when it falls to it.</summary>
    private bool IsStopTriggered(OrderSide side, decimal trigger, TriggerType triggerType)
    {
        if (_bid is null && _ask is null && _last is null)
        {
            return false;
        }

        decimal reference = TriggerReference(side, triggerType);
        return side == OrderSide.Buy ? reference >= trigger : reference <= trigger;
    }

    /// <summary>A buy MIT/LIT triggers when the market falls to the trigger; a sell when it rises to it.</summary>
    private bool IsTouched(OrderSide side, decimal trigger, TriggerType triggerType)
    {
        if (_bid is null && _ask is null && _last is null)
        {
            return false;
        }

        decimal reference = TriggerReference(side, triggerType);
        return side == OrderSide.Buy ? reference <= trigger : reference >= trigger;
    }

    private bool Crosses(OrderSide side, decimal price)
    {
        decimal? opposite = side == OrderSide.Buy ? _ask : _bid;
        if (opposite is null)
        {
            return false;
        }

        return side == OrderSide.Buy ? price >= opposite.Value : price <= opposite.Value;
    }

    private bool WouldReduce(Order order)
    {
        decimal net = _host.NetPosition(_instrument.Id);
        if (net == 0m)
        {
            return false;
        }

        return (net > 0m && order.IsSell) || (net < 0m && order.IsBuy);
    }

    // ----- Trailing stops -----

    private void InitializeTrailing(Order order)
    {
        if (ActivationPriceOf(order) is { } activation && !_pendingActivation.ContainsKey(order.ClientOrderId) && !IsTouched(order.Side, activation, order.TriggerType))
        {
            _pendingActivation[order.ClientOrderId] = activation;
            return;
        }

        decimal offset = TrailingOffsetValue(order);
        decimal? reference = order.IsBuy ? _ask ?? _last : _bid ?? _last;
        decimal trigger = order.TriggerPrice?.Value ?? (reference is { } r ? (order.IsBuy ? r + offset : r - offset) : 0m);
        _trailingTriggers[order.ClientOrderId] = trigger;
        if (order.TriggerPrice is null)
        {
            Price? limitPrice = null;
            if (order is TrailingStopLimitOrder tsl)
            {
                decimal limitOffset = tsl.TrailingOffsetType == TrailingOffsetType.BasisPoints ? trigger * tsl.LimitOffset / Scales.BasisPoints : tsl.LimitOffset;
                limitPrice = _instrument.MakePrice(order.IsBuy ? trigger + limitOffset : trigger - limitOffset);
            }

            _host.OnUpdated(order, order.Quantity, limitPrice ?? order.Price, _instrument.MakePrice(trigger), _now);
        }
    }

    private static DateOnly Day(UnixNanos ts) => DateOnly.FromDateTime(ts.ToDateTimeUtc());

    /// <summary>
    /// True while a trailing order waits for its activation price: it neither trails nor triggers before the market
    /// reaches that price, and when it does, the trail starts from there rather than from where the order was placed.
    /// </summary>
    private bool AwaitingActivation(Order order)
    {
        if (!_pendingActivation.TryGetValue(order.ClientOrderId, out decimal activation))
        {
            return false;
        }

        if (!IsTouched(order.Side, activation, order.TriggerType))
        {
            return true;
        }

        _pendingActivation.Remove(order.ClientOrderId);
        InitializeTrailing(order);
        return false;
    }

    private static decimal? ActivationPriceOf(Order order) => order switch
    {
        TrailingStopMarketOrder m => m.ActivationPrice?.Value,
        TrailingStopLimitOrder l => l.ActivationPrice?.Value,
        _ => null,
    };

    private void UpdateTrailing(Order order)
    {
        if (!_trailingTriggers.TryGetValue(order.ClientOrderId, out decimal trigger) || _triggered.Contains(order.ClientOrderId))
        {
            return;
        }

        decimal? reference = order.IsBuy ? _ask ?? _last : _bid ?? _last;
        if (reference is null)
        {
            return;
        }

        decimal offset = TrailingOffsetValue(order, reference.Value);
        decimal candidate = order.IsBuy ? reference.Value + offset : reference.Value - offset;
        bool improved = order.IsBuy ? candidate < trigger : candidate > trigger;
        if (!improved)
        {
            return;
        }

        _trailingTriggers[order.ClientOrderId] = candidate;
        Price newTrigger = _instrument.MakePrice(candidate);
        Price? newPrice = null;
        if (order is TrailingStopLimitOrder tsl)
        {
            decimal limitOffset = tsl.TrailingOffsetType == TrailingOffsetType.BasisPoints ? candidate * tsl.LimitOffset / Scales.BasisPoints : tsl.LimitOffset;
            newPrice = _instrument.MakePrice(order.IsBuy ? candidate + limitOffset : candidate - limitOffset);
        }

        _host.OnUpdated(order, order.Quantity, newPrice ?? order.Price, newTrigger, _now);
    }

    private decimal TrailingOffsetValue(Order order, decimal? reference = null)
    {
        (decimal offset, TrailingOffsetType type) = order switch
        {
            TrailingStopMarketOrder m => (m.TrailingOffset, m.TrailingOffsetType),
            TrailingStopLimitOrder l => (l.TrailingOffset, l.TrailingOffsetType),
            _ => (0m, TrailingOffsetType.Price),
        };

        decimal basis = reference ?? (order.IsBuy ? _ask ?? _last ?? 0m : _bid ?? _last ?? 0m);
        return type switch
        {
            TrailingOffsetType.BasisPoints => basis * offset / Scales.BasisPoints,
            TrailingOffsetType.Ticks => offset * Tick,
            _ => offset,
        };
    }
}
