using System.Text.Json;
using Bytex.Cli.Tests.Support;

namespace Bytex.Cli.Tests;

// Why: `bytex venues` is how a host that runs this engine as a child process learns what the engine can reach, and it
// is the only route for one that does not reference the assemblies. Its JSON is serialised from the declaration, so a
// field added there arrives for free; its human-readable form is hand-written, so a field added there arrives only if
// somebody remembers - and the field this file was written for is the one that tells two families of one venue apart.
// Printing the families of Binance without saying which settles in the coin leaves a reader choosing between two lines
// that differ by a word the venue made up.
public sealed class VenuesCommandTests
{
    [Fact]
    public async Task Every_family_prints_what_collateralises_it()
    {
        CliResult result = await CliRunner.RunAsync(["venues"]);

        Assert.Equal(0, result.ExitCode);

        // Spot borrows nothing; a linear family is collateralised in what its instruments are priced in; an inverse
        // one in the coin; and Bitget's two perpetual families name the single currency that separates them.
        Assert.Contains("nothing is borrowed", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("collateral: what the instrument is priced in", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("collateral: the instrument's own base currency (inverse)", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("collateral: USDC", result.StdOut, StringComparison.Ordinal);

        // And no family is printed without it, which is the part that would rot silently. A family's line is the one
        // indented by two spaces; everything under it is indented by four.
        string[] familyLines = [.. result.StdOut.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.StartsWith("  ", StringComparison.Ordinal) && !l.StartsWith("    ", StringComparison.Ordinal))];

        Assert.Equal(21, familyLines.Length);

        foreach (string line in familyLines)
        {
            Assert.True(
                line.Contains("collateral: ", StringComparison.Ordinal) || line.Contains("nothing is borrowed", StringComparison.Ordinal),
                $"this family line says nothing about what collateralises it: {line}");
        }
    }

    [Fact]
    public async Task The_json_carries_the_collateral_of_every_family()
    {
        // The route a host actually reads. Serialised rather than hand-listed, so this asserts the vocabulary a
        // consumer pins: a kind by name, and currencies only where a venue fixes them.
        CliResult result = await CliRunner.RunAsync(["venues", "--json"]);

        Assert.Equal(0, result.ExitCode);

        using JsonDocument document = JsonDocument.Parse(result.StdOut);
        int families = 0;
        bool sawFixedSet = false;

        foreach (JsonElement venue in document.RootElement.EnumerateArray())
        {
            foreach (JsonElement family in venue.GetProperty("families").EnumerateArray())
            {
                JsonElement collateral = family.GetProperty("collateral");
                string kind = collateral.GetProperty("kind").GetString()!;

                Assert.Contains(kind, new[] { "none", "quoteCurrency", "baseCurrency", "currencies" }, StringComparer.Ordinal);

                int currencies = collateral.GetProperty("currencies").GetArrayLength();
                Assert.Equal(kind == "currencies", currencies > 0);
                sawFixedSet |= currencies > 0;
                families++;
            }
        }

        Assert.Equal(21, families);
        Assert.True(sawFixedSet, "no family names a fixed collateral set, so the kind that carries currencies is untested here");
    }
}
