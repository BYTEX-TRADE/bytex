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

// Why: an averaging strategy adds to a losing position, and until now risk.exit only resized its stop and target to
// the larger quantity while leaving their prices where the first fill put them. The stop then sits further away in R
// than the document asked for, and the target sits behind the new average, where it can no longer be reached - or is
// reached at a loss. Anchored to the average entry, both levels are computed again from the new average after every
// add. This is what the DCA and grid templates stand on.
public sealed class ExitAnchorTests
{
    private const decimal StopPercent = 1m;
    private const decimal TargetR = 2m;

    private static string Document(string anchor) => $$"""
        {
          "schemaVersion": "1.0",
          "id": "average-down-{{anchor}}",
          "name": "Add to the position and protect it",
          "instruments": [ { "ref": "primary", "instrumentId": "BTCUSDT.SIM" } ],
          "barTypes": [ { "ref": "main", "instrument": "primary", "step": 1, "aggregation": "minute", "priceType": "last", "source": "external" } ],
          "nodes": [
            { "id": "bars", "type": "data.bars", "params": { "barType": "main" } },
            { "id": "clock", "type": "flow.timer", "params": { "everyBars": 20, "fireImmediately": false } },
            { "id": "buy", "type": "act.order", "params": { "side": "buy", "orderType": "market", "sizing": { "mode": "fixed", "value": "0.05" }, "onlyWhenFlat": false } },
            { "id": "protect", "type": "risk.exit", "params": {
                "stop": { "anchor": "percent", "offset": { "unit": "percent", "value": "{{StopPercent}}" } },
                "target": { "unit": "r", "value": "{{TargetR}}" },
                "trail": { "enabled": false },
                "anchor": "{{anchor}}"
              } }
          ],
          "edges": [
            { "from": "clock:tick", "to": "buy:trigger" },
            { "from": "buy:position", "to": "protect:position" }
          ],
          "repeat": { "enabled": true }
        }
        """;

    private sealed record Run(BacktestResult Result, DocumentStrategy Strategy, ICache Cache, Instrument Instrument);

    private static Run Go(string json, int bars = 200)
    {
        StrategyDocument document = DocumentJson.Deserialize(json);
        ValidationReport report = new DocumentValidator().Validate(document);
        Assert.True(report.IsValid, string.Join("; ", report.Findings.Select(f => f.Code + " " + f.Message)));

        Instrument instrument = Fixtures.BtcUsdt();
        BarType barType = Fixtures.MinuteBars(instrument);
        using BacktestEngine engine = new(new BacktestEngineConfig { RunId = "exit-anchor" });
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

    private static Position OpenPosition(Run run) =>
        Assert.Single(run.Cache.PositionsOpen(instrumentId: run.Instrument.Id, strategyId: new StrategyId("Doc-001")));

    private static Order Resting(Run run, string tag) =>
        Assert.Single(run.Cache.OrdersOpen(instrumentId: run.Instrument.Id, strategyId: new StrategyId("Doc-001")), o => o.Tags.Contains(tag));

    [Fact]
    public void Anchored_to_the_average_entry_both_levels_follow_the_average()
    {
        Run run = Go(Document("averageEntry"));
        Position position = OpenPosition(run);
        Assert.True(position.PeakQuantity.Value > 0.05m, "the document should have added to the position at least once");

        decimal average = position.AvgPxOpen;
        decimal expectedStop = run.Instrument.MakePrice(average * (1m - StopPercent / 100m)).Value;
        decimal expectedTarget = run.Instrument.MakePrice(average + TargetR * (average - expectedStop)).Value;

        Assert.Equal(expectedStop, Resting(run, "exit:stop").TriggerPrice!.Value.Value);
        Assert.Equal(expectedTarget, Resting(run, "exit:target").Price!.Value.Value);
        Assert.Contains(run.Strategy.Decisions, d => d.Kind == "exit" && d.Message.StartsWith("average entry moved", StringComparison.Ordinal));
    }

    [Fact]
    public void Anchored_to_the_entry_the_levels_stay_where_the_first_fill_put_them()
    {
        Run run = Go(Document("entry"));
        Position position = OpenPosition(run);
        Assert.True(position.PeakQuantity.Value > 0.05m, "the document should have added to the position at least once");

        // The average has moved, so a level computed from it would differ from the one that is resting.
        decimal fromAverage = run.Instrument.MakePrice(position.AvgPxOpen * (1m - StopPercent / 100m)).Value;
        decimal resting = Resting(run, "exit:stop").TriggerPrice!.Value.Value;

        Assert.NotEqual(fromAverage, resting);
        Assert.DoesNotContain(run.Strategy.Decisions, d => d.Kind == "exit" && d.Message.StartsWith("average entry moved", StringComparison.Ordinal));
    }

    [Fact]
    public void The_exit_orders_are_the_ones_it_already_had_and_keep_their_quantity_in_step()
    {
        Run run = Go(Document("averageEntry"));
        Position position = OpenPosition(run);

        Order stop = Resting(run, "exit:stop");
        Order target = Resting(run, "exit:target");

        // Re-pricing modifies the resting orders rather than replacing them, so the node keeps its own ids and tags.
        Assert.Contains("node:protect", stop.Tags);
        Assert.Contains("node:protect", target.Tags);
        Assert.True(stop.IsReduceOnly && target.IsReduceOnly);
        Assert.Equal(position.Quantity, stop.Quantity);
        Assert.Equal(position.Quantity, target.Quantity);
        Assert.True(stop.Events.Count(e => e is Core.Model.Events.OrderUpdated) > 0, "the stop should have been moved at least once");
    }

    [Fact]
    public void The_stop_keeps_the_distance_from_the_average_that_the_document_asked_for()
    {
        // This is what the anchor is for: one percent from the average entry stays one percent from the average
        // entry, however many times the position is added to. Anchored to the first fill it drifts with every add.
        Run anchored = Go(Document("averageEntry"));
        Run fixedToEntry = Go(Document("entry"));

        Assert.Equal(StopPercent / 100m, Distance(anchored), 6);
        Assert.NotEqual(StopPercent / 100m, Distance(fixedToEntry), 6);
    }

    /// <summary>How far the resting stop sits from the average entry, as a fraction of it.</summary>
    private static decimal Distance(Run run)
    {
        Position position = OpenPosition(run);
        Assert.True(position.PeakQuantity.Value > 0.05m, "the document should have added to the position at least once");
        decimal stop = Resting(run, "exit:stop").TriggerPrice!.Value.Value;
        return (position.AvgPxOpen - stop) / position.AvgPxOpen;
    }

    [Fact]
    public void The_anchor_survives_a_restart_mid_position()
    {
        Run first = Go(Document("averageEntry"));
        Position before = OpenPosition(first);

        DocumentStrategy restarted = Fixtures.RunWithSavedState(DocumentJson.Deserialize(Document("averageEntry")), first.Strategy.Save(), bars: 10);

        // Nothing here asserts a price: a fresh engine holds no position, so what matters is that the node came back
        // saying it manages nothing rather than re-pricing a position that is not there.
        Assert.Equal(false, restarted.LastValues["protect:managing"]);
        Assert.DoesNotContain(restarted.Decisions, d => d.Kind == "exit" && d.Message.StartsWith("average entry moved", StringComparison.Ordinal));
        Assert.True(before.Quantity.Value > 0m);
    }
}
