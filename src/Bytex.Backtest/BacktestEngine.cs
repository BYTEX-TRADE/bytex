using System.Diagnostics;
using Bytex.Core.Caching;
using Bytex.Core.Kernel;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
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
    private bool _accountsInitialized;
    private bool _disposed;

    public BacktestEngine(BacktestEngineConfig? config = null, ILoggerFactory? loggerFactory = null)
    {
        _config = config ?? new BacktestEngineConfig();
        LoggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _log = LoggerFactory.CreateLogger<BacktestEngine>();
        _clock = new TestClock();
        _kernel = new Kernel(_config.Kernel with { Environment = TradingEnvironment.Backtest }, _clock, LoggerFactory);
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

        foreach (IData element in _sorted)
        {
            UnixNanos ts = element.TsInit;
            if (ts < first)
            {
                continue;
            }

            if (ts > last)
            {
                break;
            }

            Advance(ts);
            Dispatch(element);
            Iteration++;
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
            foreach (SimulatedExchange exchange in _exchanges.Values)
            {
                exchange.ProcessDueCommands(handler.Event.TsEvent);
            }

            handler.Handle();
        }

        _clock.SetTime(to);
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
            case OrderBookDelta or OrderBookDeltas or OrderBookDepth:
                _kernel.DataEngine.Process(element);
                if (element.InstrumentId is { } bookId && _exchanges.TryGetValue(bookId.Venue, out SimulatedExchange? ox) && _kernel.Cache.OrderBook(bookId) is { } book)
                {
                    ox.ProcessOrderBook(book, element.TsEvent);
                }

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
        _sorted.AddRange(_data.Select((d, i) => (d, i)).OrderBy(x => x.d.TsInit).ThenBy(x => x.i).Select(x => x.d));
        _dataSorted = true;
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
        _dataSorted = false;
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
