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

namespace Bytex.Adapters.Gate;

/// <summary>
/// Market data from Gate's spot market: best bid and ask, matched trades, candles and a twenty-level book over the
/// public socket, bars and recent trades over REST.
/// <para>
/// A separate client from the derivative one, because the two share only the envelope. A spot amount is in base
/// currency and a futures size is a whole number of contracts; a spot candle row is an array and a futures one an
/// object; a spot trade's time is a string of milliseconds and a futures trade's a number of seconds; and the two
/// answer on different hosts under different channel prefixes.
/// </para>
/// </summary>
public sealed class GateDataClient : DataClientBase
{
    private readonly GateDataClientConfig _config;
    private readonly GateHttp _http;
    private readonly GateInstrumentProvider _instruments;
    private readonly GateCandles _candles;
    private readonly Dictionary<string, IReadOnlyList<string>> _subscriptions = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private WebSocketClient? _ws;

    public GateDataClient(ClientId clientId, GateDataClientConfig config, KernelServices services)
        : base(clientId, GateVenue.Venue, services)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        if (_config.ProductType != GateProductType.Spot)
        {
            throw new ArgumentException(
                $"This client serves Gate's spot market and the configuration says {_config.ProductType}. The two "
                + "derivative markets are GateFuturesDataClient and GateDeliveryDataClient.",
                nameof(config));
        }

        _http = new GateHttp(config, Log);
        _instruments = new GateInstrumentProvider(_http, config.InstrumentProvider, Log);
        _candles = new GateCandles();
    }

    public GateInstrumentProvider Instruments => _instruments;

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
                Url = new Uri(GateVenue.WsBase(_config)),
                PingMessage = GateStream.Ping(GateStream.SpotPrefix),
                PingInterval = GateVenue.DefaultPingInterval,
            },
            Log)
        {
            OnText = HandleMessageAsync,
            OnConnected = isReconnect =>
            {
                if (isReconnect)
                {
                    // A reconnected socket carries no subscriptions, so they are asked for again; and the client was
                    // marked disconnected by the drop, so it has to be marked back or the mark stays for good while
                    // data flows.
                    List<KeyValuePair<string, IReadOnlyList<string>>> again;
                    lock (_gate)
                    {
                        again = _subscriptions.ToList();
                    }

                    foreach ((string channel, IReadOnlyList<string> payload) in again)
                    {
                        _ws?.SendText(GateStream.Subscribe(channel, payload));
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
        NotifyConnected();
    }

    public override async Task DisconnectAsync(CancellationToken ct)
    {
        if (_ws is not null)
        {
            await _ws.DisposeAsync().ConfigureAwait(false);
            _ws = null;
        }

        NotifyDisconnected("disconnect requested");
    }

    protected override void OnDispose()
    {
        _ws?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _http.Dispose();
    }

    public override async Task SubscribeAsync(SubscribeCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        switch (command)
        {
            case SubscribeInstruments:
                await _instruments.LoadAllAsync(ct).ConfigureAwait(false);
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

            // book_ticker carries the best bid and ask and nothing else. The tickers channel carries a last price and
            // a day's statistics, which is not a quote and would move a quote on every trade.
            case SubscribeQuoteTicks q:
                Add(GateStream.SpotPrefix + GateStream.BookTicker, [Raw(q.InstrumentId)]);
                break;

            case SubscribeTradeTicks t:
                Add(GateStream.SpotPrefix + GateStream.Trades, [Raw(t.InstrumentId)]);
                break;

            case SubscribeBars b when b.BarType.IsExternal:
                _candles.Track(GateVenue.Interval(b.BarType.Spec) + "_" + Raw(b.BarType.InstrumentId), b.BarType);
                Add(GateStream.SpotPrefix + GateStream.Candlesticks, [GateVenue.Interval(b.BarType.Spec), Raw(b.BarType.InstrumentId)]);
                break;

            case SubscribeOrderBookDeltas d:
                Add(
                    GateStream.SpotPrefix + GateStream.OrderBook,
                    [Raw(d.InstrumentId), GateStream.DepthLevels, GateStream.SpotDepthInterval]);
                break;

            default:
                Sink.OnSubscriptionFailed(ClientId, command, $"{command.GetType().Name} is not supported by the Gate spot data client");
                break;
        }
    }

    public override Task UnsubscribeAsync(UnsubscribeCommand command, CancellationToken ct)
    {
        (string Channel, IReadOnlyList<string> Payload)? topic = command switch
        {
            UnsubscribeQuoteTicks q => (GateStream.SpotPrefix + GateStream.BookTicker, (IReadOnlyList<string>)[Raw(q.InstrumentId)]),
            UnsubscribeTradeTicks t => (GateStream.SpotPrefix + GateStream.Trades, [Raw(t.InstrumentId)]),
            UnsubscribeBars b => (GateStream.SpotPrefix + GateStream.Candlesticks, [GateVenue.Interval(b.BarType.Spec), Raw(b.BarType.InstrumentId)]),
            UnsubscribeOrderBookDeltas d => (GateStream.SpotPrefix + GateStream.OrderBook, [Raw(d.InstrumentId), GateStream.DepthLevels, GateStream.SpotDepthInterval]),
            _ => null,
        };

        if (topic is not { } t2)
        {
            return Task.CompletedTask;
        }

        if (command is UnsubscribeBars ub)
        {
            _candles.Forget(GateVenue.Interval(ub.BarType.Spec) + "_" + Raw(ub.BarType.InstrumentId));
        }

        bool removed;
        lock (_gate)
        {
            removed = _subscriptions.Remove(Key(t2.Channel, t2.Payload));
        }

        if (removed)
        {
            _ws?.SendText(GateStream.Subscribe(t2.Channel, t2.Payload, subscribe: false));
        }

        return Task.CompletedTask;
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
                    if (_instruments.Find(ri.InstrumentId) is { } instrument)
                    {
                        HandleInstrument(instrument);
                    }

                    SendResponse(ri, typeof(Instrument), []);
                    break;

                default:
                    SendErrorResponse(command, $"{command.GetType().Name} is not supported by the Gate spot data client");
                    break;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.LogError(e, "Gate spot request {Request} failed", command.GetType().Name);
            SendErrorResponse(command, e.Message);
        }
    }

    // History is read by GateHistory, which a catalog download calls as well, so stored and live bars agree about
    // what an interval looked like.
    private async Task<IReadOnlyList<IData>> FetchBarsAsync(RequestBars request, CancellationToken ct)
    {
        if (Find(request.BarType.InstrumentId) is not { } instrument)
        {
            return [];
        }

        IReadOnlyList<Bar> bars = await GateHistory
            .FetchBarsAsync(_http, instrument, request.BarType, request.Start, request.End, request.Limit, Clock.Timestamp, ct)
            .ConfigureAwait(false);

        return [.. bars.Cast<IData>()];
    }

    /// <summary>
    /// The recent trades this endpoint keeps. The time field is <c>create_time_ms</c> and is a STRING of
    /// milliseconds with a fractional part - the same field name on the derivative markets carries a number of
    /// seconds, so the two are read by different helpers and each has its own test.
    /// </summary>
    private async Task<IReadOnlyList<IData>> FetchTradesAsync(RequestTradeTicks request, CancellationToken ct)
    {
        if (Find(request.InstrumentId) is not { } instrument)
        {
            return [];
        }

        JsonElement data = await _http.GetPublicAsync(
            "/spot/trades",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["currency_pair"] = Raw(request.InstrumentId) },
            ct).ConfigureAwait(false);

        List<TradeTick> trades = new();
        if (data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        foreach (JsonElement row in data.EnumerateArray())
        {
            UnixNanos ts = row.FractionalMilliseconds("create_time_ms");
            if ((request.Start is { } from && ts < from) || (request.End is { } to && ts > to))
            {
                continue;
            }

            trades.Add(SpotTrade(instrument, row, ts, Clock.Timestamp));
        }

        return [.. trades.OrderBy(t => t.TsEvent.Value).TakeLast(request.Limit ?? trades.Count).Cast<IData>()];
    }

    /// <summary>
    /// A spot trade, over REST or over the socket - the two carry the same fields under the same names, which is not
    /// true of the candles or of the book. "side" is the taker's.
    /// </summary>
    private static TradeTick SpotTrade(Instrument instrument, JsonElement row, UnixNanos ts, UnixNanos init) =>
        new(instrument.Id,
            instrument.MakePrice(row.Dec("price")),
            instrument.MakeQuantity(row.Dec("amount")),
            row.Str("side") == "buy" ? AggressorSide.Buyer : AggressorSide.Seller,
            new TradeId(row.Str("id")),
            ts,
            init);

    private static string Raw(InstrumentId id) => GateVenue.ToRawSymbol(id);

    private static string Key(string channel, IReadOnlyList<string> payload) => channel + "|" + string.Join('|', payload);

    private Instrument? Find(InstrumentId id) => _instruments.Find(id) ?? Services.Cache.Instrument(id);

    private void Add(string channel, IReadOnlyList<string> payload)
    {
        bool added;
        lock (_gate)
        {
            added = _subscriptions.TryAdd(Key(channel, payload), payload);
        }

        if (added)
        {
            _ws?.SendText(GateStream.Subscribe(channel, payload));
        }
    }

    private Task HandleMessageAsync(string text)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            JsonElement root = doc.RootElement;
            string channel = root.Str("channel");
            string @event = root.Str("event");

            // A subscribe answer carries a result with a status, and an error with a code and a message. Neither is
            // data, and a failed subscription is the failure that otherwise looks like a quiet market.
            if (root.Has("error"))
            {
                JsonElement error = root.GetProperty("error");
                Log.LogWarning(
                    "Gate spot refused the {Channel} subscription with {Code}: {Message}",
                    channel,
                    error.Long("code"),
                    error.Str("message"));
                return Task.CompletedTask;
            }

            if (@event != GateStream.UpdateEvent || !root.Has("result"))
            {
                return Task.CompletedTask;
            }

            JsonElement result = root.GetProperty("result");
            switch (channel)
            {
                case GateStream.SpotPrefix + GateStream.BookTicker:
                    HandleBookTicker(result);
                    break;
                case GateStream.SpotPrefix + GateStream.Trades:
                    HandleTrade(result);
                    break;
                case GateStream.SpotPrefix + GateStream.Candlesticks:
                    HandleCandle(result);
                    break;
                case GateStream.SpotPrefix + GateStream.OrderBook:
                    HandleBook(result);
                    break;
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            Log.LogWarning(e, "Gate spot: unreadable stream message");
        }

        return Task.CompletedTask;
    }

    /// <summary>Best bid and ask. The timestamp is in whole milliseconds, which only the sockets are.</summary>
    private void HandleBookTicker(JsonElement d)
    {
        if (Find(GateVenue.ToInstrumentId(d.Str("s"))) is not { } instrument)
        {
            return;
        }

        HandleData(new QuoteTick(
            instrument.Id,
            instrument.MakePrice(d.Dec("b")),
            instrument.MakePrice(d.Dec("a")),
            instrument.MakeQuantity(d.Dec("B")),
            instrument.MakeQuantity(d.Dec("A")),
            d.Ms("t"),
            Clock.Timestamp));
    }

    private void HandleTrade(JsonElement d)
    {
        if (Find(GateVenue.ToInstrumentId(d.Str("currency_pair"))) is not { } instrument)
        {
            return;
        }

        HandleData(SpotTrade(instrument, d, d.FractionalMilliseconds("create_time_ms"), Clock.Timestamp));
    }

    /// <summary>
    /// The forming candle, republished as trades arrive. The row carries <c>w</c> - the venue's own statement that
    /// the interval has closed - and the closing is decided by <see cref="GateCandles"/> so that the two derivative
    /// markets, one of which sends no such flag, behave the same way.
    /// </summary>
    private void HandleCandle(JsonElement d)
    {
        // "n" is the subscription's own name, "1m_BTC_USDT", which is how one channel's messages are told apart.
        if (_candles.BarTypeFor(d.Str("n")) is not { } barType || Find(barType.InstrumentId) is not { } instrument)
        {
            return;
        }

        GateCandle candle = new(
            d.Long("t"),
            d.Dec("o"),
            d.Dec("h"),
            d.Dec("l"),
            d.Dec("c"),

            // "a" is the base volume and "v" the quote volume, which is the reverse of what the letters suggest and
            // the reverse of the REST row's order. A bar carries base volume, so a bar off this venue measures the
            // same thing as a bar off any other.
            d.Dec("a"),
            d.Bool("w"));

        foreach (GateCandle closed in _candles.Accept(barType, candle))
        {
            HandleData(Bar(barType, instrument, closed));
        }

        if (_config.HandleRevisedBars)
        {
            HandleData(Bar(barType, instrument, candle) with { IsRevision = true });
        }
    }

    private Bar Bar(BarType barType, Instrument instrument, GateCandle candle) =>
        new(barType,
            instrument.MakePrice(candle.Open),
            instrument.MakePrice(candle.High),
            instrument.MakePrice(candle.Low),
            instrument.MakePrice(candle.Close),
            instrument.MakeQuantity(candle.Volume),
            new((candle.OpenSeconds * UnixNanos.NanosPerSecond) + barType.Spec.IntervalNanos),
            Clock.Timestamp);

    /// <summary>
    /// A depth snapshot as a quote. The whole of the top twenty levels arrives each time, which is what makes the
    /// best of them readable without holding a book; the levels are [price, size] pairs, as the REST book is.
    /// </summary>
    private void HandleBook(JsonElement d)
    {
        if (Find(GateVenue.ToInstrumentId(d.Str("s"))) is not { } instrument
            || !d.Has("bids") || !d.Has("asks"))
        {
            return;
        }

        JsonElement bids = d.GetProperty("bids");
        JsonElement asks = d.GetProperty("asks");
        if (bids.GetArrayLength() == 0 || asks.GetArrayLength() == 0)
        {
            return;
        }

        HandleData(new QuoteTick(
            instrument.Id,
            instrument.MakePrice(bids[0][0].DecValue()),
            instrument.MakePrice(asks[0][0].DecValue()),
            instrument.MakeQuantity(bids[0][1].DecValue()),
            instrument.MakeQuantity(asks[0][1].DecValue()),
            d.Ms("t"),
            Clock.Timestamp));
    }
}

/// <summary>One candle as a Gate socket sends it, in the units the venue uses: an open time in seconds and a base volume.</summary>
internal sealed record GateCandle(long OpenSeconds, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume, bool WindowClosed);

/// <summary>
/// Which bar type a candle subscription belongs to, and when one of its candles has closed.
/// <para>
/// Shared by all three markets because the closing rule has to be the same on all three and the venue does not make
/// it so: a spot candle carries a <c>w</c> flag that says the interval has closed, a perpetual futures candle
/// carries the same flag, and a DELIVERY candle carries no flag at all. So the flag is used where it is sent and a
/// candle is otherwise closed by the arrival of a later one - which is the only signal every market gives.
/// </para>
/// </summary>
internal sealed class GateCandles
{
    private readonly Dictionary<string, BarType> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<BarType, GateCandle> _forming = new();
    private readonly object _gate = new();

    /// <summary>Remembers that <paramref name="name"/> - the venue's "1m_BTC_USDT" - is this bar type.</summary>
    public void Track(string name, BarType barType)
    {
        lock (_gate)
        {
            _byName[name] = barType;
        }
    }

    public void Forget(string name)
    {
        lock (_gate)
        {
            if (_byName.Remove(name, out BarType barType))
            {
                _forming.Remove(barType);
            }
        }
    }

    public BarType? BarTypeFor(string name)
    {
        lock (_gate)
        {
            return _byName.TryGetValue(name, out BarType barType) ? barType : null;
        }
    }

    /// <summary>
    /// Takes an update and answers with the candles it has just closed - none, or the one it replaced. An update
    /// older than what is held is dropped: the delivery socket sends the previous candle alongside the forming one,
    /// so the same interval arrives again after it has been published.
    /// </summary>
    public IReadOnlyList<GateCandle> Accept(BarType barType, GateCandle update)
    {
        lock (_gate)
        {
            if (_forming.TryGetValue(barType, out GateCandle? current))
            {
                if (update.OpenSeconds < current.OpenSeconds)
                {
                    return [];
                }

                if (update.OpenSeconds > current.OpenSeconds)
                {
                    // The one held can no longer change, so it is closed - and if this one arrives already closed,
                    // both go out together and nothing is left forming.
                    if (update.WindowClosed)
                    {
                        _forming.Remove(barType);
                        return [current, update];
                    }

                    _forming[barType] = update;
                    return [current];
                }
            }

            if (update.WindowClosed)
            {
                // The venue says this interval is done, so nothing more will change it and holding it would only
                // delay the bar until the next interval's first trade - which on a quiet pair is a long time.
                _forming.Remove(barType);
                return [update];
            }

            _forming[barType] = update;
            return [];
        }
    }
}
