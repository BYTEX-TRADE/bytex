namespace Bytex.Documents.Catalog;

/// <summary>
/// A source of node types a document may use, beyond the built-in catalog (R13.8).
///
/// <para>
/// It lives here rather than beside the other plugin providers in <c>Bytex.Core.Plugins</c> for one reason: a node type
/// is a document's concept, and the core must not learn what a document is to carry a list of them. A plugin that
/// provides node types implements this as well as <c>IPlugin</c>, and a host composes the catalog it runs with.
/// </para>
///
/// <para>
/// <b>The prefix is not decoration.</b> Every type a provider offers must begin with that provider's own prefix, and a
/// prefix may not be one the engine uses. Without that rule a plugin could register <c>act.market</c> and every document
/// that has ever used a market order would quietly mean something else - on the machines that loaded the plugin, and
/// only there.
/// </para>
/// </summary>
public interface INodeTypeProvider
{
    /// <summary>
    /// The prefix every type from this provider carries, without the dot: <c>acme</c> for <c>acme.squeeze</c>.
    /// </summary>
    string TypePrefix { get; }

    /// <summary>The types themselves. Called once, when a catalog is composed.</summary>
    IEnumerable<NodeTypeDescriptor> NodeTypes();
}

/// <summary>
/// Builds the catalog a host runs with: the built-in types, plus whatever providers it was given.
/// </summary>
public static class NodeCatalogComposer
{
    /// <summary>
    /// Prefixes the engine's own node types use. A provider claiming one of these is refused, because a document
    /// naming <c>ind.rsi</c> has to mean the same thing on every machine that reads it.
    /// </summary>
    public static readonly IReadOnlySet<string> ReservedPrefixes = new HashSet<string>(StringComparer.Ordinal)
    {
        "data", "ind", "level", "cond", "act", "risk", "flow", "event", "bytex",
    };

    /// <summary>
    /// The built-in catalog with every provider's types added.
    ///
    /// <para>
    /// A provider whose types are refused takes nothing with it: the whole composition fails rather than producing a
    /// catalog that is missing some of what was asked for. A host that started with half a plugin's nodes would give a
    /// document a validation error naming a type the plugin plainly provides.
    /// </para>
    /// </summary>
    public static NodeCatalog Compose(IEnumerable<INodeTypeProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        NodeCatalog catalog = new(BuiltinNodes.All());
        Dictionary<string, string> claimed = new(StringComparer.Ordinal);

        foreach (INodeTypeProvider provider in providers)
        {
            string prefix = Prefix(provider);
            if (claimed.TryGetValue(prefix, out string? already))
            {
                throw new InvalidOperationException(
                    $"Two providers claim the prefix '{prefix}': {already} and {provider.GetType().FullName}. A type id has to name one provider.");
            }

            claimed[prefix] = provider.GetType().FullName ?? prefix;

            foreach (NodeTypeDescriptor descriptor in provider.NodeTypes())
            {
                if (!descriptor.Type.StartsWith(prefix + ".", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"'{descriptor.Type}' does not begin with the prefix '{prefix}.' its provider declared. A type outside its provider's prefix cannot be told from a built-in one.");
                }

                catalog.Register(descriptor);
            }
        }

        return catalog;
    }

    private static string Prefix(INodeTypeProvider provider)
    {
        string prefix = provider.TypePrefix;
        if (string.IsNullOrWhiteSpace(prefix) || prefix.Contains('.', StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"'{prefix}' is not a prefix: it is one word, without a dot, and every type from that provider begins with it.");
        }

        if (ReservedPrefixes.Contains(prefix))
        {
            throw new InvalidOperationException(
                $"'{prefix}' is one of the engine's own prefixes. A plugin using it could redefine a built-in node, and every document naming that node would mean something else on the machines that loaded the plugin - and only there.");
        }

        return prefix;
    }
}
