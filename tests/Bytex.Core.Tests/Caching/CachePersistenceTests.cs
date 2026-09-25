using Bytex.Core.Caching;
using Bytex.Core.Model;
using Bytex.Core.Model.Accounts;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Positions;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Tests.Support;

namespace Bytex.Core.Tests.Caching;

// Why: R6.5 - with Persist on, every state change is written through to the database, and a restart must
// rebuild the same indexes from what the database returns.
public class CachePersistenceTests
{
    private sealed class FakeDatabase : ICacheDatabase
    {
        public List<string> Calls { get; } = new();

        public List<Instrument> Instruments { get; } = new();

        public List<Account> Accounts { get; } = new();

        public List<Order> Orders { get; } = new();

        public List<Position> Positions { get; } = new();

        public Dictionary<string, byte[]> Blobs { get; } = new(StringComparer.Ordinal);

        public void Flush() => Calls.Add("Flush");

        public IReadOnlyList<Currency> LoadCurrencies() => [];

        public IReadOnlyList<Instrument> LoadInstruments() => Instruments;

        public IReadOnlyList<Account> LoadAccounts() => Accounts;

        public IReadOnlyList<Order> LoadOrders() => Orders;

        public IReadOnlyList<Position> LoadPositions(IReadOnlyDictionary<InstrumentId, Instrument> instruments) => Positions;

        public IDictionary<string, byte[]>? LoadActorState(ActorId actorId) => null;

        public void AddCurrency(Currency currency) => Calls.Add("AddCurrency");

        public void AddInstrument(Instrument instrument) => Calls.Add($"AddInstrument:{instrument.Id}");

        public void AddAccount(Account account) => Calls.Add($"AddAccount:{account.Id}");

        public void AddOrder(Order order) => Calls.Add($"AddOrder:{order.ClientOrderId}");

        public void AddPosition(Position position) => Calls.Add($"AddPosition:{position.Id}");

        public void UpdateAccount(Account account) => Calls.Add($"UpdateAccount:{account.Id}");

        public void UpdateOrder(Order order) => Calls.Add($"UpdateOrder:{order.ClientOrderId}:{order.Status}");

        public void UpdatePosition(Position position) => Calls.Add($"UpdatePosition:{position.Id}");

        public void SaveActorState(ActorId actorId, IDictionary<string, byte[]> state) => Calls.Add($"SaveActorState:{actorId}");

        public void Add(string key, byte[] value) => Blobs[key] = value;

        public byte[]? Get(string key) => Blobs.GetValueOrDefault(key);
    }

    [Fact]
    public void Writes_go_through_to_the_database_when_persistence_is_on()
    {
        FakeDatabase db = new();
        Cache cache = new(new CacheConfig { Persist = true }, db);
        LimitOrder order = TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00");
        CashAccount account = new(TestEvents.CashState(TestIds.BinanceAccount, (Currencies.USDT, 100m, 0m)));

        cache.AddInstrument(TestInstruments.BtcUsdt());
        cache.AddAccount(account);
        cache.UpdateAccount(account);
        cache.AddOrder(order);
        order.Apply(TestEvents.Submitted(order));
        cache.UpdateOrder(order);
        cache.SaveActorState(new ActorId("A-1"), new Dictionary<string, byte[]>());

        Assert.Equal(
            ["AddInstrument:BTCUSDT.BINANCE", "AddAccount:BINANCE-001", "UpdateAccount:BINANCE-001", "AddOrder:O-1", "UpdateOrder:O-1:Submitted", "SaveActorState:A-1"],
            db.Calls);
    }

    [Fact]
    public void Nothing_is_written_when_persistence_is_off()
    {
        FakeDatabase db = new();
        Cache cache = new(new CacheConfig { Persist = false }, db);

        cache.AddInstrument(TestInstruments.BtcUsdt());
        cache.AddOrder(TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00"));
        cache.Add("k", [1]);

        Assert.Empty(db.Calls);
        Assert.Empty(db.Blobs);
    }

    [Fact]
    public void General_values_missing_from_memory_are_read_from_the_database_only_when_persisting()
    {
        FakeDatabase db = new();
        db.Blobs["k"] = [7];

        Assert.Equal(new byte[] { 7 }, new Cache(new CacheConfig { Persist = true }, db).Get("k"));
        Assert.Null(new Cache(new CacheConfig { Persist = false }, db).Get("k"));
    }

    [Fact]
    public void Loading_from_the_database_rebuilds_lookups_and_status_indexes()
    {
        FakeDatabase db = new();
        CurrencyPair instrument = TestInstruments.BtcUsdt();
        LimitOrder open = TestOrders.Limit("O-OPEN", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00");
        open.Apply(TestEvents.Submitted(open));
        open.Apply(TestEvents.Accepted(open, "V-1"));
        MarketOrder filled = TestOrders.Market("O-FILLED", TestIds.BtcUsdt, OrderSide.Buy, "1.000");
        filled.Apply(TestEvents.Submitted(filled));
        OrderFilledInto(filled, out Position position, instrument);
        db.Instruments.Add(instrument);
        db.Accounts.Add(new CashAccount(TestEvents.CashState(TestIds.BinanceAccount, (Currencies.USDT, 100m, 0m))));
        db.Orders.AddRange([open, filled]);
        db.Positions.Add(position);
        Cache cache = new(new CacheConfig { Persist = true }, db);

        cache.LoadFromDatabase();

        Assert.Same(instrument, cache.Instrument(TestIds.BtcUsdt));
        Assert.NotNull(cache.AccountForVenue(TestIds.Binance));
        Assert.Same(open, Assert.Single(cache.OrdersOpen()));
        Assert.Same(filled, Assert.Single(cache.OrdersClosed()));
        Assert.Same(open, cache.OrderForVenueId(new VenueOrderId("V-1")));
        Assert.Same(position, Assert.Single(cache.PositionsOpen()));
        Assert.Same(position, cache.PositionForOrder(filled.ClientOrderId));
        Assert.True(cache.CheckIntegrity());
        Assert.Empty(db.Calls); // loading must not write anything back
    }

    [Fact]
    public void Flush_empties_the_cache_and_the_database()
    {
        FakeDatabase db = new();
        Cache cache = new(new CacheConfig { Persist = true }, db);
        cache.AddInstrument(TestInstruments.BtcUsdt());

        cache.Flush();

        Assert.Empty(cache.Instruments());
        Assert.Equal("Flush", db.Calls[^1]);
    }

    [Fact]
    public void Loading_without_a_database_is_a_no_op()
    {
        Cache cache = new();

        cache.LoadFromDatabase();

        Assert.Empty(cache.Orders());
    }

    private static void OrderFilledInto(MarketOrder order, out Position position, Instrument instrument)
    {
        OrderFilled fill = TestEvents.Filled(order, "T-1", "1.000", "50000.00", positionId: new PositionId("P-1"));
        order.Apply(fill);
        position = new Position(instrument, fill);
    }
}
