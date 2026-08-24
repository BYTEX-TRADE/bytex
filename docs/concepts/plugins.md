# Plugins

A plugin is an assembly with one public `IPlugin` implementation. It registers
what it contributes:

```csharp
public sealed class MyPlugin : IPlugin
{
    public string Id => "acme.strategies";
    public string Version => "1.0.0";

    public void Register(IPluginRegistry registry)
    {
        registry.AddStrategyProvider(new RuleGraphStrategyProvider());
        registry.AddDataClientFactory(new AcmeDataClientFactory());
        registry.AddIndicatorFactory(new AcmeIndicatorFactory());
    }
}
```

## Strategy providers

A provider turns an opaque definition into a `Strategy`:

```csharp
public interface IStrategyProvider
{
    string Id { get; }
    IReadOnlyList<StrategyDescriptor> Describe();
    ValidationResult Validate(StrategyDefinition definition);
    Strategy Create(StrategyDefinition definition);
}

public sealed record StrategyDefinition(string ProviderId, string Name, JsonElement Payload, StrategyConfig? Config = null);
```

The engine never looks inside `Payload`. The built-in provider
`bytex.importable` treats `Name` as a type name and `Payload` as the
strategy's config; anything else — rule graphs, scripts, parameter sets — is
a provider's own business. Actors and execution algorithms have matching
provider interfaces.

## Discovery

- **Dependency injection:** `services.AddBytex().AddPlugin<MyPlugin>()`, then
  `provider.BuildPluginRegistry()`.
- **Directory loading:** `PluginLoader.LoadFromDirectory("plugins")` loads
  each subdirectory (or top-level assembly) in an isolated
  `AssemblyLoadContext` that shares only `Bytex.*`, `Microsoft.Extensions.*`,
  and `System.*` with the host, so plugins can carry conflicting dependency
  versions. The CLI's `--plugins` option and `TradingNodeConfig.PluginDirectory`
  use this.
- **Manual:** `registry.AddPlugin(new MyPlugin())`.

Strategies referenced by type name only need their assembly loaded; a plugin
with an empty `Register` is enough to make an assembly discoverable (the
examples project does this).

## Adapters as plugins

Venue integrations register `IDataClientFactory` and
`IExecutionClientFactory` implementations keyed by name (`"BINANCE"`,
`"BYBIT"`, `"TARDIS"`, `"SANDBOX"`). Node configurations refer to the factory
name and supply the factory's config type as JSON.

## Compatibility

`IPlugin`, `IPluginRegistry`, the provider interfaces, and the definition
records are additive-only within a major version. A plugin built against
`Bytex.Core` 0.x loads on any later 0.x.
