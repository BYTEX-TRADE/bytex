using System.Diagnostics;
using Bytex.Core.Caching;
using Bytex.Core.Model.Accounts;
using Bytex.Core.TradingRuntime;
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
    public TradingRuntimeConfig TradingRuntime { get; init; } = new() { Environment = TradingEnvironment.Backtest, LoadState = false, SaveState = false };

    public string RunId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Log each processed data element (very verbose).</summary>
    public bool LogData { get; init; }

    /// <summary>Stop processing on the first faulted runtimeModule instead of continuing.</summary>
    public bool StopOnRuntimeModuleFault { get; init; }

    /// <summary>
    /// Figures to report beyond the ones built in (R8.21). Each is asked once per settlement currency, and what it
    /// answers travels in the result and in <c>result.json</c> beside the engine's own.
    /// </summary>
    public IReadOnlyList<IPerformanceStatistic> Statistics { get; init; } = [];
}

/// <summary>
/// Low-level backtest runner: add venues, instruments, data, and strategies, then run the event loop.
/// </summary>
public sealed class BacktestEngine : IDisposable
{
    private readonly BacktestEngineConfig _config;
    private readonly ILogger _log;
    private readonly TestClock _clock;
    private readonly TradingRuntime _tradingRuntime;
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
        _tradingRuntime = new TradingRuntime(_config.TradingRuntime with { Environment = TradingEnvironment.Backtest }, _clock, LoggerFactory);
        _tradingRuntime.MessageBus.Subscribe(Core.Model.Commands.Topics.AllPositionEvents, OnPositionEventForEquity);
    }

    public BacktestEngineConfig Config => _config;

    public ILoggerFactory LoggerFactory { get; }

    public TradingRuntime TradingRuntime => _tradingRuntime;

    public TestClock Clock => _clock;

    public Cache Cache => _tradingRuntime.Cache;

    /// <summary>
    /// Says that one of the run's own statistics could not be worked out. A badly written measurement costs its own
    /// figure and not the run: the number is reported as absent, and the reason is in the log rather than nowhere.
    /// </summary>
    internal void LogStatisticFailure(string name, Currency currency, Exception error) =>
        _log.LogError(error, "The statistic {Statistic} could not be worked out for {Currency}; it is reported as absent", name, currency.Code);

    public ModuleHost ModuleHost => _tradingRuntime.ModuleHost;

    public IReadOnlyDictionary<Venue, SimulatedExchange> Exchanges => _exchanges;

    public IReadOnlyList<IData> Data => _data;

    /// <summary>
    /// Lets go of every element this run has already dispatched, so that a run fed in chunks holds a chunk rather
    /// than a period.
    ///
    /// <para>
    /// Streaming on its own does not bound anything: the tradingRuntime stays up and the engine keeps what it was given, so
    /// feeding a month in pieces still ends with the month in memory. This is what makes the bound real, and it is
    /// separate from <see cref="Run"/> because dropping data is a decision - a caller that means to read its own
    /// <see cref="Data"/> afterwards, or to run the same elements again, must not have them taken away for it.
    /// </para>
    ///
    /// <para>
    /// What is kept is the place: the last timestamp dispatched and its order, so the next chunk carries on rather
    /// than replaying. Elements NOT yet dispatched are kept too - a chunk that ended past the run's window has not
    /// been used yet and is still owed to the next call.
    /// </para>
    /// </summary>
    public int ReleaseDispatched()
    {
        ThrowIfDisposed();
        if (_dispatched <= 0)
        {
            return 0;
        }

        int released = _dispatched;
        List<IData> pending = _sorted.Skip(_dispatched).ToList();

        _data.Clear();
        _data.AddRange(pending);
        _sorted.Clear();
        _sortedOrder.Clear();
        _dispatched = 0;
        _dataSorted = false;

        return released;
    }

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

        SimulatedExchange exchange = new(config, _tradingRuntime.Services);
        BacktestExecutionClient execClient = new(exchange, _tradingRuntime.Services);
        BacktestDataClient dataClient = new(config.Venue, _tradingRuntime.Services, History);
        _tradingRuntime.AddExecutionClient(execClient);
        _tradingRuntime.AddDataClient(dataClient);
        _exchanges[config.Venue] = exchange;

        // Book data reaches the cache through the data engine, which publishes it in the same call, so the venue cannot
        // be handed the assembled book before that. It subscribes above the strategies instead: the book is matched, and
        // any fill it produces is published, before a strategy is told the book moved.
        void MatchBook(object message)
        {
            MarketKey? id = message switch
            {
                OrderBookDeltas deltas => deltas.MarketKey,
                OrderBook book => book.MarketKey,
                _ => null,
            };
            if (id is { } marketKey && _tradingRuntime.Cache.OrderBook(marketKey) is { } current)
            {
                exchange.ProcessOrderBook(current, _clock.Timestamp);
            }
        }

        _tradingRuntime.MessageBus.Subscribe($"data.book.deltas.{config.Venue}.*", MatchBook, priority: BookMatchingPriority);
        _tradingRuntime.MessageBus.Subscribe($"data.book.snapshots.{config.Venue}.*", MatchBook, priority: BookMatchingPriority);

        foreach (Instrument instrument in _tradingRuntime.Cache.Instruments(config.Venue))
        {
            exchange.AddInstrument(instrument);
        }

        // The venue's margin model goes on the account the risk engine judges orders against. The account does not
        // exist yet - it is created from the venue's first state - so this waits for it, the way the leverage a venue
        // grants reaches the engine through a state rather than through configuration on both sides.
        if (config.AccountType == AccountType.Margin && !ReferenceEquals(config.MarginModel, RateMarginModel.Default))
        {
            void TeachTheModel(object _)
            {
                if (_tradingRuntime.Cache.AccountForVenue(config.Venue) is MarginAccount account)
                {
                    account.MarginModel = config.MarginModel;
                }
            }

            _tradingRuntime.MessageBus.Subscribe(Topics.AllAccountEvents, TeachTheModel);
        }

        _log.LogInformation("Added venue {Venue} ({AccountType}, {Oms}, margin {MarginModel})",
            config.Venue, config.AccountType, config.OmsType, config.MarginModel.Name);
        return exchange;
    }

    public void AddInstrument(Instrument instrument)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        _tradingRuntime.Cache.AddInstrument(instrument);
        if (_exchanges.TryGetValue(instrument.Venue, out SimulatedExchange? exchange))
        {
            exchange.AddInstrument(instrument);
        }
    }

    public void AddData(IEnumerable<IData> data)
    {
        ArgumentNullException.ThrowIfNull(data);
        int before = _data.Count;
        foreach (IData element in data)
        {
            // A chunk that starts before the run has already got to would be dispatched out of order - or, worse,
            // skipped by the cursor and never dispatched at all. A caller feeding a run in pieces has to feed them in
            // the order the run will read them, and finding out here beats finding out through a strategy that
            // behaved oddly.
            if (_lastDispatchedTs is { } last && element.CreatedTime < last)
            {
                throw new InvalidOperationException(
                    $"This element is stamped {element.CreatedTime.Value} and the run has already dispatched up to "
                    + $"{last.Value}. Data has to arrive in the order it will be read; a chunk that goes backwards "
                    + "cannot be placed in a run that has already passed that moment.");
            }

            _data.Add(element);
        }

        _dataSorted = false;
        _log.LogInformation("Added {Count} data elements (total {Total})", _data.Count - before, _data.Count);
    }

    public void AddRuntimeModule(RuntimeModule runtimeModule) => _tradingRuntime.ModuleHost.AddRuntimeModule(runtimeModule);

    public void AddStrategy(Strategy strategy) => _tradingRuntime.ModuleHost.AddStrategy(strategy);

    public void AddOrderSchedule(OrderSchedule algorithm) => _tradingRuntime.ModuleHost.AddOrderSchedule(algorithm);

    // ----- Running -----

    /// <summary>
    /// Runs the backtest over the added data within the optional time range.
    /// With <paramref name="streaming"/> the tradingRuntime is left running so more data can be added and run again.
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
        UnixNanos first = start ?? _sorted[0].CreatedTime;
        UnixNanos last = end ?? _sorted[^1].CreatedTime;
        BacktestStart ??= first;
        BacktestEnd = last;

        if (!_tradingRuntime.IsRunning)
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

            _tradingRuntime.Start();
        }

        _log.LogInformation("Running backtest {RunId} from {Start} to {End} over {Count} elements", _config.RunId, first, last, _sorted.Count);

        // A streaming run continues where the last one stopped. Replaying the earlier batches sent the venue data it had
        // already matched against, so orders filled at prices that were history by the time they were placed.
        for (int i = _dispatched; i < _sorted.Count; i++)
        {
            IData element = _sorted[i];
            UnixNanos ts = element.CreatedTime;
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
            if (i + 1 >= _sorted.Count || _sorted[i + 1].CreatedTime != ts || _sorted[i + 1].CreatedTime > last)
            {
                SampleEquity(ts);
            }

            if (_config.StopOnRuntimeModuleFault && _tradingRuntime.ModuleHost.Strategies.Any(s => s.IsFaulted))
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
            _tradingRuntime.Stop();
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
        if (_tradingRuntime.IsRunning)
        {
            _tradingRuntime.Stop();
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
            _clock.SetTime(handler.Event.EventTime);
            RunDueCommands(handler.Event.EventTime);
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
                if (_exchanges.TryGetValue(quote.MarketKey.Venue, out SimulatedExchange? qx))
                {
                    qx.ProcessQuoteTick(quote);
                }

                break;
            case TradeTick trade:
                if (_exchanges.TryGetValue(trade.MarketKey.Venue, out SimulatedExchange? tx))
                {
                    tx.ProcessTradeTick(trade);
                }

                break;
            case Bar bar:
                if (_exchanges.TryGetValue(bar.CandleSeries.MarketKey.Venue, out SimulatedExchange? bx))
                {
                    bx.ProcessBar(bar);
                }

                break;
            case FundingRateUpdate funding:
                if (_exchanges.TryGetValue(funding.MarketKey.Venue, out SimulatedExchange? fx))
                {
                    fx.ProcessFundingRate(funding);
                }

                break;
            case OrderBookDelta or OrderBookDeltas or OrderBookDepth:
                // The venue matches from its high-priority subscription, before the strategies are notified.
                _tradingRuntime.MarketDataService.Process(element);
                return;
        }

        _tradingRuntime.MarketDataService.Process(element);
    }

    private void EnsureSorted()
    {
        if (_dataSorted)
        {
            return;
        }

        _sorted.Clear();
        _sortedOrder.Clear();
        foreach ((IData d, int i) in _data.Select((d, i) => (d, i)).OrderBy(x => x.d.CreatedTime).ThenBy(x => x.i))
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
        while (count < _sorted.Count && (_sorted[count].CreatedTime < ts || (_sorted[count].CreatedTime == ts && _sortedOrder[count] <= order)))
        {
            count++;
        }

        return count;
    }

    private IReadOnlyList<IData> History(RequestCommand request)
    {
        EnsureSorted();
        UnixNanos now = _clock.Timestamp;
        IEnumerable<IData> query = _sorted.Where(d => d.CreatedTime <= now);
        query = request switch
        {
            RequestBars bars => query.OfType<Bar>().Where(b => b.CandleSeries == bars.CandleSeries).Cast<IData>(),
            RequestQuoteTicks quotes => query.OfType<QuoteTick>().Where(q => q.MarketKey == quotes.MarketKey).Cast<IData>(),
            RequestTradeTicks trades => query.OfType<TradeTick>().Where(t => t.MarketKey == trades.MarketKey).Cast<IData>(),
            _ => [],
        };

        if (request.Start is { } s)
        {
            query = query.Where(d => d.CreatedTime >= s);
        }

        if (request.End is { } e)
        {
            query = query.Where(d => d.CreatedTime <= e);
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
            foreach ((Currency currency, Money unrealized) in _tradingRuntime.Portfolio.UnrealizedPnls(exchange.Venue))
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
        _tradingRuntime.Reset();
        foreach (SimulatedExchange exchange in _exchanges.Values)
        {
            exchange.Reset();
        }

        foreach (Instrument instrument in _tradingRuntime.Cache.Instruments())
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

    public void ClearStrategies() => _tradingRuntime.ModuleHost.Clear();

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tradingRuntime.Dispose();
    }
}
