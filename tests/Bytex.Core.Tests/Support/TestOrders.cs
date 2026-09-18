using Bytex.Core.Model;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;

namespace Bytex.Core.Tests.Support;

/// <summary>
/// Builds orders with explicit client order ids so that tests never depend on a generator.
/// </summary>
internal static class TestOrders
{
    public static UnixNanos T0 => UnixNanos.Parse("2024-01-01T00:00:00Z");

    public static OrderParams Params(
        string clientOrderId,
        InstrumentId instrumentId,
        OrderSide side,
        string quantity,
        StrategyId? strategyId = null,
        TimeInForce timeInForce = TimeInForce.Gtc,
        bool reduceOnly = false,
        bool quoteQuantity = false,
        ContingencyType contingency = ContingencyType.None,
        IReadOnlyList<ClientOrderId>? linked = null,
        ClientOrderId? parent = null,
        ExecAlgorithmId? execAlgorithmId = null,
        ClientOrderId? execSpawnId = null,
        UnixNanos? tsInit = null) => new()
        {
            TraderId = TestIds.Trader,
            StrategyId = strategyId ?? TestIds.Strategy,
            InstrumentId = instrumentId,
            ClientOrderId = new ClientOrderId(clientOrderId),
            Side = side,
            Quantity = Quantity.Parse(quantity),
            TimeInForce = timeInForce,
            ReduceOnly = reduceOnly,
            QuoteQuantity = quoteQuantity,
            Contingency = contingency,
            LinkedOrderIds = linked ?? [],
            ParentOrderId = parent,
            ExecAlgorithmId = execAlgorithmId,
            ExecSpawnId = execSpawnId,
            InitId = Guid.NewGuid(),
            TsInit = tsInit ?? T0,
        };

    public static MarketOrder Market(string clientOrderId, InstrumentId instrumentId, OrderSide side, string quantity, StrategyId? strategyId = null, bool reduceOnly = false, bool quoteQuantity = false, UnixNanos? tsInit = null) =>
        MarketOrder.Create(Params(clientOrderId, instrumentId, side, quantity, strategyId, reduceOnly: reduceOnly, quoteQuantity: quoteQuantity, tsInit: tsInit));

    public static LimitOrder Limit(string clientOrderId, InstrumentId instrumentId, OrderSide side, string quantity, string price, StrategyId? strategyId = null, bool reduceOnly = false, bool quoteQuantity = false, UnixNanos? tsInit = null) =>
        LimitOrder.Create(Params(clientOrderId, instrumentId, side, quantity, strategyId, reduceOnly: reduceOnly, quoteQuantity: quoteQuantity, tsInit: tsInit), Price.Parse(price));

    public static StopMarketOrder StopMarket(string clientOrderId, InstrumentId instrumentId, OrderSide side, string quantity, string trigger, StrategyId? strategyId = null) =>
        StopMarketOrder.Create(Params(clientOrderId, instrumentId, side, quantity, strategyId), Price.Parse(trigger));

    public static StopLimitOrder StopLimit(string clientOrderId, InstrumentId instrumentId, OrderSide side, string quantity, string price, string trigger) =>
        StopLimitOrder.Create(Params(clientOrderId, instrumentId, side, quantity), Price.Parse(price), Price.Parse(trigger));
}

/// <summary>
/// Builds the order events a venue would send for an order. The account is always "{VENUE}-001".
/// </summary>
internal static class TestEvents
{
    public static AccountId AccountFor(Order order) => new($"{order.InstrumentId.Venue}-001");

    public static OrderSubmitted Submitted(Order order, UnixNanos? ts = null) =>
        new(order.TraderId, order.StrategyId, order.InstrumentId, order.ClientOrderId, AccountFor(order), Guid.NewGuid(), ts ?? TestOrders.T0, ts ?? TestOrders.T0);

    public static OrderAccepted Accepted(Order order, string venueOrderId, UnixNanos? ts = null) =>
        new(order.TraderId, order.StrategyId, order.InstrumentId, order.ClientOrderId, new VenueOrderId(venueOrderId), AccountFor(order), Guid.NewGuid(), ts ?? TestOrders.T0, ts ?? TestOrders.T0);

    public static OrderRejected Rejected(Order order, string reason) =>
        new(order.TraderId, order.StrategyId, order.InstrumentId, order.ClientOrderId, AccountFor(order), reason, Guid.NewGuid(), TestOrders.T0, TestOrders.T0);

    public static OrderCanceled Canceled(Order order, UnixNanos? ts = null) =>
        new(order.TraderId, order.StrategyId, order.InstrumentId, order.ClientOrderId, order.VenueOrderId, AccountFor(order), Guid.NewGuid(), ts ?? TestOrders.T0, ts ?? TestOrders.T0);

    public static OrderExpired Expired(Order order) =>
        new(order.TraderId, order.StrategyId, order.InstrumentId, order.ClientOrderId, order.VenueOrderId, AccountFor(order), Guid.NewGuid(), TestOrders.T0, TestOrders.T0);

    public static OrderPendingUpdate PendingUpdate(Order order) =>
        new(order.TraderId, order.StrategyId, order.InstrumentId, order.ClientOrderId, order.VenueOrderId, AccountFor(order), Guid.NewGuid(), TestOrders.T0, TestOrders.T0);

    public static OrderPendingCancel PendingCancel(Order order) =>
        new(order.TraderId, order.StrategyId, order.InstrumentId, order.ClientOrderId, order.VenueOrderId, AccountFor(order), Guid.NewGuid(), TestOrders.T0, TestOrders.T0);

    public static OrderUpdated Updated(Order order, string quantity, string? price = null) =>
        new(order.TraderId, order.StrategyId, order.InstrumentId, order.ClientOrderId, order.VenueOrderId, AccountFor(order), Quantity.Parse(quantity),
            price is null ? null : Price.Parse(price), null, Guid.NewGuid(), TestOrders.T0, TestOrders.T0);

    /// <summary>A fill in the instrument's quote currency (USDT for every test instrument that uses this helper).</summary>
    public static OrderFilled Filled(
        Order order,
        string tradeId,
        string lastQty,
        string lastPx,
        string commission = "0",
        Currency? currency = null,
        PositionId? positionId = null,
        UnixNanos? ts = null)
    {
        Currency quote = currency ?? Currencies.USDT;
        return new OrderFilled(
            order.TraderId, order.StrategyId, order.InstrumentId, order.ClientOrderId, order.VenueOrderId ?? new VenueOrderId("V-" + order.ClientOrderId.Value),
            AccountFor(order), new TradeId(tradeId), positionId, order.Side, order.Type, Quantity.Parse(lastQty), Price.Parse(lastPx), quote,
            Money.Parse($"{commission} {quote.Code}"), LiquiditySide.Taker, Guid.NewGuid(), ts ?? TestOrders.T0, ts ?? TestOrders.T0);
    }

    public static AccountState CashState(AccountId accountId, params (Currency Currency, decimal Total, decimal Locked)[] balances) =>
        State(accountId, AccountType.Cash, balances);

    public static AccountState MarginState(AccountId accountId, params (Currency Currency, decimal Total, decimal Locked)[] balances) =>
        State(accountId, AccountType.Margin, balances);

    private static AccountState State(AccountId accountId, AccountType type, (Currency Currency, decimal Total, decimal Locked)[] balances)
    {
        List<AccountBalance> list = new();
        foreach ((Currency currency, decimal total, decimal locked) in balances)
        {
            list.Add(AccountBalance.Of(new Money(total, currency), new Money(locked, currency)));
        }

        return new AccountState(accountId, type, null, true, list, [], new Dictionary<string, string>(StringComparer.Ordinal), Guid.NewGuid(), TestOrders.T0, TestOrders.T0);
    }
}
