using Bytex.Core.Caching;
using Bytex.Core.Model;
using Bytex.Core.Model.Accounts;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Positions;
using Bytex.Core.Model.Primitives;
using Bytex.Persistence.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Order = Bytex.Core.Model.Orders.Order;

namespace Bytex.Persistence.Redis;

public sealed record RedisCacheConfig
{
    public string ConnectionString { get; init; } = "localhost:6379";

    public required string ModuleHostId { get; init; }

    public string KeyPrefix { get; init; } = "bytex";

    public int Database { get; init; }
}

/// <summary>
/// Stores engine state in Redis: orders and positions as event streams, accounts as their latest state,
/// instruments and currencies as JSON, plus runtimeModule state and general key/value entries.
///
/// <para>
/// WHAT is stored, and under which key, is <see cref="KeyValueCacheDatabase"/>'s - shared with every other backing
/// store, so that two of them cannot come to disagree about how an order is rebuilt. What is here is the Redis of
/// it: the connection, the prefix this node writes under, and the ten operations <see cref="RedisStore"/> serves.
/// </para>
/// </summary>
public sealed class RedisCacheDatabase : ICacheDatabase, IDisposable
{
    private readonly ConnectionMultiplexer? _connection;
    private readonly KeyValueCacheDatabase _store;

    public RedisCacheDatabase(RedisCacheConfig config, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        _connection = ConnectionMultiplexer.Connect(config.ConnectionString);
        _store = Build(config, new RedisStore(_connection.GetDatabase(config.Database)), loggerFactory);
    }

    internal RedisCacheDatabase(RedisCacheConfig config, IRedisStore store, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        _store = Build(config, store ?? throw new ArgumentNullException(nameof(store)), loggerFactory);
    }

    private static KeyValueCacheDatabase Build(RedisCacheConfig config, IKeyValueStore store, ILoggerFactory? loggerFactory) =>
        new(
            store,
            $"{config.KeyPrefix}:{config.ModuleHostId}",
            loggerFactory?.CreateLogger<RedisCacheDatabase>() ?? NullLogger<RedisCacheDatabase>.Instance);

    public void Flush() => _store.Flush();

    public IReadOnlyList<Currency> LoadCurrencies() => _store.LoadCurrencies();

    public IReadOnlyList<Instrument> LoadInstruments() => _store.LoadInstruments();

    public IReadOnlyList<Account> LoadAccounts() => _store.LoadAccounts();

    public IReadOnlyList<Order> LoadOrders() => _store.LoadOrders();

    public IReadOnlyList<Position> LoadPositions(IReadOnlyDictionary<MarketKey, Instrument> instruments) =>
        _store.LoadPositions(instruments);

    public IReadOnlyList<OrderList> LoadOrderLists(IReadOnlyDictionary<ClientOrderId, Order> orders) =>
        _store.LoadOrderLists(orders);

    public IDictionary<string, byte[]>? LoadRuntimeModuleState(RuntimeModuleId runtimeModuleId) => _store.LoadRuntimeModuleState(runtimeModuleId);

    public void AddCurrency(Currency currency) => _store.AddCurrency(currency);

    public void AddInstrument(Instrument instrument) => _store.AddInstrument(instrument);

    public void AddAccount(Account account) => _store.AddAccount(account);

    public void AddOrder(Order order) => _store.AddOrder(order);

    public void AddPosition(Position position) => _store.AddPosition(position);

    public void AddOrderList(OrderList list) => _store.AddOrderList(list);

    public void UpdateAccount(Account account) => _store.UpdateAccount(account);

    public void UpdateOrder(Order order) => _store.UpdateOrder(order);

    public void UpdatePosition(Position position) => _store.UpdatePosition(position);

    public void SaveRuntimeModuleState(RuntimeModuleId runtimeModuleId, IDictionary<string, byte[]> state) => _store.SaveRuntimeModuleState(runtimeModuleId, state);

    public void Add(string key, byte[] value) => _store.Add(key, value);

    public byte[]? Get(string key) => _store.Get(key);

    public void Dispose() => _connection?.Dispose();
}

/// <summary>
/// Serializes order and account events with a type discriminator so they can be replayed.
///
/// <para>
/// The implementation moved beside the store logic when a second backing store was added, so that both hold events
/// the same way; this is where it has always been published from and it forwards there.
/// </para>
/// </summary>
public static class EventSerializer
{
    public static string Serialize(Event e) => Shared.EventSerializer.Serialize(e);

    public static OrderEvent? DeserializeOrderEvent(string json) => Shared.EventSerializer.DeserializeOrderEvent(json);

    public static AccountState? DeserializeAccountState(string json) => Shared.EventSerializer.DeserializeAccountState(json);
}
