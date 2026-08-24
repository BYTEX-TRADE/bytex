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

public sealed record DataEngineConfig
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
public sealed class DataEngine : Component, IDataClientSink
{
    private readonly IMessageBus _bus;
    private readonly Cache _cache;
    private readonly DataEngineConfig _config;
    private readonly Dictionary<ClientId, IDataClient> _clients = new();
    private readonly Dictionary<Venue, IDataClient> _routing = new();
    private readonly Dictionary<string, int> _subscriptionCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SubscribeCommand> _subscriptions = new(StringComparer.Ordinal);
    private readonly Dictionary<BarType, BarAggregator> _aggregators = new();
    private readonly Dictionary<InstrumentId, BookType> _bookTypes = new();
    private readonly Dictionary<InstrumentId, string> _snapshotTimers = new();
    private readonly Dictionary<string, UnixNanos> _lastTimestamps = new(StringComparer.Ordinal);
    private IDataClient? _defaultClient;

    public DataEngine(IMessageBus bus, Cache cache, DataEngineConfig? config = null)
        : base(new ComponentId("DataEngine"))
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(cache);
        _bus = bus;
        _cache = cache;
        _config = config ?? new DataEngineConfig();

        _bus.Register(Endpoints.DataEngineExecute, Execute);
        _bus.Register(Endpoints.DataEngineProcess, m => Process((IData)m));
        _bus.Register(Endpoints.DataEngineRequest, m => Execute(m));
        _bus.Register(Endpoints.DataEngineResponse, m => OnResponse((DataResponse)m));
    }

    public DataEngineConfig Config => _config;

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
                Log.LogError("DataEngine cannot handle {MessageType}", message.GetType().Name);
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
            case SubscribeBars bars when bars.BarType.IsInternal:
                StartAggregator(bars);
                return;
            case SubscribeOrderBookDeltas deltas:
                _bookTypes[deltas.InstrumentId] = deltas.BookType;
                _cache.GetOrCreateOrderBook(deltas.InstrumentId, deltas.BookType);
                break;
            case SubscribeOrderBookSnapshots snapshots:
                _bookTypes[snapshots.InstrumentId] = snapshots.BookType;
                _cache.GetOrCreateOrderBook(snapshots.InstrumentId, snapshots.BookType);
                StartSnapshotTimer(snapshots);
                // Snapshots are produced locally from deltas; ensure a delta feed exists.
                HandleSubscribe(new SubscribeOrderBookDeltas(snapshots.InstrumentId, snapshots.BookType, snapshots.Depth, snapshots.ClientId, Guid.NewGuid(), snapshots.TsInit));
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
            case UnsubscribeBars bars when bars.BarType.IsInternal:
                StopAggregator(bars.BarType);
                return;
            case UnsubscribeOrderBookSnapshots snapshots:
                StopSnapshotTimer(snapshots.InstrumentId);
                HandleUnsubscribe(new UnsubscribeOrderBookDeltas(snapshots.InstrumentId, snapshots.ClientId, Guid.NewGuid(), snapshots.TsInit));
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

        Dispatch(client.RequestAsync(request, CancellationToken.None), $"request {request.GetType().Name}");
    }

    private void Dispatch(Task task, string description)
    {
        if (task.IsCompleted)
        {
            if (task.IsFaulted)
            {
                Log.LogError(task.Exception, "Data client operation failed: {Description}", description);
            }

            return;
        }

        task.ContinueWith(t => Log.LogError(t.Exception, "Data client operation failed: {Description}", description), TaskContinuationOptions.OnlyOnFaulted);
    }

    // ----- Aggregation -----

    private void StartAggregator(SubscribeBars command)
    {
        BarType barType = command.BarType;
        Instrument? instrument = _cache.Instrument(barType.InstrumentId);
        if (instrument is null)
        {
            Log.LogError("Cannot aggregate {BarType}: instrument not found in cache", barType);
            return;
        }

        BarAggregator aggregator = barType.Spec.Aggregation switch
        {
            BarAggregation.Tick => new TickBarAggregator(instrument, barType, b => Process(b), Clock),
            BarAggregation.Volume => new VolumeBarAggregator(instrument, barType, b => Process(b), Clock),
            BarAggregation.Value => new ValueBarAggregator(instrument, barType, b => Process(b), Clock),
            _ when barType.Spec.IsTimeAggregated => new TimeBarAggregator(instrument, barType, b => Process(b), Clock, _config.BuildBarsWithNoUpdates, _config.TimeBarsTimestampOnClose),
            _ => throw new NotSupportedException($"Aggregation {barType.Spec.Aggregation} is not supported."),
        };

        _aggregators[barType] = aggregator;
        if (aggregator is TimeBarAggregator time)
        {
            time.Start();
        }

        // Subscribe to the underlying source.
        SubscribeCommand source = barType.Spec.PriceType == PriceType.Last
            ? new SubscribeTradeTicks(barType.InstrumentId, command.ClientId, Guid.NewGuid(), command.TsInit)
            : new SubscribeQuoteTicks(barType.InstrumentId, command.ClientId, Guid.NewGuid(), command.TsInit);
        HandleSubscribe(source);
    }

    private void StopAggregator(BarType barType)
    {
        if (_aggregators.Remove(barType, out BarAggregator? aggregator))
        {
            aggregator.Stop();
            UnsubscribeCommand source = barType.Spec.PriceType == PriceType.Last
                ? new UnsubscribeTradeTicks(barType.InstrumentId, null, Guid.NewGuid(), Clock.Timestamp)
                : new UnsubscribeQuoteTicks(barType.InstrumentId, null, Guid.NewGuid(), Clock.Timestamp);
            HandleUnsubscribe(source);
        }
    }

    private void StartSnapshotTimer(SubscribeOrderBookSnapshots command)
    {
        string name = $"book-snapshot-{command.InstrumentId}";
        _snapshotTimers[command.InstrumentId] = name;
        InstrumentId instrumentId = command.InstrumentId;
        Clock.SetTimer(name, command.Interval, null, null, _ =>
        {
            OrderBook? book = _cache.OrderBook(instrumentId);
            if (book is not null)
            {
                _bus.Publish(Topics.BookSnapshots(instrumentId), book);
            }
        });
    }

    private void StopSnapshotTimer(InstrumentId instrumentId)
    {
        if (_snapshotTimers.Remove(instrumentId, out string? name))
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
                if (!ValidateSequence(Topics.Quotes(quote.InstrumentId), quote.TsEvent))
                {
                    return;
                }

                _cache.AddQuoteTick(quote);
                UpdateL1Book(quote);
                foreach (BarAggregator aggregator in AggregatorsFor(quote.InstrumentId, quotes: true))
                {
                    aggregator.HandleQuoteTick(quote);
                }

                _bus.Publish(Topics.Quotes(quote.InstrumentId), quote);
                break;

            case TradeTick trade:
                if (!ValidateSequence(Topics.Trades(trade.InstrumentId), trade.TsEvent))
                {
                    return;
                }

                _cache.AddTradeTick(trade);
                foreach (BarAggregator aggregator in AggregatorsFor(trade.InstrumentId, quotes: false))
                {
                    aggregator.HandleTradeTick(trade);
                }

                _bus.Publish(Topics.Trades(trade.InstrumentId), trade);
                break;

            case Bar bar:
                if (bar.IsRevision && !RevisionsAllowed(bar.BarType))
                {
                    return;
                }

                _cache.AddBar(bar);
                foreach (BarAggregator aggregator in _aggregators.Values)
                {
                    if (aggregator.BarType.InstrumentId == bar.BarType.InstrumentId && aggregator.BarType.Spec.IsTimeAggregated && bar.BarType.Spec.IsTimeAggregated
                        && aggregator.BarType.Spec.IntervalNanos > bar.BarType.Spec.IntervalNanos && aggregator.BarType.Spec.PriceType == bar.BarType.Spec.PriceType)
                    {
                        aggregator.HandleBar(bar);
                    }
                }

                _bus.Publish(Topics.Bars(bar.BarType), bar);
                break;

            case OrderBookDelta delta:
                ApplyDelta(delta);
                _bus.Publish(Topics.BookDeltas(delta.InstrumentId), new OrderBookDeltas(delta.InstrumentId, [delta], delta.Flags, delta.Sequence, delta.TsEvent, delta.TsInit));
                break;

            case OrderBookDeltas deltas:
                foreach (OrderBookDelta delta in deltas.Deltas)
                {
                    ApplyDelta(delta);
                }

                _bus.Publish(Topics.BookDeltas(deltas.InstrumentId), deltas);
                break;

            case OrderBookDepth depth:
                {
                    OrderBook book = _cache.GetOrCreateOrderBook(depth.InstrumentId, _bookTypes.GetValueOrDefault(depth.InstrumentId, BookType.L2));
                    book.Apply(depth);
                    _bus.Publish(Topics.BookSnapshots(depth.InstrumentId), book);
                    break;
                }

            case InstrumentStatus status:
                _bus.Publish(Topics.Status(status.InstrumentId), status);
                break;

            case MarkPriceUpdate mark:
                _cache.AddMarkPrice(mark);
                _bus.Publish(Topics.MarkPrices(mark.InstrumentId), mark);
                break;

            case IndexPriceUpdate index:
                _cache.AddIndexPrice(index);
                _bus.Publish(Topics.IndexPrices(index.InstrumentId), index);
                break;

            case FundingRateUpdate funding:
                _cache.AddFundingRate(funding);
                _bus.Publish(Topics.FundingRates(funding.InstrumentId), funding);
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
        OrderBook book = _cache.GetOrCreateOrderBook(delta.InstrumentId, _bookTypes.GetValueOrDefault(delta.InstrumentId, BookType.L2));
        book.Apply(delta);
    }

    private void UpdateL1Book(QuoteTick quote)
    {
        OrderBook? book = _cache.OrderBook(quote.InstrumentId);
        if (book is { BookType: BookType.L1 })
        {
            book.Apply(quote);
        }
    }

    private IEnumerable<BarAggregator> AggregatorsFor(InstrumentId instrumentId, bool quotes)
    {
        foreach (BarAggregator aggregator in _aggregators.Values)
        {
            if (aggregator.BarType.InstrumentId != instrumentId)
            {
                continue;
            }

            bool wantsQuotes = aggregator.BarType.Spec.PriceType != PriceType.Last;
            if (wantsQuotes == quotes)
            {
                yield return aggregator;
            }
        }
    }

    private bool RevisionsAllowed(BarType barType) => _subscriptions.ContainsKey(Topics.Bars(barType));

    private bool ValidateSequence(string key, UnixNanos tsEvent)
    {
        if (!_config.ValidateDataSequence)
        {
            return true;
        }

        if (_lastTimestamps.TryGetValue(key, out UnixNanos last) && tsEvent < last)
        {
            Log.LogWarning("Dropping out-of-sequence data on {Key}: {TsEvent} < {Last}", key, tsEvent, last);
            return false;
        }

        _lastTimestamps[key] = tsEvent;
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
