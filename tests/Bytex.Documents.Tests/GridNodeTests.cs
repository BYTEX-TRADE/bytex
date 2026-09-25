using Bytex.Backtest;
using Bytex.Core.Caching;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Documents.Runtime;
using Bytex.Documents.Schema;
using Bytex.Documents.Validation;
using Xunit;

namespace Bytex.Documents.Tests;

// Why: a grid is the one structure a document could not express by hand. An order node per level places each level
// once; nothing refills a level that has been through, and nothing takes the whole thing back. So what is pinned here
// is the arithmetic of the levels, the pair each level takes turns with, the refill after a round trip, and the two
// ways the grid comes off the venue: switched off mid-run, and the strategy stopping. A level the venue will not take
// has to be reported, because a grid quietly missing a level is a grid nobody can reconcile against their account.
public sealed class GridNodeTests
{
    private const int Levels = 5;
    private const decimal ProfitPerGrid = 0.5m;
    private const decimal SizePerLevel = 0.01m;
    private const string Node = "grid";
    private static readonly StrategyId Doc = new("Doc-001");

    private static string Document(decimal from, decimal to, string enable = """{ "id": "on", "type": "cond.compare", "params": { "op": "gt", "value": "0" } }""",
        int levels = Levels, decimal size = SizePerLevel, string side = "buy") => $$"""
        {
          "schemaVersion": "1.0",
          "id": "work-the-range",
          "name": "Work a range level by level",
          "instruments": [ { "ref": "primary", "instrumentId": "BTCUSDT.SIM" } ],
          "barTypes": [ { "ref": "main", "instrument": "primary", "step": 1, "aggregation": "minute", "priceType": "last", "source": "external" } ],
          "nodes": [
            { "id": "bars", "type": "data.bars", "params": { "barType": "main" } },
            { "id": "avg", "type": "ind.ema", "params": { "period": 5 } },
            {{enable}},
            { "id": "low", "type": "level.pinned", "params": { "price": "{{from}}" } },
            { "id": "high", "type": "level.pinned", "params": { "price": "{{to}}" } },
            { "id": "{{Node}}", "type": "act.grid", "params": {
                "levels": {{levels}},
                "profitPerGrid": "{{ProfitPerGrid}}",
                "sizePerLevel": "{{size}}",
                "side": "{{side}}",
                "postOnly": false
              } }
          ],
          "edges": [
            { "from": "bars:bars", "to": "avg:bars" },
            { "from": "avg:value", "to": "on:a" },
            { "from": "on:out", "to": "{{Node}}:enable" },
            { "from": "low:price", "to": "{{Node}}:from" },
            { "from": "high:price", "to": "{{Node}}:to" }
          ]
        }
        """;

    private sealed record Run(BacktestResult Result, DocumentStrategy Strategy, ICache Cache, Instrument Instrument);

    private static Run Go(string json, int bars = 400, Instrument? instrument = null)
    {
        StrategyDocument document = DocumentJson.Deserialize(json);
        ValidationReport report = new DocumentValidator().Validate(document);
        Assert.True(report.IsValid, string.Join("; ", report.Findings.Select(f => f.Code + " " + f.Message)));

        instrument ??= Fixtures.BtcUsdt();
        BarType barType = Fixtures.MinuteBars(instrument);
        using BacktestEngine engine = new(new BacktestEngineConfig { RunId = "act-grid" });
        engine.AddInstrument(instrument);
        engine.AddVenue(new SimulatedVenueConfig
        {
            Venue = Fixtures.Sim,
            AccountType = AccountType.Cash,
            StartingBalances = [new Money(1_000_000m, Currencies.USDT), new Money(10m, Currencies.BTC)],
        });
        engine.AddData(Fixtures.RandomWalkBars(instrument, barType, bars).Cast<IData>());
        DocumentStrategy strategy = new(new DocumentStrategyConfig { Document = document, StrategyId = Doc });
        engine.AddStrategy(strategy);
        engine.Run();
        return new Run(engine.GetResult(), strategy, engine.Kernel.Cache, instrument);
    }

    /// <summary>The range the walk actually visits, so the levels are prices the run reaches.</summary>
    private static (decimal Low, decimal High) Range(Instrument instrument, decimal inset = 0.25m)
    {
        IReadOnlyList<Bar> bars = Fixtures.RandomWalkBars(instrument, Fixtures.MinuteBars(instrument), 400);
        decimal low = bars.Min(b => b.Low.Value);
        decimal high = bars.Max(b => b.High.Value);
        decimal margin = (high - low) * inset;
        return (instrument.MakePrice(low + margin).Value, instrument.MakePrice(high - margin).Value);
    }

    private static IReadOnlyList<Order> Rungs(Run run, string what) =>
        run.Cache.Orders(instrumentId: run.Instrument.Id, strategyId: Doc).Where(o => o.Tags.Contains("grid:" + what)).ToList();

    private static string KeyOf(Order order) => order.Tags.First(t => t.StartsWith("grid:", StringComparison.Ordinal) && t != "grid:entry" && t != "grid:exit")["grid:".Length..];

    [Fact]
    public void Every_level_of_the_range_gets_an_order_where_the_range_puts_it()
    {
        Instrument instrument = Fixtures.BtcUsdt();
        (decimal low, decimal high) = Range(instrument);

        Run run = Go(Document(low, high));

        IReadOnlyList<Order> first = Rungs(run, "entry").Take(Levels).ToList();
        decimal[] expected = Enumerable.Range(0, Levels)
            .Select(i => instrument.MakePrice(low + ((high - low) * i / (Levels - 1))).Value)
            .ToArray();

        Assert.Equal(Levels, first.Count);
        Assert.Equal(expected, first.Select(o => o.Price!.Value.Value));
        Assert.Equal(["1", "2", "3", "4", "5"], first.Select(KeyOf));
        Assert.All(first, o => Assert.Equal(OrderSide.Buy, o.Side));
        Assert.All(first, o => Assert.Equal(instrument.MakeQuantity(SizePerLevel).Value, o.Quantity.Value));
        Assert.All(first, o => Assert.Contains("node:" + Node, o.Tags));
        Assert.Contains(run.Strategy.Decisions, d => d.NodeId == Node && d.Kind == "order" && d.Message.Contains($"grid armed: {Levels} levels", StringComparison.Ordinal));
        Assert.Equal((decimal)Levels, (decimal)run.Strategy.LastValues[Node + ":levels"]!);
    }

    [Fact]
    public void The_level_that_entered_is_the_level_that_takes_the_profit()
    {
        Instrument instrument = Fixtures.BtcUsdt();
        (decimal low, decimal high) = Range(instrument);

        Run run = Go(Document(low, high));

        IReadOnlyList<Order> exits = Rungs(run, "exit");
        Assert.NotEmpty(exits);
        Assert.All(exits, o => Assert.Equal(OrderSide.Sell, o.Side));

        // Every exit sits a grid's profit above the price of the level that placed it, and nowhere else.
        Dictionary<string, decimal> levelPrice = Enumerable.Range(0, Levels)
            .ToDictionary(i => (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                i => instrument.MakePrice(low + ((high - low) * i / (Levels - 1))).Value);
        foreach (Order exit in exits)
        {
            decimal entry = levelPrice[KeyOf(exit)];
            Assert.Equal(instrument.MakePrice(entry * (1m + (ProfitPerGrid / 100m))).Value, exit.Price!.Value.Value);
            Assert.Equal(instrument.MakeQuantity(SizePerLevel).Value, exit.Quantity.Value);
        }

        Assert.Contains(run.Strategy.Decisions, d => d.NodeId == Node && d.Message.Contains("entered at", StringComparison.Ordinal) && d.Message.Contains("taking the profit at", StringComparison.Ordinal));
    }

    [Fact]
    public void A_level_that_turns_over_is_armed_again_at_the_same_price()
    {
        Instrument instrument = Fixtures.BtcUsdt();
        (decimal low, decimal high) = Range(instrument);

        Run run = Go(Document(low, high));

        decimal roundTrips = (decimal)run.Strategy.LastValues[Node + ":roundTrips"]!;
        Assert.True(roundTrips > 0m, "no level completed a round trip over the range the walk works");
        Assert.Contains(run.Strategy.Decisions, d => d.NodeId == Node && d.Message.Contains("turned over", StringComparison.Ordinal));

        // A level that turned over has more entry orders than the one the grid armed it with, all at its own price.
        IEnumerable<IGrouping<string, Order>> byLevel = Rungs(run, "entry").GroupBy(KeyOf);
        Assert.Contains(byLevel, g => g.Count() > 1);
        foreach (IGrouping<string, Order> level in byLevel)
        {
            Assert.Single(level.Select(o => o.Price!.Value.Value).Distinct());
        }

        int turnedOver = run.Strategy.Decisions.Count(d => d.NodeId == Node && d.Message.Contains("turned over", StringComparison.Ordinal));
        Assert.Equal((int)roundTrips, turnedOver);
        Assert.Equal(Rungs(run, "entry").Count - Levels, turnedOver);
    }

    [Fact]
    public void A_level_the_venue_would_not_take_is_reported_rather_than_left_out_in_silence()
    {
        // The same range on an instrument that will not trade anything under a million quote: no level can be placed,
        // every one of them is named, and the grid does not report itself armed.
        Instrument tiny = Fixtures.BtcUsdt();
        (decimal low, decimal high) = Range(tiny);
        Instrument fussy = new CurrencyPair(new InstrumentSpec
        {
            Id = tiny.Id,
            AssetClass = AssetClass.Crypto,
            InstrumentClass = InstrumentClass.Spot,
            QuoteCurrency = Currencies.USDT,
            BaseCurrency = Currencies.BTC,
            PricePrecision = 2,
            SizePrecision = 6,
            PriceIncrement = new Price(0.01m, 2),
            SizeIncrement = new Quantity(0.000001m, 6),
            MinQuantity = new Quantity(0.00001m, 6),
            MinNotional = new Money(1_000_000m, Currencies.USDT),
            MakerFee = 0.001m,
            TakerFee = 0.001m,
        });

        Run run = Go(Document(low, high), instrument: fussy);

        for (int i = 1; i <= Levels; i++)
        {
            Assert.Contains(run.Strategy.Decisions, d => d.Kind == "skip" && d.Message.Contains($"level {i} of {Levels}", StringComparison.Ordinal)
                && d.Message.Contains("below the venue's minimum of 1000000", StringComparison.Ordinal));
        }

        Assert.False((bool)run.Strategy.LastValues[Node + ":armed"]!);
        Assert.Empty(Rungs(run, "entry"));

        // Said once, not once a bar: the range never changed, so there was nothing to say again.
        Assert.Equal(Levels, run.Strategy.Decisions.Count(d => d.Kind == "skip" && d.Message.Contains("below the venue's minimum", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_range_with_no_width_is_refused_once_and_said_so()
    {
        Instrument instrument = Fixtures.BtcUsdt();
        (decimal low, _) = Range(instrument);

        Run run = Go(Document(low, low));

        Assert.Equal(1, run.Strategy.Decisions.Count(d => d.Kind == "skip" && d.Message.Contains("has no width", StringComparison.Ordinal)));
        Assert.False((bool)run.Strategy.LastValues[Node + ":armed"]!);
        Assert.Empty(Rungs(run, "entry"));
    }

    [Fact]
    public void Switching_the_grid_off_takes_back_what_is_resting()
    {
        // Enabled while the average is under the middle of the walk, so the grid is armed and switched off again
        // inside one run.
        Instrument instrument = Fixtures.BtcUsdt();
        (decimal low, decimal high) = Range(instrument);
        string sometimes = $$"""{ "id": "on", "type": "cond.compare", "params": { "op": "lt", "value": "{{(low + high) / 2m}}" } }""";

        Run run = Go(Document(low, high, enable: sometimes));

        Assert.Contains(run.Strategy.Decisions, d => d.NodeId == Node && d.Kind == "cancel" && d.Message.Contains("the grid was switched off", StringComparison.Ordinal));

        // While it is off it holds nothing: no level, and nothing of its own resting on the venue.
        if (!(bool)run.Strategy.LastValues[Node + ":armed"]!)
        {
            Assert.Equal(0m, (decimal)run.Strategy.LastValues[Node + ":levels"]!);
            Assert.DoesNotContain(run.Cache.OrdersOpen(instrumentId: run.Instrument.Id, strategyId: Doc),
                o => o.Tags.Any(t => t.StartsWith("grid:", StringComparison.Ordinal)));
        }
    }

    [Fact]
    public void What_is_resting_when_the_strategy_stops_is_taken_back()
    {
        Instrument instrument = Fixtures.BtcUsdt();
        (decimal low, decimal high) = Range(instrument);

        Run run = Go(Document(low, high));

        Assert.True((bool)run.Strategy.LastValues[Node + ":armed"]!, "the grid has to be standing when the run ends for this to mean anything");
        StrategyEvent cancel = Assert.Single(run.Strategy.Decisions, d => d.NodeId == Node && d.Kind == "cancel" && d.Message.Contains("the strategy is stopping", StringComparison.Ordinal));
        Assert.DoesNotContain("0 grid orders", cancel.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(run.Cache.OrdersOpen(instrumentId: run.Instrument.Id, strategyId: Doc),
            o => o.Tags.Any(t => t.StartsWith("grid:", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_grid_picks_up_the_count_of_what_it_has_turned_over()
    {
        Instrument instrument = Fixtures.BtcUsdt();
        (decimal low, decimal high) = Range(instrument);
        Run first = Go(Document(low, high));
        decimal before = (decimal)first.Strategy.LastValues[Node + ":roundTrips"]!;
        Assert.True(before > 0m);

        // The same document started again over the state the first run saved, the way a live node restarts over its
        // own store. Stopping took the resting orders back, so the grid arms itself again - but it carries on counting
        // from what it had turned over, rather than reporting a record of nothing.
        DocumentStrategy resumed = Fixtures.RunWithSavedState(DocumentJson.Deserialize(Document(low, high)), first.Strategy.Save(), bars: 40);

        Assert.True((decimal)resumed.LastValues[Node + ":roundTrips"]! >= before, "the round trips of the levels before the restart were forgotten");
        Assert.Contains(resumed.Decisions, d => d.NodeId == Node && d.Message.Contains("grid armed", StringComparison.Ordinal));
    }
    [Fact]
    public void A_document_whose_only_action_is_a_grid_is_a_whole_strategy()
    {
        // The grid places what opens its levels and what closes them, so a document holding one is neither a strategy
        // that never trades nor a position nothing gets out of.
        (decimal low, decimal high) = Range(Fixtures.BtcUsdt());

        ValidationReport report = new DocumentValidator().Validate(DocumentJson.Deserialize(Document(low, high)));

        Assert.True(report.IsValid);
        Assert.DoesNotContain(report.Findings, f => f.Code is "NO_ENTRY" or "NO_EXIT");
    }

}
