using Bytex.Core.Caching;
using Bytex.Core.Common;
using Bytex.Core.Messaging;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Model.Reports;
using Bytex.Core.Timing;
using Microsoft.Extensions.Logging;

namespace Bytex.Core.Adapters;

/// <summary>
/// Kernel-owned services handed to clients and providers at construction.
/// </summary>
public sealed record KernelServices(
    IClock Clock,
    ICache Cache,
    IMessageBus MessageBus,
    ILoggerFactory Logging,
    TraderId TraderId,
    TradingEnvironment Environment);

/// <summary>
/// Receives everything a data client produces. Implemented by the data engine.
/// </summary>
public interface IDataClientSink
{
    void OnData(IData data);

    void OnInstrument(Instrument instrument);

    void OnResponse(DataResponse response);

    void OnConnected(ClientId clientId);

    void OnDisconnected(ClientId clientId, string reason);

    void OnSubscriptionFailed(ClientId clientId, SubscribeCommand command, string reason);
}

/// <summary>
/// Receives everything an execution client produces. Implemented by the execution engine.
/// </summary>
public interface IExecutionClientSink
{
    void OnOrderEvent(OrderEvent e);

    void OnAccountState(AccountState state);

    void OnConnected(ClientId clientId);

    void OnDisconnected(ClientId clientId, string reason);
}

/// <summary>
/// Market data source for one venue or provider.
/// </summary>
public interface IDataClient : IComponent
{
    ClientId ClientId { get; }

    Venue? Venue { get; }

    bool IsConnected { get; }

    void AttachSink(IDataClientSink sink);

    Task ConnectAsync(CancellationToken ct);

    Task DisconnectAsync(CancellationToken ct);

    Task SubscribeAsync(SubscribeCommand command, CancellationToken ct);

    Task UnsubscribeAsync(UnsubscribeCommand command, CancellationToken ct);

    Task RequestAsync(RequestCommand command, CancellationToken ct);
}

/// <summary>
/// Order routing and execution reporting for one venue account.
/// </summary>
public interface IExecutionClient : IComponent
{
    ClientId ClientId { get; }

    Venue Venue { get; }

    AccountId AccountId { get; }

    AccountType AccountType { get; }

    Currency? BaseCurrency { get; }

    OmsType OmsType { get; }

    bool IsConnected { get; }

    void AttachSink(IExecutionClientSink sink);

    Task ConnectAsync(CancellationToken ct);

    Task DisconnectAsync(CancellationToken ct);

    Task SubmitOrderAsync(SubmitOrder command, CancellationToken ct);

    Task SubmitOrderListAsync(SubmitOrderList command, CancellationToken ct);

    Task ModifyOrderAsync(ModifyOrder command, CancellationToken ct);

    Task CancelOrderAsync(CancelOrder command, CancellationToken ct);

    Task CancelAllOrdersAsync(CancelAllOrders command, CancellationToken ct);

    Task BatchCancelOrdersAsync(BatchCancelOrders command, CancellationToken ct);

    Task QueryOrderAsync(QueryOrder command, CancellationToken ct);

    Task<ExecutionMassStatus?> GenerateMassStatusAsync(UnixNanos? since, CancellationToken ct);

    Task<OrderStatusReport?> GenerateOrderStatusReportAsync(InstrumentId instrumentId, ClientOrderId? clientOrderId, VenueOrderId? venueOrderId, CancellationToken ct);

    Task<IReadOnlyList<OrderStatusReport>> GenerateOrderStatusReportsAsync(InstrumentId? instrumentId, UnixNanos? start, UnixNanos? end, bool openOnly, CancellationToken ct);

    Task<IReadOnlyList<FillReport>> GenerateFillReportsAsync(InstrumentId? instrumentId, VenueOrderId? venueOrderId, UnixNanos? start, UnixNanos? end, CancellationToken ct);

    Task<IReadOnlyList<PositionStatusReport>> GeneratePositionStatusReportsAsync(InstrumentId? instrumentId, UnixNanos? start, UnixNanos? end, CancellationToken ct);
}

/// <summary>
/// Loads instrument definitions from a venue.
/// </summary>
public interface IInstrumentProvider
{
    Venue Venue { get; }

    int Count { get; }

    Task LoadAllAsync(CancellationToken ct, IReadOnlyDictionary<string, string>? filters = null);

    Task LoadIdsAsync(IReadOnlyList<InstrumentId> ids, CancellationToken ct);

    Task LoadAsync(InstrumentId id, CancellationToken ct);

    Instrument? Find(InstrumentId id);

    IReadOnlyList<Instrument> GetAll();

    IReadOnlyDictionary<string, Currency> Currencies { get; }
}

public record InstrumentProviderConfig
{
    public bool LoadAll { get; init; }

    public IReadOnlyList<InstrumentId> LoadIds { get; init; } = [];

    public IReadOnlyDictionary<string, string> Filters { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public bool LogWarnings { get; init; } = true;
}

public abstract record DataClientConfig
{
    public InstrumentProviderConfig InstrumentProvider { get; init; } = new();

    /// <summary>Whether bar updates flagged as revisions should be forwarded.</summary>
    public bool HandleRevisedBars { get; init; }
}

public abstract record ExecutionClientConfig
{
    public InstrumentProviderConfig InstrumentProvider { get; init; } = new();

    /// <summary>
    /// A broker or partner id to carry on the orders this client sends, or null to send them untagged. HOW it is
    /// carried is the venue's own business and differs on every one - a prefix on the client order id, a header, a
    /// field of the order - which is why the mechanism is declared per venue in
    /// <see cref="VenueDescriptor.BrokerTag"/> and only the id is configured here.
    /// <para>
    /// A venue with no programme an adapter can carry an id for declares <see cref="BrokerTag.None"/> and ignores
    /// this, so setting it is never an error - it is a no-op there. Nothing above an adapter has to know which
    /// venues have a programme.
    /// </para>
    /// <para>
    /// Untagged is the default and must stay byte-for-byte what the venue received before the field existed, or
    /// turning it on would change how every order is placed for everybody who does not use it.
    /// </para>
    /// </summary>
    public string? BrokerId { get; init; }

    /// <summary>
    /// The leverage this client trades at, or null to leave whatever the venue already has. One field on every
    /// venue, because a host setting what a strategy was written for should not have to know which venues take
    /// leverage per order and which hold it as account state - that is the adapter's business.
    /// <para>
    /// The two shapes it has to cover: KuCoin's perpetual futures demand a leverage on every order and have no
    /// default of their own, while Binance and Bybit hold it per symbol on the account and ignore anything sent with
    /// an order. An adapter of the second kind sets it at the venue when it connects, so a strategy written for 3x
    /// is traded at 3x rather than at whatever the account happened to be left on - which is the failure this
    /// closes: a strategy backtested and papered at 3x went live at 1x, and nothing said so.
    /// </para>
    /// <para>
    /// Null means "do not touch it", which is what every configuration written before this field existed means.
    /// </para>
    /// <para>
    /// A decimal, because a whole number would be this engine's choice rather than the venues'. A strategy document
    /// carries leverage as a decimal and so does the simulated venue, so an integer here would mean rounding on the
    /// way into live - and whoever rounds has to pick a direction silently, which makes a live position a different
    /// size from the backtested one with nothing to say so. Where a venue genuinely takes only whole numbers its
    /// adapter refuses a fraction when the client is built; where it takes the number as written, it is passed
    /// through and the venue speaks for itself.
    /// </para>
    /// </summary>
    public decimal? Leverage { get; init; }
}

public interface IDataClientFactory
{
    string Name { get; }

    Type ConfigType { get; }

    IDataClient Create(ClientId clientId, DataClientConfig config, KernelServices services);
}

public interface IExecutionClientFactory
{
    string Name { get; }

    Type ConfigType { get; }

    IExecutionClient Create(ClientId clientId, ExecutionClientConfig config, KernelServices services);
}

/// <summary>
/// In-memory instrument provider that can be populated by adapters or tests.
/// </summary>
public class InstrumentProviderBase : IInstrumentProvider
{
    private readonly Dictionary<InstrumentId, Instrument> _instruments = new();
    private readonly Dictionary<string, Currency> _currencies = new(StringComparer.Ordinal);

    public InstrumentProviderBase(Venue venue, InstrumentProviderConfig? config = null, ILogger? logger = null)
    {
        Venue = venue;
        Config = config ?? new InstrumentProviderConfig();
        Log = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    public Venue Venue { get; }

    public InstrumentProviderConfig Config { get; }

    protected ILogger Log { get; }

    public int Count => _instruments.Count;

    public IReadOnlyDictionary<string, Currency> Currencies => _currencies;

    public virtual Task LoadAllAsync(CancellationToken ct, IReadOnlyDictionary<string, string>? filters = null) => Task.CompletedTask;

    public virtual async Task LoadIdsAsync(IReadOnlyList<InstrumentId> ids, CancellationToken ct)
    {
        foreach (InstrumentId id in ids)
        {
            await LoadAsync(id, ct).ConfigureAwait(false);
        }
    }

    public virtual Task LoadAsync(InstrumentId id, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Loads according to the provider configuration: everything, a list of ids, or nothing.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        if (Config.LoadAll)
        {
            await LoadAllAsync(ct, Config.Filters).ConfigureAwait(false);
        }
        else if (Config.LoadIds.Count > 0)
        {
            await LoadIdsAsync(Config.LoadIds, ct).ConfigureAwait(false);
        }
    }

    public Instrument? Find(InstrumentId id) => _instruments.GetValueOrDefault(id);

    public IReadOnlyList<Instrument> GetAll() => _instruments.Values.ToList();

    public void Add(Instrument instrument)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        _instruments[instrument.Id] = instrument;
        AddCurrency(instrument.QuoteCurrency);
        if (instrument.BaseCurrency is not null)
        {
            AddCurrency(instrument.BaseCurrency);
        }

        AddCurrency(instrument.SettlementCurrency);
    }

    public void AddCurrency(Currency currency)
    {
        ArgumentNullException.ThrowIfNull(currency);
        _currencies[currency.Code] = currency;
        Currency.Register(currency);
    }
}
