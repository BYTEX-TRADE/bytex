using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;

namespace Bytex.Core.Model.Commands;

public abstract record Command(Guid CommandId, UnixNanos TsInit);

/// <summary>
/// Base type for commands that act on orders.
/// </summary>
public abstract record TradingCommand(
    TraderId TraderId,
    StrategyId StrategyId,
    InstrumentId InstrumentId,
    ClientId? ClientId,
    Guid CommandId,
    UnixNanos TsInit) : Command(CommandId, TsInit);

public sealed record SubmitOrder(
    TraderId TraderId, StrategyId StrategyId, Order Order, PositionId? PositionId, ExecAlgorithmId? ExecAlgorithmId,
    ClientId? ClientId, Guid CommandId, UnixNanos TsInit)
    : TradingCommand(TraderId, StrategyId, Order.InstrumentId, ClientId, CommandId, TsInit);

public sealed record SubmitOrderList(
    TraderId TraderId, StrategyId StrategyId, OrderList OrderList, PositionId? PositionId, ExecAlgorithmId? ExecAlgorithmId,
    ClientId? ClientId, Guid CommandId, UnixNanos TsInit)
    : TradingCommand(TraderId, StrategyId, OrderList.InstrumentId, ClientId, CommandId, TsInit);

public sealed record ModifyOrder(
    TraderId TraderId, StrategyId StrategyId, InstrumentId InstrumentId, ClientOrderId ClientOrderId, VenueOrderId? VenueOrderId,
    Quantity? Quantity, Price? Price, Price? TriggerPrice, ClientId? ClientId, Guid CommandId, UnixNanos TsInit)
    : TradingCommand(TraderId, StrategyId, InstrumentId, ClientId, CommandId, TsInit);

public sealed record CancelOrder(
    TraderId TraderId, StrategyId StrategyId, InstrumentId InstrumentId, ClientOrderId ClientOrderId, VenueOrderId? VenueOrderId,
    ClientId? ClientId, Guid CommandId, UnixNanos TsInit)
    : TradingCommand(TraderId, StrategyId, InstrumentId, ClientId, CommandId, TsInit);

public sealed record CancelAllOrders(
    TraderId TraderId, StrategyId StrategyId, InstrumentId InstrumentId, OrderSide? OrderSide,
    ClientId? ClientId, Guid CommandId, UnixNanos TsInit)
    : TradingCommand(TraderId, StrategyId, InstrumentId, ClientId, CommandId, TsInit);

public sealed record BatchCancelOrders(
    TraderId TraderId, StrategyId StrategyId, InstrumentId InstrumentId, IReadOnlyList<CancelOrder> Cancels,
    ClientId? ClientId, Guid CommandId, UnixNanos TsInit)
    : TradingCommand(TraderId, StrategyId, InstrumentId, ClientId, CommandId, TsInit);

public sealed record QueryOrder(
    TraderId TraderId, StrategyId StrategyId, InstrumentId InstrumentId, ClientOrderId ClientOrderId, VenueOrderId? VenueOrderId,
    ClientId? ClientId, Guid CommandId, UnixNanos TsInit)
    : TradingCommand(TraderId, StrategyId, InstrumentId, ClientId, CommandId, TsInit);

/// <summary>
/// Base type for data subscription, unsubscription, and request commands handled by the data engine.
/// </summary>
public abstract record DataCommand(ClientId? ClientId, Venue? Venue, Guid CommandId, UnixNanos TsInit) : Command(CommandId, TsInit);

public abstract record SubscribeCommand(ClientId? ClientId, Venue? Venue, Guid CommandId, UnixNanos TsInit) : DataCommand(ClientId, Venue, CommandId, TsInit)
{
    /// <summary>Message-bus topic that subscribers of this stream listen on.</summary>
    public abstract string Topic { get; }
}

public abstract record UnsubscribeCommand(ClientId? ClientId, Venue? Venue, Guid CommandId, UnixNanos TsInit) : DataCommand(ClientId, Venue, CommandId, TsInit)
{
    public abstract string Topic { get; }
}

public sealed record SubscribeInstruments(Venue? Venue, ClientId? ClientId, Guid CommandId, UnixNanos TsInit) : SubscribeCommand(ClientId, Venue, CommandId, TsInit)
{
    public override string Topic => Topics.Instruments(Venue!.Value);
}

public sealed record SubscribeInstrument(InstrumentId InstrumentId, ClientId? ClientId, Guid CommandId, UnixNanos TsInit) : SubscribeCommand(ClientId, InstrumentId.Venue, CommandId, TsInit)
{
    public override string Topic => Topics.Instrument(InstrumentId);
}

public sealed record SubscribeQuoteTicks(InstrumentId InstrumentId, ClientId? ClientId, Guid CommandId, UnixNanos TsInit) : SubscribeCommand(ClientId, InstrumentId.Venue, CommandId, TsInit)
{
    public override string Topic => Topics.Quotes(InstrumentId);
}

public sealed record SubscribeTradeTicks(InstrumentId InstrumentId, ClientId? ClientId, Guid CommandId, UnixNanos TsInit) : SubscribeCommand(ClientId, InstrumentId.Venue, CommandId, TsInit)
{
    public override string Topic => Topics.Trades(InstrumentId);
}

public sealed record SubscribeBars(BarType BarType, ClientId? ClientId, Guid CommandId, UnixNanos TsInit) : SubscribeCommand(ClientId, BarType.InstrumentId.Venue, CommandId, TsInit)
{
    public override string Topic => Topics.Bars(BarType);
}

public sealed record SubscribeOrderBookDeltas(InstrumentId InstrumentId, BookType BookType, int Depth, ClientId? ClientId, Guid CommandId, UnixNanos TsInit) : SubscribeCommand(ClientId, InstrumentId.Venue, CommandId, TsInit)
{
    public override string Topic => Topics.BookDeltas(InstrumentId);
}

public sealed record SubscribeOrderBookSnapshots(InstrumentId InstrumentId, BookType BookType, int Depth, TimeSpan Interval, ClientId? ClientId, Guid CommandId, UnixNanos TsInit) : SubscribeCommand(ClientId, InstrumentId.Venue, CommandId, TsInit)
{
    public override string Topic => Topics.BookSnapshots(InstrumentId);
}

public sealed record SubscribeInstrumentStatus(InstrumentId InstrumentId, ClientId? ClientId, Guid CommandId, UnixNanos TsInit) : SubscribeCommand(ClientId, InstrumentId.Venue, CommandId, TsInit)
{
    public override string Topic => Topics.Status(InstrumentId);
}

public sealed record SubscribeMarkPrices(InstrumentId InstrumentId, ClientId? ClientId, Guid CommandId, UnixNanos TsInit) : SubscribeCommand(ClientId, InstrumentId.Venue, CommandId, TsInit)
{
    public override string Topic => Topics.MarkPrices(InstrumentId);
}

public sealed record SubscribeIndexPrices(InstrumentId InstrumentId, ClientId? ClientId, Guid CommandId, UnixNanos TsInit) : SubscribeCommand(ClientId, InstrumentId.Venue, CommandId, TsInit)
{
    public override string Topic => Topics.IndexPrices(InstrumentId);
}

public sealed record SubscribeFundingRates(InstrumentId InstrumentId, ClientId? ClientId, Guid CommandId, UnixNanos TsInit) : SubscribeCommand(ClientId, InstrumentId.Venue, CommandId, TsInit)
{
    public override string Topic => Topics.FundingRates(InstrumentId);
}

public sealed record SubscribeData(DataType DataType, ClientId? ClientId, Guid CommandId, UnixNanos TsInit) : SubscribeCommand(ClientId, null, CommandId, TsInit)
{
    public override string Topic => Topics.Custom(DataType);
}

public sealed record UnsubscribeInstruments(Venue? Venue, ClientId? ClientId, Guid CommandId, UnixNanos TsInit) : UnsubscribeCommand(ClientId, Venue, CommandId, TsInit)
{
    public override string Topic => Topics.Instruments(Venue!.Value);
}

public sealed record UnsubscribeInstrument(InstrumentId InstrumentId, ClientId? ClientId, Guid CommandId, UnixNanos TsInit) : UnsubscribeCommand(ClientId, InstrumentId.Venue, CommandId, TsInit)
{
    public override string Topic => Topics.Instrument(InstrumentId);
}

public sealed record UnsubscribeQuoteTicks(InstrumentId InstrumentId, ClientId? ClientId, Guid CommandId, UnixNanos TsInit) : UnsubscribeCommand(ClientId, InstrumentId.Venue, CommandId, TsInit)
{
    public override string Topic => Topics.Quotes(InstrumentId);
}

public sealed record UnsubscribeTradeTicks(InstrumentId InstrumentId, ClientId? ClientId, Guid CommandId, UnixNanos TsInit) : UnsubscribeCommand(ClientId, InstrumentId.Venue, CommandId, TsInit)
{
    public override string Topic => Topics.Trades(InstrumentId);
}

public sealed record UnsubscribeBars(BarType BarType, ClientId? ClientId, Guid CommandId, UnixNanos TsInit) : UnsubscribeCommand(ClientId, BarType.InstrumentId.Venue, CommandId, TsInit)
{
    public override string Topic => Topics.Bars(BarType);
}

public sealed record UnsubscribeOrderBookDeltas(InstrumentId InstrumentId, ClientId? ClientId, Guid CommandId, UnixNanos TsInit) : UnsubscribeCommand(ClientId, InstrumentId.Venue, CommandId, TsInit)
{
    public override string Topic => Topics.BookDeltas(InstrumentId);
}

public sealed record UnsubscribeOrderBookSnapshots(InstrumentId InstrumentId, ClientId? ClientId, Guid CommandId, UnixNanos TsInit) : UnsubscribeCommand(ClientId, InstrumentId.Venue, CommandId, TsInit)
{
    public override string Topic => Topics.BookSnapshots(InstrumentId);
}

public sealed record UnsubscribeInstrumentStatus(InstrumentId InstrumentId, ClientId? ClientId, Guid CommandId, UnixNanos TsInit) : UnsubscribeCommand(ClientId, InstrumentId.Venue, CommandId, TsInit)
{
    public override string Topic => Topics.Status(InstrumentId);
}

public sealed record UnsubscribeMarkPrices(InstrumentId InstrumentId, ClientId? ClientId, Guid CommandId, UnixNanos TsInit) : UnsubscribeCommand(ClientId, InstrumentId.Venue, CommandId, TsInit)
{
    public override string Topic => Topics.MarkPrices(InstrumentId);
}

public sealed record UnsubscribeIndexPrices(InstrumentId InstrumentId, ClientId? ClientId, Guid CommandId, UnixNanos TsInit) : UnsubscribeCommand(ClientId, InstrumentId.Venue, CommandId, TsInit)
{
    public override string Topic => Topics.IndexPrices(InstrumentId);
}

public sealed record UnsubscribeFundingRates(InstrumentId InstrumentId, ClientId? ClientId, Guid CommandId, UnixNanos TsInit) : UnsubscribeCommand(ClientId, InstrumentId.Venue, CommandId, TsInit)
{
    public override string Topic => Topics.FundingRates(InstrumentId);
}

public sealed record UnsubscribeData(DataType DataType, ClientId? ClientId, Guid CommandId, UnixNanos TsInit) : UnsubscribeCommand(ClientId, null, CommandId, TsInit)
{
    public override string Topic => Topics.Custom(DataType);
}

/// <summary>
/// Base type for historical data requests. The response carries the same correlation id.
/// </summary>
public abstract record RequestCommand(
    ClientId? ClientId, Venue? Venue, UnixNanos? Start, UnixNanos? End, int? Limit, Guid CommandId, UnixNanos TsInit)
    : DataCommand(ClientId, Venue, CommandId, TsInit)
{
    /// <summary>Identifies the requesting actor so the response can be routed back.</summary>
    public ActorId? Requester { get; init; }
}

public sealed record RequestInstrument(InstrumentId InstrumentId, ClientId? ClientId, Guid CommandId, UnixNanos TsInit)
    : RequestCommand(ClientId, InstrumentId.Venue, null, null, null, CommandId, TsInit);

public sealed record RequestInstruments(Venue? Venue, ClientId? ClientId, Guid CommandId, UnixNanos TsInit)
    : RequestCommand(ClientId, Venue, null, null, null, CommandId, TsInit);

public sealed record RequestQuoteTicks(InstrumentId InstrumentId, UnixNanos? Start, UnixNanos? End, int? Limit, ClientId? ClientId, Guid CommandId, UnixNanos TsInit)
    : RequestCommand(ClientId, InstrumentId.Venue, Start, End, Limit, CommandId, TsInit);

public sealed record RequestTradeTicks(InstrumentId InstrumentId, UnixNanos? Start, UnixNanos? End, int? Limit, ClientId? ClientId, Guid CommandId, UnixNanos TsInit)
    : RequestCommand(ClientId, InstrumentId.Venue, Start, End, Limit, CommandId, TsInit);

public sealed record RequestBars(BarType BarType, UnixNanos? Start, UnixNanos? End, int? Limit, ClientId? ClientId, Guid CommandId, UnixNanos TsInit)
    : RequestCommand(ClientId, BarType.InstrumentId.Venue, Start, End, Limit, CommandId, TsInit);

public sealed record RequestData(DataType DataType, UnixNanos? Start, UnixNanos? End, int? Limit, ClientId? ClientId, Guid CommandId, UnixNanos TsInit)
    : RequestCommand(ClientId, null, Start, End, Limit, CommandId, TsInit);

/// <summary>
/// Response to a <see cref="RequestCommand"/> carrying the requested data.
/// </summary>
public sealed record DataResponse(
    Guid CorrelationId,
    ClientId ClientId,
    Venue? Venue,
    Type DataType,
    IReadOnlyList<IData> Data,
    UnixNanos TsInit,
    ActorId? Requester = null,
    string? Error = null)
{
    public bool IsError => Error is not null;
}

/// <summary>
/// Message-bus topic builders shared by the data engine and actors.
/// </summary>
public static class Topics
{
    public static string Instruments(Venue venue) => $"data.instrument.{venue}.*";

    public static string Instrument(InstrumentId id) => $"data.instrument.{id.Venue}.{id.Symbol}";

    public static string Quotes(InstrumentId id) => $"data.quotes.{id.Venue}.{id.Symbol}";

    public static string Trades(InstrumentId id) => $"data.trades.{id.Venue}.{id.Symbol}";

    public static string Bars(BarType barType) => $"data.bars.{barType}";

    public static string BookDeltas(InstrumentId id) => $"data.book.deltas.{id.Venue}.{id.Symbol}";

    public static string BookSnapshots(InstrumentId id) => $"data.book.snapshots.{id.Venue}.{id.Symbol}";

    public static string Status(InstrumentId id) => $"data.status.{id.Venue}.{id.Symbol}";

    public static string MarkPrices(InstrumentId id) => $"data.mark.{id.Venue}.{id.Symbol}";

    public static string IndexPrices(InstrumentId id) => $"data.index.{id.Venue}.{id.Symbol}";

    public static string FundingRates(InstrumentId id) => $"data.funding.{id.Venue}.{id.Symbol}";

    public static string Custom(DataType dataType) => $"data.custom.{dataType.Topic}";

    public static string Signal(string name) => $"signal.{name}";

    public static string OrderEvents(StrategyId strategyId) => $"events.order.{strategyId}";

    public static string PositionEvents(StrategyId strategyId) => $"events.position.{strategyId}";

    public static string AccountEvents(AccountId accountId) => $"events.account.{accountId}";

    public static string SystemEvents(ComponentId componentId) => $"events.system.{componentId}";

    public static string DataResponses(ActorId actorId) => $"responses.data.{actorId}";

    public const string AllOrderEvents = "events.order.*";
    public const string AllPositionEvents = "events.position.*";
    public const string AllAccountEvents = "events.account.*";
}

/// <summary>
/// Point-to-point endpoints registered on the message bus.
/// </summary>
public static class Endpoints
{
    public const string DataEngineExecute = "DataEngine.execute";
    public const string DataEngineProcess = "DataEngine.process";
    public const string DataEngineRequest = "DataEngine.request";
    public const string DataEngineResponse = "DataEngine.response";
    public const string RiskEngineExecute = "RiskEngine.execute";
    public const string ExecutionEngineExecute = "ExecutionEngine.execute";
    public const string ExecutionEngineProcess = "ExecutionEngine.process";
    public const string PortfolioUpdateAccount = "Portfolio.update_account";
}
