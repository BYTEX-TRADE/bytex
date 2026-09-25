# Integrations

| Integration | Factory name | Data | Execution | Guide |
|---|---|---|---|---|
| Binance spot and USDⓈ-M futures | `BINANCE` | yes | yes | [binance.md](binance.md) |
| Bybit spot and linear perpetuals | `BYBIT` | yes | yes | [bybit.md](bybit.md) |
| Hyperliquid perpetuals | `HYPERLIQUID` | yes | yes | [hyperliquid.md](hyperliquid.md) |
| KuCoin spot | `KUCOIN` | yes | yes | [kucoin.md](kucoin.md) |
| Tardis historical data | `TARDIS` | historical only | — | [tardis.md](tardis.md) |
| Sandbox (simulated execution on live data) | `SANDBOX` | — | yes | [sandbox.md](sandbox.md) |

Writing a new adapter: see [design note 0005](../design/0005-adapter-sdk.md)
and use `DataClientBase` / `ExecutionClientBase` / `InstrumentProviderBase`
from `Bytex.Core.Adapters` together with the network helpers in
`Bytex.Live.Network` (`WebSocketClient`, `HttpClientWrapper`, `RateLimiter`,
`RetryPolicy`, `HmacSigner`).
