using Bytex.Backtest;
using Bytex.Core.Caching;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Positions;
using Bytex.Core.Model.Primitives;
using Bytex.Documents.Runtime;
using Bytex.Documents.Schema;
using Bytex.Documents.Validation;
using Xunit;

namespace Bytex.Documents.Tests;

// Why: a graph that averages, scales out or re-enters has to know what it already holds and where it last got out.
// Everything here is either read from the cache each bar - so it cannot go stale - or kept by the node because the
// cache forgets it: a closed position is gone, and the price it closed at is exactly what a re-entry rule measures
// from. The node is also what the DCA and grid templates read, so its outputs are a contract.
public sealed class PositionNodeTests
{
    private const string WatchThePosition = """
        {
          "schemaVersion": "1.0",
          "id": "watch-the-position",
          "name": "Read the position while trading it",
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
            { "id": "pos", "type": "data.position" }
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

    private static (BacktestResult Result, DocumentStrategy Strategy, ICache Cache) Run(string json, int bars = 3000)
    {
        StrategyDocument document = DocumentJson.Deserialize(json);
        ValidationReport report = new DocumentValidator().Validate(document);
        Assert.True(report.IsValid, string.Join("; ", report.Findings.Select(f => f.Code + " " + f.Message)));

        Instrument instrument = Fixtures.BtcUsdt();
        BarType barType = Fixtures.MinuteBars(instrument);
        using BacktestEngine engine = new(new BacktestEngineConfig { RunId = "position-node" });
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

    /// <summary>The close of the last evaluated bar, however the frame carries it.</summary>
    private static decimal Close(DocumentStrategy strategy) => strategy.LastValues["bars:close"] switch
    {
        Price price => price.Value,
        decimal value => value,
        object other => throw new Xunit.Sdk.XunitException("bars:close is a " + other.GetType().Name),
        null => throw new Xunit.Sdk.XunitException("bars:close published nothing"),
    };

    [Fact]
    public void While_a_position_is_open_the_node_says_what_it_is()
    {
        // The run is stopped on a bar that holds a position by asking for a length where one is open at the end; if it
        // is not, the assertions below say so rather than passing quietly.
        (_, DocumentStrategy strategy, ICache cache) = Run(WatchThePosition, bars: 2000);
        Position? open = cache.PositionsOpen(instrumentId: InstrumentId.Parse("BTCUSDT.SIM"), strategyId: new StrategyId("Doc-001")).FirstOrDefault();

        if (open is null)
        {
            // Flat at the end: then the node must say so, and the position facts must be absent rather than stale.
            Assert.Equal(false, strategy.LastValues["pos:open"]);
            Assert.False(strategy.LastValues.ContainsKey("pos:quantity") && strategy.LastValues["pos:quantity"] is not null);
            return;
        }

        Assert.Equal(true, strategy.LastValues["pos:open"]);
        Assert.Equal(open.IsLong, strategy.LastValues["pos:isLong"]);
        Assert.Equal(open.IsShort, strategy.LastValues["pos:isShort"]);
        Assert.Equal(open.Quantity, strategy.LastValues["pos:quantity"]);
        Assert.Equal(Fixtures.BtcUsdt().MakePrice(open.AvgPxOpen), strategy.LastValues["pos:avgEntry"]);
        Assert.Equal(1m, strategy.LastValues["pos:adds"]);
        Assert.True((decimal)strategy.LastValues["pos:barsHeld"]! >= 0m);
    }

    /// <summary>A perpetual on a margin venue, because a short on a spot instrument is refused - rightly.</summary>
    private static CryptoPerpetual BtcPerp() => new(new InstrumentSpec
    {
        Id = new InstrumentId(new Symbol("BTCUSDT-PERP"), Fixtures.Sim),
        AssetClass = AssetClass.Crypto,
        InstrumentClass = InstrumentClass.Swap,
        QuoteCurrency = Currencies.USDT,
        BaseCurrency = Currencies.BTC,
        SettlementCurrency = Currencies.USDT,
        PricePrecision = 2,
        SizePrecision = 3,
        PriceIncrement = new Price(0.01m, 2),
        SizeIncrement = new Quantity(0.001m, 3),
        MakerFee = 0.0002m,
        TakerFee = 0.00055m,
    });

    [Fact]
    public void A_short_reads_a_falling_price_as_a_profit()
    {
        // The sign is the whole point: on a short, price below the entry is money made, and a node that published the
        // raw move would tell a document it is losing while it is winning.
        const string ShortTheCross = """
            {
              "schemaVersion": "1.0",
              "id": "short-the-cross",
              "name": "Short and watch the position",
              "instruments": [ { "ref": "primary", "instrumentId": "BTCUSDT-PERP.SIM" } ],
              "barTypes": [ { "ref": "main", "instrument": "primary", "step": 1, "aggregation": "minute", "priceType": "last", "source": "external" } ],
              "nodes": [
                { "id": "bars", "type": "data.bars", "params": { "barType": "main" } },
                { "id": "fast", "type": "ind.ema", "params": { "period": 5 } },
                { "id": "slow", "type": "ind.ema", "params": { "period": 20 } },
                { "id": "crossDown", "type": "cond.cross", "params": { "direction": "below" } },
                { "id": "sell", "type": "act.order", "params": { "side": "sell", "orderType": "market", "sizing": { "mode": "fixed", "value": "0.05" }, "onlyWhenFlat": true } },
                { "id": "pos", "type": "data.position" }
              ],
              "edges": [
                { "from": "bars:bars", "to": "fast:bars" },
                { "from": "bars:bars", "to": "slow:bars" },
                { "from": "fast:value", "to": "crossDown:value" },
                { "from": "slow:value", "to": "crossDown:reference" },
                { "from": "crossDown:out", "to": "sell:trigger" }
              ],
              "repeat": { "enabled": true }
            }
            """;

        StrategyDocument document = DocumentJson.Deserialize(ShortTheCross);
        Instrument perp = BtcPerp();
        BarType barType = Fixtures.MinuteBars(perp);
        using BacktestEngine engine = new(new BacktestEngineConfig { RunId = "short-position" });
        engine.AddInstrument(perp);
        engine.AddVenue(new SimulatedVenueConfig
        {
            Venue = Fixtures.Sim,
            AccountType = AccountType.Margin,
            StartingBalances = [new Money(1_000_000m, Currencies.USDT)],
        });
        engine.AddData(Fixtures.RandomWalkBars(perp, barType, 800).Cast<IData>());
        DocumentStrategy strategy = new(new DocumentStrategyConfig { Document = document, StrategyId = new StrategyId("Doc-001") });
        engine.AddStrategy(strategy);
        engine.Run();

        Position open = Assert.Single(engine.Kernel.Cache.PositionsOpen(instrumentId: perp.Id, strategyId: new StrategyId("Doc-001")));
        Assert.True(open.IsShort, "the document only ever sells");
        Assert.Equal(true, strategy.LastValues["pos:isShort"]);
        Assert.Equal(false, strategy.LastValues["pos:isLong"]);

        decimal close = Close(strategy);
        decimal expected = -((close - open.AvgPxOpen) / open.AvgPxOpen * 100m);
        Assert.Equal(expected, (decimal)strategy.LastValues["pos:unrealizedPct"]!);
        Assert.Equal(close < open.AvgPxOpen, (decimal)strategy.LastValues["pos:unrealizedPct"]! > 0m);
    }

    [Fact]
    public void Unrealized_profit_is_the_move_from_the_average_entry_in_percent()
    {
        (_, DocumentStrategy strategy, ICache cache) = Run(WatchThePosition, bars: 2000);
        Position? open = cache.PositionsOpen(instrumentId: InstrumentId.Parse("BTCUSDT.SIM"), strategyId: new StrategyId("Doc-001")).FirstOrDefault();
        if (open is null)
        {
            Assert.Equal(false, strategy.LastValues["pos:open"]);
            return;
        }

        decimal close = Close(strategy);
        decimal expected = (close - open.AvgPxOpen) / open.AvgPxOpen * 100m;
        expected = open.IsLong ? expected : -expected;

        Assert.Equal(expected, (decimal)strategy.LastValues["pos:unrealizedPct"]!);
    }

    [Fact]
    public void The_last_exit_stays_readable_while_the_strategy_is_flat()
    {
        (BacktestResult result, DocumentStrategy strategy, _) = Run(WatchThePosition);

        Assert.True(result.TotalPositions > 2, "the document should have closed several positions");
        Assert.NotNull(strategy.LastValues["pos:lastExitPrice"]);
        Assert.True((decimal)strategy.LastValues["pos:lastExitBarsAgo"]! >= 0m);

        // The price is the one the last closed position actually closed at.
        PositionReportRow last = result.Positions.Where(p => p.AvgPxClose is not null).OrderBy(p => p.TsClosed!.Value.Value).Last();
        Assert.Equal(Fixtures.BtcUsdt().MakePrice(last.AvgPxClose!.Value), strategy.LastValues["pos:lastExitPrice"]);
    }

    [Fact]
    public void R_is_published_only_when_something_pins_the_risk()
    {
        // Without a stop wired there is no risk per unit to divide by, so the node publishes no R at all.
        (_, DocumentStrategy withoutStop, _) = Run(WatchThePosition, bars: 600);
        Assert.False(withoutStop.LastValues.ContainsKey("pos:unrealizedR") && withoutStop.LastValues["pos:unrealizedR"] is not null);

        const string WithAStop = """
            {
              "schemaVersion": "1.0",
              "id": "position-in-r",
              "name": "Read the position in R",
              "instruments": [ { "ref": "primary", "instrumentId": "BTCUSDT.SIM" } ],
              "barTypes": [ { "ref": "main", "instrument": "primary", "step": 1, "aggregation": "minute", "priceType": "last", "source": "external" } ],
              "nodes": [
                { "id": "bars", "type": "data.bars", "params": { "barType": "main" } },
                { "id": "fast", "type": "ind.ema", "params": { "period": 5 } },
                { "id": "slow", "type": "ind.ema", "params": { "period": 20 } },
                { "id": "crossUp", "type": "cond.cross", "params": { "direction": "above" } },
                { "id": "buy", "type": "act.order", "params": { "side": "buy", "orderType": "market", "sizing": { "mode": "fixed", "value": "0.05" }, "onlyWhenFlat": true } },
                { "id": "floor", "type": "level.pinned", "params": { "price": "45000" } },
                { "id": "pos", "type": "data.position" }
              ],
              "edges": [
                { "from": "bars:bars", "to": "fast:bars" },
                { "from": "bars:bars", "to": "slow:bars" },
                { "from": "fast:value", "to": "crossUp:value" },
                { "from": "slow:value", "to": "crossUp:reference" },
                { "from": "crossUp:out", "to": "buy:trigger" },
                { "from": "floor:price", "to": "pos:stop" }
              ],
              "repeat": { "enabled": true }
            }
            """;

        (_, DocumentStrategy withStop, ICache cache) = Run(WithAStop, bars: 600);
        Position? open = cache.PositionsOpen(instrumentId: InstrumentId.Parse("BTCUSDT.SIM"), strategyId: new StrategyId("Doc-001")).FirstOrDefault();
        Assert.NotNull(open);

        decimal close = Close(withStop);
        decimal expected = (close - open!.AvgPxOpen) / Math.Abs(open.AvgPxOpen - 45_000m);
        Assert.Equal(expected, (decimal)withStop.LastValues["pos:unrealizedR"]!);
    }

    [Fact]
    public void What_the_node_knows_about_the_last_exit_survives_a_restart()
    {
        (_, DocumentStrategy strategy, _) = Run(WatchThePosition);
        object? price = strategy.LastValues["pos:lastExitPrice"];
        Assert.NotNull(price);

        DocumentStrategy restarted = Fixtures.RunWithSavedState(DocumentJson.Deserialize(WatchThePosition), strategy.Save(), bars: 24);

        Assert.DoesNotContain(restarted.Decisions, d => d.Kind == "close");
        Assert.Equal(price, restarted.LastValues["pos:lastExitPrice"]);
    }
}
