using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Bytex.Core.Adapters;
using Bytex.Core.Indicators;
using Bytex.Core.Serialization;
using Bytex.Core.Trading;
using Microsoft.Extensions.DependencyInjection;

namespace Bytex.Core.Plugins;

public sealed record ValidationResult(bool IsValid, IReadOnlyList<string> Errors)
{
    public static readonly ValidationResult Valid = new(true, []);

    public static ValidationResult Invalid(params string[] errors) => new(false, errors);
}

/// <summary>
/// A definition the engine can turn into a strategy through a provider. The payload is opaque to the engine.
/// </summary>
public sealed record StrategyDefinition(string ProviderId, string Name, JsonElement Payload, StrategyConfig? Config = null);

public sealed record ActorDefinition(string ProviderId, string Name, JsonElement Payload, ActorConfig? Config = null);

public sealed record ExecAlgorithmDefinition(string ProviderId, string Name, JsonElement Payload, ExecAlgorithmConfig? Config = null);

public sealed record StrategyDescriptor(string Name, string DisplayName, string? Description, JsonElement? ParameterSchema);

public interface IStrategyProvider
{
    string Id { get; }

    IReadOnlyList<StrategyDescriptor> Describe();

    ValidationResult Validate(StrategyDefinition definition);

    Strategy Create(StrategyDefinition definition);
}

public interface IActorProvider
{
    string Id { get; }

    IReadOnlyList<StrategyDescriptor> Describe();

    ValidationResult Validate(ActorDefinition definition);

    Actor Create(ActorDefinition definition);
}

public interface IExecAlgorithmProvider
{
    string Id { get; }

    IReadOnlyList<StrategyDescriptor> Describe();

    ValidationResult Validate(ExecAlgorithmDefinition definition);

    ExecAlgorithm Create(ExecAlgorithmDefinition definition);
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

/// <summary>
/// Entry point of a plugin assembly.
/// </summary>
public interface IPlugin
{
    string Id { get; }

    string Version { get; }

    void Register(IPluginRegistry registry);
}

/// <summary>
/// Holds everything plugins contribute and resolves providers and factories by id.
/// </summary>
public sealed class PluginRegistry : IPluginRegistry
{
    private readonly Dictionary<string, IStrategyProvider> _strategyProviders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IActorProvider> _actorProviders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IExecAlgorithmProvider> _execAlgorithmProviders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IDataClientFactory> _dataClientFactories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IExecutionClientFactory> _executionClientFactories = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IIndicatorFactory> _indicatorFactories = new();
    private readonly List<IPlugin> _plugins = new();

    public PluginRegistry()
    {
        AddStrategyProvider(new ImportableStrategyProvider());
        AddActorProvider(new ImportableActorProvider());
    }

    public IReadOnlyList<IPlugin> Plugins => _plugins;

    public IReadOnlyDictionary<string, IStrategyProvider> StrategyProviders => _strategyProviders;

    public IReadOnlyDictionary<string, IActorProvider> ActorProviders => _actorProviders;

    public IReadOnlyDictionary<string, IExecAlgorithmProvider> ExecAlgorithmProviders => _execAlgorithmProviders;

    public IReadOnlyDictionary<string, IDataClientFactory> DataClientFactories => _dataClientFactories;

    public IReadOnlyDictionary<string, IExecutionClientFactory> ExecutionClientFactories => _executionClientFactories;

    public IReadOnlyList<IIndicatorFactory> IndicatorFactories => _indicatorFactories;

    public void AddPlugin(IPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        if (_plugins.Any(p => p.Id == plugin.Id))
        {
            throw new InvalidOperationException($"Plugin {plugin.Id} is already registered.");
        }

        plugin.Register(this);
        _plugins.Add(plugin);
    }

    public void AddStrategyProvider(IStrategyProvider provider) => _strategyProviders[provider.Id] = provider ?? throw new ArgumentNullException(nameof(provider));

    public void AddActorProvider(IActorProvider provider) => _actorProviders[provider.Id] = provider ?? throw new ArgumentNullException(nameof(provider));

    public void AddExecAlgorithmProvider(IExecAlgorithmProvider provider) => _execAlgorithmProviders[provider.Id] = provider ?? throw new ArgumentNullException(nameof(provider));

    public void AddDataClientFactory(IDataClientFactory factory) => _dataClientFactories[factory.Name] = factory ?? throw new ArgumentNullException(nameof(factory));

    public void AddExecutionClientFactory(IExecutionClientFactory factory) => _executionClientFactories[factory.Name] = factory ?? throw new ArgumentNullException(nameof(factory));

    public void AddIndicatorFactory(IIndicatorFactory factory) => _indicatorFactories.Add(factory ?? throw new ArgumentNullException(nameof(factory)));

    public Strategy CreateStrategy(StrategyDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!_strategyProviders.TryGetValue(definition.ProviderId, out IStrategyProvider? provider))
        {
            throw new InvalidOperationException($"No strategy provider registered with id '{definition.ProviderId}'.");
        }

        ValidationResult validation = provider.Validate(definition);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException($"Strategy definition '{definition.Name}' is invalid: {string.Join("; ", validation.Errors)}");
        }

        return provider.Create(definition);
    }

    public Actor CreateActor(ActorDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!_actorProviders.TryGetValue(definition.ProviderId, out IActorProvider? provider))
        {
            throw new InvalidOperationException($"No actor provider registered with id '{definition.ProviderId}'.");
        }

        return provider.Create(definition);
    }

    public ExecAlgorithm CreateExecAlgorithm(ExecAlgorithmDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!_execAlgorithmProviders.TryGetValue(definition.ProviderId, out IExecAlgorithmProvider? provider))
        {
            throw new InvalidOperationException($"No execution algorithm provider registered with id '{definition.ProviderId}'.");
        }

        return provider.Create(definition);
    }

    public IIndicator CreateIndicator(string name, IReadOnlyDictionary<string, string> parameters)
    {
        foreach (IIndicatorFactory factory in _indicatorFactories)
        {
            if (factory.Names.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                return factory.Create(name, parameters);
            }
        }

        throw new InvalidOperationException($"No indicator factory can create '{name}'.");
    }
}

/// <summary>
/// Built-in provider: <c>Name</c> is an assembly-qualified type name and the payload is the strategy's config.
/// </summary>
public sealed class ImportableStrategyProvider : IStrategyProvider
{
    public const string ProviderId = "bytex.importable";

    public string Id => ProviderId;

    public IReadOnlyList<StrategyDescriptor> Describe() => [new StrategyDescriptor("<type name>", "Importable strategy", "Any Strategy subclass referenced by assembly-qualified type name.", null)];

    public ValidationResult Validate(StrategyDefinition definition)
    {
        Type? type = ResolveType(definition.Name);
        if (type is null)
        {
            return ValidationResult.Invalid($"Type '{definition.Name}' could not be resolved.");
        }

        if (!typeof(Strategy).IsAssignableFrom(type))
        {
            return ValidationResult.Invalid($"Type '{definition.Name}' is not a Strategy.");
        }

        return ValidationResult.Valid;
    }

    public Strategy Create(StrategyDefinition definition)
    {
        Type type = ResolveType(definition.Name) ?? throw new InvalidOperationException($"Type '{definition.Name}' could not be resolved.");
        return (Strategy)TypeActivator.Create(type, definition.Payload, definition.Config);
    }

    internal static Type? ResolveType(string name)
    {
        Type? type = Type.GetType(name, throwOnError: false);
        if (type is not null)
        {
            return type;
        }

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            type = assembly.GetType(name, throwOnError: false);
            if (type is not null)
            {
                return type;
            }

            type = assembly.GetTypes().FirstOrDefault(t => t.FullName == name || t.Name == name);
            if (type is not null)
            {
                return type;
            }
        }

        return null;
    }
}

public sealed class ImportableActorProvider : IActorProvider
{
    public string Id => ImportableStrategyProvider.ProviderId;

    public IReadOnlyList<StrategyDescriptor> Describe() => [new StrategyDescriptor("<type name>", "Importable actor", "Any Actor subclass referenced by assembly-qualified type name.", null)];

    public ValidationResult Validate(ActorDefinition definition)
    {
        Type? type = ImportableStrategyProvider.ResolveType(definition.Name);
        return type is null ? ValidationResult.Invalid($"Type '{definition.Name}' could not be resolved.") : ValidationResult.Valid;
    }

    public Actor Create(ActorDefinition definition)
    {
        Type type = ImportableStrategyProvider.ResolveType(definition.Name) ?? throw new InvalidOperationException($"Type '{definition.Name}' could not be resolved.");
        return (Actor)TypeActivator.Create(type, definition.Payload, definition.Config);
    }
}

internal static class TypeActivator
{
    /// <summary>
    /// Creates an actor from a type: uses the single-parameter config constructor when available, deserialising the payload into it.
    /// </summary>
    public static object Create(Type type, JsonElement payload, ActorConfig? baseConfig)
    {
        ConstructorInfo[] constructors = type.GetConstructors();
        ConstructorInfo? configCtor = constructors.FirstOrDefault(c => c.GetParameters().Length == 1 && typeof(ActorConfig).IsAssignableFrom(c.GetParameters()[0].ParameterType));
        if (configCtor is not null)
        {
            Type configType = configCtor.GetParameters()[0].ParameterType;
            object config = payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                ? Activator.CreateInstance(configType) ?? throw new InvalidOperationException($"Cannot create {configType.Name}.")
                : JsonSerializer.Deserialize(payload.GetRawText(), configType, BytexJson.Options) ?? throw new InvalidOperationException($"Cannot deserialise {configType.Name}.");

            if (baseConfig is not null)
            {
                MergeBaseConfig(config, baseConfig);
            }

            return configCtor.Invoke([config]);
        }

        ConstructorInfo? defaultCtor = constructors.FirstOrDefault(c => c.GetParameters().Length == 0);
        if (defaultCtor is not null)
        {
            return defaultCtor.Invoke([]);
        }

        throw new InvalidOperationException($"Type {type.Name} has no usable constructor (expected (TConfig) or ()).");
    }

    /// <summary>A config of the same type with nothing set, to tell "the payload said so" from "this is the default".</summary>
    private static object? Untouched(Type configType)
    {
        try
        {
            return configType.GetConstructor(Type.EmptyTypes) is null ? null : Activator.CreateInstance(configType);
        }
        catch (Exception e) when (e is MissingMethodException or TargetInvocationException or MemberAccessException)
        {
            return null;
        }
    }

    /// <summary>True when the payload left this property alone: null, the type's own default, or an empty collection.</summary>
    private static bool IsDefault(object? current, object? untouched)
    {
        if (current is null)
        {
            return true;
        }

        if (current is IEnumerable items and not string)
        {
            return !items.GetEnumerator().MoveNext();
        }

        return untouched is not null && Equals(current, untouched);
    }

    private static void MergeBaseConfig(object config, ActorConfig baseConfig)
    {
        // Copy engine-level settings from the base config onto the typed config where the typed config left defaults.
        // What the payload set wins: a base config carries its own defaults for everything it does not mean to say, and
        // copying those over the payload turned manageGtdExpiry: true back into false.
        object? untouched = Untouched(config.GetType());
        foreach (PropertyInfo property in baseConfig.GetType().GetProperties())
        {
            PropertyInfo? target = config.GetType().GetProperty(property.Name);
            if (target is null || !target.CanWrite)
            {
                continue;
            }

            object? value = property.GetValue(baseConfig);
            if (value is not null && IsDefault(target.GetValue(config), untouched is null ? null : target.GetValue(untouched)))
            {
                target.SetValue(config, value);
            }
        }
    }
}

/// <summary>
/// Loads plugin assemblies from a directory, each in its own isolated load context.
/// </summary>
public static class PluginLoader
{
    public static IReadOnlyList<IPlugin> LoadFromDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        List<IPlugin> plugins = new();
        foreach (string pluginDir in Directory.GetDirectories(directory))
        {
            foreach (string dll in Directory.GetFiles(pluginDir, "*.dll"))
            {
                plugins.AddRange(LoadFromAssembly(dll));
            }
        }

        foreach (string dll in Directory.GetFiles(directory, "*.dll"))
        {
            plugins.AddRange(LoadFromAssembly(dll));
        }

        return plugins;
    }

    public static IReadOnlyList<IPlugin> LoadFromAssembly(string assemblyPath)
    {
        PluginLoadContext context = new(assemblyPath);
        Assembly assembly = context.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
        return FindPlugins(assembly);
    }

    public static IReadOnlyList<IPlugin> FindPlugins(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        List<IPlugin> plugins = new();
        foreach (Type type in assembly.GetTypes())
        {
            if (typeof(IPlugin).IsAssignableFrom(type) && !type.IsAbstract && type.GetConstructor(Type.EmptyTypes) is not null)
            {
                plugins.Add((IPlugin)Activator.CreateInstance(type)!);
            }
        }

        return plugins;
    }

    private sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;

        public PluginLoadContext(string pluginPath)
            : base(isCollectible: false)
        {
            _resolver = new AssemblyDependencyResolver(pluginPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Share the engine and its dependencies with the host.
            if (assemblyName.Name is { } name && (name.StartsWith("Bytex.", StringComparison.Ordinal) || name.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal) || name.StartsWith("System.", StringComparison.Ordinal)))
            {
                return null;
            }

            string? path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? nint.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}

/// <summary>
/// Dependency-injection helpers for hosting the engine.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddBytex(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<PluginRegistry>();
        return services;
    }

    public static IServiceCollection AddPlugin<TPlugin>(this IServiceCollection services) where TPlugin : class, IPlugin, new()
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IPlugin, TPlugin>();
        return services;
    }

    public static PluginRegistry BuildPluginRegistry(this IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        PluginRegistry registry = provider.GetRequiredService<PluginRegistry>();
        foreach (IPlugin plugin in provider.GetServices<IPlugin>())
        {
            if (registry.Plugins.All(p => p.Id != plugin.Id))
            {
                registry.AddPlugin(plugin);
            }
        }

        return registry;
    }
}
