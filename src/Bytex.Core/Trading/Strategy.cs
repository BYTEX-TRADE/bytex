using Bytex.Core.Caching;
using Bytex.Core.Messaging;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Positions;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Portfolios;
using Bytex.Core.Timing;
using Microsoft.Extensions.Logging;

namespace Bytex.Core.Trading;

public record StrategyConfig : ActorConfig
{
    public StrategyId? StrategyId { get; init; }

    /// <summary>Tag segment used in generated order identifiers; defaults to the strategy id tag.</summary>
    public string? OrderIdTag { get; init; }

    public OmsType OmsType { get; init; } = OmsType.Unspecified;

    /// <summary>Instruments whose external (venue-originated) orders this strategy owns.</summary>
    public IReadOnlyList<InstrumentId> ExternalOrderClaims { get; init; } = [];

    /// <summary>Cancel linked OCO/OUO orders locally when one fills, for venues without native support.</summary>
    public bool ManageContingentOrders { get; init; }

    /// <summary>Expire GTD orders locally using clock timers.</summary>
    public bool ManageGtdExpiry { get; init; }

    public bool UseHyphensInClientOrderIds { get; init; } = true;
}

/// <summary>
/// An actor that can trade: submits and manages orders and reacts to execution and position events.
/// </summary>
public abstract class Strategy : Actor
{
    private readonly Dictionary<ClientOrderId, string> _gtdTimers = new();
    private OrderFactory? _orderFactory;

    protected Strategy(StrategyConfig? config = null)
        : base(config ?? new StrategyConfig())
    {
        Config = (StrategyConfig)base.Config;
        StrategyId = Config.StrategyId ?? new StrategyId(DefaultId(GetType(), null));
        ActorId = new ActorId(StrategyId.Value);
        Id = new Model.Identifiers.ComponentId(StrategyId.Value);
        OmsType = Config.OmsType;
    }

    public new StrategyConfig Config { get; }

    public StrategyId StrategyId { get; }

    public OmsType OmsType { get; }

    public IReadOnlyList<InstrumentId> ExternalOrderClaims => Config.ExternalOrderClaims;

    protected OrderFactory OrderFactory => _orderFactory ?? throw new InvalidOperationException($"Strategy {StrategyId} has not been registered with a trader.");

    public override void Register(TraderId traderId, IClock clock, ICache cache, IMessageBus bus, IPortfolio portfolio, ILoggerFactory loggerFactory, Action<Action>? post = null)
    {
        base.Register(traderId, clock, cache, bus, portfolio, loggerFactory, post);
        // The tag shapes the client order ids the venue sees; the orders stay this strategy's, so its own events reach it.
        StrategyId idForOrders = Config.OrderIdTag is { } tag ? new StrategyId($"{StrategyId.Value.Split('-')[0]}-{tag}") : StrategyId;
        _orderFactory = new OrderFactory(traderId, StrategyId, clock, useHyphens: Config.UseHyphensInClientOrderIds, idOwner: idForOrders);

        // Seed identifier counters from cached orders so ids never repeat after a restart.
        int count = cache.Orders(strategyId: StrategyId).Count;
        _orderFactory.SetOrderIdCount(count);
        _orderFactory.SetListIdCount(cache.OrderLists(strategyId: StrategyId).Count);

        bus.Subscribe(Topics.OrderEvents(StrategyId), m => HandleOrderEvent((OrderEvent)m));
        bus.Subscribe(Topics.PositionEvents(StrategyId), m => HandlePositionEvent((PositionEvent)m));
    }

    protected override void OnReset()
    {
        base.OnReset();
        ResetIdSequence();
    }

    /// <summary>
    /// Starts the client order id sequence over, so a re-run of the same backtest numbers its orders exactly as the first
    /// run did. The trader calls this as well as the strategy's own reset, because a strategy that overrides OnReset and
    /// forgets to call the base would otherwise lose the determinism the engine promises.
    /// </summary>
    internal void ResetIdSequence() => _orderFactory?.Reset();

    // ----- Event handlers -----

    protected virtual void OnOrderInitialized(OrderInitialized e)
    {
    }

    protected virtual void OnOrderDenied(OrderDenied e)
    {
    }

    protected virtual void OnOrderEmulated(OrderEmulated e)
    {
    }

    protected virtual void OnOrderReleased(OrderReleased e)
    {
    }

    protected virtual void OnOrderSubmitted(OrderSubmitted e)
    {
    }

    protected virtual void OnOrderRejected(OrderRejected e)
    {
    }

    protected virtual void OnOrderAccepted(OrderAccepted e)
    {
    }

    protected virtual void OnOrderCanceled(OrderCanceled e)
    {
    }

    protected virtual void OnOrderExpired(OrderExpired e)
    {
    }

    protected virtual void OnOrderTriggered(OrderTriggered e)
    {
    }

    protected virtual void OnOrderPendingUpdate(OrderPendingUpdate e)
    {
    }

    protected virtual void OnOrderPendingCancel(OrderPendingCancel e)
    {
    }

    protected virtual void OnOrderModifyRejected(OrderModifyRejected e)
    {
    }

    protected virtual void OnOrderCancelRejected(OrderCancelRejected e)
    {
    }

    protected virtual void OnOrderUpdated(OrderUpdated e)
    {
    }

    protected virtual void OnOrderFilled(OrderFilled e)
    {
    }

    protected virtual void OnOrderEvent(OrderEvent e)
    {
    }

    protected virtual void OnPositionOpened(PositionOpened e)
    {
    }

    protected virtual void OnPositionChanged(PositionChanged e)
    {
    }

    protected virtual void OnPositionClosed(PositionClosed e)
    {
    }

    protected virtual void OnPositionEvent(PositionEvent e)
    {
    }

    public void HandleOrderEvent(OrderEvent e)
    {
        if (Config.LogEvents)
        {
            Log.LogDebug("{Strategy} received {Event}", StrategyId, e.GetType().Name);
        }

        if (e is OrderCanceled or OrderExpired or OrderRejected or OrderDenied
            || (e is OrderFilled && Cache.Order(e.ClientOrderId) is not { IsOpen: true }))
        {
            CancelGtdTimer(e.ClientOrderId);
        }

        if (Config.ManageContingentOrders && e is OrderFilled or OrderCanceled or OrderExpired)
        {
            ManageContingencies(e);
        }

        Guarded(() =>
        {
            switch (e)
            {
                case OrderInitialized x:
                    OnOrderInitialized(x);
                    break;
                case OrderDenied x:
                    OnOrderDenied(x);
                    break;
                case OrderEmulated x:
                    OnOrderEmulated(x);
                    break;
                case OrderReleased x:
                    OnOrderReleased(x);
                    break;
                case OrderSubmitted x:
                    OnOrderSubmitted(x);
                    break;
                case OrderRejected x:
                    OnOrderRejected(x);
                    break;
                case OrderAccepted x:
                    OnOrderAccepted(x);
                    break;
                case OrderCanceled x:
                    OnOrderCanceled(x);
                    break;
                case OrderExpired x:
                    OnOrderExpired(x);
                    break;
                case OrderTriggered x:
                    OnOrderTriggered(x);
                    break;
                case OrderPendingUpdate x:
                    OnOrderPendingUpdate(x);
                    break;
                case OrderPendingCancel x:
                    OnOrderPendingCancel(x);
                    break;
                case OrderModifyRejected x:
                    OnOrderModifyRejected(x);
                    break;
                case OrderCancelRejected x:
                    OnOrderCancelRejected(x);
                    break;
                case OrderUpdated x:
                    OnOrderUpdated(x);
                    break;
                case OrderFilled x:
                    OnOrderFilled(x);
                    break;
            }

            OnOrderEvent(e);
            OnEvent(e);
        });
    }

    public void HandlePositionEvent(PositionEvent e) => Guarded(() =>
    {
        switch (e)
        {
            case PositionOpened x:
                OnPositionOpened(x);
                break;
            case PositionChanged x:
                OnPositionChanged(x);
                break;
            case PositionClosed x:
                OnPositionClosed(x);
                break;
        }

        OnPositionEvent(e);
        OnEvent(e);
    });

    // ----- Trading commands -----

    private void SendTradingCommand(TradingCommand command)
    {
        if (Config.LogCommands)
        {
            Log.LogDebug("{Strategy} sending {Command}", StrategyId, command.GetType().Name);
        }

        MessageBus.Send(Endpoints.RiskEngineExecute, command);
    }

    protected void SubmitOrder(Order order, PositionId? positionId = null, ClientId? clientId = null)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (order.StrategyId != StrategyId && order.StrategyId.Value != OrderFactory.StrategyId.Value)
        {
            throw new ArgumentException($"Order {order.ClientOrderId} belongs to {order.StrategyId}, not {StrategyId}.", nameof(order));
        }

        if (!Cache.OrderExists(order.ClientOrderId))
        {
            ((Cache)Cache).AddOrder(order, positionId);
        }

        HandleOrderEvent(order.InitEvent);
        ScheduleGtdExpiry(order);
        SendTradingCommand(new SubmitOrder(TraderId, StrategyId, order, positionId, order.ExecAlgorithmId, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void SubmitOrderList(OrderList list, PositionId? positionId = null, ClientId? clientId = null)
    {
        ArgumentNullException.ThrowIfNull(list);
        foreach (Order order in list.Orders)
        {
            if (!Cache.OrderExists(order.ClientOrderId))
            {
                ((Cache)Cache).AddOrder(order, positionId);
            }

            HandleOrderEvent(order.InitEvent);
            ScheduleGtdExpiry(order);
        }

        ((Cache)Cache).AddOrderList(list);
        SendTradingCommand(new SubmitOrderList(TraderId, StrategyId, list, positionId, list.First.ExecAlgorithmId, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void ModifyOrder(Order order, Quantity? quantity = null, Price? price = null, Price? triggerPrice = null, ClientId? clientId = null)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (quantity is null && price is null && triggerPrice is null)
        {
            Log.LogWarning("ModifyOrder for {ClientOrderId} ignored: nothing to change", order.ClientOrderId);
            return;
        }

        if (order.IsClosed || order.IsPending)
        {
            Log.LogWarning("Cannot modify {ClientOrderId}: state {Status}", order.ClientOrderId, order.Status);
            return;
        }

        SendTradingCommand(new ModifyOrder(TraderId, StrategyId, order.InstrumentId, order.ClientOrderId, order.VenueOrderId, quantity, price, triggerPrice, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void CancelOrder(Order order, ClientId? clientId = null)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (order.IsClosed || order.Status == OrderStatus.PendingCancel)
        {
            Log.LogWarning("Cannot cancel {ClientOrderId}: state {Status}", order.ClientOrderId, order.Status);
            return;
        }

        SendTradingCommand(new CancelOrder(TraderId, StrategyId, order.InstrumentId, order.ClientOrderId, order.VenueOrderId, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void CancelOrders(IReadOnlyList<Order> orders, ClientId? clientId = null)
    {
        ArgumentNullException.ThrowIfNull(orders);
        if (orders.Count == 0)
        {
            return;
        }

        List<CancelOrder> cancels = new();
        foreach (Order order in orders)
        {
            if (order.IsClosed || order.Status == OrderStatus.PendingCancel)
            {
                continue;
            }

            cancels.Add(new CancelOrder(TraderId, StrategyId, order.InstrumentId, order.ClientOrderId, order.VenueOrderId, clientId, Guid.NewGuid(), Clock.Timestamp));
        }

        if (cancels.Count == 0)
        {
            return;
        }

        SendTradingCommand(new BatchCancelOrders(TraderId, StrategyId, cancels[0].InstrumentId, cancels, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void CancelAllOrders(InstrumentId instrumentId, OrderSide? side = null, ClientId? clientId = null)
    {
        IReadOnlyList<Order> open = Cache.OrdersOpen(instrumentId: instrumentId, strategyId: StrategyId, side: side);
        IReadOnlyList<Order> local = Cache.Orders(instrumentId: instrumentId, strategyId: StrategyId, side: side).Where(o => o.IsActiveLocal || o.IsInflight).ToList();
        if (open.Count == 0 && local.Count == 0)
        {
            return;
        }

        SendTradingCommand(new CancelAllOrders(TraderId, StrategyId, instrumentId, side, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void CancelAllOrdersAllInstruments(ClientId? clientId = null)
    {
        foreach (InstrumentId instrumentId in Cache.Orders(strategyId: StrategyId).Where(o => !o.IsClosed).Select(o => o.InstrumentId).Distinct().ToList())
        {
            CancelAllOrders(instrumentId, null, clientId);
        }
    }

    protected void ClosePosition(Position position, ClientId? clientId = null, IReadOnlyList<string>? tags = null, bool reduceOnly = true, TimeInForce timeInForce = TimeInForce.Gtc)
    {
        ArgumentNullException.ThrowIfNull(position);
        if (position.IsClosed)
        {
            Log.LogWarning("Cannot close {PositionId}: already closed", position.Id);
            return;
        }

        MarketOrder order = OrderFactory.Market(position.InstrumentId, position.Side.ClosingSide(), position.Quantity, timeInForce, reduceOnly, tags: tags ?? ["CLOSE"]);
        SubmitOrder(order, position.Id, clientId);
    }

    protected void CloseAllPositions(InstrumentId instrumentId, PositionSide? side = null, ClientId? clientId = null, IReadOnlyList<string>? tags = null, bool reduceOnly = true)
    {
        foreach (Position position in Cache.PositionsOpen(instrumentId: instrumentId, strategyId: StrategyId, side: side))
        {
            ClosePosition(position, clientId, tags, reduceOnly);
        }
    }

    protected void CloseAllPositionsAllInstruments(ClientId? clientId = null, bool reduceOnly = true)
    {
        foreach (Position position in Cache.PositionsOpen(strategyId: StrategyId))
        {
            ClosePosition(position, clientId, null, reduceOnly);
        }
    }

    protected void QueryOrder(Order order, ClientId? clientId = null)
    {
        ArgumentNullException.ThrowIfNull(order);
        SendTradingCommand(new QueryOrder(TraderId, StrategyId, order.InstrumentId, order.ClientOrderId, order.VenueOrderId, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    // ----- GTD expiry management -----

    private void ScheduleGtdExpiry(Order order)
    {
        if (!Config.ManageGtdExpiry || order.TimeInForce != TimeInForce.Gtd || order.ExpireTime is not { } expireTime)
        {
            return;
        }

        string name = $"gtd:{order.ClientOrderId}";
        _gtdTimers[order.ClientOrderId] = name;
        SetTimeAlert(name, expireTime, _ =>
        {
            _gtdTimers.Remove(order.ClientOrderId);
            Order? current = Cache.Order(order.ClientOrderId);
            if (current is { IsClosed: false })
            {
                CancelOrder(current);
            }
        });
    }

    private void CancelGtdTimer(ClientOrderId clientOrderId)
    {
        if (_gtdTimers.Remove(clientOrderId, out string? name))
        {
            CancelTimer(name);
        }
    }

    protected void CancelGtdExpiry(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);
        CancelGtdTimer(order.ClientOrderId);
    }

    // ----- Contingency management -----

    private void ManageContingencies(OrderEvent e)
    {
        Order? order = Cache.Order(e.ClientOrderId);
        if (order is null || order.Contingency == ContingencyType.None)
        {
            return;
        }

        switch (order.Contingency)
        {
            case ContingencyType.Oco:
                if (e is OrderFilled || order.IsClosed)
                {
                    foreach (ClientOrderId linkedId in order.LinkedOrderIds)
                    {
                        Order? linked = Cache.Order(linkedId);
                        if (linked is { IsClosed: false })
                        {
                            CancelOrder(linked);
                        }
                    }
                }

                break;

            case ContingencyType.Ouo:
                if (e is OrderFilled fill)
                {
                    foreach (ClientOrderId linkedId in order.LinkedOrderIds)
                    {
                        Order? linked = Cache.Order(linkedId);
                        if (linked is null || linked.IsClosed)
                        {
                            continue;
                        }

                        if (order.IsClosed)
                        {
                            CancelOrder(linked);
                        }
                        else if (linked.LeavesQuantity != order.LeavesQuantity)
                        {
                            ModifyOrder(linked, order.LeavesQuantity);
                        }
                    }
                }
                else if (order.IsClosed)
                {
                    foreach (ClientOrderId linkedId in order.LinkedOrderIds)
                    {
                        Order? linked = Cache.Order(linkedId);
                        if (linked is { IsClosed: false })
                        {
                            CancelOrder(linked);
                        }
                    }
                }

                break;

            case ContingencyType.Oto:
                if (e is OrderCanceled or OrderExpired)
                {
                    foreach (ClientOrderId linkedId in order.LinkedOrderIds)
                    {
                        Order? linked = Cache.Order(linkedId);
                        if (linked is { IsClosed: false })
                        {
                            CancelOrder(linked);
                        }
                    }
                }

                break;
        }
    }

    public override string ToString() => $"{GetType().Name}({StrategyId}, {State})";
}

/// <summary>
/// A strategy with a typed configuration.
/// </summary>
public abstract class Strategy<TConfig> : Strategy where TConfig : StrategyConfig
{
    protected Strategy(TConfig config)
        : base(config)
    {
        Config = config;
    }

    public new TConfig Config { get; }
}
