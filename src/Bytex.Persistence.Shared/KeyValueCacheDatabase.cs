using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bytex.Core.Caching;
using Bytex.Core.Model;
using Bytex.Core.Model.Accounts;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Positions;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Serialization;
using Bytex.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Order = Bytex.Core.Model.Orders.Order;

namespace Bytex.Persistence.Shared;

/// <summary>
/// Engine state in a key/value store: orders and positions as event streams, accounts as their latest state,
/// instruments and currencies as JSON, plus runtimeModule state and general key/value entries.
///
/// <para>
/// <b>This is the whole store, and none of it is a database.</b> It reads and writes through
/// <see cref="IKeyValueStore"/> - ten operations - so what decides whether a resumed node is the node that
/// stopped lives here once: the key layout, the indexes that make a reload possible at all, and the rebuilding of
/// orders from their events and of order lists from ids. A backend supplies the ten operations and nothing else.
/// </para>
///
/// <para>
/// It belonged to one backend until a second was asked for. Copying it would have been the cheaper hour and the
/// wrong one: an order-list rebuild that is right in one copy and stale in the other is a bracket missing a leg on
/// exactly one of the two stores, which no test would think to compare. It is compiled into each persistence
/// library rather than published as a library of its own, so a deployment that wants nothing to do with one
/// backend does not acquire its client in order to get this.
/// </para>
///
/// <para>
/// Every key is prefixed with the moduleHost the node runs as, so two nodes can share one database without either
/// loading or flushing the other's rows. The backend honours that by construction rather than by remembering it.
/// </para>
/// </summary>
internal sealed class KeyValueCacheDatabase : ICacheDatabase
{
    private readonly IKeyValueStore _db;
    private readonly ILogger _log;
    private readonly string _prefix;

    /// <param name="store">The ten operations, on whatever holds them.</param>
    /// <param name="prefix">Everything this node writes lives under it, and it carries the moduleHost id.</param>
    /// <param name="log">The backend's own logger, so a message says which store answered.</param>
    public KeyValueCacheDatabase(IKeyValueStore store, string prefix, ILogger log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        _db = store ?? throw new ArgumentNullException(nameof(store));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _prefix = prefix;
    }

    private string Key(string kind, string id) => $"{_prefix}:{kind}:{id}";

    private string IndexKey(string kind) => $"{_prefix}:index:{kind}";

    public void Flush()
    {
        foreach (string kind in new[] { "orders", "positions", "accounts", "instruments", "currencies", "runtimeModules", "general", "orderlists" })
        {
            foreach (string id in _db.SetMembers(IndexKey(kind)))
            {
                _db.KeyDelete(Key(kind, id));
            }

            _db.KeyDelete(IndexKey(kind));
        }

        _log.LogInformation("Flushed stored state for {Prefix}", _prefix);
    }

    // ----- Currencies -----

    public IReadOnlyList<Currency> LoadCurrencies()
    {
        List<Currency> result = new();
        foreach (string id in _db.SetMembers(IndexKey("currencies")))
        {
            string? json = Text(_db.StringGet(Key("currencies", id)));
            if (json is not null && JsonSerializer.Deserialize<CurrencyDto>(json, BytexJson.Options) is { } dto)
            {
                result.Add(new Currency(dto.Code, dto.Precision, dto.IsoCode, dto.Name, dto.Type));
            }
        }

        return result;
    }

    public void AddCurrency(Currency currency)
    {
        ArgumentNullException.ThrowIfNull(currency);
        Write(Key("currencies", currency.Code), JsonSerializer.Serialize(new CurrencyDto(currency.Code, currency.Precision, currency.IsoCode, currency.Name, currency.Type), BytexJson.Options));
        _db.SetAdd(IndexKey("currencies"), currency.Code);
    }

    // ----- Instruments -----

    public IReadOnlyList<Instrument> LoadInstruments()
    {
        List<Instrument> result = new();
        foreach (string id in _db.SetMembers(IndexKey("instruments")))
        {
            if (Text(_db.StringGet(Key("instruments", id))) is { } json)
            {
                result.Add(InstrumentJson.Deserialize(json));
            }
        }

        return result;
    }

    public void AddInstrument(Instrument instrument)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        Write(Key("instruments", instrument.Id.Value), InstrumentJson.Serialize(instrument));
        _db.SetAdd(IndexKey("instruments"), instrument.Id.Value);
    }

    // ----- Accounts -----

    public IReadOnlyList<Account> LoadAccounts()
    {
        List<Account> result = new();
        foreach (string id in _db.SetMembers(IndexKey("accounts")))
        {
            IReadOnlyList<string> events = _db.ListRange(Key("accounts", id));
            Account? account = null;
            foreach (string json in events)
            {
                AccountState? state = EventSerializer.DeserializeAccountState(json);
                if (state is null)
                {
                    continue;
                }

                if (account is null)
                {
                    account = state.AccountType == AccountType.Margin ? new MarginAccount(state) : new CashAccount(state);
                }
                else
                {
                    account.Apply(state);
                }
            }

            if (account is not null)
            {
                result.Add(account);
            }
        }

        return result;
    }

    public void AddAccount(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        string key = Key("accounts", account.Id.Value);
        _db.KeyDelete(key);
        foreach (AccountState state in account.Events)
        {
            _db.ListRightPush(key, EventSerializer.Serialize(state));
        }

        _db.SetAdd(IndexKey("accounts"), account.Id.Value);
    }

    public void UpdateAccount(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        _db.ListRightPush(Key("accounts", account.Id.Value), EventSerializer.Serialize(account.LastEvent));
        _db.SetAdd(IndexKey("accounts"), account.Id.Value);
    }

    // ----- Orders -----

    public IReadOnlyList<Order> LoadOrders()
    {
        List<Order> result = new();
        foreach (string id in _db.SetMembers(IndexKey("orders")))
        {
            IReadOnlyList<string> events = _db.ListRange(Key("orders", id));
            List<OrderEvent> list = new();
            foreach (string json in events)
            {
                OrderEvent? e = EventSerializer.DeserializeOrderEvent(json);
                if (e is not null)
                {
                    list.Add(e);
                }
            }

            if (list.Count > 0 && list[0] is OrderInitialized)
            {
                try
                {
                    result.Add(OrderUnpacker.FromEvents(list));
                }
                catch (Exception e)
                {
                    _log.LogError(e, "Failed to rebuild order {OrderId} from the store", id);
                }
            }
        }

        return result;
    }

    public void AddOrder(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);
        string key = Key("orders", order.ClientOrderId.Value);
        _db.KeyDelete(key);
        foreach (OrderEvent e in order.Events)
        {
            _db.ListRightPush(key, EventSerializer.Serialize(e));
        }

        _db.SetAdd(IndexKey("orders"), order.ClientOrderId.Value);
    }

    public void UpdateOrder(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);
        string key = Key("orders", order.ClientOrderId.Value);
        long stored = _db.ListLength(key);
        for (int i = (int)stored; i < order.EventCount; i++)
        {
            _db.ListRightPush(key, EventSerializer.Serialize(order.Events[i]));
        }

        _db.SetAdd(IndexKey("orders"), order.ClientOrderId.Value);
    }

    // ----- Positions -----

    public IReadOnlyList<Position> LoadPositions(IReadOnlyDictionary<MarketKey, Instrument> instruments)
    {
        ArgumentNullException.ThrowIfNull(instruments);
        List<Position> result = new();
        foreach (string id in _db.SetMembers(IndexKey("positions")))
        {
            IReadOnlyList<string> events = _db.ListRange(Key("positions", id));
            Position? position = null;
            foreach (string json in events)
            {
                if (EventSerializer.DeserializeOrderEvent(json) is not OrderFilled fill)
                {
                    continue;
                }

                if (position is null)
                {
                    if (!instruments.TryGetValue(fill.MarketKey, out Instrument? instrument))
                    {
                        _log.LogWarning("Cannot rebuild position {PositionId}: instrument {MarketKey} missing", id, fill.MarketKey);
                        break;
                    }

                    position = new Position(instrument, fill);
                }
                else
                {
                    position.Apply(fill);
                }
            }

            if (position is not null)
            {
                result.Add(position);
            }
        }

        return result;
    }

    public void AddPosition(Position position)
    {
        ArgumentNullException.ThrowIfNull(position);
        string key = Key("positions", position.Id.Value);
        _db.KeyDelete(key);
        foreach (OrderFilled fill in position.Events)
        {
            _db.ListRightPush(key, EventSerializer.Serialize(fill));
        }

        _db.SetAdd(IndexKey("positions"), position.Id.Value);
    }

    public void UpdatePosition(Position position)
    {
        ArgumentNullException.ThrowIfNull(position);
        string key = Key("positions", position.Id.Value);
        long stored = _db.ListLength(key);
        for (int i = (int)stored; i < position.EventCount; i++)
        {
            _db.ListRightPush(key, EventSerializer.Serialize(position.Events[i]));
        }

        _db.SetAdd(IndexKey("positions"), position.Id.Value);
    }

    // ----- Order lists -----

    public IReadOnlyList<OrderList> LoadOrderLists(IReadOnlyDictionary<ClientOrderId, Order> orders)
    {
        ArgumentNullException.ThrowIfNull(orders);
        List<OrderList> result = new();
        foreach (string id in _db.SetMembers(IndexKey("orderlists")))
        {
            if (Text(_db.StringGet(Key("orderlists", id))) is not { } json
                || JsonSerializer.Deserialize<OrderListDto>(json, BytexJson.Options) is not { } dto)
            {
                continue;
            }

            List<Order> members = new(dto.OrderIds.Count);
            foreach (string orderId in dto.OrderIds)
            {
                if (!orders.TryGetValue(new ClientOrderId(orderId), out Order? order))
                {
                    _log.LogWarning("Cannot rebuild order list {OrderListId}: order {ClientOrderId} is not loaded", id, orderId);
                    members.Clear();
                    break;
                }

                members.Add(order);
            }

            if (members.Count > 0)
            {
                result.Add(new OrderList(new OrderListId(dto.Id), members));
            }
        }

        return result;
    }

    public void AddOrderList(OrderList list)
    {
        ArgumentNullException.ThrowIfNull(list);

        // The ids, not the orders. The orders are stored once, under their own keys, and a list holding a second copy
        // of them would hand back orders that stopped agreeing with the cache's the first time one was filled.
        Write(
            Key("orderlists", list.Id.Value),
            JsonSerializer.Serialize(new OrderListDto(list.Id.Value, [.. list.Orders.Select(o => o.ClientOrderId.Value)]), BytexJson.Options));
        _db.SetAdd(IndexKey("orderlists"), list.Id.Value);
    }

    // ----- RuntimeModule state and general -----

    public IDictionary<string, byte[]>? LoadRuntimeModuleState(RuntimeModuleId runtimeModuleId)
    {
        IReadOnlyDictionary<string, byte[]> entries = _db.HashGetAll(Key("runtimeModules", runtimeModuleId.Value));
        return entries.Count == 0 ? null : new Dictionary<string, byte[]>(entries, StringComparer.Ordinal);
    }

    public void SaveRuntimeModuleState(RuntimeModuleId runtimeModuleId, IDictionary<string, byte[]> state)
    {
        ArgumentNullException.ThrowIfNull(state);
        string key = Key("runtimeModules", runtimeModuleId.Value);
        _db.KeyDelete(key);
        if (state.Count > 0)
        {
            _db.HashSet(key, new Dictionary<string, byte[]>(state, StringComparer.Ordinal));
        }

        _db.SetAdd(IndexKey("runtimeModules"), runtimeModuleId.Value);
    }

    public void Add(string key, byte[] value)
    {
        _db.StringSet(Key("general", key), value);
        _db.SetAdd(IndexKey("general"), key);
    }

    public byte[]? Get(string key) => _db.StringGet(Key("general", key));

    private void Write(string key, string json) => _db.StringSet(key, Encoding.UTF8.GetBytes(json));

    private static string? Text(byte[]? value) => value is null ? null : Encoding.UTF8.GetString(value);

    private sealed record CurrencyDto(string Code, byte Precision, ushort IsoCode, string Name, CurrencyType Type);

    private sealed record OrderListDto(string Id, IReadOnlyList<string> OrderIds);
}
