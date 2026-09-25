using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Indicators;

namespace Bytex.Documents.Runtime;

/// <summary>Common plumbing for built-in evaluators.</summary>
internal abstract class NodeBase : INodeEvaluator
{
    protected NodeBase(NodeBuildContext ctx)
    {
        Ctx = ctx;
    }

    protected NodeBuildContext Ctx { get; }

    protected NodeParams P => Ctx.Params;

    protected IStrategyServices Services => Ctx.Services;

    protected string NodeId => Ctx.Node.Id;

    protected Instrument? Instrument => Ctx.Instrument ?? Services.Cache.Instrument(Ctx.InstrumentId);

    protected Instrument RequireInstrument() => Instrument ?? throw new InvalidOperationException($"Node {NodeId}: instrument {Ctx.InstrumentId} is not in the cache.");

    public abstract void Evaluate(EvalContext ctx);

    /// <summary>Called instead of <see cref="Evaluate"/> for an action node on a warm-up bar: remember the trigger, do nothing.</summary>
    public virtual void Prime(EvalContext ctx)
    {
    }

    protected static string F(decimal value) => value.ToString("0.########", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Detects a rising edge on a boolean input, the standard trigger for actions and transitions.</summary>
internal sealed class Edge
{
    private bool _previous;

    public bool Rising(bool current)
    {
        bool rising = current && !_previous;
        _previous = current;
        return rising;
    }

    public bool Previous => _previous;

    public void Reset() => _previous = false;
}

/// <summary>Bounded history of decimals with the newest last; a thin wrapper over the engine's rolling window.</summary>
internal sealed class History
{
    private readonly RollingWindow _window;

    public History(int capacity)
    {
        _window = new RollingWindow(Math.Max(1, capacity));
    }

    public int Count => _window.Count;

    public bool IsFull => _window.IsFull;

    public decimal Latest => _window.Latest;

    public decimal Oldest => _window.Oldest;

    public void Add(decimal value) => _window.Add(value);

    /// <summary>Value <paramref name="back"/> bars ago (0 = latest).</summary>
    public decimal Back(int back) => _window[Math.Max(0, _window.Count - 1 - back)];

    public decimal Max() => _window.Max();

    public decimal Min() => _window.Min();

    public decimal Average => _window.Average;

    public decimal StandardDeviation() => _window.StandardDeviation();

    public decimal this[int index] => _window[index];

    public void Clear() => _window.Clear();
}

internal static class BarMath
{
    public static decimal Typical(Bar bar) => (bar.High.Value + bar.Low.Value + bar.Close.Value) / 3m;

    public static decimal Range(Bar bar) => bar.High.Value - bar.Low.Value;

    public static decimal Body(Bar bar) => Math.Abs(bar.Close.Value - bar.Open.Value);

    public static bool IsUp(Bar bar) => bar.Close.Value >= bar.Open.Value;

    public static DateTimeOffset Time(Bar bar) => bar.TsEvent.ToDateTimeOffset();
}

internal static class Sizing
{
    /// <summary>
    /// Computes an order quantity from the whole sizing parameter: the mode and its number, and the ceiling that no
    /// mode may take an order past. Modes: fixed (base units), notional (quote amount), percentOfBalance (free quote
    /// balance), riskPercent (balance × pct / |entry − stop|). Returns null when the inputs cannot produce a positive
    /// quantity, which includes a ceiling that leaves less than the instrument's minimum.
    /// <para>
    /// The ceiling exists because a mode says how to size and cannot say how much of the account may be in one order.
    /// riskPercent is the case that needs it: the nearer the stop, the larger the position it buys, so "risk one
    /// percent" with a stop a tenth of a percent away is the whole account. maxNotional and maxPercentOfBalance are
    /// read after the mode has worked its number out, and the smaller of them wins. Both are zero by default, which
    /// is no ceiling at all - a document written before they existed sizes exactly as it did.
    /// </para>
    /// </summary>
    public static Quantity? Compute(IStrategyServices services, Instrument instrument, NodeParams sizing, decimal entryPrice, decimal? stopPrice, Action<string>? onCapped = null)
    {
        ArgumentNullException.ThrowIfNull(sizing);
        string mode = sizing.Str("mode", "fixed");
        decimal value = sizing.Dec("value", 1m);
        decimal raw;
        switch (mode)
        {
            case "fixed":
                raw = value;
                break;
            case "notional":
                raw = entryPrice <= 0m ? 0m : value / entryPrice;
                break;
            case "percentOfBalance":
                {
                    decimal? balance = FreeBalance(services, instrument);
                    raw = balance is null || entryPrice <= 0m ? 0m : balance.Value * value / 100m / entryPrice;
                    break;
                }

            case "riskPercent":
                {
                    decimal? balance = FreeBalance(services, instrument);
                    if (balance is null || stopPrice is null)
                    {
                        return null;
                    }

                    decimal distance = Math.Abs(entryPrice - stopPrice.Value);
                    raw = distance <= 0m ? 0m : balance.Value * value / 100m / distance;
                    break;
                }

            default:
                raw = value;
                break;
        }

        if (raw <= 0m)
        {
            return null;
        }

        if (Ceiling(services, instrument, sizing, entryPrice) is { } ceiling && raw > ceiling)
        {
            onCapped?.Invoke($"sizing capped: {mode} asked for {instrument.MakeQuantity(raw)}, the ceiling allows {instrument.MakeQuantity(ceiling)}");
            raw = ceiling;
        }

        // Never size beyond what the account could actually carry at the entry price.
        if (entryPrice > 0m && BuyingPower(services, instrument) is { } affordable && affordable > 0m)
        {
            raw = Math.Min(raw, affordable * FeeHeadroom / entryPrice);
        }

        if (instrument.MaxQuantity is { } max && raw > max.Value)
        {
            raw = max.Value;
        }

        Quantity quantity = instrument.MakeQuantity(raw);
        if (quantity.Value <= 0m || (instrument.MinQuantity is { } min && quantity.Value < min.Value))
        {
            return null;
        }

        return quantity;
    }

    /// <summary>
    /// The most this order may be, in base units, or null when nothing was asked for. Two ceilings may be set and the
    /// smaller one holds: an amount in the quote currency, and a share of the free quote balance.
    /// </summary>
    private static decimal? Ceiling(IStrategyServices services, Instrument instrument, NodeParams sizing, decimal entryPrice)
    {
        if (entryPrice <= 0m)
        {
            return null;
        }

        decimal? ceiling = null;
        if (sizing.DecOrNull("maxNotional") is { } notional && notional > 0m)
        {
            ceiling = notional / entryPrice;
        }

        if (sizing.DecOrNull("maxPercentOfBalance") is { } percent && percent > 0m && FreeBalance(services, instrument) is { } balance && balance > 0m)
        {
            decimal byBalance = balance * percent / 100m / entryPrice;
            ceiling = ceiling is { } already ? Math.Min(already, byBalance) : byBalance;
        }

        return ceiling;
    }

    /// <summary>
    /// A little of the free balance left unspent, so that a fill's commission does not take the account past what it
    /// has. A size computed to the last unit of the balance is a size the venue rejects.
    /// </summary>
    private const decimal FeeHeadroom = 0.995m;

    /// <summary>
    /// The notional the account could carry at this venue: its free balance on a cash account, and on a margin
    /// account that balance turned into a position by the leverage the account holds for the instrument. Without the
    /// leverage a document could never ask for more than it could pay for outright, which made the setting mean
    /// nothing and put liquidation out of reach of anything a document does.
    /// </summary>
    public static decimal? BuyingPower(IStrategyServices services, Instrument instrument)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        if (FreeBalance(services, instrument) is not { } free)
        {
            return null;
        }

        if (services.Cache.AccountForVenue(instrument.Venue) is not Core.Model.Accounts.MarginAccount margin)
        {
            return free;
        }

        // What the venue will actually hold for the position, asked of the venue's own rule rather than guessed at:
        // the notional this balance can carry is the balance divided by the share of it that has to be posted. At 1x
        // that share is the whole notional and the answer is the balance itself; at 10x a tenth of it and ten times
        // the balance; past the instrument's own floor, no further.
        return free / instrument.InitialMarginRate(margin.Leverage(instrument.Id));
    }

    public static decimal? FreeBalance(IStrategyServices services, Instrument instrument)
    {
        Core.Model.Accounts.Account? account = services.Cache.AccountForVenue(instrument.Venue);
        if (account is null)
        {
            return null;
        }

        Money? free = account.BalanceFree(instrument.QuoteCurrency) ?? account.BalanceFree(instrument.SettlementCurrency) ?? account.BalanceTotal(instrument.QuoteCurrency);
        return free?.Amount;
    }
}
