using System.Globalization;

namespace Bytex.Backtest;

/// <summary>
/// One parameter a search may vary: where it lives in a strategy's payload, and what it is allowed to be. Either a
/// few <see cref="Choices"/> to pick between, or a numeric range walked in <see cref="Step"/>s - not both, because a
/// parameter that is both a list and a range is two parameters wearing one path.
/// </summary>
public sealed record BacktestSearchParameter
{
    /// <summary>Where the value goes, dotted, as a sweep's path is.</summary>
    public required string Path { get; init; }

    /// <summary>Which strategy of the run it belongs to, when the run has more than one.</summary>
    public int Strategy { get; init; }

    /// <summary>The values it may take, when it is a choice between a few.</summary>
    public IReadOnlyList<string> Choices { get; init; } = [];

    /// <summary>The lowest value it may take, when it is a number.</summary>
    public decimal? Min { get; init; }

    /// <summary>The highest value it may take; the range never goes past it, even when a step would overshoot.</summary>
    public decimal? Max { get; init; }

    /// <summary>What the range moves in. A search only ever proposes values on this step, so what it finds can be re-run.</summary>
    public decimal Step { get; init; } = 1m;

    /// <summary>Whether this parameter is a numeric range rather than a list of choices.</summary>
    public bool IsRange => Min is not null && Max is not null;

    /// <summary>Every value this parameter may take, in order. A search draws from these and nothing else.</summary>
    public IReadOnlyList<string> Values()
    {
        if (Choices.Count > 0)
        {
            return Choices;
        }

        List<string> values = new();
        for (decimal value = Min!.Value; value <= Max!.Value; value += Step)
        {
            values.Add(value.ToString(CultureInfo.InvariantCulture));
        }

        return values;
    }
}

/// <summary>
/// Which figure of a run a search is looking for, for a caller that describes its search rather than writing code -
/// a configuration file cannot carry a function. A caller that can write code passes the function instead, because
/// which figure makes one run better than another is the caller's judgement and not the engine's.
/// </summary>
public sealed record BacktestSearchObjective
{
    /// <summary>The column of the comparable table to judge by, named as the table names it.</summary>
    public required string Figure { get; init; }

    /// <summary>The currency whose table to read; a run reports one per currency it touched.</summary>
    public required string Currency { get; init; }

    /// <summary>Whether less is better, as it is for a drawdown.</summary>
    public bool Minimize { get; init; }

    /// <summary>The figures a search may be pointed at, and how to read each one off a row.</summary>
    public static IReadOnlyDictionary<string, Func<BacktestBatchRow, decimal>> Figures { get; } =
        new Dictionary<string, Func<BacktestBatchRow, decimal>>(StringComparer.OrdinalIgnoreCase)
        {
            ["totalPnl"] = row => row.TotalPnl,
            ["returnPercent"] = row => row.ReturnPercent,
            ["maxDrawdownPercent"] = row => row.MaxDrawdownPercent,
            ["sharpeRatio"] = row => (decimal)row.SharpeRatio,
            ["profitFactor"] = row => row.ProfitFactor ?? decimal.MaxValue,
            ["trades"] = row => row.Trades,
        };

    /// <summary>This objective as the function the search actually uses; higher is always better by the time it is used.</summary>
    public Func<BacktestBatchRow, decimal> Fitness()
    {
        if (!Figures.TryGetValue(Figure, out Func<BacktestBatchRow, decimal>? figure))
        {
            throw new InvalidOperationException(
                $"'{Figure}' is not a figure a run reports. The ones there are: {string.Join(", ", Figures.Keys.Order(StringComparer.Ordinal))}.");
        }

        return Minimize ? row => -figure(row) : figure;
    }
}

/// <summary>
/// A guided search over a parameter space: generations of chosen points, each generation the one before it judged and
/// bred. It is the same running as a batch - every candidate is a run, and a run is a run - and only the choosing is
/// new. What it is for is a space too large to cross: a grid of six parameters at ten values each is a million runs,
/// and a search finds a good corner of it in a few hundred.
/// </summary>
public sealed record BacktestSearchConfig
{
    /// <summary>The run every candidate is a variation of.</summary>
    public required BacktestRunConfig Run { get; init; }

    /// <summary>
    /// What may vary, and how far. A search varies parameters and nothing else: the instrument and the period are
    /// whatever the run says, because a candidate judged on one instrument and a candidate judged on another are not
    /// comparable, and how to weigh them together is not the engine's judgement to make. A search per instrument is
    /// a search per instrument.
    /// </summary>
    public required IReadOnlyList<BacktestSearchParameter> Space { get; init; }

    /// <summary>How many candidates a generation holds.</summary>
    public int Population { get; init; } = 24;

    /// <summary>How many generations to breed, the first one included.</summary>
    public int Generations { get; init; } = 5;

    /// <summary>
    /// How many of the best go into the next generation unchanged. Without this a generation can be worse than the
    /// one before it, and a search that can go backwards has not searched.
    /// </summary>
    public int Elites { get; init; } = 2;

    /// <summary>The chance a path is taken from the other parent rather than the first.</summary>
    public double CrossoverRate { get; init; } = 0.5;

    /// <summary>The chance a path of a child is redrawn from the space instead of inherited.</summary>
    public double MutationRate { get; init; } = 0.2;

    /// <summary>How many candidates are drawn to compete for each parent place; two is a coin, higher is greedier.</summary>
    public int TournamentSize { get; init; } = 3;

    /// <summary>
    /// What the drawing starts from. The same seed over the same space and the same data proposes the same candidates
    /// in the same order, so a search is as repeatable as the runs it is made of.
    /// </summary>
    public int Seed { get; init; } = 7;

    /// <summary>How many runs of a generation may be under way at once.</summary>
    public int MaxParallel { get; init; } = 1;

    /// <summary>Whether the first run that throws ends the search.</summary>
    public bool StopOnFailure { get; init; }

    /// <summary>
    /// Stop when this many generations in a row have not beaten the best so far. Left null a search runs every
    /// generation it was asked for.
    /// </summary>
    public int? StopAfterGenerationsWithoutImprovement { get; init; }

    /// <summary>Which figure to judge by, for a caller that cannot pass a function.</summary>
    public BacktestSearchObjective? Objective { get; init; }
}

/// <summary>One candidate: the point, the run it became, and what the caller made of it.</summary>
public sealed record BacktestSearchCandidate
{
    public required int Generation { get; init; }

    /// <summary>Where in its generation it was proposed.</summary>
    public required int Index { get; init; }

    /// <summary>The point itself, ready to be run again on its own.</summary>
    public required BacktestParameterSet Point { get; init; }

    /// <summary>The run it became, failure and all.</summary>
    public required BacktestBatchRun Run { get; init; }

    /// <summary>What the caller judged it to be worth, or null when the run produced nothing to judge.</summary>
    public decimal? Fitness { get; init; }

    /// <summary>
    /// Whether this point had already been run earlier in the search and its result was taken rather than made again.
    /// A run is deterministic, so running it twice would cost time and answer the same.
    /// </summary>
    public bool Reused { get; init; }

    /// <summary>The point as one line: <c>fast=10; slow=30</c>.</summary>
    public string PointText => string.Join("; ", Point.Values.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => $"{v.Key}={v.Value}"));
}

/// <summary>One generation: what was proposed, and how it did.</summary>
public sealed record BacktestSearchGeneration
{
    public required int Index { get; init; }

    public required IReadOnlyList<BacktestSearchCandidate> Candidates { get; init; }

    /// <summary>The best of this generation by the caller's judgement, or null when none of them produced a figure.</summary>
    public BacktestSearchCandidate? Best =>
        Candidates.Where(c => c.Fitness is not null).OrderByDescending(c => c.Fitness!.Value).FirstOrDefault();

    /// <summary>How many of this generation were actually run, the rest having been run earlier in the search.</summary>
    public int Ran => Candidates.Count(c => !c.Reused);
}

/// <summary>What a search did: every generation it bred, in order, and the best it ever saw.</summary>
public sealed class BacktestSearch
{
    internal BacktestSearch(BacktestSearchConfig config, IReadOnlyList<BacktestSearchGeneration> generations, bool stoppedEarly)
    {
        Config = config;
        Generations = generations;
        StoppedEarly = stoppedEarly;
    }

    public BacktestSearchConfig Config { get; }

    public IReadOnlyList<BacktestSearchGeneration> Generations { get; }

    /// <summary>Whether it stopped before its last generation because nothing was improving.</summary>
    public bool StoppedEarly { get; }

    /// <summary>Every candidate of every generation, in the order they were proposed.</summary>
    public IReadOnlyList<BacktestSearchCandidate> Candidates => [.. Generations.SelectMany(g => g.Candidates)];

    /// <summary>How many runs were actually made, which is what the search cost.</summary>
    public int Ran => Generations.Sum(g => g.Ran);

    /// <summary>The best candidate the search ever saw, or null when nothing it ran produced a figure.</summary>
    public BacktestSearchCandidate? Best =>
        Candidates.Where(c => c.Fitness is not null).OrderByDescending(c => c.Fitness!.Value).FirstOrDefault();

    /// <summary>
    /// The search as text: a line a generation saying what it cost and what its best was, so that a reader can see
    /// whether it was still improving when it stopped.
    /// </summary>
    public string Summary()
    {
        List<string> lines =
        [
            $"{Generations.Count} generation(s), {Candidates.Count} candidate(s), {Ran} run(s)" + (StoppedEarly ? ", stopped early" : string.Empty),
            "Gen  Ran  Best",
        ];

        foreach (BacktestSearchGeneration generation in Generations)
        {
            string best = generation.Best is { } candidate
                ? $"{candidate.Fitness!.Value.ToString("0.####", CultureInfo.InvariantCulture)}  {candidate.PointText}"
                : "nothing finished";
            lines.Add($"{generation.Index,3}  {generation.Ran,3}  {best}");
        }

        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// Turns a space and a generation's results into the next generation's points. Separate from the node that runs them
/// so that the choosing can be looked at, and tested, without running a single backtest.
/// </summary>
public static class BacktestSearchPlanner
{
    /// <summary>Checks the search describes something that can be searched, before any run is made.</summary>
    public static void Validate(BacktestSearchConfig search)
    {
        ArgumentNullException.ThrowIfNull(search);
        if (search.Space.Count == 0)
        {
            throw new InvalidOperationException("A search with nothing to vary is a single run; give it a space.");
        }

        if (search.Population < 2)
        {
            throw new InvalidOperationException("A generation of fewer than two candidates has nothing to breed from.");
        }

        if (search.Generations < 1)
        {
            throw new InvalidOperationException("A search runs at least one generation.");
        }

        if (search.Elites < 0 || search.Elites >= search.Population)
        {
            throw new InvalidOperationException("The elites carried over have to leave room for a child; keep fewer than the population.");
        }

        if (search.TournamentSize < 2)
        {
            throw new InvalidOperationException("A tournament of one picks nobody; two candidates compete at the least.");
        }

        if (search.MutationRate is < 0 or > 1 || search.CrossoverRate is < 0 or > 1)
        {
            throw new InvalidOperationException("A rate is a chance between 0 and 1.");
        }

        if (search.StopAfterGenerationsWithoutImprovement is <= 0)
        {
            throw new InvalidOperationException("Stopping after no generations without improvement would stop before it started.");
        }

        if (search.Space.Select(p => p.Strategy).Distinct().Count() > 1)
        {
            // A point is written into one strategy's payload, so a space spanning two of them cannot be run as one
            // candidate. Searching them together needs a point that can span strategies, which is not what one is.
            throw new InvalidOperationException(
                "A search varies the parameters of one strategy: this space names more than one.");
        }

        HashSet<string> paths = new(StringComparer.Ordinal);
        foreach (BacktestSearchParameter parameter in search.Space)
        {
            if (parameter.Choices.Count > 0 && parameter.IsRange)
            {
                throw new InvalidOperationException($"'{parameter.Path}' is given both choices and a range, and a search cannot draw from both.");
            }

            if (parameter.Choices.Count == 0 && !parameter.IsRange)
            {
                throw new InvalidOperationException($"'{parameter.Path}' has nothing to draw from: give it choices, or a min and a max.");
            }

            if (parameter.IsRange)
            {
                if (parameter.Step <= 0)
                {
                    throw new InvalidOperationException($"The step of '{parameter.Path}' has to be more than nothing, or the range never ends.");
                }

                if (parameter.Max < parameter.Min)
                {
                    throw new InvalidOperationException($"The range of '{parameter.Path}' ends before it begins.");
                }
            }

            if (!paths.Add($"{parameter.Strategy}:{parameter.Path}"))
            {
                throw new InvalidOperationException($"'{parameter.Path}' is in the space twice, and a point cannot set it to two values.");
            }
        }
    }

    /// <summary>The first generation: drawn from the space, so that nothing about where a search starts is inherited from a caller's guess.</summary>
    public static IReadOnlyList<BacktestParameterSet> FirstGeneration(BacktestSearchConfig search, Random random)
    {
        Validate(search);
        ArgumentNullException.ThrowIfNull(random);
        List<BacktestParameterSet> points = new(search.Population);
        for (int i = 0; i < search.Population; i++)
        {
            points.Add(Draw(search, random));
        }

        return points;
    }

    /// <summary>
    /// The next generation: the elites unchanged, then children of parents drawn by tournament, crossed over path by
    /// path and mutated. A candidate whose run produced no figure cannot be a parent - it is not evidence of anything.
    /// </summary>
    public static IReadOnlyList<BacktestParameterSet> NextGeneration(
        BacktestSearchConfig search,
        IReadOnlyList<BacktestSearchCandidate> judged,
        Random random)
    {
        Validate(search);
        ArgumentNullException.ThrowIfNull(judged);
        ArgumentNullException.ThrowIfNull(random);

        List<BacktestSearchCandidate> usable = [.. judged.Where(c => c.Fitness is not null).OrderByDescending(c => c.Fitness!.Value)];
        if (usable.Count == 0)
        {
            // Nothing finished, so there is nothing to breed from and no reason to think the same points would fare
            // better. Draw a fresh generation rather than repeat one that told us nothing.
            List<BacktestParameterSet> fresh = new(search.Population);
            for (int i = 0; i < search.Population; i++)
            {
                fresh.Add(Draw(search, random));
            }

            return fresh;
        }

        List<BacktestParameterSet> next = new(search.Population);
        foreach (BacktestSearchCandidate elite in usable.Take(Math.Min(search.Elites, usable.Count)))
        {
            next.Add(elite.Point);
        }

        while (next.Count < search.Population)
        {
            BacktestSearchCandidate first = Tournament(usable, search.TournamentSize, random);
            BacktestSearchCandidate second = Tournament(usable, search.TournamentSize, random);
            next.Add(Breed(search, first.Point, second.Point, random));
        }

        return next;
    }

    /// <summary>One point drawn from the space at random.</summary>
    private static BacktestParameterSet Draw(BacktestSearchConfig search, Random random)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (BacktestSearchParameter parameter in search.Space)
        {
            values[parameter.Path] = Pick(parameter, random);
        }

        return new BacktestParameterSet { Values = values, Strategy = search.Space[0].Strategy };
    }

    /// <summary>A value this parameter is allowed to take, never one it is not.</summary>
    private static string Pick(BacktestSearchParameter parameter, Random random)
    {
        IReadOnlyList<string> values = parameter.Values();
        return values.Count == 0
            ? throw new InvalidOperationException($"'{parameter.Path}' allows no value at all.")
            : values[random.Next(values.Count)];
    }

    /// <summary>The best of a few drawn at random: the pressure to improve, without letting one good candidate take over.</summary>
    private static BacktestSearchCandidate Tournament(IReadOnlyList<BacktestSearchCandidate> usable, int size, Random random)
    {
        BacktestSearchCandidate best = usable[random.Next(usable.Count)];
        for (int i = 1; i < size; i++)
        {
            BacktestSearchCandidate challenger = usable[random.Next(usable.Count)];
            if (challenger.Fitness!.Value > best.Fitness!.Value)
            {
                best = challenger;
            }
        }

        return best;
    }

    /// <summary>A child: each path from one parent or the other, and now and then redrawn from the space entirely.</summary>
    private static BacktestParameterSet Breed(BacktestSearchConfig search, BacktestParameterSet first, BacktestParameterSet second, Random random)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (BacktestSearchParameter parameter in search.Space)
        {
            if (random.NextDouble() < search.MutationRate)
            {
                values[parameter.Path] = Pick(parameter, random);
                continue;
            }

            BacktestParameterSet parent = random.NextDouble() < search.CrossoverRate ? second : first;
            values[parameter.Path] = parent.Values.TryGetValue(parameter.Path, out string? inherited)
                ? inherited
                : Pick(parameter, random);
        }

        return new BacktestParameterSet { Values = values, Strategy = search.Space[0].Strategy };
    }

    /// <summary>What makes two points the same point, for a search that must not pay twice for one answer.</summary>
    internal static string Key(BacktestParameterSet point) =>
        $"{point.Strategy}|" + string.Join("|", point.Values.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => $"{v.Key}={v.Value}"));
}
