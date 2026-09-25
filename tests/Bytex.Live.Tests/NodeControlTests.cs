using System.Text.Json;
using Bytex.Core.Kernel;
using Bytex.Core.Model;
using Bytex.Core.Engines;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Instruments;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Plugins;
using Bytex.Core.Trading;
using Bytex.Live.Control;
using Bytex.Live.Sandbox;
using Bytex.Live.Tests.Support;

namespace Bytex.Live.Tests;

// Why: the control channel is how anything outside the process supervises a node without holding its keys. What a host
// can rely on is pinned here: the handshake and its protocol version, heartbeats, a status on request, a view of what
// the node holds, and the commands that cancel, flatten and stop it.
public sealed class NodeControlTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(20);

    /// <summary>A strategy that tells a monitor what it is doing, which is what the view carries per strategy.</summary>
    private sealed class WatchedStrategy : Strategy, IStrategyMonitorView
    {
        public WatchedStrategy(InstrumentId instrumentId)
            : base(new StrategyConfig { StrategyId = new StrategyId("Watched-001"), ExternalOrderClaims = [instrumentId] })
        {
        }

        public OrderFactory Orders => OrderFactory;

        public void Submit(Order order) => SubmitOrder(order);

        public object? MonitorView() => new { waitingFor = "a higher close", barsSeen = 42 };
    }

    private sealed record Rig(TradingNode Node, WatchedStrategy Strategy, FakeDataClient Data, FakeExecutionClient Exec, Instrument Instrument, NodeControlServer Server, string Channel) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            await Node.DisposeAsync();
        }
    }

    private static async Task<Rig> StartAsync(TimeSpan? heartbeat = null, string? channel = null, bool displayPrices = false, TimeSpan? reconcileInterval = null)
    {
        Journal journal = new();
        PluginRegistry registry = new();
        FakeDataClientFactory dataFactory = new(journal);
        FakeExecutionClientFactory execFactory = new(journal) { Configure = exec => exec.MarketFillPrice = 50_000m };
        registry.AddDataClientFactory(dataFactory);
        registry.AddExecutionClientFactory(execFactory);
        Instrument instrument = TestInstruments.BtcUsdt("FAKE");

        TradingNode node = new(new TradingNodeConfig
        {
            Kernel = new KernelConfig { Environment = TradingEnvironment.Live, TraderId = new TraderId("TESTER-001") },
            DataClients = [new ClientEntry("FAKE", "FAKE-DATA", new FakeDataClientConfig())],
            ExecutionClients = [new ClientEntry("FAKE", "FAKE-EXEC", new FakeExecutionClientConfig())],
            HeartbeatInterval = TimeSpan.Zero,
            DisplayPrices = displayPrices,
            ReconciliationInterval = reconcileInterval ?? TimeSpan.Zero,
        }, registry);
        node.AddInstrument(instrument);
        WatchedStrategy strategy = new(instrument.Id);
        node.AddStrategy(strategy);

        channel ??= "ctl-" + Guid.NewGuid().ToString("N")[..8];
        NodeControlServer server = new(channel, node, heartbeat ?? TimeSpan.FromMinutes(10));
        server.Start();
        await node.StartAsync().WaitAsync(_timeout);
        return new Rig(node, strategy, dataFactory.Created.Single().Client, execFactory.Created.Single().Client, instrument, server, channel);
    }

    // The read is cancelled through the token the enumerator was created with, so a missing message fails the test
    // instead of leaving a read pending behind it.
    private static async Task<JsonElement> NextAsync(IAsyncEnumerator<ControlMessage> messages, string type)
    {
        while (await messages.MoveNextAsync())
        {
            if (messages.Current.Type == type)
            {
                return messages.Current.Payload?.Clone() ?? default;
            }
        }

        throw new Xunit.Sdk.XunitException($"the channel closed before a '{type}' message");
    }

    /// <summary>Puts a book on the venue, so that an order resting at a price has something quoted ahead of it.</summary>
    private static async Task BookAsync(Rig rig, decimal price, decimal size)
    {
        await rig.Node.Loop.InvokeAsync(() =>
        {
            UnixNanos ts = rig.Node.Kernel.Clock.Timestamp;
            rig.Data.Emit(new OrderBookDeltas(
                rig.Instrument.Id,
                [new OrderBookDelta(
                    rig.Instrument.Id,
                    BookAction.Update,
                    new BookOrder(OrderSide.Buy, rig.Instrument.MakePrice(price), rig.Instrument.MakeQuantity(size), 1UL),
                    RecordFlags.None,
                    1UL,
                    ts,
                    ts)],
                RecordFlags.None,
                1UL,
                ts,
                ts));
        }).WaitAsync(_timeout);
    }

    /// <summary>A stop-limit well above the market, so it rests at the venue without ever being triggered.</summary>
    private static async Task<Order> StopAsync(Rig rig)
    {
        Order order = await rig.Node.Loop.InvokeAsync(() =>
        {
            StopLimitOrder stop = rig.Strategy.Orders.StopLimit(
                rig.Instrument.Id, OrderSide.Buy, rig.Instrument.MakeQuantity(0.01m), rig.Instrument.MakePrice(60_000m), rig.Instrument.MakePrice(59_000m));
            rig.Strategy.Submit(stop);
            return (Order)stop;
        }).WaitAsync(_timeout);

        DateTime deadline = DateTime.UtcNow + _timeout;
        while (DateTime.UtcNow < deadline && await rig.Node.Loop.InvokeAsync(() => rig.Node.Kernel.Cache.OrdersOpen(instrumentId: rig.Instrument.Id).Count).WaitAsync(_timeout) == 0)
        {
            await Task.Delay(10);
        }

        return order;
    }

    private static async Task<Order> BuyAsync(Rig rig)
    {
        Order order = await rig.Node.Loop.InvokeAsync(() =>
        {
            LimitOrder limit = rig.Strategy.Orders.Limit(rig.Instrument.Id, OrderSide.Buy, rig.Instrument.MakeQuantity(0.01m), rig.Instrument.MakePrice(49_000m));
            rig.Strategy.Submit(limit);
            return (Order)limit;
        }).WaitAsync(_timeout);

        DateTime deadline = DateTime.UtcNow + _timeout;
        while (DateTime.UtcNow < deadline && await rig.Node.Loop.InvokeAsync(() => rig.Node.Kernel.Cache.OrdersOpen(instrumentId: rig.Instrument.Id).Count).WaitAsync(_timeout) == 0)
        {
            await Task.Delay(10);
        }

        return order;
    }

    [Fact]
    public async Task A_client_is_greeted_with_the_protocol_version_and_the_node_it_reached()
    {
        await using Rig rig = await StartAsync();
        await using NodeControlClient client = await NodeControlClient.ConnectAsync(rig.Channel, _timeout);
        using CancellationTokenSource limit = new(_timeout);
        await using IAsyncEnumerator<ControlMessage> messages = client.ReadAsync(limit.Token).GetAsyncEnumerator();

        JsonElement hello = await NextAsync(messages, ControlProtocol.Hello);

        Assert.Equal(ControlProtocol.Version, hello.GetProperty("version").GetInt32());
        Assert.Equal("TESTER-001", hello.GetProperty("traderId").GetString());
    }

    [Fact]
    public async Task A_client_may_connect_before_the_node_is_there_and_is_greeted_when_it_arrives()
    {
        // The normal case for a host: it starts a node and connects to it. The node needs a moment to come up, and on
        // a Unix socket that moment is a connection refused rather than a wait, so the client has to keep asking -
        // which is what the timeout is for. A named pipe waits by itself, so this is the same promise on both.
        string channel = "ctl-" + Guid.NewGuid().ToString("N")[..8];
        Task<NodeControlClient> connecting = NodeControlClient.ConnectAsync(channel, _timeout);

        await using Rig rig = await StartAsync(channel: channel);
        await using NodeControlClient client = await connecting.WaitAsync(_timeout);
        using CancellationTokenSource limit = new(_timeout);
        await using IAsyncEnumerator<ControlMessage> messages = client.ReadAsync(limit.Token).GetAsyncEnumerator();

        JsonElement hello = await NextAsync(messages, ControlProtocol.Hello);

        Assert.Equal("TESTER-001", hello.GetProperty("traderId").GetString());
    }

    [Fact]
    public async Task A_channel_nobody_serves_is_waited_for_and_then_given_up_on()
    {
        // Given up on when the window closes, not at the first refusal: a client that returned immediately would tell
        // a host the node failed to start when it had not finished starting.
        TimeSpan window = TimeSpan.FromSeconds(2);
        System.Diagnostics.Stopwatch waited = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<Exception>(() => NodeControlClient.ConnectAsync("ctl-nobody-" + Guid.NewGuid().ToString("N")[..8], window));

        Assert.True(waited.Elapsed >= window - TimeSpan.FromMilliseconds(500), $"gave up after {waited.Elapsed} of a {window} window");
    }

    [Fact]
    public async Task Heartbeats_arrive_on_their_interval_and_say_the_node_is_running()
    {
        await using Rig rig = await StartAsync(heartbeat: TimeSpan.FromMilliseconds(100));
        await using NodeControlClient client = await NodeControlClient.ConnectAsync(rig.Channel, _timeout);
        using CancellationTokenSource limit = new(_timeout);
        await using IAsyncEnumerator<ControlMessage> messages = client.ReadAsync(limit.Token).GetAsyncEnumerator();

        await NextAsync(messages, ControlProtocol.Hello);
        JsonElement first = await NextAsync(messages, ControlProtocol.Heartbeat);
        JsonElement second = await NextAsync(messages, ControlProtocol.Heartbeat);

        Assert.True(first.GetProperty("running").GetBoolean());
        Assert.True(second.GetProperty("processed").GetInt64() >= first.GetProperty("processed").GetInt64());
    }

    [Fact]
    public async Task Status_answers_with_what_the_node_holds()
    {
        await using Rig rig = await StartAsync();
        await BuyAsync(rig);
        await using NodeControlClient client = await NodeControlClient.ConnectAsync(rig.Channel, _timeout);
        using CancellationTokenSource limit = new(_timeout);
        await using IAsyncEnumerator<ControlMessage> messages = client.ReadAsync(limit.Token).GetAsyncEnumerator();
        await NextAsync(messages, ControlProtocol.Hello);

        await client.RequestStatusAsync();
        JsonElement status = await NextAsync(messages, ControlProtocol.Status);

        Assert.True(status.GetProperty("running").GetBoolean());
        Assert.Equal("Live", status.GetProperty("environment").GetString());
        Assert.Equal(1, status.GetProperty("orders").GetInt32());
        Assert.Equal("Watched-001", Assert.Single(status.GetProperty("strategies").EnumerateArray()).GetProperty("id").GetString());
    }

    [Fact]
    public async Task View_carries_the_accounts_the_instruments_the_orders_and_what_each_strategy_says_about_itself()
    {
        await using Rig rig = await StartAsync();
        Order order = await BuyAsync(rig);
        await using NodeControlClient client = await NodeControlClient.ConnectAsync(rig.Channel, _timeout);
        using CancellationTokenSource limit = new(_timeout);
        await using IAsyncEnumerator<ControlMessage> messages = client.ReadAsync(limit.Token).GetAsyncEnumerator();
        await NextAsync(messages, ControlProtocol.Hello);

        await client.RequestViewAsync();
        JsonElement view = await NextAsync(messages, ControlProtocol.View);

        Assert.Equal("TESTER-001", view.GetProperty("traderId").GetString());
        Assert.True(view.GetProperty("running").GetBoolean());
        JsonElement strategy = Assert.Single(view.GetProperty("strategies").EnumerateArray());
        Assert.Equal("Watched-001", strategy.GetProperty("id").GetString());
        Assert.Equal("a higher close", strategy.GetProperty("document").GetProperty("waitingFor").GetString());
        Assert.Equal(42, strategy.GetProperty("document").GetProperty("barsSeen").GetInt32());
        Assert.Equal(order.ClientOrderId.Value, Assert.Single(strategy.GetProperty("orders").EnumerateArray()).GetProperty("clientOrderId").GetString());
        Assert.NotEmpty(view.GetProperty("accounts").EnumerateArray());
    }

    [Fact]
    public async Task A_working_order_says_where_it_stands_in_the_queue_at_its_price()
    {
        // Why R3.15 is on the wire and not only in the simulator: "it has not filled yet" and "it will not fill until
        // five more go through at that price" are the same row on a screen without this.
        await using Rig rig = await StartAsync();
        await BookAsync(rig, price: 49_000m, size: 5m);
        Order order = await BuyAsync(rig);
        await using NodeControlClient client = await NodeControlClient.ConnectAsync(rig.Channel, _timeout);
        using CancellationTokenSource limit = new(_timeout);
        await using IAsyncEnumerator<ControlMessage> messages = client.ReadAsync(limit.Token).GetAsyncEnumerator();
        await NextAsync(messages, ControlProtocol.Hello);

        await client.RequestViewAsync();
        JsonElement view = await NextAsync(messages, ControlProtocol.View);

        JsonElement row = Assert.Single(Assert.Single(view.GetProperty("strategies").EnumerateArray()).GetProperty("orders").EnumerateArray());
        Assert.Equal(order.ClientOrderId.Value, row.GetProperty("clientOrderId").GetString());
        Assert.Equal("5.00000", row.GetProperty("sizeAhead").GetString());
        Assert.Equal(1, row.GetProperty("queuePosition").GetInt32());

        // And it moves: a seller hitting that price serves the queue in front of ours, not ours.
        await rig.Node.Loop.InvokeAsync(() => rig.Data.Emit(new TradeTick(
            rig.Instrument.Id, rig.Instrument.MakePrice(49_000m), rig.Instrument.MakeQuantity(3m), AggressorSide.Seller,
            new TradeId("T-Q"), rig.Node.Kernel.Clock.Timestamp, rig.Node.Kernel.Clock.Timestamp))).WaitAsync(_timeout);

        await client.RequestViewAsync();
        JsonElement moved = await NextAsync(messages, ControlProtocol.View);

        JsonElement after = Assert.Single(Assert.Single(moved.GetProperty("strategies").EnumerateArray()).GetProperty("orders").EnumerateArray());
        Assert.Equal("2.00000", after.GetProperty("sizeAhead").GetString());
    }

    [Fact]
    public async Task A_level_that_shrank_moves_a_working_order_forwards_on_the_wire()
    {
        // The book is changed in place by whoever applies the deltas, so nothing hears about it unless that engine
        // says so. Without this the queue would be measured once, when the order joined, and never again.
        await using Rig rig = await StartAsync();
        await BookAsync(rig, price: 49_000m, size: 5m);
        await BuyAsync(rig);
        await BookAsync(rig, price: 49_000m, size: 2m);
        await using NodeControlClient client = await NodeControlClient.ConnectAsync(rig.Channel, _timeout);
        using CancellationTokenSource limit = new(_timeout);
        await using IAsyncEnumerator<ControlMessage> messages = client.ReadAsync(limit.Token).GetAsyncEnumerator();
        await NextAsync(messages, ControlProtocol.Hello);

        await client.RequestViewAsync();
        JsonElement view = await NextAsync(messages, ControlProtocol.View);

        JsonElement row = Assert.Single(Assert.Single(view.GetProperty("strategies").EnumerateArray()).GetProperty("orders").EnumerateArray());
        Assert.Equal("2.00000", row.GetProperty("sizeAhead").GetString());
    }

    [Fact]
    public async Task An_order_standing_in_no_queue_says_nothing_rather_than_saying_it_is_at_the_front()
    {
        // A stop waiting for its trigger is a promise to the venue, not size in the book. Reporting it at the front
        // of a queue it is not in is worse than reporting nothing: a host would draw it as about to fill.
        await using Rig rig = await StartAsync();
        await BookAsync(rig, price: 49_000m, size: 5m);
        await StopAsync(rig);
        await using NodeControlClient client = await NodeControlClient.ConnectAsync(rig.Channel, _timeout);
        using CancellationTokenSource limit = new(_timeout);
        await using IAsyncEnumerator<ControlMessage> messages = client.ReadAsync(limit.Token).GetAsyncEnumerator();
        await NextAsync(messages, ControlProtocol.Hello);

        await client.RequestViewAsync();
        JsonElement view = await NextAsync(messages, ControlProtocol.View);

        JsonElement row = Assert.Single(Assert.Single(view.GetProperty("strategies").EnumerateArray()).GetProperty("orders").EnumerateArray());
        Assert.False(row.TryGetProperty("sizeAhead", out _), "a stop waiting for its trigger was given a place in a queue");
        Assert.False(row.TryGetProperty("queuePosition", out _), "a stop waiting for its trigger was given a place in a queue");
    }

    [Fact]
    public async Task A_node_whose_venue_does_not_match_its_own_orders_leaves_venues_out()
    {
        // Null rather than an empty list, so a host can tell "no venue here matches anything itself" - every live
        // node - from "a venue that matches its own orders and has been sent nothing yet".
        await using Rig rig = await StartAsync();
        await using NodeControlClient client = await NodeControlClient.ConnectAsync(rig.Channel, _timeout);
        using CancellationTokenSource limit = new(_timeout);
        await using IAsyncEnumerator<ControlMessage> messages = client.ReadAsync(limit.Token).GetAsyncEnumerator();
        await NextAsync(messages, ControlProtocol.Hello);

        await client.RequestViewAsync();
        JsonElement view = await NextAsync(messages, ControlProtocol.View);

        Assert.False(view.TryGetProperty("venues", out _), "a live venue was reported as matching its own orders");
    }

    [Fact]
    public async Task A_view_carries_every_field_the_contract_promises_for_a_position_and_a_working_order()
    {
        // Why: this payload is the contract with anything outside the process, and a sweep found 32 of its 55 field
        // names never asserted by name anywhere - including every field of a position and an order, which is what a
        // monitor renders. A field renamed or dropped would break the host and leave this suite green.
        await using Rig rig = await StartAsync();

        // A filled market order gives the view a position to describe; a resting limit gives it a working order.
        await rig.Node.Loop.InvokeAsync(() =>
        {
            rig.Strategy.Submit(rig.Strategy.Orders.Market(rig.Instrument.Id, OrderSide.Buy, rig.Instrument.MakeQuantity(0.02m)));
        }).WaitAsync(_timeout);
        Order resting = await BuyAsync(rig);

        // A price to mark the open position at: without one there is nothing to report as unrealised, and the field
        // is left out rather than sent as a guess.
        await rig.Node.Loop.InvokeAsync(() =>
        {
            UnixNanos ts = rig.Node.Kernel.Clock.Timestamp;
            rig.Data.Emit(new QuoteTick(
                rig.Instrument.Id,
                rig.Instrument.MakePrice(50_500m),
                rig.Instrument.MakePrice(50_600m),
                rig.Instrument.MakeQuantity(1m),
                rig.Instrument.MakeQuantity(1m),
                ts,
                ts));
        }).WaitAsync(_timeout);

        DateTime deadline = DateTime.UtcNow + _timeout;
        while (DateTime.UtcNow < deadline && await rig.Node.Loop.InvokeAsync(() => rig.Node.Kernel.Cache.PositionsOpen().Count).WaitAsync(_timeout) == 0)
        {
            await Task.Delay(10);
        }

        await using NodeControlClient client = await NodeControlClient.ConnectAsync(rig.Channel, _timeout);
        using CancellationTokenSource limit = new(_timeout);
        await using IAsyncEnumerator<ControlMessage> messages = client.ReadAsync(limit.Token).GetAsyncEnumerator();
        await NextAsync(messages, ControlProtocol.Hello);

        await client.RequestViewAsync();
        JsonElement view = await NextAsync(messages, ControlProtocol.View);

        // The node's own description of itself.
        Assert.Equal("TESTER-001", view.GetProperty("traderId").GetString());
        Assert.True(view.GetProperty("running").GetBoolean());
        Assert.False(view.GetProperty("halted").GetBoolean());
        Assert.Equal("Active", view.GetProperty("tradingState").GetString());

        // The account, currency by currency, in the three amounts a reader needs to tell committed from available.
        JsonElement account = Assert.Single(view.GetProperty("accounts").EnumerateArray());
        Assert.False(string.IsNullOrWhiteSpace(account.GetProperty("accountId").GetString()));
        JsonElement[] balances = account.GetProperty("balances").EnumerateArray().ToArray();
        Assert.NotEmpty(balances);
        foreach (JsonElement balance in balances)
        {
            foreach (string field in new[] { "currency", "total", "free", "locked" })
            {
                Assert.True(balance.TryGetProperty(field, out JsonElement value), $"a balance carries no {field}");
                Assert.False(string.IsNullOrWhiteSpace(value.GetString()), $"a balance's {field} is empty");
            }
        }

        JsonElement strategy = Assert.Single(view.GetProperty("strategies").EnumerateArray());
        Assert.Equal("Watched-001", strategy.GetProperty("id").GetString());
        Assert.False(string.IsNullOrWhiteSpace(strategy.GetProperty("state").GetString()));
        Assert.NotEmpty(strategy.GetProperty("instruments").EnumerateArray());

        // The position. Every one of these was unasserted on the wire, and every one of them is a number somebody
        // reads as money or as size.
        JsonElement position = Assert.Single(strategy.GetProperty("positions").EnumerateArray());
        Assert.False(string.IsNullOrWhiteSpace(position.GetProperty("positionId").GetString()));
        Assert.Equal(rig.Instrument.Id.ToString(), position.GetProperty("instrumentId").GetString());
        Assert.Equal("Long", position.GetProperty("side").GetString());
        Assert.Equal("0.02000", position.GetProperty("quantity").GetString());
        Assert.Equal("50000.00", position.GetProperty("avgPxOpen").GetString());
        Assert.False(string.IsNullOrWhiteSpace(position.GetProperty("realizedPnl").GetString()));
        Assert.False(
            string.IsNullOrWhiteSpace(position.GetProperty("unrealizedPnl").GetString()),
            "the node holds a price for this instrument, so the open position has to be marked at it");
        Assert.False(string.IsNullOrWhiteSpace(position.GetProperty("currency").GetString()));
        Assert.True(position.GetProperty("tsOpened").GetInt64() > 0, "a position opened at the epoch");

        // The working order, including where it stands in the queue at its price.
        JsonElement order = Assert.Single(strategy.GetProperty("orders").EnumerateArray());
        Assert.Equal(resting.ClientOrderId.Value, order.GetProperty("clientOrderId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(order.GetProperty("venueOrderId").GetString()));
        Assert.Equal(rig.Instrument.Id.ToString(), order.GetProperty("instrumentId").GetString());
        Assert.Equal("Buy", order.GetProperty("side").GetString());
        Assert.Equal("Limit", order.GetProperty("type").GetString());
        Assert.Equal("Accepted", order.GetProperty("status").GetString());
        Assert.Equal("0.01000", order.GetProperty("quantity").GetString());
        Assert.Equal("0", order.GetProperty("filledQuantity").GetString());
        Assert.Equal("49000.00", order.GetProperty("price").GetString());
        Assert.False(order.GetProperty("reduceOnly").GetBoolean());
        Assert.Equal(JsonValueKind.Array, order.GetProperty("tags").ValueKind);
    }

    [Fact]
    public void Every_field_the_control_payloads_carry_is_asserted_by_name_somewhere()
    {
        // The guard on the wire. It reads the payload builders for the names they emit and fails when one is not
        // asserted by name in a test, because this contract is read by another process: a field renamed here breaks
        // a host, and nothing in a suite that never names the field would notice.
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "Bytex.Live", "Control", "NodeControl.cs")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        string source = File.ReadAllText(Path.Combine(dir!.FullName, "src", "Bytex.Live", "Control", "NodeControl.cs"));

        // The anonymous objects that become the payloads are indented deeply; their assignments are the field names.
        string[] fields = System.Text.RegularExpressions.Regex
            .Matches(source, @"^\s{12,}(?<name>[a-z]\w*) = .*,\s*$", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(m => m.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(fields.Length > 30, $"the sweep found only {fields.Length} payload fields");

        string tests = string.Join(
            "\n",
            new DirectoryInfo(Path.Combine(dir.FullName, "tests")).GetFiles("*.cs", SearchOption.AllDirectories)
                .Where(f => !f.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    && !f.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Select(f => File.ReadAllText(f.FullName)));

        string[] unasserted = fields.Where(f => !tests.Contains($"\"{f}\"", StringComparison.Ordinal)).ToArray();
        Assert.True(
            unasserted.Length == 0,
            $"the control payloads carry fields no test names: {string.Join(", ", unasserted)}. Another process reads "
            + "this contract, so a field nobody asserts is a field that can be renamed without a failure here.");
    }

    [Fact]
    public async Task A_view_of_a_paper_node_says_what_each_venue_is_matching_orders_against()
    {
        // The `venues` payload exists so a paper fill cannot be read as something it is not, and it had never been
        // checked on the wire - only through the venue's own C# surface. A host reads this, not that.
        Journal journal = new();
        PluginRegistry registry = new();
        FakeDataClientFactory dataFactory = new(journal);
        registry.AddDataClientFactory(dataFactory);
        registry.AddExecutionClientFactory(new SandboxExecutionClientFactory());
        Instrument instrument = TestInstruments.BtcUsdt("FAKE");

        await using TradingNode node = new(
            new TradingNodeConfig
            {
                Kernel = new KernelConfig { Environment = TradingEnvironment.Sandbox, TraderId = new TraderId("PAPER-001") },
                DataClients = [new ClientEntry("FAKE", "FAKE-DATA", new FakeDataClientConfig())],
                ExecutionClients =
                [
                    new ClientEntry("SANDBOX", "FAKE-SANDBOX", new SandboxExecutionClientConfig
                    {
                        Venue = "FAKE",
                        StartingBalances = ["1000000 USDT"],
                    }),
                ],
                HeartbeatInterval = TimeSpan.Zero,
            },
            registry);
        node.AddInstrument(instrument);
        string channel = "ctl-" + Guid.NewGuid().ToString("N")[..8];
        await using NodeControlServer server = new(channel, node, TimeSpan.FromMinutes(10));
        server.Start();
        await node.StartAsync().WaitAsync(_timeout);

        await using NodeControlClient client = await NodeControlClient.ConnectAsync(channel, _timeout);
        using CancellationTokenSource limit = new(_timeout);
        await using IAsyncEnumerator<ControlMessage> messages = client.ReadAsync(limit.Token).GetAsyncEnumerator();
        await NextAsync(messages, ControlProtocol.Hello);

        await client.RequestViewAsync();
        JsonElement view = await NextAsync(messages, ControlProtocol.View);

        JsonElement venue = Assert.Single(view.GetProperty("venues").EnumerateArray());
        Assert.Equal("FAKE", venue.GetProperty("venue").GetString());
        JsonElement held = Assert.Single(venue.GetProperty("instruments").EnumerateArray());
        Assert.Equal(instrument.Id.ToString(), held.GetProperty("instrumentId").GetString());

        // Nothing has been sent to this node, so the honest answer is "nothing" - not "book", which is what it wants
        // and has not got. That distinction is the reason the field exists.
        Assert.Equal("nothing", held.GetProperty("against").GetString());
        Assert.True(held.GetProperty("wantsBook").GetBoolean(), "a paper venue matches against the book by default");
        Assert.True(held.GetProperty("bookRequested").GetBoolean(), "the venue never asked its data client for depth");

        await node.StopAsync().WaitAsync(_timeout);
    }

    [Fact]
    public async Task Cancel_all_cancels_the_orders_of_every_strategy()
    {
        await using Rig rig = await StartAsync();
        await BuyAsync(rig);
        await using NodeControlClient client = await NodeControlClient.ConnectAsync(rig.Channel, _timeout);
        using CancellationTokenSource limit = new(_timeout);
        await using IAsyncEnumerator<ControlMessage> messages = client.ReadAsync(limit.Token).GetAsyncEnumerator();
        await NextAsync(messages, ControlProtocol.Hello);

        await client.CancelAllAsync();

        DateTime deadline = DateTime.UtcNow + _timeout;
        while (DateTime.UtcNow < deadline && rig.Exec.CancelAlls.Count == 0)
        {
            await Task.Delay(10);
        }

        Assert.NotEmpty(rig.Exec.CancelAlls);
    }

    private static async Task<Order> MarketBuyAsync(Rig rig)
    {
        Order order = await rig.Node.Loop.InvokeAsync(() =>
        {
            MarketOrder market = rig.Strategy.Orders.Market(rig.Instrument.Id, OrderSide.Buy, rig.Instrument.MakeQuantity(0.01m));
            rig.Strategy.Submit(market);
            return (Order)market;
        }).WaitAsync(_timeout);

        DateTime deadline = DateTime.UtcNow + _timeout;
        while (DateTime.UtcNow < deadline && await rig.Node.Loop.InvokeAsync(() => rig.Node.Kernel.Cache.PositionsOpen(instrumentId: rig.Instrument.Id).Count).WaitAsync(_timeout) == 0)
        {
            await Task.Delay(10);
        }

        return order;
    }

    [Fact]
    public async Task The_status_carries_the_price_of_every_instrument_the_node_was_asked_to_show()
    {
        // A monitor that draws a node from its heartbeat alone needs a price that moves between bars; a node that was
        // not asked to show one says nothing rather than an empty list, so a host can tell the two apart.
        await using Rig shown = await StartAsync(displayPrices: true);
        await using NodeControlClient client = await NodeControlClient.ConnectAsync(shown.Channel, _timeout);
        using CancellationTokenSource limit = new(_timeout);
        await using IAsyncEnumerator<ControlMessage> messages = client.ReadAsync(limit.Token).GetAsyncEnumerator();
        await NextAsync(messages, ControlProtocol.Hello);
        shown.Data.Emit(TestInstruments.Quote(shown.Instrument, 49_999m, 50_001m, Bytex.Core.Model.Primitives.UnixNanos.FromSeconds(1)));

        // The quote travels to the kernel thread, so it is waited for here rather than raced to the status.
        await shown.Node.Loop.InvokeAsync(() => { }).WaitAsync(_timeout);
        await client.RequestStatusAsync();
        JsonElement status = await NextAsync(messages, ControlProtocol.Status);

        JsonElement price = Assert.Single(status.GetProperty("prices").EnumerateArray());
        Assert.Equal(shown.Instrument.Id.ToString(), price.GetProperty("instrumentId").GetString());
        Assert.Equal("50000.00", price.GetProperty("lastPrice").GetProperty("price").GetString());
        Assert.Equal("quote", price.GetProperty("lastPrice").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task A_node_that_was_not_asked_to_show_prices_reports_none()
    {
        await using Rig rig = await StartAsync();
        await using NodeControlClient client = await NodeControlClient.ConnectAsync(rig.Channel, _timeout);
        using CancellationTokenSource limit = new(_timeout);
        await using IAsyncEnumerator<ControlMessage> messages = client.ReadAsync(limit.Token).GetAsyncEnumerator();
        await NextAsync(messages, ControlProtocol.Hello);

        await client.RequestStatusAsync();
        JsonElement status = await NextAsync(messages, ControlProtocol.Status);

        Assert.False(status.TryGetProperty("prices", out _), "a node showing no prices sends no prices");
    }

    [Fact]
    public async Task Halt_stops_the_node_trading_and_leaves_it_running()
    {
        // The switch: the node keeps its strategies, its data and its state, and places nothing until it is released.
        await using Rig rig = await StartAsync();
        await using NodeControlClient client = await NodeControlClient.ConnectAsync(rig.Channel, _timeout);
        using CancellationTokenSource limit = new(_timeout);
        await using IAsyncEnumerator<ControlMessage> messages = client.ReadAsync(limit.Token).GetAsyncEnumerator();
        await NextAsync(messages, ControlProtocol.Hello);

        await client.HaltAsync();
        JsonElement halted = await NextAsync(messages, ControlProtocol.Status);

        Assert.True(halted.GetProperty("halted").GetBoolean());
        Assert.Equal("Halted", halted.GetProperty("tradingState").GetString());
        Assert.True(halted.GetProperty("running").GetBoolean(), "a halted node is still running");

        // What a strategy submits from here is denied, and the venue never sees it.
        Order denied = await SubmitAsync(rig);
        Assert.Equal(OrderStatus.Denied, await StatusOfAsync(rig, denied));
        Assert.DoesNotContain(rig.Exec.Submitted, c => c.Order.ClientOrderId == denied.ClientOrderId);

        await client.ResumeAsync();
        JsonElement released = await NextAsync(messages, ControlProtocol.Status);

        Assert.False(released.GetProperty("halted").GetBoolean());
        Assert.Equal("Active", released.GetProperty("tradingState").GetString());

        Order accepted = await BuyAsync(rig);
        Assert.NotEqual(OrderStatus.Denied, await StatusOfAsync(rig, accepted));
    }

    [Fact]
    public async Task Halt_can_take_the_book_off_the_venue_on_its_way_down()
    {
        // A panic button that left the position open would not be one: the close has to reach the venue although the
        // node is halting, which it does because the halt comes into force after it.
        await using Rig rig = await StartAsync();
        await MarketBuyAsync(rig);
        await BuyAsync(rig);
        await using NodeControlClient client = await NodeControlClient.ConnectAsync(rig.Channel, _timeout);
        using CancellationTokenSource limit = new(_timeout);
        await using IAsyncEnumerator<ControlMessage> messages = client.ReadAsync(limit.Token).GetAsyncEnumerator();
        await NextAsync(messages, ControlProtocol.Hello);

        await client.HaltAsync(cancelOrders: true, closePositions: true);
        JsonElement halted = await NextAsync(messages, ControlProtocol.Status);

        Assert.True(halted.GetProperty("halted").GetBoolean());
        DateTime deadline = DateTime.UtcNow + _timeout;
        while (DateTime.UtcNow < deadline && !rig.Exec.Submitted.Any(c => c.Order.IsReduceOnly))
        {
            await Task.Delay(10);
        }

        SubmitOrder close = Assert.Single(rig.Exec.Submitted, c => c.Order.IsReduceOnly);
        Assert.Equal(OrderSide.Sell, close.Order.Side);
        Assert.NotEmpty(rig.Exec.CancelAlls);
    }

    [Fact]
    public async Task A_host_sets_the_nodes_limits_over_the_channel_and_reads_back_what_it_set()
    {
        await using Rig rig = await StartAsync();
        await using NodeControlClient client = await NodeControlClient.ConnectAsync(rig.Channel, _timeout);
        using CancellationTokenSource limit = new(_timeout);
        await using IAsyncEnumerator<ControlMessage> messages = client.ReadAsync(limit.Token).GetAsyncEnumerator();
        await NextAsync(messages, ControlProtocol.Hello);

        await client.SetLimitsAsync(new RiskLimits
        {
            MaxLossPerPeriod = RiskLimit.Parse("1000 USDT"),
            MaxExposure = RiskLimit.Parse("50%"),
            MaxWorkingOrders = 1,
        });
        JsonElement status = await NextAsync(messages, ControlProtocol.Status);

        JsonElement limits = status.GetProperty("limits");
        Assert.Equal(new Money(1_000m, Currencies.USDT).ToString(), limits.GetProperty("maxLossPerPeriod").GetString());
        Assert.Equal("50%", limits.GetProperty("maxExposure").GetString());
        Assert.Equal(1, limits.GetProperty("maxWorkingOrders").GetInt32());

        // And the node enforces them: one order works, the next is over the cap it was just given.
        await BuyAsync(rig);
        Order beyond = await SubmitAsync(rig);

        Assert.Equal(OrderStatus.Denied, await StatusOfAsync(rig, beyond));
    }

    /// <summary>Submits a limit order and returns it without waiting for the venue: a denied order never reaches one.</summary>
    private static Task<Order> SubmitAsync(Rig rig) => rig.Node.Loop.InvokeAsync(() =>
    {
        LimitOrder order = rig.Strategy.Orders.Limit(rig.Instrument.Id, OrderSide.Buy, rig.Instrument.MakeQuantity(0.01m), rig.Instrument.MakePrice(48_000m));
        rig.Strategy.Submit(order);
        return (Order)order;
    }).WaitAsync(_timeout);

    /// <summary>What the venue or the engine made of an order, once it is no longer merely on its way there.</summary>
    private static async Task<OrderStatus> StatusOfAsync(Rig rig, Order order)
    {
        DateTime deadline = DateTime.UtcNow + _timeout;
        while (DateTime.UtcNow < deadline)
        {
            OrderStatus status = await rig.Node.Loop.InvokeAsync(() => rig.Node.Kernel.Cache.Order(order.ClientOrderId)?.Status ?? order.Status).WaitAsync(_timeout);
            if (status is not OrderStatus.Initialized and not OrderStatus.Submitted)
            {
                return status;
            }

            await Task.Delay(10);
        }

        return order.Status;
    }

    [Fact]
    public async Task Flatten_cancels_the_working_orders_and_closes_the_open_positions()
    {
        // The difference between flatten and cancel-all: a host that has decided to get out needs the position gone,
        // not only the orders that were waiting to add to it.
        await using Rig rig = await StartAsync();
        await MarketBuyAsync(rig);
        Order resting = await BuyAsync(rig);
        await using NodeControlClient client = await NodeControlClient.ConnectAsync(rig.Channel, _timeout);
        using CancellationTokenSource limit = new(_timeout);
        await using IAsyncEnumerator<ControlMessage> messages = client.ReadAsync(limit.Token).GetAsyncEnumerator();
        await NextAsync(messages, ControlProtocol.Hello);

        await client.FlattenAsync();

        DateTime deadline = DateTime.UtcNow + _timeout;
        while (DateTime.UtcNow < deadline && !rig.Exec.Submitted.Any(c => c.Order.IsReduceOnly))
        {
            await Task.Delay(10);
        }

        SubmitOrder close = Assert.Single(rig.Exec.Submitted, c => c.Order.IsReduceOnly);
        Assert.Equal(OrderSide.Sell, close.Order.Side);
        Assert.Equal(rig.Instrument.MakeQuantity(0.01m), close.Order.Quantity);
        Assert.Equal(OrderType.Market, close.Order.Type);
        Assert.NotEmpty(rig.Exec.CancelAlls); // the resting order goes too
        Assert.Equal(rig.Instrument.Id, resting.InstrumentId);
    }

    [Fact]
    public async Task Stop_asks_the_host_to_stop_and_says_whether_to_cancel_and_close()
    {
        await using Rig rig = await StartAsync();
        TaskCompletionSource<(bool Cancel, bool Close)> asked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Server.StopRequested += (cancel, close) => asked.TrySetResult((cancel, close));
        await using NodeControlClient client = await NodeControlClient.ConnectAsync(rig.Channel, _timeout);
        using CancellationTokenSource limit = new(_timeout);
        await using IAsyncEnumerator<ControlMessage> messages = client.ReadAsync(limit.Token).GetAsyncEnumerator();
        await NextAsync(messages, ControlProtocol.Hello);

        await client.StopAsync(cancelOrders: true, closePositions: true);

        Assert.Equal((true, true), await asked.Task.WaitAsync(_timeout));
    }

    [Fact]
    public async Task Order_events_reach_a_connected_client_as_they_happen()
    {
        await using Rig rig = await StartAsync();
        await using NodeControlClient client = await NodeControlClient.ConnectAsync(rig.Channel, _timeout);
        using CancellationTokenSource limit = new(_timeout);
        await using IAsyncEnumerator<ControlMessage> messages = client.ReadAsync(limit.Token).GetAsyncEnumerator();
        await NextAsync(messages, ControlProtocol.Hello);

        await BuyAsync(rig);
        JsonElement e = await NextAsync(messages, ControlProtocol.Event);

        // Every event says which channel it came from and carries the engine's own shape of it.
        Assert.False(string.IsNullOrWhiteSpace(e.GetProperty("channel").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(e.GetProperty("data").GetProperty("kind").GetString()));
    }

    // Why: a Unix domain socket address is a path of at most about a hundred characters, and on macOS the temporary
    // directory alone takes half of that. A node was refused for the length of its own channel name, in a background
    // task, so it surfaced only when something disposed the server.
    [Fact]
    public async Task A_channel_name_too_long_for_a_socket_path_still_serves()
    {
        string channel = "a-node-name-far-longer-than-any-socket-path-allows-" + Guid.NewGuid().ToString("N");
        await using Rig rig = await StartAsync(channel: channel);
        await using NodeControlClient client = await NodeControlClient.ConnectAsync(channel, _timeout);
        using CancellationTokenSource limit = new(_timeout);
        await using IAsyncEnumerator<ControlMessage> messages = client.ReadAsync(limit.Token).GetAsyncEnumerator();

        JsonElement hello = await NextAsync(messages, ControlProtocol.Hello);

        Assert.Equal(ControlProtocol.Version, hello.GetProperty("version").GetInt32());
        if (!ControlTransport.UseNamedPipes)
        {
            // Both ends agree on the shortened address, and it fits.
            Assert.True(ControlTransport.SocketPath(channel).Length <= 104, ControlTransport.SocketPath(channel));
            Assert.Equal(ControlTransport.SocketPath(channel), ControlTransport.SocketPath(channel));
        }
    }

    // Why: the command handler wrote its reply to the same StreamWriter the heartbeat and event writer was using, and a
    // StreamWriter throws the moment two writes overlap. A command arriving during a heartbeat took the session down.
    [Fact]
    public async Task Commands_during_a_stream_of_heartbeats_are_all_answered()
    {
        await using Rig rig = await StartAsync(heartbeat: TimeSpan.FromMilliseconds(5));
        await using NodeControlClient client = await NodeControlClient.ConnectAsync(rig.Channel, _timeout);
        using CancellationTokenSource limit = new(_timeout);
        await using IAsyncEnumerator<ControlMessage> messages = client.ReadAsync(limit.Token).GetAsyncEnumerator();
        await NextAsync(messages, ControlProtocol.Hello);

        const int Commands = 25;
        for (int i = 0; i < Commands; i++)
        {
            await client.RequestStatusAsync();
            await client.RequestViewAsync();
        }

        int answers = 0;
        while (answers < Commands * 2 && await messages.MoveNextAsync())
        {
            if (messages.Current.Type is ControlProtocol.Status or ControlProtocol.View)
            {
                answers++;
            }
        }

        Assert.Equal(Commands * 2, answers);
    }

    [Fact]
    public async Task A_node_reports_what_it_has_made_of_its_venues_own_account_of_things()
    {
        // A host cannot see a node drifting apart from its venue unless the node says so. What it says is how many
        // times it has checked, how many differences it had to take the venue's word on, when it last looked, and
        // how often it means to look.
        await using Rig rig = await StartAsync(reconcileInterval: TimeSpan.FromMinutes(7));
        await using NodeControlClient client = await NodeControlClient.ConnectAsync(rig.Channel, _timeout);
        using CancellationTokenSource limit = new(_timeout);
        await using IAsyncEnumerator<ControlMessage> messages = client.ReadAsync(limit.Token).GetAsyncEnumerator();
        await NextAsync(messages, ControlProtocol.Hello);

        await client.RequestStatusAsync();
        JsonElement reconciliation = (await NextAsync(messages, ControlProtocol.Status)).GetProperty("reconciliation");

        Assert.Equal("00:07:00", reconciliation.GetProperty("interval").GetString());
        Assert.Equal(0, reconciliation.GetProperty("differences").GetInt64());

        // The fake venue reports nothing to reconcile against, so the node has looked and found nothing rather than
        // not looked: the count is what it did, the differences are what it found.
        Assert.Equal(0, reconciliation.GetProperty("count").GetInt64());
        Assert.False(reconciliation.TryGetProperty("lastTs", out JsonElement last) && last.ValueKind != JsonValueKind.Null,
            "a node that has reconciled nothing reports no time for it");
    }
}
