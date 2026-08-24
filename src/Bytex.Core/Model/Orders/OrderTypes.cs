using System.Globalization;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Primitives;

namespace Bytex.Core.Model.Orders;

/// <summary>
/// Parameters common to every order type, supplied when building the initialising event.
/// </summary>
public sealed record OrderParams
{
    public required TraderId TraderId { get; init; }

    public required StrategyId StrategyId { get; init; }

    public required InstrumentId InstrumentId { get; init; }

    public required ClientOrderId ClientOrderId { get; init; }

    public required OrderSide Side { get; init; }

    public required Quantity Quantity { get; init; }

    public TimeInForce TimeInForce { get; init; } = TimeInForce.Gtc;

    public bool PostOnly { get; init; }

    public bool ReduceOnly { get; init; }

    public bool QuoteQuantity { get; init; }

    public TriggerType EmulationTrigger { get; init; } = TriggerType.Default;

    public InstrumentId? TriggerInstrumentId { get; init; }

    public ContingencyType Contingency { get; init; } = ContingencyType.None;

    public OrderListId? OrderListId { get; init; }

    public IReadOnlyList<ClientOrderId> LinkedOrderIds { get; init; } = [];

    public ClientOrderId? ParentOrderId { get; init; }

    public ExecAlgorithmId? ExecAlgorithmId { get; init; }

    public IReadOnlyDictionary<string, string>? ExecAlgorithmParams { get; init; }

    public ClientOrderId? ExecSpawnId { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public required Guid InitId { get; init; }

    public required UnixNanos TsInit { get; init; }

    internal OrderInitialized ToInitialized(OrderType type, IReadOnlyDictionary<string, string> options) => new(
        TraderId, StrategyId, InstrumentId, ClientOrderId, Side, type, Quantity, TimeInForce, PostOnly, ReduceOnly, QuoteQuantity,
        options, EmulationTrigger, TriggerInstrumentId, Contingency, OrderListId, LinkedOrderIds, ParentOrderId, ExecAlgorithmId,
        ExecAlgorithmParams, ExecSpawnId, Tags, InitId, TsInit, TsInit);
}

internal static class OrderOptions
{
    public static Dictionary<string, string> New() => new(StringComparer.Ordinal);

    public static Price? GetPrice(IReadOnlyDictionary<string, string> options, string key) =>
        options.TryGetValue(key, out string? text) ? Price.Parse(text) : null;

    public static Quantity? GetQuantity(IReadOnlyDictionary<string, string> options, string key) =>
        options.TryGetValue(key, out string? text) ? Quantity.Parse(text) : null;

    public static decimal? GetDecimal(IReadOnlyDictionary<string, string> options, string key) =>
        options.TryGetValue(key, out string? text) ? Order.OptionKeys.ParseDecimal(text) : null;

    public static UnixNanos? GetTime(IReadOnlyDictionary<string, string> options, string key) =>
        options.TryGetValue(key, out string? text) ? new UnixNanos(long.Parse(text, CultureInfo.InvariantCulture)) : null;

    public static TEnum GetEnum<TEnum>(IReadOnlyDictionary<string, string> options, string key, TEnum fallback) where TEnum : struct, Enum =>
        options.TryGetValue(key, out string? text) && Enum.TryParse(text, out TEnum value) ? value : fallback;
}

public sealed class MarketOrder : Order
{
    public MarketOrder(OrderInitialized init) : base(init)
    {
        if (init.TimeInForce is TimeInForce.Gtd or TimeInForce.AtTheOpen or TimeInForce.AtTheClose)
        {
            throw new ArgumentException($"Market orders do not support time in force {init.TimeInForce}.", nameof(init));
        }
    }

    public static MarketOrder Create(OrderParams p) => new(p.ToInitialized(OrderType.Market, OrderOptions.New()));
}

public sealed class LimitOrder : Order
{
    public LimitOrder(OrderInitialized init) : base(init)
    {
        Price = OrderOptions.GetPrice(init.Options, OptionKeys.Price) ?? throw new ArgumentException("Limit order requires a price.", nameof(init));
        ExpireTime = OrderOptions.GetTime(init.Options, OptionKeys.ExpireTime);
        DisplayQuantity = OrderOptions.GetQuantity(init.Options, OptionKeys.DisplayQuantity);
        if (init.TimeInForce == TimeInForce.Gtd && ExpireTime is null)
        {
            throw new ArgumentException("GTD orders require an expire time.", nameof(init));
        }
    }

    public bool IsIceberg => DisplayQuantity is not null;

    protected override void OnUpdated(OrderUpdated updated)
    {
        if (updated.Price is { } price)
        {
            Price = price;
        }
    }

    public static LimitOrder Create(OrderParams p, Price price, UnixNanos? expireTime = null, Quantity? displayQuantity = null)
    {
        Dictionary<string, string> options = OrderOptions.New();
        options[OptionKeys.Price] = price.ToString();
        if (expireTime is { } exp)
        {
            options[OptionKeys.ExpireTime] = exp.Value.ToString(CultureInfo.InvariantCulture);
        }

        if (displayQuantity is { } dq)
        {
            options[OptionKeys.DisplayQuantity] = dq.ToString();
        }

        return new LimitOrder(p.ToInitialized(OrderType.Limit, options));
    }

    public override string Info() => base.Info() + $" @ {Price}";
}

public sealed class StopMarketOrder : Order
{
    public StopMarketOrder(OrderInitialized init) : base(init)
    {
        TriggerPrice = OrderOptions.GetPrice(init.Options, OptionKeys.TriggerPrice) ?? throw new ArgumentException("Stop order requires a trigger price.", nameof(init));
        TriggerType = OrderOptions.GetEnum(init.Options, OptionKeys.TriggerType, TriggerType.Default);
        ExpireTime = OrderOptions.GetTime(init.Options, OptionKeys.ExpireTime);
    }

    public bool IsTriggered { get; private set; }

    public UnixNanos? TsTriggered { get; private set; }

    protected override void OnUpdated(OrderUpdated updated)
    {
        if (updated.TriggerPrice is { } trigger)
        {
            TriggerPrice = trigger;
        }
    }

    internal void MarkTriggered(UnixNanos ts)
    {
        IsTriggered = true;
        TsTriggered = ts;
    }

    public static StopMarketOrder Create(OrderParams p, Price triggerPrice, TriggerType triggerType = TriggerType.Default, UnixNanos? expireTime = null)
    {
        Dictionary<string, string> options = OrderOptions.New();
        options[OptionKeys.TriggerPrice] = triggerPrice.ToString();
        options[OptionKeys.TriggerType] = triggerType.ToString();
        if (expireTime is { } exp)
        {
            options[OptionKeys.ExpireTime] = exp.Value.ToString(CultureInfo.InvariantCulture);
        }

        return new StopMarketOrder(p.ToInitialized(OrderType.StopMarket, options));
    }

    public override string Info() => base.Info() + $" trigger {TriggerPrice} [{TriggerType}]";
}

public sealed class StopLimitOrder : Order
{
    public StopLimitOrder(OrderInitialized init) : base(init)
    {
        Price = OrderOptions.GetPrice(init.Options, OptionKeys.Price) ?? throw new ArgumentException("Stop-limit order requires a price.", nameof(init));
        TriggerPrice = OrderOptions.GetPrice(init.Options, OptionKeys.TriggerPrice) ?? throw new ArgumentException("Stop-limit order requires a trigger price.", nameof(init));
        TriggerType = OrderOptions.GetEnum(init.Options, OptionKeys.TriggerType, TriggerType.Default);
        ExpireTime = OrderOptions.GetTime(init.Options, OptionKeys.ExpireTime);
        DisplayQuantity = OrderOptions.GetQuantity(init.Options, OptionKeys.DisplayQuantity);
    }

    public bool IsTriggered { get; private set; }

    internal void MarkTriggered() => IsTriggered = true;

    protected override void OnUpdated(OrderUpdated updated)
    {
        if (updated.Price is { } price)
        {
            Price = price;
        }

        if (updated.TriggerPrice is { } trigger)
        {
            TriggerPrice = trigger;
        }
    }

    public static StopLimitOrder Create(OrderParams p, Price price, Price triggerPrice, TriggerType triggerType = TriggerType.Default, UnixNanos? expireTime = null, Quantity? displayQuantity = null)
    {
        Dictionary<string, string> options = OrderOptions.New();
        options[OptionKeys.Price] = price.ToString();
        options[OptionKeys.TriggerPrice] = triggerPrice.ToString();
        options[OptionKeys.TriggerType] = triggerType.ToString();
        if (expireTime is { } exp)
        {
            options[OptionKeys.ExpireTime] = exp.Value.ToString(CultureInfo.InvariantCulture);
        }

        if (displayQuantity is { } dq)
        {
            options[OptionKeys.DisplayQuantity] = dq.ToString();
        }

        return new StopLimitOrder(p.ToInitialized(OrderType.StopLimit, options));
    }

    public override string Info() => base.Info() + $" @ {Price} trigger {TriggerPrice} [{TriggerType}]";
}

public sealed class MarketIfTouchedOrder : Order
{
    public MarketIfTouchedOrder(OrderInitialized init) : base(init)
    {
        TriggerPrice = OrderOptions.GetPrice(init.Options, OptionKeys.TriggerPrice) ?? throw new ArgumentException("MIT order requires a trigger price.", nameof(init));
        TriggerType = OrderOptions.GetEnum(init.Options, OptionKeys.TriggerType, TriggerType.Default);
        ExpireTime = OrderOptions.GetTime(init.Options, OptionKeys.ExpireTime);
    }

    protected override void OnUpdated(OrderUpdated updated)
    {
        if (updated.TriggerPrice is { } trigger)
        {
            TriggerPrice = trigger;
        }
    }

    public static MarketIfTouchedOrder Create(OrderParams p, Price triggerPrice, TriggerType triggerType = TriggerType.Default, UnixNanos? expireTime = null)
    {
        Dictionary<string, string> options = OrderOptions.New();
        options[OptionKeys.TriggerPrice] = triggerPrice.ToString();
        options[OptionKeys.TriggerType] = triggerType.ToString();
        if (expireTime is { } exp)
        {
            options[OptionKeys.ExpireTime] = exp.Value.ToString(CultureInfo.InvariantCulture);
        }

        return new MarketIfTouchedOrder(p.ToInitialized(OrderType.MarketIfTouched, options));
    }

    public override string Info() => base.Info() + $" trigger {TriggerPrice} [{TriggerType}]";
}

public sealed class LimitIfTouchedOrder : Order
{
    public LimitIfTouchedOrder(OrderInitialized init) : base(init)
    {
        Price = OrderOptions.GetPrice(init.Options, OptionKeys.Price) ?? throw new ArgumentException("LIT order requires a price.", nameof(init));
        TriggerPrice = OrderOptions.GetPrice(init.Options, OptionKeys.TriggerPrice) ?? throw new ArgumentException("LIT order requires a trigger price.", nameof(init));
        TriggerType = OrderOptions.GetEnum(init.Options, OptionKeys.TriggerType, TriggerType.Default);
        ExpireTime = OrderOptions.GetTime(init.Options, OptionKeys.ExpireTime);
        DisplayQuantity = OrderOptions.GetQuantity(init.Options, OptionKeys.DisplayQuantity);
    }

    protected override void OnUpdated(OrderUpdated updated)
    {
        if (updated.Price is { } price)
        {
            Price = price;
        }

        if (updated.TriggerPrice is { } trigger)
        {
            TriggerPrice = trigger;
        }
    }

    public static LimitIfTouchedOrder Create(OrderParams p, Price price, Price triggerPrice, TriggerType triggerType = TriggerType.Default, UnixNanos? expireTime = null, Quantity? displayQuantity = null)
    {
        Dictionary<string, string> options = OrderOptions.New();
        options[OptionKeys.Price] = price.ToString();
        options[OptionKeys.TriggerPrice] = triggerPrice.ToString();
        options[OptionKeys.TriggerType] = triggerType.ToString();
        if (expireTime is { } exp)
        {
            options[OptionKeys.ExpireTime] = exp.Value.ToString(CultureInfo.InvariantCulture);
        }

        if (displayQuantity is { } dq)
        {
            options[OptionKeys.DisplayQuantity] = dq.ToString();
        }

        return new LimitIfTouchedOrder(p.ToInitialized(OrderType.LimitIfTouched, options));
    }

    public override string Info() => base.Info() + $" @ {Price} trigger {TriggerPrice} [{TriggerType}]";
}

public sealed class TrailingStopMarketOrder : Order
{
    public TrailingStopMarketOrder(OrderInitialized init) : base(init)
    {
        TrailingOffset = OrderOptions.GetDecimal(init.Options, OptionKeys.TrailingOffset) ?? throw new ArgumentException("Trailing stop requires an offset.", nameof(init));
        TrailingOffsetType = OrderOptions.GetEnum(init.Options, OptionKeys.TrailingOffsetType, TrailingOffsetType.Price);
        TriggerPrice = OrderOptions.GetPrice(init.Options, OptionKeys.TriggerPrice);
        ActivationPrice = OrderOptions.GetPrice(init.Options, OptionKeys.ActivationPrice);
        TriggerType = OrderOptions.GetEnum(init.Options, OptionKeys.TriggerType, TriggerType.Default);
        ExpireTime = OrderOptions.GetTime(init.Options, OptionKeys.ExpireTime);
    }

    public decimal TrailingOffset { get; }

    public TrailingOffsetType TrailingOffsetType { get; }

    public Price? ActivationPrice { get; }

    public bool IsActivated { get; private set; }

    internal void Activate() => IsActivated = true;

    internal void SetTriggerPrice(Price price) => TriggerPrice = price;

    protected override void OnUpdated(OrderUpdated updated)
    {
        if (updated.TriggerPrice is { } trigger)
        {
            TriggerPrice = trigger;
        }
    }

    public static TrailingStopMarketOrder Create(OrderParams p, decimal trailingOffset, TrailingOffsetType offsetType = TrailingOffsetType.Price,
        Price? triggerPrice = null, Price? activationPrice = null, TriggerType triggerType = TriggerType.Default, UnixNanos? expireTime = null)
    {
        Dictionary<string, string> options = OrderOptions.New();
        options[OptionKeys.TrailingOffset] = OptionKeys.Format(trailingOffset);
        options[OptionKeys.TrailingOffsetType] = offsetType.ToString();
        options[OptionKeys.TriggerType] = triggerType.ToString();
        if (triggerPrice is { } tp)
        {
            options[OptionKeys.TriggerPrice] = tp.ToString();
        }

        if (activationPrice is { } ap)
        {
            options[OptionKeys.ActivationPrice] = ap.ToString();
        }

        if (expireTime is { } exp)
        {
            options[OptionKeys.ExpireTime] = exp.Value.ToString(CultureInfo.InvariantCulture);
        }

        return new TrailingStopMarketOrder(p.ToInitialized(OrderType.TrailingStopMarket, options));
    }

    public override string Info() => base.Info() + $" trailing {TrailingOffset} {TrailingOffsetType} trigger {TriggerPrice?.ToString() ?? "None"}";
}

public sealed class TrailingStopLimitOrder : Order
{
    public TrailingStopLimitOrder(OrderInitialized init) : base(init)
    {
        TrailingOffset = OrderOptions.GetDecimal(init.Options, OptionKeys.TrailingOffset) ?? throw new ArgumentException("Trailing stop requires an offset.", nameof(init));
        LimitOffset = OrderOptions.GetDecimal(init.Options, OptionKeys.LimitOffset) ?? 0m;
        TrailingOffsetType = OrderOptions.GetEnum(init.Options, OptionKeys.TrailingOffsetType, TrailingOffsetType.Price);
        Price = OrderOptions.GetPrice(init.Options, OptionKeys.Price);
        TriggerPrice = OrderOptions.GetPrice(init.Options, OptionKeys.TriggerPrice);
        ActivationPrice = OrderOptions.GetPrice(init.Options, OptionKeys.ActivationPrice);
        TriggerType = OrderOptions.GetEnum(init.Options, OptionKeys.TriggerType, TriggerType.Default);
        ExpireTime = OrderOptions.GetTime(init.Options, OptionKeys.ExpireTime);
    }

    public decimal TrailingOffset { get; }

    public decimal LimitOffset { get; }

    public TrailingOffsetType TrailingOffsetType { get; }

    public Price? ActivationPrice { get; }

    public bool IsActivated { get; private set; }

    internal void Activate() => IsActivated = true;

    internal void SetPrices(Price triggerPrice, Price price)
    {
        TriggerPrice = triggerPrice;
        Price = price;
    }

    protected override void OnUpdated(OrderUpdated updated)
    {
        if (updated.Price is { } price)
        {
            Price = price;
        }

        if (updated.TriggerPrice is { } trigger)
        {
            TriggerPrice = trigger;
        }
    }

    public static TrailingStopLimitOrder Create(OrderParams p, decimal trailingOffset, decimal limitOffset, TrailingOffsetType offsetType = TrailingOffsetType.Price,
        Price? price = null, Price? triggerPrice = null, Price? activationPrice = null, TriggerType triggerType = TriggerType.Default, UnixNanos? expireTime = null)
    {
        Dictionary<string, string> options = OrderOptions.New();
        options[OptionKeys.TrailingOffset] = OptionKeys.Format(trailingOffset);
        options[OptionKeys.LimitOffset] = OptionKeys.Format(limitOffset);
        options[OptionKeys.TrailingOffsetType] = offsetType.ToString();
        options[OptionKeys.TriggerType] = triggerType.ToString();
        if (price is { } px)
        {
            options[OptionKeys.Price] = px.ToString();
        }

        if (triggerPrice is { } tp)
        {
            options[OptionKeys.TriggerPrice] = tp.ToString();
        }

        if (activationPrice is { } ap)
        {
            options[OptionKeys.ActivationPrice] = ap.ToString();
        }

        if (expireTime is { } exp)
        {
            options[OptionKeys.ExpireTime] = exp.Value.ToString(CultureInfo.InvariantCulture);
        }

        return new TrailingStopLimitOrder(p.ToInitialized(OrderType.TrailingStopLimit, options));
    }

    public override string Info() => base.Info() + $" trailing {TrailingOffset}/{LimitOffset} {TrailingOffsetType} trigger {TriggerPrice?.ToString() ?? "None"} @ {Price?.ToString() ?? "None"}";
}

public sealed class MarketToLimitOrder : Order
{
    public MarketToLimitOrder(OrderInitialized init) : base(init)
    {
        Price = OrderOptions.GetPrice(init.Options, OptionKeys.Price);
        ExpireTime = OrderOptions.GetTime(init.Options, OptionKeys.ExpireTime);
        DisplayQuantity = OrderOptions.GetQuantity(init.Options, OptionKeys.DisplayQuantity);
    }

    protected override void OnUpdated(OrderUpdated updated)
    {
        if (updated.Price is { } price)
        {
            Price = price;
        }
    }

    public static MarketToLimitOrder Create(OrderParams p, UnixNanos? expireTime = null, Quantity? displayQuantity = null)
    {
        Dictionary<string, string> options = OrderOptions.New();
        if (expireTime is { } exp)
        {
            options[OptionKeys.ExpireTime] = exp.Value.ToString(CultureInfo.InvariantCulture);
        }

        if (displayQuantity is { } dq)
        {
            options[OptionKeys.DisplayQuantity] = dq.ToString();
        }

        return new MarketToLimitOrder(p.ToInitialized(OrderType.MarketToLimit, options));
    }
}

/// <summary>
/// Rebuilds orders from their initialising events (persistence, replay, reconciliation).
/// </summary>
public static class OrderUnpacker
{
    public static Order FromInitialized(OrderInitialized init) => init.OrderType switch
    {
        OrderType.Market => new MarketOrder(init),
        OrderType.Limit => new LimitOrder(init),
        OrderType.StopMarket => new StopMarketOrder(init),
        OrderType.StopLimit => new StopLimitOrder(init),
        OrderType.MarketIfTouched => new MarketIfTouchedOrder(init),
        OrderType.LimitIfTouched => new LimitIfTouchedOrder(init),
        OrderType.TrailingStopMarket => new TrailingStopMarketOrder(init),
        OrderType.TrailingStopLimit => new TrailingStopLimitOrder(init),
        OrderType.MarketToLimit => new MarketToLimitOrder(init),
        _ => throw new ArgumentOutOfRangeException(nameof(init), init.OrderType, "Unknown order type."),
    };

    /// <summary>
    /// Rebuilds an order by replaying all of its events.
    /// </summary>
    public static Order FromEvents(IReadOnlyList<OrderEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0 || events[0] is not OrderInitialized init)
        {
            throw new ArgumentException("Event list must start with OrderInitialized.", nameof(events));
        }

        Order order = FromInitialized(init);
        for (int i = 1; i < events.Count; i++)
        {
            order.Apply(events[i]);
        }

        return order;
    }
}

/// <summary>
/// A group of orders submitted together with a contingency relationship.
/// </summary>
public sealed class OrderList
{
    public OrderList(OrderListId id, IReadOnlyList<Order> orders)
    {
        ArgumentNullException.ThrowIfNull(orders);
        if (orders.Count == 0)
        {
            throw new ArgumentException("Order list must contain at least one order.", nameof(orders));
        }

        Id = id;
        Orders = orders;
        First = orders[0];
        InstrumentId = First.InstrumentId;
        StrategyId = First.StrategyId;
        TsInit = First.TsInit;
    }

    public OrderListId Id { get; }

    public InstrumentId InstrumentId { get; }

    public StrategyId StrategyId { get; }

    public IReadOnlyList<Order> Orders { get; }

    public Order First { get; }

    public UnixNanos TsInit { get; }

    public override string ToString() => $"OrderList({Id}, {Orders.Count} orders, {InstrumentId})";
}
