# Roadmap

The releases ahead, in order. No dates: a release ships when it is ready, and
the order may change. Fixes do not wait for a milestone; they ship
continuously as patch releases (see the [changelog](../CHANGELOG.md)).

## 0.1 · Foundation ✅

Released. Deterministic kernel · backtesting · live and sandbox trading ·
Binance · Bybit · test suite on three operating systems

## 0.2 · Strategy Documents ✅

Released. Strategies as data · node catalog · three-layer validator with reason
codes · deterministic runtime · example documents and `bytex documents` · node
control channel

## 0.3 · Risk ✅

Released. Loss and exposure limits and caps on positions and working orders ·
margin checks for margin accounts · one switch that halts a node's trading and
leaves it running · limits set from node configuration, the control channel or
the command line · a moving price for whatever watches a node · the node
catalog with grid and safety-order actions and per-level state

## 0.4 · Execution ✅

Released. Order emulation: stop and if-touched orders triggered in the engine
for venues that have none · execution algorithms with `TwapExecAlgorithm` ·
continuous reconciliation while a node runs · batch runs and parameter sweeps
from one description, compared in one table · a loss limit that counts what open
positions are down, watches it between fills and stops the node when it is
reached · every choice of a node parameter saying what it means, and an
arithmetic node

## 0.5 · Realism ✅

Released. Fills bounded by the size on offer, with what is left going on
working or given up as the order says · funding payments on perpetuals ·
liquidation of margin accounts by the venue · a per-contract fee model ·
published reference backtests run on every commit

## 0.6 · Depth ✅

Released. An order that takes walks the book and pays what each level costs ·
an order that rests waits its turn in the queue at its price, and says where it
stands · a paper venue matched against the book its real venue is streaming,
and saying whether a fill came from a book, a quote or a bar · a margin account
holding the margin its working orders need · leverage that a strategy states
for itself and that changes the size it can take

## 0.7 · Venues ✅

Released. OKX · Kraken · Bitget · Gate · Hyperliquid · KuCoin Futures · Bybit inverse
contracts and options and Binance coin-margined futures, so that the venues
already here arrive with their full capabilities too · a venue declaring its own
facts per product family, readable without a client or a key · each venue's own
margin requirement and maximum leverage read from that venue instead of assumed,
and a result that says which of those it used · a broker id on every order each
adapter sends, delivered with the adapters rather than retrofitted into them

## 0.8 · Persistence

Full Redis state · message streaming · cloud storage for the catalog

## 0.9 · Markets

Databento · Interactive Brokers · options · synthetic instruments

## 1.0 · Stable

API freeze · published benchmarks · long-term support policy

## Out of scope

- A graphical user interface. BYTEX is an engine and SDK; interfaces are built
  on top of it.
- Market data distribution. Users connect their own data providers and venue
  accounts.
- Distributed execution across processes.
