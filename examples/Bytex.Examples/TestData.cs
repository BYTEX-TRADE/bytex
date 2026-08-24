using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Examples;

/// <summary>
/// Instruments and synthetic data for the examples.
/// </summary>
public static class TestData
{
    public static readonly Venue Sim = new("SIM");

    public static CurrencyPair BtcUsdt(Venue? venue = null) => new(new InstrumentSpec
    {
        Id = new InstrumentId(new Symbol("BTCUSDT"), venue ?? Sim),
        AssetClass = AssetClass.Crypto,
        InstrumentClass = InstrumentClass.Spot,
        QuoteCurrency = Currencies.USDT,
        BaseCurrency = Currencies.BTC,
        PricePrecision = 2,
        SizePrecision = 6,
        PriceIncrement = new Price(0.01m, 2),
        SizeIncrement = new Quantity(0.000001m, 6),
        MinQuantity = new Quantity(0.00001m, 6),
        MakerFee = 0.001m,
        TakerFee = 0.001m,
    });

    public static CryptoPerpetual BtcUsdtPerp(Venue? venue = null) => new(new InstrumentSpec
    {
        Id = new InstrumentId(new Symbol("BTCUSDT-PERP"), venue ?? Sim),
        AssetClass = AssetClass.Crypto,
        InstrumentClass = InstrumentClass.Swap,
        QuoteCurrency = Currencies.USDT,
        BaseCurrency = Currencies.BTC,
        SettlementCurrency = Currencies.USDT,
        PricePrecision = 1,
        SizePrecision = 3,
        PriceIncrement = new Price(0.1m, 1),
        SizeIncrement = new Quantity(0.001m, 3),
        MarginInit = 0.05m,
        MarginMaint = 0.025m,
        MakerFee = 0.0002m,
        TakerFee = 0.0005m,
    });

    /// <summary>
    /// Deterministic random-walk minute bars.
    /// </summary>
    public static IReadOnlyList<Bar> RandomWalkBars(Instrument instrument, BarType barType, int count, decimal startPrice = 50_000m, int seed = 7, UnixNanos? start = null)
    {
        Random random = new(seed);
        List<Bar> bars = new(count);
        decimal price = startPrice;
        UnixNanos ts = start ?? UnixNanos.FromDateTimeOffset(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));
        long interval = barType.Spec.IntervalNanos;
        for (int i = 0; i < count; i++)
        {
            decimal drift = (decimal)(random.NextDouble() - 0.48) * price * 0.002m;
            decimal open = price;
            decimal close = Math.Max(1m, open + drift);
            decimal high = Math.Max(open, close) + (decimal)random.NextDouble() * price * 0.001m;
            decimal low = Math.Min(open, close) - (decimal)random.NextDouble() * price * 0.001m;
            decimal volume = 1m + (decimal)random.NextDouble() * 10m;
            ts = ts.AddNanos(interval);
            bars.Add(new Bar(barType, instrument.MakePrice(open), instrument.MakePrice(high), instrument.MakePrice(low), instrument.MakePrice(close), instrument.MakeQuantity(volume), ts, ts));
            price = close;
        }

        return bars;
    }
}
