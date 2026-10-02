using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Primitives;
using static Bytex.Core.Tests.Model.ModelFixtures;

namespace Bytex.Core.Tests.Model;

// Every other market-data record is made of value types, so the equality the compiler generates for a record is
// right for them. These two are not: a snapshot holds each side as an IReadOnlyList and a batch holds its deltas the
// same way, so generated equality compared the list REFERENCES and two records with identical contents were never
// equal.
//
// It failed the way that costs the most time. Nothing threw and nothing reported which field differed - the answer
// was always "different" - so anything deduplicating a repeated snapshot quietly did nothing, and a test written the
// obvious way failed with both sides printing identically.
//
// So these tests are about the comparison itself rather than about books: same contents equal, one level different
// not equal, and the hash agreeing with the comparison.
public class BookRecordEqualityTests
{
    private static readonly MarketKey _btc = MarketKey.Parse("bx-market:v2/BINANCE/BTCUSDT");
    private static readonly MarketKey _eth = MarketKey.Parse("bx-market:v2/BINANCE/ETHUSDT");

    private static BookLevel[] Bids(int levels = 3) =>
        [.. Enumerable.Range(0, levels).Select(i => new BookLevel(new Price(41999.99m - (i * 0.01m), 2), new Quantity(3.00000m + i, 5), i + 1))];

    private static BookLevel[] Asks(int levels = 3) =>
        [.. Enumerable.Range(0, levels).Select(i => new BookLevel(new Price(42000.01m + (i * 0.01m), 2), new Quantity(0.00001m + i, 5), i + 2))];

    private static OrderBookDepth Depth(IReadOnlyList<BookLevel> bids, IReadOnlyList<BookLevel> asks, ulong sequence = 7, MarketKey? id = null) =>
        new(id ?? _btc, bids, asks, RecordFlags.Snapshot, sequence, T0, At(1));

    private static OrderBookDelta Delta(ulong orderId, decimal price = 41999.99m) =>
        new(_btc, BookAction.Add, new BookOrder(OrderSide.Buy, new Price(price, 2), new Quantity(3.00000m, 5), orderId), RecordFlags.None, orderId, T0, At(1));

    // ---------- OrderBookDepth ----------

    /// <summary>
    /// <b>The case that was broken.</b> Two snapshots built separately from equal levels, so the two lists are
    /// different objects - which is what every real caller has, because one of them came off a wire or out of a file.
    /// </summary>
    [Fact]
    public void Two_snapshots_with_the_same_levels_are_equal()
    {
        OrderBookDepth one = Depth(Bids(), Asks());
        OrderBookDepth two = Depth(Bids(), Asks());

        Assert.NotSame(one.Bids, two.Bids);
        Assert.Equal(one, two);
        Assert.True(one == two);
        Assert.False(one != two);
        Assert.Equal(one.GetHashCode(), two.GetHashCode());
    }

    /// <summary>A snapshot equals itself, and is not equal to null or to another type.</summary>
    [Fact]
    public void A_snapshot_equals_itself_and_nothing_else()
    {
        OrderBookDepth depth = Depth(Bids(), Asks());

        Assert.Equal(depth, depth);
        Assert.False(depth.Equals(null));
        Assert.False(depth.Equals((object?)"not a book"));
    }

    /// <summary>One level different is not equal - the comparison reads the levels, it does not just count them.</summary>
    [Theory]
    [InlineData("price")]
    [InlineData("size")]
    [InlineData("count")]
    public void One_level_differing_makes_two_snapshots_unequal(string field)
    {
        BookLevel[] mine = Bids();
        BookLevel original = mine[1];
        mine[1] = field switch
        {
            "price" => original with { Price = new Price(original.Price.Value + 0.01m, 2) },
            "size" => original with { Size = new Quantity(original.Size.Value + 1m, 5) },
            _ => original with { Count = original.Count + 1 },
        };

        Assert.NotEqual(Depth(Bids(), Asks()), Depth(mine, Asks()));
    }

    /// <summary>A shorter ladder is not equal to a longer one that starts the same way.</summary>
    [Fact]
    public void A_shorter_ladder_is_not_equal_to_a_longer_one()
    {
        Assert.NotEqual(Depth(Bids(3), Asks(3)), Depth(Bids(2), Asks(3)));
        Assert.NotEqual(Depth(Bids(3), Asks(3)), Depth(Bids(3), Asks(2)));
    }

    /// <summary>
    /// The sides are not interchangeable: a book whose bids and asks are swapped is a different book, even though
    /// the multiset of levels is the same.
    /// </summary>
    [Fact]
    public void Swapping_the_two_sides_makes_a_different_book()
    {
        BookLevel[] bids = Bids();
        BookLevel[] asks = Asks();

        Assert.NotEqual(Depth(bids, asks), Depth(asks, bids));
    }

    /// <summary>And every scalar still counts, which is the part generated equality already had right.</summary>
    [Fact]
    public void The_scalars_still_decide()
    {
        OrderBookDepth baseline = Depth(Bids(), Asks());

        Assert.NotEqual(baseline, Depth(Bids(), Asks(), id: _eth));
        Assert.NotEqual(baseline, Depth(Bids(), Asks(), sequence: 8));
        Assert.NotEqual(baseline, baseline with { Flags = RecordFlags.None });
        Assert.NotEqual(baseline, baseline with { EventTime = At(2) });
        Assert.NotEqual(baseline, baseline with { CreatedTime = At(3) });
    }

    /// <summary>
    /// <c>with</c> keeps working, and a copy that changed nothing is still equal - the levels are carried over by
    /// reference and the comparison does not care either way.
    /// </summary>
    [Fact]
    public void A_with_copy_that_changed_nothing_is_equal()
    {
        OrderBookDepth depth = Depth(Bids(), Asks());

        Assert.Equal(depth, depth with { });
        Assert.Equal(depth.GetHashCode(), (depth with { }).GetHashCode());
    }

    /// <summary>
    /// The point of the hash: a set recognises the second copy. A dictionary keyed on a snapshot was the use that
    /// silently did nothing before.
    /// </summary>
    [Fact]
    public void A_set_holds_one_of_two_identical_snapshots()
    {
        HashSet<OrderBookDepth> set = [Depth(Bids(), Asks()), Depth(Bids(), Asks())];

        Assert.Single(set);
        Assert.Contains(Depth(Bids(), Asks()), set);
    }

    /// <summary>An empty book is a book, and two of them are equal rather than accidentally distinct.</summary>
    [Fact]
    public void Two_empty_books_are_equal()
    {
        Assert.Equal(Depth([], []), Depth([], []));
        Assert.NotEqual(Depth([], []), Depth(Bids(1), []));
    }

    // ---------- OrderBookDeltas ----------

    /// <summary>The batch had the same defect for the same reason, and is fixed the same way.</summary>
    [Fact]
    public void Two_delta_batches_with_the_same_deltas_are_equal()
    {
        OrderBookDeltas one = new(_btc, [Delta(1), Delta(2)], RecordFlags.Snapshot, 2, T0, At(1));
        OrderBookDeltas two = new(_btc, [Delta(1), Delta(2)], RecordFlags.Snapshot, 2, T0, At(1));

        Assert.NotSame(one.Deltas, two.Deltas);
        Assert.Equal(one, two);
        Assert.True(one == two);
        Assert.Equal(one.GetHashCode(), two.GetHashCode());
    }

    /// <summary>One delta different, or one missing, or reordered, is a different batch.</summary>
    [Fact]
    public void A_batch_differing_in_its_deltas_is_not_equal()
    {
        OrderBookDeltas baseline = new(_btc, [Delta(1), Delta(2)], RecordFlags.Snapshot, 2, T0, At(1));

        Assert.NotEqual(baseline, baseline with { Deltas = [Delta(1), Delta(3)] });
        Assert.NotEqual(baseline, baseline with { Deltas = [Delta(1)] });
        Assert.NotEqual(baseline, baseline with { Deltas = [Delta(2), Delta(1)] });
        Assert.NotEqual(baseline, baseline with { Sequence = 3 });
        Assert.Equal(baseline, baseline with { Deltas = [Delta(1), Delta(2)] });
    }

    /// <summary>
    /// The other market-data records were already right, because every member of them is a value type. Kept here so
    /// that a later change to one of them has to notice this.
    /// </summary>
    [Fact]
    public void The_value_type_records_were_already_equal_by_value()
    {
        QuoteTick quote = new(_btc, new Price(1m, 2), new Price(2m, 2), new Quantity(1m, 5), new Quantity(1m, 5), T0, At(1));
        TradeTick trade = new(_btc, new Price(1m, 2), new Quantity(1m, 5), AggressorSide.Buyer, new TradeId("t-1"), T0, At(1));

        Assert.Equal(quote, quote with { });
        Assert.Equal(trade, trade with { });
        Assert.Equal(Delta(1), Delta(1));
    }
}
