# Binance

Package: `Bytex.Adapters.Binance`. Plugin: `BinancePlugin`. Factory name: `BINANCE`.

## Accounts

| `accountType` | Market | Instrument ids | Account type |
|---|---|---|---|
| `spot` | spot | `BTCUSDT.BINANCE` | cash |
| `usdMFutures` | USDⓈ-margined perpetuals and futures | `BTCUSDT-PERP.BINANCE` (perpetuals), `BTCUSDT_250926.BINANCE` (dated) | margin |
| `coinMFutures` | coin-margined perpetuals and futures | `BTCUSD_PERP.BINANCE` (perpetuals), `BTCUSD_261225.BINANCE` (dated) | margin |

One client instance serves one account type. Venue name is `BINANCE` for all
three; when a node uses more than one market, route commands explicitly with
`clientId`.

| | `spot` | `usdMFutures` | `coinMFutures` |
|---|---|---|---|
| REST | `https://api.binance.com` | `https://fapi.binance.com` | `https://dapi.binance.com` |
| WebSocket | `wss://stream.binance.com:9443` | `wss://fstream.binance.com` | `wss://dstream.binance.com` |
| Path prefix | `/api/v3` | `/fapi/v1` | `/dapi/v1` |
| Balances | `/api/v3/account` | `/fapi/v2/balance` | `/dapi/v1/balance` |
| Positions | — | `/fapi/v2/positionRisk` | `/dapi/v1/positionRisk` |
| Tradable field | `status` | `status` | `contractStatus` |
| Quantities in | base units | base units | contracts |
| Candle page | 1,000 | 1,500 | 1,500 |
| Maker/taker default | 10/10 bps | 2/5 bps | 1.5/4 bps |
| Key | `BINANCE_API_KEY`/`BINANCE_API_SECRET` | same | same |

The two account reads are versioned differently per family, and that is not a
naming pattern to extrapolate from: `/dapi/v2/balance` and `/fapi/v1/balance`
were both measured to answer with an HTML error page, so neither family's
path exists under the other's version.

### Coin-margined contracts

A coin-margined contract is quoted in USD, sized in a whole number of USD
contracts, and margined and settled in the base coin - so it is **inverse**,
and notional, margin, profit, commission and funding all come out in the coin
and all go through one over the price. An instrument from this family carries
`isInverse`, a size increment of one contract, and a multiplier equal to what
the venue publishes as its `contractSize`: measured on 2026-09-25 as 100 USD
on every BTCUSD contract and 10 USD on the other 27. Notional is
`contracts x contractSize / price`, so 200 contracts of `BTCUSD_PERP` at
50,000 is 0.4 BTC.

The instrument class comes from the venue's own `contractType` field and never
from the symbol. This family spells its perpetuals `BTCUSD_PERP` and its
quarterlies `BTCUSD_261225`, so nothing readable from a name tells the two
apart; its contract types are `PERPETUAL`, `CURRENT_QUARTER` and
`NEXT_QUARTER`, and a contract in delivery answers the compound
`CURRENT_QUARTER DELIVERING`. Ids keep the venue's own spelling, underscore
and all, because the venue has already marked which contract is which and a
second marking would have to be undone from the id alone.

Its perpetuals are charged funding and its dated contracts are not: a dated
contract answers the funding-rate read with an empty array, which is correct
rather than a gap - a contract that delivers converges by delivering.

`exchangeInfo` **ignores its `symbol` filter here as well**, which was measured
on this family rather than assumed from its sibling: asked with a symbol that
exists, one that does not, and `pair=BTCUSD`, it answered HTTP 200 with all 30
contracts every time. Loading one instrument therefore keeps only the one that
was asked for, as on the USD-margined family.

### Testnet

Both futures families share one test host, which is worth stating because the
mainnet hosts do not: `https://testnet.binancefuture.com` serves `/fapi` and
`/dapi` alike, and `wss://dstream.binancefuture.com` carries the coin-margined
streams. Reach them with `baseUrlHttp` and `baseUrlWs`. The coin-margined test
network lists 51 contracts against mainnet's 30 - including pending and
delivering ones, which the adapter drops - and allows 6,000 request weight a
minute against mainnet's 2,400.

### Margin and the leverage ceiling

Both futures families publish `requiredMarginPercent` and `maintMarginPercent`
per symbol and neither figure is what the venue requires: every one of the
coin-margined family's 30 contracts answers 5.0000 and 2.5000, from the 100-USD
BTCUSD_PERP to the 10-USD altcoin quarterlies, which is a venue-wide default
rather than a per-contract requirement. The real per-notional brackets are
behind the signed `leverageBracket` read on each host - an unauthenticated call
is refused with `-2014` on both, the web interface's own bracket feed rejects
it, and no futures-data path serves it - so they are fetched where a key is
held and the venue-wide default is kept where one is not. `maxLeverage` is then
**null**, and null means the venue did not say rather than unlimited: nothing is
refused on a guess, and the leverage guard only holds where the venue states
what it grants.

A configured `leverage` is set at the venue before anything trades, per symbol,
under each family's own prefix - an order carrying one is ignored here. It must
be a whole number on both futures families: that comes from the venue's
documentation rather than from measurement, because this venue checks the API
key before it looks at a parameter, so an unauthenticated call cannot be made
to reject a fraction and prove it.

## Configuration

```json
{ "factory": "BINANCE", "clientId": "BINANCE", "config": {
    "accountType": "spot",
    "apiKey": null, "apiSecret": null,
    "baseUrlHttp": null, "baseUrlWs": null,
    "instrumentProvider": { "loadAll": false, "loadIds": ["BTCUSDT.BINANCE"], "filters": { "quote": "USDT" } },
    "handleRevisedBars": false
} }
```

Credentials come from `apiKey`/`apiSecret` or the environment variables
`BINANCE_API_KEY`/`BINANCE_API_SECRET`. The data client works without
credentials. The execution
client adds `defaultTriggerType` (`lastPrice` or `markPrice`, futures) and
`recvWindowMs`.

**Broker id:** `brokerId` in the execution client config PREFIXES the client
order id on every order this adapter sends, which is how this venue carries a
broker id. That id is the key reconciliation matches an order on, so the
prefix is applied everywhere an order is named to the venue - placing,
cancelling, amending - and stripped from everything the venue says back, so
the engine keeps knowing the order by its own id. A broker id long enough to
push the client order id past the 36 characters the venue accepts is refused
before the order is sent, naming both parts. Left unset, the engine's own id
is sent exactly as it was before the field existed.

## Data

- Quotes from `bookTicker`, trades from `trade`, bars from `kline_*`
  (closed bars only unless `handleRevisedBars`), book deltas from
  `depth@100ms` with a REST snapshot on subscription, mark/index price and
  funding rate from `markPrice@1s` (either futures family).
- The USD-margined family needs two sockets: `/public/stream` carries book
  tickers, trades and depth, `/market/stream` carries klines, mark prices and
  aggregated trades, and the unrouted path accepts a subscription to the
  second set without ever delivering it. Spot and the coin-margined family use
  one socket each. That the coin-margined `/stream` really carries all six was
  measured, not assumed: three runs of twenty-five seconds on 2026-09-25, all
  six stream kinds subscribed at once, every one of them delivering on every
  run.
- Historical: `RequestBars` (paginated klines) and `RequestTradeTicks`
  (aggregated trades by default).
- Supported bar intervals: 1s, 1m, 3m, 5m, 15m, 30m, 1h, 2h, 4h, 6h, 8h, 12h,
  1d, 3d, 1w, 1M.

### Bar history

Bar history is fetched page by page: a request with a start time walks the
window forwards from it, one page at a time - 1,000 bars on spot and 1,500 on
either futures family - until the venue has no more or the limit is reached; a
request without one pages backwards from the end. A start bounds a bar's close
and an end bounds its open, as on the other adapters.

Every family stamps element 0 of a candle row with the bar's OPEN and element
6 with its CLOSE, in milliseconds, the close one millisecond short of the next
open. Bars are returned close-stamped and only closed bars are returned. What
element 5 means differs - base units on spot and USD-margined futures, a
number of contracts on the coin-margined one - and needs no conversion, because
that is the unit each family's instruments are sized in.

Funding history pages a thousand settlements at a time on both futures
families, and the limit is always sent: asked with none, the USD-margined host
answers 100 rows and the coin-margined one 500, so a loop treating a short page
as the end would stop early.

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

The futures column is the same on both futures families: the coin-margined
family publishes the same seven order types and the same four times in force
on every one of its 30 contracts.

Time in force: GTC, IOC, FOK; GTD on futures. `reduceOnly` on futures.
Modifications use `PUT` on the family's own order path on futures and
cancel-replace on spot (limit orders only). Cancel-all uses the venue's
endpoint. That the coin-margined family serves the modify endpoint at all was
established the only way an unauthenticated caller can: a `PUT` to it, and a
read of its `orderAmendment`, are refused for the key rather than for the
route, where a path this venue does not serve answers with an HTML error page
instead.

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
  short. The same limit is applied on the coin-margined family, from this
  venue's documentation: an order cannot be placed without a key, so the
  length at which it refuses one could not be measured. The refusal is kept
  because a refused id is a sentence naming both parts, where a truncated one
  is an order the engine can no longer match.
- Fees in instrument definitions are defaults (10 bps spot, 2/5 bps
  USD-margined futures, 1.5/4 bps coin-margined futures); actual commissions
  come from fill events. The per-family figures are this venue's published
  schedules: there is no public fee endpoint on either futures host and
  `commissionRate` is signed, so an account's own rates could not be measured.
- `bytex catalog fetch-instruments --venue BINANCE` selects a market with
  `--instrument-type Spot | UsdMFutures | CoinMFutures`; `--futures` on its own
  still means the USD-margined family, as it did before there were three.
- `bytex verify-keys --venue BINANCE` adds a `coinm-futures` check where the
  key's restrictions say futures are on. One permission covers both futures
  markets on two different hosts, so "futures on" does not establish that the
  key reaches the coin-margined one.
- What could NOT be verified without a key, and is therefore documented
  rather than measured: the shape of each family's `leverageBracket` answer,
  the shape of the coin-margined balance and position reads, the leverage
  field being a whole number, and the client-order-id length limit. The
  bracket parse accepts a row identified by either `symbol` or `pair` for
  exactly this reason, and matches a contract on both.
