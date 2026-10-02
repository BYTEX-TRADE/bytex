using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Primitives;

namespace Bytex.Data.Tests.Support;

/// <summary>
/// Market-data builders. Timestamps are given in whole nanoseconds relative to <see cref="T0"/>, an instant whose
/// nanosecond digits are all non-zero, so that any truncation to micro- or milliseconds would be visible.
/// </summary>
internal static class Sample
{
    /// <summary>2024-01-01T00:00:00.123456789Z.</summary>
    public const long T0 = 1_704_067_200_123_456_789L;

    public static readonly MarketKey Btc = MarketKey.Parse("bx-market:v2/BINANCE/BTCUSDT");
    public static readonly MarketKey Eth = MarketKey.Parse("bx-market:v2/BINANCE/ETHUSDT");

    public static readonly CandleSeries BtcMinute = new(Btc, new SamplingRule(1, SamplingMethod.Minute, PriceType.Last));
    public static readonly CandleSeries BtcHour = new(Btc, new SamplingRule(1, SamplingMethod.Hour, PriceType.Last));

    public static UnixNanos At(long offsetNanos) => new(T0 + offsetNanos);

    public static QuoteTick Quote(long offset, decimal bid = 42000.10m, MarketKey? id = null) =>
        new(id ?? Btc, new Price(bid, 2), new Price(bid + 0.01m, 2), new Quantity(1.25000m, 5), new Quantity(0.00001m, 5), At(offset - 7), At(offset));

    public static TradeTick Trade(long offset, string tradeId = "t-1", AggressorSide side = AggressorSide.Buyer, MarketKey? id = null) =>
        new(id ?? Btc, new Price(42000.55m, 2), new Quantity(0.12345m, 5), side, new TradeId(tradeId), At(offset - 3), At(offset));

    public static Bar Bar(long offset, decimal close = 42010.00m, CandleSeries? candleSeries = null) =>
        new(candleSeries ?? BtcMinute, new Price(close - 10m, 2), new Price(close + 5.55m, 2), new Price(close - 20.01m, 2), new Price(close, 2), new Quantity(123.45678m, 5), At(offset - 1), At(offset));

    public static OrderBookDelta Delta(long offset, ulong sequence, BookAction action = BookAction.Add, OrderSide side = OrderSide.Buy, ulong orderId = 1, RecordFlags flags = RecordFlags.None, MarketKey? id = null) =>
        new(id ?? Btc, action, new BookOrder(side, new Price(41999.99m, 2), new Quantity(3.00000m, 5), orderId), flags, sequence, At(offset - 5), At(offset));

    /// <summary>
    /// A book snapshot whose two sides can be asked for at any depth, because the number of levels is the thing worth
    /// varying: it belongs to whatever published the book, not to the model. Every level carries a different price,
    /// size AND order count, so a round trip that shifted, padded or dropped one shows it rather than handing back a
    /// plausible-looking ladder.
    /// </summary>
    public static OrderBookDepth Depth(long offset, int bidLevels = 25, int askLevels = 25, ulong sequence = 1, MarketKey? id = null) =>
        new(
            id ?? Btc,
            [.. Enumerable.Range(0, bidLevels).Select(i => new BookLevel(new Price(41999.99m - (i * 0.01m), 2), new Quantity(3.00000m + i, 5), i + 1))],
            [.. Enumerable.Range(0, askLevels).Select(i => new BookLevel(new Price(42000.01m + (i * 0.01m), 2), new Quantity(0.00001m + i, 5), i + 2))],
            RecordFlags.Snapshot,
            sequence,
            At(offset - 5),
            At(offset));
}
