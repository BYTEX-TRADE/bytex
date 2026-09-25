using System.Text.Json;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Documents.Schema;
using Bytex.Documents.Validation;
using Xunit;

namespace Bytex.Documents.Tests;

// Why: a short strategy was built on a spot pair and backtested with a full report, although no spot venue would take a
// single one of its entries. The validator has the instrument in hand and refuses the document before anything runs.
public sealed class ShortOnSpotTests
{
    private static readonly DocumentValidator Validator = new();

    private sealed class OneInstrument : IValidationContext
    {
        private readonly Instrument _instrument;

        public OneInstrument(Instrument instrument)
        {
            _instrument = instrument;
        }

        public TradingEnvironment TargetEnvironment => TradingEnvironment.Backtest;

        public bool ProvidesInstruments => true;

        public Instrument? Instrument(InstrumentId id) => id == _instrument.Id ? _instrument : null;

        public (UnixNanos Start, UnixNanos End)? DataRange(BarType barType) => null;
    }

    private static CryptoPerpetual Perp() => new(new InstrumentSpec
    {
        Id = Fixtures.BtcUsdt().Id,
        AssetClass = AssetClass.Crypto,
        InstrumentClass = InstrumentClass.Swap,
        QuoteCurrency = Currencies.USDT,
        BaseCurrency = Currencies.BTC,
        SettlementCurrency = Currencies.USDT,
        PricePrecision = 2,
        SizePrecision = 6,
        PriceIncrement = new Price(0.01m, 2),
        SizeIncrement = new Quantity(0.000001m, 6),
        MinQuantity = new Quantity(0.00001m, 6),
    });

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static StrategyDocument WithEntry(string json)
    {
        StrategyDocument doc = Fixtures.Example("ema-cross");
        NodeDef buy = doc.Nodes.Single(n => n.Id == "buy");
        return doc with { Nodes = doc.Nodes.Select(n => n.Id == "buy" ? buy with { Params = Json(json) } : n).ToList() };
    }

    private const string Sell = """{ "side": "sell", "orderType": "market", "sizing": { "mode": "fixed", "value": 0.01 } }""";

    [Fact]
    public void A_sell_entry_on_a_spot_instrument_blocks_and_names_the_node_and_the_instrument()
    {
        ValidationReport report = Validator.Validate(WithEntry(Sell), new OneInstrument(Fixtures.BtcUsdt()));

        Assert.False(report.IsValid);
        Finding finding = Assert.Single(report.Blocks, f => f.Code == Codes.ShortOnSpot);
        Assert.Equal("buy", finding.NodeId);
        Assert.Contains("'buy'", finding.Message, StringComparison.Ordinal);
        Assert.Contains(Fixtures.BtcUsdt().Id.ToString(), finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_sell_entry_on_a_perpetual_is_fine()
    {
        ValidationReport report = Validator.Validate(WithEntry(Sell), new OneInstrument(Perp()));

        Assert.DoesNotContain(report.Findings, f => f.Code == Codes.ShortOnSpot);
        Assert.True(report.IsValid, string.Join("\n", report.Findings));
    }

    [Fact]
    public void A_buy_entry_on_a_spot_instrument_is_fine()
    {
        ValidationReport report = Validator.Validate(Fixtures.Example("ema-cross"), new OneInstrument(Fixtures.BtcUsdt()));

        Assert.DoesNotContain(report.Findings, f => f.Code == Codes.ShortOnSpot);
    }

    [Fact]
    public void A_disabled_sell_entry_is_not_reported()
    {
        StrategyDocument doc = Fixtures.Example("ema-cross");
        NodeDef shorted = new() { Id = "short", Type = "act.order", Params = Json(Sell), Disabled = true };
        ValidationReport report = Validator.Validate(doc with { Nodes = doc.Nodes.Append(shorted).ToList() }, new OneInstrument(Fixtures.BtcUsdt()));

        Assert.DoesNotContain(report.Findings, f => f.Code == Codes.ShortOnSpot);
    }

    // Selling what a buy entry bought is how a spot strategy gets out; only a sell that can do nothing but open is refused.
    [Fact]
    public void A_sell_order_that_may_fire_with_a_position_open_is_an_exit_beside_a_buy_entry()
    {
        StrategyDocument doc = Fixtures.Example("ema-cross");
        NodeDef sellOut = new() { Id = "sellOut", Type = "act.order", Params = Json("""{ "side": "sell", "orderType": "market", "onlyWhenFlat": false, "sizing": { "mode": "fixed", "value": 0.01 } }""") };
        StrategyDocument withExit = doc with
        {
            Nodes = doc.Nodes.Append(sellOut).ToList(),
            Edges = doc.Edges.Append(new EdgeDef { From = "crossDown:out", To = "sellOut:trigger" }).ToList(),
        };

        ValidationReport report = Validator.Validate(withExit, new OneInstrument(Fixtures.BtcUsdt()));

        Assert.DoesNotContain(report.Findings, f => f.Code == Codes.ShortOnSpot);
    }

    [Fact]
    public void The_same_sell_order_blocks_when_nothing_in_the_document_ever_buys()
    {
        ValidationReport report = Validator.Validate(
            WithEntry("""{ "side": "sell", "orderType": "market", "onlyWhenFlat": false, "sizing": { "mode": "fixed", "value": 0.01 } }"""),
            new OneInstrument(Fixtures.BtcUsdt()));

        Assert.Single(report.Blocks, f => f.Code == Codes.ShortOnSpot);
    }

    [Theory]
    [InlineData("act.bracket", "stop", "target", """{ "side": "sell", "sizing": { "mode": "fixed", "value": 0.01 } }""")]
    [InlineData("act.ladder", "from", "to", """{ "side": "sell", "levels": 3, "sizing": { "mode": "fixed", "value": 0.01 } }""")]
    public void A_sell_bracket_and_a_sell_ladder_block_on_spot(string type, string upperPort, string lowerPort, string json)
    {
        StrategyDocument doc = Fixtures.Example("ema-cross");
        List<NodeDef> added =
        [
            new() { Id = "short", Type = type, Params = Json(json) },
            new() { Id = "upper", Type = "level.pinned", Params = Json("""{ "price": 51000 }""") },
            new() { Id = "lower", Type = "level.pinned", Params = Json("""{ "price": 49000 }""") },
        ];
        List<EdgeDef> wires =
        [
            new() { From = "crossDown:out", To = "short:trigger" },
            new() { From = "upper:price", To = "short:" + upperPort },
            new() { From = "lower:price", To = "short:" + lowerPort },
        ];
        StrategyDocument shorted = doc with
        {
            Nodes = doc.Nodes.Where(n => n.Id != "buy").Concat(added).ToList(),
            Edges = doc.Edges.Where(e => !e.To.StartsWith("buy:", StringComparison.Ordinal)).Concat(wires).ToList(),
        };

        Finding finding = Assert.Single(Validator.Validate(shorted, new OneInstrument(Fixtures.BtcUsdt())).Blocks, f => f.Code == Codes.ShortOnSpot);
        Assert.Equal("short", finding.NodeId);
        Assert.DoesNotContain(Validator.Validate(shorted, new OneInstrument(Perp())).Findings, f => f.Code == Codes.ShortOnSpot);
    }

    // A document parameter of type enum lets one switch flip every node that reads it, so the rule and the runtime both
    // have to follow the reference rather than read "buy" from the default.
    private static StrategyDocument WithSideParameter(string value) => WithEntry("""{ "side": { "$param": "dir" }, "orderType": "market", "sizing": { "mode": "fixed", "value": 0.01 } }""")
        with { Parameters = [.. Fixtures.Example("ema-cross").Parameters, new ParameterDef { Name = "dir", Type = "enum", Value = value, Choices = ["buy", "sell"] }] };

    [Fact]
    public void A_side_taken_from_an_enum_parameter_is_followed_by_the_rule()
    {
        Finding finding = Assert.Single(Validator.Validate(WithSideParameter("sell"), new OneInstrument(Fixtures.BtcUsdt())).Blocks, f => f.Code == Codes.ShortOnSpot);
        Assert.Equal("buy", finding.NodeId);

        ValidationReport buying = Validator.Validate(WithSideParameter("buy"), new OneInstrument(Fixtures.BtcUsdt()));
        Assert.DoesNotContain(buying.Findings, f => f.Code == Codes.ShortOnSpot);
        Assert.True(buying.IsValid, string.Join(Environment.NewLine, buying.Findings));
        Assert.DoesNotContain(Validator.Validate(WithSideParameter("sell"), new OneInstrument(Perp())).Findings, f => f.Code == Codes.ShortOnSpot);
    }

    [Fact]
    public void The_runtime_reads_the_side_from_the_parameter_and_trades_the_same_as_a_literal()
    {
        Fixtures.RunBacktest(WithSideParameter("buy"));
        string fromParameter = Fixtures.Fingerprint(Fixtures.RunBacktest(WithSideParameter("buy")).Result);
        string fromLiteral = Fixtures.Fingerprint(Fixtures.RunBacktest(WithEntry("""{ "side": "buy", "orderType": "market", "sizing": { "mode": "fixed", "value": 0.01 } }""")).Result);

        Assert.Equal(fromLiteral, fromParameter);
    }

    [Fact]
    public void A_side_reference_the_document_cannot_honour_is_a_parameter_error_and_not_a_silent_buy()
    {
        StrategyDocument missing = WithEntry("""{ "side": { "$param": "dir" }, "orderType": "market", "sizing": { "mode": "fixed", "value": 0.01 } }""");
        Assert.Contains(Validator.Validate(missing).Blocks, f => f.Code == Codes.ParameterUnknown);

        StrategyDocument wrongType = missing with { Parameters = [.. missing.Parameters, new ParameterDef { Name = "dir", Type = "decimal", Value = "1" }] };
        Assert.Contains(Validator.Validate(wrongType).Blocks, f => f.Code == Codes.ParamInvalid && f.Message.Contains("type enum", StringComparison.Ordinal));

        StrategyDocument notAChoice = missing with { Parameters = [.. missing.Parameters, new ParameterDef { Name = "dir", Type = "enum", Value = "short", Choices = ["buy", "sell", "short"] }] };
        Assert.Contains(Validator.Validate(notAChoice).Blocks, f => f.Code == Codes.ParamInvalid && f.Message.Contains("'short', which is not one of", StringComparison.Ordinal));
    }
}
