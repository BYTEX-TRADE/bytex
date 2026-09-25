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

    public static readonly InstrumentId Btc = InstrumentId.Parse("BTCUSDT.BINANCE");
    public static readonly InstrumentId Eth = InstrumentId.Parse("ETHUSDT.BINANCE");

    public static readonly BarType BtcMinute = new(Btc, new BarSpecification(1, BarAggregation.Minute, PriceType.Last));
    public static readonly BarType BtcHour = new(Btc, new BarSpecification(1, BarAggregation.Hour, PriceType.Last));

    public static UnixNanos At(long offsetNanos) => new(T0 + offsetNanos);

    public static QuoteTick Quote(long offset, decimal bid = 42000.10m, InstrumentId? id = null) =>
        new(id ?? Btc, new Price(bid, 2), new Price(bid + 0.01m, 2), new Quantity(1.25000m, 5), new Quantity(0.00001m, 5), At(offset - 7), At(offset));

    public static TradeTick Trade(long offset, string tradeId = "t-1", AggressorSide side = AggressorSide.Buyer, InstrumentId? id = null) =>
        new(id ?? Btc, new Price(42000.55m, 2), new Quantity(0.12345m, 5), side, new TradeId(tradeId), At(offset - 3), At(offset));

    public static Bar Bar(long offset, decimal close = 42010.00m, BarType? barType = null) =>
        new(barType ?? BtcMinute, new Price(close - 10m, 2), new Price(close + 5.55m, 2), new Price(close - 20.01m, 2), new Price(close, 2), new Quantity(123.45678m, 5), At(offset - 1), At(offset));

    public static OrderBookDelta Delta(long offset, ulong sequence, BookAction action = BookAction.Add, OrderSide side = OrderSide.Buy, ulong orderId = 1, RecordFlags flags = RecordFlags.None, InstrumentId? id = null) =>
        new(id ?? Btc, action, new BookOrder(side, new Price(41999.99m, 2), new Quantity(3.00000m, 5), orderId), flags, sequence, At(offset - 5), At(offset));
}
