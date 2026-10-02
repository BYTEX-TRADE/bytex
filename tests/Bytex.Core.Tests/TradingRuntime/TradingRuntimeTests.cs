using Bytex.Core.Adapters;
using Bytex.Core.TradingRuntime;
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
using CoreTradingRuntime = Bytex.Core.TradingRuntime.TradingRuntime;

namespace Bytex.Core.Tests.TradingRuntime;

// Why: R1.1/R1.2 - the tradingRuntime wires every component onto one bus and one clock. With a test clock the whole
// system must be reproducible: the same inputs give the same events in the same order, run after run.
public class TradingRuntimeTests
{
    /// <summary>Venue stand-in that accepts everything at once and fills market orders at the cached bid/ask.</summary>
    private sealed class AutoFillClient : ExecutionClientBase
    {
        private int _venueOrders;
        private int _trades;

        public AutoFillClient(TradingRuntimeServices services)
            : base(new ClientId("BINANCE"), TestIds.Binance, TestIds.BinanceAccount, AccountType.Cash, null, OmsType.Netting, services)
        {
        }

        public override Task SubmitOrderAsync(SubmitOrder command, CancellationToken ct)
        {
            Order order = command.Order;
            VenueOrderId venueOrderId = new($"V-{++_venueOrders}");
            GenerateOrderSubmitted(order.StrategyId, order.MarketKey, order.ClientOrderId, Clock.Timestamp);
            GenerateOrderAccepted(order.StrategyId, order.MarketKey, order.ClientOrderId, venueOrderId, Clock.Timestamp);
            if (order.Type == OrderType.Market)
            {
                Price price = Services.Cache.Price(order.MarketKey, order.IsBuy ? PriceType.Ask : PriceType.Bid)!.Value;
                GenerateOrderFilled(order.StrategyId, order.MarketKey, order.ClientOrderId, venueOrderId, null, new TradeId($"T-{++_trades}"), order.Side, order.Type,
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
        OrderFilled f => $"OrderFilled {f.ClientOrderId} {f.TradeId} {f.OrderSide} {f.LastQty}@{f.LastPx} pos={f.PositionId} ts={f.EventTime.ToSeconds()}",
        OrderEvent e => $"{e.GetType().Name} {e.ClientOrderId} venue={e.VenueOrderId} ts={e.EventTime.ToSeconds()}",
        PositionEvent p => $"{p.GetType().Name} {p.PositionId} {p.Side} {p.Quantity} realized={p.RealizedPnl} ts={p.EventTime.ToSeconds()}",
        QuoteTick q => $"QuoteTick {q.Bid}/{q.Ask} ts={q.EventTime.ToSeconds()}",
        _ => message.GetType().Name,
    };

    /// <summary>Drives eight one-second steps the way the backtest loop is documented: due timers first, then the data element.</summary>
    private static (List<string> Log, CoreTradingRuntime TradingRuntime) RunScript()
    {
        TestClock clock = new(TestOrders.T0);
        CoreTradingRuntime tradingRuntime = new(new TradingRuntimeConfig { ModuleHostId = TestIds.ModuleHost, InstanceId = "determinism" }, clock);
        tradingRuntime.Cache.AddInstrument(TestInstruments.BtcUsdt());
        tradingRuntime.AddExecutionClient(new AutoFillClient(tradingRuntime.Services));
        tradingRuntime.ModuleHost.AddStrategy(new ScriptedStrategy());
        List<string> log = new();
        tradingRuntime.MessageBus.Subscribe("*", m => log.Add(Describe(m)), priority: 100);
        tradingRuntime.Start();

        for (int i = 1; i <= 8; i++)
        {
            UnixNanos ts = TestOrders.T0 + TimeSpan.FromSeconds(i);
            clock.AdvanceAndRun(ts);
            decimal bid = 50_000m + (i * 10m);
            tradingRuntime.MarketDataService.Process(new QuoteTick(TestIds.BtcUsdt, new Price(bid, 2), new Price(bid + 1m, 2), Quantity.Parse("1.000"), Quantity.Parse("1.000"), ts, ts));
        }

        return (log, tradingRuntime);
    }

    [Fact]
    public void TradingRuntime_builds_every_component_ready_on_one_bus_and_clock()
    {
        TestClock clock = new(TestOrders.T0);
        using CoreTradingRuntime tradingRuntime = new(new TradingRuntimeConfig { ModuleHostId = TestIds.ModuleHost, Environment = TradingEnvironment.Backtest }, clock);

        Assert.Equal(TradingEnvironment.Backtest, tradingRuntime.Environment);
        Assert.Equal(TestIds.ModuleHost, tradingRuntime.ModuleHostId);
        Assert.Same(clock, tradingRuntime.Clock);
        Assert.Same(clock, tradingRuntime.Services.Clock);
        Assert.Same(tradingRuntime.Cache, tradingRuntime.Services.Cache);
        Assert.Same(tradingRuntime.MessageBus, tradingRuntime.Services.MessageBus);
        Assert.All(
            new[] { tradingRuntime.MarketDataService.State, tradingRuntime.OrderPolicy.State, tradingRuntime.OrderCoordinator.State, tradingRuntime.Portfolio.State, tradingRuntime.ModuleHost.State },
            state => Assert.Equal(ComponentState.Ready, state));
        Assert.All(
            new[]
            {
                Endpoints.MarketDataServiceExecute, Endpoints.MarketDataServiceProcess, Endpoints.MarketDataServiceRequest, Endpoints.MarketDataServiceResponse,
                Endpoints.OrderPolicyExecute, Endpoints.OrderCoordinatorExecute, Endpoints.OrderCoordinatorProcess, Endpoints.PortfolioUpdateAccount,
            },
            endpoint => Assert.True(tradingRuntime.MessageBus.IsRegistered(endpoint), endpoint));
        Assert.False(tradingRuntime.IsRunning);
    }

    [Fact]
    public void Start_and_stop_drive_engines_clients_and_strategies_together()
    {
        using TradingRuntimeHarness h = new();
        ProbeStrategy strategy = h.AddStrategy();

        h.TradingRuntime.Start();
        ComponentState[] running = [h.TradingRuntime.MarketDataService.State, h.TradingRuntime.OrderPolicy.State, h.TradingRuntime.OrderCoordinator.State, h.TradingRuntime.Portfolio.State, h.TradingRuntime.ModuleHost.State, strategy.State, h.Client.State];
        h.TradingRuntime.Stop();
        ComponentState[] stopped = [h.TradingRuntime.MarketDataService.State, h.TradingRuntime.OrderPolicy.State, h.TradingRuntime.OrderCoordinator.State, h.TradingRuntime.Portfolio.State, h.TradingRuntime.ModuleHost.State, strategy.State, h.Client.State];

        Assert.All(running, s => Assert.Equal(ComponentState.Running, s));
        Assert.All(stopped, s => Assert.Equal(ComponentState.Stopped, s));
    }

    [Fact]
    public void Stop_saves_runtimeModule_state_and_the_next_start_loads_it()
    {
        using TradingRuntimeHarness h = new();
        ProbeRuntimeModule runtimeModule = new(new RuntimeModuleConfig { RuntimeModuleId = new RuntimeModuleId("Stateful-001") }) { StateToSave = new Dictionary<string, byte[]> { ["k"] = [7] } };
        h.TradingRuntime.ModuleHost.AddRuntimeModule(runtimeModule);
        h.TradingRuntime.Start();

        h.TradingRuntime.Stop();
        h.TradingRuntime.Start();

        Assert.Equal(new byte[] { 7 }, runtimeModule.LoadedState!["k"]);
    }

    [Fact]
    public void Stop_cancels_every_timer_on_the_tradingRuntime_clock()
    {
        using TradingRuntimeHarness h = new();
        ProbeRuntimeModule runtimeModule = new();
        runtimeModule.StartAction = () => runtimeModule.DoSetTimer("pulse", TimeSpan.FromSeconds(1));
        h.TradingRuntime.ModuleHost.AddRuntimeModule(runtimeModule);
        h.TradingRuntime.Start();
        int whileRunning = h.Clock.TimerCount;

        h.TradingRuntime.Stop();

        Assert.Equal(1, whileRunning);
        Assert.Equal(0, h.Clock.TimerCount);
    }

    [Fact]
    public void Reset_clears_trading_state_but_keeps_instruments_clients_and_strategies()
    {
        using TradingRuntimeHarness h = new();
        ProbeStrategy strategy = h.StartWithStrategy();
        MarketOrder order = strategy.Factory.Market(TestIds.BtcUsdt, OrderSide.Buy, Quantity.Parse("1.000"));
        strategy.DoSubmit(order);
        h.Accept(order);
        h.Client.EmitFilled(order, "T-1", "1.000", "50000.00");

        h.TradingRuntime.Reset();

        Assert.False(h.TradingRuntime.IsRunning);
        Assert.Empty(h.Cache.Orders());
        Assert.Empty(h.Cache.Positions());
        Assert.NotNull(h.Cache.Instrument(TestIds.BtcUsdt));
        Assert.Equal(0, h.TradingRuntime.OrderCoordinator.EventCount);
        Assert.Equal(0, h.TradingRuntime.OrderPolicy.CommandCount);
        Assert.Same(strategy, Assert.Single(h.TradingRuntime.ModuleHost.Strategies));
        Assert.Contains(h.Client.ClientId, h.TradingRuntime.OrderCoordinator.RegisteredClients);
        Assert.Equal(ComponentState.Ready, strategy.State);

        h.TradingRuntime.Start();
        Assert.True(h.TradingRuntime.IsRunning);
    }

    [Fact]
    public void Dispose_stops_a_running_tradingRuntime_disposes_everything_and_can_be_repeated()
    {
        TradingRuntimeHarness h = new();
        ProbeStrategy strategy = h.StartWithStrategy();

        h.TradingRuntime.Dispose();
        h.TradingRuntime.Dispose();

        Assert.Equal(ComponentState.Disposed, strategy.State);
        Assert.Equal(ComponentState.Disposed, h.Client.State);
        Assert.All(
            new[] { h.TradingRuntime.MarketDataService.State, h.TradingRuntime.OrderPolicy.State, h.TradingRuntime.OrderCoordinator.State, h.TradingRuntime.Portfolio.State, h.TradingRuntime.ModuleHost.State },
            state => Assert.Equal(ComponentState.Disposed, state));
        Assert.Contains("OnStop", strategy.Calls);
    }

    [Fact]
    public void Scripted_run_produces_the_expected_orders_with_timers_ahead_of_data_at_the_same_timestamp()
    {
        (List<string> log, CoreTradingRuntime tradingRuntime) = RunScript();
        using CoreTradingRuntime disposeAtEnd = tradingRuntime;
        Assert.NotEmpty(log);

        // Quotes 2, 4, 6, 8 trigger market orders; the 3-second timer fires at t=3 and t=6. At t=6 the timer's
        // limit order (count 4) must come before the quote's market order (count 5).
        IReadOnlyList<Order> orders = tradingRuntime.Cache.Orders();
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
        Assert.Equal(["bx-market:v2/BINANCE/BTCUSDT-S-001", "bx-market:v2/BINANCE/BTCUSDT-S-001-2"], tradingRuntime.Cache.PositionsClosed().Select(p => p.Id.Value));
        Assert.Equal(Money.Parse("18.80 USDT"), tradingRuntime.Cache.PositionsClosed()[0].RealizedPnl);
        // Second round trip: bought at 50,061 (t=6), sold at 50,080 (t=8): the same 18.80; total 37.60.
        Assert.Equal(Money.Parse("37.60 USDT"), tradingRuntime.Portfolio.RealizedPnl(TestIds.BtcUsdt));
        Assert.True(tradingRuntime.Portfolio.IsCompletelyFlat());
    }

    [Fact]
    public void Two_runs_of_the_same_script_publish_identical_messages_in_identical_order()
    {
        (List<string> first, CoreTradingRuntime k1) = RunScript();
        (List<string> second, CoreTradingRuntime k2) = RunScript();
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
