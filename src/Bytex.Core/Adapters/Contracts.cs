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
/// TradingRuntime-owned services handed to clients and providers at construction.
/// </summary>
public sealed record TradingRuntimeServices(
    IClock Clock,
    ICache Cache,
    IMessageBus MessageBus,
    ILoggerFactory Logging,
    ModuleHostId ModuleHostId,
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

    Task<OrderStatusReport?> GenerateOrderStatusReportAsync(MarketKey marketKey, ClientOrderId? clientOrderId, VenueOrderId? venueOrderId, CancellationToken ct);

    Task<IReadOnlyList<OrderStatusReport>> GenerateOrderStatusReportsAsync(MarketKey? marketKey, UnixNanos? start, UnixNanos? end, bool openOnly, CancellationToken ct);

    Task<IReadOnlyList<FillReport>> GenerateFillReportsAsync(MarketKey? marketKey, VenueOrderId? venueOrderId, UnixNanos? start, UnixNanos? end, CancellationToken ct);

    Task<IReadOnlyList<PositionStatusReport>> GeneratePositionStatusReportsAsync(MarketKey? marketKey, UnixNanos? start, UnixNanos? end, CancellationToken ct);
}

/// <summary>
/// Loads instrument definitions from a venue.
/// </summary>
/// <summary>
/// One row of a venue's own listing: what the venue calls a market, and what that market is. Deliberately NOT an
/// <see cref="Instrument"/>.
///
/// <para>
/// A host that is resolving a NAME - "the perpetual on ETH against USDT, whatever this venue calls it" - needs the
/// venue's spelling for every market it lists and full detail for the one it finds. Getting detail for all of them is
/// what made that expensive: on OKX the margin tiers of 477 contracts cost about ninety-six extra requests, and 476
/// of those answers were discarded.
/// </para>
///
/// <para>
/// The row is its own type rather than a half-filled instrument, and that is the point. An instrument missing its
/// margin requirement can be traded, and a position sized against no venue requirement at all looks exactly like a
/// position. Nothing can trade one of these, so the cheap path cannot become a quiet way to trade an under-specified
/// market: a host reads the listing, picks a row, and loads THAT instrument properly.
/// </para>
/// </summary>
/// <param name="Id">The engine's id for this market, which is what to load it by.</param>
/// <param name="RawSymbol">The venue's own name for it, verbatim.</param>
/// <param name="InstrumentClass">Spot, swap, future or option, as the venue states it.</param>
/// <param name="BaseCurrency">What is being traded; null where the venue lists none, as on some derivatives.</param>
/// <param name="QuoteCurrency">What it is priced in.</param>
/// <param name="SettlementCurrency">What it settles in, which is what tells a coin-settled contract from its sibling.</param>
/// <param name="IsInverse">Whether it settles in what it is written on, the other half of that same distinction.</param>
public sealed record InstrumentListing(
    MarketKey Id,
    Symbol RawSymbol,
    InstrumentClass InstrumentClass,
    Currency? BaseCurrency,
    Currency QuoteCurrency,
    Currency SettlementCurrency,
    bool IsInverse)
{
    /// <summary>The row an instrument would be, for a provider that has no cheaper way to answer than loading one.</summary>
    public static InstrumentListing Of(Instrument instrument)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        return new InstrumentListing(
            instrument.Id,
            instrument.RawSymbol,
            instrument.InstrumentClass,
            instrument.BaseCurrency,
            instrument.QuoteCurrency,
            instrument.SettlementCurrency,
            instrument.IsInverse);
    }
}

public interface IInstrumentProvider
{
    Venue Venue { get; }

    int Count { get; }

    Task LoadAllAsync(CancellationToken ct, IReadOnlyDictionary<string, string>? filters = null);

    Task LoadIdsAsync(IReadOnlyList<MarketKey> ids, CancellationToken ct);

    Task LoadAsync(MarketKey id, CancellationToken ct);

    Instrument? Find(MarketKey id);

    IReadOnlyList<Instrument> GetAll();

    IReadOnlyDictionary<string, Currency> Currencies { get; }

    /// <summary>
    /// What this venue lists, as names rather than instruments: see <see cref="InstrumentListing"/> for why the
    /// distinction is the whole point.
    ///
    /// <para>
    /// The default answer loads everything and describes what it loaded, which is correct for every provider and
    /// cheap for none. A provider whose listing endpoint already carries the names - which is all of them, since a
    /// venue answers with its whole contract list either way - overrides this to skip whatever it fetches AFTER that
    /// list. What it must not do is publish those rows as instruments: nothing here reaches <c>Find</c> or
    /// <c>GetAll</c>, so a market described by a row still has to be loaded before it can be traded.
    /// </para>
    /// </summary>
    async Task<IReadOnlyList<InstrumentListing>> ListAsync(CancellationToken ct)
    {
        await LoadAllAsync(ct).ConfigureAwait(false);
        return [.. GetAll().Select(InstrumentListing.Of)];
    }
}

public record InstrumentProviderConfig
{
    public bool LoadAll { get; init; }

    public IReadOnlyList<MarketKey> LoadIds { get; init; } = [];

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

    IDataClient Create(ClientId clientId, DataClientConfig config, TradingRuntimeServices services);
}

public interface IExecutionClientFactory
{
    string Name { get; }

    Type ConfigType { get; }

    IExecutionClient Create(ClientId clientId, ExecutionClientConfig config, TradingRuntimeServices services);
}

/// <summary>
/// In-memory instrument provider that can be populated by adapters or tests.
/// </summary>
public class InstrumentProviderBase : IInstrumentProvider
{
    private readonly Dictionary<MarketKey, Instrument> _instruments = new();
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

    public virtual async Task LoadIdsAsync(IReadOnlyList<MarketKey> ids, CancellationToken ct)
    {
        foreach (MarketKey id in ids)
        {
            await LoadAsync(id, ct).ConfigureAwait(false);
        }
    }

    public virtual Task LoadAsync(MarketKey id, CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc cref="IInstrumentProvider.ListAsync"/>
    public virtual async Task<IReadOnlyList<InstrumentListing>> ListAsync(CancellationToken ct)
    {
        await LoadAllAsync(ct).ConfigureAwait(false);
        return [.. GetAll().Select(InstrumentListing.Of)];
    }

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

    public Instrument? Find(MarketKey id) => _instruments.GetValueOrDefault(id);

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
