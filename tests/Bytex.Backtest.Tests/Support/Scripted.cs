using System.Globalization;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Backtest.Tests.Support;

/// <summary>
/// Builders for scripted market data on a fixed simulated timeline that starts at 2025-01-01T00:00:00Z.
/// </summary>
public static class Scripted
{
    public static readonly UnixNanos Epoch = UnixNanos.FromDateTimeOffset(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));

    /// <summary>Timestamp <paramref name="milliseconds"/> after the start of the timeline.</summary>
    public static UnixNanos Ms(long milliseconds) => Epoch.AddNanos(milliseconds * UnixNanos.NanosPerMillisecond);

    public static BarType MinuteBars(Instrument instrument) =>
        new(instrument.Id, new BarSpecification(1, BarAggregation.Minute, PriceType.Last));

    public static QuoteTick Quote(Instrument instrument, long atMs, decimal bid, decimal ask) =>
        new(instrument.Id, instrument.MakePrice(bid), instrument.MakePrice(ask), instrument.MakeQuantity(10m), instrument.MakeQuantity(10m), Ms(atMs), Ms(atMs));

    public static TradeTick Trade(Instrument instrument, long atMs, decimal price, AggressorSide aggressor = AggressorSide.Buyer) =>
        new(instrument.Id, instrument.MakePrice(price), instrument.MakeQuantity(1m), aggressor, new TradeId("X-" + atMs.ToString(CultureInfo.InvariantCulture)), Ms(atMs), Ms(atMs));

    public static Bar Bar(Instrument instrument, long atMs, decimal open, decimal high, decimal low, decimal close) =>
        new(MinuteBars(instrument), instrument.MakePrice(open), instrument.MakePrice(high), instrument.MakePrice(low), instrument.MakePrice(close), instrument.MakeQuantity(1m), Ms(atMs), Ms(atMs));

    /// <summary>Order book deltas that add one bid and one ask level.</summary>
    public static OrderBookDeltas BookLevels(Instrument instrument, long atMs, decimal bid, decimal ask)
    {
        ulong sequence = (ulong)atMs;
        OrderBookDelta Add(OrderSide side, decimal price, ulong orderId) =>
            new(instrument.Id, BookAction.Add, new BookOrder(side, instrument.MakePrice(price), instrument.MakeQuantity(5m), orderId), RecordFlags.None, sequence, Ms(atMs), Ms(atMs));

        return new OrderBookDeltas(instrument.Id, [Add(OrderSide.Buy, bid, (sequence * 2) + 1), Add(OrderSide.Sell, ask, (sequence * 2) + 2)], RecordFlags.None, sequence, Ms(atMs), Ms(atMs));
    }
}
