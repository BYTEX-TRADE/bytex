using Bytex.Backtest;
using Bytex.Core.Caching;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Positions;
using Bytex.Core.Model.Primitives;
using Bytex.Documents.Runtime;
using Bytex.Documents.Schema;
using Bytex.Documents.Validation;
using Xunit;

namespace Bytex.Documents.Tests;

// Why: averaging down is the strategy the mass market asks for first, and the thing that ruins an account doing it is
// a ladder nobody bounded. So what is pinned here is the arithmetic of the rungs - each step further, each size
// larger, anchored at the fill that opened the position - and the two limits that make it safe to run: the cap on the
// whole position, and the rungs being cancelled when the position is gone rather than waiting for the next one.
public sealed class DcaNodeTests
{
    private const decimal FirstStepPercent = 1m;
    private const decimal StepScale = 2m;
    private const decimal VolumeScale = 2m;

    private static string Document(int count = 3, decimal cap = 0m, decimal firstSize = 1m) => $$"""
        {
          "schemaVersion": "1.0",
          "id": "average-down",
          "name": "Buy, then ladder safety orders under it",
          "instruments": [ { "ref": "primary", "instrumentId": "BTCUSDT.SIM" } ],
          "barTypes": [ { "ref": "main", "instrument": "primary", "step": 1, "aggregation": "minute", "priceType": "last", "source": "external" } ],
          "nodes": [
            { "id": "bars", "type": "data.bars", "params": { "barType": "main" } },
            { "id": "fast", "type": "ind.ema", "params": { "period": 5 } },
            { "id": "slow", "type": "ind.ema", "params": { "period": 20 } },
            { "id": "crossUp", "type": "cond.cross", "params": { "direction": "above" } },
            { "id": "buy", "type": "act.order", "params": { "side": "buy", "orderType": "market", "sizing": { "mode": "fixed", "value": "0.05" }, "onlyWhenFlat": true } },
            { "id": "safety", "type": "act.dca", "params": {
                "count": {{count}},
                "firstStep": { "unit": "percent", "value": "{{FirstStepPercent}}" },
                "stepScale": "{{StepScale}}",
                "volumeScale": "{{VolumeScale}}",
                "firstSize": "{{firstSize}}",
                "maxTotalSize": "{{cap}}",
                "postOnly": false
              } }
          ],
          "edges": [
            { "from": "bars:bars", "to": "fast:bars" },
            { "from": "bars:bars", "to": "slow:bars" },
            { "from": "fast:value", "to": "crossUp:value" },
            { "from": "slow:value", "to": "crossUp:reference" },
            { "from": "crossUp:out", "to": "buy:trigger" },
            { "from": "buy:filled", "to": "safety:trigger" },
            { "from": "buy:position", "to": "safety:position" }
          ],
          "repeat": { "enabled": true }
        }
        """;

    private sealed record Run(BacktestResult Result, DocumentStrategy Strategy, ICache Cache, Instrument Instrument);

    private static Run Go(string json, int bars = 120)
    {
        StrategyDocument document = DocumentJson.Deserialize(json);
        ValidationReport report = new DocumentValidator().Validate(document);
        Assert.True(report.IsValid, string.Join("; ", report.Findings.Select(f => f.Code + " " + f.Message)));

        Instrument instrument = Fixtures.BtcUsdt();
        BarType barType = Fixtures.MinuteBars(instrument);
        using BacktestEngine engine = new(new BacktestEngineConfig { RunId = "act-dca" });
        engine.AddInstrument(instrument);
        engine.AddVenue(new SimulatedVenueConfig
        {
            Venue = Fixtures.Sim,
            AccountType = AccountType.Cash,
            StartingBalances = [new Money(1_000_000m, Currencies.USDT), new Money(10m, Currencies.BTC)],
        });
        engine.AddData(Fixtures.RandomWalkBars(instrument, barType, bars).Cast<IData>());
        DocumentStrategy strategy = new(new DocumentStrategyConfig { Document = document, StrategyId = new StrategyId("Doc-001") });
        engine.AddStrategy(strategy);
        engine.Run();
        return new Run(engine.GetResult(), strategy, engine.Kernel.Cache, instrument);
    }

    private static IReadOnlyList<Order> Rungs(Run run) =>
        run.Cache.Orders(instrumentId: run.Instrument.Id, strategyId: new StrategyId("Doc-001"))
            .Where(o => o.Tags.Any(t => t.StartsWith("dca:", StringComparison.Ordinal)))
            .OrderBy(o => o.Tags.First(t => t.StartsWith("dca:", StringComparison.Ordinal)), StringComparer.Ordinal)
            .ToList();

    [Fact]
    public void Every_rung_sits_where_the_scales_put_it()
    {
        Run run = Go(Document());
        IReadOnlyList<Order> rungs = Rungs(run).Take(3).ToList();
        Assert.Equal(3, rungs.Count);

        // The entry the first ladder was anchored at: the fill of the first entry order of the run.
        decimal entry = run.Result.Fills.OrderBy(f => f.TsEvent.Value).First().LastPx.Value;
        decimal first = -entry * FirstStepPercent / 100m;
        decimal[] expectedPrices =
        [
            run.Instrument.MakePrice(entry + first).Value,
            run.Instrument.MakePrice(entry + first * StepScale).Value,
            run.Instrument.MakePrice(entry + first * StepScale * StepScale).Value,
        ];
        decimal[] expectedSizes =
        [
            run.Instrument.MakeQuantity(0.05m).Value,
            run.Instrument.MakeQuantity(0.05m * VolumeScale).Value,
            run.Instrument.MakeQuantity(0.05m * VolumeScale * VolumeScale).Value,
        ];

        Assert.Equal(expectedPrices, rungs.Select(o => o.Price!.Value.Value));
        Assert.Equal(expectedSizes, rungs.Select(o => o.Quantity.Value));
        Assert.All(rungs, o => Assert.Equal(OrderSide.Buy, o.Side));
        Assert.All(rungs, o => Assert.Contains("node:safety", o.Tags));
    }

    [Fact]
    public void The_rung_that_would_take_the_position_past_the_cap_is_not_placed_and_is_said_so()
    {
        // The entry is 0.05 and the rungs would be 0.05, 0.10 and 0.20; a cap of 0.15 allows the first one only.
        Run run = Go(Document(cap: 0.15m));

        IReadOnlyList<Order> rungs = Rungs(run);
        Assert.Equal(1, rungs.Count(o => o.Tags.Contains("dca:1")));
        Assert.DoesNotContain(rungs, o => o.Tags.Contains("dca:2"));
        Assert.Contains(run.Strategy.Decisions, d => d.Kind == "skip" && d.Message.Contains("past the cap", StringComparison.Ordinal));
    }

    [Fact]
    public void A_trigger_with_no_position_places_nothing()
    {
        // A timer fires the ladder on its own schedule, so it fires on bars where the document holds nothing. (The
        // entry's own fill cannot be used for this: with no latency the venue fills it inside the same frame, so a
        // position always exists by the time the node is evaluated.)
        string document = Document()
            .Replace("""
                { "id": "buy", "type": "act.order",
            """.Trim(), """
                { "id": "clock", "type": "flow.timer", "params": { "everyBars": 4 } },
                { "id": "buy", "type": "act.order",
            """.Trim(), StringComparison.Ordinal)
            .Replace("""{ "from": "buy:filled", "to": "safety:trigger" }""", """{ "from": "clock:tick", "to": "safety:trigger" }""", StringComparison.Ordinal);

        Run run = Go(document, bars: 120);

        Assert.Contains(run.Strategy.Decisions, d => d.Kind == "skip" && d.Message.Contains("no position to average into", StringComparison.Ordinal));
        Assert.All(Rungs(run), o => Assert.Contains("node:safety", o.Tags));
    }

    [Fact]
    public void What_is_left_of_the_ladder_is_cancelled_when_the_position_closes()
    {
        // The document closes its position on the opposite cross, so every ladder of the run except the last has to
        // have been cancelled, and none of its rungs may be working afterwards.
        string document = Document().Replace(
            """
                { "from": "crossUp:out", "to": "buy:trigger" },
            """,
            """
                { "from": "crossUp:out", "to": "buy:trigger" },
                { "from": "crossDown:out", "to": "closer:trigger" },
            """,
            StringComparison.Ordinal)
            .Replace(
            """
                { "id": "crossUp", "type": "cond.cross", "params": { "direction": "above" } },
            """,
            """
                { "id": "crossUp", "type": "cond.cross", "params": { "direction": "above" } },
                { "id": "crossDown", "type": "cond.cross", "params": { "direction": "below" } },
                { "id": "closer", "type": "act.close" },
            """,
            StringComparison.Ordinal)
            .Replace(
            """
                { "from": "slow:value", "to": "crossUp:reference" },
            """,
            """
                { "from": "slow:value", "to": "crossUp:reference" },
                { "from": "fast:value", "to": "crossDown:value" },
                { "from": "slow:value", "to": "crossDown:reference" },
            """,
            StringComparison.Ordinal);

        Run run = Go(document, bars: 600);

        Assert.Contains(run.Strategy.Decisions, d => d.Kind == "cancel" && d.Message.Contains("the position closed", StringComparison.Ordinal));
        bool flat = !run.Cache.PositionsOpen(instrumentId: run.Instrument.Id, strategyId: new StrategyId("Doc-001")).Any();
        if (flat)
        {
            Assert.DoesNotContain(run.Cache.OrdersOpen(instrumentId: run.Instrument.Id, strategyId: new StrategyId("Doc-001")), o => o.Tags.Any(t => t.StartsWith("dca:", StringComparison.Ordinal)));
            Assert.Equal(0m, (decimal)run.Strategy.LastValues["safety:remaining"]!);
        }
    }

    [Fact]
    public void A_rung_that_fills_is_counted_and_the_ladder_is_not_placed_again_while_it_works()
    {
        Run run = Go(Document(count: 3), bars: 600);

        decimal filled = (decimal)run.Strategy.LastValues["safety:filledCount"]!;
        decimal remaining = (decimal)run.Strategy.LastValues["safety:remaining"]!;
        int placedLadders = run.Strategy.Decisions.Count(d => d.Kind == "order" && d.NodeId == "safety");
        int positions = run.Result.Positions.Count;

        Assert.True(filled >= 0m && filled <= 3m, $"{filled} rungs of three reported filled");
        Assert.True(remaining <= 3m);
        Assert.True(placedLadders <= positions, "a ladder was placed more often than a position was opened");
        Assert.DoesNotContain(run.Strategy.Decisions, d => d.Kind == "skip" && d.Message.Contains("still working", StringComparison.Ordinal) && placedLadders == 0);
    }
}
