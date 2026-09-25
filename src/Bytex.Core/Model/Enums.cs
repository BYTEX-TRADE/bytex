namespace Bytex.Core.Model;

public enum OrderSide
{
    Buy = 1,
    Sell = 2,
}

public enum OrderType
{
    Market = 1,
    Limit = 2,
    StopMarket = 3,
    StopLimit = 4,
    MarketIfTouched = 5,
    LimitIfTouched = 6,
    TrailingStopMarket = 7,
    TrailingStopLimit = 8,
    MarketToLimit = 9,
}

public enum TimeInForce
{
    Gtc = 1,
    Ioc = 2,
    Fok = 3,
    Gtd = 4,
    Day = 5,
    AtTheOpen = 6,
    AtTheClose = 7,
}

public enum OrderStatus
{
    Initialized = 1,
    Denied = 2,
    Emulated = 3,
    Released = 4,
    Submitted = 5,
    Accepted = 6,
    Rejected = 7,
    Canceled = 8,
    Expired = 9,
    Triggered = 10,
    PendingUpdate = 11,
    PendingCancel = 12,
    PartiallyFilled = 13,
    Filled = 14,
}

public enum TriggerType
{
    Default = 0,
    LastPrice = 1,
    BidAsk = 2,
    MarkPrice = 3,
    IndexPrice = 4,
}

public enum TrailingOffsetType
{
    Price = 1,
    BasisPoints = 2,
    Ticks = 3,
    PriceTier = 4,
}

public enum ContingencyType
{
    None = 0,
    Oco = 1,
    Oto = 2,
    Ouo = 3,
}

public enum PositionSide
{
    Flat = 0,
    Long = 1,
    Short = 2,
}

public enum LiquiditySide
{
    None = 0,
    Maker = 1,
    Taker = 2,
}

public enum AggressorSide
{
    None = 0,
    Buyer = 1,
    Seller = 2,
}

public enum AccountType
{
    Cash = 1,
    Margin = 2,
}

public enum OmsType
{
    Unspecified = 0,
    Netting = 1,
    Hedging = 2,
}

public enum AssetClass
{
    Fx = 1,
    Equity = 2,
    Commodity = 3,
    Debt = 4,
    Index = 5,
    Crypto = 6,
    Alternative = 7,
}

public enum InstrumentClass
{
    Spot = 1,
    Swap = 2,
    Future = 3,
    Forward = 4,
    Cfd = 5,
    Option = 6,
    Warrant = 7,
    SportsBetting = 8,
}

public enum OptionKind
{
    Call = 1,
    Put = 2,
}

public enum BarAggregation
{
    Tick = 1,
    TickImbalance = 2,
    Volume = 3,
    VolumeImbalance = 4,
    Value = 5,
    ValueImbalance = 6,
    Millisecond = 7,
    Second = 8,
    Minute = 9,
    Hour = 10,
    Day = 11,
    Week = 12,
    Month = 13,
}

public enum PriceType
{
    Bid = 1,
    Ask = 2,
    Mid = 3,
    Last = 4,
    Mark = 5,
}

public enum AggregationSource
{
    External = 1,
    Internal = 2,
}

public enum BookType
{
    L1 = 1,
    L2 = 2,
    L3 = 3,
}

public enum BookAction
{
    Add = 1,
    Update = 2,
    Delete = 3,
    Clear = 4,
}

public enum MarketStatus
{
    Open = 1,
    Closed = 2,
    Paused = 3,
    Halted = 4,
    PreOpen = 5,
    PreClose = 6,
    Suspended = 7,
    NotAvailable = 8,
}

public enum TradingState
{
    Active = 1,
    Halted = 2,
    Reducing = 3,
}

public enum ComponentState
{
    PreInitialized = 0,
    Ready = 1,
    Starting = 2,
    Running = 3,
    Stopping = 4,
    Stopped = 5,
    Resuming = 6,
    Resetting = 7,
    Disposing = 8,
    Disposed = 9,
    Degrading = 10,
    Degraded = 11,
    Faulting = 12,
    Faulted = 13,
}

public enum TradingEnvironment
{
    Backtest = 1,
    Sandbox = 2,
    Live = 3,
}

public static class EnumExtensions
{
    public static OrderSide Opposite(this OrderSide side) => side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;

    public static PositionSide ToPositionSide(this OrderSide side) => side == OrderSide.Buy ? PositionSide.Long : PositionSide.Short;

    public static OrderSide ToOrderSide(this PositionSide side) => side switch
    {
        PositionSide.Long => OrderSide.Buy,
        PositionSide.Short => OrderSide.Sell,
        _ => throw new ArgumentException("Flat position has no order side.", nameof(side)),
    };

    public static OrderSide ClosingSide(this PositionSide side) => side.ToOrderSide().Opposite();

    public static AggressorSide ToAggressorSide(this OrderSide side) => side == OrderSide.Buy ? AggressorSide.Buyer : AggressorSide.Seller;

    public static bool IsTimeBased(this BarAggregation aggregation) => aggregation is BarAggregation.Millisecond or BarAggregation.Second or BarAggregation.Minute
        or BarAggregation.Hour or BarAggregation.Day or BarAggregation.Week or BarAggregation.Month;

    public static bool IsConditional(this OrderType type) => type is OrderType.StopMarket or OrderType.StopLimit or OrderType.MarketIfTouched
        or OrderType.LimitIfTouched or OrderType.TrailingStopMarket or OrderType.TrailingStopLimit;

    public static bool HasLimitPrice(this OrderType type) => type is OrderType.Limit or OrderType.StopLimit or OrderType.LimitIfTouched or OrderType.TrailingStopLimit;
}
