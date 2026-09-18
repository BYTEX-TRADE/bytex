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

## Global options

| Option | Effect |
|---|---|
| `--log-level` | `Trace`, `Debug`, `Information` (default), `Warning`, `Error` |
| `--plugins` | Directory of plugin assemblies (strategies, adapters, indicators) |
