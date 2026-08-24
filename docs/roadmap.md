# Roadmap

What is planned after the first public release, in rough priority order.
Items reference the [requirements catalog](requirements/catalog.md).

## Next

- **Order emulation** (R3.10) — local triggering of stop and conditional
  orders for venues that do not support them natively, with release as market
  or limit.
- **Execution algorithms** (R3.11) — `ExecAlgorithm` runtime with TWAP as the
  reference implementation.
- **OKX and Kraken adapters** (R11.6, R11.7).
- **Full Redis persistence** (R6.5) — complete cache rehydration including
  market-data windows and actor state.

## Later

- **Order-book-level matching** (R8.9) — L2/L3 simulation with queue position.
- **Margin and leverage risk checks** (R4.7).
- **Continuous reconciliation** (R10.8).
- **Databento adapter** (R11.8) — native client for the DBN format.
- **Synthetic instruments** (R2.5).
- **Cloud object storage for the catalog** (R9.5).
- **Interactive Brokers adapter** (R11.9).

## Out of scope

- A graphical user interface. BYTEX is an engine and SDK; user interfaces are
  built on top of it.
- Market data distribution. Users connect their own data providers and venue
  accounts.
- Distributed execution across processes.
