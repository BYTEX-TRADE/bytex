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
    void AttachSink(IDataClientSink sink);   // called by the engine (and re-called by the live node to dispatch onto the tradingRuntime thread)

    Task ConnectAsync(CancellationToken ct);
    Task DisconnectAsync(CancellationToken ct);

    // Subscriptions — each command carries the parameters and a correlation id.
    Task SubscribeAsync(SubscribeCommand command, CancellationToken ct);
    Task UnsubscribeAsync(UnsubscribeCommand command, CancellationToken ct);

    // Historical requests — results are delivered through the sink as DataResponse messages.
    Task RequestAsync(RequestCommand command, CancellationToken ct);
}

// Command families (sealed records in Bytex.Core.Adapters.Commands)
SubscribeInstruments(Venue) SubscribeInstrument(MarketKey) SubscribeQuoteTicks(MarketKey)
SubscribeTradeTicks(MarketKey) SubscribeBars(CandleSeries) SubscribeOrderBookDeltas(MarketKey, BookType, Depth)
SubscribeOrderBookSnapshots(MarketKey, BookType, Depth, Interval) SubscribeInstrumentStatus(MarketKey)
SubscribeMarkPrices / IndexPrices / FundingRates(MarketKey) SubscribeData(DataType)
Unsubscribe… (mirror)
RequestInstrument(MarketKey) RequestInstruments(Venue) RequestQuoteTicks / TradeTicks(MarketKey, Start, End, Limit)
RequestBars(CandleSeries, Start, End, Limit) RequestData(DataType, Start, End, Limit)
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

`OnData` may be called from any thread; the sink marshals onto the tradingRuntime
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
    Task<OrderStatusReport?> GenerateOrderStatusReportAsync(MarketKey id, ClientOrderId? clientOrderId, VenueOrderId? venueOrderId, CancellationToken ct);
    Task<IReadOnlyList<OrderStatusReport>> GenerateOrderStatusReportsAsync(MarketKey? id, UnixNanos? start, UnixNanos? end, bool openOnly, CancellationToken ct);
    Task<IReadOnlyList<FillReport>> GenerateFillReportsAsync(MarketKey? id, VenueOrderId? venueOrderId, UnixNanos? start, UnixNanos? end, CancellationToken ct);
    Task<IReadOnlyList<PositionStatusReport>> GeneratePositionStatusReportsAsync(MarketKey? id, UnixNanos? start, UnixNanos? end, CancellationToken ct);
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
protected void GenerateOrderSubmitted(StrategyId, MarketKey, ClientOrderId, UnixNanos eventTime);
protected void GenerateOrderAccepted(StrategyId, MarketKey, ClientOrderId, VenueOrderId, UnixNanos eventTime);
protected void GenerateOrderRejected(..., string reason, ...);
protected void GenerateOrderCanceled / Expired / Triggered / Updated / ModifyRejected / CancelRejected(...);
protected void GenerateOrderFilled(StrategyId, MarketKey, ClientOrderId, VenueOrderId, PositionId?, TradeId,
                                   OrderSide, OrderType, Quantity lastQty, Price lastPx, Currency quoteCurrency,
                                   Money commission, LiquiditySide, UnixNanos eventTime);
protected void GenerateAccountState(IReadOnlyList<AccountBalance>, IReadOnlyList<MarginBalance>, bool reported, UnixNanos eventTime);
```

### Instrument provider

```csharp
public interface IInstrumentProvider
{
    Venue Venue { get; }
    Task LoadAllAsync(CancellationToken ct, IReadOnlyDictionary<string,string>? filters = null);
    Task LoadIdsAsync(IReadOnlyList<MarketKey> ids, CancellationToken ct);
    Task LoadAsync(MarketKey id, CancellationToken ct);
    Instrument? Find(MarketKey id);
    IReadOnlyList<Instrument> GetAll();
    IReadOnlyDictionary<string, Currency> Currencies { get; }

    // What the venue lists, as NAMES. Default: load everything and describe it.
    Task<IReadOnlyList<InstrumentListing>> ListAsync(CancellationToken ct);
}

public record InstrumentProviderConfig
{
    public bool LoadAll { get; init; }
    public IReadOnlyList<MarketKey> LoadIds { get; init; } = [];
    public IReadOnlyDictionary<string, string> Filters { get; init; } = empty;
    public bool LogWarnings { get; init; } = true;
}
```

#### Listing without loading

`ListAsync` answers "what does this venue call its markets" without building
instruments. It exists because the two questions have very different costs on
the venues that enrich a listing afterwards: OKX asks for position tiers five
instrument families at a time, so a swap family of 477 contracts is about
ninety-six requests after the one that lists them, and Bitget answers tiers for
one contract at a time. A host resolving a name - "the perpetual on ETH against
USDT, whatever this venue calls it" - discards every one of those answers.
Measured by such a host: 2.4 s to load one known instrument, 40.8 s to find the
same one by its pair.

```csharp
public sealed record InstrumentListing(
    MarketKey Id,                 // what to load it by
    Symbol RawSymbol,                // the venue's own spelling, verbatim
    InstrumentClass InstrumentClass,
    Currency? BaseCurrency,
    Currency QuoteCurrency,
    Currency SettlementCurrency,     // tells a coin-settled contract from its sibling
    bool IsInverse);
```

**A row is deliberately not an instrument, and an override must publish
nothing.** An instrument missing its margin requirement can be traded, and a
position sized against no venue requirement looks exactly like a position - so a
listing that added its rows to the provider would be a quiet way into trading
under-specified markets. Nothing from `ListAsync` reaches `Find` or `GetAll`: a
host reads the listing, picks a row, and loads that instrument properly.

The default implementation loads everything and describes what it loaded. That
is correct for every provider and cheap for none; a venue whose listing endpoint
already carries the whole answer has nothing to skip and should leave it alone.
Either way the adapter parity table records, per venue, whether the answer is
the adapter's own or the base's.

### Factories and configuration

Integrations register factories so nodes can build clients from
configuration:

```csharp
public interface IDataClientFactory
{
    string Name { get; }                                         // "BINANCE", "BYBIT", "TARDIS"
    Type ConfigType { get; }                                     // lets nodes deserialise JSON into the right config
    IDataClient Create(ClientId id, DataClientConfig config, TradingRuntimeServices services);
}
public interface IExecutionClientFactory
{
    string Name { get; }
    Type ConfigType { get; }
    IExecutionClient Create(ClientId id, ExecutionClientConfig config, TradingRuntimeServices services);
}

public abstract record DataClientConfig { InstrumentProviderConfig InstrumentProvider; bool HandleRevisedBars; ... }
public abstract record ExecutionClientConfig { InstrumentProviderConfig InstrumentProvider; ... }

// TradingRuntimeServices: the tradingRuntime-owned objects a client needs
public sealed record TradingRuntimeServices(IClock Clock, ICache Cache, IMessageBus MessageBus, ILoggerFactory Logging,
                                    ModuleHostId ModuleHostId, Environment Environment);
```

Venue-specific configs derive from the base records (for example
`BinanceDataClientConfig { ApiKey, ApiSecret, AccountType (Spot|UsdMFutures|CoinMFutures), BaseUrlHttp, BaseUrlWs }`).
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
execution client. The order coordinator then:

1. Adds any venue order it has no record of as an external order (claimed by
   a strategy if the instrument is in its `ExternalOrderClaims`, otherwise
   owned by the engine's `EXTERNAL` strategy id).
2. Replays missing fills and status changes on known orders.
3. Reconciles positions against `PositionStatusReport`s and logs any
   mismatch it cannot resolve.

Adapters must be able to answer mass status from REST endpoints alone; the
private WebSocket is used only for steady-state updates.

`EXTERNAL` belongs to that protocol and to nothing else. A private stream
carries the whole account, so a handler asks `OrderPlacedHere(clientOrderId,
venueOrderId)` - both ids, because a venue may echo an id it was not sent and
the engine indexes venue order ids for exactly that - and returns without
reporting anything when the answer is null. Attributing a stream message to
`EXTERNAL` instead produces an event the engine drops as belonging to an
unknown order, and bypasses the claims above. A reply to one of the engine's
own commands may still name `EXTERNAL` for an order the cache has lost, since
the engine correlates a reply by the command rather than by the id;
`ExternalAttributionTests` holds both halves of that.

### Adapter responsibilities checklist

| Area | Adapter must |
|---|---|
| Symbols | map venue symbols to `MarketKey` and back; never leak venue strings to the engine |
| Precision | build instruments with correct `PriceIncrement` / `SizeIncrement` and fees |
| Order types | reject unsupported order types with `OrderRejected` before sending |
| Client order ids | send `ClientOrderId` as the venue's client id field; if the venue limits length, document the mapping |
| Timestamps | use the venue's event timestamp for `EventTime`, the clock for `CreatedTime` |
| Errors | translate HTTP/WS errors into `OrderRejected` / `ModifyRejected` / `CancelRejected` with the venue's message as reason |
| Rate limits | respect them locally; a throttled request must not be silently dropped |
| Reconnection | resubscribe all active streams; refresh listen keys; re-run reconciliation after a private stream gap |

## Alternatives considered

- **One combined `IVenueClient` for data and execution.** Rejected: data-only
  providers exist, and some users run data from a vendor and execution at the
  venue.
- **Synchronous client methods.** Rejected: I/O belongs off the tradingRuntime thread.
  Methods are `Task`-returning and are invoked from the engine without
  awaiting on the tradingRuntime thread.

## Consequences

- Adapters are thin translators; everything stateful (orders, positions,
  accounts) lives in the engine.
- A new venue is a new project with three classes, two configs, two
  factories, and a test fixture.
