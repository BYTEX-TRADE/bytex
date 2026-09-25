# Bybit

Package: `Bytex.Adapters.Bybit`. Plugin: `BybitPlugin`. Factory name: `BYBIT`.
Uses the V5 API with a unified account.

## Products

| `productType` | Category | Instrument ids | Account type |
|---|---|---|---|
| `spot` | `spot` | `BTCUSDT.BYBIT` | cash |
| `linear` | `linear` (USDT/USDC perpetuals and futures) | `BTCUSDT-PERP.BYBIT` | margin |
| `inverse` | `inverse` (coin-margined perpetuals and dated futures) | `BTCUSD-PERP.BYBIT`, `BTCUSDZ26.BYBIT` | margin |
| `option` | `option` (USDT-settled options) | `BTC-25JUN27-106000-P-USDT.BYBIT` | margin |

**Inverse contracts** are quoted in USD, sized in the venue's own USD contracts
and settled in the base coin, so profit, margin and fees all come out in the
base currency and go through one over the price. The instrument carries
`isInverse`, and the engine's arithmetic follows from there.

Two things about the family are easy to get wrong. Its dated contracts are
spelled `BTCUSDZ26` and `BTCUSDH27` - no dash, no date a reader could parse -
so whether a contract is a perpetual or a future comes from the venue's
`contractType` field and never from the name. And the `-PERP` suffix is not
decoration: spot lists `BTCUSD` and `ETHUSD`, both trading, so without it one
instrument id would mean two different instruments on this venue.

**Options** are unlike the three families beside them, and each difference
below is the venue's answer rather than a limit of this adapter:

| | Options here |
|---|---|
| Funding | none. `/v5/market/funding/history` refuses `category=option` |
| Bar history | none by any route. `/v5/market/kline` refuses the category, and the socket's kline topic is accepted, reported as a success and never delivered |
| Margin and leverage ceiling | not published. `/v5/market/risk-limit` refuses the category and the contract data carries no `leverageFilter`, so both are left unset and the ceiling reads as "the venue did not say" |
| Strike | published nowhere but the symbol, so it is read from there and checked against `baseCoin` and `optionsType` as it is read |
| Listing | `instruments-info` with no `baseCoin` answers one underlying and an empty cursor. Set `instrumentProvider.filters.baseCoin` to a comma-separated list of coins to list more |
| Book depths | 25 and 100, not the 50 and 200 of the other families |
| Trades | published per UNDERLYING (`publicTrade.BTC`), each row naming its contract |
| Quotes | from the `tickers` topic, under that market's own `bidPrice`/`askPrice` field names; `orderbook.1` delivers nothing |

An option's fee is charged by the venue as a fraction of the underlying's INDEX
price, capped at a share of the premium. This engine prices a commission as a
fraction of the traded notional, which for an option is the premium - so the
declared rate applied that way is a lower bound on what the venue charges
rather than the charge itself.

## Configuration

```json
{ "factory": "BYBIT", "clientId": "BYBIT", "config": {
    "productType": "linear",
    "apiKey": null, "apiSecret": null,
    "instrumentProvider": { "loadIds": ["BTCUSDT-PERP.BYBIT"] },
    "defaultTriggerType": "markPrice",
    "recvWindowMs": 5000
} }
```

Credentials: `BYBIT_API_KEY`/`BYBIT_API_SECRET`.

**Broker id:** `brokerId` in the execution client config travels in the
`X-Referer` header on order requests, which this venue's broker programme
expects. It is not part of the signed payload, so carrying one leaves the
request the venue authenticates byte-for-byte unchanged and the order's own
identity untouched. Left unset, no such header is sent.

## Data

- Quotes from `orderbook.1`, trades from `publicTrade`, bars from `kline.*`
  (confirmed bars only unless `handleRevisedBars`), book deltas from
  `orderbook.50`/`orderbook.200`, mark/index price and funding from `tickers`.
  The option market differs on every one of those; see the table above.
- Historical: `RequestBars` (paginated) and `RequestTradeTicks` (recent
  trades). Bars are refused for options, and so is funding for spot and
  options - refused rather than answered with an empty list, because an empty
  list says "never charged" where the truth is "never chargeable".
- Supported bar intervals: 1, 3, 5, 15, 30, 60, 120, 240, 360, 720 minutes,
  daily, weekly, monthly.

## Execution

| BYTEX order | Bybit |
|---|---|
| Market | `Market` (quote quantity on spot via `marketUnit`) |
| Limit | `Limit` with `GTC`/`IOC`/`FOK`/`PostOnly` |
| StopMarket / MarketIfTouched | conditional `Market` with `triggerPrice`, `triggerDirection`, `triggerBy` |
| StopLimit / LimitIfTouched | conditional `Limit` |

`reduceOnly` on linear and inverse; it is left off an option order, because
this venue's documentation of the field for that market could not be confirmed
without a key and a rejected flag would fail the whole order. Modifications use
`order/amend` (quantity, price, trigger) for every family; whether the venue
honours an option amend is unverified for the same reason. Cancel-all uses
`order/cancel-all`.

A leverage is set per symbol on the two contract families. Spot and options
have none to set - the venue margins an option by a portfolio calculation and
publishes no per-symbol ceiling - so a configured leverage there is logged as
unsent rather than dropped quietly.

Events arrive on the private stream topics `order`, `execution`, and
`wallet`; the initial account state comes from `account/wallet-balance`.
Executions are de-duplicated by `execId`. Only a trade becomes a fill:
`Trade`, `AdlTrade`, `BustTrade` and `BlockTrade`, and a row carrying no
`execType` at all, as spot sends. The venue puts more than trades on that
topic - a funding payment, a delivery, a settlement and a position transfer
all arrive there with a quantity that is the position's - and those are
ignored, on the stream and in the `execution/list` history alike.

## Reconciliation

Mass status combines `order/realtime` (open orders), `execution/list`, and
`position/list` (every family but spot, which holds balances rather than
positions). Client order ids map to `orderLinkId`.

Asking about a whole market rather than one instrument needs a filter this
venue requires and spells differently per family: a settle coin for the
contract families and an underlying for options. An inverse contract settles in
its own base coin, so there is no single settle coin for that market and the
coins come from the contracts the client holds - one request each.
