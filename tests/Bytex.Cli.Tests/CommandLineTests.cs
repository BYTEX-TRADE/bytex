using System.Text.RegularExpressions;
using Bytex.Cli.Tests.Support;

namespace Bytex.Cli.Tests;

// Why: the command line is the CLI's public contract (docs/getting-started/cli.md). Wrong usage must fail with
// a non-zero exit code and a message naming the problem, because scripts and CI rely on both.
// Every test runs the real executable as a child process; nothing here needs the network.
public sealed class CommandLineTests
{
    [Fact]
    public async Task Version_prints_the_tool_name_with_a_three_part_version_and_exits_zero()
    {
        CliResult result = await CliRunner.RunAsync(["version"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Matches(new Regex(@"^bytex \d+\.\d+\.\d+$"), result.StdOut.Trim());
    }

    [Fact]
    public async Task Help_lists_the_documented_commands_and_global_options()
    {
        CliResult result = await CliRunner.RunAsync(["--help"]);

        Assert.Equal(0, result.ExitCode);
        foreach (string expected in new[] { "backtest", "run", "catalog", "version", "--log-level", "--plugins" })
        {
            Assert.Contains(expected, result.StdOut);
        }
    }

    [Fact]
    public async Task Catalog_help_lists_its_four_subcommands()
    {
        CliResult result = await CliRunner.RunAsync(["catalog", "--help"]);

        Assert.Equal(0, result.ExitCode);
        foreach (string expected in new[] { "list", "import-csv", "add-instrument", "fetch-instruments" })
        {
            Assert.Contains(expected, result.StdOut);
        }
    }

    [Fact]
    public async Task Run_help_documents_the_env_file_and_duration_options()
    {
        CliResult result = await CliRunner.RunAsync(["run", "--help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("--env-file", result.StdOut);
        Assert.Contains("--duration", result.StdOut);
        Assert.Contains("--config", result.StdOut);
    }

    [Fact]
    public async Task No_command_is_an_error()
    {
        CliResult result = await CliRunner.RunAsync([]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Required command was not provided", result.StdErr);
    }

    [Fact]
    public async Task An_unknown_command_is_an_error_that_names_it()
    {
        CliResult result = await CliRunner.RunAsync(["optimise"]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("optimise", result.StdErr);
    }

    [Theory]
    [InlineData("backtest", "--config")]
    [InlineData("run", "--config")]
    [InlineData("catalog list", "--path")]
    [InlineData("catalog add-instrument --path x", "--file")]
    [InlineData("catalog import-csv --path x --file y --kind bars", "--instrument")]
    [InlineData("catalog fetch-instruments --path x", "--venue")]
    public async Task A_missing_required_option_is_an_error_that_names_the_option(string command, string option)
    {
        CliResult result = await CliRunner.RunAsync(command.Split(' '));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains($"'{option}' is required", result.StdErr);
    }

    [Fact]
    public async Task An_unparseable_duration_is_rejected_before_anything_runs()
    {
        CliResult result = await CliRunner.RunAsync(["run", "--config", "node.json", "--duration", "soon"]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("soon", result.StdErr);
    }

    [Fact]
    public async Task The_short_alias_c_is_accepted_for_config()
    {
        using TempDirectory temp = new();
        string missing = temp.Combine("no-such-config.json");

        CliResult result = await CliRunner.RunAsync(["backtest", "-c", missing]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain("Unrecognized", result.AllOutput);
        Assert.Contains("no-such-config.json", result.AllOutput); // it got as far as opening the file
    }

    [Fact]
    public async Task An_unknown_venue_for_fetch_instruments_fails_without_touching_the_network()
    {
        using TempDirectory temp = new();

        CliResult result = await CliRunner.RunAsync(["catalog", "fetch-instruments", "--path", temp.Combine("catalog"), "--venue", "KRAKEN"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Unknown venue", result.StdErr);
    }
}
