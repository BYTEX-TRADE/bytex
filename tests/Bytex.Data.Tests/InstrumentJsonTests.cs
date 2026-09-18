using Bytex.Core.Model;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Data.Tests.Support;

namespace Bytex.Data.Tests;

// Instrument definitions are the only non-Parquet part of the catalog. A definition that loses a precision,
// an increment or an expiry on the way to disk silently changes how every order for it is rounded and margined.
public class InstrumentJsonTests
{
    public static TheoryData<string> InstrumentIds() => new(TestInstruments.All().Select(i => i.Id.Value));

    [Theory]
    [MemberData(nameof(InstrumentIds))]
    public void Every_instrument_class_survives_a_round_trip_with_all_properties(string id)
    {
        Instrument original = TestInstruments.All().Single(i => i.Id.Value == id);

        Instrument restored = InstrumentJson.Deserialize(InstrumentJson.Serialize(original));

        InstrumentAssert.Same(original, restored);
    }

    [Fact]
    public void Increments_keep_their_precision_including_trailing_zeros()
    {
        CurrencyPair restored = (CurrencyPair)InstrumentJson.Deserialize(InstrumentJson.Serialize(TestInstruments.BtcUsdt()));

        Assert.Equal((byte)2, restored.PriceIncrement.Precision);
        Assert.Equal((byte)5, restored.SizeIncrement.Precision);
        Assert.Equal((byte)5, restored.LotSize.Precision); // written as "0.00010"
        Assert.Equal("0.00010", restored.LotSize.ToString());
        Assert.Equal((byte)2, restored.MaxPrice!.Value.Precision);
    }

    [Fact]
    public void Type_specific_fields_are_restored()
    {
        OptionContract option = (OptionContract)InstrumentJson.Deserialize(InstrumentJson.Serialize(TestInstruments.ApplePut()));
        Assert.Equal(OptionKind.Put, option.Kind);
        Assert.Equal(new Price(150m, 3), option.StrikePrice);
        Assert.Equal("AAPL", option.Underlying);
        Assert.Equal("OPRA", option.Exchange);
        Assert.Equal(TestInstruments.Expiration, option.Expiration);

        CryptoFuture future = (CryptoFuture)InstrumentJson.Deserialize(InstrumentJson.Serialize(TestInstruments.DatedCryptoFuture()));
        Assert.Equal(Currencies.BTC, future.Underlying);
        Assert.Equal(TestInstruments.Activation, future.Activation);
        Assert.Equal(1_735_689_599_999_999_999L, future.Expiration.Value);

        Equity equity = (Equity)InstrumentJson.Deserialize(InstrumentJson.Serialize(TestInstruments.AppleEquity()));
        Assert.Equal("US0378331005", equity.Isin);
    }

    [Fact]
    public void Absent_limits_stay_absent()
    {
        Instrument restored = InstrumentJson.Deserialize(InstrumentJson.Serialize(TestInstruments.EurUsd()));

        Assert.Null(restored.MaxQuantity);
        Assert.Null(restored.MinQuantity);
        Assert.Null(restored.MaxNotional);
        Assert.Null(restored.MinNotional);
        Assert.Null(restored.MaxPrice);
        Assert.Null(restored.MinPrice);
        Assert.Empty(restored.Info);
    }

    [Fact]
    public void A_negative_fee_is_a_rebate_and_keeps_its_sign()
    {
        Instrument restored = InstrumentJson.Deserialize(InstrumentJson.Serialize(TestInstruments.InversePerpetual()));

        Assert.Equal(-0.00025m, restored.MakerFee);
        Assert.Equal(0.00075m, restored.TakerFee);
        Assert.True(restored.IsInverse);
        Assert.Equal(Currencies.BTC, restored.SettlementCurrency);
    }

    [Fact]
    public void The_text_does_not_depend_on_the_current_culture()
    {
        string invariant = InstrumentJson.Serialize(TestInstruments.BtcUsdt());

        using (new CommaDecimalCulture())
        {
            Assert.Equal("0,5", 0.5m.ToString()); // guard: the culture switch is really in effect

            string underComma = InstrumentJson.Serialize(TestInstruments.BtcUsdt());
            Assert.Equal(invariant, underComma);

            InstrumentAssert.Same(TestInstruments.BtcUsdt(), InstrumentJson.Deserialize(invariant));
        }
    }

    [Fact]
    public void An_unknown_kind_is_rejected_and_named()
    {
        string json = InstrumentJson.Serialize(TestInstruments.AppleEquity()).Replace("\"Equity\"", "\"Bond\"", StringComparison.Ordinal);
        Assert.Contains("\"Bond\"", json, StringComparison.Ordinal); // guard: the replacement hit the discriminator

        FormatException error = Assert.Throws<FormatException>(() => InstrumentJson.Deserialize(json));

        Assert.Contains("Bond", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_json_null_is_rejected()
    {
        Assert.Throws<FormatException>(() => InstrumentJson.Deserialize("null"));
    }

    [Fact]
    public void A_null_instrument_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => InstrumentJson.Serialize(null!));
    }
}
