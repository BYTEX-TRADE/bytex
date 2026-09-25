using System.Globalization;
using System.Text.Json;
using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Live.Network;
using Microsoft.Extensions.Logging;

namespace Bytex.Adapters.Kucoin;

/// <summary>
/// Opens a KuCoin WebSocket. The venue has no fixed stream address: a REST call hands out a server and a token that is good
/// for one connection, so the address is asked for again before every connection attempt.
/// </summary>
internal static class KucoinStream
{
    public const string PingMessage = "{\"id\":\"bytex\",\"type\":\"ping\"}";

    public static async Task<(Uri Url, TimeSpan PingInterval)> AddressAsync(KucoinHttp http, bool privateStream, string? baseUrlWs, CancellationToken ct)
    {
        JsonElement data = privateStream
            ? await http.PostSignedAsync("/api/v1/bullet-private", null, ct).ConfigureAwait(false)
            : await http.PostPublicAsync("/api/v1/bullet-public", ct).ConfigureAwait(false);
        JsonElement server = data.GetProperty("instanceServers")[0];
        string endpoint = baseUrlWs ?? server.Str("endpoint");
        long pingMs = server.Long("pingInterval");
        string separator = endpoint.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        Uri url = new($"{endpoint}{separator}token={Uri.EscapeDataString(data.Str("token"))}&connectId={Guid.NewGuid():N}");
        return (url, pingMs > 0 ? TimeSpan.FromMilliseconds(pingMs) : KucoinVenue.DefaultPingInterval);
    }

    public static string Subscribe(string topic, bool privateChannel, bool subscribe = true) =>
        JsonSerializer.Serialize(new { id = Guid.NewGuid().ToString("N"), type = subscribe ? "subscribe" : "unsubscribe", topic, privateChannel, response = true });
}

/// <summary>
/// Market data from KuCoin spot: best bid and ask, trades, candles and a 50-level book over the public stream, bars and
/// recent trades over REST.
/// </summary>
public sealed class KucoinDataClient : DataClientBase
{
    /// <summary>How long after a candle's end the client waits for late updates before it calls the candle closed.</summary>
    private static readonly TimeSpan _closeGrace = TimeSpan.FromSeconds(2);

    private readonly KucoinDataClientConfig _config;
    private readonly KucoinHttp _http;
    private readonly KucoinInstrumentProvider _instruments;
    private readonly HashSet<string> _topics = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BarType> _candleTopics = new(StringComparer.Ordinal);
    private readonly Dictionary<BarType, Candle> _forming = new();
    private readonly object _gate = new();
    private WebSocketClient? _ws;
    private CancellationTokenSource? _closer;

    private sealed record Candle(long StartSeconds, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume);

    public KucoinDataClient(ClientId clientId, KucoinDataClientConfig config, KernelServices services)
        : base(clientId, KucoinVenue.Venue, services)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _http = new KucoinHttp(config, Log);
        _instruments = new KucoinInstrumentProvider(_http, config.InstrumentProvider, Log);
    }

    public KucoinInstrumentProvider Instruments => _instruments;

    public override async Task ConnectAsync(CancellationToken ct)
    {
        await _instruments.InitializeAsync(ct).ConfigureAwait(false);
        foreach (Instrument instrument in _instruments.GetAll())
        {
            HandleInstrument(instrument);
        }

        (Uri first, TimeSpan ping) = await KucoinStream.AddressAsync(_http, privateStream: false, _config.BaseUrlWs, ct).ConfigureAwait(false);
        Uri? prepared = first;
        _ws = new WebSocketClient(new WebSocketClientConfig
        {
            Url = new Uri(first.GetLeftPart(UriPartial.Path)),
            UrlProvider = async token =>
            {
                Uri? ready = Interlocked.Exchange(ref prepared, null);
                return ready ?? (await KucoinStream.AddressAsync(_http, privateStream: false, _config.BaseUrlWs, token).ConfigureAwait(false)).Url;
            },
            PingMessage = KucoinStream.PingMessage,
            PingInterval = ping,
        }, Log)
        {
            OnText = HandleMessageAsync,
            OnConnected = isReconnect =>
            {
                if (isReconnect)
                {
                    List<string> topics;
                    lock (_gate)
                    {
                        topics = _topics.ToList();
                    }

                    foreach (string topic in topics)
                    {
                        _ws?.SendText(KucoinStream.Subscribe(topic, privateChannel: false));
                    }

                    // The drop marked the client disconnected and the socket reconnects by itself: say so, or the mark stays for good while data flows.
                    NotifyConnected();
                }

                return Task.CompletedTask;
            },
            OnDisconnected = reason =>
            {
                NotifyDisconnected(reason);
                return Task.CompletedTask;
            },
        };
        await _ws.ConnectAsync(ct).ConfigureAwait(false);
        _closer = new CancellationTokenSource();
        _ = Task.Run(() => CloseCandlesLoopAsync(_closer.Token), CancellationToken.None);
        NotifyConnected();
    }

    public override async Task DisconnectAsync(CancellationToken ct)
    {
        _closer?.Cancel();
        if (_ws is not null)
        {
            await _ws.DisposeAsync().ConfigureAwait(false);
            _ws = null;
        }

        NotifyDisconnected("disconnect requested");
    }

    protected override void OnDispose()
    {
        _closer?.Cancel();
        _ws?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _http.Dispose();
    }

    public override async Task SubscribeAsync(SubscribeCommand command, CancellationToken ct)
    {
        switch (command)
        {
            case SubscribeInstruments:
                foreach (Instrument instrument in _instruments.GetAll())
                {
                    HandleInstrument(instrument);
                }

                return;
            case SubscribeInstrument si:
                if (_instruments.Find(si.InstrumentId) is null)
                {
                    await _instruments.LoadAsync(si.InstrumentId, ct).ConfigureAwait(false);
                }

                if (_instruments.Find(si.InstrumentId) is { } loaded)
                {
                    HandleInstrument(loaded);
                }

                return;
            case SubscribeQuoteTicks q:
                AddTopic($"/spotMarket/level1:{Raw(q.InstrumentId)}");
                break;
            case SubscribeTradeTicks t:
                AddTopic($"/market/match:{Raw(t.InstrumentId)}");
                break;
            case SubscribeBars b when b.BarType.IsExternal:
                {
                    string topic = $"/market/candles:{Raw(b.BarType.InstrumentId)}_{KucoinVenue.Interval(b.BarType.Spec)}";
                    lock (_gate)
                    {
                        _candleTopics[topic] = b.BarType;
                    }

                    AddTopic(topic);
                    _ = SeedAsync(b.BarType, ct);
                    break;
                }

            case SubscribeOrderBookDeltas d:
                AddTopic($"/spotMarket/level2Depth50:{Raw(d.InstrumentId)}");
                break;
            default:
                Sink.OnSubscriptionFailed(ClientId, command, $"{command.GetType().Name} is not supported by the KuCoin spot data client");
                break;
        }
    }

    public override Task UnsubscribeAsync(UnsubscribeCommand command, CancellationToken ct)
    {
        string? topic = command switch
        {
            UnsubscribeQuoteTicks q => $"/spotMarket/level1:{Raw(q.InstrumentId)}",
            UnsubscribeTradeTicks t => $"/market/match:{Raw(t.InstrumentId)}",
            UnsubscribeBars b => $"/market/candles:{Raw(b.BarType.InstrumentId)}_{KucoinVenue.Interval(b.BarType.Spec)}",
            UnsubscribeOrderBookDeltas d => $"/spotMarket/level2Depth50:{Raw(d.InstrumentId)}",
            _ => null,
        };

        if (topic is null)
        {
            return Task.CompletedTask;
        }

        bool removed;
        lock (_gate)
        {
            removed = _topics.Remove(topic);
            if (_candleTopics.Remove(topic, out BarType barType))
            {
                _forming.Remove(barType);
            }
        }

        if (removed)
        {
            _ws?.SendText(KucoinStream.Subscribe(topic, privateChannel: false, subscribe: false));
        }

        return Task.CompletedTask;
    }

    private void AddTopic(string topic)
    {
        bool added;
        lock (_gate)
        {
            added = _topics.Add(topic);
        }

        if (added)
        {
            _ws?.SendText(KucoinStream.Subscribe(topic, privateChannel: false));
        }
    }

    private static string Raw(InstrumentId id) => KucoinVenue.ToRawSymbol(id);

    private Instrument? InstrumentFor(string rawSymbol)
    {
        InstrumentId id = KucoinVenue.ToInstrumentId(rawSymbol);
        return _instruments.Find(id) ?? Services.Cache.Instrument(id);
    }

    private Task HandleMessageAsync(string text)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            JsonElement root = doc.RootElement;
            if (root.Str("type") != "message")
            {
                if (root.Str("type") == "error")
                {
                    Log.LogWarning("KuCoin stream error {Code}: {Data}", root.Str("code"), root.Str("data"));
                }

                return Task.CompletedTask;
            }

            string topic = root.Str("topic");
            JsonElement data = root.GetProperty("data");
            int colon = topic.IndexOf(':', StringComparison.Ordinal);
            string channel = colon < 0 ? topic : topic[..colon];
            string subject = colon < 0 ? string.Empty : topic[(colon + 1)..];
            switch (channel)
            {
                case "/spotMarket/level1":
                    HandleLevel1(subject, data);
                    break;
                case "/market/match":
                    HandleMatch(data);
                    break;
                case "/market/candles":
                    HandleCandle(topic, data);
                    break;
                case "/spotMarket/level2Depth50":
                    HandleDepth(subject, data);
                    break;
            }
        }
        catch (Exception e)
        {
            Log.LogWarning(e, "Failed to parse KuCoin message: {Text}", LogText.Truncate(text, LogText.MaxMessageLength));
        }

        return Task.CompletedTask;
    }

    private void HandleLevel1(string rawSymbol, JsonElement d)
    {
        Instrument? instrument = InstrumentFor(rawSymbol);
        if (instrument is null || d.GetProperty("bids").GetArrayLength() < 2 || d.GetProperty("asks").GetArrayLength() < 2)
        {
            return;
        }

        JsonElement bid = d.GetProperty("bids");
        JsonElement ask = d.GetProperty("asks");
        UnixNanos ts = d.Has("timestamp") ? d.Ms("timestamp") : Clock.Timestamp;
        HandleData(new QuoteTick(instrument.Id, instrument.MakePrice(bid[0].DecValue()), instrument.MakePrice(ask[0].DecValue()),
            instrument.MakeQuantity(bid[1].DecValue()), instrument.MakeQuantity(ask[1].DecValue()), ts, Clock.Timestamp));
    }

    private void HandleMatch(JsonElement d)
    {
        Instrument? instrument = InstrumentFor(d.Str("symbol"));
        if (instrument is null)
        {
            return;
        }

        // "side" is the taker's side.
        HandleData(new TradeTick(instrument.Id, instrument.MakePrice(d.Dec("price")), instrument.MakeQuantity(d.Dec("size")),
            d.Str("side") == "buy" ? AggressorSide.Buyer : AggressorSide.Seller, new TradeId(d.Str("tradeId")), d.Ns("time"), Clock.Timestamp));
    }

    /// <summary>
    /// The stream sends the forming candle on every trade, never says that a candle is closed, and sends nothing at all for
    /// an interval without a trade. The venue's own history shows such an interval as a flat candle at the previous close
    /// with no volume (it writes those in later, when the next trade comes). The client does the same as time passes: a
    /// candle is closed when the next one appears or a moment after its interval has ended, and an interval that saw no
    /// trade is published as that flat bar, so a strategy gets a bar for every interval, as it does on other venues.
    /// </summary>
    private void HandleCandle(string topic, JsonElement d)
    {
        BarType barType;
        lock (_gate)
        {
            if (!_candleTopics.TryGetValue(topic, out barType))
            {
                return;
            }
        }

        JsonElement c = d.GetProperty("candles");
        Candle update = new(c[0].LongValue(), c[1].DecValue(), c[3].DecValue(), c[4].DecValue(), c[2].DecValue(), c[5].DecValue());
        Candle? closed = null;
        lock (_gate)
        {
            if (_forming.TryGetValue(barType, out Candle? current))
            {
                if (update.StartSeconds < current.StartSeconds)
                {
                    return;
                }

                if (update.StartSeconds > current.StartSeconds)
                {
                    closed = current;
                }
            }

            _forming[barType] = update;
        }

        if (closed is not null)
        {
            Publish(barType, closed, revision: false);
        }

        if (_config.HandleRevisedBars)
        {
            Publish(barType, update, revision: true);
        }
    }

    /// <summary>
    /// Without a trade the stream stays silent, so after subscribing the client would know no price to build flat bars from.
    /// The last closed bar from REST gives it one: the interval that is forming now starts flat at that close.
    /// </summary>
    private async Task SeedAsync(BarType barType, CancellationToken ct)
    {
        try
        {
            IReadOnlyList<IData> last = await FetchBarsAsync(new RequestBars(barType, null, null, 1, null, Guid.NewGuid(), Clock.Timestamp), ct).ConfigureAwait(false);
            if (last.Count == 0 || last[^1] is not Bar bar)
            {
                return;
            }

            long intervalSeconds = barType.Spec.IntervalNanos / UnixNanos.NanosPerSecond;
            long forming = Clock.Timestamp.Value / UnixNanos.NanosPerSecond / intervalSeconds * intervalSeconds;
            lock (_gate)
            {
                if (_candleTopics.ContainsValue(barType) && !_forming.ContainsKey(barType))
                {
                    _forming[barType] = new Candle(forming, bar.Close.Value, bar.Close.Value, bar.Close.Value, bar.Close.Value, 0m);
                }
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.LogWarning(e, "KuCoin: no last bar for {BarType}; bars start with the first trade", barType);
        }
    }

    private async Task CloseCandlesLoopAsync(CancellationToken ct)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                long now = Clock.Timestamp.Value;
                List<(BarType, Candle)> due = new();
                lock (_gate)
                {
                    foreach ((BarType barType, Candle candle) in _forming.ToList())
                    {
                        long end = candle.StartSeconds * UnixNanos.NanosPerSecond + barType.Spec.IntervalNanos;
                        if (candle.StartSeconds > 0 && now >= end + (_closeGrace.Ticks * UnixNanos.NanosPerTick))
                        {
                            due.Add((barType, candle));
                            // The next interval starts flat at this close. A trade in it replaces this with the venue's
                            // candle; an update that still names the closed interval is older than this and is dropped.
                            long next = candle.StartSeconds + barType.Spec.IntervalNanos / UnixNanos.NanosPerSecond;
                            _forming[barType] = new Candle(next, candle.Close, candle.Close, candle.Close, candle.Close, 0m);
                        }
                    }
                }

                foreach ((BarType barType, Candle candle) in due)
                {
                    Publish(barType, candle, revision: false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Publish(BarType barType, Candle candle, bool revision)
    {
        Instrument? instrument = _instruments.Find(barType.InstrumentId) ?? Services.Cache.Instrument(barType.InstrumentId);
        if (instrument is null)
        {
            return;
        }

        UnixNanos close = new(candle.StartSeconds * UnixNanos.NanosPerSecond + barType.Spec.IntervalNanos);
        HandleData(new Bar(barType, instrument.MakePrice(candle.Open), instrument.MakePrice(candle.High), instrument.MakePrice(candle.Low), instrument.MakePrice(candle.Close),
            instrument.MakeQuantity(candle.Volume), close, Clock.Timestamp, IsRevision: revision));
    }

    /// <summary>The 50-level channel sends the whole top of the book every time, so every message is a snapshot.</summary>
    private void HandleDepth(string rawSymbol, JsonElement d)
    {
        Instrument? instrument = InstrumentFor(rawSymbol);
        if (instrument is null)
        {
            return;
        }

        UnixNanos ts = d.Has("timestamp") ? d.Ms("timestamp") : Clock.Timestamp;
        UnixNanos now = Clock.Timestamp;
        ulong sequence = (ulong)ts.Value;
        List<OrderBookDelta> deltas = [OrderBookDelta.Clear(instrument.Id, sequence, ts, now)];
        ulong orderId = 0;
        foreach ((string side, OrderSide orderSide) in new[] { ("bids", OrderSide.Buy), ("asks", OrderSide.Sell) })
        {
            foreach (JsonElement level in d.GetProperty(side).EnumerateArray())
            {
                deltas.Add(new OrderBookDelta(instrument.Id, BookAction.Add,
                    new BookOrder(orderSide, instrument.MakePrice(level[0].DecValue()), instrument.MakeQuantity(level[1].DecValue()), ++orderId), RecordFlags.None, sequence, ts, now));
            }
        }

        HandleData(new OrderBookDeltas(instrument.Id, deltas, RecordFlags.Snapshot | RecordFlags.Last, sequence, ts, now));
    }

    // ----- Historical requests -----

    public override async Task RequestAsync(RequestCommand command, CancellationToken ct)
    {
        try
        {
            switch (command)
            {
                case RequestBars rb:
                    SendResponse(rb, typeof(Bar), await FetchBarsAsync(rb, ct).ConfigureAwait(false));
                    break;
                case RequestTradeTicks rt:
                    SendResponse(rt, typeof(TradeTick), await FetchTradesAsync(rt, ct).ConfigureAwait(false));
                    break;
                case RequestInstrument ri:
                    await _instruments.LoadAsync(ri.InstrumentId, ct).ConfigureAwait(false);
                    if (_instruments.Find(ri.InstrumentId) is { } inst)
                    {
                        HandleInstrument(inst);
                    }

                    SendResponse(ri, typeof(Instrument), []);
                    break;
                default:
                    SendErrorResponse(command, $"{command.GetType().Name} is not supported by the KuCoin data client");
                    break;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.LogError(e, "KuCoin request {Request} failed", command.GetType().Name);
            SendErrorResponse(command, e.Message);
        }
    }

    // History is read by KucoinHistory, which a catalog download calls as well, so stored and live bars agree.
    private async Task<IReadOnlyList<IData>> FetchBarsAsync(RequestBars request, CancellationToken ct)
    {
        Instrument? instrument = _instruments.Find(request.BarType.InstrumentId) ?? Services.Cache.Instrument(request.BarType.InstrumentId);
        if (instrument is null)
        {
            return [];
        }

        IReadOnlyList<Bar> bars = await KucoinHistory.FetchBarsAsync(_http, instrument, request.BarType, request.Start, request.End, request.Limit, Clock.Timestamp, ct).ConfigureAwait(false);
        return bars.Cast<IData>().ToList();
    }

    private async Task<IReadOnlyList<IData>> FetchTradesAsync(RequestTradeTicks request, CancellationToken ct)
    {
        Instrument? instrument = _instruments.Find(request.InstrumentId) ?? Services.Cache.Instrument(request.InstrumentId);
        if (instrument is null)
        {
            return [];
        }

        JsonElement data = await _http.GetPublicAsync("/api/v1/market/histories", new Dictionary<string, string> { ["symbol"] = KucoinVenue.ToRawSymbol(request.InstrumentId) }, ct).ConfigureAwait(false);
        List<IData> trades = new();
        foreach (JsonElement t in data.EnumerateArray())
        {
            UnixNanos ts = t.Ns("time");
            trades.Add(new TradeTick(instrument.Id, instrument.MakePrice(t.Dec("price")), instrument.MakeQuantity(t.Dec("size")),
                t.Str("side") == "buy" ? AggressorSide.Buyer : AggressorSide.Seller, new TradeId(t.Str("sequence")), ts, ts));
        }

        return trades.OrderBy(t => t.TsEvent).TakeLast(request.Limit ?? trades.Count).ToList();
    }
}

public sealed class KucoinDataClientFactory : IDataClientFactory
{
    public string Name => "KUCOIN";

    public Type ConfigType => typeof(KucoinDataClientConfig);

    /// <summary>
    /// One factory name for the venue, two clients behind it. Which one a node gets follows from the configuration
    /// rather than from a second factory, so a host that knows the venue does not also have to know how many
    /// markets it has - the declaration says that.
    /// </summary>
    public IDataClient Create(ClientId clientId, DataClientConfig config, KernelServices services)
    {
        KucoinDataClientConfig kucoin = (KucoinDataClientConfig)config;
        return kucoin.ProductType == KucoinProductType.Futures
            ? new KucoinFuturesDataClient(clientId, kucoin, services)
            : new KucoinDataClient(clientId, kucoin, services);
    }
}
