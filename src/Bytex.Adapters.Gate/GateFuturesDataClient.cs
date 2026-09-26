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
/// Market data from Gate's USDT-settled perpetual contracts: best bid and ask, matched trades, candles, a
/// twenty-level book, and the mark price, index price and funding rate a perpetual is priced against. Bars, funding
/// history and recent trades come over REST.
/// <para>
/// Every size crossing this boundary is converted. The venue counts in CONTRACTS and everything above the adapter
/// counts in base currency, which is the whole of what makes a strategy portable between this venue and the others.
/// </para>
/// <para>
/// The delivery market answers on its own address with the same channel names and the same field names, so
/// <see cref="GateDeliveryDataClient"/> is this client pointed at that address rather than a second copy of it. What
/// really differs there is recorded on that class.
/// </para>
/// </summary>
public class GateFuturesDataClient : DataClientBase
{
    private readonly GateDataClientConfig _config;
    private readonly GateHttp _http;
    private readonly InstrumentProviderBase _instruments;
    private readonly GateCandles _candles = new();
    private readonly Dictionary<string, IReadOnlyList<string>> _subscriptions = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private WebSocketClient? _ws;

    public GateFuturesDataClient(ClientId clientId, GateDataClientConfig config, KernelServices services)
        : this(clientId, config, services, GateProductType.Futures)
    {
    }

    /// <summary>
    /// Builds the client for one of the venue's two derivative markets. The product is a constructor argument rather
    /// than read off the configuration alone so that a delivery client cannot be handed a perpetual configuration, or
    /// the other way round, and connect to the wrong market's socket with the right market's contracts loaded.
    /// </summary>
    protected GateFuturesDataClient(ClientId clientId, GateDataClientConfig config, KernelServices services, GateProductType product)
        : base(clientId, GateVenue.Venue, services)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        if (_config.ProductType != product)
        {
            throw new ArgumentException(
                $"This client serves Gate's {product} market and the configuration says {_config.ProductType}.",
                nameof(config));
        }

        _http = new GateHttp(config, Log);
        _instruments = product == GateProductType.Delivery
            ? new GateDeliveryInstrumentProvider(_http, config.InstrumentProvider, Log)
            : new GateFuturesInstrumentProvider(_http, config.InstrumentProvider, Log);
    }

    public InstrumentProviderBase Instruments => _instruments;

    /// <summary>Which of the two derivative markets this client is on, which decides every path it asks for.</summary>
    protected GateProductType Product => _config.ProductType;

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
                PingMessage = GateStream.Ping(GateStream.FuturesPrefix),
                PingInterval = GateVenue.DefaultPingInterval,
            },
            Log)
        {
            OnText = HandleMessageAsync,
            OnConnected = isReconnect =>
            {
                if (isReconnect)
                {
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

            case SubscribeQuoteTicks q:
                Add(GateStream.FuturesPrefix + GateStream.BookTicker, [Raw(q.InstrumentId)]);
                break;

            case SubscribeTradeTicks t:
                Add(GateStream.FuturesPrefix + GateStream.Trades, [Raw(t.InstrumentId)]);
                break;

            case SubscribeBars b when b.BarType.IsExternal:
                _candles.Track(GateVenue.Interval(b.BarType.Spec) + "_" + Raw(b.BarType.InstrumentId), b.BarType);

                // Two elements, an interval and a contract. The venue's documentation shows the joined
                // "1m_BTC_USDT" form that its messages come back under, and sending that as a single element is
                // refused with "request payload does not follow json schema".
                Add(
                    GateStream.FuturesPrefix + GateStream.Candlesticks,
                    [GateVenue.Interval(b.BarType.Spec), Raw(b.BarType.InstrumentId)]);
                break;

            case SubscribeOrderBookDeltas d:
                Add(
                    GateStream.FuturesPrefix + GateStream.OrderBook,
                    [Raw(d.InstrumentId), GateStream.DepthLevels, GateStream.FuturesDepthInterval]);
                break;

            // One channel carries the mark price, the index price and the funding rate, so all three subscriptions
            // ask for the same thing and whichever arrives first opens it.
            case SubscribeMarkPrices m:
                Add(GateStream.FuturesPrefix + GateStream.Tickers, [Raw(m.InstrumentId)]);
                break;

            case SubscribeFundingRates f when Product == GateProductType.Futures:
                Add(GateStream.FuturesPrefix + GateStream.Tickers, [Raw(f.InstrumentId)]);
                break;

            default:
                Sink.OnSubscriptionFailed(ClientId, command, $"{command.GetType().Name} is not supported by the Gate {Product} data client");
                break;
        }
    }

    public override Task UnsubscribeAsync(UnsubscribeCommand command, CancellationToken ct)
    {
        (string Channel, IReadOnlyList<string> Payload)? topic = command switch
        {
            UnsubscribeQuoteTicks q => (GateStream.FuturesPrefix + GateStream.BookTicker, (IReadOnlyList<string>)[Raw(q.InstrumentId)]),
            UnsubscribeTradeTicks t => (GateStream.FuturesPrefix + GateStream.Trades, [Raw(t.InstrumentId)]),
            UnsubscribeBars b => (GateStream.FuturesPrefix + GateStream.Candlesticks, [GateVenue.Interval(b.BarType.Spec), Raw(b.BarType.InstrumentId)]),
            UnsubscribeOrderBookDeltas d => (GateStream.FuturesPrefix + GateStream.OrderBook, [Raw(d.InstrumentId), GateStream.DepthLevels, GateStream.FuturesDepthInterval]),
            UnsubscribeMarkPrices m => (GateStream.FuturesPrefix + GateStream.Tickers, [Raw(m.InstrumentId)]),
            UnsubscribeFundingRates f => (GateStream.FuturesPrefix + GateStream.Tickers, [Raw(f.InstrumentId)]),
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

                case RequestFundingRates rf when Product == GateProductType.Futures:
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
                    SendErrorResponse(command, $"{command.GetType().Name} is not supported by the Gate {Product} data client");
                    break;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.LogError(e, "Gate {Product} request {Request} failed", Product, command.GetType().Name);
            SendErrorResponse(command, e.Message);
        }
    }

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

    private async Task<IReadOnlyList<IData>> FetchFundingAsync(RequestFundingRates request, CancellationToken ct)
    {
        IReadOnlyList<FundingRateUpdate> rates = await GateHistory
            .FetchFundingRatesAsync(_http, request.InstrumentId, request.Start, request.End, Clock.Timestamp, ct)
            .ConfigureAwait(false);

        return [.. rates.TakeLast(request.Limit ?? rates.Count).Cast<IData>()];
    }

    /// <summary>
    /// The recent trades this endpoint keeps. Two things about the row are this market's own and are silent when read
    /// wrongly: the size is SIGNED - negative means the taker sold - and <c>create_time_ms</c> carries a number of
    /// SECONDS with a fraction, despite its name. Reading it as milliseconds puts every trade in January 1970.
    /// </summary>
    private async Task<IReadOnlyList<IData>> FetchTradesAsync(RequestTradeTicks request, CancellationToken ct)
    {
        if (Find(request.InstrumentId) is not { } instrument)
        {
            return [];
        }

        JsonElement data = await _http.GetPublicAsync(
            GateFuturesVenue.Prefix(Product) + "/trades",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["contract"] = Raw(request.InstrumentId) },
            ct).ConfigureAwait(false);

        List<TradeTick> trades = new();
        if (data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        foreach (JsonElement row in data.EnumerateArray())
        {
            UnixNanos ts = row.FractionalSeconds("create_time_ms");
            if ((request.Start is { } from && ts < from) || (request.End is { } to && ts > to))
            {
                continue;
            }

            trades.Add(Trade(instrument, row, ts));
        }

        return [.. trades.OrderBy(t => t.TsEvent.Value).TakeLast(request.Limit ?? trades.Count).Cast<IData>()];
    }

    /// <summary>
    /// A derivative trade. The sign of the size is the taker's side and the magnitude is a number of contracts, so
    /// both are read off the one field: there is no side field to fall back on.
    /// </summary>
    private TradeTick Trade(Instrument instrument, JsonElement row, UnixNanos ts)
    {
        decimal contracts = row.Dec("size");
        return new TradeTick(
            instrument.Id,
            instrument.MakePrice(row.Dec("price")),
            GateFuturesVenue.ToQuantity(instrument, Math.Abs(contracts)),
            contracts >= 0m ? AggressorSide.Buyer : AggressorSide.Seller,
            new TradeId(row.Str("id")),
            ts,
            Clock.Timestamp);
    }

    private static string Raw(InstrumentId id) => GateFuturesVenue.ToRawSymbol(id);

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

            if (root.Has("error"))
            {
                JsonElement error = root.GetProperty("error");
                Log.LogWarning(
                    "Gate {Product} refused the {Channel} subscription with {Code}: {Message}",
                    Product,
                    channel,
                    error.Long("code"),
                    error.Str("message"));
                return Task.CompletedTask;
            }

            // A depth snapshot arrives under "all" here and under "update" on spot, for the same kind of message.
            if ((@event != GateStream.UpdateEvent && @event != GateStream.SnapshotEvent) || !root.Has("result"))
            {
                return Task.CompletedTask;
            }

            JsonElement result = root.GetProperty("result");
            switch (channel)
            {
                case GateStream.FuturesPrefix + GateStream.BookTicker:
                    HandleBookTicker(result);
                    break;

                // These three arrive as ARRAYS of rows where the book ticker arrives as one object, so each row is
                // handled on its own. A candle message carries the previous interval alongside the forming one.
                case GateStream.FuturesPrefix + GateStream.Trades:
                    ForEach(result, HandleTrade);
                    break;
                case GateStream.FuturesPrefix + GateStream.Candlesticks:
                    ForEach(result, HandleCandle);
                    break;
                case GateStream.FuturesPrefix + GateStream.Tickers:
                    ForEach(result, HandleTicker);
                    break;

                case GateStream.FuturesPrefix + GateStream.OrderBook:
                    HandleBook(result);
                    break;
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            Log.LogWarning(e, "Gate {Product}: unreadable stream message", Product);
        }

        return Task.CompletedTask;
    }

    private static void ForEach(JsonElement result, Action<JsonElement> handle)
    {
        if (result.ValueKind != JsonValueKind.Array)
        {
            handle(result);
            return;
        }

        foreach (JsonElement row in result.EnumerateArray())
        {
            handle(row);
        }
    }

    /// <summary>Best bid and ask, with the sizes in contracts and the timestamp in whole milliseconds.</summary>
    private void HandleBookTicker(JsonElement d)
    {
        if (Find(GateFuturesVenue.ToInstrumentId(d.Str("s"))) is not { } instrument)
        {
            return;
        }

        HandleData(new QuoteTick(
            instrument.Id,
            instrument.MakePrice(d.Dec("b")),
            instrument.MakePrice(d.Dec("a")),
            GateFuturesVenue.ToQuantity(instrument, d.Dec("B")),
            GateFuturesVenue.ToQuantity(instrument, d.Dec("A")),
            d.Ms("t"),
            Clock.Timestamp));
    }

    /// <summary>
    /// A trade off the socket. Here <c>create_time_ms</c> really is milliseconds, where the REST row of the same
    /// name is seconds - two units under one name on one market, which is why the two paths read it differently.
    /// </summary>
    private void HandleTrade(JsonElement d)
    {
        if (Find(GateFuturesVenue.ToInstrumentId(d.Str("contract"))) is not { } instrument)
        {
            return;
        }

        HandleData(Trade(instrument, d, d.Ms("create_time_ms")));
    }

    /// <summary>
    /// The forming candle. The perpetual market sends the <c>w</c> flag that says an interval has closed and the
    /// delivery market sends no such field, so the closing is left to <see cref="GateCandles"/>, which closes a
    /// candle on the arrival of a later one wherever the flag is absent.
    /// </summary>
    private void HandleCandle(JsonElement d)
    {
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

            // "v" is a number of contracts here, where on spot it is the quote volume. Same letter, same venue,
            // different market, different meaning - and both are numbers that look plausible either way.
            d.Dec("v"),
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
            GateFuturesVenue.ToQuantity(instrument, candle.Volume),
            new((candle.OpenSeconds * UnixNanos.NanosPerSecond) + barType.Spec.IntervalNanos),
            Clock.Timestamp);

    /// <summary>
    /// The mark price a contract is liquidated against, the index it is marked from, and - on the perpetual market
    /// only - the funding rate it is being charged. The delivery market sends the same channel with the funding
    /// fields EMPTY STRINGS rather than absent, which is the venue saying a dated contract is not funded; an empty
    /// string parses to zero, so a rate of nought would be published as though it had been measured.
    /// </summary>
    private void HandleTicker(JsonElement d)
    {
        if (Find(GateFuturesVenue.ToInstrumentId(d.Str("contract"))) is not { } instrument)
        {
            return;
        }

        UnixNanos ts = d.Has("t") ? d.Ms("t") : Clock.Timestamp;
        if (d.Str("mark_price") is { Length: > 0 } mark)
        {
            HandleData(new MarkPriceUpdate(instrument.Id, instrument.MakePrice(Parse(mark)), ts, Clock.Timestamp));
        }

        if (d.Str("index_price") is { Length: > 0 } index)
        {
            HandleData(new IndexPriceUpdate(instrument.Id, instrument.MakePrice(Parse(index)), ts, Clock.Timestamp));
        }

        if (Product == GateProductType.Futures && d.Str("funding_rate") is { Length: > 0 } rate)
        {
            // The rate the venue is currently charging, republished as it moves - not a settlement. What was
            // actually charged at each settlement comes from the funding history endpoint.
            HandleData(new FundingRateUpdate(
                instrument.Id,
                Parse(rate),
                d.Long("funding_next_apply") > 0 ? d.Seconds("funding_next_apply") : null,
                ts,
                Clock.Timestamp));
        }
    }

    private static decimal Parse(string value) =>
        decimal.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out decimal d)
            ? d
            : 0m;

    /// <summary>
    /// A depth snapshot as a quote. The levels are objects with <c>p</c> and <c>s</c> here where spot sends
    /// [price, size] arrays, and <c>s</c> is a number of contracts.
    /// </summary>
    private void HandleBook(JsonElement d)
    {
        if (Find(GateFuturesVenue.ToInstrumentId(d.Str("contract"))) is not { } instrument
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
            instrument.MakePrice(bids[0].Dec("p")),
            instrument.MakePrice(asks[0].Dec("p")),
            GateFuturesVenue.ToQuantity(instrument, bids[0].Dec("s")),
            GateFuturesVenue.ToQuantity(instrument, asks[0].Dec("s")),
            d.Ms("t"),
            Clock.Timestamp));
    }
}

/// <summary>
/// Market data from Gate's USDT-settled dated contracts. The same channels and the same field names as the perpetual
/// market, on the venue's own delivery address, which is why this is the perpetual client pointed elsewhere.
/// <para>
/// Two things really differ and both are handled above. There is no funding: the channel that carries a funding rate
/// on the perpetual market sends it as an empty string here, and a dated contract settles at expiry instead - so no
/// funding subscription and no funding request is accepted. And a candle here carries no window-closed flag, where a
/// perpetual candle does, so an interval is closed by the arrival of the next one.
/// </para>
/// </summary>
public sealed class GateDeliveryDataClient : GateFuturesDataClient
{
    public GateDeliveryDataClient(ClientId clientId, GateDataClientConfig config, KernelServices services)
        : base(clientId, config, services, GateProductType.Delivery)
    {
    }
}
