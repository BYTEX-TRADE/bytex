using Bytex.Core.Adapters;
using Bytex.Core.Kernel;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Tests.Support;
using Bytex.Core.Timing;
using Bytex.Core.Trading;
using CoreKernel = Bytex.Core.Kernel.Kernel;

namespace Bytex.Core.Tests.Kernel;

// Why: R1.1/R1.2 - the kernel wires every component onto one bus and one clock. With a test clock the whole
// system must be reproducible: the same inputs give the same events in the same order, run after run.
public class KernelTests
{
    /// <summary>Venue stand-in that accepts everything at once and fills market orders at the cached bid/ask.</summary>
    private sealed class AutoFillClient : ExecutionClientBase
    {
        private int _venueOrders;
        private int _trades;

        public AutoFillClient(KernelServices services)
            : base(new ClientId("BINANCE"), TestIds.Binance, TestIds.BinanceAccount, AccountType.Cash, null, OmsType.Netting, services)
        {
        }

        public override Task SubmitOrderAsync(SubmitOrder command, CancellationToken ct)
        {
            Order order = command.Order;
            VenueOrderId venueOrderId = new($"V-{++_venueOrders}");
            GenerateOrderSubmitted(order.StrategyId, order.InstrumentId, order.ClientOrderId, Clock.Timestamp);
            GenerateOrderAccepted(order.StrategyId, order.InstrumentId, order.ClientOrderId, venueOrderId, Clock.Timestamp);
            if (order.Type == OrderType.Market)
            {
                Price price = Services.Cache.Price(order.InstrumentId, order.IsBuy ? PriceType.Ask : PriceType.Bid)!.Value;
                GenerateOrderFilled(order.StrategyId, order.InstrumentId, order.ClientOrderId, venueOrderId, null, new TradeId($"T-{++_trades}"), order.Side, order.Type,
                    order.Quantity, price, Currencies.USDT, Money.Parse("0.10 USDT"), LiquiditySide.Taker, Clock.Timestamp);
            }

            return Task.CompletedTask;
        }

        public override Task ModifyOrderAsync(ModifyOrder command, CancellationToken ct) => Task.CompletedTask;

        public override Task CancelOrderAsync(CancelOrder command, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>Trades on every second quote (alternating buy/sell) and places a resting limit order on a 3-second timer.</summary>
    private sealed class ScriptedStrategy : Strategy
    {
        private int _ticks;

        public ScriptedStrategy()
            : base(new StrategyConfig { StrategyId = TestIds.Strategy })
        {
        }

        protected override void OnStart()
        {
            SubscribeQuoteTicks(TestIds.BtcUsdt);
            SetTimer("resting-bid", TimeSpan.FromSeconds(3));
        }

        protected override void OnQuoteTick(QuoteTick tick)
        {
            _ticks++;
            if (_ticks % 2 == 0)
            {
                SubmitOrder(OrderFactory.Market(TestIds.BtcUsdt, _ticks % 4 == 0 ? OrderSide.Sell : OrderSide.Buy, Quantity.Parse("1.000")));
            }
        }

        protected override void OnTimeEvent(TimeEvent e) =>
            SubmitOrder(OrderFactory.Limit(TestIds.BtcUsdt, OrderSide.Buy, Quantity.Parse("0.500"), Price.Parse("100.00")));
    }

    private static string Describe(object message) => message switch
    {
        OrderFilled f => $"OrderFilled {f.ClientOrderId} {f.TradeId} {f.OrderSide} {f.LastQty}@{f.LastPx} pos={f.PositionId} ts={f.TsEvent.ToSeconds()}",
        OrderEvent e => $"{e.GetType().Name} {e.ClientOrderId} venue={e.VenueOrderId} ts={e.TsEvent.ToSeconds()}",
        PositionEvent p => $"{p.GetType().Name} {p.PositionId} {p.Side} {p.Quantity} realized={p.RealizedPnl} ts={p.TsEvent.ToSeconds()}",
        QuoteTick q => $"QuoteTick {q.Bid}/{q.Ask} ts={q.TsEvent.ToSeconds()}",
        _ => message.GetType().Name,
    };

    /// <summary>Drives eight one-second steps the way the backtest loop is documented: due timers first, then the data element.</summary>
    private static (List<string> Log, CoreKernel Kernel) RunScript()
    {
        TestClock clock = new(TestOrders.T0);
        CoreKernel kernel = new(new KernelConfig { TraderId = TestIds.Trader, InstanceId = "determinism" }, clock);
        kernel.Cache.AddInstrument(TestInstruments.BtcUsdt());
        kernel.AddExecutionClient(new AutoFillClient(kernel.Services));
        kernel.Trader.AddStrategy(new ScriptedStrategy());
        List<string> log = new();
        kernel.MessageBus.Subscribe("*", m => log.Add(Describe(m)), priority: 100);
        kernel.Start();

        for (int i = 1; i <= 8; i++)
        {
            UnixNanos ts = TestOrders.T0 + TimeSpan.FromSeconds(i);
            clock.AdvanceAndRun(ts);
            decimal bid = 50_000m + (i * 10m);
            kernel.DataEngine.Process(new QuoteTick(TestIds.BtcUsdt, new Price(bid, 2), new Price(bid + 1m, 2), Quantity.Parse("1.000"), Quantity.Parse("1.000"), ts, ts));
        }

        return (log, kernel);
    }

    [Fact]
    public void Kernel_builds_every_component_ready_on_one_bus_and_clock()
    {
        TestClock clock = new(TestOrders.T0);
        using CoreKernel kernel = new(new KernelConfig { TraderId = TestIds.Trader, Environment = TradingEnvironment.Backtest }, clock);

        Assert.Equal(TradingEnvironment.Backtest, kernel.Environment);
        Assert.Equal(TestIds.Trader, kernel.TraderId);
        Assert.Same(clock, kernel.Clock);
        Assert.Same(clock, kernel.Services.Clock);
        Assert.Same(kernel.Cache, kernel.Services.Cache);
        Assert.Same(kernel.MessageBus, kernel.Services.MessageBus);
        Assert.All(
            new[] { kernel.DataEngine.State, kernel.RiskEngine.State, kernel.ExecutionEngine.State, kernel.Portfolio.State, kernel.Trader.State },
            state => Assert.Equal(ComponentState.Ready, state));
        Assert.All(
            new[]
            {
                Endpoints.DataEngineExecute, Endpoints.DataEngineProcess, Endpoints.DataEngineRequest, Endpoints.DataEngineResponse,
                Endpoints.RiskEngineExecute, Endpoints.ExecutionEngineExecute, Endpoints.ExecutionEngineProcess, Endpoints.PortfolioUpdateAccount,
            },
            endpoint => Assert.True(kernel.MessageBus.IsRegistered(endpoint), endpoint));
        Assert.False(kernel.IsRunning);
    }

    [Fact]
    public void Start_and_stop_drive_engines_clients_and_strategies_together()
    {
        using KernelHarness h = new();
        ProbeStrategy strategy = h.AddStrategy();

        h.Kernel.Start();
        ComponentState[] running = [h.Kernel.DataEngine.State, h.Kernel.RiskEngine.State, h.Kernel.ExecutionEngine.State, h.Kernel.Portfolio.State, h.Kernel.Trader.State, strategy.State, h.Client.State];
        h.Kernel.Stop();
        ComponentState[] stopped = [h.Kernel.DataEngine.State, h.Kernel.RiskEngine.State, h.Kernel.ExecutionEngine.State, h.Kernel.Portfolio.State, h.Kernel.Trader.State, strategy.State, h.Client.State];

        Assert.All(running, s => Assert.Equal(ComponentState.Running, s));
        Assert.All(stopped, s => Assert.Equal(ComponentState.Stopped, s));
    }

    [Fact]
    public void Stop_saves_actor_state_and_the_next_start_loads_it()
    {
        using KernelHarness h = new();
        ProbeActor actor = new(new ActorConfig { ActorId = new ActorId("Stateful-001") }) { StateToSave = new Dictionary<string, byte[]> { ["k"] = [7] } };
        h.Kernel.Trader.AddActor(actor);
        h.Kernel.Start();

        h.Kernel.Stop();
        h.Kernel.Start();

        Assert.Equal(new byte[] { 7 }, actor.LoadedState!["k"]);
    }

    [Fact]
    public void Stop_cancels_every_timer_on_the_kernel_clock()
    {
        using KernelHarness h = new();
        ProbeActor actor = new();
        actor.StartAction = () => actor.DoSetTimer("pulse", TimeSpan.FromSeconds(1));
        h.Kernel.Trader.AddActor(actor);
        h.Kernel.Start();
        int whileRunning = h.Clock.TimerCount;

        h.Kernel.Stop();

        Assert.Equal(1, whileRunning);
        Assert.Equal(0, h.Clock.TimerCount);
    }

    [Fact]
    public void Reset_clears_trading_state_but_keeps_instruments_clients_and_strategies()
    {
        using KernelHarness h = new();
        ProbeStrategy strategy = h.StartWithStrategy();
        MarketOrder order = strategy.Factory.Market(TestIds.BtcUsdt, OrderSide.Buy, Quantity.Parse("1.000"));
        strategy.DoSubmit(order);
        h.Accept(order);
        h.Client.EmitFilled(order, "T-1", "1.000", "50000.00");

        h.Kernel.Reset();

        Assert.False(h.Kernel.IsRunning);
        Assert.Empty(h.Cache.Orders());
        Assert.Empty(h.Cache.Positions());
        Assert.NotNull(h.Cache.Instrument(TestIds.BtcUsdt));
        Assert.Equal(0, h.Kernel.ExecutionEngine.EventCount);
        Assert.Equal(0, h.Kernel.RiskEngine.CommandCount);
        Assert.Same(strategy, Assert.Single(h.Kernel.Trader.Strategies));
        Assert.Contains(h.Client.ClientId, h.Kernel.ExecutionEngine.RegisteredClients);
        Assert.Equal(ComponentState.Ready, strategy.State);

        h.Kernel.Start();
        Assert.True(h.Kernel.IsRunning);
    }

    [Fact]
    public void Dispose_stops_a_running_kernel_disposes_everything_and_can_be_repeated()
    {
        KernelHarness h = new();
        ProbeStrategy strategy = h.StartWithStrategy();

        h.Kernel.Dispose();
        h.Kernel.Dispose();

        Assert.Equal(ComponentState.Disposed, strategy.State);
        Assert.Equal(ComponentState.Disposed, h.Client.State);
        Assert.All(
            new[] { h.Kernel.DataEngine.State, h.Kernel.RiskEngine.State, h.Kernel.ExecutionEngine.State, h.Kernel.Portfolio.State, h.Kernel.Trader.State },
            state => Assert.Equal(ComponentState.Disposed, state));
        Assert.Contains("OnStop", strategy.Calls);
    }

    [Fact]
    public void Scripted_run_produces_the_expected_orders_with_timers_ahead_of_data_at_the_same_timestamp()
    {
        (List<string> log, CoreKernel kernel) = RunScript();
        using CoreKernel disposeAtEnd = kernel;
        Assert.NotEmpty(log);

        // Quotes 2, 4, 6, 8 trigger market orders; the 3-second timer fires at t=3 and t=6. At t=6 the timer's
        // limit order (count 4) must come before the quote's market order (count 5).
        IReadOnlyList<Order> orders = kernel.Cache.Orders();
        Assert.Equal(
            [
                "O-20240101-000002-001-001-1 Market Buy",
                "O-20240101-000003-001-001-2 Limit Buy",
                "O-20240101-000004-001-001-3 Market Sell",
                "O-20240101-000006-001-001-4 Limit Buy",
                "O-20240101-000006-001-001-5 Market Buy",
                "O-20240101-000008-001-001-6 Market Sell",
            ],
            orders.Select(o => $"{o.ClientOrderId} {o.Type} {o.Side}").Order(StringComparer.Ordinal));

        // Bought at ask 50,021 (t=2), sold at bid 50,040 (t=4): 19 gross - 0.10 - 0.10 commission = 18.80.
        Assert.Equal(["BTCUSDT.BINANCE-S-001", "BTCUSDT.BINANCE-S-001-2"], kernel.Cache.PositionsClosed().Select(p => p.Id.Value));
        Assert.Equal(Money.Parse("18.80 USDT"), kernel.Cache.PositionsClosed()[0].RealizedPnl);
        // Second round trip: bought at 50,061 (t=6), sold at 50,080 (t=8): the same 18.80; total 37.60.
        Assert.Equal(Money.Parse("37.60 USDT"), kernel.Portfolio.RealizedPnl(TestIds.BtcUsdt));
        Assert.True(kernel.Portfolio.IsCompletelyFlat());
    }

    [Fact]
    public void Two_runs_of_the_same_script_publish_identical_messages_in_identical_order()
    {
        (List<string> first, CoreKernel k1) = RunScript();
        (List<string> second, CoreKernel k2) = RunScript();
        k1.Dispose();
        k2.Dispose();

        Assert.Equal(first, second);
        // Guard against a vacuous comparison: 8 quotes, 6 orders (Submitted + Accepted each), 4 fills, 4 position events.
        Assert.Equal(8, first.Count(l => l.StartsWith("QuoteTick", StringComparison.Ordinal)));
        Assert.Equal(6, first.Count(l => l.StartsWith("OrderAccepted", StringComparison.Ordinal)));
        Assert.Equal(4, first.Count(l => l.StartsWith("OrderFilled", StringComparison.Ordinal)));
        Assert.Equal(4, first.Count(l => l.StartsWith("Position", StringComparison.Ordinal)));
        Assert.Equal("QuoteTick 50010.00/50011.00 ts=1704067201", first[0]);
    }
}
