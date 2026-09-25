# Gate

Package: `Bytex.Adapters.Gate`. Plugin: `GatePlugin`. Factory name: `GATE`.
Three markets: spot, USDT-settled perpetual contracts, and USDT-settled dated
(delivery) futures. Margin lending and the unified account are not covered.

All three are complete: data, execution, catalog and history.

Everything below was measured against the live venue on 2026-09-25 rather than
read off its documentation, because five of the facts differ from what is
written down and each of the five produces a plausible wrong answer rather than
an error. Where a fact could not be established without an API key, it says so.

## Products

| Product | `productType` | Instrument ids | Covered |
|---|---|---|---|
| Spot | `Spot` (the default) | `BTC_USDT.GATE` | data, execution, catalog, history |
| Perpetual futures | `Futures` | `BTC_USDT.GATE` | data, execution, catalog, history, funding |
| Delivery futures | `Delivery` | `BTC_USDT_20261009.GATE` | data, execution, catalog, history |

An instrument id is the venue's own name, unchanged. Gate writes a pair
`BASE_QUOTE` with an **underscore** - the only venue in this repository that
does; Binance and Bybit write `BTCUSDT` and KuCoin `BTC-USDT` - so the id maps
to the venue symbol and back without a lookup, and a symbol typed the way
another venue spells it is refused with `INVALID_CURRENCY_PAIR`.

A perpetual and a dated contract on the same pair differ only by the date in
the name, and nothing in the adapter reads what an instrument *is* off its
name: the class comes from the endpoint the contract was loaded from and from
the venue's own `type` and `expire_time` fields.

### Markets the venue has and this adapter does not offer

The venue splits its derivatives by settlement currency in the path. Measured:

- `/futures/btc` holds exactly one contract, `BTC_USD`, and the venue reports
  it as `type: inverse` with a `quanto_multiplier` of `0`. An inverse contract
  is quoted in USD and settles in the base currency, so a quantity of one
  cannot be expressed in base units without a price. Not offered.
- `/futures/usd1` holds nine USD1-settled linear perpetuals, which this
  adapter could carry. Not offered because a venue declares each instrument
  class in exactly one family and those nine are perpetual swaps, as the 1013
  USDT-settled ones are.
- `/delivery/btc` answers HTTP 200 with an empty list. The market exists and
  holds nothing.

## Configuration

```json
{ "factory": "GATE", "clientId": "GATE", "config": {
    "productType": "Spot",
    "apiKey": null, "apiSecret": null,
    "instrumentProvider": { "loadIds": ["BTC_USDT.GATE"] }
} }
```

`productType` is `Spot`, `Futures` or `Delivery`, and `Spot` when it is not
given. `bytex venues` prints all three families with the setting that selects
each.

Credentials: `GATE_API_KEY` and `GATE_API_SECRET`. **A Gate key has two parts
and no more** - there is no passphrase and no key version, unlike the three
venues either side of this one. `bytex verify-keys --venue GATE` checks it.

Requests are signed with five lines joined by newlines: the method, the path
*including* its `/api/v4` prefix, the query string exactly as sent, the hex
SHA-512 of the body, and the timestamp **in seconds**. The body's hash is the
hash of the empty string when there is no body, not an empty field.

## Hosts

| Market | REST | WebSocket |
|---|---|---|
| Spot | `https://api.gateio.ws` | `wss://api.gateio.ws/ws/v4/` |
| Perpetual futures | `https://fx-api.gateio.ws` | `wss://fx-ws.gateio.ws/v4/ws/usdt` |
| Delivery futures | `https://fx-api.gateio.ws` | `wss://fx-ws.gateio.ws/v4/ws/delivery/usdt` |

`api.gateio.ws` serves all three markets; `fx-api.gateio.ws` serves only the
two derivative ones and answers 404 for a spot path, which is why it is the
declared default for them.

### Testnet

There is one, and it is not where the venue's older pages still point.
Measured on 2026-09-25:

| | Address | Result |
|---|---|---|
| REST, spot and futures | `https://api-testnet.gateapi.io/api/v4` | works |
| REST, futures (old) | `https://fx-api-testnet.gateio.ws/api/v4` | HTTP 502, dead |
| WebSocket, spot | `wss://ws-testnet.gate.com/v4/ws/spot` | works |
| WebSocket, futures | `wss://ws-testnet.gate.com/v4/ws/futures/usdt` | works |
| WebSocket, futures (old) | `wss://fx-ws-testnet.gateio.ws/v4/ws/usdt` | HTTP 502, dead |

The testnet has no BTC-settled futures (`/futures/btc/contracts` answers
`NOT_FOUND`), and `/delivery/usdt/contracts` answered HTTP 504 on every
attempt. Point a client at it with `baseUrlHttp` and `baseUrlWs`.

## Candles

Every timestamp on this venue is in **seconds** - the row's own stamp, the
`from` and `to` a request carries, a contract's expiry, the signature's
timestamp. Milliseconds in a request are refused with a range error;
milliseconds *read* from a row are refused by nothing and put a 2026 bar in
1970.

A stamp is the candle's **open**; bars here are close-stamped, so one interval
is added. The venue writes a flat zero-volume candle for an interval nothing
traded in - measured on BVOL_USDT, 61 consecutive minutes with no gap - so
unlike KuCoin nothing is filled in.

The row's shape differs per market:

| Market | Row | Volume | Says when a candle closed |
|---|---|---|---|
| Spot | array: `[t, quote volume, CLOSE, HIGH, LOW, OPEN, base volume, window closed]` | base | yes, `w` |
| Perpetual | object `{t, o, h, l, c, v, sum}` | contracts | no over REST, `w` over the socket |
| Delivery | object `{t, o, h, l, c, v}` | contracts | no, on either |

Note spot's order: close before high, and the **open last**, which is the
reverse of where every other venue in this repository puts it.

### Page and window caps

| Market | `limit` max | Widest `from`..`to` | What a wider window does |
|---|---|---|---|
| Spot | 1000 | 999 intervals | refused: "Candlestick range too broad. Maximum 1000 data points" |
| Perpetual | 2000 | 1999 intervals | **answered with the newest 2001 rows, silently** |
| Delivery | 2000 (and refused when `from` or `to` is given) | 1999 intervals | refused, naming `from` |

Both ends of a `from`..`to` window are inclusive, so a span of N intervals is
N+1 rows. The perpetual market's silent truncation is why the adapter sizes
the window itself rather than asking for a period and trusting the answer.

Bar lengths kept, measured: `10s`, `30s`, `1m`, `3m`, `5m`, `15m`, `30m`,
`1h`, `2h`, `4h`, `6h`, `8h`, `12h`, `1d`, `3d`, `7d`. Five of those are not
in the venue's documented list. A length the venue does not keep is refused
with `INVALID_PARAM_VALUE` rather than answered with something close to it.
A month is deliberately unsupported: the venue accepts `30d` and answers rows
**31 days** apart.

## Funding

Only the perpetual contracts are charged funding. A dated contract settles at
expiry, and the venue's ticker channel sends its funding fields as **empty
strings** rather than leaving them out - an empty string parses to zero, so a
rate of nought would otherwise be published as though it had been measured.

`GET /futures/usdt/funding_rate` looks as though `limit` pages it and it does
not. One request answers the **thirty days following `from`**, whatever the
limit says: measured at 90 rows for eight-hourly funding (BTC_USDT), 180 for
four-hourly (ACT_USDT) and 720 for hourly (CXMT_USDT). A loop that stopped
when it received fewer rows than it asked for would read one month and call it
the whole history, so the window is walked forward instead.

The venue keeps **180 days**. An older `from` is refused with "from time
exceeds 180-day limit", so `GateHistory.FetchFundingRatesAsync` refuses it
here, with a sentence, rather than sending a request to be refused there.

Funding intervals measured across the family: 1 h, 4 h and 8 h.

## Margin and risk limits

The venue publishes no initial-margin field on a contract. It publishes the
rate **per risk-limit tier** at the public
`GET /futures/{settle}/risk_limit_tiers`, whose first tier is the one a
position starts in, and it marks `risk_limit_base`, `risk_limit_step` and
`risk_limit_max` on the contract as deprecated in favour of that table.

Measured on five contracts spanning the family - BTC_USDT, ETH_USDT,
ARIA_USDT, AAPL_USDT, XAU_USDT - tier one's `initial_rate` is exactly one over
the contract's `leverage_max`, and tier one's `maintenance_rate` is exactly
the contract's own `maintenance_rate`. So an instrument's margin is published
from the contract already in hand:

- `MarginInit` = 1 / `leverage_max`
- `MarginMaint` = `maintenance_rate`

Both are the venue's own numbers, and both are the **tier-one** figures, which
is what an instrument-level rate can mean: the rate rises with the size of the
position and the tier table is the authority for a large one. Nothing here is
hard-coded - the family's initial margin rates span 0.005 to 0.1.

## Contract size

A derivative order is a whole number of **contracts**, and one contract is
`quanto_multiplier` of the base currency - 0.0001 BTC on BTC_USDT, and
anything from 0.0001 to 10000000 across the family. The instrument's size
increment is published in base currency exactly as Binance and Bybit publish
theirs, and the adapter converts; the venue's own contract size is on the
instrument's `Info` under `contractMultiplier`. A quantity that is not a whole
number of contracts is refused with the reason rather than rounded.

## Orders

**There is no client-order-id field.** The id travels in the order's `text`,
which must start `t-` and carry at most 28 characters after the prefix, from
letters, digits, `_`, `-` and `.`. The venue writes its own values there on
orders it raised itself - `web`, `api`, `liquidation`, `insurance` - so the
prefix is what tells this node's orders from everybody else's on the account.
An id the venue would refuse is refused before the order is sent, with a
sentence naming the rule.

A **derivative** order's `size` is **signed** - positive buys, negative sells -
and there is no side field at all, so an unsigned size would buy where a
strategy meant to sell and the venue would fill it. Spot is the other way
round: a `side` field and an `amount` in base currency.

The venue has **no market order type**. A derivative market order is a limit
order at a price of `0` with `tif: ioc`.

Order types this adapter places: market and limit, on all three markets.
Triggered orders live on the venue's separate price-trigger endpoints with
their own shape and are not placed; one asked for is refused rather than
approximated with a plain order.

| Engine order | Gate |
|---|---|
| Market | `price: "0"`, `tif: "ioc"` (derivatives) / `type: "market"`, `time_in_force: "ioc"` (spot) |
| Limit GTC | `tif`/`time_in_force`: `gtc` |
| Limit IOC | `ioc` |
| Limit FOK | `fok` |
| Limit post-only | `poc` (pending-or-cancelled) |
| Reduce-only | `reduce_only: true` (derivatives) |

### Amendment

This is the one capability that differs between the venue's own markets, and
it is why the delivery clients are separate classes.

| Market | Endpoint | Fields |
|---|---|---|
| Spot | `PATCH /spot/orders/{id}` | `amount`, `price` |
| Perpetual | `PUT /futures/usdt/orders/{id}` | `size`, `price` |
| Delivery | **none** | — |

The venue's delivery surface has no amend endpoint of any kind - no `PUT`, no
`PATCH`, no batch - so `GateDeliveryExecutionClient` refuses an amendment and
says so. A cancel-and-replace there would leave a dated position unguarded for
the length of two requests without the caller having asked for that.

Two of the venue's own rules on amendment are worth knowing: the side cannot
change, and a size at or below what has already filled cancels the order
instead. On spot, reducing the quantity alone keeps the order's place in the
queue while changing the price or growing the quantity moves it to the back of
the new price level.

## Leverage

Leverage is account state, per **contract**, and an order carrying one is
ignored - as on Binance and Bybit - so the configured figure is set at the
venue before anything trades. `POST /futures/{settle}/positions/{contract}/leverage`
takes it as a **query parameter**, not a body field.

Two things here are this venue's own:

- **A contract can hold two positions at once in dual (hedge) mode, and the
  leverage still belongs to the contract rather than to a side.** The venue's
  own announcement of two-side mode says the leverage ratio and the risk limit
  apply to both positions. So one call per contract sets both legs and there is
  nothing per-side to set - unlike Bybit, which takes the two sides separately
  and refuses a one-sided change.
- **`leverage: 0` means cross margin**, with a separate `cross_leverage_limit`
  carrying the ceiling and valid only while `leverage` is zero. A configured
  leverage of zero is therefore refused when the client is built: accepting it
  would put a strategy on cross margin at whatever ceiling the account was last
  left on, which is the failure a configured leverage exists to prevent.

A fractional leverage is sent as written. The venue types the parameter as a
string and documents no integer constraint, and its own risk tiers publish
fractional maximums (`150.01`, `110.01`, `66.67`) - but whether the setter
accepts a fraction is **unverified** without a key.

## Broker programme

Gate runs an API broker programme and pays a rebate on order flow. Its
mechanism is named only on the programme page - an additional channel id,
applied for through a business manager - and the API reference publishes
neither the header's spelling nor its value format. So nothing is carried:
`BrokerTag.None` and `BrokerProgramme.MechanismUndisclosed`. A configured
`brokerId` is ignored rather than refused.

The order `text` field is **not** a broker channel. Its reserved values name
the order's *source* (`web`, `api`, `app`, `liquidation`, …), and a `t-`
prefix is what a user's own id needs, not a rebate claim.

## Rate limits

The venue returns `X-Gate-RateLimit-Limit`, `X-Gate-RateLimit-Requests-Remain`
and `X-Gate-RateLimit-Reset-Timestamp` on every request. Measured: the limit
header answered **200** on both a spot and a futures public endpoint, which
matches the venue's announced 200-per-10-seconds-per-endpoint model and not the
older 900-per-second table still shown on its docs site. The adapter paces at
200 requests per 10 seconds.

Gate also restricts order placement on the perpetual market when an account
sends more than 86,400 order requests in 24 hours with a fill ratio under 1%.
Not exercised here.

## Not verified without an API key

Everything public above was measured. These were not, and are the venue's
documented shapes:

- the private WebSocket channels (`spot.orders`, `spot.usertrades`,
  `spot.balances`, and their `futures.` counterparts) and their auth object;
- that the derivative private channels need the account's numeric user id in
  their payload, which the adapter reads off `GET /futures/{settle}/accounts`;
- every order, fill, position and account response shape;
- that a derivative market order really is a zero-priced `ioc`;
- whether the leverage setter accepts a fractional value;
- whether a single spot amend accepts `price` and `amount` in one call (the
  single-amend model says "either must be specified" and the batch model says
  "only one of them can be");
- what a Gate key's permissions are, because the venue publishes no endpoint
  that lists them - `bytex verify-keys --venue GATE` reads the spot and futures
  accounts instead and says so.
