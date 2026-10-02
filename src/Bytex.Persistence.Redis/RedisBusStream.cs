using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Bytex.Core.Messaging;
using Bytex.Core.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bytex.Persistence.Redis;

/// <summary>
/// What a message looks like once it has left this process.
///
/// <para>
/// <b>Unstable until 1.0, deliberately and in writing.</b> Anything reading these streams pins this shape, and
/// promising it before the engine's own types are frozen would be promising on their behalf. It is said here so that
/// somebody building against it does so knowing.
/// </para>
///
/// <para>
/// The payload's type is carried beside it because a reader cannot infer one from a topic: several kinds of message
/// share a topic, and a consumer that guessed from the topic would decode the wrong shape on the day a second kind
/// appeared.
/// </para>
/// </summary>
public sealed record BusRecord(string Topic, string PayloadType, string Encoding, byte[] Payload);

public sealed record RedisBusStreamConfig
{
    public string ConnectionString { get; init; } = "localhost:6379";

    public required string ModuleHostId { get; init; }

    public string KeyPrefix { get; init; } = "bytex";

    /// <summary>
    /// Which bus topics leave the process. Nothing by default: publishing everything a busy node says would be a
    /// decision made for somebody rather than by them.
    /// </summary>
    public IReadOnlyList<string> Topics { get; init; } = [];

    /// <summary>
    /// Roughly how many entries a stream keeps. Redis trims approximately, which is the cheap form, so a stream may
    /// hold somewhat more than this and never unboundedly more.
    /// </summary>
    public int MaxLength { get; init; } = 100_000;

    /// <summary>
    /// How many messages may wait to be written before the oldest are dropped.
    ///
    /// <para>
    /// Dropping is the deliberate answer. The alternative is back-pressure, which means a slow or unreachable Redis
    /// stalls the thread that is trading - so a dashboard falling behind would slow down the orders it is watching.
    /// What is dropped is counted and logged, because silently losing messages while appearing to publish them is
    /// the one behaviour worse than dropping them.
    /// </para>
    /// </summary>
    public int QueueCapacity { get; init; } = 100_000;
}

/// <summary>
/// Publishes bus messages to Redis streams, for a dashboard or another process to read (R1.11).
///
/// <para>
/// One stream per topic, so a reader subscribes to what it wants rather than filtering everything. Writing happens
/// on a thread of its own: a node must not stall because a disk or a socket is slow, which is the rule its journal
/// already follows and the reason both of them drop rather than wait.
/// </para>
///
/// <para>
/// <b>What this does not do.</b> It does not read. Entries already in a stream when a reader starts are that
/// reader's business, and replaying them is not something a publisher can decide on its behalf.
/// </para>
/// </summary>
public sealed class RedisBusStream : IAsyncDisposable
{
    /// <summary>The only encoding written today, named in every record so a reader never has to assume it.</summary>
    public const string JsonEncoding = "json";

    private readonly RedisBusStreamConfig _config;
    private readonly IRedisStore _store;
    private readonly IMessageBus _bus;
    private readonly ILogger _log;
    private readonly string _prefix;
    private readonly BlockingCollection<(string Stream, BusRecord Record)> _queue;
    private readonly List<(string Topic, Action<object> Handler)> _subscriptions = new();
    private readonly Task _writer;
    private long _dropped;
    private long _published;

    public RedisBusStream(RedisBusStreamConfig config, IMessageBus bus, ILoggerFactory? loggerFactory = null)
        : this(config, bus, new RedisStore(StackExchange.Redis.ConnectionMultiplexer.Connect(config.ConnectionString).GetDatabase()), loggerFactory)
    {
    }

    internal RedisBusStream(RedisBusStreamConfig config, IMessageBus bus, IRedisStore store, ILoggerFactory? loggerFactory = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _log = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<RedisBusStream>();
        _prefix = $"{config.KeyPrefix}:{config.ModuleHostId}:stream";
        _queue = new BlockingCollection<(string, BusRecord)>(new ConcurrentQueue<(string, BusRecord)>(), config.QueueCapacity);

        foreach (string topic in config.Topics)
        {
            string stream = $"{_prefix}:{topic}";
            void Handler(object message) => Enqueue(stream, topic, message);

            _bus.Subscribe(topic, Handler);
            _subscriptions.Add((topic, Handler));
        }

        _writer = Task.Run(WriteLoop);
    }

    /// <summary>How many messages reached Redis.</summary>
    public long Published => Interlocked.Read(ref _published);

    /// <summary>How many were dropped because the queue was full, so that "keeping up" is a number rather than a hope.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    private void Enqueue(string stream, string topic, object message)
    {
        BusRecord record;
        try
        {
            record = new BusRecord(
                topic,
                message.GetType().FullName ?? message.GetType().Name,
                JsonEncoding,
                JsonSerializer.SerializeToUtf8Bytes(message, message.GetType(), BytexJson.Options));
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            // A message that cannot be written is one message, not a reason to stop publishing the rest.
            _log.LogError(e, "A {Type} on {Topic} could not be serialised and was not published", message.GetType().Name, topic);
            return;
        }

        while (!_queue.TryAdd((stream, record)))
        {
            if (!_queue.TryTake(out _))
            {
                return;
            }

            Interlocked.Increment(ref _dropped);
        }
    }

    private void WriteLoop()
    {
        foreach ((string stream, BusRecord record) in _queue.GetConsumingEnumerable())
        {
            try
            {
                _store.StreamAdd(
                    stream,
                    new Dictionary<string, byte[]>(StringComparer.Ordinal)
                    {
                        ["topic"] = Encoding.UTF8.GetBytes(record.Topic),
                        ["type"] = Encoding.UTF8.GetBytes(record.PayloadType),
                        ["encoding"] = Encoding.UTF8.GetBytes(record.Encoding),
                        ["payload"] = record.Payload,
                    },
                    _config.MaxLength);

                Interlocked.Increment(ref _published);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Redis being unreachable must not take the node with it: what is lost is visibility, and the node
                // goes on trading.
                Interlocked.Increment(ref _dropped);
                _log.LogError(e, "A message on {Topic} could not be published", record.Topic);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach ((string topic, Action<object> handler) in _subscriptions)
        {
            _bus.Unsubscribe(topic, handler);
        }

        _subscriptions.Clear();
        _queue.CompleteAdding();

        try
        {
            await _writer.ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogError(e, "The stream writer did not finish cleanly");
        }

        if (_dropped > 0)
        {
            _log.LogWarning("{Dropped} message(s) were dropped rather than slowing the node down", _dropped);
        }

        _queue.Dispose();
    }
}
