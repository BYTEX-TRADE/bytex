using System.Collections.Concurrent;

namespace Bytex.Core.Model.Primitives;

public enum CurrencyType
{
    Fiat,
    Crypto,
    CommodityBacked,
}

/// <summary>
/// A currency or asset used for pricing, settlement, or balances.
/// </summary>
public sealed record Currency
{
    private static readonly ConcurrentDictionary<string, Currency> Registry = new(StringComparer.Ordinal);

    public Currency(string code, byte precision, ushort isoCode, string name, CurrencyType type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(precision, (byte)18);
        Code = code;
        Precision = precision;
        IsoCode = isoCode;
        Name = name;
        Type = type;
    }

    public string Code { get; }

    public byte Precision { get; }

    public ushort IsoCode { get; }

    public string Name { get; }

    public CurrencyType Type { get; }

    public bool IsFiat => Type == CurrencyType.Fiat;

    public bool IsCrypto => Type == CurrencyType.Crypto;

    public bool Equals(Currency? other) => other is not null && string.Equals(Code, other.Code, StringComparison.Ordinal);

    public override int GetHashCode() => Code.GetHashCode(StringComparison.Ordinal);

    public override string ToString() => Code;

    /// <summary>
    /// Registers a currency so it can be resolved by code.
    /// </summary>
    public static Currency Register(Currency currency)
    {
        ArgumentNullException.ThrowIfNull(currency);
        Registry[currency.Code] = currency;
        return currency;
    }

    /// <summary>
    /// Returns the registered currency for the given code, or creates a crypto currency with the given precision.
    /// </summary>
    public static Currency FromCode(string code, byte defaultPrecision = 8)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        EnsureBuiltins();
        return Registry.GetOrAdd(code, c => new Currency(c, defaultPrecision, 0, c, CurrencyType.Crypto));
    }

    public static Currency? Find(string code)
    {
        EnsureBuiltins();
        return Registry.TryGetValue(code, out Currency? currency) ? currency : null;
    }

    public static bool IsRegistered(string code)
    {
        EnsureBuiltins();
        return Registry.ContainsKey(code);
    }

    public static IReadOnlyCollection<Currency> All
    {
        get
        {
            EnsureBuiltins();
            return Registry.Values.ToArray();
        }
    }

    private static void EnsureBuiltins() => System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(Currencies).TypeHandle);
}

/// <summary>
/// Built-in currency definitions.
/// </summary>
public static class Currencies
{
    public static readonly Currency AUD = new("AUD", 2, 36, "Australian dollar", CurrencyType.Fiat);
    public static readonly Currency BRL = new("BRL", 2, 986, "Brazilian real", CurrencyType.Fiat);
    public static readonly Currency CAD = new("CAD", 2, 124, "Canadian dollar", CurrencyType.Fiat);
    public static readonly Currency CHF = new("CHF", 2, 756, "Swiss franc", CurrencyType.Fiat);
    public static readonly Currency CNY = new("CNY", 2, 156, "Chinese yuan", CurrencyType.Fiat);
    public static readonly Currency EUR = new("EUR", 2, 978, "Euro", CurrencyType.Fiat);
    public static readonly Currency GBP = new("GBP", 2, 826, "British pound", CurrencyType.Fiat);
    public static readonly Currency HKD = new("HKD", 2, 344, "Hong Kong dollar", CurrencyType.Fiat);
    public static readonly Currency INR = new("INR", 2, 356, "Indian rupee", CurrencyType.Fiat);
    public static readonly Currency JPY = new("JPY", 0, 392, "Japanese yen", CurrencyType.Fiat);
    public static readonly Currency KRW = new("KRW", 0, 410, "South Korean won", CurrencyType.Fiat);
    public static readonly Currency MXN = new("MXN", 2, 484, "Mexican peso", CurrencyType.Fiat);
    public static readonly Currency NOK = new("NOK", 2, 578, "Norwegian krone", CurrencyType.Fiat);
    public static readonly Currency NZD = new("NZD", 2, 554, "New Zealand dollar", CurrencyType.Fiat);
    public static readonly Currency RUB = new("RUB", 2, 643, "Russian ruble", CurrencyType.Fiat);
    public static readonly Currency SEK = new("SEK", 2, 752, "Swedish krona", CurrencyType.Fiat);
    public static readonly Currency SGD = new("SGD", 2, 702, "Singapore dollar", CurrencyType.Fiat);
    public static readonly Currency TRY = new("TRY", 2, 949, "Turkish lira", CurrencyType.Fiat);
    public static readonly Currency USD = new("USD", 2, 840, "United States dollar", CurrencyType.Fiat);
    public static readonly Currency ZAR = new("ZAR", 2, 710, "South African rand", CurrencyType.Fiat);
    public static readonly Currency XAU = new("XAU", 3, 959, "Gold (troy ounce)", CurrencyType.CommodityBacked);
    public static readonly Currency XAG = new("XAG", 3, 961, "Silver (troy ounce)", CurrencyType.CommodityBacked);

    public static readonly Currency BTC = new("BTC", 8, 0, "Bitcoin", CurrencyType.Crypto);
    public static readonly Currency ETH = new("ETH", 8, 0, "Ether", CurrencyType.Crypto);
    public static readonly Currency USDT = new("USDT", 8, 0, "Tether", CurrencyType.Crypto);
    public static readonly Currency USDC = new("USDC", 8, 0, "USD Coin", CurrencyType.Crypto);
    public static readonly Currency BNB = new("BNB", 8, 0, "BNB", CurrencyType.Crypto);
    public static readonly Currency SOL = new("SOL", 8, 0, "Solana", CurrencyType.Crypto);
    public static readonly Currency XRP = new("XRP", 8, 0, "XRP", CurrencyType.Crypto);
    public static readonly Currency ADA = new("ADA", 8, 0, "Cardano", CurrencyType.Crypto);
    public static readonly Currency DOGE = new("DOGE", 8, 0, "Dogecoin", CurrencyType.Crypto);
    public static readonly Currency DOT = new("DOT", 8, 0, "Polkadot", CurrencyType.Crypto);
    public static readonly Currency LTC = new("LTC", 8, 0, "Litecoin", CurrencyType.Crypto);
    public static readonly Currency LINK = new("LINK", 8, 0, "Chainlink", CurrencyType.Crypto);
    public static readonly Currency AVAX = new("AVAX", 8, 0, "Avalanche", CurrencyType.Crypto);
    public static readonly Currency MATIC = new("MATIC", 8, 0, "Polygon", CurrencyType.Crypto);
    public static readonly Currency TRX = new("TRX", 8, 0, "TRON", CurrencyType.Crypto);
    public static readonly Currency XBT = new("XBT", 8, 0, "Bitcoin (XBT)", CurrencyType.Crypto);
    public static readonly Currency BUSD = new("BUSD", 8, 0, "Binance USD", CurrencyType.Crypto);
    public static readonly Currency DAI = new("DAI", 8, 0, "Dai", CurrencyType.Crypto);
    public static readonly Currency TUSD = new("TUSD", 8, 0, "TrueUSD", CurrencyType.Crypto);
    public static readonly Currency FDUSD = new("FDUSD", 8, 0, "First Digital USD", CurrencyType.Crypto);

    internal static IReadOnlyList<Currency> Builtin { get; } =
    [
        AUD, BRL, CAD, CHF, CNY, EUR, GBP, HKD, INR, JPY, KRW, MXN, NOK, NZD, RUB, SEK, SGD, TRY, USD, ZAR, XAU, XAG,
        BTC, ETH, USDT, USDC, BNB, SOL, XRP, ADA, DOGE, DOT, LTC, LINK, AVAX, MATIC, TRX, XBT, BUSD, DAI, TUSD, FDUSD,
    ];

    static Currencies()
    {
        foreach (Currency currency in Builtin)
        {
            Currency.Register(currency);
        }
    }
}
