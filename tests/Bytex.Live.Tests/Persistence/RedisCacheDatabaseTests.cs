using System.Text;
using Bytex.Core.Model;
using Bytex.Core.Model.Accounts;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Positions;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Timing;
using Bytex.Core.Trading;
using Bytex.Live.Tests.Support;
using Bytex.Persistence.Redis;

namespace Bytex.Live.Tests.Persistence;

// Why: this is what a node's state looks like after a restart. The store keeps orders and positions as their event
// streams, accounts as their states, instruments and currencies as JSON, and rebuilds all of it from what is on the
// server - so what matters is the key each entity lands under, the index that finds it again, that an update appends
// only what is new, and that the rebuilt order is the order that was saved. A server is not needed to check any of
// that, and is the only part of the picture that is not this engine's code.
public sealed class RedisCacheDatabaseTests
{
    /// <summary>Redis as a dictionary: strings, lists and hashes, with the same semantics the store relies on.</summary>
    private sealed class MemoryStore : IRedisStore
    {
        public Dictionary<string, byte[]> Strings { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, List<string>> Lists { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, HashSet<string>> Sets { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, Dictionary<string, byte[]>> Hashes { get; } = new(StringComparer.Ordinal);

        public List<string> Deleted { get; } = new();

        public byte[]? StringGet(string key) => Strings.GetValueOrDefault(key);

        public void StringSet(string key, byte[] value) => Strings[key] = value;

        public IReadOnlyList<string> SetMembers(string key) => Sets.TryGetValue(key, out HashSet<string>? set) ? set.Order(StringComparer.Ordinal).ToList() : [];

        public void SetAdd(string key, string member) => (Sets.TryGetValue(key, out HashSet<string>? set) ? set : Sets[key] = new(StringComparer.Ordinal)).Add(member);

        public IReadOnlyList<string> ListRange(string key) => Lists.TryGetValue(key, out List<string>? list) ? list : [];

        public long ListLength(string key) => Lists.TryGetValue(key, out List<string>? list) ? list.Count : 0;

        public void ListRightPush(string key, string value) => (Lists.TryGetValue(key, out List<string>? list) ? list : Lists[key] = new()).Add(value);

        public void KeyDelete(string key)
        {
            Deleted.Add(key);
            Strings.Remove(key);
            Lists.Remove(key);
            Sets.Remove(key);
            Hashes.Remove(key);
        }

        public IReadOnlyDictionary<string, byte[]> HashGetAll(string key) => Hashes.TryGetValue(key, out Dictionary<string, byte[]>? hash) ? hash : new Dictionary<string, byte[]>(StringComparer.Ordinal);

        public void HashSet(string key, IReadOnlyDictionary<string, byte[]> fields) => Hashes[key] = new Dictionary<string, byte[]>(fields, StringComparer.Ordinal);

        public IEnumerable<string> Keys => Strings.Keys.Concat(Lists.Keys).Concat(Sets.Keys).Concat(Hashes.Keys);
    }

    private static readonly RedisCacheConfig _config = new() { TraderId = "TESTER-001" };

    private static (RedisCacheDatabase Db, MemoryStore Store) New(RedisCacheConfig? config = null)
    {
        MemoryStore store = new();
        return (new RedisCacheDatabase(config ?? _config, store), store);
    }

    private static readonly TraderId _trader = new("TESTER-001");
    private static readonly StrategyId _strategy = new("EmaCross-001");
    private static readonly AccountId _account = new("SIM-001");
    private static readonly VenueOrderId _venueOrderId = new("V-1");
    private static readonly UnixNanos _ts = UnixNanos.FromSeconds(1_700_000_000);

    private static Instrument Instrument() => TestInstruments.BtcUsdt("SIM");

    private static MarketOrder Submitted(Instrument instrument)
    {
        OrderFactory factory = new(_trader, _strategy, new TestClock(_ts));
        MarketOrder order = factory.Market(instrument.Id, OrderSide.Buy, instrument.MakeQuantity(0.5m));
        order.Apply(new OrderSubmitted(_trader, _strategy, instrument.Id, order.ClientOrderId, _account, Guid.NewGuid(), _ts, _ts));
        return order;
    }

    private static MarketOrder FilledOrder(Instrument instrument)
    {
        MarketOrder order = Submitted(instrument);
        order.Apply(new OrderAccepted(_trader, _strategy, instrument.Id, order.ClientOrderId, _venueOrderId, _account, Guid.NewGuid(), _ts, _ts));
        order.Apply(Fill(instrument, order));
        return order;
    }

    private static OrderFilled Fill(Instrument instrument, Order order) =>
        new(_trader, _strategy, instrument.Id, order.ClientOrderId, _venueOrderId, _account, new TradeId("T-1"), new PositionId("P-1"), OrderSide.Buy, OrderType.Market,
            instrument.MakeQuantity(0.5m), instrument.MakePrice(50_000m), Currencies.USDT, new Money(25m, Currencies.USDT), LiquiditySide.Taker, Guid.NewGuid(), _ts, _ts);

    // ----- keys and indexes -----

    [Fact]
    public void Every_entity_is_stored_under_the_trader_it_belongs_to_and_indexed_by_its_own_id()
    {
        (RedisCacheDatabase db, MemoryStore store) = New();
        Instrument instrument = Instrument();

        Order order = FilledOrder(instrument);
        db.AddCurrency(Currencies.USDT);
        db.AddInstrument(instrument);
        db.AddOrder(order);

        Assert.Contains("bytex:TESTER-001:currencies:USDT", store.Keys);
        Assert.Contains("bytex:TESTER-001:instruments:BTCUSDT.SIM", store.Keys);
        Assert.Contains("bytex:TESTER-001:orders:" + order.ClientOrderId.Value, store.Keys);
        Assert.Equal(["USDT"], store.SetMembers("bytex:TESTER-001:index:currencies"));
        Assert.Equal(["BTCUSDT.SIM"], store.SetMembers("bytex:TESTER-001:index:instruments"));
        Assert.Equal([order.ClientOrderId.Value], store.SetMembers("bytex:TESTER-001:index:orders"));
    }

    [Fact]
    public void Two_traders_and_two_prefixes_never_read_each_others_state()
    {
        (RedisCacheDatabase mine, MemoryStore store) = New();
        RedisCacheDatabase theirs = new(new RedisCacheConfig { TraderId = "OTHER-001" }, store);
        RedisCacheDatabase elsewhere = new(new RedisCacheConfig { TraderId = "TESTER-001", KeyPrefix = "staging" }, store);
        Instrument instrument = Instrument();

        mine.AddInstrument(instrument);

        Assert.Single(mine.LoadInstruments());
        Assert.Empty(theirs.LoadInstruments());
        Assert.Empty(elsewhere.LoadInstruments());
    }

    // ----- what comes back -----

    [Fact]
    public void A_currency_and_an_instrument_come_back_as_they_were_written()
    {
        (RedisCacheDatabase db, _) = New();
        Currency custom = new("XBT", 8, 0, "Test coin", CurrencyType.Crypto);
        Instrument instrument = Instrument();

        db.AddCurrency(custom);
        db.AddInstrument(instrument);

        Currency read = Assert.Single(db.LoadCurrencies());
        Assert.Equal("XBT", read.Code);
        Assert.Equal(8, read.Precision);
        Assert.Equal("Test coin", read.Name);
        Instrument back = Assert.Single(db.LoadInstruments());
        Assert.Equal(instrument.Id, back.Id);
        Assert.Equal(instrument.PriceIncrement, back.PriceIncrement);
        Assert.Equal(instrument.SizeIncrement, back.SizeIncrement);
    }

    [Fact]
    public void An_order_is_kept_as_its_event_stream_and_rebuilt_from_it()
    {
        (RedisCacheDatabase db, MemoryStore store) = New();
        Instrument instrument = Instrument();
        Order order = FilledOrder(instrument);

        db.AddOrder(order);

        Assert.Equal(order.Events.Count, store.ListLength("bytex:TESTER-001:orders:" + order.ClientOrderId.Value));
        Order back = Assert.Single(db.LoadOrders());
        Assert.Equal(order.ClientOrderId, back.ClientOrderId);
        Assert.Equal(OrderStatus.Filled, back.Status);
        Assert.Equal(order.FilledQuantity, back.FilledQuantity);
        Assert.Equal(order.AvgPx, back.AvgPx);
        Assert.Equal(order.Events.Count, back.Events.Count);
    }

    [Fact]
    public void An_updated_order_appends_only_the_events_the_server_does_not_have()
    {
        (RedisCacheDatabase db, MemoryStore store) = New();
        Instrument instrument = Instrument();
        MarketOrder order = Submitted(instrument);
        string key = "bytex:TESTER-001:orders:" + order.ClientOrderId.Value;
        db.AddOrder(order);
        long afterAdd = store.ListLength(key);

        order.Apply(new OrderAccepted(_trader, _strategy, instrument.Id, order.ClientOrderId, _venueOrderId, _account, Guid.NewGuid(), _ts, _ts));
        db.UpdateOrder(order);
        db.UpdateOrder(order);

        Assert.Equal(afterAdd + 1, store.ListLength(key));
        Assert.Equal(OrderStatus.Accepted, Assert.Single(db.LoadOrders()).Status);
    }

    [Fact]
    public void A_position_is_rebuilt_from_its_fills_and_one_on_an_unknown_instrument_is_left_alone()
    {
        (RedisCacheDatabase db, _) = New();
        Instrument instrument = Instrument();
        Position position = new(instrument, Fill(instrument, Submitted(instrument)));

        db.AddPosition(position);

        Position back = Assert.Single(db.LoadPositions(new Dictionary<InstrumentId, Instrument> { [instrument.Id] = instrument }));
        Assert.Equal(position.Id, back.Id);
        Assert.Equal(PositionSide.Long, back.Side);
        Assert.Equal(position.Quantity, back.Quantity);
        Assert.Equal(position.AvgPxOpen, back.AvgPxOpen);
        // Without the instrument there is no way to value it, so it is not invented.
        Assert.Empty(db.LoadPositions(new Dictionary<InstrumentId, Instrument>()));
    }

    [Fact]
    public void An_account_comes_back_with_the_balances_of_its_latest_state()
    {
        (RedisCacheDatabase db, _) = New();
        AccountId id = new("SIM-001");
        AccountState first = new(id, AccountType.Cash, Currencies.USDT, true, [AccountBalance.Of(new Money(1_000m, Currencies.USDT), Money.Zero(Currencies.USDT))], [], new Dictionary<string, string>(), Guid.NewGuid(), _ts, _ts);
        AccountState second = first with { Balances = [AccountBalance.Of(new Money(900m, Currencies.USDT), new Money(100m, Currencies.USDT))], TsEvent = _ts.AddNanos(1), EventId = Guid.NewGuid() };
        CashAccount account = new(first);
        account.Apply(second);

        db.AddAccount(account);

        Account back = Assert.Single(db.LoadAccounts());
        Assert.Equal(id, back.Id);
        Assert.Equal(900m, back.Balance(Currencies.USDT)!.Total.Amount);
        Assert.Equal(100m, back.Balance(Currencies.USDT)!.Locked.Amount);
    }

    [Fact]
    public void Actor_state_round_trips_and_a_later_save_replaces_the_fields_of_the_earlier_one()
    {
        (RedisCacheDatabase db, _) = New();
        ActorId actor = new("Watcher-001");

        Assert.Null(db.LoadActorState(actor));

        db.SaveActorState(actor, new Dictionary<string, byte[]> { ["seen"] = BitConverter.GetBytes(3), ["note"] = Encoding.UTF8.GetBytes("first") });
        IDictionary<string, byte[]> saved = db.LoadActorState(actor)!;
        Assert.Equal(3, BitConverter.ToInt32(saved["seen"]));
        Assert.Equal("first", Encoding.UTF8.GetString(saved["note"]));

        db.SaveActorState(actor, new Dictionary<string, byte[]> { ["seen"] = BitConverter.GetBytes(4) });
        IDictionary<string, byte[]> again = db.LoadActorState(actor)!;
        Assert.Equal(4, BitConverter.ToInt32(again["seen"]));
        Assert.False(again.ContainsKey("note"), "a field the actor no longer saves must not come back from the last run");
    }

    [Fact]
    public void A_general_value_is_returned_byte_for_byte_and_an_unknown_key_is_null()
    {
        (RedisCacheDatabase db, _) = New();
        byte[] value = [0x00, 0x7f, 0xff, 0x10];

        db.Add("order-id-counter", value);

        Assert.Equal(value, db.Get("order-id-counter"));
        Assert.Null(db.Get("nothing-was-stored-here"));
    }

    [Fact]
    public void Flushing_removes_every_entity_and_the_indexes_that_found_them()
    {
        (RedisCacheDatabase db, MemoryStore store) = New();
        Instrument instrument = Instrument();
        db.AddCurrency(Currencies.USDT);
        db.AddInstrument(instrument);
        db.AddOrder(FilledOrder(instrument));
        db.SaveActorState(new ActorId("Watcher-001"), new Dictionary<string, byte[]> { ["seen"] = [1] });
        db.Add("counter", [2]);

        db.Flush();

        Assert.Empty(store.Keys);
        Assert.Empty(db.LoadOrders());
        Assert.Empty(db.LoadInstruments());
        Assert.Empty(db.LoadCurrencies());
        Assert.Null(db.LoadActorState(new ActorId("Watcher-001")));
        Assert.Null(db.Get("counter"));
    }
}
