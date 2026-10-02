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
    Instrument? Instrument(MarketKey id);

    IReadOnlyList<Instrument> Instruments(Venue? venue = null);

    IReadOnlyList<MarketKey> MarketKeys(Venue? venue = null);

    // Market data (index 0 is the most recent)
    QuoteTick? QuoteTick(MarketKey id, int index = 0);

    IReadOnlyList<QuoteTick> QuoteTicks(MarketKey id);

    int QuoteTickCount(MarketKey id);

    TradeTick? TradeTick(MarketKey id, int index = 0);

    IReadOnlyList<TradeTick> TradeTicks(MarketKey id);

    int TradeTickCount(MarketKey id);

    Bar? Bar(CandleSeries candleSeries, int index = 0);

    IReadOnlyList<Bar> Bars(CandleSeries candleSeries);

    int BarCount(CandleSeries candleSeries);

    IReadOnlyList<CandleSeries> CandleSeriesDefinitions(MarketKey? marketKey = null);

    OrderBook? OrderBook(MarketKey id);

    MarkPriceUpdate? MarkPrice(MarketKey id);

    IndexPriceUpdate? IndexPrice(MarketKey id);

    FundingRateUpdate? FundingRate(MarketKey id);

    Price? Price(MarketKey id, PriceType priceType);

    decimal? ExchangeRate(Currency from, Currency to, PriceType priceType = PriceType.Mid);

    bool HasQuoteTicks(MarketKey id);

    bool HasTradeTicks(MarketKey id);

    bool HasBars(CandleSeries candleSeries);

    // Orders
    Order? Order(ClientOrderId id);

    Order? OrderForVenueId(VenueOrderId id);

    ClientOrderId? ClientOrderIdFor(VenueOrderId id);

    VenueOrderId? VenueOrderIdFor(ClientOrderId id);

    IReadOnlyList<Order> Orders(Venue? venue = null, MarketKey? marketKey = null, StrategyId? strategyId = null, OrderSide? side = null);

    IReadOnlyList<Order> OrdersOpen(Venue? venue = null, MarketKey? marketKey = null, StrategyId? strategyId = null, OrderSide? side = null);

    IReadOnlyList<Order> OrdersClosed(Venue? venue = null, MarketKey? marketKey = null, StrategyId? strategyId = null, OrderSide? side = null);

    IReadOnlyList<Order> OrdersEmulated(Venue? venue = null, MarketKey? marketKey = null, StrategyId? strategyId = null, OrderSide? side = null);

    IReadOnlyList<Order> OrdersInflight(Venue? venue = null, MarketKey? marketKey = null, StrategyId? strategyId = null, OrderSide? side = null);

    IReadOnlyList<Order> OrdersForPosition(PositionId positionId);

    IReadOnlyList<Order> OrdersForOrderSchedule(OrderScheduleId orderScheduleId, Venue? venue = null, MarketKey? marketKey = null, StrategyId? strategyId = null);

    IReadOnlyList<Order> OrdersForExecSpawn(ClientOrderId execSpawnId);

    bool OrderExists(ClientOrderId id);

    bool IsOrderOpen(ClientOrderId id);

    bool IsOrderClosed(ClientOrderId id);

    bool IsOrderEmulated(ClientOrderId id);

    bool IsOrderInflight(ClientOrderId id);

    bool IsOrderPendingCancelLocal(ClientOrderId id);

    int OrdersOpenCount(Venue? venue = null, MarketKey? marketKey = null, StrategyId? strategyId = null, OrderSide? side = null);

    int OrdersClosedCount(Venue? venue = null, MarketKey? marketKey = null, StrategyId? strategyId = null, OrderSide? side = null);

    int OrdersTotalCount(Venue? venue = null, MarketKey? marketKey = null, StrategyId? strategyId = null, OrderSide? side = null);

    OrderList? OrderList(OrderListId id);

    IReadOnlyList<OrderList> OrderLists(Venue? venue = null, MarketKey? marketKey = null, StrategyId? strategyId = null);

    bool OrderListExists(OrderListId id);

    // Positions
    Position? Position(PositionId id);

    Position? PositionForOrder(ClientOrderId clientOrderId);

    PositionId? PositionIdFor(ClientOrderId clientOrderId);

    IReadOnlyList<Position> Positions(Venue? venue = null, MarketKey? marketKey = null, StrategyId? strategyId = null, PositionSide? side = null);

    IReadOnlyList<Position> PositionsOpen(Venue? venue = null, MarketKey? marketKey = null, StrategyId? strategyId = null, PositionSide? side = null);

    IReadOnlyList<Position> PositionsClosed(Venue? venue = null, MarketKey? marketKey = null, StrategyId? strategyId = null);

    /// <summary>
    /// Keeps what a position looked like at this moment (R6.6), measured against <paramref name="markPrice"/> where one
    /// is given - or against the last price the cache holds for its instrument where it is not.
    ///
    /// <para>
    /// What it is for is the unrealised figure: a position's fills are kept, so everything realised can be worked out
    /// again, and what it was worth while it was open cannot, because that needs the price at that moment.
    /// </para>
    /// </summary>
    PositionSnapshot SnapshotPosition(Position position, Price? markPrice = null);

    /// <summary>Every snapshot taken, oldest first, narrowed by whichever of these is given.</summary>
    IReadOnlyList<PositionSnapshot> PositionSnapshots(PositionId? positionId = null, MarketKey? marketKey = null, StrategyId? strategyId = null);

    bool PositionExists(PositionId id);

    bool IsPositionOpen(PositionId id);

    bool IsPositionClosed(PositionId id);

    int PositionsOpenCount(Venue? venue = null, MarketKey? marketKey = null, StrategyId? strategyId = null, PositionSide? side = null);

    int PositionsClosedCount(Venue? venue = null, MarketKey? marketKey = null, StrategyId? strategyId = null);

    int PositionsTotalCount(Venue? venue = null, MarketKey? marketKey = null, StrategyId? strategyId = null, PositionSide? side = null);

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

    // RuntimeModule and strategy state
    void SaveRuntimeModuleState(RuntimeModuleId runtimeModuleId, IDictionary<string, byte[]> state);

    IDictionary<string, byte[]>? LoadRuntimeModuleState(RuntimeModuleId runtimeModuleId);
}
