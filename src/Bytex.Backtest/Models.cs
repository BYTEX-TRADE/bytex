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
    /// <summary>
    /// The seed a fill model uses when none is given. A run has to roll the same dice as the last one, so the seed is
    /// fixed rather than drawn from the clock; any value would do, and this one is the joke every codebase makes.
    /// </summary>
    public const int DefaultSeed = 42;

    private readonly int _seed;
    private Random _random;

    /// <param name="probFillOnLimit">Probability that a resting limit order fills when the market touches (but does not trade through) its price.</param>
    /// <param name="probFillOnStop">Probability that a triggered stop fills at the trigger price rather than one tick worse.</param>
    /// <param name="probSlippage">Probability that a market order slips one tick beyond the best price.</param>
    /// <param name="seed">Seed for the random source; fixed by default for reproducibility.</param>
    public FillModel(decimal probFillOnLimit = 1m, decimal probFillOnStop = 1m, decimal probSlippage = 0m, int seed = DefaultSeed)
    {
        Validate(probFillOnLimit);
        Validate(probFillOnStop);
        Validate(probSlippage);
        ProbFillOnLimit = probFillOnLimit;
        ProbFillOnStop = probFillOnStop;
        ProbSlippage = probSlippage;
        _seed = seed;
        _random = new Random(seed);
    }

    /// <summary>
    /// Starts the random source again from its seed. A re-run of the same configuration has to roll the same dice as the
    /// first one, or the determinism the engine promises does not hold once fills are probabilistic.
    /// </summary>
    public void Reset() => _random = new Random(_seed);

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
/// Charges a fee for every contract traded, the way futures and options venues charge: the bill follows how many
/// contracts changed hands and not what they were worth, so a cheap contract and a dear one cost the same to trade
/// and a percentage of notional is the wrong shape for both.
/// <para>
/// A maker fee of its own is optional; without one, both sides pay the same. The fee is per contract as the venue
/// counts them, so an instrument whose multiplier makes one contract ten of something is still one contract here.
/// </para>
/// </summary>
public sealed class PerContractFeeModel : FeeModel
{
    private readonly Money _taker;
    private readonly Money _maker;

    public PerContractFeeModel(Money perContract)
        : this(perContract, perContract)
    {
    }

    public PerContractFeeModel(Money taker, Money maker)
    {
        if (!taker.Currency.Equals(maker.Currency))
        {
            throw new ArgumentException($"A per-contract fee cannot be charged in two currencies: {taker.Currency} for taking and {maker.Currency} for making.", nameof(maker));
        }

        _taker = taker;
        _maker = maker;
    }

    public override Money Commission(Instrument instrument, Order order, Quantity lastQty, Price lastPx, LiquiditySide liquiditySide)
    {
        Money fee = liquiditySide == LiquiditySide.Maker ? _maker : _taker;
        return new Money(fee.Amount * lastQty.Value, fee.Currency);
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
/// What bounds a fill in the simulator.
/// </summary>
public enum FillSizing
{
    /// <summary>
    /// A fill is bounded by the size on offer where it fills: the other side of the quote or the book for an order
    /// that takes, the size of the print that reached it for an order that rests. What is left of the order goes on
    /// working, or is cancelled where its time in force says so - an immediate-or-cancel order keeps what it got and
    /// gives up the rest. Several orders share one touch: whatever the first takes is not there for the second.
    /// <para>
    /// Data that says nothing about size bounds nothing. A bar carries the volume of a whole bar rather than a size at
    /// a price, so a bar-driven run fills exactly as it did before this existed.
    /// </para>
    /// </summary>
    AvailableSize = 1,

    /// <summary>
    /// An order fills its whole remaining size at one price, whatever was on offer there: what the simulator did
    /// before 0.5. Kept for reproducing a run made under it, and for a study that means to ignore liquidity.
    /// </summary>
    WholeFills = 2,
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
