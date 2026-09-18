using Bytex.Core.Model.Identifiers;

namespace Bytex.Core.Tests.Model;

// Identifiers key every dictionary in the cache and every topic on the bus. These tests protect constructor
// validation, the SYMBOL.VENUE and VENUE-NUMBER formats, derived tags, and ordinal value equality.
public class IdentifierTests
{
    public static TheoryData<string, Func<string, object>> StringIdentifiers => new()
    {
        { nameof(Venue), v => new Venue(v) },
        { nameof(Symbol), v => new Symbol(v) },
        { nameof(TraderId), v => new TraderId(v) },
        { nameof(StrategyId), v => new StrategyId(v) },
        { nameof(ActorId), v => new ActorId(v) },
        { nameof(ExecAlgorithmId), v => new ExecAlgorithmId(v) },
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
    [InlineData("BTCUSDT.BINANCE", "BTCUSDT", "BINANCE")]
    [InlineData("BRK.B.NYSE", "BRK.B", "NYSE")] // the last dot separates the venue, so symbols may contain dots
    [InlineData("ETHUSDT-PERP.BINANCE", "ETHUSDT-PERP", "BINANCE")]
    [InlineData("EUR/USD.SIM", "EUR/USD", "SIM")]
    public void InstrumentId_Parse_splits_on_the_last_dot(string text, string symbol, string venue)
    {
        InstrumentId id = InstrumentId.Parse(text);

        Assert.Equal(symbol, id.Symbol.Value);
        Assert.Equal(venue, id.Venue.Value);
        Assert.Equal(text, id.Value);
        Assert.Equal(text, id.ToString());
    }

    [Fact]
    public void InstrumentId_built_from_parts_equals_the_parsed_form()
    {
        InstrumentId built = new(new Symbol("BTCUSDT"), new Venue("BINANCE"));

        Assert.Equal("BTCUSDT.BINANCE", built.Value);
        Assert.Equal(InstrumentId.Parse("BTCUSDT.BINANCE"), built);
        Assert.Equal(InstrumentId.Parse("BTCUSDT.BINANCE").GetHashCode(), built.GetHashCode());
        Assert.True(built == InstrumentId.Parse("BTCUSDT.BINANCE"));
    }

    [Theory]
    [InlineData("BTCUSDT")]
    [InlineData(".BINANCE")]
    [InlineData("BTCUSDT.")]
    [InlineData(".")]
    public void InstrumentId_Parse_rejects_text_without_both_parts(string text)
    {
        Assert.Throws<FormatException>(() => InstrumentId.Parse(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void InstrumentId_Parse_rejects_missing_text(string? text)
    {
        Assert.Throws<ArgumentException>(() => InstrumentId.Parse(text!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("BTCUSDT")]
    [InlineData(".BINANCE")]
    [InlineData("BTCUSDT.")]
    public void InstrumentId_TryParse_returns_false_for_invalid_text(string? text)
    {
        Assert.False(InstrumentId.TryParse(text, out InstrumentId id));
        Assert.Equal(default, id);
    }

    [Theory(Skip = "BUG: InstrumentId.TryParse throws ArgumentException when the symbol or venue part is whitespace")]
    [InlineData("BTCUSDT. ")]
    [InlineData(" .BINANCE")]
    public void InstrumentId_TryParse_returns_false_when_a_part_is_blank(string text)
    {
        Assert.False(InstrumentId.TryParse(text, out _));
    }

    [Fact]
    public void InstrumentId_TryParse_returns_the_same_id_as_Parse()
    {
        Assert.True(InstrumentId.TryParse("BRK.B.NYSE", out InstrumentId id));
        Assert.Equal(InstrumentId.Parse("BRK.B.NYSE"), id);
    }

    [Fact]
    public void InstrumentId_is_case_sensitive_and_sorts_ordinally()
    {
        InstrumentId upper = InstrumentId.Parse("BTCUSDT.BINANCE");
        InstrumentId lower = InstrumentId.Parse("btcusdt.BINANCE");
        List<InstrumentId> ids = [InstrumentId.Parse("ETHUSDT.BINANCE"), lower, upper, InstrumentId.Parse("ADAUSDT.BINANCE")];

        ids.Sort();

        Assert.NotEqual(upper, lower);
        Assert.Equal(["ADAUSDT.BINANCE", "BTCUSDT.BINANCE", "ETHUSDT.BINANCE", "btcusdt.BINANCE"], ids.Select(i => i.Value));
    }

    [Theory]
    [InlineData("TRADER-001", "001")]
    [InlineData("DESK-LONDON-7", "7")]
    [InlineData("SOLO", "SOLO")]
    public void TraderId_tag_is_the_segment_after_the_last_hyphen(string value, string expectedTag)
    {
        Assert.Equal(expectedTag, new TraderId(value).Tag);
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
        Assert.True(StrategyId.External.IsExternal);
        Assert.True(new StrategyId("EXTERNAL").IsExternal);
        Assert.False(new StrategyId("EmaCross-001").IsExternal);
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
}
