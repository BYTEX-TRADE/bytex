using Bytex.Backtest;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Documents.Runtime;
using Bytex.Documents.Schema;
using Bytex.Documents.Validation;

namespace Bytex.Documents.Tests;

// Why: a paper node with a 20-bar range looked dead for five hours after Start, because a document strategy began with
// empty indicators. Warm-up replays recent closed bars first. What must hold: the document is ready on the first live bar,
// nothing is ever placed or logged from a warm-up bar, a signal that was already on before the start is not chased, short
// history counts up bar by bar, and a backtest is left exactly as it was. The frame snapshot is what a monitor shows as
// "what the strategy is waiting for", so it must say what the frame computed, for the phase the strategy is in.
public sealed class WarmupAndSnapshotTests
{
    // A 20-bar range, "close above its middle", held for two bars, then a market buy. Needs 21 bars.
    private const string RangeDocument = """
        {
          "schemaVersion": "1.0",
          "id": "warmup-range",
          "name": "Range with a hold",
          "instruments": [ { "ref": "primary", "instrumentId": "BTCUSDT.SIM" } ],
          "barTypes": [ { "ref": "main", "instrument": "primary", "step": 1, "aggregation": "minute", "priceType": "last", "source": "external" } ],
          "parameters": [ { "name": "holdBars", "type": "int", "label": "Hold", "value": "2", "min": "1", "max": "50", "step": "1" } ],
          "nodes": [
            { "id": "bars", "type": "data.bars", "params": { "barType": "main" } },
            { "id": "range", "type": "level.range", "params": { "lookback": 20 } },
            { "id": "above", "type": "cond.compare", "label": "Close above the middle", "params": { "op": "gt" } },
            { "id": "held", "type": "cond.holdFor", "params": { "bars": { "$param": "holdBars" } } },
            { "id": "off", "type": "cond.compare", "params": { "op": "lt", "value": 1 }, "disabled": true },
            { "id": "buy", "type": "act.order", "params": { "side": "buy", "orderType": "market", "sizing": { "mode": "fixed", "value": 0.05 }, "onlyWhenFlat": true } }
          ],
          "edges": [
            { "from": "bars:bars", "to": "range:bars" },
            { "from": "bars:close", "to": "above:a" },
            { "from": "range:mid", "to": "above:b" },
            { "from": "above:out", "to": "held:in" },
            { "from": "held:out", "to": "buy:trigger" }
          ],
          "repeat": { "enabled": true }
        }
        """;

    // An always-true condition once a 5-bar EMA exists: the trigger is on long before any start.
    private const string AlwaysOnDocument = """
        {
          "schemaVersion": "1.0",
          "id": "warmup-always-on",
          "name": "Always on",
          "instruments": [ { "ref": "primary", "instrumentId": "BTCUSDT.SIM" } ],
          "barTypes": [ { "ref": "main", "instrument": "primary", "step": 1, "aggregation": "minute", "priceType": "last", "source": "external" } ],
          "parameters": [],
          "nodes": [
            { "id": "bars", "type": "data.bars", "params": { "barType": "main" } },
            { "id": "ema", "type": "ind.ema", "params": { "period": 5 } },
            { "id": "positive", "type": "cond.compare", "params": { "op": "gt", "value": 0 } },
            { "id": "buy", "type": "act.order", "params": { "side": "buy", "orderType": "market", "sizing": { "mode": "fixed", "value": 0.05 }, "onlyWhenFlat": true } }
          ],
          "edges": [
            { "from": "bars:bars", "to": "ema:bars" },
            { "from": "ema:value", "to": "positive:a" },
            { "from": "positive:out", "to": "buy:trigger" }
          ],
          "repeat": { "enabled": true }
        }
        """;

    // Two phases, a condition in each and one outside both.
    private const string PhasedDocument = """
        {
          "schemaVersion": "1.0",
          "id": "snapshot-phases",
          "name": "Phased",
          "instruments": [ { "ref": "primary", "instrumentId": "BTCUSDT.SIM" } ],
          "barTypes": [ { "ref": "main", "instrument": "primary", "step": 1, "aggregation": "minute", "priceType": "last", "source": "external" } ],
          "parameters": [],
          "nodes": [
            { "id": "bars", "type": "data.bars", "params": { "barType": "main" } },
            { "id": "fast", "type": "ind.ema", "params": { "period": 10 } },
            { "id": "slow", "type": "ind.ema", "params": { "period": 30 } },
            { "id": "anywhere", "type": "cond.compare", "params": { "op": "gt", "value": 0 } },
            { "id": "crossUp", "type": "cond.cross", "params": { "direction": "above" } },
            { "id": "crossDown", "type": "cond.cross", "params": { "direction": "below" } },
            { "id": "buy", "type": "act.order", "params": { "side": "buy", "orderType": "market", "sizing": { "mode": "fixed", "value": 0.05 }, "onlyWhenFlat": true } },
            { "id": "closer", "type": "act.close" }
          ],
          "edges": [
            { "from": "bars:bars", "to": "fast:bars" },
            { "from": "bars:bars", "to": "slow:bars" },
            { "from": "bars:close", "to": "anywhere:a" },
            { "from": "fast:value", "to": "crossUp:value" },
            { "from": "slow:value", "to": "crossUp:reference" },
            { "from": "fast:value", "to": "crossDown:value" },
            { "from": "slow:value", "to": "crossDown:reference" },
            { "from": "crossUp:out", "to": "buy:trigger" },
            { "from": "crossDown:out", "to": "closer:trigger" }
          ],
          "phases": [
            { "id": "entry", "name": "Entry", "nodes": [ "crossUp" ], "initial": true },
            { "id": "manage", "name": "In position", "nodes": [ "crossDown" ] }
          ],
          "transitions": [
            { "from": "entry", "to": "manage", "on": "buy:filled" },
            { "from": "manage", "to": "entry", "on": "closer:done" }
          ],
          "repeat": { "enabled": true }
        }
        """;

    private static readonly Instrument _instrument = Fixtures.BtcUsdt();
    private static readonly BarType _barType = Fixtures.MinuteBars(_instrument);

    private static IReadOnlyList<Bar> Bars(int count) => Fixtures.RandomWalkBars(_instrument, _barType, count);

    private static (BacktestResult Result, DocumentStrategy Strategy) Run(string document, IEnumerable<Bar> data, Func<DocumentStrategyConfig, DocumentStrategyConfig>? configure = null, UnixNanos? start = null)
    {
        StrategyDocument doc = DocumentJson.Deserialize(document);
        Assert.True(new DocumentValidator().Validate(doc).IsValid);
        using BacktestEngine engine = new(new BacktestEngineConfig { RunId = "warmup" });
        engine.AddInstrument(_instrument);
        engine.AddVenue(new SimulatedVenueConfig { Venue = Fixtures.Sim, AccountType = AccountType.Cash, StartingBalances = [new Money(1_000_000m, Currencies.USDT), new Money(10m, Currencies.BTC)] });
        engine.AddData(data.Cast<IData>());
        DocumentStrategyConfig config = new() { Document = doc, StrategyId = new StrategyId("Doc-001") };
        DocumentStrategy strategy = new(configure?.Invoke(config) ?? config);
        engine.AddStrategy(strategy);
        engine.Run(start);
        return (engine.GetResult(), strategy);
    }

    private static ConditionSnapshot Condition(DocumentStrategy strategy, string nodeId) => Assert.Single(strategy.LastFrame!.Conditions, c => c.NodeId == nodeId);

    [Fact]
    public void A_backtest_does_not_warm_up_and_its_result_is_what_it_was()
    {
        IReadOnlyList<Bar> bars = Bars(1500);

        (BacktestResult plain, DocumentStrategy strategy) = Run(RangeDocument, bars);
        (BacktestResult again, _) = Run(RangeDocument, bars, c => c with { WarmupBars = 0 });

        Assert.DoesNotContain(strategy.Decisions, d => d.Kind == "warmup");
        WarmupState state = Assert.Single(strategy.Warmup);
        Assert.Equal((21, 0, 1500), (state.Needed, state.FromHistory, state.Received));
        Assert.NotEmpty(plain.Positions);
        Assert.Equal(Fixtures.Fingerprint(plain), Fixtures.Fingerprint(again));
    }

    [Fact]
    public void With_history_the_document_is_ready_on_the_first_live_bar_and_without_it_is_not()
    {
        IReadOnlyList<Bar> bars = Bars(101);
        Bar firstLive = bars[100];

        (_, DocumentStrategy cold) = Run(RangeDocument, [firstLive]);
        (BacktestResult result, DocumentStrategy warm) = Run(RangeDocument, [firstLive], c => c with { WarmupHistory = bars.Take(100).ToList() });

        // Cold: one bar seen, the range has nothing to say, so the condition has nothing to compare with and answers no.
        Assert.False(Condition(cold, "above").Output);
        Assert.DoesNotContain("b", Condition(cold, "above").Inputs.Keys);
        Assert.False(cold.IsWarmedUp);
        Assert.Equal((21, 1, 0), (cold.Warmup[0].Needed, cold.Warmup[0].Received, cold.Warmup[0].FromHistory));

        // Warm: the last 63 of the 100 supplied bars were replayed (three times the need), then the live bar.
        Assert.True(warm.IsWarmedUp);
        Assert.Equal((21, 64, 63), (warm.Warmup[0].Needed, warm.Warmup[0].Received, warm.Warmup[0].FromHistory));
        Assert.Equal(bars[37].TsEvent, warm.Warmup[0].First);
        Assert.Equal(firstLive.TsEvent, warm.Warmup[0].Last);
        Assert.Equal(64, warm.BarIndex);
        Assert.Contains("b", Condition(warm, "above").Inputs.Keys);
        StrategyEvent line = Assert.Single(warm.Decisions, d => d.Kind == "warmup");
        Assert.Equal("warmed up on 63 bars 2025-01-01 00:38 → 2025-01-01 01:40 (needs 21)", line.Message);
        Assert.Equal(("21", "63"), (line.Values["needed"], line.Values["received"]));

        // Independent check of "above": the range of the 20 bars before the live one, against the live close.
        decimal mid = (bars.Skip(80).Take(20).Max(b => b.High.Value) + bars.Skip(80).Take(20).Min(b => b.Low.Value)) / 2m;
        Assert.Equal(firstLive.Close.Value > mid, Condition(warm, "above").Output);
        Assert.DoesNotContain(result.Positions, p => p.TsOpened < firstLive.TsEvent);
    }

    [Fact]
    public void Nothing_is_placed_or_logged_from_a_warm_up_bar()
    {
        IReadOnlyList<Bar> bars = Bars(1500);
        // The premise: run live, these bars do produce orders.
        (BacktestResult live, _) = Run(RangeDocument, bars);
        Assert.NotEmpty(live.Positions);

        // The same bars as history (all of them requested), then one live bar that cannot complete a two-bar hold by itself.
        (BacktestResult result, DocumentStrategy strategy) = Run(RangeDocument, [bars[1499]], c => c with { WarmupBars = 400, WarmupHistory = bars.Take(1499).ToList() });

        Assert.Empty(result.Positions);
        Assert.Empty(result.Orders);
        Assert.All(strategy.Decisions, d => Assert.Contains(d.Kind, new[] { "lifecycle", "warmup" }));
        Assert.Equal(1000, strategy.Warmup[0].FromHistory);
    }

    [Fact]
    public void A_signal_that_was_already_on_before_the_start_is_not_chased_but_a_fresh_one_is_taken()
    {
        IReadOnlyList<Bar> bars = Bars(300);

        (BacktestResult cold, _) = Run(AlwaysOnDocument, bars.Skip(100));
        (BacktestResult warm, DocumentStrategy strategy) = Run(AlwaysOnDocument, bars.Skip(100), c => c with { WarmupHistory = bars.Take(100).ToList() });

        // Cold, the trigger rises when the EMA first has a value, and the strategy buys.
        Assert.Single(cold.Positions);
        // Warm, the trigger was on through all of the history and never rises again: no order on the first live bar or later.
        Assert.Empty(warm.Positions);
        Assert.True(Condition(strategy, "positive").Output);

        // A trigger that rises after the start is taken as usual: the range document trades in its live part.
        (BacktestResult fresh, _) = Run(RangeDocument, Bars(1500).Skip(100), c => c with { WarmupHistory = Bars(1500).Take(100).ToList() });
        Assert.NotEmpty(fresh.Positions);
    }

    [Fact]
    public void Short_history_is_reported_and_readiness_counts_up_with_the_live_bars()
    {
        IReadOnlyList<Bar> bars = Bars(60);

        (_, DocumentStrategy after5) = Run(RangeDocument, bars.Skip(12).Take(5), c => c with { WarmupHistory = bars.Take(12).ToList() });
        (_, DocumentStrategy after9) = Run(RangeDocument, bars.Skip(12).Take(9), c => c with { WarmupHistory = bars.Take(12).ToList() });

        Assert.Equal("warm-up incomplete: 12 of 21 bars (the data client returned 12)", Assert.Single(after5.Decisions, d => d.Kind == "warmup").Message);
        Assert.Equal((21, 17, 12, false), (after5.Warmup[0].Needed, after5.Warmup[0].Received, after5.Warmup[0].FromHistory, after5.Warmup[0].Done));
        Assert.False(after5.IsWarmedUp);
        Assert.Equal((21, 21, 12, true), (after9.Warmup[0].Needed, after9.Warmup[0].Received, after9.Warmup[0].FromHistory, after9.Warmup[0].Done));
        Assert.True(after9.IsWarmedUp);
    }

    [Fact]
    public void Bars_that_are_not_closed_yet_or_come_twice_are_left_out_of_the_warm_up()
    {
        IReadOnlyList<Bar> bars = Bars(60);
        Bar firstLive = bars[40];
        List<Bar> history = [.. bars.Take(40), bars[10], bars[39], .. bars.Skip(41).Take(5)];

        (_, DocumentStrategy strategy) = Run(RangeDocument, [firstLive], c => c with { WarmupHistory = history });

        Assert.Equal(40, strategy.Warmup[0].FromHistory);
        Assert.Equal(firstLive.TsEvent, strategy.Warmup[0].Last);
    }

    [Fact]
    public void Outside_a_backtest_the_history_comes_from_the_data_client()
    {
        IReadOnlyList<Bar> bars = Bars(400);
        UnixNanos start = bars[200].TsEvent;

        (_, DocumentStrategy strategy) = Run(RangeDocument, bars, c => c with { Environment = TradingEnvironment.Sandbox }, start);

        StrategyEvent line = Assert.Single(strategy.Decisions, d => d.Kind == "warmup");
        Assert.StartsWith("warmed up on 63 bars", line.Message, StringComparison.Ordinal);
        Assert.Equal(63, strategy.Warmup[0].FromHistory);
        Assert.True(strategy.Warmup[0].First < start);
        Assert.False(strategy.WarmupPending);
        // The request went out with the first live bar; that bar waited behind the history and was then evaluated live,
        // like every bar after it, and none of them was taken for history.
        Assert.Equal(200, strategy.Warmup[0].Received - strategy.Warmup[0].FromHistory);
        Assert.True(strategy.Warmup[0].First < start && strategy.Warmup[0].Last == bars[^1].TsEvent);

        (_, DocumentStrategy off) = Run(RangeDocument, bars, c => c with { Environment = TradingEnvironment.Sandbox, WarmupBars = 0 }, start);
        Assert.DoesNotContain(off.Decisions, d => d.Kind == "warmup");
        Assert.Equal(0, off.Warmup[0].FromHistory);
    }

    [Fact]
    public void The_snapshot_says_what_the_frame_computed()
    {
        IReadOnlyList<Bar> bars = Bars(200);
        Bar last = bars[^1];

        (_, DocumentStrategy strategy) = Run(RangeDocument, bars);

        FrameSnapshot frame = strategy.LastFrame!;
        Assert.Equal((199L, last.TsEvent, (string?)null), (frame.Index, frame.BarTs, frame.Phase));
        Assert.Equal(["above", "held"], frame.Conditions.Select(c => c.NodeId));

        decimal mid = (bars.Skip(179).Take(20).Max(b => b.High.Value) + bars.Skip(179).Take(20).Min(b => b.Low.Value)) / 2m;
        ConditionSnapshot above = Condition(strategy, "above");
        Assert.Equal(("cond.compare", "Close above the middle"), (above.Type, above.Label));
        Assert.Equal(last.Close.Value, decimal.Parse(above.Inputs["a"], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(mid, decimal.Parse(above.Inputs["b"], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("gt", above.Params["op"]);
        Assert.Equal(last.Close.Value > mid, above.Output);

        ConditionSnapshot held = Condition(strategy, "held");
        Assert.Equal(above.Output is true ? "true" : "false", held.Inputs["in"]);
        Assert.Equal("2", held.Params["bars"]);
        Assert.Null(held.Label);
    }

    [Fact]
    public void The_snapshot_holds_the_conditions_of_the_current_phase_and_the_ones_outside_every_phase()
    {
        IReadOnlyList<Bar> bars = Bars(3000);
        StrategyDocument doc = DocumentJson.Deserialize(PhasedDocument);
        Assert.True(new DocumentValidator().Validate(doc).IsValid);

        // Stop the data where the strategy is in each phase in turn.
        (_, DocumentStrategy whole) = Run(PhasedDocument, bars);
        List<StrategyEvent> transitions = whole.Decisions.Where(d => d.Kind == "transition").ToList();
        Assert.True(transitions.Count >= 2);
        int inManage = bars.ToList().FindIndex(b => b.TsEvent == transitions[0].TsEvent) + 2;
        int backInEntry = bars.ToList().FindIndex(b => b.TsEvent == transitions[1].TsEvent) + 2;

        (_, DocumentStrategy managing) = Run(PhasedDocument, bars.Take(inManage));
        (_, DocumentStrategy entering) = Run(PhasedDocument, bars.Take(backInEntry));

        Assert.Equal("manage", managing.LastFrame!.Phase);
        Assert.Equal(["anywhere", "crossDown"], managing.LastFrame.Conditions.Select(c => c.NodeId));
        Assert.Equal("entry", entering.LastFrame!.Phase);
        Assert.Equal(["anywhere", "crossUp"], entering.LastFrame.Conditions.Select(c => c.NodeId));
        Assert.True(Condition(entering, "anywhere").Output);
        Assert.Equal("0", Condition(entering, "anywhere").Params["value"]);
    }
}
