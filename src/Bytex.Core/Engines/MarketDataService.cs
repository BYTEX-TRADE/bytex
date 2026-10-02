using Bytex.Core.Adapters;
using Bytex.Core.Caching;
using Bytex.Core.Common;
using Bytex.Core.Messaging;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Microsoft.Extensions.Logging;

namespace Bytex.Core.Engines;

public sealed record MarketDataServiceConfig
{
    /// <summary>Emit time bars even when no ticks arrived during the interval.</summary>
    public bool BuildBarsWithNoUpdates { get; init; }

    /// <summary>Stamp internally built time bars with the interval close (true) or open (false).</summary>
    public bool TimeBarsTimestampOnClose { get; init; } = true;

    /// <summary>Validate that data timestamps are not older than the previous element per stream.</summary>
    public bool ValidateDataSequence { get; init; }

    /// <summary>Log every subscribe/unsubscribe command.</summary>
    public bool LogCommands { get; init; } = true;
}

/// <summary>
/// Owns data clients, routes subscription and request commands to them, and fans incoming data out to the cache and the message bus.
/// </summary>
public sealed class MarketDataService : Component, IDataClientSink
{
    private readonly IMessageBus _bus;
    private readonly Cache _cache;
    private readonly MarketDataServiceConfig _config;
    private readonly Action<Action> _post;
    private readonly Dictionary<ClientId, IDataClient> _clients = new();
    private readonly Dictionary<Venue, IDataClient> _routing = new();
    private readonly Dictionary<string, int> _subscriptionCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SubscribeCommand> _subscriptions = new(StringComparer.Ordinal);
    private readonly Dictionary<CandleSeries, BarAggregator> _aggregators = new();
    private readonly Dictionary<MarketKey, BookType> _bookTypes = new();
    private readonly Dictionary<MarketKey, string> _snapshotTimers = new();
    private readonly Dictionary<string, UnixNanos> _lastTimestamps = new(StringComparer.Ordinal);
    private IDataClient? _defaultClient;

    /// <summary>
    /// <paramref name="post"/> runs an action on the tradingRuntime thread; null runs it inline, which is what a backtest wants.
    /// </summary>
    public MarketDataService(IMessageBus bus, Cache cache, MarketDataServiceConfig? config = null, Action<Action>? post = null)
        : base(new ComponentId("MarketDataService"))
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(cache);
        _bus = bus;
        _cache = cache;
        _config = config ?? new MarketDataServiceConfig();
        _post = post ?? (action => action());

        _bus.Register(Endpoints.MarketDataServiceExecute, Execute);
        _bus.Register(Endpoints.MarketDataServiceProcess, m => Process((IData)m));
        _bus.Register(Endpoints.MarketDataServiceRequest, m => Execute(m));
        _bus.Register(Endpoints.MarketDataServiceResponse, m => OnResponse((DataResponse)m));
    }

    public MarketDataServiceConfig Config => _config;

    public IReadOnlyCollection<ClientId> RegisteredClients => _clients.Keys;

    public IReadOnlyCollection<string> SubscribedTopics => _subscriptions.Keys;

    public long CommandCount { get; private set; }

    public long DataCount { get; private set; }

    public long RequestCount { get; private set; }

    public long ResponseCount { get; private set; }

    // ----- Clients -----

    public void RegisterClient(IDataClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (_clients.ContainsKey(client.ClientId))
        {
            throw new InvalidOperationException($"Data client {client.ClientId} is already registered.");
        }

        client.AttachSink(this);
        _clients[client.ClientId] = client;
        if (client.Venue is { } venue)
        {
            _routing.TryAdd(venue, client);
        }

        Log.LogInformation("Registered data client {ClientId}", client.ClientId);
    }

    public void RegisterDefaultClient(IDataClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (!_clients.ContainsKey(client.ClientId))
        {
            RegisterClient(client);
        }

        _defaultClient = client;
    }

    public void RegisterVenueRouting(Venue venue, IDataClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _routing[venue] = client;
    }

    public void DeregisterClient(IDataClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _clients.Remove(client.ClientId);
        foreach (Venue venue in _routing.Where(kv => ReferenceEquals(kv.Value, client)).Select(kv => kv.Key).ToList())
        {
            _routing.Remove(venue);
        }

        if (ReferenceEquals(_defaultClient, client))
        {
            _defaultClient = null;
        }
    }

    public IDataClient? ClientFor(ClientId? clientId, Venue? venue)
    {
        if (clientId is { } id && _clients.TryGetValue(id, out IDataClient? byId))
        {
            return byId;
        }

        if (venue is { } v)
        {
            if (_routing.TryGetValue(v, out IDataClient? byVenue))
            {
                return byVenue;
            }

            if (_clients.TryGetValue(new ClientId(v.Value), out IDataClient? byName))
            {
                return byName;
            }
        }

        return _defaultClient;
    }

    protected override void OnStart()
    {
        foreach (IDataClient client in _clients.Values)
        {
            if (client.State is ComponentState.Ready or ComponentState.Stopped)
            {
                client.Start();
            }
        }
    }

    protected override void OnStop()
    {
        foreach (BarAggregator aggregator in _aggregators.Values)
        {
            aggregator.Stop();
        }

        foreach (IDataClient client in _clients.Values)
        {
            if (client.State is ComponentState.Running or ComponentState.Degraded)
            {
                client.Stop();
            }
        }
    }

    protected override void OnReset()
    {
        _subscriptionCounts.Clear();
        _subscriptions.Clear();
        _aggregators.Clear();
        _bookTypes.Clear();
        _snapshotTimers.Clear();
        _lastTimestamps.Clear();
        CommandCount = 0;
        DataCount = 0;
        RequestCount = 0;
        ResponseCount = 0;
    }

    protected override void OnDispose()
    {
        foreach (IDataClient client in _clients.Values)
        {
            client.Dispose();
        }

        _clients.Clear();
        _routing.Clear();
    }

    // ----- Commands -----

    public void Execute(object message)
    {
        CommandCount++;
        switch (message)
        {
            case SubscribeCommand subscribe:
                HandleSubscribe(subscribe);
                break;
            case UnsubscribeCommand unsubscribe:
                HandleUnsubscribe(unsubscribe);
                break;
            case RequestCommand request:
                HandleRequest(request);
                break;
            default:
                Log.LogError("MarketDataService cannot handle {MessageType}", message.GetType().Name);
                break;
        }
    }

    private void HandleSubscribe(SubscribeCommand command)
    {
        string key = command.Topic;
        _subscriptionCounts[key] = _subscriptionCounts.GetValueOrDefault(key) + 1;
        if (_subscriptionCounts[key] > 1)
        {
            return;
        }

        _subscriptions[key] = command;
        if (_config.LogCommands)
        {
            Log.LogDebug("Subscribe {Topic}", key);
        }

        switch (command)
        {
            case SubscribeBars bars when bars.CandleSeries.IsComputed:
                StartAggregator(bars);
                return;
            case SubscribeOrderBookDeltas deltas:
                _bookTypes[deltas.MarketKey] = deltas.BookType;
                _cache.GetOrCreateOrderBook(deltas.MarketKey, deltas.BookType);
                break;
            case SubscribeOrderBookSnapshots snapshots:
                _bookTypes[snapshots.MarketKey] = snapshots.BookType;
                _cache.GetOrCreateOrderBook(snapshots.MarketKey, snapshots.BookType);
                StartSnapshotTimer(snapshots);
                // Snapshots are produced locally from deltas; ensure a delta feed exists.
                HandleSubscribe(new SubscribeOrderBookDeltas(snapshots.MarketKey, snapshots.BookType, snapshots.Depth, snapshots.ClientId, Guid.NewGuid(), snapshots.CreatedTime));
                return;
        }

        IDataClient? client = ClientFor(command.ClientId, command.Venue);
        if (client is null)
        {
            Log.LogWarning("No data client for {Topic} (client={ClientId}, venue={Venue})", key, command.ClientId, command.Venue);
            return;
        }

        Dispatch(client.SubscribeAsync(command, CancellationToken.None), $"subscribe {key}");
    }

    private void HandleUnsubscribe(UnsubscribeCommand command)
    {
        string key = command.Topic;
        if (!_subscriptionCounts.TryGetValue(key, out int count))
        {
            return;
        }

        count--;
        if (count > 0)
        {
            _subscriptionCounts[key] = count;
            return;
        }

        _subscriptionCounts.Remove(key);
        _subscriptions.Remove(key);
        if (_config.LogCommands)
        {
            Log.LogDebug("Unsubscribe {Topic}", key);
        }

        switch (command)
        {
            case UnsubscribeBars bars when bars.CandleSeries.IsComputed:
                StopAggregator(bars.CandleSeries);
                return;
            case UnsubscribeOrderBookSnapshots snapshots:
                StopSnapshotTimer(snapshots.MarketKey);
                HandleUnsubscribe(new UnsubscribeOrderBookDeltas(snapshots.MarketKey, snapshots.ClientId, Guid.NewGuid(), snapshots.CreatedTime));
                return;
        }

        IDataClient? client = ClientFor(command.ClientId, command.Venue);
        if (client is null)
        {
            return;
        }

        Dispatch(client.UnsubscribeAsync(command, CancellationToken.None), $"unsubscribe {key}");
    }

    private void HandleRequest(RequestCommand request)
    {
        RequestCount++;
        IDataClient? client = ClientFor(request.ClientId, request.Venue);
        if (client is null)
        {
            Log.LogWarning("No data client for request {RequestType} (client={ClientId}, venue={Venue})", request.GetType().Name, request.ClientId, request.Venue);
            OnResponse(new DataResponse(request.CommandId, request.ClientId ?? new ClientId("NONE"), request.Venue, typeof(IData), [], Clock.Timestamp, request.Requester, "No data client available."));
            return;
        }

        Dispatch(client.RequestAsync(request, CancellationToken.None), $"request {request.GetType().Name}",
            error => OnResponse(new DataResponse(request.CommandId, client.ClientId, request.Venue, typeof(IData), [], Clock.Timestamp, request.Requester, error)));
    }

    private void Dispatch(Task task, string description, Action<string>? onFailure = null)
    {
        if (task.IsCompleted)
        {
            if (task.IsFaulted)
            {
                Fail(task.Exception);
            }

            return;
        }

        task.ContinueWith(t => Fail(t.Exception), TaskContinuationOptions.OnlyOnFaulted);

        // A request that fails has an runtimeModule waiting for it. Logging the failure and answering nothing left that runtimeModule
        // waiting for good, so the failure is delivered as the answer, on the tradingRuntime thread like every other message.
        void Fail(AggregateException? exception)
        {
            Log.LogError(exception, "Data client operation failed: {Description}", description);
            if (onFailure is null)
            {
                return;
            }

            Exception? inner = exception?.InnerExceptions.FirstOrDefault() ?? exception;
            string error = inner is null ? description + " failed" : inner.GetType().Name + ": " + inner.Message;
            _post(() => onFailure(error));
        }
    }

    // ----- Aggregation -----

    private void StartAggregator(SubscribeBars command)
    {
        CandleSeries candleSeries = command.CandleSeries;
        Instrument? instrument = _cache.Instrument(candleSeries.MarketKey);
        if (instrument is null)
        {
            Log.LogError("Cannot aggregate {CandleSeries}: instrument not found in cache", candleSeries);
            return;
        }

        BarAggregator aggregator = candleSeries.Spec.Aggregation switch
        {
            SamplingMethod.Tick => new TickBarAggregator(instrument, candleSeries, b => Process(b), Clock),
            SamplingMethod.Volume => new VolumeBarAggregator(instrument, candleSeries, b => Process(b), Clock),
            SamplingMethod.Value => new ValueBarAggregator(instrument, candleSeries, b => Process(b), Clock),
            SamplingMethod.TickImbalance or SamplingMethod.VolumeImbalance or SamplingMethod.ValueImbalance =>
                new ImbalanceBarAggregator(instrument, candleSeries, b => Process(b), Clock),
            SamplingMethod.TickRuns or SamplingMethod.VolumeRuns or SamplingMethod.ValueRuns =>
                new RunsBarAggregator(instrument, candleSeries, b => Process(b), Clock),
            _ when candleSeries.Spec.IsTimeAggregated => new TimeBarAggregator(instrument, candleSeries, b => Process(b), Clock, _config.BuildBarsWithNoUpdates, _config.TimeBarsTimestampOnClose),
            _ => throw new NotSupportedException($"Aggregation {candleSeries.Spec.Aggregation} is not supported."),
        };

        _aggregators[candleSeries] = aggregator;
        if (aggregator is TimeBarAggregator time)
        {
            time.Start();
        }

        // Subscribe to the underlying source.
        SubscribeCommand source = candleSeries.Spec.PriceType == PriceType.Last
            ? new SubscribeTradeTicks(candleSeries.MarketKey, command.ClientId, Guid.NewGuid(), command.CreatedTime)
            : new SubscribeQuoteTicks(candleSeries.MarketKey, command.ClientId, Guid.NewGuid(), command.CreatedTime);
        HandleSubscribe(source);
    }

    private void StopAggregator(CandleSeries candleSeries)
    {
        if (_aggregators.Remove(candleSeries, out BarAggregator? aggregator))
        {
            aggregator.Stop();
            UnsubscribeCommand source = candleSeries.Spec.PriceType == PriceType.Last
                ? new UnsubscribeTradeTicks(candleSeries.MarketKey, null, Guid.NewGuid(), Clock.Timestamp)
                : new UnsubscribeQuoteTicks(candleSeries.MarketKey, null, Guid.NewGuid(), Clock.Timestamp);
            HandleUnsubscribe(source);
        }
    }

    private void StartSnapshotTimer(SubscribeOrderBookSnapshots command)
    {
        string name = $"book-snapshot-{command.MarketKey}";
        _snapshotTimers[command.MarketKey] = name;
        MarketKey marketKey = command.MarketKey;
        Clock.SetTimer(name, command.Interval, null, null, _ =>
        {
            OrderBook? book = _cache.OrderBook(marketKey);
            if (book is not null)
            {
                _bus.Publish(Topics.BookSnapshots(marketKey), book);
            }
        });
    }

    private void StopSnapshotTimer(MarketKey marketKey)
    {
        if (_snapshotTimers.Remove(marketKey, out string? name))
        {
            Clock.CancelTimer(name);
        }
    }

    // ----- Data processing -----

    /// <summary>
    /// Processes a data element: updates the cache, feeds aggregators, and publishes on the bus.
    /// </summary>
    public void Process(IData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        DataCount++;

        switch (data)
        {
            case QuoteTick quote:
                if (!ValidateSequence(Topics.Quotes(quote.MarketKey), quote.EventTime))
                {
                    return;
                }

                _cache.AddQuoteTick(quote);
                UpdateL1Book(quote);
                foreach (BarAggregator aggregator in AggregatorsFor(quote.MarketKey, quotes: true))
                {
                    aggregator.HandleQuoteTick(quote);
                }

                _bus.Publish(Topics.Quotes(quote.MarketKey), quote);
                break;

            case TradeTick trade:
                if (!ValidateSequence(Topics.Trades(trade.MarketKey), trade.EventTime))
                {
                    return;
                }

                _cache.AddTradeTick(trade);
                foreach (BarAggregator aggregator in AggregatorsFor(trade.MarketKey, quotes: false))
                {
                    aggregator.HandleTradeTick(trade);
                }

                _bus.Publish(Topics.Trades(trade.MarketKey), trade);
                break;

            case Bar bar:
                if (bar.IsRevision && !RevisionsAllowed(bar.CandleSeries))
                {
                    return;
                }

                _cache.AddBar(bar);
                foreach (BarAggregator aggregator in _aggregators.Values)
                {
                    if (aggregator.CandleSeries.MarketKey == bar.CandleSeries.MarketKey && aggregator.CandleSeries.Spec.IsTimeAggregated && bar.CandleSeries.Spec.IsTimeAggregated
                        && aggregator.CandleSeries.Spec.IntervalNanos > bar.CandleSeries.Spec.IntervalNanos && aggregator.CandleSeries.Spec.PriceType == bar.CandleSeries.Spec.PriceType)
                    {
                        aggregator.HandleBar(bar);
                    }
                }

                _bus.Publish(Topics.Bars(bar.CandleSeries), bar);
                break;

            case OrderBookDelta delta:
                ApplyDelta(delta);
                _bus.Publish(Topics.BookDeltas(delta.MarketKey), new OrderBookDeltas(delta.MarketKey, [delta], delta.Flags, delta.Sequence, delta.EventTime, delta.CreatedTime));
                break;

            case OrderBookDeltas deltas:
                foreach (OrderBookDelta delta in deltas.Deltas)
                {
                    ApplyDelta(delta);
                }

                _bus.Publish(Topics.BookDeltas(deltas.MarketKey), deltas);
                break;

            case OrderBookDepth depth:
                {
                    OrderBook book = _cache.GetOrCreateOrderBook(depth.MarketKey, _bookTypes.GetValueOrDefault(depth.MarketKey, BookType.L2));
                    book.Apply(depth);
                    _cache.OwnOrders.Observe(book);
                    _bus.Publish(Topics.BookSnapshots(depth.MarketKey), book);
                    break;
                }

            case InstrumentStatus status:
                _bus.Publish(Topics.Status(status.MarketKey), status);
                break;

            case MarkPriceUpdate mark:
                _cache.AddMarkPrice(mark);
                _bus.Publish(Topics.MarkPrices(mark.MarketKey), mark);
                break;

            case IndexPriceUpdate index:
                _cache.AddIndexPrice(index);
                _bus.Publish(Topics.IndexPrices(index.MarketKey), index);
                break;

            case FundingRateUpdate funding:
                _cache.AddFundingRate(funding);
                _bus.Publish(Topics.FundingRates(funding.MarketKey), funding);
                break;

            case Signal signal:
                _bus.Publish(Topics.Signal(signal.Name), signal);
                break;

            default:
                _bus.Publish(Topics.Custom(new DataType(data.GetType())), data);
                break;
        }
    }

    /// <summary>
    /// Publishes custom data under an explicit data type (with metadata).
    /// </summary>
    public void ProcessCustom(DataType dataType, IData data)
    {
        ArgumentNullException.ThrowIfNull(dataType);
        DataCount++;
        _bus.Publish(Topics.Custom(dataType), data);
    }

    public void ProcessInstrument(Instrument instrument)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        _cache.AddInstrument(instrument);
        _bus.Publish(Topics.Instrument(instrument.Id), instrument);
    }

    private void ApplyDelta(OrderBookDelta delta)
    {
        OrderBook book = _cache.GetOrCreateOrderBook(delta.MarketKey, _bookTypes.GetValueOrDefault(delta.MarketKey, BookType.L2));
        book.Apply(delta);
        _cache.OwnOrders.Observe(book);
    }

    private void UpdateL1Book(QuoteTick quote)
    {
        OrderBook? book = _cache.OrderBook(quote.MarketKey);
        if (book is { BookType: BookType.L1 })
        {
            book.Apply(quote);
            _cache.OwnOrders.Observe(book);
        }
    }

    private IEnumerable<BarAggregator> AggregatorsFor(MarketKey marketKey, bool quotes)
    {
        foreach (BarAggregator aggregator in _aggregators.Values)
        {
            if (aggregator.CandleSeries.MarketKey != marketKey)
            {
                continue;
            }

            bool wantsQuotes = aggregator.CandleSeries.Spec.PriceType != PriceType.Last;
            if (wantsQuotes == quotes)
            {
                yield return aggregator;
            }
        }
    }

    private bool RevisionsAllowed(CandleSeries candleSeries) => _subscriptions.ContainsKey(Topics.Bars(candleSeries));

    private bool ValidateSequence(string key, UnixNanos eventTime)
    {
        if (!_config.ValidateDataSequence)
        {
            return true;
        }

        if (_lastTimestamps.TryGetValue(key, out UnixNanos last) && eventTime < last)
        {
            Log.LogWarning("Dropping out-of-sequence data on {Key}: {EventTime} < {Last}", key, eventTime, last);
            return false;
        }

        _lastTimestamps[key] = eventTime;
        return true;
    }

    // ----- IDataClientSink -----

    public void OnData(IData data) => Process(data);

    public void OnInstrument(Instrument instrument) => ProcessInstrument(instrument);

    public void OnResponse(DataResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        ResponseCount++;
        if (response.IsError)
        {
            Log.LogWarning("Data request {CorrelationId} failed: {Error}", response.CorrelationId, response.Error);
        }

        // Cache historical results so strategies can read them immediately.
        foreach (IData item in response.Data)
        {
            switch (item)
            {
                case QuoteTick q:
                    _cache.AddQuoteTick(q);
                    break;
                case TradeTick t:
                    _cache.AddTradeTick(t);
                    break;
                case Bar b:
                    _cache.AddBar(b);
                    break;
            }
        }

        if (response.Requester is { } requester)
        {
            _bus.Publish(Topics.DataResponses(requester), response);
        }
    }

    public void OnConnected(ClientId clientId) => Log.LogInformation("Data client {ClientId} connected", clientId);

    public void OnDisconnected(ClientId clientId, string reason) => Log.LogWarning("Data client {ClientId} disconnected: {Reason}", clientId, reason);

    public void OnSubscriptionFailed(ClientId clientId, SubscribeCommand command, string reason) =>
        Log.LogError("Data client {ClientId} failed to subscribe {Topic}: {Reason}", clientId, command.Topic, reason);

    /// <summary>
    /// Re-sends all active subscriptions to a client (after reconnection).
    /// </summary>
    public void Resubscribe(IDataClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        foreach (SubscribeCommand command in _subscriptions.Values)
        {
            if (ReferenceEquals(ClientFor(command.ClientId, command.Venue), client))
            {
                Dispatch(client.SubscribeAsync(command, CancellationToken.None), $"resubscribe {command.Topic}");
            }
        }
    }
}
