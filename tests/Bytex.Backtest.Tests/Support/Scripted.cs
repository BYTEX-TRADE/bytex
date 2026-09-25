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

    /// <summary>The size on both sides when a test does not say: enough for the orders these tests place.</summary>
    public const decimal DefaultQuoteSize = 10m;

    public static QuoteTick Quote(Instrument instrument, long atMs, decimal bid, decimal ask, decimal size = DefaultQuoteSize) =>
        new(instrument.Id, instrument.MakePrice(bid), instrument.MakePrice(ask), instrument.MakeQuantity(size), instrument.MakeQuantity(size), Ms(atMs), Ms(atMs));

    /// <summary>The size of a print when a test does not say.</summary>
    public const decimal DefaultTradeSize = 1m;

    public static TradeTick Trade(Instrument instrument, long atMs, decimal price, AggressorSide aggressor = AggressorSide.Buyer, decimal size = DefaultTradeSize) =>
        new(instrument.Id, instrument.MakePrice(price), instrument.MakeQuantity(size), aggressor, new TradeId("X-" + atMs.ToString(CultureInfo.InvariantCulture)), Ms(atMs), Ms(atMs));

    /// <summary>
    /// What a bar traded when a test does not say. A bar carries the volume the venue reports, and what one
    /// participant may take of it bounds a fill, so a fixture writing one unit would bound every order in the suite
    /// at a tenth of a unit - a property of the fixture, not of anything under test. A test that means to be bounded
    /// says so with a volume of its own.
    /// </summary>
    public const decimal DefaultBarVolume = 1_000m;

    public static Bar Bar(Instrument instrument, long atMs, decimal open, decimal high, decimal low, decimal close, decimal volume = DefaultBarVolume) =>
        new(MinuteBars(instrument), instrument.MakePrice(open), instrument.MakePrice(high), instrument.MakePrice(low), instrument.MakePrice(close), instrument.MakeQuantity(volume), Ms(atMs), Ms(atMs));

    /// <summary>Order book deltas that add one bid and one ask level.</summary>
    public static FundingRateUpdate Funding(Instrument instrument, long atMs, decimal rate) =>
        new(instrument.Id, rate, null, Ms(atMs), Ms(atMs));

    /// <summary>The size of each level a test adds when it does not say.</summary>
    public const decimal DefaultLevelSize = 5m;

    /// <summary>
    /// A book of several levels a side: bids descending from <paramref name="bid"/> and asks ascending from
    /// <paramref name="ask"/>, one tick apart, each holding <paramref name="size"/>. What an order that takes eats
    /// its way through.
    /// </summary>
    public static OrderBookDeltas BookDepth(Instrument instrument, long atMs, decimal bid, decimal ask, decimal size, int levels, bool replacing = false)
    {
        ulong sequence = (ulong)atMs;
        decimal tick = instrument.PriceIncrement.Value;
        List<OrderBookDelta> rows = new();

        // A venue's snapshot replaces the book; its deltas add to it. A price nobody deletes goes on being quoted, so
        // a test that needs the market to have moved has to say which of the two this is.
        if (replacing)
        {
            rows.Add(new OrderBookDelta(instrument.Id, BookAction.Clear, new BookOrder(OrderSide.Buy, instrument.MakePrice(bid), instrument.MakeQuantity(0m), 0UL), RecordFlags.None, sequence, Ms(atMs), Ms(atMs)));
        }

        for (int i = 0; i < levels; i++)
        {
            ulong id = (sequence * 100UL) + (ulong)i;
            rows.Add(new OrderBookDelta(instrument.Id, BookAction.Update, new BookOrder(OrderSide.Buy, instrument.MakePrice(bid - (i * tick)), instrument.MakeQuantity(size), id), RecordFlags.None, sequence, Ms(atMs), Ms(atMs)));
            rows.Add(new OrderBookDelta(instrument.Id, BookAction.Update, new BookOrder(OrderSide.Sell, instrument.MakePrice(ask + (i * tick)), instrument.MakeQuantity(size), id + 50UL), RecordFlags.None, sequence, Ms(atMs), Ms(atMs)));
        }

        return new OrderBookDeltas(instrument.Id, rows, RecordFlags.None, sequence, Ms(atMs), Ms(atMs));
    }

    public static OrderBookDeltas BookLevels(Instrument instrument, long atMs, decimal bid, decimal ask, decimal size = DefaultLevelSize)
    {
        ulong sequence = (ulong)atMs;
        OrderBookDelta Add(OrderSide side, decimal price, ulong orderId) =>
            new(instrument.Id, BookAction.Add, new BookOrder(side, instrument.MakePrice(price), instrument.MakeQuantity(size), orderId), RecordFlags.None, sequence, Ms(atMs), Ms(atMs));

        return new OrderBookDeltas(instrument.Id, [Add(OrderSide.Buy, bid, (sequence * 2) + 1), Add(OrderSide.Sell, ask, (sequence * 2) + 2)], RecordFlags.None, sequence, Ms(atMs), Ms(atMs));
    }
}
