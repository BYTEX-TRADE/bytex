using Bytex.Cli.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Primitives;
using Bytex.Data;

namespace Bytex.Cli.Tests;

// Why (R9.8, R9.6): `list` says what a catalog holds. What it never answered is the first question somebody with a
// large catalog asks - how much is this, and is any of it wrong - and until now there was no way to put a catalog
// right once it was. A streaming read refuses a catalog whose files overlap, which is honest and, on its own, leaves
// a person with data that reads the slow way and not the fast way and nothing saying which keys to fix.
public sealed class CatalogMaintenanceCommandTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private static readonly MarketKey _instrument = MarketKey.Parse("bx-market:v2/BINANCE/BTCUSDT");

    public void Dispose() => _temp.Dispose();

    private static QuoteTick Quote(long nanos) =>
        new(_instrument, new Price(100m, 2), new Price(101m, 2), new Quantity(1m, 3), new Quantity(1m, 3), new UnixNanos(nanos), new UnixNanos(nanos));

    private async Task<string> CatalogWithOverlapAsync()
    {
        string root = _temp.Combine("catalog");
        Directory.CreateDirectory(root);
        MarketArchive catalog = new(root);

        // Two files whose ranges interleave: 1000-5000 sorts before 2000-3000, so reading them in name order walks
        // forward and then back.
        await catalog.WriteQuoteTicksAsync([Quote(1_000), Quote(5_000)]);
        await catalog.WriteQuoteTicksAsync([Quote(2_000), Quote(3_000)]);
        return root;
    }

    [Fact]
    public async Task Info_says_how_much_is_held_and_whether_anything_would_stop_a_stream()
    {
        CliResult result = await CliRunner.RunAsync(["catalog", "info", "--path", await CatalogWithOverlapAsync()]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("quotes", result.StdOut, StringComparison.Ordinal);
        Assert.Contains(_instrument.Value, result.StdOut, StringComparison.Ordinal);
        Assert.Contains("data set(s)", result.StdOut, StringComparison.Ordinal);

        // The person reading how much data they have is exactly the person about to try to read all of it.
        Assert.Contains("would stop a streaming read", result.StdOut, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>How many rows.</b> The documented list of what this command reports has always included it and the command
    /// never printed one, because the entry it prints from carried no count: the only way to learn how much was in a
    /// data set was to read the file names, which is what this command exists to replace.
    ///
    /// <para>
    /// Four quotes go in, across two files, so the figure cannot be mistaken for a file count or a data-set count -
    /// and the total line has to agree with the per-set line, since a person reads one and trusts the other.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Info_says_how_many_rows_are_held_not_only_how_many_files()
    {
        CliResult result = await CliRunner.RunAsync(["catalog", "info", "--path", await CatalogWithOverlapAsync()]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("4 row(s)", result.StdOut, StringComparison.Ordinal);

        // The per-set line carries it too, beside the file count, rather than only the total at the top.
        string line = result.StdOut.Split('\n').Single(l => l.Contains(_instrument.Value, StringComparison.Ordinal));
        Assert.Contains("4 row(s)", line, StringComparison.Ordinal);
        Assert.Contains("2 file(s)", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// And counting is asked for rather than assumed: a listing is one request to an object store however many files
    /// there are, while a count is a read of every file's footer. A caller that did not ask gets null - "not counted" -
    /// which is why the property is nullable rather than zero.
    /// </summary>
    [Fact]
    public async Task A_listing_does_not_count_rows_unless_it_is_asked_to()
    {
        MarketArchive catalog = new(await CatalogWithOverlapAsync());

        Assert.All(catalog.Entries(), e => Assert.Null(e.Rows));
        Assert.All(catalog.Entries(rows: true), e => Assert.Equal(4, e.Rows));
    }

    [Fact]
    public async Task Check_names_the_problem_and_exits_non_zero_so_a_job_notices()
    {
        CliResult result = await CliRunner.RunAsync(["catalog", "check", "--path", await CatalogWithOverlapAsync()]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("overlap", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("consolidate", result.StdOut, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Consolidate_puts_it_right_and_check_then_has_nothing_to_say()
    {
        string root = await CatalogWithOverlapAsync();

        CliResult consolidated = await CliRunner.RunAsync(["catalog", "consolidate", "--path", root, "--kind", "quotes", "--key", _instrument.Value]);

        Assert.Equal(0, consolidated.ExitCode);
        Assert.Contains("4 record(s)", consolidated.StdOut, StringComparison.Ordinal);

        CliResult checked_ = await CliRunner.RunAsync(["catalog", "check", "--path", root]);

        Assert.Equal(0, checked_.ExitCode);
        Assert.Contains("Nothing to report", checked_.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Consolidating_something_the_catalog_does_not_hold_says_so_rather_than_succeeding_quietly()
    {
        CliResult result = await CliRunner.RunAsync(["catalog", "consolidate", "--path", await CatalogWithOverlapAsync(), "--kind", "bars"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("No data set matches", result.AllOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_on_a_healthy_catalog_reports_nothing_and_exits_zero()
    {
        string root = _temp.Combine("healthy");
        Directory.CreateDirectory(root);
        MarketArchive catalog = new(root);
        await catalog.WriteQuoteTicksAsync([Quote(1_000), Quote(2_000)]);

        CliResult result = await CliRunner.RunAsync(["catalog", "check", "--path", root]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Nothing to report", result.StdOut, StringComparison.Ordinal);
    }
}
