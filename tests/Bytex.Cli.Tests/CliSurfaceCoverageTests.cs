using System.Text.Json;
using System.Text.RegularExpressions;
using Bytex.Cli.Tests.Support;

namespace Bytex.Cli.Tests;

// Why: the command line IS this engine's public API - the CLI's own types are internal - so an option nobody tries
// is a promise nobody has checked. Counting them found four options and one command with no test at all, and one of
// the four was --environment, which is the same thing a shipped defect got wrong: a document under a live node
// followed backtest rules because nothing carried the environment to it.
//
// The last test counts them from the source, so a new option or command cannot arrive with nobody having run it.
public sealed class CliSurfaceCoverageTests
{
    [Fact]
    public async Task Schema_prints_a_json_schema_for_documents()
    {
        // The command a tool generating documents reads first, and it had no test at all.
        CliResult result = await CliRunner.RunAsync(["documents", "schema"]);

        Assert.True(result.ExitCode == 0, result.AllOutput);
        using JsonDocument schema = JsonDocument.Parse(result.StdOut);
        Assert.Equal(JsonValueKind.Object, schema.RootElement.ValueKind);
        Assert.True(schema.RootElement.TryGetProperty("properties", out JsonElement properties), result.StdOut);

        // The field a shipped document cannot do without, and the one added most recently, so the printed schema
        // cannot silently fall behind the reader.
        Assert.True(properties.TryGetProperty("nodes", out _));
        Assert.True(properties.TryGetProperty("account", out JsonElement account), "the schema does not mention account.leverage");
        Assert.True(account.GetProperty("properties").TryGetProperty("leverage", out _));
    }

    [Theory]
    [InlineData("backtest")]
    [InlineData("sandbox")]
    [InlineData("live")]
    public async Task Validate_applies_the_rules_of_the_environment_it_is_given(string environment)
    {
        // --environment had no test, and getting an environment to the thing that decides by it is exactly what a
        // shipped defect got wrong. A document with no AI-gated conditions passes in all three; what is pinned is
        // that the option is accepted and reaches the validator rather than being ignored.
        using TempDirectory temp = new();
        string documents = temp.Combine("documents");
        Assert.Equal(0, (await CliRunner.RunAsync(["documents", "examples", "-o", documents])).ExitCode);
        string document = Directory.GetFiles(documents, "ema-cross.json").Single();

        CliResult result = await CliRunner.RunAsync(["documents", "validate", "--document", document, "--environment", environment]);

        Assert.True(result.ExitCode == 0, result.AllOutput);
    }

    [Fact]
    public async Task Validate_refuses_an_environment_it_does_not_know()
    {
        // An unknown mode must not quietly become the default, because the default is the permissive one.
        using TempDirectory temp = new();
        string documents = temp.Combine("documents");
        Assert.Equal(0, (await CliRunner.RunAsync(["documents", "examples", "-o", documents])).ExitCode);
        string document = Directory.GetFiles(documents, "ema-cross.json").Single();

        CliResult result = await CliRunner.RunAsync(["documents", "validate", "--document", document, "--environment", "wishful"]);

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task Validate_with_a_catalog_checks_the_instrument_the_document_names()
    {
        // --catalog turns on the checks that need real data, and it had no test. An empty catalog cannot vouch for
        // the instrument, so the document that validated without one now says so.
        using TempDirectory temp = new();
        string documents = temp.Combine("documents");
        Assert.Equal(0, (await CliRunner.RunAsync(["documents", "examples", "-o", documents])).ExitCode);
        string document = Directory.GetFiles(documents, "ema-cross.json").Single();
        string catalog = temp.Combine("empty-catalog");
        Directory.CreateDirectory(catalog);

        CliResult without = await CliRunner.RunAsync(["documents", "validate", "--document", document]);
        CliResult with = await CliRunner.RunAsync(["documents", "validate", "--document", document, "--catalog", catalog]);

        Assert.True(without.ExitCode == 0, without.AllOutput);
        Assert.NotEqual(0, with.ExitCode);
        Assert.Contains("INSTRUMENT", with.AllOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Every_command_and_option_the_cli_offers_is_exercised_by_a_test()
    {
        // The guard on the surface. Counting these by hand found --control-heartbeat, --reconcile-interval,
        // --catalog, --environment and the schema command untried; doing it by hand again is not a plan. An option
        // is counted as covered when any of its forms - the long name or a short alias - appears in a test.
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "Bytex.Cli", "Program.cs")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        string source = string.Join(
            "\n",
            new DirectoryInfo(Path.Combine(dir!.FullName, "src", "Bytex.Cli")).GetFiles("*.cs")
                .Where(f => !f.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Select(f => File.ReadAllText(f.FullName)));

        string tests = string.Join(
            "\n",
            new DirectoryInfo(Path.Combine(dir.FullName, "tests", "Bytex.Cli.Tests")).GetFiles("*.cs", SearchOption.AllDirectories)
                .Where(f => !f.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    && !f.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Select(f => File.ReadAllText(f.FullName)));

        List<string> untried = new();
        foreach (Match match in Regex.Matches(source, "new\\(\"(?<long>--[a-z][a-z0-9-]*)\"(?<aliases>(?:\\s*,\\s*\"-[a-zA-Z]\")*)\\)"))
        {
            string[] forms = [match.Groups["long"].Value, .. Regex.Matches(match.Groups["aliases"].Value, "\"(?<a>-[a-zA-Z])\"").Select(a => a.Groups["a"].Value)];
            if (!forms.Any(f => tests.Contains($"\"{f}\"", StringComparison.Ordinal)))
            {
                untried.Add(match.Groups["long"].Value);
            }
        }

        foreach (Match match in Regex.Matches(source, "Command\\s+\\w+\\s*=\\s*new\\(\"(?<name>[a-z][a-z0-9-]*)\""))
        {
            string name = match.Groups["name"].Value;
            if (!tests.Contains($"\"{name}\"", StringComparison.Ordinal))
            {
                untried.Add(name);
            }
        }

        Assert.True(
            untried.Count == 0,
            $"the CLI offers commands or options no test runs: {string.Join(", ", untried.Distinct().Order(StringComparer.Ordinal))}. "
            + "The command line is this engine's public API, so an option nobody tries is a promise nobody has checked.");
    }
}
