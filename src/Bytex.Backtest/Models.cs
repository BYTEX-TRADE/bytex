using Bytex.Core.Model;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;

namespace Bytex.Backtest;

/// <summary>
/// Probabilistic fill behaviour for the simulated venue. Deterministic for a given seed.
/// </summary>
public sealed class FillModel
{
    private readonly Random _random;

    /// <param name="probFillOnLimit">Probability that a resting limit order fills when the market touches (but does not trade through) its price.</param>
    /// <param name="probFillOnStop">Probability that a triggered stop fills at the trigger price rather than one tick worse.</param>
    /// <param name="probSlippage">Probability that a market order slips one tick beyond the best price.</param>
    /// <param name="seed">Seed for the random source; fixed by default for reproducibility.</param>
    public FillModel(decimal probFillOnLimit = 1m, decimal probFillOnStop = 1m, decimal probSlippage = 0m, int seed = 42)
    {
        Validate(probFillOnLimit);
        Validate(probFillOnStop);
        Validate(probSlippage);
        ProbFillOnLimit = probFillOnLimit;
        ProbFillOnStop = probFillOnStop;
        ProbSlippage = probSlippage;
        _random = new Random(seed);
    }

    public decimal ProbFillOnLimit { get; }

    public decimal ProbFillOnStop { get; }

    public decimal ProbSlippage { get; }

    public bool IsLimitFilled() => Roll(ProbFillOnLimit);

    public bool IsStopFilled() => Roll(ProbFillOnStop);

    public bool IsSlipped() => Roll(ProbSlippage);

    private bool Roll(decimal probability) => probability >= 1m || (probability > 0m && (decimal)_random.NextDouble() < probability);

    private static void Validate(decimal p)
    {
        if (p < 0m || p > 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(p), "Probability must be in [0, 1].");
        }
    }
}

/// <summary>
/// Computes commissions for simulated fills.
/// </summary>
public abstract class FeeModel
{
    public abstract Money Commission(Instrument instrument, Order order, Quantity lastQty, Price lastPx, LiquiditySide liquiditySide);
}

/// <summary>
/// Uses the instrument's maker and taker fee rates.
/// </summary>
public sealed class MakerTakerFeeModel : FeeModel
{
    public override Money Commission(Instrument instrument, Order order, Quantity lastQty, Price lastPx, LiquiditySide liquiditySide) =>
        instrument.CalculateCommission(lastQty, lastPx, liquiditySide);
}

/// <summary>
/// Charges a fixed amount per fill regardless of size.
/// </summary>
public sealed class FixedFeeModel : FeeModel
{
    private readonly Money _fee;

    public FixedFeeModel(Money fee) => _fee = fee;

    public override Money Commission(Instrument instrument, Order order, Quantity lastQty, Price lastPx, LiquiditySide liquiditySide) => _fee;
}

/// <summary>
/// Applies a flat percentage of notional to every fill.
/// </summary>
public sealed class PercentFeeModel : FeeModel
{
    private readonly decimal _rate;

    public PercentFeeModel(decimal rate) => _rate = rate;

    public override Money Commission(Instrument instrument, Order order, Quantity lastQty, Price lastPx, LiquiditySide liquiditySide)
    {
        Money notional = instrument.NotionalValue(lastQty, lastPx);
        return new Money(notional.Amount * _rate, notional.Currency);
    }
}

/// <summary>
/// Delays between a command leaving the engine and the venue acting on it.
/// </summary>
public sealed record LatencyModel(TimeSpan Base, TimeSpan Insert, TimeSpan Update, TimeSpan Cancel)
{
    public static readonly LatencyModel Zero = new(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);

    public static LatencyModel Uniform(TimeSpan latency) => new(latency, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);

    public TimeSpan InsertLatency => Base + Insert;

    public TimeSpan UpdateLatency => Base + Update;

    public TimeSpan CancelLatency => Base + Cancel;

    public bool IsZero => Base == TimeSpan.Zero && Insert == TimeSpan.Zero && Update == TimeSpan.Zero && Cancel == TimeSpan.Zero;
}

/// <summary>
/// How bars are replayed through the matching engine.
/// </summary>
public enum BarExecutionMode
{
    /// <summary>Open, then the extreme closer to the open, then the other extreme, then close.</summary>
    OhlcPath = 1,

    /// <summary>Only the close price is used.</summary>
    CloseOnly = 2,
}
