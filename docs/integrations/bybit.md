# Bybit

Package: `Bytex.Adapters.Bybit`. Plugin: `BybitPlugin`. Factory name: `BYBIT`.
Uses the V5 API with a unified account.

## Products

| `productType` | Category | Instrument ids | Account type |
|---|---|---|---|
| `spot` | `spot` | `BTCUSDT.BYBIT` | cash |
| `linear` | `linear` (USDT/USDC perpetuals and futures) | `BTCUSDT-PERP.BYBIT` | margin |

## Configuration

```json
{ "factory": "BYBIT", "clientId": "BYBIT", "config": {
    "productType": "linear",
    "testnet": true,
    "apiKey": null, "apiSecret": null,
    "instrumentProvider": { "loadIds": ["BTCUSDT-PERP.BYBIT"] },
    "defaultTriggerType": "markPrice",
    "recvWindowMs": 5000
} }
```

Credentials: `BYBIT_API_KEY`/`BYBIT_API_SECRET` or `BYBIT_TESTNET_*`.

## Data

- Quotes from `orderbook.1`, trades from `publicTrade`, bars from `kline.*`
  (confirmed bars only unless `handleRevisedBars`), book deltas from
  `orderbook.50`/`orderbook.200`, mark/index price and funding from `tickers`.
- Historical: `RequestBars` (paginated) and `RequestTradeTicks` (recent
  trades).
- Supported bar intervals: 1, 3, 5, 15, 30, 60, 120, 240, 360, 720 minutes,
  daily, weekly, monthly.

## Execution

| BYTEX order | Bybit |
|---|---|
| Market | `Market` (quote quantity on spot via `marketUnit`) |
| Limit | `Limit` with `GTC`/`IOC`/`FOK`/`PostOnly` |
| StopMarket / MarketIfTouched | conditional `Market` with `triggerPrice`, `triggerDirection`, `triggerBy` |
| StopLimit / LimitIfTouched | conditional `Limit` |

`reduceOnly` on linear. Modifications use `order/amend` (quantity, price,
trigger). Cancel-all uses `order/cancel-all`.

Events arrive on the private stream topics `order`, `execution`, and
`wallet`; the initial account state comes from `account/wallet-balance`.
Executions are de-duplicated by `execId`.

## Reconciliation

Mass status combines `order/realtime` (open orders), `execution/list`, and
`position/list` (linear). Client order ids map to `orderLinkId`.
