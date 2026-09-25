using Bytex.Backtest;
using Bytex.Core.Model;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Positions;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Trading;
using Bytex.Documents.Runtime;
using Bytex.Documents.Schema;
using Bytex.Documents.Validation;
using Xunit;

namespace Bytex.Documents.Tests;

// Why: 0.4.0 shipped an execution algorithm and no way to ask for one, so a size that would move the market had to go
// out in one piece whatever a strategy wanted. What is pinned here is the whole way from a document saying "work this
// as TWAP" to the pieces arriving at the venue: that the algorithm the document names is running without its host
// knowing anything about it, that the pieces add up to the order and no more, that the node reports the instruction as
// the one order it is - working until the last piece, filled at what the pieces averaged - and that a document which
// says nothing about any of it behaves exactly as it did before the parameter existed.
public sealed class WorkedOrderTests
{
    private const decimal Balance = 100_000m;
    private const decimal Notional = 5_000m;

    /// <summary>
    /// One entry, gated on the close being above a three-bar average so the gate turns over more than once in a run,
    /// with a counter on the order's filled output: a node that reported every piece as a fill would count five where
    /// the strategy asked for one order.
    /// </summary>
    private static string Document(string work, bool onlyWhenFlat = true, string orderType = "market", int cancelAfterBars = 0, string? sizing = null) => $$"""
        {
          "schemaVersion": "1.0",
          "id": "worked-order",
          "name": "Work one entry",
          "instruments": [ { "ref": "primary", "instrumentId": "BTCUSDT.SIM" } ],
          "barTypes": [ { "ref": "main", "instrument": "primary", "step": 1, "aggregation": "minute", "priceType": "last", "source": "external" } ],
          "nodes": [
            { "id": "bars", "type": "data.bars", "params": { "barType": "main" } },
            { "id": "avg", "type": "ind.ema", "params": { "period": 3 } },
            { "id": "gate", "type": "cond.compare", "params": { "op": "gt" } },
            { "id": "buy", "type": "act.order", "params": {
                "side": "buy", "orderType": "{{orderType}}", "onlyWhenFlat": {{(onlyWhenFlat ? "true" : "false")}},
                "cancelAfterBars": {{cancelAfterBars}},
                "sizing": {{sizing ?? $$$"""{ "mode": "notional", "value": "{{{Notional}}}" }"""}}{{work}} } },
            { "id": "fills", "type": "flow.counter", "params": { "target": 1 } }
          ],
          "edges": [
            { "from": "bars:bars", "to": "avg:bars" },
            { "from": "bars:close", "to": "gate:a" },
            { "from": "avg:value", "to": "gate:b" },
            { "from": "gate:out", "to": "buy:trigger" },
            { "from": "buy:filled", "to": "fills:increment" }
          ]
        }
        """;

    /// <summary>The work parameter as a document writes it, ready to append to the order node's other parameters.</summary>
    /// <summary>Everything the account can pay for at once, which is what makes a fee the thing that stops a piece.</summary>
    private const string FullBalance = """{ "mode": "percentOfBalance", "value": "100" }""";

    private static string Twap(decimal horizonMinutes = 5m, decimal intervalMinutes = 1m, string algorithm = OrderWork.Twap) =>
        $$""", "{{OrderWork.Param}}": { "{{OrderWork.Algorithm}}": "{{algorithm}}", "{{OrderWork.HorizonMinutes}}": "{{horizonMinutes}}", "{{OrderWork.IntervalMinutes}}": "{{intervalMinutes}}" }""";

    private sealed class Run(BacktestEngine engine, DocumentStrategy strategy) : IDisposable
    {
        public BacktestEngine Engine { get; } = engine;

        public DocumentStrategy Strategy { get; } = strategy;

        public void Dispose() => Engine.Dispose();

        public IReadOnlyList<Order> Orders => Engine.Kernel.Cache.Orders();

        /// <summary>The instructions: orders a node wrote, as against the pieces an algorithm spawned from them.</summary>
        public IReadOnlyList<Order> Instructions => Orders.Where(o => o.ExecSpawnId is null).ToList();

        public IReadOnlyList<Order> Pieces(Order instruction) => Engine.Kernel.Cache.OrdersForExecSpawn(instruction.ClientOrderId);

        public object? Value(string port) => Strategy.LastValues.GetValueOrDefault("buy:" + port);

        public decimal Fills => Strategy.LastValues.GetValueOrDefault("fills:count") is decimal count ? count : 0m;
    }

    /// <summary>
    /// Two order nodes on one bar type: one worked in pieces, one sending a plain market order on the same trigger.
    /// A node that took any fill for one of its own pieces would be reading the other node's orders.
    /// </summary>
    private static string TwoNodes() => $$"""
        {
          "schemaVersion": "1.0",
          "id": "worked-order-two",
          "name": "Work one entry beside another",
          "instruments": [ { "ref": "primary", "instrumentId": "BTCUSDT.SIM" } ],
          "barTypes": [ { "ref": "main", "instrument": "primary", "step": 1, "aggregation": "minute", "priceType": "last", "source": "external" } ],
          "nodes": [
            { "id": "bars", "type": "data.bars", "params": { "barType": "main" } },
            { "id": "avg", "type": "ind.ema", "params": { "period": 3 } },
            { "id": "gate", "type": "cond.compare", "params": { "op": "gt" } },
            { "id": "buy", "type": "act.order", "params": {
                "side": "buy", "orderType": "market", "onlyWhenFlat": false,
                "sizing": { "mode": "notional", "value": "{{Notional}}" }{{Twap()}} } },
            { "id": "other", "type": "act.order", "params": {
                "side": "buy", "orderType": "market", "onlyWhenFlat": false,
                "sizing": { "mode": "notional", "value": "{{Notional}}" } } },
            { "id": "fills", "type": "flow.counter", "params": { "target": 1 } },
            { "id": "otherFills", "type": "flow.counter", "params": { "target": 1 } }
          ],
          "edges": [
            { "from": "bars:bars", "to": "avg:bars" },
            { "from": "bars:close", "to": "gate:a" },
            { "from": "avg:value", "to": "gate:b" },
            { "from": "gate:out", "to": "buy:trigger" },
            { "from": "gate:out", "to": "other:trigger" },
            { "from": "buy:filled", "to": "fills:increment" },
            { "from": "other:filled", "to": "otherFills:increment" }
          ]
        }
        """;

    private static Run Backtest(string document, int bars = 40, decimal feePerFill = 0m)
    {
        StrategyDocument parsed = DocumentJson.Deserialize(document);
        ValidationReport report = new DocumentValidator().Validate(parsed);
        Assert.True(report.IsValid, string.Join("; ", report.Findings.Select(x => x.Code + " " + x.Message)));

        Instrument instrument = Fixtures.BtcUsdt();
        BarType barType = Fixtures.MinuteBars(instrument);
        BacktestEngine engine = new(new BacktestEngineConfig { RunId = "worked-order" });
        engine.AddInstrument(instrument);
        engine.AddVenue(new SimulatedVenueConfig
        {
            Venue = Fixtures.Sim,
            AccountType = AccountType.Cash,
            StartingBalances = [new Money(Balance, Currencies.USDT)],
            FeeModel = new FixedFeeModel(new Money(feePerFill, Currencies.USDT)),
        });
        engine.AddData(Fixtures.RandomWalkBars(instrument, barType, bars).Cast<IData>());
        DocumentStrategy strategy = new(new DocumentStrategyConfig { Document = parsed, StrategyId = new StrategyId("Doc-001") });
        engine.AddStrategy(strategy);
        engine.Run();
        return new Run(engine, strategy);
    }

    [Fact]
    public void A_worked_order_reaches_the_venue_in_pieces_that_add_up_to_it()
    {
        using Run run = Backtest(Document(Twap()));

        Order instruction = Assert.Single(run.Instructions);
        Assert.Equal(TwapExecAlgorithm.DefaultExecAlgorithmId, instruction.ExecAlgorithmId);
        IReadOnlyList<Order> pieces = run.Pieces(instruction);

        // Five minutes, a piece a minute: five pieces, no more, and between them the whole order.
        Assert.Equal(5, pieces.Count);
        Assert.Equal(instruction.Quantity.Value, pieces.Sum(p => p.Quantity.Value));
        Assert.All(pieces, p => Assert.Equal(instruction.Side, p.Side));
        Assert.All(pieces, p => Assert.Contains("node:buy", p.Tags!));
        Assert.Equal(instruction.Quantity.Value, Assert.Single(run.Engine.Kernel.Cache.Positions()).PeakQuantity.Value);
    }

    [Fact]
    public void The_pace_the_document_asked_for_is_the_pace_the_order_carries()
    {
        using Run run = Backtest(Document(Twap(horizonMinutes: 4m, intervalMinutes: 2m)));

        Order instruction = Assert.Single(run.Instructions);
        Assert.Equal("00:04:00", instruction.ExecAlgorithmParams![TwapExecAlgorithm.HorizonParam]);
        Assert.Equal("00:02:00", instruction.ExecAlgorithmParams[TwapExecAlgorithm.IntervalParam]);
        Assert.Equal(2, run.Pieces(instruction).Count);
    }

    [Fact]
    public void The_algorithm_the_document_names_is_running_without_the_host_registering_anything()
    {
        // The point of the parameter: a user says "work this as TWAP" and nobody has to configure a host. An
        // order naming an algorithm nobody registered is denied, so a denial here is the feature not working at all.
        using Run run = Backtest(Document(Twap()));

        Assert.Equal(TwapExecAlgorithm.DefaultExecAlgorithmId, Assert.Single(run.Engine.Kernel.Trader.ExecAlgorithms).ExecAlgorithmId);
        Assert.All(run.Orders, o => Assert.NotEqual(OrderStatus.Denied, o.Status));
    }

    [Fact]
    public void A_document_that_says_nothing_about_working_sends_one_order_as_it_always_did()
    {
        using Run silent = Backtest(Document(string.Empty));
        using Run asked = Backtest(Document(Twap(algorithm: OrderWork.None)));

        foreach (Run run in new[] { silent, asked })
        {
            Order instruction = Assert.Single(run.Instructions);
            Assert.Null(instruction.ExecAlgorithmId);
            Assert.Empty(run.Pieces(instruction));
            Assert.Empty(run.Engine.Kernel.Trader.ExecAlgorithms);
            Assert.Equal(1m, run.Fills);
        }
    }

    [Fact]
    public void The_node_reports_the_instruction_as_one_order_filled_at_what_the_pieces_averaged()
    {
        using Run run = Backtest(Document(Twap()));

        // One fill, not five: the order the strategy asked for is one order, and anything wired to its filled output
        // would otherwise fire on every piece.
        Assert.Equal(1m, run.Fills);
        Position position = Assert.Single(run.Engine.Kernel.Cache.Positions());
        decimal reported = Assert.IsType<decimal>(run.Value("fillPrice"));
        Assert.Equal(position.AvgPxOpen, reported, 2);

        // And the pieces were not all one price: an average of one price would prove nothing.
        Assert.True(run.Pieces(Assert.Single(run.Instructions)).Count > 1);
        Assert.False(Assert.IsType<bool>(run.Value("working")));
    }

    [Fact]
    public void The_node_says_it_is_working_until_the_last_piece_is_done()
    {
        // Ten minutes at a piece a minute, on forty one-minute bars: the run ends with the order still being worked, so
        // the node has to say so. A node that took the instruction for its own state would call it closed at once -
        // the instruction never reaches a venue - and place another order on the next trigger.
        using Run run = Backtest(Document(Twap(horizonMinutes: 10m, intervalMinutes: 1m), onlyWhenFlat: false), bars: 6);

        Order instruction = Assert.Single(run.Instructions);
        Assert.InRange(run.Pieces(instruction).Count, 1, 9);
        Assert.True(Assert.IsType<bool>(run.Value("working")), "the node says nothing is working while pieces are still going out");
        Assert.Equal(0m, run.Fills);
    }

    [Fact]
    public void A_worked_order_does_not_jam_the_node_for_the_rest_of_the_run()
    {
        // The other half of the same rule: once the pieces are done the node is free again. Without the deadline and
        // the count, an instruction that never fills and never closes would look like an order still working forever.
        using Run run = Backtest(Document(Twap(horizonMinutes: 3m, intervalMinutes: 1m), onlyWhenFlat: false));

        Assert.True(run.Instructions.Count > 1, $"only {run.Instructions.Count} order went out in forty bars");

        // Every order the run did not end in the middle of was worked in full: three minutes, a piece a minute.
        Assert.All(run.Instructions.SkipLast(1), i => Assert.Equal(3, run.Pieces(i).Count));
        Assert.InRange(run.Pieces(run.Instructions[^1]).Count, 1, 3);
        Assert.Equal(run.Instructions.Count(i => run.Pieces(i).Count == 3), (int)run.Fills);
    }

    [Theory]
    [InlineData("stopMarket")]
    [InlineData("stopLimit")]
    [InlineData("marketIfTouched")]
    [InlineData("limitIfTouched")]
    public void An_order_type_no_algorithm_can_work_is_refused_before_the_run(string orderType)
    {
        StrategyDocument document = DocumentJson.Deserialize(Document(Twap(), orderType: orderType));
        ValidationReport report = new DocumentValidator().Validate(document);

        Finding finding = Assert.Single(report.Blocks, b => b.Code == Codes.OrderCannotBeWorked);
        Assert.Contains(orderType, finding.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => new DocumentStrategy(new DocumentStrategyConfig { Document = document }));
    }

    [Fact]
    public void Working_an_order_and_cancelling_it_after_bars_is_refused_before_the_run()
    {
        // Two bounds on the same order, and only one of them can hold: the instruction is not at a venue, so there is
        // nothing there to cancel, and a document that said both would silently get the horizon.
        ValidationReport report = new DocumentValidator().Validate(DocumentJson.Deserialize(Document(Twap(), cancelAfterBars: 5)));

        Assert.Single(report.Blocks, b => b.Code == Codes.OrderCannotBeWorked && b.Message.Contains("cancels it after bars", StringComparison.Ordinal));
    }

    [Fact]
    public void A_limit_order_can_be_worked_too()
    {
        // The pieces are limit orders at the parent's price: the pace is the algorithm's business, what it is willing
        // to pay stays the order's own.
        using Run run = Backtest(Document(Twap(), orderType: "limit"));

        Order instruction = Assert.Single(run.Instructions);
        IReadOnlyList<Order> pieces = run.Pieces(instruction);
        Assert.Equal(5, pieces.Count);
        Assert.All(pieces, p => Assert.Equal(OrderType.Limit, p.Type));
        Assert.All(pieces, p => Assert.Equal(instruction.Price, p.Price));
    }

    [Fact]
    public void The_run_log_says_how_the_order_is_being_worked()
    {
        using Run run = Backtest(Document(Twap(horizonMinutes: 5m, intervalMinutes: 1m)));

        StrategyEvent placed = Assert.Single(run.Strategy.Decisions, d => d.Kind == "order");
        Assert.Contains("worked as TWAP", placed.Message, StringComparison.Ordinal);
        Assert.Equal(OrderWork.Twap, placed.Values[OrderWork.Param]);
        Assert.Equal("5", placed.Values[OrderWork.HorizonMinutes]);
        Assert.Equal("1", placed.Values[OrderWork.IntervalMinutes]);
    }

    [Fact]
    public void A_node_following_its_pieces_does_not_count_another_nodes_fills()
    {
        // Five bars for five pieces that start on the third: the worked node must still say it is working two bars
        // later, and the other node's fill - a market order of the same size, on the same trigger - must not have been
        // taken for one of its pieces.
        using Run run = Backtest(TwoNodes(), bars: 5);

        Assert.Equal(1m, run.Strategy.LastValues.GetValueOrDefault("otherFills:count"));
        Assert.True(Assert.IsType<bool>(run.Value("working")), "another node's fill was taken for the pieces of this one");
        Assert.Equal(0m, run.Fills);
    }

    [Fact]
    public void The_order_is_filled_when_the_last_piece_is_in_and_not_when_the_pace_runs_out()
    {
        // Nine bars for five pieces: the order is done, and the deadline that would have freed the node anyway is
        // still ahead - so a fill reported here can only be the pieces adding up.
        using Run run = Backtest(Document(Twap(horizonMinutes: 5m, intervalMinutes: 1m)), bars: 7);

        Order instruction = Assert.Single(run.Instructions);
        Assert.True(
            run.Engine.Kernel.Clock.Timestamp < instruction.TsInit + TimeSpan.FromMinutes(6),
            "the run outlasted the order's deadline, so it proves nothing about what reported the fill");
        Assert.Equal(5, run.Pieces(instruction).Count);
        Assert.Equal(1m, run.Fills);
        Assert.False(Assert.IsType<bool>(run.Value("working")));
    }

    [Fact]
    public void A_worked_order_nothing_fills_is_let_go_when_the_pace_runs_out()
    {
        // Limit pieces five percent under the market: they rest there and nothing of the order is ever in. The node
        // has to let it go all the same - the instruction never fills and never closes, so nothing but the pace it was
        // given says it is over - and it is free to try again afterwards.
        string work = $$""", "offset": { "unit": "percent", "value": "5" }{{Twap(horizonMinutes: 3m, intervalMinutes: 1m)}}""";
        using Run run = Backtest(Document(work, onlyWhenFlat: false, orderType: "limit"));

        Assert.True(run.Instructions.Count > 1, $"the node placed {run.Instructions.Count} order in forty bars and then nothing");
        Assert.Equal(0m, run.Fills);
        Assert.Empty(run.Engine.Kernel.Cache.Positions());
        Assert.All(run.Pieces(run.Instructions[0]), p => Assert.Equal(0m, p.FilledQuantity.Value));
    }

    [Fact]
    public void An_order_the_pieces_could_not_finish_still_reports_what_it_got()
    {
        // The whole free balance, at a fee a fill, worked in three pieces: the fees mean the last piece cannot be paid
        // for, and the venue refuses it. Nothing more is coming, so the node reports the order as filled - for what
        // went in, at what that cost - rather than waiting forever on an instruction that is already over.
        using Run run = Backtest(Document(Twap(horizonMinutes: 3m, intervalMinutes: 1m), sizing: FullBalance), feePerFill: 500m);

        Order instruction = Assert.Single(run.Instructions);
        IReadOnlyList<Order> pieces = run.Pieces(instruction);
        Assert.Contains(pieces, p => p.Status == OrderStatus.Denied);
        Assert.Equal(1m, run.Fills);
        Assert.False(Assert.IsType<bool>(run.Value("working")));

        Position position = Assert.Single(run.Engine.Kernel.Cache.Positions());
        Assert.Equal(pieces.Sum(p => p.FilledQuantity.Value), position.PeakQuantity.Value);
        Assert.True(position.PeakQuantity.Value < instruction.Quantity.Value, "every piece went in after all");
    }

    [Fact]
    public void What_the_node_is_following_survives_a_restart()
    {
        // A host that restarts in the middle of a pace picks the same order back up: which instruction it is, how much
        // of it is in, what that came to, and when the algorithm will have finished with it. Without the last two the
        // node would report the wrong average and wait for a fill that has already happened.
        TrackedOrder before = new()
        {
            Id = new ClientOrderId("O-1"),
            SubmittedAt = 12,
            IsWorking = true,
            IsWorked = true,
            WorkedQuantity = 1m,
            FilledQuantity = 0.4m,
            FilledValue = 20_000m,
            FillPrice = 50_000m,
            WorkedUntil = new UnixNanos(1_700_000_000_000_000_000L),
            PositionId = new PositionId("P-1"),
        };

        TrackedOrder after = new();
        after.Load(before.Save());

        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.SubmittedAt, after.SubmittedAt);
        Assert.True(after.IsWorking);
        Assert.True(after.IsWorked);
        Assert.Equal(1m, after.WorkedQuantity);
        Assert.Equal(0.4m, after.FilledQuantity);
        Assert.Equal(20_000m, after.FilledValue);
        Assert.Equal(50_000m, after.FillPrice);
        Assert.Equal(before.WorkedUntil, after.WorkedUntil);
        Assert.Equal(before.PositionId, after.PositionId);

        // And an order that was never worked comes back as one: no deadline, nothing counted.
        TrackedOrder plain = new() { Id = new ClientOrderId("O-2"), IsWorking = true };
        TrackedOrder reloaded = new();
        reloaded.Load(plain.Save());
        Assert.False(reloaded.IsWorked);
        Assert.Null(reloaded.WorkedUntil);
        Assert.Equal(0m, reloaded.WorkedQuantity);
    }

    [Fact]
    public void The_catalog_offers_the_work_parameter_with_every_choice_explained()
    {
        Catalog.ParamSpec work = Assert.IsType<Catalog.ParamSpec>(Catalog.NodeCatalog.Default.Find("act.order")!.Param(OrderWork.Param));
        Catalog.ParamSpec algorithm = Assert.Single(work.Fields!, x => x.Name == OrderWork.Algorithm);

        Assert.Equal(OrderWork.None, algorithm.Default);
        Assert.Equal(new[] { OrderWork.None, OrderWork.Twap }, algorithm.Choices!.Select(c => c.Value).ToArray());
        Assert.All(algorithm.Choices!, c => Assert.False(string.IsNullOrWhiteSpace(c.Label) || string.IsNullOrWhiteSpace(c.Description), $"{c.Value} does not say what it does"));
        foreach (string field in new[] { OrderWork.HorizonMinutes, OrderWork.IntervalMinutes })
        {
            Catalog.ParamSpec spec = Assert.Single(work.Fields!, x => x.Name == field);
            Assert.Equal("minutes", spec.Unit);
            Assert.False(string.IsNullOrWhiteSpace(spec.Description), $"{field} does not say what it does");
        }
    }
}
