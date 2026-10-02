using Bytex.Core.Plugins;

namespace Bytex.Examples;

/// <summary>
/// Makes the example strategies discoverable when this assembly is loaded as a plugin.
/// Strategies are referenced by type name through the built-in importable provider.
/// </summary>
public sealed class ExamplesPlugin : IPlugin
{
    public string Id => "bytex.examples";

    public string Version => typeof(ExamplesPlugin).Assembly.GetName().Version?.ToString() ?? "0";

    public void Register(IPluginRegistry registry)
    {
    }
}
