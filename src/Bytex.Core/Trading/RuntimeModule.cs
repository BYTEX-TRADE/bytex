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

public record RuntimeModuleConfig
{
    public RuntimeModuleId? RuntimeModuleId { get; init; }

    public bool LogEvents { get; init; } = true;

    public bool LogCommands { get; init; } = true;
}

/// <summary>
/// Base class for anything that subscribes to data and reacts to events: monitors, signal generators, strategies.
/// </summary>
public abstract class RuntimeModule : Component
{
    private readonly List<(string Topic, Action<object> Handler)> _subscriptions = new();
    private readonly Dictionary<MarketKey, List<IIndicator>> _quoteIndicators = new();
    private readonly Dictionary<MarketKey, List<IIndicator>> _tradeIndicators = new();
    private readonly Dictionary<CandleSeries, List<IIndicator>> _barIndicators = new();
    private readonly Dictionary<MarketKey, List<IOrderBookIndicator>> _bookIndicators = new();
    private readonly List<IIndicator> _indicators = new();
    private readonly HashSet<Guid> _pendingRequests = new();
    private ICache? _cache;
    private IMessageBus? _bus;
    private IPortfolio? _portfolio;
    private Action<Action>? _post;
    private bool _registered;

    protected RuntimeModule(RuntimeModuleConfig? config = null)
        : base(new ComponentId((config?.RuntimeModuleId ?? new RuntimeModuleId(DefaultId(typeof(RuntimeModule), null))).Value))
    {
        Config = config ?? new RuntimeModuleConfig();
        RuntimeModuleId = Config.RuntimeModuleId ?? new RuntimeModuleId(DefaultId(GetType(), null));
        Id = new ComponentId(RuntimeModuleId.Value);
    }

    /// <summary>What an unnamed component calls itself: its type and the first instance of it.</summary>
    private const string FirstInstanceSuffix = "-000";

    protected static string DefaultId(Type type, string? tag) => tag is null ? $"{type.Name}{FirstInstanceSuffix}" : $"{type.Name}-{tag}";

    public RuntimeModuleConfig Config { get; }

    public RuntimeModuleId RuntimeModuleId { get; protected set; }

    public ModuleHostId ModuleHostId { get; private set; }

    public bool IsRegistered => _registered;

    protected ICache Cache => _cache ?? throw NotRegistered();

    protected IMessageBus MessageBus => _bus ?? throw NotRegistered();

    protected IPortfolio Portfolio => _portfolio ?? throw NotRegistered();

    protected IReadOnlyList<IIndicator> RegisteredIndicators => _indicators;

    protected bool IndicatorsInitialized => _indicators.Count > 0 && _indicators.All(i => i.IsInitialized);

    private InvalidOperationException NotRegistered() => new($"RuntimeModule {RuntimeModuleId} has not been registered with a moduleHost.");

    /// <summary>
    /// Called by the moduleHost to wire the runtimeModule into a tradingRuntime.
    /// </summary>
    /// <summary>
    /// What this runtimeModule is running in, as the tradingRuntime that owns it was configured. Set when the moduleHost registers it, so
    /// anything deciding behaviour by environment reads the truth rather than a default it was constructed with. Not
    /// called <c>Environment</c> on purpose: that name would shadow <see cref="System.Environment"/> inside every
    /// runtimeModule anybody writes.
    /// </summary>
    public TradingEnvironment RunningIn { get; internal set; } = TradingEnvironment.Backtest;

    public virtual void Register(ModuleHostId moduleHostId, IClock clock, ICache cache, IMessageBus bus, IPortfolio portfolio, ILoggerFactory loggerFactory, Action<Action>? post = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(portfolio);
        ModuleHostId = moduleHostId;
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
        Cache.SaveRuntimeModuleState(RuntimeModuleId, state);
        return state;
    }

    public void Load()
    {
        IDictionary<string, byte[]>? state = Cache.LoadRuntimeModuleState(RuntimeModuleId);
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

    /// <summary>
    /// A bar from a SUBSCRIPTION, as the venue closes it - or as it forms, when the data client is configured to
    /// forward revisions, in which case <see cref="Bar.IsRevision"/> tells the two apart.
    ///
    /// <para>
    /// <b>The answer to a REQUEST does not arrive here.</b> `RequestBars` delivers each bar to
    /// <see cref="OnHistoricalData"/> and then the whole response to <see cref="OnDataResponse"/>. The two
    /// handlers carry the same type for the same bar type, so overriding only this one leaves a request's
    /// answer silently unread - which looks exactly like a venue that returned nothing.
    /// </para>
    ///
    /// <para>
    /// Indicators registered for the bar type are updated from BOTH, so an runtimeModule that only wants them warmed
    /// needs neither handler: the request alone does it.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// One item of a REQUEST's answer - a bar from `RequestBars`, a tick from `RequestTradeTicks`, and so on -
    /// called once per item before <see cref="OnDataResponse"/> is called once for the response as a whole.
    ///
    /// <para>
    /// <b>Subscriptions do not arrive here</b>, and requests do not arrive at <see cref="OnBar"/> or the other
    /// typed handlers. An runtimeModule that wants both a warm-up and the live stream overrides both; one that wants
    /// only its indicators warmed overrides neither, because the dispatch updates registered indicators from
    /// historical items itself.
    /// </para>
    /// </summary>
    protected virtual void OnHistoricalData(IData data)
    {
    }

    /// <summary>
    /// A request's answer as a whole, once, after every item has been through <see cref="OnHistoricalData"/>.
    /// This is where a FAILED request arrives: <see cref="DataResponse.IsError"/> is set and
    /// <see cref="DataResponse.Error"/> says why, with no items. An runtimeModule that ignores this cannot tell a
    /// refusal from an empty answer.
    /// </summary>
    protected virtual void OnDataResponse(DataResponse response)
    {
    }

    protected virtual void OnEvent(Event e)
    {
    }

    protected virtual void OnTimeEvent(TimeEvent e)
    {
    }

    // ----- Dispatch (public so the moduleHost and tests can drive handlers) -----

    public void HandleInstrument(Instrument instrument) => Guarded(() => OnInstrument(instrument));

    public void HandleQuoteTick(QuoteTick tick) => Guarded(() =>
    {
        if (_quoteIndicators.TryGetValue(tick.MarketKey, out List<IIndicator>? indicators))
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
        if (_tradeIndicators.TryGetValue(tick.MarketKey, out List<IIndicator>? indicators))
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
        if (_barIndicators.TryGetValue(bar.CandleSeries, out List<IIndicator>? indicators))
        {
            foreach (IIndicator indicator in indicators)
            {
                indicator.Update(bar);
            }
        }

        OnBar(bar);
    });

    public void HandleOrderBookDeltas(OrderBookDeltas deltas) => Guarded(() =>
    {
        // Deltas are a change, not a book, so what the indicators are shown is the assembled book the cache holds -
        // which the data engine has already applied these deltas to by the time this runs.
        if (_bookIndicators.TryGetValue(deltas.MarketKey, out List<IOrderBookIndicator>? indicators)
            && Cache.OrderBook(deltas.MarketKey) is { } assembled)
        {
            foreach (IOrderBookIndicator indicator in indicators)
            {
                indicator.Update(assembled);
            }
        }

        OnOrderBookDeltas(deltas);
    });

    public void HandleOrderBook(OrderBook book) => Guarded(() =>
    {
        if (_bookIndicators.TryGetValue(book.MarketKey, out List<IOrderBookIndicator>? indicators))
        {
            foreach (IOrderBookIndicator indicator in indicators)
            {
                indicator.Update(book);
            }
        }

        OnOrderBook(book);
    });

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
                case Bar bar when _barIndicators.TryGetValue(bar.CandleSeries, out List<IIndicator>? bi):
                    foreach (IIndicator indicator in bi)
                    {
                        indicator.Update(bar);
                    }

                    break;
                case QuoteTick quote when _quoteIndicators.TryGetValue(quote.MarketKey, out List<IIndicator>? qi):
                    foreach (IIndicator indicator in qi)
                    {
                        indicator.Update(quote);
                    }

                    break;
                case TradeTick trade when _tradeIndicators.TryGetValue(trade.MarketKey, out List<IIndicator>? ti):
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
    /// Runs a handler, faulting the runtimeModule (but not the tradingRuntime) if it throws.
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
            Log.LogError(e, "RuntimeModule {RuntimeModuleId} handler threw; faulting runtimeModule", RuntimeModuleId);
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

    private void SendDataCommand(DataCommand command) => MessageBus.Send(Endpoints.MarketDataServiceExecute, command);

    protected void SubscribeInstruments(Venue venue, ClientId? clientId = null)
    {
        SubscribeTopic(Topics.Instruments(venue), m => HandleInstrument((Instrument)m));
        SendDataCommand(new SubscribeInstruments(venue, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void SubscribeInstrument(MarketKey marketKey, ClientId? clientId = null)
    {
        SubscribeTopic(Topics.Instrument(marketKey), m => HandleInstrument((Instrument)m));
        SendDataCommand(new SubscribeInstrument(marketKey, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void SubscribeQuoteTicks(MarketKey marketKey, ClientId? clientId = null)
    {
        SubscribeTopic(Topics.Quotes(marketKey), m => HandleQuoteTick((QuoteTick)m));
        SendDataCommand(new SubscribeQuoteTicks(marketKey, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void SubscribeTradeTicks(MarketKey marketKey, ClientId? clientId = null)
    {
        SubscribeTopic(Topics.Trades(marketKey), m => HandleTradeTick((TradeTick)m));
        SendDataCommand(new SubscribeTradeTicks(marketKey, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void SubscribeBars(CandleSeries candleSeries, ClientId? clientId = null)
    {
        SubscribeTopic(Topics.Bars(candleSeries), m => HandleBar((Bar)m));
        SendDataCommand(new SubscribeBars(candleSeries, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void SubscribeOrderBookDeltas(MarketKey marketKey, BookType bookType = BookType.L2, int depth = 0, ClientId? clientId = null)
    {
        SubscribeTopic(Topics.BookDeltas(marketKey), m => HandleOrderBookDeltas((OrderBookDeltas)m));
        SendDataCommand(new SubscribeOrderBookDeltas(marketKey, bookType, depth, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void SubscribeOrderBook(MarketKey marketKey, BookType bookType = BookType.L2, int depth = 0, TimeSpan? interval = null, ClientId? clientId = null)
    {
        SubscribeTopic(Topics.BookSnapshots(marketKey), m => HandleOrderBook((OrderBook)m));
        SendDataCommand(new SubscribeOrderBookSnapshots(marketKey, bookType, depth, interval ?? TimeSpan.FromSeconds(1), clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void SubscribeInstrumentStatus(MarketKey marketKey, ClientId? clientId = null)
    {
        SubscribeTopic(Topics.Status(marketKey), m => HandleInstrumentStatus((InstrumentStatus)m));
        SendDataCommand(new SubscribeInstrumentStatus(marketKey, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void SubscribeMarkPrices(MarketKey marketKey, ClientId? clientId = null)
    {
        SubscribeTopic(Topics.MarkPrices(marketKey), m => Guarded(() => OnMarkPrice((MarkPriceUpdate)m)));
        SendDataCommand(new SubscribeMarkPrices(marketKey, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void SubscribeIndexPrices(MarketKey marketKey, ClientId? clientId = null)
    {
        SubscribeTopic(Topics.IndexPrices(marketKey), m => Guarded(() => OnIndexPrice((IndexPriceUpdate)m)));
        SendDataCommand(new SubscribeIndexPrices(marketKey, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void SubscribeFundingRates(MarketKey marketKey, ClientId? clientId = null)
    {
        SubscribeTopic(Topics.FundingRates(marketKey), m => Guarded(() => OnFundingRate((FundingRateUpdate)m)));
        SendDataCommand(new SubscribeFundingRates(marketKey, clientId, Guid.NewGuid(), Clock.Timestamp));
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

    protected void UnsubscribeInstrument(MarketKey marketKey, ClientId? clientId = null)
    {
        UnsubscribeTopic(Topics.Instrument(marketKey));
        SendDataCommand(new UnsubscribeInstrument(marketKey, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void UnsubscribeQuoteTicks(MarketKey marketKey, ClientId? clientId = null)
    {
        UnsubscribeTopic(Topics.Quotes(marketKey));
        SendDataCommand(new UnsubscribeQuoteTicks(marketKey, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void UnsubscribeTradeTicks(MarketKey marketKey, ClientId? clientId = null)
    {
        UnsubscribeTopic(Topics.Trades(marketKey));
        SendDataCommand(new UnsubscribeTradeTicks(marketKey, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void UnsubscribeBars(CandleSeries candleSeries, ClientId? clientId = null)
    {
        UnsubscribeTopic(Topics.Bars(candleSeries));
        SendDataCommand(new UnsubscribeBars(candleSeries, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void UnsubscribeOrderBookDeltas(MarketKey marketKey, ClientId? clientId = null)
    {
        UnsubscribeTopic(Topics.BookDeltas(marketKey));
        SendDataCommand(new UnsubscribeOrderBookDeltas(marketKey, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void UnsubscribeOrderBook(MarketKey marketKey, ClientId? clientId = null)
    {
        UnsubscribeTopic(Topics.BookSnapshots(marketKey));
        SendDataCommand(new UnsubscribeOrderBookSnapshots(marketKey, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void UnsubscribeInstrumentStatus(MarketKey marketKey, ClientId? clientId = null)
    {
        UnsubscribeTopic(Topics.Status(marketKey));
        SendDataCommand(new UnsubscribeInstrumentStatus(marketKey, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void UnsubscribeMarkPrices(MarketKey marketKey, ClientId? clientId = null)
    {
        UnsubscribeTopic(Topics.MarkPrices(marketKey));
        SendDataCommand(new UnsubscribeMarkPrices(marketKey, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void UnsubscribeIndexPrices(MarketKey marketKey, ClientId? clientId = null)
    {
        UnsubscribeTopic(Topics.IndexPrices(marketKey));
        SendDataCommand(new UnsubscribeIndexPrices(marketKey, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void UnsubscribeFundingRates(MarketKey marketKey, ClientId? clientId = null)
    {
        UnsubscribeTopic(Topics.FundingRates(marketKey));
        SendDataCommand(new UnsubscribeFundingRates(marketKey, clientId, Guid.NewGuid(), Clock.Timestamp));
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
        MessageBus.Send(Endpoints.MarketDataServiceRequest, request with { Requester = RuntimeModuleId });
        return request.CommandId;
    }

    private bool _responseSubscribed;

    private void EnsureResponseSubscription()
    {
        if (_responseSubscribed)
        {
            return;
        }

        SubscribeTopic(Topics.DataResponses(RuntimeModuleId), m => HandleDataResponse((DataResponse)m));
        _responseSubscribed = true;
    }

    protected bool HasPendingRequests => _pendingRequests.Count > 0;

    protected bool IsPendingRequest(Guid requestId) => _pendingRequests.Contains(requestId);

    protected Guid RequestInstrument(MarketKey marketKey, ClientId? clientId = null) =>
        SendRequest(new RequestInstrument(marketKey, clientId, Guid.NewGuid(), Clock.Timestamp));

    protected Guid RequestInstruments(Venue venue, ClientId? clientId = null) =>
        SendRequest(new RequestInstruments(venue, clientId, Guid.NewGuid(), Clock.Timestamp));

    protected Guid RequestQuoteTicks(MarketKey marketKey, UnixNanos? start = null, UnixNanos? end = null, int? limit = null, ClientId? clientId = null) =>
        SendRequest(new RequestQuoteTicks(marketKey, start, end, limit, clientId, Guid.NewGuid(), Clock.Timestamp));

    protected Guid RequestTradeTicks(MarketKey marketKey, UnixNanos? start = null, UnixNanos? end = null, int? limit = null, ClientId? clientId = null) =>
        SendRequest(new RequestTradeTicks(marketKey, start, end, limit, clientId, Guid.NewGuid(), Clock.Timestamp));

    protected Guid RequestBars(CandleSeries candleSeries, UnixNanos? start = null, UnixNanos? end = null, int? limit = null, ClientId? clientId = null) =>
        SendRequest(new RequestBars(candleSeries, start, end, limit, clientId, Guid.NewGuid(), Clock.Timestamp));

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

    protected void PublishSignal(string name, decimal value, UnixNanos? eventTime = null)
    {
        UnixNanos now = Clock.Timestamp;
        MessageBus.Publish(Topics.Signal(name), new Signal(name, value, eventTime ?? now, now));
    }

    // ----- Indicators -----

    protected void RegisterIndicatorForQuoteTicks(MarketKey marketKey, IIndicator indicator)
    {
        ArgumentNullException.ThrowIfNull(indicator);
        AddIndicator(_quoteIndicators, marketKey, indicator);
    }

    protected void RegisterIndicatorForTradeTicks(MarketKey marketKey, IIndicator indicator)
    {
        ArgumentNullException.ThrowIfNull(indicator);
        AddIndicator(_tradeIndicators, marketKey, indicator);
    }

    protected void RegisterIndicatorForBars(CandleSeries candleSeries, IIndicator indicator)
    {
        ArgumentNullException.ThrowIfNull(indicator);
        AddIndicator(_barIndicators, candleSeries, indicator);
    }

    /// <summary>
    /// Feeds an indicator the order book of one instrument, on every delta and every snapshot (R7.8).
    ///
    /// <para>
    /// It asks for <see cref="IOrderBookIndicator"/> rather than an indicator, so an indicator that cannot read a book
    /// cannot be registered for one - which is the mistake this would otherwise invite, because every other indicator
    /// takes a price and would accept the registration and measure nothing.
    /// </para>
    ///
    /// <para>
    /// The book has to be subscribed to as well: registering an indicator asks for it to be fed, not for the data to be
    /// asked for, which is the same rule the other three follow.
    /// </para>
    /// </summary>
    protected void RegisterIndicatorForOrderBook(MarketKey marketKey, IOrderBookIndicator indicator)
    {
        ArgumentNullException.ThrowIfNull(indicator);
        AddIndicator(_bookIndicators, marketKey, indicator);
    }

    private void AddIndicator<TKey, TIndicator>(Dictionary<TKey, List<TIndicator>> map, TKey key, TIndicator indicator)
        where TKey : notnull
        where TIndicator : IIndicator
    {
        if (!map.TryGetValue(key, out List<TIndicator>? list))
        {
            list = new List<TIndicator>();
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

    private string Scoped(string name) => $"{RuntimeModuleId}:{name}";

    // ----- Background work -----

    /// <summary>
    /// Runs work on the thread pool and reports failures through <paramref name="onError"/> on the tradingRuntime thread.
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
                        Log.LogError(e, "Background work for {RuntimeModuleId} failed", RuntimeModuleId);
                    }
                });
            }
        }, ct);
    }

    /// <summary>
    /// Marshals an action onto the tradingRuntime thread (executes inline in backtests).
    /// </summary>
    protected void Post(Action action) => (_post ?? (a => a()))(action);

    public override string ToString() => $"{GetType().Name}({RuntimeModuleId}, {State})";
}
