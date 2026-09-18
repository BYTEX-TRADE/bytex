using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Plugins;
using Bytex.Data;

namespace Bytex.Backtest.Tests;

// Why: the node is the configuration-driven entry point used by the CLI. It must load instruments and data from a
// catalog, build strategies through the plugin registry, and give the same answer as a hand-assembled engine.
// The catalog lives in a temporary directory that every test removes again.
public sealed class BacktestNodeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bytex-node-tests-" + Guid.NewGuid().ToString("N"));
    private readonly CurrencyPair _spot = TestInstruments.Spot();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string CatalogPath => Path.Combine(_root, "catalog");

    private async Task WriteGoldenCatalogAsync()
    {
        ParquetDataCatalog catalog = new(CatalogPath);
        await catalog.WriteInstrumentsAsync([_spot]);
        await catalog.WriteBarsAsync(GoldenEmaCrossBacktestTests.Bars(_spot));
    }

    private static BacktestNode NodeWithEmaProvider()
    {
        PluginRegistry registry = new();
        registry.AddStrategyProvider(new EmaCrossProvider());
        return new BacktestNode(registry);
    }

    private BacktestRunConfig GoldenRun(string runId, decimal tradeSize = 2m, UnixNanos? end = null, string? outputDirectory = null) => new()
    {
        Engine = new BacktestEngineConfig { RunId = runId },
        Venues = [new BacktestVenueConfig { Venue = "SIM", StartingBalances = ["10000 USDT"] }],
        Data = [new BacktestDataConfig { CatalogPath = CatalogPath, DataKind = "bars", BarType = Scripted.MinuteBars(_spot) }],
        Strategies = [EmaCrossProvider.Definition(_spot.Id, Scripted.MinuteBars(_spot), tradeSize)],
        End = end,
        OutputDirectory = outputDirectory,
    };

    [Fact]
    public async Task Run_from_configuration_reproduces_the_hand_derived_golden_backtest()
    {
        await WriteGoldenCatalogAsync();

        BacktestResult result = await NodeWithEmaProvider().RunAsync(GoldenRun("node-golden"));

        Assert.Equal("node-golden", result.RunId);
        Assert.Equal(12, result.Iterations);
        Assert.Equal([99.00m, 102.00m, 104.00m, 100.00m, 102.00m, 102.00m], result.Fills.Select(f => f.LastPx.Value));
        CurrencyStatistics usdt = result.Currencies.Single(c => c.Currency.Equals(Currencies.USDT));
        Assert.Equal(10_000m, usdt.StartingBalance);
        Assert.Equal(9_995.564m, usdt.EndingBalance);
    }

    [Fact]
    public async Task Batch_run_returns_one_result_per_configuration_in_order()
    {
        await WriteGoldenCatalogAsync();

        IReadOnlyList<BacktestResult> results = await NodeWithEmaProvider().RunAsync([GoldenRun("size-2", 2m), GoldenRun("size-4", 4m)]);

        Assert.Equal(["size-2", "size-4"], results.Select(r => r.RunId));
        Assert.Equal(
            [9_995.564m, 9_991.128m], // the loss of 4.436 doubles with the trade size
            results.Select(r => r.Currencies.Single(c => c.Currency.Equals(Currencies.USDT)).EndingBalance));
    }

    [Fact]
    public async Task Run_end_time_limits_the_data_that_is_loaded_from_the_catalog()
    {
        // Stop after bar 7: one completed round trip (+5.196) and nothing else.
        await WriteGoldenCatalogAsync();

        BacktestResult result = await NodeWithEmaProvider().RunAsync(GoldenRun("first-seven", end: Scripted.Ms(7 * 60_000)));

        Assert.Equal(7, result.Iterations);
        Assert.Equal([99.00m, 102.00m], result.Fills.Select(f => f.LastPx.Value));
        Assert.Equal(10_005.196m, result.Currencies.Single(c => c.Currency.Equals(Currencies.USDT)).EndingBalance);
    }

    [Fact]
    public async Task Reports_are_written_into_a_directory_named_after_the_run()
    {
        await WriteGoldenCatalogAsync();
        string output = Path.Combine(_root, "reports");

        await NodeWithEmaProvider().RunAsync(GoldenRun("with-reports", outputDirectory: output));

        string runDirectory = Path.Combine(output, "with-reports");
        Assert.True(File.Exists(Path.Combine(runDirectory, "result.json")));
        Assert.True(File.Exists(Path.Combine(runDirectory, "fills.csv")));
        Assert.True(File.Exists(Path.Combine(runDirectory, "summary.txt")));
    }

    [Fact]
    public async Task Quote_data_from_the_catalog_drives_the_simulated_venue()
    {
        ParquetDataCatalog catalog = new(CatalogPath);
        await catalog.WriteInstrumentsAsync([_spot]);
        await catalog.WriteQuoteTicksAsync([Scripted.Quote(_spot, 1000, 100.00m, 100.10m), Scripted.Quote(_spot, 2000, 101.00m, 101.10m)]);
        BacktestRunConfig run = new()
        {
            Engine = new BacktestEngineConfig { RunId = "quotes" },
            Venues = [new BacktestVenueConfig { Venue = "SIM", StartingBalances = ["10000 USDT"] }],
            Data = [new BacktestDataConfig { CatalogPath = CatalogPath, DataKind = "quotes", InstrumentId = _spot.Id }],
        };

        using BacktestEngine engine = await new BacktestNode().BuildEngineAsync(run);

        Assert.Equal(2, engine.Data.Count);
        Assert.Same(engine.Cache.Instrument(_spot.Id), engine.Exchanges[TestInstruments.Sim].Instruments[_spot.Id]);
        Assert.Equal(_spot.TakerFee, engine.Cache.Instrument(_spot.Id)!.TakerFee);
        Assert.Equal(_spot.PriceIncrement, engine.Cache.Instrument(_spot.Id)!.PriceIncrement);
    }

    [Fact]
    public async Task Instrument_missing_from_every_catalog_is_reported_before_anything_runs()
    {
        ParquetDataCatalog catalog = new(CatalogPath);
        await catalog.WriteBarsAsync(GoldenEmaCrossBacktestTests.Bars(_spot));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => NodeWithEmaProvider().RunAsync(GoldenRun("no-instrument")));

        Assert.Contains("BTCUSDT.SIM", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_data_kind_is_rejected()
    {
        await WriteGoldenCatalogAsync();
        BacktestRunConfig run = GoldenRun("bad-kind") with
        {
            Data = [new BacktestDataConfig { CatalogPath = CatalogPath, DataKind = "candles", InstrumentId = _spot.Id }],
        };

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => NodeWithEmaProvider().RunAsync(run));

        Assert.Contains("candles", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Strategy_from_an_unregistered_provider_is_rejected()
    {
        await WriteGoldenCatalogAsync();

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => new BacktestNode().RunAsync(GoldenRun("no-provider")));

        Assert.Contains(EmaCrossProvider.ProviderId, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Venue_configuration_maps_every_setting_onto_the_simulated_venue()
    {
        BacktestVenueConfig config = new()
        {
            Venue = "SIM",
            OmsType = OmsType.Hedging,
            AccountType = AccountType.Margin,
            BaseCurrency = "USDT",
            StartingBalances = ["2500.5 USDT", "0.75 BTC"],
            DefaultLeverage = 5m,
            BarExecution = BarExecutionMode.CloseOnly,
            ProbFillOnLimit = 0.25m,
            ProbSlippage = 0.75m,
            FillModelSeed = 7,
            Latency = TimeSpan.FromMilliseconds(30),
            RejectStopOrdersAtMarket = false,
        };

        SimulatedVenueConfig venue = config.ToVenueConfig();

        Assert.Equal(TestInstruments.Sim, venue.Venue);
        Assert.Equal(OmsType.Hedging, venue.OmsType);
        Assert.Equal(AccountType.Margin, venue.AccountType);
        Assert.Equal(Currencies.USDT, venue.BaseCurrency);
        Assert.Equal([new Money(2500.5m, Currencies.USDT), new Money(0.75m, Currencies.BTC)], venue.StartingBalances);
        Assert.Equal(5m, venue.DefaultLeverage);
        Assert.Equal(BarExecutionMode.CloseOnly, venue.BarExecution);
        Assert.Equal(0.25m, venue.FillModel!.ProbFillOnLimit);
        Assert.Equal(0.75m, venue.FillModel.ProbSlippage);
        Assert.Equal(1m, venue.FillModel.ProbFillOnStop);
        Assert.Equal(TimeSpan.FromMilliseconds(30), venue.LatencyModel!.InsertLatency);
        Assert.Equal(TimeSpan.FromMilliseconds(30), venue.LatencyModel.CancelLatency);
        Assert.False(venue.RejectStopOrdersAtMarket);
    }
}
