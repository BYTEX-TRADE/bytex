using Bytex.Core.Caching;
using Bytex.Core.Messaging;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Portfolios;
using Bytex.Core.Tests.Support;
using Bytex.Core.Timing;
using Bytex.Core.Trading;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bytex.Core.Tests.Trading;

// Why: R5.1, R5.4-R5.7 - strategies.md promises which handler each subscription feeds, that indicators are
// updated before the handler, that a throwing handler faults only its own runtimeModule, and how timers, signals,
// custom data, requests, state and background work behave.
public class RuntimeModuleTests
{
    private sealed class Fixture
    {
        public Fixture()
        {
            Clock = new TestClock(TestOrders.T0);
            Bus = new MessageBus(TestIds.ModuleHost);
            Cache = new Cache();
            Portfolio = new Portfolio(Cache, Bus);
            Bus.Register(Endpoints.MarketDataServiceExecute, m => DataCommands.Add((DataCommand)m));
            Bus.Register(Endpoints.MarketDataServiceRequest, m => DataCommands.Add((DataCommand)m));
        }

        public TestClock Clock { get; }

        public MessageBus Bus { get; }

        public Cache Cache { get; }

        public Portfolio Portfolio { get; }

        public List<DataCommand> DataCommands { get; } = new();

        public ProbeRuntimeModule Running(string id = "Probe-001", Action<Action>? post = null)
        {
            ProbeRuntimeModule runtimeModule = new(new RuntimeModuleConfig { RuntimeModuleId = new RuntimeModuleId(id) });
            runtimeModule.Register(TestIds.ModuleHost, Clock, Cache, Bus, Portfolio, NullLoggerFactory.Instance, post);
            runtimeModule.Start();
            runtimeModule.Calls.Clear();
            return runtimeModule;
        }
    }

    private static CandleSeries MinuteBars => CandleSeries.Parse("bx-candle:v2/BINANCE/BTCUSDT/minute/1/last/provider");

    private static QuoteTick Quote() => new(TestIds.BtcUsdt, Price.Parse("49990.00"), Price.Parse("50010.00"), Quantity.Parse("1.000"), Quantity.Parse("1.000"), TestOrders.T0, TestOrders.T0);

    private static TradeTick Trade() => new(TestIds.BtcUsdt, Price.Parse("50000.00"), Quantity.Parse("1.000"), AggressorSide.Buyer, new TradeId("T-1"), TestOrders.T0, TestOrders.T0);

    private static Bar MinuteBar(CandleSeries? type = null) => new(type ?? MinuteBars, Price.Parse("1.00"), Price.Parse("2.00"), Price.Parse("0.50"), Price.Parse("1.50"), Quantity.Parse("9.000"), TestOrders.T0, TestOrders.T0);

    [Fact]
    public void RuntimeModule_id_defaults_to_the_type_name_and_can_be_configured()
    {
        ProbeRuntimeModule byDefault = new();
        ProbeRuntimeModule configured = new(new RuntimeModuleConfig { RuntimeModuleId = new RuntimeModuleId("Monitor-042") });

        Assert.Equal("ProbeRuntimeModule-000", byDefault.RuntimeModuleId.Value);
        Assert.Equal("ProbeRuntimeModule-000", byDefault.Id.Value);
        Assert.Equal("Monitor-042", configured.RuntimeModuleId.Value);
        Assert.Equal("Monitor-042", configured.Id.Value);
    }

    [Fact]
    public void Unregistered_runtimeModule_has_no_access_to_tradingRuntime_services()
    {
        ProbeRuntimeModule runtimeModule = new();

        Assert.False(runtimeModule.IsRegistered);
        Assert.Throws<InvalidOperationException>(() => runtimeModule.CachedInstrumentCount);
        Assert.Throws<InvalidOperationException>(() => runtimeModule.DoPublishSignal("s", 1m));
    }

    [Fact]
    public void Registration_makes_the_runtimeModule_ready_and_gives_it_cache_and_portfolio()
    {
        Fixture f = new();
        f.Cache.AddInstrument(TestInstruments.BtcUsdt());
        ProbeRuntimeModule runtimeModule = new();

        runtimeModule.Register(TestIds.ModuleHost, f.Clock, f.Cache, f.Bus, f.Portfolio, NullLoggerFactory.Instance);

        Assert.True(runtimeModule.IsRegistered);
        Assert.Equal(ComponentState.Ready, runtimeModule.State);
        Assert.Equal(TestIds.ModuleHost, runtimeModule.ModuleHostId);
        Assert.Equal(["OnRegistered"], runtimeModule.Calls);
        Assert.Equal(1, runtimeModule.CachedInstrumentCount);
        Assert.Equal(0m, runtimeModule.NetPositionFromPortfolio(TestIds.BtcUsdt));
    }

    [Fact]
    public void Lifecycle_hooks_are_called_in_order()
    {
        Fixture f = new();
        ProbeRuntimeModule runtimeModule = new();
        runtimeModule.Register(TestIds.ModuleHost, f.Clock, f.Cache, f.Bus, f.Portfolio, NullLoggerFactory.Instance);

        runtimeModule.Start();
        runtimeModule.Degrade();
        runtimeModule.Resume();
        runtimeModule.Stop();
        runtimeModule.Reset();
        runtimeModule.Dispose();

        Assert.Equal(["OnRegistered", "OnStart", "OnDegrade", "OnResume", "OnStop", "OnReset", "OnDispose"], runtimeModule.Calls);
    }

    [Fact]
    public void Each_subscription_sends_its_command_and_feeds_its_documented_handler()
    {
        Fixture f = new();
        ProbeRuntimeModule runtimeModule = f.Running();
        runtimeModule.DoSubscribeInstrument(TestIds.BtcUsdt);
        runtimeModule.DoSubscribeQuotes(TestIds.BtcUsdt);
        runtimeModule.DoSubscribeTrades(TestIds.BtcUsdt);
        runtimeModule.DoSubscribeBars(MinuteBars);
        runtimeModule.DoSubscribeBookDeltas(TestIds.BtcUsdt);
        runtimeModule.DoSubscribeBook(TestIds.BtcUsdt);
        runtimeModule.DoSubscribeStatus(TestIds.BtcUsdt);
        runtimeModule.DoSubscribeMarkPrices(TestIds.BtcUsdt);
        runtimeModule.DoSubscribeIndexPrices(TestIds.BtcUsdt);
        runtimeModule.DoSubscribeFundingRates(TestIds.BtcUsdt);

        f.Bus.Publish(Topics.Instrument(TestIds.BtcUsdt), TestInstruments.BtcUsdt());
        f.Bus.Publish(Topics.Quotes(TestIds.BtcUsdt), Quote());
        f.Bus.Publish(Topics.Trades(TestIds.BtcUsdt), Trade());
        f.Bus.Publish(Topics.Bars(MinuteBars), MinuteBar());
        f.Bus.Publish(Topics.BookDeltas(TestIds.BtcUsdt), new OrderBookDeltas(TestIds.BtcUsdt, [], RecordFlags.None, 1, TestOrders.T0, TestOrders.T0));
        f.Bus.Publish(Topics.BookSnapshots(TestIds.BtcUsdt), new OrderBook(TestIds.BtcUsdt, BookType.L2));
        f.Bus.Publish(Topics.Status(TestIds.BtcUsdt), new InstrumentStatus(TestIds.BtcUsdt, MarketStatus.Open, null, TestOrders.T0, TestOrders.T0));
        f.Bus.Publish(Topics.MarkPrices(TestIds.BtcUsdt), new MarkPriceUpdate(TestIds.BtcUsdt, Price.Parse("1.00"), TestOrders.T0, TestOrders.T0));
        f.Bus.Publish(Topics.IndexPrices(TestIds.BtcUsdt), new IndexPriceUpdate(TestIds.BtcUsdt, Price.Parse("1.00"), TestOrders.T0, TestOrders.T0));
        f.Bus.Publish(Topics.FundingRates(TestIds.BtcUsdt), new FundingRateUpdate(TestIds.BtcUsdt, 0.0001m, null, TestOrders.T0, TestOrders.T0));

        Assert.Equal(
            ["OnInstrument", "OnQuoteTick", "OnTradeTick", "OnBar", "OnOrderBookDeltas", "OnOrderBook", "OnInstrumentStatus", "OnMarkPrice", "OnIndexPrice", "OnFundingRate"],
            runtimeModule.Calls);
        Assert.Equal(
            [
                nameof(SubscribeInstrument), nameof(SubscribeQuoteTicks), nameof(SubscribeTradeTicks), nameof(SubscribeBars), nameof(SubscribeOrderBookDeltas),
                nameof(SubscribeOrderBookSnapshots), nameof(SubscribeInstrumentStatus), nameof(SubscribeMarkPrices), nameof(SubscribeIndexPrices), nameof(SubscribeFundingRates),
            ],
            f.DataCommands.Select(c => c.GetType().Name));
        Assert.All(f.DataCommands, c => Assert.Equal(TestOrders.T0, c.CreatedTime));
    }

    [Fact]
    public void Venue_wide_instrument_subscription_receives_every_instrument_of_that_venue()
    {
        Fixture f = new();
        ProbeRuntimeModule runtimeModule = f.Running();
        runtimeModule.DoSubscribeInstruments(TestIds.Binance);

        f.Bus.Publish(Topics.Instrument(TestIds.BtcUsdt), TestInstruments.BtcUsdt());
        f.Bus.Publish(Topics.Instrument(TestIds.EthUsdt), TestInstruments.EthUsdt());
        f.Bus.Publish(Topics.Instrument(TestIds.BtcPerp), TestInstruments.BtcPerp());

        Assert.Equal(["OnInstrument", "OnInstrument"], runtimeModule.Calls);
    }

    [Fact]
    public void Unsubscribe_stops_delivery_and_tells_the_market_data_service()
    {
        Fixture f = new();
        ProbeRuntimeModule runtimeModule = f.Running();
        runtimeModule.DoSubscribeQuotes(TestIds.BtcUsdt);

        runtimeModule.DoUnsubscribeQuotes(TestIds.BtcUsdt);
        f.Bus.Publish(Topics.Quotes(TestIds.BtcUsdt), Quote());

        Assert.Empty(runtimeModule.Calls);
        Assert.IsType<UnsubscribeQuoteTicks>(f.DataCommands[^1]);
        Assert.False(f.Bus.HasSubscribers(Topics.Quotes(TestIds.BtcUsdt)));
    }

    [Fact]
    public void Data_is_not_handled_while_the_runtimeModule_is_not_running()
    {
        Fixture f = new();
        ProbeRuntimeModule runtimeModule = f.Running();
        runtimeModule.DoSubscribeQuotes(TestIds.BtcUsdt);

        runtimeModule.Stop();
        runtimeModule.Calls.Clear();
        f.Bus.Publish(Topics.Quotes(TestIds.BtcUsdt), Quote());

        Assert.Empty(runtimeModule.Calls);
    }

    [Fact]
    public void Dispose_removes_every_bus_subscription_of_the_runtimeModule()
    {
        Fixture f = new();
        ProbeRuntimeModule runtimeModule = f.Running();
        runtimeModule.DoSubscribeQuotes(TestIds.BtcUsdt);
        runtimeModule.DoSubscribeSignal("momentum");

        runtimeModule.Dispose();

        Assert.False(f.Bus.HasSubscribers(Topics.Quotes(TestIds.BtcUsdt)));
        Assert.False(f.Bus.HasSubscribers(Topics.Signal("momentum")));
    }

    [Fact]
    public void Throwing_handler_faults_only_its_own_runtimeModule()
    {
        Fixture f = new();
        ProbeRuntimeModule faulty = f.Running("Faulty-001");
        ProbeRuntimeModule healthy = f.Running("Healthy-001");
        faulty.DoSubscribeQuotes(TestIds.BtcUsdt);
        healthy.DoSubscribeQuotes(TestIds.BtcUsdt);
        faulty.ThrowIn = "OnQuoteTick";

        f.Bus.Publish(Topics.Quotes(TestIds.BtcUsdt), Quote());
        f.Bus.Publish(Topics.Quotes(TestIds.BtcUsdt), Quote());

        Assert.Equal(ComponentState.Faulted, faulty.State);
        Assert.Equal(["OnQuoteTick", "OnFault"], faulty.Calls); // the second quote is no longer handled
        Assert.Equal(ComponentState.Running, healthy.State);
        Assert.Equal(["OnQuoteTick", "OnQuoteTick"], healthy.Calls);
    }

    [Fact]
    public void Registered_indicator_is_updated_before_the_bar_handler_runs()
    {
        Fixture f = new();
        ProbeRuntimeModule runtimeModule = f.Running();
        CountingIndicator indicator = new("EMA", runtimeModule.Calls);
        runtimeModule.DoRegisterForBars(MinuteBars, indicator);
        runtimeModule.DoSubscribeBars(MinuteBars);

        f.Bus.Publish(Topics.Bars(MinuteBars), MinuteBar());

        Assert.Equal(["indicator:EMA:bar", "OnBar"], runtimeModule.Calls);
    }

    [Fact]
    public void Indicators_receive_only_the_stream_they_were_registered_for()
    {
        Fixture f = new();
        ProbeRuntimeModule runtimeModule = f.Running();
        CountingIndicator onBars = new("BARS");
        CountingIndicator onQuotes = new("QUOTES");
        CountingIndicator onTrades = new("TRADES");
        CandleSeries fiveMinutes = CandleSeries.Parse("bx-candle:v2/BINANCE/BTCUSDT/minute/5/last/provider");
        runtimeModule.DoRegisterForBars(MinuteBars, onBars);
        runtimeModule.DoRegisterForQuotes(TestIds.BtcUsdt, onQuotes);
        runtimeModule.DoRegisterForTrades(TestIds.BtcUsdt, onTrades);

        runtimeModule.HandleBar(MinuteBar());
        runtimeModule.HandleBar(MinuteBar(fiveMinutes));
        runtimeModule.HandleQuoteTick(Quote());
        runtimeModule.HandleQuoteTick(Quote() with { MarketKey = TestIds.EthUsdt });
        runtimeModule.HandleTradeTick(Trade());

        Assert.Equal((1, 1, 1), (onBars.Count, onQuotes.Count, onTrades.Count));
    }

    [Fact]
    public void Indicators_initialized_is_true_only_when_every_registered_indicator_is()
    {
        Fixture f = new();
        ProbeRuntimeModule runtimeModule = f.Running();
        bool withoutIndicators = runtimeModule.AllIndicatorsInitialized;
        CountingIndicator fast = new("FAST", initializedAfter: 1);
        CountingIndicator slow = new("SLOW", initializedAfter: 2);
        runtimeModule.DoRegisterForBars(MinuteBars, fast);
        runtimeModule.DoRegisterForBars(MinuteBars, slow);
        runtimeModule.DoRegisterForBars(MinuteBars, slow);

        runtimeModule.HandleBar(MinuteBar());
        bool afterOne = runtimeModule.AllIndicatorsInitialized;
        runtimeModule.HandleBar(MinuteBar());

        Assert.False(withoutIndicators);
        Assert.False(afterOne);
        Assert.True(runtimeModule.AllIndicatorsInitialized);
        Assert.Equal(2, runtimeModule.IndicatorCount);
        Assert.Equal(2, slow.Count); // registered twice, updated once per bar
    }

    [Fact]
    public void Request_is_sent_with_the_runtimeModule_as_requester_and_completes_through_the_response_topic()
    {
        Fixture f = new();
        ProbeRuntimeModule runtimeModule = f.Running();
        CountingIndicator indicator = new("EMA", runtimeModule.Calls);
        runtimeModule.DoRegisterForBars(MinuteBars, indicator);

        Guid requestId = runtimeModule.DoRequestBars(MinuteBars, limit: 2);
        RequestBars sent = Assert.IsType<RequestBars>(Assert.Single(f.DataCommands));
        bool pendingBefore = runtimeModule.IsPending(requestId);
        DataResponse response = new(requestId, new ClientId("BINANCE"), TestIds.Binance, typeof(Bar), [MinuteBar(), MinuteBar()], TestOrders.T0, runtimeModule.RuntimeModuleId);
        f.Bus.Publish(Topics.DataResponses(runtimeModule.RuntimeModuleId), response);

        Assert.Equal(requestId, sent.CommandId);
        Assert.Equal(runtimeModule.RuntimeModuleId, sent.Requester);
        Assert.Equal(2, sent.Limit);
        Assert.True(pendingBefore);
        Assert.False(runtimeModule.IsPending(requestId));
        Assert.False(runtimeModule.AnyPendingRequests);
        Assert.Equal(
            ["indicator:EMA:bar", "OnHistoricalData", "indicator:EMA:bar", "OnHistoricalData", "OnDataResponse"],
            runtimeModule.Calls);
    }

    [Fact]
    public void Timer_without_a_callback_arrives_in_on_time_event_under_an_runtimeModule_scoped_name()
    {
        Fixture f = new();
        ProbeRuntimeModule runtimeModule = f.Running("Probe-001");
        runtimeModule.DoSetTimer("pulse", TimeSpan.FromSeconds(10));

        f.Clock.AdvanceAndRun(TestOrders.T0 + TimeSpan.FromSeconds(20));

        Assert.Equal(["Probe-001:pulse"], f.Clock.TimerNames);
        Assert.Equal(["OnTimeEvent", "OnEvent", "OnTimeEvent", "OnEvent"], runtimeModule.Calls);
        TimeEvent first = Assert.IsType<TimeEvent>(runtimeModule.Received[0]);
        Assert.Equal(TestOrders.T0 + TimeSpan.FromSeconds(10), first.EventTime);
    }

    [Fact]
    public void Timer_with_a_callback_bypasses_on_time_event()
    {
        Fixture f = new();
        ProbeRuntimeModule runtimeModule = f.Running();
        List<UnixNanos> fired = new();
        runtimeModule.DoSetTimeAlert("once", TestOrders.T0 + TimeSpan.FromSeconds(5), e => fired.Add(e.EventTime));

        f.Clock.AdvanceAndRun(TestOrders.T0 + TimeSpan.FromSeconds(60));

        Assert.Equal([TestOrders.T0 + TimeSpan.FromSeconds(5)], fired);
        Assert.Empty(runtimeModule.Calls);
    }

    [Fact]
    public void Timers_of_two_actors_with_the_same_name_do_not_collide_and_can_be_cancelled_separately()
    {
        Fixture f = new();
        ProbeRuntimeModule a = f.Running("A-001");
        ProbeRuntimeModule b = f.Running("B-001");
        a.DoSetTimer("pulse", TimeSpan.FromSeconds(1));
        b.DoSetTimer("pulse", TimeSpan.FromSeconds(1));

        a.DoCancelTimer("pulse");
        f.Clock.AdvanceAndRun(TestOrders.T0 + TimeSpan.FromSeconds(1));

        Assert.Empty(a.Calls);
        Assert.Equal(["OnTimeEvent", "OnEvent"], b.Calls);
    }

    [Fact]
    public void Throwing_timer_callback_faults_the_runtimeModule_instead_of_escaping_into_the_clock_loop()
    {
        Fixture f = new();
        ProbeRuntimeModule runtimeModule = f.Running();
        runtimeModule.DoSetTimeAlert("boom", TestOrders.T0 + TimeSpan.FromSeconds(1), _ => throw new InvalidOperationException("boom"));

        f.Clock.AdvanceAndRun(TestOrders.T0 + TimeSpan.FromSeconds(1));

        Assert.Equal(ComponentState.Faulted, runtimeModule.State);
    }

    [Fact]
    public void Signal_published_by_one_runtimeModule_reaches_subscribers_with_name_value_and_time()
    {
        Fixture f = new();
        ProbeRuntimeModule publisher = f.Running("Pub-001");
        ProbeRuntimeModule subscriber = f.Running("Sub-001");
        subscriber.DoSubscribeSignal("momentum");
        f.Clock.SetTime(TestOrders.T0 + TimeSpan.FromSeconds(7));

        publisher.DoPublishSignal("momentum", 0.75m);
        publisher.DoPublishSignal("other", 1m);
        subscriber.DoUnsubscribeSignal("momentum");
        publisher.DoPublishSignal("momentum", 0.80m);

        Signal signal = Assert.IsType<Signal>(Assert.Single(subscriber.Received));
        Assert.Equal(("momentum", 0.75m, TestOrders.T0 + TimeSpan.FromSeconds(7)), (signal.Name, signal.Value, signal.EventTime));
        Assert.Empty(publisher.Calls);
    }

    [Fact]
    public void Custom_data_is_delivered_by_type_and_metadata()
    {
        Fixture f = new();
        ProbeRuntimeModule publisher = f.Running("Pub-001");
        ProbeRuntimeModule english = f.Running("En-001");
        ProbeRuntimeModule any = f.Running("Any-001");
        Dictionary<string, string> en = new() { ["lang"] = "en" };
        Dictionary<string, string> de = new() { ["lang"] = "de" };
        english.DoSubscribeData<Headline>(en);
        any.DoSubscribeData<Headline>();
        Headline hello = new("hello", TestOrders.T0, TestOrders.T0);

        publisher.DoPublishData(hello, en);
        publisher.DoPublishData(new Headline("hallo", TestOrders.T0, TestOrders.T0), de);
        publisher.DoPublishData(new Headline("plain", TestOrders.T0, TestOrders.T0));

        Assert.Same(hello, Assert.Single(english.Received));
        Assert.Equal("plain", Assert.IsType<Headline>(Assert.Single(any.Received)).Text);
        Assert.Equal("Headline.lang=en", Assert.IsType<SubscribeData>(f.DataCommands[0]).DataType.Topic);
    }

    [Fact]
    public void Saved_state_is_stored_in_the_cache_under_the_runtimeModule_id_and_handed_back_on_load()
    {
        Fixture f = new();
        ProbeRuntimeModule runtimeModule = f.Running("Stateful-001");
        runtimeModule.StateToSave = new Dictionary<string, byte[]> { ["position_bias"] = [1, 0, 1] };

        runtimeModule.Save();
        ProbeRuntimeModule restarted = new(new RuntimeModuleConfig { RuntimeModuleId = new RuntimeModuleId("Stateful-001") });
        restarted.Register(TestIds.ModuleHost, f.Clock, f.Cache, f.Bus, f.Portfolio, NullLoggerFactory.Instance);
        restarted.Load();

        Assert.Equal(new byte[] { 1, 0, 1 }, restarted.LoadedState!["position_bias"]);
        Assert.Equal(new byte[] { 1, 0, 1 }, f.Cache.LoadRuntimeModuleState(new RuntimeModuleId("Stateful-001"))!["position_bias"]);
    }

    [Fact]
    public void Load_without_saved_state_does_not_call_on_load()
    {
        Fixture f = new();
        ProbeRuntimeModule runtimeModule = f.Running();

        runtimeModule.Load();

        Assert.DoesNotContain("OnLoad", runtimeModule.Calls);
    }

    [Fact]
    public void Post_runs_inline_when_the_tradingRuntime_supplies_no_dispatcher()
    {
        Fixture f = new();
        ProbeRuntimeModule runtimeModule = f.Running();
        bool ran = false;

        runtimeModule.DoPost(() => ran = true);

        Assert.True(ran);
    }

    [Fact]
    public async Task Background_failure_is_marshalled_through_the_tradingRuntime_dispatcher_before_the_error_handler_runs()
    {
        Fixture f = new();
        TaskCompletionSource<Action> posted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ProbeRuntimeModule runtimeModule = f.Running(post: a => posted.TrySetResult(a));
        Exception? seen = null;

        runtimeModule.DoRunInBackground(_ => throw new InvalidOperationException("io failed"), e => seen = e);
        Action marshalled = await posted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Exception? beforeDispatch = seen;
        marshalled();

        Assert.Null(beforeDispatch); // nothing touches runtimeModule state until the tradingRuntime thread runs the posted action
        Assert.Equal("io failed", Assert.IsType<InvalidOperationException>(seen).Message);
    }

    [Fact]
    public async Task Background_work_that_succeeds_posts_nothing()
    {
        Fixture f = new();
        int posts = 0;
        ProbeRuntimeModule runtimeModule = f.Running(post: _ => Interlocked.Increment(ref posts));
        TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        runtimeModule.DoRunInBackground(_ =>
        {
            done.SetResult();
            return Task.CompletedTask;
        });
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, Volatile.Read(ref posts));
    }

    private sealed record Headline(string Text, UnixNanos EventTime, UnixNanos CreatedTime) : CustomData(EventTime, CreatedTime);
}
