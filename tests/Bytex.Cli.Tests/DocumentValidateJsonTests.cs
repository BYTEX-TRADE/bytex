using System.Text.Json;
using Bytex.Cli.Tests.Support;

namespace Bytex.Cli.Tests;

// Why: --json is a promise about the SHAPE of the output, and a flag that keeps that promise only when things go
// well is worse than no flag. Something parsing it - a script, a CI step, a model deciding what to fix next - gets a
// parse error where it should have got the finding, and the finding is the one piece of information it needed.
//
// `documents validate` read the document BEFORE it looked at --json, so a document that could not be read at all
// printed "BLOCK JSON: The JSON property 'evaluationn' could not be mapped..." as prose on stderr. The exit code was
// right, which is what let it survive: anything checking only the code was satisfied, and anything reading the
// output was not. Strict document reading did not create this - broken JSON always did it - but it turned a rare
// case into the common one, because a misspelled member now stops the read.
//
// The same hole was one condition further down, where an unknown --environment printed prose too, and a file that
// does not exist threw with no handling at all. So the fix is one failure path that honours the flag rather than
// three places remembering to, and these tests hold every way the command can fail to the same shape.
public sealed class DocumentValidateJsonTests
{
    private const string Sound = """
        {
          "schemaVersion": "2.0",
          "id": "validate-json",
          "name": "Validate json",
          "instruments": [ { "ref": "primary", "marketKey": "bx-market:v2/SIM/BTCUSDT" } ],
          "candleSeriesDefinitions": [ { "ref": "main", "instrument": "primary", "step": 1, "aggregation": "minute", "priceType": "last", "source": "provider" } ],
          "parameters": [],
          "nodes": [
            { "id": "bars", "type": "data.bars", "params": { "candleSeries": "main" } },
            { "id": "ema", "type": "ind.ema", "params": { "period": 5 } },
            { "id": "positive", "type": "cond.compare", "params": { "op": "gt", "value": 0 } },
            { "id": "buy", "type": "act.order", "params": { "side": "buy", "orderType": "market", "sizing": { "mode": "fixed", "value": 0.05 }, "onlyWhenFlat": true } }
          ],
          "edges": [
            { "from": "bars:bars", "to": "ema:bars" },
            { "from": "ema:value", "to": "positive:a" },
            { "from": "positive:out", "to": "buy:trigger" }
          ],
          "repeat": { "enabled": true }
        }
        """;

    /// <summary>
    /// A member the schema does not model: the document cannot be read, so there is no report - and the output must
    /// still be a report-shaped object rather than the exception's own words.
    /// </summary>
    [Fact]
    public async Task A_document_that_cannot_be_read_is_reported_as_json()
    {
        using TempDirectory temp = new();
        string path = temp.File("unreadable.json", Sound.Replace("\"repeat\"", "\"evaluationn\": \"quote\", \"repeat\"", StringComparison.Ordinal));

        CliResult result = await CliRunner.RunAsync(["documents", "validate", "-d", path, "--json"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("evaluationn", Finding(result, "the member that could not be mapped has to be named"), StringComparison.Ordinal);
    }

    /// <summary>Broken JSON, which did this long before a member could stop the read.</summary>
    [Fact]
    public async Task Broken_json_is_reported_as_json()
    {
        using TempDirectory temp = new();
        string path = temp.File("broken.json", "{ \"schemaVersion\": \"1.0\", ");

        CliResult result = await CliRunner.RunAsync(["documents", "validate", "-d", path, "--json"]);

        Assert.Equal(1, result.ExitCode);
        Assert.NotEmpty(Finding(result, "a document that is not JSON at all still answers in JSON"));
    }

    /// <summary>An environment this engine does not have, which was the same hole one condition further down.</summary>
    [Fact]
    public async Task An_unknown_environment_is_reported_as_json()
    {
        using TempDirectory temp = new();
        string path = temp.File("sound.json", Sound);

        CliResult result = await CliRunner.RunAsync(["documents", "validate", "-d", path, "--json", "--environment", "liveish"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("liveish", Finding(result, "the environment that was asked for has to be named"), StringComparison.Ordinal);
    }

    /// <summary>A file that is not there, which threw with nothing handling it.</summary>
    [Fact]
    public async Task A_missing_file_is_reported_as_json()
    {
        using TempDirectory temp = new();

        CliResult result = await CliRunner.RunAsync(["documents", "validate", "-d", Path.Combine(temp.Path, "absent.json"), "--json"]);

        Assert.Equal(1, result.ExitCode);
        Assert.NotEmpty(Finding(result, "a path that does not exist answers in JSON rather than throwing"));
    }

    /// <summary>
    /// And the shape it succeeds with, so the failures above are held to the SAME keys rather than to a second
    /// format nobody documented. A consumer parses one thing whatever happened.
    /// </summary>
    [Fact]
    public async Task A_document_that_reads_keeps_the_same_keys()
    {
        using TempDirectory temp = new();
        string path = temp.File("sound.json", Sound);

        CliResult result = await CliRunner.RunAsync(["documents", "validate", "-d", path, "--json"]);

        using JsonDocument report = JsonDocument.Parse(Output(result));
        foreach (string key in new[] { "name", "valid", "stoppedAt", "findings" })
        {
            Assert.True(report.RootElement.TryGetProperty(key, out _), $"the success shape is missing '{key}'");
        }
    }

    /// <summary>The whole output, whichever stream it came out on: a caller redirects both or neither.</summary>
    private static string Output(CliResult result) =>
        string.IsNullOrWhiteSpace(result.StdOut) ? result.StdErr : result.StdOut;

    /// <summary>
    /// The first block of a report-shaped answer, parsed rather than pattern-matched: a test that searched the text
    /// for a word would pass on the prose this is meant to replace.
    /// </summary>
    private static string Finding(CliResult result, string because)
    {
        string text = Output(result);
        JsonDocument report;
        try
        {
            report = JsonDocument.Parse(text);
        }
        catch (JsonException e)
        {
            throw new Xunit.Sdk.XunitException(
                $"--json did not answer with JSON, so {because}. Exception: {e.Message}. Output was: {text}");
        }

        using (report)
        {
            Assert.False(report.RootElement.GetProperty("valid").GetBoolean());
            JsonElement findings = report.RootElement.GetProperty("findings");
            Assert.NotEqual(0, findings.GetArrayLength());
            JsonElement first = findings[0];
            Assert.Equal("block", first.GetProperty("level").GetString());
            Assert.False(string.IsNullOrWhiteSpace(first.GetProperty("code").GetString()), "a finding needs a code");
            return first.GetProperty("message").GetString() ?? string.Empty;
        }
    }
}
