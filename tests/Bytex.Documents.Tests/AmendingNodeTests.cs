using System.Text.Json;
using System.Text.RegularExpressions;
using Bytex.Documents.Catalog;

namespace Bytex.Documents.Tests;

// Why: a venue family states whether it allows an order to be changed after it is placed, and that alone tells a host
// nothing. To validate a document against a venue it needs the other half - which nodes in the document would amend -
// and the only alternative to declaring it is for the host to hard-code the engine's amending node types. That list
// goes stale the day a fifth is added, and silently: a document full of amending nodes is perfectly valid until it
// meets a venue that refuses them.
//
// What it costs to be wrong is not a failed request. On a venue that refuses amendments a protective order stays at
// the size it was first placed at, so a position that is added to keeps a stop sized for the smaller one - still open,
// still at the right price, covering less than it claims. That is why this is declared rather than discovered.
//
// The declaration is not trusted here. It is checked against the code: a node amends exactly when its implementation
// calls Services.ModifyOrder, which is the one way to change a placed order, and this reads the catalog's source to
// find out. So a new node that amends fails this test naming itself, rather than being found by a user on KuCoin.
public sealed class AmendingNodeTests
{
    private const string AmendCall = "Services.ModifyOrder";

    /// <summary>
    /// The repository root, found by walking up to the solution file. This test is kept honest by reading the source
    /// rather than by reflecting over what was compiled, because the question it asks - does this node's code amend an
    /// order - is not visible in the compiled shape at all.
    /// </summary>
    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Bytex.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("no Bytex.slnx above " + AppContext.BaseDirectory);
    }

    /// <summary>The catalog's own source files, which hold both the descriptors and the node classes they build.</summary>
    private static IReadOnlyList<string> CatalogSources()
    {
        string types = Path.Combine(RepoRoot(), "src", "Bytex.Documents", "Catalog", "Types");
        Assert.True(Directory.Exists(types), $"catalog type sources not found at {types}");
        return [.. Directory.GetFiles(types, "*.cs", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Node classes whose bodies call the amend API, found by reading the source. Classes are nested in the catalog's
    /// type files, so the enclosing declaration of each call is tracked line by line rather than parsed.
    /// </summary>
    private static HashSet<string> ClassesThatAmend()
    {
        HashSet<string> amending = new(StringComparer.Ordinal);
        Regex declaration = new(@"\bclass\s+(?<name>\w+)\b", RegexOptions.Compiled);

        foreach (string file in CatalogSources())
        {
            string? current = null;
            foreach (string line in File.ReadAllLines(file))
            {
                Match match = declaration.Match(line);
                if (match.Success)
                {
                    current = match.Groups["name"].Value;
                }

                if (current is not null && line.Contains(AmendCall, StringComparison.Ordinal))
                {
                    amending.Add(current);
                }
            }
        }

        return amending;
    }

    /// <summary>Each declared node type and the class its factory builds, read from the same source.</summary>
    private static IReadOnlyList<(string Type, string Class)> DeclaredFactories()
    {
        List<(string, string)> pairs = [];
        Regex type = new("""Type = "(?<type>[^"]+)",""", RegexOptions.Compiled);
        Regex factory = new(@"Factory = ctx => new (?<class>\w+)\(", RegexOptions.Compiled);

        foreach (string file in CatalogSources())
        {
            string? pending = null;
            foreach (string line in File.ReadAllLines(file))
            {
                Match t = type.Match(line);
                if (t.Success)
                {
                    pending = t.Groups["type"].Value;
                }

                Match f = factory.Match(line);
                if (f.Success && pending is not null)
                {
                    pairs.Add((pending, f.Groups["class"].Value));
                    pending = null;
                }
            }
        }

        return pairs;
    }

    [Fact]
    public void The_source_really_does_contain_the_things_this_file_reads()
    {
        // The reading above IS the test, so a refactor that renamed the amend call or changed how a factory is written
        // would leave every assertion below vacuously true. This is what stops that being silent.
        Assert.NotEmpty(ClassesThatAmend());
        Assert.True(DeclaredFactories().Count >= 60, $"only {DeclaredFactories().Count} node factories were found in the catalog source");
    }

    [Fact]
    public void Every_node_type_is_covered_either_by_its_own_class_or_by_a_shared_one_that_was_checked()
    {
        // Coverage, stated rather than assumed. Most types name the class their factory builds, and the scan reads
        // that class. The indicator wrappers do not: they are produced by helpers that all funnel into one shared
        // evaluator, so they have no class of their own to read.
        //
        // That is only safe while the shared evaluator is itself checked and does not amend, and while nothing but
        // indicators takes that route. Both are asserted here, so a future type that quietly shares an evaluator
        // cannot slip past the test above by having no factory line of its own.
        HashSet<string> named = [.. DeclaredFactories().Select(f => f.Type)];
        NodeTypeDescriptor[] shared = [.. NodeCatalog.Default.Types.Where(d => !named.Contains(d.Type))];

        // A type built through a helper has no class of its own, so it cannot be paired with one by reading the
        // source and the test above says nothing about it. It must therefore not claim to amend.
        Assert.All(shared, d => Assert.False(
            d.AmendsOrders,
            $"{d.Type} is built by a shared evaluator, so its claim to amend cannot be checked against code"));

        // And that is only sound because the shared evaluators are scanned too - ClassesThatAmend reads every class
        // in these files, paired or not. So the classes that amend are exactly the four with their own types, and no
        // shared evaluator is among them. A helper-built type could not become amending without one of these
        // appearing here.
        Assert.Equal(
            ["ExitNode", "ModifyNode", "MoveStopNode", "TrailNode"],
            ClassesThatAmend().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_node_declares_that_it_amends_exactly_when_its_code_amends()
    {
        // The guard. Not "these four types amend" - that is a list, and a list is what this exists to remove - but
        // that the declaration and the implementation cannot disagree.
        HashSet<string> amending = ClassesThatAmend();
        Dictionary<string, NodeTypeDescriptor> declared = NodeCatalog.Default.Types.ToDictionary(d => d.Type, StringComparer.Ordinal);

        foreach ((string type, string @class) in DeclaredFactories())
        {
            Assert.True(declared.ContainsKey(type), $"the catalog source declares {type} but NodeCatalog.Default.Types does not return it");
            bool codeAmends = amending.Contains(@class);

            Assert.Equal(codeAmends, declared[type].AmendsOrders);
        }
    }

    [Fact]
    public void The_nodes_that_amend_today_are_the_four_that_change_a_placed_order()
    {
        // Stated once, as a fact about this version rather than as the rule. If a change makes this list wrong, the
        // test above is the one that decides whether the change or the declaration was at fault; this one only makes
        // the current answer visible to somebody reading the file.
        string[] amends = [.. NodeCatalog.Default.Types.Where(d => d.AmendsOrders).Select(d => d.Type).Order(StringComparer.Ordinal)];

        Assert.Equal(["act.modify", "act.moveStop", "act.trail", "risk.exit"], amends);
    }

    [Fact]
    public void Nothing_that_only_places_or_cancels_claims_to_amend()
    {
        // The direction that would be expensive the other way round. A node wrongly claiming to amend makes a host
        // refuse a document that would have run perfectly, which is a capability quietly lost rather than a bug
        // anybody reports.
        foreach (NodeTypeDescriptor descriptor in NodeCatalog.Default.Types.Where(d => d.AmendsOrders))
        {
            Assert.Contains(descriptor.Kind, new[] { NodeKind.Action, NodeKind.Risk });
        }

        Assert.All(
            NodeCatalog.Default.Types.Where(d => d.Kind is NodeKind.Data or NodeKind.Indicator or NodeKind.Condition or NodeKind.Flow or NodeKind.Event),
            d => Assert.False(d.AmendsOrders, $"{d.Type} reads or decides; it cannot amend an order"));
    }

    [Fact]
    public void The_flag_survives_the_catalog_being_exported_to_a_host()
    {
        // A host reads the catalog as JSON across a process boundary, so a flag that exists only on the record is a
        // flag the host cannot see - which is the whole point of declaring it. It was absent when this test was first
        // written, because the export is a hand-written projection.
        using JsonDocument exported = JsonDocument.Parse(NodeCatalog.Default.ExportJson());
        JsonElement[] types = [.. exported.RootElement.GetProperty("types").EnumerateArray()];

        Assert.All(types, t => Assert.True(
            t.TryGetProperty("amendsOrders", out _),
            $"{t.GetProperty("type").GetString()} was exported without amendsOrders"));

        // Both answers, so the field is not merely present but says something: written always rather than only when
        // true, because a host has to tell "does not amend" from "this engine is too old to say".
        Assert.Contains(types, t => t.GetProperty("amendsOrders").GetBoolean());
        Assert.Contains(types, t => !t.GetProperty("amendsOrders").GetBoolean());

        foreach (JsonElement t in types.Where(t => t.GetProperty("amendsOrders").GetBoolean()))
        {
            Assert.True(NodeCatalog.Default.Find(t.GetProperty("type").GetString()!)!.AmendsOrders);
        }
    }

    [Fact]
    public void Every_field_of_a_descriptor_reaches_the_catalog_a_host_reads()
    {
        // The defect class, not this instance of it. The export is written by hand, field by field, so a property
        // added to NodeTypeDescriptor is invisible to every host until somebody remembers to add a line - and nothing
        // fails, because a catalog missing a field is still a valid catalog. amendsOrders was added and forgotten
        // within the hour, which is how this test came to exist.
        using JsonDocument exported = JsonDocument.Parse(NodeCatalog.Default.ExportJson());
        JsonElement sample = exported.RootElement.GetProperty("types").EnumerateArray()
            .First(t => t.GetProperty("type").GetString() == "risk.exit");

        string[] present = [.. sample.EnumerateObject().Select(p => p.Name)];

        // Renamed on the way out, because the file says what a host needs rather than what the type calls it: the
        // face template is "face", and the factory becomes whether the engine can run the type at all.
        Dictionary<string, string> renamed = new(StringComparer.Ordinal)
        {
            [nameof(NodeTypeDescriptor.FaceTemplate)] = "face",
            [nameof(NodeTypeDescriptor.Factory)] = "runnable",
        };

        foreach (string field in typeof(NodeTypeDescriptor).GetProperties().Select(p => p.Name))
        {
            string expected = renamed.GetValueOrDefault(field, field);
            Assert.Contains(expected, present, StringComparer.OrdinalIgnoreCase);
        }
    }
}
