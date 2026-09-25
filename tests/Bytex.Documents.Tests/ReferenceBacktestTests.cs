using System.Globalization;
using System.Text.Json;
using Bytex.Backtest;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Serialization;
using Bytex.Documents.Runtime;
using Bytex.Documents.Schema;
using Xunit;

namespace Bytex.Documents.Tests;

// Why: R12.9. A published configuration with the numbers it produces, run on every commit. Every other test in this
// repository says a rule holds; these say the engine as a whole still answers what it answered - the return, the
// drawdown, how many trades and what they cost, to the cent. A change that moves any of them fails here with the name
// of the reference and both numbers, and then it is a decision: the engine got more honest, or it broke. Either way
// nobody finds out from a user.
public sealed class ReferenceBacktestTests
{
    /// <summary>Where the published references live, beside the examples they run.</summary>
    private static string Directory => Path.Combine(RepositoryRoot(), "examples", "reference");

    public static TheoryData<string> References()
    {
        TheoryData<string> data = new();
        foreach (string file in System.IO.Directory.GetFiles(Directory, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(file));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(References))]
    public void A_reference_backtest_still_gives_the_numbers_it_is_published_with(string file)
    {
        ReferenceRun reference = Read(file);
        BacktestResult result = Run(reference);
        CurrencyStatistics stats = result.Currencies.Single(c => c.Currency.Code == reference.Currency);

        // One assertion per published number, so a failure names the one that moved rather than "the result differs".
        Assert.Equal(reference.Expect.Fills, result.Fills.Count);
        Assert.Equal(reference.Expect.Positions, result.TotalPositions);
        Assert.Equal(reference.Expect.ClosedPositions, result.Trades.ClosedPositions);
        Assert.Equal(Parse(reference.Expect.EndingBalance), stats.EndingBalance, 8);
        Assert.Equal(Parse(reference.Expect.RealizedPnl), stats.RealizedPnl, 8);
        Assert.Equal(Parse(reference.Expect.Commissions), stats.TotalCommissions, 8);
        Assert.Equal(Parse(reference.Expect.MaxDrawdown), stats.MaxDrawdown, 8);
        Assert.Equal(reference.Expect.Simulation, result.Simulation);
    }

    [Fact]
    public void Every_shipped_example_is_a_reference()
    {
        // A published example nobody runs in CI is a published example that can rot. The set here is the set there.
        string[] referenced = System.IO.Directory.GetFiles(Directory, "*.json")
            .Select(f => Read(Path.GetFileName(f)).Document)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "breakout-retest", "ema-cross", "support-bounce" }, referenced);
    }


    private static BacktestResult Run(ReferenceRun reference)
    {
        StrategyDocument document = Fixtures.Example(reference.Document);
        Instrument instrument = Fixtures.BtcUsdt();
        BarType barType = Fixtures.MinuteBars(instrument);
        using BacktestEngine engine = new(new BacktestEngineConfig { RunId = reference.Name });
        engine.AddInstrument(instrument);
        engine.AddVenue(new SimulatedVenueConfig
        {
            Venue = Fixtures.Sim,
            AccountType = AccountType.Cash,
            StartingBalances = reference.StartingBalances.Select(Money.Parse).ToList(),
        });
        engine.AddData(Fixtures.RandomWalkBars(instrument, barType, reference.Bars, Parse(reference.StartPrice), reference.Seed).Cast<IData>());
        engine.AddStrategy(new DocumentStrategy(new DocumentStrategyConfig
        {
            Document = document,
            StrategyId = new StrategyId("Doc-001"),
        }));
        engine.Run();
        return engine.GetResult();
    }

    private static ReferenceRun Read(string file) =>
        JsonSerializer.Deserialize<ReferenceRun>(File.ReadAllText(Path.Combine(Directory, file)), BytexJson.Options)
        ?? throw new InvalidOperationException($"Reference {file} could not be read.");

    private static decimal Parse(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);

    /// <summary>Walks up from the test binaries to the repository, which is where the published references are.</summary>
    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Bytex.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root is not above the test binaries.");
    }

    private sealed record ReferenceRun
    {
        public required string Name { get; init; }

        public required string Description { get; init; }

        public required string Document { get; init; }

        public required int Bars { get; init; }

        public required int Seed { get; init; }

        public required string StartPrice { get; init; }

        public required IReadOnlyList<string> StartingBalances { get; init; }

        public required string Currency { get; init; }

        public required ReferenceExpectation Expect { get; init; }
    }

    private sealed record ReferenceExpectation
    {
        public required int Fills { get; init; }

        public required int Positions { get; init; }

        public required int ClosedPositions { get; init; }

        public required string EndingBalance { get; init; }

        public required string RealizedPnl { get; init; }

        public required string Commissions { get; init; }

        public required string MaxDrawdown { get; init; }

        public required IReadOnlyList<string> Simulation { get; init; }
    }
}
