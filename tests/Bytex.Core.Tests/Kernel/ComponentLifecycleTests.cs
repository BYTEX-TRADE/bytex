using Bytex.Core.Common;
using Bytex.Core.Model;
using Bytex.Core.Model.Events;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Timing;
using Bytex.Core.Tests.Support;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bytex.Core.Tests.Kernel;

// Why: R1.4 - engines, clients, actors and strategies all share this state machine. Every legal transition
// and every refusal is pinned here, because a component in the wrong state silently drops market data.
public class ComponentLifecycleTests
{
    private sealed class Probe : Component
    {
        public Probe()
            : base(new ComponentId("Probe"))
        {
        }

        public List<string> Hooks { get; } = new();

        public string? ThrowIn { get; set; }

        public UnixNanos Now => Clock.Timestamp;

        protected override void OnStart() => Hook("OnStart");

        protected override void OnStop() => Hook("OnStop");

        protected override void OnResume() => Hook("OnResume");

        protected override void OnReset() => Hook("OnReset");

        protected override void OnDispose() => Hook("OnDispose");

        protected override void OnDegrade() => Hook("OnDegrade");

        protected override void OnFault() => Hook("OnFault");

        private void Hook(string name)
        {
            Hooks.Add(name);
            if (ThrowIn == name)
            {
                throw new InvalidOperationException(name + " failed");
            }
        }
    }

    private static Probe ReadyProbe(out List<string> transitions, UnixNanos? now = null)
    {
        Probe probe = new();
        List<string> log = new();
        probe.StateChanged += e => log.Add($"{e.PreviousState}->{e.State}");
        probe.Initialize(new TestClock(now ?? UnixNanos.Zero), NullLoggerFactory.Instance);
        log.Clear();
        transitions = log;
        return probe;
    }

    /// <summary>Drives a fresh probe into the requested resting state using only legal transitions.</summary>
    private static Probe ProbeIn(ComponentState state)
    {
        Probe probe = new();
        if (state == ComponentState.PreInitialized)
        {
            return probe;
        }

        probe.Initialize(new TestClock(), NullLoggerFactory.Instance);
        switch (state)
        {
            case ComponentState.Ready:
                break;
            case ComponentState.Running:
                probe.Start();
                break;
            case ComponentState.Stopped:
                probe.Start();
                probe.Stop();
                break;
            case ComponentState.Degraded:
                probe.Start();
                probe.Degrade();
                break;
            case ComponentState.Faulted:
                probe.Fault();
                break;
            case ComponentState.Disposed:
                probe.Dispose();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(state));
        }

        probe.Hooks.Clear();
        Assert.Equal(state, probe.State);
        return probe;
    }

    [Fact]
    public void New_component_is_pre_initialized_and_becomes_ready_when_initialized()
    {
        Probe probe = new();
        List<string> transitions = new();
        probe.StateChanged += e => transitions.Add($"{e.PreviousState}->{e.State}");

        Assert.Equal(ComponentState.PreInitialized, probe.State);
        Assert.False(probe.IsInitialized);
        probe.Initialize(new TestClock(), NullLoggerFactory.Instance);
        probe.Initialize(new TestClock(), NullLoggerFactory.Instance);

        Assert.Equal(ComponentState.Ready, probe.State);
        Assert.Equal(["PreInitialized->Ready"], transitions);
    }

    [Fact]
    public void Clock_is_unavailable_until_the_component_is_initialized()
    {
        Probe probe = new();

        Assert.Throws<InvalidOperationException>(() => probe.Now);
    }

    [Fact]
    public void Start_passes_through_starting_and_calls_the_hook_in_between()
    {
        Probe probe = ReadyProbe(out _);
        probe.StateChanged += e => probe.Hooks.Add($"{e.PreviousState}->{e.State}");

        probe.Start();

        // Hooks and transitions share one log here, so the position of OnStart is observable.
        Assert.Equal(["Ready->Starting", "OnStart", "Starting->Running"], probe.Hooks);
        Assert.True(probe.IsRunning);
    }

    [Fact]
    public void Full_cycle_start_stop_resume_stop_reset_walks_the_documented_states()
    {
        Probe probe = ReadyProbe(out List<string> transitions);

        probe.Start();
        probe.Stop();
        probe.Resume();
        probe.Stop();
        probe.Reset();

        Assert.Equal(
            [
                "Ready->Starting", "Starting->Running",
                "Running->Stopping", "Stopping->Stopped",
                "Stopped->Resuming", "Resuming->Running",
                "Running->Stopping", "Stopping->Stopped",
                "Stopped->Resetting", "Resetting->Ready",
            ],
            transitions);
        Assert.Equal(["OnStart", "OnStop", "OnResume", "OnStop", "OnReset"], probe.Hooks);
    }

    [Fact]
    public void Stopped_component_can_be_started_again()
    {
        Probe probe = ProbeIn(ComponentState.Stopped);

        probe.Start();

        Assert.Equal(ComponentState.Running, probe.State);
    }

    [Fact]
    public void Degraded_component_can_resume_or_stop()
    {
        Probe resumed = ProbeIn(ComponentState.Degraded);
        Probe stopped = ProbeIn(ComponentState.Degraded);

        resumed.Resume();
        stopped.Stop();

        Assert.True(resumed.IsRunning);
        Assert.True(stopped.IsStopped);
    }

    [Fact]
    public void Degrade_passes_through_degrading()
    {
        Probe probe = ReadyProbe(out List<string> transitions);
        probe.Start();
        transitions.Clear();

        probe.Degrade();

        Assert.Equal(["Running->Degrading", "Degrading->Degraded"], transitions);
        Assert.True(probe.IsDegraded);
        Assert.False(probe.IsRunning);
    }

    [Theory]
    [InlineData(ComponentState.PreInitialized, "start")]
    [InlineData(ComponentState.Running, "start")]
    [InlineData(ComponentState.Degraded, "start")]
    [InlineData(ComponentState.Faulted, "start")]
    [InlineData(ComponentState.Disposed, "start")]
    [InlineData(ComponentState.PreInitialized, "stop")]
    [InlineData(ComponentState.Ready, "stop")]
    [InlineData(ComponentState.Stopped, "stop")]
    [InlineData(ComponentState.Faulted, "stop")]
    [InlineData(ComponentState.Ready, "resume")]
    [InlineData(ComponentState.Running, "resume")]
    [InlineData(ComponentState.Faulted, "resume")]
    [InlineData(ComponentState.PreInitialized, "reset")]
    [InlineData(ComponentState.Running, "reset")]
    [InlineData(ComponentState.Degraded, "reset")]
    [InlineData(ComponentState.Faulted, "reset")]
    [InlineData(ComponentState.Disposed, "reset")]
    [InlineData(ComponentState.Ready, "degrade")]
    [InlineData(ComponentState.Stopped, "degrade")]
    [InlineData(ComponentState.Degraded, "degrade")]
    public void Illegal_transition_is_refused_and_leaves_the_state_alone(ComponentState from, string trigger)
    {
        Probe probe = ProbeIn(from);

        Action act = trigger switch
        {
            "start" => probe.Start,
            "stop" => probe.Stop,
            "resume" => probe.Resume,
            "reset" => probe.Reset,
            "degrade" => probe.Degrade,
            _ => throw new ArgumentOutOfRangeException(nameof(trigger)),
        };

        InvalidStateTransitionException e = Assert.Throws<InvalidStateTransitionException>(act);
        Assert.Contains(trigger, e.Message, StringComparison.Ordinal);
        Assert.Contains(from.ToString(), e.Message, StringComparison.Ordinal);
        Assert.Equal(from, probe.State);
        Assert.Empty(probe.Hooks);
    }

    [Theory]
    [InlineData(ComponentState.PreInitialized)]
    [InlineData(ComponentState.Ready)]
    [InlineData(ComponentState.Running)]
    [InlineData(ComponentState.Stopped)]
    [InlineData(ComponentState.Degraded)]
    public void Fault_is_possible_from_any_live_state(ComponentState from)
    {
        Probe probe = ProbeIn(from);

        probe.Fault();

        Assert.True(probe.IsFaulted);
        Assert.Equal(["OnFault"], probe.Hooks);
    }

    [Theory]
    [InlineData(ComponentState.Faulted)]
    [InlineData(ComponentState.Disposed)]
    public void Fault_on_a_faulted_or_disposed_component_does_nothing(ComponentState from)
    {
        Probe probe = ProbeIn(from);

        probe.Fault();

        Assert.Equal(from, probe.State);
        Assert.Empty(probe.Hooks);
    }

    [Fact]
    public void Exception_in_on_start_faults_the_component_and_is_rethrown()
    {
        Probe probe = ReadyProbe(out List<string> transitions);
        probe.ThrowIn = "OnStart";

        InvalidOperationException e = Assert.Throws<InvalidOperationException>(probe.Start);

        Assert.Equal("OnStart failed", e.Message);
        Assert.Equal(["Ready->Starting", "Starting->Faulting", "Faulting->Faulted"], transitions);
    }

    [Fact]
    public void Exception_in_on_stop_faults_the_component_and_is_rethrown()
    {
        Probe probe = ProbeIn(ComponentState.Running);
        probe.ThrowIn = "OnStop";

        Assert.Throws<InvalidOperationException>(probe.Stop);

        Assert.True(probe.IsFaulted);
    }

    [Fact]
    public void Exception_in_on_fault_still_ends_in_faulted()
    {
        Probe probe = ProbeIn(ComponentState.Running);
        probe.ThrowIn = "OnFault";

        Assert.Throws<InvalidOperationException>(probe.Fault);

        Assert.True(probe.IsFaulted);
    }

    [Theory]
    [InlineData(ComponentState.PreInitialized)]
    [InlineData(ComponentState.Ready)]
    [InlineData(ComponentState.Stopped)]
    [InlineData(ComponentState.Faulted)]
    public void Dispose_from_a_resting_state_calls_the_hook_once(ComponentState from)
    {
        Probe probe = ProbeIn(from);

        probe.Dispose();
        probe.Dispose();

        Assert.True(probe.IsDisposed);
        Assert.Equal(["OnDispose"], probe.Hooks);
    }

    [Theory]
    [InlineData(ComponentState.Running)]
    [InlineData(ComponentState.Degraded)]
    public void Dispose_of_an_active_component_stops_it_first(ComponentState from)
    {
        Probe probe = ProbeIn(from);

        probe.Dispose();

        Assert.True(probe.IsDisposed);
        Assert.Equal(["OnStop", "OnDispose"], probe.Hooks);
    }

    [Fact]
    public void State_change_events_carry_the_component_id_and_the_clock_time()
    {
        UnixNanos now = UnixNanos.Parse("2024-05-01T08:00:00Z");
        Probe probe = new();
        List<ComponentStateChanged> events = new();
        probe.StateChanged += events.Add;

        probe.Initialize(new TestClock(now), NullLoggerFactory.Instance);
        probe.Start();

        Assert.Equal(3, events.Count);
        Assert.All(events, e => Assert.Equal(new ComponentId("Probe"), e.ComponentId));
        Assert.All(events, e => Assert.Equal(now, e.TsEvent));
        Assert.Equal(3, events.Select(e => e.EventId).Distinct().Count());
    }

    [Fact]
    public void A_component_logs_under_its_own_identity()
    {
        // The category is how a log pipeline attributes a line to the part of the engine that wrote it, so a
        // component logs as itself rather than through a shared logger.
        using RecordingLoggerFactory logs = new();
        Probe probe = new() { ThrowIn = "OnStart" };
        probe.Initialize(new TestClock(UnixNanos.Zero), logs);

        Assert.Throws<InvalidOperationException>(probe.Start);

        (string Category, LogLevel Level, string Message, IReadOnlyList<KeyValuePair<string, object?>> Values) line =
            Assert.Single(logs.Lines, l => l.Message.Contains("failed to start", StringComparison.Ordinal));
        Assert.Equal(typeof(Probe).FullName, line.Category);
        Assert.Equal(LogLevel.Error, line.Level);
    }

    [Fact]
    public void A_components_log_line_carries_its_id_as_a_named_value_not_only_as_text()
    {
        // A structured sink stores the values, not the sentence: a line whose id is only interpolated into the text
        // cannot be searched for by component.
        using RecordingLoggerFactory logs = new();
        Probe probe = new() { ThrowIn = "OnStop" };
        probe.Initialize(new TestClock(UnixNanos.Zero), logs);
        probe.Start();

        Assert.Throws<InvalidOperationException>(probe.Stop);

        (string Category, LogLevel Level, string Message, IReadOnlyList<KeyValuePair<string, object?>> Values) line =
            Assert.Single(logs.Lines, l => l.Message.Contains("failed to stop", StringComparison.Ordinal));
        Assert.Contains(line.Values, v => v.Key == "Id" && string.Equals(v.Value?.ToString(), "Probe", StringComparison.Ordinal));
        Assert.Contains("Probe", line.Message, StringComparison.Ordinal);
    }
}
