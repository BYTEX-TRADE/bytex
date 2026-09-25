using System.Globalization;
using Bytex.Core.Model;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Microsoft.Extensions.Logging;

namespace Bytex.Core.Trading;

public sealed record TwapExecAlgorithmConfig : ExecAlgorithmConfig
{
    public TwapExecAlgorithmConfig() => ExecAlgorithmId = TwapExecAlgorithm.DefaultExecAlgorithmId;

    /// <summary>How long an order is worked over, unless the order itself says otherwise.</summary>
    public TimeSpan Horizon { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How often a slice goes out inside the horizon, unless the order itself says otherwise.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// Works one order as many equal ones at an even pace: the time-weighted average price. A size that would move the
/// market if it went out at once goes out in slices instead, a slice every interval until the horizon is up, so what
/// it pays is the average of the prices over that stretch rather than whatever the book held at one moment.
/// <para>
/// The parent order is the instruction and never reaches a venue; the slices are the orders. Each carries the parent's
/// id as its <see cref="Order.ExecSpawnId"/>, so <c>cache.OrdersForExecSpawn(parent)</c> is the whole picture: what has
/// gone out, what filled, and at what average. A slice is a market order when the parent is a market order, and a
/// limit order at the parent's price when the parent is a limit order - the pace is this algorithm's business, and the
/// price it is willing to pay stays the order's own.
/// </para>
/// <para>
/// The horizon and the interval come from the configuration, and an order may carry its own in
/// <see cref="Order.ExecAlgorithmParams"/> under <c>horizon</c> and <c>interval</c>, written the way a time span is
/// written: <c>"00:05:00"</c>.
/// </para>
/// </summary>
public sealed class TwapExecAlgorithm : ExecAlgorithm
{
    /// <summary>
    /// The id this algorithm answers to unless a host names its own, and the id anything asking for a paced order
    /// names: a strategy document says <c>twap</c> and means this.
    /// </summary>
    public static readonly ExecAlgorithmId DefaultExecAlgorithmId = new("TWAP");

    /// <summary>What an order writes in its own parameters to work at its own pace.</summary>
    public const string HorizonParam = "horizon";

    /// <inheritdoc cref="HorizonParam"/>
    public const string IntervalParam = "interval";

    private readonly Dictionary<ClientOrderId, Slicing> _working = new(EqualityComparer<ClientOrderId>.Default);

    public TwapExecAlgorithm(TwapExecAlgorithmConfig? config = null)
        : base(config ?? new TwapExecAlgorithmConfig())
    {
        Config = (TwapExecAlgorithmConfig)base.Config;
    }

    public new TwapExecAlgorithmConfig Config { get; }

    /// <summary>The orders being worked in slices at this moment.</summary>
    public IReadOnlyCollection<ClientOrderId> Working => _working.Keys;

    protected override void OnOrder(Order order)
    {
        if (order.Type is not (OrderType.Market or OrderType.Limit))
        {
            // Nothing is sent and the instruction does not sit there looking alive: a strategy that asked for a paced
            // order and got nothing would find out at the wrong time, and a size worked as something else is not what
            // it asked for either.
            Log.LogError("{Algorithm} cannot work a {Type} order in slices; cancelling {ClientOrderId}", ExecAlgorithmId, order.Type, order.ClientOrderId);
            CancelOrder(order);
            return;
        }

        if (Cache.Instrument(order.InstrumentId) is not { } instrument)
        {
            Log.LogError("{Algorithm} cannot work {ClientOrderId}: {InstrumentId} is not in the cache; cancelling it", ExecAlgorithmId, order.ClientOrderId, order.InstrumentId);
            CancelOrder(order);
            return;
        }

        TimeSpan horizon = Span(order, HorizonParam) ?? Config.Horizon;
        TimeSpan interval = Span(order, IntervalParam) ?? Config.Interval;
        if (horizon <= TimeSpan.Zero || interval <= TimeSpan.Zero)
        {
            Log.LogError("{Algorithm} cannot work {ClientOrderId} over a horizon of {Horizon} every {Interval}; cancelling it", ExecAlgorithmId, order.ClientOrderId, horizon, interval);
            CancelOrder(order);
            return;
        }

        Slicing slicing = Slicing.For(order, instrument, horizon, interval);
        _working[order.ClientOrderId] = slicing;
        Log.LogInformation(
            "{Algorithm} working {ClientOrderId}: {Quantity} in {Slices} of {Slice} every {Interval} over {Horizon}",
            ExecAlgorithmId, order.ClientOrderId, order.Quantity, slicing.Slices, slicing.Slice, interval, horizon);

        // The first slice goes now: a horizon that began with a wait would end one interval late, and the pace is what
        // the instruction was about.
        Send(order, slicing);
        if (slicing.Remaining > 0)
        {
            SetTimer(TimerFor(order.ClientOrderId), interval, callback: _ => Continue(order.ClientOrderId));
        }
    }

    protected override void OnOrderEvent(OrderEvent e)
    {
        // The instruction was taken back, or it could not be taken at all: nothing more is worked for it.
        if (_working.ContainsKey(e.ClientOrderId) && Cache.Order(e.ClientOrderId) is { IsClosed: true })
        {
            Stop(e.ClientOrderId, "the order it was working is " + Cache.Order(e.ClientOrderId)!.Status.ToString().ToLowerInvariant());
        }
    }

    private void Continue(ClientOrderId parentId)
    {
        if (!_working.TryGetValue(parentId, out Slicing? slicing))
        {
            return;
        }

        if (Cache.Order(parentId) is not { } parent || parent.IsClosed)
        {
            Stop(parentId, "the order it was working is gone");
            return;
        }

        Send(parent, slicing);
        if (slicing.Remaining <= 0)
        {
            Stop(parentId, "every slice has gone out");
        }
    }

    private void Send(Order parent, Slicing slicing)
    {
        Quantity quantity = slicing.Next();
        if (quantity.Value <= 0m)
        {
            return;
        }

        Order slice = parent.Type == OrderType.Market
            ? SpawnMarket(parent, quantity, parent.TimeInForce == TimeInForce.Day ? TimeInForce.Gtc : parent.TimeInForce, parent.IsReduceOnly)
            : SpawnLimit(parent, quantity, parent.Price!.Value, parent.TimeInForce, parent.ExpireTime, parent.IsPostOnly, parent.IsReduceOnly);

        SubmitOrder(slice, Cache.PositionIdFor(parent.ClientOrderId));
    }

    private void Stop(ClientOrderId parentId, string why)
    {
        if (_working.Remove(parentId))
        {
            CancelTimer(TimerFor(parentId));
            Log.LogInformation("{Algorithm} stopped working {ClientOrderId}: {Why}", ExecAlgorithmId, parentId, why);
        }
    }

    private static string TimerFor(ClientOrderId parentId) => "twap-" + parentId.Value;

    /// <summary>A time span the order carries for itself, or null when it leaves the pace to the configuration.</summary>
    private static TimeSpan? Span(Order order, string key) =>
        order.ExecAlgorithmParams is { } p && p.TryGetValue(key, out string? text) && TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out TimeSpan span)
            ? span
            : null;

    /// <summary>
    /// How one order is cut up: as many slices as the interval fits into the horizon, each the same size, with
    /// whatever rounding left over going out with the last one so the whole order is worked and no more.
    /// </summary>
    private sealed class Slicing
    {
        private Slicing(int slices, Quantity slice, decimal remaining)
        {
            Slices = slices;
            Slice = slice;
            Remaining = remaining;
            _left = slices;
        }

        private int _left;

        public int Slices { get; }

        public Quantity Slice { get; }

        /// <summary>What is still to go out, in base units.</summary>
        public decimal Remaining { get; private set; }

        public static Slicing For(Order order, Instrument instrument, TimeSpan horizon, TimeSpan interval)
        {
            decimal total = order.Quantity.Value;
            int wanted = Math.Max(1, (int)Math.Ceiling(horizon.Ticks / (decimal)interval.Ticks));

            // A slice the venue would refuse is not a slice: with a minimum size, the order is cut into as many pieces
            // as it can be cut into, and the pace is kept by sending fewer, larger ones rather than a stream of
            // rejections.
            if (instrument.MinQuantity is { } min && min.Value > 0m)
            {
                wanted = Math.Max(1, Math.Min(wanted, (int)Math.Floor(total / min.Value)));
            }

            Quantity slice = instrument.MakeQuantity(total / wanted);
            if (slice.Value <= 0m)
            {
                // The whole order is smaller than one step of size: it goes out as it is, in one.
                return new Slicing(1, order.Quantity, total);
            }

            return new Slicing(wanted, slice, total);
        }

        /// <summary>The next slice to send: the even size, or everything left when this is the last one.</summary>
        public Quantity Next()
        {
            if (Remaining <= 0m)
            {
                return new Quantity(0m, Slice.Precision);
            }

            _left--;
            decimal size = _left <= 0 || Remaining - Slice.Value <= 0m ? Remaining : Slice.Value;
            Remaining -= size;
            return new Quantity(size, Slice.Precision);
        }
    }
}
