# Bitget

Spot, USDT-margined perpetuals and USDC-margined perpetuals, through one adapter, one
host and one socket path.

Factory name `BITGET`. Three families, declared separately because almost nothing about
them is venue-wide, and readable without a key through `bytex venues --json`.

Every number on this page was measured against the live venue on 2026-09-25. Where a
figure could only come from Bitget's documentation, or could not be checked without an
account, it says so.

## Products

| Family | `productType` | Contracts | Class | Example symbol | Engine id |
|---|---|---|---|---|---|
| `spot` | — | 3,169 (3,163 online) | Spot | `BTCUSDT` | `BTCUSDT.BITGET` |
| `usdt-futures` | `USDT-FUTURES` | 805 | Swap | `BTCUSDT` | `BTCUSDT-PERP.BITGET` |
| `usdc-futures` | `USDC-FUTURES` | 49 | Swap | `BTCPERP` | `BTCUSDC-PERP.BITGET` |

The USDC family leaves the quote out of its symbol — `BTCPERP`, not `BTCUSDCPERP` — so
the engine id adds it back. That holds on all 49 contracts, and `symbol` equals
`baseCoin + quoteCoin` on all 3,169 spot pairs and all 805 USDT contracts.

**Two families hold the same instrument class.** Both futures families are `Swap`, and
they are told apart only by the product type in the request. That is why the declaration
carries a selector as well as an address, and why the cross-venue test that used to
require one class per family now requires such families to differ in their declared
configuration instead.

### Markets the venue has and this adapter does not offer

**`COIN-FUTURES` is not declared, because its contract list is empty** — `data: []` with
a success code. Its ticker endpoint still lists dated contracts such as `BTCUSDU26`, and
asking for one by symbol answers `40309 "The symbol has been removed"`. A family with no
contracts is not a market.

The **plan-order family** — Bitget's triggered orders — is a separate system with its own
placement, cancel, amend and stream channel, all confirmed to exist. **Trigger orders are
refused with a named reason** rather than emulated: a stop that is placed and cannot be
cancelled is worse than one that was never accepted. This is the largest deliberate gap.

## Configuration

Three key parts, all required:

| Variable | What it is |
|---|---|
| `BITGET_API_KEY` | the key |
| `BITGET_API_SECRET` | the secret |
| `BITGET_API_PASSPHRASE` | the passphrase set when the key was created |

The passphrase is sent **in clear** in a header, unlike KuCoin's, which is signed.

The family is selected by `productType` on the client configuration, and a **margin mode**
(`crossed` by default, or `isolated`) is also configuration rather than a per-order
choice — because this venue makes `marginMode` a required field on every derivative
order. A client that did not know it could not place one at all.

## Hosts

| | Address |
|---|---|
| REST | `https://api.bitget.com` |
| Public socket | `wss://ws.bitget.com/v2/ws/public` |
| Private socket | `wss://ws.bitget.com/v2/ws/private` |

**One host and one socket path for everything.** The family is chosen by a string in the
request — `productType` on REST, `instType` on the socket — not by an address.

The websocket keep-alive is the plain text `ping`, answered with plain text `pong` — not
JSON.

### Demo trading

The mechanism is an **`S` prefix on the product type, on the same hosts**:
`SUSDT-FUTURES`, `SCOIN-FUTURES`, `SUSDC-FUTURES`, with S-prefixed symbols
(`SBTCSUSDT`, `SBTCSPERP`). Confirmed by fetching contracts and by subscribing.

`wspap.bitget.com`, the host widely documented for demo trading, **connects and then
refuses every demo subscription** with `30001 ... doesn't exist`. It is not used.

**There is no demo spot market** — asking for `SBTCSUSDT` on the spot endpoint answers
`40034`. The clients refuse that combination at construction rather than connecting to
something that cannot work.

## Candles

The recent and historical endpoints are genuinely different, and only one is usable:

| Endpoint | Cap | 1-minute retention | Forming candle | `startTime` |
|---|---|---|---|---|
| `/market/candles` | 1,000 | full at 30 days, **empty at 6 months** | included | honoured |
| `/market/history-candles` | 200 | answered at 30 days, 6 months and a year | never | **ignored** |

`candles` returns an empty list with a success code beyond about a month, which a paging
loop reads as the start of history. So the adapter uses `history-candles` only.

Three measured behaviours it depends on:

- Timestamps are the bar's **open, in milliseconds**; bars are re-stamped at close.
- `endTime` bounds a candle's **open and excludes it**, so the adapter sends
  `lastOpen + 1`.
- `startTime` is **ignored** — a year-old window with both ends returned 200 rows that
  began before the start — and on spot a request with no `endTime` is refused outright.
  So no start is sent, and `BarWindow.Closed` and `Capped` decide the window.

Row width differs: **spot has 8 columns and derivatives 7**, spot adding a converted
turnover. The first six are identical and index 5 is base volume on both.

### One bar length, three spellings

| Bar | Spot REST | Futures REST | Socket |
|---|---|---|---|
| 1 minute | `1min` | `1m` | `candle1m` |
| 1 hour | `1h` | `1H` | `candle1H` |
| 1 day | `1day` | `1D` | `candle1D` |

The socket spelling is the same on spot and futures and refuses the REST spellings with
`30016`. The sets differ too: **2 hours exists on futures REST and not on spot**, and
**8 hours exists on the socket only**. The venue's own refusal message lists the accepted
granularities and **is wrong** — it omits `3day` on spot and `2H` and `3D` on futures,
all three of which are served.

## Funding

`/api/v2/mix/market/history-fund-rate` has **no time window at all**: it pages by
`pageNo`, newest first. Asking for a page of 200 or 500 **silently returns 100 rows with
a success code**, so the page constant is 100 and the walk never trusts what it asked
for. Funding intervals across the family: 426 contracts at 8 hours, 378 at 4, one at 1.

## Margin and leverage

`/api/v2/mix/market/query-position-lever` takes **one symbol only** — no symbol is
refused, and a comma list is refused. It returns tiers with `level`, a notional range,
`leverage` and `keepMarginRate`.

- `MarginInit` is `1 / tier1.leverage`, which equalled the bulk list's `maxLever` on all
  twelve symbols sampled.
- `MarginMaint` is `tier1.keepMarginRate`.
- The two are **not derivable from each other**: three contracts all capped at 50x carry
  maintenance rates of 1.0%, 1.4% and 1.5%.

So `BTCUSDT` needs 1/150 initial and **0.40% maintenance**, against the 2.5% that two
adapters in this repository used to hard-code — six times too large on the contract most
people trade.

**The cost, stated plainly:** there is no bulk tier endpoint, so listing a whole
derivative family costs one extra request per contract — **805 for `USDT-FUTURES`**, about
80 to 90 seconds at eight concurrent requests against a 10-per-second budget. Correctness
was chosen over speed and the cost appears in the provider's log line rather than being
hidden. Loading one named instrument costs one extra request.

**Leverage is per side.** `set-leverage` takes an optional `holdSide`. Per the venue's
interface, crossed margin holds one figure per contract and takes no side; isolated
margin holds one per side. So the adapter sends one call with no side in crossed mode,
and two calls — long then short, both at the configured figure — in isolated. A single
configured figure cannot express asymmetric leverage, and no second field was invented
for it.

A configured leverage above what the venue grants **refuses the run** rather than being
silently lowered.

## Orders

- **A spot market buy is refused unless the order carries a quote quantity.** Bitget sizes
  a spot market buy in the **quote** currency and offers no field for a base amount;
  converting would need a price the client does not have.
- Spot has no amend. `cancel-replace-order` takes the cancellation and the replacement
  together, so there is no window in which neither order exists — `AmendOrders` is true
  for it on those terms. The venue refuses a replacement reusing the id being cancelled,
  so the engine's id carries a marker and every report translates it back.
- Futures amend in place through `modify-order`.
- Recent trades are **silently capped at 100 rows** — 100, 500, 501 and 1,000 all returned
  exactly 100 with a success code.

## Broker programme

Bitget runs one and **publishes the mechanism**: an `X-CHANNEL-API-CODE` header, carried
outside the signature so an order's identity is untouched. Declared
`BrokerTag.RequestHeader` and `BrokerProgramme.Carried`. Measured to be accepted and
ignored on a request carrying no key, so a wrong code cannot break a request. What remains
is applying for a code.

## Not found

**`40034`** ("Parameter does not exist") and **`40309`** ("The symbol has been removed"),
the same on every endpoint taking a symbol, on all three families. Bitget is the only one
of the eight venues whose families agree with each other about this.

## Rate limits

The tightest published public budget is 10 requests per second, which agrees with the
`x-mbx-used-remain-limit` header counting down from 10. Per-key private limits are
unverified.

## Not verified without an API key

Everything below is Bitget's published interface, tested only against a recorded stub, so
the request shapes are pinned rather than the venue's acceptance of them. Only
`ACCESS-KEY` could be confirmed at all: sending it turns `40006` into `40037`, so the
venue does read that header.

1. **The signing scheme** — the header names and the prehash string
   `timestamp + METHOD + path?query + body` under HMAC-SHA256, base64. The key is checked
   first, so no signature refusal is reachable.
2. **The websocket login** — its signature is over `timestamp + "GET" + "/user/verify"`
   with the timestamp in **seconds** where REST uses milliseconds.
3. **Private channel names** and their `default` instrument placeholder.
4. **Every private message field**, including which spelling of "cancelled" each market
   uses. Both are read, because the two markets are documented differently.
5. **Order body field names and values**, in particular whether `force` is accepted on a
   market order.
6. **Whether `holdSide` is required or rejected in each margin mode.** The crossed and
   isolated split comes from the venue's documentation; the adapter makes it deterministic
   from the configured mode rather than guessing an error code.
7. **Position mode.** This client sends one-way orders. **If the account is in hedge mode
   the venue will refuse them** — the most likely first live failure. Calling
   `set-position-mode` was deliberately not done, because it is an account-wide change
   nobody asked for.
8. **Report and balance shapes**, including the fee detail's two forms and its sign.
9. **The broker rebate itself** — the header is carried and harmless; that a code earns a
   rebate cannot be confirmed without an approved channel code.
10. **The client-order-id length limit**, which Bitget does not publish. The adapter
    imposes no length check rather than inventing a number.
11. **Demo trading itself** — the demo markets' public data was measured; placing an order
    on one was not.
