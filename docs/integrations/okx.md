# OKX

Spot, perpetual swaps and dated futures, through one adapter and one host.

Factory name `OKX`. Three families, declared separately because almost nothing about
them is venue-wide, and readable without a key through `bytex venues --json`.

Every number on this page was measured against the live venue on 2026-09-25. Where a
figure could only come from OKX's documentation, or could not be checked without an
account, it says so.

## Products

| Family | Instruments | Class | Pays funding | Example |
|---|---|---|---|---|
| `spot` | 1,415 | Spot | no | `BTC-USDT` |
| `swap` | 477 linear | Swap | yes | `BTC-USDT-SWAP` |
| `futures` | 232 linear | Future | no | `BTC-USD_UM-261030` |

### Markets the venue has and this adapter does not offer

**Inverse contracts** — 15 swaps and 12 dated futures. They are quoted in USD and settled
in the base currency, so a quantity cannot be published in base units without a price.
Excluded deliberately, and counted in the log line after loading rather than passed over
in silence.

**Options** — the same endpoint and the same record shape, so they would parse into
something plausible and be wrong. Left out rather than guessed at.

**No USDT-margined dated futures exist.** They are commonly assumed to, and this adapter
was first written expecting `BTC-USDT-250926`. All 244 dated contracts were checked: the
linear ones settle in USD and are spelled `BTC-USD_UM-261030`; 216 of them are five-year
`*_UM_XPERP` products, including tokenised equities such as `AAPL-USD_UM_XPERP`.

## Configuration

Three key parts, all required:

| Variable | What it is |
|---|---|
| `OKX_API_KEY` | the key |
| `OKX_API_SECRET` | the secret |
| `OKX_API_PASSPHRASE` | the passphrase set when the key was created |

The family is selected by `instrumentType` on the client configuration — `spot`, `swap`
or `futures`. Nothing in the address distinguishes them, which is why the declaration
carries the selector and a host reads it rather than inferring a market from a symbol.

Two details of the signing are unlike every other venue here, and both are named
constants in the adapter rather than inline strings:

- the REST timestamp is **ISO-8601 to milliseconds**, where every other venue in this
  repository signs a Unix millisecond count;
- the websocket login signs **Unix seconds**. So one venue uses two timestamp formats.

The passphrase travels in clear in a header, unlike KuCoin's, which is signed.

## Hosts

| | Address |
|---|---|
| REST | `https://www.okx.com` — one host for all three families |
| Socket | `wss://ws.okx.com:8443`, paths `/ws/v5/public`, `/ws/v5/private`, `/ws/v5/business` |

**The data client opens two sockets.** `candle1m` is refused on the public path with
`60018 "wrong URL or channel"` and served on `/ws/v5/business`; quotes and trades are the
other way round. A client opening one would have prices flowing and bars silently absent,
which is why both are opened at connect.

The websocket keep-alive is the plain text `ping`, answered with plain text `pong` — not
JSON.

### Demo trading

The demo environment is the **same REST host** with the header
`x-simulated-trading: 1`, and a **different socket host**, `wss://wspap.okx.com:8443`.
Confirmed by connecting and subscribing there.

A family declares one socket base, so the demo socket address has nowhere to live in the
declaration: it is reached by setting `baseUrlWs` on the client configuration. That is a
gap in the declaration rather than in this venue, and it is recorded as one.

## Candles

- Timestamps are the bar's **open, in milliseconds**; bars are re-stamped at close before
  anything above the adapter sees them.
- Rows arrive **newest first**, nine fields, with a `confirm` flag — `"1"` closed, `"0"`
  still forming. Both endpoints return the forming candle and `BarWindow.Closed` drops it.
- `after` means **older than** the timestamp and `before` means newer, which is the
  opposite of what the words suggest. Both together bound a window.
- Granularities: `1s 1m 3m 5m 15m 30m 1H 2H 4H 6H 12H 1D 1W 1M` and `utc` variants.
  **`8H` is refused** with `51000`, though KuCoin serves it.

### Only one of the two candle endpoints is usable

| Endpoint | Page cap | Deep history |
|---|---|---|
| `/market/candles` | 300 | **0 rows, code 0, HTTP 200** at 2, 30 and 200 days back |
| `/market/history-candles` | 300 (documented 100) | 300 rows at every depth tried |

The first endpoint's end-of-history is **silent**: a successful, empty answer that a
paging loop reads as "no more data". So the history helper uses `history-candles` only,
which covers recent and old alike and leaves no seam between them.

### Volume is not the same unit on every family

On spot, field 5 is base currency. On swaps and futures, field 5 is **contracts** and
field 6 is the base-currency amount. Verified: one minute of `BTC-USDT-SWAP` was 3,498.64
contracts against 34.9864 BTC — exactly a hundredth. An adapter reading field 5 on a
derivative would publish a volume a hundred times too large.

## Funding

`/public/funding-rate-history`, newest first, `fundingTime` in milliseconds. Each row
carries both `fundingRate`, which is predicted, and `realizedRate`, which was charged.
**This adapter publishes the realized one**, because a backtest charging the prediction
charges something that never happened.

Served 200 rows when 200 were asked for, against a documented maximum of 100. Asking for
300 returned 286, which is the retention boundary — about 95 days — rather than a cap.
The page constant is the largest value verified honoured in full.

## Margin and leverage

`GET /public/position-tiers` **requires `instFamily` or `uly`**; `instType` alone is
refused with `50015`, and `instId` is accepted only for margin products. `instFamily`
takes a comma list capped at **five** — a sixth is refused naming the limit — so a full
swap catalog costs about 100 requests.

The tier-one figures are why a per-venue margin constant cannot work:

| Instrument | Initial | Maintenance | Max leverage |
|---|---|---|---|
| `BTC-USDT-SWAP` | 0.01 | 0.004 | 100 |
| `BTC-USD_UM` futures | 0.05 | 0.02 | 20 |

The perpetual and the dated contract **on the same coin on the same day** differ by five
times on both. Instruments publish the venue's own `imr`, `mmr` and `maxLever`; spot
publishes zero and no ceiling, because a cash trade posts no margin.

`imr` happened to equal `1/maxLever` in all three cases measured, but `mmr` is **not**
derivable from it — 0.004, 0.02 and 0.02 against leverages of 100, 20 and 10 — so the
tier fetch is necessary and nothing is inferred from anything else.

A configured leverage above what the venue grants **refuses the run** rather than being
silently lowered to what it allows.

## Orders

`POST /api/v5/account/set-leverage` is sent as `instId` + `mgnMode` + `lever`. No
`posSide`, because this client trades net mode; no `ccy`, because that is the scope for
cross margin on the venue's margin-trading product rather than for derivatives.

A **client order id must be letters and digits only, at most 32 characters**. The engine's
default id contains hyphens, so a strategy left on the default configuration would have
every order refused: the adapter refuses such an id before sending it and names
`UseHyphensInClientOrderIds` in the message. Rewriting the id instead would produce an
order reconciliation could not match, which is worse.

**Triggered orders are refused, not emulated.** OKX keeps them in a separate algo-order
system with its own ids, its own cancel and its own stream channel. A trigger price on a
plain order would be accepted as an ordinary working order with no trigger at all — a
stop that looks armed and is not. Refused with a reason naming why. This is a real
capability gap against KuCoin futures.

### Amendment

`POST /api/v5/trade/amend-order` is one endpoint independent of instrument type, so all
three families declare `AmendOrders`.

## Not found

All three families answer an unlisted instrument identically: **HTTP 200 with code
`51001`**. The same code comes back for an id that exists in a different market — spot
`BTC-USDT` asked for under `instType=SWAP` — so OKX honours its own filter, unlike
Binance's USD-M futures. An unknown `instType` is HTTP 400 with `51000` and is never
treated as "not listed".

## Free data

A daily trade archive, one zip per instrument per day, verified for spot and swaps
(`BTC-USDT-trades-2026-09-23.zip`, 19 MB). **404 for dated futures**, and 404 for the
aggregated-trade and funding paths other venues publish — so the futures family declares
an empty dataset list as a measured statement rather than an omission.

## Rate limits

20 concurrent public requests were answered and the 21st onwards returned HTTP 429, so
the public budget is 20 per 2 seconds.

## Not verified without an API key

Everything below is OKX's published specification, implemented conservatively and flagged
rather than implied. The venue checks the key before anything else, so no signature,
passphrase or parameter refusal can be provoked without a real one.

1. **Order placement** — the `/trade/order` body and the per-order `sCode` inside an
   otherwise successful envelope.
2. **`tgtCcy` on spot market orders.** OKX's documented default for a spot market order is
   the **quote** currency, so a market buy of `0.01` would spend 0.01 USDT rather than buy
   0.01 BTC. This adapter sends `base_ccy` explicitly on every spot order. **This is the
   single most important unverified behaviour here** — if it is wrong, every spot market
   order is the wrong size.
3. **The client-order-id constraint itself** — letters and digits, 32 characters.
4. **Order, account and position websocket payloads** and their field names.
5. **The fee sign.** The venue's `fee` is negated on the documented basis that a charge is
   reported negative. If that is backwards, every commission is credited instead of
   charged.
6. **`amend-order` semantics**, including its refusal when nothing changes.
7. **Demo trading on private endpoints** — the header is verified on a public endpoint
   only.
8. **Default fees** — the published tier-1 schedule; the per-account `/account/trade-fee`
   needs a key.
9. **Position mode.** This client sends no `posSide` and therefore assumes net mode. An
   account in long/short mode will have its orders refused, and `bytex verify-keys`
   warns when it reads that configuration.
