using System.Text.Json;
using System.Text.RegularExpressions;
using Bytex.Backtest;
using Bytex.Cli.Tests.Support;

namespace Bytex.Cli.Tests;

// Why: the packages, the container image, the example configurations and the notebook are all things a reader runs
// before they write any code of their own, and none of them is exercised by a normal build. A renamed project, a moved
// file or a provider id that no longer exists would leave them broken for everyone but visible to nobody. These tests
// read the shipped files and hold them to the repository they describe.
public sealed class ShippedArtifactsTests
{
    private static readonly string[] _packageProjects =
    [
        "Bytex.Core", "Bytex.Indicators", "Bytex.Documents", "Bytex.Data", "Bytex.Backtest", "Bytex.Live",
        "Bytex.Adapters.Binance", "Bytex.Adapters.Bybit", "Bytex.Adapters.Kucoin", "Bytex.Adapters.Okx",
        "Bytex.Adapters.Tardis",
        "Bytex.Persistence.Redis", "Bytex.Cli",
    ];

    // ----- what the documentation says about releases -----

    // Why: the v0.2.0 tag shipped a README that said "0.2 ships as the next release". It was false the moment it was
    // tagged, because cutting a release bumped the version and the tag and left the sentence that names the current
    // release alone. Prose about which version is out goes stale by itself, so it is held to the two things that
    // cannot lie: the version in Directory.Build.props and the sections in the changelog.
    [Fact]
    public void No_document_claims_a_release_is_still_coming()
    {
        foreach ((string path, string text) in DocumentsAndTheReadme())
        {
            foreach (string phrase in new[] { "ships as the next release", "is the next release", "until it is released", "once packages are published" })
            {
                Assert.DoesNotContain(phrase, text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void Every_milestone_marked_delivered_has_a_release_in_the_changelog()
    {
        string changelog = RepoRoot.Read("CHANGELOG.md");
        Regex sections = new(@"^## \[(\d+\.\d+)\.\d+\]", RegexOptions.Multiline, TimeSpan.FromSeconds(1));
        HashSet<string> released = sections.Matches(changelog).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(released);

        // A row like "| **0.3** ✅ |" in the README, or a heading like "## 0.3 · Risk ✅" in the roadmap.
        //
        // The two line endings are ESCAPED rather than written as a real line break inside the pattern, which is
        // how this read until a fifth adapter had to be added to the list above. A real break made what the
        // character class excludes a property of the file's BYTES: anything that renormalised the file - which is
        // what this repository's own .gitattributes does to it the moment it is committed - dropped the carriage
        // return out of the class and left a regex that still compiled, still matched, and would have accepted a
        // version heading with a stray carriage return in it. Escaped, it says what it means whatever the file's
        // line endings are, and the file can be edited.
        Regex ticked = new(@"(?:\|\s*\*\*|##\s*)(\d+\.\d+)(?:\*\*)?[^|\r\n]*✅", RegexOptions.Multiline, TimeSpan.FromSeconds(1));
        foreach ((string path, string text) in DocumentsAndTheReadme())
        {
            foreach (Match milestone in ticked.Matches(text))
            {
                string version = milestone.Groups[1].Value;
                Assert.True(released.Contains(version), $"{path} marks {version} as delivered, and the changelog has no {version}.x section for it");
            }
        }
    }

    [Fact]
    public void The_version_being_prepared_has_a_changelog_section_of_its_own()
    {
        // The release workflow reads the notes for <Version> out of the changelog and refuses a tag whose version has
        // no section. This says the same thing on every build, so the omission is found before the tag is pushed.
        string version = Regex.Match(RepoRoot.Read("Directory.Build.props"), @"<Version>([^<]+)</Version>", RegexOptions.None, TimeSpan.FromSeconds(1)).Groups[1].Value;
        Assert.False(string.IsNullOrWhiteSpace(version));

        string changelog = RepoRoot.Read("CHANGELOG.md");
        Assert.Contains($"## [{version}]", changelog, StringComparison.Ordinal);
    }

    private static IEnumerable<(string Path, string Text)> DocumentsAndTheReadme()
    {
        yield return ("README.md", RepoRoot.Read("README.md"));
        foreach (string file in Directory.EnumerateFiles(RepoRoot.Combine("docs"), "*.md", SearchOption.AllDirectories))
        {
            yield return (Path.GetFileName(file)!, File.ReadAllText(file));
        }
    }

    // ----- packages -----


    [Fact]
    public void Every_component_is_published_as_a_package_with_the_metadata_a_package_needs()
    {
        foreach (string name in _packageProjects)
        {
            string project = RepoRoot.Read("src", name, name + ".csproj");
            Assert.Contains("<IsPackable>true</IsPackable>", project, StringComparison.Ordinal);
            Assert.Matches(new Regex(@"<Description>\s*[^<\s][^<]*</Description>", RegexOptions.None, TimeSpan.FromSeconds(1)), project);
        }

        // A component added to src/ without being listed here is either a package nobody documented or an omission.
        IEnumerable<string> projects = Directory.EnumerateDirectories(RepoRoot.Combine("src")).Select(Path.GetFileName)!;
        Assert.Equal(_packageProjects.Order(), projects.Order());
    }

    [Fact]
    public void The_cli_is_packed_as_a_dotnet_tool_called_bytex()
    {
        string project = RepoRoot.Read("src", "Bytex.Cli", "Bytex.Cli.csproj");

        Assert.Contains("<PackAsTool>true</PackAsTool>", project, StringComparison.Ordinal);
        Assert.Contains("<ToolCommandName>bytex</ToolCommandName>", project, StringComparison.Ordinal);
        Assert.Contains("<AssemblyName>bytex</AssemblyName>", project, StringComparison.Ordinal);
    }

    [Fact]
    public void No_test_project_is_packable()
    {
        foreach (string project in Directory.EnumerateFiles(RepoRoot.Combine("tests"), "*.csproj", SearchOption.AllDirectories))
        {
            Assert.Contains("<IsPackable>false</IsPackable>", File.ReadAllText(project), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void No_ignore_rule_can_swallow_a_directory_of_source()
    {
        // How a build passes here and fails in CI with "the name does not exist": .gitignore said "data/", which is
        // not a path but a name, and it matched src/Bytex.Core/Model/Data. A new file there was ignored by git add,
        // compiled locally because it was on disk, and did not exist for anybody who cloned the repository. The two
        // files already in that folder were tracked from before the rule, so nothing looked wrong.
        //
        // A rule that names a directory without anchoring it to the repository root can do this to any folder that
        // happens to share the name. Where a rule means a directory at the root, it has to say so with a slash.
        // What makes a rule dangerous is hiding a directory that HOLDS SOURCE. A rule for generated output -
        // bin, obj, TestResults - has to match anywhere and is not anchored on purpose, so only directories with
        // source in them count. The first version of this test looked at every directory name and failed in CI on
        // "TestResults/", which the trx logger creates before the test runs and which exists locally only when the
        // same logger is used - a test that passed here for want of a command-line switch.
        string[] lines = File.ReadAllLines(RepoRoot.Combine(".gitignore"));
        HashSet<string> sourceDirectories = new(StringComparer.OrdinalIgnoreCase);
        foreach (string tree in new[] { "src", "tests", "examples" })
        {
            foreach (string directory in Directory.EnumerateDirectories(RepoRoot.Combine(tree), "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(RepoRoot.Path, directory);
                string[] segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (segments.Any(segment => segment is "bin" or "obj")
                    || !Directory.EnumerateFiles(directory, "*.cs").Any())
                {
                    continue;
                }

                sourceDirectories.Add(Path.GetFileName(directory));
            }
        }

        List<string> dangerous = new();
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('!') || line.StartsWith('/') || line.Contains('*') || !line.EndsWith('/'))
            {
                continue;
            }

            string name = line.TrimEnd('/');
            if (sourceDirectories.Contains(name))
            {
                dangerous.Add($"'{line}' matches a source directory named '{name}'");
            }
        }

        Assert.True(
            dangerous.Count == 0,
            "an ignore rule names a directory without anchoring it, so it hides source from git while the build still "
            + "finds it on disk: " + string.Join("; ", dangerous) + ". Put a leading slash on it.");
    }

    [Fact]
    public void Nothing_in_this_repository_names_a_product_built_on_top_of_it()
    {
        // This repository is the open engine and nothing else. Naming a private product in it - in a comment, a
        // validation hint, a document or a changelog - tells everyone who clones this what that product is and how it
        // is built, and it had happened by habit in thirteen places before anybody looked. The engine's own words for
        // whatever drives it are "a host" and "a client".
        //
        // The word is assembled rather than written, or this test would be the fourteenth place naming it.
        string product = "St" + "udio";
        List<string> found = new();

        foreach (string file in Directory.EnumerateFiles(RepoRoot.Path, "*.*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(RepoRoot.Path, file);
            if (relative.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || relative.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || relative.StartsWith(".git", StringComparison.Ordinal)
                || Path.GetExtension(file) is not (".cs" or ".md" or ".json" or ".yml" or ".yaml" or ".props" or ".csproj" or ".slnx" or ".ipynb" or ".txt"))
            {
                continue;
            }

            foreach (string line in File.ReadAllLines(file))
            {
                // "xunit.runner.visualstudio" is a package name and is nobody's product.
                if (line.Contains(product, StringComparison.OrdinalIgnoreCase)
                    && !line.Contains("visual" + product, StringComparison.OrdinalIgnoreCase))
                {
                    found.Add($"{relative}: {line.Trim()}");
                }
            }
        }

        Assert.True(found.Count == 0, "this repository names a product built on top of it: " + string.Join(" | ", found));
    }

    [Fact]
    public void No_project_references_the_same_project_twice()
    {
        // A duplicate reference builds and tests exactly as a single one does, so nothing ever complains: the adapter
        // test project carried the KuCoin reference twice and it took reading the file to notice. What it costs is the
        // reader - a list of what a project depends on stops being a list of what it depends on.
        Regex references = new(@"<ProjectReference\s+Include=""(?<path>[^""]+)""", RegexOptions.None, TimeSpan.FromSeconds(5));
        foreach (string project in Directory.EnumerateFiles(RepoRoot.Path, "*.csproj", SearchOption.AllDirectories))
        {
            if (project.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            string[] referenced = references.Matches(File.ReadAllText(project)).Select(m => m.Groups["path"].Value).ToArray();
            string[] twice = referenced.GroupBy(r => r, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();

            Assert.True(
                twice.Length == 0,
                $"{Path.GetFileName(project)} references {string.Join(", ", twice)} more than once");
        }
    }

    // ----- the container image -----

    [Fact]
    public void Everything_the_image_copies_and_publishes_is_in_the_repository()
    {
        string dockerfile = RepoRoot.Read("Dockerfile");
        Regex paths = new(@"^(?:COPY|RUN dotnet (?:publish|restore))\s+(?<args>.+)$", RegexOptions.Multiline, TimeSpan.FromSeconds(5));
        List<string> checked_ = new();

        foreach (Match match in paths.Matches(dockerfile))
        {
            foreach (string token in match.Groups["args"].Value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                // Only repository-relative sources: flags, the build stage's own output and the container paths are not ours.
                if (token.StartsWith('-') || token.StartsWith('/') || token.StartsWith("./") || token.Contains("--from", StringComparison.Ordinal) || token is "." or "-c" or "Release" or "-o")
                {
                    continue;
                }

                string path = RepoRoot.Combine(token.TrimEnd('/'));
                Assert.True(File.Exists(path) || Directory.Exists(path), $"the image copies '{token}', which is not in the repository");
                checked_.Add(token);
            }
        }

        Assert.Contains("src/Bytex.Cli/Bytex.Cli.csproj", checked_);
        Assert.Contains("examples/Bytex.Examples/Bytex.Examples.csproj", checked_);
    }

    [Fact]
    public void The_image_starts_the_cli_under_the_name_it_is_built_as()
    {
        string dockerfile = RepoRoot.Read("Dockerfile");
        string assembly = Regex.Match(RepoRoot.Read("src", "Bytex.Cli", "Bytex.Cli.csproj"), @"<AssemblyName>(?<name>[^<]+)</AssemblyName>", RegexOptions.None, TimeSpan.FromSeconds(1)).Groups["name"].Value;

        Assert.Equal("bytex", assembly);
        Assert.Contains($"ENTRYPOINT [\"dotnet\", \"{assembly}.dll\"", dockerfile, StringComparison.Ordinal);
    }

    // ----- example configurations -----

    private static readonly string[] _knownProviders = ["bytex.importable", "bytex.document"];
    private static readonly string[] _knownDataKinds = ["bars", "quotes", "trades", "deltas"];
    private static readonly string[] _knownEnvironments = ["backtest", "sandbox", "live"];
    private static readonly string[] _knownFactories = ["BINANCE", "BYBIT", "KUCOIN", "TARDIS", "SANDBOX"];

    public static TheoryData<string> ExampleConfigs()
    {
        TheoryData<string> data = new();
        foreach (string file in Directory.EnumerateFiles(RepoRoot.Combine("examples", "configs"), "*.json").Order())
        {
            data.Add(Path.GetFileName(file));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ExampleConfigs))]
    public void An_example_configuration_is_the_shape_the_cli_reads(string name)
    {
        using JsonDocument document = JsonDocument.Parse(RepoRoot.Read("examples", "configs", name));
        JsonElement root = document.RootElement;

        // A search names the run it varies, what it may vary, and the figure it is looking for. What it wraps is an
        // ordinary run, so it is unwrapped here and held to everything below.
        if (root.TryGetProperty("space", out JsonElement space))
        {
            Assert.NotEmpty(space.EnumerateArray());
            foreach (JsonElement parameter in space.EnumerateArray())
            {
                Assert.False(string.IsNullOrWhiteSpace(parameter.GetProperty("path").GetString()), "a parameter needs a path to be written to");
                Assert.True(
                    parameter.TryGetProperty("choices", out _) || (parameter.TryGetProperty("min", out _) && parameter.TryGetProperty("max", out _)),
                    "a parameter needs choices, or a min and a max, or a search has nothing to draw from");
            }

            JsonElement objective = root.GetProperty("objective");
            Assert.Contains(objective.GetProperty("figure").GetString(), BacktestSearchObjective.Figures.Keys);
            Assert.False(string.IsNullOrWhiteSpace(objective.GetProperty("currency").GetString()), "a figure is read off one currency's table");
            root = root.GetProperty("run");
        }

        // Two shapes ship: a backtest run (engine, venues, data) and a trading node (kernel, clients).
        if (root.TryGetProperty("engine", out JsonElement engine))
        {
            Assert.False(string.IsNullOrWhiteSpace(engine.GetProperty("runId").GetString()), "a run needs an id to report under");
            Assert.NotEmpty(root.GetProperty("venues").EnumerateArray());
            foreach (JsonElement venue in root.GetProperty("venues").EnumerateArray())
            {
                Assert.False(string.IsNullOrWhiteSpace(venue.GetProperty("venue").GetString()));
            }

            foreach (JsonElement data in root.GetProperty("data").EnumerateArray())
            {
                Assert.Contains(data.GetProperty("dataKind").GetString(), _knownDataKinds);
            }
        }
        else
        {
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("kernel").GetProperty("traderId").GetString()), "a node reports under its trader id");
            Assert.Contains(root.GetProperty("kernel").GetProperty("environment").GetString(), _knownEnvironments);
            Assert.NotEmpty(root.GetProperty("dataClients").EnumerateArray());
            foreach (JsonElement client in root.GetProperty("dataClients").EnumerateArray().Concat(root.GetProperty("executionClients").EnumerateArray()))
            {
                Assert.Contains(client.GetProperty("factory").GetString(), _knownFactories);
                Assert.False(string.IsNullOrWhiteSpace(client.GetProperty("clientId").GetString()));
            }
        }

        foreach (JsonElement strategy in root.GetProperty("strategies").EnumerateArray())
        {
            Assert.Contains(strategy.GetProperty("providerId").GetString(), _knownProviders);
        }
    }

    [Fact]
    public void Every_example_configuration_is_named_by_the_documentation_that_walks_through_it()
    {
        IEnumerable<string> files = Directory.EnumerateFiles(RepoRoot.Combine("examples", "configs"), "*.json").Select(Path.GetFileName)!;
        string docs = string.Concat(Directory.EnumerateFiles(RepoRoot.Combine("docs"), "*.md", SearchOption.AllDirectories).Select(File.ReadAllText)) + RepoRoot.Read("README.md");

        foreach (string file in files)
        {
            Assert.Contains(file!, docs, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_document_example_configuration_names_a_document_the_cli_can_write()
    {
        using JsonDocument document = JsonDocument.Parse(RepoRoot.Read("examples", "configs", "backtest-document.json"));
        string path = document.RootElement.GetProperty("strategies")[0].GetProperty("payload").GetProperty("documentPath").GetString()!;

        // `bytex documents examples --out <dir>` writes the embedded documents by their own names, so the path the
        // configuration names has to be one of them.
        string name = Path.GetFileName(path);
        IEnumerable<string> embedded = Directory.EnumerateFiles(RepoRoot.Combine("src", "Bytex.Documents", "Examples"), "*.json").Select(Path.GetFileName)!;
        Assert.Contains(name, embedded);
    }

    // ----- the notebook -----

    [Fact]
    public void The_notebook_references_projects_that_exist_and_holds_runnable_cells()
    {
        string notebook = RepoRoot.Read("examples", "notebooks", "first-backtest.ipynb");
        using JsonDocument document = JsonDocument.Parse(notebook);
        JsonElement cells = document.RootElement.GetProperty("cells");

        Assert.NotEmpty(cells.EnumerateArray());
        Assert.Contains(cells.EnumerateArray(), c => c.GetProperty("cell_type").GetString() == "code");

        MatchCollection references = Regex.Matches(notebook, @"#r \\""(?<path>[^""\\]+\.dll)\\""", RegexOptions.None, TimeSpan.FromSeconds(5));
        Assert.NotEmpty(references);
        foreach (Match reference in references)
        {
            // The dll itself exists only after a build, so the project behind it is what is checked.
            string assembly = Path.GetFileNameWithoutExtension(reference.Groups["path"].Value);
            bool exists = File.Exists(RepoRoot.Combine("src", assembly, assembly + ".csproj")) || File.Exists(RepoRoot.Combine("examples", assembly, assembly + ".csproj"));
            Assert.True(exists, $"the notebook references {assembly}, which is no longer a project in this repository");
        }
    }
}
