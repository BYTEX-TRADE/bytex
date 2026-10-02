# Sandbox and live trading

A trading node runs the same tradingRuntime as a backtest on a live clock, with real
data clients and either a simulated (sandbox) or real execution client.

## Sandbox: live prices, simulated fills

`examples/configs/sandbox-binance-ema-cross.json` streams Binance spot data
and executes against a simulated venue:

```json
{
  "tradingRuntime": { "moduleHostId": "SANDBOX-001", "environment": "sandbox" },
  "dataClients": [
    { "factory": "BINANCE", "clientId": "BINANCE",
      "config": { "accountType": "spot", "instrumentProvider": { "loadIds": ["bx-market:v2/BINANCE/BTCUSDT"] } } }
  ],
  "executionClients": [
    { "factory": "SANDBOX", "clientId": "BINANCE-SANDBOX",
      "config": { "venue": "BINANCE", "accountType": "cash", "startingBalances": ["100000 USDT", "1 BTC"] } }
  ],
  "strategies": [ ... ],
  "cancelOrdersOnStop": true
}
```

No credentials are needed for public market data. The sandbox client listens
to every quote, trade, and bar for its venue flowing through the engine and
fills orders exactly as the backtest venue would.

```bash
bytex --plugins ./plugins run --config sandbox-binance-ema-cross.json
```

## Live: real account

`examples/configs/live-bybit-ema-cross.json` trades a Bybit account. Start with
a sandbox node against the same live data - the simulated venue matches orders
against the book the venue is streaming - and move to this once it behaves:

```json
{
  "tradingRuntime": { "moduleHostId": "LIVE-001", "environment": "live", "loadState": true, "saveState": true,
              "orderPolicy": { "maxOrderSubmitRate": 10, "maxNotionalPerOrder": { "bx-market:v2/BYBIT/BTCUSDT-PERP": 5000 } } },
  "dataClients": [
    { "factory": "BYBIT", "clientId": "BYBIT", "config": { "productType": "linear",
      "instrumentProvider": { "loadIds": ["bx-market:v2/BYBIT/BTCUSDT-PERP"] } } }
  ],
  "executionClients": [
    { "factory": "BYBIT", "clientId": "BYBIT", "config": { "productType": "linear",
      "instrumentProvider": { "loadIds": ["bx-market:v2/BYBIT/BTCUSDT-PERP"] } } }
  ],
  "strategies": [ ... ],
  "reconcileOnStart": true,
  "cancelOrdersOnStop": true
}
```

Set `BYBIT_API_KEY` and `BYBIT_API_SECRET`, then:

```bash
bytex --plugins ./plugins run --config live-bybit-ema-cross.json
```

## What happens at startup

1. Clients are created from their factories and connected; instrument
   definitions are loaded into the cache.
2. With `reconcileOnStart`, each execution client reports open orders, recent
   fills, and positions. Unknown venue orders become external orders (owned by
   the strategy that claims the instrument through `externalOrderClaims`, or
   by `EXTERNAL`); missing fills are replayed; position mismatches are logged.
3. The tradingRuntime starts; strategies run `OnStart` and subscribe to data.
4. A heartbeat line is logged every `heartbeatInterval` with queue depth,
   open orders, and positions.

## Programmatic use

```csharp
PluginRegistry registry = new();
registry.AddPlugin(new BinancePlugin());
registry.AddExecutionClientFactory(new SandboxExecutionClientFactory());

await using TradingNode node = new(config, registry, loggerFactory);
node.AddStrategy(new EmaCross(strategyConfig));   // or reference it in config
await node.RunAsync(cancellationToken);
```

## Persistence

Add `Bytex.Persistence.Redis` and pass a `RedisCacheDatabase` to the node to
have orders, positions, accounts, and runtimeModule state written through to Redis
and reloaded on the next start:

```csharp
RedisCacheDatabase db = new(new RedisCacheConfig { ConnectionString = "localhost:6379", ModuleHostId = "LIVE-001" });
await using TradingNode node = new(config with { TradingRuntime = config.TradingRuntime with { Cache = new CacheConfig { Persist = true } } }, registry, loggerFactory, db);
```
