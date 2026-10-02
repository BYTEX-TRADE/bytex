using Bytex.Core.Plugins;
using Bytex.Documents.Catalog;

namespace Bytex.Cli;

/// <summary>
/// The node catalog this process runs documents against (R13.8).
///
/// <para>
/// A plugin may bring node types of its own, and a document that uses them means the same thing only where the same
/// plugins are loaded. Every command that reads a document therefore composes its catalog the same way, from one place:
/// a run that accepted a document its own <c>validate</c> had refused - or refused one it had accepted - would be worse
/// than not supporting plugin nodes at all.
/// </para>
/// </summary>
internal static class PluginNodes
{
    /// <summary>The catalog made from plugins already loaded, for a command that has a registry.</summary>
    public static NodeCatalog Catalog(IEnumerable<IPlugin> plugins) =>
        NodeCatalogComposer.Compose(plugins.OfType<INodeTypeProvider>());

    /// <summary>
    /// The catalog made from a plugin directory, for a command that has no registry: <c>documents validate</c>,
    /// <c>catalog</c> and <c>schema</c> need the types and nothing else a plugin registers. With no directory this is
    /// the built-in catalog, so the commands behave exactly as before.
    /// </summary>
    public static NodeCatalog Catalog(string? pluginDirectory) =>
        Catalog(pluginDirectory is null ? [] : PluginLoader.LoadFromDirectory(pluginDirectory));
}
