using Bytex.Backtest;
using Bytex.Core.Model;
using Bytex.Core.Caching;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Model.Orders;
using Bytex.Documents.Runtime;
using Bytex.Documents.Schema;
using Bytex.Documents.Validation;
using Xunit;

namespace Bytex.Documents.Tests;

// Why: closing a position is half an exit; the other half is knowing at what price it happened. A document that
// re-enters after an exit, reports what it made, or waits for the exit to complete needs the price and the moment,
// and both come from the order the node placed - so the node has to own that order rather than ask the strategy to
// flatten and forget what happened.
public sealed class CloseNodeTests
{
    private const string BuyThenClose = """
        {
          "schemaVersion": "1.0",
          "id": "close-publishes-its-fill",
          "name": "Buy, then close on the next bar",
          "instruments": [ { "ref": "primary", "instrumentId": "BTCUSDT.SIM" } ],
          "barTypes": [ { "ref": "main", "instrument": "primary", "step": 1, "aggregation": "minute", "priceType": "last", "source": "external" } ],
          "nodes": [
            { "id": "bars", "type": "data.bars", "params": { "barType": "main" } },
            { "id": "fast", "type": "ind.ema", "params": { "period": 5 } },
            { "id": "slow", "type": "ind.ema", "params": { "period": 20 } },
            { "id": "crossUp", "type": "cond.cross", "params": { "direction": "above" } },
            { "id": "crossDown", "type": "cond.cross", "params": { "direction": "below" } },
            { "id": "buy", "type": "act.order", "params": { "side": "buy", "orderType": "market", "sizing": { "mode": "fixed", "value": "0.05" }, "onlyWhenFlat": true } },
            { "id": "closer", "type": "act.close" }
          ],
          "edges": [
            { "from": "bars:bars", "to": "fast:bars" },
            { "from": "bars:bars", "to": "slow:bars" },
            { "from": "fast:value", "to": "crossUp:value" },
            { "from": "slow:value", "to": "crossUp:reference" },
            { "from": "fast:value", "to": "crossDown:value" },
            { "from": "slow:value", "to": "crossDown:reference" },
            { "from": "crossUp:out", "to": "buy:trigger" },
            { "from": "crossDown:out", "to": "closer:trigger" }
          ],
          "repeat": { "enabled": true }
        }
        """;

    /// <summary>
    /// Runs the document and hands back the cache as well: a report row carries neither the tags nor the reduce-only
    /// flag, and both are the point here.
    /// </summary>
    private static (BacktestResult Result, DocumentStrategy Strategy, ICache Cache) Run(string json, int bars = 3000)
    {
        StrategyDocument document = DocumentJson.Deserialize(json);
        ValidationReport report = new DocumentValidator().Validate(document);
        Assert.True(report.IsValid, string.Join("; ", report.Findings.Select(f => f.Code + " " + f.Message)));

        Instrument instrument = Fixtures.BtcUsdt();
        BarType barType = Fixtures.MinuteBars(instrument);
        using BacktestEngine engine = new(new BacktestEngineConfig { RunId = "close-node" });
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
        return (engine.GetResult(), strategy, engine.Kernel.Cache);
    }

    private static IReadOnlyList<Order> Closes(ICache cache) =>
        cache.Orders(instrumentId: InstrumentId.Parse("BTCUSDT.SIM")).Where(o => o.Tags.Contains("exit:close")).ToList();

    [Fact]
    public void The_order_that_closes_a_position_belongs_to_the_node_that_placed_it()
    {
        (_, DocumentStrategy strategy, ICache cache) = Run(BuyThenClose);

        Order close = Closes(cache)[0];
        Assert.Contains("node:closer", close.Tags);
        Assert.Equal(OrderType.Market, close.Type);
        Assert.Equal(OrderSide.Sell, close.Side); // the position was long
        Assert.True(close.IsReduceOnly, "a closing order must never open the other side");
        Assert.Contains(strategy.Decisions, d => d.Kind == "close" && d.NodeId == "closer");
    }

    [Fact]
    public void The_price_the_position_was_closed_at_is_published()
    {
        (_, DocumentStrategy strategy, ICache cache) = Run(BuyThenClose);

        // The frame carries the price of the last close that filled, to the last decimal.
        IReadOnlyList<Order> filled = Closes(cache).Where(o => o.Status == OrderStatus.Filled).ToList();
        Assert.NotEmpty(filled);
        Assert.Equal(filled[^1].AvgPx, strategy.LastValues["closer:fillPrice"]);
        Assert.Contains(strategy.Decisions, d => d.Kind == "close" && d.Values.ContainsKey("quantity"));
    }

    [Fact]
    public void The_closed_pulse_fires_once_per_close_and_is_down_between_them()
    {
        // A counter wired to the pulse is the only way to see a pulse from outside the frame it fired in: at the end
        // of the run it has counted exactly as many closes as the venue filled.
        const string CountTheCloses = """
            {
              "schemaVersion": "1.0",
              "id": "count-the-closes",
              "name": "Count what the closer closed",
              "instruments": [ { "ref": "primary", "instrumentId": "BTCUSDT.SIM" } ],
              "barTypes": [ { "ref": "main", "instrument": "primary", "step": 1, "aggregation": "minute", "priceType": "last", "source": "external" } ],
              "nodes": [
                { "id": "bars", "type": "data.bars", "params": { "barType": "main" } },
                { "id": "fast", "type": "ind.ema", "params": { "period": 5 } },
                { "id": "slow", "type": "ind.ema", "params": { "period": 20 } },
                { "id": "crossUp", "type": "cond.cross", "params": { "direction": "above" } },
                { "id": "crossDown", "type": "cond.cross", "params": { "direction": "below" } },
                { "id": "buy", "type": "act.order", "params": { "side": "buy", "orderType": "market", "sizing": { "mode": "fixed", "value": "0.05" }, "onlyWhenFlat": true } },
                { "id": "closer", "type": "act.close" },
                { "id": "exits", "type": "flow.counter", "params": { "target": 1000000, "resetOnPhaseEntry": false } }
              ],
              "edges": [
                { "from": "bars:bars", "to": "fast:bars" },
                { "from": "bars:bars", "to": "slow:bars" },
                { "from": "fast:value", "to": "crossUp:value" },
                { "from": "slow:value", "to": "crossUp:reference" },
                { "from": "fast:value", "to": "crossDown:value" },
                { "from": "slow:value", "to": "crossDown:reference" },
                { "from": "crossUp:out", "to": "buy:trigger" },
                { "from": "crossDown:out", "to": "closer:trigger" },
                { "from": "closer:filled", "to": "exits:increment" }
              ],
              "repeat": { "enabled": true }
            }
            """;

        (BacktestResult result, DocumentStrategy strategy, ICache cache) = Run(CountTheCloses);

        int filledCloses = Closes(cache).Count(o => o.Status == OrderStatus.Filled);
        Assert.True(filledCloses > 1, "the document should have closed several positions");
        Assert.Equal((decimal)filledCloses, strategy.LastValues["exits:count"]);
        // The run ends outside a close, so the pulse is down while the price it last exited at stays readable.
        Assert.Equal(false, strategy.LastValues["closer:filled"]);
        Assert.NotNull(strategy.LastValues["closer:fillPrice"]);
        Assert.True(result.TotalPositions > 1, "the document should have completed several round trips");
    }

    [Fact]
    public void A_trigger_while_the_position_is_already_flat_places_nothing()
    {
        // The close is wired straight to the entry's own trigger, so it fires on a bar with nothing to close.
        const string CloseOnATimer = """
            {
              "schemaVersion": "1.0",
              "id": "close-on-a-timer",
              "name": "Close every few bars, position or not",
              "instruments": [ { "ref": "primary", "instrumentId": "BTCUSDT.SIM" } ],
              "barTypes": [ { "ref": "main", "instrument": "primary", "step": 1, "aggregation": "minute", "priceType": "last", "source": "external" } ],
              "nodes": [
                { "id": "bars", "type": "data.bars", "params": { "barType": "main" } },
                { "id": "fast", "type": "ind.ema", "params": { "period": 5 } },
                { "id": "slow", "type": "ind.ema", "params": { "period": 20 } },
                { "id": "crossUp", "type": "cond.cross", "params": { "direction": "above" } },
                { "id": "clock", "type": "flow.timer", "params": { "everyBars": 3 } },
                { "id": "buy", "type": "act.order", "params": { "side": "buy", "orderType": "market", "sizing": { "mode": "fixed", "value": "0.05" }, "onlyWhenFlat": true } },
                { "id": "closer", "type": "act.close" }
              ],
              "edges": [
                { "from": "bars:bars", "to": "fast:bars" },
                { "from": "bars:bars", "to": "slow:bars" },
                { "from": "fast:value", "to": "crossUp:value" },
                { "from": "slow:value", "to": "crossUp:reference" },
                { "from": "crossUp:out", "to": "buy:trigger" },
                { "from": "clock:tick", "to": "closer:trigger" }
              ],
              "repeat": { "enabled": true }
            }
            """;

        (_, DocumentStrategy strategy, ICache cache) = Run(CloseOnATimer, bars: 400);

        // The timer fired far more often than the document held a position, so most triggers found nothing to close.
        int triggers = strategy.Decisions.Count(d => d.Kind == "close");
        IReadOnlyList<Order> closes = Closes(cache);
        Assert.True(triggers < 400 / 3, "the timer fired on every third bar; a close cannot have been placed each time");
        Assert.Equal(triggers, closes.Count);
        Assert.All(closes, o => Assert.True(o.IsReduceOnly && !o.Quantity.IsZero));
    }

    [Fact]
    public void What_the_node_knows_about_its_last_exit_survives_a_restart()
    {
        (_, DocumentStrategy strategy, _) = Run(BuyThenClose);
        object? before = strategy.LastValues["closer:fillPrice"];
        Assert.NotNull(before);

        // The same document started again over the state the first run saved, the way a live node restarts over its
        // own store. The second run is too short to close anything of its own, so what the node publishes is what it
        // read back - which is the point: a document that acts on where it got out can do so after a restart.
        DocumentStrategy restarted = Fixtures.RunWithSavedState(DocumentJson.Deserialize(BuyThenClose), strategy.Save(), bars: 24);

        Assert.DoesNotContain(restarted.Decisions, d => d.Kind == "close");
        Assert.Equal(before, restarted.LastValues["closer:fillPrice"]);
        Assert.Equal(false, restarted.LastValues["closer:filled"]);
    }
}
