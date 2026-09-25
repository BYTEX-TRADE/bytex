# Tests

One test project per package, named after it:

| Project | Covers |
|---|---|
| `Bytex.Core.Tests` | domain model (primitives, identifiers, instruments, market data, orders, positions, accounts), message bus, clock and timers, cache, portfolio, risk / execution / data engines, bar aggregation, component lifecycle, Strategy SDK |
| `Bytex.Indicators.Tests` | every indicator against an independent reference calculation, warm-up and reset |
| `Bytex.Data.Tests` | Parquet catalog round trips and queries, CSV loaders |
| `Bytex.Backtest.Tests` | order matching per order type, bar execution, fill / fee / latency models, simulated accounts, determinism, statistics and reports |
| `Bytex.Live.Tests` | live kernel loop, sandbox execution, trading node assembly and shutdown, persistence serialisation |
| `Bytex.Adapters.Tests` | Binance, Bybit and Tardis: payload parsing, mappings, request signing, pagination; offline, from recorded payload shapes |
| `Bytex.Cli.Tests` | argument parsing, `--env-file`, configuration loading, catalog commands |

```bash
dotnet test                                                     # everything
dotnet test tests/Bytex.Core.Tests                              # one package
dotnet test --filter "FullyQualifiedName~RiskEngine"            # one subject
```

Rules (the full text is in [CONTRIBUTING.md](../CONTRIBUTING.md#tests)):

- Expected values are derived independently of the code under test: by hand,
  from venue documentation, or from a plain reference implementation in the
  test project.
- No network, no wall clock, no sleeping. The default run is offline and
  deterministic on Linux, macOS and Windows.
- Tests go through the public API.
- A test marked `Skip = "BUG: …"` records a known defect with the correct
  expectation; the fix removes the skip. The current list:
  `grep -rn 'Skip = "BUG' tests`.

How the parts that talk to a venue are tested offline: the adapter and network
tests start a stub venue on `127.0.0.1` (an ephemeral port, plain
`TcpListener`) and point the clients at it through their base-URL settings, so
the real HTTP and WebSocket code paths run and nothing leaves the machine. The
CLI tests run the built `bytex` as a child process with all venue credential
variables removed from its environment.

Package versions for xunit and coverlet are pinned in
`Directory.Packages.props`. Not here yet: integration tests against venue
live venues and BenchmarkDotNet performance benchmarks.
