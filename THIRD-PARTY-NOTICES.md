# Third-party notices

BYTEX depends on the following third-party packages, each used under its own
license. The list is maintained alongside `Directory.Packages.props`; any new
dependency must be added here before it is merged.

| Package | License | Purpose |
|---|---|---|
| Microsoft.Extensions.* (Configuration, DependencyInjection, Hosting, Logging) | MIT | configuration, composition, logging abstractions |
| Npgsql | PostgreSQL | optional PostgreSQL-backed state persistence |
| Parquet.Net | MIT | Parquet read/write for the data catalog |
| StackExchange.Redis | MIT | optional Redis-backed state persistence |
| System.CommandLine | MIT | command-line interface |
| xunit, xunit.runner.visualstudio | Apache-2.0 | test framework |
| Microsoft.NET.Test.Sdk, coverlet.collector | MIT | test host and coverage |
| BenchmarkDotNet | MIT | performance benchmarks |

License information and applicable notice material are available from the
respective package metadata and upstream projects. Packages using SPDX license
expressions do not necessarily embed the full license text.

Npgsql 10.0.3 declares the `PostgreSQL` license expression in its package metadata,
copyright 2025 The Npgsql Development Team. Its upstream repository is
https://github.com/npgsql/npgsql and its declared license is available at
https://licenses.nuget.org/PostgreSQL.
