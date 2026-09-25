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
/// Market data from KuCoin's futures market: best bid and ask, matched trades, candles, a 50-level book, and the mark
/// price and funding rate a perpetual is priced against. Bars and recent trades come over REST.
/// <para>
/// A separate client from the spot one rather than a product switch inside it, because the two markets share only the
/// key and the shape of the envelope. Different topics, different subjects, a book that arrives as a comma-separated
/// string, sizes counted in contracts, and timestamps in three different units across four topics on one socket.
/// </para>
/// <para>
/// Every size crossing this boundary is converted. The venue counts in contracts and everything above the adapter
/// counts in base currency, which is the whole of what makes a strategy portable between this venue and the others.
/// </para>
/// </summary>
public sealed class KucoinFuturesDataClient : DataClientBase
{
    /// <summary>How long after a candle's end the client waits for late updates before it calls the candle closed.</summary>
    private static readonly TimeSpan _closeGrace = TimeSpan.FromSeconds(2);

    private readonly KucoinDataClientConfig _config;
    private readonly KucoinHttp _http;
    private readonly KucoinFuturesInstrumentProvider _instruments;
    private readonly HashSet<string> _topics = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BarType> _candleTopics = new(StringComparer.Ordinal);
    private readonly Dictionary<BarType, Candle> _forming = new();
    private readonly object _gate = new();
    private WebSocketClient? _ws;
    private CancellationTokenSource? _closer;

    private sealed record Candle(long StartSeconds, decimal Open, decimal High, decimal Low, decimal Close, decimal Contracts);

    public KucoinFuturesDataClient(ClientId clientId, KucoinDataClientConfig config, KernelServices services)
        : base(clientId, KucoinVenue.Venue, services)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        if (_config.ProductType != KucoinProductType.Futures)
        {
            throw new ArgumentException(
                "This client serves KuCoin's futures market and the configuration says "
                + $"{_config.ProductType}. The spot market is KucoinDataClient.",
                nameof(config));
        }

        _http = new KucoinHttp(config, Log);
        _instruments = new KucoinFuturesInstrumentProvider(_http, config.InstrumentProvider, Log);
    }

    public KucoinFuturesInstrumentProvider Instruments => _instruments;

    public override async Task ConnectAsync(CancellationToken ct)
    {
        await _instruments.InitializeAsync(ct).ConfigureAwait(false);
        foreach (Instrument instrument in _instruments.GetAll())
        {
            HandleInstrument(instrument);
        }

        // The same bullet call as spot, against the futures host: the venue hands out an address and a token that is
        // good for one connection, so the address is asked for again before every attempt.
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

            // Measured: without a ping at the interval the venue names, the socket stays open and stops delivering.
            // Seventy-five seconds of silence produced nothing at all; pinging produced candles throughout.
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
        _closer = null;
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

            // tickerV2 carries the best bid and ask and nothing else. The plain `ticker` topic carries a trade AND a
            // partial book, which would make one message two kinds of data and a quote that moves on every trade.
            case SubscribeQuoteTicks q:
                AddTopic($"/contractMarket/tickerV2:{Raw(q.InstrumentId)}");
                break;
            case SubscribeTradeTicks t:
                AddTopic($"/contractMarket/execution:{Raw(t.InstrumentId)}");
                break;
            case SubscribeBars b when b.BarType.IsExternal:
                {
                    string topic = $"/contractMarket/limitCandle:{Raw(b.BarType.InstrumentId)}_{Candles(b.BarType.Spec)}";
                    lock (_gate)
                    {
                        _candleTopics[topic] = b.BarType;
                    }

                    AddTopic(topic);
                    _ = SeedAsync(b.BarType, ct);
                    break;
                }

            case SubscribeOrderBookDeltas d:
                AddTopic($"/contractMarket/level2Depth50:{Raw(d.InstrumentId)}");
                break;

            // One topic carries both, under two subjects, so either subscription asks for the same thing.
            case SubscribeMarkPrices m:
                AddTopic($"/contract/instrument:{Raw(m.InstrumentId)}");
                break;
            case SubscribeFundingRates f:
                AddTopic($"/contract/instrument:{Raw(f.InstrumentId)}");
                break;
            default:
                Sink.OnSubscriptionFailed(ClientId, command, $"{command.GetType().Name} is not supported by the KuCoin futures data client");
                break;
        }
    }

    public override Task UnsubscribeAsync(UnsubscribeCommand command, CancellationToken ct)
    {
        string? topic = command switch
        {
            UnsubscribeQuoteTicks q => $"/contractMarket/tickerV2:{Raw(q.InstrumentId)}",
            UnsubscribeTradeTicks t => $"/contractMarket/execution:{Raw(t.InstrumentId)}",
            UnsubscribeBars b => $"/contractMarket/limitCandle:{Raw(b.BarType.InstrumentId)}_{Candles(b.BarType.Spec)}",
            UnsubscribeOrderBookDeltas d => $"/contractMarket/level2Depth50:{Raw(d.InstrumentId)}",
            UnsubscribeMarkPrices m => $"/contract/instrument:{Raw(m.InstrumentId)}",
            UnsubscribeFundingRates f => $"/contract/instrument:{Raw(f.InstrumentId)}",
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

    /// <summary>
    /// What the candle topic calls a bar length. The socket names it the way spot does - "1min", "4hour" - while the
    /// REST endpoint on this same market wants a number of minutes. Two spellings of one fact on one venue.
    /// </summary>
    private static string Candles(BarSpecification spec) => KucoinVenue.Interval(spec);

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

    private static string Raw(InstrumentId id) => KucoinFuturesVenue.ToRawSymbol(id);

    private Instrument? InstrumentFor(string rawSymbol)
    {
        InstrumentId id = KucoinFuturesVenue.ToInstrumentId(rawSymbol);
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
                    Log.LogWarning("KuCoin futures stream error {Code}: {Data}", root.Str("code"), root.Str("data"));
                }

                return Task.CompletedTask;
            }

            string topic = root.Str("topic");
            JsonElement data = root.GetProperty("data");
            int colon = topic.IndexOf(':', StringComparison.Ordinal);
            string channel = colon < 0 ? topic : topic[..colon];
            string rest = colon < 0 ? string.Empty : topic[(colon + 1)..];
            switch (channel)
            {
                case "/contractMarket/tickerV2":
                    HandleTicker(data);
                    break;
                case "/contractMarket/execution":
                    HandleMatch(data);
                    break;
                case "/contractMarket/limitCandle":
                    HandleCandle(topic, data);
                    break;
                case "/contractMarket/level2Depth50":
                    HandleDepth(rest, data);
                    break;
                case "/contract/instrument":
                    HandleInstrumentTopic(rest, root.Str("subject"), data);
                    break;
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            Log.LogWarning(e, "KuCoin futures: unreadable stream message");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Best bid and ask. The sizes are contracts and the timestamp is in NANOSECONDS - the book snapshot on the same
    /// socket stamps in milliseconds and the candle in seconds, so nothing here is shared with them.
    /// </summary>
    private void HandleTicker(JsonElement d)
    {
        Instrument? instrument = InstrumentFor(d.Str("symbol"));
        if (instrument is null)
        {
            return;
        }

        HandleData(new QuoteTick(
            instrument.Id,
            instrument.MakePrice(d.Dec("bestBidPrice")),
            instrument.MakePrice(d.Dec("bestAskPrice")),
            KucoinFuturesVenue.ToQuantity(instrument, d.Dec("bestBidSize")),
            KucoinFuturesVenue.ToQuantity(instrument, d.Dec("bestAskSize")),
            d.Ns("ts"),
            Clock.Timestamp));
    }

    private void HandleMatch(JsonElement d)
    {
        Instrument? instrument = InstrumentFor(d.Str("symbol"));
        if (instrument is null)
        {
            return;
        }

        // "side" is the taker's side, and "size" is a number of contracts.
        HandleData(new TradeTick(
            instrument.Id,
            instrument.MakePrice(d.Dec("price")),
            KucoinFuturesVenue.ToQuantity(instrument, d.Dec("size")),
            d.Str("side") == "buy" ? AggressorSide.Buyer : AggressorSide.Seller,
            new TradeId(d.Str("tradeId")),
            d.Ns("ts"),
            Clock.Timestamp));
    }

    private void HandleDepth(string rawSymbol, JsonElement d)
    {
        Instrument? instrument = InstrumentFor(rawSymbol);
        if (instrument is null || !d.Has("bids") || !d.Has("asks"))
        {
            return;
        }

        JsonElement bids = d.GetProperty("bids");
        JsonElement asks = d.GetProperty("asks");
        if (bids.GetArrayLength() == 0 || asks.GetArrayLength() == 0)
        {
            return;
        }

        UnixNanos ts = d.Has("timestamp") ? d.Ms("timestamp") : Clock.Timestamp;
        HandleData(new QuoteTick(
            instrument.Id,
            instrument.MakePrice(bids[0][0].DecValue()),
            instrument.MakePrice(asks[0][0].DecValue()),
            KucoinFuturesVenue.ToQuantity(instrument, bids[0][1].DecValue()),
            KucoinFuturesVenue.ToQuantity(instrument, asks[0][1].DecValue()),
            ts,
            Clock.Timestamp));
    }

    /// <summary>
    /// The mark price a perpetual is liquidated against, and the funding it is charged. Both arrive on one topic under
    /// separate subjects, so the subject is what says which.
    /// </summary>
    private void HandleInstrumentTopic(string rawSymbol, string subject, JsonElement d)
    {
        Instrument? instrument = InstrumentFor(rawSymbol);
        if (instrument is null)
        {
            return;
        }

        UnixNanos ts = d.Has("timestamp") ? d.Ms("timestamp") : Clock.Timestamp;
        switch (subject)
        {
            case "mark.index.price":
                if (d.Has("markPrice"))
                {
                    HandleData(new MarkPriceUpdate(instrument.Id, instrument.MakePrice(d.Dec("markPrice")), ts, Clock.Timestamp));
                }

                if (d.Has("indexPrice"))
                {
                    HandleData(new IndexPriceUpdate(instrument.Id, instrument.MakePrice(d.Dec("indexPrice")), ts, Clock.Timestamp));
                }

                break;

            case "funding.rate":
                // The rate the venue is currently predicting, republished as it moves - not a settlement. Settlement
                // is every eight hours and what was actually charged comes from the funding history endpoint, which
                // is why there is no next-settlement time on this: the venue does not put one here.
                if (d.Has("fundingRate"))
                {
                    HandleData(new FundingRateUpdate(instrument.Id, d.Dec("fundingRate"), null, ts, Clock.Timestamp));
                }

                break;
        }
    }

    /// <summary>
    /// The stream sends the forming candle as trades arrive, never says a candle is closed, and sends nothing for an
    /// interval without a trade - exactly as spot does, so the flat bars are built here the same way.
    /// <para>
    /// The row is NOT the row the REST endpoint on this same market returns. Here it is
    /// [start in seconds, open, close, high, low, turnover, volume in contracts]; there it is
    /// [start in milliseconds, open, high, low, close, volume in contracts, turnover]. The prices are in different
    /// places, the volume and the turnover are swapped, and the timestamp is in a different unit. Reading one with
    /// the other's layout gives four real prices in the wrong slots and a volume a thousand times out, with no error
    /// anywhere, which is why the two are read in different places and each has its own test.
    /// </para>
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
        Candle update = new(
            c[0].LongValue(),
            c[1].DecValue(),
            c[3].DecValue(),
            c[4].DecValue(),
            c[2].DecValue(),
            c.GetArrayLength() > 6 ? c[6].DecValue() : 0m);

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

    private void Publish(BarType barType, Candle candle, bool revision)
    {
        Instrument? instrument = _instruments.Find(barType.InstrumentId) ?? Services.Cache.Instrument(barType.InstrumentId);
        if (instrument is null)
        {
            return;
        }

        UnixNanos close = new(candle.StartSeconds * UnixNanos.NanosPerSecond + barType.Spec.IntervalNanos);
        Bar bar = new(
            barType,
            instrument.MakePrice(candle.Open),
            instrument.MakePrice(candle.High),
            instrument.MakePrice(candle.Low),
            instrument.MakePrice(candle.Close),
            KucoinFuturesVenue.ToQuantity(instrument, candle.Contracts),
            close,
            Clock.Timestamp);

        if (revision)
        {
            HandleData(bar with { IsRevision = true });
            return;
        }

        HandleData(bar);
    }

    /// <summary>
    /// Without a trade the stream stays silent, so after subscribing the client would know no price to build a flat
    /// bar from. The last closed bar over REST gives it one.
    /// </summary>
    private async Task SeedAsync(BarType barType, CancellationToken ct)
    {
        try
        {
            Instrument? instrument = _instruments.Find(barType.InstrumentId) ?? Services.Cache.Instrument(barType.InstrumentId);
            if (instrument is null)
            {
                return;
            }

            IReadOnlyList<Bar> last = await KucoinHistory
                .FetchBarsAsync(_http, instrument, barType, null, null, 1, Clock.Timestamp, ct)
                .ConfigureAwait(false);

            if (last.Count == 0)
            {
                return;
            }

            long intervalSeconds = barType.Spec.IntervalNanos / UnixNanos.NanosPerSecond;
            long forming = Clock.Timestamp.Value / UnixNanos.NanosPerSecond / intervalSeconds * intervalSeconds;
            decimal close = last[^1].Close.Value;
            lock (_gate)
            {
                if (_candleTopics.ContainsValue(barType) && !_forming.ContainsKey(barType))
                {
                    _forming[barType] = new Candle(forming, close, close, close, close, 0m);
                }
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.LogWarning(e, "KuCoin futures: no last bar for {BarType}; bars start with the first trade", barType);
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
                        long end = (candle.StartSeconds * UnixNanos.NanosPerSecond) + barType.Spec.IntervalNanos;
                        if (candle.StartSeconds > 0 && now >= end + (_closeGrace.Ticks * UnixNanos.NanosPerTick))
                        {
                            due.Add((barType, candle));
                            long next = candle.StartSeconds + (barType.Spec.IntervalNanos / UnixNanos.NanosPerSecond);
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
            // Disconnecting.
        }
    }

    public override async Task RequestAsync(RequestCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
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
                    SendErrorResponse(command, $"{command.GetType().Name} is not supported by the KuCoin futures data client");
                    break;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.LogError(e, "KuCoin futures request {Request} failed", command.GetType().Name);
            SendErrorResponse(command, e.Message);
        }
    }

    // History is read by KucoinHistory, which a catalog download calls as well, so stored and live bars agree - and
    // which routes to the futures endpoint off the http client's own product.
    private async Task<IReadOnlyList<IData>> FetchBarsAsync(RequestBars request, CancellationToken ct)
    {
        Instrument? instrument = _instruments.Find(request.BarType.InstrumentId) ?? Services.Cache.Instrument(request.BarType.InstrumentId);
        if (instrument is null)
        {
            return [];
        }

        IReadOnlyList<Bar> bars = await KucoinHistory
            .FetchBarsAsync(_http, instrument, request.BarType, request.Start, request.End, request.Limit, Clock.Timestamp, ct)
            .ConfigureAwait(false);

        return [.. bars.Cast<IData>()];
    }

    /// <summary>
    /// The last hundred trades, which is all this endpoint keeps. Sizes are contracts, as everywhere else here.
    /// </summary>
    private async Task<IReadOnlyList<IData>> FetchTradesAsync(RequestTradeTicks request, CancellationToken ct)
    {
        Instrument? instrument = _instruments.Find(request.InstrumentId) ?? Services.Cache.Instrument(request.InstrumentId);
        if (instrument is null)
        {
            return [];
        }

        JsonElement data = await _http.GetPublicAsync(
            "/api/v1/trade/history",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["symbol"] = Raw(request.InstrumentId) },
            ct).ConfigureAwait(false);

        List<TradeTick> trades = new();
        if (data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        foreach (JsonElement row in data.EnumerateArray())
        {
            UnixNanos ts = row.Ns("ts");
            if ((request.Start is { } from && ts < from) || (request.End is { } to && ts > to))
            {
                continue;
            }

            trades.Add(new TradeTick(
                instrument.Id,
                instrument.MakePrice(row.Dec("price")),
                KucoinFuturesVenue.ToQuantity(instrument, row.Dec("size")),
                row.Str("side") == "buy" ? AggressorSide.Buyer : AggressorSide.Seller,
                new TradeId(row.Str("tradeId")),
                ts,
                Clock.Timestamp));
        }

        return [.. trades.OrderBy(t => t.TsEvent.Value).TakeLast(request.Limit ?? trades.Count).Cast<IData>()];
    }
}
