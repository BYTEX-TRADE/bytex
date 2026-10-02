using Bytex.Core.Model.Identifiers;

namespace Bytex.Core.Tests.Model;

// Identifiers key every dictionary in the cache and every topic on the bus. These tests protect constructor
// validation, the bx-market:v2/VENUE/SYMBOL and VENUE-NUMBER formats, derived tags, and ordinal value equality.
public class IdentifierTests
{
    public static TheoryData<string, Func<string, object>> StringIdentifiers => new()
    {
        { nameof(Venue), v => new Venue(v) },
        { nameof(Symbol), v => new Symbol(v) },
        { nameof(ModuleHostId), v => new ModuleHostId(v) },
        { nameof(StrategyId), v => new StrategyId(v) },
        { nameof(RuntimeModuleId), v => new RuntimeModuleId(v) },
        { nameof(OrderScheduleId), v => new OrderScheduleId(v) },
        { nameof(ComponentId), v => new ComponentId(v) },
        { nameof(ClientId), v => new ClientId(v) },
        { nameof(AccountId), v => new AccountId(v) },
        { nameof(ClientOrderId), v => new ClientOrderId(v) },
        { nameof(VenueOrderId), v => new VenueOrderId(v) },
        { nameof(TradeId), v => new TradeId(v) },
        { nameof(PositionId), v => new PositionId(v) },
        { nameof(OrderListId), v => new OrderListId(v) },
    };

    [Theory]
    [MemberData(nameof(StringIdentifiers))]
    public void Every_identifier_rejects_null_empty_and_whitespace(string typeName, Func<string, object> create)
    {
        Assert.NotEmpty(typeName);
        Assert.Throws<ArgumentException>(() => create(null!));
        Assert.Throws<ArgumentException>(() => create(string.Empty));
        Assert.Throws<ArgumentException>(() => create("   "));
    }

    [Theory]
    [MemberData(nameof(StringIdentifiers))]
    public void Every_identifier_renders_as_its_value_and_compares_by_value(string typeName, Func<string, object> create)
    {
        Assert.NotEmpty(typeName);
        object first = create("ABC-001");
        object same = create("ABC-001");
        object differentCase = create("abc-001");

        Assert.Equal("ABC-001", first.ToString());
        Assert.Equal(first, same);
        Assert.Equal(first.GetHashCode(), same.GetHashCode());
        Assert.NotEqual(first, differentCase);
    }

    [Fact]
    public void Identifiers_of_different_types_with_the_same_text_are_not_equal()
    {
        object clientOrderId = new ClientOrderId("X-1");
        object venueOrderId = new VenueOrderId("X-1");

        Assert.False(clientOrderId.Equals(venueOrderId));
    }

    [Fact]
    public void Venue_rejects_a_dot_because_it_separates_symbol_from_venue()
    {
        Assert.Throws<ArgumentException>(() => new Venue("BINANCE.SPOT"));
    }

    [Fact]
    public void Venue_and_Symbol_convert_implicitly_to_string()
    {
        string venue = new Venue("BINANCE");
        string symbol = new Symbol("BTCUSDT");

        Assert.Equal("BINANCE", venue);
        Assert.Equal("BTCUSDT", symbol);
        Assert.Equal("SIM", Venue.Simulated.Value);
    }

    [Theory]
    [InlineData("bx-market:v2/BINANCE/BTCUSDT", "BTCUSDT", "BINANCE")]
    [InlineData("bx-market:v2/NYSE/BRK.B", "BRK.B", "NYSE")] // the last dot separates the venue, so symbols may contain dots
    [InlineData("bx-market:v2/BINANCE/ETHUSDT-PERP", "ETHUSDT-PERP", "BINANCE")]
    [InlineData("bx-market:v2/SIM/EUR%2FUSD", "EUR/USD", "SIM")]
    public void MarketKey_Parse_splits_on_the_last_dot(string text, string symbol, string venue)
    {
        MarketKey id = MarketKey.Parse(text);

        Assert.Equal(symbol, id.Symbol.Value);
        Assert.Equal(venue, id.Venue.Value);
        Assert.Equal(text, id.Value);
        Assert.Equal(text, id.ToString());
    }

    [Fact]
    public void MarketKey_built_from_parts_equals_the_parsed_form()
    {
        MarketKey built = new(new Symbol("BTCUSDT"), new Venue("BINANCE"));

        Assert.Equal("bx-market:v2/BINANCE/BTCUSDT", built.Value);
        Assert.Equal(MarketKey.Parse("bx-market:v2/BINANCE/BTCUSDT"), built);
        Assert.Equal(MarketKey.Parse("bx-market:v2/BINANCE/BTCUSDT").GetHashCode(), built.GetHashCode());
        Assert.True(built == MarketKey.Parse("bx-market:v2/BINANCE/BTCUSDT"));
    }

    [Theory]
    [InlineData("BTCUSDT")]
    [InlineData(".BINANCE")]
    [InlineData("BTCUSDT.")]
    [InlineData(".")]
    public void MarketKey_Parse_rejects_text_without_both_parts(string text)
    {
        Assert.Throws<FormatException>(() => MarketKey.Parse(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void MarketKey_Parse_rejects_missing_text(string? text)
    {
        Assert.Throws<ArgumentException>(() => MarketKey.Parse(text!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("BTCUSDT")]
    [InlineData(".BINANCE")]
    [InlineData("BTCUSDT.")]
    public void MarketKey_TryParse_returns_false_for_invalid_text(string? text)
    {
        Assert.False(MarketKey.TryParse(text, out MarketKey id));
        Assert.Equal(default, id);
    }

    [Theory]
    [InlineData("BTCUSDT. ")]
    [InlineData(" .BINANCE")]
    public void MarketKey_TryParse_returns_false_when_a_part_is_blank(string text)
    {
        Assert.False(MarketKey.TryParse(text, out _));
    }

    [Fact]
    public void MarketKey_TryParse_returns_the_same_id_as_Parse()
    {
        Assert.True(MarketKey.TryParse("bx-market:v2/NYSE/BRK.B", out MarketKey id));
        Assert.Equal(MarketKey.Parse("bx-market:v2/NYSE/BRK.B"), id);
    }

    [Fact]
    public void MarketKey_is_case_sensitive_and_sorts_ordinally()
    {
        MarketKey upper = MarketKey.Parse("bx-market:v2/BINANCE/BTCUSDT");
        MarketKey lower = MarketKey.Parse("bx-market:v2/BINANCE/btcusdt");
        List<MarketKey> ids = [MarketKey.Parse("bx-market:v2/BINANCE/ETHUSDT"), lower, upper, MarketKey.Parse("bx-market:v2/BINANCE/ADAUSDT")];

        ids.Sort();

        Assert.NotEqual(upper, lower);
        Assert.Equal(["bx-market:v2/BINANCE/ADAUSDT", "bx-market:v2/BINANCE/BTCUSDT", "bx-market:v2/BINANCE/ETHUSDT", "bx-market:v2/BINANCE/btcusdt"], ids.Select(i => i.Value));
    }

    [Theory]
    [InlineData("TRADER-001", "001")]
    [InlineData("DESK-LONDON-7", "7")]
    [InlineData("SOLO", "SOLO")]
    public void ModuleHostId_tag_is_the_segment_after_the_last_hyphen(string value, string expectedTag)
    {
        Assert.Equal(expectedTag, new ModuleHostId(value).Tag);
    }

    [Theory]
    [InlineData("EmaCross-001", "001")]
    [InlineData("Mean-Reversion-B", "B")]
    [InlineData("Scalper", "Scalper")]
    public void StrategyId_tag_is_the_segment_after_the_last_hyphen(string value, string expectedTag)
    {
        Assert.Equal(expectedTag, new StrategyId(value).Tag);
    }

    [Fact]
    public void StrategyId_External_marks_orders_no_strategy_claims()
    {
        Assert.True(StrategyId.External.IsProvider);
        Assert.True(new StrategyId("EXTERNAL").IsProvider);
        Assert.False(new StrategyId("EmaCross-001").IsProvider);
    }

    [Theory]
    [InlineData("BINANCE-123456", "BINANCE", "123456")]
    [InlineData("BINANCE-SPOT-001", "BINANCE", "SPOT-001")] // the first hyphen ends the issuer
    [InlineData("SIM-000", "SIM", "000")]
    public void AccountId_splits_into_issuer_and_number(string value, string issuer, string number)
    {
        AccountId id = new(value);

        Assert.Equal(issuer, id.Issuer);
        Assert.Equal(number, id.Number);
        Assert.Equal(new Venue(issuer), id.Venue);
        Assert.Equal(value, id.Value);
    }

    [Theory]
    [InlineData("BINANCE")]
    [InlineData("-123456")]
    [InlineData("BINANCE-")]
    public void AccountId_rejects_text_that_is_not_issuer_hyphen_number(string value)
    {
        Assert.Throws<ArgumentException>(() => new AccountId(value));
    }

    [Theory]
    [InlineData("bx-market:v2/%20/BTCUSDT")]
    [InlineData("bx-market:v2/BINANCE/%20")]
    public void MarketKey_Parse_refuses_what_TryParse_refuses(string text)
    {
        Assert.ThrowsAny<ArgumentException>(() => MarketKey.Parse(text));
    }
}
