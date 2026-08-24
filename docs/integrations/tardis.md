# Tardis

Package: `Bytex.Adapters.Tardis`. Plugin: `TardisPlugin`. Factory name: `TARDIS`.

Tardis provides historical market data across many venues. The adapter
downloads daily dataset files (`trades`, `quotes`, `incremental_book_L2`) and
converts them into engine types. It does not stream live data.

## Configuration

```json
{ "factory": "TARDIS", "clientId": "TARDIS", "config": {
    "apiKey": null,
    "baseUrl": "https://datasets.tardis.dev/v1",
    "exchangeMap": { "BINANCE": "binance", "BINANCE-PERP": "binance-futures", "BYBIT": "bybit-spot", "BYBIT-PERP": "bybit" },
    "cacheDirectory": "./tardis-cache"
} }
```

`TARDIS_API_KEY` supplies the key when `apiKey` is null. `exchangeMap`
translates engine venues to Tardis exchange identifiers (derivatives use the
`-PERP` key). `cacheDirectory` keeps downloaded files for reuse.

## Usage

Instruments must already be in the cache (load them from a venue adapter or
the catalog). Then:

```csharp
// from a strategy or actor
RequestTradeTicks(instrumentId, start, end);   // handled by the TARDIS client when routed to it
```

or directly, to fill a catalog:

```csharp
TardisDataClient client = new(new ClientId("TARDIS"), config, kernel.Services);
IReadOnlyList<IData> trades = await client.LoadTradesAsync(instrumentId, start, end, null, ct);
await catalog.WriteAsync(trades, ct);
```

Because the client has no venue of its own, register it as the data engine's
default client (`DataEngine.RegisterDefaultClient`) or address it with an
explicit `clientId` in requests.
