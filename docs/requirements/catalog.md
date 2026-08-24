# Requirements catalog

Every capability the platform offers or plans to offer, with its delivery
tier. Tiers: **Release** — included in the first public release;
**Roadmap** — designed for, delivered later (see [roadmap.md](../roadmap.md)).

Identifiers are stable and used in design notes, issues, and tests.

## R1 — Platform and runtime

| Id | Requirement | Tier |
|---|---|---|
| R1.1 | Single-threaded deterministic kernel; identical event ordering across runs | Release |
| R1.2 | Three environment contexts on one kernel: backtest, sandbox, live | Release |
| R1.3 | Topic-based message bus with pub/sub, send, request/response | Release |
| R1.4 | Uniform component lifecycle with state machine and fault isolation | Release |
| R1.5 | Clock abstraction with time alerts and repeating timers; test and live implementations | Release |
| R1.6 | Structured logging through `Microsoft.Extensions.Logging`; component-attributed | Release |
| R1.7 | Cross-platform: Linux, macOS, Windows, x64 and ARM64 | Release |
| R1.8 | Configuration from JSON files and environment variables; secrets never logged | Release |
| R1.9 | Docker image with the CLI | Release |
| R1.10 | Notebook support (.NET Interactive example and guide) | Release |

## R2 — Domain model

| Id | Requirement | Tier |
|---|---|---|
| R2.1 | Fixed-point `Price`, `Quantity`, `Money` with explicit precision | Release |
| R2.2 | Nanosecond timestamps with event/init distinction | Release |
| R2.3 | Typed identifiers for venue, symbol, instrument, trader, strategy, client, account, orders, trades, positions | Release |
| R2.4 | Instruments: currency pair, crypto perpetual, crypto future, equity, futures contract, option contract | Release |
| R2.5 | Synthetic instruments (formula over components) | Roadmap |
| R2.6 | Market data: quote tick, trade tick, bar, order book delta/deltas/depth, instrument status, mark/index price, funding rate | Release |
| R2.7 | Custom user data types with subscription by `DataType` | Release |
| R2.8 | Order aggregate with validated state machine and full event history | Release |
| R2.9 | Position aggregate with realized/unrealized P&L, netting and hedging OMS | Release |
| R2.10 | Cash and margin accounts with balances, locked amounts, margins | Release |
| R2.11 | Execution reports for reconciliation (order status, fill, position status, mass status) | Release |

## R3 — Orders and execution

| Id | Requirement | Tier |
|---|---|---|
| R3.1 | Market, limit, stop-market, stop-limit orders | Release |
| R3.2 | Market-if-touched, limit-if-touched | Release |
| R3.3 | Trailing stop market and trailing stop limit (price, basis points, ticks offsets) | Release |
| R3.4 | Market-to-limit | Release |
| R3.5 | Time in force: GTC, IOC, FOK, GTD, DAY, at-the-open, at-the-close | Release |
| R3.6 | Execution instructions: post-only, reduce-only, display quantity (iceberg), quote quantity | Release |
| R3.7 | Contingency lists: OCO, OTO, OUO; bracket order helper | Release |
| R3.8 | Engine-side management of contingent orders for venues without native support | Release |
| R3.9 | Engine-side GTD expiry timers | Release |
| R3.10 | Order emulation (local triggering of stop/limit orders released as market/limit) | Roadmap |
| R3.11 | Execution algorithms with spawned child orders; TWAP reference implementation | Roadmap |
| R3.12 | Modify order (quantity, price, trigger); cancel; cancel-all per instrument/side; batch cancel; query | Release |
| R3.13 | Close position / close all positions helpers with reduce-only | Release |
| R3.14 | External order claims by instrument | Release |

## R4 — Risk

| Id | Requirement | Tier |
|---|---|---|
| R4.1 | Pre-trade checks: instrument known, quantity/price precision and increments, min/max quantity, min/max notional | Release |
| R4.2 | Order rate limiting per time window | Release |
| R4.3 | Trading state: active, reducing, halted; commands denied accordingly | Release |
| R4.4 | Maximum notional per order by instrument | Release |
| R4.5 | Bypass switch for trusted environments | Release |
| R4.6 | Account balance checks for cash accounts (free balance covers order) | Release |
| R4.7 | Margin/leverage checks for margin accounts | Roadmap |

## R5 — Strategy SDK

| Id | Requirement | Tier |
|---|---|---|
| R5.1 | `Actor` with lifecycle hooks, data handlers, subscription/request API, timers, cache and portfolio access | Release |
| R5.2 | `Strategy` with order/position event handlers and trading commands; typed `Strategy<TConfig>` | Release |
| R5.3 | `OrderFactory` for all order types and lists | Release |
| R5.4 | Indicator registration with automatic updates | Release |
| R5.5 | Custom data publish/subscribe and named signals | Release |
| R5.6 | Save/load state hooks | Release |
| R5.7 | Background work helper with result marshalling to the kernel thread | Release |
| R5.8 | `ExecAlgorithm` base class | Roadmap (contract in Release) |
| R5.9 | Plugin contract: strategy/actor/exec-algorithm providers, client factories, directory loading | Release |

## R6 — Cache and portfolio

| Id | Requirement | Tier |
|---|---|---|
| R6.1 | In-memory cache of instruments, bounded windows of quotes/trades/bars, order books, orders, positions, accounts | Release |
| R6.2 | Secondary indexes: orders by venue/instrument/strategy/side/status; positions likewise | Release |
| R6.3 | Exchange-rate lookup across cached quotes for cross-currency valuation | Release |
| R6.4 | Portfolio: balances, locked, margins, realized/unrealized P&L, net exposure, net position | Release |
| R6.5 | Cache persistence to Redis with reload on startup | Release (basic) / Roadmap (full) |

## R7 — Indicators

| Id | Requirement | Tier |
|---|---|---|
| R7.1 | Moving averages: SMA, EMA, WMA, HMA, DEMA, VWAP | Release |
| R7.2 | Momentum: RSI, MACD, ROC, Stochastics | Release |
| R7.3 | Volatility: ATR, Bollinger Bands, Keltner Channel, Donchian Channel | Release |
| R7.4 | Trend: ADX, Aroon | Release |
| R7.5 | Volume: OBV | Release |
| R7.6 | Update from bars, quotes, trades, or raw values; `IsInitialized`; reset | Release |
| R7.7 | Indicator factory registration through plugins | Release |

## R8 — Backtesting

| Id | Requirement | Tier |
|---|---|---|
| R8.1 | Low-level `BacktestEngine`: add venues, instruments, data, actors, strategies; run by time range | Release |
| R8.2 | High-level `BacktestNode`: run configurations with data from the catalog; batch runs | Release |
| R8.3 | Simulated exchange per venue with per-instrument matching on quotes, trades, and bars | Release |
| R8.4 | Bar execution: configurable OHLC path, tick-size/`reveal` modes | Release |
| R8.5 | Fill model: probability of fill on limit at touch, probability of slippage | Release |
| R8.6 | Fee model: maker/taker from instrument; fixed per-trade | Release |
| R8.7 | Latency model: base, submit, modify, cancel latencies | Release |
| R8.8 | Cash and margin accounts in simulation; leverage per instrument | Release |
| R8.9 | Order-book-level matching (L2/L3) | Roadmap |
| R8.10 | Support for all release order types and time-in-force in simulation | Release |
| R8.11 | Deterministic replay; same results across runs and machines | Release |
| R8.12 | Performance statistics: returns, P&L by currency, win rate, expectancy, Sharpe, Sortino, max drawdown, profit factor | Release |
| R8.13 | Reports: orders, fills, positions, account balances as tables; equity curve | Release |
| R8.14 | Backtest results serialisable to JSON | Release |

## R9 — Data catalog and loading

| Id | Requirement | Tier |
|---|---|---|
| R9.1 | Parquet catalog for instruments, quotes, trades, bars, order book deltas | Release |
| R9.2 | Query by instrument/bar type and time range; streaming read | Release |
| R9.3 | CSV loaders for bars, quotes, trades with column mapping | Release |
| R9.4 | Catalog CLI: import, list, info | Release |
| R9.5 | Cloud object storage backends | Roadmap |

## R10 — Live trading

| Id | Requirement | Tier |
|---|---|---|
| R10.1 | `TradingNode` built from configuration: clients, strategies, cache, risk, persistence | Release |
| R10.2 | Live kernel loop on dedicated thread with bounded queue | Release |
| R10.3 | Startup reconciliation with venues (orders, fills, positions) | Release |
| R10.4 | Sandbox execution client: simulated fills on live data | Release |
| R10.5 | Graceful shutdown with optional cancel-all / flatten | Release |
| R10.6 | Reconnection handling and resubscription | Release |
| R10.7 | Health reporting and heartbeat logging | Release |
| R10.8 | Continuous reconciliation (periodic mass status) | Roadmap |

## R11 — Integrations

| Id | Requirement | Tier |
|---|---|---|
| R11.1 | Adapter SDK: data client, execution client, instrument provider, factories, network infrastructure | Release |
| R11.2 | Binance spot: instruments, quotes, trades, bars, book deltas; orders; reconciliation | Release |
| R11.3 | Binance USDⓈ-M futures: as above plus mark price, funding | Release |
| R11.4 | Bybit spot and linear perpetuals: instruments, data, orders, reconciliation | Release |
| R11.5 | Tardis historical data: trades, quotes, book snapshots to catalog | Release |
| R11.6 | OKX | Roadmap |
| R11.7 | Kraken | Roadmap |
| R11.8 | Databento | Roadmap |
| R11.9 | Interactive Brokers | Roadmap |

## R12 — Tooling and documentation

| Id | Requirement | Tier |
|---|---|---|
| R12.1 | `bytex` CLI: `backtest`, `run`, `catalog` commands | Release |
| R12.2 | Example strategies and configurations | Release |
| R12.3 | Documentation: getting started, concepts, integrations, API reference | Release |
| R12.4 | Published roadmap | Release |
| R12.5 | NuGet packages per component; CLI as a .NET tool | Release |
