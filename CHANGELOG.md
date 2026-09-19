# Changelog

Notable changes to BYTEX, newest first. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow
[Semantic Versioning](https://semver.org/). Until 1.0, a minor version may
change public APIs; a patch version only fixes.

## [Unreleased]

### Fixed

- HTTP retries: a request with a body (POST, PUT, DELETE) was never actually
  retried on a 5xx or 429 answer, because the first attempt disposed the
  content. Every attempt now gets a content of its own, so order, cancel and
  amend calls are resent intact (#13).
- Binance USD-M futures: klines, mark prices and aggregated trades were never
  delivered. The venue serves them on a separate `/market` route; the data
  client now opens the `/public` and `/market` routes and sends each
  subscription to the one that serves it (#7).
- Binance spot: a cancel confirmation was applied to the id of the cancel
  request instead of the order it cancelled, so the order stayed open in the
  engine. Binance futures: a `TAKE_PROFIT` order, the limit take-profit, was
  reported back as a market one (#14).

### Changed

- The requirements catalog now marks as Roadmap what the engine does not do
  yet: increment checks in the risk engine (R4.13), partial fills (R8.24),
  extra bar execution modes (R8.25), DAY expiry in simulation (R8.26),
  streaming catalog reads (R9.7), the catalog `info` command (R9.8), and
  headerless or quoted CSV (R9.9).

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
