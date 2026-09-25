using Bytex.Core.Model.Primitives;

namespace Bytex.Core.Tests.Model;

// Currency decides how many decimals every Money amount keeps, so the built-in table and the registry lookup
// rules are load-bearing. The registry is process-wide: tests that add to it use codes no other test touches.
public class CurrencyTests
{
    [Theory]
    [InlineData("USD", 2, 840, CurrencyType.Fiat)]
    [InlineData("EUR", 2, 978, CurrencyType.Fiat)]
    [InlineData("JPY", 0, 392, CurrencyType.Fiat)]
    [InlineData("KRW", 0, 410, CurrencyType.Fiat)]
    [InlineData("XAU", 3, 959, CurrencyType.CommodityBacked)]
    [InlineData("BTC", 8, 0, CurrencyType.Crypto)]
    [InlineData("USDT", 8, 0, CurrencyType.Crypto)]
    public void Built_in_currencies_carry_their_standard_precision_and_iso_number(string code, byte precision, ushort isoCode, CurrencyType type)
    {
        Currency? currency = Currency.Find(code);

        Assert.NotNull(currency);
        Assert.Equal(code, currency.Code);
        Assert.Equal(precision, currency.Precision);
        Assert.Equal(isoCode, currency.IsoCode);
        Assert.Equal(type, currency.Type);
        Assert.Equal(type == CurrencyType.Fiat, currency.IsFiat);
        Assert.Equal(type == CurrencyType.Crypto, currency.IsCrypto);
    }

    [Fact]
    public void FromCode_returns_the_built_in_instance_and_ignores_the_default_precision_for_it()
    {
        Currency usd = Currency.FromCode("USD", defaultPrecision: 6);

        Assert.Same(Currencies.USD, usd);
        Assert.Equal(2, usd.Precision);
    }

    [Fact]
    public void FromCode_creates_an_unknown_code_once_as_crypto_with_the_default_precision()
    {
        Assert.False(Currency.IsRegistered("ZZNEW"));

        Currency first = Currency.FromCode("ZZNEW", defaultPrecision: 6);
        Currency second = Currency.FromCode("ZZNEW", defaultPrecision: 2);

        Assert.Equal(6, first.Precision);
        Assert.Equal(CurrencyType.Crypto, first.Type);
        Assert.Same(first, second);
        Assert.True(Currency.IsRegistered("ZZNEW"));
        Assert.Same(first, Currency.Find("ZZNEW"));
    }

    [Fact]
    public void FromCode_defaults_unknown_codes_to_eight_decimals()
    {
        Assert.Equal(8, Currency.FromCode("ZZDEFAULT").Precision);
    }

    [Fact]
    public void Find_returns_null_for_an_unknown_code_without_registering_it()
    {
        Assert.Null(Currency.Find("ZZMISSING"));
        Assert.False(Currency.IsRegistered("ZZMISSING"));
    }

    [Fact]
    public void Lookup_is_case_sensitive()
    {
        Assert.Null(Currency.Find("usd"));
    }

    [Fact]
    public void Register_makes_a_custom_currency_resolvable_by_code()
    {
        Currency points = new("ZZPOINTS", 4, 0, "Loyalty points", CurrencyType.CommodityBacked);

        Currency registered = Currency.Register(points);

        Assert.Same(points, registered);
        Assert.Same(points, Currency.FromCode("ZZPOINTS"));
        Assert.Contains(points, Currency.All);
    }

    [Fact]
    public void All_contains_every_built_in_currency()
    {
        IReadOnlyCollection<Currency> all = Currency.All;

        // 20 fiat + 2 metals + 20 crypto.
        Assert.True(all.Count >= 42, $"expected at least 42 currencies, found {all.Count}");
        Assert.Contains(Currencies.USD, all);
        Assert.Contains(Currencies.XAG, all);
        Assert.Contains(Currencies.FDUSD, all);
    }

    [Fact]
    public void Equality_is_by_code_only()
    {
        Currency lookalike = new("USD", 4, 0, "Not the real one", CurrencyType.Crypto);

        Assert.Equal(Currencies.USD, lookalike);
        Assert.Equal(Currencies.USD.GetHashCode(), lookalike.GetHashCode());
        Assert.NotEqual(Currencies.USD, Currencies.USDT);
        Assert.False(Currencies.USD.Equals(null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Constructor_rejects_a_missing_code(string? code)
    {
        Assert.ThrowsAny<ArgumentException>(() => new Currency(code!, 2, 0, "x", CurrencyType.Fiat));
    }

    [Fact]
    public void Constructor_rejects_precision_above_18()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Currency("ZZWIDE", 19, 0, "x", CurrencyType.Crypto));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void FromCode_rejects_a_missing_code(string? code)
    {
        Assert.ThrowsAny<ArgumentException>(() => Currency.FromCode(code!));
    }

    [Fact]
    public void ToString_is_the_code()
    {
        Assert.Equal("BTC", Currencies.BTC.ToString());
    }
}
