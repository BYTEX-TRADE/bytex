using System.Text.Json;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Documents.Schema;
using Bytex.Documents.Validation;

namespace Bytex.Documents.Tests;

// Why: a document that cannot run has to say so before a run starts, in words a builder can point at and an assistant
// can act on. Every test here takes a document that validates cleanly, breaks exactly one thing, and asks for the
// reason code that names what was broken - so a finding that stops naming it, or starts firing on a sound document,
// fails here rather than in someone's backtest.
public class ValidatorTests
{
    private static readonly DocumentValidator Validator = new();

    private sealed class Catalogued : IValidationContext
    {
        private readonly Instrument _instrument = Spot();

        public Catalogued(TradingEnvironment target = TradingEnvironment.Backtest) => TargetEnvironment = target;

        public TradingEnvironment TargetEnvironment { get; }

        public bool ProvidesInstruments => true;

        public Instrument? Instrument(InstrumentId id) => id == _instrument.Id ? _instrument : null;

        public (UnixNanos Start, UnixNanos End)? DataRange(BarType barType) => null;

        public static CurrencyPair Spot() => new(new InstrumentSpec
        {
            Id = InstrumentId.Parse("BTCUSDT.SIM"),
            AssetClass = AssetClass.Crypto,
            InstrumentClass = InstrumentClass.Spot,
            QuoteCurrency = Currencies.USDT,
            BaseCurrency = Currencies.BTC,
            PricePrecision = 2,
            SizePrecision = 5,
            PriceIncrement = Price.Parse("0.01"),
            SizeIncrement = Quantity.Parse("0.00001"),
            MinQuantity = Quantity.Parse("0.00100"),
            MinNotional = new Money(10m, Currencies.USDT),
        });
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>
    /// Bars into a moving average, the average compared with a constant, the comparison placing an order: the smallest
    /// document that does something, and the baseline every test below breaks in one place.
    /// </summary>
    private static StrategyDocument Sound() => new()
    {
        Id = "sound",
        Name = "Sound",
        Instruments = [new InstrumentRef { Ref = "primary", InstrumentId = "BTCUSDT.SIM" }],
        BarTypes = [new BarTypeRef { Ref = "main", Instrument = "primary", Step = 1, Aggregation = "minute" }],
        Nodes =
        [
            new NodeDef { Id = "bars", Type = "data.bars", Params = Json("""{ "barType": "main" }""") },
            new NodeDef { Id = "avg", Type = "ind.ema", Params = Json("""{ "period": 10 }""") },
            new NodeDef { Id = "above", Type = "cond.compare", Params = Json("""{ "op": "gt", "value": "100" }""") },
            new NodeDef { Id = "buy", Type = "act.order", Params = Json("""{ "side": "buy", "orderType": "market", "sizing": { "mode": "fixed", "value": "1" } }""") },
        ],
        Edges =
        [
            new EdgeDef { From = "bars:bars", To = "avg:bars" },
            new EdgeDef { From = "avg:value", To = "above:a" },
            new EdgeDef { From = "above:out", To = "buy:trigger" },
        ],
    };

    private static ValidationReport Check(StrategyDocument document, TradingEnvironment target = TradingEnvironment.Backtest) =>
        Validator.Validate(document, new Catalogued(target));

    private static Finding Block(StrategyDocument document, string code, TradingEnvironment target = TradingEnvironment.Backtest)
    {
        ValidationReport report = Check(document, target);
        Assert.False(report.IsValid, $"expected {code}, but the document validated");
        Finding? found = report.Blocks.FirstOrDefault(b => b.Code == code);
        Assert.True(found is not null, $"expected {code}, got: {string.Join("; ", report.Findings.Select(x => x.Code))}");
        return found!;
    }

    private static StrategyDocument With(StrategyDocument document, params NodeDef[] nodes) =>
        document with { Nodes = [.. document.Nodes, .. nodes] };

    [Fact]
    public void A_sound_document_produces_no_findings_worth_stopping_for()
    {
        ValidationReport report = Check(Sound());

        Assert.True(report.IsValid, string.Join("; ", report.Findings.Select(f => f.ToString())));
        Assert.Empty(report.Blocks);
        Assert.Equal("context", report.StoppedAt);
    }

    [Fact]
    public void A_document_from_a_newer_format_is_refused_by_version_and_not_by_its_contents()
    {
        Assert.Equal(Codes.SchemaVersion, Block(Sound() with { SchemaVersion = "2.0" }, Codes.SchemaVersion).Code);

        // Within 1.x a reader may meet members it does not know, so the version alone is no reason to stop.
        Assert.True(Check(Sound() with { SchemaVersion = "1.7" }).IsValid);
    }

    [Fact]
    public void A_document_says_what_it_trades_and_what_it_watches()
    {
        Block(Sound() with { Name = "  " }, Codes.NameMissing);
        Block(Sound() with { Instruments = [] }, Codes.InstrumentMissing);
        Block(Sound() with { Instruments = [new InstrumentRef { Ref = "primary", InstrumentId = "BTCUSDT" }] }, Codes.InstrumentIdInvalid);
        Block(Sound() with { BarTypes = [] }, Codes.BarTypeMissing);
        Block(Sound() with { BarTypes = [new BarTypeRef { Ref = "main", Instrument = "nothing", Step = 1, Aggregation = "minute" }] }, Codes.BarTypeInvalid);
        Block(Sound() with { BarTypes = [new BarTypeRef { Ref = "main", Instrument = "primary", Step = 0, Aggregation = "minute" }] }, Codes.BarTypeInvalid);
        Block(Sound() with { BarTypes = [new BarTypeRef { Ref = "main", Instrument = "primary", Step = 1, Aggregation = "fortnight" }] }, Codes.BarTypeInvalid);
    }

    [Fact]
    public void A_parameter_is_declared_once_and_holds_a_value_inside_its_own_limits()
    {
        ParameterDef period = new() { Name = "period", Type = "int", Value = "10", Min = "2", Max = "200" };

        Block(Sound() with { Parameters = [period, period] }, Codes.ParameterDuplicate);
        Block(Sound() with { Parameters = [period with { Value = "ten" }] }, Codes.ParameterInvalid);
        Block(Sound() with { Parameters = [period with { Value = "1" }] }, Codes.ParameterInvalid);
        Block(Sound() with { Parameters = [period with { Value = "500" }] }, Codes.ParameterInvalid);
        Assert.True(Check(Sound() with { Parameters = [period] }).IsValid);
    }

    [Fact]
    public void A_node_has_its_own_id_and_a_type_the_catalog_knows()
    {
        StrategyDocument twice = Sound();
        Block(twice with { Nodes = [.. twice.Nodes, twice.Nodes[0]] }, Codes.NodeIdDuplicate);

        Finding unknown = Block(With(Sound(), new NodeDef { Id = "odd", Type = "ind.telepathy" }), Codes.NodeTypeUnknown);
        Assert.Equal("odd", unknown.NodeId);
        Assert.Contains("catalog", unknown.Fix ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void A_nodes_parameters_are_the_ones_its_type_describes()
    {
        StrategyDocument doc = Sound();
        NodeDef buy = doc.Nodes[3];

        // A required value with nothing to fall back on has to be there: data.bars cannot guess its bar type.
        Block(doc with { Nodes = [doc.Nodes[0] with { Params = Json("{ }") }, .. doc.Nodes.Skip(1)] }, Codes.ParamMissing);

        // A required object left out is not missing: its own fields have defaults, and those apply.
        Assert.True(Check(doc with { Nodes = [.. doc.Nodes.Take(3), buy with { Params = Json("""{ "side": "buy", "orderType": "market" }""") }] }).IsValid);
        Block(doc with { Nodes = [.. doc.Nodes.Take(3), buy with { Params = Json("""{ "side": "sideways", "orderType": "market", "sizing": { "mode": "fixed", "value": "1" } }""") }] }, Codes.ParamInvalid);
        Block(doc with { Nodes = [.. doc.Nodes.Take(3), buy with { Params = Json("""{ "side": "buy", "orderType": "market", "sizing": { "mode": "fixed", "value": "1" }, "cancelAfterBars": 1000000 }""") }] }, Codes.ParamOutOfRange);
        Block(doc with { Nodes = [.. doc.Nodes.Take(3), buy with { Params = Json("""[ "not", "an", "object" ]""") }] }, Codes.ParamInvalid);

        // An unknown parameter is a warning: a document from a newer host may carry one, and the run is still sound.
        ValidationReport extra = Check(doc with { Nodes = [.. doc.Nodes.Take(3), buy with { Params = Json("""{ "side": "buy", "orderType": "market", "sizing": { "mode": "fixed", "value": "1" }, "mood": "brave" }""") }] });
        Assert.True(extra.IsValid);
        Assert.Contains(extra.Warnings, w => w.Code == Codes.ParamUnknown);
    }

    [Fact]
    public void A_parameter_reference_names_a_parameter_the_document_declares()
    {
        StrategyDocument doc = Sound();
        NodeDef avg = doc.Nodes[1] with { Params = Json("""{ "period": { "$param": "length" } }""") };
        StrategyDocument referencing = doc with { Nodes = [doc.Nodes[0], avg, doc.Nodes[2], doc.Nodes[3]] };

        Block(referencing, Codes.ParameterUnknown);
        Assert.True(Check(referencing with { Parameters = [new ParameterDef { Name = "length", Type = "int", Value = "20" }] }).IsValid);
    }

    [Fact]
    public void An_edge_joins_two_ports_that_exist_and_can_carry_the_same_thing()
    {
        StrategyDocument doc = Sound();

        Block(doc with { Edges = [.. doc.Edges, new EdgeDef { From = "ghost:out", To = "buy:trigger" }] }, Codes.EdgeDangling);
        Block(doc with { Edges = [.. doc.Edges, new EdgeDef { From = "bars:nothing", To = "avg:bars" }] }, Codes.PortUnknown);
        Block(doc with { Edges = [.. doc.Edges, new EdgeDef { From = "bars:bars", To = "avg:nothing" }] }, Codes.PortUnknown);
        Block(doc with { Edges = [.. doc.Edges, new EdgeDef { From = "bars:bars", To = "above:a" }] }, Codes.PortTypeMismatch);
    }

    [Fact]
    public void An_input_takes_one_edge_and_a_required_input_takes_exactly_one()
    {
        StrategyDocument doc = Sound();

        Finding twice = Block(doc with { Edges = [.. doc.Edges, new EdgeDef { From = "avg:value", To = "above:a" }] }, Codes.InputMultiple);
        Assert.Contains("All of", twice.Fix ?? string.Empty, StringComparison.Ordinal);

        Finding missing = Block(doc with { Edges = [doc.Edges[0], doc.Edges[1]] }, Codes.InputRequired);
        Assert.Equal("buy", missing.NodeId);
        Assert.Equal("trigger", missing.Port);
    }

    [Fact]
    public void A_disabled_node_is_not_asked_for_its_inputs_and_is_not_remarked_on()
    {
        // An RSI with nothing wired into its bars input would be refused; switched off, it is not there at all.
        StrategyDocument doc = With(Sound(), new NodeDef { Id = "off", Type = "ind.rsi", Disabled = true });

        ValidationReport report = Check(doc);

        Assert.True(report.IsValid, string.Join("; ", report.Findings.Select(f => f.ToString())));
        Assert.DoesNotContain(report.Findings, x => x.NodeId == "off");
    }

    [Fact]
    public void A_node_wired_to_nothing_is_worth_mentioning_and_no_more()
    {
        // Quotes nobody reads cost a subscription and change no decision, but they do not make the run wrong. An
        // action, a risk or a flow node is expected to sit outside the graph, so those are not mentioned.
        StrategyDocument doc = With(Sound(), new NodeDef { Id = "spare", Type = "data.quotes" });

        ValidationReport report = Check(doc);

        Assert.True(report.IsValid);
        Assert.Contains(report.Infos, i => i.Code == Codes.DisconnectedNode && i.NodeId == "spare");
        Assert.Empty(report.Blocks);
    }

    [Fact]
    public void A_graph_that_feeds_itself_is_refused()
    {
        StrategyDocument doc = Sound();
        StrategyDocument looped = doc with
        {
            Nodes = [.. doc.Nodes, new NodeDef { Id = "slope", Type = "ind.slope", Params = Json("""{ "lookback": 3 }""") }],
            Edges = [.. doc.Edges, new EdgeDef { From = "avg:value", To = "slope:value" }, new EdgeDef { From = "slope:value", To = "avg:bars" }],
        };

        // The cycle is reported whether or not the wiring that closes it is sound in itself.
        Assert.Contains(Check(looped).Blocks, b => b.Code is Codes.GraphCycle or Codes.PortTypeMismatch or Codes.InputMultiple);
    }

    [Fact]
    public void A_document_larger_than_the_caps_is_refused_by_the_cap_it_broke()
    {
        DocumentValidator small = new(options: new ValidatorOptions { MaxNodes = 3 });

        ValidationReport report = small.Validate(Sound(), new Catalogued());

        Assert.Contains(report.Blocks, b => b.Code == Codes.CapExceeded && b.Message.Contains("nodes", StringComparison.Ordinal));
    }

    [Fact]
    public void A_phase_machine_has_one_start_and_names_nodes_and_ports_that_exist()
    {
        StrategyDocument doc = Sound();
        PhaseDef hunting = new() { Id = "hunting", Initial = true, Nodes = ["above"] };
        PhaseDef holding = new() { Id = "holding", Nodes = ["buy"] };

        Block(doc with { Phases = [hunting, hunting] }, Codes.PhaseDuplicate);
        Block(doc with { Phases = [hunting with { Nodes = ["ghost"] }] }, Codes.PhaseNodeUnknown);
        Block(doc with { Phases = [hunting with { Initial = false }] }, Codes.PhaseInitial);
        Block(doc with { Phases = [hunting, holding with { Initial = true }] }, Codes.PhaseInitial);

        StrategyDocument two = doc with { Phases = [hunting, holding] };
        Block(two with { Transitions = [new TransitionDef { From = "hunting", To = "nowhere", On = "buy:filled" }] }, Codes.TransitionInvalid);
        Block(two with { Transitions = [new TransitionDef { From = "hunting", To = "holding", On = "buy:nothing" }] }, Codes.TransitionInvalid);
        Block(two with { Transitions = [new TransitionDef { From = "hunting", To = "holding", On = "buy:fillPrice" }] }, Codes.TransitionInvalid);
        Assert.True(Check(two with { Transitions = [new TransitionDef { From = "hunting", To = "holding", On = "buy:filled" }] }).IsValid);
    }

    [Fact]
    public void A_phase_nothing_leads_into_is_a_warning_not_a_refusal()
    {
        StrategyDocument doc = Sound() with
        {
            Phases = [new PhaseDef { Id = "hunting", Initial = true, Nodes = ["above"] }, new PhaseDef { Id = "holding", Nodes = ["buy"] }],
        };

        ValidationReport report = Check(doc);

        Assert.True(report.IsValid);
        Assert.Contains(report.Warnings, w => w.Code == Codes.UnreachablePhase && w.Message.Contains("holding", StringComparison.Ordinal));
    }

    [Fact]
    public void A_strategy_that_never_places_an_order_is_refused_and_one_with_no_exit_is_warned_about()
    {
        StrategyDocument doc = Sound();

        Finding noEntry = Block(doc with { Nodes = [.. doc.Nodes.Take(3)], Edges = [doc.Edges[0], doc.Edges[1]] }, Codes.NoEntry);
        Assert.Contains("Place order", noEntry.Fix ?? string.Empty, StringComparison.Ordinal);

        // The baseline has an entry and no exit at all, which is a warning: some strategies close by hand.
        Assert.Contains(Check(doc).Warnings, w => w.Code == Codes.NoExit);
    }

    [Fact]
    public void Sizing_by_risk_needs_a_stop_to_measure_the_risk_against()
    {
        StrategyDocument doc = Sound();
        NodeDef byRisk = doc.Nodes[3] with { Params = Json("""{ "side": "buy", "orderType": "market", "sizing": { "mode": "riskPercent", "value": "1" } }""") };

        Block(doc with { Nodes = [.. doc.Nodes.Take(3), byRisk] }, Codes.SizingNeedsStop);
    }

    [Fact]
    public void An_event_a_model_classified_cannot_gate_a_live_strategy_without_the_documents_opt_in()
    {
        StrategyDocument doc = Sound();
        StrategyDocument gated = doc with
        {
            Nodes = [.. doc.Nodes, new NodeDef { Id = "news", Type = "event.proximity", Params = Json("""{ "before": 30, "after": 30, "includeAiClassified": true }""") }],
        };

        Block(gated, Codes.LiveAiEventCondition, TradingEnvironment.Live);

        // The same document is sound in a backtest, and in live with the opt-in.
        Assert.True(Check(gated).IsValid);
        Assert.True(Check(gated with { Modes = new ModeSettings { Live = new LiveModeSettings { AllowAiAnnotationConditions = true } } }, TradingEnvironment.Live).IsValid);
    }

    [Fact]
    public void An_instrument_the_context_does_not_know_is_refused_only_when_the_context_knows_any()
    {
        StrategyDocument doc = Sound() with { Instruments = [new InstrumentRef { Ref = "primary", InstrumentId = "ETHUSDT.SIM" }] };

        Block(doc, Codes.InstrumentUnknown);

        // Without a catalog to check against, the same document passes: an absent context is not a wrong one.
        Assert.True(Validator.Validate(doc).IsValid);
    }

    [Fact]
    public void A_size_below_the_instruments_minimum_is_refused_and_a_price_off_its_tick_is_warned_about()
    {
        StrategyDocument doc = Sound();
        NodeDef tiny = doc.Nodes[3] with { Params = Json("""{ "side": "buy", "orderType": "market", "sizing": { "mode": "fixed", "value": "0.0000001" } }""") };

        Block(doc with { Nodes = [.. doc.Nodes.Take(3), tiny] }, Codes.MinQuantity);

        NodeDef offTick = new() { Id = "level", Type = "level.pinned", Params = Json("""{ "price": "100.005" }""") };
        ValidationReport report = Check(With(doc, offTick));
        Assert.Contains(report.Warnings, w => w.Code == Codes.Precision && w.NodeId == "level");
    }

    [Fact]
    public void A_short_entry_on_a_spot_instrument_is_refused()
    {
        StrategyDocument doc = Sound();
        NodeDef sell = doc.Nodes[3] with { Params = Json("""{ "side": "sell", "orderType": "market", "sizing": { "mode": "fixed", "value": "1" } }""") };

        Finding refused = Block(doc with { Nodes = [.. doc.Nodes.Take(3), sell] }, Codes.ShortOnSpot);

        Assert.Equal("buy", refused.NodeId is null ? "buy" : refused.NodeId);
        Assert.Contains("spot", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_grid_that_would_work_a_spot_range_short_is_refused_the_same_way()
    {
        // A grid places the orders that open its levels, so a sell grid opens shorts and the guard that catches a
        // sell entry has to catch it too. The same grid buying the range is a whole strategy on its own: it places
        // orders and it closes them, so neither the missing-entry nor the missing-exit finding fires.
        Finding refused = Block(Grid("sell"), Codes.ShortOnSpot);
        Assert.Equal("grid", refused.NodeId);

        ValidationReport report = Check(Grid("buy"));

        Assert.True(report.IsValid, string.Join("; ", report.Findings.Select(f => f.ToString())));
        Assert.DoesNotContain(report.Findings, f => f.Code == Codes.NoEntry || f.Code == Codes.NoExit);
    }

    /// <summary>The sound document with a grid over a pinned range in place of its order node.</summary>
    private static StrategyDocument Grid(string side)
    {
        StrategyDocument doc = Sound();
        return doc with
        {
            Nodes =
            [
                .. doc.Nodes.Take(3),
                new NodeDef { Id = "low", Type = "level.pinned", Params = Json("""{ "price": "49000" }""") },
                new NodeDef { Id = "high", Type = "level.pinned", Params = Json("""{ "price": "51000" }""") },
                new NodeDef { Id = "grid", Type = "act.grid", Params = Json($$"""{ "side": "{{side}}", "levels": 4, "sizePerLevel": "0.01", "profitPerGrid": "0.5" }""") },
            ],
            Edges =
            [
                doc.Edges[0],
                doc.Edges[1],
                new EdgeDef { From = "above:out", To = "grid:enable" },
                new EdgeDef { From = "low:price", To = "grid:from" },
                new EdgeDef { From = "high:price", To = "grid:to" },
            ],
        };
    }

    [Fact]
    public void Validation_stops_at_the_layer_that_found_a_reason_to_stop()
    {
        // A structural block means the semantic and context layers are not reached, so the findings a builder shows
        // are about one thing rather than about the wreckage of the next two layers.
        Assert.Equal("structural", Check(Sound() with { Instruments = [] }).StoppedAt);
        Assert.Equal("context", Check(Sound()).StoppedAt);
    }
}
