using System.Text.Json;
using Bytex.Core.Model;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Timing;
using Bytex.Core.Trading;
using Bytex.Persistence.Redis;

namespace Bytex.Live.Tests.Persistence;

// Why: a restarted node rebuilds every order and account from these JSON strings. If one event type does not
// survive the round trip, the node comes back with wrong order state. No Redis server is involved here.
public sealed class EventSerializerTests
{
    private static readonly TraderId _trader = new("TESTER-001");
    private static readonly StrategyId _strategy = new("EmaCross-001");
    private static readonly InstrumentId _instrument = InstrumentId.Parse("BTCUSDT-PERP.BINANCE");
    private static readonly ClientOrderId _clientOrderId = new("O-20240101-000000-001-001-1");
    private static readonly VenueOrderId _venueOrderId = new("8886774");
    private static readonly AccountId _account = new("BINANCE-USDMFUTURES");
    private static readonly UnixNanos _tsEvent = new(1_700_000_000_123_456_789);
    private static readonly UnixNanos _tsInit = new(1_700_000_000_223_456_789);

    public static TheoryData<OrderEvent> OrderEvents()
    {
        Guid id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        return new TheoryData<OrderEvent>
        {
            new OrderDenied(_trader, _strategy, _instrument, _clientOrderId, "exceeds max notional", id, _tsEvent, _tsInit),
            new OrderEmulated(_trader, _strategy, _instrument, _clientOrderId, id, _tsEvent, _tsInit),
            new OrderReleased(_trader, _strategy, _instrument, _clientOrderId, new Price(30_000.5m, 1), id, _tsEvent, _tsInit),
            new OrderSubmitted(_trader, _strategy, _instrument, _clientOrderId, _account, id, _tsEvent, _tsInit),
            new OrderAccepted(_trader, _strategy, _instrument, _clientOrderId, _venueOrderId, _account, id, _tsEvent, _tsInit, Reconciliation: true),
            new OrderRejected(_trader, _strategy, _instrument, _clientOrderId, _account, "Account has insufficient balance", id, _tsEvent, _tsInit),
            new OrderCanceled(_trader, _strategy, _instrument, _clientOrderId, _venueOrderId, _account, id, _tsEvent, _tsInit),
            new OrderExpired(_trader, _strategy, _instrument, _clientOrderId, _venueOrderId, _account, id, _tsEvent, _tsInit),
            new OrderTriggered(_trader, _strategy, _instrument, _clientOrderId, _venueOrderId, _account, id, _tsEvent, _tsInit),
            new OrderPendingUpdate(_trader, _strategy, _instrument, _clientOrderId, _venueOrderId, _account, id, _tsEvent, _tsInit),
            new OrderPendingCancel(_trader, _strategy, _instrument, _clientOrderId, null, _account, id, _tsEvent, _tsInit),
            new OrderModifyRejected(_trader, _strategy, _instrument, _clientOrderId, _venueOrderId, _account, "price out of range", id, _tsEvent, _tsInit),
            new OrderCancelRejected(_trader, _strategy, _instrument, _clientOrderId, _venueOrderId, _account, "Unknown order sent.", id, _tsEvent, _tsInit),
            new OrderUpdated(_trader, _strategy, _instrument, _clientOrderId, _venueOrderId, _account, new Quantity(0.250m, 3), new Price(29_999.9m, 1), null, id, _tsEvent, _tsInit),
            new OrderFilled(_trader, _strategy, _instrument, _clientOrderId, _venueOrderId, _account, new TradeId("T-42"), new PositionId("P-1"), OrderSide.Sell, OrderType.Limit,
                new Quantity(0.125m, 3), new Price(30_001.1m, 1), Currencies.USDT, new Money(0.75000225m, Currencies.USDT), LiquiditySide.Maker, id, _tsEvent, _tsInit),
        };
    }

    [Theory]
    [MemberData(nameof(OrderEvents))]
    public void Every_order_event_type_survives_a_round_trip_unchanged(OrderEvent original)
    {
        string json = EventSerializer.Serialize(original);

        OrderEvent? restored = EventSerializer.DeserializeOrderEvent(json);

        Assert.NotNull(restored);
        Assert.IsType(original.GetType(), restored);
        Assert.Equal(original, restored);
    }

    [Fact]
    public void The_envelope_names_the_event_type_so_streams_can_be_replayed_without_guessing()
    {
        OrderCanceled e = new(_trader, _strategy, _instrument, _clientOrderId, _venueOrderId, _account, Guid.NewGuid(), _tsEvent, _tsInit);

        using JsonDocument doc = JsonDocument.Parse(EventSerializer.Serialize(e));

        Assert.Equal("OrderCanceled", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Object, doc.RootElement.GetProperty("payload").ValueKind);
    }

    [Fact]
    public void Nanosecond_timestamps_and_decimal_precision_are_not_rounded()
    {
        OrderFilled fill = new(_trader, _strategy, _instrument, _clientOrderId, _venueOrderId, _account, new TradeId("T-1"), null, OrderSide.Buy, OrderType.Market,
            new Quantity(0.00000001m, 8), new Price(0.00001234m, 8), Currencies.USDT, new Money(0.00000001m, Currencies.USDT), LiquiditySide.Taker, Guid.NewGuid(), _tsEvent, _tsInit);

        OrderFilled restored = Assert.IsType<OrderFilled>(EventSerializer.DeserializeOrderEvent(EventSerializer.Serialize(fill)));

        Assert.Equal(1_700_000_000_123_456_789, restored.TsEvent.Value);
        Assert.Equal(0.00000001m, restored.LastQty.Value);
        Assert.Equal(8, restored.LastQty.Precision);
        Assert.Equal(0.00001234m, restored.LastPx.Value);
        Assert.Equal(0.00000001m, restored.Commission.Amount);
    }

    [Fact]
    public void Account_state_survives_a_round_trip_with_balances_margins_and_info()
    {
        AccountState original = new(_account, AccountType.Margin, Currencies.USDT, true,
            [AccountBalance.Of(new Money(1000.5m, Currencies.USDT), new Money(250.25m, Currencies.USDT)), AccountBalance.Unlocked(new Money(0.5m, Currencies.BTC))],
            [new MarginBalance(new Money(100m, Currencies.USDT), new Money(50m, Currencies.USDT), _instrument)],
            new Dictionary<string, string> { ["source"] = "ACCOUNT_UPDATE" }, Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), _tsEvent, _tsInit);

        AccountState? restored = EventSerializer.DeserializeAccountState(EventSerializer.Serialize(original));

        Assert.NotNull(restored);
        Assert.Equal(original.AccountId, restored.AccountId);
        Assert.Equal(AccountType.Margin, restored.AccountType);
        Assert.Equal(Currencies.USDT, restored.BaseCurrency);
        Assert.True(restored.Reported);
        Assert.Equal(original.Balances, restored.Balances);
        Assert.Equal(750.25m, restored.Balances[0].Free.Amount);
        Assert.Equal(original.Margins, restored.Margins);
        Assert.Equal("ACCOUNT_UPDATE", restored.Info["source"]);
        Assert.Equal(original.EventId, restored.EventId);
        Assert.Equal(original.TsEvent, restored.TsEvent);
    }

    [Fact]
    public void An_account_state_is_not_mistaken_for_an_order_event_and_vice_versa()
    {
        AccountState state = new(_account, AccountType.Cash, null, true, [], [], new Dictionary<string, string>(), Guid.NewGuid(), _tsEvent, _tsInit);
        OrderSubmitted submitted = new(_trader, _strategy, _instrument, _clientOrderId, _account, Guid.NewGuid(), _tsEvent, _tsInit);

        Assert.Null(EventSerializer.DeserializeOrderEvent(EventSerializer.Serialize(state)));
        Assert.Null(EventSerializer.DeserializeAccountState(EventSerializer.Serialize(submitted)));
    }

    [Fact]
    public void An_order_rebuilt_from_its_serialized_event_stream_matches_the_live_order()
    {
        // The same replay RedisCacheDatabase.LoadOrders performs, minus the server.
        TestClock clock = new(_tsEvent);
        OrderFactory factory = new(_trader, _strategy, clock);
        LimitOrder live = factory.Limit(_instrument, OrderSide.Buy, new Quantity(1.000m, 3), new Price(30_000.0m, 1), TimeInForce.Gtc, postOnly: true, tags: ["ENTRY", "BATCH-7"]);
        live.Apply(new OrderSubmitted(_trader, _strategy, _instrument, live.ClientOrderId, _account, Guid.NewGuid(), _tsEvent, _tsInit));
        live.Apply(new OrderAccepted(_trader, _strategy, _instrument, live.ClientOrderId, _venueOrderId, _account, Guid.NewGuid(), _tsEvent, _tsInit));
        live.Apply(new OrderFilled(_trader, _strategy, _instrument, live.ClientOrderId, _venueOrderId, _account, new TradeId("T-1"), null, OrderSide.Buy, OrderType.Limit,
            new Quantity(0.400m, 3), new Price(29_999.5m, 1), Currencies.USDT, new Money(2.4m, Currencies.USDT), LiquiditySide.Maker, Guid.NewGuid(), _tsEvent, _tsInit));

        List<OrderEvent> replayed = live.Events.Select(e => EventSerializer.DeserializeOrderEvent(EventSerializer.Serialize(e))!).ToList();
        Order rebuilt = OrderUnpacker.FromEvents(replayed);

        LimitOrder limit = Assert.IsType<LimitOrder>(rebuilt);
        Assert.Equal(live.ClientOrderId, limit.ClientOrderId);
        Assert.Equal(OrderStatus.PartiallyFilled, limit.Status);
        Assert.Equal(_venueOrderId, limit.VenueOrderId);
        Assert.Equal(30_000.0m, limit.Price!.Value.Value);
        Assert.Equal(0.400m, limit.FilledQuantity.Value);
        Assert.Equal(0.600m, limit.LeavesQuantity.Value);
        Assert.Equal(29_999.5m, limit.AvgPx);
        Assert.True(limit.IsPostOnly);
        Assert.Equal(["ENTRY", "BATCH-7"], limit.Tags);
        Assert.Equal(live.EventCount, limit.EventCount);
    }
}
