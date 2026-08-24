# 0005 — Adapter SDK contract

Status: accepted (frozen for 0.x)

## Problem

Every venue and data provider has its own API, authentication, symbol
conventions, order semantics, and failure modes. The engine must be able to
treat all of them uniformly, and third parties must be able to add a venue
without touching engine code.

## Decision

An integration is a set of up to three components plus factories and
configuration. All three implement `Component` (see 0007) and are owned by
the engine that registers them.

```
IInstrumentProvider    loads and caches instrument definitions from the venue
IDataClient            market data: subscriptions and historical requests
IExecutionClient       trading: commands out, execution events in, reconciliation
```

### Data client

```csharp
public interface IDataClient : IComponent
{
    ClientId ClientId { get; }                // Component.Id is the ComponentId
    Venue? Venue { get; }                     // null for multi-venue providers (e.g. a data vendor)
    bool IsConnected { get; }
    void AttachSink(IDataClientSink sink);   // called by the engine (and re-called by the live node to dispatch onto the kernel thread)

    Task ConnectAsync(CancellationToken ct);
    Task DisconnectAsync(CancellationToken ct);

    // Subscriptions — each command carries the parameters and a correlation id.
    Task SubscribeAsync(SubscribeCommand command, CancellationToken ct);
    Task UnsubscribeAsync(UnsubscribeCommand command, CancellationToken ct);

    // Historical requests — results are delivered through the sink as DataResponse messages.
    Task RequestAsync(RequestCommand command, CancellationToken ct);
}

// Command families (sealed records in Bytex.Core.Adapters.Commands)
SubscribeInstruments(Venue) SubscribeInstrument(InstrumentId) SubscribeQuoteTicks(InstrumentId)
SubscribeTradeTicks(InstrumentId) SubscribeBars(BarType) SubscribeOrderBookDeltas(InstrumentId, BookType, Depth)
SubscribeOrderBookSnapshots(InstrumentId, BookType, Depth, Interval) SubscribeInstrumentStatus(InstrumentId)
SubscribeMarkPrices / IndexPrices / FundingRates(InstrumentId) SubscribeData(DataType)
Unsubscribe… (mirror)
RequestInstrument(InstrumentId) RequestInstruments(Venue) RequestQuoteTicks / TradeTicks(InstrumentId, Start, End, Limit)
RequestBars(BarType, Start, End, Limit) RequestData(DataType, Start, End, Limit)
```

The client delivers everything through a sink the engine injects at
registration:

```csharp
public interface IDataClientSink
{
    void OnData(IData data);                            // any market data element
    void OnInstrument(Instrument instrument);
    void OnResponse(DataResponse response);             // historical request results
    void OnConnected(ClientId id); void OnDisconnected(ClientId id, string reason);
    void OnSubscriptionFailed(ClientId id, SubscribeCommand command, string reason);
}
```

`OnData` may be called from any thread; the sink marshals onto the kernel
thread. Clients must deliver elements in the order they were received from
the venue.

### Execution client

```csharp
public interface IExecutionClient : IComponent
{
    ClientId ClientId { get; }
    Venue Venue { get; }
    AccountId AccountId { get; }
    AccountType AccountType { get; }
    Currency? BaseCurrency { get; }           // null for multi-currency accounts
    OmsType OmsType { get; }
    bool IsConnected { get; }
    void AttachSink(IExecutionClientSink sink);

    Task ConnectAsync(CancellationToken ct);
    Task DisconnectAsync(CancellationToken ct);

    // Commands (fire-and-forget from the engine's perspective; outcomes arrive as events)
    Task SubmitOrderAsync(SubmitOrder command, CancellationToken ct);
    Task SubmitOrderListAsync(SubmitOrderList command, CancellationToken ct);
    Task ModifyOrderAsync(ModifyOrder command, CancellationToken ct);
    Task CancelOrderAsync(CancelOrder command, CancellationToken ct);
    Task CancelAllOrdersAsync(CancelAllOrders command, CancellationToken ct);
    Task BatchCancelOrdersAsync(BatchCancelOrders command, CancellationToken ct);
    Task QueryOrderAsync(QueryOrder command, CancellationToken ct);

    // Reconciliation
    Task<ExecutionMassStatus> GenerateMassStatusAsync(UnixNanos? since, CancellationToken ct);
    Task<OrderStatusReport?> GenerateOrderStatusReportAsync(InstrumentId id, ClientOrderId? clientOrderId, VenueOrderId? venueOrderId, CancellationToken ct);
    Task<IReadOnlyList<OrderStatusReport>> GenerateOrderStatusReportsAsync(InstrumentId? id, UnixNanos? start, UnixNanos? end, bool openOnly, CancellationToken ct);
    Task<IReadOnlyList<FillReport>> GenerateFillReportsAsync(InstrumentId? id, VenueOrderId? venueOrderId, UnixNanos? start, UnixNanos? end, CancellationToken ct);
    Task<IReadOnlyList<PositionStatusReport>> GeneratePositionStatusReportsAsync(InstrumentId? id, UnixNanos? start, UnixNanos? end, CancellationToken ct);
}

public interface IExecutionClientSink
{
    void OnOrderEvent(OrderEvent e);
    void OnAccountState(AccountState state);
    void OnConnected(ClientId id); void OnDisconnected(ClientId id, string reason);
}
```

`ExecutionClientBase` (in `Bytex.Core.Adapters`) implements the sink plumbing
and provides event-construction helpers so adapters produce consistent
events:

```csharp
protected void GenerateOrderSubmitted(StrategyId, InstrumentId, ClientOrderId, UnixNanos tsEvent);
protected void GenerateOrderAccepted(StrategyId, InstrumentId, ClientOrderId, VenueOrderId, UnixNanos tsEvent);
protected void GenerateOrderRejected(..., string reason, ...);
protected void GenerateOrderCanceled / Expired / Triggered / Updated / ModifyRejected / CancelRejected(...);
protected void GenerateOrderFilled(StrategyId, InstrumentId, ClientOrderId, VenueOrderId, PositionId?, TradeId,
                                   OrderSide, OrderType, Quantity lastQty, Price lastPx, Currency quoteCurrency,
                                   Money commission, LiquiditySide, UnixNanos tsEvent);
protected void GenerateAccountState(IReadOnlyList<AccountBalance>, IReadOnlyList<MarginBalance>, bool reported, UnixNanos tsEvent);
```

### Instrument provider

```csharp
public interface IInstrumentProvider
{
    Venue Venue { get; }
    Task LoadAllAsync(CancellationToken ct, IReadOnlyDictionary<string,string>? filters = null);
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
    public IReadOnlyDictionary<string, string> Filters { get; init; } = empty;
    public bool LogWarnings { get; init; } = true;
}
```

### Factories and configuration

Integrations register factories so nodes can build clients from
configuration:

```csharp
public interface IDataClientFactory
{
    string Name { get; }                                         // "BINANCE", "BYBIT", "TARDIS"
    Type ConfigType { get; }                                     // lets nodes deserialise JSON into the right config
    IDataClient Create(ClientId id, DataClientConfig config, KernelServices services);
}
public interface IExecutionClientFactory
{
    string Name { get; }
    Type ConfigType { get; }
    IExecutionClient Create(ClientId id, ExecutionClientConfig config, KernelServices services);
}

public abstract record DataClientConfig { InstrumentProviderConfig InstrumentProvider; bool HandleRevisedBars; ... }
public abstract record ExecutionClientConfig { InstrumentProviderConfig InstrumentProvider; ... }

// KernelServices: the kernel-owned objects a client needs
public sealed record KernelServices(IClock Clock, ICache Cache, IMessageBus MessageBus, ILoggerFactory Logging,
                                    TraderId TraderId, Environment Environment);
```

Venue-specific configs derive from the base records (for example
`BinanceDataClientConfig { ApiKey, ApiSecret, AccountType (Spot|UsdMFutures), Testnet, BaseUrlHttp, BaseUrlWs }`).
Secrets are resolved from environment variables when the config value is
`null`; adapters document the variable names they read.

### Network infrastructure (`Bytex.Live.Network`)

Adapters are not required to use it, but the built-in ones do:

- `WebSocketClient` — connect, auto-reconnect with backoff, ping/pong, text
  and binary handlers, resubscription callback on reconnect.
- `HttpClientWrapper` — base URL, default headers, JSON send/receive,
  `RateLimiter` (token bucket per route), `RetryPolicy` with jitter.
- `HmacSigner` — HMAC-SHA256 / SHA512 helpers for request signing.
- `ReconnectionSupervisor` — drives reconnect and resubscription for a client.

### Reconciliation protocol

On startup a live node calls `GenerateMassStatusAsync(since)` on every
execution client. The execution engine then:

1. Adds any venue order it has no record of as an external order (claimed by
   a strategy if the instrument is in its `ExternalOrderClaims`, otherwise
   owned by the engine's `EXTERNAL` strategy id).
2. Replays missing fills and status changes on known orders.
3. Reconciles positions against `PositionStatusReport`s and logs any
   mismatch it cannot resolve.

Adapters must be able to answer mass status from REST endpoints alone; the
private WebSocket is used only for steady-state updates.

### Adapter responsibilities checklist

| Area | Adapter must |
|---|---|
| Symbols | map venue symbols to `InstrumentId` and back; never leak venue strings to the engine |
| Precision | build instruments with correct `PriceIncrement` / `SizeIncrement` and fees |
| Order types | reject unsupported order types with `OrderRejected` before sending |
| Client order ids | send `ClientOrderId` as the venue's client id field; if the venue limits length, document the mapping |
| Timestamps | use the venue's event timestamp for `TsEvent`, the clock for `TsInit` |
| Errors | translate HTTP/WS errors into `OrderRejected` / `ModifyRejected` / `CancelRejected` with the venue's message as reason |
| Rate limits | respect them locally; a throttled request must not be silently dropped |
| Reconnection | resubscribe all active streams; refresh listen keys; re-run reconciliation after a private stream gap |

## Alternatives considered

- **One combined `IVenueClient` for data and execution.** Rejected: data-only
  providers exist, and some users run data from a vendor and execution at the
  venue.
- **Synchronous client methods.** Rejected: I/O belongs off the kernel thread.
  Methods are `Task`-returning and are invoked from the engine without
  awaiting on the kernel thread.

## Consequences

- Adapters are thin translators; everything stateful (orders, positions,
  accounts) lives in the engine.
- A new venue is a new project with three classes, two configs, two
  factories, and a test fixture.
