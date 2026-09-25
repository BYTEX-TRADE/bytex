using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Trading;
using Bytex.Indicators;
using Microsoft.Extensions.Logging;

namespace Bytex.Examples.Strategies;

public sealed record EmaCrossConfig : StrategyConfig
{
    public required InstrumentId InstrumentId { get; init; }

    public required BarType BarType { get; init; }

    public int FastPeriod { get; init; } = 10;

    public int SlowPeriod { get; init; } = 20;

    public decimal TradeSize { get; init; } = 1m;
}

/// <summary>
/// Goes long when the fast EMA crosses above the slow EMA and flat when it crosses below.
/// </summary>
public sealed class EmaCross : Strategy<EmaCrossConfig>
{
    private readonly ExponentialMovingAverage _fast;
    private readonly ExponentialMovingAverage _slow;
    private Instrument? _instrument;

    public EmaCross(EmaCrossConfig config)
        : base(config)
    {
        _fast = new ExponentialMovingAverage(config.FastPeriod);
        _slow = new ExponentialMovingAverage(config.SlowPeriod);
    }

    protected override void OnStart()
    {
        _instrument = Cache.Instrument(Config.InstrumentId);
        if (_instrument is null)
        {
            Log.LogError("Instrument {InstrumentId} not found; stopping", Config.InstrumentId);
            Stop();
            return;
        }

        RegisterIndicatorForBars(Config.BarType, _fast);
        RegisterIndicatorForBars(Config.BarType, _slow);
        SubscribeBars(Config.BarType);
    }

    protected override void OnBar(Bar bar)
    {
        if (!_fast.IsInitialized || !_slow.IsInitialized || _instrument is null)
        {
            return;
        }

        if (_fast.Value > _slow.Value)
        {
            if (Portfolio.IsFlat(Config.InstrumentId))
            {
                SubmitOrder(OrderFactory.Market(Config.InstrumentId, OrderSide.Buy, _instrument.MakeQuantity(Config.TradeSize)));
            }
        }
        else if (_fast.Value < _slow.Value)
        {
            if (Portfolio.IsNetLong(Config.InstrumentId))
            {
                CloseAllPositions(Config.InstrumentId);
            }
        }
    }

    protected override void OnOrderFilled(OrderFilled e) =>
        Log.LogInformation("Filled {Side} {Qty} @ {Px} ({Liquidity})", e.OrderSide, e.LastQty, e.LastPx, e.LiquiditySide);

    protected override void OnStop()
    {
        CancelAllOrders(Config.InstrumentId);
        CloseAllPositions(Config.InstrumentId);
    }
}
