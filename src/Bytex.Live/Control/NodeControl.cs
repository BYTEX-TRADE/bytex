using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Bytex.Core.Adapters;
using Bytex.Core.Model;
using Bytex.Core.Model.Identifiers;
using Bytex.Core.Model.Orders;
using Bytex.Core.Model.Positions;
using Bytex.Core.Model.Primitives;
using Bytex.Core.Engines;
using Bytex.Core.Model.Commands;
using Bytex.Core.Model.Data;
using Bytex.Core.Model.Events;
using Bytex.Core.Serialization;
using Bytex.Core.Trading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bytex.Live.Control;

/// <summary>
/// The node control protocol (design note 0009): newline-delimited JSON over a named pipe (Windows) or a Unix domain socket.
/// A node reports status and events; a host may ask it to stop, cancel all orders, flatten, report status, or send a view:
/// what each strategy holds and waits for, the last prices and the account balances.
/// No credential, configuration, or document ever crosses the channel.
/// </summary>
public static class ControlProtocol
{
    public const string Hello = "hello";
    public const string Heartbeat = "heartbeat";
    public const string Event = "event";
    public const string Status = "status";
    public const string Bye = "bye";
    public const string View = "view";

    public const string CmdStop = "stop";
    public const string CmdCancelAll = "cancel-all";
    public const string CmdFlatten = "flatten";
    public const string CmdStatus = "status";
    public const string CmdView = "view";
    public const string CmdHalt = "halt";
    public const string CmdResume = "resume";
    public const string CmdLimits = "limits";

    /// <summary>
    /// 5: <c>venues</c> in every view - what each venue that matches its own orders is matching them against, per
    /// instrument. A paper venue fills against a book when it has one and against quotes or bars when it does not,
    /// and a fill means different things in each case, so it says which.
    /// <para>
    /// 4: <c>sizeAhead</c> and <c>queuePosition</c> on every working order in a view - where it stands in the queue
    /// at its price. Both are null for an order standing in no queue, and <c>sizeAhead</c> is zero, not null, for one
    /// at the front of its queue.
    /// </para>
    /// <para>
    /// 3: the <c>halt</c>, <c>resume</c> and <c>limits</c> commands, and the trading state and the limits in every
    /// status and view. A host reads the version from <c>hello</c> before it relies on any of it.
    /// </para>
    /// </summary>
    public const int Version = 5;
}

public sealed record ControlMessage(string Type, JsonElement? Payload, long Ts)
{
    public static ControlMessage Of(string type, object? payload, long ts) =>
        new(type, payload is null ? null : JsonSerializer.SerializeToElement(payload, BytexJson.Options), ts);

    public string ToLine() => JsonSerializer.Serialize(this, BytexJson.Options);

    public static ControlMessage? Parse(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<ControlMessage>(line, BytexJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Creates the platform-appropriate duplex stream for a control channel name.</summary>
public static class ControlTransport
{
    private const int PipeBufferSize = 1 << 16;

    public static bool UseNamedPipes => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>
    /// A Unix domain socket address is a path with a hard limit of about a hundred characters, and a temporary directory
    /// on macOS already spends half of it. A name that does not fit is replaced by a short digest of itself, so both
    /// ends still agree on the address and a node is never refused for the length of its own name.
    /// </summary>
    private const int MaxSocketPath = 104;


    public static string SocketPath(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (Path.IsPathRooted(name))
        {
            return name;
        }

        string path = Path.Combine(Path.GetTempPath(), $"bytex-{name}.sock");
        if (path.Length <= MaxSocketPath)
        {
            return path;
        }

        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name))).ToLowerInvariant()[..16];
        string shortened = Path.Combine(Path.GetTempPath(), $"bytex-{digest}.sock");
        return shortened.Length <= MaxSocketPath
            ? shortened
            : throw new InvalidOperationException(
                $"A control channel needs a socket path of at most {MaxSocketPath} characters and the temporary directory alone " +
                $"leaves no room ({Path.GetTempPath()}). Set TMPDIR to a shorter directory, or pass an absolute path as the channel name.");
    }

    public static async Task<Stream> AcceptAsync(string name, CancellationToken ct)
    {
        if (UseNamedPipes)
        {
            // With the default buffer size of zero a write waits for the other side to read: a host that sends a command
            // before it has read the node's hello and the node that is still writing that hello would wait for each other.
            NamedPipeServerStream pipe = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, PipeBufferSize, PipeBufferSize);
            await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            return pipe;
        }

        string path = SocketPath(name);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(1);
        try
        {
            Socket client = await listener.AcceptAsync(ct).ConfigureAwait(false);
            return new NetworkStream(client, ownsSocket: true);
        }
        finally
        {
            listener.Dispose();
        }
    }

    /// <summary>How often a client looks again for a node's socket while it waits for the node to come up.</summary>
    private static readonly TimeSpan ConnectRetryInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Connects to a node's channel, waiting up to <paramref name="timeout"/> for the node to be there. A host that
    /// starts a node and connects to it is the normal case, and for that the timeout has to be a window rather than
    /// one attempt: a named pipe waits by itself, and a Unix socket that does not exist yet - or is not being
    /// listened on yet - refuses at once, so it is asked again until the window closes.
    /// </summary>
    public static async Task<Stream> ConnectAsync(string name, TimeSpan timeout, CancellationToken ct)
    {
        if (UseNamedPipes)
        {
            NamedPipeClientStream pipe = new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync((int)timeout.TotalMilliseconds, ct).ConfigureAwait(false);
            return pipe;
        }

        string path = SocketPath(name);
        UnixDomainSocketEndPoint endpoint = new(path);
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(deadline - DateTimeOffset.UtcNow);
                await socket.ConnectAsync(endpoint, cts.Token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception e) when (e is SocketException or FileNotFoundException || (e is OperationCanceledException && !ct.IsCancellationRequested))
            {
                socket.Dispose();
                if (DateTimeOffset.UtcNow + ConnectRetryInterval >= deadline)
                {
                    throw new TimeoutException($"No node was listening on the control channel '{name}' ({path}) within {timeout}.", e);
                }

                await Task.Delay(ConnectRetryInterval, ct).ConfigureAwait(false);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }
}

/// <summary>
/// Serves the control channel for one <see cref="TradingNode"/>: relays events and heartbeats out, executes commands in.
/// </summary>
public sealed class NodeControlServer : IAsyncDisposable
{
    /// <summary>
    /// How many messages may wait for a slow reader. A monitor that stops reading must not hold up the node, so the
    /// channel drops the oldest once this many are queued.
    /// </summary>
    private const int OutboxCapacity = 10_000;

    private readonly string _name;
    private readonly TradingNode _node;
    private readonly TimeSpan _heartbeat;
    private readonly ILogger _log;
    private readonly Channel<ControlMessage> _outbox = Channel.CreateBounded<ControlMessage>(new BoundedChannelOptions(OutboxCapacity) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public NodeControlServer(string name, TradingNode node, TimeSpan? heartbeat = null, ILoggerFactory? loggerFactory = null)
    {
        _name = name;
        _node = node ?? throw new ArgumentNullException(nameof(node));
        _heartbeat = heartbeat ?? TimeSpan.FromSeconds(5);
        _log = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<NodeControlServer>();
    }

    /// <summary>Raised on the pipe thread when the host asks the node to stop.</summary>
    public event Action<bool, bool>? StopRequested;

    public void Start()
    {
        // Resolve the address now: a channel that cannot be served should say so to whoever started the node, not fail
        // later inside the background loop where only a dispose would show it.
        if (!ControlTransport.UseNamedPipes)
        {
            _log.LogInformation("Control channel {Name} listening on {Path}", _name, ControlTransport.SocketPath(_name));
        }

        _node.Kernel.MessageBus.Subscribe(Topics.AllOrderEvents, m => Relay("order", m));
        _node.Kernel.MessageBus.Subscribe(Topics.AllPositionEvents, m => Relay("position", m));
        _node.Kernel.MessageBus.Subscribe(Topics.AllAccountEvents, m => Relay("account", m));
        _node.Kernel.MessageBus.Subscribe("data.custom.StrategyEvent*", m => Relay("decision", m));
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    private void Relay(string kind, object message)
    {
        object payload = message switch
        {
            OrderFilled f => new { kind = f.GetType().Name, f.ClientOrderId, f.VenueOrderId, f.InstrumentId, f.StrategyId, f.OrderSide, f.LastQty, f.LastPx, f.Commission, f.LiquiditySide, f.TsEvent },
            OrderEvent o => new { kind = o.GetType().Name, o.ClientOrderId, o.VenueOrderId, o.InstrumentId, o.StrategyId, o.TsEvent },
            PositionEvent p => new { kind = p.GetType().Name, p.PositionId, p.InstrumentId, p.StrategyId, p.Side, p.Quantity, p.AvgPxOpen, p.RealizedPnl, p.UnrealizedPnl, p.TsEvent },
            AccountState a => new { kind = "AccountState", a.AccountId, a.Balances, a.TsEvent },
            CustomData c => (object)c,
            _ => new { kind = message.GetType().Name },
        };
        _outbox.Writer.TryWrite(ControlMessage.Of(ControlProtocol.Event, new { channel = kind, data = payload }, _node.Kernel.Clock.Timestamp.Value));
    }

    private object StatusPayload() => new
    {
        traderId = _node.TraderId.Value,
        environment = _node.Kernel.Environment.ToString(),
        running = _node.IsRunning,
        tradingState = _node.TradingState.ToString(),
        halted = _node.IsHalted,
        limits = LimitsPayload(),
        reconciliation = ReconciliationPayload(),
        prices = PricesPayload(),
        queue = _node.Loop.Pending,
        processed = _node.Loop.Processed,
        orders = _node.Kernel.Cache.OrdersOpenCount(),
        positions = _node.Kernel.Cache.PositionsOpenCount(),
        strategies = _node.Kernel.Trader.Strategies.Select(s => new { id = s.StrategyId.Value, state = s.State.ToString() }).ToList(),
    };

    /// <summary>
    /// Everything a monitor needs to draw a node: built on the kernel thread, because it reads the cache and the strategies.
    /// Numbers that are prices, quantities or money travel as invariant text; times are nanoseconds since the Unix epoch.
    /// </summary>
    private object ViewPayload()
    {
        Bytex.Core.Caching.Cache cache = _node.Kernel.Cache;
        return new
        {
            traderId = _node.TraderId.Value,
            environment = _node.Kernel.Environment.ToString(),
            running = _node.IsRunning,
            tradingState = _node.TradingState.ToString(),
            halted = _node.IsHalted,
            limits = LimitsPayload(),
            venues = MatchingPayload(),
            accounts = cache.Accounts().Select(a => new
            {
                accountId = a.Id.Value,
                balances = a.Balances.Values.Select(b => new { currency = b.Currency.Code, total = Text(b.Total.Amount), free = Text(b.Free.Amount), locked = Text(b.Locked.Amount) }).ToList(),
            }).ToList(),
            strategies = _node.Kernel.Trader.Strategies.Select(s =>
            {
                IReadOnlyList<Position> positions = cache.PositionsOpen(strategyId: s.StrategyId);
                IReadOnlyList<Order> orders = cache.OrdersOpen(strategyId: s.StrategyId);
                object? document = (s as IStrategyMonitorView)?.MonitorView();
                List<InstrumentId> instruments = positions.Select(p => p.InstrumentId).Concat(orders.Select(o => o.InstrumentId)).ToList();
                if (document is not null && document.GetType().GetProperty("InstrumentId")?.GetValue(document) is string own && InstrumentId.TryParse(own, out InstrumentId ownId))
                {
                    instruments.Insert(0, ownId);
                }

                return new
                {
                    id = s.StrategyId.Value,
                    state = s.State.ToString(),
                    document,
                    instruments = instruments.Distinct().Select(id => new { instrumentId = id.ToString(), lastPrice = LastPrice(cache, id) }).ToList(),
                    positions = positions.Select(p => new
                    {
                        positionId = p.Id.Value,
                        instrumentId = p.InstrumentId.ToString(),
                        side = p.Side.ToString(),
                        quantity = Text(p.Quantity.Value),
                        avgPxOpen = Text(p.AvgPxOpen),
                        unrealizedPnl = Reference(cache, p.InstrumentId) is { } px ? Text(p.UnrealizedPnl(px).Amount) : null,
                        realizedPnl = Text(p.RealizedPnl.Amount),
                        currency = p.SettlementCurrency.Code,
                        tsOpened = p.TsOpened.Value,
                    }).ToList(),
                    orders = orders.Select(o => new
                    {
                        clientOrderId = o.ClientOrderId.Value,
                        venueOrderId = o.VenueOrderId?.Value,
                        instrumentId = o.InstrumentId.ToString(),
                        side = o.Side.ToString(),
                        type = o.Type.ToString(),
                        status = o.Status.ToString(),
                        quantity = Text(o.Quantity.Value),
                        filledQuantity = Text(o.FilledQuantity.Value),
                        price = o.Price is { } limit ? Text(limit.Value) : null,
                        triggerPrice = o.TriggerPrice is { } trigger ? Text(trigger.Value) : null,
                        reduceOnly = o.IsReduceOnly,
                        sizeAhead = cache.OwnOrders.SizeAhead(o.ClientOrderId) is { } ahead ? Text(ahead) : null,
                        queuePosition = cache.OwnOrders.QueuePosition(o.ClientOrderId),
                        tags = o.Tags,
                    }).ToList(),
                };
            }).ToList(),
        };
    }

    /// <summary>
    /// What each venue that matches its own orders is matching them against. A paper fill answers "would this have
    /// filled, and at what", and the answer is worth very different amounts measured against a book, a quote or a
    /// bar - so a host showing a paper fill can say which it was. Null rather than an empty list when no venue here
    /// matches anything itself, which is every live node: a host can then tell "no such venue" from "a venue that
    /// has been sent nothing yet".
    /// </summary>
    private object? MatchingPayload()
    {
        List<object> venues = new();
        foreach (IExecutionClient client in _node.ExecutionClients)
        {
            if (client is not IMatchingReport report)
            {
                continue;
            }

            venues.Add(new
            {
                venue = client.Venue.Value,
                instruments = report.Matching().Select(m => new
                {
                    instrumentId = m.InstrumentId.ToString(),
                    against = m.Against,
                    wantsBook = m.WantsBook,
                    bookRequested = m.BookRequested,
                }).ToList(),
            });
        }

        return venues.Count == 0 ? null : venues;
    }

    /// <summary>
    /// The price of every instrument the node was asked to show, for a host that draws a node from its heartbeat
    /// alone. A node showing nothing sends nothing: this is null rather than an empty list, so a monitor can tell
    /// "not asked for" from "asked for and not arrived yet".
    /// </summary>
    private object? PricesPayload()
    {
        if (_node.DisplayPriceInstruments.Count == 0)
        {
            return null;
        }

        Bytex.Core.Caching.Cache cache = _node.Kernel.Cache;
        return _node.DisplayPriceInstruments
            .Select(id => new { instrumentId = id.ToString(), lastPrice = LastPrice(cache, id) })
            .ToList();
    }

    /// <summary>
    /// What the node is enforcing, as a host would write it back: the limits travel as the text they are set with, so
    /// what a monitor shows and what it may send are the same thing.
    /// </summary>
    /// <summary>
    /// What the node has made of its venues' own account of things: how many times it has checked, how many
    /// differences it took their word on, and when it last looked. A host watching a node watches the differences.
    /// </summary>
    private object ReconciliationPayload()
    {
        Core.Engines.ExecutionEngine execution = _node.Kernel.ExecutionEngine;
        return new
        {
            count = execution.ReconciliationCount,
            differences = execution.ReconciledDifferences,
            lastTs = execution.LastReconciliation?.Value,
            interval = _node.Config.ReconciliationInterval.ToString(),
        };
    }

    private object LimitsPayload()
    {
        RiskLimits limits = _node.Kernel.RiskEngine.Limits;
        return new
        {
            maxLossPerPeriod = limits.MaxLossPerPeriod?.ToString(),
            lossPeriod = limits.LossPeriod.ToString(),

            // As the configuration writes it, so a host can send back what it reads.
            onLossLimit = JsonNamingPolicy.CamelCase.ConvertName(limits.OnLossLimit.ToString()),
            maxExposure = limits.MaxExposure?.ToString(),
            maxOpenPositionsPerInstrument = limits.MaxOpenPositionsPerInstrument,
            maxOpenPositions = limits.MaxOpenPositions,
            maxWorkingOrdersPerInstrument = limits.MaxWorkingOrdersPerInstrument,
            maxWorkingOrders = limits.MaxWorkingOrders,
        };
    }

    /// <summary>The newest of the last trade and the last quote the node holds; null when it has neither (a bars-only node).</summary>
    private static object? LastPrice(Bytex.Core.Caching.Cache cache, InstrumentId id)
    {
        TradeTick? trade = cache.TradeTick(id);
        QuoteTick? quote = cache.QuoteTick(id);
        if (trade is { } t && (quote is not { } newer || newer.TsEvent <= t.TsEvent))
        {
            return new { price = Text(t.Price.Value), kind = "trade", ts = t.TsEvent.Value, bid = (string?)null, ask = (string?)null };
        }

        return quote is { } q
            ? new { price = Text((q.Bid.Value + q.Ask.Value) / 2m), kind = "quote", ts = q.TsEvent.Value, bid = (string?)Text(q.Bid.Value), ask = (string?)Text(q.Ask.Value) }
            : null;
    }

    private static Price? Reference(Bytex.Core.Caching.Cache cache, InstrumentId id) =>
        cache.Price(id, PriceType.Last) ?? cache.Price(id, PriceType.Mid);

    private static string Text(decimal value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Stream? stream = null;
            try
            {
                stream = await ControlTransport.AcceptAsync(_name, ct).ConfigureAwait(false);
                _log.LogInformation("Control channel {Name} connected", _name);
                await ServeAsync(stream, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
            {
                _log.LogWarning("Control channel {Name} dropped: {Reason}", _name, e.Message);
            }
            finally
            {
                if (stream is not null)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private async Task ServeAsync(Stream stream, CancellationToken ct)
    {
        using CancellationTokenSource session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        StreamWriter writer = new(stream, new UTF8Encoding(false)) { AutoFlush = true };
        StreamReader reader = new(stream, Encoding.UTF8);
        await writer.WriteLineAsync(ControlMessage.Of(ControlProtocol.Hello, new { version = ControlProtocol.Version, pid = System.Environment.ProcessId, traderId = _node.TraderId.Value, environment = _node.Kernel.Environment.ToString() }, Now()).ToLine()).ConfigureAwait(false);

        Task writerTask = Task.Run(async () =>
        {
            using PeriodicTimer timer = new(_heartbeat);
            Task<bool> tick = timer.WaitForNextTickAsync(session.Token).AsTask();
            while (!session.IsCancellationRequested)
            {
                Task<bool> next = _outbox.Reader.WaitToReadAsync(session.Token).AsTask();
                Task done = await Task.WhenAny(next, tick).ConfigureAwait(false);
                if (done == tick)
                {
                    if (!await tick.ConfigureAwait(false))
                    {
                        break;
                    }

                    SendFromLoop(ControlProtocol.Heartbeat, StatusPayload);
                    tick = timer.WaitForNextTickAsync(session.Token).AsTask();
                }
                else if (await next.ConfigureAwait(false))
                {
                    while (_outbox.Reader.TryRead(out ControlMessage? message))
                    {
                        await writer.WriteLineAsync(message.ToLine()).ConfigureAwait(false);
                    }
                }
            }
        }, session.Token);

        try
        {
            while (!session.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(session.Token).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                ControlMessage? command = ControlMessage.Parse(line);
                if (command is null)
                {
                    continue;
                }

                Handle(command);
            }
        }
        finally
        {
            session.Cancel();
            try
            {
                await writerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>
    /// Carries out a command from the client. Nothing here writes to the stream: every message the node sends goes
    /// through the outbox, so the one writer task is the only thing that touches the writer. Two tasks writing to a
    /// StreamWriter is an error the moment their writes overlap, which a command arriving during a heartbeat did.
    /// </summary>
    private void Handle(ControlMessage command)
    {
        switch (command.Type)
        {
            case ControlProtocol.CmdStatus:
                SendFromLoop(ControlProtocol.Status, StatusPayload);
                break;
            case ControlProtocol.CmdView:
                SendFromLoop(ControlProtocol.View, ViewPayload);
                break;
            case ControlProtocol.CmdHalt:
                {
                    bool cancel = Flag(command.Payload, "cancelOrders");
                    bool close = Flag(command.Payload, "closePositions");
                    _log.LogWarning("Control channel: halt requested (cancel={Cancel}, close={Close})", cancel, close);
                    _node.Halt(cancel, close, "asked over the control channel");

                    // The state the host asked for, reported once it is in force: built behind the halt itself, so
                    // the status a host reads back is never the one from before its own command.
                    SendFromLoop(ControlProtocol.Status, StatusPayload);
                    break;
                }

            case ControlProtocol.CmdResume:
                _log.LogWarning("Control channel: release requested");
                _node.Resume();
                SendFromLoop(ControlProtocol.Status, StatusPayload);
                break;
            case ControlProtocol.CmdLimits:
                {
                    if (command.Payload is not { } payload || payload.ValueKind != JsonValueKind.Object)
                    {
                        _log.LogWarning("Control channel: limits without a payload; nothing changed");
                        break;
                    }

                    RiskLimits? limits;
                    try
                    {
                        limits = JsonSerializer.Deserialize<RiskLimits>(payload.GetRawText(), BytexJson.Options);
                    }
                    catch (JsonException e)
                    {
                        // A host that sent nonsense is told nothing changed rather than left to guess from a status.
                        _log.LogError(e, "Control channel: the limits could not be read; nothing changed");
                        _outbox.Writer.TryWrite(ControlMessage.Of(ControlProtocol.Status, StatusPayload(), Now()));
                        break;
                    }

                    if (limits is null)
                    {
                        break;
                    }

                    _log.LogWarning("Control channel: limits set");
                    _node.SetLimits(limits);
                    SendFromLoop(ControlProtocol.Status, StatusPayload);
                    break;
                }

            case ControlProtocol.CmdCancelAll:
                _node.Loop.Post(() =>
                {
                    foreach (Strategy strategy in _node.Kernel.Trader.Strategies)
                    {
                        ShutdownHelper.Flatten(strategy, _node.Kernel, cancelOrders: true, closePositions: false);
                    }
                });
                _log.LogWarning("Control channel: cancel-all requested");
                break;
            case ControlProtocol.CmdFlatten:
                _node.Loop.Post(() =>
                {
                    foreach (Strategy strategy in _node.Kernel.Trader.Strategies)
                    {
                        ShutdownHelper.Flatten(strategy, _node.Kernel, cancelOrders: true, closePositions: true);
                    }
                });
                _log.LogWarning("Control channel: flatten requested");
                break;
            case ControlProtocol.CmdStop:
                {
                    bool cancel = Flag(command.Payload, "cancelOrders");
                    bool close = Flag(command.Payload, "closePositions");
                    _log.LogWarning("Control channel: stop requested (cancel={Cancel}, close={Close})", cancel, close);
                    _outbox.Writer.TryWrite(ControlMessage.Of(ControlProtocol.Bye, new { reason = "stop requested" }, Now()));
                    StopRequested?.Invoke(cancel, close);
                    break;
                }

            default:
                _log.LogWarning("Control channel: unknown command {Type}", command.Type);
                break;
        }
    }

    /// <summary>
    /// Builds a payload on the kernel thread and sends it through the outbox. Everything the node reports reads the
    /// cache and the strategies, and those belong to that thread: a status built on the channel's own thread reads
    /// what the kernel is writing, which at best reports a moment that never existed - a quote that had arrived but
    /// was not applied yet - and at worst throws while a collection is being changed. A node whose loop has stopped
    /// reports nothing, which tells a host more than a number read from under it would.
    /// </summary>
    private void SendFromLoop(string type, Func<object> payload)
    {
        try
        {
            _node.Loop.Post(() =>
            {
                object body;
                try
                {
                    body = payload();
                }
#pragma warning disable CA1031 // A monitor's question must never take the node's loop down, and it must always get an answer.
                catch (Exception e)
#pragma warning restore CA1031
                {
                    _log.LogError(e, "Control channel: the {Type} could not be built", type);
                    body = new { error = e.Message };
                }

                _outbox.Writer.TryWrite(ControlMessage.Of(type, body, Now()));
            });
        }
        catch (ChannelClosedException)
        {
            _log.LogDebug("Control channel: the node's loop has stopped, so no {Type} was built", type);
        }
    }

    private static bool Flag(JsonElement? payload, string name) =>
        payload is { } p && p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    private long Now() => _node.Kernel.Clock.Timestamp.Value;

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cts.Dispose();
    }
}

/// <summary>Connects to a node's control channel from a host process.</summary>
public sealed class NodeControlClient : IAsyncDisposable
{
    /// <summary>How long a client waits for the node's channel when it names no timeout.</summary>
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(10);

    private readonly Stream _stream;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;

    private NodeControlClient(Stream stream)
    {
        _stream = stream;
        _reader = new StreamReader(stream, Encoding.UTF8);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
    }

    public static async Task<NodeControlClient> ConnectAsync(string name, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        Stream stream = await ControlTransport.ConnectAsync(name, timeout ?? DefaultConnectTimeout, ct).ConfigureAwait(false);
        return new NodeControlClient(stream);
    }

    public async IAsyncEnumerable<ControlMessage> ReadAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            string? line = await _reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
            {
                yield break;
            }

            ControlMessage? message = ControlMessage.Parse(line);
            if (message is not null)
            {
                yield return message;
            }
        }
    }

    public Task SendAsync(string command, object? payload = null) =>
        _writer.WriteLineAsync(ControlMessage.Of(command, payload, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L).ToLine());

    public Task StopAsync(bool cancelOrders = true, bool closePositions = false) => SendAsync(ControlProtocol.CmdStop, new { cancelOrders, closePositions });

    public Task CancelAllAsync() => SendAsync(ControlProtocol.CmdCancelAll);

    public Task FlattenAsync() => SendAsync(ControlProtocol.CmdFlatten);

    public Task RequestStatusAsync() => SendAsync(ControlProtocol.CmdStatus);

    public Task RequestViewAsync() => SendAsync(ControlProtocol.CmdView);

    /// <summary>
    /// Halts the node's trading and leaves it running: it places nothing until it is released. What is resting is
    /// left alone unless <paramref name="cancelOrders"/> or <paramref name="closePositions"/> asks otherwise, and
    /// the node answers with a status once the halt is in force.
    /// </summary>
    public Task HaltAsync(bool cancelOrders = false, bool closePositions = false) =>
        SendAsync(ControlProtocol.CmdHalt, new { cancelOrders, closePositions });

    /// <summary>Lets a halted node trade again.</summary>
    public Task ResumeAsync() => SendAsync(ControlProtocol.CmdResume);

    /// <summary>Replaces what the node enforces. The node answers with a status carrying the limits it now holds.</summary>
    public Task SetLimitsAsync(RiskLimits limits) => SendAsync(ControlProtocol.CmdLimits, limits);

    public async ValueTask DisposeAsync()
    {
        _writer.Dispose();
        _reader.Dispose();
        await _stream.DisposeAsync().ConfigureAwait(false);
    }
}
