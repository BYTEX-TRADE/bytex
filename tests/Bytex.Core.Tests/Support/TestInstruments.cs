using Bytex.Core.Model;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;

namespace Bytex.Core.Tests.Support;

/// <summary>
/// Identifiers and instruments shared by the engine tests. Every instrument is built by hand so that the
/// limits a test relies on are visible at the call site.
/// </summary>
internal static class TestIds
{
    public static TraderId Trader => new("TESTER-001");

    public static StrategyId Strategy => new("S-001");

    public static StrategyId OtherStrategy => new("S-002");

    public static Venue Binance => new("BINANCE");

    public static Venue Bybit => new("BYBIT");

    public static InstrumentId BtcUsdt => new(new Symbol("BTCUSDT"), Binance);

    public static InstrumentId EthUsdt => new(new Symbol("ETHUSDT"), Binance);

    public static InstrumentId EthBtc => new(new Symbol("ETHBTC"), Binance);

    public static InstrumentId BtcUsdtBybit => new(new Symbol("BTCUSDT"), Bybit);

    public static InstrumentId BtcPerp => new(new Symbol("BTCUSDT-PERP"), Bybit);

    public static AccountId BinanceAccount => new("BINANCE-001");

    public static AccountId BybitAccount => new("BYBIT-001");
}

internal static class TestInstruments
{
    /// <summary>
    /// BTC/USDT spot: prices to 2 decimals, sizes to 3 decimals, no limits unless given.
    /// </summary>
    public static CurrencyPair BtcUsdt(
        Quantity? minQuantity = null,
        Quantity? maxQuantity = null,
        Money? minNotional = null,
        Money? maxNotional = null,
        Price? minPrice = null,
        Price? maxPrice = null,
        decimal priceIncrement = 0.01m,
        decimal sizeIncrement = 0.001m) => new(new InstrumentSpec
        {
            Id = TestIds.BtcUsdt,
            AssetClass = AssetClass.Crypto,
            InstrumentClass = InstrumentClass.Spot,
            BaseCurrency = Currencies.BTC,
            QuoteCurrency = Currencies.USDT,
            PricePrecision = 2,
            SizePrecision = 3,
            PriceIncrement = new Price(priceIncrement, 2),
            SizeIncrement = new Quantity(sizeIncrement, 3),
            MinQuantity = minQuantity,
            MaxQuantity = maxQuantity,
            MinNotional = minNotional,
            MaxNotional = maxNotional,
            MinPrice = minPrice,
            MaxPrice = maxPrice,
        });

    public static CurrencyPair EthUsdt() => Spot(TestIds.EthUsdt, Currencies.ETH, Currencies.USDT, 2, 3);

    public static CurrencyPair EthBtc() => Spot(TestIds.EthBtc, Currencies.ETH, Currencies.BTC, 5, 3);

    public static CurrencyPair BtcUsdtOnBybit() => Spot(TestIds.BtcUsdtBybit, Currencies.BTC, Currencies.USDT, 2, 3);

    public static CurrencyPair Spot(InstrumentId id, Currency baseCurrency, Currency quoteCurrency, byte pricePrecision, byte sizePrecision) => new(new InstrumentSpec
    {
        Id = id,
        AssetClass = AssetClass.Crypto,
        InstrumentClass = InstrumentClass.Spot,
        BaseCurrency = baseCurrency,
        QuoteCurrency = quoteCurrency,
        PricePrecision = pricePrecision,
        SizePrecision = sizePrecision,
        PriceIncrement = new Price(Pow10Inverse(pricePrecision), pricePrecision),
        SizeIncrement = new Quantity(Pow10Inverse(sizePrecision), sizePrecision),
    });

    /// <summary>Linear BTC/USDT perpetual on BYBIT settled in USDT.</summary>
    public static CryptoPerpetual BtcPerp() => new(new InstrumentSpec
    {
        Id = TestIds.BtcPerp,
        AssetClass = AssetClass.Crypto,
        InstrumentClass = InstrumentClass.Swap,
        BaseCurrency = Currencies.BTC,
        QuoteCurrency = Currencies.USDT,
        SettlementCurrency = Currencies.USDT,
        PricePrecision = 1,
        SizePrecision = 3,
        PriceIncrement = new Price(0.1m, 1),
        SizeIncrement = new Quantity(0.001m, 3),
        MarginInit = 0.05m,
        MarginMaint = 0.025m,
    });

    private static decimal Pow10Inverse(byte precision)
    {
        decimal value = 1m;
        for (int i = 0; i < precision; i++)
        {
            value /= 10m;
        }

        return value;
    }
}
