using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Model.Reports;
using static Bytex.Core.Tests.Model.ModelFixtures;

namespace Bytex.Core.Tests.Model;

// Reports drive reconciliation after a restart and commands carry the routing keys of the message bus.
// These tests protect the derived values on reports and the exact topic strings subscribers match on.
public class ReportsAndCommandsTests
{
    private static readonly InstrumentId _btc = InstrumentId.Parse("BTCUSDT.BINANCE");

    private static OrderStatusReport OrderReport(OrderStatus status, string quantity, string filled) => new(
        Account, _btc, new ClientOrderId("O-1"), new VenueOrderId("V-1"), OrderSide.Buy, OrderType.Limit, TimeInForce.Gtc,
        status, Quantity.Parse(quantity), Quantity.Parse(filled), At(1), At(2), At(3), Id(400), Price: Price.Parse("100.00"));

    private static FillReport FillOf(string venueOrderId, string tradeId) => new(
        Account, _btc, new VenueOrderId(venueOrderId), new TradeId(tradeId), OrderSide.Buy, Quantity.Parse("1.000000"),
        Price.Parse("100.00"), Usdt("0.05"), LiquiditySide.Taker, At(1), At(1), Id(401));

    [Theory]
    [InlineData("10.000000", "4.000000", "6.000000")]
    [InlineData("10.000000", "0.000000", "10.000000")]
    [InlineData("10.000000", "10.000000", "0.000000")]
    [InlineData("10.000000", "10.500000", "0.000000")] // a venue-side overfill never yields a negative remainder
    public void Order_report_leaves_quantity_is_quantity_minus_filled_floored_at_zero(string quantity, string filled, string expected)
    {
        Assert.Equal(Quantity.Parse(expected), OrderReport(OrderStatus.PartiallyFilled, quantity, filled).LeavesQuantity);
    }

    [Theory]
    [InlineData(OrderStatus.Accepted, true)]
    [InlineData(OrderStatus.Triggered, true)]
    [InlineData(OrderStatus.PendingUpdate, true)]
    [InlineData(OrderStatus.PendingCancel, true)]
    [InlineData(OrderStatus.PartiallyFilled, true)]
    [InlineData(OrderStatus.Submitted, false)]
    [InlineData(OrderStatus.Rejected, false)]
    [InlineData(OrderStatus.Canceled, false)]
    [InlineData(OrderStatus.Expired, false)]
    [InlineData(OrderStatus.Filled, false)]
    public void Order_report_is_open_for_the_same_statuses_as_an_order(OrderStatus status, bool expected)
    {
        Assert.Equal(expected, OrderReport(status, "10.000000", "0.000000").IsOpen);
    }

    [Theory]
    [InlineData(PositionSide.Long, "2.5", "2.5")]
    [InlineData(PositionSide.Short, "2.5", "-2.5")]
    [InlineData(PositionSide.Flat, "0", "0")]
    public void Position_report_signs_the_quantity_by_side(PositionSide side, string quantity, string expectedSigned)
    {
        PositionStatusReport report = new(Account, _btc, side, Quantity.Parse(quantity), At(1), At(1), Id(402));

        Assert.Equal(D(expectedSigned), report.SignedQuantity);
    }

    [Fact]
    public void Mass_status_finds_the_fills_of_one_venue_order()
    {
        ExecutionMassStatus status = new(
            new ClientId("BINANCE"), Account, new Venue("BINANCE"), [],
            [FillOf("V-1", "T-1"), FillOf("V-2", "T-2"), FillOf("V-1", "T-3")], [], At(5), Id(403));

        Assert.Equal([new TradeId("T-1"), new TradeId("T-3")], status.FillsForOrder(new VenueOrderId("V-1")).Select(f => f.TradeId));
        Assert.Empty(status.FillsForOrder(new VenueOrderId("V-404")));
    }

    [Fact]
    public void Submit_commands_are_routed_by_the_instrument_of_their_order()
    {
        MarketOrder order = MarketOrder.Create(BtcParams());
        OrderList list = new(new OrderListId("OL-1"), [order]);

        SubmitOrder submit = new(Trader, Strategy, order, null, null, new ClientId("BINANCE"), Id(500), At(1));
        SubmitOrderList submitList = new(Trader, Strategy, list, new PositionId("P-1"), null, null, Id(501), At(1));

        Assert.Equal(_btc, submit.InstrumentId);
        Assert.Equal(_btc, submitList.InstrumentId);
        Assert.Equal(new ClientId("BINANCE"), submit.ClientId);
        Assert.Null(submitList.ClientId);
    }

    public static TheoryData<SubscribeCommand, string> SubscribeTopics()
    {
        InstrumentId id = InstrumentId.Parse("BTCUSDT.BINANCE");
        BarType bars = BarType.Parse("BTCUSDT.BINANCE-1-MINUTE-LAST-EXTERNAL");
        Guid g = Id(600);
        return new()
        {
            { new SubscribeInstruments(new Venue("BINANCE"), null, g, T0), "data.instrument.BINANCE.*" },
            { new SubscribeInstrument(id, null, g, T0), "data.instrument.BINANCE.BTCUSDT" },
            { new SubscribeQuoteTicks(id, null, g, T0), "data.quotes.BINANCE.BTCUSDT" },
            { new SubscribeTradeTicks(id, null, g, T0), "data.trades.BINANCE.BTCUSDT" },
            { new SubscribeBars(bars, null, g, T0), "data.bars.BTCUSDT.BINANCE-1-MINUTE-LAST-EXTERNAL" },
            { new SubscribeOrderBookDeltas(id, BookType.L2, 10, null, g, T0), "data.book.deltas.BINANCE.BTCUSDT" },
            { new SubscribeOrderBookSnapshots(id, BookType.L2, 10, TimeSpan.FromSeconds(1), null, g, T0), "data.book.snapshots.BINANCE.BTCUSDT" },
            { new SubscribeInstrumentStatus(id, null, g, T0), "data.status.BINANCE.BTCUSDT" },
            { new SubscribeMarkPrices(id, null, g, T0), "data.mark.BINANCE.BTCUSDT" },
            { new SubscribeIndexPrices(id, null, g, T0), "data.index.BINANCE.BTCUSDT" },
            { new SubscribeFundingRates(id, null, g, T0), "data.funding.BINANCE.BTCUSDT" },
            { new SubscribeData(DataType.Of<Signal>(new Dictionary<string, string> { ["kind"] = "momentum" }), null, g, T0), "data.custom.Signal.kind=momentum" },
        };
    }

    [Theory]
    [MemberData(nameof(SubscribeTopics))]
    public void Subscribe_commands_publish_on_the_documented_topic(SubscribeCommand command, string expectedTopic)
    {
        Assert.Equal(expectedTopic, command.Topic);
    }

    public static TheoryData<SubscribeCommand, UnsubscribeCommand> SubscribeUnsubscribePairs()
    {
        InstrumentId id = InstrumentId.Parse("ETHUSDT-PERP.BINANCE");
        BarType bars = BarType.Parse("ETHUSDT-PERP.BINANCE-5-MINUTE-BID-INTERNAL");
        DataType custom = DataType.Of<Signal>();
        Guid g = Id(601);
        return new()
        {
            { new SubscribeInstruments(new Venue("BINANCE"), null, g, T0), new UnsubscribeInstruments(new Venue("BINANCE"), null, g, T0) },
            { new SubscribeInstrument(id, null, g, T0), new UnsubscribeInstrument(id, null, g, T0) },
            { new SubscribeQuoteTicks(id, null, g, T0), new UnsubscribeQuoteTicks(id, null, g, T0) },
            { new SubscribeTradeTicks(id, null, g, T0), new UnsubscribeTradeTicks(id, null, g, T0) },
            { new SubscribeBars(bars, null, g, T0), new UnsubscribeBars(bars, null, g, T0) },
            { new SubscribeOrderBookDeltas(id, BookType.L3, 0, null, g, T0), new UnsubscribeOrderBookDeltas(id, null, g, T0) },
            { new SubscribeOrderBookSnapshots(id, BookType.L2, 5, TimeSpan.FromSeconds(1), null, g, T0), new UnsubscribeOrderBookSnapshots(id, null, g, T0) },
            { new SubscribeInstrumentStatus(id, null, g, T0), new UnsubscribeInstrumentStatus(id, null, g, T0) },
            { new SubscribeMarkPrices(id, null, g, T0), new UnsubscribeMarkPrices(id, null, g, T0) },
            { new SubscribeIndexPrices(id, null, g, T0), new UnsubscribeIndexPrices(id, null, g, T0) },
            { new SubscribeFundingRates(id, null, g, T0), new UnsubscribeFundingRates(id, null, g, T0) },
            { new SubscribeData(custom, null, g, T0), new UnsubscribeData(custom, null, g, T0) },
        };
    }

    [Theory]
    [MemberData(nameof(SubscribeUnsubscribePairs))]
    public void Every_unsubscribe_targets_the_topic_of_its_subscribe(SubscribeCommand subscribe, UnsubscribeCommand unsubscribe)
    {
        Assert.Equal(subscribe.Topic, unsubscribe.Topic);
        Assert.Equal(subscribe.Venue, unsubscribe.Venue);
    }

    [Fact]
    public void Instrument_scoped_data_commands_take_their_venue_from_the_instrument()
    {
        InstrumentId id = InstrumentId.Parse("XBTUSD.BITMEX");

        Assert.Equal(new Venue("BITMEX"), new SubscribeQuoteTicks(id, null, Id(1), T0).Venue);
        Assert.Equal(new Venue("BITMEX"), new RequestTradeTicks(id, T0, At(60), 500, null, Id(1), T0).Venue);
        Assert.Equal(new Venue("BITMEX"), new RequestBars(BarType.Parse("XBTUSD.BITMEX-1-HOUR-LAST-EXTERNAL"), null, null, null, null, Id(1), T0).Venue);
        Assert.Null(new SubscribeData(DataType.Of<Signal>(), null, Id(1), T0).Venue);
    }

    [Fact]
    public void Requests_carry_their_window_limit_and_requester()
    {
        RequestQuoteTicks request = new(_btc, T0, At(60), 1000, new ClientId("BINANCE"), Id(1), T0) { Requester = new ActorId("Monitor-001") };

        Assert.Equal(T0, request.Start);
        Assert.Equal(At(60), request.End);
        Assert.Equal(1000, request.Limit);
        Assert.Equal(new ActorId("Monitor-001"), request.Requester);
        Assert.Null(new RequestInstrument(_btc, null, Id(1), T0).Limit);
    }

    [Fact]
    public void A_data_response_is_an_error_only_when_it_carries_one()
    {
        DataResponse ok = new(Id(1), new ClientId("BINANCE"), null, typeof(QuoteTick), [], T0);
        DataResponse failed = ok with { Error = "rate limited" };

        Assert.False(ok.IsError);
        Assert.True(failed.IsError);
    }

    [Fact]
    public void Event_topics_are_keyed_by_the_owning_identifier()
    {
        Assert.Equal("events.order.EmaCross-001", Topics.OrderEvents(Strategy));
        Assert.Equal("events.position.EmaCross-001", Topics.PositionEvents(Strategy));
        Assert.Equal("events.account.BINANCE-001", Topics.AccountEvents(Account));
        Assert.Equal("events.system.DataEngine", Topics.SystemEvents(new ComponentId("DataEngine")));
        Assert.Equal("responses.data.Monitor-001", Topics.DataResponses(new ActorId("Monitor-001")));
        Assert.Equal("signal.momentum", Topics.Signal("momentum"));
    }

    [Fact]
    public void Wildcard_topics_cover_the_per_strategy_topics()
    {
        Assert.StartsWith(Topics.AllOrderEvents.TrimEnd('*'), Topics.OrderEvents(Strategy), StringComparison.Ordinal);
        Assert.StartsWith(Topics.AllPositionEvents.TrimEnd('*'), Topics.PositionEvents(Strategy), StringComparison.Ordinal);
        Assert.StartsWith(Topics.AllAccountEvents.TrimEnd('*'), Topics.AccountEvents(Account), StringComparison.Ordinal);
    }
}
