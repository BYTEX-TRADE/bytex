using Bytex.Core.Model;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;

namespace Bytex.Backtest.Tests.Support;

/// <summary>
/// Builds linked exit orders by hand. The order factory only produces contingencies through its bracket helper,
/// so a bare OCO pair has to be assembled from <see cref="OrderParams"/>.
/// </summary>
public static class Contingent
{
    /// <summary>A sell limit (take-profit) and a sell stop (stop-loss) that are linked to each other.</summary>
    public static OrderList SellExitPair(ScriptedStrategy strategy, MarketKey marketKey, Quantity quantity, Price takeProfit, Price stopTrigger, ContingencyType contingency = ContingencyType.Oco)
    {
        ClientOrderId limitId = strategy.Orders.GenerateClientOrderId();
        ClientOrderId stopId = strategy.Orders.GenerateClientOrderId();
        OrderListId listId = strategy.Orders.GenerateOrderListId();

        OrderParams Params(ClientOrderId id, ClientOrderId linked) => new()
        {
            ModuleHostId = strategy.ModuleHostId,
            StrategyId = strategy.StrategyId,
            MarketKey = marketKey,
            ClientOrderId = id,
            Side = OrderSide.Sell,
            Quantity = quantity,
            Contingency = contingency,
            OrderListId = listId,
            LinkedOrderIds = [linked],
            InitId = Guid.NewGuid(),
            CreatedTime = strategy.Now,
        };

        LimitOrder limit = LimitOrder.Create(Params(limitId, stopId), takeProfit);
        StopMarketOrder stop = StopMarketOrder.Create(Params(stopId, limitId), stopTrigger);
        return new OrderList(listId, [limit, stop]);
    }
}
