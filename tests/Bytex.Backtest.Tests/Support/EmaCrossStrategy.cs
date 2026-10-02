using System.Text.Json;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Plugins;
using Bytex.Core.Trading;
using Bytex.Indicators;

namespace Bytex.Backtest.Tests.Support;

public sealed record EmaCrossStrategyConfig : StrategyConfig
{
    public required MarketKey MarketKey { get; init; }

    public required CandleSeries CandleSeries { get; init; }

    public int FastPeriod { get; init; } = 1;

    public int SlowPeriod { get; init; } = 3;

    public decimal TradeSize { get; init; } = 1m;
}

/// <summary>
/// Long-only EMA cross, the same shape as the strategy in the getting-started guide: buy when flat and fast &gt; slow,
/// close when long and fast &lt; slow, flatten on stop. Signals are evaluated on the bar that has just closed and the
/// market order trades at that bar's close.
/// </summary>
public sealed class EmaCrossStrategy : Strategy<EmaCrossStrategyConfig>
{
    private readonly ExponentialMovingAverage _fast;
    private readonly ExponentialMovingAverage _slow;

    public EmaCrossStrategy(EmaCrossStrategyConfig config)
        : base(config)
    {
        _fast = new ExponentialMovingAverage(config.FastPeriod);
        _slow = new ExponentialMovingAverage(config.SlowPeriod);
    }

    protected override void OnStart()
    {
        RegisterIndicatorForBars(Config.CandleSeries, _fast);
        RegisterIndicatorForBars(Config.CandleSeries, _slow);
        SubscribeBars(Config.CandleSeries);
    }

    protected override void OnBar(Bar bar)
    {
        if (!IndicatorsInitialized)
        {
            return;
        }

        Instrument instrument = Cache.Instrument(Config.MarketKey) ?? throw new InvalidOperationException("Instrument missing from cache.");
        if (_fast.Value > _slow.Value && Portfolio.IsFlat(Config.MarketKey))
        {
            SubmitOrder(OrderFactory.Market(Config.MarketKey, OrderSide.Buy, instrument.MakeQuantity(Config.TradeSize)));
        }
        else if (_fast.Value < _slow.Value && Portfolio.IsNetLong(Config.MarketKey))
        {
            CloseAllPositions(Config.MarketKey);
        }
    }

    protected override void OnStop()
    {
        CancelAllOrders(Config.MarketKey);
        CloseAllPositions(Config.MarketKey);
    }

    protected override void OnReset()
    {
        _fast.Reset();
        _slow.Reset();
    }
}

/// <summary>
/// Plugin provider so that <see cref="BacktestNode"/> can build the strategy from a JSON definition.
/// Payload: { "marketKey": "...", "candleSeries": "...", "tradeSize": 2, "strategyId": "Ema-001" }.
/// </summary>
public sealed class EmaCrossProvider : IStrategyProvider
{
    public const string ProviderId = "tests.ema";

    public string Id => ProviderId;

    public static StrategyDefinition Definition(MarketKey marketKey, CandleSeries candleSeries, decimal tradeSize, string strategyId = "Ema-001")
    {
        JsonElement payload = JsonSerializer.SerializeToElement(new
        {
            marketKey = marketKey.Value,
            candleSeries = candleSeries.ToString(),
            tradeSize,
            strategyId,
        });
        return new StrategyDefinition(ProviderId, "ema-cross", payload);
    }

    public IReadOnlyList<StrategyDescriptor> Describe() => [new StrategyDescriptor("ema-cross", "EMA cross (tests)", null, null)];

    public ValidationResult Validate(StrategyDefinition definition) =>
        definition.Payload.TryGetProperty("marketKey", out _) ? ValidationResult.Valid : ValidationResult.Invalid("marketKey is required");

    public Strategy Create(StrategyDefinition definition)
    {
        JsonElement p = definition.Payload;
        return new EmaCrossStrategy(new EmaCrossStrategyConfig
        {
            StrategyId = new StrategyId(p.GetProperty("strategyId").GetString()!),
            MarketKey = MarketKey.Parse(p.GetProperty("marketKey").GetString()!),
            CandleSeries = CandleSeries.Parse(p.GetProperty("candleSeries").GetString()!),
            TradeSize = p.GetProperty("tradeSize").GetDecimal(),
        });
    }
}
