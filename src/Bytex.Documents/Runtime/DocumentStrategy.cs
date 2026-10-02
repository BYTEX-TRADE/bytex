using System.Globalization;
using System.Text;
using System.Text.Json;
using Bytex.Core.Caching;
using Bytex.Core.Indicators;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Portfolios;
using Bytex.Core.Timing;
using Bytex.Core.Trading;
using Bytex.Documents.Annotations;
using Bytex.Documents.Catalog;
using Bytex.Documents.Schema;
using Bytex.Documents.Validation;
using Microsoft.Extensions.Logging;

namespace Bytex.Documents.Runtime;

public sealed record DocumentStrategyConfig : StrategyConfig
{
    public required StrategyDocument Document { get; init; }

    /// <summary>Parameter values that replace the document's defaults (sweeps).</summary>
    public IReadOnlyDictionary<string, decimal>? ParameterOverrides { get; init; }

    /// <summary>Publish <see cref="StrategyEvent"/>s on the message bus (monitors, assistant).</summary>
    public bool EmitDecisionEvents { get; init; } = true;

    /// <summary>How many decision events to keep in memory for reports.</summary>
    public int DecisionHistory { get; init; } = 200_000;

    /// <summary>Annotations older than this are dropped from the runtime's window.</summary>
    public TimeSpan AnnotationWindow { get; init; } = TimeSpan.FromDays(3);

    /// <summary>
    /// The context the strategy runs in, when the payload names one. Null - the usual case - means the tradingRuntime's own
    /// environment is used, so a document under a live node follows live rules without the payload having to repeat
    /// what the node already knows. The runtime uses it for rules that differ by mode, such as the live rule on
    /// conditions gated by events a model classified.
    /// </summary>
    public TradingEnvironment? Environment { get; init; }

    /// <summary>
    /// Closed bars to evaluate per bar type before the first live bar, without placing orders, so indicators, levels and
    /// counters are ready when the stream starts. Null: what the document needs. Zero: no warm-up.
    /// </summary>
    public int? WarmupBars { get; init; }

    /// <summary>
    /// Warm-up bars supplied by the host: closed bars of any of the document's bar types. When null, a sandbox or live
    /// strategy asks its data client for them; a backtest does not warm up at all, so its results do not change.
    /// </summary>
    public IReadOnlyList<Bar>? WarmupHistory { get; init; }

    /// <summary>How long to wait for the data client's history before going on without it.</summary>
    public TimeSpan WarmupTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Runs a strategy document on the engine: compiles the graph once, evaluates it on every primary bar close,
/// drives the phase machine, and turns node actions into orders through the ordinary Strategy API.
/// </summary>
public sealed class DocumentStrategy : Strategy<DocumentStrategyConfig>, IStrategyMonitorView, INeedsOrderSchedules
{
    private readonly NodeCatalog _catalog;
    private readonly Frame _frame = new();
    private readonly List<StrategyEvent> _decisions = new();
    private readonly List<Annotation> _annotations = new();
    private readonly Dictionary<CandleSeries, List<IBarObserver>> _barObservers = new();
    private readonly List<IOrderEventObserver> _orderObservers = new();
    private readonly List<IPositionEventObserver> _positionObservers = new();
    private readonly List<IAnnotationObserver> _annotationObservers = new();
    private EvaluationPlan? _plan;
    private ServicesImpl? _services;
    private string? _phase;
    private long _index;

    /// <summary>
    /// The last CLOSED bar of the primary type, and the reason a tick frame can only happen after one. A frame whose
    /// bar is a default struct would hand every node a bar of zeroes, and half of them would act on it (R13.7).
    /// </summary>
    private Bar? _lastBar;

    /// <summary>How many frames each trigger has produced, for a run to report what tick evaluation cost it.</summary>
    private long _barFrames;
    private long _tickFrames;
    private string? _stopReason;
    private bool _hadPosition;
    private JsonElement? _pendingState;
    private readonly Dictionary<CandleSeries, List<IIndicator>> _indicators = new();
    private readonly Dictionary<CandleSeries, WarmupProgress> _warmup = new();
    private readonly Dictionary<Guid, CandleSeries> _warmupRequests = new();
    private readonly List<Bar> _warmupBars = new();

    /// <summary>
    /// What every node produced on each bar of the warm-up, oldest first.
    ///
    /// <para>
    /// These values exist while the runtime replays history and used to be discarded, so a chart could only begin
    /// its line where the node started. On a five-minute bar that leaves an indicator unverifiable for an hour -
    /// and an indicator nobody can check by eye is exactly what a person is trying to see. Bounded by the warm-up
    /// itself, which replays a fixed number of bars, so this is not a rolling history and does not grow while the
    /// node runs.
    /// </para>
    /// </summary>
    private readonly List<(long BarTs, IReadOnlyDictionary<string, string> Values)> _warmupValues = new();
    private readonly List<Bar> _heldBack = new();
    private readonly List<DataResponse> _earlyResponses = new();
    private bool _requestingWarmup;
    private bool _warmupScheduled;
    private bool _warming;
    private bool _restored;
    private volatile FrameSnapshot? _lastFrame;
    private volatile IReadOnlyList<WarmupState> _warmupView = [];

    private const string WarmupTimer = "document-warmup-timeout";
    private const string WarmupStartTimer = "document-warmup-start";
    private const long WarmupStartDelayNanos = 1_000_000_000L;

    private sealed class WarmupProgress
    {
        public int Needed;
        public int Received;
        public int FromHistory;
        public UnixNanos? First;
        public UnixNanos? Last;
        public bool Pending;
        public string? Failure;
    }

    public DocumentStrategy(DocumentStrategyConfig config)
        : this(config, NodeCatalog.Default)
    {
    }

    public DocumentStrategy(DocumentStrategyConfig config, NodeCatalog catalog)
        : base(config)
    {
        ArgumentNullException.ThrowIfNull(config.Document);
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        ValidationReport report = new DocumentValidator(_catalog).Validate(config.Document);
        if (!report.IsValid)
        {
            throw new InvalidOperationException("The document is invalid: " + string.Join("; ", report.Blocks.Select(b => b.ToString())));
        }
    }

    public StrategyDocument Document => Config.Document;

    /// <summary>
    /// The execution algorithms this document's orders name, so the moduleHost has them running before the first order is
    /// sent: an order naming an algorithm nobody registered is denied, and the point of the parameter is that a
    /// document can ask for one without its host knowing anything about it.
    /// <para>
    /// The pace is the order's own - every worked order carries its horizon and its interval - so one algorithm at its
    /// defaults serves every node in the document. A host that wants its own tuned algorithm registers it under the
    /// same id, and that one runs instead.
    /// </para>
    /// </summary>
    public IEnumerable<OrderSchedule> RequiredOrderSchedules()
    {
        IReadOnlyDictionary<string, decimal> parameters = DocumentValidator.ResolveParameters(Document, Config.ParameterOverrides);
        IReadOnlyDictionary<string, string> texts = DocumentValidator.ResolveTextParameters(Document);
        bool twap = Document.Nodes.Any(n =>
            !n.Disabled && new NodeParams(n.Params, parameters, texts).Obj(OrderWork.Param).Str(OrderWork.Algorithm, OrderWork.None) == OrderWork.Twap);
        if (twap)
        {
            yield return new TwapSchedule();
        }
    }

    /// <summary>The compiled plan; available after the strategy starts.</summary>
    public EvaluationPlan? Plan => _plan;

    public string? CurrentPhase => _phase;

    public long BarIndex => _index;

    /// <summary>How many frames closed bars produced.</summary>
    public long BarFrames => _barFrames;

    /// <summary>
    /// How many frames ticks produced (R13.7). Zero for a document evaluated at the bar close, which is every
    /// document that does not ask otherwise. Read beside <see cref="BarFrames"/> it is what the choice cost.
    /// </summary>
    public long TickFrames => _tickFrames;

    /// <summary>Warm-up progress per bar type; replaced as a whole after every bar, safe to read from any thread.</summary>
    public IReadOnlyList<WarmupState> Warmup => _warmupView;

    /// <summary>True once every bar type has seen as many closed bars as the document needs.</summary>
    public bool IsWarmedUp => _warmupView.All(w => w.Done);

    /// <summary>True while the strategy waits for the data client's history; live bars are held back until it arrives.</summary>
    public bool WarmupPending => _warmupScheduled || _warmupRequests.Count > 0;

    /// <summary>The condition nodes as the last evaluated frame left them; replaced as a whole, safe to read from any thread.</summary>
    public FrameSnapshot? LastFrame => _lastFrame;

    /// <summary>
    /// What every node published on the last evaluated bar, keyed <c>nodeId:port</c>. A monitor reads it to show one
    /// node's outputs, and it is how a node's description is held to what the node actually sets.
    /// </summary>
    public IReadOnlyDictionary<string, object?> LastValues => _frame.Values;

    /// <summary>
    /// What every node produced on each bar the warm-up replayed, oldest first, keyed <c>nodeId:port</c>. Empty
    /// until warm-up finishes and never added to afterwards - the live stream carries on from where this ends.
    /// </summary>
    public IReadOnlyList<(long BarTs, IReadOnlyDictionary<string, string> Values)> WarmupValues => _warmupValues;

    /// <summary>The same state as plain values, for a monitor in another process (the node control channel's <c>view</c>).</summary>
    public object? MonitorView()
    {
        if (_plan is not { } plan)
        {
            return null;
        }

        IReadOnlyList<WarmupState> warmup = _warmupView;
        FrameSnapshot? frame = _lastFrame;
        return new DocumentMonitorView(
            plan.PrimaryInstrument.ToString(),
            plan.PrimaryCandleSeries.ToString(),
            plan.PrimaryCandleSeries.Spec.IntervalNanos / UnixNanos.NanosPerSecond,
            warmup.FirstOrDefault(w => w.CandleSeries == plan.PrimaryCandleSeries)?.Last?.Value,
            _phase,
            _index,
            WarmupPending,
            warmup.All(w => w.Done),
            warmup.Select(w => new DocumentMonitorWarmup(w.CandleSeries.ToString(), w.Needed, w.Received, w.FromHistory, w.Done, w.First?.Value, w.Last?.Value)).ToList(),
            frame is null ? null : new DocumentMonitorFrame(frame.Index, frame.BarTs.Value, frame.Phase, frame.Conditions, frame.Values),
            [.. _warmupValues.Select(p => new DocumentMonitorWarmupPoint(p.BarTs, p.Values))]);
    }

    /// <summary>Why the strategy asked to stop, if it did (for example "finished" for fired-once runs).</summary>
    public string? StopReason => _stopReason;

    /// <summary>Every decision event the runtime produced, newest last, bounded by <see cref="DocumentStrategyConfig.DecisionHistory"/>.</summary>
    public IReadOnlyList<StrategyEvent> Decisions => _decisions;

    // ----- Lifecycle -----

    /// <summary>
    /// Whether the leverage this document requires has been checked against what the venue grants. The account is
    /// not always in the cache when a strategy starts - a live node learns it from the venue - so the check runs
    /// again on data until an account is there to check against.
    /// </summary>
    private bool _leverageChecked;

    /// <summary>
    /// Refuses to trade a strategy the venue cannot carry. A document asking for ten times leverage on a venue that
    /// grants one is not a strategy running conservatively: it is a different strategy, sized a tenth of what it was
    /// written for, and a run that quietly did that would be read as evidence about the strategy somebody wrote.
    /// So it faults instead, naming both numbers.
    /// </summary>
    private void CheckLeverage()
    {
        decimal required = Document.Account.Leverage;
        if (_leverageChecked || required <= 1m)
        {
            _leverageChecked = true;
            return;
        }

        foreach (InstrumentRef declared in Document.Instruments)
        {
            if (!MarketKey.TryParse(declared.MarketKey, out MarketKey id))
            {
                continue;
            }

            if (Cache.AccountForVenue(id.Venue) is not { } account)
            {
                // Nothing to check against yet; a live node is still learning its account from the venue.
                return;
            }

            decimal granted = account is Core.Model.Accounts.MarginAccount margin ? margin.Leverage(id) : 1m;
            if (granted >= required)
            {
                continue;
            }

            _leverageChecked = true;
            Log.LogError(
                "Strategy {StrategyId} is written for {Required}x leverage on {MarketKey} and this venue grants {Granted}x. "
                + "Sized at {Granted}x it is not the strategy the document describes, so it will not be traded. "
                + "Raise the venue's leverage for the instrument, or set account.leverage to {Granted} in the document.",
                StrategyId, required, id, granted, granted, granted);
            Fault();
            return;
        }

        _leverageChecked = true;
    }

    /// <summary>
    /// The environment the rules follow: what the payload asked for, or failing that what the tradingRuntime running this
    /// strategy is. A document that says nothing under a live node is live, which is what the node already knew.
    /// </summary>
    internal TradingEnvironment Mode => Config.Environment ?? RunningIn;

    protected override void OnStart()
    {
        _services = new ServicesImpl(this);
        _plan = EvaluationPlan.Compile(Document, _catalog, Config.ParameterOverrides, _services);
        _barObservers.Clear();
        _indicators.Clear();
        _orderObservers.Clear();
        _positionObservers.Clear();
        _annotationObservers.Clear();

        foreach (PlannedNode node in _plan.Nodes)
        {
            if (node.Evaluator is IIndicatorHost host)
            {
                // Updated here rather than by the runtimeModule, so that warm-up bars and live bars held back behind them reach the
                // indicators in time order.
                foreach ((CandleSeries candleSeries, IIndicator indicator) in host.Indicators)
                {
                    (_indicators.TryGetValue(candleSeries, out List<IIndicator>? own) ? own : _indicators[candleSeries] = new List<IIndicator>()).Add(indicator);
                }
            }

            if (node.Evaluator is IBarObserver observer && node.SourceCandleSeries is { } bt)
            {
                (_barObservers.TryGetValue(bt, out List<IBarObserver>? list) ? list : _barObservers[bt] = new List<IBarObserver>()).Add(observer);
            }

            if (node.Evaluator is IOrderEventObserver o)
            {
                _orderObservers.Add(o);
            }

            if (node.Evaluator is IPositionEventObserver p)
            {
                _positionObservers.Add(p);
            }

            if (node.Evaluator is IAnnotationObserver a)
            {
                _annotationObservers.Add(a);
            }
        }

        foreach (CandleSeries candleSeries in _plan.CandleSeriesDefinitions.Values.Distinct())
        {
            SubscribeBars(candleSeries);
        }

        foreach (MarketKey marketKey in _plan.Instruments)
        {
            if (_plan.NeedsQuotes)
            {
                SubscribeQuoteTicks(marketKey);
            }

            if (_plan.NeedsTrades)
            {
                SubscribeTradeTicks(marketKey);
            }

            if (_plan.NeedsBook)
            {
                SubscribeOrderBookDeltas(marketKey);
            }

            if (_plan.NeedsMarkPrice)
            {
                SubscribeMarkPrices(marketKey);
            }

            if (_plan.NeedsFunding)
            {
                SubscribeFundingRates(marketKey);
            }
        }

        if (_plan.NeedsAnnotations)
        {
            SubscribeData<Annotation>();
        }

        _restored = _pendingState is not null;
        if (_pendingState is { } state)
        {
            RestoreState(state);
            _pendingState = null;
        }
        else
        {
            EnterPhase(_plan.InitialPhase, initial: true);
        }

        Emit("lifecycle", "strategy", $"started with {_plan.Nodes.Count} nodes on {_plan.PrimaryCandleSeries}");
        StartWarmup();
    }

    protected override void OnStop()
    {
        // Before anything else: a node that has orders resting on the venue gets to take them back, while the strategy
        // can still submit. Leaving them out there is a position nobody is managing any more.
        if (_plan is not null)
        {
            foreach (PlannedNode node in _plan.Nodes)
            {
                (node.Evaluator as IStoppableNode)?.OnStrategyStopping();
            }
        }

        CancelTimer(WarmupStartTimer);
        CancelTimer(WarmupTimer);
        _warmupScheduled = false;
        _warmupRequests.Clear();
        Emit("lifecycle", "strategy", "stopped" + (_stopReason is null ? string.Empty : $" ({_stopReason})"));
    }

    protected override void OnReset()
    {
        _index = 0;
        _lastBar = null;
        _barFrames = 0;
        _tickFrames = 0;
        _phase = null;
        _stopReason = null;
        _hadPosition = false;
        _decisions.Clear();
        _annotations.Clear();
        _frame.Clear();
        _warmup.Clear();
        _warmupRequests.Clear();
        _warmupBars.Clear();
        _warmupValues.Clear();
        _heldBack.Clear();
        _earlyResponses.Clear();
        _requestingWarmup = false;
        _warmupScheduled = false;
        _warming = false;
        _lastFrame = null;
        _warmupView = [];
    }

    // ----- Data -----

    protected override void OnBar(Bar bar)
    {
        if (_plan is null)
        {
            return;
        }

        if (!_leverageChecked)
        {
            CheckLeverage();
            if (!IsRunning)
            {
                return;
            }
        }

        if (_warmupScheduled)
        {
            // The first live bar came before the request went out: it goes out now, and this bar waits behind the history.
            _heldBack.Add(bar);
            RequestWarmup(bar);
            return;
        }

        if (_warmupRequests.Count > 0)
        {
            // History is on its way; this bar is newer than all of it and waits its turn.
            _heldBack.Add(bar);
            return;
        }

        ProcessBar(bar, warm: false);
    }

    /// <summary>
    /// A quote of the primary instrument, where the document asked to be evaluated on one (R13.7). Other instruments'
    /// quotes reach the cache the same as ever and start nothing: one graph, one clock, and the primary instrument is
    /// what the document's bars and orders are about.
    /// </summary>
    protected override void OnQuoteTick(QuoteTick tick)
    {
        if (_plan is { EvaluatesOnQuotes: true } plan && tick.MarketKey == plan.PrimaryInstrument)
        {
            EvaluateTick(FrameTrigger.Quote, tick, null);
        }
    }

    /// <summary>A trade print of the primary instrument, where the document asked to be evaluated on one (R13.7).</summary>
    protected override void OnTradeTick(TradeTick tick)
    {
        if (_plan is { EvaluatesOnTrades: true } plan && tick.MarketKey == plan.PrimaryInstrument)
        {
            EvaluateTick(FrameTrigger.Trade, null, tick);
        }
    }

    /// <summary>
    /// A frame that no bar closed for. It is deliberately the same walk over the same nodes: what differs is that the
    /// bar is the last closed one, no indicator and no rolling window has moved, and the bar index does not advance -
    /// so everything that counts bars still counts bars.
    /// </summary>
    private void EvaluateTick(FrameTrigger trigger, QuoteTick? quote, TradeTick? trade)
    {
        // Not before the first bar has closed, not while history is still arriving, and not before the run is live:
        // a tick frame during warm-up would act on figures the warm-up has not finished filling.
        if (_lastBar is not { } bar || _warming || _warmupScheduled || _warmupRequests.Count > 0 || !IsRunning)
        {
            return;
        }

        if (!_leverageChecked)
        {
            CheckLeverage();
            if (!IsRunning)
            {
                return;
            }
        }

        EvaluateFrame(bar, warm: false, trigger: trigger, quote: quote, trade: trade);
    }

    private void ProcessBar(Bar bar, bool warm)
    {
        if (_warmup.TryGetValue(bar.CandleSeries, out WarmupProgress? progress))
        {
            if (progress.Last is { } last && bar.EventTime <= last)
            {
                return;
            }

            progress.Received++;
            progress.FromHistory += warm ? 1 : 0;
            progress.First ??= bar.EventTime;
            progress.Last = bar.EventTime;
        }

        if (_indicators.TryGetValue(bar.CandleSeries, out List<IIndicator>? indicators))
        {
            foreach (IIndicator indicator in indicators)
            {
                indicator.Update(bar);
            }
        }

        if (_barObservers.TryGetValue(bar.CandleSeries, out List<IBarObserver>? observers))
        {
            foreach (IBarObserver observer in observers)
            {
                observer.OnBar(bar);
            }
        }

        // A restored strategy keeps the phase and the node state it saved; its warm-up only refills indicators and levels.
        if (bar.CandleSeries == _plan!.PrimaryCandleSeries && !(warm && _restored))
        {
            EvaluateFrame(bar, warm);
        }

        if (!warm)
        {
            PublishWarmup();
        }
    }

    // ----- Warm-up -----

    private void StartWarmup()
    {
        EvaluationPlan plan = _plan!;
        _warmup.Clear();
        foreach ((CandleSeries candleSeries, int needed) in WarmupPlan.Needed(plan))
        {
            _warmup[candleSeries] = new WarmupProgress { Needed = Config.WarmupBars ?? needed };
        }

        PublishWarmup();
        if (_warmup.Values.All(w => w.Needed <= 0))
        {
            return;
        }

        if (Config.WarmupHistory is { } supplied)
        {
            _warmupBars.AddRange(supplied);
            FinishWarmup();
            return;
        }

        if (Mode == TradingEnvironment.Backtest)
        {
            return;
        }

        // An runtimeModule takes no messages until it is running: an answer that comes back at once (a backtest's does) would be lost
        // if the request went out from OnStart, and so is a timer that is already due (a live clock fires it on the spot).
        // The request therefore goes out a moment after the start, or with the first live bar if that comes sooner, and the
        // give-up timer is set now, so that whatever happens the strategy goes on live after the timeout.
        _warmupScheduled = true;
        SetTimeAlert(WarmupStartTimer, Clock.Timestamp.AddNanos(WarmupStartDelayNanos), _ => RequestWarmup(null));
        SetTimeAlert(WarmupTimer, Clock.Timestamp.AddNanos(WarmupStartDelayNanos + (long)Config.WarmupTimeout.TotalMilliseconds * 1_000_000L), _ => OnWarmupTimeout());
    }

    private void RequestWarmup(Bar? firstLive = null)
    {
        if (!_warmupScheduled)
        {
            return;
        }

        _warmupScheduled = false;
        CancelTimer(WarmupStartTimer);

        // A data client may answer before the request call returns; such answers wait until every request is known, so
        // that the replay starts once, with all bar types together.
        _requestingWarmup = true;
        foreach ((CandleSeries candleSeries, WarmupProgress progress) in _warmup.Where(w => w.Value.Needed > 0))
        {
            progress.Pending = true;
            Guid id = RequestBars(candleSeries, end: WarmupPlan.RequestEnd(candleSeries, Clock.Timestamp, firstLive), limit: WarmupPlan.RequestSize(progress.Needed));
            _warmupRequests[id] = candleSeries;
        }

        _requestingWarmup = false;
        List<DataResponse> early = _earlyResponses.ToList();
        _earlyResponses.Clear();
        foreach (DataResponse response in early)
        {
            OnDataResponse(response);
        }
    }

    protected override void OnDataResponse(DataResponse response)
    {
        if (_requestingWarmup)
        {
            _earlyResponses.Add(response);
            return;
        }

        if (!_warmupRequests.Remove(response.CorrelationId, out CandleSeries candleSeries))
        {
            return;
        }

        WarmupProgress progress = _warmup[candleSeries];
        progress.Pending = false;
        progress.Failure = response.Error is { Length: > 0 } error ? $"request failed: {error}" : null;
        _warmupBars.AddRange(response.Data.OfType<Bar>().Where(b => b.CandleSeries == candleSeries));
        if (_warmupRequests.Count == 0)
        {
            CancelTimer(WarmupTimer);
            FinishWarmup();
        }
    }

    private void OnWarmupTimeout()
    {
        if (_warmupScheduled)
        {
            // The request never went out; ask now and let the answer come when it comes, without holding live bars back.
            _warmupScheduled = false;
            foreach (WarmupProgress never in _warmup.Values.Where(w => w.Needed > 0))
            {
                never.Failure = string.Create(CultureInfo.InvariantCulture, $"the history request could not be sent within {Config.WarmupTimeout.TotalSeconds:0} s");
            }

            FinishWarmup();
            return;
        }

        if (_warmupRequests.Count == 0)
        {
            return;
        }

        foreach (CandleSeries candleSeries in _warmupRequests.Values)
        {
            _warmup[candleSeries].Pending = false;
            _warmup[candleSeries].Failure = string.Create(CultureInfo.InvariantCulture, $"no answer from the data client within {Config.WarmupTimeout.TotalSeconds:0} s");
        }

        _warmupRequests.Clear();
        FinishWarmup();
    }

    /// <summary>
    /// Replays the collected history in time order, the primary bar type last among bars that close together so that a
    /// frame sees the other bar types' values of the same moment, then the live bars that were held back behind it.
    /// </summary>
    private void FinishWarmup()
    {
        EvaluationPlan plan = _plan!;
        UnixNanos now = Clock.Timestamp;
        List<Bar> history = _warmupBars
            .Where(b => _warmup.ContainsKey(b.CandleSeries) && b.EventTime <= now && !b.IsRevision)
            .GroupBy(b => b.CandleSeries)
            .SelectMany(g => g.OrderBy(b => b.EventTime).TakeLast(WarmupPlan.RequestSize(Math.Max(1, _warmup[g.Key].Needed))))
            .OrderBy(b => b.EventTime).ThenBy(b => b.CandleSeries == plan.PrimaryCandleSeries ? 1 : 0)
            .ToList();
        _warmupBars.Clear();

        _warming = true;
        try
        {
            foreach (Bar bar in history)
            {
                ProcessBar(bar, warm: true);

                // Taken after the bar is evaluated, so these are the values that bar produced. Only the primary
                // bar type: a chart draws one series of candles, and mixing bar types into one line would put two
                // different clocks on one axis.
                if (bar.CandleSeries == plan.PrimaryCandleSeries)
                {
                    Dictionary<string, string> values = new(StringComparer.Ordinal);
                    foreach ((string key, object? value) in _frame.Values)
                    {
                        if (value is not null)
                        {
                            values[key] = WarmupPlan.Text(value);
                        }
                    }

                    _warmupValues.Add((bar.EventTime.Value, values));
                }
            }
        }
        finally
        {
            _warming = false;
        }

        foreach ((CandleSeries candleSeries, WarmupProgress p) in _warmup.Where(w => w.Value.Needed > 0))
        {
            Dictionary<string, string> values = new(StringComparer.Ordinal) { ["candleSeries"] = candleSeries.ToString(), ["needed"] = p.Needed.ToString(System.Globalization.CultureInfo.InvariantCulture), ["received"] = p.FromHistory.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            if (p.FromHistory >= p.Needed)
            {
                Emit("warmup", "strategy", $"warmed up on {p.FromHistory} bars {Stamp(p.First)} → {Stamp(p.Last)} (needs {p.Needed})", values);
            }
            else
            {
                Emit("warmup", "strategy", $"warm-up incomplete: {p.FromHistory} of {p.Needed} bars ({p.Failure ?? $"the data client returned {p.FromHistory}"})", values);
            }
        }

        if (history.Count > 0 && !_restored && _frame.Bar.CandleSeries == plan.PrimaryCandleSeries)
        {
            _lastFrame = WarmupPlan.Snapshot(plan, _frame, _phase);
        }

        PublishWarmup();
        List<Bar> held = _heldBack.OrderBy(b => b.EventTime).ToList();
        _heldBack.Clear();
        foreach (Bar bar in held)
        {
            ProcessBar(bar, warm: false);
        }
    }

    private static string Stamp(UnixNanos? ts) => ts is { } t ? t.ToDateTimeOffset().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) : "-";

    private void PublishWarmup() =>
        _warmupView = _warmup.Select(w => new WarmupState(w.Key, w.Value.Needed, w.Value.Received, w.Value.FromHistory, w.Value.Received >= w.Value.Needed, w.Value.First, w.Value.Last)).ToList();

    protected override void OnData(IData data)
    {
        if (data is Annotation annotation)
        {
            _annotations.Add(annotation);
            long cutoff = Clock.Timestamp.Value - (long)Config.AnnotationWindow.TotalSeconds * UnixNanos.NanosPerSecond;
            _annotations.RemoveAll(a => (a.TsEnd ?? a.EventTime).Value < cutoff);
            foreach (IAnnotationObserver observer in _annotationObservers)
            {
                observer.OnAnnotation(annotation);
            }
        }
    }

    protected override void OnOrderEvent(OrderEvent e)
    {
        foreach (IOrderEventObserver observer in _orderObservers)
        {
            observer.OnOrderEvent(e);
        }

        switch (e)
        {
            case OrderFilled f:
                Emit("fill", "strategy", $"{f.OrderSide} {f.LastQty} @ {f.LastPx} ({f.LiquiditySide})", new Dictionary<string, string>(StringComparer.Ordinal) { ["clientOrderId"] = f.ClientOrderId.Value, ["position"] = f.PositionId?.Value ?? string.Empty });
                break;
            case OrderDenied d:
                Emit("denied", "strategy", $"{d.ClientOrderId}: {d.Reason}");
                break;
            case OrderRejected r:
                Emit("rejected", "strategy", $"{r.ClientOrderId}: {r.Reason}");
                break;
            default:
                break;
        }
    }

    protected override void OnPositionEvent(PositionEvent e)
    {
        foreach (IPositionEventObserver observer in _positionObservers)
        {
            observer.OnPositionEvent(e);
        }

        if (e is PositionOpened)
        {
            _hadPosition = true;
        }

        if (e is PositionClosed && _hadPosition && !Document.Repeat.Enabled && _stopReason is null)
        {
            _stopReason = "finished";
            Emit("complete", "strategy", "fired-once strategy: position closed, run complete");
        }
    }

    // ----- Evaluation -----

    private void EvaluateFrame(Bar bar, bool warm = false, FrameTrigger trigger = FrameTrigger.BarClose, QuoteTick? quote = null, TradeTick? trade = null)
    {
        EvaluationPlan plan = _plan!;
        _frame.Clear();
        _frame.Bar = bar;

        // The index of the bar this frame belongs to: the one that just closed, or - inside a bar - the last one that
        // did (R13.7). Without the step back a tick frame would report the index of the bar that has not closed yet,
        // and every "after N bars" in the catalog would fire a bar early.
        _frame.Index = trigger == FrameTrigger.BarClose ? _index : _index - 1;
        _frame.Trigger = trigger;
        _frame.Quote = quote;
        _frame.Trade = trade;
        if (trigger == FrameTrigger.BarClose)
        {
            _lastBar = bar;
        }

        foreach (PlannedNode node in plan.Nodes)
        {
            if (node.PhaseId is { } phase && phase != _phase)
            {
                continue;
            }

            EvalContext ctx = new(_frame, node.Node.Id, node.Inputs, _services!);
            if (warm && node.Descriptor.Kind == NodeKind.Action)
            {
                // A warm-up bar places nothing. The action only learns what its trigger looked like, so that a condition
                // which was already true before the start does not fire on the first live bar; a fresh rising edge does.
                (node.Evaluator as NodeBase)?.Prime(ctx);
                continue;
            }

            node.Evaluator.Evaluate(ctx);
        }

        // A position can open and close inside one bar, so the pulse that leaves a phase may already be in the frame that
        // entered it; it would be gone by the next frame and the phase would never be left. The phase just entered is
        // therefore checked against the same frame. A pulse moves the phase once per frame, which also bounds the walk.
        HashSet<string>? used = null;
        while (_phase is { } current && plan.Transitions.TryGetValue(current, out IReadOnlyList<TransitionDef>? transitions))
        {
            TransitionDef? next = transitions.FirstOrDefault(t => used?.Contains(t.On) != true && _frame.TryGet(t.On, out object? value) && value is true);
            if (next is null)
            {
                break;
            }

            (used ??= new HashSet<string>(StringComparer.Ordinal)).Add(next.On);
            Emit("transition", next.On, $"phase {next.From} → {next.To}");
            EnterPhase(next.To, initial: false);
        }

        // Only a closed bar advances the bar index, and only a closed bar counts as a bar (R13.7). A tick frame that
        // moved the index would silently turn every "after N bars" in the document into "after N ticks".
        if (trigger == FrameTrigger.BarClose)
        {
            _index++;
            _barFrames++;
        }
        else
        {
            _tickFrames++;
        }

        if (!warm)
        {
            _lastFrame = WarmupPlan.Snapshot(plan, _frame, _phase);
        }

        if (_stopReason is not null && IsRunning)
        {
            Post(() =>
            {
                if (IsRunning)
                {
                    Stop();
                }
            });
        }
    }

    private void EnterPhase(string? phaseId, bool initial)
    {
        EvaluationPlan plan = _plan!;
        if (!initial && _phase is { } old)
        {
            foreach (PlannedNode node in plan.Nodes.Where(n => n.PhaseId == old))
            {
                (node.Evaluator as IPhaseAware)?.OnPhaseExited();
            }
        }

        _phase = phaseId;
        if (phaseId is not null)
        {
            foreach (PlannedNode node in plan.Nodes.Where(n => n.PhaseId == phaseId))
            {
                (node.Evaluator as IPhaseAware)?.OnPhaseEntered();
            }
        }
    }

    private void Emit(string kind, string nodeId, string message, IReadOnlyDictionary<string, string>? values = null)
    {
        if (_warming)
        {
            return;
        }

        StrategyEvent e = new(StrategyId.Value, kind, nodeId, message, values ?? new Dictionary<string, string>(StringComparer.Ordinal), Clock.Timestamp, Clock.Timestamp);
        if (_decisions.Count >= Config.DecisionHistory)
        {
            _decisions.RemoveRange(0, Math.Max(1, Config.DecisionHistory / 10));
        }

        _decisions.Add(e);
        if (Config.EmitDecisionEvents && IsRegistered)
        {
            PublishData(e);
        }
    }

    // ----- State -----

    protected override IDictionary<string, byte[]> OnSave()
    {
        Dictionary<string, byte[]> state = new(StringComparer.Ordinal);
        if (_plan is null)
        {
            return state;
        }

        Dictionary<string, JsonElement> nodes = new(StringComparer.Ordinal);
        foreach (PlannedNode node in _plan.Nodes)
        {
            if (node.Evaluator is IStatefulNode stateful)
            {
                nodes[node.Node.Id] = stateful.SaveState();
            }
        }

        var payload = new { phase = _phase, index = _index, hadPosition = _hadPosition, nodes };
        state["document"] = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        return state;
    }

    protected override void OnLoad(IDictionary<string, byte[]> state)
    {
        if (!state.TryGetValue("document", out byte[]? bytes))
        {
            return;
        }

        using JsonDocument doc = JsonDocument.Parse(bytes);
        JsonElement root = doc.RootElement.Clone();
        if (_plan is null)
        {
            _pendingState = root;
        }
        else
        {
            RestoreState(root);
        }
    }

    private void RestoreState(JsonElement root)
    {
        EvaluationPlan plan = _plan!;
        _index = root.TryGetProperty("index", out JsonElement i) && i.ValueKind == JsonValueKind.Number ? i.GetInt64() : 0;
        _hadPosition = root.TryGetProperty("hadPosition", out JsonElement h) && h.ValueKind == JsonValueKind.True;
        string? phase = root.TryGetProperty("phase", out JsonElement p) && p.ValueKind == JsonValueKind.String ? p.GetString() : plan.InitialPhase;
        _phase = phase;
        if (root.TryGetProperty("nodes", out JsonElement nodes) && nodes.ValueKind == JsonValueKind.Object)
        {
            foreach (PlannedNode node in plan.Nodes)
            {
                if (node.Evaluator is IStatefulNode stateful && nodes.TryGetProperty(node.Node.Id, out JsonElement s))
                {
                    stateful.LoadState(s);
                }
            }
        }

        Emit("lifecycle", "strategy", $"state restored: phase {_phase}, bar {_index}");
    }

    // ----- Services -----

    private sealed class ServicesImpl : IStrategyServices
    {
        private readonly DocumentStrategy _s;

        public ServicesImpl(DocumentStrategy strategy)
        {
            _s = strategy;
        }

        public IClock Clock => _s.Clock;

        public ICache Cache => _s.Cache;

        public IPortfolio Portfolio => _s.Portfolio;

        public ILogger Log => _s.Log;

        public StrategyId StrategyId => _s.StrategyId;

        public OrderFactory OrderFactory => _s.OrderFactory;

        public TradingEnvironment Environment => _s.Mode;

        public IReadOnlyList<Annotation> Annotations => _s._annotations;

        // A warm-up frame has no effect outside the strategy, whatever node asks for one.
        public void SubmitOrder(Order order)
        {
            if (!_s._warming)
            {
                _s.SubmitOrder(order);
            }
        }

        public void SubmitOrderList(OrderList list)
        {
            if (!_s._warming)
            {
                _s.SubmitOrderList(list);
            }
        }

        public void ModifyOrder(Order order, Quantity? quantity = null, Price? price = null, Price? triggerPrice = null)
        {
            if (!_s._warming)
            {
                _s.ModifyOrder(order, quantity, price, triggerPrice);
            }
        }

        public void CancelOrder(Order order)
        {
            if (!_s._warming)
            {
                _s.CancelOrder(order);
            }
        }

        public void CancelAllOrders(MarketKey marketKey)
        {
            if (!_s._warming)
            {
                _s.CancelAllOrders(marketKey);
            }
        }

        public void CloseAllPositions(MarketKey marketKey)
        {
            if (!_s._warming)
            {
                _s.CloseAllPositions(marketKey);
            }
        }

        public void PublishSignal(string name, decimal value)
        {
            if (!_s._warming)
            {
                _s.PublishSignal(name, value);
            }
        }

        public void Emit(string nodeId, string kind, string message, IReadOnlyDictionary<string, string>? values = null) => _s.Emit(kind, nodeId, message, values);

        public void RequestStop(string reason)
        {
            if (!_s._warming)
            {
                _s._stopReason ??= reason;
            }
        }
    }
}
