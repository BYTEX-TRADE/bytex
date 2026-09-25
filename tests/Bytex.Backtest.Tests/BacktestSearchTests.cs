using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Plugins;
using Bytex.Data;

namespace Bytex.Backtest.Tests;

// Why: R8.23. A grid of six parameters at ten values each is a million runs, so a space worth searching is a space
// nobody can cross. What a search offers instead is a few hundred runs and no guarantee - which makes the things that
// must hold sharper, not vaguer: it may only ever propose points the space allows, the same seed must propose the
// same points (a search nobody can repeat is a result nobody can check), a candidate whose run produced no figure
// must never be bred from, and the best it ever saw must not be lost by a later generation. And the claim underneath
// all of it, checked here against a space small enough to cross: over a space a grid could do, the search finds what
// the grid would have found.
public sealed class BacktestSearchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bytex-search-tests-" + Guid.NewGuid().ToString("N"));
    private readonly CurrencyPair _spot = TestInstruments.Spot();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string CatalogPath => Path.Combine(_root, "catalog");

    private async Task WriteCatalogAsync()
    {
        ParquetDataCatalog catalog = new(CatalogPath);
        await catalog.WriteInstrumentsAsync([_spot]);
        await catalog.WriteBarsAsync(GoldenEmaCrossBacktestTests.Bars(_spot));
    }

    private static BacktestNode Node()
    {
        PluginRegistry registry = new();
        registry.AddStrategyProvider(new EmaCrossProvider());
        return new BacktestNode(registry);
    }

    private BacktestRunConfig Run(string runId = "search") => new()
    {
        Engine = new BacktestEngineConfig { RunId = runId },
        Venues = [new BacktestVenueConfig { Venue = "SIM", StartingBalances = ["10000 USDT"] }],
        Data = [new BacktestDataConfig { CatalogPath = CatalogPath, DataKind = "bars", BarType = Scripted.MinuteBars(_spot) }],
        Strategies = [EmaCrossProvider.Definition(_spot.Id, Scripted.MinuteBars(_spot), 2m)],
    };

    /// <summary>A space of eight trade sizes: small enough that a grid can cross it and a search can be checked against one.</summary>
    private static BacktestSearchParameter TradeSize() =>
        new() { Path = "tradeSize", Min = 1m, Max = 8m, Step = 1m };

    private BacktestSearchConfig Search(string runId = "search") => new()
    {
        Run = Run(runId),
        Space = [TradeSize()],
        Population = 6,
        Generations = 4,
        Elites = 2,
        Seed = 11,
    };

    /// <summary>What the search is looking for here: the run that made the most of the money it was given.</summary>
    private static decimal Fitness(BacktestBatchRow row) => row.ReturnPercent;

    // ----- what it may propose -----

    [Fact]
    public void Every_value_a_search_proposes_is_one_the_space_allows()
    {
        // The whole safety of a search: it never wanders off the grid it was given, so anything it reports can be
        // re-run as a batch of one and will be the same run.
        BacktestSearchConfig search = Search() with
        {
            Population = 200,
            Space = [TradeSize(), new BacktestSearchParameter { Path = "strategyId", Choices = ["A", "B", "C"] }],
        };
        HashSet<string> sizes = [.. TradeSize().Values()];

        IReadOnlyList<BacktestParameterSet> first = BacktestSearchPlanner.FirstGeneration(search, new Random(3));

        Assert.Equal(200, first.Count);
        Assert.All(first, point =>
        {
            Assert.Contains(point.Values["tradeSize"], sizes);
            Assert.Contains(point.Values["strategyId"], new[] { "A", "B", "C" });
        });
    }

    [Theory]
    [InlineData(1, 8, 1, 8)]
    [InlineData(0, 1, 0.25, 5)]
    [InlineData(10, 10, 1, 1)]
    [InlineData(1, 10, 4, 3)]
    public void A_range_is_walked_in_steps_and_never_past_its_end(decimal min, decimal max, decimal step, int expected)
    {
        BacktestSearchParameter parameter = new() { Path = "x", Min = min, Max = max, Step = step };

        IReadOnlyList<string> values = parameter.Values();

        Assert.Equal(expected, values.Count);
        Assert.All(values, v => Assert.InRange(decimal.Parse(v, System.Globalization.CultureInfo.InvariantCulture), min, max));
    }

    [Fact]
    public void The_same_seed_proposes_the_same_candidates_and_a_different_one_does_not()
    {
        // A search nobody can repeat is a result nobody can check.
        BacktestSearchConfig search = Search() with { Population = 40 };

        string[] once = [.. BacktestSearchPlanner.FirstGeneration(search, new Random(search.Seed)).Select(p => p.Values["tradeSize"])];
        string[] again = [.. BacktestSearchPlanner.FirstGeneration(search, new Random(search.Seed)).Select(p => p.Values["tradeSize"])];
        string[] elsewhere = [.. BacktestSearchPlanner.FirstGeneration(search, new Random(search.Seed + 1)).Select(p => p.Values["tradeSize"])];

        Assert.Equal(once, again);
        Assert.NotEqual(once, elsewhere);
    }

    // ----- what it breeds -----

    private static BacktestSearchCandidate Judged(string tradeSize, decimal? fitness, int index = 0) => new()
    {
        Generation = 0,
        Index = index,
        Point = new BacktestParameterSet { Values = new Dictionary<string, string>(StringComparer.Ordinal) { ["tradeSize"] = tradeSize } },
        Run = new BacktestBatchRun { Index = index, RunId = "r-" + index },
        Fitness = fitness,
    };

    [Fact]
    public void The_best_of_a_generation_goes_into_the_next_one_unchanged()
    {
        // Without this a generation can be worse than the one before it, and a search that goes backwards has not
        // searched: the answer would depend on which generation it happened to stop at.
        BacktestSearchConfig search = Search() with { Population = 6, Elites = 2 };
        List<BacktestSearchCandidate> judged =
        [
            Judged("1", 5m, 0), Judged("2", 90m, 1), Judged("3", 40m, 2),
            Judged("4", 1m, 3), Judged("5", 70m, 4), Judged("6", 20m, 5),
        ];

        IReadOnlyList<BacktestParameterSet> next = BacktestSearchPlanner.NextGeneration(search, judged, new Random(1));

        Assert.Equal(6, next.Count);
        Assert.Equal(["2", "5"], next.Take(2).Select(p => p.Values["tradeSize"]));
    }

    [Fact]
    public void A_candidate_that_produced_no_figure_is_never_bred_from()
    {
        // A run that failed is not evidence that its parameters are bad; breeding from it would spread a point that
        // was never judged at all.
        BacktestSearchConfig search = Search() with { Population = 20, Elites = 0, MutationRate = 0, CrossoverRate = 0.5 };
        List<BacktestSearchCandidate> judged = [Judged("7", 10m, 0), Judged("1", null, 1), Judged("2", null, 2)];

        IReadOnlyList<BacktestParameterSet> next = BacktestSearchPlanner.NextGeneration(search, judged, new Random(5));

        // The one judged candidate is the only parent there is, and with nothing mutating, every child is it.
        Assert.All(next, p => Assert.Equal("7", p.Values["tradeSize"]));
    }

    [Fact]
    public void A_generation_where_nothing_finished_is_drawn_afresh()
    {
        // Repeating points that told us nothing would spend a whole generation learning nothing twice.
        BacktestSearchConfig search = Search() with { Population = 20 };
        List<BacktestSearchCandidate> judged = [Judged("3", null, 0), Judged("3", null, 1)];

        IReadOnlyList<BacktestParameterSet> next = BacktestSearchPlanner.NextGeneration(search, judged, new Random(2));

        Assert.Equal(20, next.Count);
        Assert.True(next.Select(p => p.Values["tradeSize"]).Distinct().Count() > 1, "a fresh generation is drawn from the space, not copied");
    }

    [Fact]
    public void A_generation_is_the_size_it_was_asked_for_however_few_candidates_survived()
    {
        BacktestSearchConfig search = Search() with { Population = 9, Elites = 2 };

        IReadOnlyList<BacktestParameterSet> next = BacktestSearchPlanner.NextGeneration(search, [Judged("4", 1m)], new Random(4));

        Assert.Equal(9, next.Count);
    }

    // ----- what it refuses -----

    public static TheoryData<string, BacktestSearchConfig> Impossible()
    {
        BacktestRunConfig run = new()
        {
            Engine = new BacktestEngineConfig { RunId = "x" },
            Venues = [new BacktestVenueConfig { Venue = "SIM", StartingBalances = ["1 USDT"] }],
            Data = [],
        };
        BacktestSearchConfig ok = new() { Run = run, Space = [new BacktestSearchParameter { Path = "a", Min = 1, Max = 2 }] };
        return new TheoryData<string, BacktestSearchConfig>
        {
            { "give it a space", ok with { Space = [] } },
            { "nothing to breed from", ok with { Population = 1 } },
            { "at least one generation", ok with { Generations = 0 } },
            { "leave room for a child", ok with { Elites = 24 } },
            { "two candidates compete", ok with { TournamentSize = 1 } },
            { "a chance between 0 and 1", ok with { MutationRate = 1.5 } },
            { "a chance between 0 and 1", ok with { CrossoverRate = -1 } },
            { "stop before it started", ok with { StopAfterGenerationsWithoutImprovement = 0 } },
            { "cannot draw from both", ok with { Space = [new BacktestSearchParameter { Path = "a", Min = 1, Max = 2, Choices = ["1"] }] } },
            { "nothing to draw from", ok with { Space = [new BacktestSearchParameter { Path = "a" }] } },
            { "more than nothing", ok with { Space = [new BacktestSearchParameter { Path = "a", Min = 1, Max = 2, Step = 0 }] } },
            { "ends before it begins", ok with { Space = [new BacktestSearchParameter { Path = "a", Min = 5, Max = 1 }] } },
            { "in the space twice", ok with { Space = [new BacktestSearchParameter { Path = "a", Min = 1, Max = 2 }, new BacktestSearchParameter { Path = "a", Choices = ["3"] }] } },
            { "names more than one", ok with { Space = [new BacktestSearchParameter { Path = "a", Min = 1, Max = 2 }, new BacktestSearchParameter { Path = "b", Choices = ["3"], Strategy = 1 }] } },
        };
    }

    [Theory]
    [MemberData(nameof(Impossible))]
    public void A_search_that_cannot_be_made_is_refused_before_a_single_run(string because, BacktestSearchConfig search)
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => BacktestSearchPlanner.Validate(search));

        Assert.Contains(because, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_objective_naming_a_figure_no_run_reports_is_refused_and_says_what_there_is()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => new BacktestSearchObjective { Figure = "alpha", Currency = "USDT" }.Fitness());

        Assert.Contains("is not a figure a run reports", error.Message, StringComparison.Ordinal);
        Assert.Contains("returnPercent", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_objective_that_wants_less_of_something_is_the_same_order_upside_down()
    {
        BacktestBatchRow shallow = Row(drawdown: 4m);
        BacktestBatchRow deep = Row(drawdown: 30m);
        Func<BacktestBatchRow, decimal> least = new BacktestSearchObjective { Figure = "maxDrawdownPercent", Currency = "USDT", Minimize = true }.Fitness();

        Assert.True(least(shallow) > least(deep), "the shallower drawdown has to be the better candidate");
    }

    private static BacktestBatchRow Row(decimal drawdown) => new(0, "r", "i", "p", "x=1", 3, 10m, 5m, drawdown, 1.0, 1.5m, string.Empty);

    // ----- what it does -----

    [Fact]
    public async Task Over_a_space_small_enough_to_cross_the_search_finds_what_the_grid_would_have()
    {
        // The claim underneath the whole feature. Eight trade sizes is a space a grid can do, so the grid's answer is
        // known: the search has to arrive at the same point, having been told nothing about it.
        await WriteCatalogAsync();

        BacktestBatch crossed = await Node().RunBatchAsync(new BacktestBatchConfig
        {
            Run = Run("grid"),
            Sweeps = [new BacktestSweep { Path = "tradeSize", Values = [.. TradeSize().Values()] }],
        });
        BacktestBatchRow bestOfTheGrid = crossed.Best(Currencies.USDT, Fitness)[0];

        BacktestSearch searched = await Node().RunSearchAsync(Search(), Currencies.USDT, Fitness);

        Assert.NotNull(searched.Best);
        Assert.Equal(bestOfTheGrid.Parameters, searched.Best!.PointText);
        Assert.Equal(Fitness(bestOfTheGrid), searched.Best.Fitness);
    }

    [Fact]
    public async Task A_search_is_generations_of_candidates_and_the_best_it_ever_saw()
    {
        await WriteCatalogAsync();

        BacktestSearch searched = await Node().RunSearchAsync(Search(), Currencies.USDT, Fitness);

        Assert.Equal(4, searched.Generations.Count);
        Assert.All(searched.Generations, g => Assert.Equal(6, g.Candidates.Count));
        Assert.Equal([0, 1, 2, 3], searched.Generations.Select(g => g.Index));

        // The best of the whole search is the best of any generation, not the best of the last one: a search that
        // reported its last generation would throw away what it found on the way there.
        decimal best = searched.Candidates.Where(c => c.Fitness is not null).Max(c => c.Fitness!.Value);
        Assert.Equal(best, searched.Best!.Fitness);

        // Every candidate carries the run it became, so any line of the search can be looked into.
        Assert.All(searched.Candidates, c => Assert.False(string.IsNullOrEmpty(c.Run.RunId)));

        // And the summary a person reads says what it cost and where it got to.
        Assert.Contains("4 generation(s)", searched.Summary(), StringComparison.Ordinal);
        Assert.Contains(searched.Best.PointText, searched.Summary(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_best_reported_is_the_best_ever_seen_not_the_best_of_the_last_generation()
    {
        // Elites carry the best forward, so normally it is in the last generation as well and the two readings agree.
        // The difference only shows when nothing is carried: with no elites and everything mutating, a generation can
        // be worse than the one before it, and a search that reported its last generation would throw away what it
        // had already found. This configuration is chosen for exactly that - its best candidate is not in its last
        // generation.
        await WriteCatalogAsync();
        BacktestSearchConfig search = Search("backwards") with
        {
            Population = 2,
            Generations = 4,
            Elites = 0,
            MutationRate = 1.0,
            Seed = 1,
        };

        BacktestSearch searched = await Node().RunSearchAsync(search, Currencies.USDT, Fitness);

        Assert.True(
            searched.Generations[^1].Best!.Fitness < searched.Best!.Fitness,
            "this search is meant to end worse than it got: without that, the assertion below proves nothing");
        Assert.Equal(searched.Candidates.Where(c => c.Fitness is not null).Max(c => c.Fitness!.Value), searched.Best!.Fitness);
    }

    [Fact]
    public async Task A_point_the_search_has_already_run_is_not_run_again()
    {
        // Elites are carried over by definition, so without this a search pays for the same answer every generation.
        await WriteCatalogAsync();

        BacktestSearch searched = await Node().RunSearchAsync(Search(), Currencies.USDT, Fitness);

        Assert.True(searched.Ran < searched.Candidates.Count, "nothing was reused, so every elite was run again");
        Assert.Contains(searched.Candidates, c => c.Reused);

        // A reused candidate is the earlier run, not a new one: same run id, same figure.
        foreach (BacktestSearchCandidate reused in searched.Candidates.Where(c => c.Reused))
        {
            BacktestSearchCandidate first = searched.Candidates.First(c => c.PointText == reused.PointText);
            Assert.Equal(first.Run.RunId, reused.Run.RunId);
            Assert.Equal(first.Fitness, reused.Fitness);
        }
    }

    [Fact]
    public async Task The_same_search_twice_gives_the_same_answer()
    {
        await WriteCatalogAsync();

        BacktestSearch once = await Node().RunSearchAsync(Search(), Currencies.USDT, Fitness);
        BacktestSearch again = await Node().RunSearchAsync(Search(), Currencies.USDT, Fitness);

        Assert.Equal(
            once.Candidates.Select(c => $"{c.Generation}:{c.PointText}:{c.Fitness}"),
            again.Candidates.Select(c => $"{c.Generation}:{c.PointText}:{c.Fitness}"));
    }

    [Fact]
    public async Task A_search_that_stops_improving_stops()
    {
        // What a search is for is spending fewer runs than a grid. A space of one value cannot improve after its
        // first generation, so the patience is what decides when to stop paying.
        await WriteCatalogAsync();
        BacktestSearchConfig search = Search("patient") with
        {
            Space = [new BacktestSearchParameter { Path = "tradeSize", Min = 2m, Max = 2m, Step = 1m }],
            Generations = 6,
            StopAfterGenerationsWithoutImprovement = 2,
        };

        BacktestSearch searched = await Node().RunSearchAsync(search, Currencies.USDT, Fitness);

        Assert.True(searched.StoppedEarly, "nothing could improve, so it should not have run all six generations");
        Assert.True(searched.Generations.Count < 6, $"it ran {searched.Generations.Count} generations");

        // And the one point it had was run once, however many generations proposed it.
        Assert.Equal(1, searched.Ran);
    }

    [Fact]
    public async Task A_search_leaves_a_table_for_every_generation_and_one_for_the_whole_search()
    {
        // The defect this fixes, as it was reported: every generation was a batch writing batch_USDT.csv into the
        // same directory, so a three-generation search left the last generation's rows and nothing else. The winner
        // was right on screen and unreadable afterwards.
        await WriteCatalogAsync();
        string output = Path.Combine(_root, "search-reports");
        BacktestSearchConfig search = Search("reported") with { Run = Run("reported") with { OutputDirectory = output }, Generations = 3 };

        BacktestSearch searched = await Node().RunSearchAsync(search, Currencies.USDT, Fitness);

        // One table for every generation that ran something, none of them overwritten. A generation whose every
        // point had been run already runs nothing and so tabulates nothing - those candidates are in the search's
        // own table below, which is the file that has to cover all of them.
        Assert.Contains(searched.Generations, g => g.Ran > 0);
        foreach (BacktestSearchGeneration generation in searched.Generations)
        {
            string table = Path.Combine(output, $"generation-{generation.Index:00}_USDT.csv");
            if (generation.Ran == 0)
            {
                Assert.False(File.Exists(table), $"generation {generation.Index} ran nothing, so it has nothing to tabulate");
                continue;
            }

            Assert.True(File.Exists(table), $"generation {generation.Index} left no table of its own");
            Assert.Equal(generation.Ran + 1, (await File.ReadAllLinesAsync(table)).Length);
        }

        // And one table covering the search: a row a candidate, reused ones included, in the order proposed.
        string[] lines = await File.ReadAllLinesAsync(Path.Combine(output, "search_USDT.csv"));
        Assert.Equal(searched.Candidates.Count + 1, lines.Length);
        Assert.StartsWith("Generation,Candidate,Reused,Fitness,Point,RunId,", lines[0], StringComparison.Ordinal);
        Assert.Equal(
            searched.Candidates.Select(c => $"{c.Generation},{c.Index},{(c.Reused ? "yes" : "no")}"),
            lines.Skip(1).Select(l => string.Join(',', l.Split(',').Take(3))));

        // The best candidate is in it, and the summary a person reads is beside it.
        Assert.Contains(searched.Best!.PointText.Replace("; ", "; ", StringComparison.Ordinal), string.Join("|", lines), StringComparison.Ordinal);
        Assert.Contains("generation(s)", await File.ReadAllTextAsync(Path.Combine(output, "search.txt")), StringComparison.Ordinal);

        // Each candidate's own report is still where it was.
        Assert.All(searched.Candidates, c => Assert.True(File.Exists(Path.Combine(output, c.Run.RunId, "summary.txt")), $"{c.Run.RunId} wrote no report"));
    }

    [Fact]
    public async Task A_search_can_be_judged_by_the_figure_its_configuration_names()
    {
        // The caller that cannot pass a function: a configuration file says which figure and which way.
        await WriteCatalogAsync();
        BacktestSearchConfig search = Search("objective") with
        {
            Objective = new BacktestSearchObjective { Figure = "returnPercent", Currency = "USDT" },
        };

        BacktestSearch searched = await Node().RunSearchAsync(search);

        Assert.NotNull(searched.Best);
        Assert.Equal(
            searched.Candidates.Where(c => c.Fitness is not null).Max(c => c.Fitness!.Value),
            searched.Best!.Fitness);
    }

    [Fact]
    public async Task A_search_with_no_objective_and_no_function_is_refused()
    {
        await WriteCatalogAsync();

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => Node().RunSearchAsync(Search()));

        Assert.Contains("nothing to judge a candidate by", error.Message, StringComparison.Ordinal);
    }
}
