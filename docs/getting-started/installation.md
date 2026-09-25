# Installation

BYTEX targets .NET 10 and runs on Linux, macOS, and Windows (x64 and ARM64).

## Packages

Every release attaches its packages to its
[GitHub release](https://github.com/BYTEX-TRADE/bytex/releases) as `.nupkg`
files: download them and point a local feed at the folder, or build from source
and reference the projects directly. They are not on nuget.org yet; once they
are, add the packages you need to a project:

```bash
dotnet add package Bytex.Core        # engine and Strategy SDK (always)
dotnet add package Bytex.Indicators  # technical indicators
dotnet add package Bytex.Backtest    # backtesting
dotnet add package Bytex.Data        # Parquet catalog and CSV loaders
dotnet add package Bytex.Live        # live trading node, sandbox execution
dotnet add package Bytex.Adapters.Binance
dotnet add package Bytex.Adapters.Bybit
dotnet add package Bytex.Adapters.Gate
dotnet add package Bytex.Adapters.Kucoin
dotnet add package Bytex.Adapters.Tardis
dotnet add package Bytex.Persistence.Redis
```

## Command-line tool

```bash
dotnet tool install --global Bytex.Cli
bytex --help
```

## From source

```bash
git clone <repository-url> bytex
cd bytex
dotnet build
dotnet run --project examples/Bytex.Examples -- backtest-ema-cross
```

## Docker

```bash
docker build -t bytex .
docker run --rm bytex version
docker run --rm -v $(pwd)/data:/data bytex backtest --config /data/run.json
```

The image contains the CLI, the example strategies as a plugin, and the
example configurations under `/app/configs`.

## Credentials

Venue adapters read API keys from configuration or from environment
variables and never log them:

| Venue | Variables |
|---|---|
| Binance | `BINANCE_API_KEY`, `BINANCE_API_SECRET` |
| Bybit | `BYBIT_API_KEY`, `BYBIT_API_SECRET` |
| Gate | `GATE_API_KEY`, `GATE_API_SECRET` |
| Kraken | `KRAKEN_API_KEY`, `KRAKEN_API_SECRET` — but a spot key and a futures key are separate credentials in those same two variables, and neither works on the other platform |
| KuCoin | `KUCOIN_API_KEY`, `KUCOIN_API_SECRET`, `KUCOIN_API_PASSPHRASE` (optional `KUCOIN_API_KEY_VERSION`) |
| Tardis | `TARDIS_API_KEY` |
