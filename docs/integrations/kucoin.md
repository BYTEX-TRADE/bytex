# KuCoin

Package: `Bytex.Adapters.Kucoin`. Plugin: `KucoinPlugin`. Factory name:
`KUCOIN`. Two markets: a spot trading account through the venue's
high-frequency (HF) order endpoints, and USDT- and USDC-margined perpetual
futures. Margin is not covered.

Both markets are complete: data, execution, catalog and history.

## Products

| Product | `productType` | Instrument ids | Covered |
|---|---|---|---|
| Spot | `Spot` (the default) | `BTC-USDT.KUCOIN` | data, execution, history |
| Perpetual futures | `Futures` | `XBTUSDT-PERP.KUCOIN` | data, execution, catalog, history |

The two markets are separate APIs on separate hosts - `api.kucoin.com` and
`api-futures.kucoin.com` - that share a key and nothing else. They spell their
symbols differently, order their candle rows differently and denominate an
order differently. `productType` chooses; a configuration that does not say
means spot, as it did before futures existed.

A spot pair keeps the venue's own name, `BASE-QUOTE` with a dash, so an
instrument id maps to the venue symbol and back without a lookup. A futures
contract does the same by a different rule: the venue's `XBTUSDTM` becomes
`XBTUSDT-PERP.KUCOIN`, its trailing `M` traded for the `-PERP` the other
venues use. Note `XBT`: that is the venue's own name for bitcoin on this
market, kept rather than mapped so that an id round-trips exactly. Checked
against all 684 tradable contracts - every one ends in `M`, and the rule never
produces the same id twice.

## Configuration

```json
{ "factory": "KUCOIN", "clientId": "KUCOIN", "config": {
    "productType": "Spot",
    "apiKey": null, "apiSecret": null, "apiPassphrase": null,
    "apiKeyVersion": null,
    "instrumentProvider": { "loadIds": ["BTC-USDT.KUCOIN"] }
} }
```

`productType` is `Spot` or `Futures`, and `Spot` when it is not given.
`bytex venues` prints both families with the setting that selects each.

Credentials: `KUCOIN_API_KEY`, `KUCOIN_API_SECRET` and
`KUCOIN_API_PASSPHRASE`; a KuCoin key has three parts. The venue also wants to
be told the version of the key, shown on its API management page:
`apiKeyVersion` or `KUCOIN_API_KEY_VERSION`.

That version does not have to be configured. With nothing set, a client signs
as version 3 and, if the venue refuses for a reason that could be the version,
signs the same request once more as version 2 and keeps whichever worked.
`bytex verify-keys --venue KUCOIN` finds it the same way and names it, so a
green key report means a node starts. Setting the variable pins it and saves
that one extra request; a version that *is* stated is never second-guessed, so
a wrong one is reported as the venue refused it rather than quietly worked
around.

The passphrase is never sent in clear: it travels signed with the secret, as
the venue requires from key version 2 on.

Use the sandbox execution client with the KuCoin data
client for paper trading: `examples/configs/sandbox-kucoin-ema-cross.json` is
that node, ready to run with `bytex run --config`.

## Futures

One KuCoin key covers both markets, but the venue's key permissions for spot
and futures are separate, so a key may work on one and not the other.

**Leverage:** the futures market requires one on every order and has no
default of its own, so `leverage` on the execution client config supplies it
and it is **1** - no leverage - when nothing is set. Spot has none.

**Amending orders:** the futures market cannot change an order once placed, at
all, and declares `amendOrders: false`. Spot can, and does it for stop orders
by cancel-and-replace. A strategy that resizes a protective order is told
before it tries rather than after a position has grown past the stop guarding
it.

**Broker id:** nothing here carries one, so this venue declares
`brokerTag: none`. A `brokerId` set in the client config is ignored rather than
refused - nothing above an adapter has to know which venues have a programme -
and an order is sent exactly as it would be without one.

That is not the same as the venue having no programme, and this page said it
was until it was checked. KuCoin runs two broker tiers with rebate management,
so it declares `brokerProgramme: notCarried`: every trade routed here earns a
rebate nobody is collecting.

Carrying it is more than configuring an id. The mechanism is a signed partner
credential on **every** REST request, not only the ones that place orders -
`KC-API-PARTNER`, `KC-BROKER-NAME`, `KC-API-PARTNER-VERIFY`, and
`KC-API-PARTNER-SIGN`, a base64 HMAC-SHA256 over `timestamp + partner + apiKey`
made with a second secret the programme issues. Three values and a key where a
prefix or a header needs one string, which is why `brokerId` cannot express it
and why nothing carries it yet. A host that misses those headers on any request
loses the rebate on it, so this is not something to switch on halfway.

### Sizing

A futures order is a whole number of contracts, and a contract is a fraction
of the base currency that differs per instrument: one `XBTUSDTM` is 0.001 XBT,
and across the family the contract size runs from 0.01 to 1000. None of that
is visible above the adapter. An instrument is published with its size
increment in base currency - 0.001 XBT on `XBTUSDT-PERP.KUCOIN` - exactly as
Binance and Bybit publish theirs, and the adapter converts on the way to the
venue and back. A strategy that sizes in base units is the same strategy on
all three venues.

A quantity that is not a whole number of contracts is refused rather than
rounded: the engine rounds to the instrument's size increment, which is one
contract, so a quantity that fails was built by hand, and rounding it would
fill a different size than was asked for and report the size asked for.

### What is not offered

Two kinds of contract, each left out for its own reason.

**Inverse, coin-margined contracts.** They are quoted in USD and settle in the
base currency, so a quantity of one cannot be expressed in base units without
a price. Six of the venue's 690 contracts are inverse.

**Contracts with a delivery date.** This family is perpetuals, which is what it
declares, so a contract that expires is not one of them. The venue's only two
dated contracts are inverse as well, so today the first rule would already have
excluded them - the expiry is checked on its own because "not inverse" and
"perpetual" are two separate facts that currently coincide, and a contract that
expires must not be published as one that never does.

Between them, this family is perpetuals and nothing else, and there is no
`Future` instrument class on KuCoin. A dated contract the adapter leaves out is
counted in the line it logs after loading, so it is visible rather than silent.

### Data

One factory name, `KUCOIN`, and two clients behind it: `productType` decides
which a node gets, so nothing configuring the venue has to know it has two
markets.

- The stream has no fixed address, as on spot: the client asks the futures
  `bullet-public` for a server and a token before every connection. Without a
  ping at the interval the venue names the socket stays open and stops
  delivering - measured, 75 seconds of silence produced nothing.
- Quotes from `/contractMarket/tickerV2` (best bid and ask only; the plain
  `ticker` topic mixes a trade into the same message), trades from
  `/contractMarket/execution`, candles from `/contractMarket/limitCandle`, a
  50-level book from `/contractMarket/level2Depth50` (every message is a
  snapshot), and the mark price, index price and predicted funding rate from
  `/contract/instrument`, which carries all three under two subjects.
- Every size on the stream is a number of contracts and arrives converted to
  base currency.
- Timestamps are in three different units across those topics: nanoseconds on
  the ticker and on executions, milliseconds on the book and on the instrument
  topic, seconds on a candle.
- A streamed candle row is `[start in seconds, open, close, high, low,
  turnover, volume]`. **That is not the REST layout** - see below - and the two
  are read in different places for that reason.
- Bars: as on spot, the venue sends the forming candle as trades arrive, never
  marks one closed, and sends nothing for an interval without a trade. A bar is
  published when the next candle starts or two seconds after its interval ends,
  and a quiet interval becomes a flat bar at the previous close. After
  subscribing, the last closed bar is read over REST so the flat bars have a
  price to start from.

### Execution

`productType: Futures` on the execution client too, and one factory name for
both markets.

- Orders go to `/api/v1/orders`. A size is a **whole number of contracts**, so
  a quantity in base currency is divided by the contract size on the way out;
  a quantity that is not a whole number of contracts is rejected rather than
  rounded, because the engine rounds to the instrument's increment - which is
  one contract - so anything else was built by hand.
- The venue **requires a leverage on every order** and has no default of its
  own. `leverage` in the client config supplies one and it is **1**, meaning no
  leverage. Any other default would lever a position nobody asked to lever.
- Market and limit orders, `GTC` and `IOC`, `postOnly`, `reduceOnly`. An order
  sized in the quote currency is refused: this market counts contracts.
- Stops carry `stop: up` or `down` - which way the price has to move to trigger
  them - against the **mark price**, because that is what the venue liquidates
  against. A stop watching the trade price could be missed while the position
  it guards is liquidated. Spot names the same two directions `loss` and
  `entry`, so neither name is shared between the clients.
- **The venue cannot change an order once placed**, on this market, at all -
  not plain orders, which spot can amend, and not stops. A modification is
  refused with that reason rather than turned into a cancel-and-replace, which
  would leave a perpetual position unguarded for two requests without the
  caller having asked for that.
- Events arrive on the private stream: `/contractMarket/tradeOrders`,
  `/contractAccount/wallet`, `/contract/positionAll` and
  `/contractMarket/advancedOrders` for stops. Matches are de-duplicated by
  trade id. The order stream carries no fee, so a fill's commission is worked
  out from the instrument's maker or taker rate; fill reports from REST carry
  the venue's own figure.
- Balances come from `/api/v1/account-overview`, which answers for one
  settlement currency at a time, so both USDT and USDC are asked for.
- **Positions**, which a spot account does not have. The venue signs a position
  in contracts; the sign becomes the side and the size becomes base currency. A
  flat position is still reported, so reconciliation can close one this node
  thinks is open.

### History

- Bars come from `/api/v1/kline/query`, closed bars only, oldest first, the
  same contract as every other venue.
- The venue answers with the **oldest 200 rows** of the window asked for, not
  the 500 its own documentation states, so the fetch walks forwards from the
  start of the window and stops on a page that did not fill.
- A REST candle row is `[start in milliseconds, open, high, low, close,
  volume in contracts, turnover]`. Three things about that differ from the
  **websocket** candle on this same market, which is `[start in seconds, open,
  close, high, low, turnover, volume]`: where the prices are, which of the last
  two numbers is the volume, and the unit of the clock. Reading either with the
  other's layout gives four real prices in the wrong places and a volume out by
  the contract size, with no error anywhere. Spot's REST row is different again
  (`[start in seconds, open, close, high, low, volume]`, newest first). Volumes
  are converted to base currency, so a bar from any of them measures the same
  thing.
- Bar lengths are asked for as a number of minutes, not a word: 1, 3, 5, 15
  and 30 minutes, 1, 2, 4, 8 and 12 hours, daily and weekly. A length the
  venue does not keep is refused before the request is made.
- Quiet intervals behave as they do on spot: an interval without a trade is
  absent from the answer rather than reported flat, so the flat bars are put
  in here and history is continuous.
- Funding history comes from `/api/v1/contract/funding-rates`. The venue
  answers it newest first and its candles oldest first, on the same API;
  `FetchFundingRatesAsync` returns oldest first, as every other venue does.
  Settlement is every 8 hours.

## Data

- The stream has no fixed address. The client asks `bullet-public` for a
  server and a connection token before every connection, the first one and
  each reconnection, and pings at the interval the venue names.
- Quotes from `/spotMarket/level1`, trades from `/market/match`, bars from
  `/market/candles`, a 50-level book from `/spotMarket/level2Depth50` (every
  message is a snapshot).
- Bars: the venue sends the forming candle on every trade, never marks a
  candle closed, and sends nothing for an interval without a trade. A bar is
  published when the next candle starts, or two seconds after its interval has
  ended; updates that still name a closed candle are dropped. With
  `handleRevisedBars` every update is forwarded as a revision.
- Quiet intervals: the venue's own history shows an interval without a trade as
  a flat candle at the previous close with no volume, but it writes those in
  only when the next trade comes. The client builds the same flat bar as time
  passes, so a strategy gets a bar for every interval, also on a market that
  does not trade. After subscribing, the last closed bar is read over REST so
  that the flat bars have a price to start from.
- Historical: `RequestBars` (pages of 1500, closed bars only, oldest first) and
  `RequestTradeTicks` (the last 100 trades). Quiet intervals the venue has not
  written yet, the stretch since the last trade above all, are filled with the
  same flat bars up to the last closed interval, so history is continuous and
  joins the live bars without a gap.
- Supported bar intervals: 1, 3, 5, 15, 30 minutes, 1, 2, 4, 6, 8, 12 hours,
  daily, weekly, monthly.

## Execution

| BYTEX order | KuCoin |
|---|---|
| Market | `hf/orders`, `type: market` with `size`, or `funds` for a quote quantity |
| Limit | `hf/orders`, `type: limit` with `GTC`/`IOC`/`FOK`, `GTT` plus `cancelAfter` for GTD, `postOnly` |
| StopMarket / MarketIfTouched | `stop-order`, `type: market`, `stopPrice`, `stop: loss` or `entry` |
| StopLimit / LimitIfTouched | `stop-order`, `type: limit` |

`stop: loss` triggers when the price falls to the stop price and `entry` when
it rises to it: a sell stop and a buy if-touched are `loss`, a buy stop and a
sell if-touched are `entry`.

A stop order lives in the venue's stop order list until it triggers, and the
venue has no way to change it there. A modification of a stop order (a trailing
stop moving, a resize after a partial exit) is therefore a cancel followed by a
new stop order with the new trigger, price or size:

- The new stop order gets a client id of its own (`<id>-r1`, `-r2`, ..., kept
  within 40 characters), because the venue takes a client id once. The engine
  goes on knowing the order by its own id: stream events, cancels and reports
  under the replacement's id are translated back.
- The stream's "cancel" for the replaced stop order is the client's own doing
  and does not close the engine's order.
- Between the cancel and the new order the position has no stop at the venue,
  for the length of one request.
- If the venue refuses the new stop order, the previous one is placed again and
  the modification is rejected with the venue's reason. If that fails too, the
  order is reported cancelled, because that is then the truth, and a critical
  log line says that the position has no stop at the venue.

Plain orders are modified with `hf/orders/alter` (price and size) and cancelled
by client order id. Cancel-all clears both lists for the symbol.

Client order ids map to `clientOid`, which the venue limits to 40 characters of
letters, digits, `_` and `-`; a longer id is rejected before anything is sent.

Events arrive on the private stream, opened with a `bullet-private` token:
`/spotMarket/tradeOrdersV2` (received, open, match, update, canceled),
`/account/balance` and `/spotMarket/advancedOrders` (stop orders). The initial
account state comes from `accounts?type=trade`. Matches are de-duplicated by
order and trade id.

The order stream carries no fee. A fill's commission is worked out from the
instrument's maker or taker rate (the venue's base rate times the symbol's fee
coefficient); fill reports from REST carry the venue's own figure.

## Reconciliation

The venue lists open orders and fills one symbol at a time. Mass status asks
which symbols have open orders (`hf/orders/active/symbols`), reads
`hf/orders/active` for each, adds the stop order list, and reads `hf/fills`.
A spot account reports no positions.

## Not verified against a live account

The private side of both markets was built from the venue's published API
specification and tested against a stub venue; the public side of both was run
against the live venue.

Still to be confirmed with a real key, on spot: the `stop` field of a stop
order request (the specification lists `stopPrice` only), the client order id a
triggered stop order reports under, and the error code for a mismatched key
version.

And on futures: whether one key grants both markets or the venue's separate
futures permission has to be granted explicitly; the exact subject names and
field spellings of the private order, wallet and position messages; whether a
`reduceOnly` order is refused or truncated when it would exceed the position;
and whether `marginMode` has to be stated rather than left at the venue's
ISOLATED default. The futures order size is sent as `size` in contracts rather
than the newer `qty` field in base currency, because the conversion in
contracts is the one covered by tests here.
