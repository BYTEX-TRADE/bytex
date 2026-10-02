using Bytex.Core.Model;
using Bytex.Core.Model.Accounts;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Positions;
using Bytex.Core.Model.Primitives;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bytex.Core.Caching;

public sealed record CacheConfig
{
    public int TickCapacity { get; init; } = 10_000;

    public int BarCapacity { get; init; } = 10_000;

    /// <summary>When true, order and position state is written through to the configured <see cref="ICacheDatabase"/>.</summary>
    public bool Persist { get; init; }

    /// <summary>Flush the database on engine reset.</summary>
    public bool FlushOnStart { get; init; }
}

/// <summary>
/// Optional durable backing store for cache state.
/// </summary>
public interface ICacheDatabase
{
    void Flush();

    IReadOnlyList<Currency> LoadCurrencies();

    IReadOnlyList<Instrument> LoadInstruments();

    IReadOnlyList<Account> LoadAccounts();

    IReadOnlyList<Order> LoadOrders();

    IReadOnlyList<Position> LoadPositions(IReadOnlyDictionary<MarketKey, Instrument> instruments);

    /// <summary>
    /// The order lists, rebuilt around the orders already loaded.
    ///
    /// <para>
    /// A list is stored as the ids of its orders rather than as the orders, because an order held twice is an order
    /// that drifts: the copy inside the list would never see the fill that reached the one in the cache. What is given
    /// here is what was loaded, and a list whose orders are not all among them is not returned - a bracket missing a
    /// leg would look like a complete one to everything that reads it afterwards.
    /// </para>
    /// </summary>
    IReadOnlyList<OrderList> LoadOrderLists(IReadOnlyDictionary<ClientOrderId, Order> orders);

    IDictionary<string, byte[]>? LoadRuntimeModuleState(RuntimeModuleId runtimeModuleId);

    void AddCurrency(Currency currency);

    void AddInstrument(Instrument instrument);

    void AddAccount(Account account);

    void AddOrder(Order order);

    void AddPosition(Position position);

    void AddOrderList(OrderList list);

    void UpdateAccount(Account account);

    void UpdateOrder(Order order);

    void UpdatePosition(Position position);

    void SaveRuntimeModuleState(RuntimeModuleId runtimeModuleId, IDictionary<string, byte[]> state);

    void Add(string key, byte[] value);

    byte[]? Get(string key);
}

/// <summary>
/// A bounded most-recent-first window of values.
/// </summary>
internal sealed class Window<T>
{
    private readonly T[] _buffer;
    private int _head;
    private int _count;

    public Window(int capacity)
    {
        _buffer = new T[Math.Max(1, capacity)];
    }

    public int Count => _count;

    public void Add(T item)
    {
        _buffer[_head] = item;
        _head = (_head + 1) % _buffer.Length;
        if (_count < _buffer.Length)
        {
            _count++;
        }
    }

    /// <summary>Index 0 is the most recently added item.</summary>
    public T this[int index]
    {
        get
        {
            if (index < 0 || index >= _count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            int physical = (_head - 1 - index + _buffer.Length * 2) % _buffer.Length;
            return _buffer[physical];
        }
    }

    public List<T> ToList()
    {
        List<T> list = new(_count);
        for (int i = 0; i < _count; i++)
        {
            list.Add(this[i]);
        }

        return list;
    }

    public void Clear()
    {
        _head = 0;
        _count = 0;
    }
}

/// <summary>
/// In-memory state store with secondary indexes over orders and positions.
/// </summary>
public sealed class Cache : ICache
{
    private readonly CacheConfig _config;
    private readonly ICacheDatabase? _database;
    private readonly ILogger _log;

    private readonly Dictionary<MarketKey, Instrument> _instruments = new();
    private readonly Dictionary<MarketKey, Window<QuoteTick>> _quotes = new();
    private readonly Dictionary<MarketKey, Window<TradeTick>> _trades = new();
    private readonly Dictionary<CandleSeries, Window<Bar>> _bars = new();
    private readonly Dictionary<MarketKey, OrderBook> _books = new();
    private readonly Dictionary<MarketKey, MarkPriceUpdate> _markPrices = new();
    private readonly Dictionary<MarketKey, IndexPriceUpdate> _indexPrices = new();
    private readonly Dictionary<MarketKey, FundingRateUpdate> _fundingRates = new();

    private readonly Dictionary<ClientOrderId, Order> _orders = new();
    private readonly Dictionary<VenueOrderId, ClientOrderId> _venueOrderIndex = new();
    private readonly Dictionary<OrderListId, OrderList> _orderLists = new();
    private readonly Dictionary<PositionId, Position> _positions = new();
    private readonly Dictionary<AccountId, Account> _accounts = new();
    private readonly Dictionary<Venue, AccountId> _venueAccounts = new();

    private readonly Dictionary<ClientOrderId, PositionId> _orderPosition = new();
    private readonly Dictionary<ClientOrderId, StrategyId> _orderStrategy = new();
    private readonly Dictionary<PositionId, StrategyId> _positionStrategy = new();
    private readonly Dictionary<PositionId, HashSet<ClientOrderId>> _positionOrders = new();
    private readonly Dictionary<OrderScheduleId, HashSet<ClientOrderId>> _orderScheduleOrders = new();
    private readonly Dictionary<ClientOrderId, HashSet<ClientOrderId>> _execSpawnOrders = new();

    private readonly HashSet<ClientOrderId> _ordersOpen = new();
    private readonly HashSet<ClientOrderId> _ordersClosed = new();
    private readonly HashSet<ClientOrderId> _ordersEmulated = new();
    private readonly HashSet<ClientOrderId> _ordersInflight = new();
    private readonly HashSet<ClientOrderId> _ordersPendingCancel = new();
    private readonly List<PositionSnapshot> _positionSnapshots = new();
    private readonly HashSet<PositionId> _positionsOpen = new();
    private readonly HashSet<PositionId> _positionsClosed = new();

    private readonly Dictionary<string, Currency> _persistedCurrencies = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _general = new(StringComparer.Ordinal);
    private readonly Dictionary<RuntimeModuleId, IDictionary<string, byte[]>> _runtimeModuleState = new();

    public Cache(CacheConfig? config = null, ICacheDatabase? database = null, ILoggerFactory? loggerFactory = null)
    {
        _config = config ?? new CacheConfig();
        _database = database;
        _log = loggerFactory?.CreateLogger<Cache>() ?? NullLogger<Cache>.Instance;
    }

    public CacheConfig Config => _config;

    // ----- Loading from database -----

    public void LoadFromDatabase()
    {
        if (_database is null)
        {
            return;
        }

        foreach (Currency currency in _database.LoadCurrencies())
        {
            Currency.Register(currency);
        }

        foreach (Instrument instrument in _database.LoadInstruments())
        {
            _instruments[instrument.Id] = instrument;
        }

        foreach (Account account in _database.LoadAccounts())
        {
            AddAccountInternal(account);
        }

        foreach (Order order in _database.LoadOrders())
        {
            AddOrderInternal(order, persist: false);
            UpdateOrderIndexes(order);
        }

        foreach (Position position in _database.LoadPositions(_instruments))
        {
            AddPositionInternal(position, persist: false);
            UpdatePositionIndexes(position);
        }

        // After the orders, because a list is rebuilt around them rather than carrying its own copies.
        foreach (OrderList list in _database.LoadOrderLists(_orders))
        {
            _orderLists[list.Id] = list;
        }

        _log.LogInformation("Loaded {Instruments} instruments, {Accounts} accounts, {Orders} orders, {Positions} positions, {OrderLists} order lists from database",
            _instruments.Count, _accounts.Count, _orders.Count, _positions.Count, _orderLists.Count);
    }

    public void Reset()
    {
        _quotes.Clear();
        _trades.Clear();
        _bars.Clear();
        _books.Clear();
        _markPrices.Clear();
        _indexPrices.Clear();
        _fundingRates.Clear();
        _orders.Clear();
        _venueOrderIndex.Clear();
        _orderLists.Clear();
        _positions.Clear();
        _accounts.Clear();
        _venueAccounts.Clear();
        _orderPosition.Clear();
        _orderStrategy.Clear();
        _positionStrategy.Clear();
        _positionOrders.Clear();
        _orderScheduleOrders.Clear();
        _execSpawnOrders.Clear();
        _ordersOpen.Clear();
        _ordersClosed.Clear();
        _ordersEmulated.Clear();
        _ordersInflight.Clear();
        _ordersPendingCancel.Clear();
        _positionsOpen.Clear();
        _positionsClosed.Clear();
        _positionSnapshots.Clear();
        _general.Clear();
        _runtimeModuleState.Clear();
    }

    public void Flush()
    {
        Reset();
        _instruments.Clear();
        _persistedCurrencies.Clear();
        _database?.Flush();
    }

    // ----- Currencies -----

    /// <summary>
    /// Registers a currency and, when persisting, stores it.
    ///
    /// <para>
    /// This exists because of what happens without it. Most currencies here are built in; a venue's own token is not,
    /// and an unknown code is not an error anywhere - <see cref="Currency.FromCode"/> invents one at the default
    /// precision of eight. So an instrument quoted in a 6dp stablecoin comes back after a restart quoted in an 8dp
    /// currency of the same name, and every amount in it rounds two places too far out, silently and forever.
    /// </para>
    /// </summary>
    public void AddCurrency(Currency currency)
    {
        ArgumentNullException.ThrowIfNull(currency);
        Currency.Register(currency);

        if (!_config.Persist)
        {
            return;
        }

        // Every instrument on a venue shares a handful of currencies, so a thousand instruments would otherwise be
        // several thousand writes of the same USDT. What is remembered is the DEFINITION, not the currency: two
        // currencies with one code are equal here, so a set of them would take a corrected precision for one already
        // stored and keep the wrong number.
        if (_persistedCurrencies.TryGetValue(currency.Code, out Currency? stored) && stored.IsSameDefinition(currency))
        {
            return;
        }

        _persistedCurrencies[currency.Code] = currency;
        _database?.AddCurrency(currency);
    }

    /// <summary>
    /// The currencies a record cannot be read back without. Written before the record itself, so that a store read in
    /// the order it was written never sees an instrument whose currency has not arrived yet.
    /// </summary>
    private void PersistCurrencies(Instrument instrument)
    {
        AddCurrency(instrument.QuoteCurrency);
        AddCurrency(instrument.SettlementCurrency);
        if (instrument.BaseCurrency is { } baseCurrency)
        {
            AddCurrency(baseCurrency);
        }
    }

    private void PersistCurrencies(Account account)
    {
        if (account.BaseCurrency is { } baseCurrency)
        {
            AddCurrency(baseCurrency);
        }

        foreach (Currency currency in account.Currencies)
        {
            AddCurrency(currency);
        }
    }

    // ----- Instruments -----

    public void AddInstrument(Instrument instrument)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        _instruments[instrument.Id] = instrument;
        if (_config.Persist)
        {
            PersistCurrencies(instrument);
            _database?.AddInstrument(instrument);
        }
    }

    public Instrument? Instrument(MarketKey id) => _instruments.GetValueOrDefault(id);

    public IReadOnlyList<Instrument> Instruments(Venue? venue = null) =>
        venue is null ? _instruments.Values.ToList() : _instruments.Values.Where(i => i.Venue == venue.Value).ToList();

    public IReadOnlyList<MarketKey> MarketKeys(Venue? venue = null) => Instruments(venue).Select(i => i.Id).ToList();

    // ----- Market data -----

    public void AddQuoteTick(QuoteTick tick)
    {
        if (!_quotes.TryGetValue(tick.MarketKey, out Window<QuoteTick>? window))
        {
            window = new Window<QuoteTick>(_config.TickCapacity);
            _quotes[tick.MarketKey] = window;
        }

        window.Add(tick);
    }

    public void AddQuoteTicks(IEnumerable<QuoteTick> ticks)
    {
        foreach (QuoteTick tick in ticks)
        {
            AddQuoteTick(tick);
        }
    }

    /// <summary>
    /// Where this node's resting orders stand in the queue at their price. The node's own view, kept from the data
    /// the node is given: a venue matches on a sequence it never shows anybody, so this is an estimate, and in a
    /// backtest it is fed the same data the simulated venue matches on and says the same thing.
    /// </summary>
    public OwnOrderBook OwnOrders { get; } = new();

    /// <summary>
    /// A trade moves every queue it touched. Past a resting order it clears the queue outright - the market could not
    /// have traded there otherwise - and at the price it serves the side the aggressor took it from.
    /// </summary>
    private void AdvanceQueues(TradeTick tick)
    {
        if (OwnOrders.Count == 0)
        {
            return;
        }

        OwnOrders.ClearThrough(tick.MarketKey, tick.Price.Value);
        if (tick.Aggressor != AggressorSide.Seller)
        {
            OwnOrders.Consume(tick.MarketKey, OrderSide.Sell, tick.Price.Value, tick.Size.Value);
        }

        if (tick.Aggressor != AggressorSide.Buyer)
        {
            OwnOrders.Consume(tick.MarketKey, OrderSide.Buy, tick.Price.Value, tick.Size.Value);
        }
    }

    /// <summary>
    /// Keeps the queue in step with one order: an order resting at a price stands in the queue there, one that has
    /// been repriced joins the back of the new one, and one that has stopped resting stands in none.
    /// </summary>
    private void TrackQueue(Order order)
    {
        decimal? price = order.IsOpen ? QueuedAt(order) : null;
        if (price is not { } resting)
        {
            OwnOrders.Left(order.ClientOrderId);
            return;
        }

        if (OwnOrders.PriceOf(order.ClientOrderId) != resting)
        {
            OwnOrders.Joined(order, resting, OrderBook(order.MarketKey), order.TsLast);
        }
    }

    /// <summary>
    /// The price an order is standing in a queue at, or null for one that is not standing in any: a stop still
    /// waiting for its trigger is a promise to the venue rather than size in the book, and a market order never
    /// rests at a price at all.
    /// </summary>
    private static decimal? QueuedAt(Order order) => order.Price is not { } price
        ? null
        : order.Type switch
        {
            OrderType.Limit or OrderType.MarketToLimit => price.Value,
            OrderType.StopLimit or OrderType.LimitIfTouched or OrderType.TrailingStopLimit => order.Status == OrderStatus.Triggered ? price.Value : null,
            _ => null,
        };

    public void AddTradeTick(TradeTick tick)
    {
        if (!_trades.TryGetValue(tick.MarketKey, out Window<TradeTick>? window))
        {
            window = new Window<TradeTick>(_config.TickCapacity);
            _trades[tick.MarketKey] = window;
        }

        window.Add(tick);
        AdvanceQueues(tick);
    }

    public void AddTradeTicks(IEnumerable<TradeTick> ticks)
    {
        foreach (TradeTick tick in ticks)
        {
            AddTradeTick(tick);
        }
    }

    public void AddBar(Bar bar)
    {
        if (!_bars.TryGetValue(bar.CandleSeries, out Window<Bar>? window))
        {
            window = new Window<Bar>(_config.BarCapacity);
            _bars[bar.CandleSeries] = window;
        }

        if (bar.IsRevision && window.Count > 0 && window[0].EventTime == bar.EventTime)
        {
            // Replace the most recent bar in place by rebuilding the window.
            List<Bar> existing = window.ToList();
            existing[0] = bar;
            window.Clear();
            for (int i = existing.Count - 1; i >= 0; i--)
            {
                window.Add(existing[i]);
            }

            return;
        }

        window.Add(bar);
    }

    public void AddBars(IEnumerable<Bar> bars)
    {
        foreach (Bar bar in bars)
        {
            AddBar(bar);
        }
    }

    public void AddOrderBook(OrderBook book)
    {
        ArgumentNullException.ThrowIfNull(book);
        _books[book.MarketKey] = book;
    }

    public OrderBook GetOrCreateOrderBook(MarketKey id, BookType bookType)
    {
        if (!_books.TryGetValue(id, out OrderBook? book))
        {
            book = new OrderBook(id, bookType);
            _books[id] = book;
        }

        return book;
    }

    public void AddMarkPrice(MarkPriceUpdate update) => _markPrices[update.MarketKey] = update;

    public void AddIndexPrice(IndexPriceUpdate update) => _indexPrices[update.MarketKey] = update;

    public void AddFundingRate(FundingRateUpdate update) => _fundingRates[update.MarketKey] = update;

    public QuoteTick? QuoteTick(MarketKey id, int index = 0) =>
        _quotes.TryGetValue(id, out Window<QuoteTick>? w) && index < w.Count ? w[index] : null;

    public IReadOnlyList<QuoteTick> QuoteTicks(MarketKey id) => _quotes.TryGetValue(id, out Window<QuoteTick>? w) ? w.ToList() : [];

    public int QuoteTickCount(MarketKey id) => _quotes.TryGetValue(id, out Window<QuoteTick>? w) ? w.Count : 0;

    public TradeTick? TradeTick(MarketKey id, int index = 0) =>
        _trades.TryGetValue(id, out Window<TradeTick>? w) && index < w.Count ? w[index] : null;

    public IReadOnlyList<TradeTick> TradeTicks(MarketKey id) => _trades.TryGetValue(id, out Window<TradeTick>? w) ? w.ToList() : [];

    public int TradeTickCount(MarketKey id) => _trades.TryGetValue(id, out Window<TradeTick>? w) ? w.Count : 0;

    public Bar? Bar(CandleSeries candleSeries, int index = 0) =>
        _bars.TryGetValue(candleSeries, out Window<Bar>? w) && index < w.Count ? w[index] : null;

    public IReadOnlyList<Bar> Bars(CandleSeries candleSeries) => _bars.TryGetValue(candleSeries, out Window<Bar>? w) ? w.ToList() : [];

    public int BarCount(CandleSeries candleSeries) => _bars.TryGetValue(candleSeries, out Window<Bar>? w) ? w.Count : 0;

    public IReadOnlyList<CandleSeries> CandleSeriesDefinitions(MarketKey? marketKey = null) =>
        marketKey is null ? _bars.Keys.ToList() : _bars.Keys.Where(b => b.MarketKey == marketKey.Value).ToList();

    public OrderBook? OrderBook(MarketKey id) => _books.GetValueOrDefault(id);

    public MarkPriceUpdate? MarkPrice(MarketKey id) => _markPrices.GetValueOrDefault(id);

    public IndexPriceUpdate? IndexPrice(MarketKey id) => _indexPrices.GetValueOrDefault(id);

    public FundingRateUpdate? FundingRate(MarketKey id) => _fundingRates.GetValueOrDefault(id);

    public bool HasQuoteTicks(MarketKey id) => QuoteTickCount(id) > 0;

    public bool HasTradeTicks(MarketKey id) => TradeTickCount(id) > 0;

    public bool HasBars(CandleSeries candleSeries) => BarCount(candleSeries) > 0;

    public Price? Price(MarketKey id, PriceType priceType)
    {
        switch (priceType)
        {
            case PriceType.Last:
                if (TradeTick(id) is { } trade)
                {
                    return trade.Price;
                }

                break;
            case PriceType.Mark:
                if (MarkPrice(id) is { } mark)
                {
                    return mark.Value;
                }

                break;
            default:
                if (QuoteTick(id) is { } quote)
                {
                    return quote.ExtractPrice(priceType);
                }

                break;
        }

        // Fall back to the most recent bar close for any price type.
        foreach (CandleSeries candleSeries in _bars.Keys)
        {
            if (candleSeries.MarketKey == id && _bars[candleSeries].Count > 0)
            {
                return _bars[candleSeries][0].Close;
            }
        }

        if (priceType != PriceType.Last && TradeTick(id) is { } lastTrade)
        {
            return lastTrade.Price;
        }

        return null;
    }

    public decimal? ExchangeRate(Currency from, Currency to, PriceType priceType = PriceType.Mid)
    {
        if (from.Equals(to))
        {
            return 1m;
        }

        // Direct or inverse pair on any venue.
        foreach (Instrument instrument in _instruments.Values)
        {
            if (instrument.BaseCurrency is null)
            {
                continue;
            }

            if (instrument.BaseCurrency.Equals(from) && instrument.QuoteCurrency.Equals(to) && Price(instrument.Id, priceType) is { } direct)
            {
                return direct.Value;
            }

            if (instrument.BaseCurrency.Equals(to) && instrument.QuoteCurrency.Equals(from) && Price(instrument.Id, priceType) is { } inverse && inverse.Value != 0m)
            {
                return 1m / inverse.Value;
            }
        }

        // One hop via a common quote currency.
        foreach (Instrument a in _instruments.Values)
        {
            if (a.BaseCurrency is null || !a.BaseCurrency.Equals(from))
            {
                continue;
            }

            foreach (Instrument b in _instruments.Values)
            {
                if (b.BaseCurrency is null || !b.BaseCurrency.Equals(to) || !b.QuoteCurrency.Equals(a.QuoteCurrency))
                {
                    continue;
                }

                Price? pa = Price(a.Id, priceType);
                Price? pb = Price(b.Id, priceType);
                if (pa is { } ra && pb is { } rb && rb.Value != 0m)
                {
                    return ra.Value / rb.Value;
                }
            }
        }

        return null;
    }

    // ----- Orders -----

    public void AddOrder(Order order, PositionId? positionId = null)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (_orders.ContainsKey(order.ClientOrderId))
        {
            throw new InvalidOperationException($"Order {order.ClientOrderId} already exists in the cache.");
        }

        AddOrderInternal(order, persist: _config.Persist);
        if (positionId is { } pid)
        {
            IndexOrderPosition(order.ClientOrderId, pid);
        }

        UpdateOrderIndexes(order);
    }

    /// <summary>
    /// Puts an order in the place of the one already held under the same id. This is how the execution engine releases
    /// an order it was holding locally: what goes to the venue is a market or a limit order where a stop was held, and
    /// it keeps the id its owner submitted, because everything that refers to that order - a position, a strategy's own
    /// record of it, the fill that is coming back - refers to it by that id. Nothing else in the cache moves.
    /// </summary>
    public void ReplaceOrder(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (!_orders.TryGetValue(order.ClientOrderId, out Order? existing))
        {
            throw new InvalidOperationException($"Order {order.ClientOrderId} is not in the cache.");
        }

        if (existing.PositionId is { } positionId && order.PositionId is null)
        {
            order.SetPosition(positionId);
        }

        AddOrderInternal(order, persist: _config.Persist);
        UpdateOrderIndexes(order);
    }

    private void AddOrderInternal(Order order, bool persist)
    {
        _orders[order.ClientOrderId] = order;
        _orderStrategy[order.ClientOrderId] = order.StrategyId;
        if (order.OrderScheduleId is { } alg)
        {
            if (!_orderScheduleOrders.TryGetValue(alg, out HashSet<ClientOrderId>? set))
            {
                set = new HashSet<ClientOrderId>();
                _orderScheduleOrders[alg] = set;
            }

            set.Add(order.ClientOrderId);
        }

        if (order.ExecSpawnId is { } spawn)
        {
            if (!_execSpawnOrders.TryGetValue(spawn, out HashSet<ClientOrderId>? set))
            {
                set = new HashSet<ClientOrderId>();
                _execSpawnOrders[spawn] = set;
            }

            set.Add(order.ClientOrderId);
        }

        if (order.PositionId is { } pid)
        {
            IndexOrderPosition(order.ClientOrderId, pid);
        }

        if (persist)
        {
            _database?.AddOrder(order);
        }
    }

    public void AddOrderList(OrderList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        _orderLists[list.Id] = list;
        if (_config.Persist)
        {
            _database?.AddOrderList(list);
        }
    }

    public void UpdateOrder(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (order.VenueOrderId is { } venueOrderId)
        {
            _venueOrderIndex[venueOrderId] = order.ClientOrderId;
        }

        if (order.PositionId is { } pid)
        {
            IndexOrderPosition(order.ClientOrderId, pid);
        }

        UpdateOrderIndexes(order);
        if (_config.Persist)
        {
            _database?.UpdateOrder(order);
        }
    }

    public void UpdateOrderPendingCancelLocal(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);
        _ordersPendingCancel.Add(order.ClientOrderId);
    }

    private void UpdateOrderIndexes(Order order)
    {
        ClientOrderId id = order.ClientOrderId;
        foreach (VenueOrderId venueOrderId in order.VenueOrderIds)
        {
            _venueOrderIndex[venueOrderId] = id;
        }

        if (order.IsEmulated)
        {
            _ordersEmulated.Add(id);
        }
        else
        {
            _ordersEmulated.Remove(id);
        }

        if (order.IsInflight)
        {
            _ordersInflight.Add(id);
        }
        else
        {
            _ordersInflight.Remove(id);
        }

        if (order.IsOpen)
        {
            _ordersOpen.Add(id);
            _ordersClosed.Remove(id);
        }
        else if (order.IsClosed)
        {
            _ordersOpen.Remove(id);
            _ordersClosed.Add(id);
            _ordersPendingCancel.Remove(id);
        }
        else
        {
            _ordersOpen.Remove(id);
            _ordersClosed.Remove(id);
        }

        if (!order.IsPending)
        {
            _ordersPendingCancel.Remove(id);
        }

        TrackQueue(order);
    }

    private void IndexOrderPosition(ClientOrderId orderId, PositionId positionId)
    {
        _orderPosition[orderId] = positionId;
        if (!_positionOrders.TryGetValue(positionId, out HashSet<ClientOrderId>? set))
        {
            set = new HashSet<ClientOrderId>();
            _positionOrders[positionId] = set;
        }

        set.Add(orderId);
    }

    public Order? Order(ClientOrderId id) => _orders.GetValueOrDefault(id);

    public Order? OrderForVenueId(VenueOrderId id) => _venueOrderIndex.TryGetValue(id, out ClientOrderId clientId) ? _orders.GetValueOrDefault(clientId) : null;

    public ClientOrderId? ClientOrderIdFor(VenueOrderId id) => _venueOrderIndex.TryGetValue(id, out ClientOrderId clientId) ? clientId : null;

    public VenueOrderId? VenueOrderIdFor(ClientOrderId id) => _orders.TryGetValue(id, out Order? order) ? order.VenueOrderId : null;

    private IReadOnlyList<Order> Filter(IEnumerable<ClientOrderId> ids, Venue? venue, MarketKey? marketKey, StrategyId? strategyId, OrderSide? side)
    {
        List<Order> result = new();
        foreach (ClientOrderId id in ids)
        {
            if (!_orders.TryGetValue(id, out Order? order))
            {
                continue;
            }

            if (venue is { } v && order.MarketKey.Venue != v)
            {
                continue;
            }

            if (marketKey is { } i && order.MarketKey != i)
            {
                continue;
            }

            if (strategyId is { } s && order.StrategyId != s)
            {
                continue;
            }

            if (side is { } os && order.Side != os)
            {
                continue;
            }

            result.Add(order);
        }

        result.Sort((a, b) => a.CreatedTime.CompareTo(b.CreatedTime));
        return result;
    }

    public IReadOnlyList<Order> Orders(Venue? venue = null, MarketKey? marketKey = null, StrategyId? strategyId = null, OrderSide? side = null) =>
        Filter(_orders.Keys, venue, marketKey, strategyId, side);

    public IReadOnlyList<Order> OrdersOpen(Venue? venue = null, MarketKey? marketKey = null, StrategyId? strategyId = null, OrderSide? side = null) =>
        Filter(_ordersOpen, venue, marketKey, strategyId, side);

    public IReadOnlyList<Order> OrdersClosed(Venue? venue = null, MarketKey? marketKey = null, StrategyId? strategyId = null, OrderSide? side = null) =>
        Filter(_ordersClosed, venue, marketKey, strategyId, side);

    public IReadOnlyList<Order> OrdersEmulated(Venue? venue = null, MarketKey? marketKey = null, StrategyId? strategyId = null, OrderSide? side = null) =>
        Filter(_ordersEmulated, venue, marketKey, strategyId, side);

    public IReadOnlyList<Order> OrdersInflight(Venue? venue = null, MarketKey? marketKey = null, StrategyId? strategyId = null, OrderSide? side = null) =>
        Filter(_ordersInflight, venue, marketKey, strategyId, side);

    public PositionSnapshot SnapshotPosition(Position position, Price? markPrice = null)
    {
        ArgumentNullException.ThrowIfNull(position);

        // The price it is marked at, in order: the one asked for, then the side that would close it, then the last
        // traded. A position with no price anywhere is snapshotted without an unrealised figure rather than with a zero,
        // because "nothing to measure it against" and "flat" are different facts.
        Price? mark = markPrice
            ?? Price(position.MarketKey, position.IsLong ? PriceType.Bid : PriceType.Ask)
            ?? Price(position.MarketKey, PriceType.Last);

        PositionSnapshot snapshot = PositionSnapshot.Of(position, position.TsLast, mark);
        _positionSnapshots.Add(snapshot);
        return snapshot;
    }

    public IReadOnlyList<PositionSnapshot> PositionSnapshots(PositionId? positionId = null, MarketKey? marketKey = null, StrategyId? strategyId = null) =>
    [
        .. _positionSnapshots.Where(s =>
            (positionId is null || s.PositionId == positionId.Value)
            && (marketKey is null || s.MarketKey == marketKey.Value)
            && (strategyId is null || s.StrategyId == strategyId.Value)),
    ];

    public IReadOnlyList<Order> OrdersForPosition(PositionId positionId) =>
        _positionOrders.TryGetValue(positionId, out HashSet<ClientOrderId>? set) ? Filter(set, null, null, null, null) : [];

    public IReadOnlyList<Order> OrdersForOrderSchedule(OrderScheduleId orderScheduleId, Venue? venue = null, MarketKey? marketKey = null, StrategyId? strategyId = null) =>
        _orderScheduleOrders.TryGetValue(orderScheduleId, out HashSet<ClientOrderId>? set) ? Filter(set, venue, marketKey, strategyId, null) : [];

    public IReadOnlyList<Order> OrdersForExecSpawn(ClientOrderId execSpawnId) =>
        _execSpawnOrders.TryGetValue(execSpawnId, out HashSet<ClientOrderId>? set) ? Filter(set, null, null, null, null) : [];

    public bool OrderExists(ClientOrderId id) => _orders.ContainsKey(id);

    public bool IsOrderOpen(ClientOrderId id) => _ordersOpen.Contains(id);

    public bool IsOrderClosed(ClientOrderId id) => _ordersClosed.Contains(id);

    public bool IsOrderEmulated(ClientOrderId id) => _ordersEmulated.Contains(id);

    public bool IsOrderInflight(ClientOrderId id) => _ordersInflight.Contains(id);

    public bool IsOrderPendingCancelLocal(ClientOrderId id) => _ordersPendingCancel.Contains(id);

    public int OrdersOpenCount(Venue? venue = null, MarketKey? marketKey = null, StrategyId? strategyId = null, OrderSide? side = null) =>
        OrdersOpen(venue, marketKey, strategyId, side).Count;

    public int OrdersClosedCount(Venue? venue = null, MarketKey? marketKey = null, StrategyId? strategyId = null, OrderSide? side = null) =>
        OrdersClosed(venue, marketKey, strategyId, side).Count;

    public int OrdersTotalCount(Venue? venue = null, MarketKey? marketKey = null, StrategyId? strategyId = null, OrderSide? side = null) =>
        Orders(venue, marketKey, strategyId, side).Count;

    public OrderList? OrderList(OrderListId id) => _orderLists.GetValueOrDefault(id);

    public IReadOnlyList<OrderList> OrderLists(Venue? venue = null, MarketKey? marketKey = null, StrategyId? strategyId = null) =>
        _orderLists.Values.Where(l =>
            (venue is null || l.MarketKey.Venue == venue.Value) &&
            (marketKey is null || l.MarketKey == marketKey.Value) &&
            (strategyId is null || l.StrategyId == strategyId.Value)).ToList();

    public bool OrderListExists(OrderListId id) => _orderLists.ContainsKey(id);

    // ----- Positions -----

    public void AddPosition(Position position)
    {
        ArgumentNullException.ThrowIfNull(position);
        if (_positions.ContainsKey(position.Id))
        {
            throw new InvalidOperationException($"Position {position.Id} already exists in the cache.");
        }

        AddPositionInternal(position, persist: _config.Persist);
        UpdatePositionIndexes(position);
    }

    private void AddPositionInternal(Position position, bool persist)
    {
        _positions[position.Id] = position;
        _positionStrategy[position.Id] = position.StrategyId;
        foreach (ClientOrderId orderId in position.ClientOrderIds)
        {
            IndexOrderPosition(orderId, position.Id);
        }

        if (persist)
        {
            _database?.AddPosition(position);
        }
    }

    public void UpdatePosition(Position position)
    {
        ArgumentNullException.ThrowIfNull(position);
        foreach (ClientOrderId orderId in position.ClientOrderIds)
        {
            IndexOrderPosition(orderId, position.Id);
        }

        UpdatePositionIndexes(position);
        if (_config.Persist)
        {
            _database?.UpdatePosition(position);
        }
    }

    private void UpdatePositionIndexes(Position position)
    {
        if (position.IsOpen)
        {
            _positionsOpen.Add(position.Id);
            _positionsClosed.Remove(position.Id);
        }
        else
        {
            _positionsOpen.Remove(position.Id);
            _positionsClosed.Add(position.Id);
        }
    }

    public Position? Position(PositionId id) => _positions.GetValueOrDefault(id);

    public Position? PositionForOrder(ClientOrderId clientOrderId) =>
        _orderPosition.TryGetValue(clientOrderId, out PositionId pid) ? _positions.GetValueOrDefault(pid) : null;

    public PositionId? PositionIdFor(ClientOrderId clientOrderId) => _orderPosition.TryGetValue(clientOrderId, out PositionId pid) ? pid : null;

    private IReadOnlyList<Position> FilterPositions(IEnumerable<PositionId> ids, Venue? venue, MarketKey? marketKey, StrategyId? strategyId, PositionSide? side)
    {
        List<Position> result = new();
        foreach (PositionId id in ids)
        {
            if (!_positions.TryGetValue(id, out Position? position))
            {
                continue;
            }

            if (venue is { } v && position.MarketKey.Venue != v)
            {
                continue;
            }

            if (marketKey is { } i && position.MarketKey != i)
            {
                continue;
            }

            if (strategyId is { } s && position.StrategyId != s)
            {
                continue;
            }

            if (side is { } ps && position.Side != ps)
            {
                continue;
            }

            result.Add(position);
        }

        result.Sort((a, b) => a.TsOpened.CompareTo(b.TsOpened));
        return result;
    }

    public IReadOnlyList<Position> Positions(Venue? venue = null, MarketKey? marketKey = null, StrategyId? strategyId = null, PositionSide? side = null) =>
        FilterPositions(_positions.Keys, venue, marketKey, strategyId, side);

    public IReadOnlyList<Position> PositionsOpen(Venue? venue = null, MarketKey? marketKey = null, StrategyId? strategyId = null, PositionSide? side = null) =>
        FilterPositions(_positionsOpen, venue, marketKey, strategyId, side);

    public IReadOnlyList<Position> PositionsClosed(Venue? venue = null, MarketKey? marketKey = null, StrategyId? strategyId = null) =>
        FilterPositions(_positionsClosed, venue, marketKey, strategyId, null);

    public bool PositionExists(PositionId id) => _positions.ContainsKey(id);

    public bool IsPositionOpen(PositionId id) => _positionsOpen.Contains(id);

    public bool IsPositionClosed(PositionId id) => _positionsClosed.Contains(id);

    public int PositionsOpenCount(Venue? venue = null, MarketKey? marketKey = null, StrategyId? strategyId = null, PositionSide? side = null) =>
        PositionsOpen(venue, marketKey, strategyId, side).Count;

    public int PositionsClosedCount(Venue? venue = null, MarketKey? marketKey = null, StrategyId? strategyId = null) =>
        PositionsClosed(venue, marketKey, strategyId).Count;

    public int PositionsTotalCount(Venue? venue = null, MarketKey? marketKey = null, StrategyId? strategyId = null, PositionSide? side = null) =>
        Positions(venue, marketKey, strategyId, side).Count;

    // ----- Accounts -----

    public void AddAccount(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        AddAccountInternal(account);
        if (_config.Persist)
        {
            PersistCurrencies(account);
            _database?.AddAccount(account);
        }
    }

    private void AddAccountInternal(Account account)
    {
        _accounts[account.Id] = account;
        _venueAccounts[account.Id.Venue] = account.Id;
    }

    public void UpdateAccount(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        _accounts[account.Id] = account;
        if (_config.Persist)
        {
            // A balance can arrive in a currency the account did not hold before, so the currencies are written on
            // every update and not only on the first.
            PersistCurrencies(account);
            _database?.UpdateAccount(account);
        }
    }

    public Account? Account(AccountId id) => _accounts.GetValueOrDefault(id);

    public Account? AccountForVenue(Venue venue) => _venueAccounts.TryGetValue(venue, out AccountId id) ? _accounts.GetValueOrDefault(id) : null;

    public AccountId? AccountIdFor(Venue venue) => _venueAccounts.TryGetValue(venue, out AccountId id) ? id : null;

    public IReadOnlyList<Account> Accounts() => _accounts.Values.ToList();

    // ----- Strategy lookups -----

    public StrategyId? StrategyIdForOrder(ClientOrderId clientOrderId) => _orderStrategy.TryGetValue(clientOrderId, out StrategyId s) ? s : null;

    public StrategyId? StrategyIdForPosition(PositionId positionId) => _positionStrategy.TryGetValue(positionId, out StrategyId s) ? s : null;

    // ----- General storage -----

    public void Add(string key, byte[] value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        _general[key] = value;
        if (_config.Persist)
        {
            _database?.Add(key, value);
        }
    }

    public byte[]? Get(string key)
    {
        if (_general.TryGetValue(key, out byte[]? value))
        {
            return value;
        }

        return _config.Persist ? _database?.Get(key) : null;
    }

    public void SaveRuntimeModuleState(RuntimeModuleId runtimeModuleId, IDictionary<string, byte[]> state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _runtimeModuleState[runtimeModuleId] = state;
        if (_config.Persist)
        {
            _database?.SaveRuntimeModuleState(runtimeModuleId, state);
        }
    }

    public IDictionary<string, byte[]>? LoadRuntimeModuleState(RuntimeModuleId runtimeModuleId)
    {
        if (_runtimeModuleState.TryGetValue(runtimeModuleId, out IDictionary<string, byte[]>? state))
        {
            return state;
        }

        return _config.Persist ? _database?.LoadRuntimeModuleState(runtimeModuleId) : null;
    }

    // ----- Integrity -----

    /// <summary>
    /// Verifies that the indexes are consistent with the primary collections. Returns true when consistent.
    /// </summary>
    public bool CheckIntegrity()
    {
        bool ok = true;
        foreach (ClientOrderId id in _ordersOpen)
        {
            if (!_orders.TryGetValue(id, out Order? order) || !order.IsOpen)
            {
                _log.LogError("Integrity: open index contains {Order} which is not open", id);
                ok = false;
            }
        }

        foreach (ClientOrderId id in _ordersClosed)
        {
            if (!_orders.TryGetValue(id, out Order? order) || !order.IsClosed)
            {
                _log.LogError("Integrity: closed index contains {Order} which is not closed", id);
                ok = false;
            }
        }

        foreach (PositionId id in _positionsOpen)
        {
            if (!_positions.TryGetValue(id, out Position? position) || !position.IsOpen)
            {
                _log.LogError("Integrity: open index contains position {Position} which is not open", id);
                ok = false;
            }
        }

        return ok;
    }
}
