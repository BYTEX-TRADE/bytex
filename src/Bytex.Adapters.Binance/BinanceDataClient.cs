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

namespace Bytex.Adapters.Binance;

/// <summary>
/// Market data from Binance spot or USDⓈ-M futures: streaming quotes, trades, bars, book deltas, mark prices, and historical requests.
/// </summary>
public sealed class BinanceDataClient : DataClientBase
{
    private readonly BinanceDataClientConfig _config;
    private readonly BinanceHttp _http;
    private readonly BinanceInstrumentProvider _instruments;
    private readonly HashSet<string> _streams = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BarType> _klineStreams = new(StringComparer.Ordinal);
    private WebSocketClient? _ws;
    private int _requestId;

    public BinanceDataClient(ClientId clientId, BinanceDataClientConfig config, KernelServices services)
        : base(clientId, BinanceVenue.Venue, services)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _http = new BinanceHttp(config, Log);
        _instruments = new BinanceInstrumentProvider(_http, config.AccountType, config.InstrumentProvider, Log);
    }

    public BinanceInstrumentProvider Instruments => _instruments;

    public override async Task ConnectAsync(CancellationToken ct)
    {
        await _instruments.InitializeAsync(ct).ConfigureAwait(false);
        foreach (Instrument instrument in _instruments.GetAll())
        {
            HandleInstrument(instrument);
        }

        _ws = new WebSocketClient(new WebSocketClientConfig { Url = new Uri(BinanceVenue.WsBase(_config) + "/stream") }, Log)
        {
            OnText = HandleMessageAsync,
            OnConnected = isReconnect =>
            {
                if (isReconnect && _streams.Count > 0)
                {
                    SendSubscribe(_streams.ToList());
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

    // ----- Subscriptions -----

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
                AddStream($"{Lower(q.InstrumentId)}@bookTicker");
                break;
            case SubscribeTradeTicks t:
                AddStream($"{Lower(t.InstrumentId)}@trade");
                break;
            case SubscribeBars b when b.BarType.IsExternal:
                {
                    string stream = $"{Lower(b.BarType.InstrumentId)}@kline_{BinanceVenue.Interval(b.BarType.Spec)}";
                    _klineStreams[stream] = b.BarType;
                    AddStream(stream);
                    break;
                }

            case SubscribeOrderBookDeltas d:
                AddStream($"{Lower(d.InstrumentId)}@depth@100ms");
                await SendBookSnapshotAsync(d.InstrumentId, d.Depth, ct).ConfigureAwait(false);
                break;
            case SubscribeMarkPrices m when _config.AccountType == BinanceAccountType.UsdMFutures:
                AddStream($"{Lower(m.InstrumentId)}@markPrice@1s");
                break;
            case SubscribeIndexPrices i when _config.AccountType == BinanceAccountType.UsdMFutures:
                AddStream($"{Lower(i.InstrumentId)}@markPrice@1s");
                break;
            case SubscribeFundingRates f when _config.AccountType == BinanceAccountType.UsdMFutures:
                AddStream($"{Lower(f.InstrumentId)}@markPrice@1s");
                break;
            default:
                Sink.OnSubscriptionFailed(ClientId, command, $"{command.GetType().Name} is not supported by the Binance {_config.AccountType} data client");
                break;
        }
    }

    public override Task UnsubscribeAsync(UnsubscribeCommand command, CancellationToken ct)
    {
        string? stream = command switch
        {
            UnsubscribeQuoteTicks q => $"{Lower(q.InstrumentId)}@bookTicker",
            UnsubscribeTradeTicks t => $"{Lower(t.InstrumentId)}@trade",
            UnsubscribeBars b => $"{Lower(b.BarType.InstrumentId)}@kline_{BinanceVenue.Interval(b.BarType.Spec)}",
            UnsubscribeOrderBookDeltas d => $"{Lower(d.InstrumentId)}@depth@100ms",
            UnsubscribeMarkPrices m => $"{Lower(m.InstrumentId)}@markPrice@1s",
            _ => null,
        };

        if (stream is not null && _streams.Remove(stream))
        {
            _klineStreams.Remove(stream);
            Send(new { method = "UNSUBSCRIBE", @params = new[] { stream }, id = Interlocked.Increment(ref _requestId) });
        }

        return Task.CompletedTask;
    }

    private void AddStream(string stream)
    {
        if (_streams.Add(stream))
        {
            SendSubscribe([stream]);
        }
    }

    private void SendSubscribe(IReadOnlyList<string> streams) =>
        Send(new { method = "SUBSCRIBE", @params = streams, id = Interlocked.Increment(ref _requestId) });

    private void Send(object message) => _ws?.SendText(JsonSerializer.Serialize(message));

    private static string Lower(InstrumentId id) => BinanceVenue.ToRawSymbol(id).ToLowerInvariant();

    private InstrumentId? Resolve(string rawSymbol)
    {
        InstrumentId id = BinanceVenue.ToInstrumentId(rawSymbol, _config.AccountType);
        if (_instruments.Find(id) is not null)
        {
            return id;
        }

        return Services.Cache.Instrument(id) is not null ? id : null;
    }

    private Instrument? InstrumentFor(string rawSymbol)
    {
        InstrumentId? id = Resolve(rawSymbol);
        return id is null ? null : _instruments.Find(id.Value) ?? Services.Cache.Instrument(id.Value);
    }

    // ----- Stream handling -----

    private Task HandleMessageAsync(string text)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("data", out JsonElement data))
            {
                return Task.CompletedTask; // subscription acknowledgements
            }

            string stream = root.Str("stream");
            if (stream.EndsWith("@bookTicker", StringComparison.Ordinal))
            {
                HandleBookTicker(data);
            }
            else if (stream.EndsWith("@trade", StringComparison.Ordinal))
            {
                HandleTrade(data);
            }
            else if (stream.Contains("@kline_", StringComparison.Ordinal))
            {
                HandleKline(stream, data);
            }
            else if (stream.Contains("@depth", StringComparison.Ordinal))
            {
                HandleDepth(data);
            }
            else if (stream.Contains("@markPrice", StringComparison.Ordinal))
            {
                HandleMarkPrice(data);
            }
        }
        catch (Exception e)
        {
            Log.LogWarning(e, "Failed to parse Binance message: {Text}", text.Length > 300 ? text.Substring(0, 300) : text);
        }

        return Task.CompletedTask;
    }

    private void HandleBookTicker(JsonElement d)
    {
        Instrument? instrument = InstrumentFor(d.Str("s"));
        if (instrument is null)
        {
            return;
        }

        UnixNanos now = Clock.Timestamp;
        UnixNanos tsEvent = d.Has("E") ? d.Ms("E") : now;
        HandleData(new QuoteTick(instrument.Id, instrument.MakePrice(d.Dec("b")), instrument.MakePrice(d.Dec("a")),
            instrument.MakeQuantity(d.Dec("B")), instrument.MakeQuantity(d.Dec("A")), tsEvent, now));
    }

    private void HandleTrade(JsonElement d)
    {
        Instrument? instrument = InstrumentFor(d.Str("s"));
        if (instrument is null)
        {
            return;
        }

        bool buyerIsMaker = d.Bool("m");
        HandleData(new TradeTick(instrument.Id, instrument.MakePrice(d.Dec("p")), instrument.MakeQuantity(d.Dec("q")),
            buyerIsMaker ? AggressorSide.Seller : AggressorSide.Buyer, new TradeId(d.Long("t").ToString(CultureInfo.InvariantCulture)), d.Ms("T"), Clock.Timestamp));
    }

    private void HandleKline(string stream, JsonElement d)
    {
        if (!_klineStreams.TryGetValue(stream, out BarType barType))
        {
            return;
        }

        JsonElement k = d.GetProperty("k");
        bool closed = k.Bool("x");
        if (!closed && !_config.HandleRevisedBars)
        {
            return;
        }

        Instrument? instrument = InstrumentFor(k.Str("s"));
        if (instrument is null)
        {
            return;
        }

        HandleData(new Bar(barType, instrument.MakePrice(k.Dec("o")), instrument.MakePrice(k.Dec("h")), instrument.MakePrice(k.Dec("l")), instrument.MakePrice(k.Dec("c")),
            instrument.MakeQuantity(k.Dec("v")), UnixNanos.FromMilliseconds(k.Long("T") + 1), Clock.Timestamp, IsRevision: !closed));
    }

    private void HandleDepth(JsonElement d)
    {
        Instrument? instrument = InstrumentFor(d.Str("s"));
        if (instrument is null)
        {
            return;
        }

        UnixNanos tsEvent = d.Has("E") ? d.Ms("E") : Clock.Timestamp;
        ulong sequence = (ulong)d.Long("u");
        List<OrderBookDelta> deltas = new();
        ulong orderId = 0;
        foreach (JsonElement level in d.GetProperty("b").EnumerateArray())
        {
            deltas.Add(Delta(instrument, OrderSide.Buy, level, sequence, tsEvent, ++orderId));
        }

        foreach (JsonElement level in d.GetProperty("a").EnumerateArray())
        {
            deltas.Add(Delta(instrument, OrderSide.Sell, level, sequence, tsEvent, ++orderId));
        }

        if (deltas.Count > 0)
        {
            HandleData(new OrderBookDeltas(instrument.Id, deltas, RecordFlags.Last, sequence, tsEvent, Clock.Timestamp));
        }
    }

    private OrderBookDelta Delta(Instrument instrument, OrderSide side, JsonElement level, ulong sequence, UnixNanos tsEvent, ulong orderId)
    {
        decimal price = level[0].DecValue();
        decimal size = level[1].DecValue();
        BookAction action = size == 0m ? BookAction.Delete : BookAction.Update;
        return new OrderBookDelta(instrument.Id, action, new BookOrder(side, instrument.MakePrice(price), instrument.MakeQuantity(size), orderId), RecordFlags.None, sequence, tsEvent, Clock.Timestamp);
    }

    private void HandleMarkPrice(JsonElement d)
    {
        Instrument? instrument = InstrumentFor(d.Str("s"));
        if (instrument is null)
        {
            return;
        }

        UnixNanos tsEvent = d.Ms("E");
        UnixNanos now = Clock.Timestamp;
        HandleData(new MarkPriceUpdate(instrument.Id, instrument.MakePrice(d.Dec("p")), tsEvent, now));
        if (d.Has("i"))
        {
            HandleData(new IndexPriceUpdate(instrument.Id, instrument.MakePrice(d.Dec("i")), tsEvent, now));
        }

        if (d.Has("r"))
        {
            HandleData(new FundingRateUpdate(instrument.Id, d.Dec("r"), d.Has("T") ? d.Ms("T") : null, tsEvent, now));
        }
    }

    private async Task SendBookSnapshotAsync(InstrumentId instrumentId, int depth, CancellationToken ct)
    {
        Instrument? instrument = _instruments.Find(instrumentId) ?? Services.Cache.Instrument(instrumentId);
        if (instrument is null)
        {
            return;
        }

        Dictionary<string, string> query = new() { ["symbol"] = BinanceVenue.ToRawSymbol(instrumentId), ["limit"] = (depth <= 0 ? 100 : Math.Min(depth, 1000)).ToString(CultureInfo.InvariantCulture) };
        using JsonDocument doc = await _http.GetPublicAsync(_http.Prefix + "/depth", query, 5, ct).ConfigureAwait(false);
        JsonElement root = doc.RootElement;
        ulong sequence = (ulong)root.Long("lastUpdateId");
        UnixNanos now = Clock.Timestamp;
        List<OrderBookDelta> deltas = [OrderBookDelta.Clear(instrumentId, sequence, now, now)];
        ulong orderId = 0;
        foreach (JsonElement level in root.GetProperty("bids").EnumerateArray())
        {
            deltas.Add(new OrderBookDelta(instrumentId, BookAction.Add, new BookOrder(OrderSide.Buy, instrument.MakePrice(level[0].DecValue()), instrument.MakeQuantity(level[1].DecValue()), ++orderId), RecordFlags.None, sequence, now, now));
        }

        foreach (JsonElement level in root.GetProperty("asks").EnumerateArray())
        {
            deltas.Add(new OrderBookDelta(instrumentId, BookAction.Add, new BookOrder(OrderSide.Sell, instrument.MakePrice(level[0].DecValue()), instrument.MakeQuantity(level[1].DecValue()), ++orderId), RecordFlags.None, sequence, now, now));
        }

        HandleData(new OrderBookDeltas(instrumentId, deltas, RecordFlags.Snapshot | RecordFlags.Last, sequence, now, now));
    }

    // ----- Historical requests -----

    public override async Task RequestAsync(RequestCommand command, CancellationToken ct)
    {
        try
        {
            switch (command)
            {
                case RequestInstrument ri:
                    await _instruments.LoadAsync(ri.InstrumentId, ct).ConfigureAwait(false);
                    SendResponse(ri, typeof(Instrument), _instruments.Find(ri.InstrumentId) is { } inst ? [new InstrumentData(inst)] : []);
                    break;
                case RequestInstruments ris:
                    await _instruments.LoadAllAsync(ct).ConfigureAwait(false);
                    foreach (Instrument instrument in _instruments.GetAll())
                    {
                        HandleInstrument(instrument);
                    }

                    SendResponse(ris, typeof(Instrument), _instruments.GetAll().Select(i => (IData)new InstrumentData(i)).ToList());
                    break;
                case RequestBars rb:
                    SendResponse(rb, typeof(Bar), await FetchBarsAsync(rb, ct).ConfigureAwait(false));
                    break;
                case RequestTradeTicks rt:
                    SendResponse(rt, typeof(TradeTick), await FetchTradesAsync(rt, ct).ConfigureAwait(false));
                    break;
                default:
                    SendErrorResponse(command, $"{command.GetType().Name} is not supported by the Binance data client");
                    break;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.LogError(e, "Binance request {Request} failed", command.GetType().Name);
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

        List<IData> bars = new();
        int limit = request.Limit ?? 1000;
        long? start = request.Start?.ToMilliseconds();
        long end = (request.End ?? Clock.Timestamp).ToMilliseconds();
        while (bars.Count < limit)
        {
            Dictionary<string, string> query = new()
            {
                ["symbol"] = BinanceVenue.ToRawSymbol(request.BarType.InstrumentId),
                ["interval"] = BinanceVenue.Interval(request.BarType.Spec),
                ["limit"] = Math.Min(1000, limit - bars.Count).ToString(CultureInfo.InvariantCulture),
                ["endTime"] = end.ToString(CultureInfo.InvariantCulture),
            };
            if (start is { } s)
            {
                query["startTime"] = s.ToString(CultureInfo.InvariantCulture);
            }

            using JsonDocument doc = await _http.GetPublicAsync(_http.Prefix + "/klines", query, 2, ct).ConfigureAwait(false);
            List<IData> page = new();
            foreach (JsonElement k in doc.RootElement.EnumerateArray())
            {
                UnixNanos close = UnixNanos.FromMilliseconds(k[6].GetInt64() + 1);
                page.Add(new Bar(request.BarType, instrument.MakePrice(k[1].DecValue()), instrument.MakePrice(k[2].DecValue()), instrument.MakePrice(k[3].DecValue()), instrument.MakePrice(k[4].DecValue()),
                    instrument.MakeQuantity(k[5].DecValue()), close, close));
            }

            if (page.Count == 0)
            {
                break;
            }

            bars.InsertRange(0, page);
            long firstOpen = doc.RootElement[0][0].GetInt64();
            if (start is { } s2 && firstOpen <= s2)
            {
                break;
            }

            end = firstOpen - 1;
            if (page.Count < 1000)
            {
                break;
            }
        }

        return bars.Where(b => request.Start is null || b.TsInit >= request.Start.Value).Take(limit).ToList();
    }

    private async Task<IReadOnlyList<IData>> FetchTradesAsync(RequestTradeTicks request, CancellationToken ct)
    {
        Instrument? instrument = _instruments.Find(request.InstrumentId) ?? Services.Cache.Instrument(request.InstrumentId);
        if (instrument is null)
        {
            return [];
        }

        string path = _http.Prefix + (_config.UseAggTrades ? "/aggTrades" : "/trades");
        Dictionary<string, string> query = new()
        {
            ["symbol"] = BinanceVenue.ToRawSymbol(request.InstrumentId),
            ["limit"] = Math.Min(1000, request.Limit ?? 1000).ToString(CultureInfo.InvariantCulture),
        };
        if (_config.UseAggTrades)
        {
            if (request.Start is { } s)
            {
                query["startTime"] = s.ToMilliseconds().ToString(CultureInfo.InvariantCulture);
            }

            if (request.End is { } e)
            {
                query["endTime"] = e.ToMilliseconds().ToString(CultureInfo.InvariantCulture);
            }
        }

        using JsonDocument doc = await _http.GetPublicAsync(path, query, 2, ct).ConfigureAwait(false);
        List<IData> trades = new();
        foreach (JsonElement t in doc.RootElement.EnumerateArray())
        {
            bool buyerIsMaker = t.Bool("m") || t.Bool("isBuyerMaker");
            string id = t.Has("a") ? t.Long("a").ToString(CultureInfo.InvariantCulture) : t.Long("id").ToString(CultureInfo.InvariantCulture);
            UnixNanos ts = t.Has("T") ? t.Ms("T") : t.Ms("time");
            trades.Add(new TradeTick(instrument.Id, instrument.MakePrice(t.Has("p") ? t.Dec("p") : t.Dec("price")), instrument.MakeQuantity(t.Has("q") ? t.Dec("q") : t.Dec("qty")),
                buyerIsMaker ? AggressorSide.Seller : AggressorSide.Buyer, new TradeId(id), ts, ts));
        }

        return trades;
    }
}

/// <summary>
/// Wraps an instrument so it can travel in a <see cref="DataResponse"/>.
/// </summary>
public sealed record InstrumentData(Instrument Instrument) : IData
{
    public InstrumentId? InstrumentId => Instrument.Id;

    public UnixNanos TsEvent => Instrument.TsEvent;

    public UnixNanos TsInit => Instrument.TsInit;
}

public sealed class BinanceDataClientFactory : IDataClientFactory
{
    public string Name => "BINANCE";

    public Type ConfigType => typeof(BinanceDataClientConfig);

    public IDataClient Create(ClientId clientId, DataClientConfig config, KernelServices services) =>
        new BinanceDataClient(clientId, (BinanceDataClientConfig)config, services);
}
