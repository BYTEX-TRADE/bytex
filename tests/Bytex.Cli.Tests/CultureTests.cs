using System.Text.Json;
using Bytex.Cli.Tests.Support;

namespace Bytex.Cli.Tests;

// Why: the libraries are held to writing numbers invariantly whatever culture a host runs in, and each of them has a
// test for it. The tool is a different case, and a better one: it decides its own runtime, and it decides to have no
// cultures at all. That is worth pinning rather than assuming, because it is a build property somebody could remove
// while tidying and nothing else would notice - and the day it goes, every number this tool prints, and every number
// it parses out of a configuration file, starts depending on the machine it runs on.
public sealed class CultureTests
{
    [Fact]
    public void The_tool_ships_with_no_cultures_at_all()
    {
        // The flag ends up in the runtime configuration beside the executable; that file is what the runtime reads, so
        // it is what this asserts rather than the property in the project file.
        string path = Path.Combine(AppContext.BaseDirectory, "bytex.runtimeconfig.json");

        Assert.True(File.Exists(path), "the tool's runtime configuration is not beside the tests: " + path);

        using JsonDocument config = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement properties = config.RootElement.GetProperty("runtimeOptions").GetProperty("configProperties");

        Assert.True(properties.GetProperty("System.Globalization.Invariant").GetBoolean());
        Assert.True(properties.GetProperty("System.Globalization.PredefinedCulturesOnly").GetBoolean());
    }

    [Fact]
    public async Task What_the_tool_prints_does_not_depend_on_the_machine_it_runs_on()
    {
        // The proof, rather than the property: the child process is handed a culture that writes 1234.5 as "1.234,5"
        // through the variables the runtime reads, and prints the same numbers regardless.
        Dictionary<string, string> hostile = new(StringComparer.Ordinal)
        {
            ["LANG"] = "de_DE.UTF-8",
            ["LC_ALL"] = "de_DE.UTF-8",
            ["DOTNET_SYSTEM_GLOBALIZATION_PREDEFINED_CULTURES_ONLY"] = "false",
        };

        CliResult plain = await CliRunner.RunAsync(["venues", "--json"]);
        CliResult underComma = await CliRunner.RunAsync(["venues", "--json"], environment: hostile);

        Assert.Equal(0, plain.ExitCode);
        Assert.Equal(0, underComma.ExitCode);
        Assert.Equal(plain.StdOut, underComma.StdOut);

        // And there are numbers in it to get wrong: every venue publishes its fees as fractions.
        Assert.Contains("0.001", plain.StdOut, StringComparison.Ordinal);
    }
}
