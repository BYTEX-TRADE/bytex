# 0006 — Plugin contract

Status: accepted (frozen for 0.x)

## Problem

Strategies are not always C# classes compiled into the user's assembly. A
host application may want to build strategies from stored definitions
(configuration, generated code, a domain-specific description), ship venue
integrations as separately deployed assemblies, or contribute indicators and
execution algorithms. The engine needs a small, stable extension surface for
all of these that does not assume anything about where the definition comes
from.

## Decision

Plugins are ordinary .NET assemblies that expose one `IPlugin` implementation.
The engine discovers plugins through dependency injection (preferred) or by
loading assemblies from a directory (for hosted deployments).

```csharp
public interface IPlugin
{
    string Id { get; }                      // stable, e.g. "vendor.strategies"
    string Version { get; }
    void Register(IPluginRegistry registry);
}

public interface IPluginRegistry
{
    void AddStrategyProvider(IStrategyProvider provider);
    void AddActorProvider(IActorProvider provider);
    void AddExecAlgorithmProvider(IExecAlgorithmProvider provider);
    void AddDataClientFactory(IDataClientFactory factory);
    void AddExecutionClientFactory(IExecutionClientFactory factory);
    void AddIndicatorFactory(IIndicatorFactory factory);
}
```

### Strategy providers

```csharp
public interface IStrategyProvider
{
    string Id { get; }                                         // "vendor.rules", "bytex.importable"
    IReadOnlyList<StrategyDescriptor> Describe();              // what this provider can build
    Strategy Create(StrategyDefinition definition);            // build one instance
    ValidationResult Validate(StrategyDefinition definition);  // without instantiating
}

public sealed record StrategyDefinition(
    string ProviderId,
    string Name,                                               // provider-specific kind or type name
    JsonElement Payload,                                       // opaque to the engine
    StrategyConfig? Config = null);                            // engine-level config (ids, OMS type, ...)

public sealed record StrategyDescriptor(string Name, string DisplayName, string? Description, JsonElement? ParameterSchema);
```

The engine never inspects `Payload`. Whatever a provider needs — a rule
graph, a script, a parameter set — is its own business. The only thing the
engine requires is that `Create` returns a `Strategy` (see 0004).

`IActorProvider` and `IExecAlgorithmProvider` have the same shape with
`Actor` / `ExecAlgorithm` return types.

### Built-in provider

`Bytex.Core` ships `ImportableStrategyProvider` (`Id = "bytex.importable"`):
`Name` is an assembly-qualified type name, `Payload` is deserialised into the
strategy's config type, and the strategy is created through its
`(TConfig)` constructor. This is what node configuration files use to
reference hand-written strategies.

### Node configuration

Trading node and backtest configurations reference strategies by definition:

```json
{
  "strategies": [
    {
      "providerId": "bytex.importable",
      "name": "MyStrategies.EmaCross, MyStrategies",
      "payload": { "instrumentId": "BTCUSDT.BINANCE", "barType": "BTCUSDT.BINANCE-1-MINUTE-LAST-EXTERNAL", "fastPeriod": 10, "slowPeriod": 20 }
    },
    {
      "providerId": "vendor.rules",
      "name": "rule-graph",
      "payload": { "...": "opaque" }
    }
  ]
}
```

### Discovery

```csharp
// Dependency injection (host application)
services.AddBytex()
        .AddPlugin<MyPlugin>();

// Directory loading (hosted deployments)
var plugins = PluginLoader.LoadFromDirectory("plugins/");   // each subfolder: one assembly + its dependencies
```

`PluginLoader` uses an isolated `AssemblyLoadContext` per plugin directory
and shares only `Bytex.Core` (and its dependencies) with the host, so plugin
dependency versions cannot collide with each other.

### Versioning rules

- `IPlugin`, `IPluginRegistry`, the provider interfaces, and
  `StrategyDefinition` are additive-only within a major version.
- A plugin compiled against `Bytex.Core` 0.x loads on any later 0.x.
- Providers that need engine services receive them through the strategy
  itself after registration (`Clock`, `Cache`, ...); they must not capture
  them at `Create` time.

## Alternatives considered

- **Reflection-only discovery (scan all assemblies for `Strategy`
  subclasses).** Kept as the built-in importable provider, but insufficient for
  definitions that are not types.
- **Scripting (Roslyn `.csx`).** Useful but orthogonal; a scripting provider
  can be built on this contract without engine changes.

## Consequences

- Hosts can keep their strategy definitions and tooling entirely private while
  running on the public engine.
- The engine has exactly one strategy model; providers are factories, not a
  second runtime.
