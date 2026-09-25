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
using StackExchange.Redis;
using Order = Bytex.Core.Model.Orders.Order;

namespace Bytex.Persistence.Redis;

public sealed record RedisCacheConfig
{
    public string ConnectionString { get; init; } = "localhost:6379";

    public required string TraderId { get; init; }

    public string KeyPrefix { get; init; } = "bytex";

    public int Database { get; init; }
}

/// <summary>
/// Stores engine state in Redis: orders and positions as event streams, accounts as their latest state,
/// instruments and currencies as JSON, plus actor state and general key/value entries.
/// </summary>
public sealed class RedisCacheDatabase : ICacheDatabase, IDisposable
{
    private readonly RedisCacheConfig _config;
    private readonly ConnectionMultiplexer? _connection;
    private readonly IRedisStore _db;
    private readonly ILogger _log;
    private readonly string _prefix;

    public RedisCacheDatabase(RedisCacheConfig config, ILoggerFactory? loggerFactory = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _connection = ConnectionMultiplexer.Connect(config.ConnectionString);
        _db = new RedisStore(_connection.GetDatabase(config.Database));
        _log = loggerFactory?.CreateLogger<RedisCacheDatabase>() ?? NullLogger<RedisCacheDatabase>.Instance;
        _prefix = $"{config.KeyPrefix}:{config.TraderId}";
    }

    internal RedisCacheDatabase(RedisCacheConfig config, IRedisStore store, ILoggerFactory? loggerFactory = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _db = store ?? throw new ArgumentNullException(nameof(store));
        _log = loggerFactory?.CreateLogger<RedisCacheDatabase>() ?? NullLogger<RedisCacheDatabase>.Instance;
        _prefix = $"{config.KeyPrefix}:{config.TraderId}";
    }

    private string Key(string kind, string id) => $"{_prefix}:{kind}:{id}";

    private string IndexKey(string kind) => $"{_prefix}:index:{kind}";

    public void Flush()
    {
        foreach (string kind in new[] { "orders", "positions", "accounts", "instruments", "currencies", "actors", "general" })
        {
            foreach (string id in _db.SetMembers(IndexKey(kind)))
            {
                _db.KeyDelete(Key(kind, id));
            }

            _db.KeyDelete(IndexKey(kind));
        }

        _log.LogInformation("Flushed Redis state for {Prefix}", _prefix);
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
                    _log.LogError(e, "Failed to rebuild order {OrderId} from Redis", id);
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

    public IReadOnlyList<Position> LoadPositions(IReadOnlyDictionary<InstrumentId, Instrument> instruments)
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
                    if (!instruments.TryGetValue(fill.InstrumentId, out Instrument? instrument))
                    {
                        _log.LogWarning("Cannot rebuild position {PositionId}: instrument {InstrumentId} missing", id, fill.InstrumentId);
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

    // ----- Actor state and general -----

    public IDictionary<string, byte[]>? LoadActorState(ActorId actorId)
    {
        IReadOnlyDictionary<string, byte[]> entries = _db.HashGetAll(Key("actors", actorId.Value));
        return entries.Count == 0 ? null : new Dictionary<string, byte[]>(entries, StringComparer.Ordinal);
    }

    public void SaveActorState(ActorId actorId, IDictionary<string, byte[]> state)
    {
        ArgumentNullException.ThrowIfNull(state);
        string key = Key("actors", actorId.Value);
        _db.KeyDelete(key);
        if (state.Count > 0)
        {
            _db.HashSet(key, new Dictionary<string, byte[]>(state, StringComparer.Ordinal));
        }

        _db.SetAdd(IndexKey("actors"), actorId.Value);
    }

    public void Add(string key, byte[] value)
    {
        _db.StringSet(Key("general", key), value);
        _db.SetAdd(IndexKey("general"), key);
    }

    public byte[]? Get(string key) => _db.StringGet(Key("general", key));

    public void Dispose() => _connection?.Dispose();

    private void Write(string key, string json) => _db.StringSet(key, Encoding.UTF8.GetBytes(json));

    private static string? Text(byte[]? value) => value is null ? null : Encoding.UTF8.GetString(value);

    private sealed record CurrencyDto(string Code, byte Precision, ushort IsoCode, string Name, CurrencyType Type);
}

/// <summary>
/// Serializes order and account events with a type discriminator so they can be replayed.
/// </summary>
public static class EventSerializer
{
    private static readonly JsonSerializerOptions Options = new(BytexJson.Options) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private static readonly Dictionary<string, Type> OrderEventTypes = new(StringComparer.Ordinal)
    {
        [nameof(OrderInitialized)] = typeof(OrderInitialized),
        [nameof(OrderDenied)] = typeof(OrderDenied),
        [nameof(OrderEmulated)] = typeof(OrderEmulated),
        [nameof(OrderReleased)] = typeof(OrderReleased),
        [nameof(OrderSubmitted)] = typeof(OrderSubmitted),
        [nameof(OrderAccepted)] = typeof(OrderAccepted),
        [nameof(OrderRejected)] = typeof(OrderRejected),
        [nameof(OrderCanceled)] = typeof(OrderCanceled),
        [nameof(OrderExpired)] = typeof(OrderExpired),
        [nameof(OrderTriggered)] = typeof(OrderTriggered),
        [nameof(OrderPendingUpdate)] = typeof(OrderPendingUpdate),
        [nameof(OrderPendingCancel)] = typeof(OrderPendingCancel),
        [nameof(OrderModifyRejected)] = typeof(OrderModifyRejected),
        [nameof(OrderCancelRejected)] = typeof(OrderCancelRejected),
        [nameof(OrderUpdated)] = typeof(OrderUpdated),
        [nameof(OrderFilled)] = typeof(OrderFilled),
    };

    public static string Serialize(Event e)
    {
        ArgumentNullException.ThrowIfNull(e);
        Envelope envelope = new(e.GetType().Name, JsonSerializer.SerializeToElement(e, e.GetType(), Options));
        return JsonSerializer.Serialize(envelope, Options);
    }

    public static OrderEvent? DeserializeOrderEvent(string json)
    {
        Envelope? envelope = JsonSerializer.Deserialize<Envelope>(json, Options);
        if (envelope is null || !OrderEventTypes.TryGetValue(envelope.Type, out Type? type))
        {
            return null;
        }

        return (OrderEvent?)JsonSerializer.Deserialize(envelope.Payload.GetRawText(), type, Options);
    }

    public static AccountState? DeserializeAccountState(string json)
    {
        Envelope? envelope = JsonSerializer.Deserialize<Envelope>(json, Options);
        if (envelope is null || envelope.Type != nameof(AccountState))
        {
            return null;
        }

        return JsonSerializer.Deserialize<AccountState>(envelope.Payload.GetRawText(), Options);
    }

    private sealed record Envelope(string Type, JsonElement Payload);
}
