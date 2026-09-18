using Bytex.Core.Model;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Backtest.Tests.Support;

/// <summary>
/// Instruments with round numbers so that expected fills, fees and margins can be derived by hand.
/// </summary>
public static class TestInstruments
{
    public static readonly Venue Sim = new("SIM");

    public static readonly Venue Alt = new("ALT");

    /// <summary>Spot BTC/USDT: tick 0.01, lot 0.001, maker 0.1 %, taker 0.2 %.</summary>
    public static CurrencyPair Spot(Venue? venue = null) => new(new InstrumentSpec
    {
        Id = new InstrumentId(new Symbol("BTCUSDT"), venue ?? Sim),
        AssetClass = AssetClass.Crypto,
        InstrumentClass = InstrumentClass.Spot,
        QuoteCurrency = Currencies.USDT,
        BaseCurrency = Currencies.BTC,
        PricePrecision = 2,
        SizePrecision = 3,
        PriceIncrement = new Price(0.01m, 2),
        SizeIncrement = new Quantity(0.001m, 3),
        MakerFee = 0.001m,
        TakerFee = 0.002m,
    });

    /// <summary>Spot ETH/USDT on a second venue, same precision and fees as <see cref="Spot"/>.</summary>
    public static CurrencyPair EthSpot(Venue? venue = null) => new(new InstrumentSpec
    {
        Id = new InstrumentId(new Symbol("ETHUSDT"), venue ?? Alt),
        AssetClass = AssetClass.Crypto,
        InstrumentClass = InstrumentClass.Spot,
        QuoteCurrency = Currencies.USDT,
        BaseCurrency = Currencies.ETH,
        PricePrecision = 2,
        SizePrecision = 3,
        PriceIncrement = new Price(0.01m, 2),
        SizeIncrement = new Quantity(0.001m, 3),
        MakerFee = 0.001m,
        TakerFee = 0.002m,
    });

    /// <summary>Linear perpetual settled in USDT: tick 0.1, initial margin 5 %, maintenance 2.5 %, maker 0.02 %, taker 0.05 %.</summary>
    public static CryptoPerpetual Perp(Venue? venue = null) => new(new InstrumentSpec
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

    /// <summary>Inverse perpetual: quoted in USD, sized in USD contracts, settled in BTC; maker 0.02 %, taker 0.05 %.</summary>
    public static CryptoPerpetual InversePerp(Venue? venue = null) => new(new InstrumentSpec
    {
        Id = new InstrumentId(new Symbol("BTCUSD-PERP"), venue ?? Sim),
        AssetClass = AssetClass.Crypto,
        InstrumentClass = InstrumentClass.Swap,
        QuoteCurrency = Currencies.USD,
        BaseCurrency = Currencies.BTC,
        SettlementCurrency = Currencies.BTC,
        IsInverse = true,
        PricePrecision = 1,
        SizePrecision = 0,
        PriceIncrement = new Price(0.5m, 1),
        SizeIncrement = new Quantity(1m, 0),
        MarginInit = 0.05m,
        MarginMaint = 0.025m,
        MakerFee = 0.0002m,
        TakerFee = 0.0005m,
    });
}
