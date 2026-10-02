# Integrations

| Integration | Factory name | Data | Execution | Guide |
|---|---|---|---|---|
| Binance spot, USDⓈ-M and coin-margined futures | `BINANCE` | yes | yes | [binance.md](binance.md) |
| Bitget spot, USDT- and USDC-margined perpetuals | `BITGET` | yes | yes | [bitget.md](bitget.md) |
| Bybit spot, linear, inverse and options | `BYBIT` | yes | yes | [bybit.md](bybit.md) |
| Gate spot, perpetual and delivery futures | `GATE` | yes | yes | [gate.md](gate.md) |
| Hyperliquid perpetuals | `HYPERLIQUID` | yes | yes | [hyperliquid.md](hyperliquid.md) |
| Kraken spot and futures | `KRAKEN` | yes | yes | [kraken.md](kraken.md) |
| KuCoin spot and futures | `KUCOIN` | yes | yes | [kucoin.md](kucoin.md) |
| OKX spot, perpetual swaps and dated futures | `OKX` | yes | yes | [okx.md](okx.md) |
| Databento historical data | `DATABENTO` | historical only | — | [databento.md](databento.md) |
| Tardis historical data | `TARDIS` | historical only | — | [tardis.md](tardis.md) |
| Sandbox (simulated execution on live data) | `SANDBOX` | — | yes | [sandbox.md](sandbox.md) |

Writing a new adapter: see [design note 0005](../design/0005-adapter-sdk.md)
and use `DataClientBase` / `ExecutionClientBase` / `InstrumentProviderBase`
from `Bytex.Core.Adapters` together with the network helpers in
`Bytex.Live.Network` (`WebSocketClient`, `HttpClientWrapper`, `RateLimiter`,
`RetryPolicy`, `HmacSigner`).

## Checking the recordings against the venues

Every adapter test runs against payloads recorded from the live venue, with no
key and no network (R12.8). That is what makes the suite fast, deterministic and
runnable by anybody - and it leaves one thing it cannot see. A recording is a
photograph: if a venue renames a field, drops one, or starts sending a number as
a string, every offline test goes on passing against the photograph while the
adapter is broken against the venue.

So there is a second, opt-in set that runs **the same providers against the real
venues** and holds them to the same assertions (R12.10):

```bash
BYTEX_LIVE_VENUE_TESTS=1 dotnet test tests/Bytex.Adapters.Tests --filter "FullyQualifiedName~Live"
```

Without that variable they report SKIP with the reason, because a test that
passes when it did nothing reports coverage nobody has.

**Run it once per release, before tagging.** It takes about five minutes over
eight venues, needs no credentials, and is read-only: public listings and public
candles. Nothing in it places, amends or cancels an order - what needs a key and
a live order is a rehearsal against each venue, which is a checklist rather than
a test.

What it asserts is what a parse cannot fake: that a listing has markets in it,
that every market carries the increments and precisions each order is built from,
that a derivative family still publishes what it requires and the ceiling it
grants, and that candles come back oldest-first, one interval apart, stamped at
their close, with extremes that contain the open and the close. A renamed field
does not throw - it parses as zero, null or an empty string, and those are the
assertions that catch it.

Its first run earned its keep three times over: it caught a rate limit this
engine was exceeding on OKX's position-tier endpoint, a wrong host in the test
itself, and three tests hammering one venue in parallel. None of that is visible
to a stub.
