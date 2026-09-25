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
// updated before the handler, that a throwing handler faults only its own actor, and how timers, signals,
// custom data, requests, state and background work behave.
public class ActorTests
{
    private sealed class Fixture
    {
        public Fixture()
        {
            Clock = new TestClock(TestOrders.T0);
            Bus = new MessageBus(TestIds.Trader);
            Cache = new Cache();
            Portfolio = new Portfolio(Cache, Bus);
            Bus.Register(Endpoints.DataEngineExecute, m => DataCommands.Add((DataCommand)m));
            Bus.Register(Endpoints.DataEngineRequest, m => DataCommands.Add((DataCommand)m));
        }

        public TestClock Clock { get; }

        public MessageBus Bus { get; }

        public Cache Cache { get; }

        public Portfolio Portfolio { get; }

        public List<DataCommand> DataCommands { get; } = new();

        public ProbeActor Running(string id = "Probe-001", Action<Action>? post = null)
        {
            ProbeActor actor = new(new ActorConfig { ActorId = new ActorId(id) });
            actor.Register(TestIds.Trader, Clock, Cache, Bus, Portfolio, NullLoggerFactory.Instance, post);
            actor.Start();
            actor.Calls.Clear();
            return actor;
        }
    }

    private static BarType MinuteBars => BarType.Parse("BTCUSDT.BINANCE-1-MINUTE-LAST-EXTERNAL");

    private static QuoteTick Quote() => new(TestIds.BtcUsdt, Price.Parse("49990.00"), Price.Parse("50010.00"), Quantity.Parse("1.000"), Quantity.Parse("1.000"), TestOrders.T0, TestOrders.T0);

    private static TradeTick Trade() => new(TestIds.BtcUsdt, Price.Parse("50000.00"), Quantity.Parse("1.000"), AggressorSide.Buyer, new TradeId("T-1"), TestOrders.T0, TestOrders.T0);

    private static Bar MinuteBar(BarType? type = null) => new(type ?? MinuteBars, Price.Parse("1.00"), Price.Parse("2.00"), Price.Parse("0.50"), Price.Parse("1.50"), Quantity.Parse("9.000"), TestOrders.T0, TestOrders.T0);

    [Fact]
    public void Actor_id_defaults_to_the_type_name_and_can_be_configured()
    {
        ProbeActor byDefault = new();
        ProbeActor configured = new(new ActorConfig { ActorId = new ActorId("Monitor-042") });

        Assert.Equal("ProbeActor-000", byDefault.ActorId.Value);
        Assert.Equal("ProbeActor-000", byDefault.Id.Value);
        Assert.Equal("Monitor-042", configured.ActorId.Value);
        Assert.Equal("Monitor-042", configured.Id.Value);
    }

    [Fact]
    public void Unregistered_actor_has_no_access_to_kernel_services()
    {
        ProbeActor actor = new();

        Assert.False(actor.IsRegistered);
        Assert.Throws<InvalidOperationException>(() => actor.CachedInstrumentCount);
        Assert.Throws<InvalidOperationException>(() => actor.DoPublishSignal("s", 1m));
    }

    [Fact]
    public void Registration_makes_the_actor_ready_and_gives_it_cache_and_portfolio()
    {
        Fixture f = new();
        f.Cache.AddInstrument(TestInstruments.BtcUsdt());
        ProbeActor actor = new();

        actor.Register(TestIds.Trader, f.Clock, f.Cache, f.Bus, f.Portfolio, NullLoggerFactory.Instance);

        Assert.True(actor.IsRegistered);
        Assert.Equal(ComponentState.Ready, actor.State);
        Assert.Equal(TestIds.Trader, actor.TraderId);
        Assert.Equal(["OnRegistered"], actor.Calls);
        Assert.Equal(1, actor.CachedInstrumentCount);
        Assert.Equal(0m, actor.NetPositionFromPortfolio(TestIds.BtcUsdt));
    }

    [Fact]
    public void Lifecycle_hooks_are_called_in_order()
    {
        Fixture f = new();
        ProbeActor actor = new();
        actor.Register(TestIds.Trader, f.Clock, f.Cache, f.Bus, f.Portfolio, NullLoggerFactory.Instance);

        actor.Start();
        actor.Degrade();
        actor.Resume();
        actor.Stop();
        actor.Reset();
        actor.Dispose();

        Assert.Equal(["OnRegistered", "OnStart", "OnDegrade", "OnResume", "OnStop", "OnReset", "OnDispose"], actor.Calls);
    }

    [Fact]
    public void Each_subscription_sends_its_command_and_feeds_its_documented_handler()
    {
        Fixture f = new();
        ProbeActor actor = f.Running();
        actor.DoSubscribeInstrument(TestIds.BtcUsdt);
        actor.DoSubscribeQuotes(TestIds.BtcUsdt);
        actor.DoSubscribeTrades(TestIds.BtcUsdt);
        actor.DoSubscribeBars(MinuteBars);
        actor.DoSubscribeBookDeltas(TestIds.BtcUsdt);
        actor.DoSubscribeBook(TestIds.BtcUsdt);
        actor.DoSubscribeStatus(TestIds.BtcUsdt);
        actor.DoSubscribeMarkPrices(TestIds.BtcUsdt);
        actor.DoSubscribeIndexPrices(TestIds.BtcUsdt);
        actor.DoSubscribeFundingRates(TestIds.BtcUsdt);

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
            actor.Calls);
        Assert.Equal(
            [
                nameof(SubscribeInstrument), nameof(SubscribeQuoteTicks), nameof(SubscribeTradeTicks), nameof(SubscribeBars), nameof(SubscribeOrderBookDeltas),
                nameof(SubscribeOrderBookSnapshots), nameof(SubscribeInstrumentStatus), nameof(SubscribeMarkPrices), nameof(SubscribeIndexPrices), nameof(SubscribeFundingRates),
            ],
            f.DataCommands.Select(c => c.GetType().Name));
        Assert.All(f.DataCommands, c => Assert.Equal(TestOrders.T0, c.TsInit));
    }

    [Fact]
    public void Venue_wide_instrument_subscription_receives_every_instrument_of_that_venue()
    {
        Fixture f = new();
        ProbeActor actor = f.Running();
        actor.DoSubscribeInstruments(TestIds.Binance);

        f.Bus.Publish(Topics.Instrument(TestIds.BtcUsdt), TestInstruments.BtcUsdt());
        f.Bus.Publish(Topics.Instrument(TestIds.EthUsdt), TestInstruments.EthUsdt());
        f.Bus.Publish(Topics.Instrument(TestIds.BtcPerp), TestInstruments.BtcPerp());

        Assert.Equal(["OnInstrument", "OnInstrument"], actor.Calls);
    }

    [Fact]
    public void Unsubscribe_stops_delivery_and_tells_the_data_engine()
    {
        Fixture f = new();
        ProbeActor actor = f.Running();
        actor.DoSubscribeQuotes(TestIds.BtcUsdt);

        actor.DoUnsubscribeQuotes(TestIds.BtcUsdt);
        f.Bus.Publish(Topics.Quotes(TestIds.BtcUsdt), Quote());

        Assert.Empty(actor.Calls);
        Assert.IsType<UnsubscribeQuoteTicks>(f.DataCommands[^1]);
        Assert.False(f.Bus.HasSubscribers(Topics.Quotes(TestIds.BtcUsdt)));
    }

    [Fact]
    public void Data_is_not_handled_while_the_actor_is_not_running()
    {
        Fixture f = new();
        ProbeActor actor = f.Running();
        actor.DoSubscribeQuotes(TestIds.BtcUsdt);

        actor.Stop();
        actor.Calls.Clear();
        f.Bus.Publish(Topics.Quotes(TestIds.BtcUsdt), Quote());

        Assert.Empty(actor.Calls);
    }

    [Fact]
    public void Dispose_removes_every_bus_subscription_of_the_actor()
    {
        Fixture f = new();
        ProbeActor actor = f.Running();
        actor.DoSubscribeQuotes(TestIds.BtcUsdt);
        actor.DoSubscribeSignal("momentum");

        actor.Dispose();

        Assert.False(f.Bus.HasSubscribers(Topics.Quotes(TestIds.BtcUsdt)));
        Assert.False(f.Bus.HasSubscribers(Topics.Signal("momentum")));
    }

    [Fact]
    public void Throwing_handler_faults_only_its_own_actor()
    {
        Fixture f = new();
        ProbeActor faulty = f.Running("Faulty-001");
        ProbeActor healthy = f.Running("Healthy-001");
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
        ProbeActor actor = f.Running();
        CountingIndicator indicator = new("EMA", actor.Calls);
        actor.DoRegisterForBars(MinuteBars, indicator);
        actor.DoSubscribeBars(MinuteBars);

        f.Bus.Publish(Topics.Bars(MinuteBars), MinuteBar());

        Assert.Equal(["indicator:EMA:bar", "OnBar"], actor.Calls);
    }

    [Fact]
    public void Indicators_receive_only_the_stream_they_were_registered_for()
    {
        Fixture f = new();
        ProbeActor actor = f.Running();
        CountingIndicator onBars = new("BARS");
        CountingIndicator onQuotes = new("QUOTES");
        CountingIndicator onTrades = new("TRADES");
        BarType fiveMinutes = BarType.Parse("BTCUSDT.BINANCE-5-MINUTE-LAST-EXTERNAL");
        actor.DoRegisterForBars(MinuteBars, onBars);
        actor.DoRegisterForQuotes(TestIds.BtcUsdt, onQuotes);
        actor.DoRegisterForTrades(TestIds.BtcUsdt, onTrades);

        actor.HandleBar(MinuteBar());
        actor.HandleBar(MinuteBar(fiveMinutes));
        actor.HandleQuoteTick(Quote());
        actor.HandleQuoteTick(Quote() with { InstrumentId = TestIds.EthUsdt });
        actor.HandleTradeTick(Trade());

        Assert.Equal((1, 1, 1), (onBars.Count, onQuotes.Count, onTrades.Count));
    }

    [Fact]
    public void Indicators_initialized_is_true_only_when_every_registered_indicator_is()
    {
        Fixture f = new();
        ProbeActor actor = f.Running();
        bool withoutIndicators = actor.AllIndicatorsInitialized;
        CountingIndicator fast = new("FAST", initializedAfter: 1);
        CountingIndicator slow = new("SLOW", initializedAfter: 2);
        actor.DoRegisterForBars(MinuteBars, fast);
        actor.DoRegisterForBars(MinuteBars, slow);
        actor.DoRegisterForBars(MinuteBars, slow);

        actor.HandleBar(MinuteBar());
        bool afterOne = actor.AllIndicatorsInitialized;
        actor.HandleBar(MinuteBar());

        Assert.False(withoutIndicators);
        Assert.False(afterOne);
        Assert.True(actor.AllIndicatorsInitialized);
        Assert.Equal(2, actor.IndicatorCount);
        Assert.Equal(2, slow.Count); // registered twice, updated once per bar
    }

    [Fact]
    public void Request_is_sent_with_the_actor_as_requester_and_completes_through_the_response_topic()
    {
        Fixture f = new();
        ProbeActor actor = f.Running();
        CountingIndicator indicator = new("EMA", actor.Calls);
        actor.DoRegisterForBars(MinuteBars, indicator);

        Guid requestId = actor.DoRequestBars(MinuteBars, limit: 2);
        RequestBars sent = Assert.IsType<RequestBars>(Assert.Single(f.DataCommands));
        bool pendingBefore = actor.IsPending(requestId);
        DataResponse response = new(requestId, new ClientId("BINANCE"), TestIds.Binance, typeof(Bar), [MinuteBar(), MinuteBar()], TestOrders.T0, actor.ActorId);
        f.Bus.Publish(Topics.DataResponses(actor.ActorId), response);

        Assert.Equal(requestId, sent.CommandId);
        Assert.Equal(actor.ActorId, sent.Requester);
        Assert.Equal(2, sent.Limit);
        Assert.True(pendingBefore);
        Assert.False(actor.IsPending(requestId));
        Assert.False(actor.AnyPendingRequests);
        Assert.Equal(
            ["indicator:EMA:bar", "OnHistoricalData", "indicator:EMA:bar", "OnHistoricalData", "OnDataResponse"],
            actor.Calls);
    }

    [Fact]
    public void Timer_without_a_callback_arrives_in_on_time_event_under_an_actor_scoped_name()
    {
        Fixture f = new();
        ProbeActor actor = f.Running("Probe-001");
        actor.DoSetTimer("pulse", TimeSpan.FromSeconds(10));

        f.Clock.AdvanceAndRun(TestOrders.T0 + TimeSpan.FromSeconds(20));

        Assert.Equal(["Probe-001:pulse"], f.Clock.TimerNames);
        Assert.Equal(["OnTimeEvent", "OnEvent", "OnTimeEvent", "OnEvent"], actor.Calls);
        TimeEvent first = Assert.IsType<TimeEvent>(actor.Received[0]);
        Assert.Equal(TestOrders.T0 + TimeSpan.FromSeconds(10), first.TsEvent);
    }

    [Fact]
    public void Timer_with_a_callback_bypasses_on_time_event()
    {
        Fixture f = new();
        ProbeActor actor = f.Running();
        List<UnixNanos> fired = new();
        actor.DoSetTimeAlert("once", TestOrders.T0 + TimeSpan.FromSeconds(5), e => fired.Add(e.TsEvent));

        f.Clock.AdvanceAndRun(TestOrders.T0 + TimeSpan.FromSeconds(60));

        Assert.Equal([TestOrders.T0 + TimeSpan.FromSeconds(5)], fired);
        Assert.Empty(actor.Calls);
    }

    [Fact]
    public void Timers_of_two_actors_with_the_same_name_do_not_collide_and_can_be_cancelled_separately()
    {
        Fixture f = new();
        ProbeActor a = f.Running("A-001");
        ProbeActor b = f.Running("B-001");
        a.DoSetTimer("pulse", TimeSpan.FromSeconds(1));
        b.DoSetTimer("pulse", TimeSpan.FromSeconds(1));

        a.DoCancelTimer("pulse");
        f.Clock.AdvanceAndRun(TestOrders.T0 + TimeSpan.FromSeconds(1));

        Assert.Empty(a.Calls);
        Assert.Equal(["OnTimeEvent", "OnEvent"], b.Calls);
    }

    [Fact]
    public void Throwing_timer_callback_faults_the_actor_instead_of_escaping_into_the_clock_loop()
    {
        Fixture f = new();
        ProbeActor actor = f.Running();
        actor.DoSetTimeAlert("boom", TestOrders.T0 + TimeSpan.FromSeconds(1), _ => throw new InvalidOperationException("boom"));

        f.Clock.AdvanceAndRun(TestOrders.T0 + TimeSpan.FromSeconds(1));

        Assert.Equal(ComponentState.Faulted, actor.State);
    }

    [Fact]
    public void Signal_published_by_one_actor_reaches_subscribers_with_name_value_and_time()
    {
        Fixture f = new();
        ProbeActor publisher = f.Running("Pub-001");
        ProbeActor subscriber = f.Running("Sub-001");
        subscriber.DoSubscribeSignal("momentum");
        f.Clock.SetTime(TestOrders.T0 + TimeSpan.FromSeconds(7));

        publisher.DoPublishSignal("momentum", 0.75m);
        publisher.DoPublishSignal("other", 1m);
        subscriber.DoUnsubscribeSignal("momentum");
        publisher.DoPublishSignal("momentum", 0.80m);

        Signal signal = Assert.IsType<Signal>(Assert.Single(subscriber.Received));
        Assert.Equal(("momentum", 0.75m, TestOrders.T0 + TimeSpan.FromSeconds(7)), (signal.Name, signal.Value, signal.TsEvent));
        Assert.Empty(publisher.Calls);
    }

    [Fact]
    public void Custom_data_is_delivered_by_type_and_metadata()
    {
        Fixture f = new();
        ProbeActor publisher = f.Running("Pub-001");
        ProbeActor english = f.Running("En-001");
        ProbeActor any = f.Running("Any-001");
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
    public void Saved_state_is_stored_in_the_cache_under_the_actor_id_and_handed_back_on_load()
    {
        Fixture f = new();
        ProbeActor actor = f.Running("Stateful-001");
        actor.StateToSave = new Dictionary<string, byte[]> { ["position_bias"] = [1, 0, 1] };

        actor.Save();
        ProbeActor restarted = new(new ActorConfig { ActorId = new ActorId("Stateful-001") });
        restarted.Register(TestIds.Trader, f.Clock, f.Cache, f.Bus, f.Portfolio, NullLoggerFactory.Instance);
        restarted.Load();

        Assert.Equal(new byte[] { 1, 0, 1 }, restarted.LoadedState!["position_bias"]);
        Assert.Equal(new byte[] { 1, 0, 1 }, f.Cache.LoadActorState(new ActorId("Stateful-001"))!["position_bias"]);
    }

    [Fact]
    public void Load_without_saved_state_does_not_call_on_load()
    {
        Fixture f = new();
        ProbeActor actor = f.Running();

        actor.Load();

        Assert.DoesNotContain("OnLoad", actor.Calls);
    }

    [Fact]
    public void Post_runs_inline_when_the_kernel_supplies_no_dispatcher()
    {
        Fixture f = new();
        ProbeActor actor = f.Running();
        bool ran = false;

        actor.DoPost(() => ran = true);

        Assert.True(ran);
    }

    [Fact]
    public async Task Background_failure_is_marshalled_through_the_kernel_dispatcher_before_the_error_handler_runs()
    {
        Fixture f = new();
        TaskCompletionSource<Action> posted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ProbeActor actor = f.Running(post: a => posted.TrySetResult(a));
        Exception? seen = null;

        actor.DoRunInBackground(_ => throw new InvalidOperationException("io failed"), e => seen = e);
        Action marshalled = await posted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Exception? beforeDispatch = seen;
        marshalled();

        Assert.Null(beforeDispatch); // nothing touches actor state until the kernel thread runs the posted action
        Assert.Equal("io failed", Assert.IsType<InvalidOperationException>(seen).Message);
    }

    [Fact]
    public async Task Background_work_that_succeeds_posts_nothing()
    {
        Fixture f = new();
        int posts = 0;
        ProbeActor actor = f.Running(post: _ => Interlocked.Increment(ref posts));
        TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        actor.DoRunInBackground(_ =>
        {
            done.SetResult();
            return Task.CompletedTask;
        });
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, Volatile.Read(ref posts));
    }

    private sealed record Headline(string Text, UnixNanos TsEvent, UnixNanos TsInit) : CustomData(TsEvent, TsInit);
}
