# Changelog

Notable changes to BYTEX, newest first. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow
[Semantic Versioning](https://semver.org/). Until 1.0, a minor version may
change public APIs; a patch version only fixes.

## [Unreleased]

### Fixed

- Binance USD-M futures: klines, mark prices and aggregated trades were never
  delivered. The venue serves them on a separate `/market` route; the data
  client now opens the `/public` and `/market` routes and sends each
  subscription to the one that serves it (#7).

## [0.1.0] - 2026-09-19

The first tagged release of the engine.

### Included

- Event-driven kernel, domain model, strategy SDK, risk and execution engines
  (`Bytex.Core`).
- Deterministic backtesting with a simulated venue (`Bytex.Backtest`).
- Live and sandbox trading nodes (`Bytex.Live`).
- Venue adapters for Binance and Bybit, and the Tardis data adapter.
- Parquet data catalog and CSV loaders (`Bytex.Data`), indicators
  (`Bytex.Indicators`), Redis persistence, and the `bytex` command line.

### Added

- `bytex run --env-file <path>` loads venue credentials inside the node
  process, so the process that launches a node never reads them (#5).
- Engine test suite: seven test projects run on Linux, Windows and macOS on
  every commit (#8). Tests marked `BUG` are skipped on purpose: each holds the
  correct expectation for a known defect and is enabled by the fix.

### Changed

- `Bytex.Live.ShutdownHelper` is public, so a host that embeds `TradingNode`
  can run the node's own cancel-and-flatten sequence (#3).
- Line endings are normalized through `.gitattributes` (#1).

[Unreleased]: https://github.com/BYTEX-TRADE/bytex/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/BYTEX-TRADE/bytex/releases/tag/v0.1.0
