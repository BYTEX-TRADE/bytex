using Bytex.Core.Trading;
using Bytex.Core.Indicators;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Tests.Support;

namespace Bytex.Core.Tests.Trading;

// Why (R7.8): an indicator that reads the book has to be fed the way every other one is - by being registered, not by a
// strategy remembering to call it. What is pinned here is that registration feeds it on a delta and on a snapshot, that
// what it is shown is the ASSEMBLED book rather than the change, and that the registration will not take an indicator
// that cannot read a book at all.
public class BookIndicatorTests
{
    /// <summary>A book indicator that remembers what it was shown, and nothing else.</summary>
    private sealed class RecordingBookIndicator : IOrderBookIndicator
    {
        public string Name => "RECORDER";

        public bool HasInputs { get; private set; }

        public bool IsInitialized { get; private set; }

        public List<(decimal Bid, decimal Ask)> Seen { get; } = new();

        public void Update(OrderBook book)
        {
            Seen.Add((book.BestBidSize?.Value ?? 0m, book.BestAskSize?.Value ?? 0m));
            HasInputs = true;
            IsInitialized = true;
        }

        public void Update(Bar bar) => throw new NotSupportedException();

        public void Update(QuoteTick tick) => throw new NotSupportedException();

        public void Update(TradeTick tick) => throw new NotSupportedException();

        public void Reset()
        {
            Seen.Clear();
            HasInputs = false;
            IsInitialized = false;
        }
    }

    private static OrderBookDelta Delta(decimal price, decimal size, OrderSide side, ulong sequence) => new(
        TestIds.BtcUsdt,
        BookAction.Update,
        new BookOrder(side, new Price(price, 2), new Quantity(size, 3), sequence),
        RecordFlags.None,
        sequence,
        TestOrders.T0,
        TestOrders.T0);

    [Fact]
    public void A_registered_book_indicator_is_fed_the_assembled_book_on_every_delta()
    {
        // Deltas are a change, not a book. An indicator shown the deltas would have to assemble the book itself, and
        // every book indicator would assemble the same one again.
        using TradingRuntimeHarness h = new();
        ProbeRuntimeModule runtimeModule = new(new RuntimeModuleConfig { RuntimeModuleId = new RuntimeModuleId("Watcher-001") });
        h.TradingRuntime.ModuleHost.AddRuntimeModule(runtimeModule);
        h.TradingRuntime.Start();   // an runtimeModule only handles anything while it is running
        RecordingBookIndicator indicator = new();
        runtimeModule.DoRegisterForBook(TestIds.BtcUsdt, indicator);
        runtimeModule.DoSubscribeBookDeltas(TestIds.BtcUsdt);

        // The book in the cache is what the data engine has already applied the deltas to by the time an runtimeModule is called.
        OrderBook assembled = h.TradingRuntime.Cache.GetOrCreateOrderBook(TestIds.BtcUsdt, BookType.L2);
        assembled.Apply(Delta(100m, 3m, OrderSide.Buy, 1UL));
        assembled.Apply(Delta(101m, 1m, OrderSide.Sell, 2UL));
        runtimeModule.HandleOrderBookDeltas(new OrderBookDeltas(
            TestIds.BtcUsdt,
            [Delta(100m, 3m, OrderSide.Buy, 1UL)],
            RecordFlags.None,
            1UL,
            TestOrders.T0,
            TestOrders.T0));

        Assert.Equal((3m, 1m), Assert.Single(indicator.Seen));
        Assert.True(indicator.IsInitialized);
    }

    [Fact]
    public void A_registered_book_indicator_is_fed_a_snapshot_as_it_arrives()
    {
        using TradingRuntimeHarness h = new();
        ProbeRuntimeModule runtimeModule = new(new RuntimeModuleConfig { RuntimeModuleId = new RuntimeModuleId("Watcher-001") });
        h.TradingRuntime.ModuleHost.AddRuntimeModule(runtimeModule);
        h.TradingRuntime.Start();   // an runtimeModule only handles anything while it is running
        RecordingBookIndicator indicator = new();
        runtimeModule.DoRegisterForBook(TestIds.BtcUsdt, indicator);

        OrderBook book = new(TestIds.BtcUsdt, BookType.L2);
        book.Apply(Delta(100m, 5m, OrderSide.Buy, 1UL));
        book.Apply(Delta(101m, 2m, OrderSide.Sell, 2UL));
        runtimeModule.HandleOrderBook(book);

        Assert.Equal((5m, 2m), Assert.Single(indicator.Seen));
    }

    [Fact]
    public void An_indicator_registered_for_one_instrument_is_not_fed_another()
    {
        using TradingRuntimeHarness h = new();
        ProbeRuntimeModule runtimeModule = new(new RuntimeModuleConfig { RuntimeModuleId = new RuntimeModuleId("Watcher-001") });
        h.TradingRuntime.ModuleHost.AddRuntimeModule(runtimeModule);
        h.TradingRuntime.Start();   // an runtimeModule only handles anything while it is running
        RecordingBookIndicator indicator = new();
        runtimeModule.DoRegisterForBook(TestIds.BtcUsdt, indicator);

        OrderBook other = new(TestIds.EthUsdt, BookType.L2);
        runtimeModule.HandleOrderBook(other);

        Assert.Empty(indicator.Seen);
    }

    [Fact]
    public void A_book_indicator_is_counted_among_the_actors_indicators()
    {
        // So that "are my indicators ready" includes it, which is the question a strategy asks before it acts.
        using TradingRuntimeHarness h = new();
        ProbeRuntimeModule runtimeModule = new(new RuntimeModuleConfig { RuntimeModuleId = new RuntimeModuleId("Watcher-001") });
        h.TradingRuntime.ModuleHost.AddRuntimeModule(runtimeModule);
        h.TradingRuntime.Start();   // an runtimeModule only handles anything while it is running
        RecordingBookIndicator indicator = new();

        runtimeModule.DoRegisterForBook(TestIds.BtcUsdt, indicator);

        Assert.Equal(1, runtimeModule.IndicatorCount);
        Assert.False(runtimeModule.AllIndicatorsInitialized);

        OrderBook book = new(TestIds.BtcUsdt, BookType.L2);
        book.Apply(Delta(100m, 5m, OrderSide.Buy, 1UL));
        runtimeModule.HandleOrderBook(book);

        Assert.True(runtimeModule.AllIndicatorsInitialized);
    }
}
