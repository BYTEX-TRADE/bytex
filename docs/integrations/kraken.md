# Kraken

Package: `Bytex.Adapters.Kraken`. Plugin: `KrakenPlugin`. Factory name:
`KRAKEN`. **Two platforms, not two products:** a spot exchange on
`api.kraken.com` and a futures exchange on `futures.kraken.com`, with separate
credentials, separate request signing, separate error shapes and separate
symbol conventions. Spot margin trading is not covered.

Both platforms are complete: data, execution, catalog and history.

Everything on this page about a public endpoint was measured against the live
venue on 2026-09-25 - Kraken needs no key for its catalogs, candles, fee
schedules, funding history or public sockets - and in five places the venue
disagrees with its own documentation or with itself. Each is marked. What could
not be measured is in [Not verified against a live
account](#not-verified-against-a-live-account).

## Products

| Product | `productType` | Instrument ids | Covered |
|---|---|---|---|
| Spot | `Spot` (the default) | `BTC-USD.KRAKEN` | data, execution, catalog, history |
| Perpetual futures | `Futures` | `PF_XBTUSD.KRAKEN` | data, execution, catalog, history, funding |
| Dated futures | `Futures` | `FF_XBTUSD_261225.KRAKEN` | data, execution, catalog, history |

`productType` chooses the platform; a configuration that does not say means
spot, which is the key most people already have.

**There is no test environment on either platform.** The futures demo at
`demo-futures.kraken.com` now answers a permanent redirect to a marketing page,
every other spelling of a demo or sandbox host on either platform fails to
resolve, and spot has never had one. So this venue can be papered against live
data with the sandbox execution client and cannot be rehearsed against a test
venue. Both families declare that by having no testnet host and no setting that
would select one.

## How a pair is spelled

This is the most expensive fact about Kraken spot, and the venue contradicts
itself about it.

`/0/public/AssetPairs` publishes five names for bitcoin against the dollar:
the key `XXBTZUSD`, `altname: XBTUSD`, `wsname: XBT/USD`, `base: XXBT` and
`quote: ZUSD`. Of those, `XBTUSD` and `XXBTZUSD` are accepted by REST,
`BTC/USD` is accepted by REST **and** by the socket, and **`XBT/USD` - the
field whose name says it is the socket's name - is refused by both**: the
socket answers "Currency pair not supported XBT/USD" and REST answers
"EQuery:Unknown asset pair".

The same is true of dogecoin (`XDG` against `DOGE`) and of nothing else.
Substituting those two tokens into all 1451 `wsname` values reproduced the
socket's own list of 1451 symbols exactly, with no mismatches on either side.

So an instrument's raw symbol is `BASE/QUOTE` in the spelling the venue
accepts, and its id is the same with a dash: `BTC-USD.KRAKEN`, `ETH-BTC.KRAKEN`,
`DOGE-USD.KRAKEN`. The dash is there because an id is written into stored
history file names, log lines and URLs, where a slash is a path separator; the
venue's 674 asset codes contain neither character, so the two convert exactly
in both directions and the 1451 ids are all distinct.

No currency is renamed by the adapter. `BTC` is what the venue accepts and what
the socket's own instrument channel publishes. On the **futures** platform
bitcoin really is `XBT` in a contract's symbol - `PF_XBTUSD` - and stays `XBT`
there, while that contract's own `base` field says `BTC` and is used for the
currency. Two spellings on one venue, each taken from the platform that uses it.

A private balance is keyed by the **asset id** instead (`ZUSD`, `XXBT`), which
is a third naming again. The adapter reads `/0/public/Assets` once at connect
and maps each id to the code the venue answers to, so a balance in bitcoin is
published as `BTC` rather than as `XXBT`, which nothing on the venue trades.

## Configuration

```json
{ "factory": "KRAKEN", "clientId": "KRAKEN", "config": {
    "productType": "Spot",
    "apiKey": null, "apiSecret": null,
    "instrumentProvider": { "loadIds": ["BTC-USD.KRAKEN"] }
} }
```

`productType` is `Spot` or `Futures`, and `Spot` when it is not given.
`bytex venues` prints both families with the setting that selects each.

Credentials: `KRAKEN_API_KEY` and `KRAKEN_API_SECRET` (the venue calls the
second one the private key; it is base64). **The two platforms need different
credentials in those same two variables.** A spot key is issued on
`kraken.com`, a futures key on `futures.kraken.com`, and neither signs for the
other because the signing schemes differ - so the variables hold whichever
platform's key the configured family needs, and a process running both families
has to supply one of them through the client config rather than the environment.

`bytex verify-keys --venue KRAKEN` tries **both** platforms and reports which
one took the key, because a perfectly valid key configured against the wrong
family is this venue's own invitation to failure. It cannot report what the key
may do: Kraken publishes no endpoint for a key's permissions, its withdrawal
rights or its address restrictions, so those facts come back null and a check
says so rather than claiming a key is safe that nothing checked.

Use the sandbox execution client with either Kraken data client for paper
trading.

## Spot

### Data

- One socket for public market data, `wss://ws.kraken.com/v2`, and a second
  for private data, `wss://ws-auth.kraken.com/v2`. **The venue refuses each on
  the other's host and says so:** asked for `executions` on the public one it
  answers "Private data and trading are unavailable on this endpoint. Try
  ws-auth.kraken.com", and asked for `trade` on the private one it answers the
  mirror image. There is no connection that can carry both, so the data client
  opens the public host and the execution client opens the private one - the
  latter derived from the configured public base, so pointing the adapter at a
  proxy moves both halves at once.
- Quotes from `ticker`, trades from `trade`, candles from `ohlc`, and the top
  of a ten-level `book`. The book arrives as a snapshot and then as deltas that
  touch only the levels that moved, with a zero quantity meaning a level has
  gone, so the book is kept and a quote is published from its real top rather
  than from whichever level a delta named.
- Timestamps are ISO-8601 instants to the microsecond.
- **A candle on this socket is stamped by its CLOSE**, which no other venue
  here does: the message carries both `interval_begin` and `timestamp`, and the
  second is the end of the interval. A bar's event time is read from the message
  rather than computed.
- Bars: the venue resends the forming candle as trades arrive and never marks
  one closed, so a bar is published when the venue moves to the next interval,
  or two seconds after the interval it covers has ended - which is what covers
  an interval with no trade in it, where the venue sends nothing at all. With
  `handleRevisedBars` every update is forwarded as a revision.
- A refused subscription arrives with `method: "subscribe"` whatever was asked,
  so a failure is recognised by its `success` flag and not by the method it
  claims.
- Historical: `RequestBars` and `RequestTradeTicks`. A public trade row's time
  is in **fractional seconds**; read as whole seconds, trades within one second
  would share a timestamp and lose their order.

### History

**The spot candle endpoint cannot be paged, and that decides what this platform
can be used for.**

`/0/public/OHLC` takes a `since` lower bound and answers with at most **720**
intervals - and when the window asked for is wider than that it answers with
the **newest** 720 rather than the oldest. Measured: `since=0` on one-minute
candles returned the newest 720, and `since` set 2000 minutes back returned the
same newest 720. There is no parameter that reaches further back.

So **one-minute spot history older than twelve hours cannot be fetched from
Kraken at all.** Longer bars reach proportionally further - 720 days of daily
candles - and the weekly series came back complete at 678 rows because the venue
holds fewer than 720 weeks. The fetch makes one request and no loop: a loop
would ask for an older window, be handed the newest page again, and either spin
or return the newest bars believing they were the oldest.

Other things about that endpoint:

- A row is `[open time in SECONDS, open, high, low, close, vwap, volume,
  count]`. The close is the **fifth** field, where a KuCoin spot row puts it
  third, and the volume is the seventh, where the sixth is the vwap.
- **The last row is the candle still forming.** The answer's own `last` cursor
  points one interval earlier. Closed bars only are returned.
- The rows are found by being the array rather than by the name beside them:
  the venue echoes back whichever of its spellings the request used, so
  `pair=XBTUSD` comes back keyed `XXBTZUSD` and `pair=BTC/USD` comes back keyed
  `BTC/USD`.
- Bar lengths: 1, 5, 15 and 30 minutes, 1 and 4 hours, daily and weekly. The
  venue refuses 3, 120 and 720 minutes with "EGeneral:Invalid arguments" rather
  than rounding to something near them, so a length it does not keep is refused
  before the request is made. It also serves 21600 minutes - fifteen days -
  which no aggregation here names, so that is not offered.

Spot pays no funding and declares none.

### Execution

| BYTEX order | Kraken spot |
|---|---|
| Market | `AddOrder`, `ordertype: market` |
| Limit | `AddOrder`, `ordertype: limit`, `price` |
| StopMarket | `ordertype: stop-loss`, `price` is the trigger |
| StopLimit | `ordertype: stop-loss-limit`, `price` is the trigger and `price2` the limit |
| MarketIfTouched | `ordertype: take-profit` |
| LimitIfTouched | `ordertype: take-profit-limit` |

`price` means two different things on this platform. On a plain limit order it
is the limit; on a trigger order it is the **trigger**, and `price2` is that
order's limit. With the two swapped the venue takes a perfectly valid order that
triggers at the limit and limits at the trigger, and nothing reports an error.

Post-only is a flag in `oflags`; immediate-or-cancel is a `timeinforce`. A
good-till-cancelled order says nothing about its time in force, because that is
the venue's default and the bytes an order carries are what the venue's own logs
show when something is disputed. An order sized in the quote currency is
refused: this platform sizes in base currency.

**Amending is a real amend.** `/0/private/AmendOrder` keeps the order's identity
and its queue place where it can, so a protective order is never off the book
while it is being resized, and the family declares `amendOrders: true`. An
amend names the order by the venue's id when this node has one and by the client
order id otherwise, which the venue also accepts - so an amend can reach an
order whose acceptance message was missed.

**Cancel-all is a loop, on purpose.** The venue's own `/0/private/CancelAll`
takes no symbol and cancels every open order on the account, while a cancel-all
command always names an instrument. Sending it would close orders on every other
instrument the account holds, so the orders this node holds for that instrument
are cancelled one by one instead.

Events arrive on the private socket, subscribed with a short-lived token from
`/0/private/GetWebSocketsToken` - so a key that cannot sign cannot even listen,
and the token is asked for again before every connection attempt. The
`executions` channel carries the event in `exec_type` and the resulting state in
`order_status`; fills are de-duplicated by `exec_id`. A fill's commission is the
**fee the venue reported**, not the instrument's published rate, because this
account may be on a lower tier than a catalog publishes.

**Leverage is ignored on spot** and the client says so in its log. This is the
cash account; Kraken spot's margin trading is a separate feature and is not
covered, so there is no per-symbol leverage to set.

A spot account holds no positions, so none are reported - an empty list is the
answer rather than a gap.

### Fees

**The venue publishes no per-pair rate.** `/0/public/AssetPairs` carries `fees`
and `fees_maker` arrays for every pair and all 1451 of them measured empty, as
did the `?info=fees` variant. So an instrument is published with the venue's
tier-zero schedule rate, and the family's declared default fees are the same two
constants, so the two cannot drift.

## Futures

### Products and classes

**The contract type does not say whether a contract expires.**
`flexible_futures` covers `PF_XBTUSD`, which never expires, and
`FF_XBTUSD_261225`, which expires in December - 278 perpetuals and 10 dated
contracts under one value. The only field that distinguishes them is
`lastTradingTime`, and that is what the class is read from. Reading the type, or
the `PF_`/`FF_` prefix, would publish a contract that expires as one that never
does: the wrong class, no expiry recorded, and funding assumed on something that
pays none.

**Coin-margined contracts are not offered.** They are quoted in USD and settled
in the base currency, so a quantity of one cannot be expressed in base units
without a price - which is the one thing this adapter promises everything above
it. Twelve of the venue's 300 contracts are `futures_inverse`, perpetual and
dated alike, and the count of what was left out is logged after loading.

A flexible futures size is in base currency directly: `contractSize` is 1 on
every contract measured and `contractValueTradePrecision` gives the step - four
decimals on `PF_XBTUSD`, whole units on `PF_DOGEUSD`.

**The catalog ignores its own symbol filter.** `?symbol=PF_XBTUSD` answered with
all 300 contracts, and `/instruments/PF_XBTUSD` is a 404, so there is no way to
ask for one. Loading a single instrument fetches the catalog and keeps the one
that was asked for; without that, "load this instrument" would load the venue
every time, for every caller, with nothing throwing to say so. (Binance's
USD-margined futures has the same defect.)

### Margin and leverage

**Margin is a schedule, not a number.** Each contract publishes a
`marginLevels` array of tiers that rise with the size of a position:
`PF_XBTUSD` starts at 1% initial and 0.5% maintenance and rises through eight
tiers to 50%; `PI_XBTUSD` starts at 2% and 1% through seven. An instrument is
published with the **first** tier, which is what a position is charged until it
is large enough to move up, and the number of tiers is carried on the instrument
so a reader knows the published figure is the first of several.

The tier threshold has **two different field names**: `numNonContractUnits` on a
flexible futures contract and `contracts` on a coin-margined one. A reader that
knows one name finds no tiers at all on the other kind, and no tiers reads as a
contract that needs no margin.

The maximum leverage a contract allows is the inverse of that first initial
margin - 100x on `PF_XBTUSD`, 50x on `PF_DOGEUSD` - and is carried on the
instrument. It cross-checks against the venue's own ticker, which publishes
`"leverage":"100x"` for `PF_XBTUSD`.

**Leverage and margin mode are one setting here.**
`PUT /derivatives/api/v3/leveragepreferences` sets a per-symbol maximum
leverage, and the **presence** of that preference selects ISOLATED margin for
the symbol while its **absence** selects CROSS. There is no call that does one
without the other. So:

- With no `leverage` configured, nothing is set and the account keeps whatever
  mode and limit it already had.
- With one configured, it is set per instrument before anything trades, and the
  client logs that the symbol has gone into isolated margin - a strategy sized
  for cross margin behaves differently under isolated and nothing else would say
  the mode had changed.
- A figure above what the contract's first tier allows is named in the log
  rather than left to the venue, which would refuse the preference with a
  message nobody can act on.
- A refused preference does not stop the node: usually the account is not
  entitled to the figure asked for, which a node cannot fix, and abandoning the
  start silently would be worse than trading at a leverage a person can read
  about and change.
- A fractional leverage is sent as written. The venue is entitled to accept or
  refuse it; what must not happen is the number being rounded on the way, which
  would silently change the size of every position from the one that was
  backtested.

**What leverage cross margin runs at is not published by Kraken and is not
guessed at here.** The endpoint's documentation says what the presence and
absence of a preference select and does not state the limit that applies with no
preference set. Nothing in this adapter assumes one.

### Fees

**The platform publishes its fees as percentages.** The schedule `PF_XBTUSD`
points at carries `makerFee: 0.02` for two basis points. Taken as written, every
commission on this platform would be a hundred times what the venue charges, so
the figures are divided by 100.

A contract carries the **uid** of its schedule and not its rates, so the adapter
reads `/derivatives/api/v3/feeschedules` - a public endpoint - alongside the
catalog. If the schedules cannot be read the contracts keep the family default
rather than a zero fee: a zero fee would make every backtest over this family
look better than it is, which is the one direction a wrong fee must never go.

### Data

- One socket, `wss://futures.kraken.com/ws/v1`, for both public and private
  feeds - unlike spot. A different protocol from the spot socket, not a
  different channel name: `{event, feed, product_ids}` where spot takes
  `{method, params: {channel}}`.
- **One feed answers four commands.** `ticker` carries the quote, the mark
  price, the index price and the funding rate together, so asking for any of
  them subscribes to it once.
- The funding rate appears **twice** on that feed. `funding_rate` is an absolute
  charge per contract - 0.91 on `PF_XBTUSD` while the actual rate is 0.001% -
  and `relative_funding_rate` is the fraction of notional a position is charged.
  Only the second is a rate, and it is the one published. The venue also gives
  `next_funding_rate_time`, which most do not, so a position's next charge can be
  priced rather than guessed.
- Timestamps are milliseconds.
- The **trade snapshot arrives newest first**, which is the opposite of the
  order everything downstream expects, so it is reversed before publishing.
- A book delta is one price at a time with the side as a word and a zero
  quantity meaning the level has gone; a delta that arrives before the snapshot
  is dropped rather than used to build a book out of whichever levels moved.
- **A candle's length is part of the feed NAME** - `candles_trade_1m` - so a
  client subscribes to a different feed per bar length. The first message after
  subscribing arrives under the same name with `_snapshot` appended, so a client
  matching the feed name exactly would ignore the first candle of every
  subscription. A candle is stamped by its **OPEN** here, the opposite of the
  spot socket.
- An alert about a feed that does not exist carries no feed name and no request
  id, so nothing can correlate it with what was asked; it is logged.
- The private feeds - `open_orders`, `fills`, `balances`, `open_positions` - are
  opened on the same connection after a signed challenge, not with a REST token.

### History

- Candles come from `/api/charts/v1/trade/{symbol}/{resolution}`, which is a
  **separate service** on the futures host sharing neither the derivatives API's
  envelope nor its error shape: success is `{candles, more_candles}` with no
  wrapper, and a failure is **plain text** with an HTTP 400 ("Invalid
  resolution", "Invalid instrument").
- A row is an **object** - `{time, open, high, low, close, volume}` - where the
  spot row is a flat array, and `time` is the interval's **open in
  milliseconds** where spot is seconds. `from` and `to` are in **seconds**, both
  inclusive by a candle's open.
- **One page is 2000 rows, where the service documents 5000.** A loop written to
  the documented number would ask for 5000, receive 2000 and take the short page
  as the end of the venue's history, so the loop reads `more_candles` - which the
  service sets when it cut the window short - instead of counting rows. It pages
  forward, so a wide window really does come back whole; spot cannot page at all.
- `trade` is the only tick type worth asking for. The service also publishes
  `mark` and `spot` candles and their volume measured zero on every row, which
  would produce a bar whose volume is a lie rather than a bar with no volume.
- Bar lengths: 1, 5, 15 and 30 minutes, 1, 4 and 12 hours, daily and weekly -
  the same nine the socket accepts, probed against both. **No monthly length is
  offered**, because the service matches a resolution case-insensitively and
  therefore accepts `1M` and silently serves one **minute**: a caller asking for
  monthly candles would receive minutes and no error at all.
- Dated contracts return no trade candles: `FI_XBTUSD_261225` answered with an
  empty list.

### Funding

- **The endpoint is `/derivatives/api/v4/historicalfundingrates`, not v3.** The
  documented v3 path answers `404 NOT_FOUND` for every symbol, which reads as an
  unlisted symbol.
- **Settlement is hourly**, not the eight hours most venues use: `PF_XBTUSD`
  answered with 8786 rows one hour apart. A caller assuming eight hours would
  price a perpetual's carry at an eighth of what it is.
- **The endpoint takes no window and has no paging.** It answered the identical
  8786 rows for a request carrying `from` and `to` an hour apart, ignoring both
  silently - so a loop written against those parameters would fetch the same year
  over and over. The window is applied by the adapter after the fetch, and only
  the symbol is sent.
- A year is therefore all the funding history this venue has, which is worth
  knowing before a multi-year backtest of a perpetual prices its carry as free
  before that.
- `relativeFundingRate` is the rate, not the `fundingRate` beside it: the second
  is an absolute charge per contract, 1.33 on `PF_XBTUSD` and 1.5e-10 on
  `PI_XBTUSD` at the same hour, which is not a scale any rate has.

### Execution

| BYTEX order | Kraken futures |
|---|---|
| Market | `sendorder`, `orderType: mkt` |
| Limit | `orderType: lmt`, `limitPrice` |
| Limit, post-only | `orderType: post` |
| Limit, immediate-or-cancel | `orderType: ioc` |
| StopMarket / StopLimit | `orderType: stp`, `stopPrice` |
| MarketIfTouched / LimitIfTouched | `orderType: take_profit`, `stopPrice` |

Post-only and immediate-or-cancel are **order types** on this platform rather
than a flag and a time in force, which is the same intent expressed as a
different value of the same field from its spot sibling.

A stop carries `triggerSignal: mark`, because the mark price is what the venue
liquidates against: a stop watching the traded price could be missed while the
position it guards is liquidated.

**A refusal can arrive inside a successful answer.** The platform answers a
refused order with HTTP 200 and `result: "success"`, and puts the refusal in
`sendStatus.status`. A client reading only the envelope would report an order as
placed that the venue never accepted, and the strategy would hold a position it
does not have. The same is true of an amend and `editStatus.status`.

**Amending is supported**, through `/derivatives/api/v3/editorder`. That is the
one fact about this platform's private surface that is measured rather than
published: asked to edit order `abc` it answered "Invalid UUID string: abc"
**before** it refused the credentials, which a path that does not exist cannot do
- that answers `404 NOT_FOUND` instead.

Cancel-all names the contract, because this platform's `cancelallorders` takes a
symbol where its spot sibling's does not.

The balances come from the **multi-collateral** (`flex`) account. The platform
keeps several accounts under one key - one per coin-margined collateral plus that
one - and answers all of them in one call, so a client taking the first would
publish the balances of a family this adapter does not offer.

An order report adds `unfilledSize` and `filledSize` back together: the venue
publishes what is left and what has filled and **never the original size**, so
taking the unfilled part as the quantity would make every partially filled order
look smaller than it was placed at.

This family holds **positions**, which the spot account does not, and reports
them.

## Error shapes

Three, on one venue, and the adapter reads all three:

| Where | Shape |
|---|---|
| Spot, any endpoint | `{"error":["ECATEGORY:Message"],"result":null}` with **HTTP 200** |
| Futures derivatives API, a platform refusal | `{"result":"error","error":"authenticationError"}` |
| Futures derivatives API, a parser refusal | `{"result":"error","errors":[{"code":11,"message":"..."}]}` |
| Futures charts service | plain text with an HTTP 400 |

Spot answering a refusal with HTTP 200 is why the error array is read whatever
the status says. The futures platform using two different fields for a failure on
the same API was measured within one minute of itself.

Spot's tokens are stable and can be matched on - `EQuery:Unknown asset pair` and
`EQuery:Invalid asset pair` mean the pair is not listed and leave the instrument
provider empty rather than throwing, as every venue here does. The futures
platform has no equivalent: it answers a wrong key, a wrong signature and a clock
that is off with the single word `authenticationError`, so those three cannot be
told apart from outside.

## Not verified against a live account

The public side of both platforms was measured against the live venue. **The
private side of both was built from the venue's published API specification and
tested against a stub venue, and none of it has had a correct credential put
through it** - so everything in this section is a shape to confirm rather than a
shape that is known.

Neither platform validates anything before the credentials, which is why none of
it could be provoked: spot answers `EAPI:Invalid key` first, and futures answers
`authenticationError` for a wrong key, a wrong signature and a wrong clock alike.

Still to be confirmed with a real key, on spot:

- Whether the request signature is right. Four `cl_ord_id` shapes - absent, the
  engine's own, a UUID and a two-character string - all produced the same session
  error over the socket, so **even the shape of a client order id this platform
  accepts is unverified**; there may be a length or character limit that has to
  be enforced before an order is sent.
- The field names and the `exec_type` values of the `executions` channel, and
  which field carries a fill's fee (`fees` is read, with `fee_usd_equiv`
  preferred when present).
- Whether `/0/private/BalanceEx` keys its holdings by asset id, as assumed, and
  publishes `balance` and `hold_trade`.
- Whether `CancelOrder` really accepts a client order id in `txid`, which is what
  lets a cancel reach an order whose acceptance was missed.
- Which order types the platform accepts. Sending `order_type: "nonsense"` over
  the socket was refused with "EGeneral:Order type not supported" before the
  session check in one shape of message and not in another, so the accepted set
  could not be enumerated; the six in the table above are the documented ones.

And on futures:

- Whether the request signature is right - in particular that the path is signed
  with the `/derivatives` prefix **removed**, which is the part of the scheme
  easiest to get wrong.
- Whether the socket challenge answer is right. The challenge itself is measured
  - the venue issues one for any `api_key` without checking it - but a wrong
  answer and a wrong key both produce "Failed to subscribe to authenticated
  feed".
- The field names of the private `open_orders`, `fills`, `balances` and
  `open_positions` feeds.
- Whether the `broker` field is honoured. The venue documents its own futures
  `broker` parameter as available **in pre-production environments only**, so an
  id configured for this platform may simply be ignored; a partner would need to
  confirm that with Kraken. Sending it could not be tested either way, because
  `sendorder` refuses the credentials before it looks at the parameters.
- What leverage applies with no preference set - that is, under cross margin.
  Kraken's own endpoint documentation does not state it.
- Whether one key can be given permissions on both platforms, or whether the two
  are always separate keys. `verify-keys` reports which platform a key works on,
  so this is answerable the moment a key exists.

## Broker programme

Kraken runs one - it calls it the API Partner Program - and publishes the
mechanism: `AddOrder` and `AddOrderBatch` on spot, and `sendorder` on futures,
each take a **`broker`** parameter carrying the partner's own Kraken IIBAN. This
adapter carries it, so the venue declares `brokerTag: orderField` and
`brokerProgramme: carried`.

Because it is a field on the order and not a prefix on the client order id,
nothing about the order's identity changes, so turning an id on is not a
reconciliation question here the way it is on Binance. Untagged is the default
and the untagged request is byte-for-byte what the venue received before the
field existed.

What is not settled is whether the field is **accepted**: see the futures note
above, and a partner id has to be issued by Kraken before any of it earns
anything.
