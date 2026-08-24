using Bytex.Core.Caching;
using Bytex.Core.Messaging;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Portfolios;
using Bytex.Core.Timing;
using Microsoft.Extensions.Logging;

namespace Bytex.Core.Trading;

public record ExecAlgorithmConfig : ActorConfig
{
    public ExecAlgorithmId? ExecAlgorithmId { get; init; }
}

/// <summary>
/// Receives parent orders routed by execution algorithm id and works them by spawning child orders.
/// </summary>
public abstract class ExecAlgorithm : Actor
{
    private readonly Dictionary<ClientOrderId, int> _spawnCounts = new();

    protected ExecAlgorithm(ExecAlgorithmConfig? config = null)
        : base(config ?? new ExecAlgorithmConfig())
    {
        Config = (ExecAlgorithmConfig)base.Config;
        ExecAlgorithmId = Config.ExecAlgorithmId ?? new ExecAlgorithmId(GetType().Name);
        ActorId = new ActorId(ExecAlgorithmId.Value);
        Id = new ComponentId(ExecAlgorithmId.Value);
    }

    public new ExecAlgorithmConfig Config { get; }

    public ExecAlgorithmId ExecAlgorithmId { get; }

    /// <summary>Called when a parent order carrying this algorithm's id is submitted.</summary>
    protected abstract void OnOrder(Order order);

    protected virtual void OnOrderEvent(OrderEvent e)
    {
    }

    public void HandleCommand(object message)
    {
        if (message is SubmitOrder submit)
        {
            Guarded(() => OnOrder(submit.Order));
        }
        else if (message is SubmitOrderList list)
        {
            foreach (Order order in list.OrderList.Orders)
            {
                Guarded(() => OnOrder(order));
            }
        }
    }

    public void HandleOrderEvent(OrderEvent e) => Guarded(() =>
    {
        OnOrderEvent(e);
        OnEvent(e);
    });

    private ClientOrderId SpawnId(Order primary)
    {
        int count = _spawnCounts.GetValueOrDefault(primary.ClientOrderId) + 1;
        _spawnCounts[primary.ClientOrderId] = count;
        return new ClientOrderId($"{primary.ClientOrderId}-E{count}");
    }

    private OrderParams SpawnParams(Order primary, Quantity quantity, TimeInForce tif, bool reduceOnly, IReadOnlyList<string>? tags) => new()
    {
        TraderId = primary.TraderId,
        StrategyId = primary.StrategyId,
        InstrumentId = primary.InstrumentId,
        ClientOrderId = SpawnId(primary),
        Side = primary.Side,
        Quantity = quantity,
        TimeInForce = tif,
        ReduceOnly = reduceOnly,
        ExecAlgorithmId = ExecAlgorithmId,
        ExecSpawnId = primary.ClientOrderId,
        Tags = tags ?? primary.Tags,
        InitId = Guid.NewGuid(),
        TsInit = Clock.Timestamp,
    };

    protected MarketOrder SpawnMarket(Order primary, Quantity quantity, TimeInForce timeInForce = TimeInForce.Gtc, bool reduceOnly = false, IReadOnlyList<string>? tags = null)
    {
        ArgumentNullException.ThrowIfNull(primary);
        return MarketOrder.Create(SpawnParams(primary, quantity, timeInForce, reduceOnly, tags));
    }

    protected LimitOrder SpawnLimit(Order primary, Quantity quantity, Price price, TimeInForce timeInForce = TimeInForce.Gtc, UnixNanos? expireTime = null, bool postOnly = false, bool reduceOnly = false, IReadOnlyList<string>? tags = null)
    {
        ArgumentNullException.ThrowIfNull(primary);
        return LimitOrder.Create(SpawnParams(primary, quantity, timeInForce, reduceOnly, tags) with { PostOnly = postOnly }, price, expireTime);
    }

    protected MarketToLimitOrder SpawnMarketToLimit(Order primary, Quantity quantity, TimeInForce timeInForce = TimeInForce.Gtc, UnixNanos? expireTime = null, bool reduceOnly = false, IReadOnlyList<string>? tags = null)
    {
        ArgumentNullException.ThrowIfNull(primary);
        return MarketToLimitOrder.Create(SpawnParams(primary, quantity, timeInForce, reduceOnly, tags), expireTime);
    }

    protected void SubmitOrder(Order order, PositionId? positionId = null, ClientId? clientId = null)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (!Cache.OrderExists(order.ClientOrderId))
        {
            ((Cache)Cache).AddOrder(order, positionId);
        }

        MessageBus.Send(Endpoints.RiskEngineExecute, new SubmitOrder(order.TraderId, order.StrategyId, order, positionId, null, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void ModifyOrder(Order order, Quantity? quantity = null, Price? price = null, Price? triggerPrice = null, ClientId? clientId = null)
    {
        ArgumentNullException.ThrowIfNull(order);
        MessageBus.Send(Endpoints.RiskEngineExecute, new ModifyOrder(order.TraderId, order.StrategyId, order.InstrumentId, order.ClientOrderId, order.VenueOrderId, quantity, price, triggerPrice, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    protected void CancelOrder(Order order, ClientId? clientId = null)
    {
        ArgumentNullException.ThrowIfNull(order);
        MessageBus.Send(Endpoints.RiskEngineExecute, new CancelOrder(order.TraderId, order.StrategyId, order.InstrumentId, order.ClientOrderId, order.VenueOrderId, clientId, Guid.NewGuid(), Clock.Timestamp));
    }

    public override void Register(TraderId traderId, IClock clock, ICache cache, IMessageBus bus, IPortfolio portfolio, ILoggerFactory loggerFactory, Action<Action>? post = null)
    {
        base.Register(traderId, clock, cache, bus, portfolio, loggerFactory, post);
        bus.Subscribe(Topics.AllOrderEvents, m =>
        {
            if (m is OrderEvent e && cache.Order(e.ClientOrderId) is { ExecAlgorithmId: { } alg } && alg == ExecAlgorithmId)
            {
                HandleOrderEvent(e);
            }
        });
    }
}
