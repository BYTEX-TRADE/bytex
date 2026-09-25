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

namespace Bytex.Adapters.Kraken;

/// <summary>
/// The shapes of Kraken's spot socket, version 2, kept together because both clients speak it and because three of
/// them are worth stating once rather than twice.
/// </summary>
internal static class KrakenStream
{
    /// <summary>
    /// What keeps the socket alive. The venue answers <c>{"method":"ping"}</c> with a <c>pong</c> and publishes a
    /// <c>heartbeat</c> channel of its own; pinging is what proves the connection is still two-way rather than
    /// merely open.
    /// </summary>
    public const string PingMessage = """{"method":"ping"}""";

    /// <summary>The channel carrying best bid and ask, with their sizes.</summary>
    public const string TickerChannel = "ticker";

    /// <summary>The channel carrying matched trades.</summary>
    public const string TradeChannel = "trade";

    /// <summary>The channel carrying the order book, as a snapshot and then deltas.</summary>
    public const string BookChannel = "book";

    /// <summary>The channel carrying candles, which arrive close-stamped - see <c>KrakenDataClient</c>.</summary>
    public const string OhlcChannel = "ohlc";

    /// <summary>The book depth asked for. The venue offers 10, 25, 100, 500 and 1000; only the top is published.</summary>
    public const int BookDepth = 10;

    /// <summary>
    /// A subscribe or unsubscribe request. The private channels take a token here as well, which is why the token is
    /// a parameter rather than something the execution client writes its own message for.
    /// </summary>
    public static string Subscribe(object parameters, bool subscribe = true) =>
        JsonSerializer.Serialize(new
        {
            method = subscribe ? "subscribe" : "unsubscribe",
            @params = parameters,
            req_id = Random.Shared.Next(1, int.MaxValue),
        });
}

/// <summary>
/// Market data from Kraken spot: best bid and ask, matched trades, the top of a ten-level book and candles over the
/// socket; bars and recent trades over REST.
/// <para>
/// One thing about this platform decides the shape of this client. The venue serves public market data and private
/// data on two different hosts and refuses each on the other's, saying so in the error text - so this client only
/// ever opens the public one, and the execution client only ever opens the other. There is no connection that can
/// carry both.
/// </para>
/// <para>
/// The candles arrive CLOSE-stamped, which no other venue here does: the <c>ohlc</c> channel carries both
/// <c>interval_begin</c> and <c>timestamp</c>, and the second is the end of the interval the row covers. So a bar's
/// event time is read from the message rather than computed, and the interval is used only to notice when one candle
/// has given way to the next.
/// </para>
/// </summary>
public sealed class KrakenDataClient : DataClientBase
{
    /// <summary>How long after a candle's end the client waits for late updates before it calls the candle closed.</summary>
    private static readonly TimeSpan _closeGrace = TimeSpan.FromSeconds(2);

    private readonly KrakenDataClientConfig _config;
    private readonly KrakenHttp _http;
    private readonly KrakenInstrumentProvider _instruments;
    private readonly Dictionary<string, object> _subscriptions = new(StringComparer.Ordinal);
    private readonly Dictionary<BarType, Candle> _forming = new();
    private readonly Dictionary<InstrumentId, Book> _books = new();
    private readonly object _gate = new();
    private WebSocketClient? _ws;
    private CancellationTokenSource? _closer;

    private sealed record Candle(long CloseNanos, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume);

    /// <summary>
    /// The best of each side of one instrument's book. The venue sends the book as a snapshot and then as deltas
    /// that touch only the levels that moved, so a quote taken straight off a delta would report whatever level
    /// happened to change as though it were the top.
    /// </summary>
    private sealed class Book
    {
        public SortedDictionary<decimal, decimal> Bids { get; } = new(Comparer<decimal>.Create((a, b) => b.CompareTo(a)));

        public SortedDictionary<decimal, decimal> Asks { get; } = new();
    }

    public KrakenDataClient(ClientId clientId, KrakenDataClientConfig config, KernelServices services)
        : base(clientId, KrakenVenue.Venue, services)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        if (_config.ProductType != KrakenProductType.Spot)
        {
            throw new ArgumentException(
                $"This client serves Kraken's spot platform and the configuration says {_config.ProductType}. "
                + "The futures platform is KrakenFuturesDataClient.",
                nameof(config));
        }

        _http = new KrakenHttp(config, Log);
        _instruments = new KrakenInstrumentProvider(_http, config.InstrumentProvider, Log);
    }

    public KrakenInstrumentProvider Instruments => _instruments;

    public override async Task ConnectAsync(CancellationToken ct)
    {
        await _instruments.InitializeAsync(ct).ConfigureAwait(false);
        foreach (Instrument instrument in _instruments.GetAll())
        {
            HandleInstrument(instrument);
        }

        _ws = new WebSocketClient(new WebSocketClientConfig
        {
            Url = new Uri(KrakenVenue.WsBase(_config) + KrakenVenue.WsVersion),
            PingMessage = KrakenStream.PingMessage,
        }, Log)
        {
            OnText = HandleMessageAsync,
            OnConnected = isReconnect =>
            {
                if (isReconnect)
                {
                    // Nothing survives a reconnection here: the venue holds no subscriptions for a connection it
                    // has lost, so a client that did not ask again would sit on an open socket receiving nothing.
                    List<object> again;
                    lock (_gate)
                    {
                        again = _subscriptions.Values.ToList();
                    }

                    foreach (object parameters in again)
                    {
                        _ws?.SendText(KrakenStream.Subscribe(parameters));
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

            case SubscribeQuoteTicks q:
                Add(Key(KrakenStream.TickerChannel, q.InstrumentId), new
                {
                    channel = KrakenStream.TickerChannel,
                    symbol = new[] { Raw(q.InstrumentId) },
                });
                break;

            case SubscribeTradeTicks t:
                Add(Key(KrakenStream.TradeChannel, t.InstrumentId), new
                {
                    channel = KrakenStream.TradeChannel,
                    symbol = new[] { Raw(t.InstrumentId) },
                });
                break;

            case SubscribeOrderBookDeltas d:
                Add(Key(KrakenStream.BookChannel, d.InstrumentId), new
                {
                    channel = KrakenStream.BookChannel,
                    symbol = new[] { Raw(d.InstrumentId) },
                    depth = KrakenStream.BookDepth,
                });
                break;

            case SubscribeBars b when b.BarType.IsExternal:
                {
                    int interval = KrakenVenue.Interval(b.BarType.Spec);
                    lock (_gate)
                    {
                        _candleBars[CandleKey(Raw(b.BarType.InstrumentId), interval)] = b.BarType;
                    }

                    Add(Key(KrakenStream.OhlcChannel, b.BarType.InstrumentId, interval), new
                    {
                        channel = KrakenStream.OhlcChannel,
                        symbol = new[] { Raw(b.BarType.InstrumentId) },
                        interval,
                    });

                    break;
                }

            default:
                Sink.OnSubscriptionFailed(ClientId, command, $"{command.GetType().Name} is not supported by the Kraken spot data client");
                break;
        }
    }

    private readonly Dictionary<string, BarType> _candleBars = new(StringComparer.Ordinal);

    public override Task UnsubscribeAsync(UnsubscribeCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        string? key = command switch
        {
            UnsubscribeQuoteTicks q => Key(KrakenStream.TickerChannel, q.InstrumentId),
            UnsubscribeTradeTicks t => Key(KrakenStream.TradeChannel, t.InstrumentId),
            UnsubscribeOrderBookDeltas d => Key(KrakenStream.BookChannel, d.InstrumentId),
            UnsubscribeBars b => Key(KrakenStream.OhlcChannel, b.BarType.InstrumentId, KrakenVenue.Interval(b.BarType.Spec)),
            _ => null,
        };

        if (key is null)
        {
            return Task.CompletedTask;
        }

        object? parameters;
        lock (_gate)
        {
            if (!_subscriptions.Remove(key, out parameters))
            {
                return Task.CompletedTask;
            }

            if (command is UnsubscribeBars ub)
            {
                _candleBars.Remove(CandleKey(Raw(ub.BarType.InstrumentId), KrakenVenue.Interval(ub.BarType.Spec)));
                _forming.Remove(ub.BarType);
            }

            if (command is UnsubscribeOrderBookDeltas ud)
            {
                _books.Remove(ud.InstrumentId);
            }
        }

        _ws?.SendText(KrakenStream.Subscribe(parameters, subscribe: false));
        return Task.CompletedTask;
    }

    private static string Key(string channel, InstrumentId id, int interval = 0) =>
        string.Create(CultureInfo.InvariantCulture, $"{channel}:{id}:{interval}");

    private static string CandleKey(string rawSymbol, int interval) =>
        string.Create(CultureInfo.InvariantCulture, $"{rawSymbol}:{interval}");

    private void Add(string key, object parameters)
    {
        bool added;
        lock (_gate)
        {
            added = _subscriptions.TryAdd(key, parameters);
        }

        if (added)
        {
            _ws?.SendText(KrakenStream.Subscribe(parameters));
        }
    }

    private static string Raw(InstrumentId id) => KrakenVenue.ToRawSymbol(id);

    private Instrument? InstrumentFor(string rawSymbol)
    {
        InstrumentId id = KrakenVenue.ToInstrumentId(rawSymbol);
        return _instruments.Find(id) ?? Services.Cache.Instrument(id);
    }

    private Task HandleMessageAsync(string text)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            JsonElement root = doc.RootElement;

            // A request's answer names the method it answers, except that a refused subscription answers with the
            // method "subscribe" whatever was asked - an amend, a cancel and an order all come back that way. So a
            // failure is recognised by its success flag rather than by the method it claims.
            if (root.Has("method"))
            {
                if (root.TryGetProperty("success", out JsonElement success) && success.ValueKind == JsonValueKind.False)
                {
                    Log.LogWarning("Kraken spot refused a stream request: {Error}", root.Str("error"));
                }

                return Task.CompletedTask;
            }

            string channel = root.Str("channel");
            if (!root.TryGetProperty("data", out JsonElement data))
            {
                // status and heartbeat, which carry no instrument data.
                return Task.CompletedTask;
            }

            bool snapshot = root.Str("type") == "snapshot";
            switch (channel)
            {
                case KrakenStream.TickerChannel:
                    foreach (JsonElement row in data.EnumerateArray())
                    {
                        HandleTicker(row);
                    }

                    break;

                case KrakenStream.TradeChannel:
                    foreach (JsonElement row in data.EnumerateArray())
                    {
                        HandleTrade(row);
                    }

                    break;

                case KrakenStream.BookChannel:
                    foreach (JsonElement row in data.EnumerateArray())
                    {
                        HandleBook(row, snapshot);
                    }

                    break;

                case KrakenStream.OhlcChannel:
                    foreach (JsonElement row in data.EnumerateArray())
                    {
                        HandleCandle(row);
                    }

                    break;
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            Log.LogWarning(e, "Kraken spot: unreadable stream message");
        }

        return Task.CompletedTask;
    }

    private void HandleTicker(JsonElement d)
    {
        if (InstrumentFor(d.Str("symbol")) is not { } instrument)
        {
            return;
        }

        HandleData(new QuoteTick(
            instrument.Id,
            instrument.MakePrice(d.Dec("bid")),
            instrument.MakePrice(d.Dec("ask")),
            instrument.MakeQuantity(d.Dec("bid_qty")),
            instrument.MakeQuantity(d.Dec("ask_qty")),
            d.Iso("timestamp"),
            Clock.Timestamp));
    }

    private void HandleTrade(JsonElement d)
    {
        if (InstrumentFor(d.Str("symbol")) is not { } instrument)
        {
            return;
        }

        HandleData(new TradeTick(
            instrument.Id,
            instrument.MakePrice(d.Dec("price")),
            instrument.MakeQuantity(d.Dec("qty")),
            d.Str("side") == "buy" ? AggressorSide.Buyer : AggressorSide.Seller,
            new TradeId(d.Str("trade_id")),
            d.Iso("timestamp"),
            Clock.Timestamp));
    }

    /// <summary>
    /// The book, kept so that the top of it is the top of it. The venue sends a snapshot and then deltas in which a
    /// price with a zero quantity is a level that has gone; a quote published straight from a delta would name
    /// whichever level moved as the best one.
    /// </summary>
    private void HandleBook(JsonElement d, bool snapshot)
    {
        if (InstrumentFor(d.Str("symbol")) is not { } instrument)
        {
            return;
        }

        Book book;
        decimal bestBid;
        decimal bestBidQty;
        decimal bestAsk;
        decimal bestAskQty;
        lock (_gate)
        {
            if (!_books.TryGetValue(instrument.Id, out Book? held))
            {
                held = new Book();
                _books[instrument.Id] = held;
            }

            book = held;
            if (snapshot)
            {
                book.Bids.Clear();
                book.Asks.Clear();
            }

            Apply(book.Bids, d, "bids");
            Apply(book.Asks, d, "asks");
            if (book.Bids.Count == 0 || book.Asks.Count == 0)
            {
                return;
            }

            (bestBid, bestBidQty) = (book.Bids.First().Key, book.Bids.First().Value);
            (bestAsk, bestAskQty) = (book.Asks.First().Key, book.Asks.First().Value);
        }

        HandleData(new QuoteTick(
            instrument.Id,
            instrument.MakePrice(bestBid),
            instrument.MakePrice(bestAsk),
            instrument.MakeQuantity(bestBidQty),
            instrument.MakeQuantity(bestAskQty),
            d.Iso("timestamp"),
            Clock.Timestamp));
    }

    private static void Apply(SortedDictionary<decimal, decimal> side, JsonElement d, string name)
    {
        if (!d.TryGetProperty(name, out JsonElement levels) || levels.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement level in levels.EnumerateArray())
        {
            decimal price = level.Dec("price");
            decimal qty = level.Dec("qty");
            if (qty <= 0m)
            {
                side.Remove(price);
                continue;
            }

            side[price] = qty;
        }
    }

    /// <summary>
    /// A candle row. The venue carries the interval's END in <c>timestamp</c> and its start in
    /// <c>interval_begin</c>, so a bar is stamped with what the venue says rather than with a computed sum - and
    /// the two together are what says when one candle has given way to the next.
    /// <para>
    /// The row is the candle as it stands, resent as trades arrive, and the venue never says a candle is closed. So
    /// a candle is published when a row for a later interval arrives, or when its interval has passed by the grace
    /// below - which is what covers an interval with no trade in it, where the venue sends nothing at all.
    /// </para>
    /// </summary>
    private void HandleCandle(JsonElement d)
    {
        BarType barType;
        lock (_gate)
        {
            if (!_candleBars.TryGetValue(CandleKey(d.Str("symbol"), (int)d.Long("interval")), out barType))
            {
                return;
            }
        }

        UnixNanos close = d.Iso("timestamp");
        if (close == default)
        {
            return;
        }

        Candle update = new(
            close.Value,
            d.Dec("open"),
            d.Dec("high"),
            d.Dec("low"),
            d.Dec("close"),
            d.Dec("volume"));

        Candle? closed = null;
        lock (_gate)
        {
            if (_forming.TryGetValue(barType, out Candle? current))
            {
                if (update.CloseNanos < current.CloseNanos)
                {
                    return;
                }

                if (update.CloseNanos > current.CloseNanos)
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
        if ((_instruments.Find(barType.InstrumentId) ?? Services.Cache.Instrument(barType.InstrumentId)) is not { } instrument)
        {
            return;
        }

        UnixNanos close = new(candle.CloseNanos);
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
    /// Closes a candle whose interval has passed. Without this a quiet interval would leave the previous candle
    /// unpublished until the next trade, which on an illiquid pair can be hours - and the bar that eventually
    /// arrived would be the right bar at the wrong time.
    /// </summary>
    private async Task CloseCandlesLoopAsync(CancellationToken ct)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                long now = Clock.Timestamp.Value;
                long grace = _closeGrace.Ticks * UnixNanos.NanosPerTick;
                List<(BarType BarType, Candle Candle)> due = new();
                lock (_gate)
                {
                    foreach ((BarType barType, Candle candle) in _forming.ToList())
                    {
                        if (now < candle.CloseNanos + grace)
                        {
                            continue;
                        }

                        due.Add((barType, candle));

                        // The next interval starts flat at this one's close, so a run of quiet intervals produces a
                        // continuous series rather than a gap.
                        _forming[barType] = new Candle(
                            candle.CloseNanos + barType.Spec.IntervalNanos,
                            candle.Close,
                            candle.Close,
                            candle.Close,
                            candle.Close,
                            0m);
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
                    SendResponse(rb, typeof(Bar), await BarsAsync(rb, ct).ConfigureAwait(false));
                    break;
                case RequestTradeTicks rt:
                    SendResponse(rt, typeof(TradeTick), await TradesAsync(rt, ct).ConfigureAwait(false));
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
                    SendErrorResponse(command, $"{command.GetType().Name} is not supported by the Kraken spot data client");
                    break;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.LogError(e, "Kraken spot request {Request} failed", command.GetType().Name);
            SendErrorResponse(command, e.Message);
        }
    }

    // History goes through KrakenHistory, which a catalog download calls as well, so stored and live bars agree.
    private async Task<IReadOnlyList<IData>> BarsAsync(RequestBars request, CancellationToken ct)
    {
        if ((_instruments.Find(request.BarType.InstrumentId) ?? Services.Cache.Instrument(request.BarType.InstrumentId)) is not { } instrument)
        {
            return [];
        }

        IReadOnlyList<Bar> bars = await KrakenHistory
            .FetchBarsAsync(_http, instrument, request.BarType, request.Start, request.End, request.Limit, Clock.Timestamp, ct)
            .ConfigureAwait(false);

        return [.. bars.Cast<IData>()];
    }

    /// <summary>
    /// Recent trades. The row is a flat array - price, volume, time in FRACTIONAL SECONDS, the taker's side as a
    /// single letter, the order type, a misc field and the trade id - and the fractional seconds are the part worth
    /// noting: read as whole seconds every trade in a second would share a timestamp.
    /// </summary>
    private async Task<IReadOnlyList<IData>> TradesAsync(RequestTradeTicks request, CancellationToken ct)
    {
        if ((_instruments.Find(request.InstrumentId) ?? Services.Cache.Instrument(request.InstrumentId)) is not { } instrument)
        {
            return [];
        }

        JsonElement result = await _http.GetPublicAsync(
            KrakenVenue.RestVersion + "/public/Trades",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["pair"] = Raw(request.InstrumentId) },
            ct).ConfigureAwait(false);

        List<TradeTick> trades = new();
        foreach (JsonProperty property in result.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement row in property.Value.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 7)
                {
                    continue;
                }

                UnixNanos ts = new((long)(row[2].DecValue() * UnixNanos.NanosPerSecond));
                if ((request.Start is { } from && ts < from) || (request.End is { } to && ts > to))
                {
                    continue;
                }

                trades.Add(new TradeTick(
                    instrument.Id,
                    instrument.MakePrice(row[0].DecValue()),
                    instrument.MakeQuantity(row[1].DecValue()),
                    row[3].GetString() == "b" ? AggressorSide.Buyer : AggressorSide.Seller,
                    new TradeId(row[6].LongValue().ToString(CultureInfo.InvariantCulture)),
                    ts,
                    Clock.Timestamp));
            }
        }

        return [.. trades.OrderBy(t => t.TsEvent.Value).TakeLast(request.Limit ?? trades.Count).Cast<IData>()];
    }
}
