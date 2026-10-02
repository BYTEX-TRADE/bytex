using System.Text.Json;
using Bytex.Backtest;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Primitives;
using Bytex.Documents.Catalog;
using Bytex.Documents.Runtime;
using Bytex.Documents.Schema;
using Bytex.Documents.Validation;

namespace Bytex.Documents.Tests;

// Why (R13.7): a document was evaluated when a bar closed and at no other time, so everything it decided, it decided
// at a bar boundary. A stop that the market went through in the middle of a bar and came back from was not taken; a
// crossing that happened and reversed inside a bar was invisible - which is right for some strategies and wrong for
// the ones people were asking about.
//
// The thing to get right is not "evaluate more often". It is that evaluating more often must not quietly change what
// anything else means: a bar is still a bar, an indicator still moves once per bar, and a document that did not ask
// for any of this runs exactly as it did. Those three are what most of this file is about.
public sealed class TickEvaluationTests
{
    private const string Recorder = "test.recorder";

    /// <summary>What one frame looked like: what caused it, which bar it belonged to, and what it was handed.</summary>
    private sealed record Seen(FrameTrigger Trigger, long Index, decimal Last, decimal? Value);

    /// <summary>A node that records the frame rather than deciding anything, so a test can read the frames back.</summary>
    private sealed class RecorderNode : INodeEvaluator
    {
        private readonly List<Seen> _seen;

        public RecorderNode(List<Seen> seen) => _seen = seen;

        public void Evaluate(EvalContext ctx)
        {
            _seen.Add(new Seen(ctx.Trigger, ctx.Index, ctx.Last, ctx.Dec("value")));
            ctx.Set("out", false);
        }
    }

    private static NodeCatalog CatalogWith(List<Seen> seen)
    {
        NodeCatalog catalog = new(NodeCatalog.Default.Types);
        catalog.Register(new NodeTypeDescriptor
        {
            Type = Recorder,
            Kind = NodeKind.Custom,
            DisplayName = "Recorder",
            FaceTemplate = "Records the frame",
            Inputs = [new PortSpec("value", ValueKind.Series)],
            Outputs = [new PortSpec("out", ValueKind.Bool)],
            Factory = _ => new RecorderNode(seen),
        });

        return catalog;
    }

    // ----- the data -----

    private static CurrencyPair Instrument() => Fixtures.BtcUsdt();

    private static CandleSeries Bars(Instrument instrument) => Fixtures.MinuteBars(instrument);

    /// <summary>2025-01-01T00:00:00Z, and one minute per bar.</summary>
    private static readonly UnixNanos _start = UnixNanos.FromDateTimeOffset(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private const long Minute = 60_000_000_000L;

    private static Bar Bar(Instrument instrument, CandleSeries candleSeries, int minute, decimal open, decimal high, decimal low, decimal close)
    {
        UnixNanos ts = _start.AddNanos(minute * Minute);
        return new Bar(candleSeries, instrument.MakePrice(open), instrument.MakePrice(high), instrument.MakePrice(low), instrument.MakePrice(close),
            instrument.MakeQuantity(10m), ts, ts);
    }

    private static TradeTick Trade(Instrument instrument, int minute, long offsetNanos, decimal price)
    {
        UnixNanos ts = _start.AddNanos((minute * Minute) + offsetNanos);
        return new TradeTick(instrument.Id, instrument.MakePrice(price), instrument.MakeQuantity(1m), AggressorSide.Buyer,
            new TradeId("T-" + ts.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)), ts, ts);
    }

    private static QuoteTick Quote(Instrument instrument, int minute, long offsetNanos, decimal price)
    {
        UnixNanos ts = _start.AddNanos((minute * Minute) + offsetNanos);
        return new QuoteTick(instrument.Id, instrument.MakePrice(price), instrument.MakePrice(price), instrument.MakeQuantity(5m),
            instrument.MakeQuantity(5m), ts, ts);
    }

    /// <summary>
    /// A run over data given exactly as written. Deliberately not the random-walk fixture: every test here is about
    /// what happened between two bar closes, and a test that cannot say which price arrived when proves nothing.
    /// </summary>
    private static (BacktestResult Result, DocumentStrategy Strategy) Run(StrategyDocument document, IEnumerable<IData> data, NodeCatalog? catalog = null)
    {
        Instrument instrument = Instrument();
        using BacktestEngine engine = new(new BacktestEngineConfig { RunId = "ticks" });
        engine.AddInstrument(instrument);
        engine.AddVenue(new SimulatedVenueConfig
        {
            Venue = Fixtures.Sim,
            AccountType = AccountType.Cash,
            StartingBalances = [new Money(1_000_000m, Currencies.USDT), new Money(10m, Currencies.BTC)],
        });
        engine.AddData(data);
        DocumentStrategy strategy = new(new DocumentStrategyConfig { Document = document, StrategyId = new StrategyId("Doc-001") }, catalog ?? NodeCatalog.Default);
        engine.AddStrategy(strategy);
        engine.Run();
        return (engine.GetResult(), strategy);
    }

    /// <summary>Ten bars climbing gently, with three trades inside each one.</summary>
    private static List<IData> BarsAndTrades(Instrument instrument, CandleSeries candleSeries, int bars = 10)
    {
        List<IData> data = [];
        for (int i = 0; i < bars; i++)
        {
            decimal close = 100m + i;
            data.Add(Bar(instrument, candleSeries, i, close - 0.5m, close + 0.5m, close - 1m, close));
            for (int t = 1; t <= 3; t++)
            {
                data.Add(Trade(instrument, i, t * (Minute / 4), close + (t * 0.1m)));
            }
        }

        return data;
    }

    // ----- what a document that asked for nothing gets -----

    private static StrategyDocument Document(string? evaluation = null, string confirm = "barClose", bool withExit = false, bool recorder = false) => DocumentJson.Deserialize($$"""
        {
          "schemaVersion": "2.0",
          "id": "ticks",
          "name": "Ticks",
          {{(evaluation is null ? "" : $"\"evaluation\": \"{evaluation}\",")}}
          "instruments": [ { "ref": "primary", "marketKey": "bx-market:v2/SIM/BTCUSDT" } ],
          "candleSeriesDefinitions": [ { "ref": "main", "instrument": "primary", "step": 1, "aggregation": "minute", "priceType": "last", "source": "provider" } ],
          "nodes": [
            { "id": "bars", "type": "data.bars", "params": { "candleSeries": "main" } },
            { "id": "now", "type": "data.price" },
            { "id": "sma", "type": "ind.sma", "params": { "period": 2 } },
            {{(recorder ? $"{{ \"id\": \"rec\", \"type\": \"{Recorder}\" }}," : "")}}
            { "id": "cross", "type": "cond.cross", "params": { "direction": "above", "confirm": "{{confirm}}" } },
            { "id": "buy", "type": "act.order", "params": { "side": "buy", "orderType": "market", "sizing": { "mode": "fixed", "value": "0.01" }, "onlyWhenFlat": true } }
            {{(withExit ? """, { "id": "protect", "type": "risk.exit", "params": { "stop": { "anchor": "percent", "offset": { "value": "0.5" } }, "target": { "unit": "percent", "value": "50" }, "trail": { "enabled": false }, "timeStopBars": 0 } }""" : "")}}
          ],
          "edges": [
            { "from": "bars:bars", "to": "sma:bars" },
            {{(recorder ? "{ \"from\": \"sma:value\", \"to\": \"rec:value\" }," : "")}}
            { "from": "now:value", "to": "cross:value" },
            { "from": "sma:value", "to": "cross:reference" },
            { "from": "cross:out", "to": "buy:trigger" }
            {{(withExit ? """, { "from": "buy:position", "to": "protect:position" }""" : "")}}
          ],
          "repeat": { "enabled": true }
        }
        """);

    [Fact]
    public void A_document_that_asks_for_nothing_is_evaluated_at_the_bar_close_and_nowhere_else()
    {
        // The promise that matters most: tick data in the run changes nothing for a document that did not ask.
        Instrument instrument = Instrument();
        CandleSeries candleSeries = Bars(instrument);
        List<Seen> seen = [];

        (_, DocumentStrategy strategy) = Run(Document(recorder: true), BarsAndTrades(instrument, candleSeries), CatalogWith(seen));

        Assert.Equal(0, strategy.TickFrames);
        Assert.Equal(10, strategy.BarFrames);
        Assert.Equal(10, seen.Count);
        Assert.All(seen, s => Assert.Equal(FrameTrigger.BarClose, s.Trigger));
    }

    [Fact]
    public void A_document_evaluated_on_trades_gets_a_frame_for_each_one()
    {
        Instrument instrument = Instrument();
        CandleSeries candleSeries = Bars(instrument);
        List<Seen> seen = [];

        (_, DocumentStrategy strategy) = Run(Document(EvaluationModes.Trade, recorder: true), BarsAndTrades(instrument, candleSeries), CatalogWith(seen));

        // Ten bars, three trades after each one.
        Assert.Equal(10, strategy.BarFrames);
        Assert.Equal(30, strategy.TickFrames);
        Assert.Equal(40, seen.Count);
        Assert.Equal(30, seen.Count(s => s.Trigger == FrameTrigger.Trade));
    }

    [Fact]
    public void Nothing_is_evaluated_before_the_first_bar_has_closed()
    {
        // A frame before then would hand every node a bar of zeroes and half of them would act on it.
        Instrument instrument = Instrument();
        CandleSeries candleSeries = Bars(instrument);
        List<IData> data = [Trade(instrument, 0, Minute / 4, 100m), Trade(instrument, 0, Minute / 2, 101m)];

        (_, DocumentStrategy strategy) = Run(Document(EvaluationModes.Trade), data);

        Assert.Equal(0, strategy.BarFrames);
        Assert.Equal(0, strategy.TickFrames);
    }

    [Fact]
    public void The_bar_index_counts_bars_however_often_the_graph_is_walked()
    {
        // Everything in the catalog that counts - a time stop, a delay, bars held, cancel-after-bars - counts this.
        Instrument instrument = Instrument();
        CandleSeries candleSeries = Bars(instrument);
        List<Seen> seen = [];

        (_, DocumentStrategy strategy) = Run(Document(EvaluationModes.Trade, recorder: true), BarsAndTrades(instrument, candleSeries), CatalogWith(seen));

        Assert.Equal(strategy.BarFrames, strategy.BarIndex);
        Assert.Equal(10, seen.Select(s => s.Index).Distinct().Count());

        // And the tick frames inside a bar carry the index of the bar that closed before them, not one of their own.
        Seen[] insideTheSecondBar = [.. seen.Where(s => s.Index == 1)];
        Assert.Equal(FrameTrigger.BarClose, insideTheSecondBar[0].Trigger);
        Assert.All(insideTheSecondBar.Skip(1), s => Assert.Equal(FrameTrigger.Trade, s.Trigger));
    }

    [Fact]
    public void An_indicator_does_not_move_between_bars_but_the_price_does()
    {
        // The comparison tick evaluation exists to make: a live price against figures as of the last close. An
        // indicator recomputed per tick would be a different indicator with the same name.
        Instrument instrument = Instrument();
        CandleSeries candleSeries = Bars(instrument);
        List<Seen> seen = [];

        Run(Document(EvaluationModes.Trade, recorder: true), BarsAndTrades(instrument, candleSeries), CatalogWith(seen));

        foreach (IGrouping<long, Seen> bar in seen.GroupBy(s => s.Index))
        {
            Assert.Single(bar.Select(s => s.Value).Distinct());
        }

        // The price on the other hand is the tick's own, which is the whole point.
        Seen[] third = [.. seen.Where(s => s.Index == 2)];
        Assert.Equal(4, third.Length);
        Assert.Equal([102m, 102.1m, 102.2m, 102.3m], third.Select(s => s.Last).ToArray());
    }

    // ----- what changes when a decision may be taken inside a bar -----

    /// <summary>
    /// A level break: buy the moment the price is above 105, whatever the frame. Nothing here rests at the venue, so
    /// what the document is asked and when is the only thing that decides.
    /// </summary>
    private static StrategyDocument Breakout(string? evaluation) => DocumentJson.Deserialize($$"""
        {
          "schemaVersion": "2.0",
          "id": "breakout",
          "name": "Above a level",
          {{(evaluation is null ? "" : $"\"evaluation\": \"{evaluation}\",")}}
          "instruments": [ { "ref": "primary", "marketKey": "bx-market:v2/SIM/BTCUSDT" } ],
          "candleSeriesDefinitions": [ { "ref": "main", "instrument": "primary", "step": 1, "aggregation": "minute", "priceType": "last", "source": "provider" } ],
          "nodes": [
            { "id": "now", "type": "data.price" },
            { "id": "above", "type": "cond.compare", "params": { "op": "gt", "value": "105" } },
            { "id": "buy", "type": "act.order", "params": { "side": "buy", "orderType": "market", "sizing": { "mode": "fixed", "value": "0.01" }, "onlyWhenFlat": true } }
          ],
          "edges": [
            { "from": "now:value", "to": "above:a" },
            { "from": "above:out", "to": "buy:trigger" }
          ],
          "repeat": { "enabled": true }
        }
        """);

    [Fact]
    public void A_level_the_market_touched_inside_a_bar_is_acted_on_there()
    {
        // The payoff, and the honest version of it: a rule the DOCUMENT applies. One bar poked above 105 and closed
        // back below it - judged at the close that never happened, judged on ticks it happened at 106.
        Instrument instrument = Instrument();
        CandleSeries candleSeries = Bars(instrument);
        List<IData> data =
        [
            Bar(instrument, candleSeries, 0, 100m, 100m, 100m, 100m),
            Trade(instrument, 1, Minute / 4, 106m),
            Quote(instrument, 1, Minute / 4, 106m),
            Bar(instrument, candleSeries, 1, 100m, 106m, 100m, 101m),
            Bar(instrument, candleSeries, 2, 101m, 101m, 101m, 101m),
        ];

        (BacktestResult atClose, _) = Run(Breakout(null), data);
        (BacktestResult onTicks, DocumentStrategy tickStrategy) = Run(Breakout(EvaluationModes.Trade), data);

        Assert.Equal(0, atClose.TotalOrders);
        Assert.Equal(1, onTicks.TotalOrders);
        Assert.Contains(tickStrategy.Decisions, d => d.NodeId == "buy" && d.Kind == "order");
    }

    [Fact]
    public void A_protective_order_resting_at_the_venue_does_not_need_tick_evaluation()
    {
        // Worth pinning because it is the thing people expect tick evaluation to buy them and it is already theirs:
        // risk.exit does not watch the price, it PLACES a stop, and the venue walks the bar's own path. The same run
        // at both settings therefore does the same thing - and a document that only wanted an intra-bar stop does not
        // need to pay for tick frames to get one.
        Instrument instrument = Instrument();
        CandleSeries candleSeries = Bars(instrument);
        List<IData> data =
        [
            Bar(instrument, candleSeries, 0, 100m, 100m, 100m, 100m),
            Bar(instrument, candleSeries, 1, 100m, 100m, 99m, 99m),
            Bar(instrument, candleSeries, 2, 99m, 102m, 99m, 102m),

            // Down through a 0.5% stop on a fill at 102, and back up by the close.
            Trade(instrument, 3, Minute / 4, 100.0m),
            Quote(instrument, 3, Minute / 4, 100.0m),
            Bar(instrument, candleSeries, 3, 102m, 102m, 100m, 102m),
            Bar(instrument, candleSeries, 4, 102m, 102m, 102m, 102m),
        ];

        (BacktestResult atClose, DocumentStrategy closeStrategy) = Run(Document(withExit: true), data);
        (BacktestResult onTicks, DocumentStrategy tickStrategy) = Run(Document(EvaluationModes.Trade, withExit: true), data);

        Assert.True(closeStrategy.TickFrames == 0 && tickStrategy.TickFrames > 0, "the two runs were evaluated the same way");
        Assert.Equal(Fixtures.Fingerprint(atClose), Fixtures.Fingerprint(onTicks));
        Assert.True(atClose.TotalPositions > 0, "nothing was protected, so the comparison proves nothing");
    }

    [Fact]
    public void A_crossing_is_judged_when_the_document_says_it_is()
    {
        // cond.cross is the one built-in condition whose meaning depends on how often it is asked, which is why it
        // carries the parameter. Confirmed at the close it holds its answer while a bar is in progress.
        Instrument instrument = Instrument();
        CandleSeries candleSeries = Bars(instrument);
        List<IData> data =
        [
            Bar(instrument, candleSeries, 0, 100m, 100m, 100m, 100m),
            Bar(instrument, candleSeries, 1, 100m, 100m, 99m, 99m),

            // Inside this bar the price crosses back above the average and then falls under it again by the close.
            Trade(instrument, 2, Minute / 4, 105m),
            Trade(instrument, 2, Minute / 2, 98m),
            Bar(instrument, candleSeries, 2, 99m, 105m, 98m, 98m),
            Bar(instrument, candleSeries, 3, 98m, 98m, 98m, 98m),
        ];

        (BacktestResult confirmed, _) = Run(Document(EvaluationModes.Trade, confirm: "barClose", withExit: false), data);
        (BacktestResult instant, DocumentStrategy instantStrategy) = Run(Document(EvaluationModes.Trade, confirm: "instant", withExit: false), data);

        Assert.Equal(0, confirmed.TotalOrders);
        Assert.Equal(1, instant.TotalOrders);
        Assert.Contains(instantStrategy.Decisions, d => d.NodeId == "cross" && d.Kind == "condition");
    }

    [Fact]
    public void A_run_says_how_many_frames_each_trigger_cost_it()
    {
        // The choice is a cost as well as a behaviour, and a run that could not say what it paid would leave somebody
        // guessing at why a backtest took twenty minutes.
        Instrument instrument = Instrument();
        CandleSeries candleSeries = Bars(instrument);

        (_, DocumentStrategy strategy) = Run(Document(EvaluationModes.Either), BarsAndTrades(instrument, candleSeries));

        Assert.Equal(10, strategy.BarFrames);
        Assert.Equal(30, strategy.TickFrames);
    }

    // ----- what the document and the tools say -----

    [Fact]
    public void A_mode_this_engine_does_not_evaluate_on_is_refused()
    {
        ValidationReport report = new DocumentValidator().Validate(Document("everyMillisecond"));

        Finding finding = Assert.Single(report.Blocks, b => b.Code == Codes.EvaluationUnknown);

        Assert.Contains("barClose, quote, trade, either", finding.Fix!, StringComparison.Ordinal);
    }

    [Fact]
    public void Asking_for_tick_evaluation_is_a_warning_that_says_what_it_costs()
    {
        ValidationReport report = new DocumentValidator().Validate(Document(EvaluationModes.Quote));

        Finding finding = Assert.Single(report.Warnings, w => w.Code == Codes.EvaluationOnTicks);

        Assert.Contains("once per tick", finding.Message, StringComparison.Ordinal);
        Assert.Contains("Indicators still move only at the bar close", finding.Message, StringComparison.Ordinal);
        Assert.True(report.IsValid, "asking for it is not a fault");
    }

    [Fact]
    public void The_mode_is_part_of_the_document_and_of_the_schema()
    {
        StrategyDocument document = Document(EvaluationModes.Either);

        Assert.Equal(EvaluationModes.Either, DocumentJson.Deserialize(DocumentJson.Serialize(document)).Evaluation);
        Assert.Equal(EvaluationModes.BarClose, new StrategyDocument { Name = "x" }.Evaluation);

        using JsonDocument schema = JsonDocument.Parse(DocumentSchemaExporter.ExportJson());
        string[] allowed = [.. schema.RootElement.GetProperty("properties").GetProperty("evaluation").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()!)];

        Assert.Equal(EvaluationModes.All, allowed);
    }

    [Fact]
    public void A_document_evaluated_on_a_quote_is_evaluated_on_quotes_and_not_on_trades()
    {
        // A subscription that never happened looks exactly like a document being ignored, so this asserts the effect
        // rather than the plumbing: quotes in, frames out - and trades in, nothing, because that is a different ask.
        Instrument instrument = Instrument();
        CandleSeries candleSeries = Bars(instrument);
        List<IData> quotes =
        [
            Bar(instrument, candleSeries, 0, 100m, 100m, 100m, 100m),
            Quote(instrument, 1, Minute / 4, 100.5m),
            Quote(instrument, 1, Minute / 2, 100.7m),
            Bar(instrument, candleSeries, 1, 100m, 101m, 100m, 101m),
        ];

        (_, DocumentStrategy onQuotes) = Run(Document(EvaluationModes.Quote), quotes);
        (_, DocumentStrategy onTrades) = Run(Document(EvaluationModes.Trade), quotes);

        Assert.Equal(2, onQuotes.TickFrames);
        Assert.Equal(0, onTrades.TickFrames);
        Assert.Equal(2, onQuotes.BarFrames);
        Assert.Equal(2, onTrades.BarFrames);
    }
}
