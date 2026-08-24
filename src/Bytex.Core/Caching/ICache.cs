using Bytex.Core.Model;
using Bytex.Core.Model.Accounts;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Positions;
using Bytex.Core.Model.Primitives;

namespace Bytex.Core.Caching;

/// <summary>
/// Read access to the in-memory state shared by all components.
/// </summary>
public interface ICache
{
    // Instruments
    Instrument? Instrument(InstrumentId id);

    IReadOnlyList<Instrument> Instruments(Venue? venue = null);

    IReadOnlyList<InstrumentId> InstrumentIds(Venue? venue = null);

    // Market data (index 0 is the most recent)
    QuoteTick? QuoteTick(InstrumentId id, int index = 0);

    IReadOnlyList<QuoteTick> QuoteTicks(InstrumentId id);

    int QuoteTickCount(InstrumentId id);

    TradeTick? TradeTick(InstrumentId id, int index = 0);

    IReadOnlyList<TradeTick> TradeTicks(InstrumentId id);

    int TradeTickCount(InstrumentId id);

    Bar? Bar(BarType barType, int index = 0);

    IReadOnlyList<Bar> Bars(BarType barType);

    int BarCount(BarType barType);

    IReadOnlyList<BarType> BarTypes(InstrumentId? instrumentId = null);

    OrderBook? OrderBook(InstrumentId id);

    MarkPriceUpdate? MarkPrice(InstrumentId id);

    IndexPriceUpdate? IndexPrice(InstrumentId id);

    FundingRateUpdate? FundingRate(InstrumentId id);

    Price? Price(InstrumentId id, PriceType priceType);

    decimal? ExchangeRate(Currency from, Currency to, PriceType priceType = PriceType.Mid);

    bool HasQuoteTicks(InstrumentId id);

    bool HasTradeTicks(InstrumentId id);

    bool HasBars(BarType barType);

    // Orders
    Order? Order(ClientOrderId id);

    Order? OrderForVenueId(VenueOrderId id);

    ClientOrderId? ClientOrderIdFor(VenueOrderId id);

    VenueOrderId? VenueOrderIdFor(ClientOrderId id);

    IReadOnlyList<Order> Orders(Venue? venue = null, InstrumentId? instrumentId = null, StrategyId? strategyId = null, OrderSide? side = null);

    IReadOnlyList<Order> OrdersOpen(Venue? venue = null, InstrumentId? instrumentId = null, StrategyId? strategyId = null, OrderSide? side = null);

    IReadOnlyList<Order> OrdersClosed(Venue? venue = null, InstrumentId? instrumentId = null, StrategyId? strategyId = null, OrderSide? side = null);

    IReadOnlyList<Order> OrdersEmulated(Venue? venue = null, InstrumentId? instrumentId = null, StrategyId? strategyId = null, OrderSide? side = null);

    IReadOnlyList<Order> OrdersInflight(Venue? venue = null, InstrumentId? instrumentId = null, StrategyId? strategyId = null, OrderSide? side = null);

    IReadOnlyList<Order> OrdersForPosition(PositionId positionId);

    IReadOnlyList<Order> OrdersForExecAlgorithm(ExecAlgorithmId execAlgorithmId, Venue? venue = null, InstrumentId? instrumentId = null, StrategyId? strategyId = null);

    IReadOnlyList<Order> OrdersForExecSpawn(ClientOrderId execSpawnId);

    bool OrderExists(ClientOrderId id);

    bool IsOrderOpen(ClientOrderId id);

    bool IsOrderClosed(ClientOrderId id);

    bool IsOrderEmulated(ClientOrderId id);

    bool IsOrderInflight(ClientOrderId id);

    bool IsOrderPendingCancelLocal(ClientOrderId id);

    int OrdersOpenCount(Venue? venue = null, InstrumentId? instrumentId = null, StrategyId? strategyId = null, OrderSide? side = null);

    int OrdersClosedCount(Venue? venue = null, InstrumentId? instrumentId = null, StrategyId? strategyId = null, OrderSide? side = null);

    int OrdersTotalCount(Venue? venue = null, InstrumentId? instrumentId = null, StrategyId? strategyId = null, OrderSide? side = null);

    OrderList? OrderList(OrderListId id);

    IReadOnlyList<OrderList> OrderLists(Venue? venue = null, InstrumentId? instrumentId = null, StrategyId? strategyId = null);

    bool OrderListExists(OrderListId id);

    // Positions
    Position? Position(PositionId id);

    Position? PositionForOrder(ClientOrderId clientOrderId);

    PositionId? PositionIdFor(ClientOrderId clientOrderId);

    IReadOnlyList<Position> Positions(Venue? venue = null, InstrumentId? instrumentId = null, StrategyId? strategyId = null, PositionSide? side = null);

    IReadOnlyList<Position> PositionsOpen(Venue? venue = null, InstrumentId? instrumentId = null, StrategyId? strategyId = null, PositionSide? side = null);

    IReadOnlyList<Position> PositionsClosed(Venue? venue = null, InstrumentId? instrumentId = null, StrategyId? strategyId = null);

    bool PositionExists(PositionId id);

    bool IsPositionOpen(PositionId id);

    bool IsPositionClosed(PositionId id);

    int PositionsOpenCount(Venue? venue = null, InstrumentId? instrumentId = null, StrategyId? strategyId = null, PositionSide? side = null);

    int PositionsClosedCount(Venue? venue = null, InstrumentId? instrumentId = null, StrategyId? strategyId = null);

    int PositionsTotalCount(Venue? venue = null, InstrumentId? instrumentId = null, StrategyId? strategyId = null, PositionSide? side = null);

    // Accounts
    Account? Account(AccountId id);

    Account? AccountForVenue(Venue venue);

    AccountId? AccountIdFor(Venue venue);

    IReadOnlyList<Account> Accounts();

    // Strategy lookups
    StrategyId? StrategyIdForOrder(ClientOrderId clientOrderId);

    StrategyId? StrategyIdForPosition(PositionId positionId);

    // General-purpose key/value storage
    void Add(string key, byte[] value);

    byte[]? Get(string key);

    // Actor and strategy state
    void SaveActorState(ActorId actorId, IDictionary<string, byte[]> state);

    IDictionary<string, byte[]>? LoadActorState(ActorId actorId);
}
