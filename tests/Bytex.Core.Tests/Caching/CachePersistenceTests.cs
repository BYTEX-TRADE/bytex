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

        public List<OrderList> OrderLists { get; } = new();

        public Dictionary<string, byte[]> Blobs { get; } = new(StringComparer.Ordinal);

        public void Flush() => Calls.Add("Flush");

        public IReadOnlyList<Currency> LoadCurrencies() => [];

        public IReadOnlyList<Instrument> LoadInstruments() => Instruments;

        public IReadOnlyList<Account> LoadAccounts() => Accounts;

        public IReadOnlyList<Order> LoadOrders() => Orders;

        public IReadOnlyList<Position> LoadPositions(IReadOnlyDictionary<MarketKey, Instrument> instruments) => Positions;

        public IReadOnlyList<OrderList> LoadOrderLists(IReadOnlyDictionary<ClientOrderId, Order> orders) => OrderLists;

        public IDictionary<string, byte[]>? LoadRuntimeModuleState(RuntimeModuleId runtimeModuleId) => null;

        public void AddCurrency(Currency currency) => Calls.Add($"AddCurrency:{currency.Code}:{currency.Precision}");

        public void AddInstrument(Instrument instrument) => Calls.Add($"AddInstrument:{instrument.Id}");

        public void AddAccount(Account account) => Calls.Add($"AddAccount:{account.Id}");

        public void AddOrder(Order order) => Calls.Add($"AddOrder:{order.ClientOrderId}");

        public void AddPosition(Position position) => Calls.Add($"AddPosition:{position.Id}");

        public void AddOrderList(OrderList list) => Calls.Add($"AddOrderList:{list.Id}:{list.Orders.Count}");

        public void UpdateAccount(Account account) => Calls.Add($"UpdateAccount:{account.Id}");

        public void UpdateOrder(Order order) => Calls.Add($"UpdateOrder:{order.ClientOrderId}:{order.Status}");

        public void UpdatePosition(Position position) => Calls.Add($"UpdatePosition:{position.Id}");

        public void SaveRuntimeModuleState(RuntimeModuleId runtimeModuleId, IDictionary<string, byte[]> state) => Calls.Add($"SaveRuntimeModuleState:{runtimeModuleId}");

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
        cache.SaveRuntimeModuleState(new RuntimeModuleId("A-1"), new Dictionary<string, byte[]>());

        Assert.Equal(
            [
                "AddCurrency:USDT:8", "AddCurrency:BTC:8", "AddInstrument:bx-market:v2/BINANCE/BTCUSDT",
                "AddAccount:BINANCE-001", "UpdateAccount:BINANCE-001",
                "AddOrder:O-1", "UpdateOrder:O-1:Submitted", "SaveRuntimeModuleState:A-1",
            ],
            db.Calls);
    }

    // Why: the load path reads currencies and, until this was written, nothing wrote them. An unknown code is not
    // an error anywhere - Currency.FromCode invents one at the default precision of eight - so an instrument quoted in
    // a 6dp token came back after a restart quoted in an 8dp currency of the same name, and every amount in it rounded
    // two places too far out. The instrument is the record that cannot be read back without its currencies, so the
    // currencies go with it, and before it.
    [Fact]
    public void An_instrument_is_stored_with_the_currencies_it_cannot_be_read_back_without()
    {
        FakeDatabase db = new();
        Cache cache = new(new CacheConfig { Persist = true }, db);
        Currency token = new("USDX", 6, 0, "Six-place token", CurrencyType.Crypto);
        CurrencyPair instrument = TestInstruments.Spot(new MarketKey(new Symbol("BTCUSDX"), TestIds.Binance), Currencies.BTC, token, 2, 3);

        cache.AddInstrument(instrument);

        Assert.Equal(["AddCurrency:USDX:6", "AddCurrency:BTC:8", "AddInstrument:bx-market:v2/BINANCE/BTCUSDX"], db.Calls);
    }

    [Fact]
    public void An_account_is_stored_with_the_currencies_its_balances_are_in()
    {
        FakeDatabase db = new();
        Cache cache = new(new CacheConfig { Persist = true }, db);
        Currency token = new("USDY", 6, 0, "Six-place token", CurrencyType.Crypto);
        CashAccount account = new(TestEvents.CashState(TestIds.BinanceAccount, (token, 100m, 0m)));

        cache.AddAccount(account);

        Assert.Equal(["AddCurrency:USDY:6", "AddAccount:BINANCE-001"], db.Calls);
    }

    [Fact]
    public void A_balance_in_a_new_currency_is_written_on_an_update_and_not_only_on_the_first_state()
    {
        // A venue can pay a funding amount or a rebate in something the account never held, and the currency of a
        // balance that arrived later is no less needed to read the account back.
        FakeDatabase db = new();
        Cache cache = new(new CacheConfig { Persist = true }, db);
        Currency token = new("USDZ", 6, 0, "Six-place token", CurrencyType.Crypto);
        CashAccount account = new(TestEvents.CashState(TestIds.BinanceAccount, (Currencies.USDT, 100m, 0m)));
        cache.AddAccount(account);
        account.Apply(TestEvents.CashState(TestIds.BinanceAccount, (Currencies.USDT, 100m, 0m), (token, 5m, 0m)));

        cache.UpdateAccount(account);

        Assert.Contains("AddCurrency:USDZ:6", db.Calls);
    }

    [Fact]
    public void One_currency_is_written_once_however_many_instruments_share_it()
    {
        // A venue's instrument list is thousands of instruments over a handful of currencies, and a write each is
        // thousands of round trips to say USDT again.
        FakeDatabase db = new();
        Cache cache = new(new CacheConfig { Persist = true }, db);

        cache.AddInstrument(TestInstruments.BtcUsdt());
        cache.AddInstrument(TestInstruments.EthUsdt());

        Assert.Equal(1, db.Calls.Count(c => c == "AddCurrency:USDT:8"));
    }

    [Fact]
    public void A_corrected_precision_is_stored_rather_than_taken_for_the_one_already_there()
    {
        // The saving above must not become a way of keeping a wrong number: the same code with a different precision
        // is a different currency, and the stored one is now the wrong one.
        FakeDatabase db = new();
        Cache cache = new(new CacheConfig { Persist = true }, db);

        cache.AddCurrency(new Currency("USDW", 8, 0, "Token", CurrencyType.Crypto));
        cache.AddCurrency(new Currency("USDW", 6, 0, "Token", CurrencyType.Crypto));

        Assert.Equal(["AddCurrency:USDW:8", "AddCurrency:USDW:6"], db.Calls);
    }

    // Why: a bracket that outlives a restart as three unrelated orders is worse than one that does not survive at all,
    // because nothing afterwards can tell that cancelling one was meant to cancel the others.
    [Fact]
    public void An_order_list_is_written_and_comes_back_with_the_orders_already_loaded()
    {
        FakeDatabase db = new();
        Cache cache = new(new CacheConfig { Persist = true }, db);
        LimitOrder entry = TestOrders.Limit("O-1", TestIds.BtcUsdt, OrderSide.Buy, "1.000", "50000.00");
        LimitOrder exit = TestOrders.Limit("O-2", TestIds.BtcUsdt, OrderSide.Sell, "1.000", "51000.00");
        OrderList list = new(new OrderListId("OL-1"), [entry, exit]);

        cache.AddOrderList(list);

        Assert.Contains("AddOrderList:OL-1:2", db.Calls);

        db.Orders.AddRange([entry, exit]);
        db.OrderLists.Add(list);
        Cache restarted = new(new CacheConfig { Persist = true }, db);

        restarted.LoadFromDatabase();

        OrderList back = Assert.Single(restarted.OrderLists());
        Assert.Equal(new OrderListId("OL-1"), back.Id);
        // The list's orders are the cache's orders, not copies of them: a copy would stop agreeing the moment one of
        // them was filled.
        Assert.Same(restarted.Order(entry.ClientOrderId), back.Orders[0]);
        Assert.Same(restarted.Order(exit.ClientOrderId), back.Orders[1]);
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
