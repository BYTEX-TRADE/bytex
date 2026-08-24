using System.Globalization;
using Bytex.Core.Adapters;
using Bytex.Core.Caching;
using Bytex.Core.Common;
using Bytex.Core.Messaging;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Positions;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Model.Reports;
using Microsoft.Extensions.Logging;

namespace Bytex.Core.Engines;

public sealed record ExecutionEngineConfig
{
    /// <summary>Allow orders for instruments not in the cache (they are rejected otherwise).</summary>
    public bool AllowUnknownInstruments { get; init; }

    /// <summary>Generate position events for fills of orders claimed as external.</summary>
    public bool ManageExternalOrders { get; init; } = true;

    public bool LogEvents { get; init; } = true;

    /// <summary>How far back reconciliation looks for fills when no cached state exists.</summary>
    public TimeSpan ReconciliationLookback { get; init; } = TimeSpan.FromDays(1);
}

/// <summary>
/// Routes trading commands to execution clients, applies execution events to orders and positions,
/// and reconciles cached state with venue reports.
/// </summary>
public sealed class ExecutionEngine : Component, IExecutionClientSink
{
    private readonly IMessageBus _bus;
    private readonly Cache _cache;
    private readonly ExecutionEngineConfig _config;
    private readonly Dictionary<ClientId, IExecutionClient> _clients = new();
    private readonly Dictionary<Venue, IExecutionClient> _routing = new();
    private readonly Dictionary<StrategyId, OmsType> _omsTypes = new();
    private readonly Dictionary<InstrumentId, StrategyId> _externalClaims = new();
    private readonly Dictionary<string, int> _positionCounters = new(StringComparer.Ordinal);
    private readonly Dictionary<ExecAlgorithmId, Action<object>> _execAlgorithms = new();
    private IExecutionClient? _defaultClient;
    private int _positionSequence;

    public ExecutionEngine(IMessageBus bus, Cache cache, ExecutionEngineConfig? config = null)
        : base(new ComponentId("ExecutionEngine"))
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(cache);
        _bus = bus;
        _cache = cache;
        _config = config ?? new ExecutionEngineConfig();
        _bus.Register(Endpoints.ExecutionEngineExecute, Execute);
        _bus.Register(Endpoints.ExecutionEngineProcess, Process);
    }

    public ExecutionEngineConfig Config => _config;

    public IReadOnlyCollection<ClientId> RegisteredClients => _clients.Keys;

    public IReadOnlyCollection<IExecutionClient> Clients => _clients.Values;

    public long CommandCount { get; private set; }

    public long EventCount { get; private set; }

    public long PositionIdCount => _positionSequence;

    // ----- Registration -----

    public void RegisterClient(IExecutionClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (_clients.ContainsKey(client.ClientId))
        {
            throw new InvalidOperationException($"Execution client {client.ClientId} is already registered.");
        }

        client.AttachSink(this);
        _clients[client.ClientId] = client;
        _routing.TryAdd(client.Venue, client);
        Log.LogInformation("Registered execution client {ClientId} for {Venue}", client.ClientId, client.Venue);
    }

    public void RegisterDefaultClient(IExecutionClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (!_clients.ContainsKey(client.ClientId))
        {
            RegisterClient(client);
        }

        _defaultClient = client;
    }

    public void RegisterVenueRouting(Venue venue, IExecutionClient client) => _routing[venue] = client ?? throw new ArgumentNullException(nameof(client));

    public void DeregisterClient(IExecutionClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _clients.Remove(client.ClientId);
        foreach (Venue venue in _routing.Where(kv => ReferenceEquals(kv.Value, client)).Select(kv => kv.Key).ToList())
        {
            _routing.Remove(venue);
        }
    }

    public void RegisterOmsType(StrategyId strategyId, OmsType omsType) => _omsTypes[strategyId] = omsType;

    public void RegisterExternalOrderClaims(StrategyId strategyId, IEnumerable<InstrumentId> instrumentIds)
    {
        foreach (InstrumentId id in instrumentIds)
        {
            if (_externalClaims.TryGetValue(id, out StrategyId existing) && existing != strategyId)
            {
                throw new InvalidOperationException($"Instrument {id} is already claimed by {existing}.");
            }

            _externalClaims[id] = strategyId;
        }
    }

    public void RegisterExecAlgorithm(ExecAlgorithmId id, Action<object> handler) => _execAlgorithms[id] = handler ?? throw new ArgumentNullException(nameof(handler));

    public void DeregisterExecAlgorithm(ExecAlgorithmId id) => _execAlgorithms.Remove(id);

    public IExecutionClient? ClientFor(ClientId? clientId, Venue venue)
    {
        if (clientId is { } id && _clients.TryGetValue(id, out IExecutionClient? byId))
        {
            return byId;
        }

        if (_routing.TryGetValue(venue, out IExecutionClient? byVenue))
        {
            return byVenue;
        }

        if (_clients.TryGetValue(new ClientId(venue.Value), out IExecutionClient? byName))
        {
            return byName;
        }

        return _defaultClient;
    }

    protected override void OnStart()
    {
        foreach (IExecutionClient client in _clients.Values)
        {
            if (client.State is ComponentState.Ready or ComponentState.Stopped)
            {
                client.Start();
            }
        }
    }

    protected override void OnStop()
    {
        foreach (IExecutionClient client in _clients.Values)
        {
            if (client.State is ComponentState.Running or ComponentState.Degraded)
            {
                client.Stop();
            }
        }
    }

    protected override void OnReset()
    {
        _positionCounters.Clear();
        _positionSequence = 0;
        CommandCount = 0;
        EventCount = 0;
    }

    protected override void OnDispose()
    {
        foreach (IExecutionClient client in _clients.Values)
        {
            client.Dispose();
        }

        _clients.Clear();
        _routing.Clear();
    }

    // ----- Commands -----

    public void Execute(object message)
    {
        CommandCount++;
        if (message is not TradingCommand command)
        {
            Log.LogError("ExecutionEngine cannot handle {MessageType}", message.GetType().Name);
            return;
        }

        switch (command)
        {
            case SubmitOrder submit:
                HandleSubmitOrder(submit);
                break;
            case SubmitOrderList list:
                HandleSubmitOrderList(list);
                break;
            case ModifyOrder modify:
                Route(modify, (c, ct) => c.ModifyOrderAsync(modify, ct));
                break;
            case CancelOrder cancel:
                HandleCancelOrder(cancel);
                break;
            case CancelAllOrders cancelAll:
                HandleCancelAll(cancelAll);
                break;
            case BatchCancelOrders batch:
                Route(batch, (c, ct) => c.BatchCancelOrdersAsync(batch, ct));
                break;
            case QueryOrder query:
                Route(query, (c, ct) => c.QueryOrderAsync(query, ct));
                break;
        }
    }

    private void HandleSubmitOrder(SubmitOrder command)
    {
        Order order = command.Order;
        if (!_cache.OrderExists(order.ClientOrderId))
        {
            _cache.AddOrder(order, command.PositionId);
        }

        if (!_config.AllowUnknownInstruments && _cache.Instrument(order.InstrumentId) is null)
        {
            Deny(order, $"Instrument {order.InstrumentId} not found in cache");
            return;
        }

        if (command.ExecAlgorithmId is { } algId && order.ExecSpawnId is null)
        {
            if (_execAlgorithms.TryGetValue(algId, out Action<object>? handler))
            {
                handler(command);
                return;
            }

            Deny(order, $"Execution algorithm {algId} not registered");
            return;
        }

        Route(command, (c, ct) => c.SubmitOrderAsync(command, ct));
    }

    private void HandleSubmitOrderList(SubmitOrderList command)
    {
        foreach (Order order in command.OrderList.Orders)
        {
            if (!_cache.OrderExists(order.ClientOrderId))
            {
                _cache.AddOrder(order, command.PositionId);
            }
        }

        _cache.AddOrderList(command.OrderList);
        Route(command, (c, ct) => c.SubmitOrderListAsync(command, ct));
    }

    private void HandleCancelOrder(CancelOrder command)
    {
        Order? order = _cache.Order(command.ClientOrderId);
        if (order is null)
        {
            Log.LogError("Cannot cancel {ClientOrderId}: order not found", command.ClientOrderId);
            return;
        }

        if (order.IsClosed)
        {
            Log.LogWarning("Cannot cancel {ClientOrderId}: already {Status}", command.ClientOrderId, order.Status);
            return;
        }

        if (order.IsActiveLocal)
        {
            // Never reached a venue; cancel locally.
            Process(new OrderCanceled(order.TraderId, order.StrategyId, order.InstrumentId, order.ClientOrderId, order.VenueOrderId, order.AccountId, Guid.NewGuid(), Clock.Timestamp, Clock.Timestamp));
            return;
        }

        _cache.UpdateOrderPendingCancelLocal(order);
        Route(command, (c, ct) => c.CancelOrderAsync(command, ct));
    }

    private void HandleCancelAll(CancelAllOrders command)
    {
        foreach (Order order in _cache.Orders(instrumentId: command.InstrumentId, strategyId: command.StrategyId, side: command.OrderSide))
        {
            if (order.IsActiveLocal)
            {
                Process(new OrderCanceled(order.TraderId, order.StrategyId, order.InstrumentId, order.ClientOrderId, order.VenueOrderId, order.AccountId, Guid.NewGuid(), Clock.Timestamp, Clock.Timestamp));
            }
        }

        Route(command, (c, ct) => c.CancelAllOrdersAsync(command, ct));
    }

    private void Route(TradingCommand command, Func<IExecutionClient, CancellationToken, Task> action)
    {
        IExecutionClient? client = ClientFor(command.ClientId, command.InstrumentId.Venue);
        if (client is null)
        {
            Log.LogError("No execution client for {Venue} (client={ClientId}); cannot execute {Command}", command.InstrumentId.Venue, command.ClientId, command.GetType().Name);
            if (command is SubmitOrder submit)
            {
                Deny(submit.Order, $"No execution client for {command.InstrumentId.Venue}");
            }

            return;
        }

        try
        {
            Task task = action(client, CancellationToken.None);
            if (task.IsCompleted)
            {
                if (task.IsFaulted)
                {
                    Log.LogError(task.Exception, "Execution client {ClientId} failed {Command}", client.ClientId, command.GetType().Name);
                }

                return;
            }

            task.ContinueWith(t => Log.LogError(t.Exception, "Execution client {ClientId} failed {Command}", client.ClientId, command.GetType().Name), TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception e)
        {
            Log.LogError(e, "Execution client {ClientId} threw for {Command}", client.ClientId, command.GetType().Name);
        }
    }

    private void Deny(Order order, string reason) =>
        Process(new OrderDenied(order.TraderId, order.StrategyId, order.InstrumentId, order.ClientOrderId, reason, Guid.NewGuid(), Clock.Timestamp, Clock.Timestamp));

    // ----- Events -----

    public void Process(object message)
    {
        EventCount++;
        switch (message)
        {
            case OrderEvent orderEvent:
                HandleOrderEvent(orderEvent);
                break;
            case AccountState accountState:
                _bus.Send(Endpoints.PortfolioUpdateAccount, accountState);
                break;
            default:
                Log.LogError("ExecutionEngine cannot process {MessageType}", message.GetType().Name);
                break;
        }
    }

    private void HandleOrderEvent(OrderEvent e)
    {
        Order? order = _cache.Order(e.ClientOrderId);
        if (order is null && e.VenueOrderId is { } venueOrderId)
        {
            order = _cache.OrderForVenueId(venueOrderId);
        }

        if (order is null)
        {
            Log.LogWarning("{EventType} for unknown order {ClientOrderId} (venue id {VenueOrderId})", e.GetType().Name, e.ClientOrderId, e.VenueOrderId);
            return;
        }

        if (e is OrderFilled fill)
        {
            HandleFill(order, fill);
            return;
        }

        ApplyEvent(order, e);
        _cache.UpdateOrder(order);
        Publish(order, e);
    }

    private void ApplyEvent(Order order, OrderEvent e)
    {
        try
        {
            order.Apply(e);
        }
        catch (InvalidOrderTransitionException ex)
        {
            Log.LogWarning("{Message}", ex.Message);
        }
    }

    private void HandleFill(Order order, OrderFilled fill)
    {
        Instrument? instrument = _cache.Instrument(fill.InstrumentId);
        if (instrument is null)
        {
            Log.LogError("Cannot process fill for {InstrumentId}: instrument not in cache", fill.InstrumentId);
            return;
        }

        OmsType oms = ResolveOms(order.StrategyId, fill.InstrumentId.Venue);
        PositionId positionId = ResolvePositionId(order, fill, oms);
        Position? position = _cache.Position(positionId);

        // Netting: a fill that is larger than the open position flips it; split into close and open fills.
        if (oms == OmsType.Netting && position is { IsOpen: true } && position.IsOppositeSide(fill.OrderSide) && fill.LastQty > position.Quantity)
        {
            Quantity closeQty = position.Quantity;
            Quantity openQty = new(fill.LastQty.Value - closeQty.Value, fill.LastQty.Precision);
            decimal closeFraction = closeQty.Value / fill.LastQty.Value;
            Money closeCommission = new(fill.Commission.Amount * closeFraction, fill.Commission.Currency);
            Money openCommission = fill.Commission - closeCommission;

            OrderFilled closeFill = fill with { LastQty = closeQty, Commission = closeCommission, PositionId = positionId, TradeId = new TradeId(fill.TradeId.Value + "-C"), EventId = Guid.NewGuid() };
            ApplyFill(order, instrument, closeFill, position);

            PositionId newId = NextNettingPositionId(order);
            OrderFilled openFill = fill with { LastQty = openQty, Commission = openCommission, PositionId = newId, TradeId = new TradeId(fill.TradeId.Value + "-O"), EventId = Guid.NewGuid() };
            order.SetPosition(newId);
            ApplyFill(order, instrument, openFill, null);
            return;
        }

        if (position is { IsClosed: true } && oms == OmsType.Netting)
        {
            positionId = NextNettingPositionId(order);
            position = null;
        }

        ApplyFill(order, instrument, fill with { PositionId = positionId }, position);
    }

    private void ApplyFill(Order order, Instrument instrument, OrderFilled fill, Position? position)
    {
        ApplyEvent(order, fill);
        order.AssignPosition(fill.PositionId!.Value);
        _cache.UpdateOrder(order);

        PositionEvent positionEvent;
        if (position is null)
        {
            position = new Position(instrument, fill);
            _cache.AddPosition(position);
            positionEvent = new PositionOpened(position.TraderId, position.StrategyId, position.InstrumentId, position.Id, position.AccountId,
                position.OpeningOrderId, position.EntrySide, position.Side, new Quantity(Math.Abs(position.SignedQuantity), position.Quantity.Precision), position.Quantity,
                position.PeakQuantity, fill.LastQty, fill.LastPx, position.SettlementCurrency, position.AvgPxOpen, position.RealizedPnl,
                position.UnrealizedPnl(fill.LastPx), position.TsOpened, Guid.NewGuid(), fill.TsEvent, Clock.Timestamp);
        }
        else
        {
            position.Apply(fill);
            _cache.UpdatePosition(position);
            if (position.IsClosed)
            {
                positionEvent = new PositionClosed(position.TraderId, position.StrategyId, position.InstrumentId, position.Id, position.AccountId,
                    position.OpeningOrderId, position.ClosingOrderId, position.EntrySide, position.PeakQuantity, fill.LastQty, fill.LastPx,
                    position.SettlementCurrency, position.AvgPxOpen, position.AvgPxClose, position.RealizedPnl, position.TsOpened,
                    position.TsClosed, position.Duration, Guid.NewGuid(), fill.TsEvent, Clock.Timestamp);
            }
            else
            {
                positionEvent = new PositionChanged(position.TraderId, position.StrategyId, position.InstrumentId, position.Id, position.AccountId,
                    position.OpeningOrderId, position.EntrySide, position.Side, new Quantity(Math.Abs(position.SignedQuantity), position.Quantity.Precision), position.Quantity,
                    position.PeakQuantity, fill.LastQty, fill.LastPx, position.SettlementCurrency, position.AvgPxOpen, position.AvgPxClose,
                    position.RealizedPnl, position.UnrealizedPnl(fill.LastPx), position.TsOpened, Guid.NewGuid(), fill.TsEvent, Clock.Timestamp);
            }
        }

        Publish(order, fill);
        _bus.Publish(Topics.PositionEvents(position.StrategyId), positionEvent);
    }

    private OmsType ResolveOms(StrategyId strategyId, Venue venue)
    {
        if (_omsTypes.TryGetValue(strategyId, out OmsType strategyOms) && strategyOms != OmsType.Unspecified)
        {
            return strategyOms;
        }

        IExecutionClient? client = ClientFor(null, venue);
        if (client is not null && client.OmsType != OmsType.Unspecified)
        {
            return client.OmsType;
        }

        return OmsType.Netting;
    }

    private PositionId ResolvePositionId(Order order, OrderFilled fill, OmsType oms)
    {
        if (order.PositionId is { } existing && (oms == OmsType.Hedging || _cache.Position(existing) is { IsOpen: true }))
        {
            return existing;
        }

        if (oms == OmsType.Hedging)
        {
            if (fill.PositionId is { } venuePositionId)
            {
                return venuePositionId;
            }

            return new PositionId($"P-{Clock.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{++_positionSequence:000}");
        }

        // Netting: the open position for this instrument and strategy, if any.
        Position? open = _cache.PositionsOpen(instrumentId: order.InstrumentId, strategyId: order.StrategyId).FirstOrDefault();
        if (open is not null)
        {
            return open.Id;
        }

        return NextNettingPositionId(order);
    }

    private PositionId NextNettingPositionId(Order order)
    {
        string key = $"{order.InstrumentId}-{order.StrategyId}";
        int count = _positionCounters.GetValueOrDefault(key);
        while (true)
        {
            count++;
            PositionId candidate = new(count == 1 ? key : $"{key}-{count}");
            if (!_cache.PositionExists(candidate))
            {
                _positionCounters[key] = count;
                return candidate;
            }
        }
    }

    private void Publish(Order order, OrderEvent e)
    {
        if (_config.LogEvents)
        {
            Log.LogInformation("{Event}", Describe(e));
        }

        _bus.Publish(Topics.OrderEvents(order.StrategyId), e);
    }

    private static string Describe(OrderEvent e) => e switch
    {
        OrderFilled f => $"OrderFilled({f.ClientOrderId}, {f.OrderSide} {f.LastQty} @ {f.LastPx}, commission={f.Commission}, {f.LiquiditySide})",
        OrderDenied d => $"OrderDenied({d.ClientOrderId}, {d.Reason})",
        OrderRejected r => $"OrderRejected({r.ClientOrderId}, {r.Reason})",
        OrderModifyRejected r => $"OrderModifyRejected({r.ClientOrderId}, {r.Reason})",
        OrderCancelRejected r => $"OrderCancelRejected({r.ClientOrderId}, {r.Reason})",
        _ => $"{e.GetType().Name}({e.ClientOrderId})",
    };

    // ----- IExecutionClientSink -----

    public void OnOrderEvent(OrderEvent e) => Process(e);

    public void OnAccountState(AccountState state) => Process(state);

    public void OnConnected(ClientId clientId) => Log.LogInformation("Execution client {ClientId} connected", clientId);

    public void OnDisconnected(ClientId clientId, string reason) => Log.LogWarning("Execution client {ClientId} disconnected: {Reason}", clientId, reason);

    // ----- Reconciliation -----

    /// <summary>
    /// Reconciles cached orders and positions with a venue's mass status report.
    /// </summary>
    public void ReconcileMassStatus(ExecutionMassStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        Log.LogInformation("Reconciling {Venue}: {Orders} orders, {Fills} fills, {Positions} positions", status.Venue, status.OrderReports.Count, status.FillReports.Count, status.PositionReports.Count);

        foreach (OrderStatusReport report in status.OrderReports)
        {
            ReconcileOrderReport(report, status.FillsForOrder(report.VenueOrderId));
        }

        foreach (PositionStatusReport report in status.PositionReports)
        {
            ReconcilePositionReport(report);
        }
    }

    public void ReconcileOrderReport(OrderStatusReport report, IReadOnlyList<FillReport> fills)
    {
        ArgumentNullException.ThrowIfNull(report);
        Order? order = report.ClientOrderId is { } clientOrderId ? _cache.Order(clientOrderId) : null;
        order ??= _cache.OrderForVenueId(report.VenueOrderId);

        if (order is null)
        {
            order = CreateExternalOrder(report);
            if (order is null)
            {
                return;
            }
        }

        // Bring the order to the reported status, generating the events that were missed.
        if (order.Status == OrderStatus.Initialized || order.Status == OrderStatus.Submitted)
        {
            if (report.OrderStatus != OrderStatus.Rejected)
            {
                Process(new OrderAccepted(order.TraderId, order.StrategyId, order.InstrumentId, order.ClientOrderId, report.VenueOrderId, report.AccountId, Guid.NewGuid(), report.TsAccepted, Clock.Timestamp, true));
            }
            else
            {
                Process(new OrderRejected(order.TraderId, order.StrategyId, order.InstrumentId, order.ClientOrderId, report.AccountId, report.CancelReason ?? "reported rejected", Guid.NewGuid(), report.TsLast, Clock.Timestamp, true));
                return;
            }
        }

        if (report.TsTriggered is { } tsTriggered && order.Status == OrderStatus.Accepted)
        {
            Process(new OrderTriggered(order.TraderId, order.StrategyId, order.InstrumentId, order.ClientOrderId, report.VenueOrderId, report.AccountId, Guid.NewGuid(), tsTriggered, Clock.Timestamp, true));
        }

        if (report.Quantity != order.Quantity || (report.Price is { } px && order.Price is { } opx && px != opx) || (report.TriggerPrice is { } tpx && order.TriggerPrice is { } otpx && tpx != otpx))
        {
            Process(new OrderUpdated(order.TraderId, order.StrategyId, order.InstrumentId, order.ClientOrderId, report.VenueOrderId, report.AccountId, report.Quantity, report.Price, report.TriggerPrice, Guid.NewGuid(), report.TsLast, Clock.Timestamp, true));
        }

        foreach (FillReport fill in fills.OrderBy(f => f.TsEvent))
        {
            if (order.TradeIds.Contains(fill.TradeId))
            {
                continue;
            }

            Process(new OrderFilled(order.TraderId, order.StrategyId, order.InstrumentId, order.ClientOrderId, fill.VenueOrderId, fill.AccountId, fill.TradeId,
                fill.VenuePositionId, fill.OrderSide, order.Type, fill.LastQty, fill.LastPx, fill.Commission.Currency, fill.Commission, fill.LiquiditySide,
                Guid.NewGuid(), fill.TsEvent, Clock.Timestamp, true));
        }

        if (report.FilledQuantity > order.FilledQuantity)
        {
            // Fills are missing from the report; synthesise one so quantities match.
            Quantity missing = new(report.FilledQuantity.Value - order.FilledQuantity.Value, report.FilledQuantity.Precision);
            Price fillPx = report.AvgPx is { } avg ? new Price(avg, order.Price?.Precision ?? _cache.Instrument(order.InstrumentId)?.PricePrecision ?? 8) : order.Price ?? report.Price ?? Price.Zero(0);
            Instrument? instrument = _cache.Instrument(order.InstrumentId);
            Currency currency = instrument?.QuoteCurrency ?? Currencies.USD;
            Process(new OrderFilled(order.TraderId, order.StrategyId, order.InstrumentId, order.ClientOrderId, report.VenueOrderId, report.AccountId,
                new TradeId($"RECON-{report.VenueOrderId}-{order.FilledQuantity}"), null, order.Side, order.Type, missing, fillPx, currency, Money.Zero(currency), LiquiditySide.None,
                Guid.NewGuid(), report.TsLast, Clock.Timestamp, true, "Synthesised from order status report"));
        }

        switch (report.OrderStatus)
        {
            case OrderStatus.Canceled when !order.IsClosed:
                Process(new OrderCanceled(order.TraderId, order.StrategyId, order.InstrumentId, order.ClientOrderId, report.VenueOrderId, report.AccountId, Guid.NewGuid(), report.TsLast, Clock.Timestamp, true));
                break;
            case OrderStatus.Expired when !order.IsClosed:
                Process(new OrderExpired(order.TraderId, order.StrategyId, order.InstrumentId, order.ClientOrderId, report.VenueOrderId, report.AccountId, Guid.NewGuid(), report.TsLast, Clock.Timestamp, true));
                break;
        }
    }

    private Order? CreateExternalOrder(OrderStatusReport report)
    {
        Instrument? instrument = _cache.Instrument(report.InstrumentId);
        if (instrument is null)
        {
            Log.LogWarning("Cannot reconcile external order {VenueOrderId}: instrument {InstrumentId} not in cache", report.VenueOrderId, report.InstrumentId);
            return null;
        }

        StrategyId strategyId = _externalClaims.TryGetValue(report.InstrumentId, out StrategyId claimed) ? claimed : StrategyId.External;
        ClientOrderId clientOrderId = report.ClientOrderId ?? new ClientOrderId($"O-{report.VenueOrderId}");
        OrderParams p = new()
        {
            TraderId = _bus.TraderId,
            StrategyId = strategyId,
            InstrumentId = report.InstrumentId,
            ClientOrderId = clientOrderId,
            Side = report.OrderSide,
            Quantity = report.Quantity,
            TimeInForce = report.TimeInForce,
            PostOnly = report.PostOnly,
            ReduceOnly = report.ReduceOnly,
            Contingency = report.Contingency,
            OrderListId = report.OrderListId,
            InitId = Guid.NewGuid(),
            TsInit = report.TsAccepted,
        };

        Order order = report.OrderType switch
        {
            OrderType.Limit => LimitOrder.Create(p, report.Price ?? Price.Zero(instrument.PricePrecision), report.ExpireTime, report.DisplayQuantity),
            OrderType.StopMarket => StopMarketOrder.Create(p, report.TriggerPrice ?? Price.Zero(instrument.PricePrecision), report.TriggerType, report.ExpireTime),
            OrderType.StopLimit => StopLimitOrder.Create(p, report.Price ?? Price.Zero(instrument.PricePrecision), report.TriggerPrice ?? Price.Zero(instrument.PricePrecision), report.TriggerType, report.ExpireTime, report.DisplayQuantity),
            OrderType.MarketIfTouched => MarketIfTouchedOrder.Create(p, report.TriggerPrice ?? Price.Zero(instrument.PricePrecision), report.TriggerType, report.ExpireTime),
            OrderType.LimitIfTouched => LimitIfTouchedOrder.Create(p, report.Price ?? Price.Zero(instrument.PricePrecision), report.TriggerPrice ?? Price.Zero(instrument.PricePrecision), report.TriggerType, report.ExpireTime, report.DisplayQuantity),
            OrderType.TrailingStopMarket => TrailingStopMarketOrder.Create(p, report.TrailingOffset ?? 0m, report.TrailingOffsetType, report.TriggerPrice, null, report.TriggerType, report.ExpireTime),
            OrderType.TrailingStopLimit => TrailingStopLimitOrder.Create(p, report.TrailingOffset ?? 0m, 0m, report.TrailingOffsetType, report.Price, report.TriggerPrice, null, report.TriggerType, report.ExpireTime),
            OrderType.MarketToLimit => MarketToLimitOrder.Create(p, report.ExpireTime, report.DisplayQuantity),
            _ => MarketOrder.Create(p with { TimeInForce = report.TimeInForce is TimeInForce.Gtd or TimeInForce.AtTheOpen or TimeInForce.AtTheClose ? TimeInForce.Gtc : report.TimeInForce }),
        };

        _cache.AddOrder(order);
        Log.LogInformation("Reconciled external order {ClientOrderId} ({VenueOrderId}) for {StrategyId}", clientOrderId, report.VenueOrderId, strategyId);
        Publish(order, order.InitEvent);
        return order;
    }

    private void ReconcilePositionReport(PositionStatusReport report)
    {
        decimal cached = 0m;
        foreach (Position position in _cache.PositionsOpen(instrumentId: report.InstrumentId))
        {
            cached += position.SignedQuantity;
        }

        if (cached != report.SignedQuantity)
        {
            Log.LogWarning("Position mismatch for {InstrumentId}: cached {Cached}, venue {Venue}", report.InstrumentId, cached, report.SignedQuantity);
        }
    }
}
