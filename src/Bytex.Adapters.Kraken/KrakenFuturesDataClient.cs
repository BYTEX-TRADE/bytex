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
/// The shapes of Kraken's futures socket, version 1. Nothing about it is shared with the spot socket beyond being a
/// websocket: the spot one takes a <c>method</c> and a <c>params</c> object and answers with channels, and this one
/// takes an <c>event</c> and a <c>feed</c> and answers with feeds.
/// </summary>
internal static class KrakenFuturesStream
{
    /// <summary>
    /// The feed carrying best bid and ask, the mark and index prices, and the funding rate with the time of the next
    /// settlement - four kinds of data on one feed, which is why one subscription answers several commands.
    /// </summary>
    public const string TickerFeed = "ticker";

    /// <summary>The feed carrying matched trades, as a snapshot and then one message per trade.</summary>
    public const string TradeFeed = "trade";

    /// <summary>The feed carrying the book, as a snapshot and then one message per level that moves.</summary>
    public const string BookFeed = "book";

    /// <summary>
    /// The prefix of the candle feeds. The length is part of the feed NAME rather than a parameter -
    /// <c>candles_trade_1m</c> - so a client subscribes to a different feed per bar length.
    /// <para>
    /// <c>trade</c> is the only tick type worth subscribing to: the service also publishes mark and spot candles and
    /// their volume measured zero on every row, which would produce a bar whose volume is a lie rather than a bar
    /// with no volume.
    /// </para>
    /// </summary>
    public const string CandleFeedPrefix = "candles_" + KrakenFuturesVenue.TradeCandles + "_";

    /// <summary>The suffix the venue adds to a candle feed's name on the first message after subscribing.</summary>
    public const string SnapshotSuffix = "_snapshot";

    public static string CandleFeed(BarSpecification spec) => CandleFeedPrefix + KrakenFuturesVenue.Resolution(spec);

    public static string Subscribe(string feed, string? productId = null, bool subscribe = true) =>
        JsonSerializer.Serialize(productId is null
            ? new { @event = subscribe ? "subscribe" : "unsubscribe", feed }
            : (object)new { @event = subscribe ? "subscribe" : "unsubscribe", feed, product_ids = new[] { productId } });
}

/// <summary>
/// Market data from Kraken's futures platform: best bid and ask, matched trades, the top of the book, candles, and
/// the mark price and funding rate a perpetual is priced against. Bars come over the charts service.
/// <para>
/// A separate client from the spot one rather than a platform switch inside it, because the two share nothing but a
/// key shape. Different host, different message envelope, different subscription grammar, timestamps in
/// milliseconds where spot writes ISO-8601 instants, and candles stamped by their OPEN where spot stamps by the
/// close.
/// </para>
/// </summary>
public sealed class KrakenFuturesDataClient : DataClientBase
{
    /// <summary>How long after a candle's end the client waits for late updates before it calls the candle closed.</summary>
    private static readonly TimeSpan _closeGrace = TimeSpan.FromSeconds(2);

    private readonly KrakenDataClientConfig _config;
    private readonly KrakenHttp _http;
    private readonly KrakenFuturesInstrumentProvider _instruments;
    private readonly Dictionary<string, (string Feed, string? ProductId)> _subscriptions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BarType> _candleBars = new(StringComparer.Ordinal);
    private readonly Dictionary<BarType, Candle> _forming = new();
    private readonly Dictionary<InstrumentId, Book> _books = new();
    private readonly object _gate = new();
    private WebSocketClient? _ws;
    private CancellationTokenSource? _closer;

    private sealed record Candle(long OpenMs, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume);

    private sealed class Book
    {
        public SortedDictionary<decimal, decimal> Bids { get; } = new(Comparer<decimal>.Create((a, b) => b.CompareTo(a)));

        public SortedDictionary<decimal, decimal> Asks { get; } = new();
    }

    public KrakenFuturesDataClient(ClientId clientId, KrakenDataClientConfig config, KernelServices services)
        : base(clientId, KrakenVenue.Venue, services)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        if (_config.ProductType != KrakenProductType.Futures)
        {
            throw new ArgumentException(
                $"This client serves Kraken's futures platform and the configuration says {_config.ProductType}. "
                + "The spot platform is KrakenDataClient.",
                nameof(config));
        }

        _http = new KrakenHttp(config, Log);
        _instruments = new KrakenFuturesInstrumentProvider(_http, config.InstrumentProvider, Log);
    }

    public KrakenFuturesInstrumentProvider Instruments => _instruments;

    public override async Task ConnectAsync(CancellationToken ct)
    {
        await _instruments.InitializeAsync(ct).ConfigureAwait(false);
        foreach (Instrument instrument in _instruments.GetAll())
        {
            HandleInstrument(instrument);
        }

        _ws = new WebSocketClient(new WebSocketClientConfig
        {
            Url = new Uri(KrakenVenue.WsBase(_config) + KrakenFuturesVenue.WsPath),
        }, Log)
        {
            OnText = HandleMessageAsync,
            OnConnected = isReconnect =>
            {
                if (isReconnect)
                {
                    List<(string Feed, string? ProductId)> again;
                    lock (_gate)
                    {
                        again = _subscriptions.Values.ToList();
                    }

                    foreach ((string feed, string? productId) in again)
                    {
                        _ws?.SendText(KrakenFuturesStream.Subscribe(feed, productId));
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

            // One feed answers four commands, because the venue puts the quote, the mark price, the index price and
            // the funding rate on it together. Asking for any of them asks for the same feed once.
            case SubscribeQuoteTicks q:
                Add(KrakenFuturesStream.TickerFeed, Raw(q.InstrumentId));
                break;
            case SubscribeMarkPrices m:
                Add(KrakenFuturesStream.TickerFeed, Raw(m.InstrumentId));
                break;
            case SubscribeFundingRates f:
                Add(KrakenFuturesStream.TickerFeed, Raw(f.InstrumentId));
                break;

            case SubscribeTradeTicks t:
                Add(KrakenFuturesStream.TradeFeed, Raw(t.InstrumentId));
                break;

            case SubscribeOrderBookDeltas d:
                Add(KrakenFuturesStream.BookFeed, Raw(d.InstrumentId));
                break;

            case SubscribeBars b when b.BarType.IsExternal:
                {
                    string feed = KrakenFuturesStream.CandleFeed(b.BarType.Spec);
                    lock (_gate)
                    {
                        _candleBars[CandleKey(feed, Raw(b.BarType.InstrumentId))] = b.BarType;
                    }

                    Add(feed, Raw(b.BarType.InstrumentId));
                    break;
                }

            default:
                Sink.OnSubscriptionFailed(ClientId, command, $"{command.GetType().Name} is not supported by the Kraken futures data client");
                break;
        }
    }

    public override Task UnsubscribeAsync(UnsubscribeCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        (string Feed, string ProductId)? target = command switch
        {
            UnsubscribeQuoteTicks q => (KrakenFuturesStream.TickerFeed, Raw(q.InstrumentId)),
            UnsubscribeMarkPrices m => (KrakenFuturesStream.TickerFeed, Raw(m.InstrumentId)),
            UnsubscribeFundingRates f => (KrakenFuturesStream.TickerFeed, Raw(f.InstrumentId)),
            UnsubscribeTradeTicks t => (KrakenFuturesStream.TradeFeed, Raw(t.InstrumentId)),
            UnsubscribeOrderBookDeltas d => (KrakenFuturesStream.BookFeed, Raw(d.InstrumentId)),
            UnsubscribeBars b => (KrakenFuturesStream.CandleFeed(b.BarType.Spec), Raw(b.BarType.InstrumentId)),
            _ => null,
        };

        if (target is not { } t2)
        {
            return Task.CompletedTask;
        }

        bool removed;
        lock (_gate)
        {
            removed = _subscriptions.Remove(CandleKey(t2.Feed, t2.ProductId));
            if (command is UnsubscribeBars ub)
            {
                _candleBars.Remove(CandleKey(t2.Feed, t2.ProductId));
                _forming.Remove(ub.BarType);
            }

            if (command is UnsubscribeOrderBookDeltas ud)
            {
                _books.Remove(ud.InstrumentId);
            }
        }

        if (removed)
        {
            _ws?.SendText(KrakenFuturesStream.Subscribe(t2.Feed, t2.ProductId, subscribe: false));
        }

        return Task.CompletedTask;
    }

    private static string CandleKey(string feed, string productId) => feed + ":" + productId;

    private void Add(string feed, string productId)
    {
        bool added;
        lock (_gate)
        {
            added = _subscriptions.TryAdd(CandleKey(feed, productId), (feed, productId));
        }

        if (added)
        {
            _ws?.SendText(KrakenFuturesStream.Subscribe(feed, productId));
        }
    }

    private static string Raw(InstrumentId id) => KrakenFuturesVenue.ToRawSymbol(id);

    private Instrument? InstrumentFor(string rawSymbol)
    {
        InstrumentId id = KrakenFuturesVenue.ToInstrumentId(rawSymbol);
        return _instruments.Find(id) ?? Services.Cache.Instrument(id);
    }

    private Task HandleMessageAsync(string text)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            JsonElement root = doc.RootElement;

            // An acknowledgement, an alert or the version banner. A refused subscription arrives as an alert with a
            // message and no feed at all - "Couldn't subscribe to invalid feed" - so there is nothing to correlate
            // it with beyond the order requests were sent in.
            if (root.Has("event"))
            {
                if (root.Str("event") == "alert")
                {
                    Log.LogWarning("Kraken futures stream alert: {Message}", root.Str("message"));
                }

                return Task.CompletedTask;
            }

            string feed = root.Str("feed");
            switch (feed)
            {
                case KrakenFuturesStream.TickerFeed:
                    HandleTicker(root);
                    break;

                case KrakenFuturesStream.TradeFeed:
                    HandleTrade(root);
                    break;

                case KrakenFuturesStream.TradeFeed + KrakenFuturesStream.SnapshotSuffix:
                    if (root.TryGetProperty("trades", out JsonElement trades) && trades.ValueKind == JsonValueKind.Array)
                    {
                        // Newest first in the snapshot, which is the opposite of the order everything downstream
                        // expects, so it is reversed before publishing.
                        foreach (JsonElement trade in trades.EnumerateArray().Reverse())
                        {
                            HandleTrade(trade);
                        }
                    }

                    break;

                case KrakenFuturesStream.BookFeed:
                    HandleBookDelta(root);
                    break;

                case KrakenFuturesStream.BookFeed + KrakenFuturesStream.SnapshotSuffix:
                    HandleBookSnapshot(root);
                    break;

                default:
                    if (feed.StartsWith(KrakenFuturesStream.CandleFeedPrefix, StringComparison.Ordinal))
                    {
                        HandleCandle(feed, root);
                    }

                    break;
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            Log.LogWarning(e, "Kraken futures: unreadable stream message");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The ticker, which carries four different kinds of data. Two things about it are this platform's own: the
    /// funding rate appears twice, as an absolute charge per contract in <c>funding_rate</c> and as a fraction of
    /// notional in <c>relative_funding_rate</c>, and only the second is a rate; and the venue publishes the time of
    /// the NEXT settlement, which most venues do not, so a position's next charge can be priced rather than guessed.
    /// </summary>
    private void HandleTicker(JsonElement d)
    {
        if (InstrumentFor(d.Str("product_id")) is not { } instrument)
        {
            return;
        }

        UnixNanos ts = d.Ms("time");
        if (d.Has("bid") && d.Has("ask"))
        {
            HandleData(new QuoteTick(
                instrument.Id,
                instrument.MakePrice(d.Dec("bid")),
                instrument.MakePrice(d.Dec("ask")),
                instrument.MakeQuantity(d.Dec("bid_size")),
                instrument.MakeQuantity(d.Dec("ask_size")),
                ts,
                Clock.Timestamp));
        }

        if (d.Has("markPrice"))
        {
            HandleData(new MarkPriceUpdate(instrument.Id, instrument.MakePrice(d.Dec("markPrice")), ts, Clock.Timestamp));
        }

        if (d.Has("index"))
        {
            HandleData(new IndexPriceUpdate(instrument.Id, instrument.MakePrice(d.Dec("index")), ts, Clock.Timestamp));
        }

        if (d.Has("relative_funding_rate"))
        {
            HandleData(new FundingRateUpdate(
                instrument.Id,
                d.Dec("relative_funding_rate"),
                d.Has("next_funding_rate_time") ? d.Ms("next_funding_rate_time") : null,
                ts,
                Clock.Timestamp));
        }
    }

    private void HandleTrade(JsonElement d)
    {
        if (InstrumentFor(d.Str("product_id")) is not { } instrument)
        {
            return;
        }

        HandleData(new TradeTick(
            instrument.Id,
            instrument.MakePrice(d.Dec("price")),
            instrument.MakeQuantity(d.Dec("qty")),
            d.Str("side") == "buy" ? AggressorSide.Buyer : AggressorSide.Seller,
            new TradeId(d.Str("uid")),
            d.Ms("time"),
            Clock.Timestamp));
    }

    private void HandleBookSnapshot(JsonElement d)
    {
        if (InstrumentFor(d.Str("product_id")) is not { } instrument)
        {
            return;
        }

        lock (_gate)
        {
            Book book = new();
            Fill(book.Bids, d, "bids");
            Fill(book.Asks, d, "asks");
            _books[instrument.Id] = book;
        }

        PublishTop(instrument, d.Ms("timestamp"));
    }

    /// <summary>
    /// One level of the book. The venue sends the side as a word, one price at a time, and a zero quantity means the
    /// level has gone - so a quote read straight off this message would report whichever level moved as the best.
    /// </summary>
    private void HandleBookDelta(JsonElement d)
    {
        if (InstrumentFor(d.Str("product_id")) is not { } instrument)
        {
            return;
        }

        lock (_gate)
        {
            if (!_books.TryGetValue(instrument.Id, out Book? book))
            {
                // A delta before the snapshot has nothing to be a delta of. Applying it would build a book out of
                // whichever levels happened to move.
                return;
            }

            SortedDictionary<decimal, decimal> side = d.Str("side") == "buy" ? book.Bids : book.Asks;
            decimal price = d.Dec("price");
            decimal qty = d.Dec("qty");
            if (qty <= 0m)
            {
                side.Remove(price);
            }
            else
            {
                side[price] = qty;
            }
        }

        PublishTop(instrument, d.Ms("timestamp"));
    }

    private static void Fill(SortedDictionary<decimal, decimal> side, JsonElement d, string name)
    {
        if (!d.TryGetProperty(name, out JsonElement levels) || levels.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement level in levels.EnumerateArray())
        {
            decimal qty = level.Dec("qty");
            if (qty > 0m)
            {
                side[level.Dec("price")] = qty;
            }
        }
    }

    private void PublishTop(Instrument instrument, UnixNanos ts)
    {
        decimal bid;
        decimal bidQty;
        decimal ask;
        decimal askQty;
        lock (_gate)
        {
            if (!_books.TryGetValue(instrument.Id, out Book? book) || book.Bids.Count == 0 || book.Asks.Count == 0)
            {
                return;
            }

            (bid, bidQty) = (book.Bids.First().Key, book.Bids.First().Value);
            (ask, askQty) = (book.Asks.First().Key, book.Asks.First().Value);
        }

        HandleData(new QuoteTick(
            instrument.Id,
            instrument.MakePrice(bid),
            instrument.MakePrice(ask),
            instrument.MakeQuantity(bidQty),
            instrument.MakeQuantity(askQty),
            ts,
            Clock.Timestamp));
    }

    /// <summary>
    /// A candle. The venue stamps it with the interval's OPEN in milliseconds - the opposite of the spot socket,
    /// which stamps the close - so the close is computed here and a bar carries the close as every stored bar does.
    /// <para>
    /// The message arrives under two feed names: the first one after subscribing is <c>..._snapshot</c> and the rest
    /// are the plain feed, and both carry the same single candle. So the suffix is stripped to find which bar type
    /// the message belongs to, and a client that matched the feed name exactly would ignore the first candle of
    /// every subscription.
    /// </para>
    /// </summary>
    private void HandleCandle(string feed, JsonElement root)
    {
        if (!root.TryGetProperty("candle", out JsonElement c))
        {
            return;
        }

        string plain = feed.EndsWith(KrakenFuturesStream.SnapshotSuffix, StringComparison.Ordinal)
            ? feed[..^KrakenFuturesStream.SnapshotSuffix.Length]
            : feed;

        BarType barType;
        lock (_gate)
        {
            if (!_candleBars.TryGetValue(CandleKey(plain, root.Str("product_id")), out barType))
            {
                return;
            }
        }

        Candle update = new(c.Long("time"), c.Dec("open"), c.Dec("high"), c.Dec("low"), c.Dec("close"), c.Dec("volume"));
        Candle? closed = null;
        lock (_gate)
        {
            if (_forming.TryGetValue(barType, out Candle? current))
            {
                if (update.OpenMs < current.OpenMs)
                {
                    return;
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

    private void Publish(BarType barType, Candle candle, bool revision)
    {
        if ((_instruments.Find(barType.InstrumentId) ?? Services.Cache.Instrument(barType.InstrumentId)) is not { } instrument)
        {
            return;
        }

        UnixNanos close = new((candle.OpenMs * UnixNanos.NanosPerMillisecond) + barType.Spec.IntervalNanos);
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
    /// Closes a candle whose interval has passed, as the spot client does and for the same reason: the venue sends
    /// nothing for an interval with no trade in it, so a quiet stretch would leave the last candle unpublished.
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
                        long end = (candle.OpenMs * UnixNanos.NanosPerMillisecond) + barType.Spec.IntervalNanos;
                        if (candle.OpenMs <= 0 || now < end + grace)
                        {
                            continue;
                        }

                        due.Add((barType, candle));
                        _forming[barType] = new Candle(
                            end / UnixNanos.NanosPerMillisecond,
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
                case RequestFundingRates rf:
                    SendResponse(rf, typeof(FundingRateUpdate), await FundingAsync(rf, ct).ConfigureAwait(false));
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
                    SendErrorResponse(command, $"{command.GetType().Name} is not supported by the Kraken futures data client");
                    break;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.LogError(e, "Kraken futures request {Request} failed", command.GetType().Name);
            SendErrorResponse(command, e.Message);
        }
    }

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

    private async Task<IReadOnlyList<IData>> FundingAsync(RequestFundingRates request, CancellationToken ct)
    {
        IReadOnlyList<FundingRateUpdate> rates = await KrakenHistory
            .FetchFundingRatesAsync(_http, request.InstrumentId, request.Start, request.End, request.Limit, ct)
            .ConfigureAwait(false);

        return [.. rates.Cast<IData>()];
    }
}
