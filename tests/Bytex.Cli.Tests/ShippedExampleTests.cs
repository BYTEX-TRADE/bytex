using System.Text.Json;
using Bytex.Backtest;
using Bytex.Core.Serialization;
using Bytex.Live;

namespace Bytex.Cli.Tests;

// Why: examples/configs/live-bybit-ema-cross.json - the only live example shipped - could not be loaded at all for
// as long as maxNotionalPerOrder existed, because an instrument-keyed option would not deserialise. Nothing caught
// it because every other test builds its configuration in C#, so no test had ever read a file a user reads. A
// shipped example is the first thing anybody runs and the last thing anybody tested.
//
// These tests load every example through the same call the CLI makes. They do not run anything: what is pinned is
// that somebody who copies a shipped file gets past the first line.
public sealed class ShippedExampleTests
{
    /// <summary>The repository's examples directory, found from the test binary rather than a hard-coded path.</summary>
    private static DirectoryInfo Examples()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "examples", "configs")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return new DirectoryInfo(Path.Combine(dir!.FullName, "examples"));
    }

    /// <summary>
    /// Every JSON file a user would find in the examples folder of a release, which is not every JSON file on disk:
    /// a built checkout has the compiler's own files under bin and obj, and those ship to nobody.
    /// </summary>
    private static IEnumerable<string> ShippedJson()
    {
        DirectoryInfo root = Examples();
        foreach (FileInfo file in root.GetFiles("*.json", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root.FullName, file.FullName).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal))
            {
                continue;
            }

            yield return relative;
        }
    }

    public static TheoryData<string> ConfigFiles()
    {
        TheoryData<string> data = new();
        foreach (string relative in ShippedJson())
        {
            data.Add(relative);
        }

        return data;
    }

    [Fact]
    public void There_are_examples_to_check()
    {
        // A folder that quietly emptied would turn every test below into a pass.
        Assert.NotEmpty(ConfigFiles());
    }

    [Theory]
    [MemberData(nameof(ConfigFiles))]
    public void Every_shipped_example_loads_through_the_call_the_cli_makes(string relative)
    {
        string json = File.ReadAllText(Path.Combine(Examples().FullName, relative));

        // The shape decides the reader, the way the CLI decides it: a node configuration names its clients, a run
        // names its venues, a search names a run and the space to vary it in, a batch names the instruments it
        // sweeps, a reference backtest names what it expects, and anything else in the folder is a strategy document.
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
        {
            List<BacktestRunConfig?> runs = root.EnumerateArray()
                .Select(e => JsonSerializer.Deserialize<BacktestRunConfig>(e.GetRawText(), BytexJson.Options))
                .ToList();
            Assert.All(runs, r => Assert.NotNull(r));
            return;
        }

        if (root.TryGetProperty("executionClients", out _))
        {
            Assert.NotNull(JsonSerializer.Deserialize<TradingNodeConfig>(json, BytexJson.Options));
            return;
        }

        if (root.TryGetProperty("run", out _) && root.TryGetProperty("space", out _))
        {
            BacktestSearchConfig? search = JsonSerializer.Deserialize<BacktestSearchConfig>(json, BytexJson.Options);
            Assert.NotNull(search);

            // Read is not enough for a search: a space that cannot be drawn from reads perfectly well and refuses the
            // moment anybody runs it, and an objective naming a figure no run reports does the same.
            BacktestSearchPlanner.Validate(search!);
            Assert.NotNull(search!.Objective);
            Assert.NotNull(search.Objective!.Fitness());
            Assert.NotEmpty(BacktestSearchPlanner.FirstGeneration(search, new Random(search.Seed)));
            return;
        }

        if (root.TryGetProperty("run", out _) && root.TryGetProperty("instruments", out _))
        {
            Assert.NotNull(JsonSerializer.Deserialize<BacktestBatchConfig>(json, BytexJson.Options));
            return;
        }

        if (root.TryGetProperty("venues", out _))
        {
            Assert.NotNull(JsonSerializer.Deserialize<BacktestRunConfig>(json, BytexJson.Options));
            return;
        }

        if (root.TryGetProperty("expect", out _))
        {
            // A reference backtest has its own tests to run it; all that is asked here is that it names a document.
            Assert.True(
                root.TryGetProperty("document", out _) || root.TryGetProperty("documentPath", out _),
                $"{relative} expects results but names no document");
            return;
        }

        Documents.Schema.StrategyDocument document = Documents.Schema.DocumentJson.Deserialize(json);
        Assert.False(string.IsNullOrWhiteSpace(document.Name), $"{relative} is a document with no name");
    }

    [Fact]
    public void Every_shipped_node_example_survives_a_round_trip()
    {
        // Reading is half of it. A configuration the engine can read and not write back is one no tool can edit, and
        // the defect that started this was in the writing direction of the same converter.
        foreach (string relative in ShippedJson())
        {
            string json = File.ReadAllText(Path.Combine(Examples().FullName, relative));
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("executionClients", out _))
            {
                continue;
            }

            TradingNodeConfig node = JsonSerializer.Deserialize<TradingNodeConfig>(json, BytexJson.Options)!;
            string written = JsonSerializer.Serialize(node, BytexJson.Options);
            TradingNodeConfig again = JsonSerializer.Deserialize<TradingNodeConfig>(written, BytexJson.Options)!;

            // Not "it came back" - "it came back the same". A round trip that silently drops an option is what a
            // dictionary key nobody taught to be a property name looked like from the outside.
            Assert.Equal(written, JsonSerializer.Serialize(again, BytexJson.Options));
            Assert.Equal(node.Kernel.TraderId, again.Kernel.TraderId);
            Assert.Equal(
                node.ExecutionClients.Select(c => (c.Factory, c.ClientId)),
                again.ExecutionClients.Select(c => (c.Factory, c.ClientId)));
            Assert.Equal(
                node.DataClients.Select(c => (c.Factory, c.ClientId)),
                again.DataClients.Select(c => (c.Factory, c.ClientId)));
        }
    }
}
