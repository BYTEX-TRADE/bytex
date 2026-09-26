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

namespace Bytex.Adapters.Bitget;

/// <summary>
/// How a derivative position is margined on this venue, which its order endpoint demands on every order rather than
/// taking from the account.
/// <para>
/// It is configuration rather than something read off the account because the venue makes it a property of the ORDER:
/// <c>marginMode</c> is required by place-order, so a client that did not know it could not place one at all. And the
/// two modes differ in a way that reaches the leverage: crossed margin holds one leverage for a contract, and isolated
/// margin holds one per side.
/// </para>
/// </summary>
public enum BitgetMarginMode
{
    /// <summary>
    /// One pool of collateral behind every position, and one leverage per contract. The default, because it is the
    /// mode in which a single configured leverage means exactly one thing.
    /// </summary>
    Crossed,

    /// <summary>
    /// Collateral and leverage per position. The venue takes the leverage per side here, so a single configured figure
    /// is applied to both sides.
    /// </summary>
    Isolated,
}

/// <summary>
/// Order routing and execution reporting for one Bitget account, in whichever family the configuration names.
/// <para>
/// One client for all three families, because the shape of an order is the same in all of them and only the path and
/// a handful of required fields change: the derivative endpoints want a product type, a margin mode and a margin coin
/// that spot has no concept of, and spot has a cancel-and-replace where the derivatives have a true amend.
/// </para>
/// <para>
/// Everything below this line that touches a private endpoint is written from the venue's published interface and is
/// UNVERIFIED against a real account: this adapter was built without a key, and the venue checks the key before
/// anything else, so no signed request can be made to answer even the question of whether a field is named right. The
/// public half - the catalogs, the candles, the funding, the tiers, the stream - was measured against the live venue
/// and the constants that came out of that say so where they are declared. What is here is therefore held to the
/// venue's interface by tests against a recorded stub, which proves the requests this client builds and not that the
/// venue accepts them.
/// </para>
/// </summary>
public sealed class BitgetExecutionClient : ExecutionClientBase
{
    // Where an order goes. Spot and the derivatives are different paths on one host; their bodies differ as well.
    private const string SpotPlaceOrderPath = "/api/v2/spot/trade/place-order";
    private const string SpotCancelOrderPath = "/api/v2/spot/trade/cancel-order";
    private const string SpotCancelReplacePath = "/api/v2/spot/trade/cancel-replace-order";
    private const string SpotCancelSymbolOrdersPath = "/api/v2/spot/trade/cancel-symbol-order";
    private const string SpotOrderInfoPath = "/api/v2/spot/trade/orderInfo";
    private const string SpotOpenOrdersPath = "/api/v2/spot/trade/unfilled-orders";
    private const string SpotOrderHistoryPath = "/api/v2/spot/trade/history-orders";
    private const string SpotFillsPath = "/api/v2/spot/trade/fills";
    private const string SpotAssetsPath = "/api/v2/spot/account/assets";

    private const string FuturesPlaceOrderPath = "/api/v2/mix/order/place-order";
    private const string FuturesModifyOrderPath = "/api/v2/mix/order/modify-order";
    private const string FuturesCancelOrderPath = "/api/v2/mix/order/cancel-order";
    private const string FuturesCancelAllOrdersPath = "/api/v2/mix/order/cancel-all-orders";
    private const string FuturesOrderDetailPath = "/api/v2/mix/order/detail";
    private const string FuturesOpenOrdersPath = "/api/v2/mix/order/orders-pending";
    private const string FuturesOrderHistoryPath = "/api/v2/mix/order/orders-history";
    private const string FuturesFillsPath = "/api/v2/mix/order/fills";
    private const string FuturesPositionsPath = "/api/v2/mix/position/all-position";
    private const string FuturesAccountsPath = "/api/v2/mix/account/accounts";

    /// <summary>
    /// The venue's name for "every instrument of this account" in a private subscription. The private channels are
    /// subscribed once for the whole account rather than per instrument, which is what makes a node see an order it
    /// did not place.
    /// </summary>
    private const string EveryInstrument = "default";

    /// <summary>The venue's name for "every currency of this account" in the balance subscription.</summary>
    private const string EveryCoin = "default";

    /// <summary>The private channel that carries an order's life.</summary>
    private const string OrdersChannel = "orders";

    /// <summary>The private channel that carries balances.</summary>
    private const string AccountChannel = "account";

    /// <summary>The private channel that carries positions. The derivative families only.</summary>
    private const string PositionsChannel = "positions";

    /// <summary>
    /// What the websocket login signs, as the venue documents it: a timestamp in SECONDS, the method, and this path.
    /// The path is not one that exists over REST - nothing is fetched from it - so it is a constant of the login and
    /// not an endpoint.
    /// </summary>
    private const string LoginPath = "/user/verify";

    /// <summary>The method the login signature names, which is a constant of the signature and not a request.</summary>
    private const string LoginMethod = "GET";

    /// <summary>The code a successful login answers with. The venue sends it as a string rather than a number.</summary>
    private const string LoginOk = "0";

    private readonly BitgetExecutionClientConfig _config;
    private readonly BitgetHttp _http;
    private readonly BitgetInstrumentProvider _instruments;
    private readonly BitgetCredentials _credentials;
    private readonly HashSet<string> _seenTrades = new(StringComparer.Ordinal);
    private readonly Dictionary<Currency, AccountBalance> _balances = [];
    private readonly object _gate = new();
    private WebSocketClient? _ws;

    public BitgetExecutionClient(ClientId clientId, BitgetExecutionClientConfig config, KernelServices services)
        : base(
            clientId,
            BitgetVenue.Venue,
            new AccountId($"{BitgetVenue.Venue}-{BitgetVenue.Market(config)}"),
            config.ProductType == BitgetProductType.Spot ? AccountType.Cash : AccountType.Margin,
            null,
            OmsType.Netting,
            services)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        if (_config.ProductType == BitgetProductType.Spot && _config.TradingMode == BitgetTradingMode.Demo)
        {
            throw new ArgumentException(
                "Bitget has no demo spot market - it lists no S-prefixed spot pair - so a demo spot client would have "
                + "nothing to trade. Demo exists for the perpetual product types only.",
                nameof(config));
        }

        _credentials = BitgetVenue.Credentials(config);
        _http = new BitgetHttp(config, Log, requireCredentials: true, config.BrokerId);
        _instruments = new BitgetInstrumentProvider(_http, config.InstrumentProvider, Log);
    }

    public BitgetInstrumentProvider Instruments => _instruments;

    /// <summary>What the venue calls this family, which every derivative request carries as its product type.</summary>
    private string Market => _http.Market;

    private bool IsFutures => BitgetVenue.IsFutures(_config);

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

        await ApplyLeverageAsync(ct).ConfigureAwait(false);
        await PublishAccountStateAsync(ct).ConfigureAwait(false);

        _ws = new WebSocketClient(
            new WebSocketClientConfig
            {
                Url = new Uri(BitgetVenue.WsPrivate(_config)),
                PingMessage = BitgetVenue.PingMessage,
                PingInterval = BitgetVenue.PingInterval,
            },
            Log)
        {
            OnText = HandleMessageAsync,
            OnConnected = isReconnect =>
            {
                Login();
                if (isReconnect)
                {
                    // The drop marked the client disconnected and the socket reconnects by itself: say so, or the
                    // mark stays for the life of the node while orders and fills flow.
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

    /// <summary>
    /// Signs in on the private stream. The timestamp is in SECONDS here where every REST request signs one in
    /// milliseconds, which is the venue's own inconsistency and not a choice.
    /// </summary>
    private void Login()
    {
        string timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        string signature = HmacSigner.Sha256Base64(_credentials.Secret, timestamp + LoginMethod + LoginPath);
        _ws?.SendText(JsonSerializer.Serialize(new
        {
            op = "login",
            args = new[]
            {
                new
                {
                    apiKey = _credentials.Key,
                    passphrase = _credentials.Passphrase,
                    timestamp,
                    sign = signature,
                },
            },
        }));
    }

    /// <summary>
    /// The private channels this client needs: its orders, its balances, and on the derivative families its positions.
    /// <para>
    /// Fills come from the orders channel rather than from the venue's separate fill channel. One channel carrying the
    /// whole of an order's life cannot be missing while orders arrive at all, where a second channel that quietly
    /// delivered nothing would lose every fill and open no position - and the orders channel carries the fill price,
    /// the filled size, the trade id and the fee, which is everything a fill needs.
    /// </para>
    /// </summary>
    private void Subscribe()
    {
        List<object> args =
        [
            new { instType = Market, channel = OrdersChannel, instId = EveryInstrument },
            new { instType = Market, channel = AccountChannel, coin = EveryCoin },
        ];

        if (IsFutures)
        {
            args.Add(new { instType = Market, channel = PositionsChannel, instId = EveryInstrument });
        }

        _ws?.SendText(JsonSerializer.Serialize(new { op = "subscribe", args }));
    }

    // ----- the private stream -----

    private Task HandleMessageAsync(string text)
    {
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
                HandleEvent(root);
                return Task.CompletedTask;
            }

            if (!root.Has("arg") || !root.Has("data"))
            {
                return Task.CompletedTask;
            }

            string channel = root.GetProperty("arg").Str("channel");
            JsonElement data = root.GetProperty("data");
            switch (channel)
            {
                case OrdersChannel:
                    foreach (JsonElement order in data.EnumerateArray())
                    {
                        HandleOrderUpdate(order);
                    }

                    break;
                case AccountChannel:
                    HandleBalances(data, root.Has("ts") ? root.Ms("ts") : Clock.Timestamp);
                    break;
                case PositionsChannel:
                    foreach (JsonElement position in data.EnumerateArray())
                    {
                        Log.LogDebug(
                            "Bitget position {Symbol}: {Side} {Size}",
                            position.Str("instId"),
                            position.Str("holdSide"),
                            position.Str("total"));
                    }

                    break;
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            Log.LogWarning(e, "Bitget: unreadable private stream message {Text}", LogText.Truncate(text, LogText.MaxMessageLength));
        }

        return Task.CompletedTask;
    }

    private void HandleEvent(JsonElement root)
    {
        string name = root.Str("event");
        if (!string.Equals(name, "login", StringComparison.Ordinal))
        {
            if (string.Equals(name, "error", StringComparison.Ordinal))
            {
                Log.LogError("Bitget private stream refused {Op} with {Code}: {Message}", root.Str("op"), root.Str("code"), root.Str("msg"));
            }

            return;
        }

        string code = root.Str("code");
        if (code.Length == 0 || string.Equals(code, LoginOk, StringComparison.Ordinal))
        {
            Subscribe();
            return;
        }

        // Nothing can be traded from here on, and the socket stays open, so this has to be loud: a node whose private
        // stream never authenticated places orders it is never told the fate of.
        Log.LogError("Bitget private stream login failed with {Code}: {Message}", code, root.Str("msg"));
    }

    /// <summary>
    /// An order's life. The venue pushes the whole order on every change, with the increment of this change beside it -
    /// the trade id, the price and the size of the fill that caused it - so a status of partially filled or filled is
    /// both a state and a fill.
    /// <para>
    /// The trade id is what stops a fill being booked twice. The venue repeats an order's state on a reconnection and
    /// on a subscription, so without it a resubscribe would double every position.
    /// </para>
    /// </summary>
    private void HandleOrderUpdate(JsonElement o)
    {
        string clientOid = o.Str("clientOid");
        if (clientOid.Length == 0)
        {
            return;
        }

        ClientOrderId clientOrderId = new(clientOid);
        Order? order = Services.Cache.Order(clientOrderId);
        InstrumentId instrumentId = order?.InstrumentId ?? _http.ToInstrumentId(o.Str("instId"));
        Instrument? instrument = Find(instrumentId);
        if (instrument is null)
        {
            return;
        }

        StrategyId strategyId = order?.StrategyId ?? StrategyId.External;
        VenueOrderId venueOrderId = new(o.Str("orderId"));
        UnixNanos ts = o.Has("uTime") ? o.Ms("uTime") : o.Has("cTime") ? o.Ms("cTime") : Clock.Timestamp;
        string status = o.Str("status");

        if (IsFill(status))
        {
            HandleFill(o, order, strategyId, instrument, clientOrderId, venueOrderId, ts);
            return;
        }

        switch (status)
        {
            case "live":
            case "new":
                GenerateOrderAccepted(strategyId, instrumentId, clientOrderId, venueOrderId, ts);
                break;

            // The venue spells this with one L on one market and two on the other, so both are read rather than
            // whichever one happened to be tried first.
            case "cancelled":
            case "canceled":
                GenerateOrderCanceled(strategyId, instrumentId, clientOrderId, venueOrderId, ts);
                break;
            case "rejected":
                GenerateOrderRejected(strategyId, instrumentId, clientOrderId, o.Str("msg"), ts);
                break;
        }
    }

    private static bool IsFill(string status) =>
        string.Equals(status, "partially_filled", StringComparison.Ordinal)
        || string.Equals(status, "filled", StringComparison.Ordinal)
        || string.Equals(status, "partial_fill", StringComparison.Ordinal)
        || string.Equals(status, "full_fill", StringComparison.Ordinal);

    private void HandleFill(JsonElement o, Order? order, StrategyId strategyId, Instrument instrument, ClientOrderId clientOrderId, VenueOrderId venueOrderId, UnixNanos ts)
    {
        string tradeId = o.Str("tradeId");
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

        decimal size = o.Dec("baseVolume");
        decimal price = o.Dec("fillPrice");
        if (size <= 0m || price <= 0m)
        {
            // A state change with no increment on it - the venue closing an order that filled in an earlier push -
            // which is not a fill and must not be booked as one.
            return;
        }

        Quantity filled = instrument.MakeQuantity(size);
        Price at = instrument.MakePrice(price);
        LiquiditySide liquidity = string.Equals(o.Str("tradeScope"), "maker", StringComparison.OrdinalIgnoreCase)
            ? LiquiditySide.Maker
            : LiquiditySide.Taker;

        // The venue's own fee when it sends one, and the instrument's rate when it does not. A fill report read back
        // over REST carries the venue's figure and reconciliation replaces this with it.
        Currency feeCurrency = o.Str("fillFeeCoin") is { Length: > 0 } coin ? Currency.FromCode(coin) : instrument.QuoteCurrency;
        Money commission = o.Has("fillFee")
            ? new Money(Math.Abs(o.Dec("fillFee")), feeCurrency)
            : instrument.CalculateCommission(filled, at, liquidity);

        GenerateOrderFilled(
            strategyId,
            instrument.Id,
            clientOrderId,
            venueOrderId,
            null,
            new TradeId(tradeId.Length > 0 ? tradeId : Guid.NewGuid().ToString("N")),
            string.Equals(o.Str("side"), "buy", StringComparison.OrdinalIgnoreCase) ? OrderSide.Buy : OrderSide.Sell,
            order?.Type ?? MapOrderType(o.Str("orderType")),
            filled,
            at,
            instrument.QuoteCurrency,
            commission,
            liquidity,
            ts);
    }

    /// <summary>
    /// Balances. Spot names a currency <c>coin</c> and the derivatives name it <c>marginCoin</c>, and the derivative
    /// rows carry an equity that includes unrealised profit where a spot row carries only what is held.
    /// </summary>
    private void HandleBalances(JsonElement data, UnixNanos ts)
    {
        if (data.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        List<AccountBalance> all;
        lock (_gate)
        {
            foreach (JsonElement row in data.EnumerateArray())
            {
                if (Balance(row) is { } balance)
                {
                    _balances[balance.Total.Currency] = balance;
                }
            }

            all = [.. _balances.Values];
        }

        if (all.Count > 0)
        {
            GenerateAccountState(all, [], reported: true, ts);
        }
    }

    private static AccountBalance? Balance(JsonElement row)
    {
        string code = row.Str("coin") is { Length: > 0 } coin ? coin : row.Str("marginCoin");
        if (code.Length == 0)
        {
            return null;
        }

        Currency currency = Currency.FromCode(code);
        decimal available = row.Dec("available");

        // The derivative account reports equity, which already includes what is locked behind positions; the spot
        // account reports what is free and what is frozen and locked separately.
        decimal total = row.Has("accountEquity") ? row.Dec("accountEquity")
            : row.Has("equity") ? row.Dec("equity")
            : available + row.Dec("frozen") + row.Dec("locked");

        return AccountBalance.Of(new Money(total, currency), new Money(Math.Max(0m, total - available), currency));
    }

    private async Task PublishAccountStateAsync(CancellationToken ct)
    {
        try
        {
            JsonElement data = IsFutures
                ? await _http.GetSignedAsync(FuturesAccountsPath, _http.Query(), ct).ConfigureAwait(false)
                : await _http.GetSignedAsync(SpotAssetsPath, null, ct).ConfigureAwait(false);

            HandleBalances(data, Clock.Timestamp);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.LogError(e, "Failed to load Bitget {Market} account state", Market);
        }
    }

    /// <summary>
    /// Sets the configured leverage at the venue, per contract, before anything is traded.
    /// <para>
    /// On this venue leverage is account state per contract and an order carries none, so a strategy written for 3x
    /// would otherwise be traded at whatever the account was last left on - and would backtest and paper at 3x while
    /// going live at something else, with nothing saying so.
    /// </para>
    /// <para>
    /// The venue takes leverage PER SIDE. The <c>holdSide</c> parameter names the long or the short book, and the two
    /// modes treat it differently: crossed margin holds one figure for a contract and takes no side, while isolated
    /// margin holds one per side. The engine's configuration carries ONE figure, so an isolated account is set to that
    /// figure on both sides - which means asymmetric leverage cannot be expressed here at all, and saying so is better
    /// than adding a second field nothing else in the engine has.
    /// </para>
    /// <para>
    /// A refusal is logged and does not stop the node: it is usually the venue saying the account is not entitled to
    /// the figure asked for, which a node cannot fix and a person needs to read.
    /// </para>
    /// </summary>
    private async Task ApplyLeverageAsync(CancellationToken ct)
    {
        if (_config.Leverage is not { } leverage || !IsFutures)
        {
            return;
        }

        // Before anything is sent. A venue asked for more leverage than it grants does not refuse - it grants
        // what it will and trades on, so the strategy would run at a size it was never tested at.
        LeverageGuard.EnsureGranted(leverage, Services.Cache.Instruments(Venue).Concat(_instruments.GetAll()).DistinctBy(i => i.Id), BitgetVenue.Venue.Value);

        string value = Json.Fmt(leverage);
        IReadOnlyList<Instrument> tradable =
            [.. Services.Cache.Instruments(Venue).Concat(_instruments.GetAll()).DistinctBy(i => i.Id)];

        foreach (Instrument instrument in tradable)
        {
            // Nothing, one side, then the other: crossed margin takes no side and refuses one, isolated margin holds
            // a figure per side and has to be told both.
            string?[] sides = _config.MarginMode == BitgetMarginMode.Isolated ? [LongSide, ShortSide] : [null];
            foreach (string? side in sides)
            {
                Dictionary<string, object> body = new(StringComparer.Ordinal)
                {
                    ["symbol"] = instrument.RawSymbol!.Value,
                    ["productType"] = Market,
                    ["marginCoin"] = instrument.SettlementCurrency.Code,
                    ["leverage"] = value,
                };

                if (side is not null)
                {
                    body["holdSide"] = side;
                }

                try
                {
                    await _http.PostSignedAsync(BitgetVenue.LeveragePath, body, ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Log.LogError(
                        e,
                        "Bitget refused {Leverage}x on {Instrument}{Side}; it will trade at whatever the account is set to",
                        leverage,
                        instrument.Id,
                        side is null ? string.Empty : " " + side);
                }
            }
        }
    }

    /// <summary>The venue's name for the long book, which an isolated-margin leverage is set on separately.</summary>
    private const string LongSide = "long";

    /// <summary>The venue's name for the short book.</summary>
    private const string ShortSide = "short";

    // ----- helpers -----

    private Instrument? Find(InstrumentId id) => _instruments.Find(id) ?? Services.Cache.Instrument(id);

    private string Raw(InstrumentId id) => _http.ToRawSymbol(id);

    /// <summary>What the venue calls the margin mode this client trades in.</summary>
    private string MarginMode => _config.MarginMode == BitgetMarginMode.Isolated ? "isolated" : "crossed";

    // ----- commands -----

    public override async Task SubmitOrderAsync(SubmitOrder command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        Order order = command.Order;
        Instrument? instrument = Find(order.InstrumentId);
        if (instrument is null)
        {
            GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, $"instrument {order.InstrumentId} unknown to the Bitget {Market} client", Clock.Timestamp);
            return;
        }

        if (order.Type is not (OrderType.Market or OrderType.Limit))
        {
            // The venue takes a trigger order through a separate family of endpoints - its plan orders - with their
            // own cancel, their own amend and their own stream channel. None of that could be checked against a real
            // account while this was written, and a stop that is placed and cannot be cancelled is worse than one
            // that was never accepted, so it is refused here and the caller is told which order types do work.
            GenerateOrderRejected(
                order.StrategyId,
                order.InstrumentId,
                order.ClientOrderId,
                $"order type {order.Type} is not supported by the Bitget {Market} client; it sends market and limit orders",
                Clock.Timestamp);
            return;
        }

        if (order.IsQuoteQuantity && (IsFutures || order.Type != OrderType.Market || order.IsSell))
        {
            GenerateOrderRejected(
                order.StrategyId,
                order.InstrumentId,
                order.ClientOrderId,
                $"a quote quantity is only accepted on a Bitget spot market buy, and this is a {Market} {order.Type} {(order.IsBuy ? "buy" : "sell")}",
                Clock.Timestamp);
            return;
        }

        if (!IsFutures && order.Type == OrderType.Market && order.IsBuy && !order.IsQuoteQuantity)
        {
            // The venue sizes a spot market BUY in the quote currency - the same shape Binance has - and offers no
            // field that would take a base amount instead. Converting one would need a price this client does not
            // have and would fill a different size than was asked for, so it is refused and named.
            GenerateOrderRejected(
                order.StrategyId,
                order.InstrumentId,
                order.ClientOrderId,
                "Bitget sizes a spot market buy in the quote currency, so this order has to carry a quote quantity",
                Clock.Timestamp);
            return;
        }

        GenerateOrderSubmitted(order.StrategyId, order.InstrumentId, order.ClientOrderId, Clock.Timestamp);
        try
        {
            await _http.PostSignedAsync(IsFutures ? FuturesPlaceOrderPath : SpotPlaceOrderPath, Body(order, instrument), ct).ConfigureAwait(false);
        }
        catch (BitgetApiException e)
        {
            GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, e.Msg.Length > 0 ? e.Msg : e.Message, Clock.Timestamp);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            GenerateOrderRejected(order.StrategyId, order.InstrumentId, order.ClientOrderId, e.Message, Clock.Timestamp);
        }
    }

    /// <summary>
    /// The body of an order. Four fields the derivative endpoint demands that spot has no concept of - the product
    /// type, the margin mode, the margin coin and reduce-only - and one field that means different things on the two:
    /// <c>size</c> is a base amount everywhere except a spot market buy, where the venue takes the quote amount.
    /// </summary>
    private Dictionary<string, object> Body(Order order, Instrument instrument)
    {
        Dictionary<string, object> body = new(StringComparer.Ordinal)
        {
            ["symbol"] = instrument.RawSymbol!.Value,
            ["side"] = order.IsBuy ? "buy" : "sell",
            ["orderType"] = order.Type == OrderType.Limit ? "limit" : "market",
            ["size"] = Json.Fmt(order.Quantity.Value),
            ["clientOid"] = order.ClientOrderId.Value,

            // The venue's word for time in force. A market order is filled or gone, so the only one that changes
            // anything is on a limit order - but the field is required on both.
            ["force"] = Force(order),
        };

        if (order.Type == OrderType.Limit)
        {
            body["price"] = Json.Fmt(order.Price!.Value.Value);
        }

        if (!IsFutures)
        {
            return body;
        }

        body["productType"] = Market;
        body["marginMode"] = MarginMode;
        body["marginCoin"] = instrument.SettlementCurrency.Code;
        if (order.IsReduceOnly)
        {
            body["reduceOnly"] = "YES";
        }

        return body;
    }

    /// <summary>
    /// What the venue calls a time in force. Post-only is one of the values rather than a flag beside them, so an
    /// order that is both post-only and immediate-or-cancel cannot be expressed - and immediate wins, because that is
    /// the instruction that changes whether the order rests at all.
    /// </summary>
    private static string Force(Order order) => order.TimeInForce switch
    {
        TimeInForce.Ioc => "ioc",
        TimeInForce.Fok => "fok",
        _ => order.IsPostOnly ? "post_only" : "gtc",
    };

    /// <summary>
    /// Changes an order that is already placed.
    /// <para>
    /// The two markets do this differently and both are honoured. The derivative endpoint amends in place, keeping the
    /// order and its queue position. Spot has no amend and a cancel-and-replace instead, which is the venue's own
    /// endpoint rather than two requests from here: it takes the order to cancel and the replacement together, so
    /// there is no window in which neither exists.
    /// </para>
    /// <para>
    /// Spot's replacement is a NEW order at the venue and needs a new client order id, which the venue insists on. The
    /// engine's id is reused with a marker appended, so the order the venue then reports is still the engine's order.
    /// </para>
    /// </summary>
    public override async Task ModifyOrderAsync(ModifyOrder command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        Instrument? instrument = Find(command.InstrumentId);
        if (instrument is null)
        {
            GenerateOrderModifyRejected(
                command.StrategyId,
                command.InstrumentId,
                command.ClientOrderId,
                command.VenueOrderId,
                $"instrument {command.InstrumentId} unknown to the Bitget {Market} client",
                Clock.Timestamp);
            return;
        }

        Dictionary<string, object> body = new(StringComparer.Ordinal)
        {
            ["symbol"] = instrument.RawSymbol!.Value,
            ["clientOid"] = command.ClientOrderId.Value,
        };

        if (IsFutures)
        {
            body["productType"] = Market;
            body["newClientOid"] = command.ClientOrderId.Value;
            if (command.Quantity is { } size)
            {
                body["newSize"] = Json.Fmt(size.Value);
            }

            if (command.Price is { } price)
            {
                body["newPrice"] = Json.Fmt(price.Value);
            }
        }
        else
        {
            // The venue will not take a replacement under the id it is cancelling, so the engine's id carries a
            // marker. Everything that later names this order translates it back.
            body["newClientOid"] = command.ClientOrderId.Value + ReplacementSuffix;
            if (command.Quantity is { } size)
            {
                body["size"] = Json.Fmt(size.Value);
            }

            if (command.Price is { } price)
            {
                body["price"] = Json.Fmt(price.Value);
            }
        }

        GenerateOrderPendingUpdate(command.StrategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, Clock.Timestamp);
        try
        {
            await _http.PostSignedAsync(IsFutures ? FuturesModifyOrderPath : SpotCancelReplacePath, body, ct).ConfigureAwait(false);
        }
        catch (BitgetApiException e)
        {
            GenerateOrderModifyRejected(command.StrategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, e.Msg.Length > 0 ? e.Msg : e.Message, Clock.Timestamp);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            GenerateOrderModifyRejected(command.StrategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, e.Message, Clock.Timestamp);
        }
    }

    /// <summary>
    /// What is appended to a client order id when a spot order is replaced, because the venue refuses a replacement
    /// that reuses the id being cancelled. It is read back off an id so that the engine's order is still found.
    /// </summary>
    public const string ReplacementSuffix = "-R";

    public override async Task CancelOrderAsync(CancelOrder command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        Dictionary<string, object> body = new(StringComparer.Ordinal)
        {
            ["symbol"] = Raw(command.InstrumentId),
            ["clientOid"] = command.ClientOrderId.Value,
        };

        if (IsFutures)
        {
            body["productType"] = Market;
            if (Find(command.InstrumentId) is { } instrument)
            {
                body["marginCoin"] = instrument.SettlementCurrency.Code;
            }
        }

        GenerateOrderPendingCancel(command.StrategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, Clock.Timestamp);
        try
        {
            await _http.PostSignedAsync(IsFutures ? FuturesCancelOrderPath : SpotCancelOrderPath, body, ct).ConfigureAwait(false);
        }
        catch (BitgetApiException e)
        {
            GenerateOrderCancelRejected(command.StrategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, e.Msg.Length > 0 ? e.Msg : e.Message, Clock.Timestamp);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            GenerateOrderCancelRejected(command.StrategyId, command.InstrumentId, command.ClientOrderId, command.VenueOrderId, e.Message, Clock.Timestamp);
        }
    }

    public override async Task CancelAllOrdersAsync(CancelAllOrders command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.OrderSide is not null)
        {
            // The venue cancels every order of a symbol or none of them, so a one-sided cancel is done the base
            // class's way: one cancel per order the engine holds.
            await base.CancelAllOrdersAsync(command, ct).ConfigureAwait(false);
            return;
        }

        Dictionary<string, object> body = new(StringComparer.Ordinal) { ["symbol"] = Raw(command.InstrumentId) };
        if (IsFutures)
        {
            body["productType"] = Market;
            if (Find(command.InstrumentId) is { } instrument)
            {
                body["marginCoin"] = instrument.SettlementCurrency.Code;
            }
        }

        try
        {
            await _http.PostSignedAsync(IsFutures ? FuturesCancelAllOrdersPath : SpotCancelSymbolOrdersPath, body, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.LogError(e, "Bitget cancel-all failed for {Instrument}", command.InstrumentId);
        }
    }

    // ----- reports -----

    public override async Task<ExecutionMassStatus?> GenerateMassStatusAsync(UnixNanos? since, CancellationToken ct)
    {
        IReadOnlyList<OrderStatusReport> orders = await GenerateOrderStatusReportsAsync(null, since, null, openOnly: true, ct).ConfigureAwait(false);
        IReadOnlyList<FillReport> fills = await GenerateFillReportsAsync(null, null, since, null, ct).ConfigureAwait(false);
        IReadOnlyList<PositionStatusReport> positions = await GeneratePositionStatusReportsAsync(null, null, null, ct).ConfigureAwait(false);
        return new ExecutionMassStatus(ClientId, AccountId, Venue, orders, fills, positions, Clock.Timestamp, Guid.NewGuid());
    }

    public override async Task<OrderStatusReport?> GenerateOrderStatusReportAsync(InstrumentId instrumentId, ClientOrderId? clientOrderId, VenueOrderId? venueOrderId, CancellationToken ct)
    {
        List<(string Name, string Value)> parameters = [("symbol", Raw(instrumentId))];
        if (clientOrderId is { } c)
        {
            parameters.Add(("clientOid", c.Value));
        }
        else if (venueOrderId is { } v)
        {
            parameters.Add(("orderId", v.Value));
        }

        try
        {
            JsonElement data = await _http
                .GetSignedAsync(IsFutures ? FuturesOrderDetailPath : SpotOrderInfoPath, _http.Query([.. parameters]), ct)
                .ConfigureAwait(false);

            foreach (JsonElement o in Items(data))
            {
                if (ParseOrder(o) is { } report)
                {
                    return report;
                }
            }
        }
        catch (BitgetApiException e) when (BitgetVenue.ErrorsThatMeanNoSuchInstrument.Contains(e.Code))
        {
            return null;
        }

        return null;
    }

    public override async Task<IReadOnlyList<OrderStatusReport>> GenerateOrderStatusReportsAsync(InstrumentId? instrumentId, UnixNanos? start, UnixNanos? end, bool openOnly, CancellationToken ct)
    {
        List<(string Name, string Value)> parameters = [("limit", ReportPage.ToString(CultureInfo.InvariantCulture))];
        if (instrumentId is { } id)
        {
            parameters.Add(("symbol", Raw(id)));
        }

        if (start is { } s)
        {
            parameters.Add(("startTime", s.ToMilliseconds().ToString(CultureInfo.InvariantCulture)));
        }

        if (end is { } e)
        {
            parameters.Add(("endTime", e.ToMilliseconds().ToString(CultureInfo.InvariantCulture)));
        }

        string path = (IsFutures, openOnly) switch
        {
            (true, true) => FuturesOpenOrdersPath,
            (true, false) => FuturesOrderHistoryPath,
            (false, true) => SpotOpenOrdersPath,
            (false, false) => SpotOrderHistoryPath,
        };

        JsonElement data = await _http.GetSignedAsync(path, _http.Query([.. parameters]), ct).ConfigureAwait(false);
        List<OrderStatusReport> reports = [];
        foreach (JsonElement o in Items(data))
        {
            if (ParseOrder(o) is { } report)
            {
                reports.Add(report);
            }
        }

        return reports;
    }

    public override async Task<IReadOnlyList<FillReport>> GenerateFillReportsAsync(InstrumentId? instrumentId, VenueOrderId? venueOrderId, UnixNanos? start, UnixNanos? end, CancellationToken ct)
    {
        List<(string Name, string Value)> parameters = [("limit", ReportPage.ToString(CultureInfo.InvariantCulture))];
        if (instrumentId is { } id)
        {
            parameters.Add(("symbol", Raw(id)));
        }

        if (venueOrderId is { } v)
        {
            parameters.Add(("orderId", v.Value));
        }

        if (start is { } s)
        {
            parameters.Add(("startTime", s.ToMilliseconds().ToString(CultureInfo.InvariantCulture)));
        }

        if (end is { } e)
        {
            parameters.Add(("endTime", e.ToMilliseconds().ToString(CultureInfo.InvariantCulture)));
        }

        JsonElement data = await _http.GetSignedAsync(IsFutures ? FuturesFillsPath : SpotFillsPath, _http.Query([.. parameters]), ct).ConfigureAwait(false);
        List<FillReport> fills = [];
        foreach (JsonElement x in Items(data))
        {
            Instrument? instrument = Find(_http.ToInstrumentId(x.Str("symbol")));
            if (instrument is null)
            {
                continue;
            }

            // Spot reports a fill's price as an average and its size in base currency; the derivatives name the two
            // fields differently and mean the same thing.
            decimal price = x.Has("priceAvg") ? x.Dec("priceAvg") : x.Dec("price");
            decimal size = x.Has("baseVolume") ? x.Dec("baseVolume") : x.Dec("size");
            (decimal fee, Currency feeCurrency) = Fee(x, instrument);

            fills.Add(new FillReport(
                AccountId,
                instrument.Id,
                new VenueOrderId(x.Str("orderId")),
                new TradeId(x.Str("tradeId")),
                string.Equals(x.Str("side"), "buy", StringComparison.OrdinalIgnoreCase) ? OrderSide.Buy : OrderSide.Sell,
                instrument.MakeQuantity(size),
                instrument.MakePrice(price),
                new Money(fee, feeCurrency),
                string.Equals(x.Str("tradeScope"), "maker", StringComparison.OrdinalIgnoreCase) ? LiquiditySide.Maker : LiquiditySide.Taker,
                x.Ms("cTime"),
                Clock.Timestamp,
                Guid.NewGuid(),
                null));
        }

        return fills;
    }

    /// <summary>
    /// What a fill cost. The venue puts it in a <c>feeDetail</c> that is an OBJECT on spot and an ARRAY on the
    /// derivatives, and reports it as a negative number - a fee is something taken away - so it is read from either
    /// shape and turned positive, because a commission is a cost everywhere above the adapter.
    /// </summary>
    private static (decimal Fee, Currency Currency) Fee(JsonElement x, Instrument instrument)
    {
        if (!x.Has("feeDetail"))
        {
            return (0m, instrument.QuoteCurrency);
        }

        JsonElement detail = x.GetProperty("feeDetail");
        JsonElement row = detail.ValueKind == JsonValueKind.Array
            ? detail.GetArrayLength() > 0 ? detail[0] : default
            : detail;

        if (row.ValueKind != JsonValueKind.Object)
        {
            return (0m, instrument.QuoteCurrency);
        }

        decimal fee = row.Has("totalFee") ? row.Dec("totalFee") : row.Dec("fee");
        string code = row.Str("feeCoin");
        return (Math.Abs(fee), code.Length > 0 ? Currency.FromCode(code) : instrument.QuoteCurrency);
    }

    /// <summary>
    /// What the account holds, which a spot account never has. The venue reports a position's side as a word and its
    /// size as a positive number, so nothing has to read a sign.
    /// </summary>
    public override async Task<IReadOnlyList<PositionStatusReport>> GeneratePositionStatusReportsAsync(InstrumentId? instrumentId, UnixNanos? start, UnixNanos? end, CancellationToken ct)
    {
        if (!IsFutures)
        {
            return [];
        }

        JsonElement data = await _http.GetSignedAsync(FuturesPositionsPath, _http.Query(), ct).ConfigureAwait(false);
        List<PositionStatusReport> reports = [];
        foreach (JsonElement p in Items(data))
        {
            Instrument? instrument = Find(_http.ToInstrumentId(p.Str("symbol")));
            if (instrument is null || (instrumentId is { } wanted && instrument.Id != wanted))
            {
                continue;
            }

            decimal total = p.Dec("total");
            bool isLong = string.Equals(p.Str("holdSide"), LongSide, StringComparison.OrdinalIgnoreCase);
            decimal average = p.Dec("openPriceAvg");

            reports.Add(new PositionStatusReport(
                AccountId,
                instrument.Id,

                // Flat is still a position the venue lists, and reporting it keeps reconciliation able to close one
                // this node thinks is open.
                total == 0m ? PositionSide.Flat : isLong ? PositionSide.Long : PositionSide.Short,
                instrument.MakeQuantity(Math.Abs(total)),
                p.Has("uTime") ? p.Ms("uTime") : Clock.Timestamp,
                Clock.Timestamp,
                Guid.NewGuid(),
                null,
                average > 0m ? average : null));
        }

        return reports;
    }

    /// <summary>
    /// How many rows a report asks the venue for. The venue's own maximum for the order and fill endpoints, which is
    /// the same on both markets.
    /// </summary>
    private const int ReportPage = 100;

    /// <summary>
    /// The rows of an answer. Spot answers these endpoints with a bare array and the derivatives wrap theirs in an
    /// object with the list under a name of its own and a cursor beside it - two names, one for orders and one for
    /// fills - so all three shapes are unwrapped here rather than at each call.
    /// </summary>
    private static IEnumerable<JsonElement> Items(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in data.EnumerateArray())
            {
                yield return item;
            }

            yield break;
        }

        if (data.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (string name in new[] { "entrustedList", "fillList" })
        {
            if (data.TryGetProperty(name, out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in list.EnumerateArray())
                {
                    yield return item;
                }

                yield break;
            }
        }

        // A single order, which is what the detail endpoints answer with.
        if (data.Has("orderId"))
        {
            yield return data;
        }
    }

    private OrderStatusReport? ParseOrder(JsonElement o)
    {
        Instrument? instrument = Find(_http.ToInstrumentId(o.Str("symbol")));
        if (instrument is null)
        {
            return null;
        }

        string clientOid = o.Str("clientOid");
        decimal price = o.Dec("price");
        decimal size = o.Dec("size");
        decimal filled = o.Has("baseVolume") ? o.Dec("baseVolume") : o.Dec("accBaseVolume");
        decimal average = o.Dec("priceAvg");
        UnixNanos created = o.Has("cTime") ? o.Ms("cTime") : Clock.Timestamp;
        UnixNanos updated = o.Has("uTime") ? o.Ms("uTime") : created;
        string force = o.Str("force");

        return new OrderStatusReport(
            AccountId,
            instrument.Id,

            // A spot order that was replaced carries the marker this client appended, and the engine's order is the
            // one without it.
            clientOid.Length == 0 ? null : new ClientOrderId(Engine(clientOid)),
            new VenueOrderId(o.Str("orderId")),
            string.Equals(o.Str("side"), "buy", StringComparison.OrdinalIgnoreCase) ? OrderSide.Buy : OrderSide.Sell,
            MapOrderType(o.Str("orderType")),
            MapTimeInForce(force),
            MapStatus(o.Str("status")),
            instrument.MakeQuantity(size),
            instrument.MakeQuantity(filled),
            created,
            updated,
            Clock.Timestamp,
            Guid.NewGuid(),
            price > 0m ? instrument.MakePrice(price) : null,
            null,
            TriggerType.Default,
            null,
            TrailingOffsetType.Price,
            null,
            average > 0m ? average : null,
            string.Equals(force, "post_only", StringComparison.OrdinalIgnoreCase),
            string.Equals(o.Str("reduceOnly"), "YES", StringComparison.OrdinalIgnoreCase),
            null,
            null,
            ContingencyType.None,
            null);
    }

    /// <summary>The engine's own client order id behind one this client gave the venue for a replacement.</summary>
    private static string Engine(string clientOid) =>
        clientOid.EndsWith(ReplacementSuffix, StringComparison.Ordinal) ? clientOid[..^ReplacementSuffix.Length] : clientOid;

    private static OrderType MapOrderType(string orderType) =>
        string.Equals(orderType, "limit", StringComparison.OrdinalIgnoreCase) ? OrderType.Limit : OrderType.Market;

    private static TimeInForce MapTimeInForce(string force) => force.ToUpperInvariant() switch
    {
        "IOC" => TimeInForce.Ioc,
        "FOK" => TimeInForce.Fok,
        _ => TimeInForce.Gtc,
    };

    private static OrderStatus MapStatus(string status) => status switch
    {
        "live" or "new" or "init" => OrderStatus.Accepted,
        "partially_filled" or "partial_fill" => OrderStatus.PartiallyFilled,
        "filled" or "full_fill" => OrderStatus.Filled,
        "cancelled" or "canceled" => OrderStatus.Canceled,
        "rejected" => OrderStatus.Rejected,
        _ => OrderStatus.Accepted,
    };
}

public sealed class BitgetExecutionClientFactory : IExecutionClientFactory
{
    public string Name => "BITGET";

    public Type ConfigType => typeof(BitgetExecutionClientConfig);

    public IExecutionClient Create(ClientId clientId, ExecutionClientConfig config, KernelServices services) =>
        new BitgetExecutionClient(clientId, (BitgetExecutionClientConfig)config, services);
}

public sealed class BitgetPlugin : Core.Plugins.IPlugin, Core.Adapters.IVenuePlugin
{
    public string Id => "bytex.bitget";

    /// <summary>
    /// Bitget as three families behind one host and one socket. Nothing about the address changes with the family and
    /// nothing about the path does either on the socket: what selects a family is the instrument type a subscription
    /// names and the product type a REST request carries, which is why the families here differ in their symbols,
    /// their fees, their funding and their configuration rather than in where to ask.
    /// <para>
    /// The venue's fourth product type, its coin-margined futures, is not a family. Its contract list answers with an
    /// empty array - measured - so there is nothing in it to declare, and declaring a family a host could offer and
    /// find empty would be worse than not declaring it.
    /// </para>
    /// <para>
    /// The two perpetual families both hold swaps, which is the one thing about this declaration that a reader should
    /// not take for granted: <c>FamilyFor</c> answers "which family handles a swap here" with the first of them, and
    /// the USDT family is written first because it holds 805 contracts to the USDC family's 49. Which of them an
    /// instrument really belongs to is decided by asking, not by that order.
    /// </para>
    /// </summary>
    public Core.Adapters.VenueDescriptor Describe() => new()
    {
        Venue = BitgetVenue.Venue,
        DisplayName = "Bitget",

        // A header on the request - the channel API code its broker programme issues - so an id changes nothing about
        // the order itself and rides outside the signature.
        BrokerTag = Core.Adapters.BrokerTag.RequestHeader,
        BrokerProgramme = Core.Adapters.BrokerProgramme.Carried,
        Families =
        [
            new Core.Adapters.VenueFamily
            {
                Name = "spot",
                InstrumentClasses = [InstrumentClass.Spot],
                PaysFunding = false,
                HttpBase = BitgetVenue.DefaultHttpBase,
                WsBase = BitgetVenue.DefaultWsBase,
                Key = BitgetKey,
                Config = new Dictionary<string, string> { ["productType"] = nameof(BitgetProductType.Spot) },
                IgnoredConfig = ["accountType"],

                // What 3115 of the venue's 3169 pairs charge; the other 54 charge twice that, and every pair's own
                // rate is published on the instrument.
                DefaultFees = new Core.Adapters.VenueFees(0.001m, 0.001m),
                Capabilities = new Core.Adapters.VenueCapabilities
                {
                    LoadOneInstrument = true,
                    ListInstruments = true,
                    BarHistory = true,

                    // Spot pays no funding, so there is none to fetch.
                    FundingHistory = false,
                    MarketData = true,
                    Execution = true,

                    // Through the venue's own cancel-and-replace, which takes the cancellation and the replacement
                    // together rather than leaving a window in which neither order exists.
                    AmendOrders = true,
                },
            },
            new Core.Adapters.VenueFamily
            {
                Name = "usdt-futures",
                InstrumentClasses = [InstrumentClass.Swap],
                PaysFunding = true,
                HttpBase = BitgetVenue.DefaultHttpBase,
                WsBase = BitgetVenue.DefaultWsBase,
                Key = BitgetKey,
                Config = new Dictionary<string, string> { ["productType"] = nameof(BitgetProductType.UsdtFutures) },
                IgnoredConfig = ["accountType"],

                // Every one of the 805 contracts in this family charges these, and so does every one of the 49 in the
                // family below.
                DefaultFees = new Core.Adapters.VenueFees(0.0002m, 0.0006m),
                Capabilities = new Core.Adapters.VenueCapabilities
                {
                    LoadOneInstrument = true,
                    ListInstruments = true,
                    BarHistory = true,
                    FundingHistory = true,
                    MarketData = true,
                    Execution = true,

                    // A true amend here, keeping the order and its place in the queue.
                    AmendOrders = true,
                },
            },
            new Core.Adapters.VenueFamily
            {
                Name = "usdc-futures",
                InstrumentClasses = [InstrumentClass.Swap],
                PaysFunding = true,
                HttpBase = BitgetVenue.DefaultHttpBase,
                WsBase = BitgetVenue.DefaultWsBase,
                Key = BitgetKey,
                Config = new Dictionary<string, string> { ["productType"] = nameof(BitgetProductType.UsdcFutures) },
                IgnoredConfig = ["accountType"],
                DefaultFees = new Core.Adapters.VenueFees(0.0002m, 0.0006m),
                Capabilities = new Core.Adapters.VenueCapabilities
                {
                    LoadOneInstrument = true,
                    ListInstruments = true,
                    BarHistory = true,
                    FundingHistory = true,
                    MarketData = true,
                    Execution = true,
                    AmendOrders = true,
                },
            },
        ],
    };

    /// <summary>
    /// The same three parts on every family: one key, and a passphrase chosen when it was made that cannot be
    /// recovered afterwards. A host that offered two fields would leave a user with a key that looks complete and
    /// cannot sign anything.
    /// </summary>
    private static Core.Adapters.VenueKey BitgetKey => new()
    {
        Parts =
        [
            new Core.Adapters.VenueKeyPart("API key", BitgetVenue.EnvApiKey, Secret: false),
            new Core.Adapters.VenueKeyPart("API secret", BitgetVenue.EnvApiSecret, Secret: true),
            new Core.Adapters.VenueKeyPart("passphrase", BitgetVenue.EnvApiPassphrase, Secret: true),
        ],
    };

    public string Version => typeof(BitgetPlugin).Assembly.GetName().Version?.ToString() ?? "0";

    public void Register(Core.Plugins.IPluginRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.AddDataClientFactory(new BitgetDataClientFactory());
        registry.AddExecutionClientFactory(new BitgetExecutionClientFactory());
    }
}
