using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Core.Model.Positions;

/// <summary>
/// A position in an instrument, built by applying fills. Tracks average prices, realized P&amp;L, and commissions.
/// </summary>
public sealed class Position
{
    private readonly List<OrderFilled> _events = new();
    private readonly List<TradeId> _tradeIds = new();
    private readonly List<ClientOrderId> _clientOrderIds = new();
    private readonly Dictionary<Currency, Money> _commissions = new();
    private decimal _signedQuantity;
    private decimal _closedQuantity;

    public Position(Instrument instrument, OrderFilled fill)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        ArgumentNullException.ThrowIfNull(fill);
        if (fill.PositionId is null)
        {
            throw new ArgumentException("Fill must carry a position id.", nameof(fill));
        }

        Instrument = instrument;
        Id = fill.PositionId.Value;
        TraderId = fill.TraderId;
        StrategyId = fill.StrategyId;
        InstrumentId = fill.InstrumentId;
        AccountId = fill.AccountId ?? throw new ArgumentException("Fill must carry an account id.", nameof(fill));
        OpeningOrderId = fill.ClientOrderId;
        EntrySide = fill.OrderSide.ToPositionSide();
        SettlementCurrency = instrument.SettlementCurrency;
        RealizedPnl = Money.Zero(SettlementCurrency);
        Quantity = Quantity.Zero(instrument.SizePrecision);
        PeakQuantity = Quantity.Zero(instrument.SizePrecision);
        BuyQuantity = Quantity.Zero(instrument.SizePrecision);
        SellQuantity = Quantity.Zero(instrument.SizePrecision);
        TsInit = fill.TsInit;
        TsOpened = fill.TsEvent;
        TsLast = fill.TsEvent;
        Apply(fill);
    }

    public PositionId Id { get; }

    public TraderId TraderId { get; }

    public StrategyId StrategyId { get; }

    public InstrumentId InstrumentId { get; }

    public AccountId AccountId { get; }

    public Instrument Instrument { get; }

    public ClientOrderId OpeningOrderId { get; }

    public ClientOrderId? ClosingOrderId { get; private set; }

    public PositionSide EntrySide { get; }

    public PositionSide Side { get; private set; }

    public Quantity Quantity { get; private set; }

    public Quantity PeakQuantity { get; private set; }

    public Quantity BuyQuantity { get; private set; }

    public Quantity SellQuantity { get; private set; }

    public decimal SignedQuantity => _signedQuantity;

    public decimal AvgPxOpen { get; private set; }

    public decimal? AvgPxClose { get; private set; }

    public Currency SettlementCurrency { get; }

    public Money RealizedPnl { get; private set; }

    /// <summary>Realized return as a fraction of the entry notional.</summary>
    public decimal RealizedReturn { get; private set; }

    public UnixNanos TsInit { get; }

    public UnixNanos TsOpened { get; }

    public UnixNanos? TsClosed { get; private set; }

    public UnixNanos TsLast { get; private set; }

    public TimeSpan? Duration => TsClosed is { } closed ? closed - TsOpened : null;

    public IReadOnlyList<OrderFilled> Events => _events;

    public OrderFilled LastEvent => _events[^1];

    public IReadOnlyList<TradeId> TradeIds => _tradeIds;

    public IReadOnlyList<ClientOrderId> ClientOrderIds => _clientOrderIds;

    public IReadOnlyDictionary<Currency, Money> Commissions => _commissions;

    public int EventCount => _events.Count;

    public bool IsOpen => Side != PositionSide.Flat;

    public bool IsClosed => Side == PositionSide.Flat;

    public bool IsLong => Side == PositionSide.Long;

    public bool IsShort => Side == PositionSide.Short;

    public bool IsInverse => Instrument.IsInverse;

    public Quantity LastQty => LastEvent.LastQty;

    public Price LastPx => LastEvent.LastPx;

    /// <summary>
    /// Applies a fill to the position.
    /// </summary>
    public void Apply(OrderFilled fill)
    {
        ArgumentNullException.ThrowIfNull(fill);
        if (fill.InstrumentId != InstrumentId)
        {
            throw new ArgumentException($"Fill instrument {fill.InstrumentId} does not match position {InstrumentId}.", nameof(fill));
        }

        if (_tradeIds.Contains(fill.TradeId))
        {
            return;
        }

        _events.Add(fill);
        _tradeIds.Add(fill.TradeId);
        if (!_clientOrderIds.Contains(fill.ClientOrderId))
        {
            _clientOrderIds.Add(fill.ClientOrderId);
        }

        if (_commissions.TryGetValue(fill.Commission.Currency, out Money existing))
        {
            _commissions[fill.Commission.Currency] = existing + fill.Commission;
        }
        else
        {
            _commissions[fill.Commission.Currency] = fill.Commission;
        }

        decimal lastQty = fill.LastQty.Value;
        decimal lastPx = fill.LastPx.Value;

        if (fill.IsBuy)
        {
            HandleBuy(lastQty, lastPx, fill);
            BuyQuantity = new Quantity(BuyQuantity.Value + lastQty, Quantity.Precision);
        }
        else
        {
            HandleSell(lastQty, lastPx, fill);
            SellQuantity = new Quantity(SellQuantity.Value + lastQty, Quantity.Precision);
        }

        Quantity = new Quantity(Math.Abs(_signedQuantity), Instrument.SizePrecision);
        if (Quantity > PeakQuantity)
        {
            PeakQuantity = Quantity;
        }

        Side = _signedQuantity > 0m ? PositionSide.Long : _signedQuantity < 0m ? PositionSide.Short : PositionSide.Flat;
        TsLast = fill.TsEvent;

        if (Side == PositionSide.Flat)
        {
            TsClosed = fill.TsEvent;
            ClosingOrderId = fill.ClientOrderId;
        }
        else
        {
            TsClosed = null;
            ClosingOrderId = null;
        }
    }

    private void HandleBuy(decimal lastQty, decimal lastPx, OrderFilled fill)
    {
        if (_signedQuantity >= 0m)
        {
            // Opening or increasing a long.
            AvgPxOpen = WeightedAverage(AvgPxOpen, _signedQuantity, lastPx, lastQty);
            ApplyCommissionOnly(fill.Commission);
        }
        else
        {
            // Reducing (or flipping) a short.
            decimal closeQty = Math.Min(lastQty, Math.Abs(_signedQuantity));
            AvgPxClose = WeightedAverage(AvgPxClose ?? 0m, _closedQuantity, lastPx, closeQty);
            _closedQuantity += closeQty;
            RealizedPnl += CalculateRealizedPnl(closeQty, lastPx, fill.Commission, isLong: false);
            decimal remainder = lastQty - closeQty;
            if (remainder > 0m)
            {
                AvgPxOpen = lastPx;
            }
        }

        _signedQuantity += lastQty;
        RealizedReturn = CalculateReturn();
    }

    private void HandleSell(decimal lastQty, decimal lastPx, OrderFilled fill)
    {
        if (_signedQuantity <= 0m)
        {
            // Opening or increasing a short.
            AvgPxOpen = WeightedAverage(AvgPxOpen, Math.Abs(_signedQuantity), lastPx, lastQty);
            ApplyCommissionOnly(fill.Commission);
        }
        else
        {
            // Reducing (or flipping) a long.
            decimal closeQty = Math.Min(lastQty, _signedQuantity);
            AvgPxClose = WeightedAverage(AvgPxClose ?? 0m, _closedQuantity, lastPx, closeQty);
            _closedQuantity += closeQty;
            RealizedPnl += CalculateRealizedPnl(closeQty, lastPx, fill.Commission, isLong: true);
            decimal remainder = lastQty - closeQty;
            if (remainder > 0m)
            {
                AvgPxOpen = lastPx;
            }
        }

        _signedQuantity -= lastQty;
        RealizedReturn = CalculateReturn();
    }

    private void ApplyCommissionOnly(Money commission)
    {
        if (commission.Currency.Equals(SettlementCurrency))
        {
            RealizedPnl -= commission;
        }
    }

    private Money CalculateRealizedPnl(decimal closeQty, decimal closePx, Money commission, bool isLong)
    {
        decimal pnl = CalculatePnl(AvgPxOpen, closePx, closeQty, isLong);
        Money result = new(pnl, SettlementCurrency);
        if (commission.Currency.Equals(SettlementCurrency))
        {
            result -= commission;
        }

        return result;
    }

    private decimal CalculatePnl(decimal openPx, decimal closePx, decimal quantity, bool isLong)
    {
        decimal multiplier = Instrument.Multiplier.Value;
        decimal raw;
        if (Instrument.IsInverse)
        {
            raw = ((1m / openPx) - (1m / closePx)) * quantity * multiplier;
        }
        else
        {
            raw = (closePx - openPx) * quantity * multiplier;
        }

        return isLong ? raw : -raw;
    }

    private decimal CalculateReturn()
    {
        if (AvgPxOpen == 0m || PeakQuantity.IsZero)
        {
            return 0m;
        }

        decimal notional = AvgPxOpen * PeakQuantity.Value * Instrument.Multiplier.Value;
        return notional == 0m ? 0m : RealizedPnl.Amount / notional;
    }

    private static decimal WeightedAverage(decimal currentAvg, decimal currentQty, decimal newPx, decimal newQty)
    {
        decimal total = Math.Abs(currentQty) + newQty;
        if (total == 0m)
        {
            return newPx;
        }

        return ((currentAvg * Math.Abs(currentQty)) + (newPx * newQty)) / total;
    }

    /// <summary>
    /// Unrealized P&amp;L at the given price, in the settlement currency.
    /// </summary>
    public Money UnrealizedPnl(Price lastPrice)
    {
        if (Side == PositionSide.Flat)
        {
            return Money.Zero(SettlementCurrency);
        }

        decimal multiplier = Instrument.Multiplier.Value;
        decimal pnl;
        if (Instrument.IsInverse)
        {
            decimal inverseDiff = (1m / AvgPxOpen) - (1m / lastPrice.Value);
            pnl = inverseDiff * Quantity.Value * multiplier;
            if (Side == PositionSide.Short)
            {
                pnl = -pnl;
            }
        }
        else
        {
            pnl = (lastPrice.Value - AvgPxOpen) * Quantity.Value * multiplier;
            if (Side == PositionSide.Short)
            {
                pnl = -pnl;
            }
        }

        return new Money(pnl, SettlementCurrency);
    }

    public Money TotalPnl(Price lastPrice) => RealizedPnl + UnrealizedPnl(lastPrice);

    public Money NotionalValue(Price lastPrice) => Instrument.NotionalValue(Quantity, lastPrice);

    public bool IsOppositeSide(OrderSide side) => (Side == PositionSide.Long && side == OrderSide.Sell) || (Side == PositionSide.Short && side == OrderSide.Buy);

    public override string ToString() => $"Position({Side.ToString().ToUpperInvariant()} {Quantity} {InstrumentId}, id={Id}, avg_px_open={AvgPxOpen}, realized_pnl={RealizedPnl})";
}
