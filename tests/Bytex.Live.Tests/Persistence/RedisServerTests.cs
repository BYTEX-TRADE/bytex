using System.Text;
using Bytex.Core.Model;
using Bytex.Core.Caching;
using Bytex.Core.Messaging;
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
using StackExchange.Redis;
using Order = Bytex.Core.Model.Orders.Order;

namespace Bytex.Live.Tests.Persistence;

// Why: everything else in this suite runs against a dictionary, which is the right place to check what the store
// decides and the wrong place to learn anything about Redis. Here a node's state goes to a real server and is read
// back by a second store over a second connection - the way it is read back after a restart, which is the only reading
// that matters. What this catches and a double cannot: a value that survives the client's own conversions, a list that
// keeps its order, a set that does not, a stream that trims itself, and a key layout that two moduleHosts really do not
// share.
//
// Skipped where there is no server. See RedisServer for why skipped and not quietly passed.
public sealed class RedisServerTests : IDisposable
{
    private readonly string _prefix = RedisServer.Prefix();
    private readonly List<RedisCacheDatabase> _stores = new();
    private ConnectionMultiplexer? _reader;

    private static readonly ModuleHostId _moduleHost = new("TESTER-001");
    private static readonly StrategyId _strategy = new("EmaCross-001");
    private static readonly AccountId _accountId = new("SIM-001");
    private static readonly VenueOrderId _venueOrderId = new("V-1");
    private static readonly UnixNanos _ts = UnixNanos.FromSeconds(1_700_000_000);

    private RedisCacheConfig Config(string moduleHost = "TESTER-001") =>
        new() { ModuleHostId = moduleHost, KeyPrefix = _prefix, ConnectionString = RedisServer.ConnectionString };

    /// <summary>A store on its own connection, as a restarted node would open one.</summary>
    private RedisCacheDatabase Store(string moduleHost = "TESTER-001")
    {
        RedisCacheDatabase store = new(Config(moduleHost));
        _stores.Add(store);
        return store;
    }

    private IDatabase Reader()
    {
        _reader ??= ConnectionMultiplexer.Connect(RedisServer.ConnectionString);
        return _reader.GetDatabase();
    }

    public void Dispose()
    {
        // Every test leaves the server as it found it, so a developer's own Redis is not slowly filled with test state.
        foreach (RedisCacheDatabase store in _stores)
        {
            try
            {
                store.Flush();
            }
            catch (RedisException)
            {
                // Nothing to clean up if the server went away mid-test.
            }

            store.Dispose();
        }

        _reader?.Dispose();
    }

    private static Instrument Instrument() => TestInstruments.BtcUsdt("SIM");

    private static MarketOrder Submitted(Instrument instrument, string clientOrderId)
    {
        OrderFactory factory = new(_moduleHost, _strategy, new TestClock(_ts));
        MarketOrder order = factory.Market(instrument.Id, OrderSide.Buy, instrument.MakeQuantity(0.5m), clientOrderId: new ClientOrderId(clientOrderId));
        order.Apply(new OrderSubmitted(_moduleHost, _strategy, instrument.Id, order.ClientOrderId, _accountId, Guid.NewGuid(), _ts, _ts));
        return order;
    }

    private static OrderFilled Fill(Instrument instrument, Order order) =>
        new(_moduleHost, _strategy, instrument.Id, order.ClientOrderId, _venueOrderId, _accountId, new TradeId("T-1"), new PositionId("P-1"), OrderSide.Buy, OrderType.Market,
            instrument.MakeQuantity(0.5m), instrument.MakePrice(50_000m), Currencies.USDT, new Money(25m, Currencies.USDT), LiquiditySide.Taker, Guid.NewGuid(), _ts, _ts);

    [RedisFact]
    public void A_nodes_state_written_to_a_real_server_is_read_back_by_a_second_connection()
    {
        Instrument instrument = Instrument();
        Currency token = new("USDX", 6, 0, "Six-place token", CurrencyType.Crypto);
        MarketOrder order = Submitted(instrument, "O-1");
        order.Apply(new OrderAccepted(_moduleHost, _strategy, instrument.Id, order.ClientOrderId, _venueOrderId, _accountId, Guid.NewGuid(), _ts, _ts));
        OrderFilled fill = Fill(instrument, order);
        order.Apply(fill);
        Position position = new(instrument, fill);
        CashAccount account = new(new AccountState(_accountId, AccountType.Cash, null, true,
            [AccountBalance.Of(new Money(1_000m, Currencies.USDT), new Money(0m, Currencies.USDT))], [],
            new Dictionary<string, string>(StringComparer.Ordinal), Guid.NewGuid(), _ts, _ts));

        RedisCacheDatabase writer = Store();
        writer.AddCurrency(token);
        writer.AddInstrument(instrument);
        writer.AddAccount(account);
        writer.AddOrder(order);
        writer.AddPosition(position);
        writer.AddOrderList(new OrderList(new OrderListId("OL-1"), [order]));
        writer.Add("order-id-counter", [0x00, 0x7f, 0xff]);

        // A second store on a second connection: what a restarted node does.
        RedisCacheDatabase restarted = Store();

        Currency currencyBack = Assert.Single(restarted.LoadCurrencies());
        Assert.Equal("USDX", currencyBack.Code);
        // The precision is the reason currencies are stored at all: read back wrong, every amount in this currency
        // rounds in the wrong place, and nothing reports an error.
        Assert.Equal(6, currencyBack.Precision);

        Instrument instrumentBack = Assert.Single(restarted.LoadInstruments());
        Assert.Equal(instrument.Id, instrumentBack.Id);
        Assert.Equal(instrument.PriceIncrement, instrumentBack.PriceIncrement);

        Order orderBack = Assert.Single(restarted.LoadOrders());
        Assert.Equal(order.ClientOrderId, orderBack.ClientOrderId);
        Assert.Equal(OrderStatus.Filled, orderBack.Status);
        Assert.Equal(order.FilledQuantity, orderBack.FilledQuantity);
        Assert.Equal(order.AvgPx, orderBack.AvgPx);

        Account accountBack = Assert.Single(restarted.LoadAccounts());
        Assert.Equal(new Money(1_000m, Currencies.USDT), accountBack.BalanceTotal(Currencies.USDT));

        Position positionBack = Assert.Single(restarted.LoadPositions(new Dictionary<MarketKey, Instrument> { [instrument.Id] = instrumentBack }));
        Assert.Equal(position.Quantity, positionBack.Quantity);
        Assert.Equal(position.AvgPxOpen, positionBack.AvgPxOpen);

        OrderList listBack = Assert.Single(restarted.LoadOrderLists(new Dictionary<ClientOrderId, Order> { [orderBack.ClientOrderId] = orderBack }));
        Assert.Same(orderBack, Assert.Single(listBack.Orders));

        Assert.Equal(new byte[] { 0x00, 0x7f, 0xff }, restarted.Get("order-id-counter"));
    }

    [RedisFact]
    public void An_event_stream_on_the_server_keeps_the_order_the_events_happened_in()
    {
        // The rebuild depends on it entirely: the same events in another order are another order - one accepted after
        // it was filled, or filled twice. A list is the one Redis structure that promises this, which is why orders are
        // kept in one.
        Instrument instrument = Instrument();
        MarketOrder order = Submitted(instrument, "O-1");
        order.Apply(new OrderAccepted(_moduleHost, _strategy, instrument.Id, order.ClientOrderId, _venueOrderId, _accountId, Guid.NewGuid(), _ts, _ts));
        order.Apply(Fill(instrument, order));
        RedisCacheDatabase writer = Store();

        writer.AddOrder(order);

        RedisValue[] stored = Reader().ListRange($"{_prefix}:TESTER-001:orders:O-1");

        Assert.Equal(order.Events.Count, stored.Length);
        Assert.Contains(nameof(OrderInitialized), (string)stored[0]!, StringComparison.Ordinal);
        Assert.Contains(nameof(OrderFilled), (string)stored[^1]!, StringComparison.Ordinal);
    }

    [RedisFact]
    public void An_update_appends_only_the_events_the_server_has_not_got()
    {
        // Rewriting the stream on every update would cost the whole history per event, and appending what is already
        // there would replay a fill twice on the next load.
        Instrument instrument = Instrument();
        MarketOrder order = Submitted(instrument, "O-1");
        RedisCacheDatabase writer = Store();
        writer.AddOrder(order);
        long afterAdd = Reader().ListLength($"{_prefix}:TESTER-001:orders:O-1");

        writer.UpdateOrder(order);
        Assert.Equal(afterAdd, Reader().ListLength($"{_prefix}:TESTER-001:orders:O-1"));

        order.Apply(new OrderAccepted(_moduleHost, _strategy, instrument.Id, order.ClientOrderId, _venueOrderId, _accountId, Guid.NewGuid(), _ts, _ts));
        writer.UpdateOrder(order);

        Assert.Equal(afterAdd + 1, Reader().ListLength($"{_prefix}:TESTER-001:orders:O-1"));
        Assert.Equal(OrderStatus.Accepted, Assert.Single(Store().LoadOrders()).Status);
    }

    [RedisFact]
    public void Two_traders_on_one_server_do_not_see_each_others_state()
    {
        Instrument instrument = Instrument();
        RedisCacheDatabase mine = Store();
        RedisCacheDatabase theirs = Store("OTHER-001");

        mine.AddInstrument(instrument);
        mine.AddOrder(Submitted(instrument, "O-1"));

        Assert.Single(mine.LoadInstruments());
        Assert.Empty(theirs.LoadInstruments());
        Assert.Empty(theirs.LoadOrders());
    }

    [RedisFact]
    public void Flushing_leaves_nothing_of_this_moduleHost_on_the_server()
    {
        Instrument instrument = Instrument();
        RedisCacheDatabase store = Store();
        store.AddCurrency(Currencies.USDT);
        store.AddInstrument(instrument);
        store.AddOrder(Submitted(instrument, "O-1"));
        store.SaveRuntimeModuleState(new RuntimeModuleId("Watcher-001"), new Dictionary<string, byte[]> { ["seen"] = [1] });
        store.Add("counter", [2]);
        store.AddOrderList(new OrderList(new OrderListId("OL-1"), [Submitted(instrument, "O-1")]));

        store.Flush();

        // Asked of the server rather than of the store, because a key the store no longer looks at is still a key.
        Assert.Empty(Reader().Multiplexer.GetServers()[0].Keys(pattern: $"{_prefix}:*"));
    }

    // ----- the bus on a real server -----

    [RedisFact]
    public async Task A_message_published_to_a_real_stream_is_there_with_its_type_and_its_payload()
    {
        MessageBus bus = new(_moduleHost);
        RedisBusStreamConfig config = new()
        {
            ModuleHostId = "TESTER-001",
            KeyPrefix = _prefix,
            ConnectionString = RedisServer.ConnectionString,
            Topics = ["data.quotes"],
        };

        await using (RedisBusStream stream = new(config, bus))
        {
            bus.Publish("data.quotes", new Noted("halted"));
            await Eventually(() => stream.Published == 1);
        }

        StreamEntry[] entries = Reader().StreamRange($"{_prefix}:TESTER-001:stream:data.quotes");
        StreamEntry entry = Assert.Single(entries);

        Assert.Equal("data.quotes", (string)entry["topic"]!);
        Assert.Equal(RedisBusStream.JsonEncoding, (string)entry["encoding"]!);
        Assert.Contains(nameof(Noted), (string)entry["type"]!, StringComparison.Ordinal);
        Assert.Contains("halted", Encoding.UTF8.GetString((byte[])entry["payload"]!), StringComparison.Ordinal);

        Reader().KeyDelete($"{_prefix}:TESTER-001:stream:data.quotes");
    }

    [RedisFact]
    public async Task A_stream_nobody_reads_is_trimmed_by_the_server_rather_than_growing_without_limit()
    {
        // The server trims to whole nodes, so this asserts a BOUND and not a count. A test demanding an exact length
        // would be testing a promise Redis does not make, and would fail on a server configured differently.
        const int maxLength = 100;
        MessageBus bus = new(_moduleHost);
        RedisBusStreamConfig config = new()
        {
            ModuleHostId = "TESTER-001",
            KeyPrefix = _prefix,
            ConnectionString = RedisServer.ConnectionString,
            Topics = ["events"],
            MaxLength = maxLength,
        };

        await using (RedisBusStream stream = new(config, bus))
        {
            for (int i = 0; i < 1_000; i++)
            {
                bus.Publish("events", new Noted($"note-{i}"));
            }

            await Eventually(() => stream.Published + stream.Dropped == 1_000);
        }

        long length = Reader().StreamLength($"{_prefix}:TESTER-001:stream:events");

        Assert.True(length < 1_000, $"a stream told to keep about {maxLength} entries kept {length} of 1000");
        Assert.True(length > 0, "trimming must keep the newest entries rather than emptying the stream");

        Reader().KeyDelete($"{_prefix}:TESTER-001:stream:events");
    }

    private sealed record Noted(string Text);

    private static async Task Eventually(Func<bool> until)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (until())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }

        Assert.Fail("the condition never came about");
    }
}
