using System.Text.Json;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Serialization;
using static Bytex.Core.Tests.Model.ModelFixtures;

namespace Bytex.Core.Tests.Model;

// The JSON form is what persistence and the HTTP surface exchange. Design note 0002 requires decimals to be
// stored as strings, never as floating point; these tests protect that and the loss-free round trip of the
// model's value types, identifiers, market data and order events.
public class ModelJsonTests
{
    private static readonly InstrumentId _btc = InstrumentId.Parse("BTCUSDT.BINANCE");

    [Theory]
    [InlineData("100.50")]
    [InlineData("0.00000001")]
    [InlineData("-37.63")]
    [InlineData("50000")]
    public void Price_is_written_as_a_string_that_keeps_its_precision(string text)
    {
        Price price = Price.Parse(text);

        string json = BytexJson.Serialize(price);

        Assert.Equal("\"" + text + "\"", json);
        Assert.Equal(price, BytexJson.Deserialize<Price>(json));
    }

    [Fact]
    public void Quantity_is_written_as_a_string_that_keeps_its_precision()
    {
        Quantity quantity = Quantity.Parse("1.500000");

        string json = BytexJson.Serialize(quantity);

        Assert.Equal("\"1.500000\"", json);
        Assert.Equal(quantity, BytexJson.Deserialize<Quantity>(json));
    }

    [Fact]
    public void Price_and_quantity_also_accept_json_numbers()
    {
        Assert.Equal(new Price(100.5m, 1), BytexJson.Deserialize<Price>("100.5"));
        Assert.Equal(new Quantity(3m, 0), BytexJson.Deserialize<Quantity>("3"));
    }

    [Theory]
    [InlineData("1234.50", "USD", "\"1234.50 USD\"")]
    [InlineData("-0.00000001", "BTC", "\"-0.00000001 BTC\"")]
    [InlineData("1500", "JPY", "\"1500 JPY\"")]
    public void Money_is_written_as_amount_and_currency_code(string amount, string currency, string expectedJson)
    {
        Money money = new(D(amount), Currency.FromCode(currency));

        string json = BytexJson.Serialize(money);

        Assert.Equal(expectedJson, json);
        Assert.Equal(money, BytexJson.Deserialize<Money>(json));
    }

    [Fact]
    public void Currency_is_written_as_its_code_and_resolved_from_the_registry()
    {
        Assert.Equal("\"BTC\"", BytexJson.Serialize(Currencies.BTC));
        Assert.Same(Currencies.BTC, BytexJson.Deserialize<Currency>("\"BTC\""));
    }

    [Fact]
    public void Timestamps_are_written_as_integer_nanoseconds_and_read_from_numbers_or_iso_text()
    {
        UnixNanos ts = new(T0Nanos + 123_456_701L);

        Assert.Equal("1700000000123456701", BytexJson.Serialize(ts));
        Assert.Equal(ts, BytexJson.Deserialize<UnixNanos>("1700000000123456701"));
        Assert.Equal(T0, BytexJson.Deserialize<UnixNanos>("\"2023-11-14T22:13:20Z\""));
    }

    [Fact]
    public void Identifiers_are_written_as_plain_strings()
    {
        Assert.Equal("\"BTCUSDT.BINANCE\"", BytexJson.Serialize(_btc));
        Assert.Equal("\"BINANCE\"", BytexJson.Serialize(new Venue("BINANCE")));
        Assert.Equal("\"O-1\"", BytexJson.Serialize(new ClientOrderId("O-1")));
        Assert.Equal("\"BINANCE-001\"", BytexJson.Serialize(Account));

        Assert.Equal(_btc, BytexJson.Deserialize<InstrumentId>("\"BTCUSDT.BINANCE\""));
        Assert.Equal(new TradeId("T-7"), BytexJson.Deserialize<TradeId>("\"T-7\""));
        Assert.Equal(Account, BytexJson.Deserialize<AccountId>("\"BINANCE-001\""));
        Assert.Equal("001", BytexJson.Deserialize<StrategyId>("\"EmaCross-001\"").Tag);
    }

    [Fact]
    public void Bar_type_and_specification_are_written_in_their_text_form()
    {
        BarType barType = BarType.Parse("BTCUSDT.BINANCE-5-MINUTE-BID-INTERNAL");

        Assert.Equal("\"BTCUSDT.BINANCE-5-MINUTE-BID-INTERNAL\"", BytexJson.Serialize(barType));
        Assert.Equal(barType, BytexJson.Deserialize<BarType>("\"BTCUSDT.BINANCE-5-MINUTE-BID-INTERNAL\""));
        Assert.Equal("\"5-MINUTE-BID\"", BytexJson.Serialize(barType.Spec));
        Assert.Equal(barType.Spec, BytexJson.Deserialize<BarSpecification>("\"5-MINUTE-BID\""));
    }

    [Theory]
    [InlineData(OrderSide.Buy, "\"buy\"")]
    [InlineData(OrderSide.Sell, "\"sell\"")]
    public void Enums_are_written_in_camel_case(OrderSide side, string expectedJson)
    {
        Assert.Equal(expectedJson, BytexJson.Serialize(side));
        Assert.Equal(side, BytexJson.Deserialize<OrderSide>(expectedJson));
    }

    [Fact]
    public void Multi_word_enums_are_camel_case_and_read_back_ignoring_case()
    {
        Assert.Equal("\"stopMarket\"", BytexJson.Serialize(OrderType.StopMarket));
        Assert.Equal("\"partiallyFilled\"", BytexJson.Serialize(OrderStatus.PartiallyFilled));
        Assert.Equal(OrderType.StopMarket, BytexJson.Deserialize<OrderType>("\"stopMarket\""));
        Assert.Equal(OrderType.StopMarket, BytexJson.Deserialize<OrderType>("\"StopMarket\""));
        Assert.Equal(TimeInForce.Gtd, BytexJson.Deserialize<TimeInForce>("4"));
    }

    [Fact]
    public void A_quote_tick_round_trips_with_prices_and_sizes_as_strings()
    {
        QuoteTick quote = new(_btc, Price.Parse("50000.10"), Price.Parse("50000.20"), Quantity.Parse("1.500000"), Quantity.Parse("0.250000"), T0, At(1));

        string json = BytexJson.Serialize(quote);

        Assert.Contains("\"instrumentId\":\"BTCUSDT.BINANCE\"", json, StringComparison.Ordinal);
        Assert.Contains("\"bid\":\"50000.10\"", json, StringComparison.Ordinal);
        Assert.Contains("\"askSize\":\"0.250000\"", json, StringComparison.Ordinal);
        Assert.Contains("\"tsEvent\":1700000000000000000", json, StringComparison.Ordinal);
        Assert.Equal(quote, BytexJson.Deserialize<QuoteTick>(json));
    }

    [Fact]
    public void A_trade_tick_round_trips()
    {
        TradeTick trade = new(_btc, Price.Parse("50000.10"), Quantity.Parse("0.250000"), AggressorSide.Seller, new TradeId("T-77"), T0, At(1));

        string json = BytexJson.Serialize(trade);

        Assert.Contains("\"aggressor\":\"seller\"", json, StringComparison.Ordinal);
        Assert.Contains("\"tradeId\":\"T-77\"", json, StringComparison.Ordinal);
        Assert.Equal(trade, BytexJson.Deserialize<TradeTick>(json));
    }

    [Fact]
    public void A_bar_round_trips()
    {
        Bar bar = new(
            BarType.Parse("BTCUSDT.BINANCE-1-MINUTE-LAST-EXTERNAL"),
            Price.Parse("100.00"), Price.Parse("100.50"), Price.Parse("99.50"), Price.Parse("100.25"), Quantity.Parse("12.500000"), T0, At(1));

        string json = BytexJson.Serialize(bar);

        Assert.Contains("\"barType\":\"BTCUSDT.BINANCE-1-MINUTE-LAST-EXTERNAL\"", json, StringComparison.Ordinal);
        Assert.Contains("\"close\":\"100.25\"", json, StringComparison.Ordinal);
        Assert.Equal(bar, BytexJson.Deserialize<Bar>(json));
    }

    [Fact]
    public void An_order_filled_event_round_trips_with_exact_amounts()
    {
        OrderFilled fill = Fill(BtcUsdt(), OrderSide.Sell, "0.250000", "50000.10", "T-77", Usdt("6.25001250"), t: 3);

        string json = BytexJson.Serialize(fill);
        OrderFilled? back = BytexJson.Deserialize<OrderFilled>(json);

        Assert.Contains("\"commission\":\"6.25001250 USDT\"", json, StringComparison.Ordinal);
        Assert.Contains("\"lastPx\":\"50000.10\"", json, StringComparison.Ordinal);
        Assert.Contains("\"orderSide\":\"sell\"", json, StringComparison.Ordinal);
        Assert.Equal(fill, back);
    }

    [Fact]
    public void Absent_optional_values_are_omitted_and_read_back_as_null()
    {
        OrderAccepted accepted = new(Trader, Strategy, _btc, new ClientOrderId("O-1"), null, null, Id(7), T0, T0);

        string json = BytexJson.Serialize(accepted);
        OrderAccepted? back = BytexJson.Deserialize<OrderAccepted>(json);

        Assert.DoesNotContain("venueOrderId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("accountId", json, StringComparison.Ordinal);
        Assert.Equal(accepted, back);
    }

    [Fact]
    public void Indented_options_change_layout_only()
    {
        JsonSerializerOptions indented = BytexJson.Create(indented: true);
        Money money = new(12.34m, Currencies.USD);

        Assert.True(indented.WriteIndented);
        Assert.False(BytexJson.Options.WriteIndented);
        Assert.Equal(money, JsonSerializer.Deserialize<Money>(JsonSerializer.Serialize(money, indented), indented));
    }

    [Theory]
    [InlineData("0.1", "0.1")]
    [InlineData("1234567.891", "1234567.891")]
    [InlineData("0.00000001", "0.00000001")]
    [InlineData("100", "100")]
    public void FormatDecimal_is_invariant_and_never_uses_exponents(string value, string expected)
    {
        Assert.Equal(expected, BytexJson.FormatDecimal(D(value)));
    }
}
