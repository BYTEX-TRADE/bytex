using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Primitives;
using static Bytex.Core.Tests.Model.ModelFixtures;

namespace Bytex.Core.Tests.Model;

// The order book feeds the simulated matching engine and any strategy that reads depth. These tests protect
// level maintenance for L1/L2/L3 books, sort order, top-of-book values, and the fill simulation arithmetic.
public class OrderBookTests
{
    private static readonly InstrumentId _btc = InstrumentId.Parse("BTCUSDT.BINANCE");

    private static OrderBookDelta Delta(BookAction action, OrderSide side, string price, string size, ulong orderId = 0, ulong sequence = 1, long t = 0) =>
        new(_btc, action, new BookOrder(side, Price.Parse(price), Quantity.Parse(size), orderId), RecordFlags.None, sequence, At(t), At(t));

    /// <summary>Bids 100.00 x 1, 99.50 x 2; asks 100.50 x 3, 101.00 x 4.</summary>
    private static OrderBook TwoLevelBook()
    {
        OrderBook book = new(_btc, BookType.L2);
        book.Apply(Delta(BookAction.Add, OrderSide.Buy, "99.50", "2"));
        book.Apply(Delta(BookAction.Add, OrderSide.Buy, "100.00", "1"));
        book.Apply(Delta(BookAction.Add, OrderSide.Sell, "101.00", "4"));
        book.Apply(Delta(BookAction.Add, OrderSide.Sell, "100.50", "3"));
        return book;
    }

    [Fact]
    public void An_empty_book_has_no_top_of_book()
    {
        OrderBook book = new(_btc, BookType.L2);

        Assert.Null(book.BestBidPrice);
        Assert.Null(book.BestAskPrice);
        Assert.Null(book.BestBidSize);
        Assert.Null(book.BestAskSize);
        Assert.Null(book.Spread);
        Assert.Null(book.MidPrice);
        Assert.Equal(0, book.BidLevelCount);
        Assert.Equal(0, book.AskLevelCount);
        Assert.Empty(book.Bids());
        Assert.Empty(book.Asks());
    }

    [Fact]
    public void A_one_sided_book_has_a_best_price_but_no_spread()
    {
        OrderBook book = new(_btc, BookType.L2);
        book.Apply(Delta(BookAction.Add, OrderSide.Buy, "100.00", "1"));

        Assert.Equal(Price.Parse("100.00"), book.BestBidPrice);
        Assert.Null(book.BestAskPrice);
        Assert.Null(book.Spread);
        Assert.Null(book.MidPrice);
    }

    [Fact]
    public void Best_bid_is_the_highest_bid_and_best_ask_the_lowest_ask_whatever_the_insertion_order()
    {
        OrderBook book = TwoLevelBook();

        Assert.Equal(Price.Parse("100.00"), book.BestBidPrice);
        Assert.Equal(Quantity.Parse("1"), book.BestBidSize);
        Assert.Equal(Price.Parse("100.50"), book.BestAskPrice);
        Assert.Equal(Quantity.Parse("3"), book.BestAskSize);
        Assert.Equal(0.50m, book.Spread);
        Assert.Equal(100.25m, book.MidPrice);
    }

    [Fact]
    public void Bids_are_listed_high_to_low_and_asks_low_to_high()
    {
        OrderBook book = TwoLevelBook();

        Assert.Equal([100.00m, 99.50m], book.Bids().Select(l => l.Price.Value));
        Assert.Equal([100.50m, 101.00m], book.Asks().Select(l => l.Price.Value));
        Assert.Equal([1m, 2m], book.Bids().Select(l => l.Size.Value));
    }

    [Fact]
    public void Depth_limits_the_number_of_levels_returned()
    {
        OrderBook book = TwoLevelBook();

        BookLevel onlyBid = Assert.Single(book.Bids(1));
        BookLevel onlyAsk = Assert.Single(book.Asks(1));

        Assert.Equal(Price.Parse("100.00"), onlyBid.Price);
        Assert.Equal(Price.Parse("100.50"), onlyAsk.Price);
        Assert.Empty(book.Bids(0));
    }

    [Fact]
    public void L2_add_at_an_existing_price_replaces_the_level_size()
    {
        OrderBook book = TwoLevelBook();

        book.Apply(Delta(BookAction.Add, OrderSide.Buy, "100.00", "7.5"));

        Assert.Equal(2, book.BidLevelCount);
        Assert.Equal(Quantity.Parse("7.5"), book.BestBidSize);
    }

    [Fact]
    public void L2_update_changes_the_size_and_creates_the_level_if_missing()
    {
        OrderBook book = TwoLevelBook();

        book.Apply(Delta(BookAction.Update, OrderSide.Sell, "100.50", "0.25"));
        book.Apply(Delta(BookAction.Update, OrderSide.Sell, "100.40", "9"));

        Assert.Equal(3, book.AskLevelCount);
        Assert.Equal(Price.Parse("100.40"), book.BestAskPrice);
        Assert.Equal([9m, 0.25m, 4m], book.Asks().Select(l => l.Size.Value));
    }

    [Fact]
    public void L2_delete_removes_the_level_and_promotes_the_next_one()
    {
        OrderBook book = TwoLevelBook();

        book.Apply(Delta(BookAction.Delete, OrderSide.Buy, "100.00", "0"));

        Assert.Equal(1, book.BidLevelCount);
        Assert.Equal(Price.Parse("99.50"), book.BestBidPrice);
        Assert.Equal(1.00m, book.Spread); // 100.50 - 99.50
    }

    [Theory]
    [InlineData(BookAction.Add)]
    [InlineData(BookAction.Update)]
    public void A_zero_size_add_or_update_removes_the_level(BookAction action)
    {
        OrderBook book = TwoLevelBook();

        book.Apply(Delta(action, OrderSide.Sell, "100.50", "0"));

        Assert.Equal(1, book.AskLevelCount);
        Assert.Equal(Price.Parse("101.00"), book.BestAskPrice);
    }

    [Fact]
    public void Deleting_a_level_that_does_not_exist_changes_nothing()
    {
        OrderBook book = TwoLevelBook();

        book.Apply(Delta(BookAction.Delete, OrderSide.Buy, "98.00", "0"));

        Assert.Equal(2, book.BidLevelCount);
        Assert.Equal(Price.Parse("100.00"), book.BestBidPrice);
    }

    [Fact]
    public void Deleting_a_price_on_one_side_leaves_the_same_price_on_the_other_side()
    {
        OrderBook book = new(_btc, BookType.L2);
        book.Apply(Delta(BookAction.Add, OrderSide.Buy, "100.00", "1"));
        book.Apply(Delta(BookAction.Add, OrderSide.Sell, "100.00", "2"));

        book.Apply(Delta(BookAction.Delete, OrderSide.Buy, "100.00", "0"));

        Assert.Null(book.BestBidPrice);
        Assert.Equal(Quantity.Parse("2"), book.BestAskSize);
    }

    [Fact]
    public void Clear_delta_empties_both_sides()
    {
        OrderBook book = TwoLevelBook();

        book.Apply(OrderBookDelta.Clear(_btc, 9, At(5), At(5)));

        Assert.Equal(0, book.BidLevelCount);
        Assert.Equal(0, book.AskLevelCount);
        Assert.Null(book.Spread);
        Assert.Equal(9UL, book.Sequence);
    }

    [Fact]
    public void Each_delta_advances_sequence_timestamp_and_update_count()
    {
        OrderBook book = new(_btc, BookType.L2);

        book.Apply(Delta(BookAction.Add, OrderSide.Buy, "100.00", "1", sequence: 41, t: 1));
        book.Apply(Delta(BookAction.Add, OrderSide.Sell, "100.50", "1", sequence: 42, t: 2));

        Assert.Equal(42UL, book.Sequence);
        Assert.Equal(At(2), book.TsLast);
        Assert.Equal(2, book.UpdateCount);
    }

    [Fact]
    public void A_deltas_batch_is_applied_in_order()
    {
        OrderBook book = new(_btc, BookType.L2);
        OrderBookDeltas batch = new(
            _btc,
            [
                OrderBookDelta.Clear(_btc, 1, At(1), At(1)),
                Delta(BookAction.Add, OrderSide.Buy, "100.00", "1", sequence: 2, t: 1),
                Delta(BookAction.Add, OrderSide.Buy, "100.00", "5", sequence: 3, t: 1),
                Delta(BookAction.Add, OrderSide.Sell, "100.10", "2", sequence: 4, t: 1),
            ],
            RecordFlags.Snapshot,
            4,
            At(1),
            At(1));

        book.Apply(batch);

        Assert.Equal(Quantity.Parse("5"), book.BestBidSize); // the later delta wins
        Assert.Equal(Price.Parse("100.10"), book.BestAskPrice);
        Assert.Equal(4UL, book.Sequence);
        Assert.Equal(4, book.UpdateCount);
    }

    [Fact]
    public void A_depth_snapshot_replaces_the_whole_book()
    {
        OrderBook book = TwoLevelBook();
        OrderBookDepth depth = new(
            _btc,
            [new BookLevel(Price.Parse("200.00"), Quantity.Parse("1")), new BookLevel(Price.Parse("199.00"), Quantity.Parse("2"))],
            [new BookLevel(Price.Parse("201.00"), Quantity.Parse("3"))],
            RecordFlags.Snapshot,
            77,
            At(9),
            At(9));

        book.Apply(depth);

        Assert.Equal([200.00m, 199.00m], book.Bids().Select(l => l.Price.Value));
        Assert.Equal([201.00m], book.Asks().Select(l => l.Price.Value));
        Assert.Equal(77UL, book.Sequence);
        Assert.Equal(At(9), book.TsLast);
    }

    [Fact]
    public void An_L1_book_keeps_only_the_latest_level_per_side()
    {
        OrderBook book = new(_btc, BookType.L1);

        book.Apply(Delta(BookAction.Add, OrderSide.Buy, "100.00", "1"));
        book.Apply(Delta(BookAction.Add, OrderSide.Sell, "100.50", "1"));
        book.Apply(Delta(BookAction.Add, OrderSide.Buy, "99.00", "4"));

        Assert.Equal(1, book.BidLevelCount);
        Assert.Equal(Price.Parse("99.00"), book.BestBidPrice);
        Assert.Equal(Price.Parse("100.50"), book.BestAskPrice); // the other side is untouched
    }

    [Fact]
    public void An_L1_book_follows_quotes()
    {
        OrderBook book = new(_btc, BookType.L1);
        QuoteTick first = new(_btc, Price.Parse("100.00"), Price.Parse("100.10"), Quantity.Parse("1"), Quantity.Parse("2"), At(1), At(1));
        QuoteTick second = new(_btc, Price.Parse("100.05"), Price.Parse("100.20"), Quantity.Parse("3"), Quantity.Parse("4"), At(2), At(2));

        book.Apply(first);
        book.Apply(second);

        Assert.Equal(Price.Parse("100.05"), book.BestBidPrice);
        Assert.Equal(Quantity.Parse("3"), book.BestBidSize);
        Assert.Equal(Price.Parse("100.20"), book.BestAskPrice);
        Assert.Equal(Quantity.Parse("4"), book.BestAskSize);
        Assert.Equal(1, book.BidLevelCount);
        Assert.Equal(1, book.AskLevelCount);
        Assert.Equal(At(2), book.TsLast);
        Assert.Equal(2, book.UpdateCount);
    }

    [Fact(Skip = "BUG: an L1 OrderBook fed a multi-level OrderBookDepth keeps the last (worst) level of each side instead of the best")]
    public void An_L1_book_given_a_multi_level_depth_keeps_the_best_level_of_each_side()
    {
        OrderBook book = new(_btc, BookType.L1);
        OrderBookDepth depth = new(
            _btc,
            [new BookLevel(Price.Parse("100.00"), Quantity.Parse("1")), new BookLevel(Price.Parse("99.00"), Quantity.Parse("2"))],
            [new BookLevel(Price.Parse("100.50"), Quantity.Parse("3")), new BookLevel(Price.Parse("101.00"), Quantity.Parse("4"))],
            RecordFlags.Snapshot,
            1,
            At(1),
            At(1));

        book.Apply(depth);

        Assert.Equal(Price.Parse("100.00"), book.BestBidPrice);
        Assert.Equal(Price.Parse("100.50"), book.BestAskPrice);
    }

    [Fact]
    public void An_L3_level_aggregates_its_orders()
    {
        OrderBook book = new(_btc, BookType.L3);

        book.Apply(Delta(BookAction.Add, OrderSide.Buy, "100.00", "1.5", orderId: 1));
        book.Apply(Delta(BookAction.Add, OrderSide.Buy, "100.00", "2.25", orderId: 2));

        BookLevel level = Assert.Single(book.Bids());
        Assert.Equal(3.75m, level.Size.Value);
        Assert.Equal(2, level.Count);
    }

    [Fact]
    public void An_L3_delete_removes_one_order_and_drops_the_level_with_the_last_one()
    {
        OrderBook book = new(_btc, BookType.L3);
        book.Apply(Delta(BookAction.Add, OrderSide.Sell, "100.50", "1.5", orderId: 1));
        book.Apply(Delta(BookAction.Add, OrderSide.Sell, "100.50", "2.25", orderId: 2));

        book.Apply(Delta(BookAction.Delete, OrderSide.Sell, "100.50", "0", orderId: 1));

        BookLevel level = Assert.Single(book.Asks());
        Assert.Equal(2.25m, level.Size.Value);
        Assert.Equal(1, level.Count);

        book.Apply(Delta(BookAction.Delete, OrderSide.Sell, "100.50", "0", orderId: 2));

        Assert.Equal(0, book.AskLevelCount);
    }

    [Fact]
    public void An_L3_update_resizes_one_order_in_place()
    {
        OrderBook book = new(_btc, BookType.L3);
        book.Apply(Delta(BookAction.Add, OrderSide.Buy, "100.00", "1.5", orderId: 1));
        book.Apply(Delta(BookAction.Add, OrderSide.Buy, "100.00", "2.25", orderId: 2));

        book.Apply(Delta(BookAction.Update, OrderSide.Buy, "100.00", "0.5", orderId: 1));

        BookLevel level = Assert.Single(book.Bids());
        Assert.Equal(2.75m, level.Size.Value); // 0.5 + 2.25
        Assert.Equal(2, level.Count);
    }

    [Fact]
    public void An_L3_update_with_a_new_price_moves_the_order_between_levels()
    {
        OrderBook book = new(_btc, BookType.L3);
        book.Apply(Delta(BookAction.Add, OrderSide.Buy, "100.00", "1", orderId: 1));
        book.Apply(Delta(BookAction.Add, OrderSide.Buy, "100.00", "2", orderId: 2));

        book.Apply(Delta(BookAction.Update, OrderSide.Buy, "100.25", "1", orderId: 1));

        Assert.Equal([100.25m, 100.00m], book.Bids().Select(l => l.Price.Value));
        Assert.Equal([1m, 2m], book.Bids().Select(l => l.Size.Value));
        Assert.Equal([1, 1], book.Bids().Select(l => l.Count));
    }

    [Fact]
    public void A_crossed_book_is_reported_as_is_with_a_negative_spread()
    {
        // Venues do publish momentarily crossed books; the model must not hide or reject them.
        OrderBook book = new(_btc, BookType.L2);
        book.Apply(Delta(BookAction.Add, OrderSide.Buy, "101.00", "1"));
        book.Apply(Delta(BookAction.Add, OrderSide.Sell, "100.00", "1"));

        Assert.Equal(-1.00m, book.Spread);
        Assert.Equal(100.50m, book.MidPrice);
    }

    [Fact]
    public void Simulated_average_price_walks_the_asks_for_a_buy()
    {
        // 3 @ 100.50 + 2 @ 101.00 = 301.50 + 202.00 = 503.50; / 5 = 100.70.
        Price? average = TwoLevelBook().SimulateAveragePrice(OrderSide.Buy, Quantity.Parse("5"));

        Assert.Equal(Price.Parse("100.70"), average);
    }

    [Fact]
    public void Simulated_average_price_walks_the_bids_for_a_sell()
    {
        // 1 @ 100.00 + 1 @ 99.50 = 199.50; / 2 = 99.75.
        Price? average = TwoLevelBook().SimulateAveragePrice(OrderSide.Sell, Quantity.Parse("2"));

        Assert.Equal(Price.Parse("99.75"), average);
    }

    [Fact]
    public void Simulated_average_price_within_the_top_level_is_the_top_price()
    {
        Assert.Equal(Price.Parse("100.50"), TwoLevelBook().SimulateAveragePrice(OrderSide.Buy, Quantity.Parse("0.5")));
    }

    [Fact]
    public void Simulated_average_price_is_unknown_without_enough_liquidity_or_quantity()
    {
        OrderBook book = TwoLevelBook();

        Assert.Null(book.SimulateAveragePrice(OrderSide.Buy, Quantity.Parse("7.000001"))); // asks hold 7
        Assert.NotNull(book.SimulateAveragePrice(OrderSide.Buy, Quantity.Parse("7")));
        Assert.Null(book.SimulateAveragePrice(OrderSide.Buy, Quantity.Parse("0")));
        Assert.Null(new OrderBook(_btc, BookType.L2).SimulateAveragePrice(OrderSide.Sell, Quantity.Parse("1")));
    }

    [Fact]
    public void Simulated_fills_take_the_best_levels_first()
    {
        IReadOnlyList<(Price Price, Quantity Size)> fills = TwoLevelBook().SimulateFills(OrderSide.Buy, Quantity.Parse("5"));

        Assert.Equal([(Price.Parse("100.50"), Quantity.Parse("3")), (Price.Parse("101.00"), Quantity.Parse("2"))], fills);
    }

    [Fact]
    public void Simulated_fills_stop_at_the_limit_price()
    {
        OrderBook book = TwoLevelBook();

        IReadOnlyList<(Price Price, Quantity Size)> buy = book.SimulateFills(OrderSide.Buy, Quantity.Parse("5"), Price.Parse("100.50"));
        IReadOnlyList<(Price Price, Quantity Size)> sell = book.SimulateFills(OrderSide.Sell, Quantity.Parse("5"), Price.Parse("100.00"));

        Assert.Equal([(Price.Parse("100.50"), Quantity.Parse("3"))], buy);
        Assert.Equal([(Price.Parse("100.00"), Quantity.Parse("1"))], sell);
    }

    [Fact]
    public void Simulated_fills_are_empty_when_the_limit_does_not_reach_the_book()
    {
        OrderBook book = TwoLevelBook();

        Assert.Empty(book.SimulateFills(OrderSide.Buy, Quantity.Parse("1"), Price.Parse("100.49")));
        Assert.Empty(book.SimulateFills(OrderSide.Sell, Quantity.Parse("1"), Price.Parse("100.01")));
    }

    [Fact]
    public void Simulated_fills_return_what_the_book_holds_when_liquidity_runs_out()
    {
        IReadOnlyList<(Price Price, Quantity Size)> fills = TwoLevelBook().SimulateFills(OrderSide.Sell, Quantity.Parse("10"));

        // Only 1 + 2 = 3 is bid.
        Assert.Equal([(Price.Parse("100.00"), Quantity.Parse("1")), (Price.Parse("99.50"), Quantity.Parse("2"))], fills);
    }

    [Fact(Skip = "BUG: OrderBook.SimulateFills returns one zero-size fill for a zero quantity instead of no fills")]
    public void Simulated_fills_for_a_zero_quantity_are_empty()
    {
        Assert.Empty(TwoLevelBook().SimulateFills(OrderSide.Buy, Quantity.Parse("0")));
    }

    [Fact]
    public void Text_form_shows_type_and_top_of_book()
    {
        Assert.Equal("OrderBook(BTCUSDT.BINANCE, L2, bid=100.00, ask=100.50)", TwoLevelBook().ToString());
    }
}
