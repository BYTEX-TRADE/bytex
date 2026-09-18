using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Timing;

namespace Bytex.Core.Tests.Support;

/// <summary>
/// Execution client that records every command it receives and lets a test play the venue's part by emitting events.
/// </summary>
internal sealed class RecordingExecutionClient : ExecutionClientBase
{
    public RecordingExecutionClient(KernelServices services, Venue venue, OmsType omsType = OmsType.Netting, string? clientId = null, AccountType accountType = AccountType.Cash)
        : base(new ClientId(clientId ?? venue.Value), venue, new AccountId($"{venue}-001"), accountType, null, omsType, services)
    {
    }

    public List<TradingCommand> Commands { get; } = new();

    public bool ThrowOnSubmit { get; set; }

    public IEnumerable<T> Received<T>() where T : TradingCommand => Commands.OfType<T>();

    public override Task SubmitOrderAsync(SubmitOrder command, CancellationToken ct)
    {
        if (ThrowOnSubmit)
        {
            throw new InvalidOperationException("venue unavailable");
        }

        Commands.Add(command);
        return Task.CompletedTask;
    }

    public override Task ModifyOrderAsync(ModifyOrder command, CancellationToken ct)
    {
        Commands.Add(command);
        return Task.CompletedTask;
    }

    public override Task CancelOrderAsync(CancelOrder command, CancellationToken ct)
    {
        Commands.Add(command);
        return Task.CompletedTask;
    }

    public override Task QueryOrderAsync(QueryOrder command, CancellationToken ct)
    {
        Commands.Add(command);
        return Task.CompletedTask;
    }

    public void EmitSubmitted(Order order) => GenerateOrderSubmitted(order.StrategyId, order.InstrumentId, order.ClientOrderId, Clock.Timestamp);

    public void EmitAccepted(Order order, string venueOrderId) =>
        GenerateOrderAccepted(order.StrategyId, order.InstrumentId, order.ClientOrderId, new VenueOrderId(venueOrderId), Clock.Timestamp);

    public void EmitCanceled(Order order) => GenerateOrderCanceled(order.StrategyId, order.InstrumentId, order.ClientOrderId, order.VenueOrderId, Clock.Timestamp);

    public void EmitExpired(Order order) => GenerateOrderExpired(order.StrategyId, order.InstrumentId, order.ClientOrderId, order.VenueOrderId, Clock.Timestamp);

    public void EmitRejected(Order order, string reason) => GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, reason, Clock.Timestamp);

    public void EmitFilled(Order order, string tradeId, string lastQty, string lastPx, string commission = "0") =>
        GenerateOrderFilled(order.StrategyId, order.InstrumentId, order.ClientOrderId, order.VenueOrderId ?? new VenueOrderId("V-" + order.ClientOrderId.Value), null,
            new TradeId(tradeId), order.Side, order.Type, Quantity.Parse(lastQty), Price.Parse(lastPx), Currencies.USDT, Money.Parse($"{commission} USDT"), LiquiditySide.Taker, Clock.Timestamp);

    public void EmitAccountState(params AccountBalance[] balances) => GenerateAccountState(balances, [], true, Clock.Timestamp);

    public void SignalConnected() => NotifyConnected();

    public void SignalDisconnected(string reason) => NotifyDisconnected(reason);
}

/// <summary>
/// Execution client that keeps every default of <see cref="ExecutionClientBase"/> so those defaults can be tested.
/// </summary>
internal sealed class DefaultsExecutionClient : ExecutionClientBase
{
    public DefaultsExecutionClient(KernelServices services, Venue venue)
        : base(new ClientId(venue.Value), venue, new AccountId($"{venue}-001"), AccountType.Cash, null, OmsType.Netting, services)
    {
    }

    public List<SubmitOrder> Submits { get; } = new();

    public List<CancelOrder> Cancels { get; } = new();

    public override Task SubmitOrderAsync(SubmitOrder command, CancellationToken ct)
    {
        Submits.Add(command);
        return Task.CompletedTask;
    }

    public override Task ModifyOrderAsync(ModifyOrder command, CancellationToken ct) => Task.CompletedTask;

    public override Task CancelOrderAsync(CancelOrder command, CancellationToken ct)
    {
        Cancels.Add(command);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Data client that records subscriptions and requests and lets a test push data as the venue would.
/// </summary>
internal sealed class RecordingDataClient : DataClientBase
{
    public RecordingDataClient(KernelServices services, Venue? venue, string? clientId = null)
        : base(new ClientId(clientId ?? venue?.Value ?? "DATA"), venue, services)
    {
    }

    public List<SubscribeCommand> Subscriptions { get; } = new();

    public List<UnsubscribeCommand> Unsubscriptions { get; } = new();

    public List<RequestCommand> Requests { get; } = new();

    /// <summary>When set, requests are answered with this data; otherwise the base class answers "not supported".</summary>
    public IReadOnlyList<IData>? RequestResult { get; set; }

    public override Task SubscribeAsync(SubscribeCommand command, CancellationToken ct)
    {
        Subscriptions.Add(command);
        return Task.CompletedTask;
    }

    public override Task UnsubscribeAsync(UnsubscribeCommand command, CancellationToken ct)
    {
        Unsubscriptions.Add(command);
        return Task.CompletedTask;
    }

    public override Task RequestAsync(RequestCommand command, CancellationToken ct)
    {
        Requests.Add(command);
        if (RequestResult is null)
        {
            return base.RequestAsync(command, ct);
        }

        SendResponse(command, typeof(IData), RequestResult);
        return Task.CompletedTask;
    }

    public void Push(IData data) => HandleData(data);

    public void Push(Instrument instrument) => HandleInstrument(instrument);
}

/// <summary>
/// Collects everything published on a topic pattern, in arrival order.
/// </summary>
internal sealed class BusRecorder
{
    public List<object> Messages { get; } = new();

    public void Handle(object message) => Messages.Add(message);

    public IReadOnlyList<T> Of<T>() => Messages.OfType<T>().ToList();

    public IReadOnlyList<string> TypeNames => Messages.Select(m => m.GetType().Name).ToList();
}

internal static class TestClockExtensions
{
    /// <summary>
    /// Advances the clock and runs the due handlers in the order the clock returned them, as the backtest loop does.
    /// </summary>
    public static IReadOnlyList<TimeEvent> AdvanceAndRun(this TestClock clock, UnixNanos to)
    {
        IReadOnlyList<TimeEventHandler> due = clock.AdvanceTime(to);
        foreach (TimeEventHandler handler in due)
        {
            handler.Handle();
        }

        return due.Select(h => h.Event).ToList();
    }
}
