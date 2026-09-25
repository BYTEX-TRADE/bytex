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

namespace Bytex.Adapters.Bybit;

/// <summary>
/// Market data from Bybit V5 public streams and REST history.
/// </summary>
public sealed class BybitDataClient : DataClientBase
{
    private readonly BybitDataClientConfig _config;
    private readonly BybitHttp _http;
    private readonly BybitInstrumentProvider _instruments;
    private readonly HashSet<string> _topics = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BarType> _klineTopics = new(StringComparer.Ordinal);
    private readonly Dictionary<InstrumentId, (decimal Bid, decimal Ask, decimal BidSize, decimal AskSize)> _bookTops = new();

    /// <summary>
    /// The options whose trades are subscribed. Kept because one option trade topic serves every contract on an
    /// underlying, so dropping it when a single contract unsubscribes would stop the trades of all the others.
    /// </summary>
    private readonly HashSet<InstrumentId> _optionTradeSubscribers = new();
    private WebSocketClient? _ws;

    public BybitDataClient(ClientId clientId, BybitDataClientConfig config, KernelServices services)
        : base(clientId, BybitVenue.Venue, services)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _http = new BybitHttp(config, Log);
        _instruments = new BybitInstrumentProvider(_http, config.ProductType, config.InstrumentProvider, Log);
    }

    public BybitInstrumentProvider Instruments => _instruments;

    public override async Task ConnectAsync(CancellationToken ct)
    {
        await _instruments.InitializeAsync(ct).ConfigureAwait(false);
        foreach (Instrument instrument in _instruments.GetAll())
        {
            HandleInstrument(instrument);
        }

        _ws = new WebSocketClient(new WebSocketClientConfig { Url = new Uri(BybitVenue.WsPublic(_config)), PingMessage = "{\"op\":\"ping\"}", PingInterval = BybitVenue.PingInterval }, Log)
        {
            OnText = HandleMessageAsync,
            OnConnected = isReconnect =>
            {
                if (isReconnect && _topics.Count > 0)
                {
                    Send(new { op = "subscribe", args = _topics.ToList() });
                }

                if (isReconnect)
                {
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
                // Options take their top of book from the tickers topic, because this venue's orderbook.1 topic
                // does not serve them: measured, the option socket accepts orderbook.1, lists it under
                // successTopics and delivers nothing for it, while tickers carries bid, ask and both sizes.
                AddTopic(_config.ProductType == BybitProductType.Option
                    ? $"tickers.{Raw(q.InstrumentId)}"
                    : $"orderbook.{BybitVenue.TopOfBookDepth}.{Raw(q.InstrumentId)}");
                break;
            case SubscribeTradeTicks t when _config.ProductType == BybitProductType.Option:
                {
                    // The option socket publishes trades PER UNDERLYING and not per contract: publicTrade.BTC
                    // delivers every BTC option's trades, each row naming its own contract, while
                    // publicTrade.<contract> is accepted, reported as a success and silent. So the subscription
                    // names the underlying, which has to be known - it comes from the instrument and not from the
                    // symbol's spelling.
                    if (Underlying(t.InstrumentId) is not { } coin)
                    {
                        Sink.OnSubscriptionFailed(
                            ClientId,
                            command,
                            $"{t.InstrumentId} is not loaded, so the underlying Bybit publishes its option trades under is unknown");
                        return;
                    }

                    _optionTradeSubscribers.Add(t.InstrumentId);
                    AddTopic($"publicTrade.{coin}");
                    break;
                }

            case SubscribeTradeTicks t:
                AddTopic($"publicTrade.{Raw(t.InstrumentId)}");
                break;
            case SubscribeBars b when b.BarType.IsExternal && BybitVenue.HasCandles(_config.ProductType):
                {
                    string topic = $"kline.{BybitVenue.Interval(b.BarType.Spec)}.{Raw(b.BarType.InstrumentId)}";
                    _klineTopics[topic] = b.BarType;
                    AddTopic(topic);
                    break;
                }

            case SubscribeOrderBookDeltas d:
                AddTopic($"orderbook.{BookDepth(d.Depth)}.{Raw(d.InstrumentId)}");
                break;
            case SubscribeMarkPrices when _config.ProductType != BybitProductType.Spot:
            case SubscribeIndexPrices when _config.ProductType != BybitProductType.Spot:
                AddTopic($"tickers.{Raw(InstrumentOf(command))}");
                break;
            case SubscribeFundingRates when BybitVenue.PaysFunding(_config.ProductType):
                AddTopic($"tickers.{Raw(InstrumentOf(command))}");
                break;
            default:
                Sink.OnSubscriptionFailed(ClientId, command, $"{command.GetType().Name} is not supported by the Bybit {_config.ProductType} data client");
                break;
        }
    }

    /// <summary>
    /// Which of the venue's published book depths serves a requested one. The option socket publishes a different
    /// pair from the other three families and silently ignores theirs, so the pair is chosen per family rather
    /// than shared.
    /// </summary>
    private int BookDepth(int requested)
    {
        (int small, int large) = _config.ProductType == BybitProductType.Option
            ? (BybitVenue.SmallOptionBookDepth, BybitVenue.LargeOptionBookDepth)
            : (BybitVenue.SmallBookDepth, BybitVenue.LargeBookDepth);

        return requested <= 0 || requested <= small ? small : large;
    }

    /// <summary>
    /// The coin an option is written on, as the instrument states it - never read off the symbol. Null when this
    /// client holds no such instrument, which is a question it cannot answer rather than one to guess at.
    /// </summary>
    private string? Underlying(InstrumentId id) =>
        (_instruments.Find(id) ?? Services.Cache.Instrument(id))?.BaseCurrency?.Code;

    private static InstrumentId InstrumentOf(SubscribeCommand command) => command switch
    {
        SubscribeMarkPrices m => m.InstrumentId,
        SubscribeIndexPrices i => i.InstrumentId,
        SubscribeFundingRates f => f.InstrumentId,
        _ => throw new ArgumentOutOfRangeException(nameof(command)),
    };

    public override Task UnsubscribeAsync(UnsubscribeCommand command, CancellationToken ct)
    {
        if (command is UnsubscribeTradeTicks ut && _config.ProductType == BybitProductType.Option)
        {
            return UnsubscribeOptionTradesAsync(ut);
        }

        string? topic = command switch
        {
            UnsubscribeQuoteTicks q => _config.ProductType == BybitProductType.Option
                ? $"tickers.{Raw(q.InstrumentId)}"
                : $"orderbook.{BybitVenue.TopOfBookDepth}.{Raw(q.InstrumentId)}",
            UnsubscribeTradeTicks t => $"publicTrade.{Raw(t.InstrumentId)}",
            UnsubscribeBars b => $"kline.{BybitVenue.Interval(b.BarType.Spec)}.{Raw(b.BarType.InstrumentId)}",
            UnsubscribeMarkPrices m => $"tickers.{Raw(m.InstrumentId)}",
            _ => null,
        };

        if (topic is not null && _topics.Remove(topic))
        {
            _klineTopics.Remove(topic);
            Send(new { op = "unsubscribe", args = new[] { topic } });
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Drops one option's trades, and the shared topic only when it was the last contract using it. This venue
    /// publishes option trades per underlying, so the naive unsubscribe would have taken away the trades of every
    /// other contract on the same coin - silently, while each of those subscriptions still looked alive.
    /// </summary>
    private Task UnsubscribeOptionTradesAsync(UnsubscribeTradeTicks command)
    {
        if (!_optionTradeSubscribers.Remove(command.InstrumentId) || Underlying(command.InstrumentId) is not { } coin)
        {
            return Task.CompletedTask;
        }

        if (_optionTradeSubscribers.Any(id => Underlying(id) == coin))
        {
            return Task.CompletedTask;
        }

        string topic = $"publicTrade.{coin}";
        if (_topics.Remove(topic))
        {
            Send(new { op = "unsubscribe", args = new[] { topic } });
        }

        return Task.CompletedTask;
    }

    private void AddTopic(string topic)
    {
        if (_topics.Add(topic))
        {
            Send(new { op = "subscribe", args = new[] { topic } });
        }
    }

    private void Send(object message) => _ws?.SendText(JsonSerializer.Serialize(message));

    private static string Raw(InstrumentId id) => BybitVenue.ToRawSymbol(id);

    private Instrument? InstrumentFor(string rawSymbol)
    {
        InstrumentId id = BybitVenue.ToInstrumentId(rawSymbol, _config.ProductType);
        return _instruments.Find(id) ?? Services.Cache.Instrument(id);
    }

    private Task HandleMessageAsync(string text)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("topic", out JsonElement topicElement))
            {
                return Task.CompletedTask;
            }

            string topic = topicElement.GetString() ?? string.Empty;
            JsonElement data = root.GetProperty("data");
            UnixNanos ts = root.Has("ts") ? root.Ms("ts") : Clock.Timestamp;
            string type = root.Str("type");

            if (topic.StartsWith($"orderbook.{BybitVenue.TopOfBookDepth}.", StringComparison.Ordinal))
            {
                HandleTopOfBook(data, ts, type == "snapshot");
            }
            else if (topic.StartsWith("orderbook.", StringComparison.Ordinal))
            {
                HandleBook(data, ts, type == "snapshot");
            }
            else if (topic.StartsWith("publicTrade.", StringComparison.Ordinal))
            {
                HandleTrades(data);
            }
            else if (topic.StartsWith("kline.", StringComparison.Ordinal))
            {
                HandleKline(topic, data);
            }
            else if (topic.StartsWith("tickers.", StringComparison.Ordinal))
            {
                HandleTicker(data, ts);
            }
        }
        catch (Exception e)
        {
            Log.LogWarning(e, "Failed to parse Bybit message: {Text}", LogText.Truncate(text, LogText.MaxMessageLength));
        }

        return Task.CompletedTask;
    }

    private void HandleTopOfBook(JsonElement d, UnixNanos ts, bool snapshot)
    {
        Instrument? instrument = InstrumentFor(d.Str("s"));
        if (instrument is null)
        {
            return;
        }

        (decimal bid, decimal ask, decimal bidSize, decimal askSize) = _bookTops.GetValueOrDefault(instrument.Id);
        foreach (JsonElement level in d.GetProperty("b").EnumerateArray())
        {
            decimal size = level[1].DecValue();
            if (size > 0m || snapshot)
            {
                bid = level[0].DecValue();
                bidSize = size;
            }
        }

        foreach (JsonElement level in d.GetProperty("a").EnumerateArray())
        {
            decimal size = level[1].DecValue();
            if (size > 0m || snapshot)
            {
                ask = level[0].DecValue();
                askSize = size;
            }
        }

        if (bid <= 0m || ask <= 0m)
        {
            return;
        }

        _bookTops[instrument.Id] = (bid, ask, bidSize, askSize);
        HandleData(new QuoteTick(instrument.Id, instrument.MakePrice(bid), instrument.MakePrice(ask), instrument.MakeQuantity(bidSize), instrument.MakeQuantity(askSize), ts, Clock.Timestamp));
    }

    private void HandleBook(JsonElement d, UnixNanos ts, bool snapshot)
    {
        Instrument? instrument = InstrumentFor(d.Str("s"));
        if (instrument is null)
        {
            return;
        }

        ulong sequence = (ulong)d.Long("seq");
        UnixNanos now = Clock.Timestamp;
        List<OrderBookDelta> deltas = new();
        if (snapshot)
        {
            deltas.Add(OrderBookDelta.Clear(instrument.Id, sequence, ts, now));
        }

        ulong orderId = 0;
        foreach (JsonElement level in d.GetProperty("b").EnumerateArray())
        {
            decimal size = level[1].DecValue();
            deltas.Add(new OrderBookDelta(instrument.Id, size == 0m ? BookAction.Delete : snapshot ? BookAction.Add : BookAction.Update,
                new BookOrder(OrderSide.Buy, instrument.MakePrice(level[0].DecValue()), instrument.MakeQuantity(size), ++orderId), RecordFlags.None, sequence, ts, now));
        }

        foreach (JsonElement level in d.GetProperty("a").EnumerateArray())
        {
            decimal size = level[1].DecValue();
            deltas.Add(new OrderBookDelta(instrument.Id, size == 0m ? BookAction.Delete : snapshot ? BookAction.Add : BookAction.Update,
                new BookOrder(OrderSide.Sell, instrument.MakePrice(level[0].DecValue()), instrument.MakeQuantity(size), ++orderId), RecordFlags.None, sequence, ts, now));
        }

        HandleData(new OrderBookDeltas(instrument.Id, deltas, snapshot ? RecordFlags.Snapshot | RecordFlags.Last : RecordFlags.Last, sequence, ts, now));
    }

    private void HandleTrades(JsonElement data)
    {
        foreach (JsonElement t in data.EnumerateArray())
        {
            Instrument? instrument = InstrumentFor(t.Str("s"));
            if (instrument is null)
            {
                continue;
            }

            HandleData(new TradeTick(instrument.Id, instrument.MakePrice(t.Dec("p")), instrument.MakeQuantity(t.Dec("v")),
                t.Str("S") == "Buy" ? AggressorSide.Buyer : AggressorSide.Seller, new TradeId(t.Str("i")), t.Ms("T"), Clock.Timestamp));
        }
    }

    private void HandleKline(string topic, JsonElement data)
    {
        if (!_klineTopics.TryGetValue(topic, out BarType barType))
        {
            return;
        }

        Instrument? instrument = _instruments.Find(barType.InstrumentId) ?? Services.Cache.Instrument(barType.InstrumentId);
        if (instrument is null)
        {
            return;
        }

        foreach (JsonElement k in data.EnumerateArray())
        {
            bool confirmed = k.Bool("confirm");
            if (!confirmed && !_config.HandleRevisedBars)
            {
                continue;
            }

            HandleData(new Bar(barType, instrument.MakePrice(k.Dec("open")), instrument.MakePrice(k.Dec("high")), instrument.MakePrice(k.Dec("low")), instrument.MakePrice(k.Dec("close")),
                instrument.MakeQuantity(k.Dec("volume")), UnixNanos.FromMilliseconds(k.Long("end") + 1), Clock.Timestamp, IsRevision: !confirmed));
        }
    }

    private void HandleTicker(JsonElement d, UnixNanos ts)
    {
        Instrument? instrument = InstrumentFor(d.Str("symbol"));
        if (instrument is null)
        {
            return;
        }

        UnixNanos now = Clock.Timestamp;
        if (_config.ProductType == BybitProductType.Option)
        {
            HandleOptionTopOfBook(instrument, d, ts, now);
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
            HandleData(new FundingRateUpdate(instrument.Id, d.Dec("fundingRate"), d.Has("nextFundingTime") ? d.Ms("nextFundingTime") : null, ts, now));
        }
    }

    /// <summary>
    /// An option's top of book, which arrives on the tickers topic because this venue's orderbook.1 topic does not
    /// serve options at all.
    /// <para>
    /// The field names are the option market's own - bidPrice and bidSize where the contract markets write
    /// bid1Price and bid1Size - and a delta carries only what changed, so the side that did not change is carried
    /// forward from the last one. A quote needs both sides, and a book with no bid or no ask is not a quote: these
    /// contracts are illiquid enough that one-sided books are ordinary rather than exceptional.
    /// </para>
    /// </summary>
    private void HandleOptionTopOfBook(Instrument instrument, JsonElement d, UnixNanos ts, UnixNanos now)
    {
        (decimal bid, decimal ask, decimal bidSize, decimal askSize) = _bookTops.GetValueOrDefault(instrument.Id);
        if (d.Has("bidPrice"))
        {
            bid = d.Dec("bidPrice");
            bidSize = d.Dec("bidSize");
        }

        if (d.Has("askPrice"))
        {
            ask = d.Dec("askPrice");
            askSize = d.Dec("askSize");
        }

        _bookTops[instrument.Id] = (bid, ask, bidSize, askSize);
        if (bid <= 0m || ask <= 0m)
        {
            return;
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
                case RequestFundingRates rf:
                    SendResponse(rf, typeof(FundingRateUpdate), await FetchFundingRatesAsync(rf, ct).ConfigureAwait(false));
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
                    SendErrorResponse(command, $"{command.GetType().Name} is not supported by the Bybit data client");
                    break;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.LogError(e, "Bybit request {Request} failed", command.GetType().Name);
            SendErrorResponse(command, e.Message);
        }
    }

    /// <summary>
    /// The candle history the venue holds. The paging and the newest-first reversal live in
    /// <see cref="BybitHistory"/>, which a catalog download calls as well, so stored bars and a node's bars come from
    /// one piece of code rather than two that agree until the venue changes a default.
    /// </summary>
    private async Task<IReadOnlyList<IData>> FetchBarsAsync(RequestBars request, CancellationToken ct)
    {
        Instrument? instrument = _instruments.Find(request.BarType.InstrumentId) ?? Services.Cache.Instrument(request.BarType.InstrumentId);
        if (instrument is null)
        {
            return [];
        }

        return [.. await BybitHistory.FetchBarsAsync(_http, instrument, request.BarType, request.Start, request.End, request.Limit, Clock.Timestamp, ct).ConfigureAwait(false)];
    }

    /// <summary>
    /// The funding a perpetual has charged. The paging and the newest-first reversal live in
    /// <see cref="BybitHistory"/>, which a catalog download calls as well, so stored rates and a node's rates come
    /// from one piece of code.
    /// </summary>
    private async Task<IReadOnlyList<IData>> FetchFundingRatesAsync(RequestFundingRates request, CancellationToken ct)
    {
        if (_instruments.Find(request.InstrumentId) is null && Services.Cache.Instrument(request.InstrumentId) is null)
        {
            return [];
        }

        IReadOnlyList<FundingRateUpdate> rates = await BybitHistory.FetchFundingRatesAsync(
            _http,
            request.InstrumentId,
            request.Start?.ToMilliseconds(),
            request.End?.ToMilliseconds(),
            request.Limit,
            ct).ConfigureAwait(false);
        return rates.Cast<IData>().ToList();
    }

    private async Task<IReadOnlyList<IData>> FetchTradesAsync(RequestTradeTicks request, CancellationToken ct)
    {
        Instrument? instrument = _instruments.Find(request.InstrumentId) ?? Services.Cache.Instrument(request.InstrumentId);
        if (instrument is null)
        {
            return [];
        }

        Dictionary<string, string> query = new()
        {
            ["category"] = _http.Category,
            ["symbol"] = BybitVenue.ToRawSymbol(request.InstrumentId),
            ["limit"] = Math.Min(BybitVenue.TradePage, request.Limit ?? BybitVenue.TradePage).ToString(CultureInfo.InvariantCulture),
        };
        JsonElement result = await _http.GetPublicAsync("/v5/market/recent-trade", query, ct).ConfigureAwait(false);
        List<IData> trades = new();
        foreach (JsonElement t in result.GetProperty("list").EnumerateArray())
        {
            UnixNanos ts = t.Ms("time");
            trades.Add(new TradeTick(instrument.Id, instrument.MakePrice(t.Dec("price")), instrument.MakeQuantity(t.Dec("size")),
                t.Str("side") == "Buy" ? AggressorSide.Buyer : AggressorSide.Seller, new TradeId(t.Str("execId")), ts, ts));
        }

        trades.Reverse();
        return trades;
    }
}

public sealed class BybitDataClientFactory : IDataClientFactory
{
    public string Name => "BYBIT";

    public Type ConfigType => typeof(BybitDataClientConfig);

    public IDataClient Create(ClientId clientId, DataClientConfig config, KernelServices services) =>
        new BybitDataClient(clientId, (BybitDataClientConfig)config, services);
}
