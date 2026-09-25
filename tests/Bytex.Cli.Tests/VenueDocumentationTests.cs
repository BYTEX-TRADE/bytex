using Bytex.Cli.Tests.Support;

namespace Bytex.Cli.Tests;

// Why: five venues shipped in one day and every page that lists them fell behind. OKX had no row in the integration
// index at all, Bitget's guide column was a dash because no guide existed, three venues' key variables were missing
// from the installation page, and the package list named two adapters out of eight. The KuCoin row still said "spot"
// a day after its futures family shipped.
//
// Every one of those is invisible in exactly the way this repository keeps finding: nothing fails, nothing warns, and
// the venue works perfectly for anyone who already knows it is there. What is lost is somebody being able to find it
// — which for an open engine is most of what shipping a venue means.
//
// Nothing caught it because no test reads the documentation. These do. They are deliberately about presence rather
// than content: whether a guide says the right things is a matter of judgement, but whether a shipped venue has one
// at all is a fact, and it is the fact that went wrong.
public sealed class VenueDocumentationTests
{
    /// <summary>The adapters that ship, read off disk so a new one is noticed the day it lands.</summary>
    private static string[] Shipped() =>
        [.. Directory.EnumerateDirectories(RepoRoot.Combine("src"), "Bytex.Adapters.*")
            .Select(d => Path.GetFileName(d)["Bytex.Adapters.".Length..])
            .Order(StringComparer.Ordinal)];

    /// <summary>Tardis is a history service rather than a venue; it has a guide, and no key table row of the venue kind.</summary>
    private static bool IsVenue(string adapter) => !string.Equals(adapter, "Tardis", StringComparison.Ordinal);

    [Fact]
    public void Every_adapter_that_ships_has_an_integration_guide()
    {
        foreach (string adapter in Shipped())
        {
            string guide = RepoRoot.Combine("docs", "integrations", adapter.ToLowerInvariant() + ".md");

            Assert.True(
                File.Exists(guide),
                $"{adapter} ships and has no docs/integrations/{adapter.ToLowerInvariant()}.md. A venue nobody can "
                + "find is most of the way to a venue that was never added.");
        }
    }

    [Fact]
    public void Every_guide_is_linked_from_the_integration_index()
    {
        // A guide that exists and is not linked is a file in a repository. OKX shipped with neither, and Bitget
        // shipped with a row whose Guide column was an em dash.
        string index = RepoRoot.Read("docs", "integrations", "README.md");

        foreach (string adapter in Shipped())
        {
            string link = $"({adapter.ToLowerInvariant()}.md)";

            Assert.Contains(link, index, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Every_adapter_that_ships_can_be_installed_from_the_installation_page()
    {
        // The package list named Binance, Bybit, Gate, KuCoin and Tardis while eight adapters shipped, so following
        // the page left five of them uninstallable.
        string install = RepoRoot.Read("docs", "getting-started", "installation.md");

        foreach (string adapter in Shipped())
        {
            Assert.Contains($"Bytex.Adapters.{adapter}", install, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Every_venue_that_needs_a_key_says_where_it_is_read_from()
    {
        // The environment variables are declared per venue in the adapter, and a person setting one up reads this
        // page rather than the declaration. Three venues shipped without a row, so their keys were declared,
        // readable over the CLI, and undocumented.
        string install = RepoRoot.Read("docs", "getting-started", "installation.md");

        foreach (string adapter in Shipped().Where(IsVenue))
        {
            string variable = adapter.ToUpperInvariant() + "_API_KEY";
            bool named = install.Contains(variable, StringComparison.Ordinal)
                || install.Contains(adapter.ToUpperInvariant() + "_PRIVATE_KEY", StringComparison.Ordinal);

            Assert.True(
                named,
                $"{adapter} ships and the installation page names neither {variable} nor a private key for it. "
                + "A declared key nobody is told about is a venue that cannot be configured.");
        }
    }

    [Fact]
    public void The_reading_above_really_finds_the_documents()
    {
        // The whole of this file is reading files off disk, so a moved page or a renamed folder would leave every
        // assertion above vacuously true. This is what stops that being silent.
        Assert.NotEmpty(Shipped());
        Assert.True(Shipped().Length >= 8, $"only {Shipped().Length} adapters were found under src/");
        Assert.True(File.Exists(RepoRoot.Combine("docs", "integrations", "README.md")));
        Assert.True(File.Exists(RepoRoot.Combine("docs", "getting-started", "installation.md")));
    }
}
