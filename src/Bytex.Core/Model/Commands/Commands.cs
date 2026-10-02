using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;

namespace Bytex.Core.Model.Commands;

public abstract record Command(Guid CommandId, UnixNanos CreatedTime);

/// <summary>
/// Base type for commands that act on orders.
/// </summary>
public abstract record TradingCommand(
    ModuleHostId ModuleHostId,
    StrategyId StrategyId,
    MarketKey MarketKey,
    ClientId? ClientId,
    Guid CommandId,
    UnixNanos CreatedTime) : Command(CommandId, CreatedTime);

public sealed record SubmitOrder(
    ModuleHostId ModuleHostId, StrategyId StrategyId, Order Order, PositionId? PositionId, OrderScheduleId? OrderScheduleId,
    ClientId? ClientId, Guid CommandId, UnixNanos CreatedTime)
    : TradingCommand(ModuleHostId, StrategyId, Order.MarketKey, ClientId, CommandId, CreatedTime);

public sealed record SubmitOrderList(
    ModuleHostId ModuleHostId, StrategyId StrategyId, OrderList OrderList, PositionId? PositionId, OrderScheduleId? OrderScheduleId,
    ClientId? ClientId, Guid CommandId, UnixNanos CreatedTime)
    : TradingCommand(ModuleHostId, StrategyId, OrderList.MarketKey, ClientId, CommandId, CreatedTime);

public sealed record ModifyOrder(
    ModuleHostId ModuleHostId, StrategyId StrategyId, MarketKey MarketKey, ClientOrderId ClientOrderId, VenueOrderId? VenueOrderId,
    Quantity? Quantity, Price? Price, Price? TriggerPrice, ClientId? ClientId, Guid CommandId, UnixNanos CreatedTime)
    : TradingCommand(ModuleHostId, StrategyId, MarketKey, ClientId, CommandId, CreatedTime);

public sealed record CancelOrder(
    ModuleHostId ModuleHostId, StrategyId StrategyId, MarketKey MarketKey, ClientOrderId ClientOrderId, VenueOrderId? VenueOrderId,
    ClientId? ClientId, Guid CommandId, UnixNanos CreatedTime)
    : TradingCommand(ModuleHostId, StrategyId, MarketKey, ClientId, CommandId, CreatedTime);

public sealed record CancelAllOrders(
    ModuleHostId ModuleHostId, StrategyId StrategyId, MarketKey MarketKey, OrderSide? OrderSide,
    ClientId? ClientId, Guid CommandId, UnixNanos CreatedTime)
    : TradingCommand(ModuleHostId, StrategyId, MarketKey, ClientId, CommandId, CreatedTime);

public sealed record BatchCancelOrders(
    ModuleHostId ModuleHostId, StrategyId StrategyId, MarketKey MarketKey, IReadOnlyList<CancelOrder> Cancels,
    ClientId? ClientId, Guid CommandId, UnixNanos CreatedTime)
    : TradingCommand(ModuleHostId, StrategyId, MarketKey, ClientId, CommandId, CreatedTime);

public sealed record QueryOrder(
    ModuleHostId ModuleHostId, StrategyId StrategyId, MarketKey MarketKey, ClientOrderId ClientOrderId, VenueOrderId? VenueOrderId,
    ClientId? ClientId, Guid CommandId, UnixNanos CreatedTime)
    : TradingCommand(ModuleHostId, StrategyId, MarketKey, ClientId, CommandId, CreatedTime);

/// <summary>
/// Base type for data subscription, unsubscription, and request commands handled by the data engine.
/// </summary>
public abstract record DataCommand(ClientId? ClientId, Venue? Venue, Guid CommandId, UnixNanos CreatedTime) : Command(CommandId, CreatedTime);

public abstract record SubscribeCommand(ClientId? ClientId, Venue? Venue, Guid CommandId, UnixNanos CreatedTime) : DataCommand(ClientId, Venue, CommandId, CreatedTime)
{
    /// <summary>Message-bus topic that subscribers of this stream listen on.</summary>
    public abstract string Topic { get; }
}

public abstract record UnsubscribeCommand(ClientId? ClientId, Venue? Venue, Guid CommandId, UnixNanos CreatedTime) : DataCommand(ClientId, Venue, CommandId, CreatedTime)
{
    public abstract string Topic { get; }
}

public sealed record SubscribeInstruments(Venue? Venue, ClientId? ClientId, Guid CommandId, UnixNanos CreatedTime) : SubscribeCommand(ClientId, Venue, CommandId, CreatedTime)
{
    public override string Topic => Topics.Instruments(Venue!.Value);
}

public sealed record SubscribeInstrument(MarketKey MarketKey, ClientId? ClientId, Guid CommandId, UnixNanos CreatedTime) : SubscribeCommand(ClientId, MarketKey.Venue, CommandId, CreatedTime)
{
    public override string Topic => Topics.Instrument(MarketKey);
}

public sealed record SubscribeQuoteTicks(MarketKey MarketKey, ClientId? ClientId, Guid CommandId, UnixNanos CreatedTime) : SubscribeCommand(ClientId, MarketKey.Venue, CommandId, CreatedTime)
{
    public override string Topic => Topics.Quotes(MarketKey);
}

public sealed record SubscribeTradeTicks(MarketKey MarketKey, ClientId? ClientId, Guid CommandId, UnixNanos CreatedTime) : SubscribeCommand(ClientId, MarketKey.Venue, CommandId, CreatedTime)
{
    public override string Topic => Topics.Trades(MarketKey);
}

public sealed record SubscribeBars(CandleSeries CandleSeries, ClientId? ClientId, Guid CommandId, UnixNanos CreatedTime) : SubscribeCommand(ClientId, CandleSeries.MarketKey.Venue, CommandId, CreatedTime)
{
    public override string Topic => Topics.Bars(CandleSeries);
}

public sealed record SubscribeOrderBookDeltas(MarketKey MarketKey, BookType BookType, int Depth, ClientId? ClientId, Guid CommandId, UnixNanos CreatedTime) : SubscribeCommand(ClientId, MarketKey.Venue, CommandId, CreatedTime)
{
    public override string Topic => Topics.BookDeltas(MarketKey);
}

public sealed record SubscribeOrderBookSnapshots(MarketKey MarketKey, BookType BookType, int Depth, TimeSpan Interval, ClientId? ClientId, Guid CommandId, UnixNanos CreatedTime) : SubscribeCommand(ClientId, MarketKey.Venue, CommandId, CreatedTime)
{
    public override string Topic => Topics.BookSnapshots(MarketKey);
}

public sealed record SubscribeInstrumentStatus(MarketKey MarketKey, ClientId? ClientId, Guid CommandId, UnixNanos CreatedTime) : SubscribeCommand(ClientId, MarketKey.Venue, CommandId, CreatedTime)
{
    public override string Topic => Topics.Status(MarketKey);
}

public sealed record SubscribeMarkPrices(MarketKey MarketKey, ClientId? ClientId, Guid CommandId, UnixNanos CreatedTime) : SubscribeCommand(ClientId, MarketKey.Venue, CommandId, CreatedTime)
{
    public override string Topic => Topics.MarkPrices(MarketKey);
}

public sealed record SubscribeIndexPrices(MarketKey MarketKey, ClientId? ClientId, Guid CommandId, UnixNanos CreatedTime) : SubscribeCommand(ClientId, MarketKey.Venue, CommandId, CreatedTime)
{
    public override string Topic => Topics.IndexPrices(MarketKey);
}

public sealed record SubscribeFundingRates(MarketKey MarketKey, ClientId? ClientId, Guid CommandId, UnixNanos CreatedTime) : SubscribeCommand(ClientId, MarketKey.Venue, CommandId, CreatedTime)
{
    public override string Topic => Topics.FundingRates(MarketKey);
}

public sealed record SubscribeData(DataType DataType, ClientId? ClientId, Guid CommandId, UnixNanos CreatedTime) : SubscribeCommand(ClientId, null, CommandId, CreatedTime)
{
    public override string Topic => Topics.Custom(DataType);
}

public sealed record UnsubscribeInstruments(Venue? Venue, ClientId? ClientId, Guid CommandId, UnixNanos CreatedTime) : UnsubscribeCommand(ClientId, Venue, CommandId, CreatedTime)
{
    public override string Topic => Topics.Instruments(Venue!.Value);
}

public sealed record UnsubscribeInstrument(MarketKey MarketKey, ClientId? ClientId, Guid CommandId, UnixNanos CreatedTime) : UnsubscribeCommand(ClientId, MarketKey.Venue, CommandId, CreatedTime)
{
    public override string Topic => Topics.Instrument(MarketKey);
}

public sealed record UnsubscribeQuoteTicks(MarketKey MarketKey, ClientId? ClientId, Guid CommandId, UnixNanos CreatedTime) : UnsubscribeCommand(ClientId, MarketKey.Venue, CommandId, CreatedTime)
{
    public override string Topic => Topics.Quotes(MarketKey);
}

public sealed record UnsubscribeTradeTicks(MarketKey MarketKey, ClientId? ClientId, Guid CommandId, UnixNanos CreatedTime) : UnsubscribeCommand(ClientId, MarketKey.Venue, CommandId, CreatedTime)
{
    public override string Topic => Topics.Trades(MarketKey);
}

public sealed record UnsubscribeBars(CandleSeries CandleSeries, ClientId? ClientId, Guid CommandId, UnixNanos CreatedTime) : UnsubscribeCommand(ClientId, CandleSeries.MarketKey.Venue, CommandId, CreatedTime)
{
    public override string Topic => Topics.Bars(CandleSeries);
}

public sealed record UnsubscribeOrderBookDeltas(MarketKey MarketKey, ClientId? ClientId, Guid CommandId, UnixNanos CreatedTime) : UnsubscribeCommand(ClientId, MarketKey.Venue, CommandId, CreatedTime)
{
    public override string Topic => Topics.BookDeltas(MarketKey);
}

public sealed record UnsubscribeOrderBookSnapshots(MarketKey MarketKey, ClientId? ClientId, Guid CommandId, UnixNanos CreatedTime) : UnsubscribeCommand(ClientId, MarketKey.Venue, CommandId, CreatedTime)
{
    public override string Topic => Topics.BookSnapshots(MarketKey);
}

public sealed record UnsubscribeInstrumentStatus(MarketKey MarketKey, ClientId? ClientId, Guid CommandId, UnixNanos CreatedTime) : UnsubscribeCommand(ClientId, MarketKey.Venue, CommandId, CreatedTime)
{
    public override string Topic => Topics.Status(MarketKey);
}

public sealed record UnsubscribeMarkPrices(MarketKey MarketKey, ClientId? ClientId, Guid CommandId, UnixNanos CreatedTime) : UnsubscribeCommand(ClientId, MarketKey.Venue, CommandId, CreatedTime)
{
    public override string Topic => Topics.MarkPrices(MarketKey);
}

public sealed record UnsubscribeIndexPrices(MarketKey MarketKey, ClientId? ClientId, Guid CommandId, UnixNanos CreatedTime) : UnsubscribeCommand(ClientId, MarketKey.Venue, CommandId, CreatedTime)
{
    public override string Topic => Topics.IndexPrices(MarketKey);
}

public sealed record UnsubscribeFundingRates(MarketKey MarketKey, ClientId? ClientId, Guid CommandId, UnixNanos CreatedTime) : UnsubscribeCommand(ClientId, MarketKey.Venue, CommandId, CreatedTime)
{
    public override string Topic => Topics.FundingRates(MarketKey);
}

public sealed record UnsubscribeData(DataType DataType, ClientId? ClientId, Guid CommandId, UnixNanos CreatedTime) : UnsubscribeCommand(ClientId, null, CommandId, CreatedTime)
{
    public override string Topic => Topics.Custom(DataType);
}

/// <summary>
/// Base type for historical data requests. The response carries the same correlation id.
/// </summary>
public abstract record RequestCommand(
    ClientId? ClientId, Venue? Venue, UnixNanos? Start, UnixNanos? End, int? Limit, Guid CommandId, UnixNanos CreatedTime)
    : DataCommand(ClientId, Venue, CommandId, CreatedTime)
{
    /// <summary>Identifies the requesting runtimeModule so the response can be routed back.</summary>
    public RuntimeModuleId? Requester { get; init; }
}

public sealed record RequestInstrument(MarketKey MarketKey, ClientId? ClientId, Guid CommandId, UnixNanos CreatedTime)
    : RequestCommand(ClientId, MarketKey.Venue, null, null, null, CommandId, CreatedTime);

public sealed record RequestInstruments(Venue? Venue, ClientId? ClientId, Guid CommandId, UnixNanos CreatedTime)
    : RequestCommand(ClientId, Venue, null, null, null, CommandId, CreatedTime);

public sealed record RequestQuoteTicks(MarketKey MarketKey, UnixNanos? Start, UnixNanos? End, int? Limit, ClientId? ClientId, Guid CommandId, UnixNanos CreatedTime)
    : RequestCommand(ClientId, MarketKey.Venue, Start, End, Limit, CommandId, CreatedTime);

public sealed record RequestTradeTicks(MarketKey MarketKey, UnixNanos? Start, UnixNanos? End, int? Limit, ClientId? ClientId, Guid CommandId, UnixNanos CreatedTime)
    : RequestCommand(ClientId, MarketKey.Venue, Start, End, Limit, CommandId, CreatedTime);

public sealed record RequestBars(CandleSeries CandleSeries, UnixNanos? Start, UnixNanos? End, int? Limit, ClientId? ClientId, Guid CommandId, UnixNanos CreatedTime)
    : RequestCommand(ClientId, CandleSeries.MarketKey.Venue, Start, End, Limit, CommandId, CreatedTime);

/// <summary>
/// The funding a perpetual has charged over a period. A backtest that holds one and is not charged for holding it is
/// reporting a position nobody could have held for free, so this is what puts the rates where a run can read them.
/// </summary>
public sealed record RequestFundingRates(MarketKey MarketKey, UnixNanos? Start, UnixNanos? End, int? Limit, ClientId? ClientId, Guid CommandId, UnixNanos CreatedTime)
    : RequestCommand(ClientId, MarketKey.Venue, Start, End, Limit, CommandId, CreatedTime);

public sealed record RequestData(DataType DataType, UnixNanos? Start, UnixNanos? End, int? Limit, ClientId? ClientId, Guid CommandId, UnixNanos CreatedTime)
    : RequestCommand(ClientId, null, Start, End, Limit, CommandId, CreatedTime);

/// <summary>
/// Response to a <see cref="RequestCommand"/> carrying the requested data.
/// </summary>
public sealed record DataResponse(
    Guid CorrelationId,
    ClientId ClientId,
    Venue? Venue,
    Type DataType,
    IReadOnlyList<IData> Data,
    UnixNanos CreatedTime,
    RuntimeModuleId? Requester = null,
    string? Error = null)
{
    public bool IsError => Error is not null;
}

/// <summary>
/// Message-bus topic builders shared by the data engine and runtimeModules.
/// </summary>
public static class Topics
{
    public static string Instruments(Venue venue) => $"data.instrument.{venue}.*";

    public static string Instrument(MarketKey id) => $"data.instrument.{id.Venue}.{id.Symbol}";

    public static string Quotes(MarketKey id) => $"data.quotes.{id.Venue}.{id.Symbol}";

    public static string Trades(MarketKey id) => $"data.trades.{id.Venue}.{id.Symbol}";

    public static string Bars(CandleSeries candleSeries) => $"data.bars.{candleSeries}";

    public static string BookDeltas(MarketKey id) => $"data.book.deltas.{id.Venue}.{id.Symbol}";

    public static string BookSnapshots(MarketKey id) => $"data.book.snapshots.{id.Venue}.{id.Symbol}";

    public static string Status(MarketKey id) => $"data.status.{id.Venue}.{id.Symbol}";

    public static string MarkPrices(MarketKey id) => $"data.mark.{id.Venue}.{id.Symbol}";

    public static string IndexPrices(MarketKey id) => $"data.index.{id.Venue}.{id.Symbol}";

    public static string FundingRates(MarketKey id) => $"data.funding.{id.Venue}.{id.Symbol}";

    public static string Custom(DataType dataType) => $"data.custom.{dataType.Topic}";

    public static string Signal(string name) => $"signal.{name}";

    public static string OrderEvents(StrategyId strategyId) => $"events.order.{strategyId}";

    public static string PositionEvents(StrategyId strategyId) => $"events.position.{strategyId}";

    public static string AccountEvents(AccountId accountId) => $"events.account.{accountId}";

    public static string SystemEvents(ComponentId componentId) => $"events.system.{componentId}";

    public static string DataResponses(RuntimeModuleId runtimeModuleId) => $"responses.data.{runtimeModuleId}";

    /// <summary>Every instrument's quotes, trades, bars and mark prices: what moves what an open position is worth.</summary>
    public const string AllQuotes = "data.quotes.*";

    /// <inheritdoc cref="AllQuotes"/>
    public const string AllTrades = "data.trades.*";

    /// <inheritdoc cref="AllQuotes"/>
    public const string AllBars = "data.bars.*";

    /// <inheritdoc cref="AllQuotes"/>
    public const string AllMarkPrices = "data.mark.*";

    /// <inheritdoc cref="AllQuotes"/>
    public const string AllIndexPrices = "data.index.*";

    public const string AllOrderEvents = "events.order.*";
    public const string AllPositionEvents = "events.position.*";
    public const string AllAccountEvents = "events.account.*";
}

/// <summary>
/// Point-to-point endpoints registered on the message bus.
/// </summary>
public static class Endpoints
{
    public const string MarketDataServiceExecute = "MarketDataService.execute";
    public const string MarketDataServiceProcess = "MarketDataService.process";
    public const string MarketDataServiceRequest = "MarketDataService.request";
    public const string MarketDataServiceResponse = "MarketDataService.response";
    public const string OrderPolicyExecute = "OrderPolicy.execute";
    public const string OrderCoordinatorExecute = "OrderCoordinator.execute";
    public const string OrderCoordinatorProcess = "OrderCoordinator.process";
    public const string PortfolioUpdateAccount = "Portfolio.update_account";
}
