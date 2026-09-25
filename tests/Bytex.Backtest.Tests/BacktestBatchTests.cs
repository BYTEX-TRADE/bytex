using System.Text.Json;
using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Plugins;
using Bytex.Data;

namespace Bytex.Backtest.Tests;

// Why: R8.2, R8.20 and R8.27. One strategy over a venue's instruments, over a set of periods, over a grid of
// parameters or over particular points chosen elsewhere: the
// question is always the same - which of these is better - and it can only be answered if the runs are comparable and
// if what could not be run is visible. So what is pinned here is the expansion (one run per combination, in a settled
// order, each with an id of its own), the arithmetic that must not drift (the runs are independent, so the same run
// gives the same answer whether it ran alone or beside nineteen others), and that a run which failed is a line in the
// table with its reason rather than a hole in it.
public sealed class BacktestBatchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bytex-batch-tests-" + Guid.NewGuid().ToString("N"));
    private readonly CurrencyPair _spot = TestInstruments.Spot();
    private readonly CurrencyPair _eth = TestInstruments.EthSpot(TestInstruments.Sim);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string CatalogPath => Path.Combine(_root, "catalog");

    /// <summary>Two instruments in the catalog: the golden bars for one, and the same bars for the other.</summary>
    private async Task WriteCatalogAsync(bool withEth = true)
    {
        ParquetDataCatalog catalog = new(CatalogPath);
        await catalog.WriteInstrumentsAsync(withEth ? [_spot, _eth] : [_spot]);
        await catalog.WriteBarsAsync(GoldenEmaCrossBacktestTests.Bars(_spot));
        if (withEth)
        {
            await catalog.WriteBarsAsync(GoldenEmaCrossBacktestTests.Bars(_eth));
        }
    }

    private static BacktestNode Node()
    {
        PluginRegistry registry = new();
        registry.AddStrategyProvider(new EmaCrossProvider());
        return new BacktestNode(registry);
    }

    private BacktestRunConfig Run(string runId = "batch", decimal tradeSize = 2m, string? output = null) => new()
    {
        Engine = new BacktestEngineConfig { RunId = runId },
        Venues = [new BacktestVenueConfig { Venue = "SIM", StartingBalances = ["10000 USDT"] }],
        Data = [new BacktestDataConfig { CatalogPath = CatalogPath, DataKind = "bars", BarType = Scripted.MinuteBars(_spot) }],
        Strategies = [EmaCrossProvider.Definition(_spot.Id, Scripted.MinuteBars(_spot), tradeSize)],
        OutputDirectory = output,
    };

    private BacktestBatchConfig OverInstruments(params InstrumentId[] instruments) => new()
    {
        Run = Run(),
        Instruments = instruments,
        InstrumentPaths = ["instrumentId"],
        BarTypePaths = ["barType"],
    };

    [Fact]
    public void A_batch_that_varies_nothing_is_the_one_run_it_was_built_from()
    {
        IReadOnlyList<(BacktestRunConfig Run, BacktestBatchRun Description)> planned =
            BacktestBatchPlanner.Expand(new BacktestBatchConfig { Run = Run("solo") });

        (BacktestRunConfig run, BacktestBatchRun description) = Assert.Single(planned);
        Assert.Equal("solo-000", run.Engine.RunId);
        Assert.Equal(0, description.Index);
        Assert.Null(description.Instrument);
        Assert.Null(description.Period);
        Assert.Empty(description.Parameters);
    }

    [Fact]
    public void Every_combination_of_instrument_period_and_parameter_is_a_run_of_its_own()
    {
        BacktestBatchConfig batch = new()
        {
            Run = Run("grid"),
            Instruments = [_spot.Id, _eth.Id],
            InstrumentPaths = ["instrumentId"],
            BarTypePaths = ["barType"],
            Periods = [new BacktestPeriod(null, Scripted.Ms(5000), "first"), new BacktestPeriod(Scripted.Ms(5000), null, "second")],
            Sweeps = [new BacktestSweep { Path = "tradeSize", Values = ["1", "2", "3"] }],
        };

        IReadOnlyList<(BacktestRunConfig Run, BacktestBatchRun Description)> planned = BacktestBatchPlanner.Expand(batch);

        Assert.Equal(12, planned.Count);
        Assert.Equal(12, planned.Select(p => p.Run.Engine.RunId).Distinct().Count());

        // Instrument first, then period, then the sweep: a settled order, so the same batch expands the same way and
        // run 7 of yesterday's report is run 7 of today's.
        Assert.Equal(
            ["BTCUSDT.SIM|first|1", "BTCUSDT.SIM|first|2", "BTCUSDT.SIM|first|3", "BTCUSDT.SIM|second|1"],
            planned.Take(4).Select(p => $"{p.Description.Instrument}|{p.Description.Period!.Name}|{p.Description.Parameters["tradeSize"]}"));
        Assert.Equal("ETHUSDT.SIM|second|3", $"{planned[^1].Description.Instrument}|{planned[^1].Description.Period!.Name}|{planned[^1].Description.Parameters["tradeSize"]}");

        // And the period is the run's own start and end, not just a name on the line: a table that said "first" over
        // the whole history would be a report of something nobody asked for.
        Assert.Equal(Scripted.Ms(5000), planned[0].Run.End);
        Assert.Null(planned[0].Run.Start);
        Assert.Equal(Scripted.Ms(5000), planned[^1].Run.Start);
        Assert.Null(planned[^1].Run.End);
    }

    [Fact]
    public void An_instrument_reaches_the_data_and_the_strategy_that_carries_it()
    {
        IReadOnlyList<(BacktestRunConfig Run, BacktestBatchRun Description)> planned = BacktestBatchPlanner.Expand(OverInstruments(_eth.Id));

        BacktestRunConfig run = Assert.Single(planned).Run;

        Assert.Equal(_eth.Id, Assert.Single(run.Data).BarType!.Value.InstrumentId);

        JsonElement payload = Assert.Single(run.Strategies).Payload;
        Assert.Equal(_eth.Id.Value, payload.GetProperty("instrumentId").GetString());

        // A bar type carries a step and a price as well as an instrument, so only its instrument changed.
        BarType barType = BarType.Parse(payload.GetProperty("barType").GetString()!);
        Assert.Equal(_eth.Id, barType.InstrumentId);
        Assert.Equal(Scripted.MinuteBars(_spot).Spec, barType.Spec);
    }

    [Fact]
    public void A_swept_value_reaches_the_strategy_as_the_value_it_reads_as()
    {
        BacktestBatchConfig batch = new()
        {
            Run = Run(),
            Sweeps =
            [
                new BacktestSweep { Path = "tradeSize", Values = ["1.5"] },
                new BacktestSweep { Path = "strategyId", Values = ["Ema-Swept"] },
            ],
        };

        JsonElement payload = Assert.Single(BacktestBatchPlanner.Expand(batch)).Run.Strategies[0].Payload;

        Assert.Equal(1.5m, payload.GetProperty("tradeSize").GetDecimal());
        Assert.Equal(JsonValueKind.Number, payload.GetProperty("tradeSize").ValueKind);
        Assert.Equal("Ema-Swept", payload.GetProperty("strategyId").GetString());
    }

    [Fact]
    public void A_sweep_reaches_a_value_the_payload_did_not_have_yet()
    {
        // What a document strategy needs: parameterOverrides is not in the payload until something sweeps it.
        JsonElement payload = BacktestBatchPlanner.SetValue(
            JsonSerializer.SerializeToElement(new { document = new { id = "x" } }), "parameterOverrides.fast", "10");

        Assert.Equal(10m, payload.GetProperty("parameterOverrides").GetProperty("fast").GetDecimal());
        Assert.Equal("x", payload.GetProperty("document").GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("nothing.here.0.deep", "is not in this strategy's payload")]
    [InlineData("tradeSize.deeper", "has nowhere to go")]
    public void A_path_that_cannot_be_followed_is_refused_before_anything_runs(string path, string because)
    {
        BacktestBatchConfig batch = new() { Run = Run(), Sweeps = [new BacktestSweep { Path = path, Values = ["1"] }] };

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => BacktestBatchPlanner.Expand(batch));

        Assert.Contains(path, error.Message, StringComparison.Ordinal);
        Assert.Contains(because, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sweep_with_no_values_and_a_strategy_that_is_not_there_are_both_refused()
    {
        Assert.Contains("no values to try", Assert.Throws<InvalidOperationException>(() =>
            BacktestBatchPlanner.Expand(new BacktestBatchConfig { Run = Run(), Sweeps = [new BacktestSweep { Path = "tradeSize", Values = [] }] })).Message, StringComparison.Ordinal);

        Assert.Contains("the run has 1", Assert.Throws<InvalidOperationException>(() =>
            BacktestBatchPlanner.Expand(new BacktestBatchConfig { Run = Run(), Sweeps = [new BacktestSweep { Path = "tradeSize", Values = ["1"], Strategy = 3 }] })).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sweep_runs_the_grid_and_the_runs_are_comparable()
    {
        await WriteCatalogAsync();
        BacktestBatchConfig batch = new()
        {
            Run = Run("sweep"),
            Sweeps = [new BacktestSweep { Path = "tradeSize", Values = ["1", "2", "4"] }],
        };

        BacktestBatch done = await Node().RunBatchAsync(batch);

        Assert.Equal(3, done.Runs.Count);
        Assert.Equal(3, done.Completed);
        Assert.Equal(0, done.Failed);

        IReadOnlyList<BacktestBatchRow> rows = done.Rows(Currencies.USDT);
        Assert.Equal(["tradeSize=1", "tradeSize=2", "tradeSize=4"], rows.Select(r => r.Parameters));
        Assert.Equal([0, 1, 2], rows.Select(r => r.Index));
        Assert.Equal(["sweep-000", "sweep-001", "sweep-002"], rows.Select(r => r.RunId));
        Assert.All(rows, r => Assert.Empty(r.Failure));

        // The bigger the size the bigger the result, in the same direction: what the sweep is for is the comparison.
        Assert.Equal(rows.OrderBy(r => Math.Abs(r.TotalPnl)).Select(r => r.Parameters), rows.Select(r => r.Parameters));
        Assert.Equal(rows[0].TotalPnl * 4, rows[2].TotalPnl);

        // And the best of them is whatever the caller is looking for, not whatever the engine thinks.
        Assert.Equal(rows.OrderByDescending(r => r.ReturnPercent).First(), done.Best(Currencies.USDT, r => r.ReturnPercent)[0]);
    }

    [Fact]
    public async Task One_strategy_over_two_instruments_is_two_runs_side_by_side()
    {
        await WriteCatalogAsync();

        BacktestBatch done = await Node().RunBatchAsync(OverInstruments(_spot.Id, _eth.Id));

        Assert.Equal(2, done.Completed);
        Assert.Equal([_spot.Id.Value, _eth.Id.Value], done.Rows(Currencies.USDT).Select(r => r.Instrument));
        Assert.All(done.Runs, r => Assert.True(r.Result!.TotalOrders > 0, $"{r.RunId} traded nothing"));
    }

    [Fact]
    public async Task A_run_that_cannot_be_made_is_a_line_with_its_reason_rather_than_a_hole()
    {
        // The catalog has no ETH, which is what a scan over a venue's instruments meets: the run for it cannot be
        // built, and a scan that dropped it quietly would report the best of an unknown number of instruments.
        await WriteCatalogAsync(withEth: false);
        string output = Path.Combine(_root, "scan");
        BacktestBatchConfig batch = OverInstruments(_spot.Id, _eth.Id);

        BacktestBatch done = await Node().RunBatchAsync(batch with { Run = batch.Run with { OutputDirectory = output } });

        Assert.Equal(2, done.Runs.Count);
        Assert.Equal(1, done.Completed);
        Assert.Equal(1, done.Failed);

        BacktestBatchRow failed = Assert.Single(done.Rows(Currencies.USDT), r => r.Failure.Length > 0);
        Assert.Equal(_eth.Id.Value, failed.Instrument);
        Assert.Contains(_eth.Id.Value, failed.Failure, StringComparison.Ordinal);
        Assert.DoesNotContain(done.Best(Currencies.USDT, r => r.ReturnPercent), r => r.Failure.Length > 0);

        // And the file says so as well: what could not be run has to be as visible to whoever reads the table as it
        // is to whoever called the method.
        string csv = await File.ReadAllTextAsync(Path.Combine(output, "batch_USDT.csv"));
        Assert.Contains(_eth.Id.Value, csv, StringComparison.Ordinal);
        Assert.Equal(3, csv.ReplaceLineEndings("|").Trim('|').Split('|').Length);
    }

    [Fact]
    public async Task A_batch_told_to_stop_on_a_failure_stops()
    {
        await WriteCatalogAsync(withEth: false);
        BacktestBatchConfig batch = OverInstruments(_eth.Id, _spot.Id) with { StopOnFailure = true };

        await Assert.ThrowsAsync<InvalidOperationException>(() => Node().RunBatchAsync(batch));
    }

    [Fact]
    public async Task Runs_beside_each_other_give_what_they_give_alone()
    {
        // The reason a batch may run several at once is that the runs are independent; the reason it is worth proving
        // is that they share a plugin registry and a catalog on disk.
        await WriteCatalogAsync();
        BacktestBatchConfig batch = new()
        {
            Run = Run("parallel"),
            Instruments = [_spot.Id, _eth.Id],
            InstrumentPaths = ["instrumentId"],
            BarTypePaths = ["barType"],
            Sweeps = [new BacktestSweep { Path = "tradeSize", Values = ["1", "2", "3"] }],
        };

        BacktestBatch alone = await Node().RunBatchAsync(batch);
        BacktestBatch together = await Node().RunBatchAsync(batch with { MaxParallel = 4 });

        Assert.Equal(6, alone.Completed);
        Assert.Equal(6, together.Completed);
        Assert.Equal([0, 1, 2, 3, 4, 5], together.Runs.Select(r => r.Index));
        Assert.Equal(Fingerprint(alone), Fingerprint(together));
    }

    [Fact]
    public async Task The_batch_is_written_as_one_table_beside_the_runs_own_reports()
    {
        await WriteCatalogAsync();
        string output = Path.Combine(_root, "reports");
        BacktestBatchConfig batch = new()
        {
            Run = Run("written", output: output),
            Sweeps = [new BacktestSweep { Path = "tradeSize", Values = ["1", "2"] }],
        };

        BacktestBatch done = await Node().RunBatchAsync(batch);

        string csv = await File.ReadAllTextAsync(Path.Combine(output, "batch_USDT.csv"));
        Assert.StartsWith("Index,RunId,Instrument,Period,Parameters,Trades,TotalPnl,ReturnPercent,MaxDrawdownPercent,SharpeRatio,ProfitFactor,Failure", csv, StringComparison.Ordinal);
        Assert.Contains("tradeSize=2", csv, StringComparison.Ordinal);
        Assert.Equal(3, csv.Trim().Split('\n').Length); // the header and a line a run

        // Each run's own reports are still written under its own id, so a line in the table can be looked into.
        Assert.All(done.Runs, r => Assert.True(File.Exists(Path.Combine(output, r.RunId, "summary.txt")), $"{r.RunId} wrote no report"));

        // And the table a person reads says the same thing.
        string summary = done.Summary(Currencies.USDT);
        Assert.Contains("2 run(s), 2 finished, 0 failed (USDT)", summary, StringComparison.Ordinal);
        Assert.Contains("tradeSize=2", summary, StringComparison.Ordinal);
    }


    // ----- points chosen elsewhere (R8.27) -----

    /// <summary>A generation of a search: particular points, not a product of ranges.</summary>
    private static BacktestParameterSet Point(string tradeSize) =>
        new() { Values = new Dictionary<string, string>(StringComparer.Ordinal) { ["tradeSize"] = tradeSize } };

    [Fact]
    public void Each_set_is_one_run_in_the_order_it_was_given()
    {
        // A grid could not express these three: they are not the product of anything.
        BacktestBatchConfig batch = new()
        {
            Run = Run("sets"),
            ParameterSets = [Point("1.5"), Point("7"), Point("2.25")],
        };

        IReadOnlyList<(BacktestRunConfig Run, BacktestBatchRun Description)> planned = BacktestBatchPlanner.Expand(batch);

        Assert.Equal(["1.5", "7", "2.25"], planned.Select(p => p.Description.Parameters["tradeSize"]));
        Assert.Equal(["sets-000", "sets-001", "sets-002"], planned.Select(p => p.Run.Engine.RunId));
        Assert.Equal([1.5m, 7m, 2.25m], planned.Select(p => p.Run.Strategies[0].Payload.GetProperty("tradeSize").GetDecimal()));
    }

    [Fact]
    public void A_set_sets_every_path_it_names_at_once()
    {
        // The difference from a sweep that matters: two paths in one set is one run with both, where two sweeps of one
        // value each would also be one run, but two sweeps of two values would be four.
        BacktestBatchConfig batch = new()
        {
            Run = Run(),
            ParameterSets =
            [
                new BacktestParameterSet
                {
                    Values = new Dictionary<string, string>(StringComparer.Ordinal) { ["tradeSize"] = "3", ["strategyId"] = "Ema-Chosen" },
                },
            ],
        };

        (BacktestRunConfig run, BacktestBatchRun description) = Assert.Single(BacktestBatchPlanner.Expand(batch));

        Assert.Equal(3m, run.Strategies[0].Payload.GetProperty("tradeSize").GetDecimal());
        Assert.Equal("Ema-Chosen", run.Strategies[0].Payload.GetProperty("strategyId").GetString());
        Assert.Equal("strategyId=Ema-Chosen; tradeSize=3", description.ParameterText);
    }

    [Fact]
    public void A_set_of_one_value_and_a_sweep_of_one_value_describe_the_same_run()
    {
        // The two front doors have to lead to the same place, or a search would be reading figures produced by a
        // different mechanism from the grid it is compared against.
        BacktestBatchConfig swept = new()
        {
            Run = Run("same"),
            Sweeps = [new BacktestSweep { Path = "tradeSize", Values = ["3"] }, new BacktestSweep { Path = "strategyId", Values = ["Ema-One"] }],
        };
        BacktestBatchConfig given = new()
        {
            Run = Run("same"),
            ParameterSets =
            [
                new BacktestParameterSet
                {
                    Values = new Dictionary<string, string>(StringComparer.Ordinal) { ["tradeSize"] = "3", ["strategyId"] = "Ema-One" },
                },
            ],
        };

        (BacktestRunConfig fromSweep, BacktestBatchRun sweptDescription) = Assert.Single(BacktestBatchPlanner.Expand(swept));
        (BacktestRunConfig fromSet, BacktestBatchRun givenDescription) = Assert.Single(BacktestBatchPlanner.Expand(given));

        Assert.Equal(fromSweep.Engine.RunId, fromSet.Engine.RunId);
        Assert.Equal(sweptDescription.ParameterText, givenDescription.ParameterText);
        Assert.Equal(
            fromSweep.Strategies[0].Payload.GetRawText(),
            fromSet.Strategies[0].Payload.GetRawText());
    }

    [Fact]
    public void Chosen_points_still_multiply_over_instruments_and_periods()
    {
        BacktestBatchConfig batch = new()
        {
            Run = Run("gen"),
            Instruments = [_spot.Id, _eth.Id],
            InstrumentPaths = ["instrumentId"],
            BarTypePaths = ["barType"],
            Periods = [new BacktestPeriod(null, Scripted.Ms(5000), "first"), new BacktestPeriod(Scripted.Ms(5000), null, "second")],
            ParameterSets = [Point("1"), Point("2"), Point("3")],
        };

        IReadOnlyList<(BacktestRunConfig Run, BacktestBatchRun Description)> planned = BacktestBatchPlanner.Expand(batch);

        // A generation over two instruments and two periods is twelve runs of one batch, in the same settled order a
        // grid expands in: instrument, then period, then the points as given.
        Assert.Equal(12, planned.Count);
        Assert.Equal(
            ["BTCUSDT.SIM|first|1", "BTCUSDT.SIM|first|2", "BTCUSDT.SIM|first|3", "BTCUSDT.SIM|second|1"],
            planned.Take(4).Select(p => $"{p.Description.Instrument}|{p.Description.Period!.Name}|{p.Description.Parameters["tradeSize"]}"));
        Assert.Equal("ETHUSDT.SIM|second|3", $"{planned[^1].Description.Instrument}|{planned[^1].Description.Period!.Name}|{planned[^1].Description.Parameters["tradeSize"]}");
    }

    [Fact]
    public void The_same_point_twice_is_two_runs()
    {
        // A generation may well re-test a survivor beside its children, and a batch that silently ran it once would
        // leave a caller matching twenty-three results to twenty-four candidates.
        IReadOnlyList<(BacktestRunConfig Run, BacktestBatchRun Description)> planned =
            BacktestBatchPlanner.Expand(new BacktestBatchConfig { Run = Run("twice"), ParameterSets = [Point("2"), Point("2")] });

        Assert.Equal(2, planned.Count);
        Assert.Equal(["twice-000", "twice-001"], planned.Select(p => p.Run.Engine.RunId));
    }

    [Fact]
    public void A_batch_cannot_describe_its_points_both_ways_at_once()
    {
        BacktestBatchConfig batch = new()
        {
            Run = Run(),
            Sweeps = [new BacktestSweep { Path = "tradeSize", Values = ["1", "2"] }],
            ParameterSets = [Point("3")],
        };

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => BacktestBatchPlanner.Expand(batch));

        Assert.Contains("either as sweeps to cross or as sets to run as given, not both", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_set_that_sets_nothing_and_a_set_for_a_strategy_that_is_not_there_are_both_refused()
    {
        Assert.Contains("sets no parameter", Assert.Throws<InvalidOperationException>(() =>
            BacktestBatchPlanner.Expand(new BacktestBatchConfig
            {
                Run = Run(),
                ParameterSets = [new BacktestParameterSet { Values = new Dictionary<string, string>(StringComparer.Ordinal) }],
            })).Message, StringComparison.Ordinal);

        Assert.Contains("the run has 1", Assert.Throws<InvalidOperationException>(() =>
            BacktestBatchPlanner.Expand(new BacktestBatchConfig
            {
                Run = Run(),
                ParameterSets = [Point("1") with { Strategy = 3 }],
            })).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("nothing.here.0.deep", "is not in this strategy's payload")]
    [InlineData("tradeSize.deeper", "has nowhere to go")]
    public void A_sets_path_that_cannot_be_followed_is_refused_before_anything_runs(string path, string because)
    {
        // A set is written by a search rather than by a person, so a path it got wrong has to be refused as loudly as
        // a sweep's: twenty-four runs of a payload nobody meant would look exactly like twenty-four honest results.
        BacktestBatchConfig batch = new()
        {
            Run = Run(),
            ParameterSets = [new BacktestParameterSet { Values = new Dictionary<string, string>(StringComparer.Ordinal) { [path] = "1" } }],
        };

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => BacktestBatchPlanner.Expand(batch));

        Assert.Contains(path, error.Message, StringComparison.Ordinal);
        Assert.Contains(because, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_set_writes_its_paths_in_the_same_order_whatever_order_it_was_written_in()
    {
        // A set arrives as a map, and a map has no order a caller can rely on. Two searches that chose the same point
        // must produce the same payload, including the order paths are created in - a payload that differs by key
        // order is a different file, a different hash and a different answer to "have I run this already".
        BacktestParameterSet oneWay = new()
        {
            Values = new Dictionary<string, string>(StringComparer.Ordinal) { ["parameterOverrides.slow"] = "30", ["parameterOverrides.fast"] = "10" },
        };
        BacktestParameterSet theOther = new()
        {
            Values = new Dictionary<string, string>(StringComparer.Ordinal) { ["parameterOverrides.fast"] = "10", ["parameterOverrides.slow"] = "30" },
        };

        string first = Assert.Single(BacktestBatchPlanner.Expand(new BacktestBatchConfig { Run = Run(), ParameterSets = [oneWay] }))
            .Run.Strategies[0].Payload.GetProperty("parameterOverrides").GetRawText();
        string second = Assert.Single(BacktestBatchPlanner.Expand(new BacktestBatchConfig { Run = Run(), ParameterSets = [theOther] }))
            .Run.Strategies[0].Payload.GetProperty("parameterOverrides").GetRawText();

        Assert.Equal(first, second);
        Assert.StartsWith("{\"fast\"", first, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_generation_of_chosen_points_is_one_batch_with_one_table()
    {
        // The whole point of the ask: a generation used to be a batch of one per candidate, which is a report folder
        // per candidate and no table covering the generation.
        await WriteCatalogAsync();
        string output = Path.Combine(_root, "generation");
        BacktestBatchConfig batch = new()
        {
            Run = Run("gen", output: output),
            ParameterSets = [Point("1"), Point("4"), Point("2")],
        };

        BacktestBatch done = await Node().RunBatchAsync(batch);

        Assert.Equal(3, done.Completed);
        Assert.Equal(0, done.Failed);

        IReadOnlyList<BacktestBatchRow> rows = done.Rows(Currencies.USDT);
        Assert.Equal(["tradeSize=1", "tradeSize=4", "tradeSize=2"], rows.Select(r => r.Parameters));
        Assert.All(rows, r => Assert.Empty(r.Failure));

        // One table for the generation, beside each candidate's own report.
        string csv = await File.ReadAllTextAsync(Path.Combine(output, "batch_USDT.csv"));
        Assert.Equal(4, csv.Trim().Split('\n').Length);
        Assert.All(done.Runs, r => Assert.True(File.Exists(Path.Combine(output, r.RunId, "summary.txt")), $"{r.RunId} wrote no report"));

        // And what the search reads back is the figures, ordered by whatever it is selecting on.
        Assert.Equal("tradeSize=4", done.Best(Currencies.USDT, r => Math.Abs(r.TotalPnl))[0].Parameters);

        // Chosen points are run by the same machinery as a grid, so the same point gives the same answer either way.
        BacktestBatch swept = await Node().RunBatchAsync(new BacktestBatchConfig
        {
            Run = Run("gen"),
            Sweeps = [new BacktestSweep { Path = "tradeSize", Values = ["4"] }],
        });
        Assert.Equal(
            swept.Rows(Currencies.USDT)[0].TotalPnl,
            rows.Single(r => r.Parameters == "tradeSize=4").TotalPnl);
    }

    private static string Fingerprint(BacktestBatch batch) => string.Join(
        " | ",
        batch.Runs.Select(r => $"{r.Index} {r.Instrument} {r.ParameterText} {r.Result?.TotalOrders} {r.Result?.Currencies.FirstOrDefault(c => c.Currency.Equals(Currencies.USDT))?.TotalPnl}"));
}
