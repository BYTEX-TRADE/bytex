using Bytex.Core.Common;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Model.Reports;
using Microsoft.Extensions.Logging;

namespace Bytex.Core.Adapters;

/// <summary>
/// Base class for data clients: sink plumbing, connection state, and default no-op command handlers.
/// </summary>
public abstract class DataClientBase : Component, IDataClient
{
    private IDataClientSink? _sink;

    protected DataClientBase(ClientId clientId, Venue? venue, KernelServices services)
        : base(new ComponentId($"DataClient-{clientId}"))
    {
        ArgumentNullException.ThrowIfNull(services);
        ClientId = clientId;
        Venue = venue;
        Services = services;
        Initialize(services.Clock, services.Logging);
    }

    public ClientId ClientId { get; }

    public Venue? Venue { get; }

    public bool IsConnected { get; protected set; }

    protected KernelServices Services { get; }

    protected IDataClientSink Sink => _sink ?? throw new InvalidOperationException($"Data client {ClientId} has no sink attached.");

    public void AttachSink(IDataClientSink sink) => _sink = sink ?? throw new ArgumentNullException(nameof(sink));

    public virtual Task ConnectAsync(CancellationToken ct)
    {
        IsConnected = true;
        Sink.OnConnected(ClientId);
        return Task.CompletedTask;
    }

    public virtual Task DisconnectAsync(CancellationToken ct)
    {
        IsConnected = false;
        Sink.OnDisconnected(ClientId, "disconnect requested");
        return Task.CompletedTask;
    }

    public virtual Task SubscribeAsync(SubscribeCommand command, CancellationToken ct) => Task.CompletedTask;

    public virtual Task UnsubscribeAsync(UnsubscribeCommand command, CancellationToken ct) => Task.CompletedTask;

    public virtual Task RequestAsync(RequestCommand command, CancellationToken ct)
    {
        Sink.OnResponse(new DataResponse(command.CommandId, ClientId, Venue, typeof(IData), [], Clock.Timestamp, command.Requester, "Historical requests are not supported by this client."));
        return Task.CompletedTask;
    }

    protected void HandleData(IData data) => Sink.OnData(data);

    protected void HandleInstrument(Instrument instrument) => Sink.OnInstrument(instrument);

    protected void HandleResponse(DataResponse response) => Sink.OnResponse(response);

    protected void SendResponse(RequestCommand request, Type dataType, IReadOnlyList<IData> data) =>
        Sink.OnResponse(new DataResponse(request.CommandId, ClientId, Venue, dataType, data, Clock.Timestamp, request.Requester));

    protected void SendErrorResponse(RequestCommand request, string error) =>
        Sink.OnResponse(new DataResponse(request.CommandId, ClientId, Venue, typeof(IData), [], Clock.Timestamp, request.Requester, error));

    protected void NotifyConnected()
    {
        IsConnected = true;
        Sink.OnConnected(ClientId);
    }

    protected void NotifyDisconnected(string reason)
    {
        IsConnected = false;
        Sink.OnDisconnected(ClientId, reason);
    }
}

/// <summary>
/// Base class for execution clients: sink plumbing and event construction helpers that keep events consistent.
/// </summary>
public abstract class ExecutionClientBase : Component, IExecutionClient
{
    private IExecutionClientSink? _sink;

    protected ExecutionClientBase(ClientId clientId, Venue venue, AccountId accountId, AccountType accountType, Currency? baseCurrency, OmsType omsType, KernelServices services)
        : base(new ComponentId($"ExecClient-{clientId}"))
    {
        ArgumentNullException.ThrowIfNull(services);
        ClientId = clientId;
        Venue = venue;
        AccountId = accountId;
        AccountType = accountType;
        BaseCurrency = baseCurrency;
        OmsType = omsType;
        Services = services;
        Initialize(services.Clock, services.Logging);
    }

    public ClientId ClientId { get; }

    public Venue Venue { get; }

    public AccountId AccountId { get; }

    public AccountType AccountType { get; }

    public Currency? BaseCurrency { get; }

    public OmsType OmsType { get; }

    public bool IsConnected { get; protected set; }

    protected KernelServices Services { get; }

    protected TraderId TraderId => Services.TraderId;

    protected IExecutionClientSink Sink => _sink ?? throw new InvalidOperationException($"Execution client {ClientId} has no sink attached.");

    public void AttachSink(IExecutionClientSink sink) => _sink = sink ?? throw new ArgumentNullException(nameof(sink));

    public virtual Task ConnectAsync(CancellationToken ct)
    {
        IsConnected = true;
        Sink.OnConnected(ClientId);
        return Task.CompletedTask;
    }

    public virtual Task DisconnectAsync(CancellationToken ct)
    {
        IsConnected = false;
        Sink.OnDisconnected(ClientId, "disconnect requested");
        return Task.CompletedTask;
    }

    public abstract Task SubmitOrderAsync(SubmitOrder command, CancellationToken ct);

    /// <summary>
    /// Orders held back until the order that triggers them fills, keyed by that order. A one-triggers-other list is
    /// an entry and the exits that only make sense once it is on: sending the exits with the entry puts working
    /// orders at the venue against a position that does not exist yet.
    /// </summary>
    private readonly Dictionary<ClientOrderId, List<(Model.Orders.Order Order, SubmitOrderList List)>> _heldChildren = new();

    public virtual async Task SubmitOrderListAsync(SubmitOrderList command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        // One-triggers-other: the parents go now, the children wait for the fill that triggers them. Any other
        // contingency - one-cancels-other, one-updates-other - is a set of orders that belong at the venue together.
        bool oto = command.OrderList.Orders.Any(o => o.Contingency == ContingencyType.Oto);
        foreach (Model.Orders.Order order in command.OrderList.Orders)
        {
            if (oto && order.ParentOrderId is { } parent)
            {
                (_heldChildren.TryGetValue(parent, out List<(Model.Orders.Order, SubmitOrderList)>? held)
                    ? held
                    : _heldChildren[parent] = new List<(Model.Orders.Order, SubmitOrderList)>()).Add((order, command));
                Log.LogInformation(
                    "Holding {ClientOrderId} at {Venue} until {Parent} fills: a one-triggers-other child is not sent with its parent",
                    order.ClientOrderId, Venue, parent);
                continue;
            }

            await SubmitOrderAsync(new SubmitOrder(command.TraderId, command.StrategyId, order, command.PositionId, command.ExecAlgorithmId, command.ClientId, Guid.NewGuid(), Clock.Timestamp), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Releases what was waiting on an order, once that order has filled. The children go back through the execution
    /// engine rather than straight at the venue, so they are judged by the same rules as any other order the strategy
    /// sends - a child released into an account that can no longer carry it is refused, not quietly worked.
    /// </summary>
    private void ReleaseHeldChildren(ClientOrderId parent, UnixNanos ts)
    {
        if (!_heldChildren.Remove(parent, out List<(Model.Orders.Order Order, SubmitOrderList List)>? children))
        {
            return;
        }

        foreach ((Model.Orders.Order order, SubmitOrderList list) in children)
        {
            Log.LogInformation("Releasing {ClientOrderId} at {Venue}: {Parent} has filled", order.ClientOrderId, Venue, parent);
            Services.MessageBus.Send(
                Endpoints.ExecutionEngineExecute,
                new SubmitOrder(list.TraderId, list.StrategyId, order, list.PositionId, list.ExecAlgorithmId, list.ClientId, Guid.NewGuid(), ts));
        }
    }

    /// <summary>Forgets what was waiting on an order that will never fill.</summary>
    private void DropHeldChildren(ClientOrderId parent)
    {
        if (_heldChildren.Remove(parent, out List<(Model.Orders.Order Order, SubmitOrderList List)>? children))
        {
            Log.LogInformation(
                "Dropping {Count} held child order(s) of {Parent} at {Venue}: the order that would have triggered them is closed",
                children.Count, parent, Venue);
        }
    }

    public abstract Task ModifyOrderAsync(ModifyOrder command, CancellationToken ct);

    public abstract Task CancelOrderAsync(CancelOrder command, CancellationToken ct);

    public virtual async Task CancelAllOrdersAsync(CancelAllOrders command, CancellationToken ct)
    {
        foreach (Model.Orders.Order order in Services.Cache.OrdersOpen(Venue, command.InstrumentId, command.StrategyId, command.OrderSide))
        {
            await CancelOrderAsync(new CancelOrder(command.TraderId, command.StrategyId, command.InstrumentId, order.ClientOrderId, order.VenueOrderId, command.ClientId, Guid.NewGuid(), Clock.Timestamp), ct).ConfigureAwait(false);
        }
    }

    public virtual async Task BatchCancelOrdersAsync(BatchCancelOrders command, CancellationToken ct)
    {
        foreach (CancelOrder cancel in command.Cancels)
        {
            await CancelOrderAsync(cancel, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Asks the venue what became of one order. Answering this with a completed task - as this did until a count of
    /// what each venue implements found it - tells the caller the query worked and leaves it waiting for an answer
    /// that is never coming. A venue that reports orders at all can answer it, so the answer is fetched here rather
    /// than left to each adapter to remember: Binance had it and Bybit and KuCoin did not, for no reason either of
    /// them could have given.
    /// </summary>
    public virtual async Task QueryOrderAsync(QueryOrder command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        OrderStatusReport? report = await GenerateOrderStatusReportAsync(command.InstrumentId, command.ClientOrderId, command.VenueOrderId, ct).ConfigureAwait(false);
        if (report is null)
        {
            // Either the venue has never heard of this order or this client cannot ask. Both are worth saying out
            // loud, because the caller asked a question and there is no answer to it.
            Log.LogWarning(
                "Order status {ClientOrderId} at {Venue}: nothing came back, so either the venue does not know this order or this client cannot ask after one",
                command.ClientOrderId, Venue);
            return;
        }

        // The order's own fills, so that anything this node missed is applied with the venue's trade ids instead of
        // being worked out by subtraction. One extra request, and the difference between a real trade and a
        // synthesised one is the difference between a report that can be audited and one that cannot.
        IReadOnlyList<FillReport> fills = await GenerateFillReportsAsync(command.InstrumentId, report.VenueOrderId, null, null, ct).ConfigureAwait(false);

        Log.LogInformation(
            "Order status {ClientOrderId}: {Status} filled {Filled}/{Quantity}, {Fills} fill(s) reported",
            report.ClientOrderId, report.OrderStatus, report.FilledQuantity, report.Quantity, fills.Count);

        // Answering only the log would leave the caller exactly where it was: the point of asking is that the order
        // this node holds catches up with the one the venue holds.
        Services.MessageBus.Send(Endpoints.ExecutionEngineProcess, new QueryOrderAnswer(report, fills));
    }

    public virtual Task<ExecutionMassStatus?> GenerateMassStatusAsync(UnixNanos? since, CancellationToken ct) => Task.FromResult<ExecutionMassStatus?>(null);

    public virtual Task<OrderStatusReport?> GenerateOrderStatusReportAsync(InstrumentId instrumentId, ClientOrderId? clientOrderId, VenueOrderId? venueOrderId, CancellationToken ct) => Task.FromResult<OrderStatusReport?>(null);

    public virtual Task<IReadOnlyList<OrderStatusReport>> GenerateOrderStatusReportsAsync(InstrumentId? instrumentId, UnixNanos? start, UnixNanos? end, bool openOnly, CancellationToken ct) => Task.FromResult<IReadOnlyList<OrderStatusReport>>([]);

    public virtual Task<IReadOnlyList<FillReport>> GenerateFillReportsAsync(InstrumentId? instrumentId, VenueOrderId? venueOrderId, UnixNanos? start, UnixNanos? end, CancellationToken ct) => Task.FromResult<IReadOnlyList<FillReport>>([]);

    public virtual Task<IReadOnlyList<PositionStatusReport>> GeneratePositionStatusReportsAsync(InstrumentId? instrumentId, UnixNanos? start, UnixNanos? end, CancellationToken ct) => Task.FromResult<IReadOnlyList<PositionStatusReport>>([]);

    // ----- Event generation helpers -----

    protected void GenerateOrderSubmitted(StrategyId strategyId, InstrumentId instrumentId, ClientOrderId clientOrderId, UnixNanos tsEvent) =>
        Sink.OnOrderEvent(new OrderSubmitted(TraderId, strategyId, instrumentId, clientOrderId, AccountId, Guid.NewGuid(), tsEvent, Clock.Timestamp));

    protected void GenerateOrderAccepted(StrategyId strategyId, InstrumentId instrumentId, ClientOrderId clientOrderId, VenueOrderId venueOrderId, UnixNanos tsEvent, bool reconciliation = false) =>
        Sink.OnOrderEvent(new OrderAccepted(TraderId, strategyId, instrumentId, clientOrderId, venueOrderId, AccountId, Guid.NewGuid(), tsEvent, Clock.Timestamp, reconciliation));

    protected void GenerateOrderRejected(StrategyId strategyId, InstrumentId instrumentId, ClientOrderId clientOrderId, string reason, UnixNanos tsEvent, bool reconciliation = false)
    {
        DropHeldChildren(clientOrderId);
        SinkRejected(strategyId, instrumentId, clientOrderId, reason, tsEvent, reconciliation);
    }

    private void SinkRejected(StrategyId strategyId, InstrumentId instrumentId, ClientOrderId clientOrderId, string reason, UnixNanos tsEvent, bool reconciliation) =>
        Sink.OnOrderEvent(new OrderRejected(TraderId, strategyId, instrumentId, clientOrderId, AccountId, reason, Guid.NewGuid(), tsEvent, Clock.Timestamp, reconciliation));

    protected void GenerateOrderCanceled(StrategyId strategyId, InstrumentId instrumentId, ClientOrderId clientOrderId, VenueOrderId? venueOrderId, UnixNanos tsEvent, bool reconciliation = false)
    {
        DropHeldChildren(clientOrderId);
        SinkCanceled(strategyId, instrumentId, clientOrderId, venueOrderId, tsEvent, reconciliation);
    }

    private void SinkCanceled(StrategyId strategyId, InstrumentId instrumentId, ClientOrderId clientOrderId, VenueOrderId? venueOrderId, UnixNanos tsEvent, bool reconciliation) =>
        Sink.OnOrderEvent(new OrderCanceled(TraderId, strategyId, instrumentId, clientOrderId, venueOrderId, AccountId, Guid.NewGuid(), tsEvent, Clock.Timestamp, reconciliation));

    protected void GenerateOrderExpired(StrategyId strategyId, InstrumentId instrumentId, ClientOrderId clientOrderId, VenueOrderId? venueOrderId, UnixNanos tsEvent, bool reconciliation = false)
    {
        DropHeldChildren(clientOrderId);
        SinkExpired(strategyId, instrumentId, clientOrderId, venueOrderId, tsEvent, reconciliation);
    }

    private void SinkExpired(StrategyId strategyId, InstrumentId instrumentId, ClientOrderId clientOrderId, VenueOrderId? venueOrderId, UnixNanos tsEvent, bool reconciliation) =>
        Sink.OnOrderEvent(new OrderExpired(TraderId, strategyId, instrumentId, clientOrderId, venueOrderId, AccountId, Guid.NewGuid(), tsEvent, Clock.Timestamp, reconciliation));

    protected void GenerateOrderTriggered(StrategyId strategyId, InstrumentId instrumentId, ClientOrderId clientOrderId, VenueOrderId? venueOrderId, UnixNanos tsEvent, bool reconciliation = false) =>
        Sink.OnOrderEvent(new OrderTriggered(TraderId, strategyId, instrumentId, clientOrderId, venueOrderId, AccountId, Guid.NewGuid(), tsEvent, Clock.Timestamp, reconciliation));

    protected void GenerateOrderPendingUpdate(StrategyId strategyId, InstrumentId instrumentId, ClientOrderId clientOrderId, VenueOrderId? venueOrderId, UnixNanos tsEvent) =>
        Sink.OnOrderEvent(new OrderPendingUpdate(TraderId, strategyId, instrumentId, clientOrderId, venueOrderId, AccountId, Guid.NewGuid(), tsEvent, Clock.Timestamp));

    protected void GenerateOrderPendingCancel(StrategyId strategyId, InstrumentId instrumentId, ClientOrderId clientOrderId, VenueOrderId? venueOrderId, UnixNanos tsEvent) =>
        Sink.OnOrderEvent(new OrderPendingCancel(TraderId, strategyId, instrumentId, clientOrderId, venueOrderId, AccountId, Guid.NewGuid(), tsEvent, Clock.Timestamp));

    protected void GenerateOrderModifyRejected(StrategyId strategyId, InstrumentId instrumentId, ClientOrderId clientOrderId, VenueOrderId? venueOrderId, string reason, UnixNanos tsEvent) =>
        Sink.OnOrderEvent(new OrderModifyRejected(TraderId, strategyId, instrumentId, clientOrderId, venueOrderId, AccountId, reason, Guid.NewGuid(), tsEvent, Clock.Timestamp));

    protected void GenerateOrderCancelRejected(StrategyId strategyId, InstrumentId instrumentId, ClientOrderId clientOrderId, VenueOrderId? venueOrderId, string reason, UnixNanos tsEvent) =>
        Sink.OnOrderEvent(new OrderCancelRejected(TraderId, strategyId, instrumentId, clientOrderId, venueOrderId, AccountId, reason, Guid.NewGuid(), tsEvent, Clock.Timestamp));

    protected void GenerateOrderUpdated(StrategyId strategyId, InstrumentId instrumentId, ClientOrderId clientOrderId, VenueOrderId? venueOrderId, Quantity quantity, Price? price, Price? triggerPrice, UnixNanos tsEvent, bool reconciliation = false) =>
        Sink.OnOrderEvent(new OrderUpdated(TraderId, strategyId, instrumentId, clientOrderId, venueOrderId, AccountId, quantity, price, triggerPrice, Guid.NewGuid(), tsEvent, Clock.Timestamp, reconciliation));

    protected void GenerateOrderFilled(StrategyId strategyId, InstrumentId instrumentId, ClientOrderId clientOrderId, VenueOrderId venueOrderId, PositionId? venuePositionId,
        TradeId tradeId, OrderSide orderSide, OrderType orderType, Quantity lastQty, Price lastPx, Currency quoteCurrency, Money commission, LiquiditySide liquiditySide, UnixNanos tsEvent, bool reconciliation = false) =>
        SinkFilled(strategyId, instrumentId, clientOrderId, venueOrderId, venuePositionId, tradeId, orderSide, orderType, lastQty, lastPx, quoteCurrency, commission, liquiditySide, tsEvent, reconciliation);

    private void SinkFilled(StrategyId strategyId, InstrumentId instrumentId, ClientOrderId clientOrderId, VenueOrderId venueOrderId, PositionId? venuePositionId,
        TradeId tradeId, OrderSide orderSide, OrderType orderType, Quantity lastQty, Price lastPx, Currency quoteCurrency, Money commission, LiquiditySide liquiditySide, UnixNanos tsEvent, bool reconciliation)
    {
        Sink.OnOrderEvent(new OrderFilled(TraderId, strategyId, instrumentId, clientOrderId, venueOrderId, AccountId, tradeId, venuePositionId, orderSide, orderType, lastQty, lastPx, quoteCurrency, commission, liquiditySide, Guid.NewGuid(), tsEvent, Clock.Timestamp, reconciliation));

        // The fill is reported before the children go out, so whoever is listening sees the entry on before it sees
        // the exits that protect it.
        ReleaseHeldChildren(clientOrderId, tsEvent);
    }

    protected void GenerateAccountState(
        IReadOnlyList<AccountBalance> balances,
        IReadOnlyList<MarginBalance> margins,
        bool reported,
        UnixNanos tsEvent,
        IReadOnlyDictionary<string, string>? info = null,
        IReadOnlyDictionary<InstrumentId, decimal>? leverages = null,
        decimal? defaultLeverage = null) =>
        Sink.OnAccountState(new AccountState(
            AccountId, AccountType, BaseCurrency, reported, balances, margins, info ?? new Dictionary<string, string>(StringComparer.Ordinal),
            Guid.NewGuid(), tsEvent, Clock.Timestamp, leverages, defaultLeverage));

    protected void NotifyConnected()
    {
        IsConnected = true;
        Sink.OnConnected(ClientId);
    }

    protected void NotifyDisconnected(string reason)
    {
        IsConnected = false;
        Sink.OnDisconnected(ClientId, reason);
    }
}
