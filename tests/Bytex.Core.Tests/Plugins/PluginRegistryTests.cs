using System.Text.Json;
using Bytex.Core.Adapters;
using Bytex.Core.Indicators;
using Bytex.Core.Model;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Plugins;
using Bytex.Core.Tests.Support;
using Bytex.Core.Trading;
using Microsoft.Extensions.DependencyInjection;

namespace Bytex.Core.Tests.Plugins;

// Why: R5.9/R7.7 - plugins contribute providers and factories that are resolved by id. The built-in
// "bytex.importable" provider turns a type name plus a JSON payload into a configured strategy.
public class PluginRegistryTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class StubStrategyProvider : IStrategyProvider
    {
        public StubStrategyProvider(string id, ValidationResult? validation = null)
        {
            Id = id;
            Validation = validation ?? ValidationResult.Valid;
        }

        public string Id { get; }

        public ValidationResult Validation { get; }

        public List<StrategyDefinition> Created { get; } = new();

        public IReadOnlyList<StrategyDescriptor> Describe() => [new StrategyDescriptor("stub", "Stub", null, null)];

        public ValidationResult Validate(StrategyDefinition definition) => Validation;

        public Strategy Create(StrategyDefinition definition)
        {
            Created.Add(definition);
            return new ProbeStrategy(definition.Config);
        }
    }

    private sealed class StubIndicatorFactory : IIndicatorFactory
    {
        public StubIndicatorFactory(params string[] names) => Names = names;

        public IReadOnlyList<string> Names { get; }

        public IIndicator Create(string name, IReadOnlyDictionary<string, string> parameters) => new CountingIndicator($"{Names[0]}-family:{name}:{parameters.Count}");
    }

    private sealed class StubDataClientFactory : IDataClientFactory
    {
        public string Name => "Binance";

        public Type ConfigType => typeof(DataClientConfig);

        public IDataClient Create(ClientId clientId, DataClientConfig config, KernelServices services) => throw new NotSupportedException();
    }

    private sealed class StubExecutionClientFactory : IExecutionClientFactory
    {
        public string Name => "Binance";

        public Type ConfigType => typeof(ExecutionClientConfig);

        public IExecutionClient Create(ClientId clientId, ExecutionClientConfig config, KernelServices services) => throw new NotSupportedException();
    }

    private sealed class FakeServiceProvider : IServiceProvider
    {
        private readonly Dictionary<Type, object> _services = new();

        public FakeServiceProvider(PluginRegistry registry, params IPlugin[] plugins)
        {
            _services[typeof(PluginRegistry)] = registry;
            _services[typeof(IEnumerable<IPlugin>)] = plugins;
        }

        public object? GetService(Type serviceType) => _services.GetValueOrDefault(serviceType);
    }

    [Fact]
    public void New_registry_already_knows_the_importable_providers()
    {
        PluginRegistry registry = new();

        Assert.IsType<ImportableStrategyProvider>(registry.StrategyProviders["bytex.importable"]);
        Assert.IsType<ImportableActorProvider>(registry.ActorProviders["bytex.importable"]);
        Assert.Empty(registry.ExecAlgorithmProviders);
        Assert.Empty(registry.Plugins);
    }

    [Fact]
    public void Plugin_registers_its_contributions_once_and_cannot_be_added_twice()
    {
        PluginRegistry registry = new();
        SamplePlugin plugin = new();

        registry.AddPlugin(plugin);

        Assert.Same(plugin, Assert.Single(registry.Plugins));
        Assert.Equal(1, plugin.RegisterCalls);
        Assert.True(registry.StrategyProviders.ContainsKey("sample.strategies"));
        Assert.Throws<InvalidOperationException>(() => registry.AddPlugin(new SamplePlugin()));
    }

    [Fact]
    public void Strategy_is_created_by_the_provider_named_in_the_definition()
    {
        PluginRegistry registry = new();
        StubStrategyProvider a = new("provider.a");
        StubStrategyProvider b = new("provider.b");
        registry.AddStrategyProvider(a);
        registry.AddStrategyProvider(b);
        StrategyDefinition definition = new("provider.b", "anything", Json("{}"), new StrategyConfig { StrategyId = new StrategyId("FromB-001") });

        Strategy strategy = registry.CreateStrategy(definition);

        Assert.Equal("FromB-001", strategy.StrategyId.Value);
        Assert.Empty(a.Created);
        Assert.Same(definition, Assert.Single(b.Created));
    }

    [Fact]
    public void Provider_ids_are_case_sensitive_and_unknown_ids_are_an_error()
    {
        PluginRegistry registry = new();
        registry.AddStrategyProvider(new StubStrategyProvider("provider.a"));

        InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => registry.CreateStrategy(new StrategyDefinition("PROVIDER.A", "x", Json("{}"))));

        Assert.Contains("PROVIDER.A", e.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => registry.CreateActor(new ActorDefinition("nobody", "x", Json("{}"))));
        Assert.Throws<InvalidOperationException>(() => registry.CreateExecAlgorithm(new ExecAlgorithmDefinition("nobody", "x", Json("{}"))));
    }

    [Fact]
    public void Definition_rejected_by_its_provider_is_not_created_and_the_errors_are_reported()
    {
        PluginRegistry registry = new();
        StubStrategyProvider provider = new("strict", ValidationResult.Invalid("period must be positive", "instrument missing"));
        registry.AddStrategyProvider(provider);

        InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => registry.CreateStrategy(new StrategyDefinition("strict", "MyStrategy", Json("{}"))));

        Assert.Contains("MyStrategy", e.Message, StringComparison.Ordinal);
        Assert.Contains("period must be positive; instrument missing", e.Message, StringComparison.Ordinal);
        Assert.Empty(provider.Created);
    }

    [Fact]
    public void Provider_registered_under_an_existing_id_replaces_the_previous_one()
    {
        PluginRegistry registry = new();
        StubStrategyProvider first = new("same");
        StubStrategyProvider second = new("same");
        registry.AddStrategyProvider(first);

        registry.AddStrategyProvider(second);

        Assert.Same(second, registry.StrategyProviders["same"]);
    }

    [Fact]
    public void Client_factories_are_found_by_name_ignoring_case()
    {
        PluginRegistry registry = new();
        StubDataClientFactory data = new();
        StubExecutionClientFactory exec = new();

        registry.AddDataClientFactory(data);
        registry.AddExecutionClientFactory(exec);

        Assert.Same(data, registry.DataClientFactories["BINANCE"]);
        Assert.Same(exec, registry.ExecutionClientFactories["binance"]);
    }

    [Fact]
    public void Indicator_is_created_by_the_first_factory_that_knows_the_name_ignoring_case()
    {
        PluginRegistry registry = new();
        registry.AddIndicatorFactory(new StubIndicatorFactory("SMA", "EMA"));
        registry.AddIndicatorFactory(new StubIndicatorFactory("RSI", "EMA"));

        IIndicator ema = registry.CreateIndicator("ema", new Dictionary<string, string> { ["period"] = "20" });
        IIndicator rsi = registry.CreateIndicator("RSI", new Dictionary<string, string>());

        Assert.Equal("SMA-family:ema:1", ema.Name);
        Assert.Equal("RSI-family:RSI:0", rsi.Name);
        Assert.Throws<InvalidOperationException>(() => registry.CreateIndicator("MACD", new Dictionary<string, string>()));
    }

    [Fact]
    public void Importable_provider_builds_a_typed_strategy_from_its_json_configuration()
    {
        PluginRegistry registry = new();
        StrategyDefinition definition = new(
            ImportableStrategyProvider.ProviderId,
            typeof(TypedProbeStrategy).AssemblyQualifiedName!,
            Json("""{"strategyId":"Typed-009","fastPeriod":21,"instrument":"BTCUSDT.BINANCE","manageGtdExpiry":true}"""));

        TypedProbeStrategy strategy = Assert.IsType<TypedProbeStrategy>(registry.CreateStrategy(definition));

        Assert.Equal("Typed-009", strategy.StrategyId.Value);
        Assert.Equal(21, strategy.Config.FastPeriod);
        Assert.Equal(TestIds.BtcUsdt, strategy.Config.Instrument);
        Assert.True(strategy.Config.ManageGtdExpiry);
    }

    [Fact]
    public void Importable_provider_resolves_a_plain_full_type_name_and_uses_defaults_without_a_payload()
    {
        PluginRegistry registry = new();
        StrategyDefinition definition = new(ImportableStrategyProvider.ProviderId, typeof(TypedProbeStrategy).FullName!, default);

        TypedProbeStrategy strategy = Assert.IsType<TypedProbeStrategy>(registry.CreateStrategy(definition));

        Assert.Equal(10, strategy.Config.FastPeriod);
    }

    [Fact]
    public void Importable_provider_overlays_the_engine_level_config_of_the_definition()
    {
        PluginRegistry registry = new();
        StrategyDefinition definition = new(
            ImportableStrategyProvider.ProviderId,
            typeof(TypedProbeStrategy).AssemblyQualifiedName!,
            Json("""{"fastPeriod":5}"""),
            new StrategyConfig { StrategyId = new StrategyId("Overlay-001") });

        TypedProbeStrategy strategy = Assert.IsType<TypedProbeStrategy>(registry.CreateStrategy(definition));

        Assert.Equal("Overlay-001", strategy.StrategyId.Value);
        Assert.Equal(5, strategy.Config.FastPeriod);
    }

    [Fact]
    public void Importable_provider_overlay_does_not_erase_settings_made_in_the_payload()
    {
        PluginRegistry registry = new();
        StrategyDefinition definition = new(
            ImportableStrategyProvider.ProviderId,
            typeof(TypedProbeStrategy).AssemblyQualifiedName!,
            Json("""{"manageGtdExpiry":true,"omsType":"hedging"}"""),
            new StrategyConfig { StrategyId = new StrategyId("Overlay-001") });

        TypedProbeStrategy strategy = Assert.IsType<TypedProbeStrategy>(registry.CreateStrategy(definition));

        Assert.Equal("Overlay-001", strategy.StrategyId.Value);
        Assert.True(strategy.Config.ManageGtdExpiry);
        Assert.Equal(OmsType.Hedging, strategy.Config.OmsType);
    }

    [Fact]
    public void Importable_provider_rejects_unknown_types_and_types_that_are_not_strategies()
    {
        ImportableStrategyProvider provider = new();

        ValidationResult unknown = provider.Validate(new StrategyDefinition(provider.Id, "No.Such.Type, Nowhere", Json("{}")));
        ValidationResult notAStrategy = provider.Validate(new StrategyDefinition(provider.Id, typeof(ProbeActor).AssemblyQualifiedName!, Json("{}")));
        ValidationResult fine = provider.Validate(new StrategyDefinition(provider.Id, typeof(ProbeStrategy).AssemblyQualifiedName!, Json("{}")));

        Assert.False(unknown.IsValid);
        Assert.Contains("could not be resolved", Assert.Single(unknown.Errors), StringComparison.Ordinal);
        Assert.False(notAStrategy.IsValid);
        Assert.Contains("is not a Strategy", Assert.Single(notAStrategy.Errors), StringComparison.Ordinal);
        Assert.True(fine.IsValid);
        Assert.Empty(fine.Errors);
    }

    [Fact]
    public void Importable_actor_provider_builds_an_actor_with_its_configured_id()
    {
        PluginRegistry registry = new();
        ActorDefinition definition = new(ImportableStrategyProvider.ProviderId, typeof(ProbeActor).AssemblyQualifiedName!, Json("""{"actorId":"Monitor-007"}"""));

        Actor actor = registry.CreateActor(definition);

        Assert.IsType<ProbeActor>(actor);
        Assert.Equal("Monitor-007", actor.ActorId.Value);
    }

    [Fact]
    public void Importable_provider_falls_back_to_a_parameterless_constructor_and_refuses_anything_else()
    {
        ImportableStrategyProvider provider = new();

        Strategy plain = provider.Create(new StrategyDefinition(provider.Id, typeof(ParameterlessStrategy).AssemblyQualifiedName!, Json("{}")));

        Assert.IsType<ParameterlessStrategy>(plain);
        Assert.Throws<InvalidOperationException>(() => provider.Create(new StrategyDefinition(provider.Id, typeof(AwkwardStrategy).AssemblyQualifiedName!, Json("{}"))));
    }

    [Fact]
    public void Plugin_discovery_finds_concrete_plugins_with_a_parameterless_constructor()
    {
        IReadOnlyList<IPlugin> plugins = PluginLoader.FindPlugins(typeof(SamplePlugin).Assembly);

        Assert.Contains(plugins, p => p is SamplePlugin);
        Assert.DoesNotContain(plugins, p => p is PluginThatNeedsArguments);
    }

    [Fact]
    public void Loading_from_a_directory_that_does_not_exist_yields_no_plugins()
    {
        string missing = Path.Combine(Path.GetTempPath(), "bytex-plugins-" + Guid.NewGuid().ToString("N"));

        Assert.Empty(PluginLoader.LoadFromDirectory(missing));
        Assert.Throws<ArgumentException>(() => PluginLoader.LoadFromDirectory(" "));
    }

    [Fact]
    public void Service_collection_helpers_register_the_registry_and_plugins_as_singletons()
    {
        ServiceCollection services = new();

        services.AddBytex().AddPlugin<SamplePlugin>();

        Assert.Contains(services, d => d.ServiceType == typeof(PluginRegistry) && d.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, d => d.ServiceType == typeof(IPlugin) && d.ImplementationType == typeof(SamplePlugin) && d.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public void Building_the_registry_from_a_service_provider_adds_each_plugin_exactly_once()
    {
        PluginRegistry registry = new();
        SamplePlugin plugin = new();
        FakeServiceProvider provider = new(registry, plugin);

        PluginRegistry built = provider.BuildPluginRegistry();
        provider.BuildPluginRegistry();

        Assert.Same(registry, built);
        Assert.Same(plugin, Assert.Single(registry.Plugins));
        Assert.Equal(1, plugin.RegisterCalls);
    }

    private sealed class ParameterlessStrategy : Strategy
    {
        public ParameterlessStrategy()
        {
        }
    }

    private sealed class AwkwardStrategy : Strategy
    {
        public AwkwardStrategy(int period)
        {
            Period = period;
        }

        public int Period { get; }
    }
}

internal sealed class SamplePlugin : IPlugin
{
    public string Id => "sample";

    public string Version => "1.0.0";

    public int RegisterCalls { get; private set; }

    public void Register(IPluginRegistry registry)
    {
        RegisterCalls++;
        registry.AddStrategyProvider(new SampleStrategyProvider());
    }

    private sealed class SampleStrategyProvider : IStrategyProvider
    {
        public string Id => "sample.strategies";

        public IReadOnlyList<StrategyDescriptor> Describe() => [];

        public ValidationResult Validate(StrategyDefinition definition) => ValidationResult.Valid;

        public Strategy Create(StrategyDefinition definition) => new ProbeStrategy(definition.Config);
    }
}

internal sealed class PluginThatNeedsArguments : IPlugin
{
    public PluginThatNeedsArguments(string id) => Id = id;

    public string Id { get; }

    public string Version => "1.0.0";

    public void Register(IPluginRegistry registry)
    {
    }
}
