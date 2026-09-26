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
| R3.10 | Order emulation: stop and if-touched orders held in the engine, triggered on the last, bid/ask, mark or index price, and released to the venue as market or limit orders under the same id | Release |
| R3.11 | Execution algorithms with spawned child orders; `TwapExecAlgorithm` works an order in equal slices at an even pace, with a per-order horizon and interval | Release |
| R3.12 | Modify order (quantity, price, trigger); cancel; cancel-all per instrument/side; batch cancel; query | Release |
| R3.13 | Close position / close all positions helpers with reduce-only | Release |
| R3.14 | External order claims by instrument | Release |
| R3.15 | Own-order book: the node's working orders tracked against the market book | Release |
| R3.16 | A strategy document states the leverage it is written for; a venue that grants less refuses to trade it | Release |

## R4 — Risk

| Id | Requirement | Tier |
|---|---|---|
| R4.1 | Pre-trade checks: instrument known, quantity/price precision and increments, min/max quantity, min/max notional | Release |
| R4.2 | Order rate limiting per time window | Release |
| R4.3 | Trading state: active, reducing, halted; commands denied accordingly | Release |
| R4.4 | Maximum notional per order by instrument | Release |
| R4.5 | Bypass switch for trusted environments | Release |
| R4.6 | Account balance checks for cash accounts (free balance covers order) | Release |
| R4.7 | Margin/leverage checks for margin accounts: an order is judged against the initial margin it needs at the account's leverage, against the balance free of margin already committed | Release |
| R4.8 | Maximum loss per period, as an amount or a percentage of the account's equity; orders that would add are denied, orders that reduce are not | Release |
| R4.9 | Maximum exposure carried at once, as an amount or a percentage of the account's equity, counting the order being judged | Release |
| R4.10 | Caps on how many positions may be open and how many orders may be working, per instrument and across the account | Release |
| R4.11 | One switch that halts a node's trading and leaves it running, releasable, reported in status, heartbeat and view; the limits and caps settable from node configuration, the control channel and the command line | Release |
| R4.12, R4.13 | Taken: pluggable margin models, still to come, and the increment checks R4.1 absorbed. The two rows below were designed as R4.14 and R4.15 and keep those ids | - |
| R4.16 | Each venue's own margin requirement and maximum leverage, read from that venue per instrument rather than assumed, with where the figure came from recorded on the instrument and in the result of a run that traded it - the venue for this contract, the venue for all of them, or the engine | Release |
| R4.14 | The loss limit counts what open positions are down as well as what has been realised, and is watched as prices arrive rather than only when an order is submitted | Release |
| R4.15 | Reaching a loss limit stops the engine trading by itself - orders that add denied, orders that get out allowed - once per period and until a host resumes it, and, where a host asks for it, closes what is open with reduce-only orders | Release |

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
| R5.8 | `ExecAlgorithm` base class: a parent order routed by algorithm id, worked by spawned child orders that carry its id; `TwapExecAlgorithm` is built on it | Release |
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
| R8.2 | High-level `BacktestNode`: run configurations with data from the catalog; batch runs over an instrument list and a period list, in parallel if asked, with a comparable table and a run that failed carried as a line in it | Release |
| R8.3 | Simulated exchange per venue with per-instrument matching on quotes, trades, and bars | Release |
| R8.4 | Bar execution: OHLC path (open, nearer extreme, farther extreme, close) or close only | Release |
| R8.5 | Fill model: probability of fill on limit at touch, probability of slippage | Release |
| R8.6 | Fee model: maker/taker from instrument; fixed per-trade | Release |
| R8.7 | Latency model: base, submit, modify, cancel latencies | Release |
| R8.8 | Cash and margin accounts in simulation; leverage per instrument | Release |
| R8.9 | Order-book-level matching (L2/L3) | Release |
| R8.10 | Support for all release order types and time in force in simulation, without DAY expiry at a session end (R8.26) | Release |
| R8.11 | Deterministic replay; same results across runs and machines | Release |
| R8.12 | Performance statistics: returns, P&L by currency, win rate, expectancy, Sharpe, Sortino, max drawdown, profit factor | Release |
| R8.13 | Reports: orders, fills, positions, account balances as tables; equity curve | Release |
| R8.14 | Backtest results serialisable to JSON | Release |
| R8.15 | Funding payments on perpetuals: a published rate applied to an open position at the price the venue last saw, in the currency the instrument settles in, reported per payment; funding history fetched from the venue and stored in the catalog | Release |
| R8.16 | Liquidation of margin accounts: positions closed by the venue when equity falls below the maintenance margin they require, orders cancelled first, each one reported | Release |
| R8.17 | Per-contract fee model: a fee for every contract traded, the way futures and options venues charge, with its own maker rate where a venue has one | Release |
| R8.19 | Simulation modules: pluggable venue behaviours (for example rollover interest), charging through the venue and reported in the result as what they took and why | Release |
| R8.20 | Parameter sweeps: runs over a grid of values set in a strategy's own payload by path, comparable in one table | Release |
| R8.23 | Guided parameter search: genetic / evolutionary search on top of parameter sweeps | Release |
| R8.24 | Partial fills in the simulator: a fill bounded by the size on offer where it happens - the touch for quote, book and trade data, a share of the volume for a bar - with IOC remainders, iceberg slicing and market-to-limit remainders, and what was bounded reported | Release |
| R8.25 | Additional bar execution modes (tick-size and reveal) | Roadmap |
| R8.26 | DAY time in force expiring at a session end in simulation | Roadmap |
| R8.27 | Explicit parameter sets in a batch: the points to run given as they are rather than crossed, one run each, for a search that chooses where to look next | Release |

## R9 — Data catalog and loading

| Id | Requirement | Tier |
|---|---|---|
| R9.1 | Parquet catalog for instruments, quotes, trades, bars, order book deltas | Release |
| R9.2 | Query by instrument/bar type and time range | Release |
| R9.3 | CSV loaders for bars, quotes, trades with column mapping | Release |
| R9.4 | Catalog CLI: list, import-csv, add-instrument, fetch-instruments | Release |
| R9.5 | Cloud object storage backends | Roadmap |
| R9.7 | Streaming (lazy) reads of catalog queries | Roadmap |
| R9.8 | Catalog CLI `info` command: ranges, row counts and size per data set | Roadmap |
| R9.9 | CSV loaders: headerless files and quoted fields | Roadmap |

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
| R10.8 | Continuous reconciliation: a periodic mass status while the node runs, asking only for what has changed since the last check, skipped while an order is in flight, with what it found counted and reported | Release |
| R10.9 | Node control channel: a local, credential-free channel per node for heartbeats, events, status, view, cancel-all, flatten, stop, halt, release and limits | Release |
| R10.10 | Display prices: quotes subscribed for the instruments a node shows, cached and reported, delivered to no strategy that did not ask for them | Release |
| R10.11 | A paper venue matches against the book its real venue is streaming: the depth subscribed for it, the maintained book matched against, and nothing stored | Release |
| R10.12 | A paper venue says what it is matching orders against - book, quotes or bars - per instrument, in the node status | Release |

## R11 — Integrations

| Id | Requirement | Tier |
|---|---|---|
| R11.1 | Adapter SDK: data client, execution client, instrument provider, factories, network infrastructure | Release |
| R11.2 | Binance spot: instruments, quotes, trades, bars, book deltas; orders; reconciliation | Release |
| R11.3 | Binance USDⓈ-M futures: as above plus mark price, funding | Release |
| R11.4 | Bybit spot and linear perpetuals: instruments, data, orders, reconciliation | Release |
| R11.10 | Bybit inverse and options: coin-margined perpetuals and dated futures sized in the venue's own contracts and settled in the base coin, and USDT-settled option contracts with strike, kind and expiry | Release |
| R11.13 | A broker id on every order an adapter sends, settable per venue, delivered with each adapter rather than retrofitted | Release |
| R11.12 | Bitget, Gate and Hyperliquid: spot and the venues' own contract families - USDT- and USDC-margined perpetuals, dated delivery contracts, and a venue whose only market is perpetuals | Release |
| R11.14 | KuCoin spot: instruments, data, orders, reconciliation | Release |
| R11.15 | KuCoin Futures: perpetual and delivery contracts on the venue's own API - contracts sized by multiplier, linear and inverse, funding, leverage and positions | Release |
| R11.16 | A venue declares its own facts - per venue the name and the broker-tag mechanism, per product family the instrument classes, funding, hosts, key shape, required and ignored configuration, default fees and the datasets it publishes free - readable in-process and over `bytex venues --json` without a client or a key; and an instrument id resolves to the family that holds it, or to a clear answer that none does | Release |
| R11.5 | Tardis historical data: trades, quotes, book snapshots to catalog | Release |
| R11.6 | OKX spot, perpetual swaps and dated futures: one venue with three product families on one host, instruments, data, orders, reconciliation | Release |
| R11.7 | Kraken spot and futures, the futures on their own platform with their own hosts and credentials: instruments, data, orders, reconciliation | Release |
| R11.8 | Databento | Roadmap |
| R11.9 | Interactive Brokers | Roadmap |

## R12 — Tooling and documentation

| Id | Requirement | Tier |
|---|---|---|
| R12.1 | `bytex` CLI: `backtest`, `run`, `catalog` commands | Release |
| R12.2 | Example strategies and configurations | Release |
| R12.3 | Documentation: getting started, concepts, integrations, design notes, requirements catalog | Release |
| R12.4 | Published roadmap | Release |
| R12.5 | NuGet packages per component; CLI as a .NET tool | Release |
| R12.9 | Reference backtests: published configurations with the numbers they produce, compared to eight places on every commit | Release |

## R13 — Strategy documents

| Id | Requirement | Tier |
|---|---|---|
| R13.1 | JSON strategy document: instruments, bar types, parameters, typed node graph, phases, transitions | Release |
| R13.2 | Node catalog with typed ports, parameter schemas, plain-language faces; exported as JSON and as a JSON Schema for a whole document | Release |
| R13.3 | Three-layer validator with reason codes (structural, semantic, context) | Release |
| R13.4 | `DocumentStrategy` runtime: deterministic bar-close evaluation, phase machine, decision events, state save/load | Release |
| R13.5 | `bytex.document` strategy provider and `bytex documents` CLI (validate, catalog, schema; `examples` ships with the example documents) | Release |
| R13.6 | `Annotation` data type with scoped events; live rule on conditions a model classified | Release |
| R13.7 | Tick-level evaluation | Roadmap |
| R13.8 | Custom node types from plugins | Release (contract) / Roadmap (examples) |
| R13.9 | `data.position` node: average entry, quantity, side, unrealised in percent and in R, adds, bars held, last exit | Release |
| R13.10 | `risk.exit` re-prices its stop and target from the average entry when the position is added to | Release |
| R13.11 | `act.dca` safety-order ladder anchored at the entry fill, each step wider and larger than the last, capped in total | Release |
| R13.12 | `act.grid` works a price range level by level, each level arming itself again after its round trip | Release |
| R13.13 | Every choice of an enum parameter carries its own name and sentence, and a choice that decides how the number beside it is read names that number and its unit | Release |
| R13.14 | `ind.math`: a value computed from others - plus, minus, times, divide - against a second series or a constant | Release |
| R13.15 | A ceiling on sizing, in the quote currency or as a share of the free balance, applied after the mode has computed a quantity and reported when it holds an order back | Release |
| R13.16 | `act.order`: a `work` parameter that hands the order to an execution algorithm - TWAP, over a horizon at an interval - with the algorithm registered for the document that asks for it, and the node reporting the worked order as the one order it is | Release |
