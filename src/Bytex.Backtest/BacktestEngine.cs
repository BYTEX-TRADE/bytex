using System.Diagnostics;
using Bytex.Core.Caching;
using Bytex.Core.Kernel;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Timing;
using Bytex.Core.Trading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bytex.Backtest;

public sealed record BacktestEngineConfig
{
    public KernelConfig Kernel { get; init; } = new() { Environment = TradingEnvironment.Backtest, LoadState = false, SaveState = false };

    public string RunId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Log each processed data element (very verbose).</summary>
    public bool LogData { get; init; }

    /// <summary>Stop processing on the first faulted actor instead of continuing.</summary>
    public bool StopOnActorFault { get; init; }
}

/// <summary>
/// Low-level backtest runner: add venues, instruments, data, and strategies, then run the event loop.
/// </summary>
public sealed class BacktestEngine : IDisposable
{
    private readonly BacktestEngineConfig _config;
    private readonly ILogger _log;
    private readonly TestClock _clock;
    private readonly Kernel _kernel;
    private readonly Dictionary<Venue, SimulatedExchange> _exchanges = new();
    private readonly List<IData> _data = new();
    private readonly List<IData> _sorted = new();
    private bool _dataSorted;

    // Where a streaming run has got to. The cursor is an index into _sorted, but a later batch can be added with earlier
    // timestamps, which re-sorts the list, so the position of the last dispatched element is remembered by its sort key
    // (timestamp, then the order it was added in) and the cursor is worked out again after every sort.
    private readonly List<int> _sortedOrder = new();
    private int _dispatched;
    private UnixNanos? _lastDispatchedTs;
    private int _lastDispatchedOrder = -1;
    private bool _accountsInitialized;
    private bool _disposed;

    public BacktestEngine(BacktestEngineConfig? config = null, ILoggerFactory? loggerFactory = null)
    {
        _config = config ?? new BacktestEngineConfig();
        LoggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _log = LoggerFactory.CreateLogger<BacktestEngine>();
        _clock = new TestClock();
        _kernel = new Kernel(_config.Kernel with { Environment = TradingEnvironment.Backtest }, _clock, LoggerFactory);
        _kernel.MessageBus.Subscribe(Core.Model.Commands.Topics.AllPositionEvents, OnPositionEventForEquity);
    }

    public BacktestEngineConfig Config => _config;

    public ILoggerFactory LoggerFactory { get; }

    public Kernel Kernel => _kernel;

    public TestClock Clock => _clock;

    public Cache Cache => _kernel.Cache;

    public Trader Trader => _kernel.Trader;

    public IReadOnlyDictionary<Venue, SimulatedExchange> Exchanges => _exchanges;

    public IReadOnlyList<IData> Data => _data;

    public long Iteration { get; private set; }

    public UnixNanos? RunStarted { get; private set; }

    public UnixNanos? RunFinished { get; private set; }

    public UnixNanos? BacktestStart { get; private set; }

    public UnixNanos? BacktestEnd { get; private set; }

    public TimeSpan Elapsed { get; private set; }

    // ----- Setup -----

    /// <summary>
    /// Where the venue sits among the subscribers of book data: above the strategies, so a book that moves is matched
    /// and any fill it produces is published before a strategy is told the book moved.
    /// </summary>
    private const int BookMatchingPriority = 100;

    public SimulatedExchange AddVenue(SimulatedVenueConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (_exchanges.ContainsKey(config.Venue))
        {
            throw new InvalidOperationException($"Venue {config.Venue} already added.");
        }

        SimulatedExchange exchange = new(config, _kernel.Services);
        BacktestExecutionClient execClient = new(exchange, _kernel.Services);
        BacktestDataClient dataClient = new(config.Venue, _kernel.Services, History);
        _kernel.AddExecutionClient(execClient);
        _kernel.AddDataClient(dataClient);
        _exchanges[config.Venue] = exchange;

        // Book data reaches the cache through the data engine, which publishes it in the same call, so the venue cannot
        // be handed the assembled book before that. It subscribes above the strategies instead: the book is matched, and
        // any fill it produces is published, before a strategy is told the book moved.
        void MatchBook(object message)
        {
            InstrumentId? id = message switch
            {
                OrderBookDeltas deltas => deltas.InstrumentId,
                OrderBook book => book.InstrumentId,
                _ => null,
            };
            if (id is { } instrumentId && _kernel.Cache.OrderBook(instrumentId) is { } current)
            {
                exchange.ProcessOrderBook(current, _clock.Timestamp);
            }
        }

        _kernel.MessageBus.Subscribe($"data.book.deltas.{config.Venue}.*", MatchBook, priority: BookMatchingPriority);
        _kernel.MessageBus.Subscribe($"data.book.snapshots.{config.Venue}.*", MatchBook, priority: BookMatchingPriority);

        foreach (Instrument instrument in _kernel.Cache.Instruments(config.Venue))
        {
            exchange.AddInstrument(instrument);
        }

        _log.LogInformation("Added venue {Venue} ({AccountType}, {Oms})", config.Venue, config.AccountType, config.OmsType);
        return exchange;
    }

    public void AddInstrument(Instrument instrument)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        _kernel.Cache.AddInstrument(instrument);
        if (_exchanges.TryGetValue(instrument.Venue, out SimulatedExchange? exchange))
        {
            exchange.AddInstrument(instrument);
        }
    }

    public void AddData(IEnumerable<IData> data)
    {
        ArgumentNullException.ThrowIfNull(data);
        int before = _data.Count;
        _data.AddRange(data);
        _dataSorted = false;
        _log.LogInformation("Added {Count} data elements (total {Total})", _data.Count - before, _data.Count);
    }

    public void AddActor(Actor actor) => _kernel.Trader.AddActor(actor);

    public void AddStrategy(Strategy strategy) => _kernel.Trader.AddStrategy(strategy);

    public void AddExecAlgorithm(ExecAlgorithm algorithm) => _kernel.Trader.AddExecAlgorithm(algorithm);

    // ----- Running -----

    /// <summary>
    /// Runs the backtest over the added data within the optional time range.
    /// With <paramref name="streaming"/> the kernel is left running so more data can be added and run again.
    /// </summary>
    public void Run(UnixNanos? start = null, UnixNanos? end = null, bool streaming = false)
    {
        ThrowIfDisposed();
        EnsureSorted();
        if (_sorted.Count == 0)
        {
            throw new InvalidOperationException("No data has been added to the backtest engine.");
        }

        if (_exchanges.Count == 0)
        {
            throw new InvalidOperationException("No venues have been added to the backtest engine.");
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        RunStarted = UnixNanos.FromDateTimeOffset(DateTimeOffset.UtcNow);
        UnixNanos first = start ?? _sorted[0].TsInit;
        UnixNanos last = end ?? _sorted[^1].TsInit;
        BacktestStart ??= first;
        BacktestEnd = last;

        if (!_kernel.IsRunning)
        {
            _clock.SetTime(first);
            if (!_accountsInitialized)
            {
                foreach (SimulatedExchange exchange in _exchanges.Values)
                {
                    exchange.InitializeAccount();
                }

                _accountsInitialized = true;
            }

            _kernel.Start();
        }

        _log.LogInformation("Running backtest {RunId} from {Start} to {End} over {Count} elements", _config.RunId, first, last, _sorted.Count);

        // A streaming run continues where the last one stopped. Replaying the earlier batches sent the venue data it had
        // already matched against, so orders filled at prices that were history by the time they were placed.
        for (int i = _dispatched; i < _sorted.Count; i++)
        {
            IData element = _sorted[i];
            UnixNanos ts = element.TsInit;
            if (ts < first)
            {
                _dispatched = i + 1;
                _lastDispatchedTs = ts;
                _lastDispatchedOrder = _sortedOrder[i];
                continue;
            }

            if (ts > last)
            {
                break;
            }

            Advance(ts);
            Dispatch(element);
            _dispatched = i + 1;
            _lastDispatchedTs = ts;
            _lastDispatchedOrder = _sortedOrder[i];
            Iteration++;

            // One sample a timestamp, taken once everything that shares it has been dispatched: the bar the venue
            // just matched on is the price the open positions are marked at.
            if (i + 1 >= _sorted.Count || _sorted[i + 1].TsInit != ts || _sorted[i + 1].TsInit > last)
            {
                SampleEquity(ts);
            }

            if (_config.StopOnActorFault && _kernel.Trader.Strategies.Any(s => s.IsFaulted))
            {
                _log.LogError("Stopping backtest: a strategy faulted");
                break;
            }
        }

        Advance(last);
        foreach (SimulatedExchange exchange in _exchanges.Values)
        {
            exchange.ProcessDueCommands(last);
        }

        if (!streaming)
        {
            _kernel.Stop();
        }

        stopwatch.Stop();
        Elapsed += stopwatch.Elapsed;
        RunFinished = UnixNanos.FromDateTimeOffset(DateTimeOffset.UtcNow);
        _log.LogInformation("Backtest {RunId} processed {Iterations} elements in {Elapsed}", _config.RunId, Iteration, stopwatch.Elapsed);
    }

    /// <summary>
    /// Stops a streaming run.
    /// </summary>
    public void End()
    {
        if (_kernel.IsRunning)
        {
            _kernel.Stop();
        }
    }

    private void Advance(UnixNanos to)
    {
        if (to < _clock.Timestamp)
        {
            return;
        }

        IReadOnlyList<TimeEventHandler> due = _clock.AdvanceTime(to);
        foreach (TimeEventHandler handler in due)
        {
            // Set the clock to the event time so anything the handler does is stamped correctly.
            _clock.SetTime(handler.Event.TsEvent);
            RunDueCommands(handler.Event.TsEvent);
            handler.Handle();
        }

        // A command whose latency elapses between two data events is carried out then, at its own time, not at the time
        // of the next event to arrive: that is the timestamp its acknowledgement and its fill would really have had.
        RunDueCommands(to);
        _clock.SetTime(to);
    }

    /// <summary>
    /// Carries out every command in flight that comes due at or before the given time, each with the clock set to its own
    /// due time, earliest first across venues.
    /// </summary>
    private void RunDueCommands(UnixNanos to)
    {
        while (true)
        {
            UnixNanos? next = null;
            foreach (SimulatedExchange exchange in _exchanges.Values)
            {
                if (exchange.NextDueTime is { } due && due <= to && (next is not { } current || due < current))
                {
                    next = due;
                }
            }

            if (next is not { } at)
            {
                return;
            }

            _clock.SetTime(at);
            foreach (SimulatedExchange exchange in _exchanges.Values)
            {
                exchange.ProcessDueCommands(at);
            }
        }
    }

    private void Dispatch(IData element)
    {
        if (_config.LogData)
        {
            _log.LogTrace("{Data}", element);
        }

        // The venue sees the data first so that resting orders match before strategies react.
        switch (element)
        {
            case QuoteTick quote:
                if (_exchanges.TryGetValue(quote.InstrumentId.Venue, out SimulatedExchange? qx))
                {
                    qx.ProcessQuoteTick(quote);
                }

                break;
            case TradeTick trade:
                if (_exchanges.TryGetValue(trade.InstrumentId.Venue, out SimulatedExchange? tx))
                {
                    tx.ProcessTradeTick(trade);
                }

                break;
            case Bar bar:
                if (_exchanges.TryGetValue(bar.BarType.InstrumentId.Venue, out SimulatedExchange? bx))
                {
                    bx.ProcessBar(bar);
                }

                break;
            case FundingRateUpdate funding:
                if (_exchanges.TryGetValue(funding.InstrumentId.Venue, out SimulatedExchange? fx))
                {
                    fx.ProcessFundingRate(funding);
                }

                break;
            case OrderBookDelta or OrderBookDeltas or OrderBookDepth:
                // The venue matches from its high-priority subscription, before the strategies are notified.
                _kernel.DataEngine.Process(element);
                return;
        }

        _kernel.DataEngine.Process(element);
    }

    private void EnsureSorted()
    {
        if (_dataSorted)
        {
            return;
        }

        _sorted.Clear();
        _sortedOrder.Clear();
        foreach ((IData d, int i) in _data.Select((d, i) => (d, i)).OrderBy(x => x.d.TsInit).ThenBy(x => x.i))
        {
            _sorted.Add(d);
            _sortedOrder.Add(i);
        }

        _dataSorted = true;
        _dispatched = _lastDispatchedTs is not { } ts ? 0 : CountUpTo(ts, _lastDispatchedOrder);
    }

    /// <summary>How many elements of the sorted data are at or before the given sort key.</summary>
    private int CountUpTo(UnixNanos ts, int order)
    {
        int count = 0;
        while (count < _sorted.Count && (_sorted[count].TsInit < ts || (_sorted[count].TsInit == ts && _sortedOrder[count] <= order)))
        {
            count++;
        }

        return count;
    }

    private IReadOnlyList<IData> History(RequestCommand request)
    {
        EnsureSorted();
        UnixNanos now = _clock.Timestamp;
        IEnumerable<IData> query = _sorted.Where(d => d.TsInit <= now);
        query = request switch
        {
            RequestBars bars => query.OfType<Bar>().Where(b => b.BarType == bars.BarType).Cast<IData>(),
            RequestQuoteTicks quotes => query.OfType<QuoteTick>().Where(q => q.InstrumentId == quotes.InstrumentId).Cast<IData>(),
            RequestTradeTicks trades => query.OfType<TradeTick>().Where(t => t.InstrumentId == trades.InstrumentId).Cast<IData>(),
            _ => [],
        };

        if (request.Start is { } s)
        {
            query = query.Where(d => d.TsInit >= s);
        }

        if (request.End is { } e)
        {
            query = query.Where(d => d.TsInit <= e);
        }

        List<IData> result = query.ToList();
        if (request.Limit is { } limit && result.Count > limit)
        {
            result = result.Skip(result.Count - limit).ToList();
        }

        return result;
    }

    // ----- Results -----

    // ----- Equity over time -----

    /// <summary>
    /// The account's equity as the run saw it: one point per timestamp of data, each open position marked at the price
    /// the venue had just seen. A curve built afterwards from fills alone cannot fall while a position is held, so
    /// every figure taken from it - the drawdown, the daily returns, the Sharpe and Sortino built on them - said a
    /// strategy that never closes a loser had no risk. Realised profit is accumulated from position events, which is
    /// cheap per event; only the open positions are marked on each sample, which is the part that has to be fresh.
    /// </summary>
    private readonly List<EquitySample> _equity = new();
    private readonly Dictionary<PositionId, (Currency Currency, decimal Realized)> _realizedPerPosition = new();
    private readonly Dictionary<Currency, decimal> _realized = new();

    internal IReadOnlyList<EquitySample> EquitySamples => _equity;

    private void OnPositionEventForEquity(object message)
    {
        if (message is not PositionEvent e)
        {
            return;
        }

        Currency currency = e.RealizedPnl.Currency;
        decimal previous = _realizedPerPosition.TryGetValue(e.PositionId, out (Currency Currency, decimal Realized) known) ? known.Realized : 0m;
        _realizedPerPosition[e.PositionId] = (currency, e.RealizedPnl.Amount);
        _realized[currency] = _realized.GetValueOrDefault(currency) + e.RealizedPnl.Amount - previous;
    }

    private void SampleEquity(UnixNanos ts)
    {
        // Every currency the run started with, so the curve begins at the starting balance and has a point at every
        // timestamp - including the ones before anything was traded, where the account is worth exactly what it was.
        Dictionary<Currency, decimal> totals = new();
        foreach (SimulatedExchange exchange in _exchanges.Values)
        {
            foreach (Money balance in exchange.Config.StartingBalances)
            {
                totals[balance.Currency] = totals.GetValueOrDefault(balance.Currency);
            }
        }

        foreach ((Currency currency, decimal realized) in _realized)
        {
            totals[currency] = totals.GetValueOrDefault(currency) + realized;
        }

        foreach (SimulatedExchange exchange in _exchanges.Values)
        {
            foreach ((Currency currency, Money unrealized) in _kernel.Portfolio.UnrealizedPnls(exchange.Venue))
            {
                totals[currency] = totals.GetValueOrDefault(currency) + unrealized.Amount;
            }
        }

        foreach ((Currency currency, decimal total) in totals)
        {
            _equity.Add(new EquitySample(ts, currency, total));
        }
    }

    public BacktestResult GetResult() => BacktestResult.From(this);

    /// <summary>
    /// Resets engine state (orders, positions, accounts, clock) while keeping venues, instruments, data, and strategies.
    /// </summary>
    public void Reset()
    {
        ThrowIfDisposed();
        _kernel.Reset();
        foreach (SimulatedExchange exchange in _exchanges.Values)
        {
            exchange.Reset();
        }

        foreach (Instrument instrument in _kernel.Cache.Instruments())
        {
            if (_exchanges.TryGetValue(instrument.Venue, out SimulatedExchange? exchange))
            {
                exchange.AddInstrument(instrument);
            }
        }

        _accountsInitialized = false;
        _dispatched = 0;
        _lastDispatchedTs = null;
        _lastDispatchedOrder = -1;
        Iteration = 0;
        RunStarted = null;
        RunFinished = null;
        BacktestStart = null;
        BacktestEnd = null;
        Elapsed = TimeSpan.Zero;
    }

    public void ClearData()
    {
        _data.Clear();
        _sorted.Clear();
        _sortedOrder.Clear();
        _dataSorted = false;
        _dispatched = 0;
        _lastDispatchedTs = null;
        _lastDispatchedOrder = -1;
    }

    public void ClearStrategies() => _kernel.Trader.Clear();

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _kernel.Dispose();
    }
}
