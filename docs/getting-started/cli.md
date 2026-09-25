# Running from configuration with the CLI

The `bytex` tool runs backtests and trading nodes from JSON files and manages
data catalogs. Strategies are referenced by type name and loaded from plugin
assemblies, so no code changes are needed to run a different configuration.

## Prepare a catalog

```bash
# store instrument definitions from a venue
bytex catalog fetch-instruments --path ./catalog --venue BINANCE --quote USDT

# or add one from a JSON definition
bytex catalog add-instrument --path ./catalog --file btcusdt.json

# import bars from CSV (columns: timestamp,open,high,low,close,volume)
bytex catalog import-csv --path ./catalog --file btcusdt-1m.csv --kind bars \
  --instrument BTCUSDT.BINANCE --bar-type BTCUSDT.BINANCE-1-MINUTE-LAST-EXTERNAL --timestamp-format unix_ms

bytex catalog list --path ./catalog
```

The examples project can write a synthetic catalog for experiments:

```bash
dotnet run --project examples/Bytex.Examples -- write-sample-catalog ./catalog 5000
```

## Run a backtest

`examples/configs/backtest-ema-cross.json`:

```json
{
  "engine": { "runId": "ema-cross-sample", "kernel": { "traderId": "BACKTESTER-001" } },
  "venues": [
    { "venue": "SIM", "accountType": "cash", "startingBalances": ["1000000 USDT", "10 BTC"] }
  ],
  "data": [
    { "catalogPath": "./catalog", "dataKind": "bars", "barType": "BTCUSDT.SIM-1-MINUTE-LAST-EXTERNAL" }
  ],
  "strategies": [
    {
      "providerId": "bytex.importable",
      "name": "Bytex.Examples.Strategies.EmaCross",
      "payload": {
        "strategyId": "EmaCross-001",
        "instrumentId": "BTCUSDT.SIM",
        "barType": "BTCUSDT.SIM-1-MINUTE-LAST-EXTERNAL",
        "fastPeriod": 10, "slowPeriod": 30, "tradeSize": 0.5
      }
    }
  ],
  "outputDirectory": "./reports"
}
```

```bash
bytex --plugins ./plugins backtest --config backtest-ema-cross.json
```

`--plugins` points at a directory of assemblies; each subdirectory holds one
plugin with its dependencies. The built-in `bytex.importable` provider
deserialises `payload` into the strategy's config type and calls its
constructor.

A file may contain an array of run configurations; they are executed in
sequence and each gets its own report directory under `outputDirectory`.

## Run a strategy document

A document is a strategy as data; the engine runs one with no C# at all. The
built-in examples are the quickest way to see it:

```bash
bytex documents examples --out ./documents      # ema-cross, breakout-retest, support-bounce
bytex documents validate --document ./documents/ema-cross.json --catalog ./catalog
bytex backtest --config examples/configs/backtest-document.json
```

`documents validate` reports findings with their reason codes and exits non-zero
when one of them blocks a run; `--catalog` adds the instrument and data-range
checks and `--environment live` applies the live rules. `documents catalog`
prints every node type with its ports and parameters, and `documents schema`
prints the JSON Schema a builder validates against. The configuration names the
document by path:

```json
"strategies": [
  {
    "providerId": "bytex.document",
    "payload": { "documentPath": "./documents/ema-cross.json", "strategyId": "EmaCrossDoc-001", "parameterOverrides": { "fast": 8, "slow": 34 } }
  }
]
```

## Run a trading node

```bash
bytex --plugins ./plugins run --config sandbox-binance-ema-cross.json
```

The node runs until `Ctrl+C`, then cancels orders and/or flattens positions
according to `cancelOrdersOnStop` and `closePositionsOnStop` and disconnects.
See [sandbox and live trading](first-live-node.md) for the configuration.

Venue credentials are read from environment variables. To keep them out of the
launching process, put them in a file and let the node load it itself:

```bash
bytex run --config live.json --env-file keys/bybit.env
```

The file holds `KEY=VALUE` lines; blank lines and lines starting with `#` are
ignored, an `export ` prefix and surrounding quotes are accepted. The node logs
how many variables it loaded, never their names or values. Keep the file
readable only by the user that runs the node.

### Supervising a running node

A node can serve a local control channel, so a supervisor, a dashboard or a
desktop host can watch it and stop it cleanly without holding its credentials:

```bash
bytex run --config live.json --control my-node --control-heartbeat 00:00:05
```

`--control` names a pipe (Windows) or a Unix socket (elsewhere) in the
machine's own namespace; there is no network listener and no credentials on
the channel. The node sends `hello`, `heartbeat`, `event` and `bye`, and
answers `status` and `view` on request. It accepts `cancel-all`, `flatten`,
`stop`, `halt`, `resume` and `limits`, where `stop` carries the same cancel and
close flags as `cancelOrdersOnStop` and `closePositionsOnStop`.
`NodeControlClient` speaks the protocol so a host does not have to. See
[design note 0009](../design/0009-node-control-protocol.md).

## Halt a node, and set what it may risk

`halt` stops a node trading and leaves it running: it keeps its strategies,
its subscriptions and its state, every order it submits is denied, and
`resume` lets it go again. A node can also start that way, which is how a host
looks before anything trades:

```bash
bytex run --config live.json --control my-node --halted
```

What a node may lose, carry and have going at once is set in
`kernel.riskEngine.limits` in the configuration, over the channel with
`limits`, or on the command line, where each option overrides the file:

```bash
bytex run --config live.json   --max-loss "1000 USDT" --loss-period 1.00:00:00 --max-exposure 50%   --max-open-positions 5 --max-open-positions-per-instrument 1   --max-working-orders 50 --max-working-orders-per-instrument 10
```

A limit is an amount with its currency or a share of the account's equity;
orders that would add are denied when one is reached, and orders that reduce
are not. See [risk](../concepts/risk.md).

## Show a moving price

A strategy on bars alone gives a monitor nothing but the last candle close, and
between bars a frozen price reads as a stalled node. `--display-prices`
subscribes quotes for the instruments the node holds, purely to be looked at:

```bash
bytex run --config live.json --control my-node --display-prices
```

They are cached and reported in `status`, `heartbeat` and `view`, and they reach
no strategy that did not subscribe to quotes itself - a strategy is given the
data it asked for and nothing else. Name the instruments with
`displayPriceInstruments` in the configuration; left out, a node takes the
instruments it was given, and a node holding more than 25 of them asks to be
told which to show rather than opening a stream for each. A sandbox node's
venue matches on these quotes as well as on its bars, and says so in its log.

## Check an API key

Before a live node starts, ask the venue what the key is allowed to do. The
command only reads: it places nothing, moves nothing, and never prints the key
or the secret.

```bash
bytex verify-keys --venue BYBIT --env-file keys/bybit.env
bytex verify-keys --venue BINANCE --env-file keys/binance.env --json
```

| Option | Effect |
|---|---|
| `--venue` | `BINANCE`, `BYBIT` or `KUCOIN` |
| `--env-file` | The same `KEY=VALUE` file the node loads |
| `--json` | Print the result as JSON and nothing else on standard output; the log goes to standard error |
| `--timeout` | Seconds the whole check may take, 30 by default; `0` waits for as long as the venue takes |
| `--base-url` | Override the venue's REST address (a proxy or a test venue) |

The plain output is one line per check: the key was accepted, whether it may
trade, whether it may withdraw (a warning: a trading key should not), whether
it is bound to an IP address, and whether the account is readable. The exit
code is `1` when a check failed and `0` otherwise. A read-only key passes; look
at `canTrade`.

With `--json` the result has this shape:

```json
{
  "ok": true,
  "venue": "BYBIT",
  "facts": {
    "keyAccepted": true,
    "canTrade": true,
    "canWithdraw": false,
    "ipRestricted": true,
    "markets": { "spot": true, "futures": true },
    "keyExpiresAt": "2027-01-01T00:00:00Z"
  },
  "failure": null,
  "checks": [ { "status": "ok", "name": "auth", "detail": "key id ••••0661" } ]
}
```

Every fact is `true`, `false` or `null`; `null` means the venue did not say,
never "no" - a venue that has no endpoint for a right reports it as unknown
rather than absent. `facts` is `null` when the check ended before the venue gave
a verdict on the key, and holds `keyAccepted: false` when the venue refused
it.

When a check fails, `failure` names the reason:

```json
{ "code": "bad_key_or_ip", "httpStatus": 401, "venueCode": "-2015", "message": "GET /api/v3/account returned 401: ..." }
```

| `code` | Meaning |
|---|---|
| `env_file_missing` | The environment file does not exist or cannot be read |
| `no_key_in_file` | The file has no key or no secret for this venue; nothing was sent |
| `venue_unknown` | `--venue` is not `BINANCE`, `BYBIT` or `KUCOIN` |
| `bad_key` | The venue does not know the key (Bybit 10003, Binance -2014, KuCoin 400003) |
| `bad_key_or_ip` | Binance -2015: the key, the calling address or the permissions are wrong; the venue does not say which |
| `bad_signature` | The secret does not match the key (Bybit 10004, Binance -1022) |
| `key_expired` | Bybit 33004 |
| `ip_not_allowed` | The key is bound to other addresses (Bybit 10010) |
| `clock_skew` | The machine's clock is outside the venue's window (Bybit 10002, Binance -1021) |
| `permission_denied` | The key is valid but may not read its own account (Bybit 10005, Binance -1002) |
| `geo_blocked` | The venue refuses this location (HTTP 451, a 403 that says so, Bybit 10009 and 10024) |
| `unreachable` | No connection, or no answer within `--timeout` |
| `rate_limited` | HTTP 429 or 418, Bybit 10006 and 10018, Binance -1003 |
| `venue_error` | Any other error answer; `httpStatus` and `venueCode` say which |
| `unknown` | Anything else, for example an answer that is not JSON |

## Global options

| Option | Effect |
|---|---|
| `--log-level` | `Trace`, `Debug`, `Information` (default), `Warning`, `Error` |
| `--plugins` | Directory of plugin assemblies (strategies, adapters, indicators) |
