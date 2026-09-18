using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Model.Reports;

namespace Bytex.Live.Tests.Support;

internal sealed record FakeDataClientConfig : DataClientConfig
{
    public string Label { get; init; } = "default";

    public int Depth { get; init; }
}

internal sealed record FakeExecutionClientConfig : ExecutionClientConfig
{
    public string Label { get; init; } = "default";
}

/// <summary>
/// Shared, thread-safe record of what happened in which order.
/// </summary>
internal sealed class Journal
{
    private readonly List<string> _entries = new();

    public List<string> Raw => _entries;

    public void Add(string entry)
    {
        lock (_entries)
        {
            _entries.Add(entry);
        }
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_entries)
        {
            return _entries.ToList();
        }
    }
}

internal sealed class FakeDataClient : DataClientBase
{
    private readonly Journal _journal;

    public FakeDataClient(ClientId clientId, Venue venue, KernelServices services, Journal journal)
        : base(clientId, venue, services)
    {
        _journal = journal;
    }

    public bool HangOnConnect { get; init; }

    public List<SubscribeCommand> Subscriptions { get; } = new();

    public TaskCompletionSource<SubscribeCommand> FirstSubscription { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override async Task ConnectAsync(CancellationToken ct)
    {
        _journal.Add("data.connect");
        if (HangOnConnect)
        {
            await Task.Delay(Timeout.Infinite, ct);
        }

        NotifyConnected();
    }

    public override Task DisconnectAsync(CancellationToken ct)
    {
        _journal.Add("data.disconnect");
        NotifyDisconnected("disconnect requested");
        return Task.CompletedTask;
    }

    public override Task SubscribeAsync(SubscribeCommand command, CancellationToken ct)
    {
        Subscriptions.Add(command);
        FirstSubscription.TrySetResult(command);
        return Task.CompletedTask;
    }

    /// <summary>Emits data the way an adapter does: from whatever thread the caller is on.</summary>
    public void Emit(IData data) => HandleData(data);
}

internal sealed class FakeExecutionClient : ExecutionClientBase
{
    private readonly Journal _journal;
    private int _venueOrderIds;
    private int _tradeIds;

    public FakeExecutionClient(ClientId clientId, Venue venue, KernelServices services, Journal journal)
        : base(clientId, venue, new AccountId($"{venue}-001"), AccountType.Cash, null, OmsType.Netting, services)
    {
        _journal = journal;
    }

    /// <summary>Price at which market orders fill; null leaves them accepted and open.</summary>
    public decimal? MarketFillPrice { get; set; }

    public ExecutionMassStatus? MassStatus { get; set; }

    public Exception? MassStatusFailure { get; set; }

    public List<UnixNanos?> MassStatusSince { get; } = new();

    public List<SubmitOrder> Submitted { get; } = new();

    public List<CancelAllOrders> CancelAlls { get; } = new();

    public bool HangOnConnect { get; set; }

    public override async Task ConnectAsync(CancellationToken ct)
    {
        _journal.Add("exec.connect");
        if (HangOnConnect)
        {
            await Task.Delay(Timeout.Infinite, ct);
        }

        GenerateAccountState(
            [Core.Model.Events.AccountBalance.Unlocked(new Money(1_000_000m, Currencies.USDT)), Core.Model.Events.AccountBalance.Unlocked(new Money(10m, Currencies.BTC))],
            [], reported: true, Clock.Timestamp);
        NotifyConnected();
    }

    public override Task DisconnectAsync(CancellationToken ct)
    {
        _journal.Add("exec.disconnect");
        NotifyDisconnected("disconnect requested");
        return Task.CompletedTask;
    }

    public override Task SubmitOrderAsync(SubmitOrder command, CancellationToken ct)
    {
        Order order = command.Order;
        lock (Submitted)
        {
            Submitted.Add(command);
        }

        _journal.Add($"exec.submit {order.Type} {order.Side} {order.Quantity}");
        VenueOrderId venueOrderId = new($"FAKE-{Interlocked.Increment(ref _venueOrderIds)}");
        GenerateOrderSubmitted(order.StrategyId, order.InstrumentId, order.ClientOrderId, Clock.Timestamp);
        GenerateOrderAccepted(order.StrategyId, order.InstrumentId, order.ClientOrderId, venueOrderId, Clock.Timestamp);
        if (order.Type == OrderType.Market && MarketFillPrice is { } px && Services.Cache.Instrument(order.InstrumentId) is { } instrument)
        {
            GenerateOrderFilled(order.StrategyId, order.InstrumentId, order.ClientOrderId, venueOrderId, null, new TradeId($"T-{Interlocked.Increment(ref _tradeIds)}"), order.Side, order.Type,
                order.Quantity, instrument.MakePrice(px), instrument.QuoteCurrency, Money.Zero(instrument.QuoteCurrency), LiquiditySide.Taker, Clock.Timestamp);
        }

        return Task.CompletedTask;
    }

    public override Task ModifyOrderAsync(ModifyOrder command, CancellationToken ct) => Task.CompletedTask;

    public override Task CancelOrderAsync(CancelOrder command, CancellationToken ct)
    {
        GenerateOrderCanceled(command.StrategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, Clock.Timestamp);
        return Task.CompletedTask;
    }

    public override Task CancelAllOrdersAsync(CancelAllOrders command, CancellationToken ct)
    {
        lock (CancelAlls)
        {
            CancelAlls.Add(command);
        }

        _journal.Add($"exec.cancel-all {command.InstrumentId}");
        return base.CancelAllOrdersAsync(command, ct);
    }

    public override Task<ExecutionMassStatus?> GenerateMassStatusAsync(UnixNanos? since, CancellationToken ct)
    {
        _journal.Add("exec.mass-status");
        MassStatusSince.Add(since);
        if (MassStatusFailure is not null)
        {
            throw MassStatusFailure;
        }

        return Task.FromResult(MassStatus);
    }
}

internal sealed class FakeDataClientFactory : IDataClientFactory
{
    private readonly Journal _journal;
    private readonly Venue _venue;

    public FakeDataClientFactory(Journal journal, string venue = "FAKE")
    {
        _journal = journal;
        _venue = new Venue(venue);
    }

    public string Name => "FAKE";

    public Type ConfigType => typeof(FakeDataClientConfig);

    public bool HangOnConnect { get; init; }

    public List<(ClientId ClientId, FakeDataClientConfig Config, FakeDataClient Client)> Created { get; } = new();

    public IDataClient Create(ClientId clientId, DataClientConfig config, KernelServices services)
    {
        FakeDataClient client = new(clientId, _venue, services, _journal) { HangOnConnect = HangOnConnect };
        Created.Add((clientId, (FakeDataClientConfig)config, client));
        return client;
    }
}

internal sealed class FakeExecutionClientFactory : IExecutionClientFactory
{
    private readonly Journal _journal;
    private readonly Venue _venue;

    public FakeExecutionClientFactory(Journal journal, string venue = "FAKE")
    {
        _journal = journal;
        _venue = new Venue(venue);
    }

    public string Name => "FAKE";

    public Type ConfigType => typeof(FakeExecutionClientConfig);

    public Action<FakeExecutionClient>? Configure { get; init; }

    public List<(ClientId ClientId, FakeExecutionClientConfig Config, FakeExecutionClient Client)> Created { get; } = new();

    public IExecutionClient Create(ClientId clientId, ExecutionClientConfig config, KernelServices services)
    {
        FakeExecutionClient client = new(clientId, _venue, services, _journal);
        Configure?.Invoke(client);
        Created.Add((clientId, (FakeExecutionClientConfig)config, client));
        return client;
    }
}
