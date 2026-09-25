using Bytex.Backtest;
using Bytex.Core.Model;
using Bytex.Documents.Runtime;
using Bytex.Documents.Schema;
using Bytex.Documents.Validation;

namespace Bytex.Documents.Tests;

// Why: a real twelve-month run traded once. The target sat below a long entry, filled on the entry bar at the entry price,
// and the "position closed" pulse arrived while the document was still in its entry phase; the phase then moved to "in
// position" and never came back. Two defects, pinned apart: the phase walk, and exit levels on the wrong side of the entry.
public sealed class PhaseAndExitLevelTests
{
    // The entry's "filled" pulse triggers a market close in the same frame, so the pulse that enters the next phase and the
    // pulse that leaves it share one frame, with no help from risk.exit.
    private const string OpensAndClosesInOneBar = """
        {
          "schemaVersion": "1.0",
          "id": "one-bar-round-trip",
          "name": "Opens and closes inside one bar",
          "instruments": [ { "ref": "primary", "instrumentId": "BTCUSDT.SIM" } ],
          "barTypes": [ { "ref": "main", "instrument": "primary", "step": 1, "aggregation": "minute", "priceType": "last", "source": "external" } ],
          "parameters": [],
          "nodes": [
            { "id": "bars", "type": "data.bars", "params": { "barType": "main" } },
            { "id": "fast", "type": "ind.ema", "params": { "period": 10 } },
            { "id": "slow", "type": "ind.ema", "params": { "period": 30 } },
            { "id": "crossUp", "type": "cond.cross", "params": { "direction": "above" } },
            { "id": "buy", "type": "act.order", "params": { "side": "buy", "orderType": "market", "sizing": { "mode": "fixed", "value": 0.05 }, "onlyWhenFlat": true } },
            { "id": "closer", "type": "act.close" }
          ],
          "edges": [
            { "from": "bars:bars", "to": "fast:bars" },
            { "from": "bars:bars", "to": "slow:bars" },
            { "from": "fast:value", "to": "crossUp:value" },
            { "from": "slow:value", "to": "crossUp:reference" },
            { "from": "crossUp:out", "to": "buy:trigger" },
            { "from": "buy:filled", "to": "closer:trigger" }
          ],
          "phases": [
            { "id": "entry", "name": "Entry", "nodes": [ "crossUp" ], "initial": true },
            { "id": "manage", "name": "In position", "nodes": [] }
          ],
          "transitions": [
            { "from": "entry", "to": "manage", "on": "buy:filled" },
            { "from": "manage", "to": "entry", "on": "closer:done" }
          ],
          "repeat": { "enabled": true }
        }
        """;

    [Fact]
    public void A_position_that_opens_and_closes_inside_one_bar_does_not_leave_the_document_stuck_in_its_next_phase()
    {
        StrategyDocument doc = DocumentJson.Deserialize(OpensAndClosesInOneBar);
        Assert.True(new DocumentValidator().Validate(doc).IsValid);

        (BacktestResult result, DocumentStrategy strategy) = Fixtures.RunBacktest(doc, bars: 6000);

        // The premise: round trips that start and end on one bar.
        Assert.Contains(result.Positions, p => p.TsClosed == p.TsOpened);
        Assert.Equal("entry", strategy.CurrentPhase);
        Assert.True(result.Positions.Count > 3, $"expected a trade on every cross, got {result.Positions.Count} positions");

        // Both transitions are in the log, in order, on the bar of the round trip.
        List<StrategyEvent> transitions = strategy.Decisions.Where(d => d.Kind == "transition").ToList();
        Assert.Equal("phase entry → manage", transitions[0].Message);
        Assert.Equal("phase manage → entry", transitions[1].Message);
        Assert.Equal(transitions[0].TsEvent, transitions[1].TsEvent);
        Assert.Equal(0, transitions.Count % 2);
    }

    private static string ExitDocument(string side, string stop, string target, string stopLevel, string targetLevel) => $$"""
        {
          "schemaVersion": "1.0",
          "id": "exit-levels",
          "name": "Exit levels",
          "instruments": [ { "ref": "primary", "instrumentId": "BTCUSDT.SIM" } ],
          "barTypes": [ { "ref": "main", "instrument": "primary", "step": 1, "aggregation": "minute", "priceType": "last", "source": "external" } ],
          "parameters": [],
          "nodes": [
            { "id": "bars", "type": "data.bars", "params": { "barType": "main" } },
            { "id": "fast", "type": "ind.ema", "params": { "period": 10 } },
            { "id": "slow", "type": "ind.ema", "params": { "period": 30 } },
            { "id": "atr", "type": "ind.atr", "params": { "period": 14 } },
            { "id": "cross", "type": "cond.cross", "params": { "direction": "{{(side == "buy" ? "above" : "below")}}" } },
            { "id": "stopAt", "type": "level.pinned", "params": { "price": {{stopLevel}} } },
            { "id": "targetAt", "type": "level.pinned", "params": { "price": {{targetLevel}} } },
            { "id": "enter", "type": "act.order", "params": { "side": "{{side}}", "orderType": "market", "sizing": { "mode": "fixed", "value": 0.05 }, "onlyWhenFlat": true } },
            { "id": "protect", "type": "risk.exit", "params": { "stop": {{stop}}, "target": {{target}}, "trail": { "enabled": false } } }
          ],
          "edges": [
            { "from": "bars:bars", "to": "fast:bars" },
            { "from": "bars:bars", "to": "slow:bars" },
            { "from": "bars:bars", "to": "atr:bars" },
            { "from": "fast:value", "to": "cross:value" },
            { "from": "slow:value", "to": "cross:reference" },
            { "from": "cross:out", "to": "enter:trigger" },
            { "from": "enter:position", "to": "protect:position" },
            { "from": "atr:value", "to": "protect:atr" },
            { "from": "stopAt:price", "to": "protect:stopLevel" },
            { "from": "targetAt:price", "to": "protect:targetLevel" }
          ],
          "repeat": { "enabled": true }
        }
        """;

    private const string AtrStop = """{ "anchor": "entry", "offset": { "unit": "atr", "value": 2 } }""";
    private const string LevelStop = """{ "anchor": "level", "offset": { "unit": "atr", "value": 0 } }""";
    private const string LevelTarget = """{ "unit": "level", "value": 0 }""";
    private const string RTarget = """{ "unit": "r", "value": 2 }""";

    // Random-walk bars start at 50,000: 200 is below every entry, 10,000,000 above.
    [Theory]
    [InlineData("buy", "200", "above")]
    [InlineData("sell", "10000000", "below")]
    public void A_target_level_that_is_not_beyond_the_entry_is_not_placed_and_the_stop_stays(string side, string targetLevel, string word)
    {
        StrategyDocument doc = DocumentJson.Deserialize(ExitDocument(side, AtrStop, LevelTarget, "0", targetLevel));
        Assert.True(new DocumentValidator().Validate(doc).IsValid);

        (BacktestResult result, DocumentStrategy strategy) = Fixtures.RunBacktest(doc, bars: 6000);

        Assert.NotEmpty(result.Positions);
        Assert.Contains(strategy.Decisions, d => d.Kind == "exit" && d.Message.StartsWith("target ", StringComparison.Ordinal) && d.Message.Contains($"is not {word} the entry", StringComparison.Ordinal) && d.Message.EndsWith("no target placed, the stop stays", StringComparison.Ordinal));
        // No round trip on the entry bar at the entry price, and the announcement names a stop and no target.
        Assert.DoesNotContain(result.Positions, p => p.TsClosed == p.TsOpened);
        Assert.DoesNotContain(result.Positions, p => p.AvgPxClose == p.AvgPxOpen);
        Assert.All(strategy.Decisions.Where(d => d.Kind == "exit" && d.Message.StartsWith("protecting", StringComparison.Ordinal)), d => Assert.DoesNotContain("target", d.Message, StringComparison.Ordinal));
        // The stop did its work: positions were closed later, by the stop.
        Assert.Contains(result.Positions, p => p.TsClosed is { } closed && closed > p.TsOpened);
    }

    [Theory]
    [InlineData("buy", "10000000", "below")]
    [InlineData("sell", "200", "above")]
    public void A_stop_level_that_is_not_behind_the_entry_is_moved_away_from_it_and_the_log_says_so(string side, string stopLevel, string word)
    {
        StrategyDocument doc = DocumentJson.Deserialize(ExitDocument(side, LevelStop, RTarget, stopLevel, "0"));
        Assert.True(new DocumentValidator().Validate(doc).IsValid);

        (BacktestResult result, DocumentStrategy strategy) = Fixtures.RunBacktest(doc, bars: 6000);

        Assert.NotEmpty(result.Positions);
        Assert.Contains(strategy.Decisions, d => d.Kind == "exit" && d.Message.StartsWith("stop ", StringComparison.Ordinal) && d.Message.Contains($"is not {word} the entry", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Positions, p => p.TsClosed == p.TsOpened);
    }

    [Theory]
    [InlineData("buy")]
    [InlineData("sell")]
    public void Levels_on_the_right_side_are_placed_as_before(string side)
    {
        string stopLevel = side == "buy" ? "200" : "10000000";
        string targetLevel = side == "buy" ? "10000000" : "200";
        StrategyDocument doc = DocumentJson.Deserialize(ExitDocument(side, LevelStop, LevelTarget, stopLevel, targetLevel));

        (_, DocumentStrategy strategy) = Fixtures.RunBacktest(doc, bars: 3000);

        Assert.Contains(strategy.Decisions, d => d.Kind == "exit" && d.Message.StartsWith("protecting", StringComparison.Ordinal) && d.Message.Contains("target", StringComparison.Ordinal));
        Assert.DoesNotContain(strategy.Decisions, d => d.Kind == "exit" && d.Message.Contains("is not", StringComparison.Ordinal));
    }

    private static string BracketDocument(string side, string stopLevel, string targetLevel) => $$"""
        {
          "schemaVersion": "1.0",
          "id": "bracket-levels",
          "name": "Bracket levels",
          "instruments": [ { "ref": "primary", "instrumentId": "BTCUSDT.SIM" } ],
          "barTypes": [ { "ref": "main", "instrument": "primary", "step": 1, "aggregation": "minute", "priceType": "last", "source": "external" } ],
          "parameters": [],
          "nodes": [
            { "id": "bars", "type": "data.bars", "params": { "barType": "main" } },
            { "id": "fast", "type": "ind.ema", "params": { "period": 10 } },
            { "id": "slow", "type": "ind.ema", "params": { "period": 30 } },
            { "id": "cross", "type": "cond.cross", "params": { "direction": "{{(side == "buy" ? "above" : "below")}}" } },
            { "id": "stopAt", "type": "level.pinned", "params": { "price": {{stopLevel}} } },
            { "id": "targetAt", "type": "level.pinned", "params": { "price": {{targetLevel}} } },
            { "id": "enter", "type": "act.bracket", "params": { "side": "{{side}}", "sizing": { "mode": "fixed", "value": 0.05 }, "onlyWhenFlat": true } }
          ],
          "edges": [
            { "from": "bars:bars", "to": "fast:bars" },
            { "from": "bars:bars", "to": "slow:bars" },
            { "from": "fast:value", "to": "cross:value" },
            { "from": "slow:value", "to": "cross:reference" },
            { "from": "cross:out", "to": "enter:trigger" },
            { "from": "stopAt:price", "to": "enter:stop" },
            { "from": "targetAt:price", "to": "enter:target" }
          ],
          "repeat": { "enabled": true }
        }
        """;

    [Theory]
    [InlineData("buy", "100", "200", "target 200", "is not above the entry")]
    [InlineData("sell", "20000000", "10000000", "target 10000000", "is not below the entry")]
    [InlineData("buy", "10000000", "20000000", "stop 10000000", "is not below the entry")]
    [InlineData("sell", "100", "50", "stop 100", "is not above the entry")]
    public void A_bracket_with_a_level_on_the_wrong_side_of_the_entry_is_not_taken_and_the_log_says_why(string side, string stopLevel, string targetLevel, string level, string reason)
    {
        StrategyDocument doc = DocumentJson.Deserialize(BracketDocument(side, stopLevel, targetLevel));
        Assert.True(new DocumentValidator().Validate(doc).IsValid);

        (BacktestResult result, DocumentStrategy strategy) = Fixtures.RunBacktest(doc, bars: 3000);

        Assert.Contains(strategy.Decisions, d => d.Kind == "skip" && d.NodeId == "enter" && d.Message.StartsWith($"trigger ignored: {level}", StringComparison.Ordinal) && d.Message.Contains(reason, StringComparison.Ordinal));
        Assert.Empty(result.Positions);
        Assert.DoesNotContain(strategy.Decisions, d => d.Kind is "order" or "fill" or "rejected");
    }

    [Theory]
    [InlineData("buy", "200", "10000000")]
    [InlineData("sell", "10000000", "200")]
    public void A_bracket_with_its_levels_on_the_right_sides_is_taken_as_before(string side, string stopLevel, string targetLevel)
    {
        StrategyDocument doc = DocumentJson.Deserialize(BracketDocument(side, stopLevel, targetLevel));

        (BacktestResult result, DocumentStrategy strategy) = Fixtures.RunBacktest(doc, bars: 3000);

        Assert.Contains(strategy.Decisions, d => d.Kind == "order" && d.NodeId == "enter");
        Assert.NotEmpty(result.Positions);
        Assert.DoesNotContain(strategy.Decisions, d => d.Kind is "skip" or "rejected" && d.Message.Contains("entry", StringComparison.Ordinal));
    }
}
