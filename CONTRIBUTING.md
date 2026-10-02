# Contributing to BYTEX

Thank you for your interest in contributing. This document explains how the
project is organized and what is expected of a contribution.

## Ground rules

- **Original work only.** By submitting a contribution you confirm that you
  wrote it yourself and have the right to license it under the Apache License
  2.0. Do not copy code from other projects, regardless of their license.
- **Determinism is a feature.** Changes to the tradingRuntime, engines, clock, or
  matching logic must preserve deterministic event ordering. A backtest run
  twice must produce identical output.
- **Money is exact.** Never introduce `double`/`float` arithmetic on prices,
  quantities, or balances.
- **A number reads the same on every machine.** Anything that formats or parses
  a number, a date or a duration passes `CultureInfo.InvariantCulture`. `Price`,
  `Quantity`, `Money` and `UnixNanos` already do it for you, so interpolating
  one of those is safe; a bare `decimal` or `double` is not, and
  `string.Create(CultureInfo.InvariantCulture, $"...")` is how the rest of the
  engine writes one. This is not enforced by an analyzer: the repository builds
  with `InvariantGlobalization`, which switches the whole `CA13xx` family off,
  and the current culture in a test IS the invariant one - so a culture bug
  cannot fail an ordinary test. Where output leaves the process, there is a
  hostile-culture test instead (search for `CommaDecimalCulture`), and new
  output of that kind is expected to get one.
- **Every change comes with tests.** A feature is merged together with tests
  that exercise it; a bug fix is merged together with a test that fails
  without the fix. The exceptions are changes nothing can meaningfully test
  (documentation, comments, build scripts), and the pull request says so.
  See [Tests](#tests).
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
4. Add or update tests for what you changed (see [Tests](#tests)).
5. Open a pull request with a clear description of *what* changed and *why*.
   Link the related issue if there is one.

## Tests

Each package under `src/` has a test project under `tests/` with the same name
and a `.Tests` suffix. CI runs all of them on Linux, macOS and Windows for
every pull request.

- **Expected values are derived independently**: by hand, from the venue's
  documentation, or from a plain reference implementation inside the test
  project. A test that asserts whatever the code currently returns protects
  nothing.
- **No network, no wall clock, no sleeping.** Use the test clock, scripted
  market data, recorded payloads and fake clients. Tests that need a venue do
  not belong in the default run: they are gated on an environment variable and
  report SKIP with the reason without it, never a pass. The set that checks the
  recorded payloads against the live venues is run once per release - see
  [docs/integrations/README.md](docs/integrations/README.md).
- **Through the public API.** Tests use what a strategy or adapter author can
  use.
- **A known defect is a skipped test, not a missing one.** If a test exposes a
  bug that the same pull request does not fix, keep the correct expectation,
  mark it `Skip = "BUG: …"` with the issue number, and remove the skip in the
  fix.

## Project structure

| Path | Contents |
|---|---|
| `src/Bytex.Core` | domain model, messaging, clock, cache, portfolio, engines, Strategy SDK, adapter SDK, plugin contract |
| `src/Bytex.Indicators` | technical indicators |
| `src/Bytex.Data` | Parquet data catalog and loaders |
| `src/Bytex.Backtest` | backtest engine, simulated venues, reports |
| `src/Bytex.Live` | trading node, live clock, network infrastructure, sandbox execution |
| `src/Bytex.Persistence.Redis` | optional Redis state persistence |
| `src/Bytex.Persistence.S3` | optional S3-compatible object storage for the data catalog |
| `src/Bytex.Adapters.*` | venue and data-provider adapters |
| `src/Bytex.Cli` | command-line interface |
| `examples/` | example strategies and configurations |
| `tests/` | test projects |
| `bench/Bytex.Benchmarks` | the published benchmarks; `dotnet run -c Release --project bench/Bytex.Benchmarks -- --filter "*"` |
| `api/` | the public surface of every packable library, one file each |
| `docs/` | documentation and design notes |

## The surface files under `api/`

Every public and protected member of every packable library is written down there,
and a test reads the built assemblies and fails when they disagree. A change to a
public API therefore comes with an updated surface file in the same commit - the
failure prints what went and what arrived, which is what the changelog entry has to
describe. From 1.0 the same test is the API freeze. See
[docs/versioning.md](docs/versioning.md).

## Adding anything else

The extension points - strategies, runtimeModules, adapters, execution algorithms,
indicators, simulation modules, margin models, performance statistics - are listed
with their contracts in [docs/extending.md](docs/extending.md).

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
