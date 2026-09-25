# Hyperliquid

Package: `Bytex.Adapters.Hyperliquid`. Plugin: `HyperliquidPlugin`. Factory
name: `HYPERLIQUID`. One market: the venue's own perpetual futures, quoted and
settled in USDC. Data, execution, catalog and history are all covered.

Everything below was measured against `api.hyperliquid.xyz` on 2026-09-25 and
is recorded in the adapter's tests. Where something could not be established
without a funded account, it says so.

## What is unlike every other venue here

**The credential is not a key.** There is no API key, no secret and no
passphrase, and nothing to revoke from a settings page. Authentication is a
wallet signature: EIP-712 typed data over a MessagePack encoding of the
action, hashed with Keccak-256 and signed with a secp256k1 private key. The
venue recovers the signer from the signature and is never told the key.

**There are two endpoints and no paths.** Every read is `POST /info` with a
`type` field in the body saying which read; every write is `POST /exchange`
with a signed action. A `GET` on `/info` is refused with 405, so nothing here
can be a cacheable URL.

**An asset is an index.** An order names its asset by position in the
`meta.universe` array, never by a symbol. The index is not published anywhere
else and is not derivable from the coin's name, so it is carried on the
instrument by the provider that read the array.

**Reading an account needs no credential.** Positions, resting orders, fills
and fee tiers are public reads keyed by an address, and so are the order and
fill websocket channels. Only a write needs the key. That is the reverse of
every other venue here, where a report is the thing a key is for.

**There is no hedge mode.** One position per coin, reported as `oneWay` on
every account read, so the account is netting and there is no position id.

## Configuration

```json
{ "factory": "HYPERLIQUID", "clientId": "HYPERLIQUID", "config": {
    "privateKey": null, "accountAddress": null,
    "leverage": null, "crossMargin": true,
    "instrumentProvider": { "loadIds": ["BTC-PERP.HYPERLIQUID"] }
} }
```

Credentials: `HYPERLIQUID_PRIVATE_KEY`, and optionally
`HYPERLIQUID_ACCOUNT_ADDRESS`.

The address matters when the two differ. An **API wallet** the account has
approved signs on the account's behalf and has an address of its own; every
read on this venue is keyed by the **account**, not by whoever signed. With
the address left out, the account is taken to be the address the key itself
controls - right for the account's own wallet key, and silently wrong for an
API wallet, where positions, orders and balances would all be read for an
address that has never traded and every read would succeed and return nothing.
The execution client logs the pair on connecting when they differ.

Use an API wallet. The account's own wallet key can move the funds and there
is nothing that can restrict it; `bytex verify-keys --venue HYPERLIQUID` warns
when it is holding one.

`leverage` is applied per asset, and the same venue action sets the margin
mode - so `crossMargin` is a field of its own rather than being inferred.
Defaulting it silently to isolated would move every position off the shared
collateral it was sized against. An asset whose own maximum is below the
configured leverage is left alone with a warning rather than failing the
connection.

The venue takes a **whole-number** leverage. Measured: `5.5` is refused at
HTTP 422 before the signature is examined, so a fractional `leverage` is
refused when the client is built.

## Test network

`https://api.hyperliquid-testnet.xyz` and
`wss://api.hyperliquid-testnet.xyz`, reached with `baseUrlHttp` and
`baseUrlWs`. Its universe held 212 assets against mainnet's 234.

The network is taken from the host rather than configured separately, because
one letter of the signed typed data says which network it is ("a" for
mainnet, "b" for testnet) and that letter is the whole of the replay
protection between the two. Measured: a mainnet signature sent to the test
network recovers a *different* address, so the two cannot be replayed against
each other - and that only holds if the client cannot be pointed at one
network while signing for the other.

## Instruments

`BTC-PERP.HYPERLIQUID`. The venue calls the asset `BTC` with no suffix at all;
the `-PERP` is what the other venues use, so a perpetual reads as one wherever
it came from.

The universe held 234 assets, 56 of them delisted. **A delisted asset keeps
its index**: MATIC is index 3 and delisted, and DYDX after it is index 4, not
3. An adapter that filtered before numbering would put every order after the
gap on a contract nobody chose.

### Margin, from the venue

`meta` publishes a `maxLeverage` per asset and a table of tiers beside it.
Initial margin is `1 / maxLeverage`; maintenance margin is half of that.

The maintenance half was measured rather than read off a page, against three
live cross positions, each to the last decimal place:

| Notional | `maxLeverage` | Reported maintenance | `notional / (2 x maxLeverage)` |
|---|---|---|---|
| 4937.99391 | 40 | 61.724923 | 61.724923 |
| 368455.20858 | 40 | 4605.690107 | 4605.690107 |
| 377667.0 | 40 | 4720.8375 | 4720.8375 |

The divisor is the asset's **maximum** leverage, not the leverage the position
chose - those accounts were on 6x and 10x. Nothing is hard-coded per venue.

### Price precision has two rules

A price is valid when it has at most `6 - szDecimals` decimal places **and** at
most 5 significant figures. The first is a fact about the asset; the second is
a fact about the price, so the smallest valid step moves as the price moves -
and an instrument carries one increment.

Measured off seven live books:

| Asset | `szDecimals` | Price | Step observed | Rule that binds |
|---|---|---|---|---|
| BTC | 5 | 83697 | 1 | 5 figures |
| ETH | 4 | 2681.6 | 0.1 | 5 figures |
| SOL | 2 | 120.76 | 0.01 | 5 figures |
| HYPE | 2 | 91.257 | 0.001 | 5 figures |
| XRP | 0 | 1.5598 | 0.0001 | 5 figures |
| DOGE | 0 | 0.097007 | 0.000001 | decimals |
| kPEPE | 0 | 0.004418 | 0.000001 | decimals |

The instrument publishes the decimal rule, which is the venue's per-asset
fact. Every price leaving the adapter also goes through
`HyperliquidAsset.RoundPrice`, which applies both, and the execution client
logs it whenever that changes a price - so the one thing the declaration
cannot express is visible in a log rather than silent. An integer price is
left alone: the venue accepts a whole number at any size.

Size precision is `szDecimals` directly, and a size is in base units - there
is no contract to convert through.

## History

Bars: `HyperliquidHistory.FetchBarsAsync`. Funding:
`HyperliquidHistory.FetchFundingRatesAsync`. Both are public and static and
take an HTTP client, so a catalog download needs no node.

Intervals: `1m`, `3m`, `5m`, `15m`, `30m`, `1h`, `2h`, `4h`, `8h`, `12h`,
`1d`, `3d`, `1w`, `1M`. Tried one by one; `2m`, `6h` and `1y` are refused with
a deserialisation error rather than an empty answer. Note that `6h`, which
KuCoin serves, is one of the refusals.

A candle row carries **both ends** of the interval: `t` opens it and `T` is
its last millisecond - 59999 apart on a one-minute bar, not 60000. Bars are
stamped at the close computed from the open plus the interval, so taking `T`
would put every bar one millisecond early.

The **newest row is the candle still forming**. Measured at 17:46:47 UTC, the
newest one-minute row had opened at 17:46:00. `BarWindow.Closed` drops it.

### Candles are retained, not paged

**The venue serves only about the last 5000 bars of an interval, counted from
now rather than from the window asked for.** This is not a page size and no
loop gets past it:

- 100000 one-minute bars ending now → 5159 rows
- 100000 five-minute bars ending now → 5032 rows
- 100000 hourly bars ending now → 5002 rows, contiguous, no duplicates
- the same 6000-hour window ending 1000 hours ago → 4002 rows, starting at the
  same wall-clock moment, about 5002 hours before *now*
- a one-minute window entirely older than that → `[]`, not an error

So an hour of minutes from last month cannot be downloaded from here at any
page size, and a loop walking backwards would stop returning rows and look
exactly like the beginning of history. The adapter makes one request for the
window it can be served and does not page. Daily bars go back to 2020-08-19 -
2229 of them - because 2229 is under the retention count.

A `startTime` far enough in the past to be negative is refused at HTTP 422.

### Funding is complete and pages forward

Unlike the candles. A window three years back answered with real settlements
from 2023-09-26. One request returns at most 500, and it returns the **oldest**
500 of the window - the opposite of what the candle read does with the same
kind of window, so the two cannot share a loop.

Settlement is **hourly** here, where the venues beside it settle every eight
hours. `predictedFundings` publishes this venue's interval as 1 beside their
4s, so a rate off this venue is a charge for one hour and comparing it with a
Binance or Bybit rate as printed is wrong by eight.

A settlement's stamp is a few tens of milliseconds past the hour - measured at
.015, .045, .046 and .068 - never on it, so nothing paging it may compute a
cursor from a boundary.

## Market data

One socket at `wss://api.hyperliquid.xyz/ws`: no connection token, no address
handed out per connection, no per-topic path. A subscription is a JSON message
naming a type and a coin, and the venue echoes back every subscription it
accepted on a `subscriptionResponse` channel. A subscription to something it
does not list is refused on an `error` channel instead, so the echo is the only
confirmation that a stream will ever arrive.

| Engine subscription | Channel |
|---|---|
| Quotes | `bbo` |
| Trades | `trades` |
| Order book | `l2Book` |
| Bars | `candle` |
| Mark price, funding rate | `activeAssetCtx` |

The book is a **complete 20-level snapshot each time it changes**, not a
delta, so nothing keeps a local book and nothing can drift out of step. The
asset context is a snapshot too, carrying the mark price, the oracle price
(published as the index price) and the current hour's funding in one object -
so the mark and funding subscriptions ask for the same thing.

A candle is republished as it forms and never marked closed, so the arrival of
its successor is what closes it. No timer, and no bar the venue never sent.
The candle channel uses field names of its own: its coin is `s`, not `coin`,
and its `v` is a volume where a book level's `sz` is a resting size.

`bbo` carries the two sides as a **two-element array**, not as named fields.

The trade side is the aggressor's, as one letter: `B` means the buyer crossed.

## Execution

### There is no market order

Measured: an order whose type is `{"market":{}}` is refused at HTTP 422 before
the signature is examined. The only two types the venue deserialises are a
limit and a trigger.

So a market order is an immediate-or-cancel limit placed past the far touch.
The adapter reads `l2Book` for the price - one extra request on the path of a
market order - and reaches `HyperliquidVenue.MarketOrderSlippage` (5%) through
the opposite touch. **That number is this adapter's choice, not the venue's**:
the venue has no opinion about it, so there is nothing to measure. It is named
rather than buried so it can be changed.

Other measured shape rules, all refused at HTTP 422 before the signature:

- a price or size as a JSON **number** rather than a string
- `tif` other than `Gtc`, `Ioc` or `Alo` (`Fok` is refused)
- an order id as a string rather than a number
- a fractional or string `leverage`

Post-only is a time in force (`Alo`) rather than a flag, and the venue refuses
an order that would cross rather than repricing it.

### Client order ids

The venue's is a 128-bit number; the engine's is a string of the caller's
choosing, so the engine's cannot travel. What travels is the first 16 bytes of
its SHA-256, and an id coming back is resolved by recomputing that for the
orders the cache holds - which is what makes it survive a restart, where a
table would not.

### Amending

The venue has a `modify` action, and it **replaces** the order rather than
patching a field: price, size, side, time in force and any trigger all travel
again whether they changed or not. It matches by the order's own numeric id, so
an order that has not been given one cannot be amended - the client says so
rather than cancelling and replacing behind the caller's back.

### Nonces

Measured: a repeated nonce is refused with `Invalid nonce: duplicate nonce N`.
A millisecond clock is not enough on its own, so the adapter keeps a monotonic
counter - two orders inside one millisecond would otherwise see the second
refused for a reason that reads like a clock problem.

### The private streams need no authentication

`orderUpdates` and `userEvents` are subscribed to by address, over a socket
that has authenticated nothing. Measured against an arbitrary address.

Two traps. The `userEvents` subscription is acknowledged as `userEvents` and
its messages arrive on a channel called **`user`** - a client switching on the
name it subscribed with receives every message and recognises none. And one
socket may follow at most **15** accounts; the sixteenth subscription comes
back `Cannot track more than 15 total users.`

An account's fills and orders mix families: a spot fill arrives as `@142` and
a builder-deployed exchange's asset as `xyz:CL`. Both were seen on a live
account. The adapter carries the main perpetuals only, so those are skipped.

### Refusals

`/exchange` answers a refused write with **HTTP 200** and the reason in the
body, so the status says nothing. Two refusals mean completely different
things: `Unable to recover signer.` means the digest the adapter built is not
the digest the venue built, which is a fault in the action's encoding; `User
or API Wallet 0x... does not exist.` means the signature was recovered and the
address it named has no account.

`/info` refuses an unknown request type at HTTP 422 with the plain text
`Failed to deserialize the JSON body into the target type` - not JSON at all,
so there is no code to switch on. The adapter's exception carries a message and
no code for that reason.

## Fees

Base tier, read from the venue's own fee schedule with no key: **1.5 bp maker,
4.5 bp taker** on perpetuals (7 bp / 4 bp on spot, which this adapter does not
cover). Only volume moves it.

## Broker programme

The venue runs one. An order may carry a `builder` object naming an address
and a fee in tenths of a basis point; the account has to have approved that
builder's maximum fee with a signed action of its own; and the `referral` read
reports what a builder has earned under `builderRewards`. `maxBuilderFee`
answered `0` for an unapproved pair, with no key, which is how the mechanism
was confirmed published rather than undisclosed.

Nothing here carries it, for the same reason as KuCoin's: the configured
`brokerId` is one string and this needs an address **and** a fee rate bounded
by an approval the user has to have made separately. So the venue declares
`BrokerTag.None` with `BrokerProgramme.NotCarried` - a rebate going unclaimed
rather than a venue with nothing to claim.

## What is not covered

**Spot.** The venue runs a spot market - 330 pairs - and it is not declared,
for two reasons that are both about shape rather than effort. It answers on the
same host, the same path and the same socket as the perpetuals, so a second
family would be identical in every field a host reads and indistinguishable to
the test that checks a family's declared settings really select it. And 329 of
its 330 pairs are named `@1` to `@868` by index, with exactly one - PURR/USDC -
carrying a name.

**Builder-deployed perpetual exchanges.** `perpDexs` lists them; their assets
arrive as `xyz:AAPL`, `xyz:BRENTOIL` and so on, in a namespace of their own with
their own universes. Not declared and not loaded.

**Vaults.** The signature carries a vault byte, and the adapter always writes
the "no vault" zero. Trading a vault would need the address to be configured
and the byte to change - which changes the length of what is hashed, so it
cannot be retrofitted silently.

## What could not be verified without a funded account

The signing itself **was** verified without one: signing an action with a key
nobody has used and reading back the address the venue recovered is a complete
check of the whole pipeline, field order included. It was done for every action
this adapter sends - an order, an order with a trigger, a cancel by id, a
cancel by client id, an amendment and a leverage change - on mainnet and on the
test network, and every one recovered exactly the signing address.

These did not have an account to be tried against:

- Whether `modify` permits a change of **side**. The action's shape is
  accepted, but a resize that silently reverses a position is not worth
  guessing at.
- Whether the venue has really **approved an API wallet**. The `extraAgents`
  read answered `[]` on every live account tried, so its populated shape was
  never seen and is not parsed on trust. `verify-keys` reports which kind of
  key it is holding and stops short of claiming the venue agrees.
- The **order statuses** other than `open`. Only `open` was seen on the live
  stream. The venue cancels an order for several reasons it spells differently,
  so any ending status whose name carries "canceled" closes the order and the
  exact string is logged - the list can be corrected from a log rather than
  from a guess.
- The **request-weight ceiling**. 120 back-to-back `l2Book` reads all answered
  200, so the limit was not reached. The adapter's limiter is a self-imposed
  bound, not a measured cap.
- Whether `updateLeverage` **refuses** a leverage above an asset's published
  maximum. The adapter does not send one.
