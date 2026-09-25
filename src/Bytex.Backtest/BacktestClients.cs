using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Model.Reports;

namespace Bytex.Backtest;

/// <summary>
/// Execution client that forwards commands to a <see cref="SimulatedExchange"/> and relays its events.
/// </summary>
public sealed class BacktestExecutionClient : ExecutionClientBase
{
    private readonly SimulatedExchange _exchange;

    public BacktestExecutionClient(SimulatedExchange exchange, KernelServices services)
        : base(new ClientId(exchange.Venue.Value), exchange.Venue, exchange.AccountId, exchange.Config.AccountType, exchange.Config.BaseCurrency, exchange.Config.OmsType, services)
    {
        _exchange = exchange;
        _exchange.Register(this);
    }

    public SimulatedExchange Exchange => _exchange;

    public override Task SubmitOrderAsync(SubmitOrder command, CancellationToken ct)
    {
        _exchange.Submit(command);
        return Task.CompletedTask;
    }

    public override Task SubmitOrderListAsync(SubmitOrderList command, CancellationToken ct)
    {
        _exchange.SubmitList(command);
        return Task.CompletedTask;
    }

    public override Task ModifyOrderAsync(ModifyOrder command, CancellationToken ct)
    {
        _exchange.Modify(command);
        return Task.CompletedTask;
    }

    public override Task CancelOrderAsync(CancelOrder command, CancellationToken ct)
    {
        _exchange.Cancel(command);
        return Task.CompletedTask;
    }

    public override Task CancelAllOrdersAsync(CancelAllOrders command, CancellationToken ct)
    {
        _exchange.CancelAll(command);
        return Task.CompletedTask;
    }

    public override Task<ExecutionMassStatus?> GenerateMassStatusAsync(UnixNanos? since, CancellationToken ct) =>
        Task.FromResult<ExecutionMassStatus?>(_exchange.GenerateMassStatus());

    internal void RaiseSubmitted(Order order, UnixNanos ts) => GenerateOrderSubmitted(order.StrategyId, order.InstrumentId, order.ClientOrderId, ts);

    internal void RaiseAccepted(Order order, VenueOrderId venueOrderId, UnixNanos ts) => GenerateOrderAccepted(order.StrategyId, order.InstrumentId, order.ClientOrderId, venueOrderId, ts);

    internal void RaiseRejected(Order order, string reason, UnixNanos ts) => GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, reason, ts);

    internal void RaiseCanceled(Order order, UnixNanos ts) => GenerateOrderCanceled(order.StrategyId, order.InstrumentId, order.ClientOrderId, order.VenueOrderId, ts);

    internal void RaiseExpired(Order order, UnixNanos ts) => GenerateOrderExpired(order.StrategyId, order.InstrumentId, order.ClientOrderId, order.VenueOrderId, ts);

    internal void RaiseTriggered(Order order, UnixNanos ts) => GenerateOrderTriggered(order.StrategyId, order.InstrumentId, order.ClientOrderId, order.VenueOrderId, ts);

    internal void RaiseUpdated(Order order, Quantity quantity, Price? price, Price? triggerPrice, UnixNanos ts) =>
        GenerateOrderUpdated(order.StrategyId, order.InstrumentId, order.ClientOrderId, order.VenueOrderId, quantity, price, triggerPrice, ts);

    internal void RaiseModifyRejected(Order order, string reason, UnixNanos ts) => GenerateOrderModifyRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, order.VenueOrderId, reason, ts);

    internal void RaiseCancelRejected(Order order, string reason, UnixNanos ts) => GenerateOrderCancelRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, order.VenueOrderId, reason, ts);

    internal void RaiseModifyRejectedById(ModifyOrder command, string reason, UnixNanos ts) =>
        GenerateOrderModifyRejected(command.StrategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, reason, ts);

    internal void RaiseCancelRejectedById(CancelOrder command, string reason, UnixNanos ts) =>
        GenerateOrderCancelRejected(command.StrategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, reason, ts);

    internal void RaiseFilled(Order order, TradeId tradeId, Quantity lastQty, Price lastPx, Money commission, LiquiditySide liquiditySide, UnixNanos ts) =>
        GenerateOrderFilled(order.StrategyId, order.InstrumentId, order.ClientOrderId, order.VenueOrderId ?? new VenueOrderId("V-0"), null, tradeId, order.Side, order.Type, lastQty, lastPx,
            _exchange.Instruments[order.InstrumentId].QuoteCurrency, commission, liquiditySide, ts);

    internal void RaiseAccountState(IReadOnlyList<AccountBalance> balances, IReadOnlyList<MarginBalance> margins, bool reported, UnixNanos ts) =>
        GenerateAccountState(balances, margins, reported, ts, leverages: _exchange.Leverages, defaultLeverage: _exchange.DefaultLeverage);
}

/// <summary>
/// Data client for backtests. Subscriptions are no-ops (data is streamed by the engine); historical requests are served from the engine's data store.
/// </summary>
public sealed class BacktestDataClient : DataClientBase
{
    private readonly Func<RequestCommand, IReadOnlyList<IData>> _history;

    public BacktestDataClient(Venue venue, KernelServices services, Func<RequestCommand, IReadOnlyList<IData>> history)
        : base(new ClientId(venue.Value), venue, services)
    {
        _history = history;
    }

    public override Task RequestAsync(RequestCommand command, CancellationToken ct)
    {
        IReadOnlyList<IData> data = _history(command);
        Type dataType = command switch
        {
            RequestBars => typeof(Bar),
            RequestQuoteTicks => typeof(QuoteTick),
            RequestTradeTicks => typeof(TradeTick),
            _ => typeof(IData),
        };
        SendResponse(command, dataType, data);
        return Task.CompletedTask;
    }
}
