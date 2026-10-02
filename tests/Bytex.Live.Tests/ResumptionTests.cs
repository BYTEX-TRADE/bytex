using System.Globalization;
using System.Text;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Plugins;
using Bytex.Core.Trading;
using Bytex.Live.Persistence;
using Bytex.Live.Tests.Support;

namespace Bytex.Live.Tests;

// Why: a node asked to stop saved what its strategies knew. A node that was KILLED saved nothing, because SaveState
// had exactly one call site and it was inside StopAsync. So a container stop, a service manager, a crash, a power cut
// - and the host that kills this process after its own shutdown timeout, which is ordinary use - all brought a
// strategy back having forgotten its phase, its ladder and its anchors, while reconciliation handed it back the
// position it had been holding. A different strategy holding somebody's money, and nothing about it looks wrong.
//
// The guarantee is the one worth engineering against and it needs no venue, no key and no Redis: **what survives a
// kill is what survives a polite stop.** These tests get at it the only honest way from inside a test process - by
// asserting that the state is on disk BEFORE anything asks the node to stop, because that is exactly what a kill at
// that instant would have left behind.
public sealed class ResumptionTests : IDisposable
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bytex-resume-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A temporary directory that outlives one test run is not worth failing it over.
        }
    }

    /// <summary>
    /// The smallest node that starts: fake data and execution clients, one instrument. The same shape
    /// TradingNodeTests uses, kept here rather than shared because these tests need their own store directory and a
    /// rig that outlives one of them would hand state between them.
    /// </summary>
    private sealed class Rig
    {
        public Rig()
        {
            Registry.AddDataClientFactory(new FakeDataClientFactory(Journal));
            Registry.AddExecutionClientFactory(new FakeExecutionClientFactory(Journal));
        }

        public Journal Journal { get; } = new();

        public PluginRegistry Registry { get; } = new();

        public static TradingNodeConfig Config() => new()
        {
            TradingRuntime = new Core.TradingRuntime.TradingRuntimeConfig { Environment = Core.Model.TradingEnvironment.Live, ModuleHostId = new ModuleHostId("TESTER-001") },
            DataClients = [new ClientEntry("FAKE", "FAKE-DATA", new FakeDataClientConfig { Label = "typed" })],
            ExecutionClients = [new ClientEntry("FAKE", "FAKE-EXEC", new FakeExecutionClientConfig { Label = "typed" })],
            HeartbeatInterval = TimeSpan.Zero,
        };

        public TradingNode Node(TradingNodeConfig config)
        {
            TradingNode node = new(config, Registry);
            node.AddInstrument(TestInstruments.BtcUsdt("FAKE"));
            return node;
        }
    }

    /// <summary>A strategy whose whole memory is one number, so what survives is unambiguous.</summary>
    private sealed class RememberingStrategy : Strategy
    {
        public RememberingStrategy()
            : base(new StrategyConfig { StrategyId = new StrategyId("REMEMBER-001") })
        {
        }

        public int Phase { get; private set; }

        public void Advance() => Phase++;

        protected override IDictionary<string, byte[]> OnSave() =>
            new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["phase"] = Encoding.UTF8.GetBytes(Phase.ToString(CultureInfo.InvariantCulture)),
            };

        protected override void OnLoad(IDictionary<string, byte[]> state)
        {
            if (state.TryGetValue("phase", out byte[]? value))
            {
                Phase = int.Parse(Encoding.UTF8.GetString(value), CultureInfo.InvariantCulture);
            }
        }
    }

    private TradingNodeConfig Config(TimeSpan saveInterval, bool restore = true) => Rig.Config() with
    {
        Store = new NodeStoreConfig
        {
            Directory = _directory,
            SaveInterval = saveInterval,
            RestoreState = restore,
        },
    };

    private string StatePath => Path.Combine(_directory, "state", "REMEMBER-001.json");

    private string ClockPath => Path.Combine(_directory, "state", "node.clock.json");

    [Fact]
    public async Task What_a_strategy_knows_reaches_the_disk_before_anything_asks_the_node_to_stop()
    {
        // The whole defect in one assertion. Everything below depends on this being true, and until the periodic save
        // existed it was false: the file did not appear until StopAsync, so a kill before that left nothing at all.
        Rig rig = new();
        await using TradingNode node = rig.Node(Config(TimeSpan.FromMilliseconds(50)));
        RememberingStrategy strategy = new();
        node.AddStrategy(strategy);
        await node.StartAsync().WaitAsync(_timeout);

        strategy.Advance();
        strategy.Advance();
        strategy.Advance();

        await WaitUntilAsync(() => Saved() == 3);

        // Asserted while the node is still running and has been told nothing: a kill at this instant loses none of it.
        Assert.True(node.IsRunning);
        Assert.Equal(3, Saved());

        await node.StopAsync().WaitAsync(_timeout);
    }

    [Fact]
    public async Task A_node_started_over_that_state_comes_back_knowing_it()
    {
        Rig first = new();
        await using (TradingNode before = first.Node(Config(TimeSpan.FromMilliseconds(50))))
        {
            RememberingStrategy strategy = new();
            before.AddStrategy(strategy);
            await before.StartAsync().WaitAsync(_timeout);
            strategy.Advance();
            strategy.Advance();
            await WaitUntilAsync(() => Saved() == 2);
            await before.StopAsync().WaitAsync(_timeout);
        }

        Rig second = new();
        await using TradingNode resumed = second.Node(Config(TimeSpan.FromMilliseconds(50)));
        RememberingStrategy after = new();
        resumed.AddStrategy(after);
        await resumed.StartAsync().WaitAsync(_timeout);

        Assert.Equal(2, after.Phase);
        await resumed.StopAsync().WaitAsync(_timeout);
    }

    [Fact]
    public async Task A_node_with_nothing_saved_yet_starts_clean_rather_than_refusing()
    {
        // Resumption is offered, never required. A node killed before its first save has nothing to read and must
        // start rather than fail, or the feature would turn a lost memory into a node that will not run.
        Rig rig = new();
        await using TradingNode node = rig.Node(Config(TimeSpan.FromHours(1)));
        RememberingStrategy strategy = new();
        node.AddStrategy(strategy);
        await node.StartAsync().WaitAsync(_timeout);

        Assert.True(node.IsRunning);
        Assert.Equal(0, strategy.Phase);
        await node.StopAsync().WaitAsync(_timeout);
    }

    [Fact]
    public async Task A_host_that_says_not_to_restore_is_obeyed()
    {
        Rig first = new();
        await using (TradingNode before = first.Node(Config(TimeSpan.FromMilliseconds(50))))
        {
            RememberingStrategy strategy = new();
            before.AddStrategy(strategy);
            await before.StartAsync().WaitAsync(_timeout);
            strategy.Advance();
            await WaitUntilAsync(() => Saved() == 1);
            await before.StopAsync().WaitAsync(_timeout);
        }

        Rig second = new();
        await using TradingNode fresh = second.Node(Config(TimeSpan.FromMilliseconds(50), restore: false));
        RememberingStrategy after = new();
        fresh.AddStrategy(after);
        await fresh.StartAsync().WaitAsync(_timeout);

        Assert.Equal(0, after.Phase);
        await fresh.StopAsync().WaitAsync(_timeout);
    }

    [Fact]
    public async Task The_schedules_the_node_keeps_are_written_where_the_next_one_reads_them()
    {
        // The other half of "indistinguishable": not what a strategy remembers, but when it next acts. The rule
        // itself - phase kept, missed firings skipped - is pinned in TimerPhaseTests; what is asserted here is that
        // this node saves its repeating timers at all, and files them apart from any runtimeModule's state.
        Rig rig = new();
        await using TradingNode node = rig.Node(Config(TimeSpan.FromMilliseconds(50)) with
        {
            ReconciliationInterval = TimeSpan.FromMinutes(5),
        });

        await node.StartAsync().WaitAsync(_timeout);
        await WaitUntilAsync(() => File.Exists(ClockPath));

        string saved = File.ReadAllText(ClockPath);

        Assert.Contains("node-reconcile", saved, StringComparison.Ordinal);
        Assert.Contains("node-save", saved, StringComparison.Ordinal);

        await node.StopAsync().WaitAsync(_timeout);
    }

    /// <summary>The phase as it stands on disk, or -1 where nothing has been written.</summary>
    private int Saved()
    {
        if (!File.Exists(StatePath))
        {
            return -1;
        }

        try
        {
            Dictionary<string, string>? encoded = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(StatePath));
            return encoded is not null && encoded.TryGetValue("phase", out string? value)
                ? int.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(value)), CultureInfo.InvariantCulture)
                : -1;
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or FormatException)
        {
            // Caught mid-write; the next poll reads the finished file.
            return -1;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> until)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + _timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (until())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }

        Assert.Fail("the condition never came about");
    }
}
