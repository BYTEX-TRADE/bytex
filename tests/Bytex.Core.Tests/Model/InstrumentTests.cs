using Bytex.Core.Model;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using static Bytex.Core.Tests.Model.ModelFixtures;

namespace Bytex.Core.Tests.Model;

// An instrument turns raw decimals into venue-valid prices and sizes and converts size * price into money.
// These tests protect tick rounding, lot rounding, and the notional / margin / commission formulas for
// linear, multiplied and inverse contracts, all with hand-computed values.
public class InstrumentTests
{
    [Theory]
    [InlineData("50000.126", "50000.13")]
    [InlineData("50000.124", "50000.12")]
    [InlineData("50000.125", "50000.12")] // 5000012.5 ticks is a midpoint, 5000012 is even
    [InlineData("50000.135", "50000.14")] // 5000013.5 ticks is a midpoint, 5000013 is odd
    [InlineData("50000", "50000.00")]
    public void MakePrice_rounds_to_the_nearest_cent_tick(string raw, string expected)
    {
        Assert.Equal(expected, BtcUsdt().MakePrice(D(raw)).ToString());
    }

    [Theory]
    [InlineData("10000.2", "10000.0")] // 20000.4 half-point ticks
    [InlineData("10000.3", "10000.5")] // 20000.6
    [InlineData("10000.25", "10000.0")] // 20000.5 is a midpoint, 20000 is even
    [InlineData("10000.75", "10001.0")] // 20001.5 is a midpoint, 20001 is odd
    public void MakePrice_rounds_to_a_half_point_tick(string raw, string expected)
    {
        Assert.Equal(expected, XbtUsdInverse().MakePrice(D(raw)).ToString());
    }

    [Theory]
    [InlineData("4500.30", "4500.25")] // 18001.2 quarter ticks
    [InlineData("4500.40", "4500.50")] // 18001.6
    [InlineData("4500.00", "4500.00")]
    [InlineData("-0.30", "-0.25")] // spreads and some futures trade below zero
    public void MakePrice_rounds_to_a_quarter_point_tick(string raw, string expected)
    {
        Assert.Equal(expected, EsFuture().MakePrice(D(raw)).ToString());
    }

    [Theory]
    [InlineData("1.2399", "1.239")]
    [InlineData("1.2390", "1.239")]
    [InlineData("0.0009", "0.000")]
    [InlineData("-5", "0.000")] // never negative
    public void MakeQuantity_rounds_down_to_the_size_step_by_default(string raw, string expected)
    {
        Assert.Equal(expected, EthPerp().MakeQuantity(D(raw)).ToString());
    }

    [Theory]
    [InlineData("1.2396", "1.240")]
    [InlineData("1.2394", "1.239")]
    [InlineData("1.2395", "1.240")] // 1239.5 steps is a midpoint, 1239 is odd
    [InlineData("1.2385", "1.238")] // 1238.5 steps is a midpoint, 1238 is even
    public void MakeQuantity_can_round_to_the_nearest_size_step(string raw, string expected)
    {
        Assert.Equal(expected, EthPerp().MakeQuantity(D(raw), roundDown: false).ToString());
    }

    [Fact]
    public void MakeQuantity_honours_whole_contract_steps()
    {
        Assert.Equal("7", EsFuture().MakeQuantity(7.9m).ToString());
        Assert.Equal("8", EsFuture().MakeQuantity(7.9m, roundDown: false).ToString());
    }

    [Fact]
    public void Optional_spec_fields_fall_back_to_sensible_defaults()
    {
        CurrencyPair instrument = BtcUsdt();

        Assert.Equal(new Quantity(1m, 0), instrument.Multiplier);
        Assert.Equal(instrument.SizeIncrement, instrument.LotSize);
        Assert.Equal(Currencies.USDT, instrument.SettlementCurrency);
        Assert.Equal(new Symbol("BTCUSDT"), instrument.RawSymbol);
        Assert.Equal(new Venue("BINANCE"), instrument.Venue);
        Assert.Empty(instrument.Info);
        Assert.Null(instrument.MaxQuantity);
        Assert.Null(instrument.MinNotional);
        Assert.Equal("CurrencyPair(BTCUSDT.BINANCE)", instrument.ToString());
    }

    [Fact]
    public void Explicit_spec_fields_are_kept()
    {
        InstrumentSpec spec = BtcUsdtSpec() with
        {
            RawSymbol = new Symbol("BTC/USDT"),
            LotSize = new Quantity(0.001m, 3),
            MinQuantity = new Quantity(0.0001m, 4),
            MaxQuantity = new Quantity(9000m, 0),
            MinNotional = new Money(10m, Currencies.USDT),
            MaxPrice = new Price(1_000_000m, 2),
            MinPrice = new Price(0.01m, 2),
            Info = new Dictionary<string, string> { ["status"] = "TRADING" },
        };

        CurrencyPair instrument = new(spec);

        Assert.Equal("BTC/USDT", instrument.RawSymbol.Value);
        Assert.Equal(new Quantity(0.001m, 3), instrument.LotSize);
        Assert.Equal(new Quantity(0.0001m, 4), instrument.MinQuantity);
        Assert.Equal(new Quantity(9000m, 0), instrument.MaxQuantity);
        Assert.Equal(new Money(10m, Currencies.USDT), instrument.MinNotional);
        Assert.Equal(new Price(1_000_000m, 2), instrument.MaxPrice);
        Assert.Equal(new Price(0.01m, 2), instrument.MinPrice);
        Assert.Equal("TRADING", instrument.Info["status"]);
    }

    // A constructor cannot check its argument before the base call, so every subclass used to dereference the spec
    // while classifying it: a missing spec threw NullReferenceException, which names nothing anyone can act on.
    [Fact]
    public void Constructor_rejects_a_missing_spec()
    {
        // Every kind of instrument, because each one classifies the spec it is handed before the base sees it.
        Assert.Throws<ArgumentNullException>(() => new CurrencyPair(null!));
        Assert.Throws<ArgumentNullException>(() => new CryptoPerpetual(null!));
        Assert.Throws<ArgumentNullException>(() => new CryptoFuture(null!, Currencies.BTC, UnixNanos.Zero, UnixNanos.Zero));
        Assert.Throws<ArgumentNullException>(() => new Equity(null!));
        Assert.Throws<ArgumentNullException>(() => new FuturesContract(null!, "ES", UnixNanos.Zero, UnixNanos.Zero));
        Assert.Throws<ArgumentNullException>(() => new OptionContract(null!, "ES", OptionKind.Call, Price.Parse("1.00"), UnixNanos.Zero, UnixNanos.Zero));
    }

    [Fact]
    public void Constructor_rejects_a_zero_price_increment()
    {
        InstrumentSpec spec = BtcUsdtSpec() with { PriceIncrement = Price.Zero(2) };

        Assert.Throws<ArgumentException>(() => new CurrencyPair(spec));
    }

    [Fact]
    public void Constructor_rejects_a_negative_price_increment()
    {
        InstrumentSpec spec = BtcUsdtSpec() with { PriceIncrement = new Price(-0.01m, 2) };

        Assert.Throws<ArgumentException>(() => new CurrencyPair(spec));
    }

    [Fact]
    public void Constructor_rejects_a_zero_size_increment()
    {
        InstrumentSpec spec = BtcUsdtSpec() with { SizeIncrement = Quantity.Zero(6) };

        Assert.Throws<ArgumentException>(() => new CurrencyPair(spec));
    }

    [Fact]
    public void Constructor_rejects_precisions_above_18()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CurrencyPair(BtcUsdtSpec() with { PricePrecision = 19 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CurrencyPair(BtcUsdtSpec() with { SizePrecision = 19 }));
    }

    [Fact]
    public void Pairs_and_perpetuals_require_a_base_currency()
    {
        Assert.Throws<ArgumentException>(() => new CurrencyPair(BtcUsdtSpec() with { BaseCurrency = null }));
        Assert.Throws<ArgumentException>(() => new CryptoPerpetual(EthPerpSpec() with { BaseCurrency = null }));
    }

    [Fact]
    public void Each_instrument_type_fixes_its_own_classification_whatever_the_spec_says()
    {
        InstrumentSpec wrong = BtcUsdtSpec() with { AssetClass = AssetClass.Debt, InstrumentClass = InstrumentClass.Warrant };

        CurrencyPair pair = new(wrong);
        CryptoPerpetual perpetual = new(wrong);
        CryptoFuture cryptoFuture = new(wrong, Currencies.BTC, T0, At(3600));
        Equity equity = new(wrong, "US0378331005");
        FuturesContract future = new(wrong, "ES", T0, At(3600), "CME");
        OptionContract option = new(wrong, "ES", OptionKind.Put, new Price(4500m, 2), T0, At(3600));

        Assert.Equal((AssetClass.Debt, InstrumentClass.Spot), (pair.AssetClass, pair.InstrumentClass));
        Assert.Equal((AssetClass.Crypto, InstrumentClass.Swap), (perpetual.AssetClass, perpetual.InstrumentClass));
        Assert.Equal((AssetClass.Crypto, InstrumentClass.Future), (cryptoFuture.AssetClass, cryptoFuture.InstrumentClass));
        Assert.Equal((AssetClass.Equity, InstrumentClass.Spot), (equity.AssetClass, equity.InstrumentClass));
        Assert.Equal((AssetClass.Debt, InstrumentClass.Future), (future.AssetClass, future.InstrumentClass));
        Assert.Equal((AssetClass.Debt, InstrumentClass.Option), (option.AssetClass, option.InstrumentClass));
        Assert.Equal(At(3600), cryptoFuture.Expiration);
        Assert.Equal("US0378331005", equity.Isin);
        Assert.Equal(OptionKind.Put, option.Kind);
        Assert.Equal(new Price(4500m, 2), option.StrikePrice);
    }

    [Fact]
    public void Notional_of_a_linear_instrument_is_quantity_times_price_in_quote_currency()
    {
        // 0.5 BTC * 50 000.00 = 25 000 USDT.
        Money notional = BtcUsdt().NotionalValue(Quantity.Parse("0.500000"), Price.Parse("50000.00"));

        Assert.Equal(new Money(25_000m, Currencies.USDT), notional);
    }

    [Fact]
    public void Notional_includes_the_contract_multiplier()
    {
        // 2 contracts * 4500.25 points * 50 USD per point = 450 025.00 USD.
        Money notional = EsFuture().NotionalValue(Quantity.Parse("2"), Price.Parse("4500.25"));

        Assert.Equal(new Money(450_025.00m, Currencies.USD), notional);
    }

    [Theory]
    [InlineData("100000", "50000.0", "2")] // 100 000 USD / 50 000 USD per BTC
    [InlineData("100000", "12500.0", "8")]
    [InlineData("100", "30000.0", "0.00333333")] // 0.0033333... BTC rounded to 8 decimals
    [InlineData("1", "64000.0", "0.00001562")] // 0.000015625 is a midpoint at 8 decimals, 2 is even
    public void Notional_of_an_inverse_instrument_is_quantity_over_price_in_base_currency(string contracts, string price, string expectedBtc)
    {
        Money notional = XbtUsdInverse().NotionalValue(Quantity.Parse(contracts), Price.Parse(price));

        Assert.Equal(new Money(D(expectedBtc), Currencies.BTC), notional);
    }

    [Fact]
    public void Notional_of_an_inverse_instrument_can_be_asked_for_in_quote_currency()
    {
        Money notional = XbtUsdInverse().NotionalValue(Quantity.Parse("100000"), Price.Parse("50000.0"), useQuoteForInverse: true);

        Assert.Equal(new Money(100_000m, Currencies.USD), notional);
    }

    [Fact]
    public void Cost_currency_is_quote_for_linear_and_base_for_inverse()
    {
        Assert.Equal(Currencies.USDT, EthPerp().CostCurrency);
        Assert.Equal(Currencies.BTC, XbtUsdInverse().CostCurrency);
        Assert.True(XbtUsdInverse().IsInverse);
        Assert.False(EthPerp().IsInverse);
    }

    [Fact]
    public void Margins_are_a_fraction_of_notional()
    {
        CryptoPerpetual perp = EthPerp();
        Quantity quantity = Quantity.Parse("2.000");
        Price price = Price.Parse("2000.00");

        // Notional 4000 USDT; 5% initial = 200, 2.5% maintenance = 100.
        Assert.Equal(new Money(200m, Currencies.USDT), perp.CalculateInitialMargin(quantity, price));
        Assert.Equal(new Money(100m, Currencies.USDT), perp.CalculateMaintenanceMargin(quantity, price));
    }

    [Fact]
    public void Inverse_margins_are_in_base_currency()
    {
        // Notional 100 000 / 50 000 = 2 BTC; 1% initial = 0.02 BTC, 0.5% maintenance = 0.01 BTC.
        CryptoPerpetual inverse = XbtUsdInverse();

        Assert.Equal(new Money(0.02m, Currencies.BTC), inverse.CalculateInitialMargin(Quantity.Parse("100000"), Price.Parse("50000.0")));
        Assert.Equal(new Money(0.01m, Currencies.BTC), inverse.CalculateMaintenanceMargin(Quantity.Parse("100000"), Price.Parse("50000.0")));
    }

    [Fact]
    public void Commission_uses_the_maker_or_taker_rate_on_notional()
    {
        CryptoPerpetual perp = EthPerp();
        Quantity quantity = Quantity.Parse("2.000");
        Price price = Price.Parse("2000.00");

        // Notional 4000 USDT; maker 2 bps = 0.80, taker 5 bps = 2.00.
        Assert.Equal(new Money(0.8m, Currencies.USDT), perp.CalculateCommission(quantity, price, LiquiditySide.Maker));
        Assert.Equal(new Money(2m, Currencies.USDT), perp.CalculateCommission(quantity, price, LiquiditySide.Taker));
    }

    [Fact]
    public void A_negative_maker_fee_is_a_rebate()
    {
        CryptoPerpetual perp = new(EthPerpSpec() with { MakerFee = -0.0001m });

        // 4000 USDT * -1 bp = -0.40 USDT.
        Money commission = perp.CalculateCommission(Quantity.Parse("2.000"), Price.Parse("2000.00"), LiquiditySide.Maker);

        Assert.Equal(new Money(-0.4m, Currencies.USDT), commission);
    }

    [Fact]
    public void Inverse_commission_is_charged_in_base_currency()
    {
        // 2 BTC notional * 7.5 bps taker = 0.0015 BTC.
        Money commission = XbtUsdInverse().CalculateCommission(Quantity.Parse("100000"), Price.Parse("50000.0"), LiquiditySide.Taker);

        Assert.Equal(new Money(0.0015m, Currencies.BTC), commission);
    }
}
