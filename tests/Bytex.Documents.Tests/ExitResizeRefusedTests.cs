using Bytex.Backtest;
using Bytex.Core.Caching;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Events;
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

// Why: risk.exit resizes its stop and target whenever the position changes, so a strategy that adds to a position has
// its protection grow with it. If the venue REFUSES that resize, nothing used to notice - the stop stayed at the old
// smaller quantity while the position grew past it, and the position was larger than the order guarding it. On a
// leveraged instrument, with nothing visibly wrong: the stop still open, still at the right price, just for less than
// it covers.
//
// It was found on KuCoin's perpetual futures, where the venue cannot amend an order at all, so EVERY resize after a
// scale-in was refused. But it was never a venue quirk. A resize refused for any reason on any venue - a rate limit,
// a venue having a moment, the order filling while the amend was in flight - left the same half-guarded position, and
// nothing in the node catalog was watching for a rejection at all.
//
// The repair places the correctly sized order BEFORE cancelling the undersized one, so the position is briefly
// over-covered rather than ever under-covered; both orders reduce only, so the venue closes at most the position
// however many are live. Cancelling first would leave a leveraged position unguarded for a round trip in order to fix
// a partial guard, which is not obviously better than the partial guard.
public sealed class ExitResizeRefusedTests
{
    private static readonly StrategyId _strategy = new("Doc-001");

    // Adds to the position every 20 bars and protects it, which is the shape that makes a resize necessary.
    private const string Document = """
        {
          "schemaVersion": "1.0",
          "id": "adds-and-protects",
          "name": "Add to the position and protect it",
          "instruments": [ { "ref": "primary", "instrumentId": "BTCUSDT.SIM" } ],
          "barTypes": [ { "ref": "main", "instrument": "primary", "step": 1, "aggregation": "minute", "priceType": "last", "source": "external" } ],
          "nodes": [
            { "id": "bars", "type": "data.bars", "params": { "barType": "main" } },
            { "id": "clock", "type": "flow.timer", "params": { "everyBars": 20, "fireImmediately": false } },
            { "id": "buy", "type": "act.order", "params": { "side": "buy", "orderType": "market", "sizing": { "mode": "fixed", "value": "0.05" }, "onlyWhenFlat": false } },
            { "id": "protect", "type": "risk.exit", "params": {
                "stop": { "anchor": "percent", "offset": { "unit": "percent", "value": "1" } },
                "target": { "unit": "r", "value": "2" },
                "trail": { "enabled": false }
              } }
          ],
          "edges": [
            { "from": "clock:tick", "to": "buy:trigger" },
            { "from": "buy:position", "to": "protect:position" }
          ],
          "repeat": { "enabled": true }
        }
        """;

    private sealed record Run(DocumentStrategy Strategy, ICache Cache, Instrument Instrument, BacktestEngine Engine);

    private static Run Go(int bars = 200, bool refusesAmends = true)
    {
        StrategyDocument document = DocumentJson.Deserialize(Document);
        ValidationReport report = new DocumentValidator().Validate(document);
        Assert.True(report.IsValid, string.Join("; ", report.Findings.Select(f => f.Code + " " + f.Message)));

        Instrument instrument = Fixtures.BtcUsdt();
        BarType barType = Fixtures.MinuteBars(instrument);
        BacktestEngine engine = new(new BacktestEngineConfig { RunId = "exit-resize-refused" });
        engine.AddInstrument(instrument);
        engine.AddVenue(new SimulatedVenueConfig
        {
            Venue = Fixtures.Sim,
            AccountType = AccountType.Cash,
            StartingBalances = [new Money(1_000_000m, Currencies.USDT), new Money(10m, Currencies.BTC)],
            RefusesOrderAmends = refusesAmends,
        });

        engine.AddData(Fixtures.RandomWalkBars(instrument, barType, bars).Cast<IData>());
        DocumentStrategy strategy = new(new DocumentStrategyConfig { Document = document, StrategyId = _strategy });
        engine.AddStrategy(strategy);
        engine.Run();
        return new Run(strategy, engine.Kernel.Cache, instrument, engine);
    }

    private static Order Resting(Run run, string tag) =>
        Assert.Single(run.Cache.OrdersOpen(instrumentId: run.Instrument.Id, strategyId: _strategy), o => o.Tags.Contains(tag));

    private static IReadOnlyList<Order> AllResting(Run run, string tag) =>
        [.. run.Cache.OrdersOpen(instrumentId: run.Instrument.Id, strategyId: _strategy).Where(o => o.Tags.Contains(tag))];

    /// <summary>The rejection a venue that cannot amend answers a resize with.</summary>
    private static void RefuseTheResize(Run run, Order order) =>
        run.Strategy.HandleOrderEvent(new OrderModifyRejected(
            run.Cache.Orders().First().TraderId,
            _strategy,
            order.InstrumentId,
            order.ClientOrderId,
            order.VenueOrderId,
            null,
            "KuCoin futures cannot change an order once it is placed; cancel it and submit a new one",
            Guid.NewGuid(),
            run.Engine.Kernel.Clock.Timestamp,
            run.Engine.Kernel.Clock.Timestamp));

    /// <summary>Every protective order of one kind the run ever created, open or not.</summary>
    private static IReadOnlyList<Order> EverPlaced(Run run, string tag) =>
        [.. run.Cache.Orders(instrumentId: run.Instrument.Id, strategyId: _strategy).Where(o => o.Tags.Contains(tag))];

    [Fact]
    public void On_a_venue_that_cannot_amend_the_stop_still_covers_the_whole_position()
    {
        // The invariant, and the one that failed. Without a reaction to the rejection the stop keeps the quantity it
        // was first placed with while the position is added to again and again, so it ends up guarding a fraction of
        // what it is there for - and nothing says so.
        Run run = Go(refusesAmends: true);
        using BacktestEngine _ = run.Engine;

        Position position = Assert.Single(run.Cache.PositionsOpen(instrumentId: run.Instrument.Id, strategyId: _strategy));
        Assert.True(position.PeakQuantity.Value > 0.05m, "the document must have added to the position at least once");

        Order stop = Resting(run, "exit:stop");
        Assert.Equal(position.Quantity.Value, stop.Quantity.Value);
        Assert.True(stop.IsReduceOnly, "a protective order must not be able to open a position");
    }

    [Fact]
    public void The_repair_replaces_rather_than_accumulates()
    {
        // Each refusal produces one new order and cancels the one it replaces, so a long run on such a venue leaves
        // exactly one stop and one target working - not a pile of them, each covering part of the position.
        Run run = Go(refusesAmends: true);
        using BacktestEngine _ = run.Engine;

        Assert.Single(AllResting(run, "exit:stop"));

        // And it really did have to repair: more stops were placed over the run than the one it started with.
        Assert.True(
            EverPlaced(run, "exit:stop").Count > 1,
            "a venue that refuses every amend should have forced at least one replacement");

        // Every one of them reduce only, including the replacements.
        Assert.All(EverPlaced(run, "exit:stop"), o => Assert.True(o.IsReduceOnly));
    }

    [Fact]
    public void A_venue_that_can_amend_is_left_alone()
    {
        // The control. Where the venue resizes, nothing is replaced and nothing is cancelled: the repair is a
        // reaction to a refusal, not a second way of doing the same job.
        Run run = Go(refusesAmends: false);
        using BacktestEngine _ = run.Engine;

        Position position = Assert.Single(run.Cache.PositionsOpen(instrumentId: run.Instrument.Id, strategyId: _strategy));
        Order stop = Resting(run, "exit:stop");

        Assert.Equal(position.Quantity.Value, stop.Quantity.Value);
        Assert.Single(EverPlaced(run, "exit:stop"));
    }

    // TWO TESTS WERE REMOVED FROM HERE, and what they were meant to cover is worth writing down rather than
    // leaving as a gap somebody has to rediscover.
    //
    // They fed a rejection to the strategy AFTER the backtest had finished - one for an order that already covered
    // the position, one for an order this node does not own - and asserted that nothing was replaced. Both passed
    // with the code they were testing deleted, because a finished backtest no longer processes a submission: the
    // repair ran, submitted an order, and the exchange was never going to accept it. So they proved the node does
    // not crash and nothing else.
    //
    // Both guards are still in the node, and both are obviously right: an order that over-covers is capped by
    // reduce-only and needs no repair, and a rejection for somebody else's order must not produce a protective
    // order against a position this node does not manage. Neither is covered by a test, and reaching them needs a
    // live kernel rather than a completed run, because they are cases the simulator will not produce by itself.
}
