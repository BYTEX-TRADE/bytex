using System.Text;
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

namespace Bytex.Adapters.Hyperliquid;

/// <summary>
/// Market data from this venue's perpetuals: best bid and ask, matched trades, candles, a twenty-level book, and the
/// mark price and funding rate a perpetual is priced against. Bars come over the same <c>/info</c> read a catalog
/// download uses.
/// <para>
/// The socket is one address for everything - no per-topic path, no connection token, no address handed out per
/// connection - and a subscription is a JSON message naming a type and a coin. A subscription is ACKNOWLEDGED, on a
/// channel of its own, which is worth knowing because a subscription to a coin the venue does not list is refused
/// there rather than by silence.
/// </para>
/// <para>
/// Two channels are snapshots rather than deltas. The book arrives complete, twenty levels a side, every time it
/// changes; the asset context - mark, oracle, funding - arrives complete every few seconds. So nothing here keeps a
/// book of its own or applies an update to one, which is the usual source of a drifting local book.
/// </para>
/// </summary>
public sealed class HyperliquidDataClient : DataClientBase
{
    private readonly HyperliquidDataClientConfig _config;
    private readonly HyperliquidHttp _http;
    private readonly HyperliquidInstrumentProvider _instruments;
    private readonly HashSet<string> _subscriptions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BarType> _candleSubscriptions = new(StringComparer.Ordinal);
    private readonly Dictionary<BarType, long> _formingOpen = new();

    /// <summary>The newest state of each forming candle, so the one before it can be published when it closes.</summary>
    private readonly Dictionary<BarType, Bar> _lastForming = new();
    private readonly object _gate = new();
    private WebSocketClient? _ws;

    public HyperliquidDataClient(ClientId clientId, HyperliquidDataClientConfig config, KernelServices services)
        : base(clientId, HyperliquidVenue.Venue, services)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _http = new HyperliquidHttp(config, Log);
        _instruments = new HyperliquidInstrumentProvider(_http, config.InstrumentProvider, Log);
    }

    public HyperliquidInstrumentProvider Instruments => _instruments;

    public override async Task ConnectAsync(CancellationToken ct)
    {
        await _instruments.InitializeAsync(ct).ConfigureAwait(false);
        foreach (Instrument instrument in _instruments.GetAll())
        {
            HandleInstrument(instrument);
        }

        _ws = new WebSocketClient(new WebSocketClientConfig
        {
            Url = new Uri(HyperliquidVenue.WsBase(_config) + HyperliquidVenue.WsPath),

            // The keepalive is a JSON message rather than a websocket ping frame, so it goes through PingMessage and
            // not through the protocol.
            PingMessage = HyperliquidVenue.PingMessage,
            PingInterval = HyperliquidVenue.PingInterval,
        }, Log)
        {
            OnText = HandleMessageAsync,
            OnConnected = isReconnect =>
            {
                if (isReconnect)
                {
                    // A reconnected socket has no subscriptions at all: the venue keeps none across a connection.
                    List<string> again;
                    lock (_gate)
                    {
                        again = _subscriptions.ToList();
                    }

                    foreach (string subscription in again)
                    {
                        _ws?.SendText(subscription);
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

            // The best bid and ask on their own. The book channel carries the same two at the top of twenty levels,
            // so subscribing to both would publish every quote twice.
            case SubscribeQuoteTicks q:
                Subscribe(HyperliquidChannels.Bbo, HyperliquidVenue.ToCoin(q.InstrumentId));
                break;

            case SubscribeTradeTicks t:
                Subscribe(HyperliquidChannels.Trades, HyperliquidVenue.ToCoin(t.InstrumentId));
                break;

            case SubscribeOrderBookDeltas d:
                Subscribe(HyperliquidChannels.L2Book, HyperliquidVenue.ToCoin(d.InstrumentId));
                break;

            case SubscribeBars b when b.BarType.IsExternal:
                {
                    string message = HyperliquidChannels.Subscribe(
                        HyperliquidChannels.Candle,
                        HyperliquidVenue.ToCoin(b.BarType.InstrumentId),
                        HyperliquidVenue.Interval(b.BarType.Spec));

                    lock (_gate)
                    {
                        _candleSubscriptions[CandleKey(b.BarType)] = b.BarType;
                    }

                    Send(message);
                    break;
                }

            // Mark price and funding arrive on ONE channel, in one object, so either subscription asks for the same
            // thing and both are published from it.
            case SubscribeMarkPrices m:
                Subscribe(HyperliquidChannels.ActiveAssetCtx, HyperliquidVenue.ToCoin(m.InstrumentId));
                break;

            case SubscribeFundingRates f:
                Subscribe(HyperliquidChannels.ActiveAssetCtx, HyperliquidVenue.ToCoin(f.InstrumentId));
                break;

            default:
                Sink.OnSubscriptionFailed(ClientId, command, $"{command.GetType().Name} is not supported by the Hyperliquid data client");
                break;
        }
    }

    public override Task UnsubscribeAsync(UnsubscribeCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        (string Channel, string Coin, string? Interval)? topic = command switch
        {
            UnsubscribeQuoteTicks q => (HyperliquidChannels.Bbo, HyperliquidVenue.ToCoin(q.InstrumentId), null),
            UnsubscribeTradeTicks t => (HyperliquidChannels.Trades, HyperliquidVenue.ToCoin(t.InstrumentId), null),
            UnsubscribeOrderBookDeltas d => (HyperliquidChannels.L2Book, HyperliquidVenue.ToCoin(d.InstrumentId), null),
            UnsubscribeMarkPrices m => (HyperliquidChannels.ActiveAssetCtx, HyperliquidVenue.ToCoin(m.InstrumentId), null),
            UnsubscribeFundingRates f => (HyperliquidChannels.ActiveAssetCtx, HyperliquidVenue.ToCoin(f.InstrumentId), null),
            UnsubscribeBars b => (HyperliquidChannels.Candle, HyperliquidVenue.ToCoin(b.BarType.InstrumentId), HyperliquidVenue.Interval(b.BarType.Spec)),
            _ => null,
        };

        if (topic is not { } t2)
        {
            return Task.CompletedTask;
        }

        string subscribed = HyperliquidChannels.Subscribe(t2.Channel, t2.Coin, t2.Interval);
        bool removed;
        lock (_gate)
        {
            removed = _subscriptions.Remove(subscribed);
            if (command is UnsubscribeBars ub && _candleSubscriptions.Remove(CandleKey(ub.BarType), out BarType barType))
            {
                _formingOpen.Remove(barType);
                _lastForming.Remove(barType);
                removed = true;
            }
        }

        if (removed)
        {
            _ws?.SendText(HyperliquidChannels.Subscribe(t2.Channel, t2.Coin, t2.Interval, subscribe: false));
        }

        return Task.CompletedTask;
    }

    /// <summary>A channel and coin, remembered so a reconnected socket can be told about it again.</summary>
    private void Subscribe(string channel, string coin) => Send(HyperliquidChannels.Subscribe(channel, coin, null));

    private void Send(string message)
    {
        bool added;
        lock (_gate)
        {
            added = _subscriptions.Add(message);
        }

        if (added)
        {
            _ws?.SendText(message);
        }
    }

    /// <summary>
    /// How a candle message is matched back to the bar type that asked for it. The message carries the coin and the
    /// interval and nothing of the subscription, so the pair is the key.
    /// </summary>
    private static string CandleKey(BarType barType) =>
        HyperliquidVenue.ToCoin(barType.InstrumentId) + "|" + HyperliquidVenue.Interval(barType.Spec);

    private static string CandleKey(string coin, string interval) => coin + "|" + interval;

    private Instrument? InstrumentFor(string coin)
    {
        InstrumentId id = HyperliquidVenue.ToInstrumentId(coin);
        return _instruments.Find(id) ?? Services.Cache.Instrument(id);
    }

    private Task HandleMessageAsync(string text)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            JsonElement root = doc.RootElement;
            string channel = root.Str(HyperliquidChannels.ChannelField);
            if (!root.TryGetProperty(HyperliquidChannels.DataField, out JsonElement data))
            {
                return Task.CompletedTask;
            }

            switch (channel)
            {
                case HyperliquidChannels.Bbo:
                    HandleBbo(data);
                    break;
                case HyperliquidChannels.Trades:
                    HandleTrades(data);
                    break;
                case HyperliquidChannels.L2Book:
                    HandleBook(data);
                    break;
                case HyperliquidChannels.Candle:
                    HandleCandle(data);
                    break;
                case HyperliquidChannels.ActiveAssetCtx:
                    HandleAssetContext(data);
                    break;

                // The venue echoes back every subscription it accepted. Not noise: a subscription to something the
                // venue does not list is refused on the error channel instead, so seeing the echo is the only
                // confirmation there is that a stream will ever arrive.
                case HyperliquidChannels.SubscriptionResponse:
                    Log.LogDebug("Hyperliquid accepted a subscription: {Data}", data.GetRawText());
                    break;
                case HyperliquidChannels.Error:
                    Log.LogWarning("Hyperliquid stream error: {Data}", data.GetRawText());
                    break;
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            Log.LogWarning(e, "Hyperliquid: unreadable stream message");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The best bid and ask, as a two-element array rather than two named fields: index 0 is the bid and index 1 the
    /// ask, each an object with a price, a size and a count of orders behind it.
    /// </summary>
    private void HandleBbo(JsonElement d)
    {
        Instrument? instrument = InstrumentFor(d.Str(HyperliquidChannels.Coin));
        if (instrument is null || !d.TryGetProperty(HyperliquidChannels.Bbo, out JsonElement bbo)
            || bbo.ValueKind != JsonValueKind.Array || bbo.GetArrayLength() < 2)
        {
            return;
        }

        // Either side can be null when nothing is resting there, which is a real state on a thin asset and not an
        // error - a quote with half of it missing is not a quote.
        if (bbo[0].ValueKind != JsonValueKind.Object || bbo[1].ValueKind != JsonValueKind.Object)
        {
            return;
        }

        HandleData(new QuoteTick(
            instrument.Id,
            instrument.MakePrice(bbo[0].Dec(HyperliquidChannels.Price)),
            instrument.MakePrice(bbo[1].Dec(HyperliquidChannels.Price)),
            instrument.MakeQuantity(bbo[0].Dec(HyperliquidChannels.Size)),
            instrument.MakeQuantity(bbo[1].Dec(HyperliquidChannels.Size)),
            d.Ms(HyperliquidChannels.Time),
            Clock.Timestamp));
    }

    /// <summary>
    /// Matched trades, several per message. The side is the AGGRESSOR's, spelled <c>B</c> and <c>A</c> for bid and
    /// ask - so <c>B</c> means the resting bid was not what was hit: the buyer crossed.
    /// </summary>
    private void HandleTrades(JsonElement d)
    {
        if (d.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement row in d.EnumerateArray())
        {
            Instrument? instrument = InstrumentFor(row.Str(HyperliquidChannels.Coin));
            if (instrument is null)
            {
                continue;
            }

            HandleData(new TradeTick(
                instrument.Id,
                instrument.MakePrice(row.Dec(HyperliquidChannels.Price)),
                instrument.MakeQuantity(row.Dec(HyperliquidChannels.Size)),
                row.Str(HyperliquidChannels.Side) == HyperliquidChannels.BuyAggressor ? AggressorSide.Buyer : AggressorSide.Seller,
                new TradeId(row.Str(HyperliquidChannels.TradeId)),
                row.Ms(HyperliquidChannels.Time),
                Clock.Timestamp));
        }
    }

    /// <summary>
    /// The book, as a complete snapshot of twenty levels a side every time it changes. Published as a quote from the
    /// top of it, which is what the engine's book subscription is fed everywhere else too.
    /// </summary>
    private void HandleBook(JsonElement d)
    {
        Instrument? instrument = InstrumentFor(d.Str(HyperliquidChannels.Coin));
        if (instrument is null || !d.TryGetProperty(HyperliquidChannels.Levels, out JsonElement levels)
            || levels.ValueKind != JsonValueKind.Array || levels.GetArrayLength() < 2)
        {
            return;
        }

        JsonElement bids = levels[0];
        JsonElement asks = levels[1];
        if (bids.ValueKind != JsonValueKind.Array || asks.ValueKind != JsonValueKind.Array
            || bids.GetArrayLength() == 0 || asks.GetArrayLength() == 0)
        {
            return;
        }

        HandleData(new QuoteTick(
            instrument.Id,
            instrument.MakePrice(bids[0].Dec(HyperliquidChannels.Price)),
            instrument.MakePrice(asks[0].Dec(HyperliquidChannels.Price)),
            instrument.MakeQuantity(bids[0].Dec(HyperliquidChannels.Size)),
            instrument.MakeQuantity(asks[0].Dec(HyperliquidChannels.Size)),
            d.Ms(HyperliquidChannels.Time),
            Clock.Timestamp));
    }

    /// <summary>
    /// A candle, republished as it forms and never marked closed by the venue.
    /// <para>
    /// The message carries both ends of the interval, so there is no guessing which candle it is: when the open
    /// moves on, the one before it has closed. That is how a bar is published here - by the arrival of its successor
    /// rather than by a timer, which is why this client needs no closing loop and cannot publish a bar the venue
    /// never sent. The cost is that the last bar of a quiet stretch waits for the next message; the venue republishes
    /// a candle even with no trade in it, measured, so the wait is bounded by the interval and not by the next trade.
    /// </para>
    /// </summary>
    private void HandleCandle(JsonElement d)
    {
        string coin = d.Str(HyperliquidChannels.CandleCoin);
        string interval = d.Str(HyperliquidChannels.CandleInterval);
        BarType barType;
        lock (_gate)
        {
            if (!_candleSubscriptions.TryGetValue(CandleKey(coin, interval), out barType))
            {
                return;
            }
        }

        Instrument? instrument = InstrumentFor(coin);
        if (instrument is null)
        {
            return;
        }

        long openMs = d.Long(HyperliquidChannels.CandleOpenTime);
        UnixNanos close = new((openMs * UnixNanos.NanosPerMillisecond) + barType.Spec.IntervalNanos);
        Bar bar = new(
            barType,
            instrument.MakePrice(d.Dec(HyperliquidChannels.CandleOpen)),
            instrument.MakePrice(d.Dec(HyperliquidChannels.CandleHigh)),
            instrument.MakePrice(d.Dec(HyperliquidChannels.CandleLow)),
            instrument.MakePrice(d.Dec(HyperliquidChannels.CandleClose)),
            instrument.MakeQuantity(d.Dec(HyperliquidChannels.CandleVolume)),
            close,
            Clock.Timestamp);

        Bar? previous = null;
        lock (_gate)
        {
            if (_formingOpen.TryGetValue(barType, out long formingOpen) && openMs > formingOpen
                && _lastForming.TryGetValue(barType, out Bar held))
            {
                previous = held;
            }

            _formingOpen[barType] = openMs;
            _lastForming[barType] = bar;
        }

        if (previous is { } done)
        {
            HandleData(done);
        }

        if (_config.HandleRevisedBars)
        {
            HandleData(bar with { IsRevision = true });
        }
    }

    /// <summary>
    /// Mark price, oracle price and funding, all in one object on one channel. The oracle price is the index this
    /// venue computes from other exchanges, so it is published as the index price; the mark is what a position is
    /// liquidated against.
    /// </summary>
    private void HandleAssetContext(JsonElement d)
    {
        Instrument? instrument = InstrumentFor(d.Str(HyperliquidChannels.Coin));
        if (instrument is null || !d.TryGetProperty(HyperliquidChannels.Context, out JsonElement ctx))
        {
            return;
        }

        UnixNanos ts = Clock.Timestamp;
        if (ctx.Has(HyperliquidChannels.MarkPrice))
        {
            HandleData(new MarkPriceUpdate(instrument.Id, instrument.MakePrice(ctx.Dec(HyperliquidChannels.MarkPrice)), ts, ts));
        }

        if (ctx.Has(HyperliquidChannels.OraclePrice))
        {
            HandleData(new IndexPriceUpdate(instrument.Id, instrument.MakePrice(ctx.Dec(HyperliquidChannels.OraclePrice)), ts, ts));
        }

        if (ctx.Has(HyperliquidChannels.Funding))
        {
            // The rate for the CURRENT hour, republished as it moves - not a settlement. What was really charged is
            // the funding history read, which is why nothing here carries a settlement time.
            HandleData(new FundingRateUpdate(instrument.Id, ctx.Dec(HyperliquidChannels.Funding), null, ts, ts));
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

                case RequestFundingRates rf:
                    SendResponse(rf, typeof(FundingRateUpdate), await FetchFundingAsync(rf, ct).ConfigureAwait(false));
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
                    // Trade history among others: this venue publishes an account's own fills and no public trade
                    // history read at all, so recent trades can only be received live.
                    SendErrorResponse(command, $"{command.GetType().Name} is not supported by the Hyperliquid data client");
                    break;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.LogError(e, "Hyperliquid request {Request} failed", command.GetType().Name);
            SendErrorResponse(command, e.Message);
        }
    }

    private async Task<IReadOnlyList<IData>> FetchBarsAsync(RequestBars request, CancellationToken ct)
    {
        Instrument? instrument = _instruments.Find(request.BarType.InstrumentId) ?? Services.Cache.Instrument(request.BarType.InstrumentId);
        if (instrument is null)
        {
            return [];
        }

        IReadOnlyList<Bar> bars = await HyperliquidHistory
            .FetchBarsAsync(_http, instrument, request.BarType, request.Start, request.End, request.Limit, Clock.Timestamp, ct)
            .ConfigureAwait(false);

        return [.. bars.Cast<IData>()];
    }

    private async Task<IReadOnlyList<IData>> FetchFundingAsync(RequestFundingRates request, CancellationToken ct)
    {
        IReadOnlyList<FundingRateUpdate> rates = await HyperliquidHistory
            .FetchFundingRatesAsync(
                _http,
                request.InstrumentId,
                request.Start?.ToMilliseconds(),
                request.End?.ToMilliseconds(),
                ct)
            .ConfigureAwait(false);

        return [.. (request.Limit is { } limit ? rates.TakeLast(limit) : rates).Cast<IData>()];
    }
}

/// <summary>
/// The socket's channels and the field names they answer with, named once so a typo is a compiler error.
/// <para>
/// A subscription is a message rather than a path or a topic string, and the reply arrives on a channel named after
/// the subscription type - so the channel field of a message is what says what it is, and the coin is inside the
/// payload rather than in the channel name.
/// </para>
/// </summary>
public static class HyperliquidChannels
{
    public const string ChannelField = "channel";
    public const string DataField = "data";

    /// <summary>Best bid and offer, which is a two-element array and not two named fields.</summary>
    public const string Bbo = "bbo";

    public const string Trades = "trades";

    /// <summary>The book, as a complete twenty-level snapshot each time rather than a delta.</summary>
    public const string L2Book = "l2Book";

    public const string Candle = "candle";

    /// <summary>Mark, oracle, funding and open interest for one asset, as a snapshot every few seconds.</summary>
    public const string ActiveAssetCtx = "activeAssetCtx";

    /// <summary>The venue's echo of a subscription it accepted.</summary>
    public const string SubscriptionResponse = "subscriptionResponse";

    public const string Error = "error";

    public const string Coin = "coin";
    public const string Time = "time";
    public const string Price = "px";
    public const string Size = "sz";
    public const string Side = "side";
    public const string TradeId = "tid";
    public const string Levels = "levels";
    public const string Context = "ctx";
    public const string MarkPrice = "markPx";
    public const string OraclePrice = "oraclePx";
    public const string Funding = "funding";

    /// <summary>The aggressor side of a trade when the BUYER crossed. The venue writes it as one letter.</summary>
    public const string BuyAggressor = "B";

    // ----- the account's own streams -----
    //
    // Subscribed to by ADDRESS and nothing else. There is no handshake, no signature and no token: an arbitrary
    // address's order stream was subscribed to over a socket that had authenticated nothing, and the venue
    // acknowledged it. Measured. So a node can watch an account it cannot trade, and the key is needed only to
    // write - which is the reverse of every other venue here.

    /// <summary>What became of this account's orders.</summary>
    public const string OrderUpdates = "orderUpdates";

    /// <summary>What is subscribed to for the account's fills.</summary>
    public const string UserEvents = "userEvents";

    /// <summary>
    /// And where those messages ARRIVE, which is not what was subscribed to. A <c>userEvents</c> subscription is
    /// acknowledged as <c>userEvents</c> and its messages come back on <c>user</c> - measured. A client switching on
    /// the name it subscribed with receives every message and recognises none of them, which is a node that places
    /// orders and never hears that they filled.
    /// </summary>
    public const string UserEventsReply = "user";

    /// <summary>
    /// How many accounts one socket may follow. Measured: the sixteenth subscription came back "Cannot track more
    /// than 15 total users." This adapter follows one, so it is nowhere near - it is named because a host running
    /// several accounts through one process would find it, and would find it as a refused subscription rather than
    /// as an error.
    /// </summary>
    public const int MaxTrackedUsers = 15;

    public const string Fills = "fills";
    public const string Order = "order";
    public const string Cloid = "cloid";
    public const string Oid = "oid";
    public const string Status = "status";
    public const string StatusTimestamp = "statusTimestamp";
    public const string Timestamp = "timestamp";
    public const string Fee = "fee";
    public const string FeeToken = "feeToken";

    /// <summary>Whether this account was the taker on a fill, which is the liquidity side under another name.</summary>
    public const string Crossed = "crossed";

    /// <summary>The status a resting order has, and the only one seen on the live stream while this was written.</summary>
    public const string StatusOpen = "open";

    public const string StatusFilled = "filled";
    public const string StatusTriggered = "triggered";
    public const string StatusRejected = "rejected";

    /// <summary>
    /// What every ending status has in common. The venue cancels an order for several reasons it spells differently -
    /// a margin cancel, a reduce-only that no longer reduces, a delisting - and an unrecognised ending status would
    /// otherwise leave the order open in the node for ever.
    /// </summary>
    public const string CanceledMarker = "anceled";

    /// <summary>
    /// The subscriptions a trading node needs: what became of its orders, and what filled. Both keyed by the account
    /// address, and both readable without a key.
    /// </summary>
    public static IEnumerable<string> PrivateSubscriptions(string account)
    {
        yield return User(OrderUpdates, account);
        yield return User(UserEvents, account);
    }

    /// <summary>A subscription that names an account rather than a coin.</summary>
    public static string User(string channel, string account, bool subscribe = true)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("method", subscribe ? "subscribe" : "unsubscribe");
            writer.WriteStartObject("subscription");
            writer.WriteString("type", channel);
            writer.WriteString(HyperliquidReads.User, account);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // A candle message uses single letters of its own and does NOT reuse the ones above: its coin is "s" where every
    // other channel says "coin", and its size field "v" is the volume rather than a resting size.
    public const string CandleCoin = "s";
    public const string CandleInterval = "i";
    public const string CandleOpenTime = "t";
    public const string CandleOpen = "o";
    public const string CandleHigh = "h";
    public const string CandleLow = "l";
    public const string CandleClose = "c";
    public const string CandleVolume = "v";

    /// <summary>
    /// A subscribe or unsubscribe message. Built here rather than by string interpolation at each call so that the
    /// SAME text is remembered for a reconnect as was sent first time: a reconnect that re-subscribes with a
    /// differently spelled but equivalent message leaves the client unable to tell whether it is subscribed.
    /// </summary>
    public static string Subscribe(string channel, string coin, string? interval, bool subscribe = true)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("method", subscribe ? "subscribe" : "unsubscribe");
            writer.WriteStartObject("subscription");
            writer.WriteString("type", channel);
            writer.WriteString(Coin, coin);
            if (interval is not null)
            {
                writer.WriteString(CandleInterval, interval);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
