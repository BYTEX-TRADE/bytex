using Bytex.Core.Caching;
using Bytex.Core.Common;
using Bytex.Core.Indicators;
using Bytex.Core.Messaging;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Portfolios;
using Bytex.Core.Timing;
using Microsoft.Extensions.Logging;

namespace Bytex.Core.Trading;

public record ActorConfig
{
    public ActorId? ActorId { get; init; }

    public bool LogEvents { get; init; } = true;

    public bool LogCommands { get; init; } = true;
}

/// <summary>
/// Base class for anything that subscribes to data and reacts to events: monitors, signal generators, strategies.
/// </summary>
public abstract class Actor : Component
{
    private readonly List<(string Topic, Action<object> Handler)> _subscriptions = new();
    private readonly Dictionary<InstrumentId, List<IIndicator>> _quoteIndicators = new();
    private readonly Dictionary<InstrumentId, List<IIndicator>> _tradeIndicators = new();
    private readonly Dictionary<BarType, List<IIndicator>> _barIndicators = new();
    private readonly List<IIndicator> _indicators = new();
    private readonly HashSet<Guid> _pendingRequests = new();
    private ICache? _cache;
    private IMessageBus? _bus;
    private IPortfolio? _portfolio;
    private Action<Action>? _post;
    private bool _registered;

    protected Actor(ActorConfig? config = null)
        : base(new ComponentId((config?.ActorId ?? new ActorId(DefaultId(typeof(Actor), null))).Value))
    {
        Config = config ?? new ActorConfig();
        ActorId = Config.ActorId ?? new ActorId(DefaultId(GetType(), null));
        Id = new ComponentId(ActorId.Value);
    }

    /// <summary>What an unnamed component calls itself: its type and the first instance of it.</summary>
    private const string FirstInstanceSuffix = "-000";

    protected static string DefaultId(Type type, string? tag) => tag is null ? $"{type.Name}{FirstInstanceSuffix}" : $"{type.Name}-{tag}";

    public ActorConfig Config { get; }

    public ActorId ActorId { get; protected set; }

    public TraderId TraderId { get; private set; }

    public bool IsRegistered => _registered;

    protected ICache Cache => _cache ?? throw NotRegistered();

    protected IMessageBus MessageBus => _bus ?? throw NotRegistered();

    protected IPortfolio Portfolio => _portfolio ?? throw NotRegistered();

    protected IReadOnlyList<IIndicator> RegisteredIndicators => _indicators;

    protected bool IndicatorsInitialized => _indicators.Count > 0 && _indicators.All(i => i.IsInitialized);

    private InvalidOperationException NotRegistered() => new($"Actor {ActorId} has not been registered with a trader.");

    /// <summary>
    /// Called by the trader to wire the actor into a kernel.
    /// </summary>
    /// <summary>
    /// What this actor is running in, as the kernel that owns it was configured. Set when the trader registers it, so
    /// anything deciding behaviour by environment reads the truth rather than a default it was constructed with. Not
    /// called <c>Environment</c> on purpose: that name would shadow <see cref="System.Environment"/> inside every
    /// actor anybody writes.
    /// </summary>
    public TradingEnvironment RunningIn { get; internal set; } = TradingEnvironment.Backtest;

    public virtual void Register(TraderId traderId, IClock clock, ICache cache, IMessageBus bus, IPortfolio portfolio, ILoggerFactory loggerFactory, Action<Action>? post = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(portfolio);
        TraderId = traderId;
        _cache = cache;
        _bus = bus;
        _portfolio = portfolio;
        _post = post ?? (a => a());
        Initialize(clock, loggerFactory);
        _registered = true;
        OnRegistered();
    }

    protected virtual void OnRegistered()
    {
    }

    // ----- Lifecycle -----

    protected override void OnDispose()
    {
        foreach ((string topic, Action<object> handler) in _subscriptions)
        {
            _bus?.Unsubscribe(topic, handler);
        }

        _subscriptions.Clear();
    }

    /// <summary>Returns state to be persisted. Override to save strategy-specific state.</summary>
    protected virtual IDictionary<string, byte[]> OnSave() => new Dictionary<string, byte[]>(StringComparer.Ordinal);

    /// <summary>Restores state previously returned by <see cref="OnSave"/>.</summary>
    protected virtual void OnLoad(IDictionary<string, byte[]> state)
    {
    }

    public IDictionary<string, byte[]> Save()
    {
        IDictionary<string, byte[]> state = OnSave();
        Cache.SaveActorState(ActorId, state);
        return state;
    }

    public void Load()
    {
        IDictionary<string, byte[]>? state = Cache.LoadActorState(ActorId);
        if (state is not null)
        {
            OnLoad(state);
        }
    }

    // ----- Data handlers -----

    protected virtual void OnInstrument(Instrument instrument)
    {
    }

    protected virtual void OnQuoteTick(QuoteTick tick)
    {
    }

    protected virtual void OnTradeTick(TradeTick tick)
    {
    }

    protected virtual void OnBar(Bar bar)
    {
    }

    protected virtual void OnOrderBookDeltas(OrderBookDeltas deltas)
    {
    }

    protected virtual void OnOrderBook(OrderBook book)
    {
    }

    protected virtual void OnInstrumentStatus(InstrumentStatus status)
    {
    }

    protected virtual void OnMarkPrice(MarkPriceUpdate update)
    {
    }

    protected virtual void OnIndexPrice(IndexPriceUpdate update)
    {
    }

    protected virtual void OnFundingRate(FundingRateUpdate update)
    {
    }

    protected virtual void OnData(IData data)
    {
    }

    protected virtual void OnSignal(Signal signal)
    {
    }

    protected virtual void OnHistoricalData(IData data)
    {
    }

    protected virtual void OnDataResponse(DataResponse response)
    {
    }

    protected virtual void OnEvent(Event e)
    {
    }

    protected virtual void OnTimeEvent(TimeEvent e)
    {
    }

    // ----- Dispatch (public so the trader and tests can drive handlers) -----

    public void HandleInstrument(Instrument instrument) => Guarded(() => OnInstrument(instrument));

    public void HandleQuoteTick(QuoteTick tick) => Guarded(() =>
    {
        if (_quoteIndicators.TryGetValue(tick.InstrumentId, out List<IIndicator>? indicators))
        {
            foreach (IIndicator indicator in indicators)
            {
                indicator.Update(tick);
            }
        }

        OnQuoteTick(tick);
    });

    public void HandleTradeTick(TradeTick tick) => Guarded(() =>
    {
        if (_tradeIndicators.TryGetValue(tick.InstrumentId, out List<IIndicator>? indicators))
        {
            foreach (IIndicator indicator in indicators)
            {
                indicator.Update(tick);
            }
        }

        OnTradeTick(tick);
    });

    public void HandleBar(Bar bar) => Guarded(() =>
    {
        if (_barIndicators.TryGetValue(bar.BarType, out List<IIndicator>? indicators))
        {
            foreach (IIndicator indicator in indicators)
            {
                indicator.Update(bar);
            }
        }

        OnBar(bar);
    });

    public void HandleOrderBookDeltas(OrderBookDeltas deltas) => Guarded(() => OnOrderBookDeltas(deltas));

    public void HandleOrderBook(OrderBook book) => Guarded(() => OnOrderBook(book));

    public void HandleInstrumentStatus(InstrumentStatus status) => Guarded(() => OnInstrumentStatus(status));

    public void HandleData(IData data) => Guarded(() => OnData(data));

    public void HandleSignal(Signal signal) => Guarded(() => OnSignal(signal));

    public void HandleEvent(Event e) => Guarded(() =>
    {
        if (e is TimeEvent timeEvent)
        {
            OnTimeEvent(timeEvent);
        }

        OnEvent(e);
    });

    public void HandleDataResponse(DataResponse response) => Guarded(() =>
    {
        _pendingRequests.Remove(response.CorrelationId);
        foreach (IData item in response.Data)
        {
            switch (item)
            {
                case Bar bar when _barIndicators.TryGetValue(bar.BarType, out List<IIndicator>? bi):
                    foreach (IIndicator indicator in bi)
                    {
                        indicator.Update(bar);
                    }

                    break;
                case QuoteTick quote when _quoteIndicators.TryGetValue(quote.InstrumentId, out List<IIndicator>? qi):
                    foreach (IIndicator indicator in qi)
                    {
                        indicator.Update(quote);
                    }

                    break;
                case TradeTick trade when _tradeIndicators.TryGetValue(trade.InstrumentId, out List<IIndicator>? ti):
                    foreach (IIndicator indicator in ti)
                    {
                        indicator.Update(trade);
                    }

                    break;
            }

            OnHistoricalData(item);
        }

        OnDataResponse(response);
    });

    /// <summary>
    /// Runs a handler, faulting the actor (but not the kernel) if it throws.
    /// </summary>
    protected void Guarded(Action handler)
    {
        if (!IsRunning)
        {
            return;
        }

        try
        {
            handler();
        }
        catch (Exception e)
        {
            Log.LogError(e, "Actor {ActorId} handler threw; faulting actor", ActorId);
            Fault();
        }
    }

    // ----- Subscriptions -----

    private void SubscribeTopic(string topic, Action<object> handler)
    {
        MessageBus.Subscribe(topic, handler);
        _subscriptions.Add((topic, handler));
    }

    private void UnsubscribeTopic(string topic)
    {
        for (int i = _subscriptions.Count - 1; i >= 0; i--)
        {
            if (_subscriptions[i].Topic == topic)
            {
                MessageBus.Unsubscribe(topic, _subscriptions[i].Handler);
                _subscriptions.RemoveAt(i);
            }
        }
    }

    private void SendDataCommand(DataCommand command) => MessageBus.Send(Endpoints.DataEngineExecute, command);

    protected void SubscribeInstruments(Venue venue, ClientId? clientId = null)
    {
        SubscribeTopic(Topics.Instruments(venue), m => HandleInstrument((Instrument)m));
        SendDataCommand(new SubscribeInstruments(venue, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void SubscribeInstrument(InstrumentId instrumentId, ClientId? clientId = null)
    {
        SubscribeTopic(Topics.Instrument(instrumentId), m => HandleInstrument((Instrument)m));
        SendDataCommand(new SubscribeInstrument(instrumentId, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void SubscribeQuoteTicks(InstrumentId instrumentId, ClientId? clientId = null)
    {
        SubscribeTopic(Topics.Quotes(instrumentId), m => HandleQuoteTick((QuoteTick)m));
        SendDataCommand(new SubscribeQuoteTicks(instrumentId, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void SubscribeTradeTicks(InstrumentId instrumentId, ClientId? clientId = null)
    {
        SubscribeTopic(Topics.Trades(instrumentId), m => HandleTradeTick((TradeTick)m));
        SendDataCommand(new SubscribeTradeTicks(instrumentId, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void SubscribeBars(BarType barType, ClientId? clientId = null)
    {
        SubscribeTopic(Topics.Bars(barType), m => HandleBar((Bar)m));
        SendDataCommand(new SubscribeBars(barType, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void SubscribeOrderBookDeltas(InstrumentId instrumentId, BookType bookType = BookType.L2, int depth = 0, ClientId? clientId = null)
    {
        SubscribeTopic(Topics.BookDeltas(instrumentId), m => HandleOrderBookDeltas((OrderBookDeltas)m));
        SendDataCommand(new SubscribeOrderBookDeltas(instrumentId, bookType, depth, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void SubscribeOrderBook(InstrumentId instrumentId, BookType bookType = BookType.L2, int depth = 0, TimeSpan? interval = null, ClientId? clientId = null)
    {
        SubscribeTopic(Topics.BookSnapshots(instrumentId), m => HandleOrderBook((OrderBook)m));
        SendDataCommand(new SubscribeOrderBookSnapshots(instrumentId, bookType, depth, interval ?? TimeSpan.FromSeconds(1), clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void SubscribeInstrumentStatus(InstrumentId instrumentId, ClientId? clientId = null)
    {
        SubscribeTopic(Topics.Status(instrumentId), m => HandleInstrumentStatus((InstrumentStatus)m));
        SendDataCommand(new SubscribeInstrumentStatus(instrumentId, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void SubscribeMarkPrices(InstrumentId instrumentId, ClientId? clientId = null)
    {
        SubscribeTopic(Topics.MarkPrices(instrumentId), m => Guarded(() => OnMarkPrice((MarkPriceUpdate)m)));
        SendDataCommand(new SubscribeMarkPrices(instrumentId, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void SubscribeIndexPrices(InstrumentId instrumentId, ClientId? clientId = null)
    {
        SubscribeTopic(Topics.IndexPrices(instrumentId), m => Guarded(() => OnIndexPrice((IndexPriceUpdate)m)));
        SendDataCommand(new SubscribeIndexPrices(instrumentId, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void SubscribeFundingRates(InstrumentId instrumentId, ClientId? clientId = null)
    {
        SubscribeTopic(Topics.FundingRates(instrumentId), m => Guarded(() => OnFundingRate((FundingRateUpdate)m)));
        SendDataCommand(new SubscribeFundingRates(instrumentId, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void SubscribeData(DataType dataType, ClientId? clientId = null)
    {
        SubscribeTopic(Topics.Custom(dataType), m => HandleData((IData)m));
        SendDataCommand(new SubscribeData(dataType, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void SubscribeData<T>(IReadOnlyDictionary<string, string>? metadata = null, ClientId? clientId = null) where T : IData =>
        SubscribeData(DataType.Of<T>(metadata), clientId);

    protected void SubscribeSignal(string name) => SubscribeTopic(Topics.Signal(name), m => HandleSignal((Signal)m));

    protected void UnsubscribeInstruments(Venue venue, ClientId? clientId = null)
    {
        UnsubscribeTopic(Topics.Instruments(venue));
        SendDataCommand(new UnsubscribeInstruments(venue, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void UnsubscribeInstrument(InstrumentId instrumentId, ClientId? clientId = null)
    {
        UnsubscribeTopic(Topics.Instrument(instrumentId));
        SendDataCommand(new UnsubscribeInstrument(instrumentId, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void UnsubscribeQuoteTicks(InstrumentId instrumentId, ClientId? clientId = null)
    {
        UnsubscribeTopic(Topics.Quotes(instrumentId));
        SendDataCommand(new UnsubscribeQuoteTicks(instrumentId, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void UnsubscribeTradeTicks(InstrumentId instrumentId, ClientId? clientId = null)
    {
        UnsubscribeTopic(Topics.Trades(instrumentId));
        SendDataCommand(new UnsubscribeTradeTicks(instrumentId, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void UnsubscribeBars(BarType barType, ClientId? clientId = null)
    {
        UnsubscribeTopic(Topics.Bars(barType));
        SendDataCommand(new UnsubscribeBars(barType, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void UnsubscribeOrderBookDeltas(InstrumentId instrumentId, ClientId? clientId = null)
    {
        UnsubscribeTopic(Topics.BookDeltas(instrumentId));
        SendDataCommand(new UnsubscribeOrderBookDeltas(instrumentId, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void UnsubscribeOrderBook(InstrumentId instrumentId, ClientId? clientId = null)
    {
        UnsubscribeTopic(Topics.BookSnapshots(instrumentId));
        SendDataCommand(new UnsubscribeOrderBookSnapshots(instrumentId, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void UnsubscribeInstrumentStatus(InstrumentId instrumentId, ClientId? clientId = null)
    {
        UnsubscribeTopic(Topics.Status(instrumentId));
        SendDataCommand(new UnsubscribeInstrumentStatus(instrumentId, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void UnsubscribeMarkPrices(InstrumentId instrumentId, ClientId? clientId = null)
    {
        UnsubscribeTopic(Topics.MarkPrices(instrumentId));
        SendDataCommand(new UnsubscribeMarkPrices(instrumentId, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void UnsubscribeIndexPrices(InstrumentId instrumentId, ClientId? clientId = null)
    {
        UnsubscribeTopic(Topics.IndexPrices(instrumentId));
        SendDataCommand(new UnsubscribeIndexPrices(instrumentId, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void UnsubscribeFundingRates(InstrumentId instrumentId, ClientId? clientId = null)
    {
        UnsubscribeTopic(Topics.FundingRates(instrumentId));
        SendDataCommand(new UnsubscribeFundingRates(instrumentId, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void UnsubscribeData(DataType dataType, ClientId? clientId = null)
    {
        UnsubscribeTopic(Topics.Custom(dataType));
        SendDataCommand(new UnsubscribeData(dataType, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void UnsubscribeSignal(string name) => UnsubscribeTopic(Topics.Signal(name));

    // ----- Requests -----

    private Guid SendRequest(RequestCommand request)
    {
        EnsureResponseSubscription();
        _pendingRequests.Add(request.CommandId);
        MessageBus.Send(Endpoints.DataEngineRequest, request with { Requester = ActorId });
        return request.CommandId;
    }

    private bool _responseSubscribed;

    private void EnsureResponseSubscription()
    {
        if (_responseSubscribed)
        {
            return;
        }

        SubscribeTopic(Topics.DataResponses(ActorId), m => HandleDataResponse((DataResponse)m));
        _responseSubscribed = true;
    }

    protected bool HasPendingRequests => _pendingRequests.Count > 0;

    protected bool IsPendingRequest(Guid requestId) => _pendingRequests.Contains(requestId);

    protected Guid RequestInstrument(InstrumentId instrumentId, ClientId? clientId = null) =>
        SendRequest(new RequestInstrument(instrumentId, clientId, Guid.NewGuid(), Clock.Timestamp));

    protected Guid RequestInstruments(Venue venue, ClientId? clientId = null) =>
        SendRequest(new RequestInstruments(venue, clientId, Guid.NewGuid(), Clock.Timestamp));

    protected Guid RequestQuoteTicks(InstrumentId instrumentId, UnixNanos? start = null, UnixNanos? end = null, int? limit = null, ClientId? clientId = null) =>
        SendRequest(new RequestQuoteTicks(instrumentId, start, end, limit, clientId, Guid.NewGuid(), Clock.Timestamp));

    protected Guid RequestTradeTicks(InstrumentId instrumentId, UnixNanos? start = null, UnixNanos? end = null, int? limit = null, ClientId? clientId = null) =>
        SendRequest(new RequestTradeTicks(instrumentId, start, end, limit, clientId, Guid.NewGuid(), Clock.Timestamp));

    protected Guid RequestBars(BarType barType, UnixNanos? start = null, UnixNanos? end = null, int? limit = null, ClientId? clientId = null) =>
        SendRequest(new RequestBars(barType, start, end, limit, clientId, Guid.NewGuid(), Clock.Timestamp));

    protected Guid RequestData(DataType dataType, UnixNanos? start = null, UnixNanos? end = null, int? limit = null, ClientId? clientId = null) =>
        SendRequest(new RequestData(dataType, start, end, limit, clientId, Guid.NewGuid(), Clock.Timestamp));

    // ----- Publishing -----

    protected void PublishData(DataType dataType, IData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        MessageBus.Publish(Topics.Custom(dataType), data);
    }

    protected void PublishData<T>(T data, IReadOnlyDictionary<string, string>? metadata = null) where T : IData =>
        PublishData(DataType.Of<T>(metadata), data);

    protected void PublishSignal(string name, decimal value, UnixNanos? tsEvent = null)
    {
        UnixNanos now = Clock.Timestamp;
        MessageBus.Publish(Topics.Signal(name), new Signal(name, value, tsEvent ?? now, now));
    }

    // ----- Indicators -----

    protected void RegisterIndicatorForQuoteTicks(InstrumentId instrumentId, IIndicator indicator)
    {
        ArgumentNullException.ThrowIfNull(indicator);
        AddIndicator(_quoteIndicators, instrumentId, indicator);
    }

    protected void RegisterIndicatorForTradeTicks(InstrumentId instrumentId, IIndicator indicator)
    {
        ArgumentNullException.ThrowIfNull(indicator);
        AddIndicator(_tradeIndicators, instrumentId, indicator);
    }

    protected void RegisterIndicatorForBars(BarType barType, IIndicator indicator)
    {
        ArgumentNullException.ThrowIfNull(indicator);
        AddIndicator(_barIndicators, barType, indicator);
    }

    private void AddIndicator<TKey>(Dictionary<TKey, List<IIndicator>> map, TKey key, IIndicator indicator) where TKey : notnull
    {
        if (!map.TryGetValue(key, out List<IIndicator>? list))
        {
            list = new List<IIndicator>();
            map[key] = list;
        }

        if (!list.Contains(indicator))
        {
            list.Add(indicator);
        }

        if (!_indicators.Contains(indicator))
        {
            _indicators.Add(indicator);
        }
    }

    // ----- Timers -----

    protected void SetTimeAlert(string name, UnixNanos alertTime, Action<TimeEvent>? callback = null) =>
        Clock.SetTimeAlert(Scoped(name), alertTime, callback is null ? e => HandleEvent(e) : e => Guarded(() => callback(e)));

    protected void SetTimer(string name, TimeSpan interval, UnixNanos? start = null, UnixNanos? stop = null, Action<TimeEvent>? callback = null, bool fireImmediately = false) =>
        Clock.SetTimer(Scoped(name), interval, start, stop, callback is null ? e => HandleEvent(e) : e => Guarded(() => callback(e)), fireImmediately);

    protected void CancelTimer(string name) => Clock.CancelTimer(Scoped(name));

    private string Scoped(string name) => $"{ActorId}:{name}";

    // ----- Background work -----

    /// <summary>
    /// Runs work on the thread pool and reports failures through <paramref name="onError"/> on the kernel thread.
    /// </summary>
    protected void RunInBackground(Func<CancellationToken, Task> work, Action<Exception>? onError = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        _ = Task.Run(async () =>
        {
            try
            {
                await work(ct).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Post(() =>
                {
                    if (onError is not null)
                    {
                        onError(e);
                    }
                    else
                    {
                        Log.LogError(e, "Background work for {ActorId} failed", ActorId);
                    }
                });
            }
        }, ct);
    }

    /// <summary>
    /// Marshals an action onto the kernel thread (executes inline in backtests).
    /// </summary>
    protected void Post(Action action) => (_post ?? (a => a()))(action);

    public override string ToString() => $"{GetType().Name}({ActorId}, {State})";
}
