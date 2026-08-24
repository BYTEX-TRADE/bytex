# Contributing to BYTEX

Thank you for your interest in contributing. This document explains how the
project is organized and what is expected of a contribution.

## Ground rules

- **Original work only.** By submitting a contribution you confirm that you
  wrote it yourself and have the right to license it under the Apache License
  2.0. Do not copy code from other projects, regardless of their license.
- **Determinism is a feature.** Changes to the kernel, engines, clock, or
  matching logic must preserve deterministic event ordering. A backtest run
  twice must produce identical output.
- **Money is exact.** Never introduce `double`/`float` arithmetic on prices,
  quantities, or balances.
- **Design notes for decisions.** Any change that alters a public contract or
  an architectural boundary needs a short note under `docs/design/` describing
  the decision and its rationale.
- **Third-party dependencies** must be added to `Directory.Packages.props` and
  `THIRD-PARTY-NOTICES.md` in the same change. Only permissive licenses (MIT,
  BSD, Apache-2.0, ISC) are accepted.

## Development workflow

1. Fork the repository and create a branch from `main`.
2. Build and test locally:
   ```bash
   dotnet build
   dotnet test
   ```
3. Follow the conventions enforced by `.editorconfig`. The build treats style
   rules as diagnostics; keep the build clean.
4. Open a pull request with a clear description of *what* changed and *why*.
   Link the related issue if there is one.

## Project structure

| Path | Contents |
|---|---|
| `src/Bytex.Core` | domain model, messaging, clock, cache, portfolio, engines, Strategy SDK, adapter SDK, plugin contract |
| `src/Bytex.Indicators` | technical indicators |
| `src/Bytex.Data` | Parquet data catalog and loaders |
| `src/Bytex.Backtest` | backtest engine, simulated venues, reports |
| `src/Bytex.Live` | trading node, live clock, network infrastructure, sandbox execution |
| `src/Bytex.Persistence.Redis` | optional Redis state persistence |
| `src/Bytex.Adapters.*` | venue and data-provider adapters |
| `src/Bytex.Cli` | command-line interface |
| `examples/` | example strategies and configurations |
| `tests/` | test projects |
| `docs/` | documentation and design notes |

## Adding an adapter

Adapters implement the contracts in `Bytex.Core.Adapters` and live in their
own `Bytex.Adapters.<Venue>` project. See
[docs/design/adapter-sdk.md](docs/design/adapter-sdk.md) and the existing
adapters for the expected structure. An adapter must provide an instrument
provider, a data client, and (for trading venues) an execution client with
reconciliation support.

## Reporting bugs

Open an issue with a minimal reproduction. For anything security-related,
follow [SECURITY.md](SECURITY.md) instead of opening a public issue.

## Code of conduct

Be respectful and constructive. Disagreement is fine; personal attacks are not.
Maintainers may remove content or contributors that do not follow this rule.
