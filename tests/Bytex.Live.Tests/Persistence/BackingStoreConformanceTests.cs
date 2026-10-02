using Bytex.Core.Caching;
using Bytex.Core.Model;
using Bytex.Core.Model.Accounts;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Trading;
using Bytex.Live.Tests.Support;
using Bytex.Persistence.Postgres;
using Bytex.Persistence.Redis;

namespace Bytex.Live.Tests.Persistence;

// Why: the key layout, the indexes and the rebuilding of orders are one class now, shared by every backing store,
// and a backend supplies ten key operations under it. That makes a second store cheap and moves the whole risk onto
// one question: does this backend mean by those operations what the store above believes?
//
// Nothing about that is obvious. A set holds each member once - PostgreSQL knows because a primary key says so and
// Redis because a set is a set. A list keeps the order it was appended in, which is the order an order's events are
// replayed in: Redis keeps it by construction, and PostgreSQL promises NO order without ORDER BY, so a query
// missing one rebuilds an order with its acceptance after its fill. A key deleted is a key absent - which on a
// store with a table per shape is four deletes and not one.
//
// It is asked through ICacheDatabase rather than through the ten operations, deliberately. That is the contract a
// node depends on, so a promise stated here is a promise somebody can actually rely on; and it is one public type
// rather than the shared internal one, which each library holds its own copy of - an assembly seeing the internals
// of two of them sees two of every shared type, and cannot name either.
//
// A backend with no server answering reports SKIP with the reason rather than passing for having checked nothing.
public sealed class BackingStoreConformanceTests
{
    [Fact]
    public void A_dictionary_keeps_every_promise()
    {
        MemoryRedisStore store = new();
        Promises.EveryOne(moduleHost => new RedisCacheDatabase(new RedisCacheConfig { ModuleHostId = moduleHost }, store));
    }

    [RedisFact]
    public void Redis_keeps_every_promise()
    {
        string prefix = RedisServer.Prefix();
        Promises.EveryOne(moduleHost => new RedisCacheDatabase(new RedisCacheConfig
        {
            ConnectionString = RedisServer.ConnectionString,
            KeyPrefix = prefix,
            ModuleHostId = moduleHost,
        }));
    }

    [PostgresFact]
    public void PostgreSQL_keeps_every_promise()
    {
        string tables = PostgresServer.Tables();
        try
        {
            Promises.EveryOne(moduleHost => new PostgresCacheDatabase(new PostgresCacheConfig
            {
                ConnectionString = PostgresServer.ConnectionString,
                TablePrefix = tables,
                ModuleHostId = moduleHost,
            }));
        }
        finally
        {
            PostgresServer.Drop(tables);
        }
    }

    /// <summary>
    /// What a backing store has to mean. Each assertion names the promise it is about, because one method covering
    /// several has to say which one broke.
    /// </summary>
    private static class Promises
    {
        private static readonly MarketKey _btc = MarketKey.Parse("bx-market:v2/BINANCE/BTCUSDT");
        private static readonly StrategyId _strategy = new("Conformance-1");
        private static readonly ModuleHostId _moduleHost = new("TESTER-000");

        /// <summary>One contract, with the precisions every price and size below is made at.</summary>
        private static CryptoPerpetual Perpetual() => new(new InstrumentSpec
        {
            Id = _btc,
            RawSymbol = new Symbol("BTCUSDT"),
            AssetClass = AssetClass.Crypto,
            InstrumentClass = InstrumentClass.Swap,
            QuoteCurrency = Currencies.USDT,
            BaseCurrency = Currencies.BTC,
            SettlementCurrency = Currencies.USDT,
            PricePrecision = 2,
            SizePrecision = 3,
            PriceIncrement = new Price(0.01m, 2),
            SizeIncrement = new Quantity(0.001m, 3),
            MakerFee = 0m,
            TakerFee = 0m,
        });

        public static void EveryOne(Func<string, ICacheDatabase> open)
        {
            string mine = "CONFORM-" + Guid.NewGuid().ToString("N")[..6];
            string theirs = "OTHER-" + Guid.NewGuid().ToString("N")[..6];

            using Disposing db = new(open(mine));
            Instrument instrument = Perpetual();

            // An instrument and a currency round-trip, and a store nobody has written to loads empty rather than
            // throwing - a node's first start has to work.
            Assert.Empty(db.Value.LoadOrders());
            db.Value.AddCurrency(Currencies.USDT);
            db.Value.AddInstrument(instrument);
            Assert.Contains(Currencies.USDT.Code, db.Value.LoadCurrencies().Select(c => c.Code));
            Assert.Contains(_btc, db.Value.LoadInstruments().Select(i => i.Id));

            // AN ORDER IS REBUILT FROM ITS EVENTS, IN THE ORDER THEY HAPPENED. This is the promise that makes a
            // resumed node the node that stopped, and the one a store with no ordering guarantee breaks silently:
            // an acceptance replayed after a fill is an order in the wrong state holding somebody's money.
            OrderFactory orders = new(_moduleHost, _strategy, new Bytex.Core.Timing.LiveClock());
            LimitOrder order = orders.Limit(_btc, OrderSide.Buy, instrument.MakeQuantity(1m), instrument.MakePrice(25_000m));
            AccountId account = new("BINANCE-001");
            db.Value.AddOrder(order);

            order.Apply(new OrderSubmitted(order.ModuleHostId, order.StrategyId, order.MarketKey, order.ClientOrderId, account, Guid.NewGuid(), UnixNanos.FromMilliseconds(1), UnixNanos.FromMilliseconds(1)));
            db.Value.UpdateOrder(order);
            order.Apply(new OrderAccepted(order.ModuleHostId, order.StrategyId, order.MarketKey, order.ClientOrderId, new VenueOrderId("V-1"), account, Guid.NewGuid(), UnixNanos.FromMilliseconds(2), UnixNanos.FromMilliseconds(2)));
            db.Value.UpdateOrder(order);

            Order reloaded = Assert.Single(db.Value.LoadOrders());
            Assert.Equal(order.ClientOrderId, reloaded.ClientOrderId);
            Assert.Equal(
                [nameof(OrderInitialized), nameof(OrderSubmitted), nameof(OrderAccepted)],
                reloaded.Events.Select(e => e.GetType().Name));
            Assert.Equal(OrderStatus.Accepted, reloaded.Status);

            // UPDATING TWICE DOES NOT STORE THE ORDER TWICE. The index behind a reload is a set, and a store that
            // let a member in twice would rebuild one order as two - two positions, from one.
            db.Value.UpdateOrder(order);
            Assert.Single(db.Value.LoadOrders());

            // ACTOR STATE IS A MAP, AND WRITING SOME OF IT LEAVES THE REST. A strategy's memory is written a key at
            // a time; a store that replaced the whole map would forget everything it was not told again.
            RuntimeModuleId runtimeModule = new("Conformance-RuntimeModule");
            Assert.Null(db.Value.LoadRuntimeModuleState(runtimeModule));
            db.Value.SaveRuntimeModuleState(runtimeModule, new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["phase"] = [1], ["anchor"] = [2] });
            IDictionary<string, byte[]> state = Assert.IsAssignableFrom<IDictionary<string, byte[]>>(db.Value.LoadRuntimeModuleState(runtimeModule));
            Assert.Equal(2, state.Count);
            Assert.Equal<byte[]>([1], state["phase"]);
            Assert.Equal<byte[]>([2], state["anchor"]);

            // A GENERAL ENTRY ROUND-TRIPS, and a key nobody wrote is absent rather than empty.
            Assert.Null(db.Value.Get("nothing-here"));
            db.Value.Add("something", [3, 4]);
            Assert.Equal<byte[]>([3, 4], db.Value.Get("something")!);

            // TWO TRADERS SHARE A DATABASE WITHOUT SHARING STATE. Every key carries the moduleHost, so a second node
            // loads nothing of the first's - and flushes nothing of it either. A store that scoped neither would
            // hand one node the other's orders, which is the shape of a real defect in a comparable engine.
            using Disposing other = new(open(theirs));
            Assert.Empty(other.Value.LoadOrders());
            Assert.Empty(other.Value.LoadInstruments());
            other.Value.Flush();
            Assert.Single(db.Value.LoadOrders());
            Assert.Equal<byte[]>([3, 4], db.Value.Get("something")!);

            // AND A FLUSH TAKES EVERYTHING OF ITS OWN. Of every kind: orders, instruments, currencies, runtimeModule state
            // and general entries, which on a store holding a table per shape is four deletes per key.
            db.Value.Flush();
            Assert.Empty(db.Value.LoadOrders());
            Assert.Empty(db.Value.LoadInstruments());
            Assert.Empty(db.Value.LoadCurrencies());
            Assert.Null(db.Value.LoadRuntimeModuleState(runtimeModule));
            Assert.Null(db.Value.Get("something"));
        }
    }

    /// <summary>Disposes a store that has something to dispose, and leaves a double alone.</summary>
    private sealed class Disposing : IDisposable
    {
        public Disposing(ICacheDatabase value) => Value = value;

        public ICacheDatabase Value { get; }

        public void Dispose() => (Value as IDisposable)?.Dispose();
    }
}
