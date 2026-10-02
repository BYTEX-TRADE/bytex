using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Timing;

namespace Bytex.Core.Engines;

/// <summary>
/// The shared part of the bars that close on how one-sided the flow has been (R2.12): signing each update, and
/// counting it in trades, size or value.
///
/// <para>
/// <b>How an update is signed.</b> By the venue's own word where there is one: a trade carries the aggressor, and a
/// venue saying "this was a buy" is a fact rather than an inference. Where there is none - a quote, or a trade from a
/// source that does not say - the tick rule is used instead: the sign of the change from the previous price, carried
/// forward while the price does not move. The very first update has no previous price and no aggressor to fall back on,
/// so it counts as a buy by convention; it shifts the first bar of a series and nothing after it.
/// </para>
///
/// <para>
/// <b>An update is never split.</b> A volume bar can take half of a trade, because half of a size is a size. Half of a
/// signed trade is not a signed trade - the sign belongs to the whole of it - so these bars close on the update that
/// carries them over the threshold and the whole update is in the bar that closed.
/// </para>
/// </summary>
public abstract class InformationBarAggregator : BarAggregator
{
    private int _lastSign;
    private decimal _lastPrice;
    private bool _hasLastPrice;
    private AggressorSide _aggressor = AggressorSide.None;

    protected InformationBarAggregator(Instrument instrument, CandleSeries candleSeries, Action<Bar> handler, IClock clock)
        : base(instrument, candleSeries, handler, clock)
    {
        if (!candleSeries.Spec.IsInformationBased())
        {
            throw new ArgumentException($"{candleSeries.Spec.Aggregation} does not measure flow, so it cannot be aggregated this way.", nameof(candleSeries));
        }

        if (candleSeries.Spec.Step <= 0)
        {
            throw new ArgumentException("A bar that closes on flow needs a threshold above zero.", nameof(candleSeries));
        }
    }

    /// <summary>The threshold this kind of bar closes at, in whatever it counts.</summary>
    protected decimal Threshold => CandleSeries.Spec.Step;

    /// <summary>
    /// A trade carries the side that took the liquidity, which is better than any inference from prices, so it is kept
    /// for the update that follows immediately.
    /// </summary>
    public override void HandleTradeTick(TradeTick tick)
    {
        _aggressor = tick.Aggressor;
        try
        {
            base.HandleTradeTick(tick);
        }
        finally
        {
            _aggressor = AggressorSide.None;
        }
    }

    protected sealed override void ApplyUpdate(Price price, Quantity size, UnixNanos eventTime)
    {
        int sign = Sign(price.Value);
        decimal flow = CandleSeries.Spec.Aggregation.FlowUnit() switch
        {
            SamplingMethod.Tick => 1m,
            SamplingMethod.Volume => size.Value,
            _ => size.Value * price.Value,
        };

        Builder.Update(price, size, eventTime);
        Accumulate(sign, flow);

        if (Crossed())
        {
            Emit(eventTime);
            ResetFlow();
        }
    }

    /// <summary>Adds one signed update to whatever this kind of bar is measuring.</summary>
    protected abstract void Accumulate(int sign, decimal flow);

    /// <summary>Whether what has been accumulated has reached the threshold.</summary>
    protected abstract bool Crossed();

    /// <summary>Starts the next bar's measurement. The bar itself is reset by the builder.</summary>
    protected abstract void ResetFlow();

    private int Sign(decimal price)
    {
        int sign = _aggressor switch
        {
            AggressorSide.Buyer => 1,
            AggressorSide.Seller => -1,
            _ when !_hasLastPrice => 1,
            _ when price > _lastPrice => 1,
            _ when price < _lastPrice => -1,
            _ => _lastSign == 0 ? 1 : _lastSign,
        };

        _lastPrice = price;
        _hasLastPrice = true;
        _lastSign = sign;
        return sign;
    }

    /// <summary>
    /// Forgets the tick rule's memory as well as the flow. A series restarted after a gap has no previous price that
    /// means anything, and carrying one over would sign the first update of the new series from the old one.
    /// </summary>
    public virtual void ResetState()
    {
        _lastSign = 0;
        _lastPrice = 0m;
        _hasLastPrice = false;
        ResetFlow();
    }
}

/// <summary>
/// Imbalance bars: one bar per amount by which one side outweighed the other (R2.12).
///
/// <para>
/// The running total is signed, so buys and sells cancel: a market being worked in both directions produces no bar
/// however much of it there is, and a bar appears when one side has outweighed the other by the threshold. That is the
/// whole point of it - these bars sample information rather than volume or time.
/// </para>
///
/// <para>
/// <b>Where this departs from the textbook.</b> The published form closes a bar when the imbalance exceeds an
/// EXPECTATION - an exponentially weighted average of how imbalanced past bars were - so the threshold moves with the
/// market. That is deliberately not what this does. An estimated threshold makes a bar depend on the estimator's own
/// parameters and on everything the estimator has seen, so the same data can produce different bars in two runs, and
/// nothing in a bar type can carry those parameters. This closes at the threshold the caller stated, which is
/// reproducible, is what the bar type says, and is a number somebody chose rather than one that drifted.
/// </para>
/// </summary>
public sealed class ImbalanceBarAggregator : InformationBarAggregator
{
    private decimal _imbalance;

    public ImbalanceBarAggregator(Instrument instrument, CandleSeries candleSeries, Action<Bar> handler, IClock clock)
        : base(instrument, candleSeries, handler, clock)
    {
    }

    /// <summary>The running imbalance, positive where buys have outweighed sells.</summary>
    public decimal Imbalance => _imbalance;

    protected override void Accumulate(int sign, decimal flow) => _imbalance += sign * flow;

    protected override bool Crossed() => Math.Abs(_imbalance) >= Threshold;

    protected override void ResetFlow() => _imbalance = 0m;
}

/// <summary>
/// Runs bars: one bar per amount of flow that went the SAME WAY, whichever way that was (R2.12).
///
/// <para>
/// The difference from an imbalance bar is what cancels. Here the two sides are counted separately and neither is
/// subtracted from the other, so a bar closes when one side alone has reached the threshold. A market that traded
/// heavily in both directions closes a runs bar and leaves an imbalance bar open, and that is the distinction the two
/// kinds exist to draw.
/// </para>
///
/// <para>
/// As with imbalance bars, the threshold is the one the caller stated rather than an estimate of it, for the same
/// reason: a bar that depends on an estimator is not reproducible from its own description.
/// </para>
/// </summary>
public sealed class RunsBarAggregator : InformationBarAggregator
{
    private decimal _buys;
    private decimal _sells;

    public RunsBarAggregator(Instrument instrument, CandleSeries candleSeries, Action<Bar> handler, IClock clock)
        : base(instrument, candleSeries, handler, clock)
    {
    }

    /// <summary>What has gone the buyers' way since the last bar.</summary>
    public decimal Buys => _buys;

    /// <summary>What has gone the sellers' way since the last bar.</summary>
    public decimal Sells => _sells;

    protected override void Accumulate(int sign, decimal flow)
    {
        if (sign >= 0)
        {
            _buys += flow;
        }
        else
        {
            _sells += flow;
        }
    }

    protected override bool Crossed() => Math.Max(_buys, _sells) >= Threshold;

    protected override void ResetFlow()
    {
        _buys = 0m;
        _sells = 0m;
    }
}
