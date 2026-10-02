using Bytex.Backtest.Tests.Support;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Data;

namespace Bytex.Backtest.Tests;

// A data kind has to be registered in THREE places before a person can use it: the catalog's own table of kinds,
// the dispatch that consolidates one, and this node's configuration. Nothing tied them together, and both of the
// other two have already been missed - depth was stored by the catalog and then could not be consolidated, and
// could not be named by a run either, so a run had to fetch it from the vendor again on every execution. Both
// times the feature worked and was unreachable.
//
// So these tests do not list the kinds. They ask the catalog what it holds and require every answer to be
// loadable, on both paths a run can take - loaded whole, and streamed in chunks. A seventh kind added to the
// catalog and forgotten here fails this without anybody remembering to come back.
public sealed class BacktestDataKindTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bytex-datakind-tests-" + Guid.NewGuid().ToString("N"));
    private readonly CurrencyPair _spot = TestInstruments.Spot();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string CatalogPath => Path.Combine(_root, "catalog");

    /// <summary>One record of every kind the catalog can store, so that every kind has a data set to be asked for.</summary>
    private async Task<MarketArchive> CatalogOfEveryKindAsync()
    {
        MarketArchive catalog = new(CatalogPath);
        MarketKey id = _spot.Id;
        UnixNanos t0 = new(1_700_000_000_000_000_000L);
        UnixNanos t1 = new(1_700_000_000_000_000_001L);

        await catalog.WriteInstrumentsAsync([_spot]);
        await catalog.WriteBarsAsync(GoldenEmaCrossBacktestTests.Bars(_spot));
        await catalog.WriteQuoteTicksAsync([new QuoteTick(id, new Price(100m, 2), new Price(101m, 2), new Quantity(1m, 3), new Quantity(1m, 3), t0, t1)]);
        await catalog.WriteTradeTicksAsync([new TradeTick(id, new Price(100m, 2), new Quantity(1m, 3), AggressorSide.Buyer, new TradeId("t-1"), t0, t1)]);
        await catalog.WriteOrderBookDeltasAsync([new OrderBookDelta(id, BookAction.Add, new BookOrder(OrderSide.Buy, new Price(99m, 2), new Quantity(1m, 3), 1), RecordFlags.None, 1, t0, t1)]);
        await catalog.WriteOrderBookDepthAsync([new OrderBookDepth(id, [new BookLevel(new Price(99m, 2), new Quantity(1m, 3))], [new BookLevel(new Price(101m, 2), new Quantity(1m, 3))], RecordFlags.Snapshot, 1, t0, t1)]);
        await catalog.WriteFundingRatesAsync([new FundingRateUpdate(id, 0.0001m, null, t0, t1)]);
        return catalog;
    }

    private BacktestRunConfig RunFor(string kind, int? chunkSize = null) => new()
    {
        Engine = new BacktestEngineConfig { RunId = kind },
        Venues = [new BacktestVenueConfig { Venue = "SIM", StartingBalances = ["10000 USDT"] }],
        Data =
        [
            new BacktestDataConfig
            {
                CatalogPath = CatalogPath,
                DataKind = kind,

                // Bars are asked for by bar type; every other kind by instrument.
                MarketKey = kind == "bars" ? null : _spot.Id,
                CandleSeries = kind == "bars" ? Scripted.MinuteBars(_spot) : null,
            },
        ],
        ChunkSize = chunkSize,
    };

    /// <summary>
    /// <b>Every kind the catalog admits to holding can be named by a run.</b> Driven from the catalog's own
    /// listing, so this cannot go stale the way a hand-written list of kinds did.
    /// </summary>
    [Fact]
    public async Task Every_kind_the_catalog_holds_can_be_loaded_by_a_run()
    {
        MarketArchive catalog = await CatalogOfEveryKindAsync();
        string[] kinds = [.. catalog.Entries().Select(e => e.Kind).Distinct().Order(StringComparer.Ordinal)];

        Assert.Equal(["bars", "book_deltas", "book_depth", "funding", "quotes", "trades"], kinds);

        foreach (string kind in kinds)
        {
            using BacktestEngine engine = await new BacktestNode().BuildEngineAsync(RunFor(kind));
            Assert.True(engine.Data.Count > 0, $"a run asking for '{kind}' was built with no data");
        }
    }

    /// <summary>And on the streamed path too, which is a second switch over the same kinds.</summary>
    [Fact]
    public async Task Every_kind_the_catalog_holds_can_be_streamed_by_a_chunked_run()
    {
        MarketArchive catalog = await CatalogOfEveryKindAsync();
        string[] kinds = [.. catalog.Entries().Select(e => e.Kind).Distinct().Order(StringComparer.Ordinal)];

        foreach (string kind in kinds)
        {
            // A chunked run streams instead of loading, so this exercises the other switch. Running it to
            // completion is what proves the kind is accepted there: an unknown kind throws from the enumerator.
            BacktestResult result = await new BacktestNode().RunAsync(RunFor(kind, chunkSize: 1));
            Assert.Equal(kind, result.RunId);
        }
    }

    /// <summary>
    /// Depth by name, called out on its own: the catalog gained somewhere to put a book so that a run would read
    /// it once instead of fetching it from the vendor on every execution, and a run could not ask for it.
    /// </summary>
    [Fact]
    public async Task A_run_can_be_fed_stored_book_depth()
    {
        await CatalogOfEveryKindAsync();

        using BacktestEngine engine = await new BacktestNode().BuildEngineAsync(RunFor("book_depth"));

        Assert.Contains(engine.Data, d => d is OrderBookDepth);
    }

    /// <summary>A kind the catalog does not hold is still refused, and the message says what was asked for.</summary>
    [Fact]
    public async Task A_kind_the_catalog_does_not_hold_is_refused()
    {
        await CatalogOfEveryKindAsync();

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new BacktestNode().BuildEngineAsync(RunFor("greeks")));

        Assert.Contains("greeks", refused.Message, StringComparison.Ordinal);
    }
}
