using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Primitives;
using Bytex.Data;

namespace Bytex.Backtest.Tests;

// Why (R8.18): a run read each data config's whole range into a list and handed all of it over, so a dataset had to
// fit in memory about twice. Bars do not care - a year of one-minute data for an instrument is some 34 MB - but
// quote and book data do, at around 190 MB compressed per instrument-day, and a host that offers a month of it is
// offering something no run could consume.
//
// "Streaming" already existed and bounded nothing: the tradingRuntime stayed up between calls and the engine kept every
// element it had been given, so feeding a month in pieces still ended with the month in memory. A chunk size that
// looks applied and is not is worse than none, because the failure is an out-of-memory crash at some size nobody
// predicted rather than a limit somebody chose.
//
// So two things are asserted here and neither is "it runs": that a chunked run gives the SAME result as reading
// everything, and that it holds a chunk while doing it.
public sealed class ChunkedRunTests : IDisposable
{
    private readonly TempCatalog _catalog = new();

    private static readonly MarketKey _instrument = MarketKey.Parse("bx-market:v2/SIM/BTCUSDT");

    public void Dispose() => _catalog.Dispose();

    private static QuoteTick Quote(long nanos, decimal price) =>
        new(_instrument, new Price(price, 2), new Price(price + 1m, 2), new Quantity(1m, 3), new Quantity(1m, 3), new UnixNanos(nanos), new UnixNanos(nanos));

    private static TradeTick Trade(long nanos, decimal price) =>
        new(_instrument, new Price(price, 2), new Quantity(1m, 3), AggressorSide.Buyer, new TradeId(nanos.ToString(System.Globalization.CultureInfo.InvariantCulture)), new UnixNanos(nanos), new UnixNanos(nanos));

    private sealed class TempCatalog : IDisposable
    {
        public TempCatalog()
        {
            Root = Path.Combine(Path.GetTempPath(), "bytex-chunk-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Catalog = new MarketArchive(Root);
        }

        public string Root { get; }

        public MarketArchive Catalog { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void A_chunked_run_lets_go_of_what_it_has_dispatched()
    {
        // The bound itself, at the engine. Without releasing, "streaming" merely accumulated: the same elements
        // stayed in _data for the life of the run and a chunk size decided nothing.
        using SimHarness sim = SimHarness.Spot();
        BacktestEngine engine = sim.Engine;

        engine.AddData(Enumerable.Range(1, 100).Select(i => (IData)Quote(i * 1_000_000_000L, 100m + i)));
        engine.Run(streaming: true);

        Assert.Equal(100, engine.Data.Count);

        int released = engine.ReleaseDispatched();

        Assert.Equal(100, released);
        Assert.Empty(engine.Data);
    }

    [Fact]
    public void Data_that_goes_backwards_is_refused_rather_than_silently_skipped()
    {
        // A chunk out of order is not a smaller problem than out-of-order data inside one: the run's cursor has
        // already passed that moment, so those elements would be dispatched late or not at all, and a strategy would
        // simply behave oddly.
        using SimHarness sim = SimHarness.Spot();
        BacktestEngine engine = sim.Engine;

        engine.AddData([Quote(5_000_000_000L, 100m)]);
        engine.Run(streaming: true);
        engine.ReleaseDispatched();

        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(
            () => engine.AddData([Quote(1_000_000_000L, 100m)]));

        Assert.Contains("already dispatched", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_chunked_run_and_a_whole_one_produce_the_same_result()
    {
        // The property that makes the feature usable: choosing a chunk size is a memory decision, never a different
        // answer. If these ever diverge, somebody's backtest depends on how much memory their machine had.
        await _catalog.Catalog.WriteInstrumentsAsync([TestInstruments.Spot()]);
        await _catalog.Catalog.WriteQuoteTicksAsync(Enumerable.Range(1, 60).Select(i => Quote(i * 1_000_000_000L, 100m + (i % 7))));
        await _catalog.Catalog.WriteTradeTicksAsync(Enumerable.Range(1, 60).Select(i => Trade((i * 1_000_000_000L) + 500_000_000L, 100m + (i % 5))));

        BacktestRunConfig whole = Run(chunk: null);
        BacktestRunConfig chunked = Run(chunk: 7);

        BacktestNode node = new(new Core.Plugins.PluginRegistry());
        BacktestResult full = await node.RunAsync(whole);
        BacktestResult pieces = await node.RunAsync(chunked);

        Assert.Equal(full.TotalEvents, pieces.TotalEvents);
        Assert.Equal(full.Iterations, pieces.Iterations);
        Assert.Equal(full.BacktestStart, pieces.BacktestStart);
        Assert.Equal(full.BacktestEnd, pieces.BacktestEnd);
    }

    private BacktestRunConfig Run(int? chunk) => new()
    {
        Engine = new BacktestEngineConfig { RunId = chunk is null ? "whole" : "chunked" },
        ChunkSize = chunk,
        Venues = [new BacktestVenueConfig { Venue = "SIM", StartingBalances = ["100000 USDT"] }],
        Data =
        [
            new BacktestDataConfig { CatalogPath = _catalog.Root, DataKind = "quotes", MarketKey = _instrument },
            new BacktestDataConfig { CatalogPath = _catalog.Root, DataKind = "trades", MarketKey = _instrument },
        ],
    };
}
