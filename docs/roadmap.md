# Roadmap

The releases ahead, in order. No dates: a release ships when it is ready, and
the order may change. Fixes do not wait for a milestone; they ship
continuously as patch releases (see the [changelog](../CHANGELOG.md)).

## 0.1 · Foundation ✅

Released. Deterministic kernel · backtesting · live and sandbox trading ·
Binance · Bybit · test suite on three operating systems

## 0.2 · Strategy Documents

Strategies as data · node catalog · validator · node control channel

## 0.3 · Risk

Loss and exposure limits · kill switch · margin and leverage checks

## 0.4 · Execution

Order emulation · execution algorithms (TWAP) · continuous reconciliation

## 0.5 · Venues

OKX · Kraken · Bitget · Gate · Hyperliquid

## 0.6 · Realism

Funding payments · liquidation model · parameter sweeps

## 0.7 · Persistence

Full Redis state · message streaming · cloud storage for the catalog

## 0.8 · Markets

Databento · Interactive Brokers · options · synthetic instruments

## 0.9 · Depth

Order-book-level matching · own-order book

## 1.0 · Stable

API freeze · published benchmarks · long-term support policy

## Out of scope

- A graphical user interface. BYTEX is an engine and SDK; interfaces are built
  on top of it.
- Market data distribution. Users connect their own data providers and venue
  accounts.
- Distributed execution across processes.
