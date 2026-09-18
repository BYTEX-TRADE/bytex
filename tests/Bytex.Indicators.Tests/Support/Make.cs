using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Primitives;

namespace Bytex.Indicators.Tests.Support;

/// <summary>
/// Builders for market data. Prices carry four decimals and sizes three, enough for every literal in the tests.
/// </summary>
internal static class Make
{
    public const byte PricePrecision = 4;
    public const byte SizePrecision = 3;

    public static readonly InstrumentId Instrument = InstrumentId.Parse("TEST.SIM");

    public static readonly BarType BarType = new(Instrument, new BarSpecification(1, BarAggregation.Minute, PriceType.Last));

    /// <summary>2024-01-01T00:00:00Z.</summary>
    public static readonly UnixNanos Epoch2024 = UnixNanos.FromSeconds(1_704_067_200L);

    public static Bar Bar(decimal high, decimal low, decimal close, decimal volume = 1m, UnixNanos? ts = null, decimal? open = null)
    {
        UnixNanos t = ts ?? Epoch2024;
        return new Bar(BarType, P(open ?? close), P(high), P(low), P(close), Q(volume), t, t);
    }

    public static Bar Bar(Ohlcv row, UnixNanos? ts = null) => Bar(row.High, row.Low, row.Close, row.Volume, ts, row.Open);

    public static QuoteTick Quote(decimal bid, decimal ask, decimal bidSize = 1m, decimal askSize = 1m, UnixNanos? ts = null)
    {
        UnixNanos t = ts ?? Epoch2024;
        return new QuoteTick(Instrument, P(bid), P(ask), Q(bidSize), Q(askSize), t, t);
    }

    public static TradeTick Trade(decimal price, decimal size = 1m, UnixNanos? ts = null)
    {
        UnixNanos t = ts ?? Epoch2024;
        return new TradeTick(Instrument, P(price), Q(size), AggressorSide.Buyer, new TradeId("T"), t, t);
    }

    private static Price P(decimal value) => new(value, PricePrecision);

    private static Quantity Q(decimal value) => new(value, SizePrecision);
}
