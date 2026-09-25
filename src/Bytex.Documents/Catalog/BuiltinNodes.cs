using Bytex.Documents.Catalog.Types;

namespace Bytex.Documents.Catalog;

/// <summary>Assembles the built-in node catalog (version 1): every family a strategy document can draw from.</summary>
public static class BuiltinNodes
{
    public static NodeCatalog Create()
    {
        NodeCatalog catalog = new();
        foreach (NodeTypeDescriptor d in All())
        {
            catalog.Register(d);
        }

        return catalog;
    }

    public static IEnumerable<NodeTypeDescriptor> All() =>
        DataNodes.All()
            .Concat(IndicatorNodes.All())
            .Concat(LevelNodes.All())
            .Concat(ConditionNodes.All())
            .Concat(ActionNodes.All())
            .Concat(RiskNodes.All())
            .Concat(FlowNodes.All())
            .Concat(EventNodes.All());
}
