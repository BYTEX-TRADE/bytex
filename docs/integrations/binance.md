# Binance

Package: `Bytex.Adapters.Binance`. Plugin: `BinancePlugin`. Factory name: `BINANCE`.

## Accounts

| `accountType` | Market | Instrument ids | Account type |
|---|---|---|---|
| `spot` | spot | `BTCUSDT.BINANCE` | cash |
| `usdMFutures` | USDⓈ-margined perpetuals and futures | `BTCUSDT-PERP.BINANCE` (perpetuals), `BTCUSDT_250926.BINANCE` (dated) | margin |

One client instance serves one account type. Venue name is `BINANCE` for
both; when a node uses both spot and futures, route commands explicitly with
`clientId`.

## Configuration

```json
{ "factory": "BINANCE", "clientId": "BINANCE", "config": {
    "accountType": "spot",
    "testnet": false,
    "apiKey": null, "apiSecret": null,
    "baseUrlHttp": null, "baseUrlWs": null,
    "instrumentProvider": { "loadAll": false, "loadIds": ["BTCUSDT.BINANCE"], "filters": { "quote": "USDT" } },
    "handleRevisedBars": false
} }
```

Credentials come from `apiKey`/`apiSecret` or the environment variables
`BINANCE_API_KEY`/`BINANCE_API_SECRET` (`BINANCE_TESTNET_*` with
`testnet: true`). The data client works without credentials. The execution
client adds `defaultTriggerType` (`lastPrice` or `markPrice`, futures) and
`recvWindowMs`.

## Data

- Quotes from `bookTicker`, trades from `trade`, bars from `kline_*`
  (closed bars only unless `handleRevisedBars`), book deltas from
  `depth@100ms` with a REST snapshot on subscription, mark/index price and
  funding rate from `markPrice@1s` (futures).
- Historical: `RequestBars` (paginated klines) and `RequestTradeTicks`
  (aggregated trades by default).
- Supported bar intervals: 1s, 1m, 3m, 5m, 15m, 30m, 1h, 2h, 4h, 6h, 8h, 12h,
  1d, 3d, 1w, 1M.

## Execution

| BYTEX order | Spot | Futures |
|---|---|---|
| Market | `MARKET` (quote quantity supported) | `MARKET` |
| Limit | `LIMIT`, `LIMIT_MAKER` for post-only, iceberg via `icebergQty` | `LIMIT`, `GTX` for post-only |
| StopMarket | `STOP_LOSS` | `STOP_MARKET` |
| StopLimit | `STOP_LOSS_LIMIT` | `STOP` |
| MarketIfTouched | `TAKE_PROFIT` | `TAKE_PROFIT_MARKET` |
| LimitIfTouched | `TAKE_PROFIT_LIMIT` | `TAKE_PROFIT` |
| TrailingStopMarket | — | `TRAILING_STOP_MARKET` (offset as callback rate) |

Time in force: GTC, IOC, FOK; GTD on futures. `reduceOnly` on futures.
Modifications use `PUT /fapi/v1/order` on futures and cancel-replace on spot
(limit orders only). Cancel-all uses the venue's endpoint.

Order events arrive on the user data stream (`executionReport` /
`ORDER_TRADE_UPDATE`); account balances from `outboundAccountPosition` /
`ACCOUNT_UPDATE` and an initial REST snapshot. The listen key is refreshed
every 30 minutes and recreated on expiry.

## Reconciliation

Mass status combines open orders, recent trades per instrument, and (futures)
position risk. Order reports carry the client order id so reconciliation can
match cached orders.

## Notes

- Client order ids must be at most 36 characters on Binance; the default
  `O-yyyyMMdd-HHmmss-tag-tag-n` format fits when trader and strategy tags are
  short.
- Fees in instrument definitions are defaults (10 bps spot, 2/5 bps futures);
  actual commissions come from fill events.
