# Third-party notices

BYTEX depends on the following third-party packages, each used under its own
license. The list is maintained alongside `Directory.Packages.props`; any new
dependency must be added here before it is merged.

| Package | License | Purpose |
|---|---|---|
| Microsoft.Extensions.* (Configuration, DependencyInjection, Hosting, Logging) | MIT | configuration, composition, logging abstractions |
| Parquet.Net | MIT | Parquet read/write for the data catalog |
| StackExchange.Redis | MIT | optional Redis-backed state persistence |
| System.CommandLine | MIT | command-line interface |
| xunit, xunit.runner.visualstudio | Apache-2.0 | test framework |
| Microsoft.NET.Test.Sdk, coverlet.collector | MIT | test host and coverage |
| BenchmarkDotNet | MIT | performance benchmarks |

The full license texts are available from the respective package pages on
nuget.org and are embedded in the packages themselves.
