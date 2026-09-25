using Bytex.Core.Model;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Data.Tests.Support;

/// <summary>
/// One instrument of every class the engine defines, with every optional field populated by a distinctive value
/// so that a field that is dropped or swapped during a round trip cannot go unnoticed.
/// </summary>
internal static class TestInstruments
{
    public static readonly UnixNanos Activation = new(1_700_000_000_123_456_789L);
    public static readonly UnixNanos Expiration = new(1_735_689_599_999_999_999L);

    public static CurrencyPair BtcUsdt() => new(new InstrumentSpec
    {
        Id = InstrumentId.Parse("BTCUSDT.BINANCE"),
        RawSymbol = new Symbol("BTCUSDT"),
        AssetClass = AssetClass.Crypto,
        InstrumentClass = InstrumentClass.Spot,
        QuoteCurrency = Currencies.USDT,
        BaseCurrency = Currencies.BTC,
        PricePrecision = 2,
        SizePrecision = 5,
        PriceIncrement = Price.Parse("0.01"),
        SizeIncrement = Quantity.Parse("0.00001"),
        LotSize = Quantity.Parse("0.00010"),
        MaxQuantity = Quantity.Parse("9000.00000"),
        MinQuantity = Quantity.Parse("0.00001"),
        MaxNotional = new Money(9_000_000m, Currencies.USDT),
        MinNotional = new Money(5m, Currencies.USDT),
        MaxPrice = Price.Parse("1000000.00"),
        MinPrice = Price.Parse("0.01"),
        MarginInit = 0.1m,
        MarginMaint = 0.05m,
        MakerFee = 0.0002m,
        TakerFee = -0.00005m,
        TsEvent = new UnixNanos(1_704_067_200_000_000_001L),
        TsInit = new UnixNanos(1_704_067_200_000_000_002L),
        Info = new Dictionary<string, string> { ["status"] = "TRADING", ["note"] = "needs escaping: \" < > & '" },
    });

    public static CurrencyPair EurUsd() => new(new InstrumentSpec
    {
        Id = InstrumentId.Parse("EUR/USD.SIM"),
        AssetClass = AssetClass.Fx,
        InstrumentClass = InstrumentClass.Spot,
        QuoteCurrency = Currencies.USD,
        BaseCurrency = Currencies.EUR,
        PricePrecision = 5,
        SizePrecision = 0,
        PriceIncrement = Price.Parse("0.00001"),
        SizeIncrement = Quantity.Parse("1"),
    });

    public static CryptoPerpetual InversePerpetual() => new(new InstrumentSpec
    {
        Id = InstrumentId.Parse("BTCUSD-PERP.BYBIT"),
        AssetClass = AssetClass.Crypto,
        InstrumentClass = InstrumentClass.Swap,
        QuoteCurrency = Currencies.USD,
        BaseCurrency = Currencies.BTC,
        SettlementCurrency = Currencies.BTC,
        IsInverse = true,
        PricePrecision = 1,
        SizePrecision = 0,
        PriceIncrement = Price.Parse("0.5"),
        SizeIncrement = Quantity.Parse("1"),
        Multiplier = Quantity.Parse("1"),
        MarginInit = 0.01m,
        MarginMaint = 0.005m,
        MakerFee = -0.00025m,
        TakerFee = 0.00075m,
    });

    public static CryptoFuture DatedCryptoFuture() => new(
        new InstrumentSpec
        {
            Id = InstrumentId.Parse("BTCUSDT_250328.BINANCE"),
            AssetClass = AssetClass.Crypto,
            InstrumentClass = InstrumentClass.Future,
            QuoteCurrency = Currencies.USDT,
            BaseCurrency = Currencies.BTC,
            SettlementCurrency = Currencies.USDT,
            PricePrecision = 1,
            SizePrecision = 3,
            PriceIncrement = Price.Parse("0.1"),
            SizeIncrement = Quantity.Parse("0.001"),
        },
        Currencies.BTC,
        Activation,
        Expiration);

    public static Equity AppleEquity() => new(
        new InstrumentSpec
        {
            Id = InstrumentId.Parse("AAPL.XNAS"),
            AssetClass = AssetClass.Equity,
            InstrumentClass = InstrumentClass.Spot,
            QuoteCurrency = Currencies.USD,
            PricePrecision = 2,
            SizePrecision = 0,
            PriceIncrement = Price.Parse("0.01"),
            SizeIncrement = Quantity.Parse("1"),
            LotSize = Quantity.Parse("100"),
        },
        "US0378331005");

    public static FuturesContract EsFuture() => new(
        new InstrumentSpec
        {
            Id = InstrumentId.Parse("ESH5.XCME"),
            AssetClass = AssetClass.Index,
            InstrumentClass = InstrumentClass.Future,
            QuoteCurrency = Currencies.USD,
            PricePrecision = 2,
            SizePrecision = 0,
            PriceIncrement = Price.Parse("0.25"),
            SizeIncrement = Quantity.Parse("1"),
            Multiplier = Quantity.Parse("50"),
        },
        "ES",
        Activation,
        Expiration,
        "XCME");

    public static OptionContract ApplePut() => new(
        new InstrumentSpec
        {
            Id = InstrumentId.Parse("AAPL250620P00150000.OPRA"),
            AssetClass = AssetClass.Equity,
            InstrumentClass = InstrumentClass.Option,
            QuoteCurrency = Currencies.USD,
            PricePrecision = 2,
            SizePrecision = 0,
            PriceIncrement = Price.Parse("0.01"),
            SizeIncrement = Quantity.Parse("1"),
            Multiplier = Quantity.Parse("100"),
        },
        "AAPL",
        OptionKind.Put,
        Price.Parse("150.000"),
        Activation,
        Expiration,
        "OPRA");

    public static IReadOnlyList<Instrument> All() =>
        [BtcUsdt(), EurUsd(), InversePerpetual(), DatedCryptoFuture(), AppleEquity(), EsFuture(), ApplePut()];
}
