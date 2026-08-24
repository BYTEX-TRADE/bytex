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
    private readonly List<Order> _orders = new();
    private readonly Dictionary<ClientOrderId, VenueOrderId> _venueIds = new();
    private readonly HashSet<ClientOrderId> _triggered = new();
    private readonly Dictionary<ClientOrderId, decimal> _trailingTriggers = new();
    private decimal? _bid;
    private decimal? _ask;
    private decimal? _last;
    private UnixNanos _now;

    public OrderMatchingEngine(Instrument instrument, IMatchingEngineHost host, FillModel fillModel, BarExecutionMode barMode, bool rejectStopOrdersAtMarket = true)
    {
        _instrument = instrument;
        _host = host;
        _fillModel = fillModel;
        _barMode = barMode;
        _rejectStopOrdersAtMarket = rejectStopOrdersAtMarket;
    }

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
        Iterate();
    }

    public void ProcessTrade(TradeTick trade)
    {
        _now = trade.TsEvent;
        _last = trade.Price.Value;
        if (_bid is null || _ask is null)
        {
            _bid = _last;
            _ask = _last;
        }
        else
        {
            // Keep the quote consistent with the last trade when it falls outside the spread.
            if (_last < _bid)
            {
                _bid = _last;
            }

            if (_last > _ask)
            {
                _ask = _last;
            }
        }

        Iterate();
    }

    public void ProcessBar(Bar bar)
    {
        _now = bar.TsEvent;
        if (_barMode == BarExecutionMode.CloseOnly)
        {
            SetPrice(bar.Close.Value);
            Iterate();
            return;
        }

        bool bullish = bar.Close >= bar.Open;
        decimal[] path = bullish
            ? [bar.Open.Value, bar.Low.Value, bar.High.Value, bar.Close.Value]
            : [bar.Open.Value, bar.High.Value, bar.Low.Value, bar.Close.Value];

        for (int i = 0; i < path.Length; i++)
        {
            SetPrice(path[i]);
            Iterate(isBarOpen: i == 0, isBarClose: i == path.Length - 1);
        }
    }

    public void ProcessBook(OrderBook book, UnixNanos ts)
    {
        _now = ts;
        _bid = book.BestBidPrice?.Value;
        _ask = book.BestAskPrice?.Value;
        Iterate();
    }

    private void SetPrice(decimal price)
    {
        _bid = price;
        _ask = price;
        _last = price;
    }

    // ----- Commands -----

    public void ProcessOrder(Order order, UnixNanos ts)
    {
        _now = ts;
        if (_venueIds.ContainsKey(order.ClientOrderId))
        {
            _host.OnRejected(order, "duplicate client order id", ts);
            return;
        }

        if (order.IsReduceOnly && !WouldReduce(order))
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

        VenueOrderId venueOrderId = _host.NextVenueOrderId();
        _venueIds[order.ClientOrderId] = venueOrderId;

        if (order.Type == OrderType.Market || order.Type == OrderType.MarketToLimit)
        {
            if (order.TimeInForce is TimeInForce.AtTheOpen or TimeInForce.AtTheClose)
            {
                _host.OnAccepted(order, venueOrderId, ts);
                _orders.Add(order);
                return;
            }

            _host.OnAccepted(order, venueOrderId, ts);
            FillMarket(order, LiquiditySide.Taker);
            return;
        }

        _host.OnAccepted(order, venueOrderId, ts);
        _orders.Add(order);
        if (order.Type is OrderType.TrailingStopMarket or OrderType.TrailingStopLimit)
        {
            InitializeTrailing(order);
        }

        MatchOrder(order, aggressive: true);
    }

    public void ProcessModify(Order order, Quantity? quantity, Price? price, Price? triggerPrice, UnixNanos ts)
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
        _host.OnCanceled(order, ts);
    }

    public void ProcessCancelAll(OrderSide? side, StrategyId? strategyId, UnixNanos ts)
    {
        _now = ts;
        foreach (Order order in _orders.Where(o => (side is null || o.Side == side) && (strategyId is null || o.StrategyId == strategyId)).ToList())
        {
            _orders.Remove(order);
            Cleanup(order);
            _host.OnCanceled(order, ts);
        }
    }

    public VenueOrderId? VenueOrderIdFor(ClientOrderId id) => _venueIds.TryGetValue(id, out VenueOrderId v) ? v : null;

    // ----- Matching -----

    private void Iterate(bool isBarOpen = false, bool isBarClose = false)
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

            if (order.TimeInForce == TimeInForce.AtTheOpen && isBarOpen || order.TimeInForce == TimeInForce.AtTheClose && isBarClose)
            {
                _orders.Remove(order);
                FillMarket(order, LiquiditySide.Taker);
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

            case OrderType.MarketIfTouched:
                if (!triggered && IsTouched(order.Side, order.TriggerPrice!.Value.Value, order.TriggerType))
                {
                    Trigger(order);
                    _orders.Remove(order);
                    FillMarket(order, LiquiditySide.Taker);
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
            // Crossing on arrival fills as a taker at the opposite price.
            Fill(order, opposite.Value, LiquiditySide.Taker);
            return;
        }

        if (!aggressive && (through || (touch && _fillModel.IsLimitFilled())))
        {
            Fill(order, limitPrice, LiquiditySide.Maker);
            return;
        }

        if (aggressive && order.TimeInForce is TimeInForce.Ioc or TimeInForce.Fok)
        {
            _orders.Remove(order);
            Cleanup(order);
            _host.OnCanceled(order, _now);
        }
    }

    private void FillMarket(Order order, LiquiditySide side)
    {
        decimal? opposite = order.IsBuy ? _ask : _bid;
        if (opposite is null)
        {
            _orders.Remove(order);
            _host.OnRejected(order, "no market price available", _now);
            return;
        }

        decimal price = opposite.Value;
        if (_fillModel.IsSlipped())
        {
            price += order.IsBuy ? Tick : -Tick;
        }

        if (order.Type == OrderType.MarketToLimit)
        {
            _host.OnUpdated(order, order.Quantity, _instrument.MakePrice(price), null, _now);
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

        decimal price = opposite.Value;
        if (!_fillModel.IsStopFilled())
        {
            price += order.IsBuy ? Tick : -Tick;
        }

        Fill(order, price, LiquiditySide.Taker);
    }

    private void Fill(Order order, decimal price, LiquiditySide liquiditySide)
    {
        _orders.Remove(order);
        Cleanup(order);
        Quantity qty = order.LeavesQuantity;
        if (qty.IsZero)
        {
            return;
        }

        _host.OnFilled(order, qty, _instrument.MakePrice(price), liquiditySide, _now);
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
        decimal offset = TrailingOffsetValue(order);
        decimal? reference = order.IsBuy ? _ask ?? _last : _bid ?? _last;
        decimal trigger = order.TriggerPrice?.Value ?? (reference is { } r ? (order.IsBuy ? r + offset : r - offset) : 0m);
        _trailingTriggers[order.ClientOrderId] = trigger;
        if (order.TriggerPrice is null)
        {
            Price? limitPrice = null;
            if (order is TrailingStopLimitOrder tsl)
            {
                decimal limitOffset = tsl.TrailingOffsetType == TrailingOffsetType.BasisPoints ? trigger * tsl.LimitOffset / 10_000m : tsl.LimitOffset;
                limitPrice = _instrument.MakePrice(order.IsBuy ? trigger + limitOffset : trigger - limitOffset);
            }

            _host.OnUpdated(order, order.Quantity, limitPrice ?? order.Price, _instrument.MakePrice(trigger), _now);
        }
    }

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
            decimal limitOffset = tsl.TrailingOffsetType == TrailingOffsetType.BasisPoints ? candidate * tsl.LimitOffset / 10_000m : tsl.LimitOffset;
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
            TrailingOffsetType.BasisPoints => basis * offset / 10_000m,
            TrailingOffsetType.Ticks => offset * Tick,
            _ => offset,
        };
    }
}
