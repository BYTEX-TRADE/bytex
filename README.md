<p align="center">
  <img src="https://raw.githubusercontent.com/BYTEX-TRADE/bytex/main/docs/assets/banner.jpg" alt="BYTEX: technology in motion, advantage in result" width="100%">
</p>

<p align="center">
  <a href="https://github.com/BYTEX-TRADE/bytex/actions/workflows/ci.yml"><img src="https://github.com/BYTEX-TRADE/bytex/actions/workflows/ci.yml/badge.svg?branch=main" alt="CI"></a>
  <a href="https://github.com/BYTEX-TRADE/bytex/releases"><img src="https://img.shields.io/github/v/release/BYTEX-TRADE/bytex?include_prereleases&label=release" alt="Latest release"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-Apache--2.0-blue" alt="License: Apache-2.0"></a>
  <img src="https://img.shields.io/badge/.NET-10-512BD4" alt=".NET 10">
</p>

# BYTEX

**An event-driven algorithmic trading engine for .NET — one strategy, from backtest to live.**

BYTEX is an open-source trading platform written in C#. It lets you write a
trading strategy once and run it unchanged against historical data, against
live market data with simulated execution, and against real exchange accounts.
The engine is deterministic, asset-class agnostic, and built around a small set
of composable components that are the same in every environment.

> Status: pre-release. APIs may change between minor versions until 1.0, and
> the venue adapters are beta until they complete verification against live
> venues. Backtesting and sandbox trading are fully functional today.

Every commit runs more than 2,600 tests on Linux, Windows and macOS. Known
defects are tracked as [issues](https://github.com/BYTEX-TRADE/bytex/issues),
each with a test that holds the correct expectation; see the
[changelog](CHANGELOG.md) for what each release fixed.

## Why BYTEX

- **Research-to-production parity.** Backtest, sandbox, and live trading run
  the same strategy code on the same engine components. What you tested is what
  trades.
- **Deterministic by design.** A single-threaded kernel processes every event
  in a defined order; a backtest replayed twice produces identical results.
- **No floating-point money.** Prices, quantities, and balances are fixed-point
  `decimal` values with explicit precision.
- **Multi-venue, multi-strategy.** Run several strategies across several venues
  in one process, with a shared portfolio and risk layer.
- **Pluggable integrations.** Exchanges and data providers are adapters built
  on one SDK. Add a venue without touching the engine.
- **Plain .NET.** No scripting layer, no bindings — strategies are C# classes
  with full tooling support.

## Capabilities

| Area | What is included |
|---|---|
| Strategy SDK | `Actor` and `Strategy` base classes with lifecycle, data, order, and position handlers; typed configuration; order factory; cache and portfolio access; clock and timers; indicators |
| Orders | market, limit, stop-market, stop-limit, market-if-touched, limit-if-touched, trailing stops, market-to-limit; GTC / IOC / FOK / GTD / DAY; post-only, reduce-only, iceberg display quantity; OCO / OTO / OUO contingencies; bracket orders |
| Backtesting | simulated venues with a per-instrument matching engine, fill and slippage models, fee models, cash and margin accounts; bar, quote, and trade data; Parquet data catalog; performance reports |
| Live trading | trading node with live clock, exchange adapters, startup reconciliation, sandbox execution (live data, simulated fills), optional Redis state persistence |
| Risk | pre-trade validation of precision, size, notional, rate limits; trading state control |
| Adapters | Binance (spot, USDⓈ-M futures), Bybit (spot, linear perpetuals); Tardis historical data; adapter SDK for new venues |
| Tooling | `bytex` CLI for backtests, live nodes, and catalog management; Docker image; notebook support |

## Roadmap

| Release | Milestone | Highlights |
|---|---|---|
| **0.1** ✅ | **Foundation** | kernel · backtesting · live and sandbox trading · Binance · Bybit · test suite |
| **0.2** | **Strategy Documents** | strategies as data · node catalog · validator · node control channel |
| **0.3** | **Risk** | loss and exposure limits · kill switch · margin and leverage checks |
| **0.4** | **Execution** | order emulation · execution algorithms (TWAP) · continuous reconciliation |
| **0.5** | **Venues** | OKX · Kraken · Bitget · Gate · Hyperliquid |
| **0.6** | **Realism** | funding payments · liquidation model · parameter sweeps |
| **0.7** | **Persistence** | full Redis state · message streaming · cloud storage for the catalog |
| **0.8** | **Markets** | Databento · Interactive Brokers · options · synthetic instruments |
| **0.9** | **Depth** | order-book-level matching · own-order book |
| **1.0** | **Stable** | API freeze · published benchmarks · long-term support policy |

No dates: a release ships when it is ready. Fixes ship continuously as patch
releases ([changelog](CHANGELOG.md)). The same list lives in
[docs/roadmap.md](docs/roadmap.md).

## Quick start

Build from source (NuGet packages are planned; until they are published,
reference the projects directly):

```bash
git clone https://github.com/BYTEX-TRADE/bytex
cd bytex
dotnet build
dotnet run --project examples/Bytex.Examples -- backtest-ema-cross
```

```csharp
public sealed class EmaCrossConfig : StrategyConfig
{
    public required InstrumentId InstrumentId { get; init; }
    public required BarType BarType { get; init; }
    public int FastPeriod { get; init; } = 10;
    public int SlowPeriod { get; init; } = 20;
    public decimal TradeSize { get; init; } = 1m;
}

public sealed class EmaCross : Strategy<EmaCrossConfig>
{
    private readonly ExponentialMovingAverage _fast;
    private readonly ExponentialMovingAverage _slow;

    public EmaCross(EmaCrossConfig config) : base(config)
    {
        _fast = new ExponentialMovingAverage(config.FastPeriod);
        _slow = new ExponentialMovingAverage(config.SlowPeriod);
    }

    protected override void OnStart()
    {
        RegisterIndicatorForBars(Config.BarType, _fast);
        RegisterIndicatorForBars(Config.BarType, _slow);
        SubscribeBars(Config.BarType);
    }

    protected override void OnBar(Bar bar)
    {
        if (!_fast.IsInitialized || !_slow.IsInitialized)
        {
            return;
        }

        var instrument = Cache.Instrument(Config.InstrumentId)!;
        var quantity = instrument.MakeQuantity(Config.TradeSize);

        if (_fast.Value > _slow.Value && Portfolio.IsFlat(Config.InstrumentId))
        {
            SubmitOrder(OrderFactory.Market(Config.InstrumentId, OrderSide.Buy, quantity));
        }
        else if (_fast.Value < _slow.Value && Portfolio.IsNetLong(Config.InstrumentId))
        {
            CloseAllPositions(Config.InstrumentId);
        }
    }
}
```

The same class runs in a `BacktestEngine`, in a sandbox, or in a live
`TradingNode`. Full walkthroughs are in [docs/getting-started](docs/getting-started/).

## Command line

The `bytex` tool runs backtests and trading nodes from JSON configuration
(installable with `dotnet tool install --global Bytex.Cli` once packages are
published; from source, use `dotnet run --project src/Bytex.Cli --`):

```bash
bytex catalog fetch-instruments --path ./catalog --venue BINANCE --quote USDT
bytex catalog import-csv --path ./catalog --file btcusdt-1m.csv --kind bars --instrument BTCUSDT.BINANCE --bar-type BTCUSDT.BINANCE-1-MINUTE-LAST-EXTERNAL
bytex --plugins ./plugins backtest --config examples/configs/backtest-ema-cross.json
bytex --plugins ./plugins run --config examples/configs/sandbox-binance-ema-cross.json
```

Strategies are referenced by type name in JSON and loaded from plugin
assemblies, so one CLI runs any strategy. See [docs/getting-started/cli.md](docs/getting-started/cli.md).

## Repository layout

```
src/Bytex.Core               domain model, messaging, clock, cache, portfolio, engines, Strategy SDK, adapter SDK, plugins
src/Bytex.Indicators         technical indicators
src/Bytex.Data               Parquet data catalog, CSV loaders
src/Bytex.Backtest           simulated venues, backtest engine and node, reports
src/Bytex.Live               trading node, kernel loop, network infrastructure, sandbox execution
src/Bytex.Adapters.*         Binance, Bybit, Tardis
src/Bytex.Persistence.Redis  Redis state persistence
src/Bytex.Cli                the bytex command-line tool
examples/                    example strategies and configurations
tests/                       unit, integration, and performance tests
docs/                        getting started, concepts, integrations, design notes, roadmap
```

## Building from source

Requires the .NET 10 SDK.

```bash
dotnet build
dotnet run --project examples/Bytex.Examples -- backtest-ema-cross
```

## Contributing

Contributions are welcome. Please read [CONTRIBUTING.md](CONTRIBUTING.md)
first. Security issues should be reported as described in
[SECURITY.md](SECURITY.md).

## License

BYTEX is licensed under the [Apache License, Version 2.0](LICENSE).
Third-party components are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Disclaimer

BYTEX is software for building trading systems. It is not investment advice,
and it does not guarantee any trading outcome. Trading financial instruments
involves risk of loss. You are solely responsible for any strategy you run and
any account you connect.
