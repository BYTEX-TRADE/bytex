using Bytex.Core.Kernel;
using Bytex.Core.Model;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Positions;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Model.Reports;
using Bytex.Core.Plugins;
using Bytex.Core.Timing;
using Bytex.Live.Tests.Support;

namespace Bytex.Live.Tests;

// Why: three defects found on a real run, each of which leaves a node quietly wrong rather than failing. A data request
// that fails answered nobody, so whatever asked waited for ever. An alert whose time had already passed fired inside the
// call that set it, where the actor is not running yet and drops it, so the thing it was waiting for never came. And a
// position the venue reported but the engine did not have was written to the log and then ignored, so a node restarted
// later than its reconciliation window traded on as if it were flat.
public sealed class EngineDefectTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(20);

    private sealed record Rig(TradingNode Node, ProbeStrategy Strategy, FakeDataClient Data, FakeExecutionClient Exec, Instrument Instrument) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Node.DisposeAsync();
    }

    private static async Task<Rig> StartAsync(Action<FakeExecutionClient>? configureExec = null, bool started = true, IReadOnlyList<InstrumentId>? claims = null)
    {
        Journal journal = new();
        PluginRegistry registry = new();
        FakeDataClientFactory dataFactory = new(journal);
        FakeExecutionClientFactory execFactory = new(journal) { Configure = configureExec };
        registry.AddDataClientFactory(dataFactory);
        registry.AddExecutionClientFactory(execFactory);
        Instrument instrument = TestInstruments.BtcUsdt("FAKE");

        TradingNode node = new(new TradingNodeConfig
        {
            Kernel = new KernelConfig { Environment = TradingEnvironment.Live, TraderId = new TraderId("TESTER-001") },
            DataClients = [new ClientEntry("FAKE", "FAKE-DATA", new FakeDataClientConfig())],
            ExecutionClients = [new ClientEntry("FAKE", "FAKE-EXEC", new FakeExecutionClientConfig())],
            HeartbeatInterval = TimeSpan.Zero,
        }, registry);
        node.AddInstrument(instrument);
        ProbeStrategy strategy = new("Probe-001", journal, claims);
        node.AddStrategy(strategy);
        if (started)
        {
            await node.StartAsync().WaitAsync(_timeout);
        }

        return new Rig(node, strategy, dataFactory.Created.Single().Client, execFactory.Created.Single().Client, instrument);
    }

    [Fact]
    public async Task A_data_request_that_fails_answers_the_actor_that_asked_with_the_reason()
    {
        await using Rig rig = await StartAsync();
        rig.Data.RequestFailure = new InvalidOperationException("the venue refused the history request");
        BarType barType = new(rig.Instrument.Id, new BarSpecification(1, BarAggregation.Minute, PriceType.Last));

        Guid requestId = await rig.Node.Loop.InvokeAsync(() => rig.Strategy.AskForBars(barType)).WaitAsync(_timeout);
        DataResponse response = await rig.Strategy.FirstDataResponse.Task.WaitAsync(_timeout);

        Assert.Equal(requestId, response.CorrelationId);
        Assert.True(response.IsError);
        Assert.Contains("the venue refused the history request", response.Error!, StringComparison.Ordinal);
        Assert.Empty(response.Data);

        // The request is no longer pending, so the strategy can ask again instead of waiting for an answer that never comes.
        Assert.False(await rig.Node.Loop.InvokeAsync(() => rig.Strategy.HasPending(requestId)).WaitAsync(_timeout));
    }

    [Fact]
    public async Task A_request_that_succeeds_still_answers_normally()
    {
        await using Rig rig = await StartAsync();
        BarType barType = new(rig.Instrument.Id, new BarSpecification(1, BarAggregation.Minute, PriceType.Last));

        await rig.Node.Loop.InvokeAsync(() => rig.Strategy.AskForBars(barType)).WaitAsync(_timeout);
        DataResponse response = await rig.Strategy.FirstDataResponse.Task.WaitAsync(_timeout);

        Assert.False(response.IsError);
    }

    [Fact]
    public async Task An_alert_whose_time_has_already_passed_does_not_fire_inside_the_call_that_sets_it()
    {
        // What the defect was: the alert ran on the thread that set it, reentrantly, before the actor was started,
        // and the actor dropped it. So the thread is what has to be measured. A flag saying "we are inside the call"
        // measures something else - whether the alert fired at the same MOMENT - and a due alert firing at once on a
        // pool thread is not the defect, it is the fix working. That flag passed here and failed in CI, where the
        // pool thread got there before SetTimeAlert had returned.
        int setterThread = Environment.CurrentManagedThreadId;
        int handlerThread = 0;
        List<TimeEvent> fired = new();
        TaskCompletionSource<TimeEvent> first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<TimeEvent> fence = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using LiveClock clock = new();
        clock.RegisterDefaultHandler(e =>
        {
            Volatile.Write(ref handlerThread, Environment.CurrentManagedThreadId);
            lock (fired)
            {
                fired.Add(e);
            }

            if (e.Name == "fence")
            {
                fence.TrySetResult(e);
            }
            else
            {
                first.TrySetResult(e);
            }
        });

        clock.SetTimeAlert("already-due", clock.Timestamp.AddNanos(-UnixNanos.NanosPerSecond));

        TimeEvent e = await first.Task.WaitAsync(_timeout);
        Assert.Equal("already-due", e.Name);
        Assert.NotEqual(setterThread, Volatile.Read(ref handlerThread));

        // A due alert must not repeat, and waiting a while to see is how a test becomes flaky. So a second alert is
        // set just ahead of now: the clock hands events over in time order, so once the fence has arrived any repeat
        // of the first would already have.
        clock.SetTimeAlert("fence", clock.Timestamp.AddNanos(UnixNanos.NanosPerMillisecond));
        await fence.Task.WaitAsync(_timeout);
        lock (fired)
        {
            Assert.Equal(["already-due", "fence"], fired.Select(f => f.Name));
        }
    }

    // The same thing an actor does: set an alert in OnStart for a time that has passed, and be told about it once the
    // actor is running.
    [Fact]
    public async Task An_alert_set_while_starting_reaches_the_actor_that_set_it()
    {
        Journal journal = new();
        PluginRegistry registry = new();
        FakeDataClientFactory dataFactory = new(journal);
        registry.AddDataClientFactory(dataFactory);
        await using TradingNode node = new(new TradingNodeConfig
        {
            Kernel = new KernelConfig { Environment = TradingEnvironment.Live, TraderId = new TraderId("TESTER-001") },
            DataClients = [new ClientEntry("FAKE", "FAKE-DATA", new FakeDataClientConfig())],
            HeartbeatInterval = TimeSpan.Zero,
        }, registry);
        AlertOnStartStrategy strategy = new();
        node.AddStrategy(strategy);

        await node.StartAsync().WaitAsync(_timeout);

        TimeEvent e = await strategy.Fired.Task.WaitAsync(_timeout);
        Assert.Contains("due-at-start", e.Name, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_position_the_venue_reports_and_the_engine_does_not_have_is_adopted_at_start()
    {
        InstrumentId id = TestInstruments.BtcUsdt("FAKE").Id;
        ExecutionMassStatus status = new(new ClientId("FAKE-EXEC"), new AccountId("FAKE-001"), new Venue("FAKE"), [], [],
            [new PositionStatusReport(new AccountId("FAKE-001"), id, PositionSide.Long, new Quantity(0.25m, 5), new UnixNanos(1), new UnixNanos(1), Guid.NewGuid(), AvgPxOpen: 61_000m)],
            new UnixNanos(1), Guid.NewGuid());

        await using Rig rig = await StartAsync(exec => exec.MassStatus = status, claims: [id]);

        Position position = Assert.Single(await rig.Node.Loop.InvokeAsync(() => rig.Node.Kernel.Cache.PositionsOpen(instrumentId: id)).WaitAsync(_timeout));
        Assert.Equal((PositionSide.Long, 0.25m, 61_000m), (position.Side, position.Quantity.Value, position.AvgPxOpen));
        Assert.Equal(new StrategyId("Probe-001"), position.StrategyId);

        // The position stands on a real fill, under an order of its own, so the account and the strategy agree on why it exists.
        Core.Model.Orders.Order adopted = Assert.Single(await rig.Node.Loop.InvokeAsync(() => rig.Node.Kernel.Cache.Orders(instrumentId: id)).WaitAsync(_timeout));
        Assert.StartsWith("RECON-POS-", adopted.ClientOrderId.Value, StringComparison.Ordinal);
        Assert.Equal((0.25m, 61_000m), (adopted.FilledQuantity.Value, adopted.AvgPx));
    }

    [Fact]
    public async Task A_reported_position_the_engine_already_holds_is_left_alone()
    {
        InstrumentId id = TestInstruments.BtcUsdt("FAKE").Id;
        ExecutionMassStatus flat = new(new ClientId("FAKE-EXEC"), new AccountId("FAKE-001"), new Venue("FAKE"), [], [],
            [new PositionStatusReport(new AccountId("FAKE-001"), id, PositionSide.Long, new Quantity(0m, 5), new UnixNanos(1), new UnixNanos(1), Guid.NewGuid(), AvgPxOpen: 61_000m)],
            new UnixNanos(1), Guid.NewGuid());

        await using Rig rig = await StartAsync(exec => exec.MassStatus = flat, claims: [id]);

        Assert.Empty(await rig.Node.Loop.InvokeAsync(() => rig.Node.Kernel.Cache.PositionsOpen(instrumentId: id)).WaitAsync(_timeout));
    }

    [Fact]
    public async Task A_reported_position_with_no_price_anywhere_is_not_invented()
    {
        InstrumentId id = TestInstruments.BtcUsdt("FAKE").Id;
        ExecutionMassStatus priceless = new(new ClientId("FAKE-EXEC"), new AccountId("FAKE-001"), new Venue("FAKE"), [], [],
            [new PositionStatusReport(new AccountId("FAKE-001"), id, PositionSide.Short, new Quantity(1m, 5), new UnixNanos(1), new UnixNanos(1), Guid.NewGuid())],
            new UnixNanos(1), Guid.NewGuid());

        await using Rig rig = await StartAsync(exec => exec.MassStatus = priceless, claims: [id]);

        Assert.Empty(await rig.Node.Loop.InvokeAsync(() => rig.Node.Kernel.Cache.PositionsOpen(instrumentId: id)).WaitAsync(_timeout));
    }
}
