using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Primitives;
using static Bytex.Core.Tests.Model.ModelFixtures;

namespace Bytex.Core.Tests.Model;

// Ticks and bars are what strategies see. These tests protect the derived values (mid, spread, extracted
// prices and sizes), the text form used in logs, and the topic keys that custom data is routed by.
public class MarketDataTests
{
    private static readonly InstrumentId _btc = InstrumentId.Parse("BTCUSDT.BINANCE");

    private static QuoteTick Quote(string bid, string ask, string bidSize = "1.500000", string askSize = "2.500000") =>
        new(_btc, Price.Parse(bid), Price.Parse(ask), Quantity.Parse(bidSize), Quantity.Parse(askSize), T0, At(1));

    [Fact]
    public void Quote_mid_is_the_exact_average_with_one_extra_decimal()
    {
        QuoteTick quote = Quote("100.01", "100.04");

        // (100.01 + 100.04) / 2 = 100.025, which needs a third decimal.
        Assert.Equal(new Price(100.025m, 3), quote.Mid);
        Assert.Equal("100.025", quote.Mid.ToString());
    }

    [Fact]
    public void Quote_mid_precision_never_exceeds_18()
    {
        Price bid = new(0.000000000000000001m, 18);
        Price ask = new(0.000000000000000003m, 18);
        QuoteTick quote = new(_btc, bid, ask, Quantity.Parse("1"), Quantity.Parse("1"), T0, T0);

        Assert.Equal(new Price(0.000000000000000002m, 18), quote.Mid);
    }

    [Fact]
    public void Quote_spread_is_ask_minus_bid()
    {
        Assert.Equal(0.03m, Quote("100.01", "100.04").Spread);
        Assert.Equal(0m, Quote("100.00", "100.00").Spread);
    }

    [Fact]
    public void Quote_extracts_the_price_for_each_supported_price_type()
    {
        QuoteTick quote = Quote("100.00", "100.50");

        Assert.Equal(Price.Parse("100.00"), quote.ExtractPrice(PriceType.Bid));
        Assert.Equal(Price.Parse("100.50"), quote.ExtractPrice(PriceType.Ask));
        Assert.Equal(new Price(100.25m, 3), quote.ExtractPrice(PriceType.Mid));
    }

    [Fact]
    public void Quote_cannot_supply_a_mark_price()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Quote("100.00", "100.50").ExtractPrice(PriceType.Mark));
    }

    [Fact]
    public void Quote_extracts_sizes_and_averages_them_for_mid()
    {
        QuoteTick quote = Quote("100.00", "100.50", bidSize: "1.500000", askSize: "2.500000");

        Assert.Equal(Quantity.Parse("1.500000"), quote.ExtractSize(PriceType.Bid));
        Assert.Equal(Quantity.Parse("2.500000"), quote.ExtractSize(PriceType.Ask));
        Assert.Equal(Quantity.Parse("2.000000"), quote.ExtractSize(PriceType.Mid)); // (1.5 + 2.5) / 2
    }

    [Fact]
    public void Quote_text_lists_fields_with_their_precision_and_an_iso_timestamp()
    {
        Assert.Equal(
            "BTCUSDT.BINANCE,100.01,100.04,1.500000,2.500000,2023-11-14T22:13:20.0000000Z",
            Quote("100.01", "100.04").ToString());
    }

    [Fact]
    public void Trade_text_lists_fields_with_their_precision_and_an_iso_timestamp()
    {
        TradeTick trade = new(_btc, Price.Parse("50000.10"), Quantity.Parse("0.250000"), AggressorSide.Seller, new TradeId("T-77"), T0, At(1));

        Assert.Equal("BTCUSDT.BINANCE,50000.10,0.250000,Seller,T-77,2023-11-14T22:13:20.0000000Z", trade.ToString());
    }

    [Fact]
    public void Ticks_expose_their_instrument_through_the_data_interface()
    {
        IData quote = Quote("1.00", "2.00");
        IData trade = new TradeTick(_btc, Price.Parse("1.00"), Quantity.Parse("1"), AggressorSide.Buyer, new TradeId("T-1"), T0, T0);
        IData signal = new Signal("momentum", 0.75m, T0, T0);

        Assert.Equal(_btc, quote.InstrumentId);
        Assert.Equal(_btc, trade.InstrumentId);
        Assert.Null(signal.InstrumentId);
    }

    [Fact]
    public void Bar_knows_its_instrument_and_whether_it_is_a_single_price()
    {
        BarType barType = BarType.Parse("BTCUSDT.BINANCE-1-MINUTE-LAST-EXTERNAL");
        Price p = Price.Parse("100.00");
        Bar flat = new(barType, p, p, p, p, Quantity.Parse("0"), T0, T0);
        Bar moving = new(barType, p, Price.Parse("100.50"), Price.Parse("99.50"), p, Quantity.Parse("12.5"), T0, T0);

        Assert.Equal(_btc, flat.InstrumentId);
        Assert.True(flat.IsSinglePrice);
        Assert.False(moving.IsSinglePrice);
        Assert.False(flat.IsRevision);
        Assert.Equal(
            "BTCUSDT.BINANCE-1-MINUTE-LAST-EXTERNAL,100.00,100.50,99.50,100.00,12.5,2023-11-14T22:13:20.0000000Z",
            moving.ToString());
    }

    [Fact]
    public void Book_order_exposure_is_price_times_size()
    {
        BookOrder order = new(OrderSide.Buy, Price.Parse("100.50"), Quantity.Parse("2.5"), 7);

        Assert.Equal(251.25m, order.Exposure);
        Assert.Equal("Buy 2.5 @ 100.50 (#7)", order.ToString());
    }

    [Fact]
    public void Deltas_report_whether_they_are_a_snapshot()
    {
        OrderBookDeltas snapshot = new(_btc, [], RecordFlags.Snapshot | RecordFlags.Last, 1, T0, T0);
        OrderBookDeltas update = new(_btc, [], RecordFlags.Last, 2, T0, T0);

        Assert.True(snapshot.IsSnapshot);
        Assert.False(update.IsSnapshot);
    }

    [Fact]
    public void Clear_delta_carries_the_clear_action_and_no_order()
    {
        OrderBookDelta clear = OrderBookDelta.Clear(_btc, 42, T0, At(1));

        Assert.Equal(BookAction.Clear, clear.Action);
        Assert.Equal(42UL, clear.Sequence);
        Assert.Equal(default, clear.Order);
        Assert.Equal(T0, clear.TsEvent);
    }

    [Fact]
    public void Data_type_topic_is_the_type_name_when_there_is_no_metadata()
    {
        Assert.Equal("Signal", DataType.Of<Signal>().Topic);
        Assert.Equal("Signal", DataType.Of<Signal>().ToString());
    }

    [Fact]
    public void Data_type_topic_orders_metadata_by_key_so_it_is_stable()
    {
        DataType a = DataType.Of<Signal>(new Dictionary<string, string> { ["venue"] = "BINANCE", ["kind"] = "momentum" });
        DataType b = DataType.Of<Signal>(new Dictionary<string, string> { ["kind"] = "momentum", ["venue"] = "BINANCE" });

        Assert.Equal("Signal.kind=momentum.venue=BINANCE", a.Topic);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Data_types_differ_by_type_or_metadata()
    {
        DataType plain = DataType.Of<Signal>();
        DataType tagged = DataType.Of<Signal>(new Dictionary<string, string> { ["kind"] = "momentum" });
        DataType otherType = DataType.Of<MarkPriceUpdate>();

        Assert.NotEqual(plain, tagged);
        Assert.NotEqual(plain, otherType);
        Assert.False(plain.Equals(null));
        Assert.Throws<ArgumentNullException>(() => new DataType(null!));
    }
}
