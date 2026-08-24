using Bytex.Backtest;
using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Model.Reports;

namespace Bytex.Live.Sandbox;

public sealed record SandboxExecutionClientConfig : ExecutionClientConfig
{
    public required string Venue { get; init; }

    public OmsType OmsType { get; init; } = OmsType.Netting;

    public AccountType AccountType { get; init; } = AccountType.Cash;

    public string? BaseCurrency { get; init; }

    /// <summary>Starting balances as "amount CURRENCY".</summary>
    public IReadOnlyList<string> StartingBalances { get; init; } = [];

    public decimal DefaultLeverage { get; init; } = 1m;

    public BarExecutionMode BarExecution { get; init; } = BarExecutionMode.OhlcPath;

    public decimal ProbFillOnLimit { get; init; } = 1m;

    public decimal ProbSlippage { get; init; }

    public TimeSpan Latency { get; init; } = TimeSpan.Zero;
}

/// <summary>
/// Executes orders against a simulated venue fed by the live market data flowing through the kernel.
/// Data clients for the same venue provide the prices; this client provides the fills.
/// </summary>
public sealed class SandboxExecutionClient : ExecutionClientBase
{
    private readonly SimulatedExchange _exchange;
    private readonly BacktestExecutionClient _inner;
    private readonly List<(string Topic, Action<object> Handler)> _subscriptions = new();
    private bool _accountInitialized;

    public SandboxExecutionClient(ClientId clientId, SandboxExecutionClientConfig config, KernelServices services)
        : base(clientId, new Venue(config.Venue), new AccountId($"{config.Venue}-SANDBOX"), config.AccountType,
            config.BaseCurrency is null ? null : Currency.FromCode(config.BaseCurrency), config.OmsType, services)
    {
        ArgumentNullException.ThrowIfNull(config);
        SimulatedVenueConfig venueConfig = new()
        {
            Venue = new Venue(config.Venue),
            OmsType = config.OmsType,
            AccountType = config.AccountType,
            BaseCurrency = config.BaseCurrency is null ? null : Currency.FromCode(config.BaseCurrency),
            StartingBalances = config.StartingBalances.Select(Money.Parse).ToList(),
            DefaultLeverage = config.DefaultLeverage,
            BarExecution = config.BarExecution,
            FillModel = new FillModel(config.ProbFillOnLimit, 1m, config.ProbSlippage),
            LatencyModel = config.Latency == TimeSpan.Zero ? LatencyModel.Zero : LatencyModel.Uniform(config.Latency),
        };
        _exchange = new SimulatedExchange(venueConfig, services);
        _inner = new BacktestExecutionClient(_exchange, services);
    }

    public SimulatedExchange Exchange => _exchange;

    public override Task ConnectAsync(CancellationToken ct)
    {
        // Relay the inner client's events through this client's sink.
        _inner.AttachSink(new RelaySink(this));

        foreach (Instrument instrument in Services.Cache.Instruments(Venue))
        {
            _exchange.AddInstrument(instrument);
        }

        Subscribe($"data.quotes.{Venue}.*", m => OnMarketData((IData)m));
        Subscribe($"data.trades.{Venue}.*", m => OnMarketData((IData)m));
        Subscribe($"data.bars.*.{Venue}-*", m => OnMarketData((IData)m));
        Subscribe($"data.instrument.{Venue}.*", m => _exchange.AddInstrument((Instrument)m));

        if (!_accountInitialized)
        {
            _exchange.InitializeAccount();
            _accountInitialized = true;
        }

        NotifyConnected();
        return Task.CompletedTask;
    }

    private void Subscribe(string topic, Action<object> handler)
    {
        Services.MessageBus.Subscribe(topic, handler, priority: 10);
        _subscriptions.Add((topic, handler));
    }

    private void OnMarketData(IData data)
    {
        switch (data)
        {
            case QuoteTick quote:
                _exchange.ProcessQuoteTick(quote);
                break;
            case TradeTick trade:
                _exchange.ProcessTradeTick(trade);
                break;
            case Bar bar:
                _exchange.ProcessBar(bar);
                break;
        }
    }

    public override Task DisconnectAsync(CancellationToken ct)
    {
        foreach ((string topic, Action<object> handler) in _subscriptions)
        {
            Services.MessageBus.Unsubscribe(topic, handler);
        }

        _subscriptions.Clear();
        NotifyDisconnected("disconnect requested");
        return Task.CompletedTask;
    }

    public override Task SubmitOrderAsync(SubmitOrder command, CancellationToken ct) => _inner.SubmitOrderAsync(command, ct);

    public override Task SubmitOrderListAsync(SubmitOrderList command, CancellationToken ct) => _inner.SubmitOrderListAsync(command, ct);

    public override Task ModifyOrderAsync(ModifyOrder command, CancellationToken ct) => _inner.ModifyOrderAsync(command, ct);

    public override Task CancelOrderAsync(CancelOrder command, CancellationToken ct) => _inner.CancelOrderAsync(command, ct);

    public override Task CancelAllOrdersAsync(CancelAllOrders command, CancellationToken ct) => _inner.CancelAllOrdersAsync(command, ct);

    public override Task<ExecutionMassStatus?> GenerateMassStatusAsync(UnixNanos? since, CancellationToken ct) => _inner.GenerateMassStatusAsync(since, ct);

    private sealed class RelaySink : IExecutionClientSink
    {
        private readonly SandboxExecutionClient _owner;

        public RelaySink(SandboxExecutionClient owner) => _owner = owner;

        public void OnOrderEvent(Core.Model.Events.OrderEvent e) => _owner.Sink.OnOrderEvent(e with { AccountId = _owner.AccountId });

        public void OnAccountState(Core.Model.Events.AccountState state) => _owner.Sink.OnAccountState(state with { AccountId = _owner.AccountId });

        public void OnConnected(ClientId clientId)
        {
        }

        public void OnDisconnected(ClientId clientId, string reason)
        {
        }
    }
}

public sealed class SandboxExecutionClientFactory : IExecutionClientFactory
{
    public string Name => "SANDBOX";

    public Type ConfigType => typeof(SandboxExecutionClientConfig);

    public IExecutionClient Create(ClientId clientId, ExecutionClientConfig config, KernelServices services) =>
        new SandboxExecutionClient(clientId, (SandboxExecutionClientConfig)config, services);
}
