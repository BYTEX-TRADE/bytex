using Bytex.Core.Caching;
using Bytex.Core.Model.Accounts;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Positions;
using Bytex.Core.Model.Primitives;
using Bytex.Persistence.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Order = Bytex.Core.Model.Orders.Order;

namespace Bytex.Persistence.Postgres;

public sealed record PostgresCacheConfig
{
    /// <summary>An ordinary Npgsql connection string: host, database, username, password.</summary>
    public string ConnectionString { get; init; } = "Host=localhost;Database=bytex";

    /// <summary>
    /// The moduleHost this node runs as. Every key is written under it, so two nodes can share one database and neither
    /// loads nor flushes the other's rows - which is not a courtesy but the difference between a resumed node and
    /// somebody else's orders.
    /// </summary>
    public required string ModuleHostId { get; init; }

    /// <summary>The first part of every key, ahead of the moduleHost id.</summary>
    public string KeyPrefix { get; init; } = "bytex";

    /// <summary>
    /// The prefix the four tables take. A database shared by two engines rather than two nodes wants two of these.
    /// </summary>
    public string TablePrefix { get; init; } = "bytex_state";

    /// <summary>
    /// False leaves the schema alone, for a deployment where a person or a migration tool owns it and the node's
    /// credentials cannot create a table.
    /// </summary>
    public bool CreateSchema { get; init; } = true;
}

/// <summary>
/// Stores engine state in PostgreSQL: orders and positions as event streams, accounts as their latest state,
/// instruments and currencies as JSON, plus runtimeModule state and general key/value entries.
///
/// <para>
/// WHAT is stored, and under which key, is <see cref="KeyValueCacheDatabase"/>'s - the same rule Redis is held to,
/// compiled into both, so the two cannot come to disagree about how an order is rebuilt. What is here is the
/// PostgreSQL of it: the connection, the prefix this node writes under, and the ten operations
/// <see cref="PostgresStore"/> serves over four tables.
/// </para>
///
/// <para>
/// <b>What this is for, and what it is not.</b> A backing store is how a node that stopped becomes the node that
/// starts. It is not an archive of everything that happened - the node's journal is that - and it is not a cache
/// two nodes share: each node keeps its own, and pointing two of them at one database makes them durable, not
/// coherent. Two nodes CAN share a database, because every key carries the moduleHost id.
/// </para>
///
/// <para>
/// It holds runtimeModule and strategy state as Redis does, because the shape above it is the same. That is worth saying
/// because the obvious comparison does not: the engine this one is often measured against has a PostgreSQL cache
/// that does not persist runtimeModule or strategy state and documents the hole. Here there is no hole to document - the
/// hash a strategy's state is written to is one of the four tables.
/// </para>
/// </summary>
public sealed class PostgresCacheDatabase : ICacheDatabase, IDisposable
{
    private readonly PostgresStore? _owned;
    private readonly KeyValueCacheDatabase _store;

    public PostgresCacheDatabase(PostgresCacheConfig config, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        _owned = new PostgresStore(NpgsqlDataSource.Create(config.ConnectionString), config.TablePrefix);
        if (config.CreateSchema)
        {
            _owned.EnsureSchema();
        }

        _store = Build(config, _owned, loggerFactory);
    }

    internal PostgresCacheDatabase(PostgresCacheConfig config, IKeyValueStore store, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        _store = Build(config, store ?? throw new ArgumentNullException(nameof(store)), loggerFactory);
    }

    private static KeyValueCacheDatabase Build(PostgresCacheConfig config, IKeyValueStore store, ILoggerFactory? loggerFactory) =>
        new(
            store,
            $"{config.KeyPrefix}:{config.ModuleHostId}",
            loggerFactory?.CreateLogger<PostgresCacheDatabase>() ?? NullLogger<PostgresCacheDatabase>.Instance);

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

    public void Dispose() => _owned?.Dispose();
}
