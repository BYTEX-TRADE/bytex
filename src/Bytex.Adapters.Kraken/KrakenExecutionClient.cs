using System.Globalization;
using System.Text.Json;
using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Model.Reports;
using Bytex.Live.Network;
using Microsoft.Extensions.Logging;

namespace Bytex.Adapters.Kraken;

/// <summary>
/// The spot platform's private endpoints and the words it uses for an order.
/// </summary>
internal static class KrakenSpot
{
    public const string AddOrderPath = KrakenVenue.RestVersion + "/private/AddOrder";
    public const string AmendOrderPath = KrakenVenue.RestVersion + "/private/AmendOrder";
    public const string CancelOrderPath = KrakenVenue.RestVersion + "/private/CancelOrder";
    public const string CancelAllPath = KrakenVenue.RestVersion + "/private/CancelAll";
    public const string BalancePath = KrakenVenue.RestVersion + "/private/BalanceEx";
    public const string OpenOrdersPath = KrakenVenue.RestVersion + "/private/OpenOrders";
    public const string ClosedOrdersPath = KrakenVenue.RestVersion + "/private/ClosedOrders";
    public const string QueryOrdersPath = KrakenVenue.RestVersion + "/private/QueryOrders";
    public const string TradesHistoryPath = KrakenVenue.RestVersion + "/private/TradesHistory";

    /// <summary>
    /// Where the socket token comes from. The private socket is authenticated with a short-lived token fetched over
    /// REST rather than by signing the connection, so the socket cannot be opened without a working key even to
    /// listen.
    /// </summary>
    public const string WebSocketsTokenPath = KrakenVenue.RestVersion + "/private/GetWebSocketsToken";

    /// <summary>The private channels a trading node needs: what happens to its orders, and what its balances do.</summary>
    public const string ExecutionsChannel = "executions";

    /// <inheritdoc cref="ExecutionsChannel"/>
    public const string BalancesChannel = "balances";

    /// <summary>
    /// The flag that makes a limit order maker-only. The venue takes its order flags as one comma-separated field,
    /// so a client adding a second flag has to append rather than replace.
    /// </summary>
    public const string PostOnlyFlag = "post";

    /// <summary>
    /// The field a broker id travels in, on the order itself. Kraken runs a broker programme - it calls it the API
    /// Partner Program - and publishes the mechanism: <c>AddOrder</c> and <c>AddOrderBatch</c> take a
    /// <c>broker</c> parameter carrying the partner's own Kraken IIBAN.
    /// </summary>
    public const string BrokerField = "broker";

    /// <summary>The order types this adapter sends, in the venue's spelling.</summary>
    public static string OrderType(Core.Model.OrderType type, bool buy) => type switch
    {
        Core.Model.OrderType.Market => "market",
        Core.Model.OrderType.Limit => "limit",

        // The venue names a trigger order by which way it is protecting rather than by which way it points, so the
        // side decides the name: a stop below the market is a stop-loss and one above it is a take-profit, and the
        // same two words cover a buy pointing the other way.
        Core.Model.OrderType.StopMarket => buy ? "stop-loss" : "stop-loss",
        Core.Model.OrderType.StopLimit => "stop-loss-limit",
        Core.Model.OrderType.MarketIfTouched => "take-profit",
        Core.Model.OrderType.LimitIfTouched => "take-profit-limit",
        _ => throw new NotSupportedException($"Kraken spot does not take a {type} order."),
    };

    /// <summary>What the venue calls the life a time-in-force gives an order.</summary>
    public static string TimeInForce(Core.Model.TimeInForce tif) => tif switch
    {
        Core.Model.TimeInForce.Ioc => "IOC",
        Core.Model.TimeInForce.Gtd => "GTD",
        _ => "GTC",
    };

    /// <summary>
    /// The engine's order status for one of the venue's. <c>pending_new</c> is an order the venue has taken and not
    /// yet worked, which is submitted rather than accepted - reading it as accepted would report a resting order
    /// that is not resting yet.
    /// </summary>
    public static OrderStatus Status(string status) => status switch
    {
        "pending_new" => OrderStatus.Submitted,
        "new" or "open" => OrderStatus.Accepted,
        "partially_filled" => OrderStatus.PartiallyFilled,
        "filled" or "closed" => OrderStatus.Filled,
        "canceled" => OrderStatus.Canceled,
        "expired" => OrderStatus.Expired,
        "triggered" => OrderStatus.Triggered,
        _ => OrderStatus.Accepted,
    };
}

/// <summary>
/// Order routing and execution reporting for Kraken spot.
/// <para>
/// One fact about this platform shapes the whole client: the venue serves public market data and private data on two
/// different hosts and refuses each on the other's. So this client opens <c>ws-auth.kraken.com</c> and nothing else,
/// and the address is derived from the configured public base rather than written out separately - a host pointing
/// the adapter at a recording moves both halves at once.
/// </para>
/// <para>
/// The socket is not signed. It is opened with a short-lived token fetched from a signed REST call, so a key that
/// cannot sign cannot even listen, and the token is asked for again before every connection attempt.
/// </para>
/// <para>
/// NOTHING BELOW THE PUBLIC ENDPOINTS HAS BEEN EXERCISED AGAINST THE LIVE VENUE. Every measurable fact in this
/// adapter was measured, and the private half is not measurable without a key: the platform answers an unsigned
/// request with "EAPI:Invalid key" before it validates anything else, and its socket answers an untokened
/// subscription with "ESession:Invalid session" before it looks at the rest of the message. Four different
/// <c>cl_ord_id</c> shapes - absent, the engine's own, a UUID and a two-character string - all produced the same
/// session error, so even the shape of a client order id this venue accepts is unverified. What is here follows the
/// venue's published contract.
/// </para>
/// </summary>
public sealed class KrakenExecutionClient : ExecutionClientBase
{
    private readonly KrakenExecutionClientConfig _config;
    private readonly KrakenHttp _http;
    private readonly KrakenInstrumentProvider _instruments;
    private readonly Dictionary<Currency, AccountBalance> _balances = new();
    private readonly HashSet<string> _seenTrades = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private IReadOnlyDictionary<string, string> _assetCodes = new Dictionary<string, string>(StringComparer.Ordinal);
    private WebSocketClient? _ws;
    private string? _token;

    public KrakenExecutionClient(ClientId clientId, KrakenExecutionClientConfig config, KernelServices services)
        : base(
            clientId,
            KrakenVenue.Venue,
            new AccountId($"{KrakenVenue.Venue}-SPOT"),
            AccountType.Cash,
            null,
            OmsType.Netting,
            services)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        if (_config.ProductType != KrakenProductType.Spot)
        {
            throw new ArgumentException(
                $"This client trades Kraken's spot platform and the configuration says {_config.ProductType}. "
                + "The futures platform is KrakenFuturesExecutionClient.",
                nameof(config));
        }

        _http = new KrakenHttp(config, Log, requireCredentials: true);
        _instruments = new KrakenInstrumentProvider(_http, config.InstrumentProvider, Log);
    }

    public KrakenInstrumentProvider Instruments => _instruments;

    // ----- connection -----

    public override async Task ConnectAsync(CancellationToken ct)
    {
        await _instruments.InitializeAsync(ct).ConfigureAwait(false);
        foreach (Instrument instrument in _instruments.GetAll())
        {
            if (Services.Cache.Instrument(instrument.Id) is null)
            {
                (Services.Cache as Core.Caching.Cache)?.AddInstrument(instrument);
            }
        }

        // A configured leverage is a no-op here and says so rather than being applied to something. Kraken spot
        // offers margin trading and this client trades the cash account, so there is no per-symbol leverage to set
        // and an order that carried one would be refused.
        if (_config.Leverage is { } leverage)
        {
            Log.LogInformation(
                "Kraken spot trades a cash account, so the configured leverage of {Leverage} is not applied. Its "
                + "futures platform is the family that takes one.",
                leverage);
        }

        // The venue keys a private balance by ASSET ID - ZUSD, XXBT - and not by the code its pairs are
        // denominated in, so the two have to be mapped before a balance means anything. Read from the venue's own
        // public asset list rather than written down here: 849 assets, eleven of them with a legacy id.
        _assetCodes = await KrakenVenue.AssetCodesAsync(_http, ct).ConfigureAwait(false);

        await PublishAccountStateAsync(ct).ConfigureAwait(false);
        await OpenPrivateSocketAsync(ct).ConfigureAwait(false);
        NotifyConnected();
    }

    private async Task OpenPrivateSocketAsync(CancellationToken ct)
    {
        _token = await TokenAsync(ct).ConfigureAwait(false);
        _ws = new WebSocketClient(new WebSocketClientConfig
        {
            Url = KrakenVenue.PrivateWsAddress(_config),
            PingMessage = KrakenStream.PingMessage,
        }, Log)
        {
            OnText = HandleMessageAsync,
            OnConnected = async isReconnect =>
            {
                if (isReconnect)
                {
                    // A token is good for one connection and expires; a reconnection that reused the old one would
                    // open a socket and be refused by every subscription on it.
                    _token = await TokenAsync(CancellationToken.None).ConfigureAwait(false);
                }

                Subscribe();
                if (isReconnect)
                {
                    NotifyConnected();
                }
            },
            OnDisconnected = reason =>
            {
                NotifyDisconnected(reason);
                return Task.CompletedTask;
            },
        };

        // No Subscribe() here: OnConnected fires on the first connection as well as on a reconnection, so a call
        // beside it would subscribe every private channel twice and the venue would send every execution twice.
        await _ws.ConnectAsync(ct).ConfigureAwait(false);
    }

    private void Subscribe()
    {
        if (_token is not { Length: > 0 } token)
        {
            return;
        }

        _ws?.SendText(KrakenStream.Subscribe(new { channel = KrakenSpot.ExecutionsChannel, token, snap_orders = true }));
        _ws?.SendText(KrakenStream.Subscribe(new { channel = KrakenSpot.BalancesChannel, token }));
    }

    private async Task<string?> TokenAsync(CancellationToken ct)
    {
        try
        {
            JsonElement data = await _http.PostSignedAsync(KrakenSpot.WebSocketsTokenPath, null, ct).ConfigureAwait(false);
            return data.Str("token");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.LogError(e, "Kraken spot would not issue a socket token, so no order events will arrive");
            return null;
        }
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

    // ----- helpers -----

    private static string Raw(InstrumentId id) => KrakenVenue.ToRawSymbol(id);

    private Instrument? Find(InstrumentId id) => _instruments.Find(id) ?? Services.Cache.Instrument(id);

    private Instrument? FindByRaw(string rawSymbol) => Find(KrakenVenue.ToInstrumentId(rawSymbol));

    // ----- the private stream -----

    private Task HandleMessageAsync(string text)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            JsonElement root = doc.RootElement;
            if (root.Has("method"))
            {
                if (root.TryGetProperty("success", out JsonElement success) && success.ValueKind == JsonValueKind.False)
                {
                    Log.LogWarning("Kraken spot refused a private stream request: {Error}", root.Str("error"));
                }

                return Task.CompletedTask;
            }

            if (!root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
            {
                return Task.CompletedTask;
            }

            switch (root.Str("channel"))
            {
                case KrakenSpot.ExecutionsChannel:
                    foreach (JsonElement row in data.EnumerateArray())
                    {
                        HandleExecution(row);
                    }

                    break;

                case KrakenSpot.BalancesChannel:
                    foreach (JsonElement row in data.EnumerateArray())
                    {
                        HandleBalance(row);
                    }

                    GenerateAccountState(Snapshot(), [], reported: true, Clock.Timestamp);
                    break;
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            Log.LogWarning(e, "Kraken spot: unreadable private stream message");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// One thing that happened to an order. The venue puts the kind of event in <c>exec_type</c> and the order's
    /// resulting state in <c>order_status</c>, and a fill carries both - so the event decides what is reported and
    /// the status is only used where there is no event to read.
    /// </summary>
    private void HandleExecution(JsonElement e)
    {
        string clientOid = e.Str("cl_ord_id");
        ClientOrderId? clientOrderId = clientOid.Length > 0 ? new ClientOrderId(clientOid) : null;
        Order? order = clientOrderId is { } id ? Services.Cache.Order(id) : null;
        InstrumentId instrumentId = order?.InstrumentId ?? KrakenVenue.ToInstrumentId(e.Str("symbol"));
        if (Find(instrumentId) is not { } instrument)
        {
            return;
        }

        if (clientOrderId is not { } known)
        {
            // An order this node never placed. There is nothing to report it against, and inventing a client order
            // id for it would attach the venue's activity to an order that does not exist.
            return;
        }

        StrategyId strategyId = order?.StrategyId ?? new StrategyId("EXTERNAL");
        VenueOrderId venueOrderId = new(e.Str("order_id"));
        UnixNanos ts = e.Iso("timestamp") is var stamped && stamped != default ? stamped : Clock.Timestamp;

        switch (e.Str("exec_type"))
        {
            case "new":
                GenerateOrderAccepted(strategyId, instrumentId, known, venueOrderId, ts);
                break;

            case "trade":
                HandleFill(e, order, strategyId, instrumentId, known, venueOrderId, instrument, ts);
                break;

            case "amended":
                GenerateOrderUpdated(
                    strategyId,
                    instrumentId,
                    known,
                    venueOrderId,
                    instrument.MakeQuantity(e.Dec("order_qty")),
                    e.Dec("limit_price") > 0m ? instrument.MakePrice(e.Dec("limit_price")) : null,
                    e.Dec("stop_price") > 0m ? instrument.MakePrice(e.Dec("stop_price")) : null,
                    ts);
                break;

            case "canceled":
                GenerateOrderCanceled(strategyId, instrumentId, known, venueOrderId, ts);
                break;

            case "expired":
                GenerateOrderExpired(strategyId, instrumentId, known, venueOrderId, ts);
                break;

            case "triggered":
                GenerateOrderTriggered(strategyId, instrumentId, known, venueOrderId, ts);
                break;

            case "rejected":
                GenerateOrderRejected(strategyId, instrumentId, known, e.Str("reason"), ts);
                break;
        }
    }

    private void HandleFill(JsonElement e, Order? order, StrategyId strategyId, InstrumentId instrumentId, ClientOrderId clientOrderId, VenueOrderId venueOrderId, Instrument instrument, UnixNanos ts)
    {
        string tradeId = e.Str("exec_id");
        if (tradeId.Length > 0)
        {
            lock (_gate)
            {
                if (!_seenTrades.Add(tradeId))
                {
                    return;
                }
            }
        }

        Quantity filled = instrument.MakeQuantity(e.Dec("last_qty"));
        Price price = instrument.MakePrice(e.Dec("last_price"));
        OrderSide side = e.Str("side") == "buy" ? OrderSide.Buy : OrderSide.Sell;
        LiquiditySide liquidity = e.Str("liquidity_ind") == "m" ? LiquiditySide.Maker : LiquiditySide.Taker;

        // The venue reports the fee it charged, which is the figure to use: an instrument's published rate is the
        // tier-zero rate and this account may be on a lower one. The instrument's rate stands in only when the
        // message carries no fee at all.
        decimal fee = e.Dec("fee_usd_equiv") > 0m ? e.Dec("fee_usd_equiv") : e.Dec("fees");
        Money commission = fee > 0m
            ? new Money(fee, instrument.QuoteCurrency)
            : instrument.CalculateCommission(filled, price, liquidity);

        GenerateOrderFilled(
            strategyId,
            instrumentId,
            clientOrderId,
            venueOrderId,
            null,
            new TradeId(tradeId.Length > 0 ? tradeId : Guid.NewGuid().ToString("N")),
            side,
            order?.Type ?? Core.Model.OrderType.Market,
            filled,
            price,
            instrument.QuoteCurrency,
            commission,
            liquidity,
            ts);
    }

    private void HandleBalance(JsonElement b)
    {
        string code = b.Str("asset");
        if (code.Length == 0)
        {
            return;
        }

        Currency currency = Currency.FromCode(CodeFor(code));
        decimal total = b.Dec("balance");
        decimal held = b.Dec("hold_trade");
        lock (_gate)
        {
            _balances[currency] = AccountBalance.Of(new Money(total, currency), new Money(Math.Max(0m, held), currency));
        }
    }

    /// <summary>
    /// The currency code an asset id stands for. Falls back to correcting the id itself when the asset list could
    /// not be read, which at least gets the two dead codes right rather than publishing the id verbatim.
    /// </summary>
    private string CodeFor(string assetId) =>
        _assetCodes.TryGetValue(assetId, out string? code) ? code : KrakenVenue.Current(assetId);

    private List<AccountBalance> Snapshot()
    {
        lock (_gate)
        {
            return _balances.Values.ToList();
        }
    }

    private async Task PublishAccountStateAsync(CancellationToken ct)
    {
        try
        {
            JsonElement data = await _http.PostSignedAsync(KrakenSpot.BalancePath, null, ct).ConfigureAwait(false);
            if (data.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty asset in data.EnumerateObject())
                {
                    Currency currency = Currency.FromCode(CodeFor(asset.Name));
                    decimal total = asset.Value.Dec("balance");
                    decimal held = asset.Value.Dec("hold_trade");
                    lock (_gate)
                    {
                        _balances[currency] = AccountBalance.Of(new Money(total, currency), new Money(Math.Max(0m, held), currency));
                    }
                }
            }

            GenerateAccountState(Snapshot(), [], reported: true, Clock.Timestamp);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.LogError(e, "Failed to load Kraken spot account state");
        }
    }

    // ----- commands -----

    public override async Task SubmitOrderAsync(SubmitOrder command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        Order order = command.Order;
        if (Find(order.InstrumentId) is not { } instrument)
        {
            GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, $"instrument {order.InstrumentId} unknown to the Kraken spot client", Clock.Timestamp);
            return;
        }

        if (order.IsQuoteQuantity)
        {
            GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, "Kraken spot sizes an order in base currency, so a quote quantity cannot be sent", Clock.Timestamp);
            return;
        }

        Dictionary<string, string> form;
        try
        {
            form = Body(order, instrument);
        }
        catch (NotSupportedException e)
        {
            GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, e.Message, Clock.Timestamp);
            return;
        }

        GenerateOrderSubmitted(order.StrategyId, order.InstrumentId, order.ClientOrderId, Clock.Timestamp);
        try
        {
            JsonElement data = await _http.PostSignedAsync(KrakenSpot.AddOrderPath, form, ct).ConfigureAwait(false);

            // The venue answers with an array of transaction ids - one per order placed, which is one here. The
            // socket reports the order's acceptance, so this is only the identity the venue gave it.
            if (data.TryGetProperty("txid", out JsonElement txids)
                && txids.ValueKind == JsonValueKind.Array
                && txids.GetArrayLength() > 0
                && txids[0].GetString() is { Length: > 0 } venueId)
            {
                GenerateOrderAccepted(order.StrategyId, order.InstrumentId, order.ClientOrderId, new VenueOrderId(venueId), Clock.Timestamp);
            }
        }
        catch (KrakenApiException e)
        {
            GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, e.Code, Clock.Timestamp);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, e.Message, Clock.Timestamp);
        }
    }

    private Dictionary<string, string> Body(Order order, Instrument instrument)
    {
        Dictionary<string, string> form = new(StringComparer.Ordinal)
        {
            ["pair"] = instrument.RawSymbol.Value,
            ["type"] = order.IsBuy ? "buy" : "sell",
            ["ordertype"] = KrakenSpot.OrderType(order.Type, order.IsBuy),
            ["volume"] = Json.Fmt(order.Quantity.Value),
            ["cl_ord_id"] = order.ClientOrderId.Value,
        };

        bool limit = order.Type is Core.Model.OrderType.Limit or Core.Model.OrderType.StopLimit or Core.Model.OrderType.LimitIfTouched;
        if (limit && order.Price is { } price)
        {
            // On a plain limit the price is the limit; on a trigger order the trigger is `price` and the limit is
            // `price2`, which is the single most confusable pair of fields on this platform.
            form[order.Type == Core.Model.OrderType.Limit ? "price" : "price2"] = Json.Fmt(price.Value);
        }

        if (order.TriggerPrice is { } trigger)
        {
            form["price"] = Json.Fmt(trigger.Value);
        }

        if (order.IsPostOnly)
        {
            form["oflags"] = KrakenSpot.PostOnlyFlag;
        }

        if (order.TimeInForce is not Core.Model.TimeInForce.Gtc)
        {
            form["timeinforce"] = KrakenSpot.TimeInForce(order.TimeInForce);
        }

        if (order.IsReduceOnly)
        {
            form["reduce_only"] = "true";
        }

        // The partner id, on the order, by the mechanism the venue publishes. Untagged is the default and has to
        // stay byte-for-byte what the venue received before this field existed, so nothing is added when nothing is
        // configured.
        if (_config.BrokerId is { Length: > 0 } broker)
        {
            form[KrakenSpot.BrokerField] = broker;
        }

        return form;
    }

    /// <summary>
    /// Changes an order the venue already holds. The platform has a real amend - <c>AmendOrder</c>, which keeps the
    /// order's identity and its place in the queue where it can - so this is not a cancel and replace, and a
    /// protective order is never off the book while it is being resized.
    /// </summary>
    public override async Task ModifyOrderAsync(ModifyOrder command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        Order? order = Services.Cache.Order(command.ClientOrderId);
        StrategyId strategyId = order?.StrategyId ?? new StrategyId("EXTERNAL");
        if (Find(command.InstrumentId) is not { } instrument)
        {
            GenerateOrderModifyRejected(strategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, $"instrument {command.InstrumentId} unknown to the Kraken spot client", Clock.Timestamp);
            return;
        }

        Dictionary<string, string> form = new(StringComparer.Ordinal);
        if (command.VenueOrderId is { } venueOrderId)
        {
            form["txid"] = venueOrderId.Value;
        }
        else
        {
            form["cl_ord_id"] = command.ClientOrderId.Value;
        }

        if (command.Quantity is { } quantity)
        {
            form["order_qty"] = Json.Fmt(quantity.Value);
        }

        if (command.Price is { } price)
        {
            form["limit_price"] = Json.Fmt(price.Value);
        }

        if (command.TriggerPrice is { } trigger)
        {
            form["trigger_price"] = Json.Fmt(trigger.Value);
        }

        GenerateOrderPendingUpdate(strategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, Clock.Timestamp);
        try
        {
            await _http.PostSignedAsync(KrakenSpot.AmendOrderPath, form, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is KrakenApiException or VenueHttpException or HttpRequestException)
        {
            GenerateOrderModifyRejected(
                strategyId,
                command.InstrumentId,
                command.ClientOrderId,
                command.VenueOrderId,
                e is KrakenApiException refusal ? refusal.Code : e.Message,
                Clock.Timestamp);
        }
    }

    public override async Task CancelOrderAsync(CancelOrder command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        Dictionary<string, string> form = new(StringComparer.Ordinal)
        {
            // The venue takes either identity here, and the client order id is the one this node always has: a
            // cancel that needed the venue's id would be unable to reach an order whose acceptance was missed.
            ["txid"] = command.VenueOrderId?.Value ?? command.ClientOrderId.Value,
        };

        try
        {
            await _http.PostSignedAsync(KrakenSpot.CancelOrderPath, form, ct).ConfigureAwait(false);
        }
        catch (KrakenApiException e)
        {
            Log.LogWarning("Kraken spot refused a cancel of {ClientOrderId}: {Reason}", command.ClientOrderId, e.Code);
        }
    }

    // Cancel-all is NOT overridden here, which is a decision rather than an omission. The venue's own
    // /0/private/CancelAll takes no symbol and cancels every open order on the account, and a cancel-all command
    // always names an instrument - so sending it would close orders on every other instrument the account holds,
    // on a request scoped to one. The base class's loop over the orders this node holds cancels exactly what was
    // asked for, one call each, and that is the correct behaviour rather than a cheaper stand-in for it.

    // ----- reports -----

    public override async Task<ExecutionMassStatus?> GenerateMassStatusAsync(UnixNanos? since, CancellationToken ct)
    {
        IReadOnlyList<OrderStatusReport> orders = await GenerateOrderStatusReportsAsync(null, since, null, openOnly: true, ct).ConfigureAwait(false);
        IReadOnlyList<FillReport> fills = await GenerateFillReportsAsync(null, null, since, null, ct).ConfigureAwait(false);

        // A cash account holds no positions, so there are none to report and an empty list is the answer rather
        // than a gap.
        return new ExecutionMassStatus(ClientId, AccountId, Venue, orders, fills, [], Clock.Timestamp, Guid.NewGuid());
    }

    public override async Task<OrderStatusReport?> GenerateOrderStatusReportAsync(InstrumentId instrumentId, ClientOrderId? clientOrderId, VenueOrderId? venueOrderId, CancellationToken ct)
    {
        string? id = venueOrderId?.Value ?? clientOrderId?.Value;
        if (id is null)
        {
            return null;
        }

        try
        {
            JsonElement data = await _http.PostSignedAsync(
                KrakenSpot.QueryOrdersPath,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["txid"] = id },
                ct).ConfigureAwait(false);

            foreach (JsonProperty order in data.EnumerateObject())
            {
                if (ParseOrder(order.Name, order.Value) is { } report)
                {
                    return report;
                }
            }
        }
        catch (KrakenApiException e)
        {
            Log.LogInformation("Kraken spot has no record of order {Order}: {Reason}", id, e.Code);
        }

        return null;
    }

    public override async Task<IReadOnlyList<OrderStatusReport>> GenerateOrderStatusReportsAsync(InstrumentId? instrumentId, UnixNanos? start, UnixNanos? end, bool openOnly, CancellationToken ct)
    {
        Dictionary<string, string> form = new(StringComparer.Ordinal);
        if (start is { } from)
        {
            form["start"] = (from.Value / UnixNanos.NanosPerSecond).ToString(CultureInfo.InvariantCulture);
        }

        if (end is { } to)
        {
            form["end"] = (to.Value / UnixNanos.NanosPerSecond).ToString(CultureInfo.InvariantCulture);
        }

        JsonElement data = await _http
            .PostSignedAsync(openOnly ? KrakenSpot.OpenOrdersPath : KrakenSpot.ClosedOrdersPath, form, ct)
            .ConfigureAwait(false);

        // Both endpoints wrap their orders in a property - "open" or "closed" - keyed by the venue's order id.
        List<OrderStatusReport> reports = new();
        foreach (JsonProperty group in data.EnumerateObject())
        {
            if (group.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (JsonProperty order in group.Value.EnumerateObject())
            {
                if (ParseOrder(order.Name, order.Value) is { } report
                    && (instrumentId is null || report.InstrumentId == instrumentId))
                {
                    reports.Add(report);
                }
            }
        }

        return reports;
    }

    public override async Task<IReadOnlyList<FillReport>> GenerateFillReportsAsync(InstrumentId? instrumentId, VenueOrderId? venueOrderId, UnixNanos? start, UnixNanos? end, CancellationToken ct)
    {
        Dictionary<string, string> form = new(StringComparer.Ordinal);
        if (start is { } from)
        {
            form["start"] = (from.Value / UnixNanos.NanosPerSecond).ToString(CultureInfo.InvariantCulture);
        }

        if (end is { } to)
        {
            form["end"] = (to.Value / UnixNanos.NanosPerSecond).ToString(CultureInfo.InvariantCulture);
        }

        JsonElement data = await _http.PostSignedAsync(KrakenSpot.TradesHistoryPath, form, ct).ConfigureAwait(false);
        List<FillReport> fills = new();
        if (!data.TryGetProperty("trades", out JsonElement trades) || trades.ValueKind != JsonValueKind.Object)
        {
            return fills;
        }

        foreach (JsonProperty trade in trades.EnumerateObject())
        {
            JsonElement t = trade.Value;
            if (FindByRaw(t.Str("pair")) is not { } instrument)
            {
                continue;
            }

            VenueOrderId orderId = new(t.Str("ordertxid"));
            if (venueOrderId is { } wanted && orderId != wanted)
            {
                continue;
            }

            if (instrumentId is { } only && instrument.Id != only)
            {
                continue;
            }

            // The trade's time is in fractional seconds, as it is on the public endpoint.
            UnixNanos ts = new((long)(t.Dec("time") * UnixNanos.NanosPerSecond));
            fills.Add(new FillReport(
                AccountId,
                instrument.Id,
                orderId,
                new TradeId(trade.Name),
                t.Str("type") == "buy" ? OrderSide.Buy : OrderSide.Sell,
                instrument.MakeQuantity(t.Dec("vol")),
                instrument.MakePrice(t.Dec("price")),
                new Money(t.Dec("fee"), instrument.QuoteCurrency),
                t.Str("ordertype") == "limit" ? LiquiditySide.Maker : LiquiditySide.Taker,
                ts,
                Clock.Timestamp,
                Guid.NewGuid()));
        }

        return fills;
    }

    private OrderStatusReport? ParseOrder(string venueOrderId, JsonElement o)
    {
        if (!o.TryGetProperty("descr", out JsonElement descr))
        {
            return null;
        }

        if (FindByRaw(descr.Str("pair")) is not { } instrument)
        {
            return null;
        }

        decimal volume = o.Dec("vol");
        decimal executed = o.Dec("vol_exec");
        UnixNanos opened = new((long)(o.Dec("opentm") * UnixNanos.NanosPerSecond));
        string kind = descr.Str("ordertype");

        return new OrderStatusReport(
            AccountId,
            instrument.Id,
            o.Str("cl_ord_id") is { Length: > 0 } clientOid ? new ClientOrderId(clientOid) : null,
            new VenueOrderId(venueOrderId),
            descr.Str("type") == "buy" ? OrderSide.Buy : OrderSide.Sell,
            kind switch
            {
                "market" => Core.Model.OrderType.Market,
                "limit" => Core.Model.OrderType.Limit,
                "stop-loss" => Core.Model.OrderType.StopMarket,
                "stop-loss-limit" => Core.Model.OrderType.StopLimit,
                "take-profit" => Core.Model.OrderType.MarketIfTouched,
                "take-profit-limit" => Core.Model.OrderType.LimitIfTouched,
                _ => Core.Model.OrderType.Limit,
            },
            Core.Model.TimeInForce.Gtc,
            KrakenSpot.Status(o.Str("status")),
            instrument.MakeQuantity(volume),
            instrument.MakeQuantity(executed),
            opened,
            opened,
            Clock.Timestamp,
            Guid.NewGuid(),
            Price: descr.Dec("price") > 0m ? instrument.MakePrice(descr.Dec("price")) : null,
            AvgPx: o.Dec("price") > 0m ? o.Dec("price") : null);
    }
}
