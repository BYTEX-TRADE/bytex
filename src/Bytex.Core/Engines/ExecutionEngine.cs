using System.Globalization;
using Bytex.Core.Adapters;
using Bytex.Core.Caching;
using Bytex.Core.Common;
using Bytex.Core.Messaging;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
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
    private const string SplitCloseSuffix = "-C";
    private const string SplitOpenSuffix = "-O";

    private readonly IMessageBus _bus;
    private readonly Cache _cache;
    private readonly ExecutionEngineConfig _config;
    private readonly Dictionary<ClientId, IExecutionClient> _clients = new();
    private readonly Dictionary<Venue, IExecutionClient> _routing = new();
    private readonly Dictionary<StrategyId, OmsType> _omsTypes = new();
    private readonly Dictionary<InstrumentId, StrategyId> _externalClaims = new();
    private int _reconciledPositions;
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
        foreach (string topic in EmulationTopics)
        {
            _bus.Subscribe(topic, OnPriceForEmulation);
        }
    }

    public ExecutionEngineConfig Config => _config;

    public IReadOnlyCollection<ClientId> RegisteredClients => _clients.Keys;

    public IReadOnlyCollection<IExecutionClient> Clients => _clients.Values;

    public long CommandCount { get; private set; }

    public long EventCount { get; private set; }

    /// <summary>How many mass statuses from a venue the engine has reconciled itself against.</summary>
    public long ReconciliationCount { get; private set; }

    /// <summary>
    /// How many times a venue's report and this engine's own record disagreed and the engine took the venue's word:
    /// an order it had never heard of, an order whose state it had wrong, a position it was not carrying. A number
    /// that keeps climbing while a node runs is the node and its venue drifting apart, which is what a host watches.
    /// </summary>
    public long ReconciledDifferences { get; private set; }

    /// <summary>When the engine last reconciled itself against a venue; null until it has.</summary>
    public UnixNanos? LastReconciliation { get; private set; }

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
        ReconciliationCount = 0;
        ReconciledDifferences = 0;
        LastReconciliation = null;
        _emulated.Clear();
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

    /// <summary>
    /// Orders whose submission has gone to a venue and which the venue has not answered for yet. Such an order cannot be
    /// cancelled locally: the venue is about to work it, and a local cancel would leave the two sides disagreeing.
    /// </summary>
    private readonly HashSet<ClientOrderId> _routed = new();

    /// <summary>
    /// Orders whose cancel the venue refused because it did not hold the order yet. The cancel is sent again the moment
    /// the venue accepts the order, so an order its owner has given up on does not go on working.
    /// </summary>
    private readonly HashSet<ClientOrderId> _cancelOnArrival = new();

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

        if (AsksForEmulation(order))
        {
            Hold(command);
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

        // A list goes to the venue as a list, so its legs are the venue's to trigger. An order asking to be held here
        // inside one would have to be sent on its own, which would break the contingency the list was submitted for:
        // the list is refused rather than half sent, and every order in it is told so.
        if (command.OrderList.Orders.Any(AsksForEmulation))
        {
            foreach (Order order in command.OrderList.Orders)
            {
                Deny(order, "EMULATION_IN_LIST: an order list is sent to the venue as a list; an order triggered here is submitted on its own");
            }

            return;
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

        if (order.IsActiveLocal && !_routed.Contains(order.ClientOrderId))
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
            if (order.IsActiveLocal && !_routed.Contains(order.ClientOrderId))
            {
                Process(new OrderCanceled(order.TraderId, order.StrategyId, order.InstrumentId, order.ClientOrderId, order.VenueOrderId, order.AccountId, Guid.NewGuid(), Clock.Timestamp, Clock.Timestamp));
            }
        }

        Route(command, (c, ct) => c.CancelAllOrdersAsync(command, ct));
    }

    // ----- Order emulation -----

    /// <summary>What the emulator listens to: everything that carries a price a local trigger can be judged against.</summary>
    private static readonly string[] EmulationTopics =
        [Topics.AllQuotes, Topics.AllTrades, Topics.AllBars, Topics.AllMarkPrices, Topics.AllIndexPrices];

    /// <summary>The orders held here instead of at the venue, by the instrument whose price will release them.</summary>
    private readonly Dictionary<InstrumentId, HashSet<ClientOrderId>> _emulated = new();

    /// <summary>How many orders the engine is holding locally: what a venue has not been told about yet.</summary>
    public int OrdersHeldLocally => _emulated.Values.Sum(held => held.Count);

    /// <summary>
    /// An order asks to be triggered here rather than at the venue by naming the price its trigger is judged against.
    /// <see cref="TriggerType.Default"/>, which is what an order carries unless it says otherwise, leaves the trigger
    /// to the venue.
    /// </summary>
    private static bool AsksForEmulation(Order order) => order.EmulationTrigger != TriggerType.Default;

    /// <summary>
    /// Holds an order here instead of sending it. The venue is told nothing until the trigger is reached, and what it
    /// is told then is a market or a limit order, which every venue can hold - that is what emulation is for: a stop
    /// on a venue that has no stops, and a trigger that is this engine's business rather than something a strategy has
    /// to poll for. Returns false when the order was denied instead, and then there is nothing left to do with it.
    /// </summary>
    private bool Hold(SubmitOrder command)
    {
        Order order = command.Order;
        if (order.Type is not (OrderType.StopMarket or OrderType.StopLimit or OrderType.MarketIfTouched or OrderType.LimitIfTouched))
        {
            Deny(order, $"EMULATION_UNSUPPORTED: a {order.Type} order cannot be triggered here; stop-market, stop-limit, market-if-touched and limit-if-touched can");
            return false;
        }

        if (order.TriggerPrice is null)
        {
            Deny(order, "EMULATION_UNSUPPORTED: an order triggered here needs a trigger price");
            return false;
        }

        Process(new OrderEmulated(order.TraderId, order.StrategyId, order.InstrumentId, order.ClientOrderId, Guid.NewGuid(), Clock.Timestamp, Clock.Timestamp));
        if (!order.IsEmulated)
        {
            // The order refused the transition, which is logged where it was refused. The venue is told nothing: an
            // order the engine cannot account for is not one to send.
            return false;
        }

        if (!_emulated.TryGetValue(order.InstrumentId, out HashSet<ClientOrderId>? held))
        {
            held = new HashSet<ClientOrderId>();
            _emulated[order.InstrumentId] = held;
        }

        held.Add(order.ClientOrderId);
        Log.LogInformation("Holding {ClientOrderId} here: {Type} triggered on {Trigger} at {Price}", order.ClientOrderId, order.Type, order.EmulationTrigger, order.TriggerPrice);

        // A trigger the market has already reached is reached now: a stop submitted through the market does not wait
        // for the next tick to notice what it could see when it arrived.
        Review(order.InstrumentId);
        return true;
    }

    /// <summary>Forgets an order the engine was holding: it was cancelled, it expired, or it has been released.</summary>
    private void NoLongerHeld(Order order)
    {
        if (_emulated.TryGetValue(order.InstrumentId, out HashSet<ClientOrderId>? held) && held.Remove(order.ClientOrderId) && held.Count == 0)
        {
            _emulated.Remove(order.InstrumentId);
        }
    }

    /// <summary>A price arrived: whatever is held on that instrument is judged against it.</summary>
    private void OnPriceForEmulation(object message)
    {
        if (_emulated.Count == 0)
        {
            return;
        }

        InstrumentId? instrumentId = message switch
        {
            QuoteTick quote => quote.InstrumentId,
            TradeTick trade => trade.InstrumentId,
            Bar bar => bar.BarType.InstrumentId,
            MarkPriceUpdate mark => mark.InstrumentId,
            IndexPriceUpdate index => index.InstrumentId,
            _ => null,
        };

        if (instrumentId is { } id)
        {
            Review(id);
        }
    }

    /// <summary>
    /// Everything held on one instrument, judged once: released if its trigger has been reached, expired if its own
    /// time has passed, and forgotten if it is no longer this engine's to release - a cancelled order leaves through
    /// the cancel path, which needs no venue because nothing was ever sent.
    /// </summary>
    private void Review(InstrumentId instrumentId)
    {
        if (!_emulated.TryGetValue(instrumentId, out HashSet<ClientOrderId>? held) || held.Count == 0)
        {
            return;
        }

        foreach (ClientOrderId id in held.ToList())
        {
            Order? order = _cache.Order(id);
            if (order is null || !order.IsEmulated)
            {
                // An order that was cancelled or that expired was dropped when it closed, so this is the second line
                // of defence: whatever is here and no longer emulated is not this engine's to release.
                held.Remove(id);
                continue;
            }

            if (order.ExpireTime is { } expiry && Clock.Timestamp >= expiry)
            {
                held.Remove(id);
                Log.LogInformation("{ClientOrderId} expired here at {Expiry} without its trigger being reached", id, expiry);
                Process(new OrderExpired(order.TraderId, order.StrategyId, order.InstrumentId, order.ClientOrderId, order.VenueOrderId, order.AccountId, Guid.NewGuid(), Clock.Timestamp, Clock.Timestamp));
                continue;
            }

            if (Touched(order) is { } price)
            {
                held.Remove(id);
                Release(order, price);
            }
        }
    }

    /// <summary>
    /// The price that reached the order's trigger, or null. A stop is reached when the market trades at its trigger or
    /// through it; an if-touched order is the mirror, reached when the market comes back to it. Which price counts is
    /// what the order asked for: the last trade, the side of the book it would have to cross, the mark, or the index.
    /// </summary>
    private Price? Touched(Order order)
    {
        if (order.TriggerPrice is not { } trigger)
        {
            return null;
        }

        InstrumentId id = order.TriggerInstrumentId ?? order.InstrumentId;
        Price? seen = order.EmulationTrigger switch
        {
            TriggerType.LastPrice => _cache.Price(id, PriceType.Last),
            TriggerType.BidAsk => _cache.Price(id, order.IsBuy ? PriceType.Ask : PriceType.Bid),
            TriggerType.MarkPrice => _cache.Price(id, PriceType.Mark),
            TriggerType.IndexPrice => _cache.IndexPrice(id)?.Value,
            _ => null,
        };

        if (seen is not { } price)
        {
            return null;
        }

        bool reached = order.Type is OrderType.StopMarket or OrderType.StopLimit
            ? order.IsBuy ? price.Value >= trigger.Value : price.Value <= trigger.Value
            : order.IsBuy ? price.Value <= trigger.Value : price.Value >= trigger.Value;
        return reached ? price : null;
    }

    /// <summary>
    /// Sends the venue what it can hold. The released order keeps the id its owner submitted - everything that refers
    /// to that order refers to it by that id, and the fill has to come back on it - so the market or limit order that
    /// goes out takes the held order's place in the cache and carries the same history: initialised, emulated,
    /// released. A stop-market and a market-if-touched leave as market orders, a stop-limit and a limit-if-touched as
    /// limit orders at the price they were holding.
    /// </summary>
    private void Release(Order held, Price at)
    {
        OrderReleased released = new(held.TraderId, held.StrategyId, held.InstrumentId, held.ClientOrderId, at, Guid.NewGuid(), Clock.Timestamp, Clock.Timestamp);
        Process(released);
        if (held.Status != OrderStatus.Released)
        {
            return;
        }

        bool asMarket = held.Type is OrderType.StopMarket or OrderType.MarketIfTouched;
        OrderParams p = new()
        {
            TraderId = held.TraderId,
            StrategyId = held.StrategyId,
            InstrumentId = held.InstrumentId,
            ClientOrderId = held.ClientOrderId,
            Side = held.Side,
            Quantity = held.Quantity,

            // A market order cannot carry the times in force that need a resting order; a released stop keeps its own.
            TimeInForce = asMarket && held.TimeInForce is TimeInForce.Gtd or TimeInForce.AtTheOpen or TimeInForce.AtTheClose
                ? TimeInForce.Gtc
                : held.TimeInForce,
            PostOnly = held.IsPostOnly,
            ReduceOnly = held.IsReduceOnly,
            QuoteQuantity = held.IsQuoteQuantity,

            // Left at Default deliberately: this is the order that goes to the venue, not one to hold again.
            EmulationTrigger = TriggerType.Default,
            Contingency = held.Contingency,
            OrderListId = held.OrderListId,
            LinkedOrderIds = held.LinkedOrderIds,
            ParentOrderId = held.ParentOrderId,
            ExecAlgorithmId = held.ExecAlgorithmId,
            ExecSpawnId = held.ExecSpawnId,
            Tags = held.Tags,
            InitId = Guid.NewGuid(),
            TsInit = Clock.Timestamp,
        };

        Order replacement = asMarket
            ? MarketOrder.Create(p)
            : LimitOrder.Create(p, held.Price ?? at, held.TimeInForce == TimeInForce.Gtd ? held.ExpireTime : null, held.DisplayQuantity);

        replacement.Apply(new OrderEmulated(held.TraderId, held.StrategyId, held.InstrumentId, held.ClientOrderId, Guid.NewGuid(), Clock.Timestamp, Clock.Timestamp));
        replacement.Apply(released);
        _cache.ReplaceOrder(replacement);

        Log.LogInformation("Releasing {ClientOrderId} to the venue as a {Type}: {Trigger} reached at {Price}", held.ClientOrderId, replacement.Type, held.TriggerPrice, at);
        // The position the held order was submitted under, which a hedging venue is told about: it lives in the cache
        // rather than on the order, because that is where it was put when the order was first submitted.
        PositionId? positionId = _cache.PositionIdFor(replacement.ClientOrderId) ?? replacement.PositionId;
        SubmitOrder submit = new(replacement.TraderId, replacement.StrategyId, replacement, positionId, null, null, Guid.NewGuid(), Clock.Timestamp);
        Route(submit, (c, ct) => c.SubmitOrderAsync(submit, ct));
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

        switch (command)
        {
            case SubmitOrder submitted:
                _routed.Add(submitted.Order.ClientOrderId);
                break;
            case SubmitOrderList list:
                foreach (Order order in list.OrderList.Orders)
                {
                    _routed.Add(order.ClientOrderId);
                }

                break;
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
            case QueryOrderAnswer answer:
                // A strategy asked what became of one order. Whatever the venue said is applied the way a start-up
                // reconciliation applies it, so an order whose events were lost catches up here instead of waiting
                // for the next restart - which is the only thing asking was ever going to be good for.
                ReconcileOrderReport(answer.Report, answer.Fills);
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

        // The order was found by its venue id, so the event carries an id the venue made up rather than the one this
        // order was submitted under. It is stamped with the order's own id: the lookup by venue id exists for exactly
        // this case, and applying the event as it arrived threw on the mismatch, which took the handler down.
        if (e.ClientOrderId != order.ClientOrderId)
        {
            Log.LogInformation("{EventType} arrived under {Foreign}; it belongs to {ClientOrderId} by venue id {VenueOrderId}",
                e.GetType().Name, e.ClientOrderId, order.ClientOrderId, e.VenueOrderId);
            e = e with { ClientOrderId = order.ClientOrderId };
        }

        // The venue has answered for this order, so it is no longer the engine's to cancel on its own.
        _routed.Remove(order.ClientOrderId);
        if (e is OrderCancelRejected && !order.IsOpen && !order.IsClosed && _cache.IsOrderPendingCancelLocal(order.ClientOrderId))
        {
            // The cancel overtook the order it belongs to: the venue refused it because it had nothing under that id
            // yet. The order is still coming, so the cancel is owed to it.
            _cancelOnArrival.Add(order.ClientOrderId);
        }

        if (e is OrderAccepted && _cancelOnArrival.Remove(order.ClientOrderId))
        {
            Log.LogInformation("Cancelling {ClientOrderId} on arrival: the cancel its owner sent arrived before it did", order.ClientOrderId);
            CancelOrder cancel = new(order.TraderId, order.StrategyId, order.InstrumentId, order.ClientOrderId, e.VenueOrderId, null, Guid.NewGuid(), Clock.Timestamp);
            Route(cancel, (c, ct) => c.CancelOrderAsync(cancel, ct));
        }

        if (order.IsClosed)
        {
            _cancelOnArrival.Remove(order.ClientOrderId);
        }

        if (e is OrderFilled fill)
        {
            HandleFill(order, fill);
            return;
        }

        if (!ApplyEvent(order, e))
        {
            return;
        }

        _cache.UpdateOrder(order);
        if (order.IsClosed)
        {
            // An order that is done is not one this engine is still holding for a trigger. Dropped the moment it is
            // done rather than when the next price happens to arrive, so what the engine says it holds is what it holds.
            NoLongerHeld(order);
        }

        Publish(order, e);
    }

    /// <summary>
    /// Applies the event to the order. False when the order refused it, and then nothing is published: a strategy that
    /// is told its cancelled order was accepted acts on an order that does not exist.
    /// </summary>
    private bool ApplyEvent(Order order, OrderEvent e)
    {
        try
        {
            order.Apply(e);
            return true;
        }
        catch (InvalidOrderTransitionException ex)
        {
            Log.LogWarning("{Message}", ex.Message);
            return false;
        }
        catch (ArgumentException ex)
        {
            Log.LogError(ex, "{EventType} could not be applied to {ClientOrderId}", e.GetType().Name, order.ClientOrderId);
            return false;
        }
    }

    private void HandleFill(Order order, OrderFilled fill)
    {
        // A venue may deliver the same trade again (reconnect, reconciliation). A flipping fill was booked as two parts under
        // derived trade ids, so the original id is looked for in that form too.
        bool alreadySplit = order.TradeIds.Contains(new TradeId(fill.TradeId.Value + SplitCloseSuffix)) && order.TradeIds.Contains(new TradeId(fill.TradeId.Value + SplitOpenSuffix));
        if (alreadySplit || order.TradeIds.Contains(fill.TradeId))
        {
            Log.LogDebug("Duplicate fill {TradeId} for {ClientOrderId} ignored", fill.TradeId, order.ClientOrderId);
            return;
        }

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

            OrderFilled closeFill = fill with { LastQty = closeQty, Commission = closeCommission, PositionId = positionId, TradeId = new TradeId(fill.TradeId.Value + SplitCloseSuffix), EventId = Guid.NewGuid() };
            ApplyFill(order, instrument, closeFill, position);

            PositionId newId = NextNettingPositionId(order);
            OrderFilled openFill = fill with { LastQty = openQty, Commission = openCommission, PositionId = newId, TradeId = new TradeId(fill.TradeId.Value + SplitOpenSuffix), EventId = Guid.NewGuid() };
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
        // The position an order was submitted against is kept by the cache until the first fill assigns it to the order.
        PositionId? assigned = order.PositionId ?? _cache.PositionIdFor(order.ClientOrderId);
        if (assigned is { } existing && (oms == OmsType.Hedging || _cache.Position(existing) is { IsOpen: true }))
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
        ReconciliationCount++;
        LastReconciliation = Clock.Timestamp;
        long differencesBefore = ReconciledDifferences;
        Log.LogInformation("Reconciling {Venue}: {Orders} orders, {Fills} fills, {Positions} positions", status.Venue, status.OrderReports.Count, status.FillReports.Count, status.PositionReports.Count);

        foreach (OrderStatusReport report in status.OrderReports)
        {
            ReconcileOrderReport(report, status.FillsForOrder(report.VenueOrderId));
        }

        foreach (PositionStatusReport report in status.PositionReports)
        {
            ReconcilePositionReport(report);
        }

        long differences = ReconciledDifferences - differencesBefore;
        if (differences > 0)
        {
            Log.LogWarning("Reconciled {Venue} with {Differences} difference(s) between what it reported and what this node held", status.Venue, differences);
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

            ReconciledDifferences++;
            Process(new OrderFilled(order.TraderId, order.StrategyId, order.InstrumentId, order.ClientOrderId, fill.VenueOrderId, fill.AccountId, fill.TradeId,
                fill.VenuePositionId, fill.OrderSide, order.Type, fill.LastQty, fill.LastPx, fill.Commission.Currency, fill.Commission, fill.LiquiditySide,
                Guid.NewGuid(), fill.TsEvent, Clock.Timestamp, true));
        }

        if (report.FilledQuantity > order.FilledQuantity)
        {
            // Fills are missing from the report; synthesise one so quantities match.
            ReconciledDifferences++;
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
            Tags = report.Tags ?? [],
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
        ReconciledDifferences++;
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

        decimal difference = report.SignedQuantity - cached;
        if (difference == 0m)
        {
            return;
        }

        ReconciledDifferences++;
        Log.LogWarning("Position mismatch for {InstrumentId}: cached {Cached}, venue {Venue}", report.InstrumentId, cached, report.SignedQuantity);

        // The venue is right about what the account holds. Saying so in the log and leaving the engine flat was how a node
        // restarted after its entry fill had dropped out of the reconciliation window came up believing it held nothing:
        // it would then size its next entry against no position, and manage risk it could not see. The difference is
        // booked as a fill on an order of its own, so the position exists with the venue's average price behind it.
        Instrument? instrument = _cache.Instrument(report.InstrumentId);
        if (instrument is null)
        {
            Log.LogError("Cannot adopt the reported position on {InstrumentId}: the instrument is not in the cache", report.InstrumentId);
            return;
        }

        decimal? price = report.AvgPxOpen ?? _cache.QuoteTick(report.InstrumentId)?.Mid.Value ?? _cache.TradeTick(report.InstrumentId)?.Price.Value;
        if (price is not > 0m)
        {
            Log.LogError("Cannot adopt the reported position of {Quantity} on {InstrumentId}: the venue gave no average price and the cache has no price either",
                report.SignedQuantity, report.InstrumentId);
            return;
        }

        StrategyId strategyId = _externalClaims.TryGetValue(report.InstrumentId, out StrategyId claimed) ? claimed : StrategyId.External;
        OrderSide side = difference > 0m ? OrderSide.Buy : OrderSide.Sell;
        Quantity quantity = new(Math.Abs(difference), instrument.SizePrecision);
        ClientOrderId clientOrderId = new($"RECON-POS-{report.InstrumentId.Symbol}-{++_reconciledPositions}");
        VenueOrderId venueOrderId = report.VenuePositionId is { } venuePosition ? new VenueOrderId($"P-{venuePosition.Value}") : new VenueOrderId($"P-{clientOrderId.Value}");
        OrderStatusReport adopted = new(report.AccountId, report.InstrumentId, clientOrderId, venueOrderId, side, OrderType.Market, TimeInForce.Gtc,
            OrderStatus.Filled, quantity, quantity, report.TsLast, report.TsLast, report.TsInit, Guid.NewGuid(), AvgPx: price);

        Order? order = CreateExternalOrder(adopted);
        if (order is null)
        {
            return;
        }

        Process(new OrderAccepted(order.TraderId, order.StrategyId, order.InstrumentId, order.ClientOrderId, venueOrderId, report.AccountId, Guid.NewGuid(), report.TsLast, Clock.Timestamp, true));
        Process(new OrderFilled(order.TraderId, order.StrategyId, order.InstrumentId, order.ClientOrderId, venueOrderId, report.AccountId,
            new TradeId($"RECON-POS-{clientOrderId.Value}"), report.VenuePositionId, side, OrderType.Market, quantity, instrument.MakePrice(price.Value),
            instrument.QuoteCurrency, Money.Zero(instrument.QuoteCurrency), LiquiditySide.None, Guid.NewGuid(), report.TsLast, Clock.Timestamp, true,
            "Adopted from the venue's position report"));

        Log.LogInformation("Adopted the venue's position on {InstrumentId} for {StrategyId}: {Side} {Quantity} at {Price}",
            report.InstrumentId, strategyId, side, quantity, price);
    }
}
