using Bytex.Core.Caching;
using Bytex.Core.Common;
using Bytex.Core.Messaging;
using Bytex.Core.Model;
using Bytex.Core.Model.Accounts;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Portfolios;
using Microsoft.Extensions.Logging;

namespace Bytex.Core.Engines;

public sealed record RiskEngineConfig
{
    /// <summary>Skip all checks (for trusted environments and some tests).</summary>
    public bool Bypass { get; init; }

    /// <summary>Maximum number of orders per <see cref="OrderRateInterval"/>.</summary>
    public int MaxOrderSubmitRate { get; init; } = 100;

    public TimeSpan OrderRateInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Maximum number of modify commands per <see cref="OrderRateInterval"/>.</summary>
    public int MaxOrderModifyRate { get; init; } = 100;

    /// <summary>Maximum notional per order by instrument, in the instrument's cost currency.</summary>
    public IReadOnlyDictionary<InstrumentId, decimal> MaxNotionalPerOrder { get; init; } = new Dictionary<InstrumentId, decimal>();

    /// <summary>Reject buy orders on cash accounts when free balance cannot cover them.</summary>
    public bool CheckCashBalance { get; init; } = true;

    public bool LogDenials { get; init; } = true;
}

/// <summary>
/// Validates trading commands before they reach the execution engine.
/// </summary>
public sealed class RiskEngine : Component
{
    private readonly IMessageBus _bus;
    private readonly Cache _cache;
    private readonly IPortfolio _portfolio;
    private readonly RiskEngineConfig _config;
    private readonly Queue<UnixNanos> _submitTimes = new();
    private readonly Queue<UnixNanos> _modifyTimes = new();

    public RiskEngine(IMessageBus bus, Cache cache, IPortfolio portfolio, RiskEngineConfig? config = null)
        : base(new ComponentId("RiskEngine"))
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(portfolio);
        _bus = bus;
        _cache = cache;
        _portfolio = portfolio;
        _config = config ?? new RiskEngineConfig();
        _bus.Register(Endpoints.RiskEngineExecute, Execute);
    }

    public RiskEngineConfig Config => _config;

    public TradingState TradingState { get; private set; } = TradingState.Active;

    public bool IsBypassed => _config.Bypass;

    public long CommandCount { get; private set; }

    public long DeniedCount { get; private set; }

    public void SetTradingState(TradingState state)
    {
        TradingState = state;
        Log.LogWarning("Trading state set to {State}", state);
    }

    protected override void OnReset()
    {
        _submitTimes.Clear();
        _modifyTimes.Clear();
        CommandCount = 0;
        DeniedCount = 0;
        TradingState = TradingState.Active;
    }

    public void Execute(object message)
    {
        CommandCount++;
        switch (message)
        {
            case SubmitOrder submit:
                HandleSubmitOrder(submit);
                break;
            case SubmitOrderList list:
                HandleSubmitOrderList(list);
                break;
            case ModifyOrder modify:
                HandleModifyOrder(modify);
                break;
            case TradingCommand other:
                Forward(other);
                break;
            default:
                Log.LogError("RiskEngine cannot handle {MessageType}", message.GetType().Name);
                break;
        }
    }

    private void HandleSubmitOrder(SubmitOrder command)
    {
        Order order = command.Order;
        if (_config.Bypass)
        {
            Forward(command);
            return;
        }

        if (TradingState == TradingState.Halted)
        {
            Deny(order, "TRADING_HALTED");
            return;
        }

        Instrument? instrument = _cache.Instrument(order.InstrumentId);
        if (instrument is null)
        {
            Deny(order, $"Instrument {order.InstrumentId} not found in cache");
            return;
        }

        if (TradingState == TradingState.Reducing && !WouldReduce(order))
        {
            Deny(order, "TRADING_REDUCING: order would not reduce a position");
            return;
        }

        string? reason = CheckOrder(instrument, order);
        if (reason is not null)
        {
            Deny(order, reason);
            return;
        }

        if (!CheckRate(_submitTimes, _config.MaxOrderSubmitRate))
        {
            Deny(order, $"Exceeded max order submit rate of {_config.MaxOrderSubmitRate} per {_config.OrderRateInterval}");
            return;
        }

        Forward(command);
    }

    private void HandleSubmitOrderList(SubmitOrderList command)
    {
        if (_config.Bypass)
        {
            Forward(command);
            return;
        }

        if (TradingState == TradingState.Halted)
        {
            foreach (Order order in command.OrderList.Orders)
            {
                Deny(order, "TRADING_HALTED");
            }

            return;
        }

        foreach (Order order in command.OrderList.Orders)
        {
            Instrument? instrument = _cache.Instrument(order.InstrumentId);
            string? reason = instrument is null ? $"Instrument {order.InstrumentId} not found in cache" : CheckOrder(instrument, order);
            if (reason is not null)
            {
                foreach (Order o in command.OrderList.Orders)
                {
                    Deny(o, reason);
                }

                return;
            }
        }

        if (!CheckRate(_submitTimes, _config.MaxOrderSubmitRate))
        {
            foreach (Order o in command.OrderList.Orders)
            {
                Deny(o, $"Exceeded max order submit rate of {_config.MaxOrderSubmitRate} per {_config.OrderRateInterval}");
            }

            return;
        }

        Forward(command);
    }

    private void HandleModifyOrder(ModifyOrder command)
    {
        if (_config.Bypass)
        {
            Forward(command);
            return;
        }

        Order? order = _cache.Order(command.ClientOrderId);
        if (order is null)
        {
            Log.LogError("Cannot modify {ClientOrderId}: order not found", command.ClientOrderId);
            return;
        }

        if (order.IsClosed)
        {
            RejectModify(order, "order already closed");
            return;
        }

        if (order.IsPending)
        {
            RejectModify(order, $"order already {order.Status}");
            return;
        }

        Instrument? instrument = _cache.Instrument(order.InstrumentId);
        if (instrument is not null)
        {
            if (command.Quantity is { } quantity)
            {
                string? qtyReason = CheckQuantity(instrument, quantity);
                if (qtyReason is not null)
                {
                    RejectModify(order, qtyReason);
                    return;
                }
            }

            if (command.Price is { } price)
            {
                string? pxReason = CheckPrice(instrument, price);
                if (pxReason is not null)
                {
                    RejectModify(order, pxReason);
                    return;
                }
            }

            if (command.TriggerPrice is { } trigger)
            {
                string? trReason = CheckPrice(instrument, trigger);
                if (trReason is not null)
                {
                    RejectModify(order, trReason);
                    return;
                }
            }
        }

        if (!CheckRate(_modifyTimes, _config.MaxOrderModifyRate))
        {
            RejectModify(order, $"Exceeded max order modify rate of {_config.MaxOrderModifyRate} per {_config.OrderRateInterval}");
            return;
        }

        Forward(command);
    }

    private string? CheckOrder(Instrument instrument, Order order)
    {
        string? qty = CheckQuantity(instrument, order.Quantity);
        if (qty is not null)
        {
            return qty;
        }

        if (order.Price is { } price)
        {
            string? px = CheckPrice(instrument, price);
            if (px is not null)
            {
                return px;
            }
        }

        if (order.TriggerPrice is { } trigger)
        {
            string? tp = CheckPrice(instrument, trigger);
            if (tp is not null)
            {
                return tp;
            }
        }

        Price? reference = order.Price ?? order.TriggerPrice ?? _cache.Price(order.InstrumentId, order.IsBuy ? PriceType.Ask : PriceType.Bid) ?? _cache.Price(order.InstrumentId, PriceType.Last);
        if (reference is { } refPx)
        {
            Money notional = instrument.NotionalValue(order.Quantity, refPx);
            if (instrument.MaxNotional is { } maxNotional && notional.Currency.Equals(maxNotional.Currency) && notional.Amount > maxNotional.Amount)
            {
                return $"NOTIONAL_EXCEEDS_MAX: {notional} > {maxNotional}";
            }

            if (instrument.MinNotional is { } minNotional && notional.Currency.Equals(minNotional.Currency) && notional.Amount < minNotional.Amount)
            {
                return $"NOTIONAL_LESS_THAN_MIN: {notional} < {minNotional}";
            }

            if (_config.MaxNotionalPerOrder.TryGetValue(order.InstrumentId, out decimal maxPerOrder) && notional.Amount > maxPerOrder)
            {
                return $"NOTIONAL_EXCEEDS_MAX_PER_ORDER: {notional} > {maxPerOrder}";
            }

            if (_config.CheckCashBalance && _cache.AccountForVenue(order.InstrumentId.Venue) is CashAccount cash && !order.IsReduceOnly)
            {
                Money required = cash.CalculateBalanceLocked(instrument, order.Side, order.Quantity, refPx);
                Money? free = cash.BalanceFree(required.Currency);
                if (free is { } f && f.Amount < required.Amount)
                {
                    return $"INSUFFICIENT_BALANCE: {required} required, {f} free";
                }
            }
        }

        return null;
    }

    private static string? CheckQuantity(Instrument instrument, Quantity quantity)
    {
        if (quantity.Precision > instrument.SizePrecision)
        {
            return $"QUANTITY_PRECISION: {quantity.Precision} > instrument size precision {instrument.SizePrecision}";
        }

        if (quantity.IsZero)
        {
            return "QUANTITY_ZERO";
        }

        if (instrument.MaxQuantity is { } max && quantity > max)
        {
            return $"QUANTITY_EXCEEDS_MAX: {quantity} > {max}";
        }

        if (instrument.MinQuantity is { } min && quantity < min)
        {
            return $"QUANTITY_LESS_THAN_MIN: {quantity} < {min}";
        }

        return null;
    }

    private static string? CheckPrice(Instrument instrument, Price price)
    {
        if (price.Precision > instrument.PricePrecision)
        {
            return $"PRICE_PRECISION: {price.Precision} > instrument price precision {instrument.PricePrecision}";
        }

        if (!price.IsPositive)
        {
            return "PRICE_NOT_POSITIVE";
        }

        if (instrument.MaxPrice is { } max && price > max)
        {
            return $"PRICE_EXCEEDS_MAX: {price} > {max}";
        }

        if (instrument.MinPrice is { } min && price < min)
        {
            return $"PRICE_LESS_THAN_MIN: {price} < {min}";
        }

        return null;
    }

    private bool WouldReduce(Order order)
    {
        decimal net = _portfolio.NetPosition(order.InstrumentId);
        if (net == 0m)
        {
            return false;
        }

        bool opposite = (net > 0m && order.IsSell) || (net < 0m && order.IsBuy);
        return opposite && order.Quantity.Value <= Math.Abs(net);
    }

    private bool CheckRate(Queue<UnixNanos> times, int max)
    {
        UnixNanos now = Clock.Timestamp;
        UnixNanos cutoff = now - _config.OrderRateInterval;
        while (times.Count > 0 && times.Peek() < cutoff)
        {
            times.Dequeue();
        }

        if (times.Count >= max)
        {
            return false;
        }

        times.Enqueue(now);
        return true;
    }

    private void Deny(Order order, string reason)
    {
        DeniedCount++;
        if (_config.LogDenials)
        {
            Log.LogWarning("Denied {ClientOrderId}: {Reason}", order.ClientOrderId, reason);
        }

        OrderDenied denied = new(order.TraderId, order.StrategyId, order.InstrumentId, order.ClientOrderId, reason, Guid.NewGuid(), Clock.Timestamp, Clock.Timestamp);
        _bus.Send(Endpoints.ExecutionEngineProcess, denied);
    }

    private void RejectModify(Order order, string reason)
    {
        DeniedCount++;
        if (_config.LogDenials)
        {
            Log.LogWarning("Modify rejected for {ClientOrderId}: {Reason}", order.ClientOrderId, reason);
        }

        OrderModifyRejected rejected = new(order.TraderId, order.StrategyId, order.InstrumentId, order.ClientOrderId, order.VenueOrderId, order.AccountId, reason, Guid.NewGuid(), Clock.Timestamp, Clock.Timestamp);
        _bus.Send(Endpoints.ExecutionEngineProcess, rejected);
    }

    private void Forward(TradingCommand command) => _bus.Send(Endpoints.ExecutionEngineExecute, command);
}
