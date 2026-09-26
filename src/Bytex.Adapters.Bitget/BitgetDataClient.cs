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

namespace Bytex.Adapters.Bitget;

/// <summary>
/// Market data from Bitget's public stream and REST history, for whichever family the configuration names.
/// <para>
/// One client for all three families, unlike KuCoin's two, because here the family is a string in the subscription
/// rather than a different API: the same socket, the same channel names, the same message shapes, and the same
/// candle rows. What differs between the families is which instrument type they are asked for and how their symbols
/// are spelled, and both of those are the venue helper's business.
/// </para>
/// <para>
/// What does differ from every other adapter here is how a bar is closed. The venue publishes the candle that is
/// still forming and never says it has finished, so a bar is emitted when the next one opens - and, because the venue
/// does not reliably push at the boundary when nothing is trading, also by a timer once the interval has passed.
/// Measured on the quietest contract the venue lists: over two hundred seconds it pushed a candle at one of the three
/// minute boundaries and nothing at the other two.
/// </para>
/// </summary>
public sealed class BitgetDataClient : DataClientBase
{
    /// <summary>
    /// How long after a candle's interval has ended the client waits for a late update before closing it itself. The
    /// venue sometimes pushes the last state of a candle a moment after the boundary, and closing before that arrived
    /// would publish a bar missing its final trades.
    /// </summary>
    private static readonly TimeSpan _closeGrace = TimeSpan.FromSeconds(2);

    /// <summary>How often the closing loop looks for a candle whose interval has ended.</summary>
    private static readonly TimeSpan _closeTick = TimeSpan.FromSeconds(1);

    private readonly BitgetDataClientConfig _config;
    private readonly BitgetHttp _http;
    private readonly BitgetInstrumentProvider _instruments;
    private readonly HashSet<Subscription> _subscriptions = [];
    private readonly Dictionary<Subscription, BarType> _candleSubscriptions = [];
    private readonly Dictionary<BarType, Candle> _forming = [];
    private readonly Dictionary<InstrumentId, Dictionary<decimal, decimal>> _bids = [];
    private readonly Dictionary<InstrumentId, Dictionary<decimal, decimal>> _asks = [];
    private readonly object _gate = new();
    private WebSocketClient? _ws;
    private CancellationTokenSource? _closer;

    /// <summary>One channel of one instrument, which is what the venue's subscription arguments are made of.</summary>
    private sealed record Subscription(string InstType, string Channel, string InstId);

    /// <summary>The candle being built, as the venue last published it.</summary>
    private sealed record Candle(long OpenMs, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume);

    public BitgetDataClient(ClientId clientId, BitgetDataClientConfig config, KernelServices services)
        : base(clientId, BitgetVenue.Venue, services)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        if (_config.ProductType == BitgetProductType.Spot && _config.TradingMode == BitgetTradingMode.Demo)
        {
            throw new ArgumentException(
                "Bitget has no demo spot market - it lists no S-prefixed spot pair - so a demo spot client would have "
                + "nothing to subscribe to. Demo exists for the perpetual product types only.",
                nameof(config));
        }

        _http = new BitgetHttp(config, Log);
        _instruments = new BitgetInstrumentProvider(_http, config.InstrumentProvider, Log);
    }

    public BitgetInstrumentProvider Instruments => _instruments;

    public override async Task ConnectAsync(CancellationToken ct)
    {
        await _instruments.InitializeAsync(ct).ConfigureAwait(false);
        foreach (Instrument instrument in _instruments.GetAll())
        {
            HandleInstrument(instrument);
        }

        _ws = new WebSocketClient(
            new WebSocketClientConfig
            {
                Url = new Uri(BitgetVenue.WsPublic(_config)),
                PingMessage = BitgetVenue.PingMessage,
                PingInterval = BitgetVenue.PingInterval,
            },
            Log)
        {
            OnText = HandleMessageAsync,
            OnConnected = isReconnect =>
            {
                if (isReconnect)
                {
                    List<Subscription> again;
                    lock (_gate)
                    {
                        again = [.. _subscriptions];
                    }

                    if (again.Count > 0)
                    {
                        Send("subscribe", again);
                    }

                    // The drop marked the client disconnected and the socket reconnects by itself: say so, or the
                    // mark stays for the life of the node while data flows.
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

    // ----- subscriptions -----

    public override async Task SubscribeAsync(SubscribeCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
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

            // The ticker channel carries the best bid and ask with their sizes on every market, which is a quote and
            // nothing else. The trade channel is separate, so a quote here does not move on every trade.
            case SubscribeQuoteTicks q:
                Add(Channel(TickerChannel, q.InstrumentId));
                break;
            case SubscribeTradeTicks t:
                Add(Channel(TradeChannel, t.InstrumentId));
                break;
            case SubscribeBars b when b.BarType.IsExternal:
                {
                    Subscription subscription = Channel(CandleChannelPrefix + BitgetVenue.StreamGranularity(b.BarType.Spec), b.BarType.InstrumentId);
                    lock (_gate)
                    {
                        _candleSubscriptions[subscription] = b.BarType;
                    }

                    Add(subscription);
                    break;
                }

            case SubscribeOrderBookDeltas d:
                Add(Channel(BookChannel(d.Depth), d.InstrumentId));
                break;

            // The derivative ticker carries the mark price, the index price, the funding rate and the time of the next
            // settlement, so any of the three subscriptions asks for the same channel. Spot has none of them.
            case SubscribeMarkPrices m when BitgetVenue.IsFutures(_config):
                Add(Channel(TickerChannel, m.InstrumentId));
                break;
            case SubscribeIndexPrices i when BitgetVenue.IsFutures(_config):
                Add(Channel(TickerChannel, i.InstrumentId));
                break;
            case SubscribeFundingRates f when BitgetVenue.IsFutures(_config):
                Add(Channel(TickerChannel, f.InstrumentId));
                break;
            default:
                Sink.OnSubscriptionFailed(
                    ClientId,
                    command,
                    $"{command.GetType().Name} is not supported by the Bitget {_http.Market} data client");
                break;
        }
    }

    public override Task UnsubscribeAsync(UnsubscribeCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        // A book unsubscription names no depth, so which of the venue's three book channels was subscribed is not in
        // the command. Every book channel of that instrument goes, which is what "stop sending me this book" means
        // and is the only answer that cannot leave one of them running.
        if (command is UnsubscribeOrderBookDeltas d)
        {
            string instId = _http.ToRawSymbol(d.InstrumentId);
            List<Subscription> books;
            lock (_gate)
            {
                books = [.. _subscriptions.Where(s => s.InstId == instId && s.Channel.StartsWith(FullBookChannel, StringComparison.Ordinal))];
                foreach (Subscription book in books)
                {
                    _subscriptions.Remove(book);
                }
            }

            if (books.Count > 0)
            {
                Send("unsubscribe", books);
            }

            return Task.CompletedTask;
        }

        Subscription? subscription = command switch
        {
            UnsubscribeQuoteTicks q => Channel(TickerChannel, q.InstrumentId),
            UnsubscribeTradeTicks t => Channel(TradeChannel, t.InstrumentId),
            UnsubscribeBars b => Channel(CandleChannelPrefix + BitgetVenue.StreamGranularity(b.BarType.Spec), b.BarType.InstrumentId),
            UnsubscribeMarkPrices m => Channel(TickerChannel, m.InstrumentId),
            UnsubscribeIndexPrices i => Channel(TickerChannel, i.InstrumentId),
            UnsubscribeFundingRates f => Channel(TickerChannel, f.InstrumentId),
            _ => null,
        };

        if (subscription is null)
        {
            return Task.CompletedTask;
        }

        bool removed;
        lock (_gate)
        {
            removed = _subscriptions.Remove(subscription);
            if (_candleSubscriptions.Remove(subscription, out BarType barType))
            {
                _forming.Remove(barType);
            }
        }

        if (removed)
        {
            Send("unsubscribe", [subscription]);
        }

        return Task.CompletedTask;
    }

    /// <summary>The channel that carries the best bid and ask, and on the derivatives the mark, index and funding.</summary>
    private const string TickerChannel = "ticker";

    /// <summary>The channel that carries matched trades.</summary>
    private const string TradeChannel = "trade";

    /// <summary>
    /// What a candle channel is called before its length: <c>candle1m</c>. The lengths are
    /// <see cref="BitgetVenue.StreamGranularity"/>'s business, because the stream spells them differently from both
    /// REST endpoints.
    /// </summary>
    private const string CandleChannelPrefix = "candle";

    /// <summary>
    /// The channel that carries the whole book as a snapshot followed by deltas. The depth-limited channels are
    /// repeated snapshots instead, which is a different thing and is why the two are handled apart.
    /// </summary>
    private const string FullBookChannel = "books";

    /// <summary>
    /// Which book channel serves a subscription of the given depth. The venue publishes two fixed depths as snapshots
    /// and the whole book as deltas, so a depth it does not publish is served by the whole book rather than by a
    /// nearer snapshot: a caller that asked for fifty levels and was given fifteen would be quietly short.
    /// </summary>
    private static string BookChannel(int depth) => depth switch
    {
        <= 0 => FullBookChannel,
        <= BitgetVenue.SmallBookDepth => FullBookChannel + BitgetVenue.SmallBookDepth.ToString(CultureInfo.InvariantCulture),
        <= BitgetVenue.LargeBookDepth => FullBookChannel + BitgetVenue.LargeBookDepth.ToString(CultureInfo.InvariantCulture),
        _ => FullBookChannel,
    };

    private Subscription Channel(string channel, InstrumentId id) => new(_http.Market, channel, _http.ToRawSymbol(id));

    private void Add(Subscription subscription)
    {
        bool added;
        lock (_gate)
        {
            added = _subscriptions.Add(subscription);
        }

        if (added)
        {
            Send("subscribe", [subscription]);
        }
    }

    private void Send(string op, IReadOnlyList<Subscription> args) =>
        _ws?.SendText(JsonSerializer.Serialize(new
        {
            op,
            args = args.Select(a => new { instType = a.InstType, channel = a.Channel, instId = a.InstId }).ToList(),
        }));

    private Instrument? InstrumentFor(string rawSymbol)
    {
        InstrumentId id = _http.ToInstrumentId(rawSymbol);
        return _instruments.Find(id) ?? Services.Cache.Instrument(id);
    }

    // ----- the stream -----

    private Task HandleMessageAsync(string text)
    {
        // The venue's heartbeat is the four characters "pong" and not an envelope, so parsing it as JSON would log a
        // warning every twenty seconds for the life of the node.
        if (string.Equals(text, BitgetVenue.PongMessage, StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            JsonElement root = doc.RootElement;
            if (root.Has("event"))
            {
                if (string.Equals(root.Str("event"), "error", StringComparison.Ordinal))
                {
                    // A channel name the venue does not publish is refused here and nowhere else - the socket stays
                    // open and simply sends nothing - so a subscription that silently produces no data is only ever
                    // visible in this message.
                    Log.LogWarning(
                        "Bitget refused a stream subscription with {Code}: {Message} ({Arg})",
                        root.Str("code"),
                        root.Str("msg"),
                        root.Has("arg") ? root.GetProperty("arg").GetRawText() : "no argument");
                }

                return Task.CompletedTask;
            }

            if (!root.Has("arg") || !root.Has("data"))
            {
                return Task.CompletedTask;
            }

            JsonElement arg = root.GetProperty("arg");
            JsonElement data = root.GetProperty("data");
            string channel = arg.Str("channel");
            string instId = arg.Str("instId");
            bool snapshot = string.Equals(root.Str("action"), "snapshot", StringComparison.Ordinal);

            if (string.Equals(channel, TickerChannel, StringComparison.Ordinal))
            {
                foreach (JsonElement row in data.EnumerateArray())
                {
                    HandleTicker(row);
                }
            }
            else if (string.Equals(channel, TradeChannel, StringComparison.Ordinal))
            {
                HandleTrades(instId, data);
            }
            else if (channel.StartsWith(CandleChannelPrefix, StringComparison.Ordinal))
            {
                HandleCandle(new Subscription(arg.Str("instType"), channel, instId), data, snapshot);
            }
            else if (channel.StartsWith(FullBookChannel, StringComparison.Ordinal))
            {
                HandleBook(instId, data, snapshot, string.Equals(channel, FullBookChannel, StringComparison.Ordinal));
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException)
        {
            Log.LogWarning(e, "Bitget: unreadable stream message {Text}", LogText.Truncate(text, LogText.MaxMessageLength));
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The ticker row, which is a quote on every market and also the mark price, the index price and the funding rate
    /// on the derivatives. A contract with nothing resting on one side reports that side as null, so a quote is only
    /// published when both sides are there.
    /// </summary>
    private void HandleTicker(JsonElement d)
    {
        Instrument? instrument = InstrumentFor(d.Str("instId"));
        if (instrument is null)
        {
            return;
        }

        UnixNanos ts = d.Has("ts") ? d.Ms("ts") : Clock.Timestamp;
        UnixNanos now = Clock.Timestamp;
        decimal bid = d.Dec("bidPr");
        decimal ask = d.Dec("askPr");
        if (bid > 0m && ask > 0m)
        {
            HandleData(new QuoteTick(
                instrument.Id,
                instrument.MakePrice(bid),
                instrument.MakePrice(ask),
                instrument.MakeQuantity(d.Dec("bidSz")),
                instrument.MakeQuantity(d.Dec("askSz")),
                ts,
                now));
        }

        if (d.Has("markPrice"))
        {
            HandleData(new MarkPriceUpdate(instrument.Id, instrument.MakePrice(d.Dec("markPrice")), ts, now));
        }

        if (d.Has("indexPrice"))
        {
            HandleData(new IndexPriceUpdate(instrument.Id, instrument.MakePrice(d.Dec("indexPrice")), ts, now));
        }

        if (d.Has("fundingRate"))
        {
            HandleData(new FundingRateUpdate(
                instrument.Id,
                d.Dec("fundingRate"),
                d.Has("nextFundingTime") ? d.Ms("nextFundingTime") : null,
                ts,
                now));
        }
    }

    /// <summary>
    /// Matched trades. The rows carry no symbol of their own - it is in the subscription argument - and "side" is the
    /// taker's side.
    /// </summary>
    private void HandleTrades(string instId, JsonElement data)
    {
        Instrument? instrument = InstrumentFor(instId);
        if (instrument is null)
        {
            return;
        }

        foreach (JsonElement t in data.EnumerateArray())
        {
            HandleData(new TradeTick(
                instrument.Id,
                instrument.MakePrice(t.Dec("price")),
                instrument.MakeQuantity(t.Dec("size")),
                string.Equals(t.Str("side"), "buy", StringComparison.Ordinal) ? AggressorSide.Buyer : AggressorSide.Seller,
                new TradeId(t.Str("tradeId")),
                t.Ms("ts"),
                Clock.Timestamp));
        }
    }

    /// <summary>
    /// The order book. The whole-book channel sends a snapshot and then deltas, in which a size of zero removes a
    /// level; the depth-limited channels send a fresh snapshot every time and never a delta, so each of those is
    /// published as a snapshot and the previous one is cleared.
    /// </summary>
    private void HandleBook(string instId, JsonElement data, bool snapshot, bool deltas)
    {
        Instrument? instrument = InstrumentFor(instId);
        if (instrument is null)
        {
            return;
        }

        foreach (JsonElement d in data.EnumerateArray())
        {
            UnixNanos ts = d.Has("ts") ? d.Ms("ts") : Clock.Timestamp;
            UnixNanos now = Clock.Timestamp;
            ulong sequence = (ulong)d.Long("seq");
            bool clear = snapshot || !deltas;
            List<OrderBookDelta> book = [];
            if (clear)
            {
                book.Add(OrderBookDelta.Clear(instrument.Id, sequence, ts, now));
            }

            ulong orderId = 0;
            AddLevels(book, d, "bids", OrderSide.Buy, instrument, clear, sequence, ts, now, ref orderId);
            AddLevels(book, d, "asks", OrderSide.Sell, instrument, clear, sequence, ts, now, ref orderId);

            HandleData(new OrderBookDeltas(
                instrument.Id,
                book,
                clear ? RecordFlags.Snapshot | RecordFlags.Last : RecordFlags.Last,
                sequence,
                ts,
                now));

            TrackTopOfBook(instrument, d, clear, ts, now);
        }
    }

    private static void AddLevels(
        List<OrderBookDelta> book,
        JsonElement d,
        string name,
        OrderSide side,
        Instrument instrument,
        bool clear,
        ulong sequence,
        UnixNanos ts,
        UnixNanos now,
        ref ulong orderId)
    {
        if (!d.Has(name))
        {
            return;
        }

        foreach (JsonElement level in d.GetProperty(name).EnumerateArray())
        {
            decimal size = level[1].DecValue();
            book.Add(new OrderBookDelta(
                instrument.Id,
                size == 0m ? BookAction.Delete : clear ? BookAction.Add : BookAction.Update,
                new BookOrder(side, instrument.MakePrice(level[0].DecValue()), instrument.MakeQuantity(size), ++orderId),
                RecordFlags.None,
                sequence,
                ts,
                now));
        }
    }

    /// <summary>
    /// The best bid and ask of the book, published as a quote as well.
    /// <para>
    /// A book subscription is the only way to get a quote at a depth on this venue: the ticker channel carries one
    /// level. Keeping the sides here is what makes it possible on the delta channel, where a message may touch
    /// neither top level and the top of the book is still whatever the last snapshot and every delta since made it.
    /// </para>
    /// </summary>
    private void TrackTopOfBook(Instrument instrument, JsonElement d, bool clear, UnixNanos ts, UnixNanos now)
    {
        decimal bid;
        decimal ask;
        decimal bidSize;
        decimal askSize;
        lock (_gate)
        {
            if (clear)
            {
                _bids.Remove(instrument.Id);
                _asks.Remove(instrument.Id);
            }

            Dictionary<decimal, decimal> bids = Side(_bids, instrument.Id);
            Dictionary<decimal, decimal> asks = Side(_asks, instrument.Id);
            Apply(bids, d, "bids");
            Apply(asks, d, "asks");
            if (bids.Count == 0 || asks.Count == 0)
            {
                return;
            }

            bid = bids.Keys.Max();
            ask = asks.Keys.Min();
            bidSize = bids[bid];
            askSize = asks[ask];
        }

        HandleData(new QuoteTick(
            instrument.Id,
            instrument.MakePrice(bid),
            instrument.MakePrice(ask),
            instrument.MakeQuantity(bidSize),
            instrument.MakeQuantity(askSize),
            ts,
            now));
    }

    private static Dictionary<decimal, decimal> Side(Dictionary<InstrumentId, Dictionary<decimal, decimal>> sides, InstrumentId id)
    {
        if (!sides.TryGetValue(id, out Dictionary<decimal, decimal>? levels))
        {
            levels = [];
            sides[id] = levels;
        }

        return levels;
    }

    private static void Apply(Dictionary<decimal, decimal> levels, JsonElement d, string name)
    {
        if (!d.Has(name))
        {
            return;
        }

        foreach (JsonElement level in d.GetProperty(name).EnumerateArray())
        {
            decimal price = level[0].DecValue();
            decimal size = level[1].DecValue();
            if (size == 0m)
            {
                levels.Remove(price);
                continue;
            }

            levels[price] = size;
        }
    }

    /// <summary>
    /// A candle. The venue publishes the one that is still forming and never says that a candle has finished, so a bar
    /// is emitted when the open time advances - the previous candle's last published state is then final.
    /// <para>
    /// Only the LAST row of a subscription's opening snapshot is read. The venue answers a candle subscription with
    /// hundreds of historical rows - a one-minute subscription arrived with eight hours of them - and every one but
    /// the last has already closed. Publishing them would turn a subscription into an unasked-for history request,
    /// with no window, no limit and no way for a caller to have wanted it; history is <c>RequestBars</c>'s job. The
    /// last row is the candle being built, which is exactly the seed this needs.
    /// </para>
    /// </summary>
    private void HandleCandle(Subscription subscription, JsonElement data, bool snapshot)
    {
        BarType barType;
        lock (_gate)
        {
            if (!_candleSubscriptions.TryGetValue(subscription, out barType))
            {
                return;
            }
        }

        int length = data.GetArrayLength();
        if (length == 0)
        {
            return;
        }

        if (snapshot)
        {
            JsonElement last = data[length - 1];
            Candle seed = Read(last);
            lock (_gate)
            {
                _forming[barType] = seed;
            }

            return;
        }

        foreach (JsonElement row in data.EnumerateArray())
        {
            Candle update = Read(row);
            Candle? closed = null;
            lock (_gate)
            {
                if (_forming.TryGetValue(barType, out Candle? current))
                {
                    if (update.OpenMs < current.OpenMs)
                    {
                        // A row older than the one being built, which the venue sends when a subscription is repeated.
                        continue;
                    }

                    if (update.OpenMs > current.OpenMs)
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
    }

    private static Candle Read(JsonElement row) => new(
        row[0].LongValue(),
        row[1].DecValue(),
        row[2].DecValue(),
        row[3].DecValue(),
        row[4].DecValue(),
        row[5].DecValue());

    private void Publish(BarType barType, Candle candle, bool revision)
    {
        Instrument? instrument = _instruments.Find(barType.InstrumentId) ?? Services.Cache.Instrument(barType.InstrumentId);
        if (instrument is null)
        {
            return;
        }

        UnixNanos close = UnixNanos.FromMilliseconds(candle.OpenMs).AddNanos(barType.Spec.IntervalNanos);
        Bar bar = new(
            barType,
            instrument.MakePrice(candle.Open),
            instrument.MakePrice(candle.High),
            instrument.MakePrice(candle.Low),
            instrument.MakePrice(candle.Close),
            instrument.MakeQuantity(candle.Volume),
            close,
            Clock.Timestamp);

        HandleData(revision ? bar with { IsRevision = true } : bar);
    }

    /// <summary>
    /// Closes a candle whose interval has ended and which the venue has not replaced.
    /// <para>
    /// Measured on the quietest contract the venue lists, over two hundred seconds: it pushed a fresh zero-volume
    /// candle at one of the three minute boundaries in that window and nothing at the other two. So waiting for the
    /// next candle to arrive would publish a bar minutes late on a quiet instrument, or never - and a strategy on
    /// bars would simply stop being called.
    /// </para>
    /// </summary>
    private async Task CloseCandlesLoopAsync(CancellationToken ct)
    {
        using PeriodicTimer timer = new(_closeTick);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                long now = Clock.Timestamp.Value;
                List<(BarType BarType, Candle Candle)> due = [];
                lock (_gate)
                {
                    foreach ((BarType barType, Candle candle) in _forming.ToList())
                    {
                        long interval = barType.Spec.IntervalNanos;
                        long end = (candle.OpenMs * UnixNanos.NanosPerMillisecond) + interval;
                        if (candle.OpenMs > 0 && now >= end + (_closeGrace.Ticks * UnixNanos.NanosPerTick))
                        {
                            due.Add((barType, candle));

                            // The next interval starts flat at the close that has just been published, so a quiet
                            // stretch produces the flat bars it really was rather than a gap.
                            long next = candle.OpenMs + (interval / UnixNanos.NanosPerMillisecond);
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

    // ----- historical requests -----

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
                case RequestFundingRates rf when BitgetVenue.IsFutures(_config):
                    SendResponse(rf, typeof(FundingRateUpdate), await FetchFundingRatesAsync(rf, ct).ConfigureAwait(false));
                    break;
                case RequestInstrument ri:
                    await _instruments.LoadAsync(ri.InstrumentId, ct).ConfigureAwait(false);
                    if (_instruments.Find(ri.InstrumentId) is { } instrument)
                    {
                        HandleInstrument(instrument);
                    }

                    SendResponse(ri, typeof(Instrument), []);
                    break;
                default:
                    SendErrorResponse(command, $"{command.GetType().Name} is not supported by the Bitget {_http.Market} data client");
                    break;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.LogError(e, "Bitget request {Request} failed", command.GetType().Name);
            SendErrorResponse(command, e.Message);
        }
    }

    // History is read by BitgetHistory, which a catalog download calls as well, so stored bars and a node's bars come
    // from one piece of code rather than two that agree until the venue changes a default.
    private async Task<IReadOnlyList<IData>> FetchBarsAsync(RequestBars request, CancellationToken ct)
    {
        Instrument? instrument = _instruments.Find(request.BarType.InstrumentId) ?? Services.Cache.Instrument(request.BarType.InstrumentId);
        if (instrument is null)
        {
            return [];
        }

        IReadOnlyList<Bar> bars = await BitgetHistory
            .FetchBarsAsync(_http, instrument, request.BarType, request.Start, request.End, request.Limit, Clock.Timestamp, ct)
            .ConfigureAwait(false);

        return [.. bars.Cast<IData>()];
    }

    private async Task<IReadOnlyList<IData>> FetchFundingRatesAsync(RequestFundingRates request, CancellationToken ct)
    {
        if (_instruments.Find(request.InstrumentId) is null && Services.Cache.Instrument(request.InstrumentId) is null)
        {
            return [];
        }

        IReadOnlyList<FundingRateUpdate> rates = await BitgetHistory
            .FetchFundingRatesAsync(_http, request.InstrumentId, request.Start?.ToMilliseconds(), request.End?.ToMilliseconds(), request.Limit, ct)
            .ConfigureAwait(false);

        return [.. rates.Cast<IData>()];
    }

    /// <summary>The recent trades the venue keeps. One page, newest first, turned round to oldest first.</summary>
    private async Task<IReadOnlyList<IData>> FetchTradesAsync(RequestTradeTicks request, CancellationToken ct)
    {
        Instrument? instrument = _instruments.Find(request.InstrumentId) ?? Services.Cache.Instrument(request.InstrumentId);
        if (instrument is null)
        {
            return [];
        }

        Dictionary<string, string> query = _http.Query(
            ("symbol", _http.ToRawSymbol(request.InstrumentId)),
            ("limit", Math.Min(RecentTradePage, request.Limit ?? RecentTradePage).ToString(CultureInfo.InvariantCulture)));

        string path = _http.ProductType == BitgetProductType.Spot ? SpotFillsPath : FuturesFillsPath;
        JsonElement data = await _http.GetPublicAsync(path, query, ct).ConfigureAwait(false);
        List<TradeTick> trades = [];
        if (data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        foreach (JsonElement row in data.EnumerateArray())
        {
            UnixNanos ts = row.Ms("ts");
            if ((request.Start is { } from && ts < from) || (request.End is { } to && ts > to))
            {
                continue;
            }

            trades.Add(new TradeTick(
                instrument.Id,
                instrument.MakePrice(row.Dec("price")),
                instrument.MakeQuantity(row.Dec("size")),
                string.Equals(row.Str("side"), "buy", StringComparison.Ordinal) ? AggressorSide.Buyer : AggressorSide.Seller,
                new TradeId(row.Str("tradeId")),
                ts,
                Clock.Timestamp));
        }

        return [.. trades.OrderBy(t => t.TsEvent.Value).Cast<IData>()];
    }

    /// <summary>Where the recent public trades of a spot pair answer.</summary>
    private const string SpotFillsPath = "/api/v2/spot/market/fills";

    /// <summary>Where the recent public trades of a contract answer.</summary>
    private const string FuturesFillsPath = "/api/v2/mix/market/fills";

    /// <summary>
    /// The most recent trades one request answers with, measured by asking for more on both endpoints: 100, 500, 501
    /// and 1000 all answered with exactly a hundred rows and code 00000. The cap is SILENT here as it is on the
    /// funding history, so asking for the documented maximum would look like it had worked.
    /// </summary>
    private const int RecentTradePage = 100;
}

public sealed class BitgetDataClientFactory : IDataClientFactory
{
    public string Name => "BITGET";

    public Type ConfigType => typeof(BitgetDataClientConfig);

    public IDataClient Create(ClientId clientId, DataClientConfig config, KernelServices services) =>
        new BitgetDataClient(clientId, (BitgetDataClientConfig)config, services);
}
