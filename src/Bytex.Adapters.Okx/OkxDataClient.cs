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

namespace Bytex.Adapters.Okx;

/// <summary>
/// Market data from one of OKX's three markets: best bid and ask, matched trades, candles, an order book, and the
/// mark price, index price and funding rate a derivative is priced against. Bars and recent trades also come over
/// REST, through <see cref="OkxHistory"/>, so a stored bar and a live bar are the same bar.
/// <para>
/// One client for all three markets, unlike KuCoin's two, because there is one API: the channel names are the same
/// and the instrument id selects the market. What the client has to convert is the SIZE. Spot channels count in base
/// currency and the derivative channels count in CONTRACTS - a trade of 3498.64 on BTC-USDT-SWAP is 34.9864 bitcoin -
/// so every size crossing this boundary goes through the instrument's contract value, which is what makes a strategy
/// portable between this venue's markets and between this venue and the others.
/// </para>
/// <para>
/// TWO sockets, which is the one structural surprise here and was measured rather than read. Candles are published
/// only on the venue's "business" path: subscribing to <c>candle1m</c> on the public path is refused with code 60018
/// and the same subscription on the business path delivers. A client that opened one socket would have quotes and
/// trades flowing and bars silently absent, which is why both are opened when the client connects rather than one
/// being opened when a bar is first asked for - a socket that appears halfway through a run is a socket whose
/// failure looks like a missing subscription.
/// </para>
/// </summary>
public sealed class OkxDataClient : DataClientBase
{
    /// <summary>Best bid and ask and nothing else; the venue's ticker channel carries exactly that plus statistics.</summary>
    private const string ChannelTickers = "tickers";

    private const string ChannelTrades = "trades";

    /// <summary>The five-level snapshot the venue pushes whole, for a subscription that asked for a shallow book.</summary>
    private const string ChannelBooks5 = "books5";

    /// <summary>The deep book: one snapshot and then changes, with the venue saying which each message is.</summary>
    private const string ChannelBooks = "books";

    private const string ChannelFundingRate = "funding-rate";

    private const string ChannelMarkPrice = "mark-price";

    /// <summary>
    /// The index channel, which is subscribed to by the INDEX's name and not the contract's - "BTC-USDT" for
    /// BTC-USDT-SWAP. The name comes off the instrument, where the provider recorded the venue's own <c>uly</c>.
    /// </summary>
    private const string ChannelIndexTickers = "index-tickers";

    /// <summary>The prefix every candle channel shares: <c>candle1m</c>, <c>candle1H</c>.</summary>
    private const string ChannelCandlePrefix = "candle";

    /// <summary>
    /// The deepest book a <c>books5</c> subscription can serve. A subscription for more than this is served by the
    /// deep channel, and one for less is still served by this - the venue publishes no shallower book.
    /// </summary>
    private const int SmallBookDepth = 5;

    /// <summary>What the venue calls the first message of a deep book subscription, which is the whole book.</summary>
    private const string BookSnapshot = "snapshot";

    private readonly OkxDataClientConfig _config;
    private readonly OkxHttp _http;
    private readonly OkxInstrumentProvider _instruments;
    private readonly HashSet<(string Channel, string InstId)> _publicTopics = [];
    private readonly HashSet<(string Channel, string InstId)> _businessTopics = [];
    private readonly Dictionary<(string Channel, string InstId), BarType> _candleTopics = [];
    private readonly object _gate = new();

    /// <summary>
    /// Stands in for the venue's own book sequence where a message carries none, so that a consumer relying on a
    /// rising sequence still sees one. Signed because that is what an interlocked increment takes.
    /// </summary>
    private long _bookSequence;

    private WebSocketClient? _public;
    private WebSocketClient? _business;

    public OkxDataClient(ClientId clientId, OkxDataClientConfig config, KernelServices services)
        : base(clientId, OkxVenue.Venue, services)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _http = new OkxHttp(config, Log);
        _instruments = new OkxInstrumentProvider(_http, config.InstrumentType, config.InstrumentProvider, Log);
    }

    public OkxInstrumentProvider Instruments => _instruments;

    public override async Task ConnectAsync(CancellationToken ct)
    {
        await _instruments.InitializeAsync(ct).ConfigureAwait(false);
        foreach (Instrument instrument in _instruments.GetAll())
        {
            HandleInstrument(instrument);
        }

        _public = Socket(OkxVenue.WsPublic(_config), _publicTopics);
        _business = Socket(OkxVenue.WsBusiness(_config), _businessTopics);
        await _public.ConnectAsync(ct).ConfigureAwait(false);
        await _business.ConnectAsync(ct).ConfigureAwait(false);
        NotifyConnected();
    }

    /// <summary>
    /// One socket, with the subscriptions that belong to it. Both of the venue's public paths speak the same
    /// protocol, so the only thing that differs between them is which topic set is replayed after a reconnection.
    /// </summary>
    private WebSocketClient Socket(Uri url, HashSet<(string Channel, string InstId)> topics) =>
        new(new WebSocketClientConfig
        {
            Url = url,

            // Plain text, not JSON. The venue answers the string "ping" with the string "pong" - measured - and
            // closes a socket that has been silent for thirty seconds.
            PingMessage = OkxVenue.PingMessage,
            PingInterval = OkxVenue.PingInterval,
        }, Log)
        {
            OnText = HandleMessageAsync,
            OnConnected = isReconnect =>
            {
                if (!isReconnect)
                {
                    return Task.CompletedTask;
                }

                List<(string Channel, string InstId)> replay;
                lock (_gate)
                {
                    replay = [.. topics];
                }

                foreach ((string channel, string instId) in replay)
                {
                    Send(channel, instId, subscribe: true);
                }

                NotifyConnected();
                return Task.CompletedTask;
            },
            OnDisconnected = reason =>
            {
                NotifyDisconnected(reason);
                return Task.CompletedTask;
            },
        };

    public override async Task DisconnectAsync(CancellationToken ct)
    {
        foreach (WebSocketClient? socket in new[] { _public, _business })
        {
            if (socket is not null)
            {
                await socket.DisposeAsync().ConfigureAwait(false);
            }
        }

        _public = null;
        _business = null;
        NotifyDisconnected("disconnect requested");
    }

    protected override void OnDispose()
    {
        _public?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _business?.DisposeAsync().AsTask().GetAwaiter().GetResult();
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

            case SubscribeQuoteTicks q:
                Add(ChannelTickers, Raw(q.InstrumentId));
                return;

            case SubscribeTradeTicks t:
                Add(ChannelTrades, Raw(t.InstrumentId));
                return;

            case SubscribeBars b when b.BarType.IsExternal:
                {
                    string channel = ChannelCandlePrefix + OkxVenue.Interval(b.BarType.Spec);
                    string instId = Raw(b.BarType.InstrumentId);
                    lock (_gate)
                    {
                        _candleTopics[(channel, instId)] = b.BarType;
                    }

                    Add(channel, instId, business: true);
                    return;
                }

            case SubscribeOrderBookDeltas d:
                Add(d.Depth is > 0 and <= SmallBookDepth ? ChannelBooks5 : ChannelBooks, Raw(d.InstrumentId));
                return;

            case SubscribeFundingRates f when PaysFunding:
                Add(ChannelFundingRate, Raw(f.InstrumentId));
                return;

            case SubscribeMarkPrices m when IsDerivative:
                Add(ChannelMarkPrice, Raw(m.InstrumentId));
                return;

            case SubscribeIndexPrices i when IsDerivative:
                {
                    // Subscribed to by the index's own name, which the provider recorded from the venue. Without a
                    // loaded instrument there is nothing to read it off, and taking it out of the contract id would
                    // be reading a spelling - BTC-USD_UM-261030's index is BTC-USD, which no rule about dashes gets
                    // right.
                    if (Underlying(i.InstrumentId) is { } index)
                    {
                        Add(ChannelIndexTickers, index);
                        return;
                    }

                    Sink.OnSubscriptionFailed(
                        ClientId,
                        command,
                        $"{i.InstrumentId} is not loaded, so OKX's index for it is not known; load the instrument first");
                    return;
                }

            default:
                Sink.OnSubscriptionFailed(
                    ClientId,
                    command,
                    $"{command.GetType().Name} is not supported by the OKX {_http.InstType} data client");
                return;
        }
    }

    public override Task UnsubscribeAsync(UnsubscribeCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        (string Channel, string InstId)? topic = command switch
        {
            UnsubscribeQuoteTicks q => (ChannelTickers, Raw(q.InstrumentId)),
            UnsubscribeTradeTicks t => (ChannelTrades, Raw(t.InstrumentId)),
            UnsubscribeBars b => (ChannelCandlePrefix + OkxVenue.Interval(b.BarType.Spec), Raw(b.BarType.InstrumentId)),
            UnsubscribeFundingRates f => (ChannelFundingRate, Raw(f.InstrumentId)),
            UnsubscribeMarkPrices m => (ChannelMarkPrice, Raw(m.InstrumentId)),
            UnsubscribeIndexPrices i when Underlying(i.InstrumentId) is { } index => (ChannelIndexTickers, index),
            _ => null,
        };

        // A book subscription is served by one of two channels depending on the depth asked for, and an unsubscribe
        // carries no depth, so both are dropped. Dropping one the client never held is a no-op.
        if (command is UnsubscribeOrderBookDeltas d)
        {
            Remove(ChannelBooks5, Raw(d.InstrumentId));
            Remove(ChannelBooks, Raw(d.InstrumentId));
            return Task.CompletedTask;
        }

        if (topic is { } t2)
        {
            Remove(t2.Channel, t2.InstId);
        }

        return Task.CompletedTask;
    }

    private bool IsDerivative => _config.InstrumentType is OkxInstrumentType.Swap or OkxInstrumentType.Futures;

    /// <summary>Only perpetuals are funded; a dated contract converges by delivering instead.</summary>
    private bool PaysFunding => _config.InstrumentType == OkxInstrumentType.Swap;

    private string? Underlying(InstrumentId id) =>
        Find(id)?.Info?.GetValueOrDefault(OkxVenue.UnderlyingInfo) is { Length: > 0 } index ? index : null;

    private void Add(string channel, string instId, bool business = false)
    {
        bool added;
        lock (_gate)
        {
            added = (business ? _businessTopics : _publicTopics).Add((channel, instId));
        }

        if (added)
        {
            Send(channel, instId, subscribe: true);
        }
    }

    private void Remove(string channel, string instId)
    {
        bool removed;
        lock (_gate)
        {
            removed = _publicTopics.Remove((channel, instId)) | _businessTopics.Remove((channel, instId));
            _candleTopics.Remove((channel, instId));
        }

        if (removed)
        {
            Send(channel, instId, subscribe: false);
        }
    }

    /// <summary>
    /// Sends a subscription to whichever socket carries that channel. Candles go to the business socket and
    /// everything else to the public one, which is the whole of the difference between them.
    /// </summary>
    private void Send(string channel, string instId, bool subscribe)
    {
        WebSocketClient? socket = channel.StartsWith(ChannelCandlePrefix, StringComparison.Ordinal) ? _business : _public;
        socket?.SendText(OkxStream.Subscribe(channel, instId, subscribe));
    }

    private static string Raw(InstrumentId id) => OkxVenue.ToRawSymbol(id);

    private Instrument? Find(InstrumentId id) => _instruments.Find(id) ?? Services.Cache.Instrument(id);

    private Instrument? FindByRaw(string instId) => Find(OkxVenue.ToInstrumentId(instId));

    // ----- the stream -----

    private Task HandleMessageAsync(string text)
    {
        // The keep-alive answer is the bare word "pong" rather than JSON, so it is recognised before anything tries
        // to parse it. Both of this client's sockets are pinged, so both receive it.
        if (text.Length == 0 || string.Equals(text, OkxVenue.PongMessage, StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            JsonElement root = doc.RootElement;
            if (root.Str("event") is { Length: > 0 } e)
            {
                if (string.Equals(e, OkxStream.EventError, StringComparison.Ordinal))
                {
                    Log.LogWarning("OKX stream error {Code}: {Message}", root.Str("code"), root.Str("msg"));
                }

                return Task.CompletedTask;
            }

            if (!root.Has("arg") || !root.Has("data"))
            {
                return Task.CompletedTask;
            }

            JsonElement arg = root.GetProperty("arg");
            string channel = arg.Str("channel");
            string instId = arg.Str("instId");
            JsonElement data = root.GetProperty("data");

            if (channel.StartsWith(ChannelCandlePrefix, StringComparison.Ordinal))
            {
                HandleCandles(channel, instId, data);
                return Task.CompletedTask;
            }

            switch (channel)
            {
                case ChannelTickers:
                    HandleTickers(data);
                    break;
                case ChannelTrades:
                    HandleTrades(data);
                    break;
                case ChannelBooks5:
                case ChannelBooks:
                    HandleBook(instId, root.Str("action"), data);
                    break;
                case ChannelFundingRate:
                    HandleFundingRate(data);
                    break;
                case ChannelMarkPrice:
                    HandleMarkPrice(data);
                    break;
                case ChannelIndexTickers:
                    HandleIndexPrice(instId, data);
                    break;
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException)
        {
            Log.LogWarning(e, "OKX: unreadable stream message {Text}", LogText.Truncate(text, LogText.MaxMessageLength));
        }

        return Task.CompletedTask;
    }

    private static IEnumerable<JsonElement> Rows(JsonElement data) =>
        data.ValueKind == JsonValueKind.Array ? data.EnumerateArray() : [];

    /// <summary>
    /// Best bid and ask. The sizes are counted in the market's own unit - base currency on spot, contracts on a
    /// derivative - so they go through the same conversion as an order does.
    /// </summary>
    private void HandleTickers(JsonElement data)
    {
        foreach (JsonElement d in Rows(data))
        {
            Instrument? instrument = FindByRaw(d.Str("instId"));
            if (instrument is null || !d.Filled("bidPx") || !d.Filled("askPx"))
            {
                continue;
            }

            HandleData(new QuoteTick(
                instrument.Id,
                instrument.MakePrice(d.Dec("bidPx")),
                instrument.MakePrice(d.Dec("askPx")),
                OkxVenue.FromVenueSize(instrument, d.Dec("bidSz")),
                OkxVenue.FromVenueSize(instrument, d.Dec("askSz")),
                d.Ms("ts"),
                Clock.Timestamp));
        }
    }

    private void HandleTrades(JsonElement data)
    {
        foreach (JsonElement d in Rows(data))
        {
            Instrument? instrument = FindByRaw(d.Str("instId"));
            if (instrument is null)
            {
                continue;
            }

            // "side" is the taker's side, which is what an aggressor is.
            HandleData(new TradeTick(
                instrument.Id,
                instrument.MakePrice(d.Dec("px")),
                OkxVenue.FromVenueSize(instrument, d.Dec("sz")),
                d.Str("side").Equals("buy", StringComparison.Ordinal) ? AggressorSide.Buyer : AggressorSide.Seller,
                new TradeId(d.Str("tradeId")),
                d.Ms("ts"),
                Clock.Timestamp));
        }
    }

    /// <summary>
    /// The book, from either of the venue's two channels. The shallow one pushes the whole five levels every time and
    /// says nothing about what it is; the deep one says <c>snapshot</c> once and then sends changes, where a level at
    /// size zero has gone. A level is [price, size, deprecated, order count].
    /// </summary>
    private void HandleBook(string instId, string action, JsonElement data)
    {
        Instrument? instrument = FindByRaw(instId);
        if (instrument is null)
        {
            return;
        }

        foreach (JsonElement d in Rows(data))
        {
            bool snapshot = action.Length == 0 || string.Equals(action, BookSnapshot, StringComparison.Ordinal);
            ulong sequence = (ulong)d.Long("seqId");
            List<OrderBookDelta> deltas = [];
            ulong orderId = 0;
            UnixNanos ts = d.Ms("ts");

            foreach ((string side, OrderSide bookSide) in new[] { ("bids", OrderSide.Buy), ("asks", OrderSide.Sell) })
            {
                if (!d.Has(side))
                {
                    continue;
                }

                foreach (JsonElement level in d.GetProperty(side).EnumerateArray())
                {
                    decimal size = level[1].DecValue();
                    deltas.Add(new OrderBookDelta(
                        instrument.Id,
                        size == 0m ? BookAction.Delete : snapshot ? BookAction.Add : BookAction.Update,
                        new BookOrder(bookSide, instrument.MakePrice(level[0].DecValue()), OkxVenue.FromVenueSize(instrument, size), ++orderId),
                        RecordFlags.None,
                        sequence,
                        ts,
                        Clock.Timestamp));
                }
            }

            if (deltas.Count == 0)
            {
                continue;
            }

            // The venue's own sequence, where it sends one. The shallow channel carries a seqId and the venue's
            // documentation does not promise one on every message, so a count of our own covers the gap rather than
            // a run of zeroes reaching a consumer that reads the sequence.
            if (sequence == 0)
            {
                sequence = (ulong)Interlocked.Increment(ref _bookSequence);
            }

            HandleData(new OrderBookDeltas(
                instrument.Id,
                deltas,
                snapshot ? RecordFlags.Snapshot | RecordFlags.Last : RecordFlags.Last,
                sequence,
                ts,
                Clock.Timestamp));
        }
    }

    /// <summary>
    /// The funding the venue is currently predicting for the next settlement, republished as it moves. What was
    /// actually charged is a settled figure and comes from
    /// <see cref="OkxHistory.FetchFundingRatesAsync(OkxHttp, InstrumentId, DateTimeOffset, DateTimeOffset?, CancellationToken)"/>; unlike
    /// every other venue here, this channel does carry the next settlement time, so it is passed on.
    /// </summary>
    private void HandleFundingRate(JsonElement data)
    {
        foreach (JsonElement d in Rows(data))
        {
            Instrument? instrument = FindByRaw(d.Str("instId"));
            if (instrument is null)
            {
                continue;
            }

            HandleData(new FundingRateUpdate(
                instrument.Id,
                d.Dec("fundingRate"),
                d.Filled("fundingTime") ? d.Ms("fundingTime") : null,
                d.Filled("ts") ? d.Ms("ts") : Clock.Timestamp,
                Clock.Timestamp));
        }
    }

    private void HandleMarkPrice(JsonElement data)
    {
        foreach (JsonElement d in Rows(data))
        {
            Instrument? instrument = FindByRaw(d.Str("instId"));
            if (instrument is not null)
            {
                HandleData(new MarkPriceUpdate(instrument.Id, instrument.MakePrice(d.Dec("markPx")), d.Ms("ts"), Clock.Timestamp));
            }
        }
    }

    /// <summary>
    /// The index price, which arrives under the INDEX's name. Every derivative loaded here that is priced against
    /// that index is told about it, because one index serves a perpetual and every dated contract on the same pair
    /// and the venue does not repeat the message per contract.
    /// </summary>
    private void HandleIndexPrice(string index, JsonElement data)
    {
        foreach (JsonElement d in Rows(data))
        {
            UnixNanos ts = d.Ms("ts");
            decimal idx = d.Dec("idxPx");
            foreach (Instrument instrument in _instruments.GetAll())
            {
                if (string.Equals(instrument.Info?.GetValueOrDefault(OkxVenue.UnderlyingInfo), index, StringComparison.Ordinal))
                {
                    HandleData(new IndexPriceUpdate(instrument.Id, instrument.MakePrice(idx), ts, Clock.Timestamp));
                }
            }
        }
    }

    /// <summary>
    /// A candle from the business socket. The row is the same nine fields the REST endpoint returns, including the
    /// venue's own confirmation that the candle has closed - so nothing here has to guess from a clock, and a bar is
    /// published exactly once, when the venue says it is finished.
    /// <para>
    /// A revision is the forming candle, forwarded only when the configuration asks for one.
    /// </para>
    /// </summary>
    private void HandleCandles(string channel, string instId, JsonElement data)
    {
        BarType barType;
        lock (_gate)
        {
            if (!_candleTopics.TryGetValue((channel, instId), out barType))
            {
                return;
            }
        }

        Instrument? instrument = Find(barType.InstrumentId);
        if (instrument is null)
        {
            return;
        }

        foreach (JsonElement k in Rows(data))
        {
            if (k.ValueKind != JsonValueKind.Array || k.GetArrayLength() < 6)
            {
                continue;
            }

            bool closed = k.GetArrayLength() > 8 && k[8].ToString() == "1";
            if (!closed && !_config.HandleRevisedBars)
            {
                continue;
            }

            UnixNanos close = new((k[0].LongValue() * UnixNanos.NanosPerMillisecond) + barType.Spec.IntervalNanos);

            // As in history: on a derivative the volume field is a number of contracts and the base-currency figure
            // is the one beside it.
            decimal volume = instrument.InstrumentClass == InstrumentClass.Spot || k.GetArrayLength() <= 6
                ? k[5].DecValue()
                : k[6].DecValue();

            Bar bar = new(
                barType,
                instrument.MakePrice(k[1].DecValue()),
                instrument.MakePrice(k[2].DecValue()),
                instrument.MakePrice(k[3].DecValue()),
                instrument.MakePrice(k[4].DecValue()),
                instrument.MakeQuantity(volume),
                close,
                Clock.Timestamp);

            HandleData(closed ? bar : bar with { IsRevision = true });
        }
    }

    // ----- history requests -----

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
                case RequestFundingRates rf when PaysFunding:
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
                    SendErrorResponse(command, $"{command.GetType().Name} is not supported by the OKX {_http.InstType} data client");
                    break;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.LogError(e, "OKX request {Request} failed", command.GetType().Name);
            SendErrorResponse(command, e.Message);
        }
    }

    // History is read by OkxHistory, which a catalog download calls as well, so stored and live bars agree.
    private async Task<IReadOnlyList<IData>> FetchBarsAsync(RequestBars request, CancellationToken ct)
    {
        Instrument? instrument = Find(request.BarType.InstrumentId);
        if (instrument is null)
        {
            return [];
        }

        IReadOnlyList<Bar> bars = await OkxHistory
            .FetchBarsAsync(_http, instrument, request.BarType, request.Start, request.End, request.Limit, Clock.Timestamp, ct)
            .ConfigureAwait(false);

        return [.. bars.Cast<IData>()];
    }

    private async Task<IReadOnlyList<IData>> FetchFundingRatesAsync(RequestFundingRates request, CancellationToken ct)
    {
        if (Find(request.InstrumentId) is null)
        {
            return [];
        }

        IReadOnlyList<FundingRateUpdate> rates = await OkxHistory
            .FetchFundingRatesAsync(
                _http,
                request.InstrumentId,
                request.Start?.ToMilliseconds(),
                request.End?.ToMilliseconds(),
                request.Limit,
                ct)
            .ConfigureAwait(false);

        return [.. rates.Cast<IData>()];
    }

    /// <summary>
    /// The most recent trades, which is all this endpoint keeps - the venue answered 500 when asked for 1000, so
    /// that is the page, and a window older than those 500 trades comes back empty rather than wrong.
    /// </summary>
    private async Task<IReadOnlyList<IData>> FetchTradesAsync(RequestTradeTicks request, CancellationToken ct)
    {
        Instrument? instrument = Find(request.InstrumentId);
        if (instrument is null)
        {
            return [];
        }

        JsonElement data = await _http.GetPublicAsync(
            "/api/v5/market/trades",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["instId"] = Raw(request.InstrumentId),
                ["limit"] = Math.Min(request.Limit ?? OkxVenue.TradePage, OkxVenue.TradePage).ToString(CultureInfo.InvariantCulture),
            },
            ct).ConfigureAwait(false);

        List<TradeTick> trades = [];
        foreach (JsonElement row in Rows(data))
        {
            UnixNanos ts = row.Ms("ts");
            if ((request.Start is { } start && ts < start) || (request.End is { } end && ts > end))
            {
                continue;
            }

            trades.Add(new TradeTick(
                instrument.Id,
                instrument.MakePrice(row.Dec("px")),
                OkxVenue.FromVenueSize(instrument, row.Dec("sz")),
                row.Str("side").Equals("buy", StringComparison.Ordinal) ? AggressorSide.Buyer : AggressorSide.Seller,
                new TradeId(row.Str("tradeId")),
                ts,
                Clock.Timestamp));
        }

        return [.. trades.OrderBy(t => t.TsEvent.Value).Cast<IData>()];
    }
}
