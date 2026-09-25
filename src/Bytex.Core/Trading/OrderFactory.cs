using System.Globalization;
using Bytex.Core.Model;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Timing;

namespace Bytex.Core.Trading;

/// <summary>
/// Generates unique client order identifiers: O-{yyyyMMdd}-{HHmmss}-{trader_tag}-{strategy_tag}-{count}.
/// </summary>
public sealed class ClientOrderIdGenerator
{
    private readonly TraderId _traderId;
    private readonly StrategyId _strategyId;
    private readonly IClock _clock;
    private readonly bool _useHyphens;
    private int _count;

    public ClientOrderIdGenerator(TraderId traderId, StrategyId strategyId, IClock clock, int initialCount = 0, bool useHyphens = true)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _traderId = traderId;
        _strategyId = strategyId;
        _clock = clock;
        _count = initialCount;
        _useHyphens = useHyphens;
    }

    public int Count => _count;

    public void SetCount(int count) => _count = count;

    public void Reset() => _count = 0;

    public ClientOrderId Generate()
    {
        _count++;
        string stamp = _clock.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string value = $"O-{stamp}-{_traderId.Tag}-{_strategyId.Tag}-{_count}";
        return new ClientOrderId(_useHyphens ? value : value.Replace("-", string.Empty, StringComparison.Ordinal));
    }
}

public sealed class OrderListIdGenerator
{
    private readonly TraderId _traderId;
    private readonly StrategyId _strategyId;
    private readonly IClock _clock;
    private int _count;

    public OrderListIdGenerator(TraderId traderId, StrategyId strategyId, IClock clock, int initialCount = 0)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _traderId = traderId;
        _strategyId = strategyId;
        _clock = clock;
        _count = initialCount;
    }

    public int Count => _count;

    public void SetCount(int count) => _count = count;

    public void Reset() => _count = 0;

    public OrderListId Generate()
    {
        _count++;
        string stamp = _clock.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return new OrderListId($"OL-{stamp}-{_traderId.Tag}-{_strategyId.Tag}-{_count}");
    }
}

/// <summary>
/// Builds orders with generated identifiers for one strategy.
/// </summary>
public sealed class OrderFactory
{
    private readonly ClientOrderIdGenerator _orderIds;
    private readonly OrderListIdGenerator _listIds;
    private readonly IClock _clock;

    /// <summary>
    /// <paramref name="idOwner"/> is the identity the generated ids read as, when it differs from the strategy the orders
    /// belong to (an order id tag). The orders themselves always belong to <paramref name="strategyId"/>, which is what
    /// they are routed and looked up by.
    /// </summary>
    public OrderFactory(TraderId traderId, StrategyId strategyId, IClock clock, int initialOrderCount = 0, int initialListCount = 0, bool useHyphens = true, StrategyId? idOwner = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        TraderId = traderId;
        StrategyId = strategyId;
        _clock = clock;
        StrategyId forIds = idOwner ?? strategyId;
        _orderIds = new ClientOrderIdGenerator(traderId, forIds, clock, initialOrderCount, useHyphens);
        _listIds = new OrderListIdGenerator(traderId, forIds, clock, initialListCount);
    }

    public TraderId TraderId { get; }

    public StrategyId StrategyId { get; }

    public int OrderIdCount => _orderIds.Count;

    public int ListIdCount => _listIds.Count;

    public void SetOrderIdCount(int count) => _orderIds.SetCount(count);

    public void SetListIdCount(int count) => _listIds.SetCount(count);

    public void Reset()
    {
        _orderIds.Reset();
        _listIds.Reset();
    }

    public ClientOrderId GenerateClientOrderId() => _orderIds.Generate();

    public OrderListId GenerateOrderListId() => _listIds.Generate();

    private OrderParams Params(InstrumentId instrumentId, OrderSide side, Quantity quantity, TimeInForce tif, bool postOnly, bool reduceOnly, bool quoteQuantity,
        TriggerType emulationTrigger, InstrumentId? triggerInstrumentId, ContingencyType contingency, OrderListId? listId, IReadOnlyList<ClientOrderId>? linked,
        ClientOrderId? parent, ExecAlgorithmId? execAlgorithm, IReadOnlyDictionary<string, string>? execParams, ClientOrderId? execSpawn, IReadOnlyList<string>? tags, ClientOrderId? clientOrderId) => new()
    {
        TraderId = TraderId,
        StrategyId = StrategyId,
        InstrumentId = instrumentId,
        ClientOrderId = clientOrderId ?? _orderIds.Generate(),
        Side = side,
        Quantity = quantity,
        TimeInForce = tif,
        PostOnly = postOnly,
        ReduceOnly = reduceOnly,
        QuoteQuantity = quoteQuantity,
        EmulationTrigger = emulationTrigger,
        TriggerInstrumentId = triggerInstrumentId,
        Contingency = contingency,
        OrderListId = listId,
        LinkedOrderIds = linked ?? [],
        ParentOrderId = parent,
        ExecAlgorithmId = execAlgorithm,
        ExecAlgorithmParams = execParams,
        ExecSpawnId = execSpawn,
        Tags = tags ?? [],
        InitId = Guid.NewGuid(),
        TsInit = _clock.Timestamp,
    };

    public MarketOrder Market(InstrumentId instrumentId, OrderSide side, Quantity quantity, TimeInForce timeInForce = TimeInForce.Gtc, bool reduceOnly = false,
        bool quoteQuantity = false, ExecAlgorithmId? execAlgorithmId = null, IReadOnlyDictionary<string, string>? execAlgorithmParams = null, IReadOnlyList<string>? tags = null,
        ClientOrderId? clientOrderId = null) =>
        MarketOrder.Create(Params(instrumentId, side, quantity, timeInForce, false, reduceOnly, quoteQuantity, TriggerType.Default, null, ContingencyType.None, null, null, null, execAlgorithmId, execAlgorithmParams, null, tags, clientOrderId));

    public LimitOrder Limit(InstrumentId instrumentId, OrderSide side, Quantity quantity, Price price, TimeInForce timeInForce = TimeInForce.Gtc, UnixNanos? expireTime = null,
        bool postOnly = false, bool reduceOnly = false, bool quoteQuantity = false, Quantity? displayQuantity = null, TriggerType emulationTrigger = TriggerType.Default,
        InstrumentId? triggerInstrumentId = null, ExecAlgorithmId? execAlgorithmId = null, IReadOnlyDictionary<string, string>? execAlgorithmParams = null, IReadOnlyList<string>? tags = null,
        ClientOrderId? clientOrderId = null) =>
        LimitOrder.Create(Params(instrumentId, side, quantity, timeInForce, postOnly, reduceOnly, quoteQuantity, emulationTrigger, triggerInstrumentId, ContingencyType.None, null, null, null, execAlgorithmId, execAlgorithmParams, null, tags, clientOrderId), price, expireTime, displayQuantity);

    public StopMarketOrder StopMarket(InstrumentId instrumentId, OrderSide side, Quantity quantity, Price triggerPrice, TriggerType triggerType = TriggerType.Default,
        TimeInForce timeInForce = TimeInForce.Gtc, UnixNanos? expireTime = null, bool reduceOnly = false, bool quoteQuantity = false, TriggerType emulationTrigger = TriggerType.Default,
        InstrumentId? triggerInstrumentId = null, ExecAlgorithmId? execAlgorithmId = null, IReadOnlyDictionary<string, string>? execAlgorithmParams = null, IReadOnlyList<string>? tags = null,
        ClientOrderId? clientOrderId = null) =>
        StopMarketOrder.Create(Params(instrumentId, side, quantity, timeInForce, false, reduceOnly, quoteQuantity, emulationTrigger, triggerInstrumentId, ContingencyType.None, null, null, null, execAlgorithmId, execAlgorithmParams, null, tags, clientOrderId), triggerPrice, triggerType, expireTime);

    public StopLimitOrder StopLimit(InstrumentId instrumentId, OrderSide side, Quantity quantity, Price price, Price triggerPrice, TriggerType triggerType = TriggerType.Default,
        TimeInForce timeInForce = TimeInForce.Gtc, UnixNanos? expireTime = null, bool postOnly = false, bool reduceOnly = false, bool quoteQuantity = false, Quantity? displayQuantity = null,
        TriggerType emulationTrigger = TriggerType.Default, InstrumentId? triggerInstrumentId = null, ExecAlgorithmId? execAlgorithmId = null, IReadOnlyDictionary<string, string>? execAlgorithmParams = null,
        IReadOnlyList<string>? tags = null, ClientOrderId? clientOrderId = null) =>
        StopLimitOrder.Create(Params(instrumentId, side, quantity, timeInForce, postOnly, reduceOnly, quoteQuantity, emulationTrigger, triggerInstrumentId, ContingencyType.None, null, null, null, execAlgorithmId, execAlgorithmParams, null, tags, clientOrderId), price, triggerPrice, triggerType, expireTime, displayQuantity);

    public MarketIfTouchedOrder MarketIfTouched(InstrumentId instrumentId, OrderSide side, Quantity quantity, Price triggerPrice, TriggerType triggerType = TriggerType.Default,
        TimeInForce timeInForce = TimeInForce.Gtc, UnixNanos? expireTime = null, bool reduceOnly = false, bool quoteQuantity = false, TriggerType emulationTrigger = TriggerType.Default,
        InstrumentId? triggerInstrumentId = null, ExecAlgorithmId? execAlgorithmId = null, IReadOnlyDictionary<string, string>? execAlgorithmParams = null, IReadOnlyList<string>? tags = null,
        ClientOrderId? clientOrderId = null) =>
        MarketIfTouchedOrder.Create(Params(instrumentId, side, quantity, timeInForce, false, reduceOnly, quoteQuantity, emulationTrigger, triggerInstrumentId, ContingencyType.None, null, null, null, execAlgorithmId, execAlgorithmParams, null, tags, clientOrderId), triggerPrice, triggerType, expireTime);

    public LimitIfTouchedOrder LimitIfTouched(InstrumentId instrumentId, OrderSide side, Quantity quantity, Price price, Price triggerPrice, TriggerType triggerType = TriggerType.Default,
        TimeInForce timeInForce = TimeInForce.Gtc, UnixNanos? expireTime = null, bool postOnly = false, bool reduceOnly = false, bool quoteQuantity = false, Quantity? displayQuantity = null,
        TriggerType emulationTrigger = TriggerType.Default, InstrumentId? triggerInstrumentId = null, ExecAlgorithmId? execAlgorithmId = null, IReadOnlyDictionary<string, string>? execAlgorithmParams = null,
        IReadOnlyList<string>? tags = null, ClientOrderId? clientOrderId = null) =>
        LimitIfTouchedOrder.Create(Params(instrumentId, side, quantity, timeInForce, postOnly, reduceOnly, quoteQuantity, emulationTrigger, triggerInstrumentId, ContingencyType.None, null, null, null, execAlgorithmId, execAlgorithmParams, null, tags, clientOrderId), price, triggerPrice, triggerType, expireTime, displayQuantity);

    public TrailingStopMarketOrder TrailingStopMarket(InstrumentId instrumentId, OrderSide side, Quantity quantity, decimal trailingOffset, TrailingOffsetType offsetType = TrailingOffsetType.Price,
        Price? triggerPrice = null, Price? activationPrice = null, TriggerType triggerType = TriggerType.Default, TimeInForce timeInForce = TimeInForce.Gtc, UnixNanos? expireTime = null,
        bool reduceOnly = false, bool quoteQuantity = false, TriggerType emulationTrigger = TriggerType.Default, InstrumentId? triggerInstrumentId = null, ExecAlgorithmId? execAlgorithmId = null,
        IReadOnlyDictionary<string, string>? execAlgorithmParams = null, IReadOnlyList<string>? tags = null, ClientOrderId? clientOrderId = null) =>
        TrailingStopMarketOrder.Create(Params(instrumentId, side, quantity, timeInForce, false, reduceOnly, quoteQuantity, emulationTrigger, triggerInstrumentId, ContingencyType.None, null, null, null, execAlgorithmId, execAlgorithmParams, null, tags, clientOrderId), trailingOffset, offsetType, triggerPrice, activationPrice, triggerType, expireTime);

    public TrailingStopLimitOrder TrailingStopLimit(InstrumentId instrumentId, OrderSide side, Quantity quantity, decimal trailingOffset, decimal limitOffset, TrailingOffsetType offsetType = TrailingOffsetType.Price,
        Price? price = null, Price? triggerPrice = null, Price? activationPrice = null, TriggerType triggerType = TriggerType.Default, TimeInForce timeInForce = TimeInForce.Gtc, UnixNanos? expireTime = null,
        bool postOnly = false, bool reduceOnly = false, bool quoteQuantity = false, TriggerType emulationTrigger = TriggerType.Default, InstrumentId? triggerInstrumentId = null,
        ExecAlgorithmId? execAlgorithmId = null, IReadOnlyDictionary<string, string>? execAlgorithmParams = null, IReadOnlyList<string>? tags = null, ClientOrderId? clientOrderId = null) =>
        TrailingStopLimitOrder.Create(Params(instrumentId, side, quantity, timeInForce, postOnly, reduceOnly, quoteQuantity, emulationTrigger, triggerInstrumentId, ContingencyType.None, null, null, null, execAlgorithmId, execAlgorithmParams, null, tags, clientOrderId), trailingOffset, limitOffset, offsetType, price, triggerPrice, activationPrice, triggerType, expireTime);

    public MarketToLimitOrder MarketToLimit(InstrumentId instrumentId, OrderSide side, Quantity quantity, TimeInForce timeInForce = TimeInForce.Gtc, UnixNanos? expireTime = null,
        bool reduceOnly = false, bool quoteQuantity = false, Quantity? displayQuantity = null, ExecAlgorithmId? execAlgorithmId = null, IReadOnlyDictionary<string, string>? execAlgorithmParams = null,
        IReadOnlyList<string>? tags = null, ClientOrderId? clientOrderId = null) =>
        MarketToLimitOrder.Create(Params(instrumentId, side, quantity, timeInForce, false, reduceOnly, quoteQuantity, TriggerType.Default, null, ContingencyType.None, null, null, null, execAlgorithmId, execAlgorithmParams, null, tags, clientOrderId), expireTime, displayQuantity);

    /// <summary>
    /// Creates an entry order with a linked stop-loss and take-profit (OTO entry, OCO exits).
    /// </summary>
    public OrderList BracketOrder(InstrumentId instrumentId, OrderSide side, Quantity quantity, Price stopLossTrigger, Price takeProfitPrice, Price? entryPrice = null,
        Price? entryTriggerPrice = null, OrderType entryType = OrderType.Market, OrderType stopLossType = OrderType.StopMarket, OrderType takeProfitType = OrderType.Limit,
        TimeInForce timeInForce = TimeInForce.Gtc, UnixNanos? expireTime = null, bool entryPostOnly = false, bool takeProfitPostOnly = true, bool quoteQuantity = false,
        TriggerType emulationTrigger = TriggerType.Default, ExecAlgorithmId? entryExecAlgorithmId = null, IReadOnlyDictionary<string, string>? entryExecAlgorithmParams = null,
        IReadOnlyList<string>? entryTags = null, IReadOnlyList<string>? stopLossTags = null, IReadOnlyList<string>? takeProfitTags = null)
    {
        OrderListId listId = _listIds.Generate();
        ClientOrderId entryId = _orderIds.Generate();
        ClientOrderId slId = _orderIds.Generate();
        ClientOrderId tpId = _orderIds.Generate();
        OrderSide exitSide = side.Opposite();

        OrderParams entryParams = Params(instrumentId, side, quantity, timeInForce, entryPostOnly, false, quoteQuantity, emulationTrigger, null, ContingencyType.Oto, listId, [slId, tpId], null, entryExecAlgorithmId, entryExecAlgorithmParams, null, entryTags, entryId);
        Order entry = entryType switch
        {
            OrderType.Market => MarketOrder.Create(entryParams with { TimeInForce = timeInForce == TimeInForce.Gtd ? TimeInForce.Gtc : timeInForce }),
            OrderType.Limit => LimitOrder.Create(entryParams, entryPrice ?? throw new ArgumentException("Limit entry requires a price.", nameof(entryPrice)), expireTime),
            OrderType.StopLimit => StopLimitOrder.Create(entryParams, entryPrice ?? throw new ArgumentException("Stop-limit entry requires a price.", nameof(entryPrice)), entryTriggerPrice ?? throw new ArgumentException("Stop-limit entry requires a trigger price.", nameof(entryTriggerPrice)), TriggerType.Default, expireTime),
            OrderType.LimitIfTouched => LimitIfTouchedOrder.Create(entryParams, entryPrice ?? throw new ArgumentException("LIT entry requires a price.", nameof(entryPrice)), entryTriggerPrice ?? throw new ArgumentException("LIT entry requires a trigger price.", nameof(entryTriggerPrice)), TriggerType.Default, expireTime),
            OrderType.MarketIfTouched => MarketIfTouchedOrder.Create(entryParams, entryTriggerPrice ?? throw new ArgumentException("MIT entry requires a trigger price.", nameof(entryTriggerPrice)), TriggerType.Default, expireTime),
            _ => throw new ArgumentException($"Entry order type {entryType} is not supported for bracket orders.", nameof(entryType)),
        };

        OrderParams slParams = Params(instrumentId, exitSide, quantity, timeInForce, false, true, quoteQuantity, emulationTrigger, null, ContingencyType.Ouo, listId, [tpId], entryId, null, null, null, stopLossTags, slId);
        Order stopLoss = stopLossType switch
        {
            OrderType.StopMarket => StopMarketOrder.Create(slParams, stopLossTrigger, TriggerType.Default, expireTime),
            OrderType.StopLimit => StopLimitOrder.Create(slParams, stopLossTrigger, stopLossTrigger, TriggerType.Default, expireTime),
            OrderType.TrailingStopMarket => TrailingStopMarketOrder.Create(slParams, 0m, TrailingOffsetType.Price, stopLossTrigger, null, TriggerType.Default, expireTime),
            _ => throw new ArgumentException($"Stop-loss order type {stopLossType} is not supported for bracket orders.", nameof(stopLossType)),
        };

        OrderParams tpParams = Params(instrumentId, exitSide, quantity, timeInForce, takeProfitPostOnly, true, quoteQuantity, emulationTrigger, null, ContingencyType.Ouo, listId, [slId], entryId, null, null, null, takeProfitTags, tpId);
        Order takeProfit = takeProfitType switch
        {
            OrderType.Limit => LimitOrder.Create(tpParams, takeProfitPrice, expireTime),
            OrderType.LimitIfTouched => LimitIfTouchedOrder.Create(tpParams, takeProfitPrice, takeProfitPrice, TriggerType.Default, expireTime),
            OrderType.MarketIfTouched => MarketIfTouchedOrder.Create(tpParams, takeProfitPrice, TriggerType.Default, expireTime),
            _ => throw new ArgumentException($"Take-profit order type {takeProfitType} is not supported for bracket orders.", nameof(takeProfitType)),
        };

        return new OrderList(listId, [entry, stopLoss, takeProfit]);
    }

    /// <summary>
    /// Groups independently created orders into a list with an OCO/OUO relationship.
    /// </summary>
    public OrderList CreateList(IReadOnlyList<Order> orders)
    {
        ArgumentNullException.ThrowIfNull(orders);
        return new OrderList(_listIds.Generate(), orders);
    }
}
