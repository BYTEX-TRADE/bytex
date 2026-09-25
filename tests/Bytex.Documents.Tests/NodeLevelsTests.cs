using System.Text.Json;
using Bytex.Core.Model;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Timing;
using Bytex.Core.Trading;
using Bytex.Documents.Runtime;
using Xunit;

namespace Bytex.Documents.Tests;

// Why: a grid is not one order but a pair at every level, each taking turns and re-arming after a round trip. A node
// held one tracker and one bag of numbers, so the only way to express that was a node per level, hand-wired, which
// still could not refill. This is the state a node needs to work many levels at once: addressable, in a pinned order,
// each level's orders followed independently of its neighbours, and the whole thing saved and loaded as one value.
public sealed class NodeLevelsTests
{
    private static readonly TraderId _trader = new("TESTER-001");
    private static readonly StrategyId _strategy = new("Doc-001");
    private static readonly AccountId _account = new("SIM-001");
    private static readonly UnixNanos _ts = UnixNanos.FromSeconds(1_700_000_000);

    // One factory per test: a fresh one starts its counter again and would mint the same client order id twice,
    // which is exactly what these tests must be able to tell apart.
    private readonly OrderFactory _factory = new(_trader, _strategy, new TestClock(_ts));

    private MarketOrder Order(Instrument instrument, OrderSide side = OrderSide.Buy) =>
        _factory.Market(instrument.Id, side, instrument.MakeQuantity(0.01m));

    private static OrderFilled Fill(Instrument instrument, Order order, decimal price) =>
        new(_trader, _strategy, instrument.Id, order.ClientOrderId, new VenueOrderId("V-1"), _account, new TradeId("T-1"), new PositionId("P-1"),
            order.Side, OrderType.Market, order.Quantity, instrument.MakePrice(price), Currencies.USDT, new Money(0m, Currencies.USDT),
            LiquiditySide.Taker, Guid.NewGuid(), _ts, _ts);

    [Fact]
    public void Levels_are_enumerated_in_the_order_a_rerun_would_repeat()
    {
        NodeLevels levels = new();

        // Added out of order, and with more than nine so that ordinal sorting would get it wrong.
        foreach (string key in new[] { "10", "2", "1", "11", "3" })
        {
            levels.Ensure(key).Price = decimal.Parse(key, System.Globalization.CultureInfo.InvariantCulture);
        }

        Assert.Equal(["1", "2", "3", "10", "11"], levels.All.Select(l => l.Key));
        Assert.Equal(5, levels.Count);
        Assert.Same(levels["2"], levels.Find("2"));
        Assert.Null(levels.Find("4"));
    }

    [Fact]
    public void A_level_is_created_once_and_kept()
    {
        NodeLevels levels = new();

        NodeLevel first = levels.Ensure("1");
        first.Price = 50_000m;
        NodeLevel again = levels.Ensure("1");

        Assert.Same(first, again);
        Assert.Equal(50_000m, again.Price);
        Assert.Equal(1, levels.Count);
        Assert.True(levels.Remove("1"));
        Assert.False(levels.Remove("1"));
        Assert.Equal(0, levels.Count);
    }

    [Fact]
    public void One_levels_order_events_do_not_reach_its_neighbours()
    {
        Instrument instrument = Fixtures.BtcUsdt();
        NodeLevels levels = new();
        MarketOrder mine = Order(instrument);
        MarketOrder neighbours = Order(instrument);
        levels.Ensure("1").Entry.Track(mine, index: 7);
        levels.Ensure("2").Entry.Track(neighbours, index: 7);

        levels.OnOrderEvent(Fill(instrument, mine, 49_900m));

        Assert.True(levels["1"].Entry.PendingFilled);
        Assert.Equal(49_900m, levels["1"].Entry.FillPrice);
        Assert.False(levels["2"].Entry.PendingFilled);
        Assert.Null(levels["2"].Entry.FillPrice);
    }

    [Fact]
    public void Both_sides_of_a_level_are_followed_apart()
    {
        Instrument instrument = Fixtures.BtcUsdt();
        NodeLevels levels = new();
        NodeLevel level = levels.Ensure("1");
        MarketOrder entry = Order(instrument, OrderSide.Buy);
        MarketOrder exit = Order(instrument, OrderSide.Sell);
        level.Entry.Track(entry, index: 1);
        level.Exit.Track(exit, index: 1);

        levels.OnOrderEvent(Fill(instrument, exit, 50_100m));

        Assert.False(level.Entry.PendingFilled);
        Assert.True(level.Exit.PendingFilled);
        Assert.Equal(50_100m, level.Exit.FillPrice);
        Assert.True(level.IsWorking, "the entry side is still out");
        Assert.Single(levels.Working);
    }

    [Fact]
    public void A_levels_own_numbers_and_orders_survive_a_save_and_a_load()
    {
        Instrument instrument = Fixtures.BtcUsdt();
        NodeLevels saved = new();
        NodeLevel one = saved.Ensure("1");
        one.Price = 49_500m;
        one.Size = 0.02m;
        one.RoundTrips = 3;
        MarketOrder resting = Order(instrument);
        one.Entry.Track(resting, index: 12);
        saved.OnOrderEvent(Fill(instrument, resting, 49_500m));
        NodeLevel two = saved.Ensure("2");
        two.Price = 49_000m;
        two.Size = 0.04m;

        JsonElement state = saved.Save();
        NodeLevels loaded = new();
        loaded.Load(state);

        Assert.Equal(["1", "2"], loaded.All.Select(l => l.Key));
        Assert.Equal(49_500m, loaded["1"].Price);
        Assert.Equal(0.02m, loaded["1"].Size);
        Assert.Equal(3, loaded["1"].RoundTrips);
        Assert.Equal(resting.ClientOrderId, loaded["1"].Entry.Id);
        Assert.Equal(12, loaded["1"].Entry.SubmittedAt);
        Assert.Equal(49_500m, loaded["1"].Entry.FillPrice);
        Assert.Equal(49_000m, loaded["2"].Price);
        Assert.Null(loaded["2"].Entry.Id);
    }

    [Fact]
    public void Loading_replaces_whatever_the_node_was_working()
    {
        NodeLevels levels = new();
        levels.Ensure("9").Price = 1m;

        levels.Load(new NodeLevels().Save());

        Assert.Equal(0, levels.Count);
    }

    [Fact]
    public void A_level_with_no_orders_out_is_not_working()
    {
        Instrument instrument = Fixtures.BtcUsdt();
        NodeLevels levels = new();
        NodeLevel level = levels.Ensure("1");
        Assert.False(level.IsWorking);

        MarketOrder order = Order(instrument);
        level.Entry.Track(order, index: 1);
        Assert.True(level.IsWorking);

        level.Entry.OnOrderEvent(new OrderCanceled(_trader, _strategy, instrument.Id, order.ClientOrderId, new VenueOrderId("V-1"), _account, Guid.NewGuid(), _ts, _ts));

        Assert.False(level.IsWorking);
        Assert.Empty(levels.Working);
    }

    [Fact]
    public void A_key_that_is_not_a_key_is_refused()
    {
        NodeLevels levels = new();

        Assert.Throws<ArgumentException>(() => levels.Ensure("  "));
        Assert.Throws<ArgumentNullException>(() => levels.Ensure(null!));
    }
}
