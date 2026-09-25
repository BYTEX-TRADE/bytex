using Bytex.Backtest;
using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Model.Reports;
using Microsoft.Extensions.Logging;

namespace Bytex.Live.Sandbox;

public sealed record SandboxExecutionClientConfig : ExecutionClientConfig
{
    public required string Venue { get; init; }

    public OmsType OmsType { get; init; } = OmsType.Netting;

    public AccountType AccountType { get; init; } = AccountType.Cash;

    public string? BaseCurrency { get; init; }

    /// <summary>Starting balances as "amount CURRENCY".</summary>
    public IReadOnlyList<string> StartingBalances { get; init; } = [];

    public decimal DefaultLeverage { get; init; } = 1m;

    public BarExecutionMode BarExecution { get; init; } = BarExecutionMode.OhlcPath;

    /// <summary>
    /// Match against the venue's own order book while the node is connected to it. A paper node is the step before a
    /// person risks money, and the question it is there to answer - would this have filled, and at what - is the one
    /// a book answers and quotes and bars do not. The book is live, free and already streaming; nothing is stored.
    /// <para>
    /// The node has to be receiving the book for this to do anything: ask for it with the instrument's depth
    /// subscription, or set <see cref="SubscribeOrderBook"/> and the venue asks for itself.
    /// </para>
    /// </summary>
    public bool MatchAgainstBook { get; init; } = true;

    /// <summary>
    /// Ask the data client for the book of every instrument this venue holds, rather than waiting for something else
    /// to ask. On by default with <see cref="MatchAgainstBook"/>, because a venue that matches against a book nobody
    /// subscribed to is a venue that quietly goes on matching against quotes.
    /// </summary>
    public bool SubscribeOrderBook { get; init; } = true;

    /// <summary>How deep a book to ask for. Zero asks the venue for as much as it will give.</summary>
    public int BookDepth { get; init; }

    /// <summary>What kind of book to ask for: L2 is what venues stream and what matching needs.</summary>
    public BookType BookType { get; init; } = BookType.L2;

    public decimal ProbFillOnLimit { get; init; } = 1m;

    public decimal ProbSlippage { get; init; }

    public TimeSpan Latency { get; init; } = TimeSpan.Zero;

    /// <summary>What the simulated venue held when the node was last stopped; null starts it flat with no orders.</summary>
    public SandboxRestoreConfig? Restore { get; init; }
}

/// <summary>
/// The state a simulated venue starts from after a restart. Balances are not part of it: they go in
/// <see cref="SandboxExecutionClientConfig.StartingBalances"/>. On a cash account a position is a balance too, so
/// <see cref="Positions"/> is for margin accounts only.
/// </summary>
public sealed record SandboxRestoreConfig
{
    public IReadOnlyList<SandboxRestoredPosition> Positions { get; init; } = [];

    public IReadOnlyList<SandboxRestoredOrder> Orders { get; init; } = [];
}

public sealed record SandboxRestoredPosition
{
    public required string InstrumentId { get; init; }

    public required PositionSide Side { get; init; }

    public required decimal Quantity { get; init; }

    public required decimal AvgPx { get; init; }
}

public sealed record SandboxRestoredOrder
{
    public required string ClientOrderId { get; init; }

    public required string InstrumentId { get; init; }

    public required OrderSide Side { get; init; }

    /// <summary>Limit, StopMarket, StopLimit, MarketIfTouched, LimitIfTouched, TrailingStopMarket or TrailingStopLimit.</summary>
    public required OrderType Type { get; init; }

    /// <summary>What is still to be filled.</summary>
    public required decimal Quantity { get; init; }

    public decimal? Price { get; init; }

    public decimal? TriggerPrice { get; init; }

    public TimeInForce TimeInForce { get; init; } = TimeInForce.Gtc;

    /// <summary>Trailing orders only: the distance the trigger keeps from the price.</summary>
    public decimal? TrailingOffset { get; init; }

    public TrailingOffsetType TrailingOffsetType { get; init; } = TrailingOffsetType.Price;

    public bool ReduceOnly { get; init; }

    public bool PostOnly { get; init; }

    /// <summary>Orders cancelled when this one fills: the other leg of a stop and target pair.</summary>
    public IReadOnlyList<string> LinkedOrderIds { get; init; } = [];

    /// <summary>The tags the order carried, such as "node:protect", so a supervisor still sees what placed it.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
}

/// <summary>
/// Executes orders against a simulated venue fed by the live market data flowing through the kernel.
/// Data clients for the same venue provide the prices; this client provides the fills.
/// </summary>
public sealed class SandboxExecutionClient : ExecutionClientBase, IMatchingReport
{
    private readonly SimulatedExchange _exchange;
    private readonly BacktestExecutionClient _inner;
    private readonly List<(string Topic, Action<object> Handler)> _subscriptions = new();
    private readonly SandboxRestoreConfig? _restore;
    private readonly SandboxExecutionClientConfig _config;
    private readonly HashSet<InstrumentId> _bookRequested = new();
    private readonly object _restoreLock = new();
    private readonly List<RestoredOrder> _awaitingOwner = new();
    private readonly List<(OrderStatusReport Order, FillReport Fill)> _openingFills = new();
    private bool _accountInitialized;
    private bool _restored;

    public SandboxExecutionClient(ClientId clientId, SandboxExecutionClientConfig config, KernelServices services)
        : base(clientId, new Venue(config.Venue), new AccountId($"{config.Venue}-SANDBOX"), config.AccountType,
            config.BaseCurrency is null ? null : Currency.FromCode(config.BaseCurrency), config.OmsType, services)
    {
        ArgumentNullException.ThrowIfNull(config);
        SimulatedVenueConfig venueConfig = new()
        {
            Venue = new Venue(config.Venue),
            OmsType = config.OmsType,
            AccountType = config.AccountType,
            BaseCurrency = config.BaseCurrency is null ? null : Currency.FromCode(config.BaseCurrency),
            StartingBalances = config.StartingBalances.Select(Money.Parse).ToList(),
            DefaultLeverage = config.DefaultLeverage,
            BarExecution = config.BarExecution,
            FillModel = new FillModel(config.ProbFillOnLimit, 1m, config.ProbSlippage),
            LatencyModel = config.Latency == TimeSpan.Zero ? LatencyModel.Zero : LatencyModel.Uniform(config.Latency),
        };
        _exchange = new SimulatedExchange(venueConfig, services);
        _inner = new BacktestExecutionClient(_exchange, services);
        _restore = config.Restore;
        _config = config;
    }

    public SimulatedExchange Exchange => _exchange;

    /// <inheritdoc />
    public IReadOnlyList<MatchingAgainst> Matching()
    {
        List<MatchingAgainst> rows = new();
        foreach (InstrumentId id in _exchange.Instruments.Keys)
        {
            rows.Add(new MatchingAgainst(id, Against(id), _config.MatchAgainstBook, _bookRequested.Contains(id)));
        }

        return rows;
    }

    /// <summary>
    /// The best thing this venue has for an instrument right now - not what it was told to want. A book it was told
    /// to match against but has not been sent is quotes, and saying "book" there would be a promise the fills do not
    /// keep.
    /// </summary>
    private string Against(InstrumentId instrumentId)
    {
        if (_config.MatchAgainstBook && Services.Cache.OrderBook(instrumentId) is { } book && book.UpdateCount > 0)
        {
            return MatchingAgainst.Book;
        }

        if (Services.Cache.QuoteTick(instrumentId) is not null)
        {
            return MatchingAgainst.Quotes;
        }

        return Services.Cache.BarTypes().Any(b => b.InstrumentId == instrumentId) ? MatchingAgainst.Bars : MatchingAgainst.Nothing;
    }

    public override Task ConnectAsync(CancellationToken ct)
    {
        // Relay the inner client's events through this client's sink.
        _inner.AttachSink(new RelaySink(this));

        foreach (Instrument instrument in Services.Cache.Instruments(Venue))
        {
            _exchange.AddInstrument(instrument);
            AskForTheBook(instrument.Id);
        }

        Subscribe($"data.quotes.{Venue}.*", m => OnMarketData((IData)m));
        Subscribe($"data.trades.{Venue}.*", m => OnMarketData((IData)m));
        Subscribe($"data.bars.*.{Venue}-*", m => OnMarketData((IData)m));
        Subscribe($"data.instrument.{Venue}.*", m =>
        {
            Instrument instrument = (Instrument)m;
            _exchange.AddInstrument(instrument);
            AskForTheBook(instrument.Id);
        });

        if (_config.MatchAgainstBook)
        {
            // The book arrives as deltas and snapshots; what a venue matches against is the book they were applied
            // to, which the cache keeps. The backtest engine takes the same route, so a paper fill and a backtest
            // fill come out of the same matching against the same thing.
            Subscribe($"data.book.deltas.{Venue}.*", m => OnBook(m switch
            {
                OrderBookDeltas deltas => deltas.InstrumentId,
                OrderBookDelta delta => delta.InstrumentId,
                _ => null,
            }));
            Subscribe($"data.book.snapshots.{Venue}.*", m => OnBook((m as OrderBook)?.InstrumentId));
        }

        RestoreVenueState();

        if (!_accountInitialized)
        {
            _exchange.InitializeAccount();
            _accountInitialized = true;
        }

        NotifyConnected();
        return Task.CompletedTask;
    }

    // The venue gets its position back here. The engine gets it the way it would from a real venue, through the mass
    // status it reconciles with at start: the position as the fill that opened it, the orders as open orders. An instrument
    // named in the restore has to be loaded by then; starting flat instead would be a lie, so the start fails.
    private void RestoreVenueState()
    {
        if (_restore is null || _restored)
        {
            return;
        }

        _restored = true;
        UnixNanos now = Services.Clock.Timestamp;
        foreach (SandboxRestoredPosition p in _restore.Positions)
        {
            Instrument instrument = RestoredInstrument(p.InstrumentId, "position");
            if (p.Side is not (PositionSide.Long or PositionSide.Short) || p.Quantity <= 0m)
            {
                throw new InvalidOperationException($"Restored position on {p.InstrumentId} needs side Long or Short and a quantity above zero.");
            }

            _exchange.RestorePosition(instrument.Id, p.Side == PositionSide.Long ? p.Quantity : -p.Quantity, p.AvgPx);

            OrderSide side = p.Side == PositionSide.Long ? OrderSide.Buy : OrderSide.Sell;
            Quantity quantity = instrument.MakeQuantity(p.Quantity);
            VenueOrderId venueOrderId = new($"V-RESTORED-{instrument.Id.Symbol}");
            OrderStatusReport order = new(AccountId, instrument.Id, new ClientOrderId($"RESTORED-{instrument.Id.Symbol}"), venueOrderId, side, OrderType.Market, TimeInForce.Gtc,
                OrderStatus.Filled, quantity, quantity, now, now, now, Guid.NewGuid(), AvgPx: p.AvgPx);
            FillReport fill = new(AccountId, instrument.Id, venueOrderId, new TradeId($"T-RESTORED-{instrument.Id.Symbol}"), side, quantity, instrument.MakePrice(p.AvgPx),
                Money.Zero(instrument.QuoteCurrency), LiquiditySide.None, now, now, Guid.NewGuid(), order.ClientOrderId);
            _openingFills.Add((order, fill));
        }

        foreach (SandboxRestoredOrder o in _restore.Orders)
        {
            Instrument instrument = RestoredInstrument(o.InstrumentId, "order " + o.ClientOrderId);
            bool trailing = o.Type is OrderType.TrailingStopMarket or OrderType.TrailingStopLimit;
            bool needsPrice = o.Type is OrderType.Limit or OrderType.StopLimit or OrderType.LimitIfTouched or OrderType.TrailingStopLimit;
            bool needsTrigger = trailing || o.Type is OrderType.StopMarket or OrderType.StopLimit or OrderType.MarketIfTouched or OrderType.LimitIfTouched;
            bool restsAtTheBell = o.Type is OrderType.Market or OrderType.MarketToLimit && o.TimeInForce is TimeInForce.AtTheOpen or TimeInForce.AtTheClose;
            if (!needsPrice && !needsTrigger && !restsAtTheBell)
            {
                throw new InvalidOperationException($"Restored order {o.ClientOrderId} is a {o.Type} with time in force {o.TimeInForce}, which does not rest at a venue, so there is nothing to restore.");
            }

            if (o.Quantity <= 0m || (needsPrice && o.Price is not > 0m) || (needsTrigger && o.TriggerPrice is not > 0m))
            {
                throw new InvalidOperationException($"Restored order {o.ClientOrderId} ({o.Type}) needs a quantity above zero{(needsPrice ? ", a price" : "")}{(needsTrigger ? ", the trigger price it had reached" : "")}.");
            }

            if (trailing && o.TrailingOffset is not > 0m)
            {
                throw new InvalidOperationException($"Restored order {o.ClientOrderId} is a {o.Type} and needs its trailingOffset, or it would stop trailing.");
            }

            Quantity quantity = instrument.MakeQuantity(o.Quantity);
            OrderStatusReport report = new(AccountId, instrument.Id, new ClientOrderId(o.ClientOrderId), _exchange.NextVenueOrderId(), o.Side, o.Type, o.TimeInForce,
                OrderStatus.Accepted, quantity, instrument.MakeQuantity(0m), now, now, now, Guid.NewGuid(),
                Price: needsPrice ? instrument.MakePrice(o.Price!.Value) : null, TriggerPrice: needsTrigger ? instrument.MakePrice(o.TriggerPrice!.Value) : null,
                TrailingOffset: o.TrailingOffset, TrailingOffsetType: o.TrailingOffsetType,
                PostOnly: o.PostOnly, ReduceOnly: o.ReduceOnly, Tags: o.Tags);
            _awaitingOwner.Add(new RestoredOrder(report, o.LinkedOrderIds.Select(id => new ClientOrderId(id)).ToList()));
        }

        Log.LogInformation("Restored {Positions} positions and {Orders} resting orders at simulated venue {Venue}", _restore.Positions.Count, _restore.Orders.Count, Venue);
    }

    private Instrument RestoredInstrument(string instrumentId, string what)
    {
        InstrumentId id = Core.Model.Identifiers.InstrumentId.Parse(instrumentId);
        return _exchange.Instruments.TryGetValue(id, out Instrument? instrument)
            ? instrument
            : throw new InvalidOperationException($"Cannot restore {what}: instrument {instrumentId} is not loaded for venue {Venue}. Load it before the execution client connects.");
    }

    // A restored order has to be the very object the engine's cache holds, because the matching engine reads an order's
    // state from it. That object exists once the engine has reconciled the mass status, so the order goes to the matching
    // engine from then on: before any market data is matched and before any command is carried out.
    private void HandOverRestoredOrders()
    {
        lock (_restoreLock)
        {
            if (_awaitingOwner.Count == 0)
            {
                return;
            }

            foreach (RestoredOrder restored in _awaitingOwner.ToList())
            {
                Order? order = Services.Cache.Order(restored.Report.ClientOrderId!.Value);
                if (order is null)
                {
                    continue;
                }

                _awaitingOwner.Remove(restored);
                if (!order.IsClosed)
                {
                    _exchange.RestoreOrder(order, restored.Report.VenueOrderId, restored.Linked);
                }
            }
        }
    }

    private sealed record RestoredOrder(OrderStatusReport Report, IReadOnlyList<ClientOrderId> Linked);

    /// <summary>
    /// Where the sandbox venue sits among the subscribers of market data: above the strategies, so what it matches on
    /// a tick is published before a strategy reacts to the same tick.
    /// </summary>
    private const int DataPriority = 10;

    private void Subscribe(string topic, Action<object> handler)
    {
        Services.MessageBus.Subscribe(topic, handler, priority: DataPriority);
        _subscriptions.Add((topic, handler));
    }

    /// <summary>
    /// Matches the venue against the book the cache has maintained for an instrument. Called for every book event the
    /// node receives; nothing happens when the cache has no book for it yet, which is the moment before the first
    /// snapshot arrives.
    /// </summary>
    private void OnBook(InstrumentId? instrumentId)
    {
        HandOverRestoredOrders();
        if (instrumentId is { } id && Services.Cache.OrderBook(id) is { } book)
        {
            _exchange.ProcessOrderBook(book, Services.Clock.Timestamp);
        }
    }

    /// <summary>
    /// Asks the data client for an instrument's book, so a venue told to match against one is not left matching
    /// against quotes because nobody else happened to want depth.
    /// </summary>
    private void AskForTheBook(InstrumentId instrumentId)
    {
        if (!_config.MatchAgainstBook || !_config.SubscribeOrderBook || !_bookRequested.Add(instrumentId))
        {
            return;
        }

        Services.MessageBus.Send(
            Endpoints.DataEngineExecute,
            new SubscribeOrderBookDeltas(instrumentId, _config.BookType, _config.BookDepth, null, Guid.NewGuid(), Services.Clock.Timestamp));
        Log.LogInformation("Asked {Venue} for the book of {InstrumentId}: this node's paper venue matches against it", Venue, instrumentId);
    }

    private void OnMarketData(IData data)
    {
        HandOverRestoredOrders();
        if (data.InstrumentId is { } from)
        {
            // Belt for the ordering: whatever the state of the data client when this venue connected, an instrument
            // that is producing data is one whose book can be asked for now. Idempotent - the venue remembers what it
            // has already asked for.
            AskForTheBook(from);
        }

        switch (data)
        {
            case QuoteTick quote:
                _exchange.ProcessQuoteTick(quote);
                break;
            case TradeTick trade:
                _exchange.ProcessTradeTick(trade);
                break;
            case Bar bar:
                _exchange.ProcessBar(bar);
                break;
        }
    }

    public override Task DisconnectAsync(CancellationToken ct)
    {
        foreach ((string topic, Action<object> handler) in _subscriptions)
        {
            Services.MessageBus.Unsubscribe(topic, handler);
        }

        _subscriptions.Clear();
        NotifyDisconnected("disconnect requested");
        return Task.CompletedTask;
    }

    public override Task SubmitOrderAsync(SubmitOrder command, CancellationToken ct)
    {
        HandOverRestoredOrders();
        return _inner.SubmitOrderAsync(command, ct);
    }

    public override Task SubmitOrderListAsync(SubmitOrderList command, CancellationToken ct)
    {
        HandOverRestoredOrders();
        return _inner.SubmitOrderListAsync(command, ct);
    }

    public override Task ModifyOrderAsync(ModifyOrder command, CancellationToken ct)
    {
        HandOverRestoredOrders();
        return _inner.ModifyOrderAsync(command, ct);
    }

    public override Task CancelOrderAsync(CancelOrder command, CancellationToken ct)
    {
        HandOverRestoredOrders();
        return _inner.CancelOrderAsync(command, ct);
    }

    public override Task CancelAllOrdersAsync(CancelAllOrders command, CancellationToken ct)
    {
        HandOverRestoredOrders();
        return _inner.CancelAllOrdersAsync(command, ct);
    }

    // Everything is reported under this client's account, the one its order events carry. The fill that opened a restored
    // position is reported on every call: the engine knows a trade it has already applied by its id.
    public override async Task<ExecutionMassStatus?> GenerateMassStatusAsync(UnixNanos? since, CancellationToken ct)
    {
        ExecutionMassStatus? status = await _inner.GenerateMassStatusAsync(since, ct).ConfigureAwait(false);
        if (status is null)
        {
            return null;
        }

        List<OrderStatusReport> orders = status.OrderReports.Select(o => o with { AccountId = AccountId }).ToList();
        List<FillReport> fills = status.FillReports.Select(f => f with { AccountId = AccountId }).ToList();
        lock (_restoreLock)
        {
            orders.AddRange(_openingFills.Select(f => f.Order));
            fills.AddRange(_openingFills.Select(f => f.Fill));
            orders.AddRange(_awaitingOwner.Select(r => r.Report));
        }

        return status with
        {
            ClientId = ClientId,
            AccountId = AccountId,
            OrderReports = orders,
            FillReports = fills,
            PositionReports = status.PositionReports.Select(p => p with { AccountId = AccountId }).ToList(),
        };
    }

    private sealed class RelaySink : IExecutionClientSink
    {
        private readonly SandboxExecutionClient _owner;

        public RelaySink(SandboxExecutionClient owner) => _owner = owner;

        public void OnOrderEvent(Core.Model.Events.OrderEvent e) => _owner.Sink.OnOrderEvent(e with { AccountId = _owner.AccountId });

        public void OnAccountState(Core.Model.Events.AccountState state) => _owner.Sink.OnAccountState(state with { AccountId = _owner.AccountId });

        public void OnConnected(ClientId clientId)
        {
        }

        public void OnDisconnected(ClientId clientId, string reason)
        {
        }
    }
}

public sealed class SandboxExecutionClientFactory : IExecutionClientFactory
{
    public string Name => "SANDBOX";

    public Type ConfigType => typeof(SandboxExecutionClientConfig);

    public IExecutionClient Create(ClientId clientId, ExecutionClientConfig config, KernelServices services) =>
        new SandboxExecutionClient(clientId, (SandboxExecutionClientConfig)config, services);
}
